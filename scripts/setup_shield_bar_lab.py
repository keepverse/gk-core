#!/usr/bin/env python3
"""Setup a frozen lawn with pea + zombie, each with 3 RPG shield stacks (fire/ice/earth).

Preferred: python -m live_test run shield.lab   (gk-fusion/tools/live_test)
See docs/runbook/live-test-ssot.md
Use this BEFORE looking for the in-game shield bar — do not use the bare probe alone.

Requires: Melon/Bep injector connected, operator already in an Adventure day lawn
(same gate as setup_lab_run.py — Explore/Travel boards refuse).

Usage:
  python gk-core/scripts/setup_shield_bar_lab.py

What it does:
  1. wave-freeze + reset board
  2. spawn pea at col=2 row=2
  3. spawn zombie at row=2 x=7.5
  4. debug.shield.demo-all → fire/ice/earth ×100 on EVERY living plant+zombie
  5. prints PlantPtr / ZombiePtr + shield snapshot

In-game: look under the pea and the zombie for multi-stop bars labeled ~100% ×3.
F9 toggles bars; F7 overlay settings.

Replaces `scripts/setup-shield-bar-lab.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **EVERY REQUEST WAS UNBOUNDED.** Only the health GET carried `-TimeoutSec 5`; the scenario POST,
  the board-snapshot POST, the tip-window re-fetch and every `Wait-Kind` page carried none, and the
  waits polled on a sleep with no total budget. Every request now carries a timeout and every wait
  a deadline; a kind that never arrives is a named refusal rather than an infinite loop.

* **`Test-BoardStillLive` FAILED OPEN.** Its `board.end` query sat in `catch { }`, so a failed read
  fell through to `return $true` -- the script declared a board LIVE when the read that would have
  disproved it failed. The port refuses by name instead.

* **`Get-MaxEventId` WAS COPY-PASTED THREE WAYS.** The port calls the one shared implementation,
  `lib.get_debug_max_event_id`, which adds a per-request timeout AND a total search budget.

* **THE SCRIPT HAD NO MACHINE-READABLE SURFACE.** `Write-Host` progress went to the host and the
  exit code was 0 even when a fixture was missing (see the decision below). `--json` reports the
  lab evidence.

DELIBERATELY UNCHANGED — AND ONE DELIBERATE DECISION
----------------------------------------------------
Same recipe in the same order: health gate (ok AND injectorConnected), latest board.start (kinds
filter only -- this file never had the cursor-scan fallback its sibling has), board-still-live, the
Explore/Travel refusal, the lab-shield-bar scenario POST, the run-steps.done wait, the demo-all
wait (from the done cursor, then once more from the scenario cursor), the shield-snapshot wait, the
board-snapshot re-fetch near the tip, and the closing hints. The script still never POSTs
`/api/debug/shield/demo-all` itself -- the scenario endpoint does that, and adding the POST here
would change what the lab measures.

**THE MISSING-FIXTURE DECISION.** The original ended with `Write-Warning` and a continue when the
board snapshot held no living plant or no living zombie, so a lab whose pea or zombie fixture failed
to spawn reported success with half its evidence missing. The port REFUSES BY NAME instead
(`NO-LIVING-PLANT` / `NO-LIVING-ZOMBIE`), matching `setup_lab_run.py`'s lab-overlay no-zombie
refusal: a lab missing a fixture is not the lab it claims to be, and an operator sent to look under
a pea that is not there learns nothing. `Write-Warning`-then-continue is on the repo's
do-not-reproduce list, so the warn was not carried over.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "setup-shield-bar-lab"

EXIT_OK = 0
EXIT_REFUSED = 64

DEFAULT_TIMEOUT = 60
HEALTH_TIMEOUT = 5
SCENARIO_POST_TIMEOUT = 15
BOARD_SNAPSHOT_WAIT_SEC = 8
BOARD_SNAPSHOT_POLL_SEC = 0.5
BOARD_SNAPSHOT_WINDOW = 80
RUN_STEPS_POLL_SEC = 0.25
DEMO_ALL_WAIT_SEC = 15
DEMO_ALL_RETRY_SEC = 5
SHIELD_SNAPSHOT_WAIT_SEC = 10

BAD_LEVEL_TYPES = ("Explore", "TravelAdvanture", "Travel", "IZ")

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED", "NO-BOARD-START",
    "BOARD-ENDED", "BAD-LEVEL-TYPE", "RUN-STEPS-TIMEOUT", "DEMO-ALL-MISSING",
    "DEMO-ALL-PAYLOAD-INVALID", "SCENARIO-RESPONSE-INVALID", "SNAPSHOT-EMPTY",
    "NO-LIVING-PLANT", "NO-LIVING-ZOMBIE",
    "REQUEST-FAILED", "RESPONSE-NOT-JSON",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request` and `time` are
# process-wide modules: a test that patches either reaches every other test in this project. The
# event reads go through `lib`, whose own `_URLOPEN` seam the suite patches the same way.
_URLOPEN = urllib.request.urlopen
_SLEEP = time.sleep
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
        headers={"User-Agent": "FusionRpg-setup-shield-bar-lab/1.0",
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
    """The most recent `board.start` via the `kinds=` filter, or None. This file has no fallback
    scan -- the original returned `$null` here and the caller refused."""
    try:
        page = _request(f"{base_url}/api/events?kinds=board.start&limit=5", None, timeout,
                        "GET /api/events?kinds=board.start")
    except Refusal:
        return None
    hits = [i for i in page.get("items") or [] if i.get("kind") == "board.start"]
    return hits[-1] if hits else None


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


def wait_kind(base_url: str, after_id: int, kind: str, timeout_sec: int) -> dict | None:
    """Wait for an event of `kind` to land after `after_id`. None when the deadline expires.

    The original's `Wait-Kind` shape is preserved: page forward from the cursor, take the LAST
    matching event on the page, sleep 250ms between pages. The deadline is the port's addition --
    the original had none, so a kind that never arrived meant an infinite loop.
    """
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, cursor, 200)
        hit = None
        for event in items:
            if event.get("kind") == kind:
                hit = event
        if hit is not None:
            return hit
        if items:
            cursor = int(items[-1].get("id", cursor))
        _SLEEP(RUN_STEPS_POLL_SEC)
    return None


def run_setup(base_url: str, timeout: int) -> dict:
    """The whole recipe. Returns the lab evidence; raises Refusal on a precondition."""
    print("== health ==", file=sys.stderr)
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      "injector not connected — start Melon/Bep game with FusionRpg loaded, enter a "
                      "lawn, re-run")
    print(f"  source={health.get('source')} injectorConnected={health.get('injectorConnected')}",
          file=sys.stderr)

    bs = get_latest_board_start(base_url, timeout)
    if not bs:
        raise Refusal("NO-BOARD-START",
                      "No board.start — open Adventure day lawn first (main menu → Adventure → any "
                      "day), leave it running, re-run.")
    if not test_board_still_live(base_url, bs, timeout):
        raise Refusal("BOARD-ENDED",
                      "Last board already ended. Enter a lawn again, leave it running, re-run.")
    bsp = lib.get_debug_payload(bs)
    bsp = bsp if isinstance(bsp, dict) else {}
    level_type = str(bsp.get("levelType") or "")
    print(f"Board live: levelType={level_type} boardLevel={bsp.get('boardLevel')} "
          f"levelName={bsp.get('levelName')}", file=sys.stderr)
    if level_type in BAD_LEVEL_TYPES:
        raise Refusal("BAD-LEVEL-TYPE",
                      f"Refusing lab on levelType={level_type} — use Adventure/Challenge day lawn.")

    print("== scenario lab-shield-bar (freeze + spawn pea/zombie + demo-all 3 stacks) ==",
          file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    print(f"  afterId tip={after}", file=sys.stderr)
    queued = _request(f"{base_url}/api/debug/scenario/lab-shield-bar", {}, SCENARIO_POST_TIMEOUT,
                      "POST /api/debug/scenario/lab-shield-bar")
    steps = queued.get("steps") if isinstance(queued, dict) else None
    if steps is None:
        raise Refusal("SCENARIO-RESPONSE-INVALID",
                      f"POST /api/debug/scenario/lab-shield-bar answered without a 'steps' count: "
                      f"{queued!r}")
    print(f"  queued steps={steps}", file=sys.stderr)

    done = wait_kind(base_url, after, "debug.run-steps.done", timeout)
    if done is None:
        raise Refusal("RUN-STEPS-TIMEOUT", "timeout waiting for debug.run-steps.done")
    print(f"  run-steps.done id={done.get('id')}", file=sys.stderr)

    demo_all = wait_kind(base_url, int(done.get("id", 0)), "debug.shield.demo-all", DEMO_ALL_WAIT_SEC)
    if demo_all is None:
        # demo-all may have landed before run-steps.done cursor; scan from scenario afterId
        demo_all = wait_kind(base_url, after, "debug.shield.demo-all", DEMO_ALL_RETRY_SEC)
    if demo_all is None:
        raise Refusal("DEMO-ALL-MISSING", "no debug.shield.demo-all — spawn/demo failed")
    demo_payload = lib.get_debug_payload(demo_all)
    if not isinstance(demo_payload, dict):
        raise Refusal("DEMO-ALL-PAYLOAD-INVALID",
                      f"the demo-all event carried no object payload: {demo_payload!r}")
    print(f"  demo-all targets={demo_payload.get('targetCount')} amount={demo_payload.get('amount')}",
          file=sys.stderr)
    for target in demo_payload.get("targets") or []:
        print(f"    ptr={target.get('targetPtr')} stacks={target.get('count')}", file=sys.stderr)

    snap_ev = wait_kind(base_url, after, "debug.shield.snapshot", SHIELD_SNAPSHOT_WAIT_SEC)
    snap = lib.get_debug_payload(snap_ev) if snap_ev is not None else None
    if not isinstance(snap, dict) or int(snap.get("ownerCount") or 0) < 1:
        raise Refusal("SNAPSHOT-EMPTY", "shield snapshot empty — bars will not show (no stacks)")

    print("", file=sys.stderr)
    print("== living shielded units ==", file=sys.stderr)
    for owner in snap.get("owners") or []:
        print(f"  ptr={owner.get('ptr')} hp={owner.get('hp')}/{owner.get('maxHp')} "
              f"stacks={owner.get('stackCount')}", file=sys.stderr)
        for stack in owner.get("stacks") or []:
            print(f"    - {stack.get('element')} {stack.get('hp')}/{stack.get('maxHp')}",
                  file=sys.stderr)

    # Board snapshot for plant/zombie labels
    _request(f"{base_url}/api/debug/effect/board-snapshot", {}, timeout,
             "POST /api/debug/effect/board-snapshot")
    _SLEEP(BOARD_SNAPSHOT_POLL_SEC)
    wait_kind(base_url, lib.get_debug_max_event_id(base_url, timeout=timeout),
              "debug.effect.board-snapshot", BOARD_SNAPSHOT_WAIT_SEC)
    # re-fetch near tip
    tip = lib.get_debug_max_event_id(base_url, timeout=timeout)
    page = _request(f"{base_url}/api/events?afterId={max(0, tip - BOARD_SNAPSHOT_WINDOW)}"
                    f"&limit={BOARD_SNAPSHOT_WINDOW}", None, timeout,
                    "GET /api/events (tip window)")
    snaps = [i for i in page.get("items") or []
             if i.get("kind") == "debug.effect.board-snapshot"]
    board = lib.get_debug_payload(snaps[-1]) if snaps else None
    board = board if isinstance(board, dict) else {}
    plants = [e for e in board.get("entities") or []
              if isinstance(e, dict) and e.get("side") == "plant" and e.get("living")]
    zombies = [e for e in board.get("entities") or []
               if isinstance(e, dict) and e.get("side") == "zombie" and e.get("living")]

    print("", file=sys.stderr)
    print("== fixtures ==", file=sys.stderr)
    if plants:
        print(f"  PlantPtr={plants[0].get('ptr')}  (pea col=2 row=2)", file=sys.stderr)
    if zombies:
        print(f"  ZombiePtr={zombies[0].get('ptr')} (basic row=2 x≈7.5)", file=sys.stderr)
    # THE MISSING-FIXTURE DECISION: the original warned and continued here; the port refuses.
    if not plants:
        raise Refusal("NO-LIVING-PLANT",
                      "no living plant in board-snapshot — the pea fixture did not spawn")
    if not zombies:
        raise Refusal("NO-LIVING-ZOMBIE",
                      "no living zombie in board-snapshot — the zombie fixture did not spawn")

    print("", file=sys.stderr)
    print("Lab ready. Look in-game under the pea AND the zombie for fire→ice→earth bars "
          "(~100% ×3).", file=sys.stderr)
    print("F9 = toggle shield bars. F7 = Overlay Settings.", file=sys.stderr)
    print('Re-grant only: POST /api/debug/shield/demo-all  body { "amount": 100 }', file=sys.stderr)
    return {"levelType": level_type, "targetPtr": str(zombies[0].get("ptr") or ""),
            "plantPtr": str(plants[0].get("ptr") or ""), "exitCode": EXIT_OK}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="setup-shield-bar-lab",
        description="Setup a frozen lawn with pea + zombie, each with 3 RPG shield stacks "
                    "(replaces setup-shield-bar-lab.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
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
        evidence = run_setup(base_url, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source, **evidence}
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
