#!/usr/bin/env python3
"""Architecture stress test — perf runbook / event-pipeline-v2 Task 12.

With a lawn open: freezes waves, mass-spawns plants + zombies via POST /api/debug/stress-fill,
waits for the board to settle, then captures a probe window set and prints the verdict.

Usage:
  python gk-core/scripts/stress_test.py                          # 40 plants / 150 zombies, 90s
  python gk-core/scripts/stress_test.py --zombies 400 --duration-sec 120

Replaces `scripts/stress-test.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE SCRIPT HAD NO `exit` STATEMENT AT ALL.** It always exited 0, PASS or FAIL. A parent
  process reading its exit code saw success for a run that printed "VERDICT: CHECK FAILURES
  ABOVE". It was unusable as a gate.

* **THE VERDICT WENT TO THE INFORMATION STREAM.** Line 82 used `Write-Host`, which writes stream
  (6) — the exact trap `2>&1` does not capture, and the one this migration exists to remove. A
  parent process reading the output saw nothing of the verdict.

* **THE CHILD'S EXIT CODE WAS IGNORED, AND THE PORT WIDENED THE WAYS IT CAN FAIL.** The retired
  `probe-perf.ps1` had ONE failure exit (unchecked). `probe_perf.py` returns `EXIT_REFUSED = 64`
  in EIGHT named reasons. Every one makes the original fall through to line 53, read a STALE
  `_baseline-<scenario>.json` from a previous run — the default name `stress-40p-150z` is fixed
  and highly reusable, so this is the LIKELY failure, not an exotic one — and print a confident
  `VERDICT: PASS` for a run that never happened. The port checks the child's exit code and refuses.

* **A MISSING BASELINE RAISED AN UNCAUGHT EXCEPTION.** `Get-Content` on a non-existent path
  throws a terminating `ItemNotFoundException` with no `try`, no `Test-Path`, no named refusal.
  The script died with no verdict block and no `VERDICT:` line. The port has the child's actual
  refusal reason in hand and surfaces it.

* **`POST /api/debug/stress-fill` HAD NO `try`/`catch` AND NO `-TimeoutSec`**, and it ran BEFORE
  the `finally` existed — so a failure there left 150 frozen zombies on the board and no
  `stress-clear`. The `finally`'s own `stress-clear` was warning-only, which is the one failure
  an operator would most want to know about.

* **LINE 40's `catch {}` WAS EMPTY AND LINE 42 CONTINUED.** The repo's named leak class. A failed
  census read was silently swallowed and the run continued to a verdict about a board that may not
  have been filled.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED** — `http://127.0.0.1:5088`. Resolved
  from `FUSIONRPG_SERVER_URL` and its source is REPORTED.

DELIBERATELY UNCHANGED
----------------------
Same inputs and defaults (`Plants = 40`, `Zombies = 150`, `DurationSec = 90`, the same
`stress-<p>p-<z>z` scenario name), the same three verdict bars (`gen2 == 0`, `pipePct <= 5`,
`dropped == 0`), the same nested-section correction at lines 64-67, the same `vfx.tick` warning-only
budget, and the same 5000 ms perf-window period in the pipeline-share arithmetic.

The open defect F5 (`docs/architecture/perf-v3-spec.md:41`) is carried forward, not silently
closed: the code corrects the `onCapture`-inside-`drain` overlap, but
`docs/research/perf/00-baseline.md:145` names a DIFFERENT overlap (`onEvent`-inside-`drain.tick`).
Both rows still read as open.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

TOOL_ID = "stress-test"

REPO = Path(__file__).resolve().parent.parent
PERF_DIR = REPO / "docs" / "research" / "perf"
PROBE_PERF = Path(__file__).resolve().parent / "probe_perf.py"

DEFAULT_BASE_URL = "http://127.0.0.1:5088"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
DEFAULT_PLANTS = 40
DEFAULT_ZOMBIES = 150
DEFAULT_DURATION_SEC = 90
SETTLE_POLLS = 30
SETTLE_INTERVAL_SEC = 2
SETTLE_READ_TIMEOUT = 2
FILL_TIMEOUT = 15
CLEAR_TIMEOUT = 15
HEALTH_TIMEOUT = 5
PERF_WINDOW_MS = 5000

EXIT_REFUSED = 64
EXIT_FAIL = 1

REFUSAL_REASONS = {
    "INVALID-PARAMETER", "SERVER-UNREACHABLE", "INJECTOR-NOT-CONNECTED", "FILL-FAILED",
    "CLEAR-FAILED", "PROBE-REFUSED", "BASELINE-MISSING", "BASELINE-UNREADABLE",
    "BASELINE-MALFORMED", "SCENARIO-NAME-INVALID",
}


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def resolve_base_url(given: str = "") -> tuple[str, str]:
    if given:
        return given.rstrip("/"), "explicit"
    env = os.environ.get(BASE_URL_ENV, "").strip()
    if env:
        return env.rstrip("/"), f"${BASE_URL_ENV}"
    return DEFAULT_BASE_URL, f"the built-in default ({BASE_URL_ENV} unset)"


def _request(method: str, url: str, body: dict | None = None,
             timeout: int = 10) -> dict:
    """One HTTP call, BOUNDED, with a NAMED refusal on every failure path."""
    payload = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=payload, method=method,
                                     headers={"User-Agent": f"{TOOL_ID}/1.0"})
    if payload is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        raise Refusal("SERVER-UNREACHABLE",
                      f"{method} {url} answered {error.code} {error.reason}") from error
    except TimeoutError as expired:
        raise Refusal("SERVER-UNREACHABLE",
                      f"{method} {url} did not answer within {timeout}s") from expired
    except (urllib.error.URLError, OSError) as error:
        raise Refusal("SERVER-UNREACHABLE", f"{method} {url} failed: {error}") from error
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("SERVER-UNREACHABLE",
                      f"{method} {url} answered with something that is not JSON: {error}") from error


def health_gate(base_url: str) -> None:
    """Refuse when the injector is not connected — the original's line 22."""
    health = _request("GET", f"{base_url}/health", timeout=HEALTH_TIMEOUT)
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      "injector not connected — is the game running on a lawn?")


