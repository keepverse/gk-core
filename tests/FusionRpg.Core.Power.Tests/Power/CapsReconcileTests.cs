using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// caps-reconcile (T3.5, spec-caps-reconcile.md §2.1-§2.2): the two magnitude bounds that live in
/// Core (<see cref="ResourceDeltaMath"/>; <c>ShieldMath</c> has its own dedicated file,
/// <c>Combat/Shield/ShieldMathTests.cs</c>, extended by this task rather than duplicated here), the
/// derived-bound dependency graph (F13), and the §11.2a narrowing-cast regression guard. The dynamic
/// soul bound and the one-policy-across-both-paths proof live in <c>FusionRpg.Data.Tests</c>
/// (<c>SoulStoreTests.cs</c>, <c>ExpeditionRewardApplyTests.cs</c>) — <see cref="RpgStore"/> is not
/// reachable from Core.Tests, so this file's own §4 file list could not literally hold every case.
/// </summary>
public class CapsReconcileTests
{
    // ---- ResourceDeltaMath.AmountCap -----------------------------------------------------------

    [Fact]
    public void AmountCap_is_derived_from_Apply_own_arithmetic_not_a_literal()
    {
        // T3.5: live + delta, each independently bounded by AmountCap -- worst case 2*AmountCap must
        // stay under long.MaxValue. long.MaxValue/2 is exact; independently recomputed here.
        Assert.Equal(long.MaxValue / 2, ResourceDeltaMath.AmountCap);
        Assert.True(checked(2 * ResourceDeltaMath.AmountCap) < long.MaxValue);
    }

    [Fact]
    public void Apply_one_under_AmountCap_throws_nothing()
    {
        var result = ResourceDeltaMath.Apply(ResourceDeltaMath.AmountCap - 1, 0, ResourceDeltaMath.AmountCap);
        Assert.Equal(ResourceDeltaMath.AmountCap - 1, result);
    }

    [Fact]
    public void Apply_over_AmountCap_throws_naming_the_site_and_the_value()
    {
        // Never clamps: this used to be reachable only via a caller pre-check; Apply itself now
        // refuses rather than silently computing past the bound ExceedsAmountCap exists to name.
        var over = ResourceDeltaMath.AmountCap + 1;
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ResourceDeltaMath.Apply(0, over, 1000));
        Assert.Equal("delta", ex.ParamName);
    }

    [Fact]
    public void ExceedsAmountCap_still_lets_EffectFunnel_skip_before_Apply_ever_throws()
    {
        // The guarded Funnel path (EffectFunnel.cs) pre-checks with ExceedsAmountCap and skips the
        // mutation entirely -- unchanged by this task. This test proves the predicate Funnel relies on
        // agrees exactly with the new throwing boundary inside Apply, so a caller that DOES pre-check
        // never observes the new exception.
        Assert.False(ResourceDeltaMath.ExceedsAmountCap(ResourceDeltaMath.AmountCap));
        Assert.True(ResourceDeltaMath.ExceedsAmountCap(ResourceDeltaMath.AmountCap + 1));
    }

    // ---- F13: derived-bound dependency graph is acyclic --------------------------------------------

    [Fact]
    public void DerivedBoundDependencyGraph_IsAcyclic()
    {
        // F13: "a derived bound may read other caps." Declared here as (bound -> other DERIVED bounds
        // it reads) -- edges to a leaf tuning value (e.g. ShieldMath.MaxInput reading
        // ShieldPolicy.PenCapKPm, itself an exempt §11.6 leaf, not a derived bound) can never be part
        // of a cycle, so only bound-to-bound edges matter here. Today's graph has zero such edges --
        // this test exists to catch the NEXT edge a future change adds, not because one exists now.
        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ShieldMath.MaxInput"] = Array.Empty<string>(),         // reads ShieldPolicy leaves only
            ["ResourceDeltaMath.AmountCap"] = Array.Empty<string>(), // self-contained, long.MaxValue/2
            ["RpgStore.MaxSoulAwardFrom"] = Array.Empty<string>(),   // reads a runtime balance, not a cap
        };

        foreach (var (node, reads) in edges)
            foreach (var dep in reads)
                Assert.True(edges.ContainsKey(dep), $"{node} declares a read of undeclared bound '{dep}'");

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edges.Keys)
            Assert.False(HasCycle(node), $"cycle detected starting at {node}");

        bool HasCycle(string node)
        {
            if (visited.Contains(node)) return false;
            if (!visiting.Add(node)) return true;
            foreach (var dep in edges[node])
                if (HasCycle(dep)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
    }

    // ---- §11.2a: the narrowing-cast regression guard ------------------------------------------------

    [Fact]
    public void EffectEventDto_Damage_stays_wide_never_narrows_back_to_int()
    {
        // spec-caps-reconcile.md §2.2, correction: this is a REGRESSION GUARD, not a forcing function
        // -- P0.4 (Phase 0) already widened Damage before this module's wave, so this is green from
        // birth. It fails if someone later narrows the field back to int (EffectBag.cs:707 and
        // EventDrain.cs:458/475 all assign into this same field without a cast today).
        var prop = typeof(EffectEventDto).GetProperty(nameof(EffectEventDto.Damage));
        Assert.NotNull(prop);
        var underlying = Nullable.GetUnderlyingType(prop!.PropertyType) ?? prop.PropertyType;
        Assert.Equal(typeof(long), underlying);
    }
}
