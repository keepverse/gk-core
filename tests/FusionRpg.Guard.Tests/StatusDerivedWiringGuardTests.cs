using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// mechanism-wiring E2 (spec-mechanism-wiring.md §4.1, §6) — the injector's half of G1 cannot be
/// unit-tested: <c>FusionRpg.Injector</c> targets net6.0 against the game's BepInEx/Il2Cpp interop DLLs,
/// so it needs a real PVZ Fusion install to build, and the injector cannot host a test project. This
/// guard reads <c>CheatState.cs</c> and <c>StatusDerivedMods.cs</c> as TEXT, matching
/// <c>StatusStatApplierGuardTests</c>'/<c>TreeStateGuardTests</c>' own established shape.
///
/// <para><b>What it protects against.</b> Before G1, a status's derived-channel <c>StatMods</c> reached
/// the primary session bag and composed nothing (spec-mechanism-wiring.md §4.1). The fix is a fourth
/// <c>IActorStatSubsystem</c> registration wired through one optional constructor argument on
/// <c>ActorHubBootstrap.CreateDefault</c>. A refactor that silently drops the
/// <c>statusDerivedMods:</c> argument — or points it at a stub instead of the real adapter — would
/// reopen that exact gap, and nothing in Core's own test suite would notice: Core's tests exercise the
/// real <c>ActorHub</c> directly and never touch <c>CheatState.cs</c>.</para>
/// </summary>
public class StatusDerivedWiringGuardTests
{
    [Fact]
    public void CheatState_passes_the_statusDerivedMods_argument()
    {
        var text = ReadInjector("CheatState.cs");
        Assert.Contains("statusDerivedMods:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void statusDerivedMods_points_at_the_real_production_adapter()
    {
        // Not just present -- pointed at the real adapter function, not a stub or a null delegate that
        // would compile, register nothing, and leave the gap open while looking wired.
        var text = ReadInjector("CheatState.cs");
        Assert.Contains(
            "statusDerivedMods: FusionRpg.Injector.Stats.StatusDerivedMods.For",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void CheatState_still_passes_boundDerivedAtoms_alongside_it()
    {
        // statusDerivedMods is additive next to the shipped bound-atom arm -- a refactor that dropped
        // ONE of the two while leaving the other would be a silent half-regression.
        var text = ReadInjector("CheatState.cs");
        Assert.Contains("boundDerivedAtoms:", text, StringComparison.Ordinal);
        Assert.Contains("statusDerivedMods:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_reads_the_live_EffectRuntime_Status_inside_try_catch()
    {
        var text = ReadInjector("Stats", "StatusDerivedMods.cs");
        var tryIdx = text.IndexOf("try", StringComparison.Ordinal);
        Assert.True(tryIdx >= 0, "no try block found in StatusDerivedMods.cs");

        var afterTry = text[tryIdx..];
        Assert.Contains("Effects.EffectRuntime.Status", afterTry, StringComparison.Ordinal);
        Assert.Contains("catch", afterTry, StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_returns_empty_on_failure_never_throws()
    {
        // Mirrors GrantedDerivedAtoms.For exactly: a status runtime that is not up yet (no live match)
        // is a normal state, not an error -- so a live-match crash cannot come from this seam.
        var text = ReadInjector("Stats", "StatusDerivedMods.cs");
        Assert.Contains("catch { return Array.Empty<StatusDerivedMod>(); }", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_delegates_the_Unity_free_projection_to_Core()
    {
        // Everything that can be unit-tested (filtering to derived channels, parsing the op,
        // source-tagging by instance) lives in Core's StatusDerivedModReader -- this file keeps only the
        // genuinely host-specific fact: reaching the live static.
        var text = ReadInjector("Stats", "StatusDerivedMods.cs");
        Assert.Contains("StatusDerivedModReader.Read(", text, StringComparison.Ordinal);
    }

    static string ReadInjector(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Injector" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
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
