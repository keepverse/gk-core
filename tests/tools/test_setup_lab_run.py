"""Contract tests for `gk-core/scripts/setup_lab_run.py`.

A LAB SETUP, so the contract is about the recipe order, the gates, and the fail-closed board read.

THE BOARD READ IS THE POINT. The original's `Test-BoardStillLive` wrapped its `board.end` query in
`catch { }` and returned `$true` on failure -- a failed read declared a dead board LIVE, then the
script ran a lab scenario on it. The port refuses by name; these tests pin that a read failure is a
REFUSAL, never a silent "live".

THE RECIPE ORDER IS THE OTHER POINT. Health gate, latest board.start (kinds filter, then a cursor
scan), board-still-live, the Explore/Travel refusal, the scenario POST, the run-steps.done wait,
the board-snapshot wait, the living counts, the lab-overlay no-zombie refusal, and the
`--then-prove` handoff (`--base-url` always, `--target-ptr` only when a zombie was found, child's
exit code propagated).

The transport is substituted through the tool's PRIVATE seams (`_URLOPEN`, `_SLEEP`, `_RUN`,
`_MONOTONIC`) and the shared library's `_URLOPEN`, because those are process-wide modules: a test
that patches any of them reaches every other test in this project.
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
SCRIPT = Path(os.environ.get("SETUP_LAB_RUN_SCRIPT",
                              REPO / "scripts" / "setup_lab_run.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_setup_lab_run.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("setup_lab_run", SCRIPT)
slr = importlib.util.module_from_spec(_spec)
sys.modules["setup_lab_run"] = slr
_spec.loader.exec_module(slr)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": slr._URLOPEN, "_SLEEP": slr._SLEEP, "_RUN": slr._RUN,
             "_MONOTONIC": slr._MONOTONIC, "lib_URLOPEN": lib._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP", "_RUN", "_MONOTONIC"):
            if getattr(slr, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(slr, name)!r}")


class FastClock:
    """A monotonic clock that advances a fixed step per call, so a 60-second budget expires in
    milliseconds without changing what the loop computes."""

    def __init__(self, step: float = 1.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


class ScriptedHttp:
    """A scripted answer for the tool's OWN requests (health, kinds queries, scenario, snapshot).

    URL-aware: the `kinds=` queries and the scenario POST get their own scripted pages, because the
    original issued them as distinct requests with distinct answers.
    """

    def __init__(self, ok: bool = True, injector_connected: bool = True,
                 board_start: dict | None = None, board_end: list | None = None,
                 scenario_steps: int = 3) -> None:
        self.ok = ok
        self.injector_connected = injector_connected
        self.board_start = board_start
        self.board_end = board_end if board_end is not None else []
        self.scenario_steps = scenario_steps
        self.requests: list[tuple[str, str, dict | None]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        body = json.loads(request.data.decode("utf-8")) if request.data is not None else None
        self.requests.append((request.method, url, body))
        if request.data is not None:
            return slr._Response({"ok": True, "steps": self.scenario_steps})
        if url.endswith("/health"):
            return slr._Response({"ok": self.ok, "injectorConnected": self.injector_connected,
                                  "source": "test"})
        if "kinds=board.start" in url:
            return slr._Response({"items": [self.board_start] if self.board_start else []})
        if "kinds=board.end" in url:
            return slr._Response({"items": self.board_end})
        return slr._Response({"ok": True})


class LibResponder:
    """A scripted answer for the SHARED LIBRARY's event reads.

    The event-id binary search pages with `limit=1` (afterId=0 has events, afterId>=1 does not, so
    the search converges to 0); the polls page with a larger limit and are answered from a scripted
    sequence -- one event per poll, in the order the recipe issues them.
    """

    def __init__(self, wait_events: list[dict]) -> None:
        self.wait_events = list(wait_events)
        self.wait_calls = 0

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        limit_match = re.search(r"limit=(\d+)", url)
        if limit_match and limit_match.group(1) == "1":
            match = re.search(r"afterId=(\d+)", url)
            after_id = int(match.group(1)) if match else 0
            if after_id == 0:
                return slr._Response({"items": [{"id": 1, "kind": "board.start",
                                                   "payload": {}}]})
            return slr._Response({"items": []})
        if self.wait_calls >= len(self.wait_events):
            return slr._Response({"items": []})
        event = self.wait_events[self.wait_calls]
        self.wait_calls += 1
        return slr._Response({"items": [event]})


def board_start(level_type: str = "Adventure", board_level: int = 1,
                level_name: str = "Day 1") -> dict:
    return {"id": 3, "kind": "board.start",
            "payload": {"levelType": level_type, "boardLevel": board_level,
                        "levelName": level_name}}


def run_steps_done(event_id: int = 5) -> dict:
    return {"id": event_id, "kind": "debug.run-steps.done", "payload": {}}


def board_snapshot(plants: int = 1, zombies: int = 1, event_id: int = 6) -> dict:
    entities = ([{"side": "plant", "living": True, "ptr": f"P{i}"} for i in range(plants)]
                + [{"side": "zombie", "living": True, "ptr": f"Z{i}"} for i in range(zombies)])
    return {"id": event_id, "kind": "debug.effect.board-snapshot",
            "payload": {"entities": entities}}


class TheRecipe(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp(board_start=board_start())
        self.wait_events = [run_steps_done(), board_snapshot()]

    def _run(self, http: ScriptedHttp | None = None, wait_events: list[dict] | None = None,
             argv: list[str] | None = None, run_result: subprocess.CompletedProcess | None = None):
        http = http or self.http
        responder = LibResponder(wait_events if wait_events is not None else self.wait_events)
        self.run_mock = mock.Mock(return_value=run_result) if run_result is not None else None
        with mock.patch.object(slr, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(slr, "_SLEEP", lambda _s: None), \
                mock.patch.object(slr, "_MONOTONIC", FastClock()), \
                mock.patch.object(slr, "_RUN", self.run_mock):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = slr.main(argv if argv is not None else
                                ["--base-url", "http://127.0.0.1:5101", "--json"])
        return code, err.getvalue(), out.getvalue()

    def test_a_clean_run_exits_0_and_reports_the_lab(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, slr.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["tool"], "setup-lab-run")
        self.assertEqual(payload["targetPtr"], "Z0")
        self.assertEqual(payload["plantPtr"], "P0")
        self.assertEqual(payload["levelType"], "Adventure")
        self.assertIn("Board live: levelType=Adventure boardLevel=1 levelName=Day 1", err)
        self.assertIn("living plants=1 zombies=1", err)
        self.assertIn("PlantPtr=P0", err)
        self.assertIn("ZombiePtr=Z0 (use as --target-ptr)", err)
        self.assertIn("Lab ready. Next: python scripts/prove_overlay_combat.py --target-ptr Z0", err)

    def test_the_recipe_issues_the_original_requests_in_the_original_order(self) -> None:
        self._run()
        paths = [url.split("5101", 1)[1].split("?")[0] for method, url, _ in self.http.requests]
        self.assertEqual(paths, ["/health", "/api/events", "/api/events",
                                 "/api/debug/scenario/lab-overlay",
                                 "/api/debug/effect/board-snapshot"])
        kinds = [url.split("?")[1] for method, url, _ in self.http.requests
                 if "/api/events" in url]
        self.assertEqual(kinds, ["kinds=board.start&limit=5", "kinds=board.end&limit=5"])
        posts = {url.split("5101", 1)[1].split("?")[0]: body
                 for method, url, body in self.http.requests if body is not None}
        self.assertEqual(posts["/api/debug/scenario/lab-overlay"], {})
        self.assertEqual(posts["/api/debug/effect/board-snapshot"], {})

    def test_a_health_gate_that_is_not_ok_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(ok=False, board_start=board_start()))
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "HEALTH-NOT-OK")

    def test_an_injector_that_is_not_connected_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(injector_connected=False,
                                                    board_start=board_start()))
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INJECTOR-NOT-CONNECTED")

    def test_a_missing_board_start_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(board_start=None))
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "NO-BOARD-START")

    def test_a_board_that_ended_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(
            board_start=board_start(),
            board_end=[{"id": 9, "kind": "board.end", "payload": {}}]))
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "BOARD-ENDED")

    def test_a_failed_board_end_read_REFUSES_instead_of_declaring_the_board_live(self) -> None:
        """The original's `catch { }` here returned `$true` -- a failed read meant "live". The port
        must refuse, because a board whose end-state could not be read is not a board this script
        has measured."""

        def exploding(request, timeout=None):
            url = request.full_url if hasattr(request, "full_url") else str(request)
            if "kinds=board.end" in url:
                raise slr.Refusal("REQUEST-FAILED", "GET /api/events?kinds=board.end did not "
                                                     "answer within 5s")
            return self.http(request, timeout=timeout)

        code, _, out = self._run(http=mock.Mock(side_effect=exploding))
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "REQUEST-FAILED")

    def test_an_explore_board_is_refused_by_name(self) -> None:
        for level_type in ("Explore", "TravelAdvanture", "Travel", "IZ"):
            with self.subTest(level_type=level_type):
                code, _, out = self._run(http=ScriptedHttp(
                    board_start=board_start(level_type=level_type)))
                self.assertEqual(code, slr.EXIT_REFUSED)
                self.assertEqual(json.loads(out)["reason"], "BAD-LEVEL-TYPE")

    def test_a_run_steps_done_that_never_arrives_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(wait_events=[])
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "RUN-STEPS-TIMEOUT")
        self.assertIn("scenario=lab-overlay", json.loads(out)["detail"])

    def test_a_missing_board_snapshot_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(wait_events=[run_steps_done()])
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "BOARD-SNAPSHOT-MISSING")

    def test_a_lab_overlay_with_no_living_zombie_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(wait_events=[run_steps_done(), board_snapshot(zombies=0)])
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "NO-LIVING-ZOMBIE")

    def test_a_lab_empty_with_no_zombie_is_ALLOWED(self) -> None:
        """The original refuses the no-zombie condition for lab-overlay ONLY; lab-empty is a
        no-fixtures surface by design."""
        code, err, out = self._run(wait_events=[run_steps_done(), board_snapshot(zombies=0)],
                                   argv=["--base-url", "http://127.0.0.1:5101",
                                         "--scenario", "lab-empty", "--json"])
        self.assertEqual(code, slr.EXIT_OK)
        self.assertEqual(json.loads(out)["targetPtr"], "")
        self.assertIn("Lab ready. Next: python scripts/prove_overlay_combat.py", err)

    def test_a_cheat_error_line_is_printed_and_the_wait_CONTINUES(self) -> None:
        """The original printed `cheat.error` payloads mid-wait and kept polling -- an error during
        a scenario is evidence, not a reason to stop watching."""
        events = [{"id": 4, "kind": "cheat.error", "payload": {"message": "spawn jammed"}},
                  run_steps_done(), board_snapshot()]
        code, err, _ = self._run(wait_events=events)
        self.assertEqual(code, slr.EXIT_OK)
        self.assertIn("cheat.error: spawn jammed", err)

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--timeout", "0",
                                        "--json"])
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")

    def test_then_prove_runs_the_sibling_WITH_the_target_ptr_and_propagates_its_exit(self) -> None:
        code, _, _ = self._run(
            argv=["--base-url", "http://127.0.0.1:5101", "--then-prove", "--json"],
            run_result=subprocess.CompletedProcess([], 1, "prove out", "prove err"))
        self.assertEqual(code, 1, "the child's exit code must become the script's")
        cmd = self.run_mock.call_args[0][0]
        self.assertEqual(cmd[:3], [sys.executable, str(SCRIPT.parent / "prove_overlay_combat.py"),
                                   "--base-url"])
        self.assertIn("http://127.0.0.1:5101", cmd)
        self.assertEqual(cmd[cmd.index("--target-ptr") + 1], "Z0")

    def test_then_prove_without_a_zombie_omits_the_target_ptr(self) -> None:
        code, _, _ = self._run(
            wait_events=[run_steps_done(), board_snapshot(zombies=0)],
            argv=["--base-url", "http://127.0.0.1:5101", "--scenario", "lab-empty",
                  "--then-prove", "--json"],
            run_result=subprocess.CompletedProcess([], 0, "", ""))
        self.assertEqual(code, slr.EXIT_OK)
        cmd = self.run_mock.call_args[0][0]
        self.assertNotIn("--target-ptr", cmd)

    def test_a_missing_sibling_prove_is_a_NAMED_refusal(self) -> None:
        with mock.patch.object(slr.Path, "is_file", return_value=False):
            code, _, out = self._run(
                argv=["--base-url", "http://127.0.0.1:5101", "--then-prove", "--json"])
        self.assertEqual(code, slr.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "PROVE-MISSING")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - slr.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - slr.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(slr.EXIT_OK, 0)
        self.assertEqual(slr.EXIT_REFUSED, 64)

    def test_it_uses_the_shared_resolve_base_url_and_NOT_a_hardcoded_port(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.resolve_base_url", source)
        tree = ast.parse(source)
        defaults = [ast.unparse(n) for node in ast.walk(tree)
                    if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "add_argument"
                    for n in node.defaults if isinstance(n, ast.Constant)]
        for default in defaults:
            self.assertNotIn("5088", default, f"a flag still defaults to the owner's port: {default}")

    def test_it_uses_the_shared_event_id_search_and_NOT_a_reimplementation(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.get_debug_max_event_id", source)
        self.assertNotIn("def get_max_event_id", source,
                         "the event-id binary search was reimplemented locally")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Scenario", "-ThenProve", "-TimeoutSec"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("setup-lab-run.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "fail", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--scenario", "--then-prove", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_the_module_IMPORTS_cleanly(self) -> None:
        self.assertTrue(SCRIPT.is_file())
        self.assertTrue(callable(slr.main))

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
            label = ast.unparse(target) if target is not None else "?"
            root = label.split(".")[0]
            if root in ("slr", "lib"):
                continue
            if root in globals_seen:
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
