using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N26: on the seed picker the Board exists and MatchHost already reads InMatch, so
/// <c>debug.game-state</c>'s <c>liveState</c> must consult <c>InitBoard.ready</c> before the phase. Source scan: the Injector
/// has no CI-runnable unit tests.
/// </summary>
public class GameStateSeedPickerGuardTests
{
    [Fact]
    public void Live_state_reports_the_seed_picker_before_trusting_the_match_phase()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "DebugActions.cs"));
        var live = text.IndexOf("dump[\"liveState\"] = board == null", StringComparison.Ordinal);
        var picker = text.IndexOf(": initBoardReady == false", live, StringComparison.Ordinal);
        var seed = text.IndexOf("? \"SeedPicker\"", live, StringComparison.Ordinal);
        var phaseSwitch = text.IndexOf(": phase switch", live, StringComparison.Ordinal);

        Assert.True(live >= 0, "liveState assignment not found");
        Assert.True(picker > live && seed > picker, "a not-ready InitBoard must map to SeedPicker");
        Assert.True(phaseSwitch > seed, "the phase switch must come after the seed-picker check");
        Assert.Contains("initBoardReady = init.ready;", text, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
