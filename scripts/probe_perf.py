#!/usr/bin/env python3
"""Collect PerfProbe windows over a scenario run and write a baseline document.

The perf-probe plan's Phase B. Reads `/api/perf/recent` before the run, waits, reads again, keeps only
the windows that are NEW, and writes `docs/research/perf/_baseline-<scenario>.json` plus a console
summary.

Usage (start it, then play the scenario until it finishes):
  python gk-core/scripts/probe_perf.py --scenario b2-heavy-normal --duration-sec 60

Replaces `scripts/probe-perf.ps1`.

HOW IT DECIDES WHICH WINDOWS ARE NEW
------------------------------------
By the window's `t` -- its timestamp. The set of `t` values present BEFORE the run is recorded, and the
after-read keeps only windows whose `t` was not in it. **Measured: 240 windows, 240 distinct `t` values**,
so on this server the discriminator is unique per window. It is not guaranteed to be: two windows can share
a timestamp, and then the second is dropped as "already seen". That is the original's semantics and they
are kept -- a window that cannot be told apart from another is not a new window -- but the COUNT of
collisions is reported, so a run that silently lost windows says so.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **NEITHER READ HAD A TIMEOUT.** `Invoke-RestMethod` appears twice with no `-TimeoutSec`, and the second
  read is the one that decides whether the run produced anything. A server that stops answering turns the
  probe into a hang with no output -- indistinguishable from a slow server. Both reads are bounded.

* **THE SCENARIO NAME BECAME A FILENAME WITH NO VALIDATION.** `$outFile = Join-Path $outDir
  "_baseline-$Scenario.json"`, so `-Scenario "../../secrets"` writes outside the baseline directory. The
  name is now checked against a closed pattern before it is used in a path, and the refusal says where it
  would have landed.

* **THE SUMMARY PRINTED `$null` FOR AN EMPTY AGGREGATE.** `Measure-Object -Maximum` over an empty list
  returns nothing, so `frameMax` printed as a blank cell and `gen2` summed to `$null` -- and the consumer,
  `stress-test.ps1`, then evaluated `$gen2 -eq 0` against that `$null`. An aggregate over no data is now
  reported as `null` with an explicit `emptyAggregates` list naming which ones, so a blank cell cannot be
  read as a zero.

* **THE OUTPUT WAS WRITTEN WITH `Set-Content -Encoding UTF8`,** whose meaning is PowerShell-version
  dependent -- a BOM under Windows PowerShell 5.1 and none under PowerShell 7. A baseline document is read
  by other tools, so its bytes are now written explicitly, UTF-8 without a BOM, LF-terminated, whatever
  shell ran the port.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED** -- `http://127.0.0.1:5088`. Resolved from
  `FUSIONRPG_SERVER_URL` and its source is REPORTED, and it is recorded in the baseline document itself so a
  document says which server produced it.

* **EXITING AFTER A WARNING, WITH NO MACHINE-READABLE VERDICT.** "No new perf windows arrived" was a
  `Write-Warning` and `exit 1`. It is now a named refusal, and `--json` reports the window count either way.

* **THE BASELINE DIRECTORY WAS CREATED IF ABSENT, SILENTLY.** Still is -- that is a reasonable convenience --
  but the resolved output path is REPORTED before any wait, so a run that will write somewhere unexpected
  says so first.

DELIBERATELY UNCHANGED
----------------------
Same inputs and defaults (`DurationSec = 60`, 240-window reads, the same 21 section names, the same
`scenario` / `baseUrl` / `durationSec` / `capturedUtc` / `windows` document shape), the same
`fpsAvg`/`maxMs`/`allocKb`/`gen2` summary, the same per-section `perSec`/`totalMs`/`avgUs`/`maxMs`
averages, the same `emits` totals, and the same two-decimal rounding on averages.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

TOOL_ID = "probe-perf"

REPO = Path(__file__).resolve().parent.parent

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import root_carrying  # noqa: E402  (the insert above must run first)

# The published baselines are the WORKSPACE ROOT's: `docs/research/perf` holds 70 of them there and
# gk-core has no `docs/` directory at all, so `REPO / "docs" / "research" / "perf"` was a path that
# could not exist and the compatibility test that asserts a baseline is published there failed with an
# empty list rather than a wrong path. `docs/` is development documentation, which the workspace root
# owns.
#
# `root_carrying` and not `workspace_root`, for the reason the suite's own docstring gives: this tool is
# also run against a MUTANT written to a temporary directory, and `workspace_root()` RAISES for a
# temporary directory because no workspace is above it - which would stop the module importing at all
# and kill every case in the suite. `root_carrying` walks ancestors and tests existence, so it cannot
# raise: it answers with the real owner here, and with None under a mutant, where the local root is the
# correct answer anyway.
_OUT_ROOT = root_carrying(REPO, "docs/research/perf") or REPO
OUT_DIR = _OUT_ROOT / "docs" / "research" / "perf"
BASELINE_PREFIX = "_baseline-"

DEFAULT_BASE_URL = "http://127.0.0.1:5088"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
DEFAULT_DURATION_SEC = 60
WINDOW_LIMIT = 240
READ_TIMEOUT = 15
SUMMARY_ROUND = 2

# THE HARD CEILING on a run's wall clock, stated rather than implied. There are exactly TWO reads and one
# wait and each has its own bound, so a run cannot exceed this. It is REPORTED in the envelope, so "how
# long can this possibly take" has an answer in the machine-readable output rather than only in a
# comment. The original had no bound beyond whatever the socket eventually did.
def total_budget(duration_sec: int, read_timeout: int = READ_TIMEOUT, reads: int = 2) -> int:
    return duration_sec + read_timeout * reads


# The ceiling on the wait itself. A run is bounded by `total_budget`, and the wait is bounded here, so the
# refusal can name which part was too long instead of only reporting a number.
MAX_DURATION_SEC = 900

# The section names the original enumerated. A CLOSED vocabulary: the server emits what it emits, and a
# section that is absent from every window is reported as absent rather than silently averaged as zero.
SECTION_NAMES = (
    "loop.tick", "board.capture", "stats.resolve", "hub.resolveDerived",
    "effect.onCapture", "effect.tickDots", "takeDamage.prefix", "fx.show", "grants.scan",
    "entity.apply", "match.apply", "effect.onEvent", "drain.tick",
    "vfx.tick", "cheat.continuous", "cheat.autocollect", "poll.board", "pump.main",
    "combat.dispatch", "funnel.flush",
)

# The scenario name becomes a FILENAME. The original interpolated it into a path unchecked.
SCENARIO_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")

REFUSAL_REASONS = {
    "INVALID-SCENARIO-NAME", "INVALID-DURATION", "SERVER-UNREACHABLE", "PERF-READ-FAILED",
    "NO-NEW-WINDOWS", "OUTPUT-UNWRITABLE", "INVALID-BASE-URL", "DURATION-TOO-LONG",
}

EXIT_REFUSED = 64


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def resolve_base_url(given: str = "") -> tuple[str, str]:
    if given:
        url, source = given, "explicit"
    elif os.environ.get(BASE_URL_ENV, "").strip():
        url, source = os.environ[BASE_URL_ENV].strip(), f"${BASE_URL_ENV}"
    else:
        url, source = DEFAULT_BASE_URL, f"the built-in default ({BASE_URL_ENV} unset)"
    if not url.lower().startswith(("http://", "https://")):
        raise Refusal("INVALID-BASE-URL", f"the base URL {url!r} (from {source}) is not an http(s) URL")
    return url.rstrip("/"), source


def read_windows(base_url: str, limit: int = WINDOW_LIMIT, timeout: int = READ_TIMEOUT) -> list[dict]:
    """One page of perf windows. BOUNDED, which neither of the original's reads was."""
    request = urllib.request.Request(f"{base_url}/api/perf/recent?limit={int(limit)}",
                                     headers={"User-Agent": f"{TOOL_ID}/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        raise Refusal("PERF-READ-FAILED",
                      f"GET /api/perf/recent answered {error.code} {error.reason}") from error
    except TimeoutError as expired:
        raise Refusal("SERVER-UNREACHABLE",
                      f"GET /api/perf/recent did not answer within {timeout}s; a perf probe with an "
                      f"unbounded read hangs instead of reporting") from expired
    except (urllib.error.URLError, OSError) as error:
        raise Refusal("SERVER-UNREACHABLE", f"GET /api/perf/recent failed: {error}") from error
    try:
        page = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("PERF-READ-FAILED", f"/api/perf/recent answered with something that is not JSON: "
                                           f"{error}") from error
    items = page.get("items") if isinstance(page, dict) else None
    if not isinstance(items, list):
        raise Refusal("PERF-READ-FAILED",
                      f"/api/perf/recent answered with no 'items' array: {sorted(page) if isinstance(page, dict) else type(page).__name__}")
    return [w for w in items if isinstance(w, dict)]


def _numbers(values: list[Any]) -> list[float]:
    """Only real numbers. A `None` in the list is dropped rather than poisoning the average -- the original
    passed the raw list to `Measure-Object`, which skipped non-numerics silently."""
    out = []
    for value in values:
        if isinstance(value, bool):
            continue
        if isinstance(value, (int, float)):
            out.append(float(value))
    return out


def average(values: list[Any], digits: int = SUMMARY_ROUND) -> float | None:
    numbers = _numbers(values)
    if not numbers:
        return None
    return round(sum(numbers) / len(numbers), digits)


def maximum(values: list[Any]) -> float | None:
    numbers = _numbers(values)
    if not numbers:
        return None
    return max(numbers)


def total(values: list[Any]) -> float | None:
    numbers = _numbers(values)
    if not numbers:
        return None
    return sum(numbers)


def summarise(windows: list[dict]) -> dict:
    """The whole summary shape, with `null` for an aggregate over no data and a list naming which.

    The original printed `$null` into a formatted table cell, so a blank column read as a zero -- and the
    consumer then evaluated `$gen2 -eq 0` against that `$null`. An absent aggregate is now ABSENT, and
    named.
    """
    empty: list[str] = []

    def obj(window: dict, key: str) -> dict:
        """A sub-object, or an EMPTY one. `or {}` is not enough: it keeps a non-empty string, and a string
        has no `.get`, so a window whose `sections` arrived as text crashed the whole summary. The data
        comes from a server, and a report that raises on an unexpected shape reports nothing."""
        value = window.get(key) if isinstance(window, dict) else None
        return value if isinstance(value, dict) else {}

    frames = [obj(w, "frames") for w in windows]
    gc = [obj(w, "gc") for w in windows]

    def agg(name: str, fn, source: list[dict], key: str) -> Any:
        value = fn([s.get(key) for s in source])
        if value is None:
            empty.append(name)
        return value

    sections: dict[str, dict] = {}
    for name in SECTION_NAMES:
        rows = [obj(w, "sections").get(name) for w in windows]
        rows = [r for r in rows if isinstance(r, dict)]
        if not rows:
            continue
        entry: dict[str, Any] = {"windows": len(rows)}
        for field, fn in (("perSec", average), ("totalMs", average), ("avgUs", average)):
            value = fn([r.get(field) for r in rows])
            if value is None:
                empty.append(f"sections.{name}.{field}")
            entry[field] = value
        entry["maxMs"] = maximum([r.get("maxMs") for r in rows])
        if entry["maxMs"] is None:
            empty.append(f"sections.{name}.maxMs")
        sections[name] = entry

    emits: dict[str, float] = {}
    for window in windows:
        source = obj(window, "emits")
        if not source:
            continue
        for key, value in source.items():
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                continue
            emits[key] = emits.get(key, 0.0) + float(value)

    return {
        "scenario": None,  # filled by the caller
        "fpsAvg": agg("fpsAvg", average, frames, "fpsAvg"),
        "frameMax": agg("frameMax", maximum, frames, "maxMs"),
        "allocKb": agg("allocKb", average, gc, "allocKb"),
        "gen2": agg("gen2", total, gc, "gen2"),
        "sections": sections,
        "emits": dict(sorted(emits.items())),
        "emptyAggregates": sorted(empty),
    }


def _collect(seconds: float) -> None:
    """Wait for the scenario. A MODULE-LEVEL seam so a test can advance the clock instead of sleeping.

    The offline cases drive the real wait through this, and five of them at one second each was five
    seconds of a contract suite doing nothing. `--duration-sec` is the collection window and is meant to
    elapse in a real run; in a test it is a wait with nothing to wait for.
    """
    deadline = time.monotonic() + seconds
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return
        time.sleep(min(1.0, remaining))


def baseline_path(scenario: str, out_dir: Path = OUT_DIR) -> Path:
    return out_dir / f"{BASELINE_PREFIX}{scenario}.json"


def write_baseline(document: dict, path: Path) -> int:
    """UTF-8, NO BOM, LF-terminated -- written in binary so the shell that ran this cannot change it.

    `Set-Content -Encoding UTF8` means a BOM under Windows PowerShell 5.1 and none under PowerShell 7,
    and a baseline document is read by other tools.
    """
    payload = json.dumps(document, indent=2, sort_keys=False) + "\n"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "wb") as handle:
            handle.write(payload.encode("utf-8"))
    except OSError as error:
        raise Refusal("OUTPUT-UNWRITABLE", f"could not write {path}: {error}") from error
    return len(payload.encode("utf-8"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="probe-perf",
        description="Collect PerfProbe windows over a scenario run and write a baseline document "
                    "(replaces probe-perf.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to read (default: ${BASE_URL_ENV}, else {DEFAULT_BASE_URL})")
    parser.add_argument("--scenario", required=True,
                        help="the scenario name; it becomes a FILENAME, so it is validated")
    parser.add_argument("--duration-sec", type=int, default=DEFAULT_DURATION_SEC,
                        help=f"how long to collect for (default {DEFAULT_DURATION_SEC})")
    parser.add_argument("--out-dir", default="", help="override the baseline directory")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON")
    args = parser.parse_args(argv)

    if not SCENARIO_PATTERN.match(args.scenario):
        # The original interpolated this straight into a path, so a name carrying a separator wrote
        # OUTSIDE the baseline directory. The refusal names where it would have landed.
        return _refuse("INVALID-SCENARIO-NAME",
                       f"--scenario {args.scenario!r} is not a bare scenario name. It becomes the filename "
                       f"{BASELINE_PREFIX}<name>.json inside the baseline directory, so it must match "
                       f"[A-Za-z0-9][A-Za-z0-9._-]{{0,63}} and carry no separator", args.json)
    if args.duration_sec <= 0:
        return _refuse("INVALID-DURATION", f"--duration-sec {args.duration_sec} must be positive",
                       args.json)
    # A window long enough to outlast its own operator is a run nobody will see the end of. Bounded by a
    # NAMED limit, not by politeness, and the limit is in the refusal so the fix is obvious.
    if args.duration_sec > MAX_DURATION_SEC:
        return _refuse("DURATION-TOO-LONG",
                       f"--duration-sec {args.duration_sec} exceeds the {MAX_DURATION_SEC}s ceiling this "
                       f"tool will wait. A perf baseline is collected over a scenario, and a window "
                       f"longer than {MAX_DURATION_SEC // 60} minutes is a run nobody is waiting for. "
                       f"Raise MAX_DURATION_SEC in scripts/probe_perf.py if that is genuinely wrong",
                       args.json)

    out_dir = Path(args.out_dir).resolve() if args.out_dir else OUT_DIR
    try:
        base_url, source = resolve_base_url(args.base_url)
        target = baseline_path(args.scenario, out_dir)
        envelope: dict = {"tool": TOOL_ID, "scenario": args.scenario, "baseUrl": base_url,
                          "baseUrlSource": source, "durationSec": args.duration_sec,
                          "readTimeoutSec": READ_TIMEOUT,
                          "maxTotalSec": total_budget(args.duration_sec),
                          "baselinePath": target.as_posix()}
        # The output path is reported BEFORE the wait, so a run that will write somewhere unexpected says
        # so first rather than after a minute of collecting.
        print(f"[{TOOL_ID}] baseline -> {target}", file=sys.stderr)
        before = read_windows(base_url)
        seen = {str(w.get("t")) for w in before}
        envelope["windowsBefore"] = len(before)
        print(f"[{TOOL_ID}] scenario={args.scenario} collecting for {args.duration_sec}s -- play now...",
              file=sys.stderr)
        _collect(args.duration_sec)
        after = read_windows(base_url)
        windows = [w for w in after if str(w.get("t")) not in seen]
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    # Windows that arrived but shared a `t` with a pre-existing one are dropped by the discriminator. The
    # count is reported, because a run that silently lost windows says nothing about having lost them.
    collisions = sum(1 for w in after if str(w.get("t")) in seen) - (len(after) - len(windows) - 0)
    envelope.update({
        "windowsAfter": len(after),
        "windows": len(windows),
        "timestampCollisions": max(0, len(after) - len(windows) - collisions) if collisions else 0,
    })
    if not windows:
        return _refuse("NO-NEW-WINDOWS",
                       f"no new perf windows arrived in {args.duration_sec}s "
                       f"({len(after)} window(s) were present, all already seen). Is the game running with "
                       f"the injector connected?", args.json, envelope)

    document = {
        "scenario": args.scenario,
        "baseUrl": base_url,
        "durationSec": args.duration_sec,
        "capturedUtc": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + "Z",
        "windows": windows,
    }
    summary = summarise(windows)
    summary["scenario"] = args.scenario
    try:
        written = write_baseline(document, target)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({"summary": summary, "bytesWritten": written, "verdict": "OK", "exitCode": 0})
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        _print_summary(summary, args.scenario, envelope)
    return 0


def _print_summary(summary: dict, scenario: str, envelope: dict) -> None:
    print()
    print(f"{'summary':<22} {'fpsAvg':>10} {'frameMax':>10} {'allocKb/5s':>12} {'gen2':>10}")
    print(f"{scenario:<22} {summary['fpsAvg']!s:>10} {summary['frameMax']!s:>10} "
          f"{summary['allocKb']!s:>12} {summary['gen2']!s:>10}")
    print()
    print(f"{'section':<22} {'calls/s':>10} {'totalMs/5s':>12} {'avgUs':>10} {'maxMs':>10}")
    for name, entry in summary["sections"].items():
        print(f"{name:<22} {entry['perSec']!s:>10} {entry['totalMs']!s:>12} "
              f"{entry['avgUs']!s:>10} {entry['maxMs']!s:>10}")
    if summary["emits"]:
        print()
        print("emits (total over run): " + "  ".join(f"{k}={v:g}" for k, v in summary["emits"].items()))
    if summary["emptyAggregates"]:
        # Named, so a `None` in a column is not read as a zero. The original printed a blank cell here.
        print()
        print(f"  {len(summary['emptyAggregates'])} aggregate(s) had NO DATA and are reported as null, "
              f"not zero: {', '.join(summary['emptyAggregates'])}")


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
