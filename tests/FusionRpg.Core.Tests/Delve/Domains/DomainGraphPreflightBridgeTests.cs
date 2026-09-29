using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.17 row 4's own production wiring (party-dungeon-todo.md, 2026-09-07) — proves
/// <see cref="DomainGraphPreflight.Build"/> really closes row 4 through the actual
/// <see cref="DomainPreflight.Run"/> entry point, not just through a direct
/// <see cref="DelveGraphRoll.Roll"/> call (that proof already exists,
/// <c>RoomPaletteSeedFileTests.Every_real_shipped_domain_rolls_a_real_valid_graph_on_its_own_layout_and_climate</c>).
/// Rows 1/2/3/9/10 are stubbed to always pass — they are each independently proven in
/// <see cref="DomainPreflightTests"/>'s own per-row fixtures; this file's only job is row 4.
/// </summary>
public class DomainGraphPreflightBridgeTests
{
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

    static LayoutTemplateCatalog RealLayoutCatalog()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        var bandDefs = new Dictionary<string, BandDef>
        {
            ["depthBand"] = new BandDef { BandName = "depthBand", Members = Tuning.DepthBandRows.Keys.ToList() },
            ["widthBand"] = new BandDef { BandName = "widthBand", Members = Tuning.WidthBandCols.Keys.ToList() },
            ["branchiness"] = new BandDef { BandName = "branchiness", Members = Tuning.BranchinessPathWalks.Keys.ToList() },
            ["density"] = new BandDef
            {
                BandName = "density",
                Members = Tuning.GateDensityPerRoomMilli.Keys
                    .Union(Tuning.SecretDensityPerRoomMilli.Keys).Union(Tuning.OneWayDensityPerRoomMilli.Keys).ToList(),
            },
        };
        var load = LayoutTemplateCatalog.Load(rows, bandDefs, Tuning.RaidModes.Keys.ToList());
        Assert.Empty(load.Rejections);
        return load.Catalog;
    }

    // Rows 1/2/3/9/10 stubbed to trivially pass for whatever domain is handed in -- this fixture
    // isolates row 4 (CheckGraphs) exactly the way DomainPreflightTests.Passing() isolates each of
    // its own rows.
    static DomainPreflightInputs InputsIsolatingRow4(
        IReadOnlyDictionary<string, int> dangerBandOrdinals, IReadOnlyCollection<string> knownLayoutIds,
        IReadOnlyCollection<string> knownSpeciesIds, Func<DomainRow, IReadOnlyList<DomainRefusal>> checkGraphs) => new(
        DangerBandOrdinals: dangerBandOrdinals,
        KnownLayoutIds: knownLayoutIds,
        KnownSpeciesIds: knownSpeciesIds,
        ThreatBandOrdinalFor: _ => 99,
        BossFloorRungOrdinal: 0,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: checkGraphs,
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: Array.Empty<string>(),
        BoundLootKinds: Array.Empty<string>(),
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
        OfferedRungCountFor: _ => 1);

    [Fact]
    public void Build_null_arguments_throw()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();
        Assert.Throws<ArgumentNullException>(() => DomainGraphPreflight.Build(null!, palettes, layouts, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainGraphPreflight.Build(rooms, null!, layouts, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainGraphPreflight.Build(rooms, palettes, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainGraphPreflight.Build(rooms, palettes, layouts, null!));
    }

    /// <summary>The real end-to-end proof: all six real shipped domains, through the actual
    /// `DomainPreflight.Run` entry point (not a direct `Roll` call), now that the wild-room gap that
    /// blocked every real domain is closed. This is what makes the "wiring deferred until wild rooms
    /// exist" note in the todo file obsolete.</summary>
    [Fact]
    public void Every_real_shipped_domain_passes_row4_through_the_real_DomainPreflight_Run_entry_point()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();

        Assert.Equal(6, domains.Count);
        var checkGraphs = DomainGraphPreflight.Build(rooms, palettes, layouts, Tuning);
        var inputs = InputsIsolatingRow4(
            dangerBandOrdinals: domains.Select(d => d.DangerBand).Distinct(StringComparer.Ordinal)
                .ToDictionary(b => b, _ => 2, StringComparer.Ordinal),
            knownLayoutIds: domains.Select(d => d.LayoutTemplateId).ToHashSet(StringComparer.Ordinal),
            knownSpeciesIds: domains.Select(d => d.BossSpeciesRef).ToHashSet(StringComparer.Ordinal),
            checkGraphs: checkGraphs);

        var refusals = DomainPreflight.Run(domains, inputs);

        Assert.Empty(refusals);
    }

    /// <summary>The negative control: a palette that excludes every `wild`-kind room (the pre-fix
    /// shipped state, D1.10's own residual before this session's wild-room batch) DOES refuse through
    /// the real entry point, naming `domain.graph:*` -- proving the bridge detects a real failure, not
    /// just a real success.</summary>
    [Fact]
    public void A_domain_whose_palette_excludes_every_wild_room_is_refused_domain_graph_through_the_real_entry_point()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var allDomains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var domain = allDomains.Single(d => d.DomainId == "domain.fire-001");
        var paletteWithoutWild = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir())[domain.DomainId]
            .Where(id => !rooms[id].Kind.Equals("wild", StringComparison.Ordinal)).ToList();
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = paletteWithoutWild };
        var layouts = RealLayoutCatalog();

        var checkGraphs = DomainGraphPreflight.Build(rooms, palettes, layouts, Tuning);
        var inputs = InputsIsolatingRow4(
            dangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { [domain.DangerBand] = 2 },
            knownLayoutIds: new[] { domain.LayoutTemplateId },
            knownSpeciesIds: new[] { domain.BossSpeciesRef },
            checkGraphs: checkGraphs);

        var refusals = DomainPreflight.Run(new[] { domain }, inputs);

        Assert.Single(refusals);
        Assert.StartsWith("domain.graph:", refusals[0].Rule);
        Assert.Contains("wild", refusals[0].Detail);
    }

    /// <summary>Spec-domain-catalog.md's own testing strategy: "import is byte-identical on rerun."
    /// Two independent `Build` calls (as two separate server-process imports would produce) must reach
    /// the IDENTICAL verdict for the same domain -- the reason this bridge derives seeds through
    /// `SeededRng.DeriveStream` rather than `string.GetHashCode()` (process-randomized in .NET Core).</summary>
    [Fact]
    public void The_verdict_is_identical_across_two_independent_Build_calls_never_process_dependent()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();
        var domain = domains.Single(d => d.DomainId == "domain.fire-001");

        var first = DomainGraphPreflight.Build(rooms, palettes, layouts, Tuning)(domain);
        var second = DomainGraphPreflight.Build(rooms, palettes, layouts, Tuning)(domain);

        Assert.Equal(first, second);
    }
}
