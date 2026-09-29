"""Tests for the composition root and the CLI (spec-report.md).

Every test runs against a fixture repository in tmp_path: the shipped registry lives in
`gk-data/packs/fusion/data/seed/ip-censor/_registry/`, which this lane may not write, and the composition root's whole
contract is about resolving a root — so a synthetic repo is the honest fixture.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from ipcensor import cli, llm, report
from ipcensor.registry import REGISTRY_FILES, parse_registry

TESTS_DIR = Path(__file__).resolve().parent
REGISTRY_FIXTURES = TESTS_DIR / "fixtures" / "registry" / "valid"
CURATE_FIXTURES = TESTS_DIR / "fixtures" / "curate"
SCAN_FIXTURES = TESTS_DIR / "fixtures" / "scan"
REGISTRY_DIR = "data/seed/ip-censor/_registry"

# The fixture repo's tree: one file per surface the buckets need, all carrying the invented mark.
TREE = {
    "docs/guide/the-game.md": "player-prose.md",
    "docs/research/prior-art.md": "docs-citation.md",
    "docs/architecture/software-architecture.md": "deliberate-identity.md",
    "src/FusionRpg.Core/World/WorldTemplateCatalog.cs": "code-identifier.cs",
    "data/seed/narrative/_registry/names.en.v1.json": "player-name.json",
    "data/seed/items/_registry/kinds.json": "registry-self.json",
}

CODE_ONLY_TREE = {
    "src/FusionRpg.Core/World/WorldTemplateCatalog.cs": "code-identifier.cs",
}

# An enforced surface whose mark declares no scope for it: the fixture brief names `Examplemark`,
# which is scoped to the player surfaces, so the hit is report-only (IC-1b).
OUT_OF_SCOPE_TREE = {
    "tools/seedsmith/seedsmith/adapters/items/uniques/briefs.py": "generator-prompt.py",
}


def make_repo(tmp_path: Path, *, tree: dict[str, str] | None = None) -> Path:
    root = tmp_path / "repo"
    registry_dir = root / REGISTRY_DIR
    registry_dir.mkdir(parents=True, exist_ok=True)
    for name in REGISTRY_FILES:
        (registry_dir / name).write_text(
            (REGISTRY_FIXTURES / name).read_text(encoding="utf-8"), encoding="utf-8"
        )
    for target, fixture_name in (TREE if tree is None else tree).items():
        destination = root / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(
            (SCAN_FIXTURES / fixture_name).read_text(encoding="utf-8"), encoding="utf-8"
        )
    subprocess.run(["git", "init", "-q"], cwd=root, check=True)
    subprocess.run(["git", "config", "user.email", "fixture@example.invalid"], cwd=root, check=True)
    subprocess.run(["git", "config", "user.name", "fixture"], cwd=root, check=True)
    subprocess.run(["git", "add", "-A", "-f"], cwd=root, check=True)
    subprocess.run(["git", "commit", "-q", "-m", "fixture"], cwd=root, check=True)
    return root


def fixture_registry():
    return parse_registry(
        {name: (REGISTRY_FIXTURES / name).read_text(encoding="utf-8") for name in REGISTRY_FILES}
    )


# ---- root resolution ------------------------------------------------------------


def test_the_root_comes_from_git_not_the_working_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = make_repo(tmp_path)
    nested = root / "tools" / "ip-censor"
    nested.mkdir(parents=True)
    monkeypatch.chdir(nested)

    assert report.resolve_root() == root

    # And the scan enumerates the whole repository, not the folder it was started from.
    findings = report.scan_tree(root, fixture_registry())
    paths = {finding.path for finding in findings}
    assert "docs/guide/the-game.md" in paths
    assert not any(path.startswith("tools/ip-censor/") for path in paths)


def test_an_explicit_root_wins(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert report.resolve_root(root) == root


def test_a_directory_outside_a_working_tree_is_an_error(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    outside = tmp_path / "nowhere"
    outside.mkdir()
    monkeypatch.chdir(outside)
    with pytest.raises(report.ReportError, match="not inside a git working tree"):
        report.resolve_root()


def test_registry_version_is_read_from_the_file_names(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert report.registry_version(root / REGISTRY_DIR) == "v1"


# ---- the plan document ----------------------------------------------------------


def plan_for(root: Path, *, by: str = "file", authored_only: bool = True) -> report.Plan:
    registry = fixture_registry()
    findings = report.scan_tree(root, registry)
    suggestions = report.suggest_for(findings, registry, authored_only=authored_only)
    return report.build_plan(
        findings,
        root=root,
        registry=registry,
        model=None if authored_only else "fixture-model",
        suggestions=suggestions,
        generated_at="2026-09-19T00:00:00+00:00",
    )


def test_the_plan_carries_every_required_field(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    document = plan_for(root).as_dict()

    assert document["schemaVersion"] == report.PLAN_SCHEMA_VERSION
    assert document["registryVersion"] == "v1"
    assert document["model"] is None
    assert len(document["generatedFromCommit"]) == 40
    assert document["groupBy"] == "file"

    groups = document["groups"]
    assert isinstance(groups, dict)
    assert set(groups) == {path for path in TREE}
    for path, rows in groups.items():
        for row in rows:
            assert set(row) >= {
                "path",
                "line",
                "column",
                "matched",
                "mark",
                "bucket",
                "surface",
                "remediation",
                "suggestion",
            }
            assert row["path"] == path
            assert row["remediation"] in {
                "authored",
                "generator-owned",
                "upstream-imported",
                "code-change",
            }


def test_the_plan_is_byte_stable_except_for_the_timestamp(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    first = plan_for(root)
    second = plan_for(root)
    assert report.render_plan(first) == report.render_plan(second)

    later = report.Plan(
        schema_version=first.schema_version,
        generated_from_commit=first.generated_from_commit,
        registry_version=first.registry_version,
        model=first.model,
        findings=first.findings,
        suggestions=first.suggestions,
        generated_at="2026-09-20T00:00:00+00:00",
    )
    assert report.render_plan(later) != report.render_plan(first)
    stripped = json.loads(report.render_plan(later))
    stripped.pop("generatedAt")
    baseline = json.loads(report.render_plan(first))
    baseline.pop("generatedAt")
    assert stripped == baseline

    text = report.render_plan(first)
    assert text.endswith("\n")
    assert "\r\n" not in text
    assert text == json.dumps(json.loads(text), indent=2, ensure_ascii=False, sort_keys=True) + "\n"


@pytest.mark.parametrize("by", report.GROUPINGS)
def test_every_grouping_loses_no_finding(tmp_path: Path, by: str) -> None:
    root = make_repo(tmp_path)
    plan = plan_for(root)
    groups = plan.as_dict(by=by)["groups"]
    assert isinstance(groups, dict)
    assert sum(len(rows) for rows in groups.values()) == len(plan.findings)


def test_an_unknown_grouping_is_refused(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    with pytest.raises(report.ReportError, match="unknown grouping"):
        plan_for(root).as_dict(by="nonsense")


def test_the_report_mentions_every_finding(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    plan = plan_for(root)
    text = report.render_report(plan)
    for finding in plan.findings:
        assert f"{finding.path}:{finding.line}:{finding.column}" in text
    assert text.endswith("\n")


# ---- exit codes -----------------------------------------------------------------


def test_scan_exits_zero_with_findings_by_default(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert cli.main(["scan", "--root", str(root), "--authored-only"]) == cli.EXIT_OK


def test_fail_on_enforced_is_non_zero_when_a_player_finding_exists(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    code = cli.main(
        ["scan", "--root", str(root), "--authored-only", "--fail-on", "enforced"]
    )
    assert code == cli.EXIT_FINDINGS


def test_fail_on_enforced_is_zero_when_only_report_only_buckets_are_present(tmp_path: Path) -> None:
    root = make_repo(tmp_path, tree=CODE_ONLY_TREE)
    assert (
        cli.main(["scan", "--root", str(root), "--authored-only", "--fail-on", "enforced"])
        == cli.EXIT_OK
    )


def test_fail_on_enforced_ignores_a_hit_outside_the_marks_scope(tmp_path: Path) -> None:
    # `generator-prompt` is an enforced surface, but the fixture mark's row scopes it to the player
    # surfaces — IC-1b's "in scope on player-facing surfaces only". Enforcing it anyway would block a
    # release over a brief that owns no fix.
    root = make_repo(tmp_path, tree=OUT_OF_SCOPE_TREE)
    registry = fixture_registry()
    findings = report.scan_tree(root, registry)

    assert [finding.surface for finding in findings] == ["generator-prompt"]
    assert registry.is_enforced("generator-prompt")
    assert report.enforced_findings(findings, registry) == ()
    assert (
        cli.main(["scan", "--root", str(root), "--authored-only", "--fail-on", "enforced"])
        == cli.EXIT_OK
    )


def test_a_missing_registry_is_an_error_not_a_silent_pass(tmp_path: Path) -> None:
    root = tmp_path / "empty"
    root.mkdir()
    assert cli.main(["registry-check", "--root", str(root)]) == cli.EXIT_ERROR


# ---- authored-only --------------------------------------------------------------


def test_authored_only_never_builds_a_proposer(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = make_repo(tmp_path)

    def boom(*args: object, **kwargs: object) -> object:
        raise AssertionError("--authored-only must not build an LLM client")

    monkeypatch.setattr(llm, "build_proposer", boom)
    monkeypatch.setattr(llm, "load_config", boom)

    assert cli.main(["scan", "--root", str(root), "--authored-only"]) == cli.EXIT_OK


# ---- class-4 self-exclusion -----------------------------------------------------


def test_a_plan_is_not_a_finding_source_next_time(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    # Deliberately NOT under tasks/ip-censor/**, which the fixture registry already lists as class 3:
    # this test is about class 4, the output path itself.
    plan_path = "tasks/reports/plan.json"

    assert (
        cli.main(
            ["scan", "--root", str(root), "--authored-only", "--plan", plan_path]
        )
        == cli.EXIT_OK
    )
    written = root / plan_path
    assert "Examplemark" in written.read_text(encoding="utf-8")
    # The reader enumerates the TRACKED tree, so the plan has to be committed for the next scan to
    # see it — which is exactly the case spec-registry.md §self_paths warns about.
    subprocess.run(["git", "add", "-f", plan_path], cwd=root, check=True)

    # The plan now contains the mark, so without class-4 exclusion the next scan would flag it.
    without_exclusion = report.scan_tree(root, fixture_registry())
    assert plan_path in {finding.path for finding in without_exclusion}

    with_exclusion = report.scan_tree(
        root, fixture_registry(), extra_self_paths=report.output_self_paths(root, [plan_path])
    )
    assert plan_path not in {finding.path for finding in with_exclusion}


def test_output_self_paths_ignores_paths_outside_the_root(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert report.output_self_paths(root, [root / "tasks/ip-censor/x.json", tmp_path / "other"]) == (
        "tasks/ip-censor/x.json",
    )


# ---- the other verbs ------------------------------------------------------------


def test_census_and_all_run(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert cli.main(["census", "--root", str(root), "--top", "5"]) == cli.EXIT_OK
    assert (
        cli.main(
            [
                "all",
                "--root",
                str(root),
                "--authored-only",
                "--plan",
                "tasks/ip-censor/plan.json",
                "--report",
                "tasks/ip-censor/report.md",
            ]
        )
        == cli.EXIT_OK
    )
    assert (root / "tasks/ip-censor/plan.json").exists()
    assert (root / "tasks/ip-censor/report.md").exists()


def test_suggest_and_registry_check_run(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    assert cli.main(["suggest", "--root", str(root), "--authored-only"]) == cli.EXIT_OK
    assert cli.main(["registry-check", "--root", str(root)]) == cli.EXIT_OK


def test_curate_reconfirm_writes_a_candidate_file(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    code = cli.main(
        ["curate", "reconfirm", "--root", str(root), "--no-model", "--as-of", "2026-09-19"]
    )
    assert code == cli.EXIT_OK
    written = sorted((root / "tasks/ip-censor/curate").glob("*.json"))
    assert [path.name for path in written] == ["reconfirm-2026-09-19.json"]
    document = json.loads(written[0].read_text(encoding="utf-8"))
    assert document["dataset"] == "reconfirm"


def test_curate_import_writes_candidates_from_the_pinned_export(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    # The authored filter goes where the tool reads it; the shipped copy is T8 part 2.
    (root / REGISTRY_DIR / "import-filter.v1.json").write_text(
        (CURATE_FIXTURES / "import-filter.v1.json").read_text(encoding="utf-8"), encoding="utf-8"
    )

    code = cli.main(
        [
            "curate",
            "import",
            "--root",
            str(root),
            "--input",
            str(CURATE_FIXTURES / "export.xml"),
            "--dataset-version",
            "2026-09-19",
            "--as-of",
            "2026-09-19",
        ]
    )

    assert code == cli.EXIT_OK
    written = root / "tasks/ip-censor/curate/uspto-2026-09-19.json"
    document = json.loads(written.read_text(encoding="utf-8"))
    assert [row["mark"] for row in document["candidates"]] == ["EXAMPLEMARK", "Zenith Blade"]
    assert all(row["decision"] is None for row in document["candidates"])


def test_curate_admit_dry_run_changes_nothing(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    marks = root / REGISTRY_DIR / "marks.v1.json"
    before = marks.read_text(encoding="utf-8")
    candidates = root / "tasks/ip-censor/curate/reconfirm-2026-09-19.json"
    candidates.parent.mkdir(parents=True, exist_ok=True)
    candidates.write_text(
        json.dumps(
            {
                "schemaVersion": 1,
                "dataset": "reconfirm",
                "datasetVersion": None,
                "candidates": [
                    {
                        "mark": "Newword",
                        "aliases": ["Newword"],
                        "category": "franchise-mark",
                        "stage": "reconfirm",
                        "evidence": "census",
                        "source": "census:2026-09-19",
                        "decision": "accept",
                        "confirmedBy": "owner",
                        "confirmedOn": "2026-09-19",
                        "scope": ["player-name"],
                        "remediation": "authored",
                        "recheck": False,
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    assert (
        cli.main(
            [
                "curate",
                "admit",
                "--root",
                str(root),
                "--candidates",
                str(candidates),
                "--as-of",
                "2026-09-20",
                "--dry-run",
            ]
        )
        == cli.EXIT_OK
    )
    assert marks.read_text(encoding="utf-8") == before

    assert (
        cli.main(
            [
                "curate",
                "admit",
                "--root",
                str(root),
                "--candidates",
                str(candidates),
                "--as-of",
                "2026-09-20",
            ]
        )
        == cli.EXIT_OK
    )
    assert "Newword" in marks.read_text(encoding="utf-8")
