#!/usr/bin/env python3
"""
Reader census — which aptitude-tuning families have a shipped consumer, computed fresh.

class-system-todo.md P1.5/P1.6 produced this classification ONCE, by hand: a general-purpose
agent grepped src/ for readers and wrote the result into DerivedStatChannels.cs's
CombatFamilyUnitClass dict + gk-data/packs/fusion/data/seed/derived-stats/catalog.json's per-family notes. P1.5's own
verify line required "the census script is checked in and re-runnable" but no script was ever
built — gk-core/data/tuning/aptitudes.v2.json's _meta.measurable field has carried that one-time manual
count as free-form PROSE ever since, with no way to notice if it goes stale. This is that script
(class-system-todo.md P8.4).

WHAT "HAS A READER" MEANS (family granularity, matching familyRead's own granularity — not
per-resource-id/per-element): a channel family (e.g. "combat.power", "status.duration") has a
reader if ANY line under gk-core/src/FusionRpg.Core/ calls `.Get(...)` with a channel under that family's
prefix. Two detection modes:

  DIRECT   `<snapshot>.Get(DerivedStatChannels.X)` or `.Get(DerivedStatChannels.X(arg))` — X is a
           named constant or generator method declared in DerivedStatChannels.cs. Unambiguous:
           the identifier alone determines the family. High confidence.

  DYNAMIC  `<snapshot>.Get($"status.{family}.omni")` — a C# interpolated string literal passed
           straight to .Get(...), never going through a DerivedStatChannels.* identifier at all
           (ResistanceEvaluator.cs's status-potency reads are the one shipped example). The
           family is recovered by turning the template's literal (non-{}) fragments into a regex
           (holes become `[^.]*` — every shipped hole fills one dotted path segment or a
           mid-segment word fragment, never text containing a literal ".") and testing it against
           each family's own known concrete channel strings. THIS IS A HEURISTIC, not a proof: a
           regex cannot see that `family` is only ever assigned "duration"/"intensity" at the call
           site, so it can still over-match a template against a sibling channel that happens to
           fit the same literal shape. CONFIRMED CASE (worth reading before trusting DYNAMIC
           evidence blindly): ResistanceEvaluator.cs:331's `$"status.{family}.omni"` template
           matches status.duration.omni as intended, but its `[^.]*` hole is equally happy to
           consume the whole word "durationReduction", so it ALSO matches status.durationReduction
           .omni even though that channel's real evidence is line 334's sibling template two lines
           down. The verdict (status.durationReduction has a reader) is still correct either way —
           both templates live in the same function — but the CITED line can be the less specific
           of two matches. Every DYNAMIC hit is reported as "possible reader, verify manually",
           never with DIRECT's confidence, precisely because of cases like this one.

SCOPE — read this before comparing this script's total family count against P1.5's own "21 + 8 =
29": P1.5/P1.6 only classified the 29 families added by the H.1-H.7 catalog-extension registration
pass (derived-stats program, 2026-08-24) — NOT the 19 older, pre-existing families (the original
12 combat.* channels, the 5 progression.bonus.* channels, and status.power/status.resist) that
already had readers from before that program existed and were never part of the P1.5 pass. This
script has no such scope limit: it censuses all 48 families familyRead itself lists, every run.
Cross-checking this script's output against P1.5/P1.6's numbers is therefore only meaningful on
the 29-family subset that carries an explicit "Reader VERIFIED" / "No reader" citation in
catalog.json (--crosscheck does exactly that subset, not all 48).

Usage (repo root):
    python gk-core/scripts/audit-reader-census.py                 # full human-readable report
    python gk-core/scripts/audit-reader-census.py --summary        # counts only
    python gk-core/scripts/audit-reader-census.py --json            # machine-readable report (stdout)
    python gk-core/scripts/audit-reader-census.py --targets NOREADER   # bare family list, no reader
    python gk-core/scripts/audit-reader-census.py --targets READER     # bare family list, has a reader
    python gk-core/scripts/audit-reader-census.py --check            # compare vs _meta.measurable's prose
    python gk-core/scripts/audit-reader-census.py --crosscheck        # compare vs catalog.json's 21+8

Exit codes: 0 = ran cleanly (report modes) / --check or --crosscheck agrees with ground truth;
            1 = --check or --crosscheck found a disagreement; 2 = usage error.
"""
import argparse, io, json, os, re, sys
from collections import defaultdict
from pathlib import Path

# Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/scripts/lib/keepverse_roots.py).
# TWO roots here, not one: `gk-core/data/tuning/**` and `src/**` are the ENGINE repo (core), while
# `gk-data/packs/fusion/data/seed/**` is the CONTENT pack. Resolving both from one private `..` walk is what broke when the
# repo splits; the tokens kvsplit's `rules/scan.v1.json` `resolvers` looks for are `core_root(` and
# `content_root(`.
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import content_root, core_root  # noqa: E402  (the path shim above must run first)

CORE_ROOT = str(core_root())
CONTENT_ROOT = str(content_root())
# The LIVE shipped config = the highest aptitudes.v*.json, never a pinned literal (2026-09-02).
# Pinning meant the census measured an ARCHIVED file against the CURRENT registry, so a channel
# retirement made the archived file's own prose look stale when nothing about it had changed.
def _latest_aptitudes():
    import glob, re as _re
    d = os.path.join(CORE_ROOT, "data", "tuning")
    best, bestv = None, -1
    for f in glob.glob(os.path.join(d, "aptitudes.v*.json")):
        m = _re.search(r"aptitudes\.v(\d+)\.json$", f)
        if m and int(m.group(1)) > bestv: best, bestv = f, int(m.group(1))
    if best is None: raise SystemExit("no data/tuning/aptitudes.v*.json")
    return best

APTITUDES_PATH = _latest_aptitudes()
CHANNELS_CS_PATH = os.path.join(CORE_ROOT, "src", "FusionRpg.Core", "Stats", "Derived", "DerivedStatChannels.cs")
CATALOG_JSON_PATH = os.path.join(CONTENT_ROOT, "data", "seed", "derived-stats", "catalog.json")
SCAN_ROOT = os.path.join(CORE_ROOT, "src", "FusionRpg.Core")

SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
SKIP_FILE = re.compile(r"\.Generated\.cs$|\.designer\.cs$|Tests?\.cs$", re.I)
# The definitions file itself is never its own reader — excluded from the usage scan by name so
# a future const/method whose RHS literal happens to contain ".Get(" as text can't self-match.
CHANNELS_CS_BASENAME = os.path.basename(CHANNELS_CS_PATH)

CONST_RE = re.compile(r'public\s+const\s+string\s+(\w+)\s*=\s*"([^"]*)"\s*;')
METHOD_RE = re.compile(r'public\s+static\s+string\s+(\w+)\s*\([^)]*\)\s*=>\s*\$"([^"]*)"\s*;')
HOLE_RE = re.compile(r"\{([^{}]*)\}")

DIRECT_LINE_RE = re.compile(r"\.Get\(")
DIRECT_IDENT_RE = re.compile(r"DerivedStatChannels\.(\w+)")
DYNAMIC_CALL_RE = re.compile(r'\.Get\(\s*\$"([^"]*)"')


# ── gk-core/data/tuning/aptitudes.v2.json ────────────────────────────────────────────────────────────

def load_aptitudes():
    with io.open(APTITUDES_PATH, encoding="utf-8") as f:
        return json.load(f)


def family_list(apt):
    """familyRead's own keys, minus its two prose/note entries (_note, _resourceNote) — 48 on the
    shipped file. Order preserved as-authored (stable report ordering, not alphabetized away)."""
    return [k for k in apt["familyRead"].keys() if not k.startswith("_")]


def real_edges(apt):
    """edges is a JSON array mixing real {channel,source,kMilli} rows with bare {_group:"..."}
    section-comment markers (4 of the 490 array entries) — only rows with a channel are edges."""
    return [e for e in apt["edges"] if isinstance(e, dict) and "channel" in e]


