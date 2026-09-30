#!/usr/bin/env python3
"""Shared pattern-matching and ownership-resolution logic for the verification-boundary registry.

A Python port of `scripts/lib/VerificationBoundaries.ps1` (registry-contract C4). That file is
dot-sourced by `scripts/verify-change.ps1`, and imported by `gk-core/scripts/guard-verification-boundaries.py`, so there is
exactly one answer to "does this path match this pattern" and "which of these matches is more
specific"; this module is the same single answer for the Python planner
(`gk-core/scripts/verify-change.py`), which is the entry point AGENTS.md now names.

**Two copies exist while both entry points do** — the PowerShell pair is still live because
`scripts/guard-verification-boundaries.ps1` was ported to Python on 2026-09-28, which is how the case-folding defect in `pattern_match` below surfaced: the Python and PowerShell twins DISAGREED, and the disagreement was only visible in a report because no verdict depended on it.
That duplication is exactly the defect C4 warns about, so it is pinned rather than left to drift:
`gk-core/tests/tools/test_verify_change.py` runs BOTH implementations over the same planted fixtures and
fails if they disagree. The PowerShell pair can be deleted only in the commit that also ports the
guard.

Import this module; it defines functions and two constants, and executes nothing else.
"""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

# The schemaVersion this planner, this lib and `VerificationBoundaries.ps1` understand. A stale copy
# of any of the three then refuses the registry file instead of misreading it (map §3.2). Bumped in
# the same commit as the planner/guard support for whatever the new version adds. TVB2.1 starts from
# 2, as SE0.7 (solid-enforcement) left it; TVB2.5 moved it to 3 (registry-contract's first v3-only
# construct); TVB3.1 moved it to 4 (python-test-lane D1: object-shaped `projects` values and the
# `runner` vocabulary); TVB4.1 moved it to 5 (seam-coverage S3: the `full` evidence level).
ACCEPTED_VERIFICATION_SCHEMA_VERSION = 5

# S4 (seam-coverage): the enforced-roots list, code-owned (never a registry field, so a session cannot
# switch enforcement on by editing JSON). A root enters this list ONLY in the commit that maps every
# file under it, in the fixed order the module states: `gk-core/data/tuning/**` (TVB4.4), `gk-core/tests/fixtures/**`
# (TVB4.5), `gk-data/packs/fusion/data/generated/**` (TVB4.6), `gk-data/packs/fusion/data/seed/**` (TVB4.7). Before a root is here, an unmapped
# file under it is still refused by the PLANNER (`resolve_owner` returns None) but does not fail the
# GUARD — that is what lets the four roots be mapped incrementally, one module at a time, instead of
# atomically in one commit. See `VerificationBoundaries.ps1` for the per-root provenance notes.
ENFORCED_ROOTS = ("data/tuning/**", "tests/fixtures/**", "data/generated/**", "data/seed/**")


# --------------------------------------------------------------------------------------------
# Pattern matching
# --------------------------------------------------------------------------------------------

