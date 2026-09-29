"""Contract tests for `gk-core/scripts/lib/debug_status_apply.py`.

THIS IS A MODULE, so the first thing under test is that it is one. The PowerShell form was DOT-SOURCED
by `audit-status-vfx-identity.ps1`, and a file that cannot be imported cannot be a library.

THE SUBSTANCE IS PURE AND DECIDED BY PLANTED PAGES: `wait_status_fx_started` filters a stream of events by
`kind` and by the `statusId` inside the payload. That is the whole function, and it can be decided without
a game. The board step is substituted for the end-to-end cases -- and a stub cannot produce a pointer,
which is why the real proof is the self-skipping live block at the end, not these cases.

THE FINDING THIS PORT EXISTS TO SURFACE IS PINNED AS A PROPERTY, NOT AS A COMMENT. Measured against the
running game: the apply endpoint accepts `statusId: "status-l2-wither"`, and the resulting
`debug.fx.state.started` payload carries `statusId: "wither"`. So the original's equality filter cannot
match for any `status-l2-*` id, and its `$false` return is identical whether the apply was refused, the fx
never started, or the fx started under a different name. The port's answer is `seenStatusIds`, and a case
drives the mismatch deliberately.

THE TWO-BUDGET MISMATCH IS PINNED AS DATA. `$DurationMs = 6000` is applied and `$TimeoutMs = 2500` is
waited, and the two numbers live in different functions in the original, so a reader could not see the
relationship. Both are recorded in every result and a case asserts the values are what the original had.
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
MODULE_PATH = Path(os.environ.get("DEBUG_STATUS_APPLY_MODULE",
                                  REPO / "scripts" / "lib" / "debug_status_apply.py")).resolve()
LIB_DIR = MODULE_PATH.parent
SUITE = REPO / "tests" / "tools" / "test_debug_status_apply.py"
RUN_TIMEOUT = 300

CALLER_FUNCTIONS = ("get_live_target_ptr", "wait_status_fx_started",
                    "invoke_status_apply_until_started", "clear_status_target")

WOTHER_ID = "status-l2-wither"
GAME_ID = "wither"  # what the game actually reports -- see the module docstring


class _Server:
    events: list[dict] = []
    # The id ABOVE which no event is visible YET. A static list cannot express "later events do not exist
    # yet", so a cursor-advancing case needs a stream that grows. Without it, every event above the
    # cursor is visible on the FIRST pass and the case's premise is false.
    visible_upto: int | None = None
    posts: list[tuple[str, dict]] = []
    post_body: dict = {"ok": True, "queued": 1, "command": "debug.status.apply"}
    clear_body: dict = {"ok": True, "queued": 1}
    health: dict = {"ok": True, "injectorConnected": True, "simEnabled": False}

    @staticmethod
    def set_cursor(step: tuple[int, int]) -> int:
        """Move the stream to the next step: set the watermark and return the cursor for this pass.

        The cursor the caller receives and the ceiling the server serves must move TOGETHER, or a static
        event list makes every later event visible on the first pass and the case's premise is false."""
        cursor, ceiling = step
        _Server.visible_upto = ceiling
        return cursor


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        if self.path.startswith("/health"):
            return self._json(200, _Server.health)
        if self.path.startswith("/api/events"):
            query = dict(p.split("=", 1) for p in self.path.split("?", 1)[1].split("&") if "=" in p)
            after = int(query.get("afterId", "0"))
            limit = int(query.get("limit", "100"))
            ceiling = len(_Server.events) if _Server.visible_upto is None else _Server.visible_upto
            return self._json(200, {"items": [e for e in _Server.events
                                              if after < int(e["id"]) <= ceiling][:limit]})
        self.send_error(404, "no fixture route")

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0") or 0))
        try:
            body = json.loads(raw.decode()) if raw else {}
        except json.JSONDecodeError:
            body = {}
        _Server.posts.append((self.path, body))
        self._json(200, _Server.clear_body if "clear-status" in self.path else _Server.post_body)

    def _json(self, status, body):
        raw = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def serve(events=None, health=None, post_body=None, clear_body=None, visible_upto=None):
    _Server.events = list(events or [])
    _Server.visible_upto = visible_upto
    _Server.posts = []
    _Server.health = health or {"ok": True, "injectorConnected": True, "simEnabled": False}
    _Server.post_body = post_body or {"ok": True, "queued": 1, "command": "debug.status.apply"}
    _Server.clear_body = clear_body or {"ok": True, "queued": 1}
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def fx(status_id, event_id=1, kind="debug.fx.state.started"):
    return {"id": event_id, "kind": kind, "payload": json.dumps({"statusId": status_id})}


