"""Contract tests for `.claude/cmdc-agents/scripts/bcu212_full_run.py`.

A DETACHED JOB, so the contract is about the readings it records and the resume rule.

THE ROSTER COUNT IS THE POINT. The run's population is the ROSTER's own length, NEVER a literal:
a hardcoded count would silently under-run the corpus the moment the roster grows. The four
failure modes (command nonzero, empty, non-integer, non-positive) each have their own named
ABORTED/ROSTER_FAILURE pair, and a bad roster command is a hard stop -- never let its output
become the full-run count by accident.

THE RESUME RULE IS THE OTHER POINT. The resume pass runs ONLY after a successful full run, because
resume safety is the SHARED ledger, not a flag: re-invoking after a failed full run would regenerate
subjects the ledger already records. A port that always ran the resume pass would prove nothing and
cost hours.

The subprocess is substituted through the tool's PRIVATE `_RUN` seam, because it is a process-wide
module: a test that patches it reaches every other test in this project.
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
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("BCU212_FULL_RUN_SCRIPT",
                             REPO / ".claude" / "cmdc-agents" / "scripts" / "bcu212_full_run.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_bcu212_full_run.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("bcu212_full_run", SCRIPT)
bfr = importlib.util.module_from_spec(_spec)
sys.modules["bcu212_full_run"] = bfr
_spec.loader.exec_module(bfr)
_PRISTINE = {"_RUN": bfr._RUN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if bfr._RUN is not _PRISTINE["_RUN"]:
            self.fail(f"_RUN was still substituted after {self.id()}: {bfr._RUN!r}")


class FakeRun:
    """A scripted `subprocess.run`. `answers` maps a command substring to
    (returncode, stdout, stderr); unmatched commands fail the test."""

    def __init__(self, answers: dict[str, tuple[int, str, str]]) -> None:
        self.answers = answers
        self.calls: list[list[str]] = []

    def __call__(self, cmd, **kwargs):
        self.calls.append(list(cmd))
        joined = " ".join(cmd)
        for needle, (code, out, err) in self.answers.items():
            if needle in joined:
                return subprocess.CompletedProcess(cmd, code, out, err)
        raise AssertionError(f"unscripted command: {joined}")

    def commands_matching(self, needle: str) -> list[list[str]]:
        return [c for c in self.calls if needle in " ".join(c)]


def happy_answers(smoke_code: int = 0, full_code: int = 0, resume_code: int = 0,
                  check_code: int = 0, report_code: int = 0) -> dict[str, tuple[int, str, str]]:
    return {
        "rev-parse --short": (0, "abc1234\n", ""),
        "rev-parse --abbrev-ref": (0, "corpus-bcu212\n", ""),
        "load_roster": (0, "904\n", ""),
        "_j9_batch_run.py 2": (smoke_code, "", ""),
        "_j9_batch_run.py 904": (full_code, "", ""),
        "seedsmith check": (check_code, "", ""),
        "bcu212-report.py": (report_code, "", ""),
    }


class TheRosterValidation(SeamGuard):
    """The original's four failure modes, each a named refusal."""

    def test_a_positive_integer_is_the_population(self) -> None:
        self.assertEqual(bfr.validate_roster_output("904"), 904)
        self.assertEqual(bfr.validate_roster_output("  904\n"), 904)

    def test_an_EMPTY_output_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(bfr.Refusal) as caught:
            bfr.validate_roster_output("")
        self.assertEqual(caught.exception.reason, "ROSTER-COUNT-FAILED")
        self.assertIn("roster_count_empty", caught.exception.detail)

    def test_a_WHITESPACE_output_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(bfr.Refusal) as caught:
            bfr.validate_roster_output("   \n")
        self.assertIn("roster_count_empty", caught.exception.detail)

    def test_a_NON_INTEGER_output_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(bfr.Refusal) as caught:
            bfr.validate_roster_output("ninety-four")
        self.assertIn("roster_count_not_integer", caught.exception.detail)

    def test_a_ZERO_count_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(bfr.Refusal) as caught:
            bfr.validate_roster_output("0")
        self.assertIn("roster_count_non_positive", caught.exception.detail)

    def test_a_NEGATIVE_count_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(bfr.Refusal) as caught:
            bfr.validate_roster_output("-3")
        self.assertIn("roster_count_non_positive", caught.exception.detail)


