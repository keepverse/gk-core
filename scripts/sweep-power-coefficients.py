#!/usr/bin/env python3
"""
Power-coefficient sweep — E44 `power-sweep`, acceptance criteria 1 and 3.

Spec: docs/architecture/effect-atom/spec-power-sweep.md §4.1
SSOT: docs/architecture/power/ssot-power-scale.md (numeric contract: long, widen before
multiplying, divide by 1000 last, overflow throws)

WHAT THIS DOES
--------------
Loads the real generated-atom corpus under gk-data/packs/fusion/data/seed/atoms/generated/ (E43 `family-expand`,
currently 45 rows across 3 files: g-armour.json, g-attack.json, g-life.json) and computes the real
observed magnitude distribution per (kind, channel), split by `op` — `flat` (a raw stat-unit
delta), `increased` / `more` (a percentage modifier). Both share the same channel and therefore the
same `CoefficientTable` row, but are NOT the same unit — see FINDING 2 below.

`CoefficientTable`'s own contract (`PowerCoefficientRow.ReferenceScale`'s doc comment) ties
`ReferenceScale` to "what one RAW unit means for this channel" — i.e. the `flat` op's own unit.
That is the one sub-corpus this table's key granularity (kind × channel, no `op` axis) can actually
fit without conflating two different units under one number. This script therefore fits `CoeffMilli`
(the value spec §2/§4.1 names as "the flat 1000s") from the median `flat`-op magnitude of each
channel, holding `ReferenceScale` at its existing authored value (2/2/10/10 — already a reasonable,
pre-existing per-channel unit pick that this sweep does not need to re-derive, since the spec's own
inventory table already lists ReferenceScale as the dial that "varies", only CoeffMilli is flat).

FORMULA (integer, per-mille, one rounding point — mirrors CostFunction.PriceForChannel exactly):

    normalizedMilli(mag)  = round(mag * 1000 / referenceScale)     # div-round half away from zero
    price(mag, coeffMilli) = round(normalizedMilli(mag) * coeffMilli / 1000)

    fittedCoeffMilli(channel) = round(
        TARGET_POINTS * 1000 / normalizedMilli(medianFlatMagnitude(channel))
    )

TARGET_POINTS = 1000 mirrors the existing "one reference unit = 1000 pts" convention already used
elsewhere in this codebase (`RungPowerBudgetTests`' own `referencePower = PowerMath.One`) — not a
number invented for this sweep.

WHY MEDIAN, NOT MEAN: only 5 tiers per family; the median (=tier 3, the middle tier) is the same
robust "pin one representative point, not an average of a range no one exemplar hits" choice
ssot-power-scale.md §4.3 uses for its own single calibration point (`P(20) = 680`).

FINDING 1 (fit result): with CoeffMilli fitted this way, the four channels' median (tier-3) `flat`
atom prices converge from a 3.7x spread (might/atk=4500, warding/defense=2000, mending/hp=7400,
vitality/maxHp=7400, under the flat-1000 baseline) to 999-1000 across the board — measured, not
asserted; see the printed table.

FINDING 2 (structural limit, reported honestly per spec §7 criterion 7 — NOT fixed here, out of
scope): `increased`/`more` atoms sharing these same 4 channels carry percentage magnitudes (e.g.
23-47 at tier 1, regardless of channel) that are numerically indistinguishable, at this table's
(kind, channel) key granularity, from a `flat` atom's raw-unit magnitude. No choice of CoeffMilli or
ReferenceScale can reconcile the two without widening the coefficient key to include `op` — a
CostFunction/CoefficientTable lookup-key change, out of E44's scope (a coefficient-DATA change).
This is scale-invariant: rescaling CoeffMilli or ReferenceScale moves both op-classes by the same
factor and never changes the ratio between them. Reported so a later module owns it explicitly
rather than rediscovering it.

COVERAGE — ⛔ CORRECTED 2026-09-05. An earlier pass of this script claimed the other 16 rows "have
zero real corpus under gk-data/packs/fusion/data/seed/atoms/ today". **That was wrong**, and only true of the GENERATED
sub-tree it happened to glob. The SHIPPED catalog (`data/seed/atoms/fx-*.json`) carries real atoms
for status.apply (6), spawn.entity (3), shield.grant (3), and one each of board.action, grid.spawn,
grid.clear, box.set, resource.delta, resource.economy, status.clear, stat.derived. That catalog is
what actually ships to players, so it is real content, not the "synthetic data" §5 forbids fitting
against.

The real reason most of those rows still cannot be FITTED is sharper, structural, and permanent —
it is a property of the kind, not a shortage of content. `CostFunction.MeanMagnitude` walks a kind's
own declared params for the first `ParamKind.Value` one; **a kind that declares none has no
magnitude at all and returns a fixed `1`** ("one reference unit ... so it prices as 'one of whatever
this kind does'", its own doc comment). For those kinds every atom prices identically no matter how
much content exists, so there is no distribution to fit and the coefficient is a pure balance-policy
choice, not a measurement. No amount of future content changes that. Measured against the real
registry and the real corpus, per row:

  * FITTABLE, real distribution ... stat.modify atk/defense/hp/maxHp (fitted, below) and
                                    status.apply (`duration`: 2,3,3,4,5,5 — fitted, below)
  * single real value only ....... resource.economy (amount=25, n=1), stat.derived (amount=150, n=1)
                                    — a normalisation of one point, not a fit; left authored
  * NO magnitude param at all .... status.clear, shield.grant, board.action, grid.spawn, grid.clear,
                                    box.set, resource.delta — magnitude is structurally fixed at 1;
                                    coefficient is a policy choice, permanently unfittable
  * priced off a DIFFERENT path .. spawn.entity — its body goes through
                                    `ActorPowerCache.PriceBody(hp, atk)` (CostFunction.SpawnBody),
                                    not this coefficient's normalisation, so its hp/atk magnitudes
                                    must NOT be fitted into this row
  * no real atom of any kind ..... arm1/arm1Max/arm2/arm2Max — genuinely awaiting content

Usage (repo root):
    python gk-core/scripts/sweep-power-coefficients.py             # print the fit + full report
    python gk-core/scripts/sweep-power-coefficients.py --json       # also print the coefficients.v1.json
                                                             # entries this fit produces (for review)

Exit codes: 0 always (this is a research/report tool, not a gate).
"""
import argparse
import glob
import json
import re
import statistics
from collections import defaultdict

