"""Contract tests for `gk-core/scripts/lib/live_lawn_setup.py`.

THIS IS A MODULE, so the first thing under test is that it is one: importable, with the seven functions the
four callers use. The PowerShell form was DOT-SOURCED, and a file that cannot be imported cannot be a
library -- so a module that only runs as a script would be a wrapper wearing a module's name.

THE HARDEST FUNCTION IS THE ONE WITH THE REAL PROOF. `get_debug_max_event_id` is an exponential probe
then a bisect over an id space two orders of magnitude above a default. Measured against this repository's
own servers it returns 928853, and the retired PowerShell returned 928853 too. A case that drives a
loopback server whose id space is large enough to force BOTH the doubling and several bisect steps is the
only way to pin that here; a stub that always answers the same thing proves nothing about the search.

THE DEFECTS THIS PORT RETIRES, each with a case:
  * the default base URL was the OWNER's port, hardcoded, so a setup script entered somebody else's board;
  * the event reads had NO timeout and the search had no total budget, so a hung read hung the search;
  * `Get-DebugPayload`'s empty `catch` turned "no payload" and "a payload that would not parse" into the
    same `null`, so a scenario that produced no evidence looked like one that produced unreadable evidence.

AND THE THING THAT IS EASIEST TO GET WRONG: a refusal whose MESSAGE loses the skill pointer. Those
messages are the accumulated knowledge of several live-debug incidents, which is the reason the file
exists, so a case pins the content rather than the mere presence of a string.
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
MODULE_PATH = Path(os.environ.get("LIVE_LAWN_SETUP_MODULE",
                                  REPO / "scripts" / "lib" / "live_lawn_setup.py")).resolve()
LIB_DIR = MODULE_PATH.parent
SUITE = REPO / "tests" / "tools" / "test_live_lawn_setup.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("live_lawn_setup", MODULE_PATH)
lls = importlib.util.module_from_spec(_spec)
sys.modules["live_lawn_setup"] = lls
_spec.loader.exec_module(lls)

# The seven functions the four callers use. A CLOSED vocabulary: the caller set is a set the callers own,
# and a rename that misses one is invisible until a live script fails mid-run.
CALLER_FUNCTIONS = ("get_debug_max_event_id", "get_debug_payload", "invoke_debug_post",
                    "wait_live_board_snapshot", "get_live_board_entities",
                    "get_recent_live_debug_errors", "ensure_live_lab_board")


class _Server:
    """A real HTTP server for this port, with a settable id space and event kinds."""

    max_id = 0
    events: list[dict] = []
    health: dict = {"ok": True, "injectorConnected": True, "simEnabled": False}
    post_status = 200
    post_body: dict = {"entered": True, "levelType": "lab", "targetPtr": "0xZ", "plantPtr": "0xP"}
    posts: list[str] = []
    requests: list[str] = []
    post_bodies: list[bytes] = []


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        _Server.requests.append(f"GET {self.path}")
        if self.path.startswith("/health"):
            return self._json(200, _Server.health)
        if self.path.startswith("/api/events"):
            query = dict(p.split("=", 1) for p in self.path.split("?", 1)[1].split("&") if "=" in p)
            after = int(query.get("afterId", "0"))
            limit = int(query.get("limit", "100"))
            items = [e for e in _Server.events if int(e["id"]) > after][:limit]
            return self._json(200, {"items": items})
        self.send_error(404, "no fixture route")

    def do_POST(self):
        _Server.posts.append(self.path)
        _Server.post_bodies.append(self.rfile.read(int(self.headers.get("Content-Length", "0") or 0)))
        self._json(_Server.post_status, _Server.post_body)

    def _json(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def serve(**kwargs):
    _Server.max_id = kwargs.get("max_id", 0)
    _Server.events = kwargs.get("events", [])
    _Server.health = kwargs.get("health", {"ok": True, "injectorConnected": True, "simEnabled": False})
    _Server.post_status = kwargs.get("post_status", 200)
    _Server.post_body = kwargs.get("post_body", {"entered": True, "levelType": "lab",
                                                "targetPtr": "0xZ", "plantPtr": "0xP"})
    _Server.posts = []
    _Server.requests = []
    _Server.post_bodies = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def clean_env(**extra) -> dict:
    env = {k: v for k, v in os.environ.items() if k != lls.BASE_URL_ENV}
    env.update(extra)
    return env


def synthetic_events(count: int) -> list[dict]:
    return [{"id": i, "kind": "debug.status", "payload": "{}"} for i in range(1, count + 1)]


class ItIsAModule(unittest.TestCase):
    """The PowerShell form was dot-SOURCED. A file that cannot be imported cannot be a library."""

    def test_all_SEVEN_caller_functions_exist_and_are_callable(self) -> None:
        for name in CALLER_FUNCTIONS:
            self.assertTrue(callable(getattr(lls, name, None)),
                            f"{name} is missing; the callers cannot be ported onto this module")

    def test_it_imports_BY_PATH_the_way_a_caller_will(self) -> None:
        """Not just `import live_lawn_setup` from a sys.path this suite happened to set: a caller lives in
        `scripts/`, one level up, and must be able to put this directory on the path and import it."""
        script = ("import sys\n"
                  f"sys.path.insert(0, {str(LIB_DIR)!r})\n"
                  "import live_lawn_setup as m\n"
                  "print(len([n for n in " + repr(CALLER_FUNCTIONS) + " if callable(getattr(m, n, None))]))\n")
        proc = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT, cwd=str(REPO))
        self.assertEqual(proc.returncode, 0, proc.stderr[-300:])
        self.assertEqual(proc.stdout.strip(), str(len(CALLER_FUNCTIONS)))

    def test_the_CALLER_map_in_the_docstring_matches_the_REAL_function_names(self) -> None:
        """The docstring publishes a PowerShell-to-Python rename map, so a rename that misses one is a
        STALE CITATION in a file whose whole job is to stop people guessing."""
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        for old in ("Get-DebugMaxEventId", "Get-DebugPayload", "Invoke-DebugPost",
                    "Wait-LiveBoardSnapshot", "Get-LiveBoardEntities",
                    "Get-RecentLiveDebugErrors", "Ensure-LiveLabBoard"):
            self.assertIn(old, head, f"the map does not mention the retired name {old}")
        for new in CALLER_FUNCTIONS:
            self.assertIn(f"-> {new}", head, f"the map does not name {new}")

    def test_it_is_also_RUNNABLE_on_its_own(self) -> None:
        """A module that cannot be run cannot be probed by hand, which is how a live script is checked."""
        proc = subprocess.run([sys.executable, str(MODULE_PATH), "preflight", "--help"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO))
        self.assertEqual(proc.returncode, 0, proc.stderr[-300:])
        self.assertIn("--base-url", proc.stdout)


class TheSearch(unittest.TestCase):
    """The hardest function in the file, and the one with a real measured value: 928853 on this
    repository's own servers, from the retired PowerShell and from this port alike."""

    def test_it_finds_the_MAX_id_of_a_LARGE_id_space(self) -> None:
        # Large enough to force the doubling past 8 probes and several bisect steps -- an id space of 12
        # would pass with the doubling removed.
        count = 5000
        server, base = serve(max_id=count, events=synthetic_events(count))
        try:
            # N-1, NOT N. The search asks "are there events with id > X", so the largest id that still has
            # a successor over a contiguous 1..N space is N-1. The retired PowerShell returned the same
            # N-1 -- the differential measured 928853 from both against this repository's own servers --
            # so the semantics are preserved and only my EXPECTATION was wrong. A reader who assumes the
            # true maximum here will build a caller that skips the newest event.
            self.assertEqual(lls.get_debug_max_event_id(base), count - 1)
        finally:
            server.shutdown()

    def test_an_EMPTY_event_stream_reads_as_ZERO(self) -> None:
        server, base = serve(events=[])
        try:
            self.assertEqual(lls.get_debug_max_event_id(base), 0)
        finally:
            server.shutdown()

    def test_a_SINGLE_event_stream_reads_as_ZERO_because_it_has_NO_successor(self) -> None:
        """The same N-1 property at the smallest scale: one event with id 1 has nothing after it, so the
        search returns 0. The original did the same, which is why this is pinned as a value and not
        described as "the newest id"."""
        server, base = serve(events=synthetic_events(1))
        try:
            self.assertEqual(lls.get_debug_max_event_id(base), 0)
        finally:
            server.shutdown()

    def test_a_HUNG_server_is_a_NAMED_refusal_not_a_HANG(self) -> None:
        """The original had nothing stopping any read, so one hung request hung the whole search with no
        output and no way to tell it from a slow one."""
        server, base = serve(events=synthetic_events(10))
        try:
            with mock.patch.object(lls, "_URLOPEN", side_effect=TimeoutError()):
                with self.assertRaises(lls.Refusal) as caught:
                    lls.get_debug_max_event_id(base, timeout=1)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "EVENT-READ-TIMED-OUT")

    def test_the_SEARCH_carries_a_TOTAL_budget_as_well_as_a_per_request_timeout(self) -> None:
        """A per-request timeout bounds ONE call and not the cost of dozens of them. The original doubled
        `hi` until a read came back empty, which is correct and unbounded in TIME."""
        server, base = serve(events=synthetic_events(200))
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.get_debug_max_event_id(base, timeout=5, budget_sec=0.0)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "SEARCH-BUDGET-EXHAUSTED")
        self.assertIn("budget", caught.exception.detail)

    def test_EVERY_event_READ_carries_a_TIMEOUT(self) -> None:
        seen: list[dict] = []

        def open_(request, timeout=None):
            seen.append({"timeout": timeout})
            raise OSError("stop here")

        with mock.patch.object(lls, "_URLOPEN", open_):
            with self.assertRaises(lls.Refusal):
                lls.get_events("http://127.0.0.1:1", 0, 10)
        self.assertTrue(seen)
        for call in seen:
            self.assertIsNotNone(call["timeout"], "an event read with no timeout")


