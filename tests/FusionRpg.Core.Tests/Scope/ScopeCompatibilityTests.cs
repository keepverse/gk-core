using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

/// <summary>
/// T3 (buff-debuff-scope-todo.md Phase 1) — the compatibility table's own acceptance line, made
/// executable: the G8 case must resolve differently for Live vs. Sim, and an unlisted quadruple must
/// reject naming all four components, never silently default.
/// </summary>
public class ScopeCompatibilityTests
{
    [Fact]
    public void The_G8_case_resolves_to_the_side_wide_constant_shape_on_the_live_host()
    {
        var support = ScopeCompatibility.Resolve(
            "stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live, channel: "defense");

        Assert.Equal(ScopeSupportLevel.Full, support.Level);
        Assert.Equal(ScopeDeliveryShape.SideWideConstant, support.Shape);
    }

    [Fact]
    public void The_identical_G8_kind_resolves_to_the_per_entity_grant_shape_on_the_sim_host()
    {
        var support = ScopeCompatibility.Resolve(
            "stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Sim, channel: "defense");

        Assert.Equal(ScopeSupportLevel.Full, support.Level);
        Assert.Equal(ScopeDeliveryShape.PerEntityGrant, support.Shape);
    }

    [Fact]
    public void The_same_kind_two_hosts_disagree_proving_Battlefield_is_not_one_undifferentiated_case()
    {
        var live = ScopeCompatibility.Resolve("stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live, "defense");
        var sim = ScopeCompatibility.Resolve("stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Sim, "defense");

        Assert.NotEqual(live.Shape, sim.Shape);
    }

    [Fact]
    public void An_unlisted_quadruple_rejects_ScopeUnsupported_naming_all_four_components()
    {
        var ex = Assert.Throws<ScopeUnsupportedException>(() =>
            ScopeCompatibility.Resolve("resource.delta", WhereScope.WorldMap, WhoKind.Type, host: null));

        Assert.Equal("resource.delta", ex.AtomKindId);
        Assert.Equal(WhereScope.WorldMap, ex.Where);
        Assert.Equal(WhoKind.Type, ex.Who);
        Assert.Null(ex.Host);
        Assert.Contains("resource.delta", ex.Message);
        Assert.Contains("worldMap", ex.Message);
        Assert.Contains("type", ex.Message);
    }

    [Fact]
    public void TryResolve_returns_false_for_an_unlisted_combination_instead_of_throwing()
    {
        var found = ScopeCompatibility.TryResolve(
            "stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live, channel: "attack", out _);

        Assert.False(found);
    }
}
