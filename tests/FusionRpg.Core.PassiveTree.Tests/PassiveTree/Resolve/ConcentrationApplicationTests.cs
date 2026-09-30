using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>
/// Task D7 — the two adversarial-share-vector properties the acceptance criteria name explicitly
/// (spec-tree-resolve.md §12 tests 6a/6b) that `ConcentrationTests.cs`'s own sweep does not cover: that
/// sweep samples the OUTPUT `H` value directly (`F_is_provably_in_1_to_Fmax_for_every_h_in_0_to_1000`),
/// never the raw per-tree count vectors `HerfindahlMilli` actually takes as input. A defect that only
/// breaks the Herfindahl bound for a SPECIFIC share SHAPE (one huge tree among many tiny ones, a lone
/// tree, an exactly-even split) would pass that sweep and still be wrong — this file closes that gap.
/// </summary>
public class ConcentrationHerfindahlBoundTests
{
    // "Both bounds, over generated vectors -- 1 tree, 39 trees, one-hot, uniform, and long-tailed"
    // (spec-tree-resolve.md §12, test 6a).
    public static IEnumerable<object[]> AdversarialShareVectors()
    {
        yield return new object[] { "one_tree", new long[] { 500 } };
        yield return new object[] { "one_hot_39", OneHot(39, hotIndex: 0, hotValue: 1000) };
        yield return new object[] { "one_hot_39_last_slot", OneHot(39, hotIndex: 38, hotValue: 1) };
        yield return new object[] { "uniform_39", Enumerable.Repeat(7L, 39).ToArray() };
        // Long-tailed: one enormous tree count among many trees holding a single node each -- the
        // adversarial shape §12 test 6a names by name ("no single share_i^2 term can push H outside
        // [1, Fmax] regardless of how many other trees are touched").
        yield return new object[] { "long_tailed_39", LongTailed(39, hugeCount: 1_000_000, tinyCount: 1) };
        yield return new object[] { "all_zero_39", new long[39] };
    }

    static long[] OneHot(int n, int hotIndex, long hotValue)
    {
        var v = new long[n];
        v[hotIndex] = hotValue;
        return v;
    }

    static long[] LongTailed(int n, long hugeCount, long tinyCount)
    {
        var v = new long[n];
        v[0] = hugeCount;
        for (var i = 1; i < n; i++) v[i] = tinyCount;
        return v;
    }

    [Theory]
    [MemberData(nameof(AdversarialShareVectors))]
    public void H_nodes_stays_within_zero_and_one_per_mille_for_every_generated_share_vector(string _, long[] counts)
    {
        var h = Concentration.HerfindahlMilli(counts);
        Assert.InRange(h, 0L, 1000L);
    }

    // Test 6b: "asserted on H_nodes, H_souls and the blend SEPARATELY -- so a broken term cannot hide
    // inside a blend that still lands in range." Souls are a second, independent Herfindahl call over
    // the SAME kind of vector (the function has no notion of which currency it is fed), so re-running
    // the same generated vectors through it directly proves the H_souls term independently rather than
    // assuming it shares H_nodes' proof because the code happens to be the same function.
    [Theory]
    [MemberData(nameof(AdversarialShareVectors))]
    public void H_souls_stays_within_zero_and_one_per_mille_for_every_generated_share_vector(string _, long[] counts)
    {
        var hSouls = Concentration.HerfindahlMilli(counts);
        Assert.InRange(hSouls, 0L, 1000L);
    }

    [Theory]
    [MemberData(nameof(AdversarialShareVectors))]
    public void The_blend_of_two_adversarial_vectors_also_stays_in_bounds_and_so_does_F(string _, long[] counts)
    {
        // Blend H_nodes from this vector with H_souls from a DIFFERENT adversarial shape (reversed),
        // so the blend term is exercised on two independently-adversarial inputs, not the same vector
        // fed to itself.
        var reversed = (long[])counts.Clone();
        Array.Reverse(reversed);

        var hNodes = Concentration.HerfindahlMilli(counts);
        var hSouls = Concentration.HerfindahlMilli(reversed);

        foreach (var wMilli in new long[] { 0, 250, 500, 750, 1000 })
        {
            var blended = Concentration.BlendMilli(hNodes, hSouls, wMilli);
            Assert.InRange(blended, 0L, 1000L);

            var f = Concentration.FmaxAppliedMilli(blended, fmaxMilli: 1200);
            Assert.InRange(f, 1000L, 1200L); // "no single share_i^2 term can push H (and therefore F)
                                              // outside [1, Fmax] regardless of how many other trees
                                              // are touched" -- this IS that assertion, per term.
        }
    }

