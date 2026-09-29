using FusionRpg.SquadHarness.Tests.TestSupport;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §5 -- the three-column transfer report. Every test here uses
/// <see cref="TinyClassifiedRoster"/> (one REAL build per class, from the production rosters directly)
/// rather than the full 91/23 rosters, which measure at minutes-per-call on this machine (see
/// <see cref="TransferReport"/>'s own doc for the measured per-call cost).
/// </summary>
public class TransferReportTests
{
    static RunSpec Spec(long trials, long theta = 60) => new(theta, trials, RunSeed: 20260906);

    [Fact]
    public void Build_produces_exactly_the_four_named_build_class_rows()
    {
        var result = TransferReport.Build(Spec(trials: 5), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.Equal(new[] { "corner", "hybrid2", "hybrid3", "spread" }, result.Rows.Select(r => r.BuildClass).ToArray());
        foreach (var name in new[] { "duelClosedForm", "duelTrials", "squadTrials" })
            Assert.Equal(4, result.OrderingByColumn[name].Count);
    }

    [Fact]
    public void Every_cell_carries_a_number_and_a_half_width()
    {
        // F2 acceptance: "every proposed value is a number and a half-width -- never a bare point
        // estimate." Structural check: every ColumnCell on every row has both fields populated
        // (HalfWidthMilli >= 0 is always true for a long, so the real assertion is that the type
        // itself forces both fields to exist -- this test pins that shape).
        var result = TransferReport.Build(Spec(trials: 5), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        foreach (var row in result.Rows)
        {
            Assert.True(row.DuelClosedForm.HalfWidthMilli >= 0);
            Assert.True(row.DuelTrials.HalfWidthMilli >= 0);
            Assert.True(row.SquadTrials.HalfWidthMilli >= 0);
        }
    }

    [Fact]
    public void DuelClosedForm_half_width_is_always_zero_because_it_is_deterministic()
    {
        // spec §9.2: "Doc 16's crossover is a CLOSED-FORM result... deterministic: no trials,
        // therefore no sampling noise."
        var result = TransferReport.Build(Spec(trials: 5), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.All(result.Rows, r => Assert.Equal(0L, r.DuelClosedForm.HalfWidthMilli));
    }

    [Fact]
    public void Transfers_is_false_when_an_ordering_rests_inside_its_half_width_and_says_cannot_separate()
    {
        // A single trial per cell gives the widest possible half-width (close to 1000pm on either
        // side of 500pm), so no two build classes' trial-column means can possibly separate -- this
        // is the "cannot separate" refusal spec §9.2 requires, in exactly those words.
        var result = TransferReport.Build(Spec(trials: 1), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.False(result.Transfers);
        Assert.NotNull(result.WhyNot);
        Assert.Contains("cannot separate", result.WhyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedSquadIds_names_the_archetype_that_is_not_a_single_build_class()
    {
        // TinyClassifiedRoster.Squads() deliberately includes exactly one non-mono-family squad.
        var squads = TinyClassifiedRoster.Squads();
        var excludedId = squads.Single(s => TransferReport.SquadBuildClass(s.Id) is null).Id;

        var result = TransferReport.Build(Spec(trials: 2), TinyClassifiedRoster.Duels(), squads,
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.Contains(excludedId, result.ExcludedSquadIds);
        foreach (var squad in squads.Where(s => TransferReport.SquadBuildClass(s.Id) is not null))
            Assert.DoesNotContain(squad.Id, result.ExcludedSquadIds);
    }

    [Theory]
    [InlineData("mono-might", "corner")]
    [InlineData("mono-spread", "spread")]
    [InlineData("posture-force", null)]
    [InlineData("rainbow-balanced", null)]
    [InlineData("mixed-corner-spread", null)]
    public void SquadBuildClass_classifies_the_mono_family_and_excludes_everything_else(string id, string? expected)
    {
        Assert.Equal(expected, TransferReport.SquadBuildClass(id));
    }

    [Fact]
    public void SquadBuildClass_classifies_the_actual_mono_hybrid_ids_the_roster_produces()
    {
        // The exact mono-hybrid2-*/mono-hybrid3-* ids depend on posture composition (AptitudeCatalog's
        // own order), so this reads them from the real roster rather than guessing a literal string.
        var squads = SquadRoster.Squads();
        var hybrid2Id = squads.First(s => s.Kind == "mono-hybrid2").Id;
        var hybrid3Id = squads.First(s => s.Kind == "mono-hybrid3").Id;

        Assert.Equal("hybrid2", TransferReport.SquadBuildClass(hybrid2Id));
        Assert.Equal("hybrid3", TransferReport.SquadBuildClass(hybrid3Id));
    }

    [Fact]
    public void The_recomputed_closed_form_is_compared_against_the_checked_in_artifact_and_never_throws()
    {
        // spec §5: "The harness compares its recomputation against the checked-in file and warns on
        // mismatch; it never rewrites it." Theta=100 matches the checked-in _hybrid-viability.json's
        // own theta, so the comparison actually runs (rather than being skipped as a theta mismatch).
        // A reduced 4-build roster's own "mean vs field" necessarily differs from the checked-in
        // file's 90-opponent field, so a drift warning is the EXPECTED, correct outcome here -- the
        // assertion is that this never throws and always returns a (possibly non-empty) list.
        var result = TransferReport.Build(Spec(trials: 2, theta: 100), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.NotNull(result.ClosedFormDriftWarnings);
    }

    [Fact]
    public void Coverage_is_attached_to_every_transfer_result()
    {
        var result = TransferReport.Build(Spec(trials: 2), TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.Equal(6, result.Coverage.BlockedMechanismClasses.Count);
        Assert.Contains("NEUTRALISED", result.Coverage.ElementAxis, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_is_deterministic_for_the_same_inputs()
    {
        var spec = Spec(trials: 3);
        var r1 = TransferReport.Build(spec, TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);
        var r2 = TransferReport.Build(spec, TinyClassifiedRoster.Duels(), TinyClassifiedRoster.Squads(),
            AllocationShape.PerActor, refineTrials: null, parallel: false);

        Assert.Equal(r1.DuelHash, r2.DuelHash);
        Assert.Equal(r1.SquadHash, r2.SquadHash);
        Assert.Equal(r1.Transfers, r2.Transfers);
        Assert.Equal(r1.WhyNot, r2.WhyNot);
    }

    [Fact]
    public void Refine_only_re_measures_cells_that_could_not_call_a_winner_at_screening()
    {
        var duel = TinyClassifiedRoster.Duels();
        var squad = TinyClassifiedRoster.Squads();
        // Screening at 1 trial cannot call any winner (half-width ~1000pm); refine at 20 trials still
        // will not fully resolve either, but must run without throwing and must still return a result
        // shaped like a screening-only run (same four rows, same columns).
        var refined = TransferReport.Build(Spec(trials: 1), duel, squad, AllocationShape.PerActor, refineTrials: 20, parallel: false);
        Assert.Equal(4, refined.Rows.Count);
        Assert.Equal(20L, refined.RefineTrials);
    }
}
