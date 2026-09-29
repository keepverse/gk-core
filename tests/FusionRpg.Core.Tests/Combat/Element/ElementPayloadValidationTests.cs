using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

public class ElementPayloadValidationTests
{
    [Fact]
    public void Rejects_empty_payload() =>
        Assert.Throws<ArgumentException>(() => ElementPayload.Validate(Array.Empty<ElementPayloadComponent>()));

    [Fact]
    public void Rejects_zero_weight() =>
        Assert.Throws<ArgumentException>(() => ElementPayload.Validate(new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0)
        }));

    [Fact]
    public void Rejects_negative_weight() =>
        Assert.Throws<ArgumentException>(() => ElementPayload.Validate(new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, -0.5)
        }));

    [Fact]
    public void Rejects_sum_not_one() =>
        Assert.Throws<ArgumentException>(() => ElementPayload.Validate(new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.6),
            new ElementPayloadComponent(ElementTypeId.Air, 0.3)
        }));

    [Fact]
    public void Accepts_valid_payload()
    {
        var payload = ElementPayload.From(new[]
        {
            new ElementPayloadComponent(ElementTypeId.Fire, 0.7),
            new ElementPayloadComponent(ElementTypeId.Air, 0.3)
        });
        Assert.Equal(2, payload.Components.Count);
    }
}
