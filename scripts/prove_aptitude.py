#!/usr/bin/env python3
"""PROVE: an aptitude resolve agrees between the overlay composer (DerivedComposer) and the battle
composer (BattleStatComposer) for the same allocation and Theta — class-system-todo.md P2.6, V3.

Both engines being compared are pure `FusionRpg.Core` types: no live game, no injector, no server.
This drives `gk-core/tools/ProveAptitude`, a small console tool, not a REST probe against a running instance.

The default scope is Checkpoint 2's one vertical slice, `Might -> combat.power.omni`. The wider
comparison is available with `--channels ""` and is NOT the default, because it surfaces a real,
pre-existing, out-of-scope-for-Phase-2 gap: the battle composer's `ChannelMods` loop applies no cap at
all, so a `SumIncreased`-kind capped channel can never agree once either side's contribution clears the
cap. `spec-aptitude-resolve.md` §8 forbids touching that compose logic; it is P3.1's inheritance
("all twelve, all live channels... zero deltas"), not P2.6's. Documented, not hidden: run
`--channels ""` to see it for yourself.

Replaces `scripts/prove-aptitude.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE RETRY MASKED A REAL FAILURE.** The original ran `dotnet run --no-build`, and on ANY nonzero exit
  ran `dotnet run` again WITH a build, then reported the SECOND attempt's exit code. Measured on this
  repository: the first attempt executed a STALE Debug binary and died with
  `could not locate repo root`, and the script reported success because the rebuilt second attempt
  worked. A first run that fails for a real reason is therefore invisible, and the diagnosis is lost. The
  build here is keyed on the build OUTPUT being ABSENT — decided BEFORE the recipe runs — so the recipe
  runs exactly once and its exit code is the run's exit code.

* **NEITHER `dotnet` CALL WAS BOUNDED.** Two invocations, no timeouts.

* **`Push-Location $toolDir` / `Pop-Location` CHANGED THE PROCESS'S WORKING DIRECTORY**, for every caller
  sharing the process. The `dotnet run` is given an explicit working directory instead.

* **A MISSING TOOL SURFACED AS A `dotnet` MESSAGE.** With the build output absent and the build failing,
  the original exited with the build's code and no word about which tool or which directory.

* **NO MACHINE-READABLE VERDICT OF ITS OWN.** The answer is the child's stdout plus a file it writes;
  `--json` here reports the exit code, both streams, whether a build was needed, and what was forwarded.

DELIBERATELY UNCHANGED
----------------------
Same five inputs with the same defaults and the same spellings on the wire (`--theta`, `--source`,
`--points`, `--channels`, `--out`), the same tool, and the same OUTPUT PATH default. `--channels` is
still OMITTED when it is empty rather than passed as an empty string, because that omission is what
selects "every touched channel" — passing `""` would instead mean "no channels" and silently prove
nothing.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

TOOL_ID = "prove-aptitude"

EXIT_REFUSED = 64

DEFAULT_TOOL = ("tools", "ProveAptitude")
DEFAULT_ASSEMBLY = "ProveAptitude.dll"
DEFAULT_CONFIGURATION = "Debug"
TFM_GLOB = "net*"

DEFAULT_THETA = 1000
DEFAULT_SOURCE = "Might"
DEFAULT_POINTS = 100
# Checkpoint 2's scope. Empty means "every touched channel", and is passed by OMISSION.
DEFAULT_CHANNELS = "combat.power.omni"
DEFAULT_OUT = ("docs", "research", "class-system", "_prove-aptitude.json")

DEFAULT_BUILD_TIMEOUT = 1800
DEFAULT_RUN_TIMEOUT = 900

REFUSAL_REASONS = {
    "TOOL-MISSING", "TOOL-NOT-BUILT", "DOTNET-NOT-ON-PATH", "BUILD-FAILED", "BUILD-TIMED-OUT",
    "RUN-TIMED-OUT", "INVALID-TIMEOUT", "INVALID-CONFIGURATION", "RUNNER-CRASHED", "OUT-PARENT-MISSING",
    "NON-POSITIVE-THETA", "NON-POSITIVE-POINTS", "UNREADABLE-RESULT",
}

# Bound once, module-private. `subprocess` and `shutil` are process-wide modules: a test that patches
# either reaches every other test in the project, which this program has measured at 506 unrelated
# failures in one case.
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

    A TFM bump is then not a hardcoded string to forget. Globbing a directory that does not exist yields
    an empty sequence and does not raise, so no `is_dir()` guard is needed -- falsification identified one
    as redundant on the sibling forwarder.
    """
    base = tool_dir / "bin" / configuration
    for tfm in sorted(base.glob(TFM_GLOB)):
        candidate = tfm / DEFAULT_ASSEMBLY
        if candidate.is_file():
            return candidate
    return None


