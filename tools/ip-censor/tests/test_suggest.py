"""Tests for `suggest` and the tool's one LLM client (spec-suggest.md §Testing Strategy).

Findings are constructed directly: a `Finding` is a value, so the resolution rules can be exercised
without a tree, a registry file or a model. The model seam is always injected, so nothing here reaches
a network.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from ipcensor import llm
from ipcensor.registry import REGISTRY_FILES, parse_registry
from ipcensor.scan import Finding
from ipcensor.suggest import PROPOSAL_MARKER, SOURCES, suggest

TESTS_DIR = Path(__file__).resolve().parent
REGISTRY_FIXTURES = TESTS_DIR / "fixtures" / "registry" / "valid"
REAL_REPLACEMENTS = TESTS_DIR / "fixtures" / "replacements.v1.json"


def _registry_files(replacements: Path | None = None) -> dict[str, str]:
    files = {
        name: (REGISTRY_FIXTURES / name).read_text(encoding="utf-8") for name in REGISTRY_FILES
    }
    if replacements is not None:
        files["replacements.v1.json"] = replacements.read_text(encoding="utf-8")
    return files


def invented_registry():
    return parse_registry(_registry_files())


def real_pairs_registry():
    return parse_registry(_registry_files(REAL_REPLACEMENTS))


def finding(
    *,
    mark: str = "examplemark",
    matched: str | None = None,
    bucket: str = "player-prose",
    surface: str = "player-prose",
    remediation: str = "authored",
    path: str = "docs/guide/the-game.md",
    line: int = 1,
    column: int = 1,
) -> Finding:
    return Finding(
        path=path,
        line=line,
        column=column,
        matched=mark if matched is None else matched,
        mark=mark,
        bucket=bucket,
        surface=surface,
        remediation=remediation,
    )


# ---- the authored map wins ------------------------------------------------------


def test_an_authored_pair_wins_and_the_proposer_is_not_called() -> None:
    calls: list[Finding] = []

    def proposer(item: Finding) -> str:
        calls.append(item)
        return "something the model made up"

    result = suggest([finding()], invented_registry(), proposer)

    assert result[0].source == "authored"
    assert result[0].replacement == "fusionmark"
    assert result[0].note is None
    assert calls == []


def test_the_authored_map_is_matched_on_the_spelling_too() -> None:
    # IC-1b authors both `pvz` and `Plants vs. Zombies`; the second is a spelling, not the mark.
    result = suggest(
        [finding(mark="pvz", matched="Plants vs. Zombies")], real_pairs_registry()
    )
    assert result[0].source == "authored"
    assert result[0].replacement == "Fusion"


@pytest.mark.parametrize(
    ("mark", "matched", "replacement"),
    [
        ("crazy-dave", "Crazy Dave", "the Garden Keeper"),
        ("penny", "Penny", "Hourbloom"),
        ("dr-zomboss", "Dr. Zomboss", "the Rotwright"),
        ("pvz", "pvz", "Fusion"),
    ],
)
def test_the_owner_authored_pairs_resolve(mark: str, matched: str, replacement: str) -> None:
    result = suggest([finding(mark=mark, matched=matched)], real_pairs_registry())
    assert (result[0].source, result[0].replacement) == ("authored", replacement)


# ---- proposals ------------------------------------------------------------------


def test_a_mark_without_a_pair_is_proposed_and_marked() -> None:
    result = suggest(
        [finding(mark="zenith-blade", matched="Zenith Blade")],
        invented_registry(),
        lambda item: "the still air",
    )

    assert result[0].source == "proposed"
    assert result[0].replacement == "the still air"
    assert result[0].note == PROPOSAL_MARKER


def test_proposals_are_made_once_per_distinct_mark() -> None:
    calls: list[str] = []

    def proposer(item: Finding) -> str:
        calls.append(item.mark)
        return "the still air"

    findings = [
        finding(mark="zenith-blade", matched="Zenith Blade", path="a.md"),
        finding(mark="zenith-blade", matched="zenith-blade", path="b.md"),
        finding(mark="zenith-blade", matched="ZENITH BLADE", path="c.md"),
    ]
    result = suggest(findings, invented_registry(), proposer)

    assert calls == ["zenith-blade"]
    assert [item.source for item in result] == ["proposed"] * 3


def test_a_proposer_that_returns_nothing_is_not_a_proposal() -> None:
    result = suggest([finding(mark="zenith-blade")], invented_registry(), lambda item: None)
    assert result[0].source == "none"
    assert result[0].replacement is None


# ---- refusals -------------------------------------------------------------------


def test_an_identifier_finding_gets_no_suggestion_and_no_call() -> None:
    calls: list[Finding] = []

    def proposer(item: Finding) -> str:
        calls.append(item)
        return "fusionRH"

    findings = [
        finding(bucket="code-identifier", surface="code-identifier", remediation="code-change"),
        finding(mark="pvz", matched="pvz.*", remediation="code-change", path="src/x.cs"),
    ]
    result = suggest(findings, invented_registry(), proposer)

    assert [item.source for item in result] == ["none", "none"]
    assert [item.replacement for item in result] == [None, None]
    assert calls == []


def test_remediation_is_carried_through_unchanged() -> None:
    findings = [
        finding(remediation="generator-owned", bucket="player-name", surface="player-name"),
        finding(mark="zenith-blade", remediation="upstream-imported", bucket="player-prose"),
        finding(remediation="authored"),
    ]
    result = suggest(findings, invented_registry(), lambda item: "the still air")

    assert [item.remediation for item in result] == [
        "generator-owned",
        "upstream-imported",
        "authored",
    ]
    assert result[0].source == "authored"  # the invented pair still wins for `examplemark`
    assert result[1].source == "proposed"
    assert result[2].source == "authored"


def test_a_raising_proposer_is_recorded_not_fatal() -> None:
    def proposer(item: Finding) -> str:
        raise RuntimeError("endpoint refused the connection")

    result = suggest([finding(mark="zenith-blade")], invented_registry(), proposer)

    assert result[0].source == "none"
    assert result[0].replacement is None
    assert "endpoint refused the connection" in (result[0].note or "")


# ---- authored-only mode ---------------------------------------------------------


def test_authored_only_is_deterministic() -> None:
    findings = [finding(), finding(mark="zenith-blade", path="b.md")]
    first = suggest(findings, invented_registry())
    second = suggest(findings, invented_registry())

    assert first == second
    assert json.dumps([item.as_dict() for item in first]) == json.dumps(
        [item.as_dict() for item in second]
    )
    assert first[1].source == "none"
    assert first[1].note is None


def test_authored_only_never_touches_the_llm(monkeypatch: pytest.MonkeyPatch) -> None:
    def boom(*args: Any, **kwargs: Any) -> Any:
        raise AssertionError("the LLM client must not be reached in authored-only mode")

    monkeypatch.setattr(llm, "load_config", boom)
    monkeypatch.setattr(llm, "build_proposer", boom)

    result = suggest([finding(), finding(mark="zenith-blade")], invented_registry())
    assert len(result) == 2


def test_the_source_vocabulary_is_closed() -> None:
    assert SOURCES == {"authored", "proposed", "none"}
    assert {item.source for item in suggest([finding()], invented_registry())} <= SOURCES


# ---- the LLM client's configuration ---------------------------------------------


def test_config_precedence_is_env_then_committed_then_built_in() -> None:
    built_in = llm.load_config(env={}, defaults={})
    assert built_in.endpoint == llm.BUILT_IN_DEFAULTS["endpoint"]
    assert built_in.model == llm.BUILT_IN_DEFAULTS["model"]
    assert built_in.attempts == int(llm.BUILT_IN_DEFAULTS["attempts"])

    committed = llm.load_config(env={}, defaults={"IPCENSOR_LLM_MODEL": "committed-model"})
    assert committed.model == "committed-model"
    assert committed.endpoint == built_in.endpoint

    seeded = llm.load_config(
        env={"IPCENSOR_LLM_MODEL": "env-model", "IPCENSOR_LLM_TIMEOUT": "9"},
        defaults={"IPCENSOR_LLM_MODEL": "committed-model"},
    )
    assert seeded.model == "env-model"
    assert seeded.timeout == 9.0


def test_a_blank_environment_value_falls_through() -> None:
    config = llm.load_config(
        env={"IPCENSOR_LLM_MODEL": "  "}, defaults={"IPCENSOR_LLM_MODEL": "committed-model"}
    )
    assert config.model == "committed-model"


def test_read_env_file_parses_key_value_lines(tmp_path: Path) -> None:
    path = tmp_path / ".env"
    path.write_text(
        "# a comment\nIPCENSOR_LLM_MODEL=local-model\n\nIPCENSOR_LLM_TIMEOUT=12\n",
        encoding="utf-8",
    )
    assert llm.read_env_file(path) == {
        "IPCENSOR_LLM_MODEL": "local-model",
        "IPCENSOR_LLM_TIMEOUT": "12",
    }
    assert llm.read_env_file(tmp_path / "missing") == {}


class _FakeResponse:
    def __init__(self, body: str) -> None:
        self._body = body.encode("utf-8")

    def read(self) -> bytes:
        return self._body

    def close(self) -> None:
        return None


def test_the_proposer_sends_the_configured_model_and_prompt() -> None:
    captured: dict[str, Any] = {}

    def opener(request: Any, timeout: float | None = None) -> _FakeResponse:
        captured["url"] = request.full_url
        captured["timeout"] = timeout
        captured["payload"] = json.loads(request.data.decode("utf-8"))
        return _FakeResponse(json.dumps({"choices": [{"message": {"content": "the still air"}}]}))

    config = llm.LlmConfig(
        endpoint="http://localhost:1234/v1/chat/completions",
        model="fixture-model",
        timeout=5.0,
        attempts=1,
        retry_delay=0.0,
    )
    propose = llm.build_proposer(config, system_prompt="propose a token", opener=opener)

    assert propose("Examplemark") == "the still air"
    assert captured["url"] == config.endpoint
    assert captured["timeout"] == 5.0
    assert captured["payload"]["model"] == "fixture-model"
    assert captured["payload"]["messages"][0] == {"role": "system", "content": "propose a token"}
    assert captured["payload"]["messages"][1]["content"] == "Examplemark"


def test_an_unusable_response_raises_llm_error() -> None:
    config = llm.LlmConfig(
        endpoint="http://localhost:1234/v1/chat/completions",
        model="fixture-model",
        timeout=5.0,
        attempts=1,
        retry_delay=0.0,
    )
    propose = llm.build_proposer(
        config, system_prompt="s", opener=lambda request, timeout=None: _FakeResponse("{}")
    )
    with pytest.raises(llm.LlmError, match="unusable response"):
        propose("Examplemark")


def test_an_unreachable_endpoint_raises_after_the_configured_attempts() -> None:
    attempts: list[int] = []

    def opener(request: Any, timeout: float | None = None) -> _FakeResponse:
        attempts.append(1)
        raise OSError("connection refused")

    config = llm.LlmConfig(
        endpoint="http://localhost:1234/v1/chat/completions",
        model="fixture-model",
        timeout=5.0,
        attempts=2,
        retry_delay=0.0,
    )
    propose = llm.build_proposer(config, system_prompt="s", opener=opener)

    with pytest.raises(llm.LlmError, match="unreachable"):
        propose("Examplemark")
    assert len(attempts) == 2
