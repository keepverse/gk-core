"""Contract tests for `gk-core/scripts/prove_live_setup_skip.py`.

THE BUG THIS SUITE WAS BUILT AROUND CAME FROM A REAL RUN, NOT FROM THINKING. The tool declared its OWN
`Refusal` class, raised it from `check_health` and `skip_setup`, and its `main` caught only the LIBRARY's
class -- so INJECTOR-NOT-CONNECTED, the single most common condition this program meets, escaped as a
traceback and exited 1. `RefusalEscapesAreCaught` below is that bug, pinned.

THE ACKNOWLEDGEMENT IS THE SUBSTANCE, and it arrives in more than one shape. The original printed it
through `ConvertTo-Json`, so a real deployment can hand back a JSON STRING where an object is expected.
Refusing that shape would make the tool useless against the real endpoint, so it is PARSED -- and a case
pins that, because "parse the string" and "accept anything" are one edit apart.

THE SENT METHOD AND THE ACKNOWLEDGED METHOD ARE SEPARATE FACTS, and the server defaults an omitted
method to `button` while this tool's own default is `quick`. A case drives a MISMATCH deliberately, so the
reporting is pinned against a case that can fail.

THE LIVE CASES SKIP, with a stated reason, when no injector is reachable. They refuse to fall back to the
owner's default port, because a live case pointed at `:5088` would drive somebody else's board.
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
SCRIPT = Path(os.environ.get("PROVE_LIVE_SETUP_SKIP_SCRIPT",
                             REPO / "scripts" / "prove_live_setup_skip.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_live_setup_skip.py"
RUN_TIMEOUT = 300

ACK = {"method": "quick", "ok": True, "stage": "already-started", "ready": True, "board": True,
       "ui": True, "toggle": True, "waitForBoard": True, "env": False}


def _load():
    spec = importlib.util.spec_from_file_location("prove_live_setup_skip", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["prove_live_setup_skip"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


class Server:
    """A real HTTP server for this port, with a settable /health, /setup/skip answer and post status."""

    health: dict = {"ok": True, "injectorConnected": True, "simEnabled": False}
    post_status = 200
    post_body: object = {"ok": True, "method": "quick", "acknowledgement": dict(ACK)}
    posts: list[bytes] = []
    requests: list[str] = []


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        Server.requests.append(f"GET {self.path}")
        if self.path.startswith("/health"):
            return self._json(200, Server.health)
        self.send_error(404, "no fixture route")

    def do_POST(self):
        Server.requests.append(f"POST {self.path}")
        Server.posts.append(self.rfile.read(int(self.headers.get("Content-Length", "0") or 0)))
        self._json(Server.post_status, Server.post_body)

    def _json(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def serve(**kwargs):
    Server.health = kwargs.get("health", {"ok": True, "injectorConnected": True, "simEnabled": False})
    Server.post_status = kwargs.get("post_status", 200)
    Server.post_body = kwargs.get("post_body", {"ok": True, "method": "quick",
                                                "acknowledgement": dict(ACK)})
    Server.posts = []
    Server.requests = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def closed_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def run_cli(*args: str) -> tuple[int, dict, str]:
    """Run main() and parse its stdout as JSON. stdout and stderr are kept separate, because the tool
    writes its progress line to stderr and a joined stream is not JSON."""
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = p.main(["--json", *args])
    try:
        payload = json.loads(out.getvalue())
    except json.JSONDecodeError:
        payload = {"__unparseable__": out.getvalue()[:200]}
    return code, payload, err.getvalue()


class RefusalEscapesAreCaught(unittest.TestCase):
    """The bug a REAL run found: `main` caught `lib.Refusal` but not this tool's own `Refusal`, so the
    most common condition on this program escaped as a traceback. A refusal a caller cannot catch is not
    a refusal; it is a crash wearing the reason as a costume."""

    def test_every_REFUSAL_path_returns_the_ENVELOPE_and_not_a_traceback(self) -> None:
        cases = {
            "no server": (["--base-url", f"http://127.0.0.1:{closed_port()}"], "SERVER-UNREACHABLE"),
            "health not ok": None,   # filled in below, needs a server
            "no injector": None,
            "bad budget": (["--timeout-sec", "0"], "INVALID-TIMEOUT"),
            "bad url": (["--base-url", "not-a-url"], "BASE-URL-INVALID"),
        }
        code, payload, err = run_cli(*cases["no server"][0])
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], cases["no server"][1])
        self.assertNotIn("Traceback", err, "a refusal escaped as a traceback")

        code, payload, err = run_cli(*cases["bad budget"][0])
        self.assertEqual(payload["reason"], cases["bad budget"][1])
        code, payload, err = run_cli(*cases["bad url"][0])
        self.assertEqual(payload["reason"], cases["bad url"][1])

    def test_a_health_document_that_is_NOT_ok_is_its_OWN_refusal(self) -> None:
        server, base = serve(health={"ok": False, "injectorConnected": True})
        try:
            code, payload, err = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "HEALTH-NOT-OK")
        self.assertNotIn("Traceback", err)

    def test_no_INJECTOR_names_the_SKILL_and_the_SERVER_it_probed(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": False})
        try:
            code, payload, err = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(payload["reason"], "INJECTOR-NOT-CONNECTED")
        self.assertIn("live-lawn-quick-start", payload["detail"])
        self.assertIn(base, payload["detail"])

    def test_the_handler_catches_BOTH_Refusal_classes(self) -> None:
        """Stated as source, because the failure is an OMISSION and an omission compiles fine. The
        `except` clause must name this tool's class as well as the library's, or every refusal this file
        raises is a crash."""
        tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
        handlers = []
        for node in ast.walk(tree):
            if isinstance(node, ast.ExceptHandler) and node.type is not None:
                # `ast.unparse`, NOT `.attr`: for `lib.Refusal` the attribute is `Refusal`, which is also
                # the bare class's name, so reading `.attr` collapses the two into one string and the
                # check can never pass -- a case that reports a correct implementation as broken.
                names = {ast.unparse(part) for part in
                         (node.type.elts if isinstance(node.type, ast.Tuple) else [node.type])}
                handlers.append(names)
        catches_both = any({"Refusal", "lib.Refusal"} <= names for names in handlers)
        self.assertTrue(catches_both,
                        f"no handler catches both `Refusal` and `lib.Refusal`; found {handlers}")

    def test_ACKNOWLEDGE_failures_are_caught_TOO(self) -> None:
        """`acknowledge` sits OUTSIDE the main try, so it needs its own -- the same trap one scope out."""
        server, base = serve(post_body={"ok": True, "method": "quick"})  # no acknowledgement
        try:
            code, payload, err = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SETUP-SKIP-NOT-ACKNOWLEDGED")
        self.assertNotIn("Traceback", err)


class TheAcknowledgement(unittest.TestCase):
    """The game's side of the exchange, in every shape it can arrive."""

    def test_an_OBJECT_acknowledgement_is_returned_unchanged(self) -> None:
        server, base = serve()
        try:
            ack = p.acknowledge({"ok": True, "acknowledgement": dict(ACK)})
        finally:
            server.shutdown()
        self.assertEqual(ack["method"], "quick")
        self.assertEqual(ack["stage"], "already-started")

    def test_a_JSON_STRING_acknowledgement_is_PARSED_not_refused(self) -> None:
        """The original printed the acknowledgement through `ConvertTo-Json`, so a real deployment can
        hand back text where an object is expected. Refusing that would make the tool useless against the
        real endpoint -- and 'parse the string' and 'accept anything' are one edit apart, so both are
        pinned."""
        server, base = serve(post_body={"ok": True, "acknowledgement": json.dumps(ACK)})
        try:
            ack = p.acknowledge({"ok": True, "acknowledgement": json.dumps(ACK)})
        finally:
            server.shutdown()
        self.assertEqual(ack["method"], "quick")

    def test_a_STRING_that_is_NOT_JSON_is_a_NAMED_refusal(self) -> None:
        server, base = serve()
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.acknowledge({"ok": True, "acknowledgement": "not json at all"})
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "UNREADABLE-ACKNOWLEDGEMENT")

    def test_an_ABSENT_acknowledgement_is_a_NAMED_refusal_that_names_the_keys(self) -> None:
        """A 200 whose body carries no acknowledgement is a DIFFERENT failure from a skip that did not
        ack, and the keys are what tell a reader which happened."""
        server, base = serve(post_body={"ok": True, "method": "quick", "extra": 1})
        try:
            with self.assertRaises(p.Refusal) as caught:
                p.acknowledge({"ok": True, "method": "quick", "extra": 1})
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "SETUP-SKIP-NOT-ACKNOWLEDGED")
        self.assertIn("extra", caught.exception.detail, "the refusal must name what WAS in the body")

    def test_a_NON_OBJECT_answer_is_a_NAMED_refusal_naming_its_TYPE(self) -> None:
        for body in ("a string", 42, ["a", "list"], None):
            with self.subTest(body=body):
                server, base = serve()
                try:
                    with self.assertRaises(p.Refusal) as caught:
                        p.acknowledge(body)
                finally:
                    server.shutdown()
                self.assertEqual(caught.exception.reason, "UNREADABLE-ACKNOWLEDGEMENT")
                self.assertIn(type(body).__name__, caught.exception.detail)


