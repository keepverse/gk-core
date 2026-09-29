using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// battle-derived-wire T3 (W13): a positive <c>BattleRunState.ApplyHp</c> — battle's own direct/trait
/// heal path, the one `regenerator`, `immortal` and `soul-eater` call — now reaches the healer's
/// <c>resource.restore.hp</c>, the same term <c>OverlayCombatMath.FinalizeHeal</c> already adds on the
/// bag's effect path. The DoT-pulse half landed in solid-remediation T2.6; this is the apply half.
/// </summary>
public class BattleHealPowerTests
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
        Defense = BattleRuleset.BaseDefense(level)
    };

    static BattleSetup Setup(IReadOnlyList<BattleChannelMod>? mods = null) => new()
    {
        WaveId = "heal-power",
        Squad = new[] { Actor("squad:0", "squad", mods: mods) },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    const long BaseHeal = 50;

    [Fact]
    public void Heal_power_adds_to_a_battle_ApplyHp()
    {
        var setup = Setup(new[] { new BattleChannelMod(DerivedStatChannels.ResourceRestore("hp"), 20) });

        Assert.Equal(BaseHeal + 20, BattleEngine.ApplyHpForTest(setup, seed: 1, "squad:0", BaseHeal));
    }

    [Fact]
    public void Zero_heal_power_heals_exactly_the_authored_amount()
    {
        Assert.Equal(BaseHeal, BattleEngine.ApplyHpForTest(Setup(), seed: 1, "squad:0", BaseHeal));
    }

    [Fact]
    public void Heal_never_negative()
    {
        var setup = Setup(new[] { new BattleChannelMod(DerivedStatChannels.ResourceRestore("hp"), -1000) });

        Assert.Equal(0, BattleEngine.ApplyHpForTest(setup, seed: 1, "squad:0", BaseHeal));
    }

    [Fact]
    public void Battle_heal_is_the_one_OverlayCombatMath_formula()
    {
        // The "no second formula" claim made falsifiable: battle's ApplyHp result equals a direct
        // OverlayCombatMath.Finalize call against the SAME composed snapshot, not a re-derived number.
        var setup = Setup(new[] { new BattleChannelMod(DerivedStatChannels.ResourceRestore("hp"), 20) });
        var snapshot = BattleHubCompose.Compose(setup.Squad[0]);
        var expected = OverlayCombatMath
            .Create((_, _) => new CombatActorSnapshot(snapshot, ActorElementTypes.Neutral))
            .Finalize(BaseHeal, "squad:0", new DamagePacket { ActorPtr = "squad:0", SignedAmount = BaseHeal }, null);

        Assert.Equal(expected, BattleEngine.ApplyHpForTest(setup, seed: 1, "squad:0", BaseHeal));
    }
}
