#!/usr/bin/env python3
"""
Guard: gk-core/tests/FusionRpg.Bench still compiles (Release).

Why the PowerShell form was retired (scripts/guard-bench-compile.ps1, 30 lines):

  * **`dotnet build` ran with no timeout.** An unbounded build inside a guard is a guard that can
    hang the whole ci tier with no report. This port takes `--timeout` and treats expiry as a
    REFUSAL naming the stage, never as a pass.
  * **The error-detail filter was `' error '` matched against a merged stream.** That needs a
    space on both sides, so an MSBuild line formatted `error CS0103:` at the start of a segment,
    or a path containing the literal " error ", changes what is reported. A build can fail and
    print nothing the operator can act on. This port matches the MSBuild `error` token itself and
    caps the excerpt, so the finding always carries its cause.
  * **A failed temp delete was best-effort.** `if (Test-Path) { Remove-Item ... -Recurse -Force }`
    ignores a failed removal, and this repo has a measured 65.5 GB temp-leak incident from exactly
    that shape. Per docs/contributing/testing-standard.md R3, a failed cleanup is a FAILURE here,
    retried a bounded number of times and then reported - never swallowed.
  * **No machine-readable result.** A guard that only prints "OK" can be grepped, misread, and
    cannot be asserted on. `--json` emits the project, the timeout, the verdict and the excerpt.

registry-contract C5 is the reason this guard exists at all: `FusionRpg.Bench` is an Exe with no
tests, so `dotnet test` on it restores and exits 0 without building - the same false evidence
R-TV2 removed from the Launcher. The only honest proof for this project is "it still compiles".

The build goes into a temp OutputPath, never the repo's own bin/obj, so it never collides with a
concurrent build of the same project. This guard needs nothing machine-specific (no game install,
no interop refs), so it never skips.

Usage (repo root):
    python gk-core/scripts/guard-bench-compile.py
    python gk-core/scripts/guard-bench-compile.py --root <path>      # falsifier fixture
    python gk-core/scripts/guard-bench-compile.py --json             # machine-readable result
    python gk-core/scripts/guard-bench-compile.py --timeout 300

Exit 0 = compiles. 1 = a finding (the build failed, or its temp output could not be removed).
Exit 64 = REFUSED: a precondition made the check impossible (no project, no dotnet, or timeout).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

# testing-standard.md R3 does not say "give up immediately": a build's own teardown can hold a
# handle for a moment. Three bounded retries, then a FAILURE. Never an unbounded sleep, never a
# swallowed exception.
CLEANUP_ATTEMPTS = 3
CLEANUP_BACKOFF_SECONDS = 0.5
ERROR_EXCERPT_LINES = 20

# MSBuild's own error token. Matched as a word so a path or message containing "error" is not
# mistaken for a diagnostic, and a diagnostic is not missed for lacking surrounding spaces.
ERROR_LINE = re.compile(r"\berror\s+[A-Z]+\d+|\berror\s+MSB\d+", re.IGNORECASE)


class Refusal(Exception):
    """A named precondition failure: the check could not be performed. Never reported as a pass."""

    def __init__(self, stage: str, reason: str, detail: str = "") -> None:
        super().__init__(f"[{stage}] {reason}" + (f"\n{detail}" if detail else ""))
        self.stage, self.reason, self.detail = stage, reason, detail


def find_error_lines(output: str) -> list[str]:
    """The distinct diagnostic lines from a build log, in first-seen order, capped.

    MSBuild writes the same diagnostic to more than one stream, so a plain filter yields the same
    error twice and burns the excerpt budget on a repeat. Returns [] when the log names no error,
    which the caller must still treat as a FAILURE - an unexplained non-zero exit is the finding.
    """
    hits: list[str] = []
    seen: set[str] = set()
    for line in output.splitlines():
        if not ERROR_LINE.search(line):
            continue
        key = line.strip()
        if key in seen:
            continue
        seen.add(key)
        hits.append(key)
        if len(hits) >= ERROR_EXCERPT_LINES:
            break
    return hits


def remove_tree(path: Path) -> str | None:
    """Delete `path`, returning None on success or a message describing why it could not be.

    A failure here is a FAILURE, not a warning: the temp build output is the only thing standing
    between a guard run and another multi-gigabyte leak.
    """
    last = "unknown reason"
    for attempt in range(1, CLEANUP_ATTEMPTS + 1):
        try:
            shutil.rmtree(path, ignore_errors=False)
            return None
        except FileNotFoundError:
            return None
        except OSError as exc:
            last = f"{type(exc).__name__}: {exc}"
            # Windows keeps a just-exited process's handle briefly; make the file writable and retry.
            try:
                os.chmod(path, 0o700)
            except OSError:
                pass
            if attempt < CLEANUP_ATTEMPTS:
                time.sleep(CLEANUP_BACKOFF_SECONDS * attempt)
    return f"could not remove {path} after {CLEANUP_ATTEMPTS} attempts ({last})"


def run_guard(root: Path, timeout: float) -> dict:
    """Perform the check and return the machine-readable result. Raises Refusal when it cannot run."""
    project = root / "tests" / "FusionRpg.Bench" / "FusionRpg.Bench.csproj"
    if not project.is_file():
        raise Refusal("precondition", "PROJECT-MISSING",
                      f"{project} does not exist, so 'does it still compile' has no subject")

    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise Refusal("precondition", "DOTNET-MISSING",
                      "dotnet is not on PATH, so a compile check cannot be performed")

    # A unique temp OutputPath: never the repo's bin/obj, so this never collides with a concurrent
    # build of the same project (the defect guard-injector-compile.py's shape was written to avoid).
    out_dir = Path(tempfile.gettempdir()) / f"fusionrpg-bench-compile-{uuid.uuid4().hex}"

    started = time.monotonic()
    try:
        completed = subprocess.run(  # noqa: S603 - fixed argv, no shell
            [dotnet, "build", str(project), "-c", "Release",
             f"-p:OutputPath={out_dir}{os.sep}", "-nologo"],
            capture_output=True, text=True, timeout=timeout, cwd=str(root), check=False,
        )
    except subprocess.TimeoutExpired as exc:
        # Refused, not passed and not failed: we do not know whether it compiles.
        raise Refusal("build", "BUILD-TIMED-OUT",
                      f"dotnet build exceeded --timeout {timeout:g}s; the verdict is UNKNOWN, and an "
                      f"unknown verdict is never a pass") from exc
    finally:
        cleanup_error = remove_tree(out_dir)

    duration = round(time.monotonic() - started, 2)
    output = f"{completed.stdout}\n{completed.stderr}"
    result = {
        "guard": "bench-compile",
        "project": str(project.relative_to(root)).replace("\\", "/"),
        "configuration": "Release",
        "timeout_seconds": timeout,
        "duration_seconds": duration,
        "exit_code": completed.returncode,
        "cleanup_error": cleanup_error,
        "errors": find_error_lines(output),
    }

    if completed.returncode != 0:
        result["verdict"] = "FAIL"
        result["reason"] = "BUILD-FAILED"
    elif cleanup_error:
        # The build was fine, but the guard cannot claim a clean run while it has leaked a temp tree.
        result["verdict"] = "FAIL"
        result["reason"] = "TEMP-CLEANUP-FAILED"
    else:
        result["verdict"] = "OK"
        result["reason"] = "COMPILES"
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: tests/FusionRpg.Bench still compiles (Release).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--timeout", type=float, default=600.0,
                        help="hard ceiling on the build in seconds (default: 600)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    if args.timeout <= 0:
        refusal = Refusal("precondition", "TIMEOUT-NOT-POSITIVE",
                          f"--timeout {args.timeout!r} would remove the ceiling this guard exists to keep")
        print(f"BENCH-COMPILE-GUARD REFUSED {refusal}", file=sys.stderr)
        return 64

    try:
        result = run_guard(args.root.resolve(), args.timeout)
    except Refusal as refusal:
        print(f"BENCH-COMPILE-GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": "bench-compile", "verdict": "REFUSED",
                              "stage": refusal.stage, "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return 64

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print("BENCH COMPILE GUARD OK")
    else:
        for line in result["errors"]:
            print(line)
        if not result["errors"]:
            # An unexplained non-zero exit still names its stage rather than printing nothing.
            print(f"(no error line in the build log; dotnet build exited "
                  f"{result['exit_code']} - read the full log)")
        if result["cleanup_error"]:
            print(result["cleanup_error"])
        print(f"BENCH COMPILE GUARD FAILED ({result['reason']})")
    return 0 if result["verdict"] == "OK" else 1


if __name__ == "__main__":
    sys.exit(main())
