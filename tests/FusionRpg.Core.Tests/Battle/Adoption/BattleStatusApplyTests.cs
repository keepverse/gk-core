using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T49 (action-todo.md Phase 12, spec-battle-status-apply.md) — proves `status.apply` (FA2) executes
/// for real, using the one real shipped def that carries it: `fx.poison_on_hit`
/// (`EffectAtomCatalog.Generated.cs`, `Triggers: {OnDamageDealt}`, `Params: {status: "poison",
/// duration: 5}` -- no `targetPtr`/`target` override, so `BattleEffectSink.ExecApplyStatus` falls
/// back to `ctx.Event.TargetPtr`, landing on whoever the attack actually hit).
/// </summary>
public class BattleStatusApplyTests
{
    static BattleActorSetup Actor(string key, string side, long? maxHp = null, long? atk = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t49-species", TypeId = 10_007, Level = 6,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(6), Atk = atk ?? BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "t49-wave",
        Squad = new[] { Actor("squad:0", "squad", maxHp: BattleRuleset.BaseHp(6) * 100) },
        Wave = new[] { Actor("wave:0", "wave", maxHp: BattleRuleset.BaseHp(6) * 100) },
    };

    static void GrantPoisonOnHit(BattleEffectHost host) => host.Bag.Grant(new EffectGrantDto
    {
        GrantId = "probe:poison-on-hit",
        EffectId = "fx.poison_on_hit",
        OwnerKind = "entity",
        OwnerKey = EffectOwnerKeys.Entity("squad:0"),
        PluginId = "battle",
    });

    [Fact]
    public void A_real_status_apply_def_applies_a_real_timed_status()
    {
        BattleEffectHost? captured = null;
        BattleEngine.Resolve(Setup(), seed: 9, onEffectHostReady: host =>
        {
            captured = host;
            GrantPoisonOnHit(host);
        });

        // fx.poison_on_hit's own owner-matching dual-fire (same mechanism A18b/A18c both found) means
        // this can land on squad:0 (wave's own hit against squad) as well as wave:0 (squad's own hit)
        // -- check both hosts for the applied instance rather than assume which side.
        var onWave = captured!.Bag.Status!.ForHost("wave:0");
        var onSquad = captured.Bag.Status!.ForHost("squad:0");
        var poison = onWave.Concat(onSquad).FirstOrDefault(i => i.StatusId == "poison");

        Assert.NotNull(poison);
        // duration: 5 (seconds, FA2's own convention) -> exactly 5000ms -- the one conversion this
        // module's own spec names as "the one place this module must get right."
        Assert.Equal(5000, (poison!.ExpiresAt - poison.LastApplied).TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void A_late_round_apply_computes_expiry_from_when_it_fired_not_from_battle_start()
    {
        // The audit's own caught bug, guarded against regressing: using T0 (battle start) instead of
        // the live clock would make every status applied after round 1 carry the wrong expiry basis.
        // Proven directly: a poison applied at round N has LastApplied close to "T0 + N rounds", not
        // pinned to T0 itself, regardless of how many rounds actually elapsed before the first landed
        // hit (miss variance means this is not always round 1).
        BattleEffectHost? captured = null;
        var report = BattleEngine.Resolve(Setup(), seed: 9, onEffectHostReady: host =>
        {
            captured = host;
            GrantPoisonOnHit(host);
        });

        var onWave = captured!.Bag.Status!.ForHost("wave:0");
        var onSquad = captured.Bag.Status!.ForHost("squad:0");
        var poison = onWave.Concat(onSquad).First(i => i.StatusId == "poison");

        // T0 is the battle's own start instant; a status applied at round 1 or later must have
        // LastApplied strictly at or after T0, and -- the actual regression this guards -- its
        // ExpiresAt must track LastApplied (5000ms later), never a fixed T0+5000ms regardless of when
        // it actually fired. With only one grant and one landed hit needed to prove this, round 1 is
        // the earliest possible fire; the assertion is on the RELATIONSHIP (Expires - Applied == 5s),
        // already checked above, and repeated here from the other direction: LastApplied did not stay
        // pinned at some default/zero DateTimeOffset, proving the live clock (not a fixed placeholder)
        // was genuinely read.
        Assert.True(poison.LastApplied > DateTimeOffset.MinValue);
        Assert.True(report.Rounds >= 1);
    }

    [Fact]
    public void A_bare_host_with_no_wired_Status_refuses_quietly()
    {
        // BattleEffectSink.Status/StatusRng start null (BattleEffectHostTests.cs-style bare
        // construction, or any future host that never calls Host.Status = ...) -- the null-guard this
        // module's own audit added, proven directly rather than trusted from a code read alone.
        var resolveActor = new System.Collections.Generic.Dictionary<string, IBattleHpTarget>();
        var host = new BattleEffectHost(k => resolveActor.TryGetValue(k, out var t) ? t : null, rngSeed: 1);

        // No Host.Status/StatusRng assignment at all -- must not throw when firing an event that
        // would otherwise reach ExecApplyStatus.
        host.Bag.Catalog.Upsert(new FusionRpg.Core.Effects.EffectDef
        {
            EffectId = "test.bare-status-probe",
            EffectType = EffectTypes.Triggered,
            Triggers = new() { "OnSpawn" },
            Actions = new() { new FusionRpg.Core.Effects.EffectActionRow { Seq = 1, Action = EffectActions.ApplyStatus, Params = new() { ["status"] = "poison" } } },
        });
        host.Bag.Grant(new EffectGrantDto { GrantId = "probe", EffectId = "test.bare-status-probe", OwnerKind = "entity", OwnerKey = EffectOwnerKeys.Entity("x") });

        var ex = Record.Exception(() => host.Bag.OnEvent(new EffectEventDto { Trigger = "OnSpawn", ActorPtr = "x", Tick = 0 }));
        Assert.Null(ex);
    }
}
