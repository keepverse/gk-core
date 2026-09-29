using FusionRpg.Core.World.District;

namespace FusionRpg.Core.World.Turn;

/// <summary>What a battle is being fought over.</summary>
public static class BattleKinds
{
    /// <summary>Two hostile forces standing in the same sector.</summary>
    public const string Sector = "sector";

    /// <summary>Two hostile forces closing on one lane, at the moment they meet.</summary>
    public const string Lane = "lane";

    /// <summary>A deliberate attack on a slot's guard — never a consequence of walking past it.</summary>
    public const string Guard = "guard";

    /// <summary>
    /// An assault on the district around a Seat (base-defense-ideal.md decision 26). Distinct from
    /// <see cref="Guard"/>: a guard defends one slot and is cleared by a `clear` order; a district
    /// assault is fought on a board for the legions standing in its core.
    /// </summary>
    public const string District = "district";

    /// <summary>
    /// A battle's id: deterministic, unique within a turn, and readable in a report. It lives here
    /// with the kinds rather than in whichever phase happens to start the fight, so movement and
    /// sieges cannot drift into two different formats.
    /// </summary>
    public static string IdFor(int turn, string kind, string location, string attacker, string? defender) =>
        $"t{turn}:{kind}:{location}:{attacker}" + (defender is null ? "" : "|" + defender);
}

/// <summary>
/// The map's half of the combat seam (spec-world-movement.md §Contact). The world says who is
/// fighting, where, and when inside the turn; it never says who wins. Everything here is plain data
/// so the real combat module can be dropped in behind <see cref="IBattleResolver"/> without the
/// world module learning anything about rounds, decks, or damage.
/// </summary>
public sealed record BattleRequest
{
    /// <summary>Deterministic and unique within a turn — the report key and the replay anchor.</summary>
    public string BattleId { get; init; } = "";

    public string Kind { get; init; } = "";

    /// <summary>Sector id, or lane id for a crossing.</summary>
    public string LocationId { get; init; } = "";

    /// <summary>When inside the turn, in per-mille — 0 for anything settled after movement.</summary>
    public int TimeMilli { get; init; }

    public string AttackerEntityId { get; init; } = "";

    /// <summary>Null for a guard fight: a guard is slot state, not an entity.</summary>
    public string? DefenderEntityId { get; init; }

    /// <summary>
    /// True when the defender held this ground at turn start. Neither side defends when both were
    /// moving — which is the difference between meeting in the open and being met.
    /// </summary>
    public bool DefenderStationary { get; init; }

    public string? GuardWaveId { get; init; }
    public int? SlotIndex { get; init; }

    /// <summary>
    /// The board this is fought on, or null for every battle kind that has none — which is all
    /// three kinds that predate `siege-seam`. Null is the default and the existing path, so a
    /// sector fight, a lane meeting and a guard clear construct exactly the record they construct
    /// today.
    /// </summary>
    public BoardProjection? Board { get; init; }

    /// <summary>
    /// What each side may spend during this battle. Null for every battle kind without a board.
    ///
    /// <para><b>An in-battle build may NOT debit `WorldSector.LoamStock` or `WorldEntity.CarriedLoam`
    /// directly</b> — combat never writes world state; it does not claim sectors, spend shards, or
    /// move legions. The budget crosses in, the SPEND crosses back, and only the world debits.
    /// `siege-economy` owns the reconciliation.</para>
    /// </summary>
    public IReadOnlyList<SideBudget>? Budgets { get; init; }
}

/// <summary>
/// What the world hands the combat module about the ground (spec-siege-seam.md §1). Deliberately a
/// PROJECTION rather than a materialised <see cref="GridSpec"/>: the world says which sector and
/// which edge, and the combat side derives the grid from `district-layout` itself. Passing a
/// materialised grid across the seam would make the world module own a board representation, which
/// is the coupling this file's own header refuses — the world module never learns anything about
/// rounds, decks, or damage.
/// </summary>
public sealed record BoardProjection
{
    public string SectorId { get; init; } = "";
    public ulong WorldSeed { get; init; }
    public BoardEdge AttackerEdge { get; init; }

    /// <summary>base-defense `siege-ai` 17.11 (decision 47): the attacker's own lane `WardLevel`
    /// (`DistrictLayout.WardLevelFor`), 0 for every battle before this task and every attacker not
    /// currently on a lane — additive, defaulted, byte-identical until a real caller sets it.</summary>
    public int WardLevel { get; init; }

