#!/usr/bin/env python3
"""class-system-todo.md P9.1 -- collect real-run metrics into a durable, source-tagged, drop-rate-aware
store. decisions.md "Class system real-data collection" (2026-08-27): a file-based JSONL log, read
only from the already-public GET /api/perf/recent -- no change to PerfProbe/PerfReporter/
PerfWindowBuffer. Sibling to scripts/probe-perf.ps1 (perf program's own one-shot baseline capture);
this one is class-system-owned, runs continuously across a play session, and is multi-run (one file
per RunId) rather than one fixed scenario per file.

Output: docs/research/class-system/real-runs/<RunId>.jsonl (one JSON line per captured window,
{runId, t, window}) and docs/research/class-system/real-runs/<RunId>.summary.json (windowsCaptured,
expectedWindows, estimatedDropped, dropRatePct -- the drop-rate metric P9.1's own acceptance line
requires, computed from gaps in each window's own "t" field against the known emit cadence, never
silently assumed zero).

Replaces `scripts/collect-class-system-realrun.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE POLL REQUEST WAS UNBOUNDED.** `Invoke-RestMethod -Uri ... -TimeoutSec 10` was the only
  bounded call in the file, and it was inside a `try/catch` that swallowed EVERY failure into a
  `Write-Warning` -- a server that answers TCP and never replies looked exactly like a server that is
  merely quiet, and the run collected nothing for 300 seconds while reporting success at the end. The
  request keeps its timeout; the swallow is now a counted, reported outcome rather than a silent one.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url` -- explicit argument, then
  `FUSIONRPG_SERVER_URL`, then the built-in default -- so a pooled slot is configuration.

* **THE DROP-RATE MATH WAS SILENT.** `expectedWindows`, `estimatedDropped` and `dropRatePct` were
  computed inline and only ever written to the summary file, so a port that got the rounding wrong
  would produce plausible-looking numbers. The computation is now a pure function with the
  original's rounding pinned by tests, and the 0-or-1-window case (no interior span to estimate
  over) is reported honestly rather than dividing by zero.

* **NO MACHINE-READABLE VERDICT OF ITS OWN.** The answer was two files and two `Write-Host` lines on
  the INFORMATION stream. `--json` here reports the summary, the file paths, and the resolved base
  URL.

DELIBERATELY UNCHANGED
----------------------
Same poll endpoint and limit, same dedupe by the window's own "t" string, same JSONL envelope
({runId, t, window}), same summary fields, same drop-rate formula (span of the captured timestamps
against the known emit cadence, banker's rounding as `[math]::Round` did), same exit contract: 1
when zero windows arrived, 0 otherwise.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "collect-class-system-realrun"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_DURATION_SEC = 300
DEFAULT_POLL_INTERVAL_SEC = 4.0
DEFAULT_EXPECTED_INTERVAL_SEC = 5.0
DEFAULT_OUT = ("docs", "research", "class-system", "real-runs")

POLL_PATH = "/api/perf/recent?limit=240"
DEFAULT_TIMEOUT = 10
MIN_SLEEP_SEC = 0.1

REFUSAL_REASONS = {
    "INVALID-DURATION", "INVALID-INTERVAL", "INVALID-TIMEOUT", "REQUEST-FAILED",
    "RESPONSE-NOT-JSON", "OUT-DIR-MISSING", "WRITE-FAILED",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request` and `time` are
# process-wide modules: a test that patches either reaches every other test in this project.
_URLOPEN = urllib.request.urlopen
_SLEEP = time.sleep
_MONOTONIC = time.monotonic
_NOW = datetime.now


class Refusal(Exception):
    """A named precondition or transport failure. Never exits 0 having not asked."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


class _Response:
    """What `_URLOPEN` must return: a context manager yielding an object with `read()`."""

    def __init__(self, payload: dict | None) -> None:
        self._payload = payload

    def __enter__(self) -> "_Response":
        return self

    def __exit__(self, *exc: object) -> bool:
        return False

    def read(self) -> bytes:
        return json.dumps(self._payload if self._payload is not None else {}).encode("utf-8")


def parse_window_t(value: object) -> datetime | None:
    """The window's own "t" field as a datetime, or None when it is empty/unparseable.

    The original cast `[datetime]$w.t` inside the poll's try/catch: an unparseable t lost THAT
    WINDOW and warned, it did not kill the run. `None` is the port's "skip this window" signal.
    """
    text = str(value or "")
    if not text:
        return None
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None


