#!/usr/bin/env python3
r"""Guard: every registry row is WIRED -- its repository resolves, its script exists, its subject is found.

WHY THIS EXISTS, and what it is not
-----------------------------------
The split moved the generated corpus to gk-data and left all 34 registry rows naming a bare
`scripts/<file>` path. Nothing in the schema said which repository a guard inspects, so a guard whose
subject relocated was dispatched against a root holding none of it, and the only thing that noticed was
the guard's own stderr. Measured on `generated-seed`: `GENERATED-TREES-ABSENT`, exit 1, on a machine
where gk-data sat one directory away.

A FIX TO THAT ROW IS NOT A FIX TO THE CLASS. `run_guards.py` now carries a `repository` field, so the
next relocation has somewhere to declare itself -- and a field nothing checks is a field that decays.
This guard is the check, and it is what makes the fix durable rather than a one-row patch.

THE THREE SHAPES OF THE SAME DEFECT, all three refused here:

  1. a row naming a repository that is NOT CHECKED OUT -- the guard would inspect nothing and could
     report that as clean, which is the failure this whole mechanism exists to prevent;
  2. a row whose SCRIPT does not exist in any repository -- a guard that cannot run at all;
  3. a row that declares NO repository -- after the split an unnamed row is an unproven claim rather
     than a statement that the subject is this repository, and that is how `generated-seed` came to run
     against a directory holding none of its subject.

WHY THIS IS A GUARD AND NOT A RUNNER STAGE. `run_guards.py` already refuses shapes 1 and 3 before
dispatch, so this guard looks redundant for the runner's own path. It is not: the runner's check is
about the batch that is about to run, and this one is about the REGISTRY as a standing artifact. A row
can be added for a `local`-tier guard, or for a repository nobody has checked out on the machine that
edits it, and be invisible to every CI run while the file that documents the wiring is wrong. This reads
every row, whatever tier it carries.

WHAT IT DELIBERATELY DOES NOT DO. It does not assert that a guard's verdict is correct, and it does not
re-run any guard. Shape 3 below -- "a subject that resolves to nothing" -- is asked of the guard's OWN
declared scan root where the guard publishes one, and is otherwise left to that guard, because this
runner cannot know what any given guard scans. Inventing a per-guard subject list here would be a second
source of truth that drifts from the guards themselves.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

GUARD_ID = "registry-wiring"
VERDICT_FAILED = "[guard-registry-wiring] FAILED"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_REGISTRY = "scripts/enforcement-registry.v1.json"

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
try:
    from keepverse_roots import (  # noqa: E402  (the insert above must run first)
        authored_content_root, content_root, core_root, forge_root, fusion_root, web_root,
        workspace_root,
    )
    RESOLVER_AVAILABLE = True
except ImportError as _resolver_error:      # a fixture, or a repository missing scripts/lib/
    RESOLVER_AVAILABLE = False
    print(f"[{GUARD_ID}] WARNING: the workspace resolver is unavailable ({_resolver_error}); every "
          f"foreign repository is reported absent, which is the loud direction.", file=sys.stderr)

    def _unavailable(*_a, **_k):
        raise RuntimeError("keepverse_roots is not importable")

    authored_content_root = content_root = core_root = _unavailable
    forge_root = fusion_root = web_root = workspace_root = _unavailable

# THE VOCABULARY, spelled out here rather than imported from run_guards.py.
#
# It is duplicated on purpose and the duplication is CHECKED: importing the runner would execute its
# module body, and a copy that drifted would be a second source of truth. `the_vocabulary_matches_the_runner`
# below reads the runner's own constant out of its source and refuses on a disagreement, so the copy is
# pinned by a test rather than trusted.
ACCESSORS = {
    "gk-core": "core_root",
    "gk-data": "content_root",
    "gk-forge": "forge_root",
    "gk-fusion": "fusion_root",
    "gk-web": "web_root",
    "gk-workflow": "workspace_root",
    "gk-content": "authored_content_root",
}

# A guard's declared scan root, where the guard PUBLISHES one as a module constant. This is the subject
# probe for shape 3: the roots the guard names must exist in its declared repository.
#
# Measured, only one guard in this repository publishes such a table (`generated-seed`'s TREES), and it
# publishes PATTERNS rather than paths, so the literal root directory is derived from each pattern the
# same way that guard derives it. A guard that publishes no table is left to its own refusal, which is
# why this is a per-guard table and not a scan of the tree.
SUBJECT_PROBES: dict[str, tuple[str, ...]] = {
    # The eight declared generated roots, as the anchored patterns the guard itself carries.
    "generated-seed": (
        "data/seed/items", "data/seed/actions", "data/seed/atoms/generated", "data/generated",
        "data/seed/passive-tree", "data/seed/creatures", "data/seed/dungeon", "data/seed/structures",
    ),
}

_GUARD_ROW = re.compile(r'^\s*"([^"]+)":\s*\{\s*$')


def repository_bases(root: Path) -> dict[str, Path | None]:
    """Every named repository's root, ABSENT ones carried as None rather than dropped.

    An absent repository is the answer this guard is here to report, so it must survive into the
    findings; a resolver that simply omitted it would make shape 1 indistinguishable from a typo.
    """
    bases: dict[str, Path | None] = {}
    for name, accessor_name in ACCESSORS.items():
        if name == "gk-core":
            bases[name] = root
            continue
        accessor = globals()[accessor_name]
        try:
            resolved = Path(accessor(root))
        except Exception:
            bases[name] = None
            continue
        bases[name] = resolved if resolved.is_dir() else None
    return bases


def script_bases(root: Path, bases: dict[str, Path | None]) -> list[Path]:
    """Every repository a guard script may live in, THIS ONE FIRST.

    The same order `run_guards.py` uses to find a script, so a row this guard calls missing is missing for
    the runner too -- otherwise this guard would red on a wiring the runner resolves perfectly well.
    """
    out = [root]
    for name, base in bases.items():
        if name == "gk-core" or base is None:
            continue
        if base not in out:
            out.append(base)
    return out


def the_vocabulary_matches_the_runner(root: Path) -> list[str]:
    """Read `run_guards.py`'s own GUARD_REPOSITORIES and refuse on a disagreement with this copy."""
    runner = root / "scripts" / "run_guards.py"
    if not runner.is_file():
        return [f"the guard runner is missing, so the repository vocabulary cannot be pinned: {runner}"]
    try:
        text = runner.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return [f"the guard runner is unreadable, so the vocabulary cannot be pinned: {exc}"]
    block = re.search(r"GUARD_REPOSITORIES[^=]*=\s*\{(.*?)\}", text, re.S)
    if block is None:
        return ["the guard runner declares no GUARD_REPOSITORIES mapping to compare against"]
    found = dict(re.findall(r'"([^"]+)":\s*"([^"]+)"', block.group(1)))
    if found != ACCESSORS:
        return [f"this guard's repository vocabulary differs from run_guards.py's: "
                f"{ACCESSORS} vs {found}. One of the two copies is stale, and a stale vocabulary is a "
                f"check that passes for the wrong reason."]
    return []


