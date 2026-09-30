using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The enforcement registry's contract (solid-enforcement <c>enforcement-registry</c>): every rule has
/// one row saying what enforces it, where it runs and — when it does not gate — which module fixes
/// that. R1–R8 assert the contract on the real tree; each has a falsifier that feeds a deliberately
/// broken registry built in memory (never written to disk) and expects the same rule to fail. A
/// meta-test that has never been seen failing proves nothing.
///
/// <para><b>R7 is gone</b> (solid-enforcement SE0.7): it policed agreement with a duplicate guard map in
/// <c>verification-boundaries.v1.json</c>, and that map no longer exists — guard ids resolve through
/// this catalog alone, and the verification registry's integrity guard requires the map's absence.</para>
///
/// <para>What is intentionally NOT asserted: the number of guards or invariant rows. Both are
/// populations that grow as the program runs (<c>validation-ssot.md</c>). Only the two closed
/// vocabularies have their member sets pinned, with the reason beside the pin.</para>
/// </summary>
[Trait("VerificationId", "guard.enforcement-registry")]
public sealed class EnforcementRegistryGuardTests
{
    // A third tier or status is a reviewed change to the policy, not data (spec §Boundaries).
    static readonly string[] Tiers = { "ci", "local" };
    static readonly string[] Statuses = { "gating", "backlog" };

    // R4's extension (`wire-green-guards` §What "local" means): `local` has exactly ONE legal
    // reason — the guard needs the game install, which the "never download or patch the PVZ Fusion
    // game binary" hard boundary keeps off a CI runner. The reason must therefore name a way to
    // point the guard at that install: the switch/env var it actually reads (`guard-game-profile.py`
    // -GameDir, `guard-injector-compile.py` FUSIONRPG_ML_GAMEDIR / FUSIONRPG_GAME_DIR) or the words
    // "game install". Without this, "local" quietly becomes "too slow for CI".
    static readonly string[] GameInstallTokens =
        { "-GameDir", "FUSIONRPG_GAME_DIR", "FUSIONRPG_ML_GAMEDIR", "game install" };

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string Join(IReadOnlyList<string> violations) => string.Join("\n  ", violations);

