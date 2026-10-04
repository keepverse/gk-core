#!/usr/bin/env python3
r"""Guard: the verification-boundary registry is structurally valid and maps what it must map.

Replaces `guard-verification-boundaries.ps1`. All the pattern/level/project logic comes from
`gk-core/scripts/lib/verification_boundaries.py`, which was already the Python twin of `lib/VerificationBoundaries.ps1`,
so this port is the guard's own rules and not a second copy of the shared lib.

`--report` prints how deep the registry maps `src/**` and which `VerificationId` traits no boundary
selects. That is a READING, never an assertion: the numbers move whenever production code or the
registry grows, so nothing in it is pinned (`docs/architecture/validation-ssot.md` — assert the
contract, print the scale).

`--skip-coverage-walk` skips the whole-repo COMPLETENESS invariant (every `src/**`, `tests/**/*.cs`,
`tools/*.Tests/**/*.cs` file resolves to an owner; every `*.Tests.csproj` is registered). `verify-change.py`
passes it on its own internal pre-check: that check exists to trust the registry enough to plan one or
two specific paths and already resolves THOSE paths itself, so re-walking the whole repo on every local
call duplicates CI's unfiltered guard run at a cost that scales with repo size rather than with the
change being verified (measured: this walk, not process startup, is what made a 2-path `--plan-only`
call take 415s under 22 concurrent dotnet.exe hosts — lane-b review, TVB4.3).

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **The registry is a DATASET, and the guard is its only validator.** A validator that cannot be
  unit-tested case by case is a validator that only gets exercised by whatever the registry happens to
  contain today. Every rule below is now a test with a fixture, and the fixtures are generated from
  the vocabulary the guard declares rather than copied out of the registry.
* **`$failures | Select-Object -Unique`** deduplicated findings on the way out, so two rules firing on
  one row produced one line. Preserved, and pinned: a dedupe that silently dropped a *different*
  finding would hide it.
* **The original's `$null -eq $doc.projects` test ran BEFORE the `projects` loop**, so a registry with
  no `projects` key reported "requires projects and boundaries" and then also emitted a null-deref
  shaped error per property. The port checks presence first and refuses by name.

ALMOST EVERY COMPARISON FOLDS CASE
----------------------------------
`-notcontains` folds, so `Runner`/`PYTEST`/`Script` are accepted as `runner` values, and a `kind` of
`OWNER` is a valid `owner`. The port folds on exactly the same comparisons. Two places where folding
is easy to get wrong, and both are pinned by tests rather than by a fixture that happens to agree:
  * `$cells[1] -eq 'red'` on the stub register is `-eq`, so it folds — a row whose status is `RED`
    resolves a `debt` id. A `==` here would silently invalidate every `knownRed` entry.
  * the `[Trait(...)]` scan uses `-match`, which folds, so its `[a-z]` ranges also match uppercase.
    The port compiles the pattern with `re.IGNORECASE` to match, and a test proves an uppercase trait
    value is still seen.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

GUARD_ID = "verification-boundaries"

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
import verification_boundaries as vb  # noqa: E402  (the lib lives beside this tool, not on sys.path)

# THE RESOLVER IS OPTIONAL, AND SAYING SO IS THE POINT.
#
# This import was unconditional when the cross-repository resolution landed, and it broke every test
# that plants a COPY of this guard into a temporary `scripts/` directory: a fixture has no `lib/` beside
# it, so `from keepverse_roots import ...` raised ModuleNotFoundError at import time. The guard then
# died with a traceback instead of a verdict, and 29 tests in `test_verify_change.py` reported a
# missing refusal rather than the guard's own finding. Those failures were invisible for a day because
# a collection error elsewhere was aborting the whole pytest run before anything executed.
#
# A fixture legitimately has no sibling repositories, so resolving against its own root only is the
# CORRECT answer there - and an accessor that returns None is exactly how `repo_bases` already skips a
# repository it cannot name. So the fallback is not a silent reversion of the fix: it prints a warning
# naming itself, so a real repository that has lost `lib/keepverse_roots.py` is visible instead of
# quietly resolving everything against gk-core again - which is the defect this whole change existed to
# remove.
try:
    from keepverse_roots import (  # noqa: E402
        authored_content_root, content_root, core_root, forge_root, fusion_root, web_root,
        workspace_root,
    )
    from keepverse_roots import owning_base as _shared_owning_base  # noqa: E402
    from keepverse_roots import repo_bases as _shared_repo_bases  # noqa: E402
    RESOLVER_AVAILABLE = True
except ImportError as _resolver_error:  # a planted fixture, or a repository missing its lib/
    RESOLVER_AVAILABLE = False
    print(f"[{GUARD_ID}] WARNING: the workspace resolver is unavailable ({_resolver_error}); this run "
          "resolves declared paths against its OWN ROOT ONLY. In a planted fixture that is correct - a "
          "fixture has no sibling repositories. In a real repository it means "
          "scripts/lib/keepverse_roots.py is missing, and every cross-repository path will be reported "
          "unresolved.", file=sys.stderr)

    def _absent(_start=None):
        return None
    authored_content_root = content_root = core_root = _absent
    forge_root = fusion_root = web_root = workspace_root = _absent
    _shared_owning_base = _shared_repo_bases = None

EXIT_OK = 0
EXIT_FAILED = 1

REGISTRY_RELPATH = "scripts/verification-boundaries.v1.json"
ENFORCEMENT_RELPATH = "scripts/enforcement-registry.v1.json"
STUB_REGISTER_RELPATH = "docs/architecture/stub-register.md"

REGISTRY_FIELDS = ("schemaVersion", "projects", "boundaries", "knownRed")
BOUNDARY_FIELDS = ("id", "kind", "paths", "project", "verificationId", "guards", "level", "testFiles",
                   "selfSelect")
PYTEST_PROJECT_FIELDS = ("runner", "root", "tests", "repo")
# A pytest project whose `root` is not inside THIS repository says which repository owns it. The
# split moved trees out from under roots that are named the way a tree is named NEXT TO its owner
# (`tools/seedsmith` is gk-forge's, `.claude/cmdc-agents/scripts` is gk-workflow's), and nothing in
# the root itself records that. Without the field a CI-wiring guard has to GUESS the owner from the
# filesystem, and a guess is environment-dependent: gk-workflow is the workspace ROOT in a full
# workspace and a `gk-workflow/` subdirectory on a runner, so the same guard would pass in CI and
# fail locally. Closed vocabulary for the same reason VALID_RUNNERS is - a typo in an owner is a
# wire-up pointing nowhere, and it must fail here rather than at run time.
VALID_OWNING_REPOS = ("gk-forge", "gk-web", "gk-workflow", "gk-fusion", "gk-content", "gk-data")
SCRIPT_PROJECT_FIELDS = ("runner", "script")
# registry-contract C7 / python-test-lane D1. A closed vocabulary the code owns: a fourth runner is a
# reviewed change to this list, never a data edit, because a runner with no code path would just defer
# the failure to execution time with a worse error.
VALID_RUNNERS = ("dotnet", "pytest", "script")
BOUNDARY_KINDS = ("owner", "seam")
EVIDENCE_LEVELS = ("focused", "module", "seam", "full")
KNOWN_RED_FIELDS = ("project", "test", "debt")
# A boundary naming neither a project nor a guard derives to `full`, which is legal ONLY here. Off
# these roots, naming neither still means no evidence at all.
FULL_ELIGIBLE_PREFIXES = ("data/", "tests/fixtures/")
EXEMPT_TEST_PROJECTS = {
    "tests/FusionRpg.Injector.Tests/FusionRpg.Injector.Tests.csproj":
        "needs BepInEx interop to compile; see its csproj comment and map §6",
}
BUILD_ARTIFACT_DIRS = ("bin", "obj", "TestResults")
PROJECT_ID = re.compile(r"^[a-z][a-z0-9-]*$")
BOUNDARY_ID = re.compile(r"^[a-z][a-z0-9-]*$")
VERIFICATION_ID = re.compile(r"^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$")
TRAIT = re.compile(r'\[Trait\s*\(\s*"VerificationId"\s*,\s*"([a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)+)"\s*\)\]',
                    re.IGNORECASE)
STUB_ID_CELL = re.compile(r"^(~~)?`(SR-\d+)`(~~)?$", re.IGNORECASE)


class Refusal(Exception):
    """A named precondition failure: the registry could not be read at all."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Report:
    """The `--report` readings. A reading, never a contract."""

    production_mapped: int = 0
    verification_id_filtered: int = 0
    whole_project_fallback: int = 0
    broad_by_boundary: list[tuple[str, int]] = field(default_factory=list)
    orphan_trait_ids: list[str] = field(default_factory=list)
    orphan_traits_by_project: dict[str, list[str]] = field(default_factory=dict)
    full_level: list[tuple[str, list[str]]] = field(default_factory=list)

    def as_dict(self) -> dict:
        return {
            "productionMapped": self.production_mapped,
            "verificationIdFiltered": self.verification_id_filtered,
            "wholeProjectFallback": self.whole_project_fallback,
            "wholeProjectByBoundary": [{"boundary": b, "files": n} for b, n in self.broad_by_boundary],
            "orphanVerificationIdTraits": self.orphan_trait_ids,
            "orphanTraitsByProject": self.orphan_traits_by_project,
            "inputsWithNoLocalProof": [{"id": i, "paths": p} for i, p in self.full_level],
        }


