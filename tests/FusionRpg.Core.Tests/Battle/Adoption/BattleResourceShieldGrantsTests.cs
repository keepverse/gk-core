using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T46 (action-todo.md Phase 12, spec-battle-resource-shield-grants.md §3) — proves all three
/// kinds this module owns actually execute, granting directly via the T14 `onEffectHostReady` seam
/// (bypassing A18a's own binding loop, same as `OnActivateTriggerTests` — A18a supplies no per-grant
/// overlay data, and every kind here needs one, so this file proves execution in isolation from
/// binding). Real, shipped `EffectAtomCatalog` defs used where one exists for the kind
/// (`fx.overlay_damage`, `fx.shield_grant`); the DoT/contagion piggyback has **no shipped content at
/// all** (verified: `grep -n "statusId" EffectAtomCatalog.Generated.cs` finds zero hits —
/// `fx.poison_on_hit` uses the separate `ApplyStatus`/FA2 action, A18d's own kind, not this payload) —
/// a synthetic def is the only way to exercise it today.
/// </summary>
public class BattleResourceShieldGrantsTests
{
    static BattleActorSetup Actor(string key, string side, long? maxHp = null, long? atk = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t46-species", TypeId = 10_006, Level = 6,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(6), Atk = atk ?? BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup(long squadMaxHp, long waveMaxHp, long waveAtk = 1) => new()
    {
        WaveId = "t46-wave",
        Squad = new[] { Actor("squad:0", "squad", maxHp: squadMaxHp) },
        Wave = new[] { Actor("wave:0", "wave", maxHp: waveMaxHp, atk: waveAtk) },
    };

    [Fact]
    public void A_real_resource_delta_def_deals_extra_damage_through_the_full_grant_path()
    {
        // fx.overlay_damage ships with an empty amount/targetPtr -- both overlay-driven (D7). Squad's
        // own OnDamageDealt (this module's firing site, T45) supplies them via the grant's Overlay.
        var setup = Setup(squadMaxHp: BattleRuleset.BaseHp(6) * 100, waveMaxHp: BattleRuleset.BaseHp(6) * 100);

        BattleReport Resolve(bool bindProbe) => BattleEngine.Resolve(setup, seed: 5, onEffectHostReady: host =>
        {
            if (!bindProbe) return;
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:overlay-damage",
                EffectId = "fx.overlay_damage",
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
                Overlay = new() { ["amount"] = -7.0, ["targetPtr"] = "wave:0" },
            });
        });

        var without = Resolve(false);
        var with = Resolve(true);

        Assert.Equal(without.Rounds, with.Rounds); // same seed -> identical combat sequence either way
        var waveWithout = without.Actors.Single(a => a.Key == "wave:0").HpRemaining;
        var waveWith = with.Actors.Single(a => a.Key == "wave:0").HpRemaining;

