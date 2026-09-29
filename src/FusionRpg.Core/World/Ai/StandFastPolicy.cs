using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// Does nothing, on purpose (spec-ai-commander.md §Who gets which policy).
///
/// It still files an order, and that is the point. A faction that filed nothing would leave the
/// command log unable to tell **chose to do nothing** from **was never asked** — and once the AI
/// commits automatically, "was never asked" is the shape a broken policy registration takes. Two
/// rows a turn is a cheap price for being able to see the difference.
///
/// This is also the wild's permanent policy. They are a hazard on the map, not a third empire: an
/// expansionist wild would race the player for every sector and turn a map with danger on it into a
/// map with two opponents on it.
/// </summary>
public sealed class StandFastPolicy : IFactionPolicy
{
    public const string Id = "stand-fast";

    public static readonly StandFastPolicy Instance = new();

    public string PolicyId => Id;

    // The turn is in the id for legibility, not for correctness: the store's key is already
    // (world, turn, commander, commandId), so an id would be unique per turn without it. Said out
    // loud because a mutation that drops it breaks no test, and the next person to notice that
    // should find this note rather than conclude the tests have a hole.
    public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) => new[]
    {
        new PolicyOrder(
            new WorldCommand
            {
                CommanderId = view.FactionId,
                CommandId = $"ai-{view.CurrentTurn}-stand",
                Kind = WorldCommandKinds.StandFast
            },
            "stand fast")
    };
}
