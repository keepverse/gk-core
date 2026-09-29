#!/usr/bin/env python3
"""
Rewrite documentation citations when a `.ps1` tool is retired to a `.py` port.

Why this tool exists
--------------------
Deleting `scripts/guard-dal.ps1` left 360 dangling citations across 227 files under `docs/`, and
`audit-doc-citations.py --strict` reported 177 HIGH findings. `guard-dal` is the most-cited guard
in the repo, so the tail is proportional to how famous a tool is - which means every remaining
guard port inherits the same problem, and hand-editing it is how a sweep turns into 300
unreviewed diffs.

The rewrite is NOT a plain string swap, because the INVOCATION FORM changes too:

    .\\scripts\\guard-dal.ps1                      ->  python gk-core/scripts/guard-dal.py
    powershell -File scripts/guard-dal.ps1        ->  python gk-core/scripts/guard-dal.py
    pwsh -NoProfile -File scripts/guard-dal.ps1   ->  python gk-core/scripts/guard-dal.py
    scripts/guard-dal.ps1                         ->  gk-core/scripts/guard-dal.py      (a citation)
    `guard-dal.ps1`                              ->  `guard-dal.py`             (a prose mention)

Leaving any of those as `.ps1` produces a citation nobody can open, which is the defect
`guard-doc-citations` exists to report. Leaving them as a bare `.py` path loses the fact that the
tool must be RUN, not merely read - so the invocation forms get `python ` and the citation forms
stay bare.

Safety properties (each one a refusal, not a warning)
-----------------------------------------------------
* **Dry run by default.** `--apply` is required to write. A tool that rewrites 227 files on
  invocation is a tool that gets run once by accident.
* **Refuses if the old file still exists.** Renaming a citation to a `.py` that is not there yet -
  or that coexists with a `.ps1` still being run - would mint a pointer to nothing. The order is
  delete, then sweep.
* **Refuses if the new file does not exist.**
* **Idempotent.** A second run reports 0 changes, so it is safe in a loop and safe to re-verify.
* **`--json`** reports files touched, lines changed, and a per-rule count, so the operator can see
  what moved before and after.
* Bounded to an explicit set of roots (default `docs/`), never the whole tree: a sweep that
  rewrites `tasks/**` would edit historical evidence, which records what was run at the time.

Usage:
    python gk-core/scripts/ps1-rename-sweep.py --map guard-dal
    python gk-core/scripts/ps1-rename-sweep.py --map guard-dal --root docs --root AGENTS.md --json
    python gk-core/scripts/ps1-rename-sweep.py --map guard-dal --apply

    # A stem whose successor has a DIFFERENT name. Two scripts predate the kebab-case rule and became
    # snake_case Python MODULES: `accept-lane.ps1` -> `accept_lane.py`, `post-merge-check.ps1` ->
    # `post_merge_check.py`. `--map accept-lane` would derive `accept-lane.py`, which does not exist,
    # and the tool REFUSES with PORT-MISSING -- correctly, because there is nothing for the citations
    # to point at. The `old=new` form names the real target:
    python gk-core/scripts/ps1-rename-sweep.py --map accept-lane=accept_lane --root docs --apply

Exit 0 = nothing left to change (or the changes were applied). 1 = a refusal.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
# Where a repo tool may live. `scripts/` is where nearly all of them are; the manager/lane harness
# lives in `.claude/cmdc-agents/scripts/`, and hardcoding `scripts/` made the precondition report a
# MISSING path for a file that exists - a named refusal naming something the reader cannot check.
TOOL_DIRS = ("scripts", ".claude/cmdc-agents/scripts")
DEFAULT_ROOTS = ("docs",)
# `.html` was MISSING from this list until the first real run against guard-dal, which left
# docs/architecture/architecture-map.html citing a deleted script because only `*.md` was globbed.
# A generated or hand-written page that names a tool is still a citation, and an audit that only
# reads backticked markdown will not catch it - which is precisely how a stale pointer survives a
# green run. Both extensions are therefore swept.
DEFAULT_SUFFIXES = (".md", ".html")

# ---------------------------------------------------------------------------
# LINKED WORKTREES ARE NOT OURS TO EDIT
# ---------------------------------------------------------------------------
# Measured, not hypothesised: a sweep with `--root .claude` rewrote 1,180 files across ~20 OTHER
# sessions' worktrees under `.claude/worktrees/`. They are git-IGNORED, so `git status` showed
# nothing, no commit could have carried the damage, and the only reason it was caught at all was
# reading the tool's own `--json` output. Undoing it was manual: one rglob over that tree does not
# finish, and a blanket `git checkout` inside a shared worktree would have destroyed the other
# session's uncommitted work - which this repo has already paid for once.
#
# A worktree is another session's WORKING TREE, so editing one is a fence violation even when git
# cannot see the result. It is therefore a REFUSAL, not a skip: a skip is a silent reduction in
# coverage, and "coverage nobody can see" is how a green run stops meaning anything.
WORKTREE_LIST_TIMEOUT = 120


def repository_of(target: Path) -> Path | None:
    """The git repository that CONTAINS `target`, found by walking up to a `.git` entry.

    The sweep asked `linked_worktrees(REPO_ROOT)` — the repository the SCRIPT lives in — so its
    worktree list was correct only when the tool was pointed at its own repository. Point it anywhere
    else and it pruned the wrong repository's worktrees while leaving the target's own alone. That is
    the same defect class as `session-boundary-check` resolving worktree paths against the process
    directory instead of the repository it was told to check: a tool whose answer depends on where the
    TOOL is rather than on what it was pointed at. It is latent in this repo's own use (the tool and
    the target are the same repository) and was found by a fixture, because that is the only way to
    point the tool at a different repository at all.

    Returns `None` when the path is not inside a repository, and that is a DIFFERENT answer from
    "git could not be asked": outside a repository there are provably no linked worktrees, so the
    sweep proceeds with an empty list. The 1,180-file incident this function's docstring refers to was
    the opposite case - the list was UNKNOWN and treated as empty - so the two must not share a
    branch, and the distinction is the whole point of asking git at all.
    """
    current = target.resolve()
    for candidate in [current, *current.parents]:
        if (candidate / ".git").exists():
            return candidate
    return None


# The ARGS a ported tool takes, per stem, so a rewrite cannot pair a Python path with a PowerShell
# flag. This is the sweep's own third occurrence of the same defect and the second time it has shipped,
# so it is a GATE rather than a memory: the tool refuses to write a line it would break.
#
#   * `ps1-rename-sweep` rewrote 582 citations for session-boundary-check and left `-RepoRoot`/`-Session`
#     behind in 74 files. `python scripts/session-boundary-check.py -RepoRoot ...` is a command argparse
#     rejects, and in a lane brief a worker runs it, sees a usage error, and cannot tell that the tool
#     is fine and the invocation is not.
#   * The same sweep then rewrote 854 citations for guard-verification-boundaries and left `-Report`
#     behind in 11 files. A broken command is worse than a stale one: a stale one is obviously stale.
#
# The general lesson the standard already states for mangling transforms applies here: never hand-repair
# the result, add a gate that refuses to write unless the result is sound.
FLAG_SPELLINGS: dict[str, dict[str, str]] = {
    "session-boundary-check": {
        "-RepoRoot": "--repo-root", "-Session": "--session", "-Ci": "--ci",
        "-DiffBaseRef": "--diff-base-ref", "-DiffHeadRef": "--diff-head-ref",
        "-RequireDiffFence": "--require-diff-fence",
    },
    "guard-verification-boundaries": {
        "-Root": "--root", "-SkipCoverageWalk": "--skip-coverage-walk", "-Report": "--report",
        "-PlanOnly": "--plan-only",
    },
    "guard-test-substrate": {
        "-Root": "--root", "-BaselinePath": "--baseline-path", "-UpdateBaseline": "--update-baseline",
    },
}
# A PowerShell HOST flag ends the invocation; past it, the rest of the line belongs to another command.
HOST_FLAGS = ("-NoProfile", "-ExecutionPolicy", "-Bypass", "-Command", "-File", "-WorkingDirectory",
              "-ErrorAction")


class HalfRename(Exception):
    """A line that would name a `.py` tool with a PowerShell flag."""


def rewrite_arguments(line: str, stem: str) -> str:
    """Return `line` with this stem's PowerShell ARGUMENTS rewritten for the Python tool.

    Raises `HalfRename` rather than guessing when a PowerShell HOST flag follows the tool, because past
    that point the remaining flags are another command's. Reporting and skipping is right; guessing is
    not, and an earlier version of this gate aborted the whole run instead of skipping one line - a
    safety valve that became a blocker, with 200 files left unprocessed.
    """
    spellings = FLAG_SPELLINGS.get(stem)
    if not spellings:
        return line
    marker = f"{stem}.py"
    index = line.find(marker)
    if index == -1:
        return line
    cut = index + len(marker)
    head, tail = line[:cut], line[cut:]

    def replace(match: re.Match) -> str:
        token = match.group(1)
        if token in HOST_FLAGS:
            raise HalfRename(token)
        return spellings.get(token, token)

    return head + re.sub(r"(?<![\w-])(-[A-Za-z][A-Za-z]*)", replace, tail)


def linked_worktrees(repo: Path) -> list[Path]:
    """Absolute paths of every linked worktree except the main checkout.

    Read from `git worktree list` rather than hard-coding `.claude/worktrees`, because that directory
    is one tool's convention and the pool also holds worktrees under the temp directory. Fails
    CLOSED: if git cannot be asked the answer is UNKNOWN, and the caller refuses rather than assuming
    the list is empty - which is exactly the assumption behind the 1,180-file incident.
    """
    import subprocess
    try:
        proc = subprocess.run(["git", "worktree", "list", "--porcelain"], cwd=str(repo),
                              capture_output=True, text=True, timeout=WORKTREE_LIST_TIMEOUT)
    except (OSError, subprocess.SubprocessError) as exc:
        raise Refusal("WORKTREE-LIST-UNAVAILABLE",
                      "cannot ask git which trees are linked worktrees, so this sweep cannot tell "
                      f"its own tree from another session's: {exc}") from exc
    if proc.returncode != 0:
        raise Refusal("WORKTREE-LIST-UNAVAILABLE",
                      f"git worktree list failed ({proc.returncode}): {proc.stderr.strip()[:200]}")
    main: Path | None = None
    others: list[Path] = []
    for line in proc.stdout.splitlines():
        if line.startswith("worktree "):
            path = Path(line[len("worktree "):].strip()).resolve()
            if main is None:
                main = path
            elif path != main:
                others.append(path)
    return others


def worktree_of(path: Path, worktrees: list[Path]) -> Path | None:
    """The worktree root containing `path`, or None.

    Compared on resolved path COMPONENTS, not on string prefixes, because `C:/x/wt2` is a string
    prefix of `C:/x/wt20` while being no kind of parent of it - a prefix test would exclude an
    unrelated tree and still miss nothing, which is the sort of quiet wrong answer this tool exists
    to stop producing.
    """
    try:
        resolved = path.resolve()
    except OSError:
        return None
    for wt in worktrees:
        if resolved == wt or wt in resolved.parents:
            return wt
    return None


class Refusal(Exception):
    """A named precondition failure. Nothing is written when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason + (f"\n{detail}" if detail else ""))
        self.reason, self.detail = reason, detail


