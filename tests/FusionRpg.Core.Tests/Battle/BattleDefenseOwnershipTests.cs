using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// W11 (battle-derived-wire T6, audit §4.1): one gate owns <c>combat.defense.omni</c>. A status's own
/// <c>defense</c> StatMod, a <c>stat.modify</c> on <c>defense</c>, and an aura contribution on
/// <c>combat.defense.omni</c> all reach the composed value, all survive the per-round
/// <c>Recompose</c> (idempotent by construction), and none clobbers another across a round boundary.
/// </summary>
public class BattleDefenseOwnershipTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "defense-ownership",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    static long BaseDefense => BattleRuleset.BaseDefense(5);

    [Fact]
    public void No_source_leaves_the_composed_defense_at_the_base()
    {
        Assert.Equal(BaseDefense, BattleEngine.ComposedDefenseForTest(Setup(), seed: 1, "squad:0"));
    }

    [Fact]
    public void A_status_defense_mod_reaches_the_composed_value()
    {
        Assert.Equal(BaseDefense + 10,
            BattleEngine.ComposedDefenseForTest(Setup(), seed: 1, "squad:0", statusDefenseFlat: 10));
    }

    [Fact]
    public void A_status_mod_and_a_stat_modify_sum_and_survive_two_recomposes()
    {
        // The status's own mod used to be stored in BattleStatModifierLedger and never read; the
        // stat.modify push used to write the channel absolutely, so the two could not coexist. One
        // writer, one projection source: base + 10 + 20, twice recomposed.
        Assert.Equal(BaseDefense + 30,
            BattleEngine.ComposedDefenseForTest(Setup(), seed: 1, "squad:0",
                statusDefenseFlat: 10, statModifyDefenseFlat: 20));
    }

    [Fact]
    public void An_aura_and_a_stat_modify_do_not_clobber_each_other_across_a_round_boundary()
    {
        // The latent conflict audit §4.1 named: an aura targeting `combat.defense.omni` would have
        // had its value overwritten by the stat.modify absolute push (or vice versa). Now both live in
        // the same ledger, so the per-round recompose sums them.
        Assert.Equal(BaseDefense + 60,
            BattleEngine.ComposedDefenseForTest(Setup(), seed: 1, "squad:0",
                statModifyDefenseFlat: 20, auraContribution: 40));
    }

    [Fact]
    public void All_three_sources_coexist_under_the_one_gate()
    {
        Assert.Equal(BaseDefense + 70,
            BattleEngine.ComposedDefenseForTest(Setup(), seed: 1, "squad:0",
                statusDefenseFlat: 10, statModifyDefenseFlat: 20, auraContribution: 40));
    }
}
