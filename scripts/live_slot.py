#!/usr/bin/env python3
"""Manage the live-probe GAME POOL: clone a game install into a slot, claim it, see who holds what.

This is the Python port of ``scripts/live-slot.ps1`` and the entry point the live-probe
documentation now names. The CLI contract is preserved **including the PowerShell spelling**, so
the one live in-repo caller (``gk-fusion/scripts/prove-slot-connection.py``, which shells
``live-slot.ps1 -Acquire`` / ``-Release``) is re-pointed by changing the program name alone. Every
verb and parameter answers to ``-Status``/``--status``; ``--`` is the documented spelling and the
single-dash form is the compatibility alias.

    python gk-core/scripts/live_slot.py -Status
    python gk-core/scripts/live_slot.py -Clone   -Session sgc-4              # populate a free slot, verify
    python gk-core/scripts/live_slot.py -Acquire -Session sgc-4              # claim (clones on first use)
    python gk-core/scripts/live_slot.py -Release -Session sgc-4
    python gk-core/scripts/live_slot.py -Reclaim -Slot 2 -Session manager    # force-release a stale claim

Owner ruling 2026-09-21: lanes clone the game into a pool, claim a slot and release it; at most
three live runs at once; coordination is a JSON registry guarded by a lock file; no human sequences
anything. **No path is hardcoded** -- the pool root is ``$FUSIONRPG_GAME_POOL`` (or ``--pool-root``)
and the install to clone is ``$FUSIONRPG_GAME_SOURCE`` (or ``--source-install``).

SLOT MODEL (unchanged from the PowerShell original). A slot OWNS its install. Occupancy is a
state, not the absence of an entry:

    free  -> no entry (nothing cloned yet)          ready  -> cloned + verified, nobody probing
    cloning -> a clone is in flight (crash = stale)  occupied -> held by a session
    broken -> clone missing or verification failed   (re-clone with --clone --force)

``ready`` means *cloned and verified*: every entry of ``REQUIRED_ENTRIES`` must be present. A clone
missing any of them is ``broken``, never ``ready``.

A slot's SERVER PORT is **stored on acquire** (``entry["port"]``) and otherwise **derived** as
``base_port + slot``. Both halves are load-bearing -- an earlier reading of only the stored field
silently skipped two of three slots (``tasks/reports/mega-merge-manager-resume-20260925.md``
section H.3) -- so the rule lives in exactly one function here, ``resolve_port``.

Two measured defects are fixed by construction rather than patched:

**Defect 1 -- a clone inherits the OWNER's server URL.** A slot is cloned from the owner's install
and the owner's ``Mods/fusionrpg.cfg`` names the owner's server. Measured on a temp pool with a
source cfg naming ``http://127.0.0.1:5088``: ``live-slot.ps1 -Clone`` left BOTH cloned slots naming
5088, and ``-Acquire`` left it naming 5088 while writing ``port: 5101`` into the registry and
printing "YOUR SERVER PORT: 5101". The tool said the right thing and the game did the wrong thing:
the Injector reads ``FUSIONRPG_SERVER_URL``, else ``ServerUrl=`` in the cfg, else 5088 **with a
warning**, and ``deploy-play.py`` only rewrites the cfg at stage 9 of 12, so any earlier failure
leaves the inherited value. ``stamp_server_url`` now writes the slot's OWN port into the clone's
cfg on clone, on ``--force`` re-clone and on acquire, and says which value it wrote. It refuses by
name rather than inventing a key when the cfg exists but carries none.

**Defect 2 -- ``--force`` re-clone cannot delete a tree with trailing-space directory names.**
Measured: ``shutil.rmtree`` fails ``WinError 145: The directory is not empty``; Windows PowerShell
5.1's ``Remove-Item -LiteralPath <dir> -Recurse -Force`` (what the original calls) fails
``Win32Exception: The system cannot find the file specified`` with exit 1 and no guidance, leaving
the slot permanently ``broken``. The cause is a polluted *source* carrying directories such as
``Mods --no-incremental --nologo -v n `` whose trailing space the Windows path APIs normalise away.
``remove_tree`` tries ``shutil.rmtree`` first (fast, and it succeeds for every normal tree) and
falls back to mirroring an EMPTY directory over the target with ``robocopy /MIR`` -- robocopy
enumerates the offending names itself, which is why it can delete what the path APIs cannot name --
then retries. The ``\\\\?\\``-prefixed walk is the other known-good path and is deliberately NOT used
here: it needs its own long-path plumbing through every step, while the mirror is one already-
measured call whose success criterion is a documented exit range (0-7).

Tool discipline (AGENTS.md, "Language for new tooling"): Python, not PowerShell; a hard timeout on
every external call; a NAMED refusal the moment a precondition fails (fail closed, never "continue
and report empty"); machine-readable ``--json``; and a non-zero exit that names the failing stage.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Sequence

TOOL = "live_slot"

# What every install clone must contain to be usable by a probe. MelonLoader host, the shipped
# content dirs, and the executable; a clone missing any of these is `broken`, never `ready`.
# Mirrors `live-slot.ps1:57` and `gk-fusion/scripts/prove-slot-connection.py:250` (REQUIRED_ENTRIES).
REQUIRED_ENTRIES: tuple[str, ...] = (
    "PlantsVsZombiesRH.exe",
    "MelonLoader",
    "BepInEx",
    "Mods",
    "GameAssembly.dll",
)

# The MelonLoader host's per-install cfg. `deploy-play.py:269` puts it at `<install>/Mods/` and
# `:277` is explicit that the BepInEx host has NO cfg (it reads FUSIONRPG_SERVER_URL only), which
# is why an absent cfg is a warning here and not a refusal.
CFG_RELATIVE = Path("Mods") / "fusionrpg.cfg"

# The key, READ from the deploy rather than guessed: `gk-fusion/scripts/deploy-play.py:505` writes
# `ServerUrl={url}` and `:508` verifies it with exactly this pattern. `gk-core/tests/tools/test_live_slot.py`
# reads `deploy-play.py` back and fails if the key this tool stamps stops being the key the deploy
# writes, so a change to the deploy cannot silently leave this tool stamping a dead key.
SERVER_URL_PATTERN = re.compile(r"^ServerUrl=(\S+)\s*$", re.MULTILINE)

# The owner's server. `lane-server.ps1:39,68` refuses any slot that resolves here, and
# `deploy-play.py` refuses a pooled deploy whose `--server-url` is still this value.
OWNER_PORT = 5088

# Process enumeration is the ONE place this port still shells out to PowerShell, and it does so
# deliberately: `Get-Process`'s `.Path` is the only way to read a process's full image path without
# a third-party dependency (psutil is not a repo dependency), and the brief lists process
# enumeration among the external calls that need a hard timeout. It is a single implementation, not
# a second one. The sentinel proves the enumeration RAN; its absence is a refusal, never an empty
# answer -- the original swallowed the failure and reported "no game running", which is what makes
# a stale claim look live and a live claim look reclaimable.
_PROCESS_ENUM_SCRIPT = r"""
$ErrorActionPreference = "SilentlyContinue"
foreach ($p in (Get-Process)) {
  $path = $null
  try { $path = $p.Path } catch { $path = $null }
  if ($path) { "{0}`t{1}" -f $p.Id, $path }
}
"#LIVE-SLOT-ENUM-COMPLETE"
"""

# robocopy exit codes. 0-7 are success (0 nothing, 1 files copied, 2 extra files, 3 both, and 4-7
# are the mismatch/Xcopy-copyable bits); 8+ is failure. Measured on this machine: a /MIR copy of a
# populated tree returns 1, a /MIR of an empty dir over a polluted target returns 2, and a missing
# source returns 16 (the "source does not exist" bit) -- which is why >= 8 is the failure line and
# not "non-zero".
ROBOCOPY_SUCCESS_MAX = 7

EXIT_OK = 0
EXIT_REFUSED = 1     # a named refusal: a precondition failed, or the verb is illegal here.
EXIT_POOL_FAULT = 2  # the pool could not be read/written/locked, or an external call failed/timed out.
EXIT_CLONE_BROKEN = 4  # a clone finished and FAILED verification -- the PowerShell original's exit 4.

# Every refusal is named, and the name carries its meaning. A refusal that is not in this table is a
# bug: `Refusal.__init__` raises `KeyError` on an unknown name, so one cannot be added without a
# reader learning what it means.
REFUSALS = {
    "POOL-ROOT-MISSING":
        "no pool root: pass --pool-root or set FUSIONRPG_GAME_POOL (machine-specific; never commit it)",
    "POOL-ROOT-UNUSABLE":
        "the pool root could not be created or resolved to a directory",
    "SESSION-REQUIRED":
        "--clone / --acquire / --release need --session <id> (the id recorded as the holder/cloner)",
    "SLOT-REQUIRED":
        "--reclaim needs --slot <n>",
    "SLOT-UNKNOWN":
        "the named slot does not exist in the pool registry",
    "ALL-SLOTS-EXIST":
        "every slot already has an entry; pass --slot <n> --force to re-clone one",
    "SLOT-OCCUPIED":
        "the slot is held by another session; release or --reclaim it first "
        "(never clone over a live probe)",
    "SLOT-STILL-RUNNING":
        "a game process is still running from that install; close that game first "
        "(--force only when you are certain it is yours)",
    "ALL-SLOTS-HELD":
        "every live slot is held; wait for one to release "
        "(all slots held is a wait, never a kill of another session's game)",
    "SLOT-BROKEN":
        "the slot's install failed verification; re-clone it with --clone --slot <n> --force",
    "SOURCE-INSTALL-MISSING":
        "no source install: pass --source-install or set FUSIONRPG_GAME_SOURCE",
    "SOURCE-INSTALL-NOT-FOUND":
        "the source install path does not exist",
    "CLONE-FAILED":
        "robocopy could not mirror the source install into the slot",
    "ROBOCOPY-MISSING":
        "robocopy is not on PATH and not in System32, so the copy and the delete remedy are "
        "unavailable; install it or re-run with a source that does not need a mirror",
    "TREE-DELETE-FAILED":
        "the slot's install could not be deleted, even after mirroring an empty directory over it "
        "(that is the only known remedy for a tree carrying trailing-space directory names)",
    "TEMP-CLEANUP":
        "a temporary directory this tool created could not be deleted "
        "(testing-standard.md R3: a failed cleanup is a failure, never a swallow)",
    "CFG-NO-SERVERURL":
        "the clone's Mods/fusionrpg.cfg exists but carries no ServerUrl= key, so the slot's game "
        "would fall back to the owner's :5088 with only a warning. Refusing rather than inventing "
        "the key: add the key the deploy writes (deploy-play.py:505) and re-run",
    "REGISTRY-UNREADABLE":
        "the pool registry (slots.json) exists but is not readable JSON",
    "REGISTRY-UNWRITABLE":
        "the pool registry could not be written; the requested change was NOT recorded",
    "LOCK-UNAVAILABLE":
        "the slot lock could not be taken within the timeout "
        "(another agent is mid-register; retry, never force the lock)",
    "PROCESS-ENUM-FAILED":
        "the process enumeration did not complete, so a claim's liveness could not be measured. "
        "Refusing rather than reporting an empty answer: an empty answer reads as 'no game "
        "running', which is what makes a live claim look reclaimable",
    "EXTERNAL-CALL-FAILED":
        "an external call (robocopy, the PowerShell host) failed or timed out",
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
        """The line a caller reads, and it carries the REMEDY as well as the instance.

        The meaning is not decoration: the PowerShell original folded the remedy into the message
        it threw ("-- wait for one to release; do not kill another session's game"), and a refusal
        that prints only a name and a count sends the reader back to this file to find out what to
        do. The name says what happened, the detail says which instance, the meaning says the fix.
        """
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
    """Trim the pool root, say so when trimming mattered, create it, and resolve it.

    An env value can carry stray whitespace: `set VAR=value && ...` in cmd.exe captures the space
    before the `&&`, and a trailing space is invisible in every log yet turns `<pool>/slots.lock`
    into `<pool> /slots.lock`, which the path APIs refuse to find (measured 2026-09-22: a probe died
    after 4 s reporting 'Cannot find path', which reads like a missing pool rather than a stray
    byte).
    """
    if not raw or not str(raw).strip():
        raise Refusal("POOL-ROOT-MISSING", "FUSIONRPG_GAME_POOL is unset and --pool-root was not given")
    as_given = str(raw)
    trimmed = as_given.strip().rstrip("\\/")
    if not trimmed:
        # `Path("")` is the CURRENT DIRECTORY. A pool root of "/" or "\\" would therefore resolve
        # to wherever the tool happened to be started from and then mkdir a `slots.json` there --
        # so an empty-after-trim root is a refusal, never a silent cwd.
        raise Refusal("POOL-ROOT-MISSING",
                      f"the pool root {as_given!r} is nothing but whitespace and separators")
    if trimmed != as_given:
        log(f"[{TOOL}] pool root carried surrounding whitespace/separators -- using '{trimmed}'")
    try:
        Path(trimmed).mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise Refusal("POOL-ROOT-UNUSABLE", f"could not create '{trimmed}': {exc}") from exc
    return Path(trimmed).resolve()


def default_max_slots(args: argparse.Namespace) -> int:
    """The seed for a registry that does not exist yet: the flag when it was given, else 3.

    `live-slot.ps1:59` records whether `-MaxSlots` was BOUND (`$PSBoundParameters.ContainsKey`), so
    an omitted flag never overwrites a stored `maxSlots` on an existing pool. `default=None` here
    carries that same distinction, which is why this is not `args.max_slots or 3`: an explicit 0
    must stay 0 here and be floored to 1 by the acquire path, exactly as the original did.
    """
    return 3 if args.max_slots is None else args.max_slots


def read_registry(path: Path, max_slots: int) -> dict[str, Any]:
    """Read slots.json. Tolerant of a BOM, because the PowerShell original can leave one.

    `live-slot.ps1:95` writes with `Set-Content -Encoding utf8`, which emits a BOM under Windows
    PowerShell 5.1 and NOT under PowerShell 7 -- so a pool created by one host and read by the
    other raises `JSONDecodeError` on a plain `utf-8` read. Reading `utf-8-sig` decodes both.
    """
    if not path.exists():
        return {"maxSlots": max_slots, "updatedAt": now_iso(), "slots": [], "staleBreaks": []}
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise Refusal("REGISTRY-UNREADABLE", f"{path}: {exc}") from exc
    if not isinstance(data, dict):
        raise Refusal("REGISTRY-UNREADABLE", f"{path}: the registry is not a JSON object")
    for key in ("slots", "staleBreaks"):
        data.setdefault(key, [])
    return data


def write_registry(path: Path, registry: dict[str, Any], log: Logger) -> None:
    """Persist slots.json, atomically, as UTF-8 **without** a BOM.

    No BOM is the safe direction while both implementations coexist: `gk-fusion/scripts/prove-slot-connection.py:212`
    reads this file with a plain `utf-8` and a leading `\\ufeff` would raise `JSONDecodeError` there,
    whereas every reader -- PowerShell `Get-Content` and a `utf-8-sig` Python read alike -- tolerates
    its absence. The write goes through a sibling temp file and `os.replace` so a crash mid-write
    cannot leave a truncated registry; the original's direct `Set-Content` could.
    """
    registry["updatedAt"] = now_iso()
    body = json.dumps(registry, indent=2)
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
        raise Refusal("REGISTRY-UNWRITABLE", f"{path}: {exc}", stage="write-registry",
                      exit_code=EXIT_POOL_FAULT) from exc
    log(f"[{TOOL}] registry written: {path}")


@contextlib.contextmanager
def slot_lock(lock_path: Path, registry_path: Path, session: str, timeout_seconds: int,
              stale_minutes: int, log: Logger) -> Iterator[None]:
    """Hold `<pool>/slots.lock` for one read-modify-write only -- never for a whole probe.

    `CreateNew` semantics: an exclusive create is the lock. A lock older than `stale_minutes` (a
    crashed holder) is broken and the break is RECORDED in `staleBreaks`, exactly as
    `live-slot.ps1:98-124` does -- including writing that record before the lock is held, which is
    this shape's one inherited weakness and is left visible rather than silently changed.
    """
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            handle = os.open(str(lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            # The holder may have released the lock between the failed create and this check;
            # tolerate it and retry rather than measuring the age of a file that is gone.
            if not lock_path.exists():
                time.sleep(0.25)
                continue
            age_minutes = (time.time() - lock_path.stat().st_mtime) / 60.0
            if age_minutes >= stale_minutes:
                registry = read_registry(registry_path, 3)
                registry["staleBreaks"].append(
                    {"at": now_iso(), "ageMinutes": round(age_minutes, 1), "by": session})
                write_registry(registry_path, registry, log)
                with contextlib.suppress(OSError):
                    os.unlink(lock_path)
                log(f"[{TOOL}] broke a stale lock (age {round(age_minutes, 1)}m) and recorded the break")
                continue
            if time.monotonic() >= deadline:
                raise Refusal(
                    "LOCK-UNAVAILABLE",
                    f"could not take the slot lock within {timeout_seconds}s "
                    f"(another agent is mid-register; retry)")
            time.sleep(0.4)
        except OSError as exc:
            raise Refusal("LOCK-UNAVAILABLE", f"{lock_path}: {exc}", stage="lock",
                          exit_code=EXIT_POOL_FAULT) from exc
    try:
        os.write(handle, f"{session}\n".encode("utf-8"))
    finally:
        os.close(handle)
    try:
        yield
    finally:
        with contextlib.suppress(OSError):
            os.unlink(lock_path)


def run_external(argv: Sequence[str], timeout: float, stage: str) -> subprocess.CompletedProcess:
    """One external call: both streams captured, hard timeout, never a shell, never `2>&1`."""
    try:
        return subprocess.run(list(argv), capture_output=True, text=True,
                              timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("EXTERNAL-CALL-FAILED",
                      f"{argv[0]} timed out after {timeout}s: {(exc.stderr or '')[-400:]}",
                      stage=stage, exit_code=EXIT_POOL_FAULT) from exc
    except OSError as exc:
        raise Refusal("EXTERNAL-CALL-FAILED", f"{argv[0]}: {exc}", stage=stage,
                      exit_code=EXIT_POOL_FAULT) from exc


def find_robocopy() -> str | None:
    """A system binary is not always on PATH (measured 2026-09-22: `robocopy` was "not recognized"
    in the shell that ran the first REAL clone, while it had worked in an earlier shell). Resolve it
    explicitly, then fall back to System32 rather than failing the clone for a PATH reason.
    """
    found = shutil.which("robocopy")
    if found:
        return found
    system = os.environ.get("SystemRoot", r"C:\Windows")
    for candidate in (Path(system) / "System32" / "robocopy.exe", Path("C:/Windows/System32/robocopy.exe")):
        if candidate.exists():
            return str(candidate)
    return None


def mirror(source: str, target: str, timeout: float, stage: str) -> int:
    """robocopy /MIR source -> target. Returns the exit code; 0-7 is success."""
    robocopy = find_robocopy()
    if robocopy is None:
        raise Refusal("ROBOCOPY-MISSING", stage=stage, exit_code=EXIT_POOL_FAULT)
    result = run_external(
        [robocopy, source, target, "/MIR", "/NFL", "/NDL", "/NJH", "/NJS", "/R:1", "/W:1"],
        timeout=timeout, stage=stage)
    if result.returncode > ROBOCOPY_SUCCESS_MAX:
        raise Refusal(
            "CLONE-FAILED" if stage == "clone" else "EXTERNAL-CALL-FAILED",
            f"robocopy ({robocopy}) {source} -> {target} failed with code {result.returncode} "
            f"(8+ is failure; 16 is 'source does not exist'): "
            f"{(result.stdout + result.stderr)[-600:]}",
            stage=stage, exit_code=EXIT_POOL_FAULT)
    return result.returncode


def clone_tree(source: str | Path | None, target: Path, timeout: float, log: Logger) -> str:
    """Mirror a game tree into a slot. robocopy is far faster than a file copy and survives long
    paths; the fallback exists for a PATH reason only and says so loudly.
    """
    # The emptiness check is on the STRING, not on the Path: `Path("")` is the current directory
    # and is truthy, so testing the Path would mirror the CWD into a slot when the caller passed an
    # unset --source-install.
    if not str(source or "").strip():
        raise Refusal("SOURCE-INSTALL-MISSING", "FUSIONRPG_GAME_SOURCE is unset and "
                                                "--source-install was not given")
    path = Path(str(source))
    if not path.exists():
        raise Refusal("SOURCE-INSTALL-NOT-FOUND", str(path))
    target.mkdir(parents=True, exist_ok=True)
    robocopy = find_robocopy()
    if robocopy is None:
        log(f"[{TOOL}] robocopy not found on PATH or in System32 -- falling back to a file copy "
            f"(much slower, and it cannot handle the trailing-space directory names that make "
            f"a --force re-clone hard)")
        shutil.copytree(source, target, dirs_exist_ok=True)
        return "copytree"
    code = mirror(str(source), str(target), timeout, "clone")
    log(f"[{TOOL}] robocopy exit {code}")
    return f"robocopy:{code}"


def remove_tree(target: Path, timeout: float, log: Logger) -> str:
    """Delete a slot's install, surviving the trailing-space directory names (defect 2).

    `shutil.rmtree` first because it succeeds for every normal tree and is far faster than a
    robocopy pass. It fails `WinError 145` on a tree carrying a directory whose name ends in a space
    (measured), because the Windows path APIs normalise the trailing space away and then report the
    parent as unexpectedly non-empty. The remedy is to mirror an EMPTY directory over the target:
    robocopy enumerates the offending names through the `\\?\\` namespace internally, so it deletes
    what the path APIs cannot name. The mirror source is created inside the pool root (same volume)
    and its removal is checked, never swallowed (testing-standard.md R3).
    """
    if not target.exists():
        return "absent"
    try:
        shutil.rmtree(target)
        return "rmtree"
    except OSError as first_error:
        log(f"[{TOOL}] rmtree could not delete {target} ({type(first_error).__name__}: "
            f"{first_error}) -- mirroring an empty directory over it")
    robocopy = find_robocopy()
    if robocopy is None:
        raise Refusal("ROBOCOPY-MISSING",
                      f"the install at {target} needs the robocopy empty-mirror remedy and robocopy "
                      f"is not on PATH or in System32; without it the tree stays and the slot stays "
                      f"broken. First error: {first_error}",
                      stage="delete", exit_code=EXIT_POOL_FAULT)
    try:
        with tempfile.TemporaryDirectory(prefix=f"{TOOL}-empty-", dir=str(target.parent)) as empty:
            code = mirror(empty, str(target), timeout, "delete")
            log(f"[{TOOL}] robocopy empty-mirror exit {code}")
    except Refusal:
        raise
    except OSError as exc:
        raise Refusal("TEMP-CLEANUP", f"could not create/remove the empty mirror directory under "
                                      f"{target.parent}: {exc}", stage="delete",
                      exit_code=EXIT_POOL_FAULT) from exc
    try:
        shutil.rmtree(target)
    except OSError as exc:
        raise Refusal("TREE-DELETE-FAILED",
                      f"{target}: rmtree failed ({type(exc).__name__}: {exc}) and the empty-mirror "
                      f"remedy did not clear it either; the slot stays broken",
                      stage="delete", exit_code=EXIT_POOL_FAULT) from exc
    log(f"[{TOOL}] removed {target} (rmtree -> robocopy empty-mirror -> rmtree)")
    return "rmtree+robocopy-mirror"


def test_install(install: Path) -> list[str]:
    """The verification contract: every REQUIRED_ENTRIES path must exist."""
    return [entry for entry in REQUIRED_ENTRIES if not (install / entry).exists()]


def resolve_port(entry: dict[str, Any] | None, base_port: int, slot: int) -> int:
    """A slot's server port: STORED on acquire, otherwise DERIVED as base_port + slot.

    One function, because the two halves are both load-bearing and a reader who consults only one
    of them gets the wrong answer for two of three slots (measured 2026-09-25: reading only the
    stored field silently skipped every slot that had not been acquired since the registry was
    created).
    """
    if entry:
        stored = entry.get("port")
        if stored:
            return int(stored)
    return base_port + slot


def slot_processes(install: Path, process_timeout: float) -> list[dict[str, Any]]:
    """The game processes actually running FROM that install.

    Occupancy is not just a registry field: the caller reports the live process, so a stale claim
    (a crashed lane, a killed game) is visible instead of being trusted. A failure to enumerate
    REFUSES; the original swallowed it (`catch { return @() }`) and reported "no game running",
    which is exactly the wrong answer for a liveness check.
    """
    if not install.exists():
        return []
    prefix = os.path.normcase(str(install))
    for host in ("pwsh", "powershell"):
        executable = shutil.which(host)
        if executable is None:
            continue
        result = run_external([executable, "-NoProfile", "-NonInteractive", "-Command",
                               _PROCESS_ENUM_SCRIPT], timeout=process_timeout, stage="process-enum")
        if result.returncode != 0:
            raise Refusal("PROCESS-ENUM-FAILED",
                          f"{host} exited {result.returncode} while enumerating processes: "
                          f"{(result.stdout + result.stderr)[-600:]}",
                          stage="process-enum", exit_code=EXIT_POOL_FAULT)
        if "#LIVE-SLOT-ENUM-COMPLETE" not in result.stdout:
            raise Refusal("PROCESS-ENUM-FAILED",
                          f"{host} did not reach the end of the enumeration (no sentinel in "
                          f"{len(result.stdout)} bytes of output); an empty answer here would read "
                          f"as 'no game running'",
                          stage="process-enum", exit_code=EXIT_POOL_FAULT)
        found: list[dict[str, Any]] = []
        for line in result.stdout.splitlines():
            if "\t" not in line:
                continue
            pid_text, _, image = line.partition("\t")
            image = image.strip()
            if not image or not os.path.normcase(image).startswith(prefix):
                continue
            try:
                found.append({"pid": int(pid_text), "image": image})
            except ValueError:
                continue
        # The sentinel proved the enumeration RAN, so an empty list is an ANSWER ("no game from
        # this install"), not a failure. Only a missing sentinel or a non-zero exit refuses -- the
        # distinction the original lost by swallowing the error and returning an empty list.
        return found
    raise Refusal("PROCESS-ENUM-FAILED",
                  "neither pwsh nor powershell is available to enumerate processes, so a claim's "
                  "liveness cannot be measured",
                  stage="process-enum", exit_code=EXIT_POOL_FAULT)


def stamp_server_url(install: Path, slot: int, port: int, log: Logger) -> dict[str, Any]:
    """Write the slot's OWN port into its cfg (defect 1). Returns what happened, or refuses.

    Three cases, distinguished on purpose:

    * a cfg carrying a ``ServerUrl=`` key -> rewritten in place to this slot's port, and the value
      it replaced is reported. This is the measured defect: the clone inherited 5088.
    * a cfg with NO key -> **refuse by name**. The Injector's fallback is 5088 with a warning, and
      inventing a key is the guess this is not allowed to make; the refusal names the exact line
      `deploy-play.py:505` writes so the fix is one edit.
    * no cfg at all -> nothing was inherited, so the precondition "this slot will not dial the
      owner's server" already holds. Reported loudly, and no file is invented: the BepInEx host has
      no cfg by design (`deploy-play.py:277`) and a staged `deploy-play.py` run writes the key.
    """
    cfg = install / CFG_RELATIVE
    url = f"http://127.0.0.1:{port}"
    result: dict[str, Any] = {"cfg": str(cfg), "outcome": "absent", "previous": None, "current": None}
    if not cfg.exists():
        log(f"[{TOOL}] slot {slot}: no {CFG_RELATIVE.as_posix()} in the clone -- nothing was "
            f"inherited, so there is no owner URL to replace. Set FUSIONRPG_SERVER_URL or deploy "
            f"into this slot with --server-url {url} to point its game at {url}")
        return result
    try:
        text = cfg.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("CFG-NO-SERVERURL", f"{cfg} could not be read: {exc}", stage="stamp-cfg",
                      exit_code=EXIT_POOL_FAULT) from exc
    found = SERVER_URL_PATTERN.search(text)
    if not found:
        raise Refusal("CFG-NO-SERVERURL", f"{cfg} carries no 'ServerUrl=' key")
    previous = found.group(1)
    result["previous"] = previous
    if previous.rstrip("/") == url:
        result["outcome"] = "already-correct"
        result["current"] = url
        log(f"[{TOOL}] slot {slot}: {cfg.name} already names {url} (this slot's own port)")
        return result
    try:
        cfg.write_text(text[:found.start()] + f"ServerUrl={url}" + text[found.end():],
                       encoding="utf-8")
    except OSError as exc:
        raise Refusal("CFG-NO-SERVERURL", f"{cfg} could not be written: {exc}", stage="stamp-cfg",
                      exit_code=EXIT_POOL_FAULT) from exc
    result["outcome"] = "stamped"
    result["current"] = url
    log(f"[{TOOL}] slot {slot}: stamped {cfg.name} ServerUrl={previous} -> {url} "
        f"(the slot's own port; it must never name the owner's :{OWNER_PORT})")
    return result


def slot_install(root: Path, slot: int) -> Path:
    return root / f"slot-{slot}"


def entry_for(registry: dict[str, Any], slot: int) -> dict[str, Any] | None:
    for entry in registry.get("slots", []):
        try:
            if int(entry.get("slot")) == slot:
                return entry
        except (TypeError, ValueError):
            continue
    return None


def taken_slots(registry: dict[str, Any]) -> set[int]:
    taken: set[int] = set()
    for entry in registry.get("slots", []):
        try:
            taken.add(int(entry.get("slot")))
        except (TypeError, ValueError):
            continue
    return taken


def install_of(entry: dict[str, Any] | None, root: Path, slot: int) -> Path:
    """The install a slot's processes are measured from."""
    if entry and entry.get("installPath"):
        return Path(str(entry["installPath"]))
    return slot_install(root, slot)


