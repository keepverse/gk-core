using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// The commit half of the <c>holdsStock</c> precondition — item module 18's P5.2 follow-up.
///
/// <para><b>What was broken.</b> <c>A3</c> §8 and <c>A4</c> §3a (both revised 2026-08-27) settle that
/// consuming a consumable is a PRECONDITION rather than a cost, and T10 shipped the check on
/// 2026-08-28 as <c>LeafId.HoldsStock</c>. Nothing ever took the stack. A battle-context action gated
/// on <c>holdsStock</c> therefore fired for free, forever, as long as the player held one potion.</para>
///
/// <para>Named <c>ActionUsability*</c> so it joins T10's own declared
/// <c>--filter ~ActionUsability</c> rather than starting a second filter for one concept.</para>
/// </summary>
public class ActionUsabilityStockSpendTests
{
    const string Actor = "actor-1";
    static readonly Func<string, int> StockBit = id => id switch { "potion.health" => 0, "bandage" => 1, _ => -1 };

    /// <summary>An in-memory <see cref="IStockLedger"/> with the same all-or-nothing contract the real
    /// <c>RpgStore.TrySpendStock</c> implements in one transaction. The DAL's own version of these
    /// properties is proven against real SQLite in <c>ActionStockSpendStoreTests</c>; this fixture is
    /// here so the Core-side wiring is testable without a store.</summary>
    sealed class FakeStockLedger : IStockLedger
    {
        readonly Dictionary<string, long> _held = new(StringComparer.Ordinal);
        public int Calls { get; private set; }

        public FakeStockLedger Holding(string stockId, long qty)
        {
            _held[stockId] = qty;
            return this;
        }

        public long QtyOf(string stockId) => _held.TryGetValue(stockId, out var q) ? q : 0;

        public StockSpendResult TrySpend(string actorKey, string actionId, IReadOnlyList<StockDemand> demands)
        {
            Calls++;

            // validate all, then consume all -- CostLedger's own shape, so a partial spend is
            // structurally impossible rather than merely avoided.
            foreach (var d in demands)
                if (QtyOf(d.StockId) < d.MinQty) return StockSpendResult.Missing(d.StockId);
            foreach (var d in demands)
                _held[d.StockId] = QtyOf(d.StockId) - d.MinQty;

            return StockSpendResult.Spent;
        }
    }

    static ActionRow Row(string id, string? conditionsJson) => new()
    {
        ActionId = id,
        Name = "Test",
        Kind = ActionKind.Skill,
        ContainerId = "item.test",
        Rung = 1,
        Envelope = ActionEnvelope.NoOp with { ActionId = id },
        Targeting = new ActionTargetSpec(),
        ConditionsJson = conditionsJson,
    };

    static RungTable ConditionRung() => new(1, new[]
    {
        new RungRow(1, 1, 1, 1, 1000, 1000, 1000, new[] { StructureAxes.Condition }),
    });

    static (ActionRejection Rejection, CompiledAction? Compiled) Compile(
        string id, string? conditionsJson, ActionBindMode mode = ActionBindMode.Battle) =>
        ActionCompiler.Compile(
            Row(id, conditionsJson), Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { "atom.strike" }, boardAvailable: false, ConditionRung(),
            stockBit: StockBit, mode: mode);

    const string OnePotion =
        """{"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}""";

    // ---- the demand survives compilation ---------------------------------------------------------

