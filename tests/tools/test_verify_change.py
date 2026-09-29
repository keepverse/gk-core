"""Tests for `gk-core/scripts/verify-change.py` and `gk-core/scripts/lib/verification_boundaries.py`.

The load-bearing property is not "it prints a plan". It is that the plan is **the same plan**
`scripts/verify-change.ps1` prints, because that plan is the contract every agent and every
downstream tool depends on — and that a broken input is a NAMED refusal with a non-zero exit rather
than an empty plan, because an empty plan read as success is the failure this tool exists to prevent.

Three layers, deliberately:

* **The library**, unit-tested over planted fixtures — the four pattern shapes, the specificity fold,
  the most-specific-owner rule, the pytest junit/knownRed evidence rules.
* **The planner end to end** over a planted repository root (a real but tiny registry, with the real
  `guard-verification-boundaries.py` and its lib, so the integrity pre-check really runs), asserting
  the plan text, the `--format json` shape, and the exit codes.
* **Parity with the PowerShell original**: `guard-verification-boundaries.ps1`, retired 2026-09-28
  and several Guard tests are outside the porting lane's fence, so the two implementations coexist
  and must be proven equal rather than assumed equal. The lib-level parity check runs both over the
  same planted fragment; the planner-level check runs both over the real registry. Both skip — loudly,
  naming the missing binary — where no PowerShell is available, so a Windows CI runner always runs them
  and a non-Windows one is honest rather than green-by-absence.

Substrate: temp directories only, deleted in `tearDown` with the delete asserted (never swallowed),
per `docs/contributing/testing-standard.md` R3. No store, no network, no game, no real dotnet build.
"""
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL = REPO / "scripts" / "verify-change.py"
LIB = REPO / "scripts" / "lib" / "verification_boundaries.py"
PY_LIB = REPO / "scripts" / "lib" / "verification_boundaries.py"
PY_INTEGRITY_GUARD = REPO / "scripts" / "guard-verification-boundaries.py"


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


vb = _load(LIB, "verification_boundaries_under_test")
vc = _load(TOOL, "verify_change_under_test")


def run_tool(*args: str, root: Path | None = None, timeout: float = 900.0) -> subprocess.CompletedProcess:
    argv = [sys.executable, str(TOOL), *args]
    return subprocess.run(argv, cwd=str(root or REPO), capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout, check=False)


# ============================================================================================
# The shared library
# ============================================================================================

# A real tracked file that no boundary row covers, chosen by the registry's SCOPE rather than by
# happening to be unmapped today. A probe picked for being unmapped breaks when the mapping is
# legitimately repaired, which is exactly what happened to the README.md this replaced.
UNMAPPED_PROBE = ".editorconfig"


class PatternMatchingTests(unittest.TestCase):
    """`Test-PatternMatch` / `Test-ValidPatternGrammar` / `Get-PatternSpecificity`, the rule the
    whole registry is built on. A drift here silently re-points ownership, so the four shapes, the
    case-insensitivity and the "a wildcard never reaches into a deeper directory" rule are all pinned.
    """

    def test_directory_markdown_shape_is_extension_filtered(self) -> None:
        self.assertTrue(vb.pattern_match("docs/a/b.md", "docs/**/*.md"))
        # An assistant-config tree holds scripts too, which must stay unmapped rather than inherit a
        # docs-only boundary — that is why this shape exists separately from `prefix/**`.
        self.assertTrue(vb.pattern_match(".claude/agents/x.md", ".claude/**/*.md"))
        self.assertFalse(vb.pattern_match(".claude/agents/x.py", ".claude/**/*.md"))
        self.assertFalse(vb.pattern_match("docs/a/b.py", "docs/**/*.md"))
        self.assertFalse(vb.pattern_match("docs/a/b.mdx", "docs/**/*.md"))

    def test_prefix_star_star_matches_any_depth_under_the_prefix(self) -> None:
        self.assertTrue(vb.pattern_match("src/Anything/Deep/Here.cs", "src/**"))
        self.assertTrue(vb.pattern_match("src/Top.cs", "src/**"))
        self.assertFalse(vb.pattern_match("srcs/Top.cs", "src/**"))

    def test_a_wildcard_stays_inside_the_final_segment(self) -> None:
        self.assertTrue(vb.pattern_match("data/tuning/power-scale.v3.json", "data/tuning/*.json"))
        self.assertFalse(vb.pattern_match("data/tuning/deep/power.json", "data/tuning/*.json"))

    def test_every_comparison_is_case_insensitive(self) -> None:
        self.assertTrue(vb.pattern_match("SRC/Top.cs", "src/**"))
        self.assertTrue(vb.pattern_match("src/Top.CS", "src/Top.cs"))
        self.assertTrue(vb.pattern_match("Docs/A/B.MD", "docs/**/*.md"))

    def test_an_exact_pattern_is_an_exact_pattern(self) -> None:
        self.assertTrue(vb.exact_pattern("scripts/verify-change.py"))
        self.assertFalse(vb.exact_pattern("scripts/*.py"))
        self.assertFalse(vb.exact_pattern("scripts/**"))

    def test_grammar_rejects_a_wildcard_outside_the_final_segment(self) -> None:
        self.assertTrue(vb.valid_pattern_grammar("a/b/*.cs"))
        self.assertTrue(vb.valid_pattern_grammar("a/b/**"))
        self.assertTrue(vb.valid_pattern_grammar("a/b/**/*.md"))
        self.assertFalse(vb.valid_pattern_grammar("a/*/b.cs"))
        self.assertFalse(vb.valid_pattern_grammar("a/*b/**/*.md"))

    def test_specificity_ranks_exact_over_wildcard_over_prefix_and_breaks_ties_by_length(self) -> None:
        exact = vb.pattern_specificity("src/Top.cs")
        wildcard = vb.pattern_specificity("src/*.cs")
        markdown = vb.pattern_specificity("src/**/*.md")
        prefix = vb.pattern_specificity("src/**")
        self.assertTrue(exact > wildcard > markdown > prefix)
        # Same class: the longer pattern is the narrower one.
        self.assertGreater(vb.pattern_specificity("src/Fake/Deep.cs"),
                           vb.pattern_specificity("src/Fake.cs"))

    def test_a_wildcard_star_covers_a_separator_so_a_session_fence_nests(self) -> None:
        # This is the session-fence and exemption matcher (a PowerShell WildcardPattern, whose `*`
        # spans separators). A narrower reading would make every `tests/X/**` fence refuse its own
        # subdirectories.
        self.assertTrue(vb.wildcard_match("tests/X.Tests/Deep/File.cs", "tests/X.Tests/**"))
        self.assertFalse(vb.wildcard_match("tests/Xy/File.cs", "tests/X.Tests/**"))
        self.assertTrue(vb.wildcard_match("tests/x.tests/Deep/File.cs", "tests/X.Tests/**"))


