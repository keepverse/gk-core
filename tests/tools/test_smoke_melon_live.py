"""Contract tests for `gk-core/scripts/smoke_melon_live.py`.

A LIVE SMOKE, so the contract is about the four steps and the gate that precedes them.

THE GATE IS THE POINT. The health gate requires injectorConnected && !simEnabled && source ==
"injector" -- all three. A port that checked only injectorConnected would smoke a SIM board and
report success on a run that is not the Melon LIVE run the smoke exists to prove.

THE STEPS ARE THE OTHER POINT. session/start, p1-baseline and debug/events each pass or fail by
their own criterion, and the events step passes on ANY of plant/zombie/damage -- "spawn is enough for
smoke; damage may need pea shots / longer wait". A port that required all three would fail a run the
original passed.

The transport is substituted through the tool's PRIVATE seams (`_URLOPEN`, `_SLEEP`) and the shared
library's `_URLOPEN`, because those are process-wide modules: a test that patches any of them
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
SCRIPT = Path(os.environ.get("SMOKE_MELON_LIVE_SCRIPT",
                             REPO / "scripts" / "smoke_melon_live.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_smoke_melon_live.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("smoke_melon_live", SCRIPT)
sml = importlib.util.module_from_spec(_spec)
sys.modules["smoke_melon_live"] = sml
_spec.loader.exec_module(sml)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": sml._URLOPEN, "_SLEEP": sml._SLEEP, "lib_URLOPEN": lib._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP"):
            if getattr(sml, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(sml, name)!r}")


class ScriptedHttp:
    """A scripted answer for the smoke's OWN requests (health, session, scenario, events)."""

    def __init__(self, injector_connected: bool = True, sim_enabled: bool = False,
                 source: str = "injector", session_ok: bool = True, scenario_ok: bool = True,
                 event_kinds: list[str] | None = None) -> None:
        self.injector_connected = injector_connected
        self.sim_enabled = sim_enabled
        self.source = source
        self.session_ok = session_ok
        self.scenario_ok = scenario_ok
        self.event_kinds = event_kinds if event_kinds is not None else ["debug.spawn.plant"]
        self.posts: list[tuple[str, dict]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        if request.data is not None:
            body = json.loads(request.data.decode("utf-8"))
            self.posts.append((url, body))
            if url.endswith("/api/debug/session/start"):
                return sml._Response({"ok": self.session_ok, "scenarioId": "s-1"})
            if url.endswith("/api/debug/scenario/p1-baseline"):
                return sml._Response({"ok": self.scenario_ok, "steps": 3})
            return sml._Response({"ok": True})
        if url.endswith("/health"):
            return sml._Response({"ok": True, "injectorConnected": self.injector_connected,
                                  "simEnabled": self.sim_enabled, "source": self.source})
        if "/api/debug/events" in url:
            return sml._Response({"items": [{"id": i, "kind": k, "payload": {}}
                                            for i, k in enumerate(self.event_kinds)]})
        return sml._Response({"ok": True})


class LibResponder:
    """The event-id binary search's probe pages: afterId=0 has events, afterId>=1 does not."""

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        match = re.search(r"afterId=(\d+)", url)
        after_id = int(match.group(1)) if match else 0
        if after_id == 0:
            return sml._Response({"items": [{"id": 1, "kind": "board.start", "payload": {}}]})
        return sml._Response({"items": []})


class TheHappyRun(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()

    def _run(self, http: ScriptedHttp | None = None, argv: list[str] | None = None):
        http = http or self.http
        with mock.patch.object(sml, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", LibResponder()), \
                mock.patch.object(sml, "_SLEEP", lambda _s: None):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = sml.main(argv if argv is not None else
                                ["--base-url", "http://127.0.0.1:5101", "--json"])
        return code, err.getvalue(), out.getvalue()

    def test_a_clean_run_exits_0_and_reports_PASS(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "PASS")
        self.assertTrue(all(s["ok"] for s in payload["steps"]))
        self.assertIn("Melon LIVE smoke: PASSED health + session + p1-baseline + spawn/damage events.",
                      err)
        self.assertIn("Next: fill Priority A–C", err)

    def test_the_four_steps_are_reported_in_order(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        rows = [line for line in err.splitlines() if line.startswith("[")]
        self.assertEqual([r.split("]")[0] + "]" for r in rows],
                         ["[PASS]", "[PASS]", "[PASS]", "[PASS]"])
        names = [r.split("] ")[1].split(":")[0] for r in rows]
        self.assertEqual(names, ["health", "session/start", "scenario/p1-baseline", "debug/events"])

    def test_the_step_details_carry_the_original_fields(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        self.assertIn("[PASS] health: injectorConnected=True simEnabled=False source=injector", err)
        self.assertIn("[PASS] session/start: ok=True scenarioId=s-1", err)
        self.assertIn("[PASS] scenario/p1-baseline: ok=True steps=3", err)
        self.assertIn("[PASS] debug/events: afterId=0 count=1 kinds=[debug.spawn.plant] "
                      "plant=True zombie=False damage=False", err)

    def test_the_events_step_passes_on_A_SPAWN_alone(self) -> None:
        """The original: 'Spawn is enough for smoke; damage may need pea shots / longer wait'."""
        self.http = ScriptedHttp(event_kinds=["debug.spawn.zombie"])
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        self.assertIn("plant=False zombie=True damage=False", err)

    def test_the_events_step_passes_on_DAMAGE_alone(self) -> None:
        self.http = ScriptedHttp(event_kinds=["zombie.damage"])
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        self.assertIn("plant=False zombie=False damage=True", err)

    def test_the_events_step_fails_on_NO_relevant_kind(self) -> None:
        self.http = ScriptedHttp(event_kinds=["board.start"])
        code, err, out = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] debug/events", err)
        self.assertIn("Melon LIVE smoke: FAILED", err)

    def test_a_SIM_board_ABORTS_at_the_gate(self) -> None:
        """The gate requires !simEnabled. A SIM board is not the Melon LIVE run."""
        self.http = ScriptedHttp(sim_enabled=True)
        code, err, out = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] health", err)
        self.assertIn("Aborting: fix Melon injector connection", err)
        self.assertNotIn("session/start", err, "the gate aborted before the scenario steps")

    def test_a_disconnected_injector_ABORTS_at_the_gate(self) -> None:
        self.http = ScriptedHttp(injector_connected=False)
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] health", err)
        self.assertIn("Aborting", err)

    def test_a_non_injector_source_ABORTS_at_the_gate(self) -> None:
        self.http = ScriptedHttp(source="sim")
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] health", err)

    def test_a_failing_session_start_ABORTS(self) -> None:
        """The original exited 1 on a session/start exception -- the smoke cannot continue without a
        debug session."""
        self.http = ScriptedHttp(session_ok=False)
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] session/start", err)

    def test_a_failing_scenario_FAILs_the_smoke(self) -> None:
        self.http = ScriptedHttp(scenario_ok=False)
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_FAILED)
        self.assertIn("[FAIL] scenario/p1-baseline", err)
        self.assertIn("Melon LIVE smoke: FAILED", err)

    def test_the_afterId_baseline_is_captured_and_forwarded(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, sml.EXIT_OK)
        self.assertIn("afterId baseline: 0", err)
        self.assertIn("afterId=0 count=1", err)

    def test_a_NEGATIVE_wait_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101",
                                       "--wait-seconds", "-1", "--json"])
        self.assertEqual(code, sml.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-WAIT")

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--timeout", "0",
                                       "--json"])
        self.assertEqual(code, sml.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")


class TheAfterIdBaseline(SeamGuard):
    """The original's WARN-and-continue: a baseline that cannot be captured must not stop a smoke
    that can still measure the events from 0."""

    def test_a_refused_baseline_WARNs_and_the_smoke_carries_on(self) -> None:
        http = ScriptedHttp()

        def refusing(request, timeout=None):
            raise lib.Refusal("SEARCH-BUDGET-EXHAUSTED", "the harness refuses the search")

        with mock.patch.object(sml, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", refusing), \
                mock.patch.object(sml, "_SLEEP", lambda _s: None):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = sml.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sml.EXIT_OK)
        self.assertIn("WARN: could not capture afterId", err.getvalue())
        self.assertIn("afterId=0 count=1", err.getvalue(), "the events were still measured from 0")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - sml.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - sml.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(sml.EXIT_OK, 0)
        self.assertEqual(sml.EXIT_FAILED, 1)
        self.assertEqual(sml.EXIT_REFUSED, 64)

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
        for flag in ("-BaseUrl", "-WaitSeconds"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("smoke-melon-live.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "budget", "gate"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--wait-seconds", "--timeout", "--json"):
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
            if isinstance(target, ast.Name) and target.id in ("sml", "lib"):
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
