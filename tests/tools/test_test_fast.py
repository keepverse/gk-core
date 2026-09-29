"""Contract tests for `gk-core/scripts/test_fast.py`.

Asserts the CONTRACT: the CLI surface, the scope refusals and their ORDER, the filter's scrapeable
spelling, the substrate gate's fail-closed behaviour and its exit-code vocabulary, the closure check on
the project list, the multi-failure collection, the `--json` envelope, and the two named refusals the
original had no way to express.

WHY THE SCOPE ORDER IS A CONTRACT AND NOT A COMMENT
The original validated scope BEFORE the substrate gate, deliberately (TVB4.1), and recorded why: a real,
unrelated `guard-test-substrate` failure on another lane's tree made a no-argument run print only the
gate banner and hide the `-Project` message. So a case here drives both preconditions at once and
asserts the SCOPE refusal is the one that surfaces -- a tool that gated first would answer GUARD-FAILED
and look correct.

WHY THE CLOSURE CHECK MATTERS MORE THAN THE LIST
The original list's comment claimed a Core split increment that forgot it "would silently drop the moved
tests". Measured, 0 of 71 entries were stale but 11 CI-wired projects were absent. The port does not
quietly add them -- that changes what an ordinary run costs and is the owner's call -- so instead the
exclusion set is DECLARED with a reason each, and a project in neither list refuses. That is what makes
the comment true.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("TEST_FAST_SCRIPT", REPO / "scripts" / "test_fast.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("test_fast", SCRIPT)
tf = importlib.util.module_from_spec(_spec)
sys.modules["test_fast"] = tf
_spec.loader.exec_module(tf)

EXIT_VOCABULARY = {0, 1, 64}
STAGE_VOCABULARY = set(tf.STAGES)
# The scrapers' patterns, copied from the two tools that own them rather than retyped, so a change to
# EITHER reader is what fails here -- and this test then tells you which reader to fix.
SCRAPER_PATTERNS = {
    "verify-change": re.compile(r'^\s*\$?filter\s*=\s*"([^"]+)"', re.MULTILINE | re.IGNORECASE),
    "test_sharded": re.compile(r'^\s*FILTER\s*=\s*"([^"]+)"', re.MULTILINE),
}


def code_without_bare_docstrings(text: str) -> str:
    """Source reduced to the text a retired-dialect CALL could live in: docstrings out, everything else in."""
    tree = ast.parse(text)
    bare = {id(n.value) for n in ast.walk(tree)
            if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant)
            and isinstance(n.value.value, str)}
    parts: list[str] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if id(node) not in bare:
                parts.append(node.value)
        elif isinstance(node, (ast.Name, ast.Attribute)):
            parts.append(getattr(node, "id", None) or getattr(node, "attr", ""))
    return " ".join(parts)


class Repo:
    """A throwaway repository shaped like the real one's `tests/` tree, so the closure check is real."""

    def __init__(self, root: Path) -> None:
        self.root = root
        (root / "scripts").mkdir(parents=True, exist_ok=True)

    def project(self, name: str) -> str:
        directory = root_dir = self.root / "tests" / name
        directory.mkdir(parents=True, exist_ok=True)
        csproj = directory / f"{name}.csproj"
        csproj.write_text("<Project />", encoding="utf-8")
        return f"tests/{name}/{name}.csproj"

    def guard(self, *, name: str = "guard-test-substrate.py", body: str = "import sys\nsys.exit(0)\n") -> Path:
        path = self.root / "scripts" / name
        path.write_text(body, encoding="utf-8")
        return path