    /// <summary>
    /// base-defense `siege-resolver` (module 15): the sector's own development level, additive and
    /// defaulted to 0 (every battle kind that predates this field constructs the identical record it
    /// always did). `DistrictLayout.Build` reads a sector's `DevelopmentLevel` for board side (via
    /// `SideFor`) and `StructurePolicy.MaxHpOf` reads it for structure HP — the resolver needs this
    /// number to reconstruct a board without holding a live `WorldSector` reference across the seam.
    /// </summary>
    public int DevelopmentLevel { get; init; }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, session 5): the sector's own
    /// `Built`-path budget, additive and defaulted to 0 (every battle kind that predates this field —
    /// every non-district one — constructs the identical record it always did, and a district assault
    /// with no live construction wiring reads these as 0 too, which is correct: nothing could ever
    /// have earned a nonzero balance without this field existing). Threaded through so a live,
    /// in-battle construction decision (`ConstructionBoardContext.CanAffordBuilt`/`SpendBuilt`) can
    /// read the sector's REAL current stock rather than the vacuous zero it silently read before this.
    /// </summary>
    public long RubbleStock { get; init; }
    public long IronworkStock { get; init; }

    /// <summary>
    /// base-defense `siege-resolver`: the sector's own type id, additive and defaulted to `""`.
    /// `DistrictLayout.Build` reads it (via `SectorTypeCatalog`) to decide the Fortress rampart bonus —
    /// omitting it would silently drop that bonus for every district assault, which is a worse defect
    /// than the field looking redundant next to `SectorId`.
    /// </summary>
    public string SectorTypeId { get; init; } = "";

    /// <summary>Slot index → what stands there. Empty is legal — a district with no structures.</summary>
    public IReadOnlyList<SlotProjection> Slots { get; init; } = Array.Empty<SlotProjection>();
}

/// <summary>One slot's state as the world hands it across the seam — enough for the combat side to
/// place a structure on the board it derives, without the world knowing what "placing" means.</summary>
public sealed record SlotProjection
{
    public int SlotIndex { get; init; }
    public string SlotTypeId { get; init; } = "";
    public string? StructureId { get; init; }
    public string? OwnerFactionId { get; init; }

    /// <summary>
    /// base-defense `siege-resolver`: additive, defaulted to <see cref="SlotState.Intact"/> — every
    /// battle kind that predates this field constructs the identical record it always did.
    /// `DistrictLayout.Build` reads a slot's state to render a Ruined/Depleted slot as Rough terrain
    /// (rubble, no structure) rather than Open ground.
    /// </summary>
    public SlotState State { get; init; } = SlotState.Intact;

    /// <summary>
    /// base-defense `siege-resolver`: mirrors <see cref="WorldSlot.StructureHp"/> — null means
    /// undamaged (full `StructureDef.MaxHpOf`), additive and defaulted to null so every existing
    /// caller constructs the identical record it always did. Without this, a structure entering a
    /// second engagement would silently re-enter at full health regardless of prior damage.
    /// </summary>
    public long? StructureHp { get; init; }
}

/// <summary>
/// One side's spend budget for a battle with a board. The asymmetry is authored here, not invented
/// downstream: a defender draws from the sector's own stock (at home, supplied — blockading
/// production is how an attacker stops them rebuilding); an attacker draws from what the legion
/// marched in with (finite).
/// </summary>
public sealed record SideBudget
{
    public string EntityId { get; init; } = "";

    /// <summary>What this side may spend. `long` — a magnitude `contentScale` touches.</summary>
    public long Amount { get; init; }
}

/// <summary>What one side has left when the dust settles.</summary>
public sealed record BattleSideOutcome
{
    public string EntityId { get; init; } = "";

    /// <summary>Members still alive, in their original order, carrying their new wounds.</summary>
    public IReadOnlyList<WorldEntityMember> Survivors { get; init; } = Array.Empty<WorldEntityMember>();

    /// <summary>
    /// Beaten but alive: falls back down the lane it was on, whether it was still mid-crossing or had
    /// fully arrived this same turn (<see cref="BattleApplication"/>'s own fall-back logic), or holds
    /// the ground it was standing on if it had no lane this turn to fall back down — and loses next
    /// turn's orders either way.
    /// </summary>
    public bool Routed { get; init; }

    /// <summary>Nothing walked away — the entity leaves the map.</summary>
    public bool Destroyed { get; init; }

