using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W23 (spec-world-movement.md §What `hold` is for): stances finally do something.
///
/// `stance` was in the movement spec's command table from wave 1 and was never a command kind, so a
/// legion's posture was whatever the template authored and could never change — which made both
/// `scout` and `hold` dead letters. Closing that turned up something worse: **nothing in the game
/// could heal**. Wounds only ever accumulated, so every legion was on a one-way trip to death.
/// </summary>
public class StanceTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Stance(string entityId, string stance) => new()
    {
        CommanderId = "dave",
        CommandId = "s-" + entityId,
        Kind = WorldCommandKinds.Stance,
        EntityId = entityId,
        Stance = stance
    };

    static WorldCommand Move(string entityId, params string[] lanePath) => new()
    {
        CommanderId = "dave",
        CommandId = "m-" + entityId,
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = lanePath
    };

    static WorldEntity Legion(WorldState w) => w.Entities.Single(e => e.EntityId == "e-dave-legion-1");

    static WorldState Wounded(WorldState w, int wounds) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == "e-dave-legion-1"
                ? e with { Members = e.Members.Select(m => m with { Wounds = wounds }).ToList() }
                : e)
            .ToList()
    };

    static WorldState WithCarriedLoam(WorldState w, long amount) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == "e-dave-legion-1" ? e with { CarriedLoam = amount } : e)
            .ToList()
    };

    // ---- the command ---------------------------------------------------------------------

    [Fact]
    public void A_legion_can_be_told_to_change_posture()
    {
        var result = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "scout") }, seed: 1);

        Assert.Equal("scout", Legion(result.World).Stance);
        Assert.Empty(result.Report.Dropped);
    }

    [Fact]
    public void A_posture_nobody_has_heard_of_is_refused_at_admission()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World(), Stance("e-dave-legion-1", "skulk"));

        Assert.False(ok);
        Assert.Equal("stance.unknown", reason);
    }

    [Fact]
    public void Committing_to_a_posture_costs_the_turn_you_commit()
    {
        // Digging in and *then* marching your full distance would make the defensive bonus free.
        var world = World();
        var first = TurnEngine.Step(world, new[] { Stance("e-dave-legion-1", "hold") }, seed: 1);

        // The legion still had its full budget during the turn it gave the order…
        Assert.Equal("hold", Legion(first.World).Stance);
        // …and only pays for it at the refill that closes that turn.
        // Hold-allowance (spec-hold-allowance.md §Design 1): a holder refills to the tuned
        // garrison act allowance — act money, never march money — not 0.
        Assert.Equal(WorldTuningHub.Tuning.Movement.HoldAllowanceMilli, Legion(first.World).MovementRemaining);
    }

    // ---- scout ---------------------------------------------------------------------------

    [Fact]
    public void Scouting_costs_half_a_turns_march()
    {
        var scouting = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "scout") }, seed: 1);

        Assert.Equal(MovementPolicy.ScoutPointsPerTurn, Legion(scouting.World).MovementRemaining);
        Assert.Equal(MovementPolicy.PointsPerTurn / 2, Legion(scouting.World).MovementRemaining);
    }

    [Fact]
    public void Scouting_buys_twice_the_sight()
    {
        var scouting = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "scout") }, seed: 1);
        var view = new BelievedWorldView(scouting.World, "dave");

        // Two lanes out is normally invisible; a scout sees it.
        Assert.NotEqual(IntelState.Unknown, view.StateOf("ash-waste"));
        Assert.Equal(IntelState.Watched, view.StateOf("ash-waste"));
    }

    // ---- hold ----------------------------------------------------------------------------

    [Fact]
    public void A_held_legion_cannot_march_and_is_told_why()
    {
        var dug_in = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "hold") }, seed: 1);
        var result = TurnEngine.Step(dug_in.World, new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "entity.held");
        Assert.Equal("homeworld", Legion(result.World).AtSectorId);
    }

    [Fact]
    public void A_held_legions_non_march_orders_pass_the_held_gate()
    {
        // Hold-allowance (spec-hold-allowance.md §Design 3): the held gate is Move-only — a
        // holder's priced-act-shaped orders are never `entity.held`-dropped at Reveal. Claiming
        // the ground it already stands on settles as `claim.already-yours`, which proves the
        // order survived Reveal (a gate drop would be `entity.held` in Dropped instead, and no
        // budget refusal exists yet — that is budget-debit's `entity.spent`, not this gate's).
        var dug_in = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "hold") }, seed: 1);
        var claim = new WorldCommand
        {
            CommanderId = "dave",
            CommandId = "c-held",
            Kind = WorldCommandKinds.Claim,
            EntityId = "e-dave-legion-1",
            SectorId = "homeworld"
        };
        var result = TurnEngine.Step(dug_in.World, new[] { claim }, seed: 1);

        Assert.DoesNotContain(result.Report.Dropped, e => e.Detail == "entity.held");
        Assert.Contains(result.Report.Entries, e => e.Detail == "claim.already-yours:homeworld");
    }

    [Fact]
    public void Holding_in_supply_recovers_wounds()
    {
        var world = Wounded(World(), wounds: 90);
        var held = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { Stance = "hold" } : e)
                .ToList()
        };

        var result = TurnEngine.Step(held, Array.Empty<WorldCommand>(), seed: 1);

        var after = Legion(result.World).Members.First();
        Assert.True(after.Wounds < 90, "a garrison in supply should be recovering");
        Assert.Equal(90 - 110 * MovementPolicy.RecoveryMilli / 1000, after.Wounds);
    }

    [Fact]
    public void Recovery_never_takes_a_member_past_whole()
    {
        var world = Wounded(World(), wounds: 3);
        var held = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { Stance = "hold" } : e)
                .ToList()
        };

        var result = TurnEngine.Step(held, Array.Empty<WorldCommand>(), seed: 1);
        Assert.All(Legion(result.World).Members, m => Assert.Equal(0, m.Wounds));
    }

    /// <summary>
    /// Rewritten for spec-loam-legions.md: the currency is carried loam now, not wounds, but the
    /// property is the same one — holding is not a substitute for a supply line, it feeds nobody.
    /// </summary>
    [Fact]
    public void Standing_still_out_of_supply_still_burns()
    {
        var world = WithCarriedLoam(World(), amount: 100);
        var stranded = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1"
                    ? e with { Stance = "hold", AtSectorId = "verdant-shelf" }
                    : e)
                .ToList()
        };

        var burn = LegionSupply.Burn(Legion(stranded));
        var result = TurnEngine.Step(stranded, Array.Empty<WorldCommand>(), seed: 1);

        Assert.Equal(100 - burn, Legion(result.World).CarriedLoam);
    }

    [Fact]
    public void A_marching_legion_recovers_nothing_even_at_home()
    {
        var world = Wounded(World(), wounds: 40);
        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        Assert.Equal(40, Legion(result.World).Members.First().Wounds);
    }

    // `A_dug_in_defender_counts_as_stationary_even_when_nobody_moved` deleted —
    // actor-hub-and-combat-power-solid-fixing T20: it unit-tested the deleted
    // `PlaceholderBattleResolver`'s own entrenchment-multiplier reading of `defender.Stance`/
    // `DefenderStationary` directly, a mechanic that existed only inside that class for
    // non-district `BattleKinds`. `DistrictAssaultResolver` now refuses every non-district kind
    // outright (no invented winner), so there is no successor entrenchment logic for a Sector-kind
    // fight to test; District's own, separately-tested entrenched-defender bonus
    // (`SiegeTuningPolicy.Objective.DistrictDefenderBonusMilli`) is a different mechanism, covered
    // by `DistrictAssaultResolverTests`/the real siege suite, not this one.

    // ---- dowse (world-stage W30) ----------------------------------------------------------

    [Fact]
    public void Dowse_is_the_exact_same_literal_prospecting_matches_on()
    {
        // The whole defect §8c.4 named: a mismatched pair passes admission and reveals nothing,
        // and no test that only checks "was the order accepted" would ever catch it.
        Assert.Equal(Prospecting.DowserStance, MovementPolicy.Dowse);
    }

    [Fact]
    public void A_dowse_order_is_admitted_where_it_used_to_be_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World(), Stance("e-dave-legion-1", "dowse"));
        Assert.True(ok, reason);
    }

    [Fact]
    public void Dowsing_costs_the_tuned_budget_not_the_full_march()
    {
        var dowsing = TurnEngine.Step(World(), new[] { Stance("e-dave-legion-1", "dowse") }, seed: 1);

        Assert.Equal(WorldTuningHub.Tuning.Movement.DowseBudgetMilli, Legion(dowsing.World).MovementRemaining);
        // The silent half of the defect: falling through to the default arm would hand back the
        // full march budget instead, and an "order accepted" check alone would never notice.
        Assert.NotEqual(MovementPolicy.PointsPerTurn, Legion(dowsing.World).MovementRemaining);
    }

    [Fact]
    public void Stances_are_deterministic_and_independent_of_command_order()
    {
        var a = Stance("e-dave-legion-1", "scout");
        var b = new WorldCommand { CommanderId = "wild", CommandId = "s1", Kind = WorldCommandKinds.StandFast };

        Assert.Equal(
            TurnEngine.Step(World(), new[] { a, b }, seed: 1).StateHash,
            TurnEngine.Step(World(), new[] { b, a }, seed: 1).StateHash);
    }
}
