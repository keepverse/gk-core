using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// W8 (spec-turn-engine.md §Determinism): the world simulation's purity, enforced as a source scan
/// rather than as a convention someone remembers.
///
/// A wall-clock read or a `System.Random` inside the turn path would not fail a test — it would
/// quietly make replays disagree between machines, weeks later, with no obvious cause. Cheaper to
/// forbid the symbols outright.
///
/// <para><b>base-defense Gate 0 part 2 (2026-09-05):</b> widened the clock/RNG scan from
/// <c>Core/World</c> to <c>Core/World + Core/Battle + Core/Effects</c> — a siege resolves inside
/// <c>Core/Battle</c> and reads <c>Core/Effects</c>, so a wall clock or an unowned RNG in either tree
/// is exactly as replay-breaking as one in <c>World</c>. Widening surfaced three real defects in the
/// guard itself, fixed alongside the scope change rather than worked around:</para>
///
/// <para><b>1. The scan stopped at the first match per (file, symbol).</b> <c>text.IndexOf</c> found
/// only the first occurrence, so a comment mentioning a banned symbol before a real usage would have
/// masked the real one entirely. Now scans every occurrence.</para>
///
/// <para><b>2. The scan was comment-blind.</b> A doc comment that explains the rule — "never
/// System.Random", "Owned-PRNG adapter... never System.Random" — contains the banned substring and
/// tripped the ban meant to enforce the rule it was documenting. `Core/World` never had this problem
/// because nothing in it happened to explain the rule in prose; `Core/Battle`/`Core/Effects` are full
/// of exactly that prose (four hits on first widening, all comments, zero real violations). Comment
/// text is now stripped before matching, the same discipline <see cref="ReadsTheWorldItself"/>
/// already applies one line at a time.</para>
///
/// <para><b>No floating-point check (owner ruling 2026-09-15).</b> This class used to carry a
/// <c>double|float|decimal</c> declaration ban over <c>Core/World</c>. The project-wide floating-point
/// ban is removed: floating point is allowed for any quantity, and determinism of a double in a hashed
/// golden is handled by a platform stamp (ssot-power-scale.md §10.7), not a source ban. Integer RANGE
/// rules stay with <c>gk-core/scripts/audit-overflow.py</c>.</para>
/// </summary>
public class WorldDeterminismGuardTests
{
    static readonly string[] BannedSymbols =
    {
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "Environment.TickCount",
        "Stopwatch",
        "System.Random",
        "new Random("
    };

    /// <summary>
    /// The one narrow, named exemption for the clock/RNG scan — not a file skip, a single-class
    /// skip. `SystemEffectClock` (`EffectModels.cs`) exists to BE the real wall clock for exactly one
    /// legitimate, non-replayed caller: `FusionRpg.Injector/Effects/EffectRuntime.cs`'s live-PvZ
    /// composition root, which explicitly constructs it (never an implicit default — see
    /// `EffectBag.UtcNow`'s fix in the same Gate-0 pass for the contrast). The injector applies
    /// effects to a live, real-time match; nothing about it claims replay determinism, and
    /// `AGENTS.md`'s hard boundaries never make that claim either. Exempting the TYPE rather than the
    /// FILE means a new, accidental wall-clock read anywhere else in `EffectModels.cs` is still caught.
    /// </summary>
    const string SystemEffectClockException = "public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;";

