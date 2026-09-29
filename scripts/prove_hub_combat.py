#!/usr/bin/env python3
"""PROVE: the program's own operator prove for actor-hub-and-combat-power-solid-fixing (T19) --
battle Hub and sheet Hub agree on the SAME equip/tree bound atoms for one UniqueActor, AND Standing
rises via a real Hub combat writer (a shipped skill.cooldown/effectiveness aptitude edge) while Theta
(level) alone never moves it. No live game, no live server -- drives gk-forge/tools/ProveHubCombat, a small
console tool over RpgStore.InMemory(), the same shape prove-aptitude.ps1 already established.

Bullet 3 (Bound lawn aptitude input vs Server UniqueCreature compose, lawn-aptitude-parity) is
deliberately NOT here: that task (T12) found the Injector has zero UniqueCreature aptitude fetch to
compare against -- aptitude-sheet's own unique-lawn-wire (AS-1.1) is unbuilt. Nothing to prove until
that lands; see tasks/actor-hub-and-combat-power-solid-fixing-evidence-map.md T12/T19.

Replaces `scripts/prove-hub-combat.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **NEITHER `dotnet` CALL WAS BOUNDED.** `dotnet run --no-build` and the retry `dotnet run` both ran
  with no timeout. The proof boots a full server tuning graph over an in-memory store; an unbounded
  parent means a wedged child is not bounded by anything.

* **`Push-Location $toolDir` / `Pop-Location` CHANGED THE PROCESS'S WORKING DIRECTORY**, for every caller
  sharing the process. The `dotnet run` is given an explicit working directory instead.

* **A MISSING TOOL SURFACED AS A `dotnet` MESSAGE.** With the build output absent and the build failing,
  the original exited with the build's code and no word about which tool or which directory. A named
  refusal says both.

* **NO MACHINE-READABLE VERDICT OF ITS OWN.** The answer was the child's exit code and a file it
  writes; `--json` here reports the exit code, both streams, whether the retry fired, and what was
  forwarded.

THE RETRY IS DELIBERATE, AND IT IS THIS TOOL'S OWN
-------------------------------------------------
The original ran `dotnet run --no-build` and, on ANY nonzero exit, ran `dotnet run` again WITH a
build, then reported the SECOND attempt's exit code. That shape is a defect in `prove-aptitude.ps1`
(a real run has real HTTP side effects, so retrying re-runs a genuine refusal) and it is deliberately
NOT a defect here: `gk-forge/tools/ProveHubCombat` is a pure in-memory proof with no server, no game, and no
persisted state, so a second run cannot double-apply anything. The retry exists because `--no-build`
fails on a clean checkout with no prior build, and the original's own comment says so. It is kept
verbatim -- first attempt `--no-build`, retry with a build, the LAST attempt's exit code is the run's
-- and made visible: the envelope's `retried` field says whether it fired, so a run that needed the
retry is distinguishable from one that did not.

DELIBERATELY UNCHANGED
----------------------
Same tool, same default output path, same forwarded arguments (`--out`), same working directory for
the child, same exit-code contract: the child's exit code IS this script's exit code.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

TOOL_ID = "prove-hub-combat"

EXIT_REFUSED = 64

DEFAULT_TOOL = ("tools", "ProveHubCombat")
DEFAULT_ASSEMBLY = "ProveHubCombat.dll"
DEFAULT_CONFIGURATION = "Debug"
TFM_GLOB = "net*"

DEFAULT_OUT = ("docs", "research", "actor-hub-and-combat-power", "_prove-hub-combat.json")

DEFAULT_BUILD_TIMEOUT = 1800
DEFAULT_RUN_TIMEOUT = 900

REFUSAL_REASONS = {
    "TOOL-MISSING", "TOOL-NOT-BUILT", "DOTNET-NOT-ON-PATH", "BUILD-FAILED", "BUILD-TIMED-OUT",
    "RUN-TIMED-OUT", "INVALID-TIMEOUT", "INVALID-CONFIGURATION", "RUNNER-CRASHED", "OUT-PARENT-MISSING",
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

    A TFM bump is then not a hardcoded string to forget. Globbing a directory that does not exist
    yields an empty sequence and does not raise, so no `is_dir()` guard is needed.
    """
    base = tool_dir / "bin" / configuration
    for tfm in sorted(base.glob(TFM_GLOB)):
        candidate = tfm / DEFAULT_ASSEMBLY
        if candidate.is_file():
            return candidate
    return None


