using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// D1/D14 (`solid-remediation` T2.4) — an effect-driven packet that carries no authored element picks
/// up the **acting actor's own** element, so the resolver has a matrix to resolve against.
///
/// <para><b>Why this had to be proven separately.</b> Wiring <c>CombatMath</c> onto battle's bag moved
/// 0 of 13,943 tests, and wiring it with <c>ActorResolve</c> moved 0 of 14,026. Neither number is
/// evidence the fix works — both say nothing in the suite drove the path. These tests drive it.</para>
///
/// <para><b>Authored content still wins.</b> <c>EffectOverlayMerge.TryMerge</c> seeds the merged
/// overlay from the action's own params, so a def that declares its own <c>elementPayload</c> keeps it
/// — the fallback runs after the merge and only fills an EMPTY payload, matching
/// <c>AtomCompiler</c>'s own <c>!overlay.ContainsKey("elementPayload")</c> rule. The sibling file
/// <c>EffectBagMergedElementPayloadTests</c> covers the authored case; this one covers its absence.</para>
/// </summary>
[Trait("VerificationId", "core.battle-effect-math")]
public class OwnerElementFallbackTests
{
    /// <summary>A damage def with NO authored elementPayload — the case the fallback exists for.</summary>
    static EffectDef UntypedDamageDef() => new()
    {
        EffectId = "test.untyped_damage",
        EffectType = EffectTypes.Triggered,
        Name = "untyped damage",
        Enabled = true,
        SourceTag = "test",
        Triggers = new List<string> { EffectTriggers.OnDamageDealt },
        Actions = new List<EffectActionRow>
        {
            new()
            {
                Seq = 1,
                Action = EffectActions.ApplyResourceDelta,
                Params = new Dictionary<string, object?> { ["channel"] = "hp" }
            }
        }
    };

    /// <summary>Fires one untyped damage effect from P1 at Z1, with P1's element as given.</summary>
    static (IReadOnlyList<OverlayCombatBreakdown> Breakdowns, IntentPlanDto Plan) Fire(ActorElementTypes attackerElement)
    {
        var catalog = EffectSeedCatalog.CreateAll().Append(UntypedDamageDef()).ToList();
        var h = new FoundationHarness().WithOverlayCombatMath(combatSeed: 1).WithCatalog(catalog);

        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });

        // Accuracy high and crit suppressed so the outcome is dominated by the element path rather
        // than by a hit or crit roll — the same shaping the sibling authored-payload test uses.
        h.PinDerived("P1", ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyOmni, 500),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateOmni, -500)
        }));
        h.PinElementTypes("P1", attackerElement);
        h.PinElementTypes("Z1", ActorElementTypes.Create(ElementTypeId.Ice));

        h.Grant(new EffectGrantDto
        {
            GrantId = "owner-element-fallback",
            EffectId = "test.untyped_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["amount"] = -100L,
                ["icd_ms"] = 0,
                ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
            }
        });

        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant"
        });

        return (h.CombatBreakdowns, plan);
    }

    static long AppliedAmount(IntentPlanDto plan) =>
        Convert.ToInt64(Assert.Single(plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta).Params["amount"]);

    /// <summary>
    /// <b>The falsifier.</b> A fire attacker and a neutral one, firing the same untyped effect at the
    /// same ice defender. Before the fallback both applied the identical authored number, because an
    /// empty payload makes <c>OverlayCombatMath.Finalize</c> return the amount unchanged and nothing on
    /// this path ever filled the payload. The attacker's element now reaches the packet, so the matchup
    /// resolves and the two diverge.
    ///
    /// <para>Compared on the APPLIED amount, not on the breakdown: a neutral attacker correctly
    /// produces no breakdown at all, because `Finalize` returns before computing one. That asymmetry is
    /// the behaviour under test, so it cannot also be the measurement.</para>
    /// </summary>
    [Fact]
    public void An_elemental_attacker_no_longer_lands_the_same_number_as_a_neutral_one()
    {
        var neutral = Fire(ActorElementTypes.Neutral);
        var fire = Fire(ActorElementTypes.Create(ElementTypeId.Fire));

        Assert.NotEqual(AppliedAmount(neutral.Plan), AppliedAmount(fire.Plan));

        // And the resolver is what did it: the typed hit produced a breakdown, the untyped one did not.
        Assert.Empty(neutral.Breakdowns);
        Assert.Single(fire.Breakdowns);
    }

    /// <summary>
    /// The matchup is what moved it, not chance: fire into ice is a STRONG relation on the shipped
    /// ring, so the typed hit must land harder than the untyped one. Stated as a direction rather than
    /// a magnitude — <c>ElementMatchupPolicy.MatchupShareK</c> is a tunable and this must survive a
    /// balance pass.
    /// </summary>
    [Fact]
    public void The_matchup_resolves_in_the_direction_the_ring_declares()
    {
        Assert.Equal(
            Core.Combat.Element.ElementMatchupRelation.Strong,
            Core.Combat.Element.ElementRingMatrix.GetRelation(ElementTypeId.Fire, ElementTypeId.Ice));

        var neutral = AppliedAmount(Fire(ActorElementTypes.Neutral).Plan);
        var fire = AppliedAmount(Fire(ActorElementTypes.Create(ElementTypeId.Fire)).Plan);

        // Damage is negative, so "harder" is more negative.
        Assert.True(fire < neutral,
            $"fire into ice is STRONG on the ring but landed no harder than neutral: {fire} vs {neutral}");
    }

    /// <summary>
    /// A neutral attacker is exactly where it was — no element, no payload, amount unchanged. This is
    /// why the fallback moved no existing golden, asserted rather than argued.
    /// </summary>
    [Fact]
    public void A_neutral_attacker_still_lands_its_authored_amount()
    {
        var neutral = Fire(ActorElementTypes.Neutral);

        var fa10 = Assert.Single(neutral.Plan.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-100L, Convert.ToInt64(fa10.Params["amount"]));
    }
}
