#!/usr/bin/env python3
"""Plan and run the path-owned verification boundary for a change (`gk-core/scripts/verify-change.py`).

This is the entry point AGENTS.md's "Verification boundary" section names, and the Python port of
`scripts/verify-change.ps1`. It answers one question — *given these changed paths, which focused tests,
guards and static checks hold that change's contract?* — and then runs exactly those, never a broad
suite. Unfiltered full evidence is CI/nightly/release-owned; the last line of every plan says so.

**Why the port (owner-authorized 2026-09-26), two measured reasons:**

1. *The guard step's `dotnet test` could not finish the job.* The guard module check spawned
   `tests/FusionRpg.Guard.Tests/bin/Release/net8.0/testhost.exe`, which OUTLIVED the `dotnet test`
   call, so the next build of the same project died with `MSB3027 "Could not copy ...
   FusionRpg.Guard.Tests.dll ... The file is locked by: testhost"`. Reproduced twice on 2026-09-26.
   This port never lets a test spawn a build: it runs `dotnet build` ONCE per project and then every
   `dotnet test` with `--no-build` (the same shape `scripts/test-sharded.ps1` already uses, and the
   same defect class `cold-process-test-build-20260912-e5b1` fixed for tool tests).
2. *PowerShell loses output silently.* `Write-Host` writes the INFORMATION stream, so `2>&1` captures
   nothing from a script that is working correctly. Here every external call captures stdout AND
   stderr through `subprocess.run(capture_output=True, timeout=...)` — there is no stream-6 trap and no
   `*>&1` incantation to remember.

**The contract is the plan.** The per-path owner selection, the selected checks, the
`full evidence: CI/nightly/release` line, the JSON plan shape and the exit codes are all what other
tools and agents depend on, so they are reproduced exactly. `gk-core/tests/tools/test_verify_change.py`
asserts this by running BOTH implementations over the same inputs and failing if they disagree — the
PowerShell pair was still live when this note was written (`guard-verification-boundaries.ps1`, ported 2026-09-28; several Guard tests are outside
the porting lane's fence), so the two must be proven equal, not assumed equal.

Tool discipline, all of it (AGENTS.md "Language for new tooling"): a hard `--timeout` on every
external call, a NAMED refusal the moment a precondition fails (fail closed, never "continue and
report empty"), machine-readable output (`--format json`, and `--json` as its alias), and a non-zero
exit that names the failing stage.

Usage:
    python gk-core/scripts/verify-change.py --paths src/A.cs tests/A.Tests/B.cs --session <id>
    python gk-core/scripts/verify-change.py --paths docs/x.md --allow-unscoped --plan-only
    python gk-core/scripts/verify-change.py --paths a.cs --deleted-paths b.cs --session <id> --format json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from verification_boundaries import (  # noqa: E402  (the path shim above must run first)
    ACCEPTED_VERIFICATION_SCHEMA_VERSION,
    derived_level,
    junit_test_cases,
    pattern_match,
    project_has_trait,
    project_members,
    project_runner,
    pytest_project_dirs,
    resolve_known_red_outcome,
    resolve_owner,
    wildcard_match,
)

HERE = Path(__file__).resolve().parent
REPO = HERE.parent

# The default per-call budget. A module check on `FusionRpg.Data.Tests` is a ~10 minute run, so this
# is deliberately generous — it exists to turn a HANG into a named refusal, not to bound normal work.
DEFAULT_TIMEOUT = 1800.0
# The registry-integrity pre-check walks the registry but not the tree (`-SkipCoverageWalk`), so it is
# minutes, not hours. Kept separate so the cheap precondition cannot be starved by a slow test budget.
INTEGRITY_TIMEOUT = 900.0

# The default local profile's filter lives in exactly one place, `gk-core/scripts/test_fast.py`
# (testing-standard.md §6, "The default lives in exactly one place") — read it rather than restating
# the string a second time here, exactly as the PowerShell original did.
DEFAULT_FILTER_PATTERN = re.compile(r'^\s*\$?filter\s*=\s*"([^"]+)"', re.MULTILINE | re.IGNORECASE)

SESSION_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
ROOTED_PATH_PATTERN = re.compile(r"^(?:[A-Za-z]:|[\\/])")
TRAVERSAL_PATTERN = re.compile(r"(^|[\\/])\.\.([\\/]|$)")

REFUSALS = {
    "ROOT-UNRESOLVABLE": "--root does not resolve to an existing directory",
    "TOOL-MISSING": "a required tool is neither a .py nor a .ps1 on disk, so the step cannot run",
    "INTEGRITY-GUARD-FAILED": "the verification registry integrity guard refused the registry",
    "REGISTRY-MISSING": "scripts/verification-boundaries.v1.json is absent",
    "REGISTRY-UNREADABLE": "the verification registry is not valid JSON",
    "REGISTRY-SCHEMA": "the registry's schemaVersion is not the one this planner understands",
    "ENFORCEMENT-REGISTRY-MISSING": "scripts/enforcement-registry.v1.json is absent, so guard ids have no catalog",
    "ENFORCEMENT-REGISTRY-UNREADABLE": "the enforcement registry is not valid JSON",
    "ENFORCEMENT-SCHEMA": "the enforcement registry's schemaVersion is not 1",
    "INVALID-EXEMPTION": "a verificationExemptions entry is missing id, paths or reason, or names a catch-all root",
    "DEFAULT-FILTER-UNREADABLE": "the default local profile filter could not be read from scripts/test_fast.py",
    "NO-PATHS": "supply at least one repository-relative --paths or --deleted-paths value",
    "SESSION-REQUIRED": "a session id is required for agent verification (--allow-unscoped is the maintainer escape)",
    "SESSION-ID-INVALID": "the session id is not a legal record name",
    "SESSION-RECORD-MISSING": "tasks/sessions/<id>.json does not exist",
    "SESSION-RECORD-INVALID": "the session record is not valid JSON",
    "SESSION-NOT-ACTIVE": "the session record's status is not 'active'",
    "SESSION-NO-PATHS": "the session record declares no paths, so it cannot fence anything",
    "PATH-OUTSIDE-SESSION": "a path is outside the session's declared fence",
    "PATH-NOT-RELATIVE": "a path is not repository-relative, or contains a '..' segment",
    "PATH-EMPTY": "a path is empty after normalization",
    "PATH-NOT-FOUND": "a --paths value does not exist in the repository (use --deleted-paths for a removed file)",
    "DIFF-FENCE-INCOMPLETE": "reviewed verification needs both --diff-base-ref and --diff-head-ref",
    "DIFF-FENCE-UNSCOPED": "reviewed verification needs --session so the diff has a fence",
    "DIFF-ESCAPED-FENCE": "session-boundary-check refused the reviewed diff",
    "BOUNDARY-MISSING": "no owner boundary and no explicit exemption maps this path",
    "UNSUPPORTED-CHECK-KIND": "a check names a file whose extension the runner cannot dispatch",
    "BOUNDARY-AMBIGUOUS": "two or more owner boundaries tie at the winning specificity",
    "TOOL-MISSING": "an external tool this check needs is not on PATH",
    "UNKNOWN-CHECK": "the plan names a check kind, project or guard the registry does not define",
    "TEMP-CLEANUP": "a temp results directory could not be deleted (testing-standard.md R3: a failed cleanup is a failure)",
    "BUILD-FAILED": "dotnet build failed for a selected project",
    "TEST-FAILED": "a selected dotnet test invocation failed",
    "ZERO-TESTS": "a selected dotnet test invocation executed zero tests (a zero-match filter is RED)",
    "TEST-EVIDENCE-AMBIGUOUS": "a selected dotnet test invocation did not leave exactly one TRX evidence file",
    "PYTEST-ENV-MISSING": "python -m pytest is unavailable, so a pytest check cannot run",
    "PYTEST-COLLECTED-NOTHING": "pytest collected no tests for the registry's selector (a registry selector defect)",
    "PYTEST-UNEXPECTED-EXIT": "pytest exited with a code that is neither success, test-failure, nor no-collect",
    "ZERO-PYTESTS": "a pytest run executed zero tests (a registry selector defect)",
    "KNOWN-RED-FAILED": "a pytest run's outcome is not clean once knownRed is accounted for",
    # RAISED BY THIS TOOL SINCE BEFORE IT WAS REGISTERED. verify-change() refuses when the workspace
    # resolver cannot name the root that owns the session records, and it has always used this name -
    # but the name was absent from this table, so the Refusal constructor raised KeyError and the tool
    # died with a traceback instead of the named refusal it meant to report. That took three sibling
    # tests down with it, because they reach the same path through a planted fixture where the resolver
    # legitimately cannot answer.
    "SESSION-ROOT-UNRESOLVABLE": "the workspace resolver could not name the root that owns the "
                                "session records, so the session fence cannot be checked",
    "TIMEOUT": "an external command exceeded its hard timeout",
}


class Refusal(RuntimeError):
    """A named refusal. The tool prints the name, the detail, and exits non-zero."""

    def __init__(self, name: str, detail: str, *, stage: str = "plan") -> None:
        # AN UNREGISTERED NAME IS STILL A NAMED REFUSAL. This raised KeyError, and that is the wrong
        # failure in both directions: a tool that cannot describe a refusal cannot report it either, so
        # the reader got a traceback instead of the name, the detail and the stage - and the one piece of
        # information that would have said which check to look at. It also made adding a refusal a
        # two-place edit where forgetting the second place turned a clean refusal into a crash, which is
        # how `SESSION-ROOT-UNRESOLVABLE` cost three sibling tests a day before anyone noticed.
        #
        # So the name is recorded either way, `described` says whether this table can explain it, and the
        # printer says so. A missing description is a gap in the documentation of a refusal, not a reason
        # to refuse describing it.
        self.described = name in REFUSALS
        super().__init__(f"{name}: {detail}")
        self.name = name
        self.detail = detail
        self.stage = stage


# --------------------------------------------------------------------------------------------
# small helpers
# --------------------------------------------------------------------------------------------

def _sort_key(*values: Any) -> tuple:
    """PowerShell's `Sort-Object` is case-insensitive by default; this is its closest cheap analogue.

    A culture-sensitive collation is not reproducible across machines, so the tie-break is the exact
    string. Every id and path the planner sorts is already lower-case kebab, so the two agree.
    """
    return tuple((str(value or "").lower(), str(value or "")) for value in values)


def _unique_sorted(values: Sequence[str]) -> list[str]:
    """`Sort-Object -Unique` over strings: case-insensitive uniqueness, then sorted order."""
    return sorted(set(values), key=lambda v: (v.lower(), v))


def _which(tool: str) -> str:
    found = shutil.which(tool)
    if not found:
        raise Refusal("TOOL-MISSING", f"required tool is not on PATH: {tool}")
    return found


def _run(argv: Sequence[str], *, timeout: float, cwd: Path | None = None, stage: str) -> subprocess.CompletedProcess:
    """Run an external command, capturing BOTH streams, with a hard timeout. No shell, ever."""
    try:
        return subprocess.run(  # noqa: S603 - argv list, shell=False
            list(argv),
            cwd=str(cwd) if cwd else None,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise Refusal("TIMEOUT", f"{' '.join(str(a) for a in argv)} exceeded {timeout:g}s", stage=stage) from exc
    except OSError as exc:
        raise Refusal("TOOL-MISSING", f"could not start {' '.join(str(a) for a in argv)}: {exc}", stage=stage) from exc


def _powershell_argv(script: Path, script_args: Sequence[str]) -> list[str]:
    """How this repo invokes a `.ps1` from a tool: `pwsh -NoProfile -NonInteractive`, never a shell.

    `pwsh` (PowerShell 7) is preferred over Windows PowerShell 5.1 — the same choice
    `gk-fusion/scripts/deploy-play.py` makes — and `powershell` is the documented fallback so the tool still
    works on a machine that only has 5.1.
    """
    shell = shutil.which("pwsh") or shutil.which("powershell")
    if not shell:
        raise Refusal("TOOL-MISSING", "neither pwsh nor powershell is on PATH")
    return [shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(script), *script_args]


def _resolve_tool_argv(root: Path, stem: str, tool_args: Sequence[str],
                       py_args: Sequence[str] | None = None) -> list[str]:
    """Find `scripts/<stem>.py` or `scripts/<stem>.ps1` and build the argv that runs it.

    Some tools are mid-migration, so a caller that hardcoded one extension breaks the
    moment the file moves. This prefers the Python port and falls back to the
    PowerShell original, which is what lets a tool be ported without editing every
    caller in the same commit. `refusal_code` names what was missing, because
    "it is not there" and "it is there but I cannot launch it" are different faults.

    `py_args` is the ARGUMENT dialect, and it is a separate parameter from `tool_args` on purpose.
    Resolving the FILE by dialect is not enough: the two spellings disagree about how a flag is
    written, so a caller that hardcoded PowerShell's `-RepoRoot`/`-RequireDiffFence` handed them to
    argparse the moment the port landed, and argparse rejected every one of them. That is not a
    hypothetical - it is what `session-boundary-check` did on its first port, and it reads as
    "the diff fence is broken" rather than "a caller passed the wrong spelling". So the caller
    states BOTH dialects and the resolver picks the one matching the interpreter it found, which
    also keeps a revert (deleting the `.py`) working with the PowerShell spelling intact.
    """
    py = root / "scripts" / f"{stem}.py"
    if py.is_file():
        return [_which("python"), str(py), *(py_args if py_args is not None else tool_args)]
    ps1 = root / "scripts" / f"{stem}.ps1"
    if ps1.is_file():
        return _powershell_argv(ps1, tool_args)
    raise Refusal("TOOL-MISSING", f"neither scripts/{stem}.py nor scripts/{stem}.ps1 exists")


def _drain_temp_dir(path: Path) -> None:
    """Delete a temp results directory. A failure is a FAILURE, never a swallow (R3)."""
    if not path.exists():
        return
    try:
        shutil.rmtree(path)
    except OSError as exc:
        raise Refusal("TEMP-CLEANUP", f"{path}: {exc}", stage="cleanup") from exc
    if path.exists():
        raise Refusal("TEMP-CLEANUP", f"{path}: still present after delete", stage="cleanup")


def _tail(*streams: str, limit: int = 12) -> str:
    """The last few non-empty lines of captured output, for a refusal detail.

    The output is ALSO echoed to stderr as it streams; this copy is what makes a refusal
    self-contained for a reader that only kept the exit code and the message.
    """
    kept = [line for line in "\n".join(streams).splitlines() if line.strip()]
    return " | ".join(kept[-limit:])


def _script_accepts_session(script: Path) -> bool:
    """Does this `.ps1` declare a `Session` parameter in its own `param(...)` block?

    The PowerShell original asked the PowerShell type system
    (``(Get-Command -Name $scriptPath).Parameters.ContainsKey('Session')``). Parsing the declared
    block is the same question asked of the source, and it needs no PowerShell process per guard.
    """
    try:
        text = script.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    start = text.find("param(")
    if start < 0:
        return False
    depth = 0
    end = -1
    for index in range(start + len("param(") - 1, len(text)):
        if text[index] == "(":
            depth += 1
        elif text[index] == ")":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < 0:
        return False
    return re.search(r"\$Session\b", text[start:end]) is not None


# --------------------------------------------------------------------------------------------
# inputs
# --------------------------------------------------------------------------------------------

@dataclass
class Inputs:
    """Everything the planner reads from the repository, validated once, up front."""

    root: Path
    registry: dict[str, Any]
    guard_catalog: dict[str, str]
    verification_exemptions: list[dict[str, Any]]
    sharded_project_ids: set[str]
    default_profile_filter: str


def _read_json(path: Path, *, missing: str, unreadable: str, stage: str = "plan") -> Any:
    if not path.is_file():
        raise Refusal(missing, str(path), stage=stage)
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal(unreadable, f"{path}: {exc}", stage=stage) from exc


def assert_registry_integrity(root: Path, timeout: float) -> None:
    """The registry's own STRUCTURE must be trustworthy before this call plans anything.

    Schema, shapes, no dangling project/guard references, level derivation — the whole structural
    pre-check, delegated to the one guard that owns it rather than reimplemented here. The full
    repo-wide completeness walk (every src/tests/tools file resolves to an owner) is CI/the standalone
    guard's job, not every local --plan-only call's; `-SkipCoverageWalk` is what turns this pre-check
    from a cost that scales with repo size into one that scales with the registry alone (measured:
    415s -> under 10s for a 2-path call under heavy machine contention, lane-b review, TVB4.3).
    """
    integrity = _run(
        _resolve_tool_argv(
            root, "guard-verification-boundaries",
            ["-Root", str(root), "-SkipCoverageWalk"],
            # The same call in each dialect, for the same reason as the diff-fence call above. This is
            # the THIRD caller that passed PowerShell flags to a resolver that picks the interpreter:
            # resolving the FILE by dialect is not resolving the ARGUMENTS by dialect, and each one
            # discovered it only once its tool's port landed.
            ["--root", str(root), "--skip-coverage-walk"]),
        timeout=min(timeout, INTEGRITY_TIMEOUT),
        cwd=root,
        stage="integrity-guard",
    )
    if integrity.returncode != 0:
        for line in (integrity.stdout + integrity.stderr).splitlines():
            if line.strip():
                print(line, file=sys.stderr)
        raise Refusal("INTEGRITY-GUARD-FAILED", f"exit {integrity.returncode} from the verification-boundary guard")


def load_registry_inputs(root: Path) -> Inputs:
    """Read and validate every registry file the plan depends on. Pure file reads, no subprocess."""
    registry = _read_json(root / "scripts" / "verification-boundaries.v1.json",
                          missing="REGISTRY-MISSING", unreadable="REGISTRY-UNREADABLE")
    if not isinstance(registry, dict) or registry.get("schemaVersion") != ACCEPTED_VERIFICATION_SCHEMA_VERSION:
        raise Refusal("REGISTRY-SCHEMA",
                      f"expected {ACCEPTED_VERIFICATION_SCHEMA_VERSION}, got "
                      f"{(registry or {}).get('schemaVersion') if isinstance(registry, dict) else 'no document'}")

    test_fast = root / "scripts" / "test_fast.py"
    try:
        filter_match = DEFAULT_FILTER_PATTERN.search(test_fast.read_text(encoding="utf-8"))
    except OSError as exc:
        raise Refusal("DEFAULT-FILTER-UNREADABLE", f"{test_fast}: {exc}") from exc
    if filter_match is None:
        raise Refusal("DEFAULT-FILTER-UNREADABLE", f"no `$Filter = \"...\"` assignment in {test_fast}")
    default_profile_filter = filter_match.group(1)

    # data-tests-sharding (TVB1.5): a project with a shard manifest entry runs its module-level check
    # through the sharded runner instead of one dotnet test process, so a local `data-fallback` change
    # gets the same process-boundary parallelism CI uses. Focused (VerificationId) runs stay a plain
    # dotnet test — they are small, and the runner's own overlap/manifest checks would be pure overhead.
    sharded: set[str] = set()
    shard_manifest = root / "scripts" / "test-shards.v1.json"
    if shard_manifest.is_file():
        try:
            sharded = set(json.loads(shard_manifest.read_text(encoding="utf-8")).get("projects", {}))
        except (OSError, json.JSONDecodeError) as exc:
            raise Refusal("REGISTRY-UNREADABLE", f"{shard_manifest}: {exc}") from exc

    # Guard ids resolve through the enforcement registry's catalog (solid-enforcement `guard-runner`):
    # this file carries no `guards` map, so there is exactly one id -> script catalog.
    enforcement = _read_json(root / "scripts" / "enforcement-registry.v1.json",
                             missing="ENFORCEMENT-REGISTRY-MISSING", unreadable="ENFORCEMENT-REGISTRY-UNREADABLE")
    if not isinstance(enforcement, dict) or enforcement.get("schemaVersion") != 1:
        raise Refusal("ENFORCEMENT-SCHEMA", f"expected 1, got {(enforcement or {}).get('schemaVersion')}")
    guard_catalog = {
        str(guard_id): str(spec.get("script", ""))
        for guard_id, spec in (enforcement.get("guards") or {}).items()
    }

    exemptions = list(enforcement.get("verificationExemptions") or [])
    for exemption in exemptions:
        if not str(exemption.get("id") or "").strip() or not str(exemption.get("reason") or "").strip() \
                or not list(exemption.get("paths") or []):
            raise Refusal("INVALID-EXEMPTION", "id, paths, and reason are required")
        for pattern in exemption["paths"]:
            text = str(pattern)
            if text.startswith("**") or re.match(r"^(scripts|tools|\.github)/\*\*", text) \
                    or re.match(r"^(scripts|tools|\.github)/\*/", text):
                raise Refusal("INVALID-EXEMPTION", f"exemption '{exemption['id']}' is a catch-all root: {text}")

    return Inputs(root=root, registry=registry, guard_catalog=guard_catalog,
                  verification_exemptions=exemptions, sharded_project_ids=sharded,
                  default_profile_filter=default_profile_filter)


def load_inputs(root: Path, timeout: float) -> Inputs:
    """Trust the registry, then load it. Every precondition fails CLOSED, never half-loaded."""
    assert_registry_integrity(root, timeout)
    return load_registry_inputs(root)


# --------------------------------------------------------------------------------------------
# planning
# --------------------------------------------------------------------------------------------

@dataclass
class Selection:
    """One path's resolved boundary — the per-path owner selection the plan prints."""

    path: str
    boundary: str
    project: str | None
    verification_id: str | None
    level: str
    guards: list[str]
    test_files: list[str]
    self_select: bool
    exemption_reason: str | None

    def as_json(self) -> dict[str, Any]:
        # The key set per kind is part of the plan contract. `exemptionReason` is present exactly when
        # the PowerShell original set it: on the two selection shapes that can be exempt (an
        # enforcement-catalog guard, and an explicit exemption) and absent on an owner/seam selection.
        payload: dict[str, Any] = {
            "path": self.path, "boundary": self.boundary, "project": self.project,
            "verificationId": self.verification_id, "level": self.level, "guards": self.guards,
            "testFiles": self.test_files, "selfSelect": self.self_select,
        }
        if self.exemption_reason is not None or self.boundary.startswith(("enforcement-", "exemption-")):
            payload["exemptionReason"] = self.exemption_reason
        return payload


