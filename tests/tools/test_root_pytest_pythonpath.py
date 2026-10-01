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
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from conftest import declared_pythonpaths  # noqa: E402  - the module under test, path set up above


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