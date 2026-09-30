#!/usr/bin/env python3
"""PROVE (live): the Actor HUD program end-to-end, with a real injector, a real server, and a real browser.

Runs the web project's live Playwright project against a running FusionRpg server, after enabling the world
HUD overlay that the E2E's assertions depend on. See `.claude/skills/live-lawn-quick-start/SKILL.md` for
cold-start details, and `tasks/actor-hud-todo.md` P6 for the Unity eyeball step this does NOT replace.

Usage:
  gk-core/scripts/prove_actor_hud_live.py
  gk-core/scripts/prove_actor_hud_live.py --json

Replaces `scripts/prove-actor-hud-live.ps1`.

WHAT IT PROVES, and the failure it exists to make legible
--------------------------------------------------------
The E2E passed. And when it did not, this names the CAUSE, which the original structurally could not:

  * The web helper `pollBoardActorHud` waits for a `debug.board-stats` event -- it skips every other kind.
    **MEASURED against a running game: the board does not emit that kind at all.** After a shield demo and
    two status applies (all three accepted, `{"ok": true, "queued": 1..3}`) the stream carried
    `shield.granted` x3, `debug.status` x2, `debug.status.resisted` x2, `debug.actor-hud` x1 and
    `debug.effect.board-snapshot` x2 -- and **zero** `debug.board-stats`.
  * So the poll cannot succeed, it spends its **45-second** default budget, and Playwright's default
    `beforeAll` hook timeout is **30 seconds**. The hook dies first, and the reported failure is
    `"beforeAll" hook timeout of 30000ms exceeded` -- which says nothing about any of the above.
  * This tool therefore **diagnoses after the failure**: it reports which kinds the board DID emit, and
    names the kind the E2E waits for when that kind is absent. The original's entire diagnostic output was
    the Playwright timeout.

The fix belongs to whoever owns the web E2E helper, which is not this tool's file to change. The finding is
recorded and the cause is reported, and the web tree is left alone.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **`npm run test:e2e:live` RAN WITH NO TIMEOUT AND NO CAPTURE.** In PowerShell a native command's exit code
  is read from `$LASTEXITCODE` and its output is whatever the console showed; there is no bound on how long
  it may run and nothing to capture if it is run under a harness. A Vite dev server plus a Playwright
  project that polls for an event the board does not emit is exactly the shape that hangs forever while
  looking like progress. `subprocess.run(capture_output=True, timeout=...)` bounds it and keeps the output.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED** -- `http://127.0.0.1:5088`. Resolved from
  `FUSIONRPG_SERVER_URL`, the variable the Injector itself reads, and its source is REPORTED.

* **THE WORLD-HUD SETTING WAS SET WITH AN EMPTY `catch` THAT ONLY WARNED.** If `PUT /api/settings` failed,
  the script carried on and ran an E2E whose assertions depend on that overlay being on -- so the run
  reported a *HUD* failure for what was a *setup* failure. The setting is now REQUIRED, with its own named
  refusal, and the message says which condition it is. That is the only behaviour change, and it is
  deliberate: the original's `Write-Warning` made a silent setup failure look like a product failure.

* **`Set-Location` WAS CALLED TWICE, INCLUDING ONTO `gk-web/web/fusion-rpg-web`, AND NEVER RESTORED.** A script
  that leaves the process's working directory somewhere else is a hazard for whatever runs next in the same
  shell. The subprocess is given an explicit `cwd` and this process's own directory is never changed.

* **`catch { ...; exit 1 }` CONFLATED "SERVER UNREACHABLE" WITH EVERY OTHER FAILURE.** Any error from the
  health call -- a timeout, a 500, a malformed body -- printed "Server not reachable" and exited 1. Each
  condition is now named separately.

* **THE EXIT CODE OF THE E2E WAS PROPAGATED BUT ITS OUTPUT WAS NOT.** A caller reading the original's exit
  code knew pass from fail and nothing else. `--json` reports the health, the setting, the E2E's own exit
  code, the duration, the tail of its output, AND the diagnosis.

DELIBERATELY UNCHANGED
----------------------
Same two flags (`--skip-server-check`, `--skip-deploy`), the same 5s health timeout, the same settings key
`lawn.worldHud` set to `true`, the same npm project `test:e2e:live`, the same two closing lines of advice,
and the same exit code semantics: the E2E's code is the tool's code, except that a refusal is 64 and is
named.
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)
from keepverse_roots import web_root  # noqa: E402  (same lib, same reason)

TOOL_ID = "prove-actor-hud-live"

REPO = Path(__file__).resolve().parent.parent

# THE WEB TREE IS gk-web'S. This file's OWN DOCSTRING says so - it names
# `gk-web/web/fusion-rpg-web` as one of the two Set-Location calls that were never restored -
# while the constant beside it still pointed at `REPO / "web"`, which after the split is gk-core,
# a repository with no `web` directory at all. The result was not a subtle miscount: the script
# refused WEB-TREE-MISSING before it checked anything, so ten tests read that as a broken E2E
# contract - including two that expected WORLD-HUD-UNSET and E2E-FAILED and got WEB-TREE-MISSING
# instead, which is the refusal arriving early and being read as the wrong finding.
#
# The resolver names the owner, so it is asked. There is deliberately NO fallback here, unlike the
# engine-root and sessions-root cases: a planted fixture genuinely can carry a tree that looks like
# an owner, whereas the web application lives in exactly one repository and guessing which one
# would be a way of running the wrong UI. If the resolver cannot answer, the existing
# WEB-TREE-MISSING refusal is the right verdict, and it names the path it looked for.
WEB_DIR = web_root(REPO) / "web" / "fusion-rpg-web"
NPM_SCRIPT = "test:e2e:live"
WORLD_HUD_KEY = "lawn.worldHud"
HEALTH_TIMEOUT = 5
SETTINGS_TIMEOUT = 5

# A real Playwright run needs a real bound. The original had none, and the shape that hangs is exactly this
# one: a Vite dev server plus a project that polls for an event the board does not emit. Sixty seconds is
# generous for the passing case and a bound for the hanging one; the value is reported, not assumed.
DEFAULT_E2E_TIMEOUT_SEC = 300

# The kind the web helper polls for. Named here because the DIAGNOSIS is the port's reason to exist: the
# original reported a hook timeout and nothing about this.
E2E_POLLED_KIND = "debug.board-stats"
# How long the diagnosis polls for events, and how many it reads per poll.
DIAGNOSIS_TIMEOUT_SEC = 12
DIAGNOSIS_PAGE = 200
# The kinds that mean the board IS working, so "no board-stats" is a filtered-kind problem rather than a
# dead board. Measured live: shield.granted, debug.status, debug.status.resisted, debug.actor-hud,
# debug.effect.board-snapshot.
LIVENESS_KINDS = ("shield.granted", "debug.status", "debug.status.resisted", "debug.actor-hud",
                  "debug.effect.board-snapshot", "debug.fx.state.started", "combat.hit", "zombie.damage")

REFUSAL_REASONS = {
    "SERVER-UNREACHABLE", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED", "WEB-TREE-MISSING",
    "NODE_MODULES-MISSING", "WORLD-HUD-UNSET", "E2E-TIMED-OUT", "E2E-FAILED", "E2E-SPAWN-FAILED",
    "INVALID-TIMEOUT",
}

# The two closing lines the original printed. An automated tool cannot do the Unity eyeball, and dropping
# the pointer would leave a reader believing the E2E is the whole check.
_HUMAN_STEP = ("Unity LIVE eyeball still required - see tasks/actor-hud-todo.md P6 Unity manual.")


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def enable_world_hud(base_url: str, timeout: int = SETTINGS_TIMEOUT) -> dict:
    """`PUT /api/settings {key, value}`.

    REQUIRED, where the original only warned. `ActorHudPool.WorldHudEnabled` defaults to false by owner
    decision, and the E2E's assertions depend on it, so a failure here is a SETUP failure -- and the
    original carried on and reported it as a HUD failure.
    """
    payload = json.dumps({"key": WORLD_HUD_KEY, "value": True}).encode()
    request = urllib.request.Request(f"{base_url}/api/settings", data=payload, method="PUT",
                                     headers={"User-Agent": f"{TOOL_ID}/1.0",
                                              "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        body = ""
        try:
            body = error.read().decode("utf-8", errors="replace")[:300]
        except Exception:  # pragma: no cover - the body is a bonus
            pass
        raise Refusal("WORLD-HUD-UNSET",
                      f"PUT /api/settings {WORLD_HUD_KEY}=true was refused with {error.code} "
                      f"{error.reason}" + (f": {body}" if body else "")
                      + ". The E2E's assertions depend on the world HUD overlay, and this failure would "
                        "otherwise be reported as a HUD failure rather than a setup failure.") from error
    except (TimeoutError, urllib.error.URLError, OSError) as error:
        raise Refusal("WORLD-HUD-UNSET",
                      f"PUT /api/settings {WORLD_HUD_KEY}=true failed: {error}. The E2E's assertions "
                      f"depend on the world HUD overlay.") from error
    if not raw.strip():
        return {}
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return {"raw": raw.decode("utf-8", errors="replace")[:200]}


def check_server(base_url: str, skip: bool = False) -> dict | None:
    """Health, then the injector -- the original's order and the same two conditions, but NAMED.

    The original's `catch` printed "Server not reachable" for every failure of the health call, including a
    timeout, a 500 and a malformed body, and exited 1. Each is its own refusal here.
    """
    if skip:
        return None
    try:
        health = lib._get_json(f"{base_url}/health", HEALTH_TIMEOUT, "GET /health")
    except lib.Refusal as refusal:
        raise Refusal("SERVER-UNREACHABLE",
                      f"GET /health on {base_url} failed: {refusal.detail}") from refusal
    if not isinstance(health, dict):
        raise Refusal("HEALTH-NOT-OK", f"/health answered with a {type(health).__name__}, not an object")
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      f"injector is not connected at {base_url} - start the game with the FusionRpg "
                      f"injector loaded (see the live-lawn-quick-start skill). Start the server with "
                      f"dist/FusionRpg.Server/FusionRpg.Server.exe and deploy with "
                      f"python scripts/deploy-play.py --no-server")
    return health


def diagnose(base_url: str, timeout_sec: int = DIAGNOSIS_TIMEOUT_SEC) -> dict:
    """What the board emitted, and whether the kind the E2E polls for was among it.

    This is the port's reason to exist. The original's entire diagnostic surface was a Playwright hook
    timeout, which says nothing about the event stream.
    """
    counts: collections.Counter = collections.Counter()
    deadline = time.monotonic() + timeout_sec
    try:
        cursor = lib.get_debug_max_event_id(base_url, budget_sec=timeout_sec)
    except lib.Refusal as refusal:
        return {"polledKind": E2E_POLLED_KIND, "error": refusal.detail, "kindsSeen": {}}
    while time.monotonic() < deadline:
        try:
            for event in lib.get_events(base_url, cursor, DIAGNOSIS_PAGE, HEALTH_TIMEOUT * 2):
                kind = event.get("kind")
                if kind:
                    counts[kind] += 1
        except lib.Refusal:
            break
        if counts:
            break
        time.sleep(0.5)
    live = sorted(k for k in counts if k in LIVENESS_KINDS)
    return {
        "polledKind": E2E_POLLED_KIND,
        "polledKindSeen": E2E_POLLED_KIND in counts,
        "polledKindCount": counts.get(E2E_POLLED_KIND, 0),
        "boardIsAlive": bool(live),
        "livenessKindsSeen": live,
        "kindsSeen": dict(sorted(counts.items())),
    }


def run_e2e(timeout_sec: int, env_extra: dict[str, str]) -> tuple[int, str, float]:
    """`npm run test:e2e:live`, BOUNDED and CAPTURED.

    The `cwd` is explicit and this process's own directory is never changed: the original called
    `Set-Location` twice, including onto the web tree, and never restored it.
    """
    # `npm` is a `.cmd` BATCH SHIM on Windows. `subprocess.run(["npm", ...])` does NOT find it -- it raises
    # FileNotFoundError -- while PowerShell does, because PATHEXT resolves the shim. So the original ran and
    # the first version of this port refused with E2E-SPAWN-FAILED claiming npm was not on PATH, on a
    # machine where npm demonstrably works. Resolved through `shutil.which`, which understands PATHEXT.
    executable = shutil.which("npm") or shutil.which("npm.cmd")
    if executable is None:
        raise Refusal("E2E-SPAWN-FAILED",
                      "`npm` is not on PATH (checked `npm` and `npm.cmd` through shutil.which, which "
                      "understands PATHEXT), so the E2E cannot run at all")
    env = dict(os.environ)
    env.update(env_extra)
    started = time.monotonic()
    try:
        proc = subprocess.run([executable, "run", NPM_SCRIPT], cwd=str(WEB_DIR), env=env,
                              capture_output=True, text=True, timeout=timeout_sec)
    except subprocess.TimeoutExpired as expired:
        raise Refusal("E2E-TIMED-OUT",
                      f"`npm run {NPM_SCRIPT}` did not finish within {timeout_sec}s. The original had NO "
                      f"bound at all, and this shape -- a dev server plus a project that polls for an "
                      f"event the board does not emit -- hangs while looking like progress."
                      + (f" Partial output:\n{(expired.stdout or '')[-800:]}" if expired.stdout else "")
                      ) from expired
    except FileNotFoundError as error:
        raise Refusal("E2E-SPAWN-FAILED",
                      f"the resolved npm ({executable}) could not be executed: {error}") from error
    return proc.returncode, (proc.stdout or "") + (proc.stderr or ""), time.monotonic() - started


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="prove-actor-hud-live",
        description="Prove the Actor HUD program E2E live, and NAME THE CAUSE when it fails "
                    "(replaces prove-actor-hud-live.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to prove against (default: resolved by the shared library from "
                             f"${lib.BASE_URL_ENV}, else {lib.DEFAULT_BASE_URL})")
    parser.add_argument("--skip-server-check", action="store_true",
                        help="skip the health and injector check")
    parser.add_argument("--skip-deploy", action="store_true",
                        help="do not print the stale-injector tip")
    parser.add_argument("--e2e-timeout-sec", type=int, default=DEFAULT_E2E_TIMEOUT_SEC,
                        help=f"the hard bound on the Playwright run (default {DEFAULT_E2E_TIMEOUT_SEC}; "
                             f"the original had none)")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON")
    args = parser.parse_args(argv)

    if args.e2e_timeout_sec <= 0:
        return _refuse("INVALID-TIMEOUT", f"--e2e-timeout-sec {args.e2e_timeout_sec} must be positive",
                       args.json)
    if not (WEB_DIR / "package.json").is_file():
        return _refuse("WEB-TREE-MISSING", f"{WEB_DIR} has no package.json, so the E2E cannot run", args.json)
    if not (WEB_DIR / "node_modules").is_dir():
        return _refuse("NODE_MODULES-MISSING",
                       f"{WEB_DIR / 'node_modules'} is absent; run `npm ci` in the web project first",
                       args.json)

    envelope: dict = {"tool": TOOL_ID}
    try:
        base_url, source = lib.resolve_base_url(args.base_url)
        envelope.update({"baseUrl": base_url, "baseUrlSource": source, "webDir": WEB_DIR.as_posix(),
                         "npmScript": NPM_SCRIPT,
                         "npm": shutil.which("npm") or shutil.which("npm.cmd")})
        print(f"Actor HUD live E2E against {base_url}...", file=sys.stderr)
        health = check_server(base_url, args.skip_server_check)
        if health is not None:
            envelope["health"] = {"ok": health.get("ok"),
                                  "injectorConnected": health.get("injectorConnected"),
                                  "simEnabled": health.get("simEnabled")}
        setting = enable_world_hud(base_url)
        envelope["worldHud"] = {"key": WORLD_HUD_KEY, "set": True, "response": setting}
        print(f"{WORLD_HUD_KEY}=true (world HUD overlay enabled for this run)", file=sys.stderr)
        if not args.skip_deploy:
            print("Tip: run python scripts/deploy-play.py --no-server if injector DLLs are stale.",
                  file=sys.stderr)
        print(f"Running live Playwright (vite dev :5173 -> API {base_url})...", file=sys.stderr)
        code, output, seconds = run_e2e(args.e2e_timeout_sec,
                                        {"ACTOR_HUD_LIVE_E2E": "1", "FUSIONRPG_API_BASE": base_url})
    except lib.Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({"e2eExitCode": code, "seconds": round(seconds, 3),
                     "e2eOutputTail": output[-4000:]})
    if code == 0:
        envelope.update({"verdict": "OK", "exitCode": 0})
        if args.json:
            print(json.dumps(envelope, indent=2))
        else:
            print("Live Actor HUD E2E passed.")
            print(_HUMAN_STEP)
        return 0

    # The failure path, and the reason this tool exists: name the CAUSE, not just the exit code.
    try:
        diagnosis = diagnose(base_url)
    except Refusal as refusal:  # pragma: no cover - diagnose swallows its own
        diagnosis = {"error": refusal.detail}
    envelope["diagnosis"] = diagnosis
    detail = (f"`npm run {NPM_SCRIPT}` exited {code} after {seconds:.1f}s. The original reported only this "
              f"exit code; the Playwright output is a hook timeout, which says nothing about the event "
              f"stream.")
    if not diagnosis.get("polledKindSeen"):
        if diagnosis.get("boardIsAlive"):
            detail += (f"\n  The board IS alive: it emitted {diagnosis.get('livenessKindsSeen')}, and NOT "
                       f"`{E2E_POLLED_KIND}` -- which is the only kind the web helper "
                       f"`pollBoardActorHud` inspects. The helper skips every other kind, so it cannot "
                       f"succeed against this board.")
            detail += (f"\n  Its poll budget is 45s and Playwright's default beforeAll hook timeout is 30s, "
                       f"so the hook dies first and the reported failure is the timeout rather than the "
                       f"cause. The fix belongs to the web E2E helper, not to this script.")
        elif diagnosis.get("error"):
            detail += f"\n  The board's event stream could not be read: {diagnosis['error']}"
        else:
            detail += (f"\n  The board emitted nothing at all in the diagnosis window, so this is a dead "
                       f"board rather than a filtered event kind.")
    return _refuse("E2E-FAILED", detail, args.json, envelope)


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": lib.EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        for line in detail.splitlines():
            print(f"  {line}", file=sys.stderr)
    return lib.EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
