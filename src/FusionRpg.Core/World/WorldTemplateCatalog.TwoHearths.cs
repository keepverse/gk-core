using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.Narrative;

namespace FusionRpg.Core.World;

public static partial class WorldTemplateCatalog
{
    /// <summary>
    /// The gate map (spec-loam-maps.md): sixteen sectors, a dumbbell shape — two capitals, each a
    /// dense rootbed cluster with a small internal loop (so the capital itself is not a single point
    /// of failure), each trailing one outlying holding reachable by exactly one lane (the severable
    /// waist), joined by a long barren corridor with one hot, heavily-rootbedded sector at its
    /// midpoint. "Capital" means a cluster, never a second `Flags.Home` sector — after the S3
    /// resolution nothing in the loam rules reads that flag at all, so Zomboss's capital needs no
    /// model change whatsoever.
    /// </summary>
    static WorldState TwoHearths(ulong seed, string worldId) => new()
    {
        WorldId = worldId,
        TemplateId = TwoHeartsId,
        Seed = seed,
        CurrentTurn = 0,

        Factions = new WorldFaction[]
        {
            new() { FactionId = Dave, Kind = WorldFactionKind.Player, Name = LeadNamesHub.Current.Display(LeadTokens.Summoner) },
            new() { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = LeadNamesHub.Current.Display(LeadTokens.Antagonist), PolicyId = Ai.FrontierRulesPolicy.Id },
            // L41 (spec-loam-texture.md): the same row `first-light` already has — no new AI, the
            // Unmade need only that a `Wild` faction exists to spawn onto neglected barren ground.
            new() { FactionId = Wild, Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = "stand-fast" }
        }.OrderBy(f => f.FactionId, StringComparer.Ordinal).ToList(),

        Sectors = new WorldSector[]
        {
            // ---- Dave's capital: two rootbeds, a small internal loop (d-home <-> d-flank-1 <->
            // d-flank-2 <-> d-home), so no single lane cut isolates any of the three from the others.
            new()
            {
                SectorId = "d-home", TypeId = "homeworld", Climate = null, DangerBand = 0,
                Phase = SectorPhase.Held, OwnerFactionId = Dave, AuthoredIntel = IntelState.Watched,
                StabilityMilli = 1000, LoamStock = LoamPolicy.LoamCapacity, FractureIntensityMilli = 500,
                LayoutX = -6, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 1, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 2, SlotTypeId = "market", State = SlotState.Claimed, OwnerFactionId = Dave }
                }
            },
            new()
            {
                SectorId = "d-flank-1", TypeId = "stable", Climate = ElementTypeId.Earth, DangerBand = 1,
                Phase = SectorPhase.Held, OwnerFactionId = Dave, AuthoredIntel = IntelState.Scouted,
                StabilityMilli = 1000, LoamStock = LoamPolicy.LoamCapacity, FractureIntensityMilli = 600,
                LayoutX = -5, LayoutY = -1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 1, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 2, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Dave }
                }
            },
            new()
            {
                SectorId = "d-flank-2", TypeId = "stable", Climate = ElementTypeId.Earth, DangerBand = 1,
                Phase = SectorPhase.Held, OwnerFactionId = Dave, AuthoredIntel = IntelState.Scouted,
                StabilityMilli = 1000, FractureIntensityMilli = 1000,
                LayoutX = -4, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 1, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 2, SlotTypeId = "essence-deposit", Element = ElementTypeId.Earth, GuardWaveId = GuardLight, GuardState = GuardState.Intact }
                }
            },

            // ---- Dave's outpost: reachable by exactly one lane (l-df2-do) — the severable waist.
            // Cut it and this sector is its own component, alone, with no source of its own.
            new()
            {
                SectorId = "d-outpost", TypeId = "rich", Climate = ElementTypeId.Earth, DangerBand = 2,
                Phase = SectorPhase.Held, OwnerFactionId = Dave, AuthoredIntel = IntelState.Rumored,
                StabilityMilli = 1000, FractureIntensityMilli = 1200,
                LayoutX = -4, LayoutY = -2,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 1, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Dave },
                    new() { SlotIndex = 2, SlotTypeId = "shard-vein", GuardWaveId = GuardHeavy, GuardState = GuardState.Intact }
                }
            },

            // ---- The corridor: seven barren sectors, no Seat, no rootbed, ever — takeable, never
            // keepable. The Fracture strengthens toward the midpoint and eases again past it.
            new()
            {
                SectorId = "corridor-1", TypeId = "barren", Climate = ElementTypeId.Dark, DangerBand = 2,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Rumored, FractureIntensityMilli = 1600,
                LayoutX = -2, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" }
                }
            },
            new()
            {
                SectorId = "corridor-2", TypeId = "barren", Climate = ElementTypeId.Dark, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 1800,
                LayoutX = -1, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" }
                }
            },
            new()
            {
                SectorId = "corridor-3", TypeId = "barren", Climate = ElementTypeId.Dark, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 2000,
                LayoutX = 0, LayoutY = 1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "material-seam", GuardWaveId = GuardMedium, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "corridor-4", TypeId = "barren", Climate = ElementTypeId.Dark, DangerBand = 4,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 2200,
                LayoutX = 1, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" }
                }
            },

            // ---- The hot sector: several rootbeds, the highest intensity on the map, unclaimed at
            // creation — the profit centre both sides want (§12.5), and the front line's real prize.
            new()
            {
                SectorId = "hot-ground", TypeId = "rich", Climate = ElementTypeId.Dark, DangerBand = 5,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 2600,
                LayoutX = 2, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat" },
                    new() { SlotIndex = 1, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId },
                    new() { SlotIndex = 2, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId },
                    new() { SlotIndex = 3, SlotTypeId = "shard-vein", GuardWaveId = GuardHeavy, GuardState = GuardState.Intact }
                }
            },

            new()
            {
                SectorId = "corridor-5", TypeId = "barren", Climate = ElementTypeId.Fire, DangerBand = 4,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 2200,
                LayoutX = 3, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" }
                }
            },
            new()
            {
                SectorId = "corridor-6", TypeId = "barren", Climate = ElementTypeId.Fire, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 2000,
                LayoutX = 4, LayoutY = -1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "material-seam", GuardWaveId = GuardMedium, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "corridor-7", TypeId = "barren", Climate = ElementTypeId.Fire, DangerBand = 3,
                Phase = SectorPhase.Unknown, AuthoredIntel = IntelState.Unknown, FractureIntensityMilli = 1800,
                LayoutX = 5, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "wildland" },
                    new() { SlotIndex = 1, SlotTypeId = "hazard" }
                }
            },

            // ---- Zomboss's outpost: the mirror of `d-outpost` — reachable by exactly one lane
            // (l-zf2-zo), the second severable waist. Symmetric in kind, per ideal §12.3.
            new()
            {
                SectorId = "z-outpost", TypeId = "rich", Climate = ElementTypeId.Fire, DangerBand = 2,
                Phase = SectorPhase.Held, OwnerFactionId = Zomboss, AuthoredIntel = IntelState.Unknown,
                StabilityMilli = 1000, FractureIntensityMilli = 1200,
                LayoutX = 6, LayoutY = -2,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 1, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 2, SlotTypeId = "shard-vein", GuardWaveId = GuardHeavy, GuardState = GuardState.Intact }
                }
            },
            new()
            {
                SectorId = "z-flank-2", TypeId = "stable", Climate = ElementTypeId.Fire, DangerBand = 1,
                Phase = SectorPhase.Held, OwnerFactionId = Zomboss, AuthoredIntel = IntelState.Unknown,
                StabilityMilli = 1000, FractureIntensityMilli = 1000,
                LayoutX = 6, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 1, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 2, SlotTypeId = "essence-deposit", Element = ElementTypeId.Fire, GuardWaveId = GuardLight, GuardState = GuardState.Intact }
                }
            },

            // ---- Zomboss's capital: the mirror of Dave's — two rootbeds, the same internal loop
            // (z-home <-> z-flank-1 <-> z-flank-2 <-> z-home).
            new()
            {
                SectorId = "z-flank-1", TypeId = "stable", Climate = ElementTypeId.Fire, DangerBand = 1,
                Phase = SectorPhase.Held, OwnerFactionId = Zomboss, AuthoredIntel = IntelState.Unknown,
                StabilityMilli = 1000, LoamStock = LoamPolicy.LoamCapacity, FractureIntensityMilli = 600,
                LayoutX = 7, LayoutY = -1,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 1, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 2, SlotTypeId = "wildland", State = SlotState.Claimed, OwnerFactionId = Zomboss }
                }
            },
            new()
            {
                SectorId = "z-home", TypeId = "warcamp", Climate = ElementTypeId.Fire, DangerBand = 2,
                Phase = SectorPhase.Held, OwnerFactionId = Zomboss, AuthoredIntel = IntelState.Unknown,
                StabilityMilli = 1000, LoamStock = LoamPolicy.LoamCapacity, FractureIntensityMilli = 500,
                LayoutX = 8, LayoutY = 0,
                Slots = new WorldSlot[]
                {
                    new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 1, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId, State = SlotState.Claimed, OwnerFactionId = Zomboss },
                    new() { SlotIndex = 2, SlotTypeId = "lair", GuardWaveId = GuardLight, GuardState = GuardState.Intact }
                }
            }
        }.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),

        Lanes = new WorldLane[]
        {
            // Dave's capital loop.
            new() { LaneId = "l-dh-df1", FromSectorId = "d-home", ToSectorId = "d-flank-1", TypeId = "corridor", Length = 800, Width = 1000 },
            new() { LaneId = "l-df1-df2", FromSectorId = "d-flank-1", ToSectorId = "d-flank-2", TypeId = "corridor", Length = 800, Width = 1000 },
            new() { LaneId = "l-dh-df2", FromSectorId = "d-home", ToSectorId = "d-flank-2", TypeId = "corridor", Length = 900, Width = 1000 },
            // Dave's severable waist.
            new() { LaneId = "l-df2-do", FromSectorId = "d-flank-2", ToSectorId = "d-outpost", TypeId = "rift", Length = 1000, Width = 800 },

            // The corridor, one long chain.
            new() { LaneId = "l-df2-c1", FromSectorId = "d-flank-2", ToSectorId = "corridor-1", TypeId = "rift", Length = 1200, Width = 1000 },
            new() { LaneId = "l-c1-c2", FromSectorId = "corridor-1", ToSectorId = "corridor-2", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-c2-c3", FromSectorId = "corridor-2", ToSectorId = "corridor-3", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-c3-c4", FromSectorId = "corridor-3", ToSectorId = "corridor-4", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-c4-hot", FromSectorId = "corridor-4", ToSectorId = "hot-ground", TypeId = "rift", Length = 1000, Width = 1000 },
            new() { LaneId = "l-hot-c5", FromSectorId = "hot-ground", ToSectorId = "corridor-5", TypeId = "rift", Length = 1000, Width = 1000 },
            new() { LaneId = "l-c5-c6", FromSectorId = "corridor-5", ToSectorId = "corridor-6", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-c6-c7", FromSectorId = "corridor-6", ToSectorId = "corridor-7", TypeId = "rift", Length = 1100, Width = 1000 },
            new() { LaneId = "l-c7-zf2", FromSectorId = "corridor-7", ToSectorId = "z-flank-2", TypeId = "rift", Length = 1200, Width = 1000 },

            // Zomboss's severable waist and capital loop — the mirror of Dave's.
            new() { LaneId = "l-zf2-zo", FromSectorId = "z-flank-2", ToSectorId = "z-outpost", TypeId = "rift", Length = 1000, Width = 800 },
            new() { LaneId = "l-zf2-zf1", FromSectorId = "z-flank-2", ToSectorId = "z-flank-1", TypeId = "corridor", Length = 800, Width = 1000 },
            new() { LaneId = "l-zh-zf2", FromSectorId = "z-home", ToSectorId = "z-flank-2", TypeId = "corridor", Length = 900, Width = 1000 },
            new() { LaneId = "l-zf1-zh", FromSectorId = "z-flank-1", ToSectorId = "z-home", TypeId = "corridor", Length = 800, Width = 1000 }
        }.OrderBy(l => l.LaneId, StringComparer.Ordinal).ToList(),

        Entities = new WorldEntity[]
        {
            new()
            {
                EntityId = "e-dave-legion-1", Kind = WorldEntityKind.Legion, OwnerFactionId = Dave,
                AtSectorId = "d-home", Stance = "march", MovementRemaining = 1000,
                // L27 (spec-loam-legions.md): same minimum edit as first-light's own legion — one
                // bearer plus a generous starting reserve (placeholder, not harness-tuned).
                CarriedLoam = 500,
                Members = new WorldEntityMember[]
                {
                    new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                    new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
                    new() { SpeciesId = "paperzombie", Level = 1, Hp = 110, Role = WorldEntityMemberRole.Bearer }
                }
            },
            new()
            {
                EntityId = "e-zomboss-band-1", Kind = WorldEntityKind.Warband, OwnerFactionId = Zomboss,
                AtSectorId = "z-home", Stance = "march", MovementRemaining = 1000,
                Members = new WorldEntityMember[]
                {
                    new() { SpeciesId = "normalzombie", Level = 3, Hp = 200 },
                    new() { SpeciesId = "conezombie", Level = 3, Hp = 200 }
                }
            }
        }.OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
    };
}