def build_if_needed(dotnet: str, tool_dir: Path, configuration: str, timeout: int) -> bool:
    """Build when the build OUTPUT is ABSENT. Returns whether a build was needed.

    Decided BEFORE the recipe runs, and never after it. The original decided after, from the recipe's
    exit code, which is how a stale-binary failure got reported as a success.
    """
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
                      f"{tool_dir / 'bin' / configuration}; the configuration or the output name "
                      f"differs from what this tool expects")
    return True


def run_proof(dotnet: str, tool_dir: Path, forwarded: list[str], timeout: int) -> tuple[int, str, str]:
    """One bounded `dotnet run --no-build`, forwarding every argument verbatim. Never retried."""
    command = [dotnet, "run", "--no-build", "--", *forwarded]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(tool_dir))
    except subprocess.TimeoutExpired as expired:
        partial = expired.stdout or b""
        seen = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        raise Refusal("RUN-TIMED-OUT",
                      f"the proof did not finish within {timeout}s. It writes "
                      f"{'its result file'}; it is not retried, so read what it had already printed:\n"
                      f"{seen[-1500:]}") from expired
    except OSError as error:
        raise Refusal("RUNNER-CRASHED", f"could not start {dotnet}: {error}") from error
    return proc.returncode, proc.stdout or "", proc.stderr or ""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-aptitude",
        description="Prove an aptitude resolve agrees between the overlay and battle composers "
                    "(replaces prove-aptitude.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--tool", default="",
                        help=f"the console tool to drive (default: {'/'.join(DEFAULT_TOOL)})")
    parser.add_argument("--configuration", default=DEFAULT_CONFIGURATION,
                        help=f"configuration whose build output is reused (default {DEFAULT_CONFIGURATION})")
    parser.add_argument("--theta", type=int, default=DEFAULT_THETA,
                        help=f"the contest reference level (default {DEFAULT_THETA})")
    parser.add_argument("--source", default=DEFAULT_SOURCE,
                        help=f"the funded aptitude (default {DEFAULT_SOURCE!r})")
    parser.add_argument("--points", type=int, default=DEFAULT_POINTS,
                        help=f"points allocated (default {DEFAULT_POINTS})")
    parser.add_argument("--channels", default=DEFAULT_CHANNELS,
                        help="comma-separated channels to compare; EMPTY means every touched channel and "
                             f"is passed by OMISSION, because passing an empty string would mean no "
                             f"channels and silently prove nothing (default {DEFAULT_CHANNELS!r})")
    parser.add_argument("--out", default="",
                        help=f"where the tool writes its result (default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for the build, if one is needed (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--run-timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"seconds for the proof itself (default {DEFAULT_RUN_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for name, value in (("--build-timeout", args.build_timeout), ("--run-timeout", args.run_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
    if args.theta <= 0:
        return _refuse("NON-POSITIVE-THETA",
                       f"--theta {args.theta} is not a contest reference level; the tool composes at "
                       f"Theta=1000 by default and a non-positive Theta is not a level", args.json)
    if args.points <= 0:
        return _refuse("NON-POSITIVE-POINTS",
                       f"--points {args.points} allocates nothing, so there is no resolve to compare; "
                       f"an empty allocation proves nothing", args.json)
    if not args.configuration:
        return _refuse("INVALID-CONFIGURATION", "--configuration must name a configuration", args.json)

    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    tool_dir = Path(args.tool).expanduser() if args.tool else root.joinpath(*DEFAULT_TOOL)
    if not tool_dir.is_absolute():
        tool_dir = Path.cwd() / tool_dir
    tool_dir = tool_dir.resolve()
    out_path = Path(args.out).expanduser() if args.out else root.joinpath(*DEFAULT_OUT)
    if not out_path.is_absolute():
        out_path = Path.cwd() / out_path
    out_path = out_path.resolve()

    # `--channels` is OMITTED when empty. That is the whole mechanism by which "every touched channel"
    # is selected, and it is why an empty value cannot be forwarded as an empty string.
    forwarded = ["--theta", str(args.theta), "--source", args.source,
                 "--points", str(args.points), "--out", str(out_path)]
    if args.channels:
        forwarded += ["--channels", args.channels]

    result: dict | None = None
    envelope: dict = {"tool": TOOL_ID, "toolPath": str(tool_dir), "out": str(out_path),
                      "forwarded": forwarded, "channelsOmitted": not bool(args.channels), "retried": False}
    try:
        if not tool_dir.is_dir():
            raise Refusal("TOOL-MISSING", f"the tool directory does not exist: {tool_dir}")
        parent = out_path.parent
        try:
            parent.mkdir(parents=True, exist_ok=True)
        except OSError as error:
            raise Refusal("OUT-PARENT-MISSING",
                          f"the result's parent directory could not be created: {parent}: {error}") from error
        dotnet = resolve_dotnet()
        envelope["built"] = build_if_needed(dotnet, tool_dir, args.configuration, args.build_timeout)
        exit_code, out, err = run_proof(dotnet, tool_dir, forwarded, args.run_timeout)

        # The tool's OWN machine-readable surface, read back rather than inferred from its exit code: a
        # proof that exits 0 without writing a result has proved nothing, and that is reportable.
        #
        # INSIDE the try, deliberately. The first version of this read-back sat after it, so an
        # `UNREADABLE-RESULT` refusal escaped `main` as an uncaught exception and a caller got a
        # traceback instead of the machine-readable refusal this tool promises. A refusal that is not
        # catchable by the caller's own error handling is not a refusal.
        if out_path.is_file():
            try:
                result = json.loads(out_path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError) as error:
                raise Refusal("UNREADABLE-RESULT",
                              f"the proof exited {exit_code} but {out_path} is not readable JSON: "
                              f"{error}") from error
        elif exit_code == 0:
            raise Refusal("UNREADABLE-RESULT",
                          f"the proof exited 0 but wrote no result at {out_path}; an absent result is "
                          f"not a pass")
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({
        "verdict": "OK" if exit_code == 0 else "FAILED",
        "exitCode": exit_code,
        "stdout": out,
        "stderr": err,
        "resultPath": str(out_path),
        "resultWritten": result is not None,
        "pass": result.get("Pass") if isinstance(result, dict) else None,
        "deltas": result.get("Deltas") if isinstance(result, dict) else None,
        "seconds": round(time.monotonic() - START, 3),
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        if out:
            sys.stdout.write(out)
        if err:
            sys.stderr.write(err)
        print(f"prove-aptitude: exit {exit_code}"
              + ("" if envelope["built"] else "  (reused an existing build)")
              + f"  result {'written' if result is not None else 'MISSING'}"
              + (f"  pass={envelope['pass']}" if envelope["pass"] is not None else "")
              + f"  in {envelope['seconds']}s")
    return exit_code


START = time.monotonic()


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
