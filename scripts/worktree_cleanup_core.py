"""Conservative evidence and marker primitives for linked-worktree cleanup."""
from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import json
import os
import subprocess
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Sequence

TOOL_VERSION = "worktree-cleanup-v3"
MARKER_SCHEMA_VERSION = 1
MARKER_STATE_SHOULD_CLEAN = "should-clean"
MARKER_STATE_MANUAL = "manual-review"
MARKER_STATE_RECYCLED = "recycled"
MARKER_STATE_CLEANED = MARKER_STATE_RECYCLED
MARKER_STATE_CLEANUP_FAILED = "cleanup-failed"
GIT_TIMEOUT_SECONDS = 30.0

# Runner lane registries, relative to the main worktree. Each lane directory holds a `meta.json`
# (ownership: `cwd`/`path`, `branch`) and, while it is alive, a sibling `status.json` (`state`).
# A lane that resolves a worktree path OWNS that path for cleanup purposes.
RUNNER_LANE_ROOTS: tuple[tuple[str, Path], ...] = (
    ("opencode", Path(".claude") / "opencode-agents" / "agents"),
    ("cmdc", Path(".claude") / "cmdc-agents" / "agents"),
)
# A lane state in this set is finished. Every other value — including an unrecognised one, and
# including a lane with no readable `status.json` — is treated as NOT finished, because a state
# the tool cannot read is not evidence that a runner stopped. Both finished and unfinished owners
# hold their worktree; the split only changes which blocker names the reason.
RUNNER_TERMINAL_STATES = frozenset({"done", "partial", "failed", "stopped", "budget_exhausted", "quota"})
RUNNER_BLOCKER = "managed-runner-session"
RUNNER_UNFINISHED_BLOCKER = "runner-lane-not-finished"


class CleanupError(RuntimeError):
    """A cleanup operation cannot be proven safe."""


def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat()


def canonical_json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def path_key(path: Path | str) -> str:
    return os.path.normcase(str(Path(path).resolve()))


def source_digest() -> str:
    paths = [Path(__file__), Path(__file__).with_name("inspect-worktrees.py")]
    return digest([path.read_bytes().hex() for path in paths])


def _load_inspector() -> Any:
    path = Path(__file__).with_name("inspect-worktrees.py")
    spec = importlib.util.spec_from_file_location("worktree_cleanup_inspector", path)
    if spec is None or spec.loader is None:
        raise CleanupError(f"cannot load inspector module: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_git(repo: Path, *args: str, timeout: float = GIT_TIMEOUT_SECONDS) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env["GIT_OPTIONAL_LOCKS"] = "0"
    try:
        return subprocess.run(
            ["git", "-C", str(repo), *args],
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            timeout=timeout,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise CleanupError(f"git command timed out: git {' '.join(args)}") from exc


def require_git(repo: Path, *args: str, timeout: float = GIT_TIMEOUT_SECONDS) -> str:
    result = run_git(repo, *args, timeout=timeout)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "git command failed").strip()
        raise CleanupError(f"git {' '.join(args)} failed ({result.returncode}): {detail}")
    return result.stdout.strip()


def _git_error(result: subprocess.CompletedProcess[str]) -> str:
    return (result.stderr or result.stdout or "git command failed").strip()


def common_dir(repo: Path) -> Path:
    value = require_git(repo, "rev-parse", "--git-common-dir")
    candidate = Path(value)
    if not candidate.is_absolute():
        candidate = repo / candidate
    return candidate.resolve()


def marker_dir(repo: Path) -> Path:
    return common_dir(repo) / "worktree-cleanup"


def marker_root_for(repo: Path) -> Path:
    return marker_dir(repo) / "markers"


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


def _parse_status(inspector: Any, text: str) -> dict[str, Any]:
    return inspector.parse_status_z(text)


def _session_records(main_root: Path) -> tuple[list[dict[str, Any]], list[str]]:
    records: list[dict[str, Any]] = []
    errors: list[str] = []
    directory = main_root / "tasks" / "sessions"
    if not directory.is_dir():
        return records, errors
    for path in sorted(directory.glob("*.json")):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(value, dict):
                raise ValueError("record is not an object")
            raw_worktree = value.get("worktree")
            worktree_value = None
            if raw_worktree is not None:
                if not isinstance(raw_worktree, str) or not raw_worktree.strip():
                    raise ValueError("worktree must be a non-empty string or null")
                worktree = Path(raw_worktree)
                if not worktree.is_absolute():
                    worktree = main_root / worktree
                worktree_value = str(worktree.resolve())
            records.append(
                {
                    "session": str(value.get("session") or path.stem),
                    "status": str(value.get("status") or "unknown"),
                    "branch": str(value.get("branch") or ""),
                    "mode": str(value.get("mode") or ""),
                    "worktree": worktree_value,
                    "record": str(path),
                }
            )
        except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError) as exc:
            errors.append(f"{path}: {exc}")
    return records, errors


