using FusionRpg.Core.Combat;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// E28 fix #1 (spec-param-parity.md §3 row 1): the resource-pool registry for LAWN actors, keyed by
/// combat ptr rather than <c>CommanderId</c> — the same keying <c>InjectorEntityRegistry</c> already
/// uses for every other live-match lookup. Mirrors <c>CommanderResourcePoolsTests</c>' own shape one
/// keying scheme over: same wrapped <c>ActorResourcePools</c>, same "same instance every later call"
/// contract, different key type.
/// </summary>
public class LawnActorResourcePoolsTests
{
    static ActorDerivedSnapshot BaselineDerived() => ActorDerivedSnapshot.FromValues(new[]
    {
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("hp"), 100),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("stamina"), 50),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("hunger"), 100),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("spirit"), 20),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("qi"), 30),
        new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax("poise"), 10),
    });

    [Fact]
    public void Each_ptr_gets_its_own_pool_starting_at_max()
    {
        var pools = new LawnActorResourcePools();
        var actor = pools.GetOrCreate("0xABC123", BaselineDerived(), atTick: 0);

        Assert.Equal(100, actor.Resolve("hp", 0, BaselineDerived()));
        Assert.Equal(50, actor.Resolve("stamina", 0, BaselineDerived()));
        Assert.Equal(10, actor.Resolve("poise", 0, BaselineDerived()));
    }

    [Fact]
    public void GetOrCreate_returns_the_SAME_instance_on_a_later_call_for_the_same_ptr()
    {
        var pools = new LawnActorResourcePools();
        var derived = BaselineDerived();
        var first = pools.GetOrCreate("0xDEAD", derived, atTick: 0);

        first.Add("stamina", -30, nowTick: 100, derived);

        var second = pools.GetOrCreate("0xDEAD", derived, atTick: 200);

        Assert.Same(first, second); // not a fresh instance
        Assert.Equal(20, second.Resolve("stamina", 200, derived)); // 50 - 30 spent, still gone
    }

    /// <summary>Test 11 of the spec's own testing strategy shape, one layer down: two different
    /// targetPtrs never bleed into each other's pool — a drain on one actor must not touch another
    /// actor's pool of the same resource.</summary>
    [Fact]
    public void Two_different_ptrs_are_fully_independent_no_cross_actor_bleed()
    {
        var pools = new LawnActorResourcePools();
        var derived = BaselineDerived();
        var zombieA = pools.GetOrCreate("0x1111", derived, atTick: 0);
        var plantB = pools.GetOrCreate("0x2222", derived, atTick: 0);

        zombieA.Add("hp", -50, nowTick: 0, derived);

        Assert.Equal(50, zombieA.Resolve("hp", 0, derived));
        Assert.Equal(100, plantB.Resolve("hp", 0, derived)); // untouched by A's drain
    }

    /// <summary>Ptr normalization (<see cref="CombatPtr.Normalize"/>) means "0xABC", "entity:abc"
    /// and "ABC" must all resolve to the SAME pool — matching how every other Injector-side lookup
    /// keys a live actor.</summary>
    [Fact]
    public void Ptr_normalization_makes_0xABC_entity_abc_and_ABC_the_same_pool()
    {
        var pools = new LawnActorResourcePools();
        var derived = BaselineDerived();
        var viaHexPrefix = pools.GetOrCreate("0xABC", derived, atTick: 0);

        viaHexPrefix.Add("qi", -5, nowTick: 0, derived);

        var viaEntityPrefix = pools.GetOrCreate("entity:abc", derived, atTick: 0);
        var viaBare = pools.GetOrCreate("ABC", derived, atTick: 0);

        Assert.Same(viaHexPrefix, viaEntityPrefix);
        Assert.Same(viaHexPrefix, viaBare);
        Assert.Equal(25, viaBare.Resolve("qi", 0, derived));
    }

    [Fact]
    public void TryGet_reports_false_for_a_ptr_never_created()
    {
        var pools = new LawnActorResourcePools();
        Assert.False(pools.TryGet("0xNEVER", out _));
    }

    [Fact]
    public void Remove_drops_one_actors_pool_without_touching_another()
    {
        var pools = new LawnActorResourcePools();
        var derived = BaselineDerived();
        pools.GetOrCreate("0xAAA", derived, atTick: 0);
        pools.GetOrCreate("0xBBB", derived, atTick: 0);

        var removed = pools.Remove("0xAAA");

        Assert.True(removed);
        Assert.False(pools.TryGet("0xAAA", out _));
        Assert.True(pools.TryGet("0xBBB", out _));
        Assert.Equal(1, pools.Count);
    }

    [Fact]
    public void Clear_drops_every_pool()
    {
        var pools = new LawnActorResourcePools();
        var derived = BaselineDerived();
        pools.GetOrCreate("0xAAA", derived, atTick: 0);
        pools.GetOrCreate("0xBBB", derived, atTick: 0);

        pools.Clear();

        Assert.Equal(0, pools.Count);
        Assert.False(pools.TryGet("0xAAA", out _));
    }

    // ------------------------------------------------------------------------------------------
    // `basic-attack-cost` T12a (spec-basic-attack-cost.md wire 2, the anti-silent-inert check):
    // `resource.max.stamina` is 0 for every lawn actor UNLESS the injector's Hub opts into
    // `seedResourceBaseline: true` (`ActorHub.cs:147`, sole prior `true` caller
    // `UniqueActorHubCompose.cs:75`). CheatState.ActorHub is where the injector must now pass that
    // flag too — these tests pin the SAME mechanism `ActorHubTests.SeedResourceBaseline_sets_
    // resource_max_for_all_six_ids` already proves generically, framed against T12's own falsifier
    // (a lawn-shaped plant AND zombie actor, through the real ambient tuning fixture this whole
    // assembly runs under — `ContractTuningTestBootstrap.DefaultBattleResources` already carries the
    // real `poolShareMilli.stamina = 500`, unchanged since v1, so this needs no ambient mutation).
    [Fact]
    public void SeedResourceBaseline_gives_a_lawn_plant_a_non_zero_resource_max_stamina()
    {
        var hub = ActorHubBootstrap.CreateDefault(
            powerIndex: new FixedPowerIndexProvider(theta: 1), seedResourceBaseline: true);
        var ctx = hub.Stats.Contexts.ForPlant(
            "0xP1", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 }, typeId: 1);

        var derived = hub.ResolveDerived(ctx);
        var max = ResourceChannelReader.Max(derived, "stamina");

        Assert.True(max > 0, $"resource.max.stamina must be non-zero for a real lawn actor once " +
            $"seedResourceBaseline is wired -- 0 is indistinguishable from the bug this feature fixes. Got {max}.");

        // The pool itself resolves at that same max the moment it is created -- the actual
        // ActorResourcePools instance a lawn cost charge would read from.
        var pools = new LawnActorResourcePools();
        var actor = pools.GetOrCreate("0xP1", derived, atTick: 0);
        Assert.Equal(max, actor.Resolve("stamina", 0, derived));
    }

    /// <summary>Zombies get the SAME Hub-seeded pool as plants — `LawnActorResourcePools` is keyed by
    /// combat ptr with no side distinction at all (its own class doc), and `BasicAttackGrantBuilder`
    /// (T10) binds the basic-attack grant to "every spawned lawn actor, plant and zombie" — so a
    /// zombie actor resolved through the SAME Hub must show the SAME non-zero stamina max, not an
    /// exemption. This is the acceptance criterion's own falsifier for "zombies get pools too."</summary>
    [Fact]
    public void SeedResourceBaseline_gives_a_lawn_zombie_the_same_non_zero_resource_max_stamina()
    {
        var hub = ActorHubBootstrap.CreateDefault(
            powerIndex: new FixedPowerIndexProvider(theta: 1), seedResourceBaseline: true);
        var plantCtx = hub.Stats.Contexts.ForPlant(
            "0xP2", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 }, typeId: 1);
        var zombieCtx = hub.Stats.Contexts.ForZombie(
            "0xZ1", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 }, typeId: 1);

        var plantMax = ResourceChannelReader.Max(hub.ResolveDerived(plantCtx), "stamina");
        var zombieMax = ResourceChannelReader.Max(hub.ResolveDerived(zombieCtx), "stamina");

        Assert.True(zombieMax > 0, $"a zombie actor must also resolve a non-zero resource.max.stamina. Got {zombieMax}.");
        Assert.Equal(plantMax, zombieMax); // same Hub, same baseline shape -- no side-based exemption
    }

    /// <summary>lawn-combat-wire L-N18: holding a pool is half of T12's zombie criterion; this is the
    /// spend half — a zombie's lawn pool pays a stamina cost and refuses one it cannot afford, exactly
    /// like a plant's.</summary>
    [Fact]
    public void A_lawn_zombie_pool_spends_stamina_and_refuses_an_unaffordable_cost()
    {
        var hub = ActorHubBootstrap.CreateDefault(
            powerIndex: new FixedPowerIndexProvider(theta: 1), seedResourceBaseline: true);
        var zombieCtx = hub.Stats.Contexts.ForZombie(
            "0xZ2", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 }, typeId: 1);
        var derived = hub.ResolveDerived(zombieCtx);
        var max = ResourceChannelReader.Max(derived, "stamina");
        Assert.True(max > 0);

        var pools = new LawnActorResourcePools();
        var zombie = pools.GetOrCreate("0xZ2", derived, atTick: 0);

        Assert.True(zombie.TrySpend("stamina", max, nowTick: 0, derived));
        Assert.Equal(0, zombie.Resolve("stamina", 0, derived));
        Assert.False(zombie.TrySpend("stamina", 1, nowTick: 0, derived));
    }

    /// <summary>The negative control this whole feature exists to distinguish itself from: a bare
    /// `CreateDefault` (no `seedResourceBaseline`) is exactly today's bug — max reads 0. Kept here,
    /// alongside the two tests above, so a reader sees both sides of the "0 is the bug, non-zero is
    /// the fix" contrast in one file.</summary>
    [Fact]
    public void Bare_CreateDefault_without_seedResourceBaseline_still_reads_zero_stamina_max()
    {
        var hub = ActorHubBootstrap.CreateDefault(powerIndex: new FixedPowerIndexProvider(theta: 1));
        var ctx = hub.Stats.Contexts.ForPlant("0xP3", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 });

        var max = ResourceChannelReader.Max(hub.ResolveDerived(ctx), "stamina");

        Assert.Equal(0, max);
    }
}
