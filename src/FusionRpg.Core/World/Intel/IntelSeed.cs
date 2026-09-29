namespace FusionRpg.Core.World.Intel;

/// <summary>
/// What the player already knows when a world is created (spec-world-intel.md §Remembering).
///
/// A template author writes `AuthoredIntel` on each sector — "you have heard rumours of Frost Mire"
/// — and that becomes the player faction's opening belief, stamped turn zero. Every other faction
/// starts blind, which is fair: nobody has looked at anything yet.
///
/// Without this the map opens completely dark, `first-light`'s carefully authored opening reads as
/// six identical silhouettes, and the first three turns are spent walking in circles.
/// </summary>
public static class IntelSeed
{
    public static IReadOnlyList<FactionIntel> ForTemplate(WorldState world)
    {
        // Everyone starts knowing what they can already see — a warband standing in a sector at turn
        // zero obviously knows it is there, and without this the map opens with factions unable to
        // describe the ground under their own feet until a turn has been committed.
        var seen = IntelRecorder.Observe(world, world, turn: 0)
            .ToDictionary(f => f.FactionId, StringComparer.Ordinal);

        var player = world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player);

        var intel = new List<FactionIntel>();
        foreach (var faction in world.Factions.OrderBy(f => f.FactionId, StringComparer.Ordinal))
        {
            var sectors = seen.TryGetValue(faction.FactionId, out var already)
                ? already.Sectors.ToList()
                : new List<IntelSnapshot>();

            // The player gets the author's opening on top: "you have heard rumours of Frost Mire".
            // The *better* of the two wins, not the more recent — an author who says a sector was
            // scouted is describing a survey somebody already made, and a distant glimpse today must
            // not downgrade it to hearsay.
            if (player != null && string.Equals(faction.FactionId, player.FactionId, StringComparison.Ordinal))
                foreach (var sector in world.Sectors)
                {
                    var authored = DetailOf(sector.AuthoredIntel);
                    if (authored == SectorSight.None) continue;

                    var existing = sectors.FindIndex(s =>
                        string.Equals(s.SectorId, sector.SectorId, StringComparison.Ordinal));

                    if (existing < 0)
                        sectors.Add(Snapshot(world, sector, authored));
                    else if (authored > sectors[existing].Detail)
                        sectors[existing] = Snapshot(world, sector, authored);
                }

            intel.Add(new FactionIntel
            {
                FactionId = faction.FactionId,
                Sectors = sectors.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
            });
        }

        return intel;
    }

    /// <summary>
    /// Anything the author called scouted or watched was surveyed; a rumour is what a neighbour
    /// told you, which is exactly a glimpse.
    /// </summary>
    static SectorSight DetailOf(IntelState authored) => authored switch
    {
        IntelState.Watched or IntelState.Scouted => SectorSight.Full,
        IntelState.Rumored => SectorSight.Glimpse,
        _ => SectorSight.None
    };

    static IntelSnapshot Snapshot(WorldState world, WorldSector sector, SectorSight detail) => new()
    {
        SectorId = sector.SectorId,
        LastSeenTurn = 0,
        Detail = detail,
        OwnerFactionId = sector.OwnerFactionId,
        Phase = sector.Phase,
        Climate = sector.Climate,
        DangerBand = sector.DangerBand,
        DevelopmentLevel = detail == SectorSight.Full ? sector.DevelopmentLevel : 0,
        RecruitStock = detail == SectorSight.Full ? sector.RecruitStock : 0,
        ProjectId = detail == SectorSight.Full ? sector.ProjectId : null,
        ProjectTurnsRemaining = detail == SectorSight.Full ? sector.ProjectTurnsRemaining : null,
        Slots = detail == SectorSight.Full
            ? sector.Slots
                .OrderBy(sl => sl.SlotIndex)
                .Select(sl => new RememberedSlot
                {
                    SlotIndex = sl.SlotIndex,
                    SlotTypeId = sl.SlotTypeId,
                    Element = sl.Element,
                    GuardWaveId = sl.GuardWaveId,
                    State = sl.State,
                    GuardState = sl.GuardState,
                    // world-stage W7: found missing here while proving `ConstructionTurnsRemaining`
                    // reaches a client — `IntelRecorder.Observe`'s equivalent builder
                    // (`IntelRecorder.cs:107-117`) already carries both; this turn-zero bootstrap
                    // path had silently dropped them since `StructureId` was introduced.
                    StructureId = sl.StructureId,
                    ConstructionTurnsRemaining = sl.ConstructionTurnsRemaining
                })
                .ToList()
            : Array.Empty<RememberedSlot>(),
        Forces = world.Entities
            .Where(e => string.Equals(e.AtSectorId, sector.SectorId, StringComparison.Ordinal))
            .OrderBy(e => e.EntityId, StringComparer.Ordinal)
            .Select(e =>
            {
                var strength = ForceStrength.Of(e);
                return new RememberedForce
                {
                    EntityId = e.EntityId,
                    OwnerFactionId = e.OwnerFactionId,
                    Kind = e.Kind,
                    Exact = detail == SectorSight.Full,
                    Strength = detail == SectorSight.Full ? strength : 0,
                    BandIndex = StrengthBandCatalog.Of(strength).Index
                };
            })
            .ToList()
    };
}
