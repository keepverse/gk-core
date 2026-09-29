#!/usr/bin/env python3
"""
Minimal tuning publish path (tunables-ssot.md T4 + §7.1) — "the first domain builds the tool it
actually needs," not a general CLI up front. `contracts` is that first domain; §7.1 itself: "the
second domain generalises it if the shape holds" — `aptitudes` is that second domain, and its own
`edges` array (a list of `{channel, source, kMilli}` objects, not a nested dict) is a genuinely new
shape dict-only dotted paths cannot address, so this adds exactly one thing: a bracket selector on a
list segment.

T4: config is versioned and never hand-edited. This writes v{n+1}; v{n} stays on disk untouched, so
reverting a balance pass is restoring a file, not reading a diff.

Usage (repo root):
    python gk-core/tools/tuning/publish.py contracts loyalty.winGain=20
    python gk-core/tools/tuning/publish.py contracts personalityRates.loyal.gainPct=130 slots.maxSlots=64
    python gk-core/tools/tuning/publish.py contracts --label "spring balance pass" loyalty.winGain=20
    python gk-core/tools/tuning/publish.py aptitudes "edges[channel=resource.regen.hp,source=Vigor].kMilli=83"
    python gk-core/tools/tuning/publish.py aptitudes --add-edge "channel=resource.max.poise,source=Bulwark,kMilli=28000"
    python gk-core/tools/tuning/publish.py action-rungs --add-rung-power-budget 1000
    python gk-core/tools/tuning/publish.py battle-resources --add-regen-block "stamina=50,hunger=0,spirit=0,qi=0,poise=0"

`--add-edge` is the one path that ADDS rather than edits, and it exists because a coverage gap could
not be closed otherwise: `set` refuses to invent a key by design, and the file forbids hand-editing, so
a resource family that was never given a row had no legal way to gain one (resource-symmetry audit,
2026-09-02). It is deliberately narrow — it appends one `{channel, source, kMilli}` object to `edges`
and refuses anything that is not filling in a KNOWN family for a KNOWN source:

  * the channel's family must already appear on some existing edge — a new MEMBER of `resource.max.*`
    is allowed, a brand-new family is not (that is a schema change, not a balance one);
  * the source must already be a source somewhere in `edges`;
  * `(channel, source)` must not already exist — use the `set` path to change a value.

Each `key=value` is a dotted path into the JSON document (matching its own nesting — see
gk-core/data/tuning/contracts.v1.json). A SCALAR value is parsed as int, then float, then bool, then left as
a string, in that order — the first that round-trips exactly is used. A key whose existing value is
a JSON array or object instead takes valid JSON of that same kind, so a list-valued tunable round-
trips as a list:

    python gk-core/tools/tuning/publish.py set-topology 'resolutionOrder=["unique-species","family","general"]'

A scalar token against a structural key refuses by name rather than publishing the array's source
text as a string (T57).

A path segment may carry a bracket selector, `name[k1=v1,k2=v2]`, to reach one object inside a JSON
array named `name` — the object whose fields match every k=v pair exactly (string equality; ints in
the selector compare against int values). Refuses (does not guess) if zero or more than one array
element matches, same as a missing dict key refuses rather than defaulting.

Exit codes: 0 = published, 1 = no changes / bad input, 2 = usage error.
"""
import argparse, io, json, os, re, sys

TUNING_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "data", "tuning")


def parse_value(raw):
    for conv in (int, float):
        try:
            return conv(raw)
        except ValueError:
            pass
    if raw.lower() in ("true", "false"):
        return raw.lower() == "true"
    return raw


#: Kinds `parse_value` has no spelling for: its ladder ends at `str`, so handing it a JSON array or
#: object returns the source TEXT, not the value (T57). Keyed by exact type so `bool` (a subclass of
#: `int`) stays on the scalar path.
STRUCTURAL_KINDS = {list: "array", dict: "object"}


def parse_value_for(existing, raw, dotted_key):
    """Parse a `set` token against the SHAPE of the value already at the path (T57).

    `resolutionOrder=["unique-species","family","general"]` used to publish the STRING
    `'["unique-species","family","general"]'` into a new revision and report `1 change(s)` — the
    file T52 introduced, corrupted by the tool that is supposed to be the only legal writer of it.
    A structural key therefore demands valid JSON of its own kind and refuses by name otherwise;
    every scalar key keeps the historical int/float/bool/str parse, so no existing publish changes
    meaning."""
    kind = STRUCTURAL_KINDS.get(type(existing))
    if kind is None:
        return parse_value(raw)
    try:
        value = json.loads(raw)
    except ValueError as e:
        raise KeyError("'%s' holds a JSON %s, so its new value must be a JSON %s too (got %r: %s)"
                       % (dotted_key, kind, kind, raw, e))
    if not isinstance(value, type(existing)):
        raise KeyError("'%s' holds a JSON %s, so its new value must be %s (got %r, a JSON %s)"
                       % (dotted_key, kind, kind, raw, type(value).__name__))
    return value


def latest_version(domain):
    pat = re.compile(r"^%s\.v(\d+)\.json$" % re.escape(domain))
    versions = []
    for fn in os.listdir(TUNING_DIR):
        m = pat.match(fn)
        if m:
            versions.append(int(m.group(1)))
    if not versions:
        print("no existing %s.v*.json in %s" % (domain, TUNING_DIR), file=sys.stderr)
        return None
    return max(versions)


SELECTOR_RE = re.compile(r"^([^\[\]]+)\[([^\[\]]+)\]$")


def split_kv(arg):
    # A bracket selector's own clauses ("channel=resource.regen.hp") contain "=" too, so the naive
    # first-"=" split used for the selector's OWN clauses (there, correct: one "=" per clause, no
    # brackets to worry about) is wrong at the top level, where that "=" is INSIDE a [...] and not
    # the key/value separator. Split on the first "=" that is outside any bracket depth instead.
    depth = 0
    for i, ch in enumerate(arg):
        if ch == "[":
            depth += 1
        elif ch == "]":
            depth -= 1
        elif ch == "=" and depth == 0:
            return arg[:i], arg[i + 1:]
    return None


def split_path(dotted_key):
    # Plain str.split(".") would also split the dots INSIDE a bracket selector's own value (e.g.
    # "edges[channel=resource.regen.hp,source=Vigor].kMilli" -- "resource.regen.hp" is itself
    # dotted). Only split on a "." that is outside any [...].
    parts, depth, current = [], 0, ""
    for ch in dotted_key:
        if ch == "[":
            depth += 1
            current += ch
        elif ch == "]":
            depth -= 1
            current += ch
        elif ch == "." and depth == 0:
            parts.append(current)
            current = ""
        else:
            current += ch
    parts.append(current)
    return parts


def _parse_selector(raw):
    # "k1=v1,k2=v2" -> {"k1": v1, "k2": v2}, values parsed the same way CLI values are (int first).
    pairs = {}
    for kv in raw.split(","):
        if "=" not in kv:
            raise KeyError("selector clause '%s' is not key=value" % kv)
        k, v = kv.split("=", 1)
        pairs[k] = parse_value(v)
    return pairs


