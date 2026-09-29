using System.Runtime.CompilerServices;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>T1 — the clock and the Future Event List. No actors, no battle, no game.</summary>
public class EventQueueTests
{
    static List<ScheduledEvent> Drain(EventQueue q, long now)
    {
        var into = new List<ScheduledEvent>();
        q.PopDue(now, into);
        return into;
    }

    [Fact]
    public void Pop_order_is_due_tick_then_insertion()
    {
        var q = new EventQueue();
        q.Schedule(200, "b", 1, 0);
        q.Schedule(100, "a", 1, 0);
        q.Schedule(100, "c", 1, 0);   // ties with "a" — insertion order decides

        var popped = Drain(q, 500).Select(e => e.OwnerKey).ToList();
        Assert.Equal(new[] { "a", "c", "b" }, popped);
    }

    [Fact]
    public void Pop_takes_only_events_due_now()
    {
        var q = new EventQueue();
        q.Schedule(100, "a", 1, 0);
        q.Schedule(300, "b", 1, 0);

        Assert.Equal(new[] { "a" }, Drain(q, 100).Select(e => e.OwnerKey));
        Assert.Empty(Drain(q, 299));
        Assert.Equal(new[] { "b" }, Drain(q, 300).Select(e => e.OwnerKey));
    }

    [Fact]
    public void Cancel_removes_only_its_own_event()
    {
        var q = new EventQueue();
        q.Schedule(100, "a", 1, 0);
        var h = q.Schedule(100, "b", 1, 0);
        q.Schedule(100, "c", 1, 0);

        Assert.True(q.Cancel(h));
        Assert.Equal(new[] { "a", "c" }, Drain(q, 100).Select(e => e.OwnerKey));
    }

    [Fact]
    public void Cancelling_a_fired_or_unknown_handle_is_a_no_op()
    {
        var q = new EventQueue();
        var h = q.Schedule(100, "a", 1, 0);
        Drain(q, 100);

        Assert.False(q.Cancel(h));      // already fired — not an exception
        Assert.False(q.Cancel(new EventHandle(long.MaxValue, 9999)));   // foreign queue + unknown seq
    }

    [Fact]
    public void Reschedule_moves_the_event_and_preserves_seq()
    {
        // The audit's I10: implemented as cancel+insert with a fresh Seq, a delay effect would
        // silently reorder UNRELATED actors and make every golden history-dependent.
        var q = new EventQueue();
        var moved = q.Schedule(100, "moved", 1, 0);
        q.Schedule(500, "x", 1, 0);
        q.Schedule(500, "y", 1, 0);     // x before y, by insertion

        Assert.True(q.Reschedule(moved, 500));

        var popped = Drain(q, 500).Select(e => e.OwnerKey).ToList();
        // "moved" keeps its original (earliest) Seq, so it sorts ahead of x and y — and
        // crucially x still precedes y: rescheduling one event did not disturb the others.
        Assert.Equal(new[] { "moved", "x", "y" }, popped);
    }

    [Fact]
    public void Peek_reports_the_next_due_tick()
    {
        var q = new EventQueue();
        Assert.Null(q.PeekDueTick());
        q.Schedule(400, "a", 1, 0);
        q.Schedule(150, "b", 1, 0);
        Assert.Equal(150, q.PeekDueTick());
    }

    [Fact]
    public void Replaying_the_same_script_produces_the_same_sequence()
    {
        string Run()
        {
            var q = new EventQueue();
            var handles = new List<EventHandle>();
            for (var i = 0; i < 50; i++)
                handles.Add(q.Schedule((i * 37) % 200, "a" + i, i % 3, i));
            for (var i = 0; i < 50; i += 7) q.Cancel(handles[i]);
            for (var i = 1; i < 50; i += 11) q.Reschedule(handles[i], 500 + i);
            return string.Join(",", Drain(q, 10_000).Select(e => e.OwnerKey));
        }

        Assert.Equal(Run(), Run());
    }
}

public class SimulationClockTests
{
    [Fact]
    public void Next_event_advance_jumps_to_the_next_due_tick()
    {
        var q = new EventQueue();
        q.Schedule(1234, "a", 1, 0);
        var clock = new SimulationClock();

        var r = clock.TryAdvance(new NextEventAdvance(), q);
        Assert.Equal(AdvanceOutcome.Advanced, r.Outcome);
        Assert.Equal(1234, clock.Now);
    }

