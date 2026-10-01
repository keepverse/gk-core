using FusionRpg.Core.Workspace;
using System.Text.Json;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §11 S4 (todo "F6: S4 -- the budget mode, and D42's two dials"). The <c>budget</c>
/// mode: D15's marginal win share per budget point, measured across the DUEL roster -- deliberately NOT
/// squad scope, unlike <see cref="TreeModel.ConcentrationSweep"/>/<see cref="TreeModel.CrossUnlockSweep"/>/
/// <see cref="SoulTrackSweep"/>. Doc 11 §6b's own finding ("at identical Θ and identical budgets the
/// twelve corners span 0.3%-97.9% mean win share") is itself a duel-scale (<c>gk-core/tools/HybridViability</c>)
/// result, so this is the one mode in the program that re-runs that exact shape -- one build per
/// aptitude corner, via <see cref="Modes.BuildDuelRoster"/>'s own roster -- with the tree model folded in.
///
/// <para><b>What "budget point" means here -- a stated default, because no spec fixes it in
/// <see cref="TreeModel"/>'s own units.</b> <see cref="TreeModel.Resolve"/>'s <c>b</c> parameter
/// ("aptitude-point-equivalents per tier unit") is the ONLY tree-power-scale knob the model exposes as a
/// swept argument. <c>budget.treeTotalPoints</c> and <c>treeShareMilli</c> are CATALOG/PLAN-authoring-time
/// dials this harness cannot touch without reading the generated catalog or re-deriving a shipped
/// generator -- both forbidden by spec §13 "Never" (confirmed by reading the actual consumers, not
/// assumed: <c>treeShareMilli</c> feeds <c>CoefficientBinder.Bind</c>
/// (<c>gk-core/src/FusionRpg.Core/PassiveTree/Binding/CoefficientBinder.cs:23</c>) at catalog-bake time, and
/// <c>budget.treeTotalPoints</c> feeds <c>gk-forge/tools/seedsmith/seedsmith/adapters/trees/plan/emit.py:266</c> at
/// plan-authoring time -- neither is a <see cref="TreeModel.Resolve"/> argument). Sweeping <c>b</c> and
/// taking a finite difference is therefore the closest honest measurement this module's own abstraction
/// can make of "how much a tree budget point is worth in combat" -- the same conceptual role
/// spec-tree-binder.md §3.6 assigns <c>treeShareMilli</c> ("its leverage depends on what share of total
/// power trees carry"). <see cref="ProposeTreeTotalPoints"/>/<see cref="ProposeTreeShareMilli"/> report
/// this mapping gap explicitly rather than silently presenting a <c>b</c>-space number as if it were in
/// either dial's own per-mille/point unit.</para>
///
/// <para><b>Marginal, not divided.</b> Following <c>gk-core/tools/CombatSim/Marginal.cs</c>'s own
/// <c>Delta = 1.0</c> convention ("small enough to read as a derivative"), a <see cref="MarginalRow"/>
/// reports the RAW <see cref="MarginalRow.DeltaWinShareMilli"/> and <see cref="MarginalRow.DeltaB"/>
/// between two adjacent swept <c>b</c> values, never a divided ratio -- dividing a per-mille delta by a
/// small integer step loses precision exactly the way docs/architecture/numeric-types.md's "divide by 1000 last, exactly once"
/// rule warns against, and a caller who swept adjacent integers (the recommended usage, <c>DeltaB = 1</c>)
/// already has the marginal value of one point directly in <see cref="MarginalRow.DeltaWinShareMilli"/>.</para>
///
/// <para><b>Honest gap, named.</b> This session's heavy concurrent machine load made a real
/// 3,000/40,000-trial production sweep impractical to run live -- the same disclosed gap F2/F3/F4/F5
/// already carry. The mechanism (a real value+half-width report, a real corner-spread reproduction of doc
/// 11 §6b's shape, a real unit-mapping refusal) is proven at small trial counts, never presented as a
/// production balance finding.</para>
/// </summary>
public static class BudgetSweep
{
    /// <summary>One (b, corner) cell: the corner's win share against the fixed "even12" spread baseline,
    /// its half-width, and the representative H/F that produced it -- the same three-field shape every
    /// other TreeModel sweep in this module reports.</summary>
    public sealed record Cell(long B, string CornerId, long CornerVsSpreadWinShareMilli, long HalfWidthMilli, long HMilli, long FMilli);

