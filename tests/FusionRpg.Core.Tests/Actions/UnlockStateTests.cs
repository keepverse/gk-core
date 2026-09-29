using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T19 (action-todo.md, spec-unlock-ladder.md §1): <c>earnCount</c> plus the held set, and the one
/// rule that makes the whole module have teeth — "a roll with no free slot is NOT an earn and does
/// not advance the ratchet." T20's discard tests live in <see cref="UnlockDiscardTests"/>.
/// </summary>
public class UnlockStateTests
{
    /// <summary>Always misses (never below any realistic chance) — proves a miss changes nothing.</summary>
    sealed class AlwaysMiss : IAtomRandom
    {
        public int NextInclusive(int min, int max) => max;
        public int NextPerMille() => 999;
    }

    /// <summary>Always hits (chance is always > 0, so a roll of 0 always lands).</summary>
    sealed class AlwaysHit : IAtomRandom
    {
        public int NextInclusive(int min, int max) => min;
        public int NextPerMille() => 0;
    }

    /// <summary>Throws if consulted at all — proves the capacity check runs BEFORE any roll.</summary>
    sealed class PoisonRng : IAtomRandom
    {
        public int NextInclusive(int min, int max) => throw new InvalidOperationException("rng consulted despite no free slot");
        public int NextPerMille() => throw new InvalidOperationException("rng consulted despite no free slot");
    }

    static UnlockTuning TinyTuning() => new(P1Milli: 1000, DeltaMilli: 999, FloorMilli: 1, HeldCap: 2, RungCap: 2, DiscardTaxCoeffMilli: 100);

    [Fact]
    public void ARollMissDoesNotAdvanceEarnCountOrTheHeldSet()
    {
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var state = UnlockState.Empty();

        var outcome = state.TryAccept("skill.fireball", tuning, new AlwaysMiss());

        Assert.False(outcome.Accepted);
        Assert.Equal(UnlockRefusalReason.RollMissed, outcome.Reason);
        Assert.Equal(0, state.EarnCount);
        Assert.Empty(state.Held);
    }

    [Fact]
    public void ASuccessfulRollAdvancesEarnCountAndRecordsItOnTheHeldUnlock()
    {
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var state = UnlockState.Empty();

        var outcome = state.TryAccept("skill.fireball", tuning, new AlwaysHit());

        Assert.True(outcome.Accepted);
        Assert.Equal(1, state.EarnCount);
        var held = Assert.Single(state.Held);
        Assert.Equal("skill.fireball", held.UnlockId);
        Assert.Equal(1, held.EarnCountAtAcceptance); // the NEW earnCount, not the pre-roll value
    }

    [Fact]
    public void AtCapacityRefusesWithoutConsultingTheRngAtAll()
    {
        var tuning = TinyTuning(); // cap 2
        var state = UnlockState.Empty();
        state.TryAccept("a", tuning, new AlwaysHit());
        state.TryAccept("b", tuning, new AlwaysHit());
        Assert.Equal(2, state.Held.Count);

        // A poison RNG that throws if touched -- if this test passes, the capacity gate really did
        // run before any roll, not just "happened to miss".
        var outcome = state.TryAccept("c", tuning, new PoisonRng());

        Assert.False(outcome.Accepted);
        Assert.Equal(UnlockRefusalReason.AtCapacity, outcome.Reason);
        Assert.Equal(2, state.EarnCount); // unchanged -- not an earn
        Assert.Equal(2, state.Held.Count);
    }

    [Fact]
    public void NoFreeSlotMeansNoEarnEvenThoughTheRollWouldHaveHit()
    {
        // The module's own anti-exploit teeth, named directly: an actor sitting at capacity gains
        // NOTHING from a lucky roll -- the ratchet is untouched until a slot opens.
        var tuning = TinyTuning();
        var state = UnlockState.Empty();
        state.TryAccept("a", tuning, new AlwaysHit());
        state.TryAccept("b", tuning, new AlwaysHit());
        var earnCountBefore = state.EarnCount;

        state.TryAccept("c", tuning, new AlwaysHit()); // would hit -- but no slot

        Assert.Equal(earnCountBefore, state.EarnCount);
    }

