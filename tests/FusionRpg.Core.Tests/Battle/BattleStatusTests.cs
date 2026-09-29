using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// C2c: battle-local StatusRuntime — DoT pulses kill through rounds, CC skips turns, and the
/// ResistanceEvaluator over composed derived profiles blocks applies. Scenarios are structural
/// (outcome/HP shape), not magnitude-exact: netFactor scaling is the shipped L2b math.
/// </summary>
public class BattleStatusTests
{
    static BattleActorSetup Actor(string key, string side, int level,
        IReadOnlyList<BattleChannelMod>? mods = null,
        IReadOnlyList<BattleStatusSpec>? statuses = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>(),
        InitialStatuses = statuses ?? Array.Empty<BattleStatusSpec>()
    };

    static readonly BattleChannelMod[] Untouchable =
        { new(DerivedStatChannels.CombatDodgeOmni, 1000) };

    // Weak squad attacker that can barely be hit and barely scratches; the wave enemy is a
    // level-10 wall. Only the DoT can plausibly bring the wall down inside 50 rounds.
    static BattleSetup DotRace(IReadOnlyList<BattleChannelMod>? wallMods, int dotPerPulse)
    {
        var mods = new List<BattleChannelMod>();
        if (wallMods != null) mods.AddRange(wallMods);
        return new BattleSetup
        {
            WaveId = "dot-race",
            Squad = new[] { Actor("squad:0", "squad", 1, mods: Untouchable) },
            Wave = new[]
            {
                Actor("wave:0", "wave", 10, mods: mods, statuses: new[]
                {
                    new BattleStatusSpec("wither", -dotPerPulse, DurationMs: 40_000, PeriodMs: 1000)
                })
            }
        };
    }

    [Fact]
    public void Dot_kills_through_rounds()
    {
        var report = BattleEngine.Resolve(DotRace(wallMods: null, dotPerPulse: 100), 3);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
        var squad = report.Actors.Single(a => a.Side == "squad");
        var wall = report.Actors.Single(a => a.Side == "wave");
        Assert.False(wall.Survived);
        // The squad's chip damage cannot account for the kill — the DoT did it.
        Assert.True(squad.DamageDealt < BattleRuleset.BaseHp(10) / 2,
            $"squad dealt {squad.DamageDealt}, wall hp {BattleRuleset.BaseHp(10)}");
        Assert.Contains(report.Events, e => e.Kind == BattleEventKinds.Die && e.ActorKey == "wave:0");
    }

    [Fact]
    public void Resistance_blocks_the_apply()
    {
        // Same race, but the wall's composed profile carries heavy status resist — the wither
        // apply is rejected (potency floor), so nothing can kill the wall inside MaxRounds.
        var resist = new[] { new BattleChannelMod(DerivedStatChannels.StatusResistOmni, 100) };
        var report = BattleEngine.Resolve(DotRace(wallMods: resist, dotPerPulse: 100), 3);

        Assert.NotEqual(BattleOutcome.Victory, report.Outcome);
        Assert.True(report.Actors.Single(a => a.Side == "wave").Survived);
    }

    [Fact]
    public void Cc_skips_the_victims_turns()
    {
        // Equal 1v1, but the wave actor is charm-locked for the whole battle: it never attacks,
        // so the squad wins without losing a single hit point.
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "cc-lock",
            Squad = new[] { Actor("squad:0", "squad", 3) },
            Wave = new[]
            {
                Actor("wave:0", "wave", 3, statuses: new[]
                {
                    new BattleStatusSpec("charm_pulse", 0, DurationMs: 120_000, PeriodMs: 0)
                })
            }
        }, 5);

        Assert.Equal(BattleOutcome.Victory, report.Outcome);
        var squad = report.Actors.Single(a => a.Side == "squad");
        Assert.Equal(BattleRuleset.BaseHp(3), squad.HpRemaining);
    }

    [Fact]
    public void Status_specs_reject_unknown_ids()
    {
        Assert.ThrowsAny<Exception>(() => BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "bad-status",
            Squad = new[] { Actor("squad:0", "squad", 3) },
            Wave = new[]
            {
                Actor("wave:0", "wave", 3, statuses: new[]
                {
                    new BattleStatusSpec("no-such-status", -5, DurationMs: 5000, PeriodMs: 1000)
                })
            }
        }, 1));
    }

    [Fact]
    public void Status_battles_stay_deterministic()
    {
        var a = System.Text.Json.JsonSerializer.Serialize(BattleEngine.Resolve(DotRace(null, 60), 11));
        var b = System.Text.Json.JsonSerializer.Serialize(BattleEngine.Resolve(DotRace(null, 60), 11));
        Assert.Equal(a, b);
    }
}