    [Fact]
    public void Next_event_advance_blocks_on_an_empty_queue()
    {
        // The audit's C3: a jumping clock has no wall-time to dwell in, so it must be able to
        // report that it cannot advance — otherwise an interactive battle leaps past its input
        // window. This is why TryAdvance returns a result instead of void.
        var clock = new SimulationClock();
        var r = clock.TryAdvance(new NextEventAdvance(), new EventQueue());

        Assert.Equal(AdvanceOutcome.Blocked, r.Outcome);
        Assert.Equal(0, clock.Now);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void Fixed_increment_at_sixty_fps_does_not_drift()
    {
        // 1000 ms / 60 frames = 16.667 ticks per frame. Naive Δ=16 loses 0.667 ms per frame —
        // 40 ms per second, 2.4 seconds per minute. The carry must eliminate that entirely.
        var advance = new FixedIncrementAdvance(ticksPerFrameNum: 1000, ticksPerFrameDen: 60);
        var clock = new SimulationClock();
        var q = new EventQueue();

        for (var i = 0; i < 10_000; i++)
            clock.TryAdvance(advance, q, frames: 1);

        Assert.Equal(10_000L * 1000 / 60, clock.Now);
    }

    [Fact]
    public void Fixed_increment_one_frame_at_a_time_equals_one_big_step()
    {
        var a = new SimulationClock();
        var advA = new FixedIncrementAdvance(1000, 60);
        for (var i = 0; i < 600; i++) a.TryAdvance(advA, new EventQueue(), frames: 1);

        var b = new SimulationClock();
        b.TryAdvance(new FixedIncrementAdvance(1000, 60), new EventQueue(), frames: 600);

        Assert.Equal(b.Now, a.Now);
    }

    [Fact]
    public void Clock_never_moves_backwards()
    {
        var q = new EventQueue();
        q.Schedule(500, "a", 1, 0);
        var clock = new SimulationClock();
        clock.TryAdvance(new NextEventAdvance(), q);

        q.Schedule(100, "late", 1, 0);   // scheduled in the past
        var r = clock.TryAdvance(new NextEventAdvance(), q);

        Assert.Equal(AdvanceOutcome.Advanced, r.Outcome);
        Assert.Equal(500, clock.Now);    // clamped, not rewound
    }
}

/// <summary>
/// The purity guard, as a source scan rather than reflection — reflection cannot see a
/// <c>DateTime.UtcNow</c> inside a method body, and the audit's I12 point was that the
/// invariant was being enforced exactly where it was not at risk.
/// </summary>
public class TimelinePurityGuardTests
{
    static string TimelineDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                                   // tests/.../Battle/Timeline
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", ".."));   // repo root
        return Path.Combine(repo, "src", "FusionRpg.Core", "Battle", "Timeline");
    }