class ThePayloadIsNotSilentlyNull(unittest.TestCase):
    """The original's empty `catch` made 'no payload' and 'a payload that would not parse' the same value,
    so a scenario that produced no evidence looked exactly like one that produced unreadable evidence."""

    def test_an_ABSENT_payload_is_none(self) -> None:
        self.assertIsNone(lls.get_debug_payload(None))
        self.assertIsNone(lls.get_debug_payload({"id": 1, "kind": "debug.status"}))

    def test_a_JSON_STRING_payload_is_PARSED(self) -> None:
        self.assertEqual(lls.get_debug_payload({"id": 2, "payload": '{"error": "boom"}'}),
                         {"error": "boom"})

    def test_an_ALREADY_PARSED_payload_is_returned_unchanged(self) -> None:
        self.assertEqual(lls.get_debug_payload({"id": 3, "payload": {"message": "hi"}}), {"message": "hi"})

    def test_a_STRING_that_is_NOT_JSON_is_a_NAMED_refusal_not_a_null(self) -> None:
        with self.assertRaises(lls.Refusal) as caught:
            lls.get_debug_payload({"id": 4, "kind": "debug.status", "payload": "plain text"})
        self.assertEqual(caught.exception.reason, "PAYLOAD-UNPARSEABLE")
        self.assertIn("debug.status", caught.exception.detail, "the refusal must name the event kind")

    def test_the_two_ABSENT_shapes_stay_DISTINGUISHABLE(self) -> None:
        """The whole point: they must not collapse into one value. This case is the assertion the
        original's behaviour could not have passed."""
        try:
            lls.get_debug_payload({"id": 5, "kind": "k", "payload": "not json"})
            self.fail("a string that is not JSON must not read as an absent payload")
        except lls.Refusal as refusal:
            self.assertNotEqual(refusal.reason, "PAYLOAD-UNPARSEABLE-absent")
        self.assertIsNone(lls.get_debug_payload({"id": 6, "kind": "k"}))


