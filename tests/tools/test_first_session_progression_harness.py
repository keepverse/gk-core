"""Contract tests for `gk-core/scripts/first_session_progression_harness.py`.

Asserts the contract: the declared suite set, the CLI surface, the refusal vocabulary, the exit-code
vocabulary, the `--json` envelope, and the two properties the differential against the retired `.ps1`
found in the port itself.

WHAT THE DIFFERENTIAL FOUND IN THE PORT, and why each case below exists
  * **A CASE-SENSITIVE EXTENSION TEST PUT THE REAL `dotnet.exe` BACK ON THE ARGV.** `run_suite` asked
    `executable.endswith((".exe", ".cmd", ".bat"))`, so the resolved `dotnet.CMD` failed it and the tool
    fell back to the bare token `dotnet`. `CreateProcess` resolves a bare name by appending `.exe` ONLY,
    so it found the real SDK and the port ran a 4.7-second real test instead of the recorder the
    differential had put on PATH. The tool reported `exit=0` while running something other than what was
    resolved -- a silent substitution, which is the worst outcome a launcher can have.
    `a_resolved_CMD_is_what_actually_runs` pins it by spawning a `.cmd` recorder and reading the log.

  * **A TRUNCATED FILTER, TRANSCRIBED FROM A DISPLAY THAT CUT LINES AT 130 CHARACTERS.** The original's
    third filter is
    `FullyQualifiedName~Sim_victory_emits_game_driven_result_and_unlocks_ordered_onboarding_reveals`
    (94 chars, on a 159-char source line). I copied the visible prefix, so the port ran a BROADER filter
    than the original -- and a broader filter silently runs MORE tests while the harness's own evidence
    line still names the narrower claim. `the_declared_filters_are_the_ORIGINALS_exactly` pins each one,
    and `filters_are_not_PREFIXES_of_each_other` says why a prefix would be the wrong thing to accept.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("FIRST_SESSION_HARNESS_SCRIPT",
                            REPO / "scripts" / "first_session_progression_harness.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("first_session_harness", SCRIPT)
h = importlib.util.module_from_spec(_spec)
sys.modules["first_session_harness"] = h
_spec.loader.exec_module(h)

REFUSAL_REASONS = {"DOTNET-NOT-ON-PATH", "PROJECT-MISSING", "DOTNET-INVOCATION-FAILED"}

# The three suites, with the exact argv each must produce. Written out rather than derived from the
# module, because a case that reads its expectation from the module cannot fail.
EXPECTED = [
    ("onboarding-data", "tests/FusionRpg.Data.Tests", "FullyQualifiedName~Onboarding"),
    ("onboarding-http", "tests/FusionRpg.Server.Tests",
     "FullyQualifiedName~OnboardingEndpointsTests"),
    ("simulator-onboarding", "tests/FusionRpg.E2E.Tests",
     "FullyQualifiedName~Sim_victory_emits_game_driven_result_and_unlocks"
     "_ordered_onboarding_reveals"),
]


# Declarations and method names, read from the real sources on disk so a renamed class is visible.
_DECLARATIONS = [re.compile(r"\bclass\s+(\w+)"),
                 re.compile(r"\b(?:public|internal)\s+(?:async\s+)?\w[\w<>\[\],. ]*\s(\w+)\s*\(")]


def matching_names(project: str, needle: str) -> set[str]:
    """`Class.member` names under `project` whose last segment matches `needle`, case-insensitively.

    DotNet's `--filter FullyQualifiedName~X` matches the FULLY QUALIFIED name, so both the class name
    and the member name count -- which is why `Onboarding` selects a whole class and
    `Sim_..._reveals` selects one method. The comparison is `Class.member` for a member and `Class.Class`
    for a class, so a filter that names a class verbatim matches.
    """
    base = REPO / project
    if not base.is_dir():
        return set()
    found: set[str] = set()
    for path in sorted(base.rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        for pattern in _DECLARATIONS:
            for match in pattern.finditer(text):
                name = match.group(1)
                if needle.lower() in name.lower():
                    found.add(f"{path.stem}.{name}")
    return found


class TheDeclaration(unittest.TestCase):
    def test_the_SUITE_SET_is_the_ONE_the_original_declared(self) -> None:
        actual = [(s.name, s.project, s.filter) for s in h.SUITES]
        self.assertEqual(actual, EXPECTED,
                         "the declared set is the whole contract: a harness that quietly stopped "
                         "running the simulator suite would report green while covering two thirds of "
                         "the evidence it names")

    def test_each_FILTER_selects_a_NON_EMPTY_set_of_tests(self) -> None:
        """A filter naming nothing is a green run of ZERO tests, which this repository has a rule about: a
        filtered run that executes nothing is RED, not OK. Measured against the real sources on disk, so
        a renamed or deleted test class fails here rather than turning the harness into a no-op."""
        for suite in h.SUITES:
            needle = suite.filter.split("~", 1)[1]
            names = matching_names(suite.project, needle)
            self.assertTrue(names,
                            f"{suite.name}: nothing under {suite.project} matches {needle!r}, so the "
                            f"filter runs zero tests and the harness reports green")

    def test_a_MEMBER_FILTER_is_a_COMPLETE_name_not_merely_a_PREFIX_of_one(self) -> None:
        """WHY this exists, and it is worth being precise about what the risk is.

        I transcribed the simulator filter from a display that cut each line at 130 characters, so I
        copied the PREFIX of a 94-character filter that sits on a 159-character source line. MEASURED,
        that costs nothing today: the truncated needle and the full one both select exactly
        `SoulsE2ETests.Sim_victory_emits_game_driven_result_and_unlocks_ordered_onboarding_reveals`,
        because only one test matches either.

        So the truncation is a LATENT broadening, not a present wrong selection -- and the case that
        actually protects the port is the exact-string assertion in
        `test_the_SUITE_SET_is_the_ONE_the_original_declared`. This case states the hazard in the form
        that catches a recurrence: a filter that is merely a PREFIX of a real test name will silently
        start selecting more tests the day a second one shares that prefix, and nothing about the run
        would say so.

        Scoped to filters that name a METHOD (`_`-bearing needles). `FullyQualifiedName~Onboarding` is
        deliberately a class-level filter: it selects everything under `OnboardingProjectionTests` and
        `OnboardingCheckpointStoreTests`, and that breadth is CORRECT -- the Data suite means all
        onboarding coverage, not one class. So requiring a verbatim name of it would be asserting a rule
        this harness does not have, and the non-empty case above is what protects it.
        """
        checked = 0
        for suite in h.SUITES:
            needle = suite.filter.split("~", 1)[1]
            if "_" not in needle:
                continue  # a class-level filter: breadth is intended, see the docstring
            names = matching_names(suite.project, needle)
            checked += 1
            # The needle is the BARE member name; `matching_names` returns `Class.member`. Comparing the
            # needle against the qualified name is comparing the wrong side and fails on a correct
            # declaration -- the member name is the trailing segment.
            members = {name.rsplit(".", 1)[-1] for name in names}
            self.assertIn(needle, members,
                          f"{suite.name}: {needle!r} is not a real member name verbatim; it is at best a "
                          f"prefix of {sorted(members)[:3]}, so it will broaden silently the day a "
                          f"second test shares the prefix")
        self.assertGreaterEqual(checked, 1,
                                "no member-shaped filter was checked, so this case proves nothing here")

    def test_every_FILTER_names_a_TEST_that_exists(self) -> None:
        """A filter naming nothing is a green suite run of ZERO tests, which is the failure the program
        has a rule about: a filtered run that executes nothing is RED, not OK."""
        for suite in h.SUITES:
            needle = suite.filter.split("~", 1)[1]
            self.assertTrue(needle, suite.name)
            stem = needle.split("_")[0]
            sources = list((REPO / suite.project).rglob("*.cs")) if (
                REPO / suite.project).is_dir() else []
            self.assertTrue(
                any(stem.lower() in path.stem.lower() or needle.lower() in path.read_text(
                    encoding="utf-8", errors="replace").lower() for path in sources),
                f"{suite.name}: nothing under {suite.project} matches {needle!r}, so the filter would "
                f"run zero tests and the harness would report green")

    def test_the_PROJECTS_exist_on_disk(self) -> None:
        for suite in h.SUITES:
            self.assertTrue((REPO / suite.project).is_dir(), suite.project)

    def test_each_SUITE_produces_a_dotnet_test_argv(self) -> None:
        argv = h.SUITES[0].argv(no_build=False)
        self.assertEqual(argv[:2], ["dotnet", "test"])
        self.assertIn("--filter", argv)
        self.assertNotIn("--no-build", argv)
        self.assertIn("--no-build", h.SUITES[0].argv(no_build=True))

    def test_the_EVIDENCE_line_names_what_the_three_suites_cover(self) -> None:
        for phrase in ("Player 1", "match.result", "species reveal", "replay/idempotency",
                       "acknowledgement-only claim"):
            self.assertIn(phrase, h.EVIDENCE, phrase)


class TheLauncher(unittest.TestCase):
    """argv[0] is the RESOLVED executable. This is the class the differential earned."""

    def test_a_resolved_CMD_is_what_actually_runs(self) -> None:
        """A `.cmd` recorder on PATH, and the tool must invoke IT.

        `CreateProcess` resolves a bare `dotnet` by appending `.exe` only, so a tool that spawns the bare
        token silently runs the REAL SDK instead of the shim -- a substitution the tool reports as its
        own success. The log is the evidence, because that is the only place the difference shows.
        """
        with tempfile.TemporaryDirectory(prefix="harness-cmd-") as tmp:
            bindir = Path(tmp) / "bin"
            bindir.mkdir()
            log = Path(tmp) / "argv.log"
            log.touch()
            (bindir / "dotnet.cmd").write_text(
                f'@echo off\r\necho %*>>"{log}"\r\nexit /b 0\r\n', encoding="ascii")
            env = dict(os.environ)
            env["PATH"] = str(bindir) + os.pathsep + env["PATH"]

            proc = subprocess.run([sys.executable, str(SCRIPT), "--json", "--no-build"],
                                  capture_output=True, text=True, timeout=RUN_TIMEOUT,
                                  cwd=str(REPO), env=env)
            recorded = [line.strip() for line in log.read_text(encoding="utf-8").splitlines()
                        if line.strip()]

        self.assertEqual(proc.returncode, 0, proc.stdout[-1500:] + proc.stderr[-800:])
        self.assertEqual(len(recorded), len(EXPECTED),
                         f"the .cmd shim must receive every invocation; it received {recorded}")
        for (_, project, suite_filter), line in zip(EXPECTED, recorded):
            self.assertIn(f"test {project}", line)
            self.assertIn(suite_filter, line)
            self.assertIn("--no-build", line)

    def test_the_RESOLVED_tool_is_never_a_bare_token(self) -> None:
        """Structural, by AST. `endswith((".exe", ".cmd", ".bat"))` looks like a guard and is one --
        case-sensitively, so it fails on the uppercase `.CMD` that `shutil.which` actually returns. The
        property is "the resolved path is argv[0], unconditionally", and this asserts there is no
        conditional to get wrong."""
        tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
        function = next(n for n in ast.walk(tree)
                        if isinstance(n, ast.FunctionDef) and n.name == "run_suite")
        argv = next(stmt for stmt in ast.walk(function)
                    if isinstance(stmt, ast.Assign)
                    and any(isinstance(t, ast.Name) and t.id == "argv" for t in stmt.targets))
        self.assertIsInstance(argv.value, ast.List, "argv must be a plain list")
        first = argv.value.elts[0]
        self.assertIsInstance(first, ast.Name, f"argv[0] must be the resolved executable name: {first!r}")
        self.assertEqual(first.id, "executable",
                         "argv[0] must be the RESOLVED executable, with no extension test around it")

    def test_it_calls_subprocess_run_and_nothing_else(self) -> None:
        """Every spawn goes through one `subprocess.run(..., timeout=...)`. A second spawn site, or one
        without the bound, is a suite with no timeout -- the defect the retirement was about."""
        source = SCRIPT.read_text(encoding="utf-8")
        tree = ast.parse(source)
        sites = [n for n in ast.walk(tree)
                 if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                 and n.func.attr == "run" and isinstance(n.func.value, ast.Name)
                 and n.func.value.id == "subprocess"]
        self.assertEqual(len(sites), 1, f"expected exactly one subprocess.run site, found {len(sites)}")
        keywords = {kw.arg for kw in sites[0].keywords}
        self.assertIn("timeout", keywords, "the single spawn site must carry a timeout")
        self.assertIn("capture_output", keywords)
        for banned in ("os.system", "subprocess.Popen", "subprocess.call", "shell=True"):
            self.assertNotIn(banned, source, banned)


class TheRun(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="harness-contract-")
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        for _, project, _ in EXPECTED:
            (self.root / project).mkdir(parents=True)

    def invoke(self, *args: str) -> tuple[int, dict]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = h.main(["--root", str(self.root), "--json", *[str(a) for a in args]])
        try:
            return code, json.loads(out.getvalue())
        except json.JSONDecodeError:
            self.fail(f"stdout was not JSON:\n{out.getvalue()}\n{err.getvalue()}")

    def test_a_MISSING_project_REFUSES_BEFORE_any_suite_runs(self) -> None:
        (self.root / EXPECTED[2][1]).rmdir()
        with mock_run() as recorder:
            code, payload = self.invoke()
        self.assertEqual(code, h.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "PROJECT-MISSING")
        self.assertEqual(recorder.calls, [],
                         "a typo in the LAST suite must not cost two real test runs to discover")

    def test_a_MISSING_project_refusal_NAMES_EACH_one_it_is_missing(self) -> None:
        """M15's kill.

        `dotnet test <nonexistent>` fails as a build error naming a path the caller never mistyped, and
        it does so only after the earlier suites have run. A refusal that says "a project is missing"
        without saying WHICH leaves the same hunt, just sooner. Named as a list because more than one can
        be absent, and a refusal that reported only the first would be equally unhelpful.
        """
        for absent_index in (0, 1, 2):
            (self.root / EXPECTED[absent_index][1]).rmdir()
            try:
                with self.assertRaises(h.Refusal) as caught:
                    h.check_projects(self.root, h.SUITES)
                self.assertEqual(caught.exception.reason, "PROJECT-MISSING")
                detail = caught.exception.detail
                self.assertIn(EXPECTED[absent_index][1], detail,
                              f"the refusal must name suite {absent_index}'s project: {detail}")
                self.assertIn(str(self.root), detail,
                              "the refusal must name the ROOT it looked under, or a relative path "
                              "gives no clue which checkout was inspected")
            finally:
                (self.root / EXPECTED[absent_index][1]).mkdir(parents=True, exist_ok=True)

    def test_SEVERAL_missing_projects_are_ALL_named(self) -> None:
        for index in (0, 2):
            (self.root / EXPECTED[index][1]).rmdir()
        try:
            with self.assertRaises(h.Refusal) as caught:
                h.check_projects(self.root, h.SUITES)
            detail = caught.exception.detail
            for index in (0, 2):
                self.assertIn(EXPECTED[index][1], detail,
                              "every missing project must be named, not only the first")
        finally:
            for index in (0, 2):
                (self.root / EXPECTED[index][1]).mkdir(parents=True, exist_ok=True)

    def test_a_GREEN_sequence_reports_OK_and_runs_EVERY_suite_in_ORDER(self) -> None:
        with mock_run(exit_codes=[0, 0, 0]) as recorder:
            code, payload = self.invoke()
        self.assertEqual(code, h.EXIT_OK)
        self.assertEqual(payload["verdict"], "OK")
        self.assertIsNone(payload["failedStep"])
        self.assertEqual(len(recorder.calls), len(EXPECTED))
        for call, (_, project, suite_filter) in zip(recorder.calls, EXPECTED):
            self.assertIn(project, call)
            self.assertIn(suite_filter, call)

    def test_a_RED_first_suite_STOPS_the_sequence_and_names_the_project(self) -> None:
        with mock_run(exit_codes=[3]) as recorder:
            code, payload = self.invoke()
        self.assertEqual(code, h.EXIT_FAILED)
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertEqual(payload["failedStep"], 1)
        self.assertEqual(len(recorder.calls), 1,
                         "the suites are independent, so running the rest after a red buys nothing and "
                         "costs the operator the whole sequence's time on every failure")
        self.assertEqual(payload["steps"][0]["exit"], 3,
                         "the failing suite's OWN code, captured once")

    def test_a_TIMEOUT_is_a_named_verdict_that_names_the_suite(self) -> None:
        with mock_run(timeout_at=2) as recorder:
            code, payload = self.invoke()
        self.assertEqual(code, h.EXIT_FAILED)
        self.assertTrue(payload["steps"][1]["timed_out"])
        self.assertEqual(payload["failedStep"], 2)
        self.assertEqual(payload["steps"][1]["name"], EXPECTED[1][0])
        self.assertEqual(len(recorder.calls), 2, "a timed-out suite must not be followed by the next")

    def test_a_TIMEOUT_is_distinguishable_from_a_suite_that_RETURNED_red(self) -> None:
        """M7's kill.

        A timeout and an ordinary red suite both end the run with a non-zero exit, so the only thing
        separating them is the `timed_out` flag and the output. Without it a wedged suite is reported as
        "suite 2 exited 1", which sends the operator to read test output that does not exist.
        """
        with mock_run(timeout_at=1):
            _, timed = self.invoke()
        with mock_run(exit_codes=[1]):
            _, red = self.invoke()
        self.assertTrue(timed["steps"][0]["timed_out"])
        self.assertFalse(red["steps"][0]["timed_out"])
        self.assertIn("no result within", timed["steps"][0]["output"],
                      "a timeout must say so in its own output, not just in a flag")
        self.assertNotIn("no result within", red["steps"][0]["output"])

    def test_a_SPAWN_that_CANNOT_start_is_a_REFUSAL_not_a_red_suite(self) -> None:
        """M11's kill.

        A child that never started is a CONFIGURATION fault: nothing ran, so there is no test output to
        read and no failing suite to fix. Reporting it as "suite 1 exited 1" points the operator at a
        failure that did not happen.
        """
        def cannot_start(command, **kwargs):
            raise FileNotFoundError(2, "The system cannot find the file specified")

        with mock.patch.object(h.subprocess, "run", side_effect=cannot_start):
            with self.assertRaises(h.Refusal) as caught:
                h.execute(self.root, False, 60)
        self.assertEqual(caught.exception.reason, "DOTNET-INVOCATION-FAILED")
        self.assertEqual(caught.exception.exit_code, h.EXIT_REFUSED)
        self.assertIn(EXPECTED[0][1], caught.exception.detail,
                      "the refusal must name the suite it could not start, not just the OS error")

    def test_a_NON_POSITIVE_timeout_is_REFUSED_before_any_work(self) -> None:
        with mock_run() as recorder:
            code, payload = self.invoke("--timeout", 0)
        self.assertEqual(code, h.EXIT_USAGE)
        self.assertEqual(payload["reason"], "INVALID-TIMEOUT")
        self.assertEqual(recorder.calls, [])

    def test_the_SEQUENCE_SHARES_ONE_timeout_budget(self) -> None:
        """Three suites must not be able to consume three times the caller's `--timeout` -- that is the
        unbounded behaviour the retirement was about, and a flat per-suite share is exactly how it comes
        back. Asserted as a SUM, so the property is the bound rather than any one suite's share."""
        budgets: list[int] = []

        def record(command, **kwargs):
            budgets.append(kwargs["timeout"])
            return subprocess.CompletedProcess(command, 0, "", "")

        with mock.patch.object(h.subprocess, "run", side_effect=record):
            h.execute(self.root, False, 100)
        self.assertEqual(len(budgets), len(EXPECTED))
        self.assertLessEqual(sum(budgets), 100 + len(EXPECTED),
                             f"the budgets must fit one --timeout: {budgets}")
        self.assertGreater(budgets[0], 0)

    def test_the_COLD_START_share_is_ONLY_for_the_first_suite(self) -> None:
        """The first invocation restores and builds the whole graph; the rest only run tests. Handing the
        cold share to every suite would starve the first while multiplying the total, and handing it to
        none of them would let the first be cut off mid-build."""
        self.assertGreater(h.budget_for(0, 3, 100), h.budget_for(1, 3, 100))
        self.assertEqual(h.budget_for(1, 3, 100), h.budget_for(2, 3, 100),
                         "the post-cold-start suites get equal shares")
        self.assertEqual(h.budget_for(0, 1, 100), 100,
                         "a single-suite sequence gets the whole budget, not a share of it")

    def test_the_JSON_envelope_carries_KEYS_and_nothing_that_ROTS(self) -> None:
        with mock_run(exit_codes=[0, 0, 0]):
            _, payload = self.invoke()
        self.assertEqual(set(payload), {"tool", "verdict", "exitCode", "failedStep", "declaredSuites",
                                       "evidence", "steps"})
        for banned in ("pid", "duration_ms", "started", "timestamp"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run")
        # Per-step wall clock is real information about WHICH suite was slow, so it belongs in the step.
        # It is not asserted here as a value -- only its PRESENCE is a contract, because a machine cannot
        # assert what a duration should be.
        self.assertEqual(set(payload["steps"][0]),
                         {"name", "project", "argv", "exit", "seconds", "timed_out", "output"})

    def test_a_STEP_reports_its_OWN_seconds_and_not_a_shared_total(self) -> None:
        with mock_run(exit_codes=[0, 0, 0]):
            _, payload = self.invoke()
        for step in payload["steps"]:
            self.assertIn("seconds", step)
            self.assertIsInstance(step["seconds"], (int, float))


class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--no-build", "--root", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_switch(self) -> None:
        for flag in ("-NoBuild", "-Root", "-TimeoutSec"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_a_missing_dotnet_REFUSES_BY_NAME(self) -> None:
        with mock.patch.object(shutil, "which", return_value=None):
            with self.assertRaises(h.Refusal) as caught:
                h.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_the_missing_dotnet_refusal_NAMES_the_tool_and_how_to_FIX_it(self) -> None:
        """M10's kill.

        A refusal that does not name the tool leaves the operator with nothing to install, and one that
        does not say what to do about it leaves them reading a sentence. The original called `& dotnet`
        and let the shell decide, so the tool's name came from whatever PowerShell's own error happened
        to say -- which is not a contract anything can rely on.
        """
        with mock.patch.object(shutil, "which", return_value=None):
            with self.assertRaises(h.Refusal) as caught:
                h.resolve_dotnet()
        detail = caught.exception.detail
        self.assertIn("dotnet", detail)
        self.assertIn("SDK", detail, "the refusal must say WHAT to install, not only what is missing")
        self.assertIn("PATH", detail)

    def test_a_resolved_tool_is_returned_verbatim(self) -> None:
        """The counterweight, so the case above cannot be satisfied by refusing unconditionally."""
        resolved = shutil.which("dotnet")
        self.assertIsNotNone(resolved, "dotnet is not on PATH here, so this case proves nothing")
        self.assertEqual(h.resolve_dotnet(), resolved)

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', SCRIPT.read_text(encoding="utf-8")))
        found |= set(re.findall(r'Refusal\("[A-Z_]+",\s*"([A-Z-]+)"', SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - REFUSAL_REASONS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({h.EXIT_OK, h.EXIT_FAILED, h.EXIT_REFUSED, h.EXIT_USAGE}, {0, 1, 64, 64})

    def test_the_MODULE_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("first-session-progression-harness.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        for reason in ("NO TIMEOUT", "LASTEXITCODE"):
            self.assertIn(reason, head, f"the docstring omits the {reason} defect")


class mock_run:  # noqa: N801 - used as a context manager, lowercase reads like a fixture
    """Record the spawns, answering each with the next exit code.

    A fixture rather than a `Mock` because the point of every case above is WHICH argv reached the
    child and IN WHAT ORDER, and a `Mock` makes that readable only by accident.
    """

    def __init__(self, exit_codes: list[int] | None = None, timeout_at: int | None = None) -> None:
        self.exit_codes = list(exit_codes or [])
        self.timeout_at = timeout_at
        self.calls: list[list[str]] = []
        self._patcher = None

    def __enter__(self):
        outer = self

        def record(command, **kwargs):
            outer.calls.append(list(command))
            if outer.timeout_at is not None and len(outer.calls) == outer.timeout_at:
                raise subprocess.TimeoutExpired(cmd=command, timeout=kwargs.get("timeout", 1),
                                                output=b"", stderr=b"")
            code = outer.exit_codes.pop(0) if outer.exit_codes else 0
            return subprocess.CompletedProcess(command, code, "output", "")

        self._patcher = mock.patch.object(h.subprocess, "run", side_effect=record)
        self._patcher.start()
        return self

    def __exit__(self, *exc):
        self._patcher.stop()
        return False


if __name__ == "__main__":
    unittest.main()
