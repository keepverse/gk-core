using System.Linq;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Ai;

/// <summary>aura-skill T17: each of the nine authored `ZombossPattern`s names a valid aura, "authored
/// data, tunable, no AI logic" — and two commanders running opposed auras measurably cancel in one
/// contest, the property own-side-only rests on.</summary>
public class ZombossAuraTests
{
    [Theory]
    [MemberData(nameof(AllPatternIds))]
    public void Every_pattern_names_a_valid_aura(string patternId)
    {
        var pattern = ZombossPatterns.Resolve(patternId);
        Assert.True(AuraContentCatalog.IsKnown(pattern.AuraId),
            $"pattern '{patternId}' names aura '{pattern.AuraId}', which is not in AuraContentCatalog");
    }

    public static IEnumerable<object[]> AllPatternIds() =>
        ZombossPatterns.All.Select(id => new object[] { id });

    [Fact]
    public void Zombosss_aura_resolves_from_his_active_pattern_a_bare_lookup_no_AI_logic()
    {
        var allocation = new ZombossCommanderAllocation("force-pure");
        Assert.Equal("Might", allocation.ActiveAuraId);

        allocation.SetActivePattern("bastion-pure");
        Assert.Equal("Ferocity", allocation.ActiveAuraId);
    }

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
        Key = key, Side = side, SpeciesId = "zomboss-aura-species", TypeId = 30_001, Level = level,
        MaxHp = BattleRuleset.BaseHp(level), Atk = BattleRuleset.BaseAtk(level), Defense = BattleRuleset.BaseDefense(level),
    };

    [Fact]
    public void Two_commanders_running_opposed_auras_measurably_cancel_in_one_contest()
    {
        // Dave runs Might (buffs squad's combat.power.omni); Zomboss's "bastion-pure" pattern runs
        // Ferocity, but Ferocity contests combat.crit.resist.omni, not combat.defense.omni -- for a
        // CLEAN opposition test, use force-defence-bastion-breaks-guard, whose aura is Fortitude
        // (contests Might's own grant channel exactly, per AuraContentCatalog's own closure).
        var mightRow = AuraContentCatalog.Resolve("Might");
        var fortitudeRow = AuraContentCatalog.Resolve(ZombossPatterns.Resolve("force-defence-bastion-breaks-guard").AuraId);
        Assert.Equal("Fortitude", fortitudeRow.AuraId);
        Assert.Contains(mightRow.GrantChannels.Single(), fortitudeRow.ContestChannels);

        var mightValue = AuraMagnitude.Compute(rung: 10, share: 1.0, pTheta: 1_000_000, Rung7To10(), LinearGammaTuning());
        var fortitudeValue = AuraMagnitude.Compute(rung: 10, share: 1.0, pTheta: 1_000_000, Rung7To10(), LinearGammaTuning());
        Assert.Equal(mightValue, fortitudeValue); // identical formula, identical inputs -- equal commanders

        BattleSetup SetupWithAuras(IReadOnlyList<ActiveCommanderAura> auras) => new()
        {
            WaveId = "zomboss-aura-wave",
            Squad = new[] { Actor("squad:0", "squad", 10) },
            Wave = new[] { Actor("wave:0", "wave", 10) },
            ActiveAuras = auras,
        };

        var mightOnly = BattleEngine.Resolve(
            SetupWithAuras(new[] { new ActiveCommanderAura("squad", mightRow.GrantChannels.Single(), mightValue, "aura:dave-might") }),
            seed: 11);

        var mightVsFortitude = BattleEngine.Resolve(
            SetupWithAuras(new[]
            {
                new ActiveCommanderAura("squad", mightRow.GrantChannels.Single(), mightValue, "aura:dave-might"),
                new ActiveCommanderAura("wave", fortitudeRow.GrantChannels.Single(), fortitudeValue, "aura:zomboss-fortitude"),
            }),
            seed: 11);

        var damageMightOnly = mightOnly.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);
        var damageContested = mightVsFortitude.Actors.Where(a => a.Side == "squad").Sum(a => a.DamageDealt);

        Assert.True(damageContested < damageMightOnly,
            $"Zomboss's opposed Fortitude aura ({fortitudeValue}) should measurably reduce squad's damage output ({damageMightOnly} -> {damageContested})");
    }
}
