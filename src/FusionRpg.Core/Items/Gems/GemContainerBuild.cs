using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Uniques;

namespace FusionRpg.Core.Items.Gems;

/// <summary>
/// <c>ContainerRow.cs</c>'s own X7 doc comment: a <c>gem.*</c> container is "authored and lifecycle-
/// owned by `sockets-gen`... never authored outside a socket insert operation" — the same lazy,
/// build-at-use-time shape `UniqueContainerBuild.From` already proved out for uniques (D4.24: "a pure
/// function producing the `ContainerRow`; the instance is `TryInstantiate` on it — nothing else"),
/// scaled down to a gem's much smaller shape: ONE fixed atom, no variance pool, no rarity, no rung.
///
/// <para><b>Resolution, reusing the two real, already-shipped halves `UniqueContainerBuild` already
/// named:</b> `family` × `powerBand` → tier (<see cref="UniqueBudget.TierOfPowerBand"/> — a frozen
/// mirror of `bands.v1.json powerBand.tierMap`, not gem-specific, so this reuses it directly rather
/// than authoring a third private copy of the same five-band switch) → atom id
/// (<see cref="AtomRow.DeriveId"/>, passing the gem's own `element` as the variant where one is
/// authored — <c>UniqueContainerBuild</c> always passes `""` because no shipped unique fixed atom
/// carries an element; a gem regularly does, e.g. `gem.g1-001`'s `atom.elemental-power`+`fire`).</para>
///
/// <para><b>Most gems refuse today, and that is correctly a content gap, not a code defect</b> — the
/// same shape already proven for uniques (`unique-corpus-atom-family-gap`: 144 anchors name 68
/// families, only 28 are real, 4/144 buildable). Measured directly against the REAL production atom
/// catalog — `gk-data/packs/fusion/data/seed/items/affix-families/*.json` expanded through `FamilyExpansion.Expand`, the
/// exact pipeline `AtomImporter` runs at server boot (`Server/Program.cs:810`), NOT the separate,
/// much smaller `gk-data/packs/fusion/data/seed/atoms/generated/*.json` snapshot (only 3 partitions, never read at boot,
/// and not what this module resolves against): of the 60 shipped gem entries, 27 resolve as of
/// 2026-09-07 — the rest name a family the affix-family corpus does not carry yet, or a family/element
/// combination it never authored. <see cref="TryBuildOne"/> refuses each by name rather than guessing
/// a substitute atom.</para>
/// </summary>
public static class GemContainerBuild
{
    /// <summary>One gem seed's own <see cref="ContainerRow"/> lookup delegate — the atom catalog this
    /// module holds no copy of, matching <c>UniqueContainerLookups</c>'s identical idiom.</summary>
    public sealed record GemContainerLookups(Func<string, AtomRow?> LookupAtom);

    /// <summary>
    /// Build one gem's container, or refuse by name. Never throws on an unresolvable seed — a
    /// per-content-gap refusal, not an exceptional program state, since roughly 85% of the shipped
    /// corpus is expected to refuse until the generative pass widens (see class doc).
    /// </summary>
    public static ContainerRow? TryBuildOne(GemSeed seed, GemContainerLookups lookups, out string? refusalReason)
    {
        if (seed is null) throw new ArgumentNullException(nameof(seed));
        if (lookups is null) throw new ArgumentNullException(nameof(lookups));

        int tier;
        try { tier = UniqueBudget.TierOfPowerBand(seed.PowerBand); }
        catch (ArgumentOutOfRangeException)
        {
            refusalReason = $"'{seed.ContainerId}' names powerBand '{seed.PowerBand}', which is not one of bands.v1.json's five";
            return null;
        }

        var atomId = AtomRow.DeriveId(seed.Family, seed.Element ?? "", tier);
        if (lookups.LookupAtom(atomId) is null)
        {
            refusalReason = $"'{seed.ContainerId}' resolves to atom '{atomId}' (family '{seed.Family}', " +
                             $"element '{seed.Element ?? ""}', tier {tier}), which is not in the real generated atom catalog";
            return null;
        }

        refusalReason = null;
        return new ContainerRow
        {
            ContainerId = seed.ContainerId,
            Kind = ContainerKind.Gem,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
            PrefixRolls = 0,
            SuffixRolls = 0,
            Pool = Array.Empty<ContainerPoolRow>(),
        };
    }

    public readonly record struct Refusal(string SeedId, string Reason);
    public sealed record BuildReport(IReadOnlyList<ContainerRow> Built, IReadOnlyList<Refusal> Refused);

    /// <summary>Build every gem the real catalog can support right now, and name every one it can't —
    /// the corpus-wide measurement `unique-corpus-atom-family-gap`'s own report shape already proved
    /// useful for exactly this situation.</summary>
    public static BuildReport BuildAll(IReadOnlyList<GemSeed> seeds, GemContainerLookups lookups)
    {
        var built = new List<ContainerRow>();
        var refused = new List<Refusal>();
        foreach (var seed in seeds)
        {
            var container = TryBuildOne(seed, lookups, out var reason);
            if (container is not null) built.Add(container);
            else refused.Add(new Refusal(seed.ContainerId, reason!));
        }
        return new BuildReport(built, refused);
    }
}
