"""The composition root: load the registry, read the tree, write the plan and the report.

`report` is the only module that knows where things live. It resolves the SCANNED TREE from
`git rev-parse --show-toplevel` (or an explicit `--root`) and **never** from the process working
directory: `git ls-files` run inside `gk-core/tools/ip-censor` would enumerate only that folder and any gate over
it would pass vacuously (plan D4).

The REGISTRY is resolved separately, through `ipcensor.roots`, and it has to be: the split moved
`data/seed/**` into a gk-data pack, so the tree being scanned and the registry that describes it
stopped being the same repository. `registry_dir` still honours a `--root` that carries the registry -
the fixture tests depend on exactly that - and falls through to the pack only when it does not.

The plan JSON is the hand-off to the execute program. It is versioned, byte-stable apart from
`generated_at`, and it carries `remediation` on every finding so no consumer has to re-derive it.
"""

from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Mapping, Sequence

from ipcensor.census import TokenStat, census
from ipcensor.registry import MARKS_FILE, Registry, load_registry
from ipcensor.roots import owned_dir
from ipcensor.scan import Finding, scan
from ipcensor.source import SourceFile, iter_files, matches_any
from ipcensor.suggest import SuggestFn, Suggestion, suggest

PLAN_SCHEMA_VERSION = 1

# REPOSITORY-RELATIVE, AND IT MUST STAY THAT WAY. This string is not only a path: it is the form the
# authored data uses. `scope-policy.v1.json` carries the rules `data/seed/**/_registry/**` and
# `data/seed/ip-censor/**`, and `marks.v1.json` lists `self_paths` of `data/seed/ip-censor/**`, all of
# which are matched against repo-relative finding paths. Turning this into a resolved absolute path
# would stop the registry from describing its own scope. WHERE it lives is a separate question, and
# `roots` answers it - the split moved this tree into a gk-data pack, so `root / DEFAULT_REGISTRY_DIR`
# named a path in a repository that does not have it.
DEFAULT_REGISTRY_DIR = "data/seed/ip-censor/_registry"

# Closed vocabulary: a new grouping is a reviewed decision, and `--by remediation` must lose no finding.
GROUPINGS: tuple[str, ...] = ("file", "bucket", "surface", "remediation", "mark")


class ReportError(RuntimeError):
    """A composition step failed: no git root, an unreadable registry, or an unknown grouping."""


@dataclass(frozen=True, slots=True)
class Plan:
    """The versioned hand-off: what was found, where it came from, and under whose authority."""

    schema_version: int
    generated_from_commit: str
    registry_version: str
    model: str | None
    findings: tuple[Finding, ...]
    suggestions: tuple[Suggestion, ...] = ()
    generated_at: str | None = None

    def as_dict(self, *, by: str = "file") -> dict[str, object]:
        groups: dict[str, list[dict[str, object]]] = {}
        for index, finding in enumerate(self.findings):
            key = _group_key(finding, by)
            suggestion = self.suggestions[index] if index < len(self.suggestions) else None
            groups.setdefault(key, []).append(_finding_document(finding, suggestion))
        document: dict[str, object] = {
            "schemaVersion": self.schema_version,
            "generatedFromCommit": self.generated_from_commit,
            "registryVersion": self.registry_version,
            "model": self.model,
            "groupBy": by,
            "groups": {key: groups[key] for key in sorted(groups)},
        }
        if self.generated_at is not None:
            document["generatedAt"] = self.generated_at
        return document


def resolve_root(explicit: Path | str | None = None) -> Path:
    """The repository root: an explicit `--root`, else the git working tree containing the cwd."""
    if explicit is not None:
        return Path(explicit)
    result = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, check=False
    )
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise ReportError(f"not inside a git working tree ({detail})")
    return Path(result.stdout.decode("utf-8").strip())


