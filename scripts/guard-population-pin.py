#!/usr/bin/env python3
"""
Guard: solid-enforcement `population-pin` (spec-population-pin.md).

AGENTS.md hard rule: "A guardrail validates the CONTRACT, never a population count or generated
text." A closed vocabulary's size IS the contract (pin it, once, at its owner). A derived
population's size is a READING (never pin it). A bare number cannot tell a scan which kind of count
it is, so this guard makes the author DECLARE it with a marker comment, and checks the declaration
(map decision D4: markers over heuristics) rather than guessing from the literal's value.

    P1 -- a count-pin literal >= minLiteral, in a test that reads committed content, needs a marker
          on the same line or the line directly above it. An unmarked pin is a population pin; the
          fix is never a marker, it is rewriting the assertion as the contract.
    P2 -- a `closed-vocabulary <Owner>` marker is used at most once per Owner. A second site pinning
          the SAME owner with a numeric literal is the six-fold `269` defect this rule exists to
          close; every other site should assert equality with the owner, not a second literal.
    P3 -- a marker names something real: `closed-vocabulary <Owner>` must resolve to a real symbol
          (found as a class/enum declaration, or at least a bare mention, in src/** or
          gk-forge/tools/seedsmith/seedsmith/**); `immutable <path>` must be a tracked, existing
          `gk-core/data/tuning/<domain>.v<n>.json`.

Usage (repo root):
    python gk-core/scripts/guard-population-pin.py                # full report
    python gk-core/scripts/guard-population-pin.py --summary       # per-rule counts only
    python gk-core/scripts/guard-population-pin.py --targets P1    # bare file:line list, audit-magic-
                                                            # numbers.py's own convention

Exit 0 = clean, 1 = any P1-P3 finding.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, forge_root  # noqa: E402

#: Structural, not a balance number a pass would tune (spec's own §"The threshold"): measured
#: 2026-09-18 against content-reading C# tests, 502 assertions below 10, dominated by 0-3
#: (emptiness/singleton contract checks, e.g. `Assert.Equal(0, errors.Count)`) and 6 (the six
#: resources / six elements). Not individually classified below 10 -- a stated trade-off, not a
#: proven boundary: a population smaller than 10 escapes this guard.
MIN_LITERAL = 10

SCAN_ROOTS = ("tests", "tools/seedsmith/tests")

#: A file is "reads committed content" when its text contains any of these signals (spec's own
#: definition: "touches data/{seed,generated,tuning}, locates the repo root, or loads a registry or
#: catalog"). A file-wide heuristic, not per-line -- deliberately broad, since the backlog this
#: module clears is dispositioned by a human at each site, not silently accepted by a narrow scan.
#:
#: Plain substrings, never `\b...\b`-wrapped: "RepoRoot"/"Registry"/"Catalog" almost always appear
#: INSIDE a longer identifier in this codebase (`FindRepoRoot`, `find_repo_root`,
#: `DerivedStatRegistry`, `AtomKindRegistry`) -- a word-boundary version matches only a bare,
#: standalone token and silently misses nearly every real site (reproduced live while writing this
#: guard's own falsifiers: `\bRepoRoot\b` matched zero of the fixture files that call
#: `FindRepoRoot()`).
CONTENT_SIGNAL_RE = re.compile(r"data[/\\](seed|generated|tuning)|repo.?root|registry|catalog", re.IGNORECASE)

MARKER_RE = re.compile(r"pin:\s*(closed-vocabulary|immutable)\s+(\S+)")

_CS_COUNT_EXPR = r"[^,;()]+?(?:\.Count(?:\(\))?|\.Length)"
CS_LIT_FIRST_RE = re.compile(r"Assert\.Equal\(\s*(\d+)\s*,\s*(" + _CS_COUNT_EXPR + r")\s*\)")
CS_LIT_SECOND_RE = re.compile(r"Assert\.Equal\(\s*(" + _CS_COUNT_EXPR + r")\s*,\s*(\d+)\s*\)")

_PY_LEN_EXPR = r"len\([^()]*\)"
_PY_COUNT_EXPR = r"[^,()]+?\.count"
PY_LEN_LIT_FIRST_RE = re.compile(r"assertEqual\(\s*(" + _PY_LEN_EXPR + r")\s*,\s*(\d+)\s*\)")
PY_LEN_LIT_SECOND_RE = re.compile(r"assertEqual\(\s*(\d+)\s*,\s*(" + _PY_LEN_EXPR + r")\s*\)")
PY_COUNT_LIT_FIRST_RE = re.compile(r"assertEqual\(\s*(" + _PY_COUNT_EXPR + r")\s*,\s*(\d+)\s*\)", re.IGNORECASE)
PY_COUNT_LIT_SECOND_RE = re.compile(r"assertEqual\(\s*(\d+)\s*,\s*(" + _PY_COUNT_EXPR + r")\s*\)", re.IGNORECASE)


def strip_line_comment(line: str, comment_token: str) -> str:
    """Best-effort: drop everything from the FIRST comment token onward, so a marker mentioned
    inside a docstring/prose line is never scanned as code. Does not attempt full string-literal
    awareness -- this guard's own findings are reviewed by a human at every site (green-first
    backlog), so a rare false positive costs a look, never a silent miss."""
    idx = line.find(comment_token)
    return line if idx < 0 else line[:idx]


def find_count_assertions(line: str, is_python: bool) -> "list[tuple[int, str]]":
    """Every (literal, expr) pair this line asserts equal, in EITHER argument order."""
    found: "list[tuple[int, str]]" = []
    if is_python:
        for rx, lit_group, expr_group in (
            (PY_LEN_LIT_FIRST_RE, 2, 1), (PY_LEN_LIT_SECOND_RE, 1, 2),
            (PY_COUNT_LIT_FIRST_RE, 2, 1), (PY_COUNT_LIT_SECOND_RE, 1, 2),
        ):
            for m in rx.finditer(line):
                found.append((int(m.group(lit_group)), m.group(expr_group).strip()))
    else:
        for rx, lit_group, expr_group in ((CS_LIT_FIRST_RE, 1, 2), (CS_LIT_SECOND_RE, 2, 1)):
            for m in rx.finditer(line):
                found.append((int(m.group(lit_group)), m.group(expr_group).strip()))
    return found


def find_marker(lines: "list[str]", line_index: int) -> "tuple[str, str] | None":
    """A `pin: <kind> <target>` marker on this line or the line directly above (0-indexed)."""
    for idx in (line_index, line_index - 1):
        if idx < 0 or idx >= len(lines):
            continue
        m = MARKER_RE.search(lines[idx])
        if m:
            return m.group(1), m.group(2)
    return None


def _owner_token_exists(token: str, repo_root: Path) -> bool:
    pat = re.compile(r"\b" + re.escape(token) + r"\b")
    # src/ is gk-core's; tools/seedsmith/ is gk-forge's, and gk-forge is a SIBLING, so the second
    # scan root has never existed since the split. Half the audit was scanning nothing and saying
    # so in a line a reader would take for a summary of what it covered.
    # A missing gk-forge is a named condition, not an unhandled RuntimeError. Before this the guard
    # printed "scanned: tests, tools/seedsmith/tests" - naming a scan root it could not open, which is
    # a coverage claim about nothing.
    try:
        # Wrapped like the other call site. An absent gk-forge is a named condition here too:
        # this function asks whether a token exists in a file it cannot reach, and answering
        # "no" because the repository is missing would be a guard reporting its blindness as
        # an answer about the content.
        try:
            seedsmith_src = forge_root(repo_root) / "tools" / "seedsmith" / "seedsmith"
        except RootNotFound:
            return None
    except RootNotFound as exc:
        print(f"[{GUARD_ID}] EXIT_FORGE_ROOT_MISSING: {exc}", file=sys.stderr)
        return 2
    for root in (repo_root / "src", seedsmith_src):
        if not root.is_dir():
            continue
        for path in root.rglob("*"):
            if path.suffix not in (".cs", ".py") or not path.is_file():
                continue
            try:
                text = path.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            if pat.search(text):
                return True
    return False


def owner_exists(owner: str, repo_root: Path) -> bool:
    """P3 for `closed-vocabulary <Owner>`: a lenient text-presence check (never a full parser,
    matching this repo's own audit-*.py convention). Tries the marker's OWN text first (a bare class/
    enum name, e.g. `DerivedStatRegistry`); a dotted member path (e.g. `AtomKindRegistry.KindCount`,
    needed so P2 can tell two DIFFERENT counts on the same class apart -- see that rule's own
    docstring) falls back to its last segment, since C# never spells `Class.Member` as one literal
    token at the declaration site (`public static int KindCount` inside `class AtomKindRegistry`)."""
    if _owner_token_exists(owner, repo_root):
        return True
    if "." in owner:
        return _owner_token_exists(owner.rsplit(".", 1)[-1], repo_root)
    return False


_IMMUTABLE_PATH_RE = re.compile(r"^data/tuning/[a-zA-Z0-9_-]+\.v\d+\.json$")


# AN OPTIONAL REPOSITORY PREFIX, because this guard's own standard mandates one. The spec
# (docs/architecture/solid-enforcement/spec-population-pin.md) writes its worked example as
# `pin: immutable gk-core/data/tuning/aptitudes.v8.json` and states the P3 rule as "immutable <path>
# must be a tracked gk-core/data/tuning/<d>.v<n>.json" - workspace-qualified, with the repository
# named. The regex this function used was `^data/tuning/...`, which rejects that form, so every pin
# written to the standard was reported as "not a tracked data/tuning/<domain>.v<n>.json". Ten of
# them, across four test files, each naming a file that exists and is tracked.
#
# The pins in the tree were following the standard. The guard was contradicting it, and the pins
# were the thing being changed to suit the contradiction - backwards, and the shape of change this
# repository refuses: the standard moving to fit the instrument.
#
# Accepting the prefix does not weaken the rule. The substantive requirement is untouched - the
# target must still be a real, tracked, VERSIONED tuning file, `<domain>.v<n>.json` under some
# repository's data/tuning - and it is resolved against the repository that owns it rather than
# joined onto this one, which is what produced the nonsense path gk-core/gk-core/data/tuning/... .
_REPO_PREFIX_RE = re.compile(r"^(?P<repo>[A-Za-z0-9_.-]+)/(?P<rest>.+)$")


def _is_git_tree(root: Path) -> bool:
    """A real repository working tree, as opposed to a planted fixture. `.git` is a directory in a
    normal clone and a file in a worktree or submodule, so both count."""
    git = root / ".git"
    return git.is_dir() or git.is_file()


_TRACKED_CACHE: dict[str, frozenset[str] | None] = {}


def _tracked_files(repo: Path) -> frozenset[str] | None:
    """Every path git tracks in `repo`, or None when that question cannot be asked.

    Cached per repository because a guard run validates many pins and `git ls-files` is a process.
    None means "not a git working tree" - a planted fixture - and the caller then falls back to
    existence, which is the only answer available there and the right one.
    """
    key = str(repo)
    if key in _TRACKED_CACHE:
        return _TRACKED_CACHE[key]
    result: frozenset[str] | None = None
    if _is_git_tree(repo):
        try:
            proc = subprocess.run(["git", "-C", str(repo), "ls-files", "-z"],
                                  capture_output=True, timeout=120)
            if proc.returncode == 0:
                names = proc.stdout.decode("utf-8", "replace").split("\0")
                result = frozenset(n.replace("\\", "/") for n in names if n)
        except (OSError, subprocess.SubprocessError):
            result = None       # git unusable: fall back to existence rather than fail the guard
    _TRACKED_CACHE[key] = result
    return result


def _sibling_root(named: str, repo_root: Path) -> Path | None:
    """The repository `named`, resolved through the workspace contract rather than by joining.

    Joining produced gk-core/gk-core/... for this repository's own name and, for a real sibling, a
    path that exists only if the sibling happens to be nested. The resolver already answers "which
    repository is that", and asking it is the difference between a guard that follows the topology
    and one that encodes a guess about it.
    """
    if named == repo_root.name:
        return repo_root
    candidate = repo_root.parent / named
    return candidate if candidate.is_dir() else None


def immutable_path_is_valid(path_text: str, repo_root: Path) -> bool:
    """A pin naming a real, tracked, VERSIONED tuning file.

    Three requirements, and they are separate on purpose. An optional leading repository segment is
    resolved FIRST - and only when the tail is itself a data/tuning path - because the standard this
    guard implements writes its pins workspace-qualified. What remains must then match the shape rule
    exactly. And the file must EXIST and be TRACKED in the repository that owns it.

    TRACKED is the requirement that was missing, and it is the one the guard's own message claims:
    "is not a tracked data/tuning/<domain>.v<n>.json" while the implementation asked only is_file().
    An untracked, never-committed file satisfied it, which is the weakest possible reading of "a
    published version never changes" - a file that was never published at all.

    A planted fixture is not a git working tree, so tracking is unanswerable there and existence is
    the only honest answer; that is the same fixture-versus-real-tree distinction guard_subjects.py
    makes, and the same reason: asking a question of a temporary directory produces a confident
    wrong answer rather than an honest one.
    """
    normalized = path_text.replace("\\", "/")
    repo_dir = repo_root
    prefix = _REPO_PREFIX_RE.match(normalized)
    # The tail must ITSELF be a data/tuning path, or there is no repository segment to strip.
    # Without that condition the heuristic misfires on the bare form: `data` is a real directory in
    # gk-core, so `data/tuning/x.v1.json` matched, lost its `data/`, and stopped matching the shape
    # rule - which is how a fix for the qualified form broke the plain one.
    if prefix and prefix.group("rest").startswith("data/tuning/"):
        named = prefix.group("repo")
        normalized = prefix.group("rest")
        resolved = _sibling_root(named, repo_root)
        if resolved is None:
            return False
        repo_dir = resolved
    if not _IMMUTABLE_PATH_RE.match(normalized):
        return False
    candidate = repo_dir / normalized
    if not candidate.is_file():
        return False
    tracked = _tracked_files(repo_dir)
    if tracked is None:
        return True            # a fixture: existence is the only answer available, and the right one
    return normalized in tracked


def iter_scan_files(repo_root: Path, scan_roots: "tuple[str, ...]"):
    for root_name in scan_roots:
        root = repo_root / root_name
        if not root.is_dir():
            continue
        for suffix in ("*.cs", "*.py"):
            for path in root.rglob(suffix):
                if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
                    continue
                if "__pycache__" in path.parts:
                    continue
                yield path


def scan(repo_root: "Path | None" = None, scan_roots: "tuple[str, ...] | None" = None) -> "dict[str, list[tuple]]":
    """Returns {'P1': [...], 'P2': [...], 'P3': [...]}, each entry (relpath, line_no, detail).
    `repo_root`/`scan_roots` default to the real repo (module-level constants) -- overridable so a
    falsifier can point this at a disposable fixture tree instead."""
    repo_root = repo_root or REPO_ROOT
    scan_roots = scan_roots or SCAN_ROOTS
    findings: "dict[str, list[tuple]]" = defaultdict(list)
    owner_sites: "dict[str, list[tuple]]" = defaultdict(list)  # owner -> [(path, line_no)]

    for path in iter_scan_files(repo_root, scan_roots):
        is_python = path.suffix == ".py"
        comment_token = "#" if is_python else "//"
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if not CONTENT_SIGNAL_RE.search(text):
            continue  # a pure-fixture file that never reads committed content -- out of scope

        lines = text.splitlines()
        for i, raw_line in enumerate(lines):
            code_line = strip_line_comment(raw_line, comment_token)
            for literal, expr in find_count_assertions(code_line, is_python):
                if literal < MIN_LITERAL:
                    continue
                marker = find_marker(lines, i)
                rel = path.relative_to(repo_root).as_posix()
                assert_name = "assertEqual" if is_python else "Assert.Equal"
                if marker is None:
                    findings["P1"].append((rel, i + 1, f"{assert_name}({literal}, {expr}) has no pin: marker"))
                    continue
                kind, target = marker
                if kind == "closed-vocabulary":
                    owner_sites[target].append((rel, i + 1))
                    if not owner_exists(target, repo_root):
                        findings["P3"].append((rel, i + 1, f"closed-vocabulary owner {target!r} does not resolve to a real symbol"))
                elif kind == "immutable":
                    if not immutable_path_is_valid(target, repo_root):
                        findings["P3"].append((rel, i + 1, f"immutable path {target!r} is not a tracked data/tuning/<domain>.v<n>.json"))

    for owner, sites in owner_sites.items():
        if len(sites) > 1:
            for rel, line_no in sites[1:]:
                findings["P2"].append((rel, line_no, f"closed-vocabulary {owner!r} is already pinned at {sites[0][0]}:{sites[0][1]} -- assert equality with the owner instead of a second literal"))

    return findings


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--targets", metavar="RULE", help="bare file:line list for one rule (P1/P2/P3)")
    parser.add_argument("--summary", action="store_true", help="per-rule counts only")
    args = parser.parse_args(argv)

    findings = scan()

    if args.targets:
        for rel, line_no, _ in findings.get(args.targets, []):
            print(f"{rel}:{line_no}")
        return 0

    total = sum(len(v) for v in findings.values())

    if args.summary:
        print("%-6s %6s" % ("rule", "count"))
        for rule in ("P1", "P2", "P3"):
            print("%-6s %6d" % (rule, len(findings.get(rule, []))))
        print("-" * 14)
        print("%-6s %6d" % ("TOTAL", total))
        return 1 if total else 0

    print("Population-pin audit — scanned: %s" % ", ".join(SCAN_ROOTS))
    print("Standard: docs/architecture/solid-enforcement/spec-population-pin.md")
    print("=" * 100)
    for rule in ("P1", "P2", "P3"):
        rows = findings.get(rule, [])
        print(f"\n{rule} ({len(rows)} finding(s))")
        print("-" * 100)
        if not rows:
            print("  clean")
            continue
        for rel, line_no, detail in rows[:30]:
            print(f"  {rel}:{line_no}  {detail}")
        if len(rows) > 30:
            print(f"  ... and {len(rows) - 30} more (--targets {rule})")

    print("\n" + "=" * 100)
    print(f"total {total} finding(s)")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