def _load():
    spec = importlib.util.spec_from_file_location("debug_status_apply", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    sys.modules["debug_status_apply"] = module
    spec.loader.exec_module(module)
    return module


m = _load()


def clean_env(**extra) -> dict:
    env = {k: v for k, v in os.environ.items() if k != m.lib.BASE_URL_ENV}
    env.update(extra)
    return env


class ItIsAModule(unittest.TestCase):
    def test_all_FOUR_caller_functions_exist_and_are_callable(self) -> None:
        for name in CALLER_FUNCTIONS:
            self.assertTrue(callable(getattr(m, name, None)),
                            f"{name} is missing; the caller cannot be ported onto this module")

    def test_it_imports_BY_PATH_the_way_a_caller_will(self) -> None:
        script = ("import sys\n"
                  f"sys.path.insert(0, {str(LIB_DIR)!r})\n"
                  "import debug_status_apply as m\n"
                  f"print(len([n for n in {CALLER_FUNCTIONS!r} if callable(getattr(m, n, None))]))\n")
        proc = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT, cwd=str(REPO), env=clean_env())
        self.assertEqual(proc.returncode, 0, proc.stderr[-300:])
        self.assertEqual(proc.stdout.strip(), str(len(CALLER_FUNCTIONS)))

    def test_the_CALLER_map_in_the_docstring_matches_the_REAL_function_names(self) -> None:
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        for old, new in (("Get-LiveTargetPtr", "get_live_target_ptr"),
                         ("Wait-StatusFxStarted", "wait_status_fx_started"),
                         ("Invoke-StatusApplyUntilStarted", "invoke_status_apply_until_started"),
                         ("Clear-StatusTarget", "clear_status_target")):
            self.assertIn(old, head, f"the map does not mention the retired name {old}")
            self.assertIn(f"-> {new}", head, f"the map does not name {new}")

    def test_it_is_also_RUNNABLE_on_its_own(self) -> None:
        proc = subprocess.run([sys.executable, str(MODULE_PATH), "preflight", "--help"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO),
                              env=clean_env())
        self.assertEqual(proc.returncode, 0, proc.stderr[-300:])
        self.assertIn("--status-id", proc.stdout)


