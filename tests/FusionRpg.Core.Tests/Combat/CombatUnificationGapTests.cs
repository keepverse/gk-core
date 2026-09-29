using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// /test gap pass over the U1–U16 foundations: locks that keep future refactors honest.
/// </summary>
public class CombatUnificationGapTests
{
    static readonly ActorDerivedSnapshot Neutral = ActorDerivedSnapshot.StubNeutral();

    [Fact]
    public void Empty_payload_DTO_on_a_packet_stays_pass_through_never_omni()
    {
        // U3 legalized empty components at the RESOLVER. The dispatcher-side contract must
        // not drift: an empty elementPayload list on a packet is pass-through (raw amount),
        // not an omni-resolved attack. If someone "simplifies" OverlayCombatMath.Finalize
        // to always call Compute, this golden catches the silent behavior change.
        var math = OverlayCombatMath.Create((_, _) =>
            new CombatActorSnapshot(Neutral, ActorElementTypes.Neutral));
        var packet = new DamagePacket { ElementPayload = new List<ElementPayloadComponentDto>() };
        Assert.Equal(-100, math.Finalize(-100, "z1", packet, null));   // untouched, no hit roll
        Assert.Equal(-1, math.Finalize(-1, "z1", packet, null));
    }

    [Fact]
    public void Chip_floor_engages_on_typed_weak_matchup_hits_too()
    {
        // U4 tested the omni path; lock the typed path: WEK matchup + heavy typed defense
        // drives powerAdjusted to 0 — battle profile still lands the chip.
        var composer = new DerivedComposer();
        var defender = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatDefenseFire, DerivedModifierOp.Flat, 10_000.0)
        });
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
            Defender = new CombatActorSnapshot(defender,
                ActorElementTypes.Create(ElementTypeId.Air)),   // fire → air = WEK −25 on top
            ForceHit = true,
            ForceCrit = false,
            Profile = CombatProfile.BattleSim
        };
        var calc = new OverlayCombatCalculator();
        var (delta, _) = calc.Compute(request, new SeededCombatRng(1));
        Assert.Equal(-5, delta);   // ceil(0.05 × 100) — typed and omni paths share the floor

        var overlayRequest = new OverlayCombatRequest
        {
            BaseOverlayDamage = request.BaseOverlayDamage,
            Components = request.Components,
            Defender = request.Defender,
            ForceHit = true,
            ForceCrit = false
        };
        var (overlayDelta, _) = calc.Compute(overlayRequest, new SeededCombatRng(1));
        Assert.Equal(0, overlayDelta);   // overlay profile: same inputs, no floor
    }

    [Fact]
    public void Natural_roll_stream_fingerprint_is_locked()
    {
        // The U14 golden harness in miniature: 20 natural-roll swings on the owned PRNG.
        // If this fingerprint moves without a deliberate version bump, stream consumption
        // drifted (extra/missing draws) — exactly the class of accident U5's contracts ban.
        var rng = new SeededRngCombatAdapter(SeededRng.DeriveStream(1234, "crit"));
        var calc = new OverlayCombatCalculator();
        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = 100,
            Components = Array.Empty<ElementPayloadComponent>()   // neutral 0.5/0.5 rolls
        };
        var fingerprint = string.Concat(Enumerable.Range(0, 20).Select(_ =>
        {
            var (_, b) = calc.Compute(request, rng);
            return b.Hit ? (b.Crit ? "C" : "h") : ".";
        }));
        // Blessed from the first run (seed 1234, "crit" stream) — deterministic by
        // construction; any drift means stream consumption changed.
        Assert.Equal("hh.hhh.C.C.hChh....C", fingerprint);
    }

    [Fact]
    public void Gate_packet_and_component_paths_agree_on_typed_absorption()
    {
        // Same ice shield, same fire hit — once via the legacy DamagePacket path (payload
        // parsed inside the gate), once via the packet-free overload with pre-parsed
        // components and explicit snapshots. Identical remainders and shield state.
        long Run(bool packetPath)
        {
            var runtime = new ShieldRuntime();
            runtime.Apply(new ShieldGrant
            {
                OwnerKey = "entity:z1", SourceId = "s", Element = ElementTypeId.Ice, BaseHp = 60
            }, Neutral, 0);
            var gate = new ShieldGate(runtime, (_, _) =>
                new CombatActorSnapshot(Neutral, ActorElementTypes.Neutral));

            long after;
            if (packetPath)
            {
                var packet = new DamagePacket
                {
                    ElementPayload = new List<ElementPayloadComponentDto>
                    {
                        new() { Element = "fire", Weight = 1.0 }
                    }
                };
                after = gate.AbsorbFinalized(-240, "z1", packet, 1);
            }
            else
            {
                after = gate.AbsorbFinalized(-240, "z1",
                    new[] { new ElementPayloadComponent(ElementTypeId.Fire, 1.0) },
                    1, attackerSnapshot: null, ownerSnapshot: Neutral);
            }

            Assert.False(runtime.HasAnyInstances());   // STR fire vs ice 60: breaks either way
            return after;
        }

        Assert.Equal(Run(packetPath: true), Run(packetPath: false));
        Assert.Equal(-192, Run(packetPath: true));   // the shield-spec worked-example S1 number
    }

    [Fact]
    public void Sim_state_snapshot_exposes_shield_totals_shape()
    {
        var engine = new SimEngine();
        engine.BoardStart(null);
        engine.SpawnPlant(new StatsConfig(), new SimSpawnPlantRequest
        {
            Ptr = "P1", Row = 2, Col = 3, Hp = 300, MaxHp = 300
        });
        engine.GrantShield(new SimShieldGrantRequest { Ptr = "P1", Amount = 75 });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(engine.Snapshot()));
        var shields = doc.RootElement.GetProperty("shields");
        var entry = Assert.Single(shields.EnumerateArray());
        Assert.Equal("P1", entry.GetProperty("ptr").GetString());
        Assert.Equal(75, entry.GetProperty("hp").GetInt64());
        Assert.Equal(75, entry.GetProperty("maxHp").GetInt64());

        // Unshielded boards serialize an empty array — the probe script's empty-state case.
        engine.BoardEnd(null);
        engine.BoardStart(null);
        using var empty = JsonDocument.Parse(JsonSerializer.Serialize(engine.Snapshot()));
        Assert.Empty(empty.RootElement.GetProperty("shields").EnumerateArray());
    }
}
