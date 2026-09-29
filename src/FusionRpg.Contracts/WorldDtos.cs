namespace FusionRpg.Contracts;

/// <summary>
/// Wire shapes for the world map (spec-world-model.md §Server). Read-only projections: the seed is
/// deliberately absent — it is the input to every future roll, and a client that knows it can
/// predict outcomes the server has not committed yet.
/// </summary>
public sealed record WorldHeaderDto
{
    public string WorldId { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public int CurrentTurn { get; init; }
    public string State { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public long Revision { get; init; }
}

/// <summary>
/// The current turn's calendar roll only (`TurnCalendar.Roll`, pure in `(turn, seed)`) — never the
/// seed itself, and never a future roll, both of which would let a client enumerate the campaign's
/// plague months ahead of time (world-stage W15). The boundary flags are blank (false) on any turn
/// that is not a week boundary — `TurnCalendar.Roll` itself returns `default` there, not "no roll
/// happened". <see cref="Season"/> is the one exception: unlike the boundary flags it is meaningful
/// on every turn, never fogged (`TurnCalendar.SeasonOf`'s own doc comment) — world-map's own §8b.7
/// decision ("calendar, not a season", made when no season concept existed) is superseded now that
/// one does; wired 2026-09-05 per direct owner decision.
/// </summary>
public sealed record WorldCalendarDto
{
    public int DaysPerWeek { get; init; }
    public int WeeksPerMonth { get; init; }
    public bool WeekBoundary { get; init; }
    public bool MonthBoundary { get; init; }
    public bool SpecialWeek { get; init; }
    public bool SpecialMonth { get; init; }
    public bool Plague { get; init; }
    public int Season { get; init; }
}

public sealed record WorldFactionDto
{
    public string FactionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record WorldSlotDto
{
    public int SlotIndex { get; init; }
    public string SlotTypeId { get; init; } = "";
    public string? Element { get; init; }
    public string State { get; init; } = "";

    /// <summary>
    /// Owner-gated on the `StabilityMilli` pattern (`WorldEndpoints.cs:309-311`) — present only for
    /// the viewer who owns this ground, null otherwise (spec-world-wire.md §1's "cheap resolution").
    /// Projected straight from truth (`WorldSlot.OwnerFactionId`), never from belief:
    /// `RememberedSlot` deliberately does not carry it, since adding it there would move every world
    /// golden (belief is hashed) for a value nothing yet reads.
    /// </summary>
    public string? OwnerFactionId { get; init; }

    public string? GuardWaveId { get; init; }
    public string GuardState { get; init; } = "";

    /// <summary>
    /// As visible as the slot itself (spec-structure-substrate.md) — no owner-gating, following the
    /// slot's own existing fog treatment.
    /// </summary>
    public string? StructureId { get; init; }

    /// <summary>
    /// Null means either no structure or a finished one; a positive count means a structure was just
    /// built and is not yet active (spec-loam-structures.md). As visible as the slot itself — not
    /// owner-gated, the same terms as <see cref="StructureId"/> (world-stage W7).
    /// </summary>
    public int? ConstructionTurnsRemaining { get; init; }

    /// <summary>
    /// Wonder identity without a second catalog fetch per slot (empire-wonder-surfaces
    /// `wonder-wire` §Design 2): the scope of <see cref="StructureId"/>'s catalog row, else null
    /// (empty slot, unknown id, non-Wonder row). Fog-parity with <see cref="StructureId"/> — as
    /// visible as the slot itself, no owner-gating.
    /// </summary>
    public string? WonderScope { get; init; }

    /// <summary>Same null rule as <see cref="WonderScope"/> — null-together per the catalog pairing invariant.</summary>
    public string? WonderRarity { get; init; }
}

/// <summary>
/// A force as the viewer believes it to be. Exact when they were standing on the ground with it; a
/// band when they only glimpsed it from next door — an exact figure from a distance would make fog
/// cosmetic, and a bare "something is there" gives nobody anything to decide with.
/// </summary>
public sealed record WorldForceDto
{
    public string EntityId { get; init; } = "";
    public string OwnerFactionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Exact { get; init; }

    /// <summary>Meaningful only when <see cref="Exact"/>; zero otherwise.</summary>
    public long Strength { get; init; }

    public string BandName { get; init; } = "";

    /// <summary>What to plan against when being wrong would be fatal.</summary>
    public long BandCeiling { get; init; }
}

/// <summary>
/// The additive operands behind one sector's upkeep — `LoamUpkeep.cs`'s own breakdown, mirrored
/// field-for-field. <c>Base + Garrison + Development + Danger</c>, then scaled by
/// <c>IntensityMilli</c> and <c>HandicapMilli</c>, recombines to <see cref="WorldSectorDto.LoamUpkeep"/>
/// exactly (world-stage W10).
/// </summary>
public sealed record LoamUpkeepBreakdownDto
{
    public long Base { get; init; }
    public long Garrison { get; init; }
    public long Development { get; init; }
    public long Danger { get; init; }
    public int IntensityMilli { get; init; }
    public int HandicapMilli { get; init; } = 1000;

    /// <summary>
    /// The fifth additive operand (empire-wonder-surfaces `wonder-wire` §Design 3): this sector's
    /// Wonder upkeep from <c>LoamUpkeep.BreakdownFor</c>'s own <c>WonderUpkeep</c> term — never a
    /// second formula. Appended (not ordered with the original four) so the concurrent 4A.x DTO
    /// extensions merge additively. The fifth ledger ROW stays wonder-display's module; the operand
    /// + its reconciliation land here so the wire never ships a total its ledger cannot reproduce.
    /// </summary>
    public long WonderUpkeep { get; init; }
}

public sealed record WorldSectorDto
{
    public string SectorId { get; init; } = "";
    public string TypeId { get; init; } = "";
    public string? Climate { get; init; }
    public int DangerBand { get; init; }
    public string Phase { get; init; } = "";
    public string? OwnerFactionId { get; init; }
    public int StabilityMilli { get; init; }
    public int PressureMilli { get; init; }
    public int DepletionMilli { get; init; }
    public int DevelopmentLevel { get; init; }

    /// <summary>
    /// The Fracture's local strength — terrain, so it is sent to anyone who has scouted this sector
    /// (spec-loam-model.md).
    /// </summary>
    public int FractureIntensityMilli { get; init; } = 1000;

    /// <summary>
    /// Whether this ground can be kept at all — a property of the terrain, like intensity, so it is
    /// sent to anyone who has scouted the sector, not only the owner (spec-loam-fe.md).
    /// </summary>
    public bool Habitable { get; init; }

    /// <summary>What this sector earns this turn — owner-only (spec-loam-fe.md).</summary>
    public long LoamProduction { get; init; }

    /// <summary>What this sector costs this turn, after intensity and handicap — owner-only.</summary>
    public long LoamUpkeep { get; init; }

    /// <summary>
    /// The operands behind <see cref="LoamUpkeep"/>, in the order the engine applies them
    /// (`LoamUpkeep.cs`) — owner-only, structurally zero-filled for a sector this faction does not
    /// own (same gate as <see cref="LoamUpkeep"/> itself). A ledger showing only the total cannot
    /// decompose what it was never sent (world-stage W10, re-homed from `world-numbers`).
    /// </summary>
    public LoamUpkeepBreakdownDto UpkeepBreakdown { get; init; } = new();

    /// <summary>The number the abandonment decision is actually about — owner-only.</summary>
    public long LoamNet { get; init; }

    /// <summary>
    /// Which connected block of the owner's territory pools with this sector — the lowest sector id
    /// in that component, stable and meaningful on the wire rather than an opaque index. Null unless
    /// the viewer owns this sector.
    /// </summary>
    public string? ComponentId { get; init; }

    public long ComponentProduction { get; init; }
    public long ComponentUpkeep { get; init; }
    public long ComponentNet { get; init; }

    /// <summary>
    /// Raw stock, owner-only (spec-loam-fe.md's gauge). Belief/`IntelSnapshot` deliberately never
    /// carries this even for the owner — that fog rule is unchanged — but this DTO already reads
    /// `StabilityMilli` straight from truth, gated the same way, and the gauge needs stock alongside
    /// income/upkeep/net the same way.
    /// </summary>
    public long LoamStock { get; init; }

    /// <summary>The pooled stock of this sector's whole component — owner-only.</summary>
    public long ComponentStock { get; init; }

    /// <summary>
    /// True when, if nothing changes, this sector is the one the engine will actually release next
    /// turn (stability to zero, ground lost) — not merely fade further. Owner-only. Computed by
    /// <c>LoamForecast.WillRelease</c>, the same selection <c>LoamPhases.Pressure</c> itself applies
    /// the fade with, so the warning and the eventual turn cannot silently disagree.
    /// </summary>
    public bool WillReleaseNextTurn { get; init; }

    /// <summary>
    /// Set only by a warden bind (spec-loam-texture.md). Data only since warden-freeze-fix
    /// (RulesetVersion 13): a binding no longer exempts the sector from <c>FadePolicy</c>, and new
    /// binds are refused with <c>warden.retired</c>. Owner-only, following <see cref="StabilityMilli"/>'s
    /// own gate: it is read straight from truth, not belief, so the viewer sees it exactly when they
    /// own the ground, never a turn stale (world-stage W6).
    /// </summary>
    public string? WardenBindingId { get; init; }

    /// <summary>
    /// How many consecutive turns this sector has sat lost and barren — the Unmade's spawn clock
    /// (spec-loam-texture.md). Owner-only, same gate as <see cref="WardenBindingId"/>; zero on any
    /// sector not currently in that state (world-stage W6).
    /// </summary>
    public int NeglectedTurns { get; init; }

    /// <summary>
    /// The denominator <see cref="LoamStock"/> is measured against — <c>LoamPhases.EffectiveCapacity</c>,
    /// which is the base capacity plus any active granary's bonus. Owner-only, like the stock it
    /// denominates: without it a client showing "stock: 120" has no way to say against what
    /// (world-stage W6).
    /// </summary>
    public long LoamCapacity { get; init; }

    /// <summary>
    /// world-map W44/W46 (spec-sector-development.md §1): what this sector has accrued toward
    /// founding a legion. Owner-only, same gate as <see cref="StabilityMilli"/> — read straight from
    /// truth, not belief, so the viewer sees it exactly when they own the ground.
    /// </summary>
    public long RecruitStock { get; init; }

    /// <summary>A sector-wide project in progress (spec-sector-development.md §3). Owner-only, same gate as <see cref="RecruitStock"/>. Null means no project.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Null means either no project or a finished one; owner-only, same gate as <see cref="ProjectId"/>.</summary>
    public int? ProjectTurnsRemaining { get; init; }

    public string Intel { get; init; } = "";
    public int LastSeenTurn { get; init; }
    public int LayoutX { get; init; }
    public int LayoutY { get; init; }
    /// <summary>Turns since this was last seen. Zero while it is in sight.</summary>
    public int IntelAge { get; init; }

    /// <summary>Empty unless the viewer has actually stood here — a glimpse sees no slots.</summary>
    public IReadOnlyList<WorldSlotDto> Slots { get; init; } = Array.Empty<WorldSlotDto>();

    /// <summary>What the viewer believes is here. Empty where it can see nothing.</summary>
    public IReadOnlyList<WorldForceDto> Forces { get; init; } = Array.Empty<WorldForceDto>();

    /// <summary>
    /// What losing this would cost the viewer's own empire — the lifeline overlay
    /// (world-topology). Zero for anything the viewer does not hold, because it is computed over
    /// their holdings and tells them nothing about anyone else's.
    /// </summary>
    public long LifelineCost { get; init; }

    /// <summary>True when losing this sector would cut the viewer's territory in two.</summary>
    public bool Lifeline { get; init; }

    /// <summary>
    /// Live raised Unique Wonders of Sector scope in THIS sector, built + under-construction
    /// (empire-wonder-surfaces `wonder-wire` §Design 3 — the N in "N of cap raised"): producer
    /// <c>WonderExistenceScan.CountExisting(world, sector, Sector, Unique)</c>. Owner-only,
    /// structurally zero for unowned/unseen ground. `Common` is deliberately absent (never
    /// scanned — reads 0).
    /// </summary>
    public long WonderLiveCountSector { get; init; }

    /// <summary>
    /// Same producer with Empire scope across the viewer's holdings — identical on every sector of
    /// one faction, 0 for unowned/unseen. Appended after <see cref="Lifeline"/> (not beside the
    /// loam block) so concurrent 4A.x DTO extensions merge additively.
    /// </summary>
    public long WonderLiveCountEmpire { get; init; }

    /// <summary>
    /// This sector's Wonder-attributable production (empire-wonder-surfaces `wonder-wire` §Design 3,
    /// GG-49 — no mystery delta): <c>LoamProduction.For(sector, storedModifier) − For(sector, 1000)</c>
    /// in `checked`, 0 for a Wonder-free faction. Owner-only like <see cref="LoamProduction"/>.
    /// </summary>
    public long WonderProductionContribution { get; init; }
}

public sealed record WorldLaneDto
{
    public string LaneId { get; init; } = "";
    public string FromSectorId { get; init; } = "";
    public string ToSectorId { get; init; } = "";
    public string TypeId { get; init; } = "";
    public int Length { get; init; }
    public int Width { get; init; }
    public int HazardMilli { get; init; }
    public int WardLevel { get; init; }

    /// <summary>
    /// Which key opens this lane, if it is gated. As visible as the lane itself — no owner-gating;
    /// hashed at truth level already (`WorldCanonical.cs:47`), just never reached a client before
    /// (world-stage W7).
    /// </summary>
    public string? GateKeyId { get; init; }

    public string State { get; init; } = "";
}

public sealed record WorldEntityMemberDto
{
    public string? InstanceId { get; init; }
    public string SpeciesId { get; init; } = "";
    public int Level { get; init; }
    public long Hp { get; init; }
    public int Wounds { get; init; }

    /// <summary>Whether this member fights or carries (spec-loam-legions.md). Not owner-gated — a
    /// viewer only ever sees their own forces' members here (world-stage W8).</summary>
    public string Role { get; init; } = "";
}

public sealed record WorldEntityDto
{
    public string EntityId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string OwnerFactionId { get; init; } = "";
    public string? AtSectorId { get; init; }
    public string? OnLaneId { get; init; }
    public string? OnLaneTowardSectorId { get; init; }
    public int LaneProgressMilli { get; init; }
    public string Stance { get; init; } = "";
    public int MovementRemaining { get; init; }
    public bool Routed { get; init; }
    public IReadOnlyList<WorldEntityMemberDto> Members { get; init; } = Array.Empty<WorldEntityMemberDto>();

    /// <summary>
    /// Not derivable from <see cref="EntityId"/> client-side (spec-world-playback.md §4) — computed
    /// server-side by <c>EntityNaming.DisplayName</c> from stable state, once, here (world-stage W8).
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Entity-level pool (spec-loam-legions.md) — a viewer only ever sees their own forces here.</summary>
    public long CarriedLoam { get; init; }

    /// <summary>Bearer count × <c>LoamPolicy.CarryPerBearer</c> (<c>LegionSupply.Capacity</c>) — a server tunable, not derivable client-side.</summary>
    public long Capacity { get; init; }

    /// <summary>Headcount × <c>LoamPolicy.BurnPerMember</c> (<c>LegionSupply.Burn</c>) — a server tunable, not derivable client-side.</summary>
    public long Burn { get; init; }

    /// <summary>
    /// Turns of runway left in what this legion is actually carrying right now
    /// (<c>LegionSupply.TurnsUntilExhausted</c>) — the number a player can plan against, not the
    /// full-tank ceiling. Null for an empty legion, which has nothing to burn.
    /// </summary>
    public int? Runway { get; init; }
}

/// <summary>
/// The map as one faction knows it. `Sectors` carries belief; `Entities` carries **only the viewer's
/// own forces**, in full, because you always know what you brought. Everything anyone else has is in
/// its sector's <see cref="WorldSectorDto.Forces"/> at whatever detail it was seen.
/// </summary>
public sealed record WorldStateDto
{
    public string WorldId { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public int CurrentTurn { get; init; }
    public IReadOnlyList<WorldFactionDto> Factions { get; init; } = Array.Empty<WorldFactionDto>();
    public IReadOnlyList<WorldSectorDto> Sectors { get; init; } = Array.Empty<WorldSectorDto>();
    public IReadOnlyList<WorldLaneDto> Lanes { get; init; } = Array.Empty<WorldLaneDto>();
    public IReadOnlyList<WorldEntityDto> Entities { get; init; } = Array.Empty<WorldEntityDto>();

    /// <summary>
    /// One lane's march cost (`LaneCost.For`), for whichever legion the `?forLegion=` query asked
    /// about — keyed by <see cref="WorldLaneDto.LaneId"/>. Empty when no legion was named, or the
    /// named id is not one of this viewer's own forces (world-stage W9, re-homed from
    /// `world-targeting`) — never a guessed or client-derived number, per §0.13 of the map ideal.
    /// Fog-honest: computed against this viewer's *believed* climate, so a ley discount the viewer
    /// has not scouted does not apply and the march reads over-priced, exactly as planning with
    /// incomplete information should.
    /// </summary>
    public IReadOnlyDictionary<string, int> MarchCosts { get; init; } = new Dictionary<string, int>();

    public WorldCalendarDto Calendar { get; init; } = new();

    /// <summary>
    /// Every sector this viewer's dowsers have confirmed holds a loam source this turn
    /// (`Prospecting.Reveal`) — deliberately **separate** from `Sectors[].Intel`, never merged into
    /// it: a dowser answers one narrow question (is there a source here) and leaks no owner, no
    /// danger band, no forces, so folding this into `intel` would silently promote an unknown
    /// sector to scouted (world-stage W16). Empty whenever no dowser is currently out — inert, not
    /// broken, until `world-commands` makes the `dowse` stance orderable.
    /// </summary>
    public IReadOnlyList<string> ProspectedSectorIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One buildable structure (`StructureCatalog`) — rules, not state: no world id, no viewer, no fog
/// (world-stage W17, `GET /api/world/catalog`).
/// </summary>
public sealed record WorldStructureDto
{
    public string StructureId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string RequiredSlotKind { get; init; } = "";

    /// <summary>
    /// Whole loam units — the model compares it directly against a legion's `CarriedLoam`
    /// (`BuildResolver.cs:101`). Named `Cost` here on purpose (matching `StructureDef.Cost` since
    /// world-map W57 renamed it off its former, misleading `CostMilli` name): a renderer trusting
    /// a `Milli` suffix would be wrong by 1000×.
    /// </summary>
    public long Cost { get; init; }

    public int YieldMultiplierMilli { get; init; }
    public int BuildTurns { get; init; }
    public long CapacityBonus { get; init; }

    /// <summary>
    /// base-defense `structure-catalog-import` (module 25) P3-5, second half: `Name`/`Kind`/
    /// `RequiredSlotKind` above already reached this DTO before this module (verified against
    /// `WorldEndpoints.cs` directly — spec-structure-catalog-import.md's own claim that "nothing
    /// can name a structure" was stale). `ObstacleKind` and `MaterialTier` had not — an inspector
    /// panel could name a moat but not say it is an obstacle or how tough it is.
    /// </summary>
    public string ObstacleKind { get; init; } = "";

    /// <summary>Decision 32's material tier ordinal (0 = indestructible). See <see cref="ObstacleKind"/>.</summary>
    public int MaterialTier { get; init; }

    /// <summary>
    /// Wonder identity on the rules wire (empire-wonder-surfaces `wonder-wire` §Design 1 — display
    /// blockers 1–2): exact C# member spelling (`"Sector"`/`"Empire"`), null for non-Wonder rows
    /// (null-together with <see cref="WonderRarity"/> per the catalog pairing invariant). Rules, not
    /// state — no fog gate. Reserved scopes never appear: `Validate` refuses them at startup.
    /// </summary>
    public string? WonderScope { get; init; }

    /// <summary>Same producer row as <see cref="WonderScope"/> (`"Common"`/`"Unique"`), null for non-Wonder rows.</summary>
    public string? WonderRarity { get; init; }

    /// <summary>
    /// How many distinct relic instances this row costs (blocker 3): 0 for every non-Wonder row
    /// (the existing pairing invariant — never a magnitude contentScale touches). `long` end to
    /// end; the TS mirror narrows to `number` (documented transport narrowing, never a logic cast).
    /// </summary>
    public long RelicCost { get; init; }

    /// <summary>
    /// Blocker 4: <c>WonderPolicy.ExistenceCapFor</c> over `gk-core/data/tuning/loam-relics-wonders.v1.json`
    /// (UNTOUCHED — changing that file moves this number with no code change); `long.MaxValue` for
    /// `Common` and for every non-Wonder row (uncapped by construction, never a finite number).
    /// </summary>
    public long ExistenceCap { get; init; } = long.MaxValue;
}

/// <summary>One slot type (`SlotTypeCatalog`) — what a slot letter means.</summary>
public sealed record WorldSlotTypeDto
{
    public string SlotTypeId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Buildable { get; init; }
    public bool Yields { get; init; }
}

/// <summary>One force-strength tier (`StrengthBandCatalog`) — what a band name is worth.</summary>
public sealed record WorldStrengthBandDto
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public long Floor { get; init; }
    public long Ceiling { get; init; }
    public long Midpoint { get; init; }
}

/// <summary>One lane type (`LaneTypeCatalog`) — what a road's own rules are, apart from any one lane.</summary>
public sealed record WorldLaneTypeDto
{
    public string LaneTypeId { get; init; } = "";
    public string Name { get; init; } = "";
    public int CostMultiplierMilli { get; init; }
    public bool CarriesSupply { get; init; }
    public bool CarriesPressure { get; init; }
    public bool OneWay { get; init; }
    public bool Gated { get; init; }
    public bool Ley { get; init; }
}

/// <summary>
/// Rules, not state — one route, no world id, no viewer, no fog (world-stage W17). Everything a UI
/// needs to describe what is buildable, what a slot letter means, and what a strength band is worth,
/// without loading a specific world.
/// </summary>
public sealed record WorldCatalogDto
{
    public IReadOnlyList<WorldStructureDto> Structures { get; init; } = Array.Empty<WorldStructureDto>();
    public IReadOnlyList<WorldSlotTypeDto> SlotTypes { get; init; } = Array.Empty<WorldSlotTypeDto>();
    public IReadOnlyList<WorldStrengthBandDto> StrengthBands { get; init; } = Array.Empty<WorldStrengthBandDto>();
    public IReadOnlyList<WorldLaneTypeDto> LaneTypes { get; init; } = Array.Empty<WorldLaneTypeDto>();

    /// <summary>
    /// `LoamPolicy.WaystationRangeHops` (world-stage W69) — a waystation's build range in plain road
    /// hops, read from the tuning row rather than hard-coded a second time in the FE. Rules, not
    /// state, exactly like every other field on this DTO: the number itself, never a per-world or
    /// per-viewer computation.
    /// </summary>
    public int WaystationRangeHops { get; init; }
}

/// <summary>One order on the wire. Mirrors the Core command shape, kept flat for the FE.</summary>
public sealed class WorldCommandRequest
{
    public string? CommandId { get; set; }
    public string? Kind { get; set; }
    public string? EntityId { get; set; }
    public string? SectorId { get; set; }
    public int? SlotIndex { get; set; }

    /// <summary>The posture a `stance` order asks for: march, scout or hold.</summary>
    public string? Stance { get; set; }

    public List<string>? LanePath { get; set; }

    /// <summary>How much carried loam a `sustain` order spends (spec-loam-legions.md) — whole
    /// units, `long` end to end, never `int` (world-stage W22).</summary>
    public long? Amount { get; set; }

    /// <summary>Which structure a `build` order names (spec-loam-structures.md, world-stage W22).</summary>
    public string? StructureId { get; set; }

    /// <summary>Which relic instances a `build` order spends when the named structure is a Wonder
    /// (`StructureDef.RelicCost &gt; 0`, loam-relics-and-wonders `wonder-rest` §Design 1). Opaque
    /// `rpg_item.instance_id` strings, order-preserved. Null = "spends no relic", matching every
    /// other optional payload field on this record and `CommandPayload`'s own null-tolerant
    /// read-back (`RpgStore.WorldTurns.cs`). Admission owns `relic.count-mismatch` /
    /// `relic.duplicate` — this DTO never validates.</summary>
    public List<string>? RelicInstanceIds { get; set; }

    /// <summary>Which project a `develop` order names (spec-sector-development.md §3, world-map W52).</summary>
    public string? ProjectId { get; set; }

    // empire-inventory-surfaces `cargo-commands` §Design 2: the seven cargo/cache payload fields,
    // field-for-field with `WorldCommand`. Appended after `ProjectId`, never reordering the
    // `wonder-rest` (Task 4A.2) `RelicInstanceIds` lines above — merge-additive either order.
    // Deliberately absent: `weightEach` is never on the wire (mass resolves server-side).

    /// <summary>Cargo-row selector for `unload-cargo`/`transfer-cargo`/`deposit-cargo`/`withdraw-cargo`.</summary>
    public int? Seq { get; set; }

    /// <summary>Destination legion for `transfer-cargo` (`EntityId` is the source).</summary>
    public string? TargetEntityId { get; set; }

    /// <summary>Target cache for `claim-cache`.</summary>
    public string? CacheId { get; set; }

    /// <summary>`load-cargo` item shape: `'instance'` | `'stack'`.</summary>
    public string? CargoKind { get; set; }

    /// <summary>`load-cargo` `kind='instance'` only.</summary>
    public string? InstanceId { get; set; }

    /// <summary>`load-cargo` `kind='stack'` only.</summary>
    public string? ContainerId { get; set; }

    /// <summary>`load-cargo` `kind='stack'` only, whole units, `long` end to end.</summary>
    public long? Qty { get; set; }
}

/// <summary>
/// A commander's orders for the current turn. `CommanderId` may be omitted — the server then files
/// them for the world's player faction, which is what an FE almost always means.
/// </summary>
public sealed class SubmitWorldCommandsRequest
{
    public string? CommanderId { get; set; }
    public List<WorldCommandRequest>? Commands { get; set; }
}

public sealed record WorldCommandResultDto
{
    public string CommandId { get; init; } = "";
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";
    public bool Replayed { get; init; }
}

/// <summary>One commander's end-turn. The turn advances only when the last of them commits.</summary>
public sealed class CommitWorldTurnRequest
{
    public string? CommanderId { get; set; }

    /// <summary>The turn the caller means to end. Required — a commit without one is refused.</summary>
    public int? Turn { get; set; }
}

public sealed record WorldTurnCommitDto
{
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>True on the commit that actually stepped the world — at most one per turn.</summary>
    public bool Advanced { get; init; }

    public string? StateHash { get; init; }

    /// <summary>The turn now open for orders.</summary>
    public int CurrentTurn { get; init; }
}

/// <summary>One line of a turn report, as the map view plays it back.</summary>
public sealed record WorldTurnEntryDto
{
    /// <summary>Where it happened, when it happened anywhere. Null means "nowhere in particular".</summary>
    public string? SectorId { get; init; }

    public string Phase { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed record WorldTurnReportDto
{
    public int Turn { get; init; }
    public string StateHash { get; init; } = "";
    public IReadOnlyList<string> Phases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WorldTurnEntryDto> Entries { get; init; } = Array.Empty<WorldTurnEntryDto>();

    /// <summary>
    /// The orders the turn was given, and — for the ones an AI gave — why. Reports outside the hot
    /// tail are re-derived and can come back empty; commands never are, because they *are* the save,
    /// so this list survives a trim that empties <see cref="Entries"/>.
    /// </summary>
    public IReadOnlyList<WorldTurnCommandDto> Commands { get; init; } = Array.Empty<WorldTurnCommandDto>();
}

/// <summary>One order as the log holds it. `Reason` is null for anything a person filed.</summary>
public sealed record WorldTurnCommandDto
{
    public string CommanderId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? EntityId { get; init; }
    public string? SectorId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>SIM-only creation request.</summary>
public sealed class CreateWorldRequest
{
    public long? PlayerId { get; set; }
    public string? WorldId { get; set; }
    public string? TemplateId { get; set; }

    /// <summary>Sent as a string: a ulong seed does not survive JavaScript's number type.</summary>
    public string? Seed { get; set; }
}

/// <summary>
/// `POST /api/world/{worldId}/bind-warden` (world-stage W29): bind a creature contract as a
/// non-releasable warden, then file the ordinary `bind-warden` order in one call. Retired by
/// warden-freeze-fix (RulesetVersion 13): the endpoint now answers <c>warden.retired</c> before it
/// binds or charges anything.
/// </summary>
public sealed class BindWardenRequest
{
    public long? PlayerId { get; set; }
    public string? CommanderId { get; set; }
    public string? SectorId { get; set; }
    public string? InstanceId { get; set; }
}

/// <summary>
/// The two-step orchestration's outcome. `CommandReplayed` is true when the world order was already
/// on file — the correct client response to any earlier failure was to retry the whole call, and
/// this is how the client can tell that retry was the one that actually filed the order.
/// </summary>
public sealed record BindWardenResultDto
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string InstanceId { get; init; } = "";
    public string SectorId { get; init; } = "";
    public bool CommandReplayed { get; init; }
}

/// <summary>
/// Reachable-first picking's planning-time source (empire-wonder-surfaces `wonder-wire` §Design 4,
/// display blocker 9): which relic instances are reachable HERE for one (entity, sector) pair.
/// Producer: <c>RpgStore.ListReachableRelicsUnlocked</c> — the <c>IsRelicSpendableUnlocked</c>
/// commit-gate predicate factored to a list, minus the batch-only `claimed` set (planning-time has
/// no batch). Sorted ordinal, de-duplicated; `[]` for unknown/unpositioned legion or nothing
/// reachable (inert, not broken). Appended at end of file so concurrent 4A.x DTO extensions merge
/// additively — never reorder existing members.
/// </summary>
public sealed record WorldRelicReachabilityDto
{
    public string WorldId { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string SectorId { get; init; } = "";
    public IReadOnlyList<string> ReachableInstanceIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One cargo row aboard a legion (empire-inventory-surfaces `claim-endpoints` §Design 4 — the
/// `legion-sheet` cargo tab's read-back). Mirrors <c>LegionCargoRow</c> field-for-field, plus the
/// row weight the store already snapshots: `stack` rows carry `checked(qty * weightEach)`, every
/// other row carries `weightEach` (`RpgStore.LegionCargo.cs:204-205` — the projection repeats the
/// same arithmetic, never a second formula).
/// </summary>
public sealed record CargoRowDto
{
    public int Seq { get; init; }
    public string Kind { get; init; } = "";
    public string? InstanceId { get; init; }
    public string? ContainerId { get; init; }
    public long? Qty { get; init; }
    public long WeightEach { get; init; }
    public long RowWeight { get; init; }
}

/// <summary>
/// One legion's contents plus live capacities (empire-inventory-surfaces `claim-endpoints`
/// §Design 4 — one scope per read, never the empire's inventory). Capacities are computed fresh
/// server-side (`memberCount × tuning`); the fold computes meter fractions from these four
/// numbers and never recomputes capacity itself.
/// </summary>
public sealed record LegionCargoDto
{
    public string WorldId { get; init; } = "";
    public string EntityId { get; init; } = "";
    public int AsOfTurn { get; init; }
    public IReadOnlyList<CargoRowDto> Rows { get; init; } = Array.Empty<CargoRowDto>();
    public long WeightUsed { get; init; }
    public long WeightCapacity { get; init; }
    public int SlotsUsed { get; init; }
    public int SlotCapacity { get; init; }
}

/// <summary>
/// One storage row in a sector's vault (empire-inventory-surfaces `claim-endpoints` §Design 4).
/// The same row shape as <see cref="CargoRowDto"/> minus `WeightEach`/`RowWeight` — storage has
/// no `weight_each` by design (`RpgStore.SectorStorage.cs:12-15`).
/// </summary>
public sealed record SectorStorageRowDto
{
    public int Seq { get; init; }
    public string Kind { get; init; } = "";
    public string? InstanceId { get; init; }
    public string? ContainerId { get; init; }
    public long? Qty { get; init; }
}

/// <summary>
/// One sector's vault contents plus room (empire-inventory-surfaces `claim-endpoints` §Design 4 —
/// the storage panel block). <see cref="OwnerFactionId"/> is the live read and the capture-header
/// input (§Design 5); <see cref="SlotCapacity"/> is `SectorItemCapacity.EffectiveCapacity` (a
/// `long` end to end — the producer's own type, never narrowed).
/// </summary>
public sealed record SectorStorageDto
{
    public string WorldId { get; init; } = "";
    public string SectorId { get; init; } = "";
    public string? OwnerFactionId { get; init; }
    public int AsOfTurn { get; init; }
    public IReadOnlyList<SectorStorageRowDto> Rows { get; init; } = Array.Empty<SectorStorageRowDto>();
    public int SlotsUsed { get; init; }
    public long SlotCapacity { get; init; }
}

/// <summary>
/// One fallen cache the querying legion can reach right now (empire-inventory-surfaces
/// `claim-endpoints` §Design 2/4 — the wave-2 `cache-pin`). Count-only, never contents:
/// hidden-until-found falls out with no extra flag — unreachable caches are absent, not masked,
/// so there is no `visible: false` member for a client to misread.
/// </summary>
public sealed record ClaimableCacheDto
{
    public string CacheId { get; init; } = "";
    public string PlaceKind { get; init; } = "";
    public string PlaceRef { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public int ItemCount { get; init; }
    public string CreatedUtc { get; init; } = "";
}

/// <summary>
/// The presence-gated listing itself (empire-inventory-surfaces `claim-endpoints` §Design 2).
/// The response **is** the fog rule: a cache appears iff the legion stands where it lies, it is
/// non-void, and it still holds rows. Empty is `200` with zero entries, never `404`.
/// </summary>
public sealed record ClaimableCacheListDto
{
    public string WorldId { get; init; } = "";
    public string EntityId { get; init; } = "";
    public int AsOfTurn { get; init; }
    public IReadOnlyList<ClaimableCacheDto> Caches { get; init; } = Array.Empty<ClaimableCacheDto>();
    /// <summary>
    /// The live per-act claim price (`world-action-economy` act-price-table
    /// `movement.claimCostMilli`) — the FE binds this, never a literal, so the
    /// pick-up copy cannot drift from what 4A.6 debits.
    /// </summary>
    public int ClaimCostMilli { get; init; }
}

/// <summary>
/// File one `claim-cache` order (empire-inventory-surfaces `claim-endpoints` §Design 3 — a filer,
/// not a mutator). Deliberately absent: `weightEach` is never on the wire (mass resolves
/// server-side at commit through 4A.1's `TryResolveCargoWeight` — `correlationId := CommandId`,
/// so no second key travels either).
/// </summary>
public sealed class FileClaimCacheRequest
{
    public string? CommandId { get; set; }
    public string? CacheId { get; set; }
    public string? CommanderId { get; set; }
}
