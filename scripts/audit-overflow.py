#!/usr/bin/env python3
"""
Numeric-overflow audit — INTEGER RANGE only. The power ladder makes old integer type choices wrong.

This tool audits integer range: a value exceeding what its integer type can hold (C# integer
arithmetic wraps silently unless `checked`). It does NOT audit floating point. Owner ruling
2026-09-15: the project-wide floating-point ban is removed — float/double are allowed for any
quantity (magnitudes, ratios, chances, rates, multipliers, curves). A float/double losing integer
exactness past 2^24/2^53 is PRECISION (rounding), not overflow, and is not a finding here.
Determinism of a double in a hashed golden is handled by a platform stamp (ssot-power-scale.md
§10.7), not by this audit. The former A1 (float magnitude) and A7 (double magnitude review) rules
were removed for that reason; the remaining category ids are kept stable.

Integer range thresholds, computed from the shipped curve (ssot-power-scale.md §4, B=0.4):

    int     per-mille                   Theta =   3,213
    int     whole units                 Theta = 103,557
    long                                Theta = 214,748,300 <- the default for integer magnitudes

Rules kept: long for integer magnitudes (A2/A3), widen before multiplying (A4/A5), integer overflow
throws rather than wraps (A6). Integer per-mille division happens last because integer division
truncates.

Usage (repo root):
    python gk-core/scripts/audit-overflow.py                    # full report
    python gk-core/scripts/audit-overflow.py --category A4      # one category
    python gk-core/scripts/audit-overflow.py --targets A3       # bare file:line list, for targeted work
    python gk-core/scripts/audit-overflow.py --fix A4           # apply the ONE safe automatic rewrite
    python gk-core/scripts/audit-overflow.py --paths src tools  # widen the scan (default: src)

Exit codes: 0 = no critical findings, 1 = critical findings present, 2 = usage error.
"""
import argparse, io, os, re, sys
from collections import defaultdict

# ── Classification ───────────────────────────────────────────────────────────
#
# The distinction the first version of this tool got wrong, and that matters most:
#
#   A per-mille RATIO   (chance, stability, share) is bounded 0..1000 and is SAFE in int forever.
#   A per-mille MAGNITUDE (hp, damage, yield) is unbounded and overflows int at Theta 3,213.
#
# Flagging every *Milli produced 112 findings of which nearly all were bounded ratios. An audit
# that cries wolf gets ignored, so the bar here is precision, not coverage.

# Something the power ladder can multiply.
#
# "hp" is matched case-SENSITIVELY as Hp/HP/hp (never hP) — under a case-INsensitive scan, the
# bare 2-letter token also matches the accidental "hP" that appears whenever some word ending in
# "h" abuts a word starting with "P" (KillEarnWithPatron -> "...Wit-hP-atron..."). Genuine hp
# usage is always Hp (capital H, lowercase p) or all-lowercase hp; "hP" never occurs by design, so
# excluding it loses no real finding. Found via PatronPolicy.cs:55 in the P0.3 triage.
MAGNITUDE = (r"(?:Hp|HP|hp)|(?i:atk|attack|damage|defen[cs]e|armou?r|arm1|arm2|magnitude|"
             r"yield|stock|loam|souls?|essence|shield|heal|absorb|potency)")

# Bounded 0..1000 (or otherwise capped) — per-mille here means "a fraction", never "a quantity".
RATIO = re.compile(
    r"(chance|stability|pressure|depletion|intensity|hazard|progress|share|weight|ratio|rate|"
    r"handicap|odds|probability|percent|opacity|alpha|volume|pitch|threshold|tolerance|"
    r"steepness|falloff|bias|jitter|variance|drift|multiplier|bonus|proc|loot|ceil|floor|"
    r"full|perstar|per_star|scalar|factor|mult)", re.I)

# An UNBOUNDED per-mille quantity — the only kind that can overflow. Evidence: a sweep of every
# *Milli in src/ found them all to be ratios. The repo's per-mille discipline is already correct,
# so A2 flags only names that denote an accumulating total.
UNBOUNDED_MILLI = re.compile(r"(stock|total|sum|balance|treasury|banked|accrued|lifetime|cumulative)", re.I)