    /// <summary>The finite difference between two ADJACENT swept <c>b</c> values, for one corner. Never
    /// divided (see this class's own doc) -- <see cref="DeltaWinShareMilli"/> and <see cref="DeltaB"/> are
    /// reported raw, and <see cref="HalfWidthMilli"/> is the two cells' half-widths combined by
    /// <see cref="Resolution.CombinedHalfWidthMilli"/> (root-sum-square, the standard propagation rule for
    /// a difference of two independent estimates -- the same rule <see cref="TransferReport"/>'s own
    /// ordering check already uses).</summary>
    public sealed record MarginalRow(
        string CornerId, long LowB, long HighB,
        long LowWinShareMilli, long HighWinShareMilli, long DeltaWinShareMilli, long DeltaB,
        long HalfWidthMilli);

    /// <summary>A proposed dial value with its own half-width, matching this program's
    /// <c>Resolution.HalfWidthMilli</c> convention. <see cref="Resolved"/> is false, with
    /// <see cref="WhyNot"/> naming the gap, whenever no value can honestly be proposed -- never a
    /// silently-invented number (matching <see cref="SoulTrackSweep.ProposedValue"/>'s own shape).</summary>
    public sealed record ProposedValue(long ValueMilli, long HalfWidthMilli, bool Resolved, string WhyNot);

    public sealed record Result(
        string SpreadId, long Theta, long FmaxMilli, long WMilli, long Trials, long? RefineTrials,
        IReadOnlyList<long> BValues, IReadOnlyList<Cell> Cells, IReadOnlyList<MarginalRow> Marginals,
        long CornerSpreadLowMilli, long CornerSpreadHighMilli, long CornerSpreadLowestCornerB,
        ProposedValue ProposedTreeTotalPoints, ProposedValue ProposedTreeShareMilli,
        HarnessCoverage Coverage);

