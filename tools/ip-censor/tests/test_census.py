"""Tests for the distinct-token census (spec-census.md §Testing Strategy).

Fixture content lives under `tests/fixtures/census/` and is materialised into a tmp root at the
repo-relative path each case is about: `census` classifies surfaces by path, so the path is part of
the input, not an accident of where the test file happens to sit.

Shape is asserted, never a population total (validation-ssot.md).
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from ipcensor.census import ROOT_TREE, census, tokenize, tree_of
from ipcensor.registry import REGISTRY_FILES, SURFACES, parse_registry
from ipcensor.source import read_file

TESTS_DIR = Path(__file__).resolve().parent
FIXTURES = TESTS_DIR / "fixtures" / "census"
REGISTRY_FIXTURES = TESTS_DIR / "fixtures" / "registry" / "valid"


def fixture_registry():
    return parse_registry(
        {name: (REGISTRY_FIXTURES / name).read_text(encoding="utf-8") for name in REGISTRY_FILES}
    )


def materialise(tmp_path: Path) -> Path:
    """Copy the manifest's fixture content to its intended repo-relative path under a tmp root."""
    manifest = json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
    for target, fixture_name in manifest.items():
        destination = tmp_path / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text((FIXTURES / fixture_name).read_text(encoding="utf-8"), "utf-8")
    return tmp_path


def fixture_files(tmp_path: Path):
    manifest = json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
    root = materialise(tmp_path)
    return [read_file(root, target) for target in manifest]


def tokens_of(text: str) -> list[str]:
    return [token for token, _ in tokenize(text, fixture_registry().boundary)]


# ---- tokenisation ---------------------------------------------------------------


def test_tokenisation_uses_the_policy_separators() -> None:
    assert tokens_of("PvZ Fusion") == ["pvz", "fusion"]
    assert tokens_of("pvz-fusion-almanac-3.6.1") == ["pvz", "fusion", "almanac", "3", "6", "1"]
    assert tokens_of("drop.pvz.run") == ["drop", "pvz", "run"]
    assert tokens_of('"pvz.*"') == ["pvz"]


def test_an_identifier_compound_is_one_token_not_the_alias() -> None:
    # The two measured `\b` failures: `PVZRH`/`PvZ2` must not fold into `pvz`, and a script change
    # must break a run that `\b` cannot break at all.
    assert tokens_of("PVZRH") == ["pvzrh"]
    assert tokens_of("PvZ2 strategies") == ["pvz2", "strategies"]
    assert tokens_of("PvZ融合版") == ["pvz", "融合版"]
    assert tokens_of("使用PvZ Fusion的新大门") == ["使用", "pvz", "fusion", "的新大门"]


def test_demonstrate_does_not_yield_demon() -> None:
    tokens = tokens_of("demonstrate demonic demon")
    assert "demon" in tokens
    assert "demonstrate" in tokens
    assert tokens.count("demon") == 1


def test_folding_is_casefold_and_not_lower() -> None:
    # `ẞ`.lower() is `ß`; `ẞ`.casefold() is `ss`. Only casefold folds it.
    assert "ẞ".lower() != "ss"
    assert tokens_of("ẞ") == ["ss"]


# ---- census shape ---------------------------------------------------------------


def test_census_reports_every_required_field(tmp_path: Path) -> None:
    stats = census(fixture_files(tmp_path), fixture_registry())
    assert stats

    by_token = {stat.token: stat for stat in stats}
    for stat in stats:
        assert stat.token == stat.token.casefold()
        assert stat.display
        assert stat.total > 0
        assert stat.by_tree
        assert stat.by_surface
        assert sum(stat.by_tree.values()) == stat.total
        assert sum(stat.by_surface.values()) == stat.total
        assert set(stat.by_surface) <= SURFACES
        assert all(count >= 0 for count in stat.by_tree.values())

    # The same casings collapse to one token, and the dominant casing is what a report shows.
    # Exact numbers are asserted here because this is a closed fixture, not a population.
    assert by_token["pvz"].total == 9
    assert by_token["pvz"].display == "pvz"
    assert set(by_token["pvz"].by_tree) == {"src", "data"}
    assert by_token["pvz"].by_tree == {"data": 7, "src": 2}


def test_census_is_sorted_by_total_then_token(tmp_path: Path) -> None:
    stats = census(fixture_files(tmp_path), fixture_registry())
    keys = [(-stat.total, stat.token) for stat in stats]
    assert keys == sorted(keys)


def test_census_is_byte_identical_across_two_runs(tmp_path: Path) -> None:
    first = census(fixture_files(tmp_path), fixture_registry())
    second = census(fixture_files(tmp_path), fixture_registry())
    assert first == second
    assert json.dumps([stat.as_dict() for stat in first], ensure_ascii=False) == json.dumps(
        [stat.as_dict() for stat in second], ensure_ascii=False
    )


def test_boundary_fixture_classifies_as_the_policy_declares(tmp_path: Path) -> None:
    stats = census(fixture_files(tmp_path), fixture_registry())
    by_token = {stat.token: stat for stat in stats}

    # `PvZ融合版` and `pvz-fusion-almanac-3.6.1` both yield the `pvz` token; the compound
    # identifiers yield their own tokens and never fold into it.
    assert "pvz" in by_token
    assert "pvzrh" in by_token
    assert "pvz2" in by_token
    assert set(by_token["pvz"].by_surface) <= SURFACES
    # `drop.pvz.run` sits in a code path, so that occurrence is reported on the code surface.
    assert by_token["pvz"].by_surface["code-identifier"] >= 1


def test_a_name_registry_is_a_player_name_surface(tmp_path: Path) -> None:
    stats = census(fixture_files(tmp_path), fixture_registry())
    by_token = {stat.token: stat for stat in stats}
    assert by_token["demonstrate"].by_surface == {"player-name": 1}


def test_tree_of_uses_the_first_path_segment() -> None:
    assert tree_of("docs/guide/the-game.md") == "docs"
    assert tree_of("src/FusionRpg.Server/Namespaces.cs") == "src"
    assert tree_of("README.md") == ROOT_TREE


def test_census_of_nothing_is_empty() -> None:
    assert census((), fixture_registry()) == ()


def test_as_dict_sorts_nested_keys(tmp_path: Path) -> None:
    stats = census(fixture_files(tmp_path), fixture_registry())
    for stat in stats:
        payload = stat.as_dict()
        assert list(payload["by_tree"]) == sorted(payload["by_tree"])
        assert list(payload["by_surface"]) == sorted(payload["by_surface"])


@pytest.mark.parametrize("text", ["", "   ", "---", "..."])
def test_text_without_tokens_yields_nothing(text: str) -> None:
    assert tokens_of(text) == []
