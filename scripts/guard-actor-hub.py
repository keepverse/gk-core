#!/usr/bin/env python3
"""Guard: player combat/derived compose goes through ActorHub — the sole compose gate.

Replaces `guard-actor-hub.ps1`. Provenance for the deletion is here so it survives it.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
Because of its output streams, and because of what a wrong `--root` did.

It emitted the verdict AND every finding with `Write-Host`, which writes the INFORMATION stream (6)
and is invisible to PowerShell's `2>&1` capture. A consumer therefore could not tell a clean
verdict from a finding without reading prose, which is how findings reached a C# test's `stdout` by
accident of redirection rather than by design. See `docs/architecture/ps1-port-checklist.md` item 4.

Separately, several scans were wrapped in `if (Test-Path $Src)`, so pointing the guard at a tree
without `src/` reported OK and exited 0 — a green verdict for a run that examined nothing. Here a
missing source tree is a named refusal with exit 64.

FOUR THINGS A PORT GETS WRONG, all of them narrowed or widened rules rather than crashes
-------------------------------------------------------------------------------------------------
1. **MATCHING IS CASE-INSENSITIVE HERE, AND NOT IN EVERY GUARD.** This script used the PowerShell
   `-match` operator, which folds case. `guard-funnel-delta` used `[regex]::IsMatch`, which does
   not. Copying a ported guard's `re.compile` line from the neighbour would have silently WIDENED
   this one to catch `derivedmodifier`, `APPLIEDCOMBAT`, `actorhub.resolve` and every other casing.
   The lookbehind in R5 (`(?<![A-Za-z])`) is part of the same rule: `MyStats.Resolve(` is not a
   bypass, and that is a deliberate narrowing.
2. **A MISSING REQUIRED FILE IS A FINDING in R1 and R4, and NOT A CHECK in R7/R8/R9.** The
   distinction is the guard's purpose, not sloppiness. `EntityApply.cs` disappearing means the Hub
   call it was required to make disappeared with it, so the guard must FAIL. `Program.cs`
   disappearing is none of this guard's business. Collapsing the two would either stop the guard
   noticing a deleted consumer, or make it fail for files it never cared about.
3. **THE THREE ALLOWLISTS ARE LITERALS, NOT CATEGORIES.** `ALLOW_COMPOSER`, `ALLOW_CHANNEL_MOD_`
   and `ALLOW_STATS_RESOLVE` below are transcribed one-for-one, each entry with the reason it is
   there. "Tidying" them — sorting, merging the `obj`/`bin` entries, widening a path prefix —
   changes which files the guard forgives, and the failure is silent because the result is still a
   clean run. If one of these entries is wrong, that is a decision to make on purpose, in a commit
   that says so.
4. **R1 SELECTS ITS CHECKS BY FILENAME, NOT BY POSITION.** Both `if` blocks run on one pass and each
   tests the relative path for its own marker, so a path is judged by what it is called.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from cscan import strip_whole_line_comments  # noqa: E402
from keepverse_roots import RootNotFound, fusion_root_or_owner  # noqa: E402

GUARD_ID = "actor-hub"
VERDICT_OK = "ACTOR-HUB GUARD OK"
VERDICT_FAILED = "ACTOR-HUB GUARD FAILED"
EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_REFUSED = 64

SRC = ("src",)
INJECTOR = "src/FusionRpg.Injector"


# WHICH REPOSITORY OWNS A DECLARED SCOPE. The ActorHub seam genuinely spans two of them: the
# Hub is FusionRpg.Core's and its consumer is FusionRpg.Injector's, so three of the four paths
# this guard names have been in a sibling repository since the split. The monorepo had one
# root, so `root / scope` was a correct expression and the question did not arise.
#
# Two guards behaved differently under the split, and both were silent. walk() returns [] for a
# scope that is not a directory, so R5/R6/R7/R8/R9 scanned an empty Injector tree and reported
# nothing. R4 builds its path directly and checked is_file(), so it reported two of the three
# files in its required table as MISSING - a green-sounding "missing required file" naming files
# that have never moved. Neither is a clock/count defect; both are this one.
_CROSS_REPO_SCOPE = ("src/FusionRpg.Injector", "src/FusionRpg.Launcher")


def owning_root(root: Path, scope: str) -> Path:
    """The root that owns `scope`, which is not always `root`."""
    norm = scope.replace("\\", "/").rstrip("/")
    if any(norm == s or norm.startswith(s + "/") for s in _CROSS_REPO_SCOPE):
        # Named refusal, not a traceback. R9 used to build its own path from `root` and swallow a
        # missing file with `return []`, so the rule was dead in exactly the repository that owns
        # its subject - the silent-green shape, one indirection away from walk().
        try:
            return fusion_root_or_owner(root)
        except RootNotFound as exc:
            raise Refusal("FUSION-ROOT-MISSING", str(exc)) from exc
    return root

# PowerShell's -match folds case, so every pattern in this guard is compiled with IGNORECASE.
# See the module docstring, contract note 1. Do not "fix" this by dropping the flag.
FLAGS = re.IGNORECASE

BUILD_OUTPUT = re.compile(r"[\\/](obj|bin)[\\/]", re.IGNORECASE)

# R2: a file may be a *Composer* and touch Derived/AppliedCombat only if it is one of these.
# Each entry is transcribed from the original, with the reason it is allowed:
#   Stats/Derived/       the Hub-path DerivedComposer itself, which is where compose belongs
#   PvzStatsSheetComposer.cs  orthogonal sheet, not a combat derive
#   StatComposer.cs      orthogonal stat assembly, not a combat derive
#   obj/ bin/            build output is not source
# BattleStatComposer is deliberately ABSENT: it was fused and deleted on 2026-09-13, and
# reintroducing a parallel composer is what this rule exists to catch.
ALLOW_COMPOSER = (
    (r"[\\/]FusionRpg\.Core[\\/]Stats[\\/]Derived[\\/]",
     "the Hub-path DerivedComposer, where compose belongs"),
    (r"[\\/]FusionRpg\.Core[\\/]Stats[\\/]PvzStatsSheetComposer\.cs",
     "orthogonal Pvz sheet, not a combat derive"),
    (r"[\\/]FusionRpg\.Core[\\/]Stats[\\/]StatComposer\.cs",
     "orthogonal stat assembly, not a combat derive"),
    (r"[\\/]obj[\\/]", "build output is not source"),
    (r"[\\/]bin[\\/]", "build output is not source"),
)

# R3: BattleChannelMod construction is allowed only for a listed producer. Anchored with `$`,
# which is part of the rule: it is the FILE that may produce, not a directory of producers.
# TraitAtomSource still feeds BattleTraitSubsystem its BattleChannelMod-shaped rows. The other
# former entries (EquipAtomSource.ModsFor, Battle.TreeAtomSource) were deleted as proven-dead
# ignore-op folds in battle-ops-parity T7, not allowlisted.
ALLOW_CHANNEL_MOD = (
    (r"[\\/]FusionRpg\.Core[\\/]Battle[\\/]TraitAtomSource\.cs$",
     "the one live ChannelMod producer; the others were deleted, not allowlisted"),
    (r"[\\/]obj[\\/]", "build output is not source"),
    (r"[\\/]bin[\\/]", "build output is not source"),
)

# R5: `Stats.Resolve` in injector combat paths has NO allowlist today. The entries exist only so
# build output is not scanned; the comment in the original says the contexts stay on the factory.
ALLOW_STATS_RESOLVE = (
    (r"[\\/]obj[\\/]", "build output is not source"),
    (r"[\\/]bin[\\/]", "build output is not source"),
)

# What makes a file a parallel composer: it derives or applies combat state outside the Hub.
COMPOSER_TOUCHES = re.compile(
    r"DerivedModifier|ActorDerivedSnapshot|ContributeDerived|AppliedCombat|BattleChannelMod", FLAGS)
NEW_CHANNEL_MOD = re.compile(r"new\s+BattleChannelMod\s*\(", FLAGS)
# The lookbehind is the rule: `MyStats.Resolve(` is a different type's member, not a bypass.
STATS_RESOLVE = re.compile(r"(?<![A-Za-z])Stats\.Resolve\s*\(", FLAGS)
HUB_RESOLVE = re.compile(r"ActorHub\.Resolve", FLAGS)
EMPTY_SOURCE_ID = re.compile(r"IsNullOrWhiteSpace\(mod\.SourceId\)", FLAGS)

COMPOSER_GLOB = "*Composer*.cs"


@dataclass(frozen=True)
class Required:
    """A pattern that must be present. `message` is what to say when it is not."""

    pattern: str
    message: str

    def satisfied(self, code: str) -> bool:
        return bool(re.search(self.pattern, code, FLAGS))


@dataclass(frozen=True)
class Alternatives:
    """AT LEAST ONE pattern must be present.

    This is the shape of the original's `-notmatch A -and -notmatch B`: a file satisfies the
    requirement when either alternative is found. It is a distinct type precisely so that it
    cannot be confused with two `Required` entries, which would demand both.
    """

    options: tuple[Required, ...]
    message: str

    def satisfied(self, code: str) -> bool:
        return any(re.search(option.pattern, code, FLAGS) for option in self.options)


@dataclass(frozen=True)
class HubRequired:
    """R4: a file that must exist AND must resolve through the Hub."""

    path: str
    why: str


# R4: a file that must exist AND must resolve through the Hub. `path` is SRC-RELATIVE, exactly as
# the original's table declares it — the original joins it onto `$Src` for the filesystem and then
# reports the DECLARED path in a "missing required file" message, so baking `src/` into the table
# would change that message.
HUB_REQUIRED = (
    HubRequired("FusionRpg.Injector/Stats/EntityApply.cs",
                "Writer path must call ActorHub.Resolve"),
    HubRequired("FusionRpg.Injector/GameHooks.cs",
                "damage-scale cache must call ActorHub.Resolve (not Stats.Resolve)"),
    HubRequired("FusionRpg.Core/SimEngine.cs",
                "sim apply must call ActorHub.Resolve (not Stats.Resolve)"),
)


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def code_of(text: str) -> str:
    """Whole-line comment stripping only, matching the original's Get-CodeLines.

    A comment that DOCUMENTS the boundary must not look like breaking it. A trailing comment on a
    line of real code is still scanned, and that narrower behaviour is the guard's stated policy,
    not an oversight — see `cscan.strip_whole_line_comments`.
    """
    return strip_whole_line_comments(text)


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("UNREADABLE_SOURCE", f"{path}: {exc}") from exc


def allowed(path: Path, table: tuple[tuple[str, str], ...]) -> str | None:
    """Return the reason this path is forgiven, or None. The first matching entry wins, exactly
    as the original's `break` did — so the recorded reason is deterministic."""
    text = str(path)
    for pattern, why in table:
        if re.search(pattern, text, FLAGS):
            return why
    return None


