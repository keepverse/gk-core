using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// ST2 (spec-holder-rung-pricing.md, contract 5): cost is scaled at exactly one place — the
/// ledger. Compiling never pre-scales, and the ledger applies the holder's rung once.
/// </summary>
public class HolderRungPricingTests
{
    static readonly HashSet<string> OneAtomContainer = new(StringComparer.Ordinal) { "atom.strike" };

    static ActionRow BaseRow(string id = "skill.test", int rung = 1) => new()
    {
        ActionId = id,
        Name = "Test",
        Kind = ActionKind.Skill,
        ContainerId = "item.test",
        Rung = rung,
        Envelope = ActionEnvelope.NoOp with { ActionId = id },
        Targeting = new ActionTargetSpec(),
        ConditionsJson = null,
        Tags = Array.Empty<ActionTag>(),
    };

    static RungTable TwoRung() => new(cap: 2, new[]
    {
        new RungRow(1, 1, 1, 1, 1000, 1000, 1000, Array.Empty<string>()),
        new RungRow(2, 1, 1, 1, 1000, 2000, 1000, Array.Empty<string>()), // 2x
    });

    static ActorDerivedSnapshot Snapshot(double theta, params (string resourceId, double max, double regen)[] resources)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, theta, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    /// <summary>Spec test 8: compile carries the authored amount, unscaled.</summary>
    [Fact]
    public void CompileCarriesTheAuthoredAmountUnscaled()
    {
        var costs = new[] { new ActionCostRow("skill.test", "stamina", new ValueSpec(10, 20, RollPolicy.OnApply), ActionCostTiming.OnCommit) };
        var (rejection, compiled) = ActionCompiler.Compile(
            BaseRow(), costs, Array.Empty<ActionScopeRow>(), OneAtomContainer, boardAvailable: false, TwoRung());

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(10, compiled!.Costs[0].Amount.Min);
        Assert.Equal(20, compiled!.Costs[0].Amount.Max);
    }

    /// <summary>Spec test 5: a non-held action at authored rung r pays base × costMulti(r) exactly
    /// once, end to end through the real compiler and a real ledger. Proved by contrast: compile at
    /// authored rung 2 with a local table whose costMulti (2000) differs from the configured table's,
    /// then pay at rung 2 — the price follows the ledger's single scaling only. Reinstating the
    /// compile-time pre-scale compiles 20 instead of 10 and the ledger charges a second scaling on
    /// top, failing this test. The ledger expectation is computed from the configured row, never a
    /// literal; the compile table's 2000 is this test's own local fixture, not the tuning file.</summary>
    [Fact]
    public void ANonHeldActionPaysBaseTimesCostMultiExactlyOnce()
    {
        const int Base = 10;
        Assert.True(RungPolicy.Table.TryResolve(2, out var rung2));
        var scaledOnce = CurveTable.ApplyMilli(Base, rung2.CostMulti);

        var costs = new[] { new ActionCostRow("skill.test", "stamina", ValueSpec.Of(Base), ActionCostTiming.OnCommit) };
        var (rejection, compiled) = ActionCompiler.Compile(
            BaseRow(rung: 2), costs, Array.Empty<ActionScopeRow>(), OneAtomContainer, boardAvailable: false, TwoRung());
        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(Base, compiled!.Costs[0].Amount.Min); // authored, unscaled — the pre-scale is gone
        Assert.Equal(Base, compiled!.Costs[0].Amount.Max);

        var derived = Snapshot(theta: 0, ("stamina", 1_000_000, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var start = pools.Resolve("stamina", 0, derived);
        var ledgerCosts = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            ["skill.test"] = new[] { new ActionCostRow("skill.test", "stamina", compiled.Costs[0].Amount, ActionCostTiming.OnCommit) },
        };
        var ledger = new CostLedger(ledgerCosts, _ => pools, _ => derived, (_, _) => 2, () => 0);

        Assert.Equal(CostPayOutcome.Paid, ledger.TryPay("actor", "skill.test", ActionCostTiming.OnCommit, rng: null).Outcome);
        Assert.Equal(start - scaledOnce, pools.Resolve("stamina", 0, derived)); // one scaling, not two
    }

    /// <summary>Spec test 4: a held corpus action priced through a real `BattleEngine.Resolve` pays at
    /// `min(earnCount, ceiling)`. The catalog holds one action authored at rung 2 with window [1,2];
    /// squad:0 holds it at earnCount 9 — the window binds the holder to rung 2, and the same battle at
    /// earnCount 1 (no binding) prices it at rung 1. The ONLY variable across the two resolves is the
    /// held earnCount, so the band is what moves the price — not the setup, the seed, or the catalog.
    /// Production passes no `unlockStateFor` today, so there the bound stays inert (spec test 7's
    /// shape); golden movement is recorded, never re-blessed, in ST2.3's own step.</summary>
    [Fact]
    public void AHeldActionPricedThroughARealBattlePaysAtMinEarnCountCeiling()
    {
        const int Base = 40; // rung 2 prices above the CloseSetup qi fixture only when the window does NOT bind
        var row = BaseRow(id: "skill.windowed", rung: 2) with
        {
            ContainerId = "",
            RungBand = new RungBand(Floor: 1, Ceiling: 2),
        };
        var costs = new[] { new ActionCostRow("skill.windowed", "stamina", ValueSpec.Of(Base), ActionCostTiming.OnCommit) };
        var (rejection, compiled) = ActionCompiler.Compile(
            row, costs, Array.Empty<ActionScopeRow>(), OneAtomContainer, boardAvailable: false, TwoRung());
        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(new RungBand(1, 2), compiled!.RungBand); // the band survives compile

        var catalog = ActionCatalog.Build(new[] { compiled });
        var close = BattleGoldenTests.CloseSetup();
        var setup = close with
        {
            Squad = close.Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { "skill.windowed" } }
                : a).ToArray(),
        };
        var tuning = new UnlockTuning(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var bound = UnlockState.FromPersisted(9, new[] { new HeldUnlock("skill.windowed", EarnCountAtAcceptance: 9) });
        var unbound = UnlockState.FromPersisted(1, new[] { new HeldUnlock("skill.windowed", EarnCountAtAcceptance: 1) });

        var traceBound = new BattleTrace();
        BattleEngine.Resolve(setup, seed: 5501, trace: traceBound, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? bound : UnlockState.Empty(),
            unlockTuning: tuning);
        var traceUnbound = new BattleTrace();
        BattleEngine.Resolve(setup, seed: 5501, trace: traceUnbound, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? unbound : UnlockState.Empty(),
            unlockTuning: tuning);

        var boundAttacks = traceBound.Targets.Count(t => t.Contains(" squad:0->", StringComparison.Ordinal));
        var unboundAttacks = traceUnbound.Targets.Count(t => t.Contains(" squad:0->", StringComparison.Ordinal));
        Assert.True(unboundAttacks > boundAttacks); // rung 1 affordable where rung 2 (bound from 9) is not
    }
}