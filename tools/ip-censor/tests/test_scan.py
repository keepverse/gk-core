"""Tests for `scan` (spec-scan.md §Testing Strategy).

Two registries are used: the invented-mark fixture (every bucket, the boundary table, determinism)
and a copy of the day-one set plus the owner-confirmed `overwatch` group, which the known-collision
acceptance needs. Real marks live only under `gk-core/tests/fixtures/`, per this program's conventions.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path
from typing import Any

import pytest

from ipcensor.registry import REGISTRY_FILES, parse_registry
from ipcensor.scan import BUCKETS, AliasMatcher, bucket_for, has_token_boundary, scan
from ipcensor.source import (
    TEXT_BASENAMES,
    TEXT_EXTENSIONS,
    find_root,
    is_text_path,
    iter_files,
    matches_any,
    read_file,
    tracked_paths,
)

TESTS_DIR = Path(__file__).resolve().parent
FIXTURES = TESTS_DIR / "fixtures" / "scan"
REGISTRY_FIXTURES = TESTS_DIR / "fixtures" / "registry" / "valid"
REPO_ROOT = find_root(TESTS_DIR)


def _registry_files() -> dict[str, str]:
    return {name: (REGISTRY_FIXTURES / name).read_text(encoding="utf-8") for name in REGISTRY_FILES}


def invented_registry():
    return parse_registry(_registry_files())


def overwatch_registry():
    files = _registry_files()
    marks = json.loads(files["marks.v1.json"])
    marks["groups"].append(json.loads((FIXTURES / "overwatch" / "group.json").read_text("utf-8")))
    files["marks.v1.json"] = json.dumps(marks)
    return parse_registry(files)


def manifest() -> dict[str, str]:
    return json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))


def materialise(tmp_path: Path) -> Path:
    for target, fixture_name in manifest().items():
        destination = tmp_path / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text((FIXTURES / fixture_name).read_text(encoding="utf-8"), "utf-8")
    # The reader enumerates `git ls-files`, so the synthetic tree is indexed like a real one.
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True)
    subprocess.run(["git", "add", "-A", "-f"], cwd=tmp_path, check=True)
    return tmp_path


def fixture_files(tmp_path: Path, **kwargs: Any):
    root = materialise(tmp_path)
    return [read_file(root, target) for target in manifest()]


def findings_for(tmp_path: Path, registry=None):
    registry = registry if registry is not None else invented_registry()
    return scan(fixture_files(tmp_path), registry)


# ---- the boundary table ---------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "start", "end", "expected"),
    [
        ("PvZ Fusion", 0, 3, True),  # the target
        ("PVZRH", 0, 3, False),  # an identifier compound, not the mark
        ("PvZ2 strategies", 0, 3, False),  # a different game's mark
        ("pvz-fusion-almanac-3.6.1", 0, 3, True),  # `-` and `.` are declared separators
        ("drop.pvz.run", 5, 8, True),  # a namespace token; its bucket follows the surface
        ("PvZ融合版", 0, 3, True),  # a script change is a boundary, unlike `\b`
        ("Examplemarketing", 0, 11, False),  # substring guard (the `demon`/`demonstrate` shape)
        ("mark: Examplemark.", 6, 17, True),
    ],
)
def test_boundary_table(text: str, start: int, end: int, expected: bool) -> None:
    assert has_token_boundary(text, start, end, invented_registry().boundary) is expected


def test_the_matcher_finds_only_the_whole_token() -> None:
    matcher = AliasMatcher(invented_registry())
    text = "Examplemarketing is not Examplemark, and PVZRH is not either"
    hits = [text[start:end] for start, end, _ in matcher.matches(text)]
    assert hits == ["Examplemark"]


def test_matching_is_longest_first() -> None:
    matcher = AliasMatcher(invented_registry())
    # "example mark" is an alias and "Examplemark" is one; the longer spelling wins at its own span.
    hits = [(start, end) for start, end, _ in matcher.matches("an example mark")]
    assert len(hits) == 1
    assert hits[0] == (3, 15)


def test_matching_maps_folded_offsets_back_to_the_original_text() -> None:
    matcher = AliasMatcher(invented_registry())
    # `ẞ`.casefold() is `ss`, so the folded text is longer than the original: the span reported must
    # still be a span of the ORIGINAL text.
    text = "ẞ Examplemark"
    spans = [(start, end) for start, end, _ in matcher.matches(text)]
    assert spans == [(2, 13)]
    assert text[2:13] == "Examplemark"


# ---- buckets --------------------------------------------------------------------


def test_every_bucket_has_exactly_one_canonical_example(tmp_path: Path) -> None:
    pairs = {(finding.path, finding.bucket) for finding in findings_for(tmp_path)}

    assert pairs == {
        ("data/seed/narrative/_registry/names.en.v1.json", "player-name"),
        ("docs/guide/the-game.md", "player-prose"),
        ("tools/seedsmith/seedsmith/adapters/items/uniques/briefs.py", "generator-prompt"),
        ("src/FusionRpg.Core/World/WorldTemplateCatalog.cs", "code-identifier"),
        ("docs/research/prior-art.md", "docs-prose-citation"),
        ("docs/architecture/software-architecture.md", "deliberate-identity"),
        ("data/seed/items/_registry/kinds.json", "registry-self"),
    }
    assert {finding.bucket for finding in findings_for(tmp_path)} <= BUCKETS


def test_the_bucket_vocabulary_is_closed() -> None:
    assert BUCKETS == {
        "player-name",
        "player-prose",
        "generator-prompt",
        "code-identifier",
        "docs-prose-citation",
        "deliberate-identity",
        "registry-self",
    }


def test_bucket_for_is_a_pure_surface_rule() -> None:
    assert bucket_for("player-name", "anything.json") == "player-name"
    assert bucket_for("code-identifier", "anything.ts") == "code-identifier"
    assert bucket_for("registry", "data/seed/items/_registry/kinds.json") == "registry-self"
    assert bucket_for("docs-prose", "docs/research/x.md") == "docs-prose-citation"
    assert bucket_for("docs-prose", "docs/architecture/x.md") == "deliberate-identity"
    assert bucket_for("docs-prose", "tasks/x.md") == "deliberate-identity"


def test_an_architecture_doc_hit_is_a_deliberate_identity(tmp_path: Path) -> None:
    findings = [
        finding
        for finding in findings_for(tmp_path)
        if finding.path == "docs/architecture/software-architecture.md"
    ]
    assert findings
    assert {finding.bucket for finding in findings} == {"deliberate-identity"}
    # Report-only: the enforced set is the three player-facing surfaces (IC-3).
    assert not invented_registry().is_enforced(findings[0].surface)


def test_a_player_guide_hit_is_player_prose(tmp_path: Path) -> None:
    findings = [
        finding for finding in findings_for(tmp_path) if finding.path == "docs/guide/the-game.md"
    ]
    assert findings
    assert {finding.bucket for finding in findings} == {"player-prose"}
    assert invented_registry().is_enforced(findings[0].surface)


def test_a_placeholder_in_narrative_prose_yields_nothing(tmp_path: Path) -> None:
    findings = [
        finding
        for finding in findings_for(tmp_path)
        if finding.path == "data/seed/narrative/storylet.json"
    ]
    assert findings == []


# ---- known-collision acceptance (A9) --------------------------------------------


def test_the_pre_fix_node_is_reported_at_its_own_line(tmp_path: Path) -> None:
    registry = overwatch_registry()
    root = materialise(tmp_path)
    target = "data/seed/passive-tree/nodes/command.json"
    item = read_file(root, target)

    findings = scan([item], registry)

    # Two hits: the node's `name` and its `nameKey`, both on the player-name surface.
    assert all(finding.bucket == "player-name" for finding in findings)
    assert all(finding.remediation == "generator-owned" for finding in findings)
    assert all(finding.surface == "player-name" for finding in findings)
    assert all(finding.mark == "overwatch" for finding in findings)

    finding = next(finding for finding in findings if finding.matched == "Overwatch")
    assert finding.path == target
    line_text = item.text.splitlines()[finding.line - 1]
    assert finding.column == line_text.index("Overwatch Protocol") + 1
    assert "Overwatch Protocol" in line_text


def test_remediation_follows_provenance_per_file(tmp_path: Path) -> None:
    registry = overwatch_registry()
    root = materialise(tmp_path)
    node = scan([read_file(root, "data/seed/passive-tree/nodes/command.json")], registry)
    registry_self = scan([read_file(root, "data/seed/items/_registry/kinds.json")], registry)

    assert node and all(finding.remediation == "generator-owned" for finding in node)
    assert registry_self and registry_self[0].remediation == "authored"


# ---- determinism and self-exclusion ---------------------------------------------


def test_two_scans_are_identical(tmp_path: Path) -> None:
    first = findings_for(tmp_path)
    second = findings_for(tmp_path)
    assert first == second
    assert json.dumps([finding.as_dict() for finding in first]) == json.dumps(
        [finding.as_dict() for finding in second]
    )


def test_findings_are_ordered_by_path_line_column_mark(tmp_path: Path) -> None:
    findings = findings_for(tmp_path)
    keys = [(f.path, f.line, f.column, f.mark) for f in findings]
    assert keys == sorted(keys)


def test_self_paths_are_excluded_by_the_reader(tmp_path: Path) -> None:
    registry = invented_registry()
    root = materialise(tmp_path)
    self_paths = ("docs/architecture/**",)

    paths = [item.path for item in iter_files(root, self_paths=self_paths)]
    assert "docs/architecture/software-architecture.md" not in paths
    assert "docs/guide/the-game.md" in paths

    findings = scan(iter_files(root, self_paths=self_paths), registry)
    assert not [f for f in findings if matches_any(f.path, self_paths)]


def test_the_real_tree_keeps_its_own_docs_out_of_scope() -> None:
    # The program must be able to name the marks it bans, in the documents that explain the ban. That
    # is only true if the self paths are applied, and the exclusion is only load-bearing if the marks
    # ARE there: this asserts the relationship, not a count (validation-ssot.md).
    registry = overwatch_registry()
    findings = scan(iter_files(REPO_ROOT), registry)

    inside_self_paths = [
        finding for finding in findings if matches_any(finding.path, registry.self_paths)
    ]
    assert inside_self_paths, "no self-path hit found; the exclusion is untested"

    # And the self paths really are part of the scannable tree, so the exclusion covers something.
    scannable = [
        path
        for path in tracked_paths(REPO_ROOT)
        if is_text_path(path, extensions=TEXT_EXTENSIONS, basenames=TEXT_BASENAMES)
    ]
    assert scannable
    assert [path for path in scannable if matches_any(path, registry.self_paths)]
