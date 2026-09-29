#!/usr/bin/env python3
"""Post-boot Melon LIVE smoke: health gate + debug session + p1-baseline + spawn/damage events.
Run AFTER deploy-play -LoaderHost MelonLoader and a level lawn is open.

Replaces `scripts/smoke-melon-live.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** `Invoke-RestMethod` was called four times with no `-TimeoutSec`
  anywhere in the file. A wedged server holds the smoke open indefinitely. Every request now carries
  a timeout.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **THE EVENT-ID BASELINE HAD NO BUDGET.** `Get-MaxEventId` issued about `2 * log2(maxEventId)`
  requests with nothing stopping any of them. The port uses `lib.get_debug_max_event_id`, which
  carries a per-request timeout AND a total budget with a named SEARCH-BUDGET-EXHAUSTED refusal.

* **THE HEALTH GATE'S FAILURE WAS A BARE EXIT.** `exit 1` after a `Write-Step` that only the host
  saw. The gate's detail now reaches the operator on stderr, and the abort line names the fix.

DELIBERATELY UNCHANGED
----------------------
Same four steps in the same order (health gate, session/start, p1-baseline, debug/events), same
pass criteria (health: injectorConnected && !simEnabled && source == "injector"; events: any of
plant/zombie/damage), same `[PASS]`/`[FAIL]` row format, same final messages, same exit contract:
1 when the gate or any step fails, 0 on pass. The afterId baseline still warns and continues from 0
when it cannot be captured -- the original's WARN-and-continue, kept.
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

TOOL_ID = "smoke-melon-live"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_WAIT_SECONDS = 2.0
DEFAULT_TIMEOUT = 15
HEALTH_TIMEOUT = 5
EVENTS_KINDS = "zombie.damage,debug.spawn.plant,debug.spawn.zombie"
EVENTS_PATH = f"/api/debug/events?kinds={EVENTS_KINDS}&limit=200"

REFUSAL_REASONS = {
    "INVALID-WAIT", "INVALID-TIMEOUT", "REQUEST-FAILED", "RESPONSE-NOT-JSON",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request` and `time` are
# process-wide modules: a test that patches either reaches every other test in this project.
_URLOPEN = urllib.request.urlopen
_SLEEP = time.sleep


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
        headers={"User-Agent": "FusionRpg-smoke-melon-live/1.0",
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


def _step(name: str, ok: bool, detail: str) -> dict:
    """One [PASS]/[FAIL] row, in the original's format."""
    mark = "PASS" if ok else "FAIL"
    return {"name": name, "ok": ok, "detail": detail, "mark": mark}


