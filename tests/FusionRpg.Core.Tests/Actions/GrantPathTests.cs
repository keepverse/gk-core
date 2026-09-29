using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Tests.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T14 (action-todo.md, spec-basic-attack-adoption.md): the "grant path" finding, asserted rather
/// than claimed. `AtomKindRegistry.cs`'s two `D6` comments on `resource.delta` and `shield.grant`
/// say battle's sink and shield gate are real but unreachable — "Full again when battle grows a
/// grant path." `BattleEngine.Resolve`'s new <c>onEffectHostReady</c> seam (null in every production
/// and golden call site) proves both now reach the SAME host a real battle constructs.
/// </summary>
public class GrantPathTests
{
    [Fact]
    public void ShieldGate_is_wired_into_the_battle_host_unconditionally()
    {
        // The wiring itself is unconditional and inert -- proven here without granting anything.
        FusionRpg.Core.Combat.Shield.ShieldGate? captured = null;
        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002,
            onEffectHostReady: host => captured = host.Bag.ShieldGate);

        Assert.NotNull(captured);
    }

    [Fact]
    public void ResourceDelta_reaches_the_battle_sink_through_Grant_and_OnEvent()
    {
        // The definitive proof is `host.LastApplied` -- the SAME record `BattleEffectSink` writes
        // for an ordinary swing's `ApplyHp`. `OnEvent`'s returned plan does not list this action
        // (that bookkeeping only fires for a `RecordingEffectSink`, which `BattleEffectHost` is not
        // — see `EffectBag.OnEvent`'s `sinkRecorder` check), so asserting against the plan would be
        // asserting against the wrong artifact. The mutated game state is the artifact that matters.
        BattleReport? report = null;
        IReadOnlyList<BattleAppliedHpDelta>? applied = null;

        report = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, onEffectHostReady: host =>
        {
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "t14-dmg",
                EffectId = "fx.overlay_damage",
                PluginId = "test",
                Overlay = new Dictionary<string, object?>
                {
                    ["amount"] = -10L,
                    ["icd_ms"] = 0,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                    ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant },
                },
            });

            host.Bag.OnEvent(new EffectEventDto
            {
                Trigger = EffectTriggers.OnDamageDealt,
                ActorPtr = "squad:0",
                TargetPtr = "wave:0",
                Side = "squad",
                TypeId = 0,
                TargetTypeId = 0,
                Tick = 0,
            });

            applied = host.LastApplied;
        });

        // The exact amount is NOT asserted: `ApplyResourceDelta` routes through
        // `CombatDamageDispatcher.DispatchInstant`, the same combat-math pipeline an ordinary swing
        // uses (mitigation, hit roll), so the authored "-10" is a base input, not the applied
        // delta. What T14 proves is that the atom reaches the sink AT ALL -- a silent no-op (the
        // pre-T14 state) would leave `LastApplied` empty, full stop.
        Assert.NotNull(report);
        Assert.NotNull(applied);
        Assert.NotEmpty(applied!);
        Assert.All(applied!, a => Assert.True(a.Amount < 0, $"{a.ActorKey} took non-negative damage {a.Amount}"));
    }

    [Fact]
    public void ShieldGrant_actually_creates_a_shield_instance_on_the_battle_shield_stack()
    {
        FusionRpg.Core.Combat.Shield.ShieldRuntime? runtime = null;

        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, onEffectHostReady: host =>
        {
            runtime = host.Bag.ShieldGate!.Runtime;

            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "t14-shield",
                EffectId = "fx.shield_grant",
                PluginId = "test",
                Overlay = new Dictionary<string, object?>
                {
                    ["amount"] = 500L,
                    ["icd_ms"] = 0,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                },
            });

            host.Bag.OnEvent(new EffectEventDto
            {
                Trigger = EffectTriggers.OnDamageDealt,
                ActorPtr = "wave:0",
                TargetPtr = "squad:0",
                Side = "wave",
                TypeId = 0,
                TargetTypeId = 0,
                Tick = 0,
            });
        });

        Assert.NotNull(runtime);
        // ExecGrantShield writes to the SAME ShieldRuntime ordinary attacks absorb through
        // (BattleEngine's own `shields`) -- not a second, disconnected shield stack.
        var shields = runtime!.GetShields(EffectOwnerKeys.Entity("squad:0"));
        Assert.NotEmpty(shields);
    }

    [Fact]
    public void The_grant_path_seam_is_inert_when_null_the_golden_suite_already_proves_this()
    {
        // Documented here for the record: BattleGoldenTests / BasicAttackAdoptionTests /
        // PreAdoptionTraceTests all call Resolve with onEffectHostReady defaulted to null and stay
        // byte-identical after this module landed -- that is the actual proof of inertness, not
        // this one test. This test only pins the API default so a future signature change is loud.
        var report = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002);
        Assert.True(report.Rounds > 0);
    }
}