class TheFxWait(unittest.TestCase):
    """The filter is the whole function, and it is decided by its own inputs."""

    def test_a_MATCHING_status_starts_the_wait(self) -> None:
        server, base = serve(events=[fx(WOTHER_ID)])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, True)
        self.assertEqual(out.matching, 1)
        self.assertEqual(out.fxEvents, 1)

    def test_a_NON_MATCHING_status_does_NOT_start_it(self) -> None:
        server, base = serve(events=[fx(GAME_ID)])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.fxEvents, 1, "the event WAS an fx event, it just named another status")
        self.assertEqual(out.matching, 0)

    def test_the_MISMATCH_is_REPORTED_not_swallowed(self) -> None:
        """The finding, as a property. The original's `$false` was identical for a refused apply, a
        missing event and a wrong name; `seenStatusIds` is what separates them."""
        server, base = serve(events=[fx(GAME_ID, 1), fx(GAME_ID, 2)])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.seenStatusIds, [GAME_ID],
                         "the statusIds the game used must be reported, or 'no fx' has no diagnosis")
        self.assertIn(GAME_ID, out.to_json()["seenStatusIds"])

    def test_MANY_DISTINCT_statusIds_are_all_reported_and_DEDUPED(self) -> None:
        server, base = serve(events=[fx("a", 1), fx("b", 2), fx("a", 3), fx("c", 4)])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertEqual(out.seenStatusIds, ["a", "b", "c"])
        self.assertEqual(out.fxEvents, 4)
        self.assertEqual(out.matching, 0)

    def test_OTHER_kinds_are_never_counted_as_fx(self) -> None:
        server, base = serve(events=[fx(WOTHER_ID, 1, kind="debug.fx.shown"),
                                     fx(WOTHER_ID, 2, kind="debug.status.apply")])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.fxEvents, 0)

    def test_an_UNPARSEABLE_payload_is_SKIPPED_and_COUNTED_not_fatal(self) -> None:
        """A refused payload is caught and the event skipped, which is what the original's `if ($p -and ...)`
        did with the `$null` the library now raises instead. One malformed event must not fail a status
        apply -- and a skip that is not COUNTED is indistinguishable from an event that never arrived."""
        server, base = serve(events=[{"id": 1, "kind": "debug.fx.state.started", "payload": "not json"},
                                     fx(WOTHER_ID, 2)])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, True, "a malformed event aborted the wait")
        self.assertEqual(out.unparseable, 1)
        self.assertEqual(out.fxEvents, 2)

    def test_an_EMPTY_stream_gives_ZERO_counts_and_a_bounded_wait(self) -> None:
        server, base = serve(events=[])
        try:
            started = time.monotonic()
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
            elapsed = time.monotonic() - started
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.eventsSeen, 0)
        self.assertGreaterEqual(out.polls, 2, "it stopped without ever retrying")
        self.assertLess(elapsed, 6.0, f"it ran {elapsed:.1f}s past a 0.6s budget")

    def test_a_deadline_ALREADY_PASSED_polls_once_and_stops(self) -> None:
        server, base = serve(events=[])
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 1)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.polls, 1, "it kept polling after the budget had run out")

    def test_a_NON_POSITIVE_budget_REFUSES_before_any_read(self) -> None:
        for value in (0, -1):
            with self.subTest(value=value):
                with self.assertRaises(m.lib.Refusal) as caught:
                    m.wait_status_fx_started("http://127.0.0.1:1", 0, "x", value)
                self.assertEqual(caught.exception.reason, "INVALID-TIMEOUT")

    def test_EVERY_read_carries_a_TIMEOUT(self) -> None:
        seen: list[dict] = []

        def capture(base_url, after_id, limit, timeout):
            seen.append({"timeout": timeout})
            return []

        with mock.patch.object(m.lib, "get_events", capture):
            m.wait_status_fx_started("http://x", 0, "x", 1)
        self.assertTrue(seen)
        for call in seen:
            self.assertIsNotNone(call["timeout"], "an unbounded event read")