def _step(node, dotted_key, seg):
    """Advance one dotted-path segment (plain key, or name[k=v,...] into a list) and return
    (child, description) — description names what was matched, for error messages."""
    m = SELECTOR_RE.match(seg)
    if m is None:
        if not isinstance(node, dict) or seg not in node:
            raise KeyError("'%s' has no '%s' — refusing to invent a new key (T5 spirit: "
                            "publish edits existing tunables, it does not add undocumented ones)"
                            % (dotted_key, seg))
        return node[seg], seg

    list_key, selector_raw = m.group(1), m.group(2)
    if not isinstance(node, dict) or list_key not in node:
        raise KeyError("'%s' has no '%s' — refusing to invent a new key" % (dotted_key, list_key))
    lst = node[list_key]
    if not isinstance(lst, list):
        raise KeyError("'%s' is not an array — a [selector] only applies to one" % list_key)
    selector = _parse_selector(selector_raw)
    matches = [el for el in lst if isinstance(el, dict) and all(el.get(k) == v for k, v in selector.items())]
    if len(matches) == 0:
        raise KeyError("'%s[%s]' matches no element of '%s' — refusing to guess"
                        % (list_key, selector_raw, dotted_key))
    if len(matches) > 1:
        raise KeyError("'%s[%s]' matches %d elements of '%s' — selector must be unique"
                        % (list_key, selector_raw, len(matches), dotted_key))
    return matches[0], "%s[%s]" % (list_key, selector_raw)


def value_at(doc, dotted_key):
    """Read the CURRENT value at a dotted path, refusing a missing one exactly as `set_path` does.

    Split out for T57: the `set` path has to see the old value's SHAPE before it can parse the new
    token, because a list/dict key has no scalar spelling."""
    parts = split_path(dotted_key)
    node = doc
    for p in parts[:-1]:
        node, _ = _step(node, dotted_key, p)
    last = parts[-1]
    if SELECTOR_RE.match(last) is not None:
        raise KeyError("'%s' ends in a [selector] with no field after it — nothing to set" % dotted_key)
    if not isinstance(node, dict) or last not in node:
        raise KeyError("'%s' does not exist in the current document" % dotted_key)
    return node[last]


def set_path(doc, dotted_key, value):
    old = value_at(doc, dotted_key)
    parts = split_path(dotted_key)
    node = doc
    for p in parts[:-1]:
        node, _ = _step(node, dotted_key, p)
    node[parts[-1]] = value
    return old



def add_edge(doc, spec_raw):
    """Append one {channel, source, kMilli} edge. Refuses rather than guesses, matching set_path."""
    spec = _parse_selector(spec_raw)
    required = {"channel", "source", "kMilli"}
    if set(spec) != required:
        raise KeyError("--add-edge needs exactly channel=, source= and kMilli= (got: %s)"
                       % ", ".join(sorted(spec)) or "nothing")
    if not isinstance(spec["kMilli"], int):
        raise KeyError("kMilli must be an integer (got %r)" % (spec["kMilli"],))

    edges = doc.get("edges")
    if not isinstance(edges, list):
        raise KeyError("'edges' is not an array in this document — --add-edge is aptitudes-shaped")

    real = [e for e in edges if isinstance(e, dict) and "channel" in e]
    channel, source = spec["channel"], spec["source"]

    if any(e.get("channel") == channel and e.get("source") == source for e in real):
        raise KeyError("edge (channel=%s, source=%s) already exists — use the set path to change its "
                       "value, this flag only fills gaps" % (channel, source))

    known_sources = {e.get("source") for e in real}
    if source not in known_sources:
        raise KeyError("'%s' is not a source anywhere in edges — refusing to invent one" % source)

    family = channel.rsplit(".", 1)[0]
    known_families = {e["channel"].rsplit(".", 1)[0] for e in real}
    if family not in known_families:
        raise KeyError("'%s' is a NEW channel family, not a new member of an existing one — that is a "
                       "schema change, not a balance one; refusing" % family)

    edges.append({"channel": channel, "source": source, "kMilli": spec["kMilli"]})
    return channel, source, spec["kMilli"]



def remove_edge(doc, spec_raw):
    """Remove exactly one {channel, source, ...} edge. Refuses if it matches ZERO or MORE THAN ONE.

    The removal twin of --add-edge (solid-enforcement `retire-atk` R3, 2026-09-18): a channel the
    design has retired must be removable from the LIVE file, not just dropped at load, so the latest
    version says what is true rather than carrying dead edges a balance pass would try to tune.
    Published versions stay immutable, so this publishes v{n+1} exactly like every other op.

    `kMilli` is deliberately NOT required: the point of a removal is that the edge should not exist,
    and demanding its current value would make the command fail the moment someone retuned it.
    Refusing on an ambiguous match is the whole safety property -- "remove every Might edge" is a
    different, far larger operation than "remove this one"."""
    spec = _parse_selector(spec_raw)
    required = {"channel", "source"}
    if not required.issubset(spec):
        raise KeyError("--remove-edge needs at least channel= and source= (got: %s)"
                       % (", ".join(sorted(spec)) or "nothing"))
    unknown = set(spec) - required - {"kMilli"}
    if unknown:
        raise KeyError("--remove-edge does not take %s" % ", ".join(sorted(unknown)))

    edges = doc.get("edges")
    if not isinstance(edges, list):
        raise KeyError("'edges' is not an array in this document — --remove-edge is aptitudes-shaped")

    def matches(e):
        return isinstance(e, dict) and all(e.get(k) == v for k, v in spec.items())

    hits = [i for i, e in enumerate(edges) if matches(e)]
    if len(hits) == 0:
        raise KeyError("no edge matches %s — refusing to remove nothing"
                       % ", ".join("%s=%r" % (k, v) for k, v in sorted(spec.items())))
    if len(hits) > 1:
        raise KeyError("%d edges match %s — a removal must name exactly one"
                       % (len(hits), ", ".join("%s=%r" % (k, v) for k, v in sorted(spec.items()))))

    removed = edges.pop(hits[0])
    return removed.get("channel"), removed.get("source")



SELECTOR_ONLY_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\[(.+)\]$")


