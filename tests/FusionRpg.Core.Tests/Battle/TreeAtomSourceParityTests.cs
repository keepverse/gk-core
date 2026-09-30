using System.IO;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;
using LawnTreeAtomSource = FusionRpg.Core.PassiveTree.Resolve.TreeAtomSource;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>Task D6 — battle-ops-parity (T7) deleted the dead <c>Battle.TreeAtomSource.ModsFor</c>
/// adapter (zero production callers -- <c>BattleStatComposer</c>, the only thing that could have
/// consumed a "third producer slot," was itself deleted in battle-hub-fuse T6). Tree contributions
/// reach battle the same way equip does now: <see cref="LawnTreeAtomSource.BoundAtomsFor"/> already
/// returns <see cref="BoundDerivedAtom"/> -- the exact type <see cref="BattleHubInputs.BoundAtoms"/>
/// wants -- so there is no adapter left to write; the lawn's own resolve feeds
/// <see cref="BattleHubCompose"/> directly. This file now proves that capability, not a deleted
/// static's own arithmetic.</summary>
public class TreeAtomSourceParityTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static PowerTuning RealPowerTuning() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    // FlatPermille is a straight kMicro/1e6 division with no P(Theta)/Theta scaling at all -- picking
    // a kMicro that is an exact multiple of 1,000,000 makes the lawn's own double Amount an EXACT
    // whole number, so the lawn<->battle double->long rounding step introduces no ambiguity and the
    // parity claim is a genuine equality, not a "close enough" one.
    static LoadedTree FlatTree(long kMicro, string channelId) =>
        new(new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
                "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[]
            {
                new NodeRecord("skill.might-off-t3-n0", "might", TreeBranch.Off, 3, "n0",
                    Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
                    new[]
                    {
                        new NodeAtom("stat.derived", AttachPoint.Stat, channelId, NodeAtomOp.Flat,
                            null, null, kMicro, ScaleAxis.FlatPermille, UnitClass.PerMilleRatio),
                    },
                    Array.Empty<string>(), ExclusionForm.None, null, true, null),
            });

    static BattleActorSetup BattleSetupWith(IReadOnlyList<BoundDerivedAtom>? gear) => new()
    {
        Key = "s1", Side = "squad", SpeciesId = "spec", TypeId = 1, Level = 5,
        MaxHp = 100, Atk = 50, Defense = 20,
        HubInputs = gear is null ? null : new BattleHubInputs { BoundAtoms = gear },
    };

    [Fact]
    public void Lawn_and_battle_hub_resolve_to_the_same_total_for_one_actor()
    {
        var tree = FlatTree(kMicro: 7_000_000, channelId: DerivedStatChannels.CombatPowerOmni); // -> 7.0 exactly
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();

        var bound = LawnTreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);

        var lawnTotal = bound.Where(a => a.Channel == DerivedStatChannels.CombatPowerOmni).Sum(a => a.Amount);
        Assert.Equal(7.0, lawnTotal);

        var bare = BattleHubCompose.Compose(BattleSetupWith(null)).Get(DerivedStatChannels.CombatPowerOmni);
        var geared = BattleHubCompose.Compose(BattleSetupWith(bound)).Get(DerivedStatChannels.CombatPowerOmni);
        Assert.Equal(lawnTotal, geared - bare); // the actual parity claim: the same total reaches battle Hub
    }

    [Fact]
    public void Attribution_reaches_the_lawn_contribution_bag_unchanged_via_the_shared_fan_in()
    {
        var tree = FlatTree(kMicro: 3_000_000, channelId: DerivedStatChannels.CombatDefenseOmni);
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();

        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(_ =>
            LawnTreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000)));

        var ctx = new StatContext
        {
            Side = StatSide.Plant, EntityKey = "abc",
            Baseline = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 },
        };
        var (_, contributions) = hub.ResolveDerivedWithContributions(ctx);
        var one = Assert.Single(contributions.ContributionsFor(DerivedStatChannels.CombatDefenseOmni));

        Assert.Equal("tree.might.skill.might-off-t3-n0", one.SourceId); // GG-49: one row per node
        Assert.Equal(3.0, one.Value);
    }

    [Fact]
    public void Battle_composition_uses_no_new_subsystem_the_same_AtomDerivedSubsystem_carries_tree_and_equip()
    {
        // battle-ops-parity T7: no adapter, no third producer slot -- tree atoms are BoundDerivedAtom,
        // fed into the SAME HubInputs.BoundAtoms field / AtomDerivedSubsystem equip already uses.
        var tree = FlatTree(kMicro: 1_000_000, channelId: DerivedStatChannels.CombatPowerOmni);
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var bound = LawnTreeAtomSource.BoundAtomsFor(tree, owned, 10, 100, RealPowerTuning(), fMilli: 1000);

        var atom = Assert.Single(bound);
        Assert.IsType<BoundDerivedAtom>(atom);
        Assert.Equal(DerivedStatChannels.CombatPowerOmni, atom.Channel);
        Assert.Equal("tree.might.skill.might-off-t3-n0", atom.SourceId); // GG-49 attribution carried straight through, no adapter to drop it
    }

    [Fact]
    public void An_unowned_node_contributes_nothing_to_battle_hub()
    {
        var tree = FlatTree(kMicro: 5_000_000, channelId: DerivedStatChannels.CombatPowerOmni);
        var bound = LawnTreeAtomSource.BoundAtomsFor(tree, new HashSet<string>(), tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Empty(bound);

        var bare = BattleHubCompose.Compose(BattleSetupWith(null)).Get(DerivedStatChannels.CombatPowerOmni);
        var withEmptyTree = BattleHubCompose.Compose(BattleSetupWith(bound)).Get(DerivedStatChannels.CombatPowerOmni);
        Assert.Equal(bare, withEmptyTree);
    }

    /// <summary>Task D7, bullet 3: "`F` multiplies every tree-derived contribution... IN BOTH READ
    /// MODES." Battle no longer runs its own rounding step at all (the deleted
    /// <c>Battle.TreeAtomSource.ModsFor</c> used to be the one place that did) -- the lawn's own
    /// <c>BoundAtomsFor</c> amount reaches <see cref="BattleHubCompose"/> exactly, F-scaled once.</summary>
    [Fact]
    public void F_reaches_battle_hub_the_same_way_it_reaches_the_lawn_amount()
    {
        var tree = FlatTree(kMicro: 5_000_000, channelId: DerivedStatChannels.CombatPowerOmni); // -> 5.0 before F
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();

        var lawnNoF = LawnTreeAtomSource.BoundAtomsFor(tree, owned, 10, 100, tuning, fMilli: 1000)[0].Amount;
        var lawnWithF = LawnTreeAtomSource.BoundAtomsFor(tree, owned, 10, 100, tuning, fMilli: 1200);

        Assert.Equal(5.0, lawnNoF);
        Assert.Equal(6.0, lawnWithF[0].Amount); // 5.0 * 1.2 -- F genuinely reached the lawn amount

        var bare = BattleHubCompose.Compose(BattleSetupWith(null)).Get(DerivedStatChannels.CombatPowerOmni);
        var geared = BattleHubCompose.Compose(BattleSetupWith(lawnWithF)).Get(DerivedStatChannels.CombatPowerOmni);
        Assert.Equal(6.0, geared - bare); // and the SAME F-scaled amount reached battle Hub, exactly
    }
}
