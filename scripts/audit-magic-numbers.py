#!/usr/bin/env python3
"""
Magic-number audit — the balance surface must be config, not code.

Standard: docs/architecture/tunables-ssot.md

    T1  a number a balance pass would change lives in config
    T2  a structural const says why it is not tunable
    T3  no bare numeric literal in a balance-surface file
    T6  every tunable carries its unit

Precision over coverage. `audit-overflow.py`'s first run reported 121 critical findings and every
one was a false positive; the exempt lists below are the lesson from that, not timidity.

Usage (repo root):
    python gk-core/scripts/audit-magic-numbers.py                  # full report
    python gk-core/scripts/audit-magic-numbers.py --domain contracts
    python gk-core/scripts/audit-magic-numbers.py --category M1
    python gk-core/scripts/audit-magic-numbers.py --targets M1     # bare file:line list
    python gk-core/scripts/audit-magic-numbers.py --summary        # per-domain counts only

Exit codes: 0 = clean, 1 = HIGH findings present, 2 = usage error.
"""
import argparse, io, os, re, sys
from collections import defaultdict

# A file whose whole job is balance. A literal here is a magic number by definition (T3).
BALANCE_FILE = re.compile(r"(Policy|Rules|Ruleset|Catalog|Math)\.cs$")

# Never a magic number: identity, arithmetic, per-mille denominator, common small factors.
# 1_000_000 = 1000^2, the same per-mille-squared renormalization denominator "1000" already covers,
# just for compound per-mille * per-mille arithmetic (ShieldMath.cs — two Pm quantities multiplied
# together land at per-million scale, and this divides back down). Found via the M.4 migration,
# 2026-08-23.
EXEMPT_LITERALS = {"0", "1", "-1", "2", "10", "100", "1000", "1_000", "1_000_000", "60", "24", "1024", "4096"}

# Balance vocabulary — a const named with one of these is tunable until argued otherwise (T1).
#
# "star" and "xp" are matched only when they end their camelCase word (not followed by a lowercase
# letter) — the overflow audit's "hp"/"WithPatron" lesson applies here too: bare "star" also matches
# inside "TurnStartMilli" (Turn-STARt-Milli, a turn-timeline bound), and bare "xp" matches inside
# "MaxExponent" (MaxE-XP-onent, an unrelated exponent — and, as it happens, the retired POC power
# curve's own constant, not something this migration needed to chase). Found via the M.2 (world) and
# M.4 (stats) migrations, 2026-08-23. Genuine hits (PerStarMilli, StarCap, XpMilli) are unaffected —
# the word there is always followed by an uppercase letter or the end of the identifier.
BALANCE_WORD = re.compile(
    r"(cost|price|rate|chance|odds|gain|loss|decay|yield|reward|bonus|penalty|malus|"
    r"damage|heal|regen|drain|threshold|weight|multiplier|scale|factor|step|tier|band|"
    r"duration|cooldown|interval|delay|upkeep|tribute|earn|award|drop|proc|crit|"
    r"loyalty|soul|essence|loam|xp(?![a-z])|level|slot|star(?![a-z])|rarity)", re.I)

# Structural vocabulary — correctness, not feel. Exempt from T1, still subject to T2.
#
# "epsilon" added via the M.4 migration, 2026-08-23: ElementPayload.WeightSumEpsilon matched
# "weight" (BALANCE_WORD) even though a floating-point comparison tolerance is never something a
# balance pass tunes — moving it to a tuning file would misrepresent it as a balance knob.
STRUCTURAL_WORD = re.compile(
    r"(version|capacity|buffer|queue|mailbox|ring|depth|nodes|json|hash|seed|"
    r"namespace|floor_?id|offset|bytes?|width|precision|encoding|schema|"
    r"maxsegments|pool|dispose|timeout_?ms|port|epsilon)", re.I)

