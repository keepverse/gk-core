using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W32 (spec-ai-commander.md §ReachMap and the believed frontier): the edge of what you hold and the
/// edge of what you know are two different edges.
///
/// Without fog a frontier is just "not mine, next to mine". With it there is a second kind — a
/// neighbour you have never laid eyes on — and the two want opposite decisions: one is about value,
/// the other about ignorance. Merging them would leave every caller re-splitting the list.
/// </summary>
public class FrontierSetTests
{
    static WorldState Line() => GraphShapes.From(600, "a-b", "b-c", "c-d", "d-e") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z" }
        }
    };

    /// <summary>Dave holds <paramref name="mine"/>, with a legion at the first of them so he can see.</summary>
    static IWorldView Holding(params string[] mine)
    {
        var world = Line();
        var owned = world with
        {
            Sectors = world.Sectors
                .Select(s => mine.Contains(s.SectorId) ? s with { OwnerFactionId = "dave" } : s)
                .ToList(),
            Entities = new[]
            {
                new WorldEntity
                {
                    EntityId = "e-dave-1",
                    Kind = WorldEntityKind.Legion,
                    OwnerFactionId = "dave",
                    AtSectorId = mine[0],
                    Stance = "march",
                    Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 100 } }
                }
            }
        };

        return new BelievedWorldView(owned with { Intel = IntelRecorder.Observe(owned, owned, turn: 0) }, "dave");
    }

    [Fact]
    public void The_ground_you_hold_that_touches_something_else_is_your_frontier()
    {
        var frontier = FrontierSet.Of(Holding("a", "b"));

        // `a` is interior — everything it touches is yours. `b` looks outward at `c`.
        Assert.Equal(new[] { "b" }, frontier.Held);
    }

    [Fact]
    public void Everything_touching_your_territory_has_been_seen_at_least_once()
    {
        // The finding this class was rewritten around. `Visibility` makes every sector you own an
        // observation post with a one-lane radius, so a neighbour you have *never* laid eyes on
        // cannot exist — which is why there is no third set for them.
        foreach (var mine in new[] { new[] { "a" }, new[] { "a", "b" }, new[] { "a", "b", "c" } })
        {
            var view = Holding(mine);
            foreach (var sectorId in FrontierSet.Of(view).Contested)
                Assert.NotNull(view.Believed(sectorId));
        }
    }

    [Fact]
    public void The_ground_beyond_your_edge_is_reported_as_contested()
    {
        Assert.Equal(new[] { "b" }, FrontierSet.Of(Holding("a")).Contested);
        Assert.Equal(new[] { "c" }, FrontierSet.Of(Holding("a", "b")).Contested);
    }

    [Fact]
    public void An_empire_with_nothing_beyond_it_has_no_frontier_at_all()
    {
        // Holding the whole line: nothing to expand into, and the caller must not be handed a
        // frontier it then has to notice is empty of anything worth taking.
        var frontier = FrontierSet.Of(Holding("a", "b", "c", "d", "e"));

        Assert.Empty(frontier.Held);
        Assert.Empty(frontier.Contested);
    }

    [Fact]
    public void Ground_you_do_not_hold_is_never_reported_as_your_own_frontier()
    {
        var frontier = FrontierSet.Of(Holding("a"));

        Assert.DoesNotContain("b", frontier.Held);
        Assert.Equal(new[] { "a" }, frontier.Held);
    }

    [Fact]
    public void A_faction_that_believes_it_holds_nothing_has_nowhere_to_expand_from()
    {
        var world = Line();
        var view = new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, "zomboss");
        var frontier = FrontierSet.Of(view);

        Assert.Empty(frontier.Held);
    }

    [Fact]
    public void The_two_sets_never_overlap()
    {
        // Held is yours; contested and unknown are not; and a sector cannot be both seen and unseen.
        var frontier = FrontierSet.Of(Holding("a", "b"));

        Assert.Empty(frontier.Held.Intersect(frontier.Contested, StringComparer.Ordinal));
    }

    [Fact]
    public void Everything_comes_back_in_stable_order()
    {
        var frontier = FrontierSet.Of(Holding("a", "b", "c"));

        Assert.Equal(frontier.Held.OrderBy(id => id, StringComparer.Ordinal), frontier.Held);
        Assert.Equal(frontier.Contested.OrderBy(id => id, StringComparer.Ordinal), frontier.Contested);
    }
}
