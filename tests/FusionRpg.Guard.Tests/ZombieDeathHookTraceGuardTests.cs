using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N5: proof 7 needs to see both zombie death hooks and the once-per-death latch deciding, with frame
/// numbers, beside the force-kill an RPG delta triggers. The trace is env-gated (<c>FUSIONRPG_FSM_TRACE=1</c>) and must be
/// written before the latch returns, or a suppressed second hook would leave no line. Source scan: the Injector has no
/// CI-runnable unit tests.
/// </summary>
public class ZombieDeathHookTraceGuardTests
{
    [Fact]
    public void Note_zombie_dead_traces_the_hook_before_the_latch_returns()
    {
        var root = RepoRoot();
        var hooks = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "GameHooks.cs"));
        var method = hooks.IndexOf("static void NoteZombieDead(Zombie z, int reason)", StringComparison.Ordinal);
        var latch = hooks.IndexOf("var firstForPtr = DeadZombies.Add(p);", method, StringComparison.Ordinal);
        var trace = hooks.IndexOf("fsm-trace NoteZombieDead hook=", method, StringComparison.Ordinal);
        var ret = hooks.IndexOf("if (!firstForPtr) return;", method, StringComparison.Ordinal);
        Assert.True(method >= 0 && latch > method && trace > latch && ret > trace, "trace between the latch and its early return");

        // L-N37: the snapshot invalidation runs once per death, after the latch.
        var invalidate = hooks.IndexOf("Effects.InjectorBoardSnapshot.Invalidate();", method, StringComparison.Ordinal);
        Assert.True(invalidate > ret, "NoteZombieDead must invalidate the board snapshot only after the once-per-death latch");

        var writer = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Stats", "EntityStatWriter.cs"));
        Assert.Contains("writer.forceKill zombie ptr={z.Pointer.ToString(\"X\")} src={source} frame={UnityEngine.Time.frameCount}", writer, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
