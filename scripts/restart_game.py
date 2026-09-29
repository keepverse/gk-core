#!/usr/bin/env python3
"""Close and relaunch the game process, then poll real ground truth until the injector reconnects.

This is the Python port of ``scripts/restart-game.ps1``.

The easiest, most reliable recovery for a stuck/defeated board: close PlantsVsZombiesRH.exe and
start it fresh. Real problem this replaces (2026-09-14): in-place recovery via debug.ui-nav
(UIMgr.BackToMenu / EnterMainMenu / enter-level) works for menu navigation, but proving a genuinely
fresh SCENE (not a stale Board/InitBoard reference reused across the old and new "run") turned out
to need real engine-level verification this tooling doesn't have yet -- deferred, not abandoned
(see docs/architecture/live-probe/lawn-run-state-machine.md). A full process relaunch sidesteps the
question entirely: a new process cannot have a stale reference from the old one.

Does NOT rebuild the injector or server -- this only restarts the GAME. Use deploy-play.py first
if injector code changed.

Polls the same real ground truth as wait_for_deploy.py (server /health + the game process actually
existing) rather than trusting a fixed sleep -- never assume "the game is probably up by now".

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** ``[string]$BaseUrl = "http://127.0.0.1:5088"``.
  This machine has a three-slot pool on 5101/5102/5103, and ``5088`` is the OWNER's server -- not "the"
  port. A probe pointed at the owner's server measures the wrong server, or nothing, and reports it as
  ground truth. The base URL is read ONCE from ``FUSIONRPG_SERVER_URL`` (the variable the Injector itself
  reads, so both sides agree) and falls back to ``127.0.0.1:5088`` only when nothing is configured.

* **THE HEALTH CALL'S ERROR WAS SWALLOWED BY AN EMPTY ``catch { }``.** Any failure at all -- connection
  refused, a timeout, a body that is not JSON -- became ``$health = $null`` and the loop kept going with
  no record of why. The last error is now carried and reported.

* **A TIMEOUT AND A CRASH SHARED EXIT CODE 1.** A caller could not tell "the game genuinely did not
  reconnect" from "this script broke", and those warrant different responses. ``0`` ready, ``1`` the budget
  expired, ``2`` a refusal.

* **NO MACHINE-READABLE VERDICT.** ``--json`` reports the last health document, the reason ladder, the
  attempt count, the elapsed seconds, where the base URL came from, and whether the game match was by
  path or by name.

DELIBERATELY UNCHANGED
----------------------
Same five inputs and defaults, the same readiness condition (``health.ok`` AND ``health.injectorConnected``
AND a game AND a fresh heartbeat), the same five-step reason ladder, the same "never poll in an
unbounded loop" property: the loop is bounded by a wall-clock budget, and the per-request timeout is a
separate, smaller, explicit bound.

A REAL RUN IS NOT A UNIT TEST and is not attempted here. The contract suite drives a REAL HTTP server
over loopback, including one that is healthy but not injector-connected -- which is the state this tool
exists to report and which a stub returning a hand-made JSON document would not reproduce.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Sequence

TOOL = "restart_game"
TOOL_ID = "restart-game"

# Exit codes
EXIT_OK = 0
EXIT_TIMEOUT = 1
EXIT_REFUSED = 2

# The game process name
GAME_PROCESS = "PlantsVsZombiesRH"

# Default game dir (the MelonLoader install)
DEFAULT_GAME_DIR = r"H:\Games\PVZ-Fusion-3.9_MelonLoader"

# Default base URL (the owner's server)
DEFAULT_BASE_URL = "http://127.0.0.1:5088"

# Env vars
GAME_DIR_ENV = "FUSIONRPG_ML_GAMEDIR"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
POOL_ENV = "FUSIONRPG_GAME_POOL"

# Default timeouts
DEFAULT_TIMEOUT_SEC = 120
DEFAULT_INTERVAL_SEC = 3
DEFAULT_REQUEST_TIMEOUT = 5

# The reason ladder
REASON_READY = "ready"
REASON_NO_GAME = "game-process-not-found"
REASON_NO_HEALTH = "server-not-answering"
REASON_NOT_OK = "health-ok-false"
REASON_NOT_CONNECTED = "injector-connected-false"
REASON_STALE_HEARTBEAT = "heartbeat-still-matches-pre-restart-snapshot"
REASONS = {REASON_READY, REASON_NO_GAME, REASON_NO_HEALTH, REASON_NOT_OK, REASON_NOT_CONNECTED, REASON_STALE_HEARTBEAT}

# Refusals
REFUSALS = {
    "GAME-DIR-MISSING": "the game install directory does not exist",
    "GAME-EXE-MISSING": "PlantsVsZombiesRH.exe not found in the game install",
    "GAME-LOCK-HELD": "the game install is held by another live session",
    "GAME-LOCK-UNAVAILABLE": "game lock tool could not be run",
    "PROCESS-ENUM-FAILED": "the process enumeration did not complete",
    "PROCESS-KILL-FAILED": "a game process could not be killed",
    "EXTERNAL-CALL-FAILED": "an external call failed or timed out",
    "INVALID-TIMEOUT": "the timeout must be positive",
    "INVALID-INTERVAL": "the interval must be positive",
    "BASE-URL-INVALID": "the base URL is not an http(s) URL",
}

# The process enumeration script (same pattern as live_slot.py)
_PROCESS_ENUM_SCRIPT = r"""
$ErrorActionPreference = "SilentlyContinue"
foreach ($p in (Get-Process -Name "PlantsVsZombiesRH")) {
  $path = $null
  try { $path = $p.Path } catch { $path = $null }
  if ($path) { "{0}`t{1}" -f $p.Id, $path }
}
"#RESTART-GAME-ENUM-COMPLETE
"""

# Bound once, module-private: `subprocess` and `urllib` are process-wide modules, and a test that
# patches either reaches every other test in the project.
_URLOPEN = urllib.request.urlopen
_RUN = subprocess.run


class Refusal(Exception):
    """A named, fail-closed precondition failure. Never 'continue and report empty'."""

    def __init__(self, name: str, detail: str, stage: str = "preconditions",
                 exit_code: int = EXIT_REFUSED) -> None:
        if name not in REFUSALS:
            raise KeyError(f"unnamed refusal {name!r}; add it to REFUSALS with its meaning")
        self.name = name
        self.detail = detail
        self.stage = stage
        self.exit_code = exit_code
        super().__init__(f"{name}: {detail}")

    def render(self) -> str:
        return (f"{TOOL} REFUSED [{self.stage}]: {self.name}: {self.detail} -- "
                f"{REFUSALS[self.name]}")


class Logger:
    """stdout for the human transcript, plus a list of refusals for the --json verdict."""

    def __init__(self) -> None:
        self.lines: list[str] = []
        self.refusals: list[dict[str, str]] = []

    def __call__(self, message: str = "") -> None:
        self.lines.append(message)
        print(message, flush=True)

    def refuse(self, refusal: Refusal) -> None:
        self.refusals.append(
            {"name": refusal.name, "stage": refusal.stage, "detail": refusal.detail,
             "meaning": REFUSALS[refusal.name]})
        print(refusal.render(), file=sys.stderr, flush=True)


def run_external(argv: Sequence[str], timeout: float, stage: str) -> subprocess.CompletedProcess:
    """One external call: both streams captured, hard timeout, never a shell, never ``2>&1``."""
    try:
        return subprocess.run(list(argv), capture_output=True, text=True,
                              timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("EXTERNAL-CALL-FAILED",
                      f"{argv[0]} timed out after {timeout}s: {(exc.stderr or '')[-400:]}",
                      stage=stage, exit_code=EXIT_REFUSED) from exc
    except OSError as exc:
        raise Refusal("EXTERNAL-CALL-FAILED", f"{argv[0]}: {exc}", stage=stage,
                      exit_code=EXIT_REFUSED) from exc


def resolve_game_dir(given: str) -> tuple[Path, str]:
    """The game install directory, and where it came from. Read once, explicitly."""
    if given:
        source = "--game-dir"
    else:
        env = os.environ.get(GAME_DIR_ENV, "").strip()
        if env:
            given = env
            source = f"${GAME_DIR_ENV}"
        else:
            given = DEFAULT_GAME_DIR
            source = f"the built-in default ({GAME_DIR_ENV} unset)"
    path = Path(given).expanduser()
    if not path.is_dir():
        raise Refusal("GAME-DIR-MISSING", f"{path} (from {source}) does not exist")
    return path.resolve(), source


def resolve_base_url(given: str) -> tuple[str, str]:
    """The server to poll, and WHERE the value came from. Read once, explicitly."""
    if given:
        url, source = given, "--base-url"
    elif os.environ.get(BASE_URL_ENV, "").strip():
        url, source = os.environ[BASE_URL_ENV].strip(), f"${BASE_URL_ENV}"
    else:
        url, source = DEFAULT_BASE_URL, f"the built-in default ({BASE_URL_ENV} unset)"
    return url, source


def check_game_lock(game_dir: Path, session: str) -> None:
    """Check the game lock. Another live session's lock refuses the restart."""
    lock_tool = Path(__file__).resolve().parent / "game_lock.py"
    if not lock_tool.exists():
        raise Refusal("GAME-LOCK-UNAVAILABLE", f"{lock_tool} not found")
    argv = [sys.executable, str(lock_tool), "--status", "--game-dir", str(game_dir)]
    if session:
        argv += ["--session", session]
    result = run_external(argv, timeout=30, stage="game-lock")
    if result.returncode != 0:
        raise Refusal("GAME-LOCK-HELD",
                      f"game install {game_dir} is held by another live session "
                      f"(exit {result.returncode}; see above). Restart with that session's "
                      f"--session, or wait for it to release.")


