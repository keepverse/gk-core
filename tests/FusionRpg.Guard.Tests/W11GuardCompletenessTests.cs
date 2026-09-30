using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>W11-B/C source + docs locks — LimHealth Bend / GATE default, alt-sink inventory.</summary>
public class W11GuardCompletenessTests
{
    [Fact]
    public void CheatRegistry_lists_LimHealth_gate_via_T_id_default_false()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.CheatCore", "CheatRegistry.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.Contains("void T(string id, bool v = false)", text, StringComparison.Ordinal);
        Assert.Contains("\"SYS-LIMHEALTH-GATE\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("T(\"SYS-LIMHEALTH-GATE\", true)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("T(\"SYS-LIMHEALTH-GATE\",true)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Foundation_and_stat_system_document_W11_B_Bend()
    {
        var repoRoot = FindRepoRoot();
        var foundation = File.ReadAllText(Path.Combine(repoRoot, "docs", "testing", "foundation.md"));
        var stats = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture", "stat-system.md"));
        Assert.Contains("W11-B", foundation, StringComparison.Ordinal);
        Assert.Contains("Bend", foundation, StringComparison.Ordinal);
        Assert.Contains("W11-B", stats, StringComparison.Ordinal);
        Assert.Contains("Bend", stats, StringComparison.Ordinal);
    }

    [Fact]
    public void Effect_runtime_inventory_names_alt_sinks_and_HitLand_W12()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "docs", "architecture", "effect-runtime.md");
        var text = File.ReadAllText(path);
        Assert.Contains("RealTakeDamage", text, StringComparison.Ordinal);
        Assert.Contains("BodyTakeDamage", text, StringComparison.Ordinal);
        Assert.Contains("ApplyDamage", text, StringComparison.Ordinal);
        Assert.Contains("HitLand", text, StringComparison.Ordinal);
        Assert.Contains("W12", text, StringComparison.Ordinal);
        Assert.Contains("combat.hitland", text, StringComparison.Ordinal);
        Assert.DoesNotContain("| `Bullet.HitLand` | none", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GameCaptureHooks_still_emits_real_body_apply_paths()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "FusionRpg.Injector", "GameCaptureHooks.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);
        Assert.Contains("[\"path\"] = \"real\"", text, StringComparison.Ordinal);
        Assert.Contains("[\"path\"] = \"body\"", text, StringComparison.Ordinal);
        Assert.Contains("[\"path\"] = \"apply\"", text, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
