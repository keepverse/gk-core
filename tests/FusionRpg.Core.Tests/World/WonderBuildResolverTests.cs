using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Siege;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Task 2.4 (`wonder-build-flow` Core-side, spec-wonder-build-flow.md §Design 1-6):
/// `WorldCommand.RelicInstanceIds`, `StructureDef.RelicCost` + `Validate` pairing, the
/// admission-time count/dup check, `WonderExistenceScan.CountExisting`, and the
/// `BuildResolver.Run` extension (cap check + the pre-existing rubble/ironwork wiring-gap
/// fix). Split per the plan: Part A = materials-fix + cap-check + corpus RelicCost parsing;
/// Part B = admission relic count/dup check. Both live in this one suite, grouped by region.
/// </summary>
public class WonderBuildResolverTests
{
    // ---- fixture helpers -----------------------------------------------------

    static WorldSector Sector(string id, string owner, params WorldSlot[] slots) => new()
    {
        SectorId = id, TypeId = "stable", OwnerFactionId = owner, Slots = slots,
    };

    static WorldSlot Wildland(int index, string? structureId = null, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = index, SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId,
        StructureId = structureId, ConstructionTurnsRemaining = constructionTurnsRemaining,
    };

    static WorldSlot Rootbed(int index, string? structureId = null) => new()
    {
        SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, StructureId = structureId,
    };

    static WorldEntity Founder(string sectorId, long carriedLoam = 0) => new()
    {
        EntityId = "legion", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave",
        AtSectorId = sectorId, CarriedLoam = carriedLoam,
        Members = new[] { new WorldEntityMember { SpeciesId = "grunt" } },
    };

    static WorldState WorldWith(string sectorId, WorldSector sector, WorldEntity entity) => new()
    {
        Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[] { sector },
        Entities = new[] { entity },
    };

    static WorldCommand Build(string commandId, string sectorId, string structureId,
        int slotIndex = 0, params string[] relicIds) => new()
    {
        CommanderId = "dave", CommandId = commandId, Kind = WorldCommandKinds.Build,
        EntityId = "legion", SectorId = sectorId, SlotIndex = slotIndex,
        StructureId = structureId, RelicInstanceIds = relicIds,
    };

    static string RealCorpusRoot() => Path.Combine(ContentRoot.Path, "data", "seed", "structures");