def _read_json_object(path: Path) -> tuple[dict[str, Any] | None, str | None]:
    """Return (object, error). Never raises: a broken record reads as an error string, not a crash."""
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return None, str(exc)
    if not isinstance(value, dict):
        return None, "record is not an object"
    return value, None


def _lane_owner_path(value: dict[str, Any], main_root: Path) -> str | None:
    """Resolve the worktree a lane record owns, or None when the lane owns no worktree."""
    for key in ("cwd", "path"):
        raw = value.get(key)
        if raw is None:
            continue
        if not isinstance(raw, str) or not raw.strip():
            raise ValueError(f"{key} must be a non-empty string")
        candidate = Path(raw.strip())
        if not candidate.is_absolute():
            candidate = main_root / candidate
        return str(candidate.resolve())
    return None


def _lane_state(meta_path: Path) -> str:
    """Read the sibling `status.json` state. Absent or unreadable means 'unknown', never 'done'."""
    status_path = meta_path.with_name("status.json")
    if not status_path.is_file():
        return "unknown"
    value, error = _read_json_object(status_path)
    if value is None or error is not None:
        return "unknown"
    state = value.get("state")
    if not isinstance(state, str) or not state.strip():
        return "unknown"
    return state.strip().lower()


def _manager_evidence(main_root: Path) -> tuple[list[dict[str, str]], list[str]]:
    """Legacy Kilo manager registry. Unchanged ownership semantics; unreadable input is an error."""
    evidence: list[dict[str, str]] = []
    manager = main_root / ".kilo" / "agent-manager.json"
    if not manager.is_file():
        return evidence, []
    value, error = _read_json_object(manager)
    if value is None:
        return evidence, [f"{manager}: {error}"]
    worktrees = value.get("worktrees", {}) if isinstance(value, dict) else {}
    sessions = value.get("sessions", {}) if isinstance(value, dict) else {}
    if not isinstance(worktrees, dict) or not isinstance(sessions, dict):
        return evidence, [f"{manager}: invalid"]
    for session_id, session in sessions.items():
        if not isinstance(session, dict):
            continue
        worktree_id = session.get("worktreeId")
        worktree = worktrees.get(worktree_id)
        if isinstance(worktree, dict) and isinstance(worktree.get("path"), str):
            evidence.append(
                {
                    "source": str(manager),
                    "session": str(session_id),
                    "path": worktree["path"],
                    "state": "managed",
                }
            )
    return evidence, []


def _lane_evidence(main_root: Path) -> tuple[list[dict[str, str]], list[str]]:
    """Enumerate the OpenCode and cmdc lane registries this repository's runners actually write."""
    evidence: list[dict[str, str]] = []
    errors: list[str] = []
    for runner, relative in RUNNER_LANE_ROOTS:
        directory = main_root / relative
        if not directory.is_dir():
            continue
        for meta_path in sorted(directory.glob("*/meta.json")):
            value, error = _read_json_object(meta_path)
            if value is None:
                # A corrupt lane record hides its own worktree, so its path cannot be held
                # individually. Surfacing it as an error holds every candidate instead of
                # silently proceeding — the fail-closed reading of an unreadable registry.
                errors.append(f"{meta_path}: {error}")
                continue
            try:
                owned = _lane_owner_path(value, main_root)
            except ValueError as exc:
                errors.append(f"{meta_path}: {exc}")
                continue
            if owned is None:
                # A well-formed lane that resolves no worktree owns none. Review-only lanes are
                # real records in this estate, so treating them as corruption would hold every
                # worktree in the repository and make the tool unusable.
                continue
            evidence.append(
                {
                    "source": str(meta_path),
                    "session": str(value.get("id") or meta_path.parent.name),
                    "path": owned,
                    "state": _lane_state(meta_path),
                    "runner": runner,
                }
            )
    return evidence, errors


