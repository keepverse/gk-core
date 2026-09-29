using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Roll;

/// <summary>
/// D4.17 row 4's own real bridging gap (party-dungeon-todo.md, 2026-09-07): proves
/// <see cref="RoomPaletteSeedFile"/>/<see cref="DomainAnchorBuilder"/>/<see cref="DomainSeedFile"/>
/// really do turn the REAL six shipped domains, the REAL 92 shipped rooms and the REAL six shipped
/// layouts into a <see cref="DomainAnchor"/>/<see cref="LayoutTemplate"/> pair
/// <see cref="DelveGraphRoll.Roll"/> accepts — the end-to-end proof this bridge exists for, not just
/// a type-shape unit test.
/// </summary>
public class RoomPaletteSeedFileTests
{
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

    [Fact]
    public void LoadAll_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RoomPaletteSeedFile.LoadAll(null!));
    }

    [Fact]
    public void LoadAll_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(RoomPaletteSeedFile.LoadAll(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    [Fact]
    public void LoadAll_reads_all_real_shipped_rooms_keyed_by_id()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        // The room count is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19).
        Assert.NotEmpty(rooms);
        Assert.True(rooms.ContainsKey("room.fight-fire-001"));
        Assert.True(rooms.ContainsKey("room.wild-fire-001"));
    }

    [Fact]
    public void LoadAll_resolves_a_real_climate_bearing_room_to_its_real_element()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var entry = rooms["room.fight-fire-001"];
        Assert.Equal("room.fight-fire-001", entry.RoomId);
        Assert.Equal("fight", entry.Kind);
        Assert.Equal(ElementTypeId.Fire, entry.Climate);
    }

    [Fact]
    public void LoadAll_resolves_a_real_climate_neutral_room_to_null()
    {
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var entry = rooms["room.boss-none-001"];
        Assert.Null(entry.Climate);
    }

    [Fact]
    public void DomainAnchorBuilder_From_null_arguments_throw()
    {
        var domain = new DomainRow("d", "n", "f", "t", "fire", "shallow", "many", "l", "b", null, "Lair", null);
        var rooms = new Dictionary<string, RoomPaletteEntry>();
        Assert.Throws<ArgumentNullException>(() => DomainAnchorBuilder.From(null!, rooms, new List<string>()));
        Assert.Throws<ArgumentNullException>(() => DomainAnchorBuilder.From(domain, null!, new List<string>()));
        Assert.Throws<ArgumentNullException>(() => DomainAnchorBuilder.From(domain, rooms, null!));
    }

    [Fact]
    public void DomainAnchorBuilder_From_an_unknown_room_id_throws_naming_it()
    {
        var domain = new DomainRow("domain.x", "n", "f", "t", "fire", "shallow", "many", "l", "b", null, "Lair", null);
        var rooms = new Dictionary<string, RoomPaletteEntry>();
        var ex = Assert.Throws<KeyNotFoundException>(() => DomainAnchorBuilder.From(domain, rooms, new List<string> { "room.nope" }));
        Assert.Contains("domain.x", ex.Message);
        Assert.Contains("room.nope", ex.Message);
    }

    [Fact]
    public void DomainAnchorBuilder_From_an_unknown_climate_throws()
    {
        var domain = new DomainRow("domain.x", "n", "f", "t", "not-a-climate", "shallow", "many", "l", "b", null, "Lair", null);
        Assert.Throws<NotSupportedException>(() => DomainAnchorBuilder.From(domain, new Dictionary<string, RoomPaletteEntry>(), new List<string>()));
    }

    [Fact]
    public void DomainSeedFile_LoadAll_reads_all_six_real_shipped_domains()
    {
        var rows = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        Assert.Equal(6, rows.Count);
        Assert.Contains(rows, r => r.DomainId == "domain.fire-001" && r.Climate == "fire" && r.DangerBand == "shallow" && r.Entry == "many");
    }

    [Fact]
    public void DomainSeedFile_LoadRoomPalettes_reads_all_six_real_shipped_room_palettes()
    {
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        Assert.Equal(6, palettes.Count);
        // The per-domain room count is a growing content population, never pinned (population-pin
        // SE3.3, 2026-09-19).
        Assert.NotEmpty(palettes["domain.fire-001"]);
    }

    // ---- the end-to-end proof: the real bridge feeds the real Roll() ----------------------------

    static LayoutTemplateCatalog RealLayoutCatalog()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        // DepthBandRows/WidthBandCols/BranchinessPathWalks/*DensityPerRoomMilli are the REAL tuning
        // dictionaries Roll() itself reads (DelveGraphRoll.cs:44-57) -- reusing their own key sets as
        // the "known band members" the catalog validates against keeps this test honest against
        // whatever the real tuning file actually ships, never a hand-typed duplicate list.
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
        var raidModeVocab = Tuning.RaidModes.Keys.ToList();
        var load = LayoutTemplateCatalog.Load(rows, bandDefs, raidModeVocab);
        Assert.Empty(load.Rejections);
        return load.Catalog;
    }

    /// <summary>
    /// **A real, evidenced escalation of the already-known "no wild-kind rooms shipped" gap
    /// (D1.10's own honest residual), found by actually rolling — not assumed.** `DelveGraphRoll
    /// .RollUnchecked` draws a room KIND per node from `RoomKindCatalog`'s own weighted table
    /// (`DelveGraphRoll.cs:225-236`) BEFORE it ever consults the domain's own `roomPalette` — `wild`
    /// is a real, non-zero-weight member of that draw, so any sampled seed can roll a node into
    /// `wild`, and since none of the six real shipped domains carry a single `wild`-kind room
    /// (`gk-data/packs/fusion/data/seed/dungeon/rooms/` ships zero), that seed throws `DelveGraphRollRejection` naming
    /// exactly `(kind='wild', climate=...)` — confirmed live, not inferred, on the very first fixed
    /// seed tried. This means row 4 of the domain preflight chain (and everything rows 5-8 build on
    /// top of its sampled graphs) cannot pass for ANY of the six first-ship domains until real wild
    /// rooms exist, REGARDLESS of this bridge's own correctness (proven separately, above, against a
    /// hand-built palette that DOES include wild — the same fixture shape
    /// `DelveGraphRollTests.RichFireDomain` already uses).
    ///
    /// <para><b>SUPERSEDES a real, closed finding.</b> The first version of this test proved every
    /// real domain failed EVERY sample on the missing-wild-room gap (zero real `wild`-kind rooms
    /// existed anywhere). That gap is now closed, same session: a real standalone `story`-kind event
    /// plus 12 real `wild`-kind rooms (2 per climate) shipped, and the six domain anchors'
    /// `roomPalette` was patched to include them (`patch_domain_room_palettes.py`, a pure recompute
    /// of `room_palette_for_climate`, never a hand edit). Re-running the EXACT same 32-seed ×
    /// raid-mode × domain sweep now proves the positive claim directly: every real domain really
    /// does roll a real, valid graph on its own real layout, for real, not just "the bridge would
    /// work if content existed."</para>
    /// </summary>
    [Fact]
    public void Every_real_shipped_domain_rolls_a_real_valid_graph_on_its_own_layout_and_climate()
    {
        var roomsById = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();

        Assert.Equal(6, domains.Count);
        var rolled = 0;
        foreach (var domain in domains)
        {
            var anchor = DomainAnchorBuilder.From(domain, roomsById, palettes[domain.DomainId]);
            var layout = layouts.Resolve(domain.LayoutTemplateId);
            Assert.NotNull(layout);

            foreach (var raidMode in layout!.RaidModes!)
            {
                for (var i = 0; i < 32; i++) // matches spec-domain-catalog.md §2's own `preflight.sampleSeeds`
                {
                    // SeededRng.DeriveStream (FNV-1a), not string.GetHashCode() -- .NET randomizes
                    // string hashing per process, and DomainGraphPreflight (the production bridge
                    // this test proves the same sweep for) must derive the IDENTICAL, cross-process
                    // stable seed the real preflight uses, or the two could sample different seeds.
                    var streamName = $"domain-preflight:{domain.DomainId}:{raidMode}:{i}";
                    var seed = SeededRng.DeriveStream(0, streamName).NextULong();
                    var graph = DelveGraphRoll.Roll(anchor, layout, seed, raidMode, Tuning); // throws on any rejection -- no try/catch, a regression here must fail loudly
                    Assert.NotEmpty(graph.Rooms);
                    rolled++;
                }
            }
        }

        Assert.True(rolled >= 6 * 32, $"expected at least 192 successful rolls (6 domains x >=1 raid mode x 32 seeds), got {rolled}");
    }

    /// <summary>The bridge's OWN correctness, isolated from the real content gap above: a hand-built
    /// palette that DOES include every kind (the same fixture shape
    /// `DelveGraphRollTests.RichFireDomain` already uses) rolls cleanly through the real
    /// `DomainAnchorBuilder`-shaped anchor construction.</summary>
    [Fact]
    public void With_a_complete_room_palette_including_wild_the_bridge_rolls_cleanly()
    {
        var roomsById = RoomKindCatalog.All.ToDictionary(
            k => $"room-{k.RoomKindId}", k => new RoomPaletteEntry($"room-{k.RoomKindId}", k.RoomKindId, k.ClimateNeutral ? null : ElementTypeId.Fire));
        var domain = new DomainRow("domain.test", "n", "f", "t", "fire", "shallow", "many", "l", "b", null, "Lair", null);
        var anchor = DomainAnchorBuilder.From(domain, roomsById, roomsById.Keys.ToList());
        var layout = new LayoutTemplate("layout-test", "short", "wide", "linear", "none", "none", "none", new[] { "solo" });

        var graph = DelveGraphRoll.Roll(anchor, layout, seed: 12345UL, raidMode: "solo", Tuning);
        Assert.NotEmpty(graph.Rooms);
    }
}