def run_smoke(base_url: str, wait_seconds: float, timeout: int) -> dict:
    """The four-step smoke. Returns the steps and the failed flag; raises Refusal on a transport
    failure the smoke cannot carry on from."""
    steps: list[dict] = []
    failed = False

    print(f"==> Melon LIVE smoke against {base_url}", file=sys.stderr)

    # 1) Health gate
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    connected = bool(health.get("injectorConnected"))
    sim_off = not bool(health.get("simEnabled"))
    source = str(health.get("source") or "")
    ok_health = connected and sim_off and source == "injector"
    detail = f"injectorConnected={connected} simEnabled={health.get('simEnabled')} source={source}"
    steps.append(_step("health", ok_health, detail))
    if not ok_health:
        print("Aborting: fix Melon injector connection (no SIM, no FUSIONRPG_MELON_SKIP_HARMONY) "
              "before scenarios.", file=sys.stderr)
        return {"steps": steps, "failed": True, "aborted": True}

    # 2) Session start
    try:
        session = _request(f"{base_url}/api/debug/session/start", {}, timeout,
                           "POST /api/debug/session/start")
        ok_session = bool(session.get("ok"))
        steps.append(_step("session/start", ok_session,
                           f"ok={session.get('ok')} scenarioId={session.get('scenarioId')}"))
        if not ok_session:
            failed = True
    except Refusal as refusal:
        steps.append(_step("session/start", False, refusal.detail))
        return {"steps": steps, "failed": True, "aborted": True}

    # 3) Capture afterId BEFORE scenario (ListEvents is ascending from afterId; afterId=0 = oldest)
    after_id = 0
    try:
        after_id = lib.get_debug_max_event_id(base_url, timeout=timeout)
        print(f"afterId baseline: {after_id}", file=sys.stderr)
    except lib.Refusal as refusal:
        # The original's WARN-and-continue: a baseline that cannot be captured must not stop a
        # smoke that can still measure the events from 0.
        print(f"WARN: could not capture afterId: {refusal.detail}", file=sys.stderr)

    # 4) p1-baseline
    try:
        scenario = _request(f"{base_url}/api/debug/scenario/p1-baseline", {}, timeout,
                            "POST /api/debug/scenario/p1-baseline")
        ok_scenario = bool(scenario.get("ok"))
        steps.append(_step("scenario/p1-baseline", ok_scenario,
                           f"ok={scenario.get('ok')} steps={scenario.get('steps')}"))
        if not ok_scenario:
            failed = True
    except Refusal as refusal:
        steps.append(_step("scenario/p1-baseline", False, refusal.detail))
        failed = True

    # 5) Wait + events (must use afterId or kinds filter sees oldest page only)
    _SLEEP(wait_seconds)
    try:
        events = _request(f"{base_url}{EVENTS_PATH}&afterId={after_id}", None, timeout,
                          "GET /api/debug/events")
        items = [i for i in events.get("items") or [] if isinstance(i, dict)]
        kinds_seen = sorted({str(i.get("kind")) for i in items})
        has_plant = "debug.spawn.plant" in kinds_seen
        has_zombie = "debug.spawn.zombie" in kinds_seen
        has_damage = "zombie.damage" in kinds_seen
        # Spawn is enough for smoke; damage may need pea shots / longer wait
        ok_events = has_plant or has_zombie or has_damage
        detail = (f"afterId={after_id} count={len(items)} kinds=[{', '.join(kinds_seen)}] "
                  f"plant={has_plant} zombie={has_zombie} damage={has_damage}")
        steps.append(_step("debug/events", ok_events, detail))
        if not ok_events:
            failed = True
    except Refusal as refusal:
        steps.append(_step("debug/events", False, refusal.detail))
        failed = True

    return {"steps": steps, "failed": failed, "aborted": False}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="smoke-melon-live",
        description="Post-boot Melon LIVE smoke: health gate + debug session + p1-baseline + "
                    "spawn/damage events (replaces smoke-melon-live.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--wait-seconds", type=float, default=DEFAULT_WAIT_SECONDS,
                        help=f"seconds to wait before reading events (default {DEFAULT_WAIT_SECONDS})")
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
    try:
        result = run_smoke(base_url, args.wait_seconds, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    for step in result["steps"]:
        print(f"[{step['mark']}] {step['name']}: {step['detail']}", file=sys.stderr)

    print("", file=sys.stderr)
    if result["failed"]:
        print("Melon LIVE smoke: FAILED (see rows above). Continue with "
              "docs/runbook/melon-live-checklist.md", file=sys.stderr)
        envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                    "verdict": "FAILED", "exitCode": EXIT_FAILED, "steps": result["steps"]}
        if args.json:
            print(json.dumps(envelope, indent=2))
        return EXIT_FAILED

    print("Melon LIVE smoke: PASSED health + session + p1-baseline + spawn/damage events.",
          file=sys.stderr)
    print("Next: fill Priority A–C in docs/runbook/melon-live-checklist.md", file=sys.stderr)
    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                "verdict": "PASS", "exitCode": EXIT_OK, "steps": result["steps"]}
    if args.json:
        print(json.dumps(envelope, indent=2))
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
