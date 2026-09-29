#!/usr/bin/env python3
"""Shared LIVE helpers: apply a StatusRuntime status until sustained VFX starts.

The single source of truth for `POST /api/debug/status/apply` payloads -- **not** `/apply-status`, which is
the Unity CC bypass and a different thing. See `.claude/skills/live-lawn-quick-start/SKILL.md`.

Replaces `scripts/lib/DebugStatusApply.ps1`, which was DOT-SOURCED by one caller. This is a MODULE, not a
script: every function is importable, and `preflight` exposes the module's own machine-readable surface so
the file is also runnable and probeable on its own.

THE CALLER-TO-FUNCTION MAP, so the rename is checkable rather than remembered:

    Get-LiveTargetPtr                 -> get_live_target_ptr
    Wait-StatusFxStarted              -> wait_status_fx_started
    Invoke-StatusApplyUntilStarted    -> invoke_status_apply_until_started
    Clear-StatusTarget                -> clear_status_target

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **`Wait-StatusFxStarted`'s READ HAD NO TIMEOUT, INSIDE A LOOP BOUNDED ONLY BY WALL CLOCK.** The loop's
  budget is `TimeoutMs`, so a single hung `Invoke-RestMethod` makes the budget unbounded: the deadline is
  only evaluated *after* the read returns. This is the third time this defect has been retired, in three
  different files, and each time it read as "the function has a timeout" because a timeout appears two
  lines below. Every read here is bounded, and the deadline is checked BEFORE the sleep.

* **`$TimeoutMs = 2500` IS NOT THE SAME AS THE `$DurationMs = 6000` APPLIED.** The apply asks the game for a
  6-second status, then waits **2.5** seconds for the VFX to start. That is a real mismatch and a reader of
  the old file could not see it, because the two numbers sit in different functions. The port keeps both
  defaults exactly -- changing them would change the proof -- and RECORDS BOTH in every result, so the
  relationship is visible instead of implied.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED, in THREE of the four functions.** `:5088` on a
  machine with a three-slot pool on 5101/5102/5103 is somebody else's server. Resolved once by the shared
  library from `FUSIONRPG_SERVER_URL`, and its source is reported.

* **`Get-DebugPayload`'s swallowed parse failure BECAME A HARD FAILURE WHEN THE LIBRARY WENT STRICT.**
  The original called `Get-DebugPayload $ev` and then `if ($p -and ...)`, so an unparseable payload read as
  `$null` and the event was simply skipped. The ported library REFUSES on a payload that is a string but
  not JSON -- correct, because "no payload" and "unreadable payload" are different findings -- so calling
  it here without a guard would turn **one malformed event into a failed status apply**. The refusal is
  caught here and the event skipped, which is what the original did, and the count is reported so the skip
  is visible rather than silent.

* **A BOOLEAN RETURN HID THE REASON.** `Invoke-StatusApplyUntilStarted` returned `$true`/`$false`, so a
  caller could not tell "the status applied and the VFX started on the first try" from "it took all six",
  nor whether any events were seen at all. The record carries the tries, the events seen and the matching
  events -- and `started` is the original's return value, so the semantics are unchanged.

* **NO MACHINE-READABLE SURFACE.** `Clear-StatusTarget` discarded its response entirely. Every function
  now returns a record, and `preflight --json` reports the same shape a caller receives.

THE MEASURED FINDING, which is the reason the records carry `seenStatusIds`
--------------------------------------------------------------------------------
Against a running game (pool slot 2, :5102, injector connected), the apply endpoint ACCEPTS
`statusId: "status-l2-wither"` -- `{"ok": true, "queued": 1}` -- and the resulting
`debug.fx.state.started` payload carries **`statusId: "wither"`**. Measured over 31 fx events on one run,
zero of them matched the id that was applied.

So `Wait-StatusFxStarted`'s `$p.statusId -eq $StatusId` **cannot match for any `status-l2-*` id**, and its
`$false` return is identical in three different situations: the apply was refused, the fx never started, or
the fx started under a different name. The two namespaces are not the same vocabulary. The port reports
`seenStatusIds`, so "no fx" is a diagnosis rather than a shrug -- and the differential MEASURES this
rather than quoting it, failing if a run does not reproduce it.

DELIBERATELY UNCHANGED
----------------------
Same four functions with the same names and the same defaults (`lab-overlay`, level 1, 60s, 2500ms, 250ms
poll, 6000ms, amount 20, 6 tries), the same retry loop shape (cursor, POST, wait), the same
`debug.fx.state.started` marker filtered by `statusId`, and the same payload keys the SSOT is defined by.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)

TOOL_ID = "debug-status-apply"

FX_STARTED_KIND = "debug.fx.state.started"
STATUS_APPLY_PATH = "/status/apply"
CLEAR_STATUS_PATH = "/clear-status"

DEFAULT_TIMEOUT_MS = 2500
POLL_INTERVAL_MS = 250
DEFAULT_DURATION_MS = 6000
DEFAULT_AMOUNT = 20
DEFAULT_MAX_TRIES = 6
DEFAULT_FX_EVENT_PAGE = 200
APPLY_TIMEOUT_SLACK_SEC = 5

# A closed vocabulary: these are the two payload shapes the SSOT is DEFINED by, and a caller that adds a
# key is making a request the server has never been asked to answer.
APPLY_PAYLOAD_KEYS = ("statusId", "hostPtr", "amount", "durationMs")
CLEAR_PAYLOAD_KEYS = ("ptr",)

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "INVALID-TRIES", "INVALID-DURATION", "INVALID-AMOUNT", "MISSING-STATUS-ID",
    "MISSING-HOST-PTR", "CLEAR-NOT-ACKNOWLEDGED",
}


@dataclass
class FxWait:
    """What `wait_status_fx_started` observed. `started` IS the original's `$true`/`$false`."""

    started: bool = False
    polls: int = 0
    statusId: str = ""
    # DISTINCT event ids, so a poll that re-reads the same page does not inflate the counts. The first
    # version counted OBSERVATIONS: one fx event inside the poll window was counted once per poll and
    # reported `fxEvents: 4`. A counter that reports four when there was one is worse than no counter,
    # because it looks like evidence. Sets are reported sorted, so the shape is stable across runs.
    _seen: set = field(default_factory=set, repr=False)
    _fx: set = field(default_factory=set, repr=False)
    _matched: set = field(default_factory=set, repr=False)
    _bad: set = field(default_factory=set, repr=False)
    # The statusIds the GAME reported on its fx events, sorted for a stable report. This field exists
    # because of a MEASURED mismatch, not a hypothetical one: the apply endpoint accepts
    # `statusId: "status-l2-wither"`, and the resulting `debug.fx.state.started` payload carries
    # `statusId: "wither"`. The original's filter compared the two namespaces directly, so for any
    # `status-l2-*` id it could never match -- and its `$false` return was the same whether the apply was
    # refused, the fx never started, or the fx started under a different name. Reporting what the game
    # used turns "no fx" into a one-line diagnosis.
    seenStatusIds: list[str] = field(default_factory=list)

    # NOTE: these four are PROPERTIES, not fields. The first version declared them as `int` fields AND
    # added properties of the same name; the field is part of the generated __init__ and the property has
    # no setter, so every construction raised. A derived value must not also be a field -- the field wins
    # at class creation and the property is unreachable.

    @property
    def eventsSeen(self) -> int:
        """DISTINCT events, not observations. See `_seen`."""
        return len(self._seen)

    @property
    def fxEvents(self) -> int:
        return len(self._fx)

    @property
    def matching(self) -> int:
        return len(self._matched)

    @property
    def unparseable(self) -> int:
        return len(self._bad)

    def to_json(self) -> dict:
        return {"started": self.started, "polls": self.polls, "eventsSeen": self.eventsSeen,
                "fxEvents": self.fxEvents, "matching": self.matching,
                "unparseablePayloads": self.unparseable, "statusId": self.statusId,
                "seenStatusIds": sorted(self.seenStatusIds)}


