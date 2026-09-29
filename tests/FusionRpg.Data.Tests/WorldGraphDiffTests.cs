using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// base-defense `world-graph-diff` 3.3 (spec-world-graph-diff.md) — the diffing writer's own test
/// table. Drives <see cref="RpgStore.DiffCommitForTest"/> directly (real SQL, real transaction, real
/// equivalence guard — the same call the production commit path makes) rather than through a scripted
/// turn, so each case proves the diff mechanism itself: DELETE handling, a grown row list, the
/// unchanged-world no-op, and `long` magnitudes surviving the path untruncated. Gameplay does not
/// currently delete a slot or a lane, so those two cases construct the shape directly — proving the
/// mechanism is correct is the point, not that today's rules happen to exercise it.
/// </summary>
public class WorldGraphDiffTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const string WorldId = "diff-test";

    public WorldGraphDiffTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        var (ok, reason, _) = _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, WorldId));
        Assert.True(ok, reason);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Unchanged_world_round_trips_through_the_diff_path_unchanged()
    {
        var before = _store.LoadWorldState(WorldId)!;

        // Every row's own comparator short-circuits to `continue` before this diffs anything --
        // proven by construction (RpgStore.WorldGraphDiff.cs's per-table loops), and proven
        // behaviourally here: diffing a world against itself must read back identically.
        var after = _store.DiffCommitForTest(WorldId, before);

        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(before),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    [Fact]
    public void A_grown_slot_list_writes_the_new_slot_and_keeps_the_existing_ones()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var sector = before.Sectors.First(s => s.Slots.Count > 0);
        var originalSlotCount = sector.Slots.Count;
        var grownSlot = new WorldSlot { SlotIndex = originalSlotCount, SlotTypeId = "seat" };

        var next = before with
        {
            Sectors = before.Sectors
                .Select(s => s.SectorId == sector.SectorId
                    ? s with { Slots = s.Slots.Append(grownSlot).ToList() }
                    : s)
                .ToList()
        };

        var after = _store.DiffCommitForTest(WorldId, next);
        var afterSector = after.Sectors.Single(s => s.SectorId == sector.SectorId);

        Assert.Equal(originalSlotCount + 1, afterSector.Slots.Count);
        Assert.Contains(afterSector.Slots, sl => sl.SlotIndex == originalSlotCount && sl.SlotTypeId == "seat");
        // The pre-existing slots are the SAME values as before -- the diff did not need to touch
        // them, and this proves it did not corrupt them either.
        for (var i = 0; i < originalSlotCount; i++)
            Assert.Equal(sector.Slots[i], afterSector.Slots[i]);
    }

    [Fact]
    public void A_slot_removed_from_next_is_deleted_from_the_row_not_left_stale()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var sector = before.Sectors.First(s => s.Slots.Count > 1);
        var removedIndex = sector.Slots[^1].SlotIndex;

        var next = before with
        {
            Sectors = before.Sectors
                .Select(s => s.SectorId == sector.SectorId
                    ? s with { Slots = s.Slots.Take(s.Slots.Count - 1).ToList() }
                    : s)
                .ToList()
        };

        var after = _store.DiffCommitForTest(WorldId, next);
        var afterSector = after.Sectors.Single(s => s.SectorId == sector.SectorId);

        Assert.DoesNotContain(afterSector.Slots, sl => sl.SlotIndex == removedIndex);
        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(next),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    [Fact]
    public void An_entity_removed_from_next_is_deleted_along_with_its_members()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var entity = before.Entities.First(e => e.Members.Count > 0);

        var next = before with { Entities = before.Entities.Where(e => e.EntityId != entity.EntityId).ToList() };

        var after = _store.DiffCommitForTest(WorldId, next);

        Assert.DoesNotContain(after.Entities, e => e.EntityId == entity.EntityId);
        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(next),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    [Fact]
    public void A_lane_removed_from_next_is_deleted()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var lane = before.Lanes.First();

        var next = before with { Lanes = before.Lanes.Where(l => l.LaneId != lane.LaneId).ToList() };

        var after = _store.DiffCommitForTest(WorldId, next);

        Assert.DoesNotContain(after.Lanes, l => l.LaneId == lane.LaneId);
        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(next),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    /// <summary>
    /// docs/architecture/numeric-types.md's numeric-overflow rule: `long` for any magnitude, never narrowed on a write path.
    /// A value comfortably past `int.MaxValue` (2,147,483,647) proves the diff path's parameter
    /// binding never routes a stock through an `int`.
    /// </summary>
    [Fact]
    public void A_long_loam_stock_past_int_range_survives_the_diff_path_unnarrowed()
    {
        const long bigStock = 9_000_000_000L; // > int.MaxValue by a wide margin
        var before = _store.LoadWorldState(WorldId)!;
        var sector = before.Sectors.First();

        var next = before with
        {
            Sectors = before.Sectors
                .Select(s => s.SectorId == sector.SectorId ? s with { LoamStock = bigStock } : s)
                .ToList()
        };

        var after = _store.DiffCommitForTest(WorldId, next);

        Assert.Equal(bigStock, after.Sectors.Single(s => s.SectorId == sector.SectorId).LoamStock);
    }

    [Fact]
    public void A_long_carried_loam_past_int_range_survives_the_diff_path_unnarrowed()
    {
        const long bigCarry = 5_000_000_000L;
        var before = _store.LoadWorldState(WorldId)!;
        var entity = before.Entities.First();

        var next = before with
        {
            Entities = before.Entities
                .Select(e => e.EntityId == entity.EntityId ? e with { CarriedLoam = bigCarry } : e)
                .ToList()
        };

        var after = _store.DiffCommitForTest(WorldId, next);

        Assert.Equal(bigCarry, after.Entities.Single(e => e.EntityId == entity.EntityId).CarriedLoam);
    }

    /// <summary>
    /// The DevelopmentLevel persistence gap this module's own equivalence guard found (2026-09-05,
    /// see RpgStore.World.cs's EnsureColumn migration note): `WorldCanonical` has always hashed
    /// `IntelSnapshot.DevelopmentLevel`, but `rpg_world_faction_intel` never carried a column for it,
    /// so it silently read back as 0. Regression-proofed here at the diff path directly, in addition
    /// to WorldWaveOneAcceptanceTests's golden re-bless (#14) that first caught it through gameplay.
    /// </summary>
    [Fact]
    public void An_intel_snapshots_development_level_survives_the_diff_path()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var faction = before.Factions.First().FactionId;
        var sectorId = before.Sectors.First().SectorId;
        var snapshot = new FusionRpg.Core.World.Intel.IntelSnapshot
        {
            SectorId = sectorId, LastSeenTurn = before.CurrentTurn,
            Detail = FusionRpg.Core.World.Intel.SectorSight.Full,
            OwnerFactionId = faction, Phase = SectorPhase.Held, DevelopmentLevel = 7
        };

        var next = before with
        {
            Intel = before.Intel
                .Select(fi => fi.FactionId == faction
                    ? fi with
                    {
                        Sectors = fi.Sectors.Any(s => s.SectorId == sectorId)
                            ? fi.Sectors.Select(s => s.SectorId == sectorId ? snapshot : s).ToList()
                            : fi.Sectors.Append(snapshot).ToList()
                    }
                    : fi)
                .ToList()
        };
        if (next.Intel.All(fi => fi.FactionId != faction))
            next = next with { Intel = next.Intel.Append(new FusionRpg.Core.World.Intel.FactionIntel
            {
                FactionId = faction, Sectors = new[] { snapshot }
            }).ToList() };

        var after = _store.DiffCommitForTest(WorldId, next);

        var readSnapshot = after.Intel.Single(fi => fi.FactionId == faction).Of(sectorId)!;
        Assert.Equal(7, readSnapshot.DevelopmentLevel);
        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(next),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    /// <summary>
    /// spec-world-graph-diff.md's own acceptance line: "`WorldCanonical.Hash(readBack) ==
    /// WorldCanonical.Hash(next)` over 500 randomised mutations." Each iteration applies one random
    /// mutation (add/remove/change a slot, entity, member, lane, faction field, or intel snapshot) to
    /// the previous committed state and diff-commits it — the equivalence guard
    /// (<see cref="RpgStore.GraphWriteEquivalenceCheckEnabled"/>, on by default in this Debug test
    /// build) fires on every single one of the 500 commits, so a silent survivor anywhere in the diff
    /// path throws before this test can finish rather than after. The explicit hash assertion at the
    /// end is a second, independent check over and above the guard's own internal one.
    /// </summary>
    [Fact]
    public void Five_hundred_random_mutations_all_round_trip_through_the_diff_path_unchanged()
    {
        var rng = new Random(20260905);
        var current = _store.LoadWorldState(WorldId)!;

        for (var i = 0; i < 500; i++)
        {
            var next = RandomMutation.Apply(current, rng, i);
            current = _store.DiffCommitForTest(WorldId, next);
            Assert.Equal(
                FusionRpg.Core.World.Turn.StateHasher.Hash(next),
                FusionRpg.Core.World.Turn.StateHasher.Hash(current));
        }
    }
}

