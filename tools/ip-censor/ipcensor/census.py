"""The distinct-token census: what actually occurs in the tree, read-only.

`census` is what makes the registry honest. Without it a hand-written mark list aliases `wow` and
produces 142 prose false positives (ip-censor-ideal.md §6.4); the census is what would have caught
that on day one.

It decides nothing and enforces nothing: it enumerates every distinct token with its counts and the
surface each occurrence lands on, and stops there. Counts are a **reading** of a population that
changes whenever content ships, so a test may assert the shape of this output and never a total
(validation-ssot.md).
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from typing import Iterable, Iterator, Mapping

from ipcensor.registry import BoundaryPolicy, Registry, script_class
from ipcensor.source import SourceFile

# Structural: a path with no directory component still needs a distribution bucket, and an empty
# string would be indistinguishable from a missing key.
ROOT_TREE = "(root)"


@dataclass(frozen=True, slots=True)
class TokenStat:
    """One distinct token: its casefolded key, the casing a report should show, and its counts."""

    token: str
    display: str
    total: int
    by_tree: Mapping[str, int]
    by_surface: Mapping[str, int]

    def as_dict(self) -> dict[str, object]:
        """A JSON-ready view with nested keys sorted, so two runs serialise byte-identically."""
        return {
            "token": self.token,
            "display": self.display,
            "total": self.total,
            "by_tree": {key: self.by_tree[key] for key in sorted(self.by_tree)},
            "by_surface": {key: self.by_surface[key] for key in sorted(self.by_surface)},
        }


def tree_of(path: str) -> str:
    """The top-level tree a path belongs to, used as the census's distribution axis."""
    head, separator, _ = path.partition("/")
    return head if separator else ROOT_TREE


def tokenize(text: str, boundary: BoundaryPolicy) -> Iterator[tuple[str, str]]:
    """Yield `(casefolded token, original text)` for every token in `text`.

    Boundaries come from the registry's policy, never from `\\b`: `-`, `.` and `/` are non-word
    characters, so `\\bpvz\\b` matches inside `pvz-fusion-almanac-3.6.1` and `drop.pvz.run`, while it
    misses `PvZ融合版` entirely because `\\w` counts Han as a word character (ideal §6.1b–c). Folding
    is `str.casefold()`, never `.lower()`: `.lower()` misses `Pokémon`.
    """
    separators = frozenset(boundary.separators)
    for run in _runs(text, separators):
        parts = _split_on_script_change(run) if boundary.script_change_breaks else (run,)
        for part in parts:
            if not any(char.isalnum() for char in part):
                continue
            yield part.casefold(), part


def census(files: Iterable[SourceFile], registry: Registry) -> tuple[TokenStat, ...]:
    """Every distinct token in `files`, sorted by `(total desc, token asc)`.

    Deterministic by construction: the sort key is total then token, the display casing breaks ties
    on frequency then lexically, and the nested distributions are sorted when serialised.
    """
    totals: Counter[str] = Counter()
    displays: dict[str, Counter[str]] = {}
    by_tree: dict[str, Counter[str]] = {}
    by_surface: dict[str, Counter[str]] = {}

    for item in files:
        surface = registry.surface_for(item.path)
        tree = tree_of(item.path)
        for token, display in tokenize(item.text, registry.boundary):
            totals[token] += 1
            displays.setdefault(token, Counter())[display] += 1
            by_tree.setdefault(token, Counter())[tree] += 1
            by_surface.setdefault(token, Counter())[surface] += 1

    stats = [
        TokenStat(
            token=token,
            display=_dominant_display(displays[token]),
            total=total,
            by_tree=dict(by_tree[token]),
            by_surface=dict(by_surface[token]),
        )
        for token, total in totals.items()
    ]
    return tuple(sorted(stats, key=lambda stat: (-stat.total, stat.token)))


def _dominant_display(counter: Counter[str]) -> str:
    # Ties break on the text itself, not on insertion order, so the result is the same on every run.
    return min(counter.items(), key=lambda pair: (-pair[1], pair[0]))[0]


def _runs(text: str, separators: frozenset[str]) -> Iterator[str]:
    current: list[str] = []
    for char in text:
        if char in separators:
            if current:
                yield "".join(current)
                current = []
            continue
        current.append(char)
    if current:
        yield "".join(current)


def _split_on_script_change(run: str) -> tuple[str, ...]:
    """Split a run wherever the script changes between Han and Latin (ideal §6.1c)."""
    parts: list[str] = []
    current: list[str] = []
    previous = ""
    for char in run:
        script = script_class(char)
        if current and script and previous and script != previous:
            parts.append("".join(current))
            current = []
        current.append(char)
        previous = script or previous
    if current:
        parts.append("".join(current))
    return tuple(parts)