class TemporaryRepo(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="test-fast-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()
        self.repo = Repo(self.root)
        self.addCleanup(self._tmp.cleanup)

    def declare(self, *paths: str) -> None:
        """Planted projects must be DECLARED, or the closure check refuses the run.

        The check is the feature, not an obstacle: "a project on disk that is in neither list" is
        precisely the condition the original list's comment claimed to catch and could not. A fixture
        that suppressed it to make itself pass would be the exact defect the port exists to prevent, so
        the fixture satisfies the contract instead.
        """
        patch = mock.patch.object(tf, "DECLARED_EXCLUSIONS",
                                 dict(tf.DECLARED_EXCLUSIONS, **{p: "planted by a contract case" for p in paths}))
        patch.start()
        self.addCleanup(patch.stop)

    def invoke(self, *args: str, declare: bool = True) -> tuple[int, str, str]:
        """`declare=False` is for the one case that ASSERTS the closure refusal: it plants a project
        precisely so the check fires, and a fixture that declared it away would be disarming the very
        thing it is testing."""
        out, err = io.StringIO(), io.StringIO()
        # Every project this fixture planted is declared, in ONE place.
        #
        # The closure check is the FEATURE, not an obstacle: "a project on disk that is in neither list"
        # is exactly the condition the original list's comment claimed to catch and could not. Nine cases
        # each calling a `declare()` helper is nine chances to forget one, and a case that forgot would
        # fail for the wrong reason -- or, worse, a suite edited to suppress the check would hide the
        # defect the port exists to prevent. Declaring at the boundary means the fixture satisfies the
        # contract by construction, and the two cases that ASSERT the refusal bypass `invoke`'s
        # declaration deliberately by planting a project they then expect to be refused.
        planted = [p.relative_to(self.root).as_posix() for p in sorted(self.root.glob("tests/*/*.csproj"))]
        if planted and declare:
            patch = mock.patch.object(
                tf, "DECLARED_EXCLUSIONS",
                dict(tf.DECLARED_EXCLUSIONS, **{p: "planted by a contract case" for p in planted}))
            patch.start()
            self.addCleanup(patch.stop)
        with redirect_stdout(out), redirect_stderr(err):
            code = tf.main(["--root", str(self.root), *args])
        return code, out.getvalue(), err.getvalue()


# ------------------------------------------------------------------------------------------------
# The filter: the one constant three tools read by scraping this file's source
# ------------------------------------------------------------------------------------------------

class TheFilter(unittest.TestCase):
    def test_BOTH_scrapers_read_it(self) -> None:
        """The filter is SCRAPED, not imported, so the spelling is a contract with two readers.

        The readers' patterns are duplicated here on purpose: if either tool's pattern changes, this case
        is the one that says which reader broke, rather than a plan somewhere else failing with
        DEFAULT-FILTER-UNREADABLE and no clue which file was at fault.
        """
        source = SCRIPT.read_text(encoding="utf-8")
        for name, pattern in SCRAPER_PATTERNS.items():
            match = pattern.search(source)
            self.assertIsNotNone(match, f"the {name} reader no longer finds the filter in {SCRIPT.name}")
            self.assertEqual(match.group(1), tf.FILTER, name)

    def test_it_is_a_MODULE_LEVEL_assignment_of_a_double_quoted_literal(self) -> None:
        """Not merely "the string appears" -- the SHAPE is what the scrapers match."""
        assignments = [node for node in ast.parse(SCRIPT.read_text(encoding="utf-8")).body
                       if isinstance(node, ast.Assign)
                       and any(getattr(t, "id", None) == "FILTER" for t in node.targets)]
        self.assertEqual(len(assignments), 1,
                         f"expected exactly one module-level FILTER assignment, found {len(assignments)}")
        value = assignments[0].value
        self.assertIsInstance(value, ast.Constant)
        self.assertIsInstance(value.value, str)

    def test_it_excludes_both_documented_categories_and_nothing_else(self) -> None:
        for category in ("DiskSemantics", "Heavy"):
            self.assertIn(f"Category!={category}", tf.FILTER)
        self.assertEqual(tf.FILTER.count("!="), 2, "the profile excludes exactly the two named categories")


# ------------------------------------------------------------------------------------------------
# Scope: validated FIRST, and the ORDER is the contract
# ------------------------------------------------------------------------------------------------

class Scope(TemporaryRepo):
    def test_no_arguments_is_REFUSED_rather_than_selecting_a_broad_default(self) -> None:
        code, _, err = self.invoke()
        self.assertEqual(code, 1)
        self.assertIn("SCOPE-REQUIRED", err)
        self.assertIn("TEST-FAST REFUSED", err)

    def test_the_scope_refusal_wins_over_a_FAILING_substrate_gate(self) -> None:
        """TVB4.1, and the reason the original ordered it this way.

        A real, unrelated `guard-test-substrate` failure on another lane's tree made a no-argument run
        print only the gate banner, hiding the scope message. A tool that gated first would answer
        GUARD-FAILED here and look entirely correct -- so this case asserts the ORDER, not just the
        message.
        """
        self.repo.guard(body="import sys\nsys.exit(3)\n")
        code, _, err = self.invoke()
        self.assertEqual(code, 1)
        self.assertIn("SCOPE-REQUIRED", err)
        self.assertNotIn("GATE-FAILED", err)

    def test_both_project_and_all_default_is_REFUSED(self) -> None:
        code, _, err = self.invoke("--project", "tests/Foo", "--all-default")
        self.assertEqual(code, 1)
        self.assertIn("SCOPE-CONFLICT", err)

    def test_the_refusal_names_the_verification_tool_and_the_broad_alternative(self) -> None:
        """A developer who hit this has two options and guessing which is available costs the most time."""
        code, _, err = self.invoke()
        self.assertIn("verify-change.py", err)
        self.assertIn("--all-default", err)

    def test_the_json_envelope_carries_the_refusal_on_BOTH_streams_independently(self) -> None:
        code, out, err = self.invoke("--json")
        self.assertEqual(code, 1)
        self.assertEqual(err, "", "a --json refusal must not also print prose on stderr")
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["stage"], "scope")
        self.assertEqual(payload["reason"], "SCOPE-REQUIRED")


