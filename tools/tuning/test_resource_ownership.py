"""Tests for module A-R1 (docs/architecture/action-corpus/spec-resource-ownership.md SS5). The eight
named cases, in order. Run with `python -m pytest gk-core/tools/tuning/test_resource_ownership.py -v` from the
repo root, or plain `python -m pytest gk-core/tools/tuning`.
"""
import copy
import json
import os

import pytest

import resource_ownership as ro


REPO_ROOT = ro.REPO_ROOT


@pytest.fixture(scope="module")
def resource_ids():
    return ro.load_resource_ids(REPO_ROOT)


@pytest.fixture(scope="module")
def aptitude_ids():
    return ro.load_aptitude_roster(REPO_ROOT)


@pytest.fixture(scope="module")
def table():
    return ro.load_ownership_table(REPO_ROOT)


# ── test 1: the hard gate ───────────────────────────────────────────────────────────────────────────

def test_1_generation_reproduces_shipped_resource_edges_byte_for_byte(table, resource_ids, aptitude_ids):
    generated = ro.generate_edges(table, resource_ids, aptitude_ids)
    shipped, version = ro.load_shipped_resource_edges(REPO_ROOT, "aptitudes")

    # No version or edge-count pin: a tuning publish (v5 -> v9) or an honest population change fails
    # one, and the "fix" is to bump a literal (validation-ssot.md — counts are readings). The
    # triple-set equality is the contract.
    assert ro.edge_triples(generated) == ro.edge_triples(shipped)


# ── test 2: the reason this module exists ───────────────────────────────────────────────────────────

def test_2_seventh_resource_emits_24_new_edges_with_no_generator_change(table, resource_ids, aptitude_ids):
    """A 7th resource id fed into the SAME generate_edges() -- only the table and the resource-id list
    change, exactly like a real ResourceIds append would. Real density: only `max` and `regen` are
    dense, so a resource declared in only those two families' floors yields 2 x 12 = 24 new edges, not
    the spec's original 36 (which assumed a 3rd dense family -- see resource-ownership.v1.json's own
    _meta.note for the full reconciliation)."""
    fixture_resource_ids = list(resource_ids) + ["morale"]

    fixture_table = copy.deepcopy(table)
    fixture_table["families"]["max"]["floors"]["morale"] = 4000
    fixture_table["families"]["regen"]["floors"]["morale"] = 100
    # efficiency/restore are sparse -- deliberately NOT given a "morale" owner entry, which is legal
    # (owners-only families do not require coverage of a new resource).

    baseline = ro.generate_edges(table, resource_ids, aptitude_ids)
    expanded = ro.generate_edges(fixture_table, fixture_resource_ids, aptitude_ids)

    new_edges = [e for e in expanded if e["channel"].endswith(".morale")]
    assert len(new_edges) == 24
    assert len(expanded) == len(baseline) + 24
    # the two dense families each contributed exactly one edge per aptitude for the new resource
    assert {e["channel"] for e in new_edges} == {"resource.max.morale", "resource.regen.morale"}
    assert len({e["source"] for e in new_edges if e["channel"] == "resource.max.morale"}) == len(aptitude_ids)
    assert len({e["source"] for e in new_edges if e["channel"] == "resource.regen.morale"}) == len(aptitude_ids)


# ── test 3: --check catches drift ───────────────────────────────────────────────────────────────────

def test_3_check_exits_nonzero_on_hand_modified_shipped_edge(tmp_path, table, resource_ids, aptitude_ids):
    shipped, version = ro.load_shipped_resource_edges(REPO_ROOT, "aptitudes")
    doc = ro._load_json(os.path.join(REPO_ROOT, "data", "tuning", "aptitudes.v%d.json" % version))

    # hand-modify one real resource edge's value, mirroring "a shipped edge is hand-modified"
    mutated = False
    for e in doc["edges"]:
        if isinstance(e, dict) and e.get("channel") == "resource.max.hp" and e.get("source") == "Bulwark":
            e["kMilli"] = e["kMilli"] + 1
            mutated = True
            break
    assert mutated, "fixture assumption broke: resource.max.hp/Bulwark not found in the shipped file"

    tuning_dir = tmp_path / "data" / "tuning"
    tuning_dir.mkdir(parents=True)
    (tuning_dir / ("aptitudes.v%d.json" % version)).write_text(json.dumps(doc), encoding="utf-8")

    ok, message = ro.check(repo_root=str(tmp_path), domain="aptitudes",
                            table=table, resource_ids=resource_ids, aptitude_ids=aptitude_ids)
    assert ok is False
    assert "DRIFT" in message
    assert "Bulwark" in message


def test_3b_check_cli_exit_code_nonzero_on_drift(tmp_path, table, monkeypatch):
    """End-to-end through main(): builds a full fixture repo (tuning + both seed rosters), points
    ro.REPO_ROOT at it (the module-level constant main()'s default loads read), and confirms the CLI
    entry point itself returns the non-zero exit code -- not just the check() function underneath it."""
    shipped, version = ro.load_shipped_resource_edges(REPO_ROOT, "aptitudes")
    doc = ro._load_json(os.path.join(REPO_ROOT, "data", "tuning", "aptitudes.v%d.json" % version))
    for e in doc["edges"]:
        if isinstance(e, dict) and e.get("channel") == "resource.regen.qi" and e.get("source") == "Focus":
            e["kMilli"] = 1
            break

    tuning_dir = tmp_path / "data" / "tuning"
    seed_res = tmp_path / "data" / "seed" / "resources"
    seed_apt = tmp_path / "data" / "seed" / "aptitudes"
    tuning_dir.mkdir(parents=True)
    seed_res.mkdir(parents=True)
    seed_apt.mkdir(parents=True)
    (tuning_dir / ("aptitudes.v%d.json" % version)).write_text(json.dumps(doc), encoding="utf-8")
    (tuning_dir / "resource-ownership.v1.json").write_text(json.dumps(table), encoding="utf-8")
    import shutil
    shutil.copy(os.path.join(REPO_ROOT, "data", "seed", "resources", "roster.json"), seed_res / "roster.json")
    shutil.copy(os.path.join(REPO_ROOT, "data", "seed", "aptitudes", "roster.json"), seed_apt / "roster.json")

    monkeypatch.setattr(ro, "REPO_ROOT", str(tmp_path))
    rc = ro.main(["--check", "--domain", "aptitudes"])
    assert rc == 1


