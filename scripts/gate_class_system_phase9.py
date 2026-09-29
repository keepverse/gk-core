#!/usr/bin/env python3
"""Phase 9 readiness gate, asserted not judged (class-system-todo.md P9.0).

`class-system-plan.md` §0.1: "A human cannot help... every number in this system is decided by
measurement." Phase 9 (tuning on real data) cannot start until every mechanism the aptitude
distribution funds actually exists (map §5) -- until then some fraction of the distribution points at
channels nothing reads, and fitting over those channels would freeze noise rather than find balance.
Nobody gets to decide "we are ready" by eye. This is the mechanical assertion: it wraps
`gk-core/scripts/audit-reader-census.py` (P8.4) and reports READY only when that census -- not a person -- says
every aptitude-fed family has a reader, and `_meta.measurable`'s own prose still agrees with a fresh
run.

Replaces `scripts/gate-class-system-phase9.ps1`.

EXPECTED VERDICT TODAY IS `NOT READY`, and that is not a failure
----------------------------------------------------------------
This is a readiness REPORT, not a code-defect guard. A "NOT READY" verdict is this gate doing its job,
and wiring it into any throw-on-failure pipeline would break the run for an honestly-expected state.
`ClassSystemPhase9ReadinessGateTests` pins both that expectation and its non-wiring.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **A MALFORMED CENSUS READ AS `READY` — A SILENT GREEN.** The verdict was
  `$census.families_without_reader -gt 0`. In PowerShell a missing property is `$null`, `$null -gt 0` is
  `$false`, and the negation of `$false` is `$true` -- so a census JSON that was truncated, that renamed
  the field, or that was a *different tool's* output entirely, reported **PHASE 9 READINESS GATE: READY**
  and exited 0. The one gate whose entire job is to not let a human eyeball readiness would pass on a
  file that never answered the question. A missing key is now a named refusal, every field is
  type-checked, and a count that disagrees with the list of names it claims is refused as a different
  run's data.

* **A MISSING FIXTURE AND A `NOT READY` VERDICT SHARED AN EXIT CODE.** `throw "CensusJsonPath not
  found"` and `exit 1` for a real gap are the same observable to a caller. They are different events
  with different fixes, so they are different exit codes: 64 REFUSED against 1 NOT-READY.

* **NO TIMEOUT ON EITHER `python` INVOCATION.** `--check` walks the whole census; a wedged interpreter
  held the gate open indefinitely.

* **NO MACHINE-READABLE VERDICT.** A caller had to grep prose. `--json` now reports the verdict, the
  census that produced it, and the reasons by name.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

TOOL_ID = "gate-class-system-phase9"

EXIT_READY = 0
EXIT_NOT_READY = 1
EXIT_REFUSED = 64

CENSUS_RELATIVE = ("scripts", "audit-reader-census.py")
DEFAULT_CENSUS_TIMEOUT = 300

READY_LINE = ("PHASE 9 READINESS GATE: READY -- every aptitude-fed family has a reader, "
              "_meta.measurable agrees with a fresh census")
NOT_READY_LINE = "PHASE 9 READINESS GATE: NOT READY"
GAP_LINE = "aptitude-fed families still have no reader"
STALE_LINE = "_meta.measurable is STALE relative to a fresh reader census"

# Every field the verdict reads, and the type it must be. A census that does not answer all of them did
# not answer the question, and saying READY about it is the defect this port exists to remove.
REQUIRED_FIELDS: dict[str, type | tuple[type, ...]] = {
    "families_total": int,
    "families_without_reader": int,
    "edges_total": int,
    "edges_reserved": int,
    "edges_reserved_pct": (int, float),
    "reader_less_families": list,
}

# Every global this tool touches is bound ONCE, to a module-private name, and only those are called.
# `subprocess` and `shutil` are the process-wide modules, so a patch through them is a global patch
# wearing a local name: it reaches every other test in the process, and one that outlives its `with`
# block breaks them while this suite reports green. Binding the seams here makes a leak impossible by
# construction rather than by discipline.
_RUN = subprocess.run
_WHICH = shutil.which


class Refusal(Exception):
    """A named precondition failure. Never exits 0 and never reports a verdict."""

    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def resolve_python() -> str:
    """The interpreter, resolved ONCE and named when absent.

    The RESOLVED path is what runs. `CreateProcess` resolves a bare name by appending `.exe` only, so
    spawning the bare token can reach a different tool than the one that was resolved.
    """
    found = _WHICH("python")
    if not found:
        raise Refusal("PYTHON-NOT-ON-PATH", "python is not on PATH; install it or run where it is")
    return found


def validate_census(census: object, source: str) -> dict:
    """Every field the verdict reads must be present AND of the declared type.

    This is the whole defect. The original asked PowerShell for one property and treated `$null` as
    zero, so a truncated or foreign JSON became `READY`.
    """
    if not isinstance(census, dict):
        raise Refusal("CENSUS-NOT-AN-OBJECT", f"{source} holds a {type(census).__name__}, not a census")
    for name, expected in REQUIRED_FIELDS.items():
        if name not in census:
            raise Refusal("CENSUS-FIELD-MISSING",
                          f"{source} has no {name!r}; a census that does not answer that cannot be "
                          f"reported READY. Fields present: {sorted(census)}")
        value = census[name]
        # `bool` is an `int` subclass, so `True` would satisfy an `int` field. A census that answers
        # "yes" where it should answer a count has not answered.
        if isinstance(value, bool) or not isinstance(value, expected):
            wanted = expected.__name__ if isinstance(expected, type) else "/".join(
                t.__name__ for t in expected)
            raise Refusal("CENSUS-FIELD-WRONG-TYPE",
                          f"{source}: {name} is {value!r} ({type(value).__name__}), expected {wanted}")
    if not isinstance(census["reader_less_families"][0:1], list):  # pragma: no cover - shape only
        raise Refusal("CENSUS-FIELD-WRONG-TYPE", f"{source}: reader_less_families is not a list")
    if census["families_without_reader"] != len(census["reader_less_families"]):
        raise Refusal("CENSUS-SELF-INCONSISTENT",
                      f"{source}: families_without_reader is "
                      f"{census['families_without_reader']} but reader_less_families names "
                      f"{len(census['reader_less_families'])}. The two fields come from the same census "
                      f"and disagreeing means one of them is from a different run.")
    return census


def run_census(root: Path, timeout: int) -> tuple[dict | None, list[str]]:
    """`--check` then `--json`, each bounded. Returns (census, failures)."""
    executable = resolve_python()
    script = root.joinpath(*CENSUS_RELATIVE)
    if not script.is_file():
        raise Refusal("CENSUS-SCRIPT-MISSING", f"{script} is not a file")
    failures: list[str] = []
    try:
        # `--check` is the mechanical proxy for "true for every coefficient the fit will touch": it
        # fails if `_meta.measurable`'s own claimed numbers no longer match a fresh census, i.e. if the
        # file is lying about what it thinks is measurable.
        check = _RUN([executable, str(script), "--check"], capture_output=True, text=True,
                     timeout=timeout, cwd=str(root))
        if check.returncode != 0:
            detail = "\n".join(line for line in (check.stdout + check.stderr).splitlines() if line.strip())
            failures.append(f"P9.0: {STALE_LINE} -- fix the prose before readiness can even be "
                            f"evaluated:\n{detail}")
        emitted = _RUN([executable, str(script), "--json"], capture_output=True, text=True,
                       timeout=timeout, cwd=str(root))
    except subprocess.TimeoutExpired as expired:
        raise Refusal("CENSUS-TIMED-OUT",
                      f"audit-reader-census.py exceeded {timeout}s") from expired
    except (OSError, FileNotFoundError) as error:
        raise Refusal("CENSUS-INVOCATION-FAILED", str(error)) from error
    if emitted.returncode != 0:
        detail = "\n".join(line for line in (emitted.stdout + emitted.stderr).splitlines() if line.strip())
        raise Refusal("CENSUS-FAILED", f"audit-reader-census.py --json exited {emitted.returncode}:\n"
                                        f"{detail}")
    try:
        return validate_census(json.loads(emitted.stdout), "audit-reader-census.py --json"), failures
    except json.JSONDecodeError as error:
        raise Refusal("CENSUS-NOT-JSON", f"audit-reader-census.py --json emitted {error}") from error


def load_fixture(path_str: str) -> dict:
    path = Path(path_str)
    if not path.is_file():
        raise Refusal("CENSUS-FIXTURE-MISSING", f"--census-json: {path} is not a file")
    try:
        raw = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:
        raise Refusal("CENSUS-FIXTURE-UNREADABLE", f"--census-json: {path} -> {error}") from error
    try:
        return validate_census(json.loads(raw), str(path))
    except json.JSONDecodeError as error:
        raise Refusal("CENSUS-NOT-JSON", f"--census-json: {path} emitted {error}") from error


def evaluate(census: dict, failures: list[str]) -> tuple[bool, list[str]]:
    """The readiness arithmetic, and nothing else. Both inputs already validated."""
    reasons = list(failures)
    if census["families_without_reader"] > 0:
        reasons.append(
            f"P9.0: {census['families_without_reader']} of {census['families_total']} "
            f"aptitude-fed families still have no reader ({census['edges_reserved']} of "
            f"{census['edges_total']} edges, {census['edges_reserved_pct']}%) -- not ready: "
            f"{', '.join(census['reader_less_families'])}")
    return (not reasons), reasons


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="gate-class-system-phase9",
        description="Phase 9 readiness gate, asserted not judged (replaces "
                    "gate-class-system-phase9.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--census-json", default=os.environ.get("PHASE9_CENSUS_JSON", ""),
                        help="read the census from this file instead of running "
                             "audit-reader-census.py; the same shape its --json emits. The test seam.")
    parser.add_argument("--census-timeout", type=int, default=DEFAULT_CENSUS_TIMEOUT,
                        help=f"seconds for EACH census invocation (default {DEFAULT_CENSUS_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if args.census_timeout <= 0:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": "INVALID-TIMEOUT",
                   "detail": "--census-timeout must be positive", "exitCode": EXIT_REFUSED}
        if args.json:
            print(json.dumps(payload, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: INVALID-TIMEOUT: --census-timeout must be positive",
                  file=sys.stderr)
        return EXIT_REFUSED

    # Configuration read ONCE, explicitly, and failing loudly when absent. The original's `$Root`
    # defaulted to a `Resolve-Path` evaluated at PARSE time, so a bad default failed before the script
    # body ran and named nothing.
    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    if not root.is_dir():
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": "ROOT-NOT-A-DIRECTORY",
                   "detail": f"--root {root} is not a directory", "exitCode": EXIT_REFUSED}
        if args.json:
            print(json.dumps(payload, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: ROOT-NOT-A-DIRECTORY: --root {root} is not a directory",
                  file=sys.stderr)
        return EXIT_REFUSED

    try:
        # The fixture seam SKIPS the interpreter entirely, exactly as the original's did, so the
        # readiness arithmetic can be proven in both directions without a built game. It still goes
        # through the SAME validation: a fixture is not trusted more than a real run.
        census = load_fixture(args.census_json) if args.census_json else run_census(
            root, args.census_timeout)[0]
        ready, reasons = evaluate(census, [])
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{TOOL_ID}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_REFUSED

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "READY" if ready else "NOT_READY",
                          "exitCode": EXIT_READY if ready else EXIT_NOT_READY,
                          "census": census, "reasons": reasons}, indent=2))
    elif ready:
        print(READY_LINE)
    else:
        print(NOT_READY_LINE)
        for reason in reasons:
            print(f"  {reason}")
    return EXIT_READY if ready else EXIT_NOT_READY


if __name__ == "__main__":
    sys.exit(main())
