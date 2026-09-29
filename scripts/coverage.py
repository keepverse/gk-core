#!/usr/bin/env python3
"""Line and branch coverage for one namespace, as a table you can read.

Replaces `coverage.ps1`. Runs a test project under coverlet and reports per-class coverage for classes
whose full name starts with `--namespace`, worst-first, because the only rows worth reading are the low
ones.

Coverage is a FLOOR, not a score. A line can be covered by a test that asserts nothing, which is why
this ships alongside `mutate.py` -- coverage says what the tests touched, mutation says what they would
notice. Chase mutation survivors; use coverage to find the code no test reaches at all, which is the
cheaper of the two problems to find.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **A FAILURE THAT WAS NOT `Failed!` PRINTED NOTHING AT ALL.** The original piped the test run through
  `Where-Object { $_ -match "^(Passed!|Failed!)" }`, so the only output an operator ever saw was those
  two lines. A run that died before the summary -- an MSBuild switch error, a restore failure, a
  solution-level fault -- produced an empty stream, and the throw that followed said only "tests failed -
  coverage of a red suite means nothing" with no indication what had happened. This is not hypothetical:
  a mistyped `dotnet test` switch produces exactly that shape, and the exit code alone cannot say which.
  The port captures the whole stream, reports the tail on refusal, and names the stage.
* **THERE WAS NO TIMEOUT.** `dotnet test` that wedges ran forever, and a coverage tool is exactly the
  kind of long run nobody is watching. `--timeout` bounds it and the refusal names the stage.
* **`-notlike "$Namespace*"` TREATED THE NAMESPACE AS A WILDCARD.** The sibling expression on the next
  line correctly escaped the namespace for `-replace`, so the two halves of the same concept were
  escaped differently -- and `-Namespace 'FusionRpg.Core.*'` would have matched every class in the
  project and reported a total as if it were one namespace's. The intent, stated in the original's own
  SYNOPSIS, is "full name starts with the namespace", so the port compares prefixes. DECLARED DIVERGENCE.
* **`exit 1` INSIDE A `try/finally` IS NOT A REFUSAL.** A caller cannot tell a threshold breach from a
  crash by exit code, and the threshold message went to stdout where a pipe could drop it. The port
  returns from one place and separates the reading from the verdict.
* **NO MACHINE-READABLE OUTPUT.** `--json` gives the same rows a caller can assert on, so the numbers do
  not have to be scraped out of a `Format-Table` rendering.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
from dataclasses import asdict, dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from core_test_project import (  # noqa: E402  (the lib shim above must run first)
    manifest_matched,
    namespace_token,
    project_for_token,
)

TOOL_ID = "coverage"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_NAMESPACE = "FusionRpg.Core.World"
DEFAULT_TIMEOUT_SECONDS = 3600
# The filter clause that keeps instrumentation-hostile tests out. Timing assertions cannot survive
# coverlet: it rewrites every sequence point, so a test asserting nanoseconds-per-atom fails under
# coverage and passes without it. Excluded by default rather than left to look like a regression.
BENCH_CLAUSE = "FullyQualifiedName!~Bench"
COVERAGE_FILE_NAME = "coverage.cobertura.xml"


class Refusal(Exception):
    """A named precondition or stage failure. The run says which, and never exits 0 having not run."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Row:
    """One class's coverage. `branch` is None when the class had nothing to branch on."""

    cls: str
    line: int
    branch: int | None
    lines: int


def resolve_project(repo: Path, project: str | None, namespace: str) -> tuple[str, bool]:
    """The test project to run, and whether the manifest decided it.

    An explicit `--project` is the caller overriding the manifest, so the second element is False by
    definition -- not "unknown". The distinction matters because the summary line reports the project,
    and a report that says which project it measured is worth more than one that does not.
    """
    if project:
        return project, False
    token = namespace_token(namespace)
    return project_for_token(repo, token), manifest_matched(repo, token)


def test_clauses(filter_text: str | None, include_timing_tests: bool) -> str | None:
    """The VSTest filter for the run, or None to run everything.

    Running unfiltered is the HONEST number and the default: a filtered run credits only what those
    tests reach, and the resulting percentage describes the filter, not the namespace.
    """
    clauses: list[str] = []
    if filter_text:
        clauses.append(filter_text)
    if not include_timing_tests:
        clauses.append(BENCH_CLAUSE)
    return "&".join(clauses) if clauses else None


