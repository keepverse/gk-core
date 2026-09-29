using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// base-defense `combatant-kind` (spec-combatant-kind.md): a structure enters a battle without
/// behaving like a creature — never takes a turn, never keeps a battle alive, but is a real, targetable,
/// damageable participant. Every test here is proven through <see cref="BattleEngine.Resolve"/>'s
/// public surface or the <see cref="BattleEngine.HeldActionIdsForTest"/> seam added alongside it —
/// <c>BattleRunState</c> itself stays private/nested per B13's own deviation note, and nothing in the
/// round loop reads <c>HeldActionsOf</c> for real behavior yet (same gap
/// <c>EquippedActionIdsReportingTests</c> already documents for the pre-existing loadout mechanism),
/// so the garrison union can only be proven through that seam, not an end-to-end battle outcome.
/// </summary>
public class CombatantKindTests
{
    static BattleActorSetup Animate(string key, string side, long maxHp = 1000, long atk = 100, long defense = 0,
        IReadOnlyList<string>? equipped = null) => new()
    {
        Key = key, Side = side, SpeciesId = "ck-species", TypeId = 20_001, Level = 3,
        MaxHp = maxHp, Atk = atk, Defense = defense, EquippedActionIds = equipped,
    };

    static BattleActorSetup Structure(string key, string side, long maxHp = 1000, string? garrisonedBy = null,
        IReadOnlyList<string>? equipped = null) => new()
    {
        Key = key, Side = side, SpeciesId = "ck-structure", TypeId = 20_002, Level = 0,
        MaxHp = maxHp, Atk = 0, Defense = 0,
        Kind = CombatantKind.Structure, GarrisonedBy = garrisonedBy, EquippedActionIds = equipped,
    };