    static void RestoreRealCorpus() => StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));

    /// <summary>One corpus file with exactly the given entry rows. Mirrors
    /// WonderCatalogTests.Corpus — same committed shape, no hand-edited seed data, no temp file.</summary>
    /// <summary>The rows the JSON document below always held, as an **in-memory** corpus: the corpus is
    /// data, so a fixture that needs its own rows does not write a corpus file to `%TEMP%`
    /// (testing-standard R1 — the `temp-corpus-write` rule refuses the write). `StructureCorpus.FromJson`
    /// is the file format's own in-memory entrance: one parser, so a literal and a committed file cannot
    /// drift apart.</summary>
    static StructureCorpus Corpus(params string[] rows) =>
        StructureCorpus.FromJson("""
        {
          "kind": "structure-anchor",
          "_meta": {"partition": "Store"},
          "entries": [
        """ + string.Join(",\n", rows) + """
          ]
        }
        """);

    /// <summary>A minimal catalog row. wonderScope/wonderRarity/relicCost/wonderEffects are emitted
    /// only when non-null, so ordinary rows prove the genuinely-optional parse.</summary>
    static string RowJson(
        string id, string structureKind, string requiredSlotKind,
        long cost = 0, long rubble = 0, long ironwork = 0,
        string? wonderScope = null, string? wonderRarity = null, long? relicCost = null,
        string? effectsJson = null)
    {
        var extra = "";
        if (wonderScope is not null)
            extra += ", \"wonderScope\": \"" + wonderScope + "\", \"wonderRarity\": \"" + wonderRarity + "\"";
        if (relicCost is not null) extra += ", \"relicCost\": " + relicCost;
        if (effectsJson is not null) extra += ", \"wonderEffects\": [" + effectsJson + "]";
        return "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\","
            + " \"anchor\": { \"structureId\": \"" + id + "\", \"family\": \"test\","
            + " \"role\": \"Extract\", \"roleSecondary\": \"none\","
            + " \"requiredSlotKind\": \"" + requiredSlotKind + "\","
            + " \"elementPrimary\": \"none\", \"elementSecondary\": \"none\","
            + " \"tempo\": \"none\", \"reach\": \"melee\", \"strengthBand\": \"rubble\", \"rarity\": \"sprout\","
            + " \"traits\": [], \"costProfile\": \"cheap\", \"targetPreference\": \"none\", \"variants\": [],"
            + " \"acquisitionPaths\": [\"built\"], \"footprint\": \"one-cell\", \"coverTier\": \"none\","
            + " \"controlPoint\": true, \"obstacleVerbs\": [], \"reason\": \"test\" },"
            + " \"_provenance\": {\"source\": \"AUTHORED\", \"citation\": \"test\"},"
            + " \"magnitudes\": { \"structureKind\": \"" + structureKind + "\", \"cost\": " + cost + ","
            + " \"yieldMultiplierMilli\": 1000, \"buildTurns\": 0, \"capacityBonus\": 0,"
            + " \"flatYieldPerTurn\": 0,"
            + " \"constructRubbleCost\": " + rubble + ", \"constructIronworkCost\": " + ironwork + ","
            + " \"materialTier\": 0, \"blocksMovement\": false, \"blocksLineOfFire\": false,"
            + " \"obstacleKind\": \"None\", \"coverPowerMilli\": 0, \"coverRadius\": 0,"
            + " \"entryStaminaMultiplierMilli\": 1000, \"visionRangeTiles\": null" + extra + " } }";
    }

    static string WonderEffect(string scope, long valueMilli = 500) =>
        $$"""{ "kind": "LoamGenerationRate", "scope": "{{scope}}", "valueMilli": {{valueMilli}} }""";

    static WonderTuning CapTuning(long sectorCap, long empireCap) => new(
        SchemaVersion: 1, Version: 1,
        UniqueExistenceCap: new WonderUniqueExistenceCapTuning(Sector: sectorCap, Empire: empireCap));

    // ---- Part A: RelicCost wire field + Validate pairing ---------------------

    [Fact]
    public void A_relicCost_wire_field_parses_onto_the_def()
    {
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 2,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            Assert.Equal(2, StructureCatalog.Get("test-wonder").RelicCost);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Every_shipped_row_loads_with_RelicCost_zero()
    {
        // Byte-identity proof without pinning a population count: whatever the corpus holds today,
        // no shipped row opts into the new wire field — except the Wonder rows, which opt in by
        // design (covered by dedicated Wonder tests; 4B.1 standing-stones/sunspire-throne).
        var corpus = StructureCorpus.Load(RealCorpusRoot());
        foreach (var row in corpus.Rows.Where(r => r.IsCatalogLoadable && r.Magnitudes!.WonderScope is null))
            Assert.Null(row.Magnitudes!.RelicCost);

        try
        {
            StructureCatalog.Configure(corpus);
            foreach (var def in StructureCatalog.All.Where(d => d.WonderScope is null))
                Assert.Equal(0, def.RelicCost);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    static StructureDef BaseDef(string id = "test-row") => new()
    {
        StructureId = id,
        Name = "Test Row",
        RequiredSlotKind = SlotKind.Wildland,
        Kind = StructureKind.Yield,
        AcquisitionPaths = new[] { AcquisitionPath.Built },
    };

    [Fact]
    public void Validate_refuses_a_Wonder_with_no_relic_cost()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            BaseDef() with
            {
                WonderScope = WonderScope.Sector,
                WonderRarity = WonderRarity.Unique,
                WonderEffects = new[]
                {
                    new WonderEffectDef { Kind = WonderEffectKind.LoamGenerationRate, Scope = WonderScope.Sector, ValueMilli = 500 },
                },
                RelicCost = 0,
            },
        }));
        Assert.Contains("no relic cost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_refuses_a_relic_cost_with_no_WonderScope()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            BaseDef() with { RelicCost = 1 },
        }));
        Assert.Contains("no WonderScope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_a_paired_Wonder_and_an_unpaired_ordinary_row()
    {
        var rows = StructureCatalog.Validate(new[]
        {
            BaseDef("ordinary") with { RelicCost = 0 },
            BaseDef("wonder") with
            {
                WonderScope = WonderScope.Sector,
                WonderRarity = WonderRarity.Common,
                WonderEffects = new[]
                {
                    new WonderEffectDef { Kind = WonderEffectKind.LoamGenerationRate, Scope = WonderScope.Sector, ValueMilli = 500 },
                },
                RelicCost = 1,
            },
        });
        Assert.Equal(2, rows.Count);
    }

    // ---- Part A: materials fix, independent of any Wonder content -------------

    [Fact]
    public void An_ordinary_build_with_zero_material_cost_leaves_sector_stocks_untouched()
    {
        // AC: zero behavior change for the shipped rows — all author zero material cost.
        var corpus = Corpus(RowJson("test-hut", "Yield", "Rootbed"));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave", Rootbed(0)) with { RubbleStock = 40, IronworkStock = 7 };
            var world = WorldWith("s", sector, Founder("s"));
            var report = new TurnReport();

            var result = BuildResolver.Run(world, new[] { Build("b1", "s", "test-hut") }, report, "snapshot");

            Assert.Equal("test-hut", result.Sectors[0].Slots[0].StructureId);
            Assert.Equal(40, result.Sectors[0].RubbleStock);
            Assert.Equal(7, result.Sectors[0].IronworkStock);
            Assert.Contains(report.Entries, e => e.Detail == "build.started:test-hut");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void A_build_refuses_when_the_sector_cannot_cover_material_costs()
    {
        var corpus = Corpus(RowJson("test-forge", "Yield", "Rootbed", rubble: 10, ironwork: 5));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave", Rootbed(0)) with { RubbleStock = 9, IronworkStock = 5 };
            var world = WorldWith("s", sector, Founder("s"));
            var report = new TurnReport();

            var result = BuildResolver.Run(world, new[] { Build("b1", "s", "test-forge") }, report, "snapshot");

            Assert.Null(result.Sectors[0].Slots[0].StructureId);
            Assert.Equal(9, result.Sectors[0].RubbleStock);
            Assert.Contains(report.Entries, e =>
                e.Kind == TurnReportKinds.CommandDropped && e.Detail == "build.cannot-afford-materials");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void A_build_debits_sector_rubble_and_ironwork_exactly()
    {
        var corpus = Corpus(RowJson("test-forge", "Yield", "Rootbed", rubble: 10, ironwork: 5));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave", Rootbed(0)) with { RubbleStock = 30, IronworkStock = 12 };
            var world = WorldWith("s", sector, Founder("s"));
            var report = new TurnReport();

            var result = BuildResolver.Run(world, new[] { Build("b1", "s", "test-forge") }, report, "snapshot");

            Assert.Equal("test-forge", result.Sectors[0].Slots[0].StructureId);
            Assert.Equal(20, result.Sectors[0].RubbleStock);
            Assert.Equal(7, result.Sectors[0].IronworkStock);
            Assert.Contains(report.Entries, e => e.Detail == "build.started:test-forge");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- Part A: existence scan ------------------------------------------------

    [Fact]
    public void Existence_scan_counts_a_structure_still_under_construction()
    {
        // The session's found-and-fixed bypass: acceptance counts, not completion.
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 1,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave", Wildland(0, "test-wonder", constructionTurnsRemaining: 2));
            var world = WorldWith("s", sector, Founder("s"));

            Assert.Equal(1, WonderExistenceScan.CountExisting(world, world.Sectors[0], WonderScope.Sector, WonderRarity.Unique));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Existence_scan_ignores_finished_and_unfinished_non_wonders_and_other_scopes()
    {
        var corpus = Corpus(
            RowJson("test-sector-wonder", "Yield", "Wildland",
                wonderScope: "Sector", wonderRarity: "Unique", relicCost: 1,
                effectsJson: WonderEffect("Sector")),
            RowJson("test-empire-wonder", "Yield", "Wildland",
                wonderScope: "Empire", wonderRarity: "Unique", relicCost: 1,
                effectsJson: WonderEffect("Empire")),
            RowJson("test-hut", "Yield", "Wildland"));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave",
                Wildland(0, "test-hut"),
                Wildland(1, "test-empire-wonder", constructionTurnsRemaining: 3),
                Wildland(2, "unknown-row", constructionTurnsRemaining: 1));
            var world = WorldWith("s", sector, Founder("s"));

            // A Sector-scope scan sees neither the Empire wonder, the hut, nor the unknown id.
            Assert.Equal(0, WonderExistenceScan.CountExisting(world, world.Sectors[0], WonderScope.Sector, WonderRarity.Unique));
            // An Empire-scope scan sees exactly the one Empire wonder.
            Assert.Equal(1, WonderExistenceScan.CountExisting(world, world.Sectors[0], WonderScope.Empire, WonderRarity.Unique));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Existence_scan_returns_zero_for_Common_without_reading_the_world()
    {
        var world = new WorldState();
        Assert.Equal(0, WonderExistenceScan.CountExisting(
            world, Sector("s", "dave", Wildland(0, "anything")), WonderScope.Sector, WonderRarity.Common));
        Assert.Equal(0, WonderExistenceScan.CountExisting(
            world, Sector("s", "dave", Wildland(0, "anything")), WonderScope.Empire, WonderRarity.Common));
    }

    [Fact]
    public void Existence_scan_throws_for_reserved_scopes()
    {
        var world = new WorldState();
        var sector = Sector("s", "dave");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WonderExistenceScan.CountExisting(world, sector, WonderScope.World, WonderRarity.Unique));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WonderExistenceScan.CountExisting(world, sector, WonderScope.Multiverse, WonderRarity.Unique));
    }

    [Fact]
    public void Two_unique_wonders_cannot_both_be_under_construction_at_once()
    {
        // Full resolver proof of the acceptance-counted cap: the second order sees the first
        // order's still-unfinished slot and drops with wonder.cap-reached.
        WonderPolicy.Configure(CapTuning(sectorCap: 1, empireCap: 99));
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 1,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var sector = Sector("s", "dave", Wildland(0), Wildland(1));
            var world = WorldWith("s", sector, Founder("s"));
            var report = new TurnReport();

            var afterFirst = BuildResolver.Run(world,
                new[] { Build("b1", "s", "test-wonder", slotIndex: 0, "relic-a") }, report, "snapshot");
            Assert.Equal("test-wonder", afterFirst.Sectors[0].Slots[0].StructureId);

            var report2 = new TurnReport();
            var afterSecond = BuildResolver.Run(afterFirst,
                new[] { Build("b2", "s", "test-wonder", slotIndex: 1, "relic-b") }, report2, "snapshot");

            Assert.Null(afterSecond.Sectors[0].Slots[1].StructureId);
            Assert.Contains(report2.Entries, e =>
                e.Kind == TurnReportKinds.CommandDropped && e.Detail == "wonder.cap-reached");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void A_common_wonder_builds_past_a_full_unique_cap()
    {
        WonderPolicy.Configure(CapTuning(sectorCap: 1, empireCap: 99));
        var corpus = Corpus(
            RowJson("test-unique", "Yield", "Wildland",
                wonderScope: "Sector", wonderRarity: "Unique", relicCost: 1,
                effectsJson: WonderEffect("Sector")),
            RowJson("test-common", "Yield", "Wildland",
                wonderScope: "Sector", wonderRarity: "Common", relicCost: 1,
                effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            // The Unique cap is already saturated by an unfinished Unique wonder...
            var sector = Sector("s", "dave",
                Wildland(0, "test-unique", constructionTurnsRemaining: 2),
                Wildland(1));
            var world = WorldWith("s", sector, Founder("s"));
            var report = new TurnReport();

            // ...yet a Common wonder still builds: the scan short-circuits to 0 and the cap is infinite.
            var result = BuildResolver.Run(world,
                new[] { Build("b1", "s", "test-common", slotIndex: 1, "relic-a") }, report, "snapshot");

            Assert.Equal("test-common", result.Sectors[0].Slots[1].StructureId);
            Assert.Contains(report.Entries, e => e.Detail == "build.started:test-common");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void A_wonder_build_with_an_emptied_relic_list_drops_as_not_reachable()
    {
        // §Design 4's contract: the Data-side gate empties RelicInstanceIds when the relics prove
        // unreachable; the resolver refuses the count, never the ownership it cannot see.
        WonderPolicy.Configure(CapTuning(sectorCap: 99, empireCap: 99));
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 1,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var world = WorldWith("s", Sector("s", "dave", Wildland(0)), Founder("s"));
            var report = new TurnReport();

            var result = BuildResolver.Run(world,
                new[] { Build("b1", "s", "test-wonder") }, report, "snapshot");

            Assert.Null(result.Sectors[0].Slots[0].StructureId);
            Assert.Contains(report.Entries, e =>
                e.Kind == TurnReportKinds.CommandDropped && e.Detail == "relic.not-reachable");
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- Part B: admission count/dup ------------------------------------------

    [Fact]
    public void Admission_refuses_a_relic_count_mismatch()
    {
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 2,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var world = WorldWith("s", Sector("s", "dave", Wildland(0)), Founder("s"));

            var (ok, reason) = WorldCommandAdmission.Admit(world, Build("b1", "s", "test-wonder", 0, "only-one"));
            Assert.False(ok);
            Assert.Equal("relic.count-mismatch", reason);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Admission_refuses_duplicate_relic_ids()
    {
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 2,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var world = WorldWith("s", Sector("s", "dave", Wildland(0)), Founder("s"));

            var (ok, reason) = WorldCommandAdmission.Admit(world, Build("b1", "s", "test-wonder", 0, "relic-a", "relic-a"));
            Assert.False(ok);
            Assert.Equal("relic.duplicate", reason);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Admission_accepts_exact_distinct_relic_ids()
    {
        var corpus = Corpus(RowJson("test-wonder", "Yield", "Wildland",
            wonderScope: "Sector", wonderRarity: "Unique", relicCost: 2,
            effectsJson: WonderEffect("Sector")));
        try
        {
            StructureCatalog.Configure(corpus);
            var world = WorldWith("s", Sector("s", "dave", Wildland(0)), Founder("s"));

            var (ok, reason) = WorldCommandAdmission.Admit(world, Build("b1", "s", "test-wonder", 0, "relic-a", "relic-b"));
            Assert.True(ok, reason);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Admission_ignores_relic_ids_for_ordinary_structures()
    {
        var corpus = Corpus(RowJson("test-hut", "Yield", "Rootbed"));
        try
        {
            StructureCatalog.Configure(corpus);
            var world = WorldWith("s", Sector("s", "dave", Rootbed(0)), Founder("s"));

            var cmd = Build("b1", "s", "test-hut") with { SlotIndex = 0 };
            var (ok, reason) = WorldCommandAdmission.Admit(world, cmd);
            Assert.True(ok, reason);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }
}