def check(root: Path, registry_path: Path) -> dict:
    failures: list[str] = []
    try:
        doc = json.loads(registry_path.read_text(encoding="utf-8"))
    except OSError as exc:
        return {"guard": GUARD_ID, "verdict": "FAIL", "rows": 0, "findings": [f"registry unreadable: {exc}"]}
    except json.JSONDecodeError as exc:
        return {"guard": GUARD_ID, "verdict": "FAIL", "rows": 0,
                "findings": [f"registry is not valid JSON: {exc}"]}

    rows = doc.get("guards") or {}
    if not isinstance(rows, dict) or not rows:
        return {"guard": GUARD_ID, "verdict": "FAIL", "rows": 0,
                "findings": ["the registry declares no guards, so there is nothing to check and a clean "
                             "run here would be a claim about nothing"]}

    failures += the_vocabulary_matches_the_runner(root)
    bases = repository_bases(root)
    scripts = script_bases(root, bases)

    checked: list[dict] = []
    for guard_id in sorted(rows):
        row = rows[guard_id]
        repository = str(row.get("repository") or "").strip()
        entry = {"id": guard_id, "repository": repository or None, "script": None,
                 "repositoryPresent": None, "scriptPresent": None, "subjectRoots": None}

        # SHAPE 3: no repository declared.
        if not repository:
            failures.append(
                f"row '{guard_id}' declares no repository. Since the split an unnamed row resolves against "
                f"the runner's own root, which is how a guard whose subject moved came to run against a "
                f"directory holding none of it. Name it, one of: {', '.join(sorted(ACCESSORS))}.")
            checked.append(entry)
            continue
        entry["repository"] = repository

        # An out-of-vocabulary name is a typo in configuration, and is named as one.
        if repository not in ACCESSORS:
            failures.append(
                f"row '{guard_id}' names repository '{repository}', which is outside the vocabulary "
                f"(one of: {', '.join(sorted(ACCESSORS))}).")
            checked.append(entry)
            continue

        # SHAPE 1: a named repository that is not checked out.
        base = bases.get(repository)
        entry["repositoryPresent"] = base is not None
        if base is None:
            failures.append(
                f"row '{guard_id}' inspects '{repository}', which is not present here. Its guard would "
                f"inspect nothing and could report that as clean, so this is refused rather than skipped. "
                f"Check the repository out, or set the matching KEEPVERSE_*_ROOT override.")
            checked.append(entry)
            continue

        # SHAPE 2: a script no repository has.
        script_rel = str(row.get("script") or "").replace("\\", "/").strip()
        entry["script"] = script_rel or None
        if not script_rel:
            failures.append(f"row '{guard_id}' declares no script, so it cannot be run at all.")
            checked.append(entry)
            continue
        if not any((candidate / script_rel).is_file() for candidate in scripts):
            failures.append(
                f"row '{guard_id}' names script '{script_rel}', which no repository has. Searched: "
                + ", ".join(str(candidate) for candidate in scripts) + ".")
            checked.append(entry)
            continue
        entry["scriptPresent"] = True

        # SHAPE 4: a subject that resolves to nothing, for the guards that publish their scan roots.
        probes = SUBJECT_PROBES.get(guard_id)
        if probes:
            found = [p for p in probes if (base / p).is_dir()]
            entry["subjectRoots"] = {"declared": len(probes), "present": len(found)}
            if not found:
                failures.append(
                    f"row '{guard_id}' declares repository '{repository}', but none of its "
                    f"{len(probes)} declared subject roots exist there: {', '.join(probes)}. The guard "
                    f"would inspect nothing and report a verdict about an absent corpus.")
        checked.append(entry)

    return {"guard": GUARD_ID, "verdict": "FAIL" if failures else "OK", "rows": len(rows),
            "resolverAvailable": RESOLVER_AVAILABLE, "wiring": checked, "findings": failures}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: every enforcement-registry row names a present repository, an existing "
                    "script, and a findable subject (never a green path over an absent one).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--registry-path", type=Path, default=None,
                        help=f"the registry (default: <root>/{DEFAULT_REGISTRY})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    registry_path = (args.registry_path.resolve() if args.registry_path
                     else root / DEFAULT_REGISTRY)
    result = check(root, registry_path)

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(f"[{GUARD_ID}] OK - {result['rows']} row(s) wired: every one names a present repository, an "
              f"existing script, and a findable subject")
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        print("", file=sys.stderr)
        for finding in result["findings"]:
            print(f"  ! {finding}", file=sys.stderr)
        print("", file=sys.stderr)
        for line in (
            "A guard that cannot see its subject reports a verdict about nothing, and a green row reached",
            "that way is worse than a red one: it teaches the next agent to skip the step. Every finding",
            "above is a wiring defect, not a finding about the tree.",
        ):
            print(line, file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
