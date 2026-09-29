"""Contract tests for `gk-core/scripts/stress_test.py`.

THE TWO DEFECTS THIS PORT EXISTS TO FIX
=======================================
1. **THE ORIGINAL HAD NO `exit` STATEMENT.** It always exited 0, PASS or FAIL. A parent process
   reading its exit code saw success for a run that printed "VERDICT: CHECK FAILURES ABOVE".
   The port returns 0 on pass, 1 on fail, 64 on refusal.

2. **THE ORIGINAL IGNORED THE CHILD'S EXIT CODE.** `probe_perf.py` returns `EXIT_REFUSED = 64` in
   eight named reasons. Every one would make the original read a STALE baseline from a previous run
   of the same scenario name and print a confident `VERDICT: PASS` for a run that never happened.
   The port checks the child's exit code and refuses.

THE VERDICT ARITHMETIC IS A DOCUMENTED CONTRACT
===============================================
`docs/architecture/event-pipeline-v2-spec.md:44` is the authoritative bar. Three hard bars:
`gen2 == 0`, `pipePct <= 5`, `dropped == 0`. The nested-section correction (lines 64-67 of the
original) subtracts `onCapture`-inside-`drain` from the raw sum. The open defect F5
(`perf-v3-spec.md:41`) is carried forward, not silently closed.

THE LIVE CASES SKIP, with a stated reason, when no injector is reachable.
"""
from __future__ import annotations

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
SCRIPT = Path(os.environ.get("STRESS_TEST_SCRIPT", REPO / "scripts" / "stress_test.py")).resolve()
SUITE = Path(__file__).resolve()
RUN_TIMEOUT = 120


def _load():
    spec = importlib.util.spec_from_file_location("stress_test", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["stress_test"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


def run_cli(*args: str) -> tuple[int, dict, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = p.main(["--json", *args])
    try:
        payload = json.loads(out.getvalue())
    except json.JSONDecodeError:
        payload = {"__unparseable__": out.getvalue()[:200]}
    return code, payload, err.getvalue()


class TheVerdictArithmetic(unittest.TestCase):
    """The three bars and the nested-section correction, decided by planted windows."""

    def test_gen2_must_be_exactly_zero(self) -> None:
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {}, "drain": {}}])
        self.assertTrue(v["pass"], "gen2=0 with clean bars must pass")

    def test_gen2_above_zero_fails(self) -> None:
        v = p.verdict([{"gc": {"gen2": 1}, "frames": {"fpsAvg": 60},
                         "sections": {}, "drain": {}}])
        self.assertFalse(v["pass"], "gen2=1 must fail")

    def test_pipeline_share_over_5pct_fails(self) -> None:
        # drain 3000 + onCaptureOutside 0 + takeDamage 0 = 60% > 5%
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {"drain.tick": {"totalMs": 3000},
                                      "effect.onCapture": {"totalMs": 3000},
                                      "takeDamage.prefix": {"totalMs": 0}},
                         "drain": {"carried": 0, "droppedOverflow": 0}}])
        self.assertFalse(v["pass"], "60% pipeline share must fail")
        self.assertEqual(v["pipePct"], 60.0)

    def test_pipeline_share_at_5pct_passes(self) -> None:
        # drain 250 + onCaptureOutside 0 + takeDamage 0 = 5% <= 5%
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {"drain.tick": {"totalMs": 250},
                                      "effect.onCapture": {"totalMs": 250},
                                      "takeDamage.prefix": {"totalMs": 0}},
                         "drain": {"carried": 0, "droppedOverflow": 0}}])
        self.assertTrue(v["pass"], "5% pipeline share must pass")

    def test_the_nested_section_correction_subtracts_onCapture_inside_drain(self) -> None:
        """onCapture is INSIDE drain.tick, so the raw sum double-counts. The correction removes it."""
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {"drain.tick": {"totalMs": 100},
                                      "effect.onCapture": {"totalMs": 100},
                                      "takeDamage.prefix": {"totalMs": 50}},
                         "drain": {"carried": 0, "droppedOverflow": 0}}])
        # drain 100 + onCaptureOutside (100-100=0) + takeDamage 50 = 150ms = 3%
        self.assertEqual(v["ocOutside"], 0.0, "onCapture inside drain must be subtracted")
        self.assertEqual(v["pipePct"], 3.0)

    def test_dropped_overflow_fails(self) -> None:
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {}, "drain": {"droppedOverflow": 5}}])
        self.assertFalse(v["pass"], "any dropped record must fail")

    def test_vfx_tick_is_warning_only_and_does_not_enter_the_verdict(self) -> None:
        v = p.verdict([{"gc": {"gen2": 0}, "frames": {"fpsAvg": 60},
                         "sections": {"vfx.tick": {"totalMs": 100}},
                         "drain": {}}])
        self.assertEqual(v["vfxPct"], 2.0)
        self.assertTrue(v["pass"], "vfx.tick over budget is warning-only, not a verdict bar")

    def test_an_empty_window_list_is_not_a_pass(self) -> None:
        v = p.verdict([])
        self.assertFalse(v["pass"], "no windows must not pass")
        self.assertEqual(v["pipePct"], 0.0)


