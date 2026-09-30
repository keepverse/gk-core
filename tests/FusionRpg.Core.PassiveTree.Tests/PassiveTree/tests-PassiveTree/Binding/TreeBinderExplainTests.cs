using System.Collections.Generic;
using System.IO;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>
/// Task D2 — `gk-forge/tools/TreeBinder --explain &lt;nodeId&gt;` (spec-tree-binder.md §Commands): "prints the
/// whole chain for one node — plan input, anchor pin, formula, rounding, stored `kMicro`, the
/// `UnitClass` check and its verdict." Verification line: "`--explain` output reproduces §3.4's
/// worked example line by line."
/// </summary>
public class TreeBinderExplainTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    const string WorkedAffixId = "affix.worked-example.explain";
    const string WorkedAtomId = "atom.worked-example.explain";

    static (IReadOnlyDictionary<string, AffixRow>, IReadOnlyDictionary<string, AtomRow>) WorkedExampleContent()
    {
        var affix = new AffixRow(WorkedAffixId, null, new[] { new AffixRefRow(0, WorkedAtomId) });
        var atom = new AtomRow
        {
            AtomId = WorkedAtomId,
            KindId = "stat.derived",
            FamilyId = WorkedAtomId,
            Tier = 1,
            Name = "worked-example",
            ParamsJson = """{"channel":"combat.power.fire","op":"flat"}""",
            WhenJson = "{}",
        };
        return (new Dictionary<string, AffixRow> { [affix.AffixId] = affix },
                new Dictionary<string, AtomRow> { [atom.AtomId] = atom });
    }

    [Fact]
    public void Explain_reproduces_every_line_of_section_3_4s_worked_example()
    {
        var (affixes, atoms) = WorkedExampleContent();
        var node = new BindInputNode("skill.might-off-t5-n0", 1000, 1000, 45, 2,
            new[] { WorkedAffixId }, ExclusionForm.None, DeliberateHole: false);

        var explanation = TreeBinderExplain.Explain(node, affixes, atoms, RealPowerTuning());

        // Plan input.
        Assert.Contains("treeShareMilli      1000", explanation);
        Assert.Contains("treeBudgetMilli     1000", explanation);
        Assert.Contains("budgetShareMilli    45", explanation);
        Assert.Contains("branches            2", explanation);

        // Atom resolution.
        Assert.Contains("kind=stat.derived channel='combat.power.fire' op='flat'", explanation);

        // UnitClass / verdict.
        Assert.Contains("'GameUnits' -> verdict LadderScaled", explanation);

        // Anchor pin (atk 92 over hp 680 -> 135, §3.3's own worked value).
        Assert.Contains("channelAnchorMilli    135", explanation);

        // The formula, exactly as §3.4 states it.
        Assert.Contains("1000 * 1000 * 45 * 135 = 6075000000", explanation);
        Assert.Contains("2 * 1,000,000 = 2000000", explanation);

        // Rounding and the stored coefficient.
        Assert.Contains("round_half_away_from_zero(6075000000 / 2000000)", explanation);
        Assert.Contains("stored kMicro         3038", explanation);

        // The UnitClass check's verdict, printed explicitly.
        Assert.Contains("UnitClass check       PASS", explanation);
    }

    [Fact]
    public void Explain_reproduces_the_worked_examples_sibling_share_46()
    {
        var (affixes, atoms) = WorkedExampleContent();
        var node = new BindInputNode("skill.might-off-t5-n1", 1000, 1000, 46, 2,
            new[] { WorkedAffixId }, ExclusionForm.None, DeliberateHole: false);

        var explanation = TreeBinderExplain.Explain(node, affixes, atoms, RealPowerTuning());

        Assert.Contains("stored kMicro         3105", explanation);
    }

    [Fact]
    public void Explain_names_a_refusal_rather_than_throwing()
    {
        var node = new BindInputNode("skill.might-off-t1-n0", 1000, 1000, 9, 2,
            Array.Empty<string>(), ExclusionForm.None, DeliberateHole: false);

        var explanation = TreeBinderExplain.Explain(node,
            new Dictionary<string, AffixRow>(), new Dictionary<string, AtomRow>(), RealPowerTuning());

        Assert.Contains("REFUSED", explanation);
        Assert.Contains("affixIds must be 1..3, got 0", explanation);
    }
}
