using System.Globalization;
using FusionRpg.Contracts;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("VerificationId", "data.almanac-seed")]
public class AlmanacSeedTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AlmanacSeedTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void SeedDump(string side, int typeId, string? name = null, string? info = null,
        string? introduce = null, string? cost = null, string? enumName = null)
    {
        var fields = new Dictionary<string, string?>();
        if (name != null) fields["name"] = name;
        if (info != null) fields["info"] = info;
        if (introduce != null) fields["introduce"] = introduce;
        if (cost != null) fields["cost"] = cost;
        if (enumName != null) fields["enumName"] = enumName;
        _store.UpsertAlmanacTextDump(side, typeId, fields, null);
    }

    void SeedSpawnStat(string side, int type, string ptr, string source, string capturedUtc,
        int? hpBase = null, int? attackBase = null, int? armorBase = null, int? armorMaxBase = null)
    {
        var matchKey = "seed-" + Guid.NewGuid().ToString("N");
        var payload = new Dictionary<string, object?> { ["ptr"] = ptr, ["type"] = type, ["source"] = source };
        if (hpBase.HasValue) payload["hpBase"] = hpBase.Value;
        if (attackBase.HasValue) payload["attackBase"] = attackBase.Value;
        if (armorBase.HasValue) payload["armorBase"] = armorBase.Value;
        if (armorMaxBase.HasValue) payload["armorMaxBase"] = armorMaxBase.Value;

        _store.InsertEvents(new[]
        {
            new EventEnvelope { T = capturedUtc, Kind = "board.start", MatchKey = matchKey, Payload = new { } },
            new EventEnvelope
            {
                T = capturedUtc,
                Kind = side == "plant" ? "plant.spawn" : "zombie.spawn",
                MatchKey = matchKey,
                Payload = payload
            }
        });
    }

    [Fact]
    public void Rebuild_produces_one_row_per_almanac_dump_entry()
    {
        SeedDump("plant", 0, name: "豌豆射手", enumName: "Peashooter");
        SeedDump("zombie", 0, name: "僵尸", enumName: "Zombie");

        var summary = _store.RebuildAlmanacSeed();

        Assert.Equal(2, summary.Built);
        Assert.Equal(1, summary.PlantsBuilt);
        Assert.Equal(1, summary.ZombiesBuilt);
        var all = _store.ListAlmanacSeed();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Side == "plant" && r.TypeId == 0);
        Assert.Contains(all, r => r.Side == "zombie" && r.TypeId == 0);
    }

    [Fact]
    public void Rebuild_identity_and_flavor_fields_populated_and_markup_stripped()
    {
        SeedDump("zombie", 1, name: "旗帜僵尸", enumName: "FlagZombie",
            info: "摇旗呐喊。<color=red>10</color>",
            introduce: "<color=#3D1400>他迷恋旗帜。</color>");

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("zombie", 1);

        Assert.NotNull(dto);
        Assert.Equal("旗帜僵尸", dto!.DisplayName);
        Assert.Equal("FlagZombie", dto.TypeName);
        Assert.Equal("摇旗呐喊。10", dto.FlavorInfo);
        Assert.Equal("他迷恋旗帜。", dto.FlavorIntroduce);
    }

    [Fact]
    public void Plant_rows_never_carry_flavor_introduce()
    {
        SeedDump("plant", 0, name: "豌豆射手", info: "发射豌豆。");
        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0);
        Assert.NotNull(dto);
        Assert.Null(dto!.FlavorIntroduce);
    }

    [Theory]
    [InlineData(null, "absent", null, null)]
    [InlineData("花费：<color=red>100</color>\n冷却时间：<color=red>7.5秒</color>", "parsed", 100, 7.5)]
    [InlineData("花费：<color=red>125</color>\n冷却时间：<color=red>50秒</color>", "parsed", 125, 50.0)]
    [InlineData("花费：<color=#FF0000>75</color>\n冷却时间：<color=#FF0000AA>5秒</color>", "parsed", 75, 5.0)]
    [InlineData("花费：<color=red>100</color>\n冷却时间：<color=red>7.5.5秒</color>", "unparsed", null, null)]
    [InlineData("this text has no cost template at all", "unparsed", null, null)]
    public void Cost_parsing_all_three_statuses(string? costText, string expectedStatus, int? expectedCost, double? expectedCooldown)
    {
        SeedDump("plant", 0, name: "X", cost: costText);
        var summary = _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0);

        Assert.NotNull(dto);
        Assert.Equal(expectedStatus, dto!.CostStatus);
        Assert.Equal(expectedCost, dto.SunCost);
        Assert.Equal(expectedCooldown, dto.CooldownSec);

        switch (expectedStatus)
        {
            case "absent":
                Assert.Equal(1, summary.CostAbsent);
                break;
            case "parsed":
                Assert.Equal(1, summary.CostParsed);
                break;
            case "unparsed":
                Assert.Equal(1, summary.CostUnparsed);
                break;
        }
    }

    [Fact]
    public void Cooldown_parsing_is_culture_invariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // comma-decimal
            SeedDump("plant", 0, name: "X", cost: "花费：<color=red>100</color>\n冷却时间：<color=red>7.5秒</color>");
            _store.RebuildAlmanacSeed();
            var dto = _store.GetAlmanacSeed("plant", 0);
            Assert.Equal("parsed", dto!.CostStatus);
            Assert.Equal(7.5, dto.CooldownSec);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Baseline_selection_plant_prefers_start_regardless_of_ordering()
    {
        SeedDump("plant", 0, name: "X");
        // Earlier-captured, non-'start' source must be ignored even though it's earlier.
        SeedSpawnStat("plant", 0, "P1", "setPlantAttributes", "2020-01-01T00:00:00Z", hpBase: 999, attackBase: 999);
        SeedSpawnStat("plant", 0, "P2", "start", "2020-01-02T00:00:00Z", hpBase: 300, attackBase: 20);

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0);

        Assert.True(dto!.StatsObserved);
        Assert.Equal(300, dto.Hp);
        Assert.Equal(20, dto.Attack);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("initHealth")]
    public void Baseline_selection_zombie_accepts_start_or_initHealth(string source)
    {
        SeedDump("zombie", 0, name: "X");
        SeedSpawnStat("zombie", 0, "Z1", source, "2020-01-01T00:00:00Z", hpBase: 270, attackBase: 50, armorBase: 0, armorMaxBase: 0);

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("zombie", 0);

        Assert.True(dto!.StatsObserved);
        Assert.Equal(270, dto.Hp);
        Assert.Equal(50, dto.Attack);
    }

    [Fact]
    public void Baseline_selection_zombie_earliest_across_both_allowed_sources_wins()
    {
        SeedDump("zombie", 0, name: "X");
        SeedSpawnStat("zombie", 0, "Z1", "initHealth", "2020-01-02T00:00:00Z", hpBase: 999, attackBase: 999);
        SeedSpawnStat("zombie", 0, "Z2", "start", "2020-01-01T00:00:00Z", hpBase: 270, attackBase: 50);
        // A noisy non-baseline source, earlier than both — must never win.
        SeedSpawnStat("zombie", 0, "Z3", "reinforce", "2019-01-01T00:00:00Z", hpBase: 1, attackBase: 1);

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("zombie", 0);

        Assert.Equal(270, dto!.Hp);
        Assert.Equal(50, dto.Attack);
    }

    [Fact]
    public void Unobserved_type_stats_null_never_falls_back_to_types_hpbase()
    {
        SeedDump("plant", 0, name: "X"); // side effect: also upserts a `types` row for (plant, 0)
        // Seed types.hp_base directly to prove the rebuild never reads it.
        using (var hot = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var cmd = hot.CreateCommand();
            cmd.CommandText = "UPDATE types SET hp_base=9999 WHERE game=$g AND side='plant' AND type=0;";
            cmd.Parameters.AddWithValue("$g", RpgConstants.GameId);
            cmd.ExecuteNonQuery();
        }

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0);

        Assert.NotNull(dto);
        Assert.False(dto!.StatsObserved);
        Assert.Null(dto.Hp);
        Assert.Null(dto.Attack);
        Assert.Null(dto.Armor);
        Assert.Null(dto.ArmorMax);
    }

    [Fact]
    public void Plant_armor_always_null_even_when_stats_observed()
    {
        SeedDump("plant", 0, name: "X");
        SeedSpawnStat("plant", 0, "P1", "start", "2020-01-01T00:00:00Z", hpBase: 300, attackBase: 20);

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0);

        Assert.True(dto!.StatsObserved);
        Assert.Null(dto.Armor);
        Assert.Null(dto.ArmorMax);
    }

    [Fact]
    public void Zombie_armor_populated_when_observed()
    {
        SeedDump("zombie", 0, name: "X");
        SeedSpawnStat("zombie", 0, "Z1", "initHealth", "2020-01-01T00:00:00Z", hpBase: 270, attackBase: 50, armorBase: 100, armorMaxBase: 100);

        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("zombie", 0);

        Assert.Equal(100, dto!.Armor);
        Assert.Equal(100, dto.ArmorMax);
    }

    [Fact]
    public void Rebuild_transaction_rolls_back_on_malformed_row()
    {
        SeedDump("plant", 0, name: "Good1");
        SeedDump("plant", 1, name: "Good2");
        _store.RebuildAlmanacSeed();
        Assert.Equal(2, _store.ListAlmanacSeed().Count);

        // Corrupt one row's fields_json directly, bypassing the public API.
        using (var media = SqliteConnectionFactory.Open(_store.MediaPath))
        {
            using var cmd = media.CreateCommand();
            cmd.CommandText = "UPDATE type_almanac_dump SET fields_json='not valid json' WHERE side='plant' AND type_id=1;";
            cmd.ExecuteNonQuery();
        }
        SeedDump("plant", 2, name: "Good3");

        Assert.ThrowsAny<Exception>(() => _store.RebuildAlmanacSeed());

        // Table must be exactly as it was before the failed rebuild (rollback).
        var after = _store.ListAlmanacSeed();
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, r => r.TypeId == 2);
    }

    [Fact]
    public void Stale_row_removed_after_dump_deleted()
    {
        SeedDump("plant", 0, name: "X");
        SeedDump("plant", 1, name: "Y");
        _store.RebuildAlmanacSeed();
        Assert.Equal(2, _store.ListAlmanacSeed().Count);

        using (var media = SqliteConnectionFactory.Open(_store.MediaPath))
        {
            using var cmd = media.CreateCommand();
            cmd.CommandText = "DELETE FROM type_almanac_dump WHERE side='plant' AND type_id=1;";
            cmd.ExecuteNonQuery();
        }

        var summary = _store.RebuildAlmanacSeed();

        Assert.Equal(1, summary.StaleRemoved);
        var after = _store.ListAlmanacSeed();
        Assert.Single(after);
        Assert.Equal(0, after[0].TypeId);
    }

    [Fact]
    public void Rebuild_idempotent_on_unchanged_source_data()
    {
        SeedDump("plant", 0, name: "豌豆射手", enumName: "Peashooter",
            cost: "花费：<color=red>100</color>\n冷却时间：<color=red>7.5秒</color>");
        SeedSpawnStat("plant", 0, "P1", "start", "2020-01-01T00:00:00Z", hpBase: 300, attackBase: 20);

        _store.RebuildAlmanacSeed();
        var first = _store.GetAlmanacSeed("plant", 0)!;
        _store.RebuildAlmanacSeed();
        var second = _store.GetAlmanacSeed("plant", 0)!;

        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.Equal(first.TypeName, second.TypeName);
        Assert.Equal(first.SunCost, second.SunCost);
        Assert.Equal(first.CooldownSec, second.CooldownSec);
        Assert.Equal(first.CostStatus, second.CostStatus);
        Assert.Equal(first.Hp, second.Hp);
        Assert.Equal(first.Attack, second.Attack);
        Assert.Equal(first.StatsObserved, second.StatsObserved);
    }

    [Fact]
    public void Naming_falls_back_to_types_on_read_without_rerebuild()
    {
        SeedDump("plant", 0, name: "OldName", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();
        Assert.Equal("OldName", _store.GetAlmanacSeed("plant", 0)!.DisplayName);

        using (var hot = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var cmd = hot.CreateCommand();
            cmd.CommandText = "UPDATE types SET display_name='NewCorrectedName' WHERE game=$g AND side='plant' AND type=0;";
            cmd.Parameters.AddWithValue("$g", RpgConstants.GameId);
            cmd.ExecuteNonQuery();
        }

        // No re-rebuild — read must reflect the live `types` correction immediately.
        Assert.Equal("NewCorrectedName", _store.GetAlmanacSeed("plant", 0)!.DisplayName);
    }

    [Fact]
    public void ContractVersion_and_rebuiltUtc_stamped_on_every_row()
    {
        SeedDump("plant", 0, name: "X");
        _store.RebuildAlmanacSeed();
        var dto = _store.GetAlmanacSeed("plant", 0)!;

        Assert.Equal(RpgStore.AlmanacSeedContractVersion, dto.ContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(dto.RebuiltUtc));
    }

    [Fact]
    public void List_filters_by_side()
    {
        SeedDump("plant", 0, name: "P");
        SeedDump("zombie", 0, name: "Z");
        _store.RebuildAlmanacSeed();

        var plants = _store.ListAlmanacSeed("plant");
        Assert.Single(plants);
        Assert.All(plants, r => Assert.Equal("plant", r.Side));
    }
}
