using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// D4.25 (party-dungeon-todo.md, spec-unique-pipeline.md §4: "the extend-action-slot grant"): the
/// <c>loadout.slots</c> derived channel and its three readers. The load-bearing property is the "one
/// at a time" rule — composition sums honestly (two +1 grants really do add to a channel value of 2),
/// but every reader collapses any positive channel value down to exactly one extra slot, so a second
/// worn extend-slot item never stacks.
/// </summary>
public class LoadoutSlotsChannelTests
{
    static RungTable Rungs => RungPolicy.Table;

    [Fact]
    public void The_channel_is_registered_as_an_uncapped_pool_count_channel()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.LoadoutSlots, out var def));
        Assert.Equal(DerivedComposeKind.FlatSum, def.Compose);
        Assert.Equal(StatClass.Pool, def.Class);
        Assert.Equal(UnitClass.Count, def.Unit);
        Assert.Null(def.Cap);
    }

    [Fact]
    public void Composition_sums_two_extend_slot_grants_honestly_to_two_not_one()
    {
        // The "one at a time" rule belongs to the READERS, not to composition -- if composition itself
        // saturated at 1, a future reader wanting the raw worn count (e.g. a UI badge) could never
        // recover it.
        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.LoadoutSlots, DerivedModifierOp.Flat, 1.0, SourceId: "item.ring-of-extension"),
            new DerivedModifier(DerivedStatChannels.LoadoutSlots, DerivedModifierOp.Flat, 1.0, SourceId: "item.satchel-of-holding"),
        });

        Assert.Equal(2, snap.Get(DerivedStatChannels.LoadoutSlots));
    }

    // ---- LoadoutSet.EffectiveMaxSize -- the one rule, named once ------------------------------------

    [Fact]
    public void EffectiveMaxSize_with_nothing_worn_is_the_structural_base_five()
    {
        Assert.Equal(5, LoadoutSet.EffectiveMaxSize());
        Assert.Equal(LoadoutSet.MaxSize, LoadoutSet.EffectiveMaxSize(0));
    }

    [Fact]
    public void EffectiveMaxSize_with_one_extend_slot_item_is_six()
    {
        Assert.Equal(6, LoadoutSet.EffectiveMaxSize(1));
    }

    /// <summary>The todo's own literal Verify line: two extend-slot items prove the total is six, not
    /// seven.</summary>
    [Fact]
    public void EffectiveMaxSize_with_two_extend_slot_items_is_six_not_seven()
    {
        Assert.Equal(6, LoadoutSet.EffectiveMaxSize(2));
    }

    // ---- the three readers individually, end to end -------------------------------------------------

    [Fact]
    public void Validate_admits_six_actions_when_the_channel_is_two_but_rejects_a_seventh()
    {
        var six = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var okResult = LoadoutSet.Validate(six, _ => true, _ => ActionKind.Skill, () => false, loadoutSlotsChannel: 2);
        Assert.True(okResult.Ok);

        var seven = new[] { "s1", "s2", "s3", "s4", "s5", "s6", "s7" };
        var fullResult = LoadoutSet.Validate(seven, _ => true, _ => ActionKind.Skill, () => false, loadoutSlotsChannel: 2);
        Assert.False(fullResult.Ok);
        Assert.Equal(LoadoutRejectionReason.LoadoutFull, fullResult.Reason);
    }

    [Fact]
    public void Validate_with_no_channel_argument_keeps_the_original_five_slot_behaviour()
    {
        var six = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var result = LoadoutSet.Validate(six, _ => true, _ => ActionKind.Skill, () => false);
        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.LoadoutFull, result.Reason);
    }

    [Fact]
    public void AutoEquip_Select_takes_six_candidates_when_the_channel_is_two_not_seven()
    {
        var candidates = new[]
        {
            new AutoEquipCandidate("skill.a", 1), new AutoEquipCandidate("skill.b", 1),
            new AutoEquipCandidate("skill.c", 1), new AutoEquipCandidate("skill.d", 1),
            new AutoEquipCandidate("skill.e", 1), new AutoEquipCandidate("skill.f", 1),
            new AutoEquipCandidate("skill.g", 1), new AutoEquipCandidate("skill.h", 1),
        };

        var selected = AutoEquip.Select(candidates, Rungs, loadoutSlotsChannel: 2);

        Assert.Equal(6, selected.Count);
    }

    [Fact]
    public void CapPolicy_EquippedSkillCap_reflects_the_channel_one_at_a_time()
    {
        Assert.Equal(5, CapPolicy.EquippedSkillCap());
        Assert.Equal(6, CapPolicy.EquippedSkillCap(1));
        Assert.Equal(6, CapPolicy.EquippedSkillCap(2)); // two items, still one extra slot
    }

    // ---- W14 (battle-derived-wire T18): reserved, because no production site feeds it ---------------

    [Fact]
    public void The_channel_is_listed_reserved_because_no_production_site_feeds_it()
    {
        // The reader chain is fully built and tested above; what was missing is a feeder. Until the
        // equip/atom layer that grants an extend-slot item ships, the coverage report must say so.
        var reserved = FusionRpg.Core.Balance.Guards.DominanceGuard.StandardCoverage().ReservedFamilies;
        Assert.Contains(DerivedStatChannels.LoadoutSlots, reserved);
    }

    [Fact]
    public void The_registry_row_records_the_unfed_status()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.LoadoutSlots, out var def));
        Assert.False(string.IsNullOrWhiteSpace(def.UnitClassNote));
        Assert.Contains("no production site", def.UnitClassNote!, StringComparison.OrdinalIgnoreCase);
    }
}
