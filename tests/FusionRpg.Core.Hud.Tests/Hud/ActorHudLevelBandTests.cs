using FusionRpg.Core.Hud;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudLevelBandTests : IDisposable
{
    public ActorHudLevelBandTests() => InjectorDerivedOverride.Clear();

    public void Dispose() => InjectorDerivedOverride.Clear();

    [Fact]
    public void Build_levelBand_from_pin()
    {
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 12),
        }));

        Assert.True(InjectorDerivedOverride.TryGet("ABC", out var derived));
        var levelBand = PowerBandDisplay.FromTheta((long)derived.Get(DerivedStatChannels.ProgressionPower));

        Assert.Equal(12, levelBand);

        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, levelBand, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        var identity = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["identity"]);
        Assert.Equal(12, identity["levelBand"]);
    }

    [Fact]
    public void Build_omits_levelBand_without_pin()
    {
        int? levelBand = null;
        if (InjectorDerivedOverride.TryGet("NOPE", out var derived))
            levelBand = PowerBandDisplay.FromTheta((long)derived.Get(DerivedStatChannels.ProgressionPower));

        Assert.Null(levelBand);

        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, levelBand, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        var identity = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["identity"]);
        Assert.False(identity.ContainsKey("levelBand"));
    }
}
