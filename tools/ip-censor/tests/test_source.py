"""Tests for the tracked-tree reader (spec-source.md §Testing Strategy).

The synthetic trees are committed under `tests/fixtures/source/` and copied into a throwaway git
repo per test: the module's contract is `git ls-files`, so a plain directory would not exercise it.
The one fixture that must NOT be valid UTF-8 is committed under a non-text extension and renamed
into place by its test, so the real-tree smoke test stays safe.
"""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

from ipcensor.roots import owning_base
from ipcensor.source import (
    TEXT_EXTENSIONS,
    SourceError,
    find_root,
    is_text_path,
    iter_files,
    matches_any,
    read_file,
    tracked_paths,
)

TESTS_DIR = Path(__file__).resolve().parent
FIXTURES = TESTS_DIR / "fixtures" / "source"
REPO_ROOT = find_root(TESTS_DIR)


def make_repo(tmp_path: Path, *, extra: dict[str, bytes] | None = None) -> Path:
    """Copy the committed fixture tree into a throwaway git repo and index it."""
    root = tmp_path / "tree"
    shutil.copytree(FIXTURES / "tree", root)
    for name, data in (extra or {}).items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    subprocess.run(["git", "init", "-q"], cwd=root, check=True)
    subprocess.run(["git", "add", "-A", "-f"], cwd=root, check=True)
    return root


def paths_of(root: Path, **kwargs) -> list[str]:
    return [item.path for item in iter_files(root, **kwargs)]


# ---- enumeration ----------------------------------------------------------------


