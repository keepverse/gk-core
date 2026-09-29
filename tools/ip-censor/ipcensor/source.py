"""The tracked-tree reader: deterministic file enumeration and line indexing.

`source` answers exactly one question — *what are the scannable units of the tracked tree, and where
does line N of each begin?* It holds **no policy**: no notion of a mark, a bucket or a scope
(spec-source.md §Objective). A scope bug and a reader bug must never be confusable, which is why the
registry lives in its own module and this one never reads it.

Two runs over the same commit produce identical `(path, line, column)` output: a `source` failure is
always loud, never a silently shortened tree.
"""

from __future__ import annotations

import fnmatch
import subprocess
from bisect import bisect_right
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator, Sequence

# Structural constant, not tunable: an encoding is a parse concern, not a balance number
# (tunables-ssot.md §1 "Structural"). A fallback decode is deliberately NOT offered — see A8.
DEFAULT_ENCODING = "utf-8"

# An ALLOWLIST, not a denylist (spec-source.md §Open Questions 1): an unknown binary extension is
# skipped by construction, so a PNG never reaches the decoder and no content sniff is needed.
TEXT_EXTENSIONS: frozenset[str] = frozenset(
    {
        ".cs",
        ".csproj",
        ".props",
        ".slnx",
        ".xaml",
        ".resx",
        ".runsettings",
        ".py",
        ".ps1",
        ".psm1",
        ".sh",
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".css",
        ".scss",
        ".html",
        ".svg",
        ".json",
        ".jsonl",
        ".md",
        ".mdc",
        ".txt",
        ".yml",
        ".yaml",
        ".toml",
        ".ini",
        ".cfg",
        ".po",
        ".pot",
        ".sql",
        ".manifest",
        ".example",
    }
)

# Extensionless tracked text files, matched on the full basename. `.editorconfig` and `.gitignore`
# have no `splitext` suffix at all, which is why they are listed here rather than above.
TEXT_BASENAMES: frozenset[str] = frozenset(
    {
        ".editorconfig",
        ".gitattributes",
        ".gitignore",
        "CODEOWNERS",
        "LICENSE",
        "NOTICE",
    }
)


class SourceError(RuntimeError):
    """A tree read failed: an undecodable file, an out-of-range offset, or a git failure."""


@dataclass(frozen=True, slots=True)
class SourceFile:
    """One scannable tracked file.

    `path` is repository-relative with forward slashes. `text` is decoded once and CRLF-normalised
    once. `line_starts` holds the character offset at which each line begins, so a match offset maps
    to the same `(line, column)` a person sees in an editor.
    """

    path: str
    text: str
    line_starts: tuple[int, ...]

    def locate(self, offset: int) -> tuple[int, int]:
        """Character offset into `text` -> 1-based `(line, column)`.

        Offset `0` is `(1, 1)`; `offset == len(text)` is EOF and is valid; anything past EOF, or
        negative, raises `SourceError`. Columns are character counts on the CRLF-normalised text, so
        a reported column matches the editor's.
        """
        if offset < 0:
            raise SourceError(f"{self.path}: offset {offset} is negative")
        if offset > len(self.text):
            raise SourceError(
                f"{self.path}: offset {offset} is past EOF (text is {len(self.text)} characters)"
            )
        index = bisect_right(self.line_starts, offset) - 1
        return index + 1, offset - self.line_starts[index] + 1


def find_root(start: Path | str) -> Path:
    """The working-tree root that contains `start`.

    `report` resolves the scanned tree and the registry from here and never from the process working
    directory: `git ls-files` run inside `gk-core/tools/ip-censor` would enumerate only that folder and any
    gate over it would pass vacuously (plan D4).
    """
    result = subprocess.run(
        ["git", "-C", str(start), "rev-parse", "--show-toplevel"],
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.decode(DEFAULT_ENCODING, errors="replace").strip()
        raise SourceError(f"not inside a git working tree: {start} ({detail})")
    return Path(result.stdout.decode(DEFAULT_ENCODING).strip())


def tracked_paths(root: Path | str) -> tuple[str, ...]:
    """Every tracked path under `root`, repository-relative, forward slashes, sorted.

    `-z` is used so a path is never quoted or C-escaped by git, whatever its characters.
    """
    result = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.decode(DEFAULT_ENCODING, errors="replace").strip()
        raise SourceError(f"git ls-files failed in {root}: {detail}")
    names = result.stdout.decode(DEFAULT_ENCODING).split("\0")
    return tuple(sorted(name for name in names if name))


def read_file(root: Path | str, path: str) -> SourceFile:
    """Read one repository-relative `path` into a `SourceFile`.

    A file that is not valid UTF-8 **throws**, naming the path (A8). All tracked text-extension files
    decoded cleanly when this invariant was measured, so a decode failure means a genuinely new file
    class arrived — and a silent replacement decode would make a finding disappear without a trace.
    """
    full = Path(root) / path
    try:
        data = full.read_bytes()
    except OSError as exc:
        raise SourceError(f"{path}: cannot be read ({exc})") from exc
    try:
        text = data.decode(DEFAULT_ENCODING)
    except UnicodeDecodeError as exc:
        raise SourceError(
            f"{path}: not valid {DEFAULT_ENCODING} ({exc.reason} at byte {exc.start})"
        ) from exc
    # CRLF-normalise explicitly rather than relying on the text-mode default, so `locate()` is
    # defined on text this module produced (spec-source.md §Open Questions 2).
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    return SourceFile(path=path, text=text, line_starts=_line_starts(text))


def is_text_path(path: str, *, extensions: frozenset[str], basenames: frozenset[str]) -> bool:
    """Whether `path` is a scannable text unit under the allowlist."""
    name = path.rsplit("/", 1)[-1]
    if name in basenames:
        return True
    suffix = ""
    dot = name.rfind(".")
    if dot > 0:
        suffix = name[dot:].lower()
    return suffix in extensions


def matches_any(path: str, patterns: Iterable[str]) -> bool:
    """Whether `path` is `patterns` member: an exact path or anything under it.

    A pattern carrying a wildcard is matched with `fnmatch`, so `tasks/ip-censor/**` covers the
    class-3 self paths the registry declares (spec-registry.md §self_paths).
    """
    for raw in patterns:
        pattern = str(raw).replace("\\", "/").strip("/")
        if not pattern:
            continue
        if any(ch in pattern for ch in "*?["):
            if fnmatch.fnmatchcase(path, pattern):
                return True
        elif path == pattern or path.startswith(pattern + "/"):
            return True
    return False


def iter_files(
    root: Path | str,
    *,
    extensions: frozenset[str] = TEXT_EXTENSIONS,
    basenames: frozenset[str] = TEXT_BASENAMES,
    ignore: Sequence[str] = (),
    self_paths: Sequence[str] = (),
) -> Iterator[SourceFile]:
    """Yield the tracked, scannable files under `root`, in sorted path order.

    `self_paths` is passed in, never known here: the module must not learn what the program's own
    files are, or a reader bug and a scope bug become indistinguishable.
    """
    for path in tracked_paths(root):
        if not is_text_path(path, extensions=extensions, basenames=basenames):
            continue
        if matches_any(path, ignore) or matches_any(path, self_paths):
            continue
        yield read_file(root, path)


def _line_starts(text: str) -> tuple[int, ...]:
    starts = [0]
    starts.extend(index + 1 for index, char in enumerate(text) if char == "\n")
    return tuple(starts)
