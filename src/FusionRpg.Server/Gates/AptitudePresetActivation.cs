using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server.Gates;

/// <summary>
/// Where in the activation pipeline a refusal was produced. The route's own status codes are a
/// function of this stage -- a budget or scope-map refusal is 400, a materialize, budget-check or
/// pricing refusal is 409, and the store's own reasons keep the store's codes -- so the service
/// reports the stage and the endpoint keeps the HTTP mapping, exactly as
/// <see cref="PatronService"/> leaves the mapping to <c>PatronEndpoints</c>.
/// </summary>
public enum ActivationStage
{
    /// <summary>The call succeeded.</summary>
    None = 0,
    /// <summary><c>ResolveBudget</c> refused (unknown scope, missing/foreign scope key, unknown species).</summary>
    Budget,
    /// <summary><c>AptitudePresetMaterialize.Materialize</c> refused (shape, clamps, overspend).</summary>
    Materialize,
    /// <summary>Turning shares into an allocation refused.</summary>
    Allocation,
    /// <summary>The scope's own point budget check refused.</summary>
    BudgetCheck,
    /// <summary>The store's transaction refused (missing/foreign preset, souls, payment choice, ownership).</summary>
    Store
}

/// <summary>
/// build-preset BP1.4 (spec-gate-services.md): the whole of <c>POST /api/aptitude-presets/activate</c>
/// after its request-shape validation -- budget resolve, materialize, the budget re-check, the one
/// transactional store call and the scoped broadcast -- lifted out of
/// <c>AptitudePresetEndpoints.cs</c>'s lambda so a build-preset applier calls exactly what the
/// player's own click calls. Behaviour is byte-identical to the route it replaces: reason strings,
/// numbers and writes are unchanged, and the endpoint keeps the status-code mapping.
///
/// <para><b>Pricing is not this module's change.</b> The commander and unique branches are priced by
/// <c>RpgStore.TryActivateAptitudePreset</c> through <c>TryReallocateUnlocked</c>, and the species
/// branch takes the player's <c>payWith</c> through <c>TryRespecSpeciesUnlocked</c>; this service
/// only threads the choice from the request to that same store call.</para>
/// </summary>
public sealed class AptitudePresetActivation
{
    readonly RpgStore _store;
    readonly IPowerIndexProvider _powerIndex;
    readonly IHubContext<RpgHub> _hub;

    public AptitudePresetActivation(RpgStore store, IPowerIndexProvider powerIndex, IHubContext<RpgHub> hub)
    {
        _store = store;
        _powerIndex = powerIndex;
        _hub = hub;
    }

    /// <summary>Budget -> materialize -> budget check -> <c>TryActivateAptitudePreset</c> -> scoped
    /// broadcast, in the route's own order. Every branch charges inside the store transaction, where
    /// its by-hand route charges: species through <c>TryRespecSpeciesUnlocked</c> (with the player's
    /// <paramref name="payWith"/>), commander and unique through <c>TryReallocateUnlocked</c>.</summary>
    public ActivationResult Activate(
        long playerId, string presetId, string scope, string scopeKey,
        string? correlationId, RespecPayment? payWith)
    {
        var gate = Run(playerId, presetId, scope, scopeKey);
        if (!gate.Ok) return ActivationResult.Refused(gate);

        var outcome = _store.TryActivateAptitudePreset(
            playerId, presetId, gate.Scope, gate.ScopeKey, gate.Allocation!, gate.Shares, gate.Leftover,
            correlationId, payWith: payWith);
        if (!outcome.Ok)
            return new ActivationResult(
                false, ActivationStage.Store, outcome.Reason, gate.Budget, gate.Shares, gate.Leftover,
                outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance?.Balance, false)
            {
                FreeStock = outcome.FreeStock
            };

        _ = BroadcastScoped(_hub, playerId, gate.Scope, gate.ScopeKey);
        return new ActivationResult(
            true, ActivationStage.None, outcome.Reason, gate.Budget, outcome.Shares, outcome.Leftover,
            outcome.Priced, outcome.PriceAmount, outcome.RespecCount, outcome.Balance?.Balance,
            outcome.Reason == "replay")
        {
            FreeStock = outcome.FreeStock
        };
    }

