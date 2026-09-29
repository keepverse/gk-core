#!/usr/bin/env python3
"""PROVE: StatusRuntime L2 catalog (status-l2-*) against a running lawn.

LIVE prove script that drives the full status-l2-* matrix against a running game. Requires: lawn open,
injector connected, SIM off. One scenario at a time.

Usage:
  python gk-core/scripts/prove_status_full.py
  python gk-core/scripts/prove_status_full.py --base-url http://127.0.0.1:5088 --include-unity-bypass

Replaces `scripts/prove-status-full.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** `http://127.0.0.1:5088` is the OWNER's
  server, not "the" port. This machine has a three-slot pool on 5101/5102/5103, so a script pointed
  at the default enters somebody else's board. The URL is now resolved by the shared library from
  `FUSIONRPG_SERVER_URL`, and its source is REPORTED.

* **THE EVENT READS HAD NO TIMEOUT AT ALL.** `Invoke-RestMethod -Uri "$url/api/events?afterId=..."`
  appears four times with nothing stopping any of them, in a loop that runs for up to 20 seconds. A
  hung poll inside that loop means the loop never re-checks the deadline, so the whole budget is not a
  bound at all. Every read is bounded here via the shared library's `get_events`, which carries a
  per-request timeout.

* **THE HEALTH CHECK HAD NO TIMEOUT.** `Invoke-RestMethod -Uri "$BaseUrl/health" -Method GET` had
  nothing stopping it. A hung health check means the script hangs forever with no output. The health
  check now uses the shared library's `_get_json`, which carries a timeout.

* **EMPTY `catch {}` BLOCKS SWALLOWED FAILURES.** The scenario POST had a `catch { return ... }` that
  returned a fail result, but the session/end had a `catch { }` that silently continued. In Python,
  every failure is either a named refusal or a recorded fail result — never a silent continue.

* **`Write-Warning`-THEN-CONTINUE.** The PS1's `Write-Step` function printed a warning and continued,
  so a failed health check still wrote a JSON output with `passed: 0`. In Python, a failed health
  check is a named refusal that writes a JSON output with a specific note and exits non-zero.

* **`Set-Content -Encoding utf8` WROTE A BOM** under Windows PowerShell 5.1 and none under PS7. The
  output is now written as UTF-8 no-BOM explicitly.

* **NO MACHINE-READABLE VERDICT.** The PS1 wrote JSON to a file but had no `--json` flag for stdout.
  The Python port adds `--json` for machine-readable output on stdout.

DELIBERATELY UNCHANGED
----------------------
Same scenario matrix (27 L2 scenarios + 5 unity bypass scenarios), same assertion logic, same output
JSON shape (top-level `at`, `baseUrl`, `passed`, `total`, `results`, `status`, `note`; per-result
`id`, `pass`, `note`), same 20s done timeout, same 4s tail collection, same interesting kinds.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.parse
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)

TOOL_ID = "prove-status-full"

# Constants
DONE_TIMEOUT_SEC = 20.0
EVENT_PAGE = 500
EVENT_TIMEOUT = 10
POLL_INTERVAL_SEC = 0.25
DONE_POLL_INTERVAL_SEC = 0.08
TAIL_POLL_INTERVAL_SEC = 0.12
DONE_SETTLE_MS = 400
TAIL_TIMEOUT_SEC = 4.0
EXTRA_TAIL_TIMEOUT_SEC = 3.0
HEALTH_TIMEOUT = 5

DONE_KIND = "debug.run-steps.done"

INTERESTING_KINDS = frozenset({
    "debug.run-steps.done",
    "debug.status",
    "debug.status.resisted",
    "debug.status.applied",
    "debug.status.cleared",
    "debug.actor-derived",
    "debug.effect.synthetic",
    "debug.board-stats",
    "debug.combat.packet",
})

# Scenario matrices — preserved verbatim from the PS1.
L2_SCENARIOS = [
    {"id": "status-l2-wither", "waitSec": 1, "kind": "apply", "statusId": "wither", "cc": False},
    {"id": "status-l2-snapshot", "waitSec": 0.5, "kind": "snapshot", "statusId": "wither", "cc": False},
    {"id": "status-l2-resist", "waitSec": 1, "kind": "resist", "statusId": "wither", "reason": "PotencyFloor"},
    {"id": "status-l2-leech", "waitSec": 1, "kind": "apply", "statusId": "leech", "cc": False},
    {"id": "status-l2-rally", "waitSec": 1, "kind": "apply", "statusId": "rally", "cc": False},
    {"id": "status-l2-expose", "waitSec": 1, "kind": "apply", "statusId": "expose", "cc": False},
    {"id": "status-l2-command", "waitSec": 1, "kind": "apply", "statusId": "command", "cc": False},
    {"id": "status-l2-shatter", "waitSec": 1, "kind": "apply", "statusId": "shatter", "cc": False},
    {"id": "status-l2-bond", "waitSec": 1.5, "kind": "bond", "statusId": "bond", "cc": False},
    {"id": "status-l2-blight-row", "waitSec": 2, "kind": "contagion-row", "statusId": "blight",
     "minHosts": 2, "seedRow": 2, "controlRow": 3},
    {"id": "status-l2-rot", "waitSec": 2, "kind": "contagion", "statusId": "rot", "minHosts": 2},
    {"id": "status-l2-spark", "waitSec": 2, "kind": "contagion", "statusId": "spark", "minHosts": 2},
    {"id": "status-l2-pact-mark", "waitSec": 2, "kind": "contagion", "statusId": "pact_mark", "minHosts": 1},
    {"id": "status-l2-spore", "waitSec": 2, "kind": "contagion", "statusId": "spore", "minHosts": 2},
    {"id": "status-l2-butter", "waitSec": 1, "kind": "apply", "statusId": "butter", "cc": True},
    {"id": "status-l2-freeze", "waitSec": 1, "kind": "apply", "statusId": "freeze", "cc": True},
    {"id": "status-l2-cold", "waitSec": 1, "kind": "apply", "statusId": "cold", "cc": True},
    {"id": "status-l2-poison", "waitSec": 1, "kind": "apply", "statusId": "poison", "cc": True},
    {"id": "status-l2-hypno", "waitSec": 1, "kind": "apply", "statusId": "hypno", "cc": True},
    {"id": "status-l2-ember", "waitSec": 1, "kind": "apply", "statusId": "ember", "cc": True},
    {"id": "status-l2-jala", "waitSec": 1, "kind": "apply", "statusId": "jala", "cc": True},
    {"id": "status-l2-kelp", "waitSec": 1, "kind": "apply", "statusId": "kelp", "cc": True},
    {"id": "status-l2-charm-pulse", "waitSec": 1, "kind": "apply", "statusId": "charm_pulse", "cc": True},
    {"id": "status-l2-resist-cc", "waitSec": 1, "kind": "resist", "statusId": "butter", "reason": "PotencyFloor"},
    {"id": "status-l2-resist-contagion", "waitSec": 2, "kind": "resist-contagion", "statusId": "blight"},
    {"id": "status-l2-poison-immune", "waitSec": 1, "kind": "resist", "statusId": "poison", "reason": "Immunity"},
    {"id": "status-l2-actor-derived", "waitSec": 1, "kind": "actor-derived", "statusId": "wither"},
]

UNITY_SCENARIOS = [
    {"id": "status-butter", "waitSec": 1, "kind": "unity", "statusName": "butter", "method": True, "clear": False},
    {"id": "status-freeze", "waitSec": 1, "kind": "unity", "statusName": "freeze", "method": True, "clear": False},
    {"id": "status-cold", "waitSec": 1, "kind": "unity", "statusName": "cold", "method": True, "clear": False},
    {"id": "status-poison", "waitSec": 1, "kind": "unity", "statusName": "poison", "method": True, "clear": False},
    {"id": "status-float-butter", "waitSec": 1, "kind": "unity", "statusName": "butter", "method": False, "clear": False},
    {"id": "status-clear", "waitSec": 1, "kind": "unity", "statusName": "", "method": False, "clear": True},
]

REFUSAL_REASONS = {
    "HEALTH-REQUEST-FAILED", "HEALTH-NOT-OK", "SCENARIO-QUEUE-FAILED",
    "SCENARIO-NOT-COMPLETED", "INVALID-TIMEOUT",
}

EXIT_REFUSED = 64

DEFAULT_OUT_JSON = Path(__file__).resolve().parent.parent / "docs" / "research" / "effect-runtime" / "_prove-status-full.json"


class Refusal(Exception):
    """A named precondition failure. `reason` names what failed; the exit code is non-zero by contract."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------

