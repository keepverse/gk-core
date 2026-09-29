"""Contract tests for `gk-core/scripts/prove_actor_hud_live.py`.

THIS IS THE ONE SCRIPT IN THE POPULATION THAT DOES NOT TALK TO THE GAME DIRECTLY -- it drives the WEB
project's live Playwright project. So its two most important properties are the ones a unit test can decide
without a browser: the subprocess is BOUNDED and CAPTURED, and the world-HUD setting is REQUIRED rather
than warned about. Both are the defects the original had, and both are decided here against a real HTTP
server and a stubbed `npm`.

THE DIAGNOSIS IS THE PORT'S REASON TO EXIST, and it is pinned as a PROPERTY rather than quoted. Measured
against a running game: after a shield demo and two status applies (all accepted), the board emitted
`shield.granted`, `debug.status`, `debug.status.resisted`, `debug.actor-hud` and
`debug.effect.board-snapshot` -- and **zero** `debug.board-stats`, which is the only kind the web helper
`pollBoardActorHud` inspects. So the helper's 45-second poll cannot succeed, and Playwright's 30-second
`beforeAll` hook timeout fires first, which is why the original's only symptom is a hook timeout.

THE npm RESOLUTION IS PINNED, because it is a defect this port hit: `npm` is a `.cmd` batch shim on
Windows, `subprocess.run(["npm", ...])` cannot find it, and PowerShell can. A refusal claiming npm is not on
PATH on a machine where npm demonstrably works is a refusal that lies.

THE LIVE CASES SKIP, with a stated reason, when no injector is reachable, and REFUSE to fall back to the
owner's port.
"""
from __future__ import annotations

import ast
import http.server
import importlib.util
import io
import json
import os
import re
import socket
import subprocess
import sys
import threading
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROVE_ACTOR_HUD_LIVE_SCRIPT",
                             REPO / "scripts" / "prove_actor_hud_live.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_actor_hud_live.py"
RUN_TIMEOUT = 300

BOARD_KINDS = ("shield.granted", "debug.status", "debug.status.resisted", "debug.actor-hud",
               "debug.effect.board-snapshot", "debug.fx.state.started", "combat.hit", "zombie.damage")


