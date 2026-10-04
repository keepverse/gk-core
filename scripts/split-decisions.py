#!/usr/bin/env python3
"""Split docs/architecture/decisions.md into a compact index + per-category files.

WHY
---
`decisions.md` is 220 lines but 169 KB, because it is a two-column table whose
Decision cells run to 6,738 characters. 915 tracked files cite it and 385 of
those citations carry a LINE anchor, so an agent that needs one decision has to
read all 169 KB to find it.

THE CONSTRAINT THAT SHAPES THIS
--------------------------------
Decision *k* lives at line 6+k (line 7 is the first row). 382 of the 385 line
anchors point INTO the table, and only 3 point into the trailing prose. So this
script keeps the table at exactly 149 rows in exactly the original order, one
line per row, with the original 6-line preamble and the trailing prose sections
untouched. Every line number therefore still resolves to the same decision it
resolved to before, and no citation anywhere in the repo has to be rewritten.

The index row carries the Topic verbatim plus a link to the category file. The
topic names are already decision statements for most rows, so the index invents
no text and loses no rule -- the alternative, summarising the 91 rows that do
not open with a bold span, is precisely the lossy compression that strips the
incident narrative which makes a rule stick.

Usage
-----
    python gk-core/scripts/split-decisions.py --check    # report, write nothing
    python gk-core/scripts/split-decisions.py --apply    # write the index + category files
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
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402  (the path shim above first)

# RESOLVED IN A FUNCTION, NOT AT IMPORT. These three module constants were resolved at import time, so a
# checkout with no resolvable workspace raised `keepverse_roots.RootNotFound` DURING IMPORT and the tool
# died with a traceback and exit 1 — the same exit as a real "the split drifted" refusal, and the same
# shape this repository's own rule forbids: an UNHANDLED error where a NAMED refusal belongs.
# Measured in an isolated gk-core clone at cd04ab6: `split-decisions.py --check` and
# `audit-program-pipeline.py --check` both printed a bare traceback ending in
# `keepverse_roots.RootNotFound: no legacy repo or Keepverse workspace above ...`, while
# `program_status.py` — the model this is aligned to — printed a named refusal and exited 2.
#
# WHY IT MUST NOT BE SILENTLY TOLERATED EITHER: this tool's subject is the WORKSPACE ROOT's
# decisions.md, which gk-core does not carry. A standalone clone genuinely cannot answer, so the honest
# outcome is a named refusal that says which document it wanted and where it looked.
def _roots() -> "tuple[pathlib.Path, pathlib.Path]":
    """The workspace root, and the decisions.md this tool splits.

    THE FILE NAME IS `decisions.md`, NOT THE DIRECTORY `decisions/`. Those two were conflated when
    the import-time constants were replaced by this function: the returned path became
    `docs/architecture/decisions` and the file read then hit the CATEGORY DIRECTORY of an
    already-split workspace — a PermissionError on a directory, which is neither the tool's
    refusal nor a report about the split. Caught by running the workspace `--check` after the edit,
    which is the only reason it was caught at all: the clone cannot reach this code path.
    """
    try:
        root = pathlib.Path(workspace_root())
    except RootNotFound as exc:
        print(f"REFUSING [ROOT-NOT-FOUND]: {exc}. This generator's subject is the workspace root's "
              f"docs/architecture/decisions.md, which lives in the Keepverse workspace root and not in "
              f"this repository — a standalone gk-core clone has no such document to split. Run it from "
              f"the workspace, or set KEEPVERSE_WORKSPACE_ROOT to the root holding docs/.",
              file=sys.stderr)
        raise SystemExit(2) from exc
    return root, root / "docs" / "architecture" / "decisions.md"




# Line numbers are stable within the table because the split preserves them; the
# category is keyed by the ORIGINAL line so this file is auditable by eye
# against `sed -n '7,155p' docs/architecture/decisions.md`.
CATEGORY_BY_LINE: dict[int, str] = {}


def _register(category: str, *lines: int) -> None:
    for line in lines:
        CATEGORY_BY_LINE[line] = category


# --- transport & runtime plumbing -------------------------------------------
_register("transport",
          8, 9, 15, 18, 97, 99, 104, 106, 107)
# --- launcher, player entry, overlay surface --------------------------------
_register("launcher",
          12, 13, 14, 16, 17)
# --- the game host: loaders, injector, PvZ bridge, overlay loops -------------
_register("game-host",
          19, 20, 26, 27, 28, 29, 30, 31, 32, 69, 70, 71, 72, 73, 74, 103)
# --- stats and actor composition --------------------------------------------
_register("stats",
          21, 22, 23, 24, 25, 51, 52, 53, 54, 57, 126, 127, 128, 129, 130)
# --- combat mechanics -------------------------------------------------------
_register("combat",
          33, 34, 35, 36, 40, 41, 42, 43, 44, 45, 46, 47, 55, 56, 63, 64,
          122, 131, 132, 136, 137, 138, 141, 142, 143)
# --- presentation: VFX, HUD, canvas art, GUI --------------------------------
_register("presentation",
          10, 11, 37, 38, 39, 112, 113, 133, 134)
# --- power, caps, balance surface -------------------------------------------
_register("power-caps",
          58, 59, 60, 61, 62, 65)
# --- world turn, phases, sectors, match runtime -----------------------------
_register("world",
          7, 48, 49, 50, 67, 68, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85,
          135, 144, 145)
# --- persistence ------------------------------------------------------------
_register("persistence",
          88, 89, 90, 91, 92, 93, 94, 95, 96)
# --- progression, economy, creatures -----------------------------------------
_register("progression",
          66, 86, 87, 110, 114, 115, 116, 117, 118, 119, 120, 121,
          123, 124, 125, 146, 147, 148, 149, 150)
# --- generated content, narrative, seedsmith ---------------------------------
_register("content-gen",
          151, 152, 153, 154, 155)
# --- repository, testing, tooling, product framing --------------------------
_register("repo-tooling",
          98, 100, 101, 102, 105, 108, 109, 111, 139, 140)

CATEGORY_BLURB: dict[str, str] = {
    "transport": "SignalR / HTTP / CI / auth / live-web plumbing.",
    "launcher": "Player entry point, self-contained publish, and the launcher-drawn overlay surface.",
    "game-host": "Loader hosts, the injector, the PvZ middle layer, and the overlay control loops.",
    "stats": "The stat system, ActorHub composition, the actor layer stack, and SOLID.",
    "combat": "Damage, the effect Funnel, shields, elements, status, and the battle engine.",
    "presentation": "VFX, the Unity actor HUD, async canvas art, and the GUI stage kits.",
    "power-caps": "The one power ladder, the `B` dial, caps, and the balance surface.",
    "world": "Turn phase order, sector hierarchy, match lifecycle, and the Keepverse repo topology.",
    "persistence": "Contracts, SQLite, the DAL gate, schema migration, and archive timing.",
    "progression": "XP, resources, creatures, commanders, classes, legions, and structures.",
    "content-gen": "Generated narrative and seedsmith corpus rules.",
    "repo-tooling": "Docs language, the simulator, test probes, and registry versions.",
}

ROW = re.compile(r"^\|\s*(?P<topic>.+?)\s*\|\s*(?P<body>.*)$")
PREAMBLE_LINES = 6          # title, blank, intro, blank, header, separator
FIRST_ROW_LINE = 7
LAST_ROW_LINE = 155


def parse(text: str) -> tuple[list[str], list[tuple[int, str, str]], list[str]]:
    lines = text.split("\n")
    preamble = lines[:PREAMBLE_LINES]
    rows: list[tuple[int, str, str]] = []
    for lineno in range(FIRST_ROW_LINE, LAST_ROW_LINE + 1):
        raw = lines[lineno - 1]
        m = ROW.match(raw)
        if not m:
            raise SystemExit(f"REFUSING: line {lineno} is not a table row: {raw[:80]!r}")
        rows.append((lineno, m.group("topic").strip(), raw.rstrip("\n")))
    trailing = lines[LAST_ROW_LINE:]
    return preamble, rows, trailing


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    # Resolved here, so an unresolvable root is the NAMED refusal above rather than an import-time
    # traceback. Both exit 2, which is the point: the two failures must not read as one.
    root, decisions = _roots()
    if not decisions.exists():
        print(f"REFUSING [DECISIONS-MISSING]: {decisions} does not exist")
        return 2

    text = decisions.read_text(encoding="utf-8")
    preamble, rows, trailing = parse(text)

    unclassified = [ln for ln, _, _ in rows if ln not in CATEGORY_BY_LINE]
    if unclassified:
        print(f"REFUSING: {len(unclassified)} row(s) have no category: {unclassified}")
        print("  Every row must be classified. An unmapped row is a hole, not a default.")
        return 3

    by_cat: dict[str, list[tuple[int, str, str]]] = {}
    for lineno, topic, raw in rows:
        by_cat.setdefault(CATEGORY_BY_LINE[lineno], []).append((lineno, topic, raw))

    # ---- index -------------------------------------------------------------
    index: list[str] = list(preamble)
    index[2] = (
        "Locked for v1. Change here before changing code. **This table is an index**: one row per "
        "decision, in the original order, so a `decisions.md:<line>` citation still resolves to the "
        "same decision it always did. The rule text lives in the category file the row links to."
    )
    for lineno, topic, _raw in rows:
        cat = CATEGORY_BY_LINE[lineno]
        clean = topic.replace("|", "\\|")
        index.append(f"| {clean} | [{cat}](decisions/{cat}.md) |")
    index.extend(trailing)

    # ---- category files ----------------------------------------------------
    out = decisions.parent / "decisions"
    files: dict[pathlib.Path, str] = {}
    for cat, cat_rows in by_cat.items():
        body = [
            f"# Decisions — {cat}",
            "",
            f"**{CATEGORY_BLURB[cat]}**",
            "",
            f"Index: [../decisions.md](../decisions.md). Rows are listed with their original line "
            f"number in `decisions.md`, so a `{decisions.name}:<line>` citation points at the same "
            f"decision it did before the {decisions.name} split.",
            "",
            "| Topic | Decision |",
            "|---|---|",
        ]
        for lineno, _topic, raw in cat_rows:
            body.append(f"{raw} <!-- decisions.md:{lineno} -->")
        files[out / f"{cat}.md"] = "\n".join(body) + "\n"

    new_index = "\n".join(index)

    # ---- the invariant that makes this safe --------------------------------
    if len(index) != len(text.split("\n")):
        print(f"REFUSING: line count changed {len(text.split(chr(10)))} -> {len(index)}; "
              "line anchors would move")
        return 4

    print(f"rows            : {len(rows)}")
    print(f"categories      : {len(by_cat)}")
    print(f"index lines     : {len(index)}  (unchanged: {len(index) == len(text.split(chr(10)))})")
    print(f"index size      : {len(new_index.encode()):,} bytes  (was {len(text.encode()):,})")
    for cat in sorted(by_cat):
        size = len(files[out / f"{cat}.md"].encode("utf-8")) if args.apply else 0
        marker = f"{size:,}b" if args.apply else ""
        print(f"  {cat:<14} {len(by_cat[cat]):>3} rows  {marker}")

    if not args.apply:
        print("\n(dry run -- pass --apply to write)")
        return 0

    out.mkdir(parents=True, exist_ok=True)
    for path, body in files.items():
        path.write_text(body, encoding="utf-8")
    decisions.write_text(new_index, encoding="utf-8")
    print(f"\nwrote {len(files)} category files + rewrote {decisions.relative_to(root)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