class TheRefusalVocabulary(unittest.TestCase):
    def test_a_non_positive_plants_refuses(self) -> None:
        code, payload, _ = run_cli("--plants", "0")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INVALID-PARAMETER")

    def test_a_non_positive_duration_refuses(self) -> None:
        code, payload, _ = run_cli("--duration-sec", "0")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INVALID-PARAMETER")

    def test_a_hostile_scenario_name_refuses(self) -> None:
        code, payload, _ = run_cli("--scenario", "../../evil")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SCENARIO-NAME-INVALID")

    def test_an_unreachable_server_refuses_with_a_named_reason(self) -> None:
        with mock.patch.object(p, "health_gate", side_effect=p.Refusal("SERVER-UNREACHABLE", "x")):
            code, payload, _ = run_cli("--base-url", "http://127.0.0.1:5999")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SERVER-UNREACHABLE")

    def test_a_disconnected_injector_refuses(self) -> None:
        with mock.patch.object(p, "health_gate",
                               side_effect=p.Refusal("INJECTOR-NOT-CONNECTED", "x")):
            code, payload, _ = run_cli()
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INJECTOR-NOT-CONNECTED")

    def test_a_missing_baseline_refuses_with_the_path(self) -> None:
        with mock.patch.object(p, "health_gate", return_value=None), \
             mock.patch.object(p, "fill_board", return_value=None), \
             mock.patch.object(p, "wait_for_settle", return_value=True), \
             mock.patch.object(p, "run_probe", return_value=None), \
             mock.patch.object(p, "load_baseline",
                               side_effect=p.Refusal("BASELINE-MISSING", "no baseline")):
            code, payload, _ = run_cli("--scenario", "never-existed")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "BASELINE-MISSING")

    def test_a_malformed_baseline_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "_baseline-bad.json"
            path.write_text('{"no_windows": true}', encoding="utf-8")
            with mock.patch.object(p, "health_gate", return_value=None), \
                 mock.patch.object(p, "fill_board", return_value=None), \
                 mock.patch.object(p, "wait_for_settle", return_value=True), \
                 mock.patch.object(p, "PERF_DIR", Path(tmp)), \
                 mock.patch.object(p, "run_probe", return_value=None):
                code, payload, _ = run_cli("--scenario", "bad")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "BASELINE-MALFORMED")

    def test_the_refusal_reasons_are_a_closed_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found")
        self.assertEqual(found - p.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - p.REFUSAL_REASONS)}")