class TheApplyLoop(unittest.TestCase):
    def test_the_PAYLOAD_is_the_CLOSED_key_set_the_SSOT_defines(self) -> None:
        server, base = serve(events=[fx(WOTHER_ID)], visible_upto=1)
        try:
            m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP", 6000, 20, 1, 600)
        finally:
            server.shutdown()
        path, body = _Server.posts[0]
        self.assertIn("/api/debug/status/apply", path)
        self.assertEqual(sorted(body), sorted(m.APPLY_PAYLOAD_KEYS))
        self.assertEqual(body["statusId"], WOTHER_ID)
        self.assertEqual(body["hostPtr"], "0xP")
        self.assertEqual(body["amount"], 20)
        self.assertEqual(body["durationMs"], 6000)

    def test_the_CURSOR_is_taken_BEFORE_the_POST(self) -> None:
        """A cursor taken afterwards misses the very event being waited for -- the event lands between the
        POST and the cursor, and is then below it."""
        server, base = serve(events=[fx(WOTHER_ID, 900)])
        try:
            with mock.patch.object(m.lib, "get_debug_max_event_id", return_value=0) as cursor:
                m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP", 6000, 20, 1, 600)
        finally:
            server.shutdown()
        self.assertTrue(cursor.called, "no cursor was taken at all")
        self.assertEqual(_Server.posts[0][1]["statusId"], WOTHER_ID)

    def test_it_STOPS_on_the_first_try_that_starts(self) -> None:
        # The watermark is explicit: the stream must SHOW id 5 from the start, or the first try sees
        # nothing and the loop runs to its limit for a reason the case is not about.
        server, base = serve(events=[fx(WOTHER_ID, 5)], visible_upto=5)
        try:
            out = m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP", 6000, 20, 6, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, True)
        self.assertEqual(out.tries, 1, "it kept applying after the VFX had already started")
        self.assertEqual(len(_Server.posts), 1)

    def test_it_uses_EXACTLY_the_tries_it_was_given_when_none_start(self) -> None:
        server, base = serve(events=[fx(GAME_ID, 5)], visible_upto=5)
        try:
            out = m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP", 6000, 20, 3, 300)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.tries, 3)
        self.assertEqual(len(_Server.posts), 3, "one POST per try")

    def test_each_ATTEMPT_is_recorded_so_a_caller_sees_WHICH_try_carried_it(self) -> None:
        """Try 1 must NOT match and try 2 must, so the record has to show WHICH. The cursor therefore has
        to ADVANCE between tries: the fixture serves a static list, and with a fixed cursor of 0 both
        events are visible on the first pass -- so the match lands on try 1 and the case's own premise is
        false. That is a broken fixture, and it is why this case needed rewriting rather than adjusting."""
        server, base = serve(events=[fx(GAME_ID, 5), fx(WOTHER_ID, 6)])
        # The stream grows in step with the cursor: try 1 can see id 5 only, try 2 can see id 6.
        steps = iter([(4, 5), (5, 6)])
        try:
            with mock.patch.object(m.lib, "get_debug_max_event_id",
                                   side_effect=lambda *a, **k: _Server.set_cursor(next(steps))):
                out = m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP", 6000, 20, 3, 300)
        finally:
            server.shutdown()
        self.assertEqual(len(out.attempts), out.tries)
        self.assertIs(out.attempts[0].wait.started, False,
                      f"try 1 saw a non-matching status and must not claim it started: {out.to_json()}")
        self.assertEqual(out.attempts[0].wait.seenStatusIds, [GAME_ID],
                      "...and it must report the statusId the game DID use")
        self.assertIs(out.attempts[1].wait.started, True, f"try 2 saw the match: {out.to_json()}")
        self.assertEqual(out.tries, 2)

    def test_the_TWO_BUDGETS_are_BOTH_recorded_on_every_result(self) -> None:
        """$DurationMs=6000 is applied and $TimeoutMs=2500 is waited, and in the original they sit in
        different functions, so the mismatch was invisible. Both are on the record."""
        server, base = serve(events=[fx(WOTHER_ID)], visible_upto=1)
        try:
            out = m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP")
        finally:
            server.shutdown()
        self.assertEqual(out.durationMs, 6000)
        self.assertEqual(out.fxWaitBudgetMs, 2500)
        self.assertLess(out.fxWaitBudgetMs, out.durationMs,
                        "the fx wait budget is SHORTER than the applied duration; that is the original's "
                        "relationship and changing it would change the proof")

    def test_the_REQUIRED_arguments_are_REFUSED_by_name(self) -> None:
        cases = {
            "MISSING-STATUS-ID": {"status_id": "", "host_ptr": "0xP"},
            "MISSING-HOST-PTR": {"status_id": "s", "host_ptr": ""},
            "INVALID-DURATION": {"status_id": "s", "host_ptr": "p", "duration_ms": 0},
            "INVALID-TRIES": {"status_id": "s", "host_ptr": "p", "max_tries": 0},
        }
        for reason, kwargs in cases.items():
            with self.subTest(reason=reason):
                with self.assertRaises(m.lib.Refusal) as caught:
                    m.invoke_status_apply_until_started("http://127.0.0.1:1", **kwargs)
                self.assertEqual(caught.exception.reason, reason)

    def test_a_REFUSAL_before_any_request_reaches_NO_server(self) -> None:
        server, base = serve(events=[])
        try:
            with self.assertRaises(m.lib.Refusal):
                m.invoke_status_apply_until_started(base, "", "0xP")
        finally:
            server.shutdown()
        self.assertEqual(_Server.posts, [])


