using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-objective` (spec-siege-objective.md): the win condition, the field cap, and
/// the two orthogonal slot budgets. All three are pure, standalone mechanisms — `siege-resolver`
/// (a later, level-7 module) is what wires them into a live siege loop; these tests prove the
/// mechanism itself, the same way `siege-pathing`/`district-layout` were proven before their own
/// consuming modules landed.
/// </summary>
public class SiegeObjectiveTests
{
    static SiegeCombatant C(string key, string side, bool alive = true, bool withdrawn = false,
        bool inCore = false, CombatantKind kind = CombatantKind.Animate) =>
        new(key, side, alive, withdrawn, inCore, kind);

    [Fact]
    public void Core_cleared_of_animate_defenders_ends_the_siege()
    {
        var combatants = new[]
        {
            C("d1", "defender", alive: false, inCore: true),
            C("a1", "attacker", inCore: true),
        };
        Assert.Equal(SiegeOutcomeKind.CoreTaken, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Surviving_defenders_in_the_outer_ground_do_not_prevent_a_capture()
    {
        var combatants = new[]
        {
            C("d1", "defender", alive: true, inCore: false), // outside the Core -- still alive
            C("d2", "defender", alive: false, inCore: true), // the Core's own garrison, dead
            C("a1", "attacker", inCore: true),
        };
        Assert.Equal(SiegeOutcomeKind.CoreTaken, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Structures_in_the_core_do_not_prevent_a_capture()
    {
        var combatants = new[]
        {
            C("wall", "defender", alive: true, inCore: true, kind: CombatantKind.Structure),
            C("a1", "attacker", inCore: true),
        };
        Assert.Equal(SiegeOutcomeKind.CoreTaken, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Attacker_wiped_breaks_the_assault()
    {
        var combatants = new[]
        {
            C("d1", "defender", inCore: true),
            C("a1", "attacker", alive: false),
        };
        Assert.Equal(SiegeOutcomeKind.AssaultBroken, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Withdrawn_attacker_counts_the_same_as_dead_for_breaking_the_assault()
    {
        var combatants = new[]
        {
            C("d1", "defender", inCore: true),
            C("a1", "attacker", alive: true, withdrawn: true),
        };
        Assert.Equal(SiegeOutcomeKind.AssaultBroken, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Neither_at_the_horizon_is_inconclusive()
    {
        var combatants = new[]
        {
            C("d1", "defender", inCore: true),
            C("a1", "attacker"),
        };
        Assert.Equal(SiegeOutcomeKind.Inconclusive, SiegeObjective.Evaluate(combatants, "defender", "attacker"));
    }

    [Fact]
    public void Evaluate_is_pure_and_deterministic()
    {
        // Stands in for "evaluated at round boundaries only, never per action" -- this module owns no
        // loop, so the actual invocation timing is siege-resolver's responsibility; what this module
        // must guarantee is that calling it twice against the same state gives the same answer.
        var combatants = new[] { C("d1", "defender", inCore: true), C("a1", "attacker") };
        var a = SiegeObjective.Evaluate(combatants, "defender", "attacker");
        var b = SiegeObjective.Evaluate(combatants, "defender", "attacker");
        Assert.Equal(a, b);
    }

    // ---- Field cap ----

    [Fact]
    public void Field_cap_is_identical_for_both_sides()
    {
        var config = new FieldCapConfig { MaxLivingPerSide = 8 };
        Assert.Equal(FieldCap.TryAdmit("attacker", 8, config).Ok, FieldCap.TryAdmit("defender", 8, config).Ok);
        Assert.False(FieldCap.TryAdmit("attacker", 8, config).Ok);
        Assert.False(FieldCap.TryAdmit("defender", 8, config).Ok);
    }

    [Fact]
    public void Unlimited_sentinel_is_minus_one()
    {
        var config = new FieldCapConfig { MaxLivingPerSide = -1 };
        Assert.True(FieldCap.TryAdmit("attacker", 1_000_000, config).Ok);
    }

    [Fact]
    public void Cap_rejections_carry_a_stable_reason_code()
    {
        var config = new FieldCapConfig { MaxLivingPerSide = 1 };
        var result = FieldCap.TryAdmit("attacker", 1, config);
        Assert.False(result.Ok);
        Assert.Equal(SiegeRejectReasons.FieldCapSide, result.Reason);

        var invalid = FieldCap.TryAdmit("", 0, config);
        Assert.Equal(SiegeRejectReasons.FieldCapInvalidSide, invalid.Reason);
    }

    [Fact]
    public void Field_cap_is_not_derived_from_empty_cells()
    {
        // The API takes no board/cell-count parameter at all -- structurally cannot read one.
        var method = typeof(FieldCap).GetMethod(nameof(FieldCap.TryAdmit))!;
        Assert.DoesNotContain(method.GetParameters(), p =>
            p.ParameterType.Name.Contains("Grid", StringComparison.OrdinalIgnoreCase) ||
            p.ParameterType.Name.Contains("Board", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Slots ----

    [Fact]
    public void Odd_legion_slot_count_throws_at_load()
    {
        Assert.Throws<SiegeSlotsRejection>(() => SiegeSlots.LegionSlotsPerSide(3));
        Assert.Equal(2, SiegeSlots.LegionSlotsPerSide(2));
    }

    [Fact]
    public void Defense_slots_grow_with_development_until_the_capacity_point()
    {
        Assert.Equal(4, SiegeSlots.DefenseSlotsFor(0, atDevelopmentZero: 4, perDevelopmentLevel: 2, gridCapacityPoint: 2));
        Assert.Equal(6, SiegeSlots.DefenseSlotsFor(1, atDevelopmentZero: 4, perDevelopmentLevel: 2, gridCapacityPoint: 2));
        Assert.Equal(8, SiegeSlots.DefenseSlotsFor(2, atDevelopmentZero: 4, perDevelopmentLevel: 2, gridCapacityPoint: 2));
    }

    [Fact]
    public void Past_the_capacity_point_slot_count_is_flat_while_structure_tier_keeps_rising()
    {
        var atPoint = SiegeSlots.DefenseSlotsFor(2, 4, 2, gridCapacityPoint: 2);
        var wayPast = SiegeSlots.DefenseSlotsFor(500, 4, 2, gridCapacityPoint: 2);
        Assert.Equal(atPoint, wayPast); // the escape valve: slot count is flat past the point

        // ...while structure-state's own P(Theta) HP keeps rising -- proven directly, not re-derived.
        var def = new FusionRpg.Core.World.StructureDef { StructureId = "s", Name = "s", MaterialTier = 1 };
        var hpAtPoint = FusionRpg.Core.World.StructureDef.MaxHpOf(def, 2);
        var hpWayPast = FusionRpg.Core.World.StructureDef.MaxHpOf(def, 500);
        Assert.True(hpWayPast > hpAtPoint, "structure HP must keep rising past the slot-growth capacity point");
    }
}