class TheSentAndTheAcknowledgedAreSeparate(unittest.TestCase):
    """The server defaults an OMITTED method to `button`; this tool's own default is `quick`. A caller
    reading only the sent value cannot tell which one the game acted on."""

    def test_a_MATCH_is_reported_as_OK_with_both_VALUES(self) -> None:
        server, base = serve(post_body={"ok": True, "acknowledgement": dict(ACK, method="quick")})
        try:
            code, payload, err = run_cli("--base-url", base, "--method", "quick")
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["methodSent"], "quick")
        self.assertEqual(payload["methodAcknowledged"], "quick")

    def test_a_MISMATCH_is_reported_BY_NAME_and_still_succeeds(self) -> None:
        """The skip DID ack, so it is not a refusal -- but the game acted on a different method, and that
        is the fact this tool exists to surface."""
        server, base = serve(post_body={"ok": True, "acknowledgement": dict(ACK, method="button")})
        try:
            code, payload, err = run_cli("--base-url", base, "--method", "quick")
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "ACKNOWLEDGED-DIFFERENT-METHOD")
        self.assertEqual(payload["methodSent"], "quick")
        self.assertEqual(payload["methodAcknowledged"], "button")
        self.assertIn("acknowledged method='button'", err, "the mismatch must be visible without --json")

    def test_the_SERVER_default_is_REPORTED_so_the_asymmetry_is_legible(self) -> None:
        server, base = serve()
        try:
            _, payload, _ = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(payload["serverDefaultMethod"], p.SERVER_DEFAULT_METHOD)
        self.assertNotEqual(p.DEFAULT_METHOD, p.SERVER_DEFAULT_METHOD,
                            "this tool and the server default DIFFERENTLY; that is the whole reason the "
                            "field exists, so if they ever agree the case should be revisited")

    def test_the_two_defaults_are_not_swapped_by_a_refactor(self) -> None:
        self.assertEqual(p.DEFAULT_METHOD, "quick", "the original's default")
        self.assertEqual(p.SERVER_DEFAULT_METHOD, "button", "measured against the real endpoint")


