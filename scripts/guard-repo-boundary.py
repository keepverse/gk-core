#!/usr/bin/env python3
r"""Guard: the standalone-first assembly boundary (B1-B2) and the frozen planning paths (B3).
Replaces `guard-repo-boundary.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every finding and the verdict went out through `Write-Host`**, invisible to a `2>&1` capture.
  Findings now go to stderr, only the OK verdict to stdout, and `--json` carries the machine form.
* **B2 reported an ABSOLUTE path.** Every other finding in this guard - and every finding in the
  registry - is repository-relative, because that is the form the specs and CI logs are written in.
  One check embedding the machine's own temp path into its output is a portability defect, so B2's
  path is repo-relative here too. The differential masks paths and then asserts by EXISTENCE that the
  port's are real, which is a stronger claim than "they match".
* **A malformed csproj THREW**, killing the run with a stack trace and exit 1 - the same exit as a
  violation. The port names the refusal.

THE FAIL-OPEN HOLE THIS CLOSES, AND IT IS THE ONE THAT MATTERS MOST FOR CI
---------------------------------------------------------------------------
B3 is diff-based, and the original ran its git calls like this:

    $ErrorActionPreference = "Continue"
    return @(& git -C $Root @GitArgs 2>$null)

`2>$null` throws away the diagnosis, and an empty stdout is then read as "nothing changed". So a
`--base-ref` that does not resolve makes B3 find no changes and the guard reports **OK, exit 0**,
having checked nothing at all. Measured directly before writing this: with `-BaseRef no-such-ref-xyz`
the original printed its OK verdict and exited 0 while `git` was saying
`fatal: ambiguous argument 'no-such-ref-xyz': unknown revision`. A typo in CI's base reference
disables the whole check and reports green - the exact silent-pass class this program exists to
eliminate. The port treats a non-zero git exit as a named refusal (`GIT-FAILED`), so an unresolvable
ref is a refusal rather than a clean run.

The other original behaviours transcribed deliberately, not "fixed":

* **B2 scans RAW TEXT, with no comment stripping.** A `using UnityEngine;` inside a comment is
  flagged. That is over-matching, and it is the contract: B2 asks "does this file name a host
  namespace", and narrowing the scan would let a real reference hide behind a comment. The port does
  not add a stripper here, and says so rather than leaving the reader to assume one was added.
* **Matching is case-INSENSITIVE**, because the original used `-match` and `-notmatch`.
* **A `<Project>` carrying a default XML namespace is now read.** PowerShell's
  `SelectNodes("//ProjectReference")` does not match a namespaced element, so such a csproj would
  yield NO references and pass B1 vacuously. The port matches on the local tag name, which closes that
  hole. No shipped csproj carries a namespace, so this changes nothing today and prevents a silent
  pass if one ever does.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

GUARD_ID = "repo-boundary"
VERDICT_OK = ("REPO BOUNDARY GUARD OK — the five standalone assemblies keep their pinned graph, "
              "no host references, frozen planning paths untouched")
VERDICT_FAILED = "REPO BOUNDARY GUARD FAILED:"
EXIT_OK = 0
EXIT_FAILED = 1

# B1: the pinned dependency graph, measured 2026-09-18 (spec-repo-boundary.md). A new edge is an
# architecture change, and decisions.md comes first.
ALLOWED_GRAPH: dict[str, tuple[str, ...]] = {
    "FusionRpg.Contracts": (),
    "FusionRpg.Core": ("FusionRpg.Contracts",),
    "FusionRpg.CheatCore": ("FusionRpg.Contracts",),
    "FusionRpg.Data": ("FusionRpg.Contracts", "FusionRpg.Core", "FusionRpg.CheatCore"),
    "FusionRpg.Server": ("FusionRpg.Contracts", "FusionRpg.Core", "FusionRpg.CheatCore",
                         "FusionRpg.Data"),
}

# B2: a host mod-loader is a host mod-loader whatever it is called.
HOST_ASSEMBLY_PATTERNS = ("UnityEngine", "Il2Cpp", "MelonLoader", "BepInEx", "HarmonyLib", "0Harmony")

# B3: the perf stream's history, never a default or a fallback for a fresh /plan.
FROZEN_TASKS = ("tasks/plan.md", "tasks/todo.md")
FROZEN_ROOT_FILES = ("SPEC.md",)

GIT_TIMEOUT = 300
SOURCE_SUFFIX = ".cs"


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def _local(tag: str) -> str:
    """The tag without its `{namespace}` prefix. See the module docstring on namespaced csproj."""
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def _parse_csproj(path: Path) -> ET.Element:
    try:
        return ET.fromstring(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise Refusal("CSPROJ-MISSING", str(path)) from exc
    except ET.ParseError as exc:
        # The original's `[xml]` cast threw here and took the whole run down.
        raise Refusal("CSPROJ-NOT-XML", f"{path}: {exc}") from exc
    except OSError as exc:
        raise Refusal("CSPROJ-UNREADABLE", f"{path}: {exc}") from exc


def project_references(csproj: ET.Element) -> list[str]:
    """Every `ProjectReference`'s assembly name, from the file name of its Include."""
    out: list[str] = []
    for node in csproj.iter():
        if _local(node.tag) != "ProjectReference":
            continue
        include = node.get("Include")
        if not include:
            continue
        out.append(Path(include.replace("\\", "/")).stem)
    return out


