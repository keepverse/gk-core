using System.Text.Json;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// A guard must be REACHABLE by the phase that owns it, and that phase is not the deploy.
///
/// <para><b>Re-pointed 2026-09-26 (owner ruling: deploy ≠ verification; a deploy runs no guard
/// suite).</b> After solid-enforcement `guard-runner`, deploy-play ran
/// `run_guards.py --tier local --include-backlog` — the WIDEST tier (every `ci` guard plus the
/// machine-only ones, plus the report-only rows): 28 guards, 203 seconds measured, paid before every
/// deploy — one whose own measured stages come to 28.3s (2026-09-26, default MelonLoader install) —
/// and it was green all four times it ran. Verification belongs to the implement
/// phase and to CI/nightly, both of which call the runner themselves. So the assertion is no longer
/// "deploy-play invokes the runner"; it is "the guard is reachable where it is supposed to gate":</para>
///
/// <list type="bullet">
/// <item>its registry row exists and is gating, and</item>
/// <item>CI reaches it (`run_guards.py --tier ci`, spelled out in `.github/workflows/ci.yml`) and/or</item>
/// <item>the implement-phase gate reaches it — the guard id appears in
/// `gk-core/scripts/verification-boundaries.v1.json`, which `verify-change.ps1` resolves the touched paths
/// through.</item>
/// </list>
///
/// <para>A `local`-tier guard can never run in CI (it needs a game install or interop assemblies),
/// so for those the implement-phase route is the ONLY honest one and this assertion requires it.</para>
/// </summary>
static class GuardWiring
{
    /// <summary>
    /// Enumerate files under <paramref name="root"/> without letting an unreadable directory redden a
    /// guard whose subject is source text. Measured 2026-09-21 at the post-merge head: two walks
    /// (<c>CiWiringGuardTests</c>, <c>CoreTestProjectPolicyTests</c>) threw
    /// <c>UnauthorizedAccessException: Access to the path 'tools/seedsmith/.tmp-seedsmith-pytest-audit'
    /// is denied</c> from <c>Directory.GetFiles(..., AllDirectories)</c>, so neither guard ever evaluated
    /// its rule. That directory is a stale gitignored pytest temp dir, not repo content. Build output
    /// (<c>bin</c>/<c>obj</c>), VCS/agent state and temp dirs are skipped by name; a directory that cannot
    /// be opened is skipped rather than thrown. Callers keep their own bin/obj filters — skipping here
    /// only means they see strictly fewer non-content paths.
    /// </summary>
    public static string[] SafeFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                found.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly));
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf is "bin" or "obj" or ".git" or ".claude" or "node_modules") continue;
                    if (leaf.StartsWith(".tmp-", StringComparison.Ordinal)) continue;
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { /* unreadable: not evidence about source text */ }
            catch (IOException) { /* vanished or locked mid-walk: same */ }
        }
        return found.ToArray();
    }

    /// <summary>
    /// Assert <paramref name="guardId"/> is reachable by the phase that owns it. Named for what it
    /// now checks, so a future reader is not misled by the old deploy-wiring name.
    /// </summary>
    public static void AssertGuardReachableInItsOwningPhase(string repoRoot, string guardId)
    {
        var registry = EnforcementRegistry.Load(repoRoot);
        Assert.True(registry.Guards.ContainsKey(guardId),
            $"guard '{guardId}' is not in scripts/enforcement-registry.v1.json");
        var row = registry.Guards[guardId];
        Assert.True(row.Tier is "ci" or "local",
            $"guard '{guardId}' has tier '{row.Tier}' — no runner tier selects it");
        Assert.Equal("gating", row.Status);

        var ciReaches = row.Tier == "ci" && CiRunsTheRunner(repoRoot);
        var verifyReaches = VerificationBoundariesReach(repoRoot, guardId);
        var deployPrecondition = DeployInvokesItAsAPrecondition(repoRoot, guardId);
        Assert.True(ciReaches || verifyReaches || deployPrecondition,
            $"guard '{guardId}' (tier {row.Tier}, status {row.Status}) is reachable by NO owning " +
            $"phase: ci={ciReaches}, verify-change={verifyReaches}, deploy-precondition=" +
            $"{deployPrecondition}. A ci-tier guard needs the CI runner; a local-tier guard (it needs " +
            "a game install or interop refs) can only be gated by the implement phase, so its id must " +
            "appear in verification-boundaries.v1.json. The ONE exception is a guard whose position " +
            "is part of its meaning (game-profile validates the install immediately before the " +
            "injector build), which the deploy invokes by id.");
    }

    /// <summary>
    /// A deploy may invoke a guard by id when running it IS a deploy precondition. Exactly one guard
    /// qualifies: <c>game-profile</c>, which validates that the chosen bridge matches the install
    /// immediately before the injector build consumes it (its position is part of its meaning).
    /// The deploy must never run a TIER BATCH — that is the conflation this split removed.
    /// </summary>
    static bool DeployInvokesItAsAPrecondition(string repoRoot, string guardId)
    {
        var deploy = Path.Combine(repoRoot, "scripts", "deploy-play.py");
        if (!File.Exists(deploy)) return false;
        var text = File.ReadAllText(deploy);
        return text.Contains($"-Only {guardId}", StringComparison.Ordinal);
    }
    /// <summary>The deploy must NOT run a guard SUITE (owner ruling 2026-09-26).
    ///
    /// <para>The banned shape is a TIER BATCH: `-Tier local` with no `-Only` selects every ci-tier
    /// guard plus the machine-only ones (28 guards, 203s), and `-IncludeBacklog` adds the report-only
    /// rows. The permitted call is the positioned precondition that names one id.</para>
    ///
    /// <para>The scan is over CODE, not prose: the module docstring explains the rule by quoting the
    /// banned flags, and a guard that fires on its own documentation is the `guard-funnel-delta`
    /// false positive this repo already paid for ("Documenting the rule must never look like
    /// breaking it"). So the docstring and `#` comment lines are dropped first — but string
    /// LITERALS are kept, because the precondition builds the runner path inside one.</para></summary>
    public static void AssertDeployRunsNoGuardSuite(string repoRoot)
    {
        var deploy = Path.Combine(repoRoot, "scripts", "deploy-play.py");
        Assert.True(File.Exists(deploy), "missing " + deploy);
        var code = StripModuleDocstringAndComments(File.ReadAllText(deploy));

        Assert.DoesNotContain("-IncludeBacklog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("--include-backlog", code, StringComparison.Ordinal);

        // The contract is a FILE-level one, and it is asserted as such rather than through a fixed-width
        // window around the runner's name.
        //
        // A window was the wrong mechanism and cost two false failures. The first version scanned for
        // the FILE name `run_guards`, but `tool_argv` is called BY STEM, so the call site contains
        // `"run-guards"` and the file name appears only in `TOOL_FILE_STEMS`'s value -- a needle that
        // cannot see the thing under test. The second scanned the stem and still failed, because the
        // Python dialect sits ~313 characters downstream of it and the window was 300. Both were
        // PROBE-BLIND: each could not see a deploy that spells the tier correctly. A fixed width over a
        // formatted call site is a bet on formatting, and it loses in the direction that looks like a
        // real defect.
        Assert.True(code.Contains("run-guards", StringComparison.Ordinal),
            "deploy-play.py never names the guard runner — the deploy would build a bridge into an " +
            "install without validating the install first");

        // The tier a caller names must be the one the deploy can actually satisfy: a `ci` run with no
        // --ci-range REFUSES, which is the defect that broke every deploy at HEAD.
        //
        // Asserted PER LINE, and the granularity is not incidental. The flags are separate argv
        // ELEMENTS, so the source spells them `"--tier", "local"` and a search for `--tier local` can
        // never match however correct the deploy is -- which is how this assertion failed twice while
        // the deploy was right. A line is the smallest span that holds the flag, its value and its
        // sibling, and it does not move when the call is re-wrapped.
        var tierLines = 0;
        foreach (var line in code.Split('\n'))
        {
            if (!line.Contains("--tier", StringComparison.Ordinal)) continue;
            tierLines++;
            Assert.True(line.Contains("\"local\"", StringComparison.Ordinal),
                "a runner tier is named without the value `local`, so the deploy asks a ci-tier runner " +
                $"for a run it cannot satisfy: {line.Trim()}");
            Assert.True(line.Contains("--only", StringComparison.Ordinal),
                "a runner tier is named without `--only` on the same call, so the positioned " +
                $"precondition may name no guard and the deploy runs a whole tier: {line.Trim()}");
        }
        Assert.True(tierLines >= 1,
            "deploy-play.py names the runner with no tier at all, so it would ask a ci-tier runner " +
            "for a run it cannot satisfy");
    }

    /// <summary>
    /// Drop the module docstring and `#` comment lines, keep everything else (including string
    /// literals, which is where the runner invocation is written). A comment is not code; a string
    /// a script passes to a child process is.
    /// </summary>
    static string StripModuleDocstringAndComments(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var inDocstring = false;
        var docstringDone = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!docstringDone)
            {
                if (!inDocstring && trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    var quotes = trimmed.Split("\"\"\"").Length - 1;
                    if (quotes >= 2) { docstringDone = true; continue; } // one-line docstring
                    inDocstring = true;
                    continue;
                }
                if (inDocstring)
                {
                    if (trimmed.EndsWith("\"\"\"", StringComparison.Ordinal)) docstringDone = true;
                    continue;
                }
                // A module-level comment before the docstring is prose too; skip it like any other.
                if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.Length == 0) continue;
                docstringDone = true; // no docstring: everything from here is code
            }
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    static bool CiRunsTheRunner(string repoRoot)
    {
        var ci = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        if (!File.Exists(ci)) return false;
        // Separator-normalised: ci.yml spells paths with BACKslashes, so a forward-slash search here
        // would answer false for every ci.yml in the repository and quietly exempt every local-tier
        // guard from the "CI reaches it" half of this contract.
        var text = File.ReadAllText(ci).Replace('\\', '/');
        return text.Contains("run_guards.py --tier ci", StringComparison.Ordinal);
    }

    /// <summary>
    /// Does `gk-core/scripts/verification-boundaries.v1.json` name this guard id anywhere? That file is what
    /// `verify-change.ps1` resolves a touched path's checks through, so a named id means the
    /// implement-phase gate can reach the guard. Read as data (not text) so a comment cannot pass it.
    /// </summary>
    static bool VerificationBoundariesReach(string repoRoot, string guardId)
    {
        var path = Path.Combine(repoRoot, "scripts", "verification-boundaries.v1.json");
        if (!File.Exists(path)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return NamesIt(doc.RootElement, guardId);
    }

    static bool NamesIt(JsonElement element, string guardId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(element.GetString(), guardId, StringComparison.Ordinal);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (NamesIt(item, guardId)) return true;
                return false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (NamesIt(property.Value, guardId)) return true;
                return false;
            default:
                return false;
        }
    }
}
