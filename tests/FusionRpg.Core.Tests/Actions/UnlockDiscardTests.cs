using System.Linq;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T20 (action-todo.md, spec-unlock-ladder.md §3, REVISED 2026-08-28): discard. The spec's own text
/// argued for a FLAT tax and explicitly retracted a rung-scaled one — the owner overrode that live,
/// so discard's soul cost now scales with the actor's power (`Θ`) through the shared `P(Θ)` ladder
/// (<see cref="DiscardPolicy"/>), never a private curve. What did NOT change: discard must never
/// touch <see cref="UnlockState.EarnCount"/> — that is the anti-farm property this file exists to
/// prove directly, not infer.
/// </summary>
public class UnlockDiscardTests
{
    sealed class AlwaysHit : IAtomRandom
    {
        public int NextInclusive(int min, int max) => min;
        public int NextPerMille() => 0;
    }

    static UnlockTuning Tuning(int coeffMilli = 100) =>
        new(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: coeffMilli);

    // ---- UnlockState.TryDiscard: the pure, Core-only half ------------------------------------------

    [Fact]
    public void DiscardingAHeldUnlockFreesTheSlotWithoutTouchingEarnCount()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());
        state.TryAccept("skill.b", tuning, new AlwaysHit());
        var earnCountBefore = state.EarnCount;

        var outcome = state.TryDiscard("skill.a");

        Assert.True(outcome.Discarded);
        Assert.Equal(earnCountBefore, state.EarnCount); // the whole anti-farm property, checked directly
        Assert.DoesNotContain(state.Held, h => h.UnlockId == "skill.a");
        Assert.Contains(state.Held, h => h.UnlockId == "skill.b");
    }

    [Fact]
    public void DiscardingSomethingNotHeldRefusesAndChangesNothing()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());

        var outcome = state.TryDiscard("skill.never-held");

        Assert.False(outcome.Discarded);
        Assert.Equal(DiscardRefusalReason.NotHeld, outcome.Reason);
        Assert.Single(state.Held);
    }

    [Fact]
    public void DiscardThenReEarnDoesNotRestoreChance()
    {
        // THE anti-farm test named directly by the spec's own testing strategy: "without it the
        // module has no teeth." Asserted against the pre-discard chance value, not inferred from
        // EarnCount staying put (that is ALSO asserted, but this is the behaviour that actually
        // matters to a player).
        var tuning = Tuning();
        var state = UnlockState.Empty();
        for (var i = 0; i < 10; i++)
            state.TryAccept($"skill.{i}", tuning, new AlwaysHit()); // earnCount -> 10, at cap

        var chanceBeforeDiscard = UnlockLadder.ChanceMilli(state.EarnCount, tuning);

        state.TryDiscard("skill.3"); // frees a slot
        var outcome = state.TryAccept("skill.new", tuning, new AlwaysHit()); // re-earn into it

        Assert.True(outcome.Accepted);
        var chanceAfterReEarn = UnlockLadder.ChanceMilli(state.EarnCount, tuning);
        Assert.True(chanceAfterReEarn < chanceBeforeDiscard,
            $"chance rose or held after a discard+re-earn: before={chanceBeforeDiscard}, after={chanceAfterReEarn}");
        // The re-earn landed at the TOP rung (spec §5: "the next earn arrives at the top rung"), not
        // at whatever rung the discarded item held -- proving the ratchet, not the slot, decided it.
        var newHeld = state.Held.Single(h => h.UnlockId == "skill.new");
        Assert.Equal(11, newHeld.EarnCountAtAcceptance);
        Assert.Equal(10, UnlockLadder.EffectiveRung(newHeld.EarnCountAtAcceptance, tuning).Value); // clamped at rungCap
    }

    [Fact]
    public void ADiscardedSlotsRungNeverLeaksIntoASurvivingUnlocksRung()
    {
        // "A planted occupancy-keyed rung fails" (spec testing strategy), pinned specifically across
        // a discard: unlock B's rung must stay exactly what ITS OWN earnCountAtAcceptance says,
        // regardless of A being removed from the set around it.
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit()); // earnCount 1
        state.TryAccept("skill.b", tuning, new AlwaysHit()); // earnCount 2
        state.TryAccept("skill.c", tuning, new AlwaysHit()); // earnCount 3

        var bBefore = state.Held.Single(h => h.UnlockId == "skill.b");
        var bRungBefore = UnlockLadder.EffectiveRung(bBefore.EarnCountAtAcceptance, tuning);

        state.TryDiscard("skill.a");

        var bAfter = state.Held.Single(h => h.UnlockId == "skill.b");
        Assert.Equal(bRungBefore, UnlockLadder.EffectiveRung(bAfter.EarnCountAtAcceptance, tuning));
        Assert.Equal(2, bAfter.EarnCountAtAcceptance); // literally unchanged -- never re-derived from position
    }

    // ---- DiscardPolicy: power-scaled pricing -------------------------------------------------------

    [Fact]
    public void PriceScalesUpWithTheActorsTheta()
    {
        var tuning = Tuning();
        var lowTheta = DiscardPolicy.PriceOf(theta: 1, tuning);
        var highTheta = DiscardPolicy.PriceOf(theta: 100, tuning);

        Assert.True(highTheta.SoulAmount > lowTheta.SoulAmount,
            $"price did not scale with Θ: low={lowTheta.SoulAmount} high={highTheta.SoulAmount}");
    }

    [Fact]
    public void PriceIsDeterministicForTheSameThetaAndTuning()
    {
        var tuning = Tuning();
        var a = DiscardPolicy.PriceOf(theta: 42, tuning);
        var b = DiscardPolicy.PriceOf(theta: 42, tuning);
        Assert.Equal(a.SoulAmount, b.SoulAmount);
    }

    [Fact]
    public void ALargerCoefficientPricesHigherAtTheSameTheta()
    {
        var cheap = DiscardPolicy.PriceOf(theta: 50, Tuning(coeffMilli: 10));
        var expensive = DiscardPolicy.PriceOf(theta: 50, Tuning(coeffMilli: 1000));
        Assert.True(expensive.SoulAmount > cheap.SoulAmount);
    }

    // ---- UnlockDiscardService: refuse, THEN spend, THEN mutate -------------------------------------

    [Fact]
    public void NotHeldRefusesWithoutConsultingEitherDelegate()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());

        var service = new UnlockDiscardService(
            isMidRun: () => throw new InvalidOperationException("isMidRun consulted for a not-held id"),
            trySpendSoul: _ => throw new InvalidOperationException("soul spend consulted for a not-held id"));

        var outcome = service.TryDiscard(state, "skill.never-held", theta: 10, tuning);

        Assert.False(outcome.Discarded);
        Assert.Equal(DiscardRefusalReason.NotHeld, outcome.Reason);
    }

    [Fact]
    public void MidRunRefusesWithoutSpendingSoulOrMutatingState()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());

        var service = new UnlockDiscardService(
            isMidRun: () => true,
            trySpendSoul: _ => throw new InvalidOperationException("soul spend consulted while mid-run"));

        var outcome = service.TryDiscard(state, "skill.a", theta: 10, tuning);

        Assert.False(outcome.Discarded);
        Assert.Equal(DiscardRefusalReason.MidRun, outcome.Reason);
        Assert.Single(state.Held); // untouched
    }

    [Fact]
    public void InsufficientSoulRefusesAndDiscardsNothing()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());
        var earnCountBefore = state.EarnCount;

        var service = new UnlockDiscardService(isMidRun: () => false, trySpendSoul: _ => false);

        var outcome = service.TryDiscard(state, "skill.a", theta: 10, tuning);

        Assert.False(outcome.Discarded);
        Assert.Equal(DiscardRefusalReason.InsufficientSoul, outcome.Reason);
        Assert.Single(state.Held); // still held -- no state change on refusal
        Assert.Equal(earnCountBefore, state.EarnCount);
    }

    [Fact]
    public void SuccessSpendsTheExactQuotedPriceThenDiscards()
    {
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());

        long? spent = null;
        var service = new UnlockDiscardService(
            isMidRun: () => false,
            trySpendSoul: amount => { spent = amount; return true; });

        var expectedPrice = DiscardPolicy.PriceOf(theta: 77, tuning).SoulAmount;
        var outcome = service.TryDiscard(state, "skill.a", theta: 77, tuning);

        Assert.True(outcome.Discarded);
        Assert.Equal(expectedPrice, spent);
        Assert.Empty(state.Held);
    }

    [Fact]
    public void DiscardIsAlwaysAvailableNeverOnACooldownAcrossRepeatedCalls()
    {
        // "Always available... never on a cooldown" (spec §3) -- proven by discarding twice in a row
        // with no time/tick argument anywhere in the API for a cooldown to even read.
        var tuning = Tuning();
        var state = UnlockState.Empty();
        state.TryAccept("skill.a", tuning, new AlwaysHit());
        state.TryAccept("skill.b", tuning, new AlwaysHit());

        var service = new UnlockDiscardService(isMidRun: () => false, trySpendSoul: _ => true);

        var first = service.TryDiscard(state, "skill.a", theta: 10, tuning);
        var second = service.TryDiscard(state, "skill.b", theta: 10, tuning);

        Assert.True(first.Discarded);
        Assert.True(second.Discarded);
    }
}
