#!/usr/bin/env python3
"""Take, release or read the one-session-at-a-time lock on a game install.

A game install can host one live probe at a time: a deploy swaps the injector DLL and a second session's
deploy would pull it out from under a running probe. The lock is a small JSON file,
``<GameDir>/fusionrpg-session.lock``, naming the session that holds it and the absolute path of that
session's record. `deploy-play.py` asks `--status` before it deploys and refuses a held install
(docs/contributing/creative-mode.md 8.3).

The record path is stored because sessions run in different worktrees: each worktree has its own
`tasks/sessions/`, so a caller looking in its own tree would not see a sibling's record and would
wrongly call a live lock stale. A lock whose stored record is missing or no longer `active` is stale --
its holder has ended -- and `--acquire` takes it over, saying so.

`--acquire` creates the lock file with `O_CREAT | O_EXCL`, so two sessions acquiring at the same moment
cannot both win.

Replaces `game-lock.ps1`.

Exit codes, and WHY they are FOUR rather than the original's two:
    0  OK               the requested transition happened, or the status is usable by `--session`
    1  HELD             another LIVE session holds the install. Not an error in the caller.
    2  REFUSED          a caller error: no `--game-dir`, or `--acquire`/`--release` with no `--session`.
    3  UNREADABLE       a lock file or a session record exists but does not parse. FAIL CLOSED.

The original returned **1 for both "another live session holds this" and "you passed the wrong
arguments"**, and had no way to say the third thing at all. A deploy that treated those identically
would report "someone else is probing this install" for a typo.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **AN UNPARSEABLE LOCK FILE CRASHED THE TOOL.** `ConvertFrom-Json` on a partially-written file throws,
  and `$ErrorActionPreference = 'Stop'` turned that into an unhandled error with no verdict. The window
  is REAL, not theoretical: `--acquire` creates the file with `CreateNew` and then writes the body, so a
  concurrent reader between create and write reads an EMPTY file. The port refuses with `LOCK-UNREADABLE`
  and exits 3, which fails CLOSED -- the install is treated as held until a human or the holder resolves
  it -- because guessing "free" from a parse failure is how two sessions end up in one install.

* **THE SAME CRASH EXISTED FOR THE SESSION RECORD**, one level down, with no diagnostic naming which file
  failed to parse.

* **THE RECORD PATH WAS BUILT WITH A HARDCODED BACKSLASH**: `"tasks\\sessions\\$Session.json"`. That works
  on Windows and produces a path with a literal backslash in it on every other platform, where the
  session record then simply does not exist -- so every lock looks STALE and `--acquire` steals a live
  install. A stale-looking lock on a POSIX checkout is not a stale lock.

* **NO MACHINE-READABLE OUTPUT.** Every answer was a `Write-Host` sentence, so a caller could only branch
  on the exit code, and two of the four answers shared one.

WHAT THIS TOOL MUST NOT DO
---------------------------
It must never treat an unreadable or unknown holder as FREE. `--acquire` on an install whose state cannot
be read is the one operation where guessing wrong lets two sessions share a game.
"""
from __future__ import annotations

import argparse
import errno
import json
import os
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "game-lock"
EXIT_OK = 0
EXIT_HELD = 1
EXIT_REFUSED = 2
EXIT_UNREADABLE = 3

LOCK_FILENAME = "fusionrpg-session.lock"
EXIT_VOCABULARY = {EXIT_OK, EXIT_HELD, EXIT_REFUSED, EXIT_UNREADABLE}


class Refusal(Exception):
    """A named precondition or read failure, carrying the exit code its KIND deserves."""

    def __init__(self, reason: str, detail: str, exit_code: int = EXIT_REFUSED) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


@dataclass
class Answer:
    """What the tool decided. `verdict` is a CLOSED vocabulary; `message` is prose around it."""

    verdict: str  # FREE | HELD | STALE | OWN | ACQUIRED | RELEASED | ALREADY-HELD | UNLOCKED
    ok: bool
    exit: int
    holder: str | None = None
    acquired_at: str | None = None
    record: str | None = None
    lock_file: str = ""
    message: str = ""
    notes: list[str] = field(default_factory=list)