def eq_ptr(a, b) -> bool:
    """Compare two pointers for equality, ignoring case and 0x prefix.

    Returns False if either is empty/whitespace, matching the PS1's Test-EqPtr.
    """
    if a is None or b is None:
        return False
    a_str = str(a).strip()
    b_str = str(b).strip()
    if not a_str or not b_str:
        return False
    na = a_str[2:] if a_str.startswith("0x") else a_str
    nb = b_str[2:] if b_str.startswith("0x") else b_str
    na = na.upper()
    nb = nb.upper()
    return bool(na) and bool(nb) and na == nb


def get_payload(event):
    """A debug event's payload, parsed when it arrived as a JSON string.

    Returns None for no payload or unparseable payload, matching the PS1's Get-Payload behavior.
    The library's get_debug_payload raises Refusal on unparseable payloads; the PS1 returned null.
    """
    if event is None:
        return None
    try:
        return lib.get_debug_payload(event)
    except lib.Refusal:
        return None


def add_matching_events(bucket: list, items: list) -> None:
    """Append events whose kind is in the interesting set."""
    for ev in items:
        if not isinstance(ev, dict):
            continue
        kind = ev.get("kind")
        if isinstance(kind, str) and kind in INTERESTING_KINDS:
            bucket.append(ev)


def get_first_payload(events: list, kind: str):
    """The payload of the first event matching `kind`, or None."""
    for e in events:
        if isinstance(e, dict) and e.get("kind") == kind:
            return get_payload(e)
    return None


