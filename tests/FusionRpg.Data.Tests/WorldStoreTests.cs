using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// W3 (spec-world-model.md §Data): the seven world tables, creation in one transaction, and a
/// round-trip that must be byte-identical to the in-memory build — the store may persist the world,
/// never reinterpret it.
/// </summary>
public class WorldStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Create_then_load_round_trips_byte_identically()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: "w-1");

        var (ok, reason, created) = _store.CreateWorld(playerId: 1, built);
        Assert.True(ok, reason);
        Assert.NotNull(created);

        var loaded = _store.LoadWorldState("w-1");
        Assert.NotNull(loaded);
        Assert.Equal(WorldCanonical.Write(built), WorldCanonical.Write(loaded!));
    }

    /// <summary>
    /// W10 added a column, and a column that only exists in `CREATE TABLE` never reaches a database
    /// that already had a world in it. Round-tripping the flag is the cheapest proof that both the
    /// create path and the migration path carry it.
    /// </summary>
    [Fact]
    public void A_routed_force_stays_routed_across_a_save()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 7, worldId: "w-routed");
        var beaten = built with
        {
            Entities = built.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { Routed = true } : e)
                .ToList()
        };

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, beaten);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-routed");
        Assert.NotNull(loaded);
        Assert.True(loaded!.Entities.Single(e => e.EntityId == "e-dave-legion-1").Routed);
        Assert.Equal(WorldCanonical.Write(beaten), WorldCanonical.Write(loaded));
    }

    /// <summary>
    /// W19: belief is the one stored thing that is not derivable from the rest of the world, so a
    /// lossy round trip here would be invisible everywhere else — the world would reload looking
    /// perfectly valid, just with a faction quietly knowing less than it did.
    /// </summary>
    [Fact]
    public void What_a_faction_believes_survives_a_save()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 3, worldId: "w-intel");

        // The template seeds Dave's opening belief; nobody else has looked at anything.
        var dave = built.Intel.Single(i => i.FactionId == "dave");
        Assert.NotEmpty(dave.Sectors);
        // Zomboss believes what *he* can see, which since 2026-08-22 is his own fortress and its
        // neighbour — he has a warband on the map now. What matters here is that the two
        // factions do not share a belief, not that one of them has none.
        var zomboss = built.Intel.Single(i => i.FactionId == "zomboss");
        Assert.NotEmpty(zomboss.Sectors);
        Assert.NotEqual(
            dave.Sectors.Select(x => x.SectorId).OrderBy(x => x, StringComparer.Ordinal),
            zomboss.Sectors.Select(x => x.SectorId).OrderBy(x => x, StringComparer.Ordinal));

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, built);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-intel");
        Assert.NotNull(loaded);
        Assert.Equal(WorldCanonical.Write(built), WorldCanonical.Write(loaded!));

        // And the detail survives, not just the shape: a surveyed sector keeps its slots and an
        // exact head count, a glimpsed one keeps neither.
        var home = loaded!.Intel.Single(i => i.FactionId == "dave").Of("homeworld")!;
        Assert.NotEmpty(home.Slots);
        Assert.All(home.Forces, f => Assert.True(f.Exact));

        var rumoured = loaded.Intel.Single(i => i.FactionId == "dave").Of("ash-waste")!;
        Assert.Empty(rumoured.Slots);
        Assert.All(rumoured.Forces, f => Assert.False(f.Exact));
        Assert.All(rumoured.Forces, f => Assert.True(f.BandIndex > 0));
    }

    [Fact]
    public void Load_is_stably_ordered_on_every_read()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 5, worldId: "w-order");
        _store.CreateWorld(1, built);

        var first = _store.LoadWorldState("w-order")!;
        var second = _store.LoadWorldState("w-order")!;

        Assert.Equal(WorldCanonical.Write(first), WorldCanonical.Write(second));
        WorldValidation.Validate(first); // ordering is a validation rule, so a bad read throws here
    }

    [Fact]
    public void A_malformed_world_is_refused_and_writes_nothing()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1, worldId: "w-bad");

        // An entity standing nowhere — rule 7.
        var broken = built with
        {
            Entities = built.Entities.Select(e => e with { AtSectorId = null, OnLaneId = null }).ToList()
        };

        var (ok, reason, _) = _store.CreateWorld(1, broken);
        Assert.False(ok);
        Assert.Contains("invalid", reason, StringComparison.OrdinalIgnoreCase);

        // Nothing reached any of the seven tables: no header, no graph, not even a listing.
        Assert.Null(_store.LoadWorldState("w-bad"));
        Assert.Null(_store.GetActiveWorld(1));
    }

    [Fact]
    public void Two_worlds_do_not_bleed_into_each_other()
    {
        var a = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1, worldId: "w-a");
        var b = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 2, worldId: "w-b");
        Assert.True(_store.CreateWorld(1, a).Ok);
        Assert.True(_store.CreateWorld(1, b).Ok);

        var loadedA = _store.LoadWorldState("w-a")!;
        var loadedB = _store.LoadWorldState("w-b")!;

        // Same authored map, different seeds — every child table is scoped by world_id, so the
        // graphs must be identical apart from the header.
        Assert.Equal(1UL, loadedA.Seed);
        Assert.Equal(2UL, loadedB.Seed);
        Assert.Equal(loadedA.Sectors.Count, loadedB.Sectors.Count);
        Assert.Equal(loadedA.Entities.Sum(e => e.Members.Count), loadedB.Entities.Sum(e => e.Members.Count));
    }

    [Fact]
    public void Creating_the_same_world_id_twice_is_refused()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1, worldId: "w-dupe");
        Assert.True(_store.CreateWorld(1, built).Ok);

        var (ok, reason, _) = _store.CreateWorld(1, built);
        Assert.False(ok);
        Assert.Contains("exists", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_player_is_refused()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1, worldId: "w-nobody");
        var (ok, reason, _) = _store.CreateWorld(playerId: 4242, built);
        Assert.False(ok);
        Assert.Contains("player", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loading_an_unknown_world_returns_null()
    {
        Assert.Null(_store.LoadWorldState("no-such-world"));
    }

    [Fact]
    public void The_active_world_is_findable_by_player()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 3, worldId: "w-active");
        _store.CreateWorld(1, built);

        var active = _store.GetActiveWorld(1);
        Assert.NotNull(active);
        Assert.Equal("w-active", active!.WorldId);
        Assert.Equal(WorldTemplateCatalog.FirstLightId, active.TemplateId);
        Assert.Equal(3UL, active.Seed);
        Assert.Equal(0, active.CurrentTurn);
    }

    [Fact]
    public void Slot_guard_state_and_nullable_climate_survive_the_round_trip()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: "w-detail");
        _store.CreateWorld(1, built);
        var loaded = _store.LoadWorldState("w-detail")!;

        // The homeworld is the one sector with no climate — a null that must not become a default.
        var home = loaded.Sectors.Single(s => s.SectorId == "homeworld");
        Assert.Null(home.Climate);

        var guarded = loaded.Sectors.SelectMany(s => s.Slots)
            .Where(sl => sl.GuardState == GuardState.Intact)
            .ToList();
        Assert.NotEmpty(guarded);
        Assert.All(guarded, sl => Assert.False(string.IsNullOrEmpty(sl.GuardWaveId)));
    }

    [Fact]
    public void Entity_members_survive_with_their_order_and_nulls()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 2, worldId: "w-members");
        _store.CreateWorld(1, built);
        var loaded = _store.LoadWorldState("w-members")!;

        var legion = loaded.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var source = built.Entities.Single(e => e.EntityId == "e-dave-legion-1");

        Assert.Equal(source.Members.Select(m => m.SpeciesId), legion.Members.Select(m => m.SpeciesId));
        Assert.All(legion.Members, m => Assert.Null(m.InstanceId)); // template members are unbound
    }

    /// <summary>world-map W45 acceptance: a stocked, mid-project sector round-trips byte-identically.</summary>
    [Fact]
    public void A_stocked_mid_project_sector_survives_the_round_trip()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 5, worldId: "w-recruit");
        var developing = built with
        {
            Sectors = built.Sectors
                .Select(s => s.SectorId == "homeworld"
                    ? s with { RecruitStock = 340, ProjectId = "placeholder-project", ProjectTurnsRemaining = 2 }
                    : s)
                .ToList()
        };

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, developing);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-recruit");
        Assert.NotNull(loaded);
        var home = loaded!.Sectors.Single(s => s.SectorId == "homeworld");
        Assert.Equal(340, home.RecruitStock);
        Assert.Equal("placeholder-project", home.ProjectId);
        Assert.Equal(2, home.ProjectTurnsRemaining);
        Assert.Equal(WorldCanonical.Write(developing), WorldCanonical.Write(loaded));
    }

    /// <summary>
    /// A column that only exists in `CREATE TABLE` never reaches a database that already had a
    /// world in it (the same trap `A_routed_force_stays_routed_across_a_save` above proves for
    /// `routed`) — an existing saved world must read the three new columns back as zero/null, not
    /// throw or silently omit them.
    /// </summary>
    [Fact]
    public void An_existing_world_created_before_this_column_reads_it_back_as_zero_null()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 3, worldId: "w-pre-existing");
        _store.CreateWorld(1, built);

        var loaded = _store.LoadWorldState("w-pre-existing")!;
        Assert.All(loaded.Sectors, s =>
        {
            Assert.Equal(0, s.RecruitStock);
            Assert.Null(s.ProjectId);
            Assert.Null(s.ProjectTurnsRemaining);
        });
    }
}