def _rules(stem: str, target: str | None = None) -> list[tuple[str, re.Pattern[str], str]]:
    """(rule-name, pattern, replacement) for one retired stem.

    Order matters: the invocation forms are matched before the bare citation form, so a
    `powershell -File scripts/x.ps1` is rewritten as a command rather than leaving a stray
    `powershell -File scripts/x.py` behind - which is the mistake a naive swap makes.

    `target` is the SUCCESSOR's stem when it differs from the retired one - see the `old=new` form in
    the module docstring. Every PATTERN matches the retired stem (that is what exists in the documents)
    and every REPLACEMENT uses the target, so the two never have to be the same string.
    """
    out = target or stem
    return [
        # `pwsh -NoProfile -Command "& './scripts/x.ps1'"` - a THIRD invocation shape, found by the
        # guard-power sweep, which is why there are three and not two.
        #
        # Neither the `-File` rules below nor the bare-path rules match it: the wrapper is `-Command`
        # with a QUOTED path, and the `./` prefix defeats `slash-invocation`'s lookbehind. Fixing only
        # the path would leave `pwsh -NoProfile -Command "& './scripts/x.py'"` - PowerShell handed a
        # Python file, which is worse than the stale citation it replaced because it READS as updated.
        # So the whole invocation is rewritten, and this rule must stay first.
        ("powershell-command-string",
         re.compile(rf"(?i)\b(?:pwsh|powershell)(?:\s+-\w+(?:[= ][^\s]+)?)*\s+-Command\s+"
                    rf"[\"']?&\s*[\"']?(?:\./)?scripts[\\/]{re.escape(stem)}\.ps1[\"']*"),
         f"python scripts/{out}.py"),
        # `pwsh ... -File <path>.ps1` and `powershell -File <path>.ps1`, with either separator.
        ("powershell-file-sep",
         re.compile(rf"(?i)\b(?:pwsh|powershell)(?:\s+-\w+(?:[= ][^\s]+)?)*\s+-File\s+"
                    rf"(?:\./)?scripts[\\/]{re.escape(stem)}\.ps1"),
         f"python scripts/{out}.py"),
        ("powershell-file-bare",
         re.compile(rf"(?i)\b(?:pwsh|powershell)(?:\s+-\w+(?:[= ][^\s]+)?)*\s+-File\s+"
                    rf"{re.escape(stem)}\.ps1"),
         f"python scripts/{out}.py"),
        # A bare `.\scripts\x.ps1` invocation line, on its own or after other `;`-separated commands.
        ("windows-invocation",
         re.compile(rf"(?i)\.\\scripts\\{re.escape(stem)}\.ps1"),
         f"python scripts/{out}.py"),
        # A forward-slash invocation without the leading dot, e.g. `scripts/x.ps1` used as a command.
        # `(?<!\./)` before the alternation: a `./` prefix is a legitimate way to write the path and
        # the lookbehind was excluding it, which is what let the `-Command` form through to the bare
        # rules and then match nothing at all.
        ("slash-invocation",
         re.compile(rf"(?i)(?<![\w/])(?:\./)?scripts/{re.escape(stem)}\.ps1"),
         f"scripts/{out}.py"),
        # A citation that CARRIES A DIRECTORY, with any depth: `scripts/x.ps1` and
        # `.claude/cmdc-agents/scripts/x.ps1` both. Neither existing rule matched either form - the bare
        # rule's `(?<![\w/])` lookbehind rejects a stem preceded by a separator, and the slash rule
        # needs the `scripts/` tail to also be preceded by a non-path character - so a fully-qualified
        # citation survived the sweep while the audit kept reporting it. The directory is captured and
        # re-emitted, because the successor lives in the same directory as the file it replaces.
        ("path-with-directory",
         re.compile(rf"(?i)((?:[\w.\-]+[/\\])*scripts[/\\]){re.escape(stem)}\.ps1"),
         r"\g<1>" + f"{out}.py"),
        # A prose mention of the bare file name.
        ("bare-mention",
         re.compile(rf"(?<![\w/]){re.escape(stem)}\.ps1"),
         f"{out}.py"),
    ]


