using System.Text.Json;

namespace FusionRpg.Core.PassiveTree.State;

/// <summary>D26's `k` — aptitude points per unit of `t(t+1)/2`. Unit lives in the key name (ruling
/// R2) because the currency (aptitude points vs. skill points) is the single most-confused thing in
/// this program (spec-tree-plan.md §2, R-G0).</summary>
public sealed record TierLadderTuning(long ReqScalePoints);

/// <summary>`treeTotalPoints` is UNMEASURED (D42) — a working placeholder, not a real answer.
/// `branchSplitMilli` is the ‰ of `treeTotalPoints` that goes to the offensive branch (D6
/// symmetry).</summary>
public sealed record BudgetTuning(long TreeTotalPoints, long BranchSplitMilli);

/// <summary>`maxNodeShareMilli` is ‰ of ONE BRANCH budget (never the tree total — that was the
/// retired 91‰-of-total form's silent 2×, R5). `minTerminalWidth` is a count `P-1` recomputes the
/// ceiling from. `bandEdgesMilli` turns a bound `budgetShareMilli` into the ordinal `potencyBand`
/// the language stage sees — the model never sees a raw number.</summary>
public sealed record PotencyTuning(long MaxNodeShareMilli, long MinTerminalWidth, long[] BandEdgesMilli);

/// <summary>‰ of a tier's nodes that must be mechanism-shaped, at tier 1 and at `tierCount`
/// respectively — a RAMP, not a threshold (spec-tree-plan.md §4; the retired `floorMilli`/`capMilli`
/// names read as thresholds and are superseded).</summary>
public sealed record MechanismTuning(long RampStartMilli, long RampEndMilli);

/// <summary>`R-A1`'s ceiling on `max_a r_a(t) / min_a r_a(t)` across archetypes, in ‰.</summary>
public sealed record ArchetypeTuning(long RewardSpreadMaxRatioMilli);

/// <summary>D14/D40's ~2% target for the share of all nodes carrying an exclusion, in ‰.</summary>
public sealed record ExclusionTuning(long TargetShareMilli);

/// <summary>The `s = 1` reading of `req(tierCount)/3` — kept distinct from D29's `s = 0.542` reading
/// (Θ≈170) so the two conventions are never confused (A15).</summary>
public sealed record DesignTargetTuning(long ThetaAllIn);

/// <summary>D5's `Fmax` (per-mille multiplier; `1000` must be a legal, tested value since D5 is
/// provisional) and D8's `w` — the primary late-game blend weight between `H_nodes` and
/// `H_souls`.</summary>
public sealed record ConcentrationTuning(long FmaxMilli, long WMilli);

/// <summary>`Ws` — Θ contributed per soul level, in per-mille. UNMEASURED (D42); needs its own
/// `ssot-power-scale.md` §10.2 row before it ships for real (spec-tree-state.md §6.2).</summary>
public sealed record SoulTrackTuning(long ThetaPerSoulLevelMilli);

/// <summary>The rising unlock-cost wallet's `(first, step)` pair, in whole skill points — a
/// different currency from <see cref="TierLadderTuning"/>'s aptitude points (R1).</summary>
public sealed record UnlockCostTuning(long FirstPoints, long StepPoints);

/// <summary>D18's tree respec price — `RespecPolicy`'s own linear-escalation-on-a-count shape
/// (`price(count) = basePrice + basePrice*count*escalationPermille/1000`, spec-tree-state.md §5.1),
/// adopted with the tree's OWN counter and OWN tunable amount, never the species respec counter
/// (task C10's stated default; `species-build.v1.json`'s `respecBasePrice=50`,
/// `respecEscalationPermille=500` are the placeholder these mirror, both UNMEASURED for the tree
/// context specifically).</summary>
public sealed record RespecTuning(long BasePrice, long EscalationPermille);

/// <summary>Both counter-backed categories' own rate keys (OQ2 closed 2026-09-06 — neither reads
/// `AllocationScope.Aspect`; spec-gate-counters.md §5.3). The two must default equal and diverge
/// only with a stated reason — <see cref="RateDivergenceWhy"/> is `null` in the normal state.</summary>
public sealed record GateCountersTuning(
    long MasteryCurveFirstCount, long MasteryCurveStepCount,
    long ElementMasteryRatePoints, long StatusMasteryRatePoints,
    long FlushIntervalMs, string? RateDivergenceWhy);

/// <summary>
/// The passive-tree program's ONE tunable file (ruling R2) — `gk-core/data/tuning/passive-tree.v1.json`.
/// `tree-plan`, `tree-binder`, `tree-state`, `tree-resolve` and `gate-counters` each own a slice of
/// this one record; none of them get a second file, which is what let one dial (the potency ceiling)
/// grow two spellings across two specs before this file existed.
/// </summary>
public sealed record PassiveTreeTuning(
    int SchemaVersion, int Version,
    TierLadderTuning TierLadder, BudgetTuning Budget,
    long TreeShareMilli, long TreeBudgetMilli,
    PotencyTuning Potency, MechanismTuning Mechanism, ArchetypeTuning Archetype,
    ExclusionTuning Exclusion, string ArchetypeAssignment, DesignTargetTuning DesignTarget,
    ConcentrationTuning Concentration, SoulTrackTuning SoulTrack, UnlockCostTuning UnlockCost,
    RespecTuning Respec, GateCountersTuning GateCounters);

