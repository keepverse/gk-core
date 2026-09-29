using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.17 (spec-delve-attrition.md §2) — `PartyPoolsCarry`: `ActorResourcePools.FromStored`
/// in, `SettleAll` out, looping over `ResourceIds`, with the `hp == pools["hp"]` assertion.</summary>
public class PartyPoolsCarryTests
{
    // A real snapshot with non-zero max/regen for all six ids, so SettleAll's own clamp/regen math is
    // exercised meaningfully rather than degenerating to zero-everywhere.
    static ActorDerivedSnapshot Snapshot(long max = 1000, long regenPerTick = 0) =>
        ActorDerivedSnapshot.FromValues(DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), max),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), regenPerTick),
        }));

    static IReadOnlyDictionary<string, long> FullPools(long value = 1000) =>
        DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => value, StringComparer.Ordinal);

    // ---- SplitForCarryIn ----

    [Fact]
    public void SplitForCarryIn_separates_hp_from_the_five_others()
    {
        var pools = FullPools() as Dictionary<string, long> ?? new Dictionary<string, long>(FullPools());
        pools["hp"] = 700;
        pools["stamina"] = 300;

        var (hp, rest) = PartyPoolsCarry.SplitForCarryIn(pools);

        Assert.Equal(700, hp);
        Assert.Equal(300, rest["stamina"]);
        Assert.DoesNotContain("hp", rest.Keys);
        Assert.Equal(5, rest.Count); // every id except hp, never a hard-coded 5 independent of the list
    }

    [Fact]
    public void SplitForCarryIn_covers_every_id_in_ResourceIds_never_a_literal_list()
    {
        var pools = FullPools();
        var (_, rest) = PartyPoolsCarry.SplitForCarryIn(pools);
        Assert.Equal(DerivedStatChannels.ResourceIds.Where(id => id != "hp").OrderBy(x => x), rest.Keys.OrderBy(x => x));
    }

    [Fact]
    public void SplitForCarryIn_missing_any_one_id_throws_by_name()
    {
        foreach (var missing in DerivedStatChannels.ResourceIds)
        {
            var pools = new Dictionary<string, long>(FullPools());
            pools.Remove(missing);
            var ex = Assert.Throws<ArgumentException>(() => PartyPoolsCarry.SplitForCarryIn(pools));
            Assert.Contains(missing, ex.Message);
        }
    }

    [Fact]
    public void SplitForCarryIn_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => PartyPoolsCarry.SplitForCarryIn(null!));
    }

    // ---- BuildForBattle / CarryOut round trip ----

    [Fact]
    public void BuildForBattle_then_CarryOut_round_trips_every_pool_unchanged_at_the_same_tick()
    {
        var (hp, carryIn) = PartyPoolsCarry.SplitForCarryIn(FullPools(800));
        var pools = PartyPoolsCarry.BuildForBattle(hp, carryIn, atTick: 0);

        var settled = PartyPoolsCarry.CarryOut(pools, hpRemaining: 800, atTick: 0, Snapshot());

        foreach (var id in DerivedStatChannels.ResourceIds)
            Assert.Equal(800, settled[id]);
    }

    [Fact]
    public void CarryOut_throws_when_hpRemaining_and_pools_hp_disagree()
    {
        var (hp, carryIn) = PartyPoolsCarry.SplitForCarryIn(FullPools(800));
        var pools = PartyPoolsCarry.BuildForBattle(hp, carryIn, atTick: 0);

        // The verify line's own headline: "the assertion fires when hp is written twice" -- here, the
        // pool's own hp seat (800, from BuildForBattle) and a battle result CLAIMING a different
        // HpRemaining (750) are two disagreeing writes to the same seat.
        var ex = Assert.Throws<InvalidOperationException>(() => PartyPoolsCarry.CarryOut(pools, hpRemaining: 750, atTick: 0, Snapshot()));
        Assert.Contains("750", ex.Message);
        Assert.Contains("800", ex.Message);
    }

    [Fact]
    public void CarryOut_agrees_when_hp_was_actually_spent_from_the_pool_itself()
    {
        var (hp, carryIn) = PartyPoolsCarry.SplitForCarryIn(FullPools(800));
        var pools = PartyPoolsCarry.BuildForBattle(hp, carryIn, atTick: 0);

        pools.TrySpend("hp", 300, nowTick: 0, Snapshot()); // the pool itself now holds 500

        var settled = PartyPoolsCarry.CarryOut(pools, hpRemaining: 500, atTick: 0, Snapshot());
        Assert.Equal(500, settled["hp"]);
    }

    [Fact]
    public void CarryOut_null_arguments_throw()
    {
        var pools = PartyPoolsCarry.BuildForBattle(800, FullPools(800), 0);
        Assert.Throws<ArgumentNullException>(() => PartyPoolsCarry.CarryOut(null!, 800, 0, Snapshot()));
        Assert.Throws<ArgumentNullException>(() => PartyPoolsCarry.CarryOut(pools, 800, 0, null!));
    }

    // ---- the verify line's own headline: a carry test across three rooms ----

    [Fact]
    public void Pools_carry_correctly_across_three_consecutive_rooms()
    {
        // Room 1: full pools, a battle spends some of everything.
        var (hp1, carryIn1) = PartyPoolsCarry.SplitForCarryIn(FullPools(1000));
        var room1Pools = PartyPoolsCarry.BuildForBattle(hp1, carryIn1, atTick: 0);
        foreach (var id in DerivedStatChannels.ResourceIds) room1Pools.TrySpend(id, 100, 0, Snapshot());
        var afterRoom1 = PartyPoolsCarry.CarryOut(room1Pools, hpRemaining: 900, atTick: 0, Snapshot());
        Assert.All(DerivedStatChannels.ResourceIds, id => Assert.Equal(900, afterRoom1[id]));

        // Room 2: carries IN exactly what room 1 carried out -- "between rooms no tick advances, so
        // the next room's Resolve at tick 0 returns stored exactly" (spec §2, verbatim).
        var (hp2, carryIn2) = PartyPoolsCarry.SplitForCarryIn(afterRoom1);
        Assert.Equal(900, hp2);
        var room2Pools = PartyPoolsCarry.BuildForBattle(hp2, carryIn2, atTick: 0);
        foreach (var id in DerivedStatChannels.ResourceIds) room2Pools.TrySpend(id, 200, 0, Snapshot());
        var afterRoom2 = PartyPoolsCarry.CarryOut(room2Pools, hpRemaining: 700, atTick: 0, Snapshot());
        Assert.All(DerivedStatChannels.ResourceIds, id => Assert.Equal(700, afterRoom2[id]));

        // Room 3: same pattern again -- the whole chain is a pure fold, no hidden state anywhere.
        var (hp3, carryIn3) = PartyPoolsCarry.SplitForCarryIn(afterRoom2);
        Assert.Equal(700, hp3);
        var room3Pools = PartyPoolsCarry.BuildForBattle(hp3, carryIn3, atTick: 0);
        var afterRoom3 = PartyPoolsCarry.CarryOut(room3Pools, hpRemaining: 700, atTick: 0, Snapshot()); // no spend this room
        Assert.All(DerivedStatChannels.ResourceIds, id => Assert.Equal(700, afterRoom3[id]));
    }

    [Fact]
    public void A_partially_drained_start_carries_in_exactly_as_supplied_never_reset_to_max()
    {
        var mixed = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["hp"] = 550, ["stamina"] = 20, ["hunger"] = 610, ["spirit"] = 999, ["qi"] = 0, ["poise"] = 340,
        };

        var (hp, carryIn) = PartyPoolsCarry.SplitForCarryIn(mixed);
        var pools = PartyPoolsCarry.BuildForBattle(hp, carryIn, atTick: 0);
        var settled = PartyPoolsCarry.CarryOut(pools, hpRemaining: 550, atTick: 0, Snapshot());

        foreach (var (id, value) in mixed)
            Assert.Equal(value, settled[id]);
    }
}
