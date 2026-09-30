using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>
/// Task D2 — the whole-node/whole-tree orchestration B4 explicitly deferred
/// ("the CLI wrapper and full-tree orchestration are mechanical composition of what's already
/// proven … left for a follow-up pass"). Covers spec-tree-binder.md §7.2 (refused-slot reporting,
/// unspent budget, FAIL verdict, the deliberate-hole suppression flag) and §7.3 (an excluded node,
/// nullification included, binds identically to an unexcluded one).
/// </summary>
public class TreeBinderRunTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    // ---- Worked-example fixture: spec-tree-binder.md §3.4 -----------------------------------
    // "broad-and-flat, tier 5, offensive branch, first of the two, combat.power.fire, stat.derived,
    // op flat" -- budgetShareMilli 45 -> kMicro 3038, the sibling at 46 -> 3105.

    const string WorkedAffixId = "affix.worked-example.t5-n0";
    const string WorkedAtomId = "atom.worked-example.t5-n0";

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

    static BindInputNode WorkedExampleNode(string nodeId, long budgetShareMilli,
        ExclusionForm exclusionForm = ExclusionForm.None, bool deliberateHole = false) =>
        new(nodeId, TreeShareMilli: 1000, TreeBudgetMilli: 1000, budgetShareMilli, Branches: 2,
            new[] { WorkedAffixId }, exclusionForm, deliberateHole);

    // ---- 1. The worked example, end to end through the orchestrator (not just CoefficientBinder) --

    [Fact]
    public void BindNode_reproduces_the_worked_example_share_45_as_3038()
    {
        var (affixes, atoms) = WorkedExampleContent();
        var node = WorkedExampleNode("skill.might-off-t5-n0", 45);

        var bound = TreeBinderRun.BindNode(node, affixes, atoms, RealPowerTuning());

        var atom = Assert.Single(bound.Atoms);
        Assert.Equal("combat.power.fire", atom.ChannelId);
        Assert.Equal(NodeAtomOp.Flat, atom.Op);
        Assert.Equal(UnitClass.GameUnits, atom.UnitClass);
        Assert.Equal(ScaleAxis.PTheta, atom.ScaleAxis);
        Assert.Equal(3038L, atom.KMicro);
    }

    [Fact]
    public void BindNode_reproduces_the_worked_examples_sibling_share_46_as_3105()
    {
        var (affixes, atoms) = WorkedExampleContent();
        var node = WorkedExampleNode("skill.might-off-t5-n1", 46);

        var bound = TreeBinderRun.BindNode(node, affixes, atoms, RealPowerTuning());

        Assert.Equal(3105L, Assert.Single(bound.Atoms).KMicro);
    }

    // ---- 2. §7.2 -- a refused slot names its unspent budget and fails the run ------------------

    /// <summary>Superseded 2026-09-07: this test used `element.convert` as its "guaranteed unregistered
    /// kind" fixture. D56 shipped `element.convert` as a real, registered kind the same day, so it no
    /// longer refuses (see `AffixComposerTests.A_conversion_kind_atom_resolves_successfully_now_that_
    /// D56_shipped_it`) — this test's OWN subject is the generic §7.2 refusal/unspent-budget/FAIL-verdict
    /// machinery, not conversion specifically, so it is repointed at a genuinely unregistered kind
    /// rather than deleted.</summary>
    [Fact]
    public void An_unregistered_kind_node_is_refused_its_unspent_budget_is_named_and_the_run_fails()
    {
        var badAffixId = "affix.synthetic.unregistered";
        var badAtomId = "atom.synthetic.unregistered";
        var affix = new AffixRow(badAffixId, null, new[] { new AffixRefRow(0, badAtomId) });
        var badAtom = new AtomRow
        {
            AtomId = badAtomId,
            KindId = "not.a.real.kind", // guaranteed absent from AtomKindRegistry
            FamilyId = badAtomId,
            Tier = 1,
            Name = "synthetic",
            ParamsJson = """{"channel":"combat.power.fire","op":"flat"}""",
            WhenJson = "{}",
        };
        var affixes = new Dictionary<string, AffixRow> { [badAffixId] = affix };
        var atoms = new Dictionary<string, AtomRow> { [badAtomId] = badAtom };

        var (workedAffixes, workedAtoms) = WorkedExampleContent();
        var nodes = new[]
        {
            WorkedExampleNode("skill.might-off-t5-n0", 45), // binds cleanly
            new BindInputNode("skill.might-off-t7-b3", 1000, 1000, 64, 2,
                new[] { badAffixId }, ExclusionForm.None, DeliberateHole: false),
        };
        var allAffixes = workedAffixes.Concat(affixes).ToDictionary(kv => kv.Key, kv => kv.Value);
        var allAtoms = workedAtoms.Concat(atoms).ToDictionary(kv => kv.Key, kv => kv.Value);

        var report = TreeBinderRun.BindTree(nodes, allAffixes, allAtoms, RealPowerTuning());

        Assert.Single(report.Bound);
        var refusal = Assert.Single(report.Refused);
        Assert.Equal("skill.might-off-t7-b3", refusal.NodeId);
        Assert.Equal(64L, refusal.UnspentBudgetShareMilli);
        Assert.False(refusal.DeliberateHole);
        Assert.Contains("unregistered kind", refusal.Reason);
        Assert.Equal(64L, report.TotalUnspentBudgetShareMilli);

        // §7.2 item 3: FAIL, never a silent partial success.
        Assert.Equal(RunVerdict.Fail, report.Verdict);
    }

    // ---- 3. §7.2's "consequence for tree-plan" -- a deliberate hole is reported but doesn't fail --

    /// <summary>Superseded 2026-09-07: this test used `element.convert` as its "guaranteed unregistered
    /// kind" fixture, the same way the previous test did — see that test's own note. Repointed at a
    /// genuinely unregistered kind; the `DeliberateHole` suppression behavior under test is unaffected.</summary>
    [Fact]
    public void A_deliberate_hole_still_names_its_unspent_budget_but_does_not_fail_the_run()
    {
        var badAffixId = "affix.synthetic.unregistered2";
        var badAtomId = "atom.synthetic.unregistered2";
        var affix = new AffixRow(badAffixId, null, new[] { new AffixRefRow(0, badAtomId) });
        var badAtom = new AtomRow
        {
            AtomId = badAtomId,
            KindId = "not.a.real.kind",
            FamilyId = badAtomId,
            Tier = 1,
            Name = "synthetic",
            ParamsJson = """{"channel":"combat.power.fire","op":"flat"}""",
            WhenJson = "{}",
        };
        var affixes = new Dictionary<string, AffixRow> { [badAffixId] = affix };
        var atoms = new Dictionary<string, AtomRow> { [badAtomId] = badAtom };

        var nodes = new[]
        {
            new BindInputNode("skill.might-off-t7-b3", 1000, 1000, 64, 2,
                new[] { badAffixId }, ExclusionForm.None, DeliberateHole: true),
        };

        var report = TreeBinderRun.BindTree(nodes, affixes, atoms, RealPowerTuning());

        var refusal = Assert.Single(report.Refused);
        Assert.True(refusal.DeliberateHole);
        Assert.Equal(64L, refusal.UnspentBudgetShareMilli); // still named, never absorbed
        Assert.Equal(64L, report.TotalUnspentBudgetShareMilli);
        Assert.Equal(RunVerdict.Pass, report.Verdict); // suppressed -- not a surprise refusal
    }

    [Fact]
    public void A_mix_of_one_deliberate_hole_and_one_surprise_refusal_still_fails_the_run()
    {
        var report = BinderRunReport.From(
            bound: Array.Empty<BoundNode>(),
            refused: new[]
            {
                new RefusedSlot("a", "known", 10, DeliberateHole: true),
                new RefusedSlot("b", "surprise", 20, DeliberateHole: false),
            });

        Assert.Equal(RunVerdict.Fail, report.Verdict);
        Assert.Equal(30L, report.TotalUnspentBudgetShareMilli); // both named, nothing absorbed
    }

    // ---- 4. §7.3 -- an excluded node, nullification included, binds exactly like an unexcluded one --

    [Theory]
    [InlineData(ExclusionForm.None)]
    [InlineData(ExclusionForm.Reroute)]
    [InlineData(ExclusionForm.Precedence)]
    [InlineData(ExclusionForm.Nullification)]
    public void An_excluded_node_binds_the_same_kMicro_and_budget_as_the_unexcluded_form(ExclusionForm form)
    {
        var (affixes, atoms) = WorkedExampleContent();
        var baseline = TreeBinderRun.BindNode(
            WorkedExampleNode("skill.might-off-t5-n0", 45, ExclusionForm.None), affixes, atoms, RealPowerTuning());
        var excluded = TreeBinderRun.BindNode(
            WorkedExampleNode("skill.might-off-t5-n0", 45, form), affixes, atoms, RealPowerTuning());

        // Same node id, same atom count, same kMicro, same everything -- the only difference between
        // the two inputs is ExclusionForm, and it must produce zero difference in the output.
        // (BoundNode's synthesized record equality compares Atoms as a List<T> by REFERENCE, not
        // structurally, so the comparison is done field-by-field/element-by-element instead of via
        // a single Assert.Equal(baseline, excluded) on the whole record.)
        Assert.Equal(baseline.NodeId, excluded.NodeId);
        Assert.Equal(baseline.Atoms, excluded.Atoms);
    }

    [Fact]
    public void BindNode_source_never_reads_ExclusionForm()
    {
        // The pricing path must not special-case exclusion at all (§7.3) -- a per-actor runtime
        // property has no business deciding a bake-time, shared-catalog coefficient. A value test
        // alone cannot see a special case that happens to produce the same number by coincidence;
        // only reading the source can (the same discipline CoefficientBinderTests already applies
        // to tierWeight/weightTotal).
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "PassiveTree", "Binding", "TreeBinderRun.cs");
        var source = StripComments(File.ReadAllText(path));

        Assert.DoesNotContain("ExclusionForm", source);
    }

    static string StripComments(string source)
    {
        var noBlock = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//.*$", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    // ---- 5. Mechanism-class atoms compose but are not priced (B4's own stated boundary) --------

    [Fact]
    public void A_mechanism_class_atom_with_no_channel_composes_with_zero_priced_atoms()
    {
        var affixId = "affix.authored.affix-draw-000"; // the real shipped Frostbite Venom affix (B4)
        var content = LoadRealSeedContent();
        var (affixes, atoms) = (content.Affixes.ToDictionary(a => a.AffixId), content.Atoms.ToDictionary(a => a.AtomId));

        var node = new BindInputNode("skill.might-off-t1-mech", 1000, 1000, 9, 2,
            new[] { affixId }, ExclusionForm.None, DeliberateHole: false);

        var bound = TreeBinderRun.BindNode(node, affixes, atoms, RealPowerTuning());

        // Both real refs are status.apply (no channel/op) -- resolved, not priced.
        Assert.Empty(bound.Atoms);
    }

    // ---- 6. P4.2 -- a `more` op on a stat.modify atom parses, binds and is PRICED ----------------

    /// <summary>P4.2 (R2). The defect: `NodeAtomOp` had no `More`, so `ParseOp` refused every
    /// `more`-op node — but `stat.modify` legitimately supports `more` (`AtomKindRegistry.cs:517`), and
    /// the language stage picked such affixes on real nodes. This proves the whole bind path now works
    /// end to end for that case: the string `"more"` parses to <see cref="NodeAtomOp.More"/>, the node
    /// binds (not refuses), and the atom carries a non-zero `kMicro` — i.e. it is PRICED, not merely
    /// accepted. The same op on a `stat.derived` atom must still refuse (M3); that half is
    /// `ChannelLegalityTests`'.</summary>
    [Fact]
    public void A_more_op_stat_modify_atom_parses_binds_and_is_priced()
    {
        var affixId = "affix.synthetic.more";
        var atomId = "atom.synthetic.more";
        var affix = new AffixRow(affixId, null, new[] { new AffixRefRow(0, atomId) });
        var atom = new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = atomId,
            Tier = 1,
            Name = "synthetic more",
            ParamsJson = """{"channel":"atk","op":"more","amount":{"min":1,"max":1}}""",
            WhenJson = "{}",
        };
        var affixes = new Dictionary<string, AffixRow> { [affixId] = affix };
        var atoms = new Dictionary<string, AtomRow> { [atomId] = atom };
        var node = new BindInputNode("skill.might-off-t5-n0", 1000, 1000, 45, 2,
            new[] { affixId }, ExclusionForm.None, DeliberateHole: false);

        var bound = TreeBinderRun.BindNode(node, affixes, atoms, RealPowerTuning());

        var boundAtom = Assert.Single(bound.Atoms);
        Assert.Equal(NodeAtomOp.More, boundAtom.Op);
        Assert.Equal("atk", boundAtom.ChannelId);
        Assert.True(boundAtom.KMicro > 0, "a bound atom must be priced, not merely accepted");
    }

    static SeedContent LoadRealSeedContent()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(root, "data", "seed", "effects", "affixes", "all.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-board.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-core.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-status.json"),
        }.Where(File.Exists).Select(f => (f, File.ReadAllText(f))).ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        return collected.Content;
    }
}
