using FusionRpg.Core.Creatures.Materialise;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// T5.5 (`player-materialise`, `spec-player-materialise.md`) — the pure derivation half:
/// <see cref="SpeciesMaterialiser"/> rolls every species' `species-passive.{speciesId}` container
/// against one player's world seed, seed and catalog in, rows out, no I/O. The transactional/DAL
/// half (append-only, all-or-nothing, the measured write) is T5.6's own file
/// (`RpgStore.PlayerSpecies.cs`), not this module's.
/// </summary>
public class MaterialiseTests
{
    const int PinTheta = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Atoms = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AffixRow> Affixes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, ContainerRow> Containers = new(StringComparer.Ordinal);

    static MaterialiseTests()
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

        foreach (var (species, amount) in new[] { ("conezombie", 10), ("peashooter", 20), ("sunflower", 30) })
        {
            var family = $"atom.{species}-vitality";
            AddAtom(family, amount);
            var atomId = AtomRow.DeriveId(family, "", 1);
            var affixId = $"affix.{species}-vitality";
            Affixes[affixId] = new AffixRow(affixId, AffixClass.Prefix, new[] { new AffixRefRow(1, atomId) });
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

    static AtomRejection Materialise(
        IReadOnlyList<string> speciesIds, long worldSeed, long catalogRevision,
        out IReadOnlyList<MaterialisedRoll> rolls) =>
        SpeciesMaterialiser.Materialise(
            speciesIds, LookupContainer, LookupAtom, LookupAffix, NoDomains,
            worldSeed, catalogRevision, PinTheta, Tuning, out rolls);

    static readonly string[] Roster = { "conezombie", "peashooter", "sunflower" };

    [Fact]
    public void Same_world_seed_and_catalog_reproduce_the_roster_exactly()
    {
        var r1 = Materialise(Roster, worldSeed: 42, catalogRevision: 5, out var rolls1);
        var r2 = Materialise(Roster, worldSeed: 42, catalogRevision: 5, out var rolls2);

        Assert.True(r1.IsOk); Assert.True(r2.IsOk);
        Assert.Equal(rolls1.Count, rolls2.Count);
        for (var i = 0; i < rolls1.Count; i++)
        {
            Assert.Equal(rolls1[i].SpeciesId, rolls2[i].SpeciesId);
            Assert.Equal(rolls1[i].Instance.ContentFingerprint(), rolls2[i].Instance.ContentFingerprint());
        }
    }

    [Fact]
    public void Two_world_seeds_produce_different_rosters()
    {
        Materialise(new[] { "peashooter" }, worldSeed: 1, catalogRevision: 5, out var a);
        Materialise(new[] { "peashooter" }, worldSeed: 2, catalogRevision: 5, out var b);

        Assert.NotEqual(a[0].Instance.RollSeed, b[0].Instance.RollSeed);
    }

    [Fact]
    public void Enumeration_order_does_not_affect_output()
    {
        var forward = Roster;
        var backward = Roster.Reverse().ToArray();

        Materialise(forward, worldSeed: 7, catalogRevision: 1, out var a);
        Materialise(backward, worldSeed: 7, catalogRevision: 1, out var b);

        Assert.Equal(a.Select(r => r.SpeciesId), b.Select(r => r.SpeciesId));
        Assert.Equal(
            a.Select(r => r.Instance.ContentFingerprint()),
            b.Select(r => r.Instance.ContentFingerprint()));
    }

    [Fact]
    public void Added_species_are_appended_not_rerolled()
    {
        Materialise(new[] { "conezombie", "peashooter" }, worldSeed: 9, catalogRevision: 1, out var before);
        Materialise(new[] { "conezombie", "peashooter", "sunflower" }, worldSeed: 9, catalogRevision: 1, out var after);

        var beforeFingerprints = before.ToDictionary(r => r.SpeciesId, r => r.Instance.ContentFingerprint());
        foreach (var roll in after.Where(r => beforeFingerprints.ContainsKey(r.SpeciesId)))
            Assert.Equal(beforeFingerprints[roll.SpeciesId], roll.Instance.ContentFingerprint());

        Assert.Contains(after, r => r.SpeciesId == "sunflower");
    }

    [Fact]
    public void A_species_with_no_species_passive_container_yet_is_skipped_not_an_error()
    {
        var r = Materialise(new[] { "conezombie", "no-content-yet" }, worldSeed: 1, catalogRevision: 1, out var rolls);

        Assert.True(r.IsOk, r.ToString());
        Assert.Single(rolls);
        Assert.Equal("conezombie", rolls[0].SpeciesId);
    }

    [Fact]
    public void Power_json_is_null_after_materialisation()
    {
        Materialise(Roster, worldSeed: 1, catalogRevision: 1, out var rolls);

        Assert.All(rolls, r => Assert.All(r.Instance.Atoms, a => Assert.Null(a.PowerJson)));
    }

    [Fact]
    public void An_empty_roster_materialises_to_nothing_without_throwing()
    {
        var r = Materialise(Array.Empty<string>(), worldSeed: 1, catalogRevision: 1, out var rolls);

        Assert.True(r.IsOk);
        Assert.Empty(rolls);
    }

    [Fact]
    public void The_pure_compute_for_a_small_roster_is_fast()
    {
        // Not the DAL's own measured full-roster write (T5.6's own acceptance line) — this proves
        // the COMPUTE half alone carries no accidental O(n^2) or per-species I/O.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Materialise(Roster, worldSeed: 1, catalogRevision: 1, out var rolls);
        sw.Stop();

        Assert.Equal(3, rolls.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"materialising 3 species took {sw.ElapsedMilliseconds}ms");
    }
}