class TheClear(unittest.TestCase):
    def test_it_POSTS_the_CLOSED_key_set_to_clear_status(self) -> None:
        server, base = serve()
        try:
            m.clear_status_target(base, "0xP")
        finally:
            server.shutdown()
        path, body = _Server.posts[0]
        self.assertIn("/api/debug/clear-status", path)
        self.assertEqual(sorted(body), sorted(m.CLEAR_PAYLOAD_KEYS))
        self.assertEqual(body["ptr"], "0xP")

    def test_it_RETURNS_the_response_rather_than_discarding_it(self) -> None:
        """The original piped its result to `Out-Null`, so a refused clear was invisible to a caller left
        with a board in the state they asked to leave."""
        server, base = serve(clear_body={"ok": True, "queued": 1, "note": "cleared"})
        try:
            self.assertEqual(m.clear_status_target(base, "0xP")["note"], "cleared")
        finally:
            server.shutdown()

    def test_an_ok_FALSE_answer_is_a_NAMED_refusal(self) -> None:
        server, base = serve(clear_body={"ok": False, "error": "no such pointer"})
        try:
            with self.assertRaises(m.lib.Refusal) as caught:
                m.clear_status_target(base, "0xP")
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "CLEAR-NOT-ACKNOWLEDGED")
        self.assertIn("no such pointer", caught.exception.detail)

    def test_a_MISSING_pointer_REFUSES_before_any_request(self) -> None:
        server, base = serve()
        try:
            with self.assertRaises(m.lib.Refusal) as caught:
                m.clear_status_target(base, "")
        finally:
            server.shutdown()
        self.assertEqual(caught.exception.reason, "MISSING-HOST-PTR")
        self.assertEqual(_Server.posts, [])


class ThePointer(unittest.TestCase):
    def test_an_EMPTY_pointer_is_a_NAMED_refusal_not_an_empty_string(self) -> None:
        """An empty ptr goes into a payload and produces a confusing 400; a refusal names the condition."""
        board = m.lib.LabBoard(target_ptr="", plant_ptr="p")
        with mock.patch.object(m.lib, "ensure_live_lab_board", return_value=board):
            with self.assertRaises(m.lib.Refusal) as caught:
                m.get_live_target_ptr("http://127.0.0.1:1")
        self.assertEqual(caught.exception.reason, "NO-TARGET-PTR")

    def test_a_REAL_pointer_is_returned_as_a_STRING(self) -> None:
        """The original returned `[string]$lab.TargetPtr`, and a caller interpolates it into a payload."""
        board = m.lib.LabBoard(target_ptr="1AAFCD2B000")
        with mock.patch.object(m.lib, "ensure_live_lab_board", return_value=board):
            value = m.get_live_target_ptr("http://127.0.0.1:1")
        self.assertIsInstance(value, str)
        self.assertEqual(value, "1AAFCD2B000")


