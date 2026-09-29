#!/usr/bin/env python3
"""
Module A-R1 (docs/architecture/action-corpus/spec-resource-ownership.md) -- the generator that makes
resource coverage true by construction, instead of true by 526 hand-maintained edges.

`gk-core/data/tuning/resource-ownership.v1.json` is a 24-cell table (4 families x 6 resources; see that file's
own `_meta.note` for why the spec's "18 rows -> 216 edges" citation is stale -- it predates the 0.3
sparse-efficiency decision and the later `restore` family). This module reads it plus the two real SSOT
mirrors --

    gk-data/packs/fusion/data/seed/resources/roster.json   (DerivedStatChannels.ResourceIds,  DerivedStatChannels.cs:521)
    gk-data/packs/fusion/data/seed/aptitudes/roster.json   (AptitudeCatalog.All,              Aptitude.cs:38-52)

-- and emits the full `resource.*` edge set. Both roster files are the CHECKED-IN MIRROR their own
`_meta.note` says they are ("Code is the load source; this file describes it, it does not drive it" --
tunables-ssot.md SS7.2); Python cannot reference FusionRpg.Core, so reading them here is the same
pattern `scripts/guard-class-system.ps1` and the web app already use, not a third copy of either list.

Usage (repo root):
    python gk-core/tools/tuning/resource_ownership.py --check          # regenerate + diff vs the shipped file
    python gk-core/tools/tuning/resource_ownership.py --emit            # print the generated edges as JSON

Exit codes (--check): 0 = generated edges match the shipped file exactly, 1 = drift (message on stdout
names every mismatched triple), 2 = the table itself is invalid (a planted-violation refusal).

What this module must NOT do (spec SS4): change a shipped coefficient, hand-author an edge, copy
`ResourceIds`/the aptitude roster, bypass `publish.py`, use float for a magnitude, or touch the 1.0
`resource.efficiency` cap (a bounded ratio, exempt under AGENTS.md -- `efficiency` here is a channel
name, this module never reads or writes `DerivedStatPolicy.ResourceEfficiencyCap` itself).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TUNING_DIR = os.path.join(REPO_ROOT, "data", "tuning")

# Canonical emission order. Fixed here (not read off the table's own dict order) so a reordered JSON
# file can never silently change --check's diff order or the generator's determinism guarantee.
FAMILY_ORDER = ("max", "regen", "efficiency", "restore")


class ResourceOwnershipRejection(Exception):
    """Raised when the ownership table (or the roster it is read against) violates a declared
    invariant. Every message names the offending key/value -- never a silent skip or a default
    (spec-resource-ownership.md SS5 tests 5/6)."""


def _load_json(path):
    if not os.path.isfile(path):
        raise ResourceOwnershipRejection("no such file: %s" % path)
    with open(path, encoding="utf-8") as f:
        return json.load(f)


# ── SSOT reads -- never a copied list ───────────────────────────────────────────────────────────────

def load_resource_ids(repo_root=None):
    """The six (soon seven+) actor resource ids, in ordinal order -- gk-data/packs/fusion/data/seed/resources/roster.json,
    the checked-in mirror of DerivedStatChannels.ResourceIds."""
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    doc = _load_json(os.path.join(repo_root, "data", "seed", "resources", "roster.json"))
    entries = doc.get("entries")
    if not isinstance(entries, list) or not entries:
        raise ResourceOwnershipRejection("data/seed/resources/roster.json has no 'entries'")
    return [e["id"] for e in sorted(entries, key=lambda e: e["ordinal"])]


def load_aptitude_roster(repo_root=None):
    """The twelve aptitude ids, in ordinal order -- gk-data/packs/fusion/data/seed/aptitudes/roster.json, the checked-in
    mirror of AptitudeCatalog.All."""
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    doc = _load_json(os.path.join(repo_root, "data", "seed", "aptitudes", "roster.json"))
    entries = doc.get("entries")
    if not isinstance(entries, list) or not entries:
        raise ResourceOwnershipRejection("data/seed/aptitudes/roster.json has no 'entries'")
    return [e["id"] for e in sorted(entries, key=lambda e: e["ordinal"])]


def load_ownership_table(repo_root=None, version=1):
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    path = os.path.join(repo_root, "data", "tuning", "resource-ownership.v%d.json" % version)
    return _load_json(path)


def _require_int_kmilli(value, where):
    # bool is a subclass of int in Python -- exclude it explicitly. kMilli is an integer per-mille
    # field by this file's schema (the C# reader consumes it as an integer), so a non-integer value is a
    # schema error. This is a schema contract, not a floating-point ban (removed 2026-09-15).
    if isinstance(value, bool) or not isinstance(value, int):
        raise ResourceOwnershipRejection(
            "%s must be an integer kMilli (per-mille magnitude, never float) -- got %r" % (where, value))
    return value


# ── the generator itself ────────────────────────────────────────────────────────────────────────────

def generate_edges(table, resource_ids, aptitude_ids):
    """Deterministic and total: the same (table, resource_ids, aptitude_ids) always produces the same
    edge list, in the same order. Returns a list of {"channel", "source", "kMilli"} dicts.

    `resource_ids`/`aptitude_ids` are supplied by the caller (read from the SSOT mirrors above, or from
    a test fixture) -- this function never reads a file and never hardcodes either roster, which is
    what lets test 2 (a 7th resource id) prove zero-code-change coverage.
    """
    families = table.get("families")
    if not isinstance(families, dict):
        raise ResourceOwnershipRejection("table has no 'families' object")

    aptitude_set = set(aptitude_ids)
    edges = []

    for family in FAMILY_ORDER:
        row = families.get(family)
        if not isinstance(row, dict):
            raise ResourceOwnershipRejection("table is missing family '%s'" % family)

        density = row.get("density")
        if density not in ("dense", "sparse"):
            raise ResourceOwnershipRejection(
                "family '%s' has no valid 'density' (must be declared 'dense' or 'sparse', got %r) -- "
                "density is a declared property, never inferred from whether 'floors' is empty "
                "(spec-resource-ownership.md SS3.2/test 6)" % (family, density))

        floors = row.get("floors", {})
        owners = row.get("owners", {})
        if not isinstance(floors, dict):
            raise ResourceOwnershipRejection("family '%s' 'floors' must be an object" % family)
        if not isinstance(owners, dict):
            raise ResourceOwnershipRejection("family '%s' 'owners' must be an object" % family)

        if density == "sparse" and floors:
            raise ResourceOwnershipRejection(
                "family '%s' is declared sparse but carries floor(s) %r -- sparse families are "
                "owners-only, no floor row (spec-resource-ownership.md SS3.2)" % (family, floors))

        if density == "dense":
            missing = [r for r in resource_ids if r not in floors]
            if missing:
                raise ResourceOwnershipRejection(
                    "dense family '%s' has no floor for resource(s) %s -- a dense family must cover "
                    "every resource by construction, that is the whole point of declaring it dense "
                    "(spec-resource-ownership.md test 6)" % (family, ", ".join(missing)))

        for resource_id in resource_ids:
            resource_owners = owners.get(resource_id, {})
            if not isinstance(resource_owners, dict):
                raise ResourceOwnershipRejection(
                    "family '%s' resource '%s' owners must be an object" % (family, resource_id))

            for aptitude_id in resource_owners:
                if aptitude_id not in aptitude_set:
                    raise ResourceOwnershipRejection(
                        "family '%s' resource '%s' names unknown aptitude '%s' -- refusing to guess "
                        "(known aptitudes: %s)" % (family, resource_id, aptitude_id, ", ".join(aptitude_ids)))

            channel = "resource.%s.%s" % (family, resource_id)

            if density == "dense":
                floor_value = _require_int_kmilli(floors[resource_id], "%s floor" % channel)
                for aptitude_id in aptitude_ids:
                    if aptitude_id in resource_owners:
                        value = _require_int_kmilli(resource_owners[aptitude_id],
                                                      "%s/%s owner" % (channel, aptitude_id))
                    else:
                        value = floor_value
                    edges.append({"channel": channel, "source": aptitude_id, "kMilli": value})
            else:
                # sparse: emit only the declared owners, in aptitude-roster order, no floor completion.
                for aptitude_id in aptitude_ids:
                    if aptitude_id not in resource_owners:
                        continue
                    value = _require_int_kmilli(resource_owners[aptitude_id],
                                                  "%s/%s owner" % (channel, aptitude_id))
                    edges.append({"channel": channel, "source": aptitude_id, "kMilli": value})

    return edges


# ── reading the shipped file back, for --check ──────────────────────────────────────────────────────

def latest_domain_version(domain, repo_root=None):
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    pat = re.compile(r"^%s\.v(\d+)\.json$" % re.escape(domain))
    versions = []
    for fn in os.listdir(os.path.join(repo_root, "data", "tuning")):
        m = pat.match(fn)
        if m:
            versions.append(int(m.group(1)))
    if not versions:
        raise ResourceOwnershipRejection("no %s.v*.json in data/tuning" % domain)
    return max(versions)


def load_shipped_resource_edges(repo_root=None, domain="aptitudes", version=None):
    """Returns (edges, version) -- the domain file's `edges` entries whose channel starts with
    'resource.'. `_group` divider rows (no 'channel' key) are skipped, same as AptitudeTuningLoader
    and AptitudeTuningTests.GroupDividersAreSkipped_526RealEdgesNot530RawEntries."""
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    if version is None:
        version = latest_domain_version(domain, repo_root)
    doc = _load_json(os.path.join(repo_root, "data", "tuning", "%s.v%d.json" % (domain, version)))
    raw = doc.get("edges")
    if not isinstance(raw, list):
        raise ResourceOwnershipRejection("'%s.v%d.json' has no 'edges' array" % (domain, version))
    edges = [e for e in raw if isinstance(e, dict) and "channel" in e]
    return [e for e in edges if e["channel"].startswith("resource.")], version


def edge_triples(edges):
    """Order-independent identity for one edge: (channel, source, kMilli), sorted. The shipped file's
    authoring order follows its own section headings, not (family, resource, aptitude) order, so
    "byte-for-byte" here means the SET of edges is identical, not that array position matches --
    spec-resource-ownership.md test 1 names the edges, not the file's byte layout."""
    return sorted((e["channel"], e["source"], e["kMilli"]) for e in edges)


