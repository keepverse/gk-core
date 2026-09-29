#!/usr/bin/env python3
"""Damage RPG shields and show bar fillRatio shrink.

Preferred: python -m live_test run shield.absorb   (gk-fusion/tools/live_test)
See docs/runbook/live-test-ssot.md

Prerequisites: Adventure lawn live, injector connected, units with shields
   (setup-shield-bar-lab.ps1 or demo-all first).

Replaces `scripts/probe-shield-damage.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** `Invoke-RestMethod` was called seven times with no `-TimeoutSec`
  anywhere in the file. A wedged server holds the probe open indefinitely. Every request now carries
  a timeout, and every event wait carries a deadline.

* **THE EVENT WAITS COULD WAIT FOREVER.** `Wait-Kind` polled `/api/events` on a 200ms sleep with no
  total budget -- a kind that never arrives meant an infinite loop. Each wait now has a deadline,
  and a kind that does not arrive is a named refusal rather than a silent continuation.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **THE SCRIPT COULD NOT FAIL.** There was no `exit` statement anywhere: a run whose shield absorbed
  nothing printed the numbers and exited 0, so the probe reported success when the thing it exists to
  demonstrate did not happen. The verdict is now deliberate and stated below.

* **`Get-Payload`'s `catch { return $null }` DISCARDED WHY A PAYLOAD WOULD NOT PARSE.** The port
  uses `lib.get_debug_payload`, which distinguishes "no payload" from "a payload that would not
  parse" and refuses by name in the second case.

THE VERDICT, DELIBERATELY
-------------------------
The original had no exit-code contract at all. This port decides one, because a probe that cannot
fail is not a probe:

  * exit 64 -- a REFUSAL: the health gate failed (injector not connected), the setup script failed,
    demo-all got 0 targets or no event, a snapshot or the probe event never arrived, or a payload
    was unparseable. These are "the probe could not run to completion" -- preconditions and
    transport, not findings about the shield.
  * exit 1 -- a FAIL: the probe completed and produced evidence, but the shield did NOT absorb:
    `hit` is false or `shieldAbsorbed` is 0. This is the finding the probe exists to show, and the
    original reported it as a pass.
  * exit 0 -- a PASS: the probe completed, `hit` is true, and `shieldAbsorbed` is positive.

DELIBERATELY UNCHANGED
----------------------
Same recipe in the same order: health gate, OVERLAY-COMBAT toggle, demo-all, BEFORE snapshot,
combat.probe (amount default -150, forceHit, seed 1, fire element at weightPm 1000), AFTER
snapshot. Same display-fill math (`floor(trueRatio * 10) / 10`, raised to 0.1 when the true ratio is
positive but the display rounds to zero). Same operator output, including the closing hints about
10% bar steps and outer-to-inner stack order.
"""
from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "probe-shield-damage"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_AMOUNT = -150
DEFAULT_TIMEOUT = 15
HEALTH_TIMEOUT = 5
DEMO_WAIT_TIMEOUT = 15
SNAPSHOT_WAIT_TIMEOUT = 8
PROBE_WAIT_TIMEOUT = 12
POLL_SEC = 0.2
SETUP_TIMEOUT_SEC = 120

CHEATS_TOGGLE_PATH = "/api/cheats/toggle"
DEMO_ALL_PATH = "/api/debug/shield/demo-all"
SNAPSHOT_PATH = "/api/debug/shield/snapshot"
COMBAT_PROBE_PATH = "/api/debug/combat/probe"