class OwnerResolutionTests(unittest.TestCase):
    """`Resolve-Owner` — the most-specific-owner fold, and the AMBIGUOUS tie it must report."""

    BOUNDARIES = [
        {"id": "broad", "kind": "owner", "paths": ["src/**"]},
        {"id": "narrow", "kind": "owner", "paths": ["src/Fake/**"]},
        {"id": "exact", "kind": "owner", "paths": ["src/Fake/Widget.cs"]},
        {"id": "a-tie", "kind": "owner", "paths": ["src/Tie/**"]},
        {"id": "b-tie", "kind": "owner", "paths": ["src/Tie/**"]},
    ]

    def test_the_most_specific_owner_wins(self) -> None:
        for path, expected in (("src/Fake/Widget.cs", "exact"),
                               ("src/Fake/Other.cs", "narrow"),
                               ("src/Elsewhere.cs", "broad")):
            resolution = vb.resolve_owner(path, self.BOUNDARIES)
            assert resolution is not None
            self.assertEqual([owner["id"] for owner in resolution.owners], [expected], path)

    def test_two_owners_tied_at_the_winning_specificity_are_ambiguous_not_arbitrary(self) -> None:
        resolution = vb.resolve_owner("src/Tie/Thing.cs", self.BOUNDARIES)
        assert resolution is not None
        self.assertEqual([owner["id"] for owner in resolution.owners], ["a-tie", "b-tie"])

    def test_nothing_matching_resolves_to_nothing(self) -> None:
        self.assertIsNone(vb.resolve_owner("tools/Other.cs", self.BOUNDARIES))

    def test_the_index_is_sound_and_agrees_with_the_plain_scan(self) -> None:
        index = vb.build_owner_pattern_index(self.BOUNDARIES)
        for path in ("src/Fake/Widget.cs", "src/Elsewhere.cs", "tools/Other.cs", "src/Tie/Thing.cs"):
            with_index = vb.resolve_owner(path, self.BOUNDARIES, index)
            without_index = vb.resolve_owner(path, self.BOUNDARIES)
            self.assertEqual(with_index, without_index, path)

    def test_a_seam_boundary_is_never_an_owner(self) -> None:
        # The caller filters `kind == 'owner'`; feeding a seam in would let it own a path. Assert the
        # planner's own filter, not the lib's tolerance, by showing the lib is only as good as the
        # caller's filter.
        self.assertEqual(vb.derived_level({"kind": "seam", "paths": ["x"]}), "seam")
        self.assertEqual(vb.derived_level({"kind": "owner", "paths": ["x"]}), "full")

    def test_derived_level_follows_the_boundary_shape(self) -> None:
        self.assertEqual(vb.derived_level({"kind": "owner", "project": "p"}), "module")
        self.assertEqual(vb.derived_level({"kind": "owner", "guards": ["g"]}), "module")
        self.assertEqual(vb.derived_level({"kind": "owner", "project": "p", "verificationId": "a.b"}), "focused")
        self.assertEqual(vb.derived_level({"kind": "owner", "project": "p", "selfSelect": True}), "focused")
        self.assertEqual(vb.derived_level({"kind": "owner", "project": "p", "testFiles": ["t.py"]}), "focused")


class ProjectEntryTests(unittest.TestCase):
    """`Get-ProjectMembers` / `Get-ProjectRunner` / `Get-PytestProjectDirs` — the three `projects`
    shapes (a string, a C7 group, an object with a closed `runner` vocabulary).
    """

    PROJECTS = {
        "single": "tests/A.Tests/A.Tests.csproj",
        "group": ["tests/A.Tests/A.Tests.csproj", "tests/B.Tests/B.Tests.csproj"],
        "py": {"runner": "pytest", "root": "tools/x", "tests": "tests"},
        "scr": {"runner": "script", "script": "scripts/checks/x.py"},
    }

    def test_runner_and_members_per_shape(self) -> None:
        self.assertEqual(vb.project_runner(self.PROJECTS, "single"), "dotnet")
        self.assertEqual(vb.project_runner(self.PROJECTS, "group"), "dotnet")
        self.assertEqual(vb.project_runner(self.PROJECTS, "py"), "pytest")
        self.assertEqual(vb.project_runner(self.PROJECTS, "scr"), "script")
        self.assertIsNone(vb.project_runner(self.PROJECTS, "absent"))
        self.assertEqual(vb.project_members(self.PROJECTS, "group"), self.PROJECTS["group"])
        # An object-shaped entry has no .csproj members, so the group/trait resolution has nothing to
        # iterate instead of stringifying the object into a garbage path.
        self.assertEqual(vb.project_members(self.PROJECTS, "py"), [])

    def test_pytest_dirs_combine_root_and_tests(self) -> None:
        dirs = vb.pytest_project_dirs(self.PROJECTS, "py")
        assert dirs is not None
        self.assertEqual((dirs.root, dirs.tests, dirs.test_dir), ("tools/x", "tests", "tools/x/tests"))
        self.assertIsNone(vb.pytest_project_dirs(self.PROJECTS, "single"))

    def test_trait_scan_reads_the_projects_own_directory(self) -> None:
        with tempfile.TemporaryDirectory(prefix="vb-trait-") as raw:
            root = Path(raw)
            (root / "tests" / "A.Tests").mkdir(parents=True)
            (root / "tests" / "A.Tests" / "Trait.cs").write_text(
                '[Trait("VerificationId", "a.b")]\npublic class T {}\n', encoding="utf-8")
            (root / "tests" / "A.Tests" / "Other.cs").write_text("public class O {}\n", encoding="utf-8")
            self.assertTrue(vb.project_has_trait(root, "tests/A.Tests/A.Tests.csproj", "a.b"))
            self.assertFalse(vb.project_has_trait(root, "tests/A.Tests/A.Tests.csproj", "a.c"))
            self.assertFalse(vb.project_has_trait(root, "tests/Missing/Missing.csproj", "a.b"))


class PytestEvidenceTests(unittest.TestCase):
    """python-test-lane D6: a pytest exit code is not a verdict, the junit report is."""

    def setUp(self) -> None:
        self._tmp = Path(tempfile.mkdtemp(prefix="vb-junit-"))

    def tearDown(self) -> None:
        # R3: the delete is asserted, never swallowed.
        shutil.rmtree(self._tmp)
        self.assertFalse(self._tmp.exists())

    def _write(self, name: str, body: str) -> Path:
        path = self._tmp / name
        path.write_text(body, encoding="utf-8")
        return path

    def test_testcases_report_failure_and_error_as_failed(self) -> None:
        report = self._write("r.xml", """<?xml version="1.0"?>
<testsuites><testsuite>
  <testcase classname="pkg.test_m" name="test_ok" />
  <testcase classname="pkg.test_m" name="test_bad"><failure message="x" /></testcase>
  <testcase classname="pkg.test_m" name="test_boom"><error message="y" /></testcase>
</testsuite></testsuites>
""")
        cases = vb.junit_test_cases(report)
        self.assertEqual([(c.classname, c.name, c.failed) for c in cases],
                         [("pkg.test_m", "test_ok", False),
                          ("pkg.test_m", "test_bad", True),
                          ("pkg.test_m", "test_boom", True)])

    def test_a_known_red_failure_passes_and_prints_its_debt(self) -> None:
        cases = [vb.JUnitTestCase(classname="tests.test_m", name="test_known", failed=True)]
        known = [{"project": "p", "test": "tests/test_m.py::test_known", "debt": "SR-1"}]
        outcome = vb.resolve_known_red_outcome("p", cases, known)
        self.assertTrue(outcome.ok)
        self.assertIn("KNOWN RED (pre-existing)", outcome.messages[0])

    def test_an_unexpected_failure_fails_the_check(self) -> None:
        cases = [vb.JUnitTestCase(classname="tests.test_m", name="test_new", failed=True)]
        outcome = vb.resolve_known_red_outcome("p", cases, [])
        self.assertFalse(outcome.ok)
        self.assertIn("UNEXPECTED FAILURE", outcome.messages[0])

    def test_a_known_red_entry_that_went_green_is_stale_not_a_win(self) -> None:
        # D6 rule 3: pytest can exit 0 on every test while the registry still calls one of them red.
        cases = [vb.JUnitTestCase(classname="tests.test_m", name="test_known", failed=False)]
        known = [{"project": "p", "test": "tests/test_m.py::test_known", "debt": "SR-1"}]
        outcome = vb.resolve_known_red_outcome("p", cases, known)
        self.assertFalse(outcome.ok)
        self.assertIn("stale knownRed entry", outcome.messages[0])

    def test_a_known_red_entry_that_did_not_run_has_no_effect(self) -> None:
        known = [{"project": "p", "test": "tests/test_m.py::test_absent", "debt": "SR-1"}]
        outcome = vb.resolve_known_red_outcome("p", [], known)
        self.assertTrue(outcome.ok)
        self.assertEqual(outcome.messages, [])

    def test_a_node_id_derives_the_classname_pytest_would_write(self) -> None:
        self.assertTrue(vb.known_red_test_matches_node("tests/test_m.py::Klass::test_x",
                                                       "tests.test_m.Klass", "test_x"))
        self.assertTrue(vb.known_red_test_matches_node("tests/test_m.py::test_x", "tests.test_m", "test_x"))
        self.assertFalse(vb.known_red_test_matches_node("tests/test_m.py::test_x", "tests.test_m", "test_y"))
        self.assertFalse(vb.known_red_test_matches_node("not-a-node-id", "a", "b"))


