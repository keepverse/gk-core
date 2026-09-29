using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.13's own real remaining bridge, closed 2026-09-07 -- `QuestOffer.Satisfiable`'s own
/// `archetypeEventPoolHasKind(archetypeId, targetRef)` delegate. Uses the real, already-proven-clean
/// shipped event catalog (`DomainEventPreflightBridgeTests.RealCatalog`'s own established shape) so a
/// positive case does not depend on a hand-built `EventRow` construction this file would otherwise have
/// to re-derive.</summary>
public class QuestArchetypeEventBridgeTests
{
    static readonly IReadOnlyList<string> EventKinds = BandCatalog.Get("eventKind").Members;
    static readonly IReadOnlyList<string> RepeatScopes = BandCatalog.Get("repeatScope").Members;
    static readonly IReadOnlyList<string> OutcomeOrdinals = BandCatalog.Get("outcomeOrdinal").Members;
    static readonly IReadOnlyList<string> OverrideTags = OverrideTagCatalog.All;
    static int NoStatus(string id) => -1;

    static IReadOnlyList<string> ReadRealItemDropBands()
    {
        var path = Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "items", "_registry", "bands.v1.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("dropBand").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    static EventCatalog RealCatalog()
    {
        var rows = EventSeedFile.LoadAll(DungeonTestFiles.EventsDir());
        var result = EventCatalog.Load(rows, EventKinds, RepeatScopes, OutcomeOrdinals, ReadRealItemDropBands(), OverrideTags, NoStatus);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    [Fact]
    public void Build_null_arguments_throw()
    {
        var catalog = RealCatalog();
        Assert.Throws<ArgumentNullException>(() => QuestArchetypeEventBridge.Build(null!, catalog));
        Assert.Throws<ArgumentNullException>(() => QuestArchetypeEventBridge.Build(new Dictionary<string, IReadOnlyList<string>>(), null!));
    }

    [Fact]
    public void An_archetype_whose_pool_holds_a_matching_kind_returns_true()
    {
        var catalog = RealCatalog();
        var someEvent = catalog.All.First();
        var pool = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.a"] = new[] { someEvent.EventId } };

        var hasKind = QuestArchetypeEventBridge.Build(pool, catalog);

        Assert.True(hasKind("room.a", someEvent.Kind));
    }

    [Fact]
    public void An_archetype_whose_pool_events_are_all_a_different_kind_returns_false()
    {
        var catalog = RealCatalog();
        var someEvent = catalog.All.First();
        var pool = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.a"] = new[] { someEvent.EventId } };

        var hasKind = QuestArchetypeEventBridge.Build(pool, catalog);

        Assert.False(hasKind("room.a", someEvent.Kind + "-not-real"));
    }

    [Fact]
    public void An_unknown_archetype_id_returns_false_never_throws()
    {
        var catalog = RealCatalog();
        var hasKind = QuestArchetypeEventBridge.Build(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), catalog);

        Assert.False(hasKind("room.does-not-exist", "any-kind"));
    }

    [Fact]
    public void A_pool_naming_an_unresolvable_event_id_returns_false_never_throws()
    {
        var catalog = RealCatalog();
        var pool = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.a"] = new[] { "event.does-not-exist" } };

        var hasKind = QuestArchetypeEventBridge.Build(pool, catalog);

        Assert.False(hasKind("room.a", "any-kind"));
    }

    [Fact]
    public void An_empty_pool_for_a_real_archetype_returns_false()
    {
        var catalog = RealCatalog();
        var pool = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.a"] = Array.Empty<string>() };

        var hasKind = QuestArchetypeEventBridge.Build(pool, catalog);

        Assert.False(hasKind("room.a", "any-kind"));
    }
}
