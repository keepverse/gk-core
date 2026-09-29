namespace FusionRpg.Core.Combat.Shield;

/// <summary>
/// Shield policy constants — shield-system-spec.md §2.4/§2.5, owner decisions 8–9.
/// Permille ints; changing any value is an Ask-first (spec §8) and breaks the math goldens.
/// </summary>
public static class ShieldPolicy
{
    static ShieldTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(ShieldTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static ShieldTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ShieldPolicy.Configure(...) has not run. Every shield rule reads data/tuning/shield.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>Matchup share K at permille scale (0.25). Own constant, decoupled from combat MatchupShareK.</summary>
    public static long MatchupShareKPm => Tuning.MatchupShareKPm;

    /// <summary>Chip floor (0.10× input): toughness saturates at 10× efficiency — immunity impossible.</summary>
    public static long ChipFloorKPm => Tuning.ChipFloorKPm;

    /// <summary>Pen cap (3× input): pen at best triples shield burn.</summary>
    public static long PenCapKPm => Tuning.PenCapKPm;

    public static int MaxShieldsPerActor => Tuning.MaxShieldsPerActor;

    // Default drain priorities (HIGHER drains first) — outer-to-core, owner decision 9.
    public static int PriorityAura => Tuning.DrainPriority.Aura;
    public static int PrioritySkill => Tuning.DrainPriority.Skill;
    public static int PriorityInnate => Tuning.DrainPriority.Innate;
}
