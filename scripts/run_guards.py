#!/usr/bin/env python3
"""One runner for the enforcement registry's guards (solid-enforcement `guard-runner`).

Reads `gk-core/scripts/enforcement-registry.v1.json` and runs the guards it selects. This is the ONE place that
decides which guard runs where: `ci.yml`, `release.yml`, `nightly.yml`, `deploy-play.py` and
`verify-change.py` all call it, so wiring a guard is a registry edit rather than three hand-kept lists --
the mechanism that once left nine guards unwired.

Replaces `run-guards.ps1`.

EVERY SELECTED GUARD RUNS EVEN AFTER ONE FAILS, and a summary table prints before a non-zero exit: one
red guard never hides a second. That is the masking defect `ci.yml`'s old dotnet-test step met once
already, and it is the reason this tool is a LOOP and not a fail-fast chain.

WHY THE POWERSHELL FORM WAS RETIRED
-------------------------------------
* **THERE WAS NO TIMEOUT ON ANY GUARD INVOCATION, AND THE RUNNER IS THE LONGEST THING IN CI.** A guard
  that wedges -- a restore waiting on a locked feed, a subprocess that never returns -- held the whole
  run open indefinitely, and the only signal was the absence of the next table row. Every invocation is
  bounded by `--timeout` now, and a timeout is a named refusal that NAMES THE GUARD, because a table
  row reading "timed out" is not evidence about the guard it names.

* **THE STDERR LOG WAS DELETED WITH `-ErrorAction SilentlyContinue`.** A per-guard temp file, removed on a
  failure the runner is not allowed to see -- so a locked or read-only temp directory leaked one file per
  guard per run, silently and forever. This repository's own testing standard (R3) says a failed temp
  delete is a FAILURE precisely because of a 65.5 GB incident that came from exactly this shape. The
  port removes the log and reports a failure to remove it.

* **THE INTERPRETER WARNING CHECKED THE WRONG NAME AND PRODUCED A PHANTOM.** It probed for
  `powershell` while the dispatcher preferred `pwsh` and fell back to `powershell`. On any machine with
  only PowerShell 7 -- the normal case on Linux and macOS, and increasingly on Windows -- the warning
  said "PATH has no 'powershell'" for a runner that was about to work fine. A warning that cries wolf on
  the correct platform trains its reader to skip it, and the warning exists precisely to stop a phantom
  red from being read as a verdict.

* **THE SUMMARY WAS A `Format-Table` RENDERED INTO A STRING.** There was no machine-readable output at
  all, so nothing could assert on a run: not the guard ids, not the exit codes, not which of them were
  red. `--json` now reports every guard, and `test_run_guards.py` reads THAT rather than parsing a
  table, so a fixture states its expectation from the tool's own surface.

* **THE `default` DISPATCH BRANCH WROTE ITS EXPLANATION TO A FILE IT THEN DELETED UNCONDITIONALLY** --
  the message explaining an undispatchable extension was discarded on the success path. The port puts it
  in the report and in the refusal, where it is read.

WHAT THIS TOOL MUST NOT DO
--------------------------
It must never run a guard the registry did not select, and it must never report a green run for an EMPTY
selection. Both were explicit refusals in the original and are refusals here by name.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import (  # noqa: E402  (the resolver lives beside this tool, not on sys.path)
    authored_content_root, content_root, core_root, forge_root, fusion_root, web_root,
    workspace_root,
)

TOOL_ID = "run-guards"
EXIT_OK = 0
EXIT_FAILED = 1
# The dispatcher's own vocabulary for "this guard's file cannot be run at all". Carried through unchanged
# so a caller that scripted the old 64 still sees 64 rather than a generic failure it cannot classify.
EXIT_UNDISPATCHABLE = 64

TIERS = ("ci", "local")
STATUSES = ("gating", "backlog")
REGISTRY_SCHEMA_VERSION = 1
DEFAULT_TIMEOUT = 900

# THE `repository` VOCABULARY — a row's `repository` names the repository whose ROOT that guard inspects.
#
# WHY THIS FIELD EXISTS, measured rather than inferred. The nine-repository split moved every generated
# tree (`data/seed/**`, `data/generated/**`) into gk-data's pack while leaving this registry naming bare
# `scripts/<file>` paths. `generated-seed` therefore ran with gk-core as its root, found all eight of its
# own declared trees absent, and refused — correctly, and uselessly, on a machine where the corpus sits
# one directory away. The refusal was honest; the WIRING that made it unavoidable was the defect.
#
# `repo` is not a new idea in this codebase: `verification-boundaries.v1.json` already carries a `repo`
# field with a closed vocabulary (see VALID_OWNING_REPOS in guard-verification-boundaries.py), and the
# SAME resolver answers both. This is that convention, applied where it was missing.
#
# `gk-core` is in the vocabulary and means THIS repository, spelled out rather than inferred, so a row
# can be explicit about its own root and the wiring check below can require the explicitness.
GUARD_REPOSITORIES: dict[str, str] = {
    "gk-core": "core_root",
    "gk-data": "content_root",
    "gk-forge": "forge_root",
    "gk-fusion": "fusion_root",
    "gk-web": "web_root",
    "gk-workflow": "workspace_root",
    "gk-content": "authored_content_root",
}

# The rows this field was added for, and WHY each one names the repository it does. A schema field with no
# stated owner is a field nobody fills in next time, and the failure it fixes recurs silently.
#
# `gk-data` is a REPOSITORY whose scan root is the PACK (`gk-data/packs/fusion`), not the repository root
# itself — `content_root()` is what answers that, and it is why the field resolves through the shared
# resolver rather than joining a directory name onto a sibling path.
REPOSITORY_OWNERS: dict[str, str] = {
    "generated-seed": "gk-data",
}
# No `summary` stage: `summarise` RETURNS a verdict and an exit code rather than raising, so it
# cannot refuse. A stage that cannot fail is not a stage, and listing one would invite a case that
# asserts against something unreachable.
STAGES = ("registry", "arguments", "range", "selection", "catalog", "interpreters", "dispatch")

# A range switch the registry may name, in either spelling, and which is DROPPED together with its
# `{ciRange}` placeholder when there is no range. Both spellings are listed because the catalog holds both
# a PowerShell guard and a Python one and the row carries whichever the guard understands; a one-spelling
# list would silently stop dropping on the other.
RANGE_SWITCHES = ("-Range", "-RequireExplicitRange", "--range", "--require-explicit-range")
CI_RANGE_PLACEHOLDER = "{ciRange}"


class Refusal(Exception):
    """A named precondition or stage failure. The run says WHICH stage, and never exits 0 having not run."""

    def __init__(self, stage: str, reason: str, detail: str = "", exit_code: int = EXIT_FAILED) -> None:
        super().__init__(f"{stage}/{reason}: {detail}" if detail else f"{stage}/{reason}")
        self.stage = stage
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


def read_registry(root: Path) -> dict:
    """The enforcement registry, validated.

    EVERY refusal here is one the original made, by name. A registry that is absent, unparseable, of the
    wrong schema, empty, or carrying a guard with no `script`/`tier`/`status` evidence, must STOP the run
    rather than select a subset: a catalog that has quietly lost a guard would otherwise report a green
    run over fewer guards than anyone believes are gating.
    """
    path = root / "scripts" / "enforcement-registry.v1.json"
    if not path.is_file():
        raise Refusal("registry", "REGISTRY-MISSING", f"enforcement registry missing: {path}")
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("registry", "REGISTRY-UNREADABLE", f"enforcement registry is not valid JSON: {path}: {exc}")
    if doc.get("schemaVersion") != REGISTRY_SCHEMA_VERSION:
        raise Refusal("registry", "REGISTRY-SCHEMA",
                      f"unsupported enforcement registry schemaVersion: {doc.get('schemaVersion')}")
    guards = doc.get("guards")
    if not isinstance(guards, dict) or not guards:
        raise Refusal("registry", "CATALOG-EMPTY",
                      "enforcement registry has no guards; no guards selected because the catalog is "
                      "empty (RED)")
    for name, row in guards.items():
        if not isinstance(row, dict) or any(
                not str(row.get(field) or "").strip() for field in ("script", "tier", "status")):
            raise Refusal("registry", "GUARD-EVIDENCE-MISSING",
                          f"enforcement registry guard '{name}' is missing script/tier/status evidence")
        if row["tier"] not in TIERS:
            raise Refusal("registry", "GUARD-TIER-INVALID",
                          f"enforcement registry guard '{name}' has invalid tier '{row['tier']}'")
        if row["status"] not in STATUSES:
            raise Refusal("registry", "GUARD-STATUS-INVALID",
                          f"enforcement registry guard '{name}' has invalid status '{row['status']}'")
        # THE VOCABULARY IS CHECKED HERE, at parse time, rather than at dispatch. A misspelled repository
        # is a typo in configuration; refusing to parse the catalog means it cannot be discovered by a run
        # that happened to select the row, and the refusal names the typo instead of the symptom.
        declared_repo = row.get("repository")
        if declared_repo is not None and str(declared_repo) not in GUARD_REPOSITORIES:
            raise Refusal("registry", "GUARD-REPOSITORY-UNKNOWN",
                          f"enforcement registry guard '{name}' names repository "
                          f"'{declared_repo}', which is outside the vocabulary (one of "
                          f"{', '.join(sorted(GUARD_REPOSITORIES))})")
    for exemption in doc.get("verificationExemptions") or []:
        if (not isinstance(exemption, dict)
                or not str(exemption.get("id") or "").strip()
                or not str(exemption.get("reason") or "").strip()
                or not exemption.get("paths")):
            raise Refusal("registry", "EXEMPTION-INCOMPLETE",
                          "verification exemption is missing id, paths, or reason")
        for pattern in exemption["paths"]:
            text = str(pattern)
            if (text.startswith("**")
                    or any(text.startswith(f"{root_name}/**") or text.startswith(f"{root_name}/*/")
                           for root_name in ("scripts", "tools", ".github"))):
                raise Refusal("registry", "EXEMPTION-CATCH-ALL",
                              f"verification exemption '{exemption['id']}' is a catch-all root: {text}")
    return doc


def parse_local_args(entries: list[str]) -> dict[str, dict[str, str]]:
    """`ID:KEY=VALUE` triples into the per-guard local-argument map.

    MACHINE-LOCAL BY CONSTRUCTION, and that is the reason for the `ID:` prefix: the only caller-supplied
    value a guard ever receives is something that must not be committed (the game dir), so the argument
    is addressed AT a guard rather than merged into a global one. A malformed entry is a refusal by name
    rather than a silently dropped argument -- a dropped machine-local value is the kind of thing that
    makes a guard measure the wrong tree.
    """
    parsed: dict[str, dict[str, str]] = {}
    for entry in entries:
        if ":" not in entry or "=" not in entry:
            raise Refusal("arguments", "LOCAL-ARG-MALFORMED",
                          f"expected ID:KEY=VALUE, got '{entry}'")
        guard_id, pair = entry.split(":", 1)
        key, _, value = pair.partition("=")
        if not guard_id or not key:
            raise Refusal("arguments", "LOCAL-ARG-MALFORMED",
                          f"expected ID:KEY=VALUE, got '{entry}'")
        parsed.setdefault(guard_id, {})[key] = value
    return parsed


def repo_is_git(root: Path) -> bool:
    """Whether the root is inside a git work tree.

    Decided ONCE, and used for exactly one thing: whether a missing `--ci-range` is a refusal or a
    legitimate working-tree run. A non-git fixture has no push range, and refusing there would make the
    contract untestable with a planted root.
    """
    try:
        proc = subprocess.run(["git", "rev-parse", "--is-inside-work-tree"], capture_output=True,
                              text=True, timeout=60, cwd=str(root))
    except (OSError, subprocess.TimeoutExpired):
        return False
    return proc.returncode == 0 and proc.stdout.strip().lower() == "true"


def resolve_ci_range(tier: str, ci_range: str, root: Path) -> str:
    """A CI caller MUST provide the complete push/diff range; a missing range is not a green range."""
    is_git = repo_is_git(root)
    if tier == "ci" and is_git and not ci_range.strip():
        raise Refusal("range", "CI-RANGE-REQUIRED",
                      "CI guard range is required; pass --ci-range <base>..<head> (a missing range is "
                      "not a green range)")
    if tier == "ci" and ci_range.strip() and ".." not in ci_range.strip():
        raise Refusal("range", "CI-RANGE-MALFORMED",
                      f"CI guard range is malformed; expected <base>..<head>: {ci_range}")

    # A range that is well-FORMED but does not resolve is refused here, by name. Without this the
    # failure surfaces from whichever guard happens to diff first, as
    # `GIT-FAILED git diff ... fatal: unknown revision` reported as THAT GUARD BEING RED - so a
    # typo reads as "a guard failed" rather than "you passed a bad range", and every "0 red" claim
    # silently rests on a range string nobody checked. Measured 2026-10-02: passing a commit from a
    # DIFFERENT repository produced exactly that, and it was misread as a real generated-seed
    # failure before the cross-repo SHA was noticed.
    if tier == "ci" and is_git and ci_range.strip():
        base, _, head = ci_range.strip().partition("..")
        for label, rev in (("base", base.strip()), ("head", head.strip())):
            if not rev:
                continue
            probe = subprocess.run(
                ["git", "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}"],
                cwd=root, capture_output=True, text=True)
            if probe.returncode != 0:
                raise Refusal("range", "CI-RANGE-UNRESOLVED",
                              f"CI guard range {label} does not resolve to a commit in this "
                              f"repository: {rev!r}. A range from another repository, a typo, or a "
                              f"pruned ref looks identical to a red guard until it is named here.")
    return ci_range.strip()


def resolve_guard_args(guard_id: str, row: dict, tier: str, ci_range: str, local_args: dict,
                       is_git: bool, extension: str = ".py",
                       subject_root: Path | None = None,
                       script_path: Path | None = None) -> list[str]:
    """The argv for one guard: the registry's `args` for the tier, plus the caller's local exceptions.

    A `{ciRange}` placeholder with no range DROPS ITSELF AND THE SWITCH BESIDE IT. Keeping the switch
    without its value would hand a guard `-Range` and nothing after it, and the failure would be an
    argparse error from a guard that never had a chance to run.
    LOCAL ARGS ARE WRITTEN IN THE GUARD'S OWN DIALECT, which is what `extension` is for. They used to
    be emitted as `-Key` unconditionally, the PowerShell spelling, because that is what the retired
    `.ps1` guards took - while the runner dispatched `.py` guards through `sys.executable`. So a Python
    guard received `-GameDir` where argparse wanted `--game-dir`, and the failure was an
    `unrecognized arguments` exit 2 reported as a red guard. Measured: `game-profile` exited 2 with
    `the following arguments are required: --game-dir, --profile` while the runner had in fact passed
    both values, spelled the old way.

    That wiring was never exercised after the split, because until this runner could resolve a sibling
    repository's script the guard was unreachable and the runner refused before dispatching anything.
    Fixing reachability exposed the defect underneath it, which is the usual shape of this class: the
    first fix makes a previously dead path live, and the dead path was never right.

    Converting a camelCase key to kebab-case was considered and REJECTED, because it is wrong here
    anyway: `ExpectedProfile` becomes `--expected-profile`, and the guard's flag is `--profile`. A value
    whose real flag differs from its key cannot be derived from the key, so the caller states it.

    """
    resolved: list[str] = []
    for raw in (row.get("args") or {}).get(tier, []):
        arg = str(raw)
        if arg == CI_RANGE_PLACEHOLDER:
            if not ci_range.strip():
                if tier == "ci" and is_git:
                    raise Refusal("dispatch", "GUARD-RANGE-REQUIRED",
                                  f"guard '{guard_id}' requires an explicit CI range, but --ci-range "
                                  f"was empty")
                # A non-git fixture has no push range. Drop the switch and its placeholder together; a
                # real CI checkout never reaches this path.
                while resolved and resolved[-1] in RANGE_SWITCHES:
                    resolved.pop()
                continue
            arg = ci_range
        resolved.append(arg)
    # THE SUBJECT ROOT IS PASSED EXPLICITLY, and only when the guard declares `--root`.
    #
    # It is a switch rather than a working directory because the two are different things: a guard's SCRIPT
    # is found by walking every repository (resolve_guard_script), while its SUBJECT is the one repository
    # its row names. Passing `--root` makes the subject an argument the guard cannot silently disagree
    # with.
    #
    # IT IS NOT UNCONDITIONAL, and the reason is measured rather than stylistic. Of the 23 guard scripts in
    # this registry, 22 declare `--root` and one — `audit-doc-citations.py`, dispatched as `doc-citations` —
    # does not, so passing it blindly is an argparse `unrecognized arguments` exit 2, reported as a red
    # guard for a wiring change. The switch is therefore read out of the guard's own `add_argument` call,
    # which is the same static read `missing_interpreters` already makes for `.py`/`.ps1`, rather than
    # maintained here as a list that a new guard would silently fall out of.
    if (subject_root is not None and extension.lower() == ".py"
            and script_path is not None and _accepts_root_flag(script_path)):
        resolved.extend(["--root", str(subject_root)])
    # The one caller-supplied exception: machine-local values (the game dir) that must never be
    # committed. Everything else comes from the registry.
    switch = "--" if extension.lower() == ".py" else "-"
    for key, value in sorted((local_args.get(guard_id) or {}).items()):
        resolved.extend([f"{switch}{key}", str(value)])
    return resolved


def select(catalog: dict, tier: str, only: list[str], skip: list[str], include_backlog: bool) -> list[str]:
    """The guards this run will execute, sorted, and every selection rule in one place.

    `-Only` bypasses the tier/status filters but NOT `-Skip`... and it is checked against the catalog so
    an unknown id is a refusal rather than a silently empty run.
    """
    if only:
        for guard_id in only:
            if guard_id not in catalog:
                raise Refusal("selection", "UNKNOWN-GUARD", f"unknown guard: {guard_id}")
        return list(only)
    selected = []
    for guard_id in sorted(catalog):
        row = catalog[guard_id]
        if guard_id in skip:
            continue
        if row["status"] == "backlog" and not include_backlog:
            continue
        if tier == "ci" and row["tier"] != "ci":
            continue
        # ci.yml runs an `own-step` guard in its own isolated step; the runner must not run it twice.
        if tier == "ci" and row.get("ciEntry") == "own-step":
            continue
        selected.append(guard_id)
    return selected


def check_selection(guards: list[str], catalog: dict, tier: str, only: list[str],
                    include_backlog: bool) -> None:
    """An EMPTY selection is RED, and a CI run with no GATING guard is RED.

    Both were explicit in the original and both are the failure a green exit would otherwise hide: a
    tier filter that matches nothing must not print an empty table and exit 0.
    """
    if not guards:
        raise Refusal("selection", "NO-GUARDS-SELECTED",
                      f"no guards selected for tier '{tier}'; an empty guard run is RED")
    if (tier == "ci" and not only and not include_backlog
            and not any(catalog[g]["status"] == "gating" for g in guards)):
        raise Refusal("selection", "NO-GATING-GUARDS",
                      "CI selected no gating guards; an evidence-free guard run is RED")


def resolve_guard_root(root: Path, repository: str) -> Path:
    """The root a guard must inspect, from the row's `repository` field — or REFUSE.

    THE FAILURE THIS FAILS CLOSED AGAINST. A row naming a repository that is not checked out has no
    subject, so the guard would inspect nothing and — this is the whole point — could report that as
    clean. Measured on this repository's own `generated-seed`: all eight declared trees absent, the
    guard's verdict a coverage claim about nothing. So an absent repository STOPS the run here, by
    name, before any guard is dispatched. It is a refusal and not a skip, because a skip is silence and
    silence is what let the half-finished migration stay invisible.

    `gk-core` resolves to the runner's own root even where the resolver would refuse: a standalone clone
    has no workspace to walk up to, and "this repository" is the one root that is present by definition.
    """
    accessor_name = GUARD_REPOSITORIES.get(repository)
    if accessor_name is None:
        raise Refusal("catalog", "GUARD-REPOSITORY-UNKNOWN",
                      f"guard repository '{repository}' is outside the vocabulary "
                      f"(one of {', '.join(sorted(GUARD_REPOSITORIES))})")
    if repository == "gk-core":
        return root
    accessor = globals()[accessor_name]
    try:
        resolved = Path(accessor(root))
    except Exception as exc:
        raise Refusal("catalog", "GUARD-REPOSITORY-ABSENT",
                      f"guard repository '{repository}' is not resolvable here: {exc}. The row declares a "
                      f"subject this checkout does not carry, so its guard would inspect nothing and could "
                      f"report that as clean. Check the repository out, or set the matching "
                      f"KEEPVERSE_*_ROOT override.") from exc
    if not resolved.is_dir():
        raise Refusal("catalog", "GUARD-REPOSITORY-ABSENT",
                      f"guard repository '{repository}' resolved to {resolved}, which is not a directory")
    return resolved


def resolve_repo_ranges(entries: list[str], root: Path) -> dict[str, str]:
    """`NAME=RANGE` pairs, each VERIFIED to resolve in that repository, into a mapping.

    WHY A SEPARATE RANGE PER REPOSITORY. A commit range is a statement about ONE repository's history.
    Measured on this workspace: gk-core's head `b9499a1` does not resolve in gk-data at all, because the
    split gave each repository its own history — passing gk-core's range to a guard dispatched into the
    corpus repository produces `fatal: Invalid revision range`. So a row whose `repository` is not the
    runner's own cannot be handed the caller's range, and handing it one anyway would be a guard that
    either fails on git's error or, worse, quietly inspects a range nobody asked about.

    Each range is verified HERE, before any guard runs, for the reason `resolve_ci_range` already
    verifies the caller's: a range that does not resolve must be named as such rather than surfacing as
    whichever guard consumed it first.
    """
    resolved: dict[str, str] = {}
    for entry in entries:
        name, sep, value = entry.partition("=")
        name = name.strip()
        if not sep or not name or not value.strip():
            raise Refusal("arguments", "REPO-RANGE-MALFORMED",
                          f"expected NAME=RANGE for a per-repository range, got '{entry}'")
        if name not in GUARD_REPOSITORIES:
            raise Refusal("arguments", "REPO-RANGE-UNKNOWN-REPOSITORY",
                          f"per-repository range names '{name}', which is outside the vocabulary "
                          f"(one of {', '.join(sorted(GUARD_REPOSITORIES))})")
        base = resolve_guard_root(root, name)
        for label, rev in (("base", value.partition("..")[0].strip()),
                           ("head", value.partition("..")[2].strip())):
            if not rev:
                continue
            probe = subprocess.run(["git", "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}"],
                                   cwd=str(base), capture_output=True, text=True)
            if probe.returncode != 0:
                raise Refusal("range", "REPO-RANGE-UNRESOLVED",
                              f"the range for '{name}' does not resolve in {base}: {label} {rev!r}. A "
                              f"commit range belongs to the repository whose history it names, and the "
                              f"split gave each repository its own.")
        resolved[name] = value.strip()
    return resolved


def guard_range_for(guard_id: str, row: dict, root: Path, ci_range: str,
                    repo_ranges: dict[str, str]) -> str:
    """The range THIS guard must be given, from the repository its row names.

    A guard whose `repository` is the runner's own gets the caller's range. One that names a different
    repository gets that repository's own range, and the absence of one is a REFUSAL rather than a
    fallback to the caller's — because falling back would inspect the wrong repository's history and
    report the result as though it were the declared subject's.
    """
    repository = str(row["repository"])
    if repository == "gk-core":
        return ci_range
    if repository not in repo_ranges:
        raise Refusal("range", "GUARD-RANGE-UNAVAILABLE",
                      f"guard '{guard_id}' inspects '{repository}', and no range was supplied for that "
                      f"repository. A commit range names one repository's history, so this repository's "
                      f"push range cannot stand in for it. Pass --repo-range {repository}=<base>..<head>.")
    return repo_ranges[repository]


def check_guard_repositories(root: Path, guards: list[str], catalog: dict) -> None:
    """Every selected row's `repository`, resolved BEFORE dispatch — and every UNDECLARED one refused.

    This is the durable half of the fix, and it is the part that stops the next relocation from being
    invisible. A half-finished migration produced two different shapes of the same defect, and both are
    checked here:

      * a row that NAMES a repository which is absent — refused, above, because the guard would inspect
        nothing;
      * a row that names NO repository at all — refused here, because after the split an unnamed row is
        an UNPROVEN claim. It used to mean "this repository" when there was one repository; now it means
        "wherever the runner happens to be", which is how `generated-seed` came to run against a root
        holding none of its own subject. Requiring the field makes that visible at the registry rather
        than in a guard's stderr.

    EVERY row is named, including the twenty-odd whose subject is unambiguously gk-core's. That is
    deliberate: a field only half-filled is a field whose absence means nothing, and the point is that a
    reader can tell which root each guard inspects by reading the row.
    """
    unnamed = [g for g in guards if not str(catalog[g].get("repository") or "").strip()]
    if unnamed:
        raise Refusal("catalog", "GUARD-REPOSITORY-UNDECLARED",
                      f"{len(unnamed)} guard row(s) name no repository, so nothing states which root they "
                      f"inspect: {', '.join(unnamed)}. Since the split an unnamed row resolves against the "
                      f"runner's own root, which is how a guard whose subject moved came to run against a "
                      f"directory holding none of it. Name the repository (one of "
                      f"{', '.join(sorted(GUARD_REPOSITORIES))}) on every row.")
    for guard_id in guards:
        resolve_guard_root(root, str(catalog[guard_id]["repository"]))


def _guard_script_bases(root: Path) -> tuple[Path, ...]:
    """EVERY repository that could own a registry guard script, THIS ONE FIRST.

    The enforcement registry names 29 guards, all with a bare `scripts/<file>` path and no repository
    qualifier, because there was one repository when they were written. Measured on this registry: 23
    resolve in gk-core, 4 live in gk-fusion (`single-writer`, `funnel-delta`, `game-profile`,
    `injector-compile`) and 2 in gk-workflow at the workspace root (`session-boundary`,
    `doc-citations`). Joining every one onto gk-core made the runner refuse before running anything:

        REFUSED [catalog]: GUARD-SCRIPT-MISSING
          guard script missing: game-profile -> scripts/guard-game-profile.py

    That refusal reached the deploy tool, which passed its game-profile precondition through here, so
    **no deploy could run at all**. A catalog that cannot name its own files is not a finding about a
    guard; it is a finding about the reader, reported in the vocabulary of the thing it misread.

    Every accessor is wrapped because several RAISE when their subject is absent. A runner that dies
    because gk-forge is missing has turned a sibling's absence into this run's outcome, and it would
    do it as a traceback rather than a named refusal.
    """
    bases: list[Path] = [root]
    for accessor in (core_root, forge_root, fusion_root, web_root, workspace_root,
                     content_root, authored_content_root):
        try:
            base = accessor(root)
        except Exception:
            continue
        if base:
            base = Path(base)
            if base not in bases and base.is_dir():
                bases.append(base)
    return tuple(bases)


def resolve_guard_script(root: Path, script: str) -> tuple[Path, Path] | None:
    """The guard script, and the repository that owns it, or None when no repository has it.

    BOTH are returned because they have to agree. This runner executes every child with the ROOT as its
    working directory, on the recorded reasoning that a Python guard is a plain script that opens
    relative paths and fails closed when measured from elsewhere. Resolving a gk-fusion guard's file and
    then running it from gk-core would hand it a working directory with no `src/FusionRpg.Injector` in
    it, and the guard would report a red that is a measurement artefact. For a gk-core-owned guard this
    is unchanged, because the local root answers first.
    """
    rel = str(script).replace("\\", "/").strip()
    if not rel:
        return None
    for base in _guard_script_bases(root):
        candidate = base / rel
        if candidate.is_file():
            return base, candidate
    return None


def _accepts_root_flag(script: Path) -> bool:
    """Whether this guard declares a `--root` option, read from its own `add_argument` call.

    A STRING SEARCH, and deliberately not an import or an `--help` probe. The guard may be absent a
    sibling's resolver cannot see, may refuse at import (several raise when their subject is missing), and
    `--help` would run a process per guard per run. `add_argument("--root"` is the declaration site, it is
    present in every guard that has the switch, and a guard that gains the switch later is picked up here
    without this file being edited — which is the property that matters, since a list maintained here
    would be wrong the moment a guard changed.
    """
    try:
        text = script.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    return '"--root"' in text or "'--root'" in text


def check_scripts_exist(root: Path, guards: list[str], catalog: dict) -> None:
    """Every selected guard's FILE must exist, checked BEFORE any guard runs.

    Checking up front means a missing script is one refusal naming it, rather than a red row discovered
    half-way through a batch whose earlier guards have already been reported.
    """
    for guard_id in guards:
        if resolve_guard_script(root, catalog[guard_id]["script"]) is None:
            # The message names every repository consulted. One that named a single directory sends the
            # reader to add a file to a directory the script was never going to be in - which is exactly
            # what happened when this said "scripts/run_guards.py does not exist" and the tool that
            # exists with that name lives in a different repository.
            raise Refusal("catalog", "GUARD-SCRIPT-MISSING",
                          f"guard script missing: {guard_id} -> {catalog[guard_id]['script']}; searched "
                          + ", ".join(str(b) for b in _guard_script_bases(root)))


def missing_interpreters(selected_scripts: list[str], only: list[str]) -> list[str]:
    """Interpreters this batch needs and PATH does not have, by NAME.

    The original probed for `powershell` while dispatching on `pwsh` first, so a PowerShell-7-only
    machine -- the normal case off Windows -- got a warning naming an interpreter it never needed. The
    probe here asks for the interpreter each selected script's EXTENSION actually dispatches to, so a
    warning is never a phantom on the platform it is most likely to be read.
    """
    wanted = {"python"} if any(str(s).endswith(".py") for s in selected_scripts) else set()
    if any(str(s).endswith(".ps1") for s in selected_scripts):
        wanted.add("pwsh" if shutil.which("pwsh") else "powershell")
    return sorted(name for name in wanted if shutil.which(name) is None)


@dataclass
class GuardResult:
    id: str
    tier: str
    status: str
    script: str
    argv: list[str]
    exit: int
    seconds: float
    stderr: str = ""


@dataclass
class Report:
    """What the run did. Every count is re-measured; none is a constant carried forward."""

    tier: str = ""
    ci_range: str = ""
    selected: list[str] = field(default_factory=list)
    results: list[dict] = field(default_factory=list)
    red_gating: list[str] = field(default_factory=list)
    backlog: list[str] = field(default_factory=list)
    undispatchable: list[str] = field(default_factory=list)
    missing_interpreters: list[str] = field(default_factory=list)
    environment_note: str = ""
    stages: list[str] = field(default_factory=list)
    #: Guards that exited 0 while writing to stderr — a CLEAN VERDICT THAT ALSO SAID SOMETHING. Not a
    #: failure: these guards passed. Recorded because the alternative is a reader who cannot tell
    #: "checked and passed" from "could not check, said so, and the exit code was 0 anyway".
    degraded: list[str] = field(default_factory=list)


def dispatch(root: Path, guards: list[str], catalog: dict, tier: str, ci_range: str,
             local_args: dict, timeout: int, repo_ranges: dict[str, str] | None = None) -> Report:
    """Run every selected guard, collecting EVERY result -- a failure never stops the loop.

    The loop is the point: one red guard must not hide a second, which is the masking defect `ci.yml`
    met once. Every child runs with the ROOT as its working directory, because a Python guard is a plain
    script that opens relative paths and measured from anywhere else it fails closed with
    "no such path(s): ['src']". That is the runner's contract, not the caller's accident.
    """
    report = Report(tier=tier, ci_range=ci_range, selected=list(guards))
    repo_ranges = repo_ranges or {}
    is_git = repo_is_git(root)
    for guard_id in guards:
        row = catalog[guard_id]
        found = resolve_guard_script(root, row["script"])
        if found is None:
            # check_scripts_exist refuses on this before dispatch, so reaching here means the file
            # vanished between the two passes. Recorded as a red row rather than an exception, because a
            # vanished file is a fact about the tree and not about the runner.
            report.results.append({"id": guard_id, "tier": row["tier"], "status": row["status"],
                                   "script": str(row["script"]), "argv": [],
                                   "exit": EXIT_UNDISPATCHABLE, "seconds": 0.0,
                                   "stderr": f"script vanished after the catalog check: {row['script']}"})
            report.undispatchable.append(guard_id)
            continue
        # The working directory is the OWNING repository, not gk-core - see resolve_guard_script.
        script_base, script = found
        # THE ROW'S `repository` IS THE SUBJECT ROOT, and it is passed as `--root` rather than only used
        # as a working directory, because a guard that takes `--root` reads its subject from it while its
        # OWN file may live in another repository. `generated-seed` is exactly that shape: the script is
        # gk-core's, the corpus is gk-data's. Passing only `cwd` would leave the guard measuring its
        # declared trees against a repository that has none of them.
        subject_root = resolve_guard_root(root, str(row["repository"]))
        # THE RANGE FOLLOWS THE SUBJECT, not the caller. A guard dispatched into another repository is
        # handed that repository's range, and the absence of one has already refused in main().
        guard_range = guard_range_for(guard_id, row, root, ci_range, repo_ranges)
        argv = resolve_guard_args(guard_id, row, tier, guard_range, local_args, is_git,
                                  script.suffix, subject_root, script)
        extension = script.suffix.lower()
        started = time.monotonic()
        if extension == ".py":
            command = [sys.executable, str(script), *argv]
        elif extension == ".ps1":
            command = [shutil.which("pwsh") or shutil.which("powershell"), str(script), *argv]
            if command[0] is None:
                raise Refusal("interpreters", "POWERSHELL-NOT-ON-PATH",
                              f"guard '{guard_id}' is a .ps1 and neither pwsh nor powershell is on PATH",
                              EXIT_UNDISPATCHABLE)
        else:
            # Fail CLOSED on an extension the runner cannot dispatch. Guessing would run the wrong thing
            # or skip the guard, and a guard that silently does not run is worse than one that refuses.
            message = (f"runner cannot dispatch '{row['script']}': unsupported extension "
                       f"'{extension}'. A guard must be .ps1 or .py.")
            report.results.append({"id": guard_id, "tier": row["tier"], "status": row["status"],
                                  "script": str(row["script"]), "argv": argv,
                                  "exit": EXIT_UNDISPATCHABLE, "seconds": 0.0, "stderr": message})
            report.undispatchable.append(guard_id)
            continue
        try:
            proc = subprocess.run(command, capture_output=True, text=True, timeout=timeout,
                                 cwd=str(script_base))
            code, stderr = proc.returncode, (proc.stderr or "")
        except subprocess.TimeoutExpired:
            code = EXIT_UNDISPATCHABLE
            stderr = f"timed out after {timeout}s; a wedged guard holds the whole run open"
        except (OSError, FileNotFoundError) as exc:
            code, stderr = EXIT_UNDISPATCHABLE, f"spawn failed: {exc}"
        report.results.append({"id": guard_id, "tier": row["tier"], "status": row["status"],
                               "script": str(row["script"]), "argv": argv, "exit": code,
                               "seconds": round(time.monotonic() - started, 1), "stderr": stderr.strip()})
    report.stages.append("dispatch")
    report.red_gating = [r["id"] for r in report.results if r["status"] == "gating" and r["exit"] != 0]
    report.backlog = [r["id"] for r in report.results if r["status"] == "backlog"]
    return report


def summarise(report: Report) -> tuple[str, str]:
    """The verdict and the exit code, naming EVERY red gating guard.

    RETURNS rather than raises. A red run is not a refusal -- it is a run that COMPLETED, with a result set
    that is the whole point of the report. Raising here discarded that result set, which is exactly what
    the differential against the original caught: the original prints its table before it throws, so a
    reader of a red run sees which guards ran and what each returned, and a port that reports zero guards
    on a red run is less informative than the thing it replaces.
    """
    if report.red_gating:
        # THE MOST SEVERE RED, NOT THE LAST ONE. This returned report.results[-1]["exit"], so the
        # aggregate exit code depended on the ORDER the guards happened to run in: an identical set
        # of results reported 64 or 1 purely by position, and reordering the registry silently changed
        # what CI concluded. An audit caught it. It also undermines a claim made earlier in this
        # session - that a refusal is distinguishable from a finding - which is true of a guard and
        # arbitrary in aggregate, so the distinction has to be made HERE as well as there.
        #
        # UNDISPATCHABLE pairs with a guard's own refusal because neither is a finding about the
        # tree: one says the guard could not run, the other says it could not see its subject. Both
        # mean "no verdict was reached", and a reader who cannot tell that from a finding will treat
        # a broken gate as a clean one.
        red_ids = set(report.red_gating)
        codes = {r["exit"] for r in report.results if r["id"] in red_ids and r["exit"] != 0}
        worst = EXIT_UNDISPATCHABLE if EXIT_UNDISPATCHABLE in codes else (max(codes) if codes else 0)
        return (f"guards failed: {', '.join(report.red_gating)}{_degraded_note(report)}"
                f"{report.environment_note}",
                worst or EXIT_FAILED)
    return f"GUARDS OK - {len(report.results)} guard(s) run, 0 red{_degraded_note(report)}", EXIT_OK


def _degraded_note(report: Report) -> str:
    """The count of clean-verdict guards that also wrote to stderr, or nothing.

    Appended to the verdict line rather than printed separately, because the verdict line is what a CI
    log's reader sees first and "0 red" must not be able to sit alone above an unexamined subject.
    """
    if not report.degraded:
        return ""
    return (f"; {len(report.degraded)} exited 0 while writing to stderr "
            f"({', '.join(report.degraded)}) — a clean verdict that also declined to answer, "
            f"read the stderr above before treating 0 red as coverage")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run the enforcement registry's guards (replaces run-guards.ps1).")
    parser.add_argument("--tier", choices=TIERS, default="ci")
    parser.add_argument("--only", action="append", default=[], metavar="ID",
                        help="run exactly these guard ids; repeatable")
    parser.add_argument("--skip", action="append", default=[], metavar="ID",
                        help="exclude a caller-invoked guard from the tier batch; repeatable")
    parser.add_argument("--include-backlog", action="store_true",
                        help="also run `status: backlog` guards, which never gate the run")
    parser.add_argument("--local-arg", action="append", default=[], metavar="ID:KEY=VALUE",
                        help="a machine-local argument for one guard, never committed; repeatable")
    parser.add_argument("--ci-range", default="",
                        help="<base>..<head> for a CI run; required, because a missing range is not a "
                             "green range")
    parser.add_argument("--repo-range", action="append", default=[], metavar="NAME=RANGE",
                        help="the <base>..<head> to use for a guard whose registry row names repository "
                             "NAME; repeatable. A commit range belongs to one repository's history, so a "
                             "row declaring gk-data cannot be given this repository's range.")
    parser.add_argument("--root", type=Path, default=None)
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per guard (default {DEFAULT_TIMEOUT}; the original had NO timeout "
                             f"on any guard invocation, and this runner is the longest thing in CI)")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    root = (args.root or Path(__file__).resolve().parent.parent).resolve()

    try:
        local_args = parse_local_args(args.local_arg)
        doc = read_registry(root)
        report = Report()
        report.stages.append("registry")
        catalog = doc["guards"]
        ci_range = resolve_ci_range(args.tier, args.ci_range, root)
        report.stages.append("range")
        guards = select(catalog, args.tier, args.only, args.skip, args.include_backlog)
        check_selection(guards, catalog, args.tier, args.only, args.include_backlog)
        check_scripts_exist(root, guards, catalog)
        check_guard_repositories(root, guards, catalog)
        repo_ranges = resolve_repo_ranges(args.repo_range, root)
        # Every row that names ANOTHER repository must have a range for it, checked BEFORE dispatch so the
        # answer is a named refusal rather than whichever guard consumed the missing piece first.
        for guard_id in guards:
            guard_range_for(guard_id, catalog[guard_id], root, ci_range, repo_ranges)
        report.stages.extend(("selection", "catalog"))
        absent = missing_interpreters([str(catalog[g]["script"]) for g in guards], args.only)
        report.missing_interpreters = absent
        if absent:
            report.environment_note = (f" (PATH lacks {', '.join(absent)}: a red guard that shells out "
                                       f"to one is an environment fault, not a verdict)")
        report.stages.append("interpreters")
        report = dispatch(root, guards, catalog, args.tier, ci_range, local_args, args.timeout,
                         repo_ranges)
        report.ci_range = ci_range
        verdict, verdict_exit = summarise(report)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": refusal.stage,
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        else:
            print(f"RUN-GUARDS REFUSED [{refusal.stage}]: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    # `verdict` is FAILED for a red run, and the exit code is the FIRST red guard's so it stays comparable
    # with the original. A red run that exited 0, or that reported "OK" with red rows, would be the one
    # failure this whole tool exists to prevent.
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK" if verdict_exit == EXIT_OK else "FAILED",
                          "summary": verdict, "exitCode": verdict_exit, **report.__dict__}, indent=2))
        return verdict_exit
    print(f"\nGuard runner (--tier {report.tier}):")
    print(f"  {'id':<44} {'tier':<7} {'status':<9} {'exit':>5} {'s':>6}")
    for row in report.results:
        print(f"  {row['id']:<44} {row['tier']:<7} {row['status']:<9} {row['exit']:>5} {row['seconds']:>6}")
        # STDERR IS SHOWN FOR A RED ROW *AND* FOR A GREEN ONE THAT WROTE ANY.
        #
        # The condition used to be `row["exit"] != 0 and row["stderr"]`, which is right about findings
        # and wrong about refusals: this repository's guards print a NAMED REFUSAL to stderr and exit 0
        # when they cannot reach their subject — guard-generated-seed's CI-RANGE-REQUIRED,
        # guard-verification-boundaries' registry note, guard-population-pin's EXIT_FORGE_ROOT_MISSING.
        # Under the old condition every one of those printed a clean table row with its refusal
        # discarded, so "GUARDS OK - 26 guard(s) run, 0 red" was a run in which an unknown number of
        # guards inspected nothing. That is the defect the module docstring's own line 23 describes —
        # the stderr log was deleted — reproduced one level up, in the summary rather than the temp
        # file.
        #
        # The fix surfaces the text; it does not change any exit code, so a guard that genuinely passed
        # still passes. A green row that wrote to stderr is now VISIBLE as having written to stderr,
        # which is the difference between a real green and an unreached one.
        if row["stderr"]:
            label = (f"--- {row['id']} stderr ---" if row["exit"] != 0
                     else f"--- {row['id']} stderr (exit 0: a clean verdict that also said this) ---")
            print(f"    {label}")
            for line in row["stderr"].splitlines():
                print(f"    {line}")
        if row["exit"] == 0 and row["stderr"]:
            report.degraded.append(row["id"])
    if report.backlog:
        print("\nBACKLOG (not gating - a backlog guard never fails the run):")
        for row in report.results:
            if row["status"] == "backlog":
                print(f"  {row['id']:<44} {row['exit']:>5} {row['seconds']:>6}")
    if report.missing_interpreters:
        print(f"\nWARNING (environment, not a guard verdict): PATH has no "
              f"{', '.join(repr(m) for m in report.missing_interpreters)}.")
        print("A guard that shells out to a missing interpreter reports its OWN red, so a red row may be")
        print("phantom. Re-run from a shell with a complete PATH before reading this table as evidence.")
    print(f"\n{verdict}")
    return verdict_exit


if __name__ == "__main__":
    sys.exit(main())
