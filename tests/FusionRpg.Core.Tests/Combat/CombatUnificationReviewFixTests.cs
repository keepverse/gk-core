using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// Five-axis review findings on the U6/U15 changes (Prove-It: RED before the fixes).
/// </summary>
public class CombatUnificationReviewFixTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    [Fact]
    public void Gate_packet_path_allocates_nothing_for_unshielded_targets()
    {
        // Pre-refactor, the payload was parsed only AFTER the per-owner shield check; the
        // U6 wrapper regressed to parse-first — one component-list allocation per
        // payload-carrying hit on unshielded targets whenever ANY shield exists on the
        // board. The per-owner miss must stay a zero-allocation fast path.
        var runtime = new ShieldRuntime();
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:other", SourceId = "s", BaseHp = 50 }, Neutral, 0);
        var gate = new ShieldGate(runtime, (_, _) =>
            new CombatActorSnapshot(Neutral, ActorElementTypes.Neutral));
        var packet = new DamagePacket
        {
            ElementPayload = new List<ElementPayloadComponentDto>
            {
                new() { Element = "fire", Weight = 1.0 }
            }
        };
        var noPayload = new DamagePacket();
        gate.AbsorbFinalized(-100, "unshielded", packet, 1);      // warm-up both shapes
        gate.AbsorbFinalized(-100, "unshielded", noPayload, 1);

        // The owner-key concat is a pre-existing cost on both shapes; the parse must not
        // be. Assert: payload presence adds ZERO bytes on the per-owner miss path.
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            gate.AbsorbFinalized(-100, "unshielded", noPayload, 1);
        var baseline = GC.GetAllocatedBytesForCurrentThread() - b0;

        var b1 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            gate.AbsorbFinalized(-100, "unshielded", packet, 1);
        var withPayload = GC.GetAllocatedBytesForCurrentThread() - b1;

        Assert.Equal(baseline, withPayload);
    }

    [Fact]
    public void Sim_damage_keeps_overhealed_hp_semantics()
    {
        // The old sim damage path had no MaxHp clamp: an overhealed entity (spawn hp >
        // maxHp is constructible) loses hp only by the damage dealt. The U15 sink must not
        // clamp damage results down to MaxHp.
        var engine = new SimEngine();
        engine.BoardStart(null);
        engine.SpawnPlant(new StatsConfig(), new SimSpawnPlantRequest
        {
            Ptr = "P1", Row = 2, Col = 3, Hp = 500, MaxHp = 300
        });
        engine.DamagePlant(new StatsConfig(), new SimDamageRequest { Ptr = "P1", Damage = 10 });
        Assert.Equal(490, engine.Plants.Single(p => p.Ptr == "P1").Hp);
    }
}