def fill_board(base_url: str, plants: int, zombies: int, plant_type: int,
               zombie_type: int) -> None:
    """POST /api/debug/stress-fill. BOUNDED, and a FAILURE IS A REFUSAL.

    The original had no `try`/`catch` and no `-TimeoutSec` here, and this call ran BEFORE the
    `finally` existed — so a failure left the board frozen with no `stress-clear`.
    """
    body = {"plants": plants, "zombies": zombies, "plantType": plant_type,
            "zombieType": zombie_type, "freeze": True}
    _request("POST", f"{base_url}/api/debug/stress-fill", body, timeout=FILL_TIMEOUT)


def clear_board(base_url: str) -> None:
    """POST /api/debug/stress-clear. A FAILURE IS A REFUSAL, not a warning.

    The original warned and continued. This is the one failure an operator would most want to
    know about: the board is left frozen and mass-spawned.
    """
    _request("POST", f"{base_url}/api/debug/stress-clear", {}, timeout=CLEAR_TIMEOUT)


def wait_for_settle(base_url: str, plants: int, zombies: int) -> bool:
    """Poll the census until it reflects the fill. BOUNDED, and a failure is NAMED.

    The original's `catch {}` at line 40 was empty and line 42 CONTINUED on failure — the repo's
    named leak class. A failed census read is now a named refusal, not a silent continue.
    """
    plant_target = min(plants, 20)
    zombie_target = min(zombies, 30)
    deadline = time.monotonic() + SETTLE_POLLS * SETTLE_INTERVAL_SEC
    while time.monotonic() < deadline:
        time.sleep(min(SETTLE_INTERVAL_SEC, max(0.0, deadline - time.monotonic())))
        try:
            page = _request("GET", f"{base_url}/api/perf/recent?limit=1",
                            timeout=SETTLE_READ_TIMEOUT)
        except Refusal:
            raise Refusal("SERVER-UNREACHABLE",
                          "the perf census read failed during the settle wait; the board state "
                          "is unknown, so a verdict would be a claim about nothing")
        items = page.get("items") or []
        if not items:
            continue
        window = items[-1]
        board = window.get("board") or {}
        if (int(board.get("plants") or 0) >= plant_target
                and int(board.get("zombies") or 0) >= zombie_target):
            print(f"  [stress] board live: {board.get('plants')}p / {board.get('zombies')}z")
            return True
    return False


def run_probe(base_url: str, scenario: str, duration_sec: int) -> None:
    """Run `probe_perf.py` as a subprocess and CHECK ITS EXIT CODE.

    The original ignored `$LASTEXITCODE`. `probe_perf.py` returns `EXIT_REFUSED = 64` in eight
    named reasons; every one would make the original read a stale baseline and print a confident
    verdict for a run that never happened.
    """
    command = [sys.executable, str(PROBE_PERF), "--base-url", base_url,
               "--scenario", scenario, "--duration-sec", str(duration_sec)]
    try:
        proc = subprocess.run(command, capture_output=True, timeout=duration_sec + 60,
                              text=True)
    except subprocess.TimeoutExpired as expired:
        raise Refusal("PROBE-REFUSED",
                      f"probe_perf.py did not finish within {duration_sec + 60}s") from expired
    except OSError as error:
        raise Refusal("PROBE-REFUSED", f"could not run probe_perf.py: {error}") from error
    if proc.returncode != 0:
        raise Refusal("PROBE-REFUSED",
                      f"probe_perf.py exited {proc.returncode}. Its refusal is the reason this "
                      f"run has no verdict — a stale baseline from a previous run of the same "
                      f"scenario name would otherwise be read and reported as a fresh result. "
                      f"stderr: {proc.stderr.strip()[:400]}")