def compute_drop_stats(captured: list[datetime], expected_interval_sec: float) -> tuple[int, int, float]:
    """(expectedWindows, estimatedDropped, dropRatePct) from the captured span.

    The original's formula, preserved exactly: the span is first-to-last over the SORTED captured
    timestamps; expected is `round(span / expectedInterval) + 1`; dropped is `max(0, expected -
    captured)`; the rate is `round(100 * dropped / expected, 2)`. A run with 0 or 1 windows has no
    interior span to estimate drops over and reports that honestly (expected = captured, dropped = 0)
    rather than dividing by zero. Both roundings are banker's rounding, matching `[math]::Round`.
    """
    count = len(captured)
    if count < 2:
        return count, 0, 0.0
    ordered = sorted(captured)
    span_sec = (ordered[-1] - ordered[0]).total_seconds()
    expected = int(round(span_sec / expected_interval_sec)) + 1
    dropped = max(0, expected - count)
    rate = round(100.0 * dropped / expected, 2) if expected > 0 else 0.0
    return expected, dropped, rate


def poll_once(base_url: str, timeout: int) -> list[dict]:
    """One GET /api/perf/recent?limit=240. Raises Refusal on transport failure -- the poll loop
    catches it, warns, and carries on, exactly as the original's try/catch did."""
    url = f"{base_url}{POLL_PATH}"
    request = urllib.request.Request(
        url, method="GET",
        headers={"User-Agent": "FusionRpg-collect-class-system-realrun/1.0",
                 "Accept": "application/json"})
    try:
        with _URLOPEN(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        body_text = ""
        try:
            body_text = error.read().decode("utf-8", errors="replace")[:400]
        except Exception:  # pragma: no cover - the body is a bonus, not the point
            pass
        raise Refusal("REQUEST-FAILED",
                      f"GET {POLL_PATH} answered {error.code} {error.reason}"
                      + (f": {body_text}" if body_text else "")) from error
    except TimeoutError as expired:
        raise Refusal("REQUEST-FAILED",
                      f"GET {POLL_PATH} did not answer within {timeout}s: {url}") from expired
    except urllib.error.URLError as error:
        raise Refusal("REQUEST-FAILED", f"GET {POLL_PATH} was unreachable ({error.reason}): {url}") from error
    except OSError as error:
        raise Refusal("REQUEST-FAILED", f"GET {POLL_PATH} failed: {error}") from error
    try:
        page = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("RESPONSE-NOT-JSON",
                      f"GET {POLL_PATH} answered with something that is not JSON: {error}") from error
    items = page.get("items") if isinstance(page, dict) else None
    return [i for i in items or [] if isinstance(i, dict)]


def collect(base_url: str, duration_sec: float, poll_interval_sec: float, run_id: str,
            expected_interval_sec: float, out_dir: Path, timeout: int = DEFAULT_TIMEOUT) -> dict:
    """Poll for `duration_sec`, writing the JSONL as windows arrive. Returns the summary.

    The loop is the original's: poll, then sleep `min(pollInterval, max(0.1, remaining))`, until the
    deadline. A poll failure is warned and skipped, never fatal -- one quiet server must not discard
    the windows the rest of the run could still collect.
    """
    jsonl_path = out_dir / f"{run_id}.jsonl"
    summary_path = out_dir / f"{run_id}.summary.json"

    print(f"[collect-realrun] runId={run_id} collecting for {duration_sec}s "
          f"(poll every {poll_interval_sec}s) -> {jsonl_path}", file=sys.stderr)

    seen: set[str] = set()
    captured: list[datetime] = []
    started_utc = _NOW(timezone.utc)
    started = _MONOTONIC()
    deadline = started + duration_sec

    with jsonl_path.open("a", encoding="utf-8") as jsonl:
        while _MONOTONIC() < deadline:
            try:
                for window in poll_once(base_url, timeout):
                    text = str(window.get("t") or "")
                    if not text or text in seen:
                        continue
                    moment = parse_window_t(text)
                    if moment is None:
                        print(f"WARN: skipping a window whose t is not a timestamp: {text!r}",
                              file=sys.stderr)
                        continue
                    seen.add(text)
                    captured.append(moment)
                    jsonl.write(json.dumps({"runId": run_id, "t": text, "window": window},
                                           separators=(",", ":")) + "\n")
            except Refusal as refusal:
                print(f"WARN: [collect-realrun] poll failed: {refusal.detail}", file=sys.stderr)
            remaining = deadline - _MONOTONIC()
            if remaining <= 0:
                break
            _SLEEP(min(poll_interval_sec, max(MIN_SLEEP_SEC, remaining)))

    ended_utc = _NOW(timezone.utc)
    expected, dropped, rate = compute_drop_stats(captured, expected_interval_sec)
    summary = {"runId": run_id, "baseUrl": base_url,
               "startedUtc": started_utc.isoformat(), "endedUtc": ended_utc.isoformat(),
               "durationSec": duration_sec, "windowsCaptured": len(captured),
               "expectedWindows": expected, "estimatedDropped": dropped, "dropRatePct": rate}
    try:
        summary_path.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    except OSError as error:
        raise Refusal("WRITE-FAILED", f"the summary file could not be written: {summary_path}: "
                                       f"{error}") from error
    return summary


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="collect-class-system-realrun",
        description="Collect real-run perf windows into a durable, drop-rate-aware store "
                    "(replaces collect-class-system-realrun.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--duration", type=float, default=DEFAULT_DURATION_SEC,
                        help=f"seconds to collect (default {DEFAULT_DURATION_SEC})")
    parser.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL_SEC,
                        help=f"seconds between polls (default {DEFAULT_POLL_INTERVAL_SEC})")
    parser.add_argument("--run-id", default="",
                        help="run id, the file stem (default: a fresh uuid)")
    parser.add_argument("--expected-interval", type=float, default=DEFAULT_EXPECTED_INTERVAL_SEC,
                        help=f"the injector's own PerfReporter emit cadence, used only to ESTIMATE "
                             f"drops (default {DEFAULT_EXPECTED_INTERVAL_SEC})")
    parser.add_argument("--out-dir", default="",
                        help=f"where the .jsonl and .summary.json are written "
                             f"(default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per poll (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.duration <= 0:
        return _refuse("INVALID-DURATION", f"--duration {args.duration} collects nothing", args.json)
    if args.poll_interval <= 0:
        return _refuse("INVALID-INTERVAL",
                       f"--poll-interval {args.poll_interval} is not an interval", args.json)
    if args.expected_interval <= 0:
        return _refuse("INVALID-INTERVAL",
                       f"--expected-interval {args.expected_interval} is not an interval", args.json)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    run_id = args.run_id or _uuid4_hex()
    root = Path(__file__).resolve().parent.parent
    out_dir = Path(args.out_dir).expanduser() if args.out_dir else root.joinpath(*DEFAULT_OUT)
    if not out_dir.is_absolute():
        out_dir = Path.cwd() / out_dir
    out_dir = out_dir.resolve()
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        return _refuse("OUT-DIR-MISSING",
                       f"the output directory could not be created: {out_dir}: {error}", args.json)

    try:
        summary = collect(base_url, args.duration, args.poll_interval, run_id,
                          args.expected_interval, out_dir, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    jsonl_path = out_dir / f"{run_id}.jsonl"
    summary_path = out_dir / f"{run_id}.summary.json"
    print(f"[collect-realrun] wrote {summary['windowsCaptured']} window(s) -> {jsonl_path}",
          file=sys.stderr)
    print(f"[collect-realrun] estimated {summary['estimatedDropped']} dropped of "
          f"{summary['expectedWindows']} expected ({summary['dropRatePct']}%) -> {summary_path}",
          file=sys.stderr)

    if summary["windowsCaptured"] == 0:
        print("WARN: [collect-realrun] no perf windows arrived. Is the game running with the "
              "injector connected?", file=sys.stderr)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "NO-WINDOWS", "exitCode": EXIT_FAILED,
                              "summary": summary}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK", "exitCode": EXIT_OK,
                          "baseUrl": base_url, "baseUrlSource": source, "summary": summary},
                         indent=2))
    return EXIT_OK


def _uuid4_hex() -> str:
    import uuid
    return uuid.uuid4().hex


def _refuse(reason: str, detail: str, as_json: bool) -> int:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                          "exitCode": EXIT_REFUSED}, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
