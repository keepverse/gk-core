#!/usr/bin/env python3
"""Server-side shield probe — no game required (combat-unification, sim-adoption U16).

Spawns a sim plant, grants it a shield, deals more damage than the shield holds, and then CHECKS the
outcome. Spawns, grants, damages, reads state.

Replaces `scripts/probe-sim-shield.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **THE PROBE COULD NOT FAIL.** Its own comment says "expect shieldAbsorbed 50, hp 300 -> 270" and
  nothing anywhere checks that. Both event pipelines were
  `(...).events | Where-Object kind -eq "shield.granted" | Select-Object -Expand payload | Format-List` —
  so when the event was absent the pipeline produced **no output at all** and the script carried on to
  the next step and exited 0. A probe whose entire purpose is to demonstrate that a shield absorbs what
  it should, and which reports success when the shield did not absorb anything, is worse than no probe:
  it is a green light on the bug it exists to catch.

* **NO TIMEOUT ON ANY OF THE FOUR REQUESTS.** `Invoke-RestMethod` with no `-TimeoutSec` waits on the
  server's own idea of forever. A wedged or half-started server holds the probe open indefinitely.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` — `5088` is the
  OWNER's server. This repository's rule is that a port is configuration: an agent pointing this at a
  pooled slot on `5101`/`5102`/`5103` had no way to say so without editing the file, and a probe aimed
  at the wrong server measures the wrong one while reporting the result as if it were the subject.

* **`shieldAbsorbed` IS ABSENT WHEN NOTHING WAS ABSORBED.** `SimEngine` adds the key only
  `if (applied.AbsorbedAmount > 0)`, so "absorbed zero" and "the pipeline did not run" are the same
  JSON. The port therefore distinguishes them by requiring the EVENT to be present and the key to be
  present inside it, and says which of the two it found.

WHAT THIS TOOL MUST NEVER DO
----------------------------
Exit 0 having checked nothing. Every expectation is named, reported pass or fail, and counted; a single
failure is a non-zero exit naming the expectation that failed.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass, field

TOOL_ID = "probe-sim-shield"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

# A sim POST is in-process work behind one HTTP hop. The bound is generous enough for a cold start and
# short enough that a wedged server is a refusal rather than a hang.
DEFAULT_TIMEOUT = 15

# The owner's server. A DEFAULT, not a fact: a pooled slot runs its own server on its own port, and a
# probe pointed at `5088` while probing a slot measures the wrong server -- or nothing.
FALLBACK_BASE_URL = "http://127.0.0.1:5088"

PTR = "P1"
BOARD_LEVEL = "shield-probe"
PLANT_HP = 300
SHIELD_AMOUNT = 50
DAMAGE = 80

# What the probe asserts. Named, not incidental: the original stated this in a comment and checked
# nothing, so the contract now lives in code and each line is reported by name.
EXPECTED_HP_AFTER = PLANT_HP - (DAMAGE - SHIELD_AMOUNT)

BOARD_START = "/api/sim/board/start"
PLANT_SPAWN = "/api/sim/plant/spawn"
SHIELD_GRANT = "/api/sim/shield/grant"
PLANT_DAMAGE = "/api/sim/plant/damage"
SIM_STATE = "/api/sim/state"

REFUSAL_REASONS = {
    "BASE-URL-UNSET", "INVALID-TIMEOUT", "REQUEST-FAILED", "RESPONSE-NOT-JSON",
    "RESPONSE-NOT-OBJECT", "SERVER-UNREACHABLE",
}

# THE ONLY SEAM THE SUITE NEEDS, bound once to a module-private name. `urllib.request` is the
# process-wide module, so `mock.patch.object(pss.urllib.request, "urlopen", ...)` is a global patch
# wearing a local name: it reaches every other test in this project, and a patch that outlives its
# `with` block breaks them while this suite reports green. That is not hypothetical -- the sibling suite
# `test_dump_melon_p0.py` shipped once with the same mistake and made 506 unrelated failures in
# `test_ps1_port_census.py`. Binding it here makes the leak impossible by construction.
_urlopen = urllib.request.urlopen


class Refusal(Exception):
    """A named precondition or transport failure. Never exits 0 having not asked."""

    def __init__(self, reason: str, detail: str, exit_code: int = EXIT_REFUSED) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


@dataclass
class Expectation:
    """One named claim about the run, and whether the run supports it."""

    name: str
    ok: bool
    expected: str
    actual: str

    def as_dict(self) -> dict:
        return {"name": self.name, "ok": self.ok, "expected": self.expected, "actual": self.actual}


@dataclass
class Report:
    base_url: str = ""
    checks: list[Expectation] = field(default_factory=list)
    shield_event: dict | None = None
    damage_event: dict | None = None
    state: dict | None = None
    refused: tuple[str, str] | None = None

    @property
    def ok(self) -> bool:
        return self.refused is None and bool(self.checks) and all(c.ok for c in self.checks)

    @property
    def failed(self) -> list[Expectation]:
        return [c for c in self.checks if not c.ok]


def resolve_base_url(explicit: str) -> str:
    """Read the base URL ONCE, explicitly, and fail loudly when there is nothing to read.

    Order: the flag, then `FUSIONRPG_SIM_PROBE_URL` for this tool, then `FUSIONRPG_URLS` (what the
    SERVER itself reads, so a slot started with it points here correctly by default), then the owner's
    documented default. `FUSIONRPG_URLS` is consulted because a mismatch between what a server bound and
    what a probe dialled is the exact failure this repository's port rule exists to prevent.
    """
    for candidate in (explicit, os.environ.get("FUSIONRPG_SIM_PROBE_URL", ""),
                      os.environ.get("FUSIONRPG_URLS", "")):
        if candidate and candidate.strip():
            return candidate.strip().rstrip("/")
    if not FALLBACK_BASE_URL:
        raise Refusal("BASE-URL-UNSET",
                      "Pass --base-url, or set FUSIONRPG_SIM_PROBE_URL / FUSIONRPG_URLS")
    return FALLBACK_BASE_URL.rstrip("/")


def _request(url: str, body: dict | None, timeout: int) -> dict:
    """One bounded HTTP call. `urllib` rather than `requests`: a probe should not need a third-party
    package present on a machine that is already running the game and the server."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with _urlopen(request, timeout=timeout) as response:  # noqa: S310 - the URL comes from
            raw = response.read()                           # configuration and is printed in the report
    except urllib.error.HTTPError as error:
        raise Refusal("REQUEST-FAILED", f"{url} -> HTTP {error.code}") from error
    except (urllib.error.URLError, OSError) as error:
        # The common case: the server is not running, or not on the port we were told.
        raise Refusal("SERVER-UNREACHABLE", f"{url} -> {error}") from error
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("RESPONSE-NOT-JSON", f"{url} -> {error}") from error
    if not isinstance(payload, dict):
        raise Refusal("RESPONSE-NOT-OBJECT", f"{url} -> {type(payload).__name__}")
    return payload