    /// <summary>
    /// The whole S4 sweep. <paramref name="bValues"/> has no default (matching every other sweep axis in
    /// this module -- an unstated range is a design choice nobody made) and needs at least two DISTINCT
    /// values: a finite difference needs two points. Every (b, corner) cell measures the corner's win
    /// share against the SAME fixed "even12" spread baseline via a real 2-actor
    /// <see cref="Screening.RunWithRefine"/> pass, mirroring <see cref="TreeModel.ConcentrationSweep"/>'s
    /// own corner-vs-spread shape, at duel (single-actor) scope instead of squad scope.
    /// </summary>
    public static Result Run(
        RunSpec spec, IReadOnlyList<NamedBuild> duelRoster, IReadOnlyList<long> bValues,
        long fmaxMilli, long wMilli, long? refineTrials, bool parallel)
    {
        if (duelRoster is null) throw new ArgumentNullException(nameof(duelRoster));
        if (bValues is null || bValues.Count < 2)
            throw new ArgumentException("refused: --b-list needs at least two values -- a finite difference needs two points", nameof(bValues));

        var sortedB = bValues.Distinct().OrderBy(b => b).ToList();
        if (sortedB.Count < 2)
            throw new ArgumentException("refused: --b-list must name at least two DISTINCT values", nameof(bValues));

        var spread = duelRoster.SingleOrDefault(nb => nb.Id == "even12")
            ?? throw new ArgumentException("duel roster is missing its 'even12' spread baseline", nameof(duelRoster));
        var corners = duelRoster.Where(nb => nb.Kind == "corner").OrderBy(nb => nb.Id, StringComparer.Ordinal).ToList();
        if (corners.Count == 0)
            throw new ArgumentException("duel roster has no 'corner' builds to sweep", nameof(duelRoster));

        var cells = new List<Cell>();
        foreach (var b in sortedB)
        {
            var spreadEntry = TreeModel.ApplyTreeModel(spread, spec.Theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest);
            foreach (var corner in corners)
            {
                var cornerEntry = TreeModel.ApplyTreeModel(corner, spec.Theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest);
                var screening = Screening.RunWithRefine("budget", new[] { cornerEntry, spreadEntry }, spec, refineTrials, parallel);
                var pair = screening.Run.Pairs.Single(p => p.AttackerId == cornerEntry.Id && p.DefenderId == spreadEntry.Id);
                var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));

                var representative = TreeModel.Resolve(corner.Allocation, spec.Theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest);
                cells.Add(new Cell(b, corner.Id, pair.WinShareMilli, halfWidth, representative.HMilli, representative.FMilli));
            }
        }

        var marginals = new List<MarginalRow>();
        foreach (var corner in corners)
        {
            for (var i = 0; i + 1 < sortedB.Count; i++)
            {
                var low = cells.Single(c => c.B == sortedB[i] && c.CornerId == corner.Id);
                var high = cells.Single(c => c.B == sortedB[i + 1] && c.CornerId == corner.Id);
                var deltaB = checked(sortedB[i + 1] - sortedB[i]);
                var deltaWinShare = checked(high.CornerVsSpreadWinShareMilli - low.CornerVsSpreadWinShareMilli);
                var combinedHalfWidth = Resolution.CombinedHalfWidthMilli(low.HalfWidthMilli, high.HalfWidthMilli);
                marginals.Add(new MarginalRow(corner.Id, low.B, high.B, low.CornerVsSpreadWinShareMilli,
                    high.CornerVsSpreadWinShareMilli, deltaWinShare, deltaB, combinedHalfWidth));
            }
        }

        // Doc 11 section 6b's own headline, reproduced at tree scope: at the FIRST swept b, how far apart
        // are the twelve corners' win shares against the shared spread baseline? This is the direct
        // tree-scale analogue of "at identical Theta and identical budgets the twelve corners span
        // 0.3%-97.9% mean win share" -- reported, never asserted to match that exact range (a different
        // roster, a different axis).
        var firstBCells = cells.Where(c => c.B == sortedB[0]).ToList();
        var spreadLow = firstBCells.Min(c => c.CornerVsSpreadWinShareMilli);
        var spreadHigh = firstBCells.Max(c => c.CornerVsSpreadWinShareMilli);

        return new Result(spread.Id, spec.Theta, fmaxMilli, wMilli, spec.Trials, refineTrials,
            sortedB, cells, marginals, spreadLow, spreadHigh, sortedB[0],
            ProposeTreeTotalPoints(), ProposeTreeShareMilli(), Coverage.Standard());
    }

    /// <summary>
    /// <c>budget.treeTotalPoints</c> is a PLAN-authoring-time input, not a <see cref="TreeModel.Resolve"/>
    /// argument -- confirmed directly in <c>gk-forge/tools/seedsmith/seedsmith/adapters/trees/plan/emit.py:266</c>
    /// (<c>budget_total = budget_cfg["treeTotalPoints"]</c>, which sets every node's own
    /// <c>budgetPoints</c> field) and by that tool's own
    /// <c>test_a_budget_total_change_is_reported_as_a_budget_delta</c>, which asserts a
    /// <c>treeTotalPoints</c> change DOES move the emitted plan. This harness's <c>b</c>-space marginal
    /// evidence (<see cref="Result.Marginals"/>) never reads or writes a plan file (spec §13 "Never": no
    /// <c>data/</c>, no re-derivation of a shipped generator), so it cannot be converted into a point count
    /// in the plan's own units -- always unresolved, named here rather than left to the caller to
    /// discover.
    /// </summary>
    public static ProposedValue ProposeTreeTotalPoints() => new(0, 1000, Resolved: false,
        WhyNot: "budget.treeTotalPoints is a PLAN-authoring-time input (tools/seedsmith/seedsmith/adapters/" +
                "trees/plan/emit.py:266's budget_total, which sets nodes[].budgetPoints), not a " +
                "TreeModel.Resolve argument -- confirmed by that tool's own " +
                "test_a_budget_total_change_is_reported_as_a_budget_delta, which shows a treeTotalPoints " +
                "change DOES move the emitted plan. This harness measures marginal win share against its " +
                "own TreeModel.Resolve 'b' parameter, which never reads or writes a plan file (spec-squad-" +
                "harness.md section13 'Never'), so a b-space marginal cannot be translated into the plan's " +
                "own point-count unit without a mapping this module does not own -- see this class's own " +
                "Marginals evidence for what IS measured.");

    /// <summary>
    /// <c>treeShareMilli</c> feeds <c>CoefficientBinder.Bind</c>
    /// (<c>gk-core/src/FusionRpg.Core/PassiveTree/Binding/CoefficientBinder.cs:23</c>) at CATALOG-BAKE time,
    /// baking directly into every bound atom's <c>kMicro</c> -- a value this harness never reads (spec §13
    /// "Never": no reading the generated catalog, no re-deriving the binder). The <see cref="Result.Marginals"/>
    /// evidence is measured in <see cref="TreeModel"/>'s own abstract H/F/b space, which folds tree power
    /// back into an <see cref="TreeModel.ActorTreeResult.EffectiveAllocation"/> synthetically -- downstream
    /// of a DIFFERENT fold-back than the real baked <c>kMicro</c> -- so this harness can report whether
    /// budget-scale matters at all, never a <c>treeShareMilli</c> value in its own per-mille unit.
    /// </summary>
    public static ProposedValue ProposeTreeShareMilli() => new(0, 1000, Resolved: false,
        WhyNot: "treeShareMilli feeds CoefficientBinder.Bind (src/FusionRpg.Core/PassiveTree/Binding/" +
                "CoefficientBinder.cs:23) at catalog-bake time, baking directly into every bound atom's " +
                "kMicro -- a value this harness never reads (spec-squad-harness.md section13 'Never': no " +
                "reading the generated catalog, no re-deriving CoefficientBinder). This class's Marginals " +
                "evidence is measured in TreeModel's own abstract H/F/b space (a synthetic fold-back into " +
                "an effective AptitudeAllocation), not the real baked kMicro path, so it can report whether " +
                "budget-scale matters at all, never a treeShareMilli value in its own per-mille unit.");

    public static string ArtifactPath(string repoRoot) =>
        Path.Combine(KeepverseRoots.Workspace(), "_budget-sweep.json", "docs", "research", "passive-tree", "_budget-sweep.json");

    static readonly JsonSerializerOptions ArtifactOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string WriteArtifact(Result result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden, and never a data/tuning write (spec-squad-harness.md " +
                   "section13 'Never' list). Reproduces doc 11 section6b's 'twelve corners span 0.3%-97.9% mean " +
                   "win share at identical budget' finding at duel scope with the tree model folded in, sweeping " +
                   "TreeModel.Resolve's own 'b' parameter as this harness's measurable proxy for tree budget " +
                   "scale. budget.treeTotalPoints and treeShareMilli are CATALOG/PLAN-authoring-time dials this " +
                   "harness structurally cannot resolve (see proposed.treeTotalPoints.whyNot / " +
                   "proposed.treeShareMilli.whyNot) -- tools/tuning/publish.py is the only writer of a real " +
                   "tunable (tunables-ssot.md T4), on the owner's call.",
            spreadId = result.SpreadId,
            theta = result.Theta,
            fmaxMilli = result.FmaxMilli,
            wMilli = result.WMilli,
            trials = result.Trials,
            refineTrials = result.RefineTrials,
            bValues = result.BValues,
            cells = result.Cells.Select(c => new
            {
                b = c.B,
                cornerId = c.CornerId,
                cornerVsSpread = new TreeModel.ValueWithHalfWidth(c.CornerVsSpreadWinShareMilli, c.HalfWidthMilli),
                hMilli = c.HMilli,
                fMilli = c.FMilli,
            }),
            marginals = result.Marginals.Select(m => new
            {
                cornerId = m.CornerId,
                lowB = m.LowB,
                highB = m.HighB,
                lowWinShareMilli = m.LowWinShareMilli,
                highWinShareMilli = m.HighWinShareMilli,
                deltaWinShareMilli = m.DeltaWinShareMilli,
                deltaB = m.DeltaB,
                halfWidthMilli = m.HalfWidthMilli,
            }),
            cornerSpreadAtLowestB = new
            {
                b = result.CornerSpreadLowestCornerB,
                lowMilli = result.CornerSpreadLowMilli,
                highMilli = result.CornerSpreadHighMilli,
            },
            proposed = new
            {
                treeTotalPoints = new
                {
                    valueMilli = result.ProposedTreeTotalPoints.ValueMilli,
                    halfWidthMilli = result.ProposedTreeTotalPoints.HalfWidthMilli,
                    resolved = result.ProposedTreeTotalPoints.Resolved,
                    whyNot = result.ProposedTreeTotalPoints.WhyNot,
                },
                treeShareMilli = new
                {
                    valueMilli = result.ProposedTreeShareMilli.ValueMilli,
                    halfWidthMilli = result.ProposedTreeShareMilli.HalfWidthMilli,
                    resolved = result.ProposedTreeShareMilli.Resolved,
                    whyNot = result.ProposedTreeShareMilli.WhyNot,
                },
            },
            coverage = result.Coverage,
        };

        var json = JsonSerializer.Serialize(payload, ArtifactOptions);
        var path = outPath ?? ArtifactPath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;
    }
}
