using FusionRpg.Core.Balance.Guards;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §5 -- "The transfer artifact — the whole point of the module." Runs the THREE
/// columns (<c>duelClosedForm</c>, <c>duelTrials</c>, <c>squadTrials</c>) over the SAME four build
/// classes (<c>corner</c>, <c>hybrid2</c>, <c>hybrid3</c>, <c>spread</c>) and reports whether the
/// build-class ordering the closed form predicts survives at trial resolution and at squad scope.
///
/// <para><b>Resolved ambiguity, stated as a default.</b> The duel roster's 91 builds carry
/// <c>corner</c>/<c>hybrid2</c>/<c>hybrid3</c>/<c>spread</c> kinds directly (<see cref="SquadRoster.Duels"/>),
/// so the duel-side columns need no mapping. The 23-squad roster does NOT carry those four kinds — its
/// own kinds are <c>mono</c>/<c>posture</c>/<c>rainbow</c>/<c>mono-hybrid2</c>/<c>mono-hybrid3</c>/<c>mixed</c>
/// (§1.2's table), none of which is a single build class by itself except the "mono family": a
/// <c>mono-&lt;apt&gt;</c> squad IS six copies of one corner, a <c>mono-hybrid2-*</c> squad IS six copies
/// of one 2-way hybrid, and so on. That family is the ONLY part of the 23-squad roster whose members are
/// directly comparable, class-for-class, to a duel build — <c>posture-*</c>/<c>rainbow-*</c>/
/// <c>mixed-corner-spread</c> mix build shapes ACROSS their six actors and have no single build-class
/// identity to compare against a 1v1 corner. So <see cref="SquadBuildClass"/> classifies only the mono
/// family into the four duel build classes; the other six squads are still measured (they are part of
/// the full 506-cell <c>_squad-scope.json</c> matrix) but excluded from THIS four-row comparison, named
/// in <see cref="TransferRow.ExcludedSquadIds"/> so the exclusion is visible rather than silent.</para>
/// </summary>
public static class TransferReport
{
    public static readonly IReadOnlyList<string> BuildClasses = new[] { "corner", "hybrid2", "hybrid3", "spread" };

    /// <summary>See this class's own doc: only the "mono family" of the 23-squad roster maps onto a
    /// single duel build class. Order of checks matters -- the more specific prefixes are checked before
    /// the generic "mono-" one, which is what makes the 12 plain <c>mono-&lt;apt&gt;</c> ids fall through
    /// to "corner" rather than being caught by a "mono-hybrid2-" style check.</summary>
    public static string? SquadBuildClass(string squadId) => squadId switch
    {
        "mono-spread" => "spread",
        _ when squadId.StartsWith("mono-hybrid3-", StringComparison.Ordinal) => "hybrid3",
        _ when squadId.StartsWith("mono-hybrid2-", StringComparison.Ordinal) => "hybrid2",
        _ when squadId.StartsWith("mono-", StringComparison.Ordinal) => "corner",
        _ => null,
    };

    public sealed record ColumnCell(long WinShareMilli, long HalfWidthMilli);

    public sealed record TransferRow(
        string BuildClass,
        ColumnCell DuelClosedForm,
        ColumnCell DuelTrials,
        ColumnCell SquadTrials);

    public sealed record TransferResult(
        long Theta,
        long ScreeningTrials,
        long? RefineTrials,
        AllocationShape AllocationShape,
        IReadOnlyList<TransferRow> Rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> OrderingByColumn,
        bool Transfers,
        string? WhyNot,
        IReadOnlyList<string> ExcludedSquadIds,
        HarnessCoverage Coverage,
        IReadOnlyList<string> ClosedFormDriftWarnings,
        string DuelHash,
        string SquadHash);