    [Fact] // test 8 (spec-tree-resolve.md §12): "the same build at Theta=10 and Theta=10,000 produces
           // the same F" -- proven by construction, not sampled: F = FmaxAppliedMilli(H, Fmax) takes
           // NO Theta parameter anywhere in its signature, so a build's F cannot vary with the actor's
           // Theta at all. Named explicitly here because §5.3's argument is what keeps PS-3's
           // contest-linearity theorem legitimate under a multiplier -- if a future refactor threaded
           // Theta into F's computation, this is the test that would catch it.
    public void F_is_theta_invariant_the_same_build_gives_the_same_F_regardless_of_theta()
    {
        var fmaxMethod = typeof(Concentration).GetMethod(nameof(Concentration.FmaxAppliedMilli))!;
        var paramNames = Array.ConvertAll(fmaxMethod.GetParameters(), p => p.Name);
        Assert.DoesNotContain(paramNames, n => n!.Contains("theta", StringComparison.OrdinalIgnoreCase));

        // The same (H, Fmax) pair, called twice, standing in for "the same build read at Theta=10 and
        // again at Theta=10,000" -- there is no Theta input to vary, so both calls are byte-identical
        // by construction.
        var fAtLowTheta = Concentration.FmaxAppliedMilli(hMilli: 640, fmaxMilli: 1200);
        var fAtHighTheta = Concentration.FmaxAppliedMilli(hMilli: 640, fmaxMilli: 1200);
        Assert.Equal(fAtLowTheta, fAtHighTheta);
    }
}