def remove_entry(doc, spec_raw):
    """Remove exactly one ARRAY element, addressed as `<array>[k=v,...]`, e.g.
    `entries[family=progression.bonus.atk]`. Refuses if it matches ZERO or MORE THAN ONE.

    The removal twin --remove-edge needs for a domain whose rows are not called `edges`
    (solid-enforcement `retire-atk` R4, 2026-09-18): `data/tuning/derived-stat-catalog.v{n}.json`
    carries its families in `entries`, and the retired channel's row must leave the LIVE file, not
    be hand-deleted from it. Same safety property as --remove-edge: a removal names exactly one
    row, and a sweep is a different, larger operation that this flag will not do."""
    m = SELECTOR_ONLY_RE.match(spec_raw.strip())
    if m is None:
        raise KeyError("--remove-entry needs <array>[k=v,...] (got %r)" % spec_raw)
    list_key, selector_raw = m.group(1), m.group(2)
    if list_key not in doc:
        raise KeyError("'%s' is not in this document — refusing to invent it" % list_key)
    lst = doc[list_key]
    if not isinstance(lst, list):
        raise KeyError("'%s' is not an array — --remove-entry removes one array element" % list_key)
    selector = _parse_selector(selector_raw)

    def matches(e):
        return isinstance(e, dict) and all(e.get(k) == v for k, v in selector.items())

    hits = [i for i, e in enumerate(lst) if matches(e)]
    if len(hits) == 0:
        raise KeyError("no element of '%s' matches %s — refusing to remove nothing"
                       % (list_key, ", ".join("%s=%r" % (k, v) for k, v in sorted(selector.items()))))
    if len(hits) > 1:
        raise KeyError("%d elements of '%s' match %s — a removal must name exactly one"
                       % (len(hits), list_key, ", ".join("%s=%r" % (k, v) for k, v in sorted(selector.items()))))

    removed = lst.pop(hits[0])
    return "%s[%s]" % (list_key, selector_raw), removed


def remove_key(doc, dotted_key):
    """Remove one dict key, given as `container.path:leaf` (or `:leaf` at the root). Refuses if it is
    absent.

    The removal twin of --add-key. The COLON matters for the same reason --rename-key's does: a tuning
    key is very often itself dotted (`familyRead."progression.bonus.atk"`), so a plain dotted path
    cannot say where the container ends and the leaf begins. Everything after the colon is one literal
    key name, dots included."""
    if ":" not in dotted_key:
        raise KeyError("--remove-key needs container.path:leaf (got %r)" % dotted_key)
    container_path, leaf = dotted_key.split(":", 1)
    if not leaf:
        raise KeyError("--remove-key needs a non-empty leaf name (got %r)" % dotted_key)
    node = doc
    if container_path:
        for seg in split_path(container_path):
            node, _ = _step(node, container_path, seg)
    if not isinstance(node, dict):
        raise KeyError("'%s' is not an object -- --remove-key removes a key from an object"
                       % (container_path or "$"))
    if leaf not in node:
        raise KeyError("'%s' has no key '%s' — refusing to remove a key that is not there"
                       % (container_path or "$", leaf))
    removed = node.pop(leaf)
    return (container_path + "." if container_path else "") + leaf, removed



def add_rung_power_budget(doc, reference_power):
    """A-G1 (spec-tier-access-gate.md SS3.1): add `powerBudgetMilli` to every row of `rows`, derived
    from the row's OWN already-shipped columns rather than a new curve --

        powerBudgetMilli(r) = poolRolls(r) * referencePower * qPowerMilli(r) / 1000

    long arithmetic (Python ints are unbounded, so this never wraps the way the C# reader must guard
    against), widened before multiplying, divided by 1000 last and exactly once. Refuses rather than
    guesses, matching --add-edge: any row already carrying the column is refused (use `set` to change
    a value), and a row missing `poolRolls`/`qPowerMilli` is refused by name. Also records the
    derivation and the scalar's untuned status in `_meta`, the same direct-write `--label` already
    uses for `_meta.rebalanceLabel` -- `set_path` cannot fill either because both are new keys.
    """
    rows = doc.get("rows")
    if not isinstance(rows, list) or not rows:
        raise KeyError("'rows' is not a non-empty array -- --add-rung-power-budget is action-rungs-shaped")

    if any(isinstance(r, dict) and "powerBudgetMilli" in r for r in rows):
        raise KeyError("some row already has 'powerBudgetMilli' -- use the set path to change a "
                       "value, this flag only fills a first-time gap")

    added = []
    for r in rows:
        if not isinstance(r, dict) or "poolRolls" not in r or "qPowerMilli" not in r:
            raise KeyError("a row is missing 'poolRolls' or 'qPowerMilli' -- cannot derive its power budget")
        pool_rolls, q_power_milli = r["poolRolls"], r["qPowerMilli"]
        if not isinstance(pool_rolls, int) or not isinstance(q_power_milli, int):
            raise KeyError("'poolRolls'/'qPowerMilli' must be integers")

        budget = pool_rolls * reference_power * q_power_milli // 1000
        r["powerBudgetMilli"] = budget
        added.append((r.get("rung"), budget))

    meta = doc.setdefault("_meta", {})
    meta["referencePower"] = reference_power
    meta["referencePowerUntuned"] = True
    meta["powerBudgetDerivation"] = (
        "powerBudgetMilli(r) = poolRolls(r) * referencePower * qPowerMilli(r) / 1000 -- long "
        "arithmetic, widened before multiplying, divided by 1000 last and exactly once (A-G1, "
        "spec-tier-access-gate.md SS3.1). referencePower = PowerMath.One (PowerVector.cs:135, inside "
        "the PowerMath class -- one reference action is worth one unit of power. At "
        "referencePower=1000 the /1000 cancels the *1000 exactly, so the budget IS qPowerMilli's own "
        "curve, unscaled -- rung 1 lands on exactly 1000 for this reason, never by coincidence. "
        "referencePower is untuned: what it tunes against is the smoke batch's accepted-container "
        "cost distribution (a later module's output), not yet produced. It is a single scalar that "
        "moves the whole ladder together and can never introduce a second curve shape."
    )

    return added


