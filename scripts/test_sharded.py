#!/usr/bin/env python3
r"""Run one test project as N concurrent `dotnet test` processes over a complete, disjoint partition.

Replaces `test-sharded.ps1`. The shard manifest is `gk-core/scripts/test-shards.v1.json`; the project is
resolved through `gk-core/scripts/verification-boundaries.v1.json` first, so a project with no manifest entry is
refused rather than silently run unsharded.

WHY THIS EXISTS
---------------
`data-tests-sharding` H2 (`docs/architecture/test-verification-boundary/spec-data-tests-sharding.md`).
`FusionRpg.Data.Tests` cannot parallelise inside one process -- its stores serialise on SQLite's
process-global in-memory VFS mutex (`test-architecture-audit.md` §3) -- so this runner builds ONCE, then
runs each manifest shard as its own OS process against its own results directory, and reports every
shard's exit code and wall separately. A thread cap is the rejected symptom treatment; this is the
process-boundary lever.

Every run re-checks completeness: the union of the shards' executed test ids must be gap-free BY
CONSTRUCTION (the remainder shard's filter is the exact complement of every named shard), and an id
appearing in two shards' TRX output fails the run, naming the id and both shards. An empty NAMED shard
(every test under its prefixes was deleted or renamed) is a manifest defect and fails the run the same
way; an empty remainder is legal.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **IT COULD HANG FOREVER, AND A HANGING RUNNER REPORTS NOTHING.** The original started every shard
  process and then called `$p.Process.WaitForExit()` with no timeout, so a shard wedged on a hung test
  held the run open indefinitely and the run's exit code -- the only thing CI reads -- never appeared.
  `dotnet test --blame-hang-timeout 10min` bounds the *test host*, not the runner. So the port takes a
  `--shard-timeout` (default 30 minutes, comfortably past the blame-hang budget) and, on expiry, names
  the shard it was waiting on, terminates that process, and exits non-zero naming the stage. A tool that
  can hang is a defect, and this one used to.
* **`$LASTEXITCODE` WAS THE ONLY THING PROVING THE BUILD RAN.** The original wrote the build's verdict
  to a variable PowerShell populated as a side effect. The port reads an explicit return code from
  `subprocess.run(..., capture_output=True, timeout=...)`, so a build that produced no output and a build
  that failed are distinguishable.
* **THE DEFAULT-PROFILE FILTER IS READ FROM ANOTHER TOOL, so IT IS DIALECT-AWARE.** The original
  regexes `FILTER = "..."` out of `gk-core/scripts/test_fast.py`. That tool is not the sharding runner, and when it
  is, the regex must follow it. `read_default_profile_filter` therefore looks for the `.py` first and
  falls back to the `.ps1`, and it accepts both spellings of the assignment. A hard-coded `.ps1` here
  would have been a landmine that went off on a port that was not supposed to touch this file.
* **`$PSScriptRoot` IS NOT RELIABLE WHILE A MANDATORY PARAMETER IS STILL BINDING.** The original says so
  in a comment and resolves its root in the body rather than as a parameter default. Python has no such
  ordering problem, so the root is a plain optional argument that defaults to this file's repository --
  and the comment is kept, because the trap it describes is one a reader would otherwise re-introduce
  when porting something else with the same shape.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import xml.etree.ElementTree as ElementTree
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath

GUARD_ID = "test-sharded"
EXIT_OK = 0
EXIT_FAILED = 1

# The VSTest/TRX namespace. Namespaced TRX is the default and the only shape the runner writes, so a
# TRX without it is a different producer and is refused rather than parsed as empty.
TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# A shard is either a set of prefixes or the exact complement of every named shard. CLOSED vocabulary of
# two, and the manifest states which by an explicit flag rather than by absence.
REMAINDER_KEY = "remainder"

# Default `dotnet test` blame-hang budget, and the runner's own ceiling. The runner's must exceed the
# host's or it kills a run the host was about to condemn and reports on its own behalf.
BLAME_HANG = "10min"
DEFAULT_SHARD_TIMEOUT_SECONDS = 1800
DEFAULT_BUILD_TIMEOUT_SECONDS = 1800

# The other tool that owns the default profile filter. Read once, dialect-aware; see the docstring.
PROFILE_FILTER_SPELLINGS = (
    # SNAKE, not kebab: a Python module's stem has to be a valid identifier, so the ported tool is
    # `test_fast.py`. Naming `test-fast.py` here made the reader look for a file that does not exist
    # and answer NO-DEFAULT-PROFILE-FILTER, which failed 7 guard tests -- and the `.ps1` entry above
    # it had just been repointed, so the spelling right beside the edit was the one nobody re-read.
    ("test_fast.py", (r'^\s*\$?Filter\s*=\s*"([^"]+)"', r'^\s*FILTER\s*=\s*"([^"]+)"')),
)


class Refusal(Exception):
    """A named precondition failure. A run that cannot start says why instead of exiting 0."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Shard:
    """One manifest shard: an id, and either prefixes or the remainder flag."""

    id: str
    prefixes: tuple[str, ...] = ()
    remainder: bool = False


