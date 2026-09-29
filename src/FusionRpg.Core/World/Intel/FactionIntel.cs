using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Intel;

/// <summary>
/// Effective weight for fog-of-war reporting only: health still in the fight, scaled by experience.
/// Relocated here from the deleted `PlaceholderBattleResolver` (`actor-hub-and-combat-power-solid-fixing`,
/// T21) — that class used the identical formula to invent battle winners, which this program's own
/// "no invented winners" rule forbids; Intel's own use is different in kind, not degree: a
/// <see cref="RememberedForce"/> was always an estimate a fogged observer forms, never a claim about
/// who would win a fight, so the same crude weight is honest here in a way it never was as a combat
/// resolver. Never call this to decide a battle.
/// </summary>
public static class ForceStrength
{
    public static long Of(WorldEntity entity)
    {
        long total = 0;
        foreach (var m in entity.Members)
            total += Math.Max(0, m.Hp - m.Wounds) * (long)Math.Max(1, m.Level);
        return total;
    }
}

/// <summary>
/// A force as somebody else remembers it. Exact if they stood on the ground with it; a
/// <see cref="StrengthBandCatalog">band</see> if they only glimpsed it from next door.
/// </summary>
public sealed record RememberedForce
{
    public string EntityId { get; init; } = "";
    public string OwnerFactionId { get; init; } = "";
    public WorldEntityKind Kind { get; init; }

    /// <summary>True when the observer stood in the sector and counted.</summary>
    public bool Exact { get; init; }

    /// <summary>Meaningful only when <see cref="Exact"/>.</summary>
    public long Strength { get; init; }

    public int BandIndex { get; init; }

    /// <summary>What a decision should plan against: the exact figure, or the band's ceiling.</summary>
    public long Defensive => Exact ? Strength : StrengthBandCatalog.ByIndex(BandIndex).Ceiling;

    /// <summary>What a decision should expect: the exact figure, or the band's midpoint.</summary>
    public long Offensive => Exact ? Strength : StrengthBandCatalog.ByIndex(BandIndex).Midpoint;
}

/// <summary>A slot as it was last seen. Only ever recorded from a survey — a glimpse sees no slots.</summary>
public sealed record RememberedSlot
{
    public int SlotIndex { get; init; }
    public string SlotTypeId { get; init; } = "";
    public ElementTypeId? Element { get; init; }

    /// <summary>
    /// Which encounter dens here, as far as the observer could tell. Kept because `GuardState`
    /// alone cannot distinguish "cleared, and something used to live here" from "never guarded" —
    /// and because someone who walked the ground would know what they were looking at.
    /// </summary>
    public string? GuardWaveId { get; init; }

    /// <summary>Whether it has been claimed, worked out, or ruined — readable from the ground.</summary>
    public SlotState State { get; init; } = SlotState.Intact;

    public GuardState GuardState { get; init; }

    /// <summary>
    /// As visible as the slot itself (spec-structure-substrate.md) — not owner-gated, so it is
    /// captured on any survey the same way <see cref="SlotTypeId"/> already is.
    /// </summary>
    public string? StructureId { get; init; }

    /// <summary>
    /// A structure's construction state, visible on the same terms as the structure itself
    /// (spec-loam-structures.md) — not owner-gated either.
    /// </summary>
    public int? ConstructionTurnsRemaining { get; init; }
}

/// <summary>
/// One sector as one faction last saw it (spec-world-intel.md §Remembering).
///
/// Deliberately not the whole sector record: remembering the truth exactly would make fog cosmetic.
/// What is here is what an observer could plausibly carry away and report.
/// </summary>
public sealed record IntelSnapshot
{
    public string SectorId { get; init; } = "";

    /// <summary>The turn this was taken. The whole ladder is derived from it.</summary>
    public int LastSeenTurn { get; init; }

    /// <summary>How well it was seen when the snapshot was taken — a glimpse carries no slots.</summary>
    public SectorSight Detail { get; init; }

    public string? OwnerFactionId { get; init; }
    public SectorPhase Phase { get; init; }
    public ElementTypeId? Climate { get; init; }
    public int DangerBand { get; init; }

    /// <summary>
    /// The Fracture's local strength (spec-loam-model.md) — terrain, like <see cref="Climate"/> and
    /// <see cref="DangerBand"/>, so it is captured on any sighting and survives once scouted.
    /// Deliberately alone here: <c>LoamStock</c> is live state and never belongs in belief at all —
    /// there is no field for it to leak through.
    /// </summary>
    public int FractureIntensityMilli { get; init; } = 1000;

    /// <summary>
    /// How built-up it was when you last stood on it. Zero for a glimpse — you read development off
    /// the ground, not from one sector away — and the wire says so rather than reporting a bare zero
    /// that looks identical to undeveloped.
    /// </summary>
    public int DevelopmentLevel { get; init; }

    /// <summary>Ordered by slot index. Empty for a glimpse.</summary>
    public IReadOnlyList<RememberedSlot> Slots { get; init; } = Array.Empty<RememberedSlot>();

    /// <summary>
    /// world-map W45 (spec-sector-development.md §1): what you counted last time you stood here.
    /// Owner-only economy state, same gate as <see cref="Slots"/> — a glimpse tells you who holds
    /// the ground, not what it has banked. Zero for a glimpse, never a stale number.
    /// </summary>
    public long RecruitStock { get; init; }

    /// <summary>A sector-wide project as it stood on your last full survey — same full-detail-only gate as <see cref="RecruitStock"/>.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Null for a glimpse, or for a finished project; must not be set without <see cref="ProjectId"/>.</summary>
    public int? ProjectTurnsRemaining { get; init; }

    /// <summary>Ordered by entity id.</summary>
    public IReadOnlyList<RememberedForce> Forces { get; init; } = Array.Empty<RememberedForce>();
}

/// <summary>
/// Everything one faction believes about the map. Ordered by sector id, and holding only sectors it
/// has actually seen — an absent entry *is* "never heard of it".
/// </summary>
public sealed record FactionIntel
{
    public string FactionId { get; init; } = "";
    public IReadOnlyList<IntelSnapshot> Sectors { get; init; } = Array.Empty<IntelSnapshot>();

    public IntelSnapshot? Of(string sectorId)
    {
        foreach (var snapshot in Sectors)
            if (string.Equals(snapshot.SectorId, sectorId, StringComparison.Ordinal))
                return snapshot;

        return null;
    }
}

/// <summary>
/// The four-state ladder, **derived** from `(lastSeenTurn, currentTurn, seenThisTurn)` rather than
/// stored — so it cannot drift from the data underneath it.
/// </summary>
public static class IntelLadder
{
    /// <summary>Memory counts as fresh for this long before it decays to rumour.</summary>
    public const int FreshTurns = 5;

    public static IntelState StateOf(IntelSnapshot? snapshot, int currentTurn, bool seenThisTurn)
    {
        if (seenThisTurn) return IntelState.Watched;
        if (snapshot is null) return IntelState.Unknown;

        var age = currentTurn - snapshot.LastSeenTurn;
        return age <= FreshTurns ? IntelState.Scouted : IntelState.Rumored;
    }

    /// <summary>How many turns stale, floored at zero. What the map stamps on a remembered card.</summary>
    public static int AgeOf(IntelSnapshot snapshot, int currentTurn) =>
        Math.Max(0, currentTurn - snapshot.LastSeenTurn);
}
