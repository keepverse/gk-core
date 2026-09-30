using System.Text.Json;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `drop-volume` (item module 11) at the DAL — the eight tables, step 11's ONE transaction, the
/// idempotency key, and the two schema facts the spec calls out by name (pity keyed on rung ids,
/// `item_generation` with no `socket_count`). Driven with the REAL shipped loot corpus.
/// </summary>
public class DropTableStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public DropTableStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static DropVolumeTuning Tuning() => DropVolumeTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json")));

    static LootCorpus ShippedCorpus() => LootCorpusReader.Merge(
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "loot"), "tables*.json")
            .Select(p => LootCorpusReader.Parse(File.ReadAllText(p))));

    static LootManifest Manifest(string correlationId, params LootGrant[] grants) =>
        new(correlationId, "drop.web.wave-normal", 0xABCDEF, 7, grants, new[] { "envelope_narrowed" },
            "{\"smartLoot\":false,\"squadFrameMix\":{}}",
            new LootPityState(2, 3), new LootPityState(0, 4), null, false, null);

    // ---- the corpus round-trips ------------------------------------------------------------------

    [Fact]
    public void The_shipped_corpus_imports_and_reads_back_identically()
    {
        var corpus = ShippedCorpus();
        _store.ImportLootCorpus(corpus, Tuning());

        var loaded = _store.LoadLootCorpus();
        Assert.Equal(corpus.Tables.Count, loaded.Tables.Count);
        Assert.Equal(corpus.Sources.Count, loaded.Sources.Count);

        var before = corpus.Tables.OrderBy(t => t.TableId, StringComparer.Ordinal).ToList();
        var after = loaded.Tables.OrderBy(t => t.TableId, StringComparer.Ordinal).ToList();
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].TableId, after[i].TableId);
            Assert.Equal(before[i].SourceAllow, after[i].SourceAllow);
            Assert.Equal(before[i].Groups.Count, after[i].Groups.Count);
            Assert.Equal(
                before[i].Groups.Sum(g => g.Entries.Count),
                after[i].Groups.Sum(g => g.Entries.Count));
        }

        // The calibration survives the round trip — the yields are the store's, not just the file's.
        var byId = loaded.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);
        Assert.Equal(550, DropTableDraw.ExpectedEquipmentPerMille(
            byId["drop.web.wave-normal"], 3, 1000, id => byId.TryGetValue(id, out var x) ? x : null));
        Assert.Equal(4200, DropTableDraw.ExpectedEquipmentPerMille(
            byId["drop.exp.warpath-20h"], 10, 1000, id => byId.TryGetValue(id, out var x) ? x : null));
    }

    [Fact]
    public void Affix_channel_survives_the_round_trip()
    {
        _store.ImportLootCorpus(ShippedCorpus(), Tuning());
        var loaded = _store.LoadLootCorpus().Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);

        Assert.All(loaded["drop.shared.hybrid-core-boss"].Groups.Single().Entries,
            e => Assert.Equal(AffixChannels.Boss, e.AffixChannel));
        Assert.All(loaded["drop.shared.hybrid-core-any"].Groups.Single().Entries,
            e => Assert.Equal(AffixChannels.Drop, e.AffixChannel));
    }

    [Fact]
    public void A_corpus_that_fails_validation_imports_nothing()
    {
        // E14: one bad row and NOTHING is imported. Prove it against a store that already holds a
        // good corpus — a partial import would be visible as a table count change.
        _store.ImportLootCorpus(ShippedCorpus(), Tuning());
        var before = _store.LoadLootCorpus().Tables.Count;

        var bad = ShippedCorpus();
        var broken = bad.Tables.Append(new DropTableRow(
            "drop.bad.injector-only", new[] { "injector" }, null, null, true, 1,
            new[] { new DropTableGroupRow("g", 0, 1, new[] { new DropTableEntryRow(0, DropEntryKind.Nothing, "", 1) }) })).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _store.ImportLootCorpus(new LootCorpus(bad.Sources, broken), Tuning()));
        Assert.Contains("standalone-rule-violation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, _store.LoadLootCorpus().Tables.Count);
    }

    // ---- step 11: one transaction, one row per correlation ---------------------------------------

    [Fact]
    public void A_retry_mints_nothing()
    {
        var grant = new LootGrant(0, DropEntryKind.Equipment, "", 1, AffixChannels.Drop,
            "item.plant-girdle-a-001", "plant", "girdle", "fused", 50, 7, 2, 4, 1, 1, 0, 99UL, false, false, "inst-1");
        var manifest = Manifest("loot:rift-warband", grant);
        var gen = new[] { new ItemGenerationRow("inst-1", 0, "item.plant-girdle-a-001", 50, 7, "plant", "girdle", "drop") };

        var first = _store.PersistLoot("p1", manifest, "web-wave", "rift-warband", 1, 1, gen);
        var second = _store.PersistLoot("p1", manifest, "web-wave", "rift-warband", 1, 1, gen);

        Assert.Equal(first, second);
        Assert.Single(_store.ListDropLog("p1"));
        Assert.Single(_store.ListGenerations(first));

        // And the gate the pipeline reads returns the recorded manifest.
        Assert.NotNull(_store.RecordedLootManifest("p1", "loot:rift-warband"));
        Assert.Null(_store.RecordedLootManifest("p1", "loot:rift-tyrant"));
        Assert.Null(_store.RecordedLootManifest("p2", "loot:rift-warband"));
    }

    [Fact]
    public void Persist_is_one_transaction()
    {
        // A forced failure mid-persist must leave NO log row, NO generation row, NO pity update and
        // NO first-clear mark. The forced failure is a duplicate instance_id on the second stamp,
        // which trips the PK inside the same transaction as the log insert.
        var manifest = Manifest("loot:tx") with { FirstClearGrant = "item.first-clear-almanac-seed" };
        var gen = new[]
        {
            new ItemGenerationRow("dup", 0, "item.a", 30, 5, "plant", "girdle", "drop"),
            new ItemGenerationRow("dup", 0, "item.b", 30, 5, "plant", "girdle", "drop"),
        };

        Assert.ThrowsAny<Exception>(() =>
            _store.PersistLoot("p1", manifest, "web-wave", "rift-warband", 1, 1, gen));

        Assert.Empty(_store.ListDropLog("p1"));
        Assert.Equal(LootPityState.Empty, _store.GetLootPity("p1"));
        Assert.False(_store.HasFirstClear("p1", "web-wave", "rift-warband"));
    }

    [Fact]
    public void Pity_and_first_clear_land_inside_the_same_transaction()
    {
        var manifest = Manifest("loot:fc") with { FirstClearGrant = "item.first-clear-almanac-seed" };
        _store.PersistLoot("p1", manifest, "web-wave", "rift-tyrant", 1, 1, Array.Empty<ItemGenerationRow>());

        Assert.Equal(new LootPityState(0, 4), _store.GetLootPity("p1"));
        Assert.True(_store.HasFirstClear("p1", "web-wave", "rift-tyrant"));
        Assert.False(_store.HasFirstClear("p2", "web-wave", "rift-tyrant"));
    }

    // ---- the two named schema facts ---------------------------------------------------------------

    [Fact]
    public void Item_generation_has_no_socket_count_column()
    {
        // Closed by SCHEMA rather than by convention: three copies of a socket count is how one
        // silently disagrees with the sockets an item has.
        Assert.DoesNotContain("socket_count", Columns("item_generation"));
    }

    [Fact]
    public void Pity_columns_are_keyed_on_rung_ids()
    {
        var columns = Columns("item_loot_pity");
        Assert.Contains("items_since_heirloom", columns);
        Assert.Contains("items_since_sunwoven", columns);
        Assert.DoesNotContain("items_since_r4", columns);
        Assert.DoesNotContain("items_since_r6", columns);
    }

    [Fact]
    public void All_eight_tables_exist()
    {
        foreach (var name in new[]
                 {
                     "loot_source", "drop_table", "drop_table_group", "drop_table_entry",
                     "item_drop_log", "item_generation", "item_loot_pity", "item_first_clear",
                 })
            Assert.NotEmpty(Columns(name));
    }

    // ---- the measurement, and the tail trim -------------------------------------------------------

    [Fact]
    public void The_inflow_measurement_the_loot_filter_needs_is_queryable()
    {
        // I12 §8's `40/day` tripwire asks for a loot FILTER (module 20), not a cap. What this module
        // owes it is the measurement — and the measurement never gates anything.
        for (var i = 0; i < 5; i++)
        {
            var gen = new[]
            {
                new ItemGenerationRow($"inst-{i}", 0, "item.a", 30, 5, "plant", "girdle", "drop"),
            };
            _store.PersistLoot("p1", Manifest($"loot:e{i}"), "web-wave", "rift-warband", 1, 1, gen,
                nowUtc: "2026-09-04T10:00:0" + i + "Z");
        }

        Assert.Equal(5, _store.CountEquipmentMinted("p1", "2026-09-04T00:00:00Z"));
        Assert.Equal(0, _store.CountEquipmentMinted("p1", "2026-09-05T00:00:00Z"));
        Assert.Equal(0, _store.CountEquipmentMinted("p2", "2026-09-04T00:00:00Z"));
    }

    [Fact]
    public void The_tail_trim_drops_the_payload_and_keeps_the_row()
    {
        _store.PersistLoot("p1", Manifest("loot:old"), "web-wave", "rift-warband", 1, 1,
            new[] { new ItemGenerationRow("old-1", 0, "item.a", 30, 5, "plant", "girdle", "drop") },
            nowUtc: "2026-01-01T00:00:00Z");
        _store.PersistLoot("p1", Manifest("loot:new"), "web-wave", "rift-tyrant", 1, 1,
            new[] { new ItemGenerationRow("new-1", 0, "item.b", 30, 5, "plant", "girdle", "drop") },
            nowUtc: "2026-09-04T00:00:00Z");

        Assert.Equal(1, _store.TrimDropLog("2026-06-01T00:00:00Z"));

        var rows = _store.ListDropLog("p1").ToDictionary(r => r.CorrelationId, StringComparer.Ordinal);
        Assert.Equal(2, rows.Count);                         // the ROW survives — inflow stays queryable
        Assert.Equal("{}", rows["loot:old"].ContextJson);
        Assert.Equal("[]", rows["loot:old"].ResultJson);
        Assert.NotEqual("{}", rows["loot:new"].ContextJson);

        // item_generation is the permanent record and is never touched by the trim.
        Assert.Equal(2, _store.CountEquipmentMinted("p1", "2020-01-01T00:00:00Z"));
    }

    // ---- end to end: the pipeline over the store --------------------------------------------------

    [Fact]
    public void The_pipeline_resolves_over_the_stored_corpus_and_persists_once()
    {
        _store.ImportLootCorpus(ShippedCorpus(), Tuning());
        var corpus = _store.LoadLootCorpus();

        var view = new LootContentView(
            corpus.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal),
            corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal),
            Ladder(),
            (_, _) => new[] { "item.test-a", "item.test-b" },
            (p, k, i) => _store.HasFirstClear(p, k, i),
            (p, c) => _store.RecordedLootManifest(p, c));

        // species-gear-chain T29: this manifest is the REAL pipeline's own drop, and its grants include
        // a `currency: souls` entry. The credit path keys on the player's NUMERIC id (the drop log
        // carries it as text), so this uses the store's real player key rather than a placeholder.
        var player = _store.GetCurrentPlayerId();
        var playerKey = player.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = new LootRequest(playerKey, "web-wave", "rift-tyrant", 0x5EED, 20);
        Assert.True(LootPipeline.Resolve(request, view, Tuning(), _store.GetLootPity(playerKey), out var m).IsOk);
        Assert.False(m!.Replayed);

        var gens = m.Grants
            .Where(g => g.Kind == DropEntryKind.Equipment && g.BaseTypeId is not null)
            .Select((g, i) => new ItemGenerationRow($"inst-{i}", 0, g.BaseTypeId!, g.RarityOrdinal,
                g.ItemLevel, g.Frame!, g.Role!, g.AffixChannel))
            .ToList();

        var logId = _store.PersistLoot(playerKey, m, "web-wave", "rift-tyrant", 1, 1, gens);
        Assert.Equal(gens.Count, _store.ListGenerations(logId).Count);

        // T29's credit, from the pipeline's own grant list: every currency unit the drop decided is on
        // the shelf after the persist (zero counts stay zero, so this is exact in both directions).
        var souls = m.Grants.Where(g => g.Kind == DropEntryKind.Currency).Sum(g => g.Count);
        Assert.Equal(souls, _store.GetSoulBalance(player).Balance);

        // The second call hits the gate and mints nothing, and credits nothing twice.
        Assert.True(LootPipeline.Resolve(request, view, Tuning(), _store.GetLootPity(playerKey), out var replay).IsOk);
        Assert.True(replay!.Replayed);
        Assert.Equal(logId, _store.PersistLoot(playerKey, m, "web-wave", "rift-tyrant", 1, 1, gens));
        Assert.Single(_store.ListDropLog(playerKey));
        Assert.Equal(souls, _store.GetSoulBalance(player).Balance);
    }

    static IReadOnlyList<RarityRung> Ladder()
    {
        var tuning = FusionRpg.Core.Items.ItemRarityTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-rarity.v1.json")));
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "rarity", "ladder.v1.json")));

        var rungs = new List<RarityRung>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            rungs.Add(new RarityRung(id, e.GetProperty("ordinal").GetInt32(),
                e.GetProperty("prefixRolls").GetInt32(), e.GetProperty("suffixRolls").GetInt32(),
                e.GetProperty("minTier").GetInt32(), e.GetProperty("maxTier").GetInt32(),
                tuning[id].DropWeightPer100k));
        }

        return rungs;
    }

    IReadOnlyList<string> Columns(string table)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }

    // ---- species-gear-chain T30b: the parametric trophy directive's persisted shape ---------------

    [Fact]
    public void The_two_trophy_columns_exist_on_drop_table_entry()
    {
        _store.ImportLootCorpus(ShippedCorpus(), Tuning());
        var columns = Columns("drop_table_entry");
        Assert.Contains("trophy_scope", columns);
        Assert.Contains("trophy_slot", columns);
    }

    [Fact]
    public void A_trophy_directive_round_trips_through_the_store()
    {
        var corpus = ShippedCorpus();
        var withTrophy = corpus.Tables.Append(new DropTableRow(
            "drop.test.trophy-roundtrip", new[] { "web" }, null, null, true, 1, new[]
            {
                new DropTableGroupRow("g", 0, 1, new[]
                {
                    new DropTableEntryRow(0, DropEntryKind.Material, "", 100, TrophyScope: "species", TrophySlot: 1),
                    new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
                }),
            })).ToList();

        _store.ImportLootCorpus(new LootCorpus(corpus.Sources, withTrophy), Tuning());
        var loaded = _store.LoadLootCorpus().Tables
            .Single(t => t.TableId == "drop.test.trophy-roundtrip");
        var entry = loaded.Groups.Single().Entries.Single(e => e.Kind == DropEntryKind.Material);

        Assert.Equal("species", entry.TrophyScope);
        Assert.Equal(1, entry.TrophySlot);
        Assert.Equal("", entry.RefId);
    }

    [Fact]
    public void An_ordinary_entry_persists_null_trophy_columns()
    {
        // A row authored without the trophy concept must round-trip with both columns null -- never a
        // sentinel like 0 or "" that a later read could confuse with a real scope/slot. A SYNTHETIC
        // fixture, not the real shipped corpus: this test originally scanned the whole shipped corpus
        // because at T30b's own time of writing it, zero shipped rows carried a trophy scope. T34c gave
        // the real corpus real `species-trophy`/`family-trophy` groups (species-gear-chain), so "every
        // row in the shipped corpus has no trophy scope" is no longer true of the corpus -- it is still
        // true of THIS table, which is exactly what an ordinary entry's own round trip needs proven.
        var corpus = ShippedCorpus();
        var ordinaryOnly = corpus.Tables.Append(new DropTableRow(
            "drop.test.ordinary-roundtrip", new[] { "web" }, null, null, true, 1, new[]
            {
                new DropTableGroupRow("g", 0, 1, new[]
                {
                    new DropTableEntryRow(0, DropEntryKind.Material, "material.001", 100),
                    new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
                }),
            })).ToList();

        _store.ImportLootCorpus(new LootCorpus(corpus.Sources, ordinaryOnly), Tuning());
        var loaded = _store.LoadLootCorpus().Tables
            .Single(t => t.TableId == "drop.test.ordinary-roundtrip");

        Assert.All(loaded.Groups.SelectMany(g => g.Entries),
            e => { Assert.Null(e.TrophyScope); Assert.Null(e.TrophySlot); });
    }
}
