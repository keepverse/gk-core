using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// D18 — how many items drop is a LINEAR read of Θ; how strong they are keeps reading P(Θ) through
/// the untouched rarity / tier / <c>contentScale</c> path.
///
/// <para>Volume is LINEAR in Θ, not quadratic. P(Θ) is quadratic (<c>Power/PowerLadder.cs</c>, the
/// triangular term) and quadratic growth in item COUNT floods an armoury whose management minigame is
/// deferred (D5). PS-3 assigns contests to Θ and magnitudes to P(Θ); a drop count is neither — it is
/// a rate — so it reads Θ, which is the same axis, not a private curve.</para>
///
/// <para><b>No f(level) is declared here.</b> Θ arrives through
/// <see cref="IPowerIndexProvider.ActorIndex"/>, already composed in shipped code from exactly the
/// owner's three inputs (dave level, realms advanced, PvZ runs —
/// <c>Power/PowerIndexComposer.cs</c>). The world-stage term is content-side
/// (<c>ContentContext.WorldTier</c>) and contributes nothing until the world map ships; a weighted
/// arithmetic sum degrades to its available terms with no special-casing.</para>
///
/// <para>⛔ <b>There is no upper bound, anywhere.</b> A ceiling on volume would be metering the
/// player, which D26 puts permanently outside this program's scope.
/// <see cref="DropVolumeTuning.FloorMilli"/> is the one bound and it is STRUCTURAL: a draw rate
/// cannot be negative, and a Θ below the pin must not produce a negative draw count.</para>
/// </summary>
public static class DropVolume
{
    /// <summary>
    /// The volume scale in per-mille. <c>1000</c> is ×1.0 and lands exactly at
    /// <see cref="DropVolumeTuning.ThetaPin"/>, which is what makes spec-drop-volume.md Correction 1's
    /// per-event yields reproduce exactly.
    /// </summary>
    public static long VolumeScaleMilli(int thetaActor, DropVolumeTuning t)
    {
        // Widen before multiplying; the product is per-mille and is divided by 1000 exactly once, in
        // RollsEffective, never here. Overflow throws rather than wrapping (AGENTS.md).
        long delta = checked((long)thetaActor - t.ThetaPin);
        long scale = checked(t.VolumeBaseMilli + t.VolumeSlopeMilli * delta);
        return Math.Max(t.FloorMilli, scale);
    }

    /// <summary>
    /// Θ read through the shipped provider — the only door into this module. There is no
    /// subsystem-local <c>f(level)</c> here and there must never be one (ssot-power-scale.md §10's
    /// closed inventory).
    /// </summary>
    public static long VolumeScaleMilli(IPowerIndexProvider power, StatContext ctx, DropVolumeTuning t)
    {
        if (power is null) throw new ArgumentNullException(nameof(power));
        return VolumeScaleMilli(power.ActorIndex(ctx), t);
    }

    /// <summary>
    /// Step 5a — how many times a group actually draws. The integer part plus a Bernoulli on the
    /// fractional remainder, taken on the group's OWN named stream
    /// (<see cref="LootStreams.Volume"/>) so introducing step 5a shifts nothing that already rolled.
    /// </summary>
    public static long RollsEffective(long groupRolls, long volumeScaleMilli, IAtomRandom rng)
    {
        if (groupRolls < 0) throw new ArgumentOutOfRangeException(nameof(groupRolls), groupRolls, "a group's roll count cannot be negative");
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        // Widen before multiplying, divide by 1000 LAST and exactly once.
        long scaled = checked(groupRolls * volumeScaleMilli);
        long whole = scaled / 1000;
        long remainderMilli = scaled - checked(whole * 1000);

        // Integer-only, unbiased (AtomRandom.NextPerMille). No float ever touches a magnitude here.
        if (remainderMilli > 0 && rng.NextPerMille() < remainderMilli)
            whole = checked(whole + 1);

        return whole;
    }

    /// <summary>
    /// The EXPECTED number of draws, in per-mille, with no RNG — <c>groupRolls × scale</c>, which is
    /// exactly <c>E[RollsEffective] × 1000</c>. Used by the Correction 1 calibration test, which must
    /// assert a yield rather than sample one.
    /// </summary>
    public static long ExpectedRollsMilli(long groupRolls, long volumeScaleMilli) =>
        checked(groupRolls * volumeScaleMilli);

    /// <summary>
    /// D38 roll 1 — <b>does anything drop at all</b> on a kill. A flat per-mille gate, the same rate
    /// for every actor: on kills a veteran and a beginner see the same rate, and progression shows up
    /// as WHAT drops.
    ///
    /// <para>⛔ This is NOT a chance at any particular rung. WHICH rung is roll 2, against the rarity
    /// catalog's own weights — a different table entirely (<see cref="RarityDraw"/>). Conflating the
    /// two would make the top rung 20× more common than the rarity catalog says, and it is the exact
    /// misreading the owner pre-empted when ruling D38.</para>
    /// </summary>
    public static bool RollsAnythingOnKill(int thetaActor, DropVolumeTuning t, IAtomRandom rng)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        var chanceMilli = t.DropChanceOnKillMilli;
        if (t.KillScalesWithTheta)
        {
            // Recorded, not silently resolved (spec §D38): if the kill path should also scale with Θ,
            // it is this flag, not a redesign. Same linear read, same floor, same absence of a cap.
            chanceMilli = checked(chanceMilli * VolumeScaleMilli(thetaActor, t)) / 1000;
            chanceMilli = Math.Min(1000, chanceMilli); // a PROBABILITY, a bounded ratio — AGENTS.md exempts these by name.
        }

        return rng.NextPerMille() < chanceMilli;
    }
}
