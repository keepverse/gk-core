using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// aura-skill T20: `CombatDamageDispatcher.DispatchInstant`'s reflect branch has always required
/// `actorResolve != null`, but every production call site (`EffectBag.cs`, `StatusEffectBridge.cs`,
/// `CheatCommandRunner.cs`) omitted the argument — only <see cref="ReflectionTests"/>'s own
/// dispatcher-level tests ever passed it, by calling `DispatchInstant` directly. Reflect therefore
/// shipped as dead code outside the offline harness: correct math, zero production trigger.
///
/// <para>Unlike <see cref="ReflectionTests"/> (which calls `DispatchInstant` directly to isolate
/// reflect's own logic), this drives it through <see cref="FoundationHarness.Grant"/> +
/// <see cref="FoundationHarness.OnEvent"/> — <c>EffectBag</c>'s OWN internal dispatch path, the exact
/// path every real grant in production takes. That is what proves the FIX, not just the math.</para>
/// </summary>
public class ReflectActorResolveWiringTests
{
    /// <summary>Always succeeds a reflect roll — mirrors <c>ReflectionTests.FixedSuccessRng</c>
    /// (private there, so redefined here rather than shared).</summary>
    sealed class FixedSuccessRng : ICombatRng
    {
        public int Next(int exclusiveMax) => 0;
    }

    static readonly KeyValuePair<string, double>[] MaxReflect =
    {
        new(DerivedStatChannels.CombatReflectRateOmni, 9),
        new(DerivedStatChannels.CombatReflectDamageOmni, 1000)
    };

    static FoundationHarness BuildHarness()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
            new BoardEntitySnap { Ptr = "p1", Side = "plant", TypeId = 0, Col = 2, Row = 2 }
        });
        // p1 is the one taking the hit -- TryReflect reads the HIT actor's reflect stats, so p1
        // (not the attacker z1) needs them maxed for a bounce to be possible at all.
        h.PinDerived("z1", ActorDerivedSnapshot.StubNeutral());
        h.PinDerived("p1", ActorDerivedSnapshot.StubNeutral().Overlay(MaxReflect));
        h.Bag.CombatRng = new FixedSuccessRng();
        h.Grant(new EffectGrantDto
        {
            GrantId = "reflect-wiring-hit",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });
        return h;
    }

    static EffectEventDto Hit() => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = "z1",
        TargetPtr = "p1",
        Side = "zombie",
        TypeId = 0,
        TargetTypeId = 0
    };

    [Fact]
    public void Without_ActorResolve_wired_reflect_never_fires_the_pre_T20_defect()
    {
        var h = BuildHarness();
        // h.Bag.ActorResolve is left at its default (null) -- the state every production call site
        // was actually in before T20.

        var plan = h.OnEvent(Hit());

        var deltas = plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Single(deltas); // only the original hit on p1 -- no bounce back onto z1
        Assert.Equal("p1", deltas[0].Params["targetPtr"]);
    }

    [Fact]
    public void With_ActorResolve_wired_reflect_fires_through_EffectBags_own_dispatch()
    {
        var h = BuildHarness();
        h.Bag.ActorResolve = h.Resolve; // the T20 fix, applied the same way EffectRuntime.WireCombatMath does

        var plan = h.OnEvent(Hit());

        var deltas = plan.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta).ToList();
        Assert.Equal(2, deltas.Count); // the original hit AND the reflected bounce
        Assert.Contains(deltas, a => Equals(a.Params["targetPtr"], "p1"));
        Assert.Contains(deltas, a => Equals(a.Params["targetPtr"], "z1")); // bounced back at the attacker
    }
}