def is_relative_registry_path(candidate: str) -> bool:
    """`Is-RelativeRegistryPath`: relative, no `..` segment, no backslash, valid pattern grammar.

    Every regex in the original is case-INsensitive, so `..` in any casing and a backslash in any
    casing are both rejected.
    """
    if not candidate or not candidate.strip():
        return False
    # `[IO.Path]::IsPathRooted` is true for a drive-rooted path, a UNC path, OR a leading separator.
    # `Path.is_absolute()` is NOT that test on Windows: `Path("/etc/**").is_absolute()` is False,
    # because there is no drive. So the port accepted a ROOTED pattern that the original refused, and
    # no differential on this repository could have shown it - no registry row uses a rooted path, so
    # the two implementations agreed on every input the tree actually contains. A unit test varying the
    # input is the only thing that sees it. `\` is already rejected by the backslash rule below, so
    # the leading-separator case only has to cover `/` and the drive form.
    if candidate[0] in "/\\" or re.match(r"^[A-Za-z]:", candidate):
        return False
    if re.search(r"(^|[\\/])\.\.([\\/]|$)", candidate, re.IGNORECASE):
        return False
    if "\\" in candidate:
        return False
    return vb.valid_pattern_grammar(candidate)


def walk_files(start: Path, skip_dirs: tuple[str, ...] = BUILD_ARTIFACT_DIRS) -> list[Path]:
    """`Get-FilesPruned`: one pass, never descending into a skipped directory name.

    A plain `rglob` here was the measured cost the original's comment describes: it descends into every
    `bin`/`obj`/`TestResults` before filtering, which is what made the `src` walk take 60-90s on this
    tree. `os.walk` is given the same pruning via `dirs[:]` mutation.
    """
    if not start.exists():
        return []
    out: list[Path] = []
    stack = [start]
    skip = {d.casefold() for d in skip_dirs}
    while stack:
        directory = stack.pop()
        try:
            entries = list(directory.iterdir())
        except OSError:
            continue
        for entry in entries:
            if entry.is_dir():
                if entry.name.casefold() not in skip:
                    stack.append(entry)
            else:
                out.append(entry)
    return out


def ends_with_ci(value: str, suffix: str) -> bool:
    """`EndsWith($s, OrdinalIgnoreCase)`. The comparison folds; the plain `.endswith` would not."""
    return value.casefold().endswith(suffix.casefold())


def starts_with_ci(value: str, prefix: str) -> bool:
    """`StartsWith($p, OrdinalIgnoreCase)`."""
    return value.casefold().startswith(prefix.casefold())


def relative_to(root: Path, path: Path) -> str:
    """`$file.Substring($Root.Length).TrimStart('\\','/').Replace('\\','/')`, done safely.

    The original built the relative form by string surgery on the absolute path, which silently
    produced a garbage path if `$Root` was ever not a prefix. `relative_to` REFUSES in that case
    instead, and a root that does not contain the file is a broken `--root`, not a finding.
    """
    try:
        return path.relative_to(root).as_posix()
    except ValueError as exc:
        raise Refusal("PATH-OUTSIDE-ROOT", f"{path} is not under {root}") from exc


# The accessor set this guard resolves siblings through, captured AFTER the fallback stubs above, so
# a run without the resolver hands the shared helper a tuple of `_absent` and gets the fixture's
# correct answer instead of the real workspace's.
_GUARD_ACCESSORS = (core_root, forge_root, fusion_root, web_root, workspace_root,
                    content_root, authored_content_root)


def repo_bases(root: Path) -> tuple[Path, ...]:
    """Every repository that could own a repo-relative path, THIS ONE FIRST.

    The registry writes its paths repository-relative - `tools/seedsmith/tests/...`,
    `src/FusionRpg.Launcher/...`, `.claude/cmdc-agents/scripts/...`, `data/seed/...` - because that
    is how each path is written next to the thing that owns it. This guard resolves every one of them
    against gk-core, which was the only repository when it was written and is one of nine now.

    Measured on this guard before the fix: 292 problems, of which **255 named a path that exists, in
    another repository** - 191 in gk-forge, 49 in gk-workflow, 15 in gk-fusion - and a further 19
    under gk-data's pack, which `content_root()` is the accessor for. So 274 of 292 findings were this
    one defect: the guard reading a nine-repository registry through a one-repository lens. A finding
    that is really a resolution failure is worse than no finding, because it reads as a broken contract
    and sends someone to fix the contract.

    ORDER IS THE CONTRACT: the local root answers first, so a repository's own path is always its own,
    and a sibling's is only reached when the local root does not have it.

    THE ALGORITHM NOW LIVES IN THE SHARED RESOLVER, with this guard's own accessor tuple passed in
    rather than the resolver's. That parameter is the whole reason this delegation is safe: this guard
    imports the resolver OPTIONALLY, and in a copied fixture every accessor above is a stub returning
    None, so handing the shared helper its own accessors would resolve a planted fixture against the
    REAL workspace - the exact blindness this function exists to remove. The one-line fallback below is
    that same case stated directly: with every accessor absent, nothing but `root` can be a base.
    """
    if _shared_repo_bases is not None:
        return _shared_repo_bases(root, _GUARD_ACCESSORS)
    return (root,)


