using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Duration;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T28 (action-todo.md, spec-duration-resolver.md): the seam and the clamp, proved against a local,
/// test-only fixture resolver (<see cref="FixtureDurationResolver"/>). <b>T29
/// (<c>BattleDurationResolver</c>) landed 2026-08-28</b>, unblocked by `P0.5`
/// (battle-timeline's own `turn.speed`/`turn.haste` registration + <c>TurnReadiness.cs</c>, built the
/// same day under explicit owner authorization to cross the program boundary) — see the
/// `BattleDurationResolver` tests below for its own, separate proof against the REAL, registered
/// channels, not the fixture.
/// </summary>
public class DurationResolverTests
{
    static string TuningPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "data", "tuning", "action-duration.v1.json");
    }

    static readonly DurationTuning Shipped = DurationTuningLoader.Parse(File.ReadAllText(TuningPath()));

    /// <summary>A TEST-ONLY fixture proving the seam's contract — maps victim turns to ticks via a
    /// per-victim ticks-per-turn table the test supplies directly, standing in for whatever a real
    /// <c>turn.speed</c> read would someday produce. Never shipped; lives only in this file.</summary>
    sealed class FixtureDurationResolver : IDurationResolver
    {
        readonly IReadOnlyDictionary<string, long> _ticksPerTurnByVictim;
        public FixtureDurationResolver(IReadOnlyDictionary<string, long> ticksPerTurnByVictim) => _ticksPerTurnByVictim = ticksPerTurnByVictim;
        public long ToTicks(int victimTurns, string victimPtr) => checked((long)victimTurns * _ticksPerTurnByVictim[victimPtr]);
    }

    [Fact]
    public void DeepFreezeFixtureFourPlusTenAuthoredTurnsIsBoundedNotAFourteenTurnLock()
    {
        // spec's own fixture: Freeze (4 victim turns) + Chill (10 victim turns) stack additively to a
        // naive "14-turn lock" -- the ALMANAC form, what stacking looked like before this module's
        // clamp existed. The RESOLVED form this module actually produces is bounded at
        // MaxVictimTurns with the excess redirected into intensity -- the authored and almanac forms
        // MUST differ, which is "the whole reason the module exists" (testing-strategy table).
        const long freezeVictimTurnsMilli = 4 * 1000;
        const long chillVictimTurnsMilli = 10 * 1000;
        const long almanacCombinedVictimTurns = 14; // 4 + 10, naive sum, no clamp

        var combinedMilli = freezeVictimTurnsMilli + chillVictimTurnsMilli;
        var result = DurationClamp.ClampAndConvert(combinedMilli, Shipped);

        Assert.Equal(almanacCombinedVictimTurns, combinedMilli / 1000); // the almanac form really is 14
        Assert.NotEqual(almanacCombinedVictimTurns, result.ClampedVictimTurns); // authored form differs
        Assert.Equal(Shipped.MaxVictimTurns, result.ClampedVictimTurns); // bounded, not a 14-turn lock
        Assert.True(result.IntensityBonusMilli > 0); // the excess went somewhere, not nowhere
    }

    [Fact]
    public void TwoActorsDifferingTwoTimesInCadenceResolveTheSameAuthoredTurnsToDifferentTicks()
    {
        var resolver = new FixtureDurationResolver(new Dictionary<string, long>
        {
            ["wave:slow"] = 100, // 100 ticks per turn
            ["wave:fast"] = 50,  // half the ticks per turn -- 2x cadence
        });

        var slowTicks = resolver.ToTicks(victimTurns: 3, "wave:slow");
        var fastTicks = resolver.ToTicks(victimTurns: 3, "wave:fast");

        Assert.Equal(300, slowTicks);
        Assert.Equal(150, fastTicks);
        Assert.NotEqual(slowTicks, fastTicks); // same authored turns, different tick counts
    }

    [Fact]
    public void ThetaNeverMovesAResolvedTurnCount()
    {
        // Theta-free by construction (spec S1/PS-3): ToTicks's own signature has no room for Theta at
        // all, proven directly by reflection rather than by convention -- and the same authored turn
        // count against the SAME cadence produces the SAME ticks regardless of which victim (a
        // Theta=20 or a Theta=5000 actor) holds that cadence.
        var method = typeof(IDurationResolver).GetMethod(nameof(IDurationResolver.ToTicks))!;
        var parameterTypes = Array.ConvertAll(method.GetParameters(), p => p.ParameterType);
        Assert.Equal(new[] { typeof(int), typeof(string) }, parameterTypes);
        Assert.Equal(typeof(long), method.ReturnType);

        var resolver = new FixtureDurationResolver(new Dictionary<string, long>
        {
            ["theta:20"] = 100,
            ["theta:5000"] = 100, // identical cadence -- only the label differs
        });

        Assert.Equal(resolver.ToTicks(2, "theta:20"), resolver.ToTicks(2, "theta:5000"));
    }

    [Fact]
    public void ClampPositionAPlantedAuthoringTimeClampFailsToBoundAStackingBuild()
    {
        // S3.1's own failure mode: a clamp applied to the AUTHORED value (before durationNetFactor
        // scaling) never sees the stacking build's real total, so it reports "fine" while the actual
        // resolved duration blows past the bound. Planted here explicitly so the real (post-scale)
        // clamp's necessity is proven, not assumed.
        const long authoredVictimTurnsMilli = 2 * 1000; // a modest, well-under-cap authored value
        const int durationNetFactorPercent = 900; // a stacking build multiplies it 9x
        var resolvedVictimTurnsMilli = checked(authoredVictimTurnsMilli * durationNetFactorPercent / 100);

        var plantedAuthoringTimeClampPasses = authoredVictimTurnsMilli / 1000 <= Shipped.MaxVictimTurns;
        Assert.True(plantedAuthoringTimeClampPasses); // the planted bug's own check says "fine"...
        Assert.True(resolvedVictimTurnsMilli / 1000 > Shipped.MaxVictimTurns); // ...but the real total is not

        var real = DurationClamp.ClampAndConvert(resolvedVictimTurnsMilli, Shipped);
        Assert.Equal(Shipped.MaxVictimTurns, real.ClampedVictimTurns); // the REAL clamp catches it
    }

    [Fact]
    public void ClampAndConvertKeepsTotalEffectRisingPastTheBoundViaIntensity()
    {
        var atBound = DurationClamp.ClampAndConvert(Shipped.MaxVictimTurns * 1000, Shipped);
        var wellPastBound = DurationClamp.ClampAndConvert((Shipped.MaxVictimTurns + 20) * 1000, Shipped);

        Assert.Equal(Shipped.MaxVictimTurns, atBound.ClampedVictimTurns);
        Assert.Equal(Shipped.MaxVictimTurns, wellPastBound.ClampedVictimTurns); // turns never exceed the bound
        Assert.Equal(0, atBound.IntensityBonusMilli);
        Assert.True(wellPastBound.IntensityBonusMilli > atBound.IntensityBonusMilli); // total effect keeps rising
    }

    static string DurationClampSourcePath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Actions", "Duration", "DurationClamp.cs");
    }

    [Fact]
    public void BoundedRatioCommentExistsAtTheDeclaration()
    {
        // spec S1: "the declaration must say so in a comment" -- read the real declaring file's own
        // source text (the [CallerFilePath] pattern this suite already uses for on-disk tuning),
        // rather than re-asserting the rule from a runtime constant nothing forces to stay truthful.
        var path = DurationClampSourcePath();
        Assert.True(File.Exists(path), $"DurationClamp.cs not found at {path}");
        var source = File.ReadAllText(path);
        Assert.Contains("PS-8", source);
        Assert.Contains("BOUNDED RATIO", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new object[] { new[] { StatusL2bCategory.Cc } })]
    [InlineData(new object[] { new[] { StatusL2bCategory.Cc, StatusL2bCategory.Dot } })]
    public void ControlFamilyStatusesAreAcceptedForTurnAuthoring(string[] categories) =>
        DurationAuthoringGuard.RequireControlFamily("freeze", categories); // must not throw

    [Theory]
    [InlineData(new object[] { new[] { StatusL2bCategory.Dot } })]
    [InlineData(new object[] { new string[0] })]
    public void APlantedTurnAuthoredDotOrBuffIsRejected(string[] categories) =>
        Assert.Throws<TurnAuthoredDurationRejection>(() => DurationAuthoringGuard.RequireControlFamily("wither", categories));

    [Fact]
    public void NoResolverRegisteredThrowsNamingTheModeNeverSilentlyDefaults()
    {
        var registry = new DurationResolverRegistry();
        var ex = Assert.Throws<NoDurationResolverRegisteredException>(() => registry.Resolve("battle"));
        Assert.Contains("battle", ex.Message);
    }

    [Fact]
    public void ARegisteredModeResolvesThroughToTheRealResolverInstance()
    {
        var registry = new DurationResolverRegistry();
        var fixture = new FixtureDurationResolver(new Dictionary<string, long> { ["wave:0"] = 42 });
        registry.Register("lawn", fixture);

        Assert.Same(fixture, registry.Resolve("lawn"));
        Assert.Throws<NoDurationResolverRegisteredException>(() => registry.Resolve("battle")); // still unregistered
    }

    // ---- BattleDurationResolver (P0.5 unblocked T29, 2026-08-28) -------------------------------

    static ActorDerivedSnapshot TurnSnapshot(double speed, double haste)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            // turn.speed is FlatSum over its REGISTERED base, so the modifier is the delta from that
            // base — which is the configured default, not the readiness formula's scale unit.
            new DerivedModifier(DerivedTurnChannels.Speed, DerivedModifierOp.Flat, speed - DerivedStatPolicy.TurnDefaultSpeed, SourceId: "test"),
            new DerivedModifier(DerivedTurnChannels.Haste, DerivedModifierOp.Flat, haste - DerivedTurnChannels.NominalHasteMilli, SourceId: "test"),
        });
    }

    [Fact]
    public void TwoActorsDifferingTwoTimesInRealTurnSpeedResolveTheSameAuthoredTurnsToDifferentTicks()
    {
        var slow = new BattleDurationResolver(_ => TurnSnapshot(speed: 100, haste: 1000));
        var fast = new BattleDurationResolver(_ => TurnSnapshot(speed: 200, haste: 1000)); // 2x speed

        var slowTicks = slow.ToTicks(victimTurns: 3, "wave:slow");
        var fastTicks = fast.ToTicks(victimTurns: 3, "wave:fast");

        Assert.Equal(300, slowTicks); // 3 turns * 100 ticks/turn at the default rate
        Assert.Equal(150, fastTicks); // half the ticks at double speed
        Assert.NotEqual(slowTicks, fastTicks);
    }

    [Fact]
    public void HasteAloneAlsoMovesTheResolvedTickCount()
    {
        var resolver = new BattleDurationResolver(_ => TurnSnapshot(speed: 100, haste: 500)); // twice as fast
        Assert.Equal(100, resolver.ToTicks(victimTurns: 2, "wave:0")); // 200 at normal haste, halved
    }

    [Fact]
    public void ZeroAuthoredTurnsResolvesToZeroTicksNoReadinessFloorApplies()
    {
        var resolver = new BattleDurationResolver(_ => TurnSnapshot(speed: 100, haste: 1000));
        Assert.Equal(0, resolver.ToTicks(victimTurns: 0, "wave:0"));
    }

    [Fact]
    public void AZeroOrNegativeReadRateClampsToTheRegisteredDefaultRatherThanThrowingOrDividingByZero()
    {
        // speed/haste read as <= 0 (an un-hydrated snapshot, or BattleStatComposer's own real gap --
        // it seeds only its own level-formula channels, so an actor with no explicit ChannelMod reads
        // 0 for BOTH) clamps to the REGISTERED DEFAULT (100/1000), not an arbitrary 1 -- spec's own
        // "speed clamped before division" boundary rule, enforced at the resolver's own read site
        // since TurnReadiness itself throws on <= 0 by design.
        var resolver = new BattleDurationResolver(_ => ActorDerivedSnapshot.StubNeutral());
        var ticks = resolver.ToTicks(victimTurns: 1, "wave:0"); // StubNeutral has no turn.* set -> Get returns 0 for both
        // T14/B28: the expectation is derived from the CONFIGURED default speed, so this doubles as
        // the binding test -- if data/tuning/derived-stats.v{n}.json's turnDefaultSpeed stopped
        // reaching BattleDurationResolver, this fails. Not a tautology: the resolver could have
        // clamped to 1, to 0, or to NominalHasteMilli, and every one of those breaks this line.
        Assert.Equal(TurnReadiness.TicksPerFullTurn(DerivedStatPolicy.TurnDefaultSpeed), ticks);
        Assert.True(ticks > 1, "a degenerate 1-tick turn means the clamp fell back to 1, not the registered default");
    }

    [Fact]
    public void TurnSpeedDoesNotMoveARegisteredIndependentOfTheta()
    {
        // PS-3 by construction: nothing about ToTicks reads Theta at all (already proven for the
        // seam in ThetaNeverMovesAResolvedTurnCount); re-proven here for the REAL resolver specifically.
        var resolver = new BattleDurationResolver(_ => TurnSnapshot(speed: 100, haste: 1000));
        Assert.Equal(resolver.ToTicks(2, "theta:20"), resolver.ToTicks(2, "theta:5000")); // same snapshot regardless of the label
    }
}