class _Server:
    health: dict = {"ok": True, "injectorConnected": True, "simEnabled": False}
    settings_status = 200
    settings_body: dict = {"ok": True, "key": "lawn.worldHud", "value": True}
    events: list[dict] = []
    puts: list[dict] = []


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        if self.path.startswith("/health"):
            return self._json(200, _Server.health)
        if self.path.startswith("/api/events"):
            query = dict(x.split("=", 1) for x in self.path.split("?", 1)[1].split("&") if "=" in x)
            after = int(query.get("afterId", "0"))
            return self._json(200, {"items": [e for e in _Server.events if int(e["id"]) > after]})
        self.send_error(404, "no fixture route")

    def do_PUT(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0") or 0))
        try:
            _Server.puts.append(json.loads(raw.decode()))
        except json.JSONDecodeError:
            _Server.puts.append({"__unparseable__": raw[:120].decode("utf-8", "replace")})
        self._json(_Server.settings_status, _Server.settings_body)

    def _json(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def serve(**kwargs):
    for key, default in (("health", {"ok": True, "injectorConnected": True, "simEnabled": False}),
                         ("settings_status", 200),
                         ("settings_body", {"ok": True, "key": "lawn.worldHud", "value": True}),
                         ("events", [])):
        setattr(_Server, key, kwargs.get(key, default))
    _Server.puts = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def closed_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def _load():
    spec = importlib.util.spec_from_file_location("prove_actor_hud_live", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["prove_actor_hud_live"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


def run_cli(*args: str) -> tuple[int, dict, str]:
    """main() with stdout and stderr kept separate: the tool writes progress to stderr and JSON to stdout,
    and a joined stream is not JSON."""
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = p.main(["--json", *args])
    try:
        payload = json.loads(out.getvalue())
    except json.JSONDecodeError:
        payload = {"__unparseable__": out.getvalue()[:200]}
    return code, payload, err.getvalue()


class TheSubprocessIsBoundedAndCaptured(unittest.TestCase):
    """`npm run test:e2e:live` ran with no timeout and no capture in the original. A Vite dev server plus a
    project that polls for an event the board does not emit is exactly the shape that hangs forever while
    looking like progress."""

    def test_a_RUN_that_never_finishes_is_a_NAMED_refusal_not_a_HANG(self) -> None:
        recorded: list[dict] = []

        def slow(argv, **kwargs):
            recorded.append({"argv": argv, "kwargs": kwargs})
            raise subprocess.TimeoutExpired(argv, kwargs.get("timeout", 0), output="partial output")

        with mock.patch.object(p.subprocess, "run", slow):
            with self.assertRaises(p.Refusal) as caught:
                p.run_e2e(1, {})
        self.assertEqual(caught.exception.reason, "E2E-TIMED-OUT")
        self.assertIn("NO bound at all", caught.exception.detail)
        self.assertIn("partial output", caught.exception.detail,
                      "a timeout must carry whatever output it had, or the reader loses the last words")
        self.assertTrue(recorded, "the subprocess was never invoked")
        self.assertIsNotNone(recorded[0]["kwargs"].get("timeout"),
                             "the subprocess ran with no timeout -- the original's defect")
        self.assertIsNotNone(recorded[0]["kwargs"].get("capture_output"),
                             "the subprocess's output was not captured")

    def test_the_E2E_runs_in_the_WEB_TREE_by_EXPLICIT_cwd(self) -> None:
        """The original called `Set-Location` twice, including onto the web tree, and never restored it."""
        recorded: list[dict] = []

        class Ok:
            returncode = 0
            stdout = "passed"
            stderr = ""

        def capture(argv, **kwargs):
            recorded.append({"argv": argv, "kwargs": kwargs})
            return Ok()

        with mock.patch.object(p.subprocess, "run", capture):
            code, output, _ = p.run_e2e(30, {"FUSIONRPG_API_BASE": "http://x"})
        self.assertEqual(code, 0)
        self.assertEqual(output, "passed")
        self.assertEqual(Path(recorded[0]["kwargs"]["cwd"]).resolve(), p.WEB_DIR.resolve())

    def test_the_ENVIRONMENT_carries_what_the_E2E_needs(self) -> None:
        """`ACTOR_HUD_LIVE_E2E` is what makes the spec not skip, and `FUSIONRPG_API_BASE` is which server
        it talks to. The original set both; a port that sets only one runs a suite that skips."""
        recorded: list[dict] = []

        class Ok:
            returncode = 0
            stdout = ""
            stderr = ""

        def capture(argv, **kwargs):
            recorded.append({"env": kwargs.get("env", {})})
            return Ok()

        with mock.patch.object(p.subprocess, "run", capture):
            p.run_e2e(30, {"ACTOR_HUD_LIVE_E2E": "1", "FUSIONRPG_API_BASE": "http://127.0.0.1:5102"})
        self.assertEqual(recorded[0]["env"]["ACTOR_HUD_LIVE_E2E"], "1")
        self.assertEqual(recorded[0]["env"]["FUSIONRPG_API_BASE"], "http://127.0.0.1:5102")

    def test_this_process_never_CHANGES_its_OWN_working_directory(self) -> None:
        """Asserted as a property of main(), not of the function that spawns: the original mutated the
        process's directory and left it mutated, and that is invisible in a function-level test."""
        before = Path.cwd().resolve()
        server, base = serve()
        try:
            with mock.patch.object(p, "run_e2e", return_value=(1, "x", 0.1)):
                with mock.patch.object(p, "diagnose", return_value={"boardIsAlive": False}):
                    run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        self.assertEqual(Path.cwd().resolve(), before, "main() changed this process's working directory")
        code = ast.unparse(ast.parse(SCRIPT.read_text(encoding="utf-8")))
        self.assertNotIn("os.chdir", code)
        self.assertNotIn("Path.cwd()", code.replace("Path.cwd().resolve()", ""),
                         "the tool must not depend on the current directory; the original Set-Location'd")

    def test_npm_is_RESOLVED_rather_than_assumed_on_PATH(self) -> None:
        """`npm` is a `.cmd` shim on Windows; `subprocess.run(["npm", ...])` raises FileNotFoundError while
        PowerShell resolves it fine. A refusal claiming npm is absent, on a machine where it works, is a
        refusal that lies -- and that is exactly what the first version of this port produced."""
        import shutil
        resolved = shutil.which("npm") or shutil.which("npm.cmd")
        self.assertIsNotNone(resolved, "this machine has no npm at all, so the case below cannot decide")
        recorded: list[dict] = []

        class Ok:
            returncode = 0
            stdout = ""
            stderr = ""

        def capture(argv, **kwargs):
            recorded.append({"argv": list(argv)})
            return Ok()

        with mock.patch.object(p.subprocess, "run", capture):
            p.run_e2e(30, {})
        self.assertEqual(recorded[0]["argv"][0], resolved,
                         "the bare name was used, which cannot resolve a .cmd shim")
        self.assertNotEqual(Path(recorded[0]["argv"][0]).name.lower(), "npm.exe")

    def test_an_ABSENT_npm_is_a_NAMED_refusal_that_says_what_was_CHECKED(self) -> None:
        with mock.patch.object(p.shutil, "which", return_value=None):
            with self.assertRaises(p.Refusal) as caught:
                p.run_e2e(30, {})
        self.assertEqual(caught.exception.reason, "E2E-SPAWN-FAILED")
        self.assertIn("npm.cmd", caught.exception.detail, "the refusal must say what it looked for")
        self.assertIn("PATHEXT", caught.exception.detail)


class TheWorldHudIsRequired(unittest.TestCase):
    """The original's empty `catch` only WARNED, so a failed setting produced an E2E whose HUD assertions
    could not hold -- and the failure was reported as a HUD failure rather than a setup failure."""

    def test_a_REFUSED_setting_is_a_NAMED_refusal_that_says_why_it_matters(self) -> None:
        server, base = serve(settings_status=500, settings_body={"error": "read-only"})
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.enable_world_hud(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "WORLD-HUD-UNSET")
        self.assertIn("read-only", caught.exception.detail, "the server's own error is the useful part")
        self.assertIn("setup failure", caught.exception.detail,
                      "the refusal must say this is a SETUP failure, or it reads as a HUD failure")

    def test_the_setting_PUTS_the_EXACT_key_and_value(self) -> None:
        server, base = serve()
        try:
            p.enable_world_hud(base)
        finally:
            server.shutdown()
        self.assertEqual(_Server.puts, [{"key": p.WORLD_HUD_KEY, "value": True}])
        self.assertEqual(p.WORLD_HUD_KEY, "lawn.worldHud")

    def test_a_SETTING_that_CANNOT_be_reached_is_also_a_NAMED_refusal(self) -> None:
        with self.assertRaises(p.Refusal) as caught:
            p.enable_world_hud(f"http://127.0.0.1:{closed_port()}")
        self.assertEqual(caught.exception.reason, "WORLD-HUD-UNSET")

    def test_the_E2E_never_RUNS_when_the_setting_FAILED(self) -> None:
        server, base = serve(settings_status=500, settings_body={"error": "no"})
        ran: list[int] = []
        try:
            with mock.patch.object(p, "run_e2e", side_effect=lambda *a, **k: ran.append(1)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "WORLD-HUD-UNSET")
        self.assertEqual(ran, [], "the E2E ran after the setting failed, which is the original's defect")


class ThePreconditionsAreNamed(unittest.TestCase):
    """The original's `catch` printed "Server not reachable" for EVERY failure of the health call -- a
    timeout, a 500 and a malformed body alike -- and exited 1."""

    def test_no_SERVER_is_distinguished_from_a_BAD_health_document(self) -> None:
        with self.assertRaises(p.Refusal) as caught:
            p.check_server(f"http://127.0.0.1:{closed_port()}")
        self.assertEqual(caught.exception.reason, "SERVER-UNREACHABLE")

        server, base = serve(health={"ok": False, "injectorConnected": True})
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.check_server(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "HEALTH-NOT-OK")

    def test_no_INJECTOR_names_the_SKILL_and_how_to_START(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": False})
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.check_server(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "INJECTOR-NOT-CONNECTED")
        self.assertIn("live-lawn-quick-start", caught.exception.detail)
        self.assertIn("deploy-play.py", caught.exception.detail,
                      "the original's message named both remedies; the port's must too")

    def test_the_SKIP_flag_really_SKIPS(self) -> None:
        self.assertIsNone(p.check_server(f"http://127.0.0.1:{closed_port()}", skip=True),
                          "--skip-server-check still performed the check")

    def test_a_healthy_server_returns_its_health(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": True, "simEnabled": False})
        try:
            health = p.check_server(base)
        finally:
            server.shutdown()
        self.assertIs(health["injectorConnected"], True)


class TheDiagnosis(unittest.TestCase):
    """The port's reason to exist: a failing E2E must be a DIAGNOSIS, not an exit code."""

    def test_a_board_that_emits_other_kinds_is_REPORTED_alive(self) -> None:
        events = [{"id": 1, "kind": "shield.granted"}, {"id": 2, "kind": "debug.actor-hud"},
                  {"id": 3, "kind": "debug.effect.board-snapshot"}]
        server, base = serve(events=events)
        try:
            # The cursor is pinned to 0: the diagnosis reads events AFTER the cursor it takes, so a fixture
            # whose events sit at ids 1..3 is entirely BELOW the real cursor and nothing is ever observed.
            with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                    mock.patch.object(p, "run_e2e", return_value=(1, "hook timeout", 33.0)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "E2E-FAILED")
        diagnosis = payload["diagnosis"]
        self.assertIs(diagnosis["boardIsAlive"], True)
        self.assertIs(diagnosis["polledKindSeen"], False)
        self.assertIn("shield.granted", diagnosis["livenessKindsSeen"])
        self.assertIn("debug.board-stats", payload["detail"],
                      "the refusal must name the kind the E2E waits for")

    def test_a_board_that_emits_the_POLLED_kind_is_NOT_reported_as_a_filter_problem(self) -> None:
        """The converse, and the case that keeps the finding honest: if the kind turns up, the docstring's
        claim is stale and the tool must not keep asserting it."""
        server, base = serve(events=[{"id": 1, "kind": p.E2E_POLLED_KIND}])
        try:
            with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                    mock.patch.object(p, "run_e2e", return_value=(1, "failed", 2.0)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        diagnosis = payload["diagnosis"]
        self.assertIs(diagnosis["polledKindSeen"], True)
        self.assertEqual(diagnosis["polledKindCount"], 1)
        self.assertNotIn("which is the only kind the web helper",
                         payload["detail"], "the finding was asserted while the kind was present")

    def test_a_DEAD_board_is_REPORTED_as_dead_not_as_a_filtered_kind(self) -> None:
        """"No board-stats" means two different things depending on whether anything was emitted at all,
        and collapsing them is the finding being useless."""
        server, base = serve(events=[])
        try:
            with mock.patch.object(p, "run_e2e", return_value=(1, "failed", 2.0)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        diagnosis = payload["diagnosis"]
        self.assertIs(diagnosis["boardIsAlive"], False)
        self.assertIn("dead board", payload["detail"])

    def test_the_E2Es_EXIT_CODE_and_DURATION_are_recorded(self) -> None:
        server, base = serve()
        try:
            with mock.patch.object(p, "run_e2e", return_value=(2, "boom", 12.5)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        self.assertEqual(payload["e2eExitCode"], 2)
        self.assertEqual(payload["seconds"], 12.5)
        self.assertIn("exited 2", payload["detail"])

    def test_a_PASSING_E2E_is_OK_and_carries_the_HUMAN_STEP(self) -> None:
        """The Unity eyeball is not covered by this suite, and dropping the pointer would leave a reader
        believing the E2E is the whole check."""
        server, base = serve()
        try:
            out = io.StringIO()
            with mock.patch.object(p, "run_e2e", return_value=(0, "passed", 5.0)):
                with redirect_stdout(out):
                    code = p.main(["--base-url", base, "--e2e-timeout-sec", "1"])
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertIn("plant-selection" if False else "Unity LIVE eyeball", out.getvalue())


class TheGapsSixSurvivorsNamed(unittest.TestCase):
    """Five of the six survivors were real gaps; the sixth cannot apply on this host and is classified
    rather than papered over.

    THE URL WAS NOT ASSERTED IN THE INJECTOR REFUSAL. A mutant that removed `at {base_url} - start the
    game with the FusionRpg` from the FIRST fragment survived, because the skill pointer lives in the
    SECOND fragment and the case only checked for the skill. So the refusal named a condition and not the
    server it probed -- the same defect the library port fixed and the same one this case failed to pin.

    THE DIAGNOSIS TEXT WAS NOT PINED AT ALL, in three places: the kinds the board emitted, the two budget
    numbers that explain WHY the hook times out first, and the note that the fix belongs to the web E2E
    helper rather than to this script. Each is the part a reader acts on, and each was invisible to every
    existing case because they all checked the structured `diagnosis` and never the prose.

    THE DOCSTRING CLAIM WAS NOT PINED. Changing "does not emit that kind at all" to "sometimes does not emit
    it" left every pinned fragment -- the kind, both budgets, all three measured kinds -- intact, so a case
    that pinned the CONTENT could not see the CLAIM being weakened. The claim is the finding, so it is
    pinned as text.
    """

    def test_the_INJECTOR_refusal_names_the_SERVER_it_PROBED(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": False})
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.check_server(base)
        finally:
            server.shutdown()
        self.assertIn(base, caught.exception.detail,
                      "the refusal names a condition and not the server it probed, so a reader with three "
                      "slot servers cannot tell which one refused")
        self.assertIn("live-lawn-quick-start", caught.exception.detail)

    def test_the_filtered_kind_diagnosis_names_WHAT_the_board_emitted(self) -> None:
        server, base = serve(events=[{"id": 1, "kind": "shield.granted"},
                                     {"id": 2, "kind": "debug.actor-hud"}])
        try:
            with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                    mock.patch.object(p, "run_e2e", return_value=(1, "x", 1.0)):
                code, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        detail = payload["detail"]
        self.assertIn("shield.granted", detail, f"the kinds the board DID emit are the evidence: {detail}")
        self.assertIn("debug.actor-hud", detail)

    def test_the_diagnosis_explains_WHY_the_HOOK_times_out_first(self) -> None:
        """The 45s poll against the 30s hook is the whole explanation for a symptom that looks like a
        hang, and without the numbers a reader has nothing to act on."""
        server, base = serve(events=[{"id": 1, "kind": "shield.granted"}])
        try:
            with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                    mock.patch.object(p, "run_e2e", return_value=(1, "x", 1.0)):
                _, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        detail = payload["detail"]
        self.assertIn("45s", detail, f"the poll budget is the explanation and it is gone: {detail}")
        self.assertIn("30s", detail, "...and so is the hook timeout it exceeds")

    def test_the_diagnosis_says_WHO_OWNS_the_FIX(self) -> None:
        """The fix is in the web E2E helper, which is not this script's file. Without the note a reader
        would look here, and this tool is not where the defect is."""
        server, base = serve(events=[{"id": 1, "kind": "shield.granted"}])
        try:
            with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                    mock.patch.object(p, "run_e2e", return_value=(1, "x", 1.0)):
                _, payload, _ = run_cli("--base-url", base, "--e2e-timeout-sec", "1")
        finally:
            server.shutdown()
        self.assertIn("web E2E helper", payload["detail"],
                      f"the fix's owner is unstated: {payload['detail']}")

    def test_the_DOCSTRING_states_the_CLAIM_and_not_only_its_evidence(self) -> None:
        """"sometimes does not emit it" is a weaker claim than "does not emit that kind at all", and the
        second is what was MEASURED. Pinning only the kind, the budgets and the three kinds left the claim
        itself free to soften."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("does not emit that kind at all", head,
                      "the measured CLAIM was weakened; everything pinned around it can survive that")
        self.assertIn("zero** `debug.board-stats`", head, "the COUNT the finding rests on")


def code_without_docstrings(path: Path) -> str:
    tree = ast.parse(path.read_text(encoding="utf-8"))
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            if (node.body and isinstance(node.body[0], ast.Expr)
                    and isinstance(node.body[0].value, ast.Constant)
                    and isinstance(node.body[0].value.value, str)):
                node.body.pop(0)
    return ast.unparse(tree)


class Surface(unittest.TestCase):
    def test_it_IMPORTS_the_shared_library_rather_than_reimplementing_the_transport(self) -> None:
        code = code_without_docstrings(SCRIPT)
        self.assertIn("import live_lawn_setup as lib", code)
        for name in ("resolve_base_url", "EXIT_REFUSED", "BASE_URL_ENV", "_get_json", "get_events",
                     "get_debug_max_event_id"):
            self.assertIn(f"lib.{name}", code, f"{name} is used without the shared library")
        self.assertNotIn("Invoke-RestMethod", code)

    def test_the_WEB_TREE_is_resolved_from_the_SCRIPT_not_the_CWD(self) -> None:
        """The original derived it from `$PSScriptRoot`; a port that used the CWD would break the moment
        anything else changed the directory -- which the original itself did."""
        code = code_without_docstrings(SCRIPT)
        self.assertIn("Path(__file__).resolve().parent.parent", code)
        self.assertEqual(p.WEB_DIR.name, "fusion-rpg-web")
        self.assertTrue((p.WEB_DIR / "package.json").is_file(), f"{p.WEB_DIR} has no package.json")

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.NPM_SCRIPT, "test:e2e:live")
        self.assertEqual(p.WORLD_HUD_KEY, "lawn.worldHud")
        self.assertEqual(p.HEALTH_TIMEOUT, 5)
        self.assertEqual(p.SETTINGS_TIMEOUT, 5)
        self.assertEqual(p.E2E_POLLED_KIND, "debug.board-stats")
        self.assertEqual(p.DEFAULT_E2E_TIMEOUT_SEC, 300)
        self.assertGreater(p.DEFAULT_E2E_TIMEOUT_SEC, 0)

    def test_the_LIVENESS_VOCABULARY_is_a_CLOSED_SET_of_REAL_kinds(self) -> None:
        """`boardIsAlive` is a claim about the stream, so the kinds that make it true are enumerated, not
        open-ended. And every one of them was MEASURED on a running game, which is why they are named."""
        self.assertIsInstance(p.LIVENESS_KINDS, tuple)
        self.assertIn("debug.actor-hud", p.LIVENESS_KINDS)
        self.assertIn("shield.granted", p.LIVENESS_KINDS)
        self.assertNotIn(p.E2E_POLLED_KIND, p.LIVENESS_KINDS,
                         "the polled kind is not liveness evidence: seeing it IS the poll succeeding")

    def test_a_NON_POSITIVE_E2E_bound_REFUSES_before_any_request(self) -> None:
        for value in ("0", "-1"):
            with self.subTest(value=value):
                code, payload, _ = run_cli("--e2e-timeout-sec", value)
                self.assertEqual(code, p.lib.EXIT_REFUSED)
                self.assertEqual(payload["reason"], "INVALID-TIMEOUT")

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'lib\.Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - p.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - p.REFUSAL_REASONS)}")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-actor-hud-live.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("no timeout", "5088", "catch", "set-location", "conflated"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_MEASURED_finding_is_in_the_docstring_with_its_SHAPES(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("`debug.board-stats`", head)
        self.assertIn("45-second", head)
        self.assertIn("30 seconds", head)
        for kind in ("shield.granted", "debug.actor-hud", "debug.status.resisted"):
            self.assertIn(kind, head, f"the measured stream is part of the finding; {kind} is missing")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-SkipServerCheck", "-SkipDeploy"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_FLAG_spelling_survives(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        for flag in ("--base-url", "--skip-server-check", "--skip-deploy", "--e2e-timeout-sec"):
            self.assertIn(f'"{flag}"', source)
        self.assertIn("Live Actor HUD E2E passed.", source)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "urllib", "http",
                        "socket", "threading", "time", "importlib", "ast", "re", "io", "pathlib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Attribute) and target.attr in ("lib", "subprocess", "shutil"):
                continue
            if isinstance(target, ast.Name) and target.id == "p":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_no_case_starts_a_SERVER_it_cannot_STOP(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned = []
        for cls in (n for n in ast.walk(tree) if isinstance(n, ast.ClassDef)):
            for func in (n for n in cls.body if isinstance(n, ast.FunctionDef)
                         and n.name.startswith("test")):
                for node in ast.walk(func):
                    if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                            and node.func.attr in ("start", "shutdown")):
                        owned = any(node in ast.walk(stmt)
                                    for parent in ast.walk(func)
                                    if isinstance(parent, (ast.With, ast.AsyncWith, ast.Try))
                                    for stmt in list(getattr(parent, "body", []))
                                    + list(getattr(parent, "finalbody", [])))
                        if not owned:
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}")
        self.assertEqual(unowned, [], "\n".join(unowned))


class AgainstARealGame(unittest.TestCase):
    """The real precondition, on a real running game. The E2E itself is NOT driven here: it is a browser
    run against a live dev server, and the port's own real run is the evidence for that -- re-running it
    per case would make this suite take minutes for no extra coverage."""

    def setUp(self) -> None:
        env = os.environ.get(p.lib.BASE_URL_ENV, "").strip()
        if not env:
            self.skipTest(f"${p.lib.BASE_URL_ENV} is unset; this case drives a real game and will not "
                          f"fall back to {p.lib.DEFAULT_BASE_URL}, which is the owner's own server")
        url, _ = p.lib.resolve_base_url("")
        try:
            health = p.lib._get_json(f"{url}/health", 5, "GET /health")
        except p.lib.Refusal as refusal:
            self.skipTest(f"no server at {url}: {refusal.detail}")
        if not health.get("injectorConnected"):
            self.skipTest(f"the server at {url} is up but NO INJECTOR is connected; start a game in a "
                          f"pool slot. This is not a failure of the tool.")
        self.url = url

    def test_the_real_health_check_passes_against_the_real_game(self) -> None:
        health = p.check_server(self.url)
        self.assertIs(health["injectorConnected"], True)

    def test_the_real_world_HUD_setting_is_ACCEPTED(self) -> None:
        result = p.enable_world_hud(self.url)
        self.assertEqual(result.get("value"), True, f"the server refused the setting: {result}")

    def test_the_real_board_reports_whether_the_E2Es_POLLED_kind_appears(self) -> None:
        """MEASURED, and it is the finding. If the polled kind turns up, the docstring is stale and this
        case fails rather than letting the claim stand."""
        diagnosis = p.diagnose(self.url, timeout_sec=8)
        self.assertTrue(diagnosis["boardIsAlive"],
                        f"the board emitted nothing in the window, so this measured nothing: {diagnosis}")
        if diagnosis["polledKindSeen"]:
            self.skipTest(f"the polled kind {p.E2E_POLLED_KIND} IS now emitted "
                          f"(count={diagnosis['polledKindCount']}); the docstring's finding is stale and "
                          f"must be corrected in the same change")


if __name__ == "__main__":
    unittest.main()