def add_regen_block(doc, spec_raw):
    """lawn-combat-wire T11 (spec-lawn-combat-calibration.md): add `regenPerSecondShareMilli` --
    battle-resources.v1.json's own `_meta.regenIsAbsentOnPurpose` names the reason there is no regen
    block at all: `ResourceChannelReader.RegenPerTick` used to round the channel to a whole `long`,
    so the smallest expressible non-zero rate accrued ~300 poise per round against a spend of 100 --
    three counters a round, erasing the scarcity poise exists to create. `resource-subtick` (S10.1,
    2026-09-13) fixed the unit (per-mille-per-tick, carried rather than rounded), which is what makes
    this flag possible; it deliberately authored no rate itself.

    Expressed as a per-mille SHARE OF THE POOL, mirroring `poolShareMilli`'s own convention, rather
    than a flat absolute rate -- a flat number would drift out of proportion at another theta, while
    a share projects through BaseHp(theta) the same way the pool max already does (one curve, not a
    second one). Refuses rather than guesses, matching the sibling --add-* flags: the key must not
    already exist, and the ids given must be EXACTLY `poolShareMilli`'s own id set -- no invented
    resource, no silently-missing one, the same closed-coverage rule poolShareMilli itself enforces
    at load (BattleResourceTuningLoader.Parse).

    Usage: --add-regen-block "stamina=50,hunger=0,spirit=0,qi=0,poise=0"
    """
    if "regenPerSecondShareMilli" in doc:
        raise KeyError("'regenPerSecondShareMilli' already exists -- use the set path to change a "
                       "value, this flag only fills a first-time gap")

    shares = doc.get("poolShareMilli")
    if not isinstance(shares, dict) or not shares:
        raise KeyError("'poolShareMilli' is missing or empty -- --add-regen-block is "
                       "battle-resources-shaped only, and reads its id set to check coverage against")

    given = _parse_selector(spec_raw)
    required_ids, given_ids = set(shares.keys()), set(given.keys())
    if given_ids != required_ids:
        missing = required_ids - given_ids
        extra = given_ids - required_ids
        detail = []
        if missing:
            detail.append("missing: %s" % ", ".join(sorted(missing)))
        if extra:
            detail.append("unknown: %s" % ", ".join(sorted(extra)))
        raise KeyError("--add-regen-block must cover exactly poolShareMilli's own ids (%s)"
                       % "; ".join(detail))

    for rid, v in given.items():
        if isinstance(v, bool) or not isinstance(v, int) or v < 0:
            raise KeyError("regen share for '%s' must be a non-negative integer permille (got %r)"
                           % (rid, v))

    # Ordered to match poolShareMilli's own key order -- readability, not semantics (JSON objects are
    # unordered), so a diff reads id-for-id against the pool shares it derives from.
    doc["regenPerSecondShareMilli"] = {rid: given[rid] for rid in shares.keys()}

    meta = doc.setdefault("_meta", {})
    meta["regenIsAbsentOnPurpose"] = (
        "SUPERSEDED 2026-09-14 (lawn-combat-wire T11, spec-lawn-combat-calibration.md). This used to "
        "say there was no regen block at all -- ResourceChannelReader.RegenPerTick rounded the channel "
        "to a whole long, so the smallest expressible non-zero rate accrued ~300 poise per round "
        "against a spend of 100 (three counters a round, erasing the scarcity poise exists to "
        "create), and there was nothing representable between that and zero. resource-subtick (S10.1, "
        "2026-09-13) resolved the REPRESENTATION problem: the reader now carries a per-mille-per-tick "
        "remainder instead of rounding, so 999 rates exist between nothing and a whole unit. This file "
        "now authors exactly ONE non-zero regen row -- stamina, for the lawn/battle basic attack -- and "
        "leaves the other four at 0 deliberately, not by omission: poise's original scarcity argument "
        "still holds verbatim (nothing changed its spend economics, resource-hub-ssot.md SS11 pools "
        "'refill AT REST' and a battle/lawn encounter is not a rest), and hunger/spirit/qi have no cost "
        "mechanism spending them yet, so authoring a rate for them would be inventing a balance number "
        "nothing consumes. See _meta.regenDerivation for the stamina arithmetic."
    )
    meta["regenDerivation"] = (
        "UNMEASURED, derived at the pin (theta=20, power-scale.v2.json curve.pinValue=680): "
        "poolMax(20,'stamina') = BaseHp(20) * poolShareMilli.stamina / 1000 = 680 * 500 / 1000 = 340. "
        "regenPerSecondShareMilli.stamina=50 (5 percent of the pool per second) => "
        "regenPerSecond(20) = poolMax * 50 / 1000 = 17. A Peashooter's shipped attack interval is "
        "1.5s (measured live 2026-09-13), so the sustainable spend at the pin is "
        "17 * 1.5 = 25.5 -- action-corpus-cost-templates kinds.basic.baseAmountAtRung1 is authored "
        "just under that ceiling (spec-lawn-combat-calibration.md Method #2), so continuous "
        "single-target fire is sustainable with a thin margin and a genuine burst can still tip it "
        "into deficit. basic-attack-cost (T12) is the wiring that actually spends it; "
        "basic-attack-live-proof 4 is where exhaustion-then-recovery gets proven live."
    )

    return doc["regenPerSecondShareMilli"]


def add_inherit_cost_table(doc):
    """creature-standalone WAVE F2.3 (2026-09-07): add `inheritCostByRarity` — a fusion pick's cost is
    read from the PICK'S OWN source rarity, not the fusion output's, so it needs its own table rather
    than reusing `recipeCost` (which is keyed by output rarity). Reuses `recipeCost`'s own `souls`
    escalation verbatim (the todo's own "same 150->1000-souls shape... already uses" instruction) --
    a flat `{rarity: souls}` map, not the full compound recipeCost shape, since F2.4 only ever sums
    souls for a pick set. Refuses rather than guesses: `inheritCostByRarity` already existing is a
    first-time-gap violation (use `set` to change a value), and a missing/malformed `recipeCost` means
    there is nothing to derive from.
    """
    if "inheritCostByRarity" in doc:
        raise KeyError("'inheritCostByRarity' already exists -- use the set path to change a value, "
                        "this flag only fills a first-time gap")

    recipe_cost = doc.get("recipeCost")
    if not isinstance(recipe_cost, dict) or not recipe_cost:
        raise KeyError("'recipeCost' is missing or empty -- cannot derive inheritCostByRarity from it")

    table = {}
    for rarity, row in recipe_cost.items():
        if not isinstance(row, dict) or "souls" not in row or not isinstance(row["souls"], int):
            raise KeyError("recipeCost['%s'] has no integer 'souls' -- cannot derive from it" % rarity)
        table[rarity] = row["souls"]

    doc["inheritCostByRarity"] = table

    meta = doc.setdefault("_meta", {})
    meta["inheritCostByRarityDerivation"] = (
        "WAVE F2.3 (creature-standalone, 2026-09-07): a flat {rarity: souls} map, one entry per "
        "recipeCost rung, copied verbatim from recipeCost[rarity].souls at the time this table was "
        "added. Looked up by the INHERITED PICK's own source species' rarity (F2.2/F2.4), never the "
        "fusion output's own rarity -- a different lookup key from recipeCost, hence its own table "
        "rather than a second read of recipeCost. Starting values, not a validated balance decision, "
        "same as recipeCost's own note when it was widened."
    )

    return table


def add_actor_hud_screen_layout(doc):
    """Add the six first-class screen-layout tunables to actor-hud exactly once.

    These are presentation measurements (pixels), not a world-coordinate correction.  The command
    is intentionally actor-HUD-shaped: it refuses another domain, refuses a partial existing schema,
    and carries the migration note forward so a later rebalance uses ordinary dotted `set` edits.
    """
    required = {
        "screenGapPixels": 8,
        "screenWidthFactor": 0.8,
        "screenMinWidthPixels": 32,
        "screenMaxWidthPixels": 96,
        "screenRowGapPixels": 3,
        "screenResourceHeightPixels": 7,
    }
    present = set(required).intersection(doc)
    if present:
        raise KeyError("actor-hud screen layout is partially/already present (%s) -- use dotted set edits"
                       % ", ".join(sorted(present)))
    if doc.get("anchorKind") != "body":
        raise KeyError("actor-hud base document must retain its legacy body anchor metadata for a safe v3 migration")

    doc.update(required)
    meta = doc.setdefault("_meta", {})
    meta["note"] = (
        "Band B per-unit HUD — screen-space visual silhouette bottom-center plus pixel gap. "
        "The actor sprite union chooses the anchor; rows grow downward only. Legacy world fields stay "
        "only for backward parsing and are not consumed by the screen presenter."
    )
    return required