class TrxEvidenceTests(unittest.TestCase):
    """`dotnet test` can exit 0 when a filter matched nothing, so the TRX is the verdict."""
    def setUp(self) -> None:
        self._tmp = Path(tempfile.mkdtemp(prefix="vb-trx-"))

    def tearDown(self) -> None:
        shutil.rmtree(self._tmp)
        self.assertFalse(self._tmp.exists())

    def _count(self, body: str) -> int:
        path = self._tmp / "verify-change.trx"
        path.write_text(body, encoding="utf-8")
        return vc._trx_executed_tests(path)

    def test_a_namespaced_trx_result_set_counts(self) -> None:
        self.assertEqual(self._count(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '<Results><UnitTestResult testName="a" outcome="Passed" /></Results></TestRun>'), 1)

    def test_not_executed_and_blank_outcomes_are_not_evidence(self) -> None:
        self.assertEqual(self._count(
            '<TestRun><Results>'
            '<UnitTestResult testName="a" outcome="NotExecuted" />'
            '<UnitTestResult testName="b" outcome="" />'
            '<UnitTestResult testName="c" outcome="Failed" />'
            '</Results></TestRun>'), 1)

    def test_an_empty_result_set_is_zero_and_the_caller_must_refuse(self) -> None:
        self.assertEqual(self._count('<TestRun><Results /></TestRun>'), 0)


class DotnetDisciplineTests(unittest.TestCase):
    """The 2026-09-26 race: a test must never spawn the build that follows it.

    `dotnet test` spawns `bin/Release/net8.0/testhost.exe`, which can outlive the `dotnet test` call,
    and the NEXT build of that project then dies with `MSB3027 "Could not copy ...
    FusionRpg.Guard.Tests.dll ... The file is locked by: testhost"` — reproduced twice on 2026-09-26,
    one of them killing a whole verification run with `exit -1`.

    A race is a bad thing to pin by hoping it reproduces, so the DISCIPLINE is pinned instead: the
    external `dotnet` calls are captured, and the assertion is that a project is built once and every
    test against it carries `--no-build`. The same fixture also drives the TRX-evidence refusals, which
    are equally hard to reach through a real build.
    """

    def setUp(self) -> None:
        self._tmp = Path(tempfile.mkdtemp(prefix="vb-dotnet-"))
        self._calls: list[list[str]] = []
        self._results_dirs: list[str] = []
        self._trx_outcome = "Passed"
        self._trx_count = 1
        self._real_run = vc._run
        self._real_which = vc._which
        self._repo = self._tmp / "repo"
        (self._repo / "tests" / "Fake.Tests").mkdir(parents=True)
        (self._repo / "tests" / "Fake.Tests" / "Fake.Tests.csproj").write_text("<Project />\n", encoding="utf-8")
        vc._which = lambda tool: f"C:/fake/{tool}"  # noqa: ARG005 - the argv shape is what matters
        vc._run = self._fake_run

    def tearDown(self) -> None:
        vc._run = self._real_run
        vc._which = self._real_which
        shutil.rmtree(self._tmp)
        self.assertFalse(self._tmp.exists())

    def _fake_run(self, argv, *, timeout, cwd=None, stage):  # noqa: ANN001 - mirrors the real signature
        self._calls.append([str(a) for a in argv])
        if "--results-directory" in argv:
            results = Path(argv[argv.index("--results-directory") + 1])
            self._results_dirs.append(str(results))
            results.mkdir(parents=True, exist_ok=True)
            for index in range(self._trx_count):
                (results / f"verify-change{index}.trx").write_text(
                    '<TestRun><Results><UnitTestResult outcome="' + self._trx_outcome + '" /></Results></TestRun>',
                    encoding="utf-8")
        return subprocess.CompletedProcess(argv, 0, "", "")

    def _runner(self) -> "vc.Runner":
        inputs = vc.Inputs(root=self._repo, registry={}, guard_catalog={}, verification_exemptions=[],
                           sharded_project_ids=set(), default_profile_filter="Category!=Heavy")
        return vc.Runner(inputs=inputs, timeout=60.0, session=None)

    def test_a_project_is_built_once_and_every_test_against_it_runs_no_build(self) -> None:
        runner = self._runner()
        project = "tests/Fake.Tests/Fake.Tests.csproj"
        # Two focused checks against the SAME project is the ordinary case (two boundaries selecting
        # two VerificationIds on one test assembly) and the one that used to collide.
        runner._dotnet_test_with_evidence(project, "VerificationId=a.b", "guard focused check (one)")
        runner._dotnet_test_with_evidence(project, "VerificationId=c.d", "guard focused check (two)")

        builds = [c for c in self._calls if c[1] == "build"]
        tests = [c for c in self._calls if c[1] == "test"]
        self.assertEqual(len(builds), 1, f"expected exactly one build, got: {self._calls}")
        self.assertEqual(len(tests), 2)
        for call in tests:
            self.assertIn("--no-build", call, f"a test invocation may not spawn a build: {call}")
        self.assertIn("-c", builds[0])
        self.assertEqual(builds[0][builds[0].index("-c") + 1], "Release")
        self.assertEqual(tests[0][tests[0].index("-c") + 1], "Release")

    def test_a_different_project_is_built_separately(self) -> None:
        (self._repo / "tests" / "Other.Tests").mkdir(parents=True)
        (self._repo / "tests" / "Other.Tests" / "Other.Tests.csproj").write_text("<Project />\n", encoding="utf-8")
        runner = self._runner()
        runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "Category!=Heavy", "one")
        runner._dotnet_test_with_evidence("tests/Other.Tests/Other.Tests.csproj", "Category!=Heavy", "two")
        self.assertEqual(len([c for c in self._calls if c[1] == "build"]), 2)

    def test_a_zero_match_filter_is_red_not_a_pass(self) -> None:
        # Every result present but nothing EXECUTED: `dotnet test` exits 0 and the plan would report a
        # green check. The TRX is what turns that back into a refusal.
        self._trx_outcome = "NotExecuted"
        runner = self._runner()
        with self.assertRaises(vc.Refusal) as caught:
            runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "VerificationId=zz",
                                             "guard focused check")
        self.assertEqual(caught.exception.name, "ZERO-TESTS")
        self.assertIn("RED", caught.exception.detail)

    def test_ambiguous_evidence_files_are_refused(self) -> None:
        self._trx_count = 2
        runner = self._runner()
        with self.assertRaises(vc.Refusal) as caught:
            runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "Category!=Heavy", "one")
        self.assertEqual(caught.exception.name, "TEST-EVIDENCE-AMBIGUOUS")

    def test_a_nonzero_test_exit_is_refused_before_the_evidence_is_read(self) -> None:
        def failing(argv, *, timeout, cwd=None, stage):  # noqa: ANN001
            self._calls.append([str(a) for a in argv])
            if str(argv[1]) == "build":
                return subprocess.CompletedProcess(argv, 0, "", "")
            return subprocess.CompletedProcess(argv, 1, "boom\n", "")
        vc._run = failing
        runner = self._runner()
        with self.assertRaises(vc.Refusal) as caught:
            runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "Category!=Heavy", "one")
        self.assertEqual(caught.exception.name, "TEST-FAILED")
        self.assertIn("boom", caught.exception.detail)

    def test_a_failed_build_stops_the_check_before_any_test_runs(self) -> None:
        def failing(argv, *, timeout, cwd=None, stage):  # noqa: ANN001
            self._calls.append([str(a) for a in argv])
            return subprocess.CompletedProcess(argv, 1, "CS1001\n", "")
        vc._run = failing
        runner = self._runner()
        with self.assertRaises(vc.Refusal) as caught:
            runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "Category!=Heavy", "one")
        self.assertEqual(caught.exception.name, "BUILD-FAILED")
        self.assertEqual([c[1] for c in self._calls], ["build"])

    def test_a_temp_results_directory_is_removed_after_the_run(self) -> None:
        runner = self._runner()
        runner._dotnet_test_with_evidence("tests/Fake.Tests/Fake.Tests.csproj", "Category!=Heavy", "one")
        self.assertEqual(len(self._results_dirs), 1)
        self.assertFalse(Path(self._results_dirs[0]).exists(),
                         "the temp results directory survived the run")

    def test_a_delete_that_cannot_complete_is_a_named_refusal_never_a_swallow(self) -> None:
        # R3 is the whole reason this exists: an empty catch around a delete is what hid 65.5 GB of
        # leaked temp directories. A delete that cannot complete must FAIL LOUDLY, and must leave the
        # directory in place so the failure is visible rather than papered over.
        target = self._tmp / "locked"
        target.mkdir()
        (target / "f.txt").write_text("x", encoding="utf-8")
        real_rmtree = vc.shutil.rmtree
        vc.shutil.rmtree = lambda *a, **k: (_ for _ in ()).throw(OSError("the process cannot access this file"))
        try:
            with self.assertRaises(vc.Refusal) as caught:
                vc._drain_temp_dir(target)
        finally:
            vc.shutil.rmtree = real_rmtree
        self.assertEqual(caught.exception.name, "TEMP-CLEANUP")
        self.assertTrue(target.exists(), "a failed delete must not look like a successful one")

    def test_a_results_directory_that_is_already_gone_is_not_a_failure(self) -> None:
        # Nothing to delete is not a failed delete; a REAL failure is. Without this distinction every
        # idempotent re-entry would refuse.
        vc._drain_temp_dir(self._tmp / "never-existed")