class ThePureEntitySplit(unittest.TestCase):
    """`get_live_board_entities` is pure, so it is decided by its own inputs -- no server needed."""

    def test_it_splits_LIVING_plant_and_zombie_entities(self) -> None:
        plants, zombies = lls.get_live_board_entities({"entities": [
            {"side": "plant", "living": True, "ptr": "p1"},
            {"side": "plant", "living": False, "ptr": "p2"},
            {"side": "zombie", "living": True, "ptr": "z1"},
            {"side": "zombie", "living": True, "ptr": "z2"},
            {"side": "zombie", "living": False, "ptr": "z3"},
            {"side": "pet", "living": True, "ptr": "q1"},
        ]})
        self.assertEqual([p["ptr"] for p in plants], ["p1"])
        self.assertEqual([z["ptr"] for z in zombies], ["z1", "z2"])

    def test_the_DEGENERATE_snapshots_all_give_two_empty_lists(self) -> None:
        for snapshot in (None, {}, {"entities": []}, {"entities": "not a list"}, {"other": 1}):
            with self.subTest(snapshot=snapshot):
                self.assertEqual(lls.get_live_board_entities(snapshot), ([], []))


class TheErrorScan(unittest.TestCase):
    """`error` is preferred over `message`, then the raw payload -- the original's order, which encodes
    what a real failure looked like."""

    def test_it_scans_EXACTLY_the_two_error_kinds(self) -> None:
        events = [
            {"id": 1, "kind": "cheat.error", "payload": '{"error": "e1"}'},
            {"id": 2, "kind": "debug.effect.error", "payload": '{"message": "m1"}'},
            {"id": 3, "kind": "debug.status", "payload": '{"error": "ignored"}'},
            {"id": 4, "kind": "some.other.error", "payload": '{"error": "ignored"}'},
            {"id": 5, "kind": "cheat.error", "payload": "raw text"},
        ]
        server, base = serve(events=events)
        try:
            lines = lls.get_recent_live_debug_errors(base, 0)
        finally:
            server.shutdown()
        self.assertEqual(lines, ["cheat.error: e1", "debug.effect.error: m1", "cheat.error: raw text"])

    def test_error_is_PREFERRED_over_message(self) -> None:
        events = [{"id": 1, "kind": "cheat.error", "payload": '{"error": "the error", "message": "the message"}'}]
        server, base = serve(events=events)
        try:
            self.assertEqual(lls.get_recent_live_debug_errors(base, 0), ["cheat.error: the error"])
        finally:
            server.shutdown()


