using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.4 (spec-wild-room.md §2, "The outcome draw") — `WildOutcome.Draw`: an outcome golden
/// per disposition band (the todo's own Verify line), read as a distribution proof over many trials
/// since a single weighted draw's own RNG output is not hand-computable without duplicating
/// `AtomRngImpl`'s internals.</summary>
public class WildOutcomeTests
{
    // The four real shipped bands (dungeon.v1.json's own wild.outcome.* rows, already read this
    // session while building D4.1-D4.3) -- pinned as plain values since WildOutcome.Draw's own
    // signature takes a WildOutcomeRow directly, not a DungeonTuning.
    static readonly WildOutcomeRow Eager = new(500, 300, 100, 100);
    static readonly WildOutcomeRow Open = new(300, 400, 200, 100);
    static readonly WildOutcomeRow Wary = new(120, 380, 350, 150);
    static readonly WildOutcomeRow Hostile = new(30, 170, 200, 600);

    public static IEnumerable<object[]> Bands()
    {
        yield return new object[] { Eager };
        yield return new object[] { Open };
        yield return new object[] { Wary };
        yield return new object[] { Hostile };
    }

    [Fact]
    public void Draw_is_deterministic_same_seed_same_stream_always_the_same_kind()
    {
        var a = WildOutcome.Draw(Wary, 424242, "dungeon:wild:0:0:1");
        var b = WildOutcome.Draw(Wary, 424242, "dungeon:wild:0:0:1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_stream_name_genuinely_participates_in_the_roll_not_just_the_seed()
    {
        var kinds = new HashSet<WildOutcomeKind>();
        for (var seq = 1; seq <= 12; seq++)
            kinds.Add(WildOutcome.Draw(Wary, 424242, $"dungeon:wild:0:0:{seq}"));
        Assert.True(kinds.Count > 1); // 12 different streams off one seed must not all collapse to one kind
    }

    [Theory]
    [MemberData(nameof(Bands))]
    public void Every_real_band_only_ever_draws_one_of_the_four_named_kinds(WildOutcomeRow row)
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var kind = WildOutcome.Draw(row, seed, $"dungeon:wild:test:{seed}");
            Assert.True(Enum.IsDefined(typeof(WildOutcomeKind), kind));
        }
    }

    [Fact]
    public void Eager_band_draws_joins_far_more_often_than_hostile_band_a_distribution_golden_over_1000_trials()
    {
        // eager.joinsMilli=500 vs hostile.joinsMilli=30 -- a stark, load-bearing real-content gap
        // that must show up over enough trials, proving the row's own weights are genuinely read.
        static int CountJoins(WildOutcomeRow row, string salt)
        {
            var joins = 0;
            for (var seed = 0; seed < 1000; seed++)
                if (WildOutcome.Draw(row, seed, $"dungeon:wild:{salt}:{seed}") == WildOutcomeKind.Joins) joins++;
            return joins;
        }
        var eagerJoins = CountJoins(Eager, "eager-golden");
        var hostileJoins = CountJoins(Hostile, "hostile-golden");
        Assert.True(eagerJoins > hostileJoins * 5); // tuning's own gap is >16x (500 vs 30)
    }

    [Fact]
    public void Hostile_band_draws_attacks_far_more_often_than_eager_band()
    {
        static int CountAttacks(WildOutcomeRow row, string salt)
        {
            var attacks = 0;
            for (var seed = 0; seed < 1000; seed++)
                if (WildOutcome.Draw(row, seed, $"dungeon:wild:{salt}:{seed}") == WildOutcomeKind.Attacks) attacks++;
            return attacks;
        }
        Assert.True(CountAttacks(Hostile, "hostile-atk") > CountAttacks(Eager, "eager-atk") * 3); // 600 vs 100
    }

    [Fact]
    public void A_lopsided_row_still_only_draws_its_one_nonzero_option()
    {
        // WildOutcome.Draw trusts its input -- the sum-to-1000 rule is DungeonTuning.cs's own loader
        // job (already tested there); this function must simply draw over whatever weights it gets.
        var lopsided = new WildOutcomeRow(1, 0, 0, 0);
        Assert.Equal(WildOutcomeKind.Joins, WildOutcome.Draw(lopsided, 7, "dungeon:wild:lopsided:1"));
    }

    [Fact]
    public void A_null_row_throws()
    {
        Assert.Throws<ArgumentNullException>(() => WildOutcome.Draw(null!, 1, "s"));
    }

    [Fact]
    public void All_four_milli_at_zero_throws_no_drawable_option()
    {
        var empty = new WildOutcomeRow(0, 0, 0, 0);
        Assert.Throws<NoDrawableWeightedOptionException>(() => WildOutcome.Draw(empty, 1, "s"));
    }
}