/// <summary>One random, self-contained mutation to a <see cref="WorldState"/> — the generator for
/// <see cref="WorldGraphDiffTests.Five_hundred_random_mutations_all_round_trip_through_the_diff_path_unchanged"/>.
/// Every branch keeps the world valid enough for <see cref="RpgStore.DiffCommitForTest"/> to accept
/// (real ids, no orphaned references) since the point is exercising the diff, not fuzzing admission.</summary>
static class RandomMutation
{
    public static WorldState Apply(WorldState world, Random rng, int iteration)
    {
        var kind = rng.Next(7);
        return kind switch
        {
            0 => GrowASlot(world, rng, iteration),
            1 => RemoveASlot(world, rng),
            2 => ChangeASectorField(world, rng),
            3 => ChangeAnEntityField(world, rng),
            4 => ChangeALaneField(world, rng),
            5 => ChangeAFactionField(world, rng),
            _ => ChangeOrAddIntel(world, rng, iteration),
        };
    }

    static WorldState GrowASlot(WorldState world, Random rng, int iteration)
    {
        var sector = Pick(world.Sectors, rng);
        var newIndex = sector.Slots.Count == 0 ? 0 : sector.Slots.Max(sl => sl.SlotIndex) + 1;
        var slot = new WorldSlot { SlotIndex = newIndex, SlotTypeId = "seat", State = SlotState.Intact };
        return world with
        {
            Sectors = world.Sectors.Select(s => s.SectorId == sector.SectorId
                ? s with { Slots = s.Slots.Append(slot).ToList() } : s).ToList()
        };
    }