def owning_base(rel: str, root: Path) -> Path | None:
    """The repository holding `rel`, or None when no repository does.

    None is the fail-closed answer and every caller keeps its original behaviour on it: a path that
    exists nowhere is still reported. This is NOT a fallback that makes a check weaker - the check
    still has to be satisfied, by a real file in the repository that owns it, and the set of
    repositories consulted is the fixed set the split produced rather than a search upward until
    something is found.
    """
    if _shared_owning_base is not None:
        return _shared_owning_base(rel, root, _GUARD_ACCESSORS)
    rel = str(rel).replace("\\", "/").strip()
    if not rel:
        return None
    return root if (root / rel).exists() else None


def resolved_path(rel: str, root: Path) -> Path:
    """`rel` as an absolute path, resolved against its owner when there is one.

    Falls back to `root / rel` so a caller can hand the result to something that expects a path
    whether or not the file was found - the existence check still happens separately.
    """
    base = owning_base(rel, root)
    return (base / rel) if base is not None else (root / rel)


def path_exists_anywhere(rel: str, root: Path) -> bool:
    return owning_base(rel, root) is not None


def path_is_dir_anywhere(rel: str, root: Path) -> bool:
    base = owning_base(rel, root)
    return base is not None and (base / rel).is_dir()


def load_json(path: Path, what: str) -> dict:
    """Read a registry, or refuse BY NAME.

    The original turned an unparseable boundary registry into
    `VERIFICATION BOUNDARY GUARD FAILED: invalid registry` and exit 1 — one message for a missing file,
    a directory, and a syntax error. Those are three faults and the caller cannot act on any of them,
    so each gets its own refusal.
    """
    if not path.is_file():
        raise Refusal("REGISTRY-MISSING", f"{what} is not a file: {path}")
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise Refusal("REGISTRY-UNREADABLE", f"{what}: {path}: {exc}") from exc
    try:
        doc = json.loads(text)
    except json.JSONDecodeError as exc:
        raise Refusal("REGISTRY-UNPARSEABLE", f"{what}: {path}: {exc}") from exc
    if not isinstance(doc, dict):
        raise Refusal("REGISTRY-NOT-AN-OBJECT",
                      f"{what}: {path}: the document is a {type(doc).__name__}, not an object")
    return doc


# `load_json` is for the guard's SUBJECT. The enforcement registry is a CATALOG the guard consults, and
# `load_catalog` handles it leniently, because losing a catalog must not suppress the twenty findings
# the subject still has. Making both refusals is the mistake this pair exists to prevent.


def only_fields(obj: object, allowed: tuple[str, ...], label: str, failures: list[str]) -> None:
    if not isinstance(obj, dict):
        return
    for name in obj:
        if name not in allowed:
            failures.append(f"{label} has unknown field: {name}")


def check_projects(root: Path, projects: dict, failures: list[str],
                   foreign: dict[str, list[str]] | None = None) -> None:
    """A `projects` value is one of three shapes: a path, an array of paths (a group), or an object
    naming its `runner`. The group's members are all `.csproj` and all real; a `pytest`/`script`
    project is never grouped.

    A PROJECT WHOSE OWNING REPOSITORY IS ABSENT IS COUNTED, NOT ASSERTED. Every project in this
    registry names its repository, and `repo` is exactly the question "whose file is this" — so it
    is asked through `_owning_repository`, the same helper the C8 exact-path rule uses, and the two
    can never disagree about which paths are reachable. Measured at cd04ab6 in a clone: eight
    `project file missing` and two `pytest root missing`, every one for gk-forge's tools or
    gk-fusion's Launcher.
    """
    for pid, value in projects.items():
        if not PROJECT_ID.match(pid):
            failures.append(f"invalid project id: {pid}")
            continue
        if isinstance(value, list):
            if not value:
                failures.append(f"empty project group: {pid}")
                continue
            for member in value:
                if not isinstance(member, str):
                    failures.append(f"project group member is not a .csproj path: {pid}")
                    continue
                if not member.endswith(".csproj"):
                    failures.append(
                        f"project group member is not a .csproj path: {pid}: {member}")
                    continue
                if not is_relative_registry_path(member):
                    failures.append(f"invalid project path: {pid}: {member}")
                    continue
                if not path_exists_anywhere(member, root):
                    owner = _owning_repository(member, root)
                    if owner is None or owner == UNATTRIBUTED:
                        failures.append(f"project file missing: {pid}: {member}")
                    elif foreign is not None:
                        foreign[owner].append(f"project {pid}: {member}")
        elif isinstance(value, str):
            if not is_relative_registry_path(value):
                failures.append(f"invalid project path: {pid}")
                continue
            if not path_exists_anywhere(value, root):
                # `tests/` is gk-core's own AND a sibling's, so a csproj under it cannot be attributed by
                # prefix. An unattributable row is a FINDING (see UNATTRIBUTED); a NAMED absent sibling is
                # counted. A `tests/FusionRpg.TreeBinder.Tests/...` entry therefore reports as a missing
                # project in a clone, which is the honest reading: this repository cannot see gk-forge.
                owner = _owning_repository(value, root)
                if owner is None or owner == UNATTRIBUTED:
                    failures.append(f"project file missing: {pid}")
                elif foreign is not None:
                    foreign[owner].append(f"project {pid}: {value}")
        elif isinstance(value, dict):
            # `-notcontains` FOLDS, so `Runner` and `PYTEST` are accepted spellings.
            runner = str(value.get("runner", ""))
            if runner.casefold() not in {r.casefold() for r in VALID_RUNNERS}:
                failures.append(f"unsupported runner: {pid}: {runner}")
                continue
            if runner.casefold() == "pytest":
                only_fields(value, PYTEST_PROJECT_FIELDS, f"project '{pid}'", failures)
                if "repo" in value and str(value["repo"]) not in VALID_OWNING_REPOS:
                    failures.append(
                        f"project '{pid}' names an owner outside the vocabulary: {value['repo']} "
                        f"(one of {', '.join(VALID_OWNING_REPOS)})")
                pytest_root = str(value.get("root", ""))
                if not is_relative_registry_path(pytest_root):
                    failures.append(f"invalid pytest root: {pid}: {pytest_root}")
                    continue
                if not path_is_dir_anywhere(pytest_root, root):
                    owner = str(value.get("repo", ""))
                    if owner and not _dir_present(owner, root):
                        if foreign is not None:
                            foreign[owner].append(f"pytest root {pid}: {pytest_root}")
                    else:
                        failures.append(f"pytest root missing: {pid}: {pytest_root}")
                if not str(value.get("tests", "")).strip():
                    failures.append(f"pytest project missing 'tests': {pid}")
            elif runner.casefold() == "script":
                only_fields(value, SCRIPT_PROJECT_FIELDS, f"project '{pid}'", failures)
                script_path = str(value.get("script", ""))
                if not is_relative_registry_path(script_path):
                    failures.append(f"invalid script path: {pid}: {script_path}")
                    continue
                if not path_exists_anywhere(script_path, root):
                    failures.append(f"script file missing: {pid}: {script_path}")
        else:
            failures.append(f"invalid project value: {pid}")


