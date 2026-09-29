using System.Text.Json;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.17 row 7's own production wiring (party-dungeon-todo.md, 2026-09-07) — closes `event-deck`
/// D3.9's own rule 1 ("every `eventPool` id exists and fits"), proven through the real
/// `DomainPreflight.Run` entry point over all six real domains, their real room palettes and the real
/// 52 shipped events. Rows 1-6/9/10 stubbed to always pass, matching the established isolation style.
/// </summary>
public class DomainEventPreflightBridgeTests
{
    static readonly IReadOnlyList<string> EventKinds = BandCatalog.Get("eventKind").Members;
    static readonly IReadOnlyList<string> RepeatScopes = BandCatalog.Get("repeatScope").Members;
    static readonly IReadOnlyList<string> OutcomeOrdinals = BandCatalog.Get("outcomeOrdinal").Members;
    static readonly IReadOnlyList<string> OverrideTags = OverrideTagCatalog.All;
    static readonly IReadOnlyList<string> DropBands = ReadRealItemDropBands();
    static int NoStatus(string id) => -1;
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

    static IReadOnlyList<string> ReadRealItemDropBands()
    {
        var path = Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "items", "_registry", "bands.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("dropBand").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    static EventCatalog RealCatalog()
    {
        var rows = EventSeedFile.LoadAll(DungeonTestFiles.EventsDir());
        var result = EventCatalog.Load(rows, EventKinds, RepeatScopes, OutcomeOrdinals, DropBands, OverrideTags, NoStatus);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    static DomainPreflightInputs InputsIsolatingRow7(Func<DomainRow, IReadOnlyList<DomainRefusal>> checkEvents) => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.LayoutTemplateId).ToHashSet(StringComparer.Ordinal),
        KnownSpeciesIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.BossSpeciesRef).ToHashSet(StringComparer.Ordinal),
        ThreatBandOrdinalFor: _ => 99,
        BossFloorRungOrdinal: 0,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: checkEvents,
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: Array.Empty<string>(),
        BoundLootKinds: Array.Empty<string>(),
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
        OfferedRungCountFor: _ => 1);

    [Fact]
    public void Build_null_arguments_throw()
    {
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var catalog = RealCatalog();
        Assert.Throws<ArgumentNullException>(() => DomainEventPreflight.Build(null!, rooms, pools, catalog, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainEventPreflight.Build(palettes, null!, pools, catalog, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainEventPreflight.Build(palettes, rooms, null!, catalog, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainEventPreflight.Build(palettes, rooms, pools, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => DomainEventPreflight.Build(palettes, rooms, pools, catalog, null!));
    }

    /// <summary>
    /// The real end-to-end proof, through the actual `DomainPreflight.Run` entry point.
    ///
    /// <para><b>2026-09-08 (party-dungeon-todo.md D4.17 row 7, first pass, same day) — the ORIGINAL
    /// `curio` cell-headroom finding this test used to pin was FIXED, by widening room content, not
    /// by generating new events.</b> Investigation found the real 52-event corpus already had 8
    /// distinct themes for every one of `curio`/`shrine`/`trap`/`bargain`/`encounter-event` — the
    /// gap was never a content-VOLUME shortfall, it was that each individual room's own `eventPool`
    /// array (model-authored, D1.10) referenced only 1-2 of those already-existing, already-valid
    /// events, satisfying the schema's `minItems: 1` but nothing pushing for more. Since
    /// `climateAffinity` never gates pool membership (spec-event-deck.md §2, only weights the draw)
    /// and kind-fit is the only real legality rule, widening every non-`wild` archetype's own
    /// `eventPool` to the full same-kind corpus was a content-completeness fix over ALREADY-SHIPPED,
    /// ALREADY-VALIDATED content — zero new anchors generated, zero model calls, mechanically
    /// reversible. That first pass left `wild`-kind rooms (event kind `story`) as the sole remaining
    /// gap: only 2 distinct-theme `story` events existed, below the >3 floor, with nothing
    /// already-shipped left to widen with.
    ///
    /// <b>2026-09-08 (second pass, same day, later window) — CLOSED FOR REAL, not deferred.</b> The
    /// "needs sequencing infrastructure" citation this test used to repeat (`dungeon-event`'s own
    /// D1.10 entry) was re-read directly against `briefs.py`'s own `_EVENT_KIND_HINT["story"]` doc
    /// comment ("a single, self-contained beat, not part of a longer chain") and `EVENT_KIND_FIRST_
    /// SHIP`'s own comment: the deferred question is about a FUTURE multi-chapter arc, not about
    /// shipping more standalone beats in the ALREADY-ESTABLISHED shape the first 2 story events
    /// already used. Generated 2 more standalone `story` events via the real pipeline
    /// (`run_event_draws`, real local-model call — `event.story-creature.cactus-001` /
    /// `dolldiamond-001`, chained the same planner-assigned way `EventCatalog.Load`'s
    /// `ChainRefRequiredForStory` rule requires), reaching 4 distinct themes, and widened all 12 real
    /// `wild` rooms' own `eventPool` to the full 4-event set (the identical widen-by-union pattern
    /// the first pass used for the other six kinds). `wild` now clears cell-headroom exactly like
    /// every other kind — proven below directly, not assumed.</para>
    /// </summary>
    [Fact]
    public void Every_real_shipped_domain_clears_row7s_cell_headroom_for_every_archetype_including_wild()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var catalog = RealCatalog();

        Assert.Equal(6, domains.Count);

        // The fixed content directly: EVERY archetype's own real room pools clear cell headroom on
        // their own, `wild` included now (no DomainPreflight plumbing needed for this half of the
        // proof).
        foreach (var (roomId, poolIds) in pools)
        {
            if (!rooms.TryGetValue(roomId, out var room) || poolIds.Count == 0) continue;
            var distinctCells = poolIds.Select(id => catalog.Resolve(id)!)
                .Select(e => new EventFilters.EventCell(e.Kind, e.Theme)).Distinct().Count();
            Assert.True(distinctCells > Tuning.EventsNoRepeatRooms,
                $"{roomId} (kind={room.Kind}) only has {distinctCells} distinct cells after the widening fix");
        }

        // The real end-to-end run, row 7 isolated (rows 1-6/9/10 stubbed to pass): zero refusals for
        // every one of the six real domains — the frontier this row-7 chain used to stop at is gone.
        var checkEvents = DomainEventPreflight.Build(palettes, rooms, pools, catalog, Tuning);
        var refusals = DomainPreflight.Run(domains, InputsIsolatingRow7(checkEvents));

        Assert.Empty(refusals);
    }

    /// <summary>Negative control 1: an eventPool naming an unreal event id refuses `pool-ref-missing`.</summary>
    [Fact]
    public void A_room_eventPool_naming_an_unreal_event_is_refused_pool_ref_missing()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.rest-none-002" } };
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.rest-none-002"] = new[] { "event.does-not-exist" } };
        var catalog = RealCatalog();

        var checkEvents = DomainEventPreflight.Build(palettes, rooms, pools, catalog, Tuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow7(checkEvents));

        Assert.Single(refusals);
        Assert.Equal("domain.event:pool-ref-missing", refusals[0].Rule);
        Assert.Contains("event.does-not-exist", refusals[0].Detail);
    }

    /// <summary>Negative control 2: a real event whose kind does NOT fit the room's own kind (a `rest`
    /// room, which only fits `encounter-event`, naming a real `curio`-kind event instead) is refused
    /// `kind-mismatch` — proves the fit check is real, not a rubber stamp.</summary>
    [Fact]
    public void A_room_eventPool_naming_a_real_event_of_the_wrong_kind_is_refused_kind_mismatch()
    {
        var catalog = RealCatalog();
        var wrongKindEvent = catalog.All.First(e => e.Kind != "encounter-event");
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.rest-none-002" } };
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.rest-none-002"] = new[] { wrongKindEvent.EventId } };

        var checkEvents = DomainEventPreflight.Build(palettes, rooms, pools, catalog, Tuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow7(checkEvents));

        Assert.Single(refusals);
        Assert.Equal("domain.event:kind-mismatch", refusals[0].Rule);
    }

    /// <summary>
    /// Rule 9 in isolation. `EventFilters.KindFits`'s own closed table means a `rest` archetype can
    /// ONLY ever kind-fit an `encounter-event`-kind entry (rule 1 already enforces this, and runs
    /// FIRST in this same function) — so "a rest archetype with a non-empty, kind-fit-passing pool
    /// that somehow lacks an encounter-event" is not a reachable state to construct a fixture for; rule
    /// 9's own real, distinguishable bite (given this composition) is exactly an EMPTY pool on a `rest`
    /// archetype, which rule 8 does not itself catch (it is gated on a non-empty pool, since an
    /// intentionally-empty `fight`/`boss`/`cache` pool is correct content, not a headroom defect) — this
    /// proves rule 9 supplies the missing coverage for that one case, not that it is redundant with rule 8.
    /// </summary>
    [Fact]
    public void A_rest_archetype_with_an_empty_pool_is_refused_rest_needs_encounter()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            { [domain.DomainId] = new[] { "room.rest-none-002" } };
        var realRooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        // room.rest-none-002 is a real `rest`-kind room -- only its own pool is overridden here, its
        // kind/climate stay real, matching every other fixture in this file.
        var rooms = new Dictionary<string, RoomPaletteEntry>(StringComparer.Ordinal) { ["room.rest-none-002"] = realRooms["room.rest-none-002"] };
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room.rest-none-002"] = Array.Empty<string>() };
        var catalog = RealCatalog();

        var checkEvents = DomainEventPreflight.Build(palettes, rooms, pools, catalog, Tuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow7(checkEvents));

        Assert.Single(refusals);
        Assert.Equal("domain.event:rest-needs-encounter", refusals[0].Rule);
    }

    /// <summary>A domain whose palette rooms all carry an empty eventPool never even calls into the
    /// catalog — trivially fine, not a manufactured refusal.</summary>
    [Fact]
    public void A_domain_with_only_empty_event_pools_passes_trivially()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.fight-fire-001" } };
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var catalog = RealCatalog();

        var checkEvents = DomainEventPreflight.Build(palettes, rooms, pools, catalog, Tuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow7(checkEvents));

        Assert.Empty(refusals);
    }
}