# Not a magnitude at all: identifiers, counts, positions, timings.
#
# "unit" and "peractor" added by the P0.3 triage: ElementTable.ShieldUnit returns an elemental
# matchup tier (-1/0/1/2), not a shield amount; ShieldPolicy.MaxShieldsPerActor is a slot count.
# Both end in a word this list already exists to catch, so no new mechanism, just two more entries.
#
# "percontainer" added by item module 19 (granted-actions), the same shape as "peractor":
# ItemGrantLimits.MaxDefaultAttacksPerContainer is a ROW COUNT (how many `default-attack` rows one
# base type may declare), matched only because "DefaultAttacks" contains "attack". Nothing multiplies
# it and nothing scales it with Theta.
NOT_MAGNITUDE = re.compile(
    r"(id|ids|index|idx|count|len|length|size|version|revision|seed|hash|ms|millis|sec|seconds|"
    r"time|deltatime|unscaleddeltatime|tick|ticks|frame|fps|row|col|column|lane|slot|port|"
    r"priority|ordinal|rank|tier|band|level|x|y|z|width|height|capacity|cap|max|min|limit|"
    r"unit|units|peractor|percontainer)$", re.I)

# Word-level companions to NOT_MAGNITUDE (2026-09-18 re-triage, solid-enforcement SE3.15). The regex
# above is anchored at the END of the identifier, so it missed names whose count/position word sits
# in the middle: DefenseSlotsAtDevelopmentZero, WeightLowHp, attackerTheta, SoulTailPerPlayer,
# CountArmouryRows, MaxShieldSegments, KindShieldUpkeep. They matched only because a magnitude word
# (defense, hp, attack, soul, armour, shield) appears INSIDE a name for a count, weight, index or turn.
# A name containing any of these words is never a magnitude the power ladder multiplies.
NOT_MAGNITUDE_WORDS = {
    "slot", "slots", "weight", "weights", "theta", "turn", "turns", "tail", "tails", "row", "rows",
    "count", "counts", "segment", "segments", "bar", "bars", "drawn", "kind", "kinds", "index",
    "trial", "trials",  # CombatSim.Aggregate.ZeroDamageTrials: how many simulated trials, not damage
}


def name_words(name):
    """CamelCase / snake_case identifier -> lowercase words. `attackerTheta` -> [attacker, theta]."""
    return [w.lower() for w in re.findall(r"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", name)]


# An explicit, authored exemption for a site that is an int ON PURPOSE. Two legitimate reasons exist,
# and the marker must say which, so review can check it:
#   - a host boundary: a Harmony prefix must match PvZ's native `int` signature, and a value written
#     into a PvZ `int` field is bounded by that field (docs/architecture/numeric-types.md numeric rule 4, "Unity's own int
#     HP/attack fields are a host limit");
#   - a bounded ratio or score: a per-mille 0..1000 value that is a fraction, not a quantity.
# Same-line only, and the reason must be non-empty, so a marker cannot be applied by accident or
# hide a neighbour (docs/architecture/power/overflow-triage.md, 2026-09-18 section).
BOUNDED_MARKER = re.compile(r"//\s*overflow-bounded:\s*\S")

SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
SKIP_FILE = re.compile(r"\.Generated\.cs$|\.designer\.cs$", re.I)

# A4 false-positive filter. A regex cannot see operand types, and in these paths (timing,
# diagnostics, rendering, Unity interop) `(long)(a * b)` is a floating-point product converted to
# an integer — e.g. `(long)(dtSeconds * 1_000_000f)` — where no INTEGER multiply exists to wrap.
# This is not a floating-point permission list (floating point needs none); it only keeps A4, an
# integer-range rule, from misreading a float-domain conversion as an integer overflow.
FLOAT_DOMAIN_CAST_PATH = re.compile(
    r"(Diagnostics|Vfx|Overlay|Perf|Probability|Sigmoid|Random|Rng|Clock|InjectorLoop|"
    r"EventDrainHost|EffectRuntime|CheatActions|DebugActions|Host[\\/])", re.I)

CATEGORIES = {
    "A2": ("CRITICAL", "int on a per-mille MAGNITUDE - overflows at Theta 3,213"),
    "A3": ("HIGH",     "int on a magnitude - overflows at Theta 103,557; long is the default"),
    "A4": ("CRITICAL", "cast-after-multiply (long)(a*b) - the multiply already overflowed"),
    "A5": ("HIGH",     "int*int widened on assignment - widen an operand, not the result"),
    "A6": ("MEDIUM",   "unchecked on a magnitude path - overflow must throw, not wrap"),
}