@dataclass
class Check:
    """One selected check. The key set per kind is part of the JSON plan contract."""

    kind: str
    id: str
    level: str
    path: str
    verification_id: str | None = None
    runner: str | None = None
    targets: list[str] | None = None

    @property
    def dedupe_key(self) -> tuple[str, str, str]:
        return _sort_key(self.kind, self.id, self.verification_id)

    def as_json(self) -> dict[str, Any]:
        # Only the two check kinds that actually RUN something carry a runner and a target list; a
        # `guard`/`script`/`doc-citations` check has neither, and the plan shape says so.
        if self.kind not in ("test", "pytest"):
            payload = {"kind": self.kind, "id": self.id, "level": self.level, "path": self.path}
            if self.kind == "doc-citations":
                payload = {"kind": self.kind, "id": self.id, "verificationId": self.verification_id,
                           "level": self.level, "path": self.path}
            return payload
        return {"kind": self.kind, "id": self.id, "verificationId": self.verification_id,
                "level": self.level, "path": self.path, "runner": self.runner, "targets": self.targets}


@dataclass
class Plan:
    paths: list[str] = field(default_factory=list)
    selections: list[Selection] = field(default_factory=list)
    checks: list[Check] = field(default_factory=list)
    full_evidence_owner: str = "CI/nightly/release"

    def as_json(self) -> dict[str, Any]:
        return {
            "paths": self.paths,
            "selections": [selection.as_json() for selection in self.selections],
            "checks": [check.as_json() for check in self.checks],
            "fullEvidenceOwner": self.full_evidence_owner,
        }

    def render_text(self) -> str:
        """The plan exactly as the PowerShell original printed it (INFORMATION stream -> stdout).

        A `full`-level owner names no local check at all — CI/nightly/release owns that input's
        evidence — so it is printed distinctly and the plan never implies a check ran when none did.
        """
        lines = ["Verification plan:"]
        for selection in self.selections:
            if selection.level == "full":
                lines.append(f"  {selection.path} -> {selection.boundary} (full): no local check; "
                             "CI full evidence owns this input")
            elif selection.level == "exempt":
                lines.append(f"  {selection.path} -> {selection.boundary} (explicit exemption): "
                             f"{selection.exemption_reason}")
            else:
                lines.append(f"  {selection.path} -> {selection.boundary} ({selection.level})")
        for check in self.checks:
            suffix = " (sharded runner)" if (check.kind == "test" and check.runner == "sharded") else ""
            targets = check.targets or []
            target_suffix = f" [{', '.join(targets)}]" if check.kind in ("test", "pytest") and len(targets) > 1 else ""
            lines.append(f"  {check.kind}: {check.id} {check.verification_id or ''}{suffix}{target_suffix}")
        lines.append("  full evidence: CI/nightly/release")
        return "\n".join(lines)


