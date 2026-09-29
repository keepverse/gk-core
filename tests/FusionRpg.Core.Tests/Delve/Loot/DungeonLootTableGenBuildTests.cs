using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>
/// D3.15 (spec-dungeon-loot.md, Structure) — the per-(climate, room-kind) table generator half of
/// <see cref="DungeonLootTableGen"/> (the boss-unique group half is <c>DungeonLootTableGenTests.cs</c>,
/// D4.28 — a separate file since the two are independent responsibilities sharing one class).
/// </summary>
public class DungeonLootTableGenBuildTests
{
    static readonly string[] SixClimates = { "fire", "ice", "air", "earth", "light", "dark" };
    static readonly DungeonLootTableGen.Weights RealWeights = new(EquipmentWeight: 300, NothingWeight: 700);

    [Fact]
    public void Null_climates_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DungeonLootTableGen.Build(null!, RealWeights));
    }

    [Fact]
    public void Empty_climates_throws()
    {
        Assert.Throws<ArgumentException>(() => DungeonLootTableGen.Build(Array.Empty<string>(), RealWeights));
    }

    [Fact]
    public void A_blank_climate_throws()
    {
        Assert.Throws<ArgumentException>(() => DungeonLootTableGen.Build(new[] { "fire", " " }, RealWeights));
    }

    [Fact]
    public void Zero_or_negative_equipment_weight_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DungeonLootTableGen.Build(SixClimates, new DungeonLootTableGen.Weights(0, 700)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DungeonLootTableGen.Build(SixClimates, new DungeonLootTableGen.Weights(-1, 700)));
    }

    [Fact]
    public void Negative_nothing_weight_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DungeonLootTableGen.Build(SixClimates, new DungeonLootTableGen.Weights(300, -1)));
    }

    [Fact]
    public void Zero_nothing_weight_is_legal_a_guaranteed_drop_is_a_real_design_not_an_error()
    {
        var tables = DungeonLootTableGen.Build(new[] { "fire" }, new DungeonLootTableGen.Weights(300, 0));
        Assert.Equal(4, tables.Count);
    }

    [Fact]
    public void Six_climates_produce_exactly_twentyfour_tables_one_per_bound_kind()
    {
        var tables = DungeonLootTableGen.Build(SixClimates, RealWeights);
        Assert.Equal(24, tables.Count);
    }

    [Fact]
    public void Every_table_id_matches_the_spec_naming_convention_and_is_unique()
    {
        var tables = DungeonLootTableGen.Build(SixClimates, RealWeights);
        var ids = tables.Select(t => t.TableId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var climate in SixClimates)
        foreach (var kind in DungeonLootTableGen.BoundRoomKinds)
            Assert.Contains($"drop.dungeon.{climate}.{kind}", ids);
    }

    [Fact]
    public void TableId_matches_the_spec_naming_convention_directly()
    {
        Assert.Equal("drop.dungeon.fire.boss", DungeonLootTableGen.TableId("fire", "boss"));
    }

    [Fact]
    public void BoundRoomKinds_is_exactly_the_four_spec_named_kinds()
    {
        Assert.Equal(
            new[] { "fight", "elite", "boss", "cache" },
            DungeonLootTableGen.BoundRoomKinds);
    }

    static DropVolumeTuning Tuning() => Tests.Items.DropVolumeTests.Tuning();

    [Fact]
    public void Every_generated_table_passes_the_real_DropTableValidator()
    {
        var tables = DungeonLootTableGen.Build(SixClimates, RealWeights);
        var lookups = new DropContentLookups(CurrencyExists: id => id == "souls");

        var rejection = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), tables, Tuning(), lookups);
        Assert.True(rejection.IsOk, rejection.Detail);
    }

    [Fact]
    public void Every_generated_table_has_both_frames_represented()
    {
        var tables = DungeonLootTableGen.Build(new[] { "fire" }, RealWeights);
        foreach (var t in tables)
        {
            var frames = t.Groups.SelectMany(g => g.Entries).Where(e => e.Kind == DropEntryKind.Equipment)
                .Select(e => e.Frame).ToList();
            Assert.Contains(nameof(ItemFrame.Plant).ToLowerInvariant(), frames);
            Assert.Contains(nameof(ItemFrame.Humanoid).ToLowerInvariant(), frames);
        }
    }

    [Fact]
    public void ToJson_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DungeonLootTableGen.ToJson(null!));
    }

    [Fact]
    public void ToJson_round_trips_through_the_real_LootCorpusReader_byte_faithfully()
    {
        var tables = DungeonLootTableGen.Build(SixClimates, RealWeights);
        var json = DungeonLootTableGen.ToJson(tables);

        var corpus = LootCorpusReader.Parse(json);
        Assert.Equal(24, corpus.Tables.Count);
        Assert.Empty(corpus.Sources);

        var original = tables.Single(t => t.TableId == "drop.dungeon.fire.boss");
        var roundTripped = corpus.Tables.Single(t => t.TableId == "drop.dungeon.fire.boss");
        Assert.Equal(original.SourceAllow, roundTripped.SourceAllow);
        Assert.Equal(original.Enabled, roundTripped.Enabled);
        Assert.Single(roundTripped.Groups);
        Assert.Equal(original.Groups[0].Entries.Count, roundTripped.Groups[0].Entries.Count);
        for (var i = 0; i < original.Groups[0].Entries.Count; i++)
        {
            var o = original.Groups[0].Entries[i];
            var r = roundTripped.Groups[0].Entries[i];
            Assert.Equal(o.Kind, r.Kind);
            Assert.Equal(o.Weight, r.Weight);
            Assert.Equal(o.Frame, r.Frame);
            Assert.Equal(o.Role, r.Role);
        }
    }

    [Fact]
    public void ToJson_uses_LF_for_the_tracked_generated_seed_contract()
    {
        var json = DungeonLootTableGen.ToJson(DungeonLootTableGen.Build(SixClimates, RealWeights));

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_round_tripped_corpus_also_passes_the_real_validator()
    {
        var tables = DungeonLootTableGen.Build(SixClimates, RealWeights);
        var json = DungeonLootTableGen.ToJson(tables);
        var corpus = LootCorpusReader.Parse(json);

        var lookups = new DropContentLookups(CurrencyExists: id => id == "souls");
        var rejection = DropTableValidator.Validate(corpus.Sources, corpus.Tables, Tuning(), lookups);
        Assert.True(rejection.IsOk, rejection.Detail);
    }
}