# Named explicitly, not folded into a broader regex — same discipline CONTENT_FILE already uses:
# a deliberate, reviewable exception rather than a silent broadening of BALANCE_WORD/STRUCTURAL_WORD
# that could swallow a real future finding sharing the same substring.
#
# `KernelDriveHost.KindShieldUpkeep` (Injector/Effects) is an opaque scheduler-kind discriminator —
# its own doc comment: "Opaque ints to the queue by design — the scheduler never interprets them."
# It matches BALANCE_WORD only because "Shield" + "Upkeep" happen to be substrings of an id, the same
# accidental-substring class audit-overflow.py's own "WithPatron"/"hP" note already warns about.
#
# `KernelDriveHost.UpkeepPeriodTicks` is a scheduling GRANULARITY, not a balance rate — its own doc
# comment: "structural, not tunable... It stays 100 ms deliberately" (integer milli-HP regen would
# truncate to zero at finer granularity). T2 already requires this exact justification in a comment;
# this constant already carries it and was still flagged because "upkeep" is deliberately in
# BALANCE_WORD (a real resource-upkeep RATE elsewhere is a genuine balance dial).
#
# `VariantShift.MaxTier`/`MinTier` (Core/Effects/Atoms) — "which tier ROWS exist" (a schema fact: t5
# is the highest tier row authored, there is no t6), not a magnitude a balance pass scales. Its own
# doc comment names this exact audit by function: "named here so a later overflow/magic-number sweep
# does not flag ShiftTierWindow's clamp as an illegal cap." Matched via BALANCE_WORD's "tier", which
# elsewhere (a tier-scaled bonus, say) is correctly a real balance dial — the exemption is these two
# specific identifiers, not the word.
#
# `FamilyExpansion.TierCount`/`ReferenceLevel`/`BandFloorPermille`/`BandCeilingPermille` (E43,
# Core/Effects/Atoms/Generation) mirror `bands.v1.json`'s FROZEN `powerBand.tierScaling` values
# verbatim — the same "5, one per tier the atom layer already has" fact `MaxTier`/`MinTier` above are
# exempt for, and the same reasoning: a balance pass that wants to move these mints a v2 of that
# frozen, versioned registry file (its own `frozenNote`: "No in-place edit... registryVersion 2 plus
# an explicit decision"), it never edits this constant directly. Matched via BALANCE_WORD's
# "tier"/"level"/"band" — real per-family balance data (`sharePermille`) lives in
# `tier-bands.v1.json`, read at runtime by the same module, not as a `const` here.
# `TurnReadiness.SpeedScale` (battle-timeline T14/B28, 2026-09-04) is the readiness formula's UNIT OF
# MEASURE, and it matches BALANCE_WORD purely on the substring "scale" — the same accidental-substring
# class the two entries above are here for. "Scale" in BALANCE_WORD means a multiplier a balance pass
# turns up; here it means the scale numbers are *expressed in*. TicksFor computes
# `work × SpeedScale / rate` where the work supplied and the rate compared are in these same units, so
# changing it scales numerator and denominator together and cancels: nobody gets faster, the timeline is
# merely described at a different granularity. The half of that constant that IS a balance dial was
# split out in the same change and moved to config as derived-stats' `turnDefaultSpeed` — so exempting
# this name hides nothing, because the tunable half is no longer in code at all.
EXEMPT_NAMES = {
    # 2026-09-17, achievement-title. Two structural capacities that match BALANCE_WORD only through
    # "slot". Both already carry the T2 rationale at their declaration: 3 bounds the Hall layout and
    # the `hall-{1,2,3}` / `title-{1,2,3}` slot grammar, so changing the number changes what a slot id
    # MEANS, not how strong a loadout is. The knobs a balance pass turns are the share/cap tunables,
    # which are already config. Same shape as the already-exempt MaxStar.
    "HallSlots", "TitleSlots",
    # (2026-09-23) npc-story-events' `HostContentTheta.HomeworldDangerBand`. The name matches balance
    # vocabulary through "Danger", but the const is a SENTINEL, not a dial: the homeworld either has a
    # danger band or it does not, and its own declaration carries that rationale ("Structural, not
    # tunable — a place either has danger or it does not, and a balance pass that wanted the homeworld
    # to be dangerous would be redefining the homeworld"). Exempting the name hides no tunable: there
    # is no share/cap knob behind it and none is owed.
    "HomeworldDangerBand",
    # (2026-09-18) The deployment-hierarchy cache decay + retrieval entries that stood here are GONE:
    # their note said they come off "when that program lands its tuning file, and that is the
    # reviewable event". The file had landed. The values now live in
    # gk-core/data/tuning/deployment-hierarchy.v2.json (cacheDecay / cacheRetrieval), and the regression band
    # moved into the test that owns it (CacheDecayTests).
    "KindShieldUpkeep", "UpkeepPeriodTicks", "MaxTier", "MinTier",
    "TierCount", "ReferenceLevel", "BandFloorPermille", "BandCeilingPermille",
    "SpeedScale",
    # 2026-09-04: an ARRAY LENGTH — `new MeshRenderer?[MaxStatusTokens]` in ActorHudPool. §1 exempts
    # buffers by name; STRUCTURAL_WORD just does not happen to carry "token". Changing it does not
    # change how the game feels, it changes how many renderers the pool allocates.
    "MaxStatusTokens",
    # 2026-09-05: the star system's own RANGE and the anchor its reward curve is normalised around.
    # Both match BALANCE_WORD only through "star". fusion.v1.json's own _meta already records the
    # bound as structural ("SacrificesForStar's 1..5 bound and StarCap's rarity->cap shape stay
    # structural (the star system's own range), only the numbers a balance pass would tune moved"),
    # and the per-rarity cap that IS tunable lives in that file. ReferenceStar is the same kind of
    # thing as the already-exempt ReferenceLevel: move it and you redefine the curve rather than
    # retune it -- the knob a balance pass actually turns is perStarPowerMilli, which is config.
    "MaxStar", "ReferenceStar",
    # 2026-09-05, item module 17 (uniques). Two structural constants that match BALANCE_WORD only
    # through a substring, both already carrying the justification T2 requires:
    #
    # `UniqueBudget.AeScale = 100` matches "scale". It is not a scale a balance pass turns -- it is
    # the INTEGER ENCODING of the affix-equivalent unit. `item_unique.budget_ae` stores AE x 100
    # because SC4 forbids floats in content, so changing this changes what the column MEANS, not how
    # strong a unique is. The number a balance pass actually turns is budgetPremiumAeHundredths, which
    # is in gk-core/data/tuning/uniques.v1.json. Same kind of thing as the already-exempt ReferenceLevel.
    #
    # `UniqueLimits.FixedCoreChannelWeightMilli = 0` matches "weight". It is the one value in this
    # program that is zero BY CONSTRUCTION rather than by tuning: effect-pipeline L0 turns a power
    # class into a POOL RATE, and a unique's identity atoms are fixed-core rows that are never drawn,
    # so there is no draw for a weight to modify. Making it configurable would invite someone to set
    # it non-zero, which would weight a draw that does not exist. Its own doc comment says exactly
    # this, and ssot-uniques.md requires the comment to exist.
    "AeScale", "FixedCoreChannelWeightMilli",
    # 2026-09-05, item module 18 (consumables). Two structural constants that match BALANCE_WORD only
    # through a substring, both already carrying the AGENTS.md justification T2 requires:
    #
    # `ConsumableLimits.MinManifestCost = 1` matches "cost". It is not a price -- it is the FLOOR that
    # makes the belt limit a limit at all: a consumable occupying zero manifest places is free, so any
    # number of them fit in any belt and the carry rule refuses nothing. There is deliberately no
    # matching maximum, because a strong draught costing several places is what the column is for.
    #
    # `ConsumableLimits.UnbeltedSlots = 0` matches "slot". D37 is explicit that with no girdle equipped
    # the count is 0 and "not a default" -- an unequipped slot grants nothing, exactly as every other
    # role behaves. Making it configurable would reintroduce the global carry limit D37 withdrew, which
    # is the one thing gk-core/data/tuning/consumables.v1.json refuses BY NAME. The number a balance pass
    # actually moves is the girdle base type's own `consumableSlots`, which is content, not config.
    "MinManifestCost", "UnbeltedSlots",
    # 2026-09-05, party-dungeon difficulty-ladder wave. `DoorTypeCatalog.InertCostMultiplierMilli`
    # (Core/Delve) matches "cost" and "multiplier". It is not a march-cost balance dial -- nothing
    # marches in a delve (spec-dungeon-registries.md), so LaneTypeDef.CostMultiplierMilli is
    # completely inert there and this constant exists ONLY to satisfy LaneTypeCatalog's own unrelated
    # "a lane type must cost something to march" validator with a positive placeholder. Any positive
    # value produces an identical game outcome (none), which is exactly what T1's own test ("would a
    # balance pass ever want to change this number?") answers no to.
    "InertCostMultiplierMilli",
    # 2026-09-07, item-card gem inserts. `GemInsertCorpus.UnauthoredInsertTier` matches "tier" but is
    # not a balance dial -- it is the IDENTITY of "the corpus authors no tier for this gem", not a
    # number a pass would retune. Its own doc comment already says so: gems/*.json carries `powerBand`
    # (a different axis), so every insert reports the ladder's own minimum, which is provably SAFE
    # because `CombinationDistance` only ever UNDER-reports a resonance at the floor, never promises
    # one the evaluator would refuse. The day the corpus authors a real tier, this constant is deleted,
    # not retuned -- exactly T2's own "why this isn't tunable" test.
    "UnauthoredInsertTier",
}

SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
SKIP_FILE = re.compile(r"\.Generated\.cs$|\.designer\.cs$|Tests?\.cs$", re.I)

# "*Catalog.cs" is usually a reusable balance table — LaneTypeCatalog, WorldSizeCatalog — where a row
# applies to every instance of its kind and M1's bare-literal check is exactly right. WorldTemplateCatalog
# (world domain, M.2 migration, 2026-08-23) is different in kind despite the shared suffix: it is one
# hand-authored, one-off starting scenario per template (sector layout, lane geometry, entity
# placement) — level-design content, not a table a balance pass tunes independently of its neighbours.
# Forcing its numbers into a flat tuning file would fragment one coherent scenario across two files for
# no benefit a JSON sibling wouldn't also need. Named explicitly, not folded into the generic Catalog
# pattern, so this stays a deliberate, reviewable exception rather than a silent broadening.
CONTENT_FILE = re.compile(
    r"WorldTemplateCatalog(\.\w+)?\.cs$|VfxCatalog\.cs$|VfxAuraMath\.cs$|GameProfileCatalog\.cs$")

# Contexts where a literal is structure, not balance.
SKIP_LINE = re.compile(
    r"^\s*(//|///|\*|/\*)"                       # comments
    r"|\[.*\]"                                    # attributes
    r"|\bnameof\b|\bTypeId\b|\bGameTypeId\b"      # ids
    r"|^\s*case\s|^\s*=>\s*\d+\s*,?\s*$"          # switch arms over enums
    r"|\bnew\s+\w*Version\b|\bEngineVersion\b|\bPolicyVersion\b|\bRulesetVersion\b"
    r"|\.Substring\(|\[\d+\]|\bToString\(",       # indices / formatting
    re.I)

