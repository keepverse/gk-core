#!/usr/bin/env python3
"""Audit the idea -> spec -> plan pipeline for every program.

A program moves through four document stages:

    docs/architecture/<p>-ideal.md          idea phase    (/idea)
    docs/architecture/<p>-map.md            capability map (/spec)
    docs/architecture/<p>/spec-*.md         module specs   (/spec)
    tasks/<p>-plan.md + tasks/<p>-todo.md   plan           (/plan)

This tool lists where the chain stops, so forgotten work surfaces:

    IDEAL-NO-SPEC   an ideal no map, spec or plan names
    IDEAL-CITED     an ideal with no program of its own; other programs' docs
                    only cite it (review: was it absorbed, or half-built?)
    MAP-NO-SPEC     a capability map with no spec-*.md next to it
    SPEC-NO-PLAN    a spec folder no plan or todo names
    MAP-PLAN-MISSING  a map names tasks/<x>-plan.md or -todo.md that does not
                    exist (catches folders another plan only mentions in passing)
    PLAN-NO-TODO    a plan with no todo pair

v2 (backlog-clean-up pipeline-audit-v2, 2026-09-20) adds four blind spots the first live audit hit,
each fixed by hand at the time:

    B1  status detection also reads a first-lines blockquote banner
        (`> ⛔ **SUPERSEDED ...**`), not only a `**Status:**` line.
    B2  TODO-HEADER-VS-BOXES: a todo's own header (first 15 lines) claims it is
        complete/done/closed, while unticked boxes remain.
    B3  ABSORBED-NO-POINTER (advisory): a different program ticked a module id
        this folder specs, and the owning program never cites it.
    B4  STALLED-TODO (advisory): open boxes remain and no commit named the
        program (by path or by commit-subject match) in the last N days
        (`--stale-days`, default 7); suppressed when a MERGED session record
        or a ledger already names the work done.

A link is either a name match (same <p> at the next stage) or a reference
(the next stage's text names the file or folder). Parent programs such as
summoner-convergence cover many sub-programs by reference, so name matching
alone would report false gaps.

Every finding also carries the document's own **Status** line and the date git
last touched it. A WITHDRAWN / SUPERSEDED / CLOSED status is a deliberate stop,
not drift, and is listed separately.

The tool only reads. It never pins a count: the number of programs is a
reading of the tree, not a contract. `absorbed-no-pointer` and `stalled-todo`
are heuristics (advisory): printed with `(advisory)`, and excluded from
`--fail-on-open` unless `--strict` is also passed.

Usage:
    python gk-core/scripts/audit-program-pipeline.py                 # markdown report
    python gk-core/scripts/audit-program-pipeline.py --only spec-no-plan
    python gk-core/scripts/audit-program-pipeline.py --json > out.json
    python gk-core/scripts/audit-program-pipeline.py --include-closed
    python gk-core/scripts/audit-program-pipeline.py --stale-days 14
    python gk-core/scripts/audit-program-pipeline.py --fail-on-open --strict
"""
from __future__ import annotations

import argparse
import datetime
import json
import re
import subprocess
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path

# Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/scripts/lib/keepverse_roots.py).
# `docs/`, `tasks/`, `scripts/` and `.github/` are all the WORKSPACE root, so this audit asks the shared
# resolver for it instead of walking `..` from this file's own directory — the same private-walk defect
# class `gk-core/scripts/guard-test-content-root.py` refuses in tests, and the `workspace_root(` token kvsplit's
# `rules/scan.v1.json` `resolvers` looks for.
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402  (the shim above runs first)

# RESOLVED WITH A NAMED REFUSAL INSTEAD OF AN IMPORT-TIME TRACEBACK.
#
# `docs/`, `tasks/` and `scripts/` are the WORKSPACE root's, and this audit reads all three. In a
# standalone gk-core clone none of them is here, so `workspace_root()` raises `RootNotFound` — and it
# used to raise it DURING IMPORT, so the tool died with a traceback and exit 1. Measured in an isolated
# clone at cd04ab6, against the sibling that gets this right:
#
#     audit-program-pipeline.py --check   -> keepverse_roots.RootNotFound traceback, exit 1
#     program_status.py                  -> PROGRAM-STATUS REFUSED [name]: <meaning>, exit 2
#
# The exit codes are IDENTICAL, which is the whole defect: a reader cannot tell "this audit found
# nothing" from "this audit could not run", and a CI step reading the code concludes the document chain
# is intact. The repository's own rule is a NAMED refusal, and this is now one.
#
# `REPO` is still bound at import because sixteen functions take it as a DEFAULT ARGUMENT, and Python
# evaluates those at definition time. It is bound to the workspace root when there is one, and to this
# repository's own directory when there is not — a value that is never read, because `main()` refuses
# before `audit()` is reached. That keeps every signature unchanged, which matters more here than the
# tidiness of a lazy default: an audit that could still run against the wrong root would be worse than
# one that refuses.
_ROOT_REFUSAL: str | None = None
try:
    REPO = workspace_root()
except RootNotFound as exc:
    REPO = Path(__file__).resolve().parent.parent
    _ROOT_REFUSAL = (
        f"{exc}. This audit's subject is the workspace root's docs/, tasks/ and scripts/ — the "
        f"idea -> spec -> plan -> todo document chain — and a standalone gk-core clone carries none "
        f"of them. Run it from the Keepverse workspace, or set KEEPVERSE_WORKSPACE_ROOT to the root "
        f"holding docs/ and tasks/.")

