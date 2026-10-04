#!/usr/bin/env python3
"""Split the DESIGN-GATE.md §1 reading index into a compact index + per-category files.

WHY
---
`docs/DESIGN-GATE.md` is 52 KB, and §1 alone is 32,695 of those bytes across 36
topic rows whose cells run to 1,999 characters. Every agent reads the whole
file before proposing anything, so the rows it does not need are pure context
cost.

THE CONSTRAINT THAT SHAPES THIS
--------------------------------
The gate is cited two ways, and both must survive:
  * 56 NAMED section references (`DESIGN-GATE.md §1`, `§2.16`, `§3.2`, `§5`) —
    so §0-§5 headings stay exactly where they are.
  * 62 LINE anchors, of which 59 land inside the §1 table. §1 rows occupy lines
    30-65 with no gaps, so keeping one index row per topic on its original line
    keeps every one of those 59 pointing at the topic it named.

§2 (the 16 load-bearing invariants), §3 (evidence rules), §4 (failure log) and
§5 (the checklist) are not touched at all.

CATEGORIES ARE SHARED WITH decisions.md
---------------------------------------
The same 12 names, so there is one taxonomy across the two lock files and an
agent learns it once. A category with no §1 row simply gets no file — the gate
has no reading requirement for it, and the index says so rather than inventing
an empty page.

Usage
-----
    python gk-core/scripts/split-design-gate.py --check
    python gk-core/scripts/split-design-gate.py --apply
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys

# The docs are the WORKSPACE ROOT's, not this repository's: the split moved docs/ up a level, so a
# parent-of-this-file root resolved to a path that does not exist and made every run refuse with
# "does not exist" instead of checking anything. Resolved through the same shim 23 sibling scripts
# use - and `workspace_root(` is the token kvsplit's rules/scan.v1.json `resolvers` looks for.
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402  (the shim above runs first)

# RESOLVED IN A FUNCTION, NOT AT IMPORT — the same correction as split-decisions.py, and for the same
# measured reason: these module constants raised `keepverse_roots.RootNotFound` DURING IMPORT in a
# standalone clone, so the tool died with a traceback and exit 1 — indistinguishable, by exit code,
# from a real "the gate drifted" refusal. The repository's own rule is a NAMED refusal, never an
# unhandled error. `program_status.py` is the model: it prints the name, the detail and its meaning,
# and exits 2.
def _roots() -> "tuple[pathlib.Path, pathlib.Path]":
    try:
        root = pathlib.Path(workspace_root())
    except RootNotFound as exc:
        print(f"REFUSING [ROOT-NOT-FOUND]: {exc}. This generator's subject is the workspace root's "
              f"docs/DESIGN-GATE.md, which lives in the Keepverse workspace root and not in this "
              f"repository — a standalone gk-core clone has no such document to split. Run it from the "
              f"workspace, or set KEEPVERSE_WORKSPACE_ROOT to the root holding docs/",
              file=sys.stderr)
        raise SystemExit(2) from exc
    return root, root / "docs" / "DESIGN-GATE.md"

FIRST_ROW_LINE = 30
LAST_ROW_LINE = 65

# Categories that exist in docs/architecture/decisions/ but have no §1 reading
# row. Listed so the index can say "no gate row" instead of implying one.
NO_GATE_ROW = ("transport", "launcher")

CATEGORY_BY_LINE: dict[int, str] = {}


def _register(category: str, *lines: int) -> None:
    for line in lines:
        CATEGORY_BY_LINE[line] = category


_register("repo-tooling",   30, 31, 55, 64, 65)
_register("game-host",      32, 33, 46)
_register("combat",         34, 41, 42, 43, 45, 51, 52, 56, 57, 58)
_register("stats",          35, 36)
_register("power-caps",     37, 38, 39, 40)
_register("progression",    44, 48, 49, 50)
_register("content-gen",    47)
_register("persistence",    53)
_register("world",          54, 59, 63)
_register("presentation",   60, 61, 62)

CATEGORY_BLURB: dict[str, str] = {
    "repo-tooling": "The universal gate, product framing, performance, live-proof scope, creative runs.",
    "game-host": "How the injector talks to the game, where logic may live, and host-game mechanics.",
    "combat": "Damage, effects, the atom layer, status, elements, and the battle engine.",
    "stats": "The stat system and the actor layer stack.",
    "power-caps": "Caps, tunables, numeric ranges, and the one power ladder.",
    "progression": "Rarity ladders, creature vocabulary, economy and resources.",
    "content-gen": "Species generation and the seedsmith pipelines.",
    "persistence": "Data, SQL and schema.",
    "world": "Match and actor lifecycle, the world map, and the standalone web RPG.",
    "presentation": "Everything a player sees: UI, band-2 menus, and lawn stage chrome.",
}

ROW = re.compile(r"^\|\s*(?P<topic>.+?)\s*\|\s*(?P<must>.+?)\s*\|\s*(?P<wrong>.*)$")


def parse(text: str) -> tuple[list[str], list[tuple[int, str, str]], list[str]]:
    lines = text.split("\n")
    head = lines[: FIRST_ROW_LINE - 1]
    rows: list[tuple[int, str, str]] = []
    for lineno in range(FIRST_ROW_LINE, LAST_ROW_LINE + 1):
        raw = lines[lineno - 1]
        if not ROW.match(raw):
            raise SystemExit(f"REFUSING: line {lineno} is not a 3-column topic row: {raw[:80]!r}")
        rows.append((lineno, ROW.match(raw).group("topic").strip(), raw))
    tail = lines[LAST_ROW_LINE:]
    return head, rows, tail


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    # Resolved here, so an unresolvable root is the NAMED refusal in `_roots()` rather than an
    # import-time traceback. Both exit 2, which is the point.
    root, gate = _roots()
    if not gate.exists():
        print(f"REFUSING [GATE-MISSING]: {gate} does not exist")
        return 2

    text = gate.read_text(encoding="utf-8")
    head, rows, tail = parse(text)

    missing = [ln for ln, _, _ in rows if ln not in CATEGORY_BY_LINE]
    if missing:
        print(f"REFUSING: {len(missing)} §1 row(s) have no category: {missing}")
        print("  Every row must be classified. An unmapped row is a hole, not a default.")
        return 3

    by_cat: dict[str, list[tuple[int, str, str]]] = {}
    for lineno, topic, raw in rows:
        by_cat.setdefault(CATEGORY_BY_LINE[lineno], []).append((lineno, topic, raw))

    unknown = set(by_cat) - set(CATEGORY_BLURB)
    if unknown:
        print(f"REFUSING: categories with no blurb: {sorted(unknown)}")
        return 3

    index = list(head)
    # Rewrite only the two prose lines that describe the table, so the index
    # tells the truth about being an index. Verified offsets: L25 and L26 are the
    # two prose lines, L27 blank, L28 the header, L29 the separator, L30 row 1.
    index[24] = (
        "Find the row for what you are about to touch. **This table is an index**: one row per topic, "
        "in the original order, so a `DESIGN-GATE.md:<line>` citation still resolves to the topic it "
        "named. The documents you MUST read, and the mistake sessions actually make, are in the "
        "category file the row links to."
    )
    index[25] = (
        "Categories are the same 12 used by [architecture/decisions.md](architecture/decisions.md). "
        "A topic with no row here falls to **Anything at all**. Categories with no reading row: "
        + ", ".join(f"`{c}`" for c in NO_GATE_ROW)
        + "."
    )
    for lineno, topic, _raw in rows:
        cat = CATEGORY_BY_LINE[lineno]
        # §1 topics are ALREADY bolded in the source (`| **Anything at all** |`).
        # Re-wrapping them yields `****`, so only add emphasis when it is absent.
        clean = topic.replace("|", "\\|")
        if not (clean.startswith("**") and clean.endswith("**")):
            clean = f"**{clean}**"
        index.append(f"| {clean} | [{cat}](design-gate/{cat}.md) | |")
    index.extend(tail)

    out = gate.parent / "design-gate"
    files: dict[pathlib.Path, str] = {}
    for cat, cat_rows in by_cat.items():
        body = [
            f"# Design gate — {cat}",
            "",
            f"**{CATEGORY_BLURB[cat]}**",
            "",
            f"Index: [../DESIGN-GATE.md](../DESIGN-GATE.md) §1. Each row keeps its original "
            f"`DESIGN-GATE.md:<line>` number in a trailing comment, so a citation to the index still "
            f"points at the topic it named before the split.",
            "",
            "| If you're about to touch… | You MUST have read | What sessions get wrong |",
            "|---|---|---|",
        ]
        for lineno, _topic, raw in cat_rows:
            body.append(f"{raw} <!-- DESIGN-GATE.md:{lineno} -->")
        files[out / f"{cat}.md"] = "\n".join(body) + "\n"

    new_index = "\n".join(index)
    before_lines = len(text.split("\n"))
    if len(index) != before_lines:
        print(f"REFUSING: line count changed {before_lines} -> {len(index)}; line anchors would move")
        return 4

    print(f"§1 topic rows : {len(rows)}")
    print(f"categories    : {len(by_cat)}  (no reading row: {', '.join(NO_GATE_ROW)})")
    print(f"index lines   : {len(index)}  (unchanged: {len(index) == before_lines})")
    print(f"index size    : {len(new_index.encode()):,} bytes  (was {len(text.encode()):,})")
    for cat in sorted(by_cat):
        size = len(files[out / f'{cat}.md'].encode('utf-8')) if args.apply else 0
        print(f"  {cat:<14} {len(by_cat[cat]):>2} rows  {size:,}b")

    if not args.apply:
        print("\n(dry run -- pass --apply to write)")
        return 0

    out.mkdir(parents=True, exist_ok=True)
    for path, body in files.items():
        path.write_text(body, encoding="utf-8")
    gate.write_text(new_index, encoding="utf-8")
    print(f"\nwrote {len(files)} category files + rewrote {gate.relative_to(root)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