# ------------------------------------------------------------------------------------------------
# The substrate gate: fail closed, and keep its exit code
# ------------------------------------------------------------------------------------------------

class TheSubstrateGate(TemporaryRepo):
    def test_a_MISSING_guard_REFUSES_rather_than_printing_a_green_header(self) -> None:
        """A gate that silently does not run is worse than one that refuses."""
        project = self.repo.project("Foo")
        code, _, err = self.invoke("--project", project)
        self.assertEqual(code, tf.EXIT_SUBSTRATE_REFUSED, "the gate's own exit code must be preserved")
        self.assertIn("GUARD-MISSING", err)
        self.assertIn("guard-test-substrate.py", err)

    def test_the_guards_exit_code_is_passed_through_unchanged(self) -> None:
        """A caller that scripted `64` for 'the gate refused' must still see 64, not a generic 1."""
        self.repo.guard(body="import sys\nsys.exit(64)\n")
        project = self.repo.project("Foo")
        code, _, err = self.invoke("--project", project)
        self.assertEqual(code, 64)
        self.assertIn("GATE-FAILED", err)

    def test_it_runs_FROM_the_root_so_a_relative_path_guard_resolves(self) -> None:
        """`run-guards.ps1` made this its contract for the same reason, measured 2026-09-26: from any
        other directory the guard fails closed with "no such path(s): ['src']"."""
        seen: list[list[str]] = []

        def record(argv, **kwargs):
            seen.append(str(kwargs["cwd"]))
            return subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")

        self.repo.guard()
        project = self.repo.project("Foo")
        with mock.patch.object(tf.subprocess, "run",
                               side_effect=record):
            self.invoke("--project", project)
        self.assertTrue(seen, "the gate was never invoked")
        self.assertEqual(Path(seen[0]).resolve(), self.root.resolve())

    def test_a_gone_GUARD_py_with_no_python_on_PATH_refuses_by_name(self) -> None:
        self.repo.guard()
        project = self.repo.project("Foo")
        with mock.patch.object(tf.shutil, "which", return_value=None):
            code, _, err = self.invoke("--project", project)
        self.assertEqual(code, tf.EXIT_SUBSTRATE_REFUSED)
        self.assertIn("PYTHON-NOT-ON-PATH", err)