def check_preconditions(root: Path, stem: str, target: str | None = None) -> None:
    """Refuse rather than mint a pointer to nothing.

    Both the retired source and its successor are resolved across `TOOL_DIRS`, not just `scripts/`.
    Two tools live in `.claude/cmdc-agents/scripts/`, and a check that only looked in `scripts/`
    reported "scripts/accept_lane.py does not exist" for a file sitting right there under a different
    directory - a refusal naming a path the reader cannot verify, which is the opposite of what a named
    refusal is for. The refusal now lists every directory it searched.
    """
    old = next((root / directory / f"{stem}.ps1" for directory in TOOL_DIRS
                if (root / directory / f"{stem}.ps1").exists()), None)
    successor = target or stem
    new = next((root / directory / f"{successor}.py" for directory in TOOL_DIRS
                if (root / directory / f"{successor}.py").is_file()), None)
    if old is not None:
        raise Refusal("SOURCE-STILL-PRESENT",
                      f"{old.relative_to(root).as_posix()} still exists. Delete it BEFORE sweeping, "
                      f"or the docs will point at a .py nobody runs while the .ps1 still is.")
    if new is None:
        searched = ", ".join(f"{directory}/{successor}.py" for directory in TOOL_DIRS)
        raise Refusal("PORT-MISSING",
                      f"no tracked {successor}.py in any tool directory ({searched}), so there is "
                      f"nothing for the citations to point at.")
