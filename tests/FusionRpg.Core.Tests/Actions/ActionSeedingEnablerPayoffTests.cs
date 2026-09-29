using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Seeding;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T32 (action-todo.md, spec-action-seeding.md §5): "every conditional payoff in a generated pool has
/// an enabler in the same pool", asserted in Core against a planted unpaired pool — not deferred to
/// seedsmith, which does not exist as a gate for this feature. Named <c>ActionSeeding*</c> so T31's
/// and T32's shared <c>--filter ~ActionSeeding</c> verify line finds both files.
/// </summary>
public class ActionSeedingEnablerPayoffTests
{
    static EnablerPayoffPairings Pairings() => EnablerPayoffPairings.Parse("""
        {
          "atom.chill-punisher": ["atom.chill-applier"],
          "atom.rot-punisher": ["atom.rot-applier", "atom.blight-applier"]
        }
        """);

    [Fact]
    public void APoolCarryingBothThePayoffAndItsEnablerPasses()
    {
        var result = EnablerPayoffCoverage.Check(new[] { "atom.chill-punisher", "atom.chill-applier" }, Pairings());
        Assert.True(result.IsOk);
        Assert.Null(result.UnpairedPayoffFamily);
    }

    [Fact]
    public void APlantedUnpairedPoolFailsNamingThePayoff()
    {
        var result = EnablerPayoffCoverage.Check(new[] { "atom.chill-punisher", "atom.strike" }, Pairings());
        Assert.False(result.IsOk);
        Assert.Equal("atom.chill-punisher", result.UnpairedPayoffFamily);
    }

    [Fact]
    public void AnyOneOfSeveralAuthoredEnablersSatisfiesThePayoff()
    {
        // atom.rot-punisher authors TWO possible enablers -- either one alone must suffice.
        var withBlightOnly = EnablerPayoffCoverage.Check(new[] { "atom.rot-punisher", "atom.blight-applier" }, Pairings());
        Assert.True(withBlightOnly.IsOk);
    }

    [Fact]
    public void APoolWithNoPayoffsAtAllTriviallyPasses()
    {
        var result = EnablerPayoffCoverage.Check(new[] { "atom.strike", "atom.fireball" }, Pairings());
        Assert.True(result.IsOk);
    }

    [Fact]
    public void AFamilyNotAuthoredAsAPayoffIsUntrackedNeverFlagged()
    {
        Assert.False(Pairings().IsPayoff("atom.strike"));
        var result = EnablerPayoffCoverage.Check(new[] { "atom.strike" }, Pairings());
        Assert.True(result.IsOk);
    }

    [Fact]
    public void TwoPayoffsInOnePoolEachCheckedIndependentlyTheUnpairedOneIsNamed()
    {
        // chill-punisher's own enabler IS present; rot-punisher's is not -- only rot-punisher fails,
        // proving each payoff's coverage is checked on its own rather than one pass/fail for the pool.
        var result = EnablerPayoffCoverage.Check(
            new[] { "atom.chill-punisher", "atom.chill-applier", "atom.rot-punisher" }, Pairings());
        Assert.False(result.IsOk);
        Assert.Equal("atom.rot-punisher", result.UnpairedPayoffFamily);
    }

    [Fact]
    public void AZeroEnablerPayoffIsRejectedAtParseTimeNeverAllowedToShipUnpairable() =>
        Assert.Throws<EnablerPayoffPairingRejection>(() =>
            EnablerPayoffPairings.Parse("""{ "atom.impossible-punisher": [] }"""));

    [Fact]
    public void TheShippedPairingsFileLoadsAndEveryPayoffHasAtLeastOneEnabler()
    {
        var pairings = EnablerPayoffPairings.Parse(File.ReadAllText(ShippedPairingsPath()));
        Assert.True(pairings.IsPayoff("atom.chill-punisher"));
        Assert.NotEmpty(pairings.EnablersOf("atom.chill-punisher"));
    }

    static string ShippedPairingsPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "data", "seed", "actions", "pairings.json");
    }
}