# ── test 4: determinism ─────────────────────────────────────────────────────────────────────────────

def test_4_generation_is_deterministic_across_two_runs(table, resource_ids, aptitude_ids):
    run1 = ro.generate_edges(table, resource_ids, aptitude_ids)
    run2 = ro.generate_edges(copy.deepcopy(table), list(resource_ids), list(aptitude_ids))
    assert run1 == run2
    assert json.dumps(run1) == json.dumps(run2)


# ── test 5: planted violation -- unknown aptitude ───────────────────────────────────────────────────

def test_5_unknown_aptitude_in_table_is_refused_by_name(table, resource_ids, aptitude_ids):
    bad_table = copy.deepcopy(table)
    bad_table["families"]["max"]["owners"]["hp"]["NotARealAptitude"] = 99000

    with pytest.raises(ro.ResourceOwnershipRejection) as exc:
        ro.generate_edges(bad_table, resource_ids, aptitude_ids)
    assert "NotARealAptitude" in str(exc.value)


# ── test 6: planted violation -- dense family missing a resource ───────────────────────────────────

def test_6_dense_family_missing_a_resource_is_refused(table, resource_ids, aptitude_ids):
    bad_table = copy.deepcopy(table)
    del bad_table["families"]["regen"]["floors"]["qi"]

    with pytest.raises(ro.ResourceOwnershipRejection) as exc:
        ro.generate_edges(bad_table, resource_ids, aptitude_ids)
    assert "regen" in str(exc.value)
    assert "qi" in str(exc.value)


def test_6b_sparse_family_declaring_a_floor_is_refused(table, resource_ids, aptitude_ids):
    bad_table = copy.deepcopy(table)
    bad_table["families"]["efficiency"]["floors"]["hp"] = 100

    with pytest.raises(ro.ResourceOwnershipRejection) as exc:
        ro.generate_edges(bad_table, resource_ids, aptitude_ids)
    assert "efficiency" in str(exc.value)


# ── test 7: the existing C# drift guard's intent, held in Python too ──────────────────────────────

def test_7_every_resource_is_fed_in_every_resource_family(table, resource_ids, aptitude_ids):
    """Mirrors AptitudeTuningTests.EveryResourceIsFedInEveryResourceFamily (gk-core/tests/FusionRpg.Core.Tests/
    ClassSystem/AptitudeTuningTests.cs) against the GENERATED edges, not just the shipped file --
    proves the generator itself cannot silently produce a coverage hole."""
    generated = ro.generate_edges(table, resource_ids, aptitude_ids)
    for family in ("resource.max", "resource.regen", "resource.efficiency", "resource.restore"):
        fed = {e["channel"][len(family) + 1:] for e in generated if e["channel"].startswith(family + ".")}
        missing = [r for r in resource_ids if r not in fed]
        assert not missing, "%s missing resource(s): %s" % (family, missing)


# ── test 8: baselines unchanged (follows from test 1 -- nothing shipped is touched) ────────────────

def test_8_generation_never_mutates_its_inputs(table, resource_ids, aptitude_ids):
    """The generator only READS the table/roster and only WRITES a return value -- it must never
    mutate the shipped aptitudes.v5.json, so dominance/residual baselines (computed from that file)
    cannot move as a side effect of running this module (spec test 8; DominanceGuardTests reads the
    same shipped file directly)."""
    before = json.dumps(table, sort_keys=True)
    ro.generate_edges(table, resource_ids, aptitude_ids)
    after = json.dumps(table, sort_keys=True)
    assert before == after

    shipped_path = os.path.join(REPO_ROOT, "data", "tuning", "aptitudes.v5.json")
    before_bytes = open(shipped_path, "rb").read()
    ro.check(REPO_ROOT)
    after_bytes = open(shipped_path, "rb").read()
    assert before_bytes == after_bytes


# ── loader sanity (not spec-numbered, but cheap and catches a broken fixture early) ────────────────

def test_resource_ids_match_known_six(resource_ids):
    assert resource_ids == ["hp", "stamina", "hunger", "spirit", "qi", "poise"]


def test_aptitude_roster_matches_known_twelve(aptitude_ids):
    assert aptitude_ids == [
        "Might", "Fortitude", "Vigor", "Onslaught",
        "Agility", "Composure", "Pierce", "Focus",
        "Bulwark", "Retribution", "Precision", "Ferocity",
    ]


def test_table_rejects_non_integer_kmilli(table, resource_ids, aptitude_ids):
    bad_table = copy.deepcopy(table)
    bad_table["families"]["max"]["floors"]["hp"] = 6000.5
    with pytest.raises(ro.ResourceOwnershipRejection):
        ro.generate_edges(bad_table, resource_ids, aptitude_ids)
