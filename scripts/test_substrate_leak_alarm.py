#!/usr/bin/env python3
"""Alarm: a test run must not survive a temp dir, and must not leave an `rpg-*.sqlite` behind.

Wraps a command, snapshots the test substrate before and after, and refuses a run that left anything
behind. Companion to the static gate `gk-core/scripts/guard-test-substrate.py` (module disk-write-probe, T19b):
that one refuses a bad SOURCE pattern at authoring time; this one refuses a run that ACTUALLY leaked --
a temp dir, or an `rpg-*.sqlite` the source pattern would not catch.

Replaces `scripts/test-substrate-leak-alarm.ps1`.

Usage
-----
    python gk-core/scripts/test_substrate_leak_alarm.py --run dotnet test gk-core/tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj -c Release --no-build

`--run` takes the rest of the command line verbatim. A bare `--` separator is NOT accepted -- argparse
rejects it, and a form that looks conventional but silently loses its tail is worse than a form that
fails. The tool's own flags therefore come BEFORE `--run`.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **AN UNREADABLE SNAPSHOT ROOT READ AS "NO LEAK".** Both snapshots were `Get-ChildItem ...
  -ErrorAction SilentlyContinue`. An inaccessible or missing temp root therefore produced an EMPTY set,
  the set difference was empty, and the alarm reported OK having observed nothing -- the exact failure
  mode where the detector's blindness reads as the subject's cleanliness. Both snapshots now REFUSE if
  the root cannot be read, and the refusal names the path. A real run covers it: pointed at a
  non-existent `--temp-root`, the port exits 64 with `TEMP-ROOT-UNREADABLE`.

* **THE PRIVATE TEMP ROOT'S OWN DELETE WAS SWALLOWED.** `Remove-Item ... -ErrorAction SilentlyContinue`
  on the `-IsolateTemp` root. A root that could not be removed is a leak of the alarm's own making, and
  this repository's testing standard names a swallowed delete as the cause of a 65.5 GB leak. It is now
  a refusal reported with the path, and `privateRootRemoved` says which of the two happened.

* **NO TIMEOUT ON THE WRAPPED RUN.** `--blame-hang-timeout` bounds `dotnet test`; nothing bounded the
  alarm, and a run that hangs holds CI open indefinitely. `--timeout` now bounds it, and a timeout is a
  named result rather than a hang.

* **NO MACHINE-READABLE VERDICT, AND THE RUN'S OWN OUTPUT WAS UNREACHABLE.** A caller grepped prose.
  `--json` now reports the leak lists, the wrapped command's exit code, the snapshot sizes, and a
  BOUNDED tail of the wrapped run's output -- so a caller told "the wrapped command exited 1" can see
  why without re-running the whole suite by hand.

WHAT CHANGED IN THE INTERFACE, AND WHAT DID NOT
-----------------------------------------------
`[scriptblock]$Run` becomes `--run` ARGV. The obvious defect to claim here is that the original's
verdict read `$LASTEXITCODE` -- the LAST NATIVE command's exit code -- so a scriptblock whose first
command failed and whose last succeeded would report clean. **That was measured and it did NOT
reproduce:** the original exits 1 for `{ exit 3; exit 0 }`. The claim is therefore WITHDRAWN rather than
shipped as a finding, and the interface change is described here as what it is -- a narrowing from "a
sequence of commands" to "one command" -- and not as a fix for a bug that was not shown to exist.

On all five leak scenarios the two implementations AGREE, exit code for exit code, driven over one
shared fixture with the wrapped command asserted to have really run on both sides.

WHAT IS DELIBERATELY KEPT
-------------------------
The snapshot is a **set difference, never a count**. A pre-existing directory is not a new leak, and an
equal count must not pass while the membership differs. The original got this right and so does this:
standard `docs/contributing/testing-standard.md` and `docs/architecture/validation-ssot.md`.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "test-substrate-leak-alarm"

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

# The store/helper prefix. Read from one place: two tools watching two prefixes is how one of them ends
# up watching nothing and still reporting OK.
TEMP_DIR_GLOB = "fusionrpg-*"
SQLITE_GLOB = "rpg-*.sqlite"
SQLITE_OUTPUT_DIRS = ("bin", "TestResults")

# The wrapped run is a whole test suite; the bound is generous and exists so CI cannot hang. The nightly
# passes `--blame-hang-timeout 15min` INSIDE the command, so this default sits above that.
DEFAULT_TIMEOUT = 3600
DEFAULT_SNAPSHOT_TIMEOUT = 120

# How much of the wrapped run's own output travels in the envelope. Bounded on purpose: a full
# `dotnet test` transcript is megabytes, and an envelope that embeds one is an envelope nobody reads.
RUN_OUTPUT_TAIL = 8000

STANDARD = "Standard: docs/contributing/testing-standard.md"

# Bound once, module-private: the process-wide `subprocess` and `shutil` must never be patched by a
# test of this tool, because a patch that outlives its `with` block breaks every other test in the
# process. Proven the hard way by this program's own `test_dump_melon_p0.py`, which shipped with exactly
# that mistake and made 506 unrelated failures in `test_ps1_port_census.py`.
_RUN = subprocess.run
_RMTREE = shutil.rmtree
_WHICH = shutil.which

REFUSAL_REASONS = {
    "NO-WRAPPED-RUN", "INVALID-TIMEOUT", "TESTS-DIR-MISSING", "TEMP-ROOT-UNREADABLE",
    "TESTS-DIR-UNREADABLE", "PRIVATE-TEMP-ROOT-NOT-CLEANED", "WRAPPED-RUN-NOT-FOUND",
    "WRAPPED-RUN-TIMED-OUT",
}


class Refusal(Exception):
    """A named precondition or observation failure. Never reports OK having not observed."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