def pattern_match(path: str, pattern: str) -> bool:
    """Does a repo-relative path match a registry pattern? (`Test-PatternMatch`.)

    Four pattern shapes, in the order this function checks them:
      - ``dir/**/*.md``    only the Markdown files under ``dir/`` (unbounded depth) — assistant-config
                            trees (``.claude/``, ``.agents/``, ``.commandcode/``) also hold scripts,
                            which must stay unmapped rather than inherit a docs-only boundary.
      - ``prefix/**``      matches any path under ``prefix/`` (unbounded depth, any extension).
      - ``dir/name*.ext``   a ``*`` in the FINAL segment only, matching ``[^/]*`` — never crosses a
                            ``/``.
      - an exact path      equals the pattern verbatim.

    Checked in that order because ``dir/**/*.md`` also ends in neither ``/**`` nor a bare
    final-segment wildcard shape the other branches would recognize correctly on their own. A ``*``
    anywhere else in a pattern is invalid grammar (see ``valid_pattern_grammar``); this function does
    not itself validate that — the guard does, once, at load time.

    Every comparison is case-insensitive, matching the PowerShell ``OrdinalIgnoreCase`` the original
    used throughout.
    """
    if pattern.endswith("/**/*.md"):
        return path.lower().startswith(pattern[:-7].lower()) and path.lower().endswith(".md")
    if pattern.endswith("/**"):
        return path.lower().startswith(pattern[:-2].lower())
    if "*" in pattern:
        last_slash = pattern.rfind("/")
        dir_part = pattern[: last_slash + 1] if last_slash >= 0 else ""
        file_part = pattern[last_slash + 1 :] if last_slash >= 0 else pattern
        lowered = path.lower()
        if not lowered.startswith(dir_part.lower()):
            return False
        path_tail = lowered[len(dir_part) :]
        if "/" in path_tail:
            return False  # the wildcard never reaches into a deeper directory
        # re.IGNORECASE IS LOAD-BEARING, and its absence was a live defect found by the
        # guard-verification-boundaries port's differential against the PowerShell twin.
        #
        # `path_tail` is taken from `lowered` (the LOWERCASED path) while `file_part` came straight out
        # of the pattern, so a pattern whose final segment carries ANY uppercase letter - `RpgStore.
        # Story*.cs`, `NarrativeText*.cs`, `DelveEvent*.cs` - produced a regex that could never match
        # the lowercased tail. Every such pattern silently matched NOTHING in Python while matching in
        # PowerShell, so the affected files fell to a wider fallback owner. Measured on this registry:
        # 14 owner patterns affected, and at least two files provably mis-resolved
        # (`gk-core/src/FusionRpg.Contracts/NarrativeTextDtos.cs` and
        # `gk-core/tests/FusionRpg.Guard.Tests/NarrativeDoctrineReadingGuardTests.cs`; `wildcard_match` says
        # True for both, `pattern_match` said False).
        #
        # The other three branches do not have this bug because they compare with `.lower()` on BOTH
        # sides, so the fix belongs here alone - which is exactly why a reading of the whole function
        # would not have found it and the differential did.
        regex = "^" + re.escape(file_part).replace(r"\*", "[^/]*") + "$"
        return re.match(regex, path_tail, re.IGNORECASE) is not None
    return path.lower() == pattern.lower()


def valid_pattern_grammar(pattern: str) -> bool:
    """Is a registry pattern's grammar legal? (`Test-ValidPatternGrammar`.)

    A ``*`` may appear only inside the FINAL path segment (as a suffix/prefix/whole-segment wildcard),
    or the pattern may end in the unrelated ``/**`` or ``/**/*.md`` forms. A ``*`` anywhere else — a
    non-final segment, or inside ``/**/*.md``'s own prefix — is invalid (registry-contract C4, T6).
    """
    if pattern.endswith("/**/*.md"):
        return "*" not in pattern[:-8]
    if pattern.endswith("/**"):
        return "*" not in pattern[:-3]
    if "*" not in pattern:
        return True
    last_slash = pattern.rfind("/")
    dir_part = pattern[:last_slash] if last_slash >= 0 else ""
    return "*" not in dir_part


def pattern_specificity(pattern: str) -> int:
    """(class, length) specificity, encoded as one sortable integer (`Get-PatternSpecificity`).

    class rank (exact=3 > wildcard=2 > ``/**/*.md``=1 > ``/**``=0) dominates, length breaks ties
    within a class. ``/**/*.md`` outranks a plain ``/**`` at the same or a shorter prefix because it
    is the narrower match (extension-filtered). Pattern length never approaches the 100000 multiplier
    (registry paths are well under that), so no real pattern pair can collide across classes.
    """
    if pattern.endswith("/**/*.md"):
        klass = 1
    elif pattern.endswith("/**"):
        klass = 0
    elif "*" in pattern:
        klass = 2
    else:
        klass = 3
    return (klass * 100000) + len(pattern)


def exact_pattern(pattern: str) -> bool:
    """Is a registry pattern "exact"? (`Test-ExactPattern`.)

    Neither the ``/**`` form nor a C4 last-segment wildcard. An exact pattern names one specific file,
    so a stale one (naming a file that no longer exists) silently drops whatever it once owned to a
    wider fallback with nothing failing (registry-contract C8).
    """
    return not pattern.endswith("/**") and "*" not in pattern


_WILDCARD_CACHE: dict[str, re.Pattern[str]] = {}


