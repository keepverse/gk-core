"""Contract tests for `gk-core/scripts/probe_perf.py`.

THE SUMMARISING IS PURE and is where the substance is, so it is decided by PLANTED windows: the averages,
the maxima, the per-section figures and the `emits` totals. **A live run does not exercise `emits`** --
measured, every window's `emits` was an empty object -- so that path exists here and nowhere else, and a
case that only used a live run would have left it unproven.

THE EMPTY AGGREGATE IS THE DEFECT THE ORIGINAL HAD. `Measure-Object -Maximum` over an empty list returns
`$null`, which the original printed into a formatted table cell -- so a blank column read as a zero, and
the CONSUMER (`stress-test.ps1`) then evaluated `$gen2 -eq 0` against that `$null`. The port reports `None`
and NAMES it in `emptyAggregates`, and a case drives an aggregate with no data at all.

THE SCENARIO NAME BECOMES A FILENAME, and the original interpolated it into a path unchecked, so
`-Scenario "../../secrets"` wrote outside the baseline directory. The refusal names where it would have
landed.

THE WRITTEN BYTES ARE PINNED, because `Set-Content -Encoding UTF8` means a BOM under Windows PowerShell 5.1
and none under PowerShell 7, and a baseline document is read by other tools.

THE LIVE CASES SKIP, with a stated reason, when no injector is reachable, and write to a TEMPORARY
directory -- never into `docs/research/perf/`, which holds published baselines.
"""
from __future__ import annotations

import ast
import atexit
import contextlib
import datetime
import http.server
import importlib.util
import io
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROBE_PERF_SCRIPT", REPO / "scripts" / "probe_perf.py")).resolve()
SUITE = Path(__file__).resolve()
RUN_TIMEOUT = 300

# ── THE SUITE'S OWN STOP POINT ────────────────────────────────────────────────────────────────────────
# Every OTHER timeout in this file bounds one external call. This bounds the WHOLE suite, because 49 of
# the cases call `p.main` IN-PROCESS: there is no subprocess to kill, so a case that blocked -- a
# `urlopen` that never returns, a `serve()` handler that never answers -- would hang pytest with no
# output at all and no way to tell which case. That is the stop point's whole job: turn a silent hang
# into a named failure.
#
# MEASURED, not guessed: `--durations=0` puts the whole suite at 16.2s, of which 6.0s is the live
# collection and 2.1s is the deliberately-unreachable case. 120s is ~7x the measured worst case, which
# is room for a slow machine and not room for a hang.
SUITE_BUDGET_SEC = 120

# A shared temp dir for the cases that need an --out-dir, cleaned once at interpreter exit.
BUDGET_DIR = Path(tempfile.mkdtemp(prefix="probe-perf-budget-"))
atexit.register(lambda: __import__("shutil").rmtree(BUDGET_DIR, ignore_errors=True))


