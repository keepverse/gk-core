#!/usr/bin/env python3
"""
Guard: the counterbalance rule stays executable (spec-stat-taxonomy.md SS6.2, T0.3).

Why the PowerShell form was retired (scripts/guard-stat-pairs.ps1, 101 lines):

  * **No machine-readable result.** It printed only `STAT-PAIRS GUARD OK`, so it could be grepped,
    misread and never asserted on. `--json` reports the verdict, the row counts per group, and every
    finding with the rule id (P1-P4) so a caller can select on the rule rather than grep the text.
  * **Findings were anonymous strings.** `"P2 $($r.Name) <-> ..."` mixed the rule id into prose, so
    counting findings per rule meant parsing English. The rule id is now a field.
  * **A missing catalog was a bare `throw`.** In PowerShell that surfaces as a red error record on
    a stream the caller may not capture; here it is a named refusal with exit 64, and it is
    distinguishable from "the catalog is there and the rules pass".

No external process is run, so there is deliberately NO `--timeout` claim here: the unbounded-
subprocess defect that retired other PowerShell tools does not apply, and the standard is applied
where it is real rather than uniformly for its own sake.

THE CONTRACT, carried over exactly
-----------------------------------
    P1  every Contest family names a counterpart
    P2  every declared counterpart resolves AND the pair is symmetric
    P3  Race class never declares a counterpart (the opponent's own value is the counter)
    P4  a Contest magnitude (GameUnits / GameUnitsPerSecond) is never numerically capped

FOUR SUBTLETIES THE PORT HAD TO KEEP, each of which a careless rewrite silently loses:

  1. **The two arrays are checked SEPARATELY.** `entries` and `prefixFamilies` are flattened into
     one row shape but grouped, and P2 resolves a counterpart only WITHIN its own group. They
     describe different things - fixed categories vs sparse per-id overrides - and were never meant
     to cross-reference. Pooling them would make every sparse override look like a broken pair.
  2. **Comparisons are case-INSENSITIVE.** PowerShell's `-eq` and `-notcontains` are
     case-insensitive by default, so `"contest"` matched `StatClass -eq "Contest"` and `gameunits`
     matched the magnitude unit list. Python's `==` is not, so every comparison here folds case
     explicitly. Dropping the fold would quietly stop flagging a mis-cased `statClass`.
  3. **A boolean cap is NOT a numeric cap.** The original used `-is [ValueType] -and -isnot [bool]`.
     In Python `isinstance(True, int)` is TRUE, so a JSON `true` cap would be reported as a
     progression ceiling unless booleans are excluded explicitly. This is the one place where the
     port is NOT a mechanical translation, and getting it wrong invents P4 findings that do not
     exist.
  4. **Whitespace-only is absent.** `IsNullOrWhiteSpace` treats `"   "` as no counterpart. Python's
     truthiness would too, but `"0"` and `"false"` are truthy strings, so the emptiness test is
     written as an explicit strip rather than left to `if not counterpart`.

Verdict strings are byte-preserved - the Guard suite Assert.Contains them.

Usage (repo root):
    python gk-core/scripts/guard-stat-pairs.py
    python gk-core/scripts/guard-stat-pairs.py --root <path>      # falsifier fixture
    python gk-core/scripts/guard-stat-pairs.py --json

Exit 0 = clean. 1 = a finding. 64 = REFUSED (no catalog.json, so the rules have no subject and
reporting clean would be a false green).
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import content_root  # noqa: E402

CATALOG_RELATIVE = ("data", "seed", "derived-stats", "catalog.json")

# The four rules, as a closed vocabulary. Findings carry the key, so a caller selects on it.
P1 = "P1"
P2 = "P2"
P3 = "P3"
P4 = "P4"

# Scoped to the two true "magnitude" unit classes, NOT every UnitClass: SigmoidPoints and
# SigmoidMultiplierPoints are deliberately uncapped INPUTS to a bounded output (spec-stat-taxonomy.md
# SS2.5), and StatusPotencyPoints' shipped 0.95 resist cap is a pre-existing, documented,
# bounded-ratio-shaped magnitude cap (SS11.6 of ssot-power-scale.md) this guard does not relitigate.
MAGNITUDE_UNITS = ("gameunits", "gameunitspersecond")

VERDICT_OK = ("STAT-PAIRS GUARD OK — every Contest paired, every pair symmetric, "
              "no Race paired, no capped Contest magnitude")
VERDICT_FAILED = "STAT-PAIRS GUARD FAILED:"


class Refusal(Exception):
    """A named precondition failure. Never reported as a pass."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason + (f"\n{detail}" if detail else ""))
        self.reason, self.detail = reason, detail


class Row:
    """One catalog row, normalised. `group` is the array it came from, which is what keeps P2
    from resolving a counterpart across the two arrays."""

    __slots__ = ("name", "stat_class", "counterpart", "cap", "unit_class", "group")

    def __init__(self, entry: dict, name_field: str, group: str) -> None:
        self.name = str(entry.get(name_field, "") or "")
        self.stat_class = str(entry.get("statClass", "") or "")
        self.counterpart = str(entry.get("counterpart", "") or "")
        self.cap = entry.get("cap")
        self.unit_class = str(entry.get("unitClass", "") or "")
        self.group = group

    def is_contest(self) -> bool:
        return self.stat_class.casefold() == "contest"

    def is_race(self) -> bool:
        return self.stat_class.casefold() == "race"

    def is_magnitude_unit(self) -> bool:
        return self.unit_class.casefold() in MAGNITUDE_UNITS

    def has_counterpart(self) -> bool:
        """Whitespace-only counts as absent, matching IsNullOrWhiteSpace. Written as an explicit
        strip rather than truthiness, so a legitimate `"0"` or `"false"` id is not mistaken for
        emptiness."""
        return bool(self.counterpart.strip())


