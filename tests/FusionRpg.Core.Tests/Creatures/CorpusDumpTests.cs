using System.Text;
using System.Text.Json.Nodes;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Data;
using FusionRpg.Tools.CreatureCorpusDump;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// `creature-seed` module 1 (spec-corpus-dump.md). Fixtures are a small in-memory RpgStore, not the
/// 520MB live database, per the module's own testing strategy.
/// </summary>
[Trait("VerificationId", "core.creature-corpus-dump")]
public class CorpusDumpTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // The dump TREE is the thing this class tests (a real output tree on disk), so the output root
    // stays a real dir while the store itself runs in memory.
    readonly string _dir;

    public CorpusDumpTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _dir = Path.Combine(Path.GetTempPath(), "fusionrpg-corpus-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _testStore.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    void SeedDump(string side, int typeId, string? name = null, string? enumName = null, string? info = null)
    {
        var fields = new Dictionary<string, string?>();
        if (name != null) fields["name"] = name;
        if (enumName != null) fields["enumName"] = enumName;
        if (info != null) fields["info"] = info;
        _store.UpsertAlmanacTextDump(side, typeId, fields, null);
    }

    [Fact]
    public void Dump_is_byte_identical_on_rerun()
    {
        SeedDump("plant", 1, name: "豌豆射手", enumName: "Peashooter");
        SeedDump("zombie", 1, name: "旗帜僵尸", enumName: "FlagZombie");
        _store.RebuildAlmanacSeed();

        var payloadA = CorpusReader.BuildPayload(_store);
        var treeA = DumpWriter.BuildTree(payloadA, CorpusReader.CapturedUtc(payloadA));

        var payloadB = CorpusReader.BuildPayload(_store);
        var treeB = DumpWriter.BuildTree(payloadB, CorpusReader.CapturedUtc(payloadB));

        Assert.Equal(treeA.Manifest.ContentHash, treeB.Manifest.ContentHash);
        Assert.True(treeA.PlantAlmanacBytes.AsSpan().SequenceEqual(treeB.PlantAlmanacBytes));
        Assert.True(treeA.ZombieAlmanacBytes.AsSpan().SequenceEqual(treeB.ZombieAlmanacBytes));
        Assert.True(treeA.ManifestBytes.AsSpan().SequenceEqual(treeB.ManifestBytes));
    }

    [Fact]
    public void Dump_covers_every_almanac_row()
    {
        // Five species across both sides — none of them touch CreatureSpeciesCatalog at all. That
        // absence is the regression proof for the defect this module fixes (CreatureCorpusEmit
        // walked CreatureSpeciesCatalog.All and could never see a row the C# generator hadn't
        // already picked); CorpusReader only ever calls RpgStore.ListAlmanacSeed().
        for (var i = 0; i < 3; i++) SeedDump("plant", i, name: $"plant-{i}", enumName: $"Plant{i}");
        for (var i = 0; i < 2; i++) SeedDump("zombie", i, name: $"zombie-{i}", enumName: $"Zombie{i}");
        _store.RebuildAlmanacSeed();

        var payload = CorpusReader.BuildPayload(_store);
        var expected = _store.ListAlmanacSeed().Count;

        Assert.Equal(5, expected);
        Assert.Equal(expected, payload.PlantAlmanac.Count + payload.ZombieAlmanac.Count);
        Assert.Equal(3, payload.PlantAlmanac.Count);
        Assert.Equal(2, payload.ZombieAlmanac.Count);
    }

    [Fact]
    public void Manifest_hash_changes_when_any_payload_byte_changes()
    {
        var rowA = new DumpAlmanacRow("plant", 1, "Peashooter", "豌豆射手", null, null, null, null, "absent",
            300, 20, null, null, false, 1, "2026-01-01T00:00:00Z", null);
        var payloadA = new DumpPayload(new[] { rowA }, Array.Empty<DumpAlmanacRow>(), Array.Empty<DumpSpawnBaseline>(), Array.Empty<DumpRecipe>());
        var treeA = DumpWriter.BuildTree(payloadA, "2026-01-01T00:00:00Z");

        // Flip exactly one field (Hp: 300 -> 301) and nothing else.
        var rowB = rowA with { Hp = 301 };
        var payloadB = new DumpPayload(new[] { rowB }, Array.Empty<DumpAlmanacRow>(), Array.Empty<DumpSpawnBaseline>(), Array.Empty<DumpRecipe>());
        var treeB = DumpWriter.BuildTree(payloadB, "2026-01-01T00:00:00Z");

        Assert.NotEqual(treeA.Manifest.ContentHash, treeB.Manifest.ContentHash);
    }

    [Fact]
    public void Cjk_names_are_not_escaped()
    {
        var row = new DumpAlmanacRow("plant", 1, "Peashooter", "豌豆射手", "发射豌豆。", null, null, null, "absent",
            null, null, null, null, false, 1, "2026-01-01T00:00:00Z", null);
        var bytes = DumpWriter.RenderAlmanac(new[] { row });
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("豌豆射手", text);
        Assert.Contains("发射豌豆。", text);
        Assert.DoesNotContain("\\u", text);
    }

    [Fact]
    public void Null_and_absent_hash_identically_is_false()
    {
        var rowWithNull = new DumpAlmanacRow("plant", 1, null, null, null, null, null, null, "absent",
            null, null, null, null, false, 1, "2026-01-01T00:00:00Z", null);
        var bytesWithNull = DumpWriter.RenderAlmanac(new[] { rowWithNull });
        var textWithNull = Encoding.UTF8.GetString(bytesWithNull);

        // The rule this module enforces: null is written explicitly, never omitted.
        Assert.Contains("\"typeName\": null", textWithNull);

        // Prove the two forms are NOT hash-identical — build the same object by hand with the
        // key omitted entirely, and show its bytes (and therefore its hash) differ from the
        // explicit-null render above. This is why DumpWriter always assigns null explicitly
        // rather than skipping the key: if it ever did, this test would catch the collapse.
        var arrayWithKeyOmitted = new JsonArray();
        var objOmitted = new JsonObject
        {
            ["armor"] = null, ["armorMax"] = null, ["attack"] = null, ["contractVersion"] = 1,
            ["cooldownSec"] = null, ["costStatus"] = "absent",
            // "displayName" intentionally omitted (would be null if present)
            ["enrichment"] = null, ["flavorInfo"] = null, ["flavorIntroduce"] = null, ["hp"] = null,
            ["rebuiltUtc"] = "2026-01-01T00:00:00Z", ["side"] = "plant", ["statsObserved"] = false,
            ["sunCost"] = null, ["typeId"] = 1
            // "typeName" intentionally omitted too
        };
        arrayWithKeyOmitted.Add(objOmitted);
        var omittedBytes = Encoding.UTF8.GetBytes(arrayWithKeyOmitted.ToJsonString());

        Assert.NotEqual(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytesWithNull)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(omittedBytes)));
    }

    [Fact]
    public void Check_mode_exits_1_when_committed_tree_is_stale()
    {
        SeedDump("plant", 1, name: "豌豆射手", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        var payload = CorpusReader.BuildPayload(_store);
        var tree = DumpWriter.BuildTree(payload, CorpusReader.CapturedUtc(payload));

        var outputRoot = Path.Combine(_dir, "_dump");
        DumpWriter.WriteToDisk(outputRoot, tree);

        // Fresh: matches, which is what lets Program.cs's --check branch return 0.
        Assert.True(DumpWriter.MatchesDisk(outputRoot, tree));

        // Add one more species to the live store without regenerating the committed tree —
        // exactly what a stale commit looks like.
        SeedDump("plant", 2, name: "向日葵", enumName: "Sunflower");
        _store.RebuildAlmanacSeed();
        var freshPayload = CorpusReader.BuildPayload(_store);
        var freshTree = DumpWriter.BuildTree(freshPayload, CorpusReader.CapturedUtc(freshPayload));

        // Stale: does not match, which is what makes Program.cs's --check branch return 1.
        Assert.False(DumpWriter.MatchesDisk(outputRoot, freshTree));
    }

    [Fact]
    public void Verify_mode_catches_a_tampered_committed_file()
    {
        // --verify is the CI-safe, DB-free mode (T1.2 amendment) — CI has no populated hot.sqlite
        // (decisions.md: no real game/Harmony in CI), so it can only re-hash what is already on
        // disk against the manifest's declared hash, never regenerate from a live database.
        SeedDump("plant", 1, name: "豌豆射手", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();
        var payload = CorpusReader.BuildPayload(_store);
        var tree = DumpWriter.BuildTree(payload, CorpusReader.CapturedUtc(payload));

        var outputRoot = Path.Combine(_dir, "_dump");
        DumpWriter.WriteToDisk(outputRoot, tree);

        var (okFresh, _) = DumpWriter.VerifyCommittedTree(outputRoot);
        Assert.True(okFresh);

        File.AppendAllText(Path.Combine(outputRoot, "recipes.json"), "tampered");
        var (okTampered, reasonTampered) = DumpWriter.VerifyCommittedTree(outputRoot);
        Assert.False(okTampered);
        Assert.Contains("hash mismatch", reasonTampered);
    }

    // --- type_base_stats: the game's own static table, committed (creature-seed R-CS1) ------------

    static readonly (string Side, int TypeId, string Name, int Hp, int Atk)[] BaseStatRows =
    {
        ("plant", 0, "Peashooter", 300, 20),
        ("plant", 2, "CherryBomb", 300, 1800),
        ("zombie", 0, "NormalZombie", 270, 50),
    };

    static List<DumpTypeBaseStats> SampleBaseStats() => BaseStatRows
        .Select(r => new DumpTypeBaseStats(
            r.Side, r.TypeId, r.Name,
            $$"""{"side":"{{r.Side}}","typeId":{{r.TypeId}},"hpBase":{{r.Hp}},"attackBase":{{r.Atk}}}""",
            "2026-09-17T14:50:55.7671960Z"))
        .ToList();

    [Fact]
    public void Base_stat_export_is_byte_identical_across_runs_and_independent_of_input_order()
    {
        // The committed-capture contract: the same table must produce the same bytes, or a
        // generator reading it cannot be regenerated and byte-compared in CI at all.
        var ordered = DumpWriter.RenderTypeBaseStatsFile(
            DumpWriter.BuildTypeBaseStatsFile(SampleBaseStats()));
        var shuffled = SampleBaseStats();
        shuffled.Reverse();
        var reordered = DumpWriter.RenderTypeBaseStatsFile(DumpWriter.BuildTypeBaseStatsFile(shuffled));

        Assert.Equal(Encoding.UTF8.GetString(ordered), Encoding.UTF8.GetString(reordered));
    }

    [Fact]
    public void Base_stat_export_carries_the_capture_stamp_from_its_rows_never_wall_clock_time()
    {
        // Same rule the almanac tree holds: a dump that stamped itself "now" would claim a capture
        // that never happened, and a later re-capture could not be told apart from a re-render.
        var file = DumpWriter.BuildTypeBaseStatsFile(SampleBaseStats());
        Assert.Equal("2026-09-17T14:50:55.7671960Z", file.CapturedUtc);
    }

    [Fact]
    public void Base_stat_export_is_not_folded_into_the_four_file_manifest_hash()
    {
        // Deliberate: the almanac/baseline/recipe files are one snapshot and the static sweep is an
        // independent one. Folding them together would force regenerating four unrelated files —
        // and would invalidate every anchor's recorded dumpHash — to publish a capture that shares
        // none of their rows.
        SeedDump("plant", 1, name: "豌豆射手", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();
        var payload = CorpusReader.BuildPayload(_store);
        var before = DumpWriter.BuildTree(payload, CorpusReader.CapturedUtc(payload)).Manifest.ContentHash;

        var outputRoot = Path.Combine(_dir, "_dump");
        DumpWriter.WriteToDisk(outputRoot, DumpWriter.BuildTree(payload, CorpusReader.CapturedUtc(payload)));
        DumpWriter.WriteTypeBaseStats(outputRoot,
            DumpWriter.RenderTypeBaseStatsFile(DumpWriter.BuildTypeBaseStatsFile(SampleBaseStats())));

        var after = DumpWriter.BuildTree(payload, CorpusReader.CapturedUtc(payload)).Manifest.ContentHash;
        Assert.Equal(before, after);
        Assert.True(DumpWriter.VerifyCommittedTree(outputRoot).Ok);
    }

    [Fact]
    public void Base_stat_verify_catches_a_tampered_committed_capture()
    {
        var outputRoot = Path.Combine(_dir, "_dump");
        Directory.CreateDirectory(outputRoot);
        var rendered = DumpWriter.RenderTypeBaseStatsFile(DumpWriter.BuildTypeBaseStatsFile(SampleBaseStats()));
        DumpWriter.WriteTypeBaseStats(outputRoot, rendered);

        var (okFresh, _) = DumpWriter.VerifyCommittedTypeBaseStats(outputRoot);
        Assert.True(okFresh);

        var path = Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName);
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        ((JsonObject)node["entries"]![0]!)["typeName"] = "NotPeashooter";
        File.WriteAllText(path, node.ToJsonString());

        var (okTampered, reasonTampered) = DumpWriter.VerifyCommittedTypeBaseStats(outputRoot);
        Assert.False(okTampered);
        Assert.Contains("hash mismatch", reasonTampered);
    }

    [Fact]
    public void Base_stat_verify_fails_when_the_capture_is_absent_rather_than_passing_silently()
    {
        var outputRoot = Path.Combine(_dir, "_dump");
        Directory.CreateDirectory(outputRoot);
        var (ok, reason) = DumpWriter.VerifyCommittedTypeBaseStats(outputRoot);
        Assert.False(ok);
        Assert.Contains(DumpWriter.TypeBaseStatsFileName, reason);
    }
}
