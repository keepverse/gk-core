#!/usr/bin/env python3
"""LIVE prove: overlay combat + Element Hub (C1-C13) -- narrow telemetry only.
Complex damage math SSOT is offline: dotnet test ... --filter FullyQualifiedName~Combat

Requires: lawn open + lab fixtures (preferred):
  python gk-core/scripts/setup_lab_run.py
  python gk-core/scripts/prove_overlay_combat.py --target-ptr <printed ZombiePtr>

Do not invent board state here. Pass --target-ptr from setup_lab_run when possible.

Replaces `scripts/prove-overlay-combat.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** Only the health GET and the two snapshot POSTs carried
  `-TimeoutSec`; the cheats toggle, silence-vanilla, session/start, combat/probe, enqueue-delta and
  board-stats POSTs carried none, and the event waits polled on a sleep with no total budget. Every
  request now carries a timeout and every wait a deadline.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **`Get-MaxEventId` WAS COPY-PASTED THREE WAYS.** The port calls the one shared implementation,
  `lib.get_debug_max_event_id`, which adds a per-request timeout AND a total search budget.

* **`Get-Payload`'s `catch { return $null }` DISCARDED WHY A PAYLOAD WOULD NOT PARSE.** The port
  uses `lib.get_debug_payload`, which distinguishes "no payload" from "a payload that would not
  parse" and refuses by name in the second case.

* **THE SCRIPT HAD NO MACHINE-READABLE SURFACE.** The answer was a `Set-Content` file whose BOM
  depended on the PowerShell version. `--json` reports the same document that is written to disk,
  and the file is written as BOM-less UTF-8 deterministically.

DELIBERATELY UNCHANGED
----------------------
Same recipe in the same order: health gate (this file checks injectorConnected ONLY, as the
original did), OVERLAY-COMBAT toggle on, ptr resolution via combat/snapshot when either ptr is
missing, silence-vanilla + session/start, then cases C1-C13 in the original order with the original
bodies, thresholds and detail strings. Same output document shape: top-level `results` (array of
`{name, pass, detail}`), `actorPtr`, `targetPtr`, `at` -- in that key order, matching the committed
artifact. Same exit contract that `gk-core/tools/ProveAptitude/Program.cs` documents: the artifact is
always written, then exit 1 when any case failed, 0 when all passed. A refusal (the prove could not
run to completion) exits 64 and writes nothing.
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

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "prove-overlay-combat"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_TIMEOUT = 15
HEALTH_TIMEOUT = 5
SNAPSHOT_TIMEOUT_SEC = 15
SNAPSHOT_POST_TIMEOUT = 8
SNAPSHOT_POLL_SEC = 0.4
OVERLAY_WAIT_MS = 4000
OVERLAY_POLL_SEC = 0.25
FALLBACK_POLL_SEC = 0.4
ENTITY_HP_TIMEOUT_SEC = 6
ENTITY_HP_POLL_SEC = 0.3
BOARD_STATS_POST_TIMEOUT = 8
GET_OVERLAY_PAGES = 40
GET_OVERLAY_PAGE = 200
DEFAULT_OUT_JSON = ("docs", "research", "effect-runtime", "_prove-overlay-combat.json")

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "INJECTOR-NOT-CONNECTED", "NO-COMBAT-SNAPSHOT", "NO-LIVING-ZOMBIE",
    "NO-BOARD-STATS-EVENT", "PTR-NOT-IN-BOARD-STATS", "OUT-WRITE-FAILED",
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
        headers={"User-Agent": "FusionRpg-prove-overlay-combat/1.0",
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


def _fmt(value: float) -> str:
    """Render a double the way PowerShell's string interpolation does: whole numbers without a
    decimal part ("25", not "25.0"), fractions as-is ("17.5"). The committed artifact's detail
    strings were written by the PowerShell original, so the port renders numbers the same way."""
    return str(int(value)) if value == int(value) else str(value)


def get_overlay_events(base_url: str, after_id: int, timeout: int) -> list[dict]:
    """Every `debug.combat.overlay` since `after_id`, paging forward. The original's shape:
    up to 40 pages of 200, stop early on a short or empty page."""
    all_overlays: list[dict] = []
    cursor = after_id
    for _ in range(GET_OVERLAY_PAGES):
        items = lib.get_events(base_url, cursor, GET_OVERLAY_PAGE, timeout)
        if not items:
            break
        all_overlays += [i for i in items if i.get("kind") == "debug.combat.overlay"]
        cursor = int(items[-1].get("id", cursor))
        if len(items) < GET_OVERLAY_PAGE:
            break
    return all_overlays


def wait_combat_snapshot(base_url: str, timeout_sec: int = SNAPSHOT_TIMEOUT_SEC,
                         timeout: int = DEFAULT_TIMEOUT) -> dict | None:
    """Ask for a combat snapshot and wait for one to land. None when the budget expires."""
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    deadline = _MONOTONIC() + timeout_sec
    while _MONOTONIC() < deadline:
        _request(f"{base_url}/api/debug/combat/snapshot", {}, SNAPSHOT_POST_TIMEOUT,
                 "POST /api/debug/combat/snapshot")
        _SLEEP(SNAPSHOT_POLL_SEC)
        items = lib.get_events(base_url, after, 100, timeout)
        snap = [i for i in items if i.get("kind") == "debug.combat.snapshot"]
        if snap:
            payload = lib.get_debug_payload(snap[-1])
            return payload if isinstance(payload, dict) else None
        if items:
            after = int(items[-1].get("id", after))
    return None


def wait_last_overlay(base_url: str, after_id: int, timeout_ms: int = OVERLAY_WAIT_MS,
                      timeout: int = DEFAULT_TIMEOUT) -> dict | None:
    """The last overlay payload since `after_id`, polled every 250ms. On timeout, ONE fallback:
    a fresh combat snapshot's `lastOverlay`, when it carries a source."""
    deadline = _MONOTONIC() + timeout_ms / 1000.0
    while _MONOTONIC() < deadline:
        _SLEEP(OVERLAY_POLL_SEC)
        overlays = get_overlay_events(base_url, after_id, timeout)
        if overlays:
            payload = lib.get_debug_payload(overlays[-1])
            return payload if isinstance(payload, dict) else None
    snap_after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}/api/debug/combat/snapshot", {}, SNAPSHOT_POST_TIMEOUT,
             "POST /api/debug/combat/snapshot")
    _SLEEP(FALLBACK_POLL_SEC)
    items = lib.get_events(base_url, snap_after, 50, timeout)
    snap = [i for i in items if i.get("kind") == "debug.combat.snapshot"]
    if snap:
        payload = lib.get_debug_payload(snap[-1])
        if isinstance(payload, dict):
            last = payload.get("lastOverlay")
            if isinstance(last, dict) and last.get("source"):
                return last
    return None


