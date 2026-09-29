// Throwaway probe -- battle-tempo AT1-AT4, executed because Core.Tests is blocked (see PoiseProbe's
// header). Exercises ActionTimingDerivation and StructureBudgetGuard directly against real compiled
// code and the real action-rungs.v2.json / action-timing.v1.json this session published.

using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;

var dir = new DirectoryInfo(AppContext.BaseDirectory);
while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "tuning", "action-timing.v1.json")))
    dir = dir.Parent;
if (dir == null) throw new InvalidOperationException("could not locate data/tuning by walking up from " + AppContext.BaseDirectory);
string Load(string rel) => File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", rel));

var timing = ActionTimingTuningLoader.Parse(Load("action-timing.v1.json"));
var rungTable = RungTableLoader.Parse(Load("action-rungs.v2.json"));

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

const long RoundDurationMs = 1000;

// -- AT2: wind-up correlates with payoff, both ways --
{
    rungTable.TryGet(1, out var rung1);
    rungTable.TryGet(10, out var rung10);

    var lowPowerRung1 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: 200, RoundDurationMs, rung1.CdMulti, timing);
    var highPowerRung1 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: 900, RoundDurationMs, rung1.CdMulti, timing);
    var lowPowerRung10 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: 200, RoundDurationMs, rung10.CdMulti, timing);
    var highPowerRung10 = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: (long)rung10.QPowerMilli, RoundDurationMs, rung10.CdMulti, timing);

    Console.WriteLine($"  rung1 low={lowPowerRung1.WindupTicks} high={highPowerRung1.WindupTicks}  rung10 low={lowPowerRung10.WindupTicks} atQPower={highPowerRung10.WindupTicks}");
    Check("WithinRungHigherPowerWindsUpLonger", highPowerRung1.WindupTicks > lowPowerRung1.WindupTicks);
    Check("SamePowerAcrossRungsIsIdentical (rung-invariant formula, as designed)", lowPowerRung1.WindupTicks == lowPowerRung10.WindupTicks);
    Check("HigherRealizedPowerAtRung10WindsUpLongerThanRung1sLow", highPowerRung10.WindupTicks > lowPowerRung1.WindupTicks);
}

// -- D1: the cap is relative to roundDurationMs, never absolute, and actually engages --
{
    rungTable.TryGet(10, out var rung10);
    // An extreme realized power (near powerBudgetMilli's own ceiling) must be CAPPED, not left to run away.
    var extreme = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: rung10.PowerBudgetMilli ?? 37_221, RoundDurationMs, rung10.CdMulti, timing);
    var cap = timing.WindupCapTicks(RoundDurationMs);
    Console.WriteLine($"  extreme windup={extreme.WindupTicks} cap={cap} (round={RoundDurationMs})");
    Check("CapEngagesForAnExtremeRealizedPower", extreme.WindupTicks == cap);

    var capDoubleRound = timing.WindupCapTicks(RoundDurationMs * 2);
    Check("CapScalesRelativeToRoundDuration", capDoubleRound == cap * 2);
}

// -- AT2: cooldown reads the EXISTING cdMulti curve, no second curve --
{
    rungTable.TryGet(10, out var rung10);
    var derived = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, realizedPowerMilli: 500, RoundDurationMs, rung10.CdMulti, timing);
    var expectedCooldown = checked(timing.CategoryOf(ActionCategory.Attack).CooldownBaseTicks * (long)rung10.CdMulti) / 1000;
    Console.WriteLine($"  rung10.cdMulti={rung10.CdMulti} derived cooldown={derived.CooldownTicks} expected={expectedCooldown}");
    Check("CooldownEqualsCategoryBaseTimesCdMulti", derived.CooldownTicks == expectedCooldown);
}

// -- AT2: category timeCost reads the category base --
{
    rungTable.TryGet(1, out var rung1);
    var attack = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, 500, RoundDurationMs, rung1.CdMulti, timing);
    var movement = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Movement, 500, RoundDurationMs, rung1.CdMulti, timing);
    Check("TimeCostReadsCategoryBase", attack.TimeCostTicks == timing.CategoryOf(ActionCategory.Attack).TimeCostBaseTicks
        && movement.TimeCostTicks == timing.CategoryOf(ActionCategory.Movement).TimeCostBaseTicks
        && attack.TimeCostTicks != movement.TimeCostTicks);
}

// -- Uncategorized action: skip, do not guess --
{
    rungTable.TryGet(1, out var rung1);
    var result = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, category: null, 999_999, RoundDurationMs, rung1.CdMulti, timing);
    Check("UncategorizedActionReturnsBaselineUnchanged", result == ActionEnvelope.NoOp);
}

// -- Overflow: does not throw or wrap at an extreme realized power --
{
    rungTable.TryGet(10, out var rung10);
    var huge = ActionTimingDerivation.Derive(ActionEnvelope.NoOp, ActionCategory.Attack, long.MaxValue / 1000, RoundDurationMs, rung10.CdMulti, timing);
    Check("ExtremeRealizedPowerCappedNotOverflowed", huge.WindupTicks == timing.WindupCapTicks(RoundDurationMs));
}

// -- Basic attack: token wind-up, exempt from the formula, decision 11 (a felt beat) --
{
    var basic = ActionTimingDerivation.DeriveBasicAttack(ActionEnvelope.NoOp, timing);
    Check("BasicAttackCarriesItsOwnTokenWindup", basic.WindupTicks == timing.BasicAttack.WindupTicks && basic.WindupTicks > 0);
    // "meaningful fraction of the round" -- at least, say, 5% of the round, not a 1-tick token.
    Check("BasicAttackWindupIsAFeltBeatNotAMinimalToken", basic.WindupTicks * 20 >= RoundDurationMs); // >= 5%
}

// -- AT4: StructureBudgetGuard already gates multi-hit correctly at both sides of rung 7 --
{
    var multiHitEnvelope = ActionEnvelope.NoOp with { ResolveOffsets = new long[] { 0, 100 } };
    var singleHitEnvelope = ActionEnvelope.NoOp;

    ActionRow RowAt(int rung, ActionEnvelope env) => new() { ActionId = "probe.action", Rung = rung, Envelope = env, ContainerId = "" };

    var belowRefused = StructureBudgetGuard.Check(RowAt(6, multiHitEnvelope), Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), rungTable);
    var atOrAboveAccepted = StructureBudgetGuard.Check(RowAt(7, multiHitEnvelope), Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), rungTable);
    var singleHitAtLowRung = StructureBudgetGuard.Check(RowAt(1, singleHitEnvelope), Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), rungTable);

    Console.WriteLine($"  rung6 multi-hit: {belowRefused.Reason}  rung7 multi-hit: {atOrAboveAccepted.Reason}  rung1 single-hit: {singleHitAtLowRung.Reason}");
    Check("MultiHitBelowRung7IsRefused", !belowRefused.IsOk && belowRefused.Reason == ActionRejectionReason.StructureExceedsBudget);
    Check("MultiHitAtRung7IsAccepted", atOrAboveAccepted.IsOk);
    Check("SingleHitAtRung1IsUnaffected", singleHitAtLowRung.IsOk);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
