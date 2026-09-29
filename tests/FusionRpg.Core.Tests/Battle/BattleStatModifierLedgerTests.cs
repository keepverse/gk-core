using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// T50 (action-todo.md Phase 12, spec-battle-live-stat-modifiers.md §1) — the ledger proven standalone,
/// no `BattleRunState` involvement, before anything in battle consumes it (T51/T52).
/// </summary>
public class BattleStatModifierLedgerTests
{
    static StatModifier Mod(ModifierOp op, double value, string sourceId) => new()
    {
        Channel = "atk", Op = op, Value = value, SourceId = sourceId,
    };

    [Fact]
    public void Flat_increased_and_more_compose_exactly_like_PhasedComposeStrategy()
    {
        var ledger = new BattleStatModifierLedger();
        ledger.Add("squad:0", "atk", "g1", Mod(ModifierOp.Flat, 10, "g1"));
        ledger.Add("squad:0", "atk", "g2", Mod(ModifierOp.Increased, 0.5, "g2"));
        ledger.Add("squad:0", "atk", "g3", Mod(ModifierOp.More, 0.2, "g3"));

        // (baseline + flat) x (1 + increased) x (1 + more) -- PhasedComposeStrategy's own contract
        // (StatComposer.cs:24-33), reused directly, not re-derived.
        var expected = (long)Math.Round((100.0 + 10.0) * 1.5 * 1.2);
        Assert.Equal(expected, ledger.Recompose("squad:0", "atk", baseline: 100));
    }

    [Fact]
    public void An_empty_ledger_returns_the_baseline_unchanged()
    {
        var ledger = new BattleStatModifierLedger();
        Assert.Equal(100L, ledger.Recompose("squad:0", "atk", baseline: 100));
    }

    [Fact]
    public void RemoveBySource_reverts_exactly_its_own_contribution()
    {
        var ledger = new BattleStatModifierLedger();
        ledger.Add("squad:0", "atk", "g1", Mod(ModifierOp.Flat, 10, "g1"));
        ledger.Add("squad:0", "atk", "g2", Mod(ModifierOp.Flat, 20, "g2"));
        Assert.Equal(130L, ledger.Recompose("squad:0", "atk", baseline: 100));

        ledger.RemoveBySource("squad:0", "g1");

        Assert.Equal(120L, ledger.Recompose("squad:0", "atk", baseline: 100)); // g2's +20 alone
    }

    [Fact]
    public void RemoveBySource_does_not_touch_a_different_actors_contribution()
    {
        var ledger = new BattleStatModifierLedger();
        ledger.Add("squad:0", "atk", "g1", Mod(ModifierOp.Flat, 10, "g1"));
        ledger.Add("wave:0", "atk", "g1", Mod(ModifierOp.Flat, 10, "g1")); // same sourceId, different actor

        ledger.RemoveBySource("squad:0", "g1");

        Assert.Equal(100L, ledger.Recompose("squad:0", "atk", baseline: 100));
        Assert.Equal(110L, ledger.Recompose("wave:0", "atk", baseline: 100));
    }
}