def test_enumeration_uses_the_extension_allowlist(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    paths = paths_of(root)

    assert "alpha.py" in paths
    assert "beta.json" in paths
    assert "notes.txt" in paths
    # A lockfile, a binary and an image are skipped by construction — not by a content sniff.
    assert "requirements.lock" not in paths
    assert "gamma.bin" not in paths
    assert "assets/logo.png" not in paths


def test_enumeration_yields_sorted_paths(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    paths = paths_of(root)
    assert paths == sorted(paths)
    assert paths, "the fixture tree yielded nothing"


def test_ignore_list_removes_a_subtree(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    paths = paths_of(root, ignore=("ignored",))
    assert not any(path.startswith("ignored/") for path in paths)
    assert "alpha.py" in paths


def test_self_paths_are_passed_in_and_excluded(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    paths = paths_of(root, self_paths=("selfdoc.md", "selfdir/**"))
    assert "selfdoc.md" not in paths
    assert not any(path.startswith("selfdir/") for path in paths)
    assert "alpha.py" in paths


def test_two_enumerations_are_identical(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    first = [(item.path, item.locate(0), item.locate(len(item.text))) for item in iter_files(root)]
    second = [(item.path, item.locate(0), item.locate(len(item.text))) for item in iter_files(root)]
    assert first == second
    assert first


def test_an_undecodable_file_throws_naming_its_path(tmp_path: Path) -> None:
    broken = (FIXTURES / "invalid-utf8.bin").read_bytes()
    root = make_repo(tmp_path, extra={"broken.py": broken})

    with pytest.raises(SourceError, match="broken.py"):
        read_file(root, "broken.py")
    with pytest.raises(SourceError, match="broken.py"):
        list(iter_files(root))


def test_tracked_paths_reports_the_repository() -> None:
    names = tracked_paths(REPO_ROOT)
    assert names == tuple(sorted(names))
    assert "README.md" in names
    # Tracked, but not scannable: the allowlist is applied on the way out, not in the listing.
    assert "tools/seedsmith/requirements.lock" not in names, (
        "REPO_ROOT is gk-core and gk-forge owns this file, so its presence here means the split left "
        "a copy behind; a duplicate lockfile is how two clones pin different matchers and both call "
        "themselves green"
    )


def test_tracked_paths_reaches_a_file_the_split_gave_to_a_sibling() -> None:
    """The same file, read from the repository that OWNS it.

    This line used to assert `tools/seedsmith/requirements.lock` was listed by `find_root(TESTS_DIR)`,
    which is a SIBLING read: gk-forge owns that file after the split and gk-core never had it, so the
    assertion could only ever have passed before the split. Dropping it outright would have deleted a
    real check, because the underlying claim - a tracked path that is not scannable, and is still
    listed in full - is still worth pinning. So it is pinned against the repository that carries it:
    the resolver answers `gk-forge`, and `tracked_paths` enumerates that repository, which is exactly
    how `report` scans a cross-repository path. Nothing about the check is loosened; only the root
    it is measured against is now the one that owns the file.
    """
    rel = "tools/seedsmith/requirements.lock"

    owner = owning_base(rel, TESTS_DIR)

    assert owner is not None, f"no repository carries {rel}"
    assert owner != REPO_ROOT, f"{rel} resolved to the same root the sibling read assumed"
    assert (owner / rel).is_file()
    assert rel in tracked_paths(owner)


# ---- line indexing --------------------------------------------------------------


def test_locate_maps_offsets_to_line_and_column(tmp_path: Path) -> None:
    root = tmp_path / "plain"
    root.mkdir()
    (root / "sample.txt").write_bytes(b"alpha\nbeta\ngamma\n")
    item = read_file(root, "sample.txt")

    assert item.text == "alpha\nbeta\ngamma\n"
    assert item.locate(0) == (1, 1)
    assert item.locate(4) == (1, 5)  # last character of line 1
    assert item.locate(5) == (1, 6)  # the newline itself
    assert item.locate(6) == (2, 1)  # a line start
    assert item.locate(10) == (2, 5)  # the second newline
    assert item.locate(11) == (3, 1)
    assert item.locate(16) == (3, 6)
    assert item.locate(len(item.text)) == (4, 1)  # EOF is a valid position


def test_locate_out_of_range_throws(tmp_path: Path) -> None:
    root = tmp_path / "plain"
    root.mkdir()
    (root / "sample.txt").write_bytes(b"one\n")
    item = read_file(root, "sample.txt")

    assert item.locate(4) == (2, 1)
    with pytest.raises(SourceError, match="past EOF"):
        item.locate(5)
    with pytest.raises(SourceError, match="negative"):
        item.locate(-1)


def test_crlf_is_normalised_before_indexing(tmp_path: Path) -> None:
    root = tmp_path / "plain"
    root.mkdir()
    (root / "crlf.txt").write_bytes(b"one\r\ntwo\r\n")
    item = read_file(root, "crlf.txt")

    assert item.text == "one\ntwo\n"
    assert "\r" not in item.text
    assert item.line_starts == (0, 4, 8)
    assert item.locate(4) == (2, 1)


def test_empty_file_has_one_line(tmp_path: Path) -> None:
    root = tmp_path / "plain"
    root.mkdir()
    (root / "empty.txt").write_bytes(b"")
    item = read_file(root, "empty.txt")

    assert item.line_starts == (0,)
    assert item.locate(0) == (1, 1)


# ---- pure predicates ------------------------------------------------------------


def test_is_text_path_accepts_the_allowlist_only() -> None:
    assert is_text_path("a/b/c.py", extensions=TEXT_EXTENSIONS, basenames=frozenset())
    assert is_text_path("a/B.TSX", extensions=TEXT_EXTENSIONS, basenames=frozenset())
    assert not is_text_path("a/logo.png", extensions=TEXT_EXTENSIONS, basenames=frozenset())
    assert not is_text_path("requirements.lock", extensions=TEXT_EXTENSIONS, basenames=frozenset())
    assert is_text_path(".gitignore", extensions=frozenset(), basenames=frozenset({".gitignore"}))
    # `splitext` gives a dotfile no suffix at all, which is why basenames exist.
    assert not is_text_path(".gitignore", extensions=frozenset(), basenames=frozenset())


def test_matches_any_handles_exact_prefix_and_glob() -> None:
    assert matches_any("tasks/ip-censor/x.md", ("tasks/ip-censor/**",))
    assert matches_any("tasks/ip-censor", ("tasks/ip-censor",))
    assert matches_any("tasks/ip-censor/x.md", ("tasks/ip-censor",))
    assert not matches_any("tasks/ip-censor-other/x.md", ("tasks/ip-censor",))
    assert not matches_any("anything", ())


# ---- real-tree smoke (the A8 tripwire) ------------------------------------------


def test_real_tree_enumerates_and_every_file_decodes() -> None:
    # Reading is decoding here: `iter_files` raises on the first undecodable file, so a successful
    # walk IS the A8 tripwire (spec-source.md §Testing Strategy, "Encoding corpus").
    files = list(iter_files(REPO_ROOT))
    assert files, "the tracked tree enumerated nothing"

    paths = [item.path for item in files]
    assert paths == sorted(paths)
    assert len(paths) == len(set(paths))
    assert "README.md" in paths


def test_real_tree_excludes_the_programs_own_files() -> None:
    files = list(iter_files(REPO_ROOT, self_paths=("tasks/ip-censor-plan.md",)))
    paths = [item.path for item in files]
    assert "tasks/ip-censor-plan.md" not in paths
    assert "README.md" in paths
