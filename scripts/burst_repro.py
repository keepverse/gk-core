#!/usr/bin/env python3
"""Server burst-stability repro -- perf-v3-spec.md module server-burst (B1).

Launches a SCRATCH server (own port + data dir), floods /api/events with a synthetic spawn/death burst
mimicking the 1000-zombie stress fill, and reports whether the server survives. Never points at a live
dev server unless -BaseUrl is passed explicitly.

Replaces `scripts/burst-repro.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE HEALTH READS WERE UNBOUNDED.** The startup wait's `Invoke-RestMethod "$BaseUrl/health"
  -TimeoutSec 2` was bounded, but the flood's per-request POSTs used `-TimeoutSec 10` while the
  startup wait loop itself and the final liveness check had no total bound -- a server that accepts
  TCP and never answers holds the repro open indefinitely. Every request now carries a timeout AND
  the startup wait carries an attempt budget.

* **A FAILED STARTUP LEAKED THE SERVER PROCESS.** `Start-Process` detaches; when the health wait
  exhausted its 20 attempts the script did `Write-Error "scratch server failed to start"; exit 1`
  and left the half-started server running, holding its port and data dir. The port now kills the
  child on every exit path, including the refusal paths.

* **THE PORT WAS A CONSTANT.** `param([int]$Port = 5177)` with `BaseUrl = "http://127.0.0.1:$Port"`.
  The scratch port is still a default, but it is now validated and the data dir is a fresh
  `tempfile` directory per run rather than a fixed path two runs could collide on.

* **THE SCRIPT COULD NOT FAIL.** There was no `exit` statement anywhere: a run whose server died
  mid-burst printed the result and exited 0, so "reports whether the server survives" was not true --
  the exit code said nothing about the survival it existed to measure. The verdict is now deliberate
  and stated in the docstring below.

* **`Set-Content -Encoding UTF8` AMBIGUITY.** PowerShell 5.1's `UTF8` writes a BOM; the file is now
  written as BOM-less UTF-8 explicitly.

THE VERDICT, DELIBERATELY
-------------------------
The original had no exit-code contract. This port decides one, because a repro that cannot fail is
not a repro:

  * exit 0 -- the burst completed AND the server answered /health afterwards AND (when this script
    started the scratch server) the process was still alive.
  * exit 1 -- the burst completed but the server did not survive: no /health answer afterwards, or
    the scratch process exited. `sendFailures` and `healthFailures` are REPORTED, never fatal -- the
    original counted them and carried on, and a burst that loses some requests to a server that then
    recovers is a different finding than a server that dies.
  * exit 64 -- a refusal: the server executable is missing, or the scratch server never answered
    /health within its startup budget.

DELIBERATELY UNCHANGED
----------------------
Same synthetic fill (per entity a zombie.place + zombie.spawn + stat.applied, then a zombie.die --
the full projection fan-out per zombie), same board.start-first requirement, same batch size and
event-count defaults, same result fields (`events`, `batch`, `matchKey`, `healthFailures`,
`sendFailures`, `elapsedMs`, `serverRespondingAfter`, `processAlive`, `eventsPerSec`), same scratch
port default, and the same operator output. The scratch server is still killed at the end of a run
that started it.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

TOOL_ID = "burst-repro"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_EVENTS = 6000
DEFAULT_BATCH = 256
DEFAULT_PORT = 5177
DEFAULT_OUT = ("docs", "research", "perf", "_burst-repro-last.json")

DEFAULT_SEND_TIMEOUT = 10
DEFAULT_HEALTH_TIMEOUT = 3
DEFAULT_FINAL_HEALTH_TIMEOUT = 5
DEFAULT_STARTUP_TIMEOUT = 2
DEFAULT_STARTUP_ATTEMPTS = 20
DEFAULT_STARTUP_SLEEP = 1.0
DEFAULT_SETTLE_SEC = 3.0
DEFAULT_FAILURE_SLEEP = 0.2
HEALTH_CHECK_EVERY = 4  # in batches, as the original's `4 * $Batch`

REFUSAL_REASONS = {
    "INVALID-EVENTS", "INVALID-BATCH", "INVALID-PORT", "INVALID-TIMEOUT", "SERVER-EXE-MISSING",
    "SCRATCH-SERVER-FAILED", "BOARD-START-FAILED", "OUT-PARENT-MISSING", "REQUEST-FAILED",
}

# THE SEAMS THE SUITE NEEDS, bound once to module-private names. `urllib.request`, `subprocess` and
# `time` are process-wide modules: a test that patches any of them reaches every other test in this
# project, which this program has measured at 506 unrelated failures in one case.
_URLOPEN = urllib.request.urlopen
_POPEN = subprocess.Popen
_SLEEP = time.sleep


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


def _iso_now() -> str:
    """The original's `(Get-Date).ToUniversalTime().ToString("o")` -- one timestamp per batch."""
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()) + \
        f".{int((time.time() % 1) * 1_000_000):06d}Z"