def host_reference_hits(csproj: ET.Element) -> list[str]:
    """`Reference` / `PackageReference` entries naming a host assembly. FOLDS CASE, as `-match` did."""
    hits: list[str] = []
    for node in csproj.iter():
        tag = _local(node.tag)
        if tag not in ("Reference", "PackageReference"):
            continue
        include = node.get("Include") or ""
        lowered = include.lower()
        for pattern in HOST_ASSEMBLY_PATTERNS:
            if pattern.lower() in lowered:
                hits.append(f"{tag} Include='{include}'")
                break
    return hits


def check_b1_b2(root: Path) -> list[str]:
    findings: list[str] = []
    for project, allowed in ALLOWED_GRAPH.items():
        csproj_path = root / "src" / project / f"{project}.csproj"
        if not csproj_path.is_file():
            # A finding, not a refusal: the graph is a statement about the repository, and a project
            # that has gone missing is a change to it. The original did the same and `continue`d.
            findings.append(f"B1 {project}: csproj not found at {csproj_path}")
            continue
        csproj = _parse_csproj(csproj_path)

        for actual in project_references(csproj):
            if actual not in allowed:
                findings.append(
                    f"B1 {project}: references '{actual}', which is not in its allowed graph "
                    f"({', '.join(allowed)}) -- a new dependency edge is an architecture change, "
                    "decisions.md first")

        for hit in host_reference_hits(csproj):
            findings.append(
                f"B2 {project}: {hit} -- host assemblies must never be referenced by a "
                "standalone-game project")

        src_dir = root / "src" / project
        if not src_dir.is_dir():
            continue
        for path in sorted(src_dir.rglob(f"*{SOURCE_SUFFIX}")):
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError as exc:
                findings.append(f"B2 {path.as_posix()}: unreadable source file: {exc}")
                continue
            lowered = text.lower()
            for pattern in HOST_ASSEMBLY_PATTERNS:
                # RAW TEXT, no comment stripping - see the module docstring. A `using UnityEngine;`
                # in a comment is flagged, and that is the intended over-match.
                if f"using {pattern.lower()}" in lowered.replace("\t", " ").replace("\n", " ") \
                        or _using_regex(pattern).search(text):
                    findings.append(
                        f"B2 {path.relative_to(root).as_posix()}: 'using {pattern}' -- a "
                        "standalone-game .cs file must never reference a host namespace")
    return findings


def _using_regex(pattern: str):
    """`using\\s+<pattern>`, folded case - the original's `-match` with an escaped pattern."""
    import re
    return re.compile(r"using\s+" + re.escape(pattern), re.IGNORECASE)


def _git(root: Path, args: list[str]) -> list[str]:
    """Run git and return its stdout lines, or REFUSE.

    The original returned an empty list on failure and the caller read that as "nothing changed",
    which is how a bad base ref produced a clean run. See the module docstring.
    """
    try:
        proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                              timeout=GIT_TIMEOUT)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("GIT-TIMEOUT", f"git {' '.join(args)} did not finish within {GIT_TIMEOUT}s") from exc
    except OSError as exc:
        raise Refusal("GIT-UNAVAILABLE", f"cannot run git {' '.join(args)}: {exc}") from exc
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip().splitlines()
        raise Refusal("GIT-FAILED",
                      f"git {' '.join(args)} exited {proc.returncode}: "
                      f"{detail[0] if detail else 'no output'}")
    return [line for line in proc.stdout.splitlines() if line.strip()]