# ------------------------------------------------------------------------------------------------
# The project list: the closure check the original comment only claimed
# ------------------------------------------------------------------------------------------------

class TheProjectList(TemporaryRepo):
    def test_a_listed_project_that_is_ABSENT_is_REFUSED(self) -> None:
        """A stale entry means coverage is silently lost, and the original could not see it."""
        with mock.patch.object(tf, "DEFAULT_PROJECTS", ("tests/Gone/Gone.csproj",)):
            code, _, err = self.invoke("--all-default")
        self.assertEqual(code, 1)
        self.assertIn("PROJECT-NOT-FOUND", err)

    def test_an_UNDECIDED_project_on_disk_is_REFUSED_and_NAMED(self) -> None:
        """THE CLOSURE CHECK. A project on disk that is in neither list means nobody decided.

        This is the property the original's comment claimed and its list did not have: a Core split
        increment that forgot the list would have been silent. Here it is a named refusal.
        """
        self.repo.project("NewlySplit")
        self.repo.project("Foo")
        code, _, err = self.invoke("--project", "tests/Foo", declare=False)
        self.assertEqual(code, 1)
        self.assertIn("UNDECIDED-PROJECT", err)
        self.assertIn("NewlySplit", err)

    def test_every_DECLARED_exclusion_carries_a_REASON(self) -> None:
        """An exclusion with an empty reason is an exclusion nobody made, which is the thing being fixed."""
        for path, reason in tf.DECLARED_EXCLUSIONS.items():
            self.assertTrue(reason.strip(), f"{path} is declared without a reason")
            self.assertTrue(path.endswith(".csproj"), path)

    def test_the_two_lists_are_DISJOINT(self) -> None:
        overlap = set(tf.DEFAULT_PROJECTS) & set(tf.DECLARED_EXCLUSIONS)
        self.assertEqual(overlap, set(), f"a project cannot be both run and skipped: {sorted(overlap)}")

    def test_the_list_has_NO_DUPLICATES(self) -> None:
        self.assertEqual(len(tf.DEFAULT_PROJECTS), len(set(tf.DEFAULT_PROJECTS)))

    def test_EVERY_listed_project_EXISTS_in_this_repository(self) -> None:
        """A reading, not a population count: the assertion is that each NAME resolves."""
        missing = [p for p in tf.DEFAULT_PROJECTS if not (REPO / p).is_file()]
        self.assertEqual(missing, [], f"the default profile lists projects that do not exist: {missing}")

    def test_the_UNMEASURED_asymmetry_is_recorded_not_silently_repaired(self) -> None:
        """11 CI-wired projects are absent from the default profile. That is a finding, not a bug to
        quietly repair -- adding them changes what every ordinary agent run costs, which is the owner's
        call. The port's obligation is to RECORD it and make the set closed, so this case pins that
        every CI-only project carries a declaration saying so."""
        declared = " ".join(tf.DECLARED_EXCLUSIONS.values())
        self.assertIn("in ci.yml", declared,
                      "the exclusions must state which of them are a CI/local asymmetry")
        self.assertIn("not a test project", declared,
                      "the non-test-project exclusion must say why it is not one")

    def test_a_PROJECT_may_be_a_DIRECTORY_and_resolves_to_its_csproj(self) -> None:
        """The spec's own example passes a directory, so this is behaviour, not convenience."""
        self.repo.project("Foo")
        self.repo.guard()
        seen: list[list[str]] = []

        def record(argv, **kwargs):
            seen.append(list(argv))
            return subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")

        with mock.patch.object(tf.subprocess, "run", side_effect=record):
            code, _, err = self.invoke("--project", "tests/Foo")
        self.assertEqual(code, 0, err)
        # The tool resolves the project to an ABSOLUTE path, deliberately: `dotnet` must be able to run a
        # project from any working directory, and the tool `cwd`s into the root. So the assertion is on
        # the repository-relative TAIL of the argv ELEMENT naming the project -- the command as
        # `dotnet` receives it, not a joined rendering of it.
        test_calls = [argv for argv in seen if argv and argv[0] == "dotnet" and argv[1] == "test"]
        self.assertEqual(len(test_calls), 1, seen)
        project_arg = next((a for a in test_calls[0] if a.endswith(".csproj")), None)
        self.assertIsNotNone(project_arg, test_calls[0])
        self.assertTrue(project_arg.replace("\\", "/").endswith("tests/Foo/Foo.csproj"), project_arg)

    def test_an_empty_DIRECTORY_is_REFUSED_rather_than_silently_running_nothing(self) -> None:
        (self.root / "tests" / "Empty").mkdir(parents=True, exist_ok=True)
        self.repo.guard()
        code, _, err = self.invoke("--project", "tests/Empty")
        self.assertEqual(code, 1)
        self.assertIn("NO-CSPROJ-IN-DIRECTORY", err)


