#!/usr/bin/env python3
"""PROVE: a live lawn-combat observation, via `gk-fusion/tools/LawnCombatObserver` (Task 0, lawn-combat-wire
"Phase 0 -- the ruler").

The tool watches a real running `FusionRpg.Server` (plus a live game/Injector, for real hit data) over
real HTTP for a while and writes a machine-readable run file a gate can diff against another run. A server
up in any board state is enough for the non-perturbation proof and an honest "no data" read; a LIVE board
with real combat is what shows real vanilla hits.

Replaces `scripts/lawn-combat-observer.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **`Push-Location $toolDir` / `Pop-Location` CHANGED THE PROCESS'S WORKING DIRECTORY**, for every caller
  sharing the process, and the run file therefore landed relative to the tool directory. The child is
  given an explicit working directory instead, so the file lands in exactly the same place with the
  caller's cwd untouched.

* **THE BUILD FALLBACK RAN ON ANY FAILURE, AND REPORTED THE SECOND ATTEMPT.** `if (-not (Test-Path
  $builtDll)) { dotnet build }` then `dotnet run --no-build` -- that one is sound. The failure modes that
  remain are the ones the PowerShell host hid: a build that fails surfaces as `dotnet`'s exit code with
  no word about which tool or directory, and a missing tool directory is not distinguished from a missing
  assembly. Both are named refusals here.

* **NO MACHINE-READABLE VERDICT.** The answer is the child's stdout plus a file it writes. `--json` here
  reports the exit code, both streams, whether a build was needed, what was forwarded, and the tool's own
  run file READ BACK -- because the run file is the tool's real output, and a wrapper that printed only
  its stdout would be reporting the wrapper's opinion of a result rather than the result.

* **NEITHER `dotnet` CALL WAS BOUNDED.** Two invocations, no timeouts. An observation run is WALL-CLOCK
  work that spends its whole budget waiting, so a bound is the difference between "finished" and "hung".

* **FORWARDING USED `[Parameter(ValueFromRemainingArguments)] $RestArgs`.** In Python, forwarding
  arbitrary arguments that begin with `-` is the `argparse` seam this program has now hit three times:
  a bare `--` is rejected before the body runs, and a paired flag cannot carry a value beginning with `-`.
  So the wrapper takes the arguments it knows, and forwards everything else VERBATIM and IN ORDER, and
  asserts at run time that the two flag sets do not collide -- a collision would silently steal a flag
  from the tool.

DELIBERATELY UNCHANGED
----------------------
Same tool, same default run-file location (the tool directory, because that is where the tool's bare
relative default resolved under the original's `Push-Location`), same "exits 0 on a run that collected real
data, non-zero when the server could not be reached or no /api/perf window landed", and the same never-
silently-retry property: there is nothing here to retry blindly, since a "no data" result is itself the
finding.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

TOOL_ID = "lawn-combat-observer"

EXIT_REFUSED = 64

DEFAULT_TOOL = ("tools", "LawnCombatObserver")
DEFAULT_ASSEMBLY = "LawnCombatObserver.dll"
DEFAULT_CONFIGURATION = "Debug"
TFM_GLOB = "net*"
DEFAULT_RUN_FILE = "lawn-combat-observer-run.json"

DEFAULT_BUILD_TIMEOUT = 1800
DEFAULT_RUN_TIMEOUT = 1800

# The wrapper's own flags. The tool's flags are read from its source at run time and compared against
# this set, so a tool that grows a colliding flag is a named refusal rather than a silently stolen
# argument.
WRAPPER_FLAGS = {"root", "tool", "configuration", "build-timeout", "run-timeout", "json"}

REFUSAL_REASONS = {
    "TOOL-MISSING", "TOOL-NOT-BUILT", "DOTNET-NOT-ON-PATH", "BUILD-FAILED", "BUILD-TIMED-OUT",
    "RUN-TIMED-OUT", "RUNNER-CRASHED", "INVALID-TIMEOUT", "INVALID-CONFIGURATION",
    "FLAG-COLLISION", "UNREADABLE-RESULT",
}

# Bound once, module-private. `subprocess` and `shutil` are process-wide modules: a test that patches
# either reaches every other test in the project.
_RUN = subprocess.run
_WHICH = shutil.which


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def resolve_dotnet() -> str:
    found = _WHICH("dotnet")
    if not found:
        raise Refusal("DOTNET-NOT-ON-PATH", "dotnet is not on PATH")
    return found


def assembly_path(tool_dir: Path, configuration: str) -> Path | None:
    """The built entry assembly, located by GLOBBING the TFM, or None when the tool is not built.

    Globbing a directory that does not exist yields an empty sequence and does not raise, so no
    `is_dir()` guard is needed.
    """
    base = tool_dir / "bin" / configuration
    for tfm in sorted(base.glob(TFM_GLOB)):
        candidate = tfm / DEFAULT_ASSEMBLY
        if candidate.is_file():
            return candidate
    return None


def build_if_needed(dotnet: str, tool_dir: Path, configuration: str, timeout: int) -> bool:
    """Build when the build OUTPUT is ABSENT. Decided BEFORE the recipe runs, and never after it."""
    if assembly_path(tool_dir, configuration) is not None:
        return False
    try:
        proc = _RUN([dotnet, "build", str(tool_dir), "-c", configuration, "--verbosity", "quiet"],
                    capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as expired:
        raise Refusal("BUILD-TIMED-OUT",
                      f"building {tool_dir} did not finish within {timeout}s") from expired
    if proc.returncode != 0:
        tail = ((proc.stdout or "") + (proc.stderr or ""))[-1500:]
        raise Refusal("BUILD-FAILED", f"dotnet build of {tool_dir} exited {proc.returncode}:\n{tail}")
    if assembly_path(tool_dir, configuration) is None:
        raise Refusal("TOOL-NOT-BUILT",
                      f"the build reported success but no {DEFAULT_ASSEMBLY} exists under "
                      f"{tool_dir / 'bin' / configuration}; the configuration or the output name differs "
                      f"from what this tool expects")
    return True


def tool_flags(tool_dir: Path) -> set[str]:
    """The flags `gk-fusion/tools/LawnCombatObserver` accepts, read from its OWN source rather than remembered.

    Read at run time because the point of the comparison is to notice when the tool changes. A flag list
    transcribed into this file would agree with itself forever and never notice anything.
    """
    found: set[str] = set()
    for path in sorted(tool_dir.glob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if "class Options" not in text:
            continue
        for match in __import__("re").finditer(r'case "([a-z0-9]+)":', text):
            found.add(match.group(1))
    return found


def run_observer(dotnet: str, tool_dir: Path, forwarded: list[str], timeout: int) -> tuple[int, str, str]:
    """One bounded `dotnet run --no-build`, forwarding every argument verbatim. Never retried."""
    command = [dotnet, "run", "--no-build", "--", *forwarded]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(tool_dir))
    except subprocess.TimeoutExpired as expired:
        partial = expired.stdout or b""
        seen = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        raise Refusal("RUN-TIMED-OUT",
                      f"the observation did not finish within {timeout}s. It is wall-clock work that "
                      f"spends its budget waiting, and it is NOT retried, so read what it had already "
                      f"printed:\n{seen[-1500:]}") from expired
    except OSError as error:
        raise Refusal("RUNNER-CRASHED", f"could not start {dotnet}: {error}") from error
    return proc.returncode, proc.stdout or "", proc.stderr or ""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="lawn-combat-observer",
        description="Observe a live lawn over real HTTP via tools/LawnCombatObserver "
                    "(replaces lawn-combat-observer.ps1). Every other argument is forwarded to the tool.")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--tool", default="",
                        help=f"the console tool to drive (default: {'/'.join(DEFAULT_TOOL)})")
    parser.add_argument("--configuration", default=DEFAULT_CONFIGURATION,
                        help=f"configuration whose build output is reused (default {DEFAULT_CONFIGURATION})")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for the build, if one is needed (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--run-timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"seconds for the observation itself (default {DEFAULT_RUN_TIMEOUT})")
    parser.add_argument("--json", action="store_true",
                        help="print the verdict as JSON; every other argument is forwarded to the tool")
    return parser


def main(argv: list[str] | None = None) -> int:
    # `parse_known_args` is the forwarding seam: the wrapper takes the flags it owns and hands the rest to
    # the tool VERBATIM and IN ORDER. The rejected alternatives are the ones this program has now hit
    # three times -- a bare `--` dies in argparse before the body runs, and a paired flag cannot carry a
    # value beginning with `-`.
    parser = build_parser()
    args, forwarded = parser.parse_known_args(argv)

    for name, value in (("--build-timeout", args.build_timeout), ("--run-timeout", args.run_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
    if not args.configuration:
        return _refuse("INVALID-CONFIGURATION", "--configuration must name a configuration", args.json)

    root = Path(args.root).expanduser().resolve() if args.root else Path(__file__).resolve().parent.parent
    tool_dir = Path(args.tool).expanduser() if args.tool else root.joinpath(*DEFAULT_TOOL)
    if not tool_dir.is_absolute():
        tool_dir = Path.cwd() / tool_dir
    tool_dir = tool_dir.resolve()

    # The run file, wherever the forwarded `--out` says it is. Resolved against the CHILD's working
    # directory, because that is the directory the original's `Push-Location $toolDir` put the child in
    # and therefore the directory its bare relative default resolved against.
    run_file = tool_dir / DEFAULT_RUN_FILE
    for index, token in enumerate(forwarded):
        if token.lstrip("-").lower() in ("out", "outfile") and index + 1 < len(forwarded):
            # `pathlib`'s `/` already yields the right-hand side when it is ABSOLUTE, so no explicit
            # is_absolute() test is needed here. Falsification proved it: a mutant that re-resolved
            # unconditionally behaved identically, because that is what the operator does on its own.
            # A branch that cannot change the result is a branch to delete, not to defend.
            run_file = (tool_dir / Path(forwarded[index + 1]).expanduser()).resolve()

    result: dict | None = None
    started = time.monotonic()
    envelope: dict = {"tool": TOOL_ID, "toolPath": str(tool_dir), "forwarded": forwarded,
                      "runFile": str(run_file), "retried": False}
    try:
        if not tool_dir.is_dir():
            raise Refusal("TOOL-MISSING", f"the tool directory does not exist: {tool_dir}")
        accepted = tool_flags(tool_dir)
        clashing = sorted(accepted & WRAPPER_FLAGS)
        if clashing:
            raise Refusal("FLAG-COLLISION",
                          f"{tool_dir.name} accepts {clashing}, which this wrapper also owns. A forwarded "
                          f"{clashing[0]} would be claimed by the wrapper and never reach the tool. Rename "
                          f"one side; nothing is forwarded until they are disjoint")
        envelope["toolFlags"] = sorted(accepted)
        dotnet = resolve_dotnet()
        envelope["built"] = build_if_needed(dotnet, tool_dir, args.configuration, args.build_timeout)
        exit_code, out, err = run_observer(dotnet, tool_dir, forwarded, args.run_timeout)

        # The tool's OWN machine-readable surface, read back rather than inferred from its exit code: a
        # run that exits 0 without writing a run file has observed nothing, and that is reportable.
        #
        # INSIDE the try, deliberately. The first version of this read-back sat after it, so
        # UNREADABLE-RESULT escaped `main` as an uncaught exception and a caller got a traceback instead of
        # the machine-readable refusal this tool promises. That is the IDENTICAL defect the sibling
        # prove_aptitude port's suite caught, repeated here because the shape was copied rather than
        # learned: a refusal a caller cannot catch is not a refusal.
        if run_file.is_file():
            try:
                result = json.loads(run_file.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError) as error:
                raise Refusal("UNREADABLE-RESULT",
                              f"the observation exited {exit_code} but {run_file} is not readable JSON: "
                              f"{error}") from error
        elif exit_code == 0:
            raise Refusal("UNREADABLE-RESULT",
                          f"the observation exited 0 but wrote no run file at {run_file}; an absent run "
                          f"file is not an observation")
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({
        "verdict": "OK" if exit_code == 0 else "FAILED",
        "exitCode": exit_code,
        "stdout": out,
        "stderr": err,
        "resultWritten": result is not None,
        "noData": result.get("NoData") if isinstance(result, dict) else None,
        "noDataReason": result.get("NoDataReason") if isinstance(result, dict) else None,
        "totalHits": result.get("TotalHits") if isinstance(result, dict) else None,
        "windowsObserved": result.get("WindowsObserved") if isinstance(result, dict) else None,
        "injectorSessionActiveEverTrue": result.get("InjectorSessionActiveEverTrue")
        if isinstance(result, dict) else None,
        "seconds": round(time.monotonic() - started, 3),
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        if out:
            sys.stdout.write(out)
        if err:
            sys.stderr.write(err)
        print(f"lawn-combat-observer: exit {exit_code}"
              + ("" if envelope["built"] else "  (reused an existing build)")
              + f"  run file {'written' if result is not None else 'MISSING'}"
              + (f"  noData={envelope['noData']}" if envelope["noData"] is not None else "")
              + f"  in {envelope['seconds']}s")
    return exit_code


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": EXIT_REFUSED, "retried": False}
        if envelope:
            payload.update(envelope)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
