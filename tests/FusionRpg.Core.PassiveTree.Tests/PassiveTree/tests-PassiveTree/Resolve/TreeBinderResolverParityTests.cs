using System.Collections.Generic;
using System.IO;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>
/// The binder/resolver seam is a contract, not an aggregate. A binder-emitted kind that the
/// resolver drops must not be reported as a live contribution by <see cref="TreeResolveReport"/>.
/// </summary>
public class TreeBinderResolverParityTests
{
    static PowerTuning RealPowerTuning()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
        return PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(root, "data", "tuning", "power-scale.v2.json")));
    }

    [Fact]
    public void A_binder_emitted_kind_the_resolver_drops_is_not_reported_as_contributing()
    {
        const string affixId = "affix.synthetic.primary";
        const string atomId = "atom.synthetic.primary.t1";
        var affix = new AffixRow(affixId, null, new[] { new AffixRefRow(0, atomId) });
        var atom = new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.synthetic.primary",
            Tier = 1,
            Name = "synthetic primary",
            ParamsJson = """{"channel":"atk","op":"flat","amount":{"min":1,"max":1}}""",
            WhenJson = "{}",
        };
        var affixes = new Dictionary<string, AffixRow> { [affixId] = affix };
        var atoms = new Dictionary<string, AtomRow> { [atomId] = atom };
        var input = new BindInputNode(
            "skill.might-off-t5-n0", TreeShareMilli: 1000, TreeBudgetMilli: 1000,
            BudgetShareMilli: 45, Branches: 2, new[] { affixId }, ExclusionForm.None,
            DeliberateHole: false, Branch: TreeBranch.Off, Tier: 5, NodeKey: "n0");

        var bound = TreeBinderRun.BindNode(input, affixes, atoms, RealPowerTuning());
        var boundAtom = Assert.Single(bound.Atoms);
        Assert.Equal("stat.modify", boundAtom.KindId);

        var node = new NodeRecord(
            input.NodeId, "might", TreeBranch.Off, 5, "n0", Array.Empty<string>(),
            NodeClass.Magnitude, new[] { affixId }, 45, new[] { boundAtom },
            Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var tree = new LoadedTree(
            new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
                "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[] { node });
        var owned = new HashSet<string> { node.NodeId };
        var tuning = RealPowerTuning();

        // The current resolver contract is explicit: only stat.derived reaches the shared Hub shape.
        Assert.Empty(TreeAtomSource.BoundAtomsFor(tree, owned, 10, 100, tuning, 1000));

        // A report that calls this node "contributing" disagrees with the actual read seam.
        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, 10, 45, owned,
            lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);
        Assert.DoesNotContain(node.NodeId, report.ContributingNodeIds);
    }
}