    /// <summary>
    /// Left the field deliberately, whole (audit F5). **Distinct from <see cref="Routed"/>**, which
    /// is "beaten but alive and loses next turn's orders", and from <see cref="Destroyed"/>. A raid
    /// that achieves its objective and withdraws has not been beaten and must not be penalised as
    /// though it had — that penalty is precisely what would make raiding a dominated strategy nobody
    /// ever picks. Mutually exclusive with <see cref="Destroyed"/> — <see cref="BattleApplication.Apply"/>
    /// throws rather than silently picking one, since a resolver producing both has a bug that would
    /// otherwise show up as a ghost army.
    /// </summary>
    public bool Withdrawn { get; init; }
}

public sealed record BattleOutcome
{
    public string BattleId { get; init; } = "";

    /// <summary>Null when nobody won — mutual destruction, or a guard that held.</summary>
    public string? WinnerEntityId { get; init; }

    public bool GuardCleared { get; init; }

    public IReadOnlyList<BattleSideOutcome> Sides { get; init; } = Array.Empty<BattleSideOutcome>();

    /// <summary>What happened to each slot on the board. Empty for every battle that has no board —
    /// the same default-is-today's-behaviour discipline as <see cref="BattleRequest.Board"/>.</summary>
    public IReadOnlyList<SlotOutcome> SlotResults { get; init; } = Array.Empty<SlotOutcome>();

    /// <summary>
    /// base-defense `siege-resolver` §2 rule 8: stamp every resolution so a version mismatch between
    /// an original and a re-derived report is detectable rather than silently accepted. All three
    /// default to 0/null — every battle kind that predates this field, and every district assault
    /// resolved before this landed, constructs the identical outcome it always did.
    /// </summary>
    public int EngineVersion { get; init; }
    public int RulesetVersion { get; init; }
    public ulong? Seed { get; init; }

    /// <summary>
    /// base-defense `siege-engagement` (module 20, decision 24): how this engagement ended, for a
    /// `BattleKinds.District` battle only. Null for every other kind, and for every district battle
    /// resolved before this landed — the same default-is-today's-behaviour discipline every prior
    /// `siege-seam` field addition used.
    /// </summary>
    public EngagementExit? Exit { get; init; }
}

/// <summary>One board slot's result at the end of a district assault.</summary>
public sealed record SlotOutcome
{
    public int SlotIndex { get; init; }

    /// <summary>Remaining structure HP. **long** — a structure's HP is a magnitude `contentScale`
    /// touches, and AGENTS.md's rule 1 is unconditional for those.</summary>
    public long StructureHp { get; init; }

    public bool StructureDestroyed { get; init; }

    /// <summary>Who ended the battle occupying it — possession is by occupation (decision 4:
    /// buildings have no ownership). Null means nobody.</summary>
    public string? HeldByFactionId { get; init; }

    /// <summary>
    /// base-defense `siege-construction` (decision 27): the structure now standing on this slot, if a
    /// NEW one was placed this battle (`Built`/`Assembled`/`Summoned`/`Laboured`, all resolved through
    /// the `structure.place` atom). Null means no placement happened this battle — every caller that
    /// predates this field constructs the identical record it always did.
    /// </summary>
    public string? StructurePlaced { get; init; }

    /// <summary>
    /// Mirrors `WorldSlot.ConstructionTurnsRemaining`, for a <see cref="StructurePlaced"/> only —
    /// meaningless when that is null. Null means the placed structure is finished immediately
    /// (`Assembled`/`Summoned`, decision 27: "immediate — you carried it here already" / "nothing new
    /// is needed"); a positive count (`Built`/`Laboured`, from `StructureDef.BuildTurns`) means it is
    /// still under construction and decrements turn-by-turn through the SAME `LoamPhases.Production`
    /// pass that already drives the peacetime `Build` path — no second countdown mechanism.
    /// </summary>
    public int? PlacedConstructionTurnsRemaining { get; init; }
}

/// <summary>
/// The seam itself. Wave 1 ships exactly one implementation — the placeholder — and the world
/// module is the only thing that constructs it, so nothing else can start depending on its numbers.
/// </summary>
public interface IBattleResolver
{
    /// <summary>
    /// Resolves one request. <paramref name="combatants"/> carries the entities named by the
    /// request, so an implementation never needs the whole world; <paramref name="seed"/> is the
    /// world seed mixed with the turn, so a roll is reproducible without a wall clock.
    /// </summary>
    BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed);
}