class TheTwoDefects(unittest.TestCase):
    def test_a_child_that_refuses_makes_the_whole_run_refuse(self) -> None:
        """The original ignored $LASTEXITCODE. The port must not."""
        with mock.patch.object(p, "health_gate", return_value=None), \
             mock.patch.object(p, "fill_board", return_value=None), \
             mock.patch.object(p, "wait_for_settle", return_value=True), \
             mock.patch.object(p, "run_probe",
                               side_effect=p.Refusal(
                                   "PROBE-REFUSED",
                                   "probe_perf.py exited 64; a stale baseline would be read")):
            code, payload, _ = run_cli()
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "PROBE-REFUSED")
        self.assertIn("stale", payload["detail"],
                      "the refusal must say why a stale baseline is not a substitute")

    def test_a_clear_failure_is_a_refusal_not_a_warning(self) -> None:
        """The original warned and continued. The port refuses."""
        with mock.patch.object(p, "health_gate", return_value=None), \
             mock.patch.object(p, "fill_board", return_value=None), \
             mock.patch.object(p, "wait_for_settle", return_value=True), \
             mock.patch.object(p, "run_probe", return_value=None), \
             mock.patch.object(p, "load_baseline", return_value=[]), \
             mock.patch.object(p, "clear_board",
                               side_effect=p.Refusal("CLEAR-FAILED", "x")):
            code, payload, _ = run_cli()
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "CLEAR-FAILED")

    def test_the_script_exits_nonzero_on_a_failed_verdict(self) -> None:
        """The original had no exit statement at all — always 0."""
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "_baseline-fail.json"
            path.write_text(json.dumps({"windows": [
                {"gc": {"gen2": 5}, "frames": {"fpsAvg": 30}, "sections": {},
                 "drain": {"droppedOverflow": 3}}]}), encoding="utf-8")
            with mock.patch.object(p, "health_gate", return_value=None), \
                 mock.patch.object(p, "fill_board", return_value=None), \
                 mock.patch.object(p, "wait_for_settle", return_value=True), \
                 mock.patch.object(p, "PERF_DIR", Path(tmp)), \
                 mock.patch.object(p, "run_probe", return_value=None):
                code, payload, _ = run_cli("--scenario", "fail")
        self.assertEqual(code, p.EXIT_FAIL, "a failed verdict must exit 1, not 0")
        self.assertFalse(payload["pass"])

    def test_the_script_exits_zero_on_a_passing_verdict(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "_baseline-pass.json"
            path.write_text(json.dumps({"windows": [
                {"gc": {"gen2": 0}, "frames": {"fpsAvg": 60}, "sections": {},
                 "drain": {"droppedOverflow": 0}}]}), encoding="utf-8")
            with mock.patch.object(p, "health_gate", return_value=None), \
                 mock.patch.object(p, "fill_board", return_value=None), \
                 mock.patch.object(p, "wait_for_settle", return_value=True), \
                 mock.patch.object(p, "PERF_DIR", Path(tmp)), \
                 mock.patch.object(p, "run_probe", return_value=None):
                code, payload, _ = run_cli("--scenario", "pass")
        self.assertEqual(code, 0, "a passing verdict must exit 0")
        self.assertTrue(payload["pass"])


class TheJsonShape(unittest.TestCase):
    def test_the_json_envelope_carries_the_verdict_and_the_exit_code(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "_baseline-env.json"
            path.write_text(json.dumps({"windows": [
                {"gc": {"gen2": 0}, "frames": {"fpsAvg": 60}, "sections": {},
                 "drain": {"droppedOverflow": 0}}]}), encoding="utf-8")
            with mock.patch.object(p, "health_gate", return_value=None), \
                 mock.patch.object(p, "fill_board", return_value=None), \
                 mock.patch.object(p, "wait_for_settle", return_value=True), \
                 mock.patch.object(p, "PERF_DIR", Path(tmp)), \
                 mock.patch.object(p, "run_probe", return_value=None):
                code, payload, _ = run_cli("--scenario", "env")
        for key in ("tool", "scenario", "baseUrl", "baseUrlSource", "windows",
                    "pass", "exitCode"):
            self.assertIn(key, payload, f"the envelope is missing {key!r}")
        self.assertEqual(payload["tool"], "stress-test")
        self.assertEqual(payload["exitCode"], 0)

    def test_a_refusal_in_json_names_the_reason_and_the_exit_code(self) -> None:
        code, payload, _ = run_cli("--plants", "0")
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["exitCode"], p.EXIT_REFUSED)
        self.assertIn("reason", payload)
        self.assertIn("detail", payload)


class TheSurface(unittest.TestCase):
    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Plants", "-Zombies", "-DurationSec", "-BaseUrl", "-Scenario"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"],
                                      capture_output=True, text=True, timeout=RUN_TIMEOUT)
                self.assertNotEqual(proc.returncode, 0, f"{flag} was accepted")
                self.assertIn("--plants", proc.stdout + proc.stderr,
                              f"{flag} satisfied the required option")

    def test_it_requires_no_scenario_and_generates_the_default(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "_baseline-stress-40p-150z.json"
            path.write_text(json.dumps({"windows": [
                {"gc": {"gen2": 0}, "frames": {"fpsAvg": 60}, "sections": {},
                 "drain": {"droppedOverflow": 0}}]}), encoding="utf-8")
            with mock.patch.object(p, "health_gate", return_value=None), \
                 mock.patch.object(p, "fill_board", return_value=None), \
                 mock.patch.object(p, "wait_for_settle", return_value=True), \
                 mock.patch.object(p, "PERF_DIR", Path(tmp)), \
                 mock.patch.object(p, "run_probe", return_value=None):
                code, payload, _ = run_cli()
        self.assertEqual(payload["scenario"], "stress-40p-150z",
                         "the default scenario name must be stress-<p>p-<z>z")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("stress-test.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("exit", "information stream", "stale", "5088", "catch"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.DEFAULT_PLANTS, 40)
        self.assertEqual(p.DEFAULT_ZOMBIES, 150)
        self.assertEqual(p.DEFAULT_DURATION_SEC, 90)
        self.assertEqual(p.DEFAULT_BASE_URL, "http://127.0.0.1:5088")
        self.assertEqual(p.PERF_WINDOW_MS, 5000)

    def test_it_does_NOT_reach_for_the_shared_library(self) -> None:
        """This script drives the game over HTTP and shells out to probe_perf.py; it does not import
        the shared lawn library. A forced import would be a dependency with no purpose."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("import live_lawn_setup", source)
        self.assertIn("import subprocess", source)
        self.assertIn("probe_perf.py", source)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        import ast
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "tempfile", "os", "sys", "json", "urllib", "http",
                        "socket", "threading", "time", "importlib", "ast", "re", "io", "pathlib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "p":
                continue
            if isinstance(target, ast.Attribute) and target.attr in ("urllib", "read_windows"):
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))


if __name__ == "__main__":
    unittest.main()
