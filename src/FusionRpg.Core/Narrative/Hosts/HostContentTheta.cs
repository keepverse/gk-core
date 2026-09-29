using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Narrative.Hosts;

/// <summary>
/// One host's content-side Θ, with the context it was composed from
/// (spec-host-content-theta.md §3). The context travels with the number so a caller never has to
/// rebuild it — and so nothing downstream can quietly recompose from different inputs.
/// </summary>
public sealed record HostTheta(ContentContext Context, int Theta);

/// <summary>
/// Every storylet host's Θ_content, through the ONE composer and the SSOT formula
/// (spec-host-content-theta.md, npc-story-events NR2.18; ssot-power-scale.md §5). This is a wiring
/// module: it adds no weight, no curve and no second composer — each arm builds one
/// <see cref="ContentContext"/> from inputs the caller already holds and calls
/// <see cref="PowerIndexComposer.ContentExplain"/> once.
///
/// <para><b>This file is the only place in <c>FusionRpg.Core.Narrative</c> that may construct a
/// <see cref="ContentContext"/></b> (spec §SOLID: "no private curve"). A host that needs Θ adds a
/// <c>For*</c> arm here; a host that builds its own context has forked the ladder, and
/// <c>HostContentThetaTests</c>' source scan fails it by name.</para>
///
/// <para>The Delve is the exception that proves the rule: its rooms already compose Θ through
/// <see cref="RoomThetaComposer"/>, so <see cref="ForDelveRoom"/> passes that result through and
/// never recomposes (spec §3, §Boundaries "Never: a second room-Θ composer").</para>
/// </summary>
public static class HostContentTheta
{
    /// <summary>
    /// The homeworld's danger band. It is the shipped homeworld band, not a new number:
    /// <see cref="PowerIndexComposer"/>'s own band table names "homeworld 0" beside the sector
    /// bands, so the homeworld is a band like any other rather than a special case in this module.
    /// Structural, not tunable — a place either has danger or it does not, and a balance pass that
    /// wanted the homeworld to be dangerous would be redefining the homeworld.
    /// </summary>
    const int HomeworldDangerBand = 0;

    /// <summary>World slot hosts and <c>world.petition</c>: the sector's own danger band.</summary>
    public static HostTheta ForSector(PowerTuning power, WorldSector sector, ParentWorldTerms world)
    {
        ArgumentNullException.ThrowIfNull(sector);
        return Compose(power, sector.DangerBand, world);
    }

    /// <summary>
    /// <c>expedition.return</c>: the dispatch tier's danger band, loaded from the expeditions tuning
    /// (spec-host-content-theta.md §4, spec-expedition-lead-host.md §6). The tier owns the band, so
    /// this arm reads it and composes — it never derives a band from duration or battle count.
    /// </summary>
    public static HostTheta ForExpedition(PowerTuning power, ExpeditionTierDef tier, ParentWorldTerms world)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return Compose(power, tier.DangerBand, world);
    }

    /// <summary>
    /// <c>sanctum.hub</c>: danger 0 — the homeworld is safe ground. Equivalent to a band-0 sector,
    /// asserted as such rather than implemented as a copy of it.
    /// </summary>
    public static HostTheta ForHomeworld(PowerTuning power, ParentWorldTerms world)
        => Compose(power, HomeworldDangerBand, world);

    /// <summary>
    /// <c>delve.*</c>: the room's already-composed Θ and context, passed through unchanged. The
    /// context reference is preserved, so a caller can prove this arm composed nothing.
    /// </summary>
    public static HostTheta ForDelveRoom(RoomTheta room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new HostTheta(room.Context, room.Theta);
    }

    static HostTheta Compose(PowerTuning power, int dangerBand, ParentWorldTerms world)
    {
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(world);

        var context = new ContentContext(dangerBand, world.WorldTier, world.ZombossLevel, world.RealmsAdvanced);
        return new HostTheta(context, PowerIndexComposer.ContentExplain(power, context).Total);
    }
}
