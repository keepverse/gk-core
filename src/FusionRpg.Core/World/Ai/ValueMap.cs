using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Topology;

namespace FusionRpg.Core.World.Ai;

/// <summary>How much of each axis a policy cares about. Per-mille; only the ratios matter.</summary>
public sealed record ValueWeights
{
    public int Yield { get; init; }  // overflow-bounded: a per-mille desirability weight/score, 0..1000 ('only the ratios matter'), not a loam quantity
    public int Strategic { get; init; }
    public int Defensibility { get; init; }
    public int Cost { get; init; }
    public int Risk { get; init; }
    public int Curiosity { get; init; }

    /// <summary>Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/ai.v3.json's valueMap.defaultWeights.</summary>
    public static ValueWeights Default => WorldAiPolicy.DefaultWeights;
}

/// <summary>What one sector is worth, and why.</summary>
public readonly record struct SectorValue(
    long Total, int Yield, int Strategic, int Defensibility, int Cost, int Risk, int Curiosity,  // overflow-bounded: a per-mille desirability weight/score, 0..1000 ('only the ratios matter'), not a loam quantity
    long Overextension, long HabitabilityPenalty = 0)
{
    /// <summary>The line a turn report shows: enough to argue with, short enough to read.</summary>
    public string Explain() =>
        $"value {Total} (yield {Yield}, strategic {Strategic}, risk {Risk}, cost {Cost})"
        + (Overextension > 0 ? $" − overextended {Overextension}" : "")
        + (HabitabilityPenalty > 0 ? $" − barren {HabitabilityPenalty}" : "");
}

/// <summary>
/// Worth, relative to this empire (spec-ai-commander.md §ValueMap).
///
/// Six axes, each normalised to per-mille so none wins by accident of scale, weighted by the policy
/// and then **reduced by an overextension penalty that can drive the total below zero**. That last
/// part is not a detail: the classic 4X failure is blobbing outward until nothing is defensible, and
/// the only cure is for bad ground to score *worse than nothing* rather than merely least-best.
///
/// Everything reads belief. A sector glimpsed from next door carries no slots, so its yield is
/// honestly zero rather than optimistically guessed — you find out what a place is worth by standing
/// on it, which is what keeps claiming a rich-looking sector a gamble.
/// </summary>
public static class ValueMap
{
    /// <summary>How attractive unknown ground is, against the average of what you do know.</summary>
    public static int OptimismMilli => WorldAiPolicy.Tuning.ValueMap.OptimismMilli;

    /// <summary>What holding ground outside your own supply costs you, per-mille of the whole.</summary>
    public static int OverextensionPenaltyMilli => WorldAiPolicy.Tuning.ValueMap.OverextensionPenaltyMilli;

    /// <summary>
    /// What barren ground costs an `Expand`/`Take` decision, per-mille of the whole (spec-loam-ai.md).
    /// Same starting placeholder as <see cref="OverextensionPenaltyMilli"/> — the shape is mirrored
    /// deliberately, the value is its own independently-tunable lever, not borrowed from it.
    /// </summary>
    public static int HabitabilityPenaltyMilli => WorldAiPolicy.Tuning.ValueMap.HabitabilityPenaltyMilli;

    public static IReadOnlyDictionary<string, SectorValue> For(
        IWorldView view,
        IReadOnlyDictionary<string, long> threat,
        IReadOnlyDictionary<string, int>? reach = null,
        INeedVector? needs = null,
        ValueWeights? weights = null)
    {
        needs ??= UniformNeeds.Instance;
        weights ??= ValueWeights.Default;

        var supplied = BelievedSupply.ConnectedSectors(view);
        var graph = MarchGraph.Of(view);
        var strategic = StrategicByS(view, supplied);
        var worstThreat = threat.Count == 0 ? 0 : threat.Values.Max();

        // Curiosity is the *mean of what you know*, so it cannot be computed per sector in isolation
        // — and it is what stops the unknown being worth zero, which is what would stop anyone ever
        // going to look.
        var known = view.SectorIds.Select(id => YieldOf(view, id, needs)).Where(y => y > 0).ToList();
        var meanKnownYield = known.Count == 0 ? 0 : known.Sum() / known.Count;
        var curiosityValue = meanKnownYield * OptimismMilli / 1000;

        var result = new Dictionary<string, SectorValue>(StringComparer.Ordinal);

        foreach (var sectorId in view.SectorIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var believed = view.Believed(sectorId);
            var unknown = believed is null;

            var yield = unknown ? 0 : YieldOf(view, sectorId, needs);
            var curiosity = unknown ? curiosityValue : 0;
            var strategicAxis = strategic.TryGetValue(sectorId, out var s) ? s : 0;
            var defensibility = DefensibilityOf(view, graph, sectorId);
            var cost = CostOf(view, sectorId, reach);
            var risk = worstThreat == 0
                ? 1000
                : (int)(1000 - Math.Min(1000, threat.TryGetValue(sectorId, out var t) ? t * 1000 / worstThreat : 0));

            var weighted =
                (long)yield * weights.Yield +
                (long)strategicAxis * weights.Strategic +
                (long)defensibility * weights.Defensibility +
                (long)cost * weights.Cost +
                (long)risk * weights.Risk +
                (long)curiosity * weights.Curiosity;

            var divisor = weights.Yield + weights.Strategic + weights.Defensibility
                          + weights.Cost + weights.Risk + weights.Curiosity;
            var total = divisor == 0 ? 0 : weighted / divisor;

            // Would holding this leave it hanging outside the chain? Applied last and large enough
            // to go through zero.
            var overextension = WouldOverextend(view, graph, sectorId, supplied)
                ? total * OverextensionPenaltyMilli / 1000
                : 0;

            // A second post-hoc gate, mirroring Overextension's own shape and applied to the same
            // base `total` rather than chained after it — chaining would let an already-negative,
            // overextended total flip back toward positive once multiplied a second time.
            // Fires only once the sector has actually been surveyed (its slots are known): unseen
            // or merely-glimpsed ground stays governed by the curiosity axis, exactly as it already
            // is for `yield` — penalizing what you have not looked at would kill exploration outright.
            var habitabilityPenalty = believed is { Slots.Count: > 0 } surveyed
                                       && !Habitability.For(surveyed.Slots.Select(sl =>
                                           (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining)))
                ? total * HabitabilityPenaltyMilli / 1000
                : 0;

            result[sectorId] = new SectorValue(
                total - overextension - habitabilityPenalty,
                yield, strategicAxis, defensibility, cost, risk, curiosity, overextension, habitabilityPenalty);
        }

        return result;
    }

