using System.Text.Json;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §11 S3 (todo "F5: S3 -- the soul track in the model") -- the `soultrack` mode.
/// Sweeps Θ ∈ {100, 150, 200, 300, 400, 600} (the range doc 16 found every previous sweep in this
/// program missed) crossed with a `wMilli` and a `thetaPerSoulLevelMilli` sweep, using
/// <see cref="SoulTrackModel"/> so `concentration.wMilli` is finally measured against BOTH tracks
/// rather than the honest zero <see cref="TreeModel.Resolve"/> reads for `H_souls` on its own.
///
/// <para><b>One seed stream, six Θ values.</b> Every cell is measured from the SAME <see cref="RunSpec.RunSeed"/>
/// (only <c>Theta</c> varies via <c>spec with {{ Theta = theta }}</c>) -- the todo's own verification
/// line ("the sweep runs at all six Θ values from one seed stream"), never six independently-seeded
/// invocations a caller could accidentally desynchronise.</para>
///
/// <para><b>The Θ≈300 crossover reuses <see cref="TransferReport"/>'s own refusal mechanism, never a
/// second one.</b> Doc 16's crossover is a CLOSED-FORM result (spec §9.2's callout) -- this class never
/// claims to refute it. At the swept Θ=300 point it checks whether the corner-vs-spread win share can
/// be separated from the 500‰ coin-flip line using <see cref="Resolution.GapIsInsideHalfWidth"/> (the
/// EXACT same gap-vs-half-width test <see cref="TransferReport.VerdictFor"/> already uses for its own
/// column orderings) and, when it cannot, reports <see cref="Resolution.CannotSeparateMessage"/>'s
/// output verbatim -- the literal words "cannot separate," never a euphemism.</para>
///
/// <para><b>Honest gap, named.</b> This program's heavy concurrent machine load this session made a
/// real 3,000/40,000-trial production sweep impractical to run live (the same disclosed gap F2/F3/F4
/// already carry) -- <see cref="ProposeDial"/> is exercised here at small trial counts to prove the
/// MECHANISM (a real value+half-width report, a real "cannot separate" refusal, a real match against
/// <see cref="SoulTrack"/>), never presented as a production balance finding.</para>
/// </summary>
public static class SoulTrackSweep
{
    public sealed record Cell(
        long Theta, long WMilli, long ThetaPerSoulLevelMilli,
        long CornerWinShareMilli, long HalfWidthMilli,
        long HNodesMilli, long HSoulsMilli, long HMilli, long FMilli);

    /// <summary>A proposed dial value with its own half-width, matching this program's
    /// <c>Resolution.HalfWidthMilli</c> convention throughout (acceptance bullet 1). <see cref="Resolved"/>
    /// is false, with <see cref="WhyNot"/> naming the gap, when no swept combination separated the
    /// Θ≈300 crossover at the trial count this run used -- never a silently-invented value.</summary>
    public sealed record ProposedValue(long ValueMilli, long HalfWidthMilli, bool Resolved, string? WhyNot);

    public sealed record Result(
        string CornerId, string SpreadId, long B, long FmaxMilli, long Trials, long? RefineTrials,
        IReadOnlyList<Cell> Cells, IReadOnlyList<string> CannotSeparateAtTheta300,
        ProposedValue ProposedWMilli, ProposedValue ProposedThetaPerSoulLevelMilli,
        HarnessCoverage Coverage);