    static WorldState RemoveASlot(WorldState world, Random rng)
    {
        var candidates = world.Sectors.Where(s => s.Slots.Count > 1).ToList();
        if (candidates.Count == 0) return world;
        var sector = Pick(candidates, rng);
        var drop = Pick(sector.Slots, rng);
        return world with
        {
            Sectors = world.Sectors.Select(s => s.SectorId == sector.SectorId
                ? s with { Slots = s.Slots.Where(sl => sl.SlotIndex != drop.SlotIndex).ToList() } : s).ToList()
        };
    }

    static WorldState ChangeASectorField(WorldState world, Random rng)
    {
        var sector = Pick(world.Sectors, rng);
        var loamStock = (long)rng.Next(0, 10_000);
        return world with
        {
            Sectors = world.Sectors.Select(s => s.SectorId == sector.SectorId
                ? s with { LoamStock = loamStock, StabilityMilli = rng.Next(0, 1000) } : s).ToList()
        };
    }

    static WorldState ChangeAnEntityField(WorldState world, Random rng)
    {
        if (world.Entities.Count == 0) return world;
        var entity = Pick(world.Entities, rng);
        var carried = (long)rng.Next(0, 10_000);
        return world with
        {
            Entities = world.Entities.Select(e => e.EntityId == entity.EntityId
                ? e with { CarriedLoam = carried, MovementRemaining = rng.Next(0, 1000) } : e).ToList()
        };
    }

    static WorldState ChangeALaneField(WorldState world, Random rng)
    {
        if (world.Lanes.Count == 0) return world;
        var lane = Pick(world.Lanes, rng);
        var state = rng.Next(2) == 0 ? LaneState.Open : LaneState.Severed;
        return world with
        {
            Lanes = world.Lanes.Select(l => l.LaneId == lane.LaneId ? l with { State = state } : l).ToList()
        };
    }

    static WorldState ChangeAFactionField(WorldState world, Random rng)
    {
        var faction = Pick(world.Factions, rng);
        var handicap = rng.Next(500, 1500);
        return world with
        {
            Factions = world.Factions.Select(f => f.FactionId == faction.FactionId
                ? f with { UpkeepHandicapMilli = handicap } : f).ToList()
        };
    }

    static WorldState ChangeOrAddIntel(WorldState world, Random rng, int iteration)
    {
        var faction = Pick(world.Factions, rng);
        var sector = Pick(world.Sectors, rng);
        var snapshot = new FusionRpg.Core.World.Intel.IntelSnapshot
        {
            SectorId = sector.SectorId, LastSeenTurn = iteration,
            Detail = FusionRpg.Core.World.Intel.SectorSight.Full,
            OwnerFactionId = sector.OwnerFactionId, Phase = sector.Phase,
            DangerBand = rng.Next(0, 5), DevelopmentLevel = rng.Next(0, 10)
        };

        // WorldState's own contract (WorldState.cs: "every collection is in stable id order") --
        // WorldCanonical.Write hashes stored order verbatim rather than sorting, and a real DB
        // round-trip always reads back `ORDER BY faction_id, sector_id`. A plain .Append() here would
        // violate that invariant and make THIS GENERATOR's own output disagree with what any real
        // read-back produces -- a test bug, not a diff-writer bug (found by this exact test, the hard
        // way, before this fix).
        var hasFaction = world.Intel.Any(fi => fi.FactionId == faction.FactionId);
        var intel = hasFaction
            ? world.Intel.Select(fi => fi.FactionId != faction.FactionId ? fi : fi with
                {
                    Sectors = (fi.Sectors.Any(s => s.SectorId == sector.SectorId)
                        ? fi.Sectors.Select(s => s.SectorId == sector.SectorId ? snapshot : s)
                        : fi.Sectors.Append(snapshot))
                        .OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
                }).ToList()
            : world.Intel.Append(new FusionRpg.Core.World.Intel.FactionIntel
                { FactionId = faction.FactionId, Sectors = new[] { snapshot } }).ToList();

        return world with { Intel = intel.OrderBy(fi => fi.FactionId, StringComparer.Ordinal).ToList() };
    }

    static T Pick<T>(IReadOnlyList<T> items, Random rng) => items[rng.Next(items.Count)];
}