    [Fact]
    public void Kernel_sources_contain_no_wall_clock_or_rng()
    {
        var dir = TimelineDir();
        Assert.True(Directory.Exists(dir), $"kernel source dir not found: {dir}");
        Assert.True(Directory.GetFiles(dir, "*.cs").Length > 0, "purity scan found no kernel files to check");

        var offences = KernelPurityScan.Scan(dir);
        Assert.True(offences.Count == 0,
            "kernel purity violated (no wall clock, no RNG, no dictionary enumeration):\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("var t = DateTime.UtcNow;", "DateTime")]
    [InlineData("readonly Random _rng = new();", "Random")]
    // Floating point is not a purity violation (owner ruling 2026-09-15 removed the project-wide
    // floating-point ban). These lines must stay clean — a guard that flags a legal construct cries
    // wolf and gets suppressed.
    [InlineData("double ratio = 0.5;", null)]
    [InlineData("float dt = 0.016f;", null)]
    [InlineData("var x = 1.5f;", null)]
    [InlineData("var r = (double)a / b;", null)]
    [InlineData("var x = arr[1..5];", null)]
    [InlineData("var x = 0x1F;", null)]
    [InlineData("var x = 1_000L;", null)]
    [InlineData("throw new Exception(\"expected 1.5 here\");", null)]
    public void The_purity_scan_actually_detects_a_violation(string badLine, string? expectedToken)
    {
        // Tests the guard, not the kernel. A scan that stays green while the invariant is broken
        // is worse than none — it manufactures confidence. This proves it can fail.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-purity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Offender.cs"),
                new[] { "namespace X;", "public sealed class Offender", "{", "    " + badLine, "}" });

            var offences = KernelPurityScan.Scan(tmp);
            if (expectedToken is null)
            {
                // The negative half: a line that merely LOOKS numeric must stay clean.
                Assert.True(offences.Count == 0,
                    "guard cried wolf on a legitimate line:\n" + string.Join("\n", offences));
                return;
            }

            Assert.NotEmpty(offences);
            Assert.Contains(offences, o => o.Contains(expectedToken, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Theory]
    [InlineData("var x = items.Where(i => i > 0);", ".Where(")]
    [InlineData("var all = items.ToList();", ".ToList(")]
    [InlineData("var go = FindObjectsOfType<Thing>();", "FindObjectsOfType")]
    [InlineData("var s = StatSystem.Resolve(actor);", "StatSystem.Resolve")]
    public void The_scan_detects_tick_path_cost(string badLine, string expectedToken)
    {
        // Frame-cost rules, not style: the kernel ticks inside the Unity frame, so LINQ allocates
        // and a scene scan is the exact cost this repo already had to rescue once.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Ticker.cs"),
                new[] { "public sealed class Ticker", "{", "    void Tick() { " + badLine + " }", "}" });

            var offences = KernelPurityScan.Scan(tmp);
            Assert.Contains(offences, o => o.Contains(expectedToken, StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void The_diagnostics_exemption_covers_tick_cost_but_never_determinism()
    {
        // BattleTrace may allocate — it is null in production. It may NOT read a clock: a
        // diagnostic that made the kernel nondeterministic would corrupt the very fixtures the
        // byte-identity gate compares against.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "BattleTrace.cs"),
                new[] { "public sealed class BattleTrace", "{", "    object A() { return items.ToArray(); }",
                        "    object B() { return DateTime.UtcNow; }", "}" });

            var offences = KernelPurityScan.Scan(tmp);
            Assert.DoesNotContain(offences, o => o.Contains(".ToArray(", StringComparison.Ordinal));
            Assert.Contains(offences, o => o.Contains("DateTime", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void The_exemption_does_not_leak_to_any_other_file()
    {
        // The exemption is a named list precisely so it cannot grow by pattern. A file that is
        // merely NEAR the diagnostic, or named similarly, gets no relief.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "BattleTraceHelper.cs"),
                new[] { "public sealed class BattleTraceHelper", "{", "    object A() { return items.ToArray(); }", "}" });

            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains(".ToArray(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Theory]
    [InlineData("items.Where (e => e.Alive);", ".Where(")]                       // one space
    [InlineData("System.Linq.Enumerable.ToList<long>(items);", ".ToList(")]      // generic args
    [InlineData("var x = items.Select  <int> (i => i);", ".Select(")]            // both
    public void Whitespace_and_generic_arguments_do_not_defeat_the_tick_path_rules(string bad, string token)
    {
        // C# allows whitespace and type arguments between a method name and its argument list, so
        // a raw substring match on ".Where(" is defeated by ONE KEYSTROKE. Matching the call shape
        // is the difference between a guard and a formality.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Sneaky.cs"),
                new[] { "public sealed class Sneaky { void Go() { " + bad + " } }" });

            Assert.Contains(KernelPurityScan.Scan(tmp), o => o.Contains(token, StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_using_static_escape_hatch_is_itself_flagged()
    {
        // The worst bypass found: one innocuous line makes every LINQ call non-dotted, so all the
        // tick-path rules stop matching for the whole file — and the enabling line was invisible.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Escape.cs"),
                new[] { "using static System.Linq.Enumerable;",
                        "public sealed class Escape { object A() { return ToList(items); } }" });

            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains("using static", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Block_comments_and_non_LINQ_intrinsics_do_not_raise_false_alarms()
    {
        // Every one of these was a false positive before: prose inside /* */, in-place
        // Array/List reverse, string.Concat. A guard that cries wolf gets disabled, which is the
        // same outcome as no guard, reached more slowly.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Fine.cs"), new[]
            {
                "/* never read DateTime here, and the old design used .ToList( */",
                "public sealed class Fine",
                "{",
                "    void A(long[] xs) { Array.Reverse(xs); }",
                "    string B(string a, string b) { return string.Concat(a, b); }",
                "    long C(long x) { return Math.Max(0, x - 1); }",
                "}"
            });

            Assert.Empty(KernelPurityScan.Scan(tmp));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Randomness_and_wall_clock_have_no_side_doors()
    {
        var tmp = NewTempDir();
        try
        {
            foreach (var (line, token) in new[]
                     {
                         ("var g = Guid.NewGuid();", "Guid.NewGuid"),
                         ("var t = Environment.TickCount64;", "Environment.Tick"),
                         ("var h = key.GetHashCode();", ".GetHashCode(")
                     })
            {
                File.WriteAllLines(Path.Combine(tmp, "Door.cs"),
                    new[] { "public sealed class Door { void A() { " + line + " } }" });
                Assert.Contains(KernelPurityScan.Scan(tmp),
                    o => o.Contains(token, StringComparison.Ordinal));
            }
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void The_exemption_is_matched_by_path_not_by_bare_filename()
    {
        // Over a recursive scan, matching the bare name lets Nested/BattleTrace.cs inherit the
        // exemption for free — precisely the silent growth the named list exists to prevent.
        var tmp = NewTempDir();
        try
        {
            var nested = Path.Combine(tmp, "Nested");
            Directory.CreateDirectory(nested);
            File.WriteAllLines(Path.Combine(nested, "BattleTrace.cs"),
                new[] { "public sealed class BattleTrace { object A() { return items.ToArray(); } }" });

            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains(".ToArray(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Math_intrinsics_are_not_mistaken_for_LINQ_but_real_LINQ_still_is()
    {
        // The guard caught Math.Max( on its first strict run — a false positive, since Math.Max
        // is a cheap intrinsic and nothing like IEnumerable.Max. A guard that cries wolf gets
        // suppressed, which is the same outcome as no guard, reached more slowly. Both halves
        // are asserted so the exclusion cannot quietly swallow the real thing.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Fine.cs"),
                new[] { "public sealed class Fine { int A(int x) { return Math.Max(0, x - 1); } }" });
            Assert.Empty(KernelPurityScan.Scan(tmp));

            File.WriteAllLines(Path.Combine(tmp, "NotFine.cs"),
                new[] { "public sealed class NotFine { int A() { return items.Max(); } }" });
            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains(".Max(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void The_scan_reaches_into_subdirectories()
    {
        // A top-level-only scan stops covering the kernel the moment anyone adds a subfolder —
        // green guard, unguarded code. That is worse than no guard at all.
        var tmp = NewTempDir();
        try
        {
            var nested = Path.Combine(tmp, "Nested", "Deeper");
            Directory.CreateDirectory(nested);
            File.WriteAllLines(Path.Combine(nested, "Hidden.cs"),
                new[] { "public sealed class Hidden { long T() { return DateTime.UtcNow.Ticks; } }" });

            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains("DateTime", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_comment_marker_inside_a_string_does_not_blind_the_scan()
    {
        // Cutting at the first "//" is a BYPASS, not merely a false positive: the marker inside
        // the literal would truncate the line and hide the violation that follows it.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Sneaky.cs"),
                new[] { "public sealed class Sneaky", "{",
                        "    void Go() { var s = \"a//b\"; var t = DateTime.UtcNow; }", "}" });

            Assert.Contains(KernelPurityScan.Scan(tmp),
                o => o.Contains("DateTime", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_trailing_comment_does_not_trip_the_scan()
    {
        // The scan strips comments before matching, so annotating a line stays safe. Without this,
        // the guard would punish exactly the explanations that make it understandable.
        var tmp = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Clean.cs"),
                new[] { "public sealed class Clean", "{", "    long Now; // never a DateTime here", "}" });

            Assert.Empty(KernelPurityScan.Scan(tmp));
        }
        finally { Cleanup(tmp); }
    }

    static string NewTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    static void Cleanup(string dir)
    {
        Directory.Delete(dir, true);
    }

    [Fact]
    public void The_purity_scan_ignores_prose_that_merely_names_a_banned_token()
    {
        // Doc comments explain WHY wall-clock reads are forbidden, so they must be allowed to say
        // the words — otherwise the guard punishes documentation.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-purity-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllLines(Path.Combine(tmp, "Clean.cs"),
                new[] { "// never read DateTime here", "public sealed class Clean { public long Now; }" });

            Assert.Empty(KernelPurityScan.Scan(tmp));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
