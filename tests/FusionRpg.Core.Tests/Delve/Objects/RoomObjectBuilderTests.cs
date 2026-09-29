using FusionRpg.Core.Delve.Objects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Objects;

/// <summary>D3.27 (spec-supplies-and-objects.md §4) — `RoomObjectBuilder.For`, one projection test
/// per source.</summary>
public class RoomObjectBuilderTests
{
    static readonly PredicateNode SomeLeaf = new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "key.lane-a");

    [Fact]
    public void Null_and_invalid_arguments_throw()
    {
        Assert.Throws<ArgumentException>(() => RoomObjectBuilder.For("", "fight", null, null, Array.Empty<string>(), true));
        Assert.Throws<ArgumentException>(() => RoomObjectBuilder.For("r0c0", "", null, null, Array.Empty<string>(), true));
        Assert.Throws<ArgumentNullException>(() => RoomObjectBuilder.For("r0c0", "fight", null, null, null!, true));
    }

    [Fact]
    public void A_room_with_no_source_at_all_projects_nothing()
    {
        var objects = RoomObjectBuilder.For("r0c0", "fight", null, null, Array.Empty<string>(), true);
        Assert.Empty(objects);
    }

    // ---- source 1: the event-deck curio row ----

    [Fact]
    public void A_curio_source_projects_a_one_shot_curio_object()
    {
        var curio = new CurioSource("event.trap-a", new[] { "disarm", "loot" }, SomeLeaf);
        var objects = RoomObjectBuilder.For("r1c0", "curio", curio, null, Array.Empty<string>(), true);

        var obj = Assert.Single(objects);
        Assert.Equal(ObjectKind.Curio, obj.Kind);
        Assert.Equal("event.trap-a", obj.SourceRef);
        Assert.Equal(new[] { "disarm", "loot" }, obj.Verbs);
        Assert.True(obj.OneShot);
        Assert.Equal(SomeLeaf, obj.Requirement);
        Assert.Equal("sector:r1c0", obj.OwnerKey);
    }

    [Fact]
    public void A_curio_with_no_requirement_projects_a_null_requirement_not_an_empty_and()
    {
        // PredicateCompiler.cs's own comment: "absent means always and is legal, while And() would
        // quietly mean true" -- And([]) is a validation refusal, so "no gate" must be null, never Empty-And.
        var curio = new CurioSource("event.chest-a", new[] { "loot" }, Requirement: null);
        var obj = Assert.Single(RoomObjectBuilder.For("r2c0", "curio", curio, null, Array.Empty<string>(), true));
        Assert.Null(obj.Requirement);
    }

    // ---- source 2: the gated door ----

    [Fact]
    public void A_gated_door_with_a_live_key_projects_an_obstacle_with_open_and_destroy()
    {
        var door = new GatedDoorSource("lane-a", GateKeyId: "key.lane-a");
        var obj = Assert.Single(RoomObjectBuilder.For("r3c0", "fight", null, door, Array.Empty<string>(), allowDestroyOnObstacles: true));
        Assert.Equal(ObjectKind.Obstacle, obj.Kind);
        Assert.Equal("lane-a", obj.SourceRef);
        Assert.Equal(new[] { "open", "destroy" }, obj.Verbs);
        Assert.False(obj.OneShot);
    }

    [Fact]
    public void BreakMode_none_omits_destroy_from_the_obstacles_own_verb_list()
    {
        var door = new GatedDoorSource("lane-b", GateKeyId: "key.lane-b");
        var obj = Assert.Single(RoomObjectBuilder.For("r4c0", "fight", null, door, Array.Empty<string>(), allowDestroyOnObstacles: false));
        Assert.Equal(new[] { "open" }, obj.Verbs);
    }

    [Fact]
    public void A_door_whose_key_is_already_spent_projects_nothing()
    {
        var door = new GatedDoorSource("lane-c", GateKeyId: null); // WorldLane.GateKeyId = null once opened
        var objects = RoomObjectBuilder.For("r5c0", "fight", null, door, Array.Empty<string>(), true);
        Assert.Empty(objects);
    }

    // ---- source 3: room kind ("building") ----

    [Theory]
    [InlineData("shrine")]
    [InlineData("merchant")]
    [InlineData("rest")]
    [InlineData("wild")]
    [InlineData("boss")]
    public void Every_building_room_kind_projects_a_reusable_building_object(string roomKind)
    {
        var verbs = new[] { "pray" };
        var obj = Assert.Single(RoomObjectBuilder.For("r6c0", roomKind, null, null, verbs, true));
        Assert.Equal(ObjectKind.Building, obj.Kind);
        Assert.Equal(roomKind, obj.SourceRef);
        Assert.False(obj.OneShot); // "reusable within the visit"
    }

    [Fact]
    public void A_non_building_room_kind_projects_no_building_object_even_with_verbs_supplied()
    {
        var objects = RoomObjectBuilder.For("r7c0", "fight", null, null, new[] { "pray" }, true);
        Assert.Empty(objects);
    }

    [Fact]
    public void A_building_kind_with_zero_verbs_offered_projects_nothing()
    {
        var objects = RoomObjectBuilder.For("r8c0", "shrine", null, null, Array.Empty<string>(), true);
        Assert.Empty(objects);
    }

    // ---- multiple sources on one room (a gated boss room) ----

    [Fact]
    public void A_gated_boss_room_projects_both_the_obstacle_and_the_building()
    {
        var door = new GatedDoorSource("lane-d", GateKeyId: "key.lane-d");
        var objects = RoomObjectBuilder.For("r9c0", "boss", null, door, new[] { "pray" }, true);
        Assert.Equal(2, objects.Count);
        Assert.Contains(objects, o => o.Kind == ObjectKind.Obstacle);
        Assert.Contains(objects, o => o.Kind == ObjectKind.Building);
        Assert.All(objects, o => Assert.Equal("sector:r9c0", o.OwnerKey));
    }
}
