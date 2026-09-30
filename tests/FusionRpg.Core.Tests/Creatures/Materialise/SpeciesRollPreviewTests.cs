using FusionRpg.Core.Creatures.Materialise;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures.Materialise;

/// <summary>
/// `species-progression` SP0.2 — the preview is the same roll the materialiser performs, for one species
/// and one save's world seed, and it is never stored.
/// </summary>
public class SpeciesRollPreviewTests
{
    const int PinTheta = 20;
    const long CatalogRevision = 5;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Atoms = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AffixRow> Affixes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, ContainerRow> Containers = new(StringComparer.Ordinal);

    static SpeciesRollPreviewTests()
    {
        void AddAtom(string family, int amount)
        {
            var id = AtomRow.DeriveId(family, "", 1);
            Atoms[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Tier = 1,
                ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
            };
        }

        foreach (var (species, amount) in new[] { ("conezombie", 10), ("peashooter", 20) })
        {
            var family = $"atom.{species}-vitality";
            AddAtom(family, amount);
            var atomId = AtomRow.DeriveId(family, "", 1);
            Containers[$"species-passive.{species}"] = new ContainerRow
            {
                ContainerId = $"species-passive.{species}", Kind = ContainerKind.SpeciesPassive,
                Atoms = new[] { new ContainerAtomRow(1, atomId) },
            };
        }
    }

    static AtomRow? LookupAtom(string id) => Atoms.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => Affixes.TryGetValue(id, out var a) ? a : null;
    static ContainerRow? LookupContainer(string id) => Containers.TryGetValue(id, out var c) ? c : null;
    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    static SpeciesRollPreviewResult Preview(string speciesId, long worldSeed) =>
        SpeciesRollPreview.For(
            speciesId, LookupContainer, LookupAtom, LookupAffix, NoDomains,
            worldSeed, CatalogRevision, PinTheta, Tuning);

    [Fact]
    public void The_same_inputs_give_the_same_atoms()
    {
        var first = Preview("peashooter", worldSeed: 42);
        var second = Preview("peashooter", worldSeed: 42);

        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        Assert.Equal(first.Instance!.ContentFingerprint(), second.Instance!.ContentFingerprint());
    }

    [Fact]
    public void The_preview_is_the_same_roll_the_materialiser_performs()
    {
        // "calls the same per-species roll SpeciesMaterialiser performs" — proven by equality, not by
        // reading the source.
        var materialised = SpeciesMaterialiser.Materialise(
            new[] { "peashooter" }, LookupContainer, LookupAtom, LookupAffix, NoDomains,
            worldSeed: 42, catalogRevision: CatalogRevision, thetaContent: PinTheta, tuning: Tuning,
            out var rolls);
        Assert.True(materialised.IsOk);
        var preview = Preview("peashooter", worldSeed: 42);

        Assert.True(preview.IsOk);
        Assert.Equal(rolls[0].Instance.ContentFingerprint(), preview.Instance!.ContentFingerprint());
    }

    [Fact]
    public void Two_different_world_seeds_give_differing_previews()
    {
        var a = Preview("peashooter", worldSeed: 1);
        var b = Preview("peashooter", worldSeed: 2);

        Assert.True(a.IsOk);
        Assert.True(b.IsOk);
        Assert.NotEqual(a.Instance!.RollSeed, b.Instance!.RollSeed);
    }

    [Fact]
    public void A_species_with_no_container_is_refused_by_name_and_returns_no_instance()
    {
        var refusal = Preview("no-such-species", worldSeed: 1);

        Assert.False(refusal.IsOk);
        Assert.Equal("picks.source-not-materialised", refusal.RefusalCode);
        Assert.Null(refusal.Instance);
    }

    [Fact]
    public void The_preview_holds_no_state_between_calls()
    {
        // Calling it twice for two species, then again for the first, reproduces the first result: there
        // is no cache, no clock and no accumulated state (the never-stored half of the contract).
        var first = Preview("peashooter", worldSeed: 7);
        Preview("conezombie", worldSeed: 7);
        var again = Preview("peashooter", worldSeed: 7);

        Assert.Equal(first.Instance!.ContentFingerprint(), again.Instance!.ContentFingerprint());
    }

    // ---- restated from the retired PlayerMaterialiseTests (SP0.6 removed MaterialisePlayerSpecies) --

    /// <summary>
    /// seed-to-concrete Checkpoint 7's own closing line ("two players' rosters differ, and each
    /// player's own roster is stable across sessions"), restated on the delayed preview after
    /// `species-progression` SP0.6 retired the eager `MaterialisePlayerSpecies` this fact used to
    /// exercise. Imports the REAL `gk-data/packs/fusion/data/seed/effects/affixes/all.json` and the REAL
    /// `gk-data/packs/fusion/data/seed/creatures/species-effects/**` pilot batch through the SAME `AtomSeedFile.Collect`
    /// path a live server uses (never a hand-built row), then previews — never persists — each real
    /// species for two different world seeds. Reads through `FusionRpg.TestSupport.ContentRoot`
    /// (keepverse-split L3), not a hand-rolled directory walk.
    /// </summary>
    [Fact]
    public void Two_real_players_get_differing_previews_from_the_real_committed_species_effects_content()
    {
        var repoRoot = FusionRpg.TestSupport.ContentRoot.Path;
        var atomFiles = Directory.GetFiles(Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms"), "*.json")
            .Where(f => !Path.GetFileName(f).Equals("vocabulary.json", StringComparison.OrdinalIgnoreCase));
        var affixFiles = Directory.GetFiles(Path.Combine(KeepverseRoots.Content(), "data", "seed", "effects", "affixes"), "*.json");
        var speciesEffectFiles = Directory.GetFiles(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species-effects"), "*.json", SearchOption.AllDirectories);

        var files = atomFiles.Concat(affixFiles).Concat(speciesEffectFiles)
            .Select(f => (Path: f, Json: File.ReadAllText(f)));

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var atoms = collected.Content.Atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var affixes = collected.Content.Affixes.ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        var containers = collected.Content.Containers.ToDictionary(c => c.ContainerId, StringComparer.Ordinal);
        AtomRow? Atom(string id) => atoms.TryGetValue(id, out var a) ? a : null;
        AffixRow? Affix(string id) => affixes.TryGetValue(id, out var a) ? a : null;
        ContainerRow? Container(string id) => containers.TryGetValue(id, out var c) ? c : null;

        var pilotSpecies = new[] { "peashooter", "sunflower", "conezombie" }; // this pilot batch's own three
        Assert.All(pilotSpecies, id => Assert.NotNull(Container($"species-passive.{id}")));

        foreach (var speciesId in pilotSpecies)
        {
            var a = SpeciesRollPreview.For(speciesId, Container, Atom, Affix, NoDomains,
                1001, CatalogRevision, PinTheta, Tuning);
            var b = SpeciesRollPreview.For(speciesId, Container, Atom, Affix, NoDomains,
                2002, CatalogRevision, PinTheta, Tuning);

            Assert.True(a.IsOk, $"{speciesId}: {a.RefusalCode}");
            Assert.True(b.IsOk, $"{speciesId}: {b.RefusalCode}");
            // "differ": every one of the three real species rolls a different RollSeed for the two
            // world seeds (real, independent per-save world seeds — save-identity's own concern), the
            // same property the retired test proved against real persisted rows.
            Assert.NotEqual(a.Instance!.RollSeed, b.Instance!.RollSeed);
            // Real content, not the species' generic empty pool.
            Assert.NotEmpty(a.Instance!.Atoms);
        }
    }
}
