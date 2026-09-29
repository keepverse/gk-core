"""Contract tests for `gk-core/scripts/prove_status_l2_one.py`.

THIS SCRIPT'S PROOFABLE SUBSTANCE IS THE EVENT SUMMARISING, and it is PURE. Reaching the rest of the flow
needs a running game with the injector connected, which no slot on this machine has -- and a stub that
fakes a connected injector would be proving a fiction. So the counting is decided by planted pages here,
and the refusal path is decided against a REAL server, which is what every one of these live scripts
actually does until a game is running.

THE ACCEPTANCE RULE IS "started OR applied", AND `debug.status` ALONE IS DELIBERATELY NOT ENOUGH -- a
status event with no apply behind it is the signature of the bug this scenario exists to catch. So the
three planted pages are: mixed (both), fx-only (started, no applied), and neither. A case that only drove
the mixed page could not tell a correct implementation from one that accepted any page.

THE SCENARIO NAME IS A CLOSED SHAPE, and the reason is a URL. The original interpolated `$Scenario` into
`/scenario/$Scenario` with no validation, so `../../admin` became a different REQUEST than intended and the
counts that came back would have belonged to that other request. The recorded differential shows exactly
which paths the original would have requested, for five hostile names.

THE DEADLINE IS CHECKED BEFORE THE SLEEP, not only after a poll, and the sleep is clamped to what is left.
The original's `do/while` evaluated the deadline only after an unbounded poll, so a slow poll could
overrun the budget arbitrarily -- and with no per-read timeout, a hung poll meant the budget was not a
bound at all.
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
SCRIPT = Path(os.environ.get("PROVE_STATUS_L2_ONE_SCRIPT",
                             REPO / "scripts" / "prove_status_l2_one.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_status_l2_one.py"
RUN_TIMEOUT = 300

MIXED = [{"id": 30, "kind": "debug.run-steps.done"},
         {"id": 29, "kind": "debug.status.apply"},
         {"id": 28, "kind": "debug.fx.state.started"},
         {"id": 27, "kind": "debug.status"},
         {"id": 26, "kind": "debug.status"},
         {"id": 25, "kind": "chatter.event"},
         {"id": 24, "kind": "debug.fx.state.started"},
         {"id": 23, "kind": "debug.status.apply"}]
FX_ONLY = [{"id": 30, "kind": "debug.run-steps.done"},
           {"id": 29, "kind": "debug.fx.state.started"},
           {"id": 28, "kind": "debug.status"}]
NEITHER = [{"id": 30, "kind": "debug.run-steps.done"},
           {"id": 29, "kind": "debug.status"},
           {"id": 28, "kind": "chatter.event"}]


def _load():
    spec = importlib.util.spec_from_file_location("prove_status_l2_one", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["prove_status_l2_one"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


class TheSummarising(unittest.TestCase):
    """Pure, so planted pages decide it. This is the substance the scenario exists to measure."""

    def test_the_MIXED_page_counts_all_three_kinds(self) -> None:
        summary = p.summarise_events(MIXED)
        self.assertEqual(summary["started"], 2)
        self.assertEqual(summary["applied"], 2)
        self.assertEqual(summary["status"], 2)
        self.assertIs(summary["hasEvidence"], True)

    def test_UNCOUNTED_kinds_are_ignored(self) -> None:
        summary = p.summarise_events([{"id": 1, "kind": "chatter.event"},
                                      {"id": 2, "kind": "debug.run-steps.done"}])
        self.assertEqual((summary["started"], summary["applied"], summary["status"]), (0, 0, 0))

    def test_APPLY_alone_is_ENOUGH(self) -> None:
        summary = p.summarise_events([{"kind": "debug.status.apply"}])
        self.assertIs(summary["hasEvidence"], True)
        self.assertEqual(summary["evidenceKind"], "debug.status.apply")

    def test_STARTED_alone_is_ENOUGH(self) -> None:
        summary = p.summarise_events([{"kind": "debug.fx.state.started"}])
        self.assertIs(summary["hasEvidence"], True)
        self.assertEqual(summary["evidenceKind"], "debug.fx.state.started")

    def test_STATUS_alone_is_NOT_enough_and_that_is_the_POINT(self) -> None:
        """`debug.status` with no apply behind it is the signature of the bug this scenario exists to
        catch, so a page of them must NOT pass. A case that only drove the mixed page could not tell a
        correct implementation from one that accepted anything."""
        summary = p.summarise_events(NEITHER)
        self.assertEqual(summary["status"], 1)
        self.assertEqual(summary["started"], 0)
        self.assertEqual(summary["applied"], 0)
        self.assertIs(summary["hasEvidence"], False)
        self.assertEqual(summary["evidenceKind"], "")

    def test_FX_only_and_NEITHER_are_NOT_collapsed_into_one_verdict(self) -> None:
        """The two must NOT read alike: fx-only PASSES and neither FAILS. An earlier version of this case
        asserted they were EQUAL, which contradicted its own name and would have pinned the collapse in."""
        fx = p.summarise_events(FX_ONLY)
        neither = p.summarise_events(NEITHER)
        self.assertIs(fx["hasEvidence"], True)
        self.assertIs(neither["hasEvidence"], False)
        self.assertNotEqual(fx["started"], neither["started"])
        self.assertNotEqual(fx["evidenceKind"], neither["evidenceKind"])

    def test_the_three_pages_give_three_DISTINCT_summaries(self) -> None:
        shapes = {json.dumps(p.summarise_events(page), sort_keys=True)
                  for page in (MIXED, FX_ONLY, NEITHER)}
        self.assertEqual(len(shapes), 3, f"the pages collapsed: {shapes}")

    def test_a_page_that_is_NOT_a_list_of_objects_reads_as_ZERO(self) -> None:
        """The events come from a SERVER. A page that is not a list, or a list holding something that is
        not an object, must count as zero rather than raise -- a report that crashes on an unexpected
        page is a tool that cannot be pointed at a server it has not met."""
        for page in ([], [{"id": 1}], [{}], [{"kind": None}], "not a list", None, 42,
                     ["a string", 7]):
            with self.subTest(page=page):
                self.assertIs(p.summarise_events(page)["hasEvidence"], False)

    def test_a_kind_that_is_NOT_a_string_is_not_counted_as_anything(self) -> None:
        summary = p.summarise_events([{"kind": ["debug.status.apply"]}, {"kind": 1}, {"kind": None}])
        self.assertEqual((summary["started"], summary["applied"], summary["status"]), (0, 0, 0))

    def test_the_kind_of_EVIDENCE_is_reported_separately_so_a_caller_can_see_WHICH(self) -> None:
        """A run with apply evidence and a run with fx-only evidence both pass, and a caller diffing two
        runs needs to know which produced each -- otherwise the diff reads as noise."""
        both = p.summarise_events([{"kind": "debug.fx.state.started"}, {"kind": "debug.status.apply"}])
        self.assertEqual(both["evidenceKind"], "debug.status.apply",
                         "apply must win when both are present, so the field names the STRONGER evidence")

    def test_FX_only_SEPARATES_the_three_counts_where_MIXED_cannot(self) -> None:
        """The MIXED page has started == applied == 2, so an implementation reporting the started number for
        `applied` is INDISTINGUISHABLE on it -- a fixture that cannot fail. The fx-only page (started 1,
        applied 0) is what separates them, so the two counts are pinned there. This case was added after
        falsification reported the mixed-counts mutant SURVIVING, and it is why that mutant now dies."""
        summary = p.summarise_events(FX_ONLY)
        self.assertEqual(summary["started"], 1)
        self.assertEqual(summary["applied"], 0, "applied must be 0 here, or the counts are interchangeable")
        self.assertEqual(summary["status"], 1)


class TheScenarioNameIsAClosedShape(unittest.TestCase):
    """The name is interpolated into a URL PATH. The original did it unchecked, so `../../admin` asked the
    server for a different request than intended and the counts belonged to that other request."""

    def test_ORDINARY_names_are_accepted(self) -> None:
        for name in ("status-l2-wither", "status-l2-rot", "a", "A.b_c-1", "x" * 64):
            with self.subTest(name=name):
                self.assertIsNotNone(p.SCENARIO_PATTERN.match(name), name)

    def test_HOSTILE_names_are_REFUSED_by_name(self) -> None:
        for name in ("../../admin", "a/b", "a?x=1", "a b", "", ".hidden", "a#frag", "x" * 65, "a\\b"):
            with self.subTest(name=name):
                self.assertIsNone(p.SCENARIO_PATTERN.match(name),
                                  f"{name!r} would be interpolated into the URL path")

    def test_a_HOSTILE_name_REFUSES_before_any_request(self) -> None:
        for name in ("../../admin", "a/b", "a?x=1", ""):
            with self.subTest(name=name):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = p.main(["--json", "--scenario", name])
                self.assertEqual(code, p.lib.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-SCENARIO-NAME")

    def test_the_refusal_says_where_the_name_lands(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            p.main(["--json", "--scenario", "a/b"])
        detail = json.loads(out.getvalue())["detail"]
        self.assertIn("/api/debug/scenario/", detail,
                      "the refusal must name where the name would have gone")

    def test_the_name_REFUSAL_names_the_PATTERN_a_person_can_TYPE(self) -> None:
        """The head of the message names the path even after the tail is lost, so an assertion on the path
        cannot see that change -- and falsification found exactly that survivor. The ACCEPTED PATTERN is the
        part a person acts on, so that is what is pinned."""
        out = io.StringIO()
        with redirect_stdout(out):
            p.main(["--json", "--scenario", "a/b"])
        detail = json.loads(out.getvalue())["detail"]
        self.assertIn("[A-Za-z0-9][A-Za-z0-9._-]", detail,
                      "the refusal must state the pattern a valid name matches, not merely that this is not one")
        self.assertIn("0,63", detail, "...including its length bound")

    def test_an_ORDINARY_name_is_NOT_refused_by_the_pattern_alone(self) -> None:
        """A guard that also refuses valid names is not a guard, it is an outage. The default name is the
        one a runbook tells a person to type, so it is checked explicitly."""
        self.assertIsNotNone(p.SCENARIO_PATTERN.match(p.DEFAULT_SCENARIO))


class TheDeadlineIsBounded(unittest.TestCase):
    """The original checked the deadline only AFTER an unbounded poll, and had no per-read timeout, so a
    hung poll meant the budget was not a bound at all."""

    def setUp(self) -> None:
        self.served: list[dict] = []

    def _events(self, base_url, after_id, limit, timeout):
        self.served.append({"afterId": after_id, "limit": limit, "timeout": timeout})
        return [{"id": 100 + i, "kind": "chatter.event"} for i in range(2)]

    def test_it_stops_at_the_DEADLINE_and_NOT_after_it(self) -> None:
        with mock.patch.object(p.lib, "get_events", self._events):
            started = time.monotonic()
            done, collected, attempts = p.wait_for_completion("http://x", 0, time.monotonic() + 1.0)
            elapsed = time.monotonic() - started
        self.assertIs(done, False)
        self.assertGreaterEqual(attempts, 2, "it stopped without ever retrying")
        self.assertLess(elapsed, 5.0, f"it ran {elapsed:.1f}s past a 1.0s deadline")

    def test_it_returns_as_SOON_as_the_DONE_marker_appears(self) -> None:
        pages = [[{"id": 1, "kind": "chatter.event"}],
                 [{"id": 2, "kind": "debug.run-steps.done"}]]
        seen = {"n": 0}

        def events(base_url, after_id, limit, timeout):
            page = pages[min(seen["n"], len(pages) - 1)]
            seen["n"] += 1
            return page

        with mock.patch.object(p.lib, "get_events", events):
            done, collected, attempts = p.wait_for_completion("http://x", 0, time.monotonic() + 30)
        self.assertIs(done, True)
        self.assertEqual(attempts, 2)
        self.assertTrue(any(e["kind"] == "debug.run-steps.done" for e in collected))

    def test_a_DEADLINE_already_PASSED_refuses_to_poll_at_all(self) -> None:
        """A budget of zero must not buy even one read, and certainly not a full page of them."""
        with mock.patch.object(p.lib, "get_events", self._events):
            done, collected, attempts = p.wait_for_completion("http://x", 0, time.monotonic() - 1.0)
        self.assertIs(done, False)
        self.assertEqual(attempts, 1, "it polled after the deadline had already passed")

    def test_EVERY_poll_carries_a_TIMEOUT(self) -> None:
        with mock.patch.object(p.lib, "get_events", self._events):
            p.wait_for_completion("http://x", 0, time.monotonic() + 0.2)
        self.assertTrue(self.served)
        for call in self.served:
            self.assertIsNotNone(call["timeout"], f"an unbounded poll: {call}")

    def test_a_NON_POSITIVE_budget_REFUSES_before_any_work(self) -> None:
        for value in ("0", "-5"):
            with self.subTest(value=value):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = p.main(["--json", "--timeout-sec", value])
                self.assertEqual(code, p.lib.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_BAD_BUDGET_is_REFUSED_before_the_URL_is_even_resolved(self) -> None:
        """The shared library enforces the same invariant one layer down, so this check looks redundant --
        and is NOT, because the library resolves the base URL FIRST. With a hostile URL the library would
        answer BASE-URL-INVALID and the caller would never learn that its own budget was the problem. The
        ORDER is the only part that differs, so the ORDER is what a case pins. Falsification reported this
        mutant SURVIVING against a case that used a valid URL, which is why this one uses a hostile one."""
        out = io.StringIO()
        with redirect_stdout(out):
            code = p.main(["--json", "--timeout-sec", "0", "--base-url", "not-a-url"])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INVALID-TIMEOUT",
                         "the budget is this tool's own input, so it is checked before anything else")


class AgainstARealServer(unittest.TestCase):
    """The refusal path is what every one of these live scripts actually does until a game is running, so
    it is exercised against a REAL HTTP server rather than a stubbed return value."""

    def test_an_unreachable_server_is_a_named_refusal(self) -> None:
        port = closed_port()
        out = io.StringIO()
        with redirect_stdout(out):
            code = p.main(["--json", "--base-url", f"http://127.0.0.1:{port}"])
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "SERVER-UNREACHABLE")

    def test_a_server_with_NO_injector_is_a_named_refusal_that_names_the_sKILL(self) -> None:
        """The shared library's message, carried through this wrapper unchanged -- it is the accumulated
        knowledge of several live-debug incidents and it is what an operator acts on."""

        server, base = serve_health({"ok": True, "injectorConnected": False, "simEnabled": False})
        try:
            out = io.StringIO()
            with redirect_stdout(out):
                code = p.main(["--json", "--base-url", base])
        finally:
            server.shutdown()
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["reason"], "INJECTOR-NOT-CONNECTED")
        self.assertIn("live-lawn-quick-start", payload["detail"])
        self.assertIn(base, payload["detail"], "the refusal must name the server it probed")

    def test_the_REFUSAL_still_carries_the_tool_s_OWN_context(self) -> None:
        """The shared library's message says nothing about which scenario was being proved, so the wrapper
        adds it. Without this a log line naming the reason is unattributable to a run."""
        port = closed_port()
        out = io.StringIO()
        with redirect_stdout(out):
            p.main(["--json", "--base-url", f"http://127.0.0.1:{port}", "--scenario", "status-l2-rot"])
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["tool"], p.TOOL_ID)
        self.assertEqual(payload["scenario"], "status-l2-rot")
        self.assertIs(payload["skipSetup"], False)

    def test_the_BASE_URL_source_is_reported(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            p.main(["--json", "--base-url", "http://127.0.0.1:1"])
        self.assertEqual(json.loads(out.getvalue())["baseUrlSource"], "explicit")



def serve_health(health: dict):
    """A REAL HTTP server answering /health with `health`, so the refusal path is decided by a real
    response rather than a stubbed return value. The start is inside the returned pair's own lifecycle:
    the caller holds the server object and shuts it down in a `finally`."""
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            return

        def do_GET(self):
            body = json.dumps(health).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def closed_port() -> int:
    """A port nothing is listening on: bound, read, released."""
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]



def code_without_docstrings(path: Path) -> str:
    """The module's CODE, with every docstring removed.

    `ast.unparse` re-emits a docstring as a string literal, and this module's docstring QUOTES the retired
    `Invoke-RestMethod` while describing the very defect. A case that scans prose for a token the prose is
    bound to mention can only ever fail, so the docstrings go before the scan.
    """
    tree = ast.parse(path.read_text(encoding="utf-8"))
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            if (node.body and isinstance(node.body[0], ast.Expr)
                    and isinstance(node.body[0].value, ast.Constant)
                    and isinstance(node.body[0].value.value, str)):
                node.body.pop(0)
    return ast.unparse(tree)


class TheWholeRunEndToEnd(unittest.TestCase):
    """The final re-read, the verdict and the exit code are the port's actual output, and until this
    block existed NO case executed any of them -- `main()` always stopped at the library's injector
    refusal. The library stays real; only its BOARD step is substituted, and the substitution is named, so
    a reader can see exactly which step is unproven. Reaching the real one needs a running game, and a
    stub that FAKES a connected injector would be proving a fiction."""

    def _run(self, page):
        """Drive `main()` to a verdict with a scripted event stream, and return (exit, payload)."""
        calls: list[dict] = []
        pages = {"completion": list(page) + [{"id": 900, "kind": "debug.run-steps.done"}],
                 "final": list(page) + [{"id": 900, "kind": "debug.run-steps.done"}]}

        def get_events(base_url, after_id, limit, timeout):
            calls.append({"afterId": after_id, "limit": limit, "timeout": timeout})
            return pages["final"] if limit == p.FINAL_PAGE else pages["completion"]

        def posts(base_url, path, body=None, timeout=15):
            calls.append({"post": path})
            return {"entered": True, "levelType": "lab", "targetPtr": "0xZ", "plantPtr": "0xP"}

        board = p.lib.LabBoard(target_ptr="0xZ", plant_ptr="0xP", level_type="lab", entered=True)
        out = io.StringIO()
        with mock.patch.object(p.lib, "ensure_live_lab_board", return_value=board), \
                mock.patch.object(p.lib, "get_debug_max_event_id", return_value=500), \
                mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post", posts), \
                redirect_stdout(out), redirect_stderr(io.StringIO()):
            code = p.main(["--json", "--base-url", "http://127.0.0.1:5101"])
        return code, json.loads(out.getvalue()), calls

    def test_a_page_WITH_evidence_exits_ZERO_and_says_OK(self) -> None:
        code, payload, _ = self._run(MIXED)
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["counts"],
                         {"fx.state.started": 2, "debug.status.apply": 2, "debug.status": 2})
        self.assertEqual(payload["evidenceKind"], "debug.status.apply")
        self.assertIs(payload["completed"], True)

    def test_a_page_WITH_NO_evidence_exits_NON_ZERO_and_says_FAILED(self) -> None:
        """The whole point of the tool: a run that produced no apply evidence must NOT report a pass.
        Nothing executed this before, so the verdict and the exit code were entirely unproven."""
        code, payload, _ = self._run(NEITHER)
        self.assertEqual(code, 1, "a run with no organic evidence reported success")
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertEqual(payload["exitCode"], 1)
        self.assertEqual(payload["counts"],
                         {"fx.state.started": 0, "debug.status.apply": 0, "debug.status": 1})

    def test_FX_only_evidence_STILL_PASSES_but_reports_which_kind(self) -> None:
        code, payload, _ = self._run(FX_ONLY)
        self.assertEqual(code, 0)
        self.assertEqual(payload["evidenceKind"], "debug.fx.state.started",
                         "a caller diffing two runs must be able to tell which evidence carried this one")

    def test_the_three_pages_give_three_DISTINCT_exit_codes_where_they_differ(self) -> None:
        codes = {name: self._run(page)[0] for name, page in
                 (("mixed", MIXED), ("fx-only", FX_ONLY), ("neither", NEITHER))}
        self.assertEqual(codes, {"mixed": 0, "fx-only": 0, "neither": 1}, codes)
        self.assertNotEqual(codes["neither"], codes["mixed"],
                            "a run that measured nothing must not read like one that did")

    def test_the_counts_are_read_from_the_FINAL_page_not_the_last_poll(self) -> None:
        """The original re-read with a larger limit for exactly this reason. Reading the completion poll's
        page instead would undercount a run whose early events came before the done marker."""
        long_run = [{"id": 100 + i, "kind": "debug.status.apply"} for i in range(40)]
        code, payload, _ = self._run(long_run)
        self.assertEqual(code, 0)
        self.assertEqual(payload["counts"]["debug.status.apply"], 40)
        self.assertEqual(payload["collectedEvents"] > 0, True)

    def test_the_FINAL_read_asks_for_the_LARGER_page_and_carries_a_TIMEOUT(self) -> None:
        _, _, calls = self._run(MIXED)
        final = [c for c in calls if c.get("limit") == p.FINAL_PAGE]
        self.assertTrue(final, f"no read at the final page size: {calls}")
        for call in calls:
            self.assertIsNotNone(call.get("timeout", "absent"), f"an unbounded read: {call}")

    def test_the_scenario_is_POSTED_to_the_path_the_name_implies(self) -> None:
        _, _, calls = self._run(MIXED)
        posts = [c["post"] for c in calls if "post" in c]
        self.assertIn("/scenario/status-l2-wither", posts, posts)

    def test_a_run_that_NEVER_completes_is_a_NAMED_refusal_not_a_verdict(self) -> None:
        board = p.lib.LabBoard(target_ptr="0xZ", level_type="lab", entered=True)

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 100, "kind": "chatter.event"}]

        out = io.StringIO()
        with mock.patch.object(p.lib, "ensure_live_lab_board", return_value=board), \
                mock.patch.object(p.lib, "get_debug_max_event_id", return_value=500), \
                mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post", lambda *a, **k: {}), \
                redirect_stdout(out), redirect_stderr(io.StringIO()):
            code = p.main(["--json", "--base-url", "http://127.0.0.1:5101", "--timeout-sec", "1"])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, p.lib.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SCENARIO-NOT-COMPLETED")
        self.assertIn("status-l2-wither", payload["detail"],
                      "a timeout that does not name the scenario is not actionable")
        self.assertIn("1s", payload["detail"], "nor is one that does not name the budget")

class Surface(unittest.TestCase):
    def test_it_IMPORTS_the_shared_library_rather_than_reimplementing_it(self) -> None:
        """The library has seven functions and four callers. A second implementation here would be the
        two-implementations defect, and the `.ps1` still exists precisely so it can be deleted last."""
        code = code_without_docstrings(SCRIPT)
        self.assertIn("import live_lawn_setup as lib", code)
        for name in ("ensure_live_lab_board", "get_debug_max_event_id", "get_events", "invoke_debug_post"):
            self.assertIn(f"lib.{name}", code, f"{name} is used without the shared library")
        self.assertNotIn("Invoke-RestMethod", code,
                         "the retired dialect appears in the CODE, not only in the prose describing it")
        self.assertNotIn("def get_debug_max_event_id", code,
                         "the search is reimplemented here instead of imported")

    def test_the_PATH_insert_runs_BEFORE_the_import(self) -> None:
        """A sys.path insert below the import is a ModuleNotFoundError at startup -- and the import would
        still LOOK right in the source."""
        lines = [ln.strip() for ln in SCRIPT.read_text(encoding="utf-8").splitlines()]
        insert = next(i for i, ln in enumerate(lines) if ln.startswith("sys.path.insert"))
        imported = next(i for i, ln in enumerate(lines) if ln.startswith("import live_lawn_setup"))
        self.assertLess(insert, imported, "the path insert must come before the import")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Scenario", "-TimeoutSec", "-SkipSetup"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.DEFAULT_SCENARIO, "status-l2-wither")
        self.assertEqual(p.DEFAULT_TIMEOUT_SEC, 90)
        self.assertEqual(p.POLL_INTERVAL_SEC, 0.4)
        self.assertEqual(p.EVENT_PAGE, 100)
        self.assertEqual(p.FINAL_PAGE, 300)
        self.assertEqual(p.REQUEST_TIMEOUT, 10)
        self.assertEqual(p.DONE_KIND, "debug.run-steps.done")
        self.assertEqual(p.COUNTED, ("debug.fx.state.started", "debug.status.apply", "debug.status"))

    def test_the_FLAG_spelling_and_the_COUNTS_line_survive(self) -> None:
        """The original printed one specific line, which a caller or a runbook may match on."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("fx.state.started={", source)
        self.assertIn("debug.status.apply={", source)
        self.assertIn("debug.status={", source)
        self.assertIn("PASS {", source)

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - p.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - p.REFUSAL_REASONS)}")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-status-l2-one.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("timeout", "deadline", "hardcoded", "url path", "machine-readable"):
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


