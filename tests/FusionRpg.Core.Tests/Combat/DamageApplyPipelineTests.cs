using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// U6+U8 — apply-pipeline units, the one-mutation-slot invariant (with the negative test
/// documenting the silent-split failure the key discipline prevents), and cross-entry /
/// cross-sink parity.
/// </summary>
public class DamageApplyPipelineTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    sealed class RecordingSink : IHpDeltaSink
    {
        public readonly List<(string OwnerKey, long Amount)> Applied = new();
        public bool Refuse;

        public bool Apply(string ownerKey, long amount, string? pluginId, string? effectId,
            string? grantId, string channel, List<ElementPayloadComponentDto>? elements)
        {
            if (Refuse) return false;
            Applied.Add((ownerKey, amount));
            return true;
        }
    }

    static ShieldGate GateWithShield(long shieldHp, out ShieldRuntime runtime)
    {
        runtime = new ShieldRuntime();
        runtime.Apply(new ShieldGrant { OwnerKey = "entity:z1", SourceId = "s", BaseHp = shieldHp }, Neutral, 0);
        return new ShieldGate(runtime, (_, _) =>
            new CombatActorSnapshot(Neutral, ActorElementTypes.Neutral));
    }

    [Fact]
    public void Partial_absorb_applies_remainder_with_provided_snapshots_only()
    {
        var gate = GateWithShield(40, out _);
        CombatActorResolve mustNotResolve = (_, _) =>
            throw new InvalidOperationException("pipeline snapshots must be the only source");
        var strictGate = new ShieldGate(gate.Runtime, mustNotResolve);
        var sink = new RecordingSink();

        var result = DamageApplyPipeline.Apply(
            "z1", -100, 1, Array.Empty<ElementPayloadComponent>(),
            attackerSnapshot: null, ownerSnapshot: Neutral,
            strictGate, sink);

        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);
        Assert.Equal(-60, result.AppliedAmount);
        Assert.Equal(40, result.AbsorbedAmount);
        Assert.Equal(("entity:z1", -60L), Assert.Single(sink.Applied));
    }

    [Fact]
    public void Full_absorb_reaches_no_sink()
    {
        var gate = GateWithShield(500, out _);
        var sink = new RecordingSink();
        var result = DamageApplyPipeline.Apply(
            "z1", -100, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral, gate, sink);
        Assert.Equal(DamageApplyOutcome.FullyAbsorbed, result.Outcome);
        Assert.Equal(100, result.AbsorbedAmount);
        Assert.Empty(sink.Applied);
    }

    [Fact]
    public void Heals_bypass_the_gate()
    {
        var gate = GateWithShield(500, out var runtime);
        var sink = new RecordingSink();
        var result = DamageApplyPipeline.Apply(
            "z1", 80, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral, gate, sink);
        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);
        Assert.Equal((500, 500), runtime.Totals("entity:z1"));
        Assert.Equal(("entity:z1", 80L), Assert.Single(sink.Applied));
    }

    [Fact]
    public void Zero_unabsorbed_still_reaches_the_sink_for_telemetry_parity()
    {
        var sink = new RecordingSink();
        var result = DamageApplyPipeline.Apply(
            "z1", 0, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral, null, sink);
        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);
        Assert.Equal(("entity:z1", 0L), Assert.Single(sink.Applied));
    }

    [Fact]
    public void Prefixed_ptr_is_rejected_loudly()
    {
        var sink = new RecordingSink();
        Assert.Throws<ArgumentException>(() => DamageApplyPipeline.Apply(
            "entity:z1", -10, 1, Array.Empty<ElementPayloadComponent>(), null, Neutral, null, sink));
    }

    [Fact]
    public void HitCount_forwards_into_breaker_math()
    {
        // pen 10 vs shield 1000: coalesced 5×20 → damageToShield = 100 + 5×10 = 150.
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatShieldPenOmni, DerivedModifierOp.Flat, 10.0)
        });
        var gate = GateWithShield(1000, out var runtime);
        var sink = new RecordingSink();
        DamageApplyPipeline.Apply(
            "z1", -100, 5, Array.Empty<ElementPayloadComponent>(), attacker, Neutral, gate, sink);
        Assert.Equal(1000 - 150, runtime.Totals("entity:z1").Hp);
    }

    [Fact]
    public void Damage_callback_fires_only_on_applied_damage()
    {
        var sink = new RecordingSink();
        long noted = 0;
        DamageApplyPipeline.Apply("z1", -50, 1, Array.Empty<ElementPayloadComponent>(),
            null, Neutral, null, sink, onHpDamageApplied: a => noted = a);
        Assert.Equal(-50, noted);

        noted = 0;
        DamageApplyPipeline.Apply("z1", 50, 1, Array.Empty<ElementPayloadComponent>(),
            null, Neutral, null, sink, onHpDamageApplied: a => noted = a);
        Assert.Equal(0, noted);   // heals never note

        sink.Refuse = true;
        DamageApplyPipeline.Apply("z1", -50, 1, Array.Empty<ElementPayloadComponent>(),
            null, Neutral, null, sink, onHpDamageApplied: a => noted = a);
        Assert.Equal(0, noted);   // refused sink never notes
    }
}

