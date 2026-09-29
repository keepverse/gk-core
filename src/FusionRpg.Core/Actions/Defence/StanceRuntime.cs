using FusionRpg.Core.Status;

namespace FusionRpg.Core.Actions.Defence;

/// <summary>Status id for the held-stance self-buff.</summary>
public static class StanceStatusIds
{
    public const string Guard = "stance.guard";
}

/// <summary>
/// T25 (spec-defence-actions.md §1): the real <see cref="IStanceCheck"/> — Phase 2's own seam note
/// says it directly: "`A8` supplies the real implementation; until then no actor is ever mid-stance."
/// An actor mid-stance may only take the stance's OWN declared release `action_id`; every other
/// action, including movement, is refused with <see cref="UsabilityReason.StanceHeld"/> — gate 0 has
/// no exemption list, so "guard-while-moving" passes by being a DIFFERENT release action, never by a
/// bypass rule (spec §1: "guard-while-moving is a different skill, not a basic action").
///
/// <para><b>No new FSM state, no runtime of its own</b> (spec §1: "if this module grows a runtime of
/// its own, something is wrong"). "Held" is a plain per-actor dictionary entry here, and the visible
/// stat effect is an ordinary <see cref="StatusRuntime"/> self-status — the SAME
/// <c>AttackerLess</c> + <c>FixedStatusRng(0.0)</c> deterministic-apply pattern T20's
/// <c>ExhaustionPolicy</c> already uses for a structural, non-combat fact, and the SAME
/// persist-until-explicitly-cleared shape (<c>BaseDuration: 0</c> → never expires on its own;
/// <c>ClearGrant</c> on release).</para>
/// </summary>
public sealed class StanceRuntime : IStanceCheck
{
    readonly Dictionary<string, string> _releaseActionIdByActor = new(StringComparer.Ordinal);
    static readonly IStatusRng ScriptedRng = new FixedStatusRng(0.0);

    /// <param name="catalog">Registered into directly, same as <c>ExhaustionPolicy</c>'s
    /// constructor — additive; the 21 locked ids in <c>StatusCatalogBootstrap</c> are untouched.</param>
    public StanceRuntime(StatusCatalog catalog)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        StatusCategoryRegistry.Register(StanceStatusIds.Guard, StatusL2bCategory.Dot);
        catalog.Register(new StatusDef(
            StanceStatusIds.Guard,
            StatusKind.Buff,
            Family: "stance",
            Categories: new[] { StatusL2bCategory.Dot },
            Tags: Array.Empty<string>(),
            Stacking: StatusStacking.Replace,
            PayloadKinds: new[] { StatusPayloadKind.ModifyStat }));
    }

    /// <summary>True while <paramref name="actorKey"/> holds a stance — the only thing this class
    /// tracks about "held" beyond the status instance itself.</summary>
    public bool IsHeld(string actorKey) => _releaseActionIdByActor.ContainsKey(actorKey);

    /// <summary>
    /// Raises the stance: the actor may now take ONLY <paramref name="releaseActionId"/> until
    /// released, and gains the held self-status carrying <paramref name="statMods"/> (the raised
    /// defensive channels — content this module does not author values for, matching spec §0: "A8
    /// authors no damage math").
    /// </summary>
    public void Raise(
        StatusRuntime statuses, string actorKey, string releaseActionId,
        IReadOnlyList<StatusStatMod> statMods, DateTimeOffset now)
    {
        if (statuses is null) throw new ArgumentNullException(nameof(statuses));
        if (string.IsNullOrWhiteSpace(actorKey)) throw new ArgumentException("actorKey required", nameof(actorKey));
        if (string.IsNullOrWhiteSpace(releaseActionId)) throw new ArgumentException("releaseActionId required", nameof(releaseActionId));

        _releaseActionIdByActor[actorKey] = releaseActionId;

        statuses.Apply(
            new StatusApplyInput(
                StatusId: StanceStatusIds.Guard,
                HostPtr: actorKey,
                AttackerPtr: null,
                GrantId: GrantIdFor(actorKey),
                BaseMagnitude: 1.0, // inert -- ModifyStat reads StatMods directly, never EffectiveMagnitude
                BaseDuration: 0,    // 0 -> ExpiresAt = DateTimeOffset.MaxValue: persists until Release, never a timed decay
                PeriodMs: 0,
                DurationMs: 0,
                AttackerLess: true,
                StatMods: statMods),
            ScriptedRng,
            now);
    }

    /// <summary>Ends the stance: clears the gate-0 refusal and withdraws the held self-status. Safe
    /// to call on an actor who is not currently holding — a no-op either way.</summary>
    public void Release(StatusRuntime statuses, string actorKey)
    {
        if (statuses is null) throw new ArgumentNullException(nameof(statuses));
        _releaseActionIdByActor.Remove(actorKey);
        statuses.ClearGrant(GrantIdFor(actorKey));
    }

    /// <summary>Gate 0 (spec-usability-conditions.md): <c>null</c> means "does not refuse" — proceed
    /// to gate 1. The release action itself always passes; everything else, including movement,
    /// refuses with a typed reason naming the stance.</summary>
    public UsabilityResult? Check(string actorKey, string actionId)
    {
        if (!_releaseActionIdByActor.TryGetValue(actorKey, out var releaseId))
            return null;

        return actionId == releaseId ? null : UsabilityResult.Refuse(UsabilityReason.StanceHeld, actorKey);
    }

    static string GrantIdFor(string actorKey) => "stance:" + actorKey;
}
