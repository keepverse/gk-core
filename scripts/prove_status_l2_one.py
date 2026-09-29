#!/usr/bin/env python3
"""PROVE: one live StatusRuntime L2 scenario (organic atom path).

All-in-one: the shared live-lawn setup runs first unless `--skip-setup`.

Usage:
  gk-core/scripts/prove_status_l2_one.py --scenario status-l2-rot

See `.claude/skills/live-lawn-quick-start/SKILL.md`.

Replaces `scripts/prove-status-l2-one.ps1`.

WHAT IT PROVES, and the order the evidence is collected in
--------------------------------------------------------
1. `ensure_live_lab_board` -- enter level 1, run the lab scenario, get a living zombie pointer.
2. `get_debug_max_event_id` -- the cursor, so the events counted below are THIS RUN'S and not whatever the
   server already held. Without a cursor the counts include the previous run, and a scenario that did
   nothing still shows evidence.
3. `invoke_debug_post` `/scenario/<name>` -- start the scenario.
4. Poll `/api/events` for `debug.run-steps.done`, bounded by the budget.
5. Count `debug.fx.state.started`, `debug.status.apply` and `debug.status` over the run's own events.
6. Refuse unless there is ORGANIC apply evidence -- at least one `debug.fx.state.started` OR one
   `debug.status.apply`. `debug.status` alone is deliberately NOT enough: a status event with no apply
   behind it is the signature of the bug this scenario exists to catch, not of a pass.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE EVENT POLLS HAD NO TIMEOUT AND NO PER-REQUEST BOUND.** `Invoke-RestMethod -Uri
  "$BaseUrl/api/events?afterId=$tip&limit=100"` appears three times with nothing stopping any of them, in a
  loop that runs for up to `-TimeoutSec`. A hung poll inside that loop means the loop never re-checks the
  deadline, so the whole budget is not a bound at all. Every read is bounded here, and the deadline is
  checked before each sleep rather than only after a poll.

* **THE `do { } while` LOOP EVALUATED THE CONDITION AFTER THE POLL, SO A SLOW POLL COULD OVERRUN THE
  DEADLINE ARBITRARILY.** Bounded reads fix the shape of that, and the remaining budget is clamped into
  the sleep so the function returns close to when it promised.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED** -- `http://127.0.0.1:5088`, on a machine with a
  three-slot pool on 5101/5102/5103. Resolved by the shared library now, from `FUSIONRPG_SERVER_URL`, and
  its source is REPORTED.

* **THE SCENARIO NAME WAS INTERPOLATED INTO A URL PATH** (`"/scenario/$Scenario"`) with no validation, so
  a name carrying a slash or a query reached the server as a different request than the one intended. The
  name is checked against a closed pattern before it is interpolated.

* **THE COUNTS WERE COMPUTED INLINE AND NEVER REPORTED.** Three `Where-Object` counts were computed, one
  line printed, and nothing recorded which of the three carried the pass. `summarise_events` returns the
  whole shape, so a caller can see a run that had fx evidence but no apply evidence -- which is a different
  finding from a run that had neither.

* **NO MACHINE-READABLE VERDICT.** `--json` reports the base URL and its source, the scenario, the cursor,
  the attempt count, the three counts, and the refusal when there is no evidence.

DELIBERATELY UNCHANGED
----------------------
Same four inputs and defaults, the same two-step ordering (setup, then cursor, then scenario), the same
400ms poll interval, the same `debug.run-steps.done` completion marker, the same three counted kinds, the
same "started OR applied" acceptance rule, and the same refusal text for the timeout case -- which names
the scenario and the budget, because a timeout that does not say which of those is not actionable.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)

TOOL_ID = "prove-status-l2-one"

DEFAULT_SCENARIO = "status-l2-wither"
DEFAULT_TIMEOUT_SEC = 90
POLL_INTERVAL_SEC = 0.4
EVENT_PAGE = 100
FINAL_PAGE = 300
REQUEST_TIMEOUT = 10

DONE_KIND = "debug.run-steps.done"
STARTED_KIND = "debug.fx.state.started"
APPLY_KIND = "debug.status.apply"
STATUS_KIND = "debug.status"
COUNTED = (STARTED_KIND, APPLY_KIND, STATUS_KIND)

# A scenario name reaches a URL PATH, so it is a closed shape and not prose. The original interpolated any
# string, so a name carrying a slash or a query asked the server for a different request than intended.
SCENARIO_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")

REFUSAL_REASONS = {
    "INVALID-SCENARIO-NAME", "SCENARIO-NOT-COMPLETED", "NO-ORGANIC-EVIDENCE", "INVALID-TIMEOUT",
}

_RUN = None  # declared for the seam's sake; the process boundary is live_lawn_setup's


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def summarise_events(items: list[dict]) -> dict:
    """The three counts, plus which of them carried the pass.

    Pure, so a case can decide it with planted pages and no server -- and it returns the whole shape rather
    than one boolean, because a run with fx evidence and no apply evidence is a DIFFERENT finding from a
    run with neither, and collapsing them loses exactly what this scenario measures.
    """
    counts = {kind: 0 for kind in COUNTED}
    # `items` comes from a SERVER. A page that is not a list, or a list holding something that is not an
    # object, must count as zero rather than raise: the counting is a report, and a report that crashes on
    # an unexpected page is a tool that cannot be pointed at a server it has not met.
    for event in items if isinstance(items, (list, tuple)) else ():
        if not isinstance(event, dict):
            continue
        kind = event.get("kind")
        # `counts` is a dict, so `kind in counts` on an UNHASHABLE kind -- a list or a dict, which a
        # server can send as easily as a string -- raises TypeError. Found by the suite, on the input this
        # port exists to be pointed at. A count that raises on a malformed page reports nothing at all,
        # which is worse than reporting zero.
        if isinstance(kind, str) and kind in counts:
            counts[kind] += 1
    started, applied = counts[STARTED_KIND], counts[APPLY_KIND]
    return {
        "started": started,
        "applied": applied,
        "status": counts[STATUS_KIND],
        "hasEvidence": (started >= 1) or (applied >= 1),
        # Which of the two carried it, so a caller can tell fx-only from apply-only.
        "evidenceKind": (APPLY_KIND if applied >= 1 else (STARTED_KIND if started >= 1 else "")),
    }


def wait_for_completion(base_url: str, after_id: int, deadline: float) -> tuple[bool, list[dict], int]:
    """Poll for `debug.run-steps.done`. Returns (done, the run's own events, attempts).

    The deadline is checked BEFORE the sleep and the sleep is clamped to what is left, so the function
    returns close to when it promised even if the last poll was slow. The original's `do/while` checked the
    deadline only after a poll, so a slow poll could overrun it arbitrarily.
    """
    attempts = 0
    collected: list[dict] = []
    while True:
        attempts += 1
        items = lib.get_events(base_url, after_id, EVENT_PAGE, REQUEST_TIMEOUT)
        collected.extend(items)
        if any(event.get("kind") == DONE_KIND for event in items):
            return True, collected, attempts
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return False, collected, attempts
        time.sleep(min(POLL_INTERVAL_SEC, remaining))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-status-l2-one",
        description="Prove one live StatusRuntime L2 scenario over the organic atom path "
                    "(replaces prove-status-l2-one.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to prove against (default: resolved by the shared library from "
                             f"${lib.BASE_URL_ENV}, else {lib.DEFAULT_BASE_URL})")
    parser.add_argument("--scenario", default=DEFAULT_SCENARIO,
                        help=f"the scenario name (default {DEFAULT_SCENARIO!r})")
    parser.add_argument("--timeout-sec", type=int, default=DEFAULT_TIMEOUT_SEC,
                        help=f"the completion budget in seconds (default {DEFAULT_TIMEOUT_SEC})")
    parser.add_argument("--skip-setup", action="store_true",
                        help="the game is already on a lab board; skip /lawn/quick-start")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout_sec <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout-sec {args.timeout_sec} must be positive", args.json)
    if not SCENARIO_PATTERN.match(args.scenario):
        # The original interpolated this into a URL path unchecked, so a name carrying a slash or a query
        # asked the server for a different request than the one intended -- and the counts that came back
        # would have belonged to that other request.
        return _refuse("INVALID-SCENARIO-NAME",
                       f"--scenario {args.scenario!r} is not a bare scenario name. It is interpolated "
                       f"into /api/debug/scenario/<name>, so it must match "
                       f"[A-Za-z0-9][A-Za-z0-9._-]{{0,63}} and carry no slash, query or space", args.json)

    started = time.monotonic()
    try:
        url, source = lib.resolve_base_url(args.base_url)
        envelope: dict = {"tool": TOOL_ID, "baseUrl": url, "baseUrlSource": source,
                          "scenario": args.scenario, "skipSetup": args.skip_setup}
        print(f"Running scenario {args.scenario}...", file=sys.stderr)
        board = lib.ensure_live_lab_board(url, timeout_sec=args.timeout_sec, skip_setup=args.skip_setup)
        cursor = lib.get_debug_max_event_id(url, REQUEST_TIMEOUT, budget_sec=args.timeout_sec)
        lib.invoke_debug_post(url, f"/scenario/{args.scenario}", {})
        deadline = time.monotonic() + args.timeout_sec
        done, collected, attempts = wait_for_completion(url, cursor, deadline)
    except lib.Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope if "envelope" in dir() else None)

    envelope.update({"cursor": cursor, "attempts": attempts,
                     "completed": done, "collectedEvents": len(collected)})
    if not done:
        return _refuse("SCENARIO-NOT-COMPLETED",
                       f"scenario '{args.scenario}' did not complete within {args.timeout_sec}s "
                       f"({attempts} poll(s), {len(collected)} event(s) since cursor {cursor})", args.json,
                       envelope)

    # The FINAL page is the whole run's events, not just the completion poll's -- the original re-read with
    # a larger limit for the same reason, and losing the early events would undercount.
    final_items = lib.get_events(url, cursor, FINAL_PAGE, REQUEST_TIMEOUT)
    summary = summarise_events(final_items)
    envelope.update({
        "verdict": "OK" if summary["hasEvidence"] else "FAILED",
        "exitCode": 0 if summary["hasEvidence"] else 1,
        "counts": {"fx.state.started": summary["started"], "debug.status.apply": summary["applied"],
                   "debug.status": summary["status"]},
        "evidenceKind": summary["evidenceKind"],
        "targetPtr": board.target_ptr,
        "plantPtr": board.plant_ptr,
        "levelType": board.level_type,
        "seconds": round(time.monotonic() - started, 3),
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        print(f"  fx.state.started={summary['started']} debug.status.apply={summary['applied']} "
              f"debug.status={summary['status']}")
        if summary["hasEvidence"]:
            print(f"PASS {args.scenario}  (evidence: {summary['evidenceKind']})")
        else:
            print(f"no organic status apply evidence in events for {args.scenario}")
    return envelope["exitCode"]


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": lib.EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return lib.EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
