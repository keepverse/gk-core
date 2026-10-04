#!/usr/bin/env python3
"""
Guard: every row of a positional index is owned by a category row that says which line it is.

WHAT THIS IS FOR. `docs/architecture/decisions.md` and `docs/DESIGN-GATE.md` are INDEXES. Each is one
line per rule, and the rule text lives in a category file that the row links to. That makes every
`decisions.md:155` in the tree a POSITIONAL citation, so the only thing tying a category row to its
index row is the marker that row carries: `<!-- decisions.md:155 -->`.

The generator that used to maintain that correspondence is gone, so the accounting is manual, and the
check that exists (`check-decision-citations.py`) validates markers THAT EXIST: it compares a marker's
bolded title against the bolded title at the line it names and reports `MARKER-DRIFT` on a
disagreement. It never reports a row that LACKS a marker, so the index-to-category linkage of eight
rows was verified by nothing at all - measured, not assumed: 8 of 158 rows carried no marker, and one
row carried a marker naming the wrong line, which duplicated another row's claim and went unreported
for two different reasons at once (the marker named a non-bolded index row, and the comparison skips
a row that is not bolded on either side).

THE FAILURE MODES, each one a shape with no judgement in it:

    M1 ROW-UNMARKED      an index data row that no marker names. The row's rule text is unreachable
                         from the index by any checked path: nothing asserts the category file even
                         holds it, so a rule can be deleted from its category file and the index keeps
                         pointing at a file that no longer says it.
    M2 MARKER-NO-ROW     a marker naming a line that holds no index data row - out of range, blank,
                         the header, or a line in some other table in the same file.
    M3 TITLE-MISMATCH    a marker's row title disagrees with the index row at the line it names. A
                         number that survives a shift while naming a different row is the failure the
                         marker exists to catch, and comparing the number alone cannot see it.
    M4 DUPLICATE-CLAIM   two category rows naming the same index line. One index row, one owner, so
                         the second claim means one of the two rows is not actually indexed there.

THE COMPARISON IS THE FIRST CELL, BOLD-NORMALISED - never the number and never only the bolded rows.
Comparing only bolded titles is the specific blind spot this guard closes: it makes a marker that
names a plain row unverifiable, which is how the wrong-line marker survived. Stripping `**` and
collapsing whitespace compares every row, bolded or not, with no exemption to keep in step.
Measured on the tree this landed on: 194 of 194 existing markers agree, so the rule adds no
back-log for a format that is already in use.

AN INDEX DATA ROW IS DEFINED BY ITS CONTENT, NOT BY A LINE NUMBER. It is a markdown table row that
links to a file under that index's own category directory (`](decisions/<name>.md)`,
`](design-gate/<name>.md)`). Hardcoding `decisions.md:7..164` would make this guard the next thing
that rots on an append - the exact failure it exists to detect - and would silently start reporting
the header as an unmarked row the moment a row was inserted above it. The rule is content-based, so
appending a row extends the checked set with no edit here.

FAILS CLOSED, AND REFUSES RATHER THAN PASSING ON AN EMPTY SET. A guard that reads zero index rows
because a pattern stopped matching reports success while checking nothing, which is the most
dangerous state a gate can be in; zero rows is a named refusal with exit 2, never a green run. It
writes nothing, so it cannot make the tree it audits look clean by editing it.

USAGE (from gk-core, or anywhere in the workspace):
    python scripts/guard-index-marker-coverage.py            # the gate; exit 1 on any finding
    python scripts/guard-index-marker-coverage.py --json     # machine-readable verdict
    python scripts/guard-index-marker-coverage.py --root P   # a workspace root other than the resolved one

Exit 0 = every index row is owned and every marker agrees. Exit 1 = an M1-M4 finding, named.
Exit 2 = it could not run, and it says which prerequisite was missing.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402

#: The checked indexes. Declared, not globbed: adding an index to this set must be an explicit,
#: reviewable edit, because a glob would widen a gating guard's reach without anyone deciding to.
#: Each entry is (marker basename, index path, category directory, category directory name).
INDEXES = (
    ("decisions.md", "docs/architecture/decisions.md", "docs/architecture/decisions", "decisions"),
    ("DESIGN-GATE.md", "docs/DESIGN-GATE.md", "docs/design-gate", "design-gate"),
)

#: An in-table marker a category row carries pointing back at its index row.
MARKER = re.compile(r"<!--\s*([\w./\\-]*\b(?:decisions|DESIGN-GATE)\.md):(\d+)\s*-->")

#: An index DATA row: a table row whose cells link into that index's own category directory. This is
#: what separates the index table from every other table in the same file - DESIGN-GATE.md carries an
#: incident log whose rows are table rows and are not index rows, and a separator line is one too.
SEPARATOR = re.compile(r"[|:\-\s]+")


class CannotRun(RuntimeError):
    """A named prerequisite is missing. Never a crash, never a green run, never an empty check."""


def _is_separator(line: str) -> bool:
    return bool(SEPARATOR.fullmatch(line.strip()))


def _category_link(name: str) -> "re.Pattern[str]":
    return re.compile(r"\]\(" + re.escape(name) + r"/[A-Za-z0-9._-]+\.md\)")


def _row_title(line: str) -> str | None:
    """A table row's FIRST CELL with markdown bold removed and whitespace collapsed.

    The first cell is the rule's title on both sides of the correspondence: the index's `| Topic |`
    cell and the category file's own topic cell. Bold is stripped rather than required, so a marker
    naming a plain (unbolded) row is as checkable as one naming a bolded row - which is the point.
    """
    stripped = line.strip()
    if not stripped.startswith("|"):
        return None
    cell = stripped[1:].split("|")[0]
    return " ".join(cell.replace("**", "").split())


def index_data_rows(lines: "list[str]", category_dir_name: str) -> "dict[int, str]":
    """Line number -> title, for the index's data rows only."""
    link = _category_link(category_dir_name)
    rows: dict[int, str] = {}
    for n, line in enumerate(lines, 1):
        if not line.strip().startswith("|") or _is_separator(line):
            continue
        if not link.search(line):
            continue
        title = _row_title(line)
        if title:
            rows[n] = title
    return rows


