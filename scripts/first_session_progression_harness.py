#!/usr/bin/env python3
"""Run the three focused onboarding suites and report which project failed.

Replaces `scripts/first-session-progression-harness.ps1`.

WHAT THIS IS
Three `dotnet test` invocations with a filter each, run in order and stopping at the first red, covering
the first-session progression evidence: fresh Player 1, settled victory, simulator `match.result`, species
reveal, deterministic Dave item, replay/idempotency, GET projection, and acknowledgement-only claim.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **NO TIMEOUT ON ANY OF THE THREE.** A hung test host held the harness open indefinitely and the only
  signal was the absence of the next line. Three bounded invocations share one budget, fail-fast, so a
  wedged first suite costs the remaining budget rather than all of it three times over.

* **THE FAILING COMMAND'S EXIT CODE WAS READ TWICE, AND THE SECOND READ WAS A DIFFERENT NUMBER.**
  `if ($LASTEXITCODE -ne 0) { throw "onboarding Data harness failed ($LASTEXITCODE)" }` reads the side
  effect again while building the message. Anything that ran in between — and `throw` itself goes
  through the host — can change it, so the message could name a code the harness never saw. The code is
  captured once, at the point the child returned, and the same value is both tested and reported.

* **THREE COPIES OF THE SAME FOUR LINES, EACH WITH ITS OWN MESSAGE AND NEITHER WITH A `--project`.**
  Every invocation is a `dotnet test <project> --filter <expr>`, and a project name that does not resolve
  is a `dotnet` error rather than a refusal naming which project was wrong. The projects are now declared
  as data, so the set is readable and a case can assert which ones they are.

* **NO MACHINE-READABLE OUTPUT.** A caller could only read prose, so it could not tell which project
  failed without parsing a sentence that is free to be reworded.

WHAT THIS TOOL MUST NOT DO
--------------------------
It must not run the second and third suites after the first is red. They are independent, so running
them after a red buys nothing and costs the operator the whole sequence's time on every failure — and
"the harness reported a failure" must name WHICH project, because the three fixes are unrelated.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "first-session-progression-harness"
EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64
EXIT_USAGE = 64

# The whole sequence's budget, shared. A generation pass over three test projects is the slow case, and a
# harness that times out on a legitimately long one is its own false red.
DEFAULT_TIMEOUT = 1800
# `dotnet test` on a cold tree restores, builds and runs, so the FIRST invocation pays for all of it and
# the later ones only run tests. The split is what makes one budget able to bound the whole sequence; a
# flat per-suite share would either starve the first or multiply the caller's bound by three.
DOTNET_COLD_START_SHARE = 0.5


@dataclass(frozen=True)
class Suite:
    """One declared suite: the project it runs and the filter that selects its tests."""

    name: str
    project: str
    filter: str

    def argv(self, no_build: bool) -> list[str]:
        argv = ["dotnet", "test", self.project, "--filter", self.filter]
        if no_build:
            argv.append("--no-build")
        return argv


# THE DECLARED SET. Read from the tool's own surface rather than from a copy of the original's text, and
# asserted as a CLOSED list -- a harness that quietly stopped running the simulator suite would report
# green while covering two thirds of the evidence it names.
SUITES = (
    Suite("onboarding-data", "tests/FusionRpg.Data.Tests", "FullyQualifiedName~Onboarding"),
    Suite("onboarding-http", "tests/FusionRpg.Server.Tests",
          "FullyQualifiedName~OnboardingEndpointsTests"),
    Suite("simulator-onboarding", "tests/FusionRpg.E2E.Tests",
          "FullyQualifiedName~Sim_victory_emits_game_driven_result_and_unlocks"
          "_ordered_onboarding_reveals"),
)

EVIDENCE = ("fresh Player 1, settled victory, simulator match.result, species reveal, deterministic "
            "Dave item, replay/idempotency, GET projection, and acknowledgement-only claim")


class Refusal(Exception):
    """A named precondition failure. Never exits 0 having not run."""

    def __init__(self, reason: str, detail: str, exit_code: int = EXIT_REFUSED) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


@dataclass
class Step:
    name: str
    project: str
    argv: list[str]
    exit: int
    seconds: float
    timed_out: bool = False
    output: str = ""


@dataclass
class Report:
    ran: list[Step] = field(default_factory=list)
    failed_step: int | None = None
    refused: tuple[str, str] | None = None

    @property
    def red(self) -> Step | None:
        return self.ran[self.failed_step - 1] if self.failed_step else None


def resolve_dotnet() -> str:
    """The `dotnet` executable, resolved ONCE and named when it is absent.

    The original called `& dotnet` and let the shell decide, so a missing SDK surfaced as whatever
    PowerShell's own error said. Here it is a refusal naming the tool, which is the difference between
    "install the SDK" and "re-read the message".

    Resolved through `shutil.which`, and the RESULT is what runs: on Windows `dotnet` is `dotnet.exe`
    (found either way) but `npm`/`npx`/`yarn` are `.cmd` shims and `CreateProcess` will not execute one
    without its extension in argv[0] — the same gap the web-check port closed in `gk-core/scripts/checks/common.py`.
    """
    import shutil

    found = shutil.which("dotnet")
    if not found:
        raise Refusal("DOTNET-NOT-ON-PATH",
                      "dotnet is not on PATH; install the .NET SDK, or run this harness where it is")
    return found


def check_projects(root: Path, suites: tuple[Suite, ...]) -> None:
    """Every declared project must EXIST on disk, checked BEFORE anything runs.

    Checked up front because `dotnet test <missing-path>` fails as a build error naming a project the
    caller never mistyped — the harness would have run the first two suites and only then reported the
    third, so a typo in the last entry costs two runs to discover.
    """
    missing = [s.project for s in suites if not (root / s.project).is_dir()]
    if missing:
        raise Refusal("PROJECT-MISSING",
                      f"declared project(s) not on disk under {root}: {', '.join(missing)}")


def run_suite(executable: str, suite: Suite, cwd: Path, no_build: bool, timeout: int) -> Step:
    """One suite, bounded. A timeout is a Step, not an exception, so the report still says which suite.

    argv[0] is the RESOLVED executable, unconditionally and with no extension test. A differential against
    the retired `.ps1` caught the shape of that test being wrong: the first version asked
    `executable.endswith((".exe", ".cmd", ".bat"))`, which is CASE-SENSITIVE, so the resolved
    `dotnet.CMD` failed it and the tool fell back to the bare token `dotnet`. `CreateProcess` resolves a
    bare name by appending `.exe` ONLY, so it found the real `dotnet.exe` and never the `.cmd` shim --
    which is exactly the gap the web-check port closed, reintroduced by my own guard on it. There is no
    condition here to get wrong: the caller resolved the tool, so the resolved path is what runs.
    """
    argv = [executable, *suite.argv(no_build)[1:]]
    started = time.monotonic()
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
        code, output, timed_out = proc.returncode, (proc.stdout or "") + (proc.stderr or ""), False
    except subprocess.TimeoutExpired as expired:
        # `exit` is non-zero HERE as well as `timed_out`, so the sequence check below needs only one of
        # them. The first version read `if step.timed_out or step.exit != 0`, and a mutation that dropped
        # the `timed_out` clause was an EQUIVALENT mutant -- it changed nothing, because a timeout always
        # also reports a non-zero exit. The redundancy is removed rather than kept and defended.
        code, timed_out = EXIT_FAILED, True
        output = ((expired.stdout or b"") if isinstance(expired.stdout, bytes) else (expired.stdout or ""))
        output = str(output) + (str(expired.stderr) if expired.stderr else "")
        output += f"\n[harness] no result within {timeout}s"
    except (OSError, FileNotFoundError) as exc:
        # A spawn that cannot start is a CONFIGURATION fault, not a red suite: nothing ran, so reporting
        # it as "suite 1 failed" would send the operator looking at test output that does not exist.
        raise Refusal("DOTNET-INVOCATION-FAILED",
                      f"could not start {suite.project}: {exc}", EXIT_REFUSED) from exc
    return Step(name=suite.name, project=suite.project, argv=list(argv), exit=code,
                seconds=round(time.monotonic() - started, 1), timed_out=timed_out, output=output)


def budget_for(index: int, total: int, timeout: int) -> int:
    """The seconds suite `index` may take, out of ONE budget for the whole sequence.

    The first invocation gets a cold-start share because it restores and builds the whole graph; the rest
    SHARE what is left, split evenly. A flat per-suite share is what makes three suites able to consume
    three times the caller's `--timeout`, which is the unbounded behaviour the retirement was about --
    so the sum is bounded by the budget plus the rounding of one second per suite.
    """
    if total <= 1:
        return max(1, timeout)
    first = max(1, int(timeout * DOTNET_COLD_START_SHARE))
    remaining = max(1, timeout - first)
    return first if index == 0 else max(1, remaining // (total - 1))


def execute(root: Path, no_build: bool, timeout: int) -> Report:
    executable = resolve_dotnet()
    check_projects(root, SUITES)
    report = Report()
    for index, suite in enumerate(SUITES):
        step = run_suite(executable, suite, root, no_build, budget_for(index, len(SUITES), timeout))
        report.ran.append(step)
        if step.exit != 0:
            report.failed_step = index + 1
            return report
    return report


def render(report: Report, as_json: bool) -> None:
    if as_json:
        print(json.dumps({
            "tool": TOOL_ID,
            "verdict": "OK" if report.failed_step is None else "FAILED",
            "exitCode": EXIT_OK if report.failed_step is None else EXIT_FAILED,
            "failedStep": report.failed_step,
            "declaredSuites": [{"name": s.name, "project": s.project, "filter": s.filter}
                               for s in SUITES],
            "evidence": EVIDENCE,
            "steps": [step.__dict__ for step in report.ran],
        }, indent=2))
        return
    for step in report.ran:
        mark = "ok " if step.exit == 0 and not step.timed_out else "RED"
        print(f"[harness] {mark} {step.name} ({step.project}) exited {step.exit} in {step.seconds}s")
    red = report.red
    if red is None:
        print(f"[harness] OK — {len(report.ran)} suite(s) green. Evidence covered: {EVIDENCE}.")
        return
    kind = "timed out" if red.timed_out else f"exited {red.exit}"
    print(f"[harness] FAILED at step {report.failed_step} of {len(SUITES)}: {red.name} "
          f"({red.project}) {kind}", file=sys.stderr)
    skipped = len(SUITES) - report.failed_step
    if skipped > 0:
        print(f"[harness] {skipped} later suite(s) not run (fail-fast: they are independent of this one)",
              file=sys.stderr)
    for line in (red.output or "").splitlines()[-12:]:
        print(f"  {line}", file=sys.stderr)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run the three focused onboarding suites, fail-fast (replaces "
                    "first-session-progression-harness.ps1).")
    parser.add_argument("--no-build", action="store_true",
                        help="pass --no-build to each dotnet test (the original's -NoBuild)")
    parser.add_argument("--root", type=Path, default=None,
                        help="repo root the project paths are relative to")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds for the WHOLE sequence (default {DEFAULT_TIMEOUT}); shared and "
                             f"fail-fast, because the suites are independent")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    args = parser.parse_args(argv)

    if args.timeout <= 0:
        message = "INVALID-TIMEOUT: --timeout must be positive"
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": "INVALID-TIMEOUT",
                              "detail": message, "exitCode": EXIT_USAGE}, indent=2))
        else:
            print(f"[harness] REFUSED: {message}", file=sys.stderr)
        return EXIT_USAGE

    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        report = execute(root, args.no_build, args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": refusal.exit_code,
                              "declaredSuites": [{"name": s.name, "project": s.project}
                                                 for s in SUITES]}, indent=2))
        else:
            print(f"[harness] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    render(report, args.json)
    return EXIT_OK if report.failed_step is None else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
