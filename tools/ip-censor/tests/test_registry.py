"""Tests for the registry parser (spec-registry.md §Testing Strategy).

Fixtures use invented marks only, so a fixture copy of a real row is never needed to prove the rules.
Every rejection test asserts BOTH the file name and the offending key appear in the message: the
message is the contract (tunables-ssot.md T5, "a load rejection naming it").
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable

import pytest

from ipcensor.registry import (
    ADMISSION_EVIDENCE,
    CATEGORIES,
    EVIDENCES,
    EXTERNAL_REFERENCE_ROOT,
    IMPORT_RENAMES_FILE,
    MARKS_FILE,
    REGISTRY_FILES,
    REMEDIATIONS,
    REPLACEMENTS_FILE,
    STAGES,
    SURFACES,
    RegistryError,
    carries_provenance,
    classify_remediation,
    is_enforced,
    load_import_renames,
    load_registry,
    parse_boundary_policy,
    parse_import_renames,
    parse_marks,
    parse_registry,
    parse_replacements,
    parse_scope_policy,
    render_marks,
    surface_for,
)
from ipcensor.roots import owned_dir, owned_path
from ipcensor.source import matches_any, tracked_paths

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "registry" / "valid"
REMEDIATION_FIXTURES = FIXTURES.parent / "remediation"
TOOL_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = TOOL_ROOT.parents[1]

# The registry these tests read is the SHIPPED one, so it is resolved through the shared workspace
# resolver rather than joined onto a repository root: the split moved `data/seed/**` into a gk-data
# pack, and `REPO_ROOT / "data" / "seed" / ...` names a path no repository carries.
#
# NOT CIRCULAR, AND THE ASSERTION BELOW IS WHY. Resolving with the tool's own helper would let these
# tests follow it anywhere: a resolver pointed at any parseable registry would keep them green while
# the release gate read something else. `SHIPPED_REGISTRY_REPO` therefore names the repository the
# resolver CHOSE, and the guard demands it be the content pack - an independent fact about the
# workspace that the resolver cannot satisfy by being wrong in the tool's favour.
SHIPPED_REGISTRY_RELPATH = "data/seed/ip-censor/_registry"
SHIPPED_REGISTRY = owned_dir(SHIPPED_REGISTRY_RELPATH, TOOL_ROOT)
SHIPPED_REGISTRY_REPO = owned_path(f"{SHIPPED_REGISTRY_RELPATH}/{MARKS_FILE}", TOOL_ROOT).parents[1]


def valid_files() -> dict[str, str]:
    return {name: (FIXTURES / name).read_text(encoding="utf-8") for name in REGISTRY_FILES}


def mutate(files: dict[str, str], name: str, change: Callable[[Any], None]) -> dict[str, str]:
    out = dict(files)
    document = json.loads(out[name])
    change(document)
    out[name] = json.dumps(document, indent=2)
    return out


def rejects(
    files: dict[str, str], name: str, change: Callable[[Any], None], *expected: str
) -> None:
    with pytest.raises(RegistryError) as info:
        parse_registry(mutate(files, name, change))
    message = str(info.value)
    assert name in message, message
    for fragment in expected:
        assert fragment in message, message


# ---- the happy path -------------------------------------------------------------


def test_parses_the_four_authored_files() -> None:
    registry = parse_registry(valid_files())

    assert [group.mark for group in registry.groups] == ["examplemark", "zenith-blade"]
    first = registry.groups[0]
    assert first.category == "franchise-mark"
    assert first.scope == frozenset({"player-name", "player-prose"})
    assert first.remediation == "authored"
    assert first.admission.stage == "reconfirm"
    assert first.admission.evidence == "census"
    assert first.admission.confirmed_by == "fixture-owner"
    assert first.admission.reconfirmed_on is None

    second = registry.groups[1]
    assert second.category == "company-brand"
    assert second.admission.stage == "import"
    assert second.admission.evidence == "dataset"
    assert second.admission.reconfirmed_on == "2026-09-20"

    assert registry.boundary.min_alias_length == 4
    assert registry.boundary.script_change_breaks is True
    assert registry.boundary.separators[0] == " "
    assert registry.replacements == {
        "examplemark": "fusionmark",
        "example mark": "the garden keeper",
    }


def test_alias_effective_scope_prefers_its_own() -> None:
    group = parse_registry(valid_files()).groups[0]
    by_text = {alias.text: alias for alias in group.aliases}

    assert by_text["exm"].scope == frozenset({"player-name"})
    assert group.effective_scope(by_text["exm"]) == frozenset({"player-name"})
    assert by_text["examplemark"].scope is None
    assert group.effective_scope(by_text["examplemark"]) == group.scope


def test_enforcement_needs_both_the_surface_and_the_marks_scope() -> None:
    registry = parse_registry(valid_files())

    # `examplemark` is scoped to the player surfaces, so a hit in a brief (generator-prompt, an
    # enforced surface) is report-only: "in scope on player-facing surfaces only" (IC-1b).
    assert registry.enforces(mark="examplemark", matched="Examplemark", surface="player-prose")
    assert not registry.enforces(
        mark="examplemark", matched="Examplemark", surface="generator-prompt"
    )
    # `zenith-blade` is scoped to generator-prompt; an alias-level scope narrows further (IC-6).
    assert registry.enforces(mark="zenith-blade", matched="zenith blade", surface="generator-prompt")
    assert not registry.enforces(
        mark="zenith-blade", matched="zenith blade", surface="player-prose"
    )
    # A mark the registry no longer carries never falls through to "enforced".
    assert registry.scope_for("no-such-mark", "no such mark") == frozenset()
    assert not registry.enforces(mark="no-such-mark", matched="x", surface="player-prose")


def test_scope_for_prefers_the_aliases_own_scope() -> None:
    registry = parse_registry(valid_files())

    assert registry.scope_for("examplemark", "exm") == frozenset({"player-name"})
    assert registry.scope_for("examplemark", "examplemark") == frozenset(
        {"player-name", "player-prose"}
    )
    # An unrecognised spelling falls back to its mark's group scope, never to everything.
    assert registry.scope_for("examplemark", "Examplemark II") == frozenset(
        {"player-name", "player-prose"}
    )


def test_self_paths_are_the_three_authored_classes_in_order() -> None:
    registry = parse_registry(valid_files())

    assert registry.self_paths[0] == "data/seed/ip-censor/**"
    assert "docs/architecture/ip-censor/**" in registry.self_paths
    assert "tasks/ip-censor-plan.md" in registry.self_paths
    assert registry.self_paths[-1] == "tasks/ip-censor/**"
    assert registry.scope.self_paths == registry.self_paths


def test_the_enforced_surface_set_is_parsed() -> None:
    registry = parse_registry(valid_files())
    assert registry.scope.enforced_surfaces == frozenset(
        {"player-name", "player-prose", "generator-prompt"}
    )
    assert {rule.surface for rule in registry.scope.rules} <= SURFACES
    assert any(
        rule.surface == "code-identifier" and rule.paths == ("src/**", "tests/**", "tools/**")
        for rule in registry.scope.rules
    )


def test_parsing_twice_is_identical() -> None:
    assert parse_registry(valid_files()) == parse_registry(valid_files())


# ---- closed vocabularies --------------------------------------------------------


def test_the_closed_vocabularies_are_the_declared_sets() -> None:
    assert CATEGORIES == {"franchise-mark", "real-person", "company-brand"}
    assert "title" not in CATEGORIES  # IC-1: titles are out
    assert SURFACES == {
        "player-name",
        "player-prose",
        "generator-prompt",
        "docs-prose",
        "code-identifier",
        "registry",
    }
    assert REMEDIATIONS == {"authored", "generator-owned", "upstream-imported", "code-change"}
    assert STAGES == {"import", "reconfirm"}
    assert EVIDENCES == {"dataset", "census", "model-proposal"}
    assert ADMISSION_EVIDENCE["import"] == frozenset({"dataset"})
    assert ADMISSION_EVIDENCE["reconfirm"] == frozenset({"census", "model-proposal"})


def test_the_fixture_only_uses_vocabulary_members() -> None:
    registry = parse_registry(valid_files())
    for group in registry.groups:
        assert group.category in CATEGORIES
        assert group.remediation in REMEDIATIONS
        assert group.scope <= SURFACES
        assert group.admission.stage in STAGES
        assert group.admission.evidence in EVIDENCES


# ---- marks rejections -----------------------------------------------------------


def test_a_title_category_is_rejected() -> None:
    files = valid_files()
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0].__setitem__("category", "title"),
        "category",
        "title",
    )


def test_missing_and_empty_fields_are_rejected() -> None:
    files = valid_files()
    rejects(files, MARKS_FILE, lambda doc: doc.pop("groups"), "groups", "missing")
    rejects(files, MARKS_FILE, lambda doc: doc.__setitem__("groups", []), "groups", "empty")
    rejects(
        files, MARKS_FILE, lambda doc: doc["groups"][0].pop("category"), "category", "missing"
    )
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0].__setitem__("category", ""),
        "category",
        "non-empty string",
    )
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0].__setitem__("scope", "player-name"),
        "scope",
        "must be a non-empty array",
    )
    rejects(
        files, MARKS_FILE, lambda doc: doc["groups"][0].pop("aliases"), "aliases", "missing"
    )
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0]["aliases"][0].pop("text"),
        "aliases[0]",
        "text",
    )


def test_unknown_remediation_and_surface_are_rejected() -> None:
    files = valid_files()
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0].__setitem__("remediation", "hand-edit"),
        "remediation",
    )
    rejects(
        files,
        MARKS_FILE,
        lambda doc: doc["groups"][0].__setitem__("scope", ["player-prose", "title"]),
        "scope[1]",
        "surface",
    )


def test_a_duplicate_mark_is_rejected() -> None:
    def change(doc: Any) -> None:
        doc["groups"][1]["mark"] = "examplemark"
        doc["groups"][1]["aliases"].append({"text": "examplemark"})

    rejects(valid_files(), MARKS_FILE, change, "mark", "already declared")


def test_a_spelling_under_two_groups_is_rejected() -> None:
    def change(doc: Any) -> None:
        doc["groups"][1]["aliases"].append({"text": "ExampleMark"})

    rejects(valid_files(), MARKS_FILE, change, "aliases", "already declared")


def test_the_canonical_mark_must_be_one_of_its_spellings() -> None:
    def change(doc: Any) -> None:
        doc["groups"][0]["aliases"] = [{"text": "example mark"}]

    rejects(valid_files(), MARKS_FILE, change, "aliases", "canonical mark")


# ---- IC-6 short aliases ---------------------------------------------------------


def test_a_short_alias_without_its_own_scope_is_rejected() -> None:
    def change(doc: Any) -> None:
        doc["groups"][0]["aliases"][1].pop("scope")

    rejects(valid_files(), MARKS_FILE, change, "aliases[1]", "minAliasLength", "IC-6")


def test_a_short_alias_with_a_subset_scope_is_accepted() -> None:
    registry = parse_registry(valid_files())
    alias = registry.groups[0].aliases[1]
    assert alias.text == "exm"
    assert alias.scope == frozenset({"player-name"})


def test_an_alias_scope_wider_than_its_group_is_rejected() -> None:
    def change(doc: Any) -> None:
        doc["groups"][0]["aliases"][1]["scope"] = ["player-name", "generator-prompt"]

    rejects(valid_files(), MARKS_FILE, change, "aliases[1]", "wider")


def test_the_minimum_length_comes_from_the_boundary_policy_not_from_code() -> None:
    # Raise the floor to 8 AND strip the short alias's own scope: only then is the alias unlawful,
    # and the message must quote the floor the DATA declares, not a constant in the parser.
    files = valid_files()
    boundary = json.loads(files["boundary-policy.v1.json"])
    boundary["minAliasLength"] = 8
    files["boundary-policy.v1.json"] = json.dumps(boundary)
    documents = json.loads(files[MARKS_FILE])
    del documents["groups"][0]["aliases"][1]["scope"]
    files[MARKS_FILE] = json.dumps(documents)

    with pytest.raises(RegistryError) as info:
        parse_registry(files)
    assert "minAliasLength 8" in str(info.value)


# ---- IC-2 admission -------------------------------------------------------------


def test_a_group_without_admission_is_rejected() -> None:
    rejects(valid_files(), MARKS_FILE, lambda doc: doc["groups"][0].pop("admission"), "admission")


def test_import_with_census_evidence_is_rejected() -> None:
    def change(doc: Any) -> None:
        doc["groups"][1]["admission"]["evidence"] = "census"

    rejects(valid_files(), MARKS_FILE, change, "admission.evidence", "import")


def test_admission_without_a_confirming_person_is_rejected() -> None:
    rejects(
        valid_files(),
        MARKS_FILE,
        lambda doc: doc["groups"][0]["admission"].pop("confirmedBy"),
        "admission",
        "confirmedBy",
    )
    rejects(
        valid_files(),
        MARKS_FILE,
        lambda doc: doc["groups"][0]["admission"].__setitem__("confirmedOn", "  "),
        "admission",
        "confirmedOn",
    )


def test_unknown_stage_and_evidence_are_rejected() -> None:
    rejects(
        valid_files(),
        MARKS_FILE,
        lambda doc: doc["groups"][0]["admission"].__setitem__("stage", "guessed"),
        "admission.stage",
    )
    rejects(
        valid_files(),
        MARKS_FILE,
        lambda doc: doc["groups"][0]["admission"].__setitem__("evidence", "guess"),
        "admission.evidence",
    )


# ---- boundary policy ------------------------------------------------------------


def test_a_boundary_policy_with_no_real_separator_is_rejected() -> None:
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.__setitem__("separators", [r"\b"]),
        "separators",
        "no separator",
    )
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.__setitem__("separators", []),
        "separators",
        "empty",
    )


def test_boundary_policy_mistyped_fields_are_rejected() -> None:
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.pop("scriptChangeBreaks"),
        "scriptChangeBreaks",
    )
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.__setitem__("scriptChangeBreaks", "yes"),
        "scriptChangeBreaks",
        "boolean",
    )
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.__setitem__("minAliasLength", "4"),
        "minAliasLength",
        "integer",
    )
    rejects(
        valid_files(),
        "boundary-policy.v1.json",
        lambda doc: doc.__setitem__("minAliasLength", 0),
        "minAliasLength",
    )


# ---- scope policy ---------------------------------------------------------------


def test_scope_policy_requires_every_self_path_class() -> None:
    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc["selfPaths"].pop("class2"),
        "selfPaths.class2",
        "missing",
    )
    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc.__setitem__("enforcedSurfaces", []),
        "enforcedSurfaces",
        "empty",
    )
    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc.__setitem__("pathRules", [{"surface": "player-prose"}]),
        "pathRules[0]",
        "paths",
    )
    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc["pathRules"][0].__setitem__("surface", "display"),
        "pathRules[0]",
        "surface",
    )


# ---- replacements ---------------------------------------------------------------


def test_replacements_require_an_authoring_person() -> None:
    rejects(
        valid_files(),
        REPLACEMENTS_FILE,
        lambda doc: doc["replacements"][0].pop("confirmedBy"),
        "replacements[0]",
        "confirmedBy",
    )
    rejects(
        valid_files(),
        REPLACEMENTS_FILE,
        lambda doc: doc["replacements"][0].__setitem__("replacement", ""),
        "replacement",
        "non-empty",
    )
    rejects(
        valid_files(),
        REPLACEMENTS_FILE,
        lambda doc: doc["replacements"].append(dict(doc["replacements"][0])),
        "replacements[2]",
        "already has a replacement",
    )


# ---- file-level shape -----------------------------------------------------------


def test_a_missing_file_is_rejected() -> None:
    files = valid_files()
    del files["marks.v1.json"]
    with pytest.raises(RegistryError, match="marks.v1.json"):
        parse_registry(files)


def test_schema_version_is_required_and_pinned() -> None:
    rejects(
        valid_files(), MARKS_FILE, lambda doc: doc.pop("schemaVersion"), "schemaVersion", "integer"
    )
    rejects(
        valid_files(),
        MARKS_FILE,
        lambda doc: doc.__setitem__("schemaVersion", 2),
        "schemaVersion",
        "must be 1",
    )


def test_invalid_json_and_non_object_documents_are_rejected() -> None:
    with pytest.raises(RegistryError, match="not valid JSON"):
        parse_marks("{", min_alias_length=4)
    with pytest.raises(RegistryError, match="top level must be an object"):
        parse_marks("[]", min_alias_length=4)
    with pytest.raises(RegistryError, match="top level must be an object"):
        parse_replacements('"x"')
    with pytest.raises(RegistryError, match="top level must be an object"):
        parse_scope_policy("null")
    with pytest.raises(RegistryError, match="top level must be an object"):
        parse_boundary_policy("3")


def test_each_file_can_be_parsed_on_its_own() -> None:
    files = valid_files()
    groups = parse_marks(files[MARKS_FILE], min_alias_length=4)
    assert len(groups) == 2
    boundary = parse_boundary_policy(files["boundary-policy.v1.json"])
    assert boundary.min_alias_length == 4
    scope = parse_scope_policy(files["scope-policy.v1.json"])
    assert scope.self_paths
    replacements = parse_replacements(files[REPLACEMENTS_FILE])
    assert list(replacements) == ["examplemark", "example mark"]


def test_the_scope_policy_declares_a_default_surface() -> None:
    scope = parse_scope_policy(valid_files()["scope-policy.v1.json"])
    assert scope.default_surface == "code-identifier"
    assert scope.default_surface in SURFACES

    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc.pop("defaultSurface"),
        "defaultSurface",
        "missing",
    )
    rejects(
        valid_files(),
        "scope-policy.v1.json",
        lambda doc: doc.__setitem__("defaultSurface", "display"),
        "defaultSurface",
        "surface",
    )


# ---- surface classifier (A10) ---------------------------------------------------


def test_surface_for_uses_the_first_matching_authored_rule() -> None:
    registry = parse_registry(valid_files())
    policy = registry.scope

    assert surface_for("src/FusionRpg.Core/World/WorldTemplateCatalog.cs", policy) == (
        "code-identifier"
    )
    assert surface_for("tools/seedsmith/seedsmith/report/cli.py", policy) == "generator-prompt"
    assert surface_for("data/seed/narrative/arc-a.json", policy) == "player-prose"
    assert surface_for("docs/guide/start.md", policy) == "player-prose"
    assert surface_for("docs/architecture/thing.md", policy) == "docs-prose"
    assert surface_for("data/seed/items/_registry/kinds.json", policy) == "registry"
    assert surface_for("a/b/c.po", policy) == "player-prose"
    # A path no rule names falls to the authored default, never to an enforced surface.
    assert surface_for("unknown/root-file.bin", policy) == "code-identifier"
    assert not is_enforced(surface_for("unknown/root-file.bin", policy), policy)


def test_the_identity_rename_allow_list_is_never_enforced() -> None:
    # Plan D10: identity-rename keeps these identifiers on purpose, so the gate must not enforce them.
    registry = parse_registry(valid_files())
    allow_listed_paths = (
        "src/FusionRpg.Core/World/WorldTemplateCatalog.cs",
        "src/FusionRpg.Core/Empires/EmpireId.cs",
        "src/FusionRpg.Server/ZombossDeployEndpoints.cs",
        "web/fusion-rpg-web/src/lib/commander.ts",
        "src/FusionRpg.Server/DropPvzRun.cs",
        "src/FusionRpg.Injector/PVZRH.cs",
    )
    for path in allow_listed_paths:
        surface = registry.surface_for(path)
        assert surface == "code-identifier", path
        assert not registry.is_enforced(surface), path


def test_every_tracked_path_gets_a_surface() -> None:
    registry = parse_registry(valid_files())
    paths = tracked_paths(TOOL_ROOT.parents[1])

    assert paths, "no tracked paths to classify"
    for path in paths:
        assert registry.surface_for(path) in SURFACES, path


# ---- remediation derivation (A3) ------------------------------------------------


@pytest.mark.parametrize(
    "fixture_name",
    [
        "top-level-meta.json",
        "top-level-provenance.json",
        "top-level-provenance-with-rows.json",
        "per-row-provenance.json",
        "per-row-prompt-version.json",
    ],
)
def test_each_provenance_shape_is_generator_owned(fixture_name: str) -> None:
    document = json.loads((REMEDIATION_FIXTURES / fixture_name).read_text(encoding="utf-8"))
    assert carries_provenance(document), fixture_name
    assert (
        classify_remediation(
            path="data/generated/fixture/row.json",
            surface="registry",
            document=document,
        )
        == "generator-owned"
    )


def test_the_same_file_without_provenance_is_authored() -> None:
    document = json.loads((REMEDIATION_FIXTURES / "no-provenance.json").read_text(encoding="utf-8"))
    assert not carries_provenance(document)
    assert (
        classify_remediation(
            path="data/seed/creatures/species/zombie/undead.json",
            surface="registry",
            document=document,
        )
        == "authored"
    )
    assert (
        classify_remediation(
            path="data/generated/fixture/row.json", surface="registry", document=None
        )
        == "authored"
    )


def test_the_fan_pack_root_is_upstream_imported() -> None:
    document = json.loads(
        (REMEDIATION_FIXTURES / "top-level-meta.json").read_text(encoding="utf-8")
    )
    assert EXTERNAL_REFERENCE_ROOT == "data/seed/external-reference/**"
    assert (
        classify_remediation(
            path="data/seed/external-reference/almanac-enrichment/pack.json",
            surface="registry",
            document=document,
        )
        == "upstream-imported"
    )


def test_an_identifier_is_a_code_change_not_a_content_edit() -> None:
    assert (
        classify_remediation(
            path="src/FusionRpg.Core/World/WorldTemplateCatalog.cs",
            surface="code-identifier",
            document=None,
        )
        == "code-change"
    )
    assert (
        classify_remediation(path="README.md", surface="docs-prose", document=None)
        == "authored"
    )


def test_remediation_is_always_a_closed_vocabulary_member() -> None:
    registry = parse_registry(valid_files())
    for path in tracked_paths(TOOL_ROOT.parents[1]):
        surface = registry.surface_for(path)
        assert (
            classify_remediation(path=path, surface=surface, document=None) in REMEDIATIONS
        ), path


def test_the_registry_loads_from_a_directory() -> None:
    registry = load_registry(FIXTURES)
    assert [group.mark for group in registry.groups] == ["examplemark", "zenith-blade"]

    with pytest.raises(RegistryError, match="cannot be read"):
        load_registry(FIXTURES / "does-not-exist")


# ---- the shipped registry (T4 part 2) -------------------------------------------
#
# These tests read `gk-data/packs/fusion/data/seed/ip-censor/_registry/`, the authored registry the release gate runs on.
# They pin the CLOSED vocabulary — which marks exist, which scopes, which replacements, which
# surfaces are enforced — and never a hit population: how many occurrences those marks produce over
# the real tree is a reading (validation-ssot.md).


def test_the_shipped_registry_lives_in_the_content_pack_and_not_in_the_scanned_repository() -> None:
    # The precondition every other shipped-registry test here silently depends on. They all read
    # `SHIPPED_REGISTRY`, so a resolver that returned a directory that is NOT the shipped registry
    # would keep them green while proving nothing about what the release gate reads.
    assert (SHIPPED_REGISTRY / MARKS_FILE).is_file(), SHIPPED_REGISTRY

    # It is a gk-data PACK, and the pack is what the resolver returns - `gk-data/packs/fusion`, whose
    # basename is the pack name. A resolver that matched the literal prefix `data/seed` finds nothing
    # under gk-data and silently answers "the scanned repository", which is the bug this test pins.
    assert "gk-data" in SHIPPED_REGISTRY_REPO.parts, SHIPPED_REGISTRY_REPO
    assert SHIPPED_REGISTRY_REPO != REPO_ROOT
    assert not (REPO_ROOT / "data" / "seed").exists(), (
        "the scanned repository has a data/seed again, so the shipped registry is ambiguous: the "
        "resolver would answer with whichever root came first in repo_bases()"
    )


def test_the_shipped_registry_parses_and_is_byte_stable() -> None:
    first = load_registry(SHIPPED_REGISTRY)
    second = load_registry(SHIPPED_REGISTRY)

    assert first == second
    # `curate admit` writes through `render_marks`; the shipped file must already be that exact text,
    # or the first admit would reformat it and the diff would hide the row it added.
    shipped_text = (SHIPPED_REGISTRY / MARKS_FILE).read_text(encoding="utf-8")
    assert render_marks(first.groups) == shipped_text


def test_the_shipped_registry_holds_exactly_the_day_one_groups() -> None:
    # The member list is a closed vocabulary a person edits (plan D5): the marks the owner confirmed
    # on 2026-09-19. Every other mark enters through `curate` with a person's decision, so a new row
    # here is a reviewed act, not a generator output — this test is what makes that visible.
    registry = load_registry(SHIPPED_REGISTRY)

    assert [group.mark for group in registry.groups] == [
        "crazy-dave",
        "dr-zomboss",
        "overwatch",
        "penny",
        "pvz",
    ]
    assert {group.category for group in registry.groups} == {"franchise-mark"}

    by_mark = {group.mark: group for group in registry.groups}
    assert by_mark["pvz"].scope == frozenset({"player-name", "player-prose"})
    assert by_mark["overwatch"].scope == frozenset(
        {"player-name", "player-prose", "generator-prompt"}
    )
    assert by_mark["overwatch"].remediation == "generator-owned"
    for mark in ("crazy-dave", "dr-zomboss", "penny", "pvz"):
        assert by_mark[mark].remediation == "authored", mark

    # IC-6: the 3-character `pvz` alias carries its own scope, and it is a subset of its group's.
    pvz_alias = {alias.text: alias for alias in by_mark["pvz"].aliases}["pvz"]
    assert pvz_alias.scope == frozenset({"player-name", "player-prose"})

    for group in registry.groups:
        assert group.admission.stage == "reconfirm", group.mark
        assert group.admission.evidence == "census", group.mark
        assert group.admission.confirmed_by == "owner", group.mark
        assert group.admission.confirmed_on == "2026-09-19", group.mark
        assert group.admission.source, group.mark


def test_the_shipped_replacements_are_the_owner_authored_pairs() -> None:
    registry = load_registry(SHIPPED_REGISTRY)

    assert registry.replacements == {
        "pvz": "Fusion",
        "Plants vs. Zombies": "Fusion",
        "Plants vs Zombies": "Fusion",
        "Crazy Dave": "the Garden Keeper",
        "Penny": "Hourbloom",
        "Dr. Zomboss": "the Rotwright",
    }


def test_the_shipped_boundary_policy_is_the_owner_ruled_floor() -> None:
    boundary = load_registry(SHIPPED_REGISTRY).boundary

    # IC-6's value lives in data, not in code; 4 is the owner ruling this file records.
    assert boundary.min_alias_length == 4
    assert boundary.script_change_breaks is True
    # The measured `\b` failures (spec-scan §Testing Strategy): `-` and `.` must be separators.
    for separator in (" ", "-", ".", "/", "\t"):
        assert separator in boundary.separators, repr(separator)


def test_the_shipped_scope_policy_carries_the_enforced_set_and_self_paths() -> None:
    scope = load_registry(SHIPPED_REGISTRY).scope

    assert scope.enforced_surfaces == frozenset(
        {"player-name", "player-prose", "generator-prompt"}
    )
    assert scope.default_surface == "code-identifier"
    assert not is_enforced(scope.default_surface, scope)

    rules = {rule.surface: set(rule.paths) for rule in scope.rules}
    assert "data/seed/narrative/**" in rules["player-prose"]
    assert "docs/guide/**" in rules["player-prose"]
    assert "tools/seedsmith/**" in rules["generator-prompt"]
    assert "data/seed/**/_registry/**" in rules["registry"]

    assert scope.self_paths[0] == "data/seed/ip-censor/**"
    assert "docs/architecture/ip-censor/**" in scope.self_paths
    assert "tasks/ip-censor-plan.md" in scope.self_paths
    assert "tasks/ip-censor-todo.md" in scope.self_paths
    assert scope.self_paths[-1] == "tasks/ip-censor/**"


def test_the_shipped_classifier_enforces_player_surfaces_and_not_identifiers() -> None:
    registry = load_registry(SHIPPED_REGISTRY)

    enforced = {
        # The IC-4.1 collision: a shipped player-facing node name in a generated tree.
        "data/seed/passive-tree/nodes/command.json": "player-name",
        # A display registry the map names as a player-name surface (map, identity-rename row).
        "data/seed/commanders/_registry/default-commanders.v1.json": "player-name",
        "data/seed/narrative/_registry/names.en.v1.json": "player-name",
        "docs/guide/the-game.md": "player-prose",
        "web/fusion-rpg-web/src/i18n/locales/en/messages.po": "player-prose",
        "data/seed/narrative/arc-a.json": "player-prose",
        "tools/seedsmith/seedsmith/adapters/items/uniques/briefs.py": "generator-prompt",
    }
    for path, surface in enforced.items():
        assert registry.surface_for(path) == surface, path
        assert registry.is_enforced(surface), path

    report_only = {
        "src/FusionRpg.Core/World/WorldTemplateCatalog.cs": "code-identifier",
        "web/fusion-rpg-web/src/lib/commander.ts": "code-identifier",
        "tasks/ip-censor-plan.md": "code-identifier",
        "data/seed/items/_registry/words.v1.json": "registry",
        "docs/architecture/software-architecture.md": "docs-prose",
        "docs/research/action-taxonomy/05-support-healing-actions.md": "docs-prose",
        "unknown/root-file.bin": "code-identifier",
    }
    for path, surface in report_only.items():
        assert registry.surface_for(path) == surface, path
        assert not registry.is_enforced(surface), path


def test_every_tracked_path_gets_a_surface_under_the_shipped_registry() -> None:
    registry = load_registry(SHIPPED_REGISTRY)
    paths = tracked_paths(REPO_ROOT)

    assert paths, "no tracked paths to classify"
    for path in paths:
        assert registry.surface_for(path) in SURFACES, path
        assert (
            classify_remediation(path=path, surface=registry.surface_for(path), document=None)
            in REMEDIATIONS
        ), path


def test_the_shipped_registry_enforces_only_inside_each_marks_scope() -> None:
    # CP3's owner audit, mechanised: the gate must not enforce a mark on a surface the mark's own
    # row does not name. `pvz`/`dr-zomboss` are display marks (IC-1b, R8/R9); `overwatch` is the
    # generator-prompt mark (IC-4.1). Without this, every `zomboss` identifier in a seedsmith code
    # comment would block a release while owning no fix.
    registry = load_registry(SHIPPED_REGISTRY)

    assert registry.enforces(mark="dr-zomboss", matched="Dr. Zomboss", surface="player-name")
    assert registry.enforces(mark="pvz", matched="PvZ", surface="player-prose")
    assert not registry.enforces(mark="dr-zomboss", matched="zomboss", surface="generator-prompt")
    assert not registry.enforces(mark="pvz", matched="PvZ", surface="generator-prompt")
    assert not registry.enforces(mark="pvz", matched="PvZ", surface="code-identifier")
    assert registry.enforces(mark="overwatch", matched="Overwatch", surface="generator-prompt")
    assert registry.enforces(mark="overwatch", matched="Overwatch", surface="player-name")


def test_the_shipped_registry_self_excludes_its_own_files() -> None:
    registry = load_registry(SHIPPED_REGISTRY)
    scope = registry.scope

    assert scope.self_paths == registry.self_paths
    # One representative path per authored class (spec-registry.md §self_paths): the registry files,
    # this program's own docs, and the plan home that also holds `curate`'s candidate files.
    assert any(
        matches_any("data/seed/ip-censor/_registry/marks.v1.json", (path,))
        for path in scope.self_paths
    )
    assert any(
        matches_any("docs/architecture/ip-censor/spec-registry.md", (path,))
        for path in scope.self_paths
    )
    assert any(
        matches_any("tasks/ip-censor/curate/round-1.json", (path,)) for path in scope.self_paths
    )


# ---- the IC-4.2 import-time rename map (T18, T19b) ------------------------------
#
# Fixtures use invented names only: the real map's keys ARE the real names, so it lives in
# `self_paths` class 1 and a fixture cannot be a copy of it.


def import_renames_text() -> str:
    # Beside `valid/` rather than inside it: `valid/` is the four-file blueprint `valid_files()`
    # loads, and this map is a fifth authored artefact with its own shape.
    return (FIXTURES.parent / IMPORT_RENAMES_FILE).read_text(encoding="utf-8")


def rename_document() -> Any:
    return json.loads(import_renames_text())


def rejects_renames(change: Callable[[Any], None], *expected: str) -> None:
    document = rename_document()
    change(document)
    with pytest.raises(RegistryError) as info:
        parse_import_renames(json.dumps(document), source=IMPORT_RENAMES_FILE)
    message = str(info.value)
    assert IMPORT_RENAMES_FILE in message, message
    for fragment in expected:
        assert fragment in message, message


def test_the_import_rename_map_keeps_every_pairs_provenance() -> None:
    renames = parse_import_renames(import_renames_text())

    assert renames.name_map() == {
        "Examplemark Dragon": "Fusion Drake",
        "Zenith Blade": "Pinnacle Edge",
    }
    assert renames.id_map() == {"Examplemark_a": "Fusionmark_a"}
    for pair in (*renames.names, *renames.ids):
        assert pair.confirmed_by == "fixture-owner", pair.match
        assert pair.confirmed_on == "2026-09-19", pair.match


def test_the_import_rename_map_loads_from_a_directory(tmp_path: Path) -> None:
    target = tmp_path / IMPORT_RENAMES_FILE
    target.write_text(import_renames_text(), encoding="utf-8")

    assert load_import_renames(tmp_path) == parse_import_renames(import_renames_text())
    with pytest.raises(RegistryError, match="cannot be read"):
        load_import_renames(tmp_path / "missing")


def test_an_id_section_may_be_empty_until_t19b_fills_it() -> None:
    # The `names` half lands with T18; T19b extends the `ids` half. A present-but-empty `ids` array
    # is the file's own intermediate state, not a missing key.
    document = rename_document()
    document["ids"] = []
    renames = parse_import_renames(json.dumps(document))
    assert renames.id_map() == {}
    assert renames.name_map()


def test_a_rename_pair_without_a_confirming_person_is_rejected() -> None:
    for section, key in (("names", "confirmedBy"), ("ids", "confirmedOn")):
        rejects_renames(
            lambda doc, section=section, key=key: doc[section][0].pop(key),
            f"{section}[0]",
            key,
        )
    rejects_renames(lambda doc: doc["names"][0].__setitem__("replacement", ""), "replacement", "non-empty")


def test_the_import_rename_map_requires_both_sections() -> None:
    rejects_renames(lambda doc: doc.pop("names"), "names", "missing")
    rejects_renames(lambda doc: doc.pop("ids"), "ids", "missing")
    rejects_renames(lambda doc: doc.__setitem__("schemaVersion", 2), "schemaVersion", "must be 1")


def test_an_ambiguous_rename_pair_is_rejected() -> None:
    # Two old ids collapsing onto one new id would make T19b's re-key ambiguous, and a pair that
    # maps a name to itself is a silent no-op.
    rejects_renames(
        lambda doc: doc["ids"].append(dict(doc["ids"][0], match="Examplemark_b")),
        "ids[1]",
        "already the replacement",
    )
    rejects_renames(
        lambda doc: doc["ids"].append(dict(doc["ids"][0], replacement="Fusionmark_b")),
        "ids[1]",
        "already has a replacement",
    )
    rejects_renames(
        lambda doc: doc["names"][0].__setitem__("replacement", "Examplemark Dragon"),
        "names[0]",
        "maps to itself",
    )