@dataclass
class ApplyAttempt:
    """One pass of the retry loop, so a caller can see WHICH try carried the proof."""

    cursor: int = 0
    applied: dict | None = None
    wait: FxWait = field(default_factory=FxWait)

    def to_json(self) -> dict:
        return {"cursor": self.cursor, "applied": self.applied, "wait": self.wait.to_json()}


@dataclass
class ApplyOutcome:
    started: bool = False
    tries: int = 0
    attempts: list[ApplyAttempt] = field(default_factory=list)
    statusId: str = ""
    hostPtr: str = ""
    durationMs: int = DEFAULT_DURATION_MS
    fxWaitBudgetMs: int = DEFAULT_TIMEOUT_MS

    def to_json(self) -> dict:
        return {"started": self.started, "tries": self.tries, "statusId": self.statusId,
                "hostPtr": self.hostPtr, "durationMs": self.durationMs,
                "fxWaitBudgetMs": self.fxWaitBudgetMs,
                "attempts": [a.to_json() for a in self.attempts]}


def get_live_target_ptr(base_url: str = "", scenario: str = "lab-overlay", level_number: int = 1,
                        timeout_sec: int = lib.DEFAULT_SETUP_TIMEOUT_SEC,
                        skip_setup: bool = False) -> str:
    """A living zombie pointer, as a string. The original returned `[string]$lab.TargetPtr`, so a caller
    interpolates it into a payload; the cast is preserved rather than left to the caller."""
    board = lib.ensure_live_lab_board(base_url, scenario, level_number, timeout_sec, skip_setup)
    if not board.target_ptr:
        # `ensure_live_lab_board` already refuses when there is no pointer, so reaching this is a library
        # contract change rather than a board state. It is named rather than returning an empty string,
        # because an empty ptr goes into a payload and produces a confusing 400 instead of a clear refusal.
        raise lib.Refusal("NO-TARGET-PTR",
                          f"the lab board returned no target pointer (scenario={scenario} "
                          f"skip_setup={skip_setup}); an empty pointer would be sent in a payload")
    return board.target_ptr


