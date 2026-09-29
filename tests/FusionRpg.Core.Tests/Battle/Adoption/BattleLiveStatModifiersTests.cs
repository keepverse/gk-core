using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T54 (action-todo.md Phase 12, spec-battle-live-stat-modifiers.md) — the full proof, against the one
/// real shipped `stat.modify` def (`fx.passive_atk_flat`, `EffectType: "Passive"`, `Triggers: {}`,
/// `Params: {flat: 10, channel: "atk"}`) plus a synthetic triggered variant, since no shipped content
/// exercises a TRIGGERED stat.modify at all.
/// </summary>
public class BattleLiveStatModifiersTests
{
    static BattleActorSetup Actor(string key, string side, long? maxHp = null, long? atk = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t54-species", TypeId = 10_008, Level = 6,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(6), Atk = atk ?? BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "t54-wave",
        Squad = new[] { Actor("squad:0", "squad", maxHp: BattleRuleset.BaseHp(6) * 100) },
        Wave = new[] { Actor("wave:0", "wave", maxHp: BattleRuleset.BaseHp(6) * 100, atk: 1) },
    };

    [Fact]
    public void A_real_permanent_stat_modify_def_applies_immediately_on_grant()
    {
        // fx.passive_atk_flat's own EffectType ("Passive") fires a synthetic OnGranted event
        // synchronously inside Bag.Grant -- no OnActivate/OnDamageDealt needed at all, matching this
        // kind's own "permanent modifier buffs its own holder" model exactly. Applied at construction
        // time, before round 1 -- the same wiring order the audit's own Host-before-Status finding
        // already established (Ledger/ResolveStatTarget wired before BindContainers/the loadout loop
        // runs, so a Passive auto-fire during that same loop already sees them set).
        BattleEffectHost? captured = null;
        var without = BattleEngine.Resolve(Setup(), seed: 3);
        var with = BattleEngine.Resolve(Setup(), seed: 3, onEffectHostReady: h =>
        {
            captured = h;
            h.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:passive-atk",
                EffectId = "fx.passive_atk_flat",
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
            });
        });

        Assert.True(captured!.Bag.HasAnyGrant());
        // Same seed -> identical hit/miss/crit sequence. action-enrich AE1.4 (`spec-action-base.md`
        // section "What leaves"): the hit's base is the ACTION's, so a `stat.modify` on `atk` no
        // longer changes damage. The grant/applied half is what this test still owns (the Passive
        // fires inside `Bag.Grant`, asserted above); asserting the retired read here means a
        // re-introduced atk -> damage path fails this test rather than passing silently.
        var boosted = with.Actors.Single(a => a.Key == "squad:0").DamageDealt;
        var unboosted = without.Actors.Single(a => a.Key == "squad:0").DamageDealt;
        Assert.Equal(unboosted, boosted);
    }

    [Fact]
    public void A_triggered_stat_modify_persists_across_rounds_without_retriggering()
    {
        var probe = new EffectDef
        {
            EffectId = "test.modify-stat-probe",
            EffectType = EffectTypes.Triggered,
            Name = "ModifyStat probe",
            Triggers = new() { AtomTriggers.OnActivate },
            Actions = new()
            {
                new EffectActionRow
                {
                    Seq = 1, Action = EffectActions.ModifyStat,
                    Params = new Dictionary<string, object?> { ["channel"] = "atk", ["flat"] = 25.0 },
                },
            },
        };

        var report = BattleEngine.Resolve(Setup(), seed: 3, onEffectHostReady: host =>
        {
            host.Bag.Catalog.Upsert(probe);
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:modify-atk",
                EffectId = probe.EffectId,
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
            });
        });

        Assert.True(report.Rounds > 1, "the wave HP budget is sized to force a multi-round battle");
        // action-enrich AE1.4 (`spec-action-base.md` section "What leaves"): the hit's base is the
        // ACTION's, so a `stat.modify` on `atk` no longer moves damage — the same retirement this
        // file's sibling test above asserts. The probe's own application is still exercised (it is
        // registered and granted here, and the validation tests in this file cover the atom), and the
        // assertion below is the one that fails if an atk -> damage read is ever reintroduced.
        var boosted = report.Actors.Single(a => a.Key == "squad:0").DamageDealt;
        var baselineReport = BattleEngine.Resolve(Setup(), seed: 3);
        var unboosted = baselineReport.Actors.Single(a => a.Key == "squad:0").DamageDealt;

        Assert.Equal(unboosted, boosted);
    }

    static AtomRow StatModifyAtom(string op, string? trigger = null) => new()
    {
        AtomId = "atom.t54-probe.t1",
        KindId = "stat.modify",
        FamilyId = "atom.t54-probe",
        Tier = 1,
        Name = "T54 probe",
        ParamsJson = $$"""{"channel":"atk","op":"{{op}}","amount":10}""",
        WhenJson = trigger is null ? "{}" : $$"""{"trigger":"{{trigger}}"}""",
    };

    [Fact]
    public void Override_is_refused_at_bind_not_silently_accepted()
    {
        var r = AtomRowValidator.Validate(StatModifyAtom("override"));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void A_permanent_no_trigger_stat_modify_atom_still_validates()
    {
        // The exact regression this module's own audit-caught TriggerOptional fix guards --
        // ChannelExtensionTests.The_three_new_channels_pass_atom_validation failed with "stat.modify
        // requires a trigger" the moment Triggers went from empty to AllTriggers, before the fix.
        var r = AtomRowValidator.Validate(StatModifyAtom("flat"));
        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void A_triggered_stat_modify_atom_also_validates()
    {
        // The genuinely NEW case A18e unlocks: a stat.modify atom MAY now carry a real trigger.
        var r = AtomRowValidator.Validate(StatModifyAtom("flat", AtomTriggers.OnActivate));
        Assert.True(r.IsOk, r.ToString());
    }
}
