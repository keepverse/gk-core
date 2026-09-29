#!/usr/bin/env python3
"""Retire worktrees that are BOTH merged and finished — the step that keeps being forgotten.

`gk-core/scripts/worktree_cleanup_core.py` decides whether a worktree is *safe to recycle* and writes a
marker. This tool answers the question that comes after the merge: **which finished worktrees may
now be removed outright**, and refuses everything else. It exists because merged-and-finished
worktrees were accumulating by the hundred - documentation updated and merged, branch integrated,
worktree never removed.

It is deliberately stricter than the marker tool. A worktree is retirable only when EVERY one of
these is proven, and each proof is a separate, reported reason:

  1. **Not owned by a live session.** Any `status: active` record in `tasks/sessions/*.json` that
     names the worktree path owns it. This is the check the marker tool's own acceptance could not
     prove: its tests drive synthetic lane records, never a real running lane, so this tool reads the
     tracked records directly rather than trusting a runner registry.
  2. **Not held by a live runner.** A `locked` worktree is never retirable, and neither is one whose
     branch a lane registry names *when a tracked session record corroborates that claim*. An
     uncorroborated claim does not block: those registries are untracked and unpruned, so an absolute
     veto is a one-way ratchet. Measured 2026-09-26: all 83 registry entries were uncorroborated, and
     the 80 branches they held had no unmerged patch - the pile was bookkeeping, not work.
  3. **Integrated.** The branch is fully contained in the integration branch (`git cherry` reports no
     unique patch), so nothing is lost by removing the checkout.
  4. **No local work.** `git status --porcelain` is empty. Untracked files count as local work: a
     generated corpus nobody merged is exactly the case that must NOT be deleted silently.
  5. **Its own session record is closed.** If a record exists for the worktree's branch and it is
     still `active`, refuse. A record that is `merged` or `abandoned` is history.

Read-only by default: it prints a plan and exits 0. Nothing is removed without `--apply`, and
`--apply` still refuses every refusal above. Every git call carries a hard timeout, every refusal is
named, and the result is machine-readable with `--json`.

Usage:
    python gk-core/scripts/retire_worktrees.py                     # plan, markdown
    python gk-core/scripts/retire_worktrees.py --json              # plan, machine-readable
    python gk-core/scripts/retire_worktrees.py --why <branch>      # why one worktree is or is not retirable
    python gk-core/scripts/retire_worktrees.py --apply             # remove the retirable ones
    python gk-core/scripts/retire_worktrees.py --apply --dry-run   # say what --apply would do, change nothing
    python gk-core/scripts/retire_worktrees.py --timeout 20
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import subprocess
import sys
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
DEFAULT_INTEGRATION = "features/mega-merge"
DEFAULT_TIMEOUT = 60.0
SESSION_STATUSES = ("active", "merged", "abandoned")

#: Named refusals. Each prints as `RETIRE REFUSED: <name> - <meaning>` and exits 2. A tool that can
#: print an empty plan which reads as "nothing to do" is a defect, so an empty inventory refuses.
REFUSALS = {
    "NO-WORKTREES": "git listed no worktree other than the main checkout",
    "NO-SESSIONS": "tasks/sessions/*.json matched no record, so ownership cannot be proven",
    "GIT-UNAVAILABLE": "git could not produce trustworthy worktree state",
    "TIMEOUT": "an external command exceeded its hard timeout",
    "NEGATIVE-TIMEOUT": "--timeout must be positive",
    "BAD-INTEGRATION-REF": "the integration branch does not exist in this repository",
    "POOL-NOT-A-DIRECTORY": "a pool declared with --pool does not exist, so it was never "
                           "scanned and a clean result would read as coverage that did not "
                           "happen",
    "POOL-IS-THE-ROOT": "a pool declared with --pool is the repository root, so scanning it "
                        "would sweep in the whole checkout",
    "POOL-NOT-A-DIRECTORY": "a pool declared with --pool does not exist, so it was never "
                           "scanned and a clean result would read as coverage that did not "
                           "happen",
    "POOL-IS-THE-ROOT": "a pool declared with --pool is the repository root, so scanning it "
                        "would sweep in the whole checkout",
    "NO-WORKTREE-PARENT": "no registered worktree shares a parent with the main checkout, so "
                          "there is no directory to scan for unregistered leftovers",
    "DIR-SCAN-FAILED": "a candidate leftover directory could not be listed, so the inventory "
                       "would be silently incomplete",
}


class Refusal(RuntimeError):
    def __init__(self, name: str, detail: str) -> None:
        if name not in REFUSALS:
            raise KeyError(f"unnamed refusal: {name}")
        super().__init__(f"{name}: {detail}")
        self.name = name
        self.detail = detail


# ---- git, read-only, hard timeout ---------------------------------------------------------------

def git(*args: str, timeout: float, cwd: Path | None = None) -> tuple[int, str]:
    try:
        result = subprocess.run(
            ["git", "-C", str(cwd or REPO), *args], capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("TIMEOUT", f"git {' '.join(args[:3])} exceeded {timeout:g}s") from exc
    return result.returncode, (result.stdout or "").strip()


def git_diag(*args: str, timeout: float, cwd: Path | None = None) -> tuple[int, str]:
    """`git` for a command whose FAILURE must be explainable.

    A removal tool that prints `FAILED <path>: []` is worse than useless: the operator cannot act on an
    empty reason, and the natural conclusion is that the tool is broken rather than that git declined.
    This variant keeps stderr, and is used only where a non-zero exit is a result to be reported
    rather than a value to be parsed.
    """
    try:
        result = subprocess.run(
            ["git", "-C", str(cwd or REPO), *args], capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("TIMEOUT", f"git {' '.join(args[:3])} exceeded {timeout:g}s") from exc
    return result.returncode, ((result.stderr or result.stdout) or "").strip()


# ---- session records: the ownership proof this tool does not delegate -----------------------------

@dataclass(frozen=True)
class Session:
    session: str
    status: str
    branch: str
    worktree: str | None
    record: str


def load_sessions(repo: Path) -> list[Session]:
    out: list[Session] = []
    for path in sorted((repo / "tasks" / "sessions").glob("*.json")):
        if path.name.startswith("_"):  # `_template.json` is a template, not a record
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            # A record this tool cannot read is an OWNERSHIP it cannot prove. Skipping it would make
            # its worktree look unowned, which is the exact fail-open this tool exists to prevent.
            raise Refusal("NO-SESSIONS", f"{path.relative_to(repo).as_posix()} does not parse")
        status = data.get("status")
        if status not in SESSION_STATUSES:
            raise Refusal("NO-SESSIONS",
                          f"{path.relative_to(repo).as_posix()}: status {status!r} is outside "
                          f"{SESSION_STATUSES}")
        out.append(Session(session=str(data.get("session") or path.stem), status=status,
                           branch=str(data.get("branch") or ""),
                           worktree=(str(data["worktree"]) if data.get("worktree") else None),
                           record=path.relative_to(repo).as_posix()))
    if not out:
        raise Refusal("NO-SESSIONS", "no session record found, so no worktree can be proven unowned")
    return out


def _same_path(a: str | Path, b: str | Path) -> bool:
    try:
        return Path(a).resolve() == Path(b).resolve()
    except OSError:
        return str(a).replace("\\", "/").rstrip("/") == str(b).replace("\\", "/").rstrip("/")


# ---- lane registries: a live runner is an owner -------------------------------------------------

def acceptance_evidence_under(root: Path, worktree: Path) -> dict[str, str]:
    """Acceptance lanes whose raw evidence path resolves INSIDE this worktree -> {lane: logDir}.

    The acceptance artefacts are tracked and live at `<root>/.claude/cmdc-agents/acceptance/*.json`.
    Their `logDir` is relative, so the only sound test is: does `<worktree>/<logDir>` exist? A truthy
    answer means removing the worktree would delete evidence a verdict rests on.

    It also reads `contendedTree` only when it is a STRING. That field is a boolean in the current
    schema, and an earlier audit of mine treated it as a path and reported "0 missing" for a check
    that had not run - so the type is checked rather than assumed.
    """
    out: dict[str, str] = {}
    directory = root / ".claude" / "cmdc-agents" / "acceptance"
    if not directory.is_dir():
        return out
    for artefact in sorted(directory.glob("*.json")):
        try:
            doc = json.loads(artefact.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError):
            continue  # an unreadable artefact must not silently authorise a removal
        lane = str(doc.get("lane") or artefact.stem)
        for field in ("logDir", "contendedTree"):
            value = doc.get(field)
            if not isinstance(value, str) or not value or Path(value).is_absolute():
                continue
            if (worktree / value).exists():
                out[lane] = value
                break
    return out


#: Cap on entries walked per candidate directory. A full walk of the 104 leftovers measured
#: ~2M entries and cost minutes; the plan only needs to distinguish "empty" from "holds
#: something", and a bounded walk that says so beats an unbounded one that hangs.
DIR_SCAN_ENTRY_CAP = 20000

#: A worktree with a file written inside this window is treated as IN FLIGHT and never retirable.
#: Two hours is deliberately generous: the cost of a false positive is one plan line a human reads,
#: and the cost of a false negative is deleting a live agent's working directory.
RECENT_ACTIVITY_SECONDS = 2 * 60 * 60

#: Cap on files stat'd when measuring recent activity. `.git` is skipped because reading it rewrites
#: its own mtimes, which would make every worktree look permanently fresh.
_RECENT_SCAN_CAP = 40000


def _newest_mtime(root: Path, timeout: float) -> float | None:
    """Newest real content mtime under `root`, ignoring `.git`, or None if unreadable.

    `.git` must be filtered from BOTH lists, and that is not a detail: in a LINKED worktree `.git`
    is a FILE - a pointer into the main checkout's `.git/worktrees/<name>` - so it arrives in
    `filenames`, not `dirnames`. Filtering only `dirnames` counted git's own pointer file, which git
    rewrites on ordinary operations, and every linked worktree then read as freshly written. The
    first version of this guard had that bug and blocked all 8 retirable-path tests at once, which
    is how it was found.
    """
    newest: float | None = None
    seen = 0
    try:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d != ".git"]
            for name in filenames:
                if name == ".git":
                    continue
                seen += 1
                if seen > _RECENT_SCAN_CAP:
                    return newest
                try:
                    m = os.stat(os.path.join(dirpath, name)).st_mtime
                except OSError:
                    continue
                if newest is None or m > newest:
                    newest = m
    except OSError:
        return None
    return newest


@dataclass(frozen=True)
class UnregisteredDir:
    """A directory under the worktree root that `git worktree list` does not report.

    These are invisible to the worktree survey by construction: git forgot them. Two shapes were
    measured on 2026-09-26, both from a `git worktree remove` that de-registered the worktree and
    then failed to delete the directory - once on a Windows long path, once on permissions. Neither
    is reclaimable through git afterwards, and neither appears in any worktree listing, so a tool
    that surveys `git worktree list` reports a clean tree while N leftovers sit on disk.

    The 28 empty ones are litter, and they are reclaimed by `reclaim_empty_leftovers` - a directory
    with nothing in it cannot hold unlanded work, so that class needs no adjudication. The 3 that
    still held content are the reason this class exists: one held 451 lines across 9 files that
    exist nowhere else in the repository. Those this tool REPORTS and never removes, because deciding
    that an unregistered directory holding content is disposable needs a content adjudication, not
    a path test.
    """

    name: str
    path: str
    entries: int
    capped: bool
    newest_utc: str | None
    has_git: bool


#: Why a provably-empty leftover could not be reclaimed. These are named rather than collapsed into
#: one "could not remove" because they need different responses: a LOCKED directory needs its holder
#: released or an owner decision, a PATH-TOO-LONG one needs the extended-length prefix, and a
#: PERMISSION one needs an ACL. Reporting one bucket for all three is how a known gap stays a gap.
#: A leftover that still HOLDS FILES is a different problem from an empty one, and it has its own
#: tool. `git worktree remove` de-registers first and deletes second, so a long-path failure leaves a
#: directory full of files that this tool enumerates and correctly refuses to remove - 14,392 of them,
#: measured 2026-09-27 on `opencode-resume-28b-...`. `gk-core/scripts/reclaim_worktree_dir.py` closes that: the
#: manager adjudicates the unlanded paths and names them, and the tool proves every OTHER file's content
#: already exists in integration's history before deleting anything. Named here so the next reader is
#: sent to it rather than re-deriving the gap.
STALE_DIR_TOOL = 'scripts/reclaim_worktree_dir.py'

RECLAIM_REFUSALS = {
    "LOCKED": "a live process holds a handle on the directory, so the OS refuses the delete",
    "PATH-TOO-LONG": "the path exceeds the legacy 260-character limit; the \\\\?\\ prefix is the fix",
    "PERMISSION": "the ACL denies delete; this is not a content question",
    "NOT-EMPTY": "the directory gained an entry after the scan, so it is no longer provably empty",
    "GONE": "already absent - reclaimed by something else between the scan and the attempt",
}


def _classify_reclaim_error(exc: OSError) -> str:
    """Name the reason a delete was refused, from the OS's own code rather than the message text.

    Matching on the message is how a tool ends up reporting "could not remove" for a locked
    directory, a long path and a denied ACL alike - three different problems with three different
    fixes, reported as one.

    `winerror` is consulted first and exclusively when it is set, because it is the authoritative
    code and `errno` is ambiguous across these cases: on Windows a sharing violation surfaces as
    `errno` 13, which is the same number as a POSIX `EACCES`. Reading `errno` first classified a
    genuine `ERROR_ACCESS_DENIED` as LOCKED, and the `errno` fallback only runs when there is no
    `winerror` to read.
    """
    win = getattr(exc, "winerror", None)
    err = exc.errno
    if win is not None:
        if win in (32, 33):                       # ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
            return "LOCKED"
        if win == 206:                            # ERROR_FILENAME_EXCED_RANGE
            return "PATH-TOO-LONG"
        if win == 5:                              # ERROR_ACCESS_DENIED
            return "PERMISSION"
        if win == 145:                            # ERROR_DIR_NOT_EMPTY
            return "NOT-EMPTY"
        return f"UNCLASSIFIED(winerror={win}): {exc}"
    if err in (13, 16):                           # EACCES / EBUSY, where there is no winerror
        return "PERMISSION" if err == 13 else "LOCKED"
    if err == 36:                                # ENAMETOOLONG
        return "PATH-TOO-LONG"
    if err in (1,):                              # EPERM
        return "PERMISSION"
    if err == 39:                                # ENOTEMPTY
        return "NOT-EMPTY"
    return f"UNCLASSIFIED({err}): {exc}"


def reclaim_empty_leftovers(leftovers: Iterable[UnregisteredDir], apply: bool) -> list[dict[str, Any]]:
    """Remove unregistered worktree directories that are **provably empty**, and name why the rest stay.

    A leftover with zero entries anywhere beneath it cannot hold unlanded work - there is nothing in
    it. That is a proof, not a judgement, which is why this class does not need the content
    adjudication the non-empty leftovers still require. `Path.rmdir` supplies the second,
    independent proof: it only succeeds on a directory the OS also considers empty, so a directory
    that filled up between the scan and the attempt fails here rather than deleting real work.

    A `capped` scan is refused outright. The entry cap exists so a huge directory cannot stall the
    walk, and a capped count of zero is not evidence of emptiness - it is evidence the walk stopped.
    Treating it as empty would delete on the strength of a number that was never finished counting.

    Without `apply` this is a plan. With it, each refusal is classified so the leftover that stays is
    a named problem rather than a silent one.
    """
    out: list[dict[str, Any]] = []
    for d in leftovers:
        rec: dict[str, Any] = {"name": d.name, "path": d.path, "entries": d.entries}
        if d.capped:
            rec["outcome"] = "REFUSED-SCAN-CAPPED"
            rec["detail"] = ("the walk hit its entry cap, so 'empty' was never established; "
                             "removing on a count that stopped counting would be a guess")
            out.append(rec)
            continue
        if d.entries != 0:
            rec["outcome"] = "KEPT-NOT-EMPTY"
            rec["detail"] = "holds content; needs a content adjudication, not a path test"
            out.append(rec)
            continue
        rec["outcome"] = "WOULD-REMOVE"
        if not apply:
            out.append(rec)
            continue
        target = Path(d.path)
        try:
            target.rmdir()
            rec["outcome"] = "REMOVED"
        except FileNotFoundError:
            rec["outcome"] = "GONE"
        except OSError as first:
            # The extended-length prefix is the documented remedy for a path past 260 characters,
            # and it is tried before giving up so the long-path case is actually fixed rather than
            # merely named. `os.name` is checked because the prefix is a Windows convention.
            if os.name == "nt" and not str(d.path).startswith("\\\\?\\"):
                try:
                    os.rmdir("\\\\?\\" + os.path.abspath(d.path))
                    rec["outcome"] = "REMOVED-LONG-PATH"
                    out.append(rec)
                    continue
                except OSError as second:
                    first = second
            rec["outcome"] = _classify_reclaim_error(first)
            rec["detail"] = RECLAIM_REFUSALS.get(
                rec["outcome"], "not a classified refusal; see the error text")
        out.append(rec)
    return out


#: Verdict names `prove_stale_leftovers` emits. A class with no explanation is an unactionable bucket,
#: so each one says what it means and what the caller may do with it.
STALE_PROOF_VERDICTS = {
    "PROVABLY-STALE": (
        "every file's content already exists somewhere in integration's history, so the directory "
        "holds nothing unlanded; it is reclaimable by naming it to the reclaim tool, and no human "
        "judgement is required to know that"),
    "NEEDS-ADJUDICATION": (
        "at least one file's content is nowhere in integration's history, so the directory may hold "
        "real work and a manager must look at it before anything is deleted"),
    "CAPPED-NOT-PROVEN": (
        "the entry walk hit its cap, so the count that would have justified a verdict was never "
        "finished; a capped count is not evidence either way"),
    "HAS-GIT": (
        "the directory still holds a `.git` file, so it is registered or half-removed rather than a "
        "leftover, and the worktree survey is the right instrument for it"),
    "UNREADABLE": (
        "the directory could not be walked at all; a permission or a vanished path is not a verdict "
        "about content, and it is reported rather than counted as stale"),
    "PROOF-UNAVAILABLE": (
        f"the reclaim tool ({STALE_DIR_TOOL}) could not be loaded, so no content claim was made for "
        "any directory; this is a missing instrument, not a clean result"),
}


def _load_reclaim_tool(root: Path):
    """Import the reclaim tool by path, or return None with the reason.

    Imported rather than reimplemented: the reclaim tool's `audit_unlanded` is already the proof that
    every file's content exists in integration's history, and a second implementation of "is this
    content landed" is how the ignore query got defeated once already.
    """
    import importlib.util

    target = Path(root) / STALE_DIR_TOOL
    if not target.is_file():
        return None, f"{STALE_DIR_TOOL} is not present at {target}"
    try:
        spec = importlib.util.spec_from_file_location("reclaim_worktree_dir", target)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except Exception as exc:  # a broken sibling tool must not read as a clean proof
        return None, f"{STALE_DIR_TOOL} failed to import: {type(exc).__name__}: {exc}"
    if not hasattr(module, "audit_unlanded"):
        return None, f"{STALE_DIR_TOOL} exposes no audit_unlanded; the proof interface changed"
    return module, ""


def prove_stale_leftovers(root: Path, leftovers: Iterable[UnregisteredDir],
                          timeout: float) -> list[dict[str, Any]]:
    """Split content-holding leftovers into the ones that are provably stale and the ones that are not.

    Measured 2026-09-27, and this is why the function exists: the survey reported **37** unregistered
    leftovers and told a manager to adjudicate **33** of them. Running the reclaim tool's own proof
    over the same 37 said **4 empty, 10 provably stale, 23 needing adjudication** — so two thirds of
    the "needs a human" queue did not need one. A queue that is inflated by work the tool can already
    prove is a queue nobody clears, and the next survey finds the same pile again.

    Read-only by construction: it calls `audit_unlanded`, which hashes and compares, and the reclaim
    tool's removal path lives in a different function that is never reached from here.

    Every directory gets a verdict. A capped walk, a `.git` file, an unreadable path and a missing
    proof instrument are all reported as themselves, because the one thing worse than asking a manager
    to look at a directory is reporting it as clean because the instrument was missing.
    """
    out: list[dict[str, Any]] = []
    reclaim, why_not = _load_reclaim_tool(root)
    for d in sorted(leftovers, key=lambda x: -x.entries):
        # The two verdicts that need no instrument come first. An empty directory provably holds
        # nothing, and a directory still holding its `.git` file is registered or half-removed rather
        # than a leftover - neither is a question about content, so neither may be blocked by a
        # missing tool. Requiring the instrument for them would report "cannot prove" about two
        # directories whose answer was never in doubt.
        if d.entries == 0:
            out.append({"name": d.name, "path": d.path, "entries": 0,
                        "verdict": "PROVABLY-STALE", "unlanded": 0,
                        "detail": "empty; a directory with nothing in it cannot hold unlanded work"})
            continue
        if d.has_git:
            out.append({"name": d.name, "path": d.path, "entries": d.entries,
                        "verdict": "HAS-GIT", "unlanded": None,
                        "detail": STALE_PROOF_VERDICTS["HAS-GIT"]})
            continue
        if d.capped:
            out.append({"name": d.name, "path": d.path, "entries": d.entries,
                        "verdict": "CAPPED-NOT-PROVEN", "unlanded": None,
                        "detail": STALE_PROOF_VERDICTS["CAPPED-NOT-PROVEN"]})
            continue
        if reclaim is None:
            out.append({"name": d.name, "path": d.path, "entries": d.entries,
                        "verdict": "PROOF-UNAVAILABLE", "unlanded": None,
                        "detail": why_not})
            continue
        try:
            unlanded, walked, part = reclaim.audit_unlanded(Path(root), Path(d.path), [])
        except reclaim.Refusal as refusal:
            out.append({"name": d.name, "path": d.path, "entries": d.entries,
                        "verdict": f"REFUSED-{refusal.name}", "unlanded": None,
                        "detail": refusal.detail})
            continue
        except Exception as exc:  # OSError on a vanished path, a decode error on a locked file
            out.append({"name": d.name, "path": d.path, "entries": d.entries,
                        "verdict": "UNREADABLE", "unlanded": None,
                        "detail": f"{type(exc).__name__}: {exc}"})
            continue
        verdict = "PROVABLY-STALE" if not unlanded else "NEEDS-ADJUDICATION"
        out.append({"name": d.name, "path": d.path, "entries": d.entries,
                    "verdict": verdict, "unlanded": len(unlanded),
                    "filesWalked": walked,
                    "detail": STALE_PROOF_VERDICTS[verdict]})
    return out


def scan_unregistered_dirs(root: Path, registered: Iterable[str], timeout: float,
                           extra_pools: Iterable[str] = ()) -> list[UnregisteredDir]:
    """Find directories beside the worktrees that git no longer claims, including DECLARED pools.

    Deriving the pool set from the registered worktrees is also a way to UNDER-scan, and that is the
    worse direction. Measured 2026-09-27: once the last worktree was cleared from `.claude/worktrees`,
    that pool was no longer any registered worktree's parent, so it dropped out of the candidate set -
    and the four husks it still held became invisible to the tool whose entire job is to find them. The
    coverage of a cleanup tool shrank as a direct result of the cleanup working.

    So a caller may DECLARE a pool with `--pool`. A declared path is a stated location, not a guess,
    which is the property the derivation exists to keep: the tool still refuses to invent a directory,
    and the person who knows where this repository makes its worktrees says so.
    """
    known = {_same_path(p, root) for p in registered}


    def norm(p: str) -> str:
        """One comparable form for both sides.

        `os.path.abspath` normalizes the separators of the string it is HANDED, but a git-printed
        POSIX path (`D:/Works/...`) keeps its forward slashes all the way through `normcase` on
        Windows. Comparing that against a `Path` string with backslashes makes every registered
        worktree look unregistered - which is precisely the bug this scan exists to report, so it
        must not have the mirror image of it.
        """
        return os.path.normcase(
            os.path.abspath(str(p).replace("/", os.sep)).replace("\\", "/"))

    known_norm = {norm(p) for p in registered}
    # ONLY directories that git actually registered worktrees into are scanned, plus pools the caller
    # DECLARED. Deriving the candidate set some other way - the main checkout's own parent, say -
    # swept in 92 unrelated directories on this machine, including 28 whole sibling repositories
    # (`lore-weave-*`, `Keepverse`, `wabbajack`, `ComfyUI-GGUF`) that have nothing to do with this
    # repository and would have been listed as disposable litter. A leftover is only a leftover
    # relative to where worktrees are actually made.
    parents: set[Path] = set()
    for p in registered:
        if _same_path(p, root):
            continue
        cand = Path(p).parent
        if cand.is_dir() and not _same_path(cand, root):
            parents.add(cand.resolve())
    for raw in extra_pools:
        cand = Path(raw)
        if not cand.is_absolute():
            cand = Path(root) / cand
        # A declared pool that does not exist is a typo, and silently dropping it would restore the
        # very blind spot `--pool` exists to close: the caller would believe a pool was covered.
        if not cand.is_dir():
            raise Refusal("POOL-NOT-A-DIRECTORY",
                          f"declared pool {cand} does not exist or is not a directory; a typo here "
                          f"reads as a clean scan of a pool that was never looked at")
        if _same_path(cand, root):
            raise Refusal("POOL-IS-THE-ROOT",
                          f"declared pool {cand} is the repository root; scanning it would sweep in "
                          f"the whole checkout")
        parents.add(cand.resolve())
    if not parents:
        raise Refusal("NO-WORKTREE-PARENT",
                      "no registered worktree shares a parent directory with the main checkout and "
                      "no pool was declared with --pool, so there is no directory to scan for "
                      "leftovers this tool can trust")

    found: list[UnregisteredDir] = []
    for parent in sorted(parents):
        try:
            children = sorted(x for x in parent.iterdir() if x.is_dir())
        except OSError as exc:
            raise Refusal("DIR-SCAN-FAILED", f"{parent} could not be listed: {exc}") from exc
        for child in children:
            if norm(str(child)) in known_norm or _same_path(child, root):
                continue
            count = 0
            capped = False
            newest = 0.0
            has_git = (child / ".git").exists()
            for dirpath, dirnames, filenames in os.walk(child):
                dirnames[:] = [d for d in dirnames if d != ".git"]
                for name in list(dirnames) + filenames:
                    count += 1
                    if count >= DIR_SCAN_ENTRY_CAP:
                        capped = True
                        break
                    try:
                        m = os.stat(os.path.join(dirpath, name)).st_mtime
                    except OSError:
                        continue
                    if m > newest:
                        newest = m
                if capped:
                    break
            found.append(UnregisteredDir(
                name=child.name,
                path=str(child).replace("\\", "/"),
                entries=count,
                capped=capped,
                newest_utc=(time.strftime("%Y-%m-%d %H:%M", time.gmtime(newest))
                            if newest else None),
                has_git=has_git))
    return sorted(found, key=lambda d: (-d.entries, d.name))


def main_worktree(root: Path, timeout: float) -> Path:
    """The MAIN checkout, resolved from git rather than from the current directory.

    This is load-bearing for safety, not tidiness. The lane registries under
    `.claude/{opencode,cmdc}-agents/agents/` are **untracked** (gitignored), so they exist in the main
    checkout and are ABSENT from a linked worktree. Resolving them against the cwd therefore made
    this tool see 0 live lane claims instead of 151, and report 108 worktrees retirable where the
    truth is 38 - a fail-OPEN in exactly the dangerous direction, caused by running the tool from a
    worktree. `git worktree list --porcelain`'s first record is the main worktree, so ask git.
    """
    code, out = git("worktree", "list", "--porcelain", timeout=timeout, cwd=root)
    if code == 0:
        for line in out.splitlines():
            if line.startswith("worktree "):
                return Path(line[len("worktree "):])
    return root


def runner_owned_branches(repo: Path, timeout: float) -> dict[str, str]:
    """branch -> registry that claims it, from every lane directory the runners actually write.

    Mirrors `worktree_cleanup_core.RUNNER_LANE_ROOTS`, plus the legacy Kilo manager file. A branch
    named here is never retirable regardless of what git says.
    """
    found: dict[str, str] = {}
    roots = [(".claude/opencode-agents/agents", "opencode"), (".claude/cmdc-agents/agents", "cmdc")]
    for rel, runner in roots:
        for meta in sorted((repo / rel).glob("*/meta.json")):
            try:
                data = json.loads(meta.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue  # an unreadable lane record is handled by the marker tool's fail-closed path
            branch = data.get("branch")
            if isinstance(branch, str) and branch:
                found.setdefault(branch, f"{runner}:{meta.parent.name}")
    legacy = repo / ".kilo" / "agent-manager.json"
    if legacy.is_file():
        try:
            data = json.loads(legacy.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            data = {}
        for entry in (data.get("sessions") or {}).values() if isinstance(data, dict) else []:
            if isinstance(entry, dict) and isinstance(entry.get("branch"), str):
                found.setdefault(entry["branch"], "kilo:agent-manager")
    return found


# ---- the decision ------------------------------------------------------------------------------

@dataclass
class Verdict:
    path: str
    branch: str
    retirable: bool
    reasons: list[str] = field(default_factory=list)
    blockers: list[str] = field(default_factory=list)
    uniqueCommits: int | None = None
    dirtyPaths: int = 0
    untracked: int = 0
    owner: str | None = None
    staleClaims: list[str] = field(default_factory=list)
    recentlyTouched: bool = False
    #: Age in seconds of the newest non-`.git` file, recorded unconditionally and only ACTED on when
    #: nothing else blocks. See the RECENT ACTIVITY note in `survey()`.
    pendingFreshness: int | None = None

    def render(self) -> str:
        head = f"`{self.branch or '(detached)'}`"
        # A stale lane claim that did NOT block is printed, because a manager reading a RETIRABLE
        # line has to be able to see that a registry named this branch and why it was overruled.
        stale = (f"  [overrules stale lane claim: {'; '.join(self.staleClaims)}]"
                 if self.staleClaims else "")
        if self.retirable:
            return (f"RETIRABLE  {head:<44} {self.path}  [{'; '.join(self.reasons)}]"
                    f"{stale}")
        return f"KEEP       {head:<44} {self.path}  [{'; '.join(self.blockers)}]"


def survey(integration: str, timeout: float, only: str | None = None,
           repo: Path | None = None,
           scan_leftovers: bool = True,
           pools: tuple[str, ...] = (),
           ) -> tuple[list[Verdict], dict[str, Any], list[UnregisteredDir]]:
    """Decide, for every worktree but the main checkout, whether it may be removed.

    `repo` is a parameter rather than the module constant so the decision can be tested against a
    throwaway repository. A test that has to run against the real one is a test that can delete a
    real worktree.
    """
    root = Path(repo or REPO)
    code, out = git("worktree", "list", "--porcelain", timeout=timeout, cwd=root)
    if code != 0:
        raise Refusal("GIT-UNAVAILABLE", "git worktree list failed")
    entries: list[tuple[str, str, bool]] = []
    cur: dict[str, Any] = {}

    def flush(record: dict[str, Any]) -> None:
        if record:
            entries.append((record["path"], record.get("branch", ""), record.get("locked", False)))

    for line in out.splitlines():
        if line.startswith("worktree "):
            # The LAST record is flushed after the loop, never by a sentinel line. An earlier version
            # appended only when the NEXT "worktree " line appeared, so the final worktree was
            # silently dropped from the plan — and a plan that omits a worktree can omit a LIVE one.
            # `gk-core/tests/tools/test_retire_worktrees.py::OutputTests` exists to catch exactly that.
            flush(cur)
            cur = {"path": line[len("worktree "):]}
        elif line.startswith("branch "):
            cur["branch"] = line[len("branch "):].replace("refs/heads/", "")
        elif line.startswith("locked"):
            cur["locked"] = True
    flush(cur)
    entries = [e for e in entries if not _same_path(e[0], root)]
    if not entries:
        raise Refusal("NO-WORKTREES", "only the main checkout exists")

    code, _ = git("rev-parse", "--verify", integration, timeout=timeout, cwd=root)
    if code != 0:
        raise Refusal("BAD-INTEGRATION-REF", f"{integration} does not exist")

    # Ownership evidence comes from the MAIN checkout, never from `root`, and this covers BOTH
    # sources. A linked worktree carries neither: the lane registries are untracked, and
    # `tasks/sessions/` is a snapshot taken at fork time, so a session created after the fork is
    # missing there. Resolving against the cwd therefore under-counted owners in two independent
    # ways - 0 live lane branches instead of 151, and any session newer than the fork invisible - and
    # both push worktrees toward RETIRABLE. Measured swing: 108 reported retirable where the truth
    # is 38.
    main_root = main_worktree(root, timeout)
    sessions = load_sessions(main_root)
    runners = runner_owned_branches(main_root, timeout)

    verdicts: list[Verdict] = []
    for path, branch, locked in entries:
        if only and only not in branch and only not in path:
            continue
        v = Verdict(path=path, branch=branch, retirable=False)

        owner = next((s for s in sessions
                      if s.status == "active" and s.worktree and _same_path(s.worktree, path)), None)
        if owner is None:
            owner = next((s for s in sessions
                          if s.status == "active" and branch and s.branch == branch), None)
        if owner is not None:
            v.blockers.append(f"active session {owner.session} owns it")
            v.owner = owner.session

        record = next((s for s in sessions if branch and s.branch == branch), None)
        if record is not None and record.status == "active" and not v.owner:
            v.blockers.append(f"session record {record.session} is still active")
            v.owner = record.session

        if locked:
            v.blockers.append("worktree is locked")

        # RECENT ACTIVITY IS A BLOCKER, not a fact about ownership.
        #
        # Measured 2026-09-26: `.kilo/worktrees/materialistic-spear` was reported RETIRABLE -
        # "no active owner; integrated; clean" - seven minutes after a Kilo agent-manager session
        # created it. Its tracked session record did not exist yet, no lane registry named it, and
        # `git status` was empty because the checkout that creates a worktree finishes before any
        # edit is made. Every signal this tool had said "abandoned", and the one signal that said
        # otherwise - eight files written in the same second, with `.kilo/agent-manager.json`
        # written half a minute earlier - was not one it looked at.
        #
        # "No record claims it" is not "no owner owns it"; it is "no owner I can see", and a lane
        # creates its worktree before it creates its record. A clean tree is the normal state of a
        # worktree in its first minute, not evidence of abandonment.
        newest = _newest_mtime(Path(v.path), timeout)
        if newest is not None and (time.time() - newest) < RECENT_ACTIVITY_SECONDS:
            age = int(time.time() - newest)
            v.pendingFreshness = age
        if branch and branch in runners:
            # A lane registry is UNTRACKED and unpruned: nothing removes an entry when its session
            # closes, so an absolute veto here is a ratchet - once a lane writes a meta.json, no
            # worktree behind it can ever be retired again. Measured 2026-09-26: all 83 registry
            # entries had no active session behind them, and the 80 branches they held had NO
            # unmerged patch against the integration branch. The pile was pure bookkeeping, and
            # this line is what made it a pile.
            #
            # So a registry claim blocks only when a tracked session corroborates it - and an active
            # owner is already blocked above by that tracked record, which is the authority (see the
            # module docstring: ownership is resolved from tracked records, not the registry). An
            # uncorroborated claim is recorded and SURFACED, never silently dropped, so the manager
            # can see the stale count instead of inheriting an invisible veto.
            if v.owner:
                v.blockers.append(f"lane registry {runners[branch]} claims this branch, "
                                  f"corroborated by active session {v.owner}")
            else:
                v.staleClaims.append(runners[branch])

        # An acceptance artefact's raw log evidence lives INSIDE a review worktree, and retiring that
        # worktree destroys it silently. Measured 2026-09-26: of 78 artefacts, 17 name a `logDir` that
        # now resolves nowhere — and this tool had no awareness of the field at all. The artefact
        # survives (it is tracked, and its `sha` still resolves), but the full command output behind
        # the verdict does not, which is the difference between a decision you can re-read and one you
        # must take on trust.
        #
        # `logDir` is a RELATIVE path, so it resolves inside whichever tree the acceptance harness ran
        # in. Resolving it against each candidate worktree is the only honest reading: it cannot prove
        # which tree held the evidence, so it blocks on any tree the path could have been under.
        evidence = acceptance_evidence_under(root, Path(path))
        if evidence:
            v.blockers.append(
                f"acceptance evidence for {len(evidence)} lane(s) resolves inside it: "
                + ", ".join(sorted(evidence)[:3])
                + ("..." if len(evidence) > 3 else ""))

        code, status = git("status", "--porcelain=v1", "--untracked-files=all", timeout=timeout, cwd=Path(path))
        if code != 0:
            v.blockers.append("git status failed in this worktree")
        else:
            lines = [l for l in status.splitlines() if l.strip()]
            v.dirtyPaths = len(lines)
            v.untracked = sum(1 for l in lines if l.startswith("??"))
            if lines:
                v.blockers.append(f"{len(lines)} local path(s) incl. {v.untracked} untracked")

        if branch and branch != "(detached)":
            # `cwd=root` is load-bearing: without it the ref lookup happens in THIS tool's own
            # repository, where a test's or another repo's branch name does not exist, and every
            # worktree reads as "git cherry failed" — a false KEEP that looks like a safety check.
            code, cherry = git("cherry", integration, branch, timeout=timeout, cwd=root)
            if code != 0:
                v.blockers.append("git cherry failed")
            else:
                unique = sum(1 for l in cherry.splitlines() if l.startswith("+"))
                v.uniqueCommits = unique
                if unique:
                    v.blockers.append(f"{unique} commit(s) not integrated into {integration}")

        # RECENT ACTIVITY, applied last and only when nothing else blocks.
        #
        # The case it exists for: `.kilo/worktrees/materialistic-spear` was reported RETIRABLE —
        # "no active owner; integrated; clean" — about seven minutes after a Kilo agent-manager
        # session created it. No session record existed yet, no lane registry named it, and
        # `git status` was empty because the checkout that creates a worktree finishes before anyone
        # edits it. Every signal this tool had said "abandoned", and following the plan would have
        # deleted a live agent's working directory.
        #
        # It is deliberately NOT a general blocker. Measured 2026-09-26: run unconditionally it held
        # **36 of 38** worktrees, because a repo-wide event had rewritten the same three shared
        # spec/brief files in every worktree inside the hour. A bulk write to files every lane shares
        # is not an agent working in a given worktree. And on a worktree that already has local
        # paths, freshness adds nothing — the dirt blocks it, and this one MASKS the actionable
        # reason. A guard that fires on everything is the same ratchet as the lane-registry veto
        # above, and it was the same mistake twice.
        if not v.blockers and v.pendingFreshness is not None:
            v.blockers.append(
                f"files written {v.pendingFreshness}s ago (< {int(RECENT_ACTIVITY_SECONDS)}s) - in "
                f"flight, not abandoned; a lane creates its worktree before its record exists")
            v.recentlyTouched = True

        v.retirable = not v.blockers
        if v.retirable:
            v.reasons.append("no active owner")
            v.reasons.append("integrated")
            v.reasons.append("clean")
        verdicts.append(v)

    verdicts.sort(key=lambda v: (not v.retirable, -(v.uniqueCommits or 0), v.branch))
    counts = {
        "total": len(verdicts),
        "retirable": sum(1 for v in verdicts if v.retirable),
        "kept": sum(1 for v in verdicts if not v.retirable),
        "integration": integration,
        "liveLaneBranches": len(runners),
        "staleLaneClaims": sum(1 for v in verdicts if v.staleClaims),
        "activeSessions": sum(1 for s in sessions if s.status == "active"),
        "ownershipReadFrom": Path(main_root).name,
        "recentlyTouched": sum(1 for v in verdicts if v.recentlyTouched),
        "recentActivityWindowSeconds": int(RECENT_ACTIVITY_SECONDS),
    }

    # A worktree survey is blind to a directory git has forgotten, so the inventory says so
    # explicitly instead of reporting a clean tree over a littered one. `--no-leftover-scan`
    # turns it off, and the counts then record that it was off rather than implying zero.
    leftovers: list[UnregisteredDir] = []
    if scan_leftovers:
        leftovers = scan_unregistered_dirs(root, [e[0] for e in entries] + [str(root)],
                                         extra_pools=pools,
                                           timeout=timeout)
    counts["leftoverScan"] = "on" if scan_leftovers else "off"
    counts["unregisteredDirs"] = len(leftovers)
    counts["unregisteredDirsWithContent"] = sum(1 for d in leftovers if d.entries > 0)
    return verdicts, counts, leftovers


# ---- rendering ----------------------------------------------------------------------------------

def render(verdicts: list[Verdict], counts: dict[str, Any],
           leftovers: list[UnregisteredDir] | None = None,
           stale_proof: list[dict[str, Any]] | None = None) -> str:
    leftovers = leftovers or []
    stale_proof = stale_proof or []
    lines = ["# Worktree retirement plan", ""]
    lines.append(f"**{counts['total']} worktrees** (excluding the main checkout) · "
                 f"**{counts['retirable']} retirable** · {counts['kept']} kept · "
                 f"integration `{counts['integration']}` · "
                 f"{counts['activeSessions']} active session record(s) · "
                 f"{counts['liveLaneBranches']} branch(es) named by a lane registry, of which "
                 f"**{counts['staleLaneClaims']}** carry no active session behind them "
                 f"(ownership read from the main checkout `{counts['ownershipReadFrom']}`)")
    lines.append("")
    lines.append("Retirable means ALL of: no active session owns it, its working tree is clean "
                 "(untracked included), every one of its commits is already integrated, it holds no "
                 "acceptance evidence, and it is not locked.")
    lines.append("")
    lines.append("A lane-registry claim is **not** by itself a veto. Those registries are untracked "
                 "and unpruned - nothing deletes an entry when its session closes - so treating one "
                 "as absolute made every worktree behind a finished lane permanently unretirable. A "
                 "claim blocks only when a tracked session record corroborates it, which is the "
                 "authority for ownership. Uncorroborated claims are counted above and printed on "
                 "each affected line, so overriding one is visible rather than silent.")
    lines.append("")
    for v in verdicts:
        if v.retirable:
            lines.append(v.render())
    if not any(v.retirable for v in verdicts):
        lines.append("_Nothing is retirable. That is a reading, not an error._")
    lines.append("")
    lines.append("## Kept, with the reason")
    lines.append("")
    for v in verdicts:
        if not v.retirable:
            lines.append(f"- `{v.branch or '(detached)'}` — {'; '.join(v.blockers)}")

    lines.append("")
    lines.append("## Unregistered leftover directories (not retirable by this tool)")
    lines.append("")
    if not leftovers:
        if counts.get("leftoverScan") == "on":
            lines.append("_None found._")
        else:
            lines.append("_Not scanned (`--no-leftover-scan`); this is not a clean bill of health._")
    else:
        with_content = [d for d in leftovers if d.entries > 0]
        empty = [d for d in leftovers if d.entries == 0]
        lines.append(f"**{len(leftovers)}** director(ies) sit beside the worktrees that "
                     f"`git worktree list` does not report — **{len(with_content)} still hold "
                     f"content**, {len(empty)} are empty. They are the residue of a "
                     "`git worktree remove` that de-registered the worktree and then failed to "
                     "delete the directory (a Windows long path, or permissions), and git can "
                     "never reclaim them afterwards.")
        lines.append("")
        lines.append("**This tool does not remove these.** Deciding that one is disposable requires "
                     "adjudicating its content, because a name or a commit count cannot see a moved "
                     "file — and on 2026-09-26 one of these held 451 lines across 9 files that exist "
                     "nowhere else in the repository. Adjudicate first, then delete by hand.")
        lines.append("")
        if with_content:
            lines.append("### Holds content — adjudicate before touching")
            lines.append("")
            for d in with_content:
                cap = " (walk capped)" if d.capped else ""
                gitnote = "" if d.has_git else " · **no `.git`**"
                lines.append(f"- `{d.name}` — {d.entries} entries{cap}, newest "
                             f"{d.newest_utc or 'unknown'}{gitnote}")
            lines.append("")
        if stale_proof:
            stale = [r for r in stale_proof if r["verdict"] == "PROVABLY-STALE"]
            needs = [r for r in stale_proof if r["verdict"] == "NEEDS-ADJUDICATION"]
            other = [r for r in stale_proof
                     if r["verdict"] not in ("PROVABLY-STALE", "NEEDS-ADJUDICATION")]
            lines.append(f"### Proven by content (`--prove-stale`): {len(stale)} of "
                         f"{len(stale_proof)} need no human judgement")
            lines.append("")
            if stale:
                for r in sorted(stale, key=lambda r: -r["entries"]):
                    lines.append(f"- `{r['name']}` — {r['entries']} entries, every file's content "
                                 f"already in integration's history")
                lines.append("")
            if needs:
                lines.append(f"**{len(needs)} genuinely hold unlanded content** and are the real "
                             f"queue:")
                lines.append("")
                for r in sorted(needs, key=lambda r: -r["entries"]):
                    lines.append(f"- `{r['name']}` — {r['unlanded']} unlanded of "
                                 f"{r.get('filesWalked', '?')} walked, {r['entries']} entries")
                lines.append("")
            if other:
                lines.append("**Not classified either way, and reported rather than counted as "
                             "stale:**")
                lines.append("")
                for r in other:
                    lines.append(f"- `{r['name']}` — **{r['verdict']}** — {r['detail']}")
                lines.append("")
        if empty:
            lines.append("### Empty — safe to remove once you have read the list")
            lines.append("")
            lines.append(", ".join(f"`{d.name}`" for d in empty))
    lines.append("")
    lines.append("## What this cannot prove")
    lines.append("")
    lines.append("- That a *runner outside this repository's registries* is not using a worktree. "
                 "Ownership is read from the tracked session records and the lane `meta.json` files; "
                 "a lane that registers nothing is invisible here, which is why `--apply` exists and "
                 "why the manager reviews the plan before running it.")
    lines.append("- That an integrated commit was *correct*. Integration is a containment fact, not a "
                 "quality verdict; acceptance is a separate artefact.")
    lines.append(f"- That a worktree with **no record and no registry entry** is unowned. A lane "
                 f"creates its worktree before it creates its session record, and the checkout that "
                 f"creates one leaves `git status` clean, so a worktree minutes old looks exactly "
                 f"like an abandoned one. Ownership here means \"no owner this tool can see\". "
                 f"A file written within **{int(RECENT_ACTIVITY_SECONDS) // 60} minutes** is "
                 f"treated as in flight and blocks removal for that reason alone "
                 f"(**{counts.get('recentlyTouched', 0)}** worktree(s) held this way in this run).")
    if counts.get("leftoverScan") == "on":
        lines.append(f"- That the **{counts.get('unregisteredDirs', 0)} directory(ies) below** are "
                     "disposable. They are listed because a worktree survey cannot see them at all, "
                     "and this tool refuses to decide. See the next section.")
    else:
        lines.append("- **The leftover scan was OFF** (`--no-leftover-scan`), so unregistered "
                     "leftover directories were not looked for. A clean plan above does not mean "
                     "the directory is clean.")
    return "\n".join(lines)


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Plan (and optionally perform) removal of merged, finished worktrees.")
    ap.add_argument("--json", action="store_true", help="machine-readable plan")
    ap.add_argument("--apply", action="store_true", help="actually remove the retirable worktrees")
    ap.add_argument("--dry-run", action="store_true", help="with --apply, print what would be removed")
    ap.add_argument("--why", metavar="BRANCH_OR_PATH", help="explain one worktree's verdict")
    ap.add_argument("--integration", default=DEFAULT_INTEGRATION, help=f"default {DEFAULT_INTEGRATION}")
    ap.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT,
                    help=f"hard timeout per external call (default {DEFAULT_TIMEOUT:g})")
    ap.add_argument("--root", type=Path, default=REPO, help=argparse.SUPPRESS)
    ap.add_argument("--no-leftover-scan", action="store_true",
                    help="skip the scan for directories git no longer registers as worktrees. The "
                         "plan then records that the scan was off, so a clean plan is never read "
                         "as a clean directory.")
    ap.add_argument("--pool", action="append", default=[], metavar="DIR",
                    help="declare a worktree pool to scan for unregistered leftovers, in "
                         "addition to the parents of the registered worktrees. Needed because "
                         "the derived set SHRINKS as the cleanup succeeds: once the last "
                         "worktree leaves a pool, that pool is nobody's parent and the husks "
                         "it still holds become invisible. A pool that does not exist is "
                         "refused by name rather than skipped.")
    ap.add_argument("--prove-stale", action="store_true",
                    help="classify every unregistered leftover by CONTENT using the reclaim tool's "
                         "proof, splitting the ones that are provably already integrated from the "
                         "ones that may hold real work. Read-only, and never combined with --apply: "
                         "asking what is disposable must not remove anything as a side effect. "
                         "Measured 2026-09-27: of 37 leftovers the survey asked a manager to "
                         "adjudicate 33, and the proof said 4 empty + 10 provably stale + 23 real.")
    ap.add_argument("--reclaim-empty-leftovers", action="store_true",
                    help="also handle unregistered directories that hold nothing. A directory with "
                         "zero entries cannot hold unlanded work, so it needs no adjudication; "
                         "combined with --apply it is removed, and a directory that refuses is "
                         "reported with the OS reason (LOCKED, PATH-TOO-LONG, PERMISSION) rather "
                         "than one 'could not remove' bucket. Leftovers holding content are still "
                         "only reported.")
    ap.add_argument("--reclaim-only", action="store_true",
                    help="apply the leftover reclaim and touch no worktree at all. Reclaiming "
                         "litter and retiring a merged worktree are independent decisions, and "
                         "--apply alone would couple them: measured 2026-09-27, the only retirable "
                         "worktree on this machine was one held back on purpose, so a reclaim run "
                         "would have removed it as a side effect of asking about directories. "
                         "A leftover that still HOLDS FILES is out of scope here by design - "
                         "see " + STALE_DIR_TOOL + ", which takes the adjudicated paths and "
                         "proves the rest already exists in integration's history.")
    args = ap.parse_args(argv)
    if args.reclaim_only:
        # Implies the capability, so `--reclaim-only` is not a mode that silently does nothing -
        # the same defect `--why` had, where a filter plus `--apply` printed a plan, removed
        # nothing and exited 0.
        args.reclaim_empty_leftovers = True
        args.apply = True
    if args.timeout <= 0:
        print(f"RETIRE REFUSED: NEGATIVE-TIMEOUT: --timeout must be positive ({args.timeout})", file=sys.stderr)
        return 2
    if args.prove_stale and (args.apply or args.dry_run):
        # Asking what is disposable must not remove anything as a side effect. The same coupling was
        # refused for `--reclaim-only` and for a filter plus `--apply`, for the same reason: a mode
        # that answers one question while answering another is how a plan turns into an action nobody
        # asked for.
        print("RETIRE REFUSED: PROOF-COUPLED-TO-APPLY: --prove-stale is read-only and cannot be "
              "combined with --apply or --dry-run; ask what is disposable separately from removing it",
              file=sys.stderr)
        return 2
    stale_proof: list[dict[str, Any]] = []
    try:
        verdicts, counts, leftovers = survey(args.integration, args.timeout, args.why, args.root,
                                             scan_leftovers=not args.no_leftover_scan,
                                             pools=tuple(args.pool))
        if args.prove_stale and leftovers:
            stale_proof = prove_stale_leftovers(args.root, leftovers, args.timeout)
    except Refusal as refusal:
        print(f"RETIRE REFUSED: {refusal}", file=sys.stderr)
        print(f"  meaning: {REFUSALS[refusal.name]}", file=sys.stderr)
        return 2

    if args.why:
        target = [v for v in verdicts if args.why in v.branch or args.why in v.path]
        if not target:
            print(f"RETIRE REFUSED: NO-WORKTREES: nothing matches {args.why!r}", file=sys.stderr)
            return 2
        # `--why` is a filter, not a mode that short-circuits the run. An earlier version returned
        # here unconditionally, so `--why <group> --apply` printed the plan and removed NOTHING while
        # exiting 0 — the worst shape a removal tool can have: it looked like it had worked.
        if not args.apply:
            if args.json:
                print(json.dumps([asdict(v) for v in target], indent=2))
            else:
                for v in target:
                    print(v.render())
            return 0

    reclaim: list[dict[str, Any]] = []
    if args.reclaim_empty_leftovers:
        reclaim = reclaim_empty_leftovers(leftovers, apply=bool(args.apply) and not args.dry_run)

    if args.json:
        print(json.dumps({"counts": counts,
                          "worktrees": [asdict(v) for v in verdicts],
                          "unregisteredLeftovers": [asdict(d) for d in leftovers],
                          "staleProof": stale_proof,
                          "reclaim": reclaim},
                         indent=2))
    else:
        # `reconfigure` is absent when stdout is captured (a test, or a caller redirecting output),
        # and calling it unconditionally turns a successful run into an AttributeError.
        reconfigure = getattr(sys.stdout, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")
        print(render(verdicts, counts, leftovers, stale_proof))
        if args.reclaim_empty_leftovers:
            verb = "would reclaim" if (args.dry_run or not args.apply) else "reclaimed"
            removed_leftovers = [r for r in reclaim if r["outcome"].startswith("REMOVED")]
            stuck = [r for r in reclaim if not r["outcome"].startswith("REMOVED")
                     and r["outcome"] != "WOULD-REMOVE"]
            print(f"{verb} {len(removed_leftovers)} provably-empty leftover(s); "
                  f"{len(stuck)} kept")
            for r in stuck:
                print(f"  KEPT {r['name']}  [{r['outcome']}]  {r.get('detail', '')}")

    if args.reclaim_only:
        # A non-zero exit for a directory that SHOULD have been reclaimed and was not. KEPT-NOT-EMPTY
        # is the correct outcome and must not fail the run, or the tool would be unusable on a
        # machine where any leftover holds real work; GONE means something else got there first.
        # Exiting 0 while a refusal stood would be the "checker that cannot fail" shape, so LOCKED,
        # PERMISSION, PATH-TOO-LONG, REFUSED-SCAN-CAPPED and UNCLASSIFIED all fail loudly.
        unfinished = [r for r in reclaim
                      if r["outcome"] not in ("REMOVED", "REMOVED-LONG-PATH", "GONE",
                                              "KEPT-NOT-EMPTY", "WOULD-REMOVE")]
        if unfinished and not args.json:
            for r in unfinished:
                print(f"  UNFINISHED {r['name']}  [{r['outcome']}]  {r.get('detail', '')}",
                      file=sys.stderr)
        return 1 if unfinished else 0

    if not args.apply:
        return 0

    removed, failed = [], []
    for v in verdicts:
        if not v.retirable:
            continue
        if args.dry_run:
            removed.append(v.path)
            continue
        # `cwd=args.root` is load-bearing for the same reason as in `survey`: without it the removal
        # runs in whatever repository this module was imported from, and git answers "is not a working
        # tree" for every path - a refusal that looks like a policy decision rather than a wrong cwd.
        code, out = git_diag("worktree", "remove", v.path, timeout=max(args.timeout, 300.0), cwd=args.root)
        if code == 0:
            removed.append(v.path)
        else:
            failed.append({"path": v.path, "git": out.splitlines()[:3]})
    if args.json:
        print(json.dumps({"removed": removed, "failed": failed, "dryRun": bool(args.dry_run)}, indent=2))
    else:
        verb = "would remove" if args.dry_run else "removed"
        print(f"{verb} {len(removed)} worktree(s)")
        for path in removed:
            print(f"  {path}")
        for f in failed:
            print(f"  FAILED {f['path']}: {f['git']}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
