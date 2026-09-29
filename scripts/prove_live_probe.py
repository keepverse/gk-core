#!/usr/bin/env python3
"""PROVE live-probe-tool (spec-live-probe-tool.md) -- the real 6-step live-probe recipe against a
running FusionRpg.Server (+ live game/Injector for `--run -Mode B`), end to end, real HTTP throughout.

Mode A (persisted-state only, steps 1-5): Server up, no game/Injector needed.
Mode B (full 6-step proof): Injector connected + a live match/board already running.

This is a FORWARDER. It owns no recipe: `gk-fusion/tools/ProveLiveProbe` is a console tool that opens a real
HttpClient against `--BaseUrl`, and this wrapper's whole job is to make sure that tool is BUILT and then
run it, forwarding every named flag verbatim.

Replaces `scripts/prove-live-probe.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **NEITHER `dotnet` CALL WAS BOUNDED.** `dotnet build` and `dotnet run --no-build` both ran with no
  timeout. This tool drives a console app over real HTTP with real side effects, and an unbounded parent
  means a wedged child is not bounded by anything.

* **`Push-Location $toolDir` / `Pop-Location` CHANGED THE PROCESS'S WORKING DIRECTORY**, for every caller
  sharing the process. The `dotnet run` is given an explicit working directory instead.

* **A MISSING TOOL SURFACED AS A `dotnet` MESSAGE.** With the built DLL absent and the build failing, the
  original exited with the build's code and no word about which tool or which directory. A named
  refusal says both.

* **NO MACHINE-READABLE VERDICT OF ITS OWN.** The original's answer is the child's stdout text; there is
  nothing to read programmatically. `--json` here reports the child's exit code, its stdout, its stderr,
  whether the build was needed, and how long each step of the wrapper took.

DELIBERATELY UNCHANGED, AND IT MATTERS
--------------------------------------
The BUILD CHECK IS KEYED ON THE BUILD OUTPUT EXISTING, NEVER ON THE RECIPE'S OWN EXIT CODE, and that is
the original's own explicit design note: a real run has real HTTP side effects (it mints a specimen,
spends allocation points, equips an item), so retrying on ANY nonzero exit would silently re-run a
genuine step refusal a second time against the live server. This port therefore NEVER retries the recipe.
It is also deliberately NOT `prove-hub-combat.ps1`'s "--no-build, retry once with a build on any nonzero
exit" shape, for the same reason.

`--run` takes the rest of the command line verbatim, as `ValueFromRemainingArguments` did. A bare `--`
separator is NOT accepted: argparse raises `SystemExit: 2` before this module's code runs, so this
tool's own flags come BEFORE `--run`. There is deliberately no branch here that filters a `--` out --
it would be unreachable, and it would contradict this paragraph.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

TOOL_ID = "prove-live-probe"

EXIT_REFUSED = 64

DEFAULT_TOOL = ("tools", "ProveLiveProbe")
DEFAULT_ASSEMBLY = "ProveLiveProbe.dll"
# The configuration `dotnet build` produces by default, and the TFM the csproj declares. The assembly is
# located by GLOB rather than by a hardcoded TFM, so a TFM bump is not a string to forget.
DEFAULT_CONFIGURATION = "Debug"
TFM_GLOB = "net*"

DEFAULT_BUILD_TIMEOUT = 1800
DEFAULT_RUN_TIMEOUT = 3600

REFUSAL_REASONS = {
    "TOOL-MISSING", "TOOL-NOT-BUILT", "DOTNET-NOT-ON-PATH", "BUILD-FAILED", "BUILD-TIMED-OUT",
    "RUN-TIMED-OUT", "INVALID-TIMEOUT", "INVALID-CONFIGURATION", "NO-RUN-ARGS", "RUNNER-CRASHED",
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
    """The built entry assembly, located by globbing the TFM, or None if the tool is not built."""
    base = tool_dir / "bin" / configuration
    for tfm in sorted(base.glob(TFM_GLOB)):
        candidate = tfm / DEFAULT_ASSEMBLY
        if candidate.is_file():
            return candidate
    return None


def build_if_needed(dotnet: str, tool_dir: Path, configuration: str, timeout: int) -> bool:
    """Build the tool when its output is ABSENT, and refuse by name when that fails.

    Returns whether a build was needed. The check is on the build OUTPUT, never on a recipe's exit code:
    a real run has real side effects against a live server, so re-running it because it refused would
    re-run a genuine refusal. This function is therefore never called after a run.
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
        raise Refusal("BUILD-FAILED",
                      f"dotnet build of {tool_dir} exited {proc.returncode}:\n{tail}")
    if assembly_path(tool_dir, configuration) is None:
        # The build said it succeeded and there is still no entry assembly. Report that as itself rather
        # than letting `dotnet run --no-build` fail with a message about a file nobody named.
        raise Refusal("TOOL-NOT-BUILT",
                      f"the build reported success but no {DEFAULT_ASSEMBLY} exists under "
                      f"{tool_dir / 'bin' / configuration}; the configuration or the output name differs "
                      f"from what this tool expects")
    return True


