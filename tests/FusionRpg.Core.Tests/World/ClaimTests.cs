using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W11 (spec-world-movement.md §Claiming): holding ground. A sector falls only when nothing hostile
/// stands in it and every slot has been cleared — so a rich sector costs several turns and several
/// fights, which is the intended shape of the map rather than a side effect.
/// </summary>
public class ClaimTests
{
    /// <summary>Guard-kind contact resolves as a clean win — `actor-hub-and-combat-power-solid-fixing`
    /// T20 made `BattleKinds.Guard` one of `DistrictAssaultResolver`'s refused non-district kinds, so
    /// clearing a slot no longer happens on its own; this stands in so a `clear` campaign can still
    /// reach the claim it is really testing.</summary>
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

    /// <summary>The attacker always routs, the defender always wins — T20 turned Sector-kind contact
    /// into an honest no-op, so an explicit resolver manufactures the routed precondition this file's
    /// contested-claim test needs.</summary>
    sealed class AttackerAlwaysRouts : IBattleResolver
    {
        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
        {
            var attacker = combatants.Single(e => e.EntityId == request.AttackerEntityId);
            var defender = combatants.Single(e => e.EntityId == request.DefenderEntityId);

            return new BattleOutcome
            {
                BattleId = request.BattleId,
                WinnerEntityId = defender.EntityId,
                Sides = new[]
                {
                    new BattleSideOutcome { EntityId = defender.EntityId, Survivors = defender.Members },
                    new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = attacker.Members, Routed = true }
                }
            };
        }
    }

    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Claim(string commander, string entityId, string sectorId) => new()
    {
        CommanderId = commander,
        CommandId = "k-" + entityId,
        Kind = WorldCommandKinds.Claim,
        EntityId = entityId,
        SectorId = sectorId
    };

    static WorldCommand Move(string commander, string entityId, params string[] lanePath) => new()
    {
        CommanderId = commander,
        CommandId = "m-" + entityId,
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = lanePath
    };

    static WorldCommand Clear(string commander, string entityId, string sectorId, int slotIndex) => new()
    {
        CommanderId = commander,
        CommandId = "c-" + entityId + "-" + slotIndex,
        Kind = WorldCommandKinds.Clear,
        EntityId = entityId,
        SectorId = sectorId,
        SlotIndex = slotIndex
    };

    static WorldState Place(WorldState w, string entityId, string sectorId, int movement = 1000) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with
                {
                    AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null,
                    LaneProgressMilli = 0, MovementRemaining = movement, Stance = "march"
                }
                : e)
            .ToList()
    };

    /// <summary>Every guard in a sector already beaten — the state a `clear` campaign ends in.</summary>
    static WorldState AllGuardsCleared(WorldState w, string sectorId) => w with
    {
        Sectors = w.Sectors
            .Select(s => s.SectorId == sectorId
                ? s with { Slots = s.Slots.Select(sl => sl with { GuardState = GuardState.Cleared }).ToList() }
                : s)
            .ToList()
    };

    static WorldSector Sector(WorldState w, string id) => w.Sectors.Single(s => s.SectorId == id);

    [Fact]
    public void A_cleared_sector_you_are_standing_in_becomes_yours()
    {
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");

        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        var ember = Sector(result.World, "ember-hollow");
        Assert.Equal("dave", ember.OwnerFactionId);
        Assert.Equal(SectorPhase.Held, ember.Phase);
        Assert.Equal(result.World.CurrentTurn, ember.LastSeenTurn);
    }

    static PowerTuning Power() => PowerTuning.Build(
        schemaVersion: 1, version: 1,
        cMilli: 80_000, bMilli: 400, pinIndex: 20, pinValue: 680,
        wdMilli: 1000, waMilli: 25_000, wrMilli: 250, wzMilli: 1000,
        wmMilli: 5000, wwMilli: 5000, wfMilli: 25_000);

    [Fact]
    public void ClaimResolver_invokes_no_loot_resolver_when_none_is_supplied_byte_identical_to_today()
    {
        // Safe default (incremental-implementation Rule 4): every existing caller, including every
        // other test in this file, passes no PowerTuning at all -- behavior must be untouched.
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");
        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        Assert.Equal("dave", Sector(result.World, "ember-hollow").OwnerFactionId);
        Assert.DoesNotContain(result.Report.Entries, e => e.Detail.StartsWith("claim.loot:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Report.Entries, e => e.Detail.StartsWith("claim.mythic:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_successful_claim_resolves_loot_when_a_power_tuning_is_supplied()
    {
        // "Inert but correct until a real production caller exists" (drop-tables ideal.md, matching
        // the affix_channel precedent) -- ember-hollow's own danger band must resolve to a real
        // content level for this to fire; the assertion is the SHAPE (an event exists, or the sector
        // is safe ground and none does), not a specific table id this fixture doesn't control.
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");
        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") },
            seed: 1, powerTuning: Power());

        var ember = Sector(result.World, "ember-hollow");
        Assert.Equal("dave", ember.OwnerFactionId); // the claim itself is unaffected either way

        var lootEvents = result.Report.Entries.Where(e => e.Detail.StartsWith("claim.loot:", StringComparison.Ordinal)).ToList();
        if (ember.DangerBand < 1)
            Assert.Empty(lootEvents); // safe ground refuses by name, never floors (WorldSectorLootSource.cs)
        else
            Assert.Single(lootEvents);
    }

    [Fact]
    public void A_boss_lair_claim_can_roll_an_independent_mythic_bonus_reproducibly()
    {
        // The real, end-to-end usage of RateAuthoring.IndependentRateEntry/Hit the drop-tables
        // ideal doc's own open item asked for: a rate that stays exact regardless of the normal
        // weighted table draw, checked on its own named stream. Ember Hollow is not a boss lair in
        // the shipped template, so this proves the MECHANISM (same seed -> same result) rather than
        // asserting a specific hit/miss, which world-template content this test does not own would
        // make flaky.
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");
        var firstRun = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") },
            seed: 42, powerTuning: Power(), mythicClaimBonusRatePerMillion: 10_000);
        var secondRun = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") },
            seed: 42, powerTuning: Power(), mythicClaimBonusRatePerMillion: 10_000);

        bool MythicFired(TurnResult r) =>
            r.Report.Entries.Any(e => e.Detail.StartsWith("claim.mythic:", StringComparison.Ordinal));

        Assert.Equal(MythicFired(firstRun), MythicFired(secondRun));
    }

    [Fact]
    public void A_sector_with_any_guard_left_standing_cannot_be_claimed()
    {
        // ember-hollow slot 3 is a lair with a live guard; slot 2 is cleared first so the reason has
        // to name the one that is actually still in the way.
        var world = World();
        var partly = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "ember-hollow"
                    ? s with
                    {
                        Slots = s.Slots
                            .Select(sl => sl.SlotIndex == 2 ? sl with { GuardState = GuardState.Cleared } : sl)
                            .ToList()
                    }
                    : s)
                .ToList()
        };

        var result = TurnEngine.Step(Place(partly, "e-dave-legion-1", "ember-hollow"),
            new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        var dropped = Assert.Single(result.Report.Dropped);
        Assert.Equal("claim.guarded:3", dropped.Detail);
        Assert.Null(Sector(result.World, "ember-hollow").OwnerFactionId);
    }

    [Fact]
    public void Claiming_what_you_already_hold_is_a_reported_no_op()
    {
        var result = TurnEngine.Step(World(), new[] { Claim("dave", "e-dave-legion-1", "homeworld") }, seed: 1);

        Assert.Empty(result.Report.Dropped);
        Assert.Contains(result.Report.Entries, e => e.Detail.Contains("claim.already-yours"));
        Assert.Equal("dave", Sector(result.World, "homeworld").OwnerFactionId);
    }

    [Fact]
    public void A_sector_a_hostile_force_stands_in_cannot_be_claimed()
    {
        // The wild pack lives in ash-waste. Dave arrives heavy enough to beat it but not to wipe it
        // out, so a broken but still-present enemy is what blocks the claim.
        var world = AllGuardsCleared(World(), "ash-waste");
        world = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1"
                    ? e with { Members = e.Members.Select(m => m with { Hp = 400 }).ToList() }
                    : e)
                .ToList()
        };
        world = Place(world, "e-dave-legion-1", "ash-waste");

        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ash-waste") }, seed: 1);

        Assert.Null(Sector(result.World, "ash-waste").OwnerFactionId);
        Assert.Contains(result.Report.Dropped, e => e.Detail == "claim.contested");
    }

    [Fact]
    public void Claiming_a_sector_you_are_not_standing_in_is_dropped()
    {
        var world = AllGuardsCleared(World(), "ember-hollow");

        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "claim.elsewhere");
        Assert.Null(Sector(result.World, "ember-hollow").OwnerFactionId);
    }

    /// <summary>
    /// Two factions cannot both claim one sector, and there is no separate tie-break for it: while
    /// both are standing there each blocks the other, and the fight that put them in the same place
    /// is what decides it. Amended 2026-09-05: the loser of that fight now falls back down the lane
    /// it marched in on rather than staying to contest ground it just lost, so — for this shape,
    /// an attacker beaten the same turn it arrives — the winner's claim settles that same turn
    /// instead of needing a second turn to finish the loser off first (`ContactAndClearTests`'s own
    /// `A_routed_force_with_nowhere_to_fall_back_to_is_finished_off_next_turn` still proves the older,
    /// two-turn shape for a defender that never moved and truly has nothing to fall back to).
    /// </summary>
    [Fact]
    public void Two_factions_claiming_one_sector_settle_it_by_fighting_over_it()
    {
        var world = AllGuardsCleared(World(), "ember-hollow");
        world = Place(world, "e-wild-pack-1", "ember-hollow", movement: 0);

        var result = TurnEngine.Step(world, new[]
        {
            Move("dave", "e-dave-legion-1", "l-home-ember"),
            Claim("dave", "e-dave-legion-1", "ember-hollow"),
            Claim("wild", "e-wild-pack-1", "ember-hollow")
        }, seed: 1, new AttackerAlwaysRouts());

        // Dave's legion routs and falls back to homeworld before Snapshot's claim check runs — his
        // own claim is dropped for being routed, not for the ground being contested.
        Assert.Contains(result.Report.Dropped, e => e.Subject == "k-e-dave-legion-1" && e.Detail == "entity.routed");
        var legion = result.World.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        Assert.True(legion.Routed);
        Assert.Equal("homeworld", legion.AtSectorId);

        // Nothing hostile is left standing in ember-hollow by the time wild's claim is checked.
        Assert.Equal("wild", Sector(result.World, "ember-hollow").OwnerFactionId);
    }

    [Fact]
    public void A_second_legion_claiming_what_its_own_side_just_took_is_a_no_op()
    {
        var world = AllGuardsCleared(World(), "ember-hollow");
        var reinforced = world with
        {
            Entities = world.Entities
                .Append(world.Entities.Single(e => e.EntityId == "e-dave-legion-1") with
                {
                    EntityId = "e-dave-legion-2", AtSectorId = "ember-hollow"
                })
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        };
        reinforced = Place(WorldValidation.Validate(reinforced), "e-dave-legion-1", "ember-hollow");

        var result = TurnEngine.Step(reinforced, new[]
        {
            Claim("dave", "e-dave-legion-1", "ember-hollow"),
            Claim("dave", "e-dave-legion-2", "ember-hollow")
        }, seed: 1);

        Assert.Equal("dave", Sector(result.World, "ember-hollow").OwnerFactionId);
        Assert.Empty(result.Report.Dropped);
        Assert.Contains(result.Report.Entries, e => e.Detail.Contains("claim.already-yours"));
    }

    [Fact]
    public void March_clear_clear_claim_takes_a_sector_over_four_turns()
    {
        var state = World();

        var t1 = TurnEngine.Step(state, new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1);
        Assert.Equal("ember-hollow", t1.World.Entities.Single(e => e.EntityId == "e-dave-legion-1").AtSectorId);

        var t2 = TurnEngine.Step(t1.World, new[] { Clear("dave", "e-dave-legion-1", "ember-hollow", 2) }, seed: 1, new GuardAlwaysClears());
        var t3 = TurnEngine.Step(t2.World, new[] { Clear("dave", "e-dave-legion-1", "ember-hollow", 3) }, seed: 1, new GuardAlwaysClears());
        Assert.All(Sector(t3.World, "ember-hollow").Slots, sl => Assert.Equal(GuardState.Cleared, sl.GuardState));

        var t4 = TurnEngine.Step(t3.World, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);
        Assert.Equal("dave", Sector(t4.World, "ember-hollow").OwnerFactionId);
        Assert.Equal(SectorPhase.Held, Sector(t4.World, "ember-hollow").Phase);
    }

    [Fact]
    public void Claiming_is_deterministic_and_independent_of_command_order()
    {
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");
        var claim = Claim("dave", "e-dave-legion-1", "ember-hollow");
        var stand = new WorldCommand
        {
            CommanderId = "wild", CommandId = "s1", Kind = WorldCommandKinds.StandFast
        };

        var forward = TurnEngine.Step(world, new[] { claim, stand }, seed: 1);
        var reversed = TurnEngine.Step(world, new[] { stand, claim }, seed: 1);

        Assert.Equal(forward.StateHash, reversed.StateHash);
    }

    [Fact]
    public void A_claim_without_a_sector_is_refused_at_admission()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World(), new WorldCommand
        {
            CommanderId = "dave",
            CommandId = "k1",
            Kind = WorldCommandKinds.Claim,
            EntityId = "e-dave-legion-1"
        });

        Assert.False(ok);
        Assert.Equal("sector.missing", reason);
    }

    [Fact]
    public void Claiming_never_costs_a_legion_its_march_budget()
    {
        var world = Place(AllGuardsCleared(World(), "ember-hollow"), "e-dave-legion-1", "ember-hollow");
        var result = TurnEngine.Step(world, new[] { Claim("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        Assert.All(result.World.Entities,
            e => Assert.Equal(MovementPolicy.BudgetFor(e.Stance), e.MovementRemaining));
    }
}