def registry_version(directory: Path | str) -> str:
    """The version every registry file name carries, e.g. `v1`.

    The files are named `<name>.<version>.json`, so the version is read from the names rather than
    restated: two files disagreeing is a load rejection.
    """
    base = Path(directory)
    versions = {
        path.name.split(".")[-2]
        for path in base.glob("*.json")
        if len(path.name.split(".")) >= 3
    }
    if not versions:
        raise ReportError(f"{base}: no versioned registry file found")
    if len(versions) > 1:
        raise ReportError(f"{base}: registry files disagree on version: {sorted(versions)}")
    return versions.pop()


def registry_dir(root: Path | str | None = None) -> Path:
    """The registry directory, resolved through the repository that carries it (`ipcensor.roots`).

    `root` still wins when it genuinely carries the registry - that is what lets the fixture tests
    pass `--root` into a throwaway repo - and a root that does not fall through to the content pack.
    """
    return owned_dir(DEFAULT_REGISTRY_DIR, root)


def load_registry_for(root: Path | str, *, directory: str = DEFAULT_REGISTRY_DIR) -> Registry:
    return load_registry(owned_dir(directory, root))


def commit_of(root: Path | str) -> str:
    result = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, check=False
    )
    if result.returncode != 0:
        raise ReportError(f"{root}: cannot read HEAD ({result.stderr.decode('utf-8', 'replace')})")
    return result.stdout.decode("utf-8").strip()


def output_self_paths(
    root: Path | str, outputs: Iterable[Path | str | None]
) -> tuple[str, ...]:
    """Class-4 self paths: whatever this run writes must not be a finding source next time.

    Without this a plan containing every finding becomes input to the next scan, which flags its own
    previous scan and grows each run (spec-registry.md §self_paths).
    """
    base = Path(root).resolve()
    paths: list[str] = []
    for output in outputs:
        if output is None:
            continue
        candidate = Path(output)
        if not candidate.is_absolute():
            # A relative `--plan` is root-relative, exactly as the audit requires: resolving it
            # against the process working directory would put the plan outside the tree being scanned.
            candidate = base / candidate
        try:
            relative = candidate.resolve().relative_to(base)
        except ValueError:
            continue
        paths.append(relative.as_posix())
    return tuple(paths)


def scan_tree(
    root: Path | str,
    registry: Registry,
    *,
    tree: str | None = None,
    extra_self_paths: Sequence[str] = (),
) -> tuple[Finding, ...]:
    """Scan the whole repository — never the working directory (plan D4)."""
    return scan(_tree_files(root, registry, tree, extra_self_paths), registry)


def census_tree(
    root: Path | str,
    registry: Registry,
    *,
    tree: str | None = None,
    extra_self_paths: Sequence[str] = (),
) -> tuple[TokenStat, ...]:
    return census(_tree_files(root, registry, tree, extra_self_paths), registry)


def _tree_files(
    root: Path | str,
    registry: Registry,
    tree: str | None,
    extra_self_paths: Sequence[str],
) -> Iterable[SourceFile]:
    self_paths = tuple(registry.self_paths) + tuple(extra_self_paths)
    for item in iter_files(root, self_paths=self_paths):
        if _in_tree(item.path, tree):
            yield item


def build_plan(
    findings: Iterable[Finding],
    *,
    root: Path | str,
    registry: Registry,
    model: str | None = None,
    suggestions: Iterable[Suggestion] = (),
    generated_at: str | None = None,
) -> Plan:
    return Plan(
        schema_version=PLAN_SCHEMA_VERSION,
        generated_from_commit=commit_of(root),
        registry_version=registry_version(registry_dir(root)),
        model=model,
        findings=tuple(findings),
        suggestions=tuple(suggestions),
        generated_at=generated_at,
    )


