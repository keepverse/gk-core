using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Events;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>
/// lawn-combat-wire T6 / L-N13: two shooters of different composed power, recorded through ONE drain
/// and ONE <see cref="OverlayCombatMath"/>, each get their own power — the resolver is keyed strictly by
/// the event's <c>ActorPtr</c> and throws on any other key, so a dropped, swapped or bullet-substituted
/// attacker ptr fails instead of silently reading some snapshot. (The earlier
/// <c>EventDrainIntegrationTests.Differential_attacker_power_reaches_the_damage_packet</c> builds a fresh
/// snapshot per call, which a ptr-ignoring resolver would also pass.)
/// </summary>
public class AttackerPowerByPtrTests
{
    static CombatActorSnapshot Attacker(long power) => new(
        ActorDerivedSnapshot.StubNeutral().Overlay(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.CombatPowerFire, power),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatAccuracyFire, 1_000_000),
            new KeyValuePair<string, double>(DerivedStatChannels.CombatCritRateFire, 1_000_000)
        }),
        ActorElementTypes.Neutral);

    [Fact]
    public void Two_attackers_in_one_drain_each_resolve_their_own_power_by_ptr()
    {
        var byPtr = new Dictionary<string, CombatActorSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["1001"] = Attacker(10),
            ["2002"] = Attacker(400),
            ["b"] = new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral)
        };
        var resolvedKeys = new List<string>();
        var math = OverlayCombatMath.Create(
            (ptr, attackerLess) =>
            {
                if (attackerLess) return CombatActorSnapshot.AttackerLess();
                resolvedKeys.Add(ptr);
                return byPtr.TryGetValue(ptr, out var snap)
                    ? snap
                    : throw new InvalidOperationException("resolver asked for an unknown ptr: " + ptr);
            },
            rng: new SeededCombatRng(0));

        var results = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var drain = new EventDrain(dto =>
        {
            var packet = DamagePacketBuilder.FromOverlay(new Dictionary<string, object?>
            {
                ["channel"] = "hp",
                ["amount"] = -100L,
                ["elementPayload"] = new List<object?> { new Dictionary<string, object?> { ["element"] = "fire", ["weight"] = 1.0 } }
            }, dto);
            results[dto.ActorPtr!] = math.Finalize(packet.SignedAmount, dto.TargetPtr!, packet, null);
        });

        foreach (var attacker in new[] { new IntPtr(0x1001), new IntPtr(0x2002) })
        {
            drain.Record(new GameEventRec(
                GameEventKind.CombatHit, frame: 1, seq: drain.NextSeq(),
                actorPtr: attacker, targetPtr: new IntPtr(0xB),
                typeId: 1, targetTypeId: 0, side: GameEventSide.Zombie,
                amount: -100, hitCount: 1, chainDepth: 0,
                sourceGrantIdx: -1, matchKeyIdx: -1, pairId: 0,
                swingPtr: new IntPtr(0xBEEF)));
        }
        drain.Drain(long.MaxValue);

        Assert.Equal(2, results.Count);
        Assert.True(Math.Abs(results["2002"]) > Math.Abs(results["1001"]),
            $"stronger attacker must hit harder: weak {results["1001"]} strong {results["2002"]}");
        Assert.DoesNotContain(resolvedKeys, k => string.Equals(k, "BEEF", StringComparison.OrdinalIgnoreCase));
    }
}