    // ── R1 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every guard script on disk, BOTH extensions, repo-relative and ordered.
    ///
    /// One definition, used by the R1 invariant and by the test that pins the glob. That is the
    /// point: when the glob lived inline in R1, a test asserting "both extensions are present"
    /// would do its own globbing and stay green while R1 went blind again - the falsifier
    /// testing a copy of the thing rather than the thing.
    ///
    /// This glob used to be `guard-*.ps1` alone, which made the "every guard on disk is
    /// catalogued" half of R1 blind to every ported guard the moment a port landed: the test
    /// kept passing while checking strictly fewer files. That is the silent-green shape - a green
    /// invariant that has quietly stopped covering the thing it names. A guard is a guard
    /// whichever interpreter runs it.
    /// </summary>
    internal static string[] DiskGuards(string root)
    {
        var dir = Path.Combine(root, "scripts");
        return Directory.GetFiles(dir, "guard-*.ps1")
            .Concat(Directory.GetFiles(dir, "guard-*.py"))
            .Select(p => "scripts/" + Path.GetFileName(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void R1_every_guard_on_disk_is_catalogued_and_every_entry_script_exists()
    {
        var root = RepoRoot();
        var reg = EnforcementRegistry.Load(root);
        var violations = R1Violations(reg, DiskGuards(root), script => File.Exists(Path.Combine(root, script)));
        Assert.True(violations.Count == 0, "R1: " + Join(violations));
    }

    [Fact]
    public void R1_an_uncatalogued_ported_python_guard_is_reported()
    {
        // The falsifier for the function: a registry naming one guard while the disk side names
        // two must report the extra one, whatever extension it carries.
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.py","tier":"ci","status":"gating"}},"invariants":[]}""");
        var violations = R1Violations(reg, new[] { "scripts/guard-x.py", "scripts/guard-uncatalogued.py" }, _ => true);
        Assert.Single(violations);
        Assert.Contains("guard-uncatalogued.py", violations[0]);
    }

    [Fact]
    public void R1_the_disk_glob_sees_both_extensions_and_R1_uses_this_glob()
    {
        // Asserted as "the GLOB SEES both extensions", never as "both extensions are PRESENT on the
        // real tree", and never as a count. The original proved it by finding a `.ps1` guard and a `.py`
        // guard in `scripts/` — which was a POPULATION assertion, the thing this file's own header
        // says it does not do, and it went red the moment the last `scripts/guard-*.ps1` was retired
        // (2026-09-28, the guard-sim-fabrication port). A test that can only pass while a population
        // exists is a test that guards nothing once the program finishes.
        //
        // So the proof is asked of the GLOB instead of the tree: `DiskGuards` takes a root, so a
        // synthetic tree carrying one guard of each extension answers the question directly and can
        // never go vacuous. The real tree is still enumerated on the same line, because R1's actual
        // input is the real tree and narrowing the glob must show up here.
        var real = DiskGuards(RepoRoot());
        Assert.NotEmpty(real);
        Assert.Contains(real, p => p.EndsWith(".py", StringComparison.Ordinal));

        using var probe = new TempTree();
        probe.Write("scripts/guard-probe.ps1", "# a guard in the other shape\n");
        probe.Write("scripts/guard-probe.py", "# a guard in this shape\n");
        var synthetic = DiskGuards(probe.Root);
        Assert.Contains(synthetic, p => p.EndsWith(".ps1", StringComparison.Ordinal));
        Assert.Contains(synthetic, p => p.EndsWith(".py", StringComparison.Ordinal));
    }

    /// <summary>A throwaway directory that is always removed; a failed delete throws, never swallowed
    /// (docs/contributing/testing-standard.md).</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "EnforcementRegistryGuard_" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Path.Combine(Root, "scripts"));

        public void Write(string relative, string content) =>
            File.WriteAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), content);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void R1_falsifier_uncatalogued_guard_and_missing_script_are_reported()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"ci","status":"gating"}},"invariants":[]}""");
        var violations = R1Violations(reg, new[] { "scripts/guard-x.ps1", "scripts/guard-unregistered.ps1" }, _ => false);
        Assert.Equal(2, violations.Count);
    }

    // ── R2 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R2_tier_and_status_are_closed_vocabularies()
    {
        var reg = EnforcementRegistry.Load(RepoRoot());
        var violations = R2Violations(reg);
        Assert.True(violations.Count == 0, "R2: " + Join(violations));
        // The pin, with its reason: two tiers and two statuses, each a reviewed change to add.
        Assert.Equal(2, Tiers.Length);
        Assert.Equal(2, Statuses.Length);
    }

    [Fact]
    public void R2_falsifier_a_third_status_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"ci","status":"advisory"}},"invariants":[]}""");
        Assert.Single(R2Violations(reg));
    }

    // ── R3 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R3_backlog_guard_names_a_module_in_the_map()
    {
        var root = RepoRoot();
        var reg = EnforcementRegistry.Load(root);
        var violations = R3Violations(reg, EnforcementMap.ModuleIds(root));
        Assert.True(violations.Count == 0, "R3: " + Join(violations));
    }

    [Fact]
    public void R3_falsifier_a_backlog_guard_with_no_owning_module_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"ci","status":"backlog","backlogModule":"not-a-module"}},"invariants":[]}""");
        Assert.Single(R3Violations(reg, new HashSet<string>(new[] { "guard-runner" }, StringComparer.Ordinal)));
    }

    // ── R4 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R4_a_local_guard_states_why_it_cannot_run_in_ci()
    {
        var reg = EnforcementRegistry.Load(RepoRoot());
        var violations = R4Violations(reg);
        Assert.True(violations.Count == 0, "R4: " + Join(violations));
    }

    [Fact]
    public void R4_falsifier_a_local_guard_with_no_reason_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"local","status":"gating","localReason":"  "}},"invariants":[]}""");
        Assert.Single(R4Violations(reg));
    }

    [Fact]
    public void R4_falsifier_a_local_reason_that_names_no_game_install_is_refused()
    {
        // "too slow for CI" is the dumping-ground reason the extension exists to refuse: it reads as a
        // valid sentence, and could hide any guard a runner happens to dislike.
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"local","status":"gating","localReason":"too slow for CI"}},"invariants":[]}""");
        Assert.Single(R4Violations(reg));
    }

    // ── R5 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R5_a_gating_ci_guard_is_actually_run()
    {
        var root = RepoRoot();
        var reg = EnforcementRegistry.Load(root);
        var runner = Path.Combine(root, "scripts", "run_guards.py");
        var violations = R5Violations(
            reg,
            File.Exists(runner) ? File.ReadAllText(runner) : null,
            File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")));
        Assert.True(violations.Count == 0, "R5: " + Join(violations));
    }

    [Fact]
    public void R5_falsifier_a_gating_ci_guard_absent_from_ci_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"x":{"script":"scripts/guard-x.ps1","tier":"ci","status":"gating"}},"invariants":[]}""");
        Assert.Single(R5Violations(reg, null, "          .\\scripts\\guard-something-else.ps1\n"));
    }

    [Fact]
    public void R5_falsifier_a_guard_wired_by_hand_beside_the_runner_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"dal":{"script":"scripts/guard-dal.ps1","tier":"ci","status":"gating"}},"invariants":[]}""");
        const string ci = "          python .\\scripts\\run_guards.py --tier ci\n          python .\\scripts\\guard-dal.py\n";

        Assert.Single(R5Violations(reg, "reads enforcement-registry.v1.json", ci));
    }

    [Fact]
    public void R5_falsifier_a_ci_that_never_calls_the_runner_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""{"schemaVersion":1,"guards":{"dal":{"script":"scripts/guard-dal.ps1","tier":"ci","status":"gating"}},"invariants":[]}""");

        Assert.Single(R5Violations(reg, "reads enforcement-registry.v1.json", "name: CI\n"));
    }

    // ── R6 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R6_every_invariant_is_guarded_xor_carries_a_reason()
    {
        var reg = EnforcementRegistry.Load(RepoRoot());
        var violations = R6Violations(reg);
        Assert.True(violations.Count == 0, "R6: " + Join(violations));
    }

    [Fact]
    public void R6_falsifier_a_row_with_nothing_and_a_row_with_both_are_refused()
    {
        var reg = EnforcementRegistry.FromJson("""
            {"schemaVersion":1,"guards":{},
             "invariants":[
               {"id":"neither","source":"x","guards":[],"unguardableReason":null},
               {"id":"both","source":"x","guards":["g"],"unguardableReason":"why"}]}
            """);
        Assert.Equal(2, R6Violations(reg).Count);
    }

    // ── R8 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R8_every_catalog_guard_is_named_by_an_invariant()
    {
        var reg = EnforcementRegistry.Load(RepoRoot());
        var violations = R8Violations(reg);
        Assert.True(violations.Count == 0, "R8: " + Join(violations));
    }

    [Fact]
    public void R8_falsifier_a_guard_no_invariant_names_is_refused()
    {
        var reg = EnforcementRegistry.FromJson("""
            {"schemaVersion":1,
             "guards":{"orphan":{"script":"scripts/guard-orphan.ps1","tier":"ci","status":"gating"}},
             "invariants":[{"id":"i","source":"x","guards":[],"unguardableReason":"why"}]}
            """);
        Assert.Single(R8Violations(reg));
    }

    [Fact]
    public void ModuleIds_fails_loudly_when_the_map_carries_no_module_table()
    {
        var root = Path.Combine(Path.GetTempPath(), "enforcement-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "architecture"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, "docs", "architecture", "solid-enforcement-map.md"),
                "# Map\n\n## Something else\n\nno module table here\n");

            var ex = Assert.Throws<InvalidOperationException>(() => EnforcementMap.ModuleIds(root));
            Assert.Contains("## Modules", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── the rules, as pure functions over the registry ──────────────────────────────────────────

    static IReadOnlyList<string> R1Violations(EnforcementRegistry reg, IReadOnlyList<string> diskGuards, Func<string, bool> scriptExists)
    {
        var violations = new List<string>();
        var catalogScripts = reg.Guards.Values.Select(g => g.Script).ToHashSet(StringComparer.Ordinal);
        foreach (var script in diskGuards)
            if (!catalogScripts.Contains(script))
                violations.Add($"guard on disk not catalogued: {script}");
        foreach (var (id, guard) in reg.Guards)
            if (!scriptExists(guard.Script))
                violations.Add($"catalog script missing: {id} -> {guard.Script}");
        return violations;
    }

    static IReadOnlyList<string> R2Violations(EnforcementRegistry reg)
    {
        var violations = new List<string>();
        foreach (var (id, guard) in reg.Guards)
        {
            if (!Tiers.Contains(guard.Tier, StringComparer.Ordinal)) violations.Add($"{id}: tier '{guard.Tier}'");
            if (!Statuses.Contains(guard.Status, StringComparer.Ordinal)) violations.Add($"{id}: status '{guard.Status}'");
        }
        return violations;
    }

    static IReadOnlyList<string> R3Violations(EnforcementRegistry reg, IReadOnlySet<string> moduleIds)
    {
        var violations = new List<string>();
        foreach (var (id, guard) in reg.Guards)
            if (guard.Status == "backlog" && !moduleIds.Contains(guard.BacklogModule ?? ""))
                violations.Add($"{id}: backlogModule '{guard.BacklogModule}' is not a module in the map");
        return violations;
    }

    static IReadOnlyList<string> R4Violations(EnforcementRegistry reg)
    {
        var violations = new List<string>();
        foreach (var (id, guard) in reg.Guards)
        {
            if (guard.Tier != "local") continue;
            if (string.IsNullOrWhiteSpace(guard.LocalReason))
            {
                violations.Add($"{id}: tier local with no localReason");
                continue;
            }
            if (!GameInstallTokens.Any(t => guard.LocalReason.Contains(t, StringComparison.OrdinalIgnoreCase)))
                violations.Add($"{id}: tier local but the reason names no game install: '{guard.LocalReason}'");
        }
        return violations;
    }

    static IReadOnlyList<string> R5Violations(EnforcementRegistry reg, string? runnerText, string ciText)
    {
        // The registry writes repo-relative paths with forward slashes; ci.yml invokes guards and the
        // runner as `.\scripts\...`. Compare after normalising separators (same rule verify-change uses).
        var ci = ciText.Replace('\\', '/');
        var violations = new List<string>();

        if (runnerText is null)
        {
            // Transitional form (no runner yet): CI must call each gating guard directly.
            foreach (var (id, guard) in reg.Guards)
            {
                if (guard.Tier != "ci" || guard.Status != "gating") continue;
                if (!ci.Contains(guard.Script, StringComparison.Ordinal))
                    violations.Add($"{id}: tier ci + status gating but {guard.Script} is not run by ci.yml");
            }
            return violations;
        }

        // Post-runner form: CI invokes the runner, and no guard is wired by hand beside it. The only
        // exception is a row marked `ciEntry: own-step`, which ci.yml runs in its own isolated step.
        if (!runnerText.Replace('\\', '/').Contains("enforcement-registry.v1.json", StringComparison.Ordinal))
            violations.Add("run_guards.py does not read the enforcement registry");
        // ci.yml spells script paths with BACKslashes, so a forward-slash search could never match and
        // this check could never fire. Normalising first is what makes it a check; the same reason
        // applies to the hand-wired-guard scan below, which matched ZERO lines before.
        var ciSpelling = ci.Replace('\\', '/');
        if (!ciSpelling.Contains("scripts/run_guards.py", StringComparison.Ordinal))
            violations.Add("ci.yml does not invoke scripts/run_guards.py --tier ci");

        var ownStep = reg.Guards.Where(g => g.Value.CiEntry == "own-step")
            .Select(g => g.Value.Script).ToHashSet(StringComparer.Ordinal);
        foreach (var line in ciSpelling.Split('\n'))
        {
            // BOTH extensions, on the normalised text. `.ps1`-only matched nothing at all once the
            // guards were ported, and separator-normalised it would have matched every `.py` guard the
            // registry names -- including the two legitimate `own-step` rows the exception allows.
            foreach (Match match in Regex.Matches(line, @"scripts/guard-[a-z0-9-]+\.(ps1|py)"))
                if (!ownStep.Contains(match.Value))
                    violations.Add($"ci.yml hand-wires a guard beside the runner: {match.Value}");
        }
        return violations;
    }

    static IReadOnlyList<string> R6Violations(EnforcementRegistry reg)
    {
        var violations = new List<string>();
        foreach (var invariant in reg.Invariants)
        {
            var guarded = invariant.Guards.Count > 0;
            var reasoned = !string.IsNullOrWhiteSpace(invariant.UnguardableReason);
            if (guarded == reasoned)
                violations.Add($"{invariant.Id}: must have guard ids XOR an unguardableReason");
        }
        return violations;
    }

    static IReadOnlyList<string> R8Violations(EnforcementRegistry reg)
    {
        var named = reg.Invariants.SelectMany(i => i.Guards).ToHashSet(StringComparer.Ordinal);
        return reg.Guards.Keys.Where(id => !named.Contains(id))
            .Select(id => $"{id}: named by no invariant row").ToList();
    }
}