def changed_file_statuses(root: Path, base_ref: str, commit_range: str | None) -> list[tuple[str, str]]:
    """`(status, path)` for every file the diff touches, plus untracked files when not ranging.

    The `--diff-filter=ACMDR` and the R/C handling are transcribed: a rename is reported as a delete of
    the old path AND an add of the new one, because either half can be the frozen path. A copy
    contributes only the add, since copying cannot remove anything.
    """
    args = ["diff", "--name-status", "--diff-filter=ACMDR"]
    args.append(commit_range if commit_range else base_ref)
    results: list[tuple[str, str]] = []
    for line in _git(root, args):
        parts = line.split("\t")
        status = parts[0]
        if status.startswith("R") or status.startswith("C"):
            if len(parts) >= 3:
                if status.startswith("R"):
                    results.append(("D", parts[1].replace("\\", "/")))
                results.append(("A", parts[2].replace("\\", "/")))
            continue
        if len(parts) >= 2:
            results.append((status[0], parts[1].replace("\\", "/")))
    if not commit_range:
        for untracked in _git(root, ["ls-files", "--others", "--exclude-standard"]):
            results.append(("A", untracked.strip().replace("\\", "/")))
    return results


def check_b3(root: Path, base_ref: str, commit_range: str | None) -> tuple[list[str], str | None]:
    """`(findings, refusal_reason)`.

    A git failure becomes a FINDING plus a named reason, NOT a whole-run refusal. Found by the C#
    suite: its B1 and B2 fixtures build a plain directory with no git repository, and a whole-run
    refusal there suppressed the B1/B2 findings entirely - so five tests could not see the thing they
    exist to check. B1 and B2 are filesystem questions that work on any tree; only B3 needs git. So
    an unanswerable B3 is reported as a finding naming the refusal, the verdict is still FAIL, and the
    operator sees both the real violations and the reason the third check could not run.
    """
    findings: list[str] = []
    try:
        changes = changed_file_statuses(root, base_ref, commit_range)
    except Refusal as refusal:
        return ([f"B3 <no range resolved>: {refusal.reason} -- the frozen planning paths could not "
                 f"be checked: {refusal.detail}"], refusal.reason)
    for status, path in changes:
        if path in FROZEN_TASKS and status in ("M", "A"):
            findings.append(
                f"B3 {path}: frozen -- the perf stream's history, never a default or a fallback for "
                "a fresh /plan. Use tasks/<program>-plan.md / tasks/<program>-todo.md.")
        if path in FROZEN_ROOT_FILES and status == "A":
            findings.append(f"B3 {path}: never added at the repository root -- not a default for a "
                            "fresh spec.")
    return findings, None


def check(root: Path, *, base_ref: str = "HEAD",
          commit_range: str | None = None) -> dict:
    findings = check_b1_b2(root)
    # B3 is NOT gated on B1/B2 being clean. It is two `git` calls against a different question, and
    # gating it would let a broken graph hide a modified frozen path - two findings for one change is
    # what an operator wants.
    b3_findings, refusal = check_b3(root, base_ref, commit_range)
    findings += b3_findings
    by_check: dict[str, int] = {}
    for item in findings:
        key = item.split(" ", 1)[0]
        by_check[key] = by_check.get(key, 0) + 1
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "findings": findings,
        "findings_by_check": by_check,
        "base_ref": base_ref,
        "range": commit_range,
        # Present and null on a clean run, and the refusal reason when B3 could not be answered. The
        # verdict is already FAIL in that case, so this is the diagnosability, not the outcome.
        "refused": refusal,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the standalone-first assembly boundary (replaces "
                    "guard-repo-boundary.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--base-ref", default="HEAD",
                        help="diff against this ref (default: HEAD, the working tree)")
    parser.add_argument("--range", default=None,
                        help="an explicit commit range, e.g. a..b; skips the untracked-file pass")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = check(args.root.resolve(), base_ref=args.base_ref, commit_range=args.range)
    except Refusal as refusal:
        print(f"{VERDICT_FAILED} {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "findings": [], "findings_by_check": {},
                              "base_ref": args.base_ref, "range": args.range,
                              "refused": refusal.reason}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