def _runner_evidence(main_root: Path) -> tuple[list[dict[str, str]], list[str]]:
    """Return (ownership evidence, registry errors) across every runner registry in the repo.

    Reads the legacy `.kilo/agent-manager.json`, then the OpenCode and cmdc lane registries.
    A record that resolves a worktree path is an owner of that path and the worktree is never
    `should-clean`, whatever the lane's state. Never raises on unreadable input.
    """
    evidence: list[dict[str, str]] = []
    errors: list[str] = []
    for reader in (_manager_evidence, _lane_evidence):
        found, problems = reader(main_root)
        evidence.extend(found)
        errors.extend(problems)
    return evidence, errors


def _is_ancestor(repo: Path, head: str, ref: str) -> tuple[bool, str | None]:
    result = run_git(repo, "merge-base", "--is-ancestor", head, ref)
    if result.returncode == 0:
        return True, None
    if result.returncode == 1:
        return False, None
    return False, _git_error(result)


def _upstream_ref(repo: Path, branch: str, configured: str | None) -> tuple[str | None, str | None]:
    if configured:
        if not configured.startswith("refs/remotes/"):
            return None, "configured upstream must be a remote-tracking ref"
        return configured, None
    result = run_git(repo, "rev-parse", "--abbrev-ref", "--symbolic-full-name", f"{branch}@{{upstream}}")
    if result.returncode != 0:
        return None, _git_error(result)
    value = result.stdout.strip()
    return (value or None), None


