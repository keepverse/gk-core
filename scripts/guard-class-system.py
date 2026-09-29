#!/usr/bin/env python3
r"""Guard: class-system program invariants stay executable. Replaces `guard-class-system.ps1`.

  G1  every aptitude id is collision-free, and no id collides with a registered channel family
  G2  every edge channel is registered in the derived-stats catalog (exact, or family prefix)
  G3  no aptitude reaches atk twice (`combat.power.*` AND `progression.bonus.atk` from one source)
  G4  every `unitClass: null` catalog entry carries a `unitClassNote`
  G5  `AptitudeReadFunctions` has AT MOST one implementation
  G6  `DominantPosture` is never called from a resolve/subsystem path (a display read, never wired)
  G7  the closed form calls shipped combat symbols, never re-derives them

Reads JSON directly rather than the C# registry: standalone, pre-build tooling that cannot reference
`FusionRpg.Core`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every line went out through `Write-Host`**, invisible to a `2>&1` capture. The verdict goes to
  stdout, the findings and the skip notices to stderr, `--json` carries the machine form.
* **Two `throw`s with no distinguishing name** became `SOURCE-MISSING` refusals.
* **The finding paths were ABSOLUTE** (`$($f.FullName)`), so a finding was not portable and not
  comparable between machines. They are repo-relative now — a declared divergence, and the reason a
  fixture in a temp directory produces a readable path instead of one naming a temp root.

THE VERSION SORT IS NOT COSMETIC, AND THE TREE PROVES IT
--------------------------------------------------------
G2/G3 read the SHIPPED tuning config, picked as the highest `aptitudes.v<n>.json`. The sort is NUMERIC
on `n`, never lexical: the tree currently ships v1 through **v10**, and a lexical sort puts `v9` above
`v10` — so a lexical port would silently read a superseded config the first time the tenth version
ships, and G2/G3 would then be validating four-year-old edges. A version whose number does not parse
sorts as 0, exactly as `[int]("")` does in PowerShell.

G2/G3 ARE SKIPPED WHEN NO TUNING FILE EXISTS, and that is deliberate
--------------------------------------------------------------------
The original's header explains it: the config did not exist when the guard was written, and the absence
branch is what makes a fresh checkout say "nothing to check" rather than fail on a missing file. That
is a legitimate state, so it stays a skip — but it is now REPORTED, on stderr and in `--json`, because
a skipped half that reads as a clean half is the exact failure this program keeps paying for. The
planted-violation fixtures carry the file, so every rule stays independently provable.

EVERY COMPARISON HERE FOLDS CASE
--------------------------------
`-eq`, `-contains`, `-like`, `Group-Object`, `Select-Object -Unique` and every `-match` in the original
are **case-insensitive**; the one exception is `StartsWith("//", StringComparison.Ordinal)`, which is
ordinal and is only ever asked about `//`. So `Foo` and `foo` are the SAME aptitude id for G1, `ATK`
and `atk` are the same channel for G2/G3, and `Class AptitudeReadFunctions` counts for G5. This port
folds all of them, and a test asserts the fold on each compiled pattern rather than through one
fixture, because a fold that is dropped is invisible until a real casing difference ships.

TWO SUBTLE SCOPE DECISIONS, BOTH TRANSCRIBED
--------------------------------------------
* **G5's scan does NOT exclude `bin` or `obj`.** Every other port in this program filters build output;
  this one does not, so a stale copy of a source file under `src/**/bin/` would add a second
  `AptitudeReadFunctions` hit and report a duplicate that does not exist. Transcribed, and asserted, so
  the asymmetry is a decision on the record rather than an oversight to be discovered later.
* **G7's negative half skips only `//`-PREFIXED lines.** A `Math.Exp(` inside a `/* */` block, or after
  a trailing `//`, is still reported. Adding a full comment stripper here would NARROW the rule, which
  is the same silent weakening the check exists to prevent.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

GUARD_ID = "class-system"
VERDICT_OK = ("CLASS-SYSTEM GUARD OK — aptitude ids collision-free, edges registered, no atk "
              "double-count, every null unitClass noted, at most one AptitudeReadFunctions, "
              "DominantPosture unwired, closed form calls shipped combat symbols")
VERDICT_FAILED = "CLASS-SYSTEM GUARD FAILED:"
EXIT_OK = 0
EXIT_FAILED = 1

ROSTER = ("data", "seed", "aptitudes", "roster.json")
CATALOG = ("data", "seed", "derived-stats", "catalog.json")
TUNING_DIR = ("data", "tuning")
SRC_DIR = "src"
TUNING_GLOB = "aptitudes.v*.json"
VERSION_IN_NAME = re.compile(r"aptitudes\.v(\d+)\.json")  # [regex]::Match: case-sensitive

# EVERY pattern below folds case, because every one of them was a PowerShell `-match` / `-eq` /
# `-contains` / `-like` / `Group-Object`. Asserted individually in the contract tests.
APTITUDE_READ_CLASS = re.compile(r"class\s+AptitudeReadFunctions\b", re.IGNORECASE)
RESOLVE_SHAPED_NAME = re.compile(r"Resolve|Subsystem|Composer", re.IGNORECASE)
DOMINANT_POSTURE_CALL = re.compile(r"DominantPosture\s*\.\s*Of\s*\(", re.IGNORECASE)
COMBAT_POWER_CHANNEL = "combat.power."
ATK_CHANNEL = "progression.bonus.atk"
LINE_COMMENT = "//"  # compared with StringComparison.Ordinal in the original

# G7's positive half: a per-swing damage file must reference at least one of these. Matched against
# the RAW text with no comment stripping, so a symbol named only in prose counts — transcribed, and
# the negative half is the half that is meant to be sharp.
SHIPPED_COMBAT_SYMBOLS = (
    r"CombatProbability\.", r"ClampedContest\.", r"OverlayCombatCalculator\.",
    r"CombatDerivedReader\.", r"ShieldMath\.", r"ShieldRuntime\.",
    r"ResistanceEvaluator\.", r"OverlayCombatMath\.", r"CombatDamageDispatcher\.",
)
# G7's negative half: shapes the resolver owns. Per line, and only `//`-prefixed lines are skipped.
RE_DERIVATION_SHAPES = (
    (re.compile(r"Math\.Exp\s*\("), "a sigmoid (Math.Exp)"),
    (re.compile(r"1(?:\.0)?\s*/\s*\(\s*1(?:\.0)?\s*\+"), "a bare 1/(1+...) sigmoid"),
    (re.compile(r"Math\.Clamp\s*\(\s*Math\.Max\s*\("),
     "the linear clamp-and-scale rate shape (ElementalResolver.RateFromZero)"),
)
POOL_CLAMP = re.compile(r"Math\.Clamp\s*\([^;]*(Regen|_regen)[^;]*\)")
RESOURCE_POOL_OWNER = "ResourcePoolState"
STRIKE_MIXTURE_CALL = re.compile(r"StrikeMixture\s*\.\s*Compute", re.IGNORECASE)
STATUS_UPTIME_CALL = re.compile(r"StatusUptime\s*\.", re.IGNORECASE)
OWN_UPTIME_FORMULA = re.compile(r"Math\.Pow\s*\(\s*1(?:\.0)?\s*-")
ACTION_SCHEDULE_CHOOSE = re.compile(r"ActionSchedule\s*\.\s*Choose", re.IGNORECASE)
ACTION_SCHEDULE_ADVANCE = re.compile(r"ActionSchedule\s*\.\s*Advance", re.IGNORECASE)

ANALYTIC_DIR = ("src", "FusionRpg.Core", "Balance", "Analytic")
SIEGE_DIR = ("src", "FusionRpg.Core", "Battle", "Siege")
COMBAT_SIM = ("tools", "CombatSim")

# (a) POSITIVE: files whose own job is per-swing damage.
DAMAGE_COMPUTING_FILES = (
    ANALYTIC_DIR + ("StrikeMixture.cs",),
    ANALYTIC_DIR + ("PhaseModel.cs",),
    SIEGE_DIR + ("SiegeExpectedDamage.cs",),
    SIEGE_DIR + ("SiegeHitChance.cs",),
)
# (b) NEGATIVE: the explicit set whose formulas must not be re-derived. EXPLICIT, not a glob: every
# file under Balance/Analytic is covered except Race.cs, which alone legitimately writes Math.Exp and
# 1.0/(1.0 + ...) for the A&S normal CDF.
RE_DERIVATION_FILES = (
    ANALYTIC_DIR + ("StrikeMixture.cs",),
    ANALYTIC_DIR + ("PhaseModel.cs",),
    ANALYTIC_DIR + ("ActionSchedule.cs",),
    ANALYTIC_DIR + ("StatusUptime.cs",),
    ANALYTIC_DIR + ("Predictor.cs",),
    ANALYTIC_DIR + ("FirstPassage.cs",),
    SIEGE_DIR + ("SiegeExpectedDamage.cs",),
    SIEGE_DIR + ("SiegeHitChance.cs",),
)
COMBAT_SIM_STRIKE = COMBAT_SIM + ("Analytic.cs",)
COMBAT_SIM_STATUS = COMBAT_SIM + ("StatusModel.cs",)
COMBAT_SIM_ECONOMY = COMBAT_SIM + ("ActionEconomy.cs",)
ACTION_SCHEDULE_FILE = ANALYTIC_DIR + ("ActionSchedule.cs",)


class Refusal(Exception):
    """A named precondition failure. The guard's sources are its premise."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def _ps_string(value: object) -> str:
    """`[string]$value` as PowerShell spells it.

    A JSON `null` becomes the EMPTY STRING, not `"None"`. This is not pedantry: G4 reads
    `[string]$e.unitClass` and asks `IsNullOrWhiteSpace`, so `str(None) == "None"` - non-empty,
    non-blank - made the guard SKIP a catalog entry whose unitClass is null and report CLEAN. The
    differential caught it on the first fixture with a `null` unitClass and no note, which the original
    fails. Anywhere a PowerShell `[string]` cast stood, this stands in its place.
    """
    if value is None:
        return ""
    if isinstance(value, bool):
        return "True" if value else "False"  # PowerShell stringifies $true/$false this way
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, (dict, list)):
        # A cast of a container yields its type's ToString; never reached by the shipped shapes, and
        # refusing to guess is better than emitting a plausible-looking wrong string.
        raise Refusal("UNEXPECTED-JSON-SHAPE", f"cannot read {type(value).__name__} as a string field")
    return str(value)