def channel_to_family(channel, families):
    """Longest dot-segment prefix match: 'combat.crit.resist.damage.omni' must resolve to family
    'combat.crit.resist.damage', not the shorter 'combat.crit.resist' that is ALSO a real family
    and ALSO a segment-prefix of the same channel. Segment-based (not raw string prefix) so
    'combat.blockx' (hypothetical) could never falsely prefix-match family 'combat.block'."""
    chan_segs = channel.split(".")
    best = None
    for fam in families:
        fam_segs = fam.split(".")
        if chan_segs[: len(fam_segs)] == fam_segs:
            if best is None or len(fam_segs) > len(best.split(".")):
                best = fam
    return best


def edge_counts_by_family(edges, families):
    counts = defaultdict(int)
    unmapped = []
    for e in edges:
        fam = channel_to_family(e["channel"], families)
        if fam is None:
            unmapped.append(e["channel"])
            continue
        counts[fam] += 1
    return counts, unmapped


# ── gk-core/src/FusionRpg.Core/Stats/Derived/DerivedStatChannels.cs ─────────────────────────────────
#
# Parsed fresh every run (never hand-copied) so a new channel constant/method automatically joins
# the identifier table without this script needing an edit.

def parse_channel_definitions():
    text = io.open(CHANNELS_CS_PATH, encoding="utf-8-sig").read()

    consts = {}  # name -> literal string value
    for m in CONST_RE.finditer(text):
        consts[m.group(1)] = m.group(2)

    # Method templates resolve any {Identifier} hole that matches an already-known const name (the
    # "{XxxPrefix}" self-reference idiom, e.g. CombatPenetration(e) => $"{CombatPenetrationPrefix}.
    # {e.ToElementId()}") to that const's literal text; any other hole (a real parameter — element,
    # statusId, category, resourceId, ...) is left as a wildcard marker.
    WILDCARD = "\0"
    methods = {}  # name -> resolved template (literal text interspersed with WILDCARD markers)
    for m in METHOD_RE.finditer(text):
        name, template = m.group(1), m.group(2)

        def resolve(hole_m):
            token = hole_m.group(1).split(".")[0].strip()
            return consts[token] if token in consts else WILDCARD

        methods[name] = HOLE_RE.sub(resolve, template)

    # identifier -> its own literal value / resolved-prefix, for family mapping. A method's
    # "value" is the literal run before its first unresolved wildcard, trailing "." stripped —
    # e.g. "combat.penetration\0.\0" -> "combat.penetration".
    identifier_value = dict(consts)
    for name, resolved in methods.items():
        prefix = resolved.split(WILDCARD, 1)[0].rstrip(".")
        if prefix:
            identifier_value[name] = prefix

    return identifier_value


def parse_combat_family_unit_class(identifier_value):
    """CombatFamilyUnitClass dict (DerivedStatChannels.cs ~line 291) — a COMBAT-ONLY partial
    mirror of catalog.json's fuller per-family classification (it has no status.*/resource.*/
    skill.*/move.*/progression.* entries at all — confirmed by reading the file). Used only as a
    belt-and-braces self-consistency check that catalog.json hasn't drifted from the C# SSOT it
    mirrors, not as this script's primary reader evidence.

    Returns the set of family keys (resolved through identifier_value where the dict key is a
    bare identifier like CombatShieldCapacityPrefix rather than a quoted literal) that
    CombatFamilyUnitClass assigns ANY UnitClass to."""
    text = io.open(CHANNELS_CS_PATH, encoding="utf-8-sig").read()
    m = re.search(r"CombatFamilyUnitClass\s*=\s*new Dictionary<string, UnitClass>\([^)]*\)\s*\{(.*?)\n\s*\};",
                  text, re.S)
    if not m:
        return set()
    block = m.group(1)
    keys = set()
    for km in re.finditer(r'\[\s*(?:"([\w.]+)"|(\w+))\s*\]\s*=\s*UnitClass\.', block):
        literal, ident = km.group(1), km.group(2)
        if literal:
            keys.add(literal)
        elif ident in identifier_value:
            keys.add(identifier_value[ident])
    return keys