CATEGORIES = {
    "M1": ("HIGH",   "bare numeric literal in a balance-surface file (T3)"),
    "M2": ("HIGH",   "const with balance vocabulary - belongs in config (T1)"),
    "M3": ("MEDIUM", "const with no comment and no structural role (T2)"),
    "M4": ("LOW",    "tunable with no unit in its name (T6)"),
}

UNIT = re.compile(r"(milli|permille|ms$|ms[A-Z]|seconds?|minutes?|days?|permatch|perday|"
                  r"perkill|perturn|perlevel|perstar|kpm|pct|percent)", re.I)

CONST_RE  = re.compile(r"\bconst\s+(?:int|long|double|float|decimal)\s+(\w+)\s*=\s*([-\d_\.]+)")
LITERAL_RE = re.compile(r"(?<![\w.\"])(-?\d[\d_]*(?:\.\d+)?)(?![\w.\"])")


def domain_of(path):
    p = path.replace("\\", "/")
    for key in ("Contracts", "Loam", "Patron", "Fusion", "Shield", "Vfx", "Status", "Battle",
                "Overlay", "Ai", "Match", "Combat", "Expeditions", "Progression", "Creatures",
                "World", "Effects", "Stats"):
        if "/%s/" % key in p or p.endswith("/%s.cs" % key):
            return key.lower()
    return os.path.basename(os.path.dirname(p)).lower()


COMMENT_START = ("//", "///", "*", "/>")


def is_documented(lines, i, raw):
    """T2 asks a structural const to say why it is not tunable. Three shapes carry that comment, and
    the first version of this audit recognised only the first -- 13 of its 28 M3 findings on
    2026-09-18 were consts that DID carry their reason (solid-enforcement SE3.14):

      1. a comment on the line directly above;
      2. a trailing comment on the same line  (`const int MaxRows = 99; // id buffer bound`);
      3. a header comment above an UNBROKEN run of consts, which documents the whole group
         (`// three postures of four` above `PostureCount` and `PerPosture`). A blank line or any
         non-const line ends the group, so a header cannot reach an unrelated const further down.
    """
    code = re.sub(r'"[^"]*"', '""', raw)
    if re.search(r"//\s*\S", code):
        return True
    j = i - 2
    while j >= 0:
        prev = lines[j].strip()
        if prev.startswith(COMMENT_START):
            return True
        prev_code = re.sub(r"//.*$", "", re.sub(r'"[^"]*"', '""', prev)).strip()
        if prev_code and CONST_RE.search(prev_code):
            j -= 1                      # still inside the const group: keep walking up to its header
            continue
        return False                    # blank line or other code: the group has no header
    return False


