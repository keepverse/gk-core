using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T20: the production caller for <see cref="Encounter.Build"/>.
/// A real delve room anchor resolves through the real selector against the real corpus and real
/// tunings; refusal propagates; admission follows the decided delve rule. No population count is
/// asserted anywhere — corpus size is a reading that moves when content ships.
/// </summary>
[Collection(SpeciesCatalogSwapCollection.Name)]
public class DelveRoomEncounterTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly DelveBattleSessionManager _manager;

    public DelveRoomEncounterTests()
    {
        _testStore = DataTestStore.Create();
        _manager = new DelveBattleSessionManager(_testStore.Store);
        // Production boot parity (Program.cs:384): the catalog resolves from the store snapshot of
        // the real seed import — never the compiled default, which is stale for exactly the species
        // this path must admit. The import runs once per class run; Configure is cheap per test.
        CreatureSpeciesCatalog.Configure(RealSnapshot.Value);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string ReadTuning(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", name));

    /// <summary>Real seed import → snapshot defs, once: the same roster production configures
    /// (Program.cs:384 builds it from the store the species import filled). Pure data outlives the
    /// throwaway store (disposed inside); Configure runs per test. Unresolved anchors are skipped
    /// exactly like the import tool skips them — never imported, never admitted.</summary>
    static readonly Lazy<IReadOnlyList<CreatureSpeciesDef>> RealSnapshot = new(() =>
    {
        using var import = DataTestStore.Create();
        var threat = CreatureThreatTuningLoader.Parse(ReadTuning("creature-threat.v2.json"));
        var aptitudes = AptitudeTuningLoader.Parse(ReadTuning("aptitudes.v2.json"));
        var power = PowerTuningLoader.Parse(ReadTuning("power-scale.v2.json"));
        var shape = CreatureShapeTuningLoader.Parse(ReadTuning("creature-shape.v1.json"));
        var species = new List<ConcreteSpecies>();
        var speciesDir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        foreach (var file in Directory.GetFiles(speciesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            foreach (var anchor in AnchorRowReader.ReadAll(File.ReadAllText(file)))
            {
                if (SpeciesExpander.UnresolvedFields(anchor).Count > 0) continue;
                species.Add(SpeciesExpander.Expand(anchor, aptitudes, power, shape, threat));
            }
        }
        var outcome = import.Store.ImportSpecies(species);
        if (!outcome.IsOk)
            throw new InvalidOperationException(
                "real species import failed in fixture: " + string.Join("; ", outcome.Errors));
        var snapshot = import.Store.BuildCreatureSpeciesSnapshot();
        if (snapshot.Count == 0)
            throw new InvalidOperationException("real species import produced an empty snapshot");
        return snapshot;
    });

    sealed record RealInputs(
        IReadOnlyDictionary<string, EncounterAnchor> Encounters,
        IReadOnlyList<ConcreteAnchor> Corpus,
        EncounterTuning Tuning,
        CreatureThreatTuning Threat,
        RaidModeTuning Raid,
        DifficultyRungTuning Rung);

    static RealInputs LoadReal()
    {
        var threat = CreatureThreatTuningLoader.Parse(ReadTuning("creature-threat.v2.json"));
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        var tuning = EncounterTuningLoader.Parse(
            ReadTuning("encounter.v1.json"), registries,
            threat.Thresholds.Select(t => t.Id).ToList());
        var dungeon = DungeonTuningLoader.Parse(ReadTuning("dungeon.v3.json"), registries);
        var encounters = EncounterSeedFile.LoadAllById(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "encounters"), threat);
        var corpus = EncounterCorpusBuilder.Build(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species"),
            AptitudeTuningLoader.Parse(ReadTuning("aptitudes.v2.json")),
            PowerTuningLoader.Parse(ReadTuning("power-scale.v2.json")),
            CreatureShapeTuningLoader.Parse(ReadTuning("creature-shape.v1.json")),
            threat);
        return new RealInputs(encounters, corpus, tuning, threat,
            dungeon.RaidModes["solo"], dungeon.Rungs["hard"]);
    }

    [Fact]
    public void A_real_delve_room_resolves_through_Encounter_Build()
    {
        var real = LoadReal();

        var half = _manager.ResolveRoomEncounter(
            "encounter.party-mono-001", roomTheta: 30, climate: null,
            real.Raid, real.Rung, seed: 4242UL,
            real.Encounters, real.Corpus, real.Tuning, real.Threat);

        Assert.NotEmpty(half.Enemies);
        // Every emitted enemy is admitted under the decided delve rule — looked up (in catalog
        // canonical form, like the caller), not assumed.
        foreach (var enemy in half.Enemies)
            Assert.True(
                CreatureAdmission.ForDelve(CreatureSpeciesCatalog.Get(
                    (enemy.SpeciesId ?? "").Trim().ToLowerInvariant())),
                $"emitted species '{enemy.SpeciesId}' is not delve-admitted");
    }

    [Fact]
    public void Resolution_is_deterministic_for_a_given_seed()
    {
        var real = LoadReal();

        EncounterHalf First() => _manager.ResolveRoomEncounter(
            "encounter.party-mono-001", roomTheta: 30, climate: null,
            real.Raid, real.Rung, seed: 4242UL,
            real.Encounters, real.Corpus, real.Tuning, real.Threat);

        var a = First();
        var b = First();
        Assert.Equal(
            a.Enemies.Select(e => e.SpeciesId),
            b.Enemies.Select(e => e.SpeciesId));
    }

    [Fact]
    public void An_unknown_encounterRef_fails_by_name_never_as_an_empty_room()
    {
        var real = LoadReal();

        var ex = Assert.Throws<InvalidOperationException>(() => _manager.ResolveRoomEncounter(
            "encounter.no-such-room", roomTheta: 30, climate: null,
            real.Raid, real.Rung, seed: 1UL,
            real.Encounters, real.Corpus, real.Tuning, real.Threat));
        Assert.Contains("encounter.no-such-room", ex.Message);
    }

    [Fact]
    public void EventOnly_species_are_excluded_before_the_draw()
    {
        // Both anchors match the slot tuple identically — the ONLY difference is acquisition, so an
        // empty result for the refused species proves the admission filter, not the slot filter.
        // (The real corpus carries this same shape; synthetic anchors isolate it deterministically.)
        var threat = CreatureThreatTuningLoader.Parse(ReadTuning("creature-threat.v2.json"));
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        var tuning = EncounterTuningLoader.Parse(
            ReadTuning("encounter.v1.json"), registries,
            threat.Thresholds.Select(t => t.Id).ToList());
        var dungeon = DungeonTuningLoader.Parse(ReadTuning("dungeon.v3.json"), registries);

        static ConcreteAnchor Anchor(string speciesId, int gameTypeId) => new()
        {
            SpeciesId = speciesId,
            ThreatBand = "raider",
            ThreatRung = 4,
            AptitudePrimary = "Might",
            Reach = EncounterReach.Short,
            TargetPreference = TargetPreference.Frontline,
            ElementPrimary = ElementTypeId.Fire,
            GameTypeId = gameTypeId,
            Side = "plant",
            AttackIntervalMs = 1500,
            TraitPool = new[] { "sturdy" },
        };
        var corpus = new[] { Anchor("admit-me", 41), Anchor("refuse-me", 42) };
        var anchors = new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal)
        {
            ["encounter.test-synth-001"] = new EncounterAnchor(
                Formation.Pack,
                new[] { new EncounterSlot(SlotFilter.PostureOf("Might"), null, null, "lone") },
                new[] { 0 },
                ElementSpreadMode.Rainbow,
                new ThreatWindow(3, 5),
                BossSpeciesRef: null),
        };
        var defs = new[]
        {
            new CreatureSpeciesDef
            {
                SpeciesId = "admit-me", Acquisition = CreatureAcquisition.Summonable,
                Side = "plant", GameTypeId = 41,
                CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 41),
                ElementPrimary = ElementTypeId.Fire,
                Variants = Array.Empty<string>(), TraitPool = Array.Empty<string>(),
            },
            new CreatureSpeciesDef
            {
                SpeciesId = "refuse-me", Acquisition = CreatureAcquisition.EventOnly,
                Side = "plant", GameTypeId = 42,
                CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 42),
                ElementPrimary = ElementTypeId.Fire,
                Variants = Array.Empty<string>(), TraitPool = Array.Empty<string>(),
            },
        };

        using (CreatureSpeciesCatalog.UseScoped(defs))
        {
            var half = _manager.ResolveRoomEncounter(
                "encounter.test-synth-001", roomTheta: 30, climate: null,
                dungeon.RaidModes["solo"], dungeon.Rungs["hard"], seed: 7UL,
                anchors, corpus, tuning, threat);

            var emitted = half.Enemies.Select(e => e.SpeciesId).ToList();
            Assert.DoesNotContain("refuse-me", emitted);
            Assert.Contains("admit-me", emitted);
        }
    }

    [Fact]
    public void A_room_whose_admitted_pool_is_empty_refuses_loudly()
    {
        var threat = CreatureThreatTuningLoader.Parse(ReadTuning("creature-threat.v2.json"));
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        var tuning = EncounterTuningLoader.Parse(
            ReadTuning("encounter.v1.json"), registries,
            threat.Thresholds.Select(t => t.Id).ToList());
        var dungeon = DungeonTuningLoader.Parse(ReadTuning("dungeon.v3.json"), registries);

        var corpus = new[]
        {
            new ConcreteAnchor
            {
                SpeciesId = "refuse-me",
                ThreatBand = "raider",
                ThreatRung = 4,
                AptitudePrimary = "Might",
                Reach = EncounterReach.Short,
                TargetPreference = TargetPreference.Frontline,
                ElementPrimary = ElementTypeId.Fire,
                GameTypeId = 42,
                Side = "plant",
                AttackIntervalMs = 1500,
                TraitPool = new[] { "sturdy" },
            },
        };
        var anchors = new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal)
        {
            ["encounter.test-synth-002"] = new EncounterAnchor(
                Formation.Pack,
                new[] { new EncounterSlot(SlotFilter.PostureOf("Might"), null, null, "lone") },
                new[] { 0 },
                ElementSpreadMode.Rainbow,
                new ThreatWindow(3, 5),
                BossSpeciesRef: null),
        };
        var defs = new[]
        {
            new CreatureSpeciesDef
            {
                SpeciesId = "refuse-me", Acquisition = CreatureAcquisition.EventOnly,
                Side = "plant", GameTypeId = 42,
                CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 42),
                ElementPrimary = ElementTypeId.Fire,
                Variants = Array.Empty<string>(), TraitPool = Array.Empty<string>(),
            },
        };

        using (CreatureSpeciesCatalog.UseScoped(defs))
        {
            // Admission empties the pool before the draw — the slot filter then refuses naming its
            // tuple, never a default species. (countBand "lone" draws exactly one.)
            Assert.Throws<EncounterRefusal>(() => _manager.ResolveRoomEncounter(
                "encounter.test-synth-002", roomTheta: 30, climate: null,
                dungeon.RaidModes["solo"], dungeon.Rungs["hard"], seed: 7UL,
                anchors, corpus, tuning, threat));
        }
    }
}