def rules():
    m = MAGNITUDE
    # A3 doesn't pass re.I: MAGNITUDE handles its own case sensitivity (hp is case-sensitive; the
    # rest is wrapped in an inline (?i:...) group), and "int" is an always-lowercase C# keyword, so
    # a blanket re.I here would just re-fold "hp" back open.
    return [
        ("A2", re.compile(r"\bint\s+(\w*(?:milli|permille)\w*)\b", re.I)),
        # `int\??`: nullable ints were invisible until 2026-09-18 -- 29 of them, including the
        # standalone sim's entity Hp/MaxHp/Attack (SimModels.cs), which do scale with Theta.
        ("A3", re.compile(r"\b(?:public|private|internal|protected)?\s*(?:readonly\s+)?"
                          r"int\??\s+(\w*(?:%s)\w*)\b" % m)),
        ("A4", re.compile(r"\((?:long|ulong)\)\s*\([^()]*\*[^()]*\)")),
        ("A5", re.compile(r"\blong\s+\w+\s*=\s*(?!\(long\))[A-Za-z_]\w*\s*\*\s*[A-Za-z_]\w*\s*;")),
        ("A6", re.compile(r"\bunchecked\b")),
    ]


MILLI_SUFFIX = re.compile(r"(?:milli|permille)$", re.I)
CAP_SUFFIX = re.compile(r"(?:max|min|cap|limit)$", re.I)


def keep(cat, name, path, line):
    """The precision gate. Returns False for anything that cannot actually overflow."""
    if name and NOT_MAGNITUDE.search(name):
        # For A3 a cap-word suffix does not make a name a non-magnitude: every A3 candidate already
        # contains a magnitude word, so `ArmorMax` is the maximum OF armour, i.e. armour. The
        # end-anchored max/min/cap/limit exclusion hid exactly four such fields (all `ArmorMax`,
        # found 2026-09-18) and never a genuine count, which the word rule below already covers.
        if not (cat == "A3" and CAP_SUFFIX.search(name)):
            return False
    if name and NOT_MAGNITUDE_WORDS.intersection(name_words(name)):
        return False
    if cat == "A2":
        # Ratios are bounded and safe forever; only an accumulating total can overflow.
        if RATIO.search(name) or not UNBOUNDED_MILLI.search(name):
            return False
    if cat == "A3":
        # A3's identifier must contain a magnitude word (hp/damage/soul/...), but that word can
        # still name a per-mille RATIO rather than a magnitude: DefenseMilli, SoulLootMilli,
        # EssenceProcMilli are bonuses/chances bounded 0..~a few thousand, not accumulating
        # totals. A2 already draws exactly this line for pure "*Milli" names; A3 needs the same
        # exclusion because its own regex can match a magnitude word THEN a Milli suffix on one
        # identifier. Found via the P0.3 triage (SoulLootMilli and five siblings).
        if MILLI_SUFFIX.search(name) and not UNBOUNDED_MILLI.search(name):
            return False
    if cat == "A4" and FLOAT_DOMAIN_CAST_PATH.search(path):
        return False        # (long)(floatSeconds * n) is a float-to-integer conversion, not an int multiply
    if cat == "A6" and not re.search(MAGNITUDE, line, re.I):
        return False
    return True


def scan(paths):
    found, compiled = [], rules()
    for root in paths:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                if not fn.endswith(".cs") or SKIP_FILE.search(fn):
                    continue
                fp = os.path.join(dirpath, fn).replace("\\", "/")
                try:
                    lines = io.open(fp, encoding="utf-8-sig").read().splitlines()
                except (UnicodeDecodeError, OSError):
                    continue
                for i, line in enumerate(lines, 1):
                    stripped = line.strip()
                    if stripped.startswith(("//", "///", "*", "/*")):
                        continue                      # a comment is not code
                    if BOUNDED_MARKER.search(line):
                        continue                      # an authored, reasoned exemption on this line
                    # A trailing comment is prose, not code: "PvZ's int attack field" in a comment
                    # must not read as a declaration. (No shipped file puts "//" inside a string on a
                    # line these rules could match; if one ever does, it under-reports, never over.)
                    code = line.split("//", 1)[0]
                    for cat, rx in compiled:
                        # finditer, not search: a line declaring several ints ("int Weight, int Hp")
                        # used to report only the FIRST, so a filtered first name hid a real second one.
                        for mt in rx.finditer(code):
                            name = mt.group(1) if mt.groups() else ""
                            if not keep(cat, name, fp, code):
                                continue
                            found.append((cat, fp, i, stripped[:110]))
                            break                     # one finding per line per rule keeps --targets unique
    return found


