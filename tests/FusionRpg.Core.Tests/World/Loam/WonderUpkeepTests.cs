using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// Task 2.3c (`wonder-effect-empire` §Design 6): the fifth `LoamUpkeepBreakdown` term
/// (`WonderUpkeep`), charged to the Wonder's own hosting sector. Each test names the
/// acceptance-criteria bullet it proves. In-memory compute only — no SQL anywhere in this file.
/// </summary>
public class WonderUpkeepTests
{
    const string Phase = "Test";

    static long Rate => LoamPolicy.EmpireWonderUpkeepRateMilli;

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
            new WorldFaction { FactionId = "f2", Kind = WorldFactionKind.Player, Name = "F1-b" },
        },
        Sectors = sectors,
    };

    // ---- in-memory corpus scaffolding (same shape as WonderEmpireEffectsTests; writes nothing) ----

    static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    static string FindRepoRoot() => CoreRoot.Path;

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

    // ---- AC 1: fifth field, in Sum and Total ----

    [Fact]
    public void WonderUpkeep_is_a_fifth_additive_term_in_Sum_and_Total()
    {
        const long wonder = 10;
        var with = LoamUpkeep.Breakdown(4, 2, 1, 1000, 1000, 1000, wonderUpkeep: wonder);
        var without = LoamUpkeep.Breakdown(4, 2, 1, 1000, 1000, 1000);

        Assert.Equal(without.Sum + wonder, with.Sum);
        var expectedTotal = checked((without.Sum + wonder) * 1000 * 1000 * 1000 / 1_000_000_000);
        Assert.Equal(expectedTotal, with.Total);
        Assert.Equal(wonder, with.WonderUpkeep);
        Assert.Equal(0, without.WonderUpkeep);
    }

    // ---- AC 2: defaulted trailing parameter; existing call sites unchanged ----

    [Fact]
    public void The_defaulted_trailing_parameter_leaves_every_existing_call_site_at_zero()
    {
        var def = LoamUpkeep.Breakdown(4, 2, 1, 1000, 1000, 1000);
        var explicitZero = LoamUpkeep.Breakdown(4, 2, 1, 1000, 1000, 1000, wonderUpkeep: 0);

        Assert.Equal(0, def.WonderUpkeep);
        Assert.Equal(explicitZero, def);
        Assert.Equal(def.Total, LoamUpkeep.For(4, 2, 1, 1000, 1000, 1000));
    }

    [Fact]
    public void A_Wonder_free_sector_pays_the_unchanged_four_term_upkeep()
    {
        var barren = Sector("s-barren", "f1");
        var withSource = Sector("s-source", "f1", Rootbed(0));
        var world = WorldOf(barren, withSource);

        var breakdown = LoamUpkeep.BreakdownFor(world, barren);
        var expectedSum = LoamPolicy.BaseUpkeepPerSector
            + LoamPolicy.DevelopmentAndDangerUpkeep(barren.DevelopmentLevel, barren.DangerBand);

        Assert.Equal(0, breakdown.WonderUpkeep);
        Assert.Equal(expectedSum, breakdown.Sum);
        Assert.Equal(LoamUpkeep.For(world, barren), breakdown.Total);
    }

    // ---- AC 3: the rate lives in LoamPolicy / loam tuning, not WonderPolicy ----

    [Fact]
    public void The_rate_is_a_LoamPolicy_tunable_matching_the_shipped_file()
    {
        // Provisional Task-2.3c value (five percent), flagged for a balance pass — pinned here
        // against the shipped file, not chosen by this test.
        var parsed = LoamTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "loam.v5.json")));

        Assert.Equal(50, parsed.Upkeep.EmpireWonderUpkeepRateMilli);
        Assert.Equal(parsed.Upkeep.EmpireWonderUpkeepRateMilli, Rate);
    }

    [Fact]
    public void A_tuning_document_without_the_new_key_is_rejected_not_defaulted()
    {
        var v4 = File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "loam.v4.json"));

        // v4 predates the term: no built-in default fills the gap — an old document fails loudly
        // (this is why every host/reader must move to loam.v5.json; tracked as the named follow-up).
        Assert.Throws<LoamTuningRejection>(() => LoamTuningLoader.Parse(v4));
    }

    [Fact]
    public void WonderPolicy_owns_no_upkeep_rate()
    {
        // The placement decision (§Design 6): "how many may exist" (WonderPolicy) vs. "what a built
        // one costs" (LoamPolicy). A future upkeep-shaped member on WonderPolicy must acknowledge
        // this test explicitly rather than land silently.
        var upkeepShaped = typeof(WonderPolicy).GetMembers(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name.Contains("Upkeep", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(upkeepShaped);
    }

    // ---- AC 4: starve -> lose the Wonder via the existing lost-sector branch ----

    [Fact]
    public void Losing_the_hosting_sector_clears_both_the_modifier_and_the_upkeep()
    {
        var corpus = Corpus(WonderRowJson("empire-a", "Empire", 200));
        try
        {
            StructureCatalog.Configure(corpus);

            var world = WorldOf(Sector("s", "f1", Rootbed(0), Hosting(1, "empire-a")));
            var built = LoamPhases.Production(world, new TurnReport(), Phase);
            var builtSector = built.Sectors.Single(s => s.SectorId == "s");

            Assert.Equal(1200, built.Factions.Single(f => f.FactionId == "f1").ScopeModifierMilli);
            Assert.Equal(checked(200 * Rate / 1000), LoamUpkeep.BreakdownFor(built, builtSector).WonderUpkeep);

            // Pressure's own lost-sector branch (LoamPhases.cs:212-223) clears every slot's
            // StructureId unconditionally — mirrored here verbatim, no new bookkeeping anywhere.
            var lost = built with
            {
                Sectors = built.Sectors
                    .Select(s => s.SectorId == "s"
                        ? s with
                        {
                            Slots = s.Slots
                                .Select(sl => sl with { StructureId = null, ConstructionTurnsRemaining = null })
                                .ToList(),
                        }
                        : s)
                    .ToList(),
            };
            var after = LoamPhases.Production(lost, new TurnReport(), Phase);
            var afterSector = after.Sectors.Single(s => s.SectorId == "s");

            Assert.Equal(1000, after.Factions.Single(f => f.FactionId == "f1").ScopeModifierMilli);
            Assert.Equal(0, LoamUpkeep.BreakdownFor(after, afterSector).WonderUpkeep);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 5: sum-then-divide, not divide-then-sum ----

    [Fact]
    public void Two_effects_on_one_sector_cost_the_same_as_one_combined_effect()
    {
        // Deliberately non-round pair, mirroring
        // LoamUpkeepTests.The_formula_divides_only_once_not_once_per_multiplier's own proof shape:
        // at the shipped rate (50), per-term division floors each addend (333*50/1000 = 16, twice =
        // 32) while sum-first divides once (666*50/1000 = 33). Two Wonder-bearing slots on one
        // sector (a single row cannot author two same-(Kind, Scope) effects — Validate refuses the
        // duplicate pair) must match one slot authoring their combined ValueMilli.
        var corpus = Corpus(
            WonderRowJson("w-a", "Empire", 333),
            WonderRowJson("w-b", "Empire", 333),
            WonderRowJson("w-combined", "Empire", 666));
        try
        {
            StructureCatalog.Configure(corpus);

            var split = Sector("s-split", "f1", Rootbed(0), Hosting(1, "w-a"), Hosting(2, "w-b"));
            var combined = Sector("s-combined", "f1", Rootbed(0), Hosting(1, "w-combined"));
            var world = WorldOf(split, combined);

            var expected = checked((333 + 333) * Rate / 1000);
            var perTermOrder = checked(333 * Rate / 1000) + checked(333 * Rate / 1000);

            // The pair is only a proof while the two orderings actually disagree — if a future
            // balance pass moves the rate to a value where they agree, this names the requirement
            // instead of silently proving nothing.
            Assert.NotEqual(expected, perTermOrder);

            Assert.Equal(expected, LoamUpkeep.BreakdownFor(world, split).WonderUpkeep);
            Assert.Equal(expected, LoamUpkeep.BreakdownFor(world, combined).WonderUpkeep);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 6: checked overflow on the new multiply ----

    [Fact]
    public void An_out_of_range_ValueMilli_times_rate_throws_OverflowException()
    {
        // Validate bounds ValueMilli below only (StructureCatalog.cs:416-417 rejects negatives, no
        // ceiling), so a synthetic long.MaxValue effect loads fine — the running sum holds it, and
        // the upkeep multiply itself must throw, never wrap into a negative upkeep.
        var corpus = Corpus(WonderRowJson("empire-huge", "Empire", long.MaxValue));
        try
        {
            StructureCatalog.Configure(corpus);

            var sector = Sector("s", "f1", Rootbed(0), Hosting(1, "empire-huge"));
            var world = WorldOf(sector);

            Assert.Equal(long.MaxValue, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sector));
            Assert.Throws<OverflowException>(() => LoamUpkeep.BreakdownFor(world, sector));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 7: same-pass activation parity (upkeep side) ----

    [Fact]
    public void A_Wonder_finishing_construction_this_pass_is_charged_this_pass()
    {
        var corpus = Corpus(WonderRowJson("empire-a", "Empire", 200));
        try
        {
            StructureCatalog.Configure(corpus);

            var sector = Sector("s", "f1", Rootbed(0), Hosting(1, "empire-a", constructionTurnsRemaining: 1));
            var world = WorldOf(sector);

            // Still under construction: inert on both sides.
            Assert.Equal(0, LoamUpkeep.BreakdownFor(world, sector).WonderUpkeep);
            Assert.Equal(0, WonderEmpireEffects.EmpireLoamGenerationValueMilliFor(sector));

            // The real Production phase decrements first (1 -> 0); the post-decrement sector is
            // already active for the benefit side (modifier 1200) AND the upkeep side (same phase's
            // Pressure read) — not the phase after, for either side.
            var after = LoamPhases.Production(world, new TurnReport(), Phase);
            var afterSector = after.Sectors.Single(s => s.SectorId == "s");

            Assert.Equal(1200, after.Factions.Single(f => f.FactionId == "f1").ScopeModifierMilli);
            Assert.Equal(checked(200 * Rate / 1000), LoamUpkeep.BreakdownFor(after, afterSector).WonderUpkeep);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 8: belief overload unaffected ----

    [Fact]
    public void The_six_argument_belief_overload_returns_the_unchanged_four_term_value()
    {
        // The only real caller is FrontierRulesPolicy.cs:192 (grep-confirmed, left unmodified) —
        // the defaulted trailing parameter must change none of its behaviour.
        const int garrison = 4, development = 2, danger = 1;
        const int intensity = 500, handicap = 777, season = 444;

        var expectedSum = LoamPolicy.BaseUpkeepPerSector
            + (long)garrison * LoamPolicy.GarrisonUpkeepPerMember
            + LoamPolicy.DevelopmentAndDangerUpkeep(development, danger);
        var expected = checked(expectedSum * intensity * handicap * season / 1_000_000_000);

        Assert.Equal(expected, LoamUpkeep.For(garrison, development, danger, intensity, handicap, season));
        Assert.Equal(
            LoamUpkeep.Breakdown(garrison, development, danger, intensity, handicap, season).Total,
            LoamUpkeep.For(garrison, development, danger, intensity, handicap, season));
    }

    // ---- active-term proportionality (spec §Testing strategy: nonzero, never flat) ----

    [Fact]
    public void A_built_Empire_Wonder_charges_ValueMilli_times_rate_over_1000()
    {
        var corpus = Corpus(WonderRowJson("empire-a", "Empire", 200));
        try
        {
            StructureCatalog.Configure(corpus);

            var sector = Sector("s", "f1", Rootbed(0), Hosting(1, "empire-a"));
            var world = WorldOf(sector);

            // Proportional to the Wonder's own magnitude (200 * 50 / 1000 = 10 at the shipped
            // rate), never a flat per-Wonder constant — computed live off the configured rate.
            Assert.Equal(checked(200 * Rate / 1000), LoamUpkeep.BreakdownFor(world, sector).WonderUpkeep);
            Assert.True(LoamUpkeep.BreakdownFor(world, sector).WonderUpkeep > 0);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }
}