def wildcard_to_regex(pattern: str) -> re.Pattern[str]:
    """Translate a PowerShell ``WildcardPattern`` into a compiled anchored regex.

    ``*`` matches zero or more characters (INCLUDING a path separator, which is what makes a session
    fence of ``tests/Foo.Tests/**`` match ``tests/Foo.Tests/Bar/Baz.cs``), ``?`` matches exactly one
    character, and ``[...]`` is a character set whose ``!`` negates. Everything else is a literal.
    Case-insensitive, because every caller passes ``WildcardOptions.IgnoreCase``.
    """
    cached = _WILDCARD_CACHE.get(pattern)
    if cached is not None:
        return cached
    out: list[str] = ["^"]
    i = 0
    length = len(pattern)
    while i < length:
        char = pattern[i]
        if char == "*":
            out.append(".*")
            i += 1
        elif char == "?":
            out.append(".")
            i += 1
        elif char == "[":
            end = pattern.find("]", i + 1)
            if end < 0:  # an unclosed '[' is a literal bracket, as in the .NET translator
                out.append(re.escape(char))
                i += 1
                continue
            body = pattern[i + 1 : end]
            negated = body.startswith("!") or body.startswith("^")
            if negated:
                body = body[1:]
            out.append("[" + ("^" if negated else "") + body.replace("\\", "\\\\") + "]")
            i = end + 1
        else:
            out.append(re.escape(char))
            i += 1
    out.append("$")
    compiled = re.compile("".join(out), re.IGNORECASE)
    _WILDCARD_CACHE[pattern] = compiled
    return compiled


def wildcard_match(path: str, pattern: str) -> bool:
    """Case-insensitive full-string wildcard match, with both sides normalized to ``/``.

    This is the shape `verify-change.ps1` uses for a session fence (`Matches-SessionPath`) and for an
    explicit exemption pattern (`Test-ExemptionPath`): both passed a raw PowerShell
    ``WildcardPattern`` after replacing ``\\`` with ``/`` on the pattern AND on the path.
    """
    if not pattern.strip():
        return False
    return wildcard_to_regex(pattern.replace("\\", "/")).match(path.replace("\\", "/")) is not None


# --------------------------------------------------------------------------------------------
# Ownership resolution
# --------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class Resolution:
    """The outcome of `resolve_owner`: the tied winners and the specificity they won at."""

    owners: tuple[dict[str, Any], ...]
    specificity: int


def build_owner_pattern_index(owner_boundaries: Sequence[dict[str, Any]]) -> dict[str, list[tuple[dict, str]]]:
    """Index owner boundaries by the FIRST path segment of each of their patterns.

    (`New-OwnerPatternIndex`.) `pattern_match` can only match a path that shares the pattern's first
    segment (every legal pattern starts with a literal ``dir/`` — a ``*`` is legal only in the FINAL
    segment, `valid_pattern_grammar`), so this index is a SOUND pre-filter: for any path, the
    boundaries it can match are all in the bucket keyed by its own first segment.

    The index NEVER decides ownership: `resolve_owner` still runs the one `pattern_match` +
    specificity rule over the candidates. Keys are lower-cased because the pattern match is itself
    case-insensitive. The PowerShell original memoized this on the boundary-array instance for the
    guard's whole-tree walk; the planner resolves one or two paths per call, so building it once per
    call is the whole of the work.
    """
    index: dict[str, list[tuple[dict, str]]] = {}
    for boundary in owner_boundaries:
        for pattern in boundary.get("paths") or []:
            segment = str(pattern).split("/")[0]
            index.setdefault(segment.lower(), []).append((boundary, str(pattern)))
    return index


def resolve_owner(
    path: str,
    owner_boundaries: Sequence[dict[str, Any]],
    pattern_index: dict[str, list[tuple[dict, str]]] | None = None,
) -> Resolution | None:
    """Every owner boundary whose paths match, at the highest specificity found (`Resolve-Owner`).

    The planner/guard's "most specific owner wins" rule, in one place. Returns None when nothing
    matches; otherwise a `Resolution` whose `.owners` is one or more boundaries tied at the winning
    specificity — more than one means AMBIGUOUS.
    """
    if pattern_index is None:
        pattern_index = build_owner_pattern_index(owner_boundaries)
    segment = path.split("/")[0].lower()
    candidates = pattern_index.get(segment, [])

    hits: list[tuple[dict, int]] = []
    for boundary, pattern in candidates:
        if pattern_match(path, pattern):
            hits.append((boundary, pattern_specificity(pattern)))
    if not hits:
        return None

    best = max(specificity for _, specificity in hits)
    # `Sort-Object id -Unique` in the original: one entry per boundary, ordered by id.
    winners = sorted({id(boundary): boundary for boundary, spec in hits if spec == best}.values(),
                     key=lambda b: str(b.get("id", "")).lower())
    return Resolution(owners=tuple(winners), specificity=best)


