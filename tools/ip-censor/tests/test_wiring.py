"""Wiring half 1: the package installs, imports, and matches its committed lockfile.

spec-wiring.md §Testing Strategy level 2. The lockfile is READ here, never restated: a pin that
changes in `requirements.lock` without the environment following it fails this test, and a
environment that quietly moved to another matcher fails it too.
"""

from __future__ import annotations

import importlib
import importlib.metadata
import os
import subprocess
import sys
from pathlib import Path

import pytest

from ipcensor import report
from ipcensor.registry import MARKS_FILE
from ipcensor.roots import owned_dir

TOOL_ROOT = Path(__file__).resolve().parents[1]
LOCKFILE = TOOL_ROOT / "requirements.lock"
REGISTRY_FIXTURES = Path(__file__).resolve().parent / "fixtures" / "registry" / "valid"


def _locked_pins() -> list[tuple[str, str]]:
    """Every `name==version` line of the committed lockfile, in file order."""
    pins: list[tuple[str, str]] = []
    for raw in LOCKFILE.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        name, separator, version = line.partition("==")
        if not separator or not name.strip() or not version.strip():
            pytest.fail(f"requirements.lock line is not an exact pin: {raw!r}")
        pins.append((name.strip(), version.strip()))
    return pins


def test_lockfile_is_not_empty() -> None:
    assert _locked_pins(), "requirements.lock carries no pins"


@pytest.mark.parametrize(("name", "version"), _locked_pins())
def test_installed_version_matches_lockfile(name: str, version: str) -> None:
    assert importlib.metadata.version(name) == version, (
        f"{name} is installed at {importlib.metadata.version(name)}, the lockfile pins {version}"
    )


def test_package_imports() -> None:
    module = importlib.import_module("ipcensor")
    assert module.__name__ == "ipcensor"


def test_matcher_dependencies_import() -> None:
    # Both matchers the scan automaton needs (spec-scan.md §Testing Strategy). The distribution is
    # `pyahocorasick`; its import name is `ahocorasick`.
    import ahocorasick  # noqa: F401
    import regex  # noqa: F401


def _run_module(*argv: str, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    environment = dict(os.environ)
    environment["PYTHONPATH"] = str(TOOL_ROOT)
    return subprocess.run(
        [sys.executable, "-m", "ipcensor.report", *argv],
        capture_output=True,
        text=True,
        cwd=str(TOOL_ROOT if cwd is None else cwd),
        env=environment,
        check=False,
    )


def _registry_root(tmp_path: Path) -> Path:
    root = tmp_path / "root"
    directory = root / "data" / "seed" / "ip-censor" / "_registry"
    directory.mkdir(parents=True)
    for name in (
        "marks.v1.json",
        "scope-policy.v1.json",
        "boundary-policy.v1.json",
        "replacements.v1.json",
    ):
        (directory / name).write_text(
            (REGISTRY_FIXTURES / name).read_text(encoding="utf-8"), encoding="utf-8"
        )
    return root


def test_the_module_entry_point_runs_registry_check(tmp_path: Path) -> None:
    # The wiring contract from spec-wiring.md §Testing Strategy level 2, against a fixture registry:
    # the SHIPPED `gk-data/packs/fusion/data/seed/ip-censor/_registry/` is outside this lane's allowed paths, so the
    # shipped-registry form of this line lands with that data (T4 part 2).
    result = _run_module("registry-check", "--root", str(_registry_root(tmp_path)))

    assert result.returncode == 0, result.stderr
    assert "marks.v1.json" in result.stdout
    assert "v1" in result.stdout


def test_the_module_entry_point_reports_a_missing_registry(tmp_path: Path) -> None:
    result = _run_module("registry-check", "--root", str(tmp_path / "empty"))
    assert result.returncode == 2
    assert "ipcensor" in result.stderr


def test_the_module_entry_point_checks_the_shipped_registry() -> None:
    # spec-wiring.md §Testing Strategy level 2 and T10's acceptance line: the entry point exits 0 on
    # the registry the tool actually ships, with the root resolved from the git working tree (no
    # `--root`), exactly as the CI and release steps invoke it.
    #
    # The registry is located through the shared workspace resolver, because the split moved
    # `data/seed/**` into a gk-data pack. The subprocess below resolves it the SAME way from a
    # different working directory - that is the real point of this test, since CI invokes the
    # advisory scan from the repository root and this suite invokes it from `tools/ip-censor`; a
    # resolver anchored to the process working directory would pass here and crash there.
    repo_root = TOOL_ROOT.parents[1]
    shipped = owned_dir(report.DEFAULT_REGISTRY_DIR, repo_root)

    assert (shipped / MARKS_FILE).is_file(), shipped
    assert "gk-data" in shipped.parts, shipped

    result = _run_module("registry-check", cwd=repo_root)

    assert result.returncode == 0, result.stderr
    assert "marks.v1.json" in result.stdout
    assert "v1" in result.stdout
