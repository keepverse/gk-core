using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.4 (spec-event-deck.md §4): an `unknown` room's pity ladder — one counter per kind
/// (`cache`/`merchant`/`fight`), checked in registry order, a hit resetting only its own counter while
/// every other kind advances, a full miss falling through to `event`. `DungeonTuningHub` is configured
/// for the whole assembly by `Dungeon.DungeonHubTestBootstrap`'s module initializer (`RestResolverTests`'s
/// own precedent), so `RungTable.Get("hard")` / `Tuning.Rungs["hard"]` (the identity row, every
/// `*MultMilli` = 1000) is a real, shipped fixture, not hand-built.</summary>
public class UnknownPityTests
{
    static readonly DungeonTuning Tuning = DungeonTuningHub.Tuning;
    static readonly DifficultyRungTuning Hard = Tuning.Rungs["hard"];

    static IReadOnlyDictionary<string, UnknownPityTuning> PityOf(long cacheBase, long cacheStep, long merchantBase, long merchantStep, long fightBase, long fightStep) =>
        new Dictionary<string, UnknownPityTuning>(StringComparer.Ordinal)
        {
            [UnknownPity.CacheKind] = new(cacheBase, cacheStep),
            [UnknownPity.MerchantKind] = new(merchantBase, merchantStep),
            [UnknownPity.FightKind] = new(fightBase, fightStep),
        };

    // Certain-cache: cache always hits at misses=0 (chance 1000‰, roll is always in [0,999]).
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> CertainCache = PityOf(1000, 0, 0, 0, 0, 0);
    // Certain-merchant: cache impossible, merchant certain -- proves registry order (cache checked and missed first).
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> CertainMerchant = PityOf(0, 0, 1000, 0, 0, 0);
    // Certain-fight: both cache and merchant impossible -- proves both are checked and missed before fight.
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> CertainFight = PityOf(0, 0, 0, 0, 1000, 0);
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> ImpossibleAll = PityOf(0, 0, 0, 0, 0, 0);
    // Real shipped values (dungeon.v1.json nodes.unknown.pity): cache 20/20, merchant 30/30, fight 100/100.
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> Shipped = Tuning.UnknownPity;

    static RoomPaletteEntry Entry(string roomId, string kind, ElementTypeId? climate) => new(roomId, kind, climate);

    // ---- Resolve: argument validation ----