@dataclass
class ShardResult:
    """What one shard did. `wall` is seconds, which is a READING and not a threshold."""

    id: str
    exit_code: int
    wall: float
    tests: int
    remainder: bool


@dataclass
class Result:
    shards: list[ShardResult] = field(default_factory=list)
    overlaps: list[str] = field(default_factory=list)
    empty_named: list[str] = field(default_factory=list)
    total_tests: int = 0
    replayed: bool = False


def read_default_profile_filter(root: Path) -> str | None:
    """The default local-profile filter, read from whichever `test_fast` is live.

    Returns None when neither tool is present, and the CALLER decides whether that is a refusal: the
    original threw here, but only on the path that uses it, and a caller that passed `--extra-filter`
    explicitly never needed the file to be readable.
    """
    for name, patterns in PROFILE_FILTER_SPELLINGS:
        candidate = root / "scripts" / name
        if not candidate.is_file():
            continue
        text = candidate.read_text(encoding="utf-8", errors="replace")
        for pattern in patterns:
            match = re.search(pattern, text, re.MULTILINE)
            if match:
                return match.group(1)
    return None


def shard_filter(shard: Shard, named: list[Shard], extra_filter: str | None) -> str:
    """The VSTest filter for one shard.

    A named shard ORs its prefixes with `|`; the remainder shard ANDs the NEGATION of every named
    shard's prefixes with `&`. That asymmetry is what makes the partition complete and disjoint BY
    CONSTRUCTION rather than by agreement between the manifest and the runner.

    THE EMPTINESS REFUSAL LIVES HERE, NOT IN `main`, AND IT CHECKS THE SHARD'S OWN FILTER
    Both halves of that are corrections found by the contract suite, and both matter. `main` used to
    refuse an empty filter, but it did so on the string this function RETURNS -- and the return value
    has already had the extra filter ANDed onto it, so a prefix-less named shard yielded
    `()&(Category!=Heavy)`, which is non-empty, and the run proceeded. An empty filter reaching
    `dotnet test` is the exact failure this is meant to prevent: VSTest reads it as no filter at all,
    so every shard runs the WHOLE suite and the run reports success having proven nothing.
    And moving the check into the one function that builds the filter makes it impossible to reach the
    remainder branch without passing the named branch's guard -- a caller that builds a filter by hand
    gets the same refusal as `main` does.
    """
    if shard.remainder:
        prefixes = sorted(p for s in named for p in s.prefixes)
        filt = "&".join(f"FullyQualifiedName!~{p}" for p in prefixes)
    else:
        filt = "|".join(f"FullyQualifiedName~{p}" for p in sorted(shard.prefixes))
    if not filt.strip():
        raise Refusal("EMPTY-SHARD-FILTER",
                      f"shard '{shard.id}' produced an empty filter; a named shard with no prefixes "
                      "matches every test, so the partition would run the whole suite once per shard")
    return f"({filt})&({extra_filter})" if extra_filter else filt


