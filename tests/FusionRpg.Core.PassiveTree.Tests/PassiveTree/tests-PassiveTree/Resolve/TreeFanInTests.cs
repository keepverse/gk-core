using System.IO;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task B6 acceptance (a) (spec-tree-resolve.md §2.1-2.3): "an allocated trait changes a
/// combat.* channel through the existing fan-in, no new subsystem/order band/eviction." Proven the
/// same way `AtomDerivedSubsystemTests` proves the lawn executor itself: register the REAL
/// <see cref="AtomDerivedSubsystem"/> (same type, same Order 350, same `ActorHub.Register` seam) on a
/// real <see cref="ActorHub"/>, with `TreeAtomSource.BoundAtomsFor` as (part of) its `boundFor`
/// delegate — never a fourth registration, never a private compose path.</summary>
public class TreeFanInTests
{
    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    static LoadedTree MightTree(long kMicro) =>
        new(new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
                "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[]
            {
                new NodeRecord("skill.might-off-t3-n0", "might", TreeBranch.Off, 3, "n0",
                    Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
                    new[]
                    {
                        new NodeAtom("stat.derived", AttachPoint.Stat, DerivedStatChannels.CombatPowerOmni,
                            NodeAtomOp.Flat, null, null, kMicro, ScaleAxis.PTheta,
                            UnitClass.GameUnits),
                    },
                    Array.Empty<string>(), ExclusionForm.None, null, true, null),
            });

    static StatContext Ctx(string entityKey) => new()
    {
        Side = StatSide.Plant,
        EntityKey = entityKey,
        Baseline = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 },
    };

    /// <summary>The composed delegate shape production wiring would use: the SAME single
    /// `boundFor` slot `AtomDerivedSubsystem` already exposes, with the tree's contribution folded in
    /// alongside whatever else feeds it (granted atoms today) -- composition, never a second producer
    /// registered against the hub.</summary>
    static FusionRpg.Core.Stats.Derived.ActorHub HubWithTree(
        LoadedTree tree, IReadOnlySet<string> owned, int tierReached, long thetaNode, long fMilli = 1000)
    {
        var tuning = RealPowerTuning();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(_ =>
            TreeAtomSource.BoundAtomsFor(tree, owned, tierReached, thetaNode, tuning, fMilli)));
        return hub;
    }

    [Fact]
    public void An_allocated_node_moves_a_real_combat_channel_through_the_shipped_fan_in()
    {
        var tree = MightTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var hub = HubWithTree(tree, owned, tierReached: 10, thetaNode: 100);

        // P(100) = 4680 on the shipped curve (verified by TreeAtomSourceTests against the same
        // power-scale.v2.json); 3038 * 4680 / 1e6 ~= 14.2 -- the same worked example, now read back
        // through the actual composed derived snapshot rather than the bare producer function.
        var value = hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni);
        Assert.True(value > 0, "an owned, gate-open node must move the channel it targets");
        Assert.InRange(value, 13, 16);
    }

    [Fact]
    public void An_unallocated_node_on_the_same_tree_leaves_the_channel_untouched()
    {
        var tree = MightTree(kMicro: 3038);
        var hub = HubWithTree(tree, owned: new HashSet<string>(), tierReached: 10, thetaNode: 100);

        Assert.Equal(0, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact]
    public void No_new_order_band_the_registered_subsystem_is_the_shipped_atom_derived_one_at_350()
    {
        // Names the exact invariant the acceptance criterion states: composing a producer into the
        // EXISTING seam must never require a new IActorStatSubsystem or a new Order value.
        var subsystem = new AtomDerivedSubsystem(_ => Array.Empty<BoundDerivedAtom>());
        Assert.Equal("atom.derived", subsystem.SubsystemId);
        Assert.Equal(350, subsystem.Order);
    }

    [Fact]
    public void Withdrawing_the_allocation_returns_the_channel_to_its_prior_value_no_eviction_needed()
    {
        // "no eviction" (acceptance (a)): un-owning a node is just the producer returning nothing next
        // resolve -- the same statelessness AtomDerivedSubsystemTests' withdraw test already proves for
        // auras, here proven for a tree allocation specifically.
        var tree = MightTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(_ =>
            TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000)));

        Assert.True(hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni) > 0);
        owned.Clear();
        Assert.Equal(0, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact]
    public void Item_bonuses_never_move_the_gate_only_aptitude_points_and_catalog_depth_do()
    {
        // Acceptance (b)'s negative half, proven at the fan-in boundary: TierGate.Reached's signature
        // accepts only aptitude points and the catalog's own authored tier count -- there is no
        // parameter through which an item bonus (or anything else) could raise the reached tier.
        var t = typeof(TierGate).GetMethod(nameof(TierGate.Reached))!;
        var names = Array.ConvertAll(t.GetParameters(), p => p.Name);
        Assert.Equal(new[] { "aptitudePoints", "authoredTierCount", "reqScalePoints" }, names);
    }
}
