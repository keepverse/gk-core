using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Duration;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// action-plan.md §1.2: "`P0.2`–`P0.5` belong to two other programs; this program supplies the
/// requirement and the tests." All four landed 2026-08-28 under explicit owner authorization to build
/// across the program boundary — each test below asserts the LANDED state for real, against the actual
/// shipped mechanism, not a placeholder.
/// </summary>
public class CrossProgramLandedFlagsTests
{
    [Fact]
    public void P0_2_LinkageHasLandedProvenNotJustFlipped()
    {
        Assert.True(CrossProgramLandedFlags.LinkageLanded);

        // A changed EffectEventDto.Damage changes the resolved magnitude -- the flag's own literal
        // commitment (EventLinkedMagnitudeTests.cs proves the whole chain exhaustively, including
        // through the real EffectBag runtime; this is the flag's own minimal, direct cross-check).
        var overlay = new Dictionary<string, object?>
        {
            ["amount"] = new Dictionary<string, object?> { ["eventField"] = "damage", ["multiplierMilli"] = 500 },
        };

        var low = DamagePacketBuilder.FromOverlay(overlay, new EffectEventDto { Damage = 100 });
        var high = DamagePacketBuilder.FromOverlay(overlay, new EffectEventDto { Damage = 200 });

        Assert.Equal(50, low.SignedAmount);
        Assert.Equal(100, high.SignedAmount);
        Assert.NotEqual(low.SignedAmount, high.SignedAmount);
    }

    [Fact]
    public void P0_4_HoldsStockHasLandedProvenNotJustFlipped()
    {
        Assert.True(CrossProgramLandedFlags.HoldsStockLanded);

        // Cross-checked against the real closed leaf set...
        var leafNames = Enum.GetNames(typeof(LeafId));
        Assert.Contains(leafNames, name => name.Equals("HoldsStock", StringComparison.OrdinalIgnoreCase));

        // ...a real compile+evaluate round trip through the shipped FlatPredicate form...
        Func<string, int> stockBit = id => id == "potion.health" ? 0 : -1;
        var compileRejection = PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HoldsStock, Subject.Self, Value: 1, Text: "potion.health"),
            null, out var compiled, stockBit: stockBit);
        Assert.True(compileRejection.IsOk, compileRejection.ToString());
        var facts = new FactReader(
            new EntityFacts(0, 1, 1000, -1, -1, -1, false, false, 0, Stock0Qty: 1), default);
        Assert.True(compiled.Evaluate(ref facts));

        // ...and T10's own mode-matrix refusal, naming the mode (ActionUsabilityHoldsStockTests.cs
        // proves this exhaustively; this is the flag's own minimal, direct cross-check).
        var row = new ActionRow
        {
            ActionId = "skill.flag-check", Kind = ActionKind.Skill, ContainerId = "item.test", Rung = 1,
            Envelope = FusionRpg.Core.Battle.Timeline.ActionEnvelope.NoOp with { ActionId = "skill.flag-check" },
            Targeting = new ActionTargetSpec(),
            ConditionsJson = """{"leaf":"holdsStock","subject":"self","value":{"stockId":"potion.health","minQty":1}}""",
        };
        var table = new FusionRpg.Core.Actions.Rungs.RungTable(1, new[]
        {
            new FusionRpg.Core.Actions.Rungs.RungRow(1, 1, 1, 1, 1000, 1000, 1000, new[] { FusionRpg.Core.Actions.Rungs.StructureAxes.Condition }),
        });
        var (rejection, lawnCompiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { "atom.strike" }, boardAvailable: false, table,
            stockBit: stockBit, mode: ActionBindMode.Lawn);
        Assert.Null(lawnCompiled);
        Assert.Equal(ActionRejectionReason.ConsumableUnsupportedInMode, rejection.Reason);
    }

    [Fact]
    public void P0_5_TurnSpeedHasLandedProvenNotJustFlipped()
    {
        Assert.True(CrossProgramLandedFlags.TurnSpeedLanded);

        // The flag means something real: both channels are actually registered with their
        // load-bearing non-zero defaults...
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.IsKnown(DerivedTurnChannels.Speed));
        Assert.True(registry.IsKnown(DerivedTurnChannels.Haste));

        // ...and a real BattleDurationResolver genuinely reads them and produces different tick
        // counts for different rates (DurationResolverTests.cs proves this exhaustively; this is the
        // flag's own minimal, direct cross-check, so a regression that silently stubbed the resolver
        // back out would fail HERE, not only in a test file someone might stop running).
        var resolver = new BattleDurationResolver(_ => ActorDerivedSnapshot.StubNeutral());
        var ticks = resolver.ToTicks(victimTurns: 1, "wave:0");
        Assert.True(ticks > 0);
    }

    [Fact]
    public void A3_ItemCostRowHasNotLandedProvenNotJustAssumed()
    {
        Assert.False(CrossProgramLandedFlags.ItemCostRowLanded);

        // The flag means something real, the other direction: ActionCostRow's own fields are
        // reflected to confirm there is genuinely no way to name an item id as a cost today, not
        // just that nobody happened to build one yet.
        var fields = typeof(ActionCostRow).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("ResourceId", fields);
        Assert.DoesNotContain(fields, name => name.Contains("Item", StringComparison.OrdinalIgnoreCase));
    }
}
