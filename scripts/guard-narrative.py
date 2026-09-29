#!/usr/bin/env python3
r"""Guard: the npc-story-events program's one catalogued guard (`narrative`). Replaces
`guard-narrative.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every finding and the verdict went out through `Write-Host`**, which writes the INFORMATION
  stream. A caller that captured with `2>&1` — the idiom this repo used for years — got *nothing*
  from a correct script, and that single trap cost four wrong diagnoses in one file. Findings now go
  to stderr and only the OK verdict to stdout, so a capture that merges both still sees the verdict
  and a `--json` caller sees the machine form.
* **A missing registry THREW.** `throw "enforcement registry missing: $mapPath"` under
  `ErrorActionPreference = 'Stop'` produced a PowerShell stack trace and exit 1 — indistinguishable
  from the nineteen ways the guard is *meant* to fail. The port names the refusal instead.
* **Check 3 shelled `dotnet test` with no timeout at all.** The original would wait forever on a
  wedged test host, and a hang reads as "still running" rather than as a failure. The port takes a
  hard `--dotnet-timeout` and treats expiry as a named refusal, because a guard that can hang is not
  a gate.

THE VACUOUS PASS THIS EXISTS TO REFUSE
--------------------------------------
One guard covers every `narrative` invariant, so the failure it exists to prevent is a pass that
checks nothing: `--filter "Guard=narrative"` matching no test exits 0, and a row whose test forgot
`[Trait("Guard", "narrative")]` — or a row landed with no test at all — is green while enforcing
nothing. Three checks, in order:

  1. Every invariant whose `guards` carries `narrative` has a line in the committed row -> test-class
     map below, and every map line names such a row. The task that lands a row appends its line in
     the same commit as the test that makes the row true.
  2. Each mapped test class exists and carries `[Trait("Guard", "narrative")]` — the static half, so a
     stripped trait is caught without starting a test host.
  3. With `--run-trait-filter` (CI passes it through the registry's `args.ci`), the filter really
     selects at least one test in every mapped project. The runtime half: a class whose trait never
     reaches the runner is caught here.

CASE SENSITIVITY IS PER-CHECK HERE, AND THAT IS NOT A TYPO
-----------------------------------------------------------
The original mixes two matching conventions in one script:

* The trait check used `-notmatch`, which **folds case**. A class carrying `[TRAIT("GUARD",
  "NARRATIVE")]` PASSED, so the port uses `re.IGNORECASE` there. Getting this backwards would reject
  a legitimate class.
* The `Total:` / `Failed:` scrapes used `[regex]::Matches`, which is case-**sensitive**. The port
  keeps them case-sensitive.

The same trap bit `guard-actor-hub` (PowerShell `-match`, folds) and `guard-funnel-delta`
(`[regex]::IsMatch`, does not) in this program. The rule is per call site, not per repo.

CHECK 3 IS GATED ON THE EARLIER CHECKS BEING CLEAN, and that is deliberate
-------------------------------------------------------------------------
The original runs the expensive `dotnet test` sweep only when `$failures.Count -eq 0`. A map that
does not correspond to the registry is reported in full and the test host is never started. The port
keeps the gate, because a guard that starts a multi-minute test run in order to report a typo in its
own map is a guard nobody runs.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

GUARD_ID = "narrative"
VERDICT_OK = "NARRATIVE GUARD OK - {rows} row(s) guarded by 'narrative', {mapped} mapped"
VERDICT_FAILED = "NARRATIVE GUARD FAILED"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_REGISTRY = "scripts/enforcement-registry.v1.json"
NARRATIVE_GUARD = "narrative"

# The committed row -> test-class map. A literal, not a template: these are the rows a task appends
# to in the same commit as the test that makes the row true, and the whole point of check 1 is that
# this text is the thing under audit. Format is `<row id> | <test csproj> | <test class file>`.
ROW_MAP = """\
guard-narrative-row-map | tests/FusionRpg.Guard.Tests/FusionRpg.Guard.Tests.csproj | tests/FusionRpg.Guard.Tests/NarrativeGuardContractTests.cs
ns-one-content-theta-producer | tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj | tests/FusionRpg.Core.Tests/Narrative/Hosts/HostContentThetaTests.cs
narrative-tuning-no-default | tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj | tests/FusionRpg.Core.Tests/Narrative/Vocabulary/NarrativeTuningTests.cs
ns-one-narrative-text-dto | tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj | tests/FusionRpg.Core.Tests/Narrative/Text/NarrativeTextTests.cs
ns6-doctrine-no-magnitude | tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj | tests/FusionRpg.Core.Tests/Narrative/Doctrine/DoctrineCatalogTests.cs
ns6-reading-aggregate-only | tests/FusionRpg.Guard.Tests/FusionRpg.Guard.Tests.csproj | tests/FusionRpg.Guard.Tests/NarrativeDoctrineReadingGuardTests.cs
"""

# FOLDS CASE, because the original used PowerShell `-notmatch`. See the module docstring.
TRAIT_PATTERN = re.compile(r'\[Trait\(\s*"Guard"\s*,\s*"narrative"\s*\)\]', re.IGNORECASE)
# CASE-SENSITIVE, because the original used `[regex]::Matches`. See the module docstring.
TOTAL_PATTERN = re.compile(r"Total:\s*(\d+)")
FAILED_PATTERN = re.compile(r"Failed:\s*(\d+)")

# The original had no timeout at all. A test host that wedges would hang the guard forever, and a
# hang is not a failure anyone can act on.
DEFAULT_DOTNET_TIMEOUT = 1800


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def parse_row_map(text: str) -> list[dict[str, str]]:
    """The row map as records. A malformed line is a HARD failure, not a collected finding.

    Kept as a hard failure because a map line this function cannot read is a defect in the guard's
    own source, and the checks below would otherwise silently cover fewer rows than the text claims
    — a guard that audits a truncated map while reporting the full count.
    """
    rows: list[dict[str, str]] = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = [p.strip() for p in line.split("|")]
        if len(parts) != 3 or any(not p for p in parts):
            raise Refusal(
                "ROW-MAP-MALFORMED",
                f"row-map line is not '<row id> | <test csproj> | <test class file>': {line}")
        rows.append({"row": parts[0], "project": parts[1], "file": parts[2]})
    return rows


def load_registry(path: Path) -> dict:
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError as exc:
        # The original THREW here. Under `ErrorActionPreference = 'Stop'` that is a stack trace and
        # exit 1 — the same exit as a real violation, so a broken checkout read as nineteen failures.
        raise Refusal("MISSING-REGISTRY", str(path)) from exc
    except OSError as exc:
        raise Refusal("UNREADABLE-REGISTRY", f"{path}: {exc}") from exc
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise Refusal("REGISTRY-NOT-JSON", f"{path}: {exc}") from exc
    if not isinstance(data, dict) or not isinstance(data.get("invariants"), list):
        raise Refusal("REGISTRY-SHAPE-UNEXPECTED", f"{path}: no 'invariants' array")
    return data


def narrative_row_ids(registry: dict) -> list[str]:
    """Invariant ids whose `guards` carries `narrative`."""
    out: list[str] = []
    for invariant in registry["invariants"]:
        if not isinstance(invariant, dict):
            continue
        guards = invariant.get("guards")
        if isinstance(guards, list) and NARRATIVE_GUARD in guards:
            out.append(str(invariant.get("id")))
    return out


def check_correspondence(rows: list[dict[str, str]], narrative_rows: list[str]) -> list[str]:
    """Check 1. The map and the registry must describe the same set of rows, once each."""
    failures: list[str] = []
    if not narrative_rows:
        # R8 refuses a catalogued guard no invariant names, and an empty row set makes every check
        # below vacuous — which is the exact failure this whole guard exists to catch.
        failures.append(
            f"no invariant names the '{NARRATIVE_GUARD}' guard: R8 refuses a catalogued guard no "
            "invariant names, and an empty row set makes every check below vacuous")

    mapped = [r["row"] for r in rows]
    for row_id in narrative_rows:
        if row_id not in mapped:
            failures.append(
                f"row '{row_id}' is guarded by '{NARRATIVE_GUARD}' but has no line in the committed "
                "row -> test-class map (plan 4 D3)")
    for entry in rows:
        if entry["row"] not in narrative_rows:
            failures.append(
                f"map entry '{entry['row']}' names no invariant guarded by '{NARRATIVE_GUARD}'")

    seen: set[str] = set()
    for row_id in mapped:
        if row_id in seen:
            failures.append(f"map carries more than one line for row '{row_id}'")
        seen.add(row_id)
    return failures


def check_traits(root: Path, rows: list[dict[str, str]]) -> list[str]:
    """Check 2. The mapped class exists and carries the trait. No test host needed."""
    failures: list[str] = []
    for entry in rows:
        class_path = root / entry["file"]
        if not class_path.is_file():
            failures.append(
                f"mapped test class missing for row '{entry['row']}': {entry['file']}")
            continue
        try:
            text = class_path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            failures.append(
                f"mapped test class unreadable for row '{entry['row']}': {entry['file']}: {exc}")
            continue
        if not TRAIT_PATTERN.search(text):
            failures.append(
                f"mapped test class for row '{entry['row']}' does not carry "
                f'[Trait("Guard", "narrative")]: {entry["file"]}')
    return failures


def _last_int(text: str, pattern: re.Pattern[str]) -> int:
    """The LAST match, because the test host prints one summary per project and a build failure can
    print an earlier one."""
    matches = pattern.findall(text)
    return int(matches[-1]) if matches else 0


def run_trait_filter(root: Path, rows: list[dict[str, str]],
                     timeout: int) -> tuple[list[str], list[dict]]:
    """Check 3. The filter must really select tests in every mapped project.

    The verdict is the exit code and the scraped counts, never a stderr line — the original said so
    and was right, because a native child's stderr write is a terminating error under PowerShell's
    `ErrorActionPreference = 'Stop'`.
    """
    failures: list[str] = []
    observed: list[dict] = []
    for project in sorted({r["project"] for r in rows}):
        csproj = root / project
        if not csproj.is_file():
            failures.append(f"mapped test project missing: {project}")
            continue
        command = ["dotnet", "test", str(csproj), "-c", "Release", "--nologo",
                   "--verbosity", "minimal", "--filter", f"Guard={NARRATIVE_GUARD}"]
        try:
            proc = subprocess.run(command, cwd=str(root), capture_output=True, text=True,
                                  timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            # A NAMED refusal, not a hang. The original had no timeout and would wait forever; a
            # guard that can hang is not a gate, and "still running" is not a verdict.
            raise Refusal(
                "TRAIT-FILTER-TIMEOUT",
                f"dotnet test on {project} did not finish within {timeout}s") from exc
        except OSError as exc:
            raise Refusal("DOTNET-UNAVAILABLE", f"cannot run dotnet test on {project}: {exc}") from exc

        text = (proc.stdout or "") + (proc.stderr or "")
        total = _last_int(text, TOTAL_PATTERN)
        failed = _last_int(text, FAILED_PATTERN)
        observed.append({"project": project, "selected": total, "failed": failed,
                         "exit": proc.returncode})
        # Printed as the original printed it: an operator reading a CI log needs the count, and it
        # is the only place the number appears at all.
        print(f"  {project} -> Guard={NARRATIVE_GUARD} selected {total} test(s), {failed} failed")
        if total < 1:
            failures.append(
                f"the 'Guard={NARRATIVE_GUARD}' filter selected 0 tests in {project} (a row whose "
                "trait never reaches the runner enforces nothing)")
        if failed > 0 or proc.returncode != 0:
            failures.append(
                f"the 'Guard={NARRATIVE_GUARD}' filter run failed in {project} "
                f"(exit {proc.returncode})")
    return failures, observed


def check(root: Path, registry_path: Path, *, run_filter: bool = False,
          timeout: int = DEFAULT_DOTNET_TIMEOUT) -> dict:
    rows = parse_row_map(ROW_MAP)
    registry = load_registry(registry_path)
    narrative_rows = narrative_row_ids(registry)

    failures = check_correspondence(rows, narrative_rows)
    failures += check_traits(root, rows)

    # GATED, as the original gated it: a map that does not correspond to the registry is reported in
    # full and no test host is started.
    observed: list[dict] = []
    trait_filter_run = False
    if run_filter and not failures:
        more, observed = run_trait_filter(root, rows, timeout)
        failures += more
        trait_filter_run = True
    elif not run_filter:
        # STDERR, and this is a fix rather than a preference. The note went to stdout first, which
        # meant a `--json` caller received this line ahead of the envelope and `json.loads` failed on
        # it - the process said everything and the machine reader got nothing, which is the same
        # silent-failure shape the port standard exists to remove. stdout now carries the verdict and
        # the envelope, and nothing else; diagnostics go to stderr in both modes. The envelope's
        # `trait_filter_run: false` says the same thing to a machine, so nothing is lost.
        print("  trait filter NOT run (no --run-trait-filter); CI passes it through the "
              "registry's args.ci", file=sys.stderr)

    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if failures else "OK",
        "narrative_rows": len(narrative_rows),
        "mapped_rows": len(rows),
        "trait_filter_run": trait_filter_run,
        "trait_filter": observed,
        "findings": failures,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the npc-story-events catalogued guard (replaces guard-narrative.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--registry-path", type=Path, default=None,
                        help=f"the registry (default: <repo>/{DEFAULT_REGISTRY})")
    parser.add_argument("--run-trait-filter", action="store_true",
                        help="also prove the trait filter selects tests in every mapped project")
    parser.add_argument("--dotnet-timeout", type=int, default=DEFAULT_DOTNET_TIMEOUT,
                        help=f"hard timeout for each `dotnet test` (default: {DEFAULT_DOTNET_TIMEOUT}s)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    registry_path = args.registry_path or root / DEFAULT_REGISTRY
    if args.dotnet_timeout <= 0:
        print("NARRATIVE GUARD FAILED: BAD-TIMEOUT --dotnet-timeout must be positive",
              file=sys.stderr)
        return EXIT_FAILED

    try:
        result = check(root, registry_path, run_filter=args.run_trait_filter,
                       timeout=args.dotnet_timeout)
    except Refusal as refusal:
        print(f"{VERDICT_FAILED}: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "narrative_rows": 0, "mapped_rows": 0,
                              "trait_filter_run": False, "trait_filter": [], "findings": []},
                             indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK.format(rows=result["narrative_rows"], mapped=result["mapped_rows"]))
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  - {finding}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
