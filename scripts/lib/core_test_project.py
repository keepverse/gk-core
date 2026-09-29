r"""Which test project holds a given piece of Core -- ONE implementation, shared by `coverage` and `mutate`.

WHY THIS IS A MODULE AND NOT A FUNCTION IN EITHER TOOL
`coverage.ps1` and `mutate.ps1` each carried their own copy of the same folder-token -> project lookup
against `gk-core/tests/core-test-projects.v1.json`, and the copies had already begun to differ: `coverage` read
the token out of a NAMESPACE, `mutate` out of a FILE PATH, so the two agreed on the manifest rules and
disagreed on only the first line of the function. Two copies of a rule that decides WHICH TESTS RUN is
the shape that eventually makes a namespace's coverage a number measured against the wrong project.
So the rule lives here once, and each tool passes in the token it derived.

THE RULES, AND THE TWO THAT WERE SUBTLE
1. Only `FusionRpg.Core.*` is covered by the manifest. Anything else keeps the historical fallback --
   the residual -- which is what an un-split repository always did. A namespace outside Core is not an
   error; it simply has no manifest entry to find.
2. The token is the FIRST SEGMENT after the `FusionRpg.Core.` prefix, split on the separator that fits
   the caller's input: a dot for a namespace, a slash for a source path. `FusionRpg.Core.World.Ai`
   yields `World`, and `FusionRpg.Core/World/Topology.cs` yields `World` -- the two calls agree.
3. **PowerShell's `-contains` is CASE-INSENSITIVE equality, and that was load-bearing.** A namespace
   typed as `FusionRpg.Core.world` must find the `World` project. A port that used Python's `in` on a
   list would silently fall through to the residual and report a real number for the wrong project,
   which reads as plausible rather than wrong. So the comparison folds case on both sides.
4. **First match wins, in manifest order**, not the most specific match and not the alphabetically first
   project. That is what `break` did, and a "better" tie-break here would silently re-point a namespace's
   tests at a different project the day two projects both claim one folder.

WHY THE POWERSHELL ORIGINALS ARE GONE
Neither tool is being kept for a fallback path. The shared rules live in one Python module because two
maintained copies is the defect, not because the originals were hard to read.
"""

from __future__ import annotations

import json
from pathlib import Path

CORE_STEM = "FusionRpg.Core"
CORE_PREFIX = CORE_STEM + "."
# The repository-relative source root, stripped before the stem is compared. A mutant set stores paths
# like `gk-core/src/FusionRpg.Core/World/Ai/Hops.cs`, so without this the token would be the whole
# `gk-core/src/FusionRpg.Core/World` string and would match nothing in the manifest.
SRC_PREFIX = "src/"
MANIFEST_RELATIVE = "tests/core-test-projects.v1.json"
# What an un-split repository always did, and what a non-Core namespace still gets.
CORE_FALLBACK_PROJECT = "tests/FusionRpg.Core.Tests"


def namespace_token(namespace: str) -> str | None:
    """The manifest folder token for a `FusionRpg.Core.*` NAMESPACE, or None if it is not Core's.

    None means "not in the manifest", which is a legitimate answer and not a failure: a namespace
    outside Core keeps the residual fallback.
    """
    if not namespace.startswith(CORE_PREFIX):
        return None
    remainder = namespace[len(CORE_PREFIX):]
    if not remainder:
        # `FusionRpg.Core.` exactly: the prefix with nothing after it names no folder, and returning ""
        # would match the manifest's `/**` entries for every project.
        return None
    return remainder.split(".")[0] or None


def source_path_token(source_path: str) -> str | None:
    r"""The manifest folder token for a Core SOURCE PATH, or None if it is not Core's.

    ACCEPTS EVERY SHAPE A CALLER ACTUALLY HOLDS, which is a widening rather than a transcription:

    * `src/FusionRpg.Core/World/Topology.cs` -- the REPOSITORY-RELATIVE form, which is what a mutant set
      actually stores in its `file` field, and what `mutate` passed here.
    * `FusionRpg.Core/World/Topology.cs` -- the stem-relative form, with no `src/`.
    * `FusionRpg.Core.World.Ai.Navigator` -- the NAMESPACE-shaped form.
    * Any of the above with `\` separators.

    The original `mutate.ps1` handled only `gk-core/src/FusionRpg.Core/` (its own prefix) and the original
    `coverage.ps1` only `FusionRpg.Core.` (a namespace). Each therefore fell through to the residual for
    the other's shape, and a mutation score ended up measured against a different project than the same
    folder's coverage. One function that accepts all four is the point of sharing it.

    The stem comparison stays ORDINAL, because `World` and `world` are different folders and the manifest
    can list both. The case FOLDING lives one level down, in the comparison against the manifest's own
    `include` entries -- so a differently-cased folder still finds its project, and a differently-cased
    STEM is not Core's.
    """
    normalised = source_path.replace("\\", "/")
    if normalised.startswith(SRC_PREFIX):
        normalised = normalised[len(SRC_PREFIX):]
    for stem, separator in ((CORE_STEM + "/", "/"), (CORE_PREFIX, ".")):
        if normalised.startswith(stem):
            remainder = normalised[len(stem):]
            if not remainder:
                return None
            return remainder.split(separator)[0] or None
    return None


def project_for_token(repo: Path, token: str | None) -> str:
    """The repo-relative test project directory for a folder token.

    FALLS BACK RATHER THAN REFUSING, and that is deliberate: the original fell back, and a coverage
    report that refuses because a manifest entry is missing is less useful than one that names the
    project it actually used. So the returned path is always real, and callers that need to know whether
    the manifest was consulted should ask `manifest_matched` rather than infer it from this value.
    """
    if not token:
        return CORE_FALLBACK_PROJECT
    manifest_path = repo / MANIFEST_RELATIVE
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # A missing or unreadable manifest is the pre-split shape, not an error.
        return CORE_FALLBACK_PROJECT
    wanted = (f"{token}/**", f"{token}.cs")
    for project in manifest.get("projects") or []:
        include = project.get("include") or []
        if any(entry.casefold() == candidate.casefold() for entry in include for candidate in wanted):
            name = project.get("name")
            if name:
                return f"tests/{name}"
    residual = manifest.get("residual")
    return f"tests/{residual}" if residual else CORE_FALLBACK_PROJECT


def manifest_matched(repo: Path, token: str | None) -> bool:
    """Whether the manifest, rather than the fallback, decided `project_for_token`'s answer.

    Exposed so a caller can REPORT which project it used and whether the manifest was consulted, which
    is the difference between "this namespace has 40% coverage" and "this namespace has 40% coverage in
    the project the manifest points at".
    """
    if not token:
        return False
    try:
        manifest = json.loads((repo / MANIFEST_RELATIVE).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    wanted = (f"{token}/**", f"{token}.cs")
    return any(any(entry.casefold() == candidate.casefold() for candidate in wanted)
               for project in manifest.get("projects") or []
               for entry in (project.get("include") or []))