class TheJob(SeamGuard):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="bfr-contract-")
        self.addCleanup(self._tmp.cleanup)
        self.worktree = Path(self._tmp.name)

    def _run_job(self, answers: dict[str, tuple[int, str, str]], argv: list[str] | None = None):
        fake = FakeRun(answers)
        with mock.patch.object(bfr, "_RUN", fake):
            out = io.StringIO()
            err = io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = bfr.main(argv if argv is not None else
                                ["--worktree", str(self.worktree)])
        return code, out.getvalue(), err.getvalue(), fake

    def test_a_fully_successful_job_exits_0_and_prints_FINISHED(self) -> None:
        code, out, _, fake = self._run_job(happy_answers())
        self.assertEqual(code, bfr.EXIT_OK)
        self.assertIn("HEAD=abc1234  branch=corpus-bcu212", out)
        self.assertIn(f"WORKTREE={self.worktree.resolve()}", out)
        self.assertIn("STARTED=", out)
        self.assertIn("roster_species=904", out)
        self.assertIn("smoke_exit=0", out)
        self.assertIn("full_exit=0", out)
        self.assertIn("resume_exit=0", out)
        self.assertIn("check_exit=0", out)
        self.assertIn("report_exit=0", out)
        self.assertIn("FINISHED=", out)
        self.assertIn("=== VERDICT JOB END ===", out)
        self.assertNotIn("FAILED=", out)

    def test_the_resume_pass_runs_after_a_successful_full_run(self) -> None:
        code, out, _, fake = self._run_job(happy_answers())
        self.assertEqual(code, bfr.EXIT_OK)
        resume_calls = fake.commands_matching("_j9_batch_run.py 904")
        # the full run and the resume pass are the SAME command, re-invoked
        self.assertEqual(len(resume_calls), 2, "the resume pass did not re-invoke the full command")
        self.assertIn("=== resume pass", out)

    def test_the_resume_pass_is_SKIPPED_after_a_failed_full_run(self) -> None:
        code, out, _, fake = self._run_job(happy_answers(full_code=3))
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("resume_exit=skipped_after_failed_full_run", out)
        self.assertNotIn("=== resume pass", out)
        full_calls = fake.commands_matching("_j9_batch_run.py 904")
        self.assertEqual(len(full_calls), 1, "the resume pass ran after a failed full run")
        self.assertIn("FAILED=", out)
        self.assertIn("=== VERDICT JOB FAILED ===", out)

    def test_a_roster_command_that_exits_nonzero_is_a_hard_stop(self) -> None:
        answers = happy_answers()
        answers["load_roster"] = (1, "", "boom")
        code, out, _, fake = self._run_job(answers)
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("ABORTED=roster_count_failed", out)
        self.assertIn("ROSTER_FAILURE=roster_count_command_nonzero exit=1", out)
        self.assertNotIn("_j9_batch_run.py", out, "the smoke run started after a failed roster count")

    def test_a_roster_that_prints_nothing_is_a_hard_stop(self) -> None:
        answers = happy_answers()
        answers["load_roster"] = (0, "   \n", "")
        code, out, _, _ = self._run_job(answers)
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("ROSTER_FAILURE=roster_count_empty", out)

    def test_a_roster_that_prints_a_non_integer_is_a_hard_stop(self) -> None:
        answers = happy_answers()
        answers["load_roster"] = (0, "many\n", "")
        code, out, _, _ = self._run_job(answers)
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("ROSTER_FAILURE=roster_count_not_integer", out)

    def test_a_roster_that_prints_zero_is_a_hard_stop(self) -> None:
        answers = happy_answers()
        answers["load_roster"] = (0, "0\n", "")
        code, out, _, _ = self._run_job(answers)
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("ROSTER_FAILURE=roster_count_non_positive", out)

    def test_a_failed_smoke_ABORTS_with_the_smokes_exit_code(self) -> None:
        code, out, _, _ = self._run_job(happy_answers(smoke_code=7))
        self.assertEqual(code, 7, "the smoke's exit code is the job's, as the original's was")
        self.assertIn("ABORTED=smoke_failed", out)
        self.assertNotIn("_j9_batch_run.py 904", out, "the full run started after a failed smoke")

    def test_a_failed_census_FAILs_the_job_even_after_green_runs(self) -> None:
        code, out, _, _ = self._run_job(happy_answers(check_code=2))
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("check_exit=2", out)
        self.assertIn("FAILED=", out)
        self.assertIn("=== VERDICT JOB FAILED ===", out)

    def test_a_failed_report_FAILs_the_job(self) -> None:
        code, out, _, _ = self._run_job(happy_answers(report_code=4))
        self.assertEqual(code, bfr.EXIT_FAILED)
        self.assertIn("report_exit=4", out)

    def test_stale_files_are_cleared_before_the_run(self) -> None:
        batch_results = self.worktree / "tools" / "seedsmith" / "_j9_batch_run_results.json"
        final_report = self.worktree / "tasks" / "reports" / "BCU2.12-full-run.json"
        batch_results.parent.mkdir(parents=True, exist_ok=True)
        final_report.parent.mkdir(parents=True, exist_ok=True)
        batch_results.write_text('{"stale": true}', encoding="utf-8")
        final_report.write_text('{"stale": true}', encoding="utf-8")
        code, out, _, _ = self._run_job(happy_answers())
        self.assertEqual(code, bfr.EXIT_OK)
        self.assertFalse(batch_results.exists(), "the stale batch results were not cleared")
        self.assertFalse(final_report.exists(), "the stale final report was not cleared")
        self.assertIn("cleared stale", out)

    def test_the_batch_command_carries_the_roster_count_not_a_literal(self) -> None:
        """The run's population is the ROSTER's own length, NEVER a literal."""
        code, out, _, fake = self._run_job(happy_answers())
        self.assertEqual(code, bfr.EXIT_OK)
        full_calls = fake.commands_matching("_j9_batch_run.py 904")
        self.assertEqual(len(full_calls), 2, "the full run and resume must use the roster count")
        for call in full_calls:
            self.assertEqual(call[-1], "904", "the full-run count is not the roster's length")

    def test_the_python_command_is_trimmed_and_defaults(self) -> None:
        code, out, _, fake = self._run_job(happy_answers(), argv=["--python", "  "])
        self.assertEqual(code, bfr.EXIT_OK)
        roster_calls = fake.commands_matching("load_roster")
        self.assertEqual(roster_calls[0][0], "python", "a blank --python must fall back to 'python'")

    def test_a_NON_POSITIVE_smoke_REFUSES(self) -> None:
        code, _, err, _ = self._run_job(happy_answers(), argv=["--smoke", "0"])
        self.assertEqual(code, bfr.EXIT_REFUSED)
        self.assertIn("INVALID-SMOKE", err)

    def test_every_subprocess_call_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(bfr, "_RUN", run):
            with redirect_stdout(io.StringIO()):
                bfr.main([])
        self.assertTrue(seen, "no subprocess calls were made")
        for call in seen:
            self.assertIsNotNone(call["kwargs"].get("timeout"),
                                 f"unbounded: {' '.join(call['cmd'][:3])}")
            self.assertIsNotNone(call["kwargs"].get("capture_output"))


