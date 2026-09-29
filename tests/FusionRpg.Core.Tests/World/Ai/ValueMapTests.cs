using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W33 (spec-ai-commander.md §ValueMap): worth, relative to *this* empire.
///
/// The axis that matters most is the one that can go negative. Every other 4X AI failure is a matter
/// of degree; blobbing outward until nothing is defensible is a failure of kind, and the only cure
/// is for bad ground to score worse than nothing rather than merely least-best.
/// </summary>
public class ValueMapTests
{
    static readonly IReadOnlyDictionary<string, long> NoThreat = new Dictionary<string, long>();

    static WorldState Line() => GraphShapes.From(600, "a-b", "b-c", "c-d") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z" }
        }
    };

    static WorldSlot Slot(int index, string typeId, ElementTypeId? element = null) =>
        new() { SlotIndex = index, SlotTypeId = typeId, Element = element };

    /// <summary>Dave holding <paramref name="mine"/>, legion at the first, sectors dressed by <paramref name="slots"/>.</summary>
    static IWorldView View(string[] mine, Dictionary<string, WorldSlot[]>? slots = null, WorldState? from = null)
    {
        var world = from ?? Line();
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = mine.Contains(s.SectorId) ? "dave" : s.OwnerFactionId,
                    Slots = slots is not null && slots.TryGetValue(s.SectorId, out var custom)
                        ? custom
                        : s.Slots
                })
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

        return new BelievedWorldView(dressed with { Intel = IntelRecorder.Observe(dressed, dressed, 0) }, "dave");
    }

    static IReadOnlyDictionary<string, SectorValue> Value(IWorldView view,
        IReadOnlyDictionary<string, long>? threat = null) =>
        ValueMap.For(view, threat ?? NoThreat);

    // ---- the axis that must go negative ----------------------------------------------------

    [Fact]
    public void Ground_that_would_hang_outside_your_supply_scores_worse_than_nothing()
    {
        // The blobbing cure. `d` is three lanes from Dave's only holding, touching nothing supplied,
        // so taking it would make an island the moment it was made.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat") }
        });

        var value = Value(view);

        Assert.True(value["d"].Total < 0, $"an unsupportable holding scored {value["d"].Total}");
        Assert.True(value["d"].Overextension > 0);
    }

    [Fact]
    public void Ground_already_inside_your_chain_is_never_overextension()
    {
        // The sector you are standing in is in supply by definition. Penalising it would make an
        // empire's own capital score negative, and every rule that compares values would invert.
        // A real capital always carries a rootbed too (Rule11), so this dresses one here — a bare
        // Seat alone is genuinely barren under L29's gate, which is a different (and correct)
        // concern this fixture is not testing.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat"), Slot(1, SlotTypeCatalog.RootbedSlotTypeId) }
        });

        Assert.Equal(0, Value(view)["a"].Overextension);
        Assert.Equal(0, Value(view)["a"].HabitabilityPenalty);
        Assert.True(Value(view)["a"].Total > 0);
    }

    [Fact]
    public void Ground_next_to_your_chain_is_not_overextension()
    {
        // `b` touches `a`, which is supplied. Expanding along the chain is exactly what the penalty
        // must *not* discourage, or the AI never grows at all.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        Assert.Equal(0, Value(view)["b"].Overextension);
    }

    [Fact]
    public void An_empire_with_no_supply_at_all_is_not_punished_for_having_none()
    {
        // The wild hold nothing and never had a capital. Penalising every sector on the map would
        // make the whole map score negative and the policy refuse to start.
        var world = Line();
        var view = new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, "zomboss");

        Assert.All(Value(view).Values, v => Assert.Equal(0, v.Overextension));
    }

    // ---- L29: the habitability gate (spec-loam-ai.md) -------------------------------------------

    [Fact]
    public void A_surveyed_barren_sector_scores_worse_than_it_would_habitable()
    {
        // Same ground, same everything else — the only thing that differs is what is actually
        // standing in the one slot, and that alone is enough to flip a clearly-viable pick into a
        // clearly-nonviable one.
        var barren = Value(View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "wildland") }
        }))["a"];

        var habitable = Value(View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }
        }))["a"];

        Assert.True(barren.HabitabilityPenalty > 0);
        Assert.Equal(0, habitable.HabitabilityPenalty);
        Assert.True(barren.Total < 0, $"a surveyed-barren pick scored {barren.Total}, expected worse than nothing");
        Assert.True(habitable.Total > 0);
        Assert.True(barren.Total < habitable.Total);
    }

    [Fact]
    public void An_unsurveyed_sector_is_never_gated_on_habitability()
    {
        // A glimpse (adjacent, no slot detail) and total ignorance (three lanes out) must not be
        // penalised for something nobody has actually looked at yet — that is curiosity's job, not
        // this gate's.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        Assert.Equal(SectorSight.Glimpse, view.Believed("b")!.Detail);
        Assert.Equal(0, Value(view)["b"].HabitabilityPenalty);

        Assert.Null(view.Believed("d"));
        Assert.Equal(0, Value(view)["d"].HabitabilityPenalty);
    }

    // ---- yield, and what a glimpse is allowed to claim -----------------------------------------

    [Fact]
    public void A_seat_outranks_bare_wildland()
    {
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat") },
            ["b"] = new[] { Slot(0, "wildland") }
        });

        var value = Value(view);
        Assert.True(value["a"].Yield > value["b"].Yield);
    }

    [Fact]
    public void A_sector_you_have_only_glimpsed_yields_nothing_because_you_have_seen_no_slots()
    {
        // Not a bug and not pessimism: a glimpse from next door carries no slots at all, so claiming
        // a rich-looking sector stays a gamble rather than a lookup.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]>
        {
            ["b"] = new[] { Slot(0, "essence-deposit", ElementTypeId.Fire) }
        });

        Assert.Equal(SectorSight.Glimpse, view.Believed("b")!.Detail);
        Assert.Equal(0, Value(view)["b"].Yield);
    }

    // ---- curiosity is what makes anyone explore -------------------------------------------------

    [Fact]
    public void Unknown_ground_is_worth_something_or_nobody_would_ever_go_and_look()
    {
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        Assert.Null(view.Believed("d"));                   // three lanes out, never seen
        Assert.True(Value(view)["d"].Curiosity > 0);
    }

    [Fact]
    public void Curiosity_is_optimistic_but_not_greedy()
    {
        // Below the mean of what you know, so proven ground of average quality beats a guess, and a
        // poor known sector does not. That is what makes exploring self-limiting rather than manic.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });
        var value = Value(view);

        Assert.True(value["d"].Curiosity < value["a"].Yield);
    }

    [Fact]
    public void When_nothing_is_unknown_curiosity_stops_mattering()
    {
        var view = View(new[] { "a", "b", "c", "d" }, new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat") }
        });

        Assert.All(Value(view).Values, v => Assert.Equal(0, v.Curiosity));
    }

    // ---- risk ------------------------------------------------------------------------------------

    [Fact]
    public void Somewhere_frightening_is_worth_less_than_somewhere_quiet()
    {
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        var threat = new Dictionary<string, long> { ["b"] = 5000, ["c"] = 0 };
        var value = Value(view, threat);

        Assert.True(value["b"].Risk < value["c"].Risk);
    }

    [Fact]
    public void With_nothing_to_fear_anywhere_risk_does_not_quietly_zero_the_map()
    {
        // An empty threat map must read as "all safe", not as "all maximally dangerous" — an
        // inverted axis with no data is the classic way to make an AI refuse to move.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        Assert.All(Value(view).Values, v => Assert.Equal(1000, v.Risk));
    }

    // ---- cost --------------------------------------------------------------------------------------

    [Fact]
    public void Somewhere_further_away_costs_more_to_take()
    {
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });
        var reach = new Dictionary<string, int> { ["b"] = 1, ["c"] = 3 };

        var value = ValueMap.For(view, NoThreat, reach);
        Assert.True(value["b"].Cost > value["c"].Cost);
    }

    [Fact]
    public void Somewhere_still_guarded_costs_more_than_somewhere_cleared()
    {
        var guarded = new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[]
            {
                Slot(0, "seat"),
                new WorldSlot { SlotIndex = 1, SlotTypeId = "lair", GuardWaveId = "w", GuardState = GuardState.Intact }
            }
        };

        var withGuard = View(new[] { "a" }, guarded);
        var without = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });

        Assert.True(Value(withGuard)["a"].Cost < Value(without)["a"].Cost);
    }

    // ---- shape ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_sector_gets_a_value_even_the_ones_you_know_nothing_about()
    {
        var view = View(new[] { "a" });
        Assert.Equal(view.SectorIds.Count, Value(view).Count);
    }

    [Fact]
    public void The_explanation_names_the_axes_that_decided_it()
    {
        // The reason a turn report carries. It has to be readable by a person deciding whether the
        // AI made a mistake or hit a bug.
        var view = View(new[] { "a" }, new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } });
        var line = Value(view)["a"].Explain();

        Assert.Contains("value", line, StringComparison.Ordinal);
        Assert.Contains("yield", line, StringComparison.Ordinal);
        Assert.Contains("risk", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Reversing_the_world_changes_no_score()
    {
        var world = Line();
        var reversed = world with
        {
            Sectors = world.Sectors.Reverse().ToList(),
            Lanes = world.Lanes.Reverse().ToList()
        };

        var slots = new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } };

        Assert.Equal(
            Value(View(new[] { "a" }, slots)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value.Total)),
            Value(View(new[] { "a" }, slots, reversed)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value.Total)));
    }
}