def build_tool(dotnet: str, tool_dir: Path, configuration: str, timeout: int) -> None:
    """Build the tool, refusing by name when that fails. Only ever called for the RETRY, never before
    the first attempt: the first attempt is `--no-build`, exactly as the original ran it."""
    try:
        proc = _RUN([dotnet, "build", str(tool_dir), "-c", configuration, "--verbosity", "quiet"],
                    capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as expired:
        raise Refusal("BUILD-TIMED-OUT",
                      f"building {tool_dir} did not finish within {timeout}s") from expired
    except OSError as error:
        raise Refusal("RUNNER-CRASHED", f"could not start {dotnet}: {error}") from error
    if proc.returncode != 0:
        tail = ((proc.stdout or "") + (proc.stderr or ""))[-1500:]
        raise Refusal("BUILD-FAILED", f"dotnet build of {tool_dir} exited {proc.returncode}:\n{tail}")
    if assembly_path(tool_dir, configuration) is None:
        raise Refusal("TOOL-NOT-BUILT",
                      f"the build reported success but no {DEFAULT_ASSEMBLY} exists under "
                      f"{tool_dir / 'bin' / configuration}; the configuration or the output name "
                      f"differs from what this tool expects")


def run_proof(dotnet: str, tool_dir: Path, forwarded: list[str], timeout: int,
              with_build: bool) -> tuple[int, str, str]:
    """One bounded `dotnet run`, forwarding every argument verbatim after `--`.

    `with_build` selects the original's two shapes: the first attempt is `--no-build`, the retry is a
    plain `dotnet run` (which builds). The retry is the original's own documented design for a pure
    in-memory proof and is kept verbatim.
    """
    command = [dotnet, "run"] + ([] if with_build else ["--no-build"]) + ["--", *forwarded]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(tool_dir))
    except subprocess.TimeoutExpired as expired:
        partial = expired.stdout or b""
        seen = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        raise Refusal("RUN-TIMED-OUT",
                      f"the proof did not finish within {timeout}s. It writes its result file at the "
                      f"end; read what it had already printed:\n{seen[-1500:]}") from expired
    except OSError as error:
        raise Refusal("RUNNER-CRASHED", f"could not start {dotnet}: {error}") from error
    return proc.returncode, proc.stdout or "", proc.stderr or ""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-hub-combat",
        description="Prove battle Hub and sheet Hub agree on the same bound atoms, and that Standing "
                    "rises via a Hub combat writer (replaces prove-hub-combat.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--tool", default="",
                        help=f"the console tool to drive (default: {'/'.join(DEFAULT_TOOL)})")
    parser.add_argument("--configuration", default=DEFAULT_CONFIGURATION,
                        help=f"configuration whose build output is reused (default {DEFAULT_CONFIGURATION})")
    parser.add_argument("--out", default="",
                        help=f"where the tool writes its result (default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for the retry's build (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--run-timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"seconds for each proof attempt (default {DEFAULT_RUN_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for name, value in (("--build-timeout", args.build_timeout), ("--run-timeout", args.run_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
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

    forwarded = ["--out", str(out_path)]

    envelope: dict = {"tool": TOOL_ID, "toolPath": str(tool_dir), "out": str(out_path),
                      "forwarded": forwarded, "retried": False}
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

        started = time.monotonic()
        exit_code, out, err = run_proof(dotnet, tool_dir, forwarded, args.run_timeout,
                                        with_build=False)
        if exit_code != 0:
            # The original's own documented retry: `--no-build` fails on a clean checkout with no
            # prior build, so the second attempt builds. Kept verbatim; the proof is pure in-memory,
            # so a second run double-applies nothing.
            envelope["retried"] = True
            envelope["firstAttempt"] = {"exitCode": exit_code, "stdout": out, "stderr": err}
            build_tool(dotnet, tool_dir, args.configuration, args.build_timeout)
            exit_code, out, err = run_proof(dotnet, tool_dir, forwarded, args.run_timeout,
                                            with_build=True)
        envelope["seconds"] = round(time.monotonic() - started, 3)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({
        "verdict": "OK" if exit_code == 0 else "FAILED",
        "exitCode": exit_code,
        "stdout": out,
        "stderr": err,
        "resultWritten": out_path.is_file(),
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        if out:
            sys.stdout.write(out)
        if err:
            sys.stderr.write(err)
        print(f"prove-hub-combat: exit {exit_code}"
              + ("  (retried with a build)" if envelope["retried"] else "")
              + f"  result {'written' if envelope['resultWritten'] else 'MISSING'}"
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