def get_entity_hp(base_url: str, ptr: str, timeout: int) -> float:
    """A ptr's current HP via debug.board-stats. The original's shape: POST board-stats, then poll
    up to 6s for the event and read the ptr's hp out of plants+zombies."""
    _request(f"{base_url}/api/debug/board-stats", {}, BOARD_STATS_POST_TIMEOUT,
             "POST /api/debug/board-stats")
    before = lib.get_debug_max_event_id(base_url, timeout=timeout)
    deadline = _MONOTONIC() + ENTITY_HP_TIMEOUT_SEC
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, before, 50, timeout)
        stats = [i for i in items if i.get("kind") == "debug.board-stats"]
        if stats:
            payload = lib.get_debug_payload(stats[-1])
            entry = None
            if isinstance(payload, dict):
                for side in ("plants", "zombies"):
                    for candidate in payload.get(side) or []:
                        if isinstance(candidate, dict) and candidate.get("ptr") == ptr:
                            entry = candidate
                            break
                    if entry is not None:
                        break
            if entry is not None:
                return float(entry.get("hp") or 0)
            raise Refusal("PTR-NOT-IN-BOARD-STATS", f"ptr {ptr} not found in debug.board-stats")
        if items:
            before = int(items[-1].get("id", before))
        _SLEEP(ENTITY_HP_POLL_SEC)
    raise Refusal("NO-BOARD-STATS-EVENT", "no debug.board-stats event")


