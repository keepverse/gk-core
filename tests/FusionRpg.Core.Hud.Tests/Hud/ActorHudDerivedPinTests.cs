using FusionRpg.Core.Hud;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudDerivedPinTests : IDisposable
{
    public ActorHudDerivedPinTests() => InjectorDerivedOverride.Clear();

    public void Dispose() => InjectorDerivedOverride.Clear();

    [Fact]
    public void Pin_TryGet_returns_progression_power()
    {
        var snapshot = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 42),
        });

        InjectorDerivedOverride.Pin("1A2B", snapshot);

        Assert.True(InjectorDerivedOverride.TryGet("1A2B", out var read));
        Assert.Equal(42, read.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void Pin_normalizes_ptr_key()
    {
        InjectorDerivedOverride.Pin("1a2b", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 7),
        }));

        Assert.True(InjectorDerivedOverride.TryGet("1A2B", out var read));
        Assert.Equal(7, read.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void Pin_overwrites_same_ptr()
    {
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 10),
        }));
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 99),
        }));

        Assert.True(InjectorDerivedOverride.TryGet("ABC", out var read));
        Assert.Equal(99, read.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void Clear_empties_pins()
    {
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.StubNeutral());

        InjectorDerivedOverride.Clear();

        Assert.False(InjectorDerivedOverride.TryGet("ABC", out _));
    }

    [Fact]
    public void Pinned_power_maps_to_stable_level_band()
    {
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 25),
        }));

        Assert.True(InjectorDerivedOverride.TryGet("ABC", out var read));
        var bandA = PowerBandDisplay.FromTheta((long)read.Get(DerivedStatChannels.ProgressionPower));
        var bandB = PowerBandDisplay.FromTheta((long)read.Get(DerivedStatChannels.ProgressionPower));

        Assert.Equal(25, bandA);
        Assert.Equal(bandA, bandB);
    }

    [Fact]
    public void FromTheta_truncates_fractional_progression_power()
    {
        InjectorDerivedOverride.Pin("ABC", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 25.9),
        }));

        Assert.True(InjectorDerivedOverride.TryGet("ABC", out var read));
        var band = PowerBandDisplay.FromTheta((long)read.Get(DerivedStatChannels.ProgressionPower));

        Assert.Equal(25, band);
    }
}
