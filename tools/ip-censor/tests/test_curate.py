"""Tests for `curate import` and the USPTO adapter (spec-curate.md §Testing Strategy).

Fixtures use invented marks only. Most of these tests read the fixture copy of the authored filter, so
the module is proven against the schema the shipped file must satisfy; the two
`test_the_shipped_filter_*` tests instead read the SHIPPED file, resolved through the shared workspace
resolver because the split moved `data/seed/**` into a gk-data pack.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable

import pytest

from ipcensor.census import TokenStat
from ipcensor.curate import (
    DEFAULT_CANDIDATE_DIR,
    DEFAULT_FILTER_PATH,
    FILTER_KEYS,
    SCHEMA_VERSION,
    Candidate,
    CurateError,
    admit,
    candidate_file_name,
    candidate_target,
    import_candidates,
    load_import_filter,
    parse_import_filter,
    read_and_import,
    reconfirm,
    render_candidates,
    write_candidates,
    write_marks,
)
from ipcensor.datasets import uspto
from ipcensor.registry import CATEGORIES, REGISTRY_FILES, parse_marks, parse_registry
from ipcensor.registry import render_marks
from ipcensor.roots import TOOL_ROOT, owned

TESTS_DIR = Path(__file__).resolve().parent
FIXTURES = TESTS_DIR / "fixtures" / "curate"
REGISTRY_FIXTURES = TESTS_DIR / "fixtures" / "registry" / "valid"
EXPORT = FIXTURES / "export.xml"


def filter_text() -> str:
    return (FIXTURES / "import-filter.v1.json").read_text(encoding="utf-8")


def fixture_filter():
    return parse_import_filter(filter_text())


def mutate(change: Callable[[dict[str, Any]], None]) -> str:
    document = json.loads(filter_text())
    change(document)
    return json.dumps(document, indent=2)


def rejects(change: Callable[[dict[str, Any]], None], *expected: str) -> None:
    with pytest.raises(CurateError) as info:
        parse_import_filter(mutate(change), source="import-filter.v1.json")
    message = str(info.value)
    for fragment in expected:
        assert fragment in message, message


# ---- the adapter ----------------------------------------------------------------


def test_the_adapter_reads_the_pinned_export() -> None:
    records = uspto.read_export(EXPORT)

    assert len(records) == 6
    first = records[0]
    assert first.record_id == "90000001"
    assert first.mark == "EXAMPLEMARK"
    assert first.status == "700"
    assert first.classes == ("28",)
    assert "game" in first.goods
    # Multi-class records are sorted and de-duplicated, so the candidate is deterministic.
    assert records[1].classes == tuple(sorted(records[1].classes))


def test_an_unknown_export_format_is_refused() -> None:
    with pytest.raises(uspto.UnknownExportFormat) as info:
        uspto.read_export(FIXTURES / "unknown-format.xml")
    message = str(info.value)
    assert "trademark-assignments" in message
    assert uspto.FORMAT_ID in message


def test_an_unreadable_export_is_refused(tmp_path: Path) -> None:
    with pytest.raises(uspto.UnknownExportFormat, match="not valid XML"):
        uspto.read_export(FIXTURES / "broken.xml")
    with pytest.raises(uspto.UnknownExportFormat, match="cannot be read"):
        uspto.read_export(tmp_path / "missing.xml")


# ---- the authored filter --------------------------------------------------------


def test_the_filter_parses_into_the_closed_shape() -> None:
    parsed = fixture_filter()

    assert parsed.format == uspto.FORMAT_ID
    assert parsed.category == "franchise-mark"
    assert parsed.international_classes == frozenset({"9", "28", "41"})
    assert parsed.status_codes == frozenset({"700"})
    assert parsed.goods_terms == ("game",)
    assert parsed.exclude_terms == ("chair", "furniture")
    assert parsed.min_mark_length == 4


@pytest.mark.parametrize("key", FILTER_KEYS)
def test_a_filter_missing_a_key_throws_naming_it(key: str) -> None:
    rejects(lambda document: document.pop(key), key, "missing")


def test_a_mistyped_filter_field_throws_naming_it() -> None:
    rejects(lambda document: document.__setitem__("schemaVersion", 2), "schemaVersion")
    rejects(lambda document: document.__setitem__("category", "title"), "category", "unknown")
    rejects(
        lambda document: document.__setitem__("internationalClasses", "9"),
        "internationalClasses",
        "array",
    )
    rejects(
        lambda document: document.__setitem__("goodsTerms", []),
        "goodsTerms",
        "non-empty",
    )
    rejects(
        lambda document: document.__setitem__("minMarkLength", "4"),
        "minMarkLength",
        "integer",
    )
    rejects(
        lambda document: document.__setitem__("format", "wipo-xml/v1"),
        "format",
        uspto.FORMAT_ID,
    )


def test_the_filter_can_be_loaded_from_a_path(tmp_path: Path) -> None:
    target = tmp_path / "import-filter.v1.json"
    target.write_text(filter_text(), encoding="utf-8")
    assert load_import_filter(target) == fixture_filter()

    with pytest.raises(CurateError, match="cannot be read"):
        load_import_filter(tmp_path / "missing.json")


def test_the_shipped_filter_path_resolves_to_the_shipped_filter() -> None:
    # The pair `curate.DEFAULT_FILTER_PATH` / its reader was the one hard-coded path in this module.
    # It was paired with the literal below and both had to move together; this asserts RESOLVED
    # BEHAVIOUR instead, because the literal alone proved nothing - it stayed true while every read of
    # the file failed. What has to hold is that the constant the CLI passes names the real shipped
    # filter, read the same way the CLI reads it.
    assert DEFAULT_FILTER_PATH == "data/seed/ip-censor/_registry/import-filter.v1.json", (
        "the constant is repository-relative DATA, not just a path: scope-policy.v1.json matches "
        "data/seed/**/_registry/** against it and marks.v1.json lists data/seed/ip-censor/** as a "
        "self path, so a resolved or absolute spelling here would stop the registry describing itself"
    )

    resolved = owned(DEFAULT_FILTER_PATH, TOOL_ROOT)

    assert resolved.is_file(), resolved
    # Resolved through the same call the CLI makes, so this is the reader under test rather than a
    # restatement of the constant.
    assert load_import_filter() == load_import_filter(resolved)
    assert load_import_filter().format == uspto.FORMAT_ID


def test_the_shipped_filter_parses_and_pins_the_adapters_format() -> None:
    # The authored filter the tool ships (spec-curate.md §Project Structure). It is data, not code:
    # this test proves the shipped file satisfies the schema the module enforces and that its format
    # still names the adapter's own pin. Its Nice classes, status codes and goods terms are tuned
    # against real candidate output at T21; the filter's shape is what is pinned here.
    shipped = load_import_filter()

    assert shipped.format == uspto.FORMAT_ID
    assert shipped.category in CATEGORIES
    assert shipped.min_mark_length >= 1
    assert shipped.international_classes
    assert shipped.status_codes
    assert shipped.goods_terms
    assert shipped.exclude_terms


# ---- import ---------------------------------------------------------------------


def test_import_applies_only_the_authored_filter() -> None:
    candidates = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")

    # Kept: the two game-related live marks. Dropped: the non-game class, the dead status, the
    # excluded goods term, and the mark shorter than minMarkLength.
    assert [candidate.mark for candidate in candidates] == ["EXAMPLEMARK", "Zenith Blade"]


def test_every_candidate_carries_its_dataset_evidence_and_no_decision() -> None:
    candidates = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")

    assert candidates
    for candidate in candidates:
        assert candidate.stage == "import"
        assert candidate.evidence == "dataset"
        assert candidate.category == "franchise-mark"
        assert candidate.aliases == (candidate.mark,)
        assert candidate.source.startswith("uspto:2026-09-19:900000")
        assert candidate.decision is None
        assert candidate.confirmed_by is None
        assert candidate.scope is None


def test_the_filter_decides_by_status_and_subject_matter() -> None:
    parsed = fixture_filter()
    records = uspto.read_export(EXPORT)

    kept = import_candidates(records, parsed, dataset_version="v1")
    by_mark = {candidate.mark: candidate for candidate in kept}
    assert set(by_mark) == {"EXAMPLEMARK", "Zenith Blade"}

    # A dead status alone is enough to drop a record, however game-related its goods text is.
    assert "Abandonedmark" not in by_mark
    # An exclude term anywhere in mark or goods text drops it, even with a matching class.
    assert "Gaming Chair" not in by_mark
    # The class/goods pair must match: clothing under class 25 is not game-related.
    assert "Fixturemark" not in by_mark
    # Short marks are the primary false-positive source (IC-6), so the filter floors them too.
    assert "Exm" not in by_mark


def test_import_is_deterministic() -> None:
    first = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")
    second = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")
    assert first == second
    assert render_candidates(first, dataset_version="2026-09-19") == render_candidates(
        second, dataset_version="2026-09-19"
    )


def test_an_empty_filter_result_is_not_an_error() -> None:
    def narrow(document: dict[str, Any]) -> None:
        # No record matches class 41, and no goods text carries the term: the subject-matter pair is
        # what identifies game-relatedness, so clearing both sides empties the candidate set.
        document["internationalClasses"] = ["41"]
        document["goodsTerms"] = ["no-such-term"]

    strict = parse_import_filter(mutate(narrow))
    assert read_and_import(EXPORT, strict, dataset_version="v1") == ()


# ---- candidate files ------------------------------------------------------------


def test_candidate_files_go_under_the_programs_evidence_home() -> None:
    assert DEFAULT_CANDIDATE_DIR == "tasks/ip-censor/curate"
    assert candidate_target("uspto-2026-09-19.json") == Path(
        "tasks/ip-censor/curate/uspto-2026-09-19.json"
    )
    assert candidate_file_name(dataset_id="uspto", when="2026-09-19") == "uspto-2026-09-19.json"


def test_writing_a_candidate_file_is_byte_stable(tmp_path: Path) -> None:
    candidates = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")

    first = write_candidates(
        candidates,
        name="uspto-2026-09-19.json",
        directory=tmp_path,
        dataset_version="2026-09-19",
    )
    first_bytes = first.read_bytes()
    second = write_candidates(
        candidates,
        name="uspto-2026-09-19.json",
        directory=tmp_path,
        dataset_version="2026-09-19",
    )

    assert second.read_bytes() == first_bytes
    assert first_bytes.endswith(b"\n")
    assert b"\r\n" not in first_bytes

    document = json.loads(first.read_text(encoding="utf-8"))
    assert document["schemaVersion"] == SCHEMA_VERSION
    assert document["dataset"] == "uspto"
    assert document["datasetVersion"] == "2026-09-19"
    assert [row["mark"] for row in document["candidates"]] == ["EXAMPLEMARK", "Zenith Blade"]


def test_the_candidate_body_has_no_timestamp() -> None:
    candidates = read_and_import(EXPORT, fixture_filter(), dataset_version="2026-09-19")
    document = json.loads(render_candidates(candidates, dataset_version="2026-09-19"))
    assert "generatedAt" not in document
    assert "generated_at" not in document


def test_a_candidate_renders_every_declared_field() -> None:
    row = Candidate(
        mark="Examplemark",
        aliases=("Examplemark", "Example Mark"),
        category="franchise-mark",
        stage="import",
        evidence="dataset",
        source="uspto:v1:1",
    ).as_dict()
    assert set(row) == {
        "mark",
        "aliases",
        "category",
        "stage",
        "evidence",
        "source",
        "decision",
        "confirmedBy",
        "confirmedOn",
        "scope",
        "remediation",
        "recheck",
    }
    assert row["decision"] is None
    assert row["confirmedBy"] is None
    assert row["confirmedOn"] is None
    assert row["scope"] is None
    assert row["remediation"] is None
    assert row["recheck"] is False


# ---- reconfirm ------------------------------------------------------------------


def stat(
    token: str,
    *,
    display: str | None = None,
    total: int = 3,
    surfaces: dict[str, int] | None = None,
) -> TokenStat:
    by_surface = surfaces if surfaces is not None else {"player-prose": total}
    return TokenStat(
        token=token,
        display=display or token,
        total=total,
        by_tree={"docs": total},
        by_surface=by_surface,
    )


def registry_for_curate():
    files = {
        name: (REGISTRY_FIXTURES / name).read_text(encoding="utf-8")
        for name in REGISTRY_FILES
    }
    return parse_registry(files)


def test_reconfirm_without_a_model_uses_census_evidence_only() -> None:
    result = reconfirm(
        registry_for_curate(),
        [
            stat("examplemark", display="Examplemark"),
            stat("newword", display="Newword"),
            stat("identifierish", surfaces={"code-identifier": 4}),
        ],
        as_of="2026-09-19",
    )

    by_mark = {candidate.mark: candidate for candidate in result.candidates}
    assert set(by_mark) == {"examplemark", "Newword"}
    # A token the registry knows is a re-check, not a new row.
    assert by_mark["examplemark"].recheck is True
    assert by_mark["examplemark"].evidence == "census"
    assert by_mark["examplemark"].source == "census:2026-09-19"
    # A new token on an enforced surface is a candidate; a code-identifier token is not.
    assert by_mark["Newword"].recheck is False
    assert by_mark["Newword"].category is None
    assert result.model_errors == ()
    assert all(candidate.evidence != "model-proposal" for candidate in result.candidates)


def test_reconfirm_with_a_stub_model_records_the_proposal_and_the_model() -> None:
    proposed: list[str] = []

    def proposer(candidate: Candidate) -> str:
        proposed.append(candidate.mark)
        return "a candidate"

    result = reconfirm(
        registry_for_curate(),
        [stat("examplemark", display="Examplemark"), stat("newword", display="Newword")],
        as_of="2026-09-19",
        propose=proposer,
        model="fixture-model",
    )

    by_mark = {candidate.mark: candidate for candidate in result.candidates}
    assert proposed == ["Newword"]  # a re-check is not proposed
    assert by_mark["Newword"].evidence == "model-proposal"
    assert by_mark["Newword"].source == "model-proposal:fixture-model:2026-09-19"
    assert by_mark["examplemark"].evidence == "census"


def test_a_raising_model_is_recorded_not_fatal() -> None:
    def proposer(candidate: Candidate) -> str:
        raise RuntimeError("endpoint refused the connection")

    result = reconfirm(
        registry_for_curate(),
        [stat("newword", display="Newword")],
        as_of="2026-09-19",
        propose=proposer,
        model="fixture-model",
    )

    assert len(result.model_errors) == 1
    assert "endpoint refused the connection" in result.model_errors[0]
    assert result.candidates[0].evidence == "census"


def test_reconfirm_is_deterministic() -> None:
    stats = [stat("newword", display="Newword"), stat("examplemark", display="Examplemark")]
    first = reconfirm(registry_for_curate(), stats, as_of="2026-09-19")
    second = reconfirm(registry_for_curate(), stats, as_of="2026-09-19")
    assert first == second


# ---- admit ----------------------------------------------------------------------


def accepted(mark: str, **overrides: Any) -> Candidate:
    values: dict[str, Any] = {
        "mark": mark,
        "aliases": (mark,),
        "category": "franchise-mark",
        "stage": "reconfirm",
        "evidence": "census",
        "source": "census:2026-09-19",
        "decision": "accept",
        "confirmed_by": "owner",
        "confirmed_on": "2026-09-19",
        "scope": frozenset({"player-name", "player-prose"}),
        "remediation": "authored",
    }
    values.update(overrides)
    return Candidate(**values)


@pytest.mark.parametrize(
    ("overrides", "reason"),
    [
        ({"decision": None}, "no decision"),
        ({"confirmed_by": None}, "no confirmedBy"),
        ({"scope": None}, "no scope"),
        ({"scope": frozenset()}, "no scope"),
        ({"category": None}, "no category"),
        ({"remediation": None}, "no remediation"),
        ({"scope": frozenset({"display"})}, "unknown surface"),
        ({"decision": "maybe"}, "unknown decision"),
    ],
)
def test_admit_refuses_a_row_a_person_did_not_complete(
    overrides: dict[str, Any], reason: str
) -> None:
    result = admit([accepted("Newword", **overrides)], registry_for_curate(), as_of="2026-09-20")

    assert result.admitted == ()
    assert len(result.refused) == 1
    assert result.refused[0][0] == "Newword"
    assert reason in result.refused[0][1]
    assert result.groups == registry_for_curate().groups


def test_admit_lands_an_accepted_row_with_its_admission_record() -> None:
    result = admit([accepted("Newword")], registry_for_curate(), as_of="2026-09-20")

    assert result.admitted == ("Newword",)
    assert result.refused == ()
    landed = next(group for group in result.groups if group.mark == "Newword")
    assert landed.admission.stage == "reconfirm"
    assert landed.admission.evidence == "census"
    assert landed.admission.source == "census:2026-09-19"
    assert landed.admission.confirmed_by == "owner"
    assert landed.admission.confirmed_on == "2026-09-19"
    assert landed.admission.reconfirmed_on is None
    assert landed.scope == frozenset({"player-name", "player-prose"})
    assert landed.remediation == "authored"
    # The written registry re-parses, and the parse equals what admit produced.
    assert parse_marks(result.render(), min_alias_length=4) == result.groups


def test_a_recheck_keeps_the_admitting_stage_and_only_gains_reconfirmed_on() -> None:
    before = registry_for_curate()
    zenith = next(group for group in before.groups if group.mark == "zenith-blade")
    assert zenith.admission.stage == "import"

    result = admit(
        [
            accepted(
                "zenith-blade",
                stage="reconfirm",
                evidence="census",
                source="census:2026-09-20",
                recheck=True,
                confirmed_on="2026-09-20",
            )
        ],
        before,
        as_of="2026-09-20",
    )

    assert result.rechecked == ("zenith-blade",)
    assert result.admitted == ()
    after = next(group for group in result.groups if group.mark == "zenith-blade")
    assert after.admission.stage == "import"
    assert after.admission.evidence == "dataset"
    assert after.admission.source == zenith.admission.source
    assert after.admission.reconfirmed_on == "2026-09-20"
    assert parse_marks(result.render(), min_alias_length=4) == result.groups


def test_a_recheck_of_an_unknown_row_is_refused() -> None:
    result = admit(
        [accepted("Newword", recheck=True)], registry_for_curate(), as_of="2026-09-20"
    )
    assert result.refused == (("Newword", "re-check of a row the registry does not have"),)


def test_a_rejected_row_is_recorded_and_not_admitted() -> None:
    result = admit(
        [accepted("Newword", decision="reject")], registry_for_curate(), as_of="2026-09-20"
    )
    assert result.rejected == ("Newword",)
    assert result.admitted == ()
    assert result.refused == ()


def test_write_marks_writes_only_marks(tmp_path: Path) -> None:
    result = admit([accepted("Newword")], registry_for_curate(), as_of="2026-09-20")
    target = write_marks(result, tmp_path / "marks.v1.json")

    assert target.name == "marks.v1.json"
    assert [path.name for path in sorted(tmp_path.iterdir())] == ["marks.v1.json"]
    assert target.read_text(encoding="utf-8") == result.render()
    assert target.read_text(encoding="utf-8").endswith("\n")


def test_render_marks_round_trips_the_fixture_registry() -> None:
    registry = registry_for_curate()
    rendered = render_marks(registry.groups)

    assert render_marks(registry.groups) == rendered
    assert parse_marks(rendered, min_alias_length=registry.boundary.min_alias_length) == (
        registry.groups
    )
