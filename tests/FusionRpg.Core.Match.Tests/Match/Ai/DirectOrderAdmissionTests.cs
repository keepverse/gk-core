using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §2/§7) — the
/// retryable/terminal projection of <see cref="UsabilityReason"/> and the one admission rule that needs
/// no durable identity. Spec test rows 6 and 9.
/// </summary>
public class DirectOrderAdmissionTests
{
    static CompiledAction Action(string id) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "container." + id,
        FusionRpg.Core.Battle.Timeline.ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    // ---- row 9: the classifier is total over the closed enum -------------------------------

    [Fact]
    public void Every_refusal_reason_is_classified_and_Usable_is_not_a_refusal()
    {
        var retryable = 0;
        var terminal = 0;
        var refusals = 0;
        foreach (var reason in Enum.GetValues<UsabilityReason>())
        {
            var result = new UsabilityResult(reason);
            if (reason == UsabilityReason.Usable)
            {
                // "Usable" is the absence of a refusal; answering it would invent a third class.
                Assert.Throws<ArgumentException>(() => DirectOrderAdmission.Classify(result));
                continue;
            }

            if (DirectOrderAdmission.Classify(result) == DirectOrderRefusalClass.Retryable) retryable++;
            else terminal++;
            refusals++;
        }

        // Every member but `Usable` is a refusal, so a new member cannot escape this walk.
        Assert.Equal(Enum.GetValues<UsabilityReason>().Length - 1, refusals);
        // And the split is the spec's own two lists, pinned because the code owns this closed enum: 8
        // retryable and 3 terminal. A silent `_ => Retryable` arm would make these 11 and 0.
        Assert.Equal(8, retryable);
        Assert.Equal(3, terminal);
    }

    [Fact]
    public void The_retryable_and_terminal_members_are_the_specs_two_lists()
    {
        // Time cannot change these answers, which is exactly why they are terminal.
        Assert.Equal(DirectOrderRefusalClass.Terminal, DirectOrderAdmission.Classify(new UsabilityResult(UsabilityReason.NotBound)));
        Assert.Equal(DirectOrderRefusalClass.Terminal, DirectOrderAdmission.Classify(new UsabilityResult(UsabilityReason.NotEquipped)));
        Assert.Equal(DirectOrderRefusalClass.Terminal, DirectOrderAdmission.Classify(new UsabilityResult(UsabilityReason.AlreadyActive)));

        // These become true with time (or with a target), so the order stays live and is reconsidered.
        foreach (var reason in new[]
                 {
                     UsabilityReason.OnCooldown, UsabilityReason.CannotAfford, UsabilityReason.MissingStock,
                     UsabilityReason.OutOfRange, UsabilityReason.TooClose, UsabilityReason.NoValidTarget,
                     UsabilityReason.ConditionFailed, UsabilityReason.StanceHeld,
                 })
        {
            Assert.Equal(DirectOrderRefusalClass.Retryable, DirectOrderAdmission.Classify(new UsabilityResult(reason)));
        }
    }

    // ---- rows 3, 4, 5: the durable-identity refusals ------------------------------------

