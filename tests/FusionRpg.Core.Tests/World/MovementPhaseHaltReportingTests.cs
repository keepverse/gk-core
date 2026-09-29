using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W13 (fog defect B): a `halt` line's `Detail` was `"zoc:" + sectorId` — never a bare
/// sector id — and `MovementPhase.cs`'s generic report line put that same string in the structured
/// sector slot too, so `Believed("zoc:s2")` returned null and the line reached nobody. Before this
/// fix, no test in this assembly exercised a halt line's report entry at all — the todo's own claim
/// that "today it reaches nobody" was untested, not assumed.
/// </summary>
public class MovementPhaseHaltReportingTests
{
    const string Dave = "dave";
    const string Zomboss = "zomboss";
    const string Phase = "movement";

    static WorldEntity Legion(string id, string owner, string atSectorId) => new()
    {
        EntityId = id, Kind = WorldEntityKind.Legion, OwnerFactionId = owner, AtSectorId = atSectorId,
        Stance = "march", MovementRemaining = 1000,
        Members = new[] { new WorldEntityMember { SpeciesId = "grunt", Hp = 100 } }
    };

    static WorldState World(params WorldEntity[] entities) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[]
        {
            new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = "Zomboss" }
        },
        Sectors = new[]
        {
            new WorldSector { SectorId = "s1", OwnerFactionId = Dave, Phase = SectorPhase.Held },
            new WorldSector { SectorId = "s2", Phase = SectorPhase.Unknown }
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l-s1-s2", FromSectorId = "s1", ToSectorId = "s2", TypeId = "corridor", Length = 100, Width = 1000 }
        },
        Entities = entities
    };

    static WorldCommand Move(string entityId, params string[] lanePath) => new()
    {
        CommanderId = Dave, CommandId = "m1", Kind = WorldCommandKinds.Move, EntityId = entityId, LanePath = lanePath
    };

    /// <summary>The attacker always routs, the defender always wins — `actor-hub-and-combat-power-solid-fixing`
    /// T20 turned Sector-kind contact into an honest no-op, so an explicit resolver manufactures the
    /// routed precondition this file's halt-then-fall-back test needs.</summary>
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

    [Fact]
    public void A_halt_line_reaches_its_owner_and_carries_the_real_sector_it_stopped_in()
    {
        // s2 is held against Dave by a hostile Legion — walking into it during this march halts the
        // legion right there (ZoneOfControl.IsHeldAgainst, MarchResolver.cs:120-126). The same arrival
        // also triggers a Sector-kind contact fight against the stationary blocker; an explicit
        // resolver (T20: real Sector-kind contact is now a no-op) routs Dave, who falls back down
        // `l-s1-s2` to s1 (world-map, 2026-09-05) — a later effect the halt line itself predates,
        // since it is written from `moved[entity]`'s position before any battle resolves.
        var mover = Legion("e-dave", Dave, "s1");
        var blocker = Legion("e-zomboss", Zomboss, "s2");
        var world = World(mover, blocker);

        var report = new TurnReport();
        var result = MovementPhase.Run(
            world, new[] { Move("e-dave", "l-s1-s2") }, report, Phase, turn: 1,
            resolver: new AttackerAlwaysRouts(), seed: 1);

        var legion = result.World.Entities.Single(e => e.EntityId == "e-dave");
        Assert.True(legion.Routed);
        Assert.Equal("s1", legion.AtSectorId);

        // `TurnEventKinds.Halt` is the queue's own internal kind, folded into `Detail` as
        // "halt:zoc:<sectorId>" — the report entry's own `Kind` is always `TurnReportKinds.Event`.
        // Its `s2` is the halt itself, timestamped before the fall-back that follows it.
        var halt = Assert.Single(report.Entries, e => e.Detail.StartsWith(TurnEventKinds.Halt + ":"));
        Assert.Equal("s2", halt.SectorId);
        Assert.Equal(Dave, halt.Audience);
        // The lane-prefixed narration text still names the ground, which the client already reads.
        Assert.Contains("s2", halt.Detail);
    }

    [Fact]
    public void An_arrival_mid_lane_carries_no_sector_id_the_lane_stays_in_detail_only()
    {
        // A lane too long to finish in one turn's budget leaves the legion mid-lane — its Arrival
        // event's Detail is the lane id, which must not land in the structured sector slot either.
        var mover = Legion("e-dave", Dave, "s1");
        var world = World(mover) with
        {
            Lanes = new[]
            {
                new WorldLane { LaneId = "l-s1-s2", FromSectorId = "s1", ToSectorId = "s2", TypeId = "corridor", Length = 10_000, Width = 1000 }
            }
        };

        var report = new TurnReport();
        MovementPhase.Run(
            world, new[] { Move("e-dave", "l-s1-s2") }, report, Phase, turn: 1,
            resolver: DistrictAssaultResolver.Instance, seed: 1);

        var arrival = Assert.Single(report.Entries, e => e.Detail.StartsWith(TurnEventKinds.Arrival + ":"));
        Assert.Null(arrival.SectorId);
        Assert.Contains("l-s1-s2", arrival.Detail);
    }
}