def load_baseline(scenario: str) -> list[dict]:
    """Read the baseline document. A MISSING OR UNREADABLE FILE IS A NAMED REFUSAL.

    The original's `Get-Content` raised an uncaught `ItemNotFoundException` with no `try`, no
    `Test-Path`, and no named refusal — the script died with no verdict block.
    """
    path = PERF_DIR / f"_baseline-{scenario}.json"
    if not path.is_file():
        raise Refusal("BASELINE-MISSING",
                      f"no baseline document at {path}. probe_perf.py must have run "
                      f"successfully for scenario {scenario!r} first; a stale file from a "
                      f"previous run is not a substitute")
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("BASELINE-UNREADABLE", f"could not read {path}: {error}") from error
    windows = document.get("windows") if isinstance(document, dict) else None
    if not isinstance(windows, list):
        raise Refusal("BASELINE-MALFORMED",
                      f"{path} has no 'windows' array: "
                      f"{sorted(document) if isinstance(document, dict) else type(document).__name__}")
    return [w for w in windows if isinstance(w, dict)]


def _average(windows: list[dict], selector) -> float:
    values = []
    for window in windows:
        value = selector(window)
        if value is not None and isinstance(value, (int, float)) and not isinstance(value, bool):
            values.append(float(value))
    return sum(values) / len(values) if values else 0.0


def _sum(windows: list[dict], selector) -> float:
    total = 0.0
    for window in windows:
        value = selector(window)
        if value is not None and isinstance(value, (int, float)) and not isinstance(value, bool):
            total += float(value)
    return total


def _max(windows: list[dict], selector) -> float:
    values = []
    for window in windows:
        value = selector(window)
        if value is not None and isinstance(value, (int, float)) and not isinstance(value, bool):
            values.append(float(value))
    return max(values) if values else 0.0