def derived_level(boundary: dict[str, Any]) -> str:
    """The ``level`` a boundary's own shape implies (`Get-DerivedLevel`).

    registry-contract C3, extended by seam-coverage S3: ``seam`` for any seam boundary, ``focused`` for
    an owner with a selector (``verificationId`` for a dotnet project; ``testFiles`` or ``selfSelect``
    for a pytest project, python-test-lane D2), ``full`` for an owner with neither a ``project`` nor
    any ``guards`` (no local check exists; CI/nightly/release owns the evidence), ``module`` for an
    owner with no selector but at least a ``project`` or a guard. ``level`` still lives in the registry
    file (the planner prints it) — the guard fails a boundary whose stored ``level`` disagrees with
    this derivation; nothing about SELECTION changes, only the label a mismatch would otherwise let
    lie.
    """
    if boundary.get("kind") == "seam":
        return "seam"
    if boundary.get("verificationId") or boundary.get("testFiles") or boundary.get("selfSelect"):
        return "focused"
    if not boundary.get("project") and not [g for g in (boundary.get("guards") or []) if g]:
        return "full"
    return "module"


# --------------------------------------------------------------------------------------------
# Project entries
# --------------------------------------------------------------------------------------------

def project_members(projects_node: dict[str, Any], project_id: str) -> list[str]:
    """Every ``.csproj`` member of a ``projects`` entry (`Get-ProjectMembers`).

    A plain string is one member; an array is a project GROUP (registry-contract C7). The caller is
    responsible for having already validated the shape (the guard does, at load time); this just
    flattens it uniformly for the planner and for the guard's own per-member checks. An object-shaped
    entry (python-test-lane D1: a pytest or script project) has no ``.csproj`` members at all — it
    returns empty, so callers built for dotnet's group/focused-trait resolution simply have nothing to
    iterate instead of stringifying the object into a garbage path.
    """
    value = (projects_node or {}).get(project_id)
    if value is None:
        return []
    if isinstance(value, list):
        return [str(item) for item in value]
    if isinstance(value, str):
        return [value]
    return []


def project_runner(projects_node: dict[str, Any], project_id: str) -> str | None:
    """Which of the three closed ``runner`` kinds a ``projects`` entry is (`Get-ProjectRunner`).

    ``dotnet`` (a plain ``.csproj`` string, or a group array of them — C7 groups are csproj-only), or
    whatever an object entry's own ``runner`` field says (``pytest`` / ``script``, or something else
    the guard will reject). Returns None for an id the registry does not have. The original cast the
    object's runner with ``[string]``, so an absent field reads as the empty string rather than null;
    that shape is preserved.
    """
    value = (projects_node or {}).get(project_id)
    if value is None:
        return None
    if isinstance(value, (list, str)):
        return "dotnet"
    runner = value.get("runner") if isinstance(value, dict) else None
    return "" if runner is None else str(runner)


@dataclass(frozen=True)
class PytestDirs:
    """A pytest project's ``root``/``tests`` and the repo-relative test directory they combine into."""

    root: str
    tests: str
    test_dir: str


def pytest_project_dirs(projects_node: dict[str, Any], project_id: str) -> PytestDirs | None:
    """A pytest project's directories (`Get-PytestProjectDirs`).

    python-test-lane D1/D2, plus the repo-relative test directory the two combine into — the shape
    D2's ``testFiles`` existence check and ``selfSelect`` containment check both need. Returns None for
    anything that is not a pytest-runner project.
    """
    if project_runner(projects_node, project_id) != "pytest":
        return None
    value = projects_node[project_id]
    root = str(value.get("root", "") or "")
    tests = str(value.get("tests", "") or "")
    test_dir = (root.rstrip("/") + "/" + tests.lstrip("/")).rstrip("/")
    return PytestDirs(root=root, tests=tests, test_dir=test_dir)


def project_has_trait(root: Path, project_path: str, verification_id: str) -> bool:
    """Does the project at ``project_path``'s directory contain a matching ``VerificationId`` trait?

    (`Test-ProjectHasTrait`.) The same text scan the guard already performs for a single-project
    boundary, reused per-member for a group (C7: "focused selection on a group runs only the members
    whose directory contains the trait"). Case-insensitive, because ``Select-String`` is.
    """
    # An ABSOLUTE project_path is already resolved against its owning repository, which is how the
    # guard passes it now: this guard owns one repository and reads a registry whose paths belong to
    # nine, so it resolves each one before asking this question. A relative path still resolves
    # against `root`, unchanged, for every existing caller.
    resolved = Path(project_path)
    if not resolved.is_absolute():
        resolved = root / resolved
    project_directory = resolved.parent
    if not project_directory.is_dir():
        return False
    trait = re.compile(
        r'\[Trait\s*\(\s*"VerificationId"\s*,\s*"' + re.escape(verification_id) + r'"\s*\)\]',
        re.IGNORECASE,
    )
    for candidate in project_directory.rglob("*.cs"):
        if not candidate.is_file():
            continue
        try:
            text = candidate.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if trait.search(text):
            return True
    return False


