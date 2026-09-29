using FusionRpg.Core.PassiveTree.GateCounters;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>Task G2 — spec-gate-counters.md §4.3: accumulate in memory, flush is a drain-and-clear,
/// never a write from this type itself.</summary>
public class GateCounterAccumulatorTests
{
    static readonly GateCounterKey Key = new("player", "player:1", "status_applied", "wither");

    [Fact]
    public void Credit_accumulates_repeated_calls_for_the_same_key()
    {
        var acc = new GateCounterAccumulator();
        acc.Credit(Key);
        acc.Credit(Key);
        acc.Credit(Key);

        Assert.Equal(3L, acc.Snapshot()[Key]);
    }

    [Fact]
    public void DrainAndClear_returns_the_pending_deltas_and_resets_the_window()
    {
        var acc = new GateCounterAccumulator();
        acc.Credit(Key, 5);

        var drained = acc.DrainAndClear();

        Assert.Equal(5L, drained[Key]);
        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void DrainAndClear_on_an_empty_accumulator_returns_empty()
    {
        var acc = new GateCounterAccumulator();
        Assert.Empty(acc.DrainAndClear());
    }

    [Fact]
    public void A_second_flush_window_starts_from_zero_never_double_counting_a_drained_window()
    {
        var acc = new GateCounterAccumulator();
        acc.Credit(Key, 2);
        acc.DrainAndClear();

        acc.Credit(Key, 1);
        var second = acc.DrainAndClear();

        Assert.Equal(1L, second[Key]);
    }

    [Fact]
    public void Different_subjects_accumulate_independently()
    {
        var acc = new GateCounterAccumulator();
        var poison = Key with { SubjectId = "poison" };
        acc.Credit(Key);
        acc.Credit(poison);
        acc.Credit(poison);

        var snap = acc.Snapshot();
        Assert.Equal(1L, snap[Key]);
        Assert.Equal(2L, snap[poison]);
    }

    [Fact]
    public void A_zero_or_negative_credit_amount_is_refused()
    {
        var acc = new GateCounterAccumulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.Credit(Key, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.Credit(Key, -1));
    }

    [Fact]
    public void Crediting_past_long_MaxValue_throws_rather_than_wraps()
    {
        // docs/architecture/numeric-types.md: overflow throws, never wraps -- a counter is a magnitude.
        var acc = new GateCounterAccumulator();
        acc.Credit(Key, long.MaxValue);
        Assert.Throws<OverflowException>(() => acc.Credit(Key, 1));
    }
}