class AgainstARealGame(unittest.TestCase):
    """The real path, on a real running game, when one is reachable. This is the proof the port did not
    have when it landed: the tool was committed with a refusal-path proof because no game was running."""

    def setUp(self) -> None:
        env = os.environ.get(p.lib.BASE_URL_ENV, "").strip()
        if not env:
            self.skipTest(f"${p.lib.BASE_URL_ENV} is unset; this case drives a real game and will not "
                          f"fall back to {p.lib.DEFAULT_BASE_URL}, which is the owner's own server")
        if not env.lower().startswith(("http://", "127.0.0.1:", "localhost:")):
            self.skipTest(f"${p.lib.BASE_URL_ENV} is {env!r}, which is not a loopback server")
        url, _ = p.lib.resolve_base_url("")
        try:
            health = p.lib._get_json(f"{url}/health", 5, "GET /health")
        except p.lib.Refusal as refusal:
            self.skipTest(f"no server at {url}: {refusal.detail}")
        if not health.get("injectorConnected"):
            self.skipTest(f"the server at {url} is up but NO INJECTOR is connected, so the real path "
                          f"cannot run. Start a game in a pool slot; this is not a failure of the tool.")
        self.url = url

    def test_it_ENTERS_a_board_and_returns_a_REAL_pointer(self) -> None:
        """The port's real work: enter level 1, run the lab scenario, get a living zombie pointer. A stub
        cannot produce a pointer, which is exactly why this was unprovable without a game."""
        board = p.lib.ensure_live_lab_board(self.url, timeout_sec=60)
        self.assertTrue(board.target_ptr, "the board came back with no target pointer")
        self.assertRegex(board.target_ptr, r"^[0-9A-Fa-f]+$",
                         f"{board.target_ptr!r} does not look like the hex pointer the injector returns")

    def test_a_REAL_scenario_run_produces_a_VERDICT_from_a_REAL_event_stream(self) -> None:
        """The end-to-end case in `TheWholeRunEndToEnd` substitutes the board step because reaching the
        real one needed a game. With a game, this is the same flow with nothing substituted -- and it is
        the proof the port originally lacked."""
        code, payload, _ = self._run_against_the_real_game()
        self.assertIn(payload.get("verdict"), ("OK", "FAILED"),
                      f"a real run produced no verdict at all: {payload}")
        self.assertIs(payload["completed"], True, "the real scenario did not complete")
        self.assertGreater(payload["collectedEvents"], 0, "the real run observed no events")
        self.assertIn("fx.state.started", payload["counts"])
        self.assertIn(code, (0, 1), f"an unexpected exit code {code}")

    def _run_against_the_real_game(self):
        import io
        from contextlib import redirect_stderr, redirect_stdout
        out = io.StringIO()
        with redirect_stdout(out), redirect_stderr(io.StringIO()):
            code = p.main(["--json", "--timeout-sec", "60"])
        return code, json.loads(out.getvalue()), []


if __name__ == "__main__":
    unittest.main()
