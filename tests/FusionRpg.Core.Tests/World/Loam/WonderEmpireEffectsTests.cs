using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// Task 2.3a (`wonder-effect-empire` §Design 1-3): faction-input plumbing + the SUM.
/// Each test names the acceptance-criteria bullet it proves. In-memory compute only — no SQL
/// anywhere in this file (persistence is Task 2.3b's job).
/// </summary>
public class WonderEmpireEffectsTests
{
    const string Phase = "Test";

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSlot Hosting(int index, string structureId, int? constructionTurnsRemaining = null) => new()
    {
        SlotIndex = index,
        SlotTypeId = SlotTypeCatalog.WildlandSlotTypeId,
        StructureId = structureId,
        ConstructionTurnsRemaining = constructionTurnsRemaining,
    };

    static WorldSector Sector(string id, string? owner, params WorldSlot[] slots) => new()
    {
        SectorId = id, TypeId = "stable", OwnerFactionId = owner, Slots = slots,
    };

    static WorldState WorldOf(params WorldSector[] sectors) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[]
        {
            new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" },
            new WorldFaction { FactionId = "f2", Kind = WorldFactionKind.Player, Name = "F2" },
        },
        Sectors = sectors,
    };

    static WorldFaction FactionOf(WorldState world, string id) =>
        world.Factions.Single(f => f.FactionId == id);

    // ---- in-memory corpus scaffolding (same shape as WonderCatalogTests; writes nothing) ----

    static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    static void RestoreRealCorpus() => StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));

    static string WonderRowJson(string id, string scope, long valueMilli) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "anchor": {
            "structureId": "{{id}}", "family": "test", "role": "Extract", "roleSecondary": "none",
            "requiredSlotKind": "Wildland", "elementPrimary": "none", "elementSecondary": "none",
            "tempo": "none", "reach": "melee", "strengthBand": "rubble", "rarity": "sprout",
            "traits": [], "costProfile": "cheap", "targetPreference": "none", "variants": [],
            "acquisitionPaths": ["built"], "footprint": "one-cell", "coverTier": "none",
            "controlPoint": true, "obstacleVerbs": [], "reason": "test"
          },
          "_provenance": {"source": "AUTHORED", "citation": "test"},
          "magnitudes": {
            "structureKind": "Yield", "cost": 0, "yieldMultiplierMilli": 1000,
            "buildTurns": 0, "capacityBonus": 0,
            "flatYieldPerTurn": 0,
            "constructRubbleCost": 0, "constructIronworkCost": 0, "materialTier": 0,
            "blocksMovement": false, "blocksLineOfFire": false, "obstacleKind": "None",
            "coverPowerMilli": 0, "coverRadius": 0, "entryStaminaMultiplierMilli": 1000,
            "visionRangeTiles": null, "relicCost": 1,
            "wonderScope": "{{scope}}", "wonderRarity": "Common",
            "wonderEffects": [{ "kind": "LoamGenerationRate", "scope": "{{scope}}", "valueMilli": {{valueMilli}} }]
          }
        }
        """;

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

    // ---- AC 1: zero-Wonder byte-identity ----

    [Fact]
    public void The_default_modifier_is_an_identity_for_bare_and_developed_sectors()
    {
        var bare = Sector("s", "f1", Rootbed(0));
        Assert.Equal(LoamProduction.For(bare, 1000), LoamProduction.For(bare));

        var developed = bare with { DevelopmentLevel = 5 };
        Assert.Equal(LoamProduction.For(developed, 1000), LoamProduction.For(developed));
    }

    [Fact]
    public void Production_with_no_Wonders_leaves_every_faction_at_default_with_unchanged_yield()
    {
        var world = WorldOf(Sector("s", "f1", Rootbed(0)));

        var after = LoamPhases.Production(world, new TurnReport(), Phase);

        Assert.Equal(1000, FactionOf(after, "f1").ScopeModifierMilli);
        Assert.Equal(1000, FactionOf(after, "f2").ScopeModifierMilli);
        Assert.Equal(LoamPolicy.SeepPerTurn, after.Sectors.Single(s => s.SectorId == "s").LoamStock);
    }

    [Fact]
    public void Non_Empire_and_inactive_content_contributes_nothing_to_the_sum()
    {
        var corpus = Corpus(
            WonderRowJson("empire-a", "Empire", 200),
            WonderRowJson("sector-w", "Sector", 5000));
        try
        {
            StructureCatalog.Configure(corpus);

            var sectors = new[]
            {
                // Sector-scope Wonder: real content, wrong axis — never an Empire term.
                Sector("s1", "f1", Hosting(0, "sector-w")),
                // Empire Wonder still under construction — inert until its counter reaches zero.
                Sector("s2", "f1", Hosting(0, "empire-a", constructionTurnsRemaining: 2)),
                // Unknown structure id — a stale slot reference, not a Wonder.
                Sector("s3", "f1", Hosting(0, "no-such-structure")),
            };

            Assert.Equal(1000, WonderEmpireEffects.ComputeScopeModifierMilli(sectors, "f1"));
            Assert.Equal(0, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[0]));
            Assert.Equal(0, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[1]));
            Assert.Equal(0, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[2]));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 2: SUM, not max or replace — 200 + 300 = 1500 ----

    [Fact]
    public void Two_built_Empire_Wonders_sum_to_1500()
    {
        var corpus = Corpus(
            WonderRowJson("empire-a", "Empire", 200),
            WonderRowJson("empire-b", "Empire", 300));
        try
        {
            StructureCatalog.Configure(corpus);

            var sectors = new[]
            {
                Sector("s1", "f1", Rootbed(0), Hosting(1, "empire-a")),
                Sector("s2", "f1", Rootbed(0), Hosting(1, "empire-b")),
                Sector("s3", "f2", Rootbed(0), Hosting(1, "empire-a")),
            };

            // Per-sector halves sum independently; the faction total is the SUM across its own
            // sectors only — f2's Wonder never leaks into f1's modifier.
            Assert.Equal(200, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[0]));
            Assert.Equal(300, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[1]));
            Assert.Equal(1500, WonderEmpireEffects.ComputeScopeModifierMilli(sectors, "f1"));
            Assert.Equal(1200, WonderEmpireEffects.ComputeScopeModifierMilli(sectors, "f2"));

            var world = WorldOf(sectors);
            var after = LoamPhases.Production(world, new TurnReport(), Phase);

            Assert.Equal(1500, FactionOf(after, "f1").ScopeModifierMilli);
            Assert.Equal(1200, FactionOf(after, "f2").ScopeModifierMilli);

            // Every sector of the faction is multiplied by the same resolved modifier, once, last.
            var expectedS1 = checked(LoamPolicy.SeepPerTurn * 1500 / 1000);
            Assert.Equal(expectedS1, after.Sectors.Single(s => s.SectorId == "s1").LoamStock);
            Assert.Equal(LoamProduction.For(sectors[0], 1500), expectedS1);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 3: same-pass activation ----

    [Fact]
    public void A_Wonder_finishing_construction_this_phase_counts_this_phase()
    {
        var corpus = Corpus(WonderRowJson("empire-a", "Empire", 200));
        try
        {
            StructureCatalog.Configure(corpus);

            var world = WorldOf(Sector("s", "f1", Rootbed(0), Hosting(1, "empire-a", constructionTurnsRemaining: 1)));

            var after = LoamPhases.Production(world, new TurnReport(), Phase);

            // Decrement ran first (1 -> 0), so the post-decrement scan already sees it as built.
            Assert.Equal(0, after.Sectors.Single(s => s.SectorId == "s").Slots.Single(sl => sl.SlotIndex == 1).ConstructionTurnsRemaining);
            Assert.Equal(1200, FactionOf(after, "f1").ScopeModifierMilli);
            Assert.Equal(checked(LoamPolicy.SeepPerTurn * 1200 / 1000),
                after.Sectors.Single(s => s.SectorId == "s").LoamStock);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 4: lost-sector drop-out, no residue ----

    [Fact]
    public void Losing_the_Wonder_sector_returns_the_modifier_to_1000_next_phase()
    {
        var corpus = Corpus(WonderRowJson("empire-a", "Empire", 200));
        try
        {
            StructureCatalog.Configure(corpus);

            var world = WorldOf(Sector("s", "f1", Rootbed(0), Hosting(1, "empire-a")));
            var built = LoamPhases.Production(world, new TurnReport(), Phase);
            Assert.Equal(1200, FactionOf(built, "f1").ScopeModifierMilli);

            // The sector changes hands (capture/loss): the Wonder no longer belongs to f1.
            var lost = built with
            {
                Sectors = built.Sectors
                    .Select(s => s.SectorId == "s" ? s with { OwnerFactionId = "f2" } : s)
                    .ToList(),
            };
            var after = LoamPhases.Production(lost, new TurnReport(), Phase);

            Assert.Equal(1000, FactionOf(after, "f1").ScopeModifierMilli);
            // No residue, and correct reattribution: the Wonder now counts for its new owner.
            Assert.Equal(1200, FactionOf(after, "f2").ScopeModifierMilli);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 5: checked-overflow regression at the narrowing cast ----

    [Fact]
    public void An_out_of_range_sum_throws_OverflowException_at_the_narrowing_cast()
    {
        var corpus = Corpus(WonderRowJson("empire-huge", "Empire", (long)int.MaxValue));
        try
        {
            StructureCatalog.Configure(corpus);

            var sectors = new[] { Sector("s", "f1", Hosting(0, "empire-huge")) };

            // The long running sum itself is fine — it is the narrowing to the inherited int
            // field that must throw, never wrap to a negative or wrapped-around modifier.
            Assert.Equal((long)int.MaxValue, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sectors[0]));
            Assert.Throws<OverflowException>(() =>
                WonderEmpireEffects.ComputeScopeModifierMilli(sectors, "f1"));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }
}
