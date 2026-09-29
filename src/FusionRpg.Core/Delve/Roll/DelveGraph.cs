using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Delve.Roll;

/// <summary>
/// One room the domain's `roomPalette` names (spec-delve-graph-roll.md §1). A minimal, honest
/// projection of `dungeon-seed-contract`'s ROOM anchor (`ROOM_OWNERSHIP` in
/// <c>gk-forge/tools/seedsmith/seedsmith/adapters/dungeon/schema.py</c>) — that module's C# side has not
/// landed (D1.10's scope-down), so this names exactly the fields <see cref="DelveGraphRoll.Roll"/>
/// reads rather than inventing a fuller anchor type nothing else uses today. `Climate` is null only
/// for the climate-neutral kinds (`rest`, `merchant`, `boss`, `unknown` — <see cref="Dungeon.Registry.RoomKindDef.ClimateNeutral"/>).
/// </summary>
public sealed record RoomPaletteEntry(string RoomId, string Kind, ElementTypeId? Climate);

/// <summary>
/// The domain anchor's C# projection — same caveat as <see cref="RoomPaletteEntry"/>. `DangerBand`
/// is the band's MEMBER id (`shallow`/`mid`/`deep`/`abyssal`, `bands.dangerBand.v1.json`'s
/// vocabulary), resolved to an integer entrance band through <c>tuning.DangerBand[id]</c> — never a
/// raw int here, matching every other band field in this program (an ordinal until tuning resolves
/// it, ssot-power-scale.md's own discipline extended to non-power bands).
/// </summary>
/// <param name="FirstClearRef">D4.28 (spec-unique-pipeline.md §5): a rung-80+ `deterministic`
/// unique container id (`item.&lt;slug&gt;`), or `null` for "none" — VALIDATED by
/// <see cref="Loot.DungeonLootTableGen.ValidateFirstClearRef"/>, never trusted unchecked. Already
/// decided at the seedsmith schema level (`gk-forge/tools/seedsmith/seedsmith/adapters/dungeon/schema.py`);
/// this projection stayed deliberately minimal until a real consumer needed the field — D4.28 is
/// that consumer. Defaulted so this stays a source-compatible addition: no `domains/*.json` C#
/// loader exists yet to update, and every existing positional call site keeps compiling unchanged.</param>
public sealed record DomainAnchor(
    string DomainId, ElementTypeId Climate, string DangerBand, IReadOnlyList<RoomPaletteEntry> RoomPalette,
    string? FirstClearRef = null);

/// <summary>
/// The layout template anchor's C# projection (`LAYOUT_OWNERSHIP`). Every field is a band MEMBER id
/// resolved through <c>tuning</c> at roll time — <c>none</c> is legal for the three density bands
/// (spec §1: "with `none` legal").
/// </summary>
/// <param name="RaidModes">The seed contract's own 8th field (spec-dungeon-seed-contract.md §1.3:
/// "subset of `solo · pair · quad`, never empty") — absent from this record until D4.30's own
/// real-content pass (`layouts.py`) needed a real consumer for it (`DomainOffers`/`DelveStart`'s own
/// already-named `RaidModesForLayout` gap, D4.19/D4.21). Defaulted to `null` so this stays a
/// source-compatible addition: `DelveGraphRoll.Roll` itself never reads raid modes (a domain's own
/// `raidMode` request is validated one layer up, in `DelveStart.Run`), and the two existing
/// positional test call sites (`DelveGraphRollTests.cs`) keep compiling unchanged.</param>
public sealed record LayoutTemplate(
    string LayoutId, string SizeBand, string WidthBand, string Branchiness,
    string GateDensity, string SecretDensity, string OneWayDensity,
    IReadOnlyList<string>? RaidModes = null);

/// <summary>
/// The per-room facts a <see cref="World.WorldSector"/> has no field for (spec-delve-graph-roll.md
/// §1). `KeyForLaneId` is the one fact `delve-scope` persists (`rpg_delve_rooms.key_for_lane_id`);
/// every other field here is derivable from the graph and stays unstored.
/// </summary>
public sealed record DelveRoomFact(
    int Row, int Col, string SectorId, string Kind, string ArchetypeId, int BaseBand,
    bool IsSecret, int SightLanes, int ScoutSightLanes, int PartyRouteMask, string? KeyForLaneId);

/// <summary>One rolled walk — a party route when <c>PartyIndex</c> is set, an extra route
/// otherwise (spec §5).</summary>
public sealed record DelveWalk(int WalkIndex, int? PartyIndex, IReadOnlyList<string> SectorIds);

/// <summary>
/// The pure output of <see cref="DelveGraphRoll.Roll"/> — `WorldSector`/`WorldLane` rows for
/// `delve-scope` to persist, plus the read model everything else queries. Canonical (row, col)
/// order throughout (§8): `Rooms` ordinal by `SectorId`, `Doors` by `LaneId`, both already equal to
/// zero-padded-id order so <c>RequireStableOrder</c>-style checks hold with no sort at load.
/// </summary>
public sealed record DelveGraph(
    IReadOnlyList<WorldSector> Rooms, IReadOnlyList<WorldLane> Doors,
    IReadOnlyList<DelveRoomFact> Facts, IReadOnlyList<DelveWalk> Walks);
