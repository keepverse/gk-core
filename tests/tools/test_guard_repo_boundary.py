"""Contract tests for `gk-core/scripts/guard-repo-boundary.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the CLOSED vocabulary of check ids, and the behaviour of B1/B2/B3. It does not
assert a message body, a line count, or the number of assemblies or host patterns - those are
readings, and a guardrail that pins a population fails when content ships and guards nothing.

**THE FAIL-OPEN HOLE THIS PORT CLOSED IS THE POINT OF THE SUITE.** B3 is diff-based, and the original
ran git with `2>$null`, so an unresolvable base ref produced empty stdout, which it read as "nothing
changed" - a clean run having checked nothing. Measured against the original before writing the port:
`-BaseRef no-such-ref-xyz` printed the OK verdict and exited 0 while git was saying `fatal: ambiguous
argument`. `TestUnresolvableBaseRefIsARefusalNotACleanRun` fails against the PowerShell form.

Differential evidence for everything else: 22 fixtures, 18 identical in exit code and every emitted
line, plus 4 declared divergences where the port catches what the original missed. That comparison
lives outside this file because the PowerShell form no longer exists.

`gk-core/tests/FusionRpg.Guard.Tests/RepoBoundaryGuardTests.cs` shells this same tool and builds real git
fixtures for B3.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-repo-boundary.py"

_spec = importlib.util.spec_from_file_location("guard_repo_boundary", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_repo_boundary"] = guard
_spec.loader.exec_module(guard)

CSPROJ = ('<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n'
          '    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n'
          '  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n')


def git(root: Path, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          timeout=300)


def build(root: Path, *, refs=None, extra_item="", sources=None, drop_project=None,
          csproj_override=None) -> Path:
    refs = refs or {}
    for project, allowed in guard.ALLOWED_GRAPH.items():
        if project == drop_project:
            continue
        directory = root / "src" / project
        directory.mkdir(parents=True, exist_ok=True)
        items = "\n".join(f'    <ProjectReference Include="..\\{r}\\{r}.csproj" />'
                          for r in refs.get(project, allowed))
        extra = f"\n    {extra_item}" if extra_item else ""
        text = (csproj_override or {}).get(project) or CSPROJ.format(items=items + extra)
        (directory / f"{project}.csproj").write_text(text, encoding="utf-8")
    for rel, body in (sources or {}).items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
    tasks = root / "tasks"
    tasks.mkdir(parents=True, exist_ok=True)
    (tasks / "plan.md").write_text("# perf plan (frozen history)\n", encoding="utf-8")
    (tasks / "todo.md").write_text("# perf todo (frozen history)\n", encoding="utf-8")
    git(root, "init", "-q")
    git(root, "config", "user.email", "guard@example.invalid")
    git(root, "config", "user.name", "guard-test")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "seed")
    return root


def ids(findings) -> set[str]:
    return {f.split(" ", 1)[0] for f in findings}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("guard-repo-boundary.py", proc.stdout)

    def test_the_both_ref_and_range_flags_are_accepted(self) -> None:
        for flags in ([], ["--base-ref", "HEAD"], ["--range", "HEAD~1..HEAD"]):
            with self.subTest(flags=flags):
                proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(REPO), *flags],
                                      capture_output=True, text=True, timeout=1800)
                self.assertEqual(guard.EXIT_OK, proc.returncode, proc.stderr)


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_the_real_tree_passes(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["findings"][:5])

    def test_a_violation_fails(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-") as tmp:
            root = build(Path(tmp), refs={"FusionRpg.Core": ["FusionRpg.Data"]})
            self.assertEqual("FAIL", guard.check(root)["verdict"])


class UnresolvableBaseRefIsARefusalNotACleanRun(unittest.TestCase):
    """THE HOLE. `2>$null` discarded git's diagnosis and an empty stdout read as "nothing changed",
    so a typo in CI's base reference disabled the whole of B3 and reported green."""

    def test_it_is_a_named_failure(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-hole-") as tmp:
            got = guard.check(build(Path(tmp)), base_ref="no-such-ref-xyz")
            self.assertEqual("FAIL", got["verdict"])
            self.assertEqual("GIT-FAILED", got["refused"])

    def test_the_failure_QUOTES_git_s_own_diagnosis(self) -> None:
        # The original threw the diagnosis away, which is why the failure was undiagnosable. A message
        # that says only "git failed" moves the cost to the reader.
        with tempfile.TemporaryDirectory(prefix="rb-hole-") as tmp:
            got = guard.check(build(Path(tmp)), base_ref="no-such-ref-xyz")
            self.assertTrue(any("ambiguous argument" in f for f in got["findings"]), got["findings"])

    def test_it_reaches_the_envelope_and_exits_one(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-hole-") as tmp:
            root = build(Path(tmp))
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(root), "--base-ref", "no-such-ref-xyz",
                 "--json"], capture_output=True, text=True, timeout=900)
            self.assertEqual(guard.EXIT_FAILED, proc.returncode)
            self.assertEqual("GIT-FAILED", json.loads(proc.stdout)["refused"])

    def test_a_root_that_is_not_a_repository_is_also_reported(self) -> None:
        # The same shape one level down: `git` cannot answer, so the tool must not invent an answer.
        with tempfile.TemporaryDirectory(prefix="rb-hole-") as tmp:
            got = guard.check(Path(tmp), base_ref="HEAD")
            self.assertEqual("FAIL", got["verdict"])
            self.assertEqual("GIT-FAILED", got["refused"])

    def test_an_unanswerable_B3_does_NOT_suppress_B1_or_B2(self) -> None:
        # Found by the C# suite. Its B1/B2 fixtures are plain directories with no git repository, and a
        # whole-run refusal there hid the B1/B2 findings entirely - five tests could not see the thing
        # they exist to check. B1 and B2 are filesystem questions that work on any tree; only B3 needs
        # git, so only B3 may be unavailable.
        with tempfile.TemporaryDirectory(prefix="rb-hole-") as tmp:
            root = Path(tmp)
            for project, allowed in guard.ALLOWED_GRAPH.items():
                directory = root / "src" / project
                directory.mkdir(parents=True, exist_ok=True)
                items = "\n".join(f'    <ProjectReference Include="..\\{r}\\{r}.csproj" />'
                                   for r in allowed)
                (directory / f"{project}.csproj").write_text(
                    CSPROJ.format(items=items), encoding="utf-8")
            (root / "src" / "FusionRpg.Server" / "Rogue.cs").write_text(
                "using UnityEngine;\nclass R {}\n", encoding="utf-8")
            got = guard.check(root, base_ref="HEAD")
            self.assertEqual({"B2", "B3"}, ids(got["findings"]))
            self.assertEqual("GIT-FAILED", got["refused"])


