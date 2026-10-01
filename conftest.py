"""Make `pytest` from the gk-core ROOT able to collect the nested Python packages.

Measured problem: running `python -m pytest .` at gk-core's root aborted with EIGHT collection errors
before executing a single test. Seven were `ModuleNotFoundError: No module named 'ipcensor'` — the seven
tests under `tools/ip-censor/tests/` import the `ipcensor` package that sits beside them.

The seventh package is not the defect; the ABSENCE of a root configuration is. `tools/ip-censor/pyproject.toml`
already declares the fix for itself:

    [tool.pytest.ini_options]
    testpaths = ["tests"]
    pythonpath = ["."]

That file is only read when pytest's rootdir is `tools/ip-censor`. Run from gk-core's root — which has no
`pytest.ini`, `pyproject.toml`, `tox.ini`, `setup.cfg` or `conftest.py` at all — the declaration is never
read, `pythonpath` never applies, and seven test files that pass 25/26 in their own directory cannot be
collected from where a developer actually stands.

So this conftest applies what each nested package already declares, and it DISCOVERS them rather than
listing them. A hardcoded path would be a frozen prefix list, which is the wrong shape for this workspace:
the set of nested packages changes as the split settles, and a list only ever goes stale — silently,
which is the failure mode this file exists to remove. Reading every nested `pyproject.toml` means a package
added tomorrow is covered without editing anything here.

Nothing is imported and nothing is asserted by this file. It only extends `sys.path`, which is strictly
additive: a package that already resolved keeps resolving identically.
"""

from __future__ import annotations

import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parent

#: Directories that are never worth descending into when looking for nested packages.
_SKIP = frozenset({".git", "node_modules", "obj", "bin", "dist", "__pycache__", ".claude", ".agents"})


def declared_pythonpaths(root: Path | None = None) -> list[Path]:
    """Every directory a nested `pyproject.toml` asks pytest to put on `sys.path`.

    Returns resolved directories that actually exist, sorted and deduplicated, so a caller can see what
    was honoured. A `pyproject.toml` that does not parse, or that declares nothing, is skipped rather than
    fatal: this runs before collection, and raising here would replace eight collection errors with one
    collection error.
    """
    base = (root or ROOT).resolve()
    found: set[Path] = set()
    for pyproject in sorted(base.rglob("pyproject.toml")):
        if _SKIP.intersection(pyproject.parts):
            continue
        try:
            data = tomllib.loads(pyproject.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        declared = (
            data.get("tool", {}).get("pytest", {}).get("ini_options", {}).get("pythonpath", []) or []
        )
        for entry in declared:
            candidate = (pyproject.parent / entry).resolve()
            if candidate.is_dir():
                found.add(candidate)
    return sorted(found)


for _path in declared_pythonpaths():
    _text = str(_path)
    if _text not in sys.path:
        sys.path.insert(0, _text)