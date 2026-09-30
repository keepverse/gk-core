using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>Zombie HP width lives in Bridges/{profile}/ — shared injector must not assign theHealth/theMaxHealth.</summary>
public class ZombieHpBridgeGuardTests
{
    [Fact]
    public void Injector_outside_Bridges_does_not_assign_zombie_health_fields()
    {
        var repoRoot = FindRepoRoot();
        var injector = Path.Combine(repoRoot, "src", "FusionRpg.Injector");
        Assert.True(Directory.Exists(injector), "missing " + injector);

        var leaks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(injector, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(injector, file).Replace('\\', '/');
            if (rel.StartsWith("Bridges/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains(".theHealth =", StringComparison.Ordinal) ||
                    line.Contains(".theMaxHealth =", StringComparison.Ordinal))
                    leaks.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(leaks.Count == 0,
            "zombie HP writes must stay in Bridges/{profile}/:\n" + string.Join("\n", leaks));
    }

    [Fact]
    public void Injector_outside_Bridges_does_not_call_CreateZombie_SetZombie()
    {
        var repoRoot = FindRepoRoot();
        var injector = Path.Combine(repoRoot, "src", "FusionRpg.Injector");
        var leaks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(injector, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(injector, file).Replace('\\', '/');
            if (rel.StartsWith("Bridges/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains(".SetZombie(", StringComparison.Ordinal) &&
                    !line.Contains("SetZombieWithMindControl", StringComparison.Ordinal))
                    leaks.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(leaks.Count == 0,
            "CreateZombie.SetZombie calls must stay in Bridges/{profile}/CreateZombieSpawn:\n" +
            string.Join("\n", leaks));
    }

    [Fact]
    public void GameCaptureHooks_does_not_harmony_patch_SetZombie()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.Injector", "GameCaptureHooks.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(
            "[HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombie))]",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombieWithMindControl))]",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_3_8_1_spawn_has_melon_5_arg_SetZombie()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.Injector", "Bridges", "pvzrh-3.8.1", "CreateZombieSpawn.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.Contains("#if FUSIONRPG_MELON", text, StringComparison.Ordinal);
        Assert.Contains("SetZombie(row, type, x, false, mindControl)", text, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
