using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// `lawn-combat-wire` T8 (spec-lawn-action-bridge.md): field-level golden proving
/// <see cref="BasicAttackFactory.Create"/> reproduces, in every field, the row `BattleRunState.cs`
/// used to hand-build inline before this extraction — "no field's VALUE changed by this move" is an
/// assertion here, not a claim.
///
/// <para><b>Why "expected" is reconstructed rather than reflected out of a live <c>BattleRunState</c>
/// instance:</b> that type is a private nested class of <see cref="BattleEngine"/> with no public
/// accessor for the whole row (only <c>BasicAttackEnvelopeCompiled</c>, the <c>Envelope</c> field
/// alone). <c>expected</c> below mirrors the exact literal construction the pre-extraction field
/// initializer used — same authored constants (<see cref="BattleEngine.BasicAttackEnvelope"/>,
/// <see cref="BattleEngine.BasicAttackTargeting"/>), same compiler calls
/// (<see cref="ActionTimingDerivation.DeriveBasicAttack"/>, <see cref="TargetSpecCompiler.Compile"/>,
/// <see cref="PredicateCompiler.Always"/>) — so a divergence here means the factory's construction
/// itself changed, not that the test's reference differs for an unrelated reason.</para>
///
/// <para><b>Why per-field, not <c>Assert.Equal(expected, actual)</c>:</b> <see cref="CompiledAction"/>
/// is a record, but two of its reference-typed fields do not get useful structural equality from the
/// compiler-generated <c>Equals</c> — <c>Tags</c>/<c>Costs</c>/<c>Scopes</c> are <c>IReadOnlyList</c>
/// backed by freshly-allocated arrays (compared by reference), and <c>Targeting</c>
/// (<see cref="CompiledTargetSpec"/>) holds a <c>TargetSpec[]</c> of plain mutable classes with no
/// <c>Equals</c> override at all. Every field is asserted explicitly below instead, with
/// <c>Targeting</c> compared via JSON serialization (its elements already carry
/// <c>JsonPropertyName</c> — the shape the wire already treats as this type's identity) so the golden
/// is a true content comparison, not a coincidence of reference equality.</para>
/// </summary>
public class BasicAttackFactoryGoldenTests
{
    static CompiledAction Expected() => new(
        ActionId: BattleEngine.BasicAttackEnvelope.ActionId,
        Kind: ActionKind.Basic,
        Rung: 0,
        Tags: new[] { ActionTag.Offensive },
        Enabled: true,
        Revision: 0,
        Grantable: false,
        DefaultAttackEligible: true,
        ContainerId: "",
        Envelope: ActionTimingDerivation.DeriveBasicAttack(BattleEngine.BasicAttackEnvelope, ActionTimingPolicy.Tuning),
        Targeting: TargetSpecCompiler.Compile(BattleEngine.BasicAttackTargeting),
        MinRange: 0,
        MaxRange: int.MaxValue,
        RangeChannel: null,
        RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    [Fact]
    public void Factory_reproduces_every_field_of_the_pre_extraction_row()
    {
        var expected = Expected();
        var actual = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);

        Assert.Equal(expected.ActionId, actual.ActionId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Rung, actual.Rung);
        Assert.Equal(expected.Tags, actual.Tags); // both single-element [Offensive] -- IReadOnlyList<T> gets sequence equality from xunit's Assert.Equal
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Grantable, actual.Grantable);
        Assert.Equal(expected.DefaultAttackEligible, actual.DefaultAttackEligible);
        Assert.Equal(expected.ContainerId, actual.ContainerId);
        Assert.Equal(expected.Envelope, actual.Envelope); // ActionEnvelope hand-rolls a correct value Equals
        Assert.Equal(expected.MinRange, actual.MinRange);
        Assert.Equal(expected.MaxRange, actual.MaxRange);
        Assert.Equal(expected.RangeChannel, actual.RangeChannel);
        Assert.Equal(expected.RequiresLineOfSight, actual.RequiresLineOfSight);
        Assert.Equal(expected.Costs, actual.Costs); // both Array.Empty<CompiledActionCost>()
        Assert.Equal(expected.Scopes, actual.Scopes); // both Array.Empty<ActionScopeRow>()
        Assert.Equal(expected.Category, actual.Category); // both null (unchanged trailing default)
        Assert.Equal(expected.ProjectilePenalties, actual.ProjectilePenalties); // both the trailing default
        Assert.Equal(expected.StockDemands, actual.StockDemands); // both null

        // `PredicateCompiler.Always` is a process-wide singleton -- reference equality is the correct
        // check (not just "an equivalent predicate"), since the compiled predicate is a behavioural
        // object, never data (spec-lawn-action-bridge.md's own "cannot cross a wire" point).
        Assert.Same(PredicateCompiler.Always, actual.Condition);
        Assert.Same(expected.Condition, actual.Condition);

        AssertTargetingEqual(expected.Targeting, actual.Targeting);
    }

    [Fact]
    public void Factory_output_is_deterministic_across_calls()
    {
        var first = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);
        var second = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);

        Assert.Equal(first.ActionId, second.ActionId);
        Assert.Equal(first.Envelope, second.Envelope);
        AssertTargetingEqual(first.Targeting, second.Targeting);
    }

    static void AssertTargetingEqual(CompiledTargetSpec expected, CompiledTargetSpec actual)
    {
        Assert.Equal(expected.IsSelf, actual.IsSelf);
        Assert.Equal(expected.PerSide.Length, actual.PerSide.Length);

        var options = new JsonSerializerOptions();
        for (var i = 0; i < expected.PerSide.Length; i++)
        {
            var expectedJson = JsonSerializer.Serialize(expected.PerSide[i], options);
            var actualJson = JsonSerializer.Serialize(actual.PerSide[i], options);
            Assert.Equal(expectedJson, actualJson);
        }
    }
}
