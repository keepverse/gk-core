using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T25 (action-todo.md, spec-defence-actions.md §1): the real <see cref="IStanceCheck"/>. Gate-0
/// behavior is proven directly against <see cref="UsabilityEvaluator"/>'s own signature, not just
/// against <see cref="StanceRuntime.Check"/> in isolation, so the seam is proven filled correctly
/// end to end.
/// </summary>
public class DefenceActionStanceTests
{
    static StatusRuntime MakeStatuses(StatusCatalog catalog) => new(catalog, (_, _) => ActorDerivedSnapshot.Empty);

    [Fact]
    public void NoActorIsMidStanceByDefault()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        Assert.False(stance.IsHeld("wave:0"));
        Assert.Null(stance.Check("wave:0", "act.attack"));
    }

    [Fact]
    public void ARaisedStanceRefusesEveryOtherActionIncludingMovement()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);

        stance.Raise(statuses, "wave:0", "act.guard-release", new[] { new StatusStatMod("combat.defense.omni", "flat", 25) }, DateTimeOffset.UnixEpoch);

        var attackResult = stance.Check("wave:0", "act.attack");
        var moveResult = stance.Check("wave:0", "act.move");

        Assert.NotNull(attackResult);
        Assert.Equal(UsabilityReason.StanceHeld, attackResult!.Value.Reason);
        Assert.NotNull(moveResult);
        Assert.Equal(UsabilityReason.StanceHeld, moveResult!.Value.Reason);
    }

    [Fact]
    public void TheReleaseActionItselfIsAlwaysAllowedThrough()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);

        stance.Raise(statuses, "wave:0", "act.guard-release", Array.Empty<StatusStatMod>(), DateTimeOffset.UnixEpoch);

        Assert.Null(stance.Check("wave:0", "act.guard-release")); // null == not refused
    }

    [Fact]
    public void GuardWhileMovingIsADifferentActionIdNeverABypassOnTheBasicMove()
    {
        // spec S1: "guard-while-moving is a different skill, not a basic action" -- gate 0 has no
        // exemption list, so the ONLY way a move-shaped action passes while held is by BEING the
        // declared release id. A skill literally named for moving-while-guarding still refuses
        // unless it IS the release.
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);

        stance.Raise(statuses, "wave:0", "act.guard-release", Array.Empty<StatusStatMod>(), DateTimeOffset.UnixEpoch);

        Assert.NotNull(stance.Check("wave:0", "skill.guarded-advance")); // NOT the release -- still refused
    }

    [Fact]
    public void ReleasingClearsTheRefusalAndTheHeldSelfStatus()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);
        var mods = new[] { new StatusStatMod("combat.defense.omni", "flat", 25) };

        stance.Raise(statuses, "wave:0", "act.guard-release", mods, DateTimeOffset.UnixEpoch);
        Assert.Single(statuses.ForHost("wave:0"));

        stance.Release(statuses, "wave:0");

        Assert.False(stance.IsHeld("wave:0"));
        Assert.Null(stance.Check("wave:0", "act.attack"));
        Assert.Empty(statuses.ForHost("wave:0"));
    }

    [Fact]
    public void ReleasingAnActorWhoNeverRaisedIsANoOp()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);

        stance.Release(statuses, "wave:0"); // never raised -- must not throw

        Assert.False(stance.IsHeld("wave:0"));
    }

    [Fact]
    public void DifferentActorsHoldIndependently()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);

        stance.Raise(statuses, "wave:0", "act.guard-release", Array.Empty<StatusStatMod>(), DateTimeOffset.UnixEpoch);

        Assert.True(stance.IsHeld("wave:0"));
        Assert.False(stance.IsHeld("wave:1"));
        Assert.Null(stance.Check("wave:1", "act.attack")); // untouched actor, never refused
    }

    [Fact]
    public void TheHeldStatusCarriesTheAuthoredStatModsVerbatim()
    {
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);
        var mods = new StatusStatMod[]
        {
            new("combat.defense.omni", "flat", 25),
            new("combat.dodge.omni", "increased", -0.1),
        };

        stance.Raise(statuses, "wave:0", "act.guard-release", mods, DateTimeOffset.UnixEpoch);

        var instance = Assert.Single(statuses.ForHost("wave:0"));
        Assert.Equal(mods, instance.StatMods);
    }

    [Fact]
    public void EndToEndThroughTheRealUsabilityEvaluatorGate0()
    {
        // Proves the seam is FILLED correctly, not just that StanceRuntime.Check alone behaves --
        // runs the actual UsabilityEvaluator.Evaluate call chain with a real StanceRuntime.
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = MakeStatuses(catalog);
        stance.Raise(statuses, "wave:0", "act.guard-release", Array.Empty<StatusStatMod>(), DateTimeOffset.UnixEpoch);

        var attackAction = new ActionRow { ActionId = "act.attack", Kind = ActionKind.Basic };
        var facts = new FactReader();

        var result = UsabilityEvaluator.Evaluate(
            "wave:0", attackAction, actorHoldsAction: true, nowTick: 0,
            new FusionRpg.Core.Battle.Timeline.CooldownLedger(), stance, AlwaysAffordable.Instance,
            casterPos: null, targetPos: null, condition: new AlwaysTrue(),
            facts: ref facts);

        Assert.False(result.IsUsable);
        Assert.Equal(UsabilityReason.StanceHeld, result.Reason);
    }

    sealed class AlwaysTrue : ICompiledPredicate
    {
        public bool Evaluate(ref FactReader facts) => true;
    }
}