    [Fact]
    public void ABattleContextConsumableActionCarriesItsStockDemandThroughCompilation()
    {
        // The whole reason StockDemand exists: PredicateCompiler interns "potion.health" to slot 0,
        // so after compiling the tree alone can no longer name what the action requires.
        var (rejection, compiled) = Compile("skill.potion", OnePotion);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled!.StockDemands);
        var demand = Assert.Single(compiled.StockDemands!);
        Assert.Equal("potion.health", demand.StockId);
        Assert.Equal(1L, demand.MinQty);
    }

    [Fact]
    public void AnActionWithNoHoldsStockLeafCarriesNoDemandAtAll()
    {
        var (rejection, compiled) = Compile(
            "skill.plain", """{"leaf":"sideIs","subject":"target","value":"zombie"}""");

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.Null(compiled!.StockDemands);
    }

    [Fact]
    public void TwoLeavesOnOneStackCollapseToTheStrictestDemandNeverTheSum()
    {
        // Holding 2 satisfies both "at least 1" and "at least 2", so 2 is what the condition required
        // and 2 is what firing takes. Summing would charge 3 for a condition 2 satisfies.
        var tree = """
            {"op":"and","children":[
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}},
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":2}}
            ]}
            """;
        var (rejection, compiled) = Compile("skill.two", tree);

        Assert.True(rejection.IsOk, rejection.ToString());
        var demand = Assert.Single(compiled!.StockDemands!);
        Assert.Equal(2L, demand.MinQty);
    }

    [Fact]
    public void TwoDifferentStacksBothBecomeDemandsInAuthoredOrder()
    {
        var tree = """
            {"op":"and","children":[
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"bandage","minQty":3}},
                {"leaf":"sideIs","subject":"target","value":"zombie"},
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}
            ]}
            """;
        var (rejection, compiled) = Compile("skill.kit", tree);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.Equal(
            new[] { new StockDemand("bandage", 3), new StockDemand("potion.health", 1) },
            compiled!.StockDemands);
    }

    // ---- a demand that firing does not prove is refused, never guessed ---------------------------

    [Theory]
    [InlineData("""{"op":"or","children":[{"leaf":"sideIs","subject":"target","value":"zombie"},{"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}]}""")]
    [InlineData("""{"op":"not","children":[{"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}]}""")]
    public void AHoldsStockLeafTheActionCanFireWithoutIsRefusedByName(string tree)
    {
        var (rejection, compiled) = Compile("skill.maybe", tree);

        Assert.Null(compiled);
        Assert.Equal(ActionRejectionReason.ConsumableStockDemandNotGuaranteed, rejection.Reason);
        Assert.Contains("potion.health", rejection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LawnModeStillRefusesFirstSoTheModeMatrixOutranksTheNewCheck()
    {
        // Both refusals apply to this row. T10's mode answer is the more specific one and stays first,
        // so its message (and its typed reason) do not change for anyone.
        var tree = """{"op":"or","children":[{"leaf":"sideIs","subject":"target","value":"zombie"},""" + OnePotion + "]}";
        var (rejection, _) = Compile("skill.maybe", tree, ActionBindMode.Lawn);

        Assert.Equal(ActionRejectionReason.ConsumableUnsupportedInMode, rejection.Reason);
    }

    // ---- (a) firing spends -----------------------------------------------------------------------

    [Fact]
    public void FiringABattleContextConsumableActionDecrementsTheStack()
    {
        var (_, compiled) = Compile("skill.potion", OnePotion);
        var ledger = new FakeStockLedger().Holding("potion.health", 3);
        var commit = new ActionStockCommit(ledger);

        var result = commit.TryCommit(Actor, compiled!);

        Assert.True(result.IsSpent);
        Assert.Equal(2, ledger.QtyOf("potion.health"));
        Assert.True(result.AsRefusal().IsUsable);
    }

    [Fact]
    public void EachFiringSpendsAgainSoAStackOfThreeIsGoneAfterThree()
    {
        var (_, compiled) = Compile("skill.potion", OnePotion);
        var ledger = new FakeStockLedger().Holding("potion.health", 3);
        var commit = new ActionStockCommit(ledger);

        for (var i = 0; i < 3; i++) Assert.True(commit.TryCommit(Actor, compiled!).IsSpent);

        Assert.Equal(0, ledger.QtyOf("potion.health"));
        Assert.False(commit.TryCommit(Actor, compiled!).IsSpent);
    }

    // ---- (b) an exhausted stack refuses, and spends nothing --------------------------------------

    [Fact]
    public void AnExhaustedStackRefusesWithMissingStockNamingTheStack()
    {
        var (_, compiled) = Compile("skill.potion", OnePotion);
        var commit = new ActionStockCommit(new FakeStockLedger().Holding("potion.health", 0));

        var result = commit.TryCommit(Actor, compiled!);

        Assert.Equal(StockSpendOutcome.MissingStock, result.Outcome);
        Assert.Equal("potion.health", result.ShortfallStockId);

        // The typed refusal spec-usability-conditions.md §2 named and nothing ever raised until now.
        var refusal = result.AsRefusal();
        Assert.Equal(UsabilityReason.MissingStock, refusal.Reason);
        Assert.Equal("potion.health", refusal.Detail);
    }

    [Fact]
    public void AShortfallOnOneDemandSpendsNeitherStack()
    {
        var tree = """
            {"op":"and","children":[
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"bandage","minQty":1}},
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":5}}
            ]}
            """;
        var (_, compiled) = Compile("skill.kit", tree);
        var ledger = new FakeStockLedger().Holding("bandage", 9).Holding("potion.health", 1);

        var result = new ActionStockCommit(ledger).TryCommit(Actor, compiled!);

        Assert.Equal(StockSpendOutcome.MissingStock, result.Outcome);
        Assert.Equal("potion.health", result.ShortfallStockId);
        Assert.Equal(9, ledger.QtyOf("bandage")); // the affordable line is untouched
    }

    // ---- the inert default, and the short circuit ------------------------------------------------

    [Fact]
    public void WithNoLedgerWiredAConsumableActionRefusesRatherThanFiringFree()
    {
        // The opposite posture from AlwaysAffordable, deliberately: an unwired cost seam costs the
        // player nothing, an unwired stock seam would hand out unlimited consumables.
        var (_, compiled) = Compile("skill.potion", OnePotion);

        var result = new ActionStockCommit(NoStockLedger.Instance).TryCommit(Actor, compiled!);

        Assert.Equal(StockSpendOutcome.MissingStock, result.Outcome);
        Assert.Equal("potion.health", result.ShortfallStockId);
    }

    [Fact]
    public void AnActionWithNoDemandsNeverTouchesTheLedgerAtAll()
    {
        var (_, compiled) = Compile(
            "skill.plain", """{"leaf":"sideIs","subject":"target","value":"zombie"}""");
        var ledger = new FakeStockLedger();
        var commit = new ActionStockCommit(ledger);

        Assert.True(commit.TryCommit(Actor, compiled!).IsSpent);

        // Measurable, not argued: the ordinary action pays one null check, not a ledger round trip.
        Assert.Equal(0, commit.LedgerCalls);
        Assert.Equal(0, ledger.Calls);

        // …and NoStockLedger agrees, so the short circuit is not the only thing keeping it safe.
        Assert.True(new ActionStockCommit(NoStockLedger.Instance).TryCommit(Actor, compiled!).IsSpent);
    }
}