# ============================================================================================
# The planner, end to end, over a planted repository root
# ============================================================================================

def _plant_registry() -> dict:
    """A small but VALID registry: every shape the planner has a branch for, once.

    Validity is not decoration — the planner's first act is to run the real integrity guard over this
    root, so a boundary that would be refused in the real tree is refused here too. The fixture
    therefore proves the pre-check and the plan at the same time.
    """
    return {
        "schemaVersion": 5,
        "projects": {
            "guardproj": "tests/Fake.Guard.Tests/Fake.Guard.Tests.csproj",
            "groupproj": ["tests/Fake.Data.Tests/Fake.Data.Tests.csproj",
                          "tests/Fake.Data.More.Tests/Fake.Data.More.Tests.csproj"],
            "shardproj": "tests/Fake.Shard.Tests/Fake.Shard.Tests.csproj",
            "pyproj": {"runner": "pytest", "root": "tools/fakepy", "tests": "tests"},
            "scrproj": {"runner": "script", "script": "scripts/checks/fake-check.py"},
        },
        "boundaries": [
            {"id": "fake-source", "kind": "owner", "paths": ["src/Fake/Widget.cs"],
             "project": "guardproj", "verificationId": "guard.fake", "guards": [], "level": "focused"},
            {"id": "fake-group-source", "kind": "owner", "paths": ["src/FakeGroup/Alpha.cs"],
             "project": "groupproj", "verificationId": "fake.alpha", "guards": [], "level": "focused"},
            {"id": "fake-module", "kind": "owner", "paths": ["src/FakeModule/**"],
             "project": "guardproj", "verificationId": None, "guards": ["dal"], "level": "module"},
            {"id": "fake-shard", "kind": "owner", "paths": ["src/FakeShard/**"],
             "project": "shardproj", "verificationId": None, "guards": [], "level": "module"},
            # Two DIFFERENT last-segment wildcards of the same class and length that both match
            # `src/x.cs`. Identical patterns cannot express this case: the registry guard refuses a
            # duplicated owner pattern outright ("ambiguous owner pattern"), so the only reachable
            # ambiguity is two distinct patterns that tie.
            {"id": "fake-tie-a", "kind": "owner", "paths": ["src/*x.cs"],
             "project": "guardproj", "guards": [], "level": "module"},
            {"id": "fake-tie-b", "kind": "owner", "paths": ["src/x*.cs"],
             "project": "guardproj", "guards": [], "level": "module"},
            {"id": "pyproj-source", "kind": "owner", "paths": ["tools/fakepy/adapter.py"],
             "project": "pyproj", "testFiles": ["tools/fakepy/tests/test_a*.py"],
             "guards": [], "level": "focused"},
            {"id": "pyproj-tests", "kind": "owner", "paths": ["tools/fakepy/tests/**"],
             "project": "pyproj", "selfSelect": True, "guards": [], "level": "focused"},
            {"id": "scrproj-source", "kind": "owner", "paths": ["web/fake/src/**"],
             "project": "scrproj", "guards": [], "level": "module"},
            {"id": "fake-full", "kind": "owner", "paths": ["data/generated/legacy.json"],
             "guards": [], "level": "full"},
            {"id": "fake-doc", "kind": "owner", "paths": ["docs/fake-note.md"],
             "project": "guardproj", "guards": [], "level": "module"},
            {"id": "fake-seam", "kind": "seam", "paths": ["src/Fake/Widget.cs"],
             "guards": ["dal"], "level": "seam"},
        ],
        "knownRed": [],
    }


