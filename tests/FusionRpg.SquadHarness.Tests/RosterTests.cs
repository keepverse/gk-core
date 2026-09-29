using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// F1's own acceptance bullet: "the duel roster is proven to be gk-core/tools/HybridViability's same 91 builds
/// by CONSTRUCTING them, never by asserting the number 91." This class reconstructs
/// gk-core/tools/HybridViability/Program.cs's four loops (lines 106-115) independently, from
/// <see cref="BuildFactory"/> directly rather than by calling <see cref="SquadRoster"/>, and asserts the
/// two independent constructions agree build-for-build. If <see cref="SquadRoster.Duels"/> ever drifted
/// from HybridViability's own shape, this test -- not a literal "91" -- is what would catch it.
/// </summary>
public class RosterTests
{
    static readonly IReadOnlyList<string> Roster = BuildFactory.Roster;

    /// <summary>Reproduces gk-core/tools/HybridViability/Program.cs:106-115 verbatim, independently of
    /// SquadRoster.Duels, as the reference this test compares against.</summary>
    static List<(string Label, string Kind, AptitudeAllocation Allocation)> HybridViabilityShapedDuels()
    {
        var builds = new List<(string, string, AptitudeAllocation)>();
        foreach (var a in Roster) builds.Add((a, "corner", BuildFactory.Build(a)));
        for (var i = 0; i < Roster.Count; i++)
        for (var j = i + 1; j < Roster.Count; j++)
            builds.Add(($"{Roster[i]}+{Roster[j]}", "hybrid2", BuildFactory.Build(Roster[i], Roster[j])));
        for (var i = 0; i < Roster.Count; i++)
            builds.Add(($"{Roster[i]}+{Roster[(i + 1) % Roster.Count]}+{Roster[(i + 2) % Roster.Count]}", "hybrid3",
                BuildFactory.Build(Roster[i], Roster[(i + 1) % Roster.Count], Roster[(i + 2) % Roster.Count])));
        builds.Add(("even12", "spread", BuildFactory.EvenSpread()));
        return builds;
    }

    [Fact]
    public void The_duel_roster_is_the_ninety_one_HybridViability_builds()
    {
        var reference = HybridViabilityShapedDuels();
        var actual = SquadRoster.Duels();

        Assert.Equal(91, actual.Count);
        Assert.Equal(12, actual.Count(b => b.Kind == "corner"));
        Assert.Equal(66, actual.Count(b => b.Kind == "hybrid2"));
        Assert.Equal(12, actual.Count(b => b.Kind == "hybrid3"));
        Assert.Equal(1, actual.Count(b => b.Kind == "spread"));

        Assert.Equal(reference.Count, actual.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].Label, actual[i].Id);
            Assert.Equal(reference[i].Kind, actual[i].Kind);
            foreach (var apt in Roster)
                Assert.Equal(reference[i].Allocation.PointsAt(AllocationScope.Commander, apt),
                             actual[i].Allocation.PointsAt(AllocationScope.Commander, apt));
        }
    }

    [Fact]
    public void Every_squad_has_exactly_six_actors()
    {
        // Ties the roster to WebMatchService.BuildSquad's own maxSquad bound (const int maxSquad = 6),
        // per this module's own named acceptance test.
        const int maxSquad = 6;
        var squads = SquadRoster.Squads();
        Assert.Equal(23, squads.Count);
        foreach (var squad in squads)
            Assert.Equal(maxSquad, squad.Actors.Count);
    }

    [Fact]
    public void Every_squad_has_exactly_six_actors_under_the_shipped_shape_too()
    {
        var squads = SquadRoster.Squads(AllocationShape.Shipped);
        Assert.Equal(23, squads.Count);
        foreach (var squad in squads)
            Assert.Equal(6, squad.Actors.Count);
    }

    [Fact]
    public void The_squad_roster_names_every_id_shape_the_spec_table_lists()
    {
        var kinds = SquadRoster.Squads().GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(13, kinds["mono"]); // 12 mono-<apt> + mono-spread, both tagged "mono"
        Assert.True(kinds.ContainsKey("posture"));
        Assert.Equal(3, kinds["posture"]);
        Assert.Equal(2, kinds["rainbow"]); // rainbow-balanced + rainbow-force-finesse
        Assert.Equal(3, kinds["mono-hybrid2"]);
        Assert.Equal(1, kinds["mono-hybrid3"]);
        Assert.Equal(1, kinds["mixed"]);
    }

    /// <summary>F1b: the shipped shape collapses every squad onto ITS OWN first per-actor entry,
    /// replicated -- never a second, independently-derived allocation. So for every squad id, the
    /// shipped actor 0 allocation must equal the per-actor actor 0 allocation.</summary>
    [Fact]
    public void The_shipped_shape_replicates_the_per_actor_shapes_own_first_actor()
    {
        var perActor = SquadRoster.Squads(AllocationShape.PerActor).ToDictionary(s => s.Id);
        var shipped = SquadRoster.Squads(AllocationShape.Shipped).ToDictionary(s => s.Id);

        Assert.Equal(perActor.Keys.OrderBy(x => x, StringComparer.Ordinal),
                     shipped.Keys.OrderBy(x => x, StringComparer.Ordinal));

        foreach (var id in perActor.Keys)
        {
            var expectedFirst = perActor[id].Actors[0];
            var shippedActors = shipped[id].Actors;
            Assert.Equal(6, shippedActors.Count);
            foreach (var actor in shippedActors)
            foreach (var apt in Roster)
                Assert.Equal(expectedFirst.PointsAt(AllocationScope.Commander, apt),
                             actor.PointsAt(AllocationScope.Commander, apt));
        }
    }

    [Fact]
    public void Squad_and_duel_ids_are_all_distinct_and_never_empty()
    {
        var duelIds = SquadRoster.Duels().Select(b => b.Id).ToList();
        var squadIds = SquadRoster.Squads().Select(s => s.Id).ToList();
        Assert.All(duelIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.All(squadIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(duelIds.Count, duelIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(squadIds.Count, squadIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Elements_are_neutral_in_every_generated_setup()
    {
        // spec §3: ElementPrimary/Secondary stay null on every actor in both rosters, so the bridge
        // column is comparable at all.
        var duel = SquadRoster.Duels()[0];
        var actor = SquadMatch.ToActorSetup("squad:0", "squad", duel.Allocation, theta: 100);
        Assert.Null(actor.ElementPrimary);
        Assert.Null(actor.ElementSecondary);

        var squad = SquadRoster.Squads()[0];
        foreach (var allocation in squad.Actors)
        {
            var squadActor = SquadMatch.ToActorSetup("squad:0", "squad", allocation, theta: 100);
            Assert.Null(squadActor.ElementPrimary);
            Assert.Null(squadActor.ElementSecondary);
        }
    }
}
