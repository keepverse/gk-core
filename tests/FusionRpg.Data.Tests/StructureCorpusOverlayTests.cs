using FusionRpg.Core.World;
using FusionRpg.Core.World.StructureSeed;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `data-test-substrate` BU8: <see cref="StructureCorpus.FromRows"/> / <see cref="StructureCorpus.WithRows"/>
/// are the corpus's **in-memory entrance**, and this is the contract the six fixtures in this project
/// depend on. Before it existed the ctor was private and <see cref="StructureCorpus.Load"/> was the only
/// way in, so a fixture that needed the shipped rows plus one row of its own had to copy
/// `gk-data/packs/fusion/data/seed/**` into a temp directory and load the copy (BU6). The entrance is pinned here, not in
/// the fixtures, so a future change to the corpus cannot silently reopen that hole.
///
/// <para>Joins the <c>StructureCatalogSwap</c> collection because one case replaces the static
/// <c>StructureCatalog</c>; like every member of that collection it configures a **superset** of the real
/// corpus and restores the real corpus afterwards, so a suite reading that catalog concurrently never
/// sees a row disappear (see the collection's own definition in `WonderBuildTests`).</para>
/// </summary>
[Collection("StructureCatalogSwap")]
public class StructureCorpusOverlayTests
{
    static StructureCorpus RealCorpus() => StructureCorpus.Load(StructureCorpusOverlay.RealCorpusRoot());

    [Fact]
    public void FromRows_builds_a_corpus_over_rows_the_caller_holds_and_Rows_is_read_only()
    {
        var depot = StructureCorpusOverlay.Depot("test-from-rows-depot", 6);

        var corpus = StructureCorpus.FromRows(new[] { depot });

        Assert.Same(depot, Assert.Single(corpus.Rows));

        // The rows are a read-only VIEW, which is what makes `WithRows` the only way to add a row: the
        // overlay this replaced appended through `Rows`' own list backing, so a corpus handed to a
        // caller could change under it. If this assertion ever stops throwing, that seam is open again.
        Assert.Throws<NotSupportedException>(() =>
            ((IList<StructureCorpusRow>)corpus.Rows).Add(StructureCorpusOverlay.Depot("test-from-rows-other", 1)));
    }

    [Fact]
    public void WithRows_returns_a_superset_and_leaves_its_receiver_untouched()
    {
        var real = RealCorpus();
        var realIds = real.Rows.Select(r => r.StructureId).ToList();
        Assert.NotEmpty(realIds); // precondition, not a population assertion: the shipped corpus has rows

        var withExtra = real.WithRows(StructureCorpusOverlay.Depot("test-with-rows-depot", 6));

        // the receiver is untouched -- a cached corpus that grew here would duplicate ids and fail
        // StructureCatalog.Validate on the next Configure
        Assert.Equal(realIds, real.Rows.Select(r => r.StructureId).ToList());
        // and the result is the real rows, in order, plus the addition
        Assert.Equal(realIds, withExtra.Rows.Take(realIds.Count).Select(r => r.StructureId).ToList());
        Assert.Equal("test-with-rows-depot", withExtra.Rows[^1].StructureId);
    }

    [Fact]
    public void LoadWithRows_is_what_the_fixtures_configure_and_it_reaches_the_catalog()
    {
        try
        {
            StructureCatalog.Configure(StructureCorpusOverlay.LoadWithRows(
                StructureCorpusOverlay.Depot("test-overlay-depot", 6),
                StructureCorpusOverlay.Wonder("test-overlay-wonder", "Wildland", "Sector", "Common", relicCost: 1)));

            // the superset property: a shipped row is still there, at the same answer
            Assert.True(StructureCatalog.IsKnown("relic-vault"));

            var depot = StructureCatalog.Get("test-overlay-depot");
            Assert.Equal(StructureKind.ItemStorage, depot.Kind);
            Assert.Equal(6, depot.ItemStorageCapacityBonus);

            var wonder = StructureCatalog.Get("test-overlay-wonder");
            Assert.Equal(1, wonder.RelicCost);
            Assert.Equal(WonderScope.Sector, wonder.WonderScope);
            Assert.Equal(SlotKind.Wildland, wonder.RequiredSlotKind);

            // and the depot is a real capacity source, not merely a catalog row
            var sector = new WorldSector
            {
                SectorId = "s",
                TypeId = "stable",
                OwnerFactionId = "f1",
                Slots = new[] { new WorldSlot { SlotIndex = 0, StructureId = "test-overlay-depot" } },
            };
            Assert.Equal(6, SectorItemCapacity.EffectiveCapacity(sector));
        }
        finally
        {
            StructureCatalog.Configure(RealCorpus());
        }
    }
}
