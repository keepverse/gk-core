using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Rungs;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T22 (action-todo.md, spec-loadout.md §3): auto-equip. Uses the SHIPPED
/// `RungPolicy.Table` (the same 10-row `action-rungs.v1.json` ladder T1–T5 already loaded for the
/// whole test assembly via `ContractTuningTestBootstrap`) — real rung multipliers, not a hand-rolled
/// stand-in, so a monotonicity regression in the actual ladder would show up here too.
/// </summary>
public class AutoEquipTests
{
    static RungTable Rungs => RungPolicy.Table;

    [Fact]
    public void HigherRungActionsAreRankedFirst()
    {
        var candidates = new[]
        {
            new AutoEquipCandidate("skill.weak", Rung: 1),
            new AutoEquipCandidate("skill.strong", Rung: 5),
            new AutoEquipCandidate("skill.mid", Rung: 3),
        };

        var selected = AutoEquip.Select(candidates, Rungs);

        Assert.Equal(new[] { "skill.strong", "skill.mid", "skill.weak" }, selected);
    }

    [Fact]
    public void TakesAtMostFive()
    {
        var candidates = new[]
        {
            new AutoEquipCandidate("skill.a", 1), new AutoEquipCandidate("skill.b", 1),
            new AutoEquipCandidate("skill.c", 1), new AutoEquipCandidate("skill.d", 1),
            new AutoEquipCandidate("skill.e", 1), new AutoEquipCandidate("skill.f", 1),
            new AutoEquipCandidate("skill.g", 1),
        };

        var selected = AutoEquip.Select(candidates, Rungs);

        Assert.Equal(5, selected.Count);
    }

    [Fact]
    public void FewerThanFiveHeldReturnsAllOfThem()
    {
        var candidates = new[] { new AutoEquipCandidate("skill.a", 2), new AutoEquipCandidate("skill.b", 4) };
        var selected = AutoEquip.Select(candidates, Rungs);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void NoCandidatesReturnsAnEmptySet()
    {
        var selected = AutoEquip.Select(Array.Empty<AutoEquipCandidate>(), Rungs);
        Assert.Empty(selected);
    }

    [Fact]
    public void EqualPowerTieBreaksOnActionIdOrdinal()
    {
        // Two deliberately equal-power actions (same rung) — the ONLY thing that can separate them
        // is the action_id ordinal comparison, named directly in the spec's testing strategy.
        var candidates = new[]
        {
            new AutoEquipCandidate("skill.zebra", Rung: 3),
            new AutoEquipCandidate("skill.alpha", Rung: 3),
        };

        var selected = AutoEquip.Select(candidates, Rungs);

        Assert.Equal(new[] { "skill.alpha", "skill.zebra" }, selected);
    }

    [Fact]
    public void DeterministicAcrossTwoRunsAndAcrossAShuffledInputOrder()
    {
        var inOrder = new[]
        {
            new AutoEquipCandidate("skill.a", 5), new AutoEquipCandidate("skill.b", 2),
            new AutoEquipCandidate("skill.c", 8), new AutoEquipCandidate("skill.d", 2),
            new AutoEquipCandidate("skill.e", 1),
        };
        var shuffled = new[] { inOrder[3], inOrder[0], inOrder[4], inOrder[2], inOrder[1] };

        var first = AutoEquip.Select(inOrder, Rungs);
        var second = AutoEquip.Select(inOrder, Rungs); // same order, run twice
        var thirdShuffled = AutoEquip.Select(shuffled, Rungs);

        Assert.Equal(first, second);
        Assert.Equal(first, thirdShuffled);
    }

    [Fact]
    public void TheReturnTypeCarriesNoPowerValueAnywhereTheArchitectureGuarantee()
    {
        // PS-4 / spec §3: "the power score reaches nothing but the ranking." Made structural, not
        // just asserted in prose: Select's return type is IReadOnlyList<string> -- there is no
        // numeric field anywhere in the signature for a caller to read a score back out of, so a
        // future change that tried to leak it would have to change the API shape to do so, which is
        // exactly the kind of change a reviewer would notice.
        var returnType = typeof(AutoEquip).GetMethod(nameof(AutoEquip.Select))!.ReturnType;
        Assert.Equal(typeof(IReadOnlyList<string>), returnType);
    }

    [Fact]
    public void AnUnknownRungThrowsRatherThanSilentlyRankingAsZero()
    {
        var candidates = new[] { new AutoEquipCandidate("skill.a", Rung: 99999) };
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoEquip.Select(candidates, Rungs));
    }
}
