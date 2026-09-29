using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// Regression locks for Wave C/D edges that held by construction but had no test pinning them.
/// </summary>
public class WaveCDRegressionLockTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5,
        IReadOnlyList<string>? traits = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        TraitIds = traits ?? Array.Empty<string>(),
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    [Fact]
    public void Duplicate_trait_ids_apply_stat_mods_once()
    {
        // A doubled trait id (bad upstream data) must not double the crit-rate mod.
        var doubled = BattleHubCompose.Compose(
            Actor("squad:0", "squad", traits: new[] { "critical-hunter", "critical-hunter" }));
        var single = BattleHubCompose.Compose(
            Actor("squad:0", "squad", traits: new[] { "critical-hunter" }));
        Assert.Equal(
            CombatDerivedReader.CritRate(single, ElementTypeId.Fire),
            CombatDerivedReader.CritRate(doubled, ElementTypeId.Fire));
    }

    [Fact]
    public void Immortal_charge_fires_exactly_once()
    {
        // One charge: the actor survives the first lethal hit at 1 HP, then dies to the next.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "immortal-once",
            Squad = new[] { Actor("squad:0", "squad", traits: new[] { "immortal" }) },
            // AE1.4: the lethal hit is the action's base x P(Theta); `atk` no longer feeds damage.
            Wave = new[] { Actor("wave:0", "wave") with { ThetaActor = 100 } }
        }, 12);

        Assert.Equal(BattleOutcome.Defeat, report.Outcome);
        Assert.Single(report.Events, e => e.Kind == BattleEventKinds.Die && e.ActorKey == "squad:0");
    }

    [Fact]
    public void Resolver_clamps_elapsed_beyond_the_tick_count()
    {
        var squad = new[] { Actor("squad:0", "squad") };
        var exact = ExpeditionResolver.Resolve("scout-30m", squad, 5, elapsedTicks: 6);
        var over = ExpeditionResolver.Resolve("scout-30m", squad, 5, elapsedTicks: 999);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(exact),
            System.Text.Json.JsonSerializer.Serialize(over));
        Assert.Throws<ArgumentException>(() =>
            ExpeditionResolver.Resolve("scout-30m", new[] { Actor("a", "squad"), Actor("b", "squad"), Actor("c", "squad") }, 1, 6));
    }

    [Fact]
    public void Malformed_actor_setups_reject_loudly()
    {
        // 2026-08-21 review: mixed-case keys would be silently unhittable (the funnel lower-cases
        // target keys), duplicates would shadow an actor, MaxHp 0 spawns a die-event-less corpse.
        BattleSetup With(BattleActorSetup squad0) => new()
        {
            WaveId = "validate",
            Squad = new[] { squad0 },
            Wave = new[] { Actor("wave:0", "wave") }
        };

        Assert.Throws<ArgumentException>(() =>
            BattleEngine.Resolve(With(Actor("Squad:0", "squad")), 1));
        Assert.Throws<ArgumentException>(() =>
            BattleEngine.Resolve(With(Actor("wave:0", "squad")), 1)); // duplicate across sides
        Assert.Throws<ArgumentException>(() =>
            BattleEngine.Resolve(With(Actor("squad:0", "squad") with { MaxHp = 0 }), 1));
    }

    [Fact]
    public void Oversized_channel_mods_saturate_instead_of_wrapping()
    {
        // A wrapped-negative product used to flatten to 1 damage — "more power, less damage".
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "overflow",
            Squad = new[]
            {
                Actor("squad:0", "squad") with
                {
                    ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.CombatPowerOmni, 500_000_000) }
                }
            },
            Wave = new[] { Actor("wave:0", "wave") }
        }, 3);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
        Assert.True(report.Actors.Single(a => a.Side == "squad").DamageDealt > 1_000_000,
            "an absurd power mod must one-shot, not underflow to chip damage");
    }

    [Fact]
    public void Wild_joins_carry_manifest_rolled_traits()
    {
        // Review I3: the rewards manifest is complete in Core — traits included.
        for (ulong seed = 0; seed < 60; seed++)
        {
            var r = ExpeditionResolver.Resolve("warpath-20h",
                Enumerable.Range(0, 5).Select(i => Actor($"squad:{i}", "squad")).ToList(), seed, 10);
            foreach (var join in r.Rewards.WildJoins)
            {
                Assert.NotEmpty(join.TraitIds);
                Assert.All(join.TraitIds, t => Assert.True(
                    FusionRpg.Core.Creatures.CreatureTraitCatalog.IsKnown(t)));
            }
            if (r.Rewards.WildJoins.Count > 0) return;
        }

        Assert.Fail("no seed in 0..59 produced a wild join — band math is off");
    }

    [Fact]
    public void Greedy_multiplies_expedition_event_souls_in_the_manifest()
    {
        var plain = Enumerable.Range(0, 3).Select(i => Actor($"squad:{i}", "squad")).ToList();
        var greedy = plain.Select(s => s with { TraitIds = new[] { "greedy" } }).ToList();
        for (ulong seed = 0; seed < 40; seed++)
        {
            var baseSouls = ExpeditionResolver.Resolve("forage-4h", plain, seed, 8).Rewards.EventSouls;
            if (baseSouls == 0) continue;
            var greedySouls = ExpeditionResolver.Resolve("forage-4h", greedy, seed, 8).Rewards.EventSouls;
            Assert.Equal(baseSouls * (1000 + 250 * 3) / 1000, greedySouls);
            return;
        }

        Assert.Fail("no seed in 0..39 found any souls — band math is off");
    }

    [Fact]
    public void Emitter_never_kills_a_retreated_coward()
    {
        // The coward leaves alive: spawn event, NO die event, retreated+survived in the summary.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "coward-emit",
            Squad = new[] { Actor("squad:0", "squad", level: 1, traits: new[] { "coward" }) },
            Wave = new[] { Actor("wave:0", "wave", level: 3) }
        }, 6);
        Assert.True(report.Actors.Single(a => a.Key == "squad:0").Retreated, "scenario seed must retreat");

        var events = BattleReportEmitter.Emit(report, "coward-match");
        Assert.Single(events, e => e.Kind == "plant.spawn");
        Assert.DoesNotContain(events, e => e.Kind == "plant.die");

        var summary = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(events[^1].Payload)["summary"]);
        var tallies = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(summary["actors"]);
        var coward = tallies.Single(t => (string?)t["key"] == "squad:0");
        Assert.Equal(true, coward["retreated"]);
        Assert.Equal(true, coward["survived"]);
    }
}
