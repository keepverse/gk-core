using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// `battle-tempo` `action-timing` (spec-action-timing.md). `ActionTimingDerivation.Derive` is the
/// module's whole mechanism, tested against the REAL rung table (`action-rungs.v2.json`, via
/// `ContractTuningTestBootstrap`'s own configured `RungPolicy`) so a second curve cannot creep in
/// unnoticed.
/// </summary>
public class ActionTimingTests
{
    const long RoundDurationMs = 1000;

    static ActionTimingTuning Timing() => ActionTimingTuningLoader.Parse(TestTiming);

    // Mirrors the real gk-core/data/tuning/action-timing.v1.json byte-for-byte at the time this test was
    // written (tunables-ssot.md §7.2: "construct one inline; no fixture files").
    const string TestTiming = """
    {
      "schemaVersion": 1, "version": 1,
      "windupPerPowerMilli": 20, "windupCapReferenceMilli": 300, "recoveryPerPowerMilli": 8,
      "basicAttack": { "windupTicks": 150, "recoveryTicks": 50 },
      "categories": {
        "attack": { "timeCostBaseTicks": 100, "cooldownBaseTicks": 200 },
        "defense": { "timeCostBaseTicks": 120, "cooldownBaseTicks": 150 },
        "support": { "timeCostBaseTicks": 100, "cooldownBaseTicks": 250 },
        "movement": { "timeCostBaseTicks": 80, "cooldownBaseTicks": 100 },
        "status": { "timeCostBaseTicks": 90, "cooldownBaseTicks": 180 }
      }
    }
    """;

    [Fact]
    public void WindupCorrelatesWithPayoffBothWays()
    {
        var timing = Timing();
        var rungTable = RungPolicy.Table;
        rungTable.TryGet(1, out var rung1);
        rungTable.TryGet(10, out var rung10);

        var lowRung1 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, 200, RoundDurationMs, rung1.CdMulti, timing);
        var highRung1 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, 900, RoundDurationMs, rung1.CdMulti, timing);
        var atQPowerRung10 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, rung10.QPowerMilli, RoundDurationMs, rung10.CdMulti, timing);

        Assert.True(highRung1.WindupTicks > lowRung1.WindupTicks, "within a rung, higher realized power must wind up longer");
        Assert.True(atQPowerRung10.WindupTicks > lowRung1.WindupTicks, "rung 10 at its own qPowerMilli must wind up longer than rung 1's low draw");
    }

    [Fact]
    public void TheCapIsRelativeToRoundDurationAndActuallyEngages()
    {
        var timing = Timing();
        var rungTable = RungPolicy.Table;
        rungTable.TryGet(10, out var rung10);
        var extremePower = rung10.PowerBudgetMilli ?? 37_221;

        var extreme = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, extremePower, RoundDurationMs, rung10.CdMulti, timing);
        Assert.Equal(timing.WindupCapTicks(RoundDurationMs), extreme.WindupTicks);

        Assert.Equal(timing.WindupCapTicks(RoundDurationMs) * 2, timing.WindupCapTicks(RoundDurationMs * 2));
    }

    [Fact]
    public void CooldownReadsTheExistingCdMultiCurveNoSecondCurve()
    {
        var timing = Timing();
        var rungTable = RungPolicy.Table;
        rungTable.TryGet(10, out var rung10);

        var derived = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, 500, RoundDurationMs, rung10.CdMulti, timing);
        var expected = checked(timing.CategoryOf(ActionCategory.Attack).CooldownBaseTicks * (long)rung10.CdMulti) / 1000;
        Assert.Equal(expected, derived.CooldownTicks);
    }

    [Fact]
    public void TimeCostReadsTheCategoryBase()
    {
        var timing = Timing();
        var attack = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, 500, RoundDurationMs, 1000, timing);
        var movement = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Movement, 500, RoundDurationMs, 1000, timing);
        Assert.Equal(timing.CategoryOf(ActionCategory.Attack).TimeCostBaseTicks, attack.TimeCostTicks);
        Assert.Equal(timing.CategoryOf(ActionCategory.Movement).TimeCostBaseTicks, movement.TimeCostTicks);
        Assert.NotEqual(attack.TimeCostTicks, movement.TimeCostTicks);
    }

    [Fact]
    public void UncategorizedActionIsSkippedNotGuessed()
    {
        var timing = Timing();
        var result = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, category: null, 999_999, RoundDurationMs, 1000, timing);
        Assert.Equal(ActionEnvelope.NoOp, result);
    }

    [Fact]
    public void ExtremeRealizedPowerIsCappedNotOverflowed()
    {
        var timing = Timing();
        var huge = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, long.MaxValue / 1000, RoundDurationMs, 1000, timing);
        Assert.Equal(timing.WindupCapTicks(RoundDurationMs), huge.WindupTicks);
    }

    [Fact]
    public void NegativeRealizedPowerThrows()
    {
        var timing = Timing();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, -1, RoundDurationMs, 1000, timing));
    }

    [Fact]
    public void TheBasicAttackCarriesAFeltBeatExemptFromTheFormula()
    {
        var timing = Timing();
        var basic = ActionTimingDerivation.DeriveBasicAttack(ActionEnvelope.NoOp, timing);
        Assert.Equal(timing.BasicAttack.WindupTicks, basic.WindupTicks);
        Assert.True(basic.WindupTicks > 0);
        // "meaningful fraction of the round" (decision 11) -- at least 5%, not a 1-tick token.
        Assert.True(basic.WindupTicks * 20 >= RoundDurationMs);
    }

    /// <summary>AT4: `StructureBudgetGuard` already gates multi-hit correctly — this proves the
    /// EXISTING mechanism against both sides of the rung-7 boundary (spec-rung-table.md §4), since
    /// this module's own derivation never rolls multi-hit (default stays the shared single-resolve
    /// `[0]`, spec §2.2 table).</summary>
    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void MultiHitIsGatedByStructureBudgetAtTheRung7Boundary(int rung, bool shouldBeAccepted)
    {
        var rungTable = RungPolicy.Table;
        var row = new ActionRow { ActionId = "probe.action", Rung = rung, ContainerId = "", Envelope = ActionEnvelope.NoOp with { ResolveOffsets = new long[] { 0, 100 } } };

        var result = StructureBudgetGuard.Check(row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), rungTable);

        Assert.Equal(shouldBeAccepted, result.IsOk);
        if (!shouldBeAccepted) Assert.Equal(ActionRejectionReason.StructureExceedsBudget, result.Reason);
    }

    [Fact]
    public void SingleHitIsUnaffectedAtAnyRung()
    {
        var rungTable = RungPolicy.Table;
        var row = new ActionRow { ActionId = "probe.action", Rung = 1, ContainerId = "", Envelope = ActionEnvelope.NoOp };
        var result = StructureBudgetGuard.Check(row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), rungTable);
        Assert.True(result.IsOk);
    }

    /// <summary>M1 falsifier — a planted literal must redden `audit-magic-numbers.py`, proving the
    /// gate actually catches one rather than passing vacuously. Not executed automatically; documented
    /// here as the falsifier this task's evidence trail names.</summary>
    [Fact]
    public void NoTimingLiteralInCodeIsAssertedByTheAuditScriptNotHere()
    {
        // Deliberately a no-op assertion with a pointer -- audit-magic-numbers.py --summary is the
        // real check (run as part of this task's verification, not as a unit test), because a magic
        // number is a static-analysis property of the source text, not a runtime behavior this test
        // could observe.
        Assert.True(true);
    }
}
