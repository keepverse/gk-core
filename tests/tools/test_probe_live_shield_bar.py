"""Contract tests for `gk-core/scripts/probe_live_shield_bar.py`.

A SHIELD-BAR PROBE, so the contract is about the five verdict criteria and the poll loop.

THE VERDICT IS THE POINT. Five criteria, each reported by name: dataOwners > 0, shaderOk, worldBars
== dataOwners (and > 0), fillRatio > 0, and lastDraw.early == "ok". The original printed [OK]/[FAIL]
per row and exited 1 when any failed; the port keeps that contract exactly.

THE POLL LOOP IS THE OTHER POINT. The original POSTed bar-status, waited up to 3s for the event,
slept 400ms, and repeated until `shaderOk && worldBars > 0 && worldBars == dataOwners` or the
overall deadline. A port that broke on the first round, or that never broke, would measure a
different thing.

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
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROBE_LIVE_SHIELD_BAR_SCRIPT",
                             REPO / "scripts" / "probe_live_shield_bar.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_probe_live_shield_bar.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("probe_live_shield_bar", SCRIPT)
pslb = importlib.util.module_from_spec(_spec)
sys.modules["probe_live_shield_bar"] = pslb
_spec.loader.exec_module(pslb)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": pslb._URLOPEN, "_SLEEP": pslb._SLEEP, "_RUN": pslb._RUN,
             "_MONOTONIC": pslb._MONOTONIC, "lib_URLOPEN": lib._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP", "_RUN", "_MONOTONIC"):
            if getattr(pslb, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(pslb, name)!r}")


class FastClock:
    """A monotonic clock that advances a fixed step per call, so an 8-second poll budget expires in
    milliseconds without changing what the loop computes."""

    def __init__(self, step: float = 1.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


class ScriptedHttp:
    """A scripted answer for the probe's OWN requests (health, demo-all, bar-status)."""

    def __init__(self, ok: bool = True, injector_connected: bool = True) -> None:
        self.ok = ok
        self.injector_connected = injector_connected
        self.posts: list[tuple[str, dict]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        if request.data is not None:
            body = json.loads(request.data.decode("utf-8"))
            self.posts.append((url, body))
            return pslb._Response({"ok": True})
        if url.endswith("/health"):
            return pslb._Response({"ok": self.ok, "injectorConnected": self.injector_connected})
        return pslb._Response({"ok": True})


class LibResponder:
    """A scripted answer for the SHARED LIBRARY's event reads.

    The event-id binary search pages with `limit=1` (afterId=0 has events, afterId>=1 does not, so
    the search converges to 0); the wait-kind polls page with `limit=100` and are answered from a
    scripted sequence -- one event per wait, in the order the recipe issues them.
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
                return pslb._Response({"items": [{"id": 1, "kind": "board.start",
                                                   "payload": {}}]})
            return pslb._Response({"items": []})
        if self.wait_calls >= len(self.wait_events):
            return pslb._Response({"items": []})
        event = self.wait_events[self.wait_calls]
        self.wait_calls += 1
        return pslb._Response({"items": [event]})


def demo_event(target_count: int = 3) -> dict:
    return {"id": 10, "kind": "debug.shield.demo-all",
            "payload": {"targetCount": target_count, "targets": [{"targetPtr": "Z1"}]}}


def bar_status_event(data_owners: int = 2, world_bars: int = 2, shader_ok: bool = True,
                     fill_ratio: float = 0.5, early: str = "ok") -> dict:
    return {"id": 20, "kind": "debug.shield.bar-status",
            "payload": {"dataOwners": data_owners, "worldBars": world_bars, "shaderOk": shader_ok,
                        "fillRatio": fill_ratio, "lastDraw": {"early": early}}}


class TheRecipe(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.wait_events = [demo_event(), bar_status_event()]

    def _run(self, http: ScriptedHttp | None = None, wait_events: list[dict] | None = None,
             argv: list[str] | None = None):
        http = http or self.http
        responder = LibResponder(wait_events if wait_events is not None else self.wait_events)
        with mock.patch.object(pslb, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(pslb, "_SLEEP", lambda _s: None), \
                mock.patch.object(pslb, "_MONOTONIC", FastClock()):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = pslb.main(argv if argv is not None else
                                ["--base-url", "http://127.0.0.1:5101", "--json"])
        return code, err.getvalue(), out.getvalue()

    def test_a_clean_run_exits_0_and_reports_PASS(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, pslb.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "PASS")
        self.assertTrue(all(payload["criteria"].values()))
        self.assertIn("PASS — world shield bars should be under shielded units", err)

    def test_the_recipe_posts_in_the_original_order(self) -> None:
        self._run()
        paths = [url.split("5101", 1)[1].split("?")[0] for url, _ in self.http.posts]
        self.assertEqual(paths, ["/api/debug/shield/demo-all", "/api/debug/shield/bar-status"])
        bodies = {url.split("5101", 1)[1].split("?")[0]: body for url, body in self.http.posts}
        self.assertEqual(bodies["/api/debug/shield/demo-all"], {"amount": 100})
        self.assertEqual(bodies["/api/debug/shield/bar-status"], {})

    def test_the_operator_output_carries_the_five_criteria(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, pslb.EXIT_OK)
        self.assertIn("  [OK] injector has shields (dataOwners=2)", err)
        self.assertIn("  [OK] OverlayShaderProbe material ok (shaderOk=True)", err)
        self.assertIn("  [OK] world VFX bars live (worldBars=2)", err)
        self.assertIn("  [OK] fill length from capacity (fillRatio=0.5 early=ok)", err)
        self.assertIn("  targets=3", err)

    def test_the_poll_BREAKS_when_the_bar_is_fully_live(self) -> None:
        """The original broke out of the poll loop on `shaderOk && worldBars > 0 && worldBars ==
        dataOwners`. A port that polled until the deadline regardless would measure a different
        thing."""
        code, _, _ = self._run()
        self.assertEqual(code, pslb.EXIT_OK)
        bar_posts = [url for url, _ in self.http.posts if "bar-status" in url]
        self.assertEqual(len(bar_posts), 1, "the poll did not break on the fully-live bar")

    def test_a_server_with_NO_bars_is_polled_until_the_budget_and_FAILs(self) -> None:
        self.wait_events = [demo_event(), bar_status_event(data_owners=2, world_bars=0)]
        code, err, out = self._run()
        self.assertEqual(code, pslb.EXIT_FAILED)
        self.assertEqual(json.loads(out)["verdict"], "FAIL")
        self.assertIn("  [FAIL] world VFX bars live (worldBars=0)", err)
        self.assertIn("Look under pea/zombie for shader bars", err)

    def test_a_server_with_a_broken_shader_FAILs(self) -> None:
        self.wait_events = [demo_event(), bar_status_event(shader_ok=False)]
        code, _, out = self._run()
        self.assertEqual(code, pslb.EXIT_FAILED)
        self.assertFalse(json.loads(out)["criteria"]["shaderOk"])

    def test_a_server_with_zero_fillRatio_FAILs(self) -> None:
        self.wait_events = [demo_event(), bar_status_event(fill_ratio=0.0)]
        code, _, out = self._run()
        self.assertEqual(code, pslb.EXIT_FAILED)
        self.assertFalse(json.loads(out)["criteria"]["ratioOk"])

    def test_a_server_with_early_not_ok_FAILs(self) -> None:
        self.wait_events = [demo_event(), bar_status_event(early="no-shader")]
        code, err, out = self._run()
        self.assertEqual(code, pslb.EXIT_FAILED)
        self.assertFalse(json.loads(out)["criteria"]["earlyOk"])
        self.assertIn("early=no-shader → OverlayShaderProbe failed", err)

    def test_a_server_with_mismatched_bar_count_FAILs(self) -> None:
        self.wait_events = [demo_event(), bar_status_event(data_owners=3, world_bars=2)]
        code, _, out = self._run()
        self.assertEqual(code, pslb.EXIT_FAILED)
        self.assertFalse(json.loads(out)["criteria"]["barsOk"])

    def test_a_health_gate_that_is_not_ok_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(ok=False))
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "HEALTH-NOT-OK")

    def test_an_injector_that_is_not_connected_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(injector_connected=False))
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INJECTOR-NOT-CONNECTED")

    def test_a_missing_demo_all_event_is_a_NAMED_refusal(self) -> None:
        self.wait_events = []
        code, _, out = self._run()
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "DEMO-ALL-MISSING")

    def test_a_missing_bar_status_event_is_a_NAMED_refusal(self) -> None:
        self.wait_events = [demo_event()]
        code, _, out = self._run()
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "BAR-STATUS-MISSING")
        self.assertIn("injector build current", json.loads(out)["detail"])

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--timeout", "0",
                                       "--json"])
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")

    def test_a_NON_POSITIVE_wait_budget_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101",
                                       "--wait-draw-sec", "0", "--json"])
        self.assertEqual(code, pslb.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-WAIT-DRAW")


class TheSetupScript(SeamGuard):
    """The sibling setup-shield-bar-lab.ps1 is owned by another lane; the port invokes it and refuses
    by name when it is missing or fails."""

    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.wait_events = [demo_event(), bar_status_event()]

    def _run(self, run_result: subprocess.CompletedProcess):
        responder = LibResponder(self.wait_events)
        with mock.patch.object(pslb, "_URLOPEN", self.http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(pslb, "_SLEEP", lambda _s: None), \
                mock.patch.object(pslb, "_MONOTONIC", FastClock()), \
                mock.patch.object(pslb, "_RUN", return_value=run_result):
            err = io.StringIO()
            with redirect_stderr(err):
                code = pslb.main(["--base-url", "http://127.0.0.1:5101", "--setup", "--json"])
        return code, err.getvalue()

    def test_a_missing_sibling_is_a_NAMED_refusal(self) -> None:
        sibling = pslb.Path(__file__).resolve().parents[2] / "scripts" / "setup-shield-bar-lab.ps1"
        if sibling.is_file():
            self.skipTest("the sibling has been ported; this case needs it absent")
        code, _ = self._run(subprocess.CompletedProcess([], 1, "", ""))
        self.assertEqual(code, pslb.EXIT_REFUSED)

    def test_a_FAILING_sibling_is_a_NAMED_refusal_with_its_output(self) -> None:
        code, _ = self._run(subprocess.CompletedProcess([], 2, "", "setup exploded"))
        self.assertEqual(code, pslb.EXIT_REFUSED)

    def test_a_PASSING_sibling_lets_the_recipe_run(self) -> None:
        code, _ = self._run(subprocess.CompletedProcess([], 0, "", ""))
        self.assertEqual(code, pslb.EXIT_OK)


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - pslb.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - pslb.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(pslb.EXIT_OK, 0)
        self.assertEqual(pslb.EXIT_FAILED, 1)
        self.assertEqual(pslb.EXIT_REFUSED, 64)

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
        for flag in ("-BaseUrl", "-Setup", "-WaitDrawSec"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("probe-live-shield-bar.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "deadline", "verdict"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--setup", "--wait-draw-sec", "--timeout", "--json"):
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
            if isinstance(target, ast.Name) and target.id in ("pslb", "lib"):
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
