using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>A knot on the recovery curve: at this conceded budget, this effective budget.</summary>
public readonly record struct FrameMixKnot(long MinorityMilli, long EffectiveBudgetMilli);

public sealed class FrameMixTuningRejection : Exception
{
    /// <summary>The namespaced content rule this refusal carries, for a caller that wants the code.</summary>
    public AtomRejection Rejection { get; }

    public FrameMixTuningRejection(string ruleId, string detail)
        : base($"{ruleId}: {detail}")
    {
        ThresholdEvaluator.EnsureRegistered();
        Rejection = AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// `gk-core/data/tuning/item-frame-mix.v1.json`, parsed. Pure — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."), matching <see cref="ItemRarityTuning"/> and
/// <see cref="FusionRpg.Core.Items.Drops.DropVolumeTuning"/>.
///
/// <para><b>The tunable is the knot list, not the shape.</b> A balance pass moves knots with a file
/// save; it cannot reinstate a step function, because <see cref="Validate"/> refuses a knot list that
/// is not strictly increasing. That refusal is the module's highest-consequence guard: pinned only at
/// its ends, a step firing at <c>minorityMilli = 40</c> passes every endpoint assertion, and D3's
/// anti-cherry-pick mechanism then costs one cheap role instead of half a body — the suite stays green
/// and the design is gone.</para>
/// </summary>
public readonly record struct FrameMixTuning(
    long HybridCoreBudgetTotalMilli,
    long ParityMinorityMilli,
    IReadOnlyList<FrameMixKnot> Knots,
    string TierContainerIdFormat,
    string TierSourceKey,
    int TierPriority)
{
    public static FrameMixTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed", "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed", $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var core = Obj(root, "hybridCore");
            var curve = Obj(root, "recoveryCurve");
            var tiers = Obj(root, "tiers");

            if (!curve.TryGetProperty("knots", out var knotsEl) || knotsEl.ValueKind != JsonValueKind.Array)
                throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed",
                    "recoveryCurve.knots is missing or not an array");

            var knots = new List<FrameMixKnot>();
            foreach (var k in knotsEl.EnumerateArray())
                knots.Add(new FrameMixKnot(Long(k, "minorityMilli"), Long(k, "effectiveBudgetMilli")));

            var parsed = new FrameMixTuning(
                HybridCoreBudgetTotalMilli: Long(core, "budgetTotalMilli"),
                ParityMinorityMilli: Long(core, "parityMinorityMilli"),
                Knots: knots,
                TierContainerIdFormat: Str(tiers, "containerIdFormat"),
                TierSourceKey: Str(tiers, "sourceKey"),
                TierPriority: (int)Long(tiers, "priority"));

            Validate(parsed);
            return parsed;
        }
    }

    /// <summary>
    /// The four structural properties, each refused by its own rule id so a balance pass reads which
    /// one it broke rather than "invalid curve".
    /// </summary>
    public static void Validate(FrameMixTuning t)
    {
        if (t.HybridCoreBudgetTotalMilli <= 0)
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed",
                $"hybridCore.budgetTotalMilli {t.HybridCoreBudgetTotalMilli} must be positive");

        // Structural, and derived rather than chosen: the smaller of two disjoint sums over a total
        // is at most half of it. A parity above half means the curve's top knot is unreachable.
        if (t.ParityMinorityMilli * 2 != t.HybridCoreBudgetTotalMilli)
            throw new FrameMixTuningRejection("threshold.frame-mix-parity-not-half",
                $"parityMinorityMilli {t.ParityMinorityMilli} is not half of budgetTotalMilli " +
                $"{t.HybridCoreBudgetTotalMilli} — parity IS an even split of the conceded budget, " +
                "and the smaller of two disjoint sums can never exceed half the total");

        if (t.Knots.Count < 2)
            throw new FrameMixTuningRejection("threshold.frame-mix-curve-too-few-knots",
                $"recoveryCurve.knots has {t.Knots.Count} knot(s); a curve needs at least its two ends");

        if (t.Knots[0].MinorityMilli != 0)
            throw new FrameMixTuningRejection("threshold.frame-mix-curve-floor-missing",
                $"the first knot is at minorityMilli {t.Knots[0].MinorityMilli}, not 0 — " +
                "f(0) is the floor D3 rules and it must be stated, never extrapolated");

        if (t.Knots[0].EffectiveBudgetMilli != t.HybridCoreBudgetTotalMilli)
            throw new FrameMixTuningRejection("threshold.frame-mix-curve-floor-wrong",
                $"f(0) = {t.Knots[0].EffectiveBudgetMilli}, but the floor is the hybrid core's own " +
                $"{t.HybridCoreBudgetTotalMilli}‰ — a body that concedes nothing recovers nothing");

        var last = t.Knots[^1];
        if (last.MinorityMilli != t.ParityMinorityMilli)
            throw new FrameMixTuningRejection("threshold.frame-mix-curve-parity-missing",
                $"the last knot is at minorityMilli {last.MinorityMilli}, not parity " +
                $"{t.ParityMinorityMilli} — the curve must be defined over the whole reachable range");

        // Recovery is capped at +200‰ of an 800‰ body: parity, and no further. Expressed as a ratio
        // of the total rather than as a literal 1000, so a role-table rescale stays consistent.
        var parityBudget = t.HybridCoreBudgetTotalMilli + t.ParityMinorityMilli / 2;
        if (last.EffectiveBudgetMilli != parityBudget)
            throw new FrameMixTuningRejection("threshold.frame-mix-curve-parity-wrong",
                $"f(parity) = {last.EffectiveBudgetMilli}, but parity is {parityBudget}‰ " +
                $"({t.HybridCoreBudgetTotalMilli} + {t.ParityMinorityMilli}/2) — a genuine half-and-half " +
                "body recovers exactly what it conceded and nothing beyond it");

        for (var i = 1; i < t.Knots.Count; i++)
        {
            var prev = t.Knots[i - 1];
            var cur = t.Knots[i];

            if (cur.MinorityMilli <= prev.MinorityMilli)
                throw new FrameMixTuningRejection("threshold.frame-mix-curve-knots-unordered",
                    $"knot {i} is at minorityMilli {cur.MinorityMilli}, not above knot {i - 1}'s " +
                    $"{prev.MinorityMilli} — knots must ascend, and two knots at one x is a jump " +
                    "discontinuity: a step function wearing knots");

            // THE guard. A flat interval is a prefix of the range that is free, and the cheapest
            // possible cheat: concede one light role, collect the whole bonus.
            if (cur.EffectiveBudgetMilli <= prev.EffectiveBudgetMilli)
                throw new FrameMixTuningRejection("threshold.frame-mix-curve-not-strictly-increasing",
                    $"f({prev.MinorityMilli}) = {prev.EffectiveBudgetMilli} and " +
                    $"f({cur.MinorityMilli}) = {cur.EffectiveBudgetMilli}: the curve is flat or falling " +
                    "over that interval. Every permille conceded must buy something, or some prefix of " +
                    "the range is free and D3's mechanism costs one cheap role");
        }
    }

    /// <summary>
    /// The frame-mix consumer's breakpoints, derived from the knots above zero — never authored twice.
    /// Ordinals ascend in <c>minorityMilli</c>, and the id is zero-padded so ordinal string order
    /// equals numeric order in the actor effect list (<c>ORDER BY … i.container_id ASC</c>,
    /// <c>RpgStore.AtomInstances.cs</c>).
    /// </summary>
    public IReadOnlyList<ThresholdBreakpoint> TierBreakpoints()
    {
        var result = new List<ThresholdBreakpoint>();
        var ordinal = 0;
        foreach (var knot in Knots)
        {
            if (knot.MinorityMilli == 0) continue;   // the floor grants nothing
            ordinal++;
            result.Add(new ThresholdBreakpoint(knot.MinorityMilli,
                ThresholdContainerIds.FrameMixTier(ordinal)));
        }
        return result;
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed",
                $"missing or non-object '{key}'");
        return el;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed",
                $"missing or non-numeric '{key}'");
        return el.GetInt64();
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FrameMixTuningRejection("threshold.frame-mix-tuning-malformed",
                $"missing or non-string '{key}'");
        return el.GetString()!;
    }
}