def _walk(base: Path, worktrees: list[Path] | None, suffixes: tuple[str, ...],
          excluded: dict[str, int]) -> list[Path]:
    """Candidate files under `base`, never descending into a linked worktree.

    PRUNED, not filtered. The first version called `rglob` and then discarded anything inside a
    worktree, which walked ~20 other sessions' complete checkouts - including their `bin` and `obj` -
    before throwing the result away. The exclusion suite took 14m44s because of it. Pruning at the
    directory level is both the fast answer and the safe one: a directory that is never entered
    cannot be written to, whatever the file loop does afterwards.

    A worktree met on the way is named in the envelope, so the run cannot read as full coverage of a
    tree it never read. It is NOT given a file count, and that is deliberate: the first version
    counted the files inside each worktree in order to report "candidates left alone", which meant
    walking the very trees the guard exists to avoid - 63 seconds, and a number obtained by reading a
    tree the run had refused to read. A figure derived that way is not evidence about that tree, and
    publishing it as though it were is the same class of error as quoting a population.
    """
    import os
    found: list[Path] = []
    for dirpath, dirnames, filenames in os.walk(base):
        here = Path(dirpath)
        if worktrees:
            holder = worktree_of(here, worktrees)
            if holder is not None:
                excluded.setdefault(holder.as_posix(), None)
                # `dirnames[:] = []` stops the walk HERE rather than after the fact.
                dirnames[:] = []
                continue
        for name in filenames:
            if Path(name).suffix.lower() in suffixes:
                found.append(here / name)
    return found