    [Fact]
    public void The_world_simulation_reads_no_clock_and_rolls_no_unowned_dice()
    {
        var violations = new List<string>();

        foreach (var file in SimulationSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var codeOnly = StripLineComment(lines[i]);
                if (codeOnly.Trim() == SystemEffectClockException) continue;

                foreach (var banned in BannedSymbols)
                {
                    if (!codeOnly.Contains(banned, StringComparison.Ordinal)) continue;
                    violations.Add($"{Path.GetFileName(file)}:{i + 1} → {banned}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "world simulation purity violated (no wall clock, no unowned RNG):\n" + string.Join("\n", violations));
    }

    /// <summary>Drops a trailing <c>//</c> line comment (including <c>///</c> doc comments, which
    /// start with the same two characters) so a banned symbol named IN PROSE — explaining the rule
    /// rather than breaking it — cannot trip the scan. Does not attempt block-comment (<c>/* */</c>)
    /// stripping: none of this tree uses them, and a real block-comment scanner needs multi-line
    /// state this single-pass, per-line scan does not carry.</summary>
    static string StripLineComment(string line)
    {
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    /// <summary>
    /// W26 (spec-ai-commander.md §Boundaries): a policy reads belief, never the truth.
    ///
    /// This is the one rule in the module that no behavioural test can cover. An AI that consulted
    /// `WorldState` would not give *wrong* answers — it would give suspiciously good ones, and every
    /// test asserting it plays well would pass. The only way to catch a right answer arrived at by
    /// cheating is to make the source of the cheat unmentionable.
    /// </summary>
    [Fact]
    public void Nothing_under_World_Ai_may_read_the_world_itself()
    {
        var violations = new List<string>();

        foreach (var file in WorldSourceFiles())
        {
            if (!file.Replace('\\', '/').Contains("/World/Ai/", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (ReadsTheWorldItself(lines[i]))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
        }

        Assert.True(violations.Count == 0,
            "a faction policy must read IWorldView, never the world itself:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// One line's verdict, factored out so the rule can be proven directly rather than only by
    /// planting a violation in the tree and remembering to take it out again.
    /// </summary>
    static bool ReadsTheWorldItself(string line)
    {
        // A comment explaining *why* the type is out of bounds is the one place naming it is not
        // only allowed but wanted — every file under World/Ai/ says so at the top.
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;

        return line.Contains("WorldState", StringComparison.Ordinal);
    }

    [Fact]
    public void The_belief_only_guard_would_actually_catch_a_violation()
    {
        // Seen to fail, so it is known to work.
        Assert.True(ReadsTheWorldItself("    static WorldState? Cheat;"));
        Assert.True(ReadsTheWorldItself("        var truth = world.Sectors; // WorldState leaked in"));

        // And seen not to fire on the things it must not: the doc comments, and a type whose name
        // merely starts the same way.
        Assert.False(ReadsTheWorldItself("/// never touches WorldState — see the spec"));
        Assert.False(ReadsTheWorldItself("// WorldState is deliberately unreachable from here"));
        Assert.False(ReadsTheWorldItself("        var view = new BelievedWorldView(...);"));
    }

    [Fact]
    public void The_guard_would_actually_catch_a_violation()
    {
        // A guard nobody has seen fail is a guard nobody knows works.
        const string sample = "var now = DateTime.UtcNow; // sneaked in";
        Assert.Contains(BannedSymbols, banned => sample.Contains(banned, StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_actually_finds_the_world_sources()
    {
        var files = WorldSourceFiles().ToList();
        Assert.True(files.Count >= 8, $"expected the world module's sources, found {files.Count}");
        Assert.Contains(files, f => f.EndsWith("TurnEngine.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("WorldState.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_actually_finds_battle_and_effects_sources()
    {
        // The Gate 0 widening, proven the same way The_scan_actually_finds_the_world_sources proves
        // the original scope: seen to include a real, named file, not just asserted to.
        var files = SimulationSourceFiles().ToList();
        Assert.Contains(files, f => f.EndsWith("BattleEngine.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("EffectBag.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("WorldState.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Comment_stripping_hides_prose_but_not_code_on_the_same_line()
    {
        // The exact shape that produced all four false positives on first widening: a doc comment
        // EXPLAINING the ban, worded so it repeats the banned phrase.
        Assert.DoesNotContain("System.Random",
            StripLineComment("    /// Owned-PRNG adapter — never System.Random on a replayable path."));

        // And the fix must not become a new hiding place: real code before a trailing comment stays
        // visible.
        Assert.Contains("System.Random", StripLineComment("var r = new System.Random(); // TODO seed me"));

        // A bare code line with no // at all is returned whole.
        Assert.Equal("var x = 1;", StripLineComment("var x = 1;"));
    }

    [Fact]
    public void The_scan_finds_every_occurrence_not_just_the_first()
    {
        // Reproduces the exact hazard the old text.IndexOf(banned) shape had: a comment mentioning a
        // banned symbol earlier in a file could mask a real violation of the SAME symbol later in it.
        // This test constructs that shape directly against the scanning logic (StripLineComment +
        // per-line Contains, as the real fact runs it) rather than against a planted file, so it does
        // not depend on the tree staying clean of a second, deliberately-planted violation.
        var lines = new[]
        {
            "    // never System.Random here, obviously",
            "    var leaked = new System.Random();"
        };

        var hitLines = lines
            .Select((line, i) => (line: StripLineComment(line), i))
            .Where(x => x.line.Contains("System.Random", StringComparison.Ordinal))
            .Select(x => x.i)
            .ToList();

        Assert.Single(hitLines);
        Assert.Equal(1, hitLines[0]); // the SECOND line — the first is stripped as a comment
    }

    [Fact]
    public void The_system_effect_clock_exemption_is_narrow_not_a_file_skip()
    {
        // The exemption matches the exact declaration line, not the file or the class. A DIFFERENT
        // wall-clock read added anywhere else in the same file must still be caught.
        var exempt = StripLineComment("    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;").Trim();
        Assert.Equal(SystemEffectClockException, exempt);

        var notExempt = "        var leaked = DateTimeOffset.UtcNow; // a different line, same file";
        Assert.NotEqual(SystemEffectClockException, StripLineComment(notExempt).Trim());
        Assert.Contains("DateTimeOffset.UtcNow", StripLineComment(notExempt));
    }

    /// <summary>
    /// base-defense Gate 0 part 2: World-only, for the AI-belief and source-count checks — UNCHANGED.
    /// </summary>
    static IEnumerable<string> WorldSourceFiles()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "World");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
    }

    /// <summary>
    /// base-defense Gate 0 part 2: World + Battle + Effects, for
    /// `The_world_simulation_reads_no_clock_and_rolls_no_unowned_dice` only. A siege resolves inside
    /// `Core/Battle` and reads `Core/Effects`, so a wall clock or an unowned RNG in either tree is
    /// exactly as replay-breaking as one in `World` — the guard was scoped to `World` only because
    /// that was the only turn-based simulation in the repo when it was written, not because the other
    /// two are exempt.
    /// </summary>
    static IEnumerable<string> SimulationSourceFiles()
    {
        var coreRoot = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core");
        var roots = new[]
        {
            Path.Combine(coreRoot, "World"),
            Path.Combine(coreRoot, "Battle"),
            Path.Combine(coreRoot, "Effects"),
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