def add_actor_hud_element_layout(doc):
    """Add the first-class screen-space element-row geometry to actor-hud once."""
    if doc.get("anchorKind") != "body":
        raise KeyError("actor-hud base document must retain the body anchor contract")
    required = {
        "screenElementIconPixels": 16,
        "screenElementGapPixels": 3,
        "screenElementRowGapPixels": 3,
    }
    present = set(required).intersection(doc)
    if present:
        raise KeyError("actor-hud element layout is partially/already present (%s) -- use dotted set edits"
                       % ", ".join(sorted(present)))
    doc.update(required)
    return required


def upgrade_actor_hud_identity_element_layout(doc):
    """Replace v5's detached row geometry with the v6 identity-line schema exactly once."""
    legacy = {"screenElementIconPixels", "screenElementGapPixels", "screenElementRowGapPixels"}
    if not legacy.issubset(doc):
        raise KeyError("actor-hud v6 identity layout requires the complete v5 detached-row schema")
    new = {
        "screenIdentityElementPrimaryPixels": 24,
        "screenIdentityElementSecondaryPixels": 20,
        "screenIdentityElementGapPixels": doc["screenElementGapPixels"],
    }
    if set(new).intersection(doc):
        raise KeyError("actor-hud v6 identity layout is partially/already present")
    for key in legacy:
        del doc[key]
    doc.update(new)
    return new


def add_element_hud_glyph(doc, spec_raw):
    """Add one catalog-owned HUD glyph declaration to an existing element row."""
    if doc.get("kind") != "element-catalog":
        raise KeyError("--add-element-hud-glyph applies only to element-catalog")
    spec = _parse_selector(spec_raw)
    if set(spec) != {"id", "glyph"}:
        raise KeyError("--add-element-hud-glyph needs exactly id= and glyph=")
    if not isinstance(spec["id"], str) or not isinstance(spec["glyph"], str) or not spec["glyph"].strip():
        raise KeyError("element HUD glyph id and glyph must be non-empty strings")
    entries = doc.get("entries")
    if not isinstance(entries, list):
        raise KeyError("element-catalog entries is not an array")
    matches = [e for e in entries if isinstance(e, dict) and e.get("id") == spec["id"]]
    if len(matches) != 1:
        raise KeyError("element-catalog id '%s' must match exactly one entry" % spec["id"])
    entry = matches[0]
    if "hudGlyph" in entry:
        raise KeyError("element-catalog id '%s' already has hudGlyph -- use dotted set edits" % spec["id"])
    entry["hudGlyph"] = spec["glyph"].strip()
    return spec["id"], entry["hudGlyph"]


def add_vfx_impact_stamp(doc):
    """Add the Earth-pilot stamp tuning block exactly once.

    This is deliberately a domain-shaped publisher operation rather than a free-form JSON writer:
    the block is a new VFX schema contract, every presentation value has an explicit unit in
    ``_meta``, and a second invocation must use regular dotted tuning edits instead of replacing it.
    """
    if doc.get("version") != 3 or "impactStamp" in doc:
        raise KeyError("--add-vfx-impact-stamp requires vfx.v3 without an existing impactStamp block")

    values = {
        "poolCap": 12,
        "lifeSeconds": 0.55,
        "spanScale": 1.0,
        "startAlpha": 0.9,
        "endAlpha": 0.0,
        "startScale": 0.9,
        "endScale": 1.1,
        "sortOffset": 2,
    }
    units = {
        "poolCap": "instances",
        "lifeSeconds": "seconds-unscaled",
        "spanScale": "unit-frame-spans",
        "startAlpha": "ratio-0-to-1",
        "endAlpha": "ratio-0-to-1",
        "startScale": "multiplier",
        "endScale": "multiplier",
        "sortOffset": "sorting-order-delta",
    }
    doc["impactStamp"] = values
    doc.setdefault("_meta", {})["impactStampUnits"] = units
    return values


def add_vfx_earth_phase(doc):
    """Add the Earth charge/travel block exactly once through the VFX publisher."""
    if doc.get("version") != 4 or "earthPhase" in doc:
        raise KeyError("--add-vfx-earth-phase requires vfx.v4 without an existing earthPhase block")

    values = {
        "poolCap": 12,
        "chargeLifeSeconds": 0.18,
        "travelLifeSeconds": 0.32,
        "chargeSpanScale": 0.55,
        "travelLengthScale": 1.0,
        "travelThicknessScale": 0.2,
        "startAlpha": 1.0,
        "endAlpha": 0.0,
        "sortOffset": 2,
    }
    units = {
        "poolCap": "instances",
        "chargeLifeSeconds": "seconds-unscaled",
        "travelLifeSeconds": "seconds-unscaled",
        "chargeSpanScale": "unit-frame-spans",
        "travelLengthScale": "source-target-distance-multiplier",
        "travelThicknessScale": "unit-frame-spans",
        "startAlpha": "ratio-0-to-1",
        "endAlpha": "ratio-0-to-1",
        "sortOffset": "sorting-order-delta",
    }
    doc["earthPhase"] = values
    doc.setdefault("_meta", {})["earthPhaseUnits"] = units
    return values
def reprice_rung_power_budget(doc, reference_power, mark_tuned=False):
    """ST4.4 (spec-budget-calibration-report.md contract 5): recompute every row's
    `powerBudgetMilli` from its OWN already-shipped `poolRolls`/`qPowerMilli` and a new scalar --
    exactly the arithmetic `add_rung_power_budget` derives with, so retuning is one publish call
    instead of ten hand-set cells that would break the one-scalar derivation `_meta` promises.
    `_meta.referencePower` becomes REF.

    Refuses rather than guesses, matching every sibling `--add-*`/`--reprice-*` flag: a row missing
    the column is refused (this reprices a budget, it never introduces one -- that is
    `--add-rung-power-budget`'s job), and so is a non-positive REF.

    `_meta.referencePowerUntuned` flips to `false` ONLY when `mark_tuned` is true. Both flags exist
    because "we repriced to a value the report recommended" and "the owner has signed this scalar
    off as the tuned one" are different statements, and the second must never be made by accident
    (ruling 3: a neutral scalar should be visible as neutral).
    """
    rows = doc.get("rows")
    if not isinstance(rows, list) or not rows:
        raise KeyError("'rows' is not a non-empty array -- --reprice-rung-power-budget is "
                       "action-rungs-shaped")
    if isinstance(reference_power, bool) or not isinstance(reference_power, int) or reference_power <= 0:
        raise KeyError("REF must be a positive integer (got %r)" % (reference_power,))

    missing = [r.get("rung") for r in rows if not isinstance(r, dict) or "powerBudgetMilli" not in r]
    if missing:
        raise KeyError("row(s) %s carry no 'powerBudgetMilli' -- this reprices an existing budget; "
                       "derive the column first with --add-rung-power-budget" % missing)

    repriced = []
    for r in rows:
        if "poolRolls" not in r or "qPowerMilli" not in r:
            raise KeyError("row rung=%s is missing 'poolRolls' or 'qPowerMilli' -- cannot reprice it"
                           % r.get("rung"))
        budget = r["poolRolls"] * reference_power * r["qPowerMilli"] // 1000
        old = r["powerBudgetMilli"]
        r["powerBudgetMilli"] = budget
        repriced.append((r.get("rung"), old, budget))

    meta = doc.setdefault("_meta", {})
    meta["referencePower"] = reference_power
    meta["referencePowerUntuned"] = not mark_tuned
    return repriced