def read_json(path: Path, what: str) -> dict:
    """Parse a JSON file, refusing BY NAME rather than letting a parse error escape.

    `what` is the reason PREFIX, so the refusals read `LOCK-UNREADABLE` and `RECORD-UNREADABLE`. The
    first version passed the display name "lock file", which produced reasons containing a space --
    `lock file-UNREADABLE` -- and a reason name is something a caller branches on and something a
    contract enumerates, so it has to be a token.

    The distinction between "absent" and "present but unreadable" is the whole point: absent means the
    state is knowable, unreadable means it is not, and only one of those two may be answered optimistically.
    """
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        raise
    except OSError as exc:
        raise Refusal(f"{what}-UNREADABLE", f"cannot read the {what.lower()} {path}: {exc}",
                      EXIT_UNREADABLE) from exc
    if not text.strip():
        # The real shape of this: `--acquire` creates the file with O_EXCL and writes the body after, so a
        # reader in between sees an EMPTY file. Naming it beats "unexpected end of JSON input".
        raise Refusal(f"{what}-EMPTY",
                      f"the {what.lower()} {path} exists but is empty; a concurrent acquirer may be "
                      f"mid-write, so the install is treated as HELD rather than free",
                      EXIT_UNREADABLE)
    try:
        document = json.loads(text)
    except json.JSONDecodeError as exc:
        raise Refusal(f"{what}-UNREADABLE",
                      f"the {what.lower()} {path} is not valid JSON ({exc}); the install is treated as "
                      f"HELD rather than free", EXIT_UNREADABLE) from exc
    if not isinstance(document, dict):
        raise Refusal(f"{what}-SHAPE", f"the {what.lower()} {path} is not a JSON object",
                      EXIT_UNREADABLE)
    return document


def holder_is_live(lock: dict, repo_root: Path) -> tuple[bool, str | None]:
    """Is the session named by this lock still `active`?

    A lock without a stored record path predates the field; it falls back to THIS tree's record, which is
    what the original did and is the only answer available for such a lock.
    """
    session = str(lock.get("session") or "")
    stored = lock.get("record")
    record = Path(str(stored)) if stored else repo_root / "tasks" / "sessions" / f"{session}.json"
    if not record.is_file():
        return False, str(record)
    document = read_json(record, "RECORD")
    return str(document.get("status") or "") == "active", str(record)


def read_lock(lock_file: Path) -> dict | None:
    """The lock document, or None when there is no lock file at all."""
    if not lock_file.exists():
        return None
    return read_json(lock_file, "LOCK")


