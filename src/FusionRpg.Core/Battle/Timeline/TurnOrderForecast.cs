namespace FusionRpg.Core.Battle.Timeline;

/// <summary>How much a forecast can be trusted under a given profile (spec-turn-order-forecast.md §2).</summary>
public enum ForecastExactness
{
    /// <summary>Next-event advance, one actor mid-action: nothing can insert ahead of a forecast
    /// entry between now and when it fires. `classic-round`, `galaxy-sync`.</summary>
    Exact,

    /// <summary>Fixed-increment advance with `W > 1`: an action resolving inside the window can
    /// schedule an event that lands ahead of a forecast entry. Still the queue's current truth,
    /// just not a promise. `hybrid-atb`.</summary>
    SoftBounded,

    /// <summary>We do not own the clock, so there is nothing to project (`battle-turn-ideal.md` §1).</summary>
    Absent
}

/// <summary>
/// T8 — "who acts next, and in what order", answered by <b>reading</b> the queue rather than by
/// modelling it a second time.
///
/// <para><b>The projection never mutates.</b> That is the acceptance, not a nicety: a forecast that
/// consumed the queue would be a drain wearing a different name. It works on a copy, and the caller's
/// queue is observably identical afterwards — same `Count`, same `PeekDueTick`, and a subsequent real
/// drain yields exactly what the forecast said.</para>
///
/// <para><b>It does not simulate.</b> No readiness is advanced, no dice are rolled, no effects are
/// applied. An actor still `Charging` has no scheduled event and simply does not appear. Anything more
/// would be a second engine, and the two would drift — which is the failure this module's own map
/// entry exists to prevent ("it validates that the queue really is the single source of truth").</para>
/// </summary>
public static class TurnOrderForecast
{
    /// <summary>
    /// How far a forecast can be trusted under <paramref name="profile"/>. Stated per profile because
    /// a forecast that overpromises is worse than none: a UI rail that silently reorders has lied.
    /// </summary>
    public static ForecastExactness ExactnessFor(BattleModeProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));

        // The rule itself lives on BattleModeProfile, in the one file ModeProfileArchitectureTests
        // allows to branch on AdvancePolicyKind. That guard is right and this module respects it
        // rather than asking for an exemption: a fourth mode should inherit the correct answer by
        // construction, not by someone remembering to extend a switch out here.
        return profile.ForecastExactness;
    }

    /// <summary>
    /// The next <paramref name="max"/> events in the exact order <paramref name="queue"/> would pop
    /// them, appended to <paramref name="into"/>. Returns how many were written.
    ///
    /// <para>Fills a caller-owned list, mirroring <c>EventQueue.PopDue</c>'s own shape, because the
    /// kernel's callers are frame-critical and a method that allocated its own result would be the
    /// wrong shape for the one place this is most likely to be called from.</para>
    ///
    /// <para><paramref name="max"/> is a bound, not a promise: fewer scheduled events than asked for
    /// returns what exists. A negative bound is refused rather than quietly treated as zero, matching
    /// <c>PopDue</c>.</para>
    /// </summary>
    public static int Project(EventQueue queue, int max, List<ScheduledEvent> into)
    {
        if (queue is null) throw new ArgumentNullException(nameof(queue));
        if (into is null) throw new ArgumentNullException(nameof(into));
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), max, "A forecast length is never negative.");

        return queue.ProjectNext(max, into);
    }

    /// <summary>Convenience for callers that just want the list — a UI rail, a test. Allocates, and is
    /// deliberately not the overload the kernel path uses.</summary>
    public static IReadOnlyList<ScheduledEvent> Project(EventQueue queue, int max)
    {
        var into = new List<ScheduledEvent>(Math.Max(0, max));
        Project(queue, max, into);
        return into;
    }
}
