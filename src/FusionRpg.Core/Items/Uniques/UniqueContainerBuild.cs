using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Thresholds;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>Everything <see cref="UniqueContainerBuild.From"/> needs beyond the seed itself — the real
/// atom catalog this module holds no copy of (the "read model owned elsewhere" idiom this whole
/// program already uses everywhere else). <see cref="AtomsInFamily"/> exists because nothing in the
/// codebase indexes atoms by family today (confirmed by direct search) — the caller, who has the real
/// catalog loaded, is the only one who can answer it.</summary>
public sealed record UniqueContainerLookups(
    Func<string, AtomRow?> LookupAtom,
    Func<string, IReadOnlyList<AtomRow>> AtomsInFamily);

/// <summary>
/// D4.24 (spec-unique-pipeline.md §2) — "`UniqueContainerBuild.From(anchor, rollSeed, tuning, lookups)`
/// is a pure function producing the `ContainerRow`; the instance is `TryInstantiate` on it — nothing
/// else." This build takes <paramref name="rung"/> instead of a raw `rollSeed`/`tuning` pair: the seed
/// contract forbids a seed from authoring numbers, and the ONE roll a container's OWN affix pool ever
/// takes happens later, inside `TryInstantiate` itself — only <see cref="UniqueBudget.ReferenceTier"/>
/// needs the rung's own tier window, so that is the one live fact the ORIGINAL D4.24 half of this
/// function reads.
///
/// <para><b>D4.26 changes what "pure over" means here, honestly:</b> the extend-action-slot grant
/// (spec §4) is carried by rung ≥ 90 unconditionally, and DRAWN at rung 80 — a hit is decided from the
/// container's own eventual <paramref name="rollSeed"/> on a dedicated stream
/// (<see cref="ExtendSlotRoll.Hit"/>), BEFORE `TryInstantiate` ever runs, so the fixed core itself can
/// now depend on the roll seed at rung 80 specifically. The build stays pure over `(anchor, rung,
/// rungOrdinal, rollSeed, extendSlotChanceMicro)` as a whole — deterministic, reproducible, no
/// `System.Random` — just no longer independent of `rollSeed` the way the class doc above originally
/// promised.</para>
///
/// <para><b>Fixed core</b> = <c>fixedAtoms[]</c>, one <see cref="ContainerAtomRow"/> each: `family ×
/// powerBand` → tier (<see cref="UniqueBudget.TierOfPowerBand"/>) → atom id
/// (<see cref="AtomRow.DeriveId"/>) — the two real, already-shipped halves confirmed by direct search;
/// composing them is this function's own new, small work (spec §2, verbatim).</para>
///
/// <para><b>Pool = the variance slot</b> (spec §2: "`PrefixRolls + SuffixRolls = 1` (or 0), `MinTier ==
/// MaxTier` at the authored tier, 3-6 `ContainerPoolRow`s from the affix library"). <b>Confirmed by a
/// dedicated search: no existing code resolves "a family → N weighted pool candidates" anywhere in
/// this codebase</b> — the shared affix generator wraps EVERY atom in the catalog 1:1
/// (<see cref="AffixLibraryGenerator.Generate"/>) with no family filter, and the one sibling
/// container-with-a-pool build (`EliteAffix.Apply`, encounter-generator) takes its own pool as an
/// ALREADY-FILTERED parameter rather than deriving one — this is genuinely new work, not a mirror.
/// "The authored tier" is <see cref="UniqueBudget.ReferenceTier"/> (already shipped, used identically
/// for pricing: "at the rung's reference tier", `UniqueValidator.cs`'s own `UniquePricing` doc comment)
/// — the rung's own tier window midpoint, rounded down. <b>Every atom in the variance family AT that
/// tier becomes one pool candidate</b> (equal weight — nothing in the spec or the shipped tuning names
/// a per-candidate weighting rule for this pool specifically), wrapped as a single-atom affix via
/// <see cref="AffixLibraryGenerator.SingleAtomAffix"/> — never re-deriving that wrap.</para>
///
/// <para><b>The one roll's own side (prefix vs. suffix), named explicitly:</b> a container's `PrefixRolls`/
/// `SuffixRolls` are budgets `Instantiator`'s own `Draw` spends against affixes of the MATCHING
/// <see cref="AffixClass"/> only (`Instantiator.cs`'s `DrawBudget` calls, confirmed by direct read) —
/// class is derived from whether the underlying atom carries a trigger
/// (<see cref="AffixValidator.AffixClassOfAtom"/>), never authored. This build reads the FIRST
/// resolved candidate's own class and spends the container's one roll on that side; every OTHER
/// candidate of the SAME class joins the pool, and a candidate of the OTHER class is dropped from the
/// pool with a report line — it would sit in `Pool` unreachable by the container's own single roll
/// budget otherwise, which is strictly worse than a smaller, fully-reachable pool.</para>
/// </summary>
public static class UniqueContainerBuild
{
    /// <summary>One dropped variance candidate, named so a caller can see the corpus is under-using a
    /// family rather than silently shrinking it — never a hard refusal (the fixed core and the rest of
    /// the pool are still perfectly valid), matching <see cref="UniqueCorpusReport"/>'s own "measure
    /// and say so" posture rather than <see cref="UniqueCorpusValidator"/>'s "refuse".</summary>
    public readonly record struct DroppedCandidate(string SeedId, string AtomId, string Reason);

    public sealed record BuildResult(ContainerRow Container, IReadOnlyList<DroppedCandidate> Dropped);

