using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T27 (action-todo.md, spec-defence-actions.md §4): the riposte, and §0's composition claim
/// ("a guarded actor blocks more often because they guarded"). Named <c>DefenceAction*</c> so both
/// T25's and T27's declared verify filter (<c>--filter ~DefenceAction</c>) actually finds these tests
/// — the spec's own "Structure" section names one <c>DefenceActionTests.cs</c>, but this program
/// already splits by concern (<see cref="DefenceActionStanceTests"/>, <c>PoiseLedgerTests</c>, …);
/// the filter substring is what has to match, not the literal file count.
/// </summary>
public class DefenceActionRiposteTests
{
    [Theory]
    [InlineData(500, 200, 100)] // spec example shape: half the pool, half share -> quarter back as damage
    [InlineData(1000, 1000, 1000)] // share = 1000 (100%) -> all of it converts
    [InlineData(1000, 0, 0)] // share = 0 -> a guard that never ripostes, still legal
    public void DamageIsSpentPoiseTimesShareExactly(long spentPoise, int shareMilli, long expected) =>
        Assert.Equal(expected, Riposte.DamageFromSpentPoise(spentPoise, shareMilli));

    [Fact]
    public void NegativeSpentPoiseIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Riposte.DamageFromSpentPoise(-1, 500));

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void ShareOutsideTheBoundedZeroToOneRatioIsRejected(int shareMilli) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Riposte.DamageFromSpentPoise(1000, shareMilli));

    /// <summary>
    /// "Output scales with `Θ` because the pool does" — Riposte authors no `Θ` curve of its own (spec
    /// §4), so the proof is simply that it never clamps: a `poise` pool an order of magnitude larger
    /// than any pool T15's channels would realistically hand it — deliberately picked close to the
    /// `long` per-mille ceiling `docs/architecture/numeric-types.md` names (`Θ ≈ 3,213`, i.e. pools far past normal play) —
    /// still comes back proportionally larger, with no silent cap and no overflow wrap.
    /// </summary>
    [Fact]
    public void OutputScalesProportionallyWithAnArbitrarilyLargePoolNoPrivateCeiling()
    {
        const int shareMilli = 300;
        var small = Riposte.DamageFromSpentPoise(spentPoise: 10_000, shareMilli);
        var large = Riposte.DamageFromSpentPoise(spentPoise: 2_000_000_000_000L, shareMilli); // far past any Θ table entry
        Assert.Equal(3_000L, small);
        Assert.Equal(600_000_000_000L, large);
        Assert.True(large > small * 1_000); // no clamp collapsed the ratio
    }

    /// <summary>
    /// spec §0's composition claim, proved against the real <see cref="OverlayCombatCalculator"/> and
    /// a real <see cref="SeededCombatRng"/> rather than argued: a defender whose
    /// <c>combat.block.rate.omni</c> is raised the way a guard status raises it (an
    /// <see cref="ActorDerivedSnapshot.Overlay"/> delta — the same technique
    /// <c>EvasionChainTests</c> already uses to prove the rate contest itself) blocks measurably more
    /// often across identical rolls than an unguarded defender.
    ///
    /// <para><b>Honest boundary:</b> this proves the CONTENT claim — a raised <c>block.rate</c> value
    /// composes correctly through the shipped rate contest. It does not exercise
    /// <c>StatusStatPayload.ToModifiers</c> → the live modifier bag → a battle re-composed
    /// <see cref="ActorDerivedSnapshot"/>, because <c>ToModifiers</c> has zero production callers
    /// today — the same standing gap already logged for T16/T22
    /// (<c>BattleEngine.ActorState.Derived</c> is composed once at battle setup and never re-composed
    /// from live status state). A8 grants the StatMod; wiring a live re-compose is that gap's fix, not
    /// this module's.</para>
    /// </summary>
    [Fact]
    public void AGuardedDefenderBlocksMeasurablyMoreOftenAcrossIdenticalRolls()
    {
        var calc = new OverlayCombatCalculator();
        var attacker = ActorDerivedSnapshot.StubNeutral(); // parry.break = block.break = 0
        var unguardedDefender = ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 100), // some baseline block
        });
        var guardedDefender = unguardedDefender.Overlay(new[]
        {
            // the raised channel a guard-stance StatusStatMod("combat.block.rate.omni", "flat", 400) would contribute
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 500),
        });

        OverlayCombatRequest RequestAgainst(ActorDerivedSnapshot defender) => new()
        {
            BaseOverlayDamage = 100,
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral),
        };

        const int trials = 2000;
        var unguardedBlocks = 0;
        var guardedBlocks = 0;
        for (var seed = 1; seed <= trials; seed++)
        {
            if (calc.Compute(RequestAgainst(unguardedDefender), new SeededCombatRng(seed)).Breakdown.Blocked) unguardedBlocks++;
            if (calc.Compute(RequestAgainst(guardedDefender), new SeededCombatRng(seed)).Breakdown.Blocked) guardedBlocks++;
        }

        Assert.True(guardedBlocks > unguardedBlocks,
            $"guarded ({guardedBlocks}/{trials}) did not block measurably more often than unguarded ({unguardedBlocks}/{trials})");
        // "measurably" -- not a one-roll fluke: raising the band by 400/1000 of the roll's width should
        // move the observed frequency by roughly that much over enough trials, not by noise alone.
        Assert.True(guardedBlocks - unguardedBlocks > trials / 10);
    }
}
