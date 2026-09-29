using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Events;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>
/// End-to-end: ring → coalescer → drain → real EffectBag. Proves the drain's DTO conventions
/// (side flip, ptr hex, HitCount) actually drive grant matching and merged proc math — a
/// mismatch here would be invisible to the fake-processor drain tests until live.
/// </summary>
public class EventDrainIntegrationTests
{
    static FoundationHarness CounterHarness()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "B", Side = "zombie", TypeId = 0, Col = 7, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "streak",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["icd_ms"] = 0,
                ["delivery"] = new Dictionary<string, object?>
                {
                    ["mode"] = DeliveryModes.Counter,
                    ["everyHits"] = 5,
                    ["resetOnBurst"] = true,
                    ["counterScope"] = CounterScopes.Target
                },
                ["burst"] = new Dictionary<string, object?>
                {
                    ["amount"] = -500L,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                    ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant }
                }
            }
        });
        return h;
    }

    static GameEventRec Hit(EventDrain drain, long amount = -10) => new(
        GameEventKind.CombatHit, frame: 1, seq: drain.NextSeq(),
        actorPtr: new IntPtr(0xA), targetPtr: new IntPtr(0xB),
        typeId: 1, targetTypeId: 0, side: GameEventSide.Zombie,
        amount: amount, hitCount: 1, chainDepth: 0,
        sourceGrantIdx: -1, matchKeyIdx: -1, pairId: 0);

    [Fact]
    public void Merged_record_advances_counter_by_hit_count_and_bursts()
    {
        var h = CounterHarness();
        IntentPlanDto? last = null;
        var drain = new EventDrain(dto => last = h.OnEvent(dto));

        // Five same-key hits recorded in one frame → coalesce to one record, HitCount=5 →
        // the every-5 counter must burst on that single drained event.
        for (var i = 0; i < 5; i++)
            drain.Record(Hit(drain));

        var stats = drain.Drain(budgetTicks: long.MaxValue);
        Assert.Equal(1, stats.Processed);
        Assert.NotNull(last);
        var fa = Assert.Single(last!.Actions.Where(a => a.Action == EffectActions.ApplyResourceDelta));
        Assert.Equal(-500L, Convert.ToInt64(fa.Params["amount"]));
        Assert.Equal("b", fa.Params["targetPtr"]?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void Four_merged_hits_do_not_burst()
    {
        var h = CounterHarness();
        IntentPlanDto? last = null;
        var drain = new EventDrain(dto => last = h.OnEvent(dto));

        for (var i = 0; i < 4; i++)
            drain.Record(Hit(drain));
        drain.Drain(long.MaxValue);

        Assert.NotNull(last);
        Assert.DoesNotContain(last!.Actions, a => a.Action == EffectActions.ApplyResourceDelta);
    }

    [Fact]
    public void Session_mode_five_individual_events_burst_on_fifth()
    {
        var h = CounterHarness();
        var plans = new List<IntentPlanDto>();
        var drain = new EventDrain(dto => plans.Add(h.OnEvent(dto))) { SessionMode = true };

        for (var i = 0; i < 5; i++)
            drain.Record(Hit(drain));
        drain.Drain(0);

        Assert.Equal(5, plans.Count); // no coalescing in sessions — v1 fidelity
        Assert.DoesNotContain(plans[3].Actions, a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Contains(plans[4].Actions, a => a.Action == EffectActions.ApplyResourceDelta);
    }

    [Fact]
    public void Extreme_merged_amounts_preserve_the_exact_value()
    {
        // P0.4 (power overflow audit): EffectEventDto.Damage is long, so a merged amount past
        // int32 range is no longer clamped at this boundary — it is exact, the same way a single
        // RPG-scaled hit already was. This test used to assert the clamp itself
        // (`Assert.Equal(int.MinValue, seen.Damage)`); that assertion encoded the defect the
        // widening fixes, not a property worth keeping.
        var h = CounterHarness();
        EffectEventDto? seen = null;
        var drain = new EventDrain(dto => { seen = dto; h.OnEvent(dto); });

        drain.Record(Hit(drain, amount: -(long)int.MaxValue));
        drain.Record(Hit(drain, amount: -(long)int.MaxValue));
        drain.Drain(long.MaxValue);

        Assert.NotNull(seen);
        Assert.Equal(-2L * int.MaxValue, seen!.Damage); // exact merged sum, well inside long
        Assert.Equal(2, seen.HitCount);
    }

    [Fact]
    public void Drained_dealt_side_is_attacker_side()
    {
        var h = CounterHarness();
        EffectEventDto? seen = null;
        var drain = new EventDrain(dto => { seen = dto; h.OnEvent(dto); });

        drain.Record(Hit(drain)); // record side = target (zombie)
        drain.Drain(long.MaxValue);

        Assert.NotNull(seen);
        Assert.Equal(EffectTriggers.OnDamageDealt, seen!.Trigger);
        Assert.Equal("plant", seen.Side); // DTO side = attacker, mirroring EffectEventAdapterCore
    }

    [Fact]
    public void Entity_grant_bound_to_the_firing_plant_matches_a_projectile_hit()
    {
        // Dropped success criterion (lawn-combat-wire-todo.md): "An entity:{ptr} grant bound to the
        // firing plant matches on a projectile hit" — the precondition `basic-attack-grant` (T10)
        // rests on. Before T6, a projectile hit's ActorPtr was the BULLET's own ptr, so an
        // entity:{firingPlantPtr} grant could never match (EffectOwnerKey.MatchesEvent's `entity:`
        // branch tests ev.ActorPtr / ev.TargetPtr, never the bullet). Recording with the shooter's
        // ptr as ActorPtr (what EventDrainHost.TryRecordDealtFromBullet now does) fixes this.
        var firingPlantPtr = new IntPtr(0x5001);

        EffectEventDto? seen = null;
        var captor = new EventDrain(dto => seen = dto);
        captor.Record(new GameEventRec(
            GameEventKind.CombatHit, frame: 1, seq: captor.NextSeq(),
            actorPtr: firingPlantPtr, targetPtr: new IntPtr(0xB),
            typeId: 1, targetTypeId: 0, side: GameEventSide.Zombie,
            amount: -10, hitCount: 1, chainDepth: 0,
            sourceGrantIdx: -1, matchKeyIdx: -1, pairId: 0,
            swingPtr: new IntPtr(0xBEEF)));
        captor.Drain(long.MaxValue);
        Assert.NotNull(seen);
        Assert.Equal("5001", seen!.ActorPtr); // the plant, never "BEEF" (the bullet)

        var grant = new EffectGrant { OwnerKey = EffectOwnerKeys.Entity("5001") };
        Assert.True(EffectOwnerKey.MatchesEvent(grant, seen));

        // Falsifier: a grant bound to the BULLET's own ptr (the pre-fix attacker) must NOT match —
        // proves this isn't a match-everything degenerate case.
        var bulletBoundGrant = new EffectGrant { OwnerKey = EffectOwnerKeys.Entity("BEEF") };
        Assert.False(EffectOwnerKey.MatchesEvent(bulletBoundGrant, seen));
    }

    [Fact]
    public void Differential_attacker_power_reaches_the_damage_packet()
    {
        // T6 acceptance: "the attacker's own power reaches the packet", proven as a differential —
        // two different real GameEventRec.ActorPtr values, each resolving to a snapshot with a
        // DIFFERENT composed combat.power.fire, must yield two different real combat deltas for an
        // otherwise-identical hit. Exercises the real production chain this task wires:
        // GameEventRec.ActorPtr -> EventDrain.ToDto -> EffectEventDto.ActorPtr -> DamagePacketBuilder
        // (packet.ActorPtr = ev.ActorPtr, DamagePacketBuilder.cs:27) -> OverlayCombatMath.Finalize ->
        // OverlayCombatCalculator. Not "isn't the {Hp=100,MaxHp=100,Atk=10} stub" — that passes even
        // when the wiring is broken a different way (InjectorCombatBridge.cs:57-59's fallback answers
        // ANY unresolvable key, stub or not). Accuracy/crit are pinned to a saturating value so the
        // comparison needs no RNG-seed choreography: CombatProbability.RollSuccess and the sigmoid
        // both short-circuit to a fixed outcome once probability reaches 1.0 in double precision.
        var targetPtr = new IntPtr(0xB);
        const long weakPower = 10;
        const long strongPower = 400;

        long Deliver(IntPtr attackerPtr, long power)
        {
            var attacker = new CombatActorSnapshot(
                ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                {
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, power),
                    // Saturate hit/crit so the result depends on power alone, never a coin flip.
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyFire, 1_000_000),
                    new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateFire, 1_000_000)
                }),
                ActorElementTypes.Neutral);
            var defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral);

            var math = OverlayCombatMath.Create(
                (ptr, attackerLess) => attackerLess
                    ? CombatActorSnapshot.AttackerLess()
                    : (ptr == "B" ? defender : attacker),
                rng: new SeededCombatRng(0)); // never actually drawn — both probabilities saturate

            long? result = null;
            var drain = new EventDrain(dto =>
            {
                var packet = DamagePacketBuilder.FromOverlay(
                    new Dictionary<string, object?>
                    {
                        ["channel"] = "hp",
                        ["amount"] = -100L,
                        ["elementPayload"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 }
                        }
                    },
                    dto);
                result = math.Finalize(packet.SignedAmount, dto.TargetPtr!, packet, null);
            });

            drain.Record(new GameEventRec(
                GameEventKind.CombatHit, frame: 1, seq: drain.NextSeq(),
                actorPtr: attackerPtr, targetPtr: targetPtr,
                typeId: 1, targetTypeId: 0, side: GameEventSide.Zombie,
                amount: -100, hitCount: 1, chainDepth: 0,
                sourceGrantIdx: -1, matchKeyIdx: -1, pairId: 0));
            drain.Drain(long.MaxValue);

            Assert.NotNull(result);
            return result!.Value;
        }

        var weakerAttackerPtr = new IntPtr(0x1001);
        var strongerAttackerPtr = new IntPtr(0x2002);
        var weakDelta = Deliver(weakerAttackerPtr, weakPower);
        var strongDelta = Deliver(strongerAttackerPtr, strongPower);

        Assert.NotEqual(weakDelta, strongDelta);

        // The weaker attacker's own number must match ITS OWN Hub snapshot computed directly —
        // not merely "differ from the other" (that alone would pass for any two distinct wrong
        // numbers too).
        var expectedWeak = new OverlayCombatCalculator().Compute(
            new OverlayCombatRequest
            {
                BaseOverlayDamage = 100,
                Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
                Attacker = new CombatActorSnapshot(
                    ActorDerivedSnapshot.StubNeutral().Overlay(new[]
                    {
                        new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, weakPower)
                    }),
                    ActorElementTypes.Neutral),
                Defender = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
                ForceHit = true,
                ForceCrit = true
            },
            new SeededCombatRng(0)).SignedDelta;
        Assert.Equal(expectedWeak, weakDelta);
    }
}
