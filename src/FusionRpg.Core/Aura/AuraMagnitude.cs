using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Aura;

/// <summary>
/// aura-skill T10 (`spec-aura-magnitude.md` §1): how strong an aura is —
/// <c>k(rung) · share^γ · P(Θ)</c>, through the SHARED <see cref="AptitudeReadFunctions.Magnitude"/>,
/// never a second copy of that arithmetic. Two axes, per the owner's own decision (Q10, 2026-08-30):
/// the aura's own level (`rung`, via the declared <see cref="AuraTuning.RungMapping"/>) and the
/// commander's specialization in that aura's aptitude (`share`) — a commander built for offense
/// cannot buff defence well, because `share` for a defensive aptitude is near zero for them, and at
/// exactly zero the product is exactly zero (base-independence: the result depends only on this
/// aura's own two axes, never on what else contributes to the target channel).
///
/// <para><b>Not "the rung is the level."</b> `ActionRow.Rung` is an authored column nobody advances —
/// the mapping from rung to `k` is declared at registration (`spec-rung-table.md:137`), which is
/// exactly what <see cref="AuraTuning"/> is.</para>
///
/// <para><b>Lives outside `Core/Actions/` on purpose.</b> An earlier placement under `Actions/Aura/`
/// (the spec's own suggested path) tripped `ActionsPurityGuardTests` — that directory bans any bare
/// `double` declaration with no exceptions, and `share` (bounded [0,1], the same shape
/// `AptitudeReadFunctions.Magnitude` itself already takes) needs one. `AptitudeReadFunctions` lives in
/// `Stats/Aptitudes/` for the exact same reason; this type sits beside it rather than repeating the
/// mistake the purity guard exists to catch.</para>
/// </summary>
public static class AuraMagnitude
{
    /// <summary>
    /// The aura's contribution to one channel. `γ` is deliberately NOT a parameter here — it is
    /// `tuning.Read.Magnitude.ShareExponentMilli`, the SAME share→effect curve every other magnitude
    /// edge in `aptitudes.v2.json` uses (spec §6's own rule: a third, aura-local exponent would be a
    /// new power-shaped curve `guard-power.py` forbids, or a duplicate of an existing one — neither
    /// is acceptable). Emits the same `long` a `DerivedModifier`'s `Flat` value needs directly.
    /// </summary>
    public static long Compute(int rung, double share, long pTheta, AuraTuning auraTuning, AptitudeTuning aptitudeTuning)
    {
        if (aptitudeTuning is null) throw new ArgumentNullException(nameof(aptitudeTuning));
        var kMilli = (auraTuning ?? throw new ArgumentNullException(nameof(auraTuning))).KMilliFor(rung);
        return AptitudeReadFunctions.Magnitude(kMilli, share, aptitudeTuning.Read.Magnitude.ShareExponentMilli, pTheta);
    }

    /// <summary>
    /// backlog-clear AU2 (`spec-aura-binding-producer.md`'s own successor task, `backlog-clear-todo.md`
    /// AU2): the reference channel value the twelve `world-buff.aura-*` seed containers resolve their
    /// `externalRef` amounts to, at whichever channel index that container declares.
    ///
    /// <para><b>Reference point, not live per-commander scaling — named plainly, not hidden.</b> The
    /// full design (a commander's own current aptitude `share` and Θ) needs per-player aptitude-state
    /// reads this backlog task does not build; wiring that is a named follow-up. What ships here is a
    /// STRUCTURAL reference every aura shares — <see cref="AuraTuning.MinRung"/> (the floor every aura
    /// is guaranteed to at least provide), full share (<c>1.0</c>), and <c>P(Θ)</c> at the power
    /// ladder's own pin (<c>PowerTuning.FixedPinIndex</c> = 20, the same anchor the rest of the content
    /// corpus is authored against) — computed by the SAME <see cref="Compute"/> formula every real
    /// magnitude uses, never a second one, so "no hand-picked per-aura constants" holds for the twelve
    /// auras exactly as it does for every other <see cref="Compute"/> caller.</para>
    ///
    /// <para><b>Even split, remainder to the earliest channels.</b> An aura's total reference budget
    /// divides across <paramref name="channelCount"/> equally; a total that does not divide evenly
    /// gives the extra unit to the lowest <paramref name="channelIndex"/> values first, so
    /// <c>Σ channels == total</c> exactly (spec-aura-content.md's own "budget conservation" rule) —
    /// dropping the remainder would silently under-grant the aura by up to <c>channelCount − 1</c>.</para>
    /// </summary>
    public static long ReferenceChannelValue(
        int channelIndex, int channelCount, long pTheta, AuraTuning auraTuning, AptitudeTuning aptitudeTuning)
    {
        if (channelCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount), channelCount, "an aura's channel count must be positive");
        if (channelIndex < 0 || channelIndex >= channelCount)
            throw new ArgumentOutOfRangeException(nameof(channelIndex), channelIndex,
                $"channelIndex must be in [0, {channelCount})");

        var total = Compute(AuraTuning.MinRung, share: 1.0, pTheta, auraTuning, aptitudeTuning);
        var baseShare = total / channelCount;
        var remainder = total % channelCount;
        return channelIndex < remainder ? baseShare + 1 : baseShare;
    }
}