def wait_status_fx_started(base_url: str, after_id: int, status_id: str,
                           timeout_ms: int = DEFAULT_TIMEOUT_MS,
                           event_timeout: int = lib.DEFAULT_EVENT_TIMEOUT) -> FxWait:
    """Poll for a `debug.fx.state.started` event whose payload names THIS status.

    The read is bounded AND the deadline is checked before the sleep, where the original's `do/while`
    evaluated it only after an unbounded read. A refused payload is counted and the event skipped, which
    is what the original's `if ($p -and ...)` did with the `$null` that the library now raises instead of
    returning.
    """
    if timeout_ms <= 0:
        raise lib.Refusal("INVALID-TIMEOUT", f"the fx wait budget {timeout_ms}ms must be positive")
    out = FxWait(statusId=status_id)
    deadline = time.monotonic() + timeout_ms / 1000.0
    while True:
        out.polls += 1
        items = lib.get_events(base_url, after_id, DEFAULT_FX_EVENT_PAGE, event_timeout)
        for event in items:
            out._seen.add(event.get("id"))
            if event.get("kind") != FX_STARTED_KIND:
                continue
            out._fx.add(event.get("id"))
            try:
                payload = lib.get_debug_payload(event)
            except lib.Refusal:
                # Skipped, and COUNTED. One malformed event must not fail a status apply, and a skip that
                # is not reported is indistinguishable from an event that never arrived.
                out._bad.add(event.get("id"))
                continue
            if not isinstance(payload, dict):
                continue
            seen = str(payload.get("statusId") or "")
            if seen and seen not in out.seenStatusIds:
                out.seenStatusIds.append(seen)
            if seen == status_id:
                out._matched.add(event.get("id"))
                out.started = True
                return out
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return out
        time.sleep(min(POLL_INTERVAL_MS / 1000.0, remaining))


def invoke_status_apply_until_started(base_url: str = "", status_id: str = "", host_ptr: str = "",
                                      duration_ms: int = DEFAULT_DURATION_MS,
                                      amount: int = DEFAULT_AMOUNT, max_tries: int = DEFAULT_MAX_TRIES,
                                      fx_timeout_ms: int = DEFAULT_TIMEOUT_MS) -> ApplyOutcome:
    """Apply the status, then wait for its VFX. One attempt is: cursor, POST, wait."""
    if not status_id:
        raise lib.Refusal("MISSING-STATUS-ID", "status_id is required; a status apply with no status id "
                                               "asks the server to apply nothing and to say so")
    if not host_ptr:
        raise lib.Refusal("MISSING-HOST-PTR", "host_ptr is required; it is the pointer the status is "
                                              "applied to, and an empty one produces a 400 from the server")
    if duration_ms <= 0:
        raise lib.Refusal("INVALID-DURATION", f"duration_ms {duration_ms} must be positive")
    if max_tries <= 0:
        raise lib.Refusal("INVALID-TRIES", f"max_tries {max_tries} must be positive")
    url, _ = lib.resolve_base_url(base_url)
    outcome = ApplyOutcome(statusId=status_id, hostPtr=host_ptr, durationMs=duration_ms,
                           fxWaitBudgetMs=fx_timeout_ms)
    for _ in range(max_tries):
        outcome.tries += 1
        attempt = ApplyAttempt()
        # The cursor is taken BEFORE the post, so the events this pass can see are this pass's own. A
        # cursor taken afterwards would miss the very event being waited for.
        attempt.cursor = lib.get_debug_max_event_id(url, budget_sec=fx_timeout_ms / 1000.0 + 5.0)
        attempt.applied = lib.invoke_debug_post(
            url, STATUS_APPLY_PATH,
            {"statusId": status_id, "hostPtr": host_ptr, "amount": amount, "durationMs": duration_ms},
            timeout=duration_ms / 1000.0 + APPLY_TIMEOUT_SLACK_SEC)
        attempt.wait = wait_status_fx_started(url, attempt.cursor, status_id, fx_timeout_ms)
        outcome.attempts.append(attempt)
        if attempt.wait.started:
            outcome.started = True
            return outcome
    return outcome