def check_boundaries(root: Path, doc: dict, guard_catalog: dict, failures: list[str],
                     owner_index: dict,
                     foreign: dict[str, list[str]] | None = None) -> list[dict]:
    """The per-boundary rules, and the owner rows themselves for the coverage walk."""
    boundaries = doc.get("boundaries")
    if not isinstance(boundaries, list):
        return []
    seen_ids: set[str] = set()
    owner_patterns: dict[str, str] = {}
    owners: list[dict] = []
    pytest_file_cache: dict[str, list[str] | None] = {}
    # The caller owns `foreign` so this function and check_projects share one tally; a direct caller
    # that passes none gets a private one rather than losing the NOTE entirely.
    foreign = defaultdict(list) if foreign is None else foreign

    def pytest_files(project_id: str) -> list[str]:
        if project_id not in pytest_file_cache:
            dirs = vb.pytest_project_dirs(doc.get("projects") or {}, project_id)
            # Resolved against the OWNING repository, and the corpus is relativised to that same
            # base - the two have to agree or a gk-forge test file is listed under a gk-core name and
            # matches nothing. For a gk-core-owned project this is unchanged.
            #
            # AN EMPTY LIST IS ALSO WHAT AN ABSENT PROJECT'S ROOT LOOKS LIKE, and the two are not the
            # same answer: one means "this project has no tests" (which is a registry defect) and the
            # other means "the repository holding them is not checked out". Recorded as None for the
            # second so the caller can name it — see the testFiles rule below.
            node = (doc.get("projects") or {}).get(project_id)
            owner = str(node.get("repo", "")) if isinstance(node, dict) else ""
            if owner and not _dir_present(owner, root):
                pytest_file_cache[project_id] = None
                return None
            base = owning_base(dirs.test_dir, root) if dirs else None
            pytest_file_cache[project_id] = (
                [relative_to(base, p) for p in base.rglob("test_*.py") if p.is_file()]
                if base and base.is_dir() else [])
        return pytest_file_cache[project_id]

    for boundary in boundaries:
        if not isinstance(boundary, dict):
            failures.append("boundary is not an object")
            continue
        bid = str(boundary.get("id", ""))
        only_fields(boundary, BOUNDARY_FIELDS, f"boundary '{bid}'", failures)
        if not BOUNDARY_ID.match(bid):
            failures.append(f"invalid boundary id: {bid}")
        if bid in seen_ids:
            failures.append(f"duplicate boundary id: {bid}")
        seen_ids.add(bid)
        kind = str(boundary.get("kind", ""))
        if kind.casefold() not in {k.casefold() for k in BOUNDARY_KINDS}:
            failures.append(f"unsupported boundary kind: {bid}")
        paths = boundary.get("paths") or []
        if not paths:
            failures.append(f"boundary has no paths: {bid}")
        for pattern in paths:
            if not is_relative_registry_path(str(pattern)):
                failures.append(f"invalid boundary path: {bid}: {pattern}")
            if kind == "owner":
                # `ToLowerInvariant()`: the ambiguity key folds, so two owner rows differing only in
                # case are the SAME pattern and one of them silently wins a directory.
                key = str(pattern).lower()
                if key in owner_patterns:
                    failures.append(
                        f"ambiguous owner pattern: {pattern} ({owner_patterns[key]}, {bid})")
                owner_patterns[key] = bid
            # C8: an exact pattern that names no file has silently dropped whatever it owned to a wider
            # fallback, with nothing failing until now.
            #
            # AN EXACT PATH IN AN ABSENT SIBLING IS NOT A STALE PATH. The paths above
            # (src/FusionRpg.Injector/**, docs/architecture/**, data/seed/**, tools/seedsmith/**,
            # .claude/**) each live in a repository this one is not: gk-fusion, gk-workflow, gk-data,
            # gk-forge. Measured at cd04ab6 in an isolated clone: 82 "stale exact path" findings, every
            # one a file that exists in the workspace and is absent here because its repository is.
            # Each was the guard reporting an unreachable question as an answer about the registry.
            #
            # A STALE path — one in THIS repository that no longer exists — is still a finding, and is
            # what C8 was written for: a rename that silently drops a boundary's subject must fail. The
            # discriminator is which repository would have to carry the path, so it is asked as exactly
            # that question. Absent-and-foreign is reported once, on stdout, with the count and the
            # repositories named; absent-and-local is a finding.
            if vb.exact_pattern(str(pattern)) and not path_exists_anywhere(str(pattern), root):
                owner_repo = _owning_repository(str(pattern), root)
                # A NAMED sibling that is absent, and nothing else, is an absent sibling. An
                # unattributable path is a finding: this repository does not know who owns it, and
                # "unknown" must not read as "someone else has it".
                if owner_repo is None or owner_repo == UNATTRIBUTED:
                    failures.append(f"stale exact path: {bid}: {pattern}")
                else:
                    foreign[owner_repo].append(f"{bid}: {pattern}")

        projects = doc.get("projects") or {}
        project = boundary.get("project")
        # C5: `project` is optional IFF `guards` is non-empty — a guard-only boundary's only honest
        # proof is "it still runs", not a fake test mapping. Naming NEITHER derives to `full`, which is
        # legal only under data/** or gk-core/tests/fixtures/**.
        if project:
            if project not in projects:
                failures.append(f"unknown project: {project}")
        elif not (boundary.get("guards") or []):
            ineligible = [str(p) for p in paths
                          if not any(starts_with_ci(str(p), pre)
                                     for pre in FULL_ELIGIBLE_PREFIXES)]
            if ineligible:
                failures.append(f"boundary needs a project or at least one guard: {bid}")

        level = boundary.get("level")
        if level is None:
            failures.append(f"invalid evidence level: {bid}")
        elif str(level).casefold() not in {l.casefold() for l in EVIDENCE_LEVELS}:
            failures.append(f"invalid evidence level: {bid}")
        else:
            derived = vb.derived_level(boundary)
            if str(level) != derived:
                failures.append(
                    f"level mismatch: {bid}: registry says '{level}', derived '{derived}'")

        for guard in (boundary.get("guards") or []):
            if guard not in guard_catalog:
                failures.append(f"unknown guard: {guard}")

        vid = boundary.get("verificationId")
        if vid:
            if not VERIFICATION_ID.match(str(vid)):
                failures.append(f"invalid VerificationId: {vid}")
            else:
                # C7: a group-level VerificationId needs only ONE member to carry the trait — that
                # member is exactly the one the planner's focused selection will run.
                members = vb.project_members(projects, project) if project else []
                node = projects.get(project) if project else None
                owner = str(node.get("repo", "")) if isinstance(node, dict) else ""
                # Same discipline as testFiles below: the trait lives in the project's OWN tests, so
                # when that repository is absent the question cannot be asked. Reported, not asserted.
                # An UNATTRIBUTED project is counted too — a `tests/**` entry could belong to gk-forge,
                # and this repository has no way to tell, so it says so rather than asserting.
                # THE QUESTION IS "CAN THIS PROJECT'S TRAIT BE LOOKED FOR HERE", and it is answered by
                # asking whether the project's own files resolve in ANY checked-out repository — not by
                # a prefix guess about which repository owns them. Measured, both directions:
                #   * guessing by prefix made the guard skip gk-core's OWN projects (a `tests/**`
                #     csproj is unattributable by prefix), so a well-formed vid that no test carries
                #     passed — the mutation control below caught exactly that;
                #   * answering "no" from a missing sibling made it skip the sibling's, which is the
                #     original defect.
                # `owning_base` consults every checked-out repository, so this is one question with one
                # answer in both layouts, and it never weakens the check where the files are present.
                resolvable = any(owning_base(str(m), root) is not None for m in members)
                unreachable = bool(members) and not resolvable
                if unreachable and foreign is not None:
                    foreign[owner or UNATTRIBUTED].append(f"VerificationId {bid}: {vid}")
                if not unreachable and members and not any(
                        vb.project_has_trait(root, str(resolved_path(m, root)), str(vid))
                        for m in members):
                    failures.append(f"VerificationId has no matching test trait: {vid}")

        # python-test-lane D2: file-selector pairing.
        test_files = boundary.get("testFiles")
        self_select = boundary.get("selfSelect")
        if project:
            runner = (vb.project_runner(projects, project) or "").casefold()
            if test_files and runner != "pytest":
                failures.append(f"testFiles only allowed on a pytest project: {bid}")
            if vid and runner in ("pytest", "script"):
                failures.append(f"verificationId not allowed on a {runner} project: {bid}")
            if self_select:
                if runner != "pytest":
                    failures.append(f"selfSelect only allowed on a pytest project: {bid}")
                else:
                    dirs = vb.pytest_project_dirs(projects, project)
                    test_dir = dirs.test_dir if dirs else ""
                    for p in paths:
                        if not starts_with_ci(str(p), f"{test_dir}/"):
                            failures.append(
                                "selfSelect boundary path outside its project's test directory: "
                                f"{bid}: {p}")
            if test_files and runner == "pytest":
                actual = pytest_files(project)
                for pattern in test_files:
                    if not is_relative_registry_path(str(pattern)):
                        failures.append(f"invalid testFiles pattern: {bid}: {pattern}")
                        continue
                    if actual is None:
                        # The project's own repository is absent, so the pattern cannot be matched.
                        # Counted in the NOTE below rather than asserted: measured at cd04ab6 in a
                        # clone, 30-odd of these for gk-forge and gk-workflow projects alone.
                        pnode = projects.get(project)
                        powner = str(pnode.get("repo", "")) if isinstance(pnode, dict) else ""
                        foreign[powner or UNATTRIBUTED].append(f"testFiles {bid}: {pattern}")
                        continue
                    if not any(vb.pattern_match(f, str(pattern)) for f in actual):
                        failures.append(
                            f"testFiles pattern matches no test file: {bid}: {pattern}")
        else:
            if test_files:
                failures.append(f"testFiles requires a project: {bid}")
            if self_select:
                failures.append(f"selfSelect requires a project: {bid}")
        if kind == "owner":
            owners.append(boundary)
    owner_index.update(owner_patterns)
    return owners


