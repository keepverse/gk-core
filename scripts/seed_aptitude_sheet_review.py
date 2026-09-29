#!/usr/bin/env python3
"""Seed UniqueCreature + commander allocation + one Even preset for aptitude-sheet UI review.

Uses existing debug/product APIs (no new endpoints):
  POST /api/debug/derived-audit-actor   -- UniqueActor "derived-audit" @ L80 + broad aptitudes
  GET  /api/players                     -- current playerId
  POST /api/aptitude-presets/           -- Even per-mille=1000 library row
  GET  /api/aptitudes/unique/{id}       -- print budget/spent for smoke

Open after seed (hard-refresh):
  Mode A  http://127.0.0.1:5088/#/actor-ladder-demo?sel=derived-audit
          -> Open panel -> Aptitudes -> Build presets...
  Mode C  Sanctum -> Commanders -> open sheet -> Aptitudes
  Mode B  Sanctum -> Pacts -> View build (species)

Replaces `scripts/seed-aptitude-sheet-review.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** `Invoke-RestMethod` was called six times with no `-TimeoutSec`
  anywhere in the file. A wedged or half-started server holds the seeder open indefinitely, with no
  output and no way to tell it from a slow one. Every request now carries a timeout.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. This machine has a three-slot pool on 5101/5102/5103, so a seeder pointed at the
  default writes its review preset into somebody else's server and reports success. The URL is read
  ONCE through `lib.resolve_base_url` -- explicit argument, then `FUSIONRPG_SERVER_URL`, then the
  built-in default -- and the resolved value is printed so a run says which server it seeded.

* **`$ErrorActionPreference = "Stop"` MADE EVERY FAILURE A BARE EXIT 1.** A refused POST and a
  malformed JSON body both surfaced as a PowerShell error with no word about which endpoint or which
  stage. Each failure is now a named refusal that names the endpoint.

* **NO MACHINE-READABLE VERDICT OF ITS OWN.** The answer was six `Write-Host` lines on the
  INFORMATION stream, invisible to a `2>&1` capture. `--json` here reports every response the seeder
  read, in order, with the resolved base URL and where it came from.

DELIBERATELY UNCHANGED
----------------------
Same six requests in the same order, same request bodies, same preset shape (name "Review Even", kind
"player", twelve rows at per-mille 83 with the first four at 84), same default instanceId fallback
"derived-audit", and the same operator output -- the review URLs and the DevTools observation ring
are the deliverable, and an operator who knows the PowerShell output reads this one unchanged.
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "seed-aptitude-sheet-review"

EXIT_OK = 0
EXIT_REFUSED = 64

# A request against this server is one HTTP hop behind a local process. The bound is generous enough
# for a cold start and short enough that a wedged server is a refusal rather than a hang.
DEFAULT_TIMEOUT = 15

HEALTH_PATH = "/health"
PLAYERS_PATH = "/api/players"
DERIVED_AUDIT_ACTOR_PATH = "/api/debug/derived-audit-actor"
APTITUDE_PRESETS_PATH = "/api/aptitudes/presets"

# The twelve aptitudes the review sheet shows, in the original's order. The first four are funded one
# per-mille higher (84) than the rest (83) -- the original's `83 + $(if ($i -lt 4) { 1 } else { 0 })`.
APTITUDE_IDS = ("Might", "Fortitude", "Vigor", "Onslaught",
                "Agility", "Composure", "Pierce", "Focus",
                "Bulwark", "Retribution", "Precision", "Ferocity")
FIRST_FOUR_PERMILLE = 84
REST_PERMILLE = 83

DEFAULT_PRESET_NAME = "Review Even"
DEFAULT_PRESET_KIND = "player"
DEFAULT_INSTANCE_ID = "derived-audit"

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "REQUEST-FAILED", "RESPONSE-NOT-JSON", "SEED-FAILED", "PRESET-FAILED",
    "BUDGET-READ-FAILED",
}

# THE ONLY SEAM THE SUITE NEEDS, bound once to a module-private name. `urllib.request` is the
# process-wide module, so `mock.patch.object(urllib.request, "urlopen", ...)` is a global patch
# wearing a local name: it reaches every other test in this project, and a patch that outlives its
# `with` block breaks them while this suite reports green. That is not hypothetical -- the sibling
# suite `test_dump_melon_p0.py` shipped once with the same mistake and made 506 unrelated failures in
# `test_ps1_port_census.py`. Binding it here makes the leak impossible by construction.
_URLOPEN = urllib.request.urlopen


class Refusal(Exception):
    """A named precondition or transport failure. Never exits 0 having not asked."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


class _Response:
    """What `_urlopen` must return: a context manager yielding an object with `read()`."""

    def __init__(self, payload: dict | None, raw: bytes | None = None) -> None:
        self._payload = payload
        self._raw = raw

    def __enter__(self) -> "_Response":
        return self

    def __exit__(self, *exc: object) -> bool:
        return False

    def read(self) -> bytes:
        if self._raw is not None:
            return self._raw
        return json.dumps(self._payload if self._payload is not None else {}).encode("utf-8")