def events_of(payload: dict, kind: str) -> list[dict]:
    """Every event of one kind, tolerant of a missing or differently-shaped `events` key.

    `SimResult.Events` is a list; a defensive read here means a server that answers `{"ok":true}` with
    no events produces a REPORTED absence rather than a `KeyError` traceback.
    """
    events = payload.get("events")
    if not isinstance(events, list):
        return []
    return [e for e in events if isinstance(e, dict) and e.get("kind") == kind]


def check(name: str, ok: bool, expected: str, actual: str) -> Expectation:
    return Expectation(name=name, ok=bool(ok), expected=str(expected), actual=str(actual))


def execute(base_url: str, timeout: int) -> Report:
    """Drive the four writes and the one read, then assert. Never returns without a verdict."""
    report = Report(base_url=base_url)

    _request(f"{base_url}{BOARD_START}", {"levelName": BOARD_LEVEL}, timeout)
    _request(f"{base_url}{PLANT_SPAWN}",
             {"ptr": PTR, "row": 2, "col": 3, "hp": PLANT_HP, "maxHp": PLANT_HP}, timeout)

    granted = events_of(_request(f"{base_url}{SHIELD_GRANT}",
                                 {"ptr": PTR, "amount": SHIELD_AMOUNT}, timeout), "shield.granted")
    report.shield_event = granted[0] if granted else None
    report.checks.append(check(
        "the-shield-grant-emitted-a-shield.granted-event", bool(granted),
        "1 shield.granted event", f"{len(granted)} event(s)"))
    if granted:
        payload = granted[0].get("payload") or {}
        # `SimEngine.GrantShield` reports the shield as `hp`/`maxHp` totals, not as an `amount` key.
        shield_hp = payload.get("hp")
        report.checks.append(check(
            "the-granted-shield-holds-the-amount-requested", shield_hp == SHIELD_AMOUNT,
            f"hp == {SHIELD_AMOUNT}", f"hp == {shield_hp!r}"))

    damaged = events_of(_request(f"{base_url}{PLANT_DAMAGE}",
                                 {"ptr": PTR, "damage": DAMAGE}, timeout), "plant.damage")
    report.damage_event = damaged[0] if damaged else None
    report.checks.append(check(
        "the-damage-emitted-a-plant.damage-event", bool(damaged),
        "1 plant.damage event", f"{len(damaged)} event(s)"))
    if damaged:
        payload = damaged[0].get("payload") or {}
        # The key is added ONLY when `AbsorbedAmount > 0`, so an absent key is its own finding and is
        # reported as such rather than read as zero.
        report.checks.append(check(
            "the-shield-absorbed-the-whole-amount", payload.get("shieldAbsorbed") == SHIELD_AMOUNT,
            f"shieldAbsorbed == {SHIELD_AMOUNT}", f"shieldAbsorbed == {payload.get('shieldAbsorbed')!r}"))

    state = _request(f"{base_url}{SIM_STATE}", None, timeout)
    report.state = state
    plants = state.get("plants")
    rows = [p for p in plants if isinstance(p, dict) and p.get("ptr") == PTR] \
        if isinstance(plants, list) else []
    report.checks.append(check(
        "the-state-reports-the-probed-plant", bool(rows),
        f"one plant with ptr == {PTR}", f"{len(rows)} row(s)"))
    if rows:
        hp = rows[0].get("hp")
        report.checks.append(check(
            "the-plant-survived-at-the-predictable-hp", hp == EXPECTED_HP_AFTER,
            f"hp == {EXPECTED_HP_AFTER} ({PLANT_HP} - ({DAMAGE} - {SHIELD_AMOUNT}))",
            f"hp == {hp!r}"))

    shields = state.get("shields")
    report.checks.append(check(
        "the-state-carries-a-shield-table", isinstance(shields, (list, dict)) and bool(shields),
        "a non-empty shields table", f"{type(shields).__name__} "
                                     f"{'empty' if not shields else 'present'}"))
    return report


