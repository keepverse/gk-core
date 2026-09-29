#!/usr/bin/env python3
"""Poll REAL ground truth (the server's /health plus the game process) with a bounded timeout, instead of
trusting a background shell tool's own "is this task done yet" status. That status has been observed
lagging behind real completion: on 2026-09-14 a deploy had already launched a healthy, connected game
more than four minutes before the task runner still reported it "running". Never poll this in an
unbounded loop; it always terminates, successfully or with a clear timeout message.

Replaces `scripts/wait-for-deploy.ps1`.

Usage:
  gk-core/scripts/wait_for_deploy.py                                  # 300s budget, 5s interval, needs a
                                                           # healthy server AND an injected game
  gk-core/scripts/wait_for_deploy.py --timeout-sec 120 --interval-sec 3
  gk-core/scripts/wait_for_deploy.py --no-game                       # server-only restart, no game needed
  gk-core/scripts/wait_for_deploy.py --json                           # the verdict, machine-readable

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** `[string]$BaseUrl = "http://127.0.0.1:5088"`.
  This machine has a three-slot pool on 5101/5102/5103, and `5088` is the OWNER's server -- not "the"
  port. A probe pointed at the owner's server measures the wrong server, or nothing, and reports it as
  ground truth; the slot-side rule exists because that mistake has already been made once. The base URL
  is read ONCE from `FUSIONRPG_SERVER_URL` (the variable the Injector itself reads, so both sides agree)
  and falls back to `127.0.0.1:5088` only when nothing is configured. **Where the value came from is
  REPORTED**, because a probe that silently measured the wrong server is worse than one that refused.

* **THE GAME CHECK COULD SEE ANOTHER SLOT'S GAME.** `Get-Process -Name PlantsVsZombiesRH` matches by
  NAME, so with three slots running, a probe for slot 2 would see slot 1's game and report a confirmed
  deploy it had not observed. When a game install is known -- `--game-install`, or `FUSIONRPG_GAME_DIR`,
  or the slot the pool is configured for -- the process is matched by its EXECUTABLE PATH under that
  install. Without one it still matches by name, and the verdict says that is what it did, because a
  name-only match is a weaker claim and should not read like a strong one.

* **THE HEALTH CALL'S ERROR WAS SWALLOWED BY AN EMPTY `catch { }`.** Any failure at all -- connection
  refused, a timeout, a body that is not JSON -- became `$health = $null` and the loop kept going with
  no record of why. The last error is now carried and reported, so a timeout that says "server not
  answering" also says what the server said about it.

* **A TIMEOUT AND A CRASH SHARED EXIT CODE 1.** A caller could not tell "the deploy genuinely did not
  arrive" from "this script broke", and those warrant different responses. `0` ready, `1` the budget
  expired (with the reason ladder and the attempts), `64` a refusal.

* **NO MACHINE-READABLE VERDICT.** `--json` reports the last health document, the reason ladder, the
  attempt count, the elapsed seconds, where the base URL came from, and whether the game match was by
  path or by name.

DELIBERATELY UNCHANGED
----------------------
Same four inputs and defaults, the same readiness condition (`health.ok` AND `health.injectorConnected`
AND a game), the same four-step reason ladder, the same closing advice to read the deploy's own output
rather than assume it is still running, and the same "never poll in an unbounded loop" property: the loop
is bounded by a wall-clock budget, and the per-request timeout is a separate, smaller, explicit bound.

A REAL RUN IS NOT A UNIT TEST and is not attempted here. The contract suite drives a REAL HTTP server
over loopback, including one that is healthy but not injector-connected -- which is the state this tool
exists to report and which a stub returning a hand-made JSON document would not reproduce.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

TOOL_ID = "wait-for-deploy"

EXIT_TIMEOUT = 1
EXIT_REFUSED = 64

DEFAULT_BASE_URL = "http://127.0.0.1:5088"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
GAME_DIR_ENV = "FUSIONRPG_GAME_DIR"
POOL_ENV = "FUSIONRPG_GAME_POOL"
GAME_PROCESS = "PlantsVsZombiesRH"

DEFAULT_TIMEOUT_SEC = 300
DEFAULT_INTERVAL_SEC = 5
DEFAULT_REQUEST_TIMEOUT = 5

# The reason ladder, as a CLOSED vocabulary. The original had it as an if/elseif chain producing a
# string; making it closed means a caller can branch on it and a test can assert membership.
REASON_READY = "ready"
REASON_NO_HEALTH = "server-not-answering"
REASON_NOT_OK = "health-ok-false"
REASON_NOT_CONNECTED = "injector-connected-false"
REASON_NO_GAME = "game-process-not-found"
REASONS = {REASON_READY, REASON_NO_HEALTH, REASON_NOT_OK, REASON_NOT_CONNECTED, REASON_NO_GAME}

REFUSAL_REASONS = {"INVALID-TIMEOUT", "INVALID-INTERVAL", "BASE-URL-INVALID", "GAME-INSTALL-MISSING"}

# Bound once, module-private: `subprocess` and `urllib` are process-wide modules, and a test that
# patches either reaches every other test in the project. `subprocess` is bound HERE rather than
# imported inside `_tasklist`, which would have defeated the seam entirely.
_URLOPEN = urllib.request.urlopen
_RUN = subprocess.run


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def log(message: str) -> None:
    print(f"==> {message}", file=sys.stderr)


def probe_health(base_url: str, timeout: int) -> tuple[dict | None, str | None]:
    """The real health document, or `(None, why-not)`. Both are returned, because the original discarded
    the reason and a timeout that only says "not answering" costs the reader a round of guessing."""
    url = f"{base_url.rstrip('/')}/health"
    request = urllib.request.Request(url, headers={"User-Agent": "FusionRpg-wait-for-deploy/1.0"})
    try:
        with _URLOPEN(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        return None, f"HTTP {error.code} {error.reason}"
    except TimeoutError:
        return None, f"no answer within {timeout}s"
    except urllib.error.URLError as error:
        reason = error.reason
        return None, f"unreachable: {reason}"
    except OSError as error:
        return None, f"unreachable: {error}"
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        return None, f"the answer is not JSON: {error}"
    if not isinstance(document, dict):
        return None, f"the answer is a {type(document).__name__}, not an object"
    return document, None


def ladder_reason(health: dict | None, game_up: bool) -> str:
    """The same four-step ladder, in the same order, as a closed value."""
    if health is None:
        return REASON_NO_HEALTH
    if not health.get("ok"):
        return REASON_NOT_OK
    if not health.get("injectorConnected"):
        return REASON_NOT_CONNECTED
    if not game_up:
        return REASON_NO_GAME
    return REASON_READY


def game_running(install: Path | None) -> tuple[bool, str]:
    """Is the game up? By executable path under `install` when one is known, else by NAME.

    A name-only match is a weaker claim on a multi-slot machine -- the original's name-only match could
    see another slot's game and report a deploy it never observed -- so which kind of match happened is
    returned alongside the answer.
    """
    try:
        completed = _tasklist()
    except OSError as error:  # pragma: no cover - only on a host with no task listing at all
        return False, f"could not list processes: {error}"
    lines = completed.splitlines()
    rows = [ln for ln in lines if GAME_PROCESS.lower() in ln.lower()]
    if not rows:
        return False, "by-name"
    if install is None:
        return True, "by-name"
    needle = str(install.resolve()).lower()
    for row in rows:
        if needle in row.lower().replace("/", "\\"):
            return True, "by-path"
    return False, "by-name"


def _tasklist() -> str:
    """A bounded, captured process listing, over the private seam."""
    return _RUN(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True, timeout=30).stdout or ""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="wait-for-deploy",
        description="Poll the server's /health and the game process until the deploy is confirmed live "
                    "or the budget expires (replaces wait-for-deploy.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to poll (default: ${BASE_URL_ENV}, else {DEFAULT_BASE_URL}). "
                             f"Read from the environment so the probe and the injector read the SAME "
                             f"port; the original hardcoded the owner's default, which on a three-slot "
                             f"pool is the wrong server")
    parser.add_argument("--game-install", default="",
                        help=f"a game install, so the process is matched by PATH under it rather than by "
                             f"name (default: ${GAME_DIR_ENV}, else nothing). The pool root is also "
                             f"consulted via ${POOL_ENV}")
    parser.add_argument("--timeout-sec", type=int, default=DEFAULT_TIMEOUT_SEC,
                        help=f"total budget in seconds (default {DEFAULT_TIMEOUT_SEC})")
    parser.add_argument("--interval-sec", type=int, default=DEFAULT_INTERVAL_SEC,
                        help=f"seconds between checks (default {DEFAULT_INTERVAL_SEC})")
    parser.add_argument("--request-timeout", type=int, default=DEFAULT_REQUEST_TIMEOUT,
                        help=f"seconds for one /health call (default {DEFAULT_REQUEST_TIMEOUT})")
    parser.add_argument("--no-game", action="store_true",
                        help="server-only restart: do not wait for a game process")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON on stdout")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.timeout_sec <= 0:
        return _refuse("INVALID-TIMEOUT", f"--timeout-sec {args.timeout_sec} must be positive", args.json)
    if args.interval_sec <= 0:
        return _refuse("INVALID-INTERVAL", f"--interval-sec {args.interval_sec} must be positive", args.json)
    if args.request_timeout <= 0:
        return _refuse("INVALID-TIMEOUT", f"--request-timeout {args.request_timeout} must be positive", args.json)

    base_url, base_source = _resolve_base_url(args.base_url)
    if not base_url.lower().startswith(("http://", "https://")):
        # Validated HERE rather than inside the resolver, so a caller that asked for --json gets a
        # machine-readable refusal. The first version raised SystemExit from the resolver, which
        # honoured neither the flag nor the exit-code vocabulary.
        return _refuse("BASE-URL-INVALID",
                       f"the base URL {base_url!r} (from {base_source}) is not an http(s) URL. The "
                       f"original would have built '{base_url}/health' and surfaced a urllib error "
                       f"instead of a word about the input", args.json)
    # No rstrip here: `probe_health` already builds its URL as f"{base_url.rstrip('/')}/health", so a
    # second trim in main can never change the request. A mutant that removed it survived, which is the
    # measurement that says the line was dead; the trim in probe_health is the one that does the work.
    install, install_source = _resolve_install(args.game_install)
    if install_source.startswith("explicit") and not install.is_dir():
        return _refuse("GAME-INSTALL-MISSING",
                       f"--game-install {install} does not exist, so a by-path game match can never "
                       f"succeed. Pass a real install, or omit it and accept a by-name match, which is "
                       f"the weaker claim", args.json)

    started = time.monotonic()
    deadline = started + args.timeout_sec
    attempt = 0
    health: dict | None = None
    health_error: str | None = None
    game_match = "skipped" if args.no_game else "not-checked"
    reason = REASON_NO_HEALTH

    envelope: dict = {"tool": TOOL_ID, "baseUrl": base_url, "baseUrlSource": base_source,
                      "gameInstall": str(install) if install else None,
                      "gameInstallSource": install_source, "noGame": args.no_game,
                      "timeoutSec": args.timeout_sec, "intervalSec": args.interval_sec,
                      "reasons": sorted(REASONS)}
    while True:
        attempt += 1
        health, health_error = probe_health(base_url, args.request_timeout)
        if args.no_game:
            game_up, game_match = True, "skipped"
        else:
            game_up, game_match = game_running(install)
        reason = ladder_reason(health, game_up)
        if reason == REASON_READY:
            break
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        log(f"[{attempt}] not ready yet ({reason})"
            + (f" -- {health_error}" if health is None and health_error else "")
            + f" -- retrying in {min(args.interval_sec, max(0.0, remaining)):.0f}s")
        time.sleep(min(args.interval_sec, max(0.0, remaining)))

    # The loop's own `reason` IS the answer. The first version recomputed it after the loop, which meant
    # a SECOND process listing after the budget had expired -- a claim about a moment never checked, and
    # a race against a game starting in between.
    envelope.update({
        "verdict": "READY" if reason == REASON_READY else "TIMEOUT",
        "exitCode": 0 if reason == REASON_READY else EXIT_TIMEOUT,
        "ready": reason == REASON_READY,
        "reason": reason,
        "attempts": attempt,
        "health": health,
        "healthError": health_error,
        "gameMatch": game_match,
        "seconds": round(time.monotonic() - started, 3),
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        if reason == REASON_READY:
            log(f"Deploy confirmed live after {attempt} check(s) in {envelope['seconds']}s")
            print(json.dumps(health, indent=2))
        else:
            log(f"TIMEOUT after {args.timeout_sec}s -- the deploy did not reach a confirmed-live state "
                f"(last reason: {reason}, {attempt} attempt(s)).")
            log("Check the deploy's own output directly rather than assuming it is still running.")
    return envelope["exitCode"]


def _resolve_base_url(given: str) -> tuple[str, str]:
    """The server to poll, and WHERE the value came from. Read once, explicitly."""
    if given:
        url, source = given, "--base-url"
    elif os.environ.get(BASE_URL_ENV, "").strip():
        url, source = os.environ[BASE_URL_ENV].strip(), f"${BASE_URL_ENV}"
    else:
        url, source = DEFAULT_BASE_URL, f"the built-in default ({BASE_URL_ENV} unset)"
    return url, source


def _resolve_install(given: str) -> tuple[Path | None, str]:
    """The game install to match a process path against, and where it came from."""
    if given:
        return Path(given).expanduser(), "explicit --game-install"
    env = os.environ.get(GAME_DIR_ENV, "").strip()
    if env:
        return Path(env).expanduser(), f"${GAME_DIR_ENV}"
    pool = os.environ.get(POOL_ENV, "").strip()
    if pool:
        # The pool root is a CONFIGURED location for exactly this reason; a slot's own install is under
        # it, and the session's own slot is the one whose game it is entitled to observe.
        return Path(pool).expanduser(), f"${POOL_ENV} (the pool root, not one slot's install)"
    return None, "none: a by-name match only"


def _refuse(reason: str, detail: str, as_json: bool) -> int:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                          "exitCode": EXIT_REFUSED, "ready": False}, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
