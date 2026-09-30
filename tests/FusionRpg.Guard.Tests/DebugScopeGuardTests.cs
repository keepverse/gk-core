using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Runs gk-core/scripts/guard-debug-scope.py (the debug-scope guard, ported from the .ps1) against small
/// fixture files and proves the corrected
/// classification rule (spec-debug-scope-guard.md): any relay call (<c>Send(hub, inbox, ...)</c>)
/// anywhere in a handler body makes the route Game-Injector-Debug-shaped, full stop -- a real
/// persisted call alongside it is legitimate orchestration, never a violation. Separately proves the
/// guard passes green against the REAL, current DebugEndpoints.cs with zero exemptions.
/// </summary>
public class DebugScopeGuardTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static (int exit, string stdout, string stderr) RunGuard(string root, string fixturePath)
    {
        var script = Path.Combine(root, "scripts", "guard-debug-scope.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --file-path \"{fixturePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "debug-scope guard timed out");
    }

    /// <summary>Writes <paramref name="body"/> as a minimal fixture .cs file (just enough surface
    /// for the guard's regex scan — no real DebugEndpoints.cs structure required), runs the guard
    /// against it, and returns the result. Cleans the temp file up afterwards.</summary>
    /// <remarks>The classification report is on STDOUT and the findings on STDERR: the report is what
    /// every assertion below reads, and the Python port moved the findings off the stream PowerShell's
    /// <c>Write-Host</c> put both on. A 60 s budget covers a cold Python start on a large fixture.</remarks>
    static (int exit, string stdout, string stderr) RunGuardOnFixture(string body)
    {
        var root = RepoRoot();
        var fixture = Path.Combine(Path.GetTempPath(), $"DebugScopeFixture_{Guid.NewGuid():N}.cs");
        File.WriteAllText(fixture, body);
        try
        {
            return RunGuard(root, fixture);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    const string RelayHelperDefs = """
        // Minimal stand-ins for the two self-verified helpers guard-debug-scope.py checks against
        // their own definitions — both must genuinely relay, or the guard would (correctly) throw.
        static void MapPost(RouteGroupBuilder g, string path, string cmdName)
        {
            g.MapPost(path, async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
            {
                await Send(hub, inbox, cmdName, body);
                return Results.Ok(new { ok = true });
            });
        }

        public static async Task<IResult> AcceptDebugSpawnExtra(
            JsonElement body,
            RpgStore store,
            IHubContext<RpgHub> hub,
            InjectorCommandInbox inbox,
            string reasonDefault)
        {
            await Send(hub, inbox, "pvz.spawn.extra", body);
            return Results.Ok(new { ok = true });
        }
        """;

    [Fact]
    public void Relay_only_body_is_Game_Injector_Debug()
    {
        var body = $$"""
            public static class Fixture
            {
                public static void Map(RouteGroupBuilder g)
                {
                    g.MapPost("/relay-only", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
                    {
                        await Send(hub, inbox, "debug.thing", body);
                        return Results.Ok(new { ok = true });
                    });
                }

                {{RelayHelperDefs}}
            }
            """;

        var (exit, stdout, stderr) = RunGuardOnFixture(body);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[GameInjectorDebug] POST  /relay-only", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_method_only_body_no_relay_is_Rpg_Server_Debug()
    {
        var body = $$"""
            public static class Fixture
            {
                public static void Map(RouteGroupBuilder g)
                {
                    g.MapPost("/persisted-only", (JsonElement? body, RpgStore store) =>
                    {
                        store.MergeCheatField("X", true, null);
                        return Results.Ok(new { ok = true });
                    });
                }

                {{RelayHelperDefs}}
            }
            """;

        var (exit, stdout, stderr) = RunGuardOnFixture(body);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[RpgServerDebug   ] POST  /persisted-only", stdout, StringComparison.Ordinal);
    }

    /// <summary>The actual regression case for the corrected rule: a body with BOTH a real
    /// persisted-write call AND a relay call must still be Game-Injector-Debug-shaped, never flagged
    /// as a violation and never classified RPG Server Debug. The original (wrong) rule would have
    /// flagged this as a conflict; the corrected rule says "any relay wins, full stop".</summary>
    [Fact]
    public void Body_with_both_real_method_and_relay_is_still_Game_Injector_Debug()
    {
        var body = $$"""
            public static class Fixture
            {
                public static void Map(RouteGroupBuilder g)
                {
                    g.MapPost("/mixed-shape", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
                    {
                        store.MergeCheatField("DEBUG-LEVEL-ENTRY", true, null);
                        await Send(hub, inbox, "debug.enter-level", body);
                        return Results.Ok(new { ok = true });
                    });
                }

                {{RelayHelperDefs}}
            }
            """;

        var (exit, stdout, stderr) = RunGuardOnFixture(body);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[GameInjectorDebug] POST  /mixed-shape", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[RpgServerDebug   ] POST  /mixed-shape", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Bare_MapPost_helper_call_is_Game_Injector_Debug_without_a_body_scan()
    {
        var body = $$"""
            public static class Fixture
            {
                public static void Map(RouteGroupBuilder g)
                {
                    MapPost(g, "/bare-helper", "debug.bare-helper");
                }

                {{RelayHelperDefs}}
            }
            """;

        var (exit, stdout, stderr) = RunGuardOnFixture(body);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[GameInjectorDebug] POST  /bare-helper  -- shared MapPost(g, path, cmd) helper", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_with_a_non_debug_dot_star_command_name_is_still_recognized_as_a_relay()
    {
        var body = $$"""
            public static class Fixture
            {
                public static void Map(RouteGroupBuilder g)
                {
                    g.MapPost("/cheat-relay", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
                    {
                        await Send(hub, inbox, "cheat.toggle", body);
                        return Results.Ok(new { ok = true });
                    });
                }

                {{RelayHelperDefs}}
            }
            """;

        var (exit, stdout, stderr) = RunGuardOnFixture(body);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[GameInjectorDebug] POST  /cheat-relay", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_passes_green_on_the_real_current_DebugEndpoints_with_zero_exemptions()
    {
        var root = RepoRoot();
        var real = Path.Combine(root, "src", "FusionRpg.Server", "DebugEndpoints.cs");
        var (exit, stdout, stderr) = RunGuard(root, real);
        Assert.True(exit == 0, $"exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("DEBUG SCOPE GUARD OK", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /lawn/quick-start", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /scenario/{id}", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /effect/grant", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /effect/withdraw", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /effect/clear", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /effects/reload", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /spawn-extra", stdout, StringComparison.Ordinal);
        Assert.Contains("[GameInjectorDebug] POST  /fire-spawn-extra", stdout, StringComparison.Ordinal);
        // `species-progression` SP0.6 removed `/reforge-world` outright (its whole body was the eager
        // species reforge, illegitimate under C2's ruling) -- it is no longer a route this guard can
        // classify at all, so this fact is retired rather than repointed.
        Assert.DoesNotContain("/reforge-world", stdout, StringComparison.Ordinal);
        Assert.Contains("[RpgServerDebug   ] POST  /derived-audit-actor", stdout, StringComparison.Ordinal);
    }
}
