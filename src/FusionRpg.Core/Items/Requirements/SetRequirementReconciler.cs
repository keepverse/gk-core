using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Frozen set envelope" — the pure catalog-time
/// reconciler. It resolves ONE envelope (one aptitude, one upkeep resource, one period, one cost
/// ceiling) and then resolves each member's floor UNDER those ceilings, so separately generated member
/// requirements can never demand incompatible builds or maintenance pools.
///
/// <para><b>Deterministic and order-independent by construction.</b> Candidate pools and roles are
/// ordinally sorted and de-duplicated before any draw, and every draw takes its own
/// <see cref="SeededRng.DeriveStream"/> name, so an unrelated draw never shifts another and an input
/// shuffle cannot change the result. A failure is a NAMED
/// <see cref="SetRequirementUnsatisfiable"/> — never a repaired envelope, and never a partial one.</para>
///
/// <para><b>No new balance table.</b> Ceilings are the maxima of the tuning's own existing threshold
/// bands for the resolved power band; the upkeep ceiling is <c>SetEnvelopeBudget</c>; the period is a
/// draw from the tuning's own period band. The one value this resolver does NOT invent is rarity: the
/// spec's own signature carries no rarity, so the member KINDS come from the plan
/// (<see cref="SetMemberPlan"/>) and only the floors are drawn here.</para>
/// </summary>
public static class SetRequirementReconciler
{
    /// <summary>Must equal the tuning file's <c>resolverRevision</c> — a resolver change without a
    /// tuning revision is a different resolver wearing the old number (the same rule module 23's own
    /// resolver states).</summary>
    public const int Revision = 1;

    /// <summary>One fixed stream name per draw, matching module 23's convention.</summary>
    const string StreamAptitude = "item.set.requirements:v1:aptitude";

    const string StreamResource = "item.set.requirements:v1:resource";
    const string StreamPeriod = "item.set.requirements:v1:period";
    const string StreamReserve = "item.set.requirements:v1:reserve";
    const string StreamThreshold = "item.set.requirements:v1:threshold";

    public static SetRequirementEnvelope Resolve(
        SetRequirementPlan plan,
        ulong planSeed,
        long catalogRevision,
        int tuningRevision,
        long pTheta,
        IReadOnlyList<string>? buildFavorPool,
        RequirementProfileTuning tuning)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (tuningRevision != tuning.TuningRevision)
            throw new SetRequirementUnsatisfiable("revision-mismatch",
                $"tuning revision {tuningRevision} does not match the file's {tuning.TuningRevision}");
        if (catalogRevision < 0)
            throw new SetRequirementUnsatisfiable("revision-missing",
                $"catalog revision {catalogRevision} is not a resolvable content revision");
        if (pTheta < 0)
            throw new SetRequirementUnsatisfiable("power-negative", $"P(Theta) {pTheta} is not a reachable magnitude");

        var identity = SetIdentityRequirement.Validate(plan);
        var band = PowerBandFor(tuning, pTheta);

        // Roles are ordinally sorted BEFORE any draw, so the member order an input happened to carry
        // cannot change which member draws which floor.
        var members = plan.Members.OrderBy(m => ItemRoles.Id(m.Role), StringComparer.Ordinal).ToList();

        var needsAptitude = members.Exists(m => m.Kind is RequirementProfileKind.Fixed or RequirementProfileKind.Ratio);
        var needsUpkeep = members.Exists(m => m.Kind is RequirementProfileKind.Sustained or RequirementProfileKind.Jackpot);

        var favoredAptitude = needsAptitude ? DrawAptitude(planSeed, buildFavorPool) : null;

        string? upkeepResource = null;
        long upkeepReserve = 0;
        long upkeepPeriod = 0;
        if (needsUpkeep)
        {
            if (!tuning.UpkeepEligible.TryGetValue(band, out var eligible) || !eligible)
                throw new SetRequirementUnsatisfiable("upkeep-ineligible",
                    $"power band '{band}' carries no maintenance — selecting it for '{plan.SetId}' would invent it");
            upkeepResource = DrawId(planSeed, StreamResource, tuning.UpkeepResources);
            upkeepReserve = DrawBand(planSeed, StreamReserve, BandOf(tuning.ReserveBands, band, "reserve"));
            upkeepPeriod = DrawBand(planSeed, StreamPeriod, BandOf(tuning.PeriodBands, band, "period"));
            if (upkeepPeriod <= 0)
                throw new SetRequirementUnsatisfiable("period-nonpositive",
                    $"power band '{band}' drew a non-positive upkeep period ({upkeepPeriod})");
        }

