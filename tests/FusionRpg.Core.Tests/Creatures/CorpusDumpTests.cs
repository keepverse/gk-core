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

    /// <summary>Writes a committed-shaped capture whose DECLARED hash is stale, which is the exact
    /// committed-state defect this mode exists to repair: a hash left behind by a renderer that has
    /// since changed, with every payload byte still correct.</summary>
    string SeedStaleBaseStatsHash()
    {
        var outputRoot = Path.Combine(_dir, "_dump");
        Directory.CreateDirectory(outputRoot);
        var rendered = DumpWriter.RenderTypeBaseStatsFile(DumpWriter.BuildTypeBaseStatsFile(SampleBaseStats()));
        var path = Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName);
        var node = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(rendered))!;
        node["contentHash"] = new string('0', 64);
        File.WriteAllText(path, node.ToJsonString());
        return outputRoot;
    }

    [Fact]
    public void Base_stat_rehash_reports_a_stale_declared_hash_rather_than_leaving_it_red_forever()
    {
        // The shape that was blocking `--verify` on the committed tree, measured 2026-10-02: the file was
        // written 2026-09-30 04:18, the LF-renderer fix landed 2026-10-01 22:05, and nothing re-stamped
        // the hash afterwards. A finding the tool can detect but not re-stamp is permanent, so the tool
        // that reports it must also be able to repair it.
        var outputRoot = SeedStaleBaseStatsHash();
        var (beforeOk, _) = DumpWriter.VerifyCommittedTypeBaseStats(outputRoot);
        Assert.False(beforeOk);

        var rehash = DumpWriter.RehashTypeBaseStats(outputRoot);
        Assert.True(rehash.Ok, rehash.Reason);
        Assert.True(rehash.Changed);
        Assert.True(rehash.Written);
        Assert.Equal(new string('0', 64), rehash.DeclaredHash);
        Assert.NotEqual(rehash.DeclaredHash, rehash.RecomputedHash);

        // The repair is only real if the reader that reported it now agrees.
        var (afterOk, afterReason) = DumpWriter.VerifyCommittedTypeBaseStats(outputRoot);
        Assert.True(afterOk, afterReason);
    }

    [Fact]
    public void Base_stat_rehash_moves_the_hash_and_nothing_else()
    {
        // A rehash that rewrites a payload is not a rehash. Asserted field by field rather than by
        // byte-counting, so the guarantee is readable: every other field, and the whole entry table,
        // must come back identical - including capturedUtc, which answers "when did the game write
        // this?" and must never be stamped "now" by a mode that captured nothing.
        var outputRoot = SeedStaleBaseStatsHash();
        var path = Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName);
        var before = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var rehash = DumpWriter.RehashTypeBaseStats(outputRoot);
        Assert.True(rehash.Ok, rehash.Reason);

        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            before.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal),
            after.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in before.Select(kv => kv.Key))
        {
            if (key == "contentHash") continue;
            Assert.True(before[key]!.ToJsonString() == after[key]!.ToJsonString(), $"'{key}' moved");
        }
        Assert.NotEqual(before["contentHash"]!.GetValue<string>(), after["contentHash"]!.GetValue<string>());
    }

    [Fact]
    public void Base_stat_rehash_is_idempotent_because_re_running_must_be_byte_identical()
    {
        // The same rule the manifest rehash holds: an already-current file is a SUCCESS that writes
        // nothing. Churning the file on every run would make it impossible to tell a re-stamp from an
        // unrelated edit, and would break the byte-identical-rerun contract the corpus check relies on.
        var outputRoot = SeedStaleBaseStatsHash();
        Assert.True(DumpWriter.RehashTypeBaseStats(outputRoot).Changed);

        var second = DumpWriter.RehashTypeBaseStats(outputRoot);
        Assert.True(second.Ok, second.Reason);
        Assert.False(second.Changed);
        Assert.False(second.Written);
        Assert.Null(second.ManifestBytes);

        var bytesAfterSecond = File.ReadAllBytes(Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName));
        Assert.True(DumpWriter.RehashTypeBaseStats(outputRoot).Ok);
        Assert.Equal(
            bytesAfterSecond,
            File.ReadAllBytes(Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName)));
    }

    [Fact]
    public void Base_stat_rehash_refuses_a_missing_capture_rather_than_reporting_success()
    {
        var outputRoot = Path.Combine(_dir, "_dump");
        Directory.CreateDirectory(outputRoot);
        var rehash = DumpWriter.RehashTypeBaseStats(outputRoot);
        Assert.False(rehash.Ok);
        Assert.Contains("TBSREHASH-NO-FILE", rehash.Reason);
        Assert.Null(rehash.ManifestBytes);
    }

    [Fact]
    public void Base_stat_rehash_refuses_a_malformed_entry_without_writing_anything()
    {
        // Fail-closed on shape, proven against a real file rather than asserted from the return value
        // alone: a refusal that had already written would leave the tree worse than it found it.
        var outputRoot = SeedStaleBaseStatsHash();
        var path = Path.Combine(outputRoot, DumpWriter.TypeBaseStatsFileName);
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        ((JsonObject)node["entries"]![0]!).Remove("statsJson");
        File.WriteAllText(path, node.ToJsonString());
        var bytesBeforeRefusal = File.ReadAllBytes(path);

        var rehash = DumpWriter.RehashTypeBaseStats(outputRoot);
        Assert.False(rehash.Ok);
        Assert.Contains("TBSREHASH-ENTRY-SHAPE", rehash.Reason);
        Assert.Null(rehash.ManifestBytes);
        Assert.Equal(bytesBeforeRefusal, File.ReadAllBytes(path));
    }
}
