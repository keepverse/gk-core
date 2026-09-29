using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// lawn-combat-wire T4 (<c>combat-numerics</c>): the overlay damage path was <c>double</c> throughout,
/// divided by 1000 before multiplying, exited on an unchecked long-narrowing cast, and silently
/// saturated at the Unity boundary. This file proves the fixed shape:
/// <list type="bullet">
/// <item>overflow throws rather than wrapping (<see cref="Compute_throws_on_a_magnitude_past_long_range"/>);</item>
/// <item>the parry/block neutral-share arithmetic multiplies before it divides, and that ordering is
/// not cosmetic (<see cref="DivideFirst_and_divideLast_can_round_to_different_longs"/>,
/// <see cref="TheShippedCode_multipliesBeforeItDivides_forTheNeutralShare"/>);</item>
/// <item>D1 (folded into this task's acceptance): <c>ActorHub.MergeAppliedCombat</c> still folds ONLY
/// <c>progression.bonus.*</c>, never a <c>combat.*</c> channel
/// (<see cref="MergeAppliedCombat_ignores_a_combat_channel"/>);</item>
/// </list>
///
/// <para><b>No floating-point ban (owner ruling 2026-09-15, lawn-combat-wire L-N12).</b> This file used
/// to carry source-scan tests forbidding <c>double</c>/<c>float</c> outside an allowlist; they were removed
/// because floating-point is allowed inside the calculation. What stays guarded is the magnitude contract:
/// the Funnel-bound result is a checked <c>long</c> and integer per-mille math divides last.</para>
/// </summary>
public class OverlayCombatNumericsTests
{
    // ── Overflow throws, never wraps ─────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_throws_on_a_magnitude_past_long_range()
    {
        var calc = new OverlayCombatCalculator();
        // Omni fallback (no element components) with an attacker power so large that powerAdjusted /
        // finalDamage lands far past long.MaxValue (~9.22e18) — ForceHit/ForceCrit make the outcome
        // fully deterministic, no RNG draw needed to reach the mitigation math.
        var hugeAttacker = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 1e30),
        });
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = System.Array.Empty<ElementPayloadComponent>(),
            Attacker = new CombatActorSnapshot(hugeAttacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            ForceHit = true,
            ForceCrit = false
        };

        Assert.Throws<System.OverflowException>(() => calc.Compute(request, new SeededCombatRng(1)));
    }

    // ── Divide-last, not divide-first ────────────────────────────────────────────────────────────

    [Fact]
    public void DivideFirst_and_divideLast_can_round_to_different_longs()
    {
        // Demonstrates the defect the task named at (the former) OverlayCombatCalculator.cs:252 is
        // real, not cosmetic: for at least one per-mille share, "divide then multiply" and "multiply
        // then divide" round to a DIFFERENT long, even though they are the same expression in exact
        // (real-number) arithmetic. Searched rather than hand-picked, so this does not depend on
        // guessing a specific IEEE rounding boundary.
        const double effectiveBaseDamage = 100.0;
        long? differingShare = null;
        for (long share = 1; share < 1000; share++)
        {
            var divideFirst = (long)System.Math.Round(effectiveBaseDamage * (share / 1000.0), System.MidpointRounding.AwayFromZero);
            var multiplyFirst = (long)System.Math.Round(effectiveBaseDamage * share / 1000.0, System.MidpointRounding.AwayFromZero);
            if (divideFirst != multiplyFirst)
            {
                differingShare = share;
                break;
            }
        }

        Assert.True(differingShare.HasValue,
            "expected at least one per-mille share in [1,999) where divide-first and multiply-first " +
            "round to different longs at baseDamage=100 -- if this ever fails, the two orderings have " +
            "become equivalent for every share and the historical defect this test guards is gone, " +
            "which would need a different base value to keep demonstrating the property, not a weaker assertion");
    }

    [Fact]
    public void TheShippedCode_multipliesBeforeItDivides_forTheNeutralShare()
    {
        // Structural half of the divide-last proof: the SHIPPED source text uses the corrected
        // multiply-then-divide order for the parry/block neutral-share computation, not the
        // divide-then-multiply shape the task named as the defect. Same technique
        // MitigationChainTests.LongThroughout already uses for this exact file.
        var text = ReadCoreFile("Combat", "OverlayCombatCalculator.cs");
        Assert.DoesNotContain("effectiveBaseDamage * (CombatPolicy.Default.ParryNeutralShareKPm / 1000.0)", text, System.StringComparison.Ordinal);
        Assert.Contains("effectiveBaseDamage * CombatPolicy.Default.ParryNeutralShareKPm / 1000.0", text, System.StringComparison.Ordinal);
    }

    // ── D1: MergeAppliedCombat folds progression.bonus.* only, never combat.* ───────────────────────

    [Fact]
    public void MergeAppliedCombat_ignores_a_combat_channel()
    {
        // D1 (lawn-combat-wire): a combat.* channel must never reach AppliedCombat -- that would
        // double-dip RPG power into a single hit, once through OverlayCombatCalculator's own read and
        // once through the Unity-field bridge EntityStatWriter consumes. Reinforces (does not replace)
        // AptitudeMatrixTests.Progression_bonus_is_the_only_edge_family_that_can_reach_a_pvz_unity_field
        // with a minimal, direct repro that does not depend on the shipped aptitude corpus.
        // Fully qualified: this test's own namespace (FusionRpg.Core.Tests.Combat) sits under
        // FusionRpg.Core.Tests, which also contains a sibling namespace literally named "ActorHub"
        // (tests/FusionRpg.Core.Tests/ActorHub/**) -- that enclosing-namespace lookup wins over the
        // `using FusionRpg.Core.Stats.Derived;` class import, so the bare name resolves to the
        // namespace, not the class ("'ActorHub' is a namespace but is used like a type").
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new CombatChannelOnlySubsystem());

        var result = hub.Resolve(new StatContext { Side = StatSide.Plant, TypeId = 1, EntityKey = "0xTEST-D1" });

        Assert.Equal(result.RuntimePrimary.Atk, result.AppliedCombat.Atk);
        Assert.Equal(result.RuntimePrimary.MaxHp, result.AppliedCombat.MaxHp);
        Assert.Equal(result.RuntimePrimary.Hp, result.AppliedCombat.Hp);
        Assert.Equal(result.RuntimePrimary.DefenseFlat, result.AppliedCombat.DefenseFlat);
        Assert.Same(result.RuntimePrimary, result.AppliedCombat); // no bridge channel present -> same instance
    }

    sealed class CombatChannelOnlySubsystem : IActorStatSubsystem
    {
        public string SubsystemId => "test.combatChannelOnly";
        public int Order => 999;
        public void ContributeDerived(StatContext ctx, System.Collections.Generic.ICollection<DerivedModifier> mods) =>
            mods.Add(new DerivedModifier(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 999_999, SourceId: "test"));
    }

    static string ReadCoreFile(params string[] relativeUnderCore)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(new[] { dir.FullName, "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("could not find " + string.Join("/", relativeUnderCore));
    }
}
