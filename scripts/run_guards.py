#!/usr/bin/env python3
"""One runner for the enforcement registry's guards (solid-enforcement `guard-runner`).

Reads `gk-core/scripts/enforcement-registry.v1.json` and runs the guards it selects. This is the ONE place that
decides which guard runs where: `ci.yml`, `release.yml`, `nightly.yml`, `deploy-play.py` and
`verify-change.py` all call it, so wiring a guard is a registry edit rather than three hand-kept lists --
the mechanism that once left nine guards unwired.

Replaces `run-guards.ps1`.

EVERY SELECTED GUARD RUNS EVEN AFTER ONE FAILS, and a summary table prints before a non-zero exit: one
red guard never hides a second. That is the masking defect `ci.yml`'s old dotnet-test step met once
already, and it is the reason this tool is a LOOP and not a fail-fast chain.

WHY THE POWERSHELL FORM WAS RETIRED
-------------------------------------
* **THERE WAS NO TIMEOUT ON ANY GUARD INVOCATION, AND THE RUNNER IS THE LONGEST THING IN CI.** A guard
  that wedges -- a restore waiting on a locked feed, a subprocess that never returns -- held the whole
  run open indefinitely, and the only signal was the absence of the next table row. Every invocation is
  bounded by `--timeout` now, and a timeout is a named refusal that NAMES THE GUARD, because a table
  row reading "timed out" is not evidence about the guard it names.

* **THE STDERR LOG WAS DELETED WITH `-ErrorAction SilentlyContinue`.** A per-guard temp file, removed on a
  failure the runner is not allowed to see -- so a locked or read-only temp directory leaked one file per
  guard per run, silently and forever. This repository's own testing standard (R3) says a failed temp
  delete is a FAILURE precisely because of a 65.5 GB incident that came from exactly this shape. The
  port removes the log and reports a failure to remove it.

* **THE INTERPRETER WARNING CHECKED THE WRONG NAME AND PRODUCED A PHANTOM.** It probed for
  `powershell` while the dispatcher preferred `pwsh` and fell back to `powershell`. On any machine with
  only PowerShell 7 -- the normal case on Linux and macOS, and increasingly on Windows -- the warning
  said "PATH has no 'powershell'" for a runner that was about to work fine. A warning that cries wolf on
  the correct platform trains its reader to skip it, and the warning exists precisely to stop a phantom
  red from being read as a verdict.

* **THE SUMMARY WAS A `Format-Table` RENDERED INTO A STRING.** There was no machine-readable output at
  all, so nothing could assert on a run: not the guard ids, not the exit codes, not which of them were
  red. `--json` now reports every guard, and `test_run_guards.py` reads THAT rather than parsing a
  table, so a fixture states its expectation from the tool's own surface.

* **THE `default` DISPATCH BRANCH WROTE ITS EXPLANATION TO A FILE IT THEN DELETED UNCONDITIONALLY** --
  the message explaining an undispatchable extension was discarded on the success path. The port puts it
  in the report and in the refusal, where it is read.

WHAT THIS TOOL MUST NOT DO
--------------------------
It must never run a guard the registry did not select, and it must never report a green run for an EMPTY
selection. Both were explicit refusals in the original and are refusals here by name.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "run-guards"
EXIT_OK = 0
EXIT_FAILED = 1
# The dispatcher's own vocabulary for "this guard's file cannot be run at all". Carried through unchanged
# so a caller that scripted the old 64 still sees 64 rather than a generic failure it cannot classify.
EXIT_UNDISPATCHABLE = 64

TIERS = ("ci", "local")
STATUSES = ("gating", "backlog")
REGISTRY_SCHEMA_VERSION = 1
DEFAULT_TIMEOUT = 900
# No `summary` stage: `summarise` RETURNS a verdict and an exit code rather than raising, so it
# cannot refuse. A stage that cannot fail is not a stage, and listing one would invite a case that
# asserts against something unreachable.
STAGES = ("registry", "arguments", "range", "selection", "catalog", "interpreters", "dispatch")

# A range switch the registry may name, in either spelling, and which is DROPPED together with its
# `{ciRange}` placeholder when there is no range. Both spellings are listed because the catalog holds both
# a PowerShell guard and a Python one and the row carries whichever the guard understands; a one-spelling
# list would silently stop dropping on the other.
RANGE_SWITCHES = ("-Range", "-RequireExplicitRange", "--range", "--require-explicit-range")
CI_RANGE_PLACEHOLDER = "{ciRange}"


class Refusal(Exception):
    """A named precondition or stage failure. The run says WHICH stage, and never exits 0 having not run."""

    def __init__(self, stage: str, reason: str, detail: str = "", exit_code: int = EXIT_FAILED) -> None:
        super().__init__(f"{stage}/{reason}: {detail}" if detail else f"{stage}/{reason}")
        self.stage = stage
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


def read_registry(root: Path) -> dict:
    """The enforcement registry, validated.

    EVERY refusal here is one the original made, by name. A registry that is absent, unparseable, of the
    wrong schema, empty, or carrying a guard with no `script`/`tier`/`status` evidence, must STOP the run
    rather than select a subset: a catalog that has quietly lost a guard would otherwise report a green
    run over fewer guards than anyone believes are gating.
    """
    path = root / "scripts" / "enforcement-registry.v1.json"
    if not path.is_file():
        raise Refusal("registry", "REGISTRY-MISSING", f"enforcement registry missing: {path}")
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("registry", "REGISTRY-UNREADABLE", f"enforcement registry is not valid JSON: {path}: {exc}")
    if doc.get("schemaVersion") != REGISTRY_SCHEMA_VERSION:
        raise Refusal("registry", "REGISTRY-SCHEMA",
                      f"unsupported enforcement registry schemaVersion: {doc.get('schemaVersion')}")
    guards = doc.get("guards")
    if not isinstance(guards, dict) or not guards:
        raise Refusal("registry", "CATALOG-EMPTY",
                      "enforcement registry has no guards; no guards selected because the catalog is "
                      "empty (RED)")
    for name, row in guards.items():
        if not isinstance(row, dict) or any(
                not str(row.get(field) or "").strip() for field in ("script", "tier", "status")):
            raise Refusal("registry", "GUARD-EVIDENCE-MISSING",
                          f"enforcement registry guard '{name}' is missing script/tier/status evidence")
        if row["tier"] not in TIERS:
            raise Refusal("registry", "GUARD-TIER-INVALID",
                          f"enforcement registry guard '{name}' has invalid tier '{row['tier']}'")
        if row["status"] not in STATUSES:
            raise Refusal("registry", "GUARD-STATUS-INVALID",
                          f"enforcement registry guard '{name}' has invalid status '{row['status']}'")
    for exemption in doc.get("verificationExemptions") or []:
        if (not isinstance(exemption, dict)
                or not str(exemption.get("id") or "").strip()
                or not str(exemption.get("reason") or "").strip()
                or not exemption.get("paths")):
            raise Refusal("registry", "EXEMPTION-INCOMPLETE",
                          "verification exemption is missing id, paths, or reason")
        for pattern in exemption["paths"]:
            text = str(pattern)
            if (text.startswith("**")
                    or any(text.startswith(f"{root_name}/**") or text.startswith(f"{root_name}/*/")
                           for root_name in ("scripts", "tools", ".github"))):
                raise Refusal("registry", "EXEMPTION-CATCH-ALL",
                              f"verification exemption '{exemption['id']}' is a catch-all root: {text}")
    return doc


def parse_local_args(entries: list[str]) -> dict[str, dict[str, str]]:
    """`ID:KEY=VALUE` triples into the per-guard local-argument map.

    MACHINE-LOCAL BY CONSTRUCTION, and that is the reason for the `ID:` prefix: the only caller-supplied
    value a guard ever receives is something that must not be committed (the game dir), so the argument
    is addressed AT a guard rather than merged into a global one. A malformed entry is a refusal by name
    rather than a silently dropped argument -- a dropped machine-local value is the kind of thing that
    makes a guard measure the wrong tree.
    """
    parsed: dict[str, dict[str, str]] = {}
    for entry in entries:
        if ":" not in entry or "=" not in entry:
            raise Refusal("arguments", "LOCAL-ARG-MALFORMED",
                          f"expected ID:KEY=VALUE, got '{entry}'")
        guard_id, pair = entry.split(":", 1)
        key, _, value = pair.partition("=")
        if not guard_id or not key:
            raise Refusal("arguments", "LOCAL-ARG-MALFORMED",
                          f"expected ID:KEY=VALUE, got '{entry}'")
        parsed.setdefault(guard_id, {})[key] = value
    return parsed


def repo_is_git(root: Path) -> bool:
    """Whether the root is inside a git work tree.

    Decided ONCE, and used for exactly one thing: whether a missing `--ci-range` is a refusal or a
    legitimate working-tree run. A non-git fixture has no push range, and refusing there would make the
    contract untestable with a planted root.
    """
    try:
        proc = subprocess.run(["git", "rev-parse", "--is-inside-work-tree"], capture_output=True,
                              text=True, timeout=60, cwd=str(root))
    except (OSError, subprocess.TimeoutExpired):
        return False
    return proc.returncode == 0 and proc.stdout.strip().lower() == "true"


def resolve_ci_range(tier: str, ci_range: str, root: Path) -> str:
    """A CI caller MUST provide the complete push/diff range; a missing range is not a green range."""
    is_git = repo_is_git(root)
    if tier == "ci" and is_git and not ci_range.strip():
        raise Refusal("range", "CI-RANGE-REQUIRED",
                      "CI guard range is required; pass --ci-range <base>..<head> (a missing range is "
                      "not a green range)")
    if tier == "ci" and ci_range.strip() and ".." not in ci_range.strip():
        raise Refusal("range", "CI-RANGE-MALFORMED",
                      f"CI guard range is malformed; expected <base>..<head>: {ci_range}")
    return ci_range.strip()


def resolve_guard_args(guard_id: str, row: dict, tier: str, ci_range: str, local_args: dict,
                       is_git: bool) -> list[str]:
    """The argv for one guard: the registry's `args` for the tier, plus the caller's local exceptions.

    A `{ciRange}` placeholder with no range DROPS ITSELF AND THE SWITCH BESIDE IT. Keeping the switch
    without its value would hand a guard `-Range` and nothing after it, and the failure would be an
    argparse error from a guard that never had a chance to run.
    """
    resolved: list[str] = []
    for raw in (row.get("args") or {}).get(tier, []):
        arg = str(raw)
        if arg == CI_RANGE_PLACEHOLDER:
            if not ci_range.strip():
                if tier == "ci" and is_git:
                    raise Refusal("dispatch", "GUARD-RANGE-REQUIRED",
                                  f"guard '{guard_id}' requires an explicit CI range, but --ci-range "
                                  f"was empty")
                # A non-git fixture has no push range. Drop the switch and its placeholder together; a
                # real CI checkout never reaches this path.
                while resolved and resolved[-1] in RANGE_SWITCHES:
                    resolved.pop()
                continue
            arg = ci_range
        resolved.append(arg)
    # The one caller-supplied exception: machine-local values (the game dir) that must never be
    # committed. Everything else comes from the registry.
    for key, value in sorted((local_args.get(guard_id) or {}).items()):
        resolved.extend([f"-{key}", str(value)])
    return resolved


def select(catalog: dict, tier: str, only: list[str], skip: list[str], include_backlog: bool) -> list[str]:
    """The guards this run will execute, sorted, and every selection rule in one place.

    `-Only` bypasses the tier/status filters but NOT `-Skip`... and it is checked against the catalog so
    an unknown id is a refusal rather than a silently empty run.
    """
    if only:
        for guard_id in only:
            if guard_id not in catalog:
                raise Refusal("selection", "UNKNOWN-GUARD", f"unknown guard: {guard_id}")
        return list(only)
    selected = []
    for guard_id in sorted(catalog):
        row = catalog[guard_id]
        if guard_id in skip:
            continue
        if row["status"] == "backlog" and not include_backlog:
            continue
        if tier == "ci" and row["tier"] != "ci":
            continue
        # ci.yml runs an `own-step` guard in its own isolated step; the runner must not run it twice.
        if tier == "ci" and row.get("ciEntry") == "own-step":
            continue
        selected.append(guard_id)
    return selected


def check_selection(guards: list[str], catalog: dict, tier: str, only: list[str],
                    include_backlog: bool) -> None:
    """An EMPTY selection is RED, and a CI run with no GATING guard is RED.

    Both were explicit in the original and both are the failure a green exit would otherwise hide: a
    tier filter that matches nothing must not print an empty table and exit 0.
    """
    if not guards:
        raise Refusal("selection", "NO-GUARDS-SELECTED",
                      f"no guards selected for tier '{tier}'; an empty guard run is RED")
    if (tier == "ci" and not only and not include_backlog
            and not any(catalog[g]["status"] == "gating" for g in guards)):
        raise Refusal("selection", "NO-GATING-GUARDS",
                      "CI selected no gating guards; an evidence-free guard run is RED")


def check_scripts_exist(root: Path, guards: list[str], catalog: dict) -> None:
    """Every selected guard's FILE must exist, checked BEFORE any guard runs.

    Checking up front means a missing script is one refusal naming it, rather than a red row discovered
    half-way through a batch whose earlier guards have already been reported.
    """
    for guard_id in guards:
        script = root / str(catalog[guard_id]["script"])
        if not script.is_file():
            raise Refusal("catalog", "GUARD-SCRIPT-MISSING",
                          f"guard script missing: {guard_id} -> {catalog[guard_id]['script']}")


def missing_interpreters(selected_scripts: list[str], only: list[str]) -> list[str]:
    """Interpreters this batch needs and PATH does not have, by NAME.

    The original probed for `powershell` while dispatching on `pwsh` first, so a PowerShell-7-only
    machine -- the normal case off Windows -- got a warning naming an interpreter it never needed. The
    probe here asks for the interpreter each selected script's EXTENSION actually dispatches to, so a
    warning is never a phantom on the platform it is most likely to be read.
    """
    wanted = {"python"} if any(str(s).endswith(".py") for s in selected_scripts) else set()
    if any(str(s).endswith(".ps1") for s in selected_scripts):
        wanted.add("pwsh" if shutil.which("pwsh") else "powershell")
    return sorted(name for name in wanted if shutil.which(name) is None)


@dataclass
class GuardResult:
    id: str
    tier: str
    status: str
    script: str
    argv: list[str]
    exit: int
    seconds: float
    stderr: str = ""


@dataclass
class Report:
    """What the run did. Every count is re-measured; none is a constant carried forward."""

    tier: str = ""
    ci_range: str = ""
    selected: list[str] = field(default_factory=list)
    results: list[dict] = field(default_factory=list)
    red_gating: list[str] = field(default_factory=list)
    backlog: list[str] = field(default_factory=list)
    undispatchable: list[str] = field(default_factory=list)
    missing_interpreters: list[str] = field(default_factory=list)
    environment_note: str = ""
    stages: list[str] = field(default_factory=list)


def dispatch(root: Path, guards: list[str], catalog: dict, tier: str, ci_range: str,
             local_args: dict, timeout: int) -> Report:
    """Run every selected guard, collecting EVERY result -- a failure never stops the loop.

    The loop is the point: one red guard must not hide a second, which is the masking defect `ci.yml`
    met once. Every child runs with the ROOT as its working directory, because a Python guard is a plain
    script that opens relative paths and measured from anywhere else it fails closed with
    "no such path(s): ['src']". That is the runner's contract, not the caller's accident.
    """
    report = Report(tier=tier, ci_range=ci_range, selected=list(guards))
    is_git = repo_is_git(root)
    for guard_id in guards:
        row = catalog[guard_id]
        script = root / str(row["script"])
        argv = resolve_guard_args(guard_id, row, tier, ci_range, local_args, is_git)
        extension = script.suffix.lower()
        started = time.monotonic()
        if extension == ".py":
            command = [sys.executable, str(script), *argv]
        elif extension == ".ps1":
            command = [shutil.which("pwsh") or shutil.which("powershell"), str(script), *argv]
            if command[0] is None:
                raise Refusal("interpreters", "POWERSHELL-NOT-ON-PATH",
                              f"guard '{guard_id}' is a .ps1 and neither pwsh nor powershell is on PATH",
                              EXIT_UNDISPATCHABLE)
        else:
            # Fail CLOSED on an extension the runner cannot dispatch. Guessing would run the wrong thing
            # or skip the guard, and a guard that silently does not run is worse than one that refuses.
            message = (f"runner cannot dispatch '{row['script']}': unsupported extension "
                       f"'{extension}'. A guard must be .ps1 or .py.")
            report.results.append({"id": guard_id, "tier": row["tier"], "status": row["status"],
                                  "script": str(row["script"]), "argv": argv,
                                  "exit": EXIT_UNDISPATCHABLE, "seconds": 0.0, "stderr": message})
            report.undispatchable.append(guard_id)
            continue
        try:
            proc = subprocess.run(command, capture_output=True, text=True, timeout=timeout, cwd=str(root))
            code, stderr = proc.returncode, (proc.stderr or "")
        except subprocess.TimeoutExpired:
            code = EXIT_UNDISPATCHABLE
            stderr = f"timed out after {timeout}s; a wedged guard holds the whole run open"
        except (OSError, FileNotFoundError) as exc:
            code, stderr = EXIT_UNDISPATCHABLE, f"spawn failed: {exc}"
        report.results.append({"id": guard_id, "tier": row["tier"], "status": row["status"],
                               "script": str(row["script"]), "argv": argv, "exit": code,
                               "seconds": round(time.monotonic() - started, 1), "stderr": stderr.strip()})
    report.stages.append("dispatch")
    report.red_gating = [r["id"] for r in report.results if r["status"] == "gating" and r["exit"] != 0]
    report.backlog = [r["id"] for r in report.results if r["status"] == "backlog"]
    return report


def summarise(report: Report) -> tuple[str, str]:
    """The verdict and the exit code, naming EVERY red gating guard.

    RETURNS rather than raises. A red run is not a refusal -- it is a run that COMPLETED, with a result set
    that is the whole point of the report. Raising here discarded that result set, which is exactly what
    the differential against the original caught: the original prints its table before it throws, so a
    reader of a red run sees which guards ran and what each returned, and a port that reports zero guards
    on a red run is less informative than the thing it replaces.
    """
    if report.red_gating:
        return (f"guards failed: {', '.join(report.red_gating)}{report.environment_note}",
                report.results[-1]["exit"] or EXIT_FAILED)
    return f"GUARDS OK - {len(report.results)} guard(s) run, 0 red", EXIT_OK


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run the enforcement registry's guards (replaces run-guards.ps1).")
    parser.add_argument("--tier", choices=TIERS, default="ci")
    parser.add_argument("--only", action="append", default=[], metavar="ID",
                        help="run exactly these guard ids; repeatable")
    parser.add_argument("--skip", action="append", default=[], metavar="ID",
                        help="exclude a caller-invoked guard from the tier batch; repeatable")
    parser.add_argument("--include-backlog", action="store_true",
                        help="also run `status: backlog` guards, which never gate the run")
    parser.add_argument("--local-arg", action="append", default=[], metavar="ID:KEY=VALUE",
                        help="a machine-local argument for one guard, never committed; repeatable")
    parser.add_argument("--ci-range", default="",
                        help="<base>..<head> for a CI run; required, because a missing range is not a "
                             "green range")
    parser.add_argument("--root", type=Path, default=None)
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per guard (default {DEFAULT_TIMEOUT}; the original had NO timeout "
                             f"on any guard invocation, and this runner is the longest thing in CI)")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    root = (args.root or Path(__file__).resolve().parent.parent).resolve()

    try:
        local_args = parse_local_args(args.local_arg)
        doc = read_registry(root)
        report = Report()
        report.stages.append("registry")
        catalog = doc["guards"]
        ci_range = resolve_ci_range(args.tier, args.ci_range, root)
        report.stages.append("range")
        guards = select(catalog, args.tier, args.only, args.skip, args.include_backlog)
        check_selection(guards, catalog, args.tier, args.only, args.include_backlog)
        check_scripts_exist(root, guards, catalog)
        report.stages.extend(("selection", "catalog"))
        absent = missing_interpreters([str(catalog[g]["script"]) for g in guards], args.only)
        report.missing_interpreters = absent
        if absent:
            report.environment_note = (f" (PATH lacks {', '.join(absent)}: a red guard that shells out "
                                       f"to one is an environment fault, not a verdict)")
        report.stages.append("interpreters")
        report = dispatch(root, guards, catalog, args.tier, ci_range, local_args, args.timeout)
        report.ci_range = ci_range
        verdict, verdict_exit = summarise(report)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": refusal.stage,
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        else:
            print(f"RUN-GUARDS REFUSED [{refusal.stage}]: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    # `verdict` is FAILED for a red run, and the exit code is the FIRST red guard's so it stays comparable
    # with the original. A red run that exited 0, or that reported "OK" with red rows, would be the one
    # failure this whole tool exists to prevent.
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK" if verdict_exit == EXIT_OK else "FAILED",
                          "summary": verdict, "exitCode": verdict_exit, **report.__dict__}, indent=2))
        return verdict_exit
    print(f"\nGuard runner (--tier {report.tier}):")
    print(f"  {'id':<44} {'tier':<7} {'status':<9} {'exit':>5} {'s':>6}")
    for row in report.results:
        print(f"  {row['id']:<44} {row['tier']:<7} {row['status']:<9} {row['exit']:>5} {row['seconds']:>6}")
        if row["exit"] != 0 and row["stderr"]:
            print(f"    --- {row['id']} stderr ---")
            for line in row["stderr"].splitlines():
                print(f"    {line}")
    if report.backlog:
        print("\nBACKLOG (not gating - a backlog guard never fails the run):")
        for row in report.results:
            if row["status"] == "backlog":
                print(f"  {row['id']:<44} {row['exit']:>5} {row['seconds']:>6}")
    if report.missing_interpreters:
        print(f"\nWARNING (environment, not a guard verdict): PATH has no "
              f"{', '.join(repr(m) for m in report.missing_interpreters)}.")
        print("A guard that shells out to a missing interpreter reports its OWN red, so a red row may be")
        print("phantom. Re-run from a shell with a complete PATH before reading this table as evidence.")
    print(f"\n{verdict}")
    return verdict_exit


if __name__ == "__main__":
    sys.exit(main())