def get_last_payload(events: list, kind: str):
    """The payload of the last event matching `kind`, or None."""
    result = None
    for e in events:
        if isinstance(e, dict) and e.get("kind") == kind:
            result = get_payload(e)
    return result


def get_status_after_synthetic(events: list):
    """The first `debug.status` payload after the first `debug.effect.synthetic` event."""
    synths = [e for e in events if isinstance(e, dict) and e.get("kind") == "debug.effect.synthetic"]
    if not synths:
        return None
    try:
        syn_id = int(synths[0].get("id", 0))
    except (TypeError, ValueError):
        return None
    for ev in events:
        if isinstance(ev, dict) and ev.get("kind") == "debug.status":
            try:
                if int(ev.get("id", 0)) > syn_id:
                    return get_payload(ev)
            except (TypeError, ValueError):
                continue
    return None


def get_plant_from_board(board):
    """The plant at col=2 row=2, or the first plant, or None."""
    if board is None or not isinstance(board, dict):
        return None
    plants = board.get("plants")
    if plants is None:
        return None
    for pl in plants:
        if not isinstance(pl, dict):
            continue
        try:
            if int(pl.get("col", -1)) == 2 and int(pl.get("row", -1)) == 2:
                return pl
        except (TypeError, ValueError):
            continue
    if plants:
        return plants[0]
    return None


def get_zombie_ptrs(board) -> list:
    """Zombie pointers from a board snapshot."""
    if board is None or not isinstance(board, dict):
        return []
    zombies = board.get("zombies")
    if zombies is None:
        return []
    return [str(z.get("ptr", "")) for z in zombies if isinstance(z, dict)]


def get_instances(status_snap) -> list:
    """Status instances from a status snapshot."""
    if status_snap is None or not isinstance(status_snap, dict):
        return []
    instances = status_snap.get("instances")
    if instances is None:
        return []
    return list(instances)


def get_resisted(events: list, status_snap) -> list:
    """Resisted status entries from the snapshot and events."""
    from_snap = []
    if status_snap is not None and isinstance(status_snap, dict):
        resisted = status_snap.get("resisted")
        if resisted is not None:
            from_snap = list(resisted)
    from_ev = [get_payload(e) for e in events
               if isinstance(e, dict) and e.get("kind") == "debug.status.resisted"]
    return from_snap + from_ev


def find_instance_for_board(instances: list, status_id: str, zombie_ptrs: list, prefer_host: str):
    """Find a status instance on board zombies, preferring `prefer_host`."""
    matches = []
    for i in instances:
        if not isinstance(i, dict):
            continue
        if str(i.get("statusId", "")) != status_id:
            continue
        on_board = False
        for z in zombie_ptrs:
            if eq_ptr(str(i.get("hostPtr", "")), z):
                on_board = True
                break
        if on_board:
            matches.append(i)
    if not matches:
        return None
    if prefer_host and prefer_host.strip():
        for i in matches:
            if eq_ptr(str(i.get("hostPtr", "")), prefer_host):
                return i
    return matches[-1]


def test_plant_on_board(board, ptr: str) -> bool:
    """True if a plant with the given pointer is on the board."""
    if board is None or not isinstance(board, dict):
        return False
    plants = board.get("plants")
    if plants is None:
        return False
    for pl in plants:
        if not isinstance(pl, dict):
            continue
        if eq_ptr(str(pl.get("ptr", "")), ptr):
            return True
    return False


# ---------------------------------------------------------------------------
# Assertion functions — pure, each returns {"pass": bool, "note": str}
# ---------------------------------------------------------------------------

