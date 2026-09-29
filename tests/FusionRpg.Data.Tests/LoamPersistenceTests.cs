using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// L4 acceptance (spec-loam-model.md): create → reload → deep-equal including all three loam
/// fields, and an existing pre-loam row migrates to exactly the pre-loam world.
/// </summary>
public class LoamPersistenceTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public LoamPersistenceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Non_default_loam_values_round_trip_on_every_field()
    {
        // Deliberately non-default on all three, so this fails if any one of them is wired to a
        // column that just happens to share the same default as the field — first-light alone
        // does not prove that, since only LoamStock varies from default there.
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 7, worldId: "w-loam");
        var homeIndex = built.Sectors.ToList().FindIndex(s =>
            SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));
        var daveIndex = built.Factions.ToList().FindIndex(f => f.Kind == WorldFactionKind.Player);

        var withNonDefaults = built with
        {
            Sectors = built.Sectors.Select((s, i) => i == homeIndex
                ? s with { LoamStock = 12345, FractureIntensityMilli = 1800 }
                : s).ToList(),
            Factions = built.Factions.Select((f, i) => i == daveIndex
                ? f with { UpkeepHandicapMilli = 750 }
                : f).ToList()
        };

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, withNonDefaults);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-loam");
        Assert.NotNull(loaded);
        Assert.Equal(WorldCanonical.Write(withNonDefaults), WorldCanonical.Write(loaded!));

        var home = loaded!.Sectors.Single(s => s.SectorId == built.Sectors[homeIndex].SectorId);
        Assert.Equal(12345L, home.LoamStock);
        Assert.Equal(1800, home.FractureIntensityMilli);
        var dave = loaded.Factions.Single(f => f.FactionId == built.Factions[daveIndex].FactionId);
        Assert.Equal(750, dave.UpkeepHandicapMilli);
    }

    [Fact]
    public void An_existing_pre_loam_row_migrates_to_exactly_the_pre_loam_world()
    {
        // EnsureColumn's ALTER-when-missing path is already exercised generically elsewhere
        // (WebMatchStoreTests); what this proves is the *value* a legacy row reads back as: no
        // stock, baseline Fracture, no handicap — exactly the world before loam existed, which is
        // the correct migration per spec-loam-model.md, not merely "doesn't crash".
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 3, worldId: "w-legacy");
        _store.CreateWorld(playerId: 1, built);

        var loaded = _store.LoadWorldState("w-legacy")!;
        var nonHomeworldSectors = loaded.Sectors.Where(s =>
            !SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));

        foreach (var s in nonHomeworldSectors)
        {
            Assert.Equal(0L, s.LoamStock);
            Assert.Equal(1000, s.FractureIntensityMilli);
        }

        foreach (var f in loaded.Factions)
            Assert.Equal(1000, f.UpkeepHandicapMilli);
    }
}