    static CompiledAction Dummy(string actionId) => new(
        // AE1.4: a SKILL must land on a real rung row (a skill whose effective rung has no row throws
        // by design); rung 1 is this fixture's own placeholder.
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: Array.Empty<ActionTag>(),
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    [Fact]
    public void Kind_is_not_serialized()
    {
        var json = JsonSerializer.Serialize(Structure("wave:wall", "wave"));
        Assert.DoesNotContain("\"Kind\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"GarrisonedBy\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Structures_do_not_count_toward_any_active()
    {
        // Wave is nothing but a wall: AnyActive("wave") must be false from the first check, so the
        // battle never even schedules a round and resolves as an immediate Victory — even though the
        // wall itself is fully alive (Hp == MaxHp) the whole time.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[] { Structure("wave:wall", "wave", maxHp: 5000) },
        }, seed: 1);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
        var wall = report.Actors.Single(a => a.Key == "wave:wall");
        Assert.Equal(5000, wall.HpRemaining);
        Assert.True(wall.Survived);
    }

    [Fact]
    public void Battle_ends_when_all_animate_defenders_die_with_walls_still_standing()
    {
        // A weak animate defender alongside a wall tough enough to outlast the whole fight: the siege
        // must end the moment the defender dies, not stall out waiting for the wall to fall too.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad", maxHp: 5000, atk: 5000) },
            Wave = new[]
            {
                Animate("wave:defender", "wave", maxHp: 1, atk: 0, defense: 0),
                Structure("wave:wall", "wave", maxHp: long.MaxValue / 2),
            },
        }, seed: 3);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
        var wall = report.Actors.Single(a => a.Key == "wave:wall");
        Assert.True(wall.Survived, "the wall was never the objective and must still be standing");
    }

    [Fact]
    public void Structures_never_deal_damage_over_many_rounds()
    {
        // Both sides carry a durable animate member (so the battle actually runs many rounds) plus a
        // wall on the wave side. Whatever it would take to attack — being selected into the initiative
        // order (combatant-kind §3) or falling back to a basic attack with no loadout (§5) — the wall's
        // own DamageDealt tally must stay zero, since BattleRunState.DispatchHit is the ONLY place that
        // increments it and it is reachable only for an actor RunBasicAttackStep's caller selected from
        // `order`.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad", maxHp: 200_000, atk: 1) },
            Wave = new[]
            {
                Animate("wave:defender", "wave", maxHp: 200_000, atk: 1),
                Structure("wave:wall", "wave", maxHp: 200_000),
            },
        }, seed: 11);

        var wall = report.Actors.Single(a => a.Key == "wave:wall");
        Assert.Equal(0, wall.DamageDealt);
        Assert.Equal(0, wall.Kills);
    }

    [Fact]
    public void Structures_are_targetable_and_damageable()
    {
        // With no board, targeting falls back to SourceOrder (BasicAttack.cs / StubIntentSource's own
        // documented no-board behavior): the first enemy in LIST order is attacked, deterministically —
        // this is why the first attempt at this test (a wall listed AFTER an animate defender, swept
        // across 50 seeds) never once saw the wall take damage: the defender is always found first and
        // always dies first, and combatant-kind's own AnyActive fix then ends the battle the instant no
        // animate wave member remains, before the wall is ever reached. That is correct, expected
        // behavior for this module, not a bug — reordering so the wall is listed FIRST proves the
        // actual claim ("targetable and damageable") directly and deterministically instead.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "ck-wave",
            // AE1.4: damage is the ACTION's base x P(Theta), not `atk`; the strong attacker is
            // expressed as a high Theta so the wall is still killed on the first swing.
            Squad = new[] { Animate("squad:0", "squad", maxHp: 5000, atk: 5000) with { ThetaActor = 100 } },
            Wave = new[]
            {
                Structure("wave:wall", "wave", maxHp: 1),
                Animate("wave:defender", "wave", maxHp: 5000, defense: 0),
            },
        }, seed: 1);

        var wall = report.Actors.Single(a => a.Key == "wave:wall");
        Assert.False(wall.Survived, "a 1-hp wall listed first must be the deterministic first target and die");
        Assert.Equal(BattleOutcome.Victory, report.Outcome);
    }

    [Fact]
    public void Structure_with_no_actions_gets_no_basic_attack()
    {
        var setup = new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[] { Structure("wave:wall", "wave") },
        };
        var held = BattleEngine.HeldActionIdsForTest(setup, seed: 1, actorKey: "wave:wall");
        Assert.Empty(held);
    }

    [Fact]
    public void Garrisoned_structure_lends_actions_to_its_occupant()
    {
        var catalog = ActionCatalog.Build(new[] { Dummy("skill.occupant"), Dummy("skill.structure") });
        var setup = new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[]
            {
                Animate("wave:occupant", "wave", equipped: new[] { "skill.occupant" }),
                Structure("wave:tower", "wave", garrisonedBy: "wave:occupant", equipped: new[] { "skill.structure" }),
            },
        };
        var held = BattleEngine.HeldActionIdsForTest(setup, seed: 1, actorKey: "wave:occupant", actionCatalog: catalog);
        Assert.Contains("skill.occupant", held);
        Assert.Contains("skill.structure", held);
        Assert.Equal(2, held.Count);
    }

    [Fact]
    public void Garrisoning_a_wall_grants_nothing()
    {
        var catalog = ActionCatalog.Build(new[] { Dummy("skill.occupant") });
        var setup = new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[]
            {
                Animate("wave:occupant", "wave", equipped: new[] { "skill.occupant" }),
                Structure("wave:wall", "wave", garrisonedBy: "wave:occupant"),  // no EquippedActionIds
            },
        };
        var held = BattleEngine.HeldActionIdsForTest(setup, seed: 1, actorKey: "wave:occupant", actionCatalog: catalog);
        Assert.Equal(new[] { "skill.occupant" }, held);
    }

    [Fact]
    public void Garrisoned_structure_itself_still_takes_no_turn()
    {
        var setup = new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad", maxHp: 200_000, atk: 1) },
            Wave = new[]
            {
                Animate("wave:occupant", "wave", maxHp: 200_000, atk: 1),
                Structure("wave:tower", "wave", maxHp: 200_000, garrisonedBy: "wave:occupant"),
            },
        };
        var report = BattleEngine.Resolve(setup, seed: 5);
        var tower = report.Actors.Single(a => a.Key == "wave:tower");
        Assert.Equal(0, tower.DamageDealt);
    }

    [Fact]
    public void Structure_hp_is_long_end_to_end()
    {
        // MaxHp was already `long` on BattleActorSetup before this module; this asserts it round-trips
        // a magnitude well past int.MaxValue through a real resolve without narrowing.
        const long bigHp = (long)int.MaxValue + 1_000_000;
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "ck-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[] { Structure("wave:wall", "wave", maxHp: bigHp) },
        }, seed: 1);

        Assert.Equal(bigHp, report.Actors.Single(a => a.Key == "wave:wall").HpRemaining);
    }
}