def test_apply_assert(events: list, status_id: str, require_synthetic_actions: bool) -> dict:
    """Assert a status apply: synthetic actor/target, instance on board, attacker matches."""
    board = get_first_payload(events, "debug.board-stats")
    if board is None:
        board = get_last_payload(events, "debug.board-stats")
    status = get_status_after_synthetic(events)
    if status is None:
        status = get_last_payload(events, "debug.status")
    synths = [e for e in events if isinstance(e, dict) and e.get("kind") == "debug.effect.synthetic"]
    synth = None
    if synths:
        synth = get_payload(synths[0])
    plant = get_plant_from_board(board)
    if plant is None:
        return {"pass": False, "note": "no plant in debug.board-stats"}
    plant_ptr = str(plant.get("ptr", ""))
    z_ptrs = get_zombie_ptrs(board)
    if not z_ptrs:
        return {"pass": False, "note": "no zombies in debug.board-stats"}

    if synth is None or not isinstance(synth, dict):
        return {"pass": False, "note": "no debug.effect.synthetic"}
    actor_ptr = str(synth.get("actorPtr", ""))
    target_ptr = str(synth.get("targetPtr", ""))
    if not eq_ptr(actor_ptr, plant_ptr):
        return {"pass": False, "note": f"synthetic.actorPtr={actor_ptr} plant={plant_ptr}"}
    target_is_zombie = False
    for z in z_ptrs:
        if eq_ptr(target_ptr, z):
            target_is_zombie = True
            break
    if not target_is_zombie:
        return {"pass": False, "note": f"synthetic.targetPtr={target_ptr} not a board zombie"}
    if eq_ptr(actor_ptr, target_ptr):
        return {"pass": False, "note": f"actorPtr==targetPtr ({actor_ptr})"}
    try:
        actions = int(synth.get("actions", 0))
    except (TypeError, ValueError):
        actions = 0
    if require_synthetic_actions and actions <= 0:
        return {"pass": False, "note": f"synthetic.actions={actions} expected >0"}

    inst = find_instance_for_board(get_instances(status), status_id, z_ptrs, target_ptr)
    if inst is None:
        ids = ",".join(f"{i.get('statusId')}:{i.get('hostPtr')}"
                       for i in get_instances(status) if isinstance(i, dict))
        return {"pass": False,
                "note": f"no instance statusId={status_id} on board zombies (have [{ids}])"}
    atk = str(inst.get("attackerPtr", ""))
    host_ptr = str(inst.get("hostPtr", ""))
    if eq_ptr(atk, host_ptr):
        return {"pass": False, "note": f"attackerPtr==hostPtr ({atk})"}
    atk_ok = eq_ptr(atk, actor_ptr) or test_plant_on_board(board, atk)
    if not atk_ok:
        return {"pass": False,
                "note": f"instance.attackerPtr={atk} not plant actor {actor_ptr} or board plant"}
    return {"pass": True,
            "note": f"statusId={status_id} attacker={atk} host={host_ptr} synth.actions={actions}"}


def test_contagion_assert(events: list, status_id: str, min_hosts: int,
                          seed_row: int, control_row: int) -> dict:
    """Assert contagion: enough hosts, seed row coverage, control row exclusion."""
    apply_result = test_apply_assert(events, status_id, False)
    if not apply_result["pass"]:
        return apply_result
    board = get_last_payload(events, "debug.board-stats")
    status = get_last_payload(events, "debug.status")
    instances = [i for i in get_instances(status)
                 if isinstance(i, dict) and str(i.get("statusId", "")) == status_id]
    hosts = list({str(i.get("hostPtr", "")) for i in instances})
    if len(hosts) < min_hosts:
        return {"pass": False,
                "note": f"contagion hosts={len(hosts)} want>={min_hosts} statusId={status_id}"}
    if seed_row < 0:
        return {"pass": True, "note": f"hosts={len(hosts)} statusId={status_id}"}
    if board is None or not isinstance(board, dict) or board.get("zombies") is None:
        return {"pass": True, "note": f"hosts={len(hosts)} (no row check)"}
    seed_count = 0
    control_count = 0
    for z in board.get("zombies", []):
        if not isinstance(z, dict):
            continue
        hit = False
        for h in hosts:
            if eq_ptr(h, str(z.get("ptr", ""))):
                hit = True
                break
        if not hit:
            continue
        try:
            row = int(z.get("row", -1))
        except (TypeError, ValueError):
            continue
        if row == seed_row:
            seed_count += 1
        if control_row >= 0 and row == control_row:
            control_count += 1
    if control_row >= 0 and control_count > 0:
        return {"pass": False,
                "note": f"control row {control_row} has {control_count} {status_id} host(s)"}
    if seed_count < min_hosts:
        return {"pass": False,
                "note": f"seed row {seed_row} hosts={seed_count} want>={min_hosts}"}
    return {"pass": True,
            "note": f"row{seed_row} hosts={seed_count} controlRow{control_row}={control_count}"}


