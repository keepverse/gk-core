#!/usr/bin/env python3
"""Contract tests for gk-core/scripts/guard-bench-compile.py.

These pin the CONTRACT, never a population: the CLI surface, the exit codes, the refusal
vocabulary, the --json shape, and the two behaviours the PowerShell original got wrong (an
unbounded build, and a temp delete whose failure was swallowed). None of them asserts a line
count or a message body, so a rewording cannot break them and a real regression can.
"""
from __future__ import annotations

import importlib.util
import io
import json
import os
import stat
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GUARD_PATH = REPO_ROOT / "scripts" / "guard-bench-compile.py"


def _load_guard():
    spec = importlib.util.spec_from_file_location("guard_bench_compile", GUARD_PATH)
    assert spec and spec.loader, f"{GUARD_PATH} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules["guard_bench_compile"] = module
    spec.loader.exec_module(module)
    return module


guard = _load_guard()


def _run(argv: list[str]) -> tuple[int, str, str]:
    """Invoke main() capturing both streams, the way a caller would observe it."""
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = guard.main(argv)
    return code, out.getvalue(), err.getvalue()


class RefusalIsNamedAndNonZero(unittest.TestCase):
    """A precondition that cannot be satisfied is a named REFUSAL with exit 64, never a pass and
    never a bare non-zero."""

    def test_a_missing_project_refuses_with_a_named_reason(self) -> None:
        with tempfile.TemporaryDirectory() as empty:
            code, _out, err = _run(["--root", empty])
        self.assertEqual(code, 64)
        self.assertIn("REFUSED", err)
        self.assertIn("PROJECT-MISSING", err)
        self.assertIn("precondition", err)

    def test_a_non_positive_timeout_refuses_rather_than_removing_the_ceiling(self) -> None:
        for bad in ("0", "-1"):
            with self.subTest(timeout=bad):
                code, _out, err = _run(["--timeout", bad])
                self.assertEqual(code, 64)
                self.assertIn("TIMEOUT-NOT-POSITIVE", err)

    def test_a_refusal_still_emits_json_when_asked(self) -> None:
        with tempfile.TemporaryDirectory() as empty:
            code, out, _err = _run(["--root", empty, "--json"])
        self.assertEqual(code, 64)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], "PROJECT-MISSING")
        self.assertEqual(payload["stage"], "precondition")


class ExitCodeVocabulary(unittest.TestCase):
    """0 clean, 1 a finding, 64 a refusal. A caller must be able to tell them apart without
    reading prose, which is why they are distinct numbers rather than one non-zero."""

    def test_a_non_positive_timeout_never_reports_ok(self) -> None:
        code, out, _err = _run(["--timeout", "0", "--json"])
        self.assertEqual(code, 64)
        self.assertNotIn('"verdict": "OK"', out)


class ErrorExcerpt(unittest.TestCase):
    """The finding must carry its cause, and must not spend its budget on repeats."""

    def test_msbuild_diagnostics_are_recognised(self) -> None:
        log = (
            "  C:\\src\\a.cs(3,5): error CS0103: The name 'x' does not exist\n"
            "  C:\\src\\b.cs(9,1): error MSB3021: Unable to copy\n"
        )
        self.assertEqual(len(guard.find_error_lines(log)), 2)

    def test_a_path_containing_the_word_error_is_not_a_diagnostic(self) -> None:
        log = "  copying to C:\\build\\error\\output.dll succeeded\n"
        self.assertEqual(guard.find_error_lines(log), [])

    def test_the_same_diagnostic_is_reported_once(self) -> None:
        line = "  C:\\src\\a.cs(3,5): error CS0103: The name 'x' does not exist"
        self.assertEqual(len(guard.find_error_lines(line + "\n" + line)), 1)

    def test_the_excerpt_is_capped(self) -> None:
        log = "\n".join(f"  C:\\src\\f{i}.cs(1,1): error CS000{i}: boom" for i in range(50))
        self.assertEqual(len(guard.find_error_lines(log)), guard.ERROR_EXCERPT_LINES)


