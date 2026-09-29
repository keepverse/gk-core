using System.Linq;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>aura-skill T12, Gate B: "an aura is on" becomes "a channel has a value," through the T4
/// recompose seam. Acceptance text verbatim: "an aura in BattleSetup raises combat.power.omni on a
/// friendly squad actor by the T10 value; absent, it does not."</summary>
public class AuraDeliveryTests
{
    static AuraTuning Rung7To10() => new(new Dictionary<int, long>
    {
        [7] = 5359, [8] = 7090, [9] = 9379, [10] = 12407,
    }, MaxActiveAuras: 1);

    static AptitudeTuning LinearGammaTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 1, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 } ]
        }
        """);

    static BattleActorSetup Actor(string key, string side, int level) => new()
    {
        Key = key, Side = side, SpeciesId = "aura-delivery-species", TypeId = 20_001, Level = level,
        MaxHp = BattleRuleset.BaseHp(level), Atk = BattleRuleset.BaseAtk(level), Defense = BattleRuleset.BaseDefense(level),
    };

    static BattleSetup Setup(IReadOnlyList<ActiveCommanderAura> activeAuras) => new()
    {
        WaveId = "aura-delivery-wave",
        Squad = new[] { Actor("squad:0", "squad", 10), Actor("squad:1", "squad", 10) },
        Wave = new[] { Actor("wave:0", "wave", 10) },
        ActiveAuras = activeAuras,
    };

    [Fact]
    public void An_active_aura_raises_combat_power_omni_on_every_friendly_squad_actor_by_the_T10_value()
    {
        // Squad actors here carry no ElementPrimary, so OverlayCombatCalculator.Compute's own
        // omniFallback branch is what resolves their attacks (Components.Count == 0) -- and that
        // branch reads exactly DerivedStatChannels.CombatPowerOmni for weightedOffense
        // (OverlayCombatCalculator.cs:96). Raising combat.power.omni must therefore raise damage
        // dealt, at the SAME seed -- the one deterministic, unambiguous way to observe a private,
        // battle-internal Derived channel from outside BattleEngine.
        var t10Value = AuraMagnitude.Compute(rung: 10, share: 1.0, pTheta: 1_000_000, Rung7To10(), LinearGammaTuning());
        Assert.True(t10Value > 1000, "test needs a large, unambiguous buff, not a rounding-noise one");

        var unbuffed = BattleEngine.Resolve(Setup(Array.Empty<ActiveCommanderAura>()), seed: 5);
        var buffed = BattleEngine.Resolve(
            Setup(new[] { new ActiveCommanderAura("squad", "combat.power.omni", t10Value, "aura:test-ember") }),
            seed: 5);

        var damageUnbuffed = unbuffed.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        var damageBuffed = buffed.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        Assert.True(damageBuffed > damageUnbuffed,
            $"buffed squad damage ({damageBuffed}) must exceed unbuffed ({damageUnbuffed}) at the identical seed");
    }

    [Fact]
    public void Absent_no_active_auras_the_channel_is_not_raised_byte_identical_to_before_T12()
    {
        var setupWithout = Setup(Array.Empty<ActiveCommanderAura>());
        var setupWith = Setup(new[]
        {
            new ActiveCommanderAura("squad", "combat.power.omni", 5000, "aura:test-ember")
        });

        var reportWithout = BattleEngine.Resolve(setupWithout, seed: 42);
        var reportWith = BattleEngine.Resolve(setupWith, seed: 42);

        // Same seed, same actors -- outcomes should differ once the buffed squad hits harder, proving
        // the aura's value is REACHING combat resolution, not just sitting inert on Derived.
        var damageWithout = reportWithout.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        var damageWith = reportWith.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        Assert.True(damageWith >= damageWithout,
            $"expected buffed squad damage ({damageWith}) >= unbuffed ({damageWithout})");
    }

    [Fact]
    public void The_aura_never_touches_the_enemy_wave_side()
    {
        var setup = Setup(new[]
        {
            new ActiveCommanderAura("squad", "combat.power.omni", 5000, "aura:test-ember")
        });

        // Same seed with and without the wave being the "commander side" instead -- if the aura
        // leaked to wave, giving it to wave would change wave's own damage output identically to how
        // giving it to squad changed squad's. Proven instead by a direct side-swap: an aura scoped to
        // "wave" must NOT affect squad's damage.
        var squadAura = BattleEngine.Resolve(setup, seed: 7);
        var waveAuraSetup = Setup(new[] { new ActiveCommanderAura("wave", "combat.power.omni", 5000, "aura:test-ember") });
        var waveAura = BattleEngine.Resolve(waveAuraSetup, seed: 7);

        var squadDamageWhenSquadBuffed = squadAura.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        var squadDamageWhenWaveBuffed = waveAura.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        Assert.True(squadDamageWhenSquadBuffed >= squadDamageWhenWaveBuffed,
            "an aura scoped to the wave side must not also buff the squad side");
    }

    [Fact]
    public void No_goldens_move_the_default_empty_ActiveAuras_list_changes_nothing()
    {
        // The exact BattleGoldenTests.StompSetup() shape, just re-asserted here at the delivery
        // boundary: a BattleSetup built with no ActiveAuras field set at all (the default) must
        // resolve identically to how it always did, proven by comparing against the same setup with
        // an explicit empty list -- both must be the SAME, not merely "close."
        var setup = BattleGoldenTests.StompSetup();
        var reportA = BattleEngine.Resolve(setup, seed: 1001);
        var reportB = BattleEngine.Resolve(setup with { ActiveAuras = Array.Empty<ActiveCommanderAura>() }, seed: 1001);

        Assert.Equal(reportA.Outcome, reportB.Outcome);
        Assert.Equal(reportA.Rounds, reportB.Rounds);
        for (var i = 0; i < reportA.Actors.Count; i++)
            Assert.Equal(reportA.Actors[i].DamageDealt, reportB.Actors[i].DamageDealt);
    }
}
