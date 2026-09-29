using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>
/// D4.30's real prerequisite chain (2026-09-07) — proves whatever real, seedsmith-authored quest
/// content exists at `gk-data/packs/fusion/data/seed/dungeon/quests/*.json` (the first AUTHORED-field dungeon kind this
/// program has ever generated) against the REAL, authoritative `QuestCatalog.Load` validator — the
/// same "run the real validator, not just the Python-side checks" discipline `item-map`'s own
/// `ItemSeedValidator` proved essential for uniques (D4.29). `ObjectiveTemplateCatalog`/`BandCatalog`
/// are already configured for the whole assembly by `Dungeon.DungeonHubTestBootstrap`'s module
/// initializer, mirroring `QuestCatalogTests.cs`'s own established fixture exactly.
///
/// <para>Correct-by-construction against an empty directory (no content shipped yet is a real,
/// honest, zero-rejection state, matching `LayoutTemplateCatalogTests`/`DomainCatalogTests`' own
/// identical posture for a not-yet-populated corpus) — this test becomes load-bearing the moment
/// real content lands, without needing its own edit.</para>
/// </summary>
public class QuestSeedContentTests
{
    static readonly IReadOnlyList<ObjectiveTemplateDef> Templates = ObjectiveTemplateCatalog.All;
    static readonly IReadOnlyList<string> Scopes = BandCatalog.Get("questScope").Members;
    static readonly IReadOnlyList<string> RewardBands = BandCatalog.Get("rewardBand").Members;
    static readonly IReadOnlyList<string> CountBandMembers = BandCatalog.Get("countBand").Members;
    static int NoStatus(string id) => -1;

    [Fact]
    public void Every_real_shipped_quest_anchor_round_trips_with_zero_rejections()
    {
        var rows = QuestSeedFile.LoadAll(DungeonTestFiles.QuestsDir());
        var result = QuestCatalog.Load(rows, Templates, Scopes, RewardBands, CountBandMembers, NoStatus);

        Assert.Empty(result.Rejections);
        Assert.Equal(rows.Count, result.Catalog.Count);
    }

    [Fact]
    public void LoadAll_on_a_missing_directory_returns_empty_not_throws()
    {
        var rows = QuestSeedFile.LoadAll(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "dungeon", "does-not-exist"));
        Assert.Empty(rows);
    }

    [Fact]
    public void LoadAll_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => QuestSeedFile.LoadAll(null!));
    }
}
