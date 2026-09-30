using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// identity-rename T4: <c>gk-core/scripts/vocab-rename.py</c> and its rules
/// (<c>gk-core/scripts/vocab-rename/identity-rename.v1.json</c>). Bulk prose renames go through that tool
/// (plan D3) — a deterministic, dry-run-by-default pass — and an agent closes its residue by adding a
/// rule, never by editing its output.
///
/// <para><b>Runs in memory.</b> A Python harness loads the tool through
/// <c>importlib.util.spec_from_file_location</c> and replaces its five IO seams
/// (<c>repo_root</c>, <c>tracked_files</c>, <c>read_text</c>, <c>write_text</c>, <c>dirty_files</c>) with
/// one in-memory tree, so every case here — including "apply writes exactly once" and "apply refuses a
/// dirty file" — is exercised without touching the disk (the <c>DocCitationAuditTests</c> precedent).</para>
///
/// <para>What it pins: the identifier allow-list and the protected spans (a code identifier or a link
/// target is never prose), the grammar rules (sentence-start capital, preserved possessive, doubled
/// article to residue), the Han boundary, and the four behaviour contracts (plan writes nothing, plan
/// is byte-identical twice, apply is idempotent, check exits 1 while a rule matches).</para>
/// </summary>
[Trait("VerificationId", "guard.vocab-rename")]
public sealed class VocabRenameTests
{
    const string Harness = """
import importlib.util, io, json, subprocess, sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("vr", "scripts/vocab-rename.py")
vr = importlib.util.module_from_spec(spec)
sys.modules["vr"] = vr
spec.loader.exec_module(vr)

request = json.loads(sys.stdin.buffer.read().decode("utf-8"))
rules = json.loads(Path(request["rules"]).read_text(encoding="utf-8"))

store = dict(request["files"])
writes = []
dirty = set(request.get("dirty", []))

# The five IO seams, replaced with one in-memory tree. The rules file is the one real read.
vr.repo_root = lambda: ""
vr.tracked_files = lambda: sorted(store)
vr.read_text = lambda rel: store[rel] if rel in store else Path(rel).read_text(encoding="utf-8")
vr.write_text = lambda rel, text: (store.__setitem__(rel, text), writes.append(rel))
vr.dirty_files = lambda rels: {rel for rel in rels if rel in dirty}

phase = request["phase"]
action = request["action"]
report = {"action": action, "writes": writes}


def plan():
    return vr.build_plan(dict(store), rules, phase)


def rows(p):
    return {"replacements": vr.replacements_of(p), "residue": vr.residue_of(p),
            "identifiers": vr.identifiers_of(p)}


def cli(*argv):
    # The real CLI, with stdout captured so the harness own stream stays pure JSON.
    buf = io.StringIO()
    saved, sys.stdout = sys.stdout, buf
    try:
        code = vr.main(list(argv))
    finally:
        sys.stdout = saved
    return code, buf.getvalue()


if action == "scan":
    p = plan()
    report["after"] = p["changed"]
    report.update(rows(p))
elif action == "paths":
    # The derived scope needs the real tracked set (the generated pages come from real content files),
    # while the in-memory store stays the source of truth for every mutation action.
    vr.tracked_files = lambda: sorted(subprocess.run(
        ["git", "ls-files"], capture_output=True, text=True, check=True).stdout.split())
    code, out = cli("plan", "--rules", request["rules"], "--phase", phase,
                   "--paths", *request["paths"])
    report["exit"] = code
    if code == 0:
        parsed = json.loads(out)
        report["replacements"] = parsed["replacements"]
        report["residue"] = parsed["residue"]
elif action == "scope":
    vr.tracked_files = lambda: sorted(subprocess.run(
        ["git", "ls-files"], capture_output=True, text=True, check=True).stdout.split())
    phase_rules = next(p for p in rules["phases"] if p["id"] == phase)
    include, exclude = phase_rules["include"], phase_rules.get("exclude", [])
    rendered = vr.rendered_outputs(phase_rules)
    report["inScope"] = {p: (vr.in_scope(p, include, exclude) and p not in rendered)
                        for p in request["paths"]}
elif action == "plan":
    _, first = cli("plan", "--rules", request["rules"], "--phase", phase)
    _, second = cli("plan", "--rules", request["rules"], "--phase", phase)
    p = plan()
    report["planBytes"] = first
    report["secondPlanBytes"] = second
    report.update(rows(p))
    report["storeUnchanged"] = store == request["files"] and not writes
elif action == "check":
    code, _ = cli("check", "--rules", request["rules"], "--phase", phase)
    report["exit"] = code
    report.update(rows(plan()))
elif action == "apply":
    runs = []
    for _ in range(2):
        code, out = cli("apply", "--rules", request["rules"], "--phase", phase)
        runs.append({"exit": code, "store": dict(store), "writes": list(writes), "stdout": out})
    report["runs"] = runs
else:
    raise SystemExit("unknown action " + action)

sys.stdout.buffer.write(json.dumps(report, sort_keys=True).encode("utf-8"))
""";