def run_recipe(dotnet: str, tool_dir: Path, rest: list[str], timeout: int) -> tuple[int, str, str]:
    """One bounded `dotnet run --no-build`, forwarding every argument verbatim."""
    command = [dotnet, "run", "--no-build", "--", *rest]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(tool_dir))
    except subprocess.TimeoutExpired as expired:
        partial = (expired.stdout or b"")
        seen = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        raise Refusal("RUN-TIMED-OUT",
                      f"the live probe did not finish within {timeout}s. It talks to a live server with "
                      f"real side effects, so a timeout here is not a retry -- read what it had already "
                      f"printed:\n{seen[-1500:]}") from expired
    except OSError as error:
        raise Refusal("RUNNER-CRASHED", f"could not start {dotnet}: {error}") from error
    return proc.returncode, proc.stdout or "", proc.stderr or ""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prove-live-probe",
        description="Run the real 6-step live-probe recipe against a running FusionRpg.Server "
                    "(replaces prove-live-probe.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--tool", default="",
                        help=f"the console tool to drive (default: {'/'.join(DEFAULT_TOOL)})")
    parser.add_argument("--configuration", default=DEFAULT_CONFIGURATION,
                        help=f"configuration whose build output is reused (default {DEFAULT_CONFIGURATION})")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for the build, if one is needed (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--run-timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"seconds for the recipe itself (default {DEFAULT_RUN_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--run", nargs=argparse.REMAINDER, default=[],
                        help="the arguments for the recipe; everything after this is forwarded verbatim. "
                             "A bare `--` separator is NOT accepted, so this tool's own flags come "
                             "BEFORE `--run`.")
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
        tool_dir = (Path.cwd() / tool_dir)
    tool_dir = tool_dir.resolve()

    rest = list(args.run or [])
    if not rest:
        return _refuse("NO-RUN-ARGS",
                       "nothing to forward. Pass the recipe's own arguments after `--run`, e.g. "
                       "`--run -Mode A -BaseUrl http://127.0.0.1:5101 -PlayerId 1 -Side plant`", args.json)

    started = time.monotonic()
    envelope: dict = {"tool": TOOL_ID, "toolPath": str(tool_dir), "forwarded": rest}
    try:
        if not tool_dir.is_dir():
            raise Refusal("TOOL-MISSING", f"the tool directory does not exist: {tool_dir}")
        dotnet = resolve_dotnet()
        envelope["built"] = build_if_needed(dotnet, tool_dir, args.configuration, args.build_timeout)
        exit_code, out, err = run_recipe(dotnet, tool_dir, rest, args.run_timeout)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    envelope.update({
        "verdict": "OK" if exit_code == 0 else "FAILED",
        "exitCode": exit_code,
        "stdout": out,
        "stderr": err,
        "seconds": round(time.monotonic() - started, 3),
        "retried": False,
    })
    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        if out:
            sys.stdout.write(out)
        if err:
            sys.stderr.write(err)
        print(f"prove-live-probe: exit {exit_code}"
              + ("" if envelope["built"] else "  (reused an existing build)")
              + (f"  in {envelope['seconds']}s" if "seconds" in envelope else ""))
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
