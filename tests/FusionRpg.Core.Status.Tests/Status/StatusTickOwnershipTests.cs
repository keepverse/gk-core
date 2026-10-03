using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// status-tracks ST0.3 (`one-tick-owner`) — <see cref="CoreStatus.StatusRuntime.Tick"/> has a
/// CLOSED caller set, and both of its callers hand it an injected instant rather than reading one.
///
/// <para><b>What is pinned here, and how.</b> Two things, at two levels of strength:</para>
/// <list type="bullet">
/// <item><b>Exactly two call sites, source-scanned.</b> Every <c>*.cs</c> under
/// <c>gk-core/src/FusionRpg.Core/</c> is walked and each <c>.Tick(</c> is attributed to a
/// <see cref="CoreStatus.StatusRuntime"/> by REFLECTING OVER CORE for every field and property whose
/// declared type is a runtime — never by matching the name <c>Status</c>, which is how a scan ends up
/// guarding two spellings a future author would not use. This is <c>ActionBaseNoAtkReadGuardTests</c>'s
/// shape (Guard.Tests) — a single-line allowlist keyed by repo-relative path — not a new mechanism.</item>
/// <item><b>The observable half, at the lawn seam.</b> <c>EffectBag.UtcNow</c> is the seam's clock and
/// it is an injected delegate that THROWS rather than defaulting if no composition root chose one, so a
/// seam that read the wall clock itself could not be driven to an exact instant at all.</item></list>
///
/// <para><b>The honest limit of the scan, recorded rather than papered over.</b> It is line-based source
/// text resolved at member granularity, so it cannot see a call written through a renamed local
/// (<c>var rt = bag.Status; rt.Tick(now, sink);</c>) or split across lines (<c>x.Status\n  .Tick(</c>).
/// The same caveat <c>KernelPurityScan</c>'s own doc comment records, for the same reason. What the scan
/// buys is that adding a third direct tick owner anywhere under <c>Core/</c> turns this test red
/// naming the file — the regression ST0.3 exists to catch.</para>
/// </summary>
public class StatusTickOwnershipTests
{
    /// <summary>A fixed instant, never a read of the wall clock.</summary>
    static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    /// <summary>
    /// The whole caller set, keyed by repo-relative path so a finding cites a file an operator can
    /// open. Spelled out as exact lines rather than file names: the argument each seam passes is
    /// part of the contract being pinned (the injected `now`), not an incidental detail.
    /// </summary>
    static readonly (string Path, string Call)[] KnownTickOwners =
    {
        // The lawn / offline-funnel seam. `now` is `UtcNow()` — EffectBag's own injected scheduler,
        // which throws when no composition root has chosen one (EffectBag.cs:251-260).
        ("src/FusionRpg.Core/Effects/EffectBag.cs", "Status.Tick(now, sink, BoardSnapshot, StatusRng)"),

        // The battle seam. `now` is `state.T0.AddMilliseconds(roundClock.Now)` — the battle's virtual
        // clock, advanced by the round scheduler, never the wall clock (BattleEngine.cs:444-456).
        ("src/FusionRpg.Core/Battle/BattleEngine.cs",
            "state.Status.Tick(now, state.PulseSink, board: state.CombatBoardSnapshot, spreadRng: state.StatusRng)"),
    };

    // ==========================================================================================
    // ST0.3 — the caller set
    // ==========================================================================================

