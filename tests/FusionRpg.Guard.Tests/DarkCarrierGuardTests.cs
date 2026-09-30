using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `solid-remediation` 2026-09-17 — the COVER step for a register entry that cannot be BUILT yet.
///
/// <para><b>What this guards, and why a register row is not enough.</b> `SR-18` records that
/// <c>creditEmpire</c> is resolved at every mode's die event and read by nothing in production. That
/// claim was true when it was written, and a claim in a markdown table has no way of staying true. This
/// repo has now been bitten three times in one day by exactly that: `SR-17`'s blocker, the
/// `ProvePredictor` failure message, and the `solid-remediation-map` narrative were each written down
/// accurately and each went stale against the code. So the claim is asserted instead of narrated.</para>
///
/// <para><b>This test is a CANARY and is SUPPOSED to fail the day the debt is paid.</b> If someone wires
/// a real consumer for <c>creditEmpire</c>, this goes red. That is the success case, not a regression:
/// the correct response is to close `SR-18` in
/// <c>docs/architecture/stub-register.md</c>, tick CP4's *"progression credited to whoever earned it"*
/// clause if the consumer satisfies it, and delete this file. **Do not** "fix" it by adding the new
/// reader to the allowlist below — that would preserve the test and discard the point of it.</para>
///
/// <para><b>Scope, deliberately narrow.</b> Production source only (<c>src/**</c>). Tests, docs, task
/// files and build output are excluded: a test asserting the field is emitted is not a consumer of it,
/// and the register rows and todo lines that discuss the gap obviously mention the name.</para>
///
/// <para><b>An `SR-17` canary lived here too and was deleted 2026-09-17 when that debt was paid</b> —
/// the owner ruled on lawn permadeath, the tuned <c>LawnPermadeathLadder</c> is wired, and a canary
/// whose debt is settled is noise. It is worth recording HOW it ended, because it did not end
/// cleanly: it searched for the literal <c>new LawnPermadeathLadder</c>, the production call site was
/// written fully qualified as <c>new FusionRpg.Core.Battle.Attrition.LawnPermadeathLadder(</c>, and
/// the guard therefore went on passing while the thing it guarded had already changed. A false
/// NEGATIVE, found only because the wiring was done deliberately and the guard was checked against a
/// known answer. **A source-text guard matching an unqualified C# name is unsound** — the same string
/// has several legal spellings. The surviving check below matches <c>creditEmpire</c>, a JSON key that
/// has exactly one spelling, which is why it does not share the flaw.</para>
/// </summary>
public class DarkCarrierGuardTests
{
    /// <summary>The one production site allowed to mention the field: the emitter that writes it.</summary>
    const string Emitter = "src/FusionRpg.Core/Battle/BattleReportEmitter.cs";

    const string Field = "creditEmpire";

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static IEnumerable<string> ProductionSources(string root)
    {
        var src = Path.Combine(root, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.Contains("/bin/", StringComparison.Ordinal)
                    && !rel.Contains("/obj/", StringComparison.Ordinal);
            });
    }

    [Fact]
    public void creditEmpire_still_has_exactly_one_production_site_and_it_is_the_emitter()
    {
        var root = RepoRoot();
        var mentions = ProductionSources(root)
            .Where(f => File.ReadAllText(f).Contains(Field, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { Emitter }, mentions);
    }

    /// <summary>The falsifier for the tests above: if the scan were broken — wrong root, wrong filter,
    /// reading nothing — it would report zero mentions and both claims would pass vacuously for the
    /// wrong reason. This proves the scan actually reaches real files and really matches.</summary>
    [Fact]
    public void The_scan_is_not_vacuous()
    {
        var root = RepoRoot();
        var files = ProductionSources(root).ToList();

        Assert.True(files.Count > 100,
            $"the production source scan found only {files.Count} files — it is not reaching src/**, so "
            + "the guard above would pass without proving anything");

        var emitter = Path.Combine(root, Emitter);
        Assert.True(File.Exists(emitter), $"{Emitter} has moved — update this guard and SR-18's row");
        Assert.Contains(Field, File.ReadAllText(emitter), StringComparison.Ordinal);
    }
}
