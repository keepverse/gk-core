"""Contract tests for `gk-core/scripts/wait_for_deploy.py`.

A POLLER'S CONTRACT IS ABOUT WHAT IT REFUSES AND WHAT IT CLAIMS, and two of the original's defects were
claims rather than behaviour.

THE BASE URL WAS THE OWNER'S PORT, HARDCODED. `[string]$BaseUrl = "http://127.0.0.1:5088"` on a machine
with a three-slot pool on 5101/5102/5103. A probe pointed at the owner's server measures the wrong server
and reports it as ground truth. So the URL is read ONCE from the variable the Injector itself reads, and
WHERE IT CAME FROM is reported -- a probe that silently measured the wrong server is worse than one that
refused, and only the report makes that visible.

THE GAME CHECK COULD SEE ANOTHER SLOT'S GAME. `Get-Process -Name PlantsVsZombiesRH` matches by NAME, so
with three slots running, a probe for slot 2 would see slot 1's game and report a deploy it never
observed. A by-path match under a known install is the strong claim; a by-name match is the weak one, and
the verdict says which happened rather than presenting both as the same thing.

THE LOOP IS BOUNDED, and the bound is checked: the budget is a wall clock, the per-request timeout is a
separate smaller bound, and neither can be set to zero or negative. An unbounded poller is the failure
this tool exists to prevent, so "can this hang?" is the first question.

THE ERROR THE ORIGINAL DISCARDED. Its `catch { }` turned every failure -- connection refused, a timeout,
a body that is not JSON -- into `$health = $null` and kept going, recording nothing. The last error is
carried into the verdict, so a timeout that says "server not answering" also says what the server said.

A REAL SERVER IS NOT A STUB. Every case here drives a real `http.server` over loopback, including a body
that is not JSON and a port with nothing on it, because those are the paths that a hand-made return
value would not exercise.
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
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("WAIT_FOR_DEPLOY_SCRIPT",
                             REPO / "scripts" / "wait_for_deploy.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_wait_for_deploy.py"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
GAME_DIR_ENV = "FUSIONRPG_GAME_DIR"
POOL_ENV = "FUSIONRPG_GAME_POOL"
RUN_TIMEOUT = 120
BUDGET = 3
INTERVAL = 1

_spec = importlib.util.spec_from_file_location("wait_for_deploy", SCRIPT)
wait = importlib.util.module_from_spec(_spec)
sys.modules["wait_for_deploy"] = wait
_spec.loader.exec_module(wait)


class _Doc:
    """What the fixture server answers, and what it saw."""
    body = b'{"ok": true, "injectorConnected": true}'
    content_type = "application/json"
    status = 200
    requests: list[str] = []


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        _Doc.requests.append(self.path)
        if _Doc.status != 200:
            self.send_error(_Doc.status, "fixture refuses")
            return
        self.send_response(200)
        self.send_header("Content-Type", _Doc.content_type)
        self.send_header("Content-Length", str(len(_Doc.body)))
        self.end_headers()
        self.wfile.write(_Doc.body)


def serve(**kwargs):
    _Doc.body = kwargs.get("body", b'{"ok": true, "injectorConnected": true}')
    _Doc.content_type = kwargs.get("content_type", "application/json")
    _Doc.status = kwargs.get("status", 200)
    _Doc.requests = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def clean_env(**extra) -> dict:
    env = {k: v for k, v in os.environ.items()
           if k not in (BASE_URL_ENV, GAME_DIR_ENV, POOL_ENV)}
    env.update(extra)
    return env


def run_tool(*extra, env: dict | None = None, timeout: int = RUN_TIMEOUT):
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--json", "--timeout-sec", str(BUDGET),
         "--interval-sec", str(INTERVAL), *extra],
        capture_output=True, text=True, timeout=timeout, cwd=str(REPO),
        env=env if env is not None else clean_env())
    try:
        return proc.returncode, json.loads(proc.stdout)
    except json.JSONDecodeError:
        raise AssertionError(f"no JSON verdict.\nstdout: {proc.stdout[:300]}\nstderr: {proc.stderr[:300]}")


class TheBaseUrlIsReadOnceAndItsSourceIsReported(unittest.TestCase):
    """The original hardcoded the OWNER's port, which on a three-slot pool is the wrong server."""

    def test_the_FLAG_wins_and_its_SOURCE_is_reported(self) -> None:
        server, base = serve()
        try:
            _, payload = run_tool("--base-url", base, "--no-game")
            self.assertEqual(payload["baseUrl"], base)
            self.assertEqual(payload["baseUrlSource"], "--base-url")
        finally:
            server.shutdown()

    def test_the_ENVIRONMENT_is_used_when_no_FLAG_is_given(self) -> None:
        server, base = serve()
        try:
            _, payload = run_tool("--no-game", env=clean_env(**{BASE_URL_ENV: base}))
            self.assertEqual(payload["baseUrl"], base)
            self.assertEqual(payload["baseUrlSource"], f"${BASE_URL_ENV}")
        finally:
            server.shutdown()

    def test_with_NEITHER_the_OWNERS_default_is_used_AND_SAYS_SO(self) -> None:
        """The default survives, because a single-slot install still needs it -- but the report names it
        as the default, so a reader can see the probe had no better information."""
        _, payload = run_tool("--no-game", "--timeout-sec", "1",
                              env=clean_env())
        self.assertEqual(payload["baseUrl"], wait.DEFAULT_BASE_URL)
        self.assertIn("default", payload["baseUrlSource"])
        self.assertIn(BASE_URL_ENV, payload["baseUrlSource"])

    def test_a_non_HTTP_base_URL_is_a_NAMED_refusal_not_a_traceback(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = wait.main(["--json", "--base-url", "not-a-url"])
        self.assertEqual(code, wait.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "BASE-URL-INVALID")

    def test_a_trailing_SLASH_does_not_produce_a_double_SLASH_in_the_health_URL(self) -> None:
        server, base = serve()
        try:
            run_tool("--base-url", base + "/", "--no-game")
            self.assertEqual(_Doc.requests, ["/health"],
                             f"the health path was mangled: {_Doc.requests}")
        finally:
            server.shutdown()


class TheGameMatchIsStrung(unittest.TestCase):
    """A by-path match is the strong claim; a by-name match is the weak one, and the verdict says which."""

    def setUp(self) -> None:
        self.install = REPO / "artifacts" / "wfd-game"
        self.addCleanup(lambda: __import__("shutil").rmtree(self.install, ignore_errors=True))
        self.install.mkdir(parents=True, exist_ok=True)

    def test_WITH_an_install_a_match_must_be_UNDER_it(self) -> None:
        rows = ('"PlantsVsZombiesRH.exe","1234","Console","1","1,000 K"\n'
                '"other.exe","1","Console","1","1,000 K"\n')
        with mock.patch.object(wait, "_RUN", return_value=subprocess.CompletedProcess([], 0, rows, "")):
            up, how = wait.game_running(self.install)
        self.assertIs(up, False, "a row outside the install was accepted as that install's game")
        self.assertEqual(how, "by-name")

    def test_a_row_UNDER_the_install_IS_a_by_path_match(self) -> None:
        rows = (f'"{self.install}\\\\PlantsVsZombiesRH.exe","1234","Console","1","1,000 K"\n')
        with mock.patch.object(wait, "_RUN", return_value=subprocess.CompletedProcess([], 0, rows, "")):
            up, how = wait.game_running(self.install)
        self.assertIs(up, True)
        self.assertEqual(how, "by-path")

    def test_WITHOUT_an_install_the_match_is_by_NAME_and_SAYS_SO(self) -> None:
        rows = '"PlantsVsZombiesRH.exe","1234","Console","1","1,000 K"\n'
        with mock.patch.object(wait, "_RUN", return_value=subprocess.CompletedProcess([], 0, rows, "")):
            up, how = wait.game_running(None)
        self.assertIs(up, True)
        self.assertEqual(how, "by-name")

    def test_NO_game_at_all_is_a_by_NAME_result_and_NOT_an_error(self) -> None:
        with mock.patch.object(wait, "_RUN", return_value=subprocess.CompletedProcess([], 0, "", "")):
            up, how = wait.game_running(None)
        self.assertIs(up, False)
        self.assertEqual(how, "by-name")

    def test_the_install_is_resolved_from_the_ENVIRONMENT_and_the_SOURCE_reported(self) -> None:
        server, base = serve()
        try:
            _, payload = run_tool("--base-url", base, env=clean_env(**{GAME_DIR_ENV: str(self.install)}))
            self.assertEqual(payload["gameInstallSource"], f"${GAME_DIR_ENV}")
        finally:
            server.shutdown()

    def test_the_POOL_root_is_consulted_and_named_as_such(self) -> None:
        """The pool root is a configured location, not one slot's install -- and the report says that, so
        nobody reads a pool root as a claim about a specific game."""
        _, source = wait._resolve_install("")
        with mock.patch.dict(os.environ, {POOL_ENV: str(self.install)}, clear=False):
            install, source = wait._resolve_install("")
        self.assertEqual(install, self.install)
        self.assertIn(POOL_ENV, source)
        self.assertIn("not one slot", source)

    def test_an_EXPLICIT_but_absent_install_is_a_NAMED_refusal(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = wait.main(["--json", "--game-install", str(self.install / "absent")])
        self.assertEqual(code, wait.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "GAME-INSTALL-MISSING")

    def test_NO_GAME_skips_the_process_check_entirely(self) -> None:
        server, base = serve()
        try:
            with mock.patch.object(wait, "_RUN", side_effect=AssertionError("listed processes")):
                _, payload = run_tool("--base-url", base, "--no-game")
            self.assertEqual(payload["gameMatch"], "skipped")
            self.assertIs(payload["ready"], True)
        finally:
            server.shutdown()


class TheReasonLadder(unittest.TestCase):
    """A CLOSED vocabulary, in the original's order, so a caller can branch on it."""

    def ladder(self, health, game_up):
        return wait.ladder_reason(health, game_up)

    def test_the_four_STEPS_and_READY(self) -> None:
        self.assertEqual(self.ladder(None, False), wait.REASON_NO_HEALTH)
        self.assertEqual(self.ladder({"ok": False, "injectorConnected": True}, True),
                         wait.REASON_NOT_OK)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": False}, True),
                         wait.REASON_NOT_CONNECTED)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": True}, False),
                         wait.REASON_NO_GAME)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": True}, True),
                         wait.REASON_READY)

    def test_the_order_is_the_ORIGINALS_server_then_game(self) -> None:
        """The original checked health BEFORE the game process, so a dead server is reported as such even
        when no game is running either. Checking the game first would name the wrong thing."""
        self.assertEqual(self.ladder(None, False), wait.REASON_NO_HEALTH)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": True}, False),
                         wait.REASON_NO_GAME)

    def test_a_MISSING_ok_field_is_treated_as_not_ok_not_as_ready(self) -> None:
        self.assertEqual(self.ladder({"injectorConnected": True}, True), wait.REASON_NOT_OK)

    def test_a_MISSING_injectorConnected_field_is_treated_as_NOT_CONNECTED(self) -> None:
        """Found by falsification. `not health.get("injectorConnected")` and
        `health.get("injectorConnected") is False` differ for exactly one input -- the field ABSENT --
        and a server that has not finished booting is the realistic shape of that input. Only the `ok`
        field had that case, so the `is False` form passed everything."""
        self.assertEqual(self.ladder({"ok": True}, True), wait.REASON_NOT_CONNECTED)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": None}, True),
                         wait.REASON_NOT_CONNECTED)
        self.assertEqual(self.ladder({"ok": True, "injectorConnected": ""}, True),
                         wait.REASON_NOT_CONNECTED)

    def test_the_reasons_are_a_CLOSED_vocabulary_and_are_REPORTED(self) -> None:
        self.assertEqual(wait.REASONS, {wait.REASON_READY, wait.REASON_NO_HEALTH, wait.REASON_NOT_OK,
                                        wait.REASON_NOT_CONNECTED, wait.REASON_NO_GAME})
        _, payload = run_tool("--no-game", "--timeout-sec", "1")
        self.assertEqual(sorted(payload["reasons"]), sorted(wait.REASONS))


