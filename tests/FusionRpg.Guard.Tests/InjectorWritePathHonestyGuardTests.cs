using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Injector write-path honesty (backlog-clean-up BCU8.2, from combat-math-dedup's own audit §2.1b
/// "Bypass of the reporting clamp"). <c>EntityStatWriter.ClampToInt32Reporting</c> exists so an
/// int32 saturation at the Unity boundary is a <c>stat.writer.clampBoundary</c> proof event rather
/// than a silent clamp — but four call sites reached the raw <c>ZombieCombatFields.ClampToInt32</c>
/// instead, so a saturated incoming hit or a saturated LimHealth gate was invisible. Text-based,
/// like the other injector guards: the injector assembly needs a real PVZ Fusion install and never
/// builds under CI, so these are the regression coverage the writer half gets.
/// </summary>
public class InjectorWritePathHonestyGuardTests
{
    /// <summary>
    /// The contract stated by <c>ClampToInt32Reporting</c>'s own doc comment: every injector call site
    /// that used to reach the raw clamp goes through the reporting wrapper. This strips the wrapper's
    /// own body (the one legitimate raw call) and asserts nothing else in non-bridge injector code
    /// touches <c>ZombieCombatFields.ClampToInt32</c> directly.
    ///
    /// <para>Bridges are excluded on purpose: the two profile files ARE the raw clamp and must keep
    /// calling it.</para>
    /// </summary>
    [Fact]
    public void Only_the_reporting_wrapper_calls_the_raw_clamp()
    {
        var injector = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector");
        Assert.True(Directory.Exists(injector), "missing " + injector);

        var writerPath = Path.Combine(injector, "Stats", "EntityStatWriter.cs");
        var writer = StripMethodBody(
            File.ReadAllText(writerPath),
            "internal static int ClampToInt32Reporting(");
        // Prove the strip actually happened: otherwise the wrapper's own raw call would be reported
        // as a leak and this guard would be un-passable rather than broken in its stated direction.
        Assert.DoesNotContain("internal static int ClampToInt32Reporting(", writer, StringComparison.Ordinal);

        var leaks = new List<string>();
        var visited = new List<string>();
        foreach (var file in Directory.EnumerateFiles(injector, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(injector, file).Replace('\\', '/');
            if (rel.StartsWith("Bridges/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            visited.Add(rel);
            var text = file.Equals(writerPath, StringComparison.OrdinalIgnoreCase) ? writer : File.ReadAllText(file);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(".ClampToInt32(", StringComparison.Ordinal))
                    leaks.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        // The two files that hold (or held) the sites this guard exists for must actually have been
        // scanned — an empty enumeration would otherwise pass the assertion below for free.
        Assert.Contains("GameHooks.cs", visited);
        Assert.Contains("Stats/EntityStatWriter.cs", visited);

        Assert.True(leaks.Count == 0,
            "injector clamp sites must route through EntityStatWriter.ClampToInt32Reporting:\n" +
            string.Join("\n", leaks));
    }

    /// <summary>
    /// combat-math-dedup Task 18 (D22): <c>Bridges/pvzrh-3.9/ZombieCombatFields.ClampToInt32</c> and
    /// <c>Bridges/pvzrh-3.8.1/ZombieCombatFields.ClampToInt32</c> hold the same saturation math, and
    /// <b>the duplicate is kept deliberately</b>. The two bridge trees are mutually exclusive at
    /// compile time — each host csproj excludes <c>..\FusionRpg.Injector\Bridges\**</c> and re-includes
    /// only <c>Bridges\$(GameProfile)\**</c> — so hoisting one shared file would put a
    /// profile-independent file inside a tree whose contract is one profile per build. A parity test is
    /// the correct and only answer: the two bodies must stay equivalent, and a drift fails here instead
    /// of shipping one profile a different clamp.
    /// </summary>
    [Fact]
    public void The_two_profile_clamp_bodies_stay_equivalent()
    {
        var injector = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Bridges");
        var threeNine = ClampBody(Path.Combine(injector, "pvzrh-3.9", "ZombieCombatFields.cs"));
        var threeEightOne = ClampBody(Path.Combine(injector, "pvzrh-3.8.1", "ZombieCombatFields.cs"));

        Assert.Equal(threeEightOne, threeNine);

        // Non-vacuity: equivalence alone passes if both bodies were replaced by `return (int)value`
        // — i.e. exactly the silent-narrow the saturation is there to prevent (an unchecked cast
        // wraps, it does not clamp). Pin the two bounds the contract is made of.
        Assert.Contains("int.MaxValue", threeNine, StringComparison.Ordinal);
        Assert.Contains("int.MinValue", threeNine, StringComparison.Ordinal);
    }

    /// <summary>Extracts a method body by brace matching, normalized to single spaces.</summary>
    static string ClampBody(string path)
    {
        Assert.True(File.Exists(path), "missing " + path);
        var body = ExtractBody(File.ReadAllText(path), "public static int ClampToInt32(long value)");
        Assert.NotNull(body);
        return string.Join(" ", body!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    static string? ExtractBody(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        var open = text.IndexOf('{', start);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(open, i - open + 1);
            }
        }
        return null;
    }

    /// <summary>Removes one method (signature + body) so its own raw call is not a leak.</summary>
    static string StripMethodBody(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return text;
        var body = ExtractBody(text, signature);
        if (body is null) return text;
        var open = text.IndexOf('{', start);
        return text.Remove(start, open + body.Length - start);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
