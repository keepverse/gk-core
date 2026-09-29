#!/usr/bin/env python3
"""PROVE: VFX cue -> recipe -> primitive pipeline (vfx-ssot.md S11, S16.7).

Plays every catalog cue through /api/debug/fx/play and asserts one debug.fx.shown per play,
including per-element combat.hit variants, one hybrid (rainbow) payload, the
SYS-ELEMENT-FX-off neutral path (asserted by rgb payload = white), and the four story-cue
rift.* recipes (story-scene T27a).

Usage:
  python gk-core/scripts/prove_vfx.py --target-ptr <ZombiePtr>
  python gk-core/scripts/prove_vfx.py --base-url http://127.0.0.1:5101 --target-ptr <ZombiePtr>

Replaces `scripts/prove-vfx.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** `http://127.0.0.1:5088` is the
  OWNER's server, not "the" port. This machine has a three-slot pool on 5101/5102/5103, so a
  script pointed at the default enters somebody else's board. The URL is now resolved by the
  shared library from `FUSIONRPG_SERVER_URL`, and its source is REPORTED.

* **THE EVENT READS HAD NO TIMEOUT AT ALL.** `Get-MaxEventId` issues about `2 * log2(maxEventId)`
  requests -- for a long-running server that is dozens -- and `Has-After` had nothing stopping any
  of them. A single hung read makes the whole binary search hang forever, with no output and no
  way to tell it from a slow one. The library's `get_debug_max_event_id` adds a per-request
  timeout AND a total budget, so "the server stopped answering" is an outcome rather than a wait.

* **A PASSING ROW WAS WRITTEN FOR WORK THAT WAS NOT DONE.** The no-TargetPtr path emitted a
  `Write-Warning` (invisible to a `2>&1` capture) and appended a result with `ok = $true`, so a
  run with zero organic coverage reported `pass: true`. A skip is now `ok`-distinct from a pass.

* **NO TRY/CATCH AROUND THE CASE LOOP.** Under `ErrorActionPreference=Stop` one failed HTTP call
  aborts the run, so the JSON is never written -- no verdict, no named refusal. The loop is now
  wrapped so a refusal is recorded and the JSON is always produced.

* **A DOCUMENTED PARAMETER THAT DID NOT EXIST.** The header documented `-SkipSetup`, which was not
  in the `param()` block. That invocation fails on parameter binding. The parameter is gone.

* **AN OUT-OF-SCOPE VARIABLE.** `$lp` was used at line 383 but assigned only under `if ($listEv)`.
  With no `Set-StrictMode` the failure case yielded `detail = "cues="` -- the least informative
  string in the document, for the one case that failed. The variable is now initialised.

* **`Set-Content -Encoding utf8` WROTE A BOM** under Windows PowerShell 5.1 and none under PS7.
  The output is now written as UTF-8 no-BOM explicitly.

DELIBERATELY UNCHANGED
----------------------
Same test matrix (16 base plays + 4 ptr-anchored + 21 status recipes + world-flash alias +
5 organic producer cases + rate-limit + mute roundtrip + master toggle + element toggle +
fx.list), same 400ms inter-case pause, same 250ms poll interval, same 5s default match timeout,
same detail string shapes, same verdict JSON shape (top-level `at`, `baseUrl`, `targetPtr`,
`pass`, `results`; per-result `case`, `ok`, `detail`).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path
from typing import Any, Callable

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)

TOOL_ID = "prove-vfx"

DEFAULT_COL = 4
DEFAULT_ROW = 2
POLL_INTERVAL_SEC = 0.25
CASE_PAUSE_SEC = 0.4
EVENT_PAGE = 200
MAX_EVENT_PAGES = 40
DEBUG_POST_TIMEOUT = 8
CHEAT_TOGGLE_TIMEOUT = 8
DEFAULT_MATCH_TIMEOUT_MS = 5000

FX_KINDS = frozenset({
    "debug.fx.shown", "debug.fx.skipped", "debug.fx.list",
    "debug.fx.state", "debug.fx.state.started", "debug.fx.state.ended",
})

REFUSAL_REASONS = {
    "BASE-URL-INVALID", "SERVER-UNREACHABLE", "EVENT-READ-FAILED",
    "EVENT-READ-TIMED-OUT", "SEARCH-BUDGET-EXHAUSTED", "DEBUG-POST-FAILED",
    "PAYLOAD-UNPARSEABLE", "CHEAT-TOGGLE-FAILED",
}

EXIT_REFUSED = 64

DEFAULT_OUT_JSON = Path(__file__).resolve().parent.parent / "docs" / "research" / "effect-runtime" / "_prove-vfx.json"


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


# ---------------------------------------------------------------------------
# Test matrix
# ---------------------------------------------------------------------------

def build_plays(col: int, row: int, target_ptr: str) -> list[dict]:
    """The full test matrix, as data. Pure, so a case can pin it with no server.

    Each play is a dict with:
      name    -- case name in the verdict
      path    -- /api/debug/<path> to POST to
      body    -- JSON body for the POST
      expect  -- match criteria (see match_event / validate_payload)
    """
    plays: list[dict] = []

    # --- base plays (always run) ---
    plays.append({
        "name": "probe", "path": "/fx/play",
        "body": {"cueId": "debug.probe", "col": col, "row": row, "amount": -901},
        "expect": {"kind": "debug.fx.shown", "cueId": "debug.probe", "amount": -901},
    })
    plays.append({
        "name": "hit-neutral-cell", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -55},
        "expect": {"kind": "debug.fx.skipped", "cueId": "combat.hit", "amount": -55, "reason": "no-element"},
    })
    plays.append({
        "name": "hit-fire", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -61,
                 "elements": [{"element": "fire", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -61, "rgb": "#FF5A28"},
    })
    plays.append({
        "name": "hit-ice", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -62,
                 "elements": [{"element": "ice", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -62, "rgb": "#6ED2FF"},
    })
    plays.append({
        "name": "hit-air", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -63,
                 "elements": [{"element": "air", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -63, "rgb": "#BEFFAA"},
    })
    plays.append({
        "name": "hit-earth", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -64,
                 "elements": [{"element": "earth", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -64, "rgb": "#D2A046"},
    })
    plays.append({
        "name": "hit-light", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -65,
                 "elements": [{"element": "light", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -65, "rgb": "#FFE878"},
    })
    plays.append({
        "name": "hit-dark", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -66,
                 "elements": [{"element": "dark", "weight": 1.0}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -66, "rgb": "#965ADC"},
    })
    plays.append({
        "name": "hit-hybrid", "path": "/fx/play",
        "body": {"cueId": "combat.hit", "col": col, "row": row, "amount": -80,
                 "elements": [{"element": "fire", "weight": 0.7}, {"element": "ice", "weight": 0.3}]},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -80, "hybrid": True},
    })
    plays.append({
        "name": "heal-rising", "path": "/fx/play",
        "body": {"cueId": "combat.heal", "col": col, "row": row, "amount": 41, "tag": "Heal"},
        "expect": {"kind": "debug.fx.shown", "cueId": "combat.heal", "amount": 41},
    })
    plays.append({
        "name": "unknown-cue", "path": "/fx/play",
        "body": {"cueId": "status.nope.apply", "col": col, "row": row, "amount": -902},
        "expect": {"kind": "debug.fx.skipped", "cueId": "status.nope.apply", "amount": -902, "reason": "unknown-cue"},
    })

    # Story cues (story-scene T27a): the four rift.* recipes VfxCatalog.cs:449-537 authors.
    # Cell-anchored like the rest of this matrix, so each renders its Burst and no Flash.
    # DO NOT add expectRgb here -- the shown event reports the COLOR PLAN (white for a tag-less,
    # element-less cue), never a Fixed primitive's FixedRgb.
    plays.append({
        "name": "rift-portal-open", "path": "/fx/play",
        "body": {"cueId": "rift.portal.open", "col": col, "row": row, "amount": -911},
        "expect": {"kind": "debug.fx.shown", "cueId": "rift.portal.open", "amount": -911, "expectPrim": "burst"},
    })
    plays.append({
        "name": "rift-portal-surge", "path": "/fx/play",
        "body": {"cueId": "rift.portal.surge", "col": col, "row": row, "amount": -912},
        "expect": {"kind": "debug.fx.shown", "cueId": "rift.portal.surge", "amount": -912, "expectPrim": "burst"},
    })
    plays.append({
        "name": "rift-quarantine-seal", "path": "/fx/play",
        "body": {"cueId": "rift.quarantine.seal", "col": col, "row": row, "amount": -913},
        "expect": {"kind": "debug.fx.shown", "cueId": "rift.quarantine.seal", "amount": -913, "expectPrim": "burst"},
    })
    plays.append({
        "name": "rift-quarantine-fade", "path": "/fx/play",
        "body": {"cueId": "rift.quarantine.fade", "col": col, "row": row, "amount": -914},
        "expect": {"kind": "debug.fx.shown", "cueId": "rift.quarantine.fade", "amount": -914, "expectPrim": "burst"},
    })
    plays.append({
        "name": "rift-missing-anchor", "path": "/fx/play",
        "body": {"cueId": "rift.portal.open", "ptr": "0x1", "amount": -915},
        "expect": {"kind": "debug.fx.skipped", "cueId": "rift.portal.open", "amount": -915, "reason": "missing"},
    })

    # --- ptr-anchored plays (only with --target-ptr) ---
    if target_ptr:
        plays.append({
            "name": "hit-ptr-crit-fire", "path": "/fx/play",
            "body": {"cueId": "combat.hit", "ptr": target_ptr, "amount": -200, "tag": "Crit",
                     "elements": [{"element": "fire", "weight": 1.0}]},
            "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -200,
                       "rgb": "#FF5A28", "expectPrim": "flash"},
        })
        plays.append({
            "name": "hit-ptr-plain-no-burst", "path": "/fx/play",
            "body": {"cueId": "combat.hit", "ptr": target_ptr, "amount": -57},
            "expect": {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -57,
                       "rgb": "#FFFFFF", "expectPrim": "floater", "expectNotPrim": "burst"},
        })
        plays.append({
            "name": "heal-ptr", "path": "/fx/play",
            "body": {"cueId": "combat.heal", "ptr": target_ptr, "amount": 42, "tag": "Heal"},
            "expect": {"kind": "debug.fx.shown", "cueId": "combat.heal", "amount": 42, "expectPrim": "floater"},
        })
        plays.append({
            "name": "rift-portal-open-ptr", "path": "/fx/play",
            "body": {"cueId": "rift.portal.open", "ptr": target_ptr, "amount": -211},
            "expect": {"kind": "debug.fx.shown", "cueId": "rift.portal.open", "amount": -211, "expectPrim": "flash"},
        })

    # --- status recipe plays (always run) ---
    status_ids = [
        "butter", "freeze", "cold", "poison", "hypno", "ember", "jala", "kelp",
        "wither", "bond", "rally", "leech", "expose", "command", "shatter",
        "charm_pulse", "blight", "rot", "spark", "pact_mark", "spore",
    ]
    for i, sid in enumerate(status_ids):
        amount = -(700 + i + 1)
        plays.append({
            "name": f"status-recipe-{sid}", "path": "/fx/play",
            "body": {"cueId": f"status.{sid}.apply", "col": col, "row": row, "amount": amount},
            "expect": {"kind": "debug.fx.shown", "cueId": f"status.{sid}.apply", "amount": amount},
        })

    return plays


# ---------------------------------------------------------------------------
# Event helpers
# ---------------------------------------------------------------------------

def filter_fx_events(events: list[dict]) -> list[dict]:
    """Keep only fx-related events."""
    return [e for e in events if isinstance(e, dict) and e.get("kind") in FX_KINDS]


def get_fx_events(base_url: str, after_id: int, timeout: int = 10) -> list[dict]:
    """Fetch fx events since after_id, paging through the event stream."""
    all_events: list[dict] = []
    cursor = after_id
    for _ in range(MAX_EVENT_PAGES):
        page = lib.get_events(base_url, cursor, EVENT_PAGE, timeout)
        if not page:
            break
        all_events.extend(filter_fx_events(page))
        cursor = int(page[-1].get("id", cursor))
        if len(page) < EVENT_PAGE:
            break
    return all_events


def wait_for_fx_match(
    base_url: str,
    after_id: int,
    match_fn: Callable[[dict, dict], bool],
    timeout_ms: int = DEFAULT_MATCH_TIMEOUT_MS,
    poll_interval: float = POLL_INTERVAL_SEC,
    event_timeout: int = 10,
) -> dict | None:
    """Poll for the first fx event whose payload matches the predicate.

    Returns the matching payload dict, or None if the timeout expires.
    """
    deadline = time.monotonic() + timeout_ms / 1000.0
    while True:
        for event in get_fx_events(base_url, after_id, event_timeout):
            payload = lib.get_debug_payload(event)
            if isinstance(payload, dict) and match_fn(event, payload):
                return payload
        if time.monotonic() >= deadline:
            return None
        time.sleep(poll_interval)


# ---------------------------------------------------------------------------
# Match / validate
# ---------------------------------------------------------------------------

def match_event(event: dict, payload: dict, expect: dict) -> bool:
    """Base match: kind + cueId + amount (+ reason for skip)."""
    if event.get("kind") != expect.get("kind"):
        return False
    if payload.get("cueId") != expect.get("cueId"):
        return False
    if expect.get("amount") is not None and payload.get("amount") != expect.get("amount"):
        return False
    if expect.get("reason") is not None and payload.get("reason") != expect.get("reason"):
        return False
    return True


def validate_payload(payload: dict, expect: dict) -> tuple[bool, str]:
    """Post-match validation: rgb / hybrid / expectPrim / expectNotPrim.

    Returns (ok, detail).
    """
    rgb = payload.get("rgb")
    hybrid = payload.get("hybrid")
    prims = payload.get("primitives") or []
    if not isinstance(prims, list):
        prims = [prims]

    if expect.get("rgb") is not None and rgb != expect["rgb"]:
        return False, f"rgb={rgb} (expected {expect['rgb']})"
    if expect.get("hybrid") is not None and not hybrid:
        return False, f"hybrid={hybrid} (expected True)"
    if expect.get("expectPrim") is not None and expect["expectPrim"] not in prims:
        return False, f"prims={'+'.join(str(p) for p in prims)} (expected {expect['expectPrim']})"
    if expect.get("expectNotPrim") is not None and expect["expectNotPrim"] in prims:
        return False, f"prims={'+'.join(str(p) for p in prims)} (must not contain {expect['expectNotPrim']})"

    detail = f"rgb={rgb} hybrid={hybrid} prims={'+'.join(str(p) for p in prims)}"
    return True, detail


# ---------------------------------------------------------------------------
# Cheat toggle
# ---------------------------------------------------------------------------

def set_cheat(base_url: str, cheat_id: str, on: bool, timeout: int = CHEAT_TOGGLE_TIMEOUT) -> None:
    """Toggle a cheat via /api/cheats/toggle. The path is echoed in every failure."""
    payload = json.dumps({"id": cheat_id, "enabled": on}).encode()
    request = urllib.request.Request(
        f"{base_url}/api/cheats/toggle", data=payload, method="POST",
        headers={"User-Agent": "FusionRpg-prove-vfx/1.0", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            response.read()
    except urllib.error.HTTPError as error:
        raise Refusal("CHEAT-TOGGLE-FAILED",
                       f"POST /api/cheats/toggle id={cheat_id} answered {error.code} {error.reason}") from error
    except TimeoutError as expired:
        raise Refusal("CHEAT-TOGGLE-FAILED",
                       f"POST /api/cheats/toggle id={cheat_id} did not answer within {timeout}s") from expired
    except urllib.error.URLError as error:
        raise Refusal("CHEAT-TOGGLE-FAILED",
                       f"POST /api/cheats/toggle id={cheat_id} was unreachable: {error.reason}") from error
    except OSError as error:
        raise Refusal("CHEAT-TOGGLE-FAILED",
                       f"POST /api/cheats/toggle id={cheat_id} failed: {error}") from error


# ---------------------------------------------------------------------------
# Case runner
# ---------------------------------------------------------------------------

def run_case(base_url: str, play: dict, after_id: int) -> dict:
    """Run one play case. Returns {case, ok, detail}."""
    name = play["name"]
    path = play["path"]
    body = play["body"]
    expect = play["expect"]

    lib.invoke_debug_post(base_url, path, body, timeout=DEBUG_POST_TIMEOUT)

    cue_id = body.get("cueId", "")
    amount = body.get("amount")

    if expect.get("kind") == "debug.fx.skipped":
        reason = expect.get("reason", "")
        payload = wait_for_fx_match(
            base_url, after_id,
            lambda ev, pl: match_event(ev, pl, expect),
        )
        if payload is not None:
            return {"case": name, "ok": True, "detail": f"skip={payload.get('reason')}"}
        return {"case": name, "ok": False, "detail": f"expected skip={reason} not seen"}

    # shown
    payload = wait_for_fx_match(
        base_url, after_id,
        lambda ev, pl: match_event(ev, pl, expect),
    )
    if payload is None:
        return {"case": name, "ok": False, "detail": "no shown event"}

    ok, detail = validate_payload(payload, expect)
    return {"case": name, "ok": ok, "detail": detail}


def run_organic_case(base_url: str, name: str, path: str, body: dict,
                     after_id: int, expect: dict, timeout_ms: int = DEFAULT_MATCH_TIMEOUT_MS) -> dict:
    """Run one organic-producer case. Returns {case, ok, detail}."""
    lib.invoke_debug_post(base_url, path, body, timeout=DEBUG_POST_TIMEOUT)
    payload = wait_for_fx_match(
        base_url, after_id,
        lambda ev, pl: match_event(ev, pl, expect),
        timeout_ms=timeout_ms,
    )
    if payload is not None:
        return {"case": name, "ok": True, "detail": f"cue={payload.get('cueId')} rgb={payload.get('rgb')}"}
    return {"case": name, "ok": False, "detail": "no matching shown"}


# ---------------------------------------------------------------------------
# Main run
# ---------------------------------------------------------------------------

def run(base_url: str, target_ptr: str, col: int, row: int, out_json: Path,
        event_timeout: int = 10, search_budget_sec: float = 30.0) -> int:
    """Run the full prove-vfx matrix. Returns exit code."""
    url, source = lib.resolve_base_url(base_url)
    plays = build_plays(col, row, target_ptr)

    results: list[dict] = []
    passed = True

    def record(case: str, ok: bool, detail: str) -> None:
        nonlocal passed
        if not ok:
            passed = False
        results.append({"case": case, "ok": ok, "detail": detail})
        status = "PASS" if ok else "FAIL"
        print(f"[{status}] {case} -- {detail}", file=sys.stderr)

    try:
        # --- main play loop ---
        for play in plays:
            time.sleep(CASE_PAUSE_SEC)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            try:
                result = run_case(url, play, after)
            except lib.Refusal as refusal:
                result = {"case": play["name"], "ok": False,
                          "detail": f"REFUSED {refusal.reason}: {refusal.detail}"}
            record(result["case"], result["ok"], result["detail"])

        # --- world-flash alias ---
        time.sleep(CASE_PAUSE_SEC)
        after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
        lib.invoke_debug_post(url, "/fx/world-flash", {"col": col, "row": row}, timeout=DEBUG_POST_TIMEOUT)
        wf = wait_for_fx_match(
            url, after,
            lambda ev, pl: ev.get("kind") == "debug.fx.shown"
            and pl.get("cueId") == "debug.probe" and pl.get("amount") == 0,
        )
        if wf is not None:
            prims = wf.get("primitives") or []
            record("world-flash-alias", True, f"prims={'+'.join(str(p) for p in prims)}")
        else:
            record("world-flash-alias", False, "no shown")

        # --- organic producer paths ---
        if target_ptr:
            # combat: enqueue-delta drives dispatcher -> funnel mutation -> cue
            time.sleep(CASE_PAUSE_SEC)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            result = run_organic_case(
                url, "organic-combat-fire", "/effect/enqueue-delta",
                {"targetPtr": target_ptr, "amount": -50,
                 "elementPayload": [{"element": "fire", "weight": 1.0}]},
                after,
                {"kind": "debug.fx.shown", "cueId": "combat.hit", "rgb": "#FF5A28"},
            )
            record(result["case"], result["ok"], result["detail"])

            # status: debug.status.apply drives StatusRuntime.Apply -> OnApplied -> cue + sustained start
            time.sleep(CASE_PAUSE_SEC)
            expire_window = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            started = _apply_status_until_started(url, target_ptr, "wither", 4000, event_timeout, search_budget_sec)
            record("organic-status-wither", started,
                   f"started={started} (apply-roll retried)")

            # sustained lifecycle: natural expire ends the visual (reason=expired)
            expired = wait_for_fx_match(
                url, expire_window,
                lambda ev, pl: ev.get("kind") == "debug.fx.state.ended"
                and pl.get("statusId") == "wither" and pl.get("reason") == "expired",
                timeout_ms=10000,
            )
            if expired is not None:
                record("sustain-expire", True, "reason=expired")
            else:
                record("sustain-expire", False, "no expired end within 10s")

            # refresh must not flicker: once started, further applies refresh -- zero ended events
            time.sleep(CASE_PAUSE_SEC)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            started = _apply_status_until_started(url, target_ptr, "pact_mark", 15000, event_timeout, search_budget_sec)
            lib.invoke_debug_post(url, "/status/apply",
                                 {"statusId": "pact_mark", "hostPtr": target_ptr,
                                  "amount": 5, "durationMs": 15000},
                                 timeout=DEBUG_POST_TIMEOUT)
            time.sleep(1.5)
            events = get_fx_events(url, after, event_timeout)
            started_n = sum(1 for e in events
                            if e.get("kind") == "debug.fx.state.started"
                            and (lib.get_debug_payload(e) or {}).get("statusId") == "pact_mark")
            ended_n = sum(1 for e in events
                          if e.get("kind") == "debug.fx.state.ended"
                          and (lib.get_debug_payload(e) or {}).get("statusId") == "pact_mark")
            ok = started and started_n == 1 and ended_n == 0
            record("sustain-refresh-no-flicker", ok, f"started={started_n} ended={ended_n}")

            # host death reaps sustained visuals (reason=host-gone). Runs LAST.
            time.sleep(CASE_PAUSE_SEC)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            started = _apply_status_until_started(url, target_ptr, "rally", 30000, event_timeout, search_budget_sec)
            lib.invoke_debug_post(url, "/kill", {"target": "all"}, timeout=DEBUG_POST_TIMEOUT)
            gone = wait_for_fx_match(
                url, after,
                lambda ev, pl: ev.get("kind") == "debug.fx.state.ended"
                and pl.get("reason") == "host-gone",
                timeout_ms=8000,
            )
            ok = started and gone is not None
            if gone is not None:
                record("sustain-host-gone", ok, "reason=host-gone")
            else:
                record("sustain-host-gone", ok, f"started={started}, no host-gone end within 8s")
        else:
            record("organic-paths", False, "SKIPPED (no --target-ptr)")

        # --- rate limit: rapid same-cue/cell volley -> at least one collapses ---
        time.sleep(CASE_PAUSE_SEC)
        after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
        for _ in range(4):
            lib.invoke_debug_post(url, "/fx/play",
                                 {"cueId": "debug.probe", "col": col, "row": row},
                                 timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.9)
        events = get_fx_events(url, after, event_timeout)
        shown_n = sum(1 for e in events if e.get("kind") == "debug.fx.shown")
        limited = sum(1 for e in events
                      if e.get("kind") == "debug.fx.skipped"
                      and (lib.get_debug_payload(e) or {}).get("reason") == "rate-limited")
        ok = limited >= 1 and shown_n <= 3 and shown_n >= 1
        record("rate-limit-collapse", ok, f"shown={shown_n} limited={limited} (volley of 4)")

        # --- mute roundtrip ---
        time.sleep(CASE_PAUSE_SEC)
        lib.invoke_debug_post(url, "/fx/mute", {"cueId": "debug.probe"}, timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.4)
        after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
        lib.invoke_debug_post(url, "/fx/play",
                             {"cueId": "debug.probe", "col": col, "row": row},
                             timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.7)
        events = get_fx_events(url, after, event_timeout)
        muted = sum(1 for e in events
                    if e.get("kind") == "debug.fx.skipped"
                    and (lib.get_debug_payload(e) or {}).get("reason") == "muted")
        lib.invoke_debug_post(url, "/fx/unmute", {"cueId": "debug.probe"}, timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.5)
        after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
        lib.invoke_debug_post(url, "/fx/play",
                             {"cueId": "debug.probe", "col": col, "row": row},
                             timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.7)
        events = get_fx_events(url, after, event_timeout)
        unmuted = sum(1 for e in events if e.get("kind") == "debug.fx.shown")
        ok = muted >= 1 and unmuted >= 1
        record("mute-roundtrip", ok, f"muted={muted} unmutedShown={unmuted}")

        # --- master toggle off -> disabled ---
        try:
            set_cheat(url, "SYS-DAMAGE-FX", False)
            time.sleep(0.6)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            lib.invoke_debug_post(url, "/fx/play",
                                 {"cueId": "debug.probe", "col": col, "row": row},
                                 timeout=DEBUG_POST_TIMEOUT)
            time.sleep(0.7)
            events = get_fx_events(url, after, event_timeout)
            disabled = sum(1 for e in events
                           if e.get("kind") == "debug.fx.skipped"
                           and (lib.get_debug_payload(e) or {}).get("reason") == "disabled")
            ok = disabled >= 1
            record("master-toggle-off", ok, f"disabledSkips={disabled}")
        finally:
            set_cheat(url, "SYS-DAMAGE-FX", True)

        # --- element toggle off -> element hit degrades to the plain-damage path ---
        try:
            set_cheat(url, "SYS-ELEMENT-FX", False)
            time.sleep(0.6)
            after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
            lib.invoke_debug_post(url, "/fx/play",
                                 {"cueId": "combat.hit", "col": col, "row": row,
                                  "amount": -68,
                                  "elements": [{"element": "fire", "weight": 1.0}]},
                                 timeout=DEBUG_POST_TIMEOUT)
            p = wait_for_fx_match(
                url, after,
                lambda ev, pl: ev.get("kind") == "debug.fx.skipped"
                and pl.get("cueId") == "combat.hit" and pl.get("amount") == -68
                and pl.get("reason") == "no-element",
            )
            if p is not None:
                record("element-toggle-off", True, "skip=no-element")
            else:
                record("element-toggle-off", False, "expected no-element skip not seen")
        finally:
            set_cheat(url, "SYS-ELEMENT-FX", True)

        # --- fx.list roundtrip ---
        after = lib.get_debug_max_event_id(url, event_timeout, search_budget_sec)
        lib.invoke_debug_post(url, "/fx/list", {}, timeout=DEBUG_POST_TIMEOUT)
        time.sleep(0.6)
        events = get_fx_events(url, after, event_timeout)
        list_events = [e for e in events if e.get("kind") == "debug.fx.list"]
        list_ok = False
        cues_str = ""
        if list_events:
            lp = lib.get_debug_payload(list_events[-1])
            if isinstance(lp, dict):
                cues = lp.get("cues") or []
                cues_str = ",".join(str(c) for c in cues)
                list_ok = "combat.hit" in cues and "debug.probe" in cues
        record("fx-list", list_ok, f"cues={cues_str}")

    except lib.Refusal as refusal:
        record("run", False, f"REFUSED {refusal.reason}: {refusal.detail}")

    # --- write verdict JSON ---
    verdict = {
        "at": datetime.now().astimezone().isoformat(),
        "baseUrl": url,
        "targetPtr": target_ptr,
        "pass": passed,
        "results": results,
    }
    out_json.parent.mkdir(parents=True, exist_ok=True)
    with open(out_json, "w", encoding="utf-8", newline="\n") as f:
        json.dump(verdict, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"prove-vfx: {'PASS' if passed else 'FAIL'} ({len(results)} cases) -> {out_json}",
          file=sys.stderr)
    return 0 if passed else 1


def _apply_status_until_started(base_url: str, ptr: str, status_id: str, duration_ms: int,
                                event_timeout: int, search_budget_sec: float,
                                tries: int = 6) -> bool:
    """Apply a status until it starts. LIVE status applies carry a real apply-roll (~50% for
    neutral derived) -- a single POST can legitimately resist. Retry until the sustained set
    starts; refresh semantics make extra applies harmless."""
    for _ in range(tries):
        win = lib.get_debug_max_event_id(base_url, event_timeout, search_budget_sec)
        lib.invoke_debug_post(base_url, "/status/apply",
                             {"statusId": status_id, "hostPtr": ptr,
                              "amount": 20, "durationMs": duration_ms},
                             timeout=DEBUG_POST_TIMEOUT)
        p = wait_for_fx_match(
            base_url, win,
            lambda ev, pl: ev.get("kind") == "debug.fx.state.started"
            and pl.get("statusId") == status_id,
            timeout_ms=2500,
        )
        if p is not None:
            return True
    return False


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="prove_vfx",
        description="Live prove: VFX cue -> recipe -> primitive pipeline "
                    "(replaces prove-vfx.ps1).",
    )
    parser.add_argument("--base-url", default="",
                        help="server base URL (default: $FUSIONRPG_SERVER_URL or the built-in default)")
    parser.add_argument("--target-ptr", default="",
                        help="living zombie pointer for ptr-anchored and organic cases")
    parser.add_argument("--col", type=int, default=DEFAULT_COL)
    parser.add_argument("--row", type=int, default=DEFAULT_ROW)
    parser.add_argument("--out-json", default=str(DEFAULT_OUT_JSON),
                        help="verdict JSON output path")
    parser.add_argument("--event-timeout", type=int, default=10,
                        help="per-request timeout for event reads (seconds)")
    parser.add_argument("--search-budget-sec", type=float, default=30.0,
                        help="total budget for the event-id binary search (seconds)")
    parser.add_argument("--json", action="store_true",
                        help="emit the verdict as JSON on stdout")
    args = parser.parse_args(argv)

    try:
        exit_code = run(
            base_url=args.base_url,
            target_ptr=args.target_ptr,
            col=args.col,
            row=args.row,
            out_json=Path(args.out_json),
            event_timeout=args.event_timeout,
            search_budget_sec=args.search_budget_sec,
        )
        if args.json:
            with open(args.out_json, "r", encoding="utf-8") as f:
                print(f.read())
        return exit_code
    except lib.Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail,
                              "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail,
                              "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