def require_session(session: str, verb: str) -> str:
    if not session:
        raise Refusal("SESSION-REQUIRED", f"--{verb.lower()} needs --session <id>")
    return session


# ------------------------------------------------------------------------------------------------
# Verbs
# ------------------------------------------------------------------------------------------------

def cmd_status(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    root = args.pool
    registry = read_registry(root / "slots.json", default_max_slots(args))
    entries = registry.get("slots", [])
    occupied = [e for e in entries if e.get("state") == "occupied"]
    ready = [e for e in entries if e.get("state") == "ready"]
    free = int(registry.get("maxSlots", 3)) - len(occupied)
    log(f"[{TOOL}] pool: {root}   max {registry.get('maxSlots')}   free {free}   "
        f"updated {registry.get('updatedAt')}")
    log("  slot  state      session                install                          "
        "cloned/verified        port   process")
    rows: list[dict[str, Any]] = []
    for number in range(1, int(registry.get("maxSlots", 3)) + 1):
        entry = entry_for(registry, number)
        if entry is None:
            log(f"  {number:<5} {'free':<10} {'-':<22} (nothing cloned yet)")
            rows.append({"slot": number, "state": "free", "session": None, "install": None,
                         "port": resolve_port(None, args.base_port, number), "process": None,
                         "staleClaim": False, "notes": []})
            continue
        install = install_of(entry, root, number)
        process = entry.get("process")
        live: list[dict[str, Any]] = []
        if entry.get("installPath") and install.exists():
            live = slot_processes(install, args.process_timeout)
        if live:
            process_text = f"PID {live[0]['pid']}"
        elif not process:
            process_text = "none"
        else:
            process_text = str(process)
        port = resolve_port(entry, args.base_port, number)
        when = ""
        if entry.get("clonedAt"):
            when = str(entry["clonedAt"]).split("T")[0]
        if entry.get("verifiedAt"):
            when = f"{when}/{str(entry['verifiedAt']).split('T')[0]}"
        session_text = str(entry["session"]) if entry.get("session") else "-"
        log(f"  {number:<5} {str(entry.get('state')):<10} {session_text:<22} "
            f"{str(entry.get('installPath')):<32} {when:<22} {port:<6} {process_text}")
        stale = False
        if entry.get("state") == "occupied" and entry.get("acquiredAt"):
            held_minutes = int((datetime.now().astimezone()
                                - datetime.fromisoformat(str(entry["acquiredAt"]))).total_seconds() / 60)
            if held_minutes >= args.stale_claim_minutes and not process:
                stale = True
                log(f"        STALE CLAIM: held {held_minutes} min with no game process from that "
                    f"install -- --reclaim --slot {number} if the lane is gone")
        if entry.get("state") == "broken":
            log(f"        BROKEN: {'; '.join(str(n) for n in entry.get('notes') or [])} -- "
                f"re-clone with --clone --slot {number} --force")
        rows.append({"slot": number, "state": entry.get("state"), "session": entry.get("session"),
                     "install": entry.get("installPath"), "port": port,
                     "process": process_text, "staleClaim": stale,
                     "notes": list(entry.get("notes") or [])})
    log(f"  ready-to-claim (cloned + verified): {len(ready)}")
    return {"verb": "status", "poolRoot": str(root), "maxSlots": registry.get("maxSlots"),
            "free": free, "ready": len(ready), "occupied": len(occupied), "slots": rows,
            "staleBreaks": list(registry.get("staleBreaks") or []), "ok": True}


def pick_clone_slot(registry: dict[str, Any], requested: int) -> int:
    """The lowest slot number with no entry at all; a free slot must not steal an existing one."""
    cap = int(registry.get("maxSlots", 3))
    if requested > 0:
        return requested
    taken = taken_slots(registry)
    number = 1
    while number in taken and number <= cap:
        number += 1
    if number > cap:
        raise Refusal("ALL-SLOTS-EXIST",
                      f"all {cap} slots exist already; pass --slot <n> --force to re-clone one")
    return number


def new_entry(slot: int, install: Path, state: str, missing: Sequence[str]) -> dict[str, Any]:
    return {"slot": slot, "state": state, "session": None, "installPath": str(install),
            "clonedAt": now_iso(), "verifiedAt": now_iso(), "acquiredAt": None,
            "releasedAt": None, "process": None,
            "notes": [f"missing: {name}" for name in missing]}


def cmd_clone(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    session = require_session(args.session, "Clone")
    root = args.pool
    registry_path = root / "slots.json"
    with slot_lock(root / "slots.lock", registry_path, session, args.lock_timeout_seconds,
                   args.stale_lock_minutes, log):
        registry = read_registry(registry_path, default_max_slots(args))
        target = pick_clone_slot(registry, args.slot)
        install = slot_install(root, target)
        existing = entry_for(registry, target)
        if existing and existing.get("state") == "occupied" and not args.force:
            raise Refusal("SLOT-OCCUPIED", f"slot {target} is occupied by {existing.get('session')}")
        remedy = "kept"
        if install.exists():
            if not args.force:
                log(f"[{TOOL}] slot {target} already has an install; verifying instead of "
                    f"re-cloning (--force to re-clone)")
            else:
                log(f"[{TOOL}] --force: re-cloning slot {target}")
                remedy = remove_tree(install, args.timeout, log)
        clone_method = "already-present"
        if not install.exists():
            log(f"[{TOOL}] cloning {args.source_install} -> {install} "
                f"(this takes a while; game trees are large)")
            clone_method = clone_tree(args.source_install, install, args.timeout, log)
        missing = test_install(install)
        state = "broken" if missing else "ready"
        entry = new_entry(target, install, state, missing)
        verdict: dict[str, Any] = {
            "verb": "clone", "poolRoot": str(root), "slot": target, "state": state,
            "install": str(install), "session": session, "cloneMethod": clone_method,
            "deleteRemedy": remedy, "missing": list(missing), "serverUrl": None,
            "ok": state == "ready",
            # The PowerShell original's exit 4 for a clone that finished and failed verification
            # (live-slot.ps1:244), preserved because a caller that polls for it must keep working.
            "exitCode": EXIT_OK if state == "ready" else EXIT_CLONE_BROKEN,
        }
        # Stamp the slot's OWN port, but only an install that verified: a broken install is about to
        # be re-cloned and has no game to misdirect. A stamp refusal still RECORDS the slot, as
        # `broken` with the reason in its notes -- persisting the diagnosis costs no claim (a clone
        # claims nothing) and keeps the registry the one place the next agent looks.
        if state == "ready":
            try:
                verdict["serverUrl"] = stamp_server_url(install, target,
                                                       resolve_port(None, args.base_port, target),
                                                       log)
            except Refusal as refusal:
                entry["state"] = "broken"
                entry["notes"] = [f"cfg: {refusal.name}"]
                verdict["state"] = "broken"
                verdict["ok"] = False
                verdict["refusal"] = {"name": refusal.name, "detail": refusal.detail}
                registry["slots"] = [e for e in registry.get("slots", [])
                                     if _slot_of(e) != target] + [entry]
                if args.max_slots is not None:
                    registry["maxSlots"] = args.max_slots
                write_registry(registry_path, registry, log)
                raise
        registry["slots"] = [e for e in registry.get("slots", []) if _slot_of(e) != target] + [entry]
        if args.max_slots is not None:
            registry["maxSlots"] = args.max_slots
        write_registry(registry_path, registry, log)
    if state == "broken":
        log(f"[{TOOL}] slot {target} BROKEN -- missing: {', '.join(missing)}")
        return verdict
    log(f"[{TOOL}] slot {target} READY (cloned from {args.source_install}, verified: "
        f"{', '.join(REQUIRED_ENTRIES)})  cloner={session}")
    return verdict


def _slot_of(entry: dict[str, Any]) -> int:
    try:
        return int(entry.get("slot"))
    except (TypeError, ValueError):
        return -1


def cmd_acquire(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    session = require_session(args.session, "Acquire")
    root = args.pool
    registry_path = root / "slots.json"
    if args.slot:
        # The original ignores --slot on acquire (it takes the lowest ready slot, then the first
        # uncloned one) and `gk-fusion/scripts/prove-slot-connection.py:452` depends on that. The pick is
        # preserved; ignoring the flag silently is not, so it is announced.
        log(f"[{TOOL}] note: --acquire picks its own slot (the lowest ready, else the first "
            f"uncloned) and ignores --slot {args.slot}, exactly as the PowerShell original does; "
            f"use --clone --slot {args.slot} to target a specific slot")
    with slot_lock(root / "slots.lock", registry_path, session, args.lock_timeout_seconds,
                   args.stale_lock_minutes, log):
        registry = read_registry(registry_path, default_max_slots(args))
        mine = [e for e in registry.get("slots", [])
                if e.get("session") == session and e.get("state") == "occupied"]
        if mine:
            log(f"[{TOOL}] session {session} already holds slot {mine[0]['slot']} at "
                f"{mine[0]['installPath']}")
            return {"verb": "acquire", "poolRoot": str(root), "slot": mine[0]["slot"],
                    "state": "occupied", "install": mine[0]["installPath"], "session": session,
                    "alreadyHeld": True, "ok": True}
        cap = args.max_slots if args.max_slots is not None else int(registry.get("maxSlots", 3))
        cap = max(cap, 1)
        occupied = [e for e in registry.get("slots", []) if e.get("state") == "occupied"]
        if len(occupied) >= cap:
            raise Refusal("ALL-SLOTS-HELD",
                          f"all {cap} live slots are held "
                          f"({', '.join(str(e.get('session')) for e in occupied)})")
        ready = sorted((e for e in registry.get("slots", []) if e.get("state") == "ready"),
                       key=_slot_of)
        clone_method = None
        if ready:
            pick = ready[0]
        else:
            number = 1
            taken = taken_slots(registry)
            while number in taken and number <= cap:
                number += 1
            install = slot_install(root, number)
            if not install.exists():
                if not args.source_install:
                    raise Refusal("SOURCE-INSTALL-MISSING",
                                  f"slot {number} has no install and no source to clone: pass "
                                  f"--source-install or set FUSIONRPG_GAME_SOURCE")
                log(f"[{TOOL}] cloning {args.source_install} -> {install} (first use of slot {number})")
                clone_method = clone_tree(args.source_install, install, args.timeout, log)
            missing = test_install(install)
            pick = new_entry(number, install, "broken" if missing else "ready", missing)
            registry["slots"] = [e for e in registry.get("slots", []) if _slot_of(e) != number]
            registry["slots"].append(pick)
        slot = _slot_of(pick)
        install = install_of(pick, root, slot)
        if pick.get("state") == "broken":
            if args.max_slots is not None:
                registry["maxSlots"] = args.max_slots
            write_registry(registry_path, registry, log)
            raise Refusal("SLOT-BROKEN",
                          f"slot {slot} is broken "
                          f"({'; '.join(str(n) for n in pick.get('notes') or [])}); "
                          f"re-clone with --clone --slot {slot} --force")
        port = resolve_port(pick, args.base_port, slot)
        # Stamp BEFORE the claim, so a refusal costs no slot (the rule
        # gk-fusion/scripts/prove-slot-connection.py:389 was written for).
        stamp = stamp_server_url(install, slot, port, log)
        pick["state"] = "occupied"
        pick["session"] = session
        pick["acquiredAt"] = now_iso()
        pick["releasedAt"] = None
        pick["port"] = port
        if args.max_slots is not None:
            registry["maxSlots"] = args.max_slots
        write_registry(registry_path, registry, log)
    log(f"[{TOOL}] ACQUIRED slot {slot} for {session} at {install}")
    log(f"[{TOOL}] YOUR SERVER PORT: {port} -- start it with: "
        f"pwsh -NoProfile -File scripts/lane-server.ps1 -Start -Slot {slot}")
    log(f"[{TOOL}] deploy into THAT path (inline: FUSIONRPG_ML_GAMEDIR=<slot> python "
        f"scripts/deploy-play.py ...) and use your own loopback port, never :{OWNER_PORT}")
    if port == OWNER_PORT:
        log(f"[{TOOL}] WARNING: this slot resolves to the OWNER's port :{OWNER_PORT} "
            f"(--base-port {args.base_port} + slot {slot}); lane-server.ps1 will refuse it")
    return {"verb": "acquire", "poolRoot": str(root), "slot": slot, "state": "occupied",
            "install": str(install), "session": session, "port": port, "serverUrl": stamp,
            "cloneMethod": clone_method, "alreadyHeld": False, "ok": True}


def cmd_release(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    session = require_session(args.session, "Release")
    root = args.pool
    registry_path = root / "slots.json"
    with slot_lock(root / "slots.lock", registry_path, session, args.lock_timeout_seconds,
                   args.stale_lock_minutes, log):
        registry = read_registry(registry_path, default_max_slots(args))
        mine = [e for e in registry.get("slots", [])
                if e.get("session") == session and e.get("state") == "occupied"]
        if not mine:
            log(f"[{TOOL}] session {session} holds no slot; nothing to release")
        released: list[int] = []
        for entry in mine:
            install = install_of(entry, root, _slot_of(entry))
            live = slot_processes(install, args.process_timeout) if install.exists() else []
            if live:
                log(f"[{TOOL}] WARNING: PID {live[0]['pid']} is still running from {install} -- "
                    f"releasing anyway (the slot is only free once you close your game)")
            entry["state"] = "ready"
            entry["session"] = None
            entry["releasedAt"] = now_iso()
            entry["process"] = None
            released.append(_slot_of(entry))
        if args.max_slots is not None:
            registry["maxSlots"] = args.max_slots
        write_registry(registry_path, registry, log)
        free = len([e for e in registry.get("slots", []) if e.get("state") == "ready"])
    log(f"[{TOOL}] RELEASED {len(mine)} slot(s) for {session}; ready-to-claim now: {free}")
    return {"verb": "release", "poolRoot": str(root), "session": session, "released": released,
            "ready": free, "ok": True}


def cmd_reclaim(args: argparse.Namespace, log: Logger) -> dict[str, Any]:
    if args.slot <= 0:
        raise Refusal("SLOT-REQUIRED", "--reclaim needs --slot <n>")
    root = args.pool
    registry_path = root / "slots.json"
    with slot_lock(root / "slots.lock", registry_path, session=args.session or "manager",
                   timeout_seconds=args.lock_timeout_seconds,
                   stale_minutes=args.stale_lock_minutes, log=log):
        registry = read_registry(registry_path, default_max_slots(args))
        entry = entry_for(registry, args.slot)
        if entry is None:
            raise Refusal("SLOT-UNKNOWN", f"slot {args.slot} does not exist in the registry")
        install = install_of(entry, root, args.slot)
        live = slot_processes(install, args.process_timeout) if install.exists() else []
        if live and not args.force:
            raise Refusal("SLOT-STILL-RUNNING",
                          f"a game is still running from {install} (PID {live[0]['pid']})")
        previous = entry.get("session")
        entry["state"] = "ready"
        entry["session"] = None
        entry["releasedAt"] = now_iso()
        entry["process"] = None
        entry.setdefault("notes", []).append(
            f"reclaimed from '{previous}' by '{args.session}' at {now_iso()}")
        write_registry(registry_path, registry, log)
    log(f"[{TOOL}] RECLAIMED slot {args.slot} (was held by '{previous}'); recorded in the "
        f"registry notes")
    return {"verb": "reclaim", "poolRoot": str(root), "slot": args.slot, "state": "ready",
            "previousSession": previous, "session": args.session, "ok": True}


# ------------------------------------------------------------------------------------------------
# CLI
# ------------------------------------------------------------------------------------------------

VERBS = {
    "status": cmd_status,
    "clone": cmd_clone,
    "acquire": cmd_acquire,
    "release": cmd_release,
    "reclaim": cmd_reclaim,
}


def build_parser() -> argparse.ArgumentParser:
    """Every flag answers to BOTH spellings: `--session` is canonical, `-Session` is the
    compatibility alias the original's callers (and `prove-slot-connection.py`) already use. That
    is what makes re-pointing the one live code caller a one-token change.
    """
    parser = argparse.ArgumentParser(
        prog=f"scripts/{TOOL}.py",
        description="Manage the live-probe game pool: clone a game install into a slot, claim it, "
                    "see who holds what, hand it back.",
        epilog="usage: live_slot.py -Status | -Clone -Session <id> [-Slot n] [-Force] | "
               "-Acquire -Session <id> | -Release -Session <id> | -Reclaim -Slot <n> "
               "-Session <id>\n"
               "pool from -PoolRoot or $FUSIONRPG_GAME_POOL; source install from -SourceInstall or "
               "$FUSIONRPG_GAME_SOURCE; each slot's SERVER port is -BasePort + slot "
               "(scripts/lane-server.ps1 -Start -Slot n)",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-Status", "--status", dest="status", action="store_true",
                        help="report every slot: state, holder, install, age, live PID, port")
    parser.add_argument("-Clone", "--clone", dest="clone", action="store_true",
                        help="populate a free slot and VERIFY it (once per slot)")
    parser.add_argument("-Acquire", "--acquire", dest="acquire", action="store_true",
                        help="claim a slot (clones on first use if needed)")
    parser.add_argument("-Release", "--release", dest="release", action="store_true",
                        help="hand a slot back: state ready, install KEPT for reuse")
    parser.add_argument("-Reclaim", "--reclaim", dest="reclaim", action="store_true",
                        help="force-release a stale claim, after reading -Status")
    parser.add_argument("-Session", "--session", dest="session", default="",
                        help="the session id recorded as the cloner/holder")
    parser.add_argument("-Slot", "--slot", dest="slot", type=int, default=0,
                        help="target slot number (ignored by -Acquire, exactly as the original does)")
    parser.add_argument("-PoolRoot", "--pool-root", dest="pool_root",
                        default=os.environ.get("FUSIONRPG_GAME_POOL", ""),
                        help="pool root (default $FUSIONRPG_GAME_POOL)")
    parser.add_argument("-SourceInstall", "--source-install", dest="source_install",
                        default=os.environ.get("FUSIONRPG_GAME_SOURCE", ""),
                        help="the game install a slot is cloned from")
    parser.add_argument("-MaxSlots", "--max-slots", dest="max_slots", type=int, default=None,
                        help="the pool's slot count (default 3; only written when passed, exactly "
                             "as the original's $PSBoundParameters check did)")
    parser.add_argument("-BasePort", "--base-port", dest="base_port", type=int, default=5100,
                        help="slot N's SERVER port is BasePort + N; the owner keeps their own")
    parser.add_argument("-LockTimeoutSeconds", "--lock-timeout-seconds", dest="lock_timeout_seconds",
                        type=int, default=60, help="how long to wait for the slot lock")
    parser.add_argument("-StaleLockMinutes", "--stale-lock-minutes", dest="stale_lock_minutes",
                        type=int, default=15, help="a lock older than this is a crashed holder")
    parser.add_argument("-StaleClaimMinutes", "--stale-claim-minutes", dest="stale_claim_minutes",
                        type=int, default=240, help="a claim held this long with no process is stale")
    parser.add_argument("-Force", "--force", dest="force", action="store_true",
                        help="-Clone: re-clone over an existing install. -Reclaim: reclaim while a "
                             "game is running. Both are 'I am certain' flags.")
    parser.add_argument("--timeout", dest="timeout", type=float, default=1800.0,
                        help="hard budget in seconds for each external call (robocopy clone/delete)")
    parser.add_argument("--process-timeout", dest="process_timeout", type=float, default=30.0,
                        help="hard budget in seconds for the process enumeration")
    parser.add_argument("-Json", "--json", dest="json", action="store_true",
                        help="also print the machine-readable verdict")
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
