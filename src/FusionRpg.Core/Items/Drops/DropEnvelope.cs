using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Drops;

/// <summary>One rung as the pipeline reads it — module 7's seeded row plus its drop weight.</summary>
/// <param name="PrefixRolls">⚠ TWO counts, not one. The lane's <c>band.PoolRolls</c> is stale: the
/// shipped schema is <c>rarity(rarity_id, ordinal, prefix_rolls, suffix_rolls, min_tier, max_tier)</c>
/// (`RpgStore.Containers.cs`), because D2 and PoE both cap the two classes separately.</param>
public sealed record RarityRung(
    string RarityId, int Ordinal, int PrefixRolls, int SuffixRolls, int MinTier, int MaxTier, int DropWeightPer100k);

/// <summary>Step 8's outcome: the tier window and the two roll counts, after every narrowing.</summary>
public readonly record struct DropEnvelopeResult(
    int MinTier, int MaxTier, int PrefixRolls, int SuffixRolls, bool Narrowed);

/// <summary>
/// Step 8 — <c>band ∩ ilvl cap</c>, and the collapse rule.
///
/// <para>Delegates the tier arithmetic to module 8's shipped <see cref="IlvlTierLadder"/> rather than
/// restating it: D29's <c>1/1/8/18/32</c> and I12's COLLAPSING envelope (not I8's rejected sliding
/// window, which would drop t1 out of the window at high ilvl) already live there, and a second copy
/// is how two answers to one question come to ship.</para>
/// </summary>
public static class DropEnvelope
{
    /// <summary>
    /// Resolve the envelope for one rung at one item level.
    ///
    /// <para><c>env.minTier = min(band.MinTier, env.maxTier)</c> rather than a clamp upward is the
    /// anti-double-gating rule: a <c>[3,5]</c> band at ilvl 4 (cap t2) becomes <c>[2,2]</c> — six
    /// affixes at t2 — rather than an empty window that would have to reject or silently
    /// downgrade.</para>
    ///
    /// <para><paramref name="drawableGroups"/>, when supplied, is how many distinct drawable affix
    /// groups the base type's pool offers inside the narrowed window. If it is fewer than the rung
    /// asks for, the COUNT narrows and the drop records <c>envelope_narrowed</c> —
    /// <b>never</b> a rejection of a legal drop from legal content.</para>
    /// </summary>
    public static DropEnvelopeResult Resolve(
        RarityRung rung, int itemLevel, IAtomRandom rng,
        Func<int, int, int>? drawableGroups = null)
    {
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        var (minTier, maxTier) = IlvlTierLadder.Envelope(rung.MinTier, rung.MaxTier, itemLevel);

        // §4.2: NextInclusive(max(1, PoolRolls − 1), PoolRolls); 0 stays 0 for a rung with no pool.
        var prefix = DrawCount(rung.PrefixRolls, rng);
        var suffix = DrawCount(rung.SuffixRolls, rng);

        var narrowed = false;
        if (drawableGroups is not null)
        {
            var available = drawableGroups(minTier, maxTier);
            var p = EnvelopeNarrowing.Apply(prefix, available);
            var s = EnvelopeNarrowing.Apply(suffix, available);
            narrowed = p.Narrowed || s.Narrowed;
            prefix = p.RollCount;
            suffix = s.RollCount;
        }

        return new DropEnvelopeResult(minTier, maxTier, prefix, suffix, narrowed);
    }

    static int DrawCount(int bandRolls, IAtomRandom rng) =>
        bandRolls <= 0 ? 0 : rng.NextInclusive(Math.Max(1, bandRolls - 1), bandRolls);
}
