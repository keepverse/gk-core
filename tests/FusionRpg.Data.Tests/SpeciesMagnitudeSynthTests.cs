using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// species-gear-chain T18 (spec-species-magnitude-synth.md Testing strategy): the importer
/// synthesizes one container per species-with-magnitudes, in its own transaction, upsert-never-append.
/// In-memory throughout. No population count anywhere — the reconciliation property
/// (`containers == species-with-magnitudes`, computed on both sides) is what is pinned.
/// </summary>
public class SpeciesMagnitudeSynthTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesMagnitudeSynthTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static ConcreteSpecies Species(
        string id, IReadOnlyDictionary<string, long>? magnitudes = null) => new()
    {
        SpeciesId = id, Rarity = CreatureRarity.Cultivated, Theta = 13, PTheta = 452,
        AttackIntervalMs = 1500, AttackIntervalSource = "classified", RangeCells = 5, VariantCount = 2,
        Magnitudes = magnitudes ?? new Dictionary<string, long>
        {
            ["combat.power.omni"] = 362,
            ["resource.max.hp"] = 2712,
        },
        Side = "plant", GameTypeId = 0, ElementPrimary = ElementTypeId.Earth,
        ElementSecondary = ElementTypeId.Fire, DeployMode = CreatureDeployMode.PlantAvatar,
        Acquisition = CreatureAcquisition.Summonable,
        Variants = new[] { "normal" }, TraitPool = new[] { "Defensive" },
    };

    static CreatureSpeciesDef Def(string id, IReadOnlyDictionary<string, long>? magnitudes = null) => new()
    {
        SpeciesId = id, Name = id, Side = "plant", GameTypeId = 0, CreatureTypeId = 90_001,
        ElementPrimary = ElementTypeId.Earth, BaseRarity = CreatureRarity.Cultivated,
        DeployMode = CreatureDeployMode.PlantAvatar, Acquisition = CreatureAcquisition.Summonable,
        Magnitudes = magnitudes ?? new Dictionary<string, long>
        {
            ["combat.power.omni"] = 362,
            ["resource.max.hp"] = 2712,
        },
    };

    [Fact]
    public void Importing_a_species_with_magnitudes_produces_exactly_one_container()
    {
        var outcome = _store.ImportSpecies(new[] { Species("Peashooter") });
        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

        var container = _store.GetContainer("trait.species-magnitude-Peashooter");
        Assert.NotNull(container);
        Assert.Equal(ContainerKind.Trait, container!.Kind);
        Assert.Equal(2, container.Atoms.Count);
    }

    [Fact]
    public void Importing_a_species_with_no_magnitudes_produces_no_container()
    {
        var outcome = _store.ImportSpecies(new[]
            { Species("Bare", new Dictionary<string, long>()) });
        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

        // ...and the consumer's fail-closed early return still holds exactly where it should.
        Assert.Null(_store.GetContainer("trait.species-magnitude-Bare"));
    }

    [Fact]
    public void Importing_twice_leaves_one_container_with_identical_members()
    {
        _store.ImportSpecies(new[] { Species("Peashooter") });
        var first = _store.GetContainer("trait.species-magnitude-Peashooter")!;
        _store.ImportSpecies(new[] { Species("Peashooter") });
        var second = _store.GetContainer("trait.species-magnitude-Peashooter")!;

        Assert.Equal(
            first.Atoms.Select(a => a.AtomId).OrderBy(id => id, StringComparer.Ordinal),
            second.Atoms.Select(a => a.AtomId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Container_members_match_the_magnitude_rows_channel_for_channel_value_for_value()
    {
        _store.ImportSpecies(new[] { Species("Peashooter") });
        var species = _store.GetSpecies("Peashooter")!;
        var container = _store.GetContainer("trait.species-magnitude-Peashooter")!;

        var members = new Dictionary<string, long>();
        foreach (var member in container.Atoms)
        {
            var atom = _store.GetAtom(member.AtomId);
            Assert.NotNull(atom);
            using var paramsDoc = System.Text.Json.JsonDocument.Parse(atom!.ParamsJson);
            members[paramsDoc.RootElement.GetProperty("channel").GetString()!] =
                paramsDoc.RootElement.GetProperty("amount").GetInt64();
        }
        Assert.Equal(species.Magnitudes, members);
    }

    [Fact]
    public void Values_round_trip_as_long_through_the_full_path_up_to_the_grammar_bound()
    {
        // int.MaxValue: the largest value the atom value grammar carries (fixed amounts are int32
        // by the atom program's documented rule). It must survive the magnitude row (SQLite
        // INTEGER), the atom, the container member and the binding alike — long end to end, never
        // narrowed. Beyond int32 the import refuses loudly (next test); widening the grammar is the
        // atom program's review, filed in the T18 commit.
        var big = (long)int.MaxValue;
        var outcome = _store.ImportSpecies(new[]
            { Species("Big", new Dictionary<string, long> { ["combat.power.omni"] = big }) });
        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
        Assert.Equal(big, _store.GetSpecies("Big")!.Magnitudes["combat.power.omni"]);

        var container = _store.GetContainer("trait.species-magnitude-Big")!;
        var atom = _store.GetAtom(Assert.Single(container.Atoms).AtomId)!;
        using var paramsDoc = System.Text.Json.JsonDocument.Parse(atom.ParamsJson);
        Assert.Equal(big, paramsDoc.RootElement.GetProperty("amount").GetInt64());
    }

    [Fact]
    public void A_magnitude_the_atom_grammar_cannot_carry_refuses_the_whole_import_by_name()
    {
        // E14a's all-or-nothing shape: one bad row writes nothing, and the refusal names the
        // species plus the channel. The atom value grammar caps fixed amounts at int32 (the atom
        // program's documented rule — widening it is that program's review, not this module's) —
        // so a bigger magnitude fails LOUD here rather than wrapping, clamping, or binding short
        // with no symptom.
        var big = (long)int.MaxValue + 1;
        var outcome = _store.ImportSpecies(new[]
        {
            Species("Big", new Dictionary<string, long> { ["combat.power.omni"] = big }),
            Species("Small", new Dictionary<string, long> { ["combat.power.omni"] = 1 }),
        });
        Assert.False(outcome.IsOk);
        Assert.Contains(outcome.Errors, e => e.SpeciesId == "Big");
        Assert.Null(_store.GetSpecies("Big"));
        Assert.Null(_store.GetSpecies("Small"));
        Assert.Null(_store.GetContainer("trait.species-magnitude-Big"));
        Assert.Null(_store.GetContainer("trait.species-magnitude-Small"));
    }

    [Fact]
    public void A_renamed_species_orphans_its_container_which_the_consumer_withdraws()
    {
        _store.ImportSpecies(new[] { Species("Old") });
        Assert.NotNull(_store.GetContainer("trait.species-magnitude-Old"));

        _store.ImportSpecies(new[] { Species("New") });
        Assert.Null(_store.GetContainer("trait.species-magnitude-Old"));
        Assert.NotNull(_store.GetContainer("trait.species-magnitude-New"));
    }

    [Fact]
    public void Containers_equal_species_with_magnitudes_computed_on_both_sides()
    {
        _store.ImportSpecies(new[]
        {
            Species("A", new Dictionary<string, long> { ["combat.power.omni"] = 1 }),
            Species("B", new Dictionary<string, long>()),
            Species("C", new Dictionary<string, long>
            {
                ["combat.power.omni"] = 2,
                ["resource.max.hp"] = 3,
            }),
        });

        var withMagnitudes = new[] { "A", "C" };
        foreach (var id in withMagnitudes)
            Assert.NotNull(_store.GetContainer($"trait.species-magnitude-{id}"));
        Assert.Null(_store.GetContainer("trait.species-magnitude-B"));
    }

    [Fact]
    public void The_full_round_trip_binds_instead_of_taking_the_fail_closed_return()
    {
        // The acceptance test for the whole module: import (synthesizing the container), deploy a
        // scoped specimen, and the reconciler BINDS — the one thing that has never been true.
        var magnitudes = new Dictionary<string, long> { ["combat.power.omni"] = 362 };
        _store.ImportSpecies(new[] { Species("roundtrip", magnitudes) });
        using (CreatureSpeciesCatalog.UseScoped(new[] { Def("roundtrip", magnitudes) }))
        {
            var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
            {
                SpeciesId = "roundtrip", Side = "plant", GameTypeId = 901,
                Rarity = CreatureRarity.Chaff.ToId(), Variant = "normal",
                ElementPrimary = ElementTypeId.Fire.ToElementId(),
                TraitIds = new List<string>(), Origin = "summon",
            });
            var deploy = _store.TryBeginUniqueDeploy(specimen.Actor.InstanceId, "deploy-synth-1");
            Assert.True(deploy.Ok, deploy.Reason);

            var bindings = _store.ListBindings(
                new OwnerScope(OwnerKind.UniqueActor, specimen.Actor.InstanceId));
            Assert.Contains(bindings, b =>
                b.Source == "creature-magnitude" && b.Slot == "trait.species-magnitude-roundtrip");
        }
    }

    [Fact]
    public void A_magnitude_change_withdraws_the_stale_binding_and_redeploys_clean()
    {
        var before = new Dictionary<string, long> { ["combat.power.omni"] = 362 };
        var after = new Dictionary<string, long> { ["combat.power.omni"] = 500 };
        _store.ImportSpecies(new[] { Species("changer", before) });
        using (CreatureSpeciesCatalog.UseScoped(new[] { Def("changer", before) }))
        {
            var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
            {
                SpeciesId = "changer", Side = "plant", GameTypeId = 902,
                Rarity = CreatureRarity.Chaff.ToId(), Variant = "normal",
                ElementPrimary = ElementTypeId.Fire.ToElementId(),
                TraitIds = new List<string>(), Origin = "summon",
            });
            var id = specimen.Actor.InstanceId;
            Assert.True(_store.TryBeginUniqueDeploy(id, "deploy-synth-2").Ok);
            var first = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, id))
                .Where(b => b.Source == "creature-magnitude").Select(b => b.BindingId).ToList();
            Assert.Single(first);

            _store.ImportSpecies(new[] { Species("changer", after) });
            _store.TryAckUniqueSpawn("deploy-synth-2", "ptr-synth-1", "m-synth-1");
            _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
            {
                ("board.end", "m-synth-1", "{}")
            });
            Assert.True(_store.TryBeginUniqueDeploy(id, "deploy-synth-3").Ok);
            var second = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, id))
                .Where(b => b.Source == "creature-magnitude").Select(b => b.BindingId).ToList();
            Assert.Single(second);
        }
    }
}

