#!/usr/bin/env python3
"""Default test profile -- the developer/agent loop. Runs the store test suite MINUS the file-bound
(`DiskSemantics`) and long-running (`Heavy`) tests, so a routine run writes nothing to the SSD and runs
no multi-minute case. This is the ONE place that owns the default filter, so the default cannot drift
between a developer and an agent. Standard: docs/contributing/testing-standard.md.

Replaces `test-fast.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **THERE WAS NO TIMEOUT ON ANY OF THE `dotnet test` INVOCATIONS.** The default profile runs 71 of
  them, each with `--blame-hang --blame-hang-timeout 15min`. `--blame-hang` is a *hang detector*, not a
  kill switch: it dumps and kills a hung TEST HOST, and says nothing about an MSBuild node waiting on a
  locked feed or a file handle. One wedged invocation held the whole loop open indefinitely and the
  only signal was the absence of the next line. Every call is now bounded by `--timeout`, and a timeout
  is a named refusal naming the project.

* **A FAILING PROJECT DID NOT STOP THE LOOP, AND THE EXIT CODE NAMED ONLY THE FIRST ONE.** The original
  ran all 71 and kept `$exitCode` from the first failure, so a run with 40 red projects exited with one
  project's code and printed 40 `DEFAULT PROFILE TEST FAILED` lines. A reader skimming the tail sees a
  single code and one name. The port collects every failing project, names all of them in the report
  and in the refusal, and still exits with the first non-zero so the code stays comparable.

* **THE LIST'S OWN COMMENT WAS FALSE, AND NOBODY HAD MEASURED IT.** It claimed `core-split-wiring`
  "keeps this list in full ... a Core split increment that forgot this list would silently drop the
  moved tests". Measured 2026-09-28: 0 of 71 entries are stale, but **11 of the 84 test projects on disk
  are absent from the list, and 11 of those 11 are wired into `ci.yml`** -- including
  `FusionRpg.Guard.Tests`, with 117 test files. So the local profile is NOT a proxy for CI, and the
  comment described an intent the list did not have.

WHAT THIS TOOL DOES ABOUT IT, AND WHAT IT DOES NOT
---------------------------------------------------
It does not quietly add the 11. Adding them changes what every ordinary agent run costs, and that is a
product decision, not a porting detail -- so the asymmetry is REPORTED here, in `--json`, and in the
commit that retires the original, and it is left for the owner to rule on.

What the port DOES do is make the set CLOSED, so forgetting is no longer silent. `DECLARED_EXCLUSIONS`
names every project that is deliberately not in the default profile, each with a reason, and a project
that is on disk and in neither list is an `UNDECIDED-PROJECT` refusal. A new test project can no longer
join the repository without someone deciding whether the default profile runs it -- which is exactly the
property the original comment claimed and the original list did not have.

  * `FusionRpg.Bench` is an Exe with no `Microsoft.NET.Test.Sdk` and no test files, so it is not a test
    project at all and its absence from a test list is correct rather than a gap.
  * `FusionRpg.Injector.Tests` needs interop references, needs `FUSIONRPG_GAME_DIR`, and is deliberately
    NOT in `ci.yml` either -- so its absence from the local profile agrees with CI rather than diverging.

THE FILTER IS SCRAPED FROM THIS FILE'S SOURCE, AND THAT IS A KNOWN WEAKNESS
------------------------------------------------------------------------
`verify-change.py` and `test_sharded.py` both read the default filter by regexing this file's text
(`^\\s*FILTER\\s*=\\s*"..."`), which is how the PowerShell original was read too. That is a source-scraped
constant: it breaks on a rename, and it would break silently. The port keeps the spelling the readers
already look for -- a module-level `FILTER = "..."` on its own line -- so both readers work unchanged in
shape, and both are dialect-aware. Replacing the scrape with an import is the right follow-up and is
recorded as such rather than smuggled in here: an import would make every reader execute this module, and
`test_sharded.py` is imported BY the sharding tests, so the coupling has to be deliberate.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "test-fast"
EXIT_OK = 0
EXIT_FAILED = 1
# The substrate gate's own vocabulary, carried through unchanged: a caller that scripted the old exit
# code 64 must still see 64 for "the gate refused", not a generic 1 that could be any failure.
EXIT_SUBSTRATE_REFUSED = 64

# The default profile: everything except the two excluded categories. A negative filter includes
# uncategorized tests (verified -- docs/architecture/data-test-substrate/spec-test-profiles.md section 1),
# so only the excluded tests carry a trait.
#
# THE SPELLING IS A CONTRACT. `verify-change.py` and `test_sharded.py` scrape this line by regex; it must
# stay a module-level assignment of a double-quoted string literal on its own line.
FILTER = "Category!=DiskSemantics&Category!=Heavy"

# Where the substrate gate lives, in preference order. The guard is already Python; the `.ps1` spelling
# is retained only so this tool keeps working if the guard is ever re-pointed, and a refusal says so
# rather than running nothing.
SUBSTRATE_GUARD_SPELLINGS = ("guard-test-substrate.py", "guard-test-substrate.ps1")

DEFAULT_TIMEOUT = 1800

# The store test projects, Data first (it holds the majority of store sites), then every Core test
# project `core-split-wiring` moves. ORDERED deliberately: the order is the run order, so the cheapest
# signal lands first.
DEFAULT_PROJECTS: tuple[str, ...] = (
    "tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj",
    "tests/FusionRpg.Server.Tests/FusionRpg.Server.Tests.csproj",
    "tests/FusionRpg.E2E.Tests/FusionRpg.E2E.Tests.csproj",
    "tests/FusionRpg.Core.AchievementTitlesTuningTests.Tests/FusionRpg.Core.AchievementTitlesTuningTests.Tests.csproj",
    "tests/FusionRpg.Core.ActorHub.Tests/FusionRpg.Core.ActorHub.Tests.csproj",
    "tests/FusionRpg.Core.ActorSurface.Tests/FusionRpg.Core.ActorSurface.Tests.csproj",
    "tests/FusionRpg.Core.Atoms.Tests/FusionRpg.Core.Atoms.Tests.csproj",
    "tests/FusionRpg.Core.Aura.Tests/FusionRpg.Core.Aura.Tests.csproj",
    "tests/FusionRpg.Core.Balance.Tests/FusionRpg.Core.Balance.Tests.csproj",
    "tests/FusionRpg.Core.BoardProjectionTests.Tests/FusionRpg.Core.BoardProjectionTests.Tests.csproj",
    "tests/FusionRpg.Core.CapPolicyTests.Tests/FusionRpg.Core.CapPolicyTests.Tests.csproj",
    "tests/FusionRpg.Core.ClassSystem.Tests/FusionRpg.Core.ClassSystem.Tests.csproj",
    "tests/FusionRpg.Core.CombatCounterTests.Tests/FusionRpg.Core.CombatCounterTests.Tests.csproj",
    "tests/FusionRpg.Core.CombatDotTests.Tests/FusionRpg.Core.CombatDotTests.Tests.csproj",
    "tests/FusionRpg.Core.CombatFanoutTests.Tests/FusionRpg.Core.CombatFanoutTests.Tests.csproj",
    "tests/FusionRpg.Core.CombatHitEmitPolicyTests.Tests/FusionRpg.Core.CombatHitEmitPolicyTests.Tests.csproj",
    "tests/FusionRpg.Core.Commanders.Tests/FusionRpg.Core.Commanders.Tests.csproj",
    "tests/FusionRpg.Core.DamageFxPaletteTests.Tests/FusionRpg.Core.DamageFxPaletteTests.Tests.csproj",
    "tests/FusionRpg.Core.Diagnostics.Tests/FusionRpg.Core.Diagnostics.Tests.csproj",
    "tests/FusionRpg.Core.Dungeon.Tests/FusionRpg.Core.Dungeon.Tests.csproj",
    "tests/FusionRpg.Core.EffectClock.Tests/FusionRpg.Core.EffectClock.Tests.csproj",
    "tests/FusionRpg.Core.EffectEventAdapterCoreTests.Tests/FusionRpg.Core.EffectEventAdapterCoreTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectGrantSessionRecorderTests.Tests/FusionRpg.Core.EffectGrantSessionRecorderTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectGrantSessionTests.Tests/FusionRpg.Core.EffectGrantSessionTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectOfflineKitTests.Tests/FusionRpg.Core.EffectOfflineKitTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectPluginHostTests.Tests/FusionRpg.Core.EffectPluginHostTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectPluginLifecycleTests.Tests/FusionRpg.Core.EffectPluginLifecycleTests.Tests.csproj",
    "tests/FusionRpg.Core.EffectScenarioRunnerTests.Tests/FusionRpg.Core.EffectScenarioRunnerTests.Tests.csproj",
    "tests/FusionRpg.Core.Effects.Tests/FusionRpg.Core.Effects.Tests.csproj",
    "tests/FusionRpg.Core.Events.Tests/FusionRpg.Core.Events.Tests.csproj",
    "tests/FusionRpg.Core.Expeditions.Tests/FusionRpg.Core.Expeditions.Tests.csproj",
    "tests/FusionRpg.Core.GameProfileConstantsTests.Tests/FusionRpg.Core.GameProfileConstantsTests.Tests.csproj",
    "tests/FusionRpg.Core.Hud.Tests/FusionRpg.Core.Hud.Tests.csproj",
    "tests/FusionRpg.Core.Items.Tests/FusionRpg.Core.Items.Tests.csproj",
    "tests/FusionRpg.Core.Lawn.Tests/FusionRpg.Core.Lawn.Tests.csproj",
    "tests/FusionRpg.Core.LawnCoordMathTests.Tests/FusionRpg.Core.LawnCoordMathTests.Tests.csproj",
    "tests/FusionRpg.Core.Match.Tests/FusionRpg.Core.Match.Tests.csproj",
    "tests/FusionRpg.Core.MatchAdmitTests.Tests/FusionRpg.Core.MatchAdmitTests.Tests.csproj",
    "tests/FusionRpg.Core.MatchInjectContractTests.Tests/FusionRpg.Core.MatchInjectContractTests.Tests.csproj",
    "tests/FusionRpg.Core.MatchRuntimeTests.Tests/FusionRpg.Core.MatchRuntimeTests.Tests.csproj",
    "tests/FusionRpg.Core.MatchValidatorTests.Tests/FusionRpg.Core.MatchValidatorTests.Tests.csproj",
    "tests/FusionRpg.Core.Notify.Tests/FusionRpg.Core.Notify.Tests.csproj",
    "tests/FusionRpg.Core.OnboardingCheckpointEvaluatorTests.Tests/FusionRpg.Core.OnboardingCheckpointEvaluatorTests.Tests.csproj",
    "tests/FusionRpg.Core.Overlay.Tests/FusionRpg.Core.Overlay.Tests.csproj",
    "tests/FusionRpg.Core.OverlayApplyGuardTests.Tests/FusionRpg.Core.OverlayApplyGuardTests.Tests.csproj",
    "tests/FusionRpg.Core.OverlayProcTests.Tests/FusionRpg.Core.OverlayProcTests.Tests.csproj",
    "tests/FusionRpg.Core.PassiveTree.Tests/FusionRpg.Core.PassiveTree.Tests.csproj",
    "tests/FusionRpg.Core.Power.Tests/FusionRpg.Core.Power.Tests.csproj",
    "tests/FusionRpg.Core.Progression.Tests/FusionRpg.Core.Progression.Tests.csproj",
    "tests/FusionRpg.Core.PvzActivityRollupBuilderTests.Tests/FusionRpg.Core.PvzActivityRollupBuilderTests.Tests.csproj",
    "tests/FusionRpg.Core.PvzStatsApplyGateTests.Tests/FusionRpg.Core.PvzStatsApplyGateTests.Tests.csproj",
    "tests/FusionRpg.Core.PvzStatsSheetComposerTests.Tests/FusionRpg.Core.PvzStatsSheetComposerTests.Tests.csproj",
    "tests/FusionRpg.Core.RpgProgressionBalanceTests.Tests/FusionRpg.Core.RpgProgressionBalanceTests.Tests.csproj",
    "tests/FusionRpg.Core.RpgXpApplyTests.Tests/FusionRpg.Core.RpgXpApplyTests.Tests.csproj",
    "tests/FusionRpg.Core.RpgXpAwardMapTests.Tests/FusionRpg.Core.RpgXpAwardMapTests.Tests.csproj",
    "tests/FusionRpg.Core.Saves.Tests/FusionRpg.Core.Saves.Tests.csproj",
    "tests/FusionRpg.Core.SimEngineMatchIsolationTests.Tests/FusionRpg.Core.SimEngineMatchIsolationTests.Tests.csproj",
    "tests/FusionRpg.Core.SimEngineMatchOverlayTests.Tests/FusionRpg.Core.SimEngineMatchOverlayTests.Tests.csproj",
    "tests/FusionRpg.Core.SimEngineShieldTests.Tests/FusionRpg.Core.SimEngineShieldTests.Tests.csproj",
    "tests/FusionRpg.Core.StatMathAndSimTests.Tests/FusionRpg.Core.StatMathAndSimTests.Tests.csproj",
    "tests/FusionRpg.Core.StatSystemTests.Tests/FusionRpg.Core.StatSystemTests.Tests.csproj",
    "tests/FusionRpg.Core.Stats.Tests/FusionRpg.Core.Stats.Tests.csproj",
    "tests/FusionRpg.Core.Status.Tests/FusionRpg.Core.Status.Tests.csproj",
    "tests/FusionRpg.Core.TargetResolverTests.Tests/FusionRpg.Core.TargetResolverTests.Tests.csproj",
    "tests/FusionRpg.Core.TitleLifecyclePolicyTests.Tests/FusionRpg.Core.TitleLifecyclePolicyTests.Tests.csproj",
    "tests/FusionRpg.Core.UniqueBindingsTests.Tests/FusionRpg.Core.UniqueBindingsTests.Tests.csproj",
    "tests/FusionRpg.Core.UniqueEquipmentCatalogTests.Tests/FusionRpg.Core.UniqueEquipmentCatalogTests.Tests.csproj",
    "tests/FusionRpg.Core.Vfx.Tests/FusionRpg.Core.Vfx.Tests.csproj",
    "tests/FusionRpg.Core.Vocabulary.Tests/FusionRpg.Core.Vocabulary.Tests.csproj",
    "tests/FusionRpg.Core.Workspace.Tests/FusionRpg.Core.Workspace.Tests.csproj",
    "tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj",
)

# Every test project deliberately NOT in the default profile, with the reason. This table is the fix for
# the claim the original list could not support: a project on disk that appears in NEITHER list is an
# `UNDECIDED-PROJECT` refusal, so a new test project cannot arrive without someone deciding.
#
# The first eleven ARE wired into `.github/workflows/ci.yml` and are NOT here. That is the measured
# asymmetry, recorded rather than silently repaired -- adding them changes what an ordinary agent run
# costs, which is the owner's call and not a porting detail. See the module docstring.
DECLARED_EXCLUSIONS: dict[str, str] = {
    "tests/FusionRpg.AtomImporter.Tests/FusionRpg.AtomImporter.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.CheatCore.Tests/FusionRpg.CheatCore.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.Core.ElementEnumGen.Tests/FusionRpg.Core.ElementEnumGen.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.FileMove.Tests/FusionRpg.FileMove.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.Guard.Tests/FusionRpg.Guard.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28) -- 117 test files, the "
        "largest asymmetry found",
    "tests/FusionRpg.ItemSeedValidator.Tests/FusionRpg.ItemSeedValidator.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.Launcher.Tests/FusionRpg.Launcher.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.PassiveTreeRosterGen.Tests/FusionRpg.PassiveTreeRosterGen.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.SquadHarness.Tests/FusionRpg.SquadHarness.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.TestSplitAnalyzer.Tests/FusionRpg.TestSplitAnalyzer.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.TreeBinder.Tests/FusionRpg.TreeBinder.Tests.csproj":
        "in ci.yml, absent from the default local profile (measured 2026-09-28)",
    "tests/FusionRpg.Bench/FusionRpg.Bench.csproj":
        "not a test project: an Exe with no Microsoft.NET.Test.Sdk and no test files, so its absence "
        "from a test list is correct rather than a gap",
    "tests/FusionRpg.Injector.Tests/FusionRpg.Injector.Tests.csproj":
        "needs interop references and FUSIONRPG_GAME_DIR, and is deliberately NOT in ci.yml either, so "
        "its absence here agrees with CI rather than diverging from it",
}

STAGES = ("scope", "substrate-gate", "projects", "tests")


class Refusal(Exception):
    """A named precondition or stage failure. The run says WHICH stage, and never exits 0 having not run."""

    def __init__(self, stage: str, reason: str, detail: str = "", exit_code: int = EXIT_FAILED) -> None:
        super().__init__(f"{stage}/{reason}: {detail}" if detail else f"{stage}/{reason}")
        self.stage = stage
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


def run(argv: list[str], cwd: Path, timeout: int, stage: str) -> tuple[int, str]:
    """The ONLY place an external command is invoked. Returns (exit code, combined output).

    NOT RAISING ON NON-ZERO, and the difference is deliberate: the original ran all 71 projects and kept
    the first failure's code, so a run with many reds reported one. Raising here would stop at the first
    and lose the rest; so the caller collects every failing project, and the refusal names all of them
    while still exiting with the first non-zero so the code stays comparable with the original.
    """
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
    except subprocess.TimeoutExpired:
        raise Refusal(stage, "TIMEOUT", f"{' '.join(argv[:4])} did not exit within {timeout}s")
    except FileNotFoundError as exc:
        raise Refusal(stage, "NOT-ON-PATH", f"{argv[0]}: {exc}")
    except OSError as exc:
        raise Refusal(stage, "SPAWN-FAILED", f"{' '.join(argv[:4])}: {exc}")
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def relative_to(path: str, root: Path) -> str:
    """The repository-relative form of an absolute path, or the input unchanged if it is not under root.

    Falling back rather than raising matters: a caller may pass an absolute path OUTSIDE the root, which
    `resolve_test_project` accepts, and the report must still be able to name it.
    """
    try:
        return Path(path).resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path


def resolve_test_project(root: Path, requested: str) -> Path:
    """A `.csproj` path OR its directory -- the spec's own example passes a directory."""
    candidate = Path(requested)
    full = candidate if candidate.is_absolute() else root / candidate
    if full.is_dir():
        projects = sorted(full.glob("*.csproj"))
        if not projects:
            raise Refusal("projects", "NO-CSPROJ-IN-DIRECTORY", f"no .csproj found in {full}")
        return projects[0]
    if not full.exists():
        raise Refusal("projects", "PROJECT-NOT-FOUND", f"test project not found: {full}")
    return full.resolve()


