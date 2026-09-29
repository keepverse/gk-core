using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>The deterministic resolver (item/spec-requirement-profiles.md §"Deterministic resolver"):
/// pure, seeded, replay-identical for identical inputs. One fixed `SeededRng.DeriveStream` name per
/// draw, so an unrelated draw never shifts another. Candidates sort ordinally before every draw, so
/// source order never matters. A failed resolution returns a NAMED rejection — never a fallback
/// profile, never a partial persistence.
///
/// <para>Rarity chooses the distribution-matrix row ONLY. After the kind is drawn, every further
/// lookup takes the power band alone — the code takes no rarity parameter past this point, which is
/// what mechanically preserves rarity overlap while preventing a hidden rarity-to-cost multiplier.</para>
/// </summary>
public static class RequirementProfileResolver
{
    /// <summary>Must equal the tuning file's `resolverRevision` — a resolver change without a tuning
    /// revision is a different resolver wearing the old number.</summary>
    public const int Revision = 1;

    public static RequirementProfile Resolve(
        ulong rollSeed,
        int resolverRevision,
        long catalogRevision,
        int tuningRevision,
        long contentTheta,
        long pTheta,
        PowerVector power,
        string rarityId,
        IReadOnlyList<string>? buildFavorPool,
        RequirementProfileTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (resolverRevision != Revision)
            throw new RequirementProfileRejection("revision-mismatch",
                $"resolver revision {resolverRevision} does not match this resolver's {Revision}");
        if (tuningRevision != tuning.TuningRevision)
            throw new RequirementProfileRejection("revision-mismatch",
                $"tuning revision {tuningRevision} does not match the file's {tuning.TuningRevision}");
        if (catalogRevision < 0)
            throw new RequirementProfileRejection("revision-missing",
                $"catalog revision {catalogRevision} is not a resolvable content revision");
        if (contentTheta < 0)
            throw new RequirementProfileRejection("content-theta-negative",
                $"content theta {contentTheta} is not a reachable content position");
        if (pTheta < 0)
            throw new RequirementProfileRejection("power-negative",
                $"P(Theta) {pTheta} is not a reachable magnitude");

        var powerBand = tuning.PowerBands.FirstOrDefault(b => pTheta >= b.MinP && pTheta <= b.MaxP)?.Id
            ?? throw new RequirementProfileRejection("power-out-of-band",
                $"P(Theta) {pTheta} falls in no power band — the bands must cover the reachable range");
        if (!tuning.ProfileMatrix.TryGetValue((rarityId, powerBand, FocusOf(power)), out var cell))
            throw new RequirementProfileRejection("impossible-matrix-cell",
                $"no matrix cell for ({rarityId}, {powerBand}, {FocusOf(power)}) — an uncovered combination is a tuning gap, not a silent default");

        var kind = DrawKind(rollSeed, cell);
        return kind switch
        {
            RequirementProfileKind.None => new RequirementProfile(null, null, null, kind),
            RequirementProfileKind.Level => new RequirementProfile(
                CheckedLevel(DrawBand(rollSeed, "item.requirements:v1:threshold", BandOf(tuning.LevelThresholds, powerBand, "level"))),
                null, null, kind),
            RequirementProfileKind.Fixed => new RequirementProfile(null,
                new BuildTrialClause(DrawAptitude(rollSeed, buildFavorPool),
                    DrawBand(rollSeed, "item.requirements:v1:threshold", BandOf(tuning.FixedThresholds, powerBand, "fixed")),
                    null),
                null, kind),
            RequirementProfileKind.Ratio => new RequirementProfile(null,
                new BuildTrialClause(DrawAptitude(rollSeed, buildFavorPool), null,
                    DrawBand(rollSeed, "item.requirements:v1:threshold", BandOf(tuning.RatioThresholds, powerBand, "ratio"))),
                null, kind),
            RequirementProfileKind.Sustained => new RequirementProfile(null, null,
                DrawUpkeep(rollSeed, tuning, powerBand), kind),
            RequirementProfileKind.Jackpot => new RequirementProfile(
                CheckedLevel(DrawBand(rollSeed, "item.requirements:v1:threshold", BandOf(tuning.LevelThresholds, powerBand, "level"))),
                null,
                DrawUpkeep(rollSeed, tuning, powerBand), kind),
            _ => throw new RequirementProfileRejection("kind-unreachable", $"unhandled profile kind {kind}"),
        };
    }

    /// <summary>Actor level is an `int` contest input, not a magnitude — the tuning holds small
    /// whole levels, narrowed here with `checked` (a narrowing that overflows throws, never wraps).</summary>
    static int CheckedLevel(long value) => checked((int)value);

    static IReadOnlyList<WeightedValue> BandOf(
        IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> table, string powerBand, string what) =>
        table.TryGetValue(powerBand, out var band)
            ? band
            : throw new RequirementProfileRejection("band-missing",
                $"no {what} band for power band '{powerBand}' — a missing band is a tuning gap, not an empty one");