class TheRefusals(SeamGuard):
    def test_a_command_TIMEOUT_is_a_NAMED_refusal(self) -> None:
        def timing_out(cmd, **kwargs):
            raise subprocess.TimeoutExpired(" ".join(cmd), 120)

        with mock.patch.object(bfr, "_RUN", timing_out):
            with self.assertRaises(bfr.Refusal) as caught:
                bfr.run_job(Path(tempfile.gettempdir()), 2, "python",
                            Path(__file__).resolve().parents[2])
        self.assertEqual(caught.exception.reason, "COMMAND-TIMED-OUT")
        self.assertIn("60s", caught.exception.detail)

    def test_a_command_that_cannot_START_is_a_NAMED_refusal(self) -> None:
        def crashing(cmd, **kwargs):
            raise OSError("no such file")

        with mock.patch.object(bfr, "_RUN", crashing):
            with self.assertRaises(bfr.Refusal) as caught:
                bfr.run_job(Path(tempfile.gettempdir()), 2, "python",
                            Path(__file__).resolve().parents[2])
        self.assertEqual(caught.exception.reason, "RUNNER-CRASHED")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = bfr.main(["--smoke", "0"])
        self.assertEqual(code, bfr.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - bfr.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - bfr.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(bfr.EXIT_OK, 0)
        self.assertEqual(bfr.EXIT_FAILED, 1)
        self.assertEqual(bfr.EXIT_REFUSED, 64)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Worktree", "-Smoke", "-Python"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("bcu212-full-run.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "lastexitcode", "roster", "resume"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--worktree", "--smoke", "--python", "--json"):
            self.assertIn(flag, out, flag)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib", "time"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "bfr":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_NO_case_STARTS_a_patch_it_cannot_STOP(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned = []
        for cls in (n for n in ast.walk(tree) if isinstance(n, ast.ClassDef)):
            for func in (n for n in cls.body if isinstance(n, ast.FunctionDef)
                         and n.name.startswith("test")):
                for node in ast.walk(func):
                    if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                            and node.func.attr in ("start", "stop")):
                        owned = any(node in ast.walk(stmt)
                                    for parent in ast.walk(func)
                                    if isinstance(parent, (ast.With, ast.AsyncWith))
                                    for stmt in parent.body)
                        if not owned:
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}")
        self.assertEqual(unowned, [], "\n".join(unowned))


if __name__ == "__main__":
    unittest.main()