def plan(root: Path, stems: list[str], scan_roots: list[Path],
          suffixes: tuple[str, ...] = DEFAULT_SUFFIXES,
          worktrees: list[Path] | None = None, *,
          targets: dict[str, str] | None = None) -> dict:
    """Compute the rewrite without writing. Returns the machine-readable plan.

    `worktrees` are another session's working trees, excluded from the candidate set and REPORTED.
    An empty list means "none known"; `None` means "not asked", and the caller is expected to have
    asked, because a sweep that cannot tell its own tree from someone else's is the 1,180-file
    incident waiting to recur.
    """
    files: list[dict] = []
    totals: dict[str, int] = {}
    excluded: dict[str, int] = {}
    for stem in stems:
        target = (targets or {}).get(stem)
        totals.setdefault(stem, 0)
        rules = _rules(stem, (targets or {}).get(stem))
        for base in scan_roots:
            candidates = ([base] if base.is_file()
                          else sorted(_walk(base, worktrees, suffixes, excluded)))
            for path in candidates:
                if not path.is_file():
                    continue
                if path.stem == stem:
                    # The PORT IS NOT A CITATION. `guard-single-writer.py` opens with "Replaces
                    # `guard-single-writer.ps1`", and that sentence is the provenance the port
                    # standard requires so the reason for the deletion survives it. Rewriting it
                    # would leave a guard whose module docstring no longer says what it replaced -
                    # measured on this tool during the guard-single-writer port, where the plan
                    # wanted to rewrite the port it was sweeping. Skip the file whose stem IS the
                    # tool, and REPORT the skip rather than letting it look like full coverage.
                    files.append({"file": path.as_posix(), "skipped": "port-provenance"})
                    continue
                try:
                    text = original = path.read_text(encoding="utf-8")
                except (OSError, UnicodeDecodeError):
                    # A file this tool cannot read is reported, never silently skipped: a sweep
                    # that quietly skips is a sweep whose coverage nobody knows.
                    files.append({"file": path.as_posix(), "error": "unreadable"})
                    continue
                per_rule: dict[str, int] = {}
                for name, pattern, replacement in rules:
                    text, count = pattern.subn(replacement, text)
                    if count:
                        per_rule[name] = per_rule.get(name, 0) + count
                # THE GATE. The rules above rewrite PATHS and know nothing about ARGUMENTS, so a line
                # that named `-Report` still would after them, and the file would go out holding
                # `python <tool>.py -Report` - a command the tool rejects. The arguments are rewritten
                # HERE, by the tool that knows the dialect, and a line it must not guess at is left
                # untouched and REPORTED rather than written half-true.
                argument_hits, skipped_argument_lines = 0, 0
                if stem in FLAG_SPELLINGS:
                    rebuilt = []
                    for line in text.splitlines(keepends=True):
                        try:
                            rewritten = rewrite_arguments(line, stem)
                        except HalfRename:
                            skipped_argument_lines += 1
                            rebuilt.append(line)
                            continue
                        if rewritten != line:
                            argument_hits += 1
                        rebuilt.append(rewritten)
                    text = "".join(rebuilt)
                if text != original:
                    changed_lines = sum(
                        1 for a, b in zip(original.splitlines(), text.splitlines()) if a != b
                    )
                    for name, count in per_rule.items():
                        totals[name] = totals.get(name, 0) + count
                    totals[stem] = totals.get(stem, 0) + sum(per_rule.values())
                    if argument_hits:
                        per_rule["arguments"] = per_rule.get("arguments", 0) + argument_hits
                    files.append({
                        "file": path.as_posix(),
                        "changed_lines": changed_lines,
                        "rules": per_rule,
                        **({"arguments-left-alone": skipped_argument_lines}
                           if skipped_argument_lines else {}),
                        "_new": text,
                    })
    return {"files": files, "totals": totals, "excluded": excluded}


