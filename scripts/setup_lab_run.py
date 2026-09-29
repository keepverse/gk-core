#!/usr/bin/env python3
"""Setup a reusable mid-match lab lawn (freeze + clear + fixtures).

Requires: game running, injector connected, operator already in a normal day lawn
(Adventure / Challenge). Explore / travel boards are a bad lab surface.
lab-overlay / lab-empty zero plant vanilla ATK (attackPercent=0, pea atk=0,
debug.combat.silence-vanilla) so peas do not add noise during overlay prove.

Usage:
  python gk-core/scripts/setup_lab_run.py
  python gk-core/scripts/setup_lab_run.py --scenario lab-empty
  python gk-core/scripts/setup_lab_run.py --then-prove

Replaces `scripts/setup-lab-run.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server, not "the" port: this machine runs a three-slot pool on 5101/5102/5103 where a
  script pointed at the default enters somebody else's board. The URL is read ONCE through
  `lib.resolve_base_url` (`$FUSIONRPG_SERVER_URL`, else the built-in default) and the source is
  reported, so a caller can see whether it measured the board it meant to.

* **EVERY REQUEST WAS UNBOUNDED.** The health GET carried `-TimeoutSec 5`, but the scenario POST,
  the board-snapshot POST and both `kinds=` event queries carried none, and the run-steps / snapshot
  polls polled on a sleep with no total budget. A wedged server holds the script open indefinitely.
  Every request now carries a timeout and every wait a deadline.

* **`Test-BoardStillLive` FAILED OPEN.** Its `board.end` query sat in `catch { }`, so a failed read
  fell through to `return $true` -- the script declared a board LIVE when the read that would have
  disproved it failed, then ran a lab scenario on a dead board. The port refuses by name instead:
  `BOARD-ENDED` is only ever returned by a read that SUCCEEDED and found an end event.

* **`Get-MaxEventId` WAS COPY-PASTED THREE WAYS.** The same exponential-probe-then-bisect search
  existed in this file and two siblings with drifted signatures and no timeout. The port calls the one
  shared implementation, `lib.get_debug_max_event_id`, which adds a per-request timeout AND a total
  search budget.

* **THE `-ThenProve` HANDOFF SHELLED OUT WITH NO TIMEOUT** and the script had no machine-readable
  surface. The handoff is now a bounded `subprocess.run` that forwards the child's exit code, and
  `--json` reports the lab evidence.

DELIBERATELY UNCHANGED
----------------------
Same recipe in the same order: health gate (ok AND injectorConnected), latest board.start (kinds
filter first, then a cursor scan back from the tip), board-still-live, the Explore/Travel refusal,
the scenario POST, the run-steps.done wait (300ms poll, `cheat.error` lines printed while the wait
continues), the board-snapshot wait (400ms poll, 15s budget), the living plant/zombie counts, the
lab-overlay no-zombie refusal, and the `-ThenProve` argument set (`--base-url` always,
`--target-ptr` only when a zombie was found). Same refusal messages, same operator output.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "setup-lab-run"

EXIT_OK = 0
EXIT_REFUSED = 64

DEFAULT_TIMEOUT = 60
HEALTH_TIMEOUT = 5
SCENARIO_POST_TIMEOUT = 15
SNAPSHOT_TIMEOUT_SEC = 15
SNAPSHOT_POST_TIMEOUT = 8
SNAPSHOT_POLL_SEC = 0.4
RUN_STEPS_POLL_SEC = 0.3
PROVE_TIMEOUT_SEC = 600
BOARD_SCAN_PAGES = 30
BOARD_SCAN_PAGE = 500
BOARD_SCAN_BACK = 5000

BAD_LEVEL_TYPES = ("Explore", "TravelAdvanture", "Travel", "IZ")

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED", "NO-BOARD-START",
    "BOARD-ENDED", "BAD-LEVEL-TYPE", "RUN-STEPS-TIMEOUT", "BOARD-SNAPSHOT-MISSING",
    "NO-LIVING-ZOMBIE", "SCENARIO-RESPONSE-INVALID", "PROVE-MISSING", "PROVE-TIMED-OUT",
    "REQUEST-FAILED", "RESPONSE-NOT-JSON",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request`, `subprocess` and
# `time` are process-wide modules: a test that patches any of them reaches every other test in this
# project. The event reads go through `lib`, whose own `_URLOPEN` seam the suite patches the same
# way.
_URLOPEN = urllib.request.urlopen
_SLEEP = time.sleep
_RUN = subprocess.run
_MONOTONIC = time.monotonic


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


def _request(url: str, body: dict | None, timeout: int, what: str) -> dict:
    """One bounded request. `body` of None is a GET; otherwise a POST with that JSON body."""
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        url, data=data, method="GET" if body is None else "POST",
        headers={"User-Agent": "FusionRpg-setup-lab-run/1.0",
                 "Content-Type": "application/json", "Accept": "application/json"})
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
                       f"{what} answered {error.code} {error.reason}"
                       + (f": {body_text}" if body_text else "")) from error
    except TimeoutError as expired:
        raise Refusal("REQUEST-FAILED", f"{what} did not answer within {timeout}s: {url}") from expired
    except urllib.error.URLError as error:
        raise Refusal("REQUEST-FAILED", f"{what} was unreachable ({error.reason}): {url}") from error
    except OSError as error:
        raise Refusal("REQUEST-FAILED", f"{what} failed: {error}") from error
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("RESPONSE-NOT-JSON", f"{what} answered with something that is not JSON: "
                                            f"{error}") from error


def get_latest_board_start(base_url: str, timeout: int) -> dict | None:
    """The most recent `board.start` event, or None.

    The original's two-step shape is preserved: prefer the `kinds=` filter (it avoids missing a
    board.start in a combat.hit flood), and on ANY failure of that cheap query fall back to a cursor
    scan back from the event-id tip. The fallback's reads are fail-closed -- the original ran them
    under `ErrorActionPreference = "Stop"`, so a failed scan aborted the script.
    """
    try:
        page = _request(f"{base_url}/api/events?kinds=board.start&limit=5", None, timeout,
                        "GET /api/events?kinds=board.start")
        hits = [i for i in page.get("items") or [] if i.get("kind") == "board.start"]
        if hits:
            return hits[-1]
    except Refusal:
        pass  # fall through to the cursor scan -- the cheap query is an optimisation, not a gate
    max_id = lib.get_debug_max_event_id(base_url, timeout=timeout)
    if max_id <= 0:
        return None
    cursor = max(0, max_id - BOARD_SCAN_BACK)
    last = None
    for _ in range(BOARD_SCAN_PAGES):
        items = lib.get_events(base_url, cursor, BOARD_SCAN_PAGE, timeout)
        if not items:
            break
        hits = [i for i in items if i.get("kind") == "board.start"]
        if hits:
            last = hits[-1]
        cursor = int(items[-1].get("id", cursor))
        if len(items) < BOARD_SCAN_PAGE:
            break
    return last


def test_board_still_live(base_url: str, board_start_ev: dict | None, timeout: int) -> bool:
    """True when no `board.end` after the start event. FAILS CLOSED: the original's `catch { }`
    around the board.end query returned `$true` on a read failure, declaring a dead board live."""
    if not board_start_ev:
        return False
    start_id = int(board_start_ev.get("id", 0))
    page = _request(f"{base_url}/api/events?kinds=board.end&limit=5", None, timeout,
                    "GET /api/events?kinds=board.end")
    end_hits = [i for i in page.get("items") or []
                if i.get("kind") == "board.end" and int(i.get("id", 0)) > start_id]
    return not end_hits


def wait_run_steps_done(base_url: str, after_id: int, timeout_sec: int, scenario: str) -> dict:
    """Wait for `debug.run-steps.done`. `cheat.error` lines are printed and the wait continues --
    the original's shape: an error mid-scenario is evidence, not a reason to stop watching."""
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, cursor, 200)
        done = [i for i in items if i.get("kind") == "debug.run-steps.done"]
        if done:
            return done[-1]
        errs = [i for i in items if i.get("kind") == "cheat.error"]
        if errs:
            try:
                ep = lib.get_debug_payload(errs[-1])
            except lib.Refusal:
                ep = None
            message = None
            if isinstance(ep, dict):
                message = ep.get("message") or ep.get("error")
            if not message:
                message = errs[-1].get("payload")
            print(f"cheat.error: {message}", file=sys.stderr)
        if items:
            cursor = int(items[-1].get("id", cursor))
        _SLEEP(RUN_STEPS_POLL_SEC)
    raise Refusal("RUN-STEPS-TIMEOUT",
                   f"timeout waiting for debug.run-steps.done (scenario={scenario} afterId was "
                   f"advanced to {cursor})")


def wait_board_snapshot(base_url: str, after_id: int,
                        timeout_sec: int = SNAPSHOT_TIMEOUT_SEC) -> dict | None:
    """Ask for a board snapshot and wait for one to land. None when the budget expires -- the
    original's answer too, and the caller's cue to refuse rather than to guess."""
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        _request(f"{base_url}/api/debug/effect/board-snapshot", {}, SNAPSHOT_POST_TIMEOUT,
                 "POST /api/debug/effect/board-snapshot")
        _SLEEP(SNAPSHOT_POLL_SEC)
        items = lib.get_events(base_url, cursor, 100)
        snap = [i for i in items if i.get("kind") == "debug.effect.board-snapshot"]
        if snap:
            payload = lib.get_debug_payload(snap[-1])
            return payload if isinstance(payload, dict) else None
        if items:
            cursor = int(items[-1].get("id", cursor))
    return None


def run_prove(base_url: str, target_ptr: str) -> int:
    """Run the sibling prove_overlay_combat.py and return its exit code.

    The original's `-ThenProve` handoff is preserved exactly: `--base-url` always, `--target-ptr`
    only when a zombie was found. The child's streams are forwarded and its exit code becomes this
    script's exit code (`exit $LASTEXITCODE` in the original).
    """
    sibling = Path(__file__).resolve().parent / "prove_overlay_combat.py"
    if not sibling.is_file():
        raise Refusal("PROVE-MISSING", f"the prove script does not exist: {sibling}")
    cmd = [sys.executable, str(sibling), "--base-url", base_url]
    if target_ptr:
        cmd += ["--target-ptr", target_ptr]
    try:
        proc = _RUN(cmd, capture_output=True, text=True, timeout=PROVE_TIMEOUT_SEC)
    except FileNotFoundError as error:
        raise Refusal("PROVE-MISSING", f"python is not on PATH: {error}") from error
    except subprocess.TimeoutExpired as expired:
        raise Refusal("PROVE-TIMED-OUT",
                       f"prove_overlay_combat.py did not finish within {PROVE_TIMEOUT_SEC}s") from expired
    if proc.stdout:
        print(proc.stdout, end="")
    if proc.stderr:
        print(proc.stderr, end="", file=sys.stderr)
    return proc.returncode


