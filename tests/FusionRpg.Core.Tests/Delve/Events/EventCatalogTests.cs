using System.Text.Json;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.1 (spec-event-deck.md §2, §6) — `EventCatalog.Load`: rows arrive pre-parsed, bands
/// resolve against the real registries, eligibility trees compile once via `PredicateCompiler`, every
/// malformed row refuses by name and writes nothing. `DungeonTuningHub`/`DungeonRegistryHub` (and so
/// `BandCatalog`/`OverrideTagCatalog`) are configured for the whole assembly by
/// `Dungeon.DungeonHubTestBootstrap`'s module initializer.</summary>
public class EventCatalogTests
{
    static readonly IReadOnlyList<string> EventKinds = BandCatalog.Get("eventKind").Members;
    static readonly IReadOnlyList<string> RepeatScopes = BandCatalog.Get("repeatScope").Members;
    static readonly IReadOnlyList<string> OutcomeOrdinals = BandCatalog.Get("outcomeOrdinal").Members;
    static readonly IReadOnlyList<string> OverrideTags = OverrideTagCatalog.All;
    static readonly IReadOnlyList<string> DropBands = ReadRealItemDropBands();

    static IReadOnlyList<string> ReadRealItemDropBands()
    {
        var path = Path.Combine(ContentRoot.Path, "data", "seed", "items", "_registry", "bands.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("dropBand").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    static int NoStatus(string id) => -1; // no real status catalog needed for these tests -- every fixture's own tree is null or status-free
    static readonly Func<string, int> StatusBit = NoStatus;

    static EventOutcomeRow Outcome(string ordinal = "good", string dropBand = "staple", string consequence = "none") =>
        new(ordinal, dropBand, consequence, Array.Empty<EventEffectRef>());

    static EventRow Row(
        string id = "curio.test-1", string kind = "curio", string repeatScope = "per-delve",
        PredicateNode? eligibility = null, IReadOnlyList<EventOutcomeRow>? outcomes = null,
        string? supplyOverride = null, string? chainRef = null) =>
        new(id, kind, Theme: null, ClimateAffinity: null, repeatScope, eligibility,
            outcomes ?? new[] { Outcome("good"), Outcome("bad") }, supplyOverride, chainRef);

    static EventCatalogLoad Load(params EventRow[] rows) =>
        EventCatalog.Load(rows, EventKinds, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, StatusBit);

    // ---- the real shipped registries, sanity-pinned ----

    [Fact]
    public void The_real_registries_carry_exactly_the_vocabularies_the_spec_names()
    {
        Assert.Equal(new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" }, EventKinds);
        Assert.Equal(new[] { "per-delve", "per-domain", "once-per-player" }, RepeatScopes);
        Assert.Equal(new[] { "good", "mixed", "bad", "nothing" }, OutcomeOrdinals);
        Assert.Equal(new[] { "herbs", "key", "holy", "bait", "watch" }, OverrideTags);
        Assert.Equal(new[] { "staple", "frequent", "occasional", "seldom", "exceptional" }, DropBands);
    }

    // ---- the happy path ----

    [Fact]
    public void A_well_formed_event_loads_with_no_rejections()
    {
        var result = Load(Row());
        Assert.Empty(result.Rejections);
        Assert.Equal(1, result.Catalog.Count);
        Assert.NotNull(result.Catalog.Resolve("curio.test-1"));
    }

    [Fact]
    public void A_null_eligibility_tree_resolves_to_PredicateCompiler_Always()
    {
        var result = Load(Row(eligibility: null));
        Assert.Same(PredicateCompiler.Always, result.Catalog.EligibilityFor("curio.test-1"));
    }

    [Fact]
    public void An_unresolved_eventId_returns_Always_never_throws()
    {
        var result = Load(Row());
        Assert.Same(PredicateCompiler.Always, result.Catalog.EligibilityFor("not-a-real-event"));
        Assert.Null(result.Catalog.Resolve("not-a-real-event"));
    }

    [Fact]
    public void A_real_eligibility_tree_compiles_and_is_reachable_off_the_catalog()
    {
        var tree = new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 500);
        var result = Load(Row(eligibility: tree));
        Assert.Empty(result.Rejections);

        var compiled = result.Catalog.EligibilityFor("curio.test-1");
        var facts = new FactReader(
            self: new EntityFacts(0, 0, 1000, -1, -1, -1, false, false, 0),
            target: new EntityFacts(0, 0, 400, -1, -1, -1, false, false, 0));
        Assert.True(compiled.Evaluate(ref facts)); // target hp 400‰ < 500‰
    }

    // ---- one rejection per rule, each named, each writing nothing for that row ----

    [Fact]
    public void A_duplicate_eventId_refuses_the_second_row_by_name()
    {
        var result = Load(Row(id: "dup"), Row(id: "dup"));
        Assert.Equal(1, result.Catalog.Count); // the first row IS kept -- only the second is refused
        var r = Assert.Single(result.Rejections);
        Assert.StartsWith(EventRules.DuplicateId, r.Detail);
        Assert.Contains("dup", r.Detail);
    }

    [Fact]
    public void An_unknown_kind_refuses_and_writes_nothing()
    {
        var result = Load(Row(kind: "not-a-real-kind"));
        Assert.Equal(0, result.Catalog.Count);
        var r = Assert.Single(result.Rejections);
        Assert.StartsWith(EventRules.BadKind, r.Detail);
    }

    [Fact]
    public void An_unknown_repeatScope_refuses_and_writes_nothing()
    {
        var result = Load(Row(repeatScope: "not-a-real-scope"));
        Assert.Equal(0, result.Catalog.Count);
        Assert.StartsWith(EventRules.BadRepeatScope, Assert.Single(result.Rejections).Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void An_outcome_count_outside_two_to_four_refuses(int count)
    {
        var outcomes = Enumerable.Range(0, count).Select(_ => Outcome()).ToList();
        var result = Load(Row(outcomes: outcomes));
        Assert.Equal(0, result.Catalog.Count);
        Assert.StartsWith(EventRules.BadOutcomeCount, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void An_unknown_outcome_ordinal_refuses()
    {
        var result = Load(Row(outcomes: new[] { Outcome(ordinal: "not-a-real-ordinal"), Outcome() }));
        Assert.StartsWith(EventRules.BadOrdinal, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void Nothing_ordinal_is_refused_off_a_non_story_kind()
    {
        var result = Load(Row(kind: "curio", outcomes: new[] { Outcome(ordinal: "nothing"), Outcome() }));
        Assert.StartsWith(EventRules.NothingOnlyOnStory, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void Nothing_ordinal_is_accepted_on_a_story_kind_with_a_chainRef()
    {
        var result = Load(Row(kind: "story", chainRef: "events.chain-1",
            outcomes: new[] { Outcome(ordinal: "nothing"), Outcome() }));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void An_unknown_dropBand_refuses()
    {
        var result = Load(Row(outcomes: new[] { Outcome(dropBand: "not-a-real-band"), Outcome() }));
        Assert.StartsWith(EventRules.BadDropBand, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void An_unknown_consequence_refuses()
    {
        var result = Load(Row(outcomes: new[] { Outcome(consequence: "not-a-real-consequence"), Outcome() }));
        Assert.StartsWith(EventRules.BadConsequence, Assert.Single(result.Rejections).Detail);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("loot")]
    [InlineData("encounter")]
    [InlineData("scout")]
    public void Every_real_consequence_value_is_accepted(string consequence)
    {
        var result = Load(Row(outcomes: new[] { Outcome(consequence: consequence), Outcome() }));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void An_unknown_supplyOverride_refuses()
    {
        var result = Load(Row(supplyOverride: "not-a-real-tag"));
        Assert.StartsWith(EventRules.BadSupplyOverride, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void A_real_supplyOverride_tag_is_accepted()
    {
        var result = Load(Row(supplyOverride: "herbs"));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void A_story_event_missing_chainRef_refuses()
    {
        var result = Load(Row(kind: "story", chainRef: null));
        Assert.StartsWith(EventRules.ChainRefRequiredForStory, Assert.Single(result.Rejections).Detail);
    }

    [Fact]
    public void A_non_story_event_needs_no_chainRef()
    {
        var result = Load(Row(kind: "curio", chainRef: null));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void A_malformed_eligibility_tree_refuses_with_PredicateCompilers_own_reason()
    {
        // SideIs is explicitly refused by the event validator as meaningless here (spec §6) -- but
        // that refusal is event-deck's OWN future rule (D3.2's filters), not this loader's; what THIS
        // loader proves is that PredicateCompiler's OWN structural limits (depth/node count) are
        // already enforced at load, using its real rejection reason verbatim.
        var tooDeep = BuildDeeplyNested(PredicateCompiler.MaxDepth + 1);
        var result = Load(Row(eligibility: tooDeep));
        Assert.Equal(0, result.Catalog.Count);
        var r = Assert.Single(result.Rejections);
        Assert.Equal(AtomRejectionReason.DepthExceeded, r.Reason);
    }

    static PredicateNode BuildDeeplyNested(int depth)
    {
        PredicateNode node = new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 500);
        for (var i = 0; i < depth; i++)
            node = new PredicateNode.Not(node);
        return node;
    }

    // ---- N bad rows -> N rejections, every good row still loads ----

    [Fact]
    public void Multiple_bad_rows_each_produce_their_own_named_rejection_good_rows_still_load()
    {
        var result = Load(
            Row(id: "good-1"),
            Row(id: "bad-kind", kind: "not-a-kind"),
            Row(id: "good-2"),
            Row(id: "bad-scope", repeatScope: "not-a-scope"));

        Assert.Equal(2, result.Catalog.Count);
        Assert.NotNull(result.Catalog.Resolve("good-1"));
        Assert.NotNull(result.Catalog.Resolve("good-2"));
        Assert.Equal(2, result.Rejections.Count);
    }

    // ---- validation ----

    [Fact]
    public void Load_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EventCatalog.Load(null!, EventKinds, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, StatusBit));
        Assert.Throws<ArgumentNullException>(() => EventCatalog.Load(Array.Empty<EventRow>(), null!, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, StatusBit));
        Assert.Throws<ArgumentNullException>(() => EventCatalog.Load(Array.Empty<EventRow>(), EventKinds, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, null!));
    }

    [Fact]
    public void An_empty_row_list_loads_an_empty_catalog_with_no_rejections()
    {
        var result = EventCatalog.Load(Array.Empty<EventRow>(), EventKinds, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, StatusBit);
        Assert.Equal(0, result.Catalog.Count);
        Assert.Empty(result.Rejections);
    }
}