public sealed class PassiveTreeTuningRejection : Exception
{
    public PassiveTreeTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser, no file I/O (tunables-ssot.md §7.2). T5's "no built-in default" discipline applies
/// to every key: a missing one is a load rejection naming it, never a silent zero or a coerced
/// default — a balance-critical file that fails open is worse than one that fails loud.
/// </summary>
public static class PassiveTreeTuningLoader
{
    public static PassiveTreeTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PassiveTreeTuningRejection("passive-tree tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new PassiveTreeTuningRejection($"passive-tree tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var tierLadder = Obj(root, "tierLadder");
            var budget = Obj(root, "budget");
            var potency = Obj(root, "potency");
            var mechanism = Obj(root, "mechanism");
            var archetype = Obj(root, "archetype");
            var exclusion = Obj(root, "exclusion");
            var designTarget = Obj(root, "designTarget");
            var concentration = Obj(root, "concentration");
            var soulTrack = Obj(root, "soulTrack");
            var unlockCost = Obj(root, "unlockCost");
            var respec = Obj(root, "respec");
            var gateCounters = Obj(root, "gateCounters");

            var elementRate = Long(gateCounters, "elementMasteryRatePoints");
            var statusRate = Long(gateCounters, "statusMasteryRatePoints");
            var rateDivergenceWhy = OptionalString(gateCounters, "rateDivergenceWhy");
            if (elementRate != statusRate && string.IsNullOrWhiteSpace(rateDivergenceWhy))
                throw new PassiveTreeTuningRejection(
                    $"passive-tree tuning: gateCounters.elementMasteryRatePoints ({elementRate}) and " +
                    $"gateCounters.statusMasteryRatePoints ({statusRate}) diverge with no " +
                    "gateCounters.rateDivergenceWhy — spec-gate-counters.md §5.3's coupling");

            return new PassiveTreeTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                TierLadder: new TierLadderTuning(Long(tierLadder, "reqScalePoints")),
                Budget: new BudgetTuning(Long(budget, "treeTotalPoints"), Long(budget, "branchSplitMilli")),
                TreeShareMilli: Long(root, "treeShareMilli"),
                TreeBudgetMilli: Long(root, "treeBudgetMilli"),
                Potency: new PotencyTuning(
                    Long(potency, "maxNodeShareMilli"), Long(potency, "minTerminalWidth"),
                    LongArray(potency, "bandEdgesMilli")),
                Mechanism: new MechanismTuning(Long(mechanism, "rampStartMilli"), Long(mechanism, "rampEndMilli")),
                Archetype: new ArchetypeTuning(Long(archetype, "rewardSpreadMaxRatioMilli")),
                Exclusion: new ExclusionTuning(Long(exclusion, "targetShareMilli")),
                ArchetypeAssignment: StringVal(root, "archetypeAssignment"),
                DesignTarget: new DesignTargetTuning(Long(designTarget, "thetaAllIn")),
                Concentration: new ConcentrationTuning(Long(concentration, "fmaxMilli"), Long(concentration, "wMilli")),
                SoulTrack: new SoulTrackTuning(Long(soulTrack, "thetaPerSoulLevelMilli")),
                UnlockCost: new UnlockCostTuning(Long(unlockCost, "firstPoints"), Long(unlockCost, "stepPoints")),
                Respec: new RespecTuning(Long(respec, "basePrice"), Long(respec, "escalationPermille")),
                GateCounters: new GateCountersTuning(
                    Long(gateCounters, "masteryCurveFirstCount"), Long(gateCounters, "masteryCurveStepCount"),
                    elementRate, statusRate, Long(gateCounters, "flushIntervalMs"), rateDivergenceWhy));
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new PassiveTreeTuningRejection($"passive-tree tuning: missing or non-integer '{key}'");
        return v;
    }

    static string StringVal(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: missing or non-string '{key}'");
        return el.GetString()!;
    }

    static string? OptionalString(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: '{key}' is present but not a string");
        return el.GetString();
    }

    /// <summary>A whole-number reader that accepts JSON's `20` and `20.0` alike but REFUSES `20.5` —
    /// every field in this file is a per-mille/points/count magnitude, never a fractional one
    /// (docs/architecture/numeric-types.md: `long` for any magnitude, never a persisted `double`).</summary>
    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: missing or non-number '{key}'");
        if (el.TryGetInt64(out var exact)) return exact;

        var raw = el.GetDouble();
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw != Math.Floor(raw))
            throw new PassiveTreeTuningRejection(
                $"passive-tree tuning: '{key}' = {raw} is not a whole number");
        if (raw < long.MinValue || raw > long.MaxValue)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: '{key}' = {raw} is out of range for long");
        return (long)raw;
    }

    static long[] LongArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new PassiveTreeTuningRejection($"passive-tree tuning: missing or non-array '{key}'");
        var result = new long[el.GetArrayLength()];
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var v))
                throw new PassiveTreeTuningRejection($"passive-tree tuning: '{key}[{i}]' is not a whole number");
            result[i++] = v;
        }
        for (var j = 1; j < result.Length; j++)
            if (result[j] <= result[j - 1])
                throw new PassiveTreeTuningRejection(
                    $"passive-tree tuning: '{key}' must be strictly ascending — {result[j - 1]} then {result[j]} at index {j}");
        return result;
    }
}

/// <summary>
/// Host-only `Configure` (Server/`Program.cs`, or a test's inline construction); every consumer
/// reads <see cref="Tuning"/>. No built-in default — `data/tuning/passive-tree.v{n}.json` is the
/// only source (tunables-ssot.md §7.2), same shape as `AptitudeTuningHub`.
/// </summary>
public static class PassiveTreeTuningHub
{
    static PassiveTreeTuning? _tuning;

    public static void Configure(PassiveTreeTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static PassiveTreeTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "PassiveTreeTuningHub.Configure(...) has not run. Every passive-tree tunable read goes " +
        "through data/tuning/passive-tree.v{n}.json (tunables-ssot.md §7.2) — there is no built-in " +
        "default to fall back to.");
}
