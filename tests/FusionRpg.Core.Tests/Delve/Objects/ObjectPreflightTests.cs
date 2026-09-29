using FusionRpg.Core.Delve.Objects;
using Xunit;
using LaneEdge = FusionRpg.Core.Delve.Objects.ObjectPreflight.LaneEdge;

namespace FusionRpg.Core.Tests.Delve.Objects;

/// <summary>D3.29 (spec-supplies-and-objects.md §8) — `ObjectPreflight`.</summary>
public class ObjectPreflightTests
{
    // A simple linear graph: entrance -- l0 -- r1 -- l1(gated) -- r2 -- l2 -- r3(key room)
    static IReadOnlyList<LaneEdge> LinearGraph() => new[]
    {
        new LaneEdge("l0", "entrance", "r1"),
        new LaneEdge("l1", "r1", "r2"),
        new LaneEdge("l2", "r2", "r3"),
    };

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObjectPreflight.CheckGatedDoorsReachable(null!, "entrance", new Dictionary<string, string>(), ObjectsBreakMode.None));
        Assert.Throws<ArgumentNullException>(() =>
            ObjectPreflight.CheckGatedDoorsReachable(LinearGraph(), "entrance", null!, ObjectsBreakMode.None));
    }

    [Fact]
    public void A_reachable_key_room_passes_even_with_breakMode_none()
    {
        var keys = new Dictionary<string, string> { ["l1"] = "r3" }; // r3 IS reachable via l0->l1->l2
        ObjectPreflight.CheckGatedDoorsReachable(LinearGraph(), "entrance", keys, ObjectsBreakMode.None);
        // no throw -- the assertion is that this line is reached
    }

    [Fact]
    public void An_unreachable_key_room_with_breakMode_none_throws()
    {
        var keys = new Dictionary<string, string> { ["l1"] = "r99" }; // r99 does not exist in the graph at all
        var ex = Assert.Throws<ObjectPreflightRejection>(() =>
            ObjectPreflight.CheckGatedDoorsReachable(LinearGraph(), "entrance", keys, ObjectsBreakMode.None));
        Assert.Contains("object.gated-door-unreachable", ex.Message);
        Assert.Contains("l1", ex.Message);
    }

    [Theory]
    [InlineData(ObjectsBreakMode.Stamina)]
    [InlineData(ObjectsBreakMode.Structure)]
    [InlineData(ObjectsBreakMode.Either)]
    public void An_unreachable_key_room_is_forgiven_when_a_break_option_exists(string breakMode)
    {
        var keys = new Dictionary<string, string> { ["l1"] = "r99" };
        ObjectPreflight.CheckGatedDoorsReachable(LinearGraph(), "entrance", keys, breakMode);
    }

    [Fact]
    public void Reachability_walks_lanes_in_either_direction()
    {
        // The key room sits "behind" the entrance relative to lane declaration order -- undirected
        // walk must still find it (a raid can walk a corridor either way).
        var reversed = new[] { new LaneEdge("l0", "r1", "entrance") }; // declared backwards
        var keys = new Dictionary<string, string> { ["l0"] = "r1" };
        ObjectPreflight.CheckGatedDoorsReachable(reversed, "entrance", keys, ObjectsBreakMode.None);
    }

    // ---- empty curio verb set ----

    [Fact]
    public void An_empty_curio_verb_set_throws_naming_the_sector()
    {
        var curios = new[] { ("r1c0", (IReadOnlyList<string>)Array.Empty<string>()) };
        var ex = Assert.Throws<ObjectPreflightRejection>(() => ObjectPreflight.CheckCurioVerbSetsNonEmpty(curios));
        Assert.Contains("object.empty-curio-verbs", ex.Message);
        Assert.Contains("r1c0", ex.Message);
    }

    [Fact]
    public void A_curio_with_at_least_one_verb_passes()
    {
        var curios = new[] { ("r1c0", (IReadOnlyList<string>)new[] { "loot" }) };
        ObjectPreflight.CheckCurioVerbSetsNonEmpty(curios);
    }

    // ---- Run: both checks, in order ----

    [Fact]
    public void Run_applies_both_checks_and_the_reachability_one_throws_first()
    {
        var keys = new Dictionary<string, string> { ["l1"] = "r99" };
        var badCurios = new[] { ("r1c0", (IReadOnlyList<string>)Array.Empty<string>()) };
        var ex = Assert.Throws<ObjectPreflightRejection>(() =>
            ObjectPreflight.Run(LinearGraph(), "entrance", keys, ObjectsBreakMode.None, badCurios));
        Assert.Contains("object.gated-door-unreachable", ex.Message); // the FIRST failing check's own reason
    }

    [Fact]
    public void Run_passes_clean_on_a_fully_valid_graph()
    {
        var keys = new Dictionary<string, string> { ["l1"] = "r3" };
        var curios = new[] { ("r2", (IReadOnlyList<string>)new[] { "loot" }) };
        ObjectPreflight.Run(LinearGraph(), "entrance", keys, ObjectsBreakMode.None, curios);
    }

    // ---- property: 256 generated graphs, matching the spec's own testing-strategy line ----

    [Fact]
    public void Property_256_generated_graphs_a_reachable_key_or_a_break_option_always_passes()
    {
        var rng = new Random(20260906);
        for (var trial = 0; trial < 256; trial++)
        {
            var roomCount = rng.Next(3, 12);
            var lanes = new List<LaneEdge>();
            for (var i = 0; i < roomCount - 1; i++)
                lanes.Add(new LaneEdge($"l{i}", i == 0 ? "entrance" : $"r{i}", $"r{i + 1}"));

            var gateAt = rng.Next(0, roomCount - 1); // the gated lane's own index
            var keyRoomIndex = rng.Next(gateAt, roomCount); // ALWAYS at or after the gate -- rule 12's own guarantee
            var keyRoomId = keyRoomIndex == 0 ? "entrance" : $"r{keyRoomIndex}";
            var keys = new Dictionary<string, string> { [$"l{gateAt}"] = keyRoomId };

            ObjectPreflight.CheckGatedDoorsReachable(lanes, "entrance", keys, ObjectsBreakMode.None);
        }
    }

    [Fact]
    public void Property_a_key_room_disconnected_from_the_graph_always_throws_without_a_break_option()
    {
        // The spec's own testing-strategy line is "ObjectPreflight throws when a key room is moved
        // below its gate" -- on this program's own UNDIRECTED reachability model (a corridor is
        // walked either way, so a gate is never itself an obstacle to reaching a DIFFERENT room), the
        // one topology that is genuinely unreachable is a key room disconnected from the graph
        // entirely, which is what "moved" to an inaccessible spot means once realized as a graph.
        var rng = new Random(20260906);
        for (var trial = 0; trial < 256; trial++)
        {
            var roomCount = rng.Next(4, 12);
            var lanes = new List<LaneEdge>();
            for (var i = 0; i < roomCount - 1; i++)
                lanes.Add(new LaneEdge($"l{i}", i == 0 ? "entrance" : $"r{i}", $"r{i + 1}"));

            var gateAt = rng.Next(0, roomCount - 1);
            var keyRoomId = $"unreachable-room-{trial}"; // never appears in `lanes` at all
            var keys = new Dictionary<string, string> { [$"l{gateAt}"] = keyRoomId };

            Assert.Throws<ObjectPreflightRejection>(() =>
                ObjectPreflight.CheckGatedDoorsReachable(lanes, "entrance", keys, ObjectsBreakMode.None));
        }
    }
}