ONE = 1000
TARGET_POINTS = 1000  # one reference unit == 1000 pts (PowerMath.One's own convention)

# The existing, authored, PRE-EXISTING referenceScale for these four channels
# (CoefficientTable.cs Authored(), untouched by this sweep — only CoeffMilli is fitted).
AUTHORED_REFERENCE_SCALE = {"atk": 2, "defense": 2, "hp": 10, "maxHp": 10}

CORPUS_GLOB = "data/seed/atoms/generated/family-expand.*.json"

#: The SHIPPED catalog — real content, what a player actually gets. Globbed separately from the
#: generated tree because an earlier pass of this script only ever looked at the latter and
#: concluded, wrongly, that every other coefficient row had "zero real corpus".
#:
#: Deliberately EVERY committed `*.json` at the top of `gk-data/packs/fusion/data/seed/atoms/`, not an `fx-*` pattern:
#: a first cut of this used `fx-*.json` and silently missed `trait-critical-hunter.json` (the only
#: real `stat.derived` atom in the repo). Naming a shape rather than a location is how a corpus
#: sweep quietly under-reports, which is the exact failure this whole correction exists to undo.
SHIPPED_GLOB = "data/seed/atoms/*.json"

#: Registry source of truth for which param carries a kind's magnitude. Parsed from the live C#
#: rather than hand-mirrored, the same discipline `ParamParityGuardTests` uses for the same reason:
#: a hand-copied list silently goes stale the first time a kind gains or renames a param.
REGISTRY_CS = "src/FusionRpg.Core/Effects/Atoms/AtomKindRegistry.cs"

#: `spawn.entity`'s body is priced by `ActorPowerCache.PriceBody(hp, atk)` inside
#: `CostFunction.SpawnBody`, NOT by this coefficient's normalisation — so its hp/atk/x/y magnitudes
#: must never be fitted into this row. Named here rather than silently skipped.
FIT_EXCLUDED_KINDS = {"spawn.entity"}


def kind_value_params(root: str = ".") -> "dict[str, list[str]]":
    """kindId -> its declared `ParamKind.Value` param names, in declaration order — which is the
    order `CostFunction.MeanMagnitude` itself scans, so the FIRST one present on an atom is the
    magnitude that kind actually prices on."""
    src = open(f"{root}/{REGISTRY_CS}", encoding="utf-8").read()
    starts = [(m.start(), m.group(1)) for m in re.finditer(r'\bnew\("([a-z][a-z.]+)",\s*AttachPoint\.', src)]
    out: "dict[str, list[str]]" = {}
    for i, (pos, kid) in enumerate(starts):
        end = starts[i + 1][0] if i + 1 < len(starts) else len(src)
        out[kid] = re.findall(r'new ParamDef\("([^"]+)",\s*ParamKind\.Value', src[pos:end])
    return out


