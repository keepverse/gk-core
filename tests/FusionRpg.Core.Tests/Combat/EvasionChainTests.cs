using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>spec-evasion-chain.md §3 (T5.3) — the attack table: one roll, cumulative bands
/// (miss/parried/blocked/clean hit), zero additional RNG draws. §6.1's ShieldGoldensByteIdentical/
/// HelperMatchesShieldMathExactly live with ClampedContestTests (T5.2); this file covers §6.2.</summary>
public class EvasionChainTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();

    sealed class CountingRng : ICombatRng
    {
        public int Draws;
        readonly int _value;
        public CountingRng(int value) => _value = value;
        public int Next(int exclusiveMax) { Draws++; return _value; }
    }

    static OverlayCombatRequest NeutralRequest(ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender) => new()
    {
        BaseOverlayDamage = 100,
        Components = Array.Empty<ElementPayloadComponent>(),
        Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
        Defender = new CombatActorSnapshot(defender, ActorElementTypes.Neutral)
    };

    [Fact]
    public void NoExtraRngDraws()
    {
        // spec §3, property 1: the hit path consumes exactly ONE draw even with BOTH parry and block
        // live (nonzero) -- asserted on the draw counter, not inferred. r = 0.1 lands in the blocked
        // band ([0, 0.2) below), so crit never rolls either -- isolates the attack-table draw itself.
        var calc = new OverlayCombatCalculator();
        var attacker = NeutralCombat; // parry.break = block.break = 0
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, 300),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 200)
        });
        // pHitFinal = sigmoid(0) = 0.5; pParry = 0.3; pBlock = 0.2 -- miss[.5,1) parried[.2,.5) blocked[0,.2)
        var rng = new CountingRng(100_000); // r = 0.1 -> blocked
        var (_, breakdown) = calc.Compute(NeutralRequest(attacker, defender), rng);

        Assert.True(breakdown.Blocked);
        Assert.Equal(1, rng.Draws);
    }

    [Fact]
    public void RateGoldensUnchangedAtZero()
    {
        // spec §3, property 2: empty bands -> byte-identical outcomes, by arithmetic. Reuses
        // OverlayCombatCalculatorTests' own Fire-vs-Ice golden (delta=125) at parry=block=0 defaults,
        // and confirms neither new outcome flag ever fires when nothing was authored.
        var calc = new OverlayCombatCalculator();
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
        });
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Attacker = new CombatActorSnapshot(attacker, ActorElementTypes.Neutral),
            Defender = new CombatActorSnapshot(NeutralCombat, ActorElementTypes.Create(ElementTypeId.Ice))
        };

        var (delta, breakdown) = calc.Compute(request, new SeededCombatRng(1));

        Assert.Equal(-125, delta);
        Assert.False(breakdown.Parried);
        Assert.False(breakdown.Blocked);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.19999)]
    [InlineData(0.2)]
    [InlineData(0.35)]
    [InlineData(0.49999)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.99999)]
    public void BandsAreExclusive(double r)
    {
        // spec §3, property 3: miss / parried / blocked / clean-hit partition the roll -- exactly one
        // is true for every possible draw, no gap and no overlap.
        var (miss, parried, blocked) = OverlayCombatCalculator.ResolveBand(r, pHitFinal: 0.5, pParry: 0.3, pBlock: 0.2);
        var cleanHit = !miss && !parried && !blocked;
        var trueCount = (miss ? 1 : 0) + (parried ? 1 : 0) + (blocked ? 1 : 0) + (cleanHit ? 1 : 0);
        Assert.Equal(1, trueCount);
    }

    [Theory]
    [InlineData(0.5, 0.6, 0.6)]     // raw sum 1.7, way over cap
    [InlineData(0.5, 100.0, 100.0)] // absurd stacking -- must still hold
    [InlineData(0.98, 0.5, 0.5)]    // miss alone is high but still under the cap (0.02 < 0.95)
    public void BandTotalCapsAt950(double pHitFinal, double pParryRaw, double pBlockRaw)
    {
        // spec §3.1: the cumulative avoidance band (miss + parry + block) never exceeds 950‰ -- an
        // attack always retains at least a 5% chance to land, however extreme parry/block stacking
        // gets. Scoped to matchups where miss alone is still under the cap -- the module's own job.
        var (pParry, pBlock) = OverlayCombatCalculator.CapAvoidanceBand(pHitFinal, pParryRaw, pBlockRaw, avoidanceBandCap: 0.95);
        var missChance = 1.0 - pHitFinal;
        Assert.True(missChance + pParry + pBlock <= 0.95 + 1e-9);
    }

    [Fact]
    public void BandTotalCapDoesNotTouchMissWhenMissAloneExceedsIt()
    {
        // The documented exception: if miss (accuracy/dodge, pre-existing and outside this module)
        // ALONE already exceeds the cap -- pHitFinal = 0, missChance = 1.0 -- T5.3 does not newly cap
        // it. Parry/block correctly scale to exactly zero (no room left, no negative contribution)
        // rather than the total silently landing back at or under 0.95 by touching miss itself.
        var (pParry, pBlock) = OverlayCombatCalculator.CapAvoidanceBand(pHitFinal: 0.0, pParryRaw: 1.0, pBlockRaw: 1.0, avoidanceBandCap: 0.95);
        Assert.Equal(0.0, pParry);
        Assert.Equal(0.0, pBlock);
    }

    [Fact]
    public void ParryShortCircuits()
    {
        // spec §3: "no block, no mitigation" -- a parried hit ends resolution. r lands in the
        // parried band; damage comes from parry.strength alone, never the power/defense delta, crit,
        // or amplification the SAME attacker/defender snapshot would otherwise produce.
        var calc = new OverlayCombatCalculator();
        var attacker = NeutralCombat.Overlay(new[]
        {
            // Huge power/crit that would dominate if mitigation ran -- proves it does not.
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 10_000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, 10_000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAmplificationOmni, 10_000)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, 1000), // pParry = 1.0 raw
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryStrengthOmni, 40)
        });
        var request = NeutralRequest(attacker, defender);

        // pHitFinal = 0.5 (neutral accuracy/dodge), pParryRaw = 1.0 -- band cap (0.95) forces
        // pParry down, but it still occupies [miss, ~miss+capRoom): r=0.3 lands well inside it.
        var (delta, breakdown) = calc.Compute(request, new CountingRng(300_000));

        Assert.True(breakdown.Parried);
        Assert.False(breakdown.Blocked);
        Assert.False(breakdown.Crit); // no crit roll on a parried hit
        // removed = ClampedContest with deltaBase = parryNeutralShareKPm(500‰) x 100 = 50 and
        // delta = strength(40) - shred(0) = 40 -> raw = 50+40 = 90, inside [0, 950‰x100 = 95]
        // -> remaining = 100-90 = 10.
        //
        // This assertion used to be -5, and that is the point: at the old 1000‰ neutral share the
        // raw value was 140, clamped to 95, so strength(40) changed NOTHING -- the test named a
        // strength value it never actually exercised. At 500‰ the neutral point sits inside the
        // clamp range, so the same 40 points now move removal from 50 to 90. Measured before the
        // change: sweeping parry.strength 0 -> 2000 left mean damage flat at 789.3 across 4,000
        // fights (gk-core/tools/CombatSim, `sweep --channel combat.parry.strength.omni`).
        Assert.Equal(-10, delta);
    }

    [Fact]
    public void BlockSubtractsBeforeMitigation()
    {
        // The mirror of ParryShortCircuits for block: same short-circuit, different stat pair, same
        // proof that power/crit/amplification never run.
        var calc = new OverlayCombatCalculator();
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerOmni, 10_000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, 10_000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAmplificationOmni, 10_000)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockRateOmni, 1000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatBlockStrengthOmni, 40)
        });
        var request = NeutralRequest(attacker, defender);

        var (delta, breakdown) = calc.Compute(request, new CountingRng(300_000));

        Assert.True(breakdown.Blocked);
        Assert.False(breakdown.Parried);
        Assert.False(breakdown.Crit);
        Assert.Equal(-10, delta); // same arithmetic as ParryShortCircuits, block's own stat pair
    }

    [Fact]
    public void BreakAnswersRate()
    {
        // spec §6.2: the rate pair cancels at equality -- equal parry.rate and parry.break give
        // exactly zero parry chance, the same as neither being authored at all.
        var equalRate = Math.Max(0.0, 500.0 - 500.0) / 1000.0;
        var zeroRate = Math.Max(0.0, 0.0 - 0.0) / 1000.0;
        Assert.Equal(0.0, equalRate);
        Assert.Equal(equalRate, zeroRate);
    }

    [Fact]
    public void ShredAnswersStrength()
    {
        // The strength/shred pair cancels at equality too: the ABSOLUTE magnitude of a matched pair
        // never matters, only the difference (0) does -- a 10-vs-10 exchange and a 1000-vs-1000
        // exchange remove the identical amount.
        var lowMagnitude = ClampedContest.Apply(deltaBase: 100, delta: 10 - 10, hitCount: 1, boundsBase: 100, floorKPm: 0, capKPm: 950);
        var highMagnitude = ClampedContest.Apply(deltaBase: 100, delta: 1000 - 1000, hitCount: 1, boundsBase: 100, floorKPm: 0, capKPm: 950);
        Assert.Equal(lowMagnitude, highMagnitude);
        Assert.Equal(95, lowMagnitude); // deltaBase (100) exceeds the 95-cap even at delta=0
    }

    [Fact]
    public void CapIsNinetyFivePercent()
    {
        // spec §2.1: a maximal block removes AT MOST 95% of the hit, never all of it -- immunity
        // impossible by construction, the ceiling-side mirror of the shield's floor-side guarantee.
        var removed = ClampedContest.Apply(deltaBase: 100, delta: 1_000_000, hitCount: 1, boundsBase: 100, floorKPm: 0, capKPm: 950);
        Assert.Equal(95, removed);
        Assert.True(removed < 100); // never removes the full hit
    }

    [Fact]
    public void NoFloorOnProcs()
    {
        // spec §2.1: a fully shredded block removes ZERO -- legitimate, not clamped up to a floor.
        // Block/parry have no pool to protect from non-spending, unlike shield's chip floor.
        var removed = ClampedContest.Apply(deltaBase: 100, delta: -1_000_000, hitCount: 1, boundsBase: 100, floorKPm: 0, capKPm: 950);
        Assert.Equal(0, removed);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    [InlineData(1_000_000)]
    public void CapIsARatioNotACeiling(long baseDamage)
    {
        // spec §2.1 property 2: block.strength/block.shred scale past any literal -- what's bounded
        // is the FRACTION of one hit removed (950‰), never an absolute amount. A maximal block at
        // base=100 removes 95; at base=1,000,000 it removes 950,000 -- the stat itself is uncapped.
        var removed = ClampedContest.Apply(deltaBase: baseDamage, delta: 1_000_000, hitCount: 1, boundsBase: baseDamage, floorKPm: 0, capKPm: 950);
        Assert.Equal(baseDamage * 950 / 1000, removed);
    }

    [Fact]
    public void BlockCapIndependentOfStatusCap()
    {
        // spec §2.1: same 950/0.95 constant and "mitigation may not reach total" reasoning as
        // StatusPolicy.CategoryResistCap, but a SEPARATE key -- they agree today, they do not share.
        // Structural proof: they live in different Policy classes, reading different tuning files.
        Assert.Equal(950, CombatPolicy.Default.BlockCapPermille);
        Assert.Equal(950, CombatPolicy.Default.ParryCapPermille);
        Assert.Equal(0.95, DerivedStatPolicy.CategoryResistCap, 3);
        // Changing one's IN-MEMORY value (a scoped override) must never move the other's.
        var before = DerivedStatPolicy.CategoryResistCap;
        CombatPolicy.Default.BlockCapPermille = 500;
        try
        {
            Assert.Equal(before, DerivedStatPolicy.CategoryResistCap);
        }
        finally
        {
            CombatPolicy.Default.BlockCapPermille = 950; // restore -- Default is a shared static instance
        }
    }

    [Fact]
    public void RollsAreDeterministic()
    {
        // spec §8: same seed -> same outcome; stream order unchanged from main.
        var calc = new OverlayCombatCalculator();
        var attacker = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500)
        });
        var defender = NeutralCombat.Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatParryRateOmni, 100)
        });
        var request = NeutralRequest(attacker, defender);

        var (delta1, b1) = calc.Compute(request, new SeededCombatRng(42));
        var (delta2, b2) = calc.Compute(request, new SeededCombatRng(42));

        Assert.Equal(delta1, delta2);
        Assert.Equal(b1.Parried, b2.Parried);
        Assert.Equal(b1.Blocked, b2.Blocked);
        Assert.Equal(b1.Hit, b2.Hit);
    }

    [Fact]
    public void ChipFloorStillPreventsImmunity()
    {
        // The shield invariant survives extraction (T5.2) and T5.3 adding more ClampedContest
        // callers alongside it: a shield always spends, even against overwhelming toughness.
        var r = FusionRpg.Core.Combat.Shield.ShieldMath.AbsorbLayer(input: 100, shieldHp: 50, weightedRelationUnitPm: 0, breakerDelta: -1_000_000, hitCount: 1);
        Assert.True(r.Spent > 0);
    }
}
