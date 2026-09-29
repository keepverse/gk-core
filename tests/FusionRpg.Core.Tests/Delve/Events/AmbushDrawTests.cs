using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.9 (spec-event-deck.md §7, "Ambush and curio seams"): the night-ambush draw at a rest
/// room. `CatalogOf` mirrors `EventFiltersTests.cs`'s own fixture-building convention exactly.</summary>
public class AmbushDrawTests
{
    static readonly Func<string, int> StatusBit = id => id switch { "watch" => 0, "chilled" => 1, _ => -1 };

    static EventCatalog CatalogOf(params EventRow[] rows)
    {
        var eventKinds = new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" };
        var repeatScopes = new[] { "per-delve", "per-domain", "once-per-player" };
        var ordinals = new[] { "good", "mixed", "bad", "nothing" };
        var dropBands = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
        var overrideTags = new[] { "herbs", "key", "holy", "bait", "watch" };
        var result = EventCatalog.Load(rows, eventKinds, repeatScopes, ordinals, dropBands, overrideTags, StatusBit);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    static EventOutcomeRow Outcome() => new("good", "staple", "none", Array.Empty<EventEffectRef>());

    static EventRow Row(string id, string kind, PredicateNode? eligibility = null) => new(
        EventId: id, Kind: kind, Theme: null, ClimateAffinity: null, RepeatScope: "per-delve",
        Eligibility: eligibility, Outcomes: new[] { Outcome(), Outcome() }, SupplyOverride: null, ChainRef: null);

    static readonly EntityFacts EligibleFacts = new(0, 0, 1000, -1, -1, -1, false, false, 0);
    static FactReader Facts(ulong statusMask = 0) => new(EligibleFacts with { StatusMask = statusMask }, EligibleFacts);

    // ---- argument validation ----

    [Fact]
    public void Draw_null_arguments_throw()
    {
        var pool = new[] { Row("e1", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());
        var facts = Facts();
        Assert.Throws<ArgumentNullException>(() =>
            AmbushDraw.Draw(null!, 0, 0, "fire", catalog, facts, StatusBit, 500, 3000, 1000, 300, seed: 1));
        Assert.Throws<ArgumentNullException>(() =>
            AmbushDraw.Draw(pool, 0, 0, "fire", null!, facts, StatusBit, 500, 3000, 1000, 300, seed: 1));
        Assert.Throws<ArgumentNullException>(() =>
            AmbushDraw.Draw(pool, 0, 0, "fire", catalog, facts, null!, 500, 3000, 1000, 300, seed: 1));
    }

    // ---- miss ----

    [Fact]
    public void A_guaranteed_miss_never_ambushes()
    {
        var pool = new[] { Row("e1", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(), StatusBit,
                ambushMilli: 0, 3000, 1000, 300, seed);
            Assert.False(result.Ambushed);
            Assert.Null(result.Event);
            Assert.False(result.EmptyPoolWarning);
        }
    }

    // ---- hit: kind fit ----

    [Fact]
    public void A_guaranteed_hit_only_draws_encounter_event_kind_rows()
    {
        var pool = new[] { Row("fight1", "encounter-event"), Row("curio1", "curio") };
        var catalog = CatalogOf(pool.ToArray());
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(), StatusBit,
                ambushMilli: 1000, 3000, 1000, 300, seed);
            Assert.True(result.Ambushed);
            Assert.Equal("fight1", result.Event!.EventId); // the only encounter-event row
        }
    }

    // ---- hit: eligibility still applies ----

    [Fact]
    public void An_ineligible_row_is_excluded_even_on_a_hit()
    {
        var ineligible = new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Self, Value: 1); // never true at HpMilli 1000
        var pool = new[] { Row("blocked", "encounter-event", ineligible), Row("open", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(), StatusBit,
                ambushMilli: 1000, 3000, 1000, 300, seed);
            Assert.Equal("open", result.Event!.EventId);
        }
    }

    // ---- hit: the extra Not(HasStatus watch) gate ----

    [Fact]
    public void Holding_watch_removes_a_row_that_is_otherwise_eligible()
    {
        var pool = new[] { Row("e1", "encounter-event") }; // no authored eligibility -- Always
        var catalog = CatalogOf(pool.ToArray());
        var watchMask = 1UL << StatusBit("watch");

        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(watchMask), StatusBit,
                ambushMilli: 1000, 3000, 1000, 300, seed);
            Assert.False(result.Ambushed);
            Assert.True(result.EmptyPoolWarning); // the only candidate was removed by the watch gate
        }
    }

    [Fact]
    public void Not_holding_watch_leaves_the_row_eligible()
    {
        var pool = new[] { Row("e1", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());

        var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(statusMask: 0), StatusBit,
            ambushMilli: 1000, 3000, 1000, 300, seed: 1);
        Assert.True(result.Ambushed);
        Assert.Equal("e1", result.Event!.EventId);
    }

    // ---- the one designed empty-pool case: a warning, never a throw ----

    [Fact]
    public void A_hit_with_no_surviving_candidate_is_a_warning_not_a_refusal()
    {
        var pool = new[] { Row("only-curio", "curio") }; // no encounter-event row at all
        var catalog = CatalogOf(pool.ToArray());

        var result = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(), StatusBit,
            ambushMilli: 1000, 3000, 1000, 300, seed: 1);
        Assert.False(result.Ambushed);
        Assert.Null(result.Event);
        Assert.True(result.EmptyPoolWarning);
    }

    // ---- determinism and stream namespacing ----

    [Fact]
    public void Draw_is_deterministic_same_seed_same_room_same_result()
    {
        var pool = new[] { Row("a", "encounter-event"), Row("b", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());

        var x = AmbushDraw.Draw(pool, 2, 3, "fire", catalog, Facts(), StatusBit, 500, 3000, 1000, 300, seed: 777);
        var y = AmbushDraw.Draw(pool, 2, 3, "fire", catalog, Facts(), StatusBit, 500, 3000, 1000, 300, seed: 777);
        Assert.Equal(x.Ambushed, y.Ambushed);
        Assert.Equal(x.Event?.EventId, y.Event?.EventId);
    }

    [Fact]
    public void A_different_room_off_the_same_seed_can_produce_a_different_sequence()
    {
        var pool = new[] { Row("a", "encounter-event"), Row("b", "encounter-event") };
        var catalog = CatalogOf(pool.ToArray());

        var atRoomA = new List<(bool, string?)>();
        var atRoomB = new List<(bool, string?)>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            var x = AmbushDraw.Draw(pool, 0, 0, "fire", catalog, Facts(), StatusBit, 500, 3000, 1000, 300, seed);
            var y = AmbushDraw.Draw(pool, 9, 9, "fire", catalog, Facts(), StatusBit, 500, 3000, 1000, 300, seed);
            atRoomA.Add((x.Ambushed, x.Event?.EventId));
            atRoomB.Add((y.Ambushed, y.Event?.EventId));
        }
        Assert.NotEqual(atRoomA, atRoomB);
    }
}