    [Fact]
    public void ChanceReadForTheNextRollUsesTheCurrentEarnCountNotTheHeldCount()
    {
        // earnCount and held-count happen to move together while nothing is discarded -- T20 is
        // where they diverge. This test pins the CURRENT wiring: TryAccept reads UnlockLadder.Chance
        // off state.EarnCount, proven by a roll landing exactly at the earn-2 boundary.
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var state = UnlockState.Empty();
        state.TryAccept("a", tuning, new AlwaysHit()); // earnCount -> 1

        var chanceForNextRoll = UnlockLadder.ChanceMilli(state.EarnCount, tuning); // n=1 -> 440 per the formula
        var borderlineRng = new FixedPerMilleRng(chanceForNextRoll - 1); // just inside the hit range
        var outcome = state.TryAccept("b", tuning, borderlineRng);

        Assert.True(outcome.Accepted);
    }

    sealed class FixedPerMilleRng : IAtomRandom
    {
        readonly int _value;
        public FixedPerMilleRng(int value) => _value = value;
        public int NextInclusive(int min, int max) => min;
        public int NextPerMille() => _value;
    }

    [Fact]
    public void TheHeldUnlocksRungIsDerivedFromItsStoredEarnCountNeverAResolvedColumn()
    {
        var tuning = new UnlockTuning(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var state = UnlockState.Empty();
        for (var i = 0; i < 5; i++)
            state.TryAccept($"skill.{i}", tuning, new AlwaysHit());

        foreach (var held in state.Held)
        {
            // HeldUnlock exposes exactly two fields; there is no third "rung" field for a caller to
            // read a stale value from -- the rung is always this recomputation.
            var rung = UnlockLadder.EffectiveRung(held.EarnCountAtAcceptance, tuning);
            Assert.Equal(held.EarnCountAtAcceptance, rung.Value); // rungCap (10) not yet reached at 5 earns
        }
    }

    [Fact]
    public void SameSeedSameSequenceProducesIdenticalOutcomesAcrossTwoIndependentRuns()
    {
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 5, RungCap: 5, DiscardTaxCoeffMilli: 100);
        var ids = new[] { "skill.a", "skill.b", "skill.c", "skill.d", "skill.e", "skill.f", "skill.g" };

        UnlockState Run()
        {
            var state = UnlockState.Empty();
            var rng = new AtomRandom(runSeed: 424242UL, streamName: "unlock-test");
            foreach (var id in ids) state.TryAccept(id, tuning, rng);
            return state;
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first.EarnCount, second.EarnCount);
        Assert.Equal(first.Held.Count, second.Held.Count);
        for (var i = 0; i < first.Held.Count; i++)
        {
            Assert.Equal(first.Held[i].UnlockId, second.Held[i].UnlockId);
            Assert.Equal(first.Held[i].EarnCountAtAcceptance, second.Held[i].EarnCountAtAcceptance);
        }
    }

    [Fact]
    public void RestoringFromPersistedHeldRowsInAShuffledOrderProducesTheSameRungsRegardlessOfOrder()
    {
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var rows = new[]
        {
            new HeldUnlock("skill.a", 1),
            new HeldUnlock("skill.b", 2),
            new HeldUnlock("skill.c", 3),
        };

        var inOrder = UnlockState.FromPersisted(3, rows);
        var shuffled = UnlockState.FromPersisted(3, new[] { rows[2], rows[0], rows[1] });

        long RungSum(UnlockState s)
        {
            long sum = 0;
            foreach (var h in s.Held) sum += UnlockLadder.EffectiveRung(h.EarnCountAtAcceptance, tuning).Value;
            return sum;
        }

        Assert.Equal(RungSum(inOrder), RungSum(shuffled));
        Assert.Equal(inOrder.EarnCount, shuffled.EarnCount);
    }
}