def add_key(doc, spec_raw):
    """`--add-key container.path:leaf=<json>` -- ADD one new key holding a JSON value, and refuse if
    it already exists. `set` refuses to invent keys by design, so a domain gaining a whole new block
    (deployment-hierarchy's `cacheDecay`/`cacheRetrieval`, 2026-09-18: constants that had been parked
    in code until this domain's file existed) had no legal path onto the balance surface. An empty
    container (`:leaf=...`) means the document root. The container must already exist: this adds a
    key, it never creates a path."""
    if "=" not in spec_raw or ":" not in spec_raw.split("=", 1)[0]:
        raise KeyError("--add-key needs container.path:leaf=<json> (got %r)" % spec_raw)
    lhs, raw_value = spec_raw.split("=", 1)
    container_path, leaf = lhs.split(":", 1)
    if not leaf:
        raise KeyError("--add-key needs a non-empty leaf name (got %r)" % spec_raw)
    try:
        value = json.loads(raw_value)
    except ValueError as e:
        raise KeyError("--add-key value is not valid JSON: %s" % e)
    node = doc
    if container_path:
        for seg in split_path(container_path):
            node, _ = _step(node, container_path, seg)
    if not isinstance(node, dict):
        raise KeyError("'%s' is not an object -- --add-key adds a key to an object" % (container_path or "$"))
    if leaf in node:
        raise KeyError("'%s' already has '%s' -- use the set path to change a value" % (container_path or "$", leaf))
    node[leaf] = value
    return (container_path + "." if container_path else "") + leaf, value


def add_notification_category(doc, spec_raw):
    """notify-vocabulary / world-notify-source (T4) -- append one category row to `categories`.
    Refuses (mirroring `NotificationCatalogLoader.Parse`'s own rejections, so a bad publish is
    caught here, not at load): a `channel` or `severity` field (R-N2/R-N4 -- only `promotions` may
    set those), a missing id/domain/displayName/messageKeys, an unknown field, or a duplicate id.

    `messageKeys` is `|`-joined (a category id or message key never contains `|`, while `,` is
    already reserved for this tool's own selector syntax every other --add-* flag uses).

    Usage: --add-category "id=loam.shortfall,domain=world,displayName=Loam shortfall,messageKeys=world.turn-entry"
    """
    spec = _parse_selector(spec_raw)
    if "channel" in spec or "severity" in spec:
        raise KeyError("a category row may not carry 'channel' or 'severity' (R-N2/R-N4 -- only "
                       "promotions may set those)")
    required = {"id", "domain", "displayName", "messageKeys"}
    missing = required - set(spec)
    if missing:
        raise KeyError("--add-category needs %s (missing: %s)"
                       % (", ".join(sorted(required)), ", ".join(sorted(missing))))
    extra = set(spec) - required
    if extra:
        raise KeyError("--add-category has unknown field(s): %s" % ", ".join(sorted(extra)))

    categories = doc.setdefault("categories", [])
    if not isinstance(categories, list):
        raise KeyError("'categories' is not an array in this document -- --add-category is "
                       "notification-catalog-shaped only")

    cat_id = spec["id"]
    if any(isinstance(c, dict) and c.get("id") == cat_id for c in categories):
        raise KeyError("category '%s' already exists -- publish refuses to add a duplicate" % cat_id)

    keys_raw = spec["messageKeys"]
    message_keys = [k for k in str(keys_raw).split("|") if k]
    if not message_keys:
        raise KeyError("'messageKeys' must name at least one '|'-joined key")

    row = {"id": cat_id, "domain": spec["domain"], "displayName": spec["displayName"], "messageKeys": message_keys}
    categories.append(row)
    return row


def set_promotion_toast(doc, ids_csv):
    """Replace `promotions.toast` wholesale with a comma-separated id list. Refuses an id not
    registered in `categories` (mirroring the loader's own rejection) -- category rows must be
    added first, in the same or an earlier publish call."""
    return _set_promotion_list(doc, "toast", ids_csv)


def set_promotion_critical(doc, ids_csv):
    """Replace `promotions.critical` wholesale. Refuses an id not registered in `categories`, and
    (mirroring the loader) any id not also in the CURRENT `promotions.toast` -- critical is a
    subset of toast. Apply `--promote-toast` first if both change in the same call."""
    old, new = _set_promotion_list(doc, "critical", ids_csv)
    toast = set(doc.get("promotions", {}).get("toast", []))
    not_toast = [i for i in new if i not in toast]
    if not_toast:
        raise KeyError("promotions.critical must be a subset of promotions.toast -- not in toast: %s"
                       % ", ".join(not_toast))
    return old, new


def _set_promotion_list(doc, field, ids_csv):
    categories = doc.get("categories")
    known_ids = {c["id"] for c in categories if isinstance(c, dict) and "id" in c} if isinstance(categories, list) else set()
    ids = [i for i in ids_csv.split(",") if i]
    unknown = [i for i in ids if i not in known_ids]
    if unknown:
        raise KeyError("promotions.%s names unregistered id(s): %s" % (field, ", ".join(unknown)))
    promotions = doc.setdefault("promotions", {})
    old = promotions.get(field)
    promotions[field] = ids
    return old, ids