def fix_a4(findings, apply):
    """(long)(a * b) -> (long)a * b. The only rewrite that is provably local and safe."""
    rx = re.compile(r"\((long|ulong)\)\s*\(([^()*]+)\*([^()]+)\)")
    by_file = defaultdict(list)
    for cat, fp, ln, _ in findings:
        if cat == "A4":
            by_file[fp].append(ln)
    changed = 0
    for fp, lns in sorted(by_file.items()):
        lines = io.open(fp, encoding="utf-8-sig").read().splitlines(True)
        for ln in lns:
            old = lines[ln - 1]
            new = rx.sub(lambda m: "(%s)%s* %s" % (m.group(1), m.group(2), m.group(3).strip()), old)
            if new != old:
                print("  %s:%d\n    - %s\n    + %s" % (fp, ln, old.strip(), new.strip()))
                lines[ln - 1] = new
                changed += 1
        if apply and changed:
            io.open(fp, "w", encoding="utf-8").writelines(lines)
    print("\n%s %d site(s)%s" % ("Rewrote" if apply else "Would rewrite", changed,
                                 "" if apply else "  (re-run with --fix A4 to apply)"))
    return changed


def main():
    ap = argparse.ArgumentParser(description="Integer-range overflow audit for the power ladder (does not audit floating point).")
    ap.add_argument("--paths", nargs="*", default=["src"])
    ap.add_argument("--category", help="report one category only")
    ap.add_argument("--targets", metavar="CAT", help="bare file:line list for targeted work")
    ap.add_argument("--fix", metavar="CAT", help="apply automatic rewrite (A4 only)")
    a = ap.parse_args()

    paths = [p for p in a.paths if os.path.isdir(p)]
    if not paths:
        print("no such path(s): %s" % a.paths, file=sys.stderr)
        return 2

    findings = scan(paths)

    if a.fix:
        if a.fix != "A4":
            print("Only A4 is auto-fixable.\n\n"
                  "Widening an integer type (A2/A3) ripples through every caller, DTO, serializer and\n"
                  "golden that touches it. There is no safe blanket rewrite; use --targets to get the\n"
                  "list and change them behind a compiler that will tell you what broke.",
                  file=sys.stderr)
            return 2
        return 0 if fix_a4(findings, apply=True) >= 0 else 1

    if a.targets:
        for cat, fp, ln, _ in findings:
            if cat == a.targets:
                print("%s:%d" % (fp, ln))
        return 0

    buckets = defaultdict(list)
    for f in findings:
        buckets[f[0]].append(f)

    print("Integer-range overflow audit  —  scanned: %s" % ", ".join(paths))
    print("=" * 100)
    crit = 0
    for cat in sorted(CATEGORIES):
        sev, desc = CATEGORIES[cat]
        rows = buckets.get(cat, [])
        if a.category and cat != a.category:
            continue
        print("\n%s  [%s]  %s" % (cat, sev, desc))
        print("-" * 100)
        if not rows:
            print("  clean")
            continue
        if sev == "CRITICAL":
            crit += len(rows)
        for _, fp, ln, txt in rows[:40]:
            print("  %s:%d\n      %s" % (fp, ln, txt))
        if len(rows) > 40:
            print("  ... and %d more (use --targets %s)" % (len(rows) - 40, cat))

    print("\n" + "=" * 100)
    print("  ".join("%s=%d" % (c, len(buckets.get(c, []))) for c in sorted(CATEGORIES)))
    print("total %d finding(s), %d critical" % (len(findings), crit))
    return 1 if crit else 0


if __name__ == "__main__":
    sys.exit(main())
