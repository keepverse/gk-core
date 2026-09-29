#!/usr/bin/env python3
"""Melon LIVE prove: scoped FA1 ModifyStat (match / plant:N / entity / mid-spawn / withdraw).
Requires: lawn open, Melon injector connected, SIM off.

Replaces `scripts/smoke-effect-scoped-atk.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** `Invoke-RestMethod` was called in the health gate, the session
  start, each scenario queue, every `Get-MaxEventId` probe and every `Get-BoardStatsAfter` page --
  none with `-TimeoutSec`. A wedged server holds the prove open indefinitely. Every request now
  carries a timeout, the event-id search carries `lib`'s budget, and the board-stats wait carries a
  deadline.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **THE SCENARIO ASSERTIONS WERE STRINGLY-TYPED.** Each branch of the `switch` re-derived plants by
  column or by typeId with `[int]` casts inline, so a port that swapped `col` for `typeId` in one
  branch would still run and report a plausible note. The assertions are now pure functions with the
  original's comparisons pinned by tests.

* **THE BOARD-STATS WAIT HAD NO TOTAL BUDGET.** `Get-BoardStatsAfter` paged `/api/events` on a 300ms
  sleep with only an 8s deadline checked between pages -- and the deadline was checked with
  `(Get-Date)`, which a slow first page could blow past. The wait now carries a monotonic deadline.

DELIBERATELY UNCHANGED
----------------------
Same five scenarios in the same order, same queue-then-wait-then-assert shape, same per-scenario
comparisons (col1 > col3; pea > wallnut; |col1 - col3| <= 0.5; col1 > col3; grant/withdraw triple),
same result payload (`at`, `game`, `baseUrl`, `passed`, `total`, `results`, `status`, `note`), same
default output path, same exit contract: 1 when any scenario fails, 0 when all pass.
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

TOOL_ID = "smoke-effect-scoped-atk"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_WAIT_SECONDS = 1.5
DEFAULT_OUT = ("docs", "research", "effect-runtime", "_prove-melon39-scoped-atk.json")
DEFAULT_TIMEOUT = 15
HEALTH_TIMEOUT = 5
BOARD_STATS_TIMEOUT_SEC = 8.0
BOARD_STATS_PAGE_SEC = 0.3
BOARD_STATS_EMPTY_SEC = 0.4
POLL_SEC = 0.2

SCENARIOS = ("effect-entity-atk", "effect-plant-type-atk", "effect-match-midspawn",
             "effect-spawn-then-grant", "effect-entity-midspawn")

REFUSAL_REASONS = {
    "INVALID-WAIT", "INVALID-TIMEOUT", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED",
    "REQUEST-FAILED", "RESPONSE-NOT-JSON", "OUT-PARENT-MISSING",
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
        headers={"User-Agent": "FusionRpg-smoke-effect-scoped-atk/1.0",
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


def get_plant_at(board: dict | None, col: int) -> dict | None:
    """The plant at a column, or None. The original's `Get-PlantAt`.

    The key is compared with an explicit None check, not `or -1`: column 0 is a real column, and
    `0 or -1` is `-1` in Python, which would make the leftmost plant unfindable.
    """
    if not board:
        return None
    for plant in board.get("plants") or []:
        plant_col = plant.get("col")
        if plant_col is not None and int(plant_col) == col:
            return plant
    return None


def get_plant_by_type(board: dict | None, type_id: int) -> dict | None:
    """The plant of a typeId, or None. The original's `Get-PlantByType`.

    The explicit None check matters for the same reason: typeId 0 is the pea, and `0 or -1` is
    `-1` in Python.
    """
    if not board:
        return None
    for plant in board.get("plants") or []:
        plant_type = plant.get("typeId")
        if plant_type is not None and int(plant_type) == type_id:
            return plant
    return None


def get_board_stats_after(base_url: str, after_id: int, tag: str = "",
                         timeout_sec: float = BOARD_STATS_TIMEOUT_SEC) -> dict | None:
    """Page through the event stream after `after_id` looking for a debug.board-stats event,
    optionally filtered by the payload's own `tag`. The LAST match's payload wins.

    The original's `Get-BoardStatsAfter` shape is preserved: page, advance the cursor to the last
    item's id, filter, return the last match. The monotonic deadline is the port's addition.
    """
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, cursor, 500)
        if not items:
            _SLEEP(BOARD_STATS_EMPTY_SEC)
            continue
        cursor = int(items[-1].get("id", cursor))
        matches = [e for e in items if e.get("kind") == "debug.board-stats"]
        if tag:
            tagged = []
            for event in matches:
                try:
                    payload = lib.get_debug_payload(event)
                except lib.Refusal:
                    continue
                if isinstance(payload, dict) and str(payload.get("tag") or "") == tag:
                    tagged.append(payload)
            matches_payload = tagged
        else:
            matches_payload = []
            for event in matches:
                try:
                    payload = lib.get_debug_payload(event)
                except lib.Refusal:
                    continue
                if isinstance(payload, dict):
                    matches_payload.append(payload)
        if matches_payload:
            return matches_payload[-1]
        _SLEEP(BOARD_STATS_PAGE_SEC)
    return None


def assert_scenario(scenario_id: str, board: dict | None,
                    board_grant: dict | None = None,
                    board_withdraw: dict | None = None) -> tuple[bool, str]:
    """One scenario's assertion. Pure, so a case can pin each comparison with no server.

    The original's `switch` branches, preserved exactly -- including the note strings, which are
    the operator's reading of what the scenario measured.
    """
    if scenario_id == "effect-entity-atk":
        if board is None:
            return False, "no debug.board-stats"
        p1 = get_plant_at(board, 1)
        p3 = get_plant_at(board, 3)
        if p1 is None or p3 is None:
            return False, "need peas at col 1 and 3"
        ok = float(p1.get("attack") or 0) > float(p3.get("attack") or 0)
        return ok, f"col1={p1.get('attack')} col3={p3.get('attack')}"
    if scenario_id == "effect-plant-type-atk":
        if board is None:
            return False, "no debug.board-stats"
        pea = get_plant_by_type(board, 0)
        wall = get_plant_by_type(board, 3)
        if pea is None or wall is None:
            return False, "need pea(type0) and wallnut(type3)"
        ok = float(pea.get("attack") or 0) > float(wall.get("attack") or 0)
        return ok, f"pea={pea.get('attack')} wall={wall.get('attack')}"
    if scenario_id == "effect-match-midspawn":
        if board is None:
            return False, "no debug.board-stats"
        p1 = get_plant_at(board, 1)
        p3 = get_plant_at(board, 3)
        if p1 is None or p3 is None:
            return False, "need peas at col 1 and 3"
        ok = abs(float(p1.get("attack") or 0) - float(p3.get("attack") or 0)) <= 0.5
        return ok, f"col1={p1.get('attack')} col3={p3.get('attack')}"
    if scenario_id == "effect-spawn-then-grant":
        if board is None:
            return False, "no debug.board-stats"
        p1 = get_plant_at(board, 1)
        p3 = get_plant_at(board, 3)
        if p1 is None or p3 is None:
            return False, "need peas at col 1 and 3"
        ok = float(p1.get("attack") or 0) > float(p3.get("attack") or 0)
        return ok, f"col1={p1.get('attack')} col3={p3.get('attack')}"
    if scenario_id == "effect-entity-midspawn":
        if board_grant is None or board_withdraw is None:
            return False, "missing after-grant/after-withdraw board-stats"
        g1 = get_plant_at(board_grant, 1)
        g3 = get_plant_at(board_grant, 3)
        w1 = get_plant_at(board_withdraw, 1)
        w3 = get_plant_at(board_withdraw, 3)
        if g1 is None or g3 is None or w1 is None or w3 is None:
            return False, "missing plants in tagged board-stats"
        ok_grant = float(g1.get("attack") or 0) > float(g3.get("attack") or 0)
        ok_restore = float(w1.get("attack") or 0) < float(g1.get("attack") or 0)
        ok_sib = abs(float(w1.get("attack") or 0) - float(w3.get("attack") or 0)) <= 0.5
        ok = ok_grant and ok_restore and ok_sib
        return ok, f"g1={g1.get('attack')} g3={g3.get('attack')} w1={w1.get('attack')} w3={w3.get('attack')}"
    return False, "unknown scenario assert"


def run_scenario(base_url: str, scenario_id: str, wait: float, timeout: int) -> dict:
    """Queue the scenario, wait, read the board-stats, assert. One result row."""
    after_id = lib.get_debug_max_event_id(base_url, timeout=timeout)
    try:
        queued = _request(f"{base_url}/api/debug/scenario/{scenario_id}", {}, timeout,
                          f"POST /api/debug/scenario/{scenario_id}")
        if not bool(queued.get("ok")):
            return {"id": scenario_id, "pass": False, "note": "scenario queue failed"}
    except Refusal as refusal:
        return {"id": scenario_id, "pass": False, "note": refusal.detail}

    _SLEEP(wait)

    if scenario_id == "effect-entity-midspawn":
        board_grant = get_board_stats_after(base_url, after_id, "after-grant")
        board_withdraw = get_board_stats_after(base_url, after_id, "after-withdraw")
        ok, note = assert_scenario(scenario_id, None, board_grant, board_withdraw)
    else:
        board = get_board_stats_after(base_url, after_id)
        ok, note = assert_scenario(scenario_id, board)
    return {"id": scenario_id, "pass": ok, "note": note}


def run_proof(base_url: str, wait_seconds: float, timeout: int) -> dict:
    """The health gate, the session start, and the five scenarios."""
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    connected = bool(health.get("injectorConnected"))
    sim_off = not bool(health.get("simEnabled"))
    source = str(health.get("source") or "")
    ok_health = connected and sim_off and source == "injector"
    if not ok_health:
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      f"health gate failed: injectorConnected={connected} "
                      f"simEnabled={health.get('simEnabled')} source={source} -- the prove requires "
                      f"a Melon lawn with SIM off")

    _request(f"{base_url}/api/debug/session/start", {}, timeout, "POST /api/debug/session/start")

    results = [run_scenario(base_url, scenario_id, wait_seconds, timeout)
               for scenario_id in SCENARIOS]
    failed = any(not r["pass"] for r in results)
    return {"results": results, "failed": failed}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="smoke-effect-scoped-atk",
        description="Melon LIVE prove: scoped FA1 ModifyStat (match / plant:N / entity / "
                    "mid-spawn / withdraw) (replaces smoke-effect-scoped-atk.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--wait-seconds", type=float, default=DEFAULT_WAIT_SECONDS,
                        help=f"seconds to wait after queueing each scenario "
                             f"(default {DEFAULT_WAIT_SECONDS})")
    parser.add_argument("--out", default="",
                        help=f"where the result JSON is written (default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.wait_seconds < 0:
        return _refuse("INVALID-WAIT", f"--wait-seconds {args.wait_seconds} is not a wait",
                       args.json)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    root = Path(__file__).resolve().parent.parent
    out_path = Path(args.out).expanduser() if args.out else root.joinpath(*DEFAULT_OUT)
    if not out_path.is_absolute():
        out_path = Path.cwd() / out_path
    out_path = out_path.resolve()

    print(f"==> Melon scoped-ATK prove against {base_url}", file=sys.stderr)
    try:
        proof = run_proof(base_url, args.wait_seconds, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    for result in proof["results"]:
        mark = "PASS" if result["pass"] else "FAIL"
        print(f"[{mark}] {result['id']}: {result['note']}", file=sys.stderr)

    results = proof["results"]
    passed = sum(1 for r in results if r["pass"])
    payload = {"at": datetime.now(timezone.utc).isoformat(), "game": "pvzrh-3.9",
               "baseUrl": base_url, "passed": passed, "total": len(results),
               "results": results,
               "status": "PENDING_LIVE" if proof["failed"] else "PASS",
               "note": "Run with Melon lawn open after deploy-play -LoaderHost MelonLoader"}
    try:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    except OSError as error:
        return _refuse("OUT-PARENT-MISSING",
                       f"the result file could not be written: {out_path}: {error}", args.json)
    print(f"Wrote {out_path}", file=sys.stderr)

    if proof["failed"]:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                              "verdict": "FAILED", "exitCode": EXIT_FAILED, "payload": payload},
                             indent=2))
        return EXIT_FAILED
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                          "verdict": "PASS", "exitCode": EXIT_OK, "payload": payload}, indent=2))
    return EXIT_OK


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