def check(repo_root=None, domain="aptitudes", table=None, resource_ids=None, aptitude_ids=None):
    """Regenerates in memory from the table and diffs against the shipped domain file's resource
    edges. Never writes anything -- this is the read-only drift gate (acceptance criterion 5).
    Returns (ok: bool, message: str)."""
    repo_root = repo_root if repo_root is not None else REPO_ROOT
    resource_ids = resource_ids if resource_ids is not None else load_resource_ids(repo_root)
    aptitude_ids = aptitude_ids if aptitude_ids is not None else load_aptitude_roster(repo_root)
    table = table if table is not None else load_ownership_table(repo_root)

    generated = generate_edges(table, resource_ids, aptitude_ids)
    shipped, version = load_shipped_resource_edges(repo_root, domain)

    gen_set = edge_triples(generated)
    ship_set = edge_triples(shipped)

    if gen_set == ship_set:
        return True, "OK -- %d generated edges match %s.v%d.json's %d resource edges exactly" % (
            len(gen_set), domain, version, len(ship_set))

    gen_only = [t for t in gen_set if t not in ship_set]
    ship_only = [t for t in ship_set if t not in gen_set]
    lines = ["DRIFT -- generated edges do not match %s.v%d.json's resource edges "
             "(%d shipped-only, %d generated-only)" % (domain, version, len(ship_only), len(gen_only))]
    for t in ship_only:
        lines.append("  shipped only:    channel=%s source=%s kMilli=%s" % t)
    for t in gen_only:
        lines.append("  generated only:  channel=%s source=%s kMilli=%s" % t)
    return False, "\n".join(lines)


# ── CLI ──────────────────────────────────────────────────────────────────────────────────────────────

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true",
                     help="regenerate in memory and diff against the shipped aptitudes file (default action)")
    ap.add_argument("--emit", action="store_true",
                     help="print the generated edge list as JSON instead of checking")
    ap.add_argument("--domain", default="aptitudes",
                     help="tuning domain whose resource.* edges to check against (default: aptitudes)")
    a = ap.parse_args(argv)

    try:
        if a.emit:
            resource_ids = load_resource_ids()
            aptitude_ids = load_aptitude_roster()
            table = load_ownership_table()
            edges = generate_edges(table, resource_ids, aptitude_ids)
            print(json.dumps(edges, indent=2))
            return 0

        ok, message = check(domain=a.domain)
        print(message)
        return 0 if ok else 1
    except ResourceOwnershipRejection as e:
        print("refused: %s" % e, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
