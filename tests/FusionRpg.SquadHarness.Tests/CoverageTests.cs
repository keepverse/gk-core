using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §3/§10/§10.1: the coverage block must name elements, the reserved channel
/// families, §10's SIX blocked mechanism classes, and the A10a/A10b split -- "so a null result is never
/// mistaken for a measurement."
/// </summary>
public class CoverageTests
{
    [Fact]
    public void Names_exactly_the_six_blocked_mechanism_classes_from_section_10()
    {
        Assert.Equal(6, Coverage.SixBlockedMechanismClasses.Count);
    }

    [Fact]
    public void Blocked_mechanism_classes_cover_the_named_triggers_and_M7_reflect()
    {
        var joined = string.Join("\n", Coverage.SixBlockedMechanismClasses);
        Assert.Contains("OnDamageTaken", joined, StringComparison.Ordinal);
        Assert.Contains("OnSpawn", joined, StringComparison.Ordinal);
        Assert.Contains("OnDeath", joined, StringComparison.Ordinal);
        Assert.Contains("stat.derived", joined, StringComparison.Ordinal);
        Assert.Contains("RecomposeDerived", joined, StringComparison.Ordinal);
        Assert.Contains("M7 Retaliation", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Element_axis_reports_neutralised_via_the_shipped_DominanceGuard_enumeration()
    {
        var coverage = Coverage.Standard();
        Assert.Contains("NEUTRALISED", coverage.ElementAxis, StringComparison.Ordinal);
        // Never a private re-derivation -- reused from DominanceGuard.StandardCoverage() directly.
        Assert.Equal(FusionRpg.Core.Balance.Guards.DominanceGuard.StandardCoverage().ElementAxis, coverage.ElementAxis);
        Assert.NotEmpty(coverage.ReservedFamilies);
    }

    [Fact]
    public void A10_split_names_both_A10a_and_A10b_so_one_is_never_reported_as_the_other()
    {
        var coverage = Coverage.Standard();
        Assert.Contains("A10a", coverage.A10Split, StringComparison.Ordinal);
        Assert.Contains("A10b", coverage.A10Split, StringComparison.Ordinal);
    }

    [Fact]
    public void Stalemate_horizon_names_the_20_percent_flag_threshold()
    {
        var coverage = Coverage.Standard();
        Assert.Contains("20%", coverage.StalemateHorizon, StringComparison.Ordinal);
        Assert.Contains("lowConfidence", coverage.StalemateHorizon, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave_opponent_note_says_wave_content_is_never_mixed_into_the_transfer_table()
    {
        var coverage = Coverage.Standard();
        Assert.Contains("D46", coverage.WaveOpponentNote, StringComparison.Ordinal);
        Assert.Contains("_scope-transfer.json", coverage.WaveOpponentNote, StringComparison.Ordinal);
    }
}