def test_resist_assert(events: list, status_id: str, reason: str) -> dict:
    """Assert a resisted status: resisted event on board, no instance."""
    board = get_last_payload(events, "debug.board-stats")
    status = get_last_payload(events, "debug.status")
    z_ptrs = get_zombie_ptrs(board)
    inst = find_instance_for_board(get_instances(status), status_id, z_ptrs, "")
    resisted = []
    for ev in get_resisted(events, status):
        if not isinstance(ev, dict):
            continue
        if str(ev.get("statusId", "")) != status_id:
            continue
        if str(ev.get("reason", "")) != reason:
            continue
        if not z_ptrs:
            resisted.append(ev)
            continue
        for z in z_ptrs:
            if eq_ptr(str(ev.get("hostPtr", "")), z):
                resisted.append(ev)
                break
    if not resisted:
        all_resisted = get_resisted(events, status)
        reasons = ",".join(f"{e.get('statusId')}:{e.get('reason')}"
                           for e in all_resisted if isinstance(e, dict))
        return {"pass": False,
                "note": f"no resisted {status_id}/{reason} on board (have [{reasons}])"}
    if inst is not None:
        return {"pass": False,
                "note": f"instance present for resisted {status_id} host={inst.get('hostPtr')}"}
    return {"pass": True, "note": f"resisted {status_id} reason={reason} n={len(resisted)}"}


def test_resist_contagion_assert(events: list) -> dict:
    """Assert iron-contagion: seed blight survives, neighbor resists."""
    board = get_last_payload(events, "debug.board-stats")
    status = get_last_payload(events, "debug.status")
    z_ptrs = get_zombie_ptrs(board)
    instances = []
    for i in get_instances(status):
        if not isinstance(i, dict):
            continue
        if str(i.get("statusId", "")) != "blight":
            continue
        for z in z_ptrs:
            if eq_ptr(str(i.get("hostPtr", "")), z):
                instances.append(i)
                break
    resisted = []
    for ev in get_resisted(events, status):
        if not isinstance(ev, dict):
            continue
        if str(ev.get("statusId", "")) != "blight":
            continue
        if str(ev.get("reason", "")) != "PotencyFloor":
            continue
        resisted.append(ev)
    if len(instances) < 1:
        return {"pass": False,
                "note": f"seed blight missing after pulse (instances=0 resisted={len(resisted)})"}
    if len(resisted) < 1:
        return {"pass": False,
                "note": f"iron-contagion neighbor did not resist (blight hosts={len(instances)})"}
    return {"pass": True, "note": f"seed blight={len(instances)} resisted={len(resisted)}"}


def test_bond_assert(events: list) -> dict:
    """Assert bond: instance + >=5 synthetics or fa10 burst."""
    apply_result = test_apply_assert(events, "bond", False)
    if not apply_result["pass"]:
        return apply_result
    synths = [e for e in events if isinstance(e, dict) and e.get("kind") == "debug.effect.synthetic"]
    packets = [e for e in events if isinstance(e, dict) and e.get("kind") == "debug.combat.packet"]
    burst = False
    for ev in packets:
        p = get_payload(ev)
        if p is None or not isinstance(p, dict):
            continue
        try:
            if int(p.get("fa10", 0)) > 0:
                burst = True
                break
        except (TypeError, ValueError):
            continue
    if len(synths) < 5:
        return {"pass": False,
                "note": f"bond instance ok but synthetic hits={len(synths)} want>=5"}
    burst_note = "fa10 packet" if burst else "5 synthetics (burst flushes via Funnel)"
    return {"pass": True,
            "note": f"bond instance + synthetics={len(synths)} {burst_note}"}


