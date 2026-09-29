using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// passive-tree E3 / G2 (spec-mechanism-wiring.md §4.2): before this fix,
/// <c>BattleRunState.RecomposeDerived</c> had exactly one production call site — the construction-time
/// <c>ActiveAuras</c> loop — so any mechanism whose <c>BattleDerivedModifierLedger</c> contribution
/// landed AFTER construction (a status applied mid-battle, a gate quantity crossing a threshold) was
/// composed into `Derived` by nothing and read by nothing until the next battle. The fix adds exactly
/// one new call site: `BattleEngine.Resolve`'s round loop now calls
/// `state.RecomposeDerivedForAllActors()` once per actor at the start of every round.
///
/// `BattleEffectHost.AddDerivedContribution` is the minimal seam this test (and, later, aura-skill
/// T13's real live-toggle trigger) uses to reach `DerivedLedger.Add` through the same
/// `onEffectHostReady` hook every other Battle-adoption test already uses — there is no shipped content
/// today that writes to `DerivedLedger` after construction (T13 is unbuilt), so this test simulates
/// that future mechanism directly rather than through the effect/atom pipeline, which has no consumer
/// wired to this ledger yet.
/// </summary>
public class PassiveTreeMechanismRoundRecomposeTests
{
    static BattleActorSetup Actor(string key, string side, long? maxHp = null, long? atk = null) => new()
    {
        Key = key, Side = side, SpeciesId = "e3-species", TypeId = 10_009, Level = 6,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(6), Atk = atk ?? BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup(long waveMaxHpMultiple) => new()
    {
        WaveId = "e3-wave",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave", maxHp: BattleRuleset.BaseHp(6) * waveMaxHpMultiple, atk: 1) },
    };

    [Fact]
    public void A_derived_contribution_added_after_construction_still_reaches_combat_across_multiple_rounds()
    {
        // "Added after construction" (via onEffectHostReady, which fires before the ActiveAuras
        // recompose loop but after every actor's baseline Derived is already frozen) is exactly the
        // shape G2 names: a mechanism whose value the construction-time recompose never sees. Only the
        // NEW per-round call site can pick this up.
        var without = BattleEngine.Resolve(Setup(waveMaxHpMultiple: 100), seed: 7);
        var with = BattleEngine.Resolve(Setup(waveMaxHpMultiple: 100), seed: 7, onEffectHostReady: host =>
        {
            host.AddDerivedContribution!("squad:0", DerivedStatChannels.CombatPowerOmni,
                "mechanism:e3-probe", 500);
        });

        Assert.True(with.Rounds > 1, "the wave HP budget is sized to force a multi-round battle");
        var boosted = with.Actors.Single(a => a.Key == "squad:0").DamageDealt;
        var unboosted = without.Actors.Single(a => a.Key == "squad:0").DamageDealt;
        Assert.True(boosted > unboosted,
            $"expected more cumulative damage with combat.power.omni boosted after construction; boosted={boosted}, unboosted={unboosted}");
    }

    [Fact]
    public void The_per_round_recompose_never_compounds_a_contribution_across_rounds()
    {
        // BattleDerivedModifierLedger.Recompose always rebuilds from the frozen BaseDerived, never from
        // Derived's own prior value (the arithmetic proof §4.2 cites) -- so calling it every round must
        // be a no-op beyond the first application. A bug that accumulated the contribution on top of
        // itself each round would make a LONGER fight's per-round damage rate visibly grow; this
        // compares a short and a long fight (same seed, same contribution) and requires the per-round
        // rate to stay close, not diverge.
        const string channel = DerivedStatChannels.CombatPowerOmni;
        void AddProbe(FusionRpg.Core.Battle.BattleEffectHost host) =>
            host.AddDerivedContribution!("squad:0", channel, "mechanism:e3-probe", 800);

        var shortFight = BattleEngine.Resolve(Setup(waveMaxHpMultiple: 20), seed: 11, onEffectHostReady: AddProbe);
        var longFight = BattleEngine.Resolve(Setup(waveMaxHpMultiple: 200), seed: 11, onEffectHostReady: AddProbe);

        Assert.True(longFight.Rounds > shortFight.Rounds * 2,
            $"expected the long fight to run meaningfully more rounds; short={shortFight.Rounds}, long={longFight.Rounds}");

        var shortRate = (double)shortFight.Actors.Single(a => a.Key == "squad:0").DamageDealt / shortFight.Rounds;
        var longRate = (double)longFight.Actors.Single(a => a.Key == "squad:0").DamageDealt / longFight.Rounds;

        // A generous tolerance (25%) absorbs the initiative-order/crit-roll noise a longer fight
        // naturally accumulates more of -- what a compounding bug would produce is not 25% drift, it is
        // the long fight's rate landing many TIMES the short fight's, since every extra round would
        // re-add the same 800 on top of an already-boosted value.
        var ratio = longRate / shortRate;
        Assert.InRange(ratio, 0.75, 1.25);
    }
}