def enforced_findings(findings: Iterable[Finding], registry: Registry) -> tuple[Finding, ...]:
    """The findings that block a release (IC-3): an enforced surface **and** a mark in scope there.

    Both halves are authored data. A hit on an enforced surface whose mark declares no scope for it
    is report-only — `pvz` is "in scope on player-facing surfaces only" (IC-1b), so a `pvz` hit in a
    generator brief informs the reader without blocking the release.
    """
    return tuple(
        finding
        for finding in findings
        if registry.enforces(mark=finding.mark, matched=finding.matched, surface=finding.surface)
    )


def render_plan(plan: Plan, *, by: str = "file") -> str:
    """The plan's exact text: sorted keys, LF, trailing newline."""
    return json.dumps(plan.as_dict(by=by), indent=2, ensure_ascii=False, sort_keys=True) + "\n"


def render_report(plan: Plan, *, by: str = "file") -> str:
    """A human-readable companion to the plan. Every finding appears exactly once."""
    lines = [
        "# ip-censor report",
        "",
        f"- schema: {plan.schema_version}",
        f"- commit: {plan.generated_from_commit}",
        f"- registry: {plan.registry_version}",
        f"- model: {plan.model or '(none)'}",
        f"- findings: {len(plan.findings)}",
        f"- grouped by: {by}",
        "",
    ]
    document = plan.as_dict(by=by)
    groups = document["groups"]
    if not isinstance(groups, dict):
        raise ReportError("the plan document has no groups mapping")
    for key in sorted(groups):
        rows = groups[key]
        lines.append(f"## {key} ({len(rows)})")
        lines.append("")
        for row in rows:
            lines.append(
                f"- `{row['path']}:{row['line']}:{row['column']}` **{row['matched']}** "
                f"({row['mark']}) — bucket `{row['bucket']}`, remediation `{row['remediation']}`, "
                f"suggestion {_suggestion_text(row)}"
            )
        lines.append("")
    return "\n".join(lines).rstrip("\n") + "\n"


def suggest_for(
    findings: Iterable[Finding],
    registry: Registry,
    *,
    authored_only: bool,
    propose: SuggestFn | None = None,
) -> tuple[Suggestion, ...]:
    """Suggestions for a finding set, in the plan's own order."""
    ordered = tuple(findings)
    if authored_only:
        return suggest(ordered, registry, None)
    return suggest(ordered, registry, propose)


def _finding_document(finding: Finding, suggestion: Suggestion | None) -> dict[str, object]:
    document = finding.as_dict()
    document["suggestion"] = None if suggestion is None else suggestion.as_dict()
    return document


def _suggestion_text(row: Mapping[str, object]) -> str:
    suggestion = row.get("suggestion")
    if not isinstance(suggestion, Mapping):
        return "(none)"
    replacement = suggestion.get("replacement")
    source = suggestion.get("source")
    if replacement is None:
        return f"none ({source})"
    return f"`{replacement}` ({source})"


def _group_key(finding: Finding, by: str) -> str:
    if by not in GROUPINGS:
        raise ReportError(f"unknown grouping {by!r}; expected one of {list(GROUPINGS)}")
    if by == "file":
        return finding.path
    if by == "bucket":
        return finding.bucket
    if by == "surface":
        return finding.surface
    if by == "remediation":
        return finding.remediation
    return finding.mark


def _in_tree(path: str, tree: str | None) -> bool:
    if tree is None:
        return True
    return matches_any(path, (f"{tree.rstrip('/')}/**",)) or path == tree


def registry_check(root: Path | str) -> str:
    """Parse the shipped registry and describe it. Raises `RegistryError` when it cannot be loaded."""
    directory = registry_dir(root)
    registry = load_registry(directory)
    return (
        f"{MARKS_FILE}: {registry_version(directory)}, "
        f"{len(registry.groups)} group(s), {len(registry.replacements)} replacement pair(s), "
        f"{len(registry.self_paths)} self path(s)"
    )


if __name__ == "__main__":  # `python -m ipcensor.report <verb>`
    from ipcensor import cli

    raise SystemExit(cli.main())