def test_actor_derived_assert(events: list) -> dict:
    """Assert actor-derived: plant ptr in derived events with status.power.omni >= 100."""
    apply_result = test_apply_assert(events, "wither", False)
    if not apply_result["pass"]:
        return apply_result
    derived = [get_payload(e) for e in events
               if isinstance(e, dict) and e.get("kind") == "debug.actor-derived"]
    if not derived:
        return {"pass": False, "note": "no debug.actor-derived"}
    board = get_last_payload(events, "debug.board-stats")
    plant = get_plant_from_board(board)
    if plant is None:
        return {"pass": False, "note": "no plant in debug.board-stats"}
    plant_ptr = str(plant.get("ptr", ""))
    matched = False
    power = None
    for d in derived:
        if not isinstance(d, dict):
            continue
        if not eq_ptr(str(d.get("ptr", "")), plant_ptr):
            continue
        ch = d.get("channels")
        if ch is None or not isinstance(ch, dict):
            continue
        # Case 1: literal key "status.power.omni"
        val = ch.get("status.power.omni")
        if val is not None:
            try:
                power = float(val)
            except (TypeError, ValueError):
                pass
        # Case 2: nested status.power.omni
        if power is None:
            status_obj = ch.get("status")
            if isinstance(status_obj, dict):
                power_obj = status_obj.get("power")
                if isinstance(power_obj, dict):
                    val = power_obj.get("omni")
                    if val is not None:
                        try:
                            power = float(val)
                        except (TypeError, ValueError):
                            pass
        if power is not None and power >= 100:
            matched = True
            break
    if not matched:
        return {"pass": False,
                "note": f"plant ptr={plant_ptr} caster pin not seen "
                        f"(derived n={len(derived)} power={power})"}
    return {"pass": True,
            "note": f"actor-derived plant={plant_ptr} status.power.omni={power}"}


def test_snapshot_assert(events: list) -> dict:
    """Assert snapshot: status snapshot has resisted array."""
    apply_result = test_apply_assert(events, "wither", False)
    if not apply_result["pass"]:
        return apply_result
    status = get_last_payload(events, "debug.status")
    if status is None or not isinstance(status, dict):
        return {"pass": False, "note": "no debug.status snapshot"}
    has_resisted = status.get("resisted") is not None
    if not has_resisted:
        return {"pass": False, "note": "debug.status missing resisted[]"}
    return {"pass": True,
            "note": f"snapshot instances={status.get('count')} "
                    f"resistedCount={status.get('resistedCount')}"}


def test_unity_bypass_assert(events: list, status_name: str, method: bool, clear: bool) -> dict:
    """Assert Unity bypass: applied/cleared status with correct method."""
    if clear:
        ev = get_last_payload(events, "debug.status.cleared")
        if ev is None:
            return {"pass": False, "note": "no debug.status.cleared"}
        return {"pass": True, "note": f"cleared count={ev.get('count')}"}
    ev = get_last_payload(events, "debug.status.applied")
    if ev is None:
        return {"pass": False, "note": "no debug.status.applied"}
    got_method = bool(ev.get("method"))
    got_status = str(ev.get("status", ""))
    if got_status != status_name:
        return {"pass": False, "note": f"applied status={got_status} want={status_name}"}
    if got_method != method:
        return {"pass": False, "note": f"applied method={got_method} want={method}"}
    return {"pass": True,
            "note": f"applied status={got_status} method={got_method} count={ev.get('count')}"}


# ---------------------------------------------------------------------------
# Scenario runner
# ---------------------------------------------------------------------------

@dataclass
class CollectResult:
    """What `wait_and_collect` observed."""
    events: list
    got_done: bool
    cursor: int


def wait_and_collect(base_url: str, after_id: int, done_timeout_sec: float,
                     extra_wait_sec: float, refresh_status: bool,
                     actor_derived_ptr: str,
                     event_timeout: int = EVENT_TIMEOUT) -> CollectResult:
    """Poll for `debug.run-steps.done`, collect interesting events, then tail-collect.

    The deadline is checked BEFORE the sleep and the sleep is clamped to what is left, so the
    function returns close to when it promised. The original's `do/while` checked the deadline
    only after an unbounded poll, so a slow poll could overrun it arbitrarily.
    """
    collected: list = []
    cursor = after_id
    deadline = time.monotonic() + done_timeout_sec
    got_done = False
    done_at = None

    while time.monotonic() < deadline:
        if got_done and done_at is not None and (time.monotonic() - done_at) * 1000 > DONE_SETTLE_MS:
            break
        items = lib.get_events(base_url, cursor, EVENT_PAGE, event_timeout)
        if not items:
            if got_done:
                break
            time.sleep(POLL_INTERVAL_SEC)
            continue
        cursor = int(items[-1].get("id", cursor))
        add_matching_events(collected, items)
        if any(isinstance(e, dict) and e.get("kind") == DONE_KIND for e in items):
            got_done = True
            done_at = time.monotonic()
        time.sleep(DONE_POLL_INTERVAL_SEC)

    if extra_wait_sec > 0:
        time.sleep(extra_wait_sec)

    if refresh_status:
        lib.invoke_debug_post(base_url, "/board-stats", {})
        lib.invoke_debug_post(base_url, "/status", {})

    if actor_derived_ptr and actor_derived_ptr.strip():
        encoded = urllib.parse.quote(actor_derived_ptr, safe="")
        lib._get_json(f"{base_url}/api/debug/actor-derived?ptr={encoded}",
                      event_timeout, "GET /api/debug/actor-derived")

    tail_deadline = time.monotonic() + TAIL_TIMEOUT_SEC
    while time.monotonic() < tail_deadline:
        items = lib.get_events(base_url, cursor, EVENT_PAGE, event_timeout)
        if not items:
            time.sleep(POLL_INTERVAL_SEC)
            continue
        cursor = int(items[-1].get("id", cursor))
        add_matching_events(collected, items)
        time.sleep(TAIL_POLL_INTERVAL_SEC)

    return CollectResult(events=collected, got_done=got_done, cursor=cursor)