    /// <summary>
    /// The whole S3 sweep: every (Θ, wMilli, thetaPerSoulLevelMilli) cell, the Θ≈300 refusal list, and
    /// the two proposed dials. `--theta` has no default (matches `--fmax-milli`/`--w-milli`/`--rule`'s
    /// own "no default sweep range" convention) -- an unstated Θ range would be a design choice nobody
    /// made, exactly the failure doc 16 names ("every previous sweep... missed" this range).
    /// </summary>
    public static Result Run(
        RunSpec spec, SquadBuild corner, SquadBuild spread,
        IReadOnlyList<long> thetas, IReadOnlyList<long> wMillis, IReadOnlyList<long> thetaPerSoulLevelMillis,
        long fmaxMilli, long b, long? refineTrials, bool parallel)
    {
        if (thetas is null || thetas.Count == 0)
            throw new ArgumentException("theta sweep must be non-empty", nameof(thetas));
        if (wMillis is null || wMillis.Count == 0)
            throw new ArgumentException("wMilli sweep must be non-empty", nameof(wMillis));
        if (thetaPerSoulLevelMillis is null || thetaPerSoulLevelMillis.Count == 0)
            throw new ArgumentException("thetaPerSoulLevelMilli sweep must be non-empty", nameof(thetaPerSoulLevelMillis));

        var cells = new List<Cell>();
        var cannotSeparate = new List<string>();

        foreach (var theta in thetas)
        {
            // Same RunSeed for every theta -- only Theta moves. This is what "one seed stream" means.
            var cellSpec = spec with { Theta = theta };
            foreach (var wMilli in wMillis)
            foreach (var ws in thetaPerSoulLevelMillis)
            {
                var cornerEntry = SoulTrackModel.ApplyTreeModel(
                    corner, theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest, ws);
                var spreadEntry = SoulTrackModel.ApplyTreeModel(
                    spread, theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest, ws);

                var screening = Screening.RunWithRefine("soultrack", new[] { cornerEntry, spreadEntry }, cellSpec, refineTrials, parallel);
                var pair = screening.Run.Pairs.Single(p => p.AttackerId == cornerEntry.Id && p.DefenderId == spreadEntry.Id);
                var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));

                var representative = SoulTrackModel.Resolve(
                    corner.Actors[0], theta, fmaxMilli, wMilli, b, includeOwnershipCost: true, TreeModel.CreditRule.Largest, ws);
                cells.Add(new Cell(theta, wMilli, ws, pair.WinShareMilli, halfWidth,
                    representative.HNodesMilli, representative.HSoulsMilli, representative.HMilli, representative.FMilli));

                // spec §9.2's callout, applied at the swept Theta=300 point specifically: the crossover
                // is 0.5pp or less in the closed form -- below a 3,000-trial screening run's own noise
                // floor. Reusing Resolution.GapIsInsideHalfWidth against the 500pm coin-flip line (a
                // "half-width 0" comparator) is the SAME mechanism TransferReport.VerdictFor uses for an
                // adjacent-pair gap, applied here to "can this cell's win share be told apart from 50%."
                if (theta == 300 && Resolution.GapIsInsideHalfWidth(pair.WinShareMilli, halfWidth, 500L, 0L))
                {
                    cannotSeparate.Add(Resolution.CannotSeparateMessage(
                        cornerEntry.Id, pair.WinShareMilli, halfWidth,
                        "the 500pm crossover", 500L, 0L,
                        $"theta=300 (wMilli={wMilli}, thetaPerSoulLevelMilli={ws})"));
                }
            }
        }

        var proposedW = ProposeDial(cells, thetas, wMillis, c => c.WMilli);
        var proposedWs = ProposeDial(cells, thetas, thetaPerSoulLevelMillis, c => c.ThetaPerSoulLevelMilli);

        return new Result(corner.Id, spread.Id, b, fmaxMilli, spec.Trials, refineTrials,
            cells, cannotSeparate, proposedW, proposedWs, Coverage.Standard());
    }

    /// <summary>
    /// Acceptance bullet 1: "reported as a value AND a half-width." Picks the FIRST swept value (by the
    /// sweep's own input order, so the result is deterministic and never a hand-picked favourite) whose
    /// Θ=300 cell separates the win share from the 500‰ coin-flip line -- i.e. the crossover was
    /// actually resolved at that value, using this run's own trial count. The half-width reported is
    /// that resolved cell's own win-share half-width (<see cref="Resolution.HalfWidthMilli"/>), the same
    /// unit and convention every other cell in this module carries.
    ///
    /// <para>Honest refusal, never a fabricated value: at this module's necessarily small trial counts
    /// (this session's disclosed live-sweep gap), NOTHING may resolve the crossover -- in that case this
    /// returns <see cref="ProposedValue.Resolved"/> = false with a <see cref="ProposedValue.WhyNot"/>
    /// naming exactly that, rather than reporting the sweep's first value as if it meant something.</para>
    ///
    /// <para><b>Public</b> (not just internal to <see cref="Run"/>) so it is directly testable against a
    /// SYNTHETIC cell list -- the same "pure math tested directly, engine wiring tested separately at
    /// small trial counts" split every other file in this module uses
    /// (<see cref="Erosion.DetermineVerdict"/>, <see cref="Resolution"/>'s own tests).</para>
    /// </summary>
    public static ProposedValue ProposeDial(
        IReadOnlyList<Cell> cells, IReadOnlyList<long> thetas, IReadOnlyList<long> sweptValues, Func<Cell, long> axis)
    {
        if (!thetas.Contains(300L))
            return new ProposedValue(0, 1000,
                Resolved: false,
                WhyNot: "theta=300 is not in this run's --theta sweep, so no crossover verdict is available to propose from");

        foreach (var value in sweptValues)
        {
            // The SAME check that produced cannotSeparateAtTheta300's entries (Resolution.GapIsInsideHalfWidth
            // against the 500pm coin-flip line), recomputed directly per candidate cell rather than
            // re-parsed out of that list's message text.
            var cell = cells.FirstOrDefault(c => c.Theta == 300 && axis(c) == value
                && !Resolution.GapIsInsideHalfWidth(c.CornerWinShareMilli, c.HalfWidthMilli, 500L, 0L));
            if (cell is not null)
                return new ProposedValue(value, cell.HalfWidthMilli, Resolved: true, WhyNot: null);
        }

        return new ProposedValue(0, 1000, Resolved: false,
            WhyNot: "no swept combination separated the theta=300 crossover from the 500pm coin-flip line at " +
                    "this run's trial count -- a deeper --refine pass (spec 9.2's 40,000-trial tier) is needed " +
                    "before a real value can be proposed; see this module's own disclosed live-sweep gap");
    }

    public static string ArtifactPath(string repoRoot) =>
        Path.Combine(repoRoot, "docs", "research", "passive-tree", "_soul-track-sweep.json");

    static readonly JsonSerializerOptions ArtifactOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string WriteArtifact(Result result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden, and never a data/tuning write (spec-squad-harness.md " +
                   "§13 'Never' list). Proposes soulTrack.thetaPerSoulLevelMilli and concentration.wMilli evidence " +
                   "only; tools/tuning/publish.py is the only writer of the real tunable (tunables-ssot.md T4). " +
                   "Doc 16's Theta~300 crossover is a CLOSED-FORM result -- a 'cannot separate' entry here at " +
                   "screening trial counts is NOT a refutation of it (spec §9.2's callout).",
            cornerId = result.CornerId,
            spreadId = result.SpreadId,
            b = result.B,
            fmaxMilli = result.FmaxMilli,
            trials = result.Trials,
            refineTrials = result.RefineTrials,
            cells = result.Cells.Select(c => new
            {
                theta = c.Theta,
                wMilli = c.WMilli,
                thetaPerSoulLevelMilli = c.ThetaPerSoulLevelMilli,
                cornerVsSpread = new TreeModel.ValueWithHalfWidth(c.CornerWinShareMilli, c.HalfWidthMilli),
                hNodesMilli = c.HNodesMilli,
                hSoulsMilli = c.HSoulsMilli,
                hMilli = c.HMilli,
                fMilli = c.FMilli,
            }),
            cannotSeparateAtTheta300 = result.CannotSeparateAtTheta300,
            // Deliberately NOT TreeModel.ValueWithHalfWidth here: that record's field is literally named
            // WinShareMilli, and a dial value (a wMilli, a Ws) is not a win share -- reusing it would
            // print a misleading "winShareMilli" key under a proposed DIAL. valueMilli/halfWidthMilli
            // keeps the same value+half-width SHAPE (acceptance bullet 1) with an honest name.
            proposed = new
            {
                wMilli = new
                {
                    valueMilli = result.ProposedWMilli.ValueMilli,
                    halfWidthMilli = result.ProposedWMilli.HalfWidthMilli,
                    resolved = result.ProposedWMilli.Resolved,
                    whyNot = result.ProposedWMilli.WhyNot,
                },
                thetaPerSoulLevelMilli = new
                {
                    valueMilli = result.ProposedThetaPerSoulLevelMilli.ValueMilli,
                    halfWidthMilli = result.ProposedThetaPerSoulLevelMilli.HalfWidthMilli,
                    resolved = result.ProposedThetaPerSoulLevelMilli.Resolved,
                    whyNot = result.ProposedThetaPerSoulLevelMilli.WhyNot,
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