/// <summary>U8 — funnel-slot invariants and cross-entry/cross-sink parity.</summary>
public class DamageApplyPipelineFunnelTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    static (FoundationHarness Harness, FunnelHpDeltaSink Sink) FunnelHost()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[] { new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 } });
        return (h, new FunnelHpDeltaSink(h.Funnel));
    }

    static int SlotsFor(FoundationHarness h, string ptr)
    {
        h.Funnel.Flush();
        return h.Sink.Items.Count(a =>
            a.Action == EffectActions.ApplyResourceDelta &&
            string.Equals(a.Params["targetPtr"]?.ToString(), ptr, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Several_pipeline_deltas_one_actor_one_window_merge_to_one_slot()
    {
        var (h, sink) = FunnelHost();
        for (var i = 0; i < 3; i++)
            DamageApplyPipeline.Apply("z1", -10, 1, Array.Empty<ElementPayloadComponent>(),
                null, Neutral, null, sink, pluginId: "test");
        Assert.Equal(1, SlotsFor(h, "z1"));
        var slot = h.Sink.Items.Single(a => a.Action == EffectActions.ApplyResourceDelta);
        Assert.Equal(-30L, Convert.ToInt64(slot.Params["amount"]));
    }

    [Fact]
    public void Mixed_raw_and_pipeline_keys_split_the_slot_the_failure_the_discipline_prevents()
    {
        var (h, sink) = FunnelHost();
        DamageApplyPipeline.Apply("z1", -10, 1, Array.Empty<ElementPayloadComponent>(),
            null, Neutral, null, sink, pluginId: "test");
        h.Funnel.EnqueueMutation("z1", -10, pluginId: "test");   // raw key — the banned pattern
        Assert.Equal(2, SlotsFor(h, "z1"));   // silent split: nothing crashes, telemetry forks
    }

    [Fact]
    public void Packet_and_general_entries_agree_on_shield_outcomes()
    {
        // Same shield stack + same damage through both pipeline entries → same numbers.
        long ApplyVia(bool packetEntry)
        {
            var runtime = new ShieldRuntime();
            runtime.Apply(new ShieldGrant { OwnerKey = "entity:z1", SourceId = "s", BaseHp = 40 }, Neutral, 0);
            var gate = new ShieldGate(runtime, (_, _) =>
                new CombatActorSnapshot(Neutral, ActorElementTypes.Neutral));
            var (h, sink) = FunnelHost();
            DamageApplyResult result;
            if (packetEntry)
            {
                result = DamageApplyPipeline.ApplyPacketToFunnel(
                    new DamagePacket { PluginId = "test" }, "z1", -100, 1, gate, h.Funnel, null);
            }
            else
            {
                result = DamageApplyPipeline.Apply("z1", -100, 1,
                    Array.Empty<ElementPayloadComponent>(), null, Neutral, gate, sink, pluginId: "test");
            }

            Assert.Equal(40, result.AbsorbedAmount);
            return result.AppliedAmount;
        }

        Assert.Equal(ApplyVia(packetEntry: true), ApplyVia(packetEntry: false));
    }

    [Fact] // "the origin defaults, so every existing call site is zero lines changed" -- ApplyPacketToFunnel's
    // half of P1 (spec-gate-counters.md §7): only DamageApplyPipeline.Apply got the origin parameter
    // originally; ApplyPacketToFunnel (the dispatcher hot path CombatDamageDispatcher.cs calls) had none
    // at all, so the live-lawn/overlay pulse path had no way to ever signal DamageOrigin.StatusPulse.
    // Closed 2026-09-06 (found while verifying G3), additively, matching the exact P1 shape.
    public void ApplyPacketToFunnel_origin_defaults_to_DirectHit_and_accepts_StatusPulse_explicitly()
    {
        var (h, _) = FunnelHost();
        // The exact pre-existing call shape (no origin argument at all) still compiles and behaves
        // identically -- proof this is an additive parameter, not a breaking signature change.
        var result = DamageApplyPipeline.ApplyPacketToFunnel(
            new DamagePacket { PluginId = "test" }, "z1", -10, 1, null, h.Funnel, null);
        Assert.Equal(DamageApplyOutcome.Applied, result.Outcome);

        var (h2, _) = FunnelHost();
        var withOrigin = DamageApplyPipeline.ApplyPacketToFunnel(
            new DamagePacket { PluginId = "test" }, "z1", -10, 1, null, h2.Funnel, null,
            origin: DamageOrigin.StatusPulse);
        Assert.Equal(DamageApplyOutcome.Applied, withOrigin.Outcome);
    }
}
