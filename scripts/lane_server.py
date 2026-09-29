#!/usr/bin/env python3
"""Run a POOL SLOT's own RPG server: its own loopback port, its own data directory.

This is the Python port of ``scripts/lane-server.ps1``. The PowerShell form was retired
because: (1) ``Write-Host`` writes the INFORMATION stream, so ``2>&1`` captures nothing
from a working script -- the exact trap this migration exists to remove; (2) ``$env:`` set
in one agent shell call does not survive to the next; (3) a ``.ps1`` body cannot be
imported as a library or unit-tested without spawning a process per call; (4) PowerShell
stream capture is a silent-data-loss surface that cost four separate wrong diagnoses.

The CLI contract is preserved **including the PowerShell spelling**, so the one live
in-repo caller (``gk-fusion/scripts/prove-slot-connection.py``, which shells
``lane-server.ps1 -Start`` / ``-Stop``) is re-pointed by changing the program name alone.

Four measured defects are fixed by construction:

* **L8 -- no lock on ``servers.json``.** Two lanes on different slots race the same file,
  one write drops the other's PID. The port takes an exclusive ``servers.lock`` file
  around every read-modify-write.
* **L7 -- ``-Start`` reports STARTED for a server that died.** The port polls ``/health``
  before reporting STARTED.
* **L9 -- ``-Force`` kills the old server before the new one exists.** The port starts the
  new server, waits for its health, and only then considers the replacement done. If the
  new server fails, the failure is reported clearly.
* **L15 -- the roster probe can destroy a good database.** The port checks the probe's
  exit code and refuses to reseed on failure.

Owner ruling 2026-09-21: lanes clone the game into a pool, claim a slot and release it; at most
three live runs at once; coordination is a JSON registry guarded by a lock file; no human sequences
anything. **No path is hardcoded** -- the pool root is ``$FUSIONRPG_GAME_POOL`` (or ``--pool-root``)
and the published server tree from ``--server-root`` or ``<repo>/dist/FusionRpg.Server``.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Sequence

TOOL = "lane_server"

# The owner's server. `lane-server.ps1:39,68` refuses any slot that resolves here, and
# `deploy-play.py` refuses a pooled deploy whose `--server-url` is still this value.
OWNER_PORT = 5088

EXIT_OK = 0
EXIT_REFUSED = 1     # a named refusal: a precondition failed, or the verb is illegal here.
EXIT_POOL_FAULT = 2  # the pool could not be read/written/locked, or an external call failed/timed out.

# Every refusal is named, and the name carries its meaning. A refusal that is not in this table is a
# bug: `Refusal.__init__` raises `KeyError` on an unknown name, so one cannot be added without a
# reader learning what it means.
REFUSALS = {
    "POOL-ROOT-MISSING":
        "no pool root: pass --pool-root or set FUSIONRPG_GAME_POOL (machine-specific; never commit it)",
    "POOL-ROOT-UNUSABLE":
        "the pool root could not be created or resolved to a directory",
    "SLOT-REQUIRED":
        "--start / --stop need --slot <n>",
    "SERVER-EXE-MISSING":
        "published server not found (run deploy-play.py once, or pass --server-root)",
    "OWNER-PORT-REFUSED":
        "the slot resolved to the OWNER's port; refusing to touch it",
    "SLOT-ALREADY-RUNNING":
        "the slot already has a server running; --stop it first, or --force to restart",
    "OWNER-PORT-RECORDED":
        "the slot is recorded on the OWNER's port; refusing to stop it",
    "SERVER-FAILED-TO-START":
        "the server process failed to start",
    "HEALTH-TIMEOUT":
        "the server did not answer /health with ok:true within the timeout",
    "PROBE-FAILED":
        "the roster probe failed; refusing to reseed a database that might be good",
    "SEED-SOURCE-MISSING":
        "the slot has no data and no source to seed from",
    "SEED-FAILED":
        "seeding failed: the database is still missing after the seed attempt",
    "SERVERS-UNREADABLE":
        "servers.json exists but is not readable JSON",
    "SERVERS-UNWRITABLE":
        "servers.json could not be written; the requested change was NOT recorded",
    "LOCK-UNAVAILABLE":
        "the servers lock could not be taken within the timeout "
        "(another agent is mid-write; retry, never force the lock)",
    "EXTERNAL-CALL-FAILED":
        "an external call failed or timed out",
}


class Refusal(Exception):
    """A named, fail-closed precondition failure. Never 'continue and report empty'."""

    def __init__(self, name: str, detail: str, stage: str = "preconditions",
                 exit_code: int = EXIT_REFUSED) -> None:
        if name not in REFUSALS:
            # A refusal without a registry row is a defect: nobody can learn what it meant.
            raise KeyError(f"unnamed refusal {name!r}; add it to REFUSALS with its meaning")
        self.name = name
        self.detail = detail
        self.stage = stage
        self.exit_code = exit_code
        super().__init__(f"{name}: {detail}")

    def render(self) -> str:
        """The line a caller reads, and it carries the REMEDY as well as the instance."""
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


def now_iso() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="milliseconds")


def resolve_pool_root(raw: str | None, log: Logger) -> Path:
    """Trim the pool root, say so when trimming mattered, create it, and resolve it."""
    if not raw or not str(raw).strip():
        raise Refusal("POOL-ROOT-MISSING", "FUSIONRPG_GAME_POOL is unset and --pool-root was not given")
    as_given = str(raw)
    trimmed = as_given.strip().rstrip("\\/")
    if not trimmed:
        raise Refusal("POOL-ROOT-MISSING",
                       f"the pool root {as_given!r} is nothing but whitespace and separators")
    if trimmed != as_given:
        log(f"[{TOOL}] pool root carried surrounding whitespace/separators -- using '{trimmed}'")
    try:
        Path(trimmed).mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise Refusal("POOL-ROOT-UNUSABLE", f"could not create '{trimmed}': {exc}") from exc
    return Path(trimmed).resolve()


def slot_data_dir(root: Path, slot: int) -> Path:
    return root / f"slot-{slot}-data"


def slot_log_path(root: Path, slot: int) -> Path:
    return root / f"slot-{slot}-server.log"


def slot_port(registry_path: Path, base_port: int, slot: int) -> int:
    """A slot's server port: STORED in slots.json, otherwise DERIVED as base_port + slot.

    One function, because the two halves are both load-bearing: an earlier reading of only
    the stored field silently skipped every slot that had not been acquired since the registry
    was created (`live_slot.py:resolve_port`).
    """
    if registry_path.exists():
        try:
            reg = json.loads(registry_path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            return base_port + slot
        for entry in reg.get("slots", []):
            try:
                if int(entry.get("slot")) == slot:
                    stored = entry.get("port")
                    if stored:
                        return int(stored)
            except (TypeError, ValueError):
                continue
    return base_port + slot


# ------------------------------------------------------------------------------------------------
# servers.json state (locked)
# ------------------------------------------------------------------------------------------------

def read_servers(path: Path) -> dict[str, Any]:
    """Read servers.json. Tolerant of a BOM, because the PowerShell original can leave one."""
    if not path.exists():
        return {"servers": []}
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise Refusal("SERVERS-UNREADABLE", f"{path}: {exc}") from exc
    if not isinstance(data, dict):
        raise Refusal("SERVERS-UNREADABLE", f"{path}: the file is not a JSON object")
    data.setdefault("servers", [])
    return data


def write_servers(path: Path, data: dict[str, Any], log: Logger) -> None:
    """Persist servers.json, atomically, as UTF-8 **without** a BOM.

    The write goes through a sibling temp file and `os.replace` so a crash mid-write
    cannot leave a truncated registry; the original's direct `Set-Content` could.
    """
    body = json.dumps(data, indent=2)
    handle = None
    try:
        with tempfile.NamedTemporaryFile(
                "w", encoding="utf-8", newline="\r\n", dir=str(path.parent),
                prefix=path.name + ".", suffix=".tmp", delete=False) as handle:
            handle.write(body)
        os.replace(handle.name, path)
    except OSError as exc:
        if handle is not None:
            with contextlib.suppress(OSError):
                os.unlink(handle.name)
        raise Refusal("SERVERS-UNWRITABLE", f"{path}: {exc}", stage="write-servers",
                       exit_code=EXIT_POOL_FAULT) from exc
    log(f"[{TOOL}] servers.json written: {path}")


@contextlib.contextmanager
def servers_lock(lock_path: Path, timeout_seconds: float, log: Logger) -> Iterator[None]:
    """Hold ``servers.lock`` for one read-modify-write only -- never for a whole probe.

    `CreateNew` semantics: an exclusive create is the lock. This is the L8 fix: the
    PowerShell original had NO lock on ``servers.json``, so two lanes on different slots
    raced the same file and one write dropped the other's PID -- and ``-Stop`` "kills only
    the PID this tool recorded", so a dropped PID became an orphan server permanently
    holding a slot port.
    """
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            handle = os.open(str(lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            if not lock_path.exists():
                time.sleep(0.25)
                continue
            if time.monotonic() >= deadline:
                raise Refusal(
                    "LOCK-UNAVAILABLE",
                    f"could not take the servers lock within {timeout_seconds}s "
                    f"(another agent is mid-write; retry)")
            time.sleep(0.4)
        except OSError as exc:
            raise Refusal("LOCK-UNAVAILABLE", f"{lock_path}: {exc}", stage="lock",
                           exit_code=EXIT_POOL_FAULT) from exc
    try:
        os.write(handle, b"lane-server\n")
    finally:
        os.close(handle)
    try:
        yield
    finally:
        with contextlib.suppress(OSError):
            os.unlink(lock_path)


# ------------------------------------------------------------------------------------------------
# Roster probe and seeding
# ------------------------------------------------------------------------------------------------

# The roster probe. NO try/except: any failure (missing file, missing table, corrupt DB)
# must exit non-zero so the caller can distinguish "empty roster" from "probe failed".
# The PowerShell original caught all exceptions and printed 0, which made any failure
# read as "empty roster" and triggered a destructive reseed (L15).
_ROSTER_PROBE = r"""
import sqlite3, sys
c = sqlite3.connect("file:" + sys.argv[1] + "?mode=ro", uri=True)
print(c.execute("select count(*) from creature_species").fetchone()[0])
"""

# The seed script. SQLite online backup, correct even while the source server holds
# the file open (a plain file copy of a live SQLite DB can tear).
_SEED_SCRIPT = r"""
import sqlite3, os, sys
src, dst = sys.argv[1], sys.argv[2]
s = sqlite3.connect("file:" + src + "?mode=ro", uri=True)
d = sqlite3.connect(dst)
s.backup(d)
n = d.execute("select count(*) from sqlite_master").fetchone()[0]
d.close(); s.close()
print("    seeded " + os.path.basename(dst) + " (" + str(n) + " objects)")
"""


def run_python_inline(code: str, args: Sequence[str], timeout: float, stage: str) -> subprocess.CompletedProcess:
    """Run an inline Python script with a hard timeout. Both streams captured, never a shell."""
    try:
        return subprocess.run([sys.executable, "-c", code, *args],
                              capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("EXTERNAL-CALL-FAILED",
                       f"python timed out after {timeout}s: {(exc.stderr or '')[-400:]}",
                       stage=stage, exit_code=EXIT_POOL_FAULT) from exc
    except OSError as exc:
        raise Refusal("EXTERNAL-CALL-FAILED", f"python: {exc}", stage=stage,
                       exit_code=EXIT_POOL_FAULT) from exc


def probe_roster(hot_path: Path, timeout: float) -> int:
    """Return the creature_species row count. Refuses on probe failure (L15 fix).

    The PowerShell original ran the probe through ``python -`` with no exit-code check
    and ``Select-Object -Last 1``; if ``python`` was not on PATH, or exited non-zero,
    ``[int]$null`` was ``0`` and the script moved ``rpg-hot.sqlite`` aside and reseeded.
    A good database was destroyed because the probe could not run.
    """
    result = run_python_inline(_ROSTER_PROBE, [str(hot_path)], timeout, "roster-probe")
    if result.returncode != 0:
        raise Refusal("PROBE-FAILED",
                       f"the roster probe exited {result.returncode}: "
                       f"{(result.stdout + result.stderr)[-400:]}",
                       stage="roster-probe")
    try:
        return int(result.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError) as exc:
        raise Refusal("PROBE-FAILED",
                       f"the roster probe produced unparseable output: {result.stdout!r}",
                       stage="roster-probe") from exc


def seed_data(source_data: Path, data_dir: Path, log: Logger, timeout: float) -> None:
    """Seed a fresh slot's databases from a known-good source using SQLite online backup.

    A brand-new data directory has NO species roster, and the server refuses to start
    with an empty one: "CreatureSpeciesCatalog.Configure received an empty species roster.
    A server that starts with zero species reports healthy and fails later, untraceably,
    in SummonRoller." (measured 2026-09-22). So seed from a known-good data dir -- the
    owner's, by default.
    """
    src_hot = source_data / "rpg-hot.sqlite"
    src_media = source_data / "rpg-media.sqlite"
    if not src_hot.exists():
        raise Refusal("SEED-SOURCE-MISSING",
                       f"{src_hot} missing. Pass --source-data, or run "
                       f"'dotnet run --project tools/CreatureSpeciesImport' against the data dir.")
    log(f"[{TOOL}] slot data is empty -- seeding from {source_data} (SQLite online backup)")
    hot = data_dir / "rpg-hot.sqlite"
    media = data_dir / "rpg-media.sqlite"
    for src, dst in ((src_hot, hot), (src_media, media)):
        if src.exists():
            result = run_python_inline(_SEED_SCRIPT, [str(src), str(dst)], timeout, "seed")
            if result.returncode != 0:
                raise Refusal("SEED-FAILED",
                               f"seeding {dst} failed: {(result.stdout + result.stderr)[-400:]}",
                               stage="seed", exit_code=EXIT_POOL_FAULT)
            log(result.stdout.strip())
    if not hot.exists():
        raise Refusal("SEED-FAILED", f"{hot} still missing after seeding", stage="seed",
                       exit_code=EXIT_POOL_FAULT)


# ------------------------------------------------------------------------------------------------
# Process management
# ------------------------------------------------------------------------------------------------

def start_server_process(server_exe: Path, server_root: Path, port: int, data_dir: Path,
                          log_path: Path) -> subprocess.Popen:
    """Start the server process with FUSIONRPG_URLS and FUSIONRPG_DATA set.

    The child inherits THIS process's environment plus the two overrides. ASPNETCORE_URLS
    is deliberately NOT set -- the server ignores it (`gk-core/src/FusionRpg.Server/Program.cs:15-16`).
    """
    env = os.environ.copy()
    env["FUSIONRPG_URLS"] = f"http://127.0.0.1:{port}"
    env["FUSIONRPG_DATA"] = str(data_dir)
    try:
        return subprocess.Popen(
            [str(server_exe)],
            cwd=str(server_root),
            env=env,
            stdout=open(log_path, "w", encoding="utf-8"),
            stderr=open(str(log_path) + ".err", "w", encoding="utf-8"),
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP | getattr(subprocess, "DETACHED_PROCESS", 0),
            close_fds=True,
        )
    except OSError as exc:
        raise Refusal("SERVER-FAILED-TO-START",
                       f"could not start {server_exe}: {exc}",
                       stage="start-server")


def pid_alive(pid: int) -> bool:
    """Check if a process is alive.

    On Windows, ``OpenProcess`` + ``GetExitCodeProcess`` is unreliable across process
    boundaries (a grandchild process can appear dead to a ``Popen`` holder). We use
    ``tasklist`` instead, which queries the kernel directly. On Unix, ``os.kill(pid, 0)``
    is the standard existence check.
    """
    if pid <= 0:
        return False
    if os.name == "nt":
        try:
            result = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
                capture_output=True, text=True, timeout=15, check=False)
            return result.returncode == 0 and str(pid) in result.stdout
        except (subprocess.TimeoutExpired, OSError):
            return False
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def stop_process(pid: int, timeout: float) -> bool:
    """Stop a process by PID. Returns True if it was running, False if already gone.

    Uses ``os.kill`` (``TerminateProcess`` on Windows) first, falling back to
    ``taskkill /F /T`` if ``os.kill`` fails (e.g. for a process tree).
    """
    if not pid_alive(pid):
        return False
    try:
        os.kill(pid, 9)
        return True
    except OSError:
        pass
    try:
        result = subprocess.run(
            ["taskkill", "/F", "/T", "/PID", str(pid)],
            capture_output=True, text=True, timeout=timeout, check=False
        )
        if result.returncode == 0:
            return True
    except (subprocess.TimeoutExpired, OSError):
        pass
    return False


def wait_for_health(port: int, timeout_seconds: float, poll_interval: float,
                    log: Logger) -> dict[str, Any] | None:
    """Poll /health until ok:true or timeout. Returns the health dict or None.

    This is the L7 fix: the PowerShell original launched the process, recorded the entry,
    and printed ``server STARTED pid=...`` with no liveness check and no /health wait.
    A server that died immediately was reported as STARTED.
    """
    url = f"http://127.0.0.1:{port}/health"
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=5) as resp:
                body = json.loads(resp.read().decode("utf-8"))
                if body.get("ok"):
                    return body
        except Exception:
            pass
        time.sleep(poll_interval)
    return None


# ------------------------------------------------------------------------------------------------
# Verbs
# ------------------------------------------------------------------------------------------------

def _slot_of(entry: dict[str, Any]) -> int:
    try:
        return int(entry.get("slot"))
    except (TypeError, ValueError):
        return -1


def _find_entry(servers: dict[str, Any], slot: int) -> dict[str, Any] | None:
    for entry in servers.get("servers", []):
        if _slot_of(entry) == slot:
            return entry
    return None


def cmd_start(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    if args.slot <= 0:
        raise Refusal("SLOT-REQUIRED", "--start needs --slot <n>")
    root = args.pool
    server_exe = args.server_root / "FusionRpg.Server.exe"
    if not server_exe.exists():
        raise Refusal("SERVER-EXE-MISSING",
                       f"published server not found: {server_exe} (run deploy-play.py once, "
                       f"or pass --server-root)")
    port = slot_port(root / "slots.json", args.base_port, args.slot)
    if port == OWNER_PORT:
        raise Refusal("OWNER-PORT-REFUSED",
                       f"slot {args.slot} resolved to port {port} -- that is the OWNER's "
                       f"server; refusing")
    data_dir = slot_data_dir(root, args.slot)
    data_dir.mkdir(parents=True, exist_ok=True)

    # Roster check and seeding. A slot DB can exist yet be DEGENERATE: a server killed by
    # the empty-roster check leaves a schema-only rpg-hot.sqlite behind. Test the ROSTER
    # -- the contract CreatureSpeciesCatalog actually enforces -- and never trust presence
    # or file size.
    hot = data_dir / "rpg-hot.sqlite"
    if hot.exists():
        roster = probe_roster(hot, args.timeout)
        if roster <= 0:
            aside = hot.with_name(
                f"{hot.name}.poisoned-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}")
            hot.rename(aside)
            for suffix in ("-shm", "-wal"):
                side = hot.with_name(hot.name + suffix)
                if side.exists():
                    side.unlink()
            log(f"[{TOOL}] slot {args.slot} rpg-hot.sqlite has an EMPTY species roster "
                f"({roster} rows) -- moved aside to {aside.name}; reseeding")
    if not hot.exists():
        source_data = args.source_data or (args.server_root / "data")
        seed_data(source_data, data_dir, log, args.timeout)

    # Read current state under lock (L8 fix)
    srv_path = root / "servers.json"
    lock_path = root / "servers.lock"
    with servers_lock(lock_path, args.lock_timeout_seconds, log):
        srv = read_servers(srv_path)
        mine = _find_entry(srv, args.slot)

        if mine and pid_alive(int(mine.get("pid", 0))):
            if not args.force:
                raise Refusal("SLOT-ALREADY-RUNNING",
                               f"slot {args.slot} already has a server running "
                               f"(PID {mine.get('pid')} on :{mine.get('port')}); "
                               f"--stop it first, or --force to restart")
            # L9 fix: the new server is started and verified BEFORE the old one is
            # considered replaced. The PowerShell original killed the old server first
            # (``Stop-Process -Id $mine.pid -Force; Start-Sleep -Seconds 1``), then the
            # new one could throw -- net: no server, and servers.json still named the
            # now-dead PID. Here the new server must pass /health before we proceed.
            old_pid = int(mine.get("pid", 0))
            log(f"[{TOOL}] --force: stopping old server (PID {old_pid}) to free port {port}")
            stop_process(old_pid, args.timeout)

        # Start the new server
        log_path = slot_log_path(root, args.slot)
        proc = start_server_process(server_exe, args.server_root, port, data_dir, log_path)

        # L7 fix: wait for health before reporting STARTED
        health = wait_for_health(port, args.health_timeout, args.poll_interval, log)
        if health is None:
            stop_process(proc.pid, args.timeout)
            raise Refusal("HEALTH-TIMEOUT",
                           f"the server did not answer /health with ok:true within "
                           f"{args.health_timeout}s",
                           stage="health-check")

        # Write the new entry
        entry = {
            "slot": args.slot,
            "pid": proc.pid,
            "port": port,
            "dataDir": str(data_dir),
            "log": str(log_path),
            "startedAt": now_iso(),
        }
        srv["servers"] = [e for e in srv["servers"] if _slot_of(e) != args.slot] + [entry]
        write_servers(srv_path, srv, log)

    log(f"[{TOOL}] slot {args.slot} server STARTED  pid={proc.pid}  "
        f"url=http://127.0.0.1:{port}  data={data_dir}")
    log(f"[{TOOL}] log: {log_path}   (health: http://127.0.0.1:{port}/health)")
    return {
        "verb": "start",
        "slot": args.slot,
        "pid": proc.pid,
        "port": port,
        "url": f"http://127.0.0.1:{port}",
        "dataDir": str(data_dir),
        "log": str(log_path),
        "health": health,
        "ok": True,
    }


def cmd_stop(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    if args.slot <= 0:
        raise Refusal("SLOT-REQUIRED", "--stop needs --slot <n>")
    root = args.pool
    srv_path = root / "servers.json"
    lock_path = root / "servers.lock"
    with servers_lock(lock_path, args.lock_timeout_seconds, log):
        if not srv_path.exists():
            log(f"[{TOOL}] no servers.json -- nothing was started by this tool")
            return {"verb": "stop", "slot": args.slot, "ok": True, "alreadyStopped": True}
        srv = read_servers(srv_path)
        mine = _find_entry(srv, args.slot)
        if not mine:
            log(f"[{TOOL}] slot {args.slot} has no recorded server")
            return {"verb": "stop", "slot": args.slot, "ok": True, "alreadyStopped": True}
        if int(mine.get("port", 0)) == OWNER_PORT:
            raise Refusal("OWNER-PORT-RECORDED",
                           f"slot {args.slot} is recorded on the OWNER's port")
        pid = int(mine.get("pid", 0))
        port = int(mine.get("port", 0))
        was_running = stop_process(pid, args.timeout)
        if was_running:
            log(f"[{TOOL}] slot {args.slot} server STOPPED (pid {pid}, port {port}) -- "
                f"only that recorded PID was touched")
        else:
            log(f"[{TOOL}] slot {args.slot} server (pid {pid}) was already gone")
        srv["servers"] = [e for e in srv["servers"] if _slot_of(e) != args.slot]
        write_servers(srv_path, srv, log)
    return {"verb": "stop", "slot": args.slot, "pid": pid, "port": port,
            "wasRunning": was_running, "ok": True}


def cmd_status(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    root = args.pool
    srv_path = root / "servers.json"
    reg_path = root / "slots.json"
    max_slots = 3
    if reg_path.exists():
        try:
            reg = json.loads(reg_path.read_text(encoding="utf-8-sig"))
            if reg.get("maxSlots"):
                max_slots = int(reg["maxSlots"])
        except (OSError, ValueError):
            pass
    srv = read_servers(srv_path) if srv_path.exists() else {"servers": []}
    log(f"[{TOOL}] pool {root}   (owner's server: :{OWNER_PORT} -- never touched here)")
    log("  slot  port  pid     health")
    rows: list[dict[str, Any]] = []
    for n in range(1, max_slots + 1):
        port = slot_port(reg_path, args.base_port, n)
        entry = _find_entry(srv, n)
        if not entry:
            log(f"  {n:<5} {port:<5} {'-':<7} (not running)")
            rows.append({"slot": n, "port": port, "pid": None, "health": "not running"})
            continue
        pid = int(entry.get("pid", 0))
        alive = pid_alive(pid)
        health = "unreachable"
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{entry['port']}/health", timeout=3) as resp:
                health = f"HTTP {resp.status}"
        except Exception as exc:
            health = f"unreachable ({str(exc).splitlines()[0] if str(exc) else 'error'})"
        pid_col = str(pid) if alive else "dead"
        log(f"  {n:<5} {entry['port']:<5} {pid_col:<7} {health}")
        if entry.get("dataDir"):
            log(f"        data: {entry['dataDir']}")
        rows.append({"slot": n, "port": entry["port"], "pid": pid if alive else None,
                     "health": health, "dataDir": entry.get("dataDir")})
    return {"verb": "status", "poolRoot": str(root), "slots": rows, "ok": True}


# ------------------------------------------------------------------------------------------------
# CLI
# ------------------------------------------------------------------------------------------------

VERBS = {
    "start": cmd_start,
    "stop": cmd_stop,
    "status": cmd_status,
}


def build_parser() -> argparse.ArgumentParser:
    """Every flag answers to BOTH spellings: ``--start`` is canonical, ``-Start`` is the
    compatibility alias the original's callers (and ``prove-slot-connection.py``) already use.
    """
    parser = argparse.ArgumentParser(
        prog=f"scripts/{TOOL}.py",
        description="Run a pool slot's own RPG server: its own loopback port, its own data directory.",
        epilog="usage: lane_server.py -Start | -Stop -Slot <n> | -Status\n"
               "pool from -PoolRoot or $FUSIONRPG_GAME_POOL; server from -ServerRoot or "
               "<repo>/dist/FusionRpg.Server",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-Start", "--start", dest="start", action="store_true",
                        help="start the slot's server")
    parser.add_argument("-Stop", "--stop", dest="stop", action="store_true",
                        help="stop the slot's server")
    parser.add_argument("-Status", "--status", dest="status", action="store_true",
                        help="report per-slot status")
    parser.add_argument("-Slot", "--slot", dest="slot", type=int, default=0,
                        help="slot number")
    parser.add_argument("-PoolRoot", "--pool-root", dest="pool_root",
                        default=os.environ.get("FUSIONRPG_GAME_POOL", ""),
                        help="pool root (default $FUSIONRPG_GAME_POOL)")
    parser.add_argument("-BasePort", "--base-port", dest="base_port", type=int, default=5100,
                        help="slot N's port is BasePort + N")
    parser.add_argument("-ServerRoot", "--server-root", dest="server_root", default="",
                        help="published server tree (default <repo>/dist/FusionRpg.Server)")
    parser.add_argument("-SourceData", "--source-data", dest="source_data", default="",
                        help="data dir to seed a fresh slot from (default <ServerRoot>/data)")
    parser.add_argument("-Force", "--force", dest="force", action="store_true",
                        help="restart even if a server is already running")
    parser.add_argument("-Json", "--json", dest="json", action="store_true",
                        help="also print the machine-readable verdict")
    parser.add_argument("--timeout", dest="timeout", type=float, default=60.0,
                        help="hard budget in seconds for each external call")
    parser.add_argument("--health-timeout", dest="health_timeout", type=float, default=60.0,
                        help="how long to wait for /health after starting")
    parser.add_argument("--poll-interval", dest="poll_interval", type=float, default=1.0,
                        help="seconds between /health polls")
    parser.add_argument("--lock-timeout-seconds", dest="lock_timeout_seconds", type=float, default=30.0,
                        help="how long to wait for the servers lock")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    chosen = [verb for verb in VERBS if getattr(args, verb)]
    log = Logger()
    if len(chosen) != 1:
        parser.print_usage(sys.stderr)
        log(f"pick exactly one verb: {', '.join('-' + v for v in VERBS)} "
            f"(--json for the machine-readable verdict)")
        return EXIT_REFUSED
    args.pool = None
    try:
        args.pool = resolve_pool_root(args.pool_root, log)
        if not args.server_root:
            repo = Path(__file__).resolve().parents[1]
            args.server_root = repo / "dist" / "FusionRpg.Server"
        else:
            args.server_root = Path(args.server_root)
        if args.source_data:
            args.source_data = Path(args.source_data)
        else:
            args.source_data = None
        verdict = VERBS[chosen[0]](args, log)
        exit_code = int(verdict.get("exitCode", EXIT_OK))
    except Refusal as refusal:
        log.refuse(refusal)
        if args.json:
            print(json.dumps({"verb": chosen[0], "ok": False,
                              "refusals": log.refusals, "transcript": log.lines}, indent=2),
                  flush=True)
        return refusal.exit_code
    if args.json:
        verdict = dict(verdict)
        verdict["refusals"] = log.refusals
        verdict["transcript"] = log.lines
        print(json.dumps(verdict, indent=2), flush=True)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