class FixtureRepo:
    """A planted repository root the planner accepts end to end, and a temp dir for its teardown."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="vb-fixture-"))

    def close(self) -> None:
        # R3: the delete is asserted, never swallowed.
        shutil.rmtree(self.root)
        self.assert_absent()

    def assert_absent(self) -> None:
        assert not self.root.exists(), f"fixture root survived teardown: {self.root}"

    def write(self, relative: str, text: str = "") -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def build(self) -> None:
        # The real integrity guard and the real library: the pre-check is delegated, not faked.
        (self.root / "scripts" / "lib").mkdir(parents=True, exist_ok=True)
        # The planner's integrity pre-check is DELEGATED to the real guard, and that guard is Python and
        # imports the Python lib -- so the fixture provides the Python pair and nothing else. Copying
        # the PowerShell pair left a fixture whose pre-check could not run once the registry row named a
        # `.py`, and the SAME PowerShell lib was being copied TWICE on consecutive lines, which is how a
        # stale "BOTH libs" justification survived a change that made it false.
        shutil.copy2(PY_INTEGRITY_GUARD, self.root / "scripts" / "guard-verification-boundaries.py")
        shutil.copy2(PY_LIB, self.root / "scripts" / "lib" / "verification_boundaries.py")
        self.write("scripts/test_fast.py", 'FILTER = "Category!=DiskSemantics&Category!=Heavy"\n')
        self.write("scripts/test-shards.v1.json", json.dumps(
            {"schemaVersion": 1, "projects": {"shardproj": {"shards": [{"id": "all", "remainder": True}]}}}))
        self.write("scripts/verification-boundaries.v1.json", json.dumps(_plant_registry(), indent=2))
        self.write("scripts/enforcement-registry.v1.json", json.dumps({
            "schemaVersion": 1,
            "guards": {
                "dal": {"script": "scripts/guard-dal.ps1"},
                "test-substrate": {"script": "scripts/guard-test-substrate.ps1"},
            },
            "verificationExemptions": [
                {"id": "legacy-ops", "paths": ["scripts/legacy-tool.ps1"],
                 "reason": "planted exemption with no local proof"},
            ],
        }, indent=2))
        # The files the registry's patterns and traits name have to exist, or the guard refuses the
        # root as stale — which is exactly the check under test.
        for csproj in ("tests/Fake.Guard.Tests/Fake.Guard.Tests.csproj",
                       "tests/Fake.Data.Tests/Fake.Data.Tests.csproj",
                       "tests/Fake.Data.More.Tests/Fake.Data.More.Tests.csproj",
                       "tests/Fake.Shard.Tests/Fake.Shard.Tests.csproj"):
            self.write(csproj, "<Project />\n")
        # A group-level VerificationId needs the trait in AT LEAST ONE member; only one member has it,
        # which is also what makes focused group selection narrower than the whole group.
        self.write("tests/Fake.Guard.Tests/Traits.cs",
                   '[Trait("VerificationId", "guard.fake")]\npublic class G {}\n')
        self.write("tests/Fake.Data.Tests/Traits.cs",
                   '[Trait("VerificationId", "fake.alpha")]\npublic class A {}\n')
        self.write("src/Fake/Widget.cs", "// planted\n")
        self.write("src/Fake/Other.cs", "// planted\n")
        self.write("src/x.cs", "// planted: matched by two equal-specificity owner patterns\n")
        self.write("src/FakeGroup/Alpha.cs", "// planted\n")
        self.write("src/FakeModule/Thing.cs", "// planted\n")
        self.write("src/FakeShard/Thing.cs", "// planted\n")
        self.write("web/fake/src/Thing.tsx", "// planted\n")
        self.write("tools/fakepy/adapter.py", "# planted\n")
        self.write("tools/fakepy/tests/test_alpha.py", "# planted\n")
        self.write("tools/fakepy/tests/test_beta.py", "# planted\n")
        self.write("tools/fakepy/tests/conftest.py", "# planted\n")
        self.write("scripts/checks/fake-check.py", "# planted\n")
        self.write("scripts/guard-dal.ps1", "# planted\n")
        self.write("scripts/guard-test-substrate.ps1", "# planted\n")
        self.write("scripts/legacy-tool.ps1", "# planted\n")
        self.write("docs/fake-note.md", "# planted\n")
        self.write("data/generated/legacy.json", "{}\n")
        self.write("src/Unmapped/Thing.cs", "// planted, deliberately unmapped\n")
        (self.root / "tasks" / "sessions").mkdir(parents=True, exist_ok=True)
        self.write("tasks/sessions/fake-active.json", json.dumps({
            "session": "fake-active", "program": "p", "problem": "x", "mode": "worktree",
            "branch": "port/x", "worktree": None, "paths": ["src/Fake/**", "docs/**"],
            "started": "2026-09-26T00:00:00Z", "status": "active"}))
        self.write("tasks/sessions/fake-closed.json", json.dumps({
            "session": "fake-closed", "program": "p", "problem": "x", "mode": "worktree",
            "branch": "port/y", "worktree": None, "paths": ["src/Fake/**"],
            "started": "2026-09-26T00:00:00Z", "status": "merged"}))


# NO `skipIf(powershell() is None)` HERE, and its removal is deliberate. The skip claimed the integrity
# pre-check needed pwsh, but the pre-check is `guard-verification-boundaries.py` -- a PYTHON tool whose
# lib is `verification_boundaries.py`, and the fixture copies that pair. So nothing in this class ever
# spawned PowerShell, and on any machine without pwsh on PATH these 32 tests were skipped while the run
# still reported green. A suite that quietly drops a third of itself checks less than it says it does.
class PlannedFixtureTests(unittest.TestCase):
    """Plan selection, the JSON shape and the exit codes, over a planted root the guard accepts."""

    @classmethod
    def setUpClass(cls) -> None:
        cls._fixture = FixtureRepo()
        cls._fixture.build()

    @classmethod
    def tearDownClass(cls) -> None:
        cls._fixture.close()

    # -- the plan ----------------------------------------------------------------------------

    def _plan(self, *args: str) -> subprocess.CompletedProcess:
        # `--root` is the point of the fixture: the tool's own directory is the real repo, so a
        # planted root has to be named explicitly. The PowerShell original's `-Root` says the same.
        return run_tool(*args, "--root", str(self._fixture.root), timeout=600)

    def test_a_focused_source_path_selects_its_owners_boundary_and_nothing_wider(self) -> None:
        result = self._plan("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("src/Fake/Widget.cs -> fake-source (focused)", result.stdout)
        # The seam layered on the same path is selected too, and the wide module fallback is NOT.
        self.assertIn("src/Fake/Widget.cs -> fake-seam (seam)", result.stdout)
        self.assertNotIn("fake-module", result.stdout)
        self.assertIn("test: guardproj guard.fake", result.stdout)
        self.assertIn("  full evidence: CI/nightly/release", result.stdout)

    def test_a_group_projects_focused_selection_narrows_to_the_member_carrying_the_trait(self) -> None:
        result = self._plan("--paths", "src/FakeGroup/Alpha.cs", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "test")
        # C7: module selection would target every member; focused targets only the one whose directory
        # holds the trait, so the runner never depends on a filter that cannot match there.
        self.assertEqual(check["targets"], ["tests/Fake.Data.Tests/Fake.Data.Tests.csproj"])
        self.assertEqual(check["runner"], "dotnet")

    def test_a_sharded_project_is_planned_through_the_sharded_runner(self) -> None:
        result = self._plan("--paths", "src/FakeShard/Thing.cs", "--allow-unscoped", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("test: shardproj  (sharded runner)", result.stdout)

    # -- the sharded runner is LAUNCHED, not merely planned --------------------------------------
    #
    # The plan line `test: shardproj  (sharded runner)` is printed before any process starts, so a case
    # that asserts only the plan stays green while the branch cannot run. This branch used to launch
    # `pwsh -File scripts/test-sharded.ps1 -Project ... -ExtraFilter ... -Root ...`, and that file was
    # ported to `gk-core/scripts/test_sharded.py`, so every sharded-project check was launching a missing script.
    # These two cases drive the real CLI and observe the child, which the plan-only case cannot.

    def test_the_sharded_runner_is_launched_as_python_with_the_long_flags(self) -> None:
        """A planted runner that prints its own argv, so the flags that REACH the child are the proof."""
        planted = self._fixture.root / "scripts" / "test_sharded.py"
        saved = planted.read_bytes() if planted.is_file() else None
        planted.write_text(
            "import sys\n"
            "print('SHARDED-CHILD-ARGV ' + ' '.join(sys.argv[1:]))\n"
            "raise SystemExit(0)\n",
            encoding="utf-8")
        try:
            result = self._plan("--paths", "src/FakeShard/Thing.cs", "--allow-unscoped",
                                "--timeout", "600")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            # The child's echoed lines go to STDERR: `_echo` writes there, which is also where the
            # sharded runner's own shard-by-shard progress appears. Reading stdout -- where only the PLAN
            # is printed -- is why the first version of this case failed while the tool was correct.
            seen = result.stdout + result.stderr
            self.assertIn("SHARDED-CHILD-ARGV", seen,
                          "the sharded child never ran, so this proves nothing about the argv")
            line = next(ln for ln in seen.splitlines() if "SHARDED-CHILD-ARGV" in ln)
            argv = line.split("SHARDED-CHILD-ARGV", 1)[1]
            # The LONG flags of the ported tool, not the PowerShell spellings it replaced.
            for flag in ("--project", "--extra-filter", "--root"):
                self.assertIn(flag, argv, f"{flag} did not reach the child: {argv}")
            # The value, not just the flag: `--root` carries an absolute path and legitimately ends the
            # argv, so a truncated read of this line is what made it look absent in the first place.
            # Compared as a RESOLVED path, because `%TEMP%` is reached here through the 8.3 short-name
            # alias `NENESC~1` while the tool passes the resolved long form; as strings the same directory
            # compares unequal, and the case would be measuring the filesystem's naming instead.
            tokens = argv.split()
            self.assertIn("--root", tokens)
            self.assertEqual(pathlib.Path(tokens[tokens.index("--root") + 1]).resolve(),
                             pathlib.Path(self._fixture.root).resolve(),
                             f"the fixture root did not reach the child: {argv}")
            for power_shell_spelling in ("-Project", "-ExtraFilter", "-Root "):
                self.assertNotIn(power_shell_spelling, argv,
                                 f"a PowerShell spelling reached the Python runner: {argv}")
            self.assertNotIn(".ps1", line, "the sharded branch still names a retired .ps1")
            self.assertNotIn("pwsh", seen.lower(),
                             "the sharded branch still goes through PowerShell")
        finally:
            if saved is None:
                planted.unlink(missing_ok=True)
            else:
                planted.write_bytes(saved)

    def test_a_MISSING_sharded_runner_is_a_named_refusal_not_a_missing_executable(self) -> None:
        """Fail closed where a reader can see which file went away."""
        planted = self._fixture.root / "scripts" / "test_sharded.py"
        saved = planted.read_bytes() if planted.is_file() else None
        planted.unlink(missing_ok=True)
        try:
            result = self._plan("--paths", "src/FakeShard/Thing.cs", "--allow-unscoped",
                                "--timeout", "600")
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("VERIFY-CHANGE REFUSED", result.stderr)
            self.assertIn("TOOL-MISSING", result.stderr)
            self.assertIn("test_sharded.py", result.stderr,
                          "the refusal must name the file, not merely that a launch failed")
            self.assertIn("sharded-test", result.stderr, "the refusal must name the stage")
        finally:
            if saved is not None:
                planted.write_bytes(saved)

    def test_a_pytest_boundary_expands_its_fixed_file_selector_against_real_files(self) -> None:
        result = self._plan("--paths", "tools/fakepy/adapter.py", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "pytest")
        self.assertEqual(check["id"], "pyproj")
        # The boundary's `testFiles` is `test_a*.py`, and it is expanded against the REAL files under
        # the project's own test dir — so `test_beta.py` exists and is deliberately NOT selected. An
        # expansion that ignored the pattern and took every test in the dir would fail here.
        self.assertEqual(check["targets"], ["tools/fakepy/tests/test_alpha.py"])

    def test_a_changed_pytest_test_file_selects_itself_under_a_self_select_boundary(self) -> None:
        result = self._plan("--paths", "tools/fakepy/tests/test_alpha.py", "--allow-unscoped",
                            "--plan-only", "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "pytest")
        self.assertEqual(check["targets"], ["tools/fakepy/tests/test_alpha.py"])

    def test_a_changed_fixture_falls_through_to_the_module_pytest_run(self) -> None:
        result = self._plan("--paths", "tools/fakepy/tests/conftest.py", "--allow-unscoped",
                            "--plan-only", "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "pytest")
        # D2: a conftest/fixture change can affect every test in the project, so NeedsModule wins over
        # any explicit file list and `targets` is empty (the project's own tests dir is the selector).
        self.assertEqual(check["targets"], [])

    def test_one_pytest_check_per_project_even_when_two_boundaries_reach_it(self) -> None:
        result = self._plan("--paths", "tools/fakepy/adapter.py", "tools/fakepy/tests/test_alpha.py",
                            "--allow-unscoped", "--plan-only", "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        self.assertEqual(len([c for c in payload["checks"] if c["kind"] == "pytest"]), 1)

    def test_a_script_project_plans_a_wrapper_check_with_no_selector(self) -> None:
        result = self._plan("--paths", "web/fake/src/Thing.tsx", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "script")
        self.assertEqual(set(check), {"kind", "id", "level", "path"})
        self.assertEqual(check["id"], "scrproj")

    def test_a_full_level_owner_prints_as_having_no_local_check(self) -> None:
        result = self._plan("--paths", "data/generated/legacy.json", "--allow-unscoped", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("data/generated/legacy.json -> fake-full (full): no local check; "
                      "CI full evidence owns this input", result.stdout)
        self.assertNotIn("test:", result.stdout)

    def test_a_guard_script_verifies_itself_through_the_enforcement_catalog(self) -> None:
        result = self._plan("--paths", "scripts/guard-dal.ps1", "--allow-unscoped", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("scripts/guard-dal.ps1 -> enforcement-dal (module)", result.stdout)
        self.assertIn("guard: dal ", result.stdout)

    def test_an_explicit_exemption_prints_its_reason(self) -> None:
        result = self._plan("--paths", "scripts/legacy-tool.ps1", "--allow-unscoped", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("scripts/legacy-tool.ps1 -> exemption-legacy-ops (explicit exemption): "
                      "planted exemption with no local proof", result.stdout)
        payload = self._plan("--paths", "scripts/legacy-tool.ps1", "--allow-unscoped", "--plan-only",
                             "--format", "json")
        self.assertEqual(payload.returncode, 0, payload.stderr)
        self.assertEqual(json.loads(payload.stdout)["checks"], [])

    def test_a_markdown_path_also_gets_the_scoped_doc_citation_audit(self) -> None:
        result = self._plan("--paths", "docs/fake-note.md", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        check = next(c for c in payload["checks"] if c["kind"] == "doc-citations")
        self.assertEqual(set(check), {"kind", "id", "verificationId", "level", "path"})

    def test_a_deleted_markdown_path_still_selects_its_boundary_but_is_not_audited(self) -> None:
        result = self._plan("--deleted-paths", "docs/fake-note.md", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        self.assertIn("docs/fake-note.md", payload["paths"])
        self.assertEqual([c for c in payload["checks"] if c["kind"] == "doc-citations"], [])

    def test_the_json_plan_carries_the_four_contract_fields(self) -> None:
        result = self._plan("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only",
                            "--format", "json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        self.assertEqual(set(payload), {"paths", "selections", "checks", "fullEvidenceOwner"})
        self.assertEqual(payload["fullEvidenceOwner"], "CI/nightly/release")
        self.assertEqual(payload["paths"], ["src/Fake/Widget.cs"])
        self.assertEqual(
            set(payload["selections"][0]),
            {"path", "boundary", "project", "verificationId", "level", "guards", "testFiles", "selfSelect"})

    def test_json_is_also_reachable_through_the_bare_json_alias(self) -> None:
        result = self._plan("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["fullEvidenceOwner"], "CI/nightly/release")

    def test_a_path_may_be_repeated_or_batched_and_the_plan_is_identical(self) -> None:
        batched = self._plan("--paths", "src/Fake/Widget.cs", "docs/fake-note.md",
                             "--allow-unscoped", "--plan-only")
        repeated = self._plan("--paths", "src/Fake/Widget.cs", "--paths", "docs/fake-note.md",
                              "--allow-unscoped", "--plan-only")
        self.assertEqual(batched.returncode, 0, batched.stderr)
        self.assertEqual(batched.stdout, repeated.stdout)

    # -- refusals: named, non-zero, and never a plan that reads as success ---------------------

    def _refused(self, *args: str) -> subprocess.CompletedProcess:
        result = self._plan(*args)
        self.assertEqual(result.returncode, 1, f"expected a refusal exit, got {result.returncode}\n{result.stdout}")
        self.assertIn("VERIFY-CHANGE REFUSED", result.stderr)
        self.assertEqual(result.stdout, "", "a refusal must not print a plan")
        return result

    def test_an_unmapped_path_is_a_named_refusal_not_a_broad_suite(self) -> None:
        result = self._refused("--paths", "src/Unmapped/Thing.cs", "--allow-unscoped", "--plan-only")
        self.assertIn("BOUNDARY-MISSING", result.stderr)
        self.assertIn("do not run a broad suite as a fallback", result.stderr)

    def test_two_owners_tied_at_the_winning_specificity_are_refused_as_ambiguous(self) -> None:
        result = self._refused("--paths", "src/x.cs", "--allow-unscoped", "--plan-only")
        self.assertIn("BOUNDARY-AMBIGUOUS", result.stderr)
        self.assertIn("fake-tie-a, fake-tie-b", result.stderr)

    def test_a_path_outside_the_session_fence_is_refused(self) -> None:
        result = self._refused("--paths", "src/FakeModule/Thing.cs", "--session", "fake-active", "--plan-only")
        self.assertIn("PATH-OUTSIDE-SESSION", result.stderr)
        self.assertIn("fake-active", result.stderr)

    def test_an_in_scope_path_under_a_session_passes_the_fence(self) -> None:
        result = self._plan("--paths", "src/Fake/Widget.cs", "--session", "fake-active", "--plan-only")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_a_session_that_is_not_active_is_refused(self) -> None:
        result = self._refused("--paths", "src/Fake/Widget.cs", "--session", "fake-closed", "--plan-only")
        self.assertIn("SESSION-NOT-ACTIVE", result.stderr)

    def test_a_session_with_no_record_is_refused(self) -> None:
        result = self._refused("--paths", "src/Fake/Widget.cs", "--session", "no-such-session", "--plan-only")
        self.assertIn("SESSION-RECORD-MISSING", result.stderr)

    def test_omitting_the_session_without_the_maintainer_escape_is_refused(self) -> None:
        result = self._refused("--paths", "src/Fake/Widget.cs", "--plan-only")
        self.assertIn("SESSION-REQUIRED", result.stderr)

    def test_a_path_that_does_not_exist_is_refused_with_the_deleted_paths_hint(self) -> None:
        result = self._refused("--paths", "src/Fake/Gone.cs", "--allow-unscoped", "--plan-only")
        self.assertIn("PATH-NOT-FOUND", result.stderr)
        self.assertIn("--deleted-paths", result.stderr)

    def test_an_absolute_or_traversing_path_is_refused(self) -> None:
        for bad in ("C:/Windows/System32/drivers", "src/../../etc/passwd", "/etc/passwd"):
            with self.subTest(path=bad):
                result = self._refused("--paths", bad, "--allow-unscoped", "--plan-only")
                self.assertIn("PATH-NOT-RELATIVE", result.stderr)

    def test_no_paths_at_all_is_refused(self) -> None:
        self.assertIn("NO-PATHS", self._refused("--allow-unscoped", "--plan-only").stderr)

    def test_a_reviewed_diff_needs_both_refs_and_a_fence(self) -> None:
        self.assertIn("DIFF-FENCE-INCOMPLETE", self._refused(
            "--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only",
            "--diff-base-ref", "HEAD~1").stderr)
        self.assertIn("DIFF-FENCE-UNSCOPED", self._refused(
            "--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only",
            "--diff-base-ref", "HEAD~1", "--diff-head-ref", "HEAD").stderr)

    def test_a_registry_the_planner_does_not_understand_is_refused_not_misread(self) -> None:
        registry = json.loads((self._fixture.root / "scripts" / "verification-boundaries.v1.json")
                              .read_text(encoding="utf-8"))
        registry["schemaVersion"] = 999
        (self._fixture.root / "scripts" / "verification-boundaries.v1.json").write_text(
            json.dumps(registry), encoding="utf-8")
        try:
            result = self._refused("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only")
            self.assertIn("INTEGRITY-GUARD-FAILED", result.stderr)
        finally:
            registry["schemaVersion"] = 5
            (self._fixture.root / "scripts" / "verification-boundaries.v1.json").write_text(
                json.dumps(registry), encoding="utf-8")

    def test_a_missing_registry_is_refused_rather_than_reported_as_no_checks(self) -> None:
        registry = self._fixture.root / "scripts" / "verification-boundaries.v1.json"
        saved = registry.read_bytes()
        try:
            registry.unlink()
            result = self._refused("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only")
            self.assertIn("INTEGRITY-GUARD-FAILED", result.stderr)
        finally:
            registry.write_bytes(saved)

    def test_a_missing_default_profile_filter_is_refused(self) -> None:
        test_fast = self._fixture.root / "scripts" / "test_fast.py"
        saved = test_fast.read_bytes()
        try:
            test_fast.write_text("param()\n", encoding="utf-8")
            result = self._refused("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only")
            self.assertIn("DEFAULT-FILTER-UNREADABLE", result.stderr)
        finally:
            test_fast.write_bytes(saved)

    def test_a_non_positive_timeout_is_refused_before_anything_is_read(self) -> None:
        result = self._refused("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only",
                               "--timeout", "0")
        self.assertIn("TIMEOUT", result.stderr)

    def test_a_root_that_does_not_exist_is_a_named_refusal_not_a_crash(self) -> None:
        # Called directly, not through `_plan`: that helper appends its own `--root`, and argparse takes
        # the last one, so a `--root` passed through it would be silently overwritten.
        result = run_tool("--paths", "src/Fake/Widget.cs", "--allow-unscoped", "--plan-only",
                          "--root", str(self._fixture.root / "no-such-dir"), timeout=600)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("ROOT-UNRESOLVABLE", result.stderr)
        self.assertEqual(result.stdout, "")


# ============================================================================================
# Parity with the PowerShell original, which is still live
# ============================================================================================

def _mapped_existing_guard_script() -> str | None:
    """A registry-mapped ``scripts/guard-*`` path that exists on disk, or None.

    Derived rather than hardcoded on purpose. The parity test below used to name
    `scripts/guard-dal.ps1`, and commit 17d884e09 deleted that file when it ported the guard body to
    Python - so the test failed with PATH-NOT-FOUND on a path it had never meant to be about. The
    guard-port stream deletes ``.ps1`` files as its work, so any hardcoded one goes stale the next
    time a port lands, and it fails as a planner error rather than as the shape drift it looks like.

    A mapped path is required as well as an existing one: an unmapped path is refused by both
    planners as unmapped, which is a different contract from the one under test here.
    """
    registry = Path(__file__).resolve().parents[2] / "scripts" / "verification-boundaries.v1.json"
    try:
        doc = json.loads(registry.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    repo = Path(__file__).resolve().parents[2]
    candidates = sorted({
        raw
        for entry in doc.get("boundaries", [])
        for raw in (entry.get("paths") or [])
        if raw.startswith("scripts/guard-") and "*" not in raw and "?" not in raw
    })
    for raw in candidates:
        if (repo / raw).is_file():
            return raw
    return None


# NO skipIf here: the golden needs no PowerShell interpreter, which is the whole point of
# goldening the answers rather than re-running the oracle on every CI run.
class RetiredOracleParityTests(unittest.TestCase):
    """The evidence the PowerShell oracle produced, and the evidence that died with it.

    This class used to be `PowerShellParityTests`: three tests that ran `verify-change.ps1` and
    `lib/VerificationBoundaries.ps1` against their Python twins.

    WHAT SURVIVES, and is nearly as strong:

    * `test_the_python_library_reproduces_every_answer_the_retired_oracle_gave` compares the Python
      library against a COMMITTED GOLDEN of the PowerShell library's actual answers -- 84 pattern
      matches, 7 grammar verdicts, 7 specificities and 12 owner resolutions, captured by running the
      real `.ps1` immediately before it was deleted. A Python regression on any of those 110 answers
      still fails, and the golden does not move.

    * `test_the_golden_probe_set_is_still_exercised` fails if a case is present on one side only, so the
      golden cannot be quietly narrowed into a weaker assertion.

    WHAT DIED, and no replacement exists for it:

    * The two PLANNER comparisons ran both tools over the REAL registry -- every path shape, every
      branch the real tree exercises -- and asserted identical text and identical JSON. That is a
      cross-implementation check over a live corpus, and it cannot be reproduced with one
      implementation. `test_the_planner_selects_each_shape_it_is_asked_about` now asserts those shapes
      DIRECTLY, which pins the behaviour but proves nothing about agreement with another writer of the
      same rule.

    The specific loss is named rather than buried: a differential discovers bugs by asking questions
    nobody thought to ask. A golden only answers questions already recorded, and a direct assertion
    only checks the cases its author chose. Each of those three was capable of finding a class of defect
    the others cannot, and two of them are gone.
    """

    def setUp(self) -> None:
        self._tmp = Path(tempfile.mkdtemp(prefix="vb-parity-"))

    def tearDown(self) -> None:
        shutil.rmtree(self._tmp)
        self.assertFalse(self._tmp.exists())

    @staticmethod
    def _golden() -> dict:
        path = Path(__file__).resolve().parent / "fixtures" / "verification_boundaries_parity.json"
        if not path.is_file():
            raise AssertionError(
                f"{path} is missing. It holds the retired PowerShell library's answers and there is no "
                f"way to regenerate it now that the library is deleted -- which makes this fixture "
                f"load-bearing rather than optional.")
        return json.loads(path.read_text(encoding="utf-8"))

    def test_the_python_library_reproduces_every_answer_the_retired_oracle_gave(self) -> None:
        golden = self._golden()
        fragment = golden["fragment"]
        probes, patterns = golden["probes"], golden["patterns"]

        self.assertEqual([[case["p"], case["t"], vb.pattern_match(case["p"], case["t"])]
                          for case in golden["match"]],
                         [[case["p"], case["t"], case["m"]] for case in golden["match"]],
                         "pattern_match disagrees with the retired oracle on at least one case")
        self.assertEqual([[case["t"], vb.valid_pattern_grammar(case["t"])] for case in golden["grammar"]],
                         [[case["t"], case["g"]] for case in golden["grammar"]],
                         "valid_pattern_grammar disagrees with the retired oracle")
        self.assertEqual({case["t"]: vb.pattern_specificity(case["t"]) for case in golden["specificity"]},
                         {case["t"]: case["s"] for case in golden["specificity"]},
                         "pattern_specificity disagrees with the retired oracle")

        ours_resolve = []
        for path in probes:
            resolution = vb.resolve_owner(path, fragment)
            ours_resolve.append([path,
                                 "NONE" if resolution is None else "+".join(o["id"] for o in resolution.owners),
                                 -1 if resolution is None else resolution.specificity])
        self.assertEqual(ours_resolve, [[case["p"], case["owners"], case["specificity"]]
                                         for case in golden["resolve"]],
                         "resolve_owner disagrees with the retired oracle on at least one path")

    def test_the_golden_probe_set_is_still_exercised(self) -> None:
        """A golden that has been NARROWED is a weaker test wearing a stronger test's name.

        Nothing stops someone deleting the disagreeing case from the golden to make this suite green --
        the same mistake a test-to-fit-a-broken-tool makes, in the other direction. So the shape of the
        golden is checked against the fragment and probe lists it carries, which is what a narrowing
        would have to edit as well.
        """
        golden = self._golden()
        self.assertEqual(golden["patterns"],
                         [p for b in golden["fragment"] for p in b["paths"]])
        self.assertEqual([case["p"] for case in golden["match"]],
                         [p for p in golden["probes"] for _ in golden["patterns"]])
        self.assertEqual([case["t"] for case in golden["match"]],
                         [t for _ in golden["probes"] for t in golden["patterns"]])
        self.assertEqual([case["p"] for case in golden["resolve"]], golden["probes"])
        # A golden whose answers are all one value pins nothing, so the corpus has to DISCRIMINATE.
        answers = [case["m"] for case in golden["match"]]
        self.assertTrue(any(answers) and not all(answers),
                        "every recorded match answer is the same, so the golden pins no behaviour")

    def test_the_planner_selects_each_shape_it_is_asked_about(self) -> None:
        """What the two planner comparisons became: the shapes, asserted directly.

        The guard script is derived from the registry rather than hardcoded -- see
        `_mapped_existing_guard_script` for why a hardcoded one is a defect here rather than a
        convenience.
        """
        guard_script = _mapped_existing_guard_script()
        self.assertIsNotNone(
            guard_script,
            "no mapped scripts/guard-* exists on disk, so the guard-script shape is gone")
        paths = ["src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs",
                 "tests/FusionRpg.Data.Tests/Items/ItemSocketStoreTests.cs",
                 "AGENTS.md",
                 guard_script]
        deleted = ["src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs"]
        for fmt in ("text", "json"):
            with self.subTest(format=fmt):
                result = run_tool("--paths", *paths, "--deleted-paths", *deleted,
                                  "--allow-unscoped", "--plan-only", "--format", fmt)
                self.assertEqual(result.returncode, 0, result.stderr)
                if fmt == "text":
                    # The plan's ENVELOPE, not a particular owner id: every asked path gets a
                    # `path -> boundary (level)` line, and the plan names at least one check. Pinning a
                    # boundary NAME here would pin a population -- and a registry repair would then turn
                    # this red on a fix rather than on a defect, which is the failure mode the original
                    # comparison had too.
                    mapped = {line.split(" -> ")[0].strip()
                              for line in result.stdout.splitlines() if " -> " in line}
                    for path in paths:
                        self.assertIn(path, mapped, f"the plan did not map {path}")
                    levels = {m.group(1) for m in
                              (re.search(r"->\s+\S+\s+\((focused|module|seam|full)\)", line)
                               for line in result.stdout.splitlines()) if m}
                    self.assertTrue(levels <= {"focused", "module", "seam", "full"},
                                    f"the plan emitted levels outside the vocabulary: {levels}")
                    # The plan INDENTS its body, so each line is stripped before the prefix test --
                    # otherwise "  guard: dal" never starts with "guard:" and the assertion passes for
                    # the wrong reason on an empty plan.
                    self.assertTrue(any(line.strip().startswith(("guard:", "test:", "pytest:"))
                                        for line in result.stdout.splitlines()),
                                    f"the plan named no check, so it verifies nothing:\n{result.stdout}")
                else:
                    plan = json.loads(result.stdout)
                    selected = {s["path"]: s["boundary"] for s in plan["selections"]}
                    for path in paths:
                        self.assertIn(path, selected)
                    self.assertTrue(all(isinstance(v, str) and v for v in selected.values()))
                    self.assertTrue(plan["checks"], "a plan with no checks verifies nothing")

    def test_an_unmapped_path_is_refused_rather_than_answered_with_a_broad_suite(self) -> None:
        """What the refusal comparison became: the refusal, asserted directly.

        The probe is `.editorconfig` BY SCOPE, not by accident -- it used to be `README.md`, which was
        unmapped only until 2026-09-26, and the test went red on a fix rather than on a defect. A probe
        chosen because it happens to be unmapped pins a POPULATION, which rots the moment the population
        is corrected; the registry's rows cover code and documents, and dotfile configuration is neither.
        """
        result = run_tool("--paths", UNMAPPED_PROBE, "--allow-unscoped", "--plan-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "", "a refusal must not also print a plan")
        self.assertIn("BOUNDARY-MISSING", result.stderr)
        self.assertIn("Add an owner mapping or an explicit registry exemption; do not "
                      "run a broad suite as a fallback.", result.stderr)


def _normalise(text: str) -> str:
    return "\n".join(line.rstrip() for line in text.replace("\r\n", "\n").splitlines()).strip()


if __name__ == "__main__":
    unittest.main()
