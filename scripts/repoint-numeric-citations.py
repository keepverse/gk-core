#!/usr/bin/env python3
"""Repoint docs/architecture/numeric-types.md numeric citations at docs/architecture/numeric-types.md.

Reads: the numeric SSOT created 2026-09-27, plus an explicit allowlist of
citations that point at a DIFFERENT AGENTS.md rule (caps, ActorHub, generated
seed, no-private-f, layer boundary) and must not be touched.

Only lines whose claim is about numeric types / overflow / range / per-mille
math are rewritten. Everything else is left byte-identical, because AGENTS.md
still owns those rules and this pass does not move them.

Usage:  python gk-core/scripts/repoint-numeric-citations.py [--check] [--apply]
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
NEW_DOC = "docs/architecture/numeric-types.md"

# Areas to sweep. `src/`, `tests/`, `tools/`, `web/`, `scripts/` -- the places a
# code comment can cite a rule. `docs/` and `tasks/` are prose and are handled
# separately; this pass is the mechanical one.
AREAS = ("src", "tests", "tools", "web", "scripts")

SKIP_DIR_PARTS = {"bin", "obj", "node_modules", "__pycache__", ".venv", "dist", ".git"}

# A line is a NUMERIC citation when it mentions one of these markers within the
# same comment. Chosen from the actual corpus, not invented.
NUMERIC_MARKERS = re.compile(
    r"""
      numeric
    | overflow
    | per-mille
    | per_mille
    | \bMilli\b
    | \bmilli\b
    | \bTheta\b|\bΘ\b
    | 2\^24|2\^53
    | 103,557|3,213
    | \bchecked\b
    | widen
    | narrowing
    | \blong\b
    | \bint\b
    | \bdouble\b|\bfloat\b
    | saturat
    | divide by 1000|/ 1000
    | 1000\b
    | magnitude
    | non-deterministic|deterministic
    | private f\(level\)|no private f
    """,
    re.IGNORECASE | re.VERBOSE,
)

# Citations that name a DIFFERENT AGENTS.md rule by its heading. These must keep
# pointing at AGENTS.md -- that file still owns them.
FOREIGN_RULES = re.compile(
    r"""
      "Caps"|\bCaps\b
    | One\s+ActorHub
    | Generated seed data is never hand-edited
    | no private f\(level\)|private curve
    | depend on registered abstractions
    | RPG layer
    | every RPG feature
    | closed-vocabulary table
    | balance-surface|balance surface
    | never hand-edited
    | hardcode/commit|hardcoded
    | Injector not built by CI
    | no hard progression ceiling
    | worktree|checkpoint record
    | local-only assistant
    | charter rule
    | population-pin|never pinned|generated text
    | no private per-sector|per-sector combat fold
    | SOLID-boundary
    | observed rather than reimplements
    | works? like|depth-price
    | F10
    """,
    re.IGNORECASE | re.VERBOSE,
)

CLAUDE_REF = re.compile(r"CLAUDE\.md")


def classify(line: str) -> str:
    """Return 'numeric', 'foreign', or 'skip' for one source line."""
    if not CLAUDE_REF.search(line):
        return "skip"
    # Only judge the neighbourhood of the citation, not the whole file.
    idx = line.find("AGENTS.md")
    window = line[max(0, idx - 260) : idx + 200]
    if FOREIGN_RULES.search(window):
        return "foreign"
    if NUMERIC_MARKERS.search(window):
        return "numeric"
    return "foreign"


def iter_files() -> list[pathlib.Path]:
    out: list[pathlib.Path] = []
    for area in AREAS:
        base = REPO_ROOT / area
        if not base.is_dir():
            continue
        for path in base.rglob("*"):
            if not path.is_file():
                continue
            if SKIP_DIR_PARTS & set(path.parts):
                continue
            if path.suffix.lower() not in {".cs", ".py", ".ps1", ".ts", ".tsx", ".mjs", ".json", ".md"}:
                continue
            out.append(path)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="write the rewrites")
    ap.add_argument("--check", action="store_true", help="report only (default)")
    args = ap.parse_args()

    files = iter_files()
    changed_files: list[tuple[pathlib.Path, int]] = []
    total_hits = 0

    for path in files:
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if "AGENTS.md" not in text:
            continue

        lines = text.splitlines(keepends=True)
        hits = 0
        for i, line in enumerate(lines):
            if classify(line) != "numeric":
                continue
            hits += 1
            # Repoint the citation, keeping whatever wording followed it.
            lines[i] = CLAUDE_REF.sub(f"{NEW_DOC}", line)
        if hits:
            changed_files.append((path, hits))
            total_hits += hits
            if args.apply:
                path.write_text("".join(lines), encoding="utf-8")

    print(f"files scanned      : {len(files)}")
    print(f"files with rewrites: {len(changed_files)}")
    print(f"lines rewritten    : {total_hits}")
    if not args.apply:
        print("\n(dry run -- pass --apply to write)")
        print("\nper-file:")
        for path, hits in sorted(changed_files, key=lambda t: -t[1])[:40]:
            rel = path.relative_to(REPO_ROOT).as_posix()
            print(f"  {hits:3}  {rel}")
        if len(changed_files) > 40:
            print(f"  ... and {len(changed_files) - 40} more files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
