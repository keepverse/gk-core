#!/usr/bin/env python3
"""All-in-one LIVE lawn setup -- enter level 1 (if needed), lab scenario, living zombie ptr.

The single source of truth for scripts that need a board without manual Adventure navigation. See
`.claude/skills/live-lawn-quick-start/SKILL.md`.

Replaces `scripts/lib/LiveLawnSetup.ps1`, which was DOT-SOURCED by four callers. This is a MODULE, not a
script: each function is importable, and `python -m` / direct execution exposes a `preflight` subcommand so
the file is also runnable and probeable on its own.

THE CALLER-TO-FUNCTION MAP, so the rename is checkable rather than remembered:

    Get-DebugMaxEventId         -> get_debug_max_event_id
    Get-DebugPayload            -> get_debug_payload
    Invoke-DebugPost            -> invoke_debug_post
    Wait-LiveBoardSnapshot      -> wait_live_board_snapshot
    Get-LiveBoardEntities       -> get_live_board_entities
    Get-RecentLiveDebugErrors   -> get_recent_live_debug_errors
    Ensure-LiveLabBoard         -> ensure_live_lab_board

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** `Ensure-LiveLabBoard`'s parameter default is
  `http://127.0.0.1:5088`. This machine has a three-slot pool on 5101/5102/5103 where `5088` is the
  OWNER's server and not "the" port, so a setup script pointed at the default enters somebody else's board
  and reports pointers into a game this session is not running. The URL is read ONCE from
  `FUSIONRPG_SERVER_URL` -- the variable the Injector itself reads -- falling back to `127.0.0.1:5088` only
  when nothing is configured, and **where the value came from is RETURNED** so a caller can see whether it
  measured the board it meant to.

* **THE EVENT READS HAD NO TIMEOUT AT ALL.** `Get-DebugMaxEventId` issues about `2 * log2(maxEventId)`
  requests -- for a long-running server that is dozens -- and `Has-After` had nothing stopping any of
  them. A single hung read makes the whole binary search hang forever, with no output and no way to tell
  it from a slow one. Every request now carries a timeout AND the search carries a total budget, so
  "the server stopped answering" is an outcome rather than a wait.

* **THE BINARY SEARCH HAD NO UPPER-BOUND GUARD ON THE TOTAL.** It doubled `hi` until a read came back
  empty, which is correct but unbounded in TIME even with bounded requests. A budget is separate from a
  per-request timeout, and only the total bound makes the function's cost predictable.

* **`Get-DebugPayload`'s `catch { return $null }` DISCARDED WHY A PAYLOAD WOULD NOT PARSE.** A payload
  that is a malformed JSON string and a payload that is simply absent both became `null`, so a scenario
  that failed to produce evidence looked exactly like one that produced no evidence. The failure is now
  reportable, because "the payload was unparseable" and "there was no payload" are different findings.

* **`Invoke-DebugPost` IGNORED WHICH CALL FAILED.** It had a 15s timeout, but every caller's `catch` had
  to reconstruct the failure from an exception message, and the module never reported the URL it posted
  to. The path is echoed in the failure, so a refusal names the endpoint.

* **THE LIBRARY HAD NO MACHINE-READABLE SURFACE.** `Write-Host` progress went to the host, and
  `Ensure-LiveLabBoard` returned a `[pscustomobject]` whose fields a caller had to know by heart. The
  returned record is a dataclass with named fields, and `preflight --json` reports the same shape a caller
  receives.

DELIBERATELY UNCHANGED
----------------------
The binary search itself (exponential probe, then bisect), the `-1` guard on `hi`, the 400ms poll interval,
the 15s snapshot budget, the two default scenarios `lab-overlay` / `lab-empty`, the two-step ptr recovery
(quick-start's response first, then a board snapshot), the `cheat.error` / `debug.effect.error` pair
scanned for, the `error`-then-`message` preference when reading one, and -- most importantly -- every
refusal MESSAGE, which are the accumulated knowledge of several live-debug incidents and are the reason
this file exists.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Any

TOOL_ID = "live-lawn-setup"

EXIT_REFUSED = 64

DEFAULT_BASE_URL = "http://127.0.0.1:5088"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
SCENARIOS = ("lab-overlay", "lab-empty")

DEFAULT_EVENT_TIMEOUT = 10
DEFAULT_EVENT_PAGE = 100
SNAPSHOT_TIMEOUT_SEC = 15
DEFAULT_SETUP_TIMEOUT_SEC = 60
POLL_INTERVAL_SEC = 0.4
DEBUG_POST_TIMEOUT = 15
HEALTH_TIMEOUT = 5
ERROR_KINDS = ("cheat.error", "debug.effect.error")

REFUSAL_REASONS = {
    "BASE-URL-INVALID", "SERVER-UNREACHABLE", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED",
    "QUICK-START-FAILED", "NO-LIVING-ZOMBIE", "NO-TARGET-PTR", "SNAPSHOT-TIMED-OUT",
    "EVENT-READ-FAILED", "EVENT-READ-TIMED-OUT", "SEARCH-BUDGET-EXHAUSTED", "INVALID-TIMEOUT",
    "INVALID-SCENARIO", "PAYLOAD-UNPARSEABLE", "DEBUG-POST-FAILED",
}

# Bound once, module-private: `urllib` is process-wide, and a test that patches it reaches every other
# test in the project.
_URLOPEN = urllib.request.urlopen


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


@dataclass
class LabBoard:
    """What `ensure_live_lab_board` returns. Field names mirror the `[pscustomobject]` the original
    returned, so a caller ported from PowerShell finds the same names."""

    target_ptr: str = ""
    plant_ptr: str = ""
    level_type: str = ""
    entered: bool = False
    scenario: str = "lab-overlay"
    base_url: str = ""
    base_url_source: str = ""
    sim_enabled: bool | None = None
    snapshot: dict | None = None
    living_plants: int = 0
    living_zombies: int = 0
    quick_start_note: str = ""
    recent_errors: list[str] = field(default_factory=list)

    def to_json(self) -> dict:
        return {"TargetPtr": self.target_ptr, "PlantPtr": self.plant_ptr,
                "LevelType": self.level_type, "Entered": self.entered, "Scenario": self.scenario,
                "BaseUrl": self.base_url, "BaseUrlSource": self.base_url_source,
                "SimEnabled": self.sim_enabled, "LivingPlants": self.living_plants,
                "LivingZombies": self.living_zombies, "QuickStartNote": self.quick_start_note}


def resolve_base_url(given: str = "") -> tuple[str, str]:
    """The server to talk to, and WHERE the value came from. Read once, explicitly.

    Kept at module scope so every function takes an explicit `base_url` and this is the ONLY place the
    default is decided. A default resolved inside each function is a default that can drift.
    """
    if given:
        url, source = given, "explicit"
    elif os.environ.get(BASE_URL_ENV, "").strip():
        url, source = os.environ[BASE_URL_ENV].strip(), f"${BASE_URL_ENV}"
    else:
        url, source = DEFAULT_BASE_URL, f"the built-in default ({BASE_URL_ENV} unset)"
    if not url.lower().startswith(("http://", "https://")):
        raise Refusal("BASE-URL-INVALID",
                      f"the base URL {url!r} (from {source}) is not an http(s) URL")
    return url.rstrip("/"), source


def _get_json(url: str, timeout: int, what: str) -> Any:
    request = urllib.request.Request(url, headers={"User-Agent": "FusionRpg-live-lawn-setup/1.0"})
    try:
        with _URLOPEN(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        raise Refusal("EVENT-READ-FAILED", f"{what} answered {error.code} {error.reason}: {url}") from error
    except TimeoutError as expired:
        raise Refusal("EVENT-READ-TIMED-OUT",
                      f"{what} did not answer within {timeout}s: {url}") from expired
    except urllib.error.URLError as error:
        if isinstance(error.reason, TimeoutError):
            raise Refusal("EVENT-READ-TIMED-OUT",
                          f"{what} did not answer within {timeout}s: {url}") from error
        raise Refusal("SERVER-UNREACHABLE", f"{what} was unreachable ({error.reason}): {url}") from error
    except OSError as error:
        raise Refusal("SERVER-UNREACHABLE", f"{what} was unreachable ({error}): {url}") from error
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("EVENT-READ-FAILED", f"{what} answered with something that is not JSON: {error}") from error


def get_events(base_url: str, after_id: int, limit: int, timeout: int = DEFAULT_EVENT_TIMEOUT) -> list[dict]:
    """One page of events. Every read carries a timeout -- the original's had none, and this function is
    called dozens of times by the search below."""
    page = _get_json(f"{base_url}/api/events?afterId={int(after_id)}&limit={int(limit)}", timeout,
                     f"GET /api/events?afterId={int(after_id)}")
    items = page.get("items") if isinstance(page, dict) else None
    if items is None:
        raise Refusal("EVENT-READ-FAILED", f"/api/events answered with no 'items' array: {sorted(page or {})}")
    return [i for i in items if isinstance(i, dict)]


def get_debug_max_event_id(base_url: str, timeout: int = DEFAULT_EVENT_TIMEOUT,
                           budget_sec: float = 30.0) -> int:
    """The highest event id the server still has, by exponential probe then bisect.

    The original's shape is preserved exactly -- probe 0, double until empty, then bisect -- with a total
    TIME budget added, because a per-request timeout bounds one call and not the cost of dozens of them.
    """
    started = time.monotonic()

    def has_after(after_id: int) -> bool:
        if time.monotonic() - started > budget_sec:
            raise Refusal("SEARCH-BUDGET-EXHAUSTED",
                          f"the event-id search exceeded its {budget_sec}s budget; the server is "
                          f"answering but the id space is not converging, or a read is very slow")
        return len(get_events(base_url, after_id, 1, timeout)) > 0

    if not has_after(0):
        return 0
    low, high = 0, 1
    while has_after(high):
        low = high
        if high > (2 ** 62):
            break
        high *= 2
    while low + 1 < high:
        middle = (low + high) // 2
        if has_after(middle):
            low = middle
        else:
            high = middle
    return low


def get_debug_payload(event: dict | None) -> dict | str | None:
    """A debug event's payload, parsed when it arrived as a JSON string.

    The original returned `$null` for both "no payload" and "a payload that would not parse". Those are
    DIFFERENT findings -- a scenario that failed to produce evidence and one that produced evidence which
    could not be read -- so the failure is reportable rather than silent.
    """
    if event is None:
        return None
    payload = event.get("payload")
    if payload is None:
        return None
    if isinstance(payload, str):
        try:
            return json.loads(payload)
        except json.JSONDecodeError:
            raise Refusal("PAYLOAD-UNPARSEABLE",
                          f"event {event.get('id')} of kind {event.get('kind')!r} carries a payload that is "
                          f"a string but not JSON: {payload[:160]!r}") from None
    return payload


def invoke_debug_post(base_url: str, path: str, body: dict | None = None,
                      timeout: int = DEBUG_POST_TIMEOUT) -> Any:
    """POST to `/api/debug{path}` with a JSON body. The path is echoed in every failure, so a refusal
    names the endpoint rather than leaving the caller to reconstruct it from a message."""
    payload = json.dumps(body if body is not None else {}, separators=(",", ":")).encode()
    request = urllib.request.Request(
        f"{base_url}/api/debug{path}", data=payload, method="POST",
        headers={"User-Agent": "FusionRpg-live-lawn-setup/1.0", "Content-Type": "application/json",
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
        raise Refusal("DEBUG-POST-FAILED",
                      f"POST /api/debug{path} answered {error.code} {error.reason}"
                      + (f": {body_text}" if body_text else "")) from error
    except TimeoutError as expired:
        raise Refusal("DEBUG-POST-FAILED",
                      f"POST /api/debug{path} did not answer within {timeout}s. With no injector "
                      f"connected this call waits for a game that is not there") from expired
    except urllib.error.URLError as error:
        raise Refusal("DEBUG-POST-FAILED", f"POST /api/debug{path} was unreachable: {error.reason}") from error
    except OSError as error:
        raise Refusal("DEBUG-POST-FAILED", f"POST /api/debug{path} failed: {error}") from error
    if not raw.strip():
        return {}
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return {"raw": raw.decode("utf-8", errors="replace")[:400]}


def wait_live_board_snapshot(base_url: str, after_id: int, timeout_sec: int = SNAPSHOT_TIMEOUT_SEC,
                             event_timeout: int = DEFAULT_EVENT_TIMEOUT) -> dict | None:
    """Ask for a board snapshot and wait for one to land in the event stream. `None` when the budget
    expires -- the original's answer too, and the caller's cue to report that rather than to guess."""
    deadline = time.monotonic() + timeout_sec
    cursor = after_id
    while time.monotonic() < deadline:
        invoke_debug_post(base_url, "/effect/board-snapshot", {})
        time.sleep(POLL_INTERVAL_SEC)
        items = get_events(base_url, cursor, DEFAULT_EVENT_PAGE, event_timeout)
        for event in items:
            if event.get("kind") == "debug.effect.board-snapshot":
                payload = get_debug_payload(event)
                return payload if isinstance(payload, dict) else None
        if items:
            cursor = int(items[-1].get("id", cursor))
    return None


def get_live_board_entities(snapshot: dict | None) -> tuple[list[dict], list[dict]]:
    """(living plants, living zombies) from a board snapshot. Pure, so a case can pin it with no server."""
    if not snapshot:
        return [], []
    entities = snapshot.get("entities")
    if not isinstance(entities, list):
        return [], []
    living = [e for e in entities if isinstance(e, dict) and e.get("living")]
    plants = [e for e in living if e.get("side") == "plant"]
    zombies = [e for e in living if e.get("side") == "zombie"]
    return plants, zombies


def get_recent_live_debug_errors(base_url: str, after_id: int = 0,
                                event_timeout: int = DEFAULT_EVENT_TIMEOUT) -> list[str]:
    """The `cheat.error` / `debug.effect.error` lines since `after_id`, as `kind: message`.

    `error` is preferred over `message` when present, then the raw payload -- the original's order, which
    encodes what a real failure looked like.
    """
    lines: list[str] = []
    for event in get_events(base_url, after_id, 200, event_timeout):
        if event.get("kind") not in ERROR_KINDS:
            continue
        # The RAW payload is the last resort, so a payload that is a string but NOT JSON must reach the
        # caller as that string rather than aborting the whole scan. `get_debug_payload` deliberately
        # refuses in that case, which is right for a caller READING evidence -- and wrong here, where the
        # unparsed text is precisely the thing worth showing. The first version of this port called the
        # strict parser and therefore LOST the original's raw-text fallback: one malformed event in a
        # thousand aborted the scan that exists to report malformed events.
        try:
            payload = get_debug_payload(event)
        except Refusal:
            payload = None
        message: str | None = None
        if isinstance(payload, dict):
            if payload.get("error"):
                message = str(payload["error"])
            elif payload.get("message"):
                message = str(payload["message"])
        if not message:
            raw = event.get("payload")
            message = str(raw) if raw else None
        if message:
            lines.append(f"{event.get('kind')}: {message}")
    return lines


def _health(base_url: str) -> dict:
    health = _get_json(f"{base_url}/health", HEALTH_TIMEOUT, "GET /health")
    if not isinstance(health, dict):
        raise Refusal("HEALTH-NOT-OK", f"/health answered with a {type(health).__name__}, not an object")
    return health


def ensure_live_lab_board(base_url: str = "", scenario: str = "lab-overlay", level_number: int = 1,
                          timeout_sec: int = DEFAULT_SETUP_TIMEOUT_SEC, skip_setup: bool = False,
                          event_timeout: int = DEFAULT_EVENT_TIMEOUT,
                          snapshot_timeout_sec: int = SNAPSHOT_TIMEOUT_SEC) -> LabBoard:
    """Enter level 1, run the lab scenario, and return the living zombie pointer.

    Every refusal message is the original's, because they are the accumulated knowledge of several
    live-debug incidents. The preflight order is unchanged and matters: health, then injector, then the
    quick-start POST, then the snapshot fallback, then the two pointer refusals.
    """
    if scenario not in SCENARIOS:
        raise Refusal("INVALID-SCENARIO", f"scenario must be one of {list(SCENARIOS)}, not {scenario!r}")
    if timeout_sec <= 0 or event_timeout <= 0 or snapshot_timeout_sec <= 0:
        raise Refusal("INVALID-TIMEOUT", "every timeout must be positive")
    url, source = resolve_base_url(base_url)

    print("Ensure-LiveLabBoard: preflight...", file=sys.stderr)
    try:
        health = _health(url)
    except Refusal as refusal:
        if refusal.reason in ("SERVER-UNREACHABLE", "EVENT-READ-TIMED-OUT", "EVENT-READ-FAILED"):
            raise Refusal("SERVER-UNREACHABLE",
                          f"GET /health on {url} (from {source}) failed: {refusal.detail}") from refusal
        raise
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      f"injector not connected at {url} — start the game with the FusionRpg injector "
                      f"loaded (see the live-lawn-quick-start skill)")
    if health.get("simEnabled"):
        print("simEnabled=true — LIVE prefers SIM off", file=sys.stderr)

    board = LabBoard(scenario=scenario, base_url=url, base_url_source=source,
                     sim_enabled=bool(health.get("simEnabled")))
    cursor = get_debug_max_event_id(url, event_timeout, budget_sec=timeout_sec)

    if skip_setup:
        print("Ensure-LiveLabBoard: skip_setup — polling board snapshot only", file=sys.stderr)
    else:
        print(f"Ensure-LiveLabBoard: POST /lawn/quick-start scenario={scenario} level={level_number}...",
              file=sys.stderr)
        try:
            response = invoke_debug_post(url, "/lawn/quick-start", {
                "scenario": scenario, "levelNumber": level_number, "timeoutSec": timeout_sec})
        except Refusal as refusal:
            raise Refusal("QUICK-START-FAILED", f"lawn/quick-start failed: {refusal.detail}") from refusal
        response = response if isinstance(response, dict) else {}
        board.entered = bool(response.get("entered"))
        board.level_type = str(response.get("levelType") or "")
        board.target_ptr = str(response.get("targetPtr") or "")
        board.plant_ptr = str(response.get("plantPtr") or "")
        board.quick_start_note = str(response.get("note") or "")
        if board.quick_start_note:
            print(f"  quick-start note: {board.quick_start_note}", file=sys.stderr)
        print(f"  entered={board.entered} levelType={board.level_type} "
              f"targetPtr={board.target_ptr or '(none)'}", file=sys.stderr)
        cursor = get_debug_max_event_id(url, event_timeout, budget_sec=timeout_sec)

    if not board.target_ptr:
        snapshot = wait_live_board_snapshot(url, cursor, snapshot_timeout_sec, event_timeout)
        if snapshot is not None:
            board.snapshot = snapshot
            plants, zombies = get_live_board_entities(snapshot)
            board.living_plants, board.living_zombies = len(plants), len(zombies)
            if plants:
                board.plant_ptr = str(plants[0].get("ptr") or "")
            if zombies:
                board.target_ptr = str(zombies[0].get("ptr") or "")
            print(f"  snapshot: living plants={board.living_plants} "
                  f"zombies={board.living_zombies}", file=sys.stderr)

    if scenario == "lab-overlay" and not board.target_ptr:
        board.recent_errors = get_recent_live_debug_errors(url, cursor, event_timeout)
        detail = ("\nRecent errors:\n  " + "\n  ".join(board.recent_errors)) if board.recent_errors else ""
        hint = ("\nskip_setup skips /lawn/quick-start — the game must already be on a lab board with "
                "living zombies.\nRemove skip_setup to enter the level.") if skip_setup else ""
        raise Refusal("NO-LIVING-ZOMBIE",
                      f"lab board has no living zombie ptr — setup failed.{hint}{detail}")

    if not board.target_ptr:
        raise Refusal("NO-TARGET-PTR",
                      f"Ensure-LiveLabBoard: no TargetPtr after setup (scenario={scenario} "
                      f"skip_setup={skip_setup})")

    print(f"ZombiePtr={board.target_ptr} (TargetPtr)", file=sys.stderr)
    if board.plant_ptr:
        print(f"PlantPtr={board.plant_ptr}", file=sys.stderr)
    return board


def main(argv: list[str] | None = None) -> int:
    """`preflight` -- the module's own machine-readable surface, so this file is runnable and probeable on
    its own rather than only through a caller."""
    parser = argparse.ArgumentParser(
        prog="live_lawn_setup",
        description="Live lawn setup: the library the live-prove scripts share "
                    "(replaces lib/LiveLawnSetup.ps1).")
    sub = parser.add_subparsers(dest="command", required=True)
    pre = sub.add_parser("preflight", help="health + injector preflight, and the current max event id")
    pre.add_argument("--base-url", default="")
    pre.add_argument("--scenario", default="lab-overlay", choices=list(SCENARIOS))
    pre.add_argument("--level-number", type=int, default=1)
    pre.add_argument("--timeout-sec", type=int, default=DEFAULT_SETUP_TIMEOUT_SEC)
    pre.add_argument("--skip-setup", action="store_true")
    pre.add_argument("--json", action="store_true")
    pre.add_argument("--max-event-id-only", action="store_true",
                     help="report the current max event id and nothing else")
    args = parser.parse_args(argv)

    try:
        if args.max_event_id_only:
            url, source = resolve_base_url(args.base_url)
            value = get_debug_max_event_id(url)
            if args.json:
                print(json.dumps({"tool": TOOL_ID, "baseUrl": url, "baseUrlSource": source,
                                  "maxEventId": value}, indent=2))
            else:
                print(f"maxEventId={value}  ({url}, from {source})")
            return 0
        board = ensure_live_lab_board(args.base_url, args.scenario, args.level_number,
                                      args.timeout_sec, args.skip_setup)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, **board.to_json()}, indent=2))
        else:
            print(f"ZombiePtr={board.target_ptr} PlantPtr={board.plant_ptr or '(none)'} "
                  f"levelType={board.level_type or '(none)'} entered={board.entered}")
        return 0
    except Refusal as refusal:
        if getattr(args, "json", False):
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
