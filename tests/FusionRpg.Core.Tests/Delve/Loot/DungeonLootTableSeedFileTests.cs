using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Items.Drops;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>
/// D3.15 — the REAL, committed <c>gk-data/packs/fusion/data/seed/loot/tables-dungeon.json</c> (`DungeonLootTableGen.Build`
/// over the six real first-ship climates, `DungeonLootTableGen.ToJson`). This is the file
/// `FusionRpg.Server/Program.cs`'s own boot import already reads via
/// <c>Directory.EnumerateFiles(lootDir, "tables*.json")</c> — the prefix-wildcard glob matches this
/// file alongside the pre-existing `tables.v1.json` with zero server code changes, verified here by
/// merging both real files through the same <see cref="LootCorpusReader.Merge"/> call the boot path
/// itself makes rather than reading this file in isolation.
/// </summary>
public class DungeonLootTableSeedFileTests
{
    static string RepoRoot() => KeepverseRoots.Core();

    static readonly string[] SixClimates = { "fire", "ice", "air", "earth", "light", "dark" };
    static readonly DungeonLootTableGen.Weights Weights = new(EquipmentWeight: 300, NothingWeight: 700);

    [Fact]
    public void The_committed_file_exists()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "loot", "tables-dungeon.json");
        Assert.True(File.Exists(path), $"expected {path} to exist — run DungeonLootTableGen.Build/.ToJson and commit the result");
    }

    /// <summary>
    /// No hand edits: the committed file is exactly what the generator emits.
    ///
    /// <para><b>Line endings are normalised</b> (CanonicalEol, 2026-09-19). This used to compare raw
    /// bytes, and `<c>.gitattributes</c>` checks the file out as LF while the generator wrote CRLF — so
    /// it was red in every fresh worktree and green only on the machine whose working tree held the
    /// generator's own CRLF output. That was not a hand edit, and normalising does not weaken the
    /// check: any content difference still fails.</para>
    /// </summary>
    [Fact]
    public void The_committed_file_matches_the_generator_no_hand_edits()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "loot", "tables-dungeon.json");
        var committed = File.ReadAllText(path).ToLf();
        var regenerated = DungeonLootTableGen.ToJson(DungeonLootTableGen.Build(SixClimates, Weights));
        Assert.Equal(regenerated, committed);
    }

    [Fact]
    public void The_committed_file_parses_and_validates_through_the_real_reader_and_validator()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "loot", "tables-dungeon.json");
        var corpus = LootCorpusReader.Parse(File.ReadAllText(path));
        // The table count is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19); the real contract is the validator call below.
        Assert.NotEmpty(corpus.Tables);

        var lookups = new DropContentLookups(CurrencyExists: id => id == "souls");
        var rejection = DropTableValidator.Validate(corpus.Sources, corpus.Tables, Tests.Items.DropVolumeTests.Tuning(), lookups);
        Assert.True(rejection.IsOk, rejection.Detail);
    }

    /// <summary>The real boot-import shape: merges every `tables*.json` under `gk-data/packs/fusion/data/seed/loot/`, then
    /// validates the WHOLE merged corpus — the only way a cross-file id collision would ever surface,
    /// matching `Program.cs:510-512`'s own `LootCorpusReader.Merge(Directory.EnumerateFiles(...))` call.</summary>
    [Fact]
    public void Merged_with_the_real_item_corpus_the_whole_loot_directory_still_validates_with_no_id_collisions()
    {
        var lootDir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "loot");
        var files = Directory.EnumerateFiles(lootDir, "tables*.json");
        var corpus = LootCorpusReader.Merge(files.Select(f => LootCorpusReader.Parse(File.ReadAllText(f))));

        var ids = corpus.Tables.Select(t => t.TableId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        var lookups = new DropContentLookups(CurrencyExists: id => id == "souls");
        var rejection = DropTableValidator.Validate(corpus.Sources, corpus.Tables, Tests.Items.DropVolumeTests.Tuning(), lookups);
        Assert.True(rejection.IsOk, rejection.Detail);
    }
}
