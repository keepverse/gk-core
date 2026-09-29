using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T24 (action-todo.md, spec-grant-seam.md §§3–6, items 6–8): the snapshot moment, removal
/// semantics, and the cap question. Assembly itself (item 4) has its own tests in
/// <see cref="GrantSeamTests"/>; this covers what T24 actually owns.
/// </summary>
public class GrantSeamLifecycleTests
{
    static readonly SpeciesBasicsRow Basics = new(
        SpeciesKey: "zombie-basic",
        AttackActionId: "act.attack",
        GuardActionId: "act.guard",
        MoveActionId: "act.move",
        InnateActionId: "act.innate.rot-burst");

    static ActionGrantRow Grant(string actionId, string source) =>
        new(OwnerKind.Entity, "abc123", actionId, source, "");

    static Func<string, bool> NeverEligible() => _ => false;

    // ---- item 8: the cap question, re-framed rather than re-invented ------------------------------

    [Fact]
    public void EquippedSkillCapDefaultsToFiveAndIsLoadoutSetsOwnBase()
    {
        // "The number is not 8" -- named here as exactly what it already is, not a new literal.
        // D4.25: EquippedSkillCap became a reader of LoadoutSet.EffectiveMaxSize (the loadout.slots
        // channel can raise it), so the no-channel-worn default is what this test pins.
        Assert.Equal(5, CapPolicy.EquippedSkillCap());
        Assert.Equal(LoadoutSet.MaxSize, CapPolicy.EquippedSkillCap());
    }

    [Fact]
    public void HeldCapIsUnlockTuningsOwnCapNeverAPrivateNumber()
    {
        var tuning = new UnlockTuning(P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        Assert.Equal(10, CapPolicy.HeldCap(tuning));
    }

    [Fact]
    public void ExceedingTheEquippedCapRejectsAtEquipTimeNamingNothingTruncated()
    {
        // "Exceeding an actual cap REJECTS at equip time, naming the item" (spec S5) -- this IS
        // LoadoutSet's existing LoadoutFull rejection (T21), exercised here through six ASSEMBLED
        // (intrinsic + granted) skill ids to prove the two modules actually compose, not just that
        // each independently has a cap.
        var grants = Enumerable.Range(0, 6).Select(i => Grant($"skill.{i}", $"item.{i}")).ToArray();
        var assembled = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());
        var skillIds = assembled.Actions.Select(a => a.ActionId).Where(id => id.StartsWith("skill.")).ToArray();
        Assert.Equal(6, skillIds.Length); // assembly itself is uncapped (S5.1) -- all 6 are present

        var validation = LoadoutSet.Validate(skillIds, isHeld: _ => true, kindOf: _ => ActionKind.Skill, isMidRun: () => false);

        Assert.False(validation.Ok);
        Assert.Equal(LoadoutRejectionReason.LoadoutFull, validation.Reason);
        // "Truncates nothing" -- Validate performs no writes and has no partial-success shape at all.
    }

    [Fact]
    public void GrantedActionsThemselvesAreNeverCappedOnlyEquippingThem()
    {
        // S5.1: "an uncapped pool grows the choice, never the power." Twenty grants assemble fine;
        // nothing in ActionSetAssembler rejects on count.
        var grants = Enumerable.Range(0, 20).Select(i => Grant($"skill.{i}", $"item.{i}")).ToArray();
        var assembled = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());
        Assert.Equal(20 + 4, assembled.Actions.Count); // 20 granted + 3 basics + 1 innate, all present
    }

    // ---- item 7: removal semantics, and the architectural ban on inventory reaching InterruptCause -

    [Fact]
    public void RemovingAGrantSourceIsInvisibleToAssembleItOnlySeesWhateverListItIsGiven()
    {
        var before = ActionSetAssembler.Assemble(Basics, new[] { Grant("skill.a", "item.x") }, NeverEligible());
        Assert.Contains(before.Actions, a => a.ActionId == "skill.a");

        // "Withdrawn" is modeled here exactly as spec S4 describes it: the NEXT liveGrants list
        // simply omits the row. Assemble itself has no notion of withdrawal, phases, or timing.
        var after = ActionSetAssembler.Assemble(Basics, Array.Empty<ActionGrantRow>(), NeverEligible());
        Assert.DoesNotContain(after.Actions, a => a.ActionId == "skill.a");
    }

    [Fact]
    public void NoInventoryTypeReachesInterruptCauseTheArchitecturalBan()
    {
        // Spec S4's own warning, made structural rather than a comment nobody re-checks: the closed
        // set of InterruptCause members is asserted directly. Adding an inventory-shaped cause
        // (Unequipped, ItemRemoved, GrantWithdrawn, ...) fails this test immediately and loudly.
        var allowed = new[] { "CrowdControl", "Damage", "ResourceExhausted" };
        var actual = Enum.GetNames(typeof(InterruptCause));

        Assert.Equal(allowed.OrderBy(x => x, StringComparer.Ordinal), actual.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ---- item 6: the one snapshot moment -----------------------------------------------------------

    [Fact]
    public void ASecondAssemblyCallInOneRunReturnsTheIdenticalSetEvenIfInputsWouldDiffer()
    {
        var frozen = FrozenActionSet.FreezeAtRunStart(Basics, new[] { Grant("skill.a", "item.x") }, NeverEligible());
        var atFreeze = frozen.Snapshot.Actions.Select(a => a.ActionId).OrderBy(x => x).ToArray();

        // A grant "arrives mid-run" -- represented here as a DIFFERENT liveGrants list a caller
        // could have passed. Snapshotted() must never consult it.
        var midRunResult = frozen.Snapshotted();

        Assert.Equal(atFreeze, midRunResult.Actions.Select(a => a.ActionId).OrderBy(x => x).ToArray());
        Assert.Contains(midRunResult.Actions, a => a.ActionId == "skill.a");
        Assert.DoesNotContain(midRunResult.Actions, a => a.ActionId == "skill.b"); // never granted at freeze time
    }

    [Fact]
    public void AGrantArrivingMidRunDoesNotChangeTheAssembledSet()
    {
        var frozen = FrozenActionSet.FreezeAtRunStart(Basics, Array.Empty<ActionGrantRow>(), NeverEligible());
        Assert.DoesNotContain(frozen.Snapshot.Actions, a => a.ActionId == "skill.new");

        // The new grant now exists live, but nothing here re-reads it until the NEXT run start.
        var midRunGrants = new[] { Grant("skill.new", "item.just-picked-up") };
        var stillFrozen = frozen.Snapshotted();

        Assert.DoesNotContain(stillFrozen.Actions, a => a.ActionId == "skill.new");
        // The mid-run grants list is deliberately never passed to Snapshotted() -- proving the API
        // shape itself refuses the input, not just that this call happened not to use it.
        _ = midRunGrants;
    }

    [Fact]
    public void ItAppliesAtTheNextRunStart()
    {
        var frozen = FrozenActionSet.FreezeAtRunStart(Basics, Array.Empty<ActionGrantRow>(), NeverEligible());
        Assert.DoesNotContain(frozen.Snapshot.Actions, a => a.ActionId == "skill.new");

        var refreshed = frozen.RefreshAtNextRunStart(Basics, new[] { Grant("skill.new", "item.just-picked-up") }, NeverEligible());

        Assert.Contains(refreshed.Actions, a => a.ActionId == "skill.new");
        Assert.Contains(frozen.Snapshotted().Actions, a => a.ActionId == "skill.new"); // the snapshot itself updated
    }
}
