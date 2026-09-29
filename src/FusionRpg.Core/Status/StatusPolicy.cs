namespace FusionRpg.Core.Status;

/// <summary>Design defaults — actor-hub-ssot.md §5.</summary>
public static class StatusPolicy
{
    static StatusTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(StatusTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static StatusTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "StatusPolicy.Configure(...) has not run. Every status rule reads data/tuning/" +
        "status.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    // CategoryResistCap moved to Stats.Derived.DerivedStatPolicy (cap-consolidation, T1, 2026-08-24) —
    // it was clamped a second time here after DerivedComposer already applied it, making a raised
    // tunable a silent no-op. One key now: gk-core/data/tuning/derived-stats.v1.json.
    public static double ApplyScaleK => Tuning.ApplyScaleK;
    public static double ApplyScaleFloor => Tuning.ApplyScaleFloor;
    public static double ResistFromPowerRatio => Tuning.ResistFromPowerRatio;
    public static double MinNetFactor => Tuning.MinNetFactor;
    public static double MaxNetFactor => Tuning.MaxNetFactor;
    /// <summary>T3.2 (audit F4): netFactor = 1 + delta/NetFactorScale — linear, no cliff.</summary>
    public static double NetFactorScale => Tuning.NetFactorScale;
    public static double ProgressionPowerStubDefault => Tuning.ProgressionPowerStubDefault;
    public static int ProcDepthLimitDefault => Tuning.ProcDepthLimitDefault;

    /// <summary>P1: tierPower feeds ApplyScale and delta totals.</summary>
    public const bool IncludeTierPowerInDelta = true;

    /// <summary>Which curve turns <c>delta</c> into an apply chance (2026-08-25).</summary>
    public static StatusApplyShape ApplyShape => Tuning.ApplyShape;

    /// <summary>Where the apply contest's neutral point sits, in delta units. <c>0</c> reproduces the
    /// shipped behaviour exactly. Positive values mean an attacker needs a real power advantage before
    /// a status becomes likely — which is what stops an unequipped attacker landing everything on a
    /// coin flip.</summary>
    public static double ApplyOffsetK => Tuning.ApplyOffsetK;

    public static double ApplyScaleKForCategory(string category) => ApplyScaleK;

    public static double ApplySteepnessForCategory(string category) => Tuning.ApplySteepnessDefault;
}

/// <summary>
/// How <c>delta</c> becomes an apply probability (ResistanceEvaluator Phase 1).
/// </summary>
public enum StatusApplyShape
{
    /// <summary>Shipped v1: <c>sigmoid((delta − offset)/scale, steepness)</c>. Smooth and
    /// soft-counterable — it never reaches 0 or 1, so no amount of resistance confers immunity.
    /// <b>But a sigmoid's neutral point is 0.5 for every scale and every steepness</b>, so at
    /// <c>offset = 0</c> a status lands on a coin flip against a target that has bought no resistance
    /// at all. For a <c>cc</c> with duration ≥ 2 rounds that is not a 50% chance, it is a permanent
    /// lock (measured 2026-08-25). Raising <c>ApplyOffsetK</c> moves the neutral point without
    /// changing the shape, which keeps soft-counterability AND fixes the default.</summary>
    Sigmoid,

    /// <summary>Linear from zero: <c>clamp((delta − offset)/scale, 0, 1)</c>. The shape the evasion
    /// chain already chose for exactly this problem — <c>OverlayCombatCalculator</c>'s rate contests
    /// are linear per-mille precisely because <i>"a sigmoid would give 0.5 at delta=0 … which is not
    /// 'empty bands are a no-op', it is a new default nobody chose"</i>. Hard-counterable: enough
    /// resistance zeroes the apply chance outright, the same way <c>parry.break</c> can zero a parry.
    /// That is a real trade — see PS-8 and the RPS measurement's rule 5 on hard vs soft defences —
    /// which is why both shapes stay reachable rather than one replacing the other.</summary>
    LinearFromZero
}

public static class StatusL2bCategory
{
    public const string Dot = "dot";
    public const string Cc = "cc";
    public const string Contagion = "contagion";
}

public enum StatusResistReason
{
    Immunity,
    PotencyFloor,
    ApplyRoll,
    UselessMagnitude,
    UnknownStatus,
    StatusIcd
}

/// <summary>
/// <c>Delta</c>/<c>NetFactor</c> stay Phase 1's apply-chance delta and its net factor — untouched by
/// spec-status-potency.md's split (§2.1: "Phase 1 is untouched"). <c>DurationNetFactor</c> and
/// <c>IntensityNetFactor</c> are the NEW, independent Phase 2 values that actually drive
/// <c>EffectiveDuration</c>/<c>EffectiveMagnitude</c> now — the potency floor
/// (<see cref="StatusResistReason.PotencyFloor"/>) checks <c>IntensityNetFactor</c> only (§2.2: a
/// zero-duration status is instantaneous, a legitimate effect, not a resist).
/// </summary>
public sealed record StatusApplyResult(
    bool Applied,
    StatusResistReason? ResistReason,
    double Delta,
    double NetFactor,
    double PApply,
    double PFinal,
    double EffectiveApplyScale,
    double EffectiveMagnitude,
    double EffectiveDuration,
    double DurationNetFactor = 0,
    double IntensityNetFactor = 0);

public sealed record StatusApplyRequest(
    string StatusId,
    string HostPtr,
    string? AttackerPtr,
    double BaseMagnitude,
    double BaseDuration,
    double GrantChance = 1.0,
    bool AttackerLess = false,
    IReadOnlyList<string>? ImmunityTags = null);
