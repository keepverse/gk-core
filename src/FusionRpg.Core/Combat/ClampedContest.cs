namespace FusionRpg.Core.Combat;

/// <summary>
/// spec-evasion-chain.md §2 — the shape shield-system-spec.md §2.4 already used, generalised so
/// block/parry (T5.3) reuse it rather than authoring a second saturation curve (Q6:
/// <c>block.strength ↔ block.shred</c> is arithmetically identical to the shipped
/// <c>shield.toughness ↔ shield.pen</c>). All arithmetic is 64-bit integer at permille scale,
/// matching <c>ShieldMath</c>'s own rule — no floats in any game-affecting branch.
/// </summary>
public static class ClampedContest
{
    /// <param name="deltaBase">The pre-contest amount the delta term adds to — for shield,
    /// <c>input + elemMod</c> (element matchup is shield-specific and stays computed by the caller;
    /// T5.3's block/parry never read <c>ShieldElementMatrix</c>, per spec §7). For block/parry
    /// (no elemMod concept at all), this is the same authored amount as
    /// <paramref name="boundsBase"/>.</param>
    /// <param name="delta">Attacker side minus defender side, already subtracted by the caller — for
    /// shield, <c>pen − toughness</c> (<c>breakerDelta</c>). One pre-combined delta rather than two
    /// separate parameters: mathematically identical (<c>hitCount × (a − d) == hitCount × delta</c>),
    /// and matches <c>ShieldMath.AbsorbLayer</c>'s existing parameter shape exactly, so extracting
    /// this helper changes zero call-site behaviour.</param>
    /// <param name="hitCount">Coalesced hit count (≥ 1) — scales the delta term so coalesced ≡ n×
    /// uncoalesced.</param>
    /// <param name="boundsBase">What the floor and cap scale against. For shield this is the RAW
    /// <c>input</c>, deliberately NOT <c>input + elemMod</c> — the shipped math has always bounded
    /// against the pre-matchup amount, and "shield behaviour unchanged" (T5.2's own acceptance
    /// criterion) means preserving that exactly, even though spec-evasion-chain.md §2's own
    /// pseudocode describes both the delta term and the bounds using one shared "base" — checked
    /// against `ShieldMath.AbsorbLayer`'s actual shipped code, not assumed from the spec's prose, and
    /// the two differ whenever a real elemental matchup makes elemMod nonzero. For block/parry (T5.3,
    /// no elemMod), this is the same authored amount as <paramref name="deltaBase"/>.</param>
    /// <param name="floorKPm">Floor as a per-mille share of <paramref name="boundsBase"/>, ceiling-
    /// rounded. Shield: <c>ShieldPolicy.ChipFloorKPm</c> (100 -- a shield always spends). Block/parry
    /// (T5.3): <c>0</c> -- no pool to protect from non-spending, so a fully shredded proc removing
    /// nothing is a legitimate outcome, not a bug (spec-evasion-chain.md §2.1).</param>
    /// <param name="capKPm">Cap as a per-mille share of <paramref name="boundsBase"/>. Shield:
    /// <c>ShieldPolicy.PenCapKPm</c> (3000 -- penetration at best triples shield burn). Block/parry
    /// (T5.3): <c>950</c> -- mitigation may not reach total; same reasoning and same constant as
    /// <c>StatusPolicy.CategoryResistCap</c>, not shared with it (spec-evasion-chain.md §2.1).</param>
    public static long Apply(long deltaBase, long delta, long hitCount, long boundsBase, long floorKPm, long capKPm)
    {
        var raw = deltaBase + hitCount * delta;
        var floor = CeilDiv(floorKPm * boundsBase, 1000);
        var cap = capKPm * boundsBase / 1000;
        return Math.Clamp(raw, floor, cap);
    }

    static long CeilDiv(long num, long div) => (num + div - 1) / div;
}
