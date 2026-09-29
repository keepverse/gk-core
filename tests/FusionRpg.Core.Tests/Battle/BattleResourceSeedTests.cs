using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `battle-tempo` `battle-resources` (spec-battle-resources.md) — every battle actor used to hold all
/// six resource pools at max 0, because <see cref="BattleStatComposer"/> seeded no `resource.*`
/// channel. These prove the seed exists, is derived rather than hand-listed, and is inert.
/// </summary>
public class BattleResourceSeedTests
{
    static BattleActorSetup Actor(string key = "squad:0", int level = 5, long maxHp = 0) => new()
    {
        Key = key,
        Side = "squad",
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = maxHp > 0 ? maxHp : BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        ChannelMods = Array.Empty<BattleChannelMod>()
    };

    /// <summary>
    /// §6.2 — the expected set is DERIVED from `ResourceIds`, never enumerated here, so a seventh
    /// resource is covered by construction and a hand-listed regression fails loudly. This is
    /// `resource-hub-ssot.md` §8's six-coverage rule expressed as a test.
    /// </summary>
    [Fact]
    public void EverySeededResourceChannelIsDerivedFromResourceIds()
    {
        var snap = BattleHubCompose.Compose(Actor());

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            var maxCh = DerivedStatChannels.ResourceMax(id);
            var regenCh = DerivedStatChannels.ResourceRegen(id);

            Assert.True(snap.Get(maxCh) > 0, $"{maxCh} must be seeded above zero — an empty pool is the defect this module removes");
            Assert.Equal(0, (long)snap.Get(regenCh));   // §2.5: regen is a structural zero
        }
    }

    /// <summary>§2.6 — `hp`'s max mirrors the actor's own MaxHp rather than deriving a second,
    /// disagreeing HP maximum.</summary>
    [Fact]
    public void HpMaxMirrorsSetupMaxHpRatherThanDerivingASecondNumber()
    {
        var setup = Actor(maxHp: 4321);
        var snap = BattleHubCompose.Compose(setup);
        Assert.Equal(4321, (long)snap.Get(DerivedStatChannels.ResourceMax("hp")));
    }

    /// <summary>§2.2 — a projection of the shipped ladder, so the pool tracks `BaseHp` exactly rather
    /// than following a private curve of its own.</summary>
    [Fact]
    public void PoolMaxIsAPerMilleShareOfTheHpLadder()
    {
        const int share = 500;   // battle-resources.v1.json poolShareMilli.poise
        foreach (var theta in new[] { 1, 5, 20, 100 })
        {
            var snap = BattleHubCompose.Compose(Actor(level: theta));
            var expected = BattleRuleset.BaseHp(theta) * share / 1000;
            Assert.Equal(expected, (long)snap.Get(DerivedStatChannels.ResourceMax("poise")));
        }
    }

    /// <summary>
    /// The whole point: a pool built from a seeded snapshot can actually afford something.
    /// Before this module <see cref="ActorResourcePools.CreateFull"/> produced a 0-capacity pool and
    /// every <see cref="ActorResourcePools.TrySpend"/> refused.
    /// </summary>
    [Fact]
    public void APoolBuiltFromASeededSnapshotCanAffordTheShippedPoiseSpend()
    {
        const long poiseSpend = 100;   // reaction-lane.v1.json poiseSpend
        var snap = BattleHubCompose.Compose(Actor(level: 20));
        var pools = ActorResourcePools.CreateFull(snap, atTick: 0);

        Assert.True(pools.Resolve("poise", 0, snap) >= poiseSpend,
            "a pinned actor must afford at least one counter — this is the assertion that was impossible before the seed");
        Assert.True(pools.TrySpend("poise", poiseSpend, 0, snap));
    }

    /// <summary>
    /// ⭐ §6.5a — the regen-cliff falsifier, and the most load-bearing test here. It proves the
    /// structural zero in <see cref="BattleRuleset.BaseResourceRegen"/> is a DECISION: set regen to
    /// a whole unit per tick and a single round refills far more than a counter costs, erasing the
    /// scarcity the pool exists to create.
    ///
    /// <para>S10.1 (2026-09-13) changed what "1/tick" <i>is</i> relative to the floor: it used to be
    /// the smallest rate that could be expressed at all, and is now 1000× the smallest. The cliff it
    /// demonstrates is unchanged — a whole unit per tick still refills a round's worth — but it is no
    /// longer the only alternative to zero. See <c>ResourceSubTickRegenTests</c>.</para>
    /// </summary>
    [Fact]
    public void TheSmallestNonZeroRegenWouldRefillFasterThanASpendDrains()
    {
        const long poiseSpend = 100;
        const long ticksPerRound = 300;   // action-timing.v1.json: basic attack alone is 150 + 50

        var seeded = BattleHubCompose.Compose(Actor(level: 20));

        // Same actor, but with regen forced to a whole unit per tick.
        var withRegen = ActorDerivedSnapshot.FromValues(
            DerivedStatChannels.ResourceIds
                .Select(id => new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), seeded.Get(DerivedStatChannels.ResourceMax(id))))
                .Concat(DerivedStatChannels.ResourceIds
                    .Select(id => new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), 1))));

        Assert.Equal(1000, ResourceChannelReader.RegenPerMilleTick(withRegen, "poise"));   // 1 unit/tick == 1000 per-mille/tick

        var pools = ActorResourcePools.CreateFull(withRegen, atTick: 0);
        Assert.True(pools.TrySpend("poise", poiseSpend, 0, withRegen));

        var afterOneRound = pools.Resolve("poise", ticksPerRound, withRegen);
        var max = ResourceChannelReader.Max(withRegen, "poise");

        Assert.True(ticksPerRound > poiseSpend,
            "the cliff: one round accrues more than a whole spend, so scarcity cannot survive any non-zero rate");
        Assert.Equal(max, afterOneRound);   // fully topped back up within a single round

        // And the shipped configuration does NOT do this.
        Assert.Equal(0, ResourceChannelReader.RegenPerMilleTick(seeded, "poise"));
    }

    /// <summary>§2.7 — the seed must be inert on the shipped path. Resource channels never join the
    /// combat roster, so nothing the resolver reads can see them.</summary>
    [Fact]
    public void SeededResourceChannelsAreNotInTheCombatRoster()
    {
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.DoesNotContain(DerivedStatChannels.ResourceMax(id), DerivedStatChannels.AllCombatChannelIds);
            Assert.DoesNotContain(DerivedStatChannels.ResourceRegen(id), DerivedStatChannels.AllCombatChannelIds);
        }
    }

    /// <summary>BR1 — a missing share is a rejection naming it, never a silent default. A silently
    /// absent share would make exactly one pool max 0, which is the original defect wearing a
    /// different hat.</summary>
    [Fact]
    public void AMissingPoolShareIsRejectedByName()
    {
        var json = """
        { "schemaVersion": 1, "version": 1,
          "poolShareMilli": { "stamina": 500, "hunger": 500, "spirit": 500, "qi": 500 } }
        """;
        var ex = Assert.Throws<BattleResourceTuningRejection>(() => BattleResourceTuningLoader.Parse(json));
        Assert.Contains("poise", ex.Message);
    }

    /// <summary>§2.6 — authoring an `hp` share is refused, because it would create a second HP
    /// maximum that disagrees with the actor's own.</summary>
    [Fact]
    public void AnHpShareIsRefusedRatherThanQuietlyOverridingMaxHp()
    {
        var json = """
        { "schemaVersion": 1, "version": 1,
          "poolShareMilli": { "hp": 500, "stamina": 500, "hunger": 500, "spirit": 500, "qi": 500, "poise": 500 } }
        """;
        var ex = Assert.Throws<BattleResourceTuningRejection>(() => BattleResourceTuningLoader.Parse(json));
        Assert.Contains("hp", ex.Message);
    }
}