def red_debt_ids(root: Path) -> set[str]:
    r"""The `SR-\d+` ids whose stub-register row is `red`.

    `$cells[1] -eq 'red'` FOLDS, so a `RED` row resolves a debt. `==` here would invalidate every
    `knownRed` entry in a register that happened to use capitals.
    """
    # RESOLVED AGAINST ITS OWNING REPOSITORY. The stub register is a gk-workflow document and this
    # guard is gk-core's, so `root / STUB_REGISTER_RELPATH` found nothing, `red_debt_ids` returned the
    # empty set, and every `knownRed` entry reported that its debt did not resolve to a red row - five
    # findings that were one document in the wrong repository. The entries themselves were fine; the
    # question was unanswerable and the guard answered it confidently.
    base = owning_base(STUB_REGISTER_RELPATH, root)
    path = (base / STUB_REGISTER_RELPATH) if base is not None else (root / STUB_REGISTER_RELPATH)
    if not path.is_file():
        return set()
    ids: set[str] = set()
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ids
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped.startswith("|"):
            continue
        cells = [c.strip() for c in stripped.strip("|").split("|")]
        if len(cells) < 2:
            continue
        found = STUB_ID_CELL.match(cells[0])
        if not found:
            continue
        if cells[1].casefold() == "red":
            ids.add(found.group(2))
    return ids


def check_known_red(root: Path, doc: dict, failures: list[str]) -> None:
    """python-test-lane D6 rule 4: every `knownRed` entry's fields are exactly {project, test, debt};
    its `project` exists; the test's own file exists under that project; its `debt` resolves to a
    `red` row in the stub register. No test asserts how many entries exist — only that each is honest.

    AN ENTRY WHOSE OWNING REPOSITORY IS ABSENT IS NOT CHECKED, AND SAYS SO ON STDOUT.

    All five entries in this registry belong to project `seedsmith`, whose node names `repo: gk-forge`.
    Both questions they ask are therefore questions ABOUT gk-forge — is this test file there, and does
    SR-25 read `red` in the stub register — and in a standalone gk-core clone neither is answerable:
    measured at cd04ab6, `dotnet build` clean but this guard reporting ten findings
    ("knownRed entry's test file does not exist" / "its debt does not resolve to a red row"), which
    made `verify-change.py` refuse at INTEGRITY-GUARD-FAILED before it classified any path. An agent in
    a clone could not tell a good change from an unmapped one, because both produced byte-identical
    output and exit 1.

    THE TENSION, STATED. This check exists because a `knownRed` entry that names a vanished test is
    worse than no entry: it is a standing claim that a failure is accounted for, and if the test is gone
    nothing accounts for it. Answering "the repository is not checked out" is not a weakening of that —
    the entry is still checked, in every repository where it can be — but reporting ABSENCE as a
    finding is exactly the class of error `red_debt_ids`' own docstring records having already made once
    ("the entries themselves were fine; the question was unanswerable and the guard answered it
    confidently"). So the same discipline applies here: fail closed for a path that exists NOWHERE, and
    say plainly which entries were not examined and why.

    NOT a skip that hides a defect. The stdout line names every skipped entry and its owner, so a run
    that checked nothing is visibly a run that checked nothing — which is the whole difference between
    this and the silent pass being fixed.
    """
    entries = [e for e in (doc.get("knownRed") or []) if e]
    if not entries:
        return
    projects = doc.get("projects") or {}
    red_ids = red_debt_ids(root)
    unexamined: list[str] = []
    for entry in entries:
        if not isinstance(entry, dict):
            failures.append("knownRed entry is not an object")
            continue
        test = str(entry.get("test", ""))
        only_fields(entry, KNOWN_RED_FIELDS, f"knownRed entry '{test}'", failures)
        project_id = entry.get("project")
        if project_id not in projects:
            failures.append(f"knownRed entry names an unknown project: {project_id}: {test}")
        node = projects.get(project_id)
        owner = str(node.get("repo", "")) if isinstance(node, dict) else ""
        # A project that names NO repo is this repository's own, and is always checkable. Only a named
        # foreign repo can be absent.
        if owner and not _repo_present(owner, root):
            unexamined.append(f"{test} (owned by {owner})")
            continue
        test_file = test.split("::")[0]
        base = str(node.get("root", "")) if isinstance(node, dict) else ""
        rel = f"{base.rstrip('/')}/{test_file}" if base else test_file
        if not path_exists_anywhere(rel, root):
            failures.append(f"knownRed entry's test file does not exist: {test}")
        debt = str(entry.get("debt", "")).strip("`")
        if debt not in red_ids:
            failures.append(f"knownRed entry's debt does not resolve to a red row: "
                            f"{entry.get('debt')}: {test}")
    if unexamined:
        print(f"[{GUARD_ID}] NOTE: {len(unexamined)} knownRed entr(y/ies) NOT examined — the "
              f"repository that owns them is not checked out here, so the question cannot be asked "
              f"(this is an absent sibling, not a stale entry):")
        for item in unexamined:
            print(f"  - {item}")


