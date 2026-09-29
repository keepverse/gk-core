using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Actions.Cost;

/// <summary>Status id for a resource's exhaustion debuff — <c>exhaustion.{resourceId}</c>.</summary>
public static class ExhaustionStatusIds
{
    public static string For(string resourceId) => $"exhaustion.{resourceId}";

    /// <summary>Host- and resource-scoped grant id, so <c>StatusRuntime.ClearGrant</c> withdraws
    /// exactly one actor's one resource's exhaustion — never every actor's, and never a sibling
    /// resource's on the same actor.</summary>
    public static string GrantIdFor(string hostPtr, string resourceId) => $"exhaustion:{hostPtr}:{resourceId}";
}

/// <summary>
/// Exhaustion as a status (spec-action-costs.md §7, T16). Every resource except <c>hp</c> debuffs
/// derived stats once its resolved value hits zero — <c>hp</c> is exempt because depletion there is
/// death, owned by the turn FSM's <c>Downed</c> state (resource-hub-ssot.md §10), never a status.
///
/// <para><b>Reuses <see cref="StatusRuntime"/></b> for storage and lifecycle, and its
/// <see cref="StatusStatMod"/> list for the debuff payload — a container of (channel, op, value)
/// atoms, never a hardcoded channel switch. Application is deterministic, not a combat resist roll:
/// a resource hitting zero is a mechanical fact, not something an actor's stats can contest, so
/// <c>Apply</c> is called <c>AttackerLess</c> with <see cref="FixedStatusRng"/>(0) — the identical
/// pattern <c>BattleEngine</c> already uses for scripted, attacker-less riders.</para>
///
/// <para><b>"Re-evaluates on read" is a pure function</b> (<see cref="IsExhausted"/>), never a cached
/// flag: whatever gameplay code needs to know "is this actor exhausted right now" calls it directly
/// against T15's freshly-resolved pool value. The <see cref="StatusRuntime"/> instance this class
/// applies/withdraws is a secondary concern — the derived-stat modifier payload and any VFX cue —
/// decoupled from that gameplay-critical check.</para>
/// </summary>
public sealed class ExhaustionPolicy
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> _debuffs;

    static readonly IStatusRng ScriptedRng = new FixedStatusRng(0.0);

    /// <param name="catalog">Registered into directly — one exhaustion <see cref="StatusDef"/> per
    /// resource id present in <paramref name="debuffsByResourceId"/>, alongside its
    /// <see cref="StatusCategoryRegistry"/> entry (additive; the 21 locked ids in
    /// <c>StatusCatalogBootstrap</c> are untouched).</param>
    /// <param name="debuffsByResourceId">One entry per exhaustible resource this policy manages —
    /// never <c>hp</c>. Validated here, at load: a debuff that touches its OWN resource's regen
    /// channel is the one true spiral (§7 — "an exhaustion debuff must never touch a channel feeding
    /// its own resource's regen") and is rejected at construction rather than surfacing as a battle
    /// that never recovers.</param>
    public ExhaustionPolicy(StatusCatalog catalog, IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> debuffsByResourceId)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));

        foreach (var (resourceId, mods) in debuffsByResourceId)
        {
            if (resourceId == "hp")
                throw new ArgumentException("hp exhaustion does not exist -- depletion is death (resource-hub-ssot.md §10)", nameof(debuffsByResourceId));

            var ownRegenChannel = DerivedStatChannels.ResourceRegen(resourceId);
            foreach (var mod in mods)
            {
                if (mod.ChannelId == ownRegenChannel)
                    throw new ArgumentException(
                        $"self-regen cycle: '{resourceId}' exhaustion must not touch its own regen channel '{ownRegenChannel}'",
                        nameof(debuffsByResourceId));
            }

            var statusId = ExhaustionStatusIds.For(resourceId);
            StatusCategoryRegistry.Register(statusId, StatusL2bCategory.Dot);
            catalog.Register(new StatusDef(
                statusId,
                StatusKind.Debuff,
                Family: "exhaustion",
                Categories: new[] { StatusL2bCategory.Dot },
                Tags: Array.Empty<string>(),
                Stacking: StatusStacking.Refresh,
                PayloadKinds: new[] { StatusPayloadKind.ModifyStat }));
        }

        _debuffs = debuffsByResourceId;
    }

    /// <summary>
    /// The only question gameplay code should gate on. A pure function of the ALREADY-RESOLVED pool
    /// value (T15's lazy reader) — never a stored flag, so a pool that decayed past its rail while
    /// nothing touched it is exhausted the moment anything asks (resource-hub-ssot.md §8).
    /// </summary>
    public static bool IsExhausted(long resolvedValue) => resolvedValue <= 0;

    /// <summary>
    /// Applies, withdraws, or does nothing, matching the current resolved value. Idempotent across
    /// repeated calls at an unchanged exhausted/not-exhausted state — a pool held at the threshold
    /// with regen trickling calls this every tick, and only the transition produces a write, which
    /// is what makes "one status apply, not one per tick" true and countable rather than an accident
    /// of how <see cref="StatusStacking.Refresh"/> happens to collapse duplicates.
    /// </summary>
    /// <returns>True only on a call that actually applied the status (a fresh enter transition) —
    /// false for an already-exhausted no-op, a not-exhausted no-op, or a withdraw.</returns>
    public bool Sync(StatusRuntime runtime, string hostPtr, string resourceId, long resolvedValue, DateTimeOffset now)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        if (!_debuffs.TryGetValue(resourceId, out var mods))
            return false; // not a resource this policy manages -- not an error, just nothing to do

        var statusId = ExhaustionStatusIds.For(resourceId);
        var grantId = ExhaustionStatusIds.GrantIdFor(hostPtr, resourceId);
        var exhausted = IsExhausted(resolvedValue);
        var live = FindLive(runtime, hostPtr, statusId);

        if (!exhausted)
        {
            if (live != null)
                runtime.ClearGrant(grantId); // explicit withdraw -- this status never expires on its own (see Apply below)
            return false;
        }

        if (live != null)
            return false; // already exhausted; no re-apply

        var outcome = runtime.Apply(
            new StatusApplyInput(
                StatusId: statusId,
                HostPtr: hostPtr,
                AttackerPtr: null,
                GrantId: grantId,
                BaseMagnitude: 1.0, // inert -- ModifyStat reads StatMods directly, never EffectiveMagnitude
                BaseDuration: 0,    // 0 -> ExpiresAt = DateTimeOffset.MaxValue (Apply): persists until ClearGrant, never a timed decay
                PeriodMs: 0,
                DurationMs: 0,
                AttackerLess: true,
                StatMods: mods),
            ScriptedRng,
            now);

        return outcome.Applied;
    }

    static StatusInstance? FindLive(StatusRuntime runtime, string hostPtr, string statusId)
    {
        foreach (var instance in runtime.ForHost(hostPtr))
        {
            if (instance.StatusId == statusId)
                return instance;
        }
        return null;
    }
}