def mean_magnitude(params: dict, value_params: "list[str]"):
    """`CostFunction.MeanMagnitude`, reimplemented exactly: the first declared Value param present
    wins; a range reads as its own mean; NO Value param at all means the kind has no magnitude and
    C# returns a fixed 1 -- represented here as `None` so the caller can tell "structurally has no
    magnitude" apart from "really measured 1"."""
    for name in value_params:
        if name in params:
            v = params[name]
            if isinstance(v, dict) and "min" in v and "max" in v:
                return div_round(int(v["min"]) + int(v["max"]), 2)
            if isinstance(v, bool):
                continue
            if isinstance(v, int):
                return v
    return None


def load_shipped_corpus(root: str = "."):
    """Every real shipped atom, keyed the way `CoefficientTable.Find` keys it: (kindId, channel),
    with channel empty for the channel-less kinds."""
    vps = kind_value_params(root)
    rows = defaultdict(list)
    for f in sorted(glob.glob(f"{root}/{SHIPPED_GLOB}")):
        for e in json.load(open(f, encoding="utf-8")).get("entries", []):
            kind = e.get("kind")
            params = e.get("params") or {}
            rows[(kind, params.get("channel", ""))].append({
                "file": f, "family": e.get("family"), "tier": e.get("tier"),
                "mag": mean_magnitude(params, vps.get(kind, [])),
            })
    return rows


def fit_one(mags: "list[int]", ref_scale: int):
    """The identical pin used for the channel fit: put the MEDIAN real atom at TARGET_POINTS."""
    med = int(statistics.median(sorted(mags)))
    normalized = div_round(med * ONE, max(1, ref_scale))
    if normalized == 0:
        return None
    return {"median": med, "referenceScale": ref_scale, "normalizedMilliAtMedian": normalized,
            "fittedCoeffMilli": div_round(TARGET_POINTS * ONE, normalized)}