class TheBaseUrlIsReadOnceAndItsSourceIsReported(unittest.TestCase):
    """The original's parameter default was the OWNER's port on a machine with a three-slot pool."""

    def test_the_FLAG_wins(self) -> None:
        with mock.patch.dict(os.environ, {lls.BASE_URL_ENV: "http://127.0.0.1:9"}, clear=False):
            url, source = lls.resolve_base_url("http://127.0.0.1:5101")
        self.assertEqual(url, "http://127.0.0.1:5101")
        self.assertEqual(source, "explicit")

    def test_the_ENVIRONMENT_is_used_when_no_FLAG_is_given(self) -> None:
        with mock.patch.dict(os.environ, {lls.BASE_URL_ENV: "http://127.0.0.1:5102"}, clear=False):
            url, source = lls.resolve_base_url("")
        self.assertEqual(url, "http://127.0.0.1:5102")
        self.assertEqual(source, f"${lls.BASE_URL_ENV}")

    def test_with_NEITHER_the_DEFAULT_is_used_AND_SAYS_so(self) -> None:
        env = {k: v for k, v in os.environ.items() if k != lls.BASE_URL_ENV}
        with mock.patch.dict(os.environ, env, clear=True):
            url, source = lls.resolve_base_url("")
        self.assertEqual(url, lls.DEFAULT_BASE_URL)
        self.assertIn("default", source)
        self.assertIn(lls.BASE_URL_ENV, source)

    def test_a_trailing_SLASH_is_TRIMMED(self) -> None:
        self.assertEqual(lls.resolve_base_url("http://127.0.0.1:5103/")[0], "http://127.0.0.1:5103")

    def test_a_non_HTTP_base_URL_is_a_NAMED_refusal(self) -> None:
        with self.assertRaises(lls.Refusal) as caught:
            lls.resolve_base_url("not-a-url")
        self.assertEqual(caught.exception.reason, "BASE-URL-INVALID")

    def test_the_RETURNED_record_carries_the_URL_and_its_SOURCE(self) -> None:
        """A caller that cannot see which server it measured cannot tell a wrong board from a right one."""
        board = lls.LabBoard(base_url="http://127.0.0.1:5101", base_url_source=f"${lls.BASE_URL_ENV}")
        self.assertEqual(board.to_json()["BaseUrl"], "http://127.0.0.1:5101")
        self.assertEqual(board.to_json()["BaseUrlSource"], f"${lls.BASE_URL_ENV}")


