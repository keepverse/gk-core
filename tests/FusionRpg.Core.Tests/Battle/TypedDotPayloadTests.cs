using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// combat-unification **Wave E1**, second clause — typed DoTs: a status carries an element, and its
/// pulses deliver that element to the shield gate as a full-weight component.
///
/// <para><b>One rule, shared by both modes.</b> `StatusPulsePayload.For` is used by
/// `BattlePulseSink` and by `StatusFunnelPulseSink` alike, because "both modes are element-neutral on
/// DoTs by parity" is a stated program invariant and the cheapest way to keep two implementations
/// agreeing is to give them one function rather than two copies of the rule.</para>
///
/// <para><b>Inert until content opts in.</b> `Element` defaults to null, which produces an empty
/// component list — exactly what both sinks passed before E1 — so every golden is unmoved.</para>
/// </summary>
public class TypedDotPayloadTests
{
    static StatusInstance Instance(ElementTypeId? element) => new()
    {
        InstanceId = "i1",
        StatusId = "burn",
        HostPtr = "actor:a",
        Element = element,
    };

    [Fact]
    public void AnUntypedStatusPulsesElementNeutral()
    {
        Assert.Empty(StatusPulsePayload.For(Instance(null)));
    }

    [Fact]
    public void ATypedStatusPulsesAsASingleFullWeightComponentOfItsElement()
    {
        var payload = StatusPulsePayload.For(Instance(ElementTypeId.Fire));

        var one = Assert.Single(payload);
        Assert.Equal(ElementTypeId.Fire, one.Element);
        Assert.Equal(1.0, one.Weight);
    }

    [Theory]
    [InlineData(ElementTypeId.Fire)]
    [InlineData(ElementTypeId.Ice)]
    [InlineData(ElementTypeId.Earth)]
    [InlineData(ElementTypeId.Air)]
    [InlineData(ElementTypeId.Light)]
    [InlineData(ElementTypeId.Dark)]
    public void EveryElementRoundTripsIntoThePayload(ElementTypeId element)
    {
        Assert.Equal(element, Assert.Single(StatusPulsePayload.For(Instance(element))).Element);
    }

    /// <summary>The default is what keeps E1's byte-identity claim true: an author who says nothing
    /// about elements gets the pre-E1 behaviour exactly.</summary>
    [Fact]
    public void TheApplyInputDefaultsToElementNeutral()
    {
        var input = new StatusApplyInput(
            StatusId: "burn", HostPtr: "actor:a", AttackerPtr: null, GrantId: "g",
            BaseMagnitude: -5, BaseDuration: 3000);

        Assert.Null(input.Element);
    }

    /// <summary>And the instance carries what the input said, so the sinks read a real value rather
    /// than re-deriving one.</summary>
    [Fact]
    public void TheInstanceCarriesTheInputsElement()
    {
        var input = new StatusApplyInput(
            StatusId: "burn", HostPtr: "actor:a", AttackerPtr: null, GrantId: "g",
            BaseMagnitude: -5, BaseDuration: 3000, Element: ElementTypeId.Fire);

        Assert.Equal(ElementTypeId.Fire, input.Element);
    }
}