    /// <summary>What the ground produces, as this empire values it. Zero for anything not surveyed.</summary>
    static int YieldOf(IWorldView view, string sectorId, INeedVector needs)  // overflow-bounded: returns Math.Min(1000, ...), a clamped score; the sum inside is long
    {
        if (view.Believed(sectorId) is not { } believed || believed.Slots.Count == 0) return 0;

        long total = 0;
        foreach (var slot in believed.Slots)
        {
            if (!SlotTypeCatalog.IsKnown(slot.SlotTypeId)) continue;

            var kind = SlotTypeCatalog.Get(slot.SlotTypeId).Kind;
            var basis = SlotValueCatalog.Of(kind);

            total += (long)basis * needs.ForSlotKind(kind) / 1000
                                 * needs.ForElement(slot.Element) / 1000;
        }

        return (int)Math.Min(1000, total / Math.Max(1, believed.Slots.Count));
    }

    /// <summary>
    /// What it would cost the empire to lose this, normalised against **its own** worst case.
    ///
    /// Against its own rather than the map's, because the map's maximum is not knowable under fog,
    /// and "how bad would this be for me" is the question a garrison decision actually asks.
    /// </summary>
    static IReadOnlyDictionary<string, int> StrategicByS(IWorldView view, IReadOnlySet<string> supplied)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (supplied.Count < 3) return result;   // two sectors cannot be split by losing one

        var costs = ReconnectionCost.For(
            view.SectorIds, view.Lanes, MarchGraph.ClimateOf(view), supplied);

        var worst = costs.Count == 0 ? 0 : costs.Values.Max();
        if (worst <= 0) return result;

        foreach (var (sectorId, cost) in costs)
            result[sectorId] = (int)Math.Min(1000, cost * 1000 / worst);

        return result;
    }

    /// <summary>A chokepoint is cheap to keep; a crossroads is not.</summary>
    static int DefensibilityOf(IWorldView view, LaneGraph graph, string sectorId)
    {
        if (!graph.Contains(sectorId)) return 1000;   // nothing can reach it, so nothing can take it

        var exposed = graph.Edges
            .Where(e => string.Equals(e.FromSectorId, sectorId, StringComparison.Ordinal))
            .Select(e => e.ToSectorId)
            .Distinct(StringComparer.Ordinal)
            .Count(neighbour => view.Believed(neighbour) is not { } b
                                || !string.Equals(b.OwnerFactionId, view.FactionId, StringComparison.Ordinal));

        // Every open side is a way in. Four is already indefensible, so the scale ends there.
        return Math.Max(0, 1000 - exposed * 250);
    }

    /// <summary>What taking it would actually cost: the march, and whatever is still guarding it.</summary>
    static int CostOf(IWorldView view, string sectorId, IReadOnlyDictionary<string, int>? reach)
    {
        var turns = reach is null ? 0
            : reach.TryGetValue(sectorId, out var t) ? t
            : 99;                                       // no route at all is the most expensive answer there is

        var guards = view.Believed(sectorId) is { } believed
            ? believed.Slots.Count(s => s.GuardState == GuardState.Intact)
            : 0;

        return Math.Max(0, 1000 - turns * 200 - guards * 150);
    }

    /// <summary>
    /// Would taking this leave it dangling? True when nothing beside it is already in supply, so the
    /// holding would be an island the moment it was made.
    /// </summary>
    static bool WouldOverextend(IWorldView view, LaneGraph graph, string sectorId, IReadOnlySet<string> supplied)
    {
        if (supplied.Count == 0) return false;      // no chain to be outside of
        if (supplied.Contains(sectorId)) return false;

        foreach (var edge in graph.Edges)
            if (string.Equals(edge.FromSectorId, sectorId, StringComparison.Ordinal)
                && supplied.Contains(edge.ToSectorId))
                return false;

        return true;
    }
}