# Structural: how far into a document the status line may sit. Headers are
# short; a status buried past this is prose, not a header.
STATUS_SCAN_LINES = 25
STATUS_MAX_CHARS = 140
BANNER_SCAN_LINES = 10
HEADER_SCAN_LINES = 15
DEFAULT_STALE_DAYS = 7

STATUS_RE = re.compile(r"^\s*(?:>\s*)?\*\*Status[:*]*\s*(.*)$", re.IGNORECASE)

# One closed-status vocabulary, used by both the plain "**Status:**" reader and the
# blockquote-banner reader (B1) below — a status is closed no matter which shape said so.
CLOSED_STATUSES = ("WITHDRAWN", "SUPERSEDED", "CLOSED", "RETIRED", "ABANDONED", "REJECTED", "OBSOLETE")
CLOSED_RE = re.compile(r"\b(" + "|".join(CLOSED_STATUSES) + r")\b", re.IGNORECASE)
# B1: a first-lines blockquote banner, e.g. "> ⛔ **SUPERSEDED 2026-09-06.**" — no "Status:"
# keyword at all, which is exactly what v1's STATUS_RE missed. The status word may be preceded by a
# marker inside the bold run (`> **⛔ CLOSED — SUPERSEDED, 2026-09-03.**`), which the first version of
# this regex missed — RECON-F7's `tasks/loam-todo.md:1361` phase banner is exactly that shape.
BANNER_RE = re.compile(r"^\s*>.*\*\*[^\w*]*(" + "|".join(CLOSED_STATUSES) + r")\b[^*]*\*\*", re.IGNORECASE)

# Files that match spec-*.md but are not module specs.
NOT_A_SPEC = re.compile(r"^spec-rulings-")

# B2: a todo header claiming completion. A line is excluded when it also carries a negation cue
# ("No task is done until its verification command is green" is a per-task RULE, not a whole-todo
# completion claim) — found live: it made the naive form of this pattern fire on almost every todo.
HEADER_COMPLETE_RE = re.compile(r"\b(complete|done|closed)\b|all\s+.*\bgated\s+pass\b", re.IGNORECASE)
HEADER_NEGATION_RE = re.compile(r"\b(no|not|never|until|before|isn.t|hasn.t|doesn.t|if only)\b", re.IGNORECASE)
# paperwork-reconcile rule 4's own remediation for this exact finding: once a todo carries this
# banner, the header/box mismatch is explained on purpose and stops being a finding.
RECONCILED_BANNER_RE = re.compile(r"closed by the header above", re.IGNORECASE)
BOX_ANY_RE = re.compile(r"^\s*-\s*\[[ xX]\]")
BOX_CHECKED_HEADING_RE = re.compile(r"^#{2,6}\s+-\s*\[[xX]\]")
BOX_OPEN_RE = re.compile(r"^\s*-\s*\[\s\]")
# Step 3 of the block rule is about a COLUMN-0 open box (a task row); an indented `- [ ]` is an
# acceptance box, which is the thing a ticked block leaves unticked and the metric shades.
BOX_OPEN_COL0_RE = re.compile(r"^- \[ \]")
BOX_ID_RE = re.compile(r"\*\*([A-Za-z][\w.]*)\b")

# B4: paths a task line names explicitly, e.g. "Files: `a.cs`, `b.cs`".
FILES_LINE_RE = re.compile(r"files:\s*(.+)$", re.IGNORECASE)
BACKTICK_RE = re.compile(r"`([^`]+)`")

KINDS = (
    "ideal-no-spec",
    "ideal-cited",
    "map-no-spec",
    "spec-no-plan",
    "map-plan-missing",
    "plan-no-todo",
    "todo-header-vs-boxes",
    "todo-task-blocks",
    "absorbed-no-pointer",
    "stalled-todo",
)
ADVISORY_KINDS = ("absorbed-no-pointer", "stalled-todo")


@dataclass
class Finding:
    kind: str
    program: str
    path: str
    status: str
    last_touched: str
    closed: bool
    evidence: list[str] = field(default_factory=list)


def rel(p: Path, repo: Path = REPO) -> str:
    return p.relative_to(repo).as_posix()


def read(p: Path) -> str:
    try:
        return p.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def status_of(p: Path) -> str:
    lines = read(p).splitlines()
    for line in lines[:STATUS_SCAN_LINES]:
        m = STATUS_RE.match(line)
        if m:
            text = m.group(1).replace("**", "").strip()
            return text[:STATUS_MAX_CHARS] + ("..." if len(text) > STATUS_MAX_CHARS else "")
    for line in lines[:BANNER_SCAN_LINES]:
        m = BANNER_RE.match(line)
        if m:
            text = line.strip()
            if text.startswith(">"):
                text = text[1:].strip()
            text = text.replace("**", "").strip()
            return text[:STATUS_MAX_CHARS] + ("..." if len(text) > STATUS_MAX_CHARS else "")
    return ""


