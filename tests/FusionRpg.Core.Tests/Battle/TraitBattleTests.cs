using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// C2d: TraitBattleCatalog — every creature trait has battle semantics. 7 Funnel-routed
/// (stat/HP mutations) + 7 engine-native behaviors (targeting/retreat/report multipliers),
/// the split locked in creature-standalone-plan.md §Refinement.
/// </summary>
public class TraitBattleTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5,
        IReadOnlyList<string>? traits = null,
        IReadOnlyList<BattleChannelMod>? mods = null,
        int? maxHp = null, int? atk = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        TraitIds = traits ?? Array.Empty<string>(),
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>(),
        MaxHp = maxHp ?? BattleRuleset.BaseHp(level),
        Atk = atk ?? BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    // ---- the 14-row table ----

    [Fact]
    public void Every_creature_trait_has_exactly_one_battle_def()
    {
        var traitIds = CreatureTraitCatalog.All.Select(t => t.TraitId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var battleIds = TraitBattleCatalog.All.Select(t => t.TraitId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        // The count is fully implied by the element-wise equality above -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(traitIds, battleIds);
    }

    [Fact]
    public void Mechanism_split_matches_the_plan()
    {
        var funnel = TraitBattleCatalog.All.Where(t => t.Mechanism == TraitBattleMechanism.FunnelRouted)
            .Select(t => t.TraitId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var behavior = TraitBattleCatalog.All.Where(t => t.Mechanism == TraitBattleMechanism.EngineBehavior)
            .Select(t => t.TraitId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "berserker", "critical-hunter", "guardian", "immortal", "regenerator", "soul-eater", "swift" }, funnel);
        Assert.Equal(new[] { "bloodthirsty", "chaos-marked", "coward", "genius", "greedy", "loyal", "void-touched" }, behavior);
    }

    [Fact]
    public void Unknown_trait_ids_reject()
    {
        Assert.Throws<ArgumentException>(() => TraitBattleCatalog.Get("no-such-trait"));
        Assert.ThrowsAny<Exception>(() => BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "bad-trait",
            Squad = new[] { Actor("squad:0", "squad", traits: new[] { "no-such-trait" }) },
            Wave = new[] { Actor("wave:0", "wave") }
        }, 1));
    }

    // ---- Funnel-routed ----

    [Fact]
    public void Berserker_ramp_is_pure_and_staged()
    {
        var def = TraitBattleCatalog.Get("berserker");
        Assert.Equal(1000, TraitBattleMath.BerserkerRampMilli(def, hp: 100, maxHp: 100));
        Assert.Equal(1000 + def.BerserkRampHalfMilli, TraitBattleMath.BerserkerRampMilli(def, hp: 49, maxHp: 100));
        Assert.Equal(1000 + def.BerserkRampQuarterMilli, TraitBattleMath.BerserkerRampMilli(def, hp: 24, maxHp: 100));
    }

    [Fact]
    public void Berserker_deals_more_over_fixed_battles()
    {
        long with = 0, without = 0;
        for (ulong seed = 0; seed < 15; seed++)
        {
            with += SquadDamage(seed, "berserker");
            without += SquadDamage(seed, null);
        }

        Assert.True(with > without, $"berserker dealt {with}, plain dealt {without}");
    }

    [Fact]
    public void Regenerator_ends_battles_healthier()
    {
        long with = 0, without = 0;
        for (ulong seed = 0; seed < 15; seed++)
        {
            with += SquadHpLeft(seed, "regenerator");
            without += SquadHpLeft(seed, null);
        }

        Assert.True(with > without, $"regenerator kept {with} hp, plain kept {without}");
    }

    [Fact]
    public void Soul_eater_heals_on_kill()
    {
        // A strong soul-eater carving through a many-body weak wave ends healthier than the mirror.
        long with = 0, without = 0;
        for (ulong seed = 0; seed < 15; seed++)
        {
            with += FeastHpLeft(seed, withSoulEater: true);
            without += FeastHpLeft(seed, withSoulEater: false);
        }

        Assert.True(with > without, $"soul-eater kept {with} hp, plain kept {without}");
    }

    [Fact]
    public void Critical_hunter_mods_ride_the_composed_snapshot()
    {
        var snap = BattleHubCompose.Compose(Actor("squad:0", "squad", traits: new[] { "critical-hunter" }));
        var plain = BattleHubCompose.Compose(Actor("squad:0", "squad"));
        Assert.True(CombatDerivedReader.CritRate(snap, ElementTypeId.Fire)
                    > CombatDerivedReader.CritRate(plain, ElementTypeId.Fire));
    }

    [Fact]
    public void Guardian_shares_adjacent_ally_damage()
    {
        // Ally first in setup order gets targeted; the adjacent guardian pulls a share onto itself.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "guard",
            Squad = new[]
            {
                Actor("squad:0", "squad"),
                Actor("squad:1", "squad", traits: new[] { "guardian" })
            },
            Wave = new[] { Actor("wave:0", "wave", level: 7) }
        }, 9);

        var guardian = report.Actors.Single(a => a.Key == "squad:1");
        Assert.True(guardian.HpRemaining < BattleRuleset.BaseHp(5),
            "guardian never absorbed a share of the ally's hits");
    }

    [Fact]
    public void Swift_always_acts_first()
    {
        // Mutual one-shot duel: whoever swings first wins. Swift must win every seed. The one-shot
        // comes from the ACTION's base x P(Theta) (action-enrich AE1.4: `atk` is no longer a damage
        // input), so the duel is decided by a high Theta and swings-first, exactly as before.
        for (ulong seed = 0; seed < 10; seed++)
        {
            var report = BattleEngine.Resolve(new BattleSetup
            {
                WaveId = "duel",
                Squad = new[] { Actor("squad:0", "squad", traits: new[] { "swift" }) with { ThetaActor = 100 } },
                Wave = new[] { Actor("wave:0", "wave") with { ThetaActor = 100 } }
            }, seed);
            Assert.Equal(BattleOutcome.Victory, report.Outcome);
        }
    }

    [Fact]
    public void Immortal_refuses_the_first_death()
    {
        // AE1.4: the one-shot is the action's base x P(Theta), not the retired `atk` knob.
        var oneShotWave = new[] { Actor("wave:0", "wave", traits: new[] { "swift" }) with { ThetaActor = 100 } };

        var plain = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "immortal",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = oneShotWave
        }, 4);
        var withTrait = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "immortal",
            Squad = new[] { Actor("squad:0", "squad", traits: new[] { "immortal" }) },
            Wave = oneShotWave
        }, 4);

        var plainDeath = plain.Events.Single(e => e.Kind == BattleEventKinds.Die && e.ActorKey == "squad:0").Round;
        var traitDeath = withTrait.Events.Single(e => e.Kind == BattleEventKinds.Die && e.ActorKey == "squad:0").Round;
        Assert.True(traitDeath > plainDeath,
            $"immortal died round {traitDeath}, plain died round {plainDeath} — the charge never fired");
    }

    // ---- engine-native behaviors ----

    [Fact]
    public void Coward_survives_a_wipe()
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "wipe",
            // Chip damage, not one-shots: the coward must cross the retreat window alive.
            Squad = new[] { Actor("squad:0", "squad", level: 1, traits: new[] { "coward" }) },
            Wave = new[] { Actor("wave:0", "wave", level: 3) }
        }, 6);

        Assert.Equal(BattleOutcome.Defeat, report.Outcome);
        var coward = report.Actors.Single(a => a.Key == "squad:0");
        Assert.True(coward.Retreated, "coward never retreated");
        Assert.True(coward.Survived, "coward should leave the battle alive");
        Assert.DoesNotContain(report.Events, e => e.Kind == BattleEventKinds.Die && e.ActorKey == "squad:0");
    }

    [Fact]
    public void Bloodthirsty_hunts_the_lowest_hp_opponent()
    {
        // Wave has a wounded straggler behind a healthy front actor (setup order): a bloodthirsty
        // attacker kills the straggler first; a plain attacker chews the front first.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "hunt",
            Squad = new[] { Actor("squad:0", "squad", level: 8, traits: new[] { "bloodthirsty" }) },
            Wave = new[]
            {
                Actor("wave:0", "wave", level: 8),
                Actor("wave:1", "wave", level: 8, maxHp: 20)
            }
        }, 2);

        var frontDie = report.Events.SingleOrDefault(e => e.Kind == BattleEventKinds.Die && e.ActorKey == "wave:0");
        var straggler = report.Events.SingleOrDefault(e => e.Kind == BattleEventKinds.Die && e.ActorKey == "wave:1");
        Assert.NotNull(straggler);
        if (frontDie != null)
            Assert.True(straggler!.Round < frontDie.Round, "bloodthirsty should kill the weakest first");
    }

    [Fact]
    public void Loyal_redirects_hits_from_its_adjacent_ally()
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "bodyguard",
            Squad = new[]
            {
                Actor("squad:0", "squad"),
                Actor("squad:1", "squad", traits: new[] { "loyal" })
            },
            Wave = new[] { Actor("wave:0", "wave") }
        }, 8);

        var ward = report.Actors.Single(a => a.Key == "squad:0");
        var loyal = report.Actors.Single(a => a.Key == "squad:1");
        Assert.Equal(BattleRuleset.BaseHp(5), ward.HpRemaining); // every hit was intercepted
        Assert.True(loyal.HpRemaining < BattleRuleset.BaseHp(5), "loyal never took the redirected hits");
    }

    [Fact]
    public void Greedy_multiplies_battle_loot_in_the_report()
    {
        var with = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "loot",
            Squad = new[] { Actor("squad:0", "squad", level: 9, traits: new[] { "greedy" }) },
            Wave = new[] { Actor("wave:0", "wave", level: 1) }
        }, 3);
        var without = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "loot",
            Squad = new[] { Actor("squad:0", "squad", level: 9) },
            Wave = new[] { Actor("wave:0", "wave", level: 1) }
        }, 3);

        Assert.Equal(1000, without.SoulLootMilli);
        Assert.Equal(1000 + TraitBattleCatalog.Get("greedy").SoulLootBonusMilli, with.SoulLootMilli);
    }

    [Fact]
    public void Genius_stamps_a_specimen_xp_multiplier()
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "learn",
            Squad = new[]
            {
                Actor("squad:0", "squad", traits: new[] { "genius" }),
                Actor("squad:1", "squad")
            },
            Wave = new[] { Actor("wave:0", "wave", level: 1) }
        }, 3);

        Assert.Equal(1000 + TraitBattleCatalog.Get("genius").SpecimenXpBonusMilli,
            report.Actors.Single(a => a.Key == "squad:0").XpMilli);
        Assert.Equal(1000, report.Actors.Single(a => a.Key == "squad:1").XpMilli);
    }

    [Theory]
    [InlineData("void-touched")]
    [InlineData("chaos-marked")]
    public void Essence_riders_add_damage_over_fixed_battles(string trait)
    {
        long with = 0, without = 0;
        for (ulong seed = 0; seed < 20; seed++)
        {
            with += SquadDamage(seed, trait);
            without += SquadDamage(seed, null);
        }

        Assert.True(with > without, $"{trait} dealt {with}, plain dealt {without} — no rider ever fired");
    }

    // ---- helpers ----

    long SquadDamage(ulong seed, string? trait)
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "swing",
            Squad = new[] { Actor("squad:0", "squad", traits: trait == null ? null : new[] { trait }) },
            Wave = new[] { Actor("wave:0", "wave") }
        }, seed);
        return report.Actors.Single(a => a.Side == "squad").DamageDealt;
    }

    long SquadHpLeft(ulong seed, string? trait)
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "endure",
            Squad = new[] { Actor("squad:0", "squad", level: 7, traits: trait == null ? null : new[] { trait }) },
            Wave = new[] { Actor("wave:0", "wave", level: 4) }
        }, seed);
        return report.Actors.Single(a => a.Side == "squad").HpRemaining;
    }

    long FeastHpLeft(ulong seed, bool withSoulEater)
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "feast",
            Squad = new[] { Actor("squad:0", "squad", level: 9, traits: withSoulEater ? new[] { "soul-eater" } : null) },
            Wave = Enumerable.Range(0, 4).Select(i => Actor($"wave:{i}", "wave", level: 2)).ToList()
        }, seed);
        return report.Actors.Single(a => a.Side == "squad").HpRemaining;
    }
}