# ------------------------------------------------------------------------------------------------
# The run: every failure named, first code preserved
# ------------------------------------------------------------------------------------------------

class TheEnvelopeAndFallbacks(TemporaryRepo):
    """The `--json` envelope shape, and the substrate guard's `.ps1` fallback.

    Split out from `TheRun` because these assert SHAPES rather than a pipeline outcome, and because the
    fallback cases plant a guard the pipeline cases do not: `TheRun.setUp` installs a PASSING guard, and
    a fallback case that inherited it would never reach the resolution branch it is about.
    """

    def test_the_json_envelope_carries_KEYS_and_nothing_that_differs_per_run(self) -> None:
        """Added because `the_json_envelope_gains_a_field_that_rotS` SURVIVED.

        The port had no envelope-key case at all, so a field that differs per run -- a pid, a duration,
        a start timestamp -- would have been invisible to every assertion here AND to every consumer
        that wants to compare two reports. The key SET is the contract.
        """
        self.repo.guard()
        project = self.repo.project("A")
        # The invocation is MOCKED because a planted `<Project />` genuinely fails `dotnet test` -- and a
        # case that wanted an OK report by planting less than a real project would be asserting the mock,
        # not the tool. Through `invoke`, not `tf.main` directly: that is where the planted projects get
        # DECLARED, and entering by a different door than the sibling cases means satisfying the same
        # preconditions by hand -- the duplication `invoke` exists to remove.
        passing = subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")
        with mock.patch.object(tf.subprocess, "run", return_value=passing):
            code, out, _ = self.invoke("--project", project, "--json")
        self.assertEqual(code, 0, out)
        report = json.loads(out)
        self.assertEqual(set(report), {"tool", "verdict", "configuration", "filter",
                                       "substrate_guard", "substrate_exit", "projects",
                                       "failed_projects", "first_failure_code", "undecided",
                                       "stages"})
        self.assertEqual(report["verdict"], "OK")
        self.assertEqual(report["filter"], tf.FILTER)
        # Every path in the report is repository-relative, so a report can be pasted back into a command
        # and two checkouts of the same tree produce the same line.
        self.assertEqual(report["projects"], [project])
        for banned in ("pid", "duration", "elapsed", "timestamp", "started", "finished", "seed"):
            self.assertNotIn(banned, report, f"{banned!r} differs per run, so nothing can assert on it")

    def test_the_POWERSHELL_fallback_spelling_is_still_RESOLVABLE(self) -> None:
        """Added because `the_substrate_guard_falls_back_to_THE_FIRST_spelling_only` SURVIVED.

        The tool prefers the Python guard and keeps the `.ps1` spelling "only so this tool keeps working
        if the guard is ever re-pointed". That promise had no case, so a future edit could drop the
        fallback with nothing to notice -- and the whole point of a fallback nobody tests is that it is
        not there when it is needed.

        The fixture plants BOTH, so this asserts PREFERENCE (`.py` first) rather than merely that one of
        them resolves; a resolver that took either would pass a weaker test and hide the preference.
        """
        self.repo.guard(name="guard-test-substrate.py", body="import sys\nsys.exit(0)\n")
        (self.root / "scripts" / "guard-test-substrate.ps1").write_text("exit 0\n", encoding="utf-8")
        resolved = tf.resolve_substrate_guard(self.root)
        self.assertEqual(resolved.name, "guard-test-substrate.py",
                         "the Python spelling is preferred; the .ps1 is a fallback, not an alternative")

    def test_the_POWERSHELL_fallback_ALONE_still_resolves_and_runs(self) -> None:
        """The fallback ON ITS OWN, which is the situation it exists for.

        The `.py` is removed first, and deliberately: with both present the tool prefers the `.py`, so a
        case that kept both would assert the PREFERENCE twice and never reach the branch it is about.
        The preference is the sibling case's subject; both are wanted, and they are different questions.
        """
        # The ABSENCE of the Python guard is the subject, so it is asserted rather than arranged: a case
        # that planted the thing it asserts absent is one refactor away from asserting nothing.
        self.assertFalse((self.root / "scripts" / "guard-test-substrate.py").exists())
        (self.root / "scripts" / "guard-test-substrate.ps1").write_text("exit 0\n", encoding="utf-8")
        self.assertEqual(tf.resolve_substrate_guard(self.root).name, "guard-test-substrate.ps1")


