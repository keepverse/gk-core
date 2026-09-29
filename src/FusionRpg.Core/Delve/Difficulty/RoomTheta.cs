using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>
/// The parent world's terms Θ_content needs beyond the room's own band — passed through from the
/// world, never composed here (spec-difficulty-ladder.md §1: "WorldTier, ZombossLevel,
/// RealmsAdvanced pass through from the parent world; this module composes the band and nothing
/// else").
/// </summary>
public sealed record ParentWorldTerms(int WorldTier, int ZombossLevel, int RealmsAdvanced);

/// <summary>
/// The domain-side inputs to band composition (spec-difficulty-ladder.md §1, §5). A minimal,
/// honest stand-in for the domain anchor's C# projection — <c>domain-catalog</c> (wave 4) has not
/// landed yet, so this names exactly the two fields this module reads rather than inventing a
/// fuller domain type nothing else uses today.
/// </summary>
public sealed record DomainThetaInputs(int EntranceBand, bool IsOnceEntry, string? PermadeathFromRungOverride = null);

/// <summary>Thrown by <see cref="RoomTheta.Compose"/> when the composed band falls below
/// <c>difficulty.minOfferedBand</c> — refuse, never reach <c>ClampNonNegative</c> (§6).</summary>
public sealed class RungNotOffered : Exception
{
    public string RungId { get; }
    public int Band { get; }
    public RungNotOffered(string rungId, int band)
        : base($"rung '{rungId}' composes band {band}, below the offered floor — refused, not clamped.")
    {
        RungId = rungId;
        Band = band;
    }
}

public sealed record RoomTheta(ContentContext Context, int Theta, int Band);

/// <summary>
/// The first production composer of Θ_content (spec-difficulty-ladder.md §1). One call into the
/// shipped <see cref="PowerIndexComposer.ContentExplain"/> — this module owns the BAND, nothing
/// downstream of it. No private curve: contests read Θ, magnitudes read <c>P(Θ)</c>
/// (ssot-power-scale.md §10).
/// </summary>
public static class RoomThetaComposer
{
    public static RoomTheta Compose(
        PowerTuning power, DungeonTuning dungeon, DomainThetaInputs domain, DifficultyRungTuning rung,
        int row, int tailPlus, bool isBoss, ParentWorldTerms world)
    {
        // int counts of bands; the composer widens to long internally (PowerIndexComposer.cs).
        var band = checked(
            domain.EntranceBand
            + row / dungeon.DepthRowsPerBandStep
            + rung.BandDelta
            + (domain.IsOnceEntry ? dungeon.Domain.OnceEntry.BandDelta : 0)
            + tailPlus * dungeon.DifficultyTail.BandStepPerPlus
            + (isBoss ? dungeon.DepthBossBandDelta : 0));

        // §6: refuse-not-clamp. A rung offered to the player never reaches this composer with a
        // band below the floor in the first place (RungOffer's job) — this is the backstop for a
        // caller that composes directly (a boss row recompute, a preview) without going through
        // the offer path first.
        if (band < dungeon.MinOfferedBand) // difficulty.minOfferedBand (band 0 is "safe ground" — a delve is never safe ground)
            throw new RungNotOffered(RungIdOf(dungeon, rung), band);

        var ctx = new ContentContext(band, world.WorldTier, world.ZombossLevel, world.RealmsAdvanced);
        var theta = PowerIndexComposer.ContentExplain(power, ctx).Total;
        return new RoomTheta(ctx, theta, band);
    }

    static string RungIdOf(DungeonTuning dungeon, DifficultyRungTuning rung)
    {
        foreach (var (id, def) in dungeon.Rungs)
            if (ReferenceEquals(def, rung)) return id;
        return "unknown";
    }
}
