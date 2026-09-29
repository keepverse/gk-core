using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>U12/U13 — shields in battle end-to-end: absorb, innate, events, report, replay.</summary>
public class BattleShieldTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5,
        ElementTypeId? elem = null, BattleInnateShield? shield = null,
        IReadOnlyList<string>? traits = null, int? maxHp = null) => new()
    {
        Key = key, Side = side, SpeciesId = "spec", TypeId = 1, Level = level,
        ElementPrimary = elem,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        InnateShield = shield,
        TraitIds = traits ?? Array.Empty<string>()
    };

    static BattleReport Run(BattleActorSetup squad, BattleActorSetup wave, ulong seed = 5) =>
        BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "shield-battle", Squad = new[] { squad }, Wave = new[] { wave }
        }, seed);

    [Fact]
    public void Innate_shield_absorbs_before_hp_and_reports_the_tally()
    {
        var shielded = Actor("wave:0", "wave", shield: new BattleInnateShield(120));
        var report = Run(Actor("squad:0", "squad", level: 8), shielded);

        var tank = report.Actors.Single(a => a.Key == "wave:0");
        Assert.True(tank.ShieldAbsorbed > 0, "innate shield must absorb something");
        Assert.Contains(report.Events, e => e.Kind == BattleEventKinds.ShieldGranted && e.ActorKey == "wave:0");
        Assert.Contains(report.Events, e => e.Kind == BattleEventKinds.ShieldAbsorbed && e.Amount > 0);
    }

    [Fact]
    public void Broken_shield_emits_break_after_its_absorb_aggregate()
    {
        var shielded = Actor("wave:0", "wave", level: 1, shield: new BattleInnateShield(30));
        var report = Run(Actor("squad:0", "squad", level: 10), shielded, seed: 9);

        var broken = report.Events.Where(e => e.Kind == BattleEventKinds.ShieldBroken).ToList();
        Assert.Single(broken);
        var absorbedIdx = report.Events.ToList().FindIndex(e => e.Kind == BattleEventKinds.ShieldAbsorbed);
        var brokenIdx = report.Events.ToList().FindIndex(e => e.Kind == BattleEventKinds.ShieldBroken);
        Assert.True(absorbedIdx >= 0 && absorbedIdx < brokenIdx,
            "the absorb aggregate precedes the break (shield spec ordering)");
    }

    [Fact]
    public void Guardian_share_deaths_keep_the_initiating_attacker_as_killer()
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "guardian-attribution",
            Squad = new[] { Actor("squad:0", "squad", level: 10) },
            Wave = new[]
            {
                Actor("wave:0", "wave", level: 1, maxHp: 1),
                Actor("wave:1", "wave", level: 1, maxHp: 1, traits: new[] { "guardian" })
            }
        }, 5);

        var deaths = report.Events
            .Where(e => e.Kind == BattleEventKinds.Die)
            .ToDictionary(e => e.ActorKey);

        Assert.Equal(2, deaths.Count);
        Assert.Equal("squad:0", deaths["wave:0"].KillerActorKey);
        Assert.Equal("squad:0", deaths["wave:1"].KillerActorKey);
    }

    [Fact]
    public void Timed_innate_shield_expires_by_rounds()
    {
        // 2500 ms → ceil = 3 round-ticks. Give both sides huge HP so the battle outlives it.
        var shielded = Actor("wave:0", "wave", maxHp: 5_000,
            shield: new BattleInnateShield(100_000, DurationMs: 2500));
        var report = Run(Actor("squad:0", "squad", maxHp: 5_000), shielded, seed: 11);

        var expired = report.Events.Single(e => e.Kind == BattleEventKinds.ShieldExpired);
        Assert.Equal(3, expired.Round);
        Assert.Equal("wave:0", expired.ActorKey);
    }

    [Fact]
    public void Dot_pulses_drain_shields_before_hp()
    {
        // ISOLATES the DoT. A bare ">= n" proves nothing here: attack absorption alone clears
        // any small threshold, and deleting the poison can even RAISE the tally (it shortens
        // the battle). So make the attacker unable to land — unreachable dodge — and the
        // shield's ONLY input is the poison. The control then absorbs exactly zero, which is
        // what makes the poisoned figure attributable.
        BattleActorSetup Tank(bool poisoned)
        {
            var a = Actor("wave:0", "wave", maxHp: 1_000_000, shield: new BattleInnateShield(1_000_000)) with
            {
                ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.CombatDodgeOmni, 100_000) }
            };
            // "wither", NOT "poison": poison is registered StatusKind.UnityCc (the vanilla game
            // owns its damage), so StatusRuntime.Tick — which pulses only OverTime/Contagion —
            // never ticks it, and IsCcLocked treats it as a turn lock instead. "wither" is the
            // overlay-authored OverTime/PulseHp DoT, which is what this test is about.
            return poisoned
                ? a with { InitialStatuses = new[] { new BattleStatusSpec("wither", -25, DurationMs: 4000, PeriodMs: 1000) } }
                : a;
        }

        long Absorbed(bool poisoned) => Run(
            Actor("squad:0", "squad", level: 1, maxHp: 1_000_000), Tank(poisoned), seed: 13)
            .Actors.Single(a => a.Key == "wave:0").ShieldAbsorbed;

        Assert.Equal(0, Absorbed(poisoned: false));      // nothing lands: no attack absorption at all
        Assert.Equal(100, Absorbed(poisoned: true));     // 4000 ms / 1000 ms = 4 pulses of 25, all eaten
    }

    [Fact]
    public void Guardian_two_slice_both_shields_absorb_their_own_portion()
    {
        var target = Actor("wave:0", "wave", maxHp: 3_000, shield: new BattleInnateShield(400));
        var guardian = Actor("wave:1", "wave", maxHp: 3_000,
            shield: new BattleInnateShield(400), traits: new[] { "guardian" });
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "guardian-slice",
            Squad = new[] { Actor("squad:0", "squad", level: 10, maxHp: 3_000) },
            Wave = new[] { target, guardian }
        }, 21);

        var t = report.Actors.Single(a => a.Key == "wave:0");
        var g = report.Actors.Single(a => a.Key == "wave:1");
        Assert.True(t.ShieldAbsorbed > 0, "target's shield absorbs its slice");
        Assert.True(g.ShieldAbsorbed > 0, "guardian's shield absorbs the pulled share");
    }

    [Fact]
    public void Replay_with_shields_is_byte_identical()
    {
        BattleReport Once() => BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "replay",
            Squad = new[]
            {
                Actor("squad:0", "squad", elem: ElementTypeId.Fire,
                    shield: new BattleInnateShield(80, ElementTypeId.Light)),
                Actor("squad:1", "squad", traits: new[] { "guardian", "regenerator" })
            },
            Wave = new[]
            {
                Actor("wave:0", "wave", elem: ElementTypeId.Ice, shield: new BattleInnateShield(150)),
                Actor("wave:1", "wave", traits: new[] { "coward" })
            }
        }, 12345);

        var a = System.Text.Json.JsonSerializer.Serialize(Once());
        var b = System.Text.Json.JsonSerializer.Serialize(Once());
        Assert.Equal(a, b);   // closes the shield spec §7 determinism replay promise
    }

    [Fact]
    public void Prefixed_actor_keys_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Run(
            Actor("entity:squad:0", "squad"), Actor("wave:0", "wave")));
        Assert.Throws<ArgumentException>(() => Run(
            Actor("0xsquad", "squad"), Actor("wave:0", "wave")));
    }

    [Fact]
    public void Report_carries_ruleset_v3_and_platform_stamp()
    {
        var report = Run(Actor("squad:0", "squad"), Actor("wave:0", "wave"));
        // battle-hub-fuse (2026-09-13): RulesetVersion 4 -> 5.
        Assert.Equal(5, report.RulesetVersion);
        Assert.Equal(BattleEnvironment.Stamp, report.EnvironmentStamp);
        Assert.False(string.IsNullOrWhiteSpace(report.EnvironmentStamp));
    }

    [Fact]
    public void Emitter_forwards_shield_events_and_stamp()
    {
        var shielded = Actor("wave:0", "wave", level: 1, shield: new BattleInnateShield(30));
        var report = Run(Actor("squad:0", "squad", level: 10), shielded, seed: 9);
        var envelopes = BattleReportEmitter.Emit(report, "web-test");

        var start = envelopes.First(e => e.Kind == "board.start");
        var startPayload = (Dictionary<string, object?>)start.Payload!;
        Assert.Equal(BattleEnvironment.Stamp, startPayload["environmentStamp"]);

        Assert.Contains(envelopes, e => e.Kind == "shield.broken");
        var end = (Dictionary<string, object?>)envelopes.Single(e => e.Kind == "board.end").Payload!;
        var summary = (Dictionary<string, object?>)end["summary"]!;
        var actors = (List<Dictionary<string, object?>>)summary["actors"]!;
        Assert.All(actors, a => Assert.True(a.ContainsKey("shieldAbsorbed")));
    }
}
