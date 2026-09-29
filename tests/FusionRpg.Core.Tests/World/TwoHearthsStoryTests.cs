using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L19 acceptance (spec-loam-maps.md): the cut-off-and-lost story, played through the real engine
/// on `two-hearths` — not asserted against `LoamPhases` in isolation.
///
/// **A note on the story's shape.** The spec's phrasing is "take a rootbed sector, hold it, get cut
/// off, lose it." Played through: `hot-ground` (the map's rootbed prize) sits behind four unowned
/// corridor sectors, so claiming it never actually connects it to Dave's territory in
/// `TerritoryComponents`' sense — it becomes its own isolated component the instant it is claimed,
/// and with two rootbeds of its own it is self-sufficient (production comfortably exceeds its
/// upkeep). It cannot be "lost" to the fade by design; that is the settlement rule's *positive*
/// case, not a story to script. The story below claims ordinary ground instead — `corridor-1`, which
/// starts subsidised by Dave's capital cluster once connected, and fades once the lane that connects
/// it is severed. That is the actual anchoring lesson: a rootbed holding is safe on its own; ordinary
/// ground needs the chain home, and losing the chain is what "cut off" means.
/// </summary>
public class TwoHearthsStoryTests
{
    const ulong Seed = 999;

    /// <summary>Guard-kind contact resolves as a clean win — `actor-hub-and-combat-power-solid-fixing`
    /// T20 made `BattleKinds.Guard` one of `DistrictAssaultResolver`'s refused non-district kinds, so
    /// clearing a slot no longer happens on its own; this stands in so the story below can still reach
    /// the claim it is really testing.</summary>
    sealed class GuardAlwaysClears : IBattleResolver
    {
        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
        {
            var attacker = combatants.Single(e => e.EntityId == request.AttackerEntityId);
            return new BattleOutcome
            {
                BattleId = request.BattleId,
                WinnerEntityId = attacker.EntityId,
                GuardCleared = true,
                Sides = new[] { new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = attacker.Members } }
            };
        }
    }

    static WorldSector Find(WorldState w, string id) => w.Sectors.Single(s => s.SectorId == id);

    [Fact]
    public void A_legion_can_take_and_hold_the_hot_sector_the_maps_rootbed_prize()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7, worldId: "story-hot");
        var path = new[] { "l-dh-df2", "l-df2-c1", "l-c1-c2", "l-c2-c3", "l-c3-c4", "l-c4-hot" };

        // March: the legion needs the order re-filed every turn — an entity mid-lane with no fresh
        // order simply stops (there is no "continue automatically" behaviour to lean on).
        for (var i = 0; i < 8 && world.Entities.Single(e => e.EntityId == "e-dave-legion-1").AtSectorId != "hot-ground"; i++)
        {
            var move = new WorldCommand { CommanderId = "dave", CommandId = "move" + i, Kind = WorldCommandKinds.Move, EntityId = "e-dave-legion-1", LanePath = path };
            world = TurnEngine.Step(world, new[] { move }, Seed).World;
        }
        Assert.Equal("hot-ground", world.Entities.Single(e => e.EntityId == "e-dave-legion-1").AtSectorId);

        // Clear the one guarded slot, then claim.
        var guarded = Find(world, "hot-ground").Slots.Single(sl => sl.GuardState == GuardState.Intact);
        var clear = new WorldCommand { CommanderId = "dave", CommandId = "clear1", Kind = WorldCommandKinds.Clear, EntityId = "e-dave-legion-1", SectorId = "hot-ground", SlotIndex = guarded.SlotIndex };
        world = TurnEngine.Step(world, new[] { clear }, Seed, new GuardAlwaysClears()).World;
        Assert.All(Find(world, "hot-ground").Slots, sl => Assert.NotEqual(GuardState.Intact, sl.GuardState));

        var claim = new WorldCommand { CommanderId = "dave", CommandId = "claim1", Kind = WorldCommandKinds.Claim, EntityId = "e-dave-legion-1", SectorId = "hot-ground" };
        world = TurnEngine.Step(world, new[] { claim }, Seed).World;
        Assert.Equal("dave", Find(world, "hot-ground").OwnerFactionId);

        // Hold it for a run of turns: two rootbeds make it self-sufficient even isolated from every
        // other sector Dave owns (it sits behind unowned corridor ground, so it is its own
        // component from the moment it is claimed) — the settlement rule's positive case.
        for (var i = 0; i < 20; i++)
            world = TurnEngine.Step(world, Array.Empty<WorldCommand>(), Seed).World;

        Assert.Equal("dave", Find(world, "hot-ground").OwnerFactionId);
        Assert.True(Find(world, "hot-ground").StabilityMilli > 0);
    }

    [Fact]
    public void Ordinary_ground_taken_and_connected_is_subsidised_then_lost_once_cut_off()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7, worldId: "story-cutoff");

        // corridor-1 is unguarded — reachable and claimable in the same short march.
        for (var i = 0; i < 4 && Find(world, "corridor-1").OwnerFactionId != "dave"; i++)
        {
            var legion = world.Entities.Single(e => e.EntityId == "e-dave-legion-1");
            WorldCommand command = legion.AtSectorId == "corridor-1"
                ? new WorldCommand { CommanderId = "dave", CommandId = "claim" + i, Kind = WorldCommandKinds.Claim, EntityId = "e-dave-legion-1", SectorId = "corridor-1" }
                : new WorldCommand { CommanderId = "dave", CommandId = "move" + i, Kind = WorldCommandKinds.Move, EntityId = "e-dave-legion-1", LanePath = new[] { "l-dh-df2", "l-df2-c1" } };

            world = TurnEngine.Step(world, new[] { command }, Seed).World;
        }

        Assert.Equal("dave", Find(world, "corridor-1").OwnerFactionId);

        // Connected: pooled with the whole capital cluster, corridor-1 is subsidised and holds.
        // Unclaimed ground is authored at zero stability (nobody has been anchoring it), so
        // "subsidised" here means climbing steadily via the paid-in-full recovery rate, not
        // starting full — the fiction fits: barren ground seized fresh is fragile until it is held.
        var stabilityHistory = new List<int> { Find(world, "corridor-1").StabilityMilli };
        for (var i = 0; i < 15; i++)
        {
            world = TurnEngine.Step(world, Array.Empty<WorldCommand>(), Seed).World;
            stabilityHistory.Add(Find(world, "corridor-1").StabilityMilli);
        }

        Assert.Equal("dave", Find(world, "corridor-1").OwnerFactionId);
        Assert.NotEqual(SectorPhase.Lost, Find(world, "corridor-1").Phase);
        Assert.True(stabilityHistory[^1] > stabilityHistory[0],
            "connected ground must be recovering, not fading, while the capital cluster can pay for it");
        Assert.All(stabilityHistory.Zip(stabilityHistory.Skip(1)), pair => Assert.True(pair.Second >= pair.First,
            "stability must never drop while the component is paid in full"));

        // Cut off: sever the one lane joining corridor-1 to Dave's capital cluster. The capital
        // itself (d-home, d-flank-1, d-flank-2, d-outpost) is untouched — this is the far side
        // starving, not the whole empire.
        world = world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == "l-df2-c1" ? l with { State = LaneState.Severed } : l).ToList()
        };

        var turnsRun = 0;
        while (Find(world, "corridor-1").Phase != SectorPhase.Lost && turnsRun < 200)
        {
            world = TurnEngine.Step(world, Array.Empty<WorldCommand>(), Seed).World;
            turnsRun++;
        }

        Assert.Equal(SectorPhase.Lost, Find(world, "corridor-1").Phase);
        Assert.Null(Find(world, "corridor-1").OwnerFactionId);
        Assert.True(turnsRun is > 0 and < 200, $"expected a predictable countdown, took {turnsRun}");

        // The near half — the whole capital cluster — is untouched by the far side's loss.
        Assert.Equal(1000, Find(world, "d-home").StabilityMilli);
        Assert.Equal(1000, Find(world, "d-flank-1").StabilityMilli);
        Assert.Equal(1000, Find(world, "d-flank-2").StabilityMilli);
        Assert.Equal("dave", Find(world, "d-outpost").OwnerFactionId);
    }
}
