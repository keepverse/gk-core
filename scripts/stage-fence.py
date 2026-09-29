"""Stage exactly the paths this session's fence declares, and nothing else.

WHY THIS EXISTS
---------------
Three times in this program, a commit of mine published another lane's work:

  1a376cc4e  swallowed 45 of my staged test files (the other direction)
  3307f0597  published 87 of my staged paths under another lane's message
  ace893432  published two BuildPresets test files belonging to a salvage lane

The first two are the shared index: `git commit` publishes the whole index, not the paths you name,
so anything staged when another process commits goes with it. The THIRD was my own staging rule.
I was building the stage list from `git status` MINUS A DENYLIST of path fragments I had happened
to learn about the other lanes - seedsmith, .commandcode/taste, ip-censor, numeric-types. That is
a list of what I noticed, and it failed the moment a lane touched a path I had not been told
about.

THE FENCE IS THE AUTHORITY, NOT THE STATUS OUTPUT
-------------------------------------------------
`tasks/sessions/<session>.json` already declares the exact set of paths this session may edit, and
the session-boundary standard makes it load-bearing. Staging from the FENCE means an unfamiliar
path is simply not staged, rather than being staged because no rule excluded it. The failure mode
becomes "a path I edited is not in the commit", which is visible and harmless, instead of "a path I
did not edit is in the commit", which is silent.

Usage:
    python gk-core/scripts/stage-fence.py --session ps1-ban-manager-20260926 --dry-run
    python gk-core/scripts/stage-fence.py --session ps1-ban-manager-20260926
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
# Windows caps a process command line near 32k characters; a busy fence is 250+ paths, which
# overruns it. `--pathspec-from-file` passes the list on stdin instead, so the SIZE of a change
# stops being a limit. `-A` is required because a path this commit deletes no longer matches.
# The scratch file goes to the system temp directory, NOT next to this script: a tool that stages
# from the fence must not leave its own litter in a tracked directory, where it shows up as an
# unexplained untracked path and, being unignored, invites a `git add -A` from someone else.
PATHS_FROM = Path(tempfile.gettempdir()) / f"stage-fence-{os.getpid()}.pathspec"


def git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *args], cwd=REPO_ROOT, capture_output=True, text=True)


def fence_paths(session: str) -> list[str]:
    record = REPO_ROOT / "tasks" / "sessions" / f"{session}.json"
    if not record.is_file():
        raise SystemExit(f"FENCE-RECORD-MISSING: {record}")
    data = json.loads(record.read_text(encoding="utf-8"))
    paths = [str(p) for p in data.get("paths", [])]
    if not paths:
        raise SystemExit(f"FENCE-EMPTY: {record} declares no paths")
    return paths


def changed() -> set[str]:
    """Repo-relative paths that need STAGING, from `git status`.

    Only paths with a change in the WORKTREE column. A path whose index already matches the worktree
    needs no `git add`, and passing one to `git add` is not merely redundant - it FAILS. A path
    already removed by `git rm` has no index entry and no file, so `git add -A -- <path>` aborts the
    whole batch with "pathspec did not match any files". That is the third time this repository
    shape has bitten a port, so it is handled here rather than worked around at each call site.
    """
    out = git("status", "--porcelain").stdout
    found = set()
    for line in out.splitlines():
        code, path = line[:2], line[3:].strip()
        if not code or code[1] == " " and code[0] == " ":
            continue
        # `code[1]` is the WORKTREE column: a space there means the index already carries it.
        if code[1] == " " and code[0] != "?":
            continue
        if " -> " in path:
            old, new = path.split(" -> ", 1)
            found.add(old.strip('"').replace("\\", "/"))
            found.add(new.strip('"').replace("\\", "/"))
        else:
            found.add(path.strip('"').replace("\\", "/"))
    return found


def in_fence(path: str, patterns: list[str]) -> bool:
    """A path is in the fence when it EQUALS a declared path or matches a declared glob.

    Equality first, so an exact entry is never shadowed by a broader glob, and a pattern ending in
    `/**` is treated as a directory prefix because fnmatch's `*` already crosses separators.
    """
    for pattern in patterns:
        if path == pattern:
            return True
        if any(ch in pattern for ch in "*?["):
            if fnmatch.fnmatch(path, pattern):
                return True
            if pattern.endswith("/**") and path.startswith(pattern[:-2]):
                return True
    return False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--session", required=True, help="session id under tasks/sessions/")
    parser.add_argument("--dry-run", action="store_true",
                        help="print what would be staged and change nothing")
    args = parser.parse_args(argv)

    patterns = fence_paths(args.session)
    todo = sorted(p for p in changed() if in_fence(p, patterns))
    # Report the other side too. A changed path the fence does NOT cover is the signal that the
    # fence is behind, and it is exactly the path a denylist-based rule would have staged.
    outside = sorted(p for p in changed() if not in_fence(p, patterns))

    print(f"  fence declares {len(patterns)} patterns; {len(todo)} changed path(s) in fence")
    if outside:
        print(f"  {len(outside)} changed path(s) OUTSIDE the fence, deliberately not staged:")
        for path in outside[:12]:
            print(f"      {path}")
        if len(outside) > 12:
            print(f"      ... and {len(outside) - 12} more")

    if args.dry_run:
        print("  DRY RUN - nothing staged")
        return 0
    if not todo:
        print("  nothing to stage")
        return 0

    PATHS_FROM.write_bytes(("\n".join(todo) + "\n").encode("utf-8"))
    try:
        proc = git("add", "-A", "--pathspec-from-file", str(PATHS_FROM))
    finally:
        PATHS_FROM.unlink(missing_ok=True)
    if proc.returncode:
        print(f"  STAGE-FAILED: {proc.stderr.strip()[:200]}", file=sys.stderr)
        return 1
    staged = git("diff", "--cached", "--name-only").stdout.split()
    leaked = [p for p in staged if not in_fence(p, patterns)]
    print(f"  staged {len(staged)} path(s)")
    if leaked:
        # Fail closed: a staged path outside the fence means the index already held someone
        # else's work, and committing now would publish it. Say so instead of proceeding.
        print(f"  STAGED-OUTSIDE-FENCE ({len(leaked)}): {', '.join(leaked[:6])}", file=sys.stderr)
        print("  another lane's work is in the index; commit deliberately or unstage it first",
              file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