def discover_test_projects(root: Path) -> list[str]:
    """Every `tests/*/*.csproj` on disk, repository-relative. A reading, not a constant."""
    return sorted(p.relative_to(root).as_posix() for p in (root / "tests").glob("*/*.csproj"))


def undecided_projects(root: Path) -> list[str]:
    """Test projects on disk that are in NEITHER the default list NOR the declared exclusions.

    This is the closure check. The original list's comment claimed a Core split increment that forgot it
    would be caught; it would not have been, because nothing looked. Today this returns empty, so the
    refusal is free -- and a new project cannot land without a decision.
    """
    known = set(DEFAULT_PROJECTS) | set(DECLARED_EXCLUSIONS)
    return [p for p in discover_test_projects(root) if p not in known]


def resolve_substrate_guard(root: Path) -> Path:
    """The static substrate gate. FAIL CLOSED: a gate that silently does not run is worse than none.

    The gate reads SOURCE, so it is profile-independent and cannot false-positive on the file-bound
    directories the default profile legitimately writes. The RUNTIME alarm deliberately stays on `full`.
    """
    for name in SUBSTRATE_GUARD_SPELLINGS:
        candidate = root / "scripts" / name
        if candidate.is_file():
            if name.endswith(".py") and shutil.which("python") is None:
                raise Refusal("substrate-gate", "PYTHON-NOT-ON-PATH",
                              f"{candidate} exists but python is not on PATH", EXIT_SUBSTRATE_REFUSED)
            return candidate
    raise Refusal("substrate-gate", "GUARD-MISSING",
                  f"guard-test-substrate is neither {' nor '.join(SUBSTRATE_GUARD_SPELLINGS)} "
                  f"under {root / 'scripts'}", EXIT_SUBSTRATE_REFUSED)


