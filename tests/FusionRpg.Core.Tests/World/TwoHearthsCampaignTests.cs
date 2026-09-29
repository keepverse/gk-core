using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// A stand-in for the owner's ten-turn playtest, run considerably longer and with both sides acting:
/// Dave scripted into an aggressive expansion down the real corridor toward `hot-ground`, Zomboss
/// driven every turn by his actual `FrontierRulesPolicy` (the same decision the map's own AI would
/// make, not a hand-picked order) — the combination `TwoHearthsStoryTests` (Dave alone) and
/// `AbandonRuleTests`' hundred-turn survival test (Zomboss alone, against an idle Dave) each cover
/// separately, but neither runs together. This cannot answer the owner's three subjective questions
/// (spec-loam-maps.md) — nothing can — but it is the strongest mechanical falsifier available: if the
/// loam economy behaves badly under two active sides over a long run, this is where it would show.
///
/// Note for whoever reads this expecting Zomboss to react like this during the owner's own live
/// playtest: he currently will not. Nothing in `WorldEndpoints`'s `/commit` route invokes
/// `FrontierRulesPolicy` for a non-player faction — the AI commander driver that would is specced
/// (world-map-program) but unbuilt. This test drives his policy directly, the same way
/// `AbandonRuleTests` already does, because that is the only way to exercise it at all today.
/// </summary>
public class TwoHearthsCampaignTests
{
    const string Dave = "dave";
    const string Zomboss = "zomboss";
    const ulong Seed = 4242;
    readonly ITestOutputHelper _output;

    public TwoHearthsCampaignTests(ITestOutputHelper output) => _output = output;

    static WorldSector Find(WorldState w, string id) => w.Sectors.Single(s => s.SectorId == id);

    /// <summary>Dave's own scripted push: march the corridor, then clear and claim `hot-ground` once
    /// there — the same route and mechanics `TwoHearthsStoryTests` already proved reachable, replayed
    /// here turn-by-turn instead of run to completion up front, so it can interleave with Zomboss's AI.</summary>
    static WorldCommand? DaveOrder(WorldState world, int turn)
    {
        var legion = world.Entities.SingleOrDefault(e => e.EntityId == "e-dave-legion-1" && e.OwnerFactionId == Dave);
        if (legion is null) return null; // routed and destroyed — nothing left to command

        if (legion.AtSectorId != "hot-ground")
        {
            var path = new[] { "l-dh-df2", "l-df2-c1", "l-c1-c2", "l-c2-c3", "l-c3-c4", "l-c4-hot" };
            return new WorldCommand { CommanderId = Dave, CommandId = $"d-move-{turn}", Kind = WorldCommandKinds.Move, EntityId = "e-dave-legion-1", LanePath = path };
        }

        var hot = Find(world, "hot-ground");
        if (hot.OwnerFactionId == Dave) return null; // taken and held — nothing left to script

        var guarded = hot.Slots.Where(sl => sl.GuardState == GuardState.Intact).ToList();
        return guarded.Count > 0
            ? new WorldCommand { CommanderId = Dave, CommandId = $"d-clear-{turn}", Kind = WorldCommandKinds.Clear, EntityId = "e-dave-legion-1", SectorId = "hot-ground", SlotIndex = guarded[0].SlotIndex }
            : new WorldCommand { CommanderId = Dave, CommandId = $"d-claim-{turn}", Kind = WorldCommandKinds.Claim, EntityId = "e-dave-legion-1", SectorId = "hot-ground" };
    }

    [Fact]
    public void A_sixty_turn_campaign_dave_pushes_the_corridor_while_zombosss_ai_reacts()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7, worldId: "campaign");
        var log = new System.Text.StringBuilder();
        var maxComponentsSeen = new Dictionary<string, int> { [Dave] = 1, [Zomboss] = 1 };

        for (var turn = 0; turn < 60; turn++)
        {
            var commands = new List<WorldCommand>();
            var daveOrder = DaveOrder(world, turn);
            if (daveOrder is not null) commands.Add(daveOrder);

            var zombossView = new BelievedWorldView(world, Zomboss);
            commands.AddRange(FrontierRulesPolicy.Instance.Decide(zombossView, Seed).Select(o => o.Command));

            world = TurnEngine.Step(world, commands, Seed).World;

            foreach (var factionId in new[] { Dave, Zomboss })
            {
                var componentCount = TerritoryComponents.For(world, factionId).Count;
                if (componentCount > maxComponentsSeen[factionId]) maxComponentsSeen[factionId] = componentCount;

                foreach (var component in TerritoryComponents.For(world, factionId))
                    foreach (var id in component)
                        Assert.True(Find(world, id).LoamStock >= 0, $"turn {turn}, {factionId}, sector {id}: stock went negative");
            }

            if (turn % 10 == 9 || turn == 59)
                log.AppendLine(
                    $"turn {turn}: dave owns {world.Sectors.Count(s => s.OwnerFactionId == Dave)} sectors " +
                    $"(components={TerritoryComponents.For(world, Dave).Count}), zomboss owns " +
                    $"{world.Sectors.Count(s => s.OwnerFactionId == Zomboss)} (components={TerritoryComponents.For(world, Zomboss).Count}); " +
                    $"hot-ground owner={Find(world, "hot-ground").OwnerFactionId ?? "none"}, " +
                    $"dave-legion at={world.Entities.SingleOrDefault(e => e.EntityId == "e-dave-legion-1")?.AtSectorId ?? "destroyed"}");
        }

        _output.WriteLine(log.ToString());
        _output.WriteLine($"max components ever seen — dave: {maxComponentsSeen[Dave]}, zomboss: {maxComponentsSeen[Zomboss]}");

        // Both capitals are two-rootbed self-sufficient clusters (map design intent, already proven in
        // isolation by A_rich_core_carries_a_poor_frontier_indefinitely) — sixty turns of the other
        // side's own activity elsewhere on the map must not, by itself, cost either commander their
        // home, matching the ten-turn baseline probe's finding at a much longer horizon.
        Assert.Equal(Dave, Find(world, "d-home").OwnerFactionId);
        Assert.True(Find(world, "d-home").StabilityMilli > 0);
        Assert.Equal(Zomboss, Find(world, "z-home").OwnerFactionId);
        Assert.True(Find(world, "z-home").StabilityMilli > 0);
    }
}
