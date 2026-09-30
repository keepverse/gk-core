using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai wave 4 — <b>the lawn AI's Core contract, enforced rather than asserted in prose.</b>
///
/// <para>Four specs state these rules in their Code-style and Boundaries sections, and every one of them
/// recorded the same honest gap: *"the rule 'the lawn reads exactly one time base' … and 'per-actor AI
/// state is dropped before ptr reuse' … are currently covered only by this module's tests"*
/// (`spec-lawn-cast-trigger.md`'s design-gate checklist, last line). A rule carried only by prose drifts
/// the moment somebody adds `DateTime.UtcNow` to a lawn file and no test notices — which is exactly the
/// defect D15 exists to prevent (*"one board, two notions of when"*).</para>
///
/// <para><b>The scope is the DIRECTORY, enumerated, never a list.</b> Every `.cs` under
/// `gk-core/src/FusionRpg.Core/Match/Ai/` is scanned, so a file added tomorrow is covered without anyone
/// remembering to add it — and there is no allowlist for a future session to widen, which is the failure
/// mode `CAI-guard-1`'s re-pin and the atom vocabulary's four stale counts both name.</para>
///
/// <para><b>Comments are stripped first, and that is load-bearing, not tidiness.</b> Two of these files
/// contain the word `DateTime` in a doc comment that PROMISES the ban
/// (`LawnDecisionTrigger.cs` — *"no `DateTime` appears anywhere on its path"*;
/// `LawnOrderQueue.cs` — *"nothing here reads a `DateTime` or a `DateTimeOffset`"*). Scanning the raw
/// text would fail on the very prose that documents the rule, so the stripper is proven in both
/// directions by its own test below. The pattern mirrors the test-local strippers already used for the
/// same reason (`NamingBanTests`, `LegacyEquipTableRetirementGuardTests`); Core has no shared one, so
/// this file carries its own three-line copy rather than reaching into another test assembly.</para>
/// </summary>
public class LawnCoreContractScanTests
{
    /// <summary>Where a wall clock can enter. The lawn reads exactly ONE time base — the 100 ms
    /// `KernelDriveHost.NowTicks` grid, passed in as `nowTick` — and `AdvancedEffectClock` is
    /// wall-clock-seeded for status expiry and is explicitly NOT the decision clock (`CAI4.7` §6, which
    /// found the map row naming the wrong one).</summary>
    static readonly string[] WallClock =
    {
        "DateTime", "DateTimeOffset", "Stopwatch", "Environment.TickCount",
    };

    /// <summary>Where ambient randomness can enter. Randomness comes only from
    /// `SeededRng.DeriveStream`, never from a clock or an ambient generator — the map's own determinism
    /// rule, and the reason an offset is reproducible from `(matchSeed, actorKey)` alone.</summary>
    static readonly string[] AmbientRng =
    {
        "System.Random", "new Random(", "Random.Shared",
    };

    /// <summary>Where file I/O can enter. Core reads no file: the host reads
    /// `data/tuning/combat-ai*.json` and hands Core a string (`tunables-ssot.md` §7.2).</summary>
    static readonly string[] FileIo =
    {
        "File.", "Directory.", "FileStream", "StreamReader", "StreamWriter", "Path.Combine",
    };

    /// <summary>Unity never appears in Core — CI builds it without the game
    /// (`guard-secondary-no-unity.ps1`'s sibling rule for the lawn's own files).</summary>
    static readonly string[] Unity = { "UnityEngine" };

    /// <summary>Where a blocking wait or a second thread can enter. The lawn's decision path never
    /// awaits (`overlay-control-loops.md` Hot rule 3) and the frame slot is main-thread only.</summary>
    static readonly string[] Concurrency =
    {
        "Task.Run", "ThreadPool", "new Thread(", "await ",
    };

    [Fact]
    public void Every_lawn_Core_file_reads_no_wall_clock() =>
        AssertClean(WallClock, "the lawn has exactly one time base; a tick is passed in as `nowTick`");

    [Fact]
    public void Every_lawn_Core_file_uses_only_the_owned_rng() =>
        AssertClean(AmbientRng, "randomness comes only from `SeededRng.DeriveStream`, never a clock or `Random`");

    [Fact]
    public void Every_lawn_Core_file_reads_no_file() =>
        AssertClean(FileIo, "Core reads no file; the host reads the tuning file and hands Core a string");

    [Fact]
    public void Every_lawn_Core_file_is_Unity_free() =>
        AssertClean(Unity, "Core is CI-built without the game");

    [Fact]
    public void Every_lawn_Core_file_never_awaits_or_spawns_a_thread() =>
        AssertClean(Concurrency, "the decision path never awaits (Hot rule 3) and the frame slot is main-thread");

    [Fact]
    public void The_scanned_surface_is_the_whole_lawn_directory_and_every_file_is_readable()
    {
        var files = LawnCoreFiles();

        // Non-empty, so a wrong root or a moved directory fails loudly instead of vacuously passing.
        Assert.NotEmpty(files);
        // The wave-4 Core files this scan exists for are in it — named once, here, so their disappearance
        // is a failure rather than a quieter scan.
        foreach (var expected in new[]
                 {
                     "LawnHeldActionSets.cs", "LawnCastPlan.cs", "LawnDecisionTrigger.cs",
                     "LawnDecisionBudget.cs", "LawnCastTokenPool.cs", "LawnOrderQueue.cs",
                     "DirectOrderAdmission.cs",
                 })
        {
            Assert.Contains(files, f => f.EndsWith(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_scan_strips_comments_so_prose_that_promises_the_ban_does_not_trip_it()
    {
        // Both directions, on REAL content: `LawnDecisionTrigger.cs` says in a doc comment that no
        // `DateTime` appears on its path, and `LawnOrderQueue.cs` says nothing there reads one. Raw text
        // therefore contains the banned token, and the stripped text must not.
        foreach (var name in new[] { "LawnDecisionTrigger.cs", "LawnOrderQueue.cs" })
        {
            var raw = ReadCode(name);
            Assert.Contains("DateTime", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("DateTime", StripComments(raw), StringComparison.Ordinal);
        }

        // And the ban half still bites: a planted code line trips it, while a comment ABOUT the ban does
        // not. A guard never proven to fail is not evidence.
        const string planted = """
            // we must never read DateTime.Now here
            var now = DateTime.Now;
            """;
        Assert.DoesNotContain("DateTime", StripComments("// only a comment: DateTime.Now is banned"), StringComparison.Ordinal);
        Assert.Contains("DateTime", StripComments(planted), StringComparison.Ordinal);
    }

    // ---- the mechanism ---------------------------------------------------------------------

    static void AssertClean(string[] banned, string why)
    {
        var offences = new List<string>();
        foreach (var path in LawnCoreFiles())
        {
            var code = StripComments(File.ReadAllText(path));
            foreach (var token in banned)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                    offences.Add($"{Path.GetFileName(path)} uses `{token}`");
            }
        }

        Assert.True(offences.Count == 0, $"{why}. Offences: {string.Join("; ", offences)}");
    }

    static IReadOnlyList<string> LawnCoreFiles()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Match", "Ai");
        Assert.True(Directory.Exists(dir), "missing " + dir);
        var files = new List<string>(Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    static string ReadCode(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Match", "Ai", fileName);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    /// <summary>Strips `//` line comments (which covers `///` doc comments) and `/* */` block comments,
    /// so the checks see code and never prose that happens to name the thing it bans.</summary>
    static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
