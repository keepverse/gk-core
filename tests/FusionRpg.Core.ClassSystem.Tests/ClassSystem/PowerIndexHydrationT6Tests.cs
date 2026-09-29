using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>aura-skill T6 (W2): `InjectorPowerIndexProvider.Hydrate` existed with no caller —
/// `PowerIndexHydrationTests.Magnitude_isFlatWhenThetaIsZero` pins the pre-T6 symptom (every actor
/// reads `P(0) = C` regardless of level). These are the complementary post-T6 proofs the acceptance
/// text names directly: two different Θ values must produce two different magnitudes, and Θ = 0 must
/// still resolve without throwing (the flat floor is a real value, not a crash).</summary>
public class PowerIndexHydrationT6Tests
{
    static PowerTuning MinimalTuning() => PowerTuning.Build(
        schemaVersion: 1, version: 1,
        cMilli: 80_000, bMilli: 400, pinIndex: 20, pinValue: 680,
        wdMilli: 1000, waMilli: 1000, wrMilli: 1000, wzMilli: 1000, wmMilli: 1000, wwMilli: 1000, wfMilli: 1000);

    [Fact]
    public void Two_hydrated_Theta_values_produce_two_different_magnitudes()
    {
        var tuning = MinimalTuning();
        var ladder = new PowerLadder(tuning);
        var provider = new HydratedPowerIndexProvider(tuning);
        var stats = StatSystemBootstrap.CreateDefault();

        // HydratedPowerIndexProvider.Key is PlayerId -- NOT EntityKey, Side or TypeId (spec-power-
        // index.md §2.2: Θ is a per-player ladder bucket, not a per-actor-instance one). Two DIFFERENT
        // players is the real-world shape this actually varies over; two same-key contexts sharing
        // only a different EntityKey would collide on the SAME cache slot, which a first draft of this
        // test discovered the hard way (both writes landing on the same key silently overwrote each
        // other, both entries reading DaveLevel=50).
        var lowCtx = stats.Contexts.ForPlant("Low", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }, playerId: 1);
        var highCtx = stats.Contexts.ForPlant("High", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }, playerId: 2);
        provider.Hydrate(lowCtx, new ActorLadderSnapshot(DaveLevel: 5, RealmsAdvanced: 0, PvzRuns: 0));
        provider.Hydrate(highCtx, new ActorLadderSnapshot(DaveLevel: 50, RealmsAdvanced: 0, PvzRuns: 0));

        var thetaLow = provider.ActorIndex(lowCtx);
        var thetaHigh = provider.ActorIndex(highCtx);
        Assert.NotEqual(thetaLow, thetaHigh); // different ladder snapshots must yield different Θ

        var magnitudeLow = ladder.Value(thetaLow);
        var magnitudeHigh = ladder.Value(thetaHigh);
        Assert.NotEqual(magnitudeLow, magnitudeHigh); // the flat P(0)=C floor this task exists to fix

        // Proven end to end through ActorHub too, not just the provider directly.
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new RpgProgressionSubsystem(powerIndex: provider));
        var lowPower = hub.Resolve(lowCtx).Derived.Get(DerivedStatChannels.ProgressionPower);
        var highPower = hub.Resolve(highCtx).Derived.Get(DerivedStatChannels.ProgressionPower);
        Assert.NotEqual(lowPower, highPower);
    }

    [Fact]
    public void Theta_zero_still_resolves_without_throwing()
    {
        var tuning = MinimalTuning();
        var provider = new HydratedPowerIndexProvider(tuning);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = stats.Contexts.ForPlant("Zero", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        provider.Hydrate(ctx, new ActorLadderSnapshot(DaveLevel: 0, RealmsAdvanced: 0, PvzRuns: 0));

        var exception = Record.Exception(() =>
        {
            var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
            hub.Register(new RpgProgressionSubsystem(powerIndex: provider));
            hub.Resolve(ctx);
        });

        Assert.Null(exception);
    }
}
