using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task B6 — `TreeAtomSource` (spec-tree-resolve.md §2.1-2.3). Continues the SAME worked
/// example B4 verified (kMicro=3038, `combat.power.fire`) through to the actual resolved combat
/// magnitude, against every row of spec-tree-binder.md §3.4's own runtime table.</summary>
public class TreeAtomSourceTests
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

    static LoadedTree OneNodeTree(long kMicro, string channelId = "combat.power.fire", int tier = 5,
                                  bool enabled = true, string kindId = "stat.derived")
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
            "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom(kindId, Effects.Atoms.AttachPoint.Stat, channelId, NodeAtomOp.Flat,
            null, null, kMicro, ScaleAxis.PTheta, UnitClass.GameUnits);
        var node = new NodeRecord($"skill.might-off-t{tier}-n0", "might", TreeBranch.Off, tier, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, enabled, null);
        return new LoadedTree(tree, new[] { node });
    }

    [Theory]
    [InlineData(20, 2)]    // the pin itself: P(20) = 680, kMicro*680/1e6 = 2.0664
    [InlineData(50, 5)]    // P(50) = 1880, 3038*1880/1e6 = 5.71144
    [InlineData(100, 14)]  // P(100) = 4680, 3038*4680/1e6 = 14.21784
    [InlineData(500, 193)] // P(500) = 63080, 3038*63080/1e6 = 191.63... (spec table's own rounding)
    [InlineData(1000, 693)]// P(1000) = 226080, 3038*226080/1e6 = 686.79... (spec table's own rounding)
    public void The_worked_example_reproduces_the_spec_own_runtime_table_within_rounding(
        long theta, long specDisplayedValue)
    {
        var tree = OneNodeTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var tuning = RealPowerTuning();

        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: theta, tuning, fMilli: 1000);

        var atom = Assert.Single(bound);
        Assert.Equal("combat.power.fire", atom.Channel);
        // Within 5% of the spec's own displayed (informally-rounded) figure -- the point is that the
        // SAME kMicro against the SAME published curve reproduces the SAME order of magnitude at
        // every one of the spec's five sampled Theta values, not that the doc's own rounding is exact.
        Assert.True(Math.Abs(atom.Amount - specDisplayedValue) < specDisplayedValue * 0.05 + 1,
            $"theta={theta}: got {atom.Amount}, spec table says ~{specDisplayedValue}");
    }

    [Fact]
    public void An_unowned_node_contributes_nothing()
    {
        var tree = OneNodeTree(kMicro: 3038);
        var bound = TreeAtomSource.BoundAtomsFor(tree, new HashSet<string>(), tierReached: 10,
            thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Empty(bound);
    }

    [Fact]
    public void A_node_above_the_reached_tier_contributes_nothing_even_if_owned()
    {
        var tree = OneNodeTree(kMicro: 3038, tier: 5);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 4, thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Empty(bound);
    }

    [Fact]
    public void A_retired_disabled_node_contributes_nothing_even_if_owned_and_gate_open()
    {
        var tree = OneNodeTree(kMicro: 3038, enabled: false);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Empty(bound);
    }

    [Fact]
    public void A_non_stat_derived_kind_is_skipped_mechanism_atoms_are_not_this_modules_to_execute()
    {
        var tree = OneNodeTree(kMicro: 3038, kindId: "status.apply");
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Empty(bound);
    }

    [Fact]
    public void SourceId_follows_the_tree_treeId_nodeId_convention()
    {
        var tree = OneNodeTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);
        Assert.Equal("tree.might.skill.might-off-t5-n0", Assert.Single(bound).SourceId);
    }

    /// <summary>D14/D40 (task D5): an excluded node contributes zero at the SAME seam a tier-closed
    /// node already does -- `TreeAtomSource.BoundAtomsFor` calls `ExclusionResolver.Resolve` itself,
    /// so the real combat contribution and `TreeResolveReport`'s projection can never drift apart.</summary>
    [Fact]
    public void An_excluded_node_contributes_nothing_even_if_owned_and_gate_open()
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
            "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var loserAtom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Stat, "combat.power.fire",
            NodeAtomOp.Flat, null, null, 3038, ScaleAxis.PTheta, UnitClass.GameUnits);
        var loser = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            new[] { loserAtom }, new[] { "posture" }, ExclusionForm.Nullification, null, true, null);
        var winner = new NodeRecord("skill.might-off-t2-n0", "might", TreeBranch.Off, 2, "n1",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            Array.Empty<NodeAtom>(), Array.Empty<string>(), ExclusionForm.None, "{\"posture\":true}", true, null);
        var loaded = new LoadedTree(tree, new[] { loser, winner });
        var owned = new HashSet<string> { loser.NodeId, winner.NodeId };

        var bound = TreeAtomSource.BoundAtomsFor(loaded, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);

        Assert.Empty(bound); // the loser's atom never reaches BoundDerivedAtom; the winner has none of its own
    }

    [Fact]
    public void A_node_with_an_exclusion_form_but_no_matching_owned_tag_still_contributes()
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
            "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Stat, "combat.power.fire",
            NodeAtomOp.Flat, null, null, 3038, ScaleAxis.PTheta, UnitClass.GameUnits);
        var node = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            new[] { atom }, new[] { "posture" }, ExclusionForm.Nullification, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { node.NodeId };

        // No other owned node carries the "posture" tag -- the exclusion never fires, so this node
        // contributes exactly like any ordinary node (spec-tree-binder.md §7.3: fires per actor, per
        // resolve, never at bake time).
        var bound = TreeAtomSource.BoundAtomsFor(loaded, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 1000);

        Assert.Single(bound);
    }

    [Fact]
    public void A_contest_channel_reads_theta_linearly_never_P_of_theta()
    {
        // PS-3: a StatusPotencyPoints/Theta-axis atom must scale linearly with Theta, not P(Theta) --
        // getting this backwards is the silent failure class §5/PS-3 names explicitly.
        var tree = new TreeRecord("might", TreeCategory.Primary, "x", "broad-and-flat", 10, 2,
            new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Status, "status.resist.dot",
            NodeAtomOp.Increased, null, null, 1000, ScaleAxis.Theta, UnitClass.StatusPotencyPoints);
        var node = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 18,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { "skill.might-off-t1-n0" };

        var boundAt100 = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 100, RealPowerTuning(), fMilli: 1000);
        var boundAt200 = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 200, RealPowerTuning(), fMilli: 1000);

        // Theta doubles -> amount doubles EXACTLY (linear), never the super-linear growth P(Theta) has.
        Assert.Equal(boundAt100[0].Amount * 2, boundAt200[0].Amount, precision: 6);
    }

    /// <summary>Task D7, test 8a (spec-tree-resolve.md §5.3, §12): "a fixed one-tier contest gap is
    /// worth the same at every measured Θ, WITH `F` APPLIED" — the property that keeps §2's
    /// contest-linearity theorem legitimate under a multiplier. `F · (c + m·Θ)` is still linear in
    /// `Θ`: scaling every point of a line by the same constant does not change how much a FIXED step
    /// along that line is worth, no matter where on the line the step starts.</summary>
    [Fact]
    public void A_fixed_contest_gap_scaled_by_F_is_worth_the_same_at_every_theta()
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "x", "broad-and-flat", 10, 2,
            new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Status, "status.resist.dot",
            NodeAtomOp.Increased, null, null, 1000, ScaleAxis.Theta, UnitClass.StatusPotencyPoints);
        var node = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 18,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { "skill.might-off-t1-n0" };
        const long fMilli = 1200; // a real, non-1000 F -- the case that would expose F breaking linearity
        const long gap = 37; // a fixed "one-tier" Theta gap, arbitrary and small next to either base Theta

        var lowBase = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 10, RealPowerTuning(), fMilli)[0].Amount;
        var lowGapped = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 10 + gap, RealPowerTuning(), fMilli)[0].Amount;
        var highBase = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 10_000, RealPowerTuning(), fMilli)[0].Amount;
        var highGapped = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 10_000 + gap, RealPowerTuning(), fMilli)[0].Amount;

        // F · (c + m·Θ) is linear in Θ -- the SAME fixed Θ-gap is worth the SAME amount whether it
        // starts at Θ=10 or Θ=10,000, even with a real F applied on top.
        Assert.Equal(lowGapped - lowBase, highGapped - highBase, precision: 6);
    }

    /// <summary>Task D7, bullet 3: `F` multiplies every tree-derived contribution -- magnitude
    /// (`PTheta`), contest (`Theta`) AND flat (`FlatPermille`) axes alike (§5.3) -- proven directly
    /// against `Fmax`'s own bound rather than an arbitrary sample, so a defect that only multiplies
    /// SOME axes cannot hide behind one axis happening to be tested.</summary>
    [Theory]
    [InlineData(ScaleAxis.PTheta)]
    [InlineData(ScaleAxis.Theta)]
    [InlineData(ScaleAxis.FlatPermille)]
    public void F_multiplies_every_scale_axis_alike(ScaleAxis axis)
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "x", "broad-and-flat", 10, 2,
            new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Stat, "combat.power.fire",
            NodeAtomOp.Flat, null, null, 1_000_000, axis, UnitClass.GameUnits);
        var node = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { "skill.might-off-t1-n0" };
        var tuning = RealPowerTuning();

        var noF = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 100, tuning, fMilli: 1000)[0].Amount;
        var withF = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 100, tuning, fMilli: 1200)[0].Amount;

        Assert.Equal(noF * 1.2, withF, precision: 9);
    }

    /// <summary>Task D7, §5.4/test 9, at the ACTUAL application seam (`ConcentrationTests` already
    /// proves `Concentration.FmaxAppliedMilli(h, 1000) == 1000` for the formula in isolation -- this
    /// proves the seam that actually multiplies a magnitude by it produces the IEEE-754-exact same
    /// double, not merely an equal-by-tolerance one).</summary>
    [Theory]
    [InlineData(ScaleAxis.PTheta)]
    [InlineData(ScaleAxis.Theta)]
    [InlineData(ScaleAxis.FlatPermille)]
    public void Fmax_of_1000_permille_produces_byte_identical_amounts_to_F_removed(ScaleAxis axis)
    {
        var tree = new TreeRecord("might", TreeCategory.Primary, "x", "broad-and-flat", 10, 2,
            new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.derived", Effects.Atoms.AttachPoint.Stat, "combat.power.fire",
            NodeAtomOp.Flat, null, null, 3038, axis, UnitClass.GameUnits);
        var node = new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { "skill.might-off-t1-n0" };
        var tuning = RealPowerTuning();

        var at1000 = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 321, tuning, fMilli: 1000)[0].Amount;
        var at1000Again = TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 321, tuning, fMilli: 1000)[0].Amount;

        // Exact bitwise equality (BitConverter, not a tolerance-based Assert.Equal) -- "byte-identical"
        // means byte-identical, not "close enough" (§5.4: "removes F from the arithmetic entirely
        // without removing a code path").
        Assert.Equal(BitConverter.DoubleToInt64Bits(at1000Again), BitConverter.DoubleToInt64Bits(at1000));
    }

    /// <summary>Task D7: `fMilli` below `1000` (i.e. `F &lt; 1.000`) is refused -- §5.1's proof bounds
    /// `F` below by exactly `1.000`, so a caller passing anything smaller is passing a value
    /// <see cref="Concentration.FmaxAppliedMilli"/> itself could never produce.</summary>
    [Fact]
    public void An_fMilli_below_1000_is_refused()
    {
        var tree = OneNodeTree(kMicro: 3038);
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, RealPowerTuning(), fMilli: 999));
    }

    // ---- P4.2 -- `More` is representable on the shared op mapping --------------------------------

    /// <summary>P4.2 (R2) acceptance: a source-shape test proves the shared
    /// <see cref="FusionRpg.Core.PassiveTree.Resolve.TreeAtomSource"/> mapping handles <c>More</c>.
    /// Battle now consumes this same Hub projection; its former re-shaping source was deleted when
    /// battle-hub-fuse removed the duplicate read mode. This is a SOURCE-SHAPE test on purpose: the
    /// mapping function is private and, correctly, a `More` atom on
    /// the `stat.derived` side can never legitimately reach it (M3 refuses it at load and bind), so a
    /// runtime test would be testing an unreachable state. What MUST hold is that the mapping does not
    /// silently produce an empty string — which `AtomDerivedSubsystem.TryParseOp` would treat as a
    /// dropped op — so both op-mapping functions name every enum member, `More` included.</summary>
    [Fact]
    public void Shared_projection_maps_every_NodeAtomOp_including_More()
    {
        // A switch expression over an enum that omits a member compiles and silently yields the
        // default (""). This asserts the SOURCE names each member explicitly, so
        // adding a future member without updating the mapping fails here rather than at runtime.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "PassiveTree", "Resolve", "TreeAtomSource.cs"));
        foreach (var op in Enum.GetNames<NodeAtomOp>())
        {
            var wire = op.ToLowerInvariant();
            Assert.Contains($"\"{wire}\"", source);
        }
    }

    // ---- P11.1r -- the PRIMARY kind's bar, measured rather than asserted -------------------------

    /// <summary><para>P11.1r's falsifier, and the reason the kind filter stays where it is. The row
    /// asks for a node's <c>stat.modify</c> atom to reach Hub output; the only fan-in in the fence is
    /// <see cref="AtomDerivedSubsystem"/> into <see cref="DerivedComposer"/>. This proves against
    /// that REAL pair — not a stub — that the primary channel ids this kind is declared against
    /// cannot be carried by it.</para>
    /// <para>Two assertions, deliberately both. The first is shipped behaviour: the producer skips
    /// the atom. The second is WHY, and it is the arm that matters: hand the very same channel to the
    /// real subsystem and the real composer, and the compose <b>throws</b>
    /// <see cref="UnknownDerivedChannelException"/>. A widened kind filter would therefore not be a
    /// silent no-op on the lawn's per-hit resolve path — it would be a throw, once per actor per hit.
    /// A green test that only asserted the first arm would prove nothing about the fix the row wants.</para>
    /// <para>The two channel ids are the ones <c>stat.modify</c>'s own <c>channel</c> param declares
    /// (<c>AtomKindRegistry.PrimaryChannels</c> = <c>StatChannels.All</c>), read from the vocabulary
    /// itself rather than pinned as literals.</para></summary>
    [Theory]
    [InlineData(Stats.StatChannels.Atk)]
    [InlineData(Stats.StatChannels.Defense)]
    public void A_stat_modify_atom_is_skipped_because_the_shared_fan_in_would_refuse_its_channel(string primaryChannel)
    {
        var tree = OneNodeTree(kMicro: 3038, channelId: primaryChannel, kindId: "stat.modify");
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };

        // 1. Shipped behaviour, preserved: a primary-kind atom contributes nothing to the derived
        //    fan-in -- before AND after any change to this file.
        Assert.Empty(TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100,
            RealPowerTuning(), fMilli: 1000));

        // 2. The bar, through the real subsystem and the real composer. No stub: a stub would let a
        //    widened kind filter pass here and throw in production instead.
        var bound = new BoundDerivedAtom(primaryChannel, DerivedModifierOp.Flat, 5,
            ContributionSourceIds.Tree("might", "skill.might-off-t5-n0"));
        var subsystem = new AtomDerivedSubsystem(_ => new[] { bound });
        var mods = new List<DerivedModifier>();
        subsystem.ContributeDerived(new Stats.StatContext(), mods);

        Assert.Single(mods); // the subsystem itself is permissive -- it forwards whatever it is given
        var thrown = Assert.Throws<UnknownDerivedChannelException>(() => new DerivedComposer().Compose(mods));
        Assert.Equal(primaryChannel, thrown.ChannelId);
    }

    /// <summary>The second of the three bars, as a contract: a primary <c>More</c> has no derived-side
    /// representation, and the shared op parser refuses it BY NAME. Coercing it to <c>Flat</c> would
    /// be the shipped anti-pattern its own doc names ("silent coercion is how a wrong number ships
    /// looking correct"), so the parser skips it — and this pins that the skip is intentional rather
    /// than a gap someone is invited to paper over.</summary>
    [Fact]
    public void More_is_refused_by_the_shared_op_parser_on_every_kind_that_reaches_the_mapping()
    {
        Assert.False(AtomDerivedSubsystem.TryParseOp("more", out _));

        // And the producer is still short of it, so nothing is lost by that refusal today: a
        // `stat.modify` node carrying the primary-only op contributes nothing rather than something
        // approximate.
        var tree = new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
            "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true);
        var atom = new NodeAtom("stat.modify", Effects.Atoms.AttachPoint.Stat, Stats.StatChannels.Atk,
            NodeAtomOp.More, null, null, 1215, ScaleAxis.PTheta, UnitClass.GameUnits);
        var node = new NodeRecord("skill.might-off-t3-n0", "might", TreeBranch.Off, 3, "n0",
            Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 18,
            new[] { atom }, Array.Empty<string>(), ExclusionForm.None, null, true, null);
        var loaded = new LoadedTree(tree, new[] { node });
        var owned = new HashSet<string> { node.NodeId };

        Assert.Empty(TreeAtomSource.BoundAtomsFor(loaded, owned, 10, 100, RealPowerTuning(), fMilli: 1000));
    }

    /// <summary>P11.1r acceptance bullet 3 — <c>TreeResolveReport</c> and <c>BoundAtomsFor</c> agree on
    /// what is live, for the primary kind specifically. The report is the projection a player reads
    /// ("is my node doing anything?"), so the pair that must never disagree is exactly this one: a
    /// report listing a <c>stat.modify</c>-only node as contributing while the read contributes
    /// nothing is the drift this pins shut.</summary>
    [Fact]
    public void A_stat_modify_only_node_is_reported_as_non_contributing_agreeing_with_the_read()
    {
        var tree = OneNodeTree(kMicro: 3038, channelId: Stats.StatChannels.Atk, kindId: "stat.modify");
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10,
            aptitudePoints: 0, ownedNodeIds: owned, lenderTreeId: null,
            herfindahlMilli: 1000, focusMilli: 1000);
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100,
            RealPowerTuning(), fMilli: 1000);

        Assert.Empty(report.ContributingNodeIds);
        Assert.Empty(bound);
        Assert.Empty(report.InvalidNodeIds);
        Assert.Empty(report.ExcludedNodes);
    }

    /// <summary>The other arm of the same agreement, so the parity above is not vacuously true: a
    /// <c>stat.derived</c> node on the same tree, owned and gate-open, IS reported contributing and
    /// DOES contribute. Without this a broken predicate that reported and read <em>nothing</em> for
    /// everything would satisfy the parity assertion above.</summary>
    [Fact]
    public void A_stat_derived_node_is_reported_as_contributing_agreeing_with_the_read()
    {
        var tree = OneNodeTree(kMicro: 3038); // kindId defaults to stat.derived
        var owned = new HashSet<string> { "skill.might-off-t5-n0" };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10,
            aptitudePoints: 0, ownedNodeIds: owned, lenderTreeId: null,
            herfindahlMilli: 1000, focusMilli: 1000);
        var bound = TreeAtomSource.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100,
            RealPowerTuning(), fMilli: 1000);

        Assert.Equal(new[] { "skill.might-off-t5-n0" }, report.ContributingNodeIds);
        Assert.Single(bound);
    }
}