        var fixedCeiling = Ceiling(tuning.FixedThresholds, band, "fixed");
        var ratioCeiling = Ceiling(tuning.RatioThresholds, band, "ratio");

        var resolved = new List<SetMemberProfile>(members.Count);
        foreach (var member in members)
        {
            var profile = member.Kind switch
            {
                RequirementProfileKind.None => new RequirementProfile(null, null, null, member.Kind),
                RequirementProfileKind.Level => new RequirementProfile(
                    CheckedLevel(DrawBand(planSeed, StreamThreshold, BandOf(tuning.LevelThresholds, band, "level"))),
                    null, null, member.Kind),
                RequirementProfileKind.Fixed => new RequirementProfile(null,
                    new BuildTrialClause(
                        favoredAptitude!,
                        DrawBand(planSeed, StreamThreshold, BandOf(tuning.FixedThresholds, band, "fixed")),
                        null), null, member.Kind),
                RequirementProfileKind.Ratio => new RequirementProfile(null,
                    new BuildTrialClause(
                        favoredAptitude!, null,
                        DrawBand(planSeed, StreamThreshold, BandOf(tuning.RatioThresholds, band, "ratio"))),
                    null, member.Kind),
                RequirementProfileKind.Sustained => new RequirementProfile(null, null,
                    new UpkeepClause(upkeepResource!,
                        upkeepReserve,
                        DrawBand(planSeed, StreamThreshold, BandOf(tuning.CostBands, band, "cost")),
                        upkeepPeriod), member.Kind),
                RequirementProfileKind.Jackpot => new RequirementProfile(
                    CheckedLevel(DrawBand(planSeed, StreamThreshold, BandOf(tuning.LevelThresholds, band, "level"))),
                    null,
                    new UpkeepClause(upkeepResource!,
                        upkeepReserve,
                        DrawBand(planSeed, StreamThreshold, BandOf(tuning.CostBands, band, "cost")),
                        upkeepPeriod), member.Kind),
                _ => throw new SetRequirementUnsatisfiable("kind-unreachable", $"unhandled member kind {member.Kind}"),
            };

            // Ceilings are enforced, never repaired: a plan whose kind draws above the band's own
            // ceiling is a tuning/plan disagreement, and clamping it would hide exactly that.
            if (profile.BuildTrial is { MinimumPoints: { } points } && fixedCeiling is { } fc && points > fc)
                throw new SetRequirementUnsatisfiable("fixed-floor-above-ceiling",
                    $"role '{ItemRoles.Id(member.Role)}' floor {points} exceeds the fixed ceiling {fc}");
            if (profile.BuildTrial is { MinimumShareMilli: { } share } && ratioCeiling is { } rc && share > rc)
                throw new SetRequirementUnsatisfiable("ratio-floor-above-ceiling",
                    $"role '{ItemRoles.Id(member.Role)}' share {share}‰ exceeds the ratio ceiling {rc}‰");

            resolved.Add(new SetMemberProfile(member.Role, profile));
        }

        var envelope = new SetRequirementEnvelope(
            plan.SetId, Revision, catalogRevision, tuningRevision, identity, resolved,
            favoredAptitude, fixedCeiling, ratioCeiling, upkeepResource,
            tuning.SetEnvelopeBudget, upkeepReserve, upkeepPeriod);

        var memberTotal = envelope.MemberUpkeepTotal;
        if (memberTotal > tuning.SetEnvelopeBudget)
            throw new SetRequirementUnsatisfiable("upkeep-budget-exceeded",
                $"set '{plan.SetId}' members declare {memberTotal} maintenance against a ceiling of {tuning.SetEnvelopeBudget}");

