#!/usr/bin/env python3
"""Answer "what is done, what is left" for every program, from the tree itself.

This is the tool AGENTS.md makes every agent read before reporting status. It answers three
questions and refuses to answer a fourth:

  1. What is DONE and what is LEFT, measured in **task blocks** — never in `- [ ]` lines
     (AGENTS.md: "An unchecked `- [ ]` line is not a unit of work"; reading:
     `tasks/reports/backlog-reconciliation-20260921.md` §3).
  2. WHO is on it — which active session record fences which program's files, and which
     acceptance artefacts name it.
  3. WHAT IS UNPROVEN — programs the marker map cannot measure, sessions with no closure,
     and acceptance verdicts that are not `GREEN`.
  4. It refuses to answer "is this program finished?" A tick is a claim, not evidence, and this
     tool has no way to prove one. It prints what the documents say plus who holds the fence.

**The block rule is not reimplemented here.** `gk-core/scripts/audit-program-pipeline.py` owns it
(`iter_task_blocks` / `task_blocks`, markers declared per file in `gk-core/scripts/todo-shapes.v1.json`),
because two counters over one corpus is how two incompatible backlogs ship. This tool consumes
that rule per block, so it can name *which* rows are open — and cross-checks its own enumeration
against the counting view, refusing `BLOCK-RULE-MISMATCH` if the two ever disagree.

Sibling tool, different question: `audit-program-pipeline.py` asks whether the idea -> spec ->
plan -> todo CHAIN is intact. This one asks what state the tasks are in. Run both.

Every number here is a **reading of the current tree**, not a constant, and not a work estimate:
an `L` and an `XS` block count the same. Nothing here is pinned by a test.

Refusals are named and fail closed (`PROGRAM-STATUS REFUSED: <name>`); the exit code is non-zero
and every external `git` call carries a hard timeout.

Usage:
    python gk-core/scripts/program_status.py                      # markdown
    python gk-core/scripts/program_status.py --json               # machine-readable, full detail
    python gk-core/scripts/program_status.py --program combat-ai  # one program
    python gk-core/scripts/program_status.py --fail-on-open       # non-zero while open blocks remain
    python gk-core/scripts/program_status.py --timeout 10
"""
from __future__ import annotations

import argparse
import fnmatch
import importlib.util
import json
import re
import subprocess
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
DEFAULT_TIMEOUT = 30.0

# Closed vocabularies. Each is owned elsewhere and RE-CHECKED against its owner by
# `gk-core/tests/tools/test_program_status.py`, so neither copy can drift alone:
#   - the session-record contract (required fields, modes, statuses) and the `_`-prefix skip:
#     `scripts/session-boundary-check.py`'s `REQUIRED_FIELDS` / `VALID_MODES` / `VALID_STATUS` and its
#     `TEMPLATE_PREFIX` skip. That tool decides crossing; this one reads the same records, so it
#     validates them by the same rules instead of a second schema. The citation was a LINE RANGE into
#     `session-boundary-check.ps1` and is now a NAME, because a port changes every line number and a
#     line citation into a retired file is a citation that can only rot.
#   - the acceptance verdict vocabulary: the RETIRED
#     `.claude/cmdc-agents/scripts/accept-lane.ps1` `AcceptanceVerdicts`, carried into
#     `.claude/cmdc-agents/scripts/accept_lane.py`. The tuple below is a literal, not a read of
#     either file - the shape was transcribed, so deleting the .ps1 changes nothing here.
SESSION_STATUSES = ("active", "merged", "abandoned")
SESSION_MODES = ("direct", "worktree")
SESSION_REQUIRED_FIELDS = ("session", "program", "problem", "mode", "branch", "worktree", "paths",
                           "started", "status")
ACCEPTANCE_VERDICTS = ("GREEN", "RED", "RED-KNOWN", "UNATTRIBUTED")

REFUSALS = {
    "NO-REPO": "the path given is not this repository (no tasks/ and no scripts/todo-shapes.v1.json)",
    "SHAPE-MAP-UNREADABLE": "scripts/todo-shapes.v1.json is missing or unparseable; the block rule has no declared shapes, so every program would read as unmeasured",
    "NO-TODO-FILES": "no tasks/*-todo.md files were found; an empty reading is not a status",
    "BLOCK-RULE-MISMATCH": "the per-block enumeration disagrees with the counting view of the block rule",
    "GIT-UNAVAILABLE": "git could not produce trustworthy head state",
    "TIMEOUT": "an external command exceeded its hard timeout",
    "SESSION-RECORD-INVALID": "a tasks/sessions/*.json record is not a valid record of the closed schema",
    "ACCEPTANCE-ARTEFACT-INVALID": "an acceptance artefact is not valid JSON",
    "LEDGER-UNREADABLE": "a tasks/*-ledger.jsonl line does not parse",
}


