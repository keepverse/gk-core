using System.Diagnostics;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

public class ActorHubGuardTests
{
    [Fact]
    public void Guard_script_exits_zero()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-actor-hub.py");
        Assert.True(File.Exists(script), "missing " + script);

        var (exit, stdout, stderr) = RunScript(script, repoRoot);
        Assert.True(exit == 0,
            $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("ACTOR-HUB GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_script_exits_nonzero_when_GameHooks_uses_Stats_Resolve()
    {
        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "guard-actor-hub.py");
        var fixture = Path.Combine(Path.GetTempPath(), "fusionrpg-hub-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteOkSkeleton(fixture);
            // Bypass: damage-scale cache reads StatSystem only.
            File.WriteAllText(
                Path.Combine(fixture, "src", "FusionRpg.Injector", "GameHooks.cs"),
                "namespace X { class GameHooks { void Cache() { var f = Stats.Resolve(ctx); } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0,
                $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("ACTOR-HUB GUARD FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("GameHooks", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void DeployPlay_invokes_actor_hub_guard()
    {
        var repoRoot = FindRepoRoot();
        GuardWiring.AssertGuardReachableInItsOwningPhase(repoRoot, "actor-hub");
    }

    static void WriteOkSkeleton(string root)
    {
        void Write(string rel, string body)
        {
            var full = Path.Combine(root, "src", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        Write("FusionRpg.Server/AuraDerivedEndpoints.cs",
            "class AuraDerivedEndpoints { void M() { UniqueActorHubCompose.Build(); var composeKind = 1; } }\n");
        Write("FusionRpg.Server/UniqueActorHubCompose.cs",
            "class UniqueActorHubCompose { void M() { ActorHubBootstrap.CreateDefault(); ResolveDerivedWithContributions(); EquippedBoundAtoms.X(); } }\n");
        Write("FusionRpg.Server/Program.cs",
            "class Program { void M() { BattleStatComposer.UseEquipment(EquippedBoundAtoms.SourceFromStore(s)); } }\n");
        Write("FusionRpg.Injector/Stats/EntityApply.cs",
            "class EntityApply { void M() { CheatState.ActorHub.Resolve(ctx); } }\n");
        Write("FusionRpg.Injector/GameHooks.cs",
            "class GameHooks { void M() { CheatState.ActorHub.Resolve(ctx); } }\n");
        Write("FusionRpg.Core/SimEngine.cs",
            "class SimEngine { void M() { ActorHub.Resolve(ctx); } }\n");
        Write("FusionRpg.Core/Stats/Derived/Subsystems/StatusDerivedSubsystem.cs",
            "class StatusDerivedSubsystem { void ContributeDerived() { if (string.IsNullOrWhiteSpace(mod.SourceId)) continue; } }\n");
        Write("FusionRpg.Injector/CheatCommandRunner.cs",
            "class CheatCommandRunner { void EmitActorDerived() { InjectorStatusBridge.ResolveDerivedWithContributions(ptr, false); } }\n");
    }

    static (int Exit, string Stdout, string Stderr) RunScript(string script, string root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