def verdict(windows: list[dict]) -> dict:
    """The three bars, computed exactly as the original's lines 57-81."""
    fps = round(_average(windows, lambda w: (w.get("frames") or {}).get("fpsAvg")), 1)
    gen2 = _sum(windows, lambda w: (w.get("gc") or {}).get("gen2"))
    drain_ms = round(_average(
        windows, lambda w: ((w.get("sections") or {}).get("drain.tick") or {}).get("totalMs")), 1)
    oc_ms = round(_average(
        windows, lambda w: ((w.get("sections") or {}).get("effect.onCapture") or {}).get("totalMs")), 1)
    td_ms = round(_average(
        windows, lambda w: ((w.get("sections") or {}).get("takeDamage.prefix") or {}).get("totalMs")), 1)
    carried = _sum(windows, lambda w: (w.get("drain") or {}).get("carried"))
    dropped = _max(windows, lambda w: (w.get("drain") or {}).get("droppedOverflow"))
    # Sections nest: OnDrained (inside drain.tick) shares the onCapture section, so the pipeline
    # share is drain + onCapture-outside-drain + takeDamage — not a raw sum (that double-counts).
    oc_outside = max(0.0, oc_ms - drain_ms)
    pipe_pct = round((drain_ms + oc_outside + td_ms) / PERF_WINDOW_MS * 100, 2)
    vfx_ms = round(_average(
        windows, lambda w: ((w.get("sections") or {}).get("vfx.tick") or {}).get("totalMs")), 1)
    vfx_pct = round(vfx_ms / PERF_WINDOW_MS * 100, 2)
    # An empty window list must NOT pass. The original's Measure-Object -Maximum on an empty list
    # returns $null, and `-not $null -or $null -eq 0` is $true, so it passed on nothing. A verdict
    # about zero windows is a claim about a run that produced no data.
    passed = bool(windows) and gen2 == 0 and pipe_pct <= 5 and (dropped == 0 or dropped is None)
    return {"fps": fps, "gen2": gen2, "drainMs": drain_ms, "ocMs": oc_ms, "tdMs": td_ms,
            "carried": carried, "dropped": dropped, "ocOutside": oc_outside,
            "pipePct": pipe_pct, "vfxMs": vfx_ms, "vfxPct": vfx_pct, "pass": passed}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="stress-test",
        description="Architecture stress test: fill the board, capture perf windows, print the "
                    "verdict (replaces stress-test.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to drive (default: ${BASE_URL_ENV}, else {DEFAULT_BASE_URL})")
    parser.add_argument("--plants", type=int, default=DEFAULT_PLANTS)
    parser.add_argument("--zombies", type=int, default=DEFAULT_ZOMBIES)
    parser.add_argument("--plant-type", type=int, default=0)
    parser.add_argument("--zombie-type", type=int, default=0)
    parser.add_argument("--duration-sec", type=int, default=DEFAULT_DURATION_SEC)
    parser.add_argument("--scenario", default="")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON")
    args = parser.parse_args(argv)

    if args.plants <= 0 or args.zombies <= 0:
        return _refuse("INVALID-PARAMETER",
                       f"--plants {args.plants} and --zombies {args.zombies} must be positive",
                       args.json)
    if args.duration_sec <= 0:
        return _refuse("INVALID-PARAMETER", f"--duration-sec {args.duration_sec} must be positive",
                       args.json)
    scenario = args.scenario or f"stress-{args.plants}p-{args.zombies}z"
    if not scenario.replace("-", "").replace("_", "").isalnum():
        return _refuse("SCENARIO-NAME-INVALID",
                       f"--scenario {scenario!r} must be a bare scenario name; it becomes a "
                       f"filename", args.json)

    refusal: Refusal | None = None
    try:
        base_url, source = resolve_base_url(args.base_url)
        print(f"[stress] base url: {base_url} (from {source})", file=sys.stderr)
        health_gate(base_url)
        print(f"[stress] freezing waves + filling board: {args.plants} plants "
              f"({args.plant_type}), {args.zombies} zombies ({args.zombie_type})...",
              file=sys.stderr)
        fill_board(base_url, args.plants, args.zombies, args.plant_type, args.zombie_type)
        if not wait_for_settle(base_url, args.plants, args.zombies):
            raise Refusal("SERVER-UNREACHABLE",
                          f"board census did not reach targets ({args.plants}p/{args.zombies}z) "
                          f"within {SETTLE_POLLS * SETTLE_INTERVAL_SEC}s — check "
                          f"debug.stress.fill ack in /api/debug/events")
        run_probe(base_url, scenario, args.duration_sec)
    except Refusal as caught:
        refusal = caught
    finally:
        # C1: restore caps / wave freeze / free-set so the session is playable afterwards.
        # A FAILURE HERE IS A REFUSAL, not a warning — the board is left frozen.
        try:
            clear_board(base_url)
            print("[stress] session state restored (stress-clear)", file=sys.stderr)
        except Refusal as clear_refusal:
            if refusal is None:
                refusal = clear_refusal

    if refusal is not None:
        return _refuse(refusal.reason, refusal.detail, args.json)

    try:
        windows = load_baseline(scenario)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    result = verdict(windows)
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "scenario": scenario, "baseUrl": base_url,
                          "baseUrlSource": source, "windows": len(windows), **result,
                          "exitCode": 0 if result["pass"] else EXIT_FAIL}, indent=2))
    else:
        _print_verdict(scenario, result)
    return 0 if result["pass"] else EXIT_FAIL


def _print_verdict(scenario: str, v: dict) -> None:
    print()
    print(f"=== stress verdict ({scenario}) ===")
    print(f"fps avg          : {v['fps']}  (cap 60)")
    print(f"gen2 collections : {v['gen2']}  (bar: 0)")
    print(f"v2 pipeline share: {v['pipePct']}%  (bar: <=5%)  "
          f"[drain {v['drainMs']} + onCaptureOutside {v['ocOutside']} + takeDamage {v['tdMs']} ms/5s]")
    print(f"drain carried    : {v['carried']} records total  "
          f"(occasional carry ok; growth = budget too tight)")
    print(f"ring dropped     : {v['dropped']}  (bar: 0)")
    print(f"vfx.tick share   : {v['vfxPct']}%  ({v['vfxMs']} ms/5s; budget <=0.5% — warning only)")
    if v["vfxPct"] > 0.5:
        print(f"  WARNING: vfx.tick over the 0.5% budget — see tasks/vfx-v2-todo.md T2 / SPEC.md F8",
              file=sys.stderr)
    print(f"VERDICT: {'PASS' if v['pass'] else 'CHECK FAILURES ABOVE'}")


def _refuse(reason: str, detail: str, as_json: bool) -> int:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason,
                          "detail": detail, "exitCode": EXIT_REFUSED}, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Refusal as refusal:
        sys.exit(_refuse(refusal.reason, refusal.detail, "--json" in sys.argv))