class ThePreflightOrder(unittest.TestCase):
    """Order matters and is the original's: health, then injector, then the quick-start POST. Checking the
    injector first would name the wrong thing when there is no server at all."""

    def test_no_SERVER_is_distinguished_from_no_INJECTOR(self) -> None:
        with self.assertRaises(lls.Refusal) as caught:
            lls.ensure_live_lab_board("http://127.0.0.1:1")
        self.assertEqual(caught.exception.reason, "SERVER-UNREACHABLE")
        self.assertIn("/health", caught.exception.detail)

    def test_health_ok_false_is_its_OWN_refusal(self) -> None:
        server, base = serve(health={"ok": False, "injectorConnected": True})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "HEALTH-NOT-OK")

    def test_no_INJECTOR_names_the_SKILL_and_the_URL_it_probed(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": False})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "INJECTOR-NOT-CONNECTED")
        # The original's message, which is the accumulated knowledge of several live-debug incidents.
        self.assertIn("live-lawn-quick-start", caught.exception.detail)
        self.assertIn(base, caught.exception.detail)

    def test_an_INVALID_scenario_REFUSES_before_any_request(self) -> None:
        with self.assertRaises(lls.Refusal) as caught:
            lls.ensure_live_lab_board("http://127.0.0.1:1", scenario="not-a-scenario")
        self.assertEqual(caught.exception.reason, "INVALID-SCENARIO")

    def test_a_NON_POSITIVE_TIMEOUT_REFUSES_before_any_request(self) -> None:
        for kwargs in ({"timeout_sec": 0}, {"event_timeout": 0}, {"snapshot_timeout_sec": 0}):
            with self.subTest(**kwargs):
                with self.assertRaises(lls.Refusal) as caught:
                    lls.ensure_live_lab_board("http://127.0.0.1:1", **kwargs)
                self.assertEqual(caught.exception.reason, "INVALID-TIMEOUT")

    def test_simEnabled_is_REPORTED_rather_than_only_warned_about(self) -> None:
        server, base = serve(health={"ok": True, "injectorConnected": True, "simEnabled": True},
                             events=synthetic_events(4))
        try:
            with mock.patch.object(lls, "wait_live_board_snapshot", return_value=None):
                with self.assertRaises(lls.Refusal):
                    lls.ensure_live_lab_board(base, skip_setup=True)
        finally:
            server.shutdown()


class ThePostNamesItsEndpoint(unittest.TestCase):
    def test_a_FAILED_POST_names_the_PATH_it_posted_to(self) -> None:
        server, base = serve(post_status=500, post_body={"error": "no injector"})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.invoke_debug_post(base, "/lawn/quick-start", {"scenario": "lab-overlay"})
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "DEBUG-POST-FAILED")
        self.assertIn("/api/debug/lawn/quick-start", caught.exception.detail)

    def test_a_TIMED_OUT_POST_says_the_INJECTOR_is_the_thing_that_is_not_there(self) -> None:
        with mock.patch.object(lls, "_URLOPEN", side_effect=TimeoutError()):
            with self.assertRaises(lls.Refusal) as caught:
                lls.invoke_debug_post("http://127.0.0.1:1", "/effect/board-snapshot", {})
        self.assertEqual(caught.exception.reason, "DEBUG-POST-FAILED")
        self.assertIn("injector", caught.exception.detail)

    def test_an_EMPTY_POST_body_is_SENT_as_an_empty_OBJECT_not_a_null(self) -> None:
        """The property is about the REQUEST. The first version asserted the RESPONSE, which the fixture
        fills in, so it would have passed no matter what the wrapper sent."""
        # The SHARED fixture, not a second inlined server. The first version of this case spun up its own
        # server whose `Thread(...).start()` sat outside any `with`/`try`, and the suite's own
        # "no case starts a server it cannot stop" case correctly flagged it -- a meta-case that is right
        # about the code and gets ignored is worse than none.
        server, base = serve()
        try:
            lls.invoke_debug_post(base, "/effect/board-snapshot", None)
        finally:
            server.shutdown()
        self.assertEqual(len(_Server.post_bodies), 1)
        self.assertEqual(json.loads(_Server.post_bodies[0].decode()), {},
                         f"the request body was {_Server.post_bodies[0]!r}, not an empty object")

    def test_the_POST_carries_a_timeout(self) -> None:
        seen: list[dict] = []

        def open_(request, timeout=None):
            seen.append({"timeout": timeout})
            raise OSError("stop")

        with mock.patch.object(lls, "_URLOPEN", open_):
            with self.assertRaises(lls.Refusal):
                lls.invoke_debug_post("http://127.0.0.1:1", "/x", {})
        self.assertIsNotNone(seen[0]["timeout"])