# --------------------------------------------------------------------------------------------
# pytest evidence (python-test-lane D3/D6)
# --------------------------------------------------------------------------------------------

def known_red_test_matches_node(known_red_test: str, classname: str, name: str) -> bool:
    """Does a ``knownRed`` entry's ``test`` field name the same test a junit ``<testcase>`` reports?

    (`Test-KnownRedTestMatchesNode`, python-test-lane D6.) The entry is a pytest node id
    (``file.py::Class::method`` or ``file.py::method``); pytest's junit XML never writes the node id
    itself, only the dotted module/class path and the bare method name — so this derives what pytest's
    OWN ``classname`` would read for that node id directly from the registry string, rather than
    guessing at the XML's shape. PowerShell's ``-eq`` is case-insensitive, and so is this.
    """
    parts = known_red_test.split("::")
    if len(parts) < 2:
        return False
    file_part = parts[0]
    name_expected = parts[-1]
    module_dotted = re.sub(r"\.py$", "", file_part).replace("/", ".")
    if len(parts) > 2:
        classname_expected = module_dotted + "." + ".".join(parts[1:-1])
    else:
        classname_expected = module_dotted
    return classname_expected.lower() == classname.lower() and name_expected.lower() == name.lower()


@dataclass(frozen=True)
class JUnitTestCase:
    """One ``<testcase>`` from a pytest junit report."""

    classname: str
    name: str
    failed: bool


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def junit_test_cases(junit_xml_path: Path) -> list[JUnitTestCase]:
    """Every ``<testcase>`` in a pytest junit XML report (`Get-JUnitTestCases`).

    ``Failed`` is true when the case carries a ``<failure>`` or ``<error>`` child. Used against the
    runner's own temp ``--junitxml`` output at execution time.

    One deliberate widening of the PowerShell original, in the fail-closed → correct direction: the
    original selected ``//testcase`` (XPath with no namespace axis), so a NAMESPACE-qualified junit
    document matched nothing and the caller refused with "executed zero tests". Elements are matched
    here by LOCAL name, so a namespaced report yields the same evidence PowerShell would have refused
    rather than a silent empty list. It can only ever select more real test cases, never fewer, and it
    never turns a refusal into a pass without real test nodes behind it.
    """
    document = ET.parse(junit_xml_path).getroot()
    result: list[JUnitTestCase] = []
    for element in document.iter():
        if _local_name(element.tag) != "testcase":
            continue
        failed = any(_local_name(child.tag) in ("failure", "error") for child in element)
        result.append(JUnitTestCase(
            classname=element.get("classname") or "",
            name=element.get("name") or "",
            failed=failed,
        ))
    return result


@dataclass(frozen=True)
class KnownRedOutcome:
    """The outcome a runner reads off a junit report (`Resolve-KnownRedOutcome`)."""

    ok: bool
    messages: list[str]


def resolve_known_red_outcome(
    project_id: str,
    test_cases: Iterable[JUnitTestCase],
    known_red_entries: Sequence[dict[str, Any]],
) -> KnownRedOutcome:
    """python-test-lane D6 rule 3.

    Every failure that matches a ``knownRed`` entry for this project passes with a printed
    ``KNOWN RED (pre-existing) <test> -> <SR-id>`` line; any other failure fails the check; any
    ``knownRed`` entry whose test RAN and PASSED in this run fails with
    ``stale knownRed entry <test>: remove it and its red row`` (the list can only shrink). A
    ``knownRed`` test that was not selected/run this call has no effect — checked purely by absence
    from ``test_cases``, never assumed.
    """
    project_entries = [entry for entry in (known_red_entries or []) if entry.get("project") == project_id]
    messages: list[str] = []
    ok = True
    for case in test_cases:
        if not case.failed:
            continue
        match = next(
            (entry for entry in project_entries
             if known_red_test_matches_node(str(entry.get("test", "")), case.classname, case.name)),
            None,
        )
        if match is not None:
            messages.append(f"KNOWN RED (pre-existing) {match.get('test')} -> {match.get('debt')}")
        else:
            ok = False
            messages.append(f"UNEXPECTED FAILURE: {case.classname}::{case.name}")
    for entry in project_entries:
        ran_and_passed = any(
            not case.failed and known_red_test_matches_node(str(entry.get("test", "")), case.classname, case.name)
            for case in test_cases
        )
        if ran_and_passed:
            ok = False
            messages.append(f"stale knownRed entry {entry.get('test')}: remove it and its red row")
    return KnownRedOutcome(ok=ok, messages=messages)
