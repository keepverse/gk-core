using System.Runtime.CompilerServices;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// lawn-combat-wire L-N28, owner decision 2026-09-15: battle stamina regen is ON. T11's stamina row
/// reaches battle through the <c>ResourceBaselineSubsystem</c> that <c>BattleHubCompose</c> registers;
/// <c>resource-hub-ssot.md</c> §11 is amended to say so. These tests read the real shipped
/// <c>battle-resources.v2.json</c> through an explicit tuning instance (never the assembly's all-zero
/// ambient fixture, never by mutating it), so the battle behaviour the owner ruled on is actually covered.
/// </summary>
public class BattleStaminaRegenTests
{
    const int Theta = 20;

    static BattleResourceTuning RealV2([CallerFilePath] string here = "") =>
        BattleResourceTuningLoader.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "..", "..", "data", "tuning", "battle-resources.v2.json"))));

    static ActorDerivedSnapshot BattleActorDerived(BattleResourceTuning tuning)
    {
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new ResourceBaselineSubsystem(new FixedPowerIndexProvider(Theta), tuning));
        return hub.ResolveDerived(new StatContext
        {
            Side = StatSide.Plant,
            EntityKey = "squad-0",
            Baseline = new EntityBaseline { Hp = BattleRuleset.BaseHp(Theta), MaxHp = BattleRuleset.BaseHp(Theta) },
        });
    }

    [Fact]
    public void Battle_compose_registers_the_shared_resource_baseline_seam()
    {
        var derived = BattleHubCompose.Compose(new BattleActorSetup { Level = Theta, Atk = 10, Defense = 0, MaxHp = 680 });
        Assert.True(ResourceChannelReader.Max(derived, "stamina") > 0,
            "battle must seed resource.max.stamina through ResourceBaselineSubsystem — the seam the regen row rides");
    }

    [Fact]
    public void With_the_real_v2_row_a_battle_actor_regenerates_stamina_and_nothing_else()
    {
        var tuning = RealV2();
        var derived = BattleActorDerived(tuning);

        Assert.True(ResourceChannelReader.RegenPerMilleTick(derived, "stamina") > 0);
        foreach (var id in new[] { "hp", "poise", "hunger", "spirit", "qi" })
            Assert.Equal(0, ResourceChannelReader.RegenPerMilleTick(derived, id));
    }

    [Fact]
    public void A_battle_pool_spends_stamina_and_recovers_it_mid_encounter()
    {
        var tuning = RealV2();
        var derived = BattleActorDerived(tuning);
        var max = ResourceChannelReader.Max(derived, "stamina");
        var ratePerMilleTick = ResourceChannelReader.RegenPerMilleTick(derived, "stamina");
        Assert.True(ratePerMilleTick > 0);

        // BattleRunState.ResourcePools is a LawnActorResourcePools — the same type exercised here.
        var pools = new LawnActorResourcePools();
        var actor = pools.GetOrCreate("squad-0", derived, atTick: 0);
        Assert.True(actor.TrySpend("stamina", max, nowTick: 0, derived));
        Assert.Equal(0, actor.Resolve("stamina", 0, derived));

        const long ticks = 50;
        var recovered = actor.Resolve("stamina", ticks, derived);

        Assert.True(recovered > 0, "a spent battle pool must recover stamina before the encounter ends");
        Assert.Equal(Math.Min(max, checked(ticks * ratePerMilleTick) / 1000), recovered);
    }
}