class Surface(unittest.TestCase):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = MODULE_PATH.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": refusal\.reason', source)) and set() or set()
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - lls.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - lls.REFUSAL_REASONS)}")

    def test_the_SCENARIOS_are_a_CLOSED_vocabulary_the_original_declared(self) -> None:
        self.assertEqual(lls.SCENARIOS, ("lab-overlay", "lab-empty"))

    def test_the_ERROR_KINDS_are_the_two_the_original_scanned(self) -> None:
        self.assertEqual(lls.ERROR_KINDS, ("cheat.error", "debug.effect.error"))

    def test_the_default_INTERVAL_and_snapshot_budget_survive_the_port(self) -> None:
        self.assertEqual(lls.POLL_INTERVAL_SEC, 0.4)
        self.assertEqual(lls.SNAPSHOT_TIMEOUT_SEC, 15)
        self.assertEqual(lls.DEFAULT_SETUP_TIMEOUT_SEC, 60)
        self.assertEqual(lls.DEBUG_POST_TIMEOUT, 15)
        self.assertEqual(lls.HEALTH_TIMEOUT, 5)

    def test_it_states_WHY_POWERSHELL_WAS_retired(self) -> None:
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("LiveLawnSetup.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("hardcoded", "timeout", "budget", "null", "dot-sourced"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_RETURNED_record_keeps_the_ORIGINALS_field_names(self) -> None:
        """A caller ported from PowerShell looks the fields up by name. Renaming them is a silent break in
        four files, so the PowerShell names are part of the contract."""
        payload = lls.LabBoard(target_ptr="z", plant_ptr="p", level_type="lab", entered=True).to_json()
        for field in ("TargetPtr", "PlantPtr", "LevelType", "Entered", "Scenario"):
            self.assertIn(field, payload, f"the original's {field} field name is gone")

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
            if isinstance(target, ast.Name) and target.id == "lls":
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



class TheMessagesAreTheReasonTheFileExists(unittest.TestCase):
    """The refusal texts are the accumulated knowledge of several live-debug incidents. A refusal that
    stops carrying its evidence is a message that got shorter and less useful, and no other tool notices."""

    def test_a_health_document_with_NO_ok_field_is_NOT_ok(self) -> None:
        """Found by falsification. `not health.get("ok")` and `health.get("ok") is False` differ for
        exactly one input -- the field being ABSENT -- and no fixture omitted it. A server part-way through
        boot is the realistic shape of that input, and reading it as ready would send a caller into a
        setup that cannot succeed."""
        server, base = serve(health={"injectorConnected": True})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "HEALTH-NOT-OK")

    def test_the_no_LIVING_ZOMBIE_refusal_carries_the_RECENT_ERRORS(self) -> None:
        """The error lines are the whole diagnostic value of this message: without them a live operator
        has a refusal and nothing to act on."""
        # ONE error event, at the id the search's cursor lands JUST BELOW. The search returns max-1, so
        # with ids 1..6 and 900 the cursor is 899 and the event at 900 is above it. Two earlier fixtures
        # planted the errors AT and just above the maximum, which the cursor then correctly excluded -- the
        # case failed because its own data was filtered out, which is a broken case and not a finding.
        events = synthetic_events(6) + [
            {"id": 900, "kind": "cheat.error", "payload": '{"error": "lab board never opened"}'},
        ]
        server, base = serve(events=events, post_body={"entered": False, "levelType": ""})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base, scenario="lab-overlay", snapshot_timeout_sec=1)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "NO-LIVING-ZOMBIE")
        self.assertIn("Recent errors", caught.exception.detail)
        self.assertIn("lab board never opened", caught.exception.detail,
                      "the section is present but empty, which is the same loss with a heading")

    def test_the_skip_SETUP_hint_is_present_because_it_moves_the_burden(self) -> None:
        """`skip_setup` skips /lawn/quick-start, so the caller must already have the game on a lab board.
        Without that sentence the flag looks like a harmless shortcut."""
        server, base = serve(events=synthetic_events(4), post_body={})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base, scenario="lab-overlay", skip_setup=True,
                                        snapshot_timeout_sec=1)
        finally:
            server.shutdown()
        self.assertIn("skip_setup skips /lawn/quick-start", caught.exception.detail)
        self.assertIn("already be on a lab board", caught.exception.detail)

    def test_a_FAILED_quick_start_names_the_ENDPOINT_it_POSTED_to(self) -> None:
        server, base = serve(post_status=500, post_body={"error": "no level 1 on this board"})
        try:
            with self.assertRaises(lls.Refusal) as caught:
                lls.ensure_live_lab_board(base)
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "QUICK-START-FAILED")
        self.assertIn("/api/debug/lawn/quick-start", caught.exception.detail,
                      "a quick-start failure that names no endpoint sends the reader looking")

    def test_simEnabled_is_REPORTED_on_the_RECORD_not_only_warned_about(self) -> None:
        """The original wrote one `Write-Warning` to the host and returned a record that did not mention
        it, so the fact was visible to a person watching and invisible to a caller. A caller that reads
        the record is the case this port exists for."""
        server, base = serve(health={"ok": True, "injectorConnected": True, "simEnabled": True},
                             events=synthetic_events(4),
                             post_body={"entered": True, "levelType": "lab", "targetPtr": "0xZ"})
        try:
            board = lls.ensure_live_lab_board(base, snapshot_timeout_sec=1)
        finally:
            server.shutdown()
        self.assertIs(board.sim_enabled, True)
        self.assertIs(board.to_json()["SimEnabled"], True)

