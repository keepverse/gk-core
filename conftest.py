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

# `scripts/lib` is where the workspace's shared resolver lives (`keepverse_roots`), and nothing declares
# it as a pythonpath because it is not a package with tests of its own — it is the shared-script library the
# tooling imports. Adding it here is what lets `workspace_available()` below ask the ONE implementation of
# "is this inside a workspace" rather than restating its markers and drifting from them.
_SHARED_LIB = ROOT / "scripts" / "lib"
if _SHARED_LIB.is_dir() and str(_SHARED_LIB) not in sys.path:
    sys.path.insert(0, str(_SHARED_LIB))


# ---------------------------------------------------------------------------
# ADDITION 9(a): gk-core's tests must RUN with every private sibling absent.
# ---------------------------------------------------------------------------
#
# MEASURED, before this section existed: a standalone `git clone` of gk-core with no siblings could not
# COLLECT fifteen modules under tests/tools/, all failing with
# `keepverse_roots.RootNotFound: no legacy repo or Keepverse workspace above <path>`. A collection error
# aborts what follows it, so a developer in a clean clone got no result at all.
#
# Those fifteen are the tests of WORKSPACE-WIDE tools — the guard runner, program_status, the
# session-boundary checker, the append-only resolver, the live-slot and live-probe helpers. Their subjects
# resolve a Keepverse workspace at import time, and the resolver raises rather than guessing a root, which
# is correct behaviour for a tool and wrong for a test run.
#
# So the modules are IGNORED, never silently, and only when there is genuinely no workspace. Two properties
# keep this honest, and both are asserted rather than assumed:
#
#   1. When a workspace IS present the ignore set is EMPTY. The mechanism cannot hide a single test in the
#      normal case, so it can never make a failure disappear where it matters.
#   2. The set UNDER-approximates on purpose. A workspace-scoped test added later is not in this list, so it
#      fails loudly at collection instead of vanishing — the safe direction for a list to fail in.
#
# That is the opposite of the failure this workspace has hit repeatedly: an `Assert.All` over an empty
# collection passes, and a scan of an empty tree reads as green. Fifteen uncollected files that look like
# fifteen passing tests is the same defect wearing a different hat.
WORKSPACE_SCOPED_TESTS: tuple[str, ...] = (
    "tests/tools/test_audit_program_pipeline.py",
    "tests/tools/test_bcu212_full_run.py",
    "tests/tools/test_f13_schema_upgrade_proof.py",
    "tests/tools/test_guard_funnel_delta.py",
    "tests/tools/test_guard_game_profile.py",
    "tests/tools/test_guard_injector_compile.py",
    "tests/tools/test_guard_single_writer.py",
    "tests/tools/test_lawn_combat_observer.py",
    "tests/tools/test_live_slot.py",
    "tests/tools/test_program_status.py",
    "tests/tools/test_prove_actor_hud_live.py",
    "tests/tools/test_prove_hub_combat.py",
    "tests/tools/test_reemit_colliding_item_names.py",
    "tests/tools/test_session_boundary_check.py",
    "tests/tools/test_union_append_only.py",
)


def _workspace_markers_present() -> bool:
    """Structural fallback for the workspace probe, mirroring the resolver's own markers.

    This exists for one reason: the probe must never answer "no workspace" merely because an IMPORT failed.
    That direction is the dangerous one — it silently ignores fifteen tests in a full workspace, which is
    precisely the defect this file was written to remove. So if `keepverse_roots` cannot be imported at all,
    the question is answered from the filesystem instead of from the exception.

    The markers are the resolver's own (keepverse_roots._layout): a workspace is a directory holding both
    `gk-core` and `gk-data`; a pre-split legacy checkout is one holding `FusionRpg.slnx` alongside both
    `data/seed` and `data/tuning`. Kept identical deliberately — a second, divergent definition of "where
    am I" is how a probe starts lying.
    """
    for directory in (ROOT, *ROOT.parents):
        if (directory / "gk-core").is_dir() and (directory / "gk-data").is_dir():
            return True
        if ((directory / "FusionRpg.slnx").is_file()
                and (directory / "data" / "seed").is_dir()
                and (directory / "data" / "tuning").is_dir()):
            return True
    return False


def workspace_available() -> bool:
    """Is there a Keepverse workspace above this repository? Asked of the one implementation that knows.

    `keepverse_roots` fails closed by design — it raises `RootNotFound` rather than guessing a root — so the
    question has to be asked inside a `try`. A failure to import is NOT treated as "no workspace": that
    answer would ignore fifteen tests in the full workspace, so it falls through to the structural probe
    instead. Both paths ask the same question and only the structural one can be wrong by omission.
    """
    try:
        from keepverse_roots import workspace_root  # noqa: PLC0415 - deliberately late

        workspace_root(ROOT)
        return True
    except ImportError:
        return _workspace_markers_present()
    except Exception:                                        # noqa: BLE001 - RootNotFound and friends
        return False


def ignored_test_files() -> list[str]:
    """The workspace-scoped modules to skip, or an empty list when none need skipping."""
    if workspace_available():
        return []
    return [rel for rel in WORKSPACE_SCOPED_TESTS if (ROOT / rel).is_file()]


collect_ignore = ignored_test_files()