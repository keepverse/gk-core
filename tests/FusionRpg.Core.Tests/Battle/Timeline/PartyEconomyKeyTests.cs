using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// D2.11 (economy-key half) — a raid's own parties each get an INDEPENDENT budget under a
/// <see cref="TurnEconomyScope.PerSide"/> economy, keyed `side:{side}:p{PartyIndex}` rather than
/// sharing one `side:{side}` pool across every actor on that side. `BattleEngine.Resolve`'s own
/// `EconomyKey` is a private local function (unreachable directly, like the rest of
/// <c>BattleRunState</c>'s internals) — proven here the same way <c>ModeProfileCapabilityTests</c>
/// proves `ActionSlots`/`W`: end to end, through a real `Resolve` call, by its OBSERVABLE effect.
/// No shipped profile uses a `PerSide` economy today (`OneActionPerTurnEconomy`/`ActionPointsEconomy`
/// both report `PerActor`, including `Delve`'s own `points: true` choice) — this proves the hook is
/// correctly wired for the one economy type that IS `PerSide`-scoped (`PressTurnEconomy`), the same
/// "declared, not yet exercised by any catalog row" status `UsesTimelineDispatch` already carries.
/// </summary>
public class PartyEconomyKeyTests
{
    static BattleModeProfile PressTurnProfile(long startingIcons) => new()
    {
        ProfileId = "test-press-turn",
        AdvancePolicy = AdvancePolicyKind.NextEvent,
        W = 1,
        WScope = WScope.Global,
        DefaultCommitment = Commitment.LateBound,
        PassQuantum = 1,
        ForecastExactness = ForecastExactness.Exact,
        NewEconomy = () => new PressTurnEconomy(startingIcons),
        MaxRounds = 3,
        RoundDurationMs = 1000
    };

    [Fact]
    public void PressTurnEconomy_isolates_budgets_by_key_a_pure_check_with_no_battle_engine_involved()
    {
        var economy = new PressTurnEconomy(startingIcons: 1);
        Assert.True(economy.TryAcquire("side:squad:p0", 1, 0));
        Assert.True(economy.TryAcquire("side:squad:p1", 1, 0)); // a DIFFERENT key -- its own icon, unaffected by p0's spend
        Assert.False(economy.TryAcquire("side:squad:p0", 1, 0)); // p0's own icon is already spent
    }

    [Fact]
    public void Two_squad_actors_with_different_party_indices_both_act_under_one_shared_starting_icon()
    {
        // With ONE starting icon and the OLD "side:squad" key, only the first actor in initiative
        // order could ever act -- the second's TryAcquire would fail forever (PressTurn's icons
        // reset per TURN, and both actors act within the same turn/pass). With PartyIndex-scoped
        // keys, each party gets its own icon, so BOTH act.
        var stomp = BattleGoldenTests.StompSetup();
        var squad = new[]
        {
            stomp.Squad[0] with { PartyIndex = 0 },
            stomp.Squad[1] with { PartyIndex = 1 },
        };
        var setup = stomp with { Squad = squad };

        var report = BattleEngine.Resolve(setup, seed: 9001, profile: PressTurnProfile(startingIcons: 1));

        var p0 = report.Actors.Single(a => a.Key == "squad:0");
        var p1 = report.Actors.Single(a => a.Key == "squad:1");
        Assert.True(p0.DamageDealt > 0, "party 0 never got to act -- economy key did not isolate its budget");
        Assert.True(p1.DamageDealt > 0, "party 1 never got to act -- it shared party 0's icon instead of getting its own");
    }

    [Fact]
    public void Without_a_party_index_two_squad_actors_still_share_one_side_wide_budget_no_behaviour_change()
    {
        // The control case: PartyIndex null (every existing caller's shape) reproduces today's
        // documented press-turn contract exactly -- a SHARED side pool, so with only one starting
        // icon at most one of the two squad actors can act in the round.
        var report = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), seed: 9002, profile: PressTurnProfile(startingIcons: 1));

        var p0 = report.Actors.Single(a => a.Key == "squad:0");
        var p1 = report.Actors.Single(a => a.Key == "squad:1");
        Assert.False(p0.DamageDealt > 0 && p1.DamageDealt > 0, "both acted despite sharing one icon -- the shared-budget contract moved");
    }
}
