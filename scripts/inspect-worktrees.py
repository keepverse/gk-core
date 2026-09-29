#!/usr/bin/env python3
"""Inspect every Git worktree registered by this repository without modifying it.

The report is intentionally based on Git's own worktree registry and porcelain status,
not a recursive filesystem walk. That makes it fast enough for shared multi-agent trees
and keeps ignored build output out of the dirty-path list.

This tool is read-only: it never fetches, checks out, resets, cleans, prunes, locks, or
unlocks a worktree. ``GIT_OPTIONAL_LOCKS=0`` also prevents ``git status`` from refreshing
an index as a side effect.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Sequence

CONFLICT_STATUSES = {"DD", "AU", "UD", "UA", "DU", "AA", "UU"}
RENAME_STATUSES = {"R", "C"}


class GitInspectionError(RuntimeError):
    """A Git command could not produce trustworthy worktree evidence."""


def _run_git(
    repo: Path,
    *args: str,
    timeout_seconds: float,
    check: bool = True,
) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    # `git status` may otherwise update the index while merely reporting status.
    env["GIT_OPTIONAL_LOCKS"] = "0"
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=timeout_seconds,
        check=False,
    )
    if check and result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        rendered = " ".join(("git", "-C", str(repo), *args))
        raise GitInspectionError(f"{rendered} failed ({result.returncode}): {detail}")
    return result


def parse_worktree_porcelain(text: str) -> list[dict[str, Any]]:
    """Parse ``git worktree list --porcelain`` into one dictionary per worktree."""
    records: list[dict[str, Any]] = []
    current: dict[str, Any] | None = None

    def finish() -> None:
        nonlocal current
        if current is not None:
            records.append(current)
            current = None

    for raw_line in text.splitlines():
        line = raw_line.rstrip("\r")
        if not line:
            finish()
            continue
        if line.startswith("worktree "):
            finish()
            current = {
                "path": line[len("worktree ") :],
                "head": None,
                "branch": None,
                "detached": False,
                "bare": False,
                "locked": None,
                "prunable": None,
            }
            continue
        if current is None:
            continue
        if line.startswith("HEAD "):
            current["head"] = line[len("HEAD ") :]
        elif line.startswith("branch "):
            ref = line[len("branch ") :]
            current["branch"] = ref.removeprefix("refs/heads/")
        elif line == "detached":
            current["detached"] = True
        elif line == "bare":
            current["bare"] = True
        elif line.startswith("locked"):
            current["locked"] = line[len("locked") :].lstrip()
        elif line.startswith("prunable"):
            current["prunable"] = line[len("prunable") :].lstrip()

    finish()
    return records


def _display_rename_path(original: str, destination: str) -> str:
    return f"{original} -> {destination}"


def parse_status_z(text: str) -> dict[str, Any]:
    """Parse NUL-delimited ``git status --porcelain=v1 -z --untracked-files=all``."""
    fields = [field for field in text.split("\0") if field]
    changed: list[dict[str, str]] = []
    staged: list[str] = []
    unstaged: list[str] = []
    untracked: list[str] = []
    conflicted: list[str] = []
    ignored: list[str] = []
    index = 0

    while index < len(fields):
        record = fields[index]
        index += 1
        if len(record) < 3:
            # A valid v1 record has XY plus a path. Preserve unexpected input rather
            # than silently treating it as a clean worktree.
            changed.append({"status": "??", "path": record})
            untracked.append(record)
            continue

        status = record[:2]
        path = record[3:]
        original_path: str | None = None
        if status[0] in RENAME_STATUSES or status[1] in RENAME_STATUSES:
            if index >= len(fields):
                raise GitInspectionError(f"rename/copy status missing destination: {record!r}")
            destination = fields[index]
            index += 1
            original_path = path
            path = _display_rename_path(path, destination)

        changed.append({"status": status, "path": path, "originalPath": original_path or ""})
        if status == "??":
            untracked.append(path)
        elif status == "!!":
            ignored.append(path)
        else:
            if status[0] not in {" ", "?"}:
                staged.append(path)
            if status[1] not in {" ", "?"}:
                unstaged.append(path)
            if status in CONFLICT_STATUSES:
                conflicted.append(path)

    return {
        "changed": len(changed),
        "changedPaths": changed,
        "staged": staged,
        "unstaged": unstaged,
        "untracked": untracked,
        "conflicted": conflicted,
        "ignored": ignored,
    }


def _normal_path(path: Path) -> str:
    return os.path.normcase(str(path.resolve()))


def _load_session_owners(main_root: Path) -> tuple[dict[str, list[dict[str, str]]], list[str]]:
    owners: dict[str, list[dict[str, str]]] = {}
    errors: list[str] = []
    sessions_dir = main_root / "tasks" / "sessions"
    if not sessions_dir.is_dir():
        return owners, errors

    for session_path in sorted(sessions_dir.glob("*.json")):
        try:
            session = json.loads(session_path.read_text(encoding="utf-8"))
            raw_worktree = session.get("worktree")
            if not isinstance(raw_worktree, str) or not raw_worktree:
                continue
            worktree = Path(raw_worktree)
            if not worktree.is_absolute():
                worktree = main_root / worktree
            key = _normal_path(worktree)
            owners.setdefault(key, []).append(
                {
                    "id": str(session.get("id") or session_path.stem),
                    "status": str(session.get("status") or "unknown"),
                    "branch": str(session.get("branch") or ""),
                }
            )
        except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError) as exc:
            errors.append(f"{session_path}: {exc}")
    return owners, errors


def _git_error(result: subprocess.CompletedProcess[str]) -> str:
    return (result.stderr or result.stdout or "git command failed").strip()


def inspect_repo(
    repo: Path,
    *,
    timeout_seconds: float = 30.0,
    max_paths: int = 200,
) -> dict[str, Any]:
    """Return a JSON-serializable, read-only snapshot of all registered worktrees."""
    repo = repo.resolve()
    if not repo.is_dir():
        raise GitInspectionError(f"repository directory does not exist: {repo}")

    listed = _run_git(
        repo,
        "worktree",
        "list",
        "--porcelain",
        timeout_seconds=timeout_seconds,
    )
    records = parse_worktree_porcelain(listed.stdout)
    if not records:
        raise GitInspectionError("git returned no worktree records")

    main_root = Path(records[0]["path"]).resolve()
    owners, owner_errors = _load_session_owners(main_root)
    worktrees: list[dict[str, Any]] = []

    for record in records:
        raw_path = record["path"]
        path = Path(raw_path)
        item: dict[str, Any] = {
            **record,
            "path": str(path),
            "exists": path.is_dir(),
            "owners": owners.get(_normal_path(path), []) if path.exists() else [],
            "error": None,
        }

        if record["bare"]:
            item.update(
                {
                    "dirty": False,
                    "status": _empty_status(),
                    "statusError": "bare worktree has no working tree",
                }
            )
            worktrees.append(item)
            continue

        if not path.is_dir():
            item.update(
                {
                    "dirty": None,
                    "status": _empty_status(),
                    "statusError": "registered worktree path is missing",
                }
            )
            worktrees.append(item)
            continue

        try:
            status_result = _run_git(
                path,
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all",
                timeout_seconds=timeout_seconds,
                check=False,
            )
            if status_result.returncode != 0:
                item.update(
                    {
                        "dirty": None,
                        "status": _empty_status(),
                        "statusError": _git_error(status_result),
                    }
                )
                worktrees.append(item)
                continue

            status = parse_status_z(status_result.stdout)
            if max_paths >= 0:
                status["changedPaths"] = status["changedPaths"][:max_paths]
            item["dirty"] = status["changed"] > 0
            item["status"] = status
            item["statusError"] = None
        except (GitInspectionError, subprocess.TimeoutExpired) as exc:
            item.update(
                {
                    "dirty": None,
                    "status": _empty_status(),
                    "statusError": str(exc),
                }
            )

        worktrees.append(item)

    dirty_count = sum(item.get("dirty") is True for item in worktrees)
    unknown_count = sum(item.get("dirty") is None for item in worktrees)
    return {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "repoRoot": str(repo),
        "mainWorktree": str(main_root),
        "worktreeCount": len(worktrees),
        "dirtyCount": dirty_count,
        "unknownCount": unknown_count,
        "ownerRecordErrors": owner_errors,
        "worktrees": worktrees,
    }


def _empty_status() -> dict[str, Any]:
    return {
        "changed": 0,
        "changedPaths": [],
        "staged": [],
        "unstaged": [],
        "untracked": [],
        "conflicted": [],
        "ignored": [],
    }


def _print_human(report: dict[str, Any], *, dirty_only: bool) -> None:
    print(
        f"Worktrees: {report['worktreeCount']}  "
        f"dirty: {report['dirtyCount']}  unknown: {report['unknownCount']}"
    )
    for item in report["worktrees"]:
        if dirty_only and item.get("dirty") is not True:
            continue
        branch = item.get("branch") or ("detached" if item.get("detached") else "no-branch")
        head = (item.get("head") or "no-head")[:12]
        state = "DIRTY" if item.get("dirty") is True else "UNKNOWN" if item.get("dirty") is None else "CLEAN"
        labels = []
        if item.get("locked") is not None:
            labels.append("locked")
        if item.get("prunable") is not None:
            labels.append("prunable")
        suffix = f" [{', '.join(labels)}]" if labels else ""
        print(f"\n{state} {item['path']} ({branch}@{head}){suffix}")
        for owner in item.get("owners", []):
            print(f"  session: {owner['id']} [{owner['status']}]")
        if item.get("statusError"):
            print(f"  error: {item['statusError']}")
            continue
        status = item["status"]
        print(
            "  changed: "
            f"{status['changed']} total, {len(status['staged'])} staged, "
            f"{len(status['unstaged'])} unstaged, {len(status['untracked'])} untracked, "
            f"{len(status['conflicted'])} conflicted"
        )
        for changed in status["changedPaths"]:
            print(f"    {changed['status']} {changed['path']}")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="repository worktree to inspect (defaults to the script's repository)",
    )
    parser.add_argument("--json", action="store_true", help="emit machine-readable JSON")
    parser.add_argument(
        "--dirty-only",
        action="store_true",
        help="hide clean worktrees from human-readable output (JSON remains complete)",
    )
    parser.add_argument(
        "--max-paths",
        type=int,
        default=200,
        help="maximum changed paths retained per worktree; -1 retains all (default: 200)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=30.0,
        help="per-Git-command timeout in seconds (default: 30)",
    )
    parser.add_argument(
        "--fail-on-dirty",
        action="store_true",
        help="return exit code 1 when any worktree is dirty or cannot be inspected",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.timeout <= 0:
        print("--timeout must be greater than zero", file=sys.stderr)
        return 2
    if args.max_paths < -1:
        print("--max-paths must be -1 or greater", file=sys.stderr)
        return 2

    try:
        report = inspect_repo(
            args.repo,
            timeout_seconds=args.timeout,
            max_paths=args.max_paths,
        )
    except (GitInspectionError, subprocess.TimeoutExpired) as exc:
        print(f"worktree inspection failed: {exc}", file=sys.stderr)
        return 2

    if args.json:
        json.dump(report, sys.stdout, indent=2, ensure_ascii=False)
        sys.stdout.write("\n")
    else:
        _print_human(report, dirty_only=args.dirty_only)

    if args.fail_on_dirty and (report["dirtyCount"] or report["unknownCount"]):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