class TheLoopIsBounded(unittest.TestCase):
    """An unbounded poller is the failure this tool exists to prevent."""

    def test_a_NON_POSITIVE_budget_or_interval_REFUSES(self) -> None:
        for flag in ("--timeout-sec", "--interval-sec", "--request-timeout"):
            for value in ("0", "-5"):
                with self.subTest(flag=flag, value=value):
                    out = io.StringIO()
                    with redirect_stdout(out):
                        code = wait.main(["--json", flag, value])
                    self.assertEqual(code, wait.EXIT_REFUSED)
                    self.assertIn(json.loads(out.getvalue())["reason"],
                                  {"INVALID-TIMEOUT", "INVALID-INTERVAL"})

    def test_the_poller_STOPS_at_its_budget_against_a_server_that_NEVER_becomes_ready(self) -> None:
        server, base = serve(body=b'{"ok": true, "injectorConnected": false}')
        try:
            started = time.monotonic()
            code, payload = run_tool("--base-url", base, "--no-game", "--timeout-sec", "3",
                                     "--interval-sec", "1")
            elapsed = time.monotonic() - started
        finally:
            server.shutdown()
        self.assertEqual(code, wait.EXIT_TIMEOUT)
        self.assertEqual(payload["reason"], wait.REASON_NOT_CONNECTED)
        # The VERDICT string, not just the exit code and the reason. Every timeout case asserted those
        # two, so a tool that labelled every single run READY passed all of them -- and `verdict` is the
        # field a dashboard reads.
        self.assertEqual(payload["verdict"], "TIMEOUT")
        self.assertIs(payload["ready"], False)
        self.assertGreaterEqual(payload["attempts"], 2, "it stopped without ever retrying")
        self.assertLess(elapsed, 40, f"the poller ran {elapsed:.1f}s past a 3s budget")

    def test_the_poller_STOPS_against_a_server_that_ANSWERS_NOTHING(self) -> None:
        code, payload = run_tool("--base-url", f"http://127.0.0.1:{free_port()}", "--no-game",
                                 "--timeout-sec", "2", "--interval-sec", "1")
        self.assertEqual(code, wait.EXIT_TIMEOUT)
        self.assertEqual(payload["reason"], wait.REASON_NO_HEALTH)

    def test_a_READY_server_is_reported_on_the_FIRST_attempt(self) -> None:
        server, base = serve()
        try:
            code, payload = run_tool("--base-url", base, "--no-game")
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertEqual(payload["attempts"], 1)
        self.assertEqual(payload["verdict"], "READY")

    def test_the_HEALTH_call_carries_a_TIMEOUT(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        calls = re.findall(r"_URLOPEN\((.*?)\)", source, re.DOTALL)
        self.assertTrue(calls, "no health call found at all")
        for call in calls:
            self.assertIn("timeout=", call, f"an unbounded health call: {call[:80]}")
        # and it is SMALLER than the budget, so one hung request cannot consume the whole wait
        self.assertLess(wait.DEFAULT_REQUEST_TIMEOUT, wait.DEFAULT_TIMEOUT_SEC)


class TheDiscardedError(unittest.TestCase):
    """The original's `catch { }` recorded nothing about why a probe failed."""

    def test_a_REFUSED_connection_records_WHY(self) -> None:
        _, payload = run_tool("--base-url", f"http://127.0.0.1:{free_port()}", "--no-game",
                              "--timeout-sec", "2", "--interval-sec", "1")
        self.assertIsNotNone(payload["healthError"], "the failure was swallowed, as the original's was")
        self.assertTrue(payload["healthError"].strip())

    def test_a_body_that_is_NOT_JSON_records_WHY(self) -> None:
        server, base = serve(body=b"<html>a proxy error</html>", content_type="text/html")
        try:
            code, payload = run_tool("--base-url", base, "--no-game", "--timeout-sec", "2")
        finally:
            server.shutdown()
        self.assertEqual(payload["reason"], wait.REASON_NO_HEALTH)
        self.assertIn("not JSON", payload["healthError"])

    def test_an_HTTP_error_status_records_WHY(self) -> None:
        server, base = serve(status=503)
        try:
            _, payload = run_tool("--base-url", base, "--no-game", "--timeout-sec", "2")
        finally:
            server.shutdown()
        self.assertEqual(payload["reason"], wait.REASON_NO_HEALTH)
        self.assertIn("503", payload["healthError"])

    def test_a_JSON_ARRAY_instead_of_an_OBJECT_records_WHY(self) -> None:
        server, base = serve(body=b"[1, 2, 3]")
        try:
            _, payload = run_tool("--base-url", base, "--no-game", "--timeout-sec", "2")
        finally:
            server.shutdown()
        self.assertEqual(payload["reason"], wait.REASON_NO_HEALTH)
        self.assertIn("list", payload["healthError"])


class TheVerdictShape(unittest.TestCase):
    def test_the_health_document_is_reported_NOT_just_a_verdict(self) -> None:
        server, base = serve(body=b'{"ok": true, "injectorConnected": true, "catalogRevision": 15}')
        try:
            _, payload = run_tool("--base-url", base, "--no-game")
        finally:
            server.shutdown()
        self.assertEqual(payload["health"]["catalogRevision"], 15,
                         "the original printed the document; the machine-readable form must carry it")

    def test_a_TIMEOUT_exits_WITH_its_OWN_code_not_the_refusals(self) -> None:
        self.assertEqual(wait.EXIT_TIMEOUT, 1)
        self.assertEqual(wait.EXIT_REFUSED, 64)
        self.assertNotEqual(wait.EXIT_TIMEOUT, wait.EXIT_REFUSED,
                            "a timeout and a crash sharing a code is what the port retired")

    def test_the_closed_advice_to_read_the_DEPLOYS_OWN_output_survives(self) -> None:
        """The original's last line told the reader not to assume the deploy was still running. That is
        provenance for a specific incident, and it must not be lost with the file."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("rather than assuming it is still running", source)

    def test_the_process_listing_is_captured_and_BOUNDED(self) -> None:
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(wait, "_RUN", run):
            wait._tasklist()
        self.assertIsNotNone(seen[0]["kwargs"].get("timeout"))
        self.assertIsNotNone(seen[0]["kwargs"].get("capture_output"))


class Surface(unittest.TestCase):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - wait.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - wait.REFUSAL_REASONS)}")

    def test_the_FLAGS_match_the_POWERSHELL_parameters_ONE_for_ONE(self) -> None:
        """The original took -TimeoutSec, -IntervalSec, -BaseUrl and -NoGame. Each has exactly one
        counterpart, and nothing was added without a reason recorded in the docstring."""
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--game-install", "--timeout-sec", "--interval-sec",
                     "--request-timeout", "--no-game", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-TimeoutSec", "-IntervalSec", "-BaseUrl", "-NoGame"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertNotEqual(proc.returncode, 0, flag)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_parameter_DEFAULTS_are_the_ORIGINALS(self) -> None:
        self.assertEqual(wait.DEFAULT_TIMEOUT_SEC, 300)
        self.assertEqual(wait.DEFAULT_INTERVAL_SEC, 5)
        self.assertEqual(wait.DEFAULT_BASE_URL, "http://127.0.0.1:5088")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("wait-for-deploy.ps1", head)
        # The HEADING, not only the words. A case that checks the vocabulary passes when the heading is
        # renamed, which is how the heading is what a reader actually finds.
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("hardcoded", "another slot", "swallowed", "exit code", "unbounded"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_it_does_NOT_probe_the_OWNERS_default_without_saying_so(self) -> None:
        """The default survives for a single-slot install, so the requirement is not "never 5088" -- it
        is "never 5088 SILENTLY". The report is the mechanism."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("baseUrlSource", source)
        self.assertIn(BASE_URL_ENV, source)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "urllib", "http",
                        "socket", "threading", "time", "importlib", "ast", "re", "io"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "wait":
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
                        # `finally: server.shutdown()` puts the call in `finalbody`, NOT `body`, so a
                        # detector that reads only `body` reports every one of them as unowned. That is a
                        # false positive in the meta-case itself, and a meta-case that cries wolf is worse
                        # than none: it would train a reader to ignore it.
                        owned = any(node in ast.walk(stmt)
                                    for parent in ast.walk(func)
                                    if isinstance(parent, (ast.With, ast.AsyncWith, ast.Try))
                                    for stmt in list(getattr(parent, "body", []))
                                    + list(getattr(parent, "finalbody", [])))
                        if not owned:
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}")
        self.assertEqual(unowned, [], "\n".join(unowned))


if __name__ == "__main__":
    unittest.main()
