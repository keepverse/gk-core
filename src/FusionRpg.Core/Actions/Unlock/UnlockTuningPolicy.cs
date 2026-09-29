namespace FusionRpg.Core.Actions.Unlock;

/// <summary>
/// T59.7 (spec-action-instance-and-grant.md §4): the process-wide, configured-once
/// `gk-core/data/tuning/action-unlock.v1.json` tuning, mirroring `RungPolicy`/`ActionTimingPolicy`'s own
/// "no built-in default, configure once at startup" shape exactly. This is the FIRST real,
/// production, non-test caller of the unlock ladder (`UnlockState.TryAccept`,
/// `UnlockLadder.EffectiveRung`) — every earlier caller was a test fixture building its own
/// `UnlockTuning` literal, so no shared, load-once holder existed yet.
/// </summary>
public static class UnlockTuningPolicy
{
    static UnlockTuning? _tuning;

    public static void Configure(UnlockTuning tuning) => _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Null, never throwing, until configured — unlike `RungPolicy`/`ActionTimingPolicy`
    /// (both configured by every real host AND by the shared test bootstrap before any test runs),
    /// this is read from a brand-new production call site (`RpgStore.TryRollActionUnlocks`) that
    /// EVERY existing XP-award caller across every test project already reaches whenever a level
    /// crosses — throwing here would turn an unrelated level-up test in a project that never
    /// configures this into a hard failure. The caller treats null as "skip the roll, not an error."
    /// </summary>
    public static UnlockTuning? Tuning => _tuning;
}
