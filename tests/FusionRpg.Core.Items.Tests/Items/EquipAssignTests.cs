using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

public class EquipAssignTests
{
    static readonly EquipGate Gate = new();

    [Fact]
    public void Assigning_into_a_slot_the_unlock_predicate_closes_is_refused_with_a_reason()
    {
        var closedGate = new EquipGate(new SlotUnlock(new AlwaysClosed()));
        var actor = new SpecimenActor("s1", Frame: null, Level: 50, Faction: null);

        var refusal = closedGate.Explain(ItemRole.ArmamentPrimary, actor, itemFrame: null, levelReq: null, factionReq: null);

        Assert.NotNull(refusal);
        Assert.Equal(EquipRefusalReason.RoleLocked, refusal!.Value.Reason);
    }

    sealed class AlwaysClosed : ISlotUnlockRule
    {
        public bool Evaluate(ItemRole role, ActorContext actor) => false;
    }

    [Fact]
    public void A_lapsed_level_req_reports_a_shortfall_and_keeps_the_binding()
    {
        // I11 §2.6: filtering a lapsed requirement out of the projection would be force-unequip
        // wearing a projection's clothes. Projectable must stay true; Admits must refuse.
        var actor = new SpecimenActor("s1", Frame: null, Level: 3, Faction: null);

        Assert.False(Gate.Admits(ItemRole.CoreGuard, actor, itemFrame: null, levelReq: 10, factionReq: null));
        Assert.True(Gate.Projectable(ItemRole.CoreGuard, actor, itemFrame: null, factionReq: null));
    }

    [Fact]
    public void Level_req_is_actually_enforced_end_to_end()
    {
        // A4: BindGate.cs:53's arm exists but no production caller supplies OwnerLevel. Assert the
        // refusal through this module's own gate, not the branch in isolation.
        var actor = new SpecimenActor(SpecimenId: "s42", Frame: null, Level: 5, Faction: null);

        Assert.False(Gate.Admits(ItemRole.ArmamentPrimary, actor, itemFrame: null, levelReq: 20, factionReq: null));
        Assert.True(Gate.Admits(ItemRole.ArmamentPrimary, actor, itemFrame: null, levelReq: 5, factionReq: null));
    }

    [Fact]
    public void Level_req_compares_against_the_specimen_not_the_account()
    {
        // Pinned by construction: SpecimenActor carries only the specimen's own Level -- there is no
        // account-level concept anywhere in this type for a rule to accidentally read instead.
        Assert.DoesNotContain(typeof(SpecimenActor).GetProperties(), p => p.Name.Contains("Account"));
    }

    [Fact]
    public void The_gate_refuses_a_wrong_frame_with_a_reason()
    {
        var actor = new SpecimenActor("s1", Frame: "plant", Level: 50, Faction: null);

        var refusal = Gate.Explain(ItemRole.ArmamentPrimary, actor, itemFrame: "humanoid", levelReq: null, factionReq: null);

        Assert.NotNull(refusal);
        Assert.Equal(EquipRefusalReason.RoleNotOnFrame, refusal!.Value.Reason);
    }

    [Fact]
    public void The_frame_arm_is_inert_while_no_species_carries_a_frame()
    {
        // X1 has not run: every actor built today has Frame = null, because nothing supplies one.
        // A frame check against a null actor.Frame must never fire, regardless of the item's own
        // frame -- this is the wiring gap, asserted rather than assumed. It fails the day X1 lands
        // and something starts passing a real Frame, which is the reminder to populate it.
        var actorWithNoFrameYet = new SpecimenActor("s1", Frame: null, Level: 50, Faction: null);

        Assert.True(Gate.Admits(ItemRole.ArmamentPrimary, actorWithNoFrameYet,
            itemFrame: "humanoid", levelReq: null, factionReq: null));
        Assert.True(Gate.Admits(ItemRole.ArmamentPrimary, actorWithNoFrameYet,
            itemFrame: "plant", levelReq: null, factionReq: null));
    }

    [Fact]
    public void No_element_clause_can_reach_the_gate()
    {
        // I11 §2.4's refusal, asserted as a negative: the closed refusal-reason list has no element
        // arm, and never will without a reviewed spec change.
        var reasonNames = Enum.GetNames<EquipRefusalReason>();
        Assert.DoesNotContain(reasonNames, n => n.Contains("Element", StringComparison.OrdinalIgnoreCase));
    }

    // ---- UnassistedAttributes (I11 §2.7's cycle rule) -------------------------------------------

    [Fact]
    public void The_gate_input_excludes_the_four_equippable_container_kinds()
    {
        var sources = new[]
        {
            ("trait", 10), ("item", 999), ("gem", 999), ("set", 999), ("charm", 999), ("patron", 5),
        };

        var unassisted = UnassistedAttributes.Filter(sources).ToList();

        Assert.Equal(new[] { ("trait", 10), ("patron", 5) }, unassisted);
    }

    [Fact]
    public void An_equippable_grant_cannot_flip_an_admission()
    {
        // Structural proof, not a claim: composing the actor's level from a source list that
        // includes an "item" grant would let two items cross-grant what the other requires. Filtering
        // through UnassistedAttributes before the gate ever sees the number makes that impossible by
        // construction -- the item-sourced boost never reaches actor.Level at all.
        var sources = new[] { ("trait", 5), ("item", 999) }; // an item claiming to grant +999 level
        var unassistedLevel = UnassistedAttributes.Filter(sources).Sum(s => s.Value);

        var actor = new SpecimenActor("s1", Frame: null, Level: unassistedLevel, Faction: null);

        Assert.False(Gate.Admits(ItemRole.ArmamentPrimary, actor, itemFrame: null, levelReq: 10, factionReq: null));
    }
}