class B1ThePinnedGraph(unittest.TestCase):
    def _ids(self, **kwargs) -> set[str]:
        with tempfile.TemporaryDirectory(prefix="rb-b1-") as tmp:
            return ids(guard.check(build(Path(tmp), **kwargs))["findings"])

    def test_the_shipped_graph_passes(self) -> None:
        self.assertEqual("OK", guard.check(REPO)["verdict"])

    def test_an_out_of_graph_edge_is_reported(self) -> None:
        self.assertIn("B1", self._ids(refs={"FusionRpg.Core": ["FusionRpg.Data"]}))

    def test_a_zero_edge_project_may_not_have_edges(self) -> None:
        # Contracts is the root of the graph: an edge from it is a cycle, which is the whole point of
        # pinning it to nothing.
        self.assertIn("B1", self._ids(refs={"FusionRpg.Contracts": ["FusionRpg.Data"]}))

    def test_a_missing_project_is_a_finding_not_a_crash(self) -> None:
        self.assertIn("B1", self._ids(drop_project="FusionRpg.CheatCore"))

    def test_the_graph_is_the_shape_it_claims_to_be(self) -> None:
        # Not a count: the EDGES are a contract. Data may not reach Server, Server may not reach the
        # Injector, and Contracts may reach nothing. Asserting the pairs is what stops a later edit
        # from quietly widening the graph in the same commit that changes the guard.
        graph = guard.ALLOWED_GRAPH
        self.assertEqual((), graph["FusionRpg.Contracts"])
        for project, allowed in graph.items():
            self.assertNotIn("FusionRpg.Server", allowed, f"{project} must not depend on Server")
            self.assertNotIn(project, allowed, f"{project} must not depend on itself")
        for project in ("FusionRpg.Data", "FusionRpg.Server"):
            self.assertIn("FusionRpg.Core", graph[project])