    /// <summary>
    /// The read half, exposed without the write: the same budget resolve, materialize and budget
    /// check <see cref="Activate"/> performs before it writes, plus the respec quote for the scope --
    /// for <c>species</c> the soul price AND the empire's free respec stock (the player chooses); for
    /// <c>commander</c>/<c>unique</c> whether it is a respec and its soul price, never a free option.
    /// The quote is the same read <see cref="Activate"/>'s write path makes
    /// (<c>QuoteSpeciesRespec</c>, <c>QuoteReallocation</c>), so a preview and a spend cannot
    /// disagree. Nothing here writes, spends or reserves.
    /// </summary>
    public ActivationPreview Preview(long playerId, string presetId, string scope, string scopeKey)
    {
        var gate = Run(playerId, presetId, scope, scopeKey);
        if (!gate.Ok) return ActivationPreview.Refused(gate);

        switch (gate.Scope)
        {
            case "species":
            {
                // `respec-free-counter` EP4.9/EP4.10 -- the species override is free on a first
                // override or a revert, and priced otherwise; exactly the test TryRespecSpeciesUnlocked
                // makes. `QuoteSpeciesRespec` is that spend's own read-side twin, so the price cannot
                // drift from it.
                var isRevert = gate.Allocation!.TotalForScope(AllocationScope.CreatureType) == 0;
                var everTouched = _store.HasEverRespecced(playerId, gate.ScopeKey);
                var free = isRevert || !everTouched;
                var quote = _store.QuoteSpeciesRespec(playerId, gate.ScopeKey);
                return ActivationPreview.Priced(gate, free ? 0 : quote.Souls.Amount, quote.FreeStock, !free);
            }
            case "commander":
            case "unique":
            {
                // R18 and its correction: a commander or unique target pays souls on a take-back and
                // never draws the empire's free respec stock, which is why QuoteReallocation passes
                // freeStock: 0 for every quote. Priced against the KEY the write prices against --
                // for commander that is the player's own pool, never the (empty) request scopeKey.
                var payer = new EmpireRef(new SaveId(playerId), _store.HumanEmpireOf(playerId));
                var quote = _store.QuoteReallocation(
                    payer, gate.AllocationScope!.Value, ReallocationKey(playerId, gate.Scope, gate.ScopeKey),
                    gate.Allocation!, out var isRespec);
                return ActivationPreview.Priced(gate, isRespec ? quote.Souls.Amount : 0, 0, isRespec);
            }
            default:
                // Unreachable: ResolveBudget refuses any scope outside the three the map knows, so
                // Run never returns a gate whose Scope is anything else.
                throw new ArgumentOutOfRangeException(nameof(scope), gate.Scope, "unknown activation scope");
        }
    }

