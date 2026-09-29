using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// W6 (battle-derived-wire T5, which is also the W4 decision): a status whose StatMod names a
/// <c>combat.*</c> channel reaches battle's composed `Derived` through the per-round
/// <c>BattleDerivedModifierLedger.Recompose</c>, and withdraws with the status. Primary channels keep
/// the phased <c>BattleStatModifierLedger</c>. Before this, a status's <c>combat.*</c> mod was stored
/// in the primary ledger and read by nothing — battle owned no status→<c>combat.*</c> path at all.
/// </summary>
public class BattleStatusDerivedContributionTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "status-derived",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    static long BaseDefense => BattleRuleset.BaseDefense(5);

    static double Composed(string statusChannel, string op, double value, bool withdraw = false) =>
        BattleEngine.ComposedChannelAfterStatusForTest(
            Setup(), seed: 1, "squad:0",
            channel: DerivedStatChannels.CombatDefenseOmni,
            statusChannel: statusChannel, op: op, value: value, withdraw: withdraw);

    [Fact]
    public void A_primary_channel_status_mod_does_not_reach_the_derived_ledger()
    {
        Assert.Equal(BaseDefense, Composed("atk", "flat", 500));
    }

    [Fact]
    public void A_status_combat_channel_mod_reaches_the_composed_derived()
    {
        // base (from the frozen BaseDerived) + the status's flat contribution.
        Assert.Equal(BaseDefense + 200, Composed(DerivedStatChannels.CombatDefenseOmni, "flat", 200));
    }

    [Fact]
    public void Withdrawing_the_status_reverts_to_the_frozen_base()
    {
        Assert.Equal(BaseDefense,
            Composed(DerivedStatChannels.CombatDefenseOmni, "flat", 200, withdraw: true));
    }

    [Fact]
    public void An_increased_op_on_a_combat_channel_contributes_nothing()
    {
        // `combat.*` registers FlatSum, and `DerivedComposer.ComposeChannel` sums only Flat for that
        // kind — the same rule this routing follows, so battle and the lawn agree on what an
        // `increased` write to a FlatSum channel means.
        Assert.Equal(BaseDefense, Composed(DerivedStatChannels.CombatDefenseOmni, "increased", 0.5));
    }

    [Fact]
    public void Battle_registers_progression_but_not_the_status_derived_subsystem()
    {
        // W4's decision, pinned against its return: battle's own derived ledger owns status->combat.*,
        // so registering `StatusDerivedSubsystem` as well would double-apply the same mod. W5's
        // registration is the sibling fact, checked in the same set so a future edit that swaps one
        // for the other fails here rather than in a live balance reading.
        var hub = BattleHubCompose.BuildHubForTest(Setup().Squad[0]);
        var ids = hub.Subsystems.Select(s => s.SubsystemId).ToList();

        Assert.Contains("rpg.progression", ids);
        Assert.DoesNotContain("l2b.derived", ids);
    }
}