class TheBudget(unittest.TestCase):
    """The measured finding: the original's POST timeout is `$TimeoutSec + 5`, so -5 became exactly 0 --
    which .NET defines as INFINITE -- while -6 was caught by PowerShell's own validation. The dangerous
    value sat one step INSIDE the check."""

    def test_a_NON_POSITIVE_budget_REFUSES_before_any_request(self) -> None:
        for value in ("0", "-1", "-5", "-6"):
            with self.subTest(value=value):
                code, payload, err = run_cli("--timeout-sec", value)
                self.assertEqual(code, p.lib.EXIT_REFUSED)
                self.assertEqual(payload["reason"], "INVALID-TIMEOUT")

    def test_the_refusal_states_the_MEASURED_arithmetic(self) -> None:
        _, payload, _ = run_cli("--timeout-sec", "-5")
        detail = payload["detail"]
        self.assertIn("+ 5", detail, "the shift is the mechanism and must be stated")
        self.assertIn("INFINITE", detail)

    def test_a_NEGATIVE_budget_NEVER_reaches_the_server(self) -> None:
        """The original sent it, and the server answered 409 about a budget that cannot elapse."""
        server, base = serve()
        try:
            run_cli("--base-url", base, "--timeout-sec", "-5")
        finally:
            server.shutdown()
        self.assertEqual(Server.posts, [], f"a refused budget still reached the server: {Server.posts}")

    def test_the_POST_budget_is_the_argument_PLUS_the_slack(self) -> None:
        self.assertEqual(p.POST_TIMEOUT_SLACK_SEC, 5, "the original's arithmetic, kept")
        server, base = serve()
        try:
            run_cli("--base-url", base, "--timeout-sec", "15")
        finally:
            server.shutdown()
        self.assertEqual(len(Server.posts), 1)

    def test_a_positive_budget_is_SENT_as_the_TIMEOUT_SEC_the_SERVER_exppects(self) -> None:
        server, base = serve()
        try:
            run_cli("--base-url", base, "--timeout-sec", "7")
        finally:
            server.shutdown()
        self.assertEqual(json.loads(Server.posts[0].decode())["timeoutSec"], 7)