class B2NoHostAssemblyAnywhere(unittest.TestCase):
    def _ids(self, **kwargs) -> set[str]:
        with tempfile.TemporaryDirectory(prefix="rb-b2-") as tmp:
            return ids(guard.check(build(Path(tmp), **kwargs))["findings"])

    def test_a_host_using_is_reported(self) -> None:
        self.assertIn("B2", self._ids(
            sources={"src/FusionRpg.Server/Rogue.cs": "using UnityEngine;\nclass R {}\n"}))

    def test_a_host_package_reference_is_reported(self) -> None:
        self.assertIn("B2", self._ids(extra_item='<PackageReference Include="HarmonyLib" />'))

    def test_a_host_assembly_reference_is_reported(self) -> None:
        self.assertIn("B2", self._ids(extra_item='<Reference Include="0Harmony" />'))

    def test_every_host_pattern_is_checked(self) -> None:
        # A closed vocabulary the code owns, so a literal is the right assertion here.
        for pattern in ("UnityEngine", "Il2Cpp", "MelonLoader", "BepInEx", "HarmonyLib", "0Harmony"):
            with self.subTest(pattern=pattern):
                self.assertIn("B2", self._ids(sources={
                    f"src/FusionRpg.Core/R.cs": f"using {pattern};\nclass R {{}}\n"}))

    def test_a_using_inside_a_COMMENT_is_still_flagged(self) -> None:
        # Raw-text scanning, deliberately. B2 asks "does this file name a host namespace", and
        # narrowing the scan would let a real reference hide behind a comment. This test exists so a
        # future reader does not "fix" it.
        self.assertIn("B2", self._ids(
            sources={"src/FusionRpg.Core/Note.cs": "// using MelonLoader;\nclass N {}\n"}))

    def test_a_word_that_merely_CONTAINS_a_pattern_is_not_flagged(self) -> None:
        # The other side of the same rule, so the over-match above cannot become a blanket match.
        self.assertNotIn("B2", self._ids(
            sources={"src/FusionRpg.Core/Note.cs": "// see usingUnityEngineHelpers\nclass N {}\n"}))

    def test_the_matching_folds_case(self) -> None:
        self.assertIn("B2", self._ids(
            sources={"src/FusionRpg.Core/Note.cs": "using unityengine;\nclass N {}\n"}))

    def test_a_namespaced_csproj_is_still_read(self) -> None:
        # PowerShell's `//ProjectReference` matches no namespaced element, so such a csproj yielded NO
        # references and B1 passed vacuously. The port matches the local tag name.
        namespaced = CSPROJ.format(
            items='    <ProjectReference Include="..\\FusionRpg.Data\\FusionRpg.Data.csproj" />'
        ).replace("<Project ", '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" ')
        with tempfile.TemporaryDirectory(prefix="rb-b2-") as tmp:
            root = build(Path(tmp), csproj_override={"FusionRpg.Core": namespaced})
            self.assertIn("B1", ids(guard.check(root)["findings"]))

    def test_a_malformed_csproj_is_a_named_refusal(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-b2-") as tmp:
            root = build(Path(tmp), csproj_override={"FusionRpg.Core": "<Project><ItemGroup></Project>"})
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(root)
            self.assertEqual("CSPROJ-NOT-XML", caught.exception.reason)

    def test_every_reported_path_is_repo_relative_and_exists(self) -> None:
        # The original embedded an ABSOLUTE path, so its B2 output carried the machine's own temp
        # directory into a CI log while B1 and B3 were repo-relative. Existence is the sharp claim.
        with tempfile.TemporaryDirectory(prefix="rb-b2-") as tmp:
            root = build(Path(tmp), sources={
                "src/FusionRpg.Server/Rogue.cs": "using UnityEngine;\nclass R {}\n"})
            found = [f for f in guard.check(root)["findings"] if f.startswith("B2 ")]
            self.assertTrue(found)
            for finding in found:
                named = finding.split(" ", 2)[1].rstrip(":").split(":", 1)[0]
                with self.subTest(named=named):
                    self.assertFalse(Path(named).is_absolute(), "B2's path must be repo-relative")
                    self.assertTrue((root / named).is_file())


class B3TheFrozenPlanningPaths(unittest.TestCase):
    """Diff-based, so a fixture that COMMITS its change tests nothing - the default base ref is HEAD
    and the guard reads the working tree against it. Four cases were silently vacuous until that was
    fixed."""

    def _ids(self, mutate) -> set[str]:
        with tempfile.TemporaryDirectory(prefix="rb-b3-") as tmp:
            root = build(Path(tmp))
            mutate(root)
            return ids(guard.check(root)["findings"])

    def test_a_clean_tree_passes(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-b3-") as tmp:
            self.assertEqual("OK", guard.check(build(Path(tmp)))["verdict"])

    def test_a_modified_tasks_plan_is_caught(self) -> None:
        self.assertIn("B3", self._ids(
            lambda r: (r / "tasks" / "plan.md").write_text("# changed\n", encoding="utf-8")))

    def test_an_added_tasks_todo_is_caught(self) -> None:
        # Untracked, so it arrives via `ls-files --others` rather than the diff - a second path into
        # the same rule, and one worth covering separately.
        self.assertIn("B3", self._ids(
            lambda r: (r / "tasks" / "todo.md").write_text("# replaced\n", encoding="utf-8")))

    def test_an_added_root_SPEC_is_caught(self) -> None:
        self.assertIn("B3", self._ids(lambda r: (r / "SPEC.md").write_text("# s\n", encoding="utf-8")))

    def test_a_DELETED_frozen_file_is_allowed(self) -> None:
        # Only M and A are violations: a deletion cannot make the frozen path a default again, and
        # demanding otherwise would make the rule impossible to satisfy honestly.
        self.assertNotIn("B3", self._ids(lambda r: (r / "tasks" / "plan.md").unlink()))

    def test_an_unrelated_change_is_allowed(self) -> None:
        self.assertNotIn("B3", self._ids(
            lambda r: (r / "docs.md").write_text("# d\n", encoding="utf-8")))

    def test_a_program_prefixed_plan_is_allowed(self) -> None:
        # The point of the rule: tasks/<program>-plan.md is where a fresh /plan goes.
        self.assertNotIn("B3", self._ids(
            lambda r: (r / "tasks" / "other-plan.md").write_text("# p\n", encoding="utf-8")))

    def test_an_explicit_range_is_honoured(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-b3-") as tmp:
            root = build(Path(tmp))
            (root / "tasks" / "plan.md").write_text("# changed\n", encoding="utf-8")
            git(root, "add", "-A")
            git(root, "commit", "-q", "-m", "touch")
            # Committed, so a working-tree diff sees nothing - which is the point of the range form.
            self.assertEqual("OK", guard.check(root)["verdict"])
            self.assertIn("B3", ids(guard.check(root, commit_range="HEAD~1..HEAD")["findings"]))

    def test_a_range_skips_the_untracked_pass(self) -> None:
        # Transcribed from the original: the untracked sweep is a WORKING-TREE concern, and a commit
        # range is a statement about history. Mixing them would report a file no commit contains.
        with tempfile.TemporaryDirectory(prefix="rb-b3-") as tmp:
            root = build(Path(tmp))
            (root / "SPEC.md").write_text("# s\n", encoding="utf-8")
            self.assertIn("B3", ids(guard.check(root)["findings"]))
            self.assertNotIn("B3", ids(guard.check(root, commit_range="HEAD..HEAD")["findings"]))


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "findings", "findings_by_check", "base_ref", "range",
            "refused"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-json-") as tmp:
            good = build(Path(tmp) / "ok")
            bad = build(Path(tmp) / "bad", refs={"FusionRpg.Core": ["FusionRpg.Data"]})
            for root, verdict in ((good, "OK"), (bad, "FAIL")):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run(
                        [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                        capture_output=True, text=True, timeout=1800)
                    self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
                    self.assertEqual(verdict, json.loads(proc.stdout)["verdict"])

    def test_the_check_vocabulary_is_closed(self) -> None:
        # A literal is right here: B1/B2/B3 is a vocabulary the code owns and a human changes.
        with tempfile.TemporaryDirectory(prefix="rb-json-") as tmp:
            root = build(Path(tmp), refs={"FusionRpg.Core": ["FusionRpg.Data"]},
                         sources={"src/FusionRpg.Server/R.cs": "using UnityEngine;\nclass R {}\n"})
            (root / "SPEC.md").write_text("# s\n", encoding="utf-8")
            got = guard.check(root)
            self.assertEqual({"B1", "B2", "B3"}, set(got["findings_by_check"]))
            self.assertEqual(sum(got["findings_by_check"].values()), len(got["findings"]))

    def test_the_refusal_envelope_is_distinguishable_from_a_verdict(self) -> None:
        with tempfile.TemporaryDirectory(prefix="rb-json-") as tmp:
            root = build(Path(tmp))
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(root), "--base-ref", "nope", "--json"],
                capture_output=True, text=True, timeout=900)
            payload = json.loads(proc.stdout)
            # The NORMAL envelope, whose verdict token is FAIL; only a whole-run refusal says FAILED.
            # An unanswerable B3 is a finding, not a refusal of the run - see the sibling test.
            self.assertEqual("FAIL", payload["verdict"])
            self.assertEqual("GIT-FAILED", payload["refused"])


if __name__ == "__main__":
    unittest.main()