#: Which repository carries which repository-relative PREFIX, for the "absent sibling or stale row?"
#: question. The prefixes are the ones the split moved; each is measured, not assumed, and every one is
#: checked with `is_dir()` on the owning repository so a path is never attributed by string shape alone.
#:
#: `None` in the value position means "this repository's own" — the prefix belongs to gk-core, so a
#: missing path there is a genuine stale row and stays a finding.
FOREIGN_PREFIX_OWNERS: tuple[tuple[str, str], ...] = (
    # ONLY a repository this guard can RECOGNISE may be reported as an absent sibling. A fixture root,
    # or any path no sibling could carry, is gk-core's own row and a missing file there is a genuine
    # stale path.
    #
    # "src/FusionRpg.Injector" and "src/FusionRpg.Launcher" are what the split moved out; `src/Fake` and
    # every other name is not in this list and therefore falls through to the stale finding. That is not
    # a special case for a test: it is the general rule, stated as a prefix list rather than as "is this
    # row's owner checkable", because a check that asks whether a row's OWNER is reachable must first be
    # able to name the owner, and `src/Fake` names nothing.
    ("src/FusionRpg.Injector", "gk-fusion"),
    ("src/FusionRpg.Launcher", "gk-fusion"),
    ("web/fusion-rpg-web", "gk-web"),
    ("tools/seedsmith", "gk-forge"),
    ("tools/Creature", "gk-forge"),
    ("tools/DominanceBaseline", "gk-forge"),
    ("tools/ItemSeedValidator", "gk-forge"),
    ("tools/FamilyExpandGen", "gk-forge"),
    (".claude", "gk-workflow"),
    ("tasks", "gk-workflow"),
    ("docs", "gk-workflow"),
    ("data/seed", "gk-data"),
    ("data/generated", "gk-data"),
)


#: The bucket name for a path this repository cannot attribute to ANY repository — not gk-core's own
#: (which would be a finding), and not a sibling it can name (which `_owning_repository` reports by
#: name). Measured at cd04ab6 in a clone: eight `tests/*.csproj` project entries and three root-level
#: files (`game-profiles.json`, `scripts/audit-doc-citations.py`, `.agents/skills/**`). These have no
#: prefix that discriminates — `tests/` is gk-core's own AND gk-forge's — so no honest prefix rule can
#: place them, and inventing one would be a guess about a sibling's layout.
#: The bucket for a path this repository cannot attribute to any sibling. ATTRIBUTION IS NOT PROOF OF
#: ABSENCE, so this bucket produces a FINDING — the fail-closed direction. A path named `src/Fake/Sample.cs`
#: in a planted fixture, or `tests/FusionRpg.Launcher.Tests/...` where no `repo` field says who owns it, is
#: not evidence that a sibling is missing; it is evidence that this repository does not know. Treating it
#: as "probably in a sibling" is what made T13 accept a stale path, and the guard's whole purpose is to
#: catch one.
#:
#: MEASURED: with UNATTRIBUTED treated as reachable, `T13_an_exact_path_that_stops_existing_fails_as_stale`
#: failed with "guard accepted a stale exact path" — the check C8 was written for, disabled by a change
#: that was supposed to be about absent siblings.
UNATTRIBUTED = "(not declared by this repository)"

#: PREFIXES NO SIBLING CARRIES, so a missing path under one of them is gk-core's own stale row and
#: stays a finding. Derived by measuring every sibling rather than by listing what gk-core has: gk-forge
#: and gk-fusion both own a `tests/` and a `scripts/`, which is precisely why those two prefixes are
#: absent here. Anything NOT in this table and not in FOREIGN_PREFIX_OWNERS is unattributed, which is
#: reported — so the classification errs toward SAYING IT CANNOT TELL, never toward calling a sibling's
#: file stale.
CORE_OWNED_PREFIXES: tuple[str, ...] = (
    "src/FusionRpg.Core",
    "src/FusionRpg.Contracts",
    "src/FusionRpg.Data",
    "src/FusionRpg.Bridge",
    "src/FusionRpg.Server",
    "src/FusionRpg.CheatCore",
    "data/tuning",
    "tools/CombatSim",
    "tools/ProveAptitude",
    "tools/ProvePredictor",
    "tools/RealDataAggregate",
    "tools/ResidualFitLoop",
    "tools/CreatureCorpusDump",
)


def _owning_repository(rel: str, root: Path) -> str | None:
    """The repository that WOULD carry `rel`, or None when it is gk-core's own and it is a real finding.

    A returned name means "not stale" — unreachable, or unattributable. Never "the row has rotted".
    """
    rel = str(rel).replace("\\", "/").strip()
    for prefix, repo in FOREIGN_PREFIX_OWNERS:
        if rel == prefix or rel.startswith(prefix + "/"):
            return None if _dir_present(repo, root) else repo
    for prefix in CORE_OWNED_PREFIXES:
        if rel == prefix or rel.startswith(prefix + "/"):
            return None
    return UNATTRIBUTED


#: Every repository a registry path can name, and the ACCESSOR that answers for it. One mapping, used
#: by both helpers below, so "which repository owns this" is asked one way and never two.
#:
#: `workspace_root` is the resolver's answer, not a `root/docs` probe. Measured: gk-core carries NEITHER
#: `docs/` nor `tasks/` (the split moved both up a level), so the obvious local probe reported the
#: workspace absent even in a full workspace and would have downgraded four real boundary paths
#: (tasks/sessions/**, .claude/cmdc-agents/**) to unverified there. Asking the resolver is the same
#: question the paths themselves are resolved through, so the two cannot disagree.
_REPO_ACCESSORS = {
    "gk-forge": "forge_root",
    "gk-fusion": "fusion_root",
    "gk-web": "web_root",
    "gk-data": "content_root",
    "gk-workflow": "workspace_root",
}


def _dir_present(repo: str, root: Path) -> bool:
    """Whether the named repository is actually on disk here.

    gk-core is this repository and is present by definition. Everything else is asked of the resolver,
    and every accessor here REFUSES when its subject is absent — which is the answer wanted here, so the
    exception is the mechanism rather than something swallowed.
    """
    if repo == "gk-core":
        return True
    name = _REPO_ACCESSORS.get(repo)
    if name is None or _shared_owning_base is None:
        return True        # unknown name, or no resolver: do not invent an absence
    accessor = globals().get(name)
    if accessor is None:
        return True
    try:
        return Path(accessor(root)).is_dir()
    except Exception:
        return False


def _repo_present(owner: str, root: Path) -> bool:
    """Whether the named repository is checked out. See `_dir_present`, which answers this for every
    registry repository from one mapping — a project's `repo` field names a sibling, and the question
    is identical to the one an exact boundary path asks."""
    return _dir_present(owner, root)


@dataclass
class Walk:
    """What the completeness walk found. `resolutions` is BOTH src and test sources.

    The original appends to one `$resolved` list from the src loop AND from the test loop, and every
    report heading is computed over that list — including the one that reads "production files mapped",
    which is a misnomer for "src + test sources". An earlier version of this port collected only `src/**`
    for the report, so the fallback table listed a different set of owners and the file count was
    roughly half the original's. A report that disagrees with the tool it reports on is worse than no
    report, so the shape is carried explicitly rather than implied.
    """

    resolutions: list[tuple[str, str, str]] = field(default_factory=list)
    src_files: list[Path] = field(default_factory=list)
    test_source_files: list[Path] = field(default_factory=list)
    tests_files: list[Path] = field(default_factory=list)
    tools_test_files: list[Path] = field(default_factory=list)


