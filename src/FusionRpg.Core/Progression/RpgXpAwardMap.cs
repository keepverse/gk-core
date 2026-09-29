using FusionRpg.Core.Activity;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Progression;

/// <summary>Maps Activity fact kinds to XP awards (pure; used by server apply).</summary>
public static class RpgXpAwardMap
{
    /// <summary>
    /// One XP award. <c>Delta</c> is whole XP (`long`) — the ONE place a scaled award is rounded, so
    /// no fraction ever reaches the persisted total. <c>PowerScale</c> stays `double`: it is carried
    /// into the kill ledger's audit payload and is never itself a magnitude. <c>ScopeKey</c>
    /// (species-build T1.1) carries a human-readable key alongside <c>TypeId</c> for kinds where the
    /// int id alone isn't self-describing — today only <see cref="RpgActorKinds.Species"/>, which
    /// writes it into `rpg_actor_progression.scope_key`; every other kind leaves it null.
    /// </summary>
    public readonly record struct Award(string Kind, int TypeId, long Delta, string Reason, double PowerScale = 1.0, string? ScopeKey = null);

    /// <summary>
    /// The single rounding point for a scaled award — half away from zero, matching
    /// <c>PowerLadder</c>'s own end-rounding convention so both ladders round the same direction.
    /// Today <see cref="NoKillPowerScaleYet"/> is exactly 1.0 and this is the identity; it exists so
    /// that when content-scale supplies a real multiplier the fraction dies here rather than in the
    /// XP total (progression-shape-audit-2026-09-04.md §4.1).
    /// </summary>
    static long ScaledAward(long baseAward, double scale)
    {
        var scaled = baseAward * scale;
        if (double.IsNaN(scaled) || double.IsInfinity(scaled))
            throw new InvalidOperationException($"XP award scaled to a non-finite value: {baseAward} x {scale}");
        return checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    // T3.3 (power-plan.md, done 2026-08-24): RpgXpPowerScale deleted -- its documented future job
    // ("scale kill XP by zombie power") is exactly what Theta_content does (content-scale, T3.4+).
    // Structural, not a balance tunable: it is the multiplicative identity, required so this
    // deletion changes zero observable behaviour until content-scale gives it a real value.
    // Award.PowerScale itself stays -- RpgStore.Progression.cs still reads it into the kill ledger's
    // audit payload (rpg-progression.md: "powerScale for future audit"), a separate consumer from
    // the XP multiply this constant used to feed.
    static readonly double NoKillPowerScaleYet = 1.0;

    public static IReadOnlyList<Award> FromActivity(
        string factKind, string? resultRaw, int? typeId, string? payloadJson = null,
        string? sourceKind = null, string? sourceId = null)
    {
        switch (factKind)
        {
            case PvzActivityKinds.ZombieKilled:
            {
                var scale = NoKillPowerScaleYet;
                return new[]
                {
                    new Award(RpgActorKinds.Player, 0, ScaledAward(RpgXpAwards.Kill, scale), RpgXpReasons.Kill, scale)
                };
            }
            case PvzActivityKinds.MatchEnded:
                var result = PvzActivityKinds.NormalizeMatchResult(resultRaw);
                if (result == "defeat")
                    return new[] { new Award(RpgActorKinds.Player, 0, RpgXpAwards.Defeat, RpgXpReasons.Defeat) };
                return Array.Empty<Award>();
            case PvzActivityKinds.MowerUsed:
                return new[] { new Award(RpgActorKinds.Player, 0, RpgXpAwards.Mower, RpgXpReasons.Mower) };
            case PvzActivityKinds.PlantPlaced:
                return WithSpeciesPlacement(
                    new Award(RpgActorKinds.Plant, typeId ?? 0, RpgXpAwards.PlantPlace, RpgXpReasons.PlantPlace),
                    "plant", typeId, sourceKind, sourceId);
            case PvzActivityKinds.ZombieSpawned:
                return WithSpeciesPlacement(
                    new Award(RpgActorKinds.Zombie, typeId ?? 0, RpgXpAwards.ZombieSpawn, RpgXpReasons.ZombieSpawn),
                    "zombie", typeId, sourceKind, sourceId);
            default:
                return Array.Empty<Award>();
        }
    }

    /// <summary>
    /// `species-build` T1.2 — projects the SAME lawn placement fact onto the species' own progression
    /// row (spec-species-xp.md §2 "Lawn" source), alongside the existing PvZ-type award above, which
    /// stays untouched. `typeAward` is deliberately NOT `!pvzGame`-gated the way `award.Kind !=
    /// RpgActorKinds.Player` gates PvZ almanac types in `RpgStore.Progression.cs` — a species row is
    /// not a PvZ almanac type (spec's own ⛔ callout), so it levels from this fact regardless of which
    /// game mode produced it.
    ///
    /// <para>Best-effort, never a hard requirement: most progression tests never configure
    /// <see cref="CreatureSpeciesCatalog"/> or <see cref="SpeciesProgressionTuningHub"/>, and awarding the
    /// existing type/player XP above must keep working identically without either configured. A
    /// species award additionally requires a typed <c>EmpireGeneral</c> provenance claim; the legacy
    /// <paramref name="payloadJson"/> argument remains for call-site compatibility but is not a
    /// source discriminator.</para>
    /// </summary>
    static IReadOnlyList<Award> WithSpeciesPlacement(
        Award typeAward, string side, int? gameTypeId, string? sourceKind, string? sourceId)
    {
        // Source provenance is the only discriminator. A missing, malformed, unique, or commander
        // claim is ineligible for the empire fallback; payload fields such as `instanceId` and the
        // injector's opaque `source` token are deliberately ignored.
        if (!IsEmpireGeneralSource(sourceKind, sourceId, side, gameTypeId))
            return new[] { typeAward };
        if (gameTypeId is not { } tid || !CreatureSpeciesCatalog.IsConfigured || !SpeciesProgressionTuningHub.IsConfigured)
            return new[] { typeAward };

        var index = new LawnElementIndex(CreatureSpeciesCatalog.All);
        if (!index.TryGet(side, tid, out var species))
            return new[] { typeAward };

        return new[]
        {
            typeAward,
            new Award(RpgActorKinds.Species, species.CreatureTypeId,
                SpeciesProgressionTuningHub.Tuning.PlacementAward, typeAward.Reason,
                ScopeKey: species.SpeciesId)
        };
    }

    static bool IsEmpireGeneralSource(string? sourceKind, string? sourceId, string side, int? gameTypeId)
    {
        if (!string.Equals(sourceKind, CreatureProgressionSource.EmpireGeneralKind, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceId)
            || gameTypeId is not { } tid
            || !CreatureSpeciesCatalog.IsConfigured) return false;
        try
        {
            if (CreatureProgressionSource.Parse(sourceKind!, sourceId!)
                is not CreatureProgressionSource.EmpireGeneralSource general) return false;
            return new LawnElementIndex(CreatureSpeciesCatalog.All).TryGet(side, tid, out var species)
                && string.Equals(species.SpeciesId, general.SpeciesId, StringComparison.Ordinal);
        }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
    }
}
