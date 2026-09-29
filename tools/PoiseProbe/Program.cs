// Throwaway probe -- battle-tempo PU1/PU2, executed because the whole Core.Tests assembly is
// currently blocked by an unrelated, pre-existing build break in the loam-economy stream's
// uncommitted work (LoamPolicy field rename vs StructureCatalog.cs). FusionRpg.Core itself builds
// clean; this probe exercises the real compiled PoiseLedger/ActorResourcePools/Riposte against the
// exact scenarios PoiseLedgerTests.cs asserts, as hard evidence pending that unrelated fix.
// Deleted once Core.Tests builds again and the migrated suite can run directly.

using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;

// Configure from the real published tuning file -- the same one production loads (Server/Injector
// hosts). Locates the repo root by walking up from the probe's own bin output.
var dir = new DirectoryInfo(AppContext.BaseDirectory);
while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "tuning", "derived-stats.v2.json")))
    dir = dir.Parent;
if (dir == null) throw new InvalidOperationException("could not locate data/tuning/derived-stats.v2.json by walking up from " + AppContext.BaseDirectory);
var derivedStatsJson = File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", "derived-stats.v2.json"));
DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(derivedStatsJson));

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}
void CheckThrows(string name, Action action)
{
    try { action(); Console.WriteLine($"FAIL  {name} (did not throw)"); failures++; }
    catch (ArgumentOutOfRangeException) { Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { Console.WriteLine($"FAIL  {name} (wrong exception: {ex.GetType().Name})"); failures++; }
}

ActorDerivedSnapshot PoiseSnapshot(double max, double regenPerTick)
{
    var registry = DerivedStatRegistry.CreateDefault();
    var composer = new DerivedComposer(registry);
    return composer.Compose(new[]
    {
        new DerivedModifier(DerivedStatChannels.ResourceMax("poise"), DerivedModifierOp.Flat, max, SourceId: "probe"),
        new DerivedModifier(DerivedStatChannels.ResourceRegen("poise"), DerivedModifierOp.Flat, regenPerTick, SourceId: "probe"),
    });
}

// -- TryCommitSpendsTheFlatAmountOnce --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var ok = PoiseLedger.TryCommit(pools, 30, 0, derived);
    Check("TryCommitSpendsTheFlatAmountOnce", ok && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 70);
}

// -- RepeatedCommitsEachCostTheFlatAmountRegardlessOfOutcome --
{
    var derived = PoiseSnapshot(1000, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var a = PoiseLedger.TryCommit(pools, 50, 0, derived);
    var b = PoiseLedger.TryCommit(pools, 50, 0, derived);
    var c = PoiseLedger.TryCommit(pools, 50, 0, derived);
    Check("RepeatedCommitsEachCostTheFlatAmountRegardlessOfOutcome", a && b && c && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 850);
}

// -- RaisingWithInsufficientPoiseIsRefusedByAffordabilityNotSilence --
{
    var derived = PoiseSnapshot(10, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var ok = PoiseLedger.TryCommit(pools, 50, 0, derived);
    Check("RaisingWithInsufficientPoiseIsRefusedByAffordabilityNotSilence", !ok && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 10);
}

// -- CommitWithNegativeCostThrows --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    CheckThrows("CommitWithNegativeCostThrows", () => PoiseLedger.TryCommit(pools, -1, 0, derived));
}

// -- AbsorbDrainScalesWithWhatWasAbsorbedNotAFlatAmount --
{
    Check("AbsorbDrainScalesWithWhatWasAbsorbedNotAFlatAmount",
        PoiseLedger.AbsorbDrainAmount(500, 100) == 50 &&
        PoiseLedger.AbsorbDrainAmount(500, 1000) == 500 &&
        PoiseLedger.AbsorbDrainAmount(0, 1000) == 0);
}

// -- PayAbsorbDrainSpendsTheDrainedAmountWhenAffordable --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var drained = PoiseLedger.PayAbsorbDrain(pools, 400, 250, 0, derived);
    Check("PayAbsorbDrainSpendsTheDrainedAmountWhenAffordable", drained == 100 && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 0);
}