def load_rows(root: Path) -> tuple[list[Row], dict]:
    """Read the catalog and flatten it. Raises Refusal when the guard cannot run at all."""
    # The derived-stats catalog is gk-data's, not this repository's. Resolving it against `root`
    # asked gk-core for a file it does not hold, so the guard REFUSED instead of reporting - and
    # refusing is the correct response to a missing subject that was never missing.
    catalog_path = content_root(root).joinpath(*CATALOG_RELATIVE)
    if not catalog_path.is_file():
        raise Refusal("CATALOG-MISSING",
                      f"{catalog_path.as_posix()} does not exist, so the counterbalance rules have "
                      f"no subject; reporting clean would be a false green")
    try:
        catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("CATALOG-UNREADABLE",
                      f"{catalog_path.as_posix()}: {type(exc).__name__}: {exc}") from exc
    if not isinstance(catalog, dict):
        raise Refusal("CATALOG-SHAPE",
                      f"{catalog_path.as_posix()} is a {type(catalog).__name__}, not an object")

    rows: list[Row] = []
    counts: dict[str, int] = {}
    # Two arrays, two name fields, two groups. Kept SEPARATE on purpose - see the docstring.
    for key, name_field in (("entries", "family"), ("prefixFamilies", "prefix")):
        block = catalog.get(key) or []
        if not isinstance(block, list):
            raise Refusal("CATALOG-SHAPE",
                          f"catalog.{key} is a {type(block).__name__}, not an array")
        for entry in block:
            if not isinstance(entry, dict):
                raise Refusal("CATALOG-SHAPE", f"catalog.{key} holds a non-object entry")
            rows.append(Row(entry, name_field, key))
        counts[key] = len(block)
    return rows, counts


def is_numeric(value: object) -> bool:
    """True for a JSON number, FALSE for a JSON boolean.

    `isinstance(True, int)` is True in Python, so without the bool exclusion a `"cap": true` would
    be reported as a capped magnitude - a P4 finding that does not exist. The original expressed
    exactly this with `-is [ValueType] -and -isnot [bool]`.
    """
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def check(rows: list[Row]) -> list[dict]:
    findings: list[dict] = []

    # P1 - every Contest family names a counterpart.
    for row in rows:
        if row.is_contest() and not row.has_counterpart():
            findings.append({"rule": P1, "name": row.name, "group": row.group,
                             "message": "Contest class with no counterpart"})

    # P2 - every declared counterpart resolves within its OWN group, and the pair is symmetric.
    by_group: dict[str, dict[str, Row]] = {}
    for row in rows:
        by_group.setdefault(row.group, {})[row.name] = row
    for group, index in by_group.items():
        for row in index.values():
            if not row.has_counterpart():
                continue
            other = index.get(row.counterpart)
            if other is None:
                findings.append({
                    "rule": P2, "name": row.name, "group": group,
                    "message": f"counterpart '{row.counterpart}' does not resolve in {group}"})
                continue
            if other.counterpart != row.name:
                findings.append({
                    "rule": P2, "name": row.name, "group": group,
                    "message": (f"asymmetric pair with '{row.counterpart}': "
                                f"back-reference is '{other.counterpart}'")})

    # P3 - Race class never declares a counterpart.
    for row in rows:
        if row.is_race() and row.has_counterpart():
            findings.append({"rule": P3, "name": row.name, "group": row.group,
                             "message": "Race class must not declare a counterpart"})

    # P4 - a Contest magnitude is never numerically capped.
    for row in rows:
        if row.is_contest() and row.is_magnitude_unit() and is_numeric(row.cap):
            findings.append({
                "rule": P4, "name": row.name, "group": row.group,
                "message": (f"Contest magnitude ({row.unit_class}) carries a numeric cap "
                            f"{row.cap} — a capped defender half is a progression ceiling (PS-8)")})
    return findings


def scan(root: Path) -> dict:
    rows, counts = load_rows(root)
    findings = check(rows)
    by_rule: dict[str, int] = {}
    for finding in findings:
        by_rule[finding["rule"]] = by_rule.get(finding["rule"], 0) + 1
    return {
        "guard": "stat-pairs",
        "catalog": "/".join(CATALOG_RELATIVE),
        "rows": len(rows),
        "rows_per_group": counts,
        "findings": findings,
        "findings_by_rule": by_rule,
        "verdict": "FAIL" if findings else "OK",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: the counterbalance rule stays executable.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"STAT-PAIRS GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": "stat-pairs", "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        return 64

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding['rule']} {finding['name']}: {finding['message']}", file=sys.stderr)
    return 0 if result["verdict"] == "OK" else 1


if __name__ == "__main__":
    sys.exit(main())