def collect_markers(ws: Path, basename: str, category_dir: Path) -> "dict[int, list[dict]]":
    """Index line -> every category row claiming it. More than one claimant is M4, not a merge."""
    claims: dict[int, list[dict]] = {}
    if not category_dir.is_dir():
        raise CannotRun(f"the category directory is missing: {category_dir}")
    files = sorted(category_dir.glob("*.md"))
    if not files:
        raise CannotRun(f"the category directory holds no category file: {category_dir}")
    for path in files:
        for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for m in MARKER.finditer(line):
                if not m.group(1).endswith(basename):
                    continue
                claims.setdefault(int(m.group(2)), []).append(
                    {"doc": path.relative_to(ws).as_posix(), "line": n, "text": line}
                )
    return claims


def check_index(ws: Path, basename: str, index_rel: str, category_rel: str, category_name: str) -> dict:
    index_path = ws / index_rel
    if not index_path.is_file():
        raise CannotRun(f"the checked index does not exist: {index_path}")

    lines = index_path.read_text(encoding="utf-8").splitlines()
    rows = index_data_rows(lines, category_name)
    claims = collect_markers(ws, basename, ws / category_rel)

    if not rows:
        # The dangerous state: a pattern stopped matching and the guard would report success while
        # checking nothing. Refuse instead.
        raise CannotRun(
            f"{index_rel} yielded ZERO index data rows -- no table row links into "
            f"{category_name}/. Refusing rather than reporting a clean run against an empty set."
        )

    findings: list[dict] = []

    # M1 - a row nobody claims. This is the check the retired generator performed and
    # check-decision-citations.py does not: it validates markers that exist, never absence.
    for n in sorted(set(rows) - set(claims)):
        findings.append(dict(code="M1-ROW-UNMARKED", line=n,
                             note=f"index row has no marker naming it: {rows[n]!r}"))

    for n in sorted(claims):
        holders = claims[n]
        if n < 1 or n > len(lines):
            findings.append(dict(code="M2-MARKER-NO-ROW", line=n,
                                 note=f"marker names line {n}, past the end of {index_rel} "
                                      f"({len(lines)} lines) - {holders[0]['doc']}:{holders[0]['line']}"))
            continue
        if n not in rows:
            # Either a line that is not a row at all, or a row of a DIFFERENT table in the same file.
            what = "blank" if not lines[n - 1].strip() else (
                "the table header" if lines[n - 1].strip().startswith("|") and
                not _category_link(category_name).search(lines[n - 1]) else "a row outside this index")
            findings.append(dict(code="M2-MARKER-NO-ROW", line=n,
                                 note=f"marker names line {n}, which holds {what} - "
                                      f"{holders[0]['doc']}:{holders[0]['line']}"))
            continue

        # M4 - two category rows claiming one index line. Checked before the title comparison
        # because it is a different defect: both titles could agree with each other while only one
        # of the rows is the row actually indexed there.
        if len(holders) > 1:
            where = ", ".join(f"{h['doc']}:{h['line']}" for h in holders)
            findings.append(dict(code="M4-DUPLICATE-CLAIM", line=n,
                                 note=f"{len(holders)} category rows claim index line {n}: {where}"))

        # M3 - the title is the assertion; the number is only how it is written down.
        for h in holders:
            want = _row_title(h["text"])
            got = rows[n]
            if want is None:
                findings.append(dict(code="M3-TITLE-MISMATCH", line=n,
                                     note=f"marker at {h['doc']}:{h['line']} is not on a table row"))
            elif want != got:
                findings.append(dict(code="M3-TITLE-MISMATCH", line=n,
                                     note=f"{h['doc']}:{h['line']} claims {want!r} but index line {n} "
                                          f"is {got!r}"))

    return {
        "index": index_rel,
        "index_lines": len(lines),
        "rows": len(rows),
        "rows_first": min(rows),
        "rows_last": max(rows),
        "marked_lines": len(set(claims) & set(rows)),
        "markers": sum(len(v) for v in claims.values()),
        "findings": findings,
    }


