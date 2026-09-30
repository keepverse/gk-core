#!/usr/bin/env python3
r"""Guard: the power ladder stays the one power ladder (spec-power-guard.md, T4.1). Replaces
`guard-power.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every finding went out through `Write-Host`**, invisible to a `2>&1` capture. Findings now go to
  stderr and only the OK verdict to stdout, and `--json` carries the machine form.
* **Two preconditions THREW.** A missing `Core/Power` or `inventory.json` produced a stack trace and
  exit 1 — the same exit as a real violation, so a broken checkout read as four G1 findings. The port
  names the refusal.
* **TWO FAIL-OPEN HOLES, both fixed here and both measured.**

  1. **A scale row with no `location` silently disabled G3 for the whole repository.** The original
     did `$null -split ',\s*'`, which yields one EMPTY string; a location token of `""` then satisfies
     `$relFwd.StartsWith("")` for every file, so one unlocated scale licensed every power-shaped
     method everywhere. A scale that is not located cannot license anything, so an absent or empty
     location is now a finding. The shipped `inventory.json` is clean (33 scales, 0 unlocated), so
     this closes the hole without reddening the tree.
  2. **A tuning file missing a `curve` field read as ZERO.** `[long]$curve.cMilli` on an absent
     property is 0, and a file with `bMilli` absent can still divide its pin exactly and PASS. A
     tuning file that does not state its own curve is now a finding, because the point of G4 is to
     re-derive the pin rather than trust what the file claims.
  3. **THE REPORTED PATH WAS OFTEN A FILE THAT DOES NOT EXIST.** Every finding interpolated
     `$_.FullName.Substring($Root.Length)`, and `Get-ChildItem` returns a path in whatever form the
     filesystem canonicalises to, while `$Root` is whatever the caller spelled. When those differ — an
     8.3 short name against the long form, which is exactly what `tempfile` and CI both produce — the
     substring slices the wrong number of characters and the finding names a truncated path. Measured
     on a fixture rooted at an 8.3 path, the original reported `G4 pin\data\tuning\power-scale.v9.json`
     for a file that lives at `g4-broken-pin/data/tuning/…`: a path an operator cannot open, from a
     guard whose entire job is to point at a file. `Path.relative_to` cannot mis-slice, which is why
     every finding here is built from it.

CASE SENSITIVITY IS PER CALL SITE, AND THREE CONVENTIONS APPEAR IN ONE SCRIPT
----------------------------------------------------------------------------
* G1 used `-match` on a line → **folds case**.
* The G2/G3 signature pattern carries an inline `(?im)` → **folds case**.
* The body-window test used `-notmatch` → **folds case**.
* `inventory` membership: `$relFwd -eq $_` is PowerShell's string `-eq`, which **folds case**; but
  `$relFwd.StartsWith($_)` is an **ordinal** comparison and does **not**.

That last pair is the one a port gets wrong by reflex, because the two halves of a single `Where-Object`
disagree. Both are transcribed as they are.

THE OVERFLOW RULE IS STRICTER HERE, AND THAT IS A DIFFERENCE WORTH NAMING
------------------------------------------------------------------------
The original cast every curve field to `[long]`, so a hostile or corrupt tuning file would have thrown
on overflow. Python integers do not overflow, so the port cannot throw: it computes the exact value
and the pin check then fails, which is the outcome the original was reaching for. G4's values are
milliscale (`pinValue * 1000`), so `[long]` was never at risk in practice and no shipped file is
affected. The behaviour is documented rather than emulated, because emulating a wrap would be worse
than the arithmetic being right.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from cscan import strip_whole_line_comments  # noqa: E402
from guard_subjects import subject_root  # noqa: E402
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402

GUARD_ID = "power"
VERDICT_OK = "POWER GUARD OK — one ladder, pin holds, no private f(level)"
VERDICT_FAILED = "POWER GUARD FAILED:"
EXIT_OK = 0
EXIT_FAILED = 1
# A REFUSAL IS NOT A FINDING, AND THE EXIT CODE IS HOW SAY SO. guard-stat-pairs.py already
# established 64 for "I cannot run"; these four mapped every Refusal onto EXIT_FAILED, so a
# guard refusing because a sibling repository is not checked out was indistinguishable from a
# guard that found a violation. A standalone clone is a SUPPORTED layout, so on a clone six
# guards legitimately cannot run, and an operator has to be able to read that as a named
# condition rather than as six broken guards.
EXIT_REFUSED = 64

POWER_DIR = ("src", "FusionRpg.Core", "Power")
INVENTORY = ("docs", "architecture", "power", "inventory.json")
TUNING_DIR = ("data", "tuning")
TUNING_GLOB = "power-scale.v*.json"
BUILD_OUTPUT = ("obj", "bin")

# G1: a literal curve field in Core/Power outside the loader. PowerTuningLoader.cs reads the tuning
# files, and PowerTuning.cs is exempt too: the three FixedC/PinIndex/PinValue anchor consts
# legitimately live there BY DESIGN (an ask-first ADR, not a tuning edit - its own doc comment), and
# Build()'s belt-and-braces re-derivation is structural verification math, not a second curve.
G1_EXEMPT = ("PowerTuningLoader.cs", "PowerTuning.cs")
# FOLDS CASE, because the original used `-match`.
G1_PATTERN = re.compile(r"\b(CMilli|AMilli|BMilli|PinIndex|PinValue)\s*[:=]\s*-?\d", re.IGNORECASE)

# G2/G3: a method taking a level/lvl/index parameter, doing arithmetic on it, and returning a
# numeric type, outside Core/Power. The heuristic over-matches BY DESIGN (spec-power-guard.md §2.2);
# the allowlists are the safety valve, and every entry in the default is reasoned in the original's
# own param block. The inline (?i) makes this case-insensitive, as the original's was.
G2_PATTERN = re.compile(
    r"^[ \t]*(public|internal|private|protected|static)[^(=;]*\b(int|long|double|float)\s+\w+\s*"
    r"\([^)]*\b(level|lvl|index)\b[^)]*\)",
    re.IGNORECASE | re.MULTILINE)
PARAM_PATTERN = re.compile(r"\b(level|lvl|index)\b", re.IGNORECASE)
# The body window: the parameter name combined with +, - or * in either order. FOLDS CASE, because the
# original used `-notmatch`.
BODY_ARITHMETIC = 600
BODY_AFTER = re.compile(r"([A-Za-z0-9_]+)\s*[*+\-]|[*+\-]\s*([A-Za-z0-9_]+)")

# Five reasoned false positives, each a COST ladder or a self-level term rather than a second power
# curve. Transcribed with their reasoning in the module docstring of the original; the reasoning
# lives in docs/architecture/power/ssot-power-scale.md §10 and inventory.json, which is where a
# reader should check whether one of these has since stopped being true.
G2_ALLOWLIST = ("PatronPolicy.cs", "RpgProgression.cs", "EnhancePolicy.cs",
                "SpeciesProgression.cs", "MasteryIndex.cs")


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def code_lines(text: str) -> list[str]:
    """Lines with comment-only lines BLANKED, not removed.

    Blanking rather than dropping is what keeps each entry's index equal to its real 1-based line
    number, which is what lets G1 report `file:line` rather than a position in a stripped blob. The
    original did this inline; `cscan.strip_whole_line_comments` is the same policy under a name, and
    it is deliberately WEAKER than full comment awareness — a trailing comment on a line of real code
    is still scanned, because a comment documenting the boundary must never look like breaking it.
    """
    return strip_whole_line_comments(text).split("\n")


def _rel(root: Path, path: Path) -> str:
    """Repository-relative path, forward-slashed, as the registry and the specs are written in."""
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


def inventory_locations(inventory: dict) -> tuple[list[str], list[str]]:
    """Every `location` token in the inventory, plus the scales that name none.

    A scale with no location is returned as a FINDING rather than filtered out, because the original
    turned one into an empty token that matched every file - see the module docstring.
    """
    tokens: list[str] = []
    unlocated: list[str] = []
    scales = inventory.get("scales")
    if not isinstance(scales, list):
        raise Refusal("INVENTORY-SHAPE-UNEXPECTED", "'scales' is not an array")
    for row in scales:
        if not isinstance(row, dict):
            continue
        scale_id = str(row.get("id", "<no id>"))
        raw = row.get("location")
        if not isinstance(raw, str) or not raw.strip():
            unlocated.append(scale_id)
            continue
        for part in re.split(r",\s*", raw):
            part = part.strip()
            if part:
                tokens.append(part)
    return tokens, unlocated


def _is_listed(rel_fwd: str, locations: list[str]) -> bool:
    """`$relFwd -eq $_` (case-INSENSITIVE) or `$relFwd.StartsWith($_)` (ordinal, case-SENSITIVE).

    The two halves disagree about case in the original, and transcribing them as one comparison would
    change which files G3 licenses.
    """
    for token in locations:
        if rel_fwd.lower() == token.lower():
            return True
        if rel_fwd.startswith(token):  # ordinal: a case-differing prefix does NOT match
            return True
    return False


def check_g1(root: Path, g1_allowlist: list[str]) -> list[str]:
    power_dir = root.joinpath(*POWER_DIR)
    exempt = set(G1_EXEMPT) | set(g1_allowlist)
    findings: list[str] = []
    for path in sorted(power_dir.glob("*.cs")):
        if path.name in exempt:
            continue
        rel = _rel(root, path)
        for index, line in enumerate(code_lines(path.read_text(encoding="utf-8", errors="replace"))):
            if G1_PATTERN.search(line):
                findings.append(
                    f"G1 {rel}:{index + 1}: literal curve field outside PowerTuningLoader "
                    f"— {line.strip()}")
    return findings


def check_g2_g3(root: Path, g2_allowlist: list[str], locations: list[str]) -> list[str]:
    src = root / "src"
    power_dir = root.joinpath(*POWER_DIR)
    findings: list[str] = []
    allow = set(g2_allowlist)
    for path in sorted(src.rglob("*.cs")):
        parts = set(path.parts)
        if parts & set(BUILD_OUTPUT):
            continue
        if power_dir in path.parents:
            continue
        rel = _rel(root, path)
        text = "\n".join(code_lines(path.read_text(encoding="utf-8", errors="replace")))
        for match in G2_PATTERN.finditer(text):
            param = PARAM_PATTERN.search(match.group(0))
            if not param:
                continue
            name = param.group(0)
            window = text[match.start():match.start() + BODY_ARITHMETIC]
            if not _does_arithmetic(window, name):
                continue
            line_no = text.count("\n", 0, match.start()) + 1
            if path.name not in allow:
                findings.append(
                    f"G2 {rel}:{line_no}: private f({name})-shaped method outside Core/Power")
            if not _is_listed(rel, locations):
                findings.append(
                    f"G3 {rel}:{line_no}: power-shaped method not listed in inventory.json")
    return findings


def _does_arithmetic(window: str, name: str) -> bool:
    """Is `name` combined with +, - or * in the window, in either order?

    The original built the pattern as `[regex]::Escape($paramName) + '\\s*[*+\\-]'` OR
    `'[*+\\-]\\s*' + [regex]::Escape($paramName)`, and skipped the match when neither held. A window
    is only 600 characters, so this is a cheap scan of a bounded string rather than a second regex
    over the file.
    """
    escaped = re.escape(name)
    after = re.compile(escaped + r"\s*[*+\-]", re.IGNORECASE)
    before = re.compile(r"[*+\-]\s*" + escaped, re.IGNORECASE)
    return bool(after.search(window) or before.search(window))


def _curve_int(curve: dict, key: str, rel: str) -> tuple[int | None, list[str]]:
    """Read one curve field as an exact integer, or explain why it is not one.

    Fail CLOSED: the original's `[long]$curve.missing` was 0, and a file whose `bMilli` is absent can
    still divide its pin exactly and pass G4. A tuning file that does not state its own curve is a
    finding, because G4 exists to re-derive the pin rather than trust what the file claims.
    """
    if key not in curve:
        return None, [f"G4 {rel}: curve.{key} is absent, so the pin cannot be re-derived"]
    value = curve[key]
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None, [f"G4 {rel}: curve.{key} is not a number ({value!r})"]
    if isinstance(value, float) and not value.is_integer():
        return None, [f"G4 {rel}: curve.{key} is not a whole number ({value!r})"]
    return int(value), []


def check_g4(root: Path) -> list[str]:
    """Every power-scale.v*.json must reproduce its own pinValue, re-derived here rather than trusted
    from the C# loader. This guard runs standalone, pre-build, which is the point: it mirrors
    `PowerTuning.Build`'s own belt-and-braces check without needing the build.

    A repository with no `gk-core/data/tuning` at all has nothing for G4 to check, which is distinct from
    having the directory and no matching file - also legal, and also checks nothing.
    """
    tuning_dir = root.joinpath(*TUNING_DIR)
    if not tuning_dir.is_dir():
        return []
    findings: list[str] = []
    for path in sorted(tuning_dir.glob(TUNING_GLOB)):
        rel = _rel(root, path)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            findings.append(f"G4 {rel}: not readable JSON, so the pin cannot be re-derived: {exc}")
            continue
        curve = data.get("curve") if isinstance(data, dict) else None
        if not isinstance(curve, dict):
            findings.append(f"G4 {rel}: no 'curve' object, so the pin cannot be re-derived")
            continue

        values: dict[str, int] = {}
        broke = False
        for key in ("cMilli", "bMilli", "pinIndex", "pinValue"):
            value, problems = _curve_int(curve, key, rel)
            findings += problems
            if value is None:
                broke = True
                continue
            values[key] = value
        if broke:
            continue

        pin_index = values["pinIndex"]
        if pin_index <= 0:
            findings.append(f"G4 {rel}: pinIndex must be positive, got {pin_index}")
            continue

        # Halve before multiplying, the same shape as PowerLadder.TriangularMilli: it avoids forming
        # the un-halved product, which is the exact overflow PowerLadder.cs's own comment documents
        # finding. Python integers do not overflow, so this is belt-and-braces rather than a
        # necessity - kept because the shape is the documented one and the comment explains why.
        if pin_index % 2 == 0:
            half, other = pin_index // 2, pin_index - 1
        else:
            half, other = (pin_index - 1) // 2, pin_index
        triangular = values["bMilli"] * half * other

        numerator = values["pinValue"] * 1000 - values["cMilli"] - triangular
        if numerator % pin_index != 0:
            findings.append(
                f"G4 {rel}: bMilli={values['bMilli']} does not divide the pin exactly at "
                f"pinIndex={pin_index}")
            continue
        a_milli = numerator // pin_index
        pin_check = values["cMilli"] + a_milli * pin_index + triangular
        if pin_check != values["pinValue"] * 1000:
            findings.append(
                f"G4 {rel}: pin broken — P({pin_index})*1000 = {pin_check}, "
                f"expected {values['pinValue'] * 1000}")
    return findings


def check(root: Path, *, g1_allowlist: list[str] | None = None,
          g2_allowlist: list[str] | None = None) -> dict:
    power_dir = root.joinpath(*POWER_DIR)
    if not power_dir.is_dir():
        raise Refusal("CORE-POWER-MISSING", str(power_dir))
    # The power inventory is developer documentation, so it lives in gk-workflow - and gk-workflow
    # is an ANCESTOR of this repository, not this repository. Resolving it against `root` asks
    # gk-core for a document it does not hold, which is why this guard refused with
    # INVENTORY-MISSING on a path that exists. The monorepo had one root and the question did not
    # arise; nine repositories make "which root owns this file" the first question, not the last.
    #
    # `root` is still what the G1-G3 source scans walk, because src/FusionRpg.Core/Power IS this
    # repository's. One guard legitimately reads from two repositories, which is why the root is
    # named per file rather than once for the whole run.
    # A missing workspace is a refusal, not a traceback - see guard-class-system.py for why the
    # distinction is load-bearing rather than cosmetic.
    # Root-then-owner: a fixture carrying its own inventory.json is the subject under test, and
    # only a root without one is resolved to gk-workflow. See scripts/lib/guard_subjects.py.
    try:
        inventory_path = subject_root(root, INVENTORY, workspace_root).joinpath(*INVENTORY)
    except RootNotFound as exc:
        raise Refusal("WORKSPACE-ROOT-MISSING", str(exc)) from exc
    if not inventory_path.is_file():
        raise Refusal("INVENTORY-MISSING", str(inventory_path))
    try:
        inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("INVENTORY-UNREADABLE", f"{inventory_path}: {exc}") from exc
    if not isinstance(inventory, dict):
        raise Refusal("INVENTORY-SHAPE-UNEXPECTED", f"{inventory_path}: not an object")

    locations, unlocated = inventory_locations(inventory)

    findings: list[str] = []
    # A scale that names no location cannot license any file. See the module docstring for why the
    # original's behaviour here was the opposite.
    for scale_id in unlocated:
        findings.append(
            f"G3 inventory.json: scale '{scale_id}' names no location, so it licenses no file; an "
            "unlocated scale used to match every path in the repository")
    findings += check_g1(root, g1_allowlist or [])
    # `None` means "not supplied", which is DIFFERENT from "supplied empty". The first version passed an
    # empty list from main() and tested `is not None`, so the five reasoned allowlist entries were
    # silently dropped and the guard reported six false positives on the real tree - the allowlists
    # are the safety valve, and losing them turns a working guard into a red one. The default is now
    # reached only when the flag is genuinely absent.
    findings += check_g2_g3(root, g2_allowlist if g2_allowlist is not None else list(G2_ALLOWLIST),
                           locations)
    findings += check_g4(root)

    by_check: dict[str, int] = {}
    for item in findings:
        key = item.split(" ", 1)[0]
        by_check[key] = by_check.get(key, 0) + 1
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "findings": findings,
        "findings_by_check": by_check,
        "inventory_locations": len(locations),
        "inventory_unlocated": unlocated,
    }


def _split_list(values: list[str] | None) -> list[str]:
    """Accept `--g2-allowlist-file a.cs`, repeated, and/or comma-separated in one value.

    The original took `[string[]]$G2AllowlistFiles` and the C# suite drove it through `-File` mode as
    a plain string (`-G2AllowlistFiles Sneaky.cs`), because `-File` passes arguments as strings rather
    than re-parsing PowerShell syntax. Supporting both shapes keeps every existing caller working
    without anyone having to learn a second convention.

    AN EXPLICIT LIST REPLACES THE DEFAULT, as it did in PowerShell, where binding a single value to a
    `[string[]]` parameter overwrites the declared default rather than adding to it. The C# suite
    cannot tell the two apart: its allowlist fixture plants exactly one file in a temp root, so
    "replaced" and "appended" both exit 0. That is a good fixture for the rule and a silent one for
    this decision, so the behaviour is stated here rather than left to be discovered.
    """
    out: list[str] = []
    for value in values or []:
        for part in value.split(","):
            part = part.strip()
            if part:
                out.append(part)
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the power ladder stays the one power ladder (replaces guard-power.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--g1-allowlist-file", action="append", default=None, metavar="NAME",
                        help="extra file exempt from G1 (repeatable and/or comma-separated)")
    parser.add_argument("--g2-allowlist-file", action="append", default=None, metavar="NAME",
                        help="extra file exempt from G2 (repeatable and/or comma-separated)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    # An absent flag is None, not []. See the note in check(): the two are different and conflating
    # them silently disables the default allowlists.
    g1_extra = _split_list(args.g1_allowlist_file) if args.g1_allowlist_file is not None else None
    g2_list = _split_list(args.g2_allowlist_file) if args.g2_allowlist_file is not None else None
    try:
        result = check(args.root.resolve(),
                       g1_allowlist=(g1_extra or []) + (list(G1_EXEMPT) if g1_extra else []),
                       g2_allowlist=g2_list)
    except Refusal as refusal:
        print(f"{GUARD_ID} REFUSED: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "findings": [],
                              "findings_by_check": {}, "inventory_locations": 0,
                              "inventory_unlocated": []}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
