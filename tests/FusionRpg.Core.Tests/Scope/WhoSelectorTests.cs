using FusionRpg.Contracts;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

public class WhoSelectorTests
{
    [Theory]
    [InlineData(WhoKind.Target)]
    [InlineData(WhoKind.Type)]
    [InlineData(WhoKind.UniqueCreature)]
    [InlineData(WhoKind.Relation)]
    public void Every_WhoKind_value_round_trips_through_Name_and_TryParse(WhoKind kind)
    {
        var name = WhoKinds.Name(kind);
        Assert.NotEqual("", name);
        Assert.True(WhoKinds.TryParse(name, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void An_unknown_WhoKind_string_rejects_rather_than_defaulting()
    {
        Assert.False(WhoKinds.TryParse("everyone", out _));
        Assert.False(WhoKinds.TryParse(null, out _));
    }

    [Fact]
    public void A_target_selector_carries_only_its_own_payload()
    {
        var sel = new WhoSelector { Kind = WhoKind.Target, TargetPtr = "ABC123" };
        Assert.Equal(WhoKind.Target, sel.Kind);
        Assert.Equal("ABC123", sel.TargetPtr);
        Assert.Null(sel.TypeIds);
        Assert.Null(sel.InstanceId);
        Assert.Null(sel.Relation);
    }

    [Fact]
    public void A_relation_selector_references_the_shared_Contracts_type_directly()
    {
        // The whole point of the T1 extraction: WhoSelector depends on FusionRpg.Contracts.RelationKind,
        // not FusionRpg.Core.Actions.ActionRelation — this compiling at all is part of the proof.
        var sel = new WhoSelector { Kind = WhoKind.Relation, Relation = RelationKind.Ally };
        Assert.Equal(RelationKind.Ally, sel.Relation);
    }
}
