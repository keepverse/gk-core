"""The root conftest must actually apply what nested packages declare, and must not rot into a no-op.

A conftest that discovers nothing is indistinguishable from one that works: `sys.path` grows silently or it
does not, and only a failure elsewhere reveals which. This is the pattern that has bitten the guard suite
repeatedly in this workspace — an `Assert.All` over an empty collection passes, and a scan of a tree with
nothing in it is vacuous, not green — so the anchor is asserted here instead of assumed.

Three things are checked, and each can fail on its own:

  1. AT LEAST ONE nested package declares a pythonpath. Without this the whole file could be deleted and
     the suite would collect exactly as before, which is the state this file was written to end.
  2. Every declared path is ON `sys.path` at collection time, so a broken discovery loop fails here rather
     than as a `ModuleNotFoundError` in somebody else's test.
  3. The seven ip-censor test modules are actually importable, which is the concrete regression: they were
     seven collection errors from the root and 12 real failures when run from their own directory.

The count of declaring packages is a reading, not a constant — it is asserted as "more than none", and the
number is printed, because a literal here would fail the day a package is added and the fix would be to
edit the number.
"""

from __future__ import annotations

import importlib.util
import sys

import pytest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from conftest import (  # noqa: E402  - the module under test, path set up above
    ROOT as CONFTEST_ROOT,
    WORKSPACE_SCOPED_TESTS,
    declared_pythonpaths,
    ignored_test_files,
    workspace_available,
)

# The conftest's own ROOT is the single definition of "where is gk-core". Recomputing it here once put this
# file two levels up, on gk-core/tests, which made all fifteen declared paths look absent — the "declared
# path does not exist" failure firing on every entry at once, which is the signature of a wrong base rather
# than of fifteen typos. Asserting the two agree turns that class into one clear failure.
assert ROOT == CONFTEST_ROOT, f"test root {ROOT} disagrees with conftest root {CONFTEST_ROOT}"


def test_a_nested_package_declares_a_pythonpath() -> None:
    """The anchor: at least one package asks for this, or this file is dead weight."""
    found = declared_pythonpaths()
    assert found, (
        "no nested pyproject.toml declares [tool.pytest.ini_options] pythonpath, so the root conftest "
        "does nothing. Either the nested packages lost their declaration or they no longer need one; "
        "decide which and delete this conftest if the second."
    )
    print(f"\n  nested packages declaring pythonpath: {len(found)}")


def test_every_declared_path_is_on_sys_path() -> None:
    """Discovery and application are separate steps; a broken second step must fail here."""
    for path in declared_pythonpaths():
        assert str(path) in sys.path, f"declared but not applied: {path}"


def test_the_ipcensor_tests_are_importable_from_the_root() -> None:
    """The concrete regression this file exists to end: seven collection errors."""
    for module in (
        "ipcensor.census",
        "ipcensor.registry",
        "ipcensor.source",
        "ipcensor.curate",
    ):
        spec = importlib.util.find_spec(module)
        assert spec is not None and spec.origin, (
            f"{module} is not importable when pytest runs from the gk-core root, so its test files "
            f"cannot be collected. tools/ip-censor/pyproject.toml declares pythonpath = [\".\"]."
        )


# --- the ADDITION 9(a) skip, and the two properties that keep it honest ---------------------------


def test_nothing_is_ignored_when_a_workspace_is_present() -> None:
    """THE anti-vacuity anchor. In the normal case the ignore set must be empty.

    Without this, "fifteen files quietly not collected" would be indistinguishable from "fifteen tests
    passed", and a green suite could be produced by suppressing exactly the modules most likely to fail.
    This test runs in the full workspace, so it fails the moment the mechanism starts hiding anything.
    """
    if not workspace_available():
        pytest.skip("no Keepverse workspace above this clone; the ignore set is the point here")
    assert ignored_test_files() == [], (
        f"a workspace IS present, so nothing may be ignored, but these are: {ignored_test_files()}"
    )


def test_every_workspace_scoped_test_declared_exists() -> None:
    """A declared path that does not exist is a typo, not a skip — and typos hide nothing loudly.

    This is the other direction the list must fail in. It deliberately UNDER-approximates: a new
    workspace-scoped test is absent from the list and so fails loudly at collection, which is the safe
    outcome. A stale entry here is the unsafe one, because it looks like coverage that does not exist.
    """
    missing = [rel for rel in WORKSPACE_SCOPED_TESTS if not (ROOT / rel).is_file()]
    assert not missing, f"declared workspace-scoped tests that do not exist: {missing}"


def test_the_skip_only_engages_without_a_workspace() -> None:
    """The mechanism is all-or-nothing on the probe, never per-file opportunistic skipping."""
    if workspace_available():
        assert ignored_test_files() == []
    else:
        # Everything declared that exists must be ignored, or a standalone clone still aborts on
        # whichever module happened to be missed.
        assert sorted(ignored_test_files()) == sorted(
            rel for rel in WORKSPACE_SCOPED_TESTS if (ROOT / rel).is_file()
        )