// -- PayAbsorbDrainNeverDrainsMoreThanThePoolHoldsAndNeverRefuses (the D9 fix) --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var drained = PoiseLedger.PayAbsorbDrain(pools, 10_000, 300, 0, derived); // ideal 3000, pool 100
    Check("PayAbsorbDrainNeverDrainsMoreThanThePoolHoldsAndNeverRefuses",
        drained == 100 && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 0 && PoiseLedger.IsExhausted(pools, 0, derived));
}

// -- LightAttritionDoesNotBreakTheGuardButASingleHeavyStopDoes --
{
    const long maxPoise = 1000, regenPerTick = 80; const int absorbShareMilli = 300;
    var attritionDerived = PoiseSnapshot(maxPoise, regenPerTick);
    var attritionPools = ActorResourcePools.CreateFull(attritionDerived, 0);
    PoiseLedger.PayAbsorbDrain(attritionPools, 100, absorbShareMilli, 0, attritionDerived);
    var notExhausted = !PoiseLedger.IsExhausted(attritionPools, 1, attritionDerived);
    var capped = pools_resolve_eq(attritionPools, attritionDerived, 1, maxPoise);

    var heavyDerived = PoiseSnapshot(maxPoise, 0);
    var heavyPools = ActorResourcePools.CreateFull(heavyDerived, 0);
    PoiseLedger.PayAbsorbDrain(heavyPools, 10_000, absorbShareMilli, 0, heavyDerived);
    var brokeOutright = PoiseLedger.IsExhausted(heavyPools, 0, heavyDerived);

    Check("LightAttritionDoesNotBreakTheGuardButASingleHeavyStopDoes", notExhausted && capped && brokeOutright);
}

bool pools_resolve_eq(ActorResourcePools pools, ActorDerivedSnapshot derived, long tick, long expected) =>
    pools.Resolve(PoiseLedger.ResourceId, tick, derived) == expected;

// -- SustainedAbsorbPressureAboveRegenEventuallyExhaustsThePool --
{
    const long maxPoise = 1000, regenPerTick = 80, drainPerRound = 120;
    var derived = PoiseSnapshot(maxPoise, regenPerTick);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var broke = false; var rounds = 0; long tick = 0;
    for (var round = 0; round < 100 && !broke; round++)
    {
        PoiseLedger.PayAbsorbDrain(pools, drainPerRound, 1000, tick, derived);
        rounds++;
        if (PoiseLedger.IsExhausted(pools, tick, derived)) { broke = true; break; }
        tick++;
    }
    Check("SustainedAbsorbPressureAboveRegenEventuallyExhaustsThePool", broke && rounds > 1);
}

// -- ASpendThroughPoiseLedgerIsVisibleToBothResolveAndSettleAll --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    PoiseLedger.TryCommit(pools, 40, 0, derived);
    var resolved = pools.Resolve(PoiseLedger.ResourceId, 0, derived);
    var settled = pools.SettleAll(0, derived);
    Check("ASpendThroughPoiseLedgerIsVisibleToBothResolveAndSettleAll",
        resolved == 60 && settled.ContainsKey(PoiseLedger.ResourceId) && settled[PoiseLedger.ResourceId] == 60);
}

// -- HoldTickIsJustAnOrdinaryPerTickSpend --
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var a = PoiseLedger.TryPayHoldTick(pools, 10, 0, derived);
    var b = PoiseLedger.TryPayHoldTick(pools, 10, 1, derived);
    Check("HoldTickIsJustAnOrdinaryPerTickSpend", a && b && pools.Resolve(PoiseLedger.ResourceId, 1, derived) == 80);
}

// -- NegativeAbsorbedAmountOrRatioIsRejected --
{
    CheckThrows("NegativeAbsorbedAmountIsRejected", () => PoiseLedger.AbsorbDrainAmount(-1, 100));
    CheckThrows("NegativeRatioIsRejected", () => PoiseLedger.AbsorbDrainAmount(100, -1));
}

// -- IsExhaustedTracksTheResolvedPoolNotAStoredFlag --
{
    var derived = PoiseSnapshot(50, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var beforeNotExhausted = !PoiseLedger.IsExhausted(pools, 0, derived);
    PoiseLedger.TryCommit(pools, 50, 0, derived);
    var afterExhausted = PoiseLedger.IsExhausted(pools, 0, derived);
    Check("IsExhaustedTracksTheResolvedPoolNotAStoredFlag", beforeNotExhausted && afterExhausted);
}

// -- Riposte: same-arithmetic properties (PoiseRuntime.Riposte deleted; Riposte.DamageFromSpentPoise is the survivor) --
{
    Check("RiposteDamageIsSpentPoiseTimesShareExactly", Riposte.DamageFromSpentPoise(500, 400) == 200);
    CheckThrows("RiposteNegativeSpentPoiseThrows", () => Riposte.DamageFromSpentPoise(-1, 500));
    CheckThrows("RiposteShareAboveOneThrows", () => Riposte.DamageFromSpentPoise(1000, 1001));
    var enormous = Riposte.DamageFromSpentPoise(2_000_000_000_000L, 300);
    Check("RiposteScalesWithNoPrivateCeiling", enormous == 600_000_000_000L);
}

// -- reaction-lane RL2: ReactionCounter.TryCounter -- the spend IS the attack (decision 12) --
{
    var derived = PoiseSnapshot(1000, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 400, riposteShareCapMilli: 300, 0, derived);
    Check("ASuccessfulCounterCommitsThePoiseAndDealsExactlyRiposteDamage",
        committed && damage == 120 && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 600);
}
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 500, riposteShareCapMilli: 300, 0, derived);
    Check("AnUnaffordableCounterRefusesAndChangesNothing",
        !committed && damage == 0 && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 100);
}
{
    var derived = PoiseSnapshot(1000, 0);
    var smallPools = ActorResourcePools.CreateFull(derived, 0);
    var bigPools = ActorResourcePools.CreateFull(derived, 0);
    var (_, smallDamage) = ReactionCounter.TryCounter(smallPools, 100, 300, 0, derived);
    var (_, bigDamage) = ReactionCounter.TryCounter(bigPools, 800, 300, 0, derived);
    Check("ABiggerSpendDealsMoreDamageButLeavesLessPoiseForWhatComesNext",
        bigDamage > smallDamage && bigPools.Resolve(PoiseLedger.ResourceId, 0, derived) < smallPools.Resolve(PoiseLedger.ResourceId, 0, derived));
}
{
    var derived = PoiseSnapshot(100, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 0, riposteShareCapMilli: 300, 0, derived);
    Check("ZeroSpendIsLegalAndDealsZeroDamage", committed && damage == 0 && pools.Resolve(PoiseLedger.ResourceId, 0, derived) == 100);
}
{
    const long enormous = 2_000_000_000_000L;
    var derived = PoiseSnapshot(enormous, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: enormous, riposteShareCapMilli: 300, 0, derived);
    Check("AnEnormousSpendConvertsProportionallyWithNoPrivateCeiling", committed && damage == enormous * 300 / 1000);
}
{
    var derived = PoiseSnapshot(1000, 0);
    var pools = ActorResourcePools.CreateFull(derived, 0);
    CheckThrows("AnOutOfRangeShareThrows", () => ReactionCounter.TryCounter(pools, 100, 1001, 0, derived));
}

// -- Confirms PoiseRuntime no longer exists anywhere in the loaded assembly (D9's single-pool claim) --
{
    var asm = typeof(PoiseLedger).Assembly;
    var poiseRuntimeType = asm.GetType("FusionRpg.Core.Combat.Guard.PoiseRuntime");
    Check("PoiseRuntimeTypeNoLongerExistsInTheAssembly", poiseRuntimeType is null);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