def _request(url: str, body: dict | None, timeout: int, what: str) -> dict:
    """One bounded request. `body` of None is a GET; otherwise a POST with that JSON body."""
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        url, data=data, method="GET" if body is None else "POST",
        headers={"User-Agent": "FusionRpg-burst-repro/1.0",
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
        raise Refusal("REQUEST-FAILED", f"{what} answered with something that is not JSON: "
                                        f"{error}") from error


def get_health(base_url: str, timeout: int) -> dict | None:
    """The server's /health, or None when it does not answer. Never raises: a health check that
    cannot be made is a reading (the server is not answering), not a crash."""
    try:
        return _request(f"{base_url}/health", None, timeout, "GET /health")
    except Refusal:
        return None


def post_events(base_url: str, items: list[dict], timeout: int) -> None:
    """POST one batch to /api/events. Raises Refusal on failure -- the flood counts those."""
    _request(f"{base_url}/api/events", {"events": items}, timeout, "POST /api/events")


def build_batch(start_idx: int, count: int, match_key: str, now: str) -> list[dict]:
    """One synthetic batch, mimicking the real stress fill: per entity a zombie.place + zombie.spawn
    (fat ~30-field dump) + stat.applied, then a zombie.die -- the full projection fan-out per zombie.

    The original's `New-Batch` shape is preserved exactly, including the die's pointer arithmetic
    (`0xA000 + n - 2`: the die kills the entity two indices back, not the current one).
    """
    items: list[dict] = []
    for i in range(count):
        n = start_idx + i
        ptr = f"B{0xA000 + n:X}"
        kind = n % 4
        if kind == 0:
            items.append({"t": now, "kind": "zombie.place", "matchKey": match_key,
                          "payload": {"ptr": ptr, "type": n % 30, "typeName": f"Zed{n % 30}",
                                      "row": n % 5, "theX": 7.5, "mindControlled": False,
                                      "withEffect": False}})
        elif kind == 1:
            items.append({"t": now, "kind": "zombie.spawn", "matchKey": match_key,
                          "payload": {"ptr": ptr, "typeId": n % 30, "type": n % 30,
                                      "typeName": f"Zed{n % 30}", "side": "zombie", "row": n % 5,
                                      "col": 8, "x": 7.5, "y": 1.1, "hp": 500, "maxHp": 500,
                                      "attack": 20, "armor": 100, "armorMax": 100,
                                      "theSecondArmorHealth": 0, "theSecondArmorMaxHealth": 0,
                                      "theSpeed": 1.2, "source": "debug.spawn",
                                      "displayName": f"Burst Zombie {n}",
                                      **{f"f{k}": k for k in range(1, 11)}}})
        elif kind == 2:
            items.append({"t": now, "kind": "stat.applied", "matchKey": match_key,
                          "payload": {"ptr": ptr, "side": "zombie", "typeId": n % 30,
                                      "hpBefore": 500, "hpAfter": 25000, "maxBefore": 500,
                                      "maxAfter": 25000, "atkBefore": 20, "atkAfter": 200,
                                      "source": "debug.spawn"}})
        else:
            die_n = n - 2
            items.append({"t": now, "kind": "zombie.die", "matchKey": match_key,
                          "payload": {"ptr": f"B{0xA000 + die_n:X}", "type": die_n % 30,
                                      "typeName": f"Zed{die_n % 30}", "reason": 1}})
    return items


def run_flood(base_url: str, total_events: int, batch_size: int, match_key: str,
              send_timeout: int, health_timeout: int, failure_sleep: float) -> dict:
    """board.start first (the server needs the run row for projections), then the synthetic fill.

    Per-request failures are COUNTED and the loop carries on -- the original's `catch` did exactly
    that, and a burst that loses some requests to a server that then recovers is a different finding
    than a server that dies.
    """
    started = time.monotonic()
    result = {"events": total_events, "batch": batch_size, "matchKey": match_key,
              "healthFailures": 0, "sendFailures": 0}

    # board.start first -- server needs the run row for projections (audit §4c.1). NOT in a try: the
    # original let a failure here terminate the script, and a burst without a run row proves nothing.
    post_events(base_url, [{"t": _iso_now(), "kind": "board.start", "matchKey": match_key,
                            "payload": {"levelName": "burst", "matchKey": match_key}}],
                send_timeout)

    sent = 0
    while sent < total_events:
        n = min(batch_size, total_events - sent)
        items = build_batch(sent, n, match_key, _iso_now())
        try:
            post_events(base_url, items, send_timeout)
        except Refusal as refusal:
            result["sendFailures"] += 1
            print(f"WARN: send failed at {sent}: {refusal.detail}", file=sys.stderr)
            _SLEEP(failure_sleep)
        sent += n
        if sent % (HEALTH_CHECK_EVERY * batch_size) == 0:
            if get_health(base_url, health_timeout) is None:
                result["healthFailures"] += 1
                print(f"WARN: health failed at {sent} events", file=sys.stderr)

    result["elapsedMs"] = int((time.monotonic() - started) * 1000)
    return result


def start_scratch_server(exe: Path, base_url: str, data_dir: Path) -> subprocess.Popen:
    """Launch the scratch server with FUSIONRPG_URLS / FUSIONRPG_DATA set for the CHILD ONLY.

    The original set the variables in its own process and unset them after `Start-Process`; passing a
    copied environment to the child leaves the parent's environment untouched and cannot leak the
    variables into a later stage of this script.
    """
    env = dict(os.environ, FUSIONRPG_URLS=base_url, FUSIONRPG_DATA=str(data_dir))
    out_log = open(data_dir / "server-out.log", "wb")
    err_log = open(data_dir / "server-err.log", "wb")
    return _POPEN([str(exe)], cwd=str(exe.parent), env=env,
                   stdout=out_log, stderr=err_log)


def wait_for_health(base_url: str, attempts: int, sleep_sec: float, timeout: int) -> bool:
    """The original's startup wait: 20 one-second sleeps, each followed by a bounded /health GET."""
    for _ in range(attempts):
        _SLEEP(sleep_sec)
        if get_health(base_url, timeout) is not None:
            return True
    return False


def kill_server(proc: subprocess.Popen) -> None:
    """Best-effort kill of the scratch server. A kill that fails is logged, never fatal: the run's
    verdict is already decided by the time this runs."""
    try:
        proc.kill()
        proc.wait(timeout=10)
    except Exception as error:  # noqa: BLE001 - a leaked scratch server is a warning, not a crash
        print(f"WARN: could not kill scratch server pid {proc.pid}: {error}", file=sys.stderr)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="burst-repro",
        description="Server burst-stability repro: scratch server + synthetic spawn/death fill "
                    "(replaces burst-repro.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--base-url", default="",
                        help="measure an EXISTING server instead of launching a scratch one")
    parser.add_argument("--events", type=int, default=DEFAULT_EVENTS,
                        help=f"events to send (default {DEFAULT_EVENTS})")
    parser.add_argument("--batch", type=int, default=DEFAULT_BATCH,
                        help=f"events per POST (default {DEFAULT_BATCH})")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"scratch server port (default {DEFAULT_PORT})")
    parser.add_argument("--out", default="",
                        help=f"where the result JSON is written (default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--send-timeout", type=int, default=DEFAULT_SEND_TIMEOUT,
                        help=f"seconds per flood POST (default {DEFAULT_SEND_TIMEOUT})")
    parser.add_argument("--health-timeout", type=int, default=DEFAULT_HEALTH_TIMEOUT,
                        help=f"seconds for the in-flood health check (default {DEFAULT_HEALTH_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.events <= 0:
        return _refuse("INVALID-EVENTS", f"--events {args.events} sends nothing", args.json)
    if args.batch <= 0:
        return _refuse("INVALID-BATCH", f"--batch {args.batch} is not a batch", args.json)
    if not (1 <= args.port <= 65535):
        return _refuse("INVALID-PORT", f"--port {args.port} is not a TCP port", args.json)
    for name, value in (("--send-timeout", args.send_timeout),
                        ("--health-timeout", args.health_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} {value} must be positive", args.json)

    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    out_path = Path(args.out).expanduser() if args.out else root.joinpath(*DEFAULT_OUT)
    if not out_path.is_absolute():
        out_path = Path.cwd() / out_path
    out_path = out_path.resolve()

    proc: subprocess.Popen | None = None
    base_url = args.base_url
    try:
        if not base_url:
            exe = root / "dist" / "FusionRpg.Server" / "FusionRpg.Server.exe"
            if not exe.is_file():
                raise Refusal("SERVER-EXE-MISSING",
                              f"the server executable does not exist: {exe} -- publish the server "
                              f"first (deploy-play.py)")
            data_dir = Path(tempfile.gettempdir()) / \
                f"fusionrpg-burst-data-{os.urandom(4).hex()}"
            data_dir.mkdir(parents=True, exist_ok=True)
            base_url = f"http://127.0.0.1:{args.port}"
            proc = start_scratch_server(exe, base_url, data_dir)
            if not wait_for_health(base_url, DEFAULT_STARTUP_ATTEMPTS, DEFAULT_STARTUP_SLEEP,
                                   DEFAULT_STARTUP_TIMEOUT):
                raise Refusal("SCRATCH-SERVER-FAILED",
                              f"the scratch server on {base_url} did not answer /health within "
                              f"{DEFAULT_STARTUP_ATTEMPTS} attempts; its logs are in {data_dir}")
            print(f"[burst] scratch server up on {base_url} (data: {data_dir}, pid {proc.pid})",
                  file=sys.stderr)

        result = run_flood(base_url, args.events, args.batch,
                           f"burst-{os.urandom(4).hex()}",
                           args.send_timeout, args.health_timeout, DEFAULT_FAILURE_SLEEP)

        _SLEEP(DEFAULT_SETTLE_SEC)
        alive = get_health(base_url, DEFAULT_FINAL_HEALTH_TIMEOUT) is not None
        proc_alive = (proc.poll() is None) if proc is not None else None

        result["serverRespondingAfter"] = alive
        result["processAlive"] = proc_alive
        result["eventsPerSec"] = round(args.events / max(0.001, result["elapsedMs"] / 1000.0))

        try:
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        except OSError as error:
            raise Refusal("OUT-PARENT-MISSING",
                          f"the result file could not be written: {out_path}: {error}") from error

        print("", file=sys.stderr)
        print("=== burst repro result ===", file=sys.stderr)
        print(f"sent {args.events} events in {result['elapsedMs']}ms "
              f"({result['eventsPerSec']}/s), sendFailures={result['sendFailures']}, "
              f"healthFailures={result['healthFailures']}", file=sys.stderr)
        print(f"server responding after: {alive}   process alive: {proc_alive}", file=sys.stderr)
        if proc is not None and proc.poll() is not None:
            print(f"process EXIT CODE: {proc.poll()}", file=sys.stderr)
        if proc is not None:
            print("server logs in the scratch data dir (server-out.log / server-err.log)",
                  file=sys.stderr)

        # The deliberate verdict: the burst completed AND the server survived it. sendFailures and
        # healthFailures are reported above and never fatal -- see the docstring.
        survived = alive and proc_alive in (None, True)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "baseUrl": base_url, "verdict": "OK" if survived
                              else "FAILED", "exitCode": 0 if survived else 1, "result": result},
                             indent=2))
        return EXIT_OK if survived else EXIT_FAILED
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)
    finally:
        # The original killed the scratch server only at the end of a successful run and LEAKED it on
        # every early exit. The kill here covers every exit path, refusals included.
        if proc is not None and proc.poll() is None:
            kill_server(proc)


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