    /// <param name="rungOrdinal">The rarity ladder ordinal <paramref name="rung"/>'s own `RarityId`
    /// resolves to — a separate, caller-supplied value rather than a field on `RarityRungWindow`
    /// itself (that struct is deliberately narrow, shared with the rarity-overlap harness), matching
    /// the identical `(RarityRungWindow rung, int rungOrdinal)` pair `UniqueValidator.Validate`
    /// already takes for the same reason.</param>
    /// <param name="rollSeed">The container's own eventual instance roll seed — read ONLY to decide
    /// the rung-80 extend-slot draw (<see cref="ExtendSlotRoll.Hit"/>); never used to roll anything
    /// else here (D4.26; see this class's own doc comment above).</param>
    /// <param name="extendSlotChanceMicro">`loot.extendSlotChanceMicro` (`DungeonTuning`), per-million —
    /// the same key the normal-drop arm reads (spec §4). Not read from a tuning object directly: this
    /// module has no dependency on `Dungeon.Tuning`, matching every other primitive this function
    /// already takes rather than a whole tuning record.</param>
    public static BuildResult From(UniqueSeed anchor, RarityRungWindow rung, int rungOrdinal, long rollSeed,
        long extendSlotChanceMicro, UniqueContainerLookups lookups)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (lookups is null) throw new ArgumentNullException(nameof(lookups));
        if (!string.Equals(rung.RarityId, anchor.RarityId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"unique '{anchor.SeedId}' authors rarity '{anchor.RarityId}' but the supplied window is for '{rung.RarityId}'",
                nameof(rung));

        var atoms = new List<ContainerAtomRow>();
        var seq = 0;
        foreach (var fa in anchor.FixedAtoms)
        {
            var tier = UniqueBudget.TierOfPowerBand(fa.PowerBand);
            var atomId = AtomRow.DeriveId(fa.Family, "", tier);
            if (lookups.LookupAtom(atomId) is null)
                throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed,
                    $"unique '{anchor.SeedId}' fixed atom '{atomId}' (family '{fa.Family}', band '{fa.PowerBand}') is not in the atom catalog");
            atoms.Add(new ContainerAtomRow(seq++, atomId));
        }

        // D4.26 (spec-unique-pipeline.md §4): rung >= 90 carries the extend-slot atom unconditionally
        // (no roll spent); rung 80 draws exactly one roll on its own named stream. Appended AFTER the
        // authored fixed core and never counted against anchor.FixedAtoms itself — this atom is the
        // rung's own grant, not the seed's.
        if (rungOrdinal >= 90 || (rungOrdinal >= 80 && ExtendSlotRoll.Hit(rollSeed, extendSlotChanceMicro)))
        {
            if (lookups.LookupAtom(ExtendSlotAtom.Id) is null)
                throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed,
                    $"unique '{anchor.SeedId}' would carry the extend-slot atom '{ExtendSlotAtom.Id}' but it is not in the atom catalog");
            atoms.Add(new ContainerAtomRow(seq++, ExtendSlotAtom.Id));
        }

        var pool = new List<ContainerPoolRow>();
        var dropped = new List<DroppedCandidate>();
        int? minTier = null, maxTier = null;
        var prefixRolls = 0;
        var suffixRolls = 0;

        if (anchor.VarianceSlot is { } variance)
        {
            var tier = UniqueBudget.ReferenceTier(rung);
            var candidates = lookups.AtomsInFamily(variance.Family).Where(a => a.Tier == tier).ToList();
            if (candidates.Count == 0)
                throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed,
                    $"unique '{anchor.SeedId}' variance family '{variance.Family}' has no atom at reference tier {tier}");

            var rollClass = AffixValidator.AffixClassOfAtom(candidates[0]);
            if (rollClass == AffixClass.Prefix) prefixRolls = 1; else suffixRolls = 1;

            foreach (var atom in candidates)
            {
                if (AffixValidator.AffixClassOfAtom(atom) != rollClass)
                {
                    dropped.Add(new DroppedCandidate(anchor.SeedId, atom.AtomId,
                        $"family '{variance.Family}' mixes {rollClass} and {AffixValidator.AffixClassOfAtom(atom)} atoms; " +
                        $"the container's one roll is {rollClass}-side, so this candidate would never be reachable"));
                    continue;
                }
                pool.Add(new ContainerPoolRow(AffixLibraryGenerator.SingleAtomAffix(atom).AffixId, Weight: 1));
            }

            minTier = tier;
            maxTier = tier;
        }

        var container = new ContainerRow
        {
            ContainerId = anchor.ContainerId,
            Kind = ContainerKind.Item,
            Rarity = anchor.RarityId,
            // D3.15/D4.12/D3.11's real remaining blocker, closed here: a unique's own authored frame/
            // base-type pair, carried forward the identical way Rarity already is (see ContainerRow.
            // Frame's own doc comment) -- the SAME shared helper UniqueCorpusValidator already uses for
            // this exact ItemFrame -> wire-id conversion, not a second, drifting inline ternary.
            Frame = FrameMixPredicate.BucketOf(anchor.Frame),
            BaseTypeId = anchor.BaseTypeId,
            MinTier = minTier,
            MaxTier = maxTier,
            PrefixRolls = prefixRolls,
            SuffixRolls = suffixRolls,
            Atoms = atoms,
            Pool = pool,
        };
        return new BuildResult(container, dropped);
    }
}