def run_setup(base_url: str, scenario: str, then_prove: bool, timeout: int) -> dict:
    """The whole recipe. Returns the lab evidence; raises Refusal on a precondition."""
    print("Health check...", file=sys.stderr)
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      "injector not connected — start game with FusionRpg injector loaded")

    bs = get_latest_board_start(base_url, timeout)
    if not bs:
        raise Refusal("NO-BOARD-START",
                      "No board.start in events — enter a normal Adventure day lawn first "
                      "(main menu → Adventure → any day), then re-run.")
    if not test_board_still_live(base_url, bs, timeout):
        raise Refusal("BOARD-ENDED",
                      "Last board already ended (board.end after board.start). Enter a lawn again, "
                      "leave it running, then re-run.")
    bsp = lib.get_debug_payload(bs)
    bsp = bsp if isinstance(bsp, dict) else {}
    level_type = str(bsp.get("levelType") or "")
    board_level = bsp.get("boardLevel")
    level_name = str(bsp.get("levelName") or "")
    print(f"Board live: levelType={level_type} "
          f"boardLevel={'' if board_level is None else board_level} levelName={level_name}",
          file=sys.stderr)

    if level_type in BAD_LEVEL_TYPES:
        raise Refusal("BAD-LEVEL-TYPE",
                      f"Refusing lab on levelType={level_type} "
                      f"boardLevel={'' if board_level is None else board_level} "
                      f"(levelName={level_name}).\n"
                      "That mode often looks like 'run never starts' (zero zombie speed / empty "
                      "Explore).\nReturn to main menu and open Adventure (or Challenge) day lawn, "
                      "then re-run.")

    print(f"Scenario={scenario} (freeze + reset + fixtures)...", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    print(f"event cursor afterId={after}", file=sys.stderr)
    queued = _request(f"{base_url}/api/debug/scenario/{scenario}", {}, SCENARIO_POST_TIMEOUT,
                      f"POST /api/debug/scenario/{scenario}")
    steps = queued.get("steps") if isinstance(queued, dict) else None
    if steps is None:
        raise Refusal("SCENARIO-RESPONSE-INVALID",
                      f"POST /api/debug/scenario/{scenario} answered without a 'steps' count: "
                      f"{queued!r}")
    print(f"queued steps={steps}", file=sys.stderr)

    done = wait_run_steps_done(base_url, after, timeout, scenario)
    print(f"run-steps.done id={done.get('id')}", file=sys.stderr)

    snap2 = wait_board_snapshot(base_url, int(done.get("id", 0)), SNAPSHOT_TIMEOUT_SEC)
    if not snap2:
        raise Refusal("BOARD-SNAPSHOT-MISSING",
                      "no debug.effect.board-snapshot after lab — injector may not have Board")
    plants, zombies = lib.get_live_board_entities(snap2)

    print(f"living plants={len(plants)} zombies={len(zombies)}", file=sys.stderr)
    if plants:
        print(f"PlantPtr={plants[0].get('ptr')}", file=sys.stderr)
    target_ptr = ""
    if zombies:
        target_ptr = str(zombies[0].get("ptr") or "")
        print(f"ZombiePtr={target_ptr} (use as --target-ptr)", file=sys.stderr)

    if scenario == "lab-overlay" and not zombies:
        raise Refusal("NO-LIVING-ZOMBIE",
                      "lab-overlay finished but no living zombie — check spawn Admit / Board state")

    next_cmd = "python scripts/prove_overlay_combat.py"
    if target_ptr:
        next_cmd += f" --target-ptr {target_ptr}"
    print(f"Lab ready. Next: {next_cmd}", file=sys.stderr)

    evidence = {"levelType": level_type,
                "boardLevel": "" if board_level is None else board_level,
                "levelName": level_name, "targetPtr": target_ptr,
                "plantPtr": str(plants[0].get("ptr") or "") if plants else "",
                "exitCode": EXIT_OK}
    if then_prove:
        evidence["exitCode"] = run_prove(base_url, target_ptr)
    return evidence


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="setup-lab-run",
        description="Setup a reusable mid-match lab lawn (freeze + clear + fixtures) "
                    "(replaces setup-lab-run.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--scenario", default="lab-overlay", choices=list(lib.SCENARIOS),
                        help="lab scenario to run (default: lab-overlay)")
    parser.add_argument("--then-prove", action="store_true",
                        help="run prove_overlay_combat.py after the lab is ready")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request and the run-steps budget (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    try:
        evidence = run_setup(base_url, args.scenario, args.then_prove, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                "scenario": args.scenario, **evidence}
    if args.json:
        print(json.dumps(envelope, indent=2))
    return evidence["exitCode"]


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