    [Fact]
    public void An_order_issued_in_a_different_run_is_refused_StaleRun()
    {
        // §2 step 1. The planted violation this kills is removing the comparison: without it an order
        // issued in the PREVIOUS match would command a creature in the next one, because the ptr it
        // names is entirely likely to be live again.
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckScope("match.7", "match.7"));
        Assert.Equal(DirectOrderRefusal.StaleRun, DirectOrderAdmission.CheckScope("match.6", "match.7"));
        // Ordinal, never case-insensitive: a scope id is an opaque key, not player-facing text.
        Assert.Equal(DirectOrderRefusal.StaleRun, DirectOrderAdmission.CheckScope("Match.7", "match.7"));
    }

    [Fact]
    public void A_subject_whose_binding_names_a_different_ptr_is_refused_SubjectMoved()
    {
        // §2 step 3 — the whole ptr-reuse answer. The durable id resolves, but to another address, so
        // the order is about a different creature than the one it would command.
        var moved = new OrderSubject(Ptr: "ptr.reused", IsBound: true, IsLive: true);

        Assert.Equal(DirectOrderRefusal.SubjectMoved, DirectOrderAdmission.CheckSubject(moved, "ptr.original"));

        // The same order against the address the binding actually names is admitted — the rule is a
        // comparison, not a refusal of everything.
        var same = new OrderSubject(Ptr: "ptr.original", IsBound: true, IsLive: true);
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckSubject(same, "ptr.original"));
    }

    [Fact]
    public void A_subject_with_no_bound_row_is_refused_SubjectGone_and_admission_invents_nothing()
    {
        // §2 step 2: admission cannot invent a subject. `default` is the un-resolved case, and it must
        // refuse rather than fall back to the ptr the caller supplied.
        Assert.Equal(DirectOrderRefusal.SubjectGone, DirectOrderAdmission.CheckSubject(default, "ptr.any"));

        // A binding that exists but is PendingSpawn or Cleared chooses a different subject.
        var notBound = new OrderSubject(Ptr: "ptr.any", IsBound: false, IsLive: true);
        Assert.Equal(DirectOrderRefusal.SubjectGone, DirectOrderAdmission.CheckSubject(notBound, "ptr.any"));

        // A bound row with no ptr has nothing to admit against either.
        var boundNoPtr = new OrderSubject(Ptr: null, IsBound: true, IsLive: true);
        Assert.Equal(DirectOrderRefusal.SubjectGone, DirectOrderAdmission.CheckSubject(boundNoPtr, "ptr.any"));
    }

    [Fact]
    public void A_subject_that_is_bound_and_matching_but_not_live_is_refused_SubjectGone()
    {
        // §2 step 4: the right address, nothing holding it. The order arrives after the withdrawal edge,
        // which is exactly the transport window §2 exists to close.
        var dead = new OrderSubject(Ptr: "ptr.a", IsBound: true, IsLive: false);

        Assert.Equal(DirectOrderRefusal.SubjectGone, DirectOrderAdmission.CheckSubject(dead, "ptr.a"));

        // And the same three facts with it live is admitted, so this is a statement about liveness and
        // not about the test fixture.
        var live = new OrderSubject(Ptr: "ptr.a", IsBound: true, IsLive: true);
        Assert.Equal(DirectOrderRefusal.None, DirectOrderAdmission.CheckSubject(live, "ptr.a"));
    }

    // ---- row 6: NotHeld ----------------------------------------------------------------

    [Fact]
    public void An_action_outside_the_actors_held_set_is_not_held()
    {
        var held = new List<CompiledAction> { Action("act.skill"), Action("act.guard") };

        Assert.True(DirectOrderAdmission.IsHeld("act.skill", held));
        Assert.False(DirectOrderAdmission.IsHeld("act.other", held));
    }

    [Fact]
    public void IsHeld_is_false_for_an_empty_id_an_empty_set_and_a_null_set()
    {
        Assert.False(DirectOrderAdmission.IsHeld("", new List<CompiledAction> { Action("act.skill") }));
        Assert.False(DirectOrderAdmission.IsHeld("act.skill", Array.Empty<CompiledAction>()));
        Assert.False(DirectOrderAdmission.IsHeld("act.skill", null!));
    }

    [Fact]
    public void The_refusal_vocabulary_is_closed_and_its_zero_is_not_a_refusal()
    {
        // A default-constructed result must read as "no refusal", never as a spurious first member.
        Assert.Equal(DirectOrderRefusal.None, default(DirectOrderRefusal));
        Assert.Equal(
            new[] { "None", "StaleRun", "SubjectMoved", "SubjectGone", "NotHeld", "QueueFull" },
            Enum.GetNames<DirectOrderRefusal>());
    }
}
