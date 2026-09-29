using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W35–W36 (spec-ai-commander.md §The decision layer): the rules, in order, first match wins.
///
/// Each rule gets a scenario that fires it and one that does not — a rule proven only by firing is a
/// rule that might fire always, which on an ordered list means everything below it is dead.
/// </summary>
public class FrontierRulesTests
{
    static WorldState Map() => GraphShapes.From(600, "a-b", "b-c", "c-d") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z", PolicyId = FrontierRulesPolicy.Id }
        }
    };

    static WorldSlot Slot(int index, string typeId, string? guard = null) => new()
    {
        SlotIndex = index,
        SlotTypeId = typeId,
        GuardWaveId = guard,
        GuardState = guard is null ? GuardState.Cleared : GuardState.Intact
    };

    static WorldEntity Band(string at, string owner = "zomboss", string stance = "march",
        int hp = 200, int wounds = 0, WorldEntityKind kind = WorldEntityKind.Warband) => new()
    {
        EntityId = $"e-{owner}-1",
        Kind = kind,
        OwnerFactionId = owner,
        AtSectorId = at,
        Stance = stance,
        MovementRemaining = MovementPolicy.BudgetFor(stance),
        // A bearer by default (L31, spec-loam-ai.md): every existing rule test in this file predates
        // the march-loam gate and has nothing to do with it, so the default band carries enough
        // capacity to survive any route this small map can produce. Tests that mean to exercise the
        // gate itself build their own, deliberately weaker or stronger, entity.
        Members = new[]
        {
            new WorldEntityMember
            {
                SpeciesId = "normalzombie", Level = 1, Hp = hp, Wounds = wounds,
                Role = WorldEntityMemberRole.Bearer
            }
        }
    };

    /// <summary>A world dressed to order, observed, and seen through Zomboss's eyes.</summary>
    static IWorldView Seen(
        Dictionary<string, string>? owners = null,
        Dictionary<string, WorldSlot[]>? slots = null,
        params WorldEntity[] entities)
    {
        var world = Map();
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = owners is not null && owners.TryGetValue(s.SectorId, out var o) ? o : null,
                    Slots = slots is not null && slots.TryGetValue(s.SectorId, out var custom) ? custom : s.Slots
                })
                .ToList(),
            Entities = entities.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
        };

        return new BelievedWorldView(dressed with { Intel = IntelRecorder.Observe(dressed, dressed, 0) }, "zomboss");
    }

    /// <summary>
    /// Six sectors on short lanes, for the Explore rule alone.
    ///
    /// On the four-sector map Explore is **untestable**: a scouting legion sees two lanes
    /// (`ScoutSightLanes`), so everything close enough to reach is already glimpsed and everything
    /// unknown is further than `ExploreTurns`. Scouting reveals the very thing it was going to
    /// explore. Three of this class's mutants survived on exactly that.
    /// </summary>
    static IWorldView Far(string stance, params string[] mine)
    {
        var world = GraphShapes.From(400, "a-b", "b-c", "c-d", "d-e", "e-f") with { Factions = Map().Factions };
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = mine.Contains(s.SectorId) ? "zomboss" : null,
                    Slots = s.SectorId == "a" ? new[] { Slot(0, "seat") } : s.Slots
                })
                .ToList(),
            Entities = new[] { Band("a", stance: stance) }
        };

        return new BelievedWorldView(dressed with { Intel = IntelRecorder.Observe(dressed, dressed, 0) }, "zomboss");
    }

    static IReadOnlyList<PolicyOrder> Decide(IWorldView view) =>
        FrontierRulesPolicy.Instance.Decide(view, seed: 1);

    static PolicyOrder Only(IWorldView view) => Assert.Single(Decide(view));

    // ---- the invariant that bounds everything ------------------------------------------------

    [Fact]
    public void No_entity_is_ever_given_two_orders_in_one_turn()
    {
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat"), Slot(1, "lair", "w") } },
            Band("a"));

        var subjects = Decide(view).Select(o => o.Command.EntityId).Where(id => id != null).ToList();
        Assert.Equal(subjects.Count, subjects.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_order_names_the_faction_whose_eyes_it_was_given()
    {
        var view = Seen(entities: Band("a"));
        Assert.All(Decide(view), o => Assert.Equal("zomboss", o.Command.CommanderId));
    }

    [Fact]
    public void A_faction_with_no_forces_still_says_something()
    {
        // The barrier needs a commit either way, and the log must be able to tell "nothing to do"
        // from "never asked".
        var order = Only(Seen());
        Assert.Equal(WorldCommandKinds.StandFast, order.Command.Kind);
    }

    [Fact]
    public void A_routed_force_is_left_alone()
    {
        // It spends the turn recovering whatever it is told, so ordering it about would only fill
        // the report with drops.
        var view = Seen(entities: Band("a") with { Routed = true });
        Assert.Equal(WorldCommandKinds.StandFast, Only(view).Command.Kind);
    }

    // ---- rule 2: Finish ----------------------------------------------------------------------

    [Fact]
    public void Standing_on_a_guarded_slot_it_clears_the_lowest_one()
    {
        var view = Seen(
            slots: new Dictionary<string, WorldSlot[]>
            {
                ["a"] = new[] { Slot(0, "wildland"), Slot(1, "lair", "w"), Slot(2, "spire", "w") }
            },
            entities: Band("a"));

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Clear, order.Command.Kind);
        Assert.Equal(1, order.Command.SlotIndex);      // lowest guarded, not lowest overall
        Assert.Equal("a", order.Command.SectorId);
    }

    [Fact]
    public void It_does_not_stop_to_clear_a_slot_with_an_enemy_watching()
    {
        var view = Seen(
            slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "lair", "w") } },
            entities: new[] { Band("a"), Band("a", owner: "dave") });

        Assert.NotEqual(WorldCommandKinds.Clear, Only(view).Command.Kind);
    }

    // ---- rule 3: Take ------------------------------------------------------------------------

    [Fact]
    public void Standing_on_clear_unowned_ground_it_claims_it()
    {
        var view = Seen(
            slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "wildland") } },
            entities: Band("a"));

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Claim, order.Command.Kind);
        Assert.Equal("a", order.Command.SectorId);
    }

    [Fact]
    public void It_does_not_claim_ground_that_is_still_guarded()
    {
        // Finish comes first for exactly this reason: claiming a guarded sector is refused at reveal.
        var view = Seen(
            slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "lair", "w") } },
            entities: Band("a"));

        Assert.Equal(WorldCommandKinds.Clear, Only(view).Command.Kind);
    }

    [Fact]
    public void It_does_not_claim_what_it_already_holds()
    {
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "wildland") } },
            Band("a"));

        Assert.NotEqual(WorldCommandKinds.Claim, Only(view).Command.Kind);
    }

    // ---- rule 1: Defend ----------------------------------------------------------------------

    [Fact]
    public void A_seat_under_more_threat_than_its_garrison_pulls_a_legion_home()
    {
        // Zomboss holds a Seat at `a` with nobody on it, a big enemy stack next door, and his own
        // band out at `c`.
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } },
            Band("c"),
            Band("b", owner: "dave", hp: 9000) with { EntityId = "e-dave-host" });

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Move, order.Command.Kind);
        Assert.Contains("defend a", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seat_whose_garrison_already_covers_the_threat_is_left_to_it()
    {
        // The rule that would otherwise fire forever. Threat *spreads*, so on a small map almost
        // everywhere carries some; comparing against zero would deadlock every legion at home.
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } },
            Band("a", hp: 9000),
            Band("c") with { EntityId = "e-zomboss-2" },
            Band("b", owner: "dave", hp: 50) with { EntityId = "e-dave-scout" });

        Assert.DoesNotContain(Decide(view), o => o.Reason.Contains("defend", StringComparison.Ordinal));
    }

    // ---- rule 5: Sever (spec-loam-ai.md) ------------------------------------------------------

    /// <summary>
    /// A dedicated shape for `Sever` alone: a-b-c-d. Zomboss holds his own Seat at "a"; Dave holds
    /// b, c, d as a chain. "c" is Dave's own articulation point — cutting it splits "d" off from
    /// "b" entirely; neither "b" nor "d" is.
    ///
    /// Zomboss's belief of Dave's three sectors is built directly (fully scouted), not through
    /// natural visibility the way `Seen()` computes it: `Sever` is scouting-gated by construction
    /// (spec-loam-ai.md), and a fixture that skipped this would only exercise the zero-fog
    /// degenerate case, not the rule itself.
    /// </summary>
    static IWorldView Severable(params WorldEntity[] entities)
    {
        var world = GraphShapes.From(600, "a-b", "b-c", "c-d") with { Factions = Map().Factions };
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId switch
                {
                    "a" => s with { OwnerFactionId = "zomboss", Slots = new[] { Slot(0, "seat") } },
                    "b" or "c" or "d" => s with { OwnerFactionId = "dave" },
                    _ => s
                })
                .ToList(),
            Entities = entities.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
        };

        IntelSnapshot Scout(string sectorId)
        {
            var sector = dressed.Sectors.Single(s => s.SectorId == sectorId);
            return new IntelSnapshot
            {
                SectorId = sectorId, LastSeenTurn = 0, Detail = SectorSight.Full,
                OwnerFactionId = sector.OwnerFactionId, Phase = sector.Phase, Climate = sector.Climate,
                DangerBand = sector.DangerBand
            };
        }

        var snapshots = new[] { "a", "b", "c", "d" }.Select(Scout)
            .OrderBy(s => s.SectorId, StringComparer.Ordinal)
            .ToList();

        return new BelievedWorldView(
            dressed with { Intel = new[] { new FactionIntel { FactionId = "zomboss", Sectors = snapshots } } },
            "zomboss");
    }

    [Fact]
    public void An_enemy_articulation_point_is_worth_marching_at()
    {
        var view = Severable(Band("a"));

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Move, order.Command.Kind);
        Assert.Contains("sever c", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_march_at_an_enemy_sector_that_is_not_load_bearing()
    {
        // Same shape, but only "b" and "d" — Dave's non-critical ends — are within reach the rule
        // would even consider naming as the *best* target once "c" is removed from the picture.
        // Proven directly: neither end's own severance score clears the threshold.
        var view = Severable(Band("a"));
        Assert.False(SeveranceScore.For(view, "dave", "b") > FrontierRulesPolicy.SeveranceThresholdCost);
        Assert.False(SeveranceScore.For(view, "dave", "d") > FrontierRulesPolicy.SeveranceThresholdCost);
    }

    [Fact]
    public void A_higher_rule_already_claiming_the_turn_pre_empts_sever()
    {
        // Zomboss's own band stands on clear, unowned ground (Take's own precondition) in the same
        // world that also has a genuine cut available elsewhere — Take must still win, since it is
        // strictly lower-risk than attacking an enemy-held junction.
        var world = GraphShapes.From(600, "a-b", "b-c", "c-d") with { Factions = Map().Factions };
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId switch
                {
                    "a" => s with { Slots = new[] { Slot(0, "wildland") } },
                    "b" or "c" or "d" => s with { OwnerFactionId = "dave" },
                    _ => s
                })
                .ToList(),
            Entities = new[] { Band("a") }
        };

        IntelSnapshot Scout(string sectorId)
        {
            var sector = dressed.Sectors.Single(s => s.SectorId == sectorId);
            return new IntelSnapshot
            {
                SectorId = sectorId, LastSeenTurn = 0, Detail = SectorSight.Full,
                OwnerFactionId = sector.OwnerFactionId, Phase = sector.Phase, Climate = sector.Climate,
                DangerBand = sector.DangerBand
            };
        }

        var snapshots = new[] { "a", "b", "c", "d" }.Select(Scout)
            .OrderBy(s => s.SectorId, StringComparer.Ordinal)
            .ToList();

        var view = new BelievedWorldView(
            dressed with { Intel = new[] { new FactionIntel { FactionId = "zomboss", Sectors = snapshots } } },
            "zomboss");

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Claim, order.Command.Kind);
        Assert.Equal("a", order.Command.SectorId);
    }

    // ---- rule 6: Recover ---------------------------------------------------------------------

    [Fact]
    public void Badly_hurt_and_in_supply_it_digs_in()
    {
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } },
            Band("a", hp: 200, wounds: 120));

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Stance, order.Command.Kind);
        Assert.Equal(MovementPolicy.Hold, order.Command.Stance);
    }

    [Fact]
    public void A_scratch_is_not_worth_a_turn_standing_still()
    {
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } },
            Band("a", hp: 200, wounds: 10));

        Assert.NotEqual(MovementPolicy.Hold, Only(view).Command.Stance);
    }

    [Fact]
    public void It_does_not_dig_in_where_there_is_nothing_to_eat()
    {
        // Holding is not a substitute for a supply line: out of supply it heals nothing and the
        // legion would simply starve in place.
        //
        // `c` is owned but has no Seat and no chain to one, so it is held and unsupplied — the only
        // shape that reaches this rule. Standing on *unowned* ground lets Take answer first and the
        // supply check is never consulted, which is how the first version of this passed while
        // asserting nothing.
        var view = Seen(
            new Dictionary<string, string> { ["c"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["c"] = new[] { Slot(0, "wildland") } },
            Band("c", hp: 200, wounds: 150));

        Assert.NotEqual(MovementPolicy.Hold, Only(view).Command.Stance);
    }

    [Fact]
    public void A_legion_already_dug_in_does_not_re_file_the_stance()
    {
        // The oscillation trap. A stance costs the turn it is committed, so re-filing it every turn
        // would leave the legion permanently committing to something it has already done.
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } },
            Band("a", stance: MovementPolicy.Hold, hp: 200, wounds: 150));

        var order = Only(view);
        Assert.False(order.Command.Kind == WorldCommandKinds.Stance
                     && order.Command.Stance == MovementPolicy.Hold);

        // And it says what it is actually doing. Falling through to "nothing worth doing" for the
        // five turns a legion spends healing is the audit trail lying about its most legible act.
        Assert.Contains("recovering", order.Reason, StringComparison.Ordinal);
    }

    // ---- rule 5: Explore ---------------------------------------------------------------------

    [Fact]
    public void Unknown_ground_within_reach_is_worth_going_to_look_at()
    {
        var view = Far(MovementPolicy.March, "a");
        Assert.Null(view.Believed("d"));

        var order = Only(view);
        Assert.Equal(WorldCommandKinds.Stance, order.Command.Kind);
        Assert.Equal(MovementPolicy.Scout, order.Command.Stance);
        Assert.Contains("scout toward", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_legion_already_scouting_marches_instead_of_re_filing_the_stance()
    {
        // The other half of the oscillation trap, and the one that would have every move dropped
        // forever: commit the posture on one turn, walk on the next.
        var order = Only(Far(MovementPolicy.Scout, "a"));

        Assert.Equal(WorldCommandKinds.Move, order.Command.Kind);
        Assert.NotEmpty(order.Command.LanePath);
        Assert.Contains("explore", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ground_further_than_it_would_walk_is_not_explored()
    {
        // Long lanes, so the *nearest* unknown sector is already past `ExploreTurns`. Choosing the
        // nearest is not the same as respecting the limit — an earlier version of this test asserted
        // the far sector was not named, which the nearest-first ordering satisfies on its own, so
        // deleting the limit changed nothing and the mutant lived.
        var world = GraphShapes.From(1600, "a-b", "b-c", "c-d") with { Factions = Map().Factions };
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = s.SectorId == "a" ? "zomboss" : null,
                    Slots = s.SectorId == "a" ? new[] { Slot(0, "seat") } : s.Slots
                })
                .ToList(),
            Entities = new[] { Band("a") }
        };

        var view = new BelievedWorldView(dressed with { Intel = IntelRecorder.Observe(dressed, dressed, 0) }, "zomboss");

        // `c` is the nearest unknown and it is two 1600-point lanes away: four turns against a
        // thousand-point budget, over the three the rule will spend.
        Assert.Null(view.Believed("c"));
        Assert.DoesNotContain(Decide(view), o => o.Reason.Contains("scout", StringComparison.Ordinal));
    }

    [Fact]
    public void With_the_whole_map_seen_there_is_nothing_left_to_explore()
    {
        var owners = new Dictionary<string, string> { ["a"] = "zomboss", ["b"] = "zomboss", ["c"] = "zomboss", ["d"] = "zomboss" };
        var slots = new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } };

        Assert.DoesNotContain(Decide(Seen(owners, slots, Band("a"))),
            o => o.Reason.Contains("scout", StringComparison.Ordinal));
    }

    [Fact]
    public void It_does_not_claim_guarded_ground_even_when_it_cannot_clear_it()
    {
        // Finish is blocked by the enemy standing here, so Take is the rule that actually decides —
        // and it has to refuse on its own account. Every earlier version of this test let Finish
        // answer first, so Take's guard was never exercised at all.
        var view = Seen(
            slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "lair", "w") } },
            entities: new[] { Band("a"), Band("a", owner: "dave") });

        Assert.NotEqual(WorldCommandKinds.Claim, Only(view).Command.Kind);
    }

    // ---- rule 6: Expand ----------------------------------------------------------------------

    [Fact]
    public void With_everything_seen_it_marches_at_the_best_ground_it_does_not_hold()
    {
        // Explore is exhausted, so the next rule down decides — and its reason has to carry the
        // numbers, because that is what makes an AI mistake legible as a mistake.
        var owners = new Dictionary<string, string> { ["a"] = "zomboss", ["b"] = "zomboss", ["c"] = "zomboss" };
        var slots = new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat") },
            ["d"] = new[] { Slot(0, "essence-deposit") }
        };

        var view = Seen(owners, slots, Band("a"));
        var order = Only(view);

        Assert.Equal(WorldCommandKinds.Move, order.Command.Kind);
        Assert.Contains("expand to", order.Reason, StringComparison.Ordinal);
        Assert.Contains("value", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void It_refuses_to_march_at_ground_that_scores_nothing()
    {
        // Expand takes the *best* reachable sector only if that best is worth having. Ground hanging
        // outside supply scores below zero by design, and a rule that marched at the least-bad
        // option would walk an army into exactly the overextension the penalty exists to prevent.
        //
        // The shape this needs is narrow, and getting it wrong is why the mutant survived twice:
        // there has to be a candidate Expand *would* take if it did not check the score.
        //
        // Zomboss holds `a` (his Seat) and `b`; his band stands at `c`. Everything is therefore seen,
        // so Explore declines. That leaves exactly one candidate — `d`, which touches nothing
        // supplied and so carries the overextension penalty straight through zero. Taking the
        // least-bad option would walk an army into precisely what the penalty exists to prevent.
        var owners = new Dictionary<string, string> { ["a"] = "zomboss", ["b"] = "zomboss" };
        var slots = new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat") } };
        var view = Seen(owners, slots, Band("c"));

        Assert.NotNull(view.Believed("d"));
        Assert.True(ValueMap.For(view, ThreatMap.For(view, ThreatReading.Defensive))["d"].Total < 0);

        Assert.DoesNotContain(Decide(view), o => o.Reason.Contains("expand", StringComparison.Ordinal));
    }

    // ---- rule 7: Hold ------------------------------------------------------------------------

    [Fact]
    public void With_nothing_worth_doing_it_stands_fast_rather_than_wandering()
    {
        // Everything owned, everything seen, nothing guarded, nothing threatening: the bottom of the
        // list, and it must be reachable or the AI fidgets forever.
        var owners = new Dictionary<string, string> { ["a"] = "zomboss", ["b"] = "zomboss", ["c"] = "zomboss", ["d"] = "zomboss" };
        var slots = new Dictionary<string, WorldSlot[]>
        {
            ["a"] = new[] { Slot(0, "seat") },
            ["b"] = new[] { Slot(0, "wildland") },
            ["c"] = new[] { Slot(0, "wildland") },
            ["d"] = new[] { Slot(0, "wildland") }
        };

        Assert.Equal(WorldCommandKinds.StandFast, Only(Seen(owners, slots, Band("a"))).Command.Kind);
    }

    // ---- what the engine makes of it ------------------------------------------------------------

    [Fact]
    public void Everything_the_policy_files_is_admissible()
    {
        // A policy that files something admission refuses is a faction that commits every turn
        // having done nothing — indistinguishable from standing fast on purpose.
        foreach (var view in new[]
                 {
                     Seen(slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "lair", "w") } }, entities: Band("a")),
                     Seen(slots: new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "wildland") } }, entities: Band("a")),
                     Seen(entities: Band("a", wounds: 150)),
                     Seen(entities: Band("c"))
                 })
        {
            var world = Map() with
            {
                Sectors = Map().Sectors.Select(s => s with { Slots = s.Slots }).ToList(),
                Entities = new[] { Band("a"), Band("c") with { EntityId = "e-zomboss-2" } }
            };

            foreach (var order in Decide(view))
            {
                // The world the engine will judge it against has both bands present, so an order for
                // either is well-formed.
                var (ok, reason) = WorldCommandAdmission.Admit(world, order.Command);
                Assert.True(ok, $"{order.Command.Kind}: {reason} ({order.Reason})");
            }
        }
    }

    // ---- determinism ------------------------------------------------------------------------------

    [Fact]
    public void The_same_belief_decides_the_same_way_twice()
    {
        var view = Seen(
            new Dictionary<string, string> { ["a"] = "zomboss" },
            new Dictionary<string, WorldSlot[]> { ["a"] = new[] { Slot(0, "seat"), Slot(1, "lair", "w") } },
            Band("a"));

        static string Render(IReadOnlyList<PolicyOrder> orders) =>
            string.Join("|", orders.Select(o => $"{o.Command.CommandId}:{o.Command.Kind}:{o.Reason}"));

        Assert.Equal(Render(Decide(view)), Render(Decide(view)));
    }

    [Fact]
    public void Reversing_the_forces_changes_nothing()
    {
        var entities = new[] { Band("a"), Band("c") with { EntityId = "e-zomboss-2" } };
        var forward = Seen(entities: entities);
        var backward = Seen(entities: entities.Reverse().ToArray());

        Assert.Equal(
            Decide(forward).Select(o => o.Command.CommandId),
            Decide(backward).Select(o => o.Command.CommandId));
    }

    // ---- L31: the march-loam gate (spec-loam-ai.md) -------------------------------------------

    static WorldEntity ThinBand(string at) => new()
    {
        EntityId = "e-zomboss-1", Kind = WorldEntityKind.Warband, OwnerFactionId = "zomboss",
        AtSectorId = at, Stance = "march", MovementRemaining = MovementPolicy.BudgetFor("march"),
        // 8 members, one bearer: Capacity 200, Burn 80 — a short leash the gate can actually
        // discriminate on, unlike the well-provisioned default `Band()`.
        Members = Enumerable.Range(0, 7)
            .Select(_ => new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 200 })
            .Append(new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 200, Role = WorldEntityMemberRole.Bearer })
            .ToList()
    };

    [Fact]
    public void The_gate_measures_the_worst_out_of_supply_run_not_the_routes_total_length()
    {
        // Same total length (5 lanes, a through f) both times, and that is deliberate: it isolates
        // the one thing that must decide the outcome. Continuous: only "a" is supplied, so the
        // whole rest of the route is one five-long stretch — 200 capacity cannot cover 80 x 5 = 400.
        // Broken: "d" is also supplied, splitting the same five lanes into two two-long stretches —
        // 200 easily covers 80 x 2 = 160. A check that only summed total distance would treat both
        // identically; this one does not.
        var lanePath = new[] { "l-a-b", "l-b-c", "l-c-d", "l-d-e", "l-e-f" };
        var world = GraphShapes.From(600, "a-b", "b-c", "c-d", "d-e", "e-f");
        var entity = ThinBand("a");

        var continuous = new HashSet<string>(StringComparer.Ordinal) { "a" };
        Assert.False(FrontierRulesPolicy.SurvivesTheRoute(
            new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, "zomboss"),
            entity, lanePath, continuous));

        var broken = new HashSet<string>(StringComparer.Ordinal) { "a", "d" };
        Assert.True(FrontierRulesPolicy.SurvivesTheRoute(
            new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, "zomboss"),
            entity, lanePath, broken));
    }

    [Fact]
    public void A_thin_roster_is_refused_a_march_a_well_provisioned_one_would_take()
    {
        // Zomboss holds only "a"; "b" and "c" are unowned ground he must cross to reach "d", so the
        // whole three-lane route beyond his own Seat is one unsupplied stretch. Belief is built
        // directly (fully scouted) rather than through natural visibility, matching this gate's own
        // scouting-adjacent precondition and sidestepping Explore's own glimpse/unknown handling,
        // which is not what this test is about.
        var world = Map();
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId switch
                {
                    "a" => s with { OwnerFactionId = "zomboss", Slots = new[] { Slot(0, "seat") } },
                    "d" => s with { Slots = new[] { Slot(0, "essence-deposit") } },
                    _ => s
                })
                .ToList(),
            Entities = new[] { ThinBand("a") }
        };

        IntelSnapshot Scout(string sectorId)
        {
            var sector = dressed.Sectors.Single(s => s.SectorId == sectorId);
            return new IntelSnapshot
            {
                SectorId = sectorId, LastSeenTurn = 0, Detail = SectorSight.Full,
                OwnerFactionId = sector.OwnerFactionId, Phase = sector.Phase, Climate = sector.Climate,
                DangerBand = sector.DangerBand,
                Slots = sector.Slots.Select(sl => new RememberedSlot
                {
                    SlotIndex = sl.SlotIndex, SlotTypeId = sl.SlotTypeId, Element = sl.Element,
                    GuardWaveId = sl.GuardWaveId, State = sl.State, GuardState = sl.GuardState
                }).ToList()
            };
        }

        var snapshots = new[] { "a", "b", "c", "d" }.Select(Scout)
            .OrderBy(s => s.SectorId, StringComparer.Ordinal)
            .ToList();

        var view = new BelievedWorldView(
            dressed with { Intel = new[] { new FactionIntel { FactionId = "zomboss", Sectors = snapshots } } },
            "zomboss");

        Assert.DoesNotContain(Decide(view), o => o.Reason.Contains("expand", StringComparison.Ordinal));
    }
}