    /// <summary>The focus band is the power vector's own dominant category — ties read the vector's
    /// canonical order, never hash order.</summary>
    internal static string FocusOf(PowerVector power)
    {
        var best = 0;
        for (var i = 1; i < PowerVector.Categories.Length; i++)
            if (power[i] > power[best])
                best = i;
        return PowerVector.Categories[best];
    }

    static RequirementProfileKind DrawKind(ulong rollSeed, IReadOnlyDictionary<RequirementProfileKind, long> cell)
    {
        var ordered = cell.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).ToList();
        var total = ordered.Sum(kv => kv.Value);
        if (total <= 0)
            throw new RequirementProfileRejection("weight-total-nonpositive",
                "matrix cell weights sum to zero — nothing is drawable, and a silent kind is not drawn");
        var draw = DrawULong(rollSeed, "item.requirements:v1:profile", total);
        foreach (var (kind, weight) in ordered)
        {
            if (draw < weight) return kind;
            draw -= weight;
        }
        throw new RequirementProfileRejection("weight-unreachable",
            "weighted draw fell through — the total moved under it, which cannot happen on a stable table");
    }

    static string DrawAptitude(ulong rollSeed, IReadOnlyList<string>? pool)
    {
        if (pool is null || pool.Count == 0)
            throw new RequirementProfileRejection("pool-missing",
                "a fixed/ratio profile needs a build-favor pool and none was supplied");
        var candidates = pool.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (candidates.Count != pool.Count)
            throw new RequirementProfileRejection("pool-duplicate",
                "the build-favor pool carries a duplicate candidate");
        foreach (var id in candidates)
            if (!AptitudeCatalog.IsAptitudeId(id))
                throw new RequirementProfileRejection("pool-unknown-aptitude",
                    $"build-favor pool names '{id}', which is not one of the twelve aptitudes");
        return candidates[(int)DrawULong(rollSeed, "item.requirements:v1:aptitude", candidates.Count)];
    }

    static long DrawBand(ulong rollSeed, string stream, IReadOnlyList<WeightedValue> band)
    {
        if (band.Count == 0)
            throw new RequirementProfileRejection("band-empty",
                "a threshold band with no entries draws nothing — an empty band is a tuning gap");
        var ordered = band.OrderBy(e => e.Value).ToList();
        var total = ordered.Sum(e => e.Weight);
        if (total <= 0)
            throw new RequirementProfileRejection("band-weight-nonpositive",
                "threshold band weights sum to zero");
        var draw = DrawULong(rollSeed, stream, total);
        foreach (var entry in ordered)
        {
            if (draw < entry.Weight) return entry.Value;
            draw -= entry.Weight;
        }
        throw new RequirementProfileRejection("band-unreachable",
            "band draw fell through on a stable table");
    }

    static string DrawId(ulong rollSeed, string stream, IReadOnlyList<WeightedId> ids)
    {
        var ordered = ids.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var total = ordered.Sum(e => e.Weight);
        if (total <= 0)
            throw new RequirementProfileRejection("id-weight-nonpositive",
                "id weights sum to zero");
        var draw = DrawULong(rollSeed, stream, total);
        foreach (var entry in ordered)
        {
            if (draw < entry.Weight) return entry.Id;
            draw -= entry.Weight;
        }
        throw new RequirementProfileRejection("id-unreachable",
            "id draw fell through on a stable table");
    }

    static UpkeepClause DrawUpkeep(
        ulong rollSeed, RequirementProfileTuning tuning, string powerBand)
    {
        if (!tuning.UpkeepEligible.TryGetValue(powerBand, out var eligible) || !eligible)
            throw new RequirementProfileRejection("upkeep-ineligible",
                $"power band '{powerBand}' carries no maintenance — selecting upkeep here would invent it");
        if (!tuning.ReserveBands.TryGetValue(powerBand, out var reserves)
            || !tuning.CostBands.TryGetValue(powerBand, out var costs)
            || !tuning.PeriodBands.TryGetValue(powerBand, out var periods))
            throw new RequirementProfileRejection("upkeep-band-missing",
                $"power band '{powerBand}' has no upkeep bands");
        return new UpkeepClause(
            DrawId(rollSeed, "item.requirements:v1:resource", tuning.UpkeepResources),
            DrawBand(rollSeed, "item.requirements:v1:reserve", reserves),
            DrawBand(rollSeed, "item.requirements:v1:threshold", costs),
            DrawBand(rollSeed, "item.requirements:v1:period", periods));
    }

    static long DrawULong(ulong rollSeed, string stream, long total)
    {
        if (total <= 0)
            throw new RequirementProfileRejection("draw-total-nonpositive",
                $"stream '{stream}' has nothing to draw from");
        if (total > int.MaxValue)
            throw new RequirementProfileRejection("draw-total-unbounded",
                $"stream '{stream}' weight total {total} exceeds the unbiased draw range — no honest weight table is this large");
        return SeededRng.DeriveStream(rollSeed, stream).NextInt((int)total);
    }
}

