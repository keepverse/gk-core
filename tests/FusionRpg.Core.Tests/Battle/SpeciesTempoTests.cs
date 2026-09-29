using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `battle-tempo` `tempo-content` (spec-tempo-content.md). `SpeciesTempoProjection.SpeedFor` is the
/// module's whole mechanism — a projection, not a lookup table, so every test here derives its
/// expectation from the real published numbers rather than a fixture guess.
/// </summary>
public class SpeciesTempoTests
{
    // The real, shipped anchors (gk-core/data/tuning/creature-shape.v1.json) and the real TurnDefaultSpeed
    // (gk-core/data/tuning/derived-stats.v2.json) — read as literals here because ContractTuningTestBootstrap
    // configures DerivedStatPolicy from the identical working set (tunables-ssot.md §7.2: "construct
    // one inline; no fixture files"), so this is the SAME 100 the assembly-wide bootstrap loads.
    const long Ponderous = 3000, Slow = 2400, Steady = 1500, Quick = 900, Flurry = 500;
    const long ReferenceIntervalMs = Steady; // spec-tempo-content.md §2.1: steady is the anchor.

    [Fact]
    public void TheFiveShippedTemposProjectToFiveDistinctOrderedSpeeds()
    {
        var defaultSpeed = DerivedStatPolicy.TurnDefaultSpeed;

        var ponderous = SpeciesTempoProjection.SpeedFor(Ponderous, ReferenceIntervalMs, defaultSpeed);
        var slow = SpeciesTempoProjection.SpeedFor(Slow, ReferenceIntervalMs, defaultSpeed);
        var steady = SpeciesTempoProjection.SpeedFor(Steady, ReferenceIntervalMs, defaultSpeed);
        var quick = SpeciesTempoProjection.SpeedFor(Quick, ReferenceIntervalMs, defaultSpeed);
        var flurry = SpeciesTempoProjection.SpeedFor(Flurry, ReferenceIntervalMs, defaultSpeed);

        Assert.True(ponderous < slow, $"{ponderous} < {slow}");
        Assert.True(slow < steady, $"{slow} < {steady}");
        Assert.True(steady < quick, $"{steady} < {quick}");
        Assert.True(quick < flurry, $"{quick} < {flurry}");

        // The steady anchor projects to exactly the default -- no scaling either way, by construction.
        Assert.Equal(defaultSpeed, steady);
    }

    [Fact]
    public void TheFloorHoldsForZeroOrNegativeIntervalAndNeverThrows()
    {
        var defaultSpeed = DerivedStatPolicy.TurnDefaultSpeed;
        Assert.Equal(defaultSpeed, SpeciesTempoProjection.SpeedFor(0, ReferenceIntervalMs, defaultSpeed));
        Assert.Equal(defaultSpeed, SpeciesTempoProjection.SpeedFor(-1, ReferenceIntervalMs, defaultSpeed));
    }

    [Fact]
    public void EqualTemposReproduceTodaysOrderingExactly()
    {
        // The containment property: two actors sharing a tempo project to the SAME speed, so their
        // relative order still falls through to the initiative jitter exactly as it did before this
        // module existed -- the "moves nothing when nothing differs" half of tempo-content's contract.
        var defaultSpeed = DerivedStatPolicy.TurnDefaultSpeed;
        var a = SpeciesTempoProjection.SpeedFor(Quick, ReferenceIntervalMs, defaultSpeed);
        var b = SpeciesTempoProjection.SpeedFor(Quick, ReferenceIntervalMs, defaultSpeed);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(long.MaxValue / 100)] // near the long ceiling for the numerator multiply, not overflowing
    public void ExtremeIntervalsNeverOverflow(long hugeInterval)
    {
        var defaultSpeed = DerivedStatPolicy.TurnDefaultSpeed;
        var speed = SpeciesTempoProjection.SpeedFor(hugeInterval, ReferenceIntervalMs, defaultSpeed);
        Assert.True(speed >= 1); // Math.Max(1, ...) floor still holds at the extreme
    }

    [Fact]
    public void ReferenceIntervalMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeciesTempoProjection.SpeedFor(Steady, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeciesTempoProjection.SpeedFor(Steady, -1, 100));
    }

    [Fact]
    public void DefaultSpeedMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeciesTempoProjection.SpeedFor(Steady, ReferenceIntervalMs, 0));
    }

    /// <summary>
    /// spec-tempo-content.md §2.3: `swift` moves the initiative jitter, not `turn.speed` — re-pointing
    /// it would double-count the same advantage two ways. This is a closed-vocabulary check: `swift`'s
    /// own tuning row must carry `initiativeBonusMilli` and nothing that resembles a speed channel.
    /// </summary>
    [Fact]
    public void SwiftIsNotDoubleCountedItMovesTheJitterNotTheSpeed()
    {
        var swift = TraitBattleCatalog.All.Single(t => t.TraitId == "swift");
        Assert.True(swift.InitiativeBonusMilli > 0, "swift must still carry its initiative bonus");
        Assert.Empty(swift.ChannelMods.Where(m =>
            m.ChannelId == DerivedTurnChannels.Speed || m.ChannelId == DerivedTurnChannels.Haste));
    }

    /// <summary>
    /// End-to-end: `WaveCatalog` carries a species' interval onto `BattleActorSetup`, and
    /// `BattleHubCompose.Compose` projects it into `turn.speed` -- the production path, not a
    /// synthetic channel mod (the gap `B39` could only prove around).
    /// </summary>
    [Fact]
    public void AFasterSpeciesActsFirstOnTheProductionPathProvenByContrastBothDirections()
    {
        var setupA = new BattleActorSetup { Key = "a", MaxHp = 100, AttackIntervalMs = Flurry };  // fast
        var setupB = new BattleActorSetup { Key = "b", MaxHp = 100, AttackIntervalMs = Ponderous }; // slow

        var derivedA = BattleHubCompose.Compose(setupA);
        var derivedB = BattleHubCompose.Compose(setupB);

        var speedA = derivedA.Get(DerivedTurnChannels.Speed);
        var speedB = derivedB.Get(DerivedTurnChannels.Speed);
        Assert.True(speedA > speedB, "the flurry-tempo actor must project a higher turn.speed");

        // Contrast in the OTHER direction -- swap which setup is fast, confirm the relationship flips,
        // so an initiative roll cannot be passing this by luck.
        var setupC = new BattleActorSetup { Key = "c", MaxHp = 100, AttackIntervalMs = Ponderous };
        var setupD = new BattleActorSetup { Key = "d", MaxHp = 100, AttackIntervalMs = Flurry };
        var speedC = BattleHubCompose.Compose(setupC).Get(DerivedTurnChannels.Speed);
        var speedD = BattleHubCompose.Compose(setupD).Get(DerivedTurnChannels.Speed);
        Assert.True(speedD > speedC);
    }
}