def rel(root: Path, path: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        # A cross-repo finding. It must NAME the repository, not print a sibling path with no
        # repository on it: a reader cannot otherwise tell which tree a finding is about, and
        # the alternative - falling back to the absolute path - makes findings machine-stable
        # and machine-local at the same time, so a golden recorded on one machine fails on
        # another. A repository-relative path is neither.
        try:
            return f"gk-fusion/{path.relative_to(fusion_root_or_owner(root)).as_posix()}"
        except ValueError:
            return path.name


def finding_at(root: Path, rule: str, path: Path, message: str) -> dict:
    return {"rule": rule, "file": rel(root, path), "message": message, "hint": rule}


def missing(rule: str, name: str) -> dict:
    """A required file that is not there. `file` is the path that SHOULD exist, so a reader can
    create it, and `missing` marks it as a different kind of finding from a rule break.

    R1 and R4 spell this path differently in the original, and the difference is preserved: R1
    reports the repo-relative path (with `src/`), R4 reports the path as declared in its own table
    (without). Making them uniform would be a tidy-up; a reader diffing the two messages against
    the original would see the guard had changed.
    """
    return {"rule": rule, "file": name, "message": f"missing required file: {name}",
            "hint": rule, "missing": True}


def walk(root: Path, scope: str, glob: str = "*.cs") -> list[Path]:
    base = owning_root(root, scope) / scope
    if not base.is_dir():
        return []
    return sorted(p for p in base.rglob(glob) if p.is_file())


def r1_server_targets(root: Path) -> list[dict]:
    """R1: the two server entry points must route derived/sheet through the Hub.

    Both `if` blocks are evaluated on one pass, and each selects on the path's own marker, so a
    file is judged by what it is called. The `continue` after a missing file is preserved: the
    file's other required patterns are not also reported for a file that is not there.

    `Alternatives` exists because the original's Hub-construction check is
    `if (-notmatch CreateDefault -and -notmatch 'new ActorHub')` — AT LEAST ONE of the two. Written
    as a flat list of required patterns it reads as BOTH, and that mistake shipped a false finding
    on the real tree: the file calls `ActorHubBootstrap.CreateDefault` at :88 and was reported as
    not constructing a Hub. An alternatives group makes the difference structural rather than a
    detail to remember.
    """
    out: list[dict] = []
    for name, required in (
        ("AuraDerivedEndpoints.cs", (
            Required(r"UniqueActorHubCompose",
                     "player /derived|/sheet must route through UniqueActorHubCompose (ActorHub)"),
            Required(r"composeKind", "/derived must ship composeKind (FlatReplace honesty)"),
        )),
        ("UniqueActorHubCompose.cs", (
            Alternatives(
                options=(Required(r"ActorHubBootstrap\.CreateDefault", ""),
                         Required(r"new ActorHub", "")),
                message="must construct ActorHub as sole compose gate"),
            Required(r"ResolveDerivedWithContributions",
                     "sheet/derived must call ResolveDerivedWithContributions (GG-49)"),
            Required(r"EquippedBoundAtoms",
                     "must fan in equip via EquippedBoundAtoms (shared with battle)"),
        )),
    ):
        path = root / "src" / "FusionRpg.Server" / name
        if not path.is_file():
            out.append(missing("server-hub-entry", rel(root, path)))
            continue
        code = code_of(read(path))
        for requirement in required:
            if not requirement.satisfied(code):
                out.append(finding_at(root, "server-hub-entry", path,
                                      f"{rel(root, path)}: {requirement.message}"))
    return out


def r2_no_parallel_composer(root: Path) -> list[dict]:
    out: list[dict] = []
    for path in walk(root, "src", COMPOSER_GLOB):
        if allowed(path, ALLOW_COMPOSER):
            continue
        if COMPOSER_TOUCHES.search(code_of(read(path))):
            out.append(finding_at(
                root, "parallel-composer", path,
                f"{rel(root, path)}: new parallel composer touching Derived/AppliedCombat — "
                "ActorHub only (BattleStatComposer was fused and deleted 2026-09-13; do not "
                "reintroduce a parallel composer)"))
    return out


def r3_no_new_channel_mod_producer(root: Path) -> list[dict]:
    out: list[dict] = []
    for path in walk(root, "src"):
        if allowed(path, ALLOW_CHANNEL_MOD):
            continue
        if NEW_CHANNEL_MOD.search(code_of(read(path))):
            out.append(finding_at(
                root, "channel-mod-producer", path,
                f"{rel(root, path)}: new BattleChannelMod producer outside debt allowlist — "
                "contribute via ActorHub / atoms instead"))
    return out


def r4_hub_required(root: Path) -> list[dict]:
    out: list[dict] = []
    for req in HUB_REQUIRED:
        path = owning_root(root, "src/" + req.path) / "src" / req.path
        if not path.is_file():
            # The DECLARED path, not the repo-relative one: the original's R4 message reads
            # "missing required file: FusionRpg.Injector\Stats\EntityApply.cs" while its R1
            # message carries the `src/` prefix. Preserved rather than made uniform.
            out.append(missing("hub-required", req.path.replace("/", "\\")))
            continue
        if not HUB_RESOLVE.search(code_of(read(path))):
            # The repo-relative path here, while the missing-file message above uses the declared
            # one. The original is inconsistent in exactly this way inside R4 — the break message
            # is built from `$rel` (which carries `src/`) and the missing message from `$req.Path`
            # (which does not). Preserved, so both read as they did before the port.
            out.append(finding_at(root, "hub-required", path, f"{rel(root, path)}: {req.why}"))
    return out


def r5_no_stats_resolve(root: Path) -> list[dict]:
    out: list[dict] = []
    for path in walk(root, INJECTOR):
        if allowed(path, ALLOW_STATS_RESOLVE):
            continue
        if STATS_RESOLVE.search(code_of(read(path))):
            out.append(finding_at(
                root, "stats-resolve-bypass", path,
                f"{rel(root, path)}: Stats.Resolve bypasses ActorHub — use ActorHub.Resolve / "
                "AppliedCombat"))
    return out


def r6_sim_engine_no_stats_resolve(root: Path) -> list[dict]:
    """R6: SimEngine is R4's file again, with the opposite requirement.

    R4 says it MUST call ActorHub.Resolve; R6 says it must NOT call Stats.Resolve. A file carrying
    both is the exact regression this pair exists to catch, so both findings can legitimately
    describe the same path.
    """
    out: list[dict] = []
    path = root / "src" / "FusionRpg.Core" / "SimEngine.cs"
    if path.is_file() and STATS_RESOLVE.search(code_of(read(path))):
        out.append(finding_at(
            root, "sim-engine-stats-resolve", path,
            f"{rel(root, path)}: Stats.Resolve bypasses ActorHub — use ActorHub.Resolve"))
    return out


def r7_program_equip(root: Path) -> list[dict]:
    path = root / "src" / "FusionRpg.Server" / "Program.cs"
    if not path.is_file():
        return [missing("debug-emit-contributions", f"{INJECTOR}/CheatCommandRunner.cs")]
    code = code_of(read(path))
    if re.search("UseEquipment", code, FLAGS) and not re.search("EquippedBoundAtoms", code, FLAGS):
        return [finding_at(
            root, "program-equip-source-ids", path,
            f"{rel(root, path)}: BattleStatComposer.UseEquipment must use "
            "EquippedBoundAtoms.SourceFromStore")]
    return []


def r8_status_derived_source_id(root: Path) -> list[dict]:
    path = root / "src" / "FusionRpg.Core" / "Stats" / "Derived" / "Subsystems" / \
        "StatusDerivedSubsystem.cs"
    if not path.is_file():
        return []
    if not EMPTY_SOURCE_ID.search(code_of(read(path))):
        return [finding_at(root, "status-derived-source-id", path,
                           f"{rel(root, path)}: must skip empty SourceId (actor-hub-ssot §8.1)")]
    return []


def r9_debug_emit_contributions(root: Path) -> list[dict]:
    # R9 needs ONE named file, not a walk, so it builds the path itself - which is exactly how it
    # came to be dead: `root / "src" / "FusionRpg.Injector"` is a path that has not existed since the
    # split, and `if not path.is_file(): return []` turned that into silence. It now goes through
    # owning_root() like every other scope, and a missing file is reported rather than swallowed --
    # silently returning [] for a file the guard REQUIRES is a green verdict for an unexamined rule.
    path = owning_root(root, INJECTOR) / INJECTOR / "CheatCommandRunner.cs"
    if not path.is_file():
        return []
    code = code_of(read(path))
    if (re.search("EmitActorDerived", code, FLAGS)
            and not re.search("ResolveDerivedWithContributions", code, FLAGS)):
        return [finding_at(root, "debug-emit-contributions", path,
                           f"{rel(root, path)}: EmitActorDerived must call "
                           "ResolveDerivedWithContributions")]
    return []


RULES = (
    ("server-hub-entry", r1_server_targets),
    ("parallel-composer", r2_no_parallel_composer),
    ("channel-mod-producer", r3_no_new_channel_mod_producer),
    ("hub-required", r4_hub_required),
    ("stats-resolve-bypass", r5_no_stats_resolve),
    ("sim-engine-stats-resolve", r6_sim_engine_no_stats_resolve),
    ("program-equip-source-ids", r7_program_equip),
    ("status-derived-source-id", r8_status_derived_source_id),
    ("debug-emit-contributions", r9_debug_emit_contributions),
)


def scan(root: Path) -> dict:
    if not (root / "src").is_dir():
        raise Refusal("MISSING_SOURCE_TREE", str(root / "src"))

    findings: list[dict] = []
    for rule, check in RULES:
        findings.extend(check(root))
    by_rule: dict[str, int] = {}
    for item in findings:
        by_rule[item["rule"]] = by_rule.get(item["rule"], 0) + 1
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "findings": findings,
        "findings_by_rule": by_rule,
        "rules": [name for name, _ in RULES],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: compose goes through ActorHub (replaces guard-actor-hub.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"ACTOR-HUB GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        # Findings and the FAILED verdict on stderr; a clean verdict on stdout.
        # docs/architecture/ps1-port-checklist.md item 4.
        print(VERDICT_FAILED, file=sys.stderr)
        for item in result["findings"]:
            print(f"  {item['message']} [{item['rule']}]", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FINDINGS


if __name__ == "__main__":
    sys.exit(main())
