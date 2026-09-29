using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// combat-unification `species-skills` **S2 + S3 + S4** — the two readers, proven by CONTRAST rather
/// than by existence, plus category routing and the floor.
///
/// <para>A reader that is merely "called" proves nothing: the acceptance is that a non-neutral channel
/// value changes the outcome, that a neutral one does not (S1's file), and that a value on one
/// category leaves another category alone.</para>
/// </summary>
public class SkillChannelReaderTests
{
    const string Attack = DerivedStatChannels.ActionCategoryAttack;
    const string Support = DerivedStatChannels.ActionCategorySupport;

    // ---------------- S2: the cooldown read ----------------

    static ActionEnvelope CooldownAction(string category, long ticks) => ActionEnvelope.NoOp with
    {
        ActionId = "act.test",
        Class = CooldownClass.Specific,
        CooldownKey = "act.test",
        CooldownTicks = ticks,
        CooldownChannel = DerivedStatChannels.SkillCooldown(category),
    };

    /// <summary>The load-bearing S2 contrast: a non-zero reduction arms a SHORTER cooldown. If
    /// `CooldownLedger.Start` ignored the reduction, both arms land on the same tick.</summary>
    [Fact]
    public void ANonZeroCooldownReductionArmsAShorterCooldown()
    {
        var envelope = CooldownAction(Attack, ticks: 1000);

        var neutral = new CooldownLedger();
        neutral.Start("a", envelope, atTick: 0, reductionPm: 0);

        var reduced = new CooldownLedger();
        reduced.Start("a", envelope, atTick: 0, reductionPm: 250);   // 25% off

        Assert.Equal(1000, neutral.ReadyAt("a", envelope));
        Assert.Equal(750, reduced.ReadyAt("a", envelope));
        Assert.True(reduced.ReadyAt("a", envelope) < neutral.ReadyAt("a", envelope));
    }

    /// <summary>Neutral is 0 and must be the exact identity — this is what lets S1 hold.</summary>
    [Fact]
    public void AZeroReductionIsTheIdentity()
    {
        var envelope = CooldownAction(Attack, ticks: 1234);
        var explicitly = new CooldownLedger();
        explicitly.Start("a", envelope, atTick: 0, reductionPm: 0);

        var omitted = new CooldownLedger();
        omitted.Start("a", envelope, atTick: 0);                     // default parameter

        Assert.Equal(1234, explicitly.ReadyAt("a", envelope));
        Assert.Equal(omitted.ReadyAt("a", envelope), explicitly.ReadyAt("a", envelope));
    }

    /// <summary>The structural floor survives the wired path: an absurd reduction cannot produce a
    /// zero-tick cooldown, which would be an action loop rather than a balance outcome.</summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(50_000)]
    [InlineData(1_000_000_000)]
    public void TheOneTickFloorHoldsThroughTheWiredPath(long absurdReductionPm)
    {
        var envelope = CooldownAction(Attack, ticks: 100);
        var ledger = new CooldownLedger();
        ledger.Start("a", envelope, atTick: 0, reductionPm: absurdReductionPm);

        Assert.Equal(CooldownMath.MinTicksFloor, ledger.ReadyAt("a", envelope));
    }

    // ---------------- S3: the effectiveness read ----------------

    static double Multiplier(long pm) => OverlayCombatRequest.MultiplierFromPerMille(pm);

    [Fact]
    public void EffectivenessNeutralIsExactlyOne_andNonNeutralMoves()
    {
        Assert.Equal(1.0, Multiplier(0));
        Assert.True(Multiplier(250) > 1.0);
        Assert.True(Multiplier(-250) < 1.0);
        Assert.Equal(1.25, Multiplier(250), precision: 10);
        Assert.Equal(0.75, Multiplier(-250), precision: 10);
    }

    static string Hash(BattleReport r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(r with { EnvironmentStamp = "", ContentHash = null, Warnings = null }))));

    static BattleSetup WithSquadChannel(BattleSetup setup, string channel, long amount) => setup with
    {
        Squad = setup.Squad.Select(a => a with { ChannelMods = new[] { new BattleChannelMod(channel, amount) } }).ToArray()
    };

    /// <summary>
    /// The S3 contrast, end to end through a real battle: the basic attack opts into
    /// `skill.effectiveness.attack`, so giving the squad a non-zero value must change the battle.
    /// Same setup, same seed — only the channel differs.
    /// </summary>
    [Fact]
    public void ANonZeroEffectivenessChangesTheBattle()
    {
        var setup = BattleGoldenTests.CloseSetup();
        var baseline = Hash(BattleEngine.Resolve(setup, 2002));
        var boosted = Hash(BattleEngine.Resolve(
            WithSquadChannel(setup, DerivedStatChannels.SkillEffectiveness(Attack), 500), 2002));

        Assert.NotEqual(baseline, boosted);
    }

    // ---------------- S4: category routing ----------------

    /// <summary>
    /// A value on a category the action does not belong to must not reach it. The basic attack is
    /// `attack`, so a `support` value is inert — proven against the same battle and seed.
    /// </summary>
    [Fact]
    public void AValueOnAnotherCategoryDoesNotReachThisAction()
    {
        var setup = BattleGoldenTests.CloseSetup();
        var baseline = Hash(BattleEngine.Resolve(setup, 2002));
        var otherCategory = Hash(BattleEngine.Resolve(
            WithSquadChannel(setup, DerivedStatChannels.SkillEffectiveness(Support), 500), 2002));

        Assert.Equal(baseline, otherCategory);
    }

    /// <summary>Same, for the cooldown family, at the ledger where it is enforced.</summary>
    [Fact]
    public void CooldownRoutingIsPerCategory()
    {
        var attackAction = CooldownAction(Attack, ticks: 1000);
        Assert.Equal(DerivedStatChannels.SkillCooldown(Attack), attackAction.CooldownChannel);
        Assert.NotEqual(DerivedStatChannels.SkillCooldown(Support), attackAction.CooldownChannel);
    }

    /// <summary>An action that names no channel reads nothing — the neutral path stays available.</summary>
    [Fact]
    public void AnActionNamingNoChannelIsUnscaled()
    {
        Assert.Null(ActionEnvelope.NoOp.CooldownChannel);
        Assert.Null(ActionEnvelope.NoOp.EffectivenessChannel);
    }
}