    /// <summary>The budget/materialize/allocation/budget-check half both <see cref="Activate"/> and
    /// <see cref="Preview"/> share -- one implementation, so the read and the write cannot disagree
    /// about the allocation either.</summary>
    GateResult Run(long playerId, string presetId, string scope, string scopeKey)
    {
        var preset = _store.GetAptitudePreset(presetId);
        if (preset is null) return GateResult.Refused(ActivationStage.Store, "presets.notFound");
        if (preset.PlayerId != playerId) return GateResult.Refused(ActivationStage.Store, "presets.owner.mismatch");

        var budgetResolve = ResolveBudget(_store, _powerIndex, playerId, scope, scopeKey);
        if (!budgetResolve.Ok)
            return GateResult.Refused(ActivationStage.Budget, budgetResolve.Reason);

        var mat = AptitudePresetMaterialize.Materialize(
            RpgStore.ToRowSpecs(_store.GetAptitudePresetEntries(preset.PresetId)), budgetResolve.Budget);
        if (!mat.Ok) return GateResult.Refused(ActivationStage.Materialize, mat.Reason);

        AptitudeAllocation allocation;
        try
        {
            allocation = ToAllocation(scope, mat.Shares);
        }
        catch (ArgumentException ex)
        {
            // Defensive: ValidateTargetPermilleSum already refused an unknown aptitude id, so this is
            // unreachable today. Kept because the route kept it, with the same reason and detail.
            return GateResult.Refused(ActivationStage.Allocation, "aptitudes.unknownid", detail: ex.Message);
        }

        var allocScope = ScopeToAllocation(scope);
        if (allocScope is null)
            return GateResult.Refused(ActivationStage.Budget, "presets.scope.unknown");

        var check = PointBudget.CheckScope(allocScope.Value, allocation, budgetResolve.Source, AptitudeTuningHub.Tuning);
        if (!check.WithinBudget)
            return GateResult.Refused(ActivationStage.BudgetCheck, "aptitudes.overbudget",
                spent: check.Spent, checkBudget: check.Budget);

        return new GateResult(
            true, ActivationStage.None, "", preset.PresetId, scope, scopeKey,
            budgetResolve.Budget, mat.Shares, mat.Leftover, allocation, allocScope);
    }

    /// <summary>The scope key the re-allocation gate prices and writes against --
    /// <c>TryReallocateUnlocked</c>'s own key, never the request's raw <c>scopeKey</c>: a commander
    /// target is the player's own pool (<c>player:{id}</c>), a unique target is its instance id.</summary>
    internal static string ReallocationKey(long playerId, string scope, string scopeKey) =>
        scope == "commander" ? $"player:{playerId}" : scopeKey;

    /// <summary>The scope map every route in <c>AptitudePresetEndpoints.cs</c> keys off -- one
    /// spelling-to-scope table, never a second string comparison that could drift from this one.</summary>
    public static AllocationScope? ScopeToAllocation(string scope) => scope switch
    {
        "commander" => AllocationScope.Commander,
        "unique" => AllocationScope.UniqueCreature,
        "species" => AllocationScope.CreatureType,
        _ => null
    };

    internal static AptitudeAllocation ToAllocation(string scope, IReadOnlyDictionary<string, long> shares)
    {
        var allocScope = ScopeToAllocation(scope)
            ?? throw new ArgumentException($"unknown scope '{scope}'");
        return shares.Aggregate(AptitudeAllocation.Empty,
            (acc, kv) => acc + AptitudeAllocation.Single(allocScope, kv.Key, kv.Value));
    }

    internal static Task BroadcastScoped(IHubContext<RpgHub> hub, long playerId, string scope, string? scopeKey) =>
        AptitudeEndpoints.BroadcastBestEffort(hub, scope switch
        {
            "unique" => new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "unique", scopeKey, null),
            "species" => new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "species", null, scopeKey),
            _ => new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "commander", null, null)
        });

    internal static (bool Ok, string Reason, long Budget, long Source) ResolveBudget(
        RpgStore store, IPowerIndexProvider powerIndex, long playerId, string scope, string scopeKey)
    {
        switch (scope)
        {
            case "commander":
            {
                var theta = (long)powerIndex.ActorIndex(new StatContext { PlayerId = playerId });
                var budget = PointBudget.PointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
                return (true, "", budget, theta);
            }
            case "unique":
            {
                if (string.IsNullOrWhiteSpace(scopeKey))
                    return (false, "presets.scopeKey.missing", 0, 0);
                var actor = store.GetUniqueActor(scopeKey);
                if (actor is null) return (false, "unique.notFound", 0, 0);
                if (actor.PlayerId != playerId) return (false, "presets.owner.mismatch", 0, 0);
                var source = PointBudget.UniqueCreatureSourceFromLevel(actor.Level);
                var budget = PointBudget.PointsFor(AllocationScope.UniqueCreature, source, AptitudeTuningHub.Tuning);
                return (true, "", budget, source);
            }
            case "species":
            {
                if (string.IsNullOrWhiteSpace(scopeKey) || !CreatureSpeciesCatalog.IsKnown(scopeKey))
                    return (false, "species.unknown", 0, 0);
                var creatureTypeId = CreatureSpeciesCatalog.Get(scopeKey).CreatureTypeId;
                var level = store.GetRpgActor(playerId, RpgActorKinds.Species, creatureTypeId)?.Level ?? 1;
                var source = PointBudget.CreatureTypeSourceFromLevel(level);
                var budget = PointBudget.PointsFor(AllocationScope.CreatureType, source, AptitudeTuningHub.Tuning);
                return (true, "", budget, source);
            }
            default:
                return (false, "presets.scope.unknown", 0, 0);
        }
    }
}

