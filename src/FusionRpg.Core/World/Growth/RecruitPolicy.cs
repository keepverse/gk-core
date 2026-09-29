using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Growth;

/// <summary>
/// Every tunable growth constant, in one place with its reasoning — the `LoamPolicy`/`MovementPolicy`
/// precedent (world-map W42, spec-sector-development.md §1). Every number here is a provisional
/// placeholder except <see cref="LegionTarget"/>, which is the already-decided calibration target
/// (world-stage-ideal.md §8e.3): the L9-style acceptance harness (W59) decides what the rest should
/// be, this file only gives it something to run against. Ships at identity — <see cref="SeatPulsePerWeek"/>
/// is 0, so nothing accrues and no golden moves — until W58 turns it on.
/// </summary>
public static class RecruitPolicy
{
    static WorldGrowthTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(WorldGrowthTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static WorldGrowthTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "RecruitPolicy.Configure(...) has not run. Every growth rule reads data/tuning/world.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>What one held Seat contributes to its sector's `RecruitStock` on a week boundary,
    /// before a cleared lair or a special week scale it. Zero until a later publish turns growth on.</summary>
    public static long SeatPulsePerWeek => Tuning.SeatPulsePerWeek;

    /// <summary>What a cleared lair multiplies its sector's pulse by — the same shape a rootbed/well
    /// pair already uses (`StructureDef.YieldMultiplierMilli`), reused rather than a new idiom.</summary>
    public static int LairMultiplierMilli => Tuning.LairMultiplierMilli;

    /// <summary>What a special week scales the pulse by. A plague month suppresses growth outright
    /// and beats a special week — that rule lives in `TurnCalendar`, not here (`TurnCalendar.cs:52-54`).</summary>
    public static int SpecialWeekMultiplierMilli => Tuning.SpecialWeekMultiplierMilli;

    /// <summary>What `raise` spends from a sector's `RecruitStock` to found a legion there.</summary>
    public static long RaiseCostPoints => Tuning.RaiseCostPoints;

    /// <summary>
    /// A raised legion's one founding member's starting Hp (world-map W51) — its own tunable, not a
    /// reuse of `LoamPolicy.UnmadeMemberHp`: a barbarian's difficulty and a player's own legion
    /// strength are different balance surfaces.
    /// </summary>
    public static long RaiseMemberHp => Tuning.RaiseMemberHp;

    /// <summary>
    /// The 6–10-by-turn-40 calibration target — read only by the acceptance harness (W59), never by
    /// the engine: a legion count the engine enforced would be a hard progression ceiling (AGENTS.md).
    /// </summary>
    public static LegionTargetTuning LegionTarget => Tuning.LegionTarget;

    /// <summary>
    /// world-map W43: one sector's recruit-stock accrual for one week boundary
    /// (spec-sector-development.md §1) — pure over its inputs, no world mutation, no tuning lookup
    /// of its own. Every number the formula uses is one of this class's own accessors above; the
    /// caller (`GrowthPhases`, W50) is what reads them and the per-sector facts (`hasSeat`,
    /// `lairCleared`) from the world, so this leaf stays independently testable without mutating the
    /// process-wide tuning singleton other tests share (a real hazard under xUnit's default
    /// parallelism, since <see cref="Configure"/> sets one static field for the whole assembly).
    ///
    /// Zero unless the boundary is a real week boundary, the sector has a Seat, and
    /// <paramref name="seatPulsePerWeek"/> is positive (it ships at 0 — identity — until a later
    /// publish turns growth on, W58). **The plague beats the special week** — matching the rule
    /// <see cref="TurnCalendar"/> (`TurnCalendar.cs:52-54`) already applies to its own month term,
    /// reused here rather than re-derived. The two multipliers compose on one combined per-mille
    /// product with a single division at the end (AGENTS.md's overflow rule: widen before
    /// multiplying, divide by 1000 last and exactly once — here twice-per-mille, so once by
    /// 1,000,000), inside a `checked` block so an overflow throws rather than wrapping.
    /// </summary>
    public static long PulseFor(
        bool hasSeat, bool lairCleared, CalendarRoll roll,
        long seatPulsePerWeek, int lairMultiplierMilli, int specialWeekMultiplierMilli)
    {
        if (!hasSeat || !roll.WeekBoundary || roll.Plague || seatPulsePerWeek <= 0) return 0;

        var lairFactorMilli = lairCleared ? lairMultiplierMilli : 1000;
        var weekFactorMilli = roll.SpecialWeek ? specialWeekMultiplierMilli : 1000;

        checked
        {
            return seatPulsePerWeek * lairFactorMilli * weekFactorMilli / 1_000_000;
        }
    }
}
