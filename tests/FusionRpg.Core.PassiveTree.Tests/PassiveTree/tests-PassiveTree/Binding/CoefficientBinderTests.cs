using System.IO;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>Task B4 — `CoefficientBinder` (spec-tree-binder.md §3.1-3.4). Every expected number here
/// is copied from the spec's own worked example, computed before this class was written.</summary>
public class CoefficientBinderTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    [Fact]
    public void The_worked_example_share_45_produces_3038()
    {
        // spec-tree-binder.md §3.4: broad-and-flat, tier 5, offensive, first of two,
        // combat.power.fire, budgetShareMilli=45, channelAnchorMilli=135 (atk).
        var kMicro = CoefficientBinder.Bind(
            treeShareMilli: 1000, treeBudgetMilli: 1000, budgetShareMilli: 45,
            channelAnchorMilli: 135, branches: 2);

        Assert.Equal(3038L, kMicro);
    }

    [Fact]
    public void The_worked_examples_sibling_share_46_produces_3105_exactly()
    {
        var kMicro = CoefficientBinder.Bind(
            treeShareMilli: 1000, treeBudgetMilli: 1000, budgetShareMilli: 46,
            channelAnchorMilli: 135, branches: 2);

        Assert.Equal(3105L, kMicro);
    }

    [Fact]
    public void ChannelAnchor_reproduces_the_atk_and_defense_anchors_from_the_real_tuning_file()
    {
        var tuning = RealPowerTuning();

        Assert.Equal(135L, ChannelAnchor.ForChannel("atk", tuning));
        Assert.Equal(32L, ChannelAnchor.ForChannel("defense", tuning));
        Assert.Equal(135L, ChannelAnchor.ForChannel("combat.power.fire", tuning));
        Assert.Equal(32L, ChannelAnchor.ForChannel("combat.defense.ice", tuning));
    }

    [Fact]
    public void An_unmapped_channel_family_refuses_naming_it_rather_than_guessing()
    {
        var tuning = RealPowerTuning();
        var ex = Assert.Throws<ChannelAnchor.UnknownChannelPin>(
            () => ChannelAnchor.ForChannel("status.resist.dot", tuning));
        Assert.Contains("status.resist.dot", ex.Message);
    }

    [Fact]
    public void End_to_end_the_real_tuning_file_reproduces_the_worked_example()
    {
        var tuning = RealPowerTuning();
        var anchor = ChannelAnchor.ForChannel("combat.power.fire", tuning);
        var kMicro = CoefficientBinder.Bind(1000, 1000, 45, anchor, 2);
        Assert.Equal(3038L, kMicro);
    }

    [Fact]
    public void Overflow_throws_rather_than_wraps()
    {
        Assert.Throws<CoefficientBinder.CoefficientOverflow>(() =>
            CoefficientBinder.Bind(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, 2));
    }

    [Fact]
    public void Zero_budget_share_produces_zero_kMicro()
    {
        Assert.Equal(0L, CoefficientBinder.Bind(1000, 1000, 0, 135, 2));
    }

    // ---- Source-shape test: R4 deleted tierWeight/weightTotal/w[t] from the formula. A value test
    // cannot see this defect (the same tier could pass either formula by coincidence) — only reading
    // the source can. ----

    [Fact]
    public void CoefficientBinder_source_contains_no_tierWeight_weightTotal_or_w_bracket_t()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "PassiveTree", "Binding", "CoefficientBinder.cs");
        var source = StripComments(File.ReadAllText(path));

        Assert.DoesNotContain("tierWeight", source);
        Assert.DoesNotContain("weightTotal", source);
        Assert.DoesNotContain("w[t]", source);
        Assert.DoesNotContain("w[", source);
    }

    static string StripComments(string source)
    {
        var noBlock = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//.*$", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }
}