    /// <summary>Mean and 95% half-width of a set of independent (value, half-width) estimates, per
    /// spec §9.2's resolution discipline. The half-width of a MEAN of independent estimates is
    /// <c>1.96 * sqrt(sum(SE_i^2)) / n</c> — the standard propagation-of-error rule, applied twice here
    /// (pair -> build, then build -> class) exactly as <c>gk-core/tools/HybridViability</c>'s own two-level mean
    /// (<c>mean[i]</c> then <c>MeanOf(kind)</c>) aggregates VALUES; this is the same aggregation with a
    /// half-width carried alongside every value, never a bare point estimate (F2 acceptance).</summary>
    static (long MeanMilli, long HalfWidthMilli) Aggregate(IReadOnlyList<(long ValueMilli, long HalfWidthMilli)> items)
    {
        if (items.Count == 0) return (0, 1000);
        var meanMilli = (long)Math.Round(items.Average(x => (double)x.ValueMilli));
        var sumStandardErrorSquared = items.Sum(x => Math.Pow(x.HalfWidthMilli / Resolution.Z95, 2));
        var standardErrorOfMean = Math.Sqrt(sumStandardErrorSquared) / items.Count;
        var halfWidth = (long)Math.Ceiling(Resolution.Z95 * standardErrorOfMean);
        return (meanMilli, halfWidth);
    }

    /// <summary>Per-build "mean vs the field" (every OTHER member of its own roster), the same shape
    /// <c>gk-core/tools/HybridViability/Program.cs</c>'s own <c>mean[i]</c> computes -- then grouped into the
    /// four build classes via <paramref name="classify"/>, which returns null for a roster member this
    /// column does not classify (the six excluded squad archetypes).</summary>
    static IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> PerClassMeans(
        IReadOnlyList<(string AttackerId, long ValueMilli, long HalfWidthMilli)> pairs,
        Func<string, string?> classify)
    {
        var perBuild = pairs
            .GroupBy(p => p.AttackerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Aggregate(g.Select(x => (x.ValueMilli, x.HalfWidthMilli)).ToList()), StringComparer.Ordinal);

        return perBuild
            .Select(kv => (Id: kv.Key, Class: classify(kv.Key), kv.Value))
            .Where(x => x.Class is not null)
            .GroupBy(x => x.Class!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Aggregate(g.Select(x => x.Value).ToList()), StringComparer.Ordinal);
    }

    /// <summary>Production entry point: the full 91-build duel roster and the full 23-squad roster
    /// (§1.2). See the roster-parametrized overload for why tests use a different roster.</summary>
    public static TransferResult Build(RunSpec screenSpec, AllocationShape shape, long? refineTrials, bool parallel) =>
        Build(screenSpec, SquadRoster.Duels(), SquadRoster.Squads(shape), shape, refineTrials, parallel);