def coverage_walk(root: Path, owners: list[dict], failures: list[str]) -> Walk:
    """The whole-repo COMPLETENESS invariant."""
    walk = Walk()
    walk.src_files = [f for f in walk_files(root / "src") if ends_with_ci(f.name, ".cs")]
    walk.tests_files = walk_files(root / "tests")
    tools_dir = root / "tools"
    if tools_dir.is_dir():
        for sub in sorted(tools_dir.iterdir()):
            if sub.is_dir() and sub.name.endswith(".Tests"):
                walk.tools_test_files += walk_files(sub)
    walk.test_source_files = [f for f in walk.tests_files + walk.tools_test_files
                              if ends_with_ci(f.name, ".cs")]

    for file, label in [(f, "unmapped source") for f in walk.src_files] + \
            [(f, "unmapped test source") for f in walk.test_source_files]:
        rel = relative_to(root, file)
        resolution = vb.resolve_owner(rel, owners)
        if resolution is None:
            failures.append(f"{label}: {rel}")
            continue
        if not resolution.owners:
            continue
        # `Resolution.owners` is a tuple of plain boundary DICTS, not objects. The original's
        # `$resolution.Owners[0].verificationId` is PowerShell's property syntax on a PSCustomObject;
        # the Python twin keeps the raw dicts, so it is subscripted. Reaching for `.id` here is the
        # same mistake as `.Owners` - the shape has to be read, not recalled from the other language.
        winner = resolution.owners[0]
        walk.resolutions.append((rel, str(winner.get("id", "")),
                                 str(winner.get("verificationId") or "")))

    # seam-coverage S4: an ENFORCED root is already fully mapped by its own module, so an unmapped
    # file under it is a real registry omission rather than the general Bazel-diff gap that keeps
    # `data/**` and `gk-core/tests/fixtures/**` off the enforced list.
    for enforced in vb.ENFORCED_ROOTS:
        # `TrimEnd('/', '*')` on a char ARRAY here — distinct from the three-call chain in
        # session-boundary-check, and it does strip an interleaved run.
        base = str(enforced).rstrip("/*").rstrip("/")
        for file in walk_files(root / base):
            rel = relative_to(root, file)
            if vb.resolve_owner(rel, owners) is None:
                failures.append(f"unmapped enforced-root file: {rel}")

    return walk


def registered_project_paths(doc: dict) -> set[str]:
    """Every path named by a `projects` value. An OBJECT value (a pytest/script project) contributes
    nothing here, because C2 is about `.csproj` registration only — the original's `else` branch
    stringified the whole object, which could never match a `.csproj` path."""
    out: set[str] = set()
    for value in (doc.get("projects") or {}).values():
        if isinstance(value, list):
            out |= {m for m in value if isinstance(m, str)}
        elif isinstance(value, str):
            out.add(value)
    return out


def check_test_projects(root: Path, doc: dict, tests_files: list[Path],
                        tools_test_files: list[Path], failures: list[str]) -> None:
    registered = registered_project_paths(doc)
    candidates = [f for f in tests_files + tools_test_files
                  if ends_with_ci(f.name, ".Tests.csproj")]
    for file in candidates:
        rel = relative_to(root, file)
        if rel in registered or rel in EXEMPT_TEST_PROJECTS:
            continue
        failures.append(f"test project not in registry: {rel}")


def build_report(root: Path, doc: dict, owners: list[dict], walk: Walk) -> Report:
    resolutions = walk.resolutions
    report = Report(production_mapped=len(resolutions))
    focused = [r for r in resolutions if r[2]]
    report.verification_id_filtered = len(focused)
    report.whole_project_fallback = len(resolutions) - len(focused)
    # The original is `Group-Object boundary | Sort-Object Count -Descending`, which does NOT
    # secondary-sort the key, so the order within a count is whatever the grouping produced. A
    # deterministic `(-count, name)` is a declared divergence rather than a silent reordering: the
    # SETS and COUNTS are the reading, and the report is explicitly a reading.
    broad = Counter(r[1] for r in resolutions if not r[2])
    report.broad_by_boundary = sorted(broad.items(), key=lambda kv: (-kv[1], kv[0]))

    # C6: every `[Trait("VerificationId", "...")]` value no boundary selects, grouped by the project
    # whose directory the trait was found in. A READING, never a failure — an orphan trait can be
    # deliberate or simply not wired up yet.
    selected = {str(b["verificationId"]) for b in doc.get("boundaries") or []
                if isinstance(b, dict) and b.get("verificationId")}
    project_dirs: dict[str, str] = {}
    for pid in (doc.get("projects") or {}):
        for member in vb.project_members(doc.get("projects") or {}, pid):
            parent = str(Path(member).parent).replace("\\", "/")
            project_dirs[parent] = pid

    def report_project(rel: str) -> str:
        best = ""
        for directory in project_dirs:
            if starts_with_ci(rel, f"{directory}/") and len(directory) > len(best):
                best = directory
        return project_dirs[best] if best else "(unmapped)"

    by_project: dict[str, set[str]] = defaultdict(set)
    seen: set[Path] = set()
    for file in walk.src_files + walk.test_source_files:
        if file in seen:
            continue
        seen.add(file)
        try:
            text = file.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for found in TRAIT.finditer(text):
            vid = found.group(1)
            if vid in selected:
                continue
            by_project[report_project(relative_to(root, file))].add(vid)
    report.orphan_traits_by_project = {p: sorted(v) for p, v in sorted(by_project.items())}
    report.orphan_trait_ids = sorted({v for vs in by_project.values() for v in vs})

    # seam-coverage S3: every `full`-level owner — the live Bazel-diff gap list. A reading, never a
    # pinned count, since which domains land at `full` is a finding, not a decision.
    report.full_level = sorted(
        ((str(b.get("id", "")), [str(p) for p in (b.get("paths") or [])]) for b in owners
         if vb.derived_level(b) == "full"),
        key=lambda kv: kv[0])
    return report


def load_catalog(root: Path, failures: list[str]) -> dict[str, str]:
    """The guard -> script catalog, LENIENTLY.

    The original ACCUMULATED a missing or unparseable enforcement registry as one failure among all the
    others and carried on with an empty catalog:

        if (-not (Test-Path $enforcementPath)) { $failures += 'enforcement registry missing: ...' }
        else { try { ... } catch { $failures += 'enforcement registry is not valid JSON' } }

    The port made this a REFUSAL, which was over-closing in the direction that costs the most: it
    suppressed every OTHER finding. A fixture holding a valid boundary registry and no enforcement
    catalog received one refusal naming the catalog instead of the twenty boundary problems it actually
    had, and sixteen C# tests that assert a specific boundary finding went red on it. A validator that
    stops at the first missing input is not a validator.

    So the distinction is by ROLE, not by severity: the guard's SUBJECT is a refusal when unreadable
    (there is nothing to validate), and a CATALOG it consults is a finding. Both are reported, and the
    guard still walks the whole registry.
    """
    path = root / ENFORCEMENT_RELPATH
    if not path.is_file():
        failures.append(f"enforcement registry missing: {ENFORCEMENT_RELPATH}")
        return {}
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        failures.append("enforcement registry is not valid JSON")
        return {}
    if not isinstance(doc, dict):
        failures.append("enforcement registry is not valid JSON")
        return {}
    return doc


