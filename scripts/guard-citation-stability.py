#!/usr/bin/env python3
"""Refuse a change that MOVES a line someone cited by number.

WHAT THIS IS FOR. The two index files are cited by line, not by name: 633 citations naming
`decisions.md` or `DESIGN-GATE.md`, across 192 files (measured: 534 and 99 respectively; the 59
section-form `§` citations are NOT line-fragile and are out of scope). A citation like
`decisions.md:155` is a promise that line 155 still holds what it held when the sentence was
written. Inserting one row above line 155 keeps every citation syntactically valid and silently
repoints all of them at different text.

`scripts/split-decisions.py --check` does NOT catch this, and a control proved it: planting one
blank line at `decisions.md` line 5 leaves that tool at exit 0. Its job is generator consistency -
the split and the per-category files agree - not citation stability.

THE RULE, WHICH IS ABOUT MEANING RATHER THAN NUMBERS. For every citation found, this guard
fingerprints the line it points at and compares it with a committed baseline. A change that shifts a
cited line therefore changes the fingerprint at that line and is refused. A change that renumbers a
citation but lands on text with the same fingerprint is NOT refused, because nothing was misdirected
- refusing that would be refusing a harmless edit and would teach people to re-baseline to get work
in. Refusing a changed LINE COUNT would have the same defect: the count can change for a reason that
misdirects nothing.

So the invariant is: every positional citation still points at the same text. That is the property
the 633 citations actually depend on, and it is checkable without a human in the loop.

USAGE
    python scripts/guard-citation-stability.py            # the gate; exit 1 on any drift
    python scripts/guard-citation-stability.py --report   # list every citation and its fingerprint
    python scripts/guard-citation-stability.py --update   # re-baseline, PRINTING what changed

--update exists because the baseline must be re-taken after a deliberate, reviewed edit. It is
never implicit and this guard never calls it: a gate that can quietly re-baseline is not a gate.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / "lib"))
from keepverse_roots import workspace_root  # noqa: E402  (the shim above must run first)

BASELINE = pathlib.Path(__file__).resolve().parent / "citation-stability.v1.json"

# The closed set of line-cited files. Declared rather than globbed so that ADDING a file to this set
# is an explicit, reviewable edit - a glob would silently widen what the gate protects.
CITED = (
    ("decisions.md", "docs/architecture/decisions.md"),
    ("DESIGN-GATE.md", "docs/DESIGN-GATE.md"),
)

CITATION = re.compile(r"(?P<basename>decisions\.md|DESIGN-GATE\.md):(?P<line>\d+)")

SKIP_DIRS = {".git", "node_modules", "obj", "bin", "__pycache__", "dist", ".kilo", "wwwroot"}
SCANNED_SUFFIXES = {".md", ".py", ".cs", ".ts", ".tsx", ".yml", ".yaml", ".json", ".cfg"}


def normalise(line: str) -> str:
    """The line's identity for comparison: whitespace collapsed, so a re-indent is not a drift."""
    return " ".join(line.split())


def fingerprint(line: str) -> str:
    return hashlib.sha256(normalise(line).encode("utf-8")).hexdigest()[:16]


def cited_files(ws: pathlib.Path):
    """Every tracked-ish text file that could carry a citation, deduplicated by real path."""
    seen: dict[str, pathlib.Path] = {}
    for p in sorted(ws.rglob("*")):
        if not p.is_file() or p.suffix not in SCANNED_SUFFIXES:
            continue
        if any(part in SKIP_DIRS for part in p.parts):
            continue
        try:
            key = str(p.resolve()).lower()
        except OSError:
            continue
        seen.setdefault(key, p)
    return list(seen.values())


def collect(ws: pathlib.Path) -> dict[str, tuple[str, str]]:
    """Map `basename:line` -> (citing file, citing line) for every citation in the workspace."""
    found: dict[str, tuple[str, str]] = {}
    for p in cited_files(ws):
        try:
            text = p.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for m in CITATION.finditer(text):
            key = f"{m.group('basename')}:{int(m.group('line'))}"
            if key not in found:
                where = text.count("\n", 0, m.start()) + 1
                found[key] = (str(p.relative_to(ws)), str(where))
    return found


def index_lines(ws: pathlib.Path) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for basename, rel in CITED:
        path = ws / rel
        if not path.is_file():
            raise SystemExit(f"REFUSING: the cited index {rel} does not exist under {ws}")
        out[basename] = path.read_text(encoding="utf-8").splitlines()
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--report", action="store_true", help="print every citation and its fingerprint")
    ap.add_argument("--update", action="store_true", help="re-baseline, printing what changed")
    args = ap.parse_args()

    ws = pathlib.Path(workspace_root())
    citations = collect(ws)
    lines = index_lines(ws)

    # The current fingerprint of every cited line. A citation past the end of its file has NO
    # fingerprint, which is itself a finding rather than something to skip quietly.
    current: dict[str, str | None] = {}
    for key in citations:
        basename, _, raw = key.rpartition(":")
        n = int(raw)
        body = lines[basename]
        current[key] = fingerprint(body[n - 1]) if 1 <= n <= len(body) else None

    if args.report:
        print(f"CITATION STABILITY REPORT - {len(citations)} positional citation(s)")
        for key in sorted(citations, key=lambda k: (k.rpartition(':')[0], int(k.rpartition(':')[2]))):
            where = citations[key]
            print(f"   {key:<26} {current[key] or 'PAST END OF FILE':<18} {where[0]}:{where[1]}")
        return 0

    if args.update:
        BASELINE.write_text(json.dumps(current, indent=1, sort_keys=True) + "\n", encoding="utf-8")
        print(f"re-baselined {len(current)} citation(s) -> {BASELINE.name}")
        return 0

    if not BASELINE.is_file():
        print(f"REFUSING: no baseline at {BASELINE}. Run with --update to take one deliberately.",
              file=sys.stderr)
        return 2

    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))

    drift: list[str] = []
    for key, fp in sorted(current.items()):
        was = baseline.get(key)
        if was is None:
            drift.append(f"  NEW CITATION {key} - no baseline entry "
                         f"(cited from {citations[key][0]}:{citations[key][1]})")
        elif fp is None:
            drift.append(f"  {key} now points PAST THE END of its file "
                         f"(cited from {citations[key][0]}:{citations[key][1]})")
        elif fp != was:
            drift.append(f"  {key} now holds different text "
                         f"({citations[key][0]}:{citations[key][1]}) - the line moved or changed")

    gone = [k for k in baseline if k not in current]

    if not drift and not gone:
        print(f"CITATION STABILITY GUARD OK - {len(current)} citation(s), "
              f"{len(lines['decisions.md'])}-line decisions.md, no drift")
        return 0

    print("REFUSING: a change moved a line that is cited by number.", file=sys.stderr)
    for d in drift:
        print(d, file=sys.stderr)
    for k in gone:
        print(f"  (note) baseline entry {k} is no longer cited - re-baseline deliberately "
              f"once you have confirmed the citation was removed on purpose", file=sys.stderr)
    print("  If the edit was deliberate and you have checked each cited line still means what the "
          "citing sentence says, re-take the baseline with --update.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())