    /// <summary>
    /// Roster-parametrized so tests can measure this class's own logic (classification, aggregation,
    /// ordering, the transfers verdict) without paying the full roster's cost. MEASURED, not assumed:
    /// <c>SquadMatch.ToBattleSetup</c> rebuilds a fresh <c>AptitudeResolver</c>/<c>DerivedStatRegistry</c>
    /// per trial per actor, which times at roughly 20ms per <c>BattleEngine.Resolve</c> call on this
    /// machine -- so the full roster (8,190 duel pairs + 506 squad pairs) takes on the order of MINUTES
    /// even at a single trial, and would take hours at the 3,000/40,000-trial counts §9.2 actually calls
    /// for. That per-trial-rebuild cost is <c>SquadMatch.cs</c>'s own (F1, independently verified) and is
    /// out of this task's scope to rewrite; this overload is how F2's own tests stay fast without
    /// touching it -- a small, REAL subset of the production roster (one build per class, taken from
    /// <see cref="SquadRoster.Duels"/>/<see cref="SquadRoster.Squads"/> directly, never a hand-built
    /// stand-in), not a synthetic fixture that could silently drift from what production classifies.
    /// </summary>
    public static TransferResult Build(
        RunSpec screenSpec, IReadOnlyList<NamedBuild> duelBuilds, IReadOnlyList<SquadBuild> squadBuilds,
        AllocationShape shape, long? refineTrials, bool parallel)
    {
        // --- duelClosedForm: DominanceGuard.Measure, the no-round-limit closed form, recomputed in
        // process (spec §5: "recomputed in process, not read from the checked-in file... one tuning
        // load, one Theta, so a drift shows up as a diff rather than propagating silently").
        var duelKindById = duelBuilds.ToDictionary(b => b.Id, b => b.Kind, StringComparer.Ordinal);
        var duelAllocations = duelBuilds.Select(b => b.Allocation).ToList();
        var closedForm = DominanceGuard.Measure(duelAllocations, screenSpec.Theta);
        // DominanceGuard.Measure names actors positionally ("corner{i}") regardless of what the build
        // actually is -- index maps back to duelBuilds[i] by construction (DominanceGuard's own doc).
        static int ActorIndex(string name) => int.Parse(name["corner".Length..]);
        var closedFormPairs = closedForm.Matrix
            .Select(arrow => (
                AttackerId: duelBuilds[ActorIndex(arrow.AttackerName)].Id,
                ValueMilli: (long)Math.Round(arrow.WinShareAttacker * 1000.0),
                HalfWidthMilli: 0L)) // deterministic closed form: no sampling noise, per spec §9.2's callout
            .ToList();
        var closedFormByClass = PerClassMeans(closedFormPairs, id => duelKindById.GetValueOrDefault(id));

        // --- duelTrials: the SAME 91 builds, resolved over BattleEngine.Resolve, two-stage screened.
        var duelRoster = duelBuilds.Select(RosterEntry.From).ToList();
        var duelScreening = Screening.RunWithRefine("duel", duelRoster, screenSpec, refineTrials, parallel);
        var duelTrialPairs = duelScreening.Run.Pairs
            .Select(p => (p.AttackerId, ValueMilli: p.WinShareMilli, HalfWidthMilli: Resolution.HalfWidthMilli(checked(p.Victories + p.Defeats))))
            .ToList();
        var duelTrialsByClass = PerClassMeans(duelTrialPairs, id => duelKindById.GetValueOrDefault(id));

        // --- squadTrials: the 23-squad roster, mono family mapped onto the same four classes (this
        // class's own doc). The other six squads are measured (part of _squad-scope.json) but excluded
        // from this comparison.
        var squadRoster = squadBuilds.Select(RosterEntry.From).ToList();
        var squadScreening = Screening.RunWithRefine("squad", squadRoster, screenSpec, refineTrials, parallel);
        var squadTrialPairs = squadScreening.Run.Pairs
            .Select(p => (p.AttackerId, ValueMilli: p.WinShareMilli, HalfWidthMilli: Resolution.HalfWidthMilli(checked(p.Victories + p.Defeats))))
            .ToList();
        var squadTrialsByClass = PerClassMeans(squadTrialPairs, SquadBuildClass);
        var excludedSquadIds = squadBuilds.Select(b => b.Id).Where(id => SquadBuildClass(id) is null)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

        var rows = BuildClasses.Select(cls => new TransferRow(
            cls,
            ToCell(closedFormByClass, cls),
            ToCell(duelTrialsByClass, cls),
            ToCell(squadTrialsByClass, cls))).ToList();

        var orderingByColumn = new Dictionary<string, IReadOnlyList<string>>
        {
            ["duelClosedForm"] = OrderDescending(closedFormByClass),
            ["duelTrials"] = OrderDescending(duelTrialsByClass),
            ["squadTrials"] = OrderDescending(squadTrialsByClass),
        };

        var (transfers, whyNot) = VerdictFor(orderingByColumn, closedFormByClass, duelTrialsByClass, squadTrialsByClass);

        var driftWarnings = CheckAgainstCheckedInClosedForm(closedFormPairs, duelBuilds, screenSpec.Theta);

        return new TransferResult(
            screenSpec.Theta, screenSpec.Trials, refineTrials, shape, rows, orderingByColumn, transfers, whyNot,
            excludedSquadIds, Coverage.Standard(), driftWarnings,
            DuelHash: DeterminismHash.Hash(duelScreening.Run),
            SquadHash: DeterminismHash.Hash(squadScreening.Run));
    }

    static ColumnCell ToCell(IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> byClass, string cls) =>
        byClass.TryGetValue(cls, out var v) ? new ColumnCell(v.MeanMilli, v.HalfWidthMilli) : new ColumnCell(0, 1000);

    static IReadOnlyList<string> OrderDescending(IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> byClass) =>
        byClass.Keys.OrderByDescending(k => byClass[k].MeanMilli).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>spec §5/§9.2: "transfers is true only when all three orderings are identical and every
    /// pairwise gap that decides the ordering exceeds its own half-width." "The gap that decides the
    /// ordering" is each column's own adjacent pair in its sorted-by-mean list -- if every adjacent gap
    /// clears its combined half-width, the column's total order is fully resolved by construction
    /// (transitivity), so checking adjacent pairs is sufficient without enumerating every non-adjacent
    /// pair too.</summary>
    static (bool Transfers, string? WhyNot) VerdictFor(
        IReadOnlyDictionary<string, IReadOnlyList<string>> orderingByColumn,
        IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> closedForm,
        IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> duelTrials,
        IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> squadTrials)
    {
        var byColumn = new (string Name, IReadOnlyDictionary<string, (long MeanMilli, long HalfWidthMilli)> Values)[]
        {
            ("duelClosedForm", closedForm), ("duelTrials", duelTrials), ("squadTrials", squadTrials),
        };

        foreach (var (name, values) in byColumn)
        {
            var order = orderingByColumn[name];
            for (var i = 0; i + 1 < order.Count; i++)
            {
                var a = order[i];
                var b = order[i + 1];
                if (!values.TryGetValue(a, out var va) || !values.TryGetValue(b, out var vb)) continue;
                if (Resolution.GapIsInsideHalfWidth(va.MeanMilli, va.HalfWidthMilli, vb.MeanMilli, vb.HalfWidthMilli))
                    return (false, Resolution.CannotSeparateMessage(a, va.MeanMilli, va.HalfWidthMilli, b, vb.MeanMilli, vb.HalfWidthMilli, name));
            }
        }

        var closedFormOrder = orderingByColumn["duelClosedForm"];
        var duelTrialsOrder = orderingByColumn["duelTrials"];
        var squadTrialsOrder = orderingByColumn["squadTrials"];
        var allIdentical = closedFormOrder.SequenceEqual(duelTrialsOrder) && duelTrialsOrder.SequenceEqual(squadTrialsOrder);
        if (!allIdentical)
            return (false, $"orderings differ across columns despite resolved gaps: " +
                           $"duelClosedForm=[{string.Join(",", closedFormOrder)}], " +
                           $"duelTrials=[{string.Join(",", duelTrialsOrder)}], " +
                           $"squadTrials=[{string.Join(",", squadTrialsOrder)}]");

        return (true, null);
    }

    /// <summary>spec §5: "The harness compares its recomputation against the checked-in file and warns
    /// on mismatch; it never rewrites it." Compared only when the checked-in file's own theta matches
    /// this run's -- a different theta is not a drift, it is a different measurement. Returns warning
    /// strings; callers print them (Console.Error) and never fail the run over them.</summary>
    static IReadOnlyList<string> CheckAgainstCheckedInClosedForm(
        IReadOnlyList<(string AttackerId, long ValueMilli, long HalfWidthMilli)> closedFormPairs,
        IReadOnlyList<NamedBuild> duelBuilds, long theta)
    {
        var warnings = new List<string>();
        var checkedInPath = Path.Combine(TuningBootstrap.FindRepoRoot(), "docs", "research", "class-system", "_hybrid-viability.json");
        if (!File.Exists(checkedInPath)) return warnings;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(checkedInPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("theta", out var thetaEl) || thetaEl.GetInt64() != theta)
            {
                warnings.Add($"checked-in _hybrid-viability.json is at theta={(root.TryGetProperty("theta", out var t) ? t.ToString() : "?")}, this run is theta={theta} -- comparison skipped, not a drift.");
                return warnings;
            }

            var perBuildMean = closedFormPairs
                .GroupBy(p => p.AttackerId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Average(x => x.ValueMilli / 1000.0), StringComparer.Ordinal);

            const double DriftToleranceFraction = 0.01; // 1pp -- comparison-only guard band, not a balance dial
            foreach (var build in root.GetProperty("builds").EnumerateArray())
            {
                var label = build.GetProperty("label").GetString()!;
                var checkedInMean = build.GetProperty("meanWinShare").GetDouble();
                if (!perBuildMean.TryGetValue(label, out var recomputed)) continue;
                if (Math.Abs(recomputed - checkedInMean) > DriftToleranceFraction)
                    warnings.Add($"drift vs checked-in _hybrid-viability.json for '{label}': recomputed {recomputed:P2} vs checked-in {checkedInMean:P2}");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            warnings.Add($"could not parse checked-in _hybrid-viability.json: {ex.Message}");
        }

        return warnings;
    }
}