def read_trx_test_ids(trx_path: Path) -> list[str]:
    """Every executed test's identity from a TRX file, whatever the outcome.

    "Executed" means the runner RECORDED A RESULT, not that it passed: a failing test is still one the
    shard ran, and dropping failures here would make an overlap invisible exactly when the tree is
    broken. The name is resolved through `TestDefinitions` so it is the identity rather than the
    display name, falling back to the result's own `testName` when the definition is absent.
    """
    try:
        tree = ElementTree.parse(trx_path)
    except (ElementTree.ParseError, OSError) as exc:
        raise Refusal("TRX-UNREADABLE", f"{trx_path.name}: {exc}") from exc
    root = tree.getroot()
    names: dict[str, str] = {}
    for unit in root.findall("./t:TestDefinitions/t:UnitTest", TRX_NS):
        method = unit.find("t:TestMethod", TRX_NS)
        if method is not None:
            names[unit.get("id", "")] = f"{method.get('className', '')}.{method.get('name', '')}"
    ids: list[str] = []
    for result in root.findall("./t:Results/t:UnitTestResult", TRX_NS):
        test_id = result.get("testId", "")
        ids.append(names.get(test_id) or result.get("testName", ""))
    return ids


def normalized_repo_path(root: Path, candidate: str) -> str:
    """`candidate` as a root-joined, slash-normalised, trailing-slash-free path.

    STRING-NORMALISED ONLY, never touching disk -- the planted registries the manifest tests use name
    files that do not exist, and a tool that resolved them would refuse the very fixtures that exercise
    its overlap detection.
    """
    text = candidate if PurePosixPath(candidate).is_absolute() or candidate.startswith(("/", "\\")) \
        else str(root / candidate)
    return text.replace("\\", "/").rstrip("/")


def load_json(path: Path, what: str) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise Refusal(f"{what}-MISSING", str(path)) from exc
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal(f"{what}-UNREADABLE", f"{path}: {exc}") from exc


def resolve_project_id(registry: dict, root: Path, project: str) -> str:
    """The verification-registry project id for `project`, or a refusal naming what it resolved to."""
    target = normalized_repo_path(root, project)
    projects = registry.get("projects") or {}
    for name, value in projects.items():
        if normalized_repo_path(root, str(value)) == target:
            return name
    raise Refusal("PROJECT-NOT-IN-REGISTRY",
                  f"'{project}' resolves to {target}, which is no verification-boundaries project id")


def load_shards(manifest: dict, project_id: str, manifest_name: str) -> tuple[list[Shard], int]:
    """The manifest's shards for one project, and its thread cap."""
    entry = (manifest.get("projects") or {}).get(project_id)
    if not entry:
        raise Refusal("NO-SHARD-ENTRY", f"no shard entry for project '{project_id}' in {manifest_name}")
    shards: list[Shard] = []
    for raw in entry.get("shards") or []:
        shards.append(Shard(id=str(raw.get("id", "")),
                            prefixes=tuple(raw.get("prefixes") or ()),
                            remainder=bool(raw.get(REMAINDER_KEY, False))))
    if not shards:
        raise Refusal("NO-SHARDS", f"project '{project_id}' has an empty shard list in {manifest_name}")
    ids = [s.id for s in shards]
    duplicates = sorted({i for i in ids if ids.count(i) > 1})
    if duplicates:
        raise Refusal("DUPLICATE-SHARD-ID", f"project '{project_id}': {', '.join(duplicates)}")
    return shards, int(entry.get("maxParallelThreads", 0) or 0)


