using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Narrative;

namespace FusionRpg.Core.World;

/// <summary>
/// Authored starter maps. Wave 1 ships exactly one — the generator (wave 4) replaces this with
/// templates, budgets, and guard bands once the loop can tell us whether a map is any good.
///
/// `first-light` is hand-placed, so the seed is recorded rather than consumed: same template, same
/// world, every time. When generation lands, the seed starts doing work and this contract does not
/// change.
/// </summary>
public static partial class WorldTemplateCatalog
{
    public const string FirstLightId = "first-light";
    public const string TwoHeartsId = "two-hearths";

    public static IReadOnlyList<string> All { get; } = new[] { FirstLightId, TwoHeartsId };

    /// <summary>
    /// Which size tier each template declares (spec-loam-maps.md) — checked by
    /// <see cref="WorldValidation"/> against the actual sector count, so a template cannot silently
    /// drift outside the range its own vocabulary claims.
    /// </summary>
    public static string SizeIdOf(string templateId) => templateId switch
    {
        FirstLightId => WorldSizeCatalog.SmallId,
        TwoHeartsId => WorldSizeCatalog.MediumId,
        _ => throw new ArgumentException($"No declared size for template '{templateId}'.")
    };

    public static bool IsKnown(string? templateId) =>
        templateId != null && All.Contains(templateId, StringComparer.Ordinal);

    /// <summary>
    /// `first-light` stays the default until the ⭐ gate (owner decision, spec-loam-maps.md): an
    /// unreviewed map should not become the thing every new save gets.
    /// </summary>
    public static WorldState Build(string templateId, ulong seed, string worldId = "world-1") =>
        templateId switch
        {
            FirstLightId => WorldValidation.Validate(WithSeededIntel(FirstLight(seed, worldId))),
            TwoHeartsId => WorldValidation.Validate(WithSeededIntel(TwoHearths(seed, worldId))),
            _ => throw new ArgumentException($"Unknown world template id '{templateId}'.")
        };

    /// <summary>
    /// Turns the authored `AuthoredIntel` on each sector into the player faction's opening belief.
    /// Without it a new map opens completely dark and `first-light`'s carefully written opening
    /// reads as six identical silhouettes.
    /// </summary>
    static WorldState WithSeededIntel(WorldState world) =>
        world with { Intel = Intel.IntelSeed.ForTemplate(world) };

    // Faction ids
    const string Dave = "dave";
    const string Zomboss = "zomboss";
    const string Wild = "wild";

    // Guard encounter ids are opaque in wave 1 — the combat stream owns what they mean.
    const string GuardLight = "guard-light";
    const string GuardMedium = "guard-medium";
    const string GuardHeavy = "guard-heavy";