def _read_json(path: Path, label: str) -> object:
    if not path.is_file():
        raise Refusal("SOURCE-MISSING", f"{label} missing: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("SOURCE-UNREADABLE", f"{label}: {path}: {exc}") from exc


def shipped_tuning_file(root: Path) -> Path | None:
    """The live `aptitudes.v<n>.json`, highest `n` NUMERICALLY.

    A lexical sort picks `v9` over `v10`, and the tree ships ten versions, so this is not
    hypothetical. A name whose number does not parse scores 0, matching `[int]("")`.
    """
    directory = root.joinpath(*TUNING_DIR)
    if not directory.is_dir():
        return None
    best: tuple[int, Path] | None = None
    for candidate in directory.glob(TUNING_GLOB):
        match = VERSION_IN_NAME.search(candidate.name)
        version = int(match.group(1)) if match else 0
        if best is None or version > best[0]:
            best = (version, candidate)
    return best[1] if best else None


def _read_text(path: Path, rel: str, skipped: list[str]) -> str | None:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        skipped.append(f"{rel}: unreadable ({exc})")
        return None


def _lines(path: Path, rel: str, skipped: list[str]) -> list[tuple[int, str]] | None:
    """1-based `(line, text)` pairs, or None if the file could not be read."""
    try:
        raw = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        skipped.append(f"{rel}: unreadable ({exc})")
        return None
    return list(enumerate(raw.splitlines(), 1))


def csharp_sources(src: Path) -> list[Path]:
    """Every `.cs` under `src`, recursively.

    Build output is NOT excluded, and that asymmetry is transcribed on purpose: the original's
    `Get-ChildItem -Recurse -Filter *.cs` has no such filter, so a stale copy under `src/**/bin/` would
    add a phantom second `AptitudeReadFunctions`. Changing it would be a behaviour change, not a port.
    """
    if not src.is_dir():
        return []
    return sorted(p for p in src.rglob("*.cs") if p.is_file())


def check(root: Path) -> dict:
    root = root.resolve()
    roster_doc = _read_json(root.joinpath(*ROSTER), "aptitudes roster.json")
    catalog_doc = _read_json(root.joinpath(*CATALOG), "catalog.json")

    roster_entries = roster_doc.get("entries") if isinstance(roster_doc, dict) else None
    catalog_entries = catalog_doc.get("entries") if isinstance(catalog_doc, dict) else None
    if not isinstance(roster_entries, list):
        raise Refusal("ROSTER-SHAPE", "roster.json has no `entries` array")
    if not isinstance(catalog_entries, list):
        raise Refusal("CATALOG-SHAPE", "catalog.json has no `entries` array")

    families = [_ps_string(entry.get("family")) for entry in catalog_entries
                if isinstance(entry, dict)]
    # The original's finding names the CATALOG's spelling of the family, not the id that collided with
    # it: `"G1 ${id}: collides with a registered channel family '$family'"`. Those can differ in case,
    # and the family is the more useful half -- it says WHICH registered family you hit. A first
    # version echoed the id, so a fixture where the id is `ARMOR.PLATE` and the family is `armor.plate`
    # produced a different finding message from the original's. Keep the mapping, not just the set.
    family_by_key = {family.casefold(): family for family in families}
    family_keys = set(family_by_key)
    ids = [_ps_string(entry.get("id")) for entry in roster_entries if isinstance(entry, dict)]

    failures: list[str] = []
    skipped: list[str] = []

    # ---- G1: ids are collision-free, and distinct from every registered family -----------------
    # `Group-Object` is case-INsensitive, so Foo and foo are ONE id here.
    for name, count in Counter(i.casefold() for i in ids).items():
        if count > 1:
            spelled = next(i for i in ids if i.casefold() == name)
            failures.append(f"G1 {spelled}: duplicate aptitude id")
    for identifier in ids:
        key = identifier.casefold()  # `-eq` folds case
        if key in family_keys:
            failures.append(f"G1 {identifier}: collides with a registered channel family "
                            f"'{family_by_key[key]}'")

    # ---- G2 / G3: the shipped tuning config. Skipped, and REPORTED, when absent ------------------
    tuning_file = shipped_tuning_file(root)
    if tuning_file is None:
        skipped.append("G2/G3: no shipped data/tuning/aptitudes.v*.json, so there is nothing to "
                       "check. The original prints this too; it is reported here rather than "
                       "silently passing, because a skipped half that reads as a clean half is the "
                       "failure this program keeps paying for.")
    else:
        tuning = _read_json(tuning_file, "shipped aptitude tuning")
        edges = [e for e in (tuning.get("edges", []) if isinstance(tuning, dict) else [])
                 if isinstance(e, dict) and e.get("channel") and _ps_string(e["channel"]).strip()]
        for edge in edges:
            channel = _ps_string(edge["channel"])
            # `-contains` folds case.
            registered = channel.casefold() in family_keys
            if not registered:
                last_dot = channel.rfind(".")
                if last_dot > 0:
                    registered = channel[:last_dot].casefold() in family_keys
            if not registered:
                failures.append(f"G2 {_ps_string(edge.get('source'))} -> {channel}: channel not found in "
                                "derived-stats catalog (exact or family prefix)")
        atk_sources = [_ps_string(e.get("source")) for e in edges
                       if _ps_string(e.get("channel")).casefold() == ATK_CHANNEL]
        power_sources = [_ps_string(e.get("source")) for e in edges
                         if _ps_string(e.get("channel")).casefold().startswith(COMBAT_POWER_CHANNEL)]
        # `Select-Object -Unique` is case-insensitive too.
        for source in dict.fromkeys(s.casefold() for s in atk_sources):
            if source in {p.casefold() for p in power_sources}:
                spelled = next(s for s in atk_sources if s.casefold() == source)
                failures.append(f"G3 {spelled}: feeds both combat.power.* and "
                                "progression.bonus.atk — double-counted atk")

    # ---- G4: every unitClass: null carries a note ----------------------------------------------
    for entry in catalog_entries:
        if not isinstance(entry, dict):
            continue
        if _ps_string(entry.get("unitClass")).strip():
            continue
        if not _ps_string(entry.get("unitClassNote")).strip():
            failures.append(f"G4 {_ps_string(entry.get('family'))}: unitClass is null with no unitClassNote")

    # ---- G5: at most one AptitudeReadFunctions -------------------------------------------------
    src = root / SRC_DIR
    if not src.is_dir():
        skipped.append("G5/G6/G7: no src/ directory, so the source-scanning rules had nothing to read")
    else:
        hits = []
        for path in csharp_sources(src):
            text = _read_text(path, path.relative_to(root).as_posix(), skipped)
            if text is not None and APTITUDE_READ_CLASS.search(text):
                hits.append(path.relative_to(root).as_posix())
        if len(hits) > 1:
            failures.append(f"G5: AptitudeReadFunctions has {len(hits)} implementations: "
                            f"{', '.join(hits)}")

        # ---- G6: DominantPosture is never a resolve input --------------------------------------
        for path in csharp_sources(src):
            rel = path.relative_to(root).as_posix()
            if path.name.casefold() == "dominantposture.cs":  # `-eq` folds case
                continue
            if not RESOLVE_SHAPED_NAME.search(path.name):
                continue
            text = _read_text(path, rel, skipped)
            if text is not None and DOMINANT_POSTURE_CALL.search(text):
                failures.append(f"G6 {rel}: resolve-shaped file calls DominantPosture.Of — it is a "
                                "display read, never a resolve input")

    # ---- G7: the closed form calls shipped combat symbols, never re-derives them ---------------
    for parts in DAMAGE_COMPUTING_FILES:
        path = root.joinpath(*parts)
        rel = "/".join(parts)
        if not path.is_file():
            skipped.append(f"G7: {rel} does not exist, so its positive check did not run")
            continue
        text = _read_text(path, rel, skipped)
        if text is None:
            continue
        if not any(re.search(symbol, text, re.IGNORECASE) for symbol in SHIPPED_COMBAT_SYMBOLS):
            failures.append(f"G7 {rel}: no reference to a shipped combat symbol found — a per-swing "
                            "damage file must call the shipped resolver, never re-derive its formulas")

    for parts in RE_DERIVATION_FILES:
        path = root.joinpath(*parts)
        rel = "/".join(parts)
        if not path.is_file():
            skipped.append(f"G7: {rel} does not exist, so its re-derivation scan did not run")
            continue
        rows = _lines(path, rel, skipped)
        if rows is None:
            continue
        for number, line in rows:
            # ONLY a `//`-prefixed line is skipped. A shape inside a `/* */` block, or after a
            # trailing `//`, is still reported — transcribe, do not "improve".
            if line.lstrip().startswith(LINE_COMMENT):
                continue
            for pattern, what in RE_DERIVATION_SHAPES:
                if pattern.search(line):
                    failures.append(f"G7 {rel}:{number} re-derives {what} — call the shipped owner "
                                    "(ElementalResolver / CombatProbability) instead")

    # T6: CombatSim's per-swing Strike must call the Core owner.
    path = root.joinpath(*COMBAT_SIM_STRIKE)
    rel = "/".join(COMBAT_SIM_STRIKE)
    if path.is_file():
        text = _read_text(path, rel, skipped)
        if text is not None and not STRIKE_MIXTURE_CALL.search(text):
            failures.append(f"G7 {rel}: CombatSim's per-swing Strike must call StrikeMixture.Compute, "
                            "not re-assemble the omni swing")
    else:
        skipped.append(f"G7: {rel} does not exist, so the CombatSim Strike check did not run")

    # T7: CombatSim's status bookkeeping must call StatusUptime.
    path = root.joinpath(*COMBAT_SIM_STATUS)
    rel = "/".join(COMBAT_SIM_STATUS)
    if path.is_file():
        text = _read_text(path, rel, skipped)
        if text is not None:
            if not STATUS_UPTIME_CALL.search(text):
                failures.append(f"G7 {rel}: CombatSim's status bookkeeping must call StatusUptime, "
                                "not re-write the uptime formula")
            if OWN_UPTIME_FORMULA.search(text):
                failures.append(f"G7 {rel}: contains an uptime formula of its own (Math.Pow(1 - ...))")
    else:
        skipped.append(f"G7: {rel} does not exist, so the CombatSim status check did not run")

    # T8: no file under Balance/Analytic advances a pool with a local double clamp.
    analytic = root.joinpath(*ANALYTIC_DIR)
    if analytic.is_dir():
        for path in sorted(analytic.glob("*.cs")):
            rel = path.relative_to(root).as_posix()
            rows = _lines(path, rel, skipped)
            if rows is None:
                continue
            for number, line in rows:
                if line.lstrip().startswith(LINE_COMMENT):
                    continue
                if POOL_CLAMP.search(line):
                    failures.append(f"G7 {rel}:{number} advances a resource pool with a double clamp "
                                    "— call ResourcePoolState/ActionSchedule.Advance instead")
        action_schedule = root.joinpath(*ACTION_SCHEDULE_FILE)
        rel = "/".join(ACTION_SCHEDULE_FILE)
        if action_schedule.is_file():
            text = _read_text(action_schedule, rel, skipped)
            if text is not None and RESOURCE_POOL_OWNER not in text:
                failures.append(f"G7 {rel}: the analytic pool advance must go through "
                                "ResourcePoolState.Settle")
        else:
            skipped.append(f"G7: {rel} does not exist, so the pool-owner check did not run")
    else:
        skipped.append("G7: no Balance/Analytic directory, so the pool-clamp scan had nothing to read")

    # T8: CombatSim's action economy must CALL the twin, not mirror it.
    path = root.joinpath(*COMBAT_SIM_ECONOMY)
    rel = "/".join(COMBAT_SIM_ECONOMY)
    if path.is_file():
        text = _read_text(path, rel, skipped)
        if text is not None and not (ACTION_SCHEDULE_CHOOSE.search(text)
                                     and ACTION_SCHEDULE_ADVANCE.search(text)):
            failures.append(f"G7 {rel}: CombatSim's action economy must call "
                            "ActionSchedule.Choose/Advance, not mirror them")
    else:
        skipped.append(f"G7: {rel} does not exist, so the CombatSim economy check did not run")

    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if failures else "OK",
        "root": root.as_posix(),
        "shipped_tuning": tuning_file.relative_to(root).as_posix() if tuning_file else None,
        "aptitude_ids": len(ids),
        "catalog_families": len(families),
        "failures": failures,
        "skipped": skipped,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: class-system program invariants stay executable "
                    "(replaces guard-class-system.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="the repository to check (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = check(args.root)
    except Refusal as refusal:
        print(f"{VERDICT_FAILED} {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            # ONE envelope shape, not two. A consumer reading --json should not need a branch for "was
            # it a refusal?", so the refusal carries the SAME keys as the normal result with empty
            # values, plus the two that name it. The counting keys are present and zero rather than
            # absent: a key that vanishes on one path is a consumer's KeyError.
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "root": str(args.root),
                              "shipped_tuning": None, "aptitude_ids": 0, "catalog_families": 0,
                              "failures": [], "skipped": []}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
        return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED

    # stdout carries the VERDICT and nothing else; findings and skip notices are stderr.
    # Skips are announced on BOTH paths, and that is the point of reporting them at all: a skipped
    # half that prints nothing is indistinguishable from a half that ran and found nothing.
    for notice in result["skipped"]:
        print(f"[guard-class-system] SKIPPED {notice}", file=sys.stderr)
    if result["verdict"] == "OK":
        print(VERDICT_OK)
        return EXIT_OK
    print(VERDICT_FAILED)
    for failure in result["failures"]:
        print(f"  {failure}", file=sys.stderr)
    return EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