def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Refuse an index row that no category row claims, or a marker that names the wrong one.")
    parser.add_argument("--json", action="store_true", help="print the machine-readable verdict")
    parser.add_argument("--root", default="", help="workspace root (default: the resolved workspace root)")
    args = parser.parse_args(argv)

    try:
        ws = Path(args.root).resolve() if args.root else Path(workspace_root())
    except RootNotFound as exc:
        print(f"REFUSING INDEX-MARKER-COVERAGE: the checked indexes are gk-workflow's, which this "
              f"clone cannot see: {exc}", file=sys.stderr)
        print("  Set KEEPVERSE_WORKSPACE_ROOT, or run inside the workspace.", file=sys.stderr)
        return 2

    reports: list[dict] = []
    try:
        for basename, index_rel, category_rel, category_name in INDEXES:
            reports.append(check_index(ws, basename, index_rel, category_rel, category_name))
    except CannotRun as exc:
        print(f"REFUSING INDEX-MARKER-COVERAGE: {exc}", file=sys.stderr)
        return 2

    findings = [f for r in reports for f in r["findings"]]

    if args.json:
        print(json.dumps({"guard": "index-marker-coverage", "ok": not findings,
                          "indexes": reports, "findings": findings}, indent=2, ensure_ascii=False))
        return 1 if findings else 0

    for r in reports:
        print(f"{r['index']}: {r['rows']} data row(s) (lines {r['rows_first']}-{r['rows_last']}) of "
              f"{r['index_lines']}, {r['markers']} marker(s) covering {r['marked_lines']} of them")

    if not findings:
        print("INDEX-MARKER-COVERAGE GUARD OK - every index row is claimed by exactly one category "
              "row, every marker names a row, and every title agrees")
        return 0

    by_code: dict[str, int] = {}
    for f in findings:
        by_code[f["code"]] = by_code.get(f["code"], 0) + 1
    print("\nINDEX-MARKER-COVERAGE GUARD FAILED:")
    for code, n in sorted(by_code.items()):
        print(f"  {code:20} {n}")
    print()
    for f in findings:
        where = f.get("doc")
        loc = f"{where}:{f['line']}" if where else f"index line {f['line']}"
        print(f"  {f['code']:20} {loc}")
        print(f"      {f['note']}")
    print("\nAn index row nothing claims is a rule the index cannot open; a marker naming a "
          "different row is a citation that resolves to the wrong decision. Give the row its "
          "marker, or correct the marker to the line the row actually occupies.")
    return 1


if __name__ == "__main__":
    sys.exit(main())