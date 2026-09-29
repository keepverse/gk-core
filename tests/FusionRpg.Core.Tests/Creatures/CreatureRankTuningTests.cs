using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// Task 1 (spec-species-rank.md §2, §6): the rank tuning table and its loader. The axis vocabularies
/// are READ from their declaring files — the rarity ladder's ids off <see cref="CreatureRarityLadder"/>
/// (itself guard-pinned to <c>gk-data/packs/fusion/data/seed/rarity/ladder.v1.json</c>) and the threat rungs off
/// <c>creature-threat.v2.json</c> — never restated here, and no cell count is pinned: the grid's shape
/// is <c>threatBands × rarities</c>, expressed over those two closed vocabularies.
/// </summary>
public class CreatureRankTuningTests
{
    static readonly IReadOnlyList<string> RarityIds =
        CreatureRarityLadder.All.Select(r => r.ToId()).ToList();

    static readonly IReadOnlyList<string> ThreatIds =
        CreatureThreatTuningLoader.Parse(
            File.ReadAllText(Path.Combine(TuningDir, "creature-threat.v2.json"))).RungIds;

    static string TuningDir => Path.Combine(FindRepoRoot(), "data", "tuning");

    static CreatureRankTuning Shipped =>
        CreatureRankTuningLoader.Parse(
            File.ReadAllText(Path.Combine(TuningDir, CreatureRankTuningLoader.File)), ThreatIds, RarityIds);

    [Fact]
    public void Shipped_table_mirrors_the_rarity_ladder_and_covers_every_pair_exactly_once()
    {
        var tuning = Shipped;

        // Row for row against the declaring ladder (spec Assumption 1) — never a second vocabulary.
        Assert.Equal(RarityIds, tuning.RankIds);
        Assert.Equal(ThreatIds.Count * RarityIds.Count, tuning.Grid.Count);
        Assert.Equal(
            tuning.Grid.Count,
            tuning.Grid.Select(c => (c.ThreatBand, c.Rarity)).Distinct().Count());
    }

    [Fact]
    public void Shipped_table_ships_diagonal_by_default()
    {
        // "Diagonal-by-default" = every cell's rank is its own column's rarity rung, so the threat
        // row changes nothing until a balance pass authors an off-diagonal divergence.
        foreach (var cell in Shipped.Grid)
            Assert.Equal(cell.Rarity, cell.RankId);
    }

    [Fact]
    public void Shipped_gate_floors_all_default_to_the_bottom_rung()
    {
        var tuning = Shipped;
        Assert.Equal(CreatureRankTuning.GateIds.Count, tuning.Floors.Count);
        foreach (var gate in CreatureRankTuning.GateIds)
            Assert.Equal(tuning.BottomRankId, tuning.FloorFor(gate));
        Assert.Equal(tuning.RankIds[0], tuning.BottomRankId);
    }

    // ---- negative paths: the loader names the TABLE and the CELL, never a bare "invalid" ----

    [Fact]
    public void An_unknown_rank_id_rejects_naming_the_table_and_the_cell()
    {
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "legendary"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("grid[1]", ex.Message);
        Assert.Contains("(threatBand 'pest', rarity 'almanac')", ex.Message);
        Assert.Contains("'ranks'", ex.Message);
        Assert.Contains("'legendary'", ex.Message);
    }

    [Fact]
    public void An_unknown_threatBand_rejects_naming_the_cell()
    {
        var json = Json(FixtureGrid(("harbinger", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("grid[0]", ex.Message);
        Assert.Contains("'harbinger'", ex.Message);
        // The LOADER's own message names the file the production readers load (still v1 — TB-H2's
        // denied C# half); this test's own parse above uses the current version.
        Assert.Contains("creature-threat.v1.json", ex.Message);
    }

    [Fact]
    public void An_unknown_rarity_rejects_naming_the_cell()
    {
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "sunwoven", "sunwoven"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("grid[1]", ex.Message);
        Assert.Contains("'sunwoven'", ex.Message);
        Assert.Contains("rarity ladder", ex.Message);
    }

    [Fact]
    public void The_unresolved_sentinel_never_reaches_the_table()
    {
        var json = Json(FixtureGrid(("unresolved", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("grid[0]", ex.Message);
        Assert.Contains("unresolved", ex.Message);
        Assert.Contains("never reach the table", ex.Message);
    }

    [Fact]
    public void A_repeated_pair_rejects()
    {
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "chaff", "chaff"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("repeats", ex.Message);
    }

    [Fact]
    public void A_missing_pair_rejects_naming_the_expected_coverage()
    {
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff")), FixtureRarity);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("expected one per (threatBand, rarity) pair", ex.Message);
    }

    [Fact]
    public void A_rank_table_that_does_not_mirror_the_ladder_rejects()
    {
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), new[] { "chaff", "sprout" });

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("does not mirror the rarity ladder", ex.Message);
    }

    [Fact]
    public void A_missing_or_unknown_gate_floor_rejects()
    {
        var missing = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity,
            floors: new Dictionary<string, string> { ["fusionPromotion"] = "chaff" });
        var missingEx = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(missing, FixtureThreat, FixtureRarity));
        Assert.Contains("missing gate", missingEx.Message);

        var unknown = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity,
            floors: CreatureRankTuning.GateIds.ToDictionary(g => g, _ => "chaff", StringComparer.Ordinal)
                .Append(new KeyValuePair<string, string>("sixthGate", "chaff"))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        var unknownEx = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(unknown, FixtureThreat, FixtureRarity));
        Assert.Contains("'sixthGate'", unknownEx.Message);
        Assert.Contains("not one of the five gates", unknownEx.Message);
    }

    [Fact]
    public void A_gate_floor_naming_a_non_rank_id_rejects()
    {
        var floors = CreatureRankTuning.GateIds.ToDictionary(g => g, _ => "chaff", StringComparer.Ordinal);
        floors["waveBand"] = "legendary";
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "chaff"), ("tyrant", "almanac", "almanac")), FixtureRarity, floors);

        var ex = Assert.Throws<CreatureRankTuningRejection>(
            () => CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity));

        Assert.Contains("floors.waveBand", ex.Message);
        Assert.Contains("'ranks'", ex.Message);
    }

    // ---- lookup contract ----

    [Fact]
    public void RankIdFor_refuses_an_unresolved_input_on_either_axis()
    {
        var tuning = Shipped;

        var threatEx = Assert.Throws<CreatureRankTuningRejection>(
            () => tuning.RankIdFor("unresolved", tuning.RankIds[0]));
        Assert.Contains("unresolved", threatEx.Message);

        var rarityEx = Assert.Throws<CreatureRankTuningRejection>(
            () => tuning.RankIdFor(ThreatIds[0], "unresolved"));
        Assert.Contains("unresolved", rarityEx.Message);
    }

    [Fact]
    public void RankIdFor_reads_the_authored_row_not_just_the_column()
    {
        // The one planted off-diagonal cell proves threat selects the ROW: (tyrant, chaff) is authored
        // to almanac while (pest, chaff) keeps the diagonal default.
        var json = Json(FixtureGrid(("pest", "chaff", "chaff"), ("pest", "almanac", "almanac"),
            ("tyrant", "chaff", "almanac"), ("tyrant", "almanac", "almanac")), FixtureRarity);
        var tuning = CreatureRankTuningLoader.Parse(json, FixtureThreat, FixtureRarity);

        Assert.Equal("chaff", tuning.RankIdFor("pest", "chaff"));
        Assert.Equal("almanac", tuning.RankIdFor("tyrant", "chaff"));
    }

    [Fact]
    public void FloorFor_refuses_an_unknown_gate()
    {
        var ex = Assert.Throws<CreatureRankTuningRejection>(() => Shipped.FloorFor("notAGate"));
        Assert.Contains("notAGate", ex.Message);
        Assert.Contains("closed gate vocabulary", ex.Message);
    }

    // ---- fixture plumbing ----

    static readonly string[] FixtureThreat = { "pest", "tyrant" };
    static readonly string[] FixtureRarity = { "chaff", "almanac" };

    static (string, string, string)[] FixtureGrid(params (string, string, string)[] cells) => cells;

    static string Json(
        (string Threat, string Rarity, string Rank)[] grid, string[] ranks,
        IReadOnlyDictionary<string, string>? floors = null)
    {
        floors ??= CreatureRankTuning.GateIds.ToDictionary(g => g, _ => ranks[0], StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            version = 1,
            ranks,
            grid = grid.Select(c => new { threatBand = c.Threat, rarity = c.Rarity, rank = c.Rank }),
            floors,
        });
    }

    static string FindRepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