class TheServerRefusal(unittest.TestCase):
    def test_an_ok_FALSE_answer_is_a_NAMED_refusal_carrying_the_SERVERS_error(self) -> None:
        server, base = serve(post_status=409,
                             post_body={"ok": False, "method": "quick",
                                        "error": "debug.setup.skip did not ack within 0s"})
        try:
            code, payload, err = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SETUP-SKIP-FAILED")
        self.assertIn("did not ack within", payload["detail"],
                      "the server's own words are the most useful part of the message")

    def test_the_endpoint_is_NAMED_in_the_refusal(self) -> None:
        server, base = serve(post_status=409, post_body={"ok": False, "error": "no"})
        try:
            _, payload, _ = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertIn("/setup/skip", payload["detail"])

    def test_the_request_GOES_to_setup_skip(self) -> None:
        server, base = serve()
        try:
            run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertIn("POST /api/debug/setup/skip", Server.requests)

    def test_the_body_carries_the_METHOD_and_the_BUDGET(self) -> None:
        server, base = serve()
        try:
            run_cli("--base-url", base, "--method", "button", "--timeout-sec", "12")
        finally:
            server.shutdown()
        body = json.loads(Server.posts[0].decode())
        self.assertEqual(body, {"method": "button", "timeoutSec": 12})


