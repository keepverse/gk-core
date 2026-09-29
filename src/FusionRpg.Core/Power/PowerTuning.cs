namespace FusionRpg.Core.Power;

/// <summary>Θ-ladder curve (ssot-power-scale.md §4): P(Θ) = C + A·Θ + B·Θ(Θ−1)/2, all per-mille.</summary>
public sealed record PowerCurveTuning(long CMilli, long AMilli, long BMilli, int PinIndex, long PinValue);

/// <summary>
/// The five ladders' per-mille weights (ssot-power-scale.md §5.3), consumed by power-index (T1.3) —
/// one file, one load (spec-power-index.md §2.1). <see cref="WmMilli"/> is legal as <c>null</c> at
/// rest; power-index throws when <c>ContentIndex</c> first needs it, never here.
/// </summary>
public sealed record PowerWeightsTuning(long WdMilli, long WaMilli, long WrMilli, long WzMilli, long? WmMilli, long WwMilli, long WfMilli);

/// <summary>
/// Loaded, validated Θ-ladder tuning. Construct only via <see cref="PowerTuningLoader.Parse"/> (or
/// <see cref="Build"/> directly from already-parsed fields, which is what the loader calls).
/// </summary>
/// <param name="Channels">
/// Per-channel pins for <see cref="ChannelLadder"/> (T2.1, spec-battle-magnitude.md §2.1) — atk,
/// defense, and any future magnitude channel. hp needs no entry (it reads <see cref="FixedCMilli"/>/
/// <see cref="FixedPinValue"/> directly). Empty by default: T1.1's own tests predate T2.1 and never
/// mention channels — an absent key here means "no channel loaded yet", not a rejection; a consumer
/// that actually needs a specific channel (<c>ChannelLadder</c>'s constructor) is where a missing key
/// throws, matching WmMilli's own "legal to be absent, throws only where it's actually needed" shape.
/// </param>
public sealed record PowerTuning(int SchemaVersion, int Version, PowerCurveTuning Curve, PowerWeightsTuning Weights,
    IReadOnlyDictionary<string, PowerChannelTuning>? Channels = null)
{
    /// <summary>Never null — <see cref="Build"/> substitutes empty when the caller passes null.</summary>
    public IReadOnlyDictionary<string, PowerChannelTuning> ChannelsOrEmpty => Channels ?? EmptyChannels;
    static readonly IReadOnlyDictionary<string, PowerChannelTuning> EmptyChannels =
        new Dictionary<string, PowerChannelTuning>(StringComparer.Ordinal);

    // The anchor the item corpus is authored against (ssot-power-scale.md §4.3). Changing any of
    // these three is an ask-first ADR, never a tuning edit — see FixedConstantChanged below.
    internal const long FixedCMilli = 80_000;
    // Paired with FixedCMilli/FixedPinValue above — the pin BattleRuleset.BaseHp(20) is calibrated at.
    internal const int FixedPinIndex = 20;
    // Paired with FixedCMilli/FixedPinIndex above — P(20) the item corpus is authored against.
    internal const long FixedPinValue = 680;

    /// <summary>
    /// Validates raw parsed fields and derives `A` from the pin (§4.3) — a caller can never author
    /// `A` directly, which is what makes "retune `B`, item corpus unaffected" a structural guarantee
    /// rather than a promise. Every check is a typed <see cref="PowerTuningRejection"/>; none defaults.
    /// </summary>
    public static PowerTuning Build(
        int schemaVersion, int version,
        long cMilli, long bMilli, int pinIndex, long pinValue,
        long wdMilli, long waMilli, long wrMilli, long wzMilli, long? wmMilli, long wwMilli, long wfMilli,
        IReadOnlyDictionary<string, PowerChannelTuning>? channels = null)
    {
        if (cMilli != FixedCMilli || pinIndex != FixedPinIndex || pinValue != FixedPinValue)
            throw new PowerTuningRejection(PowerRejectionReason.FixedConstantChanged,
                $"power tuning: cMilli/pinIndex/pinValue must be {FixedCMilli}/{FixedPinIndex}/{FixedPinValue} " +
                $"(anchored to BattleRuleset.BaseHp and the item corpus, ssot-power-scale.md §4.3) — got {cMilli}/{pinIndex}/{pinValue}");

        if (bMilli < 0)
            throw new PowerTuningRejection(PowerRejectionReason.NegativeB,
                $"power tuning: bMilli must not be negative — got {bMilli}");

        // A_milli = (pinValue·1000 − cMilli − bMilli·pinIndex·(pinIndex−1)/2) / pinIndex (§2.2).
        // pinIndex·(pinIndex−1) is always even, so the triangular term itself never needs bMilli to
        // be even — it is the FINAL division by pinIndex that requires it, for this fixed pinIndex=20.
        // `checked`: bMilli is operator-authored config, not a bounded runtime value — an absurd
        // entry must throw OverflowException, never silently wrap into a plausible-looking curve
        // (docs/architecture/numeric-types.md: "overflow throws, never wraps; no silent unchecked on a magnitude path").
        long aMilli, triangularMilli;
        checked
        {
            triangularMilli = bMilli * (long)pinIndex * (pinIndex - 1) / 2;
            long numerator = pinValue * 1000L - cMilli - triangularMilli;
            if (numerator % pinIndex != 0)
                throw new PowerTuningRejection(PowerRejectionReason.OddB,
                    $"power tuning: bMilli={bMilli} does not divide the pin exactly (A would need rounding, which breaks the pin) — " +
                    $"nearest legal values are {bMilli - 1} and {bMilli + 1}");
            aMilli = numerator / pinIndex;
        }

        // Belt-and-braces (§2.2): re-derive P(pinIndex) independently of the derivation above and
        // check it reproduces pinValue exactly. Algebraically this cannot fail given a correct
        // derivation — it exists to catch a future edit that breaks one formula without the other.
        long pinCheckMilli = checked(cMilli + aMilli * pinIndex + triangularMilli);
        if (pinCheckMilli != pinValue * 1000L)
            throw new PowerTuningRejection(PowerRejectionReason.PinBroken,
                $"power tuning: derived curve does not reproduce the pin — P({pinIndex})·1000 = {pinCheckMilli}, expected {pinValue * 1000L}");

        foreach (var (name, w) in new (string, long)[] { ("Wd", wdMilli), ("Wa", waMilli), ("Wr", wrMilli), ("Wz", wzMilli), ("Ww", wwMilli), ("Wf", wfMilli) })
        {
            if (w < 0)
                throw new PowerTuningRejection(PowerRejectionReason.NegativeWeight,
                    $"power tuning: weight {name}Milli must not be negative — got {w}");
        }
        if (wmMilli is { } wmValue && wmValue < 0)
            throw new PowerTuningRejection(PowerRejectionReason.NegativeWeight,
                $"power tuning: weight WmMilli must not be negative — got {wmValue}");

        if (channels is not null)
        {
            foreach (var (name, ch) in channels)
            {
                if (ch.CMilli < 0)
                    throw new PowerTuningRejection(PowerRejectionReason.NegativeWeight,
                        $"power tuning: channel '{name}' cMilli must not be negative — got {ch.CMilli}");
                if (ch.PinValue <= 0)
                    throw new PowerTuningRejection(PowerRejectionReason.PinBroken,
                        $"power tuning: channel '{name}' pinValue must be positive — got {ch.PinValue}");
            }
        }

        var curve = new PowerCurveTuning(cMilli, aMilli, bMilli, pinIndex, pinValue);
        var weights = new PowerWeightsTuning(wdMilli, waMilli, wrMilli, wzMilli, wmMilli, wwMilli, wfMilli);
        return new PowerTuning(schemaVersion, version, curve, weights, channels);
    }
}