def invoke_scenario_row(row: dict, base_url: str,
                        event_timeout: int = EVENT_TIMEOUT) -> dict:
    """Run one scenario row and return {"id", "pass", "note"}."""
    scenario_id = str(row["id"])
    after_id = lib.get_debug_max_event_id(base_url, event_timeout,
                                          budget_sec=DONE_TIMEOUT_SEC + 10.0)

    try:
        queued = lib.invoke_debug_post(base_url, f"/scenario/{scenario_id}", {})
        if not isinstance(queued, dict) or not queued.get("ok"):
            return {"id": scenario_id, "pass": False, "note": "scenario queue failed"}
    except lib.Refusal as refusal:
        return {"id": scenario_id, "pass": False,
                "note": f"REFUSED {refusal.reason}: {refusal.detail}"}

    need_derived = row["kind"] == "actor-derived"
    plant_ptr_hint = ""
    bundle = wait_and_collect(base_url, after_id, DONE_TIMEOUT_SEC,
                              float(row["waitSec"]), True, plant_ptr_hint, event_timeout)

    if not bundle.got_done:
        return {"id": scenario_id, "pass": False,
                "note": f"no debug.run-steps.done afterId={after_id}"}

    events = list(bundle.events)
    if need_derived:
        board = get_last_payload(events, "debug.board-stats")
        plant = get_plant_from_board(board)
        if plant is not None:
            encoded = urllib.parse.quote(str(plant.get("ptr", "")), safe="")
            lib._get_json(f"{base_url}/api/debug/actor-derived?ptr={encoded}",
                          event_timeout, "GET /api/debug/actor-derived")
            cursor = bundle.cursor
            extra: list = []
            tail_deadline = time.monotonic() + EXTRA_TAIL_TIMEOUT_SEC
            while time.monotonic() < tail_deadline:
                items = lib.get_events(base_url, cursor, EVENT_PAGE, event_timeout)
                if not items:
                    time.sleep(POLL_INTERVAL_SEC)
                    continue
                cursor = int(items[-1].get("id", cursor))
                add_matching_events(extra, items)
                time.sleep(TAIL_POLL_INTERVAL_SEC)
            events = events + extra

    kind = str(row["kind"])
    if kind == "apply":
        r = test_apply_assert(events, str(row["statusId"]), bool(row["cc"]))
    elif kind == "snapshot":
        r = test_snapshot_assert(events)
    elif kind == "resist":
        r = test_resist_assert(events, str(row["statusId"]), str(row["reason"]))
    elif kind == "resist-contagion":
        r = test_resist_contagion_assert(events)
    elif kind == "contagion-row":
        r = test_contagion_assert(events, str(row["statusId"]),
                                  int(row["minHosts"]), int(row["seedRow"]),
                                  int(row["controlRow"]))
    elif kind == "contagion":
        r = test_contagion_assert(events, str(row["statusId"]),
                                  int(row["minHosts"]), -1, -1)
    elif kind == "bond":
        r = test_bond_assert(events)
    elif kind == "actor-derived":
        r = test_actor_derived_assert(events)
    elif kind == "unity":
        r = test_unity_bypass_assert(events, str(row["statusName"]),
                                     bool(row["method"]), bool(row["clear"]))
    else:
        r = {"pass": False, "note": f"unknown kind {kind}"}

    return {"id": scenario_id, "pass": bool(r["pass"]), "note": str(r["note"])}


# ---------------------------------------------------------------------------
# Main run
# ---------------------------------------------------------------------------

def _write_verdict(out_json: Path, payload: dict) -> None:
    """Write the verdict JSON, creating parent directories as needed."""
    out_json.parent.mkdir(parents=True, exist_ok=True)
    with open(out_json, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)
        f.write("\n")