def _status(repo: Path, inspector: Any) -> tuple[dict[str, Any], str | None]:
    result = run_git(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching")
    if result.returncode != 0:
        return _empty_status(), _git_error(result)
    return _parse_status(inspector, result.stdout), None


def _candidate_item(
    inspector: Any,
    record: dict[str, Any],
    main_root: Path,
    owner_records: list[dict[str, Any]],
    runner_records: list[dict[str, str]],
    global_blockers: list[str],
    integration_ref: str,
    configured_upstream: str | None,
    upstream_refs: dict[str, str],
    timeout: float,
) -> dict[str, Any]:
    path = Path(record["path"]).resolve()
    key = path_key(path)
    item: dict[str, Any] = {
        **record,
        "path": str(path),
        "exists": path.is_dir(),
        "owners": [
            owner
            for owner in owner_records
            if (owner["worktree"] is not None and path_key(owner["worktree"]) == key)
            or (owner["worktree"] is None and owner["branch"] == record.get("branch"))
        ],
        "runnerEvidence": [
            owner for owner in runner_records if owner.get("path") and path_key(owner["path"]) == key
        ],
        "blockers": list(global_blockers),
        "completion": {},
    }
    if key == path_key(main_root):
        item["blockers"].append("main-worktree")
        item["state"] = MARKER_STATE_MANUAL
        return item
    if record.get("bare"):
        item["blockers"].extend(["bare-worktree"])
        item["state"] = MARKER_STATE_MANUAL
        return item
    if not item["exists"]:
        item["blockers"].append("missing-worktree-path")
        item["state"] = MARKER_STATE_MANUAL
        return item
    if record.get("detached") or not record.get("branch"):
        item["blockers"].append("detached-head")
        item["state"] = MARKER_STATE_MANUAL
        return item
    if record.get("locked") is not None:
        item["blockers"].append("locked-worktree")
    if record.get("prunable") is not None:
        item["blockers"].append("prunable-worktree")
    status, status_error = _status(path, inspector)
    item["status"] = status
    item["statusDigest"] = digest(status)
    if status_error:
        item["blockers"].append("status-unreadable")
        item["statusError"] = status_error
    elif status["changed"]:
        item["blockers"].append("dirty-or-ignored-worktree")
    try:
        head = require_git(path, "rev-parse", "HEAD", timeout=timeout)
    except CleanupError as exc:
        item["blockers"].append("head-unreadable")
        item["blockers"].append(str(exc))
        head = record.get("head") or ""
    item["head"] = head
    try:
        actual_branch = require_git(path, "symbolic-ref", "--quiet", "--short", "HEAD", timeout=timeout)
    except CleanupError:
        actual_branch = "HEAD"
    item["actualBranch"] = actual_branch
    if actual_branch != record.get("branch"):
        item["blockers"].append("branch-registry-drift")
    active_owners = [owner for owner in item["owners"] if owner["status"] == "active"]
    if active_owners:
        item["blockers"].append("active-session-owner")
    if not item["owners"]:
        item["blockers"].append("no-session-owner")
    for owner in item["owners"]:
        if owner["branch"] and owner["branch"] != record.get("branch"):
            item["blockers"].append("session-branch-mismatch")
            break
    if item["runnerEvidence"]:
        # Any runner record that resolves this path is an owner, finished or not: a stale owner is
        # a human decision, never an automatic removal. The unfinished blocker names the subset
        # whose `status.json` state is not one of RUNNER_TERMINAL_STATES (including "unknown").
        item["blockers"].append(RUNNER_BLOCKER)
        if any(
            owner.get("runner") and owner.get("state") not in RUNNER_TERMINAL_STATES
            for owner in item["runnerEvidence"]
        ):
            item["blockers"].append(RUNNER_UNFINISHED_BLOCKER)
    if not head:
        item["completion"] = {"complete": False, "merged": False, "pushed": False}
        item["state"] = MARKER_STATE_MANUAL
        return item
    integration_error = None
    merged, integration_error = _is_ancestor(path, head, integration_ref)
    upstream, upstream_error = _upstream_ref(path, record["branch"], upstream_refs.get(record["branch"]))
    pushed = False
    if upstream:
        pushed, upstream_error = _is_ancestor(path, head, upstream)
    elif upstream_error is None:
        upstream_error = "no configured upstream"
    item["completion"] = {
        "complete": merged or pushed,
        "merged": merged,
        "pushed": pushed,
        "integrationRef": integration_ref,
        "upstreamRef": upstream,
        "integrationError": integration_error,
        "upstreamError": upstream_error,
    }
    if not merged and not pushed:
        item["blockers"].append("unmerged-or-unpushed")
    if integration_error and not merged:
        item["blockers"].append("integration-ref-unreadable")
    if upstream_error and not merged and not pushed:
        item["blockers"].append("upstream-unreadable")
    item["blockers"] = list(dict.fromkeys(item["blockers"]))
    item["state"] = MARKER_STATE_SHOULD_CLEAN if not item["blockers"] else MARKER_STATE_MANUAL
    item["evidenceFingerprint"] = digest(
        {
            "toolVersion": TOOL_VERSION,
            "sourceDigest": source_digest(),
            "repoCommonDir": path_key(common_dir(path)),
            "mainWorktree": path_key(main_root),
            "integrationRef": integration_ref,
            "configuredUpstream": configured_upstream,
            "upstreamRefs": upstream_refs,
            "worktree": item,
        }
    )
    return item


def build_report(
    repo: Path,
    integration_ref: str,
    configured_upstream: str | None = None,
    upstream_refs: dict[str, str] | None = None,
    timeout: float = GIT_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """Return a complete read-only classification report for every registered worktree."""
    inspector = _load_inspector()
    listed = run_git(repo, "worktree", "list", "--porcelain", timeout=timeout)
    if listed.returncode != 0:
        raise CleanupError(_git_error(listed))
    records = inspector.parse_worktree_porcelain(listed.stdout)
    if not records:
        raise CleanupError("git returned no worktree records")
    main_root = Path(records[0]["path"]).resolve()
    owner_records, owner_errors = _session_records(main_root)
    runner_records, runner_errors = _runner_evidence(main_root)
    upstream_refs = dict(upstream_refs or {})
    for ref in upstream_refs.values():
        if not ref.startswith("refs/remotes/"):
            raise CleanupError(f"upstream ref must be remote-tracking: {ref}")
    global_blockers: list[str] = []
    if owner_errors:
        global_blockers.append("session-record-errors")
    if runner_errors:
        global_blockers.append("runner-record-errors")
    items = [
        _candidate_item(
            inspector,
            record,
            main_root,
            owner_records,
            runner_records,
            global_blockers,
            integration_ref,
            configured_upstream,
            upstream_refs,
            timeout,
        )
        for record in records
    ]
    return {
        "schemaVersion": MARKER_SCHEMA_VERSION,
        "toolVersion": TOOL_VERSION,
        "generatedUtc": utcnow(),
        "repoRoot": str(repo.resolve()),
        "mainWorktree": str(main_root),
        "repoCommonDir": str(common_dir(repo)),
        "integrationRef": integration_ref,
        "configuredUpstream": configured_upstream,
        "upstreamRefs": upstream_refs,
        "sessionRecordErrors": owner_errors,
        "runnerRecordErrors": runner_errors,
        "worktrees": items,
    }


def marker_id(common: Path, worktree_path: Path) -> str:
    return "wt-" + digest([path_key(common), path_key(worktree_path)])[:20]


def confirmation_token(marker: dict[str, Any]) -> str:
    return "confirm-" + digest([marker["markerId"], marker["evidenceFingerprint"]])[:20]


def marker_for_item(report: dict[str, Any], item: dict[str, Any]) -> dict[str, Any]:
    mid = marker_id(Path(report["repoCommonDir"]), Path(item["path"]))
    state = item.get("state", MARKER_STATE_MANUAL)
    marker = {
        "schemaVersion": MARKER_SCHEMA_VERSION,
        "toolVersion": TOOL_VERSION,
        "markerId": mid,
        "state": state,
        "createdUtc": utcnow(),
        "repoCommonDir": report["repoCommonDir"],
        "mainWorktree": report["mainWorktree"],
        "integrationRef": report["integrationRef"],
        "configuredUpstream": report.get("configuredUpstream"),
        "upstreamRefs": report.get("upstreamRefs", {}),
        "sourceDigest": source_digest(),
        "worktree": {
            "path": item["path"],
            "branch": item.get("branch"),
            "head": item.get("head"),
            "statusDigest": item.get("statusDigest"),
            "clean": item.get("status", {}).get("changed", 1) == 0,
            "owners": item.get("owners", []),
        },
        "evidence": item,
        "blockers": item.get("blockers", []),
        "evidenceFingerprint": item.get("evidenceFingerprint"),
    }
    marker["confirmationToken"] = confirmation_token(marker)
    return marker


def _atomic_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, indent=2, sort_keys=True, ensure_ascii=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


@contextlib.contextmanager
def marker_lock(repo: Path, timeout: float = 5.0) -> Iterator[None]:
    directory = marker_dir(repo)
    directory.mkdir(parents=True, exist_ok=True)
    lock = directory / "markers.lock"
    started = time.monotonic()
    while True:
        try:
            fd = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, str(os.getpid()).encode("ascii"))
            os.close(fd)
            break
        except FileExistsError as exc:
            if time.monotonic() - started >= timeout:
                raise CleanupError(f"marker lock is busy: {lock}") from exc
            time.sleep(0.05)
    try:
        yield
    finally:
        try:
            lock.unlink()
        except FileNotFoundError:
            pass


def write_marker(repo: Path, marker: dict[str, Any]) -> Path:
    path = marker_root_for(repo) / f"{marker['markerId']}.json"
    _atomic_json(path, marker)
    return path


def read_marker(repo: Path, marker_name: str) -> dict[str, Any]:
    if not marker_name or Path(marker_name).name != marker_name or not marker_name.endswith(".json"):
        raise CleanupError(f"invalid marker name: {marker_name}")
    path = marker_root_for(repo) / marker_name
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise CleanupError(f"cannot read marker {marker_name}: {exc}") from exc
    if not isinstance(value, dict) or not isinstance(value.get("markerId"), str):
        raise CleanupError(f"invalid marker {marker_name}")
    return value


def remove_marker(repo: Path, marker_name: str) -> None:
    path = marker_root_for(repo) / marker_name
    if path.exists():
        path.unlink()


def list_markers(repo: Path) -> list[dict[str, Any]]:
    directory = marker_root_for(repo)
    if not directory.is_dir():
        return []
    values: list[dict[str, Any]] = []
    for path in sorted(directory.glob("wt-*.json")):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            values.append({"markerId": path.stem, "state": "unreadable", "error": str(exc)})
            continue
        if isinstance(value, dict):
            value["markerFile"] = path.name
            values.append(value)
    return values


def mark_report(repo: Path, report: dict[str, Any]) -> list[dict[str, Any]]:
    markers: list[dict[str, Any]] = []
    with marker_lock(repo):
        for item in report["worktrees"]:
            marker = marker_for_item(report, item)
            write_marker(repo, marker)
            markers.append(marker)
    return markers


def effective_marker_state(marker: dict[str, Any], current_item: dict[str, Any] | None) -> str:
    if marker.get("state") == MARKER_STATE_CLEANED:
        return MARKER_STATE_CLEANED
    if marker.get("state") == MARKER_STATE_MANUAL:
        return MARKER_STATE_MANUAL
    if current_item is None or marker.get("evidenceFingerprint") != current_item.get("evidenceFingerprint"):
        return "stale"
    return marker.get("state", "unknown")


def recycle_directory(path: Path) -> dict[str, Any]:
    """Move a directory to the Windows Recycle Bin and return a durable receipt."""
    if os.name != "nt":
        raise CleanupError("recycle-bin cleanup is supported only on Windows")
    if not path.is_dir():
        raise CleanupError(f"cannot recycle missing worktree directory: {path}")
    path_string = str(path)
    if path_string.rstrip("\\/") == str(path.anchor).rstrip("\\/"):
        raise CleanupError("refusing to recycle a filesystem root")
    script = (
        "Add-Type -AssemblyName Microsoft.VisualBasic; "
        "[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory("
        "$env:WORKTREE_RECYCLE_PATH, [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs, "
        "[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)"
    )
    env = os.environ.copy()
    env["WORKTREE_RECYCLE_PATH"] = path_string
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=120,
        check=False,
    )
    if result.returncode != 0:
        raise CleanupError(f"recycle-bin operation failed: {(result.stderr or result.stdout).strip()}")
    if path.exists():
        raise CleanupError(f"recycle-bin operation reported success but directory remains: {path}")
    return {
        "backend": "windows-recycle-bin",
        "sourcePath": path_string,
        "recycledUtc": utcnow(),
    }