/// <summary>Outcome of the shared gate half, before the store's transaction runs.</summary>
internal sealed record GateResult(
    bool Ok,
    ActivationStage Stage,
    string Reason,
    string PresetId,
    string Scope,
    string ScopeKey,
    long Budget,
    IReadOnlyDictionary<string, long> Shares,
    long Leftover,
    AptitudeAllocation? Allocation,
    AllocationScope? AllocationScope)
{
    public string Detail { get; init; } = "";
    public long Spent { get; init; }
    public long CheckBudget { get; init; }

    public static GateResult Refused(ActivationStage stage, string reason, string detail = "",
        long spent = 0, long checkBudget = 0) =>
        new(false, stage, reason, "", "", "",
            0, new Dictionary<string, long>(StringComparer.Ordinal), 0, null, null)
        {
            Detail = detail,
            Spent = spent,
            CheckBudget = checkBudget
        };

    public static GateResult Refused(GateResult gate) =>
        new(false, gate.Stage, gate.Reason, "", "", "",
            0, gate.Shares, 0, null, null)
        {
            Detail = gate.Detail,
            Spent = gate.Spent,
            CheckBudget = gate.CheckBudget
        };
}

/// <summary>The whole outcome of an activation: what it charged, what it stored, and where it refused.</summary>
public sealed record ActivationResult(
    bool Ok,
    ActivationStage Stage,
    string Reason,
    long Budget,
    IReadOnlyDictionary<string, long> Shares,
    long Leftover,
    bool Priced,
    long PriceAmount,
    long RespecCount,
    long? SoulBalance,
    bool Replay)
{
    public string Detail { get; init; } = "";
    public long Spent { get; init; }
    public long CheckBudget { get; init; }
    /// <summary>The empire's free-respec stock after the call, or the stock a refusal quotes.</summary>
    public long FreeStock { get; init; }

    internal static ActivationResult Refused(GateResult gate) =>
        new(false, gate.Stage, gate.Reason, gate.Budget, gate.Shares, 0, false, 0, 0, null, false)
        {
            Detail = gate.Detail,
            Spent = gate.Spent,
            CheckBudget = gate.CheckBudget
        };
}

/// <summary>The read half's outcome: the allocation the preset would write, and the quote it would face.</summary>
public sealed record ActivationPreview(
    bool Ok,
    ActivationStage Stage,
    string Reason,
    long Budget,
    IReadOnlyDictionary<string, long> Shares,
    long Leftover,
    long SoulPrice,
    long FreeStock,
    bool IsRespec)
{
    public string Detail { get; init; } = "";
    public long Spent { get; init; }
    public long CheckBudget { get; init; }

    internal static ActivationPreview Priced(GateResult gate, long soulPrice, long freeStock, bool isRespec) =>
        new(true, ActivationStage.None, "", gate.Budget, gate.Shares, gate.Leftover, soulPrice, freeStock, isRespec);

    internal static ActivationPreview Refused(GateResult gate) =>
        new(false, gate.Stage, gate.Reason, gate.Budget, gate.Shares, gate.Leftover, 0, 0, false)
        {
            Detail = gate.Detail,
            Spent = gate.Spent,
            CheckBudget = gate.CheckBudget
        };
}
