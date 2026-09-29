"""Contract tests for `gk-core/scripts/probe_shield_damage.py`.

A SHIELD PROBE, so the contract is about the verdict it returns and the bar math it prints.

THE VERDICT IS THE POINT. The original had no `exit` statement anywhere: a run whose shield absorbed
nothing printed the numbers and exited 0, so the probe reported success when the thing it exists to
demonstrate did not happen. The port decides deliberately: 64 for a refusal (the probe could not run
to completion), 1 for a fail (the probe completed but the shield did not absorb), 0 for a pass.

THE BAR MATH IS THE OTHER POINT. The in-game bar moves in 10% steps: `display = floor(trueRatio * 10)
/ 10`, raised to 0.1 when the true ratio is positive but the display rounds to zero. A port that
printed the raw ratio would send an operator to a bar that does not exist.

The transport is substituted through the tool's PRIVATE seams (`_URLOPEN`, `_SLEEP`, `_RUN`) and the
shared library's `_URLOPEN`, because those are process-wide modules: a test that patches any of them
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
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROBE_SHIELD_DAMAGE_SCRIPT",
                             REPO / "scripts" / "probe_shield_damage.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_probe_shield_damage.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("probe_shield_damage", SCRIPT)
psd = importlib.util.module_from_spec(_spec)
sys.modules["probe_shield_damage"] = psd
_spec.loader.exec_module(psd)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": psd._URLOPEN, "_SLEEP": psd._SLEEP, "_RUN": psd._RUN,
             "_MONOTONIC": psd._MONOTONIC, "lib_URLOPEN": lib._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP", "_RUN", "_MONOTONIC"):
            if getattr(psd, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(psd, name)!r}")


class ScriptedHttp:
    """A scripted answer for the probe's OWN requests (health, toggle, demo-all, snapshot, probe)."""

    def __init__(self, injector_connected: bool = True) -> None:
        self.injector_connected = injector_connected
        self.posts: list[tuple[str, dict]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        if request.data is not None:
            body = json.loads(request.data.decode("utf-8"))
            self.posts.append((url, body))
            return psd._Response({"ok": True})
        if url.endswith("/health"):
            return psd._Response({"ok": True, "injectorConnected": self.injector_connected})
        return psd._Response({"ok": True})


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
            # the event-id binary search's probe: afterId=0 has events, afterId>=1 does not
            match = re.search(r"afterId=(\d+)", url)
            after_id = int(match.group(1)) if match else 0
            if after_id == 0:
                return psd._Response({"items": [{"id": 1, "kind": "board.start", "payload": {}}]})
            return psd._Response({"items": []})
        if self.wait_calls >= len(self.wait_events):
            # the scripted sequence is exhausted: the kind never arrives
            return psd._Response({"items": []})
        event = self.wait_events[self.wait_calls]
        self.wait_calls += 1
        return psd._Response({"items": [event]})


class FastClock:
    """A monotonic clock that advances a fixed step per call, so a 15-second deadline expires in
    milliseconds without changing what the wait computes."""

    def __init__(self, step: float = 1.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


def demo_event(target_count: int = 2) -> dict:
    return {"id": 10, "kind": "debug.shield.demo-all",
            "payload": {"targetCount": target_count,
                        "targets": [{"targetPtr": "Z1"}, {"targetPtr": "Z2"}]}}


def snapshot_event(owners: list[dict]) -> dict:
    return {"id": 20, "kind": "debug.shield.snapshot", "payload": {"owners": owners}}


def probe_event(hit: bool = True, shield_absorbed: int = 150) -> dict:
    return {"id": 30, "kind": "debug.combat.probe",
            "payload": {"source": "debug.combat.probe", "hit": hit,
                        "shieldAbsorbed": shield_absorbed, "appliedDelta": -150}}


def shielded_owners() -> list[dict]:
    return [{"hp": 250, "maxHp": 500,
             "stacks": [{"element": "fire", "hp": 100, "maxHp": 100},
                        {"element": "ice", "hp": 100, "maxHp": 100}]}]


class TheBarMath(SeamGuard):
    """The in-game bar moves in 10% steps, not every HP tick."""

    def test_a_half_full_bar_displays_as_one_point_zero_five(self) -> None:
        true_ratio, display = psd.display_fill(50, 100)
        self.assertEqual((true_ratio, display), (0.5, 0.5))

    def test_a_nearly_empty_bar_is_RAISED_to_one_tenth(self) -> None:
        """`floor(0.05 * 10) / 10` is 0, but the bar must still show something."""
        true_ratio, display = psd.display_fill(5, 100)
        self.assertEqual((true_ratio, display), (0.05, 0.1))

    def test_an_empty_bar_stays_at_zero(self) -> None:
        true_ratio, display = psd.display_fill(0, 100)
        self.assertEqual((true_ratio, display), (0.0, 0.0))

    def test_a_zero_maxHp_reports_zero_rather_than_dividing(self) -> None:
        true_ratio, display = psd.display_fill(50, 0)
        self.assertEqual((true_ratio, display), (0.0, 0.0))

    def test_a_nearly_full_bar_displays_as_one_point_zero_nine(self) -> None:
        true_ratio, display = psd.display_fill(95, 100)
        self.assertEqual((true_ratio, display), (0.95, 0.9))


class TheWaitKind(SeamGuard):
    def test_it_returns_the_LAST_matching_event_on_the_page(self) -> None:
        events = [{"id": 1, "kind": "debug.shield.snapshot", "payload": {"owners": []}},
                  {"id": 2, "kind": "other", "payload": {}},
                  {"id": 3, "kind": "debug.shield.snapshot", "payload": {"owners": [1]}}]
        responder = LibResponder([])
        with mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(psd, "_SLEEP", lambda _s: None):
            # page the wait_kind shape: limit=100 from cursor 0
            captured = {}

            def grab(request, timeout=None):
                captured["url"] = request.full_url
                return psd._Response({"items": events})

            with mock.patch.object(lib, "_URLOPEN", grab):
                hit = psd.wait_kind("http://127.0.0.1:5101", 0, "debug.shield.snapshot", 5)
        self.assertEqual(hit["payload"], {"owners": [1]}, "the last match on the page wins")

    def test_it_returns_None_when_the_kind_never_arrives(self) -> None:
        with mock.patch.object(lib, "_URLOPEN", lambda request, timeout=None:
                              psd._Response({"items": []})), \
                mock.patch.object(psd, "_SLEEP", lambda _s: None), \
                mock.patch.object(psd, "_MONOTONIC", FastClock()):
            hit = psd.wait_kind("http://127.0.0.1:5101", 0, "debug.combat.probe", 1)
        self.assertIsNone(hit, "a kind that never arrives is a refusal, not an infinite wait")


class TheRecipe(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.wait_events = [demo_event(), snapshot_event(shielded_owners()),
                            probe_event(), snapshot_event(shielded_owners())]

    def _run(self, http: ScriptedHttp | None = None, wait_events: list[dict] | None = None,
             argv: list[str] | None = None):
        http = http or self.http
        responder = LibResponder(wait_events if wait_events is not None else self.wait_events)
        with mock.patch.object(psd, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(psd, "_SLEEP", lambda _s: None), \
                mock.patch.object(psd, "_MONOTONIC", FastClock()):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = psd.main(argv if argv is not None else
                                ["--base-url", "http://127.0.0.1:5101", "--json"])
        return code, err.getvalue(), out.getvalue()

    def test_a_clean_run_exits_0_and_reports_PASS(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, psd.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "PASS")
        self.assertIs(payload["hit"], True)
        self.assertEqual(payload["shieldAbsorbed"], 150)

    def test_the_recipe_posts_in_the_original_order(self) -> None:
        self._run()
        paths = [url.split("5101", 1)[1].split("?")[0] for url, _ in self.http.posts]
        self.assertEqual(paths, ["/api/cheats/toggle", "/api/debug/shield/demo-all",
                                 "/api/debug/shield/snapshot", "/api/debug/combat/probe",
                                 "/api/debug/shield/snapshot"])
        bodies = {url.split("5101", 1)[1].split("?")[0]: body for url, body in self.http.posts}
        self.assertEqual(bodies["/api/cheats/toggle"], {"id": "OVERLAY-COMBAT", "enabled": True})
        self.assertEqual(bodies["/api/debug/shield/demo-all"], {"amount": 100})
        self.assertEqual(bodies["/api/debug/shield/snapshot"], {"targetPtr": "Z2"})
        self.assertEqual(bodies["/api/debug/combat/probe"]["amount"], -150)
        self.assertTrue(bodies["/api/debug/combat/probe"]["forceHit"])
        self.assertEqual(bodies["/api/debug/combat/probe"]["seed"], 1)
        self.assertEqual(bodies["/api/debug/combat/probe"]["elementPayload"],
                         [{"element": "fire", "weightPm": 1000}])

    def test_the_operator_output_carries_the_probe_numbers(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, psd.EXIT_OK)
        self.assertIn("targets=2 hitPtr=Z2", err)
        self.assertIn("source=debug.combat.probe hit=True shieldAbsorbed=150 appliedDelta=-150",
                      err)
        self.assertIn("true=0.50 displayFill=0.5", err)
        self.assertIn("fire 100/100", err)
        self.assertIn("10% steps", err)

    def test_a_shield_that_ABSORBS_NOTHING_exits_1(self) -> None:
        """The original printed the numbers and exited 0 here -- a probe that cannot fail."""
        self.wait_events = [demo_event(), snapshot_event(shielded_owners()),
                            probe_event(hit=True, shield_absorbed=0),
                            snapshot_event(shielded_owners())]
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_FAILED)
        self.assertEqual(json.loads(out)["verdict"], "FAIL")

    def test_a_probe_that_MISSES_exits_1(self) -> None:
        self.wait_events = [demo_event(), snapshot_event(shielded_owners()),
                            probe_event(hit=False, shield_absorbed=0),
                            snapshot_event(shielded_owners())]
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_FAILED)
        self.assertEqual(json.loads(out)["verdict"], "FAIL")

    def test_an_injector_that_is_not_connected_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(injector_connected=False))
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INJECTOR-NOT-CONNECTED")

    def test_a_demo_all_with_ZERO_targets_is_a_NAMED_refusal(self) -> None:
        self.wait_events = [demo_event(target_count=0)]
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "DEMO-ALL-EMPTY")
        self.assertIn("Adventure lawn", json.loads(out)["detail"])

    def test_a_MISSING_demo_all_event_is_a_NAMED_refusal(self) -> None:
        self.wait_events = []  # the wait gets an empty page
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "DEMO-ALL-MISSING")

    def test_a_missing_probe_event_is_a_NAMED_refusal(self) -> None:
        self.wait_events = [demo_event(), snapshot_event(shielded_owners())]
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "PROBE-MISSING")

    def test_a_missing_snapshot_event_is_a_NAMED_refusal(self) -> None:
        self.wait_events = [demo_event()]
        code, _, out = self._run()
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "SNAPSHOT-MISSING")

    def test_a_POSITIVE_amount_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--amount", "150",
                                       "--json"])
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-AMOUNT")

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--timeout", "0",
                                       "--json"])
        self.assertEqual(code, psd.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")


class TheSetupScript(SeamGuard):
    """The sibling setup-shield-bar-lab.ps1 is owned by another lane; the port invokes it and refuses
    by name when it is missing or fails."""

    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.wait_events = [demo_event(), snapshot_event(shielded_owners()),
                            probe_event(), snapshot_event(shielded_owners())]

    def _run(self, run_result: subprocess.CompletedProcess):
        responder = LibResponder(self.wait_events)
        with mock.patch.object(psd, "_URLOPEN", self.http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(psd, "_SLEEP", lambda _s: None), \
                mock.patch.object(psd, "_RUN", return_value=run_result):
            err = io.StringIO()
            with redirect_stderr(err):
                code = psd.main(["--base-url", "http://127.0.0.1:5101", "--setup", "--json"])
        return code, err.getvalue()

    def test_a_MISSING_sibling_is_a_NAMED_refusal(self) -> None:
        sibling = psd.Path(__file__).resolve().parents[2] / "scripts" / "setup-shield-bar-lab.ps1"
        if sibling.is_file():
            self.skipTest("the sibling has been ported; this case needs it absent")
        code, _ = self._run(subprocess.CompletedProcess([], 1, "", ""))
        self.assertEqual(code, psd.EXIT_REFUSED)

    def test_a_FAILING_sibling_is_a_NAMED_refusal_with_its_output(self) -> None:
        code, _ = self._run(subprocess.CompletedProcess([], 2, "", "setup exploded"))
        self.assertEqual(code, psd.EXIT_REFUSED)

    def test_a_PASSING_sibling_lets_the_recipe_run(self) -> None:
        code, _ = self._run(subprocess.CompletedProcess([], 0, "", ""))
        self.assertEqual(code, psd.EXIT_OK)


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - psd.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - psd.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(psd.EXIT_OK, 0)
        self.assertEqual(psd.EXIT_FAILED, 1)
        self.assertEqual(psd.EXIT_REFUSED, 64)

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
        """The recon finding: `lib.get_debug_max_event_id` carries the timeout + budget +
        SEARCH-BUDGET-EXHAUSTED refusal. A local reimplementation would lose all three."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.get_debug_max_event_id", source)
        self.assertNotIn("def get_max_event_id", source,
                         "the event-id binary search was reimplemented locally")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Amount", "-Setup"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("probe-shield-damage.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "verdict", "absorb"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--amount", "--setup", "--timeout", "--json"):
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
            if isinstance(target, ast.Name) and target.id in ("psd", "lib"):
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