@dataclass
class Report:
    """What the run did. Every count is re-measured; none is a constant carried forward."""

    configuration: str = ""
    filter: str = ""
    substrate_guard: str = ""
    substrate_exit: int = 0
    projects: list[str] = field(default_factory=list)
    failed_projects: list[str] = field(default_factory=list)
    first_failure_code: int = 0
    undecided: list[str] = field(default_factory=list)
    stages: list[str] = field(default_factory=list)


def run_profile(root: Path, projects: list[str], configuration: str, timeout: int) -> Report:
    """The whole pipeline. Raises on the first NAMED failure; collects every test failure."""
    # Every path in the REPORT is repository-relative, for the same reason the failure list is: a report
    # a reader cannot paste back into a command is a report that has to be re-typed by hand.
    report = Report(configuration=configuration, filter=FILTER,
                    projects=[relative_to(p, root) for p in projects])

    guard = resolve_substrate_guard(root)
    report.substrate_guard = str(guard)
    code, _ = run([sys.executable, str(guard)], root, timeout, "substrate-gate") \
        if guard.suffix == ".py" else run([str(guard)], root, timeout, "substrate-gate")
    report.substrate_exit = code
    report.stages.append("substrate-gate")
    if code != 0:
        raise Refusal("substrate-gate", "GATE-FAILED",
                      f"{guard.name} exited {code} -- a test leaks or swears a temp delete", code)

    first_code = 0
    for project in projects:
        code, _ = run(["dotnet", "test", project, "-c", configuration, "--verbosity", "minimal",
                       "--filter", FILTER, "--blame-hang", "--blame-hang-timeout", "15min"],
                      root, timeout, "tests")
        if code != 0:
            # The refusal names the project the way the CALLER wrote it -- repository-relative -- while
            # `dotnet` receives the absolute path it needs. An absolute path in a failure message is
            # something a reader can copy but not act on, and it differs between two checkouts of the same
            # tree, so the same failure yields two different log lines.
            report.failed_projects.append(relative_to(project, root))
            if first_code == 0:
                first_code = code
    report.first_failure_code = first_code
    report.stages.append("tests")

    if first_code:
        listed = "\n  ".join(report.failed_projects)
        raise Refusal("tests", "PROJECTS-FAILED",
                      f"{len(report.failed_projects)} of {len(projects)} project(s) failed "
                      f"(filter: {FILTER}):\n  {listed}", first_code)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Default test profile: the dev/agent loop (replaces test-fast.ps1).")
    parser.add_argument("--project", action="append", default=[], metavar="PATH",
                        help="a test project directory or .csproj; repeatable")
    parser.add_argument("--all-default", action="store_true",
                        help="intentional broad local validation over the default project list")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--root", type=Path, default=None)
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds per external command (default {DEFAULT_TIMEOUT}; the original "
                             f"had NO timeout on any of its 71 dotnet test invocations)")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    root = (args.root or Path(__file__).resolve().parent.parent).resolve()

    # SCOPE VALIDATION FIRST, deliberately (test-verification-boundary TVB4.1). A run with neither
    # --project nor --all-default is refused regardless of the substrate gate's state, so an unrelated
    # gate failure elsewhere in the tree cannot mask this refusal, and a call that was always going to be
    # rejected never pays for an external process it did not need. Found live: a real, unrelated
    # guard-test-substrate failure on another lane's tree made a no-argument run print only the gate
    # banner, hiding this message.
    if not args.project and not args.all_default:
        detail = ("a path-scoped test requires --project. Use scripts/verify-change.py --paths "
                  "<changed files>, or pass --all-default for intentional broad local validation")
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": "scope",
                              "reason": "SCOPE-REQUIRED", "detail": detail}, indent=2))
        else:
            print(f"TEST-FAST REFUSED [scope]: SCOPE-REQUIRED: {detail}", file=sys.stderr)
        return EXIT_FAILED
    if args.project and args.all_default:
        detail = "choose --project or --all-default, not both"
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": "scope",
                              "reason": "SCOPE-CONFLICT", "detail": detail}, indent=2))
        else:
            print(f"TEST-FAST REFUSED [scope]: SCOPE-CONFLICT: {detail}", file=sys.stderr)
        return EXIT_FAILED

    undecided = undecided_projects(root)
    try:
        if undecided:
            raise Refusal("scope", "UNDECIDED-PROJECT",
                          "these test projects are on disk and in neither DEFAULT_PROJECTS nor "
                          "DECLARED_EXCLUSIONS, so no one has decided whether the default profile runs "
                          "them:\n  " + "\n  ".join(undecided))
        requested = DEFAULT_PROJECTS if args.all_default else tuple(args.project)
        projects = [str(resolve_test_project(root, p)) for p in requested]
        report = run_profile(root, projects, args.configuration, args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": refusal.stage,
                              "reason": refusal.reason, "detail": refusal.detail,
                              "undecided": undecided}, indent=2))
        else:
            print(f"TEST-FAST REFUSED [{refusal.stage}]: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK",
                          **report.__dict__}, indent=2))
        return EXIT_OK
    print("==> Test profile: default (fast)")
    print(f"    filter: {report.filter}")
    print(f"    substrate gate: {Path(report.substrate_guard).name} (exit {report.substrate_exit})")
    print(f"    projects: {len(report.projects)}")
    for project in report.projects:
        print(f"      {project}")
    print()
    print(f"Default test profile OK (filter: {report.filter})")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
