using FusionRpg.Core.Delve.Objects;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Objects;

/// <summary>D3.28 (spec-supplies-and-objects.md §5) — `VerbResolver.Resolve`, one red/green pair per
/// verb (the todo's own Verify line).</summary>
public class VerbResolverTests
{
    static VerbResolverTests()
    {
        // The registry's own six (spec-dungeon-registries.md:78) -- configured once, idempotent
        // across the whole test run the same way DungeonTuningHub/PowerTuningHub bootstraps are.
        InteractionVerbCatalog.Configure(new[]
        {
            new InteractionVerbDef { VerbId = "open" },
            new InteractionVerbDef { VerbId = "disarm" },
            new InteractionVerbDef { VerbId = "pray" },
            new InteractionVerbDef { VerbId = "loot" },
            new InteractionVerbDef { VerbId = "destroy", Decision = 12 },
            new InteractionVerbDef { VerbId = "garrison", Decision = 15 },
        });
    }

    static RoomObject Obstacle(IReadOnlyList<string> verbs, bool oneShot = false, PredicateNode.Leaf? requirement = null) =>
        new("r0c0", ObjectKind.Obstacle, "lane-a", verbs, requirement, oneShot, "sector:r0c0");

    static RoomObject Curio(IReadOnlyList<string> verbs, bool oneShot = true, PredicateNode.Leaf? requirement = null) =>
        new("r1c0", ObjectKind.Curio, "event.trap-a", verbs, requirement, oneShot, "sector:r1c0");

    static RoomObject Structure(IReadOnlyList<string> verbs) =>
        new("r2c0", ObjectKind.Structure, "structure-a", verbs, null, false, "sector:r2c0");

    // ---- pre-checks common to every verb ----

    [Fact]
    public void An_unknown_verb_throws_it_is_a_content_bug_not_a_refusal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            VerbResolver.Resolve(Obstacle(new[] { "open" }), "ignite", true, false, ObjectsBreakMode.Either, "some"));
    }

    [Fact]
    public void A_verb_the_object_does_not_offer_refuses()
    {
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open" }), "destroy", true, false, ObjectsBreakMode.Either, "some");
        Assert.False(outcome.Ok);
        Assert.Equal("object.verb-not-offered", outcome.Reason);
    }

    [Fact]
    public void A_spent_one_shot_refuses()
    {
        var outcome = VerbResolver.Resolve(Curio(new[] { "loot" }, oneShot: true), "loot", true, alreadySpent: true, ObjectsBreakMode.Either, "some");
        Assert.False(outcome.Ok);
        Assert.Equal("object.spent", outcome.Reason);
    }

    [Fact]
    public void An_unmet_requirement_refuses()
    {
        var req = new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "key.lane-a");
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open" }, requirement: req), "open", requirementHolds: false, false, ObjectsBreakMode.Either, "some");
        Assert.False(outcome.Ok);
        Assert.Equal("object.requirement-unmet", outcome.Reason);
    }

    [Fact]
    public void A_null_requirement_always_holds_never_checked_against_requirementHolds()
    {
        // obj.Requirement is null -> the gate is skipped entirely, even if the caller (wrongly)
        // passes requirementHolds: false.
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open" }), "open", requirementHolds: false, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
    }

    // ---- obstacle x open ----

    [Fact]
    public void Obstacle_open_hands_off_to_LaneGate_naming_the_key_to_consume()
    {
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open" }), "open", true, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("open-gate", outcome.Kind);
        Assert.Equal("lane-a", outcome.LaneId);
        Assert.Equal("key.lane-a", outcome.ConsumeStock);
    }

    // ---- obstacle x destroy(stamina) ----

    [Theory]
    [InlineData(ObjectsBreakMode.Stamina)]
    [InlineData(ObjectsBreakMode.Either)]
    public void Obstacle_destroy_under_stamina_or_either_pays_the_break_action(string breakMode)
    {
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open", "destroy" }), "destroy", true, false, breakMode, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("pay-then-open", outcome.Kind);
        Assert.Equal("delve.break", outcome.ActionId);
        Assert.Equal("lane-a", outcome.LaneId);
    }

    // ---- obstacle x destroy(structure) ----

    [Fact]
    public void Obstacle_destroy_under_structure_breakMode_fights_instead_of_paying()
    {
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "open", "destroy" }), "destroy", true, false, ObjectsBreakMode.Structure, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("structure-fight", outcome.Kind);
        Assert.Equal("r0c0", outcome.SectorId);
        Assert.Equal("some", outcome.StructureHpBand);
    }

    [Fact]
    public void A_structure_kind_destroy_reads_the_same_breakMode_choice_as_an_obstacle()
    {
        // §5's own table lists destroy for BOTH obstacle and structure, with the identical
        // breakMode-driven choice -- a Structure object is not a special "always fights" case.
        var fights = VerbResolver.Resolve(Structure(new[] { "destroy" }), "destroy", true, false, ObjectsBreakMode.Structure, "some");
        Assert.Equal("structure-fight", fights.Kind);
        var pays = VerbResolver.Resolve(Structure(new[] { "destroy" }), "destroy", true, false, ObjectsBreakMode.Stamina, "some");
        Assert.Equal("pay-then-open", pays.Kind);
    }

    // ---- curio x loot / disarm / open ----

    [Fact]
    public void Curio_loot_draws_the_deck()
    {
        var outcome = VerbResolver.Resolve(Curio(new[] { "loot" }), "loot", true, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("deck-draw", outcome.Kind);
        Assert.Equal("event.trap-a", outcome.DeckSourceRef);
    }

    [Fact]
    public void Curio_disarm_also_draws_the_deck()
    {
        var outcome = VerbResolver.Resolve(Curio(new[] { "disarm" }), "disarm", true, false, ObjectsBreakMode.Either, "some");
        Assert.Equal("deck-draw", outcome.Kind);
    }

    [Fact]
    public void Curio_open_a_chest_also_draws_the_deck_section5s_own_table_not_the_style_samples_switch()
    {
        // §5's own table row for "open": "curio (chest)" resolves via "event-deck outcome draw with
        // the key override tag" -- the code-style sample's own switch omits this arm entirely (it only
        // shows loot/disarm/pray for Curio), but the detailed table wins per this file's own doc comment.
        var outcome = VerbResolver.Resolve(Curio(new[] { "open" }), "open", true, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("deck-draw", outcome.Kind);
    }

    [Fact]
    public void Obstacle_disarm_a_trapped_door_also_draws_the_deck()
    {
        var outcome = VerbResolver.Resolve(Obstacle(new[] { "disarm" }), "disarm", true, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("deck-draw", outcome.Kind);
    }

    [Fact]
    public void Structure_garrison_refuses_explicitly_in_v1()
    {
        var outcome = VerbResolver.Resolve(Structure(new[] { "garrison" }), "garrison", true, false, ObjectsBreakMode.Either, "some");
        Assert.False(outcome.Ok);
        Assert.Equal("object.garrison-unbuilt", outcome.Reason);
    }

    // ---- building x pray(shrine) ----

    [Fact]
    public void Building_pray_shrine_draws_the_deck()
    {
        var shrine = new RoomObject("r3c0", ObjectKind.Building, "shrine", new[] { "pray" }, null, false, "sector:r3c0");
        var outcome = VerbResolver.Resolve(shrine, "pray", true, false, ObjectsBreakMode.Either, "some");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("deck-draw", outcome.Kind);
        Assert.Equal("shrine", outcome.DeckSourceRef);
    }
}
