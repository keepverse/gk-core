namespace FusionRpg.Core.Power;

/// <summary>
/// Why a power-scale tuning document was refused (spec-power-ladder.md §2.4). Never a default —
/// a rejection names the rule that fired so a balance edit can be fixed without a debugger.
/// </summary>
public enum PowerRejectionReason
{
    /// <summary>Tuning document absent, unparseable, or missing/mistyped a required field. No built-in fallback constants exist.</summary>
    TuningMissing,

    /// <summary>bMilli is negative — a concave ladder is not a design, it is a typo.</summary>
    NegativeB,

    /// <summary>bMilli does not divide the pin exactly; A would need rounding, and a rounded A breaks the pin (§2.2).</summary>
    OddB,

    /// <summary>A weight component is negative.</summary>
    NegativeWeight,

    /// <summary>cMilli / pinIndex / pinValue differ from the fixed anchor (80000 / 20 / 680) — ask-first, not a tuning knob (§4.3).</summary>
    FixedConstantChanged,

    /// <summary>The derived A does not reproduce pinValue at pinIndex — the belt-and-braces check on §2.2's algebra.</summary>
    PinBroken,
}

/// <summary>Thrown by <see cref="PowerTuningLoader"/> / <see cref="PowerTuning"/>. Carries which rule fired.</summary>
public sealed class PowerTuningRejection : Exception
{
    public PowerRejectionReason Reason { get; }

    public PowerTuningRejection(PowerRejectionReason reason, string message) : base(message)
    {
        Reason = reason;
    }
}
