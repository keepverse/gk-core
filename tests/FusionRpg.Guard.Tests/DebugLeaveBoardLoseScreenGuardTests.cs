using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N32 (live 2026-09-15): after a real loss, <c>debug.leave-board</c> waited 20 s for the in-battle menu
/// button, which the lose screen does not have, and returned 409. The leave machine must press the lose menu's own
/// back-to-menu button first and then wait for the board to be destroyed. Source scan: the Injector has no CI-runnable
/// unit tests.
/// </summary>
public class DebugLeaveBoardLoseScreenGuardTests
{
    [Fact]
    public void The_open_menu_stage_tries_the_lose_menu_before_the_battle_menu_and_then_waits_for_the_board()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "DebugLeaveBoard.cs"));
        Assert.Contains("const string LoseBackToMenuPath = \"CanvasUp/LoseMenu(Clone)/backtomenu\";", text, StringComparison.Ordinal);

        var stage0 = text.IndexOf("case 0:", StringComparison.Ordinal);
        var lose = text.IndexOf("Press(LoseBackToMenuPath, \"OnMouseDown\", \"OnMouseUp\")", stage0, StringComparison.Ordinal);
        var jump = text.IndexOf("p.Stage = 3;", lose, StringComparison.Ordinal);
        var battle = text.IndexOf("Press(MenuButtonPath, \"OnMouseUpAsButton\")", stage0, StringComparison.Ordinal);

        Assert.True(stage0 >= 0 && lose > stage0, "stage 0 must press the lose menu's back-to-menu button");
        Assert.True(jump > lose && jump < battle, "a lose-menu press must go straight to waiting for the board to be destroyed");
        Assert.True(battle > lose, "the lose menu must be tried before the in-battle menu");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