# ── gk-data/packs/fusion/data/seed/derived-stats/catalog.json — the P1.5/P1.6 ground truth ───────────────────────

def load_catalog_ground_truth():
    """Returns {family: 'reader' | 'no-reader'} for exactly the families catalog.json's own
    entries[].note / entries[].unitClassNote carry an explicit P1.5/P1.6 citation for — the
    literal strings the census itself wrote ("Reader VERIFIED ... (class-system P1.5)" and
    "No reader:" / "No shipped reader"). Families outside this set were never part of that
    pass (see the SCOPE note in this file's docstring) and are not ground truth either way."""
    with io.open(CATALOG_JSON_PATH, encoding="utf-8") as f:
        catalog = json.load(f)
    truth = {}
    for e in catalog["entries"]:
        fam = e.get("family")
        note = (e.get("note") or "")
        ucn = (e.get("unitClassNote") or "").lower()
        if "reader verified" in note.lower():
            truth[fam] = "reader"
        elif "no reader" in ucn or "no shipped reader" in ucn:
            truth[fam] = "no-reader"
    return truth


# ── gk-core/src/FusionRpg.Core/ usage scan ───────────────────────────────────────────────────────────

def scan_usage(identifier_value, families, edges_by_family_channels):
    """Walks gk-core/src/FusionRpg.Core/ once, returns {family: {"direct": [...evidence], "dynamic":
    [...evidence]}}. Evidence entries are "relative/path.cs:LINE"."""
    hits = defaultdict(lambda: {"direct": [], "dynamic": []})

    # Per-family candidate concrete channel strings for DYNAMIC template matching — the family key
    # itself, family+".omni" (the universal shipped default variant), and every concrete channel
    # string that actually appears in the tuning file's edges for that family.
    candidates = defaultdict(set)
    for fam in families:
        candidates[fam].add(fam)
        candidates[fam].add(fam + ".omni")
    for fam, chans in edges_by_family_channels.items():
        candidates[fam].update(chans)

    for dirpath, dirnames, filenames in os.walk(SCAN_ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith(".cs") or fn == CHANNELS_CS_BASENAME or SKIP_FILE.search(fn):
                continue
            fp = os.path.join(dirpath, fn)
            rel = os.path.relpath(fp, CORE_ROOT).replace("\\", "/")
            try:
                lines = io.open(fp, encoding="utf-8-sig").read().splitlines()
            except (UnicodeDecodeError, OSError):
                continue

            for i, raw in enumerate(lines, 1):
                stripped = raw.strip()
                if stripped.startswith(("//", "///", "*", "/*")):
                    continue
                if not DIRECT_LINE_RE.search(raw):
                    continue

                # DIRECT: any DerivedStatChannels.X identifier on a line that also calls .Get(.
                for idm in DIRECT_IDENT_RE.finditer(raw):
                    ident = idm.group(1)
                    value = identifier_value.get(ident)
                    if value is None:
                        continue
                    fam = channel_to_family(value, families)
                    if fam is None:
                        continue
                    hits[fam]["direct"].append("%s:%d" % (rel, i))

                # DYNAMIC: .Get($"...") — interpolated literal, no DerivedStatChannels.* identifier.
                dm = DYNAMIC_CALL_RE.search(raw)
                if dm:
                    template = dm.group(1)
                    frags = [f for f in HOLE_RE.split(template)]
                    # HOLE_RE.split alternates literal, hole, literal, hole, ... — odd indices are
                    # hole contents (discarded), even indices are the literal fragments to anchor on.
                    literals = frags[0::2]
                    if any(literals):  # at least one non-empty literal fragment to anchor against
                        # [^.]* (not .*?) between fragments — every shipped dynamic hole (family,
                        # category, statusId) fills exactly one dotted path segment or a mid-segment
                        # word fragment (the "{family}Reduction" idiom), never text containing a
                        # literal ".". This is a real, deliberate precision choice, not a free
                        # tightening: without it, the "{family}Reduction.omni" template's hole can
                        # swallow "duration." and land on a completely different family's ".omni"
                        # candidate, and status.duration's own "{family}.omni" template can just as
                        # easily swallow "durationReduction" the same way — confirmed by hand: an
                        # earlier .*? version cited status.duration's evidence lines for
                        # status.durationReduction too, which is wrong even though both families
                        # happen to have a real reader regardless. [^.]* still cannot see that
                        # `family` is only ever assigned "duration"/"intensity" at the call site
                        # (that needs real semantic analysis, not a regex) — DYNAMIC matches stay a
                        # heuristic, "verify manually", never DIRECT's confidence.
                        pattern = "[^.]*".join(re.escape(x) for x in literals)
                        rx = re.compile(pattern)
                        for fam, cands in candidates.items():
                            if any(rx.search(c) for c in cands):
                                hits[fam]["dynamic"].append("%s:%d" % (rel, i))

    return hits


# ── report assembly ──────────────────────────────────────────────────────────────────────────

def build_report():
    apt = load_aptitudes()
    families = family_list(apt)
    edges = real_edges(apt)
    edge_counts, unmapped = edge_counts_by_family(edges, families)

    edges_by_family_channels = defaultdict(set)
    for e in edges:
        fam = channel_to_family(e["channel"], families)
        if fam:
            edges_by_family_channels[fam].add(e["channel"])

    identifier_value = parse_channel_definitions()
    usage = scan_usage(identifier_value, families, edges_by_family_channels)
    read_mode = apt["familyRead"]

    rows = []
    for fam in families:
        direct = usage[fam]["direct"]
        dynamic = usage[fam]["dynamic"]
        has_reader = bool(direct or dynamic)
        confidence = "direct" if direct else ("dynamic-heuristic" if dynamic else "none")
        evidence = (direct or dynamic)[:3]
        rows.append({
            "family": fam,
            "has_reader": has_reader,
            "confidence": confidence,
            "evidence": evidence,
            "edge_count": edge_counts.get(fam, 0),
            "read_mode": read_mode.get(fam),
        })

    edges_total = len(edges)
    reader_less = [r for r in rows if not r["has_reader"]]
    edges_reserved = sum(r["edge_count"] for r in reader_less)

    return {
        "families_total": len(families),
        "families_with_reader": len(rows) - len(reader_less),
        "families_without_reader": len(reader_less),
        "edges_total": edges_total,
        "edges_unmapped": unmapped,
        "edges_reserved": edges_reserved,
        "edges_reserved_pct": round(100.0 * edges_reserved / edges_total, 1) if edges_total else 0.0,
        "families": rows,
        "reader_less_families": [r["family"] for r in reader_less],
    }


# ── _meta.measurable prose parsing (--check) ─────────────────────────────────────────────────

MEASURABLE_FAMILIES_RE = re.compile(r"(\d+)\s+families with no shipped reader")
MEASURABLE_EDGES_RE = re.compile(r"(\d+)\s+of\s+(\d+)\s+edges\s*/\s*(\d+)\s*%")


def parse_measurable_claim(apt):
    text = apt["_meta"]["measurable"]
    fm = MEASURABLE_FAMILIES_RE.search(text)
    em = MEASURABLE_EDGES_RE.search(text)
    if not fm or not em:
        return None
    return {
        "families_without_reader": int(fm.group(1)),
        "edges_reserved": int(em.group(1)),
        "edges_total": int(em.group(2)),
        "edges_reserved_pct": int(em.group(3)),
    }


def cmd_check(report, apt):
    claim = parse_measurable_claim(apt)
    print("_meta.measurable check")
    print("=" * 100)
    if claim is None:
        print("COULD NOT PARSE _meta.measurable's prose — the expected phrasing "
              '("N families with no shipped reader...", "M of T edges / P%") was not found.')
        print("Treat this as a FAIL: the field changed shape and this script was not updated to match.")
        return 1

    computed_pct_int = int(round(report["edges_reserved_pct"]))
    checks = [
        ("families with no shipped reader", claim["families_without_reader"], report["families_without_reader"]),
        ("edges reserved", claim["edges_reserved"], report["edges_reserved"]),
        ("edges total", claim["edges_total"], report["edges_total"]),
        ("edges reserved pct", claim["edges_reserved_pct"], computed_pct_int),
    ]
    ok = True
    for label, claimed, computed in checks:
        status = "OK" if claimed == computed else "MISMATCH"
        if claimed != computed:
            ok = False
        print("  %-32s claimed=%-6s computed=%-6s  [%s]" % (label, claimed, computed, status))

    print()
    if ok:
        print("PASS — _meta.measurable agrees with a fresh census.")
        return 0
    print("FAIL — _meta.measurable is STALE relative to a fresh census. See scripts/audit-reader-census.py "
          "--json for the current per-family breakdown; the file's own prose needs a manual rewrite "
          "(_meta.measurable is prose, not something this script or publish.py may edit for you).")
    return 1


# ── ground-truth crosscheck (--crosscheck) ───────────────────────────────────────────────────

def cmd_crosscheck(report):
    truth = load_catalog_ground_truth()
    identifier_value = parse_channel_definitions()
    cfuc_families = parse_combat_family_unit_class(identifier_value)

    by_family = {r["family"]: r for r in report["families"]}
    print("Crosscheck vs data/seed/derived-stats/catalog.json's P1.5/P1.6 citations "
          "(%d families carry an explicit citation)" % len(truth))
    print("=" * 100)
    mismatches = []
    for fam, expected in sorted(truth.items()):
        row = by_family.get(fam)
        if row is None:
            mismatches.append((fam, expected, "MISSING from familyRead"))
            continue
        computed = "reader" if row["has_reader"] else "no-reader"
        status = "OK" if computed == expected else "MISMATCH"
        if computed != expected:
            mismatches.append((fam, expected, computed))
        print("  %-34s ground-truth=%-10s computed=%-10s  [%s]" % (fam, expected, computed, status))

    print()
    print("Secondary check: CombatFamilyUnitClass (C#) vs catalog.json agreement")
    print("-" * 100)
    cfuc_mismatches = []
    for fam in sorted(cfuc_families):
        if truth.get(fam) == "no-reader":
            cfuc_mismatches.append(fam)
            print("  %-34s has a UnitClass in CombatFamilyUnitClass but catalog.json says NO READER" % fam)
    for fam, verdict in sorted(truth.items()):
        if verdict == "reader" and fam.startswith("combat.") and fam not in cfuc_families \
                and fam != "combat.heal.power":
            # combat.heal.power is H.4 (Pool, unpaired) and was never added to CombatFamilyUnitClass
            # by design (it is not part of the H.1 element-typed generation this dict exists for) —
            # not a drift, so excluded here explicitly rather than silently matching everything.
            print("  %-34s catalog.json says READER but has no CombatFamilyUnitClass entry (informational — "
                  "CombatFamilyUnitClass is combat.*-only by design, so this may be expected)" % fam)
    if not cfuc_mismatches:
        print("  no contradictions found")

    print()
    if not mismatches:
        print("PASS — every one of the %d ground-truth-cited families agrees with this script's own "
              "fresh computation." % len(truth))
        return 0

    print("FAIL — %d disagreement(s) with established ground truth:" % len(mismatches))
    for fam, expected, computed in mismatches:
        print("  %-34s ground-truth=%-10s computed=%s" % (fam, expected, computed))
    print()
    print("A disagreement usually means this script's detection logic has a bug — that is the default")
    print("assumption, and the evidence lines above (this script's normal report, or --json) are where to")
    print("start. But ground truth is not infallible either: it is a dated snapshot (catalog.json's own")
    print("citations name class-system P1.5/P1.6, 2026-08-26), and code keeps moving. Before concluding")
    print('"script bug", check whether the family\'s own evidence lines sit in a file `git status`/`git log`')
    print("shows as newer than that citation date — a family can gain (or lose) a real reader after the")
    print("census that classified it ran, and a re-runnable script reporting THAT is the entire reason")
    print("P8.4 exists. Report which explanation applies with the git evidence attached either way.")
    return 1


# ── output ────────────────────────────────────────────────────────────────────────────────────

def print_report(report, only=None):
    print("Reader census  —  scanned: %s" % os.path.relpath(SCAN_ROOT, CORE_ROOT).replace("\\", "/"))
    print("Family list source: %s (familyRead, %d families)"
          % (os.path.relpath(APTITUDES_PATH, CORE_ROOT).replace("\\", "/"), report["families_total"]))
    print("=" * 100)
    for r in report["families"]:
        if only == "reader" and not r["has_reader"]:
            continue
        if only == "noreader" and r["has_reader"]:
            continue
        marker = {"direct": "READER (direct)", "dynamic-heuristic": "READER (dynamic, verify manually)",
                  "none": "no reader"}[r["confidence"]]
        print("  %-34s %-32s edges=%-4d mode=%s" % (r["family"], marker, r["edge_count"], r["read_mode"]))
        for ev in r["evidence"]:
            print("      %s" % ev)

    if only:
        return
    print("\n" + "=" * 100)
    print("families: %d total, %d with a reader, %d without"
          % (report["families_total"], report["families_with_reader"], report["families_without_reader"]))
    print("edges: %d total, %d belong to a reader-less family (%.1f%%)"
          % (report["edges_total"], report["edges_reserved"], report["edges_reserved_pct"]))
    if report["edges_unmapped"]:
        print("WARNING — %d edge channel(s) matched no family in familyRead: %s"
              % (len(report["edges_unmapped"]), report["edges_unmapped"]))


def print_summary(report):
    print("Reader census summary")
    print("-" * 60)
    print("  families with a reader:    %3d / %d" % (report["families_with_reader"], report["families_total"]))
    print("  families without a reader: %3d / %d" % (report["families_without_reader"], report["families_total"]))
    print("  reader-less: %s" % ", ".join(report["reader_less_families"]))
    print("  edges reserved (no reader): %d / %d (%.1f%%)"
          % (report["edges_reserved"], report["edges_total"], report["edges_reserved_pct"]))


def main():
    ap = argparse.ArgumentParser(description="Reader census for aptitude-tuning families (class-system-todo.md P8.4).")
    ap.add_argument("--json", action="store_true", help="machine-readable report to stdout")
    ap.add_argument("--summary", action="store_true", help="counts only")
    ap.add_argument("--targets", choices=["READER", "NOREADER"], help="bare family-name list, one mode only")
    ap.add_argument("--check", action="store_true", help="compare computed numbers vs _meta.measurable's prose")
    ap.add_argument("--crosscheck", action="store_true", help="compare vs catalog.json's P1.5/P1.6 citations")
    a = ap.parse_args()

    if not os.path.isfile(APTITUDES_PATH):
        print("missing %s" % APTITUDES_PATH, file=sys.stderr)
        return 2
    if not os.path.isfile(CHANNELS_CS_PATH):
        print("missing %s" % CHANNELS_CS_PATH, file=sys.stderr)
        return 2
    if not os.path.isdir(SCAN_ROOT):
        print("missing scan root %s" % SCAN_ROOT, file=sys.stderr)
        return 2

    report = build_report()

    if a.check:
        return cmd_check(report, load_aptitudes())
    if a.crosscheck:
        return cmd_crosscheck(report)
    if a.targets:
        for r in report["families"]:
            if a.targets == "READER" and r["has_reader"]:
                print(r["family"])
            elif a.targets == "NOREADER" and not r["has_reader"]:
                print(r["family"])
        return 0
    if a.json:
        print(json.dumps(report, indent=2))
        return 0
    if a.summary:
        print_summary(report)
        return 0

    print_report(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
