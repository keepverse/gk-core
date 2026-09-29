using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

public class BattleEffectHostTests
{
    sealed class FakeTarget : IBattleHpTarget
    {
        public FakeTarget(long hp, long maxHp) { Hp = hp; MaxHp = maxHp; }
        public long Hp { get; set; }
        public long MaxHp { get; }
    }

    static (BattleEffectHost Host, Dictionary<string, FakeTarget> Actors) NewHost(params (string Key, long Hp, long MaxHp)[] actors)
    {
        var map = actors.ToDictionary(a => a.Key, a => new FakeTarget(a.Hp, a.MaxHp), StringComparer.Ordinal);
        var host = new BattleEffectHost(key => map.TryGetValue(key, out var t) ? t : null, rngSeed: 42);
        return (host, map);
    }

    [Fact]
    public void Heal_applies_through_the_funnel_and_caps_at_max_hp()
    {
        var (host, actors) = NewHost(("squad:0", 50, 100));
        Assert.True(host.QueueHpDelta("squad:0", 5));
        host.Flush();
        Assert.Equal(55, actors["squad:0"].Hp);

        host.QueueHpDelta("squad:0", 500);
        host.Flush();
        Assert.Equal(100, actors["squad:0"].Hp); // cap holds at MaxHp
    }

    [Fact]
    public void Regen_heals_across_rounds()
    {
        var (host, actors) = NewHost(("squad:0", 40, 100));
        for (var round = 0; round < 3; round++)
        {
            host.QueueHpDelta("squad:0", 5);
            host.Flush();
        }

        Assert.Equal(55, actors["squad:0"].Hp);
    }

    [Fact]
    public void Opposite_sign_sums_net_into_one_apply()
    {
        var (host, actors) = NewHost(("squad:0", 50, 100));
        host.QueueHpDelta("squad:0", 10);
        host.QueueHpDelta("squad:0", -4);
        host.Flush();

        Assert.Equal(56, actors["squad:0"].Hp);
        var applied = Assert.Single(host.LastApplied);
        Assert.Equal(6, applied.Amount);
        Assert.Equal(2, applied.MergedCount);
    }

    [Fact]
    public void Damage_floors_at_zero()
    {
        var (host, actors) = NewHost(("wave:0", 20, 100));
        host.QueueHpDelta("wave:0", -999);
        host.Flush();
        Assert.Equal(0, actors["wave:0"].Hp);
        Assert.Equal(-20, Assert.Single(host.LastApplied).Amount); // applied delta is the clamped one
    }

    [Fact]
    public void Hp_above_int32_max_keeps_its_long_magnitude()
    {
        const long hp = (long)int.MaxValue + 123;
        var (host, actors) = NewHost(("wave:0", hp, hp));

        Assert.True(host.QueueHpDelta("wave:0", -1));
        host.Flush();

        Assert.Equal(hp - 1, actors["wave:0"].Hp);
        Assert.Equal(-1, Assert.Single(host.LastApplied).Amount);
    }

    [Fact]
    public void Amount_cap_refuses_oversized_mutations()
    {
        // T3.5 (spec-caps-reconcile.md §2.1): AmountCap is now derived (long.MaxValue/2), not the old
        // 1e9 literal -- "one past the cap" has to be computed from the live value, not hardcoded.
        var (host, actors) = NewHost(("squad:0", 50, 100));
        Assert.False(host.QueueHpDelta("squad:0", ResourceDeltaMath.AmountCap + 1));
        host.Flush();
        Assert.Equal(50, actors["squad:0"].Hp);
        Assert.Empty(host.LastApplied);
    }

    [Fact]
    public void Unknown_target_applies_nothing()
    {
        var (host, _) = NewHost(("squad:0", 50, 100));
        host.QueueHpDelta("wave:9", -10);
        host.Flush();
        Assert.Empty(host.LastApplied);
    }

    [Fact]
    public void LastApplied_resets_each_flush_window()
    {
        var (host, _) = NewHost(("squad:0", 50, 100));
        host.QueueHpDelta("squad:0", 5);
        host.Flush();
        Assert.Single(host.LastApplied);
        host.Flush();
        Assert.Empty(host.LastApplied);
    }
}