        return envelope;
    }

    /// <summary>Every non-empty build clause in one envelope must name the SAME aptitude, and every
    /// upkeep clause the same resource and period — the "one build/resource direction per set" the
    /// acceptance line asks for, as a check rather than a promise.</summary>
    public static void RequireOneDirection(SetRequirementEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        string? aptitude = null;
        string? resource = null;
        long? period = null;
        foreach (var member in envelope.Members)
        {
            if (member.Profile.BuildTrial is { } build)
                Require(ref aptitude, build.AptitudeId, "aptitude", envelope.SetId, member.Role);
            if (member.Profile.Upkeep is { } upkeep)
            {
                Require(ref resource, upkeep.ResourceId, "resource", envelope.SetId, member.Role);
                if (period is { } seen && seen != upkeep.PeriodTicks)
                    throw new SetRequirementUnsatisfiable("member-period-disagrees",
                        $"set '{envelope.SetId}' member '{ItemRoles.Id(member.Role)}' has period {upkeep.PeriodTicks}, not {seen}");
                period = upkeep.PeriodTicks;
            }
        }
    }

    static void Require(ref string? seen, string value, string what, string setId, ItemRole role)
    {
        if (seen is null) { seen = value; return; }
        if (!string.Equals(seen, value, StringComparison.Ordinal))
            throw new SetRequirementUnsatisfiable($"member-{what}-disagrees",
                $"set '{setId}' member '{ItemRoles.Id(role)}' names {what} '{value}', not '{seen}'");
    }

    static string PowerBandFor(RequirementProfileTuning tuning, long pTheta)
    {
        foreach (var band in tuning.PowerBands)
            if (pTheta >= band.MinP && pTheta <= band.MaxP) return band.Id;
        throw new SetRequirementUnsatisfiable("power-out-of-band",
            $"P(Theta) {pTheta} falls in no power band — an uncovered combination is a tuning gap, not a silent default");
    }

    static int CheckedLevel(long value) => checked((int)value);

    static IReadOnlyList<WeightedValue> BandOf(
        IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> table, string band, string what) =>
        table.TryGetValue(band, out var rows) && rows.Count > 0
            ? rows
            : throw new SetRequirementUnsatisfiable($"band-missing-{what}",
                $"power band '{band}' has no {what} band — a missing band is a tuning gap, not an empty one");

    static long? Ceiling(IReadOnlyDictionary<string, IReadOnlyList<WeightedValue>> table, string band, string what)
    {
        if (!table.TryGetValue(band, out var rows) || rows.Count == 0) return null;
        long max = long.MinValue;
        foreach (var row in rows) if (row.Value > max) max = row.Value;
        return max;
    }

    static string DrawAptitude(ulong seed, IReadOnlyList<string>? pool)
    {
        if (pool is null || pool.Count == 0)
            throw new SetRequirementUnsatisfiable("pool-missing",
                "a set with a build clause needs a build-favor pool and none was supplied");
        var candidates = pool.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (candidates.Count != pool.Count)
            throw new SetRequirementUnsatisfiable("pool-duplicate", "the build-favor pool carries a duplicate candidate");
        foreach (var id in candidates)
            if (!AptitudeCatalog.IsAptitudeId(id))
                throw new SetRequirementUnsatisfiable("pool-unknown-aptitude",
                    $"build-favor pool names '{id}', which is not one of the twelve aptitudes");
        return candidates[(int)DrawULong(seed, StreamAptitude, candidates.Count)];
    }

    static string DrawId(ulong seed, string stream, IReadOnlyList<WeightedId> ids)
    {
        var ordered = ids.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var total = ordered.Sum(e => e.Weight);
        if (total <= 0) throw new SetRequirementUnsatisfiable("id-weight-nonpositive", "id weights sum to zero");
        var draw = DrawULong(seed, stream, total);
        foreach (var entry in ordered)
        {
            if (draw < entry.Weight) return entry.Id;
            draw -= entry.Weight;
        }
        throw new SetRequirementUnsatisfiable("id-unreachable", "id draw fell through on a stable table");
    }

    static long DrawBand(ulong seed, string stream, IReadOnlyList<WeightedValue> band)
    {
        var ordered = band.OrderBy(e => e.Value).ToList();
        var total = ordered.Sum(e => e.Weight);
        if (total <= 0) throw new SetRequirementUnsatisfiable("band-weight-nonpositive", "band weights sum to zero");
        var draw = DrawULong(seed, stream, total);
        foreach (var entry in ordered)
        {
            if (draw < entry.Weight) return entry.Value;
            draw -= entry.Weight;
        }
        throw new SetRequirementUnsatisfiable("band-unreachable", "band draw fell through on a stable table");
    }

    static long DrawULong(ulong seed, string stream, long total)
    {
        if (total <= 0) throw new SetRequirementUnsatisfiable("draw-total-nonpositive", $"stream '{stream}' has nothing to draw from");
        if (total > int.MaxValue)
            throw new SetRequirementUnsatisfiable("draw-total-unbounded",
                $"stream '{stream}' weight total {total} exceeds the unbiased draw range");
        return SeededRng.DeriveStream(seed, stream).NextInt((int)total);
    }
}
