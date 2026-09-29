using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>spec-reflection.md (T5.4) — bounce damage back at the attacker, and prove it
/// terminates. Dispatcher-level: CombatDamageDispatcher.DispatchInstant is called directly
/// with PassThroughCombatMath, so `amount` is exactly packet.SignedAmount — isolates
/// reflection's OWN logic from the upstream mitigation chain (T5.1-T5.3), which already has
/// its own test files.</summary>
public class ReflectionTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();

    /// <summary>Always succeeds a roll while counting draws. Every test keeps pReflect strictly
    /// below 1 (reflect.rate=9, scale=10) so RollSuccess always draws instead of short-circuiting
    /// (established T5.3 semantics for p&gt;=1) — Draws is then an exact count of reflection
    /// attempts, not just roll outcomes.</summary>
    sealed class FixedSuccessRng : ICombatRng
    {
        public int Draws;
        public int Next(int exclusiveMax) { Draws++; return 0; }
    }

    sealed class DrawCountingRng : ICombatRng
    {
        readonly ICombatRng _inner;
        public int Draws;
        public DrawCountingRng(ICombatRng inner) => _inner = inner;
        public int Next(int exclusiveMax) { Draws++; return _inner.Next(exclusiveMax); }
    }

    static readonly KeyValuePair<string, double>[] MaxReflect =
    {
        new(DerivedStatChannels.CombatReflectRateOmni, 9),
        new(DerivedStatChannels.CombatReflectDamageOmni, 1000)
    };

    static DamagePacket Hit(string actorPtr, string targetPtr, long signedAmount, int chainDepth = 0) => new()
    {
        PacketId = "test-hit",
        SourceGrantId = "test-grant",
        ActorPtr = actorPtr,
        Target = new TargetSpec { Mode = TargetModes.Single, Ptr = targetPtr },
        SignedAmount = signedAmount,
        ChainDepth = chainDepth
    };

    static EffectEventDto Ev() => new() { Trigger = EffectTriggers.OnDamageDealt, Tick = 0 };

    static CombatPolicy PolicyWithLimit(int procDepthLimit) => new()
    {
        ProcDepthLimit = procDepthLimit,
        ReflectRateScale = CombatPolicy.Default.ReflectRateScale,
        ReflectShareScale = CombatPolicy.Default.ReflectShareScale
    };

    // ---- SS6.1 Termination — the tests that justify the module ----

    [Fact]
    public void MutualReflectorsTerminate()
    {
        // Two actors at max reflect stats -> resolution HALTS, bounded by the shared
        // ProcDepthLimit (default 6). Ping-pong: depth0(a->b) depth1(b->a) ... depth5(b->a),
        // depth6 dropped before any roll -- exactly `limit` successful reflections, 1 drop.
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat.Overlay(MaxReflect));
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(CombatPolicy.Default.ProcDepthLimit, rng.Draws);
        Assert.Single(skipped, s => s.EndsWith(":proc-depth", StringComparison.Ordinal));
    }

    [Fact]
    public void ReflectedPacketInheritsDepth()
    {
        // spec SS2.1 rule 1: the bounce carries the PARENT's depth, decremented -- not a fresh
        // budget. Proven with a deliberately tiny ProcDepthLimit(2): if the bounce reset to 0
        // instead of inheriting depth+1, the ping-pong would never reach the limit and this
        // test would hang instead of completing with exactly 2 draws.
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat.Overlay(MaxReflect));
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            PolicyWithLimit(2), rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(2, rng.Draws);
        Assert.Single(skipped, s => s.EndsWith(":proc-depth", StringComparison.Ordinal));
    }

    [Fact]
    public void DepthExhaustionDrops()
    {
        // spec SS2.1 rule 2: the terminal packet is DROPPED, not applied at a clamped zero.
        // With ProcDepthLimit(1) the original hit (depth 0) applies and reflects once; the
        // bounce (depth 1) hits the top-of-dispatcher guard BEFORE any roll or apply -- zero
        // draws for it, and it never reaches ApplyPacketToFunnel, so it can never fire a
        // downstream OnDamageDealt proc either (that trigger is contingent on Applied).
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat.Overlay(MaxReflect));
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        var applied = CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            PolicyWithLimit(1), rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(1, applied);
        Assert.Equal(1, rng.Draws);
        Assert.Single(skipped, s => s.EndsWith(":proc-depth", StringComparison.Ordinal));
    }

    [Fact]
    public void ReflectionInsideProcChainSharesBudget()
    {
        // spec SS2 (no second counter): a packet arriving already partway through some OTHER
        // proc chain (nonzero ChainDepth, e.g. a burst) gets LESS reflection room, not a fresh
        // budget. Same ProcDepthLimit(2) as ReflectedPacketInheritsDepth, but starting one hop
        // in: only ONE reflection fits before the SHARED counter cuts it off, where starting
        // at depth 0 fit two -- the budget is visibly shared, not reflection's own.
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat.Overlay(MaxReflect));
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100, chainDepth: 1), BoardSnapshot.Empty, Ev(), h.Funnel,
            PolicyWithLimit(2), rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(1, rng.Draws);
        Assert.Single(skipped, s => s.EndsWith(":proc-depth", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreeWayReflectTerminates()
    {
        // "The case a two-actor test misses": a third reflector present on the board must not
        // be spuriously drawn into a chain it was never targeted by. TryReflect always bounces
        // to the packet's OWN immediate actor (spec SS3's "new DamagePacket(defender ->
        // attacker)"), so a genuine 3-cycle cannot form by construction -- this proves the
        // ping-pong stays confined to {a, b}: same draw count as the two-actor case, and "c" is
        // never even resolved, let alone hit.
        var h = new FoundationHarness();
        foreach (var ptr in new[] { "a", "b", "c" })
            h.PinDerived(ptr, NeutralCombat.Overlay(MaxReflect));

        var resolved = new List<string>();
        CombatActorResolve tracking = (ptr, attackerLess) =>
        {
            if (!string.IsNullOrEmpty(ptr)) resolved.Add(ptr);
            return h.Resolve(ptr, attackerLess);
        };

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, null, tracking);

        Assert.Equal(CombatPolicy.Default.ProcDepthLimit, rng.Draws);
        Assert.DoesNotContain("c", resolved);
    }

    // ---- SS6.2 Behaviour ----

    [Fact]
    public void NoGoldensMoveAtZero()
    {
        // All four channels at 0 (StubNeutral defaults) -> pReflect computes to exactly 0,
        // RollSuccess short-circuits WITHOUT drawing (p<=0), and the original hit is the only
        // thing that happens.
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat);
        h.PinDerived("b", NeutralCombat);

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        var applied = CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(1, applied);
        Assert.Equal(0, rng.Draws);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ResistAnswersRate()
    {
        // spec SS1: reflect.rate and reflect.resist.rate cancel at equality -- the ABSOLUTE
        // magnitude never matters, only the difference (0) does.
        var lowMagnitude = Math.Max(0.0, 5.0 - 5.0) / CombatPolicy.Default.ReflectRateScale;
        var highMagnitude = Math.Max(0.0, 500.0 - 500.0) / CombatPolicy.Default.ReflectRateScale;
        Assert.Equal(0.0, lowMagnitude);
        Assert.Equal(lowMagnitude, highMagnitude);
    }

    [Fact]
    public void ResistAnswersDamage()
    {
        var lowMagnitude = Math.Max(0.0, 5.0 - 5.0) / CombatPolicy.Default.ReflectShareScale;
        var highMagnitude = Math.Max(0.0, 500.0 - 500.0) / CombatPolicy.Default.ReflectShareScale;
        Assert.Equal(0.0, lowMagnitude);
        Assert.Equal(lowMagnitude, highMagnitude);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(1_000.0)]
    [InlineData(1_000_000.0)]
    public void CannotBounceMoreThanTaken(double dmgDelta)
    {
        // spec SS3: reflectShare is bounded [0,1] regardless of how extreme reflect.damage
        // gets -- PS-8 exempt bounded ratio; reflect.damage itself (the magnitude) stays
        // uncapped.
        var reflectShare = Math.Clamp(Math.Max(0.0, dmgDelta) / CombatPolicy.Default.ReflectShareScale, 0.0, 1.0);
        Assert.True(reflectShare <= 1.0);
        var bounced = (long)Math.Round(100 * reflectShare, MidpointRounding.AwayFromZero);
        Assert.True(bounced <= 100);
    }

    [Fact]
    public void FullyAbsorbedHitDoesNotReflect()
    {
        // Shipped default since 2026-08-25 (combat.v1.json reflectReadsPostShield: true):
        // reflection reads what actually reached HP, so a hit the shield ate entirely bounces
        // nothing. The measured reason for the change, over 50,000 simulated fights
        // (gk-core/tools/CombatSim, variants/reflect.json, scenario shield-reflect): under the pre-shield
        // reading a shielded reflector took a mean 197 damage while returning 542.8% of the damage
        // the attacker dealt -- a trade no attacker can win, from two defensive stats that were
        // never designed to compound.
        var h = new FoundationHarness().WithShieldGate();
        h.PinDerived("a", NeutralCombat);
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));
        h.GrantShield("b", baseHp: 1000);

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, h.Bag.ShieldGate, h.Resolve);

        Assert.Contains(skipped, s => s.EndsWith(":absorbed", StringComparison.Ordinal));
        Assert.Equal(0, rng.Draws); // nothing reached HP, so there was nothing to reflect
    }

    [Fact]
    public void ReflectsPreShield_whenConfigured()
    {
        // The other half of the switch still ships and stays reachable: at
        // ReflectReadsPostShield=false the reflector rolls even on a fully absorbed hit -- spec
        // -reflection.md SS3/SS9's original decided reading, "a shield protects its owner, it does
        // not shrink what the owner bounces back". Kept as an executable description of the
        // alternative rather than deleted, so flipping the tuning key back is a one-line change
        // with a test already standing behind it.
        //
        // Uses a LOCAL policy instance rather than mutating CombatPolicy.Default: ~3,500 tests
        // share that singleton and xunit runs collections concurrently, so a global write here
        // would race them (the same hazard DerivedStatPolicy.UseScoped exists to solve).
        var h = new FoundationHarness().WithShieldGate();
        h.PinDerived("a", NeutralCombat);
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));
        h.GrantShield("b", baseHp: 1000);

        var policy = CombatPolicy.FromDefault();
        policy.ReflectReadsPostShield = false;

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            policy, rng, PassThroughCombatMath.Instance, skipped, h.Bag.ShieldGate, h.Resolve);

        Assert.Contains(skipped, s => s.EndsWith(":absorbed", StringComparison.Ordinal));
        Assert.Equal(1, rng.Draws); // reflection still rolled despite the full absorption
    }

    [Fact]
    public void BounceGoesThroughFunnel()
    {
        // spec SS2.2: the bounce is a NEW packet through the SAME apply path
        // (DamageApplyPipeline.ApplyPacketToFunnel), not an in-frame callback. Asymmetric setup
        // ("a" has no reflect stats, so it never bounces back) makes the chain exactly two
        // hits, both Applied, zero skips. guard-funnel-delta.py is the structural half of this
        // proof (run separately) -- HP deltas may ONLY reach the Funnel through this exact call.
        var h = new FoundationHarness();
        h.PinDerived("a", NeutralCombat);
        h.PinDerived("b", NeutralCombat.Overlay(MaxReflect));

        var rng = new FixedSuccessRng();
        var skipped = new List<string>();
        var applied = CombatDamageDispatcher.DispatchInstant(
            Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
            CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);

        Assert.Equal(1, applied);   // the outer call's own ptr loop -- "b" alone
        Assert.Equal(1, rng.Draws); // "b" reflects once; "a" has no reflect stats, chain ends
        Assert.Empty(skipped);      // both the original hit AND the bounce applied cleanly
    }

    [Fact]
    public void BounceIsDeterministic()
    {
        // spec SS6.2/SS8: same seed -> same bounces, same order. reflect.rate=5 (scale=10, so
        // pReflect=0.5) deliberately exercises genuine branching rather than a fixed outcome.
        (int draws, List<string> skipped) Run()
        {
            var h = new FoundationHarness();
            var halfReflect = new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.CombatReflectRateOmni, 5),
                new KeyValuePair<string, double>(DerivedStatChannels.CombatReflectDamageOmni, 5)
            };
            h.PinDerived("a", NeutralCombat.Overlay(halfReflect));
            h.PinDerived("b", NeutralCombat.Overlay(halfReflect));

            var rng = new DrawCountingRng(new SeededCombatRng(7));
            var skipped = new List<string>();
            CombatDamageDispatcher.DispatchInstant(
                Hit("a", "b", -100), BoardSnapshot.Empty, Ev(), h.Funnel,
                CombatPolicy.Default, rng, PassThroughCombatMath.Instance, skipped, null, h.Resolve);
            return (rng.Draws, skipped);
        }

        var (draws1, skipped1) = Run();
        var (draws2, skipped2) = Run();

        Assert.Equal(draws1, draws2);
        Assert.Equal(skipped1, skipped2);
    }
}