def run_tests(project: str, clauses: str | None, repo: Path, timeout: int) -> tuple[int, str]:
    """Run the tests, and return the exit code plus the WHOLE output.

    The whole output, not the two summary lines: a run that never reached a summary is the case the
    original rendered as silence, and rendering it as silence is what this port exists to stop.
    """
    argv = ["dotnet", "test", project, "--collect:XPlat Code Coverage", "--nologo", "-v", "q"]
    if clauses:
        argv += ["--filter", clauses]
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, cwd=str(repo))
    except subprocess.TimeoutExpired as exc:
        raise Refusal("TESTS-TIMEOUT",
                      f"dotnet test {project} did not exit within {timeout}s "
                      f"(filter: {clauses or 'none'})") from exc
    except OSError as exc:
        raise Refusal("TESTS-UNRUNNABLE", f"dotnet test {project}: {exc}") from exc
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def newest_coverage_report(results_dir: Path) -> Path:
    """The most recently written cobertura file under the results directory.

    Newest-WINS, as the original's `Sort-Object LastWriteTime -Descending | Select -First 1` did. The
    results directory is emptied first, so in practice there is exactly one candidate; the ordering is
    kept so a run that leaves a stale file behind still reads its own report rather than refusing.
    """
    if not results_dir.is_dir():
        raise Refusal("NO-RESULTS-DIR",
                      f"dotnet test reported no results directory at {results_dir}")
    candidates = [p for p in results_dir.rglob(COVERAGE_FILE_NAME) if p.is_file()]
    if not candidates:
        raise Refusal("NO-COVERAGE-REPORT",
                      f"no {COVERAGE_FILE_NAME} under {results_dir}; the collector may not have run "
                      f"for this project")
    return max(candidates, key=lambda p: (p.stat().st_mtime, str(p)))


def read_classes(report: Path) -> list[tuple[str, float, float, int, int]]:
    """`(full name, line-rate, branch-rate, line count, branch-line count)` for every class in the report.

    The report is a COVERLET product with a fixed shape, but a partial or hand-written file is read as
    far as it goes rather than refused: the caller then reports what it found, and the namespace check
    downstream decides whether that is enough. Refusing a file whose root is merely unfamiliar would
    turn a renamed attribute into "no coverage report produced", which names the wrong fault.
    """
    try:
        root = ElementTree.parse(report).getroot()
    except (ElementTree.ParseError, OSError) as exc:
        raise Refusal("COVERAGE-REPORT-UNREADABLE", f"{report.name}: {exc}") from exc

    classes: list[tuple[str, float, float, int, int]] = []
    for node in root.iter("class"):
        name = node.get("name") or ""
        try:
            line_rate = float(node.get("line-rate") or 0.0)
            branch_rate = float(node.get("branch-rate") or 0.0)
        except ValueError:
            continue
        lines = node.findall("./lines/line")
        # A line element carrying branch="True" is a BRANCH SITE. Counting these rather than reading
        # branch-rate is what makes "no branches" distinguishable from "0% of branches covered": a class
        # with no branch sites has no branch-rate to report, and showing 0% for it reads as a failure
        # rather than as "there was nothing to branch on".
        branch_lines = sum(1 for line in lines if (line.get("branch") or "").lower() == "true")
        classes.append((name, line_rate, branch_rate, len(lines), branch_lines))
    return classes


def rows_for_namespace(classes, namespace: str) -> list[Row]:
    """Per-class rows for classes whose full name starts with `namespace`, worst-first.

    PREFIX comparison, not a wildcard match -- see the module docstring for why. The namespace and an
    optional trailing dot are stripped from the displayed name so the table shows what is left of it.
    """
    rows: list[Row] = []
    for name, line_rate, branch_rate, line_count, branch_lines in classes:
        if not name.startswith(namespace):
            continue
        short = name[len(namespace):]
        if short.startswith("."):
            short = short[1:]
        rows.append(Row(cls=short,
                        line=round(line_rate * 100),
                        branch=round(branch_rate * 100) if branch_lines > 0 else None,
                        lines=line_count))
    return rows


def summarise(rows: list[Row], namespace: str) -> tuple[int, int, int]:
    """(total lines, covered lines, class count) for the summary line.

    Covered lines are recovered as `lines * line% / 100` because that is all a per-class INTEGER
    percentage carries. The figure is therefore approximate by construction, and it is only ever
    reported as a rounded percentage -- quoting a decimal places would be inventing precision the
    report does not contain.
    """
    total = sum(row.lines for row in rows)
    covered = sum(row.lines * row.line / 100 for row in rows)
    return total, int(round(covered)), len(rows)