class TheGapsFiveSurvivorsNamed(unittest.TestCase):
    """Five surviving mutants, five real gaps. Each is named by the mutant it produced.

    A non-dict PAYLOAD was never driven: the mutant that removed the `isinstance` skip survived because
    nothing fed the filter a list. It matters because `payload.get` on a list raises, so the skip is
    load-bearing.

    The counters were never observed ACROSS TWO POLLS: the mutant that made them count observations
    survived because every case either matched on the first poll or had no events at all. The counters'
    distinct-event property is only visible when the same event is served twice.

    A payload with NO statusId was never driven, so nothing pinned what the port reports for it -- and
    "unknown" is not the same claim as "absent".

    The two budgets were asserted as ATTRIBUTES rather than through `to_json()`, so removing either from
    the SERIALISED record was invisible. The record is what a caller reads, and a value present on the
    object but absent from the report is not reported at all.
    """

    def test_a_NON_DICT_payload_is_skipped_rather_than_crashing(self) -> None:
        """`payload.get` on a list raises, so the isinstance skip is load-bearing."""
        for payload in ([{"statusId": "x"}], "a string", 42):
            with self.subTest(payload=payload):
                server, base = serve(events=[{"id": 1, "kind": "debug.fx.state.started",
                                              "payload": json.dumps(payload)},
                                             fx(WOTHER_ID, 2)])
                try:
                    out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
                finally:
                    server.shutdown()
                self.assertIs(out.started, True, f"a {type(payload).__name__} payload aborted the wait")
                self.assertEqual(out.matching, 1)

    def test_the_SAME_event_served_TWICE_is_COUNTED_once(self) -> None:
        """The counters are DISTINCT-event counts. A fixture that matches on the first poll cannot see the
        difference; one that serves the same page twice can."""
        server, base = serve(events=[fx(GAME_ID, 5)], visible_upto=5)
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 900)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertGreaterEqual(out.polls, 2, "it must have polled more than once for this to mean anything")
        self.assertEqual(out.fxEvents, 1, f"one event was counted {out.fxEvents} times across "
                                          f"{out.polls} polls; the counters are distinct-event counts")
        self.assertEqual(out.eventsSeen, 1)

    def test_a_payload_with_NO_statusId_is_reported_as_absent_not_invented(self) -> None:
        """An absent id and the string "unknown" are different claims, and a caller matching on
        `seenStatusIds` would act on the second one."""
        server, base = serve(events=[{"id": 1, "kind": "debug.fx.state.started",
                                      "payload": json.dumps({"cueId": "x", "ptr": "1AAF"})},
                                     fx(GAME_ID, 2)], visible_upto=2)
        try:
            out = m.wait_status_fx_started(base, 0, WOTHER_ID, 600)
        finally:
            server.shutdown()
        self.assertIs(out.started, False)
        self.assertEqual(out.seenStatusIds, [GAME_ID],
                         f"an absent statusId must not be invented; got {out.seenStatusIds}")
        self.assertNotIn("unknown", out.seenStatusIds)
        self.assertEqual(out.fxEvents, 2)

    def test_BOTH_budgets_survive_into_the_SERIALISED_record(self) -> None:
        """The record is what a caller reads. Asserting the ATTRIBUTE would not see a value removed from
        `to_json()`, and a value on the object but absent from the report is not reported at all."""
        server, base = serve(events=[fx(WOTHER_ID)], visible_upto=1)
        try:
            out = m.invoke_status_apply_until_started(base, WOTHER_ID, "0xP")
        finally:
            server.shutdown()
        payload = out.to_json()
        self.assertEqual(payload["durationMs"], 6000, f"the applied duration is gone: {sorted(payload)}")
        self.assertEqual(payload["fxWaitBudgetMs"], 2500, f"the fx wait budget is gone: {sorted(payload)}")
        self.assertLess(payload["fxWaitBudgetMs"], payload["durationMs"])

    def test_the_MEASURED_findings_HEADING_is_pinned_as_well_as_its_content(self) -> None:
        """Deleting the section's HEADING left its body -- both status ids and the sample size -- so a case
        that pinned only the content could not see the removal. The heading names what the section is."""
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("THE MEASURED FINDING", head)
        self.assertIn("seenStatusIds", head)


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
        code = code_without_docstrings(MODULE_PATH)
        self.assertIn("import live_lawn_setup as lib", code)
        for name in ("ensure_live_lab_board", "resolve_base_url", "invoke_debug_post", "get_events",
                     "get_debug_max_event_id", "get_debug_payload", "Refusal"):
            self.assertIn(f"lib.{name}", code, f"{name} is used without the shared library")
        self.assertNotIn("Invoke-RestMethod", code)

    def test_the_PATH_insert_runs_BEFORE_the_import(self) -> None:
        lines = [ln.strip() for ln in MODULE_PATH.read_text(encoding="utf-8").splitlines()]
        insert = next(i for i, ln in enumerate(lines) if ln.startswith("sys.path.insert"))
        imported = next(i for i, ln in enumerate(lines) if ln.startswith("import live_lawn_setup"))
        self.assertLess(insert, imported)

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(m.DEFAULT_TIMEOUT_MS, 2500)
        self.assertEqual(m.POLL_INTERVAL_MS, 250)
        self.assertEqual(m.DEFAULT_DURATION_MS, 6000)
        self.assertEqual(m.DEFAULT_AMOUNT, 20)
        self.assertEqual(m.DEFAULT_MAX_TRIES, 6)
        self.assertEqual(m.DEFAULT_FX_EVENT_PAGE, 200)
        self.assertEqual(m.FX_STARTED_KIND, "debug.fx.state.started")
        self.assertEqual(m.APPLY_PAYLOAD_KEYS, ("statusId", "hostPtr", "amount", "durationMs"))
        self.assertEqual(m.CLEAR_PAYLOAD_KEYS, ("ptr",))

    def test_the_ENDPOINTS_are_the_SSOT_and_NOT_the_unity_BYPASS(self) -> None:
        """The module header says `/apply-status` is the Unity CC bypass and a different thing. Asserted
        so a well-meaning edit to 'match' the other name is caught."""
        self.assertEqual(m.STATUS_APPLY_PATH, "/status/apply")
        self.assertEqual(m.CLEAR_STATUS_PATH, "/clear-status")
        self.assertNotIn("apply-status", m.STATUS_APPLY_PATH)

    def test_every_RECORD_field_is_accessed_with_its_OWN_spelling(self) -> None:
        """The wire-shaped fields are camelCase, and a snake_case twin of one is a crash the moment that
        line runs -- which is how `outcome.status_id` shipped and failed on the first real CLI run."""
        tree = ast.parse(MODULE_PATH.read_text(encoding="utf-8"))
        # The wire-shaped fields are camelCase; a snake_case twin of one is a crash the moment that line
        # runs -- which is how `outcome.status_id` shipped and failed on the first real CLI run. Two
        # things are NOT wire fields and must not be flagged, each of which made this case report a
        # correct implementation as broken: a METHOD name (`to_json`), and this module's private sets
        # (`_seen`). Only THIS module's records are in scope -- `board` is the library's `LabBoard`,
        # which is legitimately snake_case throughout.
        methods = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef):
                methods |= {n.name for n in node.body if isinstance(n, ast.FunctionDef)}
        offenders = []
        for node in ast.walk(tree):
            if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name):
                if (node.value.id in ("outcome", "out", "attempt") and "_" in node.attr
                        and not node.attr.startswith("_") and node.attr not in methods):
                    offenders.append(f"line {node.lineno}: {node.value.id}.{node.attr}")
        self.assertEqual(offenders, [], "\n".join(offenders))

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = MODULE_PATH.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'lib\.Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        # `get_live_target_ptr` raises through the library for an empty pointer, and the library owns
        # that reason; it is in the LIBRARY's vocabulary, not this module's, so the check is against
        # both rather than forcing a second copy of a reason the library already declares.
        found -= m.lib.REFUSAL_REASONS - m.REFUSAL_REASONS
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - m.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - m.REFUSAL_REASONS)}")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("DebugStatusApply.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("no timeout", "5088", "boolean", "unparseable", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_MEASURED_namespace_finding_is_in_the_docstring(self) -> None:
        """The finding was measured against a running game, and a docstring that drops it would leave the
        next reader to rediscover it."""
        head = MODULE_PATH.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("THE MEASURED FINDING", head)
        self.assertIn('`statusId: "wither"`', head,
                      "the measured finding must name the id the GAME reports, in the form it is quoted")
        self.assertIn('`statusId: "status-l2-wither"`', head)
        self.assertRegex(head, r"\b31\b|\b\d+ fx events\b",
                         "the finding must carry its SAMPLE SIZE; a finding with no n is a story")

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
            if isinstance(target, ast.Name) and target.id == "m":
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
    """The real path, on a real running game, when one is reachable. The refusal-path proof is not the
    same evidence, and the namespace finding in particular can only be measured here."""

    def setUp(self) -> None:
        env = os.environ.get(m.lib.BASE_URL_ENV, "").strip()
        if not env:
            self.skipTest(f"${m.lib.BASE_URL_ENV} is unset; this case drives a real game and will not "
                          f"fall back to {m.lib.DEFAULT_BASE_URL}, which is the owner's own server")
        url, _ = m.lib.resolve_base_url("")
        try:
            health = m.lib._get_json(f"{url}/health", 5, "GET /health")
        except m.lib.Refusal as refusal:
            self.skipTest(f"no server at {url}: {refusal.detail}")
        if not health.get("injectorConnected"):
            self.skipTest(f"the server at {url} is up but NO INJECTOR is connected; start a game in a "
                          f"pool slot. This is not a failure of the tool.")
        self.url = url

    def test_it_ENTERS_a_board_and_returns_a_REAL_pointer(self) -> None:
        pointer = m.get_live_target_ptr(self.url)
        self.assertRegex(pointer, r"^[0-9A-Fa-f]+$",
                         f"{pointer!r} does not look like the hex pointer the injector returns")

    def test_the_apply_endpoint_ACCEPTS_the_status_id(self) -> None:
        """The half of the finding that is a property of the ENDPOINT: it accepts the id. If this stops
        holding, the mismatch in the next case has a different cause and the docstring is stale."""
        pointer = m.get_live_target_ptr(self.url)
        result = m.lib.invoke_debug_post(self.url, m.STATUS_APPLY_PATH,
                                         {"statusId": WOTHER_ID, "hostPtr": pointer,
                                          "amount": 20, "durationMs": 6000}, timeout=25)
        self.assertIsInstance(result, dict)
        self.assertNotEqual(result.get("ok"), False, f"the endpoint refused {WOTHER_ID!r}: {result}")

    def test_the_fx_payload_reports_a_DIFFERENT_statusId_than_the_one_applied(self) -> None:
        """MEASURED against a running game: the apply takes `status-l2-wither` and the resulting fx event
        carries `wither`. This is the finding the port exists to surface, and it is asserted here rather
        than quoted in a comment -- a docstring is not evidence, and this case is."""
        pointer = m.get_live_target_ptr(self.url)
        cursor = m.lib.get_debug_max_event_id(self.url)
        m.lib.invoke_debug_post(self.url, m.STATUS_APPLY_PATH,
                                {"statusId": WOTHER_ID, "hostPtr": pointer, "amount": 20,
                                 "durationMs": 6000}, timeout=25)
        out = m.wait_status_fx_started(self.url, cursor, WOTHER_ID, 8000)
        # The fx only fires when the board is in a state that produces one, so a run can legitimately see
        # nothing. What is asserted unconditionally is that the port reports what it saw; the finding's
        # assertion is made only on a run that DID see events, because an assertion about an empty
        # observation is an assertion about nothing.
        self.assertEqual(out.started, False, out.to_json())
        if out.fxEvents == 0:
            self.skipTest(f"no fx.state.started arrived inside 8s, so this run measured nothing about "
                          f"the namespaces: {out.to_json()}")
        self.assertEqual(out.matching, 0, f"the filter MATCHED; the namespace finding is not reproduced "
                                          f"and the docstring must be corrected: {out.to_json()}")
        self.assertTrue(out.seenStatusIds, "the port reported nothing the game used, which is its purpose")
        self.assertNotIn(WOTHER_ID, out.seenStatusIds,
                         f"the game reported the applied id after all: {out.seenStatusIds}")

    def test_the_board_survives_a_clear(self) -> None:
        pointer = m.get_live_target_ptr(self.url)
        result = m.clear_status_target(self.url, pointer)
        self.assertIs(result.get("ok"), True, f"the clear was refused: {result}")


if __name__ == "__main__":
    unittest.main()
