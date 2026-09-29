using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §11 S3 (todo "F5: S3 -- the soul track in the model"). Two layers, matching
/// <see cref="ErosionTests"/>'s/<see cref="TreeModelTests"/>'s own split: <see cref="SoulTrackSweep.ProposeDial"/>
/// is pure and tested directly with synthetic cell lists (no <c>BattleEngine</c> call at all); the
/// smaller live layer proves the doc-16 theta range and the one-seed-stream wiring actually hold, at
/// trivial trial counts -- this repo's heavy concurrent machine load this session made a real
/// 3,000/40,000-trial production sweep impractical to run live (matching F2/F3/F4's own disclosed gap),
/// so a full-scale finding is NOT produced here; the mechanism is proven instead, and that gap is named
/// again on <c>ProposedValue.Resolved == false</c>'s own <c>WhyNot</c>.
/// </summary>
public class SoulTrackSweepTests
{

    static (SquadBuild Corner, SquadBuild Spread) Rosters()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{BuildFactory.Roster[0].ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        return (corner, spread);
    }

    // ---- Run: refusals, and the shape of a real sweep -------------------------------------------------

    [Fact]
    public void Run_refuses_an_empty_theta_sweep()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(Theta: 100, Trials: 1, RunSeed: 20260906);
        Assert.Throws<ArgumentException>(() =>
            SoulTrackSweep.Run(spec, corner, spread, Array.Empty<long>(), new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false));
    }

    [Fact]
    public void Run_refuses_an_empty_wMilli_sweep()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(100, 1, 20260906);
        Assert.Throws<ArgumentException>(() =>
            SoulTrackSweep.Run(spec, corner, spread, new long[] { 100 }, Array.Empty<long>(), new long[] { 250 }, 1200, 5, null, false));
    }

    [Fact]
    public void Run_refuses_an_empty_thetaPerSoulLevelMilli_sweep()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(100, 1, 20260906);
        Assert.Throws<ArgumentException>(() =>
            SoulTrackSweep.Run(spec, corner, spread, new long[] { 100 }, new long[] { 500 }, Array.Empty<long>(), 1200, 5, null, false));
    }

    [Fact]
    public void Run_sweeps_all_six_of_doc16s_theta_values_from_a_single_invocation()
    {
        // The exact range spec section11 S3 / doc 16 names: Theta in {100,150,200,300,400,600}. Trials
        // stay tiny (this session's disclosed live-sweep gap) -- this proves the WIRING (all six values
        // actually get invoked and reported), not a balance finding.
        var (corner, spread) = Rosters();
        var thetas = new long[] { 100, 150, 200, 300, 400, 600 };
        var spec = new RunSpec(Theta: 0 /* overwritten per-cell */, Trials: 2, RunSeed: 20260906);

        var result = SoulTrackSweep.Run(spec, corner, spread, thetas, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);

        var thetasSeen = result.Cells.Select(c => c.Theta).Distinct().OrderBy(t => t).ToList();
        Assert.Equal(thetas.OrderBy(t => t).ToList(), thetasSeen);
        Assert.Equal(thetas.Length, result.Cells.Count); // 6 theta x 1 wMilli x 1 Ws = 6 cells
    }

    [Fact]
    public void Run_produces_one_cell_per_theta_wMilli_thetaPerSoulLevelMilli_combination()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 1, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread,
            new long[] { 100, 200 }, new long[] { 0, 500, 1000 }, new long[] { 0, 250 }, 1200, 5, null, false);

        Assert.Equal(2 * 3 * 2, result.Cells.Count);
        foreach (var cell in result.Cells)
        {
            Assert.InRange(cell.CornerWinShareMilli, 0, 1000);
            Assert.True(cell.HalfWidthMilli > 0);
        }
    }

    [Fact]
    public void Run_reuses_the_same_RunSeed_across_every_swept_theta_one_seed_stream_not_six()
    {
        // "One seed stream" (todo's own verification line): a cell for Theta=100 measured ALONGSIDE five
        // other thetas must be byte-identical to the SAME cell measured alone, because both draw from the
        // identical RunSpec.RunSeed -- proving the sweep never re-seeds per theta.
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 3, 20260906);

        var wide = SoulTrackSweep.Run(spec, corner, spread,
            new long[] { 100, 150, 200, 300, 400, 600 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);
        var narrow = SoulTrackSweep.Run(spec, corner, spread,
            new long[] { 100 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);

        var wideCell = wide.Cells.Single(c => c.Theta == 100);
        var narrowCell = narrow.Cells.Single(c => c.Theta == 100);
        Assert.Equal(narrowCell.CornerWinShareMilli, wideCell.CornerWinShareMilli);
        Assert.Equal(narrowCell.HalfWidthMilli, wideCell.HalfWidthMilli);
        Assert.Equal(narrowCell.HNodesMilli, wideCell.HNodesMilli);
        Assert.Equal(narrowCell.HSoulsMilli, wideCell.HSoulsMilli);
    }

    // ---- the Theta=300 crossover: "cannot separate" in those words, never a refutation ----------------

    [Fact]
    public void CannotSeparateAtTheta300_uses_the_literal_words_when_the_screening_run_cannot_resolve_it()
    {
        // At trials=2, the maximum possible gap to the 500pm coin-flip line is 500pm, and
        // HalfWidthMilli(decided<=2) is always >= ~693pm -- so this is DETERMINISTICALLY "cannot
        // separate" regardless of which build actually wins more, proving the wording fires correctly at
        // a screening trial count doc 16's own crossover is far too fine for (spec 9.2's callout).
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 2, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread, new long[] { 100, 300 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);

        Assert.NotEmpty(result.CannotSeparateAtTheta300);
        foreach (var msg in result.CannotSeparateAtTheta300)
            Assert.Contains("cannot separate", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void CannotSeparateAtTheta300_is_empty_when_theta_300_is_not_in_the_sweep()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 2, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread, new long[] { 100, 200 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);
        Assert.Empty(result.CannotSeparateAtTheta300);
    }

    [Fact]
    public void A_cannot_separate_entry_at_theta300_is_never_presented_as_a_refutation_of_the_closed_form()
    {
        // The artifact's own note text (written once, asserted here so it cannot silently drift) must
        // name the closed-form/trial distinction spec 9.2's callout requires.
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 2, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread, new long[] { 300 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);
        var json = SoulTrackSweep.WriteArtifact(result, Path.Combine(Path.GetTempPath(), $"soultrack-test-{Guid.NewGuid():N}.json"));
        Assert.Contains("CLOSED-FORM result", json, StringComparison.Ordinal);
        Assert.Contains("NOT a refutation", json, StringComparison.Ordinal);
    }

    // ---- ProposeDial: pure, synthetic, no engine call --------------------------------------------------

    static SoulTrackSweep.Cell Cell(long theta, long w, long ws, long winShare, long halfWidth) =>
        new(theta, w, ws, winShare, halfWidth, HNodesMilli: 0, HSoulsMilli: 0, HMilli: 0, FMilli: 1000);

    [Fact]
    public void ProposeDial_reports_unresolved_with_a_named_gap_when_theta_300_is_not_in_the_sweep()
    {
        var cells = new[] { Cell(100, 500, 250, 900, 10) };
        var proposed = SoulTrackSweep.ProposeDial(cells, new long[] { 100 }, new long[] { 500 }, c => c.WMilli);
        Assert.False(proposed.Resolved);
        Assert.NotNull(proposed.WhyNot);
        Assert.Contains("theta=300", proposed.WhyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposeDial_resolves_to_the_first_swept_value_that_separates_the_theta300_crossover()
    {
        // wMilli=0 sits exactly on the coin flip (gap 0 <= any half-width -> never resolved); wMilli=500
        // is a clean blowout (winShare 950, halfWidth 10 -> gap 450 > 10 -> resolved). ProposeDial must
        // skip the unresolved candidate and pick the resolved one, in sweep order.
        var cells = new[]
        {
            Cell(300, 0, 250, 500, 10),
            Cell(300, 500, 250, 950, 10),
        };
        var proposed = SoulTrackSweep.ProposeDial(cells, new long[] { 300 }, new long[] { 0, 500 }, c => c.WMilli);
        Assert.True(proposed.Resolved);
        Assert.Equal(500L, proposed.ValueMilli);
        Assert.Equal(10L, proposed.HalfWidthMilli);
        Assert.Null(proposed.WhyNot);
    }

    [Fact]
    public void ProposeDial_reports_unresolved_when_no_swept_value_separates_the_crossover()
    {
        var cells = new[]
        {
            Cell(300, 0, 250, 500, 500),
            Cell(300, 500, 250, 520, 500),
        };
        var proposed = SoulTrackSweep.ProposeDial(cells, new long[] { 300 }, new long[] { 0, 500 }, c => c.WMilli);
        Assert.False(proposed.Resolved);
        Assert.Equal(0L, proposed.ValueMilli);
        Assert.Equal(1000L, proposed.HalfWidthMilli);
        Assert.Contains("refine", proposed.WhyNot!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProposeDial_is_deterministic_never_a_hand_picked_favourite()
    {
        var cells = new[]
        {
            Cell(300, 100, 250, 950, 10),
            Cell(300, 500, 250, 40, 10),
        };
        var first = SoulTrackSweep.ProposeDial(cells, new long[] { 300 }, new long[] { 100, 500 }, c => c.WMilli);
        var second = SoulTrackSweep.ProposeDial(cells, new long[] { 300 }, new long[] { 100, 500 }, c => c.WMilli);
        Assert.Equal(first, second);
        Assert.Equal(100L, first.ValueMilli); // first in sweep ORDER, not "best" by any magnitude
    }

    // ---- artifact writer: propose only, never touches gk-core/data/tuning -------------------------------------

    [Fact]
    public void WriteArtifact_never_writes_to_data_tuning()
    {
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 1, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread, new long[] { 100 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);

        var tuningDir = Path.Combine(TuningBootstrap.FindRepoRoot(), "data", "tuning");
        var before = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"soultrack-test-{Guid.NewGuid():N}.json");
        try
        {
            SoulTrackSweep.WriteArtifact(result, path);
            Assert.True(File.Exists(path));
            var after = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();
            Assert.Equal(before, after);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteArtifact_reports_both_proposed_dials_as_a_value_and_a_half_width()
    {
        // Acceptance bullet 1: "soulTrack.thetaPerSoulLevelMilli and concentration.wMilli are each
        // reported as a value AND a half-width."
        var (corner, spread) = Rosters();
        var spec = new RunSpec(0, 1, 20260906);
        var result = SoulTrackSweep.Run(spec, corner, spread, new long[] { 300 }, new long[] { 500 }, new long[] { 250 }, 1200, 5, null, false);

        var json = SoulTrackSweep.WriteArtifact(result, Path.Combine(Path.GetTempPath(), $"soultrack-test-{Guid.NewGuid():N}.json"));
        Assert.Contains("\"wMilli\"", json, StringComparison.Ordinal);
        Assert.Contains("\"thetaPerSoulLevelMilli\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"halfWidthMilli\"", json, StringComparison.Ordinal);
    }
}