class TempCleanupIsAFailureNotASwallow(unittest.TestCase):
    """testing-standard.md R3: a failed temp delete is a failure. This is the defect class behind
    a measured 65.5 GB leak, and the PowerShell original ignored the failed removal."""

    def test_a_removable_tree_is_removed_and_reported_as_success(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "build-out"
            (target / "nested").mkdir(parents=True)
            (target / "nested" / "a.dll").write_bytes(b"x")
            self.assertIsNone(guard.remove_tree(target))
            self.assertFalse(target.exists())

    def test_an_absent_tree_is_not_a_failure(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            self.assertIsNone(guard.remove_tree(Path(tmp) / "never-existed"))

    def test_an_unremovable_tree_returns_a_message_instead_of_raising(self) -> None:
        if os.name != "nt":
            self.skipTest("the read-only-directory lock is a Windows behaviour")
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "locked"
            target.mkdir()
            (target / "a.dll").write_bytes(b"x")
            # A directory whose contents cannot be deleted: read-only, and delete-children refused.
            original = guard.shutil.rmtree

            def refuse(*_args, **_kwargs):
                raise PermissionError("simulated: the file is being used by another process")

            guard.shutil.rmtree = refuse
            try:
                message = guard.remove_tree(target)
            finally:
                guard.shutil.rmtree = original
        self.assertIsNotNone(message, "a failed delete must be reported, not swallowed")
        self.assertIn("could not remove", message)
        self.assertIn(str(guard.CLEANUP_ATTEMPTS), message)

    def test_cleanup_is_retried_a_bounded_number_of_times_then_gives_up(self) -> None:
        if os.name != "nt":
            self.skipTest("the retry backoff is asserted through the Windows path")
        calls: list[int] = []
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "locked"
            target.mkdir()
            original = guard.shutil.rmtree

            def always_refuse(*_args, **_kwargs):
                calls.append(1)
                raise PermissionError("still locked")

            guard.shutil.rmtree = always_refuse
            try:
                message = guard.remove_tree(target)
            finally:
                guard.shutil.rmtree = original
        self.assertEqual(len(calls), guard.CLEANUP_ATTEMPTS)
        self.assertIsNotNone(message)


class RefusalCarriesItsStage(unittest.TestCase):
    """The standard requires a non-zero exit that NAMES the failing stage, so a failure in a long
    pipeline is attributable without a stack trace."""

    def test_a_refusal_exposes_stage_reason_and_detail(self) -> None:
        refusal = guard.Refusal("build", "BUILD-TIMED-OUT", "exceeded the ceiling")
        self.assertEqual(refusal.stage, "build")
        self.assertEqual(refusal.reason, "BUILD-TIMED-OUT")
        self.assertIn("build", str(refusal))
        self.assertIn("exceeded the ceiling", str(refusal))


class TimedOutIsNotPassed(unittest.TestCase):
    """A build that exceeds its ceiling has an UNKNOWN verdict. Unknown is never a pass."""

    def test_a_timeout_becomes_a_refusal_not_a_verdict(self) -> None:
        import subprocess

        class FakeCompleted:
            returncode = 0
            stdout = "Build succeeded."
            stderr = ""

        def fake_run(*_args, **kwargs):
            raise subprocess.TimeoutExpired(cmd="dotnet", timeout=kwargs.get("timeout", 1))

        with tempfile.TemporaryDirectory() as root:
            project = Path(root) / "tests" / "FusionRpg.Bench" / "FusionRpg.Bench.csproj"
            project.parent.mkdir(parents=True)
            project.write_text("<Project/>", encoding="utf-8")
            original_run, original_which = guard.subprocess.run, guard.shutil.which
            guard.subprocess.run = fake_run
            guard.shutil.which = lambda _name: "dotnet"
            try:
                with self.assertRaises(guard.Refusal) as caught:
                    guard.run_guard(Path(root), timeout=0.01)
            finally:
                guard.subprocess.run, guard.shutil.which = original_run, original_which
        self.assertEqual(caught.exception.reason, "BUILD-TIMED-OUT")
        self.assertIn("UNKNOWN", caught.exception.detail)


if __name__ == "__main__":
    unittest.main()