def rename_key(doc, spec_raw):
    """Rename one dict key in place, preserving insertion order. Refuses rather than guesses."""
    # `container.path:oldLeaf=newLeaf`. The COLON matters: a tuning key is very often itself dotted
    # (`familyRead."combat.heal.power"`), so a plain dotted path cannot say where the container ends
    # and the leaf begins. Everything after the colon is one literal key name, dots included.
    if "=" not in spec_raw or ":" not in spec_raw.split("=", 1)[0]:
        raise KeyError("--rename-key needs container.path:oldLeaf=newLeaf (got %r)" % spec_raw)
    lhs, new_leaf = spec_raw.split("=", 1)
    container_path, last = lhs.split(":", 1)
    node = doc
    if container_path:
        for seg in split_path(container_path):
            node, _ = _step(node, container_path, seg)
    if not isinstance(node, dict) or last not in node:
        raise KeyError("'%s' has no key '%s' — refusing to rename a key that is not there" % (container_path, last))
    if new_leaf in node:
        raise KeyError("'%s' already exists alongside '%s' — refusing to overwrite it" % (new_leaf, last))
    rebuilt = {}
    for k, v in node.items():
        rebuilt[new_leaf if k == last else k] = v
    node.clear()
    node.update(rebuilt)
    return last, new_leaf


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("domain")
    ap.add_argument("sets", nargs="*", metavar="key=value")
    ap.add_argument("--add-edge", action="append", default=[], dest="add_edges",
                    metavar="channel=..,source=..,kMilli=..",
                    help="append one edge to `edges` (fills a coverage gap; refuses duplicates and unknown families)")
    ap.add_argument("--rename-key", action="append", default=[], dest="renames",
                    metavar="container.path:oldLeaf=newLeaf",
                    help="rename one dict key in place (order preserved); refuses if absent or if the new name is taken")
    ap.add_argument("--add-rung-power-budget", type=int, default=None, dest="add_rung_power_budget",
                    metavar="REFERENCE_POWER",
                    help="derive and add `powerBudgetMilli` to every row of `rows` "
                         "(powerBudgetMilli = poolRolls * REFERENCE_POWER * qPowerMilli / 1000); "
                         "refuses if any row already has the column (action-rungs-shaped only)")
    ap.add_argument("--reprice-rung-power-budget", type=int, default=None, dest="reprice_rung_power_budget",
                    metavar="REF",
                    help="recompute every row's `powerBudgetMilli` from its own poolRolls/qPowerMilli "
                         "and REF, and set _meta.referencePower = REF (action-rungs-shaped only); "
                         "refuses if any row lacks the column")
    ap.add_argument("--mark-tuned", action="store_true", dest="mark_tuned",
                    help="clear _meta.referencePowerUntuned -- only ever with an explicit flag, so "
                         "'untuned' cannot be cleared by accident")
    ap.add_argument("--add-inherit-cost-table", action="store_true", dest="add_inherit_cost_table",
                    help="derive and add `inheritCostByRarity` from `recipeCost`'s own souls escalation "
                         "(fusion-shaped only); refuses if the key already exists")
    ap.add_argument("--add-regen-block", default=None, dest="add_regen_block",
                    metavar="id=milli,id=milli,...",
                    help="add `regenPerSecondShareMilli` -- one per-mille-of-pool-per-second regen "
                          "share per resource id, covering exactly poolShareMilli's own ids "
                          "(battle-resources-shaped only); refuses if the key already exists")
    ap.add_argument("--add-actor-hud-screen-layout", action="store_true", dest="add_actor_hud_screen_layout",
                    help="add the six screen-space silhouette HUD layout keys to actor-hud once; "
                         "future balance edits use ordinary dotted paths")
    ap.add_argument("--add-actor-hud-element-layout", action="store_true", dest="add_actor_hud_element_layout",
                    help="add the three screen-space element-row geometry keys to actor-hud once")
    ap.add_argument("--upgrade-actor-hud-identity-element-layout", action="store_true", dest="upgrade_actor_hud_identity_element_layout",
                    help="replace v5 detached element-row geometry with the v6 identity-line schema")
    ap.add_argument("--add-element-hud-glyph", action="append", default=[], dest="add_element_hud_glyphs",
                    metavar="id=...,glyph=...",
                    help="add one catalog-owned minimal HUD glyph kind to an element-catalog entry")
    ap.add_argument("--add-vfx-impact-stamp", action="store_true", dest="add_vfx_impact_stamp",
                    help="add the Earth-pilot impactStamp tuning block and explicit units to vfx.v3")
    ap.add_argument("--add-vfx-earth-phase", action="store_true", dest="add_vfx_earth_phase",
                    help="add Earth charge/travel tuning and explicit units to vfx.v4")
    ap.add_argument("--add-key", action="append", default=[], dest="add_keys",
                    metavar="container.path:leaf=<json>",
                    help="add one NEW key holding a JSON value (empty container = root); refuses if it exists")
    ap.add_argument("--remove-edge", action="append", default=[], dest="remove_edges",
                    metavar="channel=..,source=..",
                    help="remove exactly one edge from `edges` (retire-atk R3); refuses if it matches "
                         "zero or more than one")
    ap.add_argument("--remove-key", action="append", default=[], dest="remove_keys",
                    metavar="container.path:leaf",
                    help="remove one dict key (empty container = root); refuses if it is absent")
    ap.add_argument("--remove-entry", action="append", default=[], dest="remove_entries",
                    metavar="<array>[k=v,...]",
                    help="remove exactly one array element (retire-atk R4, e.g. a catalog row); "
                         "refuses if it matches zero or more than one")
    ap.add_argument("--add-category", action="append", default=[], dest="add_categories",
                    metavar="id=..,domain=..,displayName=..,messageKeys=k1|k2",
                    help="append one category row to `categories` (notification-catalog-shaped only); "
                         "refuses a duplicate id, an unknown field, or a channel/severity field")
    ap.add_argument("--promote-toast", default=None, dest="promote_toast",
                    metavar="id1,id2,...",
                    help="replace promotions.toast wholesale with this list (notification-catalog-shaped "
                         "only); refuses an id not registered in categories")
    ap.add_argument("--promote-critical", default=None, dest="promote_critical",
                    metavar="id1,id2,...",
                    help="replace promotions.critical wholesale; refuses an id not registered or not "
                         "also in promotions.toast (critical is a subset of toast)")
    ap.add_argument("--label", default="", help="short human note, stored in _meta.rebalanceLabel")
    ap.add_argument("--tuning-dir", default=None, dest="tuning_dir",
                    help="override the tuning directory (default: data/tuning next to this repo) -- "
                         "for a subprocess caller (solid-enforcement tuning-immutability: a test tool "
                         "measuring against a disposable domain) that must never write into the real "
                         "data/tuning/. A Python caller already has this via monkeypatching "
                         "publish.TUNING_DIR directly (tools/tuning/test_publish_add_key.py's own "
                         "established pattern); this flag is the same override, reachable from a "
                         "`python tools/tuning/publish.py ...` subprocess call.")
    a = ap.parse_args()

    global TUNING_DIR
    if a.tuning_dir:
        TUNING_DIR = a.tuning_dir

    if not os.path.isdir(TUNING_DIR):
        print("no such directory: %s" % TUNING_DIR, file=sys.stderr)
        return 2

    current = latest_version(a.domain)
    if current is None:
        return 2

    src_path = os.path.join(TUNING_DIR, "%s.v%d.json" % (a.domain, current))
    doc = json.loads(io.open(src_path, encoding="utf-8").read())

    changes = []
    for spec in a.renames:
        try:
            old_leaf, new_leaf = rename_key(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append((spec, old_leaf, new_leaf))
        print("  %-52s RENAMED -> %s" % (old_leaf, new_leaf))

    for spec in a.add_keys:
        try:
            path, value = add_key(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append((path, None, value))
        print("  %-52s ADDED" % path)

    # Removals run BEFORE sets/renames/adds: a `set` naming a key a removal deletes would otherwise
    # succeed against a value that is about to be gone, and the diff would claim a change nobody made.
    for spec in a.remove_edges:
        try:
            ch, src = remove_edge(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append(("edges[channel=%s,source=%s]" % (ch, src), "removed", None))
        print("  %-52s REMOVED" % ("%s / %s" % (ch, src)))

    for key in a.remove_keys:
        try:
            path, old = remove_key(doc, key)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append((path, old, None))
        print("  %-52s REMOVED (%r)" % (path, old))

    for spec in a.remove_entries:
        try:
            path, removed = remove_entry(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        shown = removed.get("family", removed.get("channel", removed)) if isinstance(removed, dict) else removed
        changes.append((path, shown, None))
        print("  %-52s REMOVED (%r)" % (path, shown))

    # add_categories has no ordering dependency on the removals above (disjoint domains: a
    # retire-atk removal never touches notification-catalog's own `categories`), so it runs
    # right after them, before sets/renames/adds as the same removals-first rule intends.
    for spec in a.add_categories:
        try:
            row = add_notification_category(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append(("categories[id=%s]" % row["id"], None, row))
        print("  %-52s ADDED" % ("category %s" % row["id"]))

    for kv in a.sets:
        split = split_kv(kv)
        if split is None:
            print("not a key=value pair: %r" % kv, file=sys.stderr)
            return 2
        key, raw = split
        try:
            old = value_at(doc, key)
            value = parse_value_for(old, raw, key)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        try:
            set_path(doc, key, value)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        if old == value:
            print("  %-40s unchanged (%r)" % (key, value))
        else:
            changes.append((key, old, value))
            print("  %-40s %r -> %r" % (key, old, value))

    # AFTER sets/renames on purpose: a rename can create the family an --add-edge then extends.
    for spec in a.add_edges:
        try:
            ch, src, k = add_edge(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append((("edges[channel=%s,source=%s].kMilli" % (ch, src)), None, k))
        print("  %-52s ADDED (%r)" % ("%s / %s" % (ch, src), k))

    if a.add_rung_power_budget is not None:
        try:
            added = add_rung_power_budget(doc, a.add_rung_power_budget)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for rung, budget in added:
            changes.append(("rows[rung=%s].powerBudgetMilli" % rung, None, budget))
            print("  %-52s ADDED (%r)" % ("rung %s powerBudgetMilli" % rung, budget))

    if a.reprice_rung_power_budget is not None:
        try:
            repriced = reprice_rung_power_budget(doc, a.reprice_rung_power_budget, a.mark_tuned)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for rung, old, new in repriced:
            changes.append(("rows[rung=%s].powerBudgetMilli" % rung, old, new))
            print("  %-52s %r -> %r" % ("rung %s powerBudgetMilli" % rung, old, new))
        changes.append(("_meta.referencePower", None, a.reprice_rung_power_budget))
        print("  %-52s %r%s" % ("_meta.referencePower", a.reprice_rung_power_budget,
                                "" if a.mark_tuned else " (still untuned)"))

    if a.add_inherit_cost_table:
        try:
            table = add_inherit_cost_table(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for rarity, souls in table.items():
            changes.append(("inheritCostByRarity.%s" % rarity, None, souls))
        print("  %-52s ADDED (%d rung(s))" % ("inheritCostByRarity", len(table)))

    if a.add_regen_block is not None:
        try:
            block = add_regen_block(doc, a.add_regen_block)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for rid, v in block.items():
            changes.append(("regenPerSecondShareMilli.%s" % rid, None, v))
        print("  %-52s ADDED (%d id(s))" % ("regenPerSecondShareMilli", len(block)))

    if a.add_actor_hud_screen_layout:
        if a.domain != "actor-hud":
            print("refused: --add-actor-hud-screen-layout applies only to actor-hud", file=sys.stderr)
            return 1
        try:
            layout = add_actor_hud_screen_layout(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for key, value in layout.items():
            changes.append((key, None, value))
        print("  %-52s ADDED (%d screen layout key(s))" % ("actor-hud screen layout", len(layout)))

    if a.add_actor_hud_element_layout:
        if a.domain != "actor-hud":
            print("refused: --add-actor-hud-element-layout applies only to actor-hud", file=sys.stderr)
            return 1
        try:
            layout = add_actor_hud_element_layout(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for key, value in layout.items():
            changes.append((key, None, value))
        print("  %-52s ADDED (%d element layout key(s))" % ("actor-hud element layout", len(layout)))

    if a.upgrade_actor_hud_identity_element_layout:
        if a.domain != "actor-hud":
            print("refused: --upgrade-actor-hud-identity-element-layout applies only to actor-hud", file=sys.stderr)
            return 1
        try:
            layout = upgrade_actor_hud_identity_element_layout(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for key, value in layout.items():
            changes.append((key, None, value))
        print("  %-52s UPGRADED (%d v6 identity element key(s))" % ("actor-hud identity element layout", len(layout)))

    for spec in a.add_element_hud_glyphs:
        try:
            element_id, glyph = add_element_hud_glyph(doc, spec)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append(("entries[id=%s].hudGlyph" % element_id, None, glyph))
        print("  %-52s ADDED (%s)" % ("%s.hudGlyph" % element_id, glyph))

    if a.add_vfx_impact_stamp:
        if a.domain != "vfx":
            print("refused: --add-vfx-impact-stamp applies only to vfx", file=sys.stderr)
            return 1
        try:
            stamp = add_vfx_impact_stamp(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for key, value in stamp.items():
            changes.append(("impactStamp." + key, None, value))
        print("  %-52s ADDED (%d presentation key(s))" % ("impactStamp", len(stamp)))
    if a.add_vfx_earth_phase:
        if a.domain != "vfx":
            print("refused: --add-vfx-earth-phase applies only to vfx", file=sys.stderr)
            return 1
        try:
            phase = add_vfx_earth_phase(doc)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        for key, value in phase.items():
            changes.append(("earthPhase." + key, None, value))
        print("  %-52s ADDED (%d presentation key(s))" % ("earthPhase", len(phase)))
    if a.promote_toast is not None:
        try:
            old, new = set_promotion_toast(doc, a.promote_toast)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append(("promotions.toast", old, new))
        print("  %-52s %r -> %r" % ("promotions.toast", old, new))

    if a.promote_critical is not None:
        try:
            old, new = set_promotion_critical(doc, a.promote_critical)
        except KeyError as e:
            print("refused: %s" % e, file=sys.stderr)
            return 1
        changes.append(("promotions.critical", old, new))
        print("  %-52s %r -> %r" % ("promotions.critical", old, new))

    if not changes:
        print("no changes — nothing published")
        return 1

    new_version = current + 1
    doc["version"] = new_version
    if a.label:
        doc.setdefault("_meta", {})["rebalanceLabel"] = a.label

    dst_path = os.path.join(TUNING_DIR, "%s.v%d.json" % (a.domain, new_version))
    if os.path.exists(dst_path):
        print("refusing to overwrite existing %s" % dst_path, file=sys.stderr)
        return 1

    io.open(dst_path, "w", encoding="utf-8").write(json.dumps(doc, indent=2) + "\n")
    print("\npublished %s (v%d -> v%d, %d change(s)); v%d stays on disk for revert"
          % (a.domain, current, new_version, len(changes), current))
    return 0


if __name__ == "__main__":
    sys.exit(main())
