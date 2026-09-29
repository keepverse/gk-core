"""Contract tests for `gk-core/scripts/collect_class_system_realrun.py`.

A COLLECTOR, so the contract is about the drop-rate math and the exit contract.

THE DROP-RATE MATH IS THE POINT. The original computed `expectedWindows`, `estimatedDropped` and
`dropRatePct` inline and only ever wrote them to the summary file, so a port that got the rounding
wrong would produce plausible-looking numbers. The computation is now a pure function with the
original's rounding pinned -- including the banker's rounding `[math]::Round` did, which is NOT the
half-up rounding a careless port would write.

THE EXIT CONTRACT IS THE OTHER POINT. The original exited 1 when zero windows arrived ("Is the game
running with the injector connected?") and 0 otherwise. A collector that exits 0 having collected
nothing is a green light on a dead run.

A POLL FAILURE IS WARNED AND SKIPPED, never fatal -- one quiet server must not discard the windows
the rest of the run could still collect. The original's `catch` did that; the port keeps it and makes
the warning say which stage failed.

The transport and the clock are substituted through the tool's PRIVATE seams (`_URLOPEN`,
`_SLEEP`, `_MONOTONIC`), because those are process-wide modules: a test that patches any of them
reaches every other test in this project.
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
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("COLLECT_CLASS_SYSTEM_REALRUN_SCRIPT",
                             REPO / "scripts" / "collect_class_system_realrun.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_collect_class_system_realrun.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("collect_class_system_realrun", SCRIPT)
ccsr = importlib.util.module_from_spec(_spec)
sys.modules["collect_class_system_realrun"] = ccsr
_spec.loader.exec_module(ccsr)
_PRISTINE = {"_URLOPEN": ccsr._URLOPEN, "_SLEEP": ccsr._SLEEP, "_MONOTONIC": ccsr._MONOTONIC}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(ccsr, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(ccsr, name)!r}")


class FakeClock:
    """A monotonic clock that advances a fixed step per call, so a 300-second collection finishes
    in milliseconds without changing what the loop computes."""

    def __init__(self, step: float = 4.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


class ScriptedHttp:
    """A scripted `urllib` answer. `pages` is the sequence of /api/perf/recent answers, one per
    poll; `faults` counts polls that must raise instead of answering."""

    def __init__(self, pages: list[dict], faults: int = 0) -> None:
        self.pages = list(pages)
        self.faults = faults
        self.calls = 0

    def __call__(self, request, timeout=None):
        self.calls += 1
        if self.faults > 0:
            self.faults -= 1
            raise OSError(f"connection refused by the harness: {request.full_url}")
        if not self.pages:
            return ccsr._Response({"items": []})
        return ccsr._Response(self.pages.pop(0))


def window(t: str, tag: str = "") -> dict:
    return {"t": t, "tag": tag, "fps": 60}


class TheDropRateMath(SeamGuard):
    """The original's formula, preserved exactly -- including banker's rounding."""

    def test_a_run_with_ZERO_windows_reports_no_span_rather_than_dividing_by_zero(self) -> None:
        expected, dropped, rate = ccsr.compute_drop_stats([], 5.0)
        self.assertEqual((expected, dropped, rate), (0, 0, 0.0))

    def test_a_run_with_ONE_window_has_no_interior_span_to_estimate_over(self) -> None:
        expected, dropped, rate = ccsr.compute_drop_stats(
            [datetime(2026, 1, 1, tzinfo=timezone.utc)], 5.0)
        self.assertEqual((expected, dropped, rate), (1, 0, 0.0))

    def test_the_expected_count_is_the_span_over_the_cadence_plus_one(self) -> None:
        captured = [datetime(2026, 1, 1, 0, 0, 0, tzinfo=timezone.utc),
                    datetime(2026, 1, 1, 0, 0, 10, tzinfo=timezone.utc)]
        expected, dropped, rate = ccsr.compute_drop_stats(captured, 5.0)
        self.assertEqual(expected, 3)  # round(10/5) + 1
        self.assertEqual(dropped, 1)   # max(0, 3 - 2)
        self.assertEqual(rate, 33.33)  # round(100 * 1 / 3, 2)

    def test_the_rounding_is_BANKERS_as_PowerShells_Round_was(self) -> None:
        """`[math]::Round` and Python's `round` both round half to EVEN. A port that wrote half-up
        rounding would produce 1 here where the original produced 0 -- a different expected count
        from the same span, in a file whose whole purpose is the drop rate."""
        captured = [datetime(2026, 1, 1, 0, 0, 0, tzinfo=timezone.utc),
                    datetime(2026, 1, 1, 0, 0, 2, 500000, tzinfo=timezone.utc)]
        expected, _, _ = ccsr.compute_drop_stats(captured, 5.0)
        self.assertEqual(expected, 1, "2.5s / 5s = 0.5 must round to 0 (even), giving expected 1")

    def test_a_NEGATIVE_span_is_impossible_because_the_timestamps_are_sorted(self) -> None:
        captured = [datetime(2026, 1, 1, 0, 0, 10, tzinfo=timezone.utc),
                    datetime(2026, 1, 1, 0, 0, 0, tzinfo=timezone.utc)]
        expected, dropped, rate = ccsr.compute_drop_stats(captured, 5.0)
        self.assertEqual(expected, 3, "the span is first-to-last over the SORTED timestamps")

    def test_a_fully_healthy_run_reports_zero_drops(self) -> None:
        captured = [datetime(2026, 1, 1, 0, 0, 0, tzinfo=timezone.utc),
                    datetime(2026, 1, 1, 0, 0, 5, tzinfo=timezone.utc),
                    datetime(2026, 1, 1, 0, 0, 10, tzinfo=timezone.utc)]
        expected, dropped, rate = ccsr.compute_drop_stats(captured, 5.0)
        self.assertEqual((expected, dropped, rate), (3, 0, 0.0))


class TheTimestampParse(SeamGuard):
    def test_an_ISO_timestamp_with_a_Z_suffix_parses(self) -> None:
        parsed = ccsr.parse_window_t("2026-01-01T00:00:00Z")
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed.year, 2026)

    def test_an_ISO_timestamp_with_an_offset_parses(self) -> None:
        self.assertIsNotNone(ccsr.parse_window_t("2026-01-01T00:00:00+00:00"))

    def test_an_EMPTY_t_is_skipped(self) -> None:
        self.assertIsNone(ccsr.parse_window_t(""))
        self.assertIsNone(ccsr.parse_window_t(None))

    def test_a_GARBAGE_t_is_skipped_rather_than_killing_the_run(self) -> None:
        """The original cast `[datetime]$w.t` inside the poll's try/catch: an unparseable t lost THAT
        WINDOW and warned, it did not kill the run."""
        self.assertIsNone(ccsr.parse_window_t("not a timestamp"))


class TheCollection(SeamGuard):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="ccsr-contract-")
        self.addCleanup(self._tmp.cleanup)
        self.out_dir = Path(self._tmp.name)

    def _collect(self, http: ScriptedHttp, duration: float = 300, interval: float = 4.0,
                 run_id: str = "run-1"):
        clock = FakeClock(step=interval)
        with mock.patch.object(ccsr, "_URLOPEN", http), \
                mock.patch.object(ccsr, "_SLEEP", lambda _s: None), \
                mock.patch.object(ccsr, "_MONOTONIC", clock):
            return ccsr.collect("http://127.0.0.1:5101", duration, interval, run_id, 5.0,
                                self.out_dir)

    def test_windows_are_deduped_by_their_own_t_string(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z"),
                                          window("2026-01-01T00:00:05Z"),
                                          window("2026-01-01T00:00:00Z")]}])
        summary = self._collect(http)
        self.assertEqual(summary["windowsCaptured"], 2, "the duplicate t was not deduped")
        lines = (self.out_dir / "run-1.jsonl").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(lines), 2)

    def test_the_JSONL_envelope_is_runId_t_window(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z", tag="a")]}])
        self._collect(http)
        line = json.loads((self.out_dir / "run-1.jsonl").read_text(encoding="utf-8").splitlines()[0])
        self.assertEqual(sorted(line), ["runId", "t", "window"])
        self.assertEqual(line["runId"], "run-1")
        self.assertEqual(line["t"], "2026-01-01T00:00:00Z")
        self.assertEqual(line["window"]["tag"], "a")

    def test_the_summary_carries_the_original_fields(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z"),
                                          window("2026-01-01T00:00:10Z")]}])
        summary = self._collect(http)
        for field in ("runId", "baseUrl", "startedUtc", "endedUtc", "durationSec",
                      "windowsCaptured", "expectedWindows", "estimatedDropped", "dropRatePct"):
            self.assertIn(field, summary, field)
        self.assertEqual(summary["runId"], "run-1")
        self.assertEqual(summary["baseUrl"], "http://127.0.0.1:5101")
        self.assertEqual(summary["durationSec"], 300)
        self.assertEqual(summary["windowsCaptured"], 2)
        self.assertEqual(summary["expectedWindows"], 3)
        self.assertEqual(summary["estimatedDropped"], 1)
        self.assertEqual(summary["dropRatePct"], 33.33)

    def test_a_FAILED_poll_is_WARNED_and_SKIPPED_and_the_run_CARRIES_ON(self) -> None:
        """One quiet server must not discard the windows the rest of the run could still collect."""
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z")]}], faults=1)
        err = io.StringIO()
        clock = FakeClock(step=4.0)
        with mock.patch.object(ccsr, "_URLOPEN", http), \
                mock.patch.object(ccsr, "_SLEEP", lambda _s: None), \
                mock.patch.object(ccsr, "_MONOTONIC", clock), \
                redirect_stderr(err):
            summary = ccsr.collect("http://127.0.0.1:5101", 300, 4.0, "run-1", 5.0, self.out_dir)
        self.assertIn("poll failed", err.getvalue())
        self.assertEqual(summary["windowsCaptured"], 1)

    def test_a_window_with_a_GARBAGE_t_is_warned_and_skipped(self) -> None:
        http = ScriptedHttp([{"items": [window("not a timestamp"),
                                          window("2026-01-01T00:00:00Z")]}])
        err = io.StringIO()
        clock = FakeClock(step=4.0)
        with mock.patch.object(ccsr, "_URLOPEN", http), \
                mock.patch.object(ccsr, "_SLEEP", lambda _s: None), \
                mock.patch.object(ccsr, "_MONOTONIC", clock), \
                redirect_stderr(err):
            summary = ccsr.collect("http://127.0.0.1:5101", 300, 4.0, "run-1", 5.0, self.out_dir)
        self.assertIn("not a timestamp", err.getvalue())
        self.assertEqual(summary["windowsCaptured"], 1)

    def test_the_summary_file_is_written_NEXT_to_the_jsonl(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z")]}])
        self._collect(http)
        summary_path = self.out_dir / "run-1.summary.json"
        self.assertTrue(summary_path.is_file())
        on_disk = json.loads(summary_path.read_text(encoding="utf-8"))
        self.assertEqual(on_disk["windowsCaptured"], 1)


class TheExitContract(SeamGuard):
    """The original exited 1 when zero windows arrived and 0 otherwise."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="ccsr-exit-")
        self.addCleanup(self._tmp.cleanup)
        self.out_dir = Path(self._tmp.name)

    def _main(self, http: ScriptedHttp, argv: list[str]):
        clock = FakeClock(step=4.0)
        with mock.patch.object(ccsr, "_URLOPEN", http), \
                mock.patch.object(ccsr, "_SLEEP", lambda _s: None), \
                mock.patch.object(ccsr, "_MONOTONIC", clock):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = ccsr.main(argv + ["--out-dir", str(self.out_dir)])
        return code, err.getvalue(), out.getvalue()

    def test_a_run_with_ZERO_windows_exits_1_and_says_why(self) -> None:
        code, err, _ = self._main(ScriptedHttp([]), ["--json"])
        self.assertEqual(code, ccsr.EXIT_FAILED)
        self.assertIn("no perf windows arrived", err)

    def test_a_run_with_windows_exits_0(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z")]}])
        code, err, out = self._main(http, ["--json"])
        self.assertEqual(code, ccsr.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["summary"]["windowsCaptured"], 1)
        self.assertIn("wrote 1 window(s)", err)

    def test_the_operator_output_names_both_files(self) -> None:
        http = ScriptedHttp([{"items": [window("2026-01-01T00:00:00Z")]}])
        code, err, _ = self._main(http, ["--run-id", "run-1"])
        self.assertEqual(code, ccsr.EXIT_OK)
        self.assertIn("run-1.jsonl", err)
        self.assertIn("run-1.summary.json", err)


class TheRefusals(SeamGuard):
    def test_a_NON_POSITIVE_duration_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = ccsr.main(["--duration", "0", "--json"])
        self.assertEqual(code, ccsr.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-DURATION")

    def test_a_NON_POSITIVE_poll_interval_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = ccsr.main(["--poll-interval", "0", "--json"])
        self.assertEqual(code, ccsr.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-INTERVAL")

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = ccsr.main(["--timeout", "0", "--json"])
        self.assertEqual(code, ccsr.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = ccsr.main(["--duration", "0"])
        self.assertEqual(code, ccsr.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - ccsr.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - ccsr.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(ccsr.EXIT_OK, 0)
        self.assertEqual(ccsr.EXIT_FAILED, 1)
        self.assertEqual(ccsr.EXIT_REFUSED, 64)

    def test_it_uses_the_shared_resolve_base_url_and_NOT_a_hardcoded_port(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.resolve_base_url", source)
        tree = ast.parse(source)
        defaults = [ast.unparse(n) for node in ast.walk(tree)
                    if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "add_argument"
                    for n in node.defaults if isinstance(n, ast.Constant)]
        for default in defaults:
            self.assertNotIn("5088", default, f"a flag still defaults to the owner's port: {default}")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-DurationSec", "-PollIntervalSec", "-RunId"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("collect-class-system-realrun.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "drop-rate", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--duration", "--poll-interval", "--run-id",
                     "--expected-interval", "--out-dir", "--timeout", "--json"):
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
            if isinstance(target, ast.Name) and target.id == "ccsr":
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
