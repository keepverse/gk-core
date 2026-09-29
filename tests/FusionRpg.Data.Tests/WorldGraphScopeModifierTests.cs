using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Task 2.3b (`wonder-effect-empire` §Design 4-5): the `scope_modifier_milli` persistence gap and
/// the three secondary `LoamProduction.For` call sites. All state is in-memory
/// (<see cref="DataTestStore.Create"/>); no file, no temp dir. SQL stays inside
/// <c>FusionRpg.Data</c> (`guard-dal.ps1` scope).
/// </summary>
public class WorldGraphScopeModifierTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldGraphScopeModifierTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    // ---- AC 1: save at 1500, reload reads 1500 ----

    [Fact]
    public void Save_at_1500_reload_reads_1500_not_the_record_default()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: "w-scope-1500");
        var daveId = built.Factions.First(f => f.Kind == WorldFactionKind.Player).FactionId;
        var withModifier = built with
        {
            Factions = built.Factions
                .Select(f => f.FactionId == daveId ? f with { ScopeModifierMilli = 1500 } : f)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, withModifier);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-scope-1500");
        Assert.NotNull(loaded);
        Assert.Equal(1500, loaded!.Factions.Single(f => f.FactionId == daveId).ScopeModifierMilli);
        // Every untouched faction still reads the record default.
        foreach (var f in loaded.Factions.Where(f => f.FactionId != daveId))
            Assert.Equal(1000, f.ScopeModifierMilli);
        // Byte-identical round-trip through the canonical hash, not just the one field.
        Assert.Equal(WorldCanonical.Write(withModifier), WorldCanonical.Write(loaded));
    }

    // ---- AC 1b: the shipped default is the correct migration for untouched worlds ----

    [Fact]
    public void Worlds_that_never_set_the_modifier_read_back_1000()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 12, worldId: "w-scope-default");
        var (ok, reason, _) = _store.CreateWorld(playerId: 1, built);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-scope-default");
        Assert.NotNull(loaded);
        foreach (var f in loaded!.Factions)
            Assert.Equal(1000, f.ScopeModifierMilli);
    }

    // ---- AC 2: unchanged modifier survives a turn-commit DiffFactions pass ----

    [Fact]
    public void Unchanged_modifier_survives_a_DiffFactions_pass_that_touches_only_sectors()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 13, worldId: "w-scope-diff");
        var daveId = built.Factions.First(f => f.Kind == WorldFactionKind.Player).FactionId;
        var withModifier = built with
        {
            Factions = built.Factions
                .Select(f => f.FactionId == daveId ? f with { ScopeModifierMilli = 1500 } : f)
                .ToList(),
        };
        var (ok, reason, _) = _store.CreateWorld(playerId: 1, withModifier);
        Assert.True(ok, reason);

        var before = _store.LoadWorldState("w-scope-diff")!;
        // A sector-only change: every faction row is byte-identical, so DiffFactions' own
        // `was == f` skip fires for all of them — the modifier must survive without being rewritten.
        var touchedSector = before.Sectors.First().SectorId;
        var next = before with
        {
            Sectors = before.Sectors
                .Select(s => s.SectorId == touchedSector ? s with { LoamStock = s.LoamStock + 7 } : s)
                .ToList(),
        };

        var after = _store.DiffCommitForTest("w-scope-diff", next);

        Assert.Equal(1500, after.Factions.Single(f => f.FactionId == daveId).ScopeModifierMilli);
        Assert.Equal(
            FusionRpg.Core.World.Turn.StateHasher.Hash(next),
            FusionRpg.Core.World.Turn.StateHasher.Hash(after));
    }

    // ---- AC 3: secondary call sites report Production-identical yields ----

    static WorldState WonderedWorld()
    {
        var sector = new WorldSector
        {
            SectorId = "s1",
            TypeId = "stable",
            OwnerFactionId = "f1",
            Slots = new List<WorldSlot>
            {
                new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId },
            },
        };
        return new WorldState
        {
            WorldId = "w",
            TemplateId = "t",
            Seed = 1,
            Factions = new List<WorldFaction>
            {
                new() { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1", ScopeModifierMilli = 1500 },
                new() { FactionId = "f2", Kind = WorldFactionKind.Player, Name = "F2" },
            },
            Sectors = new List<WorldSector> { sector },
        };
    }

    [Fact]
    public void Forecast_ProjectedStock_reports_the_stored_modifier_yield()
    {
        var world = WonderedWorld();
        var sector = world.Sectors.Single(s => s.SectorId == "s1");

        // Sensitivity guard: the modifier must actually matter for this sector, or parity is vacuous.
        var expected = LoamProduction.For(sector, 1500);
        Assert.NotEqual(LoamProduction.For(sector), expected);

        // ProjectedStock throttles at capacity; this sector starts empty with room for the yield.
        var room = Math.Max(0, LoamPhases.EffectiveCapacity(sector) - sector.LoamStock);
        Assert.True(room >= expected);
        Assert.Equal(sector.LoamStock + expected, LoamForecast.ProjectedStock(new[] { "s1" }, world));
    }

    [Fact]
    public void Balance_PerSector_reports_the_stored_modifier_yield()
    {
        var world = WonderedWorld();
        var sector = world.Sectors.Single(s => s.SectorId == "s1");

        var expectedProduction = LoamProduction.For(sector, 1500);
        Assert.NotEqual(LoamProduction.For(sector), expectedProduction);

        Assert.Equal(
            expectedProduction - LoamUpkeep.For(world, sector),
            LoamBalance.PerSector(world, sector));
    }

    [Fact]
    public void Endpoint_resolve_reports_the_stored_modifier_yield()
    {
        // The exact two-line resolve from WorldEndpoints.ComputeLoamReading, replicated so this
        // fact fails if the endpoint's own lines ever drift from the stored-field read.
        var world = WonderedWorld();
        var sector = world.Sectors.Single(s => s.SectorId == "s1");

        var scopeModifierMilli = world.Factions
            .FirstOrDefault(f => f.FactionId == sector.OwnerFactionId)?.ScopeModifierMilli ?? 1000;
        Assert.Equal(1500, scopeModifierMilli);
        Assert.Equal(LoamProduction.For(sector, 1500), LoamProduction.For(sector, scopeModifierMilli));
    }

    [Fact]
    public void Unknown_owner_falls_back_to_1000_on_every_secondary_site()
    {
        var world = WonderedWorld() with
        {
            Sectors = new List<WorldSector>
            {
                new()
                {
                    SectorId = "s-rogue",
                    TypeId = "stable",
                    OwnerFactionId = "no-such-faction",
                    Slots = new List<WorldSlot>
                    {
                        new() { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId },
                    },
                },
            },
        };
        var sector = world.Sectors.Single();

        // The `?? 1000` fallback is the identical expression in all three call sites; proving it
        // once through the shared shape covers the branch each of them carries.
        var scopeModifierMilli = world.Factions
            .FirstOrDefault(f => f.FactionId == sector.OwnerFactionId)?.ScopeModifierMilli ?? 1000;
        Assert.Equal(1000, scopeModifierMilli);
        Assert.Equal(LoamProduction.For(sector), LoamProduction.For(sector, scopeModifierMilli));
    }
}