class AgainstARealGame(unittest.TestCase):
    """The library's real path, on a real running game, when one is reachable.

    The port was committed with the refusal path as its only live proof, because no game was running.
    These are the cases that were not possible then, and they are the ones that would have caught a
    wrong assumption about the injector's actual answers."""

    def setUp(self) -> None:
        env = os.environ.get(lls.BASE_URL_ENV, "").strip()
        if not env:
            self.skipTest(f"${lls.BASE_URL_ENV} is unset; this case drives a real game and will not "
                          f"fall back to {lls.DEFAULT_BASE_URL}, which is the owner's own server")
        if not env.lower().startswith(("http://", "127.0.0.1:", "localhost:")):
            self.skipTest(f"${lls.BASE_URL_ENV} is {env!r}, which is not a loopback server")
        url, _ = lls.resolve_base_url("")
        try:
            health = lls._get_json(f"{url}/health", 5, "GET /health")
        except lls.Refusal as refusal:
            self.skipTest(f"no server at {url}: {refusal.detail}")
        if not health.get("injectorConnected"):
            self.skipTest(f"the server at {url} is up but NO INJECTOR is connected, so the real path "
                          f"cannot run. Start a game in a pool slot; this is not a failure of the tool.")
        self.url = url

    def test_the_search_finds_a_REAL_max_id_in_a_LIVE_event_stream(self) -> None:
        """The committed proof used a loopback fixture. Against a real game the stream is being written
        as the search runs, which is the condition a fixture cannot reproduce."""
        value = lls.get_debug_max_event_id(self.url)
        self.assertGreater(value, 0, "a live server reported no events at all")

    def test_Ensure_LiveLabBoard_returns_a_POINTER_that_looks_LIKE_a_pointer(self) -> None:
        board = lls.ensure_live_lab_board(self.url, timeout_sec=60)
        self.assertTrue(board.target_ptr, "a real board came back with no target pointer")
        self.assertRegex(board.target_ptr, r"^[0-9A-Fa-f]+$",
                         f"{board.target_ptr!r} does not look like the hex pointer the injector returns")
        self.assertEqual(board.base_url, self.url)
        self.assertIsNotNone(board.sim_enabled, "simEnabled is not reported, which was one of the port's promises")

    def test_a_LIVE_payload_is_either_PARSED_or_NAMED_unparseable_never_silently_null(self) -> None:
        """The port's headline change. Against a real stream this is exercised by whatever the game
        actually emitted, not by a shape I chose -- so the strict path is proven on real payloads."""
        items = lls.get_events(self.url, 0, 200)
        self.assertTrue(items, "the live stream was empty")
        unparseable = 0
        for event in items:
            try:
                payload = lls.get_debug_payload(event)
            except lls.Refusal as refusal:
                self.assertEqual(refusal.reason, "PAYLOAD-UNPARSEABLE")
                unparseable += 1
                continue
            if payload is not None and not isinstance(payload, (dict, list, str, int, float, bool)):
                self.fail(f"an unexpected payload type: {type(payload).__name__}")
        # Reported, not asserted to be zero: the property is that the two states stay DISTINGUISHABLE,
        # and a run where no payload happened to be a bad string proves nothing either way.
        self.assertGreater(len(items), 0)
        print(f"    live stream: {len(items)} event(s), {unparseable} unparseable payload(s)")

    def test_the_error_scan_survives_a_LIVE_stream(self) -> None:
        lines = lls.get_recent_live_debug_errors(self.url, 0)
        self.assertIsInstance(lines, list)
        for line in lines:
            self.assertRegex(line, r"^(cheat\.error|debug\.effect\.error): ",
                             f"an error line in the wrong shape: {line!r}")


if __name__ == "__main__":
    unittest.main()