def div_round(n: int, d: int) -> int:
    """PowerMath.DivRound — integer division, round half away from zero, same rule everywhere."""
    if d == 0:
        return 0
    sign = -1 if (n < 0) != (d < 0) else 1
    n, d = abs(n), abs(d)
    return sign * ((n + d // 2) // d)


def mul_milli(value: int, milli: int) -> int:
    """PowerMath.MulMilli."""
    return div_round(value * milli, ONE)


def price_total(mag: int, ref_scale: int, coeff_milli: int) -> int:
    """CostFunction.PriceForChannel's own arithmetic, conditionality neutral (=1000, no trigger)."""
    normalized_milli = div_round(mag * ONE, max(1, ref_scale))
    return mul_milli(normalized_milli, coeff_milli)


def load_corpus():
    atoms = []
    for f in sorted(glob.glob(CORPUS_GLOB)):
        doc = json.load(open(f, encoding="utf-8"))
        for e in doc["entries"]:
            p = e["params"]
            amt = p["amount"]
            mean_mag = div_round(amt["min"] + amt["max"], 2)
            atoms.append({
                "file": f, "family": e["family"], "tier": e["tier"],
                "kind": e["kind"], "channel": p["channel"], "op": p["op"], "mag": mean_mag,
            })
    return atoms


def fit(atoms):
    flat_by_channel = defaultdict(list)
    for a in atoms:
        if a["op"] == "flat":
            flat_by_channel[a["channel"]].append(a["mag"])

    fitted = {}
    detail = {}
    for ch, mags in flat_by_channel.items():
        med = statistics.median(sorted(mags))
        scale = AUTHORED_REFERENCE_SCALE[ch]
        normalized_at_median = div_round(int(med) * ONE, scale)
        coeff = div_round(TARGET_POINTS * ONE, normalized_at_median)
        fitted[ch] = int(coeff)
        detail[ch] = {
            "medianFlatMagnitude": med, "referenceScale": scale,
            "normalizedMilliAtMedian": normalized_at_median, "fittedCoeffMilli": int(coeff),
        }
    return fitted, detail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true", help="also print the coefficients.v1.json entries")
    args = ap.parse_args()

    atoms = load_corpus()
    print(f"Corpus: {len(atoms)} atom(s) from {CORPUS_GLOB}")
    channels = sorted({a["channel"] for a in atoms})
    print(f"Channels touched: {channels}")
    print()

    fitted, detail = fit(atoms)
    print("Fit method: CoeffMilli(channel) = round(1,000,000 / normalizedMilli(medianFlatMagnitude))")
    print("(ReferenceScale held at its existing authored value; only CoeffMilli moves.)\n")
    for ch in sorted(detail):
        d = detail[ch]
        print(f"  {ch:8s} medianFlat={d['medianFlatMagnitude']:<4} referenceScale={d['referenceScale']:<3} "
              f"-> fitted CoeffMilli={d['fittedCoeffMilli']}")
    print()

    fams = defaultdict(list)
    for a in atoms:
        fams[a["family"]].append(a)

    print(f"{'family':16s} {'chan':8s} {'op':10s} {'tier':4s} {'mag':5s} {'flat1000.price':14s} {'fitted.price':12s}")
    for fam, rows in sorted(fams.items()):
        rows.sort(key=lambda r: r["tier"])
        ch = rows[0]["channel"]
        scale = AUTHORED_REFERENCE_SCALE[ch]
        for r in rows:
            baseline = price_total(r["mag"], scale, 1000)
            fitted_price = price_total(r["mag"], scale, fitted[ch])
            print(f"{fam:16s} {ch:8s} {r['op']:10s} {r['tier']:<4d} {r['mag']:<5d} "
                  f"{baseline:<14d} {fitted_price:<12d}")

    print()
    print("== Finding 1: flat-op cross-channel parity at the median (tier 3) tier ==")
    for fam, rows in sorted(fams.items()):
        r = next(x for x in rows if x["tier"] == 3)
        if r["op"] != "flat":
            continue
        ch = r["channel"]
        scale = AUTHORED_REFERENCE_SCALE[ch]
        baseline = price_total(r["mag"], scale, 1000)
        fitted_price = price_total(r["mag"], scale, fitted[ch])
        print(f"  {fam:16s} ({ch:8s}) mag={r['mag']:<5d} flat-1000-price={baseline:<8d} fitted-price={fitted_price:<8d}")

    print()
    print("== Finding 2 (structural, NOT fixed here): op disparity at tier 1, same channel ==")
    print("   (ratio is scale-invariant under any CoeffMilli/ReferenceScale choice)")
    by_chan_op_t1 = defaultdict(dict)
    for a in atoms:
        if a["tier"] == 1:
            by_chan_op_t1[a["channel"]][a["op"]] = a["mag"]
    for ch, ops in sorted(by_chan_op_t1.items()):
        print(f"  {ch}: {ops}")

    # ---- the SHIPPED catalog, and why most of its rows are not fittable ------------------------
    print()
    print(f"== Shipped catalog ({SHIPPED_GLOB}) -- real content, per coefficient row ==")
    print("   (an earlier pass globbed only the generated tree and wrongly reported 'zero corpus')")
    shipped = load_shipped_corpus()
    authored = {(e["kindId"], e.get("channel", "")): e
                for e in json.load(open("data/seed/power/coefficients.v1.json", encoding="utf-8"))["entries"]}

    shipped_fits = {}
    for (kind, ch), rows in sorted(shipped.items()):
        mags = [r["mag"] for r in rows if r["mag"] is not None]
        row = authored.get((kind, ch))
        scale = row["referenceScale"] if row else 1
        label = f"{kind} ch={ch or '-'}"
        if kind in FIT_EXCLUDED_KINDS:
            print(f"  {label:42s} n={len(rows):2d}  NOT FITTED -- priced via ActorPowerCache.PriceBody, "
                  f"not this coefficient")
        elif not mags:
            print(f"  {label:42s} n={len(rows):2d}  NOT FITTABLE -- kind declares no ParamKind.Value "
                  f"param, so MeanMagnitude is a fixed 1 for every atom (policy choice, not a measurement)")
        elif len(set(mags)) == 1:
            print(f"  {label:42s} n={len(mags):2d}  single value {mags[0]} -- a normalisation of one "
                  f"point, not a fit; left authored")
        else:
            fit_detail = fit_one(mags, scale)
            shipped_fits[(kind, ch)] = fit_detail
            print(f"  {label:42s} n={len(mags):2d}  FITTABLE mags={sorted(mags)} median="
                  f"{fit_detail['median']} refScale={scale} -> fitted CoeffMilli="
                  f"{fit_detail['fittedCoeffMilli']} (was {row['coeffMilli'] if row else 'n/a'})")

    if args.json:
        print()
        print("== coefficients.v1.json entries (fitted rows only) ==")
        entries = []
        for ch in sorted(fitted):
            entries.append({
                "kindId": "stat.modify", "channel": ch,
                "coeffMilli": fitted[ch], "referenceScale": AUTHORED_REFERENCE_SCALE[ch],
            })
        for (kind, ch), d in sorted(shipped_fits.items()):
            entries.append({
                "kindId": kind, "channel": ch,
                "coeffMilli": d["fittedCoeffMilli"], "referenceScale": d["referenceScale"],
            })
        print(json.dumps(entries, indent=2))


if __name__ == "__main__":
    main()