    const string Rules = "scripts/vocab-rename/identity-rename.v1.json";

    // ---------------------------------------------------------------- plumbing

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    sealed record Result(JsonElement Root)
    {
        public string After(string rel) =>
            Root.GetProperty("after").TryGetProperty(rel, out var text) ? text.GetString()! : "";

        public JsonElement Findings(string section) => Root.GetProperty(section);

        public bool Has(string section, string rule, string? reason = null)
        {
            foreach (var row in Findings(section).EnumerateArray())
            {
                if (row.GetProperty("rule").GetString() != rule) continue;
                if (reason is null) return true;
                if (row.TryGetProperty("reason", out var r) && r.GetString() == reason) return true;
            }

            return false;
        }

        public string[] ChangedFiles() =>
            Array.ConvertAll(Root.GetProperty("after").EnumerateObject().ToArray(), p => p.Name);

        public string[] Written() =>
            Array.ConvertAll(Root.GetProperty("writes").EnumerateArray().ToArray(), e => e.GetString()!);

        public JsonElement Run(int index) => Root.GetProperty("runs")[index];

        public string RunStore(int index, string rel) => Run(index).GetProperty("store").GetProperty(rel).GetString()!;

        public int RunWrites(int index) => Run(index).GetProperty("writes").GetArrayLength();

        public bool InScope(string rel) => Root.GetProperty("inScope").GetProperty(rel).GetBoolean();
    }

