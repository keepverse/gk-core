using System.Text.RegularExpressions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Data.Tests.CacheDecay;

/// <summary>Task 0.2 (`cache-decay-void`, deployment-hierarchy module 4) — the clock, amended for
/// durable world-map places (spec <c>docs/architecture/deployment-hierarchy/spec-cache-decay-void.md</c>,
/// V5 amendment): every `place_kind` starts its clock at creation, but `world_sector`/`world_lane`
/// never flip `in_void`. In-memory stores only (<c>DataTestStore.Create()</c>).</summary>
public class CacheDecayTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly ITestOutputHelper _output;

    const ulong WorldSeed = 7;
    const string WorldId = "w-decay";

    public CacheDecayTests(ITestOutputHelper output)
    {
        _output = output;
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
    }

    public void Dispose() => _testStore.Dispose();

    void CreateMapWorld() =>
        Assert.True(_store.CreateWorld(
            _playerId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, WorldSeed, WorldId)).Ok);

    string ResolveWithOwner(string placeKind, string placeRef)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.ResolveOrCreateCacheUnlocked(db, null, placeKind, placeRef, "death", _playerId);
    }

    (int? StartedTurn, string? StartedUtc, int InVoid, long? Owner) ReadClock(string cacheId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT decay_started_turn, decay_started_utc, in_void, owner_player_id
            FROM rpg_corpse_cache WHERE cache_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", cacheId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (
            r.IsDBNull(0) ? null : r.GetInt32(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetInt64(3));
    }

    static void InsertStackItem(RpgStore store, string cacheId, int seq, long qty = 5)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
            VALUES ($c, $s, 'stack', NULL, 'item.cachedecay-test', $q, 'test');
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.ExecuteNonQuery();
    }

    int Tick(string cacheId, int turn)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.TickCorpseCacheDecayForPlayerUnlocked(
            db, null, _playerId, turn, WorldSeed, "2026-09-15T00:00:00+00:00");
    }

    long LogRowCount(string cacheId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_corpse_cache_decay_log WHERE cache_id = $c;";
        cmd.Parameters.AddWithValue("$c", cacheId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>AC1: `TryStartDecayClockUnlocked` branches on `place_kind` — lawn/siege/delve_room
    /// enter the void immediately; world_sector/world_lane start the same clock but stay
    /// field-reachable.</summary>
    [Theory]
    [InlineData("lawn", 1)]
    [InlineData("siege", 1)]
    [InlineData("delve_room", 1)]
    [InlineData("world_sector", 0)]
    [InlineData("world_lane", 0)]
    public void Clock_starts_per_place_kind_branch(string placeKind, int expectedInVoid)
    {
        CreateMapWorld();

        var cacheId = ResolveWithOwner(placeKind, "ref-" + placeKind);

        var (startedTurn, startedUtc, inVoid, owner) = ReadClock(cacheId);
        Assert.Equal(0, startedTurn); // the map world's current_turn at creation
        Assert.False(string.IsNullOrWhiteSpace(startedUtc));
        Assert.Equal(expectedInVoid, inVoid);
        Assert.Equal(_playerId, owner);
    }

    /// <summary>AC2: a `world_sector`/`world_lane` cache never reaches `in_void=1`, even ticked
    /// far past the ~112-turn scale that empties a lawn cache — while the clock provably ran
    /// (one log row per tick).</summary>
    [Theory]
    [InlineData("world_sector")]
    [InlineData("world_lane")]
    public void World_caches_never_enter_void_far_past_lawn_timeout(string placeKind)
    {
        CreateMapWorld();
        var cacheId = ResolveWithOwner(placeKind, "ref-far-" + placeKind);

        // An emptied cache is skipped by the tick (spec §Design 1), so a fresh stack is dealt
        // whenever the previous one died — the clock itself must run all 400 turns regardless.
        const int ticks = 400;
        var nextSeq = 0;
        for (var turn = 1; turn <= ticks; turn++)
        {
            if (_store.ListCorpseCacheItems(cacheId).Count == 0)
                InsertStackItem(_store, cacheId, nextSeq++);
            Tick(cacheId, turn);
        }

        var (_, _, inVoid, _) = ReadClock(cacheId);
        Assert.Equal(0, inVoid);
        Assert.Equal(ticks, LogRowCount(cacheId));
    }

    /// <summary>No re-stamp on reuse: a second death joining an already-decaying cache inherits
    /// whatever is left of the existing window.</summary>
    [Fact]
    public void Second_death_into_a_decaying_cache_does_not_restamp_the_clock()
    {
        CreateMapWorld();
        var cacheId = ResolveWithOwner("lawn", "ref-restamp");
        Assert.Equal(0, ReadClock(cacheId).StartedTurn);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_worlds SET current_turn = 5 WHERE world_id = $w;";
            cmd.Parameters.AddWithValue("$w", WorldId);
            cmd.ExecuteNonQuery();
        }

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            var again = _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "ref-restamp", "death", _playerId);
            Assert.Equal(cacheId, again);
        }

        Assert.Equal(0, ReadClock(cacheId).StartedTurn);
    }

    /// <summary>Idempotent replay: ticking the identical `(ownerPlayerId, newTurn)` twice leaves
    /// byte-identical item state and exactly one log row.</summary>
    [Fact]
    public void Replaying_one_tick_changes_nothing_the_second_time()
    {
        CreateMapWorld();
        var cacheId = ResolveWithOwner("lawn", "ref-replay");
        InsertStackItem(_store, cacheId, 0);
        InsertStackItem(_store, cacheId, 1);

        Assert.Equal(1, Tick(cacheId, 1));
        var afterFirst = _store.ListCorpseCacheItems(cacheId);
        Assert.Equal(0, Tick(cacheId, 1)); // already processed: zero caches touched
        var afterSecond = _store.ListCorpseCacheItems(cacheId);

        Assert.Equal(afterFirst.Count, afterSecond.Count);
        Assert.Equal(
            afterFirst.Select(i => (i.Seq, i.Kind, i.ContainerId, i.Qty)).ToList(),
            afterSecond.Select(i => (i.Seq, i.Kind, i.ContainerId, i.Qty)).ToList());
        Assert.Equal(1, LogRowCount(cacheId));
    }

    /// <summary>A cache with no clock set (owner-unknown creation) decays across zero world turns,
    /// no matter how many pass.</summary>
    [Fact]
    public void A_clockless_cache_decays_zero_turns()
    {
        CreateMapWorld();
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
            _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "ref-clockless", "death");
        var cacheId = Assert.Single(_store.ListCorpseCaches()).CacheId;
        InsertStackItem(_store, cacheId, 0);

        for (var turn = 1; turn <= 10; turn++)
            Assert.Equal(0, Tick(cacheId, turn));

        Assert.Single(_store.ListCorpseCacheItems(cacheId));
        Assert.Equal(0, LogRowCount(cacheId));
        Assert.Null(ReadClock(cacheId).StartedTurn);
    }

    /// <summary>Without a MAP-kind world there is no turn clock to read: the cache waits clockless
    /// (module 3's pre-decay shape) rather than stamping a guess — this is also what keeps Task
    /// 0.1's landed death-path tests green.</summary>
    [Fact]
    public void Clock_start_without_a_map_world_leaves_the_cache_clockless()
    {
        var cacheId = ResolveWithOwner("lawn", "ref-no-world");

        var (startedTurn, startedUtc, inVoid, owner) = ReadClock(cacheId);
        Assert.Null(startedTurn);
        Assert.Null(startedUtc);
        Assert.Equal(0, inVoid);
        Assert.Null(owner);
    }

    /// <summary>The `CommitWorldTurn` hook (§Design 3): resolving a real turn writes the decay log
    /// for the newly-committed turn in the same commit.</summary>
    [Fact]
    public void CommitWorldTurn_ticks_decay_before_its_own_commit()
    {
        CreateMapWorld();
        var cacheId = ResolveWithOwner("lawn", "ref-hook");
        InsertStackItem(_store, cacheId, 0);

        var last = CommitAll();
        Assert.True(last.Advanced);

        Assert.Equal(1, LogRowCount(cacheId));
    }

    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    WorldTurnCommitResult CommitAll()
    {
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            last = _store.CommitWorldTurn(WorldId, c, open);
        return last;
    }

    static readonly PowerTuning GearTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    void SeedCatalog()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.cachedecay-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.cachedecay-test", Variant = "", Tier = 1, Name = "Cache Decay Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.cachedecay-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.cachedecay-test.t1") },
        }).IsOk);
    }

    string SeedOwnedInstance()
    {
        var container = _store.GetContainer("item.cachedecay-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, GearTuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        var instanceId = _store.SaveInstance(inst!);
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = _playerId.ToString(),
            AcquiredUtc = "2026-09-15T00:00:00+00:00",
        });
        return instanceId;
    }

    static void InsertInstanceItem(RpgStore store, string cacheId, int seq, string instanceId, string owner)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
            VALUES ($c, $s, 'instance', $inst, NULL, NULL, $owner);
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$inst", instanceId);
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A destroyed `instance` row cascades through the inlined `effect_instance`/
    /// `rpg_item` deletes and leaves its cache row — a stack dies as one unit, never per-unit.</summary>
    [Fact]
    public void Destroyed_instance_cascades_and_stack_dies_as_one_unit()
    {
        CreateMapWorld();
        SeedCatalog();
        var cacheId = ResolveWithOwner("lawn", "ref-destroy");
        var instances = new List<string>();
        for (var s = 0; s < 8; s++)
        {
            var id = SeedOwnedInstance();
            instances.Add(id);
            InsertInstanceItem(_store, cacheId, s, id, "test");
        }
        InsertStackItem(_store, cacheId, 8, qty: 7);

        string? destroyed = null;
        var stackGone = false;
        for (var turn = 1; turn <= 2000 && (destroyed is null || !stackGone); turn++)
        {
            Tick(cacheId, turn);
            if (destroyed is null)
                destroyed = instances.FirstOrDefault(id => _store.GetItem(id) is null);
            stackGone = _store.ListCorpseCacheItems(cacheId).All(i => i.Seq != 8);
        }

        Assert.True(destroyed is not null,
            "no instance destroyed in 2000 ticks at 994/1000 survival — the tick never rolls");
        Assert.True(stackGone, "the stack row never lost its coin flip in 2000 ticks");
        Assert.Null(_store.GetItem(destroyed));
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM effect_instance WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", destroyed);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        Assert.DoesNotContain(_store.ListCorpseCacheItems(cacheId), i => i.InstanceId == destroyed);
        // The header itself is never deleted, even once items are gone.
        Assert.Contains(_store.ListCorpseCaches(), c => c.CacheId == cacheId);
    }

    // The spec's regression band for the median turn-to-empty sweep. An ACCEPTANCE WINDOW, so it lives
    // with the test that accepts, not in production code (it was an RpgStore constant until
    // 2026-09-18). It brackets the shipped curve's ~112-turn median ((994/1000)^112 ~ 0.50).
    const int DecayMedianBandMin = 95;
    const int DecayMedianBandMax = 130;

    /// <summary>The D26-shaped regression: a fixed-seed sweep's observed median turn-to-empty lands
    /// inside the spec band — computed from the shipped tunables, never asserted as "112".</summary>
    [Fact]
    public void Sweep_median_empty_turn_lands_inside_spec_band()
    {
        const int caches = 64;
        var empties = new List<int>(caches);
        for (var i = 0; i < caches; i++)
        {
            var seed = 1000UL + (ulong)i;
            var turn = 0;
            while (true)
            {
                turn++;
                var stream = SeededRng.DeriveStream(seed, $"corpse-decay:sweep:{i}:{turn}:0");
                if (!(stream.NextPerMille() < FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningHub.Tuning.CacheDecay.BaseMilli)) break;
                Assert.True(turn < 100_000, "a sweep item survived 100k ticks — the roll never fails");
            }
            empties.Add(turn);
        }
        empties.Sort();
        var median = (empties[caches / 2 - 1] + empties[caches / 2]) / 2.0;
        _output.WriteLine($"median empty turn: {median} (band [{DecayMedianBandMin},{DecayMedianBandMax}])");
        Assert.True(median >= DecayMedianBandMin && median <= DecayMedianBandMax,
            $"median empty turn {median} outside band [{DecayMedianBandMin},{DecayMedianBandMax}]");
    }

    /// <summary>The no-clock guard: no wall-clock read and no framework RNG in the decay module —
    /// comment lines skipped, mirroring `guard-dal.ps1` semantics.</summary>
    [Fact]
    public void CacheDecay_module_never_reads_wall_clock()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Data", "Sqlite", "RpgStore.CacheDecay.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var lines = File.ReadAllLines(
            Path.Combine(dir.FullName, "src", "FusionRpg.Data", "Sqlite", "RpgStore.CacheDecay.cs"));
        var code = string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var hits = Regex.Matches(code,
            @"DateTime\.(UtcNow|Now)|DateTimeOffset\.(UtcNow|Now)|Environment\.TickCount|System\.Random");
        Assert.Empty(hits);
    }
}