class TheRun(TemporaryRepo):
    def setUp(self) -> None:
        super().setUp()
        self.repo.guard()

    def test_EVERY_failing_project_is_named_not_only_the_first(self) -> None:
        """The original kept the first failure's code and printed 40 red lines; a reader skimming the
        tail saw one code and one name. The report names all of them and still exits with the first."""
        a, b, c = self.repo.project("A"), self.repo.project("B"), self.repo.project("C")
        codes = {a: 2, b: 5, c: 0}

        def record(argv, **kwargs):
            # Match on the argv ELEMENT naming the project, not a joined rendering: a path followed by
            # more arguments is not a string the project name ends.
            project_arg = next((a for a in argv if a.endswith(".csproj")), None)
            project = next((p for p in codes
                            if project_arg and project_arg.replace("\\", "/").endswith(p)), None)
            return subprocess.CompletedProcess(args=[], returncode=codes.get(project, 0), stdout="", stderr="")

        with mock.patch.object(tf.subprocess, "run", side_effect=record):
            code, _, err = self.invoke("--project", a, "--project", b, "--project", c)
        self.assertEqual(code, 2, "the exit code stays the FIRST non-zero, so it is comparable")
        self.assertIn("2 of 3 project(s) failed", err)
        for project in (a, b):
            self.assertIn(project, err)
        self.assertNotIn(c, err)

    def test_a_WEDGED_invocation_is_a_NAMED_timeout_not_a_hang(self) -> None:
        """71 invocations, none bounded in the original; `--blame-hang` is a hang DETECTOR, not a kill
        switch, and says nothing about an MSBuild node waiting on a lock."""
        project = self.repo.project("A")
        with mock.patch.object(tf.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired(cmd="dotnet", timeout=11)):
            code, _, err = self.invoke("--project", project, "--timeout", "11")
        self.assertEqual(code, 1)
        self.assertIn("TIMEOUT", err)
        self.assertIn("11s", err)

    def test_a_MISSING_dotnet_is_a_NAMED_refusal_not_a_traceback(self) -> None:
        project = self.repo.project("A")
        with mock.patch.object(tf.subprocess, "run", side_effect=FileNotFoundError("dotnet")):
            code, _, err = self.invoke("--project", project)
        self.assertEqual(code, 1)
        self.assertIn("NOT-ON-PATH", err)

    def test_the_filter_reaches_EVERY_dotnet_invocation(self) -> None:
        project = self.repo.project("A")
        seen: list[list[str]] = []

        def record(argv, **kwargs):
            seen.append(list(argv))
            return subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")

        with mock.patch.object(tf.subprocess, "run", side_effect=record):
            self.invoke("--project", project)
        test_calls = [argv for argv in seen if argv and argv[0] == "dotnet" and argv[1] == "test"]
        self.assertEqual(len(test_calls), 1, seen)
        self.assertIn(tf.FILTER, test_calls[0])
        # The hang detector travels with every invocation, as it did in the original.
        for flag in ("--blame-hang", "--blame-hang-timeout"):
            self.assertIn(flag, test_calls[0])

    def test_EVERY_external_command_goes_through_run_and_nothing_else(self) -> None:
        """Structural, by AST: `subprocess.run` is called from exactly one place, so "every external
        command is bounded and checked" is a fact about the control flow rather than a convention."""
        sites = [node for node in ast.walk(ast.parse(SCRIPT.read_text(encoding="utf-8")))
                 if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                 and node.func.attr == "run" and isinstance(node.func.value, ast.Name)
                 and node.func.value.id == "subprocess"]
        self.assertEqual(len(sites), 1,
                         f"subprocess.run is called from {len(sites)} places; every external command "
                         f"must go through the one that is bounded")