def apply_plan(planned: dict) -> int:
    """Write the planned rewrites. An entry is written only if it carries the new text.

    A planned entry has THREE shapes, not one: a rewrite (carries `_new`), an unreadable file, and
    a deliberate skip. Keying on `"_new" in entry` rather than on the absence of `"error"` is what
    makes the third shape safe: the first version skipped on `"error"` alone, so a deliberate skip
    reached `entry.pop("_new")` and raised KeyError mid-run - leaving the sweep half applied and
    the remaining citations stale, with the exception as the only report.
    """
    written = 0
    for entry in planned["files"]:
        if "_new" not in entry:
            continue
        path = REPO_ROOT / entry["file"]
        path.write_text(entry.pop("_new"), encoding="utf-8")
        written += 1
    return written


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Rewrite doc citations when a .ps1 tool is retired to its .py port.")
    parser.add_argument("--map", dest="stems", action="append", required=True, metavar="STEM",
                        help="a retired tool stem, e.g. guard-dal (repeatable). Use OLD=NEW when the "
                             "successor's name differs, e.g. accept-lane=accept_lane for the two "
                             "snake_case module ports that predate the kebab-case rule")
    parser.add_argument("--root", dest="roots", action="append", default=None, metavar="PATH",
                        help=f"file or directory to sweep (repeatable; default: {' '.join(DEFAULT_ROOTS)})")
    parser.add_argument("--apply", action="store_true",
                        help="write the changes. Omitted, this is a dry run.")
    parser.add_argument("--suffix", dest="suffixes", action="append", default=None,
                        metavar=".EXT",
                        help=f"file extension to sweep (repeatable; default: {' '.join(DEFAULT_SUFFIXES)})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    scan_roots = [Path(r) for r in args.roots] if args.roots else [REPO_ROOT / r for r in DEFAULT_ROOTS]
    missing = [r for r in scan_roots if not r.exists()]
    if missing:
        # `--json` had to be honoured here too. Four of the five refusals emitted an envelope and
        # this one did not, so a machine caller that passed `--json` got an EMPTY stdout and a
        # non-zero exit - which is indistinguishable from "the sweep found nothing to do", the exact
        # silent-green reading the port standard forbids. A refusal that only reaches stderr is the
        # PowerShell capture trap wearing a Python costume.
        refusal = Refusal("ROOT-MISSING", ", ".join(str(m) for m in missing))
        print(f"PS1-RENAME-SWEEP REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return 1

    # `--map` takes a bare stem or OLD=NEW. The split lives here rather than in argparse so a stem is
    # never silently reinterpreted, and an empty target is refused by name instead of deriving
    # `<stem>.py` and failing later with PORT-MISSING, which would name the wrong thing.
    stems: list[str] = []
    targets: dict[str, str] = {}
    for entry in args.stems:
        retired, separator, successor = entry.partition("=")
        if separator and not successor:
            refusal = Refusal("MAP-TARGET-EMPTY", f"--map {entry} names no successor")
            print(f"PS1-RENAME-SWEEP REFUSED {refusal}", file=sys.stderr)
            if args.json:
                print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                                  "detail": refusal.detail}, indent=2))
            return 1
        stems.append(retired)
        if separator:
            targets[retired] = successor

    for stem in stems:
        try:
            check_preconditions(REPO_ROOT, stem, targets.get(stem))
        except Refusal as refusal:
            print(f"PS1-RENAME-SWEEP REFUSED {refusal}", file=sys.stderr)
            if args.json:
                print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                                  "detail": refusal.detail}, indent=2))
            return 1

    # Ask git which trees are LINKED WORKTREES before touching anything, and refuse outright if a
    # --root points into one: being handed a path inside someone else's working tree is a mistake
    # worth stopping on, not a coverage reduction worth reporting after the fact.
    try:
        # The repository that owns what we were pointed at, NOT the one this script lives in. See
        # `repository_of`: the previous `linked_worktrees(REPO_ROOT)` was right only by coincidence.
        target_repo = repository_of(scan_roots[0] if scan_roots else REPO_ROOT)
        if target_repo is None:
            # Not a repository: there are provably no linked worktrees here, so the sweep serves the
            # request with an empty list rather than refusing. Sweeping a documentation directory that
            # happens to sit outside any checkout is a legitimate request, and the previous version only
            # worked there by accident - it always asked about ITS OWN repository.
            worktrees = []
        else:
            worktrees = linked_worktrees(target_repo)
        foreign = [r for r in scan_roots if worktree_of(r.resolve(), worktrees) is not None]
    except Refusal as refusal:
        print(f"PS1-RENAME-SWEEP REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return 1
    if foreign:
        refusal = Refusal("ROOT-IS-A-LINKED-WORKTREE",
                          ", ".join(f"{r} (inside {worktree_of(r.resolve(), worktrees)})"
                                    for r in foreign))
        print(f"PS1-RENAME-SWEEP REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return 1

    suffixes = tuple(args.suffixes) if args.suffixes else DEFAULT_SUFFIXES
    planned = plan(REPO_ROOT, stems, scan_roots, suffixes, worktrees, targets=targets)
    # Three kinds of file, and the summary has to keep them apart. A sweep that reports one total
    # for "files it considered" makes a SKIP indistinguishable from a change, which is how a
    # caller concludes full coverage from a partial pass.
    changed = [f for f in planned["files"] if "error" not in f and "skipped" not in f]
    unreadable = [f for f in planned["files"] if "error" in f]
    skipped = [f for f in planned["files"] if "skipped" in f]
    written = apply_plan(planned) if args.apply else 0

    result = {
        "verdict": "APPLIED" if args.apply else "DRY-RUN",
        "stems": args.stems,
        "roots": [r.as_posix() for r in scan_roots],
        "files_changed": len(changed),
        "lines_changed": sum(f.get("changed_lines", 0) for f in changed),
        "substitutions": planned["totals"],
        "files_skipped": [{"file": f["file"], "reason": f["skipped"]} for f in skipped],
        # Said out loud, with a count per tree, because "the sweep covered docs/" is a claim about
        # the WHOLE tree and this is the number that makes it checkable.
        "worktrees_excluded": [{"worktree": k, "action": "pruned-not-entered"}
                               for k in sorted(planned["excluded"])],
        "files_unreadable": [f["file"] for f in unreadable],
        "files": [{k: v for k, v in f.items() if k != "_new"} for f in planned["files"]],
    }

    if args.json:
        print(json.dumps(result, indent=2))
    else:
        mode = "APPLIED" if args.apply else "DRY RUN (pass --apply to write)"
        print(f"ps1-rename-sweep {mode}: {result['files_changed']} file(s), "
              f"{result['lines_changed']} line(s), {sum(v for k, v in planned['totals'].items() if not k.endswith('.ps1'))} substitution(s)")
        for name, count in sorted(planned["totals"].items()):
            print(f"  {name:<28} {count}")
        if unreadable:
            print(f"  {len(unreadable)} file(s) UNREADABLE and therefore not swept: "
                  f"{', '.join(f['file'] for f in unreadable)}", file=sys.stderr)
        if skipped:
            # Said out loud, because a silent skip reads as full coverage. The port's own
            # docstring names the .ps1 it replaced ON PURPOSE - that sentence is the provenance -
            # so "not rewritten" is the correct outcome and must not look like an oversight.
            print(f"  {len(skipped)} file(s) deliberately NOT rewritten: "
                  f"{', '.join(f['file'] for f in skipped)} (port provenance)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