    /// <summary>
    /// Six sectors teaching the whole wave: home, two claimable neighbours (one fire, one ice), a
    /// no-base waste in the middle, a rich prize, and a nexus behind it.
    /// </summary>
    static WorldState FirstLight(ulong seed, string worldId) => new()
    {
        WorldId = worldId,
        TemplateId = FirstLightId,
        Seed = seed,
        CurrentTurn = 0,

        Factions = new WorldFaction[]
        {
            new() { FactionId = Dave, Kind = WorldFactionKind.Player, Name = LeadNamesHub.Current.Display(LeadTokens.Summoner) },
            new() { FactionId = Wild, Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = "stand-fast" },
            // The wild keep `stand-fast` on purpose: they are a hazard on the map, not a third empire.
            // An expansionist wild would race the player for every sector and turn a map with danger
            // on it into a map with two opponents on it.
            new() { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = LeadNamesHub.Current.Display(LeadTokens.Antagonist), PolicyId = Ai.FrontierRulesPolicy.Id }
        }.OrderBy(f => f.FactionId, StringComparer.Ordinal).ToList(),

        Sectors = new WorldSector[]
        {
            new()
            {
                SectorId = "ash-waste", TypeId = "barren", Climate = ElementTypeId.Earth, DangerBand = 2,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Rumored, LayoutX = 4, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" },
                    new() { SlotIndex = 2, SlotTypeId = "material-seam", GuardWaveId = GuardMedium, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "black-gate", TypeId = "nexus", Climate = ElementTypeId.Dark, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, LayoutX = 6, LayoutY = 1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", GuardWaveId = GuardHeavy, GuardState = GuardState.Intact },
                    new() { SlotIndex = 1, SlotTypeId = "wildland" },
                    new() { SlotIndex = 2, SlotTypeId = "spire", GuardWaveId = GuardMedium, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "ember-hollow", TypeId = "stable", Climate = ElementTypeId.Fire, DangerBand = 1,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Scouted, LayoutX = 2, LayoutY = -1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat" },
                    new() { SlotIndex = 1, SlotTypeId = "wildland" },
                    new() { SlotIndex = 2, SlotTypeId = "essence-deposit", Element = ElementTypeId.Fire, GuardWaveId = GuardLight, GuardState = GuardState.Intact },
                    new() { SlotIndex = 3, SlotTypeId = "lair", GuardWaveId = GuardLight, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "frost-mire", TypeId = "stable", Climate = ElementTypeId.Ice, DangerBand = 1,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Rumored, LayoutX = 2, LayoutY = 1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat" },
                    new() { SlotIndex = 1, SlotTypeId = "wildland" },
                    new() { SlotIndex = 2, SlotTypeId = "essence-deposit", Element = ElementTypeId.Ice, GuardWaveId = GuardLight, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "homeworld", TypeId = "homeworld", Climate = null, DangerBand = 0,
                Phase = SectorPhase.Held, OwnerFactionId = Dave, AuthoredIntel = IntelState.Watched,
                StabilityMilli = 1000, LayoutX = 0, LayoutY = 0,
                // G-D: validation rule 4 (loam-model) requires the homeworld carry a rootbed, and a
                // starting stock (G-A) keeps a new world from fading on turn one once loam-turn is
                // wired. Both are placeholders — L9's harness, not this template, decides the real
                // number.
                LoamStock = 500,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 1, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 2, SlotTypeId = "market", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 3, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Dave }
                }
            },
            new()
            {
                SectorId = "verdant-shelf", TypeId = "rich", Climate = ElementTypeId.Earth, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, LayoutX = 6, LayoutY = -1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat" },
                    new() { SlotIndex = 1, SlotTypeId = "wildland" },
                    new() { SlotIndex = 2, SlotTypeId = "shard-vein", GuardWaveId = GuardHeavy, GuardState = GuardState.Intact },
                    new() { SlotIndex = 3, SlotTypeId = "essence-deposit", Element = ElementTypeId.Earth, GuardWaveId = GuardMedium, GuardState = GuardState.Intact }
                }
            }
        }.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),

        // Penny's one open lane first, then the frontier beyond it.
        Lanes = new WorldLane[]
        {
            new() { LaneId = "l-ash-black", FromSectorId = "ash-waste", ToSectorId = "black-gate", TypeId = "rift", Length = 1200, Width = 800, HazardMilli = 100 },
            // Verdant Shelf hangs off Black Gate rather than Ash Waste (2026-08-22). Ash Waste used to
            // touch four of six sectors, so one march to the middle lit the entire map permanently and
            // the fog was a three-turn opening rather than a condition. Now the far corner stays dark
            // until somebody goes and looks, and the richest ground on the map sits behind Zomboss.
            new() { LaneId = "l-black-verdant", FromSectorId = "black-gate", ToSectorId = "verdant-shelf", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-ember-ash", FromSectorId = "ember-hollow", ToSectorId = "ash-waste", TypeId = "ley", Length = 1000, Width = 600 },
            new() { LaneId = "l-frost-ash", FromSectorId = "frost-mire", ToSectorId = "ash-waste", TypeId = "rift", Length = 1000, Width = 1000 },
            new() { LaneId = "l-home-ember", FromSectorId = "homeworld", ToSectorId = "ember-hollow", TypeId = "corridor", Length = 800, Width = 1000 },
            new() { LaneId = "l-home-frost", FromSectorId = "homeworld", ToSectorId = "frost-mire", TypeId = "rift", Length = 900, Width = 1000 }
        }.OrderBy(l => l.LaneId, StringComparer.Ordinal).ToList(),

        Entities = new WorldEntity[]
        {
            new()
            {
                EntityId = "e-dave-legion-1", Kind = WorldEntityKind.Legion, OwnerFactionId = Dave,
                AtSectorId = "homeworld", Stance = "march", MovementRemaining = 1000,
                // L27 (spec-loam-legions.md): one bearer, and a generous starting reserve — the
                // same "minimum edit to keep the template functional under a new mechanic" shape
                // G-D used for the homeworld's starting `LoamStock` (both placeholders, not
                // harness-tuned). A legion with no reserve at all would be destroyed the instant it
                // steps off owned ground, before it can clear or claim anything.
                CarriedLoam = 500,
                Members = new WorldEntityMember[]
                {
                    new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                    new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
                    new() { SpeciesId = "paperzombie", Level = 1, Hp = 110, Role = WorldEntityMemberRole.Bearer }
                }
            },
            // Zomboss's standing force, at the far end of the map behind his own Seat.
            //
            // The template shipped without one until 2026-08-22: he had a faction, a fortress and no
            // army, so once `ai-commander` gave him a brain he had nothing to command and correctly
            // stood still every turn. Found by playing twenty turns rather than by any test — every
            // suite agreed the AI worked, and it did; there was simply nobody to be.
            //
            // Deliberately smaller than Dave's legion and parked three lanes away. He is meant to be
            // a presence you meet in the middle of the map, not an opening threat.
            new()
            {
                EntityId = "e-zomboss-band-1", Kind = WorldEntityKind.Warband, OwnerFactionId = Zomboss,
                AtSectorId = "black-gate", Stance = "march", MovementRemaining = 1000,
                Members = new WorldEntityMember[]
                {
                    new() { SpeciesId = "normalzombie", Level = 3, Hp = 200 },
                    new() { SpeciesId = "conezombie", Level = 3, Hp = 200 }
                }
            },
            new()
            {
                EntityId = "e-wild-pack-1", Kind = WorldEntityKind.Warband, OwnerFactionId = Wild,
                // A holder refills to the garrison act allowance, not 0 (spec-hold-allowance.md
                // §Design 5) — this literal is `movement.holdAllowanceMilli` (provisional 250).
                // The template cannot read tuning at build time, so the next balance pass moves
                // this number alongside the JSON row.
                AtSectorId = "ash-waste", Stance = "hold", MovementRemaining = 250,
                Members = new WorldEntityMember[]
                {
                    new() { SpeciesId = "normalzombie", Level = 2, Hp = 140 },
                    new() { SpeciesId = "flagzombie", Level = 2, Hp = 140 }
                }
            }
        }.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
    };
}