class TheUnreachedBranches(unittest.TestCase):
    """Six mutants survived, and they name TWO root causes, both suite gaps rather than tool defects.

    **CAUSE 1: THE `ok: false` PATH WAS NEVER DRIVEN, because the fixture used a 409.**
    `lib.invoke_debug_post` RAISES on a non-2xx status before the body is ever returned, so a fixture
    answering `409 {"ok": false, "error": ...}` exercises `skip_setup`'s HTTPError branch and NEVER
    reaches the tool's own `if not result.get("ok")`. Three surviving mutants live in that unreachable
    branch: the top-level `ok` check, the server's own error text, and the endpoint named in the refusal.
    So a real guard was entirely unproven -- and the measured server does answer 409 for a bad budget, so
    `ok: false` at HTTP 200 is a shape nothing has yet produced. It is a branch the endpoint's own
    contract allows, and an unproven branch is not a safe branch.

    **CAUSE 2: NO CASE CAPTURED THE TIMEOUT PASSED TO THE TRANSPORT.** The suite pins the CONSTANT
    `POST_TIMEOUT_SLACK_SEC == 5` and that the BODY carries the right `timeoutSec`, but neither observes the
    timeout handed to `invoke_debug_post` -- so removing `+ POST_TIMEOUT_SLACK_SEC` from the call was
    invisible. Pinning a constant and pinning its USE are different claims, and only the second is the
    defect the mutant represents.
    """

    def test_a_200_with_ok_FALSE_is_a_NAMED_refusal_carrying_the_SERVERS_error(self) -> None:
        """HTTP 200 with `ok: false`: a different shape from a 409, and the one the original's
        `if (-not $result.ok) { throw "setup skip failed: $($result.error)" }` handled. The server's own
        `error` is the most useful part of the message, so it must survive."""
        server, base = serve(post_status=200,
                             post_body={"ok": False, "method": "quick",
                                        "error": "debug.setup.skip did not ack within 3s",
                                        "acknowledgement": dict(ACK)})
        try:
            code, payload, err = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SETUP-SKIP-FAILED",
                         "a 200 whose body says ok=false must NOT be reported as a success")
        self.assertIn("did not ack within 3s", payload["detail"],
                      "the server's own error text is the most useful part of the refusal")
        self.assertIn("/setup/skip", payload["detail"], "the refusal must name the endpoint")
        self.assertIn("ok=False", payload["detail"], "...and say what the server actually answered")

    def test_a_200_with_ok_FALSE_does_NOT_report_the_ACKNOWLEDGEMENT(self) -> None:
        """The body carries a full, well-formed acknowledgement AND `ok: false`. Reporting the
        acknowledgement anyway would be the worst outcome available here: a caller would see a real
        pointer from a skip the server said had failed."""
        server, base = serve(post_status=200, post_body={"ok": False, "error": "nope",
                                                          "acknowledgement": dict(ACK)})
        try:
            _, payload, _ = run_cli("--base-url", base)
        finally:
            server.shutdown()
        self.assertNotIn("acknowledgement", payload)
        self.assertNotIn("methodAcknowledged", payload)

    def test_the_TIMEOUT_handed_to_the_TRANSPORT_is_the_budget_PLUS_the_slack(self) -> None:
        """Pinning the constant is not pinning its USE, and only the second is the defect the mutant
        represents: dropping `+ POST_TIMEOUT_SLACK_SEC` left every other case green."""
        seen = {}
        real = p.lib.invoke_debug_post

        def capture(base_url, path, body=None, timeout=15):
            seen["timeout"] = timeout
            return real(base_url, path, body, timeout)

        server, base = serve()
        try:
            with mock.patch.object(p.lib, "invoke_debug_post", capture):
                run_cli("--base-url", base, "--timeout-sec", "11")
        finally:
            server.shutdown()
        self.assertEqual(seen.get("timeout"), 11 + p.POST_TIMEOUT_SLACK_SEC,
                         f"the transport was given {seen.get('timeout')!r}, which is not the original's "
                         f"budget-plus-slack arithmetic")

    def test_the_DOCSTRING_carries_the_MEASURED_ARITHMETIC_not_a_claim(self) -> None:
        """The finding is a TABLE of three measured values PLUS the conclusion they support, and a docstring
        that keeps the word 'infinite' while losing either still reads as if it were documented. Both halves
        are pinned because both are the evidence: the table is the measurement and the sentence is the
        finding. A survivor here meant the conclusion sentence was pinned by nothing at all."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        for fragment in ("$TimeoutSec + 5", "-TimeoutSec  0  ->  POST timeout  5s",
                         "-TimeoutSec -5  ->  POST timeout  0s", "-TimeoutSec -6",
                         "ConnectionTimeoutSeconds",
                         "So the infinite read is at **-5**, not at 0"):
            self.assertIn(fragment, head,
                          f"the docstring omits {fragment!r}; the measured table IS the finding, and a "
                          f"prose summary of it can drift from what was actually observed")


class Surface(unittest.TestCase):
    def test_it_IMPORTS_the_shared_library_rather_than_reimplementing_the_transport(self) -> None:
        """The library owns the HTTP, the refusal vocabulary and the base-URL resolution. A second
        implementation here would be the two-implementations defect."""
        code = code_without_docstrings(SCRIPT)
        self.assertIn("import live_lawn_setup as lib", code)
        for name in ("resolve_base_url", "invoke_debug_post", "EXIT_REFUSED", "BASE_URL_ENV"):
            self.assertIn(f"lib.{name}", code, f"{name} is used without the shared library")
        self.assertNotIn("Invoke-RestMethod", code)

    def test_the_PATH_insert_runs_BEFORE_the_import(self) -> None:
        lines = [ln.strip() for ln in SCRIPT.read_text(encoding="utf-8").splitlines()]
        insert = next(i for i, ln in enumerate(lines) if ln.startswith("sys.path.insert"))
        imported = next(i for i, ln in enumerate(lines) if ln.startswith("import live_lawn_setup"))
        self.assertLess(insert, imported)

    def test_the_HUMAN_STEP_SURVIVES(self) -> None:
        """The last step is a person confirming the panel advanced. An automated tool cannot observe it,
        and dropping the line would leave a probe continuing against a board that never started."""
        self.assertIn("plant-selection panel advanced", p._HUMAN_STEP)
        server, base = serve()
        try:
            out = io.StringIO()
            with redirect_stdout(out):
                p.main(["--base-url", base])
        finally:
            server.shutdown()
        self.assertIn("plant-selection panel advanced", out.getvalue())

    def test_the_success_LINE_spelling_survives(self) -> None:
        """The original printed one specific line, which a runbook or a caller may match on."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("LIVE setup skip succeeded: method=", source)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Method", "-TimeoutSec"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.METHODS, ("quick", "button"))
        self.assertEqual(p.DEFAULT_METHOD, "quick")
        self.assertEqual(p.DEFAULT_TIMEOUT_SEC, 15)
        self.assertEqual(p.HEALTH_TIMEOUT, 5)
        self.assertEqual(p.POST_TIMEOUT_SLACK_SEC, 5)

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - p.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - p.REFUSAL_REASONS)}")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-live-setup-skip.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("infinite", "hardcoded", "validateset", "stack trace", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

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
            if isinstance(target, ast.Attribute) and target.attr == "lib":
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


