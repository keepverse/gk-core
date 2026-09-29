using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T10 (action-todo.md, spec-usability-conditions.md's `holdsStock` mode matrix). Named
/// <c>ActionUsability*</c> so this file matches T10's own declared <c>--filter ~ActionUsability</c>.
/// Unblocked by building `P0.4` (the `holdsStock` leaf itself, effect-atom program) across the
/// program boundary under explicit owner authorization.
/// </summary>
public class ActionUsabilityHoldsStockTests
{
    static readonly Func<string, int> StockBit = id => id switch { "potion.health" => 0, "bandage" => 1, _ => -1 };

    static ActionRow Row(string id, string? conditionsJson, int rung = 1) => new()
    {
        ActionId = id,
        Name = "Test",
        Kind = ActionKind.Skill,
        ContainerId = "item.test",
        Rung = rung,
        Envelope = ActionEnvelope.NoOp with { ActionId = id },
        Targeting = new ActionTargetSpec(),
        ConditionsJson = conditionsJson,
    };

    static RungTable ConditionRung() => new(1, new[]
    {
        new RungRow(1, 1, 1, 1, 1000, 1000, 1000, new[] { StructureAxes.Condition }),
    });

    const string HoldsStockJson = """{"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}""";

    // ---- LeafId / validation --------------------------------------------------------------------

    [Fact]
    public void AHoldsStockLeafWithNoStockIdIsRejected() =>
        Assert.False(PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1), null, out _).IsOk);

    [Fact]
    public void AHoldsStockLeafWithMinQtyBelowOneIsRejected() =>
        Assert.False(PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 0, Text: "potion.health"), null, out _).IsOk);

    [Fact]
    public void AWellFormedHoldsStockLeafCompiles() =>
        Assert.True(PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "potion.health"),
            null, out _, stockBit: StockBit).IsOk);

    // ---- JSON grammar ---------------------------------------------------------------------------

    [Fact]
    public void TheCompoundObjectValueParsesStockIdAndMinQty()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(HoldsStockJson);
        var rejection = AtomJson.TryReadPredicate(doc.RootElement, out var node);

        Assert.True(rejection.IsOk, rejection.ToString());
        var leaf = Assert.IsType<PredicateNode.Leaf>(node);
        Assert.Equal(LeafId.HoldsStock, leaf.Id);
        Assert.Equal("potion.health", leaf.Text);
        Assert.Equal(1, leaf.Value);
    }

    // ---- real evaluation through FactReader ----------------------------------------------------

    [Theory]
    [InlineData(1, true)]   // holds exactly minQty
    [InlineData(5, true)]   // holds more than minQty
    [InlineData(0, false)]  // holds none
    public void TheCompiledLeafEvaluatesAgainstTheRealStockQty(int actualQty, bool expected)
    {
        var rejection = PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "potion.health"),
            null, out var compiled, stockBit: StockBit);
        Assert.True(rejection.IsOk, rejection.ToString());

        var self = new EntityFacts(0, 1, 1000, -1, -1, -1, false, false, 0, Stock0Qty: actualQty);
        var target = new EntityFacts(1, 2, 1000, -1, -1, -1, false, false, 0);
        var facts = new FactReader(self, target);

        Assert.Equal(expected, compiled.Evaluate(ref facts));
    }

    [Fact]
    public void AnUnresolvableStockIdReadsAsZeroNeverThrows()
    {
        // stockBit returns -1 for an unknown id; FactReader.StockQty reads out-of-range as 0.
        var rejection = PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "unknown-item"),
            null, out var compiled, stockBit: StockBit);
        Assert.True(rejection.IsOk, rejection.ToString());

        var self = new EntityFacts(0, 1, 1000, -1, -1, -1, false, false, 0, Stock0Qty: 99, Stock1Qty: 99, Stock2Qty: 99, Stock3Qty: 99);
        var facts = new FactReader(self, self);

        Assert.False(compiled.Evaluate(ref facts)); // never true for a stock id nobody interned
    }

    // ---- T10's own acceptance: the mode matrix ---------------------------------------------------

    [Fact]
    public void BattleModeResolvesAConsumableActionAtAssembly()
    {
        var row = Row("skill.potion", HoldsStockJson);
        var (rejection, compiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { "atom.strike" }, boardAvailable: false, ConditionRung(),
            stockBit: StockBit, mode: ActionBindMode.Battle);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled);
    }

    [Fact]
    public void LawnModeRefusesToBindAConsumableActionNamingTheMode()
    {
        var row = Row("skill.potion", HoldsStockJson);
        var (rejection, compiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { "atom.strike" }, boardAvailable: false, ConditionRung(),
            stockBit: StockBit, mode: ActionBindMode.Lawn);

        Assert.Null(compiled);
        Assert.Equal(ActionRejectionReason.ConsumableUnsupportedInMode, rejection.Reason);
        Assert.Contains("Lawn", rejection.Detail); // an unsupported mode named, never one left unstated
    }

    [Fact]
    public void ANonConsumableActionBindsFineInBothModes()
    {
        var plainCondition = """{"leaf":"sideIs","subject":"target","value":"zombie"}""";
        var row = Row("skill.plain", plainCondition);

        foreach (var mode in new[] { ActionBindMode.Battle, ActionBindMode.Lawn })
        {
            var (rejection, compiled) = ActionCompiler.Compile(
                row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
                new HashSet<string> { "atom.strike" }, boardAvailable: false, ConditionRung(),
                stockBit: StockBit, mode: mode);

            Assert.True(rejection.IsOk, $"mode {mode}: {rejection}");
            Assert.NotNull(compiled);
        }
    }

    [Fact]
    public void AConsumableInsideAndOrIsStillDetected()
    {
        var nested = """
            {"op":"and","children":[
                {"leaf":"sideIs","subject":"target","value":"zombie"},
                {"leaf":"holdsStock","subject":"self","value":{"stockId":"bandage","minQty":1}}
            ]}
            """;
        var row = Row("skill.nested", nested);

        var (rejection, compiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { "atom.strike" }, boardAvailable: false, ConditionRung(),
            stockBit: StockBit, mode: ActionBindMode.Lawn);

        Assert.Null(compiled);
        Assert.Equal(ActionRejectionReason.ConsumableUnsupportedInMode, rejection.Reason);
    }
}
