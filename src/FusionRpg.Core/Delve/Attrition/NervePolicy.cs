using System.Text.Json;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>Status id for a nerve stage — `nerve.{stage}`.</summary>
public static class NerveStatusIds
{
    public static string For(string stage) => $"nerve.{stage}";

    /// <summary>Host-scoped grant id — `StatusRuntime.ClearGrant` withdraws exactly one actor's nerve
    /// status. At most one `nerve.*` instance is ever live per creature (spec §4), so one grant id per
    /// host is enough — never per-stage, unlike <c>ExhaustionStatusIds</c>'s per-resource scoping.</summary>
    public static string GrantIdFor(string hostPtr) => $"nerve:{hostPtr}";
}

/// <summary>
/// The staged `nerve` status (D2.19, spec-delve-attrition.md §4) — the `ExhaustionPolicy.Sync` shape,
/// reused for a projection rather than a threshold crossing.
///
/// <para><b>Why this is a build beside the shipped `Counter` kind.</b> `StatusInstance` carries no
/// count (`StatusRuntime.cs`), and `UpsertInstance`'s `Refresh`/`Replace`/`Coexist` are re-apply
/// policies, not a counter — the closest shipped shape (`Counter`, `bond`) still carries none. So the
/// stack counter lives in party state (`DelveMemberState.NerveStacks`) and the live status is its
/// PROJECTION: <see cref="NerveLadder.StageFor"/> turns stacks (plus spirit's own exhaustion) into a
/// stage index, and <see cref="Sync"/> makes the runtime's live instance match it — at most one
/// `nerve.*` instance per creature, never a status field.</para>
/// </summary>
public sealed class NervePolicy
{
    readonly IReadOnlyList<string> _stages;
    readonly IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> _modsByStage;

    static readonly IStatusRng ScriptedRng = new FixedStatusRng(0.0);
    static readonly string SpiritRegenChannel = DerivedStatChannels.ResourceRegen("spirit");

    /// <param name="catalog">Read, never written: the fixed `nerve.*` ids are registered by
    /// `StatusCatalogBootstrap`'s own `9.5 Nerve` block (the ADR-locked list, 21 → 24), not by this
    /// constructor. Every stage this policy will ever apply is checked against it here, so a drift
    /// between the `nerveStage` registry and the catalog fails at construction, never at the first
    /// `Sync` call mid-battle.</param>
    /// <param name="stages">The `nerveStage` registry's members, in threshold order (`unsettled`,
    /// `shaken`, `afflicted`) — a plain parameter, never re-derived here, matching
    /// <see cref="NerveLadder.StageFor"/>'s own "caller supplies the ordered list" contract and this
    /// module's established "read model owned elsewhere" shape (<c>HungerCharge.ForRoom</c>,
    /// <c>ExhaustionPolicy</c>'s own `debuffsByResourceId`).</param>
    /// <param name="modsByStage">One entry per member of <paramref name="stages"/> — from
    /// <see cref="NerveContainer.Load"/> in production, a hand-built fixture in tests. Validated here
    /// exactly like <see cref="Actions.Cost.ExhaustionPolicy"/>'s own constructor: a stage whose stat
    /// block touches spirit's own regen channel is the one true spiral (spirit is the pool nerve
    /// stacks read) and is rejected at construction rather than surfacing as a creature that can never
    /// climb back down the ladder.</param>
    public NervePolicy(
        StatusCatalog catalog,
        IReadOnlyList<string> stages,
        IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> modsByStage)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (stages is null) throw new ArgumentNullException(nameof(stages));
        if (modsByStage is null) throw new ArgumentNullException(nameof(modsByStage));

        foreach (var stage in stages)
        {
            if (!modsByStage.TryGetValue(stage, out var mods))
                throw new ArgumentException($"nerve stage '{stage}' has no stat block.", nameof(modsByStage));

            foreach (var mod in mods)
            {
                if (mod.ChannelId == SpiritRegenChannel)
                    throw new ArgumentException(
                        $"self-regen cycle: nerve stage '{stage}' must not touch spirit's own regen channel '{SpiritRegenChannel}'",
                        nameof(modsByStage));
            }

            catalog.GetRequired(NerveStatusIds.For(stage));
        }

