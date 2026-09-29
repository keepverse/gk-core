using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>D4.26 (spec-unique-pipeline.md §4): the rung-80 gate for the extend-action-slot grant.
/// Rung ≥ 90 carries the atom unconditionally (a fixed core, no roll spent); rung 80 draws this ONE
/// roll, on its own named stream so a hit never collides with any other draw off the same rollSeed —
/// the build then stays pure over (anchor, rollSeed). No clock, no <see cref="System.Random"/>.
///
/// <para><b>Per-MILLION, not per-mille.</b> <see cref="Effects.Atoms.IAtomRandom.NextPerMille"/> tops
/// out at 1000 and cannot express the tunable's 100-per-million rate (0.1 per mille) — confirmed by a
/// dedicated search finding zero existing per-million integer gate anywhere in this codebase; this is
/// the first one. Goes straight to <see cref="SeededRng"/> instead, the same RNG
/// <see cref="Effects.Atoms.AtomRandom"/> itself wraps.</para>
/// </summary>
public static class ExtendSlotRoll
{
    /// <summary>True on a hit. <paramref name="rollSeed"/> is <c>long</c>, matching every other
    /// `Instantiator`-adjacent roll in this codebase, and cast to <c>ulong</c> internally — the
    /// identical cast <c>Instantiator.cs</c>'s own <c>AtomRandom</c> construction already does, for
    /// the identical reason — rather than forcing every caller to already hold a <c>ulong</c>. The
    /// comparison widens the drawn <c>uint</c> up to <c>long</c> rather than narrowing
    /// <paramref name="chanceMicro"/> down, per this repo's own "widen, never cast down" rule.</summary>
    public static bool Hit(long rollSeed, long chanceMicro) =>
        (long)SeededRng.DeriveStream(unchecked((ulong)rollSeed), "unique:extend-slot").NextUInt(1_000_000) < chanceMicro;
}

/// <summary>The one shipped `stat.derived` → `loadout.slots` atom
/// (<c>gk-data/packs/fusion/data/seed/atoms/extend-slot.json</c>) — a structural grant, not one of the 144 unique anchors'
/// own family/variant/tier atoms, so its id is held here once rather than hand-typed at every call
/// site that needs to append or look it up.</summary>
public static class ExtendSlotAtom
{
    public static readonly string Id = Effects.Atoms.AtomRow.DeriveId("atom.extend-slot", "", 1);
}
