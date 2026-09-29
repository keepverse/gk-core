"""Match every registry alias against the tree and classify each hit.

The whole value of `scan` is one distinction: **the same word means different things in different
places.** `pvz` is the product premise, another company's mark, and a code namespace; `Overwatch` in
`docs/research/**` is an attributed citation while the same word in a shipped tree node is a
player-facing name. A scanner that cannot tell those apart produces the thousands-of-false-positives
failure that gets a tool disabled.

`scan` produces findings and never edits anything; it has no write path at all. It is a **release
gate and only that** (IC-3): it never blocks a commit, a `verify-change.ps1` run or CI.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Iterable, Iterator, Literal

import ahocorasick

from ipcensor.registry import BoundaryPolicy, Registry, classify_remediation, script_class
from ipcensor.source import SourceFile, matches_any

# The bucket vocabulary is closed and owned here (the capability map places it in `scan`). Four members
# follow the surface directly; the rest split `docs-prose` and the registry surface apart, because a
# citation, a deliberate in-house mention, and a hit inside an authored registry are three different
# things for the reader and only the surface could not tell them apart.
Bucket = Literal[
    "player-name",
    "player-prose",
    "generator-prompt",
    "code-identifier",
    "docs-prose-citation",
    "deliberate-identity",
    "registry-self",
]

BUCKETS: frozenset[str] = frozenset(
    {
        "player-name",
        "player-prose",
        "generator-prompt",
        "code-identifier",
        "docs-prose-citation",
        "deliberate-identity",
        "registry-self",
    }
)

# Buckets that take their name from the surface one-for-one. The remaining two surfaces
# (`docs-prose`, `registry`) need a rule, below.
_SURFACE_BUCKETS: frozenset[str] = frozenset(
    {"player-name", "player-prose", "generator-prompt", "code-identifier"}
)

# Attributed prior-art research is the one docs root whose hits are citations (IC-1): the ideal
# measured ~3,400 of them, overwhelmingly sourced references, which stay report-only rather than
# becoming enforced findings with no owner.
CITATION_PATHS: tuple[str, ...] = ("docs/research/**",)

DOCUMENT_EXTENSIONS: tuple[str, ...] = (".json",)


@dataclass(frozen=True, slots=True)
class Finding:
    """One registry hit. `remediation` is carried, never recomputed by a consumer."""

    path: str
    line: int
    column: int
    matched: str
    mark: str
    bucket: str
    surface: str
    remediation: str

    def as_dict(self) -> dict[str, object]:
        return {
            "path": self.path,
            "line": self.line,
            "column": self.column,
            "matched": self.matched,
            "mark": self.mark,
            "bucket": self.bucket,
            "surface": self.surface,
            "remediation": self.remediation,
        }


def bucket_for(surface: str, path: str) -> str:
    """Which bucket a hit on `surface` at `path` lands in — exactly one, by construction."""
    if surface in _SURFACE_BUCKETS:
        return surface
    if surface == "registry":
        return "registry-self"
    if matches_any(path, CITATION_PATHS):
        return "docs-prose-citation"
    return "deliberate-identity"


def has_token_boundary(
    text: str, start: int, end: int, boundary: BoundaryPolicy
) -> bool:
    """Whether `text[start:end]` is a whole token under the policy.

    Never bare `\\b`: that is a `\\w` transition, so it matches inside `pvz-fusion-almanac-3.6.1`
    (`-` and `.` are non-word characters) while missing `PvZ融合版` entirely (`\\w` counts Han as a
    word character). A boundary here is: an edge of the text, a declared separator, or a script
    change when the policy declares one.
    """
    separators = frozenset(boundary.separators)
    before = text[start - 1] if start > 0 else ""
    after = text[end] if end < len(text) else ""
    matched = text[start:end]
    if not matched:
        return False
    return _edge_is_boundary(before, matched[0], separators, boundary) and _edge_is_boundary(
        after, matched[-1], separators, boundary
    )


def fold(text: str) -> tuple[str, tuple[tuple[int, int], ...] | None]:
    """`str.casefold()` the text and remember where every folded character came from.

    Folding can change a character's length (`ẞ` -> `ss`), so offsets must be mapped rather than
    assumed equal: `spans[i]` is the original `(start, end)` of folded index `i`. ASCII text folds
    1:1, so it returns `spans=None` and the common path costs no per-character Python loop — measured
    at 94 s of a 106 s full-tree scan before this fast path existed.
    """
    folded = text.casefold()
    if text.isascii():
        return folded, None
    spans: list[tuple[int, int]] = []
    for index, char in enumerate(text):
        piece = char.casefold()
        spans.extend([(index, index + 1)] * len(piece))
    return folded, tuple(spans)


class AliasMatcher:
    """The automaton, built once from the registry and reused for every file."""

    def __init__(self, registry: Registry) -> None:
        self._boundary = registry.boundary
        automaton = ahocorasick.Automaton()
        for group in registry.groups:
            for alias in group.aliases:
                key = alias.text.casefold()
                if not key:
                    continue
                automaton.add_word(key, (key, group.mark))
        automaton.make_automaton()
        self._automaton = automaton

    def matches(self, text: str) -> Iterator[tuple[int, int, str]]:
        """Yield `(start, end, mark)` for every unbounded alias occurrence, longest-match-first."""
        folded, spans = fold(text)
        candidates: list[tuple[int, int, str]] = []
        for end, (key, mark) in self._automaton.iter(folded):
            start = end - len(key) + 1
            candidates.append((start, end + 1, mark))
        # Longest-match-first: at the same start the longest spelling wins, so `plants vs. zombies`
        # is one hit rather than `plants` plus a shorter alias inside it.
        candidates.sort(key=lambda item: (item[0], -item[1]))

        accepted_end = -1
        for start, end, mark in candidates:
            if start < accepted_end:
                continue
            if not has_token_boundary(folded, start, end, self._boundary):
                continue
            accepted_end = end
            if spans is None:
                yield start, end, mark
            else:
                yield spans[start][0], spans[end - 1][1], mark


def scan(files: Iterable[SourceFile], registry: Registry) -> tuple[Finding, ...]:
    """Every registry hit in `files`, bucketed, remediation-carrying and deterministically ordered."""
    matcher = AliasMatcher(registry)
    findings: list[Finding] = []

    for item in files:
        surface = registry.surface_for(item.path)
        bucket = bucket_for(surface, item.path)
        remediation = classify_remediation(
            path=item.path, surface=surface, document=document_of(item)
        )
        for start, end, mark in matcher.matches(item.text):
            line, column = item.locate(start)
            findings.append(
                Finding(
                    path=item.path,
                    line=line,
                    column=column,
                    matched=item.text[start:end],
                    mark=mark,
                    bucket=bucket,
                    surface=surface,
                    remediation=remediation,
                )
            )

    return tuple(sorted(findings, key=lambda f: (f.path, f.line, f.column, f.mark)))


def document_of(item: SourceFile) -> object | None:
    """The file's parsed JSON when it is JSON, else `None`.

    Used only to read provenance for the remediation derivation. A file that does not parse is not an
    error here: the scanner's job is to find marks, and `source` already refused anything undecodable.
    """
    if not item.path.endswith(DOCUMENT_EXTENSIONS):
        return None
    try:
        return json.loads(item.text)
    except json.JSONDecodeError:
        return None


def _edge_is_boundary(
    neighbour: str, edge: str, separators: frozenset[str], boundary: BoundaryPolicy
) -> bool:
    if neighbour == "":
        return True
    if neighbour in separators:
        return True
    if not boundary.script_change_breaks:
        return False
    left = script_class(neighbour)
    right = script_class(edge)
    return bool(left) and bool(right) and left != right