class Refusal(RuntimeError):
    """A named refusal. The tool prints the name, the detail, and exits non-zero."""

    def __init__(self, name: str, detail: str) -> None:
        if name not in REFUSALS:
            raise KeyError(f"unnamed refusal: {name}")
        super().__init__(f"{name}: {detail}")
        self.name = name
        self.detail = detail


# ---- the block rule, imported, never reimplemented -----------------------------------------------

def load_rule_module(repo: Path = REPO):
    """Import `gk-core/scripts/audit-program-pipeline.py` (hyphenated name, so by path)."""
    path = repo / "scripts" / "audit-program-pipeline.py"
    if not path.is_file():
        raise Refusal("NO-REPO", f"missing the block rule module {path.name}")
    spec = importlib.util.spec_from_file_location("audit_program_pipeline", path)
    if spec is None or spec.loader is None:  # pragma: no cover - import machinery failure
        raise Refusal("NO-REPO", "could not load the block rule module")
    module = importlib.util.module_from_spec(spec)
    # `@dataclass` resolves annotations through `sys.modules[cls.__module__]`, so the module must be
    # registered BEFORE it is executed. Skipping this raises `AttributeError: 'NoneType' ...` from
    # inside dataclasses, which is how an import-by-path quietly turns into a traceback.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


# ---- git state, read-only, hard timeouts --------------------------------------------------------

@dataclass(frozen=True)
class GitState:
    head: str
    shortHead: str
    branch: str
    detached: bool
    upstream: str | None
    ahead: int | None
    behind: int | None
    dirtyPaths: int
    untrackedPaths: int
    conflictedPaths: int
    worktrees: int

    def render(self) -> str:
        rel = "detached HEAD" if self.detached else f"`{self.branch}`"
        if self.upstream is None:
            up = "no upstream"
        else:
            up = f"{self.upstream} (+{self.ahead}/-{self.behind})"
        return (f"{self.shortHead} on {rel} · upstream {up} · "
                f"dirty {self.dirtyPaths} (untracked {self.untrackedPaths}, "
                f"conflicted {self.conflictedPaths}) · worktrees {self.worktrees}")


def _git(repo: Path, *args: str, timeout: float) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True, encoding="utf-8", errors="replace", timeout=timeout, check=False,
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip().splitlines()
        raise Refusal("GIT-UNAVAILABLE", f"`git {' '.join(args)}` -> {detail[0] if detail else 'no output'}")
    return result.stdout.strip()