def code_without_docstrings(path: Path) -> str:
    tree = ast.parse(path.read_text(encoding="utf-8"))
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            if (node.body and isinstance(node.body[0], ast.Expr)
                    and isinstance(node.body[0].value, ast.Constant)
                    and isinstance(node.body[0].value.value, str)):
                node.body.pop(0)
    return ast.unparse(tree)


class AgainstARealGame(unittest.TestCase):
    """The real path, on a real running game, when one is reachable. The refusal-path proof is what this
    tool was committed with, and it is not the same evidence as a real acknowledgement."""

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

    def test_both_METHODS_produce_a_REAL_acknowledgement(self) -> None:
        seen = {}
        for method in p.METHODS:
            code, payload, _ = run_cli("--base-url", self.url, "--method", method)
            self.assertEqual(code, 0, f"{method} did not succeed: {payload}")
            self.assertEqual(payload["verdict"], "OK")
            seen[method] = payload["methodAcknowledged"]
        self.assertEqual(seen["quick"], "quick", "the game acknowledged a different method than was sent")
        self.assertEqual(seen["button"], "button")
        self.assertNotEqual(seen["quick"], seen["button"],
                            "the two methods acknowledged identically, so neither is distinguishable here")

    def test_a_REAL_acknowledgement_carries_the_game_side_FIELDS(self) -> None:
        _, payload, _ = run_cli("--base-url", self.url)
        ack = payload["acknowledgement"]
        for field in ("method", "ok", "stage"):
            self.assertIn(field, ack, f"the real acknowledgement has no {field!r}: {ack}")


if __name__ == "__main__":
    unittest.main()
