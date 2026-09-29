#!/usr/bin/env python3
"""
Golden-attribution guard — class-system-todo.md P6.4.

BattleGoldenTests.cs already refuses a SILENT re-bless (a moved hash with no RulesetVersion bump
fails the test outright). This adds the other half: WHO moved it and WHY, checked mechanically
rather than trusted from a comment block. A hash may legitimately move when RulesetVersion bumps --
but the bump must be matched by an entry in golden-attribution.json naming the program and task,
or this script fails the build. Two entries claiming the SAME RulesetVersion from different
programs is a two-stream collision -- also a failure, independent of whether that version is the
one currently live.

Sources read, none hand-retyped (matching _baseline-goldens.json's own "extracted, not
hand-retyped" rule):
  - LIVE ruleset version:  gk-core/src/FusionRpg.Core/Battle/BattleModels.cs        (RulesetVersion const)
  - LIVE golden hashes:    gk-core/tests/FusionRpg.Core.Tests/Battle/BattleGoldenTests.cs (four consts)
  - LAST BLESSED snapshot: docs/research/class-system/_baseline-goldens.json
  - ATTRIBUTION log:       docs/research/class-system/golden-attribution.json

Usage (repo root):
    python gk-core/scripts/verify-golden-attribution.py

Exit codes: 0 = clean (no move, or every move attributed with no collision), 1 = unattributed
move / collision / silent re-bless, 2 = usage or parse error (a source file this script depends on
does not have the shape it expects).
"""
import io, json, os, re, sys
from pathlib import Path

# Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/scripts/lib/keepverse_roots.py).
# TWO roots here, not one: `src/**` and `tests/**` are the ENGINE repo (core), while `docs/**` is the
# WORKSPACE. The tokens kvsplit's `rules/scan.v1.json` `resolvers` looks for are `core_root(` and
# `workspace_root(`; one private `..` walk for both is what breaks when the repo splits.
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import core_root, workspace_root  # noqa: E402  (the path shim above must run first)

CORE_ROOT = str(core_root())
WORKSPACE_ROOT = str(workspace_root())
BATTLE_MODELS = os.path.join(CORE_ROOT, "src", "FusionRpg.Core", "Battle", "BattleModels.cs")
GOLDEN_TESTS = os.path.join(CORE_ROOT, "tests", "FusionRpg.Core.Tests", "Battle", "BattleGoldenTests.cs")
BASELINE_PATH = os.path.join(WORKSPACE_ROOT, "docs", "research", "class-system", "_baseline-goldens.json")
ATTRIBUTION_PATH = os.path.join(WORKSPACE_ROOT, "docs", "research", "class-system", "golden-attribution.json")

HASH_NAMES = ["stompHash", "closeHash", "wipeHash", "seedSweepHash"]
# Maps the JSON key (baseline/attribution) to the C# const name (BattleGoldenTests.cs).
CONST_NAME = {"stompHash": "StompHash", "closeHash": "CloseHash", "wipeHash": "WipeHash", "seedSweepHash": "SeedSweepHash"}


def read(path):
    with io.open(path, encoding="utf-8-sig") as f:
        return f.read()


def load_json(path):
    return json.loads(read(path))


def live_ruleset_version():
    text = read(BATTLE_MODELS)
    m = re.search(r"const\s+int\s+RulesetVersion\s*=\s*(\d+)\s*;", text)
    if not m:
        print(f"could not find 'const int RulesetVersion = N;' in {BATTLE_MODELS}", file=sys.stderr)
        sys.exit(2)
    return int(m.group(1))


def live_hashes():
    text = read(GOLDEN_TESTS)
    result = {}
    for json_key, const_name in CONST_NAME.items():
        m = re.search(r'const\s+string\s+' + const_name + r'\s*=\s*"([0-9A-Fa-f]+)"\s*;', text)
        if not m:
            print(f"could not find 'const string {const_name} = \"...\";' in {GOLDEN_TESTS}", file=sys.stderr)
            sys.exit(2)
        result[json_key] = m.group(1)
    return result


def main():
    live_version = live_ruleset_version()
    live = live_hashes()

    baseline = load_json(BASELINE_PATH)
    attribution = load_json(ATTRIBUTION_PATH)
    entries = attribution["entries"]

    # ── Two-stream collision: any two entries sharing a rulesetVersion, independent of what is
    # live right now -- a corrupt attribution file is a failure on its own terms.
    by_version = {}
    for e in entries:
        v = e["rulesetVersion"]
        if v in by_version:
            other = by_version[v]
            print(f"GOLDEN ATTRIBUTION GUARD FAILED — two-stream collision at rulesetVersion {v}:", file=sys.stderr)
            print(f"  '{other['program']}' / '{other['task']}' ({other['date']})", file=sys.stderr)
            print(f"  '{e['program']}' / '{e['task']}' ({e['date']})", file=sys.stderr)
            print("  Two streams claimed the same RulesetVersion number — pick a new one for the second.", file=sys.stderr)
            return 1
        by_version[v] = e

    baseline_version = baseline["rulesetVersion"]
    moved = [name for name in HASH_NAMES if baseline.get(name) != live[name]]

    if live_version == baseline_version:
        if moved:
            print("GOLDEN ATTRIBUTION GUARD FAILED — silent re-bless:", file=sys.stderr)
            print(f"  {', '.join(moved)} moved with NO RulesetVersion bump (still {live_version}).", file=sys.stderr)
            print("  BattleGoldenTests.cs's own rule: a diff here MUST be a conscious bump, never silent.", file=sys.stderr)
            return 1
        print(f"GOLDEN ATTRIBUTION GUARD OK — rulesetVersion {live_version} unchanged, no golden moved.")
        return 0

    # A bump happened. Every hash that actually moved must be named in an entry for live_version.
    entry = by_version.get(live_version)
    if entry is None:
        print("GOLDEN ATTRIBUTION GUARD FAILED — unattributed RulesetVersion bump:", file=sys.stderr)
        print(f"  baseline rulesetVersion {baseline_version} -> live {live_version}, but golden-attribution.json", file=sys.stderr)
        print(f"  has no entry for rulesetVersion {live_version}. Add one naming the program and task.", file=sys.stderr)
        return 1

    unattributed = [name for name in moved if name not in entry["hashes"]]
    if unattributed:
        print("GOLDEN ATTRIBUTION GUARD FAILED — hash moved but not listed in its own rulesetVersion's entry:", file=sys.stderr)
        print(f"  rulesetVersion {live_version} ('{entry['program']}' / '{entry['task']}') lists: {entry['hashes']}", file=sys.stderr)
        print(f"  but these ALSO moved and are missing from that list: {unattributed}", file=sys.stderr)
        return 1

    print(f"GOLDEN ATTRIBUTION GUARD OK — rulesetVersion {baseline_version} -> {live_version}, "
          f"{len(moved)} hash(es) moved, fully attributed to '{entry['program']}' / '{entry['task']}' ({entry['date']}).")
    if len(moved) < len(HASH_NAMES):
        unmoved = [n for n in HASH_NAMES if n not in moved]
        print(f"  (unmoved this bump: {unmoved} — fine; not every bump moves every golden.)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