    [Fact]
    public void Resolve_null_pityTuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UnknownPity.Resolve(0, 0, UnknownPityState.Empty, null!, Hard, seed: 1));
    }

    [Fact]
    public void Resolve_null_rung_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, null!, seed: 1));
    }

    // ---- Resolve: registry order and certain/impossible cases ----

    [Fact]
    public void A_certain_first_kind_always_resolves_to_cache_and_resets_only_its_own_counter()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed);
            Assert.Equal(UnknownPity.CacheKind, result.Kind);
            Assert.Equal(new UnknownPityState(0, 1, 1), result.NextPity);
        }
    }

    [Fact]
    public void A_certain_second_kind_always_resolves_to_merchant_proving_registry_order()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainMerchant, Hard, seed);
            Assert.Equal(UnknownPity.MerchantKind, result.Kind);
            Assert.Equal(new UnknownPityState(1, 0, 1), result.NextPity);
        }
    }

    [Fact]
    public void A_certain_third_kind_always_resolves_to_fight_proving_both_earlier_kinds_were_checked()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainFight, Hard, seed);
            Assert.Equal(UnknownPity.FightKind, result.Kind);
            Assert.Equal(new UnknownPityState(1, 1, 0), result.NextPity);
        }
    }

    [Fact]
    public void All_impossible_always_falls_through_to_event_and_advances_every_counter()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, ImpossibleAll, Hard, seed);
            Assert.Equal(UnknownPity.EventKind, result.Kind);
            Assert.Null(result.ArchetypeId);
            Assert.Equal(new UnknownPityState(1, 1, 1), result.NextPity);
        }
    }

    [Fact]
    public void A_hit_resets_only_its_own_kind_even_when_prior_misses_are_nonzero()
    {
        var pity = new UnknownPityState(MissesCache: 5, MissesMerchant: 3, MissesFight: 7);
        var result = UnknownPity.Resolve(0, 0, pity, CertainCache, Hard, seed: 1);
        Assert.Equal(new UnknownPityState(0, 4, 8), result.NextPity);
    }

    // ---- Resolve: the chance formula itself ----

    [Fact]
    public void Chance_may_exceed_1000_without_throwing_or_clamping()
    {
        // base 900 + step 200 * mult 1000/1000 * misses 5 = 1900‰ -- past certainty, never clamped.
        var tuning = PityOf(900, 200, 0, 0, 0, 0);
        var pity = new UnknownPityState(MissesCache: 5, MissesMerchant: 0, MissesFight: 0);
        for (ulong seed = 0; seed < 20; seed++)
            Assert.Equal(UnknownPity.CacheKind, UnknownPity.Resolve(0, 0, pity, tuning, Hard, seed).Kind);
    }

    [Fact]
    public void Overflow_on_a_pathologically_large_miss_count_throws_never_wraps()
    {
        var pity = new UnknownPityState(MissesCache: long.MaxValue, MissesMerchant: 0, MissesFight: 0);
        Assert.Throws<OverflowException>(() =>
            UnknownPity.Resolve(0, 0, pity, PityOf(1, 1, 0, 0, 0, 0), Hard, seed: 1));
    }

    [Fact]
    public void The_rungs_own_step_multiplier_scales_the_step_term()
    {
        // fight base=0, step=1000 -- at mult=1000 (hard) and misses=1, chance = 0 + 1000*1000/1000*1 = 1000 (certain).
        // At a rung with fight mult 0 instead, the SAME misses=1 gives chance = 0 (impossible), proving the
        // multiplier is actually read per-kind, not a stand-in constant.
        var tuning = PityOf(0, 0, 0, 0, 0, 1000);
        var pity = new UnknownPityState(0, 0, 1);
        var zeroFightMult = Hard with { UnknownPityStepMultMilliFight = 0 };

        for (ulong seed = 0; seed < 10; seed++)
        {
            Assert.Equal(UnknownPity.FightKind, UnknownPity.Resolve(0, 0, pity, tuning, Hard, seed).Kind);
            Assert.Equal(UnknownPity.EventKind, UnknownPity.Resolve(0, 0, pity, tuning, zeroFightMult, seed).Kind);
        }
    }

    // ---- Resolve: determinism and stream namespacing ----

    [Fact]
    public void Resolve_is_deterministic_same_seed_same_room_same_result()
    {
        var a = UnknownPity.Resolve(2, 3, UnknownPityState.Empty, Shipped, Hard, seed: 555);
        var b = UnknownPity.Resolve(2, 3, UnknownPityState.Empty, Shipped, Hard, seed: 555);
        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.NextPity, b.NextPity);
    }

    [Fact]
    public void Different_seeds_can_resolve_differently_at_a_moderate_chance()
    {
        // fight base 100 + step 100*mult1000/1000*misses 5 = 600 permille at "hard" -- neither certain nor impossible.
        var pity = new UnknownPityState(0, 0, 5);
        var kinds = new HashSet<string>();
        for (ulong seed = 0; seed < 60; seed++)
            kinds.Add(UnknownPity.Resolve(0, 0, pity, Shipped, Hard, seed).Kind);
        Assert.True(kinds.Count > 1, "expected both a hit and a fall-through across 60 seeds at a 60% chance");
    }

    [Fact]
    public void A_different_room_off_the_same_seed_can_resolve_differently()
    {
        var pity = new UnknownPityState(0, 0, 5); // fight ~60% at "hard", per the test above
        var atRoomA = new List<string>();
        var atRoomB = new List<string>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            atRoomA.Add(UnknownPity.Resolve(0, 0, pity, Shipped, Hard, seed).Kind);
            atRoomB.Add(UnknownPity.Resolve(9, 9, pity, Shipped, Hard, seed).Kind);
        }
        Assert.NotEqual(atRoomA, atRoomB);
    }

    // ---- The verify line's own headline: four parties, four independent counters ----

    [Fact]
    public void Four_parties_carry_four_independent_pity_counters()
    {
        var partyA = UnknownPityState.Empty;
        var partyB = UnknownPityState.Empty;
        var partyC = UnknownPityState.Empty;
        var partyD = UnknownPityState.Empty;

        // Party A always hits cache; party B always hits merchant; party C always hits fight;
        // party D always falls through -- four different tunings, same room, same seed range.
        for (ulong seed = 0; seed < 5; seed++)
        {
            partyA = UnknownPity.Resolve(0, 0, partyA, CertainCache, Hard, seed).NextPity;
            partyB = UnknownPity.Resolve(0, 0, partyB, CertainMerchant, Hard, seed).NextPity;
            partyC = UnknownPity.Resolve(0, 0, partyC, CertainFight, Hard, seed).NextPity;
            partyD = UnknownPity.Resolve(0, 0, partyD, ImpossibleAll, Hard, seed).NextPity;
        }

        // A hit every time on its own kind: that counter stays 0, the other two climb to 5.
        Assert.Equal(new UnknownPityState(0, 5, 5), partyA);
        Assert.Equal(new UnknownPityState(5, 0, 5), partyB);
        Assert.Equal(new UnknownPityState(5, 5, 0), partyC);
        // D missed every kind every time: all three climb to 5.
        Assert.Equal(new UnknownPityState(5, 5, 5), partyD);
    }

    // ---- Resolve + RepickArchetype (the optional `domain` parameter) ----

    [Fact]
    public void Resolve_with_no_domain_leaves_ArchetypeId_null_even_on_a_hit()
    {
        var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed: 1);
        Assert.Null(result.ArchetypeId);
    }

    [Fact]
    public void Resolve_with_a_domain_supplied_repicks_a_real_archetype_on_a_hit()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.cache-fire-001", "cache", ElementTypeId.Fire),
            Entry("room.cache-fire-002", "cache", ElementTypeId.Fire),
        });

        var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed: 1, domain);
        Assert.NotNull(result.ArchetypeId);
        Assert.Contains(result.ArchetypeId, new[] { "room.cache-fire-001", "room.cache-fire-002" });
    }

    [Fact]
    public void Repick_only_admits_the_domains_own_climate_for_a_climate_specific_kind()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.cache-fire", "cache", ElementTypeId.Fire),
            Entry("room.cache-ice", "cache", ElementTypeId.Ice),
        });

        for (ulong seed = 0; seed < 30; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed, domain);
            Assert.Equal("room.cache-fire", result.ArchetypeId);
        }
    }

    [Fact]
    public void Repick_ignores_climate_for_a_climate_neutral_kind_like_merchant()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.merchant-fire", "merchant", ElementTypeId.Fire),
            Entry("room.merchant-ice", "merchant", ElementTypeId.Ice),
        });

        var picked = new HashSet<string>();
        for (ulong seed = 0; seed < 60; seed++)
        {
            var result = UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainMerchant, Hard, seed, domain);
            picked.Add(result.ArchetypeId!);
        }
        Assert.Contains("room.merchant-fire", picked);
        Assert.Contains("room.merchant-ice", picked); // proves climate-neutral truly ignores climate, not coincidence
    }

    [Fact]
    public void Repick_throws_DelveGraphRollRejection_on_an_empty_cell()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.merchant-fire", "merchant", ElementTypeId.Fire), // no "cache" entry at all
        });

        var ex = Assert.Throws<DelveGraphRollRejection>(() =>
            UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed: 1, domain));
        Assert.Contains("cache", ex.Message);
        Assert.Contains(domain.DomainId, ex.Message);
    }

    [Fact]
    public void Repick_is_deterministic_same_seed_same_room_same_archetype()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.cache-a", "cache", ElementTypeId.Fire),
            Entry("room.cache-b", "cache", ElementTypeId.Fire),
        });

        var a = UnknownPity.Resolve(4, 5, UnknownPityState.Empty, CertainCache, Hard, seed: 900, domain);
        var b = UnknownPity.Resolve(4, 5, UnknownPityState.Empty, CertainCache, Hard, seed: 900, domain);
        Assert.Equal(a.ArchetypeId, b.ArchetypeId);
    }

    [Fact]
    public void Repick_stream_is_namespaced_by_room_like_the_event_pick()
    {
        var domain = new DomainAnchor("domain.fire-shallow-001", ElementTypeId.Fire, "shallow", new[]
        {
            Entry("room.cache-a", "cache", ElementTypeId.Fire),
            Entry("room.cache-b", "cache", ElementTypeId.Fire),
        });

        var atRoomA = new List<string?>();
        var atRoomB = new List<string?>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            atRoomA.Add(UnknownPity.Resolve(0, 0, UnknownPityState.Empty, CertainCache, Hard, seed, domain).ArchetypeId);
            atRoomB.Add(UnknownPity.Resolve(7, 7, UnknownPityState.Empty, CertainCache, Hard, seed, domain).ArchetypeId);
        }
        Assert.NotEqual(atRoomA, atRoomB);
    }
}