def render(report: Report, as_json: bool) -> None:
    if as_json:
        print(json.dumps({"tool": TOOL_ID,
                          "verdict": "OK" if report.ok else "FAILED",
                          "exitCode": EXIT_OK if report.ok else EXIT_FAILED,
                          "baseUrl": report.base_url,
                          "checks": [c.as_dict() for c in report.checks],
                          "failedChecks": [c.name for c in report.failed],
                          "shieldEvent": report.shield_event,
                          "damageEvent": report.damage_event,
                          "state": report.state}, indent=2))
        return
    print(f"probe-sim-shield against {report.base_url}")
    print(f"== grant {SHIELD_AMOUNT} shield to {PTR} ==")
    print(f"== deal {DAMAGE} damage (expect shieldAbsorbed {SHIELD_AMOUNT}, "
          f"hp {PLANT_HP} -> {EXPECTED_HP_AFTER}) ==")
    print("== checks ==")
    for c in report.checks:
        print(f"  [{'ok' if c.ok else 'FAIL'}] {c.name}")
        if not c.ok:
            print(f"         expected {c.expected}; got {c.actual}")
    passed = len(report.checks) - len(report.failed)
    print(f"== {passed}/{len(report.checks)} check(s) passed ==")
    if report.failed:
        print("FAILED: " + ", ".join(c.name for c in report.failed))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="probe-sim-shield",
        description="Server-side shield probe; no game required (replaces probe-sim-shield.ps1).")
    parser.add_argument("--base-url", default=os.environ.get("FUSIONRPG_SIM_PROBE_URL", ""),
                        help=f"sim server root (default: $FUSIONRPG_SIM_PROBE_URL, else "
                             f"$FUSIONRPG_URLS, else {FALLBACK_BASE_URL})")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds for EACH request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    if args.timeout <= 0:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": "INVALID-TIMEOUT",
                   "detail": "--timeout must be positive", "exitCode": EXIT_REFUSED}
        if args.json:
            print(json.dumps(payload, indent=2))
        else:
            print("[probe-sim-shield] REFUSED: INVALID-TIMEOUT: --timeout must be positive",
                  file=sys.stderr)
        return EXIT_REFUSED

    report = None
    try:
        report = execute(resolve_base_url(args.base_url), args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": refusal.exit_code}, indent=2))
        else:
            print(f"[probe-sim-shield] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    render(report, args.json)
    return EXIT_OK if report.ok else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
