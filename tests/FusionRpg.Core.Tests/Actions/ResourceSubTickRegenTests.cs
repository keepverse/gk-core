using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// `lawn-combat-wire` T5 / `resource-subtick` (spec-resource-subtick.md) — S10.1, the sub-tick unit
/// that `battle-resources.v1.json`'s own `_meta.regenIsAbsentOnPurpose` named as the fix for its own
/// diagnosis: the reader rounded the regen channel to a whole <c>long</c>, so the smallest
/// expressible non-zero rate was 1/tick, which over a ~300-tick round accrued ~300 poise against a
/// spend of 100. Regen was therefore switched off everywhere — not as a position against regen, but
/// because the unit was too coarse to hold one.
///
/// <para>The fix is carry-correction for <i>quantity</i>, the same discipline
/// <c>KernelDriveHost</c> already applies to <i>time</i>: it re-arms a repeating effect off the
/// event's own <c>DueTick</c> rather than off "now" (<c>KernelDriveHost.cs:186-192</c>), so a
/// stuttering frame merely delays a DoT tick instead of permanently slowing its cadence. Rounding
/// regen per tick is the quantity-side version of re-arming off "now" — the lost fraction never
/// comes back and the error compounds. Carrying the remainder makes it zero, forever.</para>
///
/// <para>This module makes rates <b>expressible</b>. It authors none itself — every assertion below
/// still reads 0 through the <b>ambient test fixture</b>
/// (<see cref="ContractTuningTestBootstrap.DefaultBattleResources"/>), which deliberately stays at
/// the pre-T11 all-zero baseline so this whole assembly stays byte-identical. `lawn-combat-wire` T11
/// (spec-lawn-combat-calibration.md, 2026-09-14) is the module that DOES author a rate — `stamina`,
/// in the real shipped <c>battle-resources.v2.json</c> — so <see cref="BattleRuleset.BaseResourceRegen"/>
/// itself is no longer an unconditional 0; it now reads <see cref="BattleResourceTuning.RegenShareOf"/>,
/// which happens to be 0 for every id in this assembly's own fixture. See
/// <c>LawnCombatCalibrationGuardTests</c> for the test that exercises the real v2 numbers directly.</para>
/// </summary>
public class ResourceSubTickRegenTests
{
    /// <summary>A snapshot carrying one poise pool. <paramref name="regenPerTick"/> is in WHOLE UNITS
    /// per tick — the channel's meaning is unchanged by S10.1; what changed is that the reader can
    /// now carry a fraction of one out of it.</summary>
    static ActorDerivedSnapshot PoiseSnapshot(double max, double regenPerTick)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax("poise"), DerivedModifierOp.Flat, max, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen("poise"), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        });
    }

    // ---------------------------------------------------------------- the unit exists at all

    /// <summary>The headline: a rate below one whole unit per tick used to round to 0 (nothing at
    /// all) or, one notch up, to 1 (ten units in ten ticks). Neither is 3.</summary>
    [Fact]
    public void ARateBelowOneUnitPerTickAccruesExactlyRatherThanRoundingToZeroOrToAWholeUnit()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0.3);   // 300 per-mille per tick
        Assert.Equal(300, ResourceChannelReader.RegenPerMilleTick(derived, "poise"));

        var state = new ResourcePoolState(Stored: 0, LastTick: 0);
        const long rate = 300;

        Assert.Equal(0, state.Resolve(1, rate, max: 1000));    // 300‰ — not yet one unit
        Assert.Equal(0, state.Resolve(3, rate, max: 1000));    // 900‰ — still not one
        Assert.Equal(1, state.Resolve(4, rate, max: 1000));    // 1200‰ — one unit, 200‰ carried
        Assert.Equal(3, state.Resolve(10, rate, max: 1000));   // 3000‰ — exactly three, not 0 and not 10
    }

    /// <summary>The 999 rates that did not exist before, each landing on its own distinct total —
    /// the gap between "nothing" and "three counters a round" is now populated.</summary>
    [Theory]
    [InlineData(1, 1)]        // 1‰/tick over 1000 ticks == 1 unit; formerly unrepresentable
    [InlineData(37, 37)]
    [InlineData(500, 500)]
    [InlineData(999, 999)]
    public void EveryPerMilleRateIsDistinctlyExpressibleOverAThousandTicks(long ratePerMille, long expectedUnits)
    {
        var state = new ResourcePoolState(Stored: 0, LastTick: 0);
        Assert.Equal(expectedUnits, state.Resolve(1000, ratePerMille, max: 100_000));
    }

    // ---------------------------------------------------------------- no drift

    /// <summary>
    /// The acceptance criterion: over ≥10,000 ticks, accrued == <c>floor(rate × ticks / 1000)</c>
    /// EXACTLY — measured by settling every single tick, which is the path that would drift if the
    /// remainder were dropped per tick. A per-tick round would give 0 for every rate below 500‰ and
    /// 10,000 for every rate at or above it; neither is the right answer for any of these.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(300)]
    [InlineData(333)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1234)]
    public void SettlingEveryTickForTenThousandTicksAccruesWithZeroDrift(long ratePerMille)
    {
        const long ticks = 10_000;
        var derived = PoiseSnapshot(max: 100_000_000, regenPerTick: ratePerMille / 1000.0);
        Assert.Equal(ratePerMille, ResourceChannelReader.RegenPerMilleTick(derived, "poise"));

        var perTick = ActorResourcePools.FromStored(Empty(), atTick: 0);
        for (long t = 1; t <= ticks; t++)
            perTick.Add("poise", 0, t, derived);   // Add(0) settles the pool without moving it

        var expected = ratePerMille * ticks / 1000;   // divide by 1000 LAST, exactly once
        Assert.Equal(expected, perTick.Resolve("poise", ticks, derived));

        // And settling once at the end must agree with settling ten thousand times — the two
        // disagree by exactly the accumulated rounding error, so equality IS the no-drift proof.
        var oneShot = ActorResourcePools.FromStored(Empty(), atTick: 0);
        Assert.Equal(expected, oneShot.Resolve("poise", ticks, derived));
    }

    /// <summary>Determinism: integer-only arithmetic, so the same inputs give the same sequence every
    /// run — asserted as the full 10,000-sample sequence, not just its endpoint.</summary>
    [Fact]
    public void ThePerTickSequenceIsIdenticalAcrossRuns()
    {
        const long ticks = 10_000;
        var derived = PoiseSnapshot(max: 100_000, regenPerTick: 0.333);

        static long[] Run(ActorDerivedSnapshot derived, long ticks)
        {
            var pools = ActorResourcePools.FromStored(Empty(), atTick: 0);
            var samples = new long[ticks];
            for (long t = 1; t <= ticks; t++)
                samples[t - 1] = pools.Add("poise", 0, t, derived);
            return samples;
        }

        Assert.Equal(Run(derived, ticks), Run(derived, ticks));
        Assert.Equal(3_330, Run(derived, ticks)[^1]);   // 333‰ × 10,000 / 1000
    }

    // ---------------------------------------------------------------- zero, carry, and the rails

    /// <summary>A zero rate accrues nothing and never moves the pool — the shipped configuration, and
    /// the reason battle stays byte-identical.</summary>
    [Fact]
    public void AZeroRateAccruesNothingAndNeverTouchesThePool()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0);
        Assert.Equal(0, ResourceChannelReader.RegenPerMilleTick(derived, "poise"));

        var state = new ResourcePoolState(Stored: 400, LastTick: 0);
        foreach (var t in new long[] { 1, 300, 10_000, 1_000_000 })
            Assert.Equal(400, state.Resolve(t, 0, max: 1000));

        var settled = state.Settle(1_000_000, 0, max: 1000);
        Assert.Equal(400, settled.Stored);
        Assert.Equal(0, settled.Carry);
    }

    /// <summary>Reading is pure: resolving a pool does not consume or reset the carry, so a read
    /// taken mid-fraction cannot silently cost the actor the fraction it had already earned.</summary>
    [Fact]
    public void ReadingAPoolDoesNotResetTheCarry()
    {
        const long rate = 300;
        var atThree = new ResourcePoolState(Stored: 0, LastTick: 0).Settle(3, rate, max: 1000);
        Assert.Equal(0, atThree.Stored);
        Assert.Equal(900, atThree.Carry);   // 900‰ earned but not yet whole

        // Ten reads of the same tick must not each eat the carry.
        for (var i = 0; i < 10; i++)
            Assert.Equal(0, atThree.Resolve(3, rate, max: 1000));

        Assert.Equal(900, atThree.Carry);
        // 900 carried + 100 from the next tick = exactly one whole unit at tick 4, not at tick 7.
        Assert.Equal(1, atThree.Resolve(4, rate, max: 1000));
    }

    /// <summary>The carry survives a settle, so incremental settling and one-shot settling agree —
    /// the same claim the drift test makes, stated at the struct level where the carry is visible.</summary>
    [Fact]
    public void TheCarrySurvivesSettlingAndIsNeverRounded()
    {
        const long rate = 300;
        var s = new ResourcePoolState(Stored: 0, LastTick: 0);
        for (long t = 1; t <= 10; t++) s = s.Settle(t, rate, max: 1000);

        Assert.Equal(3, s.Stored);
        Assert.Equal(0, s.Carry);
        Assert.Equal(new ResourcePoolState(Stored: 0, LastTick: 0).Settle(10, rate, max: 1000).Stored, s.Stored);
    }

    /// <summary>A full pool discards the overflow AND the carry: it must not bank a windfall it would
    /// hand back the instant something spends from it.</summary>
    [Fact]
    public void AFullPoolDiscardsTheOverflowAndTheCarry()
    {
        const long max = 10;
        const long rate = 900;   // 0.9 units/tick

        var full = new ResourcePoolState(Stored: max, LastTick: 0);
        var after100 = full.Settle(100, rate, max);     // would have accrued 90 units
        Assert.Equal(max, after100.Stored);
        Assert.Equal(0, after100.Carry);                 // the windfall is gone, not banked

        // Same claim through the pool API, which is where a windfall would actually be spendable.
        var derived = PoiseSnapshot(max, regenPerTick: 0.9);
        var pools = ActorResourcePools.FromStored(Empty(poise: max), atTick: 0);
        Assert.True(pools.TrySpend("poise", max, 100, derived));   // full for 100 ticks, then drained
        Assert.Equal(0, pools.Resolve("poise", 100, derived));
        Assert.Equal(0, pools.Resolve("poise", 101, derived));     // 900‰ — not a banked 90
        Assert.Equal(1, pools.Resolve("poise", 102, derived));     // 1800‰ — one unit, honestly earned
    }

    /// <summary>The floor rail mirrors the ceiling: a drained pool banks no negative carry either, so
    /// a debuff that ran a pool to 0 does not leave a debt the next positive rate has to repay.</summary>
    [Fact]
    public void ADrainedPoolDiscardsTheNegativeCarryToo()
    {
        var drained = new ResourcePoolState(Stored: 5, LastTick: 0).Settle(100, -900, max: 1000);
        Assert.Equal(0, drained.Stored);
        Assert.Equal(0, drained.Carry);
    }

    /// <summary>A negative rate floors rather than truncating toward zero, so the carry stays inside
    /// [0, 1000) and the remainder never drifts in the direction the pool is not moving.</summary>
    [Fact]
    public void ANegativeRateFloorsAndKeepsTheCarryInRange()
    {
        var s = new ResourcePoolState(Stored: 100, LastTick: 0).Settle(1, -300, max: 1000);
        Assert.Equal(99, s.Stored);      // -300‰ => floor(-0.3) == -1 whole
        Assert.Equal(700, s.Carry);      // remainder carried forward, in [0, 1000)

        // And it is drift-free in that direction too: 10 ticks of -300‰ is exactly -3.
        var t = new ResourcePoolState(Stored: 100, LastTick: 0);
        for (long i = 1; i <= 10; i++) t = t.Settle(i, -300, max: 1000);
        Assert.Equal(97, t.Stored);
        Assert.Equal(0, t.Carry);
    }

    // ---------------------------------------------------------------- scope boundary

    /// <summary>
    /// The scope boundary, asserted rather than promised: this module (resource-subtick) makes rates
    /// expressible and authors none. Battle stays byte-identical in THIS ASSEMBLY's own ambient
    /// fixture, which still carries an explicit all-zero regen share for every resource id (T11
    /// authored a real one only in the shipped `battle-resources.v2.json`, not in this test bootstrap)
    /// — proven by the shipped compose still reading 0 per-mille and a pool still sitting still across
    /// a whole battle's worth of ticks. (`BattleGoldenTests` covers the trace-level identity.)
    /// </summary>
    [Fact]
    public void BattleStaysByteIdenticalUnderThisAssemblysAllZeroRegenFixture()
    {
        foreach (var theta in new[] { 1, 5, 20, 100, 1000 })
            foreach (var id in DerivedStatChannels.ResourceIds)
                Assert.Equal(0, BattleRuleset.BaseResourceRegen(theta, id));

        var snap = BattleHubCompose.Compose(new BattleActorSetup
        {
            Key = "squad:0",
            Side = "squad",
            SpeciesId = "test-species",
            TypeId = 10_001,
            Level = 20,
            MaxHp = BattleRuleset.BaseHp(20),
            Atk = BattleRuleset.BaseAtk(20),
            Defense = BattleRuleset.BaseDefense(20),
            ChannelMods = Array.Empty<BattleChannelMod>()
        });

        var pools = ActorResourcePools.CreateFull(snap, atTick: 0);
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.Equal(0, ResourceChannelReader.RegenPerMilleTick(snap, id));

            var atStart = pools.Resolve(id, 0, snap);
            Assert.Equal(atStart, pools.Resolve(id, 300, snap));      // one round
            Assert.Equal(atStart, pools.Resolve(id, 100_000, snap));  // an implausibly long battle
        }
    }

    static Dictionary<string, long> Empty(long poise = 0) =>
        new() { ["hp"] = 0, ["stamina"] = 0, ["hunger"] = 0, ["spirit"] = 0, ["qi"] = 0, ["poise"] = poise };
}
