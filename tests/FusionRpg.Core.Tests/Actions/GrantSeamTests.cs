using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T23 (action-todo.md, spec-grant-seam.md §2, item 4): action-set assembly — the entry point the
/// item lane was told not to implement for itself. Cap enforcement and the run-start freeze are
/// T24's; these tests exercise assembly alone: dedupe with provenance, default-attack resolution,
/// the redundant-grant report, and ordinal order under a shuffled input.
/// </summary>
public class GrantSeamTests
{
    static readonly SpeciesBasicsRow Basics = new(
        SpeciesKey: "zombie-basic",
        AttackActionId: "act.attack",
        GuardActionId: "act.guard",
        MoveActionId: "act.move",
        InnateActionId: "act.innate.rot-burst");

    static readonly SpeciesBasicsRow BasicsNoInnate = Basics with { InnateActionId = null };

    static Func<string, bool> NeverEligible() => _ => false;
    static Func<string, bool> AlwaysEligible() => _ => true;

    static ActionGrantRow Grant(string actionId, string source, string role = "") =>
        new(OwnerKind.Entity, "abc123", actionId, source, role);

    [Fact]
    public void AnActorWithNoItemsHasExactlyTheThreeBasicsAndItsInnate()
    {
        var result = ActionSetAssembler.Assemble(Basics, Array.Empty<ActionGrantRow>(), NeverEligible());

        var ids = result.Actions.Select(a => a.ActionId).ToArray();
        Assert.Equal(4, ids.Length);
        Assert.Contains("act.attack", ids);
        Assert.Contains("act.guard", ids);
        Assert.Contains("act.move", ids);
        Assert.Contains("act.innate.rot-burst", ids);
    }

    [Fact]
    public void AnUnarmedActorWithNoInnateHasExactlyTheThreeBasics()
    {
        var result = ActionSetAssembler.Assemble(BasicsNoInnate, Array.Empty<ActionGrantRow>(), NeverEligible());
        Assert.Equal(3, result.Actions.Count);
    }

    [Fact]
    public void TwoItemsGrantingTheSameActionProduceOneEntryAndTwoRows()
    {
        var grants = new[] { Grant("skill.fireball", "item.sword"), Grant("skill.fireball", "item.ring") };

        var result = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());

        var entry = result.Actions.Single(a => a.ActionId == "skill.fireball");
        Assert.Equal(2, entry.Sources.Count);
        Assert.Contains("item.sword", entry.Sources);
        Assert.Contains("item.ring", entry.Sources);
        Assert.Empty(result.RedundantGrants); // NOT a redundant-intrinsic report -- two paid sources overlapping
    }

    [Fact]
    public void RemovingOneOfTwoSourcesLeavesTheActionStillAssembled()
    {
        var bothGrants = new[] { Grant("skill.fireball", "item.sword"), Grant("skill.fireball", "item.ring") };
        var oneRemoved = new[] { Grant("skill.fireball", "item.ring") }; // "item.sword" withdrawn

        var before = ActionSetAssembler.Assemble(Basics, bothGrants, NeverEligible());
        var after = ActionSetAssembler.Assemble(Basics, oneRemoved, NeverEligible());

        Assert.Contains(before.Actions, a => a.ActionId == "skill.fireball");
        Assert.Contains(after.Actions, a => a.ActionId == "skill.fireball"); // still present
        Assert.Single(after.Actions.Single(a => a.ActionId == "skill.fireball").Sources);
    }

    [Fact]
    public void AnItemGrantingWhatTheSpeciesAlreadyHasIsOneEntryAndAReport()
    {
        // "act.attack" is already intrinsic -- granting it again is not a rejection, not a duplicate
        // slot, but it IS reported: the player must be able to tell that grant did nothing here.
        var grants = new[] { Grant("act.attack", "item.redundant-charm") };

        var result = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());

        var entry = result.Actions.Single(a => a.ActionId == "act.attack");
        Assert.Equal(2, entry.Sources.Count); // "intrinsic" + the item, both kept
        var report = Assert.Single(result.RedundantGrants);
        Assert.Equal("act.attack", report.ActionId);
        Assert.Equal("item.redundant-charm", report.Source);
    }

    [Fact]
    public void DefaultAttackOverrideReplacesTheSpeciesAttackWhenEligible()
    {
        var grants = new[] { Grant("skill.great-sword-swing", "item.great-sword", ActionGrantRoles.DefaultAttack) };

        var result = ActionSetAssembler.Assemble(Basics, grants, AlwaysEligible());

        Assert.Equal("skill.great-sword-swing", result.DefaultAttackActionId);
    }

    [Fact]
    public void AnUnarmedActorKeepsTheSpeciesAttackAsDefault()
    {
        var result = ActionSetAssembler.Assemble(Basics, Array.Empty<ActionGrantRow>(), NeverEligible());
        Assert.Equal("act.attack", result.DefaultAttackActionId);
    }

    [Fact]
    public void ADefaultAttackRoleOnAnIneligibleActionThrows()
    {
        var grants = new[] { Grant("skill.not-eligible", "item.x", ActionGrantRoles.DefaultAttack) };
        Assert.Throws<ArgumentException>(() => ActionSetAssembler.Assemble(Basics, grants, NeverEligible()));
    }

    [Fact]
    public void AssemblyOrderIsActionIdOrdinalEvenUnderAShuffledGrantList()
    {
        var inOrder = new[] { Grant("skill.c", "item.1"), Grant("skill.a", "item.2"), Grant("skill.b", "item.3") };
        var shuffled = new[] { inOrder[1], inOrder[2], inOrder[0] };

        var a = ActionSetAssembler.Assemble(Basics, inOrder, NeverEligible());
        var b = ActionSetAssembler.Assemble(Basics, shuffled, NeverEligible());

        var idsA = a.Actions.Select(x => x.ActionId).ToArray();
        var idsB = b.Actions.Select(x => x.ActionId).ToArray();

        Assert.Equal(idsA, idsB);
        // Explicitly ordinal, not just "some deterministic order": act.* sorts before skill.*.
        var expected = idsA.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, idsA);
    }

    [Fact]
    public void AssemblingTwiceWithTheSameInputsReturnsTheIdenticalSet()
    {
        var grants = new[] { Grant("skill.a", "item.1") };
        var first = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());
        var second = ActionSetAssembler.Assemble(Basics, grants, NeverEligible());

        Assert.Equal(
            first.Actions.Select(a => a.ActionId),
            second.Actions.Select(a => a.ActionId));
    }
}