@dataclass
class Report:
    wrapped: list[str] = field(default_factory=list)
    run_exit: int = 0
    run_timed_out: bool = False
    run_output: str = ""
    temp_before: int = 0
    temp_after: int = 0
    sqlite_before: int = 0
    sqlite_after: int = 0
    leaked_dirs: list[str] = field(default_factory=list)
    new_sqlite: list[str] = field(default_factory=list)
    isolated_temp: str = ""
    private_root_removed: bool | None = None
    refused: tuple[str, str] | None = None

    @property
    def ok(self) -> bool:
        return (self.refused is None and not self.run_timed_out and self.run_exit == 0
                and not self.leaked_dirs and not self.new_sqlite
                and self.private_root_removed is not False)

    @property
    def reasons(self) -> list[str]:
        out: list[str] = []
        if self.run_timed_out:
            out.append("the wrapped run exceeded its timeout")
        if self.run_exit != 0:
            out.append(f"the wrapped command exited {self.run_exit}")
        for d in self.leaked_dirs:
            out.append(f"temp dir survived: {d}")
        for s in self.new_sqlite:
            out.append(f"rpg sqlite left: {s}")
        if self.private_root_removed is False:
            out.append(f"the alarm's own private temp root could not be removed: {self.isolated_temp}")
        return out


def temp_snapshot(root: Path) -> set[str]:
    """Directory NAMES matching the store prefix. A name set, so membership can be diffed.

    REFUSES when the root cannot be read. The original used `-ErrorAction SilentlyContinue`, so an
    inaccessible root produced an empty set and the alarm reported OK having observed nothing -- a
    detector's blindness reading as the subject's cleanliness.
    """
    if not root.is_dir():
        raise Refusal("TEMP-ROOT-UNREADABLE",
                      f"{root} is not a directory. The alarm refuses rather than reporting no leak: an "
                      f"empty snapshot is indistinguishable from a clean run.")
    try:
        return {entry.name for entry in root.iterdir()
                if entry.is_dir() and entry.name.startswith("fusionrpg-")}
    except OSError as error:
        raise Refusal("TEMP-ROOT-UNREADABLE", f"{root} could not be listed: {error}") from error


def sqlite_snapshot(tests_dir: Path) -> set[str]:
    """`rpg-*.sqlite` under each test project's `bin/` and `TestResults/`, recursively.

    The full path is the member, not the name: two projects can each hold a file of the same name and
    they are two survivors, not one.
    """
    if not tests_dir.is_dir():
        raise Refusal("TESTS-DIR-MISSING", f"{tests_dir} is not a directory")
    found: set[str] = set()
    try:
        projects = sorted(entry for entry in tests_dir.iterdir() if entry.is_dir())
    except OSError as error:
        raise Refusal("TESTS-DIR-UNREADABLE", f"{tests_dir} could not be listed: {error}") from error
    for project in projects:
        for name in SQLITE_OUTPUT_DIRS:
            out_root = project / name
            if not out_root.is_dir():
                continue
            for path in out_root.rglob(SQLITE_GLOB):
                if path.is_file():
                    found.add(str(path))
    return found


