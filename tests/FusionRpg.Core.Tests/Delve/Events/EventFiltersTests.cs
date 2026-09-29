using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.2 (spec-event-deck.md §2) — the four pool filters: kind fit, eligibility, repeat scope,
/// recent cells. Each is proven independently, then together, then in a DIFFERENT order, to make the
/// "these are set operations, not a pipeline" claim real rather than assumed.</summary>
public class EventFiltersTests
{
    static EventOutcomeRow Outcome() => new("good", "staple", "none", Array.Empty<EventEffectRef>());

    static EventRow Row(string id, string kind, string? theme = null, string repeatScope = "per-delve", PredicateNode? eligibility = null) =>
        new(id, kind, theme, ClimateAffinity: null, repeatScope, eligibility, new[] { Outcome(), Outcome() }, null, null);

    static readonly IReadOnlySet<string> NoneSeen = new HashSet<string>();
    static readonly IReadOnlySet<EventFilters.EventCell> NoRecentCells = new HashSet<EventFilters.EventCell>();
    // HpMilli 500 for both sides -- HpBelowMilli(999) reads true, HpBelowMilli(1) reads false,
    // giving both fixture directions a real, distinguishable threshold to author against.
    static FactReader MidHpFacts() => new(
        self: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0),
        target: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0));

    static EventCatalog CatalogOf(params EventRow[] rows)
    {
        var eventKinds = new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" };
        var repeatScopes = new[] { "per-delve", "per-domain", "once-per-player" };
        var ordinals = new[] { "good", "mixed", "bad", "nothing" };
        var dropBands = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
        var overrideTags = new[] { "herbs", "key", "holy", "bait", "watch" };
        var result = EventCatalog.Load(rows, eventKinds, repeatScopes, ordinals, dropBands, overrideTags, _ => -1);
        Assert.Empty(result.Rejections); // a test fixture bug, not the thing under test, if this ever fires
        return result.Catalog;
    }

    // ---- Filter 1: kind fit ----

    [Theory]
    [InlineData("curio", "curio", true)]
    [InlineData("shrine", "shrine", true)]
    [InlineData("trap", "trap", true)]
    [InlineData("merchant", "bargain", true)]
    [InlineData("wild", "story", true)]
    [InlineData("rest", "encounter-event", true)]
    [InlineData("curio", "shrine", false)]
    [InlineData("merchant", "curio", false)]
    public void KindFits_matches_the_spec_table_exactly(string roomKind, string eventKind, bool expected)
    {
        Assert.Equal(expected, EventFilters.KindFits(roomKind, eventKind));
    }

    [Theory]
    [InlineData("curio")]
    [InlineData("story")]
    [InlineData("bargain")]
    public void Unknown_room_kind_fits_any_event_kind(string eventKind)
    {
        Assert.True(EventFilters.KindFits("unknown", eventKind));
    }

    [Fact]
    public void ByKindFit_keeps_only_events_whose_kind_matches_the_room_archetype()
    {
        var pool = new[] { Row("a", "curio"), Row("b", "shrine"), Row("c", "curio") };
        var filtered = EventFilters.ByKindFit(pool, "curio");
        Assert.Equal(new[] { "a", "c" }, filtered.Select(e => e.EventId));
    }

    // ---- Filter 2: eligibility ----

    [Fact]
    public void ByEligibility_keeps_events_whose_compiled_tree_evaluates_true()
    {
        var eligible = Row("eligible", "curio", eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 999));
        var ineligible = Row("ineligible", "curio", eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 1));
        var catalog = CatalogOf(eligible, ineligible);

        var filtered = EventFilters.ByEligibility(new[] { eligible, ineligible }, catalog, MidHpFacts());

        Assert.Equal(new[] { "eligible" }, filtered.Select(e => e.EventId));
    }

    [Fact]
    public void ByEligibility_keeps_an_event_with_no_tree_since_absent_means_Always()
    {
        var row = Row("no-tree", "curio", eligibility: null);
        var catalog = CatalogOf(row);
        var filtered = EventFilters.ByEligibility(new[] { row }, catalog, MidHpFacts());
        Assert.Equal(new[] { "no-tree" }, filtered.Select(e => e.EventId));
    }

    [Fact]
    public void ByEligibility_does_not_mutate_the_callers_facts_or_leak_Reads_between_candidates()
    {
        var a = Row("a", "curio", eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 999));
        var b = Row("b", "curio", eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 999));
        var catalog = CatalogOf(a, b);
        var facts = MidHpFacts();
        var readsBefore = facts.Reads;

        EventFilters.ByEligibility(new[] { a, b }, catalog, facts);

        Assert.Equal(readsBefore, facts.Reads); // passed by value -- the caller's own struct is untouched
    }

    // ---- Filter 3: repeat scope ----

    [Fact]
    public void ByRepeatScope_refuses_any_scope_already_in_the_perDelve_seen_set_the_absolute_invariant()
    {
        var perDelve = Row("once-per-player-but-also-seen-this-delve", "curio", repeatScope: "once-per-player");
        var pool = new[] { perDelve };
        var seenThisDelve = new HashSet<string> { perDelve.EventId }; // seen this delve, even though its own scope is wider

        var filtered = EventFilters.ByRepeatScope(pool, seenThisDelve, NoneSeen, NoneSeen);

        Assert.Empty(filtered); // per-delve applies UNCONDITIONALLY, regardless of the row's own wider scope
    }

    [Fact]
    public void ByRepeatScope_a_per_delve_event_is_unaffected_by_domain_or_player_seen_sets()
    {
        var row = Row("x", "curio", repeatScope: "per-delve");
        var domainSeen = new HashSet<string> { row.EventId };
        var playerSeen = new HashSet<string> { row.EventId };

        var filtered = EventFilters.ByRepeatScope(new[] { row }, NoneSeen, domainSeen, playerSeen);

        Assert.Equal(new[] { "x" }, filtered.Select(e => e.EventId)); // per-delve never reads the other two sets
    }

    [Fact]
    public void ByRepeatScope_refuses_a_per_domain_event_seen_in_this_domain_before()
    {
        var row = Row("y", "curio", repeatScope: "per-domain");
        var domainSeen = new HashSet<string> { row.EventId };
        Assert.Empty(EventFilters.ByRepeatScope(new[] { row }, NoneSeen, domainSeen, NoneSeen));
    }

    [Fact]
    public void ByRepeatScope_refuses_a_once_per_player_event_seen_by_this_player_before()
    {
        var row = Row("z", "curio", repeatScope: "once-per-player");
        var playerSeen = new HashSet<string> { row.EventId };
        Assert.Empty(EventFilters.ByRepeatScope(new[] { row }, NoneSeen, NoneSeen, playerSeen));
    }

    [Fact]
    public void ByRepeatScope_keeps_an_event_never_seen_in_any_set()
    {
        var row = Row("fresh", "curio", repeatScope: "once-per-player");
        var filtered = EventFilters.ByRepeatScope(new[] { row }, NoneSeen, NoneSeen, NoneSeen);
        Assert.Equal(new[] { "fresh" }, filtered.Select(e => e.EventId));
    }

    // ---- Filter 4: recent cells ----

    [Fact]
    public void ByRecentCells_refuses_a_kind_theme_cell_drawn_recently()
    {
        var row = Row("a", "curio", theme: "graveyard");
        var recent = new HashSet<EventFilters.EventCell> { new("curio", "graveyard") };
        Assert.Empty(EventFilters.ByRecentCells(new[] { row }, recent));
    }

    [Fact]
    public void ByRecentCells_keeps_the_same_kind_with_a_different_theme()
    {
        var row = Row("a", "curio", theme: "graveyard");
        var recent = new HashSet<EventFilters.EventCell> { new("curio", "forest") };
        Assert.Equal(new[] { "a" }, EventFilters.ByRecentCells(new[] { row }, recent).Select(e => e.EventId));
    }

    [Fact]
    public void ByRecentCells_handles_a_null_theme_cell_correctly()
    {
        var row = Row("a", "curio", theme: null);
        var recentWithNullTheme = new HashSet<EventFilters.EventCell> { new("curio", null) };
        Assert.Empty(EventFilters.ByRecentCells(new[] { row }, recentWithNullTheme));

        var recentWithoutIt = new HashSet<EventFilters.EventCell> { new("shrine", null) };
        Assert.Single(EventFilters.ByRecentCells(new[] { row }, recentWithoutIt));
    }

    // ---- the verify line's own headline: order never changes the result ----

    [Fact]
    public void ApplyAll_and_every_other_ordering_of_the_four_filters_land_on_the_identical_surviving_set()
    {
        var survivor = Row("survivor", "curio", theme: "old-cell",
            eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 999));
        var failsKind = Row("fails-kind", "shrine");
        var failsEligibility = Row("fails-eligibility", "curio",
            eligibility: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 1));
        var failsRepeat = Row("fails-repeat", "curio", repeatScope: "per-domain");
        var failsRecent = Row("fails-recent", "curio", theme: "hot-cell");

        var pool = new[] { survivor, failsKind, failsEligibility, failsRepeat, failsRecent };
        var catalog = CatalogOf(pool);
        var facts = MidHpFacts();
        var perDelveSeen = NoneSeen;
        var perDomainSeen = new HashSet<string> { failsRepeat.EventId };
        var oncePerPlayerSeen = NoneSeen;
        var recentCells = new HashSet<EventFilters.EventCell> { new("curio", "hot-cell") };

        var viaApplyAll = EventFilters.ApplyAll(
            pool, "curio", catalog, facts, perDelveSeen, perDomainSeen, oncePerPlayerSeen, recentCells);
        Assert.Equal(new[] { "survivor" }, viaApplyAll.Select(e => e.EventId));

        // The exact same four filters, called in the REVERSE order by hand.
        var afterRecent = EventFilters.ByRecentCells(pool, recentCells);
        var afterRepeat = EventFilters.ByRepeatScope(afterRecent, perDelveSeen, perDomainSeen, oncePerPlayerSeen);
        var afterEligibility = EventFilters.ByEligibility(afterRepeat, catalog, facts);
        var reverseOrder = EventFilters.ByKindFit(afterEligibility, "curio");

        Assert.Equal(viaApplyAll.Select(e => e.EventId), reverseOrder.Select(e => e.EventId));

        // A third, shuffled order for good measure.
        var shuffled = EventFilters.ByRepeatScope(
            EventFilters.ByEligibility(
                EventFilters.ByRecentCells(EventFilters.ByKindFit(pool, "curio"), recentCells),
                catalog, facts),
            perDelveSeen, perDomainSeen, oncePerPlayerSeen);

        Assert.Equal(viaApplyAll.Select(e => e.EventId), shuffled.Select(e => e.EventId));
    }

    // ---- validation ----

    [Fact]
    public void Every_filter_null_arguments_throw()
    {
        var pool = new[] { Row("a", "curio") };
        var catalog = CatalogOf(pool);
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByKindFit(null!, "curio"));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByEligibility(null!, catalog, MidHpFacts()));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByEligibility(pool, null!, MidHpFacts()));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByRepeatScope(null!, NoneSeen, NoneSeen, NoneSeen));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByRepeatScope(pool, null!, NoneSeen, NoneSeen));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByRecentCells(null!, NoRecentCells));
        Assert.Throws<ArgumentNullException>(() => EventFilters.ByRecentCells(pool, null!));
    }

    [Fact]
    public void KindFits_bad_arguments_throw()
    {
        Assert.Throws<ArgumentException>(() => EventFilters.KindFits("", "curio"));
        Assert.Throws<ArgumentException>(() => EventFilters.KindFits("curio", ""));
    }
}
