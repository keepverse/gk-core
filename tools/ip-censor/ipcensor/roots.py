"""Where the shipped registry lives, now that no repository carries it at its own root.

THE SPLIT MOVED THE DATA TREE. The authored registry this tool scans against is
`data/seed/ip-censor/_registry/`, and after the Keepverse split that path belongs to a gk-data PACK
(`gk-data/packs/fusion/data/seed/ip-censor/_registry/`), not to the repository the tool lives in.
`root / "data/seed/..."` - `root` being the git toplevel - therefore named a path in a repository that
does not have it, and the tool refused with `marks.v1.json: cannot be read` while the file sat
present two directories away. Both CI steps throw on that: the tool's own suite and the advisory scan.

THE RESOLVER IS SHARED, NOT RE-DERIVED. gk-core already owns one
(`scripts/lib/keepverse_roots.py`, `owned_path(rel, start)`), held byte-identical to its copies in
gk-fusion and gk-forge by `ResolverCopyParityTests`. Fourteen `gk-core/scripts/**` tools reach it the
same way, and this module copies that idiom verbatim rather than inventing a sibling lookup that would
drift from it on the next split:

    sys.path.insert(0, str(<core>/scripts/lib))
    from keepverse_roots import owned_path  # noqa: E402  (the insert above first)

`ip-censor` is a standalone tool under `tools/` with its own lockfile, so `seedsmith` is not
importable and `gk-core/scripts/lib` is not on `sys.path` by default. `gk-core`'s copy is the one to
import - it is a FILE IN THIS REPOSITORY two levels up, not a sibling repository, which is why this
import is unconditional: every accessor's ABSENCE is already fail-closed (`owned_path` falls back to
the caller's spelling of the path, and `seed_root` returns a path that does not exist), so there is no
refusal for an optional import to soften. The `guard-verification-boundaries.py` optional-import dance
exists because guard tests plant COPIES of that guard into temporary directories; a fixture copy of
this tool is not a supported layout, and a planted copy would rather fail loudly at import than
resolve every path against its own root.

TWO FORMS, BECAUSE A DIRECTORY IS NOT A FILE. `owned_path` answers "which repository carries this
file?" by testing `(base / rel).exists()`, so it needs a complete file path - `owned_path("data/seed")`
is exactly the directory question it cannot answer, and its own docstring says so. `registry_dir` asks
it about a FILE inside the directory and takes the parent, which is sound because the registry
directory always carries `MARKS_FILE`.

WHY NOT `seed_root`. `seed_root()` is the pack's `data/seed` and is the right answer for content the
tool merely READS, but it would break the fixture contract: `tests/test_report.py` and
`tests/test_wiring.py` plant a registry inside a throwaway git repo and pass it as `--root`, precisely
because the shipped pack is outside the lane's allowed paths. `owned_*` puts `start` - the `--root` -
FIRST, so a root that genuinely carries the registry still wins, and only a root that does not falls
through to the pack. That ordering is what keeps `tests/test_wiring.py::test_the_module_entry_point_reports_a_missing_registry`
refusing instead of silently reading the real registry.
"""

from __future__ import annotations

import sys
from pathlib import Path

from ipcensor.registry import MARKS_FILE

# `ipcensor/roots.py` -> `ip-censor` -> `tools` -> `gk-core`, so the resolver's own `scripts/lib` is
# three levels above this file.
TOOL_ROOT = Path(__file__).resolve().parents[1]
CORE_ROOT = Path(__file__).resolve().parents[3]

sys.path.insert(0, str(CORE_ROOT / "scripts" / "lib"))
from keepverse_roots import (  # noqa: E402  (the insert above must run first)
    RootNotFound,
    owned_path,
    owning_base,
    seed_root,
)

__all__ = ("CORE_ROOT", "RootNotFound", "TOOL_ROOT", "owned", "owned_dir", "owning_base", "seed_root")


def _start(root: Path | str | None) -> Path:
    """Where the resolver's upward walk begins: the caller's `--root`, else the tool's own directory.

    The tool's own directory is the default rather than the process working directory because CI
    invokes the advisory scan from the repository root while the suite invokes it from
    `tools/ip-censor`; anchoring to the tool makes resolution independent of the caller, which is the
    same reason `report` never reads a relative path from the process working directory.
    """
    return Path(root) if root is not None else TOOL_ROOT


def owned(rel: str, root: Path | str | None = None) -> Path:
    """`rel` - repository-relative, forward slashes - resolved against the repository that carries it.

    FAIL CLOSED, which is the contract and not an accident of the fallback: a path no repository
    carries comes back spelled exactly as the caller wrote it, so `load_registry` raises its named
    `RegistryError` on a MISSING FILE rather than the tool reporting an empty registry, a clean scan,
    or zero findings. A registry that is absent must be a refusal with a non-zero exit, because a
    silent empty result is indistinguishable from a passing gate.
    """
    return owned_path(rel, _start(root))


def owned_dir(rel: str, root: Path | str | None = None) -> Path:
    """The directory `rel` names, resolved through a FILE inside it rather than through itself.

    `rel` is a directory, so it cannot be asked of `owned_path` directly; `MARKS_FILE` is the member
    that makes the question answerable, and every registry directory the tool reads carries it.
    """
    return owned(f"{rel.rstrip('/')}/{MARKS_FILE}", root).parent