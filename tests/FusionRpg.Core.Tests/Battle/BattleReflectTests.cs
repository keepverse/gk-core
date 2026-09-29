using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// W3 (battle-derived-wire T2, audit D2): battle's direct HP apply — the basic attack, the guardian
/// share and the DoT pulse — now reaches the SAME reflect step the effect path uses. All four
/// <c>combat.reflect.*</c> families were inert in every battle before this, because
/// <c>BattleRunState.ApplyHp</c> entered at <c>DamageApplyPipeline</c>, one level below
/// <c>CombatDamageDispatcher.DispatchInstant</c>'s reflect step.
/// </summary>
public class BattleReflectTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5,
        IReadOnlyList<BattleChannelMod>? mods = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>(),
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
    };

    static BattleSetup Setup(IReadOnlyList<BattleChannelMod>? squadMods = null,
        IReadOnlyList<BattleChannelMod>? waveMods = null) => new()
    {
        WaveId = "reflect",
        Squad = new[] { Actor("squad:0", "squad", mods: squadMods) },
        Wave = new[] { Actor("wave:0", "wave", mods: waveMods) },
    };

    static readonly BattleChannelMod[] Reflector =
    {
        new(DerivedStatChannels.CombatReflectRateOmni, 1000),
        new(DerivedStatChannels.CombatReflectDamageOmni, 1000),
    };

    const long Hit = -100;

    [Fact]
    public void A_defender_with_reflect_bounces_the_hit_onto_the_attacker()
    {
        // rateDelta 1000 / ReflectRateScale 10 -> p_reflect 1.0; dmgDelta 1000 / ReflectShareScale 100
        // -> share 1.0; the post-shield applied amount is the full 100, so the bounce is exactly 100.
        var hp = BattleEngine.AttackerHpAfterApplyHpForTest(
            Setup(waveMods: Reflector), seed: 1, "wave:0", Hit, "squad:0");

        Assert.Equal(BattleRuleset.BaseHp(5) - 100, hp);
    }

    [Fact]
    public void With_reflect_at_zero_the_attacker_takes_nothing()
    {
        var hp = BattleEngine.AttackerHpAfterApplyHpForTest(
            Setup(), seed: 1, "wave:0", Hit, "squad:0");

        Assert.Equal(BattleRuleset.BaseHp(5), hp);
    }

    [Fact]
    public void A_mutual_reflect_chain_terminates_under_ProcDepthLimit()
    {
        // The termination proof (combat-damage-ssot.md §6.7a): ProcDepthLimit is the ONLY bound, and a
        // reflected packet is itself reflectable. Two reflectors must ping-pong and stop, never hang.
        var report = BattleEngine.Resolve(Setup(squadMods: Reflector, waveMods: Reflector), seed: 7);

        Assert.True(report.Rounds > 0, "the battle must terminate and report at least one round");
    }
}