def _request(url: str, body: dict | None, timeout: int, what: str) -> dict:
    """One bounded request. `body` of None is a GET; otherwise a POST with that JSON body."""
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        url, data=data, method="GET" if body is None else "POST",
        headers={"User-Agent": "FusionRpg-seed-aptitude-sheet-review/1.0",
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


def aptitude_rows() -> list[dict]:
    """The twelve review rows: the first four at per-mille 84, the rest at 83."""
    return [{"aptitudeId": aptitude_id,
             "targetPermille": FIRST_FOUR_PERMILLE if i < 4 else REST_PERMILLE}
            for i, aptitude_id in enumerate(APTITUDE_IDS)]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="seed-aptitude-sheet-review",
        description="Seed UniqueCreature + commander allocation + one Even preset for aptitude-sheet "
                    "UI review (replaces seed-aptitude-sheet-review.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    timeout = args.timeout
    responses: list[dict] = []

    def call(method: str, path: str, body: dict | None, what: str) -> dict:
        url = f"{base_url}{path}"
        payload = _request(url, body, timeout, what)
        responses.append({"method": method, "path": path, "what": what, "response": payload})
        return payload

    try:
        print("=== aptitude-sheet review seed ===", file=sys.stderr)
        print(f"BaseUrl: {base_url} (from {source})", file=sys.stderr)

        health = call("GET", HEALTH_PATH, None, "GET /health")
        print(f"health ok={health.get('ok')} playerId={health.get('currentPlayerId')} "
              f"injector={health.get('injectorConnected')}", file=sys.stderr)

        seed = call("POST", DERIVED_AUDIT_ACTOR_PATH, {"playerId": health.get("currentPlayerId")},
                   "POST /api/debug/derived-audit-actor")
        instance_id = seed.get("instanceId") or DEFAULT_INSTANCE_ID
        print(f"seeded UniqueActor instanceId={instance_id}", file=sys.stderr)

        players = call("GET", PLAYERS_PATH, None, "GET /api/players")
        player_id = players.get("currentPlayerId")
        print(f"currentPlayerId={player_id}", file=sys.stderr)

        preset = call("POST", APTITUDE_PRESETS_PATH,
                      {"playerId": player_id, "name": DEFAULT_PRESET_NAME,
                       "kind": DEFAULT_PRESET_KIND, "rows": aptitude_rows()},
                      "POST /api/aptitudes/presets")
        print(f"preset saved presetId={preset.get('presetId')} name={preset.get('name')}",
              file=sys.stderr)

        unique = call("GET", f"/api/aptitudes/unique/{instance_id}", None,
                      f"GET /api/aptitudes/unique/{instance_id}")
        print(f"unique budget={unique.get('budget')} spent={unique.get('spent')} "
              f"leftover={unique.get('leftover')}", file=sys.stderr)

        commander = call("GET", f"/api/aptitudes/{player_id}", None,
                         f"GET /api/aptitudes/{player_id}")
        print(f"commander budget={commander.get('budget')} spent={commander.get('spent')}",
              file=sys.stderr)

        print("", file=sys.stderr)
        print("Review URLs (hard-refresh after FE deploy):", file=sys.stderr)
        print(f"  Mode A  {base_url}/#/actor-ladder-demo?sel={instance_id}", file=sys.stderr)
        print("          Open panel \u2192 Aptitudes \u2192 Build presets\u2026", file=sys.stderr)
        print(f"  Mode C  {base_url}/  \u2192 Sanctum \u2192 Commanders \u2192 sheet \u2192 Aptitudes",
              file=sys.stderr)
        print(f"  Mode B  {base_url}/  \u2192 Sanctum \u2192 Pacts \u2192 View build", file=sys.stderr)
        print("", file=sys.stderr)
        print("Obs ring in DevTools:", file=sys.stderr)
        print("  window.__fusionRpgAptitudeObs", file=sys.stderr)
        print("", file=sys.stderr)
        print("FE rebuild into wwwroot if UI looks stale:", file=sys.stderr)
        print("  cd web\\fusion-rpg-web; npm run build", file=sys.stderr)
        print("  # or: python scripts\\deploy-play.py --no-server --no-game", file=sys.stderr)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, responses, base_url, source)

    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                "instanceId": instance_id, "playerId": player_id,
                "presetId": preset.get("presetId"), "verdict": "OK", "responses": responses}
    if args.json:
        print(json.dumps(envelope, indent=2))
    return EXIT_OK


def _refuse(reason: str, detail: str, as_json: bool, responses: list[dict] | None = None,
            base_url: str = "", source: str = "") -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": EXIT_REFUSED, "baseUrl": base_url, "baseUrlSource": source,
                   "responses": responses or []}
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