REFUSAL_REASONS = {
    "INVALID-AMOUNT", "INVALID-TIMEOUT", "INJECTOR-NOT-CONNECTED", "SETUP-FAILED",
    "DEMO-ALL-EMPTY", "DEMO-ALL-MISSING", "SNAPSHOT-MISSING", "PROBE-MISSING",
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
        headers={"User-Agent": "FusionRpg-probe-shield-damage/1.0",
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


def wait_kind(base_url: str, after_id: int, kind: str, timeout_sec: int) -> dict | None:
    """Wait for an event of `kind` to land after `after_id`. None when the deadline expires.

    The original's `Wait-Kind` shape is preserved: page forward from the cursor, take the LAST
    matching event on the page, sleep 200ms between pages. The deadline is the port's addition -- the
    original had none, so a kind that never arrived meant an infinite loop.
    """
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, cursor, 100)
        hit = None
        for event in items:
            if event.get("kind") == kind:
                hit = event
        if hit is not None:
            return hit
        if items:
            cursor = int(items[-1].get("id", cursor))
        _SLEEP(POLL_SEC)
    return None


def display_fill(hp: float, max_hp: float) -> tuple[float, float]:
    """(trueRatio, displayFill) -- the original's bar math, preserved exactly.

    `display = floor(trueRatio * 10) / 10`, raised to 0.1 when the true ratio is positive but the
    display rounds to zero: the in-game bar moves in 10% steps, not every HP tick.
    """
    true_ratio = (hp / max_hp) if max_hp > 0 else 0.0
    display = math.floor(true_ratio * 10) / 10
    if true_ratio > 0 and display == 0:
        display = 0.1
    return true_ratio, display


def run_setup_script(base_url: str) -> None:
    """Run the sibling setup-shield-bar-lab.ps1 (owned by another lane; no Python port exists yet).

    The original invoked it with `& "$PSScriptRoot\\setup-shield-bar-lab.ps1" -BaseUrl $BaseUrl`.
    """
    sibling = Path(__file__).resolve().parent / "setup-shield-bar-lab.ps1"
    if not sibling.is_file():
        raise Refusal("SETUP-FAILED",
                      f"the sibling setup script does not exist: {sibling}")
    try:
        proc = _RUN(
            ["pwsh", "-NoProfile", "-NonInteractive", "-File", str(sibling), "-BaseUrl", base_url],
            capture_output=True, text=True, timeout=SETUP_TIMEOUT_SEC)
    except FileNotFoundError as error:
        raise Refusal("SETUP-FAILED", f"pwsh is not on PATH: {error}") from error
    except subprocess.TimeoutExpired as expired:
        raise Refusal("SETUP-FAILED",
                      f"{sibling.name} did not finish within {SETUP_TIMEOUT_SEC}s") from expired
    if proc.returncode != 0:
        tail = ((proc.stdout or "") + (proc.stderr or ""))[-800:]
        raise Refusal("SETUP-FAILED",
                      f"{sibling.name} exited {proc.returncode}:\n{tail}")


def run_probe(base_url: str, amount: int, setup: bool, timeout: int) -> dict:
    """The whole recipe. Returns the evidence; raises Refusal when a precondition fails."""
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      f"injector not connected at {base_url} -- start the game with the FusionRpg "
                      f"injector loaded and enter an Adventure lawn")

    if setup:
        run_setup_script(base_url)

    print("== enable OVERLAY-COMBAT (needed for element probe path; passthrough also absorbs after "
          "injector fix) ==", file=sys.stderr)
    _request(f"{base_url}{CHEATS_TOGGLE_PATH}",
             {"id": "OVERLAY-COMBAT", "enabled": True}, timeout, "POST /api/cheats/toggle")

    print("== ensure shields ==", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}{DEMO_ALL_PATH}", {"amount": 100}, timeout, "POST /api/debug/shield/demo-all")
    demo_event = wait_kind(base_url, after, "debug.shield.demo-all", DEMO_WAIT_TIMEOUT)
    if demo_event is None:
        raise Refusal("DEMO-ALL-MISSING",
                      f"no debug.shield.demo-all event within {DEMO_WAIT_TIMEOUT}s of the POST")
    demo = lib.get_debug_payload(demo_event)
    if not isinstance(demo, dict) or int(demo.get("targetCount") or 0) < 1:
        raise Refusal("DEMO-ALL-EMPTY",
                      "demo-all got 0 targets -- enter Adventure lawn (not Idle)")
    targets = demo.get("targets") or []
    z_ptr = str(targets[-1].get("targetPtr") or "")
    print(f"  targets={demo.get('targetCount')} hitPtr={z_ptr}", file=sys.stderr)

    print("== BEFORE ==", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}{SNAPSHOT_PATH}", {"targetPtr": z_ptr}, timeout,
             "POST /api/debug/shield/snapshot")
    before_event = wait_kind(base_url, after, "debug.shield.snapshot", SNAPSHOT_WAIT_TIMEOUT)
    if before_event is None:
        raise Refusal("SNAPSHOT-MISSING",
                      f"no debug.shield.snapshot event within {SNAPSHOT_WAIT_TIMEOUT}s of the POST")
    before = lib.get_debug_payload(before_event)
    before_owners = before.get("owners") if isinstance(before, dict) else None
    for owner in before_owners or []:
        print(f"  hp={owner.get('hp')}/{owner.get('maxHp')}", file=sys.stderr)

    print(f"== combat.probe amount={amount} ==", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}{COMBAT_PROBE_PATH}",
             {"amount": amount, "targetPtr": z_ptr, "forceHit": True, "seed": 1,
              "elementPayload": [{"element": "fire", "weightPm": 1000}]},
             timeout, "POST /api/debug/combat/probe")
    probe_event = wait_kind(base_url, after, "debug.combat.probe", PROBE_WAIT_TIMEOUT)
    if probe_event is None:
        raise Refusal("PROBE-MISSING",
                      f"no debug.combat.probe event within {PROBE_WAIT_TIMEOUT}s of the POST")
    probe = lib.get_debug_payload(probe_event)
    if not isinstance(probe, dict):
        raise Refusal("PROBE-MISSING", "the combat.probe event carried no readable payload")
    print(f"  source={probe.get('source')} hit={probe.get('hit')} "
          f"shieldAbsorbed={probe.get('shieldAbsorbed')} appliedDelta={probe.get('appliedDelta')}",
          file=sys.stderr)

    print("== AFTER ==", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}{SNAPSHOT_PATH}", {"targetPtr": z_ptr}, timeout,
             "POST /api/debug/shield/snapshot")
    after_event = wait_kind(base_url, after, "debug.shield.snapshot", SNAPSHOT_WAIT_TIMEOUT)
    if after_event is None:
        raise Refusal("SNAPSHOT-MISSING",
                      f"no debug.shield.snapshot event within {SNAPSHOT_WAIT_TIMEOUT}s of the POST")
    after_snap = lib.get_debug_payload(after_event)
    after_owners = after_snap.get("owners") if isinstance(after_snap, dict) else None
    fills: list[tuple[float, float]] = []
    for owner in after_owners or []:
        true_ratio, display = display_fill(float(owner.get("hp") or 0),
                                           float(owner.get("maxHp") or 0))
        fills.append((true_ratio, display))
        print(f"  hp={owner.get('hp')}/{owner.get('maxHp')} true={true_ratio:.2f} "
              f"displayFill={display:.1f}", file=sys.stderr)
        for stack in owner.get("stacks") or []:
            print(f"    {stack.get('element')} {stack.get('hp')}/{stack.get('maxHp')}",
                  file=sys.stderr)

    print("In-game: bar fill length uses 10% steps (displayFill), not every HP tick.", file=sys.stderr)
    print("Repeat probe to drain further; stacks break outer→inner (fire then ice then earth).",
          file=sys.stderr)

    return {"target_ptr": z_ptr, "demo": demo, "before_owners": before_owners or [],
            "probe": probe, "after_owners": after_owners or [], "fills": fills}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="probe-shield-damage",
        description="Damage RPG shields and show bar fillRatio shrink "
                    "(replaces probe-shield-damage.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--amount", type=int, default=DEFAULT_AMOUNT,
                        help=f"damage to deal, MUST be negative (default {DEFAULT_AMOUNT})")
    parser.add_argument("--setup", action="store_true",
                        help="run the sibling setup-shield-bar-lab.ps1 first")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.amount >= 0:
        return _refuse("INVALID-AMOUNT",
                       f"--amount {args.amount} is not negative damage (e.g. -150)", args.json)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    try:
        evidence = run_probe(base_url, args.amount, args.setup, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    probe = evidence["probe"]
    hit = bool(probe.get("hit"))
    absorbed = float(probe.get("shieldAbsorbed") or 0)
    # The deliberate verdict: the probe completed AND the shield absorbed. The original exited 0
    # here even when the shield absorbed nothing.
    passed = hit and absorbed > 0
    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                "verdict": "PASS" if passed else "FAIL",
                "exitCode": EXIT_OK if passed else EXIT_FAILED,
                "hit": hit, "shieldAbsorbed": absorbed,
                "appliedDelta": probe.get("appliedDelta"), "targetPtr": evidence["target_ptr"]}
    if args.json:
        print(json.dumps(envelope, indent=2))
    return EXIT_OK if passed else EXIT_FAILED


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