        // A directional claim, not an exact count: unlike OnActivate (fires unconditionally),
        // OnDamageDealt only fires on a LANDED hit (RunBasicAttackStep's own `if (!breakdown.Hit)
        // return Continue` gates it, above this call site) -- found empirically, trying an exact
        // "2 x Rounds" prediction first (the same owner-matching dual-check as OnActivateTriggerTests
        // still applies: squad's own hit matches via ActorPtr, wave's own hit against squad ALSO
        // matches via TargetPtr) overshot, because not every round's attack connects at this combat
        // tier's hit chance. The robust, hit-rate-independent claim: strictly more damage landed on
        // wave:0 with the probe bound than without, over identical combat (same seed, same Rounds).
        Assert.True(waveWith < waveWithout, $"expected extra probe damage on wave:0; without={waveWithout}, with={waveWith}");
    }

    [Fact]
    public void A_resource_delta_DoT_payload_applies_a_real_ticking_status()
    {
        // Found empirically: StatusEffectBridge.TryApplyFromGrant (EffectBag.cs:439) reads
        // `grant.Overlay` DIRECTLY, not the def-Params/grant-Overlay MERGED dictionary FireGrant
        // builds for the instant-damage packet -- so the DoT payload (statusId/periodMs/durationMs)
        // must live on the GRANT's own Overlay, never the def's Actions[0].Params, regardless of where
        // the plain `amount`/`channel` live. The def's own action can otherwise be the minimal legal
        // ApplyResourceDelta shape; every magnitude here is overlay-driven, the same "ships with EMPTY
        // params" shape fx.overlay_damage/fx.shield_grant already use for exactly this reason (D7/D10).
        var probe = new EffectDef
        {
            EffectId = "test.dot-probe",
            EffectType = EffectTypes.Triggered,
            Name = "DoT probe",
            Triggers = new() { AtomTriggers.OnDamageDealt },
            Actions = new()
            {
                new EffectActionRow { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() },
            },
        };

        BattleEffectHost? captured = null;
        BattleEngine.Resolve(Setup(squadMaxHp: BattleRuleset.BaseHp(6) * 100, waveMaxHp: BattleRuleset.BaseHp(6) * 100),
            seed: 5, onEffectHostReady: host =>
            {
                captured = host;
                host.Bag.Catalog.Upsert(probe);
                host.Bag.Grant(new EffectGrantDto
                {
                    GrantId = "probe:dot",
                    EffectId = probe.EffectId,
                    OwnerKind = "entity",
                    OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                    PluginId = "battle",
                    Overlay = new()
                    {
                        ["amount"] = 0.0, // the instant packet itself deals nothing -- the DoT is the whole point
                        ["targetPtr"] = "wave:0",
                        ["statusId"] = "poison",
                        ["periodMs"] = 500.0,
                        ["durationMs"] = 2000.0,
                    },
                });
            });

        // Fires from both squad's own activation (targets wave:0, matches this assertion) and wave's
        // activation against squad (owner-matching dual-check, same as every other probe in this
        // file) -- but this grant's OWN overlay has no `target` override at all, so ResolveHostPtr
        // (StatusEffectBridge.cs:184-199) falls back to `ev.TargetPtr` for BOTH triggering events:
        // wave:0 when squad attacks (ev.TargetPtr = wave:0), squad:0 when wave attacks (ev.TargetPtr
        // = squad:0) -- the DoT genuinely lands on whichever side got hit that round, not always on
        // wave:0. Checking wave:0 specifically only proves squad's own contribution; that is enough to
        // prove the mechanism fires and applies for real.
        var instances = captured!.Bag.Status!.ForHost("wave:0");
        Assert.Contains(instances, i => i.StatusId == "poison");
    }

    [Fact]
    public void GrantChance_rolls_against_the_real_status_stream_not_a_fixed_default()
    {
        // EffectBag.StatusRng defaults to FixedStatusRng(0.0) (EffectBag.cs:170) -- a stream that
        // always "rolls" 0.0, i.e. ALWAYS succeeds regardless of chance, ALWAYS, on EVERY seed. T44
        // wires the real state.StatusRng in its place. Distinguishing "wired" from "still the silent
        // default" needs no exact per-seed prediction: a real stream shows a MIX of apply/no-apply
        // outcomes across many seeds at SOME chance value; a fixed always-succeed stream shows every
        // seed identical at every chance value. `chance` here is 0.1, tuned empirically: 0.5 saturated
        // to always-true and 0.01 saturated to always-false across all 20 seeds (found while building
        // this test) -- ResistanceEvaluator.cs:228 multiplies GrantChance by a power-based `pApply`
        // term (a pre-existing mechanic this module does not own or need to re-derive), so the two
        // matched-level actors here push the genuinely-mixed band down from a naive 50%, not a defect.
        // A mix, not a specific count, is the falsifiable signature of the regression this test exists
        // to catch.
        var probe = new EffectDef
        {
            EffectId = "test.chance-probe",
            EffectType = EffectTypes.Triggered,
            Name = "Chance probe",
            Triggers = new() { AtomTriggers.OnDamageDealt },
            Actions = new() { new EffectActionRow { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() } },
        };

        bool AppliedFor(ulong seed)
        {
            BattleEffectHost? captured = null;
            BattleEngine.Resolve(Setup(squadMaxHp: BattleRuleset.BaseHp(6) * 100, waveMaxHp: BattleRuleset.BaseHp(6) * 100),
                seed, onEffectHostReady: host =>
                {
                    captured = host;
                    host.Bag.Catalog.Upsert(probe);
                    host.Bag.Grant(new EffectGrantDto
                    {
                        GrantId = "probe:chance",
                        EffectId = probe.EffectId,
                        OwnerKind = "entity",
                        OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                        PluginId = "battle",
                        Overlay = new()
                        {
                            ["amount"] = 0.0, ["statusId"] = "poison", ["periodMs"] = 500.0,
                            ["durationMs"] = 2000.0, ["chance"] = 0.1,
                        },
                    });
                });
            return captured!.Bag.Status!.ForHost("wave:0").Any(i => i.StatusId == "poison")
                || captured.Bag.Status!.ForHost("squad:0").Any(i => i.StatusId == "poison");
        }

        var outcomes = Enumerable.Range(1, 20).Select(i => AppliedFor((ulong)i)).ToList();

        Assert.Contains(true, outcomes);
        Assert.Contains(false, outcomes);
    }

    [Fact]
    public void A_real_shield_grant_def_grants_a_shield_that_measurably_absorbs()
    {
        // Squad shields itself on its own hit (OnDamageDealt), then a later wave attack is partially
        // absorbed. waveAtk raised so a single hit clearly exceeds the granted shield's own capacity,
        // proving partial absorption rather than a full no-damage round that could also be explained
        // by a miss.
        var setup = Setup(squadMaxHp: BattleRuleset.BaseHp(6) * 100, waveMaxHp: BattleRuleset.BaseHp(6) * 100,
            waveAtk: BattleRuleset.BaseAtk(6) * 5);

        BattleReport Resolve(bool bindProbe) => BattleEngine.Resolve(setup, seed: 5, onEffectHostReady: host =>
        {
            if (!bindProbe) return;
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:shield-grant",
                EffectId = "fx.shield_grant",
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
                // GrantShield's own overlay allowlist (EffectProcAndOwner.cs:167-171) has no flat
                // "targetPtr" key at all -- only nested "target" (found empirically: granting with
                // "targetPtr" throws "unknown overlay key 'targetPtr' for effect actions").
                Overlay = new() { ["amount"] = 500.0, ["target"] = new Dictionary<string, object?> { ["mode"] = "single", ["ptr"] = "squad:0" } },
            });
        });

        var without = Resolve(false);
        var with = Resolve(true);

        var squadWithout = without.Actors.Single(a => a.Key == "squad:0").ShieldAbsorbed;
        var squadWith = with.Actors.Single(a => a.Key == "squad:0").ShieldAbsorbed;

        Assert.Equal(0, squadWithout); // no grant bound -> no shield -> nothing to absorb
        Assert.True(squadWith > 0, "a bound shield.grant must measurably absorb at least one hit");
    }
}