    [Fact]
    public void StatusTick_is_driven_from_exactly_the_two_known_seams_inside_Core()
    {
        var root = KeepverseRoots.Core();
        var coreDir = Path.Combine(root, "src", "FusionRpg.Core");
        Assert.True(Directory.Exists(coreDir), $"core source dir not found: {coreDir}");

        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, call) in KnownTickOwners)
        {
            Assert.True(File.Exists(Path.Combine(root, path)), $"tick owner missing: {path}");
            known[path] = call;
        }

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsStatusRuntimeTickCall(lines[i])) continue;
                if (known.TryGetValue(rel, out var call) && lines[i].Contains(call, StringComparison.Ordinal))
                    continue;
                offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "StatusRuntime.Tick has no owner outside its two declared seams — the battle's virtual " +
            "clock and the injected-`now` funnel. A third call site would introduce a clock this " +
            "program does not admit, so it needs the map updated and a reason, not a test edit. " +
            "Unrecognised tick calls:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The positive half, so the scan above cannot pass by the tick being gone. Each owner is
    /// re-asserted on the line itself: the allowlist holds exact call text, and a seam that had its
    /// arguments rewritten would stop matching and redden the test above — but only if the file is
    /// still there. Asserting the text separately makes a silently deleted seam loud on its own.
    /// </summary>
    [Fact]
    public void Both_tick_owners_still_pass_an_injected_now_and_neither_reads_a_clock()
    {
        foreach (var (path, call) in KnownTickOwners)
        {
            var lines = File.ReadAllLines(Path.Combine(KeepverseRoots.Core(), path));
            var hit = lines.Count(l => l.Contains(call, StringComparison.Ordinal));

            Assert.True(hit == 1,
                $"{path}: expected exactly one line carrying '{call}', found {hit}. The seam either " +
                "moved, was deleted, or was rewritten — all three need the map, not a test edit.");
        }

        // Neither seam computes an instant: both pass a local named `now`, and neither file reads a
        // clock in code. The two `DateTimeOffset.UtcNow` mentions under EffectBag are inside the doc
        // comment explaining why the field default was deleted, so this reads CODE, not raw text.
        Assert.Equal(0, WallClockHits(EffectBagPath()));
        Assert.Equal(0, WallClockHits(BattleEnginePath()));

        // ...and the lawn seam's instant comes from the injected scheduler, read inside `TickDots` as
        // a bare `UtcNow()` (EffectBag.cs:844) rather than from any clock the seam could reach itself.
        Assert.Equal(1, LinesContaining(EffectBagPath(), "var now = UtcNow();"));
    }

    [Fact]
    public void The_two_filed_names_the_scan_resolves_through_are_the_ones_the_code_still_declares()
    {
        // Two names, asserted here so a rename on either side of the pin fails loudly rather than
        // leaving a scan that can never match anything again (and therefore always green).
        var effectBag = File.ReadAllText(EffectBagPath());
        Assert.Contains("public StatusRuntime? Status { get; set; }", effectBag, StringComparison.Ordinal);
        var battleState = File.ReadAllText(Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Core", "Battle", "BattleRunState.cs"));
        Assert.Contains("public readonly StatusRuntime Status;", battleState, StringComparison.Ordinal);
    }

    // ==========================================================================================
    // ST0.3 — the observable half: injected, deterministic, no double-pulse
    // ==========================================================================================

    [Fact]
    public void Tick_is_clock_injected_so_the_same_instant_twice_pulses_once()
    {
        var rt = Runtime();
        var now = Origin;
        var applied = rt.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(applied.Applied);

        // One due pulse at `now + period`, then the SAME instant again. `Tick` mutates `NextPulse`
        // in place, so the second call has nothing left to fire — a double-pulse would mean the
        // schedule advanced by elapsed real time rather than by the instant it was handed.
        var sink = new CountingPulseSink();
        Assert.Equal(1, rt.Tick(now.AddMilliseconds(1000), sink));
        Assert.Equal(0, rt.Tick(now.AddMilliseconds(1000), sink));
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void The_lawn_seams_instant_is_the_injected_scheduler_the_composition_root_wired()
    {
        // `EffectBag.UtcNow` has no field default any more (Gate 0, base-defense audit C4): unset, it
        // throws instead of silently supplying wall time, and a composition root that wires it to the
        // real clock is stating "this runtime does not claim replay determinism" out loud. Either way
        // the seam never reads a clock itself, which is what makes it drivable to an exact instant.
        //
        // One runtime for both halves: the apply and the expiry must share a single bag, or the second
        // assertion below would be pruning a status this call cannot see.
        var bag = new EffectBag(
            new InMemoryEffectCatalog(), new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
            new RecordingEffectSink());
        var now = Origin;
        bag.UtcNow = () => now;
        bag.Status = Runtime();

        // One DoT at an explicit instant. `TickDots()` takes no arguments -- the instant is not a
        // parameter here, so a caller cannot hand the seam a time of its own choosing.
        Assert.True(bag.Status.Apply(WitherApply(now), new CoreStatus.FixedStatusRng(0.0), now).Applied);
        Assert.Equal(0, bag.TickDots()); // nothing due: the first pulse is at `now + period`
        Assert.Single(bag.Status.ForHost("Z1"));

        bag.UtcNow = () => now.AddMilliseconds(1000); // the period elapses on the root's own scheduler
        Assert.Equal(1, bag.TickDots()); // one pulse, reported by the seam, to the exact instant the root chose
        Assert.Equal(1, bag.Status.ForHost("Z1")[0].PulsesFired);
        Assert.Single(bag.Status.ForHost("Z1"));

        bag.UtcNow = () => now.AddMilliseconds(6000); // past ExpiresAt
        Assert.Equal(0, bag.TickDots());
        Assert.Empty(bag.Status.ForHost("Z1"));
    }

    [Fact]
    public void The_lawn_seam_refuses_to_invent_an_instant_when_no_composition_root_chose_one()
    {
        // The loud half. `TickDots` reads `UtcNow()` INSIDE its `Status != null` branch (Gate 0), so
        // a bag with no runtime never pays for a clock, and a bag WITH one cannot have that clock
        // supplied silently on its behalf -- the caller must choose.
        var bag = new EffectBag(
            new InMemoryEffectCatalog(), new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
            new RecordingEffectSink());

        Assert.Equal(0, bag.TickDots()); // no Status wired: the gate is skipped before the clock is read

        bag.Status = Runtime();
        var ex = Assert.Throws<InvalidOperationException>(() => bag.TickDots());
        Assert.Contains("has not been set", ex.Message, StringComparison.Ordinal);
    }

    // ==========================================================================================
    // helpers
    // ==========================================================================================

    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static CoreStatus.StatusApplyInput WitherApply(DateTimeOffset now) => new(
        "wither",
        HostPtr: "Z1",
        AttackerPtr: "P1",
        GrantId: "g1",
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    /// <summary>
    /// Is this line a call on a <see cref="CoreStatus.StatusRuntime"/>? Resolved through the DECLARED
    /// TYPE of the receiver, never its name: the names come from reflecting over Core itself, so a
    /// field that stops being a runtime stops matching and a field that starts being one starts.
    /// </summary>
    static bool IsStatusRuntimeTickCall(string line)
    {
        var trimmed = line.TrimStart();
        var at = trimmed.IndexOf(".Tick(", StringComparison.Ordinal);
        if (at <= 0) return false;
        if (trimmed[..at].Trim() == nameof(CoreStatus.StatusRuntime)) return false; // no static Tick exists

        // The trailing IDENTIFIER, not the whole left-hand side: one seam writes `Status.Tick(`
        // unqualified (inside EffectBag itself) and the other writes `state.Status.Tick(`, and both
        // must resolve to the same member. Splitting the left side on `.` instead would keep the
        // assignment in `n = Status` and miss the unqualified call entirely.
        var member = TrailingIdentifier.Match(trimmed[..at]);
        return member.Success && StatusRuntimeMemberNames().Contains(member.Value);
    }

    static readonly Regex TrailingIdentifier = new(@"[\p{L}\p{N}_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Every member name Core declares whose type IS a <see cref="CoreStatus.StatusRuntime"/>,
    /// derived rather than listed: `EffectBag.Status` and `BattleRunState.Status` are today the whole
    /// set, and a third runtime-typed member joins it the moment someone adds one. That is what makes
    /// the scan's "no owner beyond the two seams" claim about the CALLER rather than about two
    /// spellings a future author would not use.
    ///
    /// <para>Compiler-generated backing fields are dropped: they carry no spelling anyone writes.</para>
    ///
    /// <para><b>Known limit, stated rather than hidden.</b> The receiver's ROOT is not resolved (it is
    /// a local in both seams, and resolving a local's type from source text is a compiler's job), so a
    /// call written through a renamed local — <c>var rt = bag.Status; rt.Tick(now, sink);</c> — is not
    /// seen. A line-based scan cannot see that, and a test that claimed otherwise would be the kind of
    /// guard that stays green while the thing it guards changes. What it does catch is every direct
    /// member call, which is how both shipped seams are written.</para>
    /// </summary>
    static HashSet<string> StatusRuntimeMemberNames()
    {
        if (_runtimeMemberNames != null) return _runtimeMemberNames;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Core().GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.Static))
                if (field.FieldType == typeof(CoreStatus.StatusRuntime)) names.Add(field.Name);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance | BindingFlags.Static))
                if (property.PropertyType == typeof(CoreStatus.StatusRuntime)) names.Add(property.Name);
        }

        // Compiler-generated backing fields carry no spelling anyone writes; `Status` covers both
        // seams either way, and leaving them in would only widen the match to a name the compiler owns.
        foreach (var backing in names.Where(n => n.Contains("<>", StringComparison.Ordinal)).ToList())
            names.Remove(backing);

        // A runtime is reachable from here, so the set is never empty and the scan above is never
        // vacuously green on a Core that failed to load its own types.
        Assert.NotEmpty(names);
        return _runtimeMemberNames = names;
    }

    static HashSet<string>? _runtimeMemberNames;

    static Assembly Core() => typeof(CoreStatus.StatusRuntime).Assembly;

    /// <summary>The lines of <paramref name="path"/> whose CODE (comments stripped) contains
    /// <paramref name="token"/> as a plain substring.</summary>
    static int LinesContaining(string path, string token)
    {
        var count = 0;
        foreach (var line in Code(File.ReadAllText(path)))
            if (line.Contains(token, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>How many CODE lines in the file read an ambient clock or an unseeded RNG.</summary>
    static int WallClockHits(string path)
    {
        var forbidden = new[]
        {
            "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
            "Environment.TickCount", "Stopwatch", "System.Random", "new Random(", "Random.Shared",
        };

        var hits = 0;
        foreach (var line in Code(File.ReadAllText(path)))
            foreach (var token in forbidden)
                if (line.Contains(token, StringComparison.Ordinal))
                {
                    hits++;
                    break;
                }
        return hits;
    }

    /// <summary>
    /// The file's lines with comments removed. Both tick owners NAME the banned constructs in their
    /// own doc comments (why the field default was deleted, what the round clock is), so scanning raw
    /// text would fail on the prose that documents the rule — the false positive
    /// <c>LawnCoreContractScanTests</c> records, and the reason that guard strips first.
    /// </summary>
    static IEnumerable<string> Code(string text) =>
        Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => Regex.Replace(l, @"//.*$", "").Trim())
            .Where(l => l.Length > 0);

    static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    static string EffectBagPath() =>
        Path.Combine(KeepverseRoots.Core(), "src", "FusionRpg.Core", "Effects", "EffectBag.cs");

    static string BattleEnginePath() =>
        Path.Combine(KeepverseRoots.Core(), "src", "FusionRpg.Core", "Battle", "BattleEngine.cs");

    sealed class CountingPulseSink : CoreStatus.IStatusPulseSink
    {
        public int Count { get; private set; }

        public void PulseHp(CoreStatus.StatusInstance instance, double amount) => Count++;
    }
}