        _stages = stages;
        _modsByStage = modsByStage;
    }

    /// <summary>
    /// Syncs the live projection to <paramref name="stage"/> (as returned by
    /// <see cref="NerveLadder.StageFor"/>; -1 = no nerve status at all). On a stage CHANGE,
    /// `ClearGrant`s the old grant and `Apply`s the new stage attacker-less with `FixedStatusRng(0)`
    /// and `BaseDuration 0` (persists until the next `Sync`, never a timed decay) — on no change,
    /// no write, the identical idempotence `ExhaustionPolicy.Sync` guarantees.
    /// </summary>
    /// <returns>True only on a call that actually applied a (fresh or replacing) stage — false for a
    /// no-op or a drop to -1.</returns>
    public bool Sync(StatusRuntime runtime, string hostPtr, int stage, DateTimeOffset now)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        if (stage < -1 || stage >= _stages.Count)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, $"stage must be -1..{_stages.Count - 1}");

        var grantId = NerveStatusIds.GrantIdFor(hostPtr);
        var wantStatusId = stage >= 0 ? NerveStatusIds.For(_stages[stage]) : null;
        var live = FindLive(runtime, hostPtr);

        if (live != null && live.StatusId == wantStatusId)
            return false; // already at this stage (including "still no nerve at all" when both are null)

        if (live != null)
            runtime.ClearGrant(grantId); // stage changed away from this id -- explicit withdraw, this status never expires on its own

        if (wantStatusId is null)
            return false; // dropped to -1 -- withdrawal only, no re-apply

        var outcome = runtime.Apply(
            new StatusApplyInput(
                StatusId: wantStatusId,
                HostPtr: hostPtr,
                AttackerPtr: null,
                GrantId: grantId,
                BaseMagnitude: 1.0, // inert -- ModifyStat reads StatMods directly, never EffectiveMagnitude
                BaseDuration: 0,    // 0 -> ExpiresAt = DateTimeOffset.MaxValue: persists until ClearGrant
                PeriodMs: 0,
                DurationMs: 0,
                AttackerLess: true,
                StatMods: _modsByStage[_stages[stage]]),
            ScriptedRng,
            now);

        return outcome.Applied;
    }

    static StatusInstance? FindLive(StatusRuntime runtime, string hostPtr)
    {
        foreach (var instance in runtime.ForHost(hostPtr))
        {
            if (instance.StatusId.StartsWith("nerve.", StringComparison.Ordinal))
                return instance;
        }
        return null;
    }
}

/// <summary>
/// The container loader for `gk-data/packs/fusion/data/seed/dungeon/_containers/nerve.v1.json` (spec-delve-attrition.md
/// §4: "each stage is a container of atoms"). A fixed, non-rolled bag of <see cref="StatusStatMod"/>s
/// per stage — deliberately never routed through the affix-roll `Instantiator`/`ContainerRow` pipeline
/// (`Effects/Atoms/ContainerRow.cs`): that machinery exists for player-rolled items with a seed, a
/// rarity and a tier window, none of which a nerve stage has. A nerve stage is shipped catalog
/// content, identical for every creature that reaches it, so its "stat" block is parsed by the SAME,
/// already-shipped <see cref="StatusStatPayload.TryParse"/> an overlay-authored status already uses
/// (`StatusEffectBridge.cs`) — one parser, not a second one for a second kind of "container".
/// </summary>
public static class NerveContainer
{
    public static IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> Load(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, IReadOnlyList<StatusStatMod>>(StringComparer.Ordinal);

        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (!entry.TryGetProperty("stage", out var stageEl) || stageEl.ValueKind != JsonValueKind.String)
                throw new FormatException("nerve container: every entry needs a string 'stage'.");
            var stage = stageEl.GetString()!;

            if (!StatusStatPayload.TryParse(
                    entry.TryGetProperty("stat", out var statEl) ? statEl : null,
                    out var mods, out var error))
                throw new FormatException($"nerve container '{stage}': {error}");

            result[stage] = mods;
        }

        return result;
    }
}