def remove_worktree(repo: Path, marker: dict[str, Any], report: dict[str, Any], confirmation: str) -> dict[str, Any]:
    if marker.get("state") != MARKER_STATE_SHOULD_CLEAN:
        raise CleanupError(f"marker is not consumable: {marker.get('state')}")
    if confirmation != marker.get("confirmationToken"):
        raise CleanupError("confirmation token does not match marker")
    if marker.get("repoCommonDir") != str(common_dir(repo).resolve()):
        raise CleanupError("marker belongs to a different Git common directory")
    with marker_lock(repo):
        current = read_marker(repo, marker["markerId"] + ".json")
        if current.get("state") == MARKER_STATE_CLEANED:
            return current
        if current.get("state") != MARKER_STATE_SHOULD_CLEAN:
            raise CleanupError(f"marker changed to non-cleanable state: {current.get('state')}")
        if confirmation != current.get("confirmationToken"):
            raise CleanupError("confirmation token no longer matches the stored marker")
        current_report = build_report(
            repo,
            current["integrationRef"],
            current.get("configuredUpstream"),
            current.get("upstreamRefs", {}),
        )
        current_item = next(
            (item for item in current_report["worktrees"] if path_key(item["path"]) == path_key(current["worktree"]["path"])),
            None,
        )
        if current_item is None:
            raise CleanupError("marked worktree is no longer registered")
        if current_item.get("state") != MARKER_STATE_SHOULD_CLEAN:
            raise CleanupError(f"current candidate is blocked: {', '.join(current_item.get('blockers', []))}")
        if current_item.get("evidenceFingerprint") != current.get("evidenceFingerprint"):
            raise CleanupError("candidate evidence changed after marking")
        path = Path(current_item["path"])
        try:
            recycle_receipt = recycle_directory(path)
        except CleanupError as exc:
            failed = dict(current)
            failed["state"] = MARKER_STATE_CLEANUP_FAILED
            failed["cleanupError"] = str(exc)
            failed["cleanupFailedUtc"] = utcnow()
            write_marker(repo, failed)
            raise
        result = run_git(repo, "worktree", "remove", str(path))
        if result.returncode != 0:
            failed = dict(current)
            failed["state"] = MARKER_STATE_CLEANUP_FAILED
            failed["cleanupError"] = _git_error(result)
            failed["cleanupFailedUtc"] = utcnow()
            failed["recycleReceipt"] = recycle_receipt
            failed["recoveryRequired"] = True
            write_marker(repo, failed)
            raise CleanupError(
                f"worktree was recycled but Git unregistration failed ({result.returncode}): "
                f"{_git_error(result)}; restore the recycled directory at {path} before retrying"
            )
        cleaned = dict(current)
        cleaned["state"] = MARKER_STATE_CLEANED
        cleaned["cleanedUtc"] = utcnow()
        cleaned["recycleReceipt"] = recycle_receipt
        cleaned["cleanup"] = {
            "recycleWorktree": True,
            "permanentDelete": False,
            "unregisterWorktree": True,
            "deleteBranch": False,
        }
        write_marker(repo, cleaned)
        return cleaned


def load_report_item(report: dict[str, Any], path: Path) -> dict[str, Any] | None:
    return next((item for item in report["worktrees"] if path_key(item["path"]) == path_key(path)), None)