def run_build(project: str, configuration: str, cwd: Path, timeout: int) -> None:
    """Build once for every shard, and refuse BY NAME when it fails.

    A build that is skipped or that fails quietly would let every shard report a confusing failure, so
    this is a refusal rather than a reported finding: the run never started.
    """
    proc = subprocess.run(["dotnet", "build", project, "-c", configuration],
                          capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
    if proc.returncode != 0:
        tail = (proc.stdout or proc.stderr or "").strip().splitlines()[-6:]
        raise Refusal("BUILD-FAILED",
                      f"dotnet build {project} -c {configuration} exited {proc.returncode}: "
                      + " | ".join(tail))


def start_shard(project: str, configuration: str, shard: Shard, filt: str, results_dir: Path,
                threads: int, cwd: Path):
    """Start one shard's `dotnet test` and return its Popen with stdout/stderr redirected to files."""
    argv = ["dotnet", "test", project, "-c", configuration, "--no-build",
            "--filter", filt,
            "--blame-hang", "--blame-hang-timeout", BLAME_HANG,
            "--logger", f"trx;LogFileName={shard.id}.trx",
            "--results-directory", str(results_dir),
            "--", f"xUnit.MaxParallelThreads={threads}"]
    results_dir.mkdir(parents=True, exist_ok=True)
    out = (results_dir / "stdout.log").open("w", encoding="utf-8", errors="replace")
    err = (results_dir / "stderr.log").open("w", encoding="utf-8", errors="replace")
    try:
        proc = subprocess.Popen(argv, stdout=out, stderr=err, cwd=str(cwd), text=True)
    except OSError as exc:
        out.close()
        err.close()
        raise Refusal("SHARD-SPAWN-FAILED", f"shard '{shard.id}': {exc}") from exc
    return proc, out, err


def collect(project: str, configuration: str, shards: list[Shard], threads: int, root: Path,
            temp_root: Path, shard_timeout: int) -> list[ShardResult]:
    """Start every shard, wait for every shard, and report each one's exit code and wall.

    The original collected all PIDs then waited for all of them, which is the parallelism. Kept exactly:
    a shard that finishes early frees its slot for nothing, because they all start together -- the
    parallelism is in the BUILDS of the test host, not in scheduling them one at a time.
    """
    started: list[tuple[Shard, object, object, object, float, Path]] = []
    for shard in shards:
        filt = shard_filter(shard, [s for s in shards if not s.remainder], None)
        results_dir = temp_root / shard.id
        proc, out, err = start_shard(project, configuration, shard, filt, results_dir, threads, root)
        started.append((shard, proc, out, err, time.monotonic(), results_dir))

    results: list[ShardResult] = []
    for shard, proc, out, err, began, results_dir in started:
        try:
            proc.wait(timeout=shard_timeout)
        except subprocess.TimeoutExpired:
            # Name the shard, not "the run". A run that fails on a timeout must say WHICH shard wedged,
            # because the whole point of the per-shard split is that one wedged shard is diagnosable.
            proc.kill()
            try:
                proc.wait(timeout=30)
            except subprocess.TimeoutExpired:  # pragma: no cover - a killed process that will not die
                pass
            out.close()
            err.close()
            raise Refusal("SHARD-TIMEOUT",
                          f"shard '{shard.id}' did not exit within {shard_timeout}s and was killed; "
                          f"its logs are under {results_dir}") from None
        finally:
            out.close()
            err.close()
        results.append(ShardResult(shard.id, proc.returncode, time.monotonic() - began, 0,
                                   shard.remainder))
    return results


def analyse(shard_runs: list[ShardResult], shards: list[Shard], temp_root: Path) -> Result:
    """Overlap and empty-named-shard detection over each shard's OWN TRX output.

    PER SHARD, not per run: each shard's results directory is `<temp>/<shard id>`, and reading the whole
    temp root instead makes every shard see every other shard's ids. The first version of this function
    did exactly that, and the consequence is worth recording because it was a FALSE PASS rather than a
    red: the overlap case still found two shards claiming one id, so its assertion was satisfied -- for
    the wrong reason -- while the two cases that assert a shard's OWN test count went red. A test that
    passes because the tool over-reports is worse than one that fails, because it stops looking.
    """
    by_id = {s.id: s for s in shards}
    result = Result()
    seen: dict[str, str] = {}
    for run in shard_runs:
        shard = by_id.get(run.id)
        run.remainder = bool(shard and shard.remainder)
        results_dir = temp_root / run.id
        ids: list[str] = []
        if results_dir.is_dir():
            for trx in sorted(results_dir.rglob("*.trx")):
                ids.extend(read_trx_test_ids(trx))
        run.tests = len(set(ids))
        result.total_tests += run.tests
        if not run.remainder and run.tests == 0:
            result.empty_named.append(run.id)
        for test_id in sorted(set(ids)):
            if test_id in seen:
                result.overlaps.append(f"{test_id} (shard '{seen[test_id]}' and shard '{run.id}')")
            else:
                seen[test_id] = run.id
    result.shards = shard_runs
    return result


@contextmanager
def scratch_root(replay_root: str | None, base: str | None = None):
    """Yield the run's results root, and ALWAYS remove it if this tool created it.

    THE REPO'S TEST-SUBSTRATE RULE, APPLIED TO THE TOOL'S OWN SCRATCH DIRECTORY: a failed temp-delete is
    a FAILURE, never a swallowed `except`. SQLite's process-global state and pooled handles are the
    reason that rule exists; this tool holds no such handle, but a runner that quietly leaves a
    directory per run behind is the same leak wearing a different coat, and it is the leak this repo
    once paid 65.5 GB for.

    Two things are separated here on purpose. `replay_root` is the CALLER's directory and is never
    touched -- a `--replay-results-root` fixture must survive the run so the test can read it. `base`
    is only the parent this tool creates a uniquely-named child in, and it defaults to the platform
    temp dir. Both are parameters rather than implicit globals so a test can drive this function
    directly: when the deletion was inline in `main`, the only way to reach it was a full
    `dotnet build` plus four shard processes, and the contract was therefore untested.
    """
    if replay_root is not None:
        yield Path(replay_root)
        return
    root = Path(base) if base else Path(tempfile.gettempdir())
    root.mkdir(parents=True, exist_ok=True)
    scratch = root / f"test-sharded-{uuid.uuid4().hex}"
    scratch.mkdir(parents=True, exist_ok=True)
    try:
        yield scratch
    finally:
        if scratch.is_dir():
            shutil.rmtree(scratch)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run one test project as N concurrent `dotnet test` processes over a complete, "
                    "disjoint partition (replaces test-sharded.ps1).")
    parser.add_argument("--project", required=True,
                        help="the .csproj to run, relative to the root or absolute")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--extra-filter", default=None,
                        help="an additional VSTest filter ANDed onto every shard's own filter")
    parser.add_argument("--root", type=Path, default=None,
                        help="the repository (default: this tool's own repository)")
    parser.add_argument("--replay-results-root", default=None,
                        help="test-only: read each shard's TRX from here instead of running, and treat "
                             "every shard as exited 0")
    parser.add_argument("--registry-path", default=None, help="test-only registry override")
    parser.add_argument("--manifest-path", default=None, help="test-only shard-manifest override")
    parser.add_argument("--scratch-base", default=None,
                        help="test-only: the directory to create this run's scratch root in "
                             "(default: the platform temp dir)")
    parser.add_argument("--shard-timeout", type=int, default=DEFAULT_SHARD_TIMEOUT_SECONDS,
                        help=f"per-shard wall ceiling in seconds (default {DEFAULT_SHARD_TIMEOUT_SECONDS}; "
                             f"the original had NO timeout and could hang forever)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        registry = load_json(Path(args.registry_path) if args.registry_path
                             else root / "scripts" / "verification-boundaries.v1.json",
                             "REGISTRY")
        manifest_path = Path(args.manifest_path) if args.manifest_path \
            else root / "scripts" / "test-shards.v1.json"
        manifest = load_json(manifest_path, "MANIFEST")
        project_id = resolve_project_id(registry, root, args.project)
        shards, threads = load_shards(manifest, project_id, manifest_path.name)

        extra_filter = args.extra_filter
        if extra_filter is None:
            extra_filter = read_default_profile_filter(root)
            if extra_filter is None:
                raise Refusal("NO-DEFAULT-PROFILE-FILTER",
                              "scripts/test_fast.py yielded no FILTER assignment, so the "
                              "$Filter assignment; pass --extra-filter explicitly")

        # Every shard's filter is computed UP FRONT, so one manifest defect is named before a single
        # process starts rather than after one shard has already run. `shard_filter` owns the emptiness
        # refusal; the loop here is what makes the refusal EARLY, and the re-check below is a second
        # line of defence against `shard_filter` ever being loosened into returning "".
        named_shards = [s for s in shards if not s.remainder]
        for shard in shards:
            filt = shard_filter(shard, named_shards, extra_filter)
            if not filt.strip():
                raise Refusal("EMPTY-SHARD-FILTER",
                              f"shard '{shard.id}' produced an empty filter after the extra filter "
                              "was applied; this is a defect in shard_filter, not in the manifest")

        replayed = args.replay_results_root is not None
        with scratch_root(args.replay_results_root, base=args.scratch_base) as temp_root:
            if replayed:
                shard_runs = [ShardResult(s.id, 0, 0.0, 0, s.remainder) for s in shards]
            else:
                run_build(args.project, args.configuration, root, DEFAULT_BUILD_TIMEOUT_SECONDS)
                shard_runs = collect(args.project, args.configuration, shards, threads, root,
                                     temp_root, args.shard_timeout)
            outcome = analyse(shard_runs, shards, temp_root)
            outcome.replayed = replayed
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "shards": []}, indent=2))
        else:
            print(f"TEST-SHARDED REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    failed = bool(outcome.overlaps or outcome.empty_named
                  or any(s.exit_code != 0 for s in outcome.shards))
    if args.json:
        print(json.dumps({
            "tool": GUARD_ID,
            "verdict": "FAIL" if failed else "OK",
            "project_id": project_id,
            "replayed": outcome.replayed,
            "shards": [{"id": s.id, "exit_code": s.exit_code, "wall_seconds": round(s.wall, 3),
                        "tests": s.tests, "remainder": s.remainder} for s in outcome.shards],
            "total_tests": outcome.total_tests,
            "overlaps": outcome.overlaps,
            "empty_named_shards": outcome.empty_named,
        }, indent=2))
        return EXIT_FAILED if failed else EXIT_OK

    # INTERLEAVED, per shard, in manifest order -- exactly as the original emitted it. The port's first
    # version printed every shard's reading first and then every finding, which reads as one undirected
    # list; pairing each shard's line with its own verdict is what makes a four-shard failure diagnosable
    # without re-running anything.
    by_id = {s.id: s for s in outcome.shards}
    for shard in outcome.shards:
        # The shard line is a READING, so it is report and goes to stdout whatever the verdict; the
        # FAILED lines beside it are findings and go to stderr. The original sent both to stdout
        # through `Write-Host`, which is the difference this port's stream discipline exists to fix.
        print(f"  shard {shard.id}: exit {shard.exit_code}, {shard.wall:.1f}s, {shard.tests} tests")
        if shard.id in outcome.empty_named:
            print(f"TEST-SHARDED FAILED: named shard '{shard.id}' executed zero tests "
                  "(manifest defect \u2014 a prefix likely names no live test)", file=sys.stderr)
        if shard.exit_code != 0:
            print(f"TEST-SHARDED FAILED: shard '{shard.id}' exited {shard.exit_code}", file=sys.stderr)
    for overlap in outcome.overlaps:
        print(f"TEST-SHARDED FAILED: test ran in two shards: {overlap}", file=sys.stderr)
    if failed:
        return EXIT_FAILED
    print(f"TEST-SHARDED OK: {len(outcome.shards)} shards, {outcome.total_tests} tests, no overlap")
    return EXIT_OK




if __name__ == "__main__":
    sys.exit(main())
