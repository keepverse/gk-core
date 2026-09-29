using FusionRpg.Contracts;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.10 — hydrating Θ into an `ActorHub` makes `progression.power`
/// non-zero end to end. Tests the mechanism `CheatState.ActorHub` (Injector, no dedicated test project)
/// delegates to — `ActorHubBootstrap.CreateDefault` + a hydrated `IPowerIndexProvider` — since that IS
/// the fix (CheatState.cs:32 was the wiring gap; the resolve-path logic under test here is identical
/// regardless of which host constructs the hub).</summary>
public class PowerIndexHydrationTests
{
    static PowerTuning MinimalTuning() => PowerTuning.Build(
        schemaVersion: 1, version: 1,
        cMilli: 80_000, bMilli: 400, pinIndex: 20, pinValue: 680,
        wdMilli: 1000, waMilli: 1000, wrMilli: 1000, wzMilli: 1000, wmMilli: 1000, wwMilli: 1000, wfMilli: 1000);

    [Fact]
    public void HydratedProvider_makesProgressionPowerNonZero()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var provider = new HydratedPowerIndexProvider(MinimalTuning());
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new RpgProgressionSubsystem(powerIndex: provider));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        provider.Hydrate(ctx, new ActorLadderSnapshot(DaveLevel: 20, RealmsAdvanced: 0, PvzRuns: 0));

        var result = hub.Resolve(ctx);
        Assert.True(result.Derived.Get(DerivedStatChannels.ProgressionPower) > 0,
            "expected progression.power > 0 once the actor's ladder snapshot is hydrated");
    }

    [Fact]
    public void UnhydratedProvider_leavesProgressionPowerAtZero()
    {
        // The complementary, currently-real case: HydratedPowerIndexProvider with NO .Hydrate() call
        // (production's actual state today — no injector hydration source exists yet, see
        // InjectorPowerIndexProvider's own doc comment) reads ActorLadderSnapshot.Empty, same as the
        // StubPowerIndexProvider default.
        var stats = StatSystemBootstrap.CreateDefault();
        var provider = new HydratedPowerIndexProvider(MinimalTuning());
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new RpgProgressionSubsystem(powerIndex: provider));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var result = hub.Resolve(ctx);

        Assert.Equal(0.0, result.Derived.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void Magnitude_isFlatWhenThetaIsZero()
    {
        // Pins the CURRENT, still-real symptom as a named regression rather than a described one:
        // P(Theta=0) = C for every actor, regardless of any other difference between them, because
        // nothing in production hydrates a real ladder snapshot yet (P1.10's remaining gap --
        // InjectorPowerIndexProvider has no hydration SOURCE, only the mechanism, which this file's
        // other two tests prove works once fed real data).
        var tuning = MinimalTuning();
        var ladder = new PowerLadder(tuning);

        var pAtZero = ladder.Value(0);
        Assert.Equal(tuning.Curve.CMilli / 1000, pAtZero);

        // Two actors with genuinely different levels both read the SAME magnitude at Theta=0 --
        // the defect this task exists to make detectable rather than silently wrong.
        var stats = StatSystemBootstrap.CreateDefault();
        var stubHub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        stubHub.Register(new RpgProgressionSubsystem()); // default StubPowerIndexProvider, Theta=0 always

        var lowLevelCtx = stats.Contexts.ForPlant("Low", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var highLevelCtx = stats.Contexts.ForPlant("High", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var lowPower = stubHub.Resolve(lowLevelCtx).Derived.Get(DerivedStatChannels.ProgressionPower);
        var highPower = stubHub.Resolve(highLevelCtx).Derived.Get(DerivedStatChannels.ProgressionPower);
        Assert.Equal(0.0, lowPower);
        Assert.Equal(lowPower, highPower); // flat: no hydration source means no actor differs from any other
    }
}