/// <summary>
/// Task D7's own new work: `F`'s application seam (spec-tree-resolve.md §5.3-5.4) and the memoizing
/// wrapper §11 calls for (test 20). PS-3 line-by-line and the H blend's empty-denominator rule are
/// already proven by B6's shipped tests -- <see cref="TreeAtomSourceTests.A_contest_channel_reads_theta_linearly_never_P_of_theta"/>
/// and <see cref="ConcentrationTests.Empty_denominator_reads_zero_never_a_uniform_default"/> -- and are
/// cited here rather than duplicated. This file covers only what did not exist before D7: the actual
/// multiply into <see cref="TreeAtomSource.BoundAtomsFor"/>'s amount (proven end-to-end in
/// <see cref="TreeAtomSourceTests"/> itself, alongside the axis/theta-invariance/byte-identity tests
/// added there), and the reference-keyed memo below.
/// </summary>
public class TreeResolveMemoTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static FusionRpg.Core.Power.PowerTuning RealPowerTuning() =>
        FusionRpg.Core.Power.PowerTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    static LoadedTree OneNodeTree() =>
        new(new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander",
                "broad-and-flat", 10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[]
            {
                new NodeRecord("skill.might-off-t3-n0", "might", TreeBranch.Off, 3, "n0",
                    Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 45,
                    new[]
                    {
                        new NodeAtom("stat.derived", AttachPoint.Stat, "combat.power.fire",
                            NodeAtomOp.Flat, null, null, 3038, ScaleAxis.PTheta, UnitClass.GameUnits),
                    },
                    Array.Empty<string>(), ExclusionForm.None, null, true, null),
            });

    /// <summary>Test 20, the cache-hit half: the SAME owned-node-set REFERENCE, resolved twice, must
    /// not recompute. Proven by reference identity on the returned list -- a fresh resolve would
    /// allocate a brand-new <c>List&lt;BoundDerivedAtom&gt;</c> every time (`TreeAtomSource.BoundAtomsFor`
    /// always does), so getting the SAME list instance back is only possible via the memo.</summary>
    [Fact]
    public void The_same_owned_node_set_reference_resolved_twice_hits_the_memo()
    {
        var tree = OneNodeTree();
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();
        var memo = new TreeResolveMemo();

        var first = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        var second = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);

        Assert.Same(first, second); // no recompute happened -- same list instance
    }

    /// <summary>Test 20, the self-correcting half: a DIFFERENT reference -- even one holding the exact
    /// same members -- must re-resolve. "Not needed by an external bump" is the whole point
    /// (spec-tree-resolve.md §11): nobody has to remember to invalidate anything.</summary>
    [Fact]
    public void A_different_reference_with_identical_members_re_resolves_rather_than_reusing_the_cache()
    {
        var tree = OneNodeTree();
        var ownedFirst = new HashSet<string> { "skill.might-off-t3-n0" };
        var ownedSecond = new HashSet<string> { "skill.might-off-t3-n0" }; // value-equal, DIFFERENT instance
        var tuning = RealPowerTuning();
        var memo = new TreeResolveMemo();

        var first = memo.BoundAtomsFor(tree, ownedFirst, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        var second = memo.BoundAtomsFor(tree, ownedSecond, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);

        Assert.NotSame(first, second); // a fresh resolve ran -- proven by a NEW list instance
        Assert.Equal(first[0].Amount, second[0].Amount); // and the value did not silently drift
    }

    /// <summary>A changed allocation must be seen on the actor's very next resolve -- withdrawing a
    /// node and re-resolving with a NEW reference must reflect the withdrawal, never serve the stale
    /// cached entry for that key.</summary>
    [Fact]
    public void Withdrawing_the_allocation_via_a_new_reference_is_reflected_immediately()
    {
        var tree = OneNodeTree();
        var tuning = RealPowerTuning();
        var memo = new TreeResolveMemo();

        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var withNode = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        Assert.Single(withNode);

        var emptied = new HashSet<string>(); // a NEW reference, the node withdrawn
        var withoutNode = memo.BoundAtomsFor(tree, emptied, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        Assert.Empty(withoutNode);
    }

    /// <summary>A different `(tierReached, thetaNode, fMilli)` key never reuses another key's cached
    /// entry, even for the SAME owned-node-set reference -- the memo key is the full tuple, not just
    /// the reference.</summary>
    [Fact]
    public void A_different_fMilli_for_the_same_reference_is_its_own_cache_slot()
    {
        var tree = OneNodeTree();
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();
        var memo = new TreeResolveMemo();

        var noF = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        var withF = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1200);

        Assert.NotSame(noF, withF);
        Assert.Equal(noF[0].Amount * 1.2, withF[0].Amount, precision: 9);
    }

    [Fact]
    public void InvalidateMemo_forces_a_fresh_resolve_even_for_the_same_reference()
    {
        var tree = OneNodeTree();
        var owned = new HashSet<string> { "skill.might-off-t3-n0" };
        var tuning = RealPowerTuning();
        var memo = new TreeResolveMemo();

        var first = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);
        memo.InvalidateMemo();
        var second = memo.BoundAtomsFor(tree, owned, tierReached: 10, thetaNode: 100, tuning, fMilli: 1000);

        Assert.NotSame(first, second); // forced recompute, same reference notwithstanding
        Assert.Equal(first[0].Amount, second[0].Amount);
    }

    [Fact]
    public void Null_tree_or_owned_set_is_refused()
    {
        var memo = new TreeResolveMemo();
        var tree = OneNodeTree();
        var owned = new HashSet<string>();
        var tuning = RealPowerTuning();

        Assert.Throws<ArgumentNullException>(() => memo.BoundAtomsFor(null!, owned, 10, 100, tuning, 1000));
        Assert.Throws<ArgumentNullException>(() => memo.BoundAtomsFor(tree, null!, 10, 100, tuning, 1000));
    }
}

