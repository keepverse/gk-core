using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// lawn-combat-wire L-N20: a debug-spawn HP pin is a Hub input, so it sets the base max HP and every
/// Hub max-HP bonus still lands on top. Before, the pin was re-asserted after the Hub write and
/// replaced the composed max, erasing the bonus.
/// </summary>
public class SpawnHpPinTests
{
    const string Ptr = "1A2B";

    static EntityBaseline Baseline() => new() { Hp = 300, MaxHp = 300, Atk = 20 };

    static FusionRpg.Core.Stats.Derived.ActorHub HubWithMaxHpBonus(double bonus)
    {
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new MaxHpBonus(bonus));
        return hub;
    }

    static StatContext Ctx(IReadOnlyDictionary<string, int>? absolute) =>
        new() { Side = StatSide.Zombie, EntityKey = Ptr, Baseline = Baseline(), CheatAbsolute = absolute };

    [Fact]
    public void A_pinned_entity_keeps_its_Hub_max_hp_bonus_on_top_of_the_pin()
    {
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, 5000);

        var resolved = HubWithMaxHpBonus(700).Resolve(Ctx(pins.ApplyTo(Ptr, null)));

        Assert.Equal(5000, resolved.RuntimePrimary.MaxHp);
        Assert.Equal(5700, resolved.AppliedCombat.MaxHp);
    }

    [Fact]
    public void The_pin_survives_a_reapply_that_drops_global_absolutes()
    {
        // includeAbsolute:false passes a null cheat-absolute map (EntityApply) — the pin must still apply.
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, 5000);

        var resolved = HubWithMaxHpBonus(0).Resolve(Ctx(pins.ApplyTo(Ptr, cheatAbsolute: null)));

        Assert.Equal(5000, resolved.AppliedCombat.MaxHp);
    }

    [Fact]
    public void For_its_own_ptr_the_pin_wins_over_the_global_max_hp_channel_and_keeps_other_absolutes()
    {
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, 5000);
        var global = new Dictionary<string, int> { [StatChannels.MaxHp] = 999, [StatChannels.Atk] = 77 };

        var merged = pins.ApplyTo(Ptr, global)!;

        Assert.Equal(5000, merged[StatChannels.MaxHp]);
        Assert.Equal(77, merged[StatChannels.Atk]);
        Assert.Equal(999, global[StatChannels.MaxHp]); // caller's map untouched
    }

    [Fact]
    public void An_unpinned_ptr_passes_the_absolute_map_through_unchanged()
    {
        var pins = new SpawnHpPin();
        pins.Pin("FFFF", 5000);
        var global = new Dictionary<string, int> { [StatChannels.MaxHp] = 999 };

        Assert.Same(global, pins.ApplyTo(Ptr, global));
        Assert.Null(pins.ApplyTo(Ptr, null));
    }

    [Theory]
    [InlineData("0x1a2b")]
    [InlineData("entity:1A2B")]
    [InlineData("1a2b")]
    public void Every_ptr_spelling_reads_the_same_pin(string spelling)
    {
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, 5000);
        Assert.True(pins.TryGet(spelling, out var v));
        Assert.Equal(5000, v);
    }

    [Fact]
    public void Remove_and_Clear_drop_pins_so_a_recycled_ptr_starts_clean()
    {
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, 5000);
        pins.Pin("FFFF", 10);

        pins.Remove("0x1A2B");
        Assert.False(pins.TryGet(Ptr, out _));
        Assert.True(pins.TryGet("FFFF", out _));

        pins.Clear();
        Assert.Equal(0, pins.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_pin_is_ignored(int value)
    {
        var pins = new SpawnHpPin();
        pins.Pin(Ptr, value);
        Assert.False(pins.TryGet(Ptr, out _));
    }

    [Fact]
    public void A_reapply_keeps_the_live_hp_ratio_against_the_pinned_max()
    {
        // The writer rule the removed post-write re-assert used to duplicate: a reapply source
        // preserves live current/max, rescaled onto the new composed max.
        var hp = StatSystem.CurrentHpForWrite("cheat.pushScales", liveHp: 150, previousMax: 300, composedHp: 300, newMax: 5000);
        Assert.Equal(2500, hp);
    }

    sealed class MaxHpBonus(double bonus) : IActorStatSubsystem
    {
        public string SubsystemId => "test.maxhp-bonus";
        public int Order => 100;

        public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
        {
            if (bonus != 0)
                mods.Add(new DerivedModifier(DerivedStatChannels.ProgressionBonusMaxHp, DerivedModifierOp.Flat, bonus, SourceId: SubsystemId));
        }
    }
}
