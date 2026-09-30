#!/usr/bin/env python3
"""Tight shield-bar HUD probe -- world VFX bars, not IMGUI.

Preferred: python -m live_test run shield.bar  (from gk-fusion/tools/live_test)
See docs/runbook/live-test-ssot.md

Prerequisites: Melon/Bep injector connected + Adventure lawn live.
Optional: already ran setup-shield-bar-lab.ps1 (or pass -Setup).

Pass criteria:
  dataOwners > 0
  shaderOk = true
  worldBars == dataOwners (and lastDraw.early = ok)
  fillRatio > 0

Replaces `scripts/probe-live-shield-bar.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **EVERY REQUEST WAS UNBOUNDED.** `Invoke-RestMethod` was called in the health gate, the demo-all
  POST, the bar-status POST and every `Wait-Kind` page -- none with `-TimeoutSec`. A wedged server
  holds the probe open indefinitely. Every request now carries a timeout, and every wait carries a
  deadline.

* **THE EVENT WAITS COULD WAIT FOREVER.** `Wait-Kind` polled `/api/events` on a 200ms sleep with no
  total budget -- a kind that never arrives meant an infinite loop. Each wait now has a deadline.

* **THE PORT WAS A CONSTANT.** `param([string]$BaseUrl = "http://127.0.0.1:5088")` -- `5088` is the
  OWNER's server. The URL is read ONCE through `lib.resolve_base_url`.

* **`Get-Payload`'s `catch { return $null }` DISCARDED WHY A PAYLOAD WOULD NOT PARSE.** The port
  uses `lib.get_debug_payload`, which distinguishes "no payload" from "a payload that would not
  parse". In the poll loop a round whose payload is unparseable is SKIPPED with a warning -- the
  original's `if ($p)` skipped it silently -- and the refusal vocabulary names the case.

DELIBERATELY UNCHANGED
----------------------
Same recipe in the same order: health gate (ok AND injectorConnected), optional setup, demo-all, then
the bar-status poll (POST, wait up to 3s for the event, 400ms between rounds, overall deadline
`--wait-draw-sec`). Same five verdict criteria, same `[OK]`/`[FAIL]` rows, same PASS line and the
same failure hints. Same exit contract: 1 when any criterion fails, 0 on pass.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import live_lawn_setup as lib  # noqa: E402  (the shared live-lawn library; see scripts/lib/)

TOOL_ID = "probe-live-shield-bar"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_WAIT_DRAW_SEC = 8
DEFAULT_TIMEOUT = 15
HEALTH_TIMEOUT = 5
DEMO_WAIT_TIMEOUT = 15
BAR_STATUS_WAIT_TIMEOUT = 3
BAR_STATUS_POLL_SEC = 0.4
POLL_SEC = 0.2
SETUP_TIMEOUT_SEC = 120

DEMO_ALL_PATH = "/api/debug/shield/demo-all"
BAR_STATUS_PATH = "/api/debug/shield/bar-status"

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "INVALID-WAIT-DRAW", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED",
    "SETUP-FAILED", "DEMO-ALL-MISSING", "BAR-STATUS-MISSING", "REQUEST-FAILED",
    "RESPONSE-NOT-JSON",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request`, `subprocess` and
# `time` are process-wide modules: a test that patches any of them reaches every other test in this
# project. The event reads go through `lib`, whose own `_URLOPEN` seam the suite patches the same
# way.
_URLOPEN = urllib.request.urlopen
_SLEEP = time.sleep
_RUN = subprocess.run
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
        headers={"User-Agent": "FusionRpg-probe-live-shield-bar/1.0",
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


def wait_kind(base_url: str, after_id: int, kind: str, timeout_sec: int) -> dict | None:
    """Wait for an event of `kind` to land after `after_id`. None when the deadline expires.

    The original's `Wait-Kind` shape is preserved: page forward from the cursor, take the LAST
    matching event on the page, sleep 200ms between pages. The deadline is the port's addition -- the
    original had none, so a kind that never arrived meant an infinite loop.
    """
    deadline = _MONOTONIC() + timeout_sec
    cursor = after_id
    while _MONOTONIC() < deadline:
        items = lib.get_events(base_url, cursor, 100)
        hit = None
        for event in items:
            if event.get("kind") == kind:
                hit = event
        if hit is not None:
            return hit
        if items:
            cursor = int(items[-1].get("id", cursor))
        _SLEEP(POLL_SEC)
    return None


def run_setup_script(base_url: str) -> None:
    """Run the sibling lab-setup script, which is a PYTHON tool now.

    Two things were stale here and both were the retirement rather than the split.

    The sibling is `setup_shield_bar_lab.py`. `setup-shield-bar-lab.ps1` is gone - the port landed as
    `setup_shield_bar_lab.py` - and this function still named it, so every `-Setup` run refused
    SETUP-FAILED with "the sibling setup script does not exist". Its docstring claimed "no Python port
    exists yet", which had stopped being true.

    And it SHELLED OUT TO `pwsh`, which the ps1 ruling forbids: this repository is retiring PowerShell,
    and a Python tool that shells back out to an interpreter is a wrapper, not a port. Neither suite
    tested for it - they assert the tool answers no PowerShell-SPELLED parameter and states why
    PowerShell was retired, which is a different claim from "does not invoke the interpreter". The two
    probes each carried their own copy of this function, so the stale spelling was in both.

    The flag moves too: the port takes `--base-url`, not `-BaseUrl`.
    """
    sibling = Path(__file__).resolve().parent / "setup_shield_bar_lab.py"
    if not sibling.is_file():
        raise Refusal("SETUP-FAILED",
                      f"the sibling setup script does not exist: {sibling}")
    try:
        proc = _RUN(
            [sys.executable, str(sibling), "--base-url", base_url],
            capture_output=True, text=True, timeout=SETUP_TIMEOUT_SEC)
    except FileNotFoundError as error:  # pragma: no cover - sys.executable is this process
        raise Refusal("SETUP-FAILED", f"this interpreter could not be re-invoked: {error}") from error
    except subprocess.TimeoutExpired as expired:
        raise Refusal("SETUP-FAILED",
                      f"{sibling.name} did not finish within {SETUP_TIMEOUT_SEC}s") from expired
    if proc.returncode != 0:
        tail = ((proc.stdout or "") + (proc.stderr or ""))[-800:]
        raise Refusal("SETUP-FAILED",
                      f"{sibling.name} exited {proc.returncode}:\n{tail}")


def run_probe(base_url: str, setup: bool, wait_draw_sec: int, timeout: int) -> dict:
    """The whole recipe. Returns the best bar-status payload; raises Refusal on a precondition."""
    print("== health ==", file=sys.stderr)
    health = _request(f"{base_url}/health", None, HEALTH_TIMEOUT, "GET /health")
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      "injector not connected — start Melon game on an Adventure lawn")
    print(f"  injectorConnected={health.get('injectorConnected')}", file=sys.stderr)

    if setup:
        print("== setup lab-shield-bar ==", file=sys.stderr)
        run_setup_script(base_url)

    print("== ensure shields (demo-all) ==", file=sys.stderr)
    after = lib.get_debug_max_event_id(base_url, timeout=timeout)
    _request(f"{base_url}{DEMO_ALL_PATH}", {"amount": 100}, timeout,
             "POST /api/debug/shield/demo-all")
    demo_event = wait_kind(base_url, after, "debug.shield.demo-all", DEMO_WAIT_TIMEOUT)
    if demo_event is None:
        raise Refusal("DEMO-ALL-MISSING",
                      f"no debug.shield.demo-all event within {DEMO_WAIT_TIMEOUT}s of the POST")
    demo = lib.get_debug_payload(demo_event)
    print(f"  targets={demo.get('targetCount') if isinstance(demo, dict) else '?'}",
          file=sys.stderr)

    print("== wait world VFX bars (poll bar-status) ==", file=sys.stderr)
    best: dict | None = None
    deadline = _MONOTONIC() + wait_draw_sec
    while _MONOTONIC() < deadline:
        after = lib.get_debug_max_event_id(base_url, timeout=timeout)
        _request(f"{base_url}{BAR_STATUS_PATH}", {}, timeout, "POST /api/debug/shield/bar-status")
        event = wait_kind(base_url, after, "debug.shield.bar-status", BAR_STATUS_WAIT_TIMEOUT)
        payload = None
        if event is not None:
            try:
                parsed = lib.get_debug_payload(event)
            except lib.Refusal as refusal:
                # The original's `if ($p)` skipped a round whose payload was unparseable. The port
                # skips it too, but says so -- a silently skipped round is indistinguishable from a
                # round that never happened.
                print(f"WARN: skipping a bar-status round: {refusal.detail}", file=sys.stderr)
                parsed = None
            if isinstance(parsed, dict):
                payload = parsed
        if payload is not None:
            best = payload
            last_draw = payload.get("lastDraw") or {}
            print(f"  data={payload.get('dataOwners')} worldBars={payload.get('worldBars')} "
                  f"shaderOk={payload.get('shaderOk')} fillRatio={payload.get('fillRatio')} "
                  f"early={last_draw.get('early')}", file=sys.stderr)
            world_bars = int(payload.get("worldBars") or 0)
            if (bool(payload.get("shaderOk")) and world_bars > 0
                    and world_bars == int(payload.get("dataOwners") or 0)):
                break
        _SLEEP(BAR_STATUS_POLL_SEC)

    if best is None:
        raise Refusal("BAR-STATUS-MISSING",
                      "no debug.shield.bar-status — is injector build current?")
    return {"best": best}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="probe-live-shield-bar",
        description="Tight shield-bar HUD probe -- world VFX bars, not IMGUI "
                    "(replaces probe-live-shield-bar.ps1).")
    parser.add_argument("--base-url", default="",
                        help="server base (default: $FUSIONRPG_SERVER_URL, else the built-in default)")
    parser.add_argument("--setup", action="store_true",
                        help="run the sibling setup-shield-bar-lab.ps1 first")
    parser.add_argument("--wait-draw-sec", type=int, default=DEFAULT_WAIT_DRAW_SEC,
                        help=f"overall bar-status poll budget (default {DEFAULT_WAIT_DRAW_SEC})")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per request (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout {args.timeout} must be positive", args.json)
    if args.wait_draw_sec <= 0:
        return _refuse("INVALID-WAIT-DRAW",
                       f"--wait-draw-sec {args.wait_draw_sec} is not a budget", args.json)

    base_url, source = lib.resolve_base_url(args.base_url)
    try:
        evidence = run_probe(base_url, args.setup, args.wait_draw_sec, args.timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)

    best = evidence["best"]
    data_ok = int(best.get("dataOwners") or 0) > 0
    shader_ok = bool(best.get("shaderOk"))
    world_bars = int(best.get("worldBars") or 0)
    bars_ok = world_bars > 0 and world_bars == int(best.get("dataOwners") or 0)
    ratio_ok = float(best.get("fillRatio") or 0) > 0
    early_ok = str((best.get("lastDraw") or {}).get("early") or "") == "ok"

    print("", file=sys.stderr)
    print("== verdict ==", file=sys.stderr)
    for ok, label, detail in (
            (data_ok, "injector has shields", f"dataOwners={best.get('dataOwners')}"),
            (shader_ok, "OverlayShaderProbe material ok", f"shaderOk={best.get('shaderOk')}"),
            (bars_ok, "world VFX bars live", f"worldBars={best.get('worldBars')}"),
            (ratio_ok and early_ok, "fill length from capacity",
             f"fillRatio={best.get('fillRatio')} early={(best.get('lastDraw') or {}).get('early')}")):
        print(f"  [{'OK' if ok else 'FAIL'}] {label} ({detail})", file=sys.stderr)

    passed = data_ok and shader_ok and bars_ok and ratio_ok and early_ok
    if not passed:
        print("", file=sys.stderr)
        print("Look under pea/zombie for shader bars (not top-left GUI).", file=sys.stderr)
        print("early=no-shader → OverlayShaderProbe failed (no GUI fallback).", file=sys.stderr)
        print("early=no-body → shields exist but AnchorResolver miss.", file=sys.stderr)
    else:
        print("", file=sys.stderr)
        print("PASS — world shield bars should be under shielded units (length = capacity).",
              file=sys.stderr)

    envelope = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": source,
                "verdict": "PASS" if passed else "FAIL",
                "exitCode": EXIT_OK if passed else EXIT_FAILED,
                "criteria": {"dataOk": data_ok, "shaderOk": shader_ok, "barsOk": bars_ok,
                             "ratioOk": ratio_ok, "earlyOk": early_ok},
                "best": best}
    if args.json:
        print(json.dumps(envelope, indent=2))
    return EXIT_OK if passed else EXIT_FAILED


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
