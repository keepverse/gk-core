using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

public class BattleHubComposeTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5,
        ElementTypeId? elem = null, ElementTypeId? elemSecondary = null,
        IReadOnlyList<BattleChannelMod>? mods = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        ElementPrimary = elem,
        ElementSecondary = elemSecondary,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>()
    };

    [Fact]
    public void Composed_channel_reads_match_CombatDerivedReader()
    {
        var setup = Actor("squad:0", "squad", level: 4, elem: ElementTypeId.Fire, elemSecondary: ElementTypeId.Ice);
        var snap = BattleHubCompose.Resolve(setup).Derived;

        // battle-adoption mapping: Atk is the resolver's BASE, so power.omni is 0 (double-count
        // ban); Defense KEEPS its omni channel; affinity shares stay on both sides.
        Assert.Equal(0, (int)snap.Get(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal(setup.Atk / 4, (int)CombatDerivedReader.Power(snap, ElementTypeId.Fire));
        Assert.Equal(setup.Atk / 8, (int)CombatDerivedReader.Power(snap, ElementTypeId.Ice));
        Assert.Equal(0, (int)CombatDerivedReader.Power(snap, ElementTypeId.Earth));
        Assert.Equal(setup.Defense + setup.Defense / 4, (int)CombatDerivedReader.Defense(snap, ElementTypeId.Fire));
        Assert.Equal(setup.Defense + setup.Defense / 8, (int)CombatDerivedReader.Defense(snap, ElementTypeId.Ice));

        // Accuracy family baselines come from level formulas (omni only — no element affinity).
        Assert.Equal(BattleRuleset.BaseAccuracy(4), (int)CombatDerivedReader.Accuracy(snap, ElementTypeId.Fire));
        Assert.Equal(BattleRuleset.BaseDodge(4), (int)CombatDerivedReader.Dodge(snap, ElementTypeId.Fire));
        Assert.Equal(BattleRuleset.BaseCritRate(4), (int)CombatDerivedReader.CritRate(snap, ElementTypeId.Fire));
        Assert.Equal(BattleRuleset.BaseCritResist(4), (int)CombatDerivedReader.CritResist(snap, ElementTypeId.Fire));
    }

    [Fact]
    public void Untyped_actor_reads_omni_only()
    {
        var setup = Actor("squad:0", "squad", level: 3);
        var snap = BattleHubCompose.Compose(setup);
        Assert.Equal(0, (int)snap.Get(DerivedStatChannels.CombatPowerOmni));   // Atk = request base
        Assert.Equal(0, (int)CombatDerivedReader.Power(snap, ElementTypeId.Fire));
        Assert.Equal(setup.Defense, (int)CombatDerivedReader.Defense(snap, ElementTypeId.Dark));
    }

    [Fact]
    public void Channel_mods_overlay_additively()
    {
        var setup = Actor("squad:0", "squad", level: 5, mods: new[]
        {
            new BattleChannelMod(DerivedStatChannels.CombatDodgeOmni, 200),
            new BattleChannelMod(DerivedStatChannels.CombatDodgeOmni, 50),
            new BattleChannelMod(DerivedStatChannels.CombatPowerFire, 30)
        });
        var snap = BattleHubCompose.Compose(setup);
        Assert.Equal(BattleRuleset.BaseDodge(5) + 250, (int)CombatDerivedReader.Dodge(snap, ElementTypeId.Fire));
        Assert.Equal(30, (int)CombatDerivedReader.Power(snap, ElementTypeId.Fire));   // mods only — no Atk base
    }

    [Fact]
    public void Aggression_channel_defaults_to_zero_with_no_content()
    {
        // base-defense/spec-siege-ai.md §5.20 rule 4 -- IBattleView.AggressionOf reads this channel
        // directly (BattleRunState.cs). No taunt/stealth content exists anywhere yet, so every real
        // actor must compose to exactly 0 -- byte-for-byte the same as the accessor's old hardcoded
        // return, proving this wiring changed nothing observable until content actually targets it.
        var setup = Actor("squad:0", "squad");
        var snap = BattleHubCompose.Compose(setup);
        Assert.Equal(0, (int)snap.Get(DerivedStatChannels.AiAggression));
    }

    [Fact]
    public void Aggression_channel_composes_additively_from_channel_mods()
    {
        // Proves the channel is genuinely live and FlatSum (not a registration nobody can reach):
        // two independent contributions (e.g. two stacked taunts) add rather than replace.
        var setup = Actor("squad:0", "squad", mods: new[]
        {
            new BattleChannelMod(DerivedStatChannels.AiAggression, 1),
            new BattleChannelMod(DerivedStatChannels.AiAggression, 1)
        });
        var snap = BattleHubCompose.Compose(setup);
        Assert.Equal(2, (int)snap.Get(DerivedStatChannels.AiAggression));
    }

    [Fact]
    public void Unknown_mod_channel_rejects()
    {
        var setup = Actor("squad:0", "squad", mods: new[] { new BattleChannelMod("combat.power.plasma", 10) });
        Assert.Throws<ArgumentException>(() => BattleHubCompose.Compose(setup));
    }

    [Fact]
    public void Dodge_mod_swings_fixed_battles()
    {
        long withDodge = 0, without = 0;
        for (ulong seed = 0; seed < 20; seed++)
        {
            withDodge += SquadDamageDealt(seed, defenderMods: new[]
                { new BattleChannelMod(DerivedStatChannels.CombatDodgeOmni, 300) });
            without += SquadDamageDealt(seed, defenderMods: null);
        }

        Assert.True(withDodge < without,
            $"squad dealt {withDodge} vs dodgy defender, {without} vs plain defender");
    }

    [Fact]
    public void Crit_mod_swings_fixed_battles()
    {
        long withCrit = 0, without = 0;
        for (ulong seed = 0; seed < 20; seed++)
        {
            withCrit += SquadDamageDealt(seed, attackerMods: new[]
                { new BattleChannelMod(DerivedStatChannels.CombatCritRateOmni, 300) });
            without += SquadDamageDealt(seed, attackerMods: null);
        }

        Assert.True(withCrit > without,
            $"critty squad dealt {withCrit}, plain squad dealt {without}");
    }

    long SquadDamageDealt(ulong seed,
        IReadOnlyList<BattleChannelMod>? attackerMods = null,
        IReadOnlyList<BattleChannelMod>? defenderMods = null)
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "stat-swing",
            Squad = new[] { Actor("squad:0", "squad", mods: attackerMods) },
            Wave = new[] { Actor("wave:0", "wave", mods: defenderMods) }
        }, seed);
        return report.Actors.Single(a => a.Side == "squad").DamageDealt;
    }

    [Fact]
    public void Host_AddDerivedContribution_reaches_Derived_and_swings_a_real_battle()
    {
        // base-defense siege-ai / passive-tree G2 (spec-mechanism-wiring.md §4.2): proves the SAME
        // channel-mutation effect Dodge_mod_swings_fixed_battles proves via setup-time ChannelMods, but
        // through the DIFFERENT, MID-BATTLE path a future taunt/stealth EFFECT needs for
        // ai.aggression: Host.AddDerivedContribution -> sourced ledger row -> BattleHubCompose.Resolve
        // -> stable Derived snapshot -> every existing consumer. The per-round refresh runs from the
        // SHARED battle loop, so Siege/Delve inherit it rather than receiving a private composer.
        long withHostDodge = 0, without = 0;
        for (ulong seed = 0; seed < 20; seed++)
        {
            withHostDodge += SquadDamageDealtWithHostContribution(seed, "wave:0", DerivedStatChannels.CombatDodgeOmni, 300);
            without += SquadDamageDealt(seed, defenderMods: null);
        }

        Assert.True(withHostDodge < without,
            $"squad dealt {withHostDodge} vs a host-contributed-dodge defender, {without} vs plain defender");
    }

    long SquadDamageDealtWithHostContribution(ulong seed, string targetActorKey, string channel, double value)
    {
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "stat-swing-host",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:0", "wave") }
        }, seed, onEffectHostReady: host =>
            host.AddDerivedContribution?.Invoke(targetActorKey, channel, "test-host-contribution", value));
        return report.Actors.Single(a => a.Side == "squad").DamageDealt;
    }

    [Fact]
    public void ATurnDotChannelModThroughTheComposePathDoesNotThrow()
    {
        // battle-timeline B9's own acceptance line: "a turn.* modifier through the compose path does
        // not throw." Before P0.5, turn.speed/turn.haste were unregistered, so BattleStatComposer's
        // KnownChannels check would have rejected this mod as unknown.
        //
        // `battle-tempo` `tempo-content` (2026-09-05) corrected this test's own original assumption
        // for `turn.speed`: `BattleStatComposer.Compose` seeds it from `SpeciesTempoProjection.SpeedFor`
        // (spec-tempo-content.md §2.1), falling back to `DerivedStatPolicy.TurnDefaultSpeed` for any
        // actor with no authored `AttackIntervalMs` (this test's own `Actor()` helper, unchanged).
        //
        // `battle-hub-fuse` T5 (2026-09-12) found the same was already true of `turn.haste`, just via
        // a different route: `DerivedStatRegistry` registers its `DefaultValue` as
        // `DerivedTurnChannels.NominalHasteMilli` (not 0), and Hub compose adds every channel's
        // registered default underneath every contribution (`BattleTempoSubsystem`'s own doc comment).
        // No subsystem overlays `turn.haste` for this setup, so the mod overlays on that registered
        // default -- never on an implicit 0, matching how `BattleEngine`/`BattleDurationResolver`
        // already treat an unset haste as `NominalHasteMilli` elsewhere in this codebase.
        var setup = Actor("squad:0", "squad", mods: new[]
        {
            new BattleChannelMod(DerivedTurnChannels.Speed, 50),
            new BattleChannelMod(DerivedTurnChannels.Haste, -200),
        });

        var snap = BattleHubCompose.Compose(setup);

        // tempo-content's own seed (TurnDefaultSpeed, for an actor with no authored AttackIntervalMs)
        // plus the mod's own 50 -- read from the same tunable this composer itself reads, never
        // hardcoded, so this stays correct if the tunable's value ever changes.
        Assert.Equal(DerivedStatPolicy.TurnDefaultSpeed + 50, (int)snap.Get(DerivedTurnChannels.Speed));
        Assert.Equal(DerivedTurnChannels.NominalHasteMilli - 200, (int)snap.Get(DerivedTurnChannels.Haste));
    }
}