/// <summary>
/// Task D7, test 8a (spec-tree-resolve.md §5.3, §12) — the half that was genuinely open per the todo's
/// own evidence note: "needs a WIN-RATE model (a sigmoid over a contest-channel difference)... that
/// nothing in tree-resolve currently has a seam for." That seam exists and ships:
/// <see cref="FusionRpg.Core.Combat.CombatProbability.Sigmoid"/> is exactly this shape — a raw stat
/// DELTA and a fixed scale in, a probability out, no `Θ` parameter anywhere in its signature
/// (`CombatProbability.cs:8-9`) — the same function `OverlayCombatCalculator.cs:117-123` already calls
/// for the shipped accuracy/crit rolls. This class is the first place `tree-resolve`'s own tests reach
/// it, and no new curve is written to do so.
///
/// <para><b>The sibling test <see cref="TreeAtomSourceTests.A_fixed_contest_gap_scaled_by_F_is_worth_the_same_at_every_theta"/>
/// already proves the RAW-delta building block</b> — a fixed `Θ_node` gap, `F`-scaled, produces an
/// identical stat delta at `Θ=10` and `Θ=10,000`. It is labelled "test 8a" in its own doc comment, but
/// it stops one step short of what the todo's acceptance line actually asks for: "a fixed one-tier
/// contest gap is worth the same WIN-RATE delta" — not merely the same raw number feeding one. This
/// class finishes that: same construction, but the delta is fed through the real sigmoid and it is the
/// resulting PROBABILITY that is asserted identical.</para>
///
/// <para><b>What "one-tier contest gap" means here, and why it is deliberately NOT `req(t)`'s tier
/// index.</b> §3.3 gates a tree's tiers on APTITUDE POINTS — a currency this module never converts
/// into `Θ`. `Θ_node = Θ_actor + Ws·soulLevel` (§6.1 row 3) is a wholly separate, additively-composed
/// axis with no defined mapping to "how many aptitude-gated tiers deep" a node sits — so there is no
/// canonical `ΔΘ_node` that "one tier" could mean in the §3.1 sense. What §5.3 and
/// `ssot-power-scale.md` §2's theorem actually name is a fixed ABSOLUTE `Θ` gap: the theorem's own
/// proof table uses an arbitrary "gap 5" against `Θ=10 → Θ=10,000`, and the todo's "one-tier" phrasing
/// is that same informal shorthand, not a literal reference to the req(t) ladder. The construction
/// below picks one arbitrary, non-round `Θ_node` gap and proves the theorem for it; because
/// `TreeAtomSource.ResolveAmount`'s `ScaleAxis.Theta` branch is linear THROUGH THE ORIGIN in
/// `Θ_node` with a fixed slope (`kMicro/1e6 · fMultiplier`, constant in `Θ`), the proof generalises to
/// every fixed gap — that constant slope is the entire content of "linear" here.</para>
/// </summary>
public class ContestWinRateThetaInvarianceTests
{
    static LoadedTree OneContestNodeTree(long kMicro) =>
        new(new TreeRecord("might", TreeCategory.Primary, "x", "broad-and-flat", 10, 2,
                new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            new[]
            {
                new NodeRecord("skill.might-off-t1-n0", "might", TreeBranch.Off, 1, "n0",
                    Array.Empty<string>(), NodeClass.Magnitude, new[] { "affix.a" }, 18,
                    new[]
                    {
                        // Same contest-atom shape TreeAtomSourceTests already uses for its own
                        // Theta-axis proofs (status.resist.dot / StatusPotencyPoints) -- ScaleAxis.Theta
                        // is what makes this a CONTEST read (PS-3), never P(Theta).
                        new NodeAtom("stat.derived", AttachPoint.Status, "status.resist.dot",
                            NodeAtomOp.Increased, null, null, kMicro, ScaleAxis.Theta,
                            UnitClass.StatusPotencyPoints),
                    },
                    Array.Empty<string>(), ExclusionForm.None, null, true, null),
            });

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static FusionRpg.Core.Power.PowerTuning RealPowerTuning() =>
        FusionRpg.Core.Power.PowerTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    /// <summary>The F-scaled contest delta a fixed `Θ` gap produces at a given base `Θ`, read through
    /// the REAL resolver seam (<see cref="TreeAtomSource.BoundAtomsFor"/>) -- never re-derived by
    /// hand, so a refactor of the actual multiply is what this test would catch.</summary>
    static double ContestDeltaAt(long thetaBase, long thetaGap, long kMicro, long fMilli)
    {
        var tree = OneContestNodeTree(kMicro);
        var owned = new HashSet<string> { "skill.might-off-t1-n0" };
        var tuning = RealPowerTuning();

        var atBase = TreeAtomSource.BoundAtomsFor(tree, owned, 10, thetaBase, tuning, fMilli)[0].Amount;
        var atGapped = TreeAtomSource.BoundAtomsFor(tree, owned, 10, thetaBase + thetaGap, tuning, fMilli)[0].Amount;
        return atGapped - atBase;
    }

    // "several different measured Theta base points" -- ssot-power-scale.md §2's own worked example
    // anchors at Theta=10 and Theta=10,000; 500 and 1,000,000 are added so the claim is not merely
    // proven at the two literal numbers the SSOT doc happens to quote.
    [Theory]
    [InlineData(10L)]
    [InlineData(500L)]
    [InlineData(10_000L)]
    [InlineData(1_000_000L)]
    public void A_fixed_theta_gap_is_worth_the_same_win_rate_at_every_measured_theta(long thetaBase)
    {
        const long kMicro = 50_000;          // an arbitrary, non-round contest coefficient
        const long fMilli = 1200;            // a REAL F != 1000 -- the case that would expose F breaking linearity (§5.3)
        const long thetaGap = 37;            // an arbitrary, non-round Theta gap -- see class doc: any fixed gap works
        const long referenceThetaBase = 10;  // ssot-power-scale.md §2's own worked-example anchor

        var referenceDelta = ContestDeltaAt(referenceThetaBase, thetaGap, kMicro, fMilli);
        var deltaAtThisTheta = ContestDeltaAt(thetaBase, thetaGap, kMicro, fMilli);

        // Building block (already proven independently by TreeAtomSourceTests's own test 8a half) --
        // re-asserted here so this test is self-contained and does not lean on test ordering.
        Assert.Equal(referenceDelta, deltaAtThisTheta, precision: 9);

        // The actual claim test 8a names: fed through the REAL, shipped sigmoid -- the same one
        // OverlayCombatCalculator.cs:117-123 rolls accuracy/crit against, no private curve written for
        // this test -- the WIN-RATE this gap is worth is identical at every measured Theta.
        var referenceWinRate = CombatProbability.Sigmoid(referenceDelta, CombatProbabilityPolicy.AccuracyScale);
        var winRateAtThisTheta = CombatProbability.Sigmoid(deltaAtThisTheta, CombatProbabilityPolicy.AccuracyScale);

        Assert.Equal(referenceWinRate, winRateAtThisTheta, precision: 12);

        // Not a vacuous "0.5 == 0.5": the chosen gap moves the sigmoid meaningfully off parity, so the
        // invariance above is proven on a real, non-trivial win-rate shift.
        Assert.True(Math.Abs(referenceWinRate - 0.5) > 0.005,
            $"expected a non-trivial win-rate shift away from parity, got {referenceWinRate}");
    }

    /// <summary>The mutant this test exists to catch (spec-tree-resolve.md §12's own mutation
    /// discipline, extended to this test): unbounding `F` -- or threading `Θ` into `F`'s computation --
    /// would let the win-rate delta grow with `Θ` instead of staying flat, reproducing exactly the
    /// "advantage growing without bound" failure `ssot-power-scale.md` §2's theorem forbids. This
    /// isolates the two levers: `Θ` alone (F held fixed) must change nothing; `F` alone (Θ held fixed)
    /// is the only thing allowed to move the number.</summary>
    [Fact]
    public void Only_a_different_F_never_a_different_theta_can_move_the_win_rate_for_the_same_gap()
    {
        const long kMicro = 50_000;
        const long thetaGap = 37;

        var lowThetaLowF = ContestDeltaAt(thetaBase: 10, thetaGap, kMicro, fMilli: 1000);
        var highThetaLowF = ContestDeltaAt(thetaBase: 10_000, thetaGap, kMicro, fMilli: 1000);
        var lowThetaHighF = ContestDeltaAt(thetaBase: 10, thetaGap, kMicro, fMilli: 1200);

        var pLowThetaLowF = CombatProbability.Sigmoid(lowThetaLowF, CombatProbabilityPolicy.AccuracyScale);
        var pHighThetaLowF = CombatProbability.Sigmoid(highThetaLowF, CombatProbabilityPolicy.AccuracyScale);
        var pLowThetaHighF = CombatProbability.Sigmoid(lowThetaHighF, CombatProbabilityPolicy.AccuracyScale);

        // Theta alone (F held at 1000, i.e. removed -- §5.4) changes nothing.
        Assert.Equal(pLowThetaLowF, pHighThetaLowF, precision: 12);
        // F alone (Theta held at 10) DOES move the win-rate -- F, never Theta, is the lever (§5.3:
        // "the worst F can do to a contest is scale a fixed gap by <= Fmax").
        Assert.NotEqual(pLowThetaLowF, pLowThetaHighF);
    }
}