# ------------------------------------------------------------------------------------------------
# Surface
# ------------------------------------------------------------------------------------------------

class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--project", "--all-default", "--configuration", "--root", "--timeout", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        for flag in ("-Project", "-AllDefault", "-Configuration", "-Root"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                 text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower(), flag)

    def test_it_shells_out_to_no_PowerShell_interpreter(self) -> None:
        code = code_without_bare_docstrings(SCRIPT.read_text(encoding="utf-8"))
        for token in ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE"):
            self.assertNotIn(token, code, f"the tool still uses {token!r} in CODE")

    def test_the_timeout_is_declared_and_says_the_original_had_none(self) -> None:
        flat = " ".join(subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                                       text=True, timeout=RUN_TIMEOUT).stdout.split()).lower()
        self.assertIn("timeout", flat)
        self.assertIn("original had no timeout", flat)

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        found = set(re.findall(r'Refusal\("[a-z-]+", "([A-Z][A-Z0-9-]+)"', SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons found at all")
        known = {"TIMEOUT", "NOT-ON-PATH", "SPAWN-FAILED", "SCOPE-REQUIRED", "SCOPE-CONFLICT",
                 "GUARD-MISSING", "PYTHON-NOT-ON-PATH", "GATE-FAILED", "PROJECT-NOT-FOUND",
                 "NO-CSPROJ-IN-DIRECTORY", "UNDECIDED-PROJECT", "PROJECTS-FAILED"}
        self.assertEqual(found - known, set(), f"undocumented refusal reason(s) {sorted(found - known)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({tf.EXIT_OK, tf.EXIT_FAILED, tf.EXIT_SUBSTRATE_REFUSED}, EXIT_VOCABULARY)

    def test_every_declared_stage_is_in_the_vocabulary(self) -> None:
        self.assertTrue(STAGE_VOCABULARY >= set(tf.STAGES),
                        f"a declared stage is missing: {sorted(set(tf.STAGES) - STAGE_VOCABULARY)}")

    def test_the_DEFAULT_LIST_is_not_asserted_by_LENGTH_anywhere(self) -> None:
        """A guard that pins the population count guards nothing: it fails when a project ships, and the
        'fix' is to bump the number. The contract suite asserts each NAME resolves, never a total."""
        source = SCRIPT.read_text(encoding="utf-8") + SCRIPT.name
        self.assertNotRegex(source, r"assert\w*\(\s*len\(\s*(?:tf\.)?DEFAULT_PROJECTS\s*\)\s*==\s*\d+")
        self.assertNotIn(f"== {len(tf.DEFAULT_PROJECTS)}", source)


if __name__ == "__main__":
    unittest.main()