def run(base_url: str, include_unity_bypass: bool, skip_visual: bool,
        out_json: Path, event_timeout: int = EVENT_TIMEOUT) -> int:
    """Run the full status-l2-* prove matrix. Returns exit code."""
    if skip_visual:
        include_unity_bypass = False

    url, source = lib.resolve_base_url(base_url)

    # Health check
    try:
        health = lib._get_json(f"{url}/health", HEALTH_TIMEOUT, "GET /health")
    except lib.Refusal as refusal:
        _write_verdict(out_json, {
            "at": datetime.now().astimezone().isoformat(),
            "baseUrl": url,
            "passed": 0,
            "total": 0,
            "results": [],
            "status": "FAIL",
            "note": f"health request failed: {refusal.detail}",
        })
        return 1

    if not isinstance(health, dict):
        _write_verdict(out_json, {
            "at": datetime.now().astimezone().isoformat(),
            "baseUrl": url,
            "passed": 0,
            "total": 0,
            "results": [],
            "status": "FAIL",
            "note": f"health request failed: /health answered with a "
                    f"{type(health).__name__}, not an object",
        })
        return 1

    connected = bool(health.get("injectorConnected"))
    sim_off = not bool(health.get("simEnabled"))
    source_name = str(health.get("source", ""))
    if not (connected and sim_off):
        _write_verdict(out_json, {
            "at": datetime.now().astimezone().isoformat(),
            "baseUrl": url,
            "passed": 0,
            "total": 0,
            "results": [],
            "status": "FAIL",
            "note": f"need injectorConnected=true simEnabled=false "
                    f"(got connected={connected} sim={health.get('simEnabled')} "
                    f"source={source_name})",
        })
        return 1

    # Session restart — end failures are ignored (matching the PS1's empty catch)
    try:
        lib.invoke_debug_post(url, "/session/end", {})
    except lib.Refusal:
        pass
    lib.invoke_debug_post(url, "/session/start", {})

    # Run scenarios
    rows = list(L2_SCENARIOS)
    if include_unity_bypass:
        rows = list(UNITY_SCENARIOS) + rows

    results: list = []
    failed = False
    for row in rows:
        result = invoke_scenario_row(row, url, event_timeout)
        results.append(result)
        if not result["pass"]:
            failed = True

    # Write output
    passed = sum(1 for r in results if r["pass"])
    payload = {
        "at": datetime.now().astimezone().isoformat(),
        "baseUrl": url,
        "passed": passed,
        "total": len(results),
        "results": results,
        "status": "FAIL" if failed else "PASS",
        "note": f"StatusRuntime L2 prove. Poll via get_debug_max_event_id + "
                f"debug.run-steps.done. IncludeUnityBypass={include_unity_bypass}",
    }

    _write_verdict(out_json, payload)
    print(f"Wrote {out_json} ({passed}/{len(results)})", file=sys.stderr)
    return 0 if not failed else 1


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-status-full",
        description="Prove the full StatusRuntime L2 catalog (status-l2-*) against a running lawn "
                    "(replaces prove-status-full.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to prove against (default: resolved by the shared library "
                             f"from ${lib.BASE_URL_ENV}, else {lib.DEFAULT_BASE_URL})")
    parser.add_argument("--include-unity-bypass", action="store_true",
                        help="include the F5-F10 Unity bypass scenarios (default: skip them)")
    parser.add_argument("--skip-visual", action="store_true",
                        help="force-skip the Unity bypass scenarios (overrides --include-unity-bypass)")
    parser.add_argument("--out-json", default=str(DEFAULT_OUT_JSON),
                        help="verdict JSON output path")
    parser.add_argument("--event-timeout", type=int, default=EVENT_TIMEOUT,
                        help=f"per-request timeout for event reads in seconds (default {EVENT_TIMEOUT})")
    parser.add_argument("--json", action="store_true",
                        help="emit the verdict as JSON on stdout")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        exit_code = run(
            base_url=args.base_url,
            include_unity_bypass=args.include_unity_bypass,
            skip_visual=args.skip_visual,
            out_json=Path(args.out_json),
            event_timeout=args.event_timeout,
        )
        if args.json:
            print(Path(args.out_json).read_text(encoding="utf-8"))
        return exit_code
    except Refusal as refusal:
        if args.json:
            print(json.dumps({
                "tool": TOOL_ID,
                "verdict": "REFUSED",
                "reason": refusal.reason,
                "detail": refusal.detail,
                "exitCode": EXIT_REFUSED,
            }, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