def scan(paths):
    found = []
    for root in paths:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                if not fn.endswith(".cs") or SKIP_FILE.search(fn):
                    continue
                fp = os.path.join(dirpath, fn).replace("\\", "/")
                is_balance = bool(BALANCE_FILE.search(fn)) and not CONTENT_FILE.search(fn)
                try:
                    lines = io.open(fp, encoding="utf-8-sig").read().splitlines()
                except (UnicodeDecodeError, OSError):
                    continue
                for i, raw in enumerate(lines, 1):
                    # Strip trailing comments and string literals BEFORE scanning. A number inside
                    # `// extra damage below 50% own HP` is prose, not a magic number, and the first
                    # version of this tool flagged several of them.
                    code = re.sub(r'"[^"]*"', '""', raw)
                    code = re.sub(r"//.*$", "", code)
                    line = code.strip()
                    if not line or SKIP_LINE.search(code):
                        continue

                    m = CONST_RE.search(line)
                    if m:
                        name, val = m.group(1), m.group(2)
                        structural = bool(STRUCTURAL_WORD.search(name)) or name in EXEMPT_NAMES
                        balance = bool(BALANCE_WORD.search(name)) and name not in EXEMPT_NAMES
                        documented = is_documented(lines, i, raw)
                        if balance and not structural:
                            found.append(("M2", fp, i, line[:110], domain_of(fp)))
                            if not UNIT.search(name):
                                found.append(("M4", fp, i, line[:110], domain_of(fp)))
                        elif not structural and not documented and val not in EXEMPT_LITERALS:
                            found.append(("M3", fp, i, line[:110], domain_of(fp)))
                        continue

                    if is_balance:
                        for lit in LITERAL_RE.findall(line):
                            bare = lit.replace("_", "").lstrip("-")
                            # Single digits inline are arithmetic, not balance: `hp * 4 < maxHp`
                            # means "a quarter". Real balance values are 2+ digits (20, 250, 400).
                            # A meaningful single digit belongs in a named const anyway, where M2/M3
                            # catch it.
                            if len(bare.split(".")[0]) < 2:
                                continue
                            if bare in {x.replace("_", "") for x in EXEMPT_LITERALS}:
                                continue
                            found.append(("M1", fp, i, line[:110], domain_of(fp)))
                            break
    return found


def main():
    ap = argparse.ArgumentParser(description="Magic-number audit (tunables-ssot.md).")
    ap.add_argument("--paths", nargs="*", default=["src"])
    ap.add_argument("--category")
    ap.add_argument("--domain")
    ap.add_argument("--targets", metavar="CAT")
    ap.add_argument("--summary", action="store_true")
    a = ap.parse_args()

    paths = [p for p in a.paths if os.path.isdir(p)]
    if not paths:
        print("no such path(s): %s" % a.paths, file=sys.stderr)
        return 2

    found = scan(paths)
    if a.domain:
        found = [f for f in found if f[4] == a.domain.lower()]

    if a.targets:
        for cat, fp, ln, _, _ in found:
            if cat == a.targets:
                print("%s:%d" % (fp, ln))
        return 0

    if a.summary:
        per = defaultdict(lambda: defaultdict(int))
        for cat, _, _, _, dom in found:
            per[dom][cat] += 1
        print("%-16s %5s %5s %5s %5s   total" % ("domain", "M1", "M2", "M3", "M4"))
        print("-" * 52)
        for dom in sorted(per, key=lambda d: -sum(per[d].values())):
            c = per[dom]
            print("%-16s %5d %5d %5d %5d   %5d"
                  % (dom, c["M1"], c["M2"], c["M3"], c["M4"], sum(c.values())))
        print("-" * 52)
        print("%-16s %5d %5d %5d %5d   %5d" % ("TOTAL",
              *[sum(1 for f in found if f[0] == k) for k in ("M1", "M2", "M3", "M4")], len(found)))
        return 1 if any(f[0] in ("M1", "M2") for f in found) else 0

    buckets = defaultdict(list)
    for f in found:
        buckets[f[0]].append(f)

    print("Magic-number audit  —  scanned: %s" % ", ".join(paths))
    print("Standard: docs/architecture/tunables-ssot.md")
    print("=" * 100)
    high = 0
    for cat in sorted(CATEGORIES):
        if a.category and cat != a.category:
            continue
        sev, desc = CATEGORIES[cat]
        rows = buckets.get(cat, [])
        print("\n%s  [%s]  %s" % (cat, sev, desc))
        print("-" * 100)
        if not rows:
            print("  clean")
            continue
        if sev == "HIGH":
            high += len(rows)
        for _, fp, ln, txt, dom in rows[:30]:
            print("  %s:%d  [%s]\n      %s" % (fp, ln, dom, txt))
        if len(rows) > 30:
            print("  ... and %d more (--targets %s)" % (len(rows) - 30, cat))

    print("\n" + "=" * 100)
    print("  ".join("%s=%d" % (c, len(buckets.get(c, []))) for c in sorted(CATEGORIES)))
    print("total %d finding(s), %d high" % (len(found), high))
    return 1 if high else 0


if __name__ == "__main__":
    sys.exit(main())