def run_cases(base_url: str, target_ptr: str, actor_ptr: str, timeout: int) -> list[dict]:
    """Cases C1-C13 in the original order. A case that throws is a FAIL with the message as its
    detail -- the original's `Run-Case` caught everything, so a transport failure inside a case
    fails THAT case, not the whole prove."""
    results: list[dict] = []

    def run_case(name: str, body) -> None:
        ok = False
        detail = ""
        try:
            detail = body()
            ok = True
        except Exception as error:
            detail = str(error)
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}", file=sys.stderr)
        results.append({"name": name, "pass": ok, "detail": detail})

    def assert_matchup(name: str, pin_el: str, payload: list[dict], expected_bonus: float) -> None:
        def body() -> str:
            before = lib.get_debug_max_event_id(base_url, timeout=timeout)
            _request(f"{base_url}/api/debug/combat/probe",
                     {"amount": -100, "targetPtr": target_ptr, "seed": 1, "forceHit": True,
                      "forceCrit": False, "pinTargetElement": pin_el, "elementPayload": payload},
                     timeout, "POST /api/debug/combat/probe")
            p = wait_last_overlay(base_url, before, timeout=timeout)
            if not p:
                raise ValueError("no debug.combat.overlay")
            bonus = float(p.get("matchupBonus") or 0)
            if abs(bonus - expected_bonus) > 0.5:
                raise ValueError(
                    f"matchupBonus={_fmt(bonus)} expected ~{_fmt(float(expected_bonus))}")
            return f"matchupBonus={_fmt(bonus)}"
        run_case(name, body)

    fire = [{"element": "fire", "weight": 1.0}]
    ice = [{"element": "ice", "weight": 1.0}]
    air = [{"element": "air", "weight": 1.0}]
    earth = [{"element": "earth", "weight": 1.0}]

    assert_matchup("C1 overlay-fire-vs-ice", "ice", fire, 25)
    assert_matchup("C2 overlay-fire-vs-air", "air", fire, -25)
    assert_matchup("C3 overlay-hybrid-vs-ice", "ice",
                   [{"element": "fire", "weight": 0.7}, {"element": "air", "weight": 0.3}], 17.5)

    def c4() -> str:
        before = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}/api/debug/combat/probe",
                 {"amount": -100, "targetPtr": target_ptr, "seed": 1, "forceMiss": True,
                  "pinTargetElement": "ice", "elementPayload": fire},
                 timeout, "POST /api/debug/combat/probe")
        p = wait_last_overlay(base_url, before, timeout=timeout)
        if not p:
            raise ValueError("no debug.combat.overlay")
        if p.get("hit") is not False:
            raise ValueError(f"hit={p.get('hit')} expected false")
        if int(p.get("finalSignedDelta") or 0) != 0:
            raise ValueError(f"finalSignedDelta={p.get('finalSignedDelta')} expected 0")
        return "hit=false finalSignedDelta=0"
    run_case("C4 overlay-miss", c4)

    def c5() -> str:
        before = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}/api/debug/combat/probe",
                 {"amount": 50, "targetPtr": target_ptr, "seed": 1, "elementPayload": fire},
                 timeout, "POST /api/debug/combat/probe")
        _SLEEP(0.6)
        overlays = get_overlay_events(base_url, before, timeout)
        if overlays:
            raise ValueError("unexpected debug.combat.overlay on heal")
        return "no overlay breakdown; heal pass-through"
    run_case("C5 overlay-heal", c5)

    def c6() -> str:
        _request(f"{base_url}/api/cheats/toggle", {"id": "OVERLAY-COMBAT", "enabled": False},
                 timeout, "POST /api/cheats/toggle")
        _SLEEP(0.3)
        before = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}/api/debug/combat/probe",
                 {"amount": -100, "targetPtr": target_ptr, "seed": 1, "forceHit": True,
                  "pinTargetElement": "ice", "elementPayload": fire},
                 timeout, "POST /api/debug/combat/probe")
        _SLEEP(0.6)
        overlays = get_overlay_events(base_url, before, timeout)
        if overlays:
            raise ValueError("unexpected debug.combat.overlay when flag off")
        _request(f"{base_url}/api/cheats/toggle", {"id": "OVERLAY-COMBAT", "enabled": True},
                 timeout, "POST /api/cheats/toggle")
        return "pass-through -100; no overlay emit"
    run_case("C6 overlay-flag-off", c6)

    assert_matchup("C7 overlay-ice-vs-fire", "fire", ice, -25)
    assert_matchup("C8 overlay-air-vs-earth", "earth", air, -25)
    assert_matchup("C9 overlay-earth-vs-air", "air", earth, 25)

    def pin_actor_channels(ptr: str, channels: dict) -> None:
        _request(f"{base_url}/api/debug/combat/probe",
                 {"targetPtr": ptr, "actorPtr": ptr, "amount": 0,
                  "pinActorChannels": channels},
                 timeout, "POST /api/debug/combat/probe")

    def c10() -> str:
        if not actor_ptr:
            raise ValueError("need living plant ActorPtr for crit channels (lab-overlay)")
        before = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}/api/debug/combat/probe",
                 {"amount": -100, "targetPtr": target_ptr, "actorPtr": actor_ptr, "seed": 1,
                  "forceHit": True, "forceCrit": True, "pinTargetElement": "ice",
                  "pinActorChannels": {"combat.accuracy.omni": 500,
                                       "combat.crit.damage.omni": 500,
                                       "combat.crit.rate.omni": 500},
                  "elementPayload": fire},
                 timeout, "POST /api/debug/combat/probe")
        p = wait_last_overlay(base_url, before, timeout=timeout)
        if not p:
            raise ValueError("no debug.combat.overlay")
        if p.get("crit") is not True:
            raise ValueError(f"crit={p.get('crit')} expected true")
        mult = float(p.get("critMultiplierFinal") or 0)
        if mult <= 1.0:
            raise ValueError(f"critMultiplierFinal={mult} expected > 1")
        return f"crit=true critMultiplierFinal={mult}"
    run_case("C10 overlay-force-crit", c10)

    def c11() -> str:
        if not target_ptr:
            raise ValueError("need TargetPtr")
        pin_actor_channels(target_ptr, {"resource.restore.hp": 40})
        before = get_entity_hp(base_url, target_ptr, timeout)
        _request(f"{base_url}/api/debug/effect/enqueue-delta",
                 {"targetPtr": target_ptr, "amount": 10,
                  "target": {"mode": "single", "ptr": target_ptr}, "elementPayload": fire},
                 timeout, "POST /api/debug/effect/enqueue-delta")
        _SLEEP(0.4)
        after = get_entity_hp(base_url, target_ptr, timeout)
        healed = after - before
        # effectiveHeal = max(0, signedAmount + healPower) = max(0, 10 + 40) = 50 -- FinalizeHeal's
        # own formula (OverlayCombatMath.cs). Capped at maxHp, which setup_lab_run's fixtures leave
        # headroom under -- if this ever fails on a full-HP target, heal a smaller amount first
        # via debug tooling.
        if abs(healed - 50) > 0.5:
            raise ValueError(f"healed={_fmt(healed)} expected ~50 (10 base + 40 heal.power)")
        return f"healed={_fmt(healed)} (expected ~50)"
    run_case("C11 overlay-heal-with-payload-scales-with-heal-power", c11)

    def c12() -> str:
        # spec: `if (signedAmount > 0) return FinalizeHeal(...)` runs BEFORE the payload-null check
        # (OverlayCombatMath.Finalize) -- so a heal WITHOUT any elementPayload must still scale by
        # resource.restore.hp, not silently fall through to "amount unchanged" the way a
        # PAYLOAD-LESS DAMAGE packet would (Finalize's own
        # `if (packet.ElementPayload == null) return signedAmount`, which only applies below the
        # heal branch, never reached for signedAmount > 0).
        if not target_ptr:
            raise ValueError("need TargetPtr")
        pin_actor_channels(target_ptr, {"resource.restore.hp": 40})
        before = get_entity_hp(base_url, target_ptr, timeout)
        _request(f"{base_url}/api/debug/effect/enqueue-delta",
                 {"targetPtr": target_ptr, "amount": 10,
                  "target": {"mode": "single", "ptr": target_ptr}},
                 timeout, "POST /api/debug/effect/enqueue-delta")
        _SLEEP(0.4)
        after = get_entity_hp(base_url, target_ptr, timeout)
        healed = after - before
        if abs(healed - 50) > 0.5:
            raise ValueError("healed="
                             f"{_fmt(healed)} expected ~50 -- a value of exactly 10 here would "
                             "mean the no-payload heal fell through to raw signedAmount instead "
                             "of FinalizeHeal")
        return f"healed={_fmt(healed)} (expected ~50, proving FinalizeHeal ran despite no payload)"
    run_case("C12 overlay-heal-with-no-payload-still-reads-heal-power", c12)

    def c13() -> str:
        # owner decision 6: the overlay profile's MinChipShareKPm is 0 (CombatProfiles.Overlay =
        # new(0)), unlike every other profile's 50 per-mille floor -- a fully-mitigated overlay hit
        # must resolve to EXACTLY 0, not a guaranteed minimum chip. Sky-high combat.defense.omni on
        # the defender drives DivisiveMitigation's powerAdjusted to 0 (Math.Max(0, powerAdjusted)
        # floors it), and 0 times any crit/amp multiplier is still 0.
        if not target_ptr:
            raise ValueError("need TargetPtr")
        pin_actor_channels(target_ptr, {"combat.defense.omni": 999999999})
        before = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}/api/debug/combat/probe",
                 {"amount": -100, "targetPtr": target_ptr, "seed": 1, "forceHit": True,
                  "elementPayload": fire},
                 timeout, "POST /api/debug/combat/probe")
        p = wait_last_overlay(base_url, before, timeout=timeout)
        if not p:
            raise ValueError("no debug.combat.overlay")
        if int(p.get("finalSignedDelta") or 0) != 0:
            raise ValueError("finalSignedDelta="
                             f"{p.get('finalSignedDelta')} expected exactly 0 -- a nonzero value "
                             "here would mean a chip floor leaked into the overlay profile")
        return "finalSignedDelta=0, no exception -- the game handled full mitigation cleanly"
    run_case("C13 overlay-full-mitigation-resolves-to-zero-no-chip-floor", c13)

    return results