def probe_health(base_url: str, timeout: float) -> tuple[dict | None, str | None]:
    """The real health document, or ``(None, why-not)``. Both are returned."""
    url = f"{base_url.rstrip('/')}/health"
    request = urllib.request.Request(url, headers={"User-Agent": "FusionRpg-restart-game/1.0"})
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


def enumerate_game_processes(game_dir: Path, timeout: float) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Enumerate PlantsVsZombiesRH processes, split into own-install and other-install.

    Returns ``(own, other)`` where ``own`` is the list of processes from this install and ``other`` is
    the list of processes from other installs. A failure to enumerate REFUSES; the original swallowed
    it and reported "no game running", which is exactly the wrong answer for a liveness check.
    """
    prefix = os.path.normcase(str(game_dir))
    for host in ("pwsh", "powershell"):
        executable = shutil.which(host)
        if executable is None:
            continue
        result = run_external([executable, "-NoProfile", "-NonInteractive", "-Command",
                               _PROCESS_ENUM_SCRIPT], timeout=timeout, stage="process-enum")
        if result.returncode != 0:
            raise Refusal("PROCESS-ENUM-FAILED",
                          f"{host} exited {result.returncode} while enumerating processes: "
                          f"{(result.stdout + result.stderr)[-600:]}",
                          stage="process-enum", exit_code=EXIT_REFUSED)
        if "#RESTART-GAME-ENUM-COMPLETE" not in result.stdout:
            raise Refusal("PROCESS-ENUM-FAILED",
                          f"{host} did not reach the end of the enumeration (no sentinel in "
                          f"{len(result.stdout)} bytes of output); an empty answer here would read "
                          f"as 'no game running'",
                          stage="process-enum", exit_code=EXIT_REFUSED)
        own: list[dict[str, Any]] = []
        other: list[dict[str, Any]] = []
        for line in result.stdout.splitlines():
            if "\t" not in line:
                continue
            pid_text, _, image = line.partition("\t")
            image = image.strip()
            if not image:
                continue
            try:
                pid = int(pid_text)
            except ValueError:
                continue
            if os.path.normcase(image).startswith(prefix):
                own.append({"pid": pid, "image": image})
            else:
                other.append({"pid": pid, "image": image})
        return own, other
    raise Refusal("PROCESS-ENUM-FAILED",
                  "neither pwsh nor powershell is available to enumerate processes, so the game "
                  "process cannot be identified",
                  stage="process-enum", exit_code=EXIT_REFUSED)


def kill_processes(processes: list[dict[str, Any]], timeout: float) -> None:
    """Kill the given processes by PID. A failure to kill REFUSES."""
    for proc in processes:
        result = run_external(["taskkill", "/PID", str(proc["pid"]), "/F"],
                              timeout=timeout, stage="process-kill")
        if result.returncode != 0:
            raise Refusal("PROCESS-KILL-FAILED",
                          f"could not kill PID {proc['pid']} ({proc['image']}): "
                          f"{(result.stdout + result.stderr)[-400:]}",
                          stage="process-kill", exit_code=EXIT_REFUSED)


def launch_game(game_exe: Path, game_dir: Path, base_url: str) -> None:
    """Launch the game with FUSIONRPG_SERVER_URL set to the base URL."""
    env = os.environ.copy()
    env["FUSIONRPG_SERVER_URL"] = base_url
    try:
        subprocess.Popen([str(game_exe)], cwd=str(game_dir), env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         stdin=subprocess.DEVNULL, close_fds=True)
    except OSError as exc:
        raise Refusal("EXTERNAL-CALL-FAILED", f"could not launch {game_exe}: {exc}",
                      stage="launch", exit_code=EXIT_REFUSED) from exc


def ladder_reason(health: dict | None, game_up: bool, is_fresh_heartbeat: bool) -> str:
    """The same five-step ladder, in the same order, as a closed value."""
    if not game_up:
        return REASON_NO_GAME
    if health is None:
        return REASON_NO_HEALTH
    if not health.get("ok"):
        return REASON_NOT_OK
    if not health.get("injectorConnected"):
        return REASON_NOT_CONNECTED
    if not is_fresh_heartbeat:
        return REASON_STALE_HEARTBEAT
    return REASON_READY


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog=f"scripts/{TOOL}.py",
        description="Close and relaunch the game process, then poll real ground truth until the "
                    "injector reconnects (replaces restart-game.ps1).",
        epilog="usage: restart_game.py [--timeout-sec 120] [--interval-sec 3] "
               "[--game-dir <dir>] [--base-url <url>] [--session <id>] [--json]\n"
               f"game dir from --game-dir or ${GAME_DIR_ENV} or the built-in default; "
               f"base URL from --base-url or ${BASE_URL_ENV} or the built-in default",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--timeout-sec", type=int, default=DEFAULT_TIMEOUT_SEC,
                        help=f"max seconds to wait for the relaunched game's injector to reconnect "
                             f"(default {DEFAULT_TIMEOUT_SEC})")
    parser.add_argument("--interval-sec", type=int, default=DEFAULT_INTERVAL_SEC,
                        help=f"seconds between polls (default {DEFAULT_INTERVAL_SEC})")
    parser.add_argument("--game-dir", default="",
                        help=f"the game install dir (default: ${GAME_DIR_ENV}, else "
                             f"{DEFAULT_GAME_DIR})")
    parser.add_argument("--base-url", default="",
                        help=f"the server base URL to poll (default: ${BASE_URL_ENV}, else "
                             f"{DEFAULT_BASE_URL})")
    parser.add_argument("--session", default="",
                        help="this session id (tasks/sessions/<id>.json); the target install's game "
                             "lock is checked")
    parser.add_argument("--json", dest="json", action="store_true",
                        help="also print the machine-readable verdict")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    log = Logger()

    if args.timeout_sec <= 0:
        refusal = Refusal("INVALID-TIMEOUT", f"--timeout-sec {args.timeout_sec} must be positive")
        log.refuse(refusal)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.name,
                              "detail": refusal.detail, "exitCode": refusal.exit_code,
                              "refusals": log.refusals, "transcript": log.lines}, indent=2),
                  flush=True)
        return refusal.exit_code
    if args.interval_sec <= 0:
        refusal = Refusal("INVALID-INTERVAL", f"--interval-sec {args.interval_sec} must be positive")
        log.refuse(refusal)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.name,
                              "detail": refusal.detail, "exitCode": refusal.exit_code,
                              "refusals": log.refusals, "transcript": log.lines}, indent=2),
                  flush=True)
        return refusal.exit_code

    try:
        game_dir, game_dir_source = resolve_game_dir(args.game_dir)
        game_exe = game_dir / f"{GAME_PROCESS}.exe"
        if not game_exe.is_file():
            raise Refusal("GAME-EXE-MISSING", f"{game_exe} not found")

        base_url, base_url_source = resolve_base_url(args.base_url)
        if not base_url.lower().startswith(("http://", "https://")):
            raise Refusal("BASE-URL-INVALID",
                          f"the base URL {base_url!r} (from {base_url_source}) is not an http(s) URL")

        check_game_lock(game_dir, args.session)

        # Pre-restart health snapshot
        before_health, _ = probe_health(base_url, DEFAULT_REQUEST_TIMEOUT)
        before_heartbeat = before_health.get("lastHeartbeatUtc") if before_health else None

        # Enumerate and kill own processes
        own, other = enumerate_game_processes(game_dir, 30)
        if own:
            log(f"==> Closing {GAME_PROCESS} from {game_dir} (pid {', '.join(str(p['pid']) for p in own)})")
            kill_processes(own, 30)
            time.sleep(2)
        else:
            log(f"==> Game not currently running from {game_dir}")
        if other:
            log(f"==> Left alone (other installs, pid {', '.join(str(p['pid']) for p in other)}) "
                f"-- close those only from their own session")

        # Launch
        log(f"==> Launching {game_exe}")
        launch_game(game_exe, game_dir, base_url)

        # Poll loop
        started = time.monotonic()
        deadline = started + args.timeout_sec
        attempt = 0
        health: dict | None = None
        health_error: str | None = None
        game_up = False
        is_fresh_heartbeat = False
        reason = REASON_NO_GAME

        while True:
            attempt += 1
            health, health_error = probe_health(base_url, DEFAULT_REQUEST_TIMEOUT)
            own, _ = enumerate_game_processes(game_dir, 30)
            game_up = len(own) > 0
            is_fresh_heartbeat = (health is not None
                                   and health.get("lastHeartbeatUtc")
                                   and health.get("lastHeartbeatUtc") != before_heartbeat)
            reason = ladder_reason(health, game_up, is_fresh_heartbeat)
            if reason == REASON_READY:
                break
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            log(f"==> [{attempt}] not ready yet ({reason})"
                + (f" -- {health_error}" if health is None and health_error else "")
                + f" -- retrying in {min(args.interval_sec, max(0.0, remaining)):.0f}s")
            time.sleep(min(args.interval_sec, max(0.0, remaining)))

        elapsed = round(time.monotonic() - started, 3)
        ready = reason == REASON_READY
        if ready:
            log(f"==> Game relaunched and injector reconnected after {attempt} check(s) "
                f"(~{elapsed}s):")
            log(json.dumps(health, indent=2))
        else:
            log(f"==> TIMEOUT after {args.timeout_sec}s -- game did not reach a confirmed "
                f"fresh-connected state (last reason: {reason}, {attempt} attempt(s)).")

        envelope: dict[str, Any] = {
            "tool": TOOL_ID,
            "verdict": "READY" if ready else "TIMEOUT",
            "exitCode": EXIT_OK if ready else EXIT_TIMEOUT,
            "ready": ready,
            "reason": reason,
            "attempts": attempt,
            "health": health,
            "healthError": health_error,
            "gameUp": game_up,
            "isFreshHeartbeat": is_fresh_heartbeat,
            "seconds": elapsed,
            "baseUrl": base_url,
            "baseUrlSource": base_url_source,
            "gameDir": str(game_dir),
            "gameDirSource": game_dir_source,
            "gameExe": str(game_exe),
            "session": args.session or None,
            "timeoutSec": args.timeout_sec,
            "intervalSec": args.interval_sec,
            "reasons": sorted(REASONS),
            "killedPids": [p["pid"] for p in own] if ready else [],
            "leftAlonePids": [p["pid"] for p in other],
            "beforeHeartbeat": before_heartbeat,
            "refusals": log.refusals,
            "transcript": log.lines,
        }
        if args.json:
            print(json.dumps(envelope, indent=2), flush=True)
        return envelope["exitCode"]

    except Refusal as refusal:
        log.refuse(refusal)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.name,
                              "detail": refusal.detail, "exitCode": refusal.exit_code,
                              "refusals": log.refusals, "transcript": log.lines}, indent=2),
                  flush=True)
        return refusal.exit_code


if __name__ == "__main__":
    sys.exit(main())
