using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// battle-timeline T14/B28 (spec-timeline-tunables.md §1) — `turnDefaultSpeed` is a published balance
/// dial, and `TurnReadiness.SpeedScale` is a structural unit. Before B28 they were one constant
/// (`DerivedTurnChannels.BaseSpeed`) and a caller could not tell which it meant.
///
/// <para>These tests exist to make the split <b>falsifiable</b>. The acceptance is not "a number came
/// back" — it is that the registered default genuinely comes from config (so a balance pass can move
/// it without a rebuild) and that config cannot silently omit it (so the value can never quietly
/// revert to a hardcoded one).</para>
/// </summary>
public class TurnDefaultSpeedTuningTests
{
    /// <summary>The load-bearing one. If <c>turn.speed</c>'s registered default were still a compile-time
    /// constant, this fails — the scoped tuning would be ignored and the base would stay 100.</summary>
    [Fact]
    public void TheRegisteredTurnSpeedDefaultComesFromConfig_notAConstant()
    {
        using var _ = DerivedStatPolicy.UseScoped(
            new DerivedStatTuning(SchemaVersion: 2, Version: 2, CategoryResistCap: 0.95, TurnDefaultSpeed: 250));

        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedTurnChannels.Speed, out var def));
        Assert.Equal(250, def!.DefaultValue);
    }

    /// <summary>Contrast case, so the test above cannot pass by accident on a fixed value.</summary>
    [Fact]
    public void ADifferentConfiguredValueProducesADifferentRegisteredDefault()
    {
        double Base(long configured)
        {
            using var _ = DerivedStatPolicy.UseScoped(
                new DerivedStatTuning(SchemaVersion: 2, Version: 2, CategoryResistCap: 0.95, TurnDefaultSpeed: configured));
            var registry = DerivedStatRegistry.CreateDefault();
            registry.TryGet(DerivedTurnChannels.Speed, out var def);
            return def!.DefaultValue;
        }

        Assert.NotEqual(Base(100), Base(400));
    }

    /// <summary>No silent fallback: a tuning document that forgets the key is REFUSED, not defaulted.
    /// This is what stops the number quietly returning to code.</summary>
    [Fact]
    public void ConfigMissingTurnDefaultSpeedIsRefused()
    {
        var ex = Assert.Throws<DerivedStatTuningRejection>(() =>
            DerivedStatTuningLoader.Parse("""{"schemaVersion":2,"version":2,"categoryResistCap":0.95}"""));
        Assert.Contains("turnDefaultSpeed", ex.Message);
    }

    /// <summary>It is a divisor and a rate, so a non-positive value is refused at the boundary rather
    /// than producing a divide-by-zero deep inside the readiness formula — the same "clamped before
    /// division" rule spec-readiness-model.md states, enforced one layer earlier.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTurnDefaultSpeedIsRefused(long bad)
    {
        var ex = Assert.Throws<DerivedStatTuningRejection>(() =>
            DerivedStatTuningLoader.Parse(
                $$"""{"schemaVersion":2,"version":2,"categoryResistCap":0.95,"turnDefaultSpeed":{{bad}}}"""));
        Assert.Contains("turnDefaultSpeed", ex.Message);
    }

    /// <summary><see cref="TurnReadiness.SpeedScale"/> is the OTHER half and must stay structural: the
    /// readiness formula is invariant under scaling it, because work and rate are both expressed in
    /// those units. Proven by construction rather than asserted in prose — a full turn at the default
    /// rate costs exactly one scale unit of ticks, whatever the scale is.</summary>
    [Fact]
    public void SpeedScaleIsAUnit_theFormulaIsInvariantUnderIt()
    {
        Assert.Equal(TurnReadiness.SpeedScale, TurnReadiness.TicksPerFullTurn(TurnReadiness.SpeedScale));
        Assert.Equal(TurnReadiness.SpeedScale, TurnReadiness.OneTurnWork);
    }
}