def render_table(rows: list[Row], project: str, namespace: str, resolved: bool) -> str:
    """The report, as text. Widths are fixed rather than console-derived so the output is diffable."""
    def cell(value, width: int, right: bool = False) -> str:
        text = "n/a" if value is None else str(value)
        return text.rjust(width) if right else text.ljust(width)

    lines = [f"coverage for {namespace} in {project} "
             f"({'manifest' if resolved else 'explicit --project'})",
             f"{'Class'.ljust(46)} {'Line%'.rjust(6)} {'Branch%'.rjust(8)} {'Lines'.rjust(6)}",
             f"{'-' * 46} {'-' * 6} {'-' * 8} {'-' * 6}"]
    for row in rows:
        lines.append(f"{row.cls.ljust(46)} {cell(row.line, 6, True)} "
                     f"{cell(row.branch, 8, True)} {cell(row.lines, 6, True)}")
    total, covered, count = summarise(rows, namespace)
    percent = round(covered / total * 100) if total else 0
    lines.append(f"{namespace}: {percent}% of {total} lines across {count} classes")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Line and branch coverage for one namespace (replaces coverage.ps1).")
    parser.add_argument("--project", default=None,
                        help="the test project to run; default resolves it from --namespace via "
                             "tests/core-test-projects.v1.json")
    parser.add_argument("--namespace", default=DEFAULT_NAMESPACE)
    parser.add_argument("--filter", default=None,
                        help="an additional VSTest filter; omitted means the honest whole-project number")
    parser.add_argument("--include-timing-tests", action="store_true",
                        help="do not exclude *Bench* tests, which cannot survive instrumentation")
    parser.add_argument("--threshold", type=int, default=0,
                        help="exit non-zero when any class falls below this line coverage; 0 disables")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"seconds for the test run (default {DEFAULT_TIMEOUT_SECONDS}; the "
                             f"original had NO timeout and could hang forever)")
    parser.add_argument("--root", type=Path, default=None, help="the repository")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    args = parser.parse_args(argv)

    repo = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        project, resolved = resolve_project(repo, args.project, args.namespace)
        results_dir = repo / project / "TestResults"
        # Emptied before the run so a stale report from a previous namespace cannot be read as this
        # one's. Guarded rather than blind, because this is a RECURSIVE DELETE of a path assembled from
        # a value the caller or the manifest supplied. The original's `Remove-Item -Recurse -Force` had
        # no check at all.
        #
        # THE GUARD IS CONTAINMENT, NOT THE DIRECTORY NAME. The first version tested
        # `results_dir.name != "TestResults"`, which can never be true: the path is built as
        # `repo / project / "TestResults"`, so its final segment is that literal by construction. A guard
        # that cannot fire is worse than no guard, because it reads as protection in review. The hazard
        # that is actually reachable is a `project` that is absolute or contains `..`, which would send
        # a recursive delete outside the repository -- so that is what is checked.
        # BOTH SIDES ARE RESOLVED, and that is load-bearing rather than decorative.
        # `is_relative_to` compares LEXICALLY, so `repo / "../elsewhere" / "TestResults"` still
        # "starts with" `repo` and the guard passed -- which is the exact case it exists for. The
        # test caught this by failing NO-RESULTS-DIR instead of RESULTS-DIR-UNSAFE: the escape
        # sailed through the guard and the delete then found no directory at all.
        if not results_dir.resolve().is_relative_to(repo.resolve()):
            raise Refusal("RESULTS-DIR-UNSAFE",
                          f"refusing to remove {results_dir}: it is outside the repository, which means "
                          f"--project named a path the repository does not own")
        if results_dir.is_dir():
            shutil.rmtree(results_dir)

        clauses = test_clauses(args.filter, args.include_timing_tests)
        exit_code, output = run_tests(project, clauses, repo, args.timeout)
        if exit_code != 0:
            # The tail, because the whole point is that the original showed nothing here.
            tail = [ln for ln in output.splitlines() if ln.strip()][-8:]
            raise Refusal("TESTS-FAILED",
                          f"dotnet test {project} exited {exit_code}; coverage of a red suite means "
                          f"nothing" + ("\n" + "\n".join(tail) if tail else " (no output at all)"))

        report = newest_coverage_report(results_dir)
        rows = rows_for_namespace(read_classes(report), args.namespace)
        if not rows:
            raise Refusal("NO-MATCHING-CLASSES",
                          f"no class in {report.name} starts with '{args.namespace}'; the project that "
                          f"ran was {project}, so the number you wanted may be in another project")
        rows.sort(key=lambda r: (r.line, r.cls))
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "rows": []}, indent=2))
        else:
            print(f"COVERAGE REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    under = [r for r in rows if r.line < args.threshold] if args.threshold > 0 else []
    breached = bool(under)
    total, covered, count = summarise(rows, args.namespace)

    if args.json:
        print(json.dumps({
            "tool": TOOL_ID,
            "verdict": "FAIL" if breached else "OK",
            "namespace": args.namespace,
            "project": project,
            "project_from_manifest": resolved,
            "filter": clauses,
            "threshold": args.threshold,
            "total_lines": total,
            "covered_lines": covered,
            "class_count": count,
            "under_threshold": [asdict(r) for r in under],
            "rows": [asdict(r) for r in rows],
        }, indent=2))
        return EXIT_FAILED if breached else EXIT_OK

    print(render_table(rows, project, args.namespace, resolved))
    if under:
        print(f"below the {args.threshold}% floor:", file=sys.stderr)
        for row in under:
            print(f"  {row.cls} ({row.line}%)", file=sys.stderr)
    return EXIT_FAILED if breached else EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