def _normalize_path(raw: str, *, must_exist: bool, root: Path) -> str:
    """Repository-relative, forward-slashed, no traversal — and (for --paths) a real file."""
    text = str(raw or "").strip()
    if ROOTED_PATH_PATTERN.match(text) or TRAVERSAL_PATTERN.search(text):
        raise Refusal("PATH-NOT-RELATIVE", f"path must be repository-relative without traversal: {raw}")
    normalized = text.replace("\\", "/").lstrip("/")
    if not normalized:
        raise Refusal("PATH-EMPTY", f"path is empty: '{raw}'")
    if must_exist and not (root / normalized).is_file():
        raise Refusal("PATH-NOT-FOUND", f"{normalized} (a removed file belongs to --deleted-paths)")
    return normalized


def _check_session_fence(root: Path, session: str, normalized: Sequence[str]) -> None:
    """Every planned path must be inside the session's declared fence."""
    if not SESSION_ID_PATTERN.match(session):
        raise Refusal("SESSION-ID-INVALID", session)
    # Session records are WORKSPACE state, not repository state. `root` is the repository being
    # verified - gk-core - and gk-core has no tasks/ directory at all, so resolving the record from
    # `root` could only ever raise SESSION-RECORD-MISSING and refuse every fenced plan. The split
    # gave task records and session records to gk-workflow, whose root is the workspace that
    # CONTAINS this repository; that asymmetry is the same one KeepverseRoots.Workspace() exists to
    # express on the C# side, and keepverse_roots.workspace_root() is its Python half.
    #
    # This mattered more than a wrong path. Because the record was unresolvable here, the fence
    # check could not distinguish "no record" from "record outside the fence", so the guard suite's
    # failure count came to depend on which repository the harness was pointed at rather than on
    # what it was checking. Resolving from the owning root is what makes the answer mean anything.
    # PREFER THE RESOLVER, FALL BACK TO DIRECT EVIDENCE OF OWNERSHIP, REFUSE ONLY WHEN NEITHER ANSWERS.
    #
    # The resolver names the root that owns the session records by looking for a legacy repo or a
    # Keepverse workspace ABOVE this one, which is right in the workspace and unanswerable in a planted
    # fixture: a temporary directory has no ancestor that qualifies, so every session-fence test refused
    # with SESSION-ROOT-UNRESOLVABLE before the fence it was written to exercise ever ran.
    #
    # A root that itself carries `tasks/sessions` is not a guess - that directory IS the evidence, and it
    # is the same evidence the resolver would have used one level up. So the fallback is conditioned on
    # carrying it, not on the root merely existing, and the refusal still stands when there is nothing to
    # fall back to. A blanket "assume the current root" would have made the tool silently check fences
    # against a directory that owns nothing, which is the failure this whole check exists to prevent.
    sessions_root = None
    try:
        from keepverse_roots import workspace_root
        sessions_root = workspace_root(root)
    except Exception as exc:  # noqa: BLE001 - a resolver failure must refuse, never guess
        if (root / "tasks" / "sessions").is_dir():
            sessions_root = root
            print(f"[verify-change] the workspace resolver could not name a root ({exc}); this root "
                  "carries tasks/sessions, so it is the owner of these records", file=sys.stderr)
        else:
            raise Refusal("SESSION-ROOT-UNRESOLVABLE", f"{root}: {exc}") from exc
    record_path = sessions_root / "tasks" / "sessions" / f"{session}.json"
    if not record_path.is_file():
        raise Refusal("SESSION-RECORD-MISSING", str(record_path))
    try:
        record = json.loads(record_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("SESSION-RECORD-INVALID", f"{session}: {exc}") from exc
    if record.get("status") != "active":
        raise Refusal("SESSION-NOT-ACTIVE", f"{session}: status is {record.get('status')!r}")
    session_paths = [str(p) for p in (record.get("paths") or [])]
    if not session_paths:
        raise Refusal("SESSION-NO-PATHS", session)
    for path in normalized:
        if not any(wildcard_match(path, pattern) for pattern in session_paths):
            raise Refusal("PATH-OUTSIDE-SESSION", f"{session}: {path}")


def build_plan(
    inputs: Inputs,
    *,
    paths: Sequence[str],
    deleted_paths: Sequence[str],
    session: str | None,
    allow_unscoped: bool,
    diff_base_ref: str | None,
    diff_head_ref: str | None,
    timeout: float,
) -> Plan:
    """Resolve every path to its owner(s) and turn the selection into the check list.

    Refuses — loudly, and before printing anything that could be read as a plan — on a path with no
    owner, a path two owners tie on, a path outside the session fence, or a reviewed diff that escaped
    it. A broad suite is never the fallback: that refusal IS the finding.
    """
    root = inputs.root
    registry = inputs.registry
    owner_boundaries = [b for b in (registry.get("boundaries") or []) if b.get("kind") == "owner"]
    seam_boundaries = sorted((b for b in (registry.get("boundaries") or []) if b.get("kind") == "seam"),
                             key=lambda b: _sort_key(b.get("id", "")))

    normalized = _unique_sorted(
        [_normalize_path(p, must_exist=True, root=root) for p in paths]
        + [_normalize_path(p, must_exist=False, root=root) for p in deleted_paths]
    )
    if not normalized:
        raise Refusal("NO-PATHS", "nothing to verify")
    if not session and not allow_unscoped:
        raise Refusal("SESSION-REQUIRED",
                      "pass --session <id>; maintainers may pass --allow-unscoped for read-only "
                      "planning outside a session")
    if session:
        _check_session_fence(root, session, normalized)
    if diff_base_ref or diff_head_ref:
        if not (diff_base_ref and diff_head_ref):
            raise Refusal("DIFF-FENCE-INCOMPLETE", f"base={diff_base_ref!r} head={diff_head_ref!r}")
        if not session:
            raise Refusal("DIFF-FENCE-UNSCOPED", "a reviewed diff needs --session so it has a fence")
        fence = _run(
            _resolve_tool_argv(
                root, "session-boundary-check",
                ["-RepoRoot", str(root), "-Ci", "-Session", session,
                 "-DiffBaseRef", diff_base_ref, "-DiffHeadRef", diff_head_ref,
                 "-RequireDiffFence"],
                # The same call in each dialect. `-RepoRoot`/`-RequireDiffFence` handed to argparse
                # are rejected as unknown, so the Python spelling is a separate argument list rather
                # than a transform of the PowerShell one.
                ["--repo-root", str(root), "--ci", "--session", session,
                 "--diff-base-ref", diff_base_ref, "--diff-head-ref", diff_head_ref,
                 "--require-diff-fence"]),
            timeout=timeout,
            cwd=root,
            stage="diff-fence",
        )
        if fence.returncode != 0:
            for line in (fence.stdout + fence.stderr).splitlines():
                if line.strip():
                    print(line, file=sys.stderr)
            raise Refusal("DIFF-ESCAPED-FENCE", f"exit {fence.returncode}")

    # Guard ids are ALSO reachable by script path: changing a guard script verifies itself.
    script_to_guard = {script.replace("\\", "/"): guard_id
                       for guard_id, script in inputs.guard_catalog.items()}

    selections: list[Selection] = []
    for path in normalized:
        resolution = resolve_owner(path, owner_boundaries)
        if resolution is None:
            guard_id = script_to_guard.get(path)
            if guard_id:
                selections.append(Selection(path=path, boundary=f"enforcement-{guard_id}", project=None,
                                            verification_id=None, level="module", guards=[guard_id],
                                            test_files=[], self_select=False, exemption_reason=None))
                continue
            exemption = next(
                (entry for entry in inputs.verification_exemptions
                 if any(wildcard_match(path, str(pattern)) for pattern in (entry.get("paths") or []))),
                None,
            )
            if exemption is None:
                raise Refusal("BOUNDARY-MISSING",
                              f"{path}. Add an owner mapping or an explicit registry exemption; do not "
                              "run a broad suite as a fallback.")
            selections.append(Selection(path=path, boundary=f"exemption-{exemption.get('id')}", project=None,
                                        verification_id=None, level="exempt", guards=[], test_files=[],
                                        self_select=False, exemption_reason=str(exemption.get("reason"))))
            continue
        if len(resolution.owners) != 1:
            raise Refusal("BOUNDARY-AMBIGUOUS",
                          f"{path} matches "
                          f"{', '.join(str(o.get('id')) for o in resolution.owners)} at specificity "
                          f"{resolution.specificity}")
        owner = resolution.owners[0]
        selections.append(Selection(
            path=path, boundary=str(owner.get("id")), project=owner.get("project"),
            verification_id=owner.get("verificationId"), level=str(owner.get("level", derived_level(owner))),
            guards=[str(g) for g in (owner.get("guards") or [])],
            # A missing `testFiles` field is absent, not a one-element list holding null: filtering
            # truthy values is what collapses "absent" to zero selectors.
            test_files=[str(f) for f in (owner.get("testFiles") or []) if f],
            self_select=bool(owner.get("selfSelect")),
            exemption_reason=None,
        ))
        for seam in seam_boundaries:
            if any(pattern_match(path, str(pattern)) for pattern in (seam.get("paths") or [])):
                selections.append(Selection(
                    path=path, boundary=str(seam.get("id")), project=seam.get("project"),
                    verification_id=seam.get("verificationId"), level=str(seam.get("level", derived_level(seam))),
                    guards=[str(g) for g in (seam.get("guards") or [])],
                    test_files=[str(f) for f in (seam.get("testFiles") or []) if f],
                    self_select=bool(seam.get("selfSelect")), exemption_reason=None,
                ))

    selections.sort(key=lambda s: _sort_key(s.path, s.boundary))
    checks = _build_checks(inputs, selections, normalized, deleted_paths)
    return Plan(paths=normalized, selections=selections, checks=checks)


def _build_checks(
    inputs: Inputs,
    selections: Sequence[Selection],
    normalized: Sequence[str],
    deleted_paths: Sequence[str],
) -> list[Check]:
    """Turn selections into the deduplicated check list.

    python-test-lane D2/D3: a pytest project's check is built ONCE per PROJECT (not per boundary or
    per path) — the same project can be reached through a testFiles boundary and a selfSelect boundary
    in the same call, and a plain per-entry check would either duplicate the pytest run or (worse) let
    the final dedupe below silently drop one boundary's file list, since every pytest check shares
    ``verificationId = null``. Grouping first keeps ONE check per project with the UNION of every
    selector that fired: NeedsModule wins over any explicit file list, because a conftest/fixture
    change (D2) can affect every test in the project.
    """
    root = inputs.root
    projects_node = inputs.registry.get("projects") or {}
    checks: list[Check] = []
    pytest_groups: dict[str, dict[str, Any]] = {}

    for entry in selections:
        for guard in entry.guards:
            checks.append(Check(kind="guard", id=guard, level=entry.level, path=entry.path))

        # registry-contract C5: `project` is optional iff `guards` is non-empty (a guard-only
        # boundary's only honest proof is "it still compiles", not a fake dotnet-test mapping). No
        # `project` means no test check for this path at all; the guards above are its whole evidence.
        if not entry.project:
            continue

        runner_kind = project_runner(projects_node, entry.project)
        if runner_kind == "script":
            # python-test-lane D5: a script project is an argument-free wrapper; its exit code IS the
            # verdict. It is never a guard and never takes a file selector.
            checks.append(Check(kind="script", id=entry.project, level=entry.level, path=entry.path))
            continue
        if runner_kind == "pytest":
            group = pytest_groups.setdefault(entry.project, {
                "level": entry.level, "path": entry.path, "files": set(), "needs_module": False,
            })
            if entry.test_files:
                # D2: `testFiles` is a fixed selector on the BOUNDARY itself (e.g. tuning-publish-tool's
                # test_publish_add_key.py runs no matter which of its two owned files changed) — expand
                # each pattern against the real test_*.py files under the project's own test directory.
                dirs = pytest_project_dirs(projects_node, entry.project)
                if dirs is None:
                    raise Refusal("UNKNOWN-CHECK", f"pytest project has no root/tests: {entry.project}")
                test_dir = root / Path(dirs.test_dir)
                real_files: list[str] = []
                if test_dir.is_dir():
                    real_files = sorted(
                        candidate.relative_to(root).as_posix()
                        for candidate in test_dir.rglob("test_*.py") if candidate.is_file()
                    )
                for pattern in entry.test_files:
                    for real in real_files:
                        if pattern_match(real, pattern):
                            group["files"].add(real)
            elif entry.self_select:
                # D2: a changed `test_*.py` under a selfSelect boundary selects itself; any other
                # changed file there (conftest.py, a fixture) falls through to the module run instead.
                if re.match(r"^test_.*\.py$", os.path.basename(entry.path)):
                    group["files"].add(entry.path)
                else:
                    group["needs_module"] = True
            else:
                group["needs_module"] = True
            continue

        # data-tests-sharding (TVB1.5): a module-level check (no VerificationId) on a project the shard
        # manifest owns plans through the sharded runner, not one dotnet test process. Focused runs are
        # always plain dotnet test — they are small, and shard bookkeeping would be pure overhead.
        runner = "sharded" if (not entry.verification_id and entry.project in inputs.sharded_project_ids) else "dotnet"
        # registry-contract C7: a project id may name a GROUP (an array of .csproj paths). Module
        # selection targets every member — this is what keeps a group's fallback verifying every member
        # even mid-split. Focused selection targets only the member(s) whose directory actually holds
        # the matching trait, so the runner never depends on how `dotnet test` reports a filter that
        # cannot match anything there. Resolved once, here, so the printed/JSON plan and the execution
        # loop below can never disagree about what will run.
        members = project_members(projects_node, entry.project)
        if entry.verification_id and len(members) > 1:
            targets = [member for member in members
                       if project_has_trait(root, member, str(entry.verification_id))]
        else:
            targets = list(members)
        checks.append(Check(kind="test", id=entry.project, verification_id=entry.verification_id,
                            level=entry.level, path=entry.path, runner=runner, targets=targets))

    for project_id, group in pytest_groups.items():
        targets = [] if group["needs_module"] else sorted(group["files"], key=lambda f: (f.lower(), f))
        checks.append(Check(kind="pytest", id=project_id, verification_id=None, level=group["level"],
                            path=group["path"], runner="pytest", targets=targets))

    # Every Markdown path also gets the doc-citation audit, scoped to that one file: a citation nobody
    # can open is not evidence (DESIGN-GATE §5). Scoped per file because the whole docs/ tree still
    # carries older dead citations that are not this change's to fix. A DELETED path is not audited —
    # there is nothing left to read.
    deleted = {str(p) for p in deleted_paths}
    for path in normalized:
        if path.lower().endswith(".md") and path not in deleted:
            checks.append(Check(kind="doc-citations", id=path, verification_id=None, level="focused", path=path))

    # `Sort-Object kind, id, verificationId -Unique`: one check per (kind, id, selector), keeping the
    # first in the order above. This sort is STABLE here, where PowerShell's was not — the surviving
    # duplicate is therefore determinate rather than whichever way the array happened to fall.
    checks.sort(key=lambda check: check.dedupe_key)
    unique: list[Check] = []
    seen: set[tuple[str, str, str]] = set()
    for check in checks:
        if check.dedupe_key in seen:
            continue
        seen.add(check.dedupe_key)
        unique.append(check)
    return unique


# --------------------------------------------------------------------------------------------
# execution
# --------------------------------------------------------------------------------------------

@dataclass
class Runner:
    """Executes the selected checks. Tracks built projects so no test ever spawns a build."""

    inputs: Inputs
    timeout: float
    session: str | None
    _built: set[str] = field(default_factory=set)

    def _echo(self, message: str) -> None:
        print(message, file=sys.stderr)

    # -- dotnet ------------------------------------------------------------------------------

    def _build(self, project_path: str) -> None:
        """Build a project ONCE, before any test runs against it.

        The 2026-09-26 race: `dotnet test` spawns `bin/Release/net8.0/testhost.exe`, which can outlive
        the `dotnet test` call, and the NEXT build of the same project then fails with MSB3027
        "Could not copy ... FusionRpg.Guard.Tests.dll ... The file is locked by: testhost" — observed
        twice, one of them killing the whole verification run with exit -1. So: build here, once, and
        every test below runs with `--no-build`. Memoized per absolute project path, which also means a
        project is never rebuilt after its own tests have run.
        """
        absolute = str((self.inputs.root / Path(project_path)).resolve())
        if absolute in self._built:
            return
        dotnet = _which("dotnet")
        result = _run([dotnet, "build", absolute, "-c", "Release"],
                      timeout=self.timeout, cwd=self.inputs.root, stage="build")
        if result.returncode != 0:
            for line in (result.stdout + result.stderr).splitlines():
                if line.strip():
                    self._echo(line)
            raise Refusal("BUILD-FAILED",
                          f"dotnet build {project_path} exited {result.returncode}. {_tail(result.stdout, result.stderr)}",
                          stage="build")
        self._built.add(absolute)

    def _dotnet_test_with_evidence(self, project_path: str, test_filter: str, description: str,
                                   stage: str = "test") -> None:
        """Build once, then run a filtered test with `--no-build` and read the TRX it leaves.

        A filtered dotnet invocation is only evidence when the test host actually executed a test:
        `dotnet test` can return zero when a stale or mistyped filter matches nothing, so the process
        status alone is not a verdict. The TRX is read for its result nodes and the check fails CLOSED
        when the evidence file is missing, ambiguous, empty, or contains only non-executed outcomes.
        """
        self._build(project_path)
        results = Path(tempfile.mkdtemp(prefix="verify-change-dotnet-"))
        try:
            dotnet = _which("dotnet")
            result = _run([
                dotnet, "test", str((self.inputs.root / Path(project_path)).resolve()),
                "-c", "Release", "--no-build", "--verbosity", "minimal",
                "--filter", test_filter,
                "--logger", "trx;LogFileName=verify-change.trx",
                "--results-directory", str(results),
            ], timeout=self.timeout, cwd=self.inputs.root, stage=stage)
            if result.returncode != 0:
                for line in (result.stdout + result.stderr).splitlines():
                    if line.strip():
                        self._echo(line)
                raise Refusal("TEST-FAILED",
                              f"{description} failed with exit {result.returncode}. "
                              f"{_tail(result.stdout, result.stderr)}",
                              stage=stage)
            trx_files = sorted(results.glob("*.trx"))
            if len(trx_files) != 1:
                raise Refusal("TEST-EVIDENCE-AMBIGUOUS",
                              f"{description} produced {len(trx_files)} TRX evidence file(s); "
                              "expected exactly one", stage=stage)
            executed = _trx_executed_tests(trx_files[0])
            if executed == 0:
                raise Refusal("ZERO-TESTS",
                              f"{description} executed zero tests for filter '{test_filter}'; "
                              "a zero-match filter is RED", stage=stage)
            self._echo(f"  {description}: {executed} test(s) executed")
        finally:
            _drain_temp_dir(results)

    # -- the other kinds ---------------------------------------------------------------------

    def run_guard(self, guard_id: str) -> int:
        script_rel = self.inputs.guard_catalog.get(guard_id)
        if not script_rel:
            raise Refusal("UNKNOWN-CHECK", f"unknown guard: {guard_id}", stage="guard")
        script = self.inputs.root / Path(script_rel)
        # Dispatch on the file's extension, because the registry may now name either. A guard whose
        # logic was ported to Python is a .py row, and `pwsh -File <a .py>` exits 64 with "the file
        # does not have a '.ps1' extension" — measured, not assumed — so a hardcoded powershell_argv
        # turns every ported guard into a hard red. This mirrors run_script, which already branched
        # on the suffix; the two paths must not disagree about how a check is launched.
        suffix = script.suffix.lower()
        if suffix == ".ps1":
            argv = _powershell_argv(script, [])
        elif suffix == ".py":
            argv = [_which("python"), str(script)]
        else:
            # Fail CLOSED with the reason, not a bare exec attempt: an extension this tool cannot
            # launch must not be silently tried as a bare command, because that reads as a pass when
            # the file is not executable and a mystery failure when it is.
            raise Refusal("UNSUPPORTED-CHECK-KIND",
                          f"guard '{guard_id}' names '{script_rel}', which is neither .ps1 nor .py; "
                          f"the runner cannot dispatch it",
                          stage="guard")
        # A guard that can scope its verdict to one session gets it: session-boundary-check.py then
        # fails only on drift that names this session, not on other sessions' leftovers.
        if self.session and _script_accepts_session(script):
            argv += ["-Session", self.session]
        result = _run(argv, timeout=self.timeout, cwd=self.inputs.root, stage="guard")
        if result.stdout.strip():
            for line in result.stdout.splitlines():
                self._echo(line)
        return result.returncode

    def run_doc_citations(self, path: str) -> int:
        audit = self.inputs.root / "scripts" / "audit-doc-citations.py"
        python = _which("python")
        result = _run([python, str(audit), "--strict", "--scope", path],
                      timeout=self.timeout, cwd=self.inputs.root, stage="doc-citations")
        if result.stdout.strip():
            for line in result.stdout.splitlines():
                self._echo(line)
        return result.returncode

    def run_script(self, project_id: str) -> int:
        projects_node = self.inputs.registry.get("projects") or {}
        script_rel = (projects_node.get(project_id) or {}).get("script") if isinstance(
            projects_node.get(project_id), dict) else None
        if not script_rel:
            raise Refusal("UNKNOWN-CHECK", f"unknown script project: {project_id}", stage="script")
        target = self.inputs.root / Path(str(script_rel))
        if target.suffix.lower() == ".ps1":
            argv = _powershell_argv(target, [])
        elif target.suffix.lower() == ".py":
            argv = [_which("python"), str(target)]
        else:
            argv = [str(target)]
        result = _run(argv, timeout=self.timeout, cwd=self.inputs.root, stage="script")
        if result.stdout.strip():
            for line in result.stdout.splitlines():
                self._echo(line)
        return result.returncode

    def run_pytest(self, check: Check) -> int:
        dirs = pytest_project_dirs(self.inputs.registry.get("projects") or {}, check.id)
        if dirs is None:
            raise Refusal("UNKNOWN-CHECK", f"unknown pytest project: {check.id}", stage="pytest")
        absolute_root = self.inputs.root / Path(dirs.root)
        results = Path(tempfile.mkdtemp(prefix="vb-pytest-"))
        try:
            python = _which("python")
            probe = _run([python, "-m", "pytest", "--version"], timeout=min(self.timeout, 120.0),
                         cwd=absolute_root, stage="pytest")
            if probe.returncode != 0:
                raise Refusal("PYTEST-ENV-MISSING",
                              f"python test environment missing — install per AGENTS.md 'Seedsmith' "
                              f"(exit {probe.returncode})", stage="pytest")
            root_prefix = dirs.root.rstrip("/") + "/"
            # The registry's file list is RELATIVE TO THE PROJECT ROOT; pytest is invoked with that
            # root as cwd, so the prefix is stripped here. Never splat the string: a single selector
            # must stay ONE argument (`pytest t e s t s` is the bug that shape produced once).
            targets = check.targets or []
            if targets:
                selectors = sorted((t[len(root_prefix):] if t.startswith(root_prefix) else t
                                    for t in targets), key=lambda s: (s.lower(), s))
            else:
                selectors = [dirs.tests]
            junit_path = results / "r.xml"
            result = _run([python, "-m", "pytest", *selectors, "-q", "-p", "no:cacheprovider",
                           "--junitxml", str(junit_path)],
                          timeout=self.timeout, cwd=absolute_root, stage="pytest")
            if result.stdout.strip():
                for line in result.stdout.splitlines():
                    self._echo(line)
            joined = ", ".join(selectors)
            if result.returncode == 5:
                raise Refusal("PYTEST-COLLECTED-NOTHING",
                              f"pytest collected no tests for {joined} - registry selector defect",
                              stage="pytest")
            if result.returncode not in (0, 1):
                raise Refusal("PYTEST-UNEXPECTED-EXIT",
                              f"pytest exited {result.returncode} unexpectedly for {joined}",
                              stage="pytest")
            # python-test-lane D6 rule 3: read the outcome off the junit report REGARDLESS of pytest's
            # own exit code — a `knownRed` entry that just turned GREEN (exit 0, every test passed)
            # must still be caught as stale, not silently treated as a win.
            test_cases = junit_test_cases(junit_path)
            if not test_cases:
                raise Refusal("ZERO-PYTESTS",
                              f"pytest executed zero tests for {joined} - registry selector defect",
                              stage="pytest")
            outcome = resolve_known_red_outcome(check.id, test_cases, self.inputs.registry.get("knownRed") or [])
            for message in outcome.messages:
                self._echo(f"  {message}")
            return 0 if outcome.ok else 1
        finally:
            _drain_temp_dir(results)

    def run_test(self, check: Check) -> None:
        targets = check.targets or []
        if not targets:
            raise Refusal("UNKNOWN-CHECK", f"unknown project: {check.id}", stage="test")
        if check.verification_id:
            for target in targets:
                self._dotnet_test_with_evidence(
                    target, f"VerificationId={check.verification_id}",
                    f"{check.id} focused check ({target})", stage="focused-test")
        elif check.id in self.inputs.sharded_project_ids:
            # The sharded runner already owns this shape: it builds once and runs every shard with
            # --no-build, which is the same discipline this port applies to the plain dotnet path.
            #
            # Repointed from `scripts/test-sharded.ps1` with PowerShell flag spellings
            # (`-Project`/`-ExtraFilter`/`-Root`). That file was ported to `gk-core/scripts/test_sharded.py` and
            # no longer exists, so EVERY sharded-project check -- FusionRpg.Data.Tests -- was launching
            # a missing script through pwsh. Found by a sweep for executable references to a `.ps1`
            # whose target is not tracked, which is what the generic suffix branches at lines 225/870/909
            # do NOT do: they prefer the `.py` and fall back, so they are dispatch and not breakage.
            sharded = self.inputs.root / "scripts" / "test_sharded.py"
            if not sharded.is_file():
                # Named, not left to surface as a missing-executable error from deep inside a
                # subprocess call: fail closed where a reader can see which file went away.
                raise Refusal("TOOL-MISSING",
                              f"the sharded runner is missing: {sharded} (the sharded check "
                              f"{check.id!r} cannot run without it)", stage="sharded-test")
            result = _run(
                [_which("python"), str(sharded),
                 "--project", targets[0],
                 "--extra-filter", self.inputs.default_profile_filter,
                 "--root", str(self.inputs.root)],
                timeout=self.timeout, cwd=self.inputs.root, stage="sharded-test")
            if result.stdout.strip():
                for line in result.stdout.splitlines():
                    self._echo(line)
            if result.returncode != 0:
                raise Refusal("TEST-FAILED", f"sharded test check failed: {check.id} "
                                             f"(exit {result.returncode}). "
                                             f"{_tail(result.stdout, result.stderr)}", stage="sharded-test")
        else:
            for member in targets:
                self._dotnet_test_with_evidence(
                    member, self.inputs.default_profile_filter, f"{check.id} module check ({member})",
                    stage="module-test")


def _trx_executed_tests(trx_path: Path) -> int:
    """How many `UnitTestResult` nodes in a TRX carry a real, non-`NotExecuted` outcome."""
    try:
        document = ET.parse(trx_path).getroot()
    except ET.ParseError as exc:
        raise Refusal("TEST-EVIDENCE-AMBIGUOUS", f"{trx_path.name}: {exc}", stage="test") from exc
    executed = 0
    for element in document.iter():
        if element.tag.rsplit("}", 1)[-1] != "UnitTestResult":
            continue
        outcome = element.get("outcome")
        if outcome and outcome.strip() and outcome != "NotExecuted":
            executed += 1
    return executed


def execute(plan: Plan, inputs: Inputs, *, session: str | None, timeout: float) -> int:
    """Run every selected check in plan order, returning the process exit code."""
    runner = Runner(inputs=inputs, timeout=timeout, session=session)
    for check in plan.checks:
        if check.kind == "guard":
            code = runner.run_guard(check.id)
            if code != 0:
                return code
        elif check.kind == "doc-citations":
            code = runner.run_doc_citations(check.id)
            if code != 0:
                return code
        elif check.kind == "script":
            code = runner.run_script(check.id)
            if code != 0:
                return code
        elif check.kind == "pytest":
            code = runner.run_pytest(check)
            if code != 0:
                return code
        else:
            runner.run_test(check)
    return 0


# --------------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------------

def _flatten(values: list[list[str]] | None) -> list[str]:
    return [item for group in (values or []) for item in group]


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Plan and run the path-owned verification boundary for a change.")
    ap.add_argument("--paths", action="append", nargs="+", metavar="PATH",
                    help="repository-relative path that changed (repeatable; several per flag)")
    ap.add_argument("--deleted-paths", action="append", nargs="+", metavar="PATH",
                    help="repository-relative path that was REMOVED; its old boundary is still selected")
    ap.add_argument("--plan-only", action="store_true", help="print the plan and exit 0 without running it")
    ap.add_argument("--allow-unscoped", action="store_true",
                    help="maintainer escape: plan without a session fence (read-only planning)")
    ap.add_argument("--format", choices=("text", "json"), default="text",
                    help="plan output format (default: text)")
    ap.add_argument("--json", dest="as_json", action="store_true",
                    help="alias for --format json")
    ap.add_argument("--session", help="the active session id that fences these paths")
    ap.add_argument("--diff-base-ref", help="reviewed-diff base ref (needs --diff-head-ref and --session)")
    ap.add_argument("--diff-head-ref", help="reviewed-diff head ref (needs --diff-base-ref and --session)")
    ap.add_argument("--root", type=Path, default=None,
                    help="repository root (default: the parent of this script's directory)")
    ap.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT,
                    help=f"hard timeout in seconds for every external call (default {DEFAULT_TIMEOUT:g})")
    args = ap.parse_args(argv)

    if args.timeout <= 0:
        print("VERIFY-CHANGE REFUSED: TIMEOUT: --timeout must be positive", file=sys.stderr)
        return 1

    try:
        try:
            root = (args.root or REPO).resolve(strict=True)
        except OSError as exc:
            raise Refusal("ROOT-UNRESOLVABLE", f"{args.root or REPO}: {exc}") from exc
        if not root.is_dir():
            raise Refusal("ROOT-UNRESOLVABLE", str(root))
        inputs = load_inputs(root, args.timeout)
        plan = build_plan(
            inputs,
            paths=_flatten(args.paths),
            deleted_paths=_flatten(args.deleted_paths),
            session=args.session,
            allow_unscoped=args.allow_unscoped,
            diff_base_ref=args.diff_base_ref,
            diff_head_ref=args.diff_head_ref,
            timeout=args.timeout,
        )
    except Refusal as refusal:
        print(f"VERIFY-CHANGE REFUSED [{refusal.stage}]: {refusal.name}: {refusal.detail}", file=sys.stderr)
        print(f"  meaning: {REFUSALS[refusal.name]}", file=sys.stderr)
        return 1
    except subprocess.TimeoutExpired as exc:
        print(f"VERIFY-CHANGE REFUSED [timeout]: TIMEOUT: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # fail closed on the unexpected, never a half-printed plan
        print(f"VERIFY-CHANGE REFUSED [plan]: UNPLANNED: {type(exc).__name__}: {exc}", file=sys.stderr)
        print("  meaning: no plan was produced; nothing above this line is a verification verdict.",
              file=sys.stderr)
        return 1

    if args.format == "json" or args.as_json:
        print(json.dumps(plan.as_json(), indent=2, ensure_ascii=False))
    else:
        print(plan.render_text())
    if args.plan_only:
        return 0

    try:
        return execute(plan, inputs, session=args.session, timeout=args.timeout)
    except Refusal as refusal:
        print(f"VERIFY-CHANGE REFUSED [{refusal.stage}]: {refusal.name}: {refusal.detail}", file=sys.stderr)
        print(f"  meaning: {REFUSALS[refusal.name]}", file=sys.stderr)
        return 1
    except Exception as exc:
        print(f"VERIFY-CHANGE REFUSED [execute]: UNCHECKED: {type(exc).__name__}: {exc}", file=sys.stderr)
        print("  meaning: the selected checks did not all report; this is NOT a pass.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