def check(root: Path, skip_coverage_walk: bool = False, want_report: bool = False) -> dict:
    doc = load_json(root / REGISTRY_RELPATH, "the verification-boundary registry")

    failures: list[str] = []
    enforcement = load_catalog(root, failures)
    only_fields(doc, REGISTRY_FIELDS, "registry", failures)
    if doc.get("schemaVersion") != vb.ACCEPTED_VERIFICATION_SCHEMA_VERSION:
        failures.append("unsupported schemaVersion")
    if doc.get("projects") is None or doc.get("boundaries") is None:
        # The original tested this BEFORE the projects loop and then went on to enumerate a null node,
        # so a registry missing `projects` produced a shape error per property on top of the real
        # finding. The port reports presence and stops: there is nothing to enumerate.
        failures.append("registry requires projects and boundaries")
        return {"guard": GUARD_ID, "verdict": "FAIL", "problems": _dedupe(failures),
                "report": None, "owners": 0, "boundaries": 0, "walked": False}

    # The `guards` map is gone (solid-enforcement `guard-runner`): guard ids resolve through ONE
    # catalog. A map here would be a second id -> script source of truth.
    if "guards" in doc:
        failures.append(
            "verification-boundaries.v1.json must not carry a 'guards' section - guard ids resolve "
            "through scripts/enforcement-registry.v1.json")
    guard_catalog: dict[str, str] = {}
    if enforcement.get("schemaVersion") != 1:
        failures.append("unsupported enforcement registry schemaVersion")
    guards_node = enforcement.get("guards")
    if not isinstance(guards_node, dict):
        failures.append("enforcement registry carries no 'guards' object")
    else:
        for gid, entry in guards_node.items():
            if isinstance(entry, dict):
                guard_catalog[gid] = str(entry.get("script", ""))

    projects = doc["projects"]
    # ONE accumulator for every "absent sibling, therefore unanswerable" case in this run, created
    # here so check_projects and check_boundaries report into the same tally and the NOTE below can
    # state one total instead of three that have to be added up by the reader.
    foreign: dict[str, list[str]] = defaultdict(list)
    check_projects(root, projects, failures, foreign)

    owner_index: dict[str, str] = {}
    owners = check_boundaries(root, doc, guard_catalog, failures, owner_index, foreign)

    walked = not skip_coverage_walk
    walk = Walk()
    if walked:
        walk = coverage_walk(root, owners, failures)
        check_test_projects(root, doc, walk.tests_files, walk.tools_test_files, failures)

    check_known_red(root, doc, failures)

    if foreign:
        # ONE note for the whole run, per absent repository, with the count. Every row in it is a
        # registry entry this repository cannot reach and therefore did not examine — a path, a project
        # file, a pytest root, a testFiles pattern or a VerificationId trait. They are not findings: each
        # is still checked in every checkout where its repository is present, and the workspace run is
        # what proves that (this guard exits 0 there with no NOTE at all).
        #
        # WHY IT IS PRINTED RATHER THAN SWALLOWED: a run that examined none of these must be visibly a
        # run that examined none. Silence is what made the 300-odd findings this replaces read as a
        # verdict about the registry, and it would make a genuine green here indistinguishable from one
        # that skipped the reachable half.
        print(f"[{GUARD_ID}] NOTE: {sum(len(v) for v in foreign.values())} registry entr(y/ies) NOT "
              f"examined — the repository that owns each is not checked out here. Absent siblings, not "
              f"stale rows:")
        for repo in sorted(foreign):
            print(f"  - {repo}: {len(foreign[repo])} entr(y/ies)")

    report = None
    if want_report:
        if not walked:
            # `-Report` needs the walk; combining the two is not a real use and the original did not
            # support it either. Saying so beats printing an empty report that reads as a clean run.
            raise Refusal("REPORT-NEEDS-THE-WALK",
                          "--report needs the coverage walk, so it cannot be combined with "
                          "--skip-coverage-walk. The original silently printed an empty report here.")
        report = build_report(root, doc, owners, walk).as_dict()

    return {"guard": GUARD_ID, "verdict": "FAIL" if failures else "OK",
            "problems": _dedupe(failures), "report": report, "owners": len(owners),
            "boundaries": len(doc.get("boundaries") or []), "walked": walked}


def _dedupe(failures: list[str]) -> list[str]:
    """`$failures | Select-Object -Unique`: first-seen order, later duplicates dropped.

    Two rules firing on one row produce one line. A dedupe that dropped a *different* finding would
    hide it, so the key is the whole message and the order is stable — both pinned by tests.
    """
    seen: set[str] = set()
    out: list[str] = []
    for failure in failures:
        if failure not in seen:
            seen.add(failure)
            out.append(failure)
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the verification-boundary registry is valid and maps what it must map "
                    "(replaces guard-verification-boundaries.ps1).")
    parser.add_argument("--root", type=Path, default=None,
                        help="the repository to check (default: this tool's own repository)")
    parser.add_argument("--report", action="store_true",
                        help="print how deep the registry maps src/** and which VerificationId "
                             "traits no boundary selects. A reading, never an assertion.")
    parser.add_argument("--skip-coverage-walk", action="store_true",
                        help="skip the whole-repo completeness walk; keep the registry's own "
                             "structural checks")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root or Path(__file__).resolve().parent.parent
    root = root.resolve()

    try:
        result = check(root, args.skip_coverage_walk, args.report)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "problems": [], "report": None,
                              "owners": 0, "boundaries": 0, "walked": False}, indent=2))
        else:
            print(f"VERIFICATION BOUNDARY GUARD FAILED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
        return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED

    if result["verdict"] == "FAIL":
        # Findings to stderr; the OK line is the only thing on stdout, so a caller reading stdout alone
        # cannot mistake a finding for a verdict.
        print("VERIFICATION BOUNDARY GUARD FAILED", file=sys.stderr)
        for problem in result["problems"]:
            print(f"  {problem}", file=sys.stderr)
        return EXIT_FAILED
    print("VERIFICATION BOUNDARY GUARD OK")

    report = result.get("report")
    if report:
        print("")
        print("Registry depth over src/** (a reading, not a contract):")
        print(f"  production files mapped : {report['productionMapped']}")
        print(f"  VerificationId-filtered : {report['verificationIdFiltered']}")
        print(f"  whole-project fallback  : {report['wholeProjectFallback']}")
        print("")
        print("  Owners still running a whole project, widest first:")
        for entry in report["wholeProjectByBoundary"]:
            print(f"    {entry['files']:>6}  {entry['boundary']}")
        print("")
        print("  Orphan VerificationId traits (no boundary selects them): "
              f"{len(report['orphanVerificationIdTraits'])}")
        for project, vids in report["orphanTraitsByProject"].items():
            print(f"    {project}:")
            for vid in vids:
                print(f"      {vid}")
        print("")
        print(f"  Inputs with no local proof (level 'full'): "
              f"{len(report['inputsWithNoLocalProof'])}")
        for entry in report["inputsWithNoLocalProof"]:
            print(f"    {entry['id']}: {', '.join(entry['paths'])}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