def acquire(lock_file: Path, session: str, repo_root: Path) -> bool:
    """Create the lock atomically. False means another acquirer won the race.

    `O_EXCL` with `O_CREAT` is the whole mechanism: the kernel makes create-if-absent a single atomic
    step, so two acquirers cannot both observe "absent" and both proceed. There is deliberately NO retry
    loop -- a version of this had a deadline that the unconditional returns below made unreachable, which
    is the worst kind of backstop: it reads as protection and protects nothing.
    """
    body = json.dumps({
        "session": session,
        # os.path.join, not "tasks\\sessions\\..." -- a hardcoded backslash produces a path with a
        # literal backslash on POSIX, so the record never resolves and every lock reads as stale.
        "record": str(repo_root / "tasks" / "sessions" / f"{session}.json"),
        "acquiredAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }, indent=2)
    try:
        fd = os.open(lock_file, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        return False
    except OSError as exc:
        if exc.errno == errno.EEXIST:
            return False
        raise Refusal("LOCK-UNWRITABLE", f"cannot create {lock_file}: {exc}", EXIT_REFUSED) from exc
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(body)
    except OSError as exc:
        # A lock file that exists but cannot be written is worse than none: report it rather than
        # leaving a zero-byte lock that the next reader has to interpret.
        raise Refusal("LOCK-UNWRITABLE",
                      f"created {lock_file} but could not write it: {exc}", EXIT_REFUSED) from exc
    return True


def status(lock_file: Path, session: str, repo_root: Path) -> Answer:
    lock = read_lock(lock_file)
    if lock is None:
        return Answer("FREE", True, EXIT_OK, lock_file=str(lock_file),
                      message=f"{lock_file.parent} is free")
    holder = str(lock.get("session") or "")
    acquired = lock.get("acquiredAt")
    if session and holder == session:
        return Answer("OWN", True, EXIT_OK, holder=holder, acquired_at=acquired,
                      record=str(lock.get("record") or ""), lock_file=str(lock_file),
                      message=f"{lock_file.parent} is held by '{holder}' (you)")
    live, record = holder_is_live(lock, repo_root)
    if not live:
        return Answer("STALE", True, EXIT_OK, holder=holder, acquired_at=acquired, record=record or "",
                      lock_file=str(lock_file),
                      message=f"{lock_file.parent} is held by '{holder}' since {acquired} (stale: no "
                              f"active session record)")
    return Answer("HELD", False, EXIT_HELD, holder=holder, acquired_at=acquired, record=record or "",
                  lock_file=str(lock_file),
                  message=f"{lock_file.parent} is held by '{holder}' since {acquired}")


def do_acquire(lock_file: Path, session: str, repo_root: Path) -> Answer:
    lock = read_lock(lock_file)
    notes: list[str] = []
    if lock is not None:
        holder = str(lock.get("session") or "")
        if holder == session:
            return Answer("ALREADY-HELD", True, EXIT_OK, holder=holder,
                          acquired_at=lock.get("acquiredAt"), lock_file=str(lock_file),
                          message=f"{session} already holds {lock_file.parent}")
        live, record = holder_is_live(lock, repo_root)
        if live:
            return Answer("HELD", False, EXIT_HELD, holder=holder, acquired_at=lock.get("acquiredAt"),
                          record=record or "", lock_file=str(lock_file),
                          message=f"{lock_file.parent} is held by '{holder}' since "
                                  f"{lock.get('acquiredAt')}")
        lock_file.unlink()
        # The takeover is the one action here that DISCARDS another session's claim, so it has to be
        # reported, not inferred from the absence of an error. A differential against the original caught
        # this: the original prints "taking over a stale lock: '<holder>' has no active session record"
        # and the port's single-line message did not, so a takeover and a fresh acquire were
        # indistinguishable in the log and in `--json`.
        notes.append(f"took over a stale lock from '{holder}': no active session record")
    if not acquire(lock_file, session, repo_root):
        winner = read_lock(lock_file) or {}
        return Answer("HELD", False, EXIT_HELD, holder=str(winner.get("session") or "?"),
                      acquired_at=winner.get("acquiredAt"), lock_file=str(lock_file), notes=notes,
                      message=f"lost the race for {lock_file.parent} to "
                              f"'{winner.get('session')}'")
    return Answer("ACQUIRED", True, EXIT_OK, holder=session, lock_file=str(lock_file), notes=notes,
                  message=f"{session} holds {lock_file.parent}")


def do_release(lock_file: Path, session: str) -> Answer:
    lock = read_lock(lock_file)
    if lock is None:
        # Releasing a free install is not an error: the postcondition holds.
        return Answer("UNLOCKED", True, EXIT_OK, lock_file=str(lock_file),
                      message=f"{lock_file.parent} was not locked")
    holder = str(lock.get("session") or "")
    if holder != session:
        return Answer("HELD", False, EXIT_HELD, holder=holder,
                      acquired_at=lock.get("acquiredAt"), lock_file=str(lock_file),
                      message=f"refusing to release: {lock_file.parent} is held by '{holder}', not "
                              f"'{session}'")
    lock_file.unlink()
    return Answer("RELEASED", True, EXIT_OK, holder=session, lock_file=str(lock_file),
                  message=f"{session} released {lock_file.parent}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Take, release or read the one-session lock on a game install "
                    "(replaces game-lock.ps1).")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--acquire", action="store_true")
    action.add_argument("--release", action="store_true")
    action.add_argument("--status", action="store_true")
    parser.add_argument("--game-dir", required=True,
                        help="the game install; the lock is <game-dir>/fusionrpg-session.lock")
    parser.add_argument("--session", default="", help="required to acquire or release")
    parser.add_argument("--repo-root", type=Path, default=None)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    repo_root = (args.repo_root or Path(__file__).resolve().parent.parent).resolve()
    try:
        game_dir = Path(args.game_dir).expanduser()
        if not game_dir.is_dir():
            raise Refusal("GAME-DIR-MISSING", f"no such game directory: {args.game_dir}")
        # Normalised so `H:\Games\x` and `H:/Games/x` name the SAME lock; the original compared and
        # prefixed raw strings, so two spellings of one install were two locks.
        game_dir = game_dir.resolve()
        if (args.acquire or args.release) and not args.session.strip():
            raise Refusal("SESSION-REQUIRED", "--session is required to acquire or release")
        lock_file = game_dir / LOCK_FILENAME
        if args.acquire:
            answer = do_acquire(lock_file, args.session, repo_root)
        elif args.release:
            answer = do_release(lock_file, args.session)
        else:
            answer = status(lock_file, args.session, repo_root)
    except Refusal as refusal:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                   "detail": refusal.detail, "exitCode": refusal.exit_code}
        if args.json:
            print(json.dumps(payload, indent=2))
        else:
            print(f"[game-lock] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code
    except OSError as exc:
        # A filesystem that says no for a reason no precondition covered. Still named, still non-zero.
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": "FILESYSTEM-ERROR",
                   "detail": str(exc), "exitCode": EXIT_REFUSED}
        if args.json:
            print(json.dumps(payload, indent=2))
        else:
            print(f"[game-lock] REFUSED: FILESYSTEM-ERROR: {exc}", file=sys.stderr)
        return EXIT_REFUSED

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK", **answer.__dict__}, indent=2))
    else:
        print(f"[game-lock] {answer.message}")
        for note in answer.notes:
            print(f"[game-lock]   {note}")
    return answer.exit


if __name__ == "__main__":
    sys.exit(main())