def run_prove(base_url: str, target_ptr: str, actor_ptr: str, out_json: str,
              timeout: int) -> dict:
    """The whole recipe. Writes the artifact and returns {"results", "exitCode"}; raises Refusal
    when the prove cannot run to completion."""
    print("Health...", file=sys.stderr)
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      "injector not connected — start game + FusionRpg injector first")

    print("Enabling OVERLAY-COMBAT...", file=sys.stderr)
    _request(f"{base_url}/api/cheats/toggle", {"id": "OVERLAY-COMBAT", "enabled": True}, timeout,
             "POST /api/cheats/toggle")

    if not target_ptr or not actor_ptr:
        print("Resolving board via combat/snapshot (prefer setup-lab-run.py)...", file=sys.stderr)
        snap = wait_combat_snapshot(base_url, timeout=timeout)
        if snap is None:
            raise Refusal("NO-COMBAT-SNAPSHOT",
                          "no combat snapshot — run scripts/setup_lab_run.py first")
        if not target_ptr:
            zombies = [e for e in snap.get("entities") or []
                       if isinstance(e, dict) and e.get("side") == "zombie" and e.get("living")]
            if not zombies:
                raise Refusal("NO-LIVING-ZOMBIE",
                              "No living zombie — run scripts/setup_lab_run.py then pass "
                              "--target-ptr <ZombiePtr>")
            target_ptr = str(zombies[0].get("ptr") or "")
        if not actor_ptr:
            plants = [e for e in snap.get("entities") or []
                      if isinstance(e, dict) and e.get("side") == "plant" and e.get("living")]
            if plants:
                actor_ptr = str(plants[0].get("ptr") or "")
    print(f"Target ptr: {target_ptr}", file=sys.stderr)
    if actor_ptr:
        print(f"Actor ptr: {actor_ptr}", file=sys.stderr)

    print("Silence vanilla plant ATK...", file=sys.stderr)
    _request(f"{base_url}/api/debug/combat/silence-vanilla", {"plant": True}, timeout,
             "POST /api/debug/combat/silence-vanilla")
    _request(f"{base_url}/api/debug/session/start", {}, timeout, "POST /api/debug/session/start")

    results = run_cases(base_url, target_ptr, actor_ptr, timeout)

    payload = {"results": results, "actorPtr": actor_ptr, "targetPtr": target_ptr,
               "at": datetime.now().astimezone().isoformat()}
    try:
        with open(out_json, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, indent=2)
            handle.write("\n")
    except OSError as error:
        raise Refusal("OUT-WRITE-FAILED", f"could not write {out_json}: {error}") from error
    print(f"Wrote {out_json}", file=sys.stderr)

    fail = sum(1 for r in results if not r["pass"])
    return {"results": results, "exitCode": EXIT_FAILED if fail else EXIT_OK}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-overlay-combat",
        description="LIVE prove: overlay combat + Element Hub (C1-C13) -- narrow telemetry only "
                    "(replaces prove-overlay-combat.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--target-ptr", default="",
                        help="zombie ptr to hit (from setup_lab_run's ZombiePtr)")
    parser.add_argument("--actor-ptr", default="",
                        help="plant ptr to crit from (optional; resolved from the board when empty)")
    parser.add_argument("--out-json", default=str(Path(*DEFAULT_OUT_JSON)),
                        help=f"artifact path (default: {str(Path(*DEFAULT_OUT_JSON))})")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    try:
        evidence = run_prove(base_url, args.target_ptr, args.actor_ptr, args.out_json,
                             args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    if args.json:
        # report exactly the document that was written to disk
        with open(args.out_json, "r", encoding="utf-8") as handle:
            written = json.load(handle)
        print(json.dumps(written, indent=2))
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