def git_state(repo: Path, timeout: float) -> GitState:
    try:
        head = _git(repo, "rev-parse", "HEAD", timeout=timeout)
        branch = _git(repo, "rev-parse", "--abbrev-ref", "HEAD", timeout=timeout)
        porcelain = _git(repo, "status", "--porcelain=v1", "--untracked-files=all", timeout=timeout)
        worktrees = _git(repo, "worktree", "list", "--porcelain", timeout=timeout).count("\nworktree ")
        upstream = None
        ahead = behind = None
        if branch and branch != "HEAD":
            got = subprocess.run(
                ["git", "-C", str(repo), "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"],
                capture_output=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
            if got.returncode == 0:
                upstream = got.stdout.strip()
                counts = subprocess.run(
                    ["git", "-C", str(repo), "rev-list", "--left-right", "--count", f"{upstream}...HEAD"],
                    capture_output=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
                if counts.returncode == 0 and counts.stdout.split():
                    behind, ahead = (int(x) for x in counts.stdout.split())
        paths = [line for line in porcelain.splitlines() if line.strip()]
        conflicted = sum(1 for line in paths if line[:2] in ("DD", "AU", "UD", "UA", "DU", "AA", "UU"))
        untracked = sum(1 for line in paths if line.startswith("??"))
        return GitState(
            head=head, shortHead=head[:9], branch=branch, detached=branch == "HEAD",
            upstream=upstream, ahead=ahead, behind=behind,
            dirtyPaths=len(paths), untrackedPaths=untracked, conflictedPaths=conflicted,
            worktrees=worktrees,
        )
    except subprocess.TimeoutExpired as exc:
        raise Refusal("TIMEOUT", f"git {' '.join(exc.cmd[2:]) if exc.cmd else ''} exceeded {timeout}s") from exc


# ---- programs: one row per tasks/<program>-todo.md ----------------------------------------------

@dataclass(frozen=True)
class OpenBlock:
    id: str
    line: int
    title: str
    signals: tuple[str, ...]
    untickedBoxes: int

    @property
    def label(self) -> str:
        """A printable label. The id is the identity; a block with no id is labelled by its title,
        because a bare line number reads as a defect and the title is what a human needs."""
        return self.id or (self.title if len(self.title) <= 44 else self.title[:43].rstrip() + "…") \
            or f"line {self.line}"


@dataclass
class ProgramStatus:
    program: str
    todo: str
    shape: str | None
    measured: bool
    openBlocks: int
    doneBlocks: int
    untickedBoxes: int
    shadedBoxes: int
    note: str
    open: list[OpenBlock] = field(default_factory=list)
    fencedBy: list[str] = field(default_factory=list)
    acceptanceNames: list[str] = field(default_factory=list)
    # A tick is a claim. These split `doneBlocks` by whether any GREEN acceptance
    # artefact names a SHA that is in this head, so "done" can never be read as
    # "shipped" without a gate run behind it.
    doneVerified: int = 0
    doneUnverified: int = 0
    verifiedBy: list[str] = field(default_factory=list)


def _shape_map(repo: Path) -> dict[str, dict]:
    path = repo / "scripts" / "todo-shapes.v1.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise Refusal("SHAPE-MAP-UNREADABLE", "scripts/todo-shapes.v1.json does not exist") from exc
    except (OSError, ValueError) as exc:
        raise Refusal("SHAPE-MAP-UNREADABLE", f"scripts/todo-shapes.v1.json: {exc}") from exc
    if not isinstance(data, dict) or not data:
        raise Refusal("SHAPE-MAP-UNREADABLE", "the shape map is not a non-empty object")
    return data


def program_statuses(repo: Path, rule, shapes: dict[str, dict], timeout: float) -> list[ProgramStatus]:
    todos = sorted(repo.glob("tasks/*-todo.md"))
    if not todos:
        raise Refusal("NO-TODO-FILES", "tasks/*-todo.md matched nothing")
    out: list[ProgramStatus] = []
    for todo in todos:
        rel = todo.relative_to(repo).as_posix()
        text = todo.read_text(encoding="utf-8", errors="replace")
        declared = shapes.get(rel)
        shape = (declared or {}).get("shape")
        if not declared or shape in (None, "", "none"):
            out.append(ProgramStatus(
                program=todo.name[: -len("-todo.md")], todo=rel, shape=shape, measured=False,
                openBlocks=0, doneBlocks=0, untickedBoxes=0, shadedBoxes=0,
                note="no shape declared in scripts/todo-shapes.v1.json — unmeasured by design, never counted as zero",
            ))
            continue
        blocks = rule.iter_task_blocks(text, shape)
        open_blocks, done_blocks, unticked, shaded = rule.task_blocks(text, shape)
        if (len([b for b in blocks if not b.done]), len([b for b in blocks if b.done])) != (open_blocks, done_blocks):
            raise Refusal("BLOCK-RULE-MISMATCH", f"{rel}: enumeration disagrees with the counting view")
        out.append(ProgramStatus(
            program=todo.name[: -len("-todo.md")], todo=rel, shape=shape, measured=True,
            openBlocks=open_blocks, doneBlocks=done_blocks, untickedBoxes=unticked, shadedBoxes=shaded,
            note=f"shape {shape}" + (f" (declared exemplar: {(declared or {}).get('exemplar', '—')})"),
            open=[OpenBlock(id=b.task_id, line=b.line, title=b.title, signals=b.signals,
                            untickedBoxes=b.unticked_boxes) for b in blocks if not b.done],
        ))
    return out


# ---- session records: who holds the fence --------------------------------------------------------

@dataclass(frozen=True)
class SessionRecord:
    session: str
    status: str
    mode: str
    branch: str
    program: str
    paths: tuple[str, ...]
    record: str

    def fences(self, rel: str) -> bool:
        for pattern in self.paths:
            if pattern == rel or fnmatch.fnmatch(rel, pattern.rstrip("/") + "/*") or fnmatch.fnmatch(rel, pattern):
                return True
        return False


# The `_`-prefix skip, named so `gk-core/tests/tools/test_program_status.py` can pin it as a VALUE against the
# owner's `TEMPLATE_PREFIX`. The previous drift guard matched the PowerShell fragment
# `Where-Object { $_.Name -notlike '_*' }` in the owner's source, which made the owner's DIALECT the
# thing under test.
SESSION_TEMPLATE_PREFIX = "_"


def session_records(repo: Path) -> list[SessionRecord]:
    out: list[SessionRecord] = []
    # `_`-prefixed files are templates, not records — the same skip `session-boundary-check.py` makes
    # through its `TEMPLATE_PREFIX`, and `tasks/sessions/_template.json` is the one that needs it.
    for path in sorted(repo.glob("tasks/sessions/*.json")):
        if path.name.startswith(SESSION_TEMPLATE_PREFIX):
            continue
        rel = path.relative_to(repo).as_posix()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: {exc}") from exc
        if not isinstance(data, dict):
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: not an object")
        missing = [f for f in SESSION_REQUIRED_FIELDS
                   if f not in data and not (f == "worktree" and data.get("worktree") is None)]
        if missing:
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: missing required field(s) {', '.join(missing)}")
        name = data.get("session")
        if not isinstance(name, str) or not name:
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: no session name")
        if name != path.stem:
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: session name {name!r} does not match the file name")
        if data.get("status") not in SESSION_STATUSES:
            raise Refusal("SESSION-RECORD-INVALID",
                          f"{rel}: status {data.get('status')!r} is outside {SESSION_STATUSES}")
        if data.get("mode") not in SESSION_MODES:
            raise Refusal("SESSION-RECORD-INVALID",
                          f"{rel}: mode {data.get('mode')!r} is outside {SESSION_MODES}")
        paths = data.get("paths") or []
        if not isinstance(paths, list) or not all(isinstance(p, str) for p in paths):
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: paths is not a list of strings")
        if data.get("status") == "active" and data.get("mode") == "worktree" and not data.get("worktree"):
            raise Refusal("SESSION-RECORD-INVALID", f"{rel}: active worktree record has no worktree path")
        out.append(SessionRecord(
            session=name, status=data["status"], mode=data["mode"], branch=str(data.get("branch") or "?"),
            program=str(data.get("program") or "?"), paths=tuple(paths), record=rel,
        ))
    if not out:
        raise Refusal("SESSION-RECORD-INVALID", "tasks/sessions/*.json matched no record")
    return out


# ---- acceptance artefacts: verdicts, and whether the SHA is in this head -------------------------

@dataclass(frozen=True)
class Acceptance:
    lane: str
    shortSha: str
    verdict: str
    verdictFamily: str
    verdictDetail: str
    schema: int | None
    inThisHead: bool | None
    artefact: str
    scope: str


def _in_head(repo: Path, sha: str, head: str, timeout: float) -> bool | None:
    result = subprocess.run(
        ["git", "-C", str(repo), "merge-base", "--is-ancestor", sha, head],
        capture_output=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
    if result.returncode not in (0, 1):
        return None  # the SHA is not in this repository at all
    return result.returncode == 0


def acceptances(repo: Path, head: str, timeout: float) -> list[Acceptance]:
    out: list[Acceptance] = []
    for path in sorted(repo.glob(".claude/cmdc-agents/acceptance/*.json")):
        rel = path.relative_to(repo).as_posix()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise Refusal("ACCEPTANCE-ARTEFACT-INVALID", f"{rel}: {exc}") from exc
        if not isinstance(data, dict) or "verdict" not in data:
            raise Refusal("ACCEPTANCE-ARTEFACT-INVALID", f"{rel}: no verdict")
        verdict = str(data["verdict"])
        family, _, detail = verdict.partition("(")
        family = family.strip()
        detail = detail.rstrip(") ").strip()
        if family not in ACCEPTANCE_VERDICTS:
            raise Refusal("ACCEPTANCE-ARTEFACT-INVALID",
                          f"{rel}: verdict family {family!r} is outside {ACCEPTANCE_VERDICTS}")
        sha = str(data.get("sha") or "")
        named = path.stem.rsplit("-", 1)[-1]
        if sha and named and not sha.startswith(named) and not named.startswith(sha[:8]):
            raise Refusal("ACCEPTANCE-ARTEFACT-INVALID",
                          f"{rel}: file names {named!r} but the artefact's sha is {sha[:12]!r} — "
                          "read the artefact named for the SHA under review, never a stale one")
        out.append(Acceptance(
            lane=str(data.get("lane") or path.stem.rsplit("-", 1)[0]),
            shortSha=sha[:8] or named, verdict=verdict, verdictFamily=family, verdictDetail=detail,
            schema=data.get("schemaVersion") if isinstance(data.get("schemaVersion"), int) else None,
            inThisHead=_in_head(repo, sha, head, timeout) if sha else None,
            artefact=rel, scope=str(data.get("scope") or ""),
        ))
    if not out:
        raise Refusal("ACCEPTANCE-ARTEFACT-INVALID", "no acceptance artefacts found")
    return out


# ---- ledgers: health only, never a completion claim ---------------------------------------------

@dataclass(frozen=True)
class Ledger:
    path: str
    lines: int
    lastEvent: str | None


def ledgers(repo: Path) -> list[Ledger]:
    out: list[Ledger] = []
    for path in sorted(repo.glob("tasks/*-ledger.jsonl")):
        rel = path.relative_to(repo).as_posix()
        lines, last = 0, None
        for number, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            if not raw.strip():
                continue
            try:
                data = json.loads(raw)
            except ValueError as exc:
                raise Refusal("LEDGER-UNREADABLE", f"{rel}:{number}: {exc}") from exc
            lines += 1
            for key in ("event", "kind", "type"):
                if isinstance(data, dict) and data.get(key):
                    last = f"{data[key]}"
                    break
            else:
                last = last or "(unlabelled)"
        out.append(Ledger(path=rel, lines=lines, lastEvent=last))
    return out


# ---- the report ----------------------------------------------------------------------------------

@dataclass
class Report:
    git: dict[str, Any]
    totals: dict[str, int]
    programs: list[dict[str, Any]]
    unmeasured: list[dict[str, Any]]
    sessions: dict[str, Any]
    acceptances: dict[str, Any]
    ledgers: list[dict[str, Any]]
    headerClaims: list[dict[str, Any]]
    caveats: list[str]


CAVEATS = [
    "A tick is a claim, not evidence. This tool cannot prove a task is done; it reports what the "
    "documents claim and who holds the fence.",
    "Every count is a reading of this tree, not a constant and not a work estimate: an `L` and an "
    "`XS` block count the same.",
    "A `signals` entry (`BLOCKED`, `OWNER-ONLY`, `PAUSED`, …) is a phrase the block itself carries. "
    "It is evidence for a human, not a third state, and it is never counted.",
    "A program with no declared shape stays unmeasured by design; it is never reported as zero open.",
    "Acceptance verdicts describe a lane at its own SHA. Cross-lane interaction is only visible to a "
    "gate run on the merged head (`.claude/cmdc-agents/scripts/post_merge_check.py`).",
    "The report's per-file hand overrides (backlog-reconciliation §3 notes) are not implemented; a "
    "count here can differ from that prototype's.",
    "A `falsified` header claim means only that the number in a todo header no longer matches this "
    "file's block reading. It is not a verdict on intent: the claim may be scoped to a wave, stale, "
    "or counting a different unit, and `scoped` / `unclear` are reported as their own verdicts.",
]


def build_report(repo: Path = REPO, program: str | None = None, timeout: float = DEFAULT_TIMEOUT) -> Report:
    if not (repo / "tasks").is_dir() or not (repo / "scripts").is_dir():
        raise Refusal("NO-REPO", f"{repo} has no tasks/ and scripts/")
    # The shape map is read FIRST: it is a local file with its own named refusal, and importing the
    # block rule before it turns "your marker map is gone" into an opaque import error.
    shapes = _shape_map(repo)
    rule = load_rule_module(repo)
    git = git_state(repo, timeout)
    statuses = program_statuses(repo, rule, shapes, timeout)
    if program:
        statuses = [s for s in statuses if s.program == program or fnmatch.fnmatch(s.program, program)]
        if not statuses:
            raise Refusal("NO-TODO-FILES", f"no program matches {program!r}")
    sessions = session_records(repo)
    arts = acceptances(repo, git.head, timeout)
    books = ledgers(repo)

    for status in statuses:
        status.fencedBy = sorted(s.session for s in sessions
                                 if s.status == "active" and s.fences(status.todo))
        token = status.program.replace("-", "")
        mine = [a for a in arts
                if status.program in a.scope or token in a.lane.replace("-", "")]
        status.acceptanceNames = sorted(a.artefact for a in mine)
        # Verified means a GREEN verdict naming a SHA that is an ancestor of this
        # head. A GREEN against a SHA that is not in this head proves that lane,
        # not this integration -- which is the distinction the whole report is for.
        status.verifiedBy = sorted(a.artefact for a in mine
                                   if a.verdictFamily == "GREEN" and a.inThisHead is True)
        if status.doneBlocks:
            if status.verifiedBy:
                status.doneVerified = status.doneBlocks
            else:
                status.doneUnverified = status.doneBlocks

    measured = [s for s in statuses if s.measured]
    unmeasured = [s for s in statuses if not s.measured]
    by_status: dict[str, int] = {}
    for s in sessions:
        by_status[s.status] = by_status.get(s.status, 0) + 1
    families: dict[str, int] = {}
    for a in arts:
        families[a.verdictFamily] = families.get(a.verdictFamily, 0) + 1

    return Report(
        git=asdict(git),
        totals={
            "programs": len(statuses),
            "measured": len(measured),
            "unmeasured": len(unmeasured),
            "openBlocks": sum(s.openBlocks for s in measured),
            "doneBlocks": sum(s.doneBlocks for s in measured),
            "doneVerified": sum(s.doneVerified for s in measured),
            "doneUnverified": sum(s.doneUnverified for s in measured),
            "openSignalledBlocks": sum(1 for s in measured for b in s.open if b.signals),
        },
        programs=[
            {
                "program": s.program, "todo": s.todo, "shape": s.shape, "note": s.note,
                "openBlocks": s.openBlocks, "doneBlocks": s.doneBlocks,
                "doneVerified": s.doneVerified, "doneUnverified": s.doneUnverified,
                "untickedBoxes": s.untickedBoxes, "shadedBoxes": s.shadedBoxes,
                "open": [asdict(b) | {"signals": list(b.signals)} for b in s.open],
                "fencedByActiveSessions": s.fencedBy,
                "acceptanceArtefactsNamingIt": s.acceptanceNames,
                "greenAcceptancesInThisHead": s.verifiedBy,
            }
            for s in sorted(measured, key=lambda s: (-s.openBlocks, s.program))
        ],
        unmeasured=[{"program": s.program, "todo": s.todo, "shape": s.shape, "note": s.note} for s in unmeasured],
        sessions={
            "counts": by_status,
            "active": [asdict(s) | {"paths": list(s.paths)} for s in sessions if s.status == "active"],
        },
        acceptances={
            "counts": families,
            "inThisHead": sum(1 for a in arts if a.inThisHead is True),
            "notInThisHead": sum(1 for a in arts if a.inThisHead is False),
            "notGreen": [
                {"artefact": a.artefact, "lane": a.lane, "sha": a.shortSha, "verdict": a.verdict,
                 "inThisHead": a.inThisHead}
                for a in arts if a.verdictFamily != "GREEN"
            ],
            "notInThisHeadAndNotGreen": [
                {"artefact": a.artefact, "lane": a.lane, "sha": a.shortSha, "verdict": a.verdict}
                for a in arts if a.verdictFamily != "GREEN" and a.inThisHead is not True
            ],
        },
        ledgers=[asdict(b) for b in books],
        headerClaims=[asdict(c) for c in header_claims(repo, statuses, rule)],
        caveats=list(CAVEATS),
    )


def _label(block: dict[str, Any]) -> str:
    """Printable label for an open block, matching `OpenBlock.label` (id, else title, else line)."""
    title = block.get("title") or ""
    if block.get("id"):
        return str(block["id"])
    if len(title) > 44:
        title = title[:43].rstrip() + "…"
    return title or f"line {block['line']}"


# A number a todo header claims about its own work. The vocabulary is closed on purpose: a claim the
# reader cannot classify must stay unread rather than be guessed at.
#
#   measured against the block reading : done, open, remaining, unticked
#   informational only                 : tasks, shipped  (a "N tasks" line is usually scoped to a
#                                       wave or a module, so it is printed, never called false)
#
# The lookbehind is load-bearing and was added after the first run accused four programs falsely:
# a bare `(\d+)\s+open` matches the tail of a RANGE ("Phases 6-10 open") and the number inside a TASK
# ID ("P0 done", "F7 done"). Both are numbers that are not claims about the file, so the number must
# not be preceded by a word character, a dot, or any dash.
HEADER_CLAIM_RE = re.compile(
    r"(?<![\w.\-‐-―−])"
    r"(?P<n>\d+)\s+(?P<unit>done|open|remaining|unticked|shipped|tasks)\b", re.IGNORECASE)
# A claim scoped to part of the file is not a whole-file claim, and calling it stale would be wrong.
# These cues are what separate "46 done" (the file) from "15 tasks in wave 2" (a slice).
CLAIM_SCOPE_RE = re.compile(
    r"\b(wave|phase|module|modules|step|steps|tier|sub-?program|under|of\s+the|in\s+the|"
    r"of\s+\d|chapter|gate|checkpoint|row|rows|only|remaining\s+after)\b", re.IGNORECASE)
CLAIM_UNITS_MEASURED = ("done", "open", "remaining", "unticked")


@dataclass(frozen=True)
class HeaderClaim:
    file: str
    line: int
    text: str
    number: int
    unit: str
    verdict: str
    measured: int | None


def header_claims(repo: Path, statuses: list[ProgramStatus], rule
                  ) -> list[HeaderClaim]:
    """Every numeric self-claim in a todo's HEADER, beside what the block rule measures today.

    The header is the text **before the first task block** — what a reader believes without reading
    the body. Scanning a fixed line count instead swept in task rows, where "P0 done" is a task id,
    and accused `gui-lego` of a stale claim it never made.

    `falsified` means exactly one thing: the header's number does not match the block reading of this
    file now. It does NOT mean the header was written in bad faith — a claim may be scoped to a wave,
    stale, or counting a different unit, and the three cases need different fixes. `scoped` and
    `unclear` are therefore first-class verdicts, not soft failures.
    """
    out: list[HeaderClaim] = []
    by_todo = {s.todo: s for s in statuses}
    for path in sorted(repo.glob("tasks/*-todo.md")):
        rel = path.relative_to(repo).as_posix()
        status = by_todo.get(rel)
        if status is None or not status.measured:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        blocks = rule.iter_task_blocks(text, status.shape)
        # The header ends where the first task block starts; with no blocks the whole file is header.
        header_end = blocks[0].line - 1 if blocks else len(text.splitlines())
        for number, line in enumerate(text.splitlines()[:header_end], 1):
            for match in HEADER_CLAIM_RE.finditer(line):
                unit = match.group("unit").lower()
                # `open` is also an ordinary adjective, and a fourth false-positive class came from
                # it: "267 + 9 open prefix families" and "§14 open question 2" are not claims that
                # nine things are unfinished. A state reading puts nothing but punctuation or a
                # number after the word; an adjective reading puts a noun there.
                if unit == "open" and re.match(r"\s+[a-z]", line[match.end():]):
                    continue
                sentence = line[max(0, match.start() - 90): match.end() + 60]
                scoped = bool(CLAIM_SCOPE_RE.search(sentence))
                if unit not in CLAIM_UNITS_MEASURED:
                    measured, verdict = None, ("scoped" if scoped else "unclear")
                else:
                    measured = {"done": status.doneBlocks, "open": status.openBlocks,
                                "remaining": status.openBlocks,
                                "unticked": status.untickedBoxes}[unit]
                    if scoped:
                        verdict = "scoped"
                    else:
                        verdict = "confirmed" if measured == int(match.group("n")) else "falsified"
                out.append(HeaderClaim(
                    file=rel, line=number, text=line.strip()[:160], number=int(match.group("n")),
                    unit=unit, verdict=verdict, measured=measured))
    return out


def git_state_line(git: dict[str, Any]) -> str:
    """One line of head state for a human.

    A detached checkout has no branch and usually no upstream, and printing
    `branch HEAD · upstream none (+None/-None)` reads like three separate problems
    where there is one normal state. Only the parts that exist are printed.
    """
    where = "detached HEAD" if git.get("detached") or git.get("branch") == "HEAD" else f"branch `{git['branch']}`"
    if git.get("upstream"):
        where += f" · upstream `{git['upstream']}` +{git.get('ahead')}/-{git.get('behind')}"
    else:
        where += " · no upstream"
    return (f"{where} · dirty {git['dirtyPaths']} (untracked {git['untrackedPaths']}, "
            f"conflicted {git['conflictedPaths']}) · worktrees {git['worktrees']}")


def render_markdown(report: Report, max_details: int = 8, show_claims: bool = False) -> str:
    git, totals = report.git, report.totals
    out: list[str] = []
    out.append("# Program status")
    out.append("")
    out.append(f"**Head** `{git['shortHead']}` · {git_state_line(git)}")
    out.append("")
    unv = totals.get("doneUnverified", 0)
    ver = totals.get("doneVerified", 0)
    out.append(f"**{totals['programs']} programs** — {totals['measured']} measured, "
               f"{totals['unmeasured']} unmeasured · **{totals['openBlocks']} open task blocks**, "
               f"{totals['doneBlocks']} done ({totals['openSignalledBlocks']} open blocks carry a "
               f"blocking signal in their own text).")
    out.append("")
    out.append(f"**Of the {totals['doneBlocks']} done: {ver} have a GREEN acceptance naming a SHA in "
               f"this head; {unv} are ticked with nothing behind them.** A tick is a claim — the second "
               f"number is the one that is not yet shipped. Read `--json` for the per-program split "
               f"(`doneVerified` / `doneUnverified`).")
    out.append("")
    out.append("A block is a task, not a checkbox line. These are readings of this tree, not constants.")
    out.append("")
    out.append("## Left, per program (open blocks, descending)")
    out.append("")
    out.append("| Program | open | done | verified | unticked boxes | fenced by | open blocks |")
    out.append("|---|--:|--:|--:|--:|---|---|")
    for p in report.programs:
        if p["openBlocks"] == 0 and not p["fencedByActiveSessions"]:
            continue
        ids = ", ".join(_label(b) for b in p["open"][:max_details])
        if len(p["open"]) > max_details:
            ids += f", … (+{len(p['open']) - max_details})"
        fence = ", ".join(f"`{s}`" for s in p["fencedByActiveSessions"]) or "—"
        done = p["doneBlocks"]
        ver = p.get("doneVerified", 0)
        mark = f"{ver}/{done}" if done else "—"
        out.append(f"| `{p['program']}` | {p['openBlocks']} | {done} | {mark} | "
                   f"{p['untickedBoxes']} | {fence} | {ids or '—'} |")
    out.append("")
    out.append("`verified/done` = done blocks in a program that has a GREEN acceptance naming a SHA "
               "in this head. `0/40` means forty ticked rows and no gate run behind any of them.")
    out.append("")
    complete = [p for p in report.programs if p["openBlocks"] == 0]
    if complete:
        unverified_complete = [p for p in complete if p.get("doneUnverified", 0) > 0]
        out.append(f"**No open task blocks by the declared shapes:** "
                   + ", ".join(f"`{p['program']}`" for p in complete))
        out.append("")
        if unverified_complete:
            out.append(f"⚠ **{len(unverified_complete)} of those {len(complete)} have ticked rows with no "
                       f"GREEN acceptance in this head** — "
                       + ", ".join(f"`{p['program']}` ({p['doneUnverified']})"
                                   for p in unverified_complete[:12])
                       + (f", … (+{len(unverified_complete) - 12} more)" if len(unverified_complete) > 12 else "")
                       + ". Zero open blocks here means the documents claim completion, not that a gate "
                         "ever ran.")
            out.append("")
        out.append("_That is what the documents claim. It is not proof the work is done, and it says "
                   "nothing about live, browser or merged-head gates._")
        out.append("")
    if report.unmeasured:
        out.append("## Unmeasured (never counted as zero)")
        out.append("")
        for u in report.unmeasured:
            out.append(f"- `{u['program']}` — {u['note']}")
        out.append("")
    out.append("## Who is on it (active session records)")
    out.append("")
    if not report.sessions["active"]:
        out.append("_No active session record._")
    for s in report.sessions["active"]:
        out.append(f"- `{s['session']}` · {s['mode']} · `{s['branch']}` · {len(s['paths'])} fenced path(s)")
    out.append("")
    out.append(f"Session records: " + ", ".join(f"{k} {v}" for k, v in sorted(report.sessions["counts"].items())))
    out.append("")
    out.append("## Acceptance evidence")
    out.append("")
    acc = report.acceptances
    out.append(f"{acc['inThisHead']} artefact(s) name a SHA in this head; {acc['notInThisHead']} do not. "
               + "Verdicts: " + ", ".join(f"{k} {v}" for k, v in sorted(acc["counts"].items())) + ".")
    out.append("")
    not_green = acc["notGreen"]
    if not_green:
        shown = not_green[:max_details * 2]
        out.append("| Not GREEN | lane | sha | in this head |")
        out.append("|---|---|---|---|")
        for a in shown:
            out.append(f"| {a['verdict']} | `{a['lane']}` | `{a['sha']}` | {a['inThisHead']} |")
        if len(not_green) > len(shown):
            out.append(f"| … (+{len(not_green) - len(shown)}) | | | |")
        out.append("")
        stale = acc["notInThisHeadAndNotGreen"]
        if stale:
            out.append(f"**{len(stale)} not-GREEN artefact(s) name a SHA that is not in this head** — read "
                       "the log before calling them open debt; a superseded lane is not a red suite.")
            out.append("")
    else:
        out.append("_Every acceptance artefact on disk is GREEN._")
        out.append("")
    falsified = [c for c in report.headerClaims if c["verdict"] == "falsified"]
    if falsified or show_claims:
        out.append("## Header claims vs the block reading")
        out.append("")
        out.append("A todo header that states a number about its own work can go stale, and a stale "
                   "number in a status line is read as current by everyone who skips the body. "
                   "`falsified` = the header's number does not match the block reading today; it is "
                   "not a verdict on intent.")
        out.append("")
        shown = falsified if not show_claims else report.headerClaims
        out.append("| File | line | claim | measured | verdict |")
        out.append("|---|--:|---|--:|---|")
        for c in shown[:max_details * 2]:
            measured = "—" if c["measured"] is None else c["measured"]
            out.append(f"| `{c['file']}` | {c['line']} | {c['number']} {c['unit']} | {measured} | "
                       f"**{c['verdict']}** |")
        if len(shown) > max_details * 2:
            out.append(f"| … (+{len(shown) - max_details * 2} more) | | | | |")
        out.append("")
        if falsified:
            out.append(f"**{len(falsified)} header claim(s) do not match this tree.** Reconcile them "
                       "in the owning program's todo — a number nobody reproduces is not a status.")
            out.append("")
    if report.ledgers:
        out.append(f"## Ledgers ({len(report.ledgers)}) — health only, never a completion claim")
        out.append("")
        out.append("| ledger | events | last event label |")
        out.append("|---|--:|---|")
        for b in report.ledgers:
            out.append(f"| `{b['path']}` | {b['lines']} | {b['lastEvent'] or '—'} |")
        out.append("")
    out.append("## What this cannot prove")
    out.append("")
    for caveat in report.caveats:
        out.append(f"- {caveat}")
    out.append("")
    return "\n".join(out)


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Report done/left task blocks, session fences and acceptance evidence.")
    ap.add_argument("--json", action="store_true", help="emit the full report as JSON")
    ap.add_argument("--program", help="limit to one program (glob allowed)")
    ap.add_argument("--max-details", type=int, default=8, help="open block ids to print per program (default 8)")
    ap.add_argument("--all-claims", action="store_true",
                    help="print every header claim, not only the ones that no longer match")
    ap.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT,
                    help=f"hard timeout in seconds for every external call (default {DEFAULT_TIMEOUT:g})")
    ap.add_argument("--fail-on-open", action="store_true", help="exit 1 while any open task block remains")
    ap.add_argument("--fail-on-claim", action="store_true",
                    help="exit 1 while any todo header states a number the block reading contradicts")
    ap.add_argument("--root", type=Path, default=REPO, help=argparse.SUPPRESS)
    args = ap.parse_args(argv)
    if args.timeout <= 0:
        print("PROGRAM-STATUS REFUSED: TIMEOUT: --timeout must be positive", file=sys.stderr)
        return 2
    try:
        report = build_report(args.root, args.program, args.timeout)
    except Refusal as refusal:
        print(f"PROGRAM-STATUS REFUSED: {refusal}", file=sys.stderr)
        print(f"  meaning: {REFUSALS[refusal.name]}", file=sys.stderr)
        return 2
    except subprocess.TimeoutExpired as exc:
        print(f"PROGRAM-STATUS REFUSED: TIMEOUT: {exc}", file=sys.stderr)
        return 2
    except Exception as exc:  # fail closed on the unexpected, never a half-printed report
        print(f"PROGRAM-STATUS REFUSED: NO-REPORT: {type(exc).__name__}: {exc}", file=sys.stderr)
        print("  meaning: the report could not be produced; nothing above this line is a status.",
              file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(asdict(report), indent=2, ensure_ascii=False))
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(render_markdown(report, args.max_details, args.all_claims))
    if args.fail_on_open and report.totals["openBlocks"]:
        return 1
    if args.fail_on_claim and any(c["verdict"] == "falsified" for c in report.headerClaims):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