def git_history(repo: Path = REPO) -> tuple[dict[str, str], list[tuple[str, str]]]:
    """One git log pass for the whole audit: newest commit date per path, and (date, subject) per
    commit (newest first). Every other check reuses this instead of spawning its own `git log`."""
    try:
        out = subprocess.run(
            ["git", "log", "--format=%x01%cs%x02%s", "--name-only"],
            cwd=repo, capture_output=True, text=True, encoding="utf-8", check=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        return {}, []
    newest: dict[str, str] = {}
    commits: list[tuple[str, str]] = []
    date = ""
    for line in out.splitlines():
        if line.startswith("\x01"):
            rest = line[1:]
            date, _, subject = rest.partition("\x02")
            commits.append((date, subject))
        elif line and line not in newest:
            newest[line] = date  # log is newest-first
    return newest, commits


def resolve_touched(paths: list[str], newest: dict[str, str]) -> dict[str, str]:
    """v1's per-path resolution (exact match, else newest under a folder prefix), now fed by the
    single git_history() pass instead of running its own `git log`."""
    result: dict[str, str] = {}
    for p in paths:
        if p in newest:
            result[p] = newest[p]
            continue
        prefix = p.rstrip("/") + "/"
        dates = [d for f, d in newest.items() if f.startswith(prefix)]
        result[p] = max(dates) if dates else "uncommitted"
    return result


def program_of(p: Path, suffix: str) -> str:
    return p.name[: -len(suffix)]


def collect(repo: Path = REPO):
    arch = repo / "docs" / "architecture"
    design = repo / "docs" / "design"
    tasks_dir = repo / "tasks"
    ideals = sorted(p for p in arch.rglob("*-ideal.md"))
    maps = sorted(p for p in arch.rglob("*.md") if p.name.endswith(("-map.md", "-program.md")))
    spec_files = [
        p for root in (arch, design) for p in root.rglob("spec-*.md") if not NOT_A_SPEC.match(p.name)
    ]
    spec_dirs: dict[Path, list[Path]] = {}
    for p in spec_files:
        spec_dirs.setdefault(p.parent, []).append(p)
    plans = sorted(tasks_dir.glob("*-plan.md"))
    todos = sorted(tasks_dir.glob("*-todo.md"))
    return ideals, maps, spec_dirs, plans, todos


def referencing(needles: list[str], corpus: dict[Path, str], repo: Path = REPO, exclude: Path | None = None) -> list[str]:
    hits = []
    for path, text in corpus.items():
        if path == exclude:
            continue
        if any(n in text for n in needles):
            hits.append(rel(path, repo))
    return sorted(hits)


def header_claims_complete(text: str) -> bool:
    if reconciled_banner_present(text):
        return False
    head = text.splitlines()[:HEADER_SCAN_LINES]
    return any(HEADER_COMPLETE_RE.search(line) and not HEADER_NEGATION_RE.search(line) for line in head)


# ---- TVB-F20: the block counter -------------------------------------------------------------------
# The shapes `tasks/reports/backlog-reconciliation-20260921.md` §1 observed, and the marker each one
# uses. A file's shape is declared in `gk-core/scripts/todo-shapes.v1.json`; a file absent from that map, or
# declared `none`, is reported UNMEASURED — never defaulted to zero (RECON-F8).
TODO_SHAPE_MARKERS = {
    "H-task": re.compile(r"^(?P<level>#{2,6})\s+.*\bTask\b\s+\S"),
    "H-id": re.compile(r"^(?P<level>#{2,6})\s+[A-Za-z][\w.-]*\d[\w.-]*\s*[:.\u2014\u2013-]"),
    "H-bracket": re.compile(r"^(?P<level>#{2,6}) \[[ xX]\]"),
    "H-checkbox-heading": re.compile(r"^(?P<level>#{2,6}) - \[[ xX]\]"),
    "R-bold": re.compile(r"^- \[[ xX]\] \*\*"),
    "R-plain": re.compile(r"^- \[[ xX]\] "),
}
HEADING_SHAPES = ("H-task", "H-id", "H-bracket", "H-checkbox-heading")
# Step 1 of the block rule: a positive marker or a negative declaration in the heading.
BLOCK_MARKER_RE = re.compile(
    r"✅|⭐|🔶|\b(DONE|CLOSED|BUILT|SHIPPED|EXCLUDED|SUPERSEDED|BLOCKED|GENUINELY|OWNER-ONLY)\b")
# A heading that declares ITSELF open beats any file-level banner: `achievement-title-todo.md`'s header
# closes Tasks 1-7a while four `## Task 7b-… (OPEN)` headings say in their own words that they do not.
BLOCK_DECLARED_OPEN_RE = re.compile(r"\(\s*OPEN\s*\)|\bOPEN\b\s*$", re.IGNORECASE)
# Step 2: a closure phrase in the BODY. Load-bearing ordering — checking the boxes first moved 68
# blocks in the report's own run (858 -> 790) and reported `battle-derived-wire` as 19 open, not 14.
BLOCK_CLOSURE_RE = re.compile(
    r"closed\s+(?:\d{4}-\d{2}-\d{2}\s+)?by pointer|\*\*Done\s+\d{4}-\d{2}-\d{2}|\bCLOSED\s+\d{4}-\d{2}-\d{2}",
    re.IGNORECASE)
# RECON-F6: `tasks/species-gear-chain-todo.md:16-17` declares the same thing in words — "a ticked
# task's acceptance boxes are its original contract" — with no banner phrase, so the old regex missed
# it and 240 unticked lines read as open work where the true count is 6 blocks.
CONTRACT_CLAUSE_RE = re.compile(r"acceptance boxes are its original contract", re.IGNORECASE)


def reconciled_banner_present(text: str) -> bool:
    """A declaration ANYWHERE in the document that its unticked boxes are not queued work.

    RECON-F7: `header_claims_complete` used to look only at the first `HEADER_SCAN_LINES`, so
    `tasks/loam-todo.md`'s phase banner at `:1361` was invisible and the file read as a false defect.
    Only these two explicit declaration shapes are honoured — never a general completion word, which
    would fire on ordinary prose.
    """
    for line in text.splitlines():
        m = RECONCILED_BANNER_RE.search(line)
        # A document that DESCRIBES the phrase (inside a code span — `species-gear-chain-todo.md:3234`
        # explains the metric and quotes the regex) is not using it as a banner. Measured: without this
        # the phrase closed all 66 of that file's blocks where six rows are genuinely open.
        if m and "`" not in line[: m.start()]:
            return True
        if CONTRACT_CLAUSE_RE.search(line) or BANNER_RE.match(line):
            return True
    return False


def declaration_lines(text: str) -> list[int]:
    """Line indices of a CLOSED-status banner (`> **⛔ CLOSED …**`), which closes the blocks that follow it.

    A banner names its own phase and declares that phase dead, so it closes what comes AFTER it — the
    report's §4.3 "phase-level closing banner". Measured both ways: `tasks/loam-todo.md:1361` sits
    above the L44-L50 tasks it kills, and `tasks/species-gear-chain-todo.md:3234` sits at the END, so
    closing everything before it would swallow that file's six genuinely-open rows.

    `CONTRACT_CLAUSE_RE` is deliberately NOT here: it says "a *ticked* task's acceptance boxes are its
    original contract", so it reconciles the boxes of a ticked block (shading, and the header-vs-boxes
    finding) and never closes an unticked one.
    """
    out = []
    for i, line in enumerate(text.splitlines()):
        if BANNER_RE.match(line):
            out.append(i)
    return out


# A block's own text saying it is waiting on something. This is a SIGNAL the reader prints, never a
# state: a block that says "blocked" may be blocked, deferred, superseded or owner-only, and only the
# owner decides which. Kept as a closed phrase list so the signal cannot drift into prose matching.
BLOCKED_SIGNAL_RE = re.compile(
    r"⛔|\bBLOCKED\b|\bBLOCKER\b|\bOWNER[- ]ONLY\b|\bNEEDS? AN? RULING\b|\bAWAITING\b|\bGATED ON\b"
    r"|\bPAUSED\b|\bNOT MEASURED\b",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class TaskBlock:
    """One task block, as the block rule read it.

    `done` is the rule's verdict and nothing else. `signals` are phrases the block itself carries
    (`⛔`, `OWNER-ONLY`, `PAUSED`, …) and are printed as evidence for a human, never counted as a
    second state — the report's §3 rule has exactly two states, and a third one invented here is how
    two incompatible backlogs ship at once.
    """

    index: int
    line: int
    head: str
    body: str
    done: bool
    reconciled: bool
    declared_open: bool
    unticked_boxes: int
    shaded_boxes: int
    task_id: str
    title: str
    signals: tuple[str, ...]

    @property
    def label(self) -> str:
        return self.task_id or self.title or f"line {self.line}"


# A task id, by this repo's own grammar (the `H-id` shape marker above): a word that carries a digit
# (`TVB5.9`, `ADG-F4`, `T19b`, `BCL-ledger-43`) or a bare number (`21.3`, `4`). A heading with no digit
# (`## Task 5 — prose`, `**Owner live proof**`, `**PAUSED**`) is NOT an id: the first bold token in a
# heading is frequently an English word, and a label grammar that returns `needs` or `PAUSED` for a
# task row makes the whole report unreadable. Measured: the first `**token**` heuristic labelled 27%
# of the open blocks on this tree with a word.
TASK_ID_RE = re.compile(
    r"^(?P<id>[A-Za-z][\w]*(?:[.-][\w]+)*\d[\w.-]*|\d+[A-Za-z]*(?:\.[\w]+)*)"
    r"(?:\s*[.:·—–-]|\s+|$)")


def block_identity(head: str) -> tuple[str, str]:
    """`(task_id, title)` for a block heading or row.

    `- [ ] **EPL1.1 — the closed enum**` -> `("EPL1.1", "the closed enum")`. The id must come from the
    front of the row, not from the first bold run: a bold run is emphasis, not identity. A block with no
    id-shaped token keeps an empty id and is labelled by its title, so nothing is invented.
    """
    text = re.sub(r"^[#\s]*", "", head)
    text = re.sub(r"^-\s*\[[ xX]\]\s*", "", text)   # row shape: `- [ ] **id** …`
    text = re.sub(r"^\[[ xX]\]\s*", "", text)        # bracket-heading shape: `### [ ] id …`
    text = text.lstrip("*_")  # `**EPL1.1 …` — emphasis leads the row, the id still leads the text
    match = TASK_ID_RE.match(text)
    if match:
        return match.group("id").rstrip(".-"), text[match.end():]
    return "", text.replace("**", " ")


def block_title(text: str, limit: int = 90) -> str:
    """A readable one-line title: emphasis and code ticks removed, whitespace collapsed, length capped.

    A capped title is a display string, never a count — the id is the identity.
    """
    plain = re.sub(r"`([^`]*)`", r"\1", text)
    plain = re.sub(r"[*_]+", " ", plain)
    plain = re.sub(r"\s+", " ", plain).strip(" —–-:·|")
    return plain if len(plain) <= limit else plain[: limit - 1].rstrip() + "…"


def iter_task_blocks(text: str, shape: str) -> list[TaskBlock]:
    """Every task block in one file, with the block rule's verdict attached.

    This is the single implementation of the rule; `task_blocks` is the counting view over it and
    `gk-core/scripts/program_status.py` is the per-block view. An undeclared shape yields no blocks, which is
    how a file the marker map does not cover stays unmeasured instead of being counted as zero.
    """
    lines = text.splitlines()
    marker = TODO_SHAPE_MARKERS.get(shape)
    if marker is None:
        return []
    declarations = declaration_lines(text)
    header_reconciled = any(
        RECONCILED_BANNER_RE.search(line) and "`" not in line[: RECONCILED_BANNER_RE.search(line).start()]
        for line in lines)

    spans: list[tuple[int, list[str]]] = []
    if shape in HEADING_SHAPES:
        level = 0
        for i, line in enumerate(lines):
            m = marker.match(line)
            if m:
                level = len(m.group("level"))
                spans.append((i, [line]))
            elif level and re.match(r"^#{1,6}\s", line) and len(re.match(r"^(#+)", line).group(1)) <= level:
                level = 0  # the block ended at a same-or-higher-level heading
            elif level:
                spans[-1][1].append(line)
    else:
        for i, line in enumerate(lines):
            if marker.match(line):
                spans.append((i, [line]))
            elif spans:
                spans[-1][1].append(line)

    out: list[TaskBlock] = []
    for index, (start, block) in enumerate(spans):
        head, body = block[0], "\n".join(block[1:])
        # A banner closes the blocks that START AFTER it (it names its own phase); a banner at the
        # file end therefore closes nothing before it — the species-gear-chain shape.
        reconciled = header_reconciled or any(i < start for i in declarations)
        declared_open = bool(BLOCK_DECLARED_OPEN_RE.search(head))
        if shape in HEADING_SHAPES:
            if declared_open:
                done = False  # the heading says so itself; a file-level banner cannot override that
            elif shape == "H-checkbox-heading" and BOX_CHECKED_HEADING_RE.match(head):
                done = True  # the heading's own checkbox is the closed state for this shape
            elif BLOCK_MARKER_RE.search(head) or reconciled:
                done = True
            elif BLOCK_CLOSURE_RE.search(body):
                done = True  # step 2, BEFORE the boxes
            elif any(BOX_OPEN_COL0_RE.match(line) for line in block):
                done = False  # step 3
            elif any(BOX_ANY_RE.match(line) for line in block):
                done = True  # step 4
            else:
                done = False  # step 5, prose-only
        else:
            done = not BOX_OPEN_COL0_RE.match(head)
        task_id, raw_title = block_identity(head)
        out.append(TaskBlock(
            index=index,
            line=start + 1,
            head=head,
            body=body,
            done=done,
            reconciled=reconciled,
            declared_open=declared_open,
            unticked_boxes=sum(1 for line in block if BOX_OPEN_RE.match(line)),
            shaded_boxes=sum(1 for line in block if BOX_OPEN_RE.match(line)) if done else 0,
            task_id=task_id,
            title=block_title(raw_title),
            signals=tuple(sorted({m.group(0).upper() for m in BLOCKED_SIGNAL_RE.finditer(head + "\n" + body)})),
        ))
    return out


def task_blocks(text: str, shape: str) -> tuple[int, int, int, int]:
    """`(open_blocks, done_blocks, unticked_lines, shaded)` for one file, by the report's block rule.

    `shaded` is the unticked lines that sit inside a block the file itself declares done — the
    "boxes are the original contract" footprint. It is a reading, never a work count.

    A reconciliation declaration closes the blocks it FOLLOWS (the report's `achievement-title` banner
    covers Tasks 1-7a only, so it sits after them); a declaration inside the file's own header closes
    every block. A heading that declares itself `(OPEN)` is never closed by one. The report's per-file
    hand overrides (§3's notes column) are deliberately NOT implemented — this is the rule alone, which
    is why a count here can differ from the prototype's.
    """
    lines = text.splitlines()
    if TODO_SHAPE_MARKERS.get(shape) is None:
        return (0, 0, 0, 0)
    blocks = iter_task_blocks(text, shape)
    unticked = sum(1 for line in lines if BOX_OPEN_RE.match(line))
    open_blocks = sum(1 for b in blocks if not b.done)
    done_blocks = sum(1 for b in blocks if b.done)
    return (open_blocks, done_blocks, unticked, sum(b.shaded_boxes for b in blocks))


def load_todo_shapes(repo: Path = REPO) -> dict[str, dict]:
    path = repo / "scripts" / "todo-shapes.v1.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def box_lines(text: str) -> list[tuple[bool, str]]:
    """(is_open, task_id_or_empty) for every checkbox line in the document."""
    out = []
    for line in text.splitlines():
        if not BOX_ANY_RE.match(line):
            continue
        is_open = bool(BOX_OPEN_RE.match(line))
        m = BOX_ID_RE.search(line)
        out.append((is_open, m.group(1) if m else ""))
    return out


def files_named_in(text: str) -> list[str]:
    out: list[str] = []
    for line in text.splitlines():
        m = FILES_LINE_RE.search(line)
        if not m:
            continue
        out.extend(BACKTICK_RE.findall(m.group(1)))
    return out


def merged_session_names(repo: Path = REPO) -> set[str]:
    """Every string a MERGED session record names: its program id and every path it claims. A
    stalled-todo whose program appears here already has a session that finished and merged; the
    todo just was not re-ticked."""
    names: set[str] = set()
    sessions_dir = repo / "tasks" / "sessions"
    if not sessions_dir.is_dir():
        return names
    for f in sessions_dir.glob("*.json"):
        if f.name.startswith("_"):
            continue
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if data.get("status") != "merged":
            continue
        prog = data.get("program")
        if isinstance(prog, str) and prog:
            names.add(prog)
        for p in data.get("paths") or []:
            if isinstance(p, str):
                names.add(p)
    return names


def ledger_done_ids(repo: Path = REPO) -> set[str]:
    """Every task id any *-ledger.jsonl records as done. A ledger can be ahead of its todo's
    checkboxes; that is drift in the checkboxes, not a stall."""
    ids: set[str] = set()
    tasks_dir = repo / "tasks"
    if not tasks_dir.is_dir():
        return ids
    for f in tasks_dir.glob("*-ledger.jsonl"):
        for line in read(f).splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("kind") == "task" and rec.get("state") == "done" and rec.get("id"):
                ids.add(str(rec["id"]))
    return ids


def session_covers(prog: str, session_names: set[str]) -> bool:
    if prog in session_names:
        return True
    return any(prog in name for name in session_names if len(prog) >= 3)


def audit(repo: Path = REPO, stale_days: int = DEFAULT_STALE_DAYS, today: datetime.date | None = None) -> list[Finding]:
    ideals, maps, spec_dirs, plans, todos = collect(repo)
    today = today or datetime.date.today()

    map_by_program = {program_of(m, "-map.md" if m.name.endswith("-map.md") else "-program.md"): m for m in maps}
    spec_dir_names = {d.name for d in spec_dirs}
    plan_names = {program_of(p, "-plan.md") for p in plans}
    todo_names = {program_of(t, "-todo.md") for t in todos}

    spec_corpus = {p: read(p) for files in spec_dirs.values() for p in files}
    map_corpus = {m: read(m) for m in maps}
    plan_corpus = {p: read(p) for p in plans + todos}
    downstream_of_ideal = {**map_corpus, **spec_corpus, **plan_corpus}

    raw: list[tuple[str, str, Path, list[str]]] = []

    # IDEAL-NO-SPEC: no same-name map/spec folder/plan, and nothing downstream names the file.
    for ideal in ideals:
        prog = program_of(ideal, "-ideal.md")
        if prog in map_by_program or prog in spec_dir_names or prog in plan_names:
            continue
        refs = referencing([ideal.name], downstream_of_ideal, repo, exclude=ideal)
        if not refs:
            raw.append(("ideal-no-spec", prog, ideal, []))
        else:
            raw.append(("ideal-cited", prog, ideal, [f"cited by {len(refs)}: " + ", ".join(refs[:3])
                                                     + (", ..." if len(refs) > 3 else "")]))

    # MAP-NO-SPEC: a map whose sibling folder holds no spec files.
    for prog, m in sorted(map_by_program.items()):
        folder = m.parent / prog
        if folder in spec_dirs:
            continue
        # A top-level parent map (trade-network) delegates to nested sub-maps.
        nested = [s for s in maps if s.parent == folder]
        if nested:
            continue
        # An umbrella map may point at specs that live in another program's folder.
        if re.search(r"spec-[\w.-]+\.md", map_corpus[m]):
            continue
        raw.append(("map-no-spec", prog, m, []))

    # SPEC-NO-PLAN: no same-name plan, and no plan/todo names the folder or its map.
    for folder, files in sorted(spec_dirs.items()):
        prog = folder.name if folder not in (repo / "docs" / "architecture", repo / "docs" / "design") else rel(folder, repo)
        if prog in plan_names:
            continue
        folder_rel = rel(folder, repo)
        needles = [folder_rel + "/", folder_rel.replace("docs/", "", 1) + "/", f"{folder.name}-map.md"]
        needles += [f.name for f in files]
        refs = referencing(needles, plan_corpus, repo)
        if not refs:
            raw.append(("spec-no-plan", prog, folder, [f"{len(files)} spec file(s)"]))

    # MAP-PLAN-MISSING: the map promises a plan/todo path that was never written.
    for prog, m in sorted(map_by_program.items()):
        promised = sorted(set(re.findall(r"tasks/[\w.-]+-(?:plan|todo)\.md", map_corpus[m])))
        missing = [p for p in promised if not (repo / p).exists()]
        if missing:
            raw.append(("map-plan-missing", prog, m, ["missing: " + ", ".join(missing)]))

    # PLAN-NO-TODO
    for p in plans:
        prog = program_of(p, "-plan.md")
        if prog not in todo_names:
            raw.append(("plan-no-todo", prog, p, []))

    # ---- v2 B2: todo-header-vs-boxes ----
    todo_boxes: dict[Path, list[tuple[bool, str]]] = {}
    for t in todos:
        text = plan_corpus[t]
        boxes = box_lines(text)
        todo_boxes[t] = boxes
        open_count = sum(1 for is_open, _ in boxes if is_open)
        if header_claims_complete(text) and open_count > 0:
            prog = program_of(t, "-todo.md")
            raw.append(("todo-header-vs-boxes", prog, t, [f"{open_count} open box(es) under a header claiming completion"]))

    # ---- TVB-F20: todo-task-blocks — the replacement metric ----------------------------------------
    # One finding per todo: the open/done BLOCK counts (the metric), the unticked-line count beside
    # them (explicitly NOT a work count) and the shaded count. A file whose shape is not declared is
    # reported unmeasured rather than defaulted to zero (RECON-F8).
    shapes = load_todo_shapes(repo)
    for t in todos:
        declared = shapes.get(rel(t, repo))
        prog = program_of(t, "-todo.md")
        if not declared or declared.get("shape") in (None, "", "none"):
            raw.append(("todo-task-blocks", prog, t, [
                "unmeasured: no shape declared in scripts/todo-shapes.v1.json "
                "(this file has no reliable task marker, so no count is invented)",
            ]))
            continue
        shape = declared["shape"]
        opened, done, unticked, shaded = task_blocks(plan_corpus[t], shape)
        raw.append(("todo-task-blocks", prog, t, [
            f"shape={shape} open={opened} done={done} boxes={unticked} (not a work count) shaded={shaded}",
            "reads remaining ROWS, not remaining effort: a `L-run` and an `XS` block count the same; "
            "a tick is taken as true; an open block may be deferred, blocked or owner-only",
        ]))

    # ---- v2 B4: stalled-todo (advisory), suppressed per B2's session/ledger rule ----
    newest, commits = git_history(repo)
    session_names = merged_session_names(repo)
    done_ids = ledger_done_ids(repo)
    for t in todos:
        boxes = todo_boxes[t]
        open_ids = [tid for is_open, tid in boxes if is_open]
        if not open_ids:
            continue
        prog = program_of(t, "-todo.md")
        if session_covers(prog, session_names):
            continue
        if open_ids and all(tid and tid in done_ids for tid in open_ids):
            continue
        if RECONCILED_BANNER_RE.search(plan_corpus[t]):
            continue
        explicit_paths = files_named_in(plan_corpus[t]) + [rel(t, repo)]
        touched = resolve_touched(explicit_paths, newest)
        dates = [d for d in touched.values() if d and d != "uncommitted"]
        for date, subject in commits:
            if prog and len(prog) >= 3 and prog in subject.lower():
                dates.append(date)
                break  # commits is newest-first: first hit is the newest
        last = max(dates) if dates else None
        stale = last is None
        if last is not None:
            try:
                stale = (today - datetime.date.fromisoformat(last)).days >= stale_days
            except ValueError:
                stale = False
        if stale:
            note = f"{len(open_ids)} open box(es), " + (f"last activity {last}" if last else "no activity found")
            raw.append(("stalled-todo", prog, t, [note]))

    # ---- v2 B3: absorbed-no-pointer (advisory) ----
    checked_lines: list[tuple[Path, str]] = []
    for p, text in plan_corpus.items():
        for line in text.splitlines():
            if re.search(r"\[[xX]\]", line):
                checked_lines.append((p, line))
    for folder, files in sorted(spec_dirs.items()):
        owner_prog = folder.name
        owner_texts = []
        if owner_prog in map_by_program:
            owner_texts.append(map_corpus[map_by_program[owner_prog]])
        owner_todo = repo / "tasks" / f"{owner_prog}-todo.md"
        if owner_todo in plan_corpus:
            owner_texts.append(plan_corpus[owner_todo])
        owner_plan = repo / "tasks" / f"{owner_prog}-plan.md"
        if owner_plan in plan_corpus:
            owner_texts.append(plan_corpus[owner_plan])
        owner_corpus_text = "\n".join(owner_texts)
        for spec_file in files:
            stem = spec_file.stem
            module_id = stem[len("spec-"):] if stem.startswith("spec-") else stem
            if not module_id:
                continue
            # A bare short/common word (e.g. "budget", "action") collides with unrelated prose in
            # hundreds of unrelated checked lines. Require either a hyphenated compound id (this
            # repo's real absorbed-module ids: corpse-cache, cache-decay-void, ...) as a whole word,
            # or an explicit backtick/code-span citation of the exact id, so a generic single word
            # cannot match on its own.
            seen_other_progs: set[str] = set()
            id_re = (re.compile(r"`" + re.escape(module_id) + r"`|\b" + re.escape(module_id) + r"\b")
                     if "-" in module_id else re.compile(r"`" + re.escape(module_id) + r"`"))
            for other_path, line in checked_lines:
                if not id_re.search(line):
                    continue
                other_prog = (program_of(other_path, "-plan.md") if other_path.name.endswith("-plan.md")
                              else program_of(other_path, "-todo.md"))
                if other_prog == owner_prog or other_prog in seen_other_progs:
                    continue
                if other_prog in owner_corpus_text:
                    continue
                seen_other_progs.add(other_prog)
                raw.append((
                    "absorbed-no-pointer", owner_prog, spec_file,
                    [f"module '{module_id}' ticked done by {other_prog} ({rel(other_path, repo)}); {owner_prog} never cites it"],
                ))

    touched = resolve_touched([rel(path, repo) for _, _, path, _ in raw], newest)
    findings = []
    for kind, prog, path, evidence in raw:
        status = status_of(path) if path.is_file() else _folder_status(path)
        findings.append(Finding(
            kind=kind,
            program=prog,
            path=rel(path, repo),
            status=status,
            last_touched=touched.get(rel(path, repo), "?"),
            closed=bool(CLOSED_RE.search(status)),
            evidence=evidence,
        ))
    return findings


def _folder_status(folder: Path) -> str:
    """A spec folder's status: its map's status line, else the first spec's."""
    m = folder.parent / f"{folder.name}-map.md"
    if m.is_file() and (s := status_of(m)):
        return s
    for spec in sorted(folder.glob("spec-*.md")):
        if s := status_of(spec):
            return s
    return ""


TITLES = {
    "ideal-no-spec": "Ideal with no spec (no map, spec folder or plan names it)",
    "ideal-cited": "Ideal with no program of its own, only cited by other docs (review)",
    "map-no-spec": "Capability map with no spec files and no spec links",
    "spec-no-plan": "Spec folder with no plan (no plan or todo names it)",
    "map-plan-missing": "Map names a plan/todo file that does not exist",
    "plan-no-todo": "Plan with no todo",
    "todo-header-vs-boxes": "Todo header claims completion, boxes stay unticked",
    "todo-task-blocks": "Open/done TASK BLOCKS per todo (the metric; boxes are not a work count)",
    "absorbed-no-pointer": "Another program ticked this module, owner never cites it",
    "stalled-todo": "Open boxes, no commit or session activity found",
}


def render_markdown(findings: list[Finding], include_closed: bool, kinds: tuple[str, ...] = KINDS) -> str:
    out = ["# Program pipeline audit", ""]
    for kind in kinds:
        rows = [f for f in findings if f.kind == kind]
        open_rows = sorted((f for f in rows if not f.closed), key=lambda f: f.last_touched)
        closed_rows = [f for f in rows if f.closed]
        advisory = " (advisory)" if kind in ADVISORY_KINDS else ""
        if kind == "todo-task-blocks":
            out.append(f"## {TITLES[kind]}{advisory}: {len(rows)} todo file(s)")
            out.append("")
            out.append("| Program | shape | open blocks | done blocks | unticked `- [ ]` | shaded |")
            out.append("|---|---|---:|---:|---:|---:|")
            totals = {"open": 0, "done": 0, "boxes": 0, "shaded": 0}
            unmeasured = 0
            for f in sorted(rows, key=lambda f: f.path):
                reading = f.evidence[0] if f.evidence else ""
                m = re.match(
                    r"shape=(\S+) open=(\d+) done=(\d+) boxes=(\d+) \(not a work count\) shaded=(\d+)", reading)
                if not m:
                    unmeasured += 1
                    out.append(f"| {f.program} | — | — | — | — | — |")
                    continue
                shape, opened, done, boxes, shaded = m.group(1), *map(int, m.groups()[1:])
                totals["open"] += opened
                totals["done"] += done
                totals["boxes"] += boxes
                totals["shaded"] += shaded
                out.append(f"| {f.program} | {shape} | {opened} | {done} | {boxes} | {shaded} |")
            out.append("")
            out.append(
                f"**TOTAL open={totals['open']} done={totals['done']} "
                f"boxes={totals['boxes']} (not a work count) shaded={totals['shaded']}"
                + (f" · unmeasured={unmeasured} file(s)" if unmeasured else "")
                + "**"
            )
            out.append("")
            out.append(
                "What this cannot prove: that a tick is true; that an open block is unfinished rather "
                "than deferred, blocked, superseded or owner-only; that the marker map is complete "
                "(a file with no declared shape stays unmeasured by design); that no work exists outside "
                "the todo; and any SIZE — a `L-run` and an `XS` block count the same, so this measures "
                "remaining rows, not remaining effort."
            )
            out.append("")
            out.append(
                "This is the report's §1 block rule ALONE: `tasks/reports/backlog-reconciliation-20260921.md`"
                " §3 applied per-file hand overrides on top of it (its notes column names them), and those"
                " are deliberately not implemented, so a count here can differ from that prototype's."
            )
            out.append("")
            continue
        out.append(f"## {TITLES[kind]}{advisory}: {len(open_rows)} open, {len(closed_rows)} closed")
        out.append("")
        shown = open_rows + (closed_rows if include_closed else [])
        if not shown:
            out.append("_none_")
            out.append("")
            continue
        out.append("| Program | Last touched | Path | Status |")
        out.append("|---|---|---|---|")
        for f in shown:
            status = (f.status or "-").replace("|", "\\|")
            tag = " (closed)" if f.closed else ""
            extra = f" ({', '.join(f.evidence)})" if f.evidence else ""
            out.append(f"| {f.program}{tag} | {f.last_touched} | `{f.path}`{extra} | {status} |")
        out.append("")
    if not include_closed:
        out.append("_Closed rows (WITHDRAWN/SUPERSEDED/...) hidden; pass `--include-closed` to list them._")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--json", action="store_true", help="emit findings as JSON")
    ap.add_argument("--only", choices=KINDS, action="append", help="limit to one or more finding kinds")
    ap.add_argument("--include-closed", action="store_true", help="also list withdrawn/superseded documents")
    ap.add_argument("--fail-on-open", action="store_true", help="exit 1 when any open, non-advisory finding remains")
    ap.add_argument("--strict", action="store_true", help="with --fail-on-open, also fail on open advisory findings")
    ap.add_argument("--stale-days", type=int, default=DEFAULT_STALE_DAYS,
                    help=f"stalled-todo threshold in days (default {DEFAULT_STALE_DAYS})")
    ap.add_argument("--root", type=Path, default=REPO, help=argparse.SUPPRESS)
    args = ap.parse_args()

    # The refusal is honoured UNLESS the caller named a root explicitly. `--root` exists precisely so a
    # caller can point this at a checkout the resolver cannot see, and refusing after being handed a
    # root would make that flag useless — while honouring it is what keeps the exit code honest.
    if _ROOT_REFUSAL is not None and args.root == REPO:
        print("AUDIT-PROGRAM-PIPELINE REFUSED: ROOT-NOT-FOUND", file=sys.stderr)
        print(f"  {_ROOT_REFUSAL}", file=sys.stderr)
        return 2

    findings = audit(repo=args.root, stale_days=args.stale_days)
    if args.only:
        findings = [f for f in findings if f.kind in args.only]

    if args.json:
        shown = findings if args.include_closed else [f for f in findings if not f.closed]
        print(json.dumps([asdict(f) for f in shown], indent=2))
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(render_markdown(findings, args.include_closed, tuple(args.only or KINDS)))

    if args.fail_on_open:
        blocking = findings if args.strict else [f for f in findings if f.kind not in ADVISORY_KINDS]
        if any(not f.closed for f in blocking):
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