def resolve_wrapped(argv: list[str]) -> str:
    """The executable to spawn, RESOLVED ONCE.

    `CreateProcess` resolves a bare name by appending `.exe` only, so spawning the bare token can reach
    a different tool than the one that was resolved -- and the nightly's command is `dotnet`, which on
    Windows is fine but is not fine everywhere.
    """
    head = argv[0]
    if any(sep in head for sep in ("/", "\\")) or Path(head).is_absolute():
        return head
    found = _WHICH(head)
    if not found:
        raise Refusal("WRAPPED-RUN-NOT-FOUND",
                      f"{head!r} is not on PATH. The alarm names the tool it could not start rather "
                      f"than reporting a clean run it never performed.")
    return found


def run_wrapped(argv: list[str], timeout: int, cwd: Path) -> tuple[int, bool, str]:
    """One bounded run of the wrapped command. A timeout is RETURNED, not raised, so the report still
    reaches the leak comparison -- a hung run is also a run that left everything behind."""
    try:
        proc = _RUN(argv, capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
        return proc.returncode, False, (proc.stdout or "") + (proc.stderr or "")
    except subprocess.TimeoutExpired as expired:
        partial = expired.stdout or b""
        text = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        return EXIT_FAILED, True, f"{text}\n[no result within {timeout}s]"
    except (OSError, FileNotFoundError) as error:
        raise Refusal("WRAPPED-RUN-NOT-FOUND", str(error)) from error


def execute(root: Path, temp_root: Path, wrapped: list[str], isolate_temp: bool,
            timeout: int) -> Report:
    report = Report(wrapped=list(wrapped))
    tests_dir = root / "tests"
    if not tests_dir.is_dir():
        raise Refusal("TESTS-DIR-MISSING", f"tests/ missing: {tests_dir}")

    # Own the wrapped run's temp root when asked, so no other process can pollute the snapshot. The
    # original's fix for a shared-machine false positive: another agent's concurrent run created and
    # removed its own fusionrpg-* dirs inside the before/after window and they read as leaks. Measured:
    # a 26-test Server run reported ~108 survivors that all belonged to a concurrent Data.Tests run.
    saved_env: dict[str, str | None] = {}
    owned: Path | None = None
    if isolate_temp:
        owned = Path(tempfile.gettempdir()) / f"test-substrate-alarm-{uuid.uuid4().hex}"
        try:
            owned.mkdir(parents=True)
        except OSError as error:
            raise Refusal("TEMP-ROOT-UNREADABLE", f"could not create {owned}: {error}") from error
        report.isolated_temp = str(owned)
        report.private_root_removed = None
        temp_root = owned
        for variable in ("TEMP", "TMP"):
            saved_env[variable] = os.environ.get(variable)
            os.environ[variable] = str(owned)

    try:
        before_dirs = temp_snapshot(temp_root)
        before_sqlite = sqlite_snapshot(tests_dir)
        report.temp_before = len(before_dirs)
        report.sqlite_before = len(before_sqlite)

        report.run_exit, report.run_timed_out, report.run_output = run_wrapped(
            [resolve_wrapped(wrapped), *wrapped[1:]], timeout, root)

        after_dirs = temp_snapshot(temp_root)
        after_sqlite = sqlite_snapshot(tests_dir)
        report.temp_after = len(after_dirs)
        report.sqlite_after = len(after_sqlite)
    finally:
        # Restore the ambient temp env FIRST, so a failure below cannot leave the process pointing at a
        # directory this alarm is about to delete.
        for variable, value in saved_env.items():
            if value is None:
                os.environ.pop(variable, None)
            else:
                os.environ[variable] = value

    # The leak comparison. Set DIFFERENCE, never a count: a pre-existing dir is not a new leak, and an
    # equal count must not pass while the membership differs.
    report.leaked_dirs = sorted(after_dirs - before_dirs)
    report.new_sqlite = sorted(after_sqlite - before_sqlite)

    if owned is not None:
        # Only remove the root when nothing is left in it. A survivor IS a leak and is already reported,
        # so deleting it here would erase the evidence and then delete the evidence.
        try:
            if not any(owned.iterdir()):
                _RMTREE(owned)
                report.private_root_removed = True
            else:
                report.private_root_removed = False
        except OSError as error:
            report.private_root_removed = False
            raise Refusal("PRIVATE-TEMP-ROOT-NOT-CLEANED",
                          f"{owned} could not be removed: {error}. The alarm's own root leaking is a "
                          f"defect in the alarm.") from error
    return report


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="test-substrate-leak-alarm",
        description="Refuse a test run that left a temp dir or an rpg-*.sqlite behind (replaces "
                    "test-substrate-leak-alarm.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--temp-root", default=None,
                        help="directory to snapshot for fusionrpg-* temp dirs (default: the system temp)")
    parser.add_argument("--isolate-temp", action="store_true",
                        help="give the wrapped run its own private temp root (TEMP/TMP redirected), which "
                             "removes cross-talk from a concurrent run on a shared machine")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds for the WRAPPED RUN (default {DEFAULT_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--run", nargs=argparse.REMAINDER, default=[],
                        help="the command to wrap; everything after this is passed through verbatim, so "
                             "this tool's own flags must come BEFORE --run")
    return parser


def render(report: Report, as_json: bool) -> None:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK" if report.ok else "FAILED",
                          "exitCode": EXIT_OK if report.ok else EXIT_FAILED,
                          "wrapped": report.wrapped, "runExit": report.run_exit,
                          "runTimedOut": report.run_timed_out,
                          # The wrapped run's own output, BOUNDED. It was captured and then DISCARDED in
                          # the first draft, which is the same blindness this tool exists to remove: a
                          # caller told only "the wrapped command exited 1" has to re-run the whole suite
                          # by hand to find out why. Bounded because a full `dotnet test` log is
                          # megabytes and this is an envelope, not a transcript.
                          "runOutput": report.run_output[-RUN_OUTPUT_TAIL:],
                          "runOutputTruncated": len(report.run_output) > RUN_OUTPUT_TAIL,
                          "tempDirs": {"before": report.temp_before, "after": report.temp_after},
                          "rpgSqlite": {"before": report.sqlite_before, "after": report.sqlite_after},
                          "leakedTempDirs": report.leaked_dirs,
                          "newRpgSqlite": report.new_sqlite,
                          "isolatedTemp": report.isolated_temp,
                          "privateRootRemoved": report.private_root_removed,
                          "reasons": report.reasons}, indent=2))
        return
    if report.ok:
        print("TEST SUBSTRATE LEAK ALARM OK - no fusionrpg-* temp dir or rpg-*.sqlite survived the run")
        return
    print("TEST SUBSTRATE LEAK ALARM FAILED - the run did not clean up after itself:")
    for reason in report.reasons:
        print(f"  {reason}")
    print()
    if report.run_output.strip():
        print("  the wrapped run's own output (tail):")
        for line in report.run_output[-RUN_OUTPUT_TAIL:].splitlines()[-20:]:
            print(f"    {line}")
        print()
    print(STANDARD)


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if args.timeout <= 0:
        return _refuse("INVALID-TIMEOUT", "--timeout must be positive", args.json)
    if not args.run:
        return _refuse("NO-WRAPPED-RUN",
                       "Pass the command to wrap: --run dotnet test <project> [--no-build]", args.json)

    # Configuration read ONCE, explicitly. The original's `$Root` defaulted to a `Resolve-Path`
    # evaluated at PARSE time, so a bad default failed before the body ran and named nothing.
    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    if not root.is_dir():
        return _refuse("TEMP-ROOT-UNREADABLE", f"--root {root} is not a directory", args.json)
    temp_root = Path(args.temp_root).expanduser().resolve() if args.temp_root else \
        Path(tempfile.gettempdir())

    report = None
    try:
        report = execute(root, temp_root, args.run, args.isolate_temp, args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED

    render(report, args.json)
    return EXIT_OK if report.ok else EXIT_FAILED


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
