#!/usr/bin/env python3
"""
Census the couplings a `.ps1` tool must be repointed from, BEFORE deleting it.

Why this exists
---------------
A guard port has FIVE places that name the script, and finding four of them after the delete has
already broken unrelated tests is how this program spent a stretch at 22 red:

    1. the enforcement-registry row           gk-core/scripts/enforcement-registry.v1.json
    2. the verification-boundaries owner row  gk-core/scripts/verification-boundaries.v1.json
    3. a C# test that SHELLS the script        tests/**/*.cs   (executes it)
    4. a REPO-ROOT LANDMARK                    tests/**/*.cs   (a deleted file breaks every
                                                test that walks up looking for the repo root,
                                                in projects this session may not own)
    4b. prose citations                       docs/**, tests/**, scripts/**

(1) and (2) are the two the plan documented. (3), (4) and (4b) were discovered the hard way, and
(4) is the landmine: `scripts/guard-dal.ps1` was the landmark for ~45 test files across six
projects, so deleting it broke tests nobody had touched.

The trap inside the fix, which this tool exists to prevent repeating: a walk-up loop that uses
`Path.Combine(dir.FullName, "scripts", "guard-x.ps1")` may be EITHER a landmark probe (the path is
only passed to `File.Exists`; the function returns the DIRECTORY) or a script finder (the function
RETURNS the path, which is then executed). Rewriting a script finder to point at
`Directory.Build.props` turns it into `powershell -File <repo>/Directory.Build.props`. This tool
separates the two by what the enclosing loop RETURNS, so the distinction is made before the edit
rather than after 11 red tests.

Usage:
    python gk-core/scripts/ps1-port-census.py --tool guard-stat-pairs
    python gk-core/scripts/ps1-port-census.py --tool guard-dal --json
    python gk-core/scripts/ps1-port-census.py --tool guard-dal --all

Exit 0 always: this is a READER. It reports what must be repointed; it does not gate.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


def _grep(pattern: str, *pathspecs: str) -> list[tuple[str, int, str]]:
    """git grep -n -E, as (path, line, text). Empty on no match."""
    proc = subprocess.run(["git", "grep", "-n", "-E", pattern, "--", *pathspecs],
                          capture_output=True, text=True, cwd=REPO_ROOT)
    hits = []
    for raw in proc.stdout.splitlines():
        if not raw.strip():
            continue
        path, _, rest = raw.partition(":")
        line_no, _, text = rest.partition(":")
        try:
            hits.append((path, int(line_no), text))
        except ValueError:
            hits.append((path, 0, text))
    return hits


# A walk-up probe: the combined path is passed to File.Exists.
_INLINE_PROBE = re.compile(
    r'File\.Exists\(\s*Path\.Combine\(\s*\w+(?:\.FullName)?\s*,\s*"scripts",\s*"(?P<stem>[\w.-]+)"')
# The two-step form: the path is built into a local, then probed.
_LOCAL_PROBE = re.compile(
    r'var\s+(?P<var>\w+)\s*=\s*Path\.Combine\(\s*(?P<base>\w+(?:\.FullName)?)\s*,\s*"scripts",\s*"(?P<stem>[\w.-]+)"')


def classify_landmarks(stem: str) -> dict:
    """Split landmark-shaped sites into MARKER (safe to repoint) and SCRIPT (must not be).

    The discriminator is what the enclosing walk-up loop RETURNS. A repo-root finder returns the
    directory; a script finder returns the path, and that path is then executed.
    """
    marker: list[dict] = []
    script: list[dict] = []
    seen: set[tuple[str, int]] = set()

    # Only a `dir` / `dir.FullName` base is a walk-up landmark. `root` / `repoRoot` / `fixture` are
    # the execute role - the guard being run against a fixture - and coupling 3 reports those.
    for path, line_no, text in _grep(rf'Path\.Combine\(\s*dir(\.FullName)?\s*,\s*"scripts",\s*"{re.escape(stem)}\.ps1"',
                                     "tests/"):
        lines = (REPO_ROOT / path).read_text(encoding="utf-8", errors="replace").splitlines()
        window = "\n".join(lines[max(0, line_no - 5):line_no + 6])
        local = _LOCAL_PROBE.search(text)
        inline = _INLINE_PROBE.search(text)
        var = local.group("var") if local else None
        if var and re.search(rf"\breturn\s+{var}\s*;", window):
            kind, script_role = "SCRIPT", True
        elif inline or local:
            kind, script_role = "MARKER", False
        else:
            kind, script_role = "UNKNOWN", False
        entry = {"file": path, "line": line_no, "kind": kind}
        if (path, line_no) not in seen:
            seen.add((path, line_no))
            (script if script_role else marker).append(entry)

    return {"marker": marker, "script": script}


SHELLS_POWERSHELL = re.compile(r'FileName\s*=\s*"(?:powershell|pwsh)"')


def classify_tests(stem: str) -> dict:
    """Split the test sites into EXECUTE and PROSE.

    Coupling 3 cannot be found by grepping for an invocation on one line, and that is not a
    hypothetical: `FunnelDeltaGuardTests.cs` builds the script path at :23 and sets
    `FileName = "powershell"` at :152, 129 lines apart, so a same-line search for
    `powershell.*funnel-delta` returns NOTHING while the class does execute the guard four times.

    The reliable signal is per FILE: a test file that names the tool AND spawns a shell is
    executing it somewhere; a file that only names it is citing it. Prose citations need the
    rename sweep; executing files need the interpreter repointed AND their stdout/stderr
    assertions moved - different work, so conflating them is how one gets missed.
    """
    execute: list[dict] = []
    prose: list[dict] = []
    for path, line_no, _text in _grep(re.escape(stem), "tests/"):
        body = (REPO_ROOT / path).read_text(encoding="utf-8", errors="replace")
        entry = {"file": path, "line": line_no}
        if SHELLS_POWERSHELL.search(body):
            execute.append(entry)
        else:
            prose.append(entry)
    return {"execute": execute, "prose": prose}


def census(stem: str) -> dict:
    ps1 = f"{stem}.ps1"
    registry = _grep(rf'"script":\s*"scripts/{re.escape(ps1)}"', "scripts/enforcement-registry.v1.json")
    boundary = _grep(re.escape(ps1), "scripts/verification-boundaries.v1.json")
    tests = classify_tests(stem)
    ci = _grep(re.escape(ps1), ".github/")
    prose_docs = _grep(re.escape(ps1), "docs/")
    landmarks = classify_landmarks(stem)
    return {
        "tool": stem,
        "source": f"scripts/{ps1}",
        "source_exists": (REPO_ROOT / "scripts" / ps1).is_file(),
        "port_exists": (REPO_ROOT / "scripts" / f"{stem}.py").is_file(),
        "couplings": {
            "1_registry_row": [{"file": f, "line": n} for f, n, _ in registry],
            "2_boundary_owner_row": [{"file": f, "line": n} for f, n, _ in boundary],
            "3_tests_naming_it": tests,
            "4_landmark_sites": landmarks,
            "5_ci_or_dispatcher": [{"file": f, "line": n} for f, n, _ in ci],
        },
        "prose_citations_in_docs": len(prose_docs),
    }


def render(result: dict) -> str:
    c = result["couplings"]
    lines = [f"{result['tool']}  ({result['source']})"]
    if not result["source_exists"]:
        lines.append("  source is ALREADY GONE - this is a repair, not a port")
    if result["port_exists"] and result["source_exists"]:
        lines.append("  WARNING: both .ps1 and .py exist; decide which is the implementation")
    lines.append(f"  1 registry row            {len(c['1_registry_row'])}")
    lines.append(f"  2 boundary owner row      {len(c['2_boundary_owner_row'])}"
                 + ("   <-- none: a missing mapping is a defect to fix" if not c["2_boundary_owner_row"] else ""))
    named = c["3_tests_naming_it"]
    lines.append(f"  3 tests naming it         {len(named['execute'])} EXECUTE"
                 f" / {len(named['prose'])} prose citation(s)")
    for entry in named["execute"]:
        lines.append(f"      EXECUTE  {entry['file']}:{entry['line']}")
    lm = c["4_landmark_sites"]
    lines.append(f"  4 landmark-shaped sites   {len(lm['marker'])} MARKER (safe to repoint)"
                 f" / {len(lm['script'])} SCRIPT (returns the guard - do NOT repoint)")
    for entry in lm["script"]:
        lines.append(f"      SCRIPT  {entry['file']}:{entry['line']}")
    lines.append(f"  5 CI / dispatcher         {len(c['5_ci_or_dispatcher'])}")
    lines.append(f"  prose citations in docs/  {result['prose_citations_in_docs']}")
    lines.append("")
    lines.extend(render_actions(result))
    return "\n".join(lines)


def render_actions(result: dict) -> list[str]:
    """What to DO, derived from what the census found.

    The contract itself lives in docs/architecture/ps1-port-checklist.md; this prints the parts
    that are conditional on THIS tool's census, so the reader does not have to remember which
    steps apply. It is here rather than in the doc because a procedure that lives where it is used
    gets followed, and a procedure that lives in a document gets skimmed - which is how four
    consecutive ports each rediscovered the stdout/stderr split at the cost of a red run.
    """
    c = result["couplings"]
    out = ["  DO (see docs/architecture/ps1-port-checklist.md):"]
    if not c["2_boundary_owner_row"]:
        out.append("    - ADD a boundary owner row (coupling 2 is 0) carrying the .py AND its test file")
    if c["3_tests_naming_it"]["execute"]:
        out.append("    - repoint the test that SHELLS it: python <script> --root <root>")
        out.append("    - move EVERY finding assertion to STDERR; keep the OK assertion on stdout")
    if c["4_landmark_sites"]["marker"]:
        out.append(f"    - repoint {len(c['4_landmark_sites']['marker'])} MARKER site(s) to Directory.Build.props")
    if c["4_landmark_sites"]["script"]:
        out.append("    - SCRIPT site(s) above: point them at the .py once ported, NOT at a landmark")
    if c["5_ci_or_dispatcher"]:
        out.append("    - a CI/dispatcher site names it; make that caller interpreter-aware first")
    if result["prose_citations_in_docs"]:
        out.append(f"    - {result['prose_citations_in_docs']} prose citation(s) in docs/; sweep, never hand-edit")
    out.append("    - differential-test against the .ps1 BEFORE deleting it (a fixture per rule)")
    out.append("    - falsify the new test suite: break the implementation, watch a test go red")
    out.append("    - after deleting: scripts/ps1-rename-sweep.py --map "
               + result["tool"] + " --apply, then audit-doc-citations.py --strict to 0 HIGH")
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Census the couplings a .ps1 tool must be repointed from, before deleting it.")
    parser.add_argument("--tool", dest="tools", action="append", default=[], metavar="STEM",
                        help="a tool stem, e.g. guard-dal (repeatable)")
    parser.add_argument("--all", action="store_true",
                        help="census every remaining registry .ps1 guard")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    stems = list(args.tools)
    if args.all:
        reg = json.loads((REPO_ROOT / "scripts" / "enforcement-registry.v1.json")
                         .read_text(encoding="utf-8"))
        for row in reg["guards"].values():
            script = row.get("script") or ""
            if script.endswith(".ps1") and (REPO_ROOT / script).is_file():
                stems.append(Path(script).stem)

    if not stems:
        parser.error("pass --tool STEM, or --all")

    results = [census(stem) for stem in dict.fromkeys(stems)]
    if args.json:
        print(json.dumps(results if len(results) > 1 else results[0], indent=2))
    else:
        for index, result in enumerate(results):
            if index:
                print()
            print(render(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