def clear_status_target(base_url: str = "", host_ptr: str = "") -> dict:
    """Clear the status on one pointer. The original discarded the response; it is returned, because a
    refused clear that a caller cannot see is a board left in the state the caller asked to leave."""
    if not host_ptr:
        raise lib.Refusal("MISSING-HOST-PTR", "host_ptr is required; there is nothing to clear without it")
    url, _ = lib.resolve_base_url(base_url)
    result = lib.invoke_debug_post(url, CLEAR_STATUS_PATH, {"ptr": host_ptr},
                                   timeout=lib.DEBUG_POST_TIMEOUT)
    if isinstance(result, dict) and result.get("ok") is False:
        raise lib.Refusal("CLEAR-NOT-ACKNOWLEDGED",
                          f"POST /api/debug/clear-status was refused: {result.get('error')!r}")
    return result if isinstance(result, dict) else {"raw": result}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="debug-status-apply",
        description="Apply a StatusRuntime status until sustained VFX starts "
                    "(replaces lib/DebugStatusApply.ps1).")
    sub = parser.add_subparsers(dest="command", required=True)
    pre = sub.add_parser("preflight", help="board pointer, then a real apply/wait/clear cycle")
    pre.add_argument("--base-url", default="")
    pre.add_argument("--scenario", default="lab-overlay", choices=list(lib.SCENARIOS))
    pre.add_argument("--level-number", type=int, default=1)
    pre.add_argument("--status-id", default="status-l2-wither",
                     help="the status to apply (the default is one this repository's scenarios use)")
    pre.add_argument("--skip-setup", action="store_true")
    pre.add_argument("--duration-ms", type=int, default=DEFAULT_DURATION_MS)
    pre.add_argument("--amount", type=int, default=DEFAULT_AMOUNT)
    pre.add_argument("--max-tries", type=int, default=DEFAULT_MAX_TRIES)
    pre.add_argument("--fx-timeout-ms", type=int, default=DEFAULT_TIMEOUT_MS)
    pre.add_argument("--no-apply", action="store_true",
                     help="report the board pointer and stop, without applying anything")
    pre.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    try:
        url, source = lib.resolve_base_url(args.base_url)
        envelope: dict = {"tool": TOOL_ID, "baseUrl": url, "baseUrlSource": source}
        print(f"Board pointer: scenario={args.scenario} against {url}...", file=sys.stderr)
        pointer = get_live_target_ptr(url, args.scenario, args.level_number,
                                      lib.DEFAULT_SETUP_TIMEOUT_SEC, args.skip_setup)
        envelope.update({"targetPtr": pointer, "statusId": args.status_id})
        if args.no_apply:
            envelope.update({"verdict": "POINTER-ONLY", "exitCode": 0})
            if args.json:
                print(json.dumps(envelope, indent=2))
            else:
                print(f"ZombiePtr={pointer}")
            return 0
        outcome = invoke_status_apply_until_started(
            url, args.status_id, pointer, args.duration_ms, args.amount, args.max_tries,
            args.fx_timeout_ms)
        cleared = clear_status_target(url, pointer)
        envelope.update({
            "apply": outcome.to_json(),
            "cleared": cleared,
            "verdict": "OK" if outcome.started else "NO-FX",
            "exitCode": 0 if outcome.started else 1,
        })
    except lib.Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": lib.EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return lib.EXIT_REFUSED

    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        print(f"started={outcome.started}  tries={outcome.tries}  statusId={outcome.statusId}  "
              f"durationMs={outcome.durationMs}  fxWaitBudgetMs={outcome.fxWaitBudgetMs}")
        for index, attempt in enumerate(outcome.attempts, 1):
            print(f"  try {index}: cursor={attempt.cursor} fxEvents={attempt.wait.fxEvents} "
                  f"matching={attempt.wait.matching} polls={attempt.wait.polls}")
        if not outcome.started:
            seen = sorted({i for a in outcome.attempts for i in a.wait.seenStatusIds})
            print(f"  no fx.started event for statusId={args.status_id!r} within the budget.")
            if seen:
                print(f"  THE GAME REPORTED statusId {seen} instead -- the apply namespace and the fx "
                      f"namespace differ, and this filter compares them directly.")
            else:
                print("  no fx.state.started event arrived at all, so the apply produced no fx to match.")
            print("  The board is unchanged.")
    return envelope["exitCode"]


if __name__ == "__main__":
    sys.exit(main())