def _load():
    spec = importlib.util.spec_from_file_location("probe_perf", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["probe_perf"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


def window(tag, fps=60, frame_max=20, alloc=100, gen2=0, sections=None, emits=None):
    w = {"t": tag, "frames": {"fpsAvg": fps, "maxMs": frame_max}, "gc": {"allocKb": alloc, "gen2": gen2},
         "sections": sections if sections is not None else {}, "emits": emits}
    return w


def section(per_sec=60, total_ms=100, avg_us=1600, max_ms=4):
    return {"perSec": per_sec, "totalMs": total_ms, "avgUs": avg_us, "maxMs": max_ms}


class _Server:
    pages: list[list[dict]] = []
    reads: list[int] = []


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        index = min(len(_Server.reads), len(_Server.pages) - 1)
        _Server.reads.append(index)
        body = json.dumps({"items": _Server.pages[index]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def serve(*pages):
    _Server.pages = [list(pg) for pg in pages] or [[]]
    _Server.reads = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def run_cli(*args: str, collect: bool = True) -> tuple[int, dict, str]:
    """Drive the real `main`, in-process.

    `collect=False` advances the clock instead of sleeping: the collection window is a REAL wait in a real
    run, and nothing in these cases needs it to elapse. Five cases at `--duration-sec 1` was five seconds
    of the suite doing nothing -- and the flow is unchanged, because `_collect` is still called and still
    reads the same two pages of the same server.
    """
    out, err = io.StringIO(), io.StringIO()
    with mock.patch.object(p, "_collect", return_value=None) if not collect \
            else contextlib.nullcontext():
        with redirect_stdout(out), redirect_stderr(err):
            code = p.main(["--json", *args])
    try:
        payload = json.loads(out.getvalue())
    except json.JSONDecodeError:
        payload = {"__unparseable__": out.getvalue()[:200]}
    return code, payload, err.getvalue()


class TheSummarising(unittest.TestCase):
    """Pure, and decided by planted windows."""

    def test_the_FOUR_top_level_aggregates(self) -> None:
        """gen2 is pinned as a SUM against values where a SUM and a MAXIMUM DIFFER. The first draft of this
        case used gen2 0 and 2, on which `sum == max == 2.0` -- so the `gen2-becomes-max` mutation was an
        EQUIVALANT alternative and the case pinned nothing about which one it was."""
        s = p.summarise([window("a", fps=60, frame_max=20, alloc=100, gen2=3),
                         window("b", fps=40, frame_max=30, alloc=200, gen2=5)])
        self.assertEqual(s["fpsAvg"], 50.0)
        self.assertEqual(s["frameMax"], 30.0, "frameMax is a MAXIMUM, not an average")
        self.assertEqual(s["allocKb"], 150.0)
        self.assertEqual(s["gen2"], 8.0, "gen2 is a SUM: 3+5=8, and a maximum would say 5")

    def test_gen2_is_a_SUM_not_a_maximum_or_a_mean(self) -> None:
        """The same distinction, stated so a fixture change cannot quietly make it equivalent again."""
        s = p.summarise([window("a", gen2=3), window("b", gen2=5)])
        self.assertEqual(s["gen2"], 8.0)
        self.assertNotEqual(s["gen2"], 5.0, "a maximum is not a sum")
        self.assertNotEqual(s["gen2"], 4.0, "a mean is not a sum")

    def test_an_aggregate_over_NO_data_is_NULL_and_NAMED_not_zero(self) -> None:
        """The original printed `$null` into a table cell, so a blank column read as a zero -- and the
        consumer then evaluated `$gen2 -eq 0` against it."""
        s = p.summarise([{"t": "x"}])
        self.assertIsNone(s["fpsAvg"])
        self.assertIsNone(s["frameMax"])
        self.assertIsNone(s["gen2"])
        self.assertEqual(s["emptyAggregates"], ["allocKb", "fpsAvg", "frameMax", "gen2"])

    def test_a_MISSING_or_MALFORMED_sub_object_does_not_RAISE_and_is_named(self) -> None:
        """`x or {}` is not enough: it KEEPS a non-empty string, and a string has no `.get`, so a window
        whose `sections` arrived as text crashed the whole summary. The data comes from a server, and a
        report that raises on an unexpected shape reports nothing at all."""
        for windows in ([{}], [{"frames": None, "gc": None}],
                        [{"sections": "not a dict"}], [{"frames": [], "gc": 7, "emits": "x"}]):
            with self.subTest(windows=windows):
                s = p.summarise(windows)
                self.assertIsInstance(s["emptyAggregates"], list)
                self.assertIn("fpsAvg", s["emptyAggregates"])

    def test_a_malformed_sub_object_does_not_LOSE_a_GOOD_sibling(self) -> None:
        """One row's `frames` arrives as text; the other's is good. The good half must survive, and the
        wholly-absent side is still NAMED. `allocKb` is not in `emptyAggregates` because one row carried
        it -- the name list is for an aggregate over NO data, not for a partial one."""
        s = p.summarise([{"t": "a", "frames": "junk", "gc": {"allocKb": 5}},
                         {"t": "b", "frames": {"fpsAvg": 30}, "gc": "junk"}])
        self.assertEqual(s["allocKb"], 5.0, "the good gc row must survive the malformed one")
        self.assertEqual(s["fpsAvg"], 30.0)
        self.assertEqual(s["emptyAggregates"], ["frameMax", "gen2"],
                         "only the aggregates with no data at all are named")

    def test_the_SECTION_figures(self) -> None:
        s = p.summarise([window("a", sections={"loop.tick": section(60, 100, 1600, 4)}),
                         window("b", sections={"loop.tick": section(30, 200, 2000, 9)})])
        entry = s["sections"]["loop.tick"]
        self.assertEqual(entry["perSec"], 45.0)
        self.assertEqual(entry["totalMs"], 150.0)
        self.assertEqual(entry["avgUs"], 1800.0)
        self.assertEqual(entry["maxMs"], 9.0, "a section maxMs is a MAXIMUM")
        self.assertEqual(entry["windows"], 2, "the section's own window count is reported")

    def test_a_SECTION_field_is_averaged_over_the_ROWS_that_carry_it(self) -> None:
        """`Measure-Object -Average` over `@($sec | ForEach-Object { $_.totalMs })` ignores the nulls, so
        the original averaged a PRESENT field over the rows that had it. The port keeps that, and the
        section's `windows` is the denominator, so a partial average is not a claim about all of them."""
        s = p.summarise([window("a", sections={"loop.tick": {"perSec": 60, "maxMs": 4}}),
                         window("b", sections={"loop.tick": section(30, 200, 2000, 9)})])
        entry = s["sections"]["loop.tick"]
        self.assertEqual(entry["windows"], 2, "both rows were PRESENT")
        self.assertEqual(entry["perSec"], 45.0, "two rows carry perSec, so both are averaged")
        self.assertEqual(entry["maxMs"], 9.0, "maxMs is a maximum over the rows that carry it")
        self.assertEqual(entry["totalMs"], 200.0, "one row carries totalMs, so the mean is that row's value")
        self.assertNotIn("sections.loop.tick.totalMs", s["emptyAggregates"],
                         "a field SOME row carried is a partial average, not an aggregate over no data")

    def test_a_SECTION_field_that_NO_row_carries_is_NULL_and_NAMED(self) -> None:
        """The half of the same property the original got wrong: a wholly-absent field was a `$null` in a
        table cell, which read as a zero."""
        s = p.summarise([window("a", sections={"loop.tick": {"perSec": 60}}),
                         window("b", sections={"loop.tick": {"perSec": 30}})])
        self.assertIn("sections.loop.tick.totalMs", s["emptyAggregates"])
        self.assertIn("sections.loop.tick.maxMs", s["emptyAggregates"])

    def test_an_ABSENT_section_is_OMITTED_rather_than_averaged_as_zero(self) -> None:
        """The original `continue`d on a section with no rows, so an absent section printed nothing. An
        average over zero rows would print 0.0, which is a measurement claim about a section that never
        ran."""
        s = p.summarise([window("a", sections={"loop.tick": section()}),
                         window("b", sections={"loop.tick": section()})])
        self.assertEqual(list(s["sections"]), ["loop.tick"])
        self.assertNotIn("vfx.tick", s["sections"])

    def test_the_SECTION_names_are_a_CLOSED_VOCABULARY_and_match_the_original(self) -> None:
        self.assertEqual(len(p.SECTION_NAMES), 20, "the original enumerates 20 section names")
        for name in ("loop.tick", "drain.tick", "vfx.tick", "takeDamage.prefix",
                     "effect.onCapture", "pump.main", "funnel.flush"):
            self.assertIn(name, p.SECTION_NAMES)
        self.assertEqual(len(set(p.SECTION_NAMES)), len(p.SECTION_NAMES), "a duplicate in the vocabulary")

    def test_emits_are_TOTALLED_across_windows_and_sorted(self) -> None:
        """A live run leaves `emits` EMPTY on every window -- measured -- so this path is unproven by any
        live run and this is where it is decided."""
        s = p.summarise([window("a", emits={"stat.applied": 10, "combat.hit": 3}),
                         window("b", emits={"stat.applied": 5, "combat.hit": 7})])
        self.assertEqual(s["emits"], {"combat.hit": 10.0, "stat.applied": 15.0})
        self.assertEqual(list(s["emits"]), sorted(s["emits"]), "emits are reported sorted")

    def test_an_emits_that_is_absent_or_NOT_a_dict_is_ignored(self) -> None:
        for emits in (None, [], "not a dict", {"a": "not a number", "b": True}):
            with self.subTest(emits=emits):
                s = p.summarise([window("a", emits=emits), window("b", emits={"real": 2})])
                self.assertEqual(s["emits"], {"real": 2.0},
                                 "a non-numeric emit must be dropped, not summed as text")

    def test_a_BOOLEAN_is_never_counted_as_a_NUMBER(self) -> None:
        """`isinstance(True, int)` is true in Python, so a bool would silently become 1.0 in a perf total."""
        self.assertEqual(p.average([True, False, 10]), 10.0,
                         "a bool must be DROPPED, not counted as 1/0 -- 10.0, not 11/3")
        self.assertIsNone(p.average([True, False]))
        self.assertIsNone(p.maximum([True]))
        self.assertIsNone(p.total([True, False]))

    def test_NON_NUMERIC_values_are_dropped_rather_than_poisoning_the_average(self) -> None:
        self.assertEqual(p.average([10, "x", None, 20]), 15.0)
        self.assertIsNone(p.maximum(["x", None]))
        self.assertIsNone(p.total([None, {}]))


class TheScenarioNameBecomesAFilename(unittest.TestCase):
    """The original interpolated it into a path unchecked, so a name with a separator wrote OUTSIDE the
    baseline directory."""

    def test_ORDINARY_names_are_accepted(self) -> None:
        for name in ("b2-heavy-normal", "stress-40p-150z", "a", "A.b_c-1", "x" * 64):
            with self.subTest(name=name):
                self.assertIsNotNone(p.SCENARIO_PATTERN.match(name), name)

    def test_HOSTILE_names_are_REFUSED_by_name(self) -> None:
        for name in ("../../secrets", "a/b", "a\\b", "..", "a b", "", ".hidden", "x" * 65, "a:b", "a*b"):
            with self.subTest(name=name):
                self.assertIsNone(p.SCENARIO_PATTERN.match(name),
                                  f"{name!r} would be interpolated into a path")

    def test_a_HOSTILE_name_REFUSES_before_any_READ_and_names_where_it_would_land(self) -> None:
        with mock.patch.object(p, "read_windows") as read:
            code, payload, _ = run_cli("--scenario", "../../secrets")
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INVALID-SCENARIO-NAME")
        self.assertIn("_baseline-", payload["detail"],
                      "the refusal must name the filename the name would have become")
        self.assertFalse(read.called, "a hostile name reached the network")

    def test_a_NON_POSITIVE_DURATION_REFUSES_before_any_READ(self) -> None:
        for value in ("0", "-1"):
            with self.subTest(value=value):
                with mock.patch.object(p, "read_windows") as read:
                    code, payload, _ = run_cli("--scenario", "ok", "--duration-sec", value)
                self.assertEqual(payload["reason"], "INVALID-DURATION")
                self.assertFalse(read.called)


class TheReadsAreBounded(unittest.TestCase):
    """Neither of the original's `Invoke-RestMethod` calls had a `-TimeoutSec`, and the SECOND one decides
    whether the run produced anything."""

    def test_a_read_that_never_answers_is_a_NAMED_refusal_not_a_HANG(self) -> None:
        with mock.patch.object(p.urllib.request, "urlopen", side_effect=TimeoutError()):
            with self.assertRaises(p.Refusal) as caught:
                p.read_windows("http://x")
        self.assertEqual(caught.exception.reason, "SERVER-UNREACHABLE")
        self.assertIn("unbounded read hangs", caught.exception.detail,
                      "the refusal must say what the bound is FOR")

    def test_the_read_carries_a_TIMEOUT(self) -> None:
        seen: list = []

        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def read(self):
                return b'{"items": []}'

        def capture(request, timeout=None):
            seen.append(timeout)
            return Response()

        with mock.patch.object(p.urllib.request, "urlopen", capture):
            p.read_windows("http://x")
        self.assertTrue(seen)
        self.assertIsNotNone(seen[0], "a perf read with no timeout")

    def test_a_body_with_NO_items_array_is_a_NAMED_refusal(self) -> None:
        class Response:
            def __init__(self, raw):
                self.raw = raw

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def read(self):
                return self.raw

        for raw, label in ((b'{"other": 1}', "no items key"), (b"not json", "not JSON"),
                           (b"[]", "an array, not an object")):
            with self.subTest(label=label):
                with mock.patch.object(p.urllib.request, "urlopen",
                                       lambda r, timeout=None: Response(raw)):
                    with self.assertRaises(p.Refusal) as caught:
                        p.read_windows("http://x")
                self.assertEqual(caught.exception.reason, "PERF-READ-FAILED")

    def test_an_HTTP_error_is_a_NAMED_refusal_with_its_STATUS(self) -> None:
        import urllib.error

        def boom(request, timeout=None):
            raise urllib.error.HTTPError("http://x", 503, "Service Unavailable", {}, None)

        with mock.patch.object(p.urllib.request, "urlopen", boom):
            with self.assertRaises(p.Refusal) as caught:
                p.read_windows("http://x")
        self.assertEqual(caught.exception.reason, "PERF-READ-FAILED")
        self.assertIn("503", caught.exception.detail)


class WhatCountsAsNew(unittest.TestCase):
    def test_only_windows_whose_t_is_NEW_are_kept(self) -> None:
        before = [window("a"), window("b")]
        after = [window("a"), window("b"), window("c"), window("d")]
        seen = {str(w.get("t")) for w in before}
        self.assertEqual([w["t"] for w in after if str(w.get("t")) not in seen], ["c", "d"])

    def test_a_WINDOW_that_SHARES_a_timestamp_with_an_older_one_is_DROPPED(self) -> None:
        """The discriminator is the timestamp, so a collision silently loses a window. Measured: 240
        windows, 240 distinct `t` on this server -- so it does not collide HERE, which is exactly why the
        case below and the live uniqueness case both exist rather than the behaviour being assumed."""
        before = [window("a")]
        after = [window("a"), window("a"), window("b")]
        seen = {str(w.get("t")) for w in before}
        kept = [w for w in after if str(w.get("t")) not in seen]
        self.assertEqual(len(kept), 1, "the colliding window is dropped by the discriminator")

    def test_the_DISCIMINATOR_is_the_TIMESTAMP_and_nothing_else(self) -> None:
        before = [{"t": "a", "frames": {"fpsAvg": 1}}]
        after = [{"t": "a", "frames": {"fpsAvg": 999}}]
        seen = {str(w.get("t")) for w in before}
        self.assertEqual([w for w in after if str(w.get("t")) not in seen], [],
                         "a window with the same t but different content is NOT new")

    def test_a_window_with_NO_t_is_never_NEW(self) -> None:
        seen = {"None"}
        self.assertEqual([w for w in [{"noT": 1}] if str(w.get("t")) not in seen], [])


class TheWrittenBytes(unittest.TestCase):
    """`Set-Content -Encoding UTF8` means a BOM under Windows PowerShell 5.1 and none under PowerShell 7, and
    a baseline document is read by other tools."""

    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp(prefix="probe-perf-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.dir, ignore_errors=True))

    def test_the_document_is_UTF8_with_NO_BOM_and_LF_ENDED(self) -> None:
        target = p.baseline_path("x", self.dir)
        written = p.write_baseline({"scenario": "x", "windows": []}, target)
        raw = target.read_bytes()
        self.assertNotEqual(raw[:3], bytes([0xEF, 0xBB, 0xBF]), "a BOM was written")
        self.assertEqual(raw.count(bytes([13])), 0, "a CR was written")
        self.assertTrue(raw.endswith(b"\n"), "the file must end with a newline")
        self.assertEqual(len(raw), written)

    def test_it_round_trips_through_json(self) -> None:
        target = p.baseline_path("x", self.dir)
        document = {"scenario": "x", "baseUrl": "http://y", "durationSec": 3, "capturedUtc": "now",
                    "windows": [window("a")]}
        p.write_baseline(document, target)
        self.assertEqual(json.loads(target.read_text(encoding="utf-8")), document)

    def test_the_KEY_ORDER_is_the_PUBLISHED_shape(self) -> None:
        """Pins that `write_baseline` does not REORDER what it is given. It is deliberately NOT the case
        that pins the published order -- that is `test_the_MAIN_flow_writes_the_PUBLISHED_KEY_ORDER`, and
        the two had been conflated: a case that builds its own dict and hands it to the writer cannot see
        the order `main()` assembles, so `key-order-sorted` survived it."""
        target = p.baseline_path("x", self.dir)
        p.write_baseline({"zebra": 1, "apple": 2}, target)
        self.assertEqual(list(json.loads(target.read_text(encoding="utf-8"))), ["zebra", "apple"],
                         "write_baseline must not sort the keys it is given")

    def test_an_UNWRITABLE_target_is_a_NAMED_refusal(self) -> None:
        # A file where a directory belongs. A null byte in the path would be rejected by Python before the
        # OS ever saw it, so it would test argparse, not the writer.
        blocker = self.dir / "a-file"
        blocker.write_text("not a directory", encoding="utf-8")
        with self.assertRaises(p.Refusal) as caught:
            p.write_baseline({}, blocker / "nested" / "_baseline-x.json")
        self.assertEqual(caught.exception.reason, "OUTPUT-UNWRITABLE")


class TheWholeFlow(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp(prefix="probe-perf-flow-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.dir, ignore_errors=True))

    def test_a_RUN_with_NEW_windows_writes_them_and_reports_OK(self) -> None:
        server, base = serve([window("a")], [window("a"), window("b", fps=40)])
        try:
            code, payload, _ = run_cli("--base-url", base, "--scenario", "flow",
                                      "--duration-sec", "1", "--out-dir", str(self.dir),
                                      collect=False)
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["windows"], 1)
        self.assertEqual(payload["windowsBefore"], 1)
        self.assertEqual(payload["windowsAfter"], 2)
        document = json.loads((self.dir / "_baseline-flow.json").read_text(encoding="utf-8"))
        self.assertEqual([w["t"] for w in document["windows"]], ["b"],
                         "the document must hold the NEW windows only")

    def test_a_RUN_with_NO_NEW_windows_is_a_NAMED_refusal(self) -> None:
        server, base = serve([window("a")], [window("a")])
        try:
            code, payload, _ = run_cli("--base-url", base, "--scenario", "none",
                                      "--duration-sec", "1", "--out-dir", str(self.dir),
                                      collect=False)
        finally:
            server.shutdown()
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "NO-NEW-WINDOWS")
        self.assertIn("injector connected", payload["detail"])
        self.assertFalse((self.dir / "_baseline-none.json").exists(),
                         "a run with nothing to report wrote a document anyway")

    def test_the_output_PATH_is_reported_BEFORE_the_wait(self) -> None:
        """A run that will write somewhere unexpected should say so first, not after a minute."""
        server, base = serve([window("a")], [window("a"), window("b")])
        try:
            code, payload, err = run_cli("--base-url", base, "--scenario", "early",
                                         "--duration-sec", "1", "--out-dir", str(self.dir),
                                         collect=False)
        finally:
            server.shutdown()
        self.assertEqual(code, 0)
        self.assertIn("_baseline-early.json", payload["baselinePath"])
        self.assertIn("_baseline-early.json", err, "the path must be announced on stderr before collecting")

    def test_the_BASE_URL_source_is_reported_and_RECORDED_in_the_document(self) -> None:
        server, base = serve([window("a")], [window("a"), window("b")])
        try:
            _, payload, _ = run_cli("--base-url", base, "--scenario", "src",
                                    "--duration-sec", "1", "--out-dir", str(self.dir),
                                    collect=False)
        finally:
            server.shutdown()
        self.assertEqual(payload["baseUrlSource"], "explicit")
        document = json.loads((self.dir / "_baseline-src.json").read_text(encoding="utf-8"))
        self.assertEqual(document["baseUrl"], base,
                         "a baseline must say which server produced it")

    def test_the_MAIN_flow_writes_the_PUBLISHED_KEY_ORDER(self) -> None:
        """The order the CONSUMER reads, decided through the real flow rather than through a hand-built
        dict. `stress-test.ps1` and the published baselines both index this document, so its key order is
        part of the contract and not an accident of dict construction -- and the case that was supposed to
        pin it built its own dict, so it never saw the order `main()` assembles."""
        server, base = serve([window("a")], [window("a"), window("b")])
        try:
            code, payload, _ = run_cli("--base-url", base, "--scenario", "order",
                                      "--duration-sec", "1", "--out-dir", str(self.dir),
                                      collect=False)
        finally:
            server.shutdown()
        self.assertEqual(code, 0, f"the run refused: {payload.get('reason')}")
        document = json.loads((self.dir / "_baseline-order.json").read_text(encoding="utf-8"))
        self.assertEqual(list(document),
                         ["scenario", "baseUrl", "durationSec", "capturedUtc", "windows"])

    def test_an_unreachable_server_is_a_NAMED_refusal_before_any_wait(self) -> None:
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        started = time.monotonic()
        code, payload, _ = run_cli("--base-url", f"http://127.0.0.1:{port}", "--scenario", "dead",
                                  "--duration-sec", "30", "--out-dir", str(self.dir))
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "SERVER-UNREACHABLE")
        self.assertLess(time.monotonic() - started, 10,
                        "it waited the full duration before reporting an unreachable server")


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
    def test_it_does_NOT_reach_for_the_SHARED_library(self) -> None:
        """This script talks to ONE endpoint the shared library does not wrap, and it WRITES a file, which
        the library does not do. A forced import would be a dependency with no purpose -- stated here so
        the absence reads as a decision rather than an oversight."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("import live_lawn_setup", source)
        self.assertIn("urllib.request", code_without_docstrings(SCRIPT))

    def test_the_OUT_DIR_is_resolved_from_the_SCRIPT_not_the_CWD(self) -> None:
        """The rule is that OUT_DIR is derived from the SCRIPT, not the working directory.

        The expectation is built FROM `SCRIPT`, not from the repository: when this suite runs against a
        MUTANT the tool is written to a temporary directory, and asserting the repo path would make this
        case fail for every mutant regardless of what was changed to it. A case that kills everything is
        not a case -- it hides the holes it is standing next to. Derived from `SCRIPT` it holds for the
        real tool and for every mutant, and still pins the rule.
        """
        code = code_without_docstrings(SCRIPT)
        self.assertIn("Path(__file__).resolve().parent.parent", code)
        self.assertEqual(p.OUT_DIR, SCRIPT.resolve().parent.parent / "docs" / "research" / "perf")
        for cwd_derivation in ("Path.cwd()", "os.getcwd()"):
            self.assertNotIn(cwd_derivation, code,
                                 f"OUT_DIR must not follow the working directory ({cwd_derivation})")

    def test_the_REPO_tool_publishes_its_baselines_beside_these(self) -> None:
        """The compatibility target only exists for the tool AS COMMITTED. Skipped for a mutant, whose
        location says nothing about where the real tool writes."""
        if SCRIPT != (REPO / "scripts" / "probe_perf.py").resolve():
            self.skipTest(f"running against a relocated tool at {SCRIPT}, not the committed one")
        published = sorted(p.OUT_DIR.glob("_baseline-*.json"))
        self.assertTrue(published, f"{p.OUT_DIR} holds no published baseline to be compatible with")

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.DEFAULT_DURATION_SEC, 60)
        self.assertEqual(p.WINDOW_LIMIT, 240)
        self.assertEqual(p.READ_TIMEOUT, 15)
        self.assertEqual(p.SUMMARY_ROUND, 2)
        self.assertEqual(p.BASELINE_PREFIX, "_baseline-")
        self.assertEqual(p.DEFAULT_BASE_URL, "http://127.0.0.1:5088")


class TheRunIsBounded(unittest.TestCase):
    """THE STOP POINT OF THE TOOL ITSELF.

    Three bounds, and they compose into a total: the wait (`--duration-sec`, with a ceiling), the two
    reads (`READ_TIMEOUT` each), and therefore `total_budget`. The original had a wait with no ceiling
    and two `Invoke-RestMethod` calls with no `-TimeoutSec` at all, so "how long can this take" had no
    answer except the socket's.
    """

    def test_the_BUDGET_is_the_WAIT_plus_both_READS(self) -> None:
        self.assertEqual(p.total_budget(60), 60 + 2 * p.READ_TIMEOUT)
        self.assertEqual(p.total_budget(1, read_timeout=5, reads=2), 11)

    def test_the_BUDGET_is_REPORTED_in_the_envelope(self) -> None:
        server, base = serve([window("a")], [window("a"), window("b")])
        try:
            _, payload, _ = run_cli("--base-url", base, "--scenario", "b",
                                    "--duration-sec", "1", "--out-dir", str(BUDGET_DIR),
                                    collect=False)
        finally:
            server.shutdown()
        self.assertEqual(payload["maxTotalSec"], 1 + 2 * p.READ_TIMEOUT)
        self.assertEqual(payload["readTimeoutSec"], p.READ_TIMEOUT,
                         "the per-read bound is reported, not just the sum")

    def test_a_DURATION_over_the_CEILING_is_REFUSED_by_name(self) -> None:
        with mock.patch.object(p, "read_windows") as read:
            code, payload, _ = run_cli("--scenario", "forever", "--duration-sec",
                                      str(p.MAX_DURATION_SEC + 1))
        self.assertEqual(code, p.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "DURATION-TOO-LONG")
        self.assertIn(str(p.MAX_DURATION_SEC), payload["detail"],
                      "the refusal must name the ceiling so the fix is obvious")
        self.assertFalse(read.called, "a run that will be refused reached the network first")

    def test_the_CEILING_refusal_SAYS_WHAT_TO_DO_and_names_the_KNOB(self) -> None:
        """The first case only asserted that the number appears somewhere in the detail, and a mutant that
        DELETED the actionable sentence survived it: the ceiling was still named in the first clause, so
        "the fix is obvious" was never actually pinned. This pins the two halves separately -- the ceiling
        is named, and the CONSTANT that a reader would edit is named, because a refusal that says "too
        long" without saying what to change is a dead end."""
        with mock.patch.object(p, "read_windows"):
            _, payload, _ = run_cli("--scenario", "forever", "--duration-sec",
                                    str(p.MAX_DURATION_SEC + 1))
        detail = payload["detail"]
        self.assertIn(str(p.MAX_DURATION_SEC), detail, "the ceiling value is named")
        self.assertIn("MAX_DURATION_SEC", detail,
                      "the refusal must name the CONSTANT to change, not just its value")
        self.assertIn("Raise", detail, "the refusal must say what to do about it")
        self.assertIn("scripts/probe_perf.py", detail,
                      "the refusal must name the file that holds the knob")

    def test_the_CEILING_is_a_ceiling_not_a_DEFAULT(self) -> None:
        self.assertGreater(p.MAX_DURATION_SEC, p.DEFAULT_DURATION_SEC,
                           "the ceiling must not reject the documented default")
        self.assertLessEqual(p.MAX_DURATION_SEC, 3600,
                             "over an hour is not a stop point, it is a formality")

    def test_the_WAIT_is_a_SEAM_and_does_not_sleep_when_replaced(self) -> None:
        """The offline cases advance the clock through this. It is the difference between a 16s suite and
        a 5s one, and it exists so the collection window is a real wait in a real run and not in a test."""
        self.assertTrue(callable(p._collect))
        slept: list = []
        original_sleep = time.sleep
        try:
            time.sleep = lambda s: slept.append(s)  # type: ignore[assignment]
            started = time.monotonic()
            p._collect(0.05)
            elapsed = time.monotonic() - started
        finally:
            time.sleep = original_sleep  # type: ignore[assignment]
        self.assertTrue(slept, "_collect must actually wait when nothing has replaced the clock")
        self.assertLess(elapsed, 1.0, "a 50ms wait took longer than the whole rest of the suite")

    def test_the_BASE_URL_is_read_from_the_ENVIRONMENT_and_the_source_is_named(self) -> None:
        with mock.patch.dict(os.environ, {p.BASE_URL_ENV: "http://127.0.0.1:5102"}, clear=False):
            url, source = p.resolve_base_url("")
        self.assertEqual(url, "http://127.0.0.1:5102")
        self.assertEqual(source, f"${p.BASE_URL_ENV}")
        with mock.patch.dict(os.environ, {}, clear=True):
            url, source = p.resolve_base_url("")
        self.assertEqual(url, p.DEFAULT_BASE_URL)
        self.assertIn("default", source)
        with self.assertRaises(p.Refusal) as caught:
            p.resolve_base_url("not-a-url")
        self.assertEqual(caught.exception.reason, "INVALID-BASE-URL")

    def test_a_trailing_SLASH_is_TRIMMED(self) -> None:
        self.assertEqual(p.resolve_base_url("http://127.0.0.1:5103/")[0], "http://127.0.0.1:5103")

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - p.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - p.REFUSAL_REASONS)}")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("probe-perf.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("timeout", "filename", "bom", "5088", "null"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        """A port that still accepted `-Scenario` would be a wrapper with a new name. Argparse reports the
        MISSING REQUIRED option rather than "unrecognized", because the old flag does not satisfy
        `--scenario` -- so the property to assert is that the run FAILS and the new option is still
        required, not that a particular error string appears."""
        for flag in ("-Scenario", "-DurationSec", "-BaseUrl"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                said = (proc.stdout + proc.stderr)
                self.assertNotEqual(proc.returncode, 0, f"{flag} was accepted as an argument")
                self.assertIn("--scenario", said, f"{flag} satisfied the required option")

    def test_it_REQUIRES_a_scenario(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--json"], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("required", (proc.stdout + proc.stderr).lower())

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
            if isinstance(target, ast.Name) and target.id == "p":
                continue
            if isinstance(target, ast.Attribute) and target.attr in ("urllib", "read_windows"):
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_no_case_starts_a_SERVER_it_cannot_STOP(self) -> None:
        """Every `serve()` in this file binds a socket, so a case that starts one and does not shut it
        down leaks a listener for the rest of the run.

        `StopPoint` is excluded because it MANAGES ITS OWN THREADS and is held to that by
        `test_a_case_that_BLOCKS_...`, which joins the worker in a `finally` and asserts it is dead. The
        exclusion is a narrower rule, not a waived one -- a case that started a real server there would
        still be caught by the dedicated case.
        """
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned = []
        for cls in (n for n in ast.walk(tree) if isinstance(n, ast.ClassDef)):
            if cls.name == "StopPoint":
                continue
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
    """The real read, against a real running game. The run WRITES to a temporary directory: a live case
    that wrote into `docs/research/perf/` would add a baseline to a directory of PUBLISHED ones.

    THE PRECONDITION IS LIVENESS, NOT REACHABILITY, and the difference cost a real failure. The first
    version of `setUp` asked only whether `/api/perf/recent` ANSWERED -- and a server with a stale ring
    answers perfectly, because the ring still holds the last 240 windows. MEASURED: with the game shut,
    all three slots answered, `injectorConnected=False` on every one, and the newest window was 321s old.
    So the case got past its precondition, slept a real 6 seconds, and failed on `NO-NEW-WINDOWS` -- six
    seconds spent discovering a fact a 2-second poll could have established, and a FAILED case where the
    honest verdict was "the game is not running".

    The precondition therefore polls for an ARRIVING window, bounded, and SKIPS with the reason when
    none arrives. A live case that cannot observe its subject skips; it does not assert about nothing.
    """

    LIVE_POLL_SEC = 12
    LIVE_POLL_INTERVAL = 2

    # Asked ONCE per class. The first draft put the liveness poll in `setUp`, and with four cases that was
    # four 12-second polls -- 48 seconds of the suite waiting for a game that is not running, which is
    # exactly the "it runs forever" failure the stop point exists to prevent. A precondition is asked once.
    url: str = ""
    dir: Path
    _skip: str = ""

    @classmethod
    def setUpClass(cls) -> None:
        env = os.environ.get(p.BASE_URL_ENV, "").strip()
        if not env:
            cls._skip = (f"${p.BASE_URL_ENV} is unset; this class reads a real server and will not fall "
                         f"back to {p.DEFAULT_BASE_URL}, which is the owner's own server")
            return
        try:
            url, _ = p.resolve_base_url("")
        except p.Refusal as refusal:
            cls._skip = f"the configured base url is unusable: {refusal.detail}"
            return
        try:
            first = p.read_windows(url, timeout=5)
        except p.Refusal as refusal:
            cls._skip = f"no perf endpoint at {url}: {refusal.detail}"
            return
        if not first:
            cls._skip = f"{url} answers /api/perf/recent with NO windows at all"
            return
        cls.url = url
        cls.dir = Path(tempfile.mkdtemp(prefix="probe-perf-live-"))
        cls.addClassCleanup(lambda: __import__("shutil").rmtree(cls.dir, ignore_errors=True))
        if cls._await_a_new_window():
            return
        age = cls._newest_window_age()
        cls._skip = (
            f"no NEW perf window arrived in {cls.LIVE_POLL_SEC}s at {url} (the newest is {age}); the "
            f"game is not producing windows, so these cases would assert about a stopped game. The probe "
            f"tool is exercised offline, and NO-NEW-WINDOWS is the CORRECT answer here -- a failed case "
            f"would misreport a stopped game as a broken tool")

    @classmethod
    def _await_a_new_window(cls) -> bool:
        before = {str(w.get("t")) for w in p.read_windows(cls.url, timeout=5)}
        deadline = time.monotonic() + cls.LIVE_POLL_SEC
        while time.monotonic() < deadline:
            time.sleep(min(cls.LIVE_POLL_INTERVAL, max(0.0, deadline - time.monotonic())))
            if {str(w.get("t")) for w in p.read_windows(cls.url, timeout=5)} - before:
                return True
        return False

    @classmethod
    def _newest_window_age(cls) -> str:
        windows = p.read_windows(cls.url, timeout=5)
        if not windows:
            return "absent"
        try:
            newest = max(datetime.datetime.fromisoformat(str(w.get("t")).replace("Z", "+00:00"))
                         for w in windows if w.get("t"))
        except (TypeError, ValueError):
            return "unparseable"
        delta = (datetime.datetime.now(datetime.timezone.utc) - newest).total_seconds()
        return f"{delta:.0f}s old"

    def setUp(self) -> None:
        if self._skip:
            self.skipTest(self._skip)

    def test_the_LIVE_precondition_is_LIVENESS_and_its_poll_is_BOUNDED(self) -> None:
        """Reaching this line MEANS the liveness poll found a new window within LIVE_POLL_SEC, so the
        budget is asserted against the measurement rather than declared."""
        self.assertGreater(self.LIVE_POLL_SEC, 0)
        self.assertLessEqual(self.LIVE_POLL_SEC, 60,
                             "a liveness poll over a minute is not a precondition, it is a wait")
        self.assertLess(self.LIVE_POLL_INTERVAL, self.LIVE_POLL_SEC)
        self.assertRegex(self._newest_window_age(), r"^\d+s old$",
                         "the newest window must be parseable enough to state an age")

    def test_a_LIVE_read_returns_WINDOWS_with_the_shape_the_summary_expects(self) -> None:
        windows = p.read_windows(self.url)
        self.assertGreater(len(windows), 0, "the live server reported no perf windows")
        first = windows[-1]
        for key in ("t", "frames", "gc", "sections"):
            self.assertIn(key, first, f"a live window has no {key!r}: {sorted(first)}")
        self.assertIn("fpsAvg", first["frames"])
        self.assertIn("gen2", first["gc"])

    def test_the_T_DISCRIMINATOR_is_measured_to_be_UNIQUE_here(self) -> None:
        """MEASURED, and stated as a measurement rather than an assumption: if `t` collided on this server,
        a collision would silently lose a window and the finding would change."""
        windows = p.read_windows(self.url)
        stamps = [str(w.get("t")) for w in windows]
        duplicates = len(stamps) - len(set(stamps))
        print(f"    live: {len(windows)} window(s), {duplicates} duplicate t value(s)")
        self.assertEqual(duplicates, 0,
                         f"{duplicates} live window(s) share a `t`, so the discriminator loses windows "
                         f"and the collision count is not informational")

    def test_a_LIVE_run_captures_windows_and_writes_a_document(self) -> None:
        code, payload, _ = run_cli("--base-url", self.url, "--scenario", "ps1banlive",
                                   "--duration-sec", "6", "--out-dir", str(self.dir))
        target = self.dir / "_baseline-ps1banlive.json"
        self.assertTrue(target.is_file(), f"no document was written: {payload}")
        document = json.loads(target.read_text(encoding="utf-8"))
        self.assertGreater(len(document["windows"]), 0,
                           "a live run captured no windows, so this measured nothing")
        self.assertEqual(document["scenario"], "ps1banlive")
        bom = target.read_bytes()[:3]
        self.assertNotEqual(bom, bytes([0xEF, 0xBB, 0xBF]), f"the live document carries a BOM: {bom!r}")
        self.assertEqual(code, 0, f"the live run refused: {payload.get('reason')} {payload.get('detail')}")


class StopPoint(unittest.TestCase):
    """The suite's stop point, and the cases that keep it honest.

    49 of the cases here call `p.main` IN-PROCESS -- there is no subprocess to kill and no `RUN_TIMEOUT`
    standing between a blocked `urlopen` and a hung pytest. This is the bound that turns that hang into a
    named failure instead of a machine that sits there.
    """

    def test_the_WHOLE_SUITE_is_bounded_and_the_BUDGET_is_a_MEASUREMENT(self) -> None:
        """The budget is a reading, so it is re-read here rather than trusted: a budget nobody checks is a
        comment. This asserts the CONSTANT is consistent with what the suite is made of -- it is not a
        stop point by itself, it is the declaration of one, and `_run_under` is the enforcement."""
        slowest = max(1.0, float(os.environ.get("PROBE_PERF_SUITE_BUDGET_SEC", SUITE_BUDGET_SEC)))
        self.assertGreater(slowest, 10.0,
                           "a budget under 10s cannot hold the live tier, which sleeps a real 6s")
        self.assertLessEqual(slowest, 600.0,
                             "a budget over 10 minutes is not a stop point, it is a formality")
        self.assertTrue(callable(StopPoint._run_under), "the enforcement is missing")

    def test_a_case_that_BLOCKS_is_a_NAMED_failure_not_a_HANG(self) -> None:
        """The property the stop point exists for, exercised against a case that really does block.

        A thread that never returns stands in for the failure this has to catch. Without a bound this
        test itself hangs, so it is the one case that would take the suite down with it -- which is
        exactly the situation being guarded, and why the bound is applied to the JOIN.
        """
        blocked = threading.Event()

        def never_returns():
            blocked.wait(30)  # released only by the timeout below

        worker = threading.Thread(target=never_returns, daemon=True)
        worker.start()
        try:
            with self.assertRaises(TimeoutError) as caught:
                StopPoint._run_under(0.25, lambda: worker.join(30))
            self.assertIn("stop point", str(caught.exception).lower())
        finally:
            blocked.set()
            worker.join(5)
            self.assertFalse(worker.is_alive(), "the worker outlived the stop point")

    def test_work_that_FINISHES_in_time_is_NOT_reported_as_a_timeout(self) -> None:
        StopPoint._run_under(10, lambda: sum(range(10_000)))  # reaching the next line IS the assertion

    def test_an_EXCEPTION_inside_the_budget_PROPAGATES_rather_than_becoming_a_timeout(self) -> None:
        def boom():
            raise ValueError("the real failure")

        with self.assertRaises(ValueError) as caught:
            StopPoint._run_under(10, boom)
        self.assertIn("real failure", str(caught.exception),
                      "a failing case must report its own failure, not a stop point")

    @staticmethod
    def _run_under(seconds: float, work) -> None:
        """Run `work` in a thread, join with a bound, and never let a bound look like a green run.

        A daemon thread is used so a genuinely stuck case cannot keep the INTERPRETER alive after the
        failure is reported -- otherwise reporting the stop point would itself hang the run.
        """
        done = threading.Event()
        box: dict = {}

        def runner():
            try:
                work()
            except BaseException as error:  # noqa: BLE001 - re-raised in the caller, not swallowed
                box["error"] = error
            finally:
                done.set()

        worker = threading.Thread(target=runner, daemon=True)
        worker.start()
        if not done.wait(seconds):
            raise TimeoutError(
                f"stop point: the suite exceeded {seconds}s inside a case. A case here is either blocked "
                f"on a socket with no timeout, or a loop that will not terminate. 49 of these cases call "
                f"`p.main` in-process, so there is no subprocess to kill and nothing else would stop it.")
        if "error" in box:
            raise box["error"]


if __name__ == "__main__":
    unittest.main()