    static Result Call(string action, string phase, Dictionary<string, string> files,
        string[]? dirty = null, string rules = Rules, string[]? paths = null)
    {
        var request = JsonSerializer.Serialize(new
        {
            action,
            phase,
            rules,
            files,
            paths = paths ?? Array.Empty<string>(),
            dirty = dirty ?? Array.Empty<string>()
        });

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = RepoRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Harness);

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(request);
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(120_000), "vocab-rename harness timed out");
        var stdout = stdoutTask.Result;
        Assert.True(p.ExitCode == 0, $"vocab-rename harness failed exit={p.ExitCode}\n{stdout}\n{stderrTask.Result}");

        using var doc = JsonDocument.Parse(stdout);
        return new Result(doc.RootElement.Clone());
    }

    static Result Plan(string phase, string text, string rel = "docs/guide/x.md") =>
        Call("scan", phase, new Dictionary<string, string> { [rel] = text });

    static Result PlanFusion(string text, string rel = "docs/guide/x.md") => Plan("fusion", text, rel);

    const string NamesControl = "Rise of Summoner";
    const string NamesControlAfter = "Garden Keeper and his Multiverse";

    // ---------------------------------------------------------------- identifiers

    public static IEnumerable<object[]> IdentifierFixtures()
    {
        // Names phase: each of these sits next to a renamable phrase, so a pass that rewrites the
        // neighbour and leaves the identifier alone proves the allow-list actually fired.
        yield return new object[] { "names", "EmpireId.Dave", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "WorldFactionKind.Zomboss", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "ZombossDeployEndpoints", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "commander:dave", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "first-win-dave", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "actor-dave", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "drop.pvz.run", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "pvz.*", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "PVZRH", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "pvzrh-3.9", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "[x](mechanisms/dave-level.md)", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "`Dave`", NamesControl, NamesControlAfter };
        yield return new object[] { "names", "<a href=\"Dave.html\">the page</a>", NamesControl, NamesControlAfter };
        // Fusion phase: the PvZ rule is the one these must survive.
        yield return new object[] { "fusion", "PvZ2", "PvZ Fusion", "Fusion" };
        yield return new object[] { "fusion", "PvZ Heroes", "PvZ Fusion", "Fusion" };
        yield return new object[] { "fusion", "Garden Warfare", "PvZ Fusion", "Fusion" };
        yield return new object[] { "fusion", "PVZRH", "PvZ Fusion", "Fusion" };
        yield return new object[] { "fusion", "drop.pvz.run", "PvZ Fusion", "Fusion" };
    }

    [Theory]
    [MemberData(nameof(IdentifierFixtures))]
    public void An_identifier_survives_a_pass_that_rewrites_its_neighbour(
        string phase, string identifier, string control, string controlAfter)
    {
        var result = Plan(phase, $"The {identifier} guide, per {control}.\n");
        var after = result.After("docs/guide/x.md");

        Assert.Contains(identifier, after, StringComparison.Ordinal);
        Assert.Contains(controlAfter, after, StringComparison.Ordinal);
        Assert.DoesNotContain(control, after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fenced_block_is_never_rewritten_while_the_prose_around_it_is()
    {
        var text = "Zomboss waits.\n\n```\nZomboss in a fence, Crazy Dave too\n```\n\nZomboss again.\n";
        var after = Plan("names", text).After("docs/guide/x.md");

        Assert.Contains("Zomboss in a fence, Crazy Dave too", after, StringComparison.Ordinal);
        Assert.Contains("The Rotwright waits.", after, StringComparison.Ordinal);
        Assert.Contains("The Rotwright again.", after, StringComparison.Ordinal);

        // The same for a JSON surface: a machine-field value is not prose.
        var json = "{\n  \"slug\": \"Rise of Summoner\",\n  \"title\": \"Rise of Summoner\",\n"
            + "  \"related\": [\"Rise of Summoner\"],\n  \"pillar\": \"Penny\"\n}\n";
        var afterJson = Plan("names", json, "docs/guide/mechanisms/_content/x.json")
            .After("docs/guide/mechanisms/_content/x.json");
        Assert.Contains("\"slug\": \"Rise of Summoner\"", afterJson, StringComparison.Ordinal);
        Assert.Contains("\"related\": [\"Rise of Summoner\"]", afterJson, StringComparison.Ordinal);
        Assert.Contains("\"pillar\": \"Penny\"", afterJson, StringComparison.Ordinal);
        Assert.Contains($"\"title\": \"{NamesControlAfter}\"", afterJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- grammar + boundary

    [Fact]
    public void APvZ_suffix_in_Han_script_matches_where_a_word_boundary_would_not()
    {
        // `\b` fails here: both `Z` and a Han character count as word characters. The tool's named
        // boundary policy (a non-identifier neighbour) is what makes this replace (plan D3).
        var after = PlanFusion("The PvZ融合版 page.\n").After("docs/guide/x.md");

        Assert.Equal("The Fusion融合版 page.\n", after);
    }

    [Fact]
    public void A_prose_compound_goes_to_residue_rather_than_being_skipped()
    {
        // `Dave-level` is closed by a phrase rule as of T7, so the residue CONTRACT is pinned with a
        // compound no rule covers. What it asserts is "never silently skipped", not "Dave-level
        // always residues" — the phrase rule that closes a compound is the other half, below.
        var result = Plan("names", "Open the Dave-metrics page.\n");

        Assert.True(result.Has("residue", "dave", "prose-compound"),
            "a prose compound no rule covers must be reported as residue, never silently skipped");
        Assert.False(result.Has("replacements", "dave"));
        Assert.Empty(result.ChangedFiles());
    }

    [Fact]
    public void A_phrase_rule_closes_a_compound_the_generic_rule_refuses()
    {
        // the other half of the pair: the way an agent closes residue is by ADDING A RULE (plan D3),
        // never by editing the tool's output. `dave-level-compound` is that rule.
        var after = Plan("names", "Open the Dave-level page, per Rise of Summoner.\n")
            .After("docs/guide/x.md");

        Assert.Contains("Keeper-level", after, StringComparison.Ordinal);
        Assert.Contains(NamesControlAfter, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Dave", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Crazy Dave waves.\n", "The Garden Keeper waves.\n")]
    [InlineData("Then Crazy Dave waves.\n", "Then the Garden Keeper waves.\n")]
    [InlineData("Destroy Zomboss’s lair.\n", "Destroy the Rotwright’s lair.\n")]
    public void A_replacement_takes_the_grammar_the_sentence_needs(string before, string after)
    {
        Assert.Equal(after, Plan("names", before).After("docs/guide/x.md"));
    }

    [Fact]
    public void ADoubled_article_is_residue_not_a_replacement()
    {
        var result = Plan("names", "Then the Zomboss retreats.\n");

        Assert.True(result.Has("residue", "zomboss", "doubled-article"));
        Assert.False(result.Has("replacements", "zomboss"));
        Assert.Empty(result.ChangedFiles());
    }

    [Fact]
    public void APvZ_Fusion_phrase_becomes_one_Fusion_and_never_Fusion_Fusion()
    {
        var after = PlanFusion("Enter the PvZ Fusion lab, then the Fusion lab again.\n")
            .After("docs/guide/x.md");

        Assert.Equal("Enter the Fusion lab, then the Fusion lab again.\n", after);
        Assert.DoesNotContain("Fusion Fusion", after, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- behaviour contracts

    [Fact]
    public void Plan_reports_the_replacements_and_writes_nothing()
    {
        var result = Call("plan", "names", new Dictionary<string, string>
        {
            ["docs/guide/x.md"] = "Rise of Summoner is here.\n"
        });

        Assert.True(result.Has("replacements", "title"));
        Assert.Empty(result.Written());
        Assert.True(result.Root.GetProperty("storeUnchanged").GetBoolean());
    }

    [Fact]
    public void Two_plan_runs_are_byte_identical()
    {
        var result = Call("plan", "names", new Dictionary<string, string>
        {
            ["docs/guide/x.md"] = "Rise of Summoner: Crazy Dave, Dr. Zomboss and Penny.\n"
        });

        Assert.Equal(result.Root.GetProperty("planBytes").GetString(),
            result.Root.GetProperty("secondPlanBytes").GetString());
    }

    [Fact]
    public void Apply_twice_equals_apply_once()
    {
        var text = "Rise of Summoner: Crazy Dave and Penny.\n";
        var result = Call("apply", "names", new Dictionary<string, string> { ["docs/guide/x.md"] = text });

        Assert.Equal(result.RunStore(0, "docs/guide/x.md"), result.RunStore(1, "docs/guide/x.md"));
        Assert.NotEqual(text, result.RunStore(0, "docs/guide/x.md"));
        // The first pass wrote once; the second has nothing left to match, so it writes nothing more.
        Assert.Equal(1, result.RunWrites(0));
        Assert.Equal(1, result.RunWrites(1));
    }

    [Fact]
    public void Check_exits_one_while_a_rule_still_matches_and_zero_once_it_does_not()
    {
        var dirty = Call("check", "names", new Dictionary<string, string> { ["docs/guide/x.md"] = "Rise of Summoner.\n" });
        Assert.Equal(1, dirty.Root.GetProperty("exit").GetInt32());

        var clean = Call("check", "names", new Dictionary<string, string>
        {
            ["docs/guide/x.md"] = $"{NamesControlAfter}.\n"
        });
        Assert.Equal(0, clean.Root.GetProperty("exit").GetInt32());
    }

    [Fact]
    public void Apply_refuses_a_file_with_uncommitted_changes()
    {
        var text = "Rise of Summoner is here.\n";
        var result = Call("apply", "names", new Dictionary<string, string> { ["docs/guide/x.md"] = text },
            dirty: new[] { "docs/guide/x.md" });

        Assert.Equal(0, result.RunWrites(0));
        Assert.Equal(text, result.RunStore(0, "docs/guide/x.md"));
        Assert.Equal(1, result.Run(0).GetProperty("exit").GetInt32());
    }

    [Fact]
    public void The_rules_file_names_a_phase_scope_and_a_closed_identifier_list()
    {
        using var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), Rules)));

        var phases = new List<string>();
        foreach (var phase in rules.RootElement.GetProperty("phases").EnumerateArray())
        {
            phases.Add(phase.GetProperty("id").GetString()!);
            Assert.NotEmpty(phase.GetProperty("include").EnumerateArray());
            Assert.NotEmpty(phase.GetProperty("rules").EnumerateArray());
        }

        Assert.Equal(new[] { "names", "fusion" }, phases);
        Assert.NotEmpty(rules.RootElement.GetProperty("identifierAllowList").EnumerateArray());
    }

    [Fact]
    public void An_explicit_path_may_sit_outside_the_phase_scope()
    {
        // Plan D4: a code task proves "zero replaceable hits outside identifiers" over the files it
        // just changed, and those files live outside the prose scope `apply` walks. `--paths` is that
        // override. The exclude list still binds, so a rendered page is refused rather than scanned.
        var outside = Call("paths", "names", new Dictionary<string, string>(),
            paths: new[] { "web/fusion-rpg-web/src/features/story-scene/actorCast.ts" });
        Assert.Equal(0, outside.Root.GetProperty("exit").GetInt32());

        var excluded = Call("paths", "names", new Dictionary<string, string>(),
            paths: new[] { "docs/guide/mechanisms/dave-level.md" });
        Assert.Equal(2, excluded.Root.GetProperty("exit").GetInt32());
    }

    [Fact]
    public void The_rendered_guide_pages_are_out_of_scope_and_their_content_sources_are_in()
    {
        // Plan risk 4: a rendered page edited instead of its source is silently reverted by the next
        // _render.py run, so the tool must not see it at all (plan D5). The JSON sources must be in.
        var result = Call("scope", "names", new Dictionary<string, string>(), paths: new[]
        {
            "docs/guide/mechanisms/dave-level.md",
            "docs/guide/mechanisms/README.md",
            "docs/guide/site/mechanisms/dave-level.html",
            "docs/guide/mechanisms/_render.py",
            "docs/guide/mechanisms/_gen-stubs.ps1",
            "docs/guide/mechanisms/_content/dave-level.json",
            "docs/guide/the-game.md",
            "README.md",
            "CONTRIBUTING.md",
            "docs/README.md",
            "docs/assets/banner.svg",
            "docs/guide/mechanisms/local-control-room.md",
            "docs/guide/site/mechanisms/local-control-room.html"
        });

        Assert.False(result.InScope("docs/guide/mechanisms/dave-level.md"));
        Assert.False(result.InScope("docs/guide/mechanisms/README.md"));
        Assert.False(result.InScope("docs/guide/site/mechanisms/dave-level.html"));
        Assert.False(result.InScope("docs/guide/mechanisms/_render.py"));
        Assert.False(result.InScope("docs/guide/mechanisms/_gen-stubs.ps1"));
        Assert.True(result.InScope("docs/guide/mechanisms/_content/dave-level.json"));
        Assert.True(result.InScope("docs/guide/the-game.md"));
        Assert.True(result.InScope("README.md"));
        Assert.True(result.InScope("CONTRIBUTING.md"));
        Assert.True(result.InScope("docs/README.md"));
        Assert.True(result.InScope("docs/assets/banner.svg"));

        // The derivation is what keeps a HAND-AUTHORED page in scope: local-control-room has no
        // _content twin, and a glob exclude over every mechanisms/*.md swallowed its twelve real
        // hits while `check` still reported clean (found by T8's reading of the rendered output).
        Assert.True(result.InScope("docs/guide/mechanisms/local-control-room.md"));
        Assert.True(result.InScope("docs/guide/site/mechanisms/local-control-room.html"));
    }
}
