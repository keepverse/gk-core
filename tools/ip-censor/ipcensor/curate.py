"""Curation: import a dataset down to reviewable candidates, then transcribe a person's decisions.

The registry is built the way the owner ruled (IC-2): **first** import a trademark dataset curated
down to game-related marks, **then** re-confirm it from the census and from model-proposed candidates.
Every admitted row records which stage admitted it.

`curate` produces candidates and transcribes decisions. It never admits a row on its own judgement, and
the model never sets a category or a scope. This module owns the import half; `reconfirm` and `admit`
are added by their own row.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, NoReturn

from ipcensor.census import TokenStat
from ipcensor.datasets import uspto
from ipcensor.registry import (
    CATEGORIES,
    SURFACES,
    Admission,
    Alias,
    AliasGroup,
    Registry,
    parse_marks,
)
from ipcensor.registry import render_marks as registry_render_marks

# The spec's evidence home (self_paths class 3). The tool's only write site resolves through here, so a
# candidate file cannot land anywhere else.
DEFAULT_CANDIDATE_DIR = "tasks/ip-censor/curate"
DEFAULT_FILTER_PATH = "data/seed/ip-censor/_registry/import-filter.v1.json"
SCHEMA_VERSION = 1

FILTER_KEYS: tuple[str, ...] = (
    "schemaVersion",
    "format",
    "category",
    "internationalClasses",
    "statusCodes",
    "goodsTerms",
    "excludeTerms",
    "minMarkLength",
)


class CurateError(RuntimeError):
    """A curation input failed to load. The message names the file and the offending key."""


@dataclass(frozen=True, slots=True)
class ImportFilter:
    """The authored narrowing: which dataset records are worth a person's review.

    Every value is data (spec-curate.md §Success criteria). The category is authored here because a
    dataset record cannot say whether a game-related mark is a franchise mark or a company brand.
    """

    format: str
    category: str
    international_classes: frozenset[str]
    status_codes: frozenset[str]
    goods_terms: tuple[str, ...]
    exclude_terms: tuple[str, ...]
    min_mark_length: int


@dataclass(frozen=True, slots=True)
class Candidate:
    """One proposed registry row. `decision`, `confirmedBy`, `confirmedOn`, `scope`, `category` and
    `remediation` are a person's to fill in.

    Three fields go beyond spec-curate.md's shape, each because the registry parser requires the value
    they carry: `confirmedOn` (an `Admission` needs a date), `remediation` (an `AliasGroup` needs one),
    and `recheck` (a re-check of an existing row must not overwrite that row's admitting stage).
    """

    mark: str
    aliases: tuple[str, ...]
    category: str | None
    stage: str
    evidence: str
    source: str
    decision: str | None = None
    confirmed_by: str | None = None
    confirmed_on: str | None = None
    scope: frozenset[str] | None = None
    remediation: str | None = None
    recheck: bool = False

    def as_dict(self) -> dict[str, object]:
        return {
            "mark": self.mark,
            "aliases": list(self.aliases),
            "category": self.category,
            "stage": self.stage,
            "evidence": self.evidence,
            "source": self.source,
            "decision": self.decision,
            "confirmedBy": self.confirmed_by,
            "confirmedOn": self.confirmed_on,
            "scope": None if self.scope is None else sorted(self.scope),
            "remediation": self.remediation,
            "recheck": self.recheck,
        }


# ---- the authored filter --------------------------------------------------------


def parse_import_filter(text: str, *, source: str = DEFAULT_FILTER_PATH) -> ImportFilter:
    """Parse the authored filter. A missing, empty or mistyped key throws naming it."""
    try:
        document = json.loads(text)
    except json.JSONDecodeError as exc:
        raise CurateError(f"{source}: not valid JSON ({exc})") from exc
    if not isinstance(document, dict):
        raise CurateError(f"{source}: the top level must be an object")
    for key in FILTER_KEYS:
        if key not in document or document[key] is None:
            _fail(source, key, "missing")

    version = document["schemaVersion"]
    if not isinstance(version, int) or isinstance(version, bool):
        _fail(source, "schemaVersion", "must be an integer")
    if version != SCHEMA_VERSION:
        _fail(source, "schemaVersion", f"must be {SCHEMA_VERSION}, got {version}")

    format_id = _as_str(document, "format", source)
    if format_id != uspto.FORMAT_ID:
        _fail(
            source,
            "format",
            f"names {format_id!r}, but the only adapter pins {uspto.FORMAT_ID!r}",
        )

    category = _as_str(document, "category", source)
    if category not in CATEGORIES:
        _fail(source, "category", f"unknown category {category!r}")

    min_mark_length = document["minMarkLength"]
    if not isinstance(min_mark_length, int) or isinstance(min_mark_length, bool):
        _fail(source, "minMarkLength", "must be an integer")
    if min_mark_length < 1:
        _fail(source, "minMarkLength", "must be at least 1")

    return ImportFilter(
        format=format_id,
        category=category,
        international_classes=frozenset(_as_str_list(document, "internationalClasses", source)),
        status_codes=frozenset(_as_str_list(document, "statusCodes", source)),
        goods_terms=tuple(_as_str_list(document, "goodsTerms", source)),
        exclude_terms=tuple(_as_str_list(document, "excludeTerms", source)),
        min_mark_length=min_mark_length,
    )


def load_import_filter(path: Path | str = DEFAULT_FILTER_PATH) -> ImportFilter:
    file = Path(path)
    try:
        return parse_import_filter(file.read_text(encoding="utf-8"), source=str(file))
    except OSError as exc:
        raise CurateError(f"{file}: cannot be read ({exc})") from exc


# ---- import ---------------------------------------------------------------------


def import_candidates(
    records: Iterable[uspto.DatasetRecord],
    filter: ImportFilter,
    *,
    dataset_id: str = uspto.DATASET_ID,
    dataset_version: str,
) -> tuple[Candidate, ...]:
    """Every record the authored filter keeps, as a candidate carrying its dataset evidence.

    A record is kept when its status is one the filter admits **and** either an international class or
    the goods text marks it as game-related, and no exclude term occurs in its mark or goods text. The
    status narrows to live marks first; the class/goods pair is what identifies the subject matter.
    """
    kept: list[Candidate] = []
    for record in records:
        if not _keeps(record, filter):
            continue
        kept.append(
            Candidate(
                mark=record.mark,
                aliases=(record.mark,),
                category=filter.category,
                stage="import",
                evidence="dataset",
                source=f"{dataset_id}:{dataset_version}:{record.record_id}",
            )
        )
    return tuple(sorted(kept, key=lambda candidate: (candidate.mark.casefold(), candidate.source)))


def read_and_import(
    export_path: Path | str,
    filter: ImportFilter,
    *,
    dataset_id: str = uspto.DATASET_ID,
    dataset_version: str,
) -> tuple[Candidate, ...]:
    """Read the pinned export and apply the filter in one step."""
    return import_candidates(
        uspto.read_export(export_path),
        filter,
        dataset_id=dataset_id,
        dataset_version=dataset_version,
    )


def _keeps(record: uspto.DatasetRecord, filter: ImportFilter) -> bool:
    mark = record.mark.strip()
    if len(mark) < filter.min_mark_length:
        return False
    if record.status not in filter.status_codes:
        return False
    goods = record.goods.casefold()
    class_match = any(item in filter.international_classes for item in record.classes)
    goods_match = any(term.casefold() in goods for term in filter.goods_terms)
    if not (class_match or goods_match):
        return False
    haystack = f"{mark.casefold()} {goods}"
    return not any(term.casefold() in haystack for term in filter.exclude_terms)


# ---- candidate files ------------------------------------------------------------


def candidate_file_name(*, dataset_id: str = uspto.DATASET_ID, when: str) -> str:
    """`<dataset>-<date>.json` — the date lives in the name so the body can stay byte-stable."""
    return f"{dataset_id}-{when}.json"


def candidate_target(name: str, *, directory: Path | str | None = None) -> Path:
    """Where a candidate file goes: `tasks/ip-censor/curate/` unless a caller overrides it.

    The override exists so a test can write into a tmp directory; the tool never passes one.
    """
    base = Path(DEFAULT_CANDIDATE_DIR if directory is None else directory)
    return base / name


def render_candidates(
    candidates: Iterable[Candidate],
    *,
    dataset_id: str = uspto.DATASET_ID,
    dataset_version: str | None = None,
) -> str:
    """The candidate file's exact text: sorted keys, LF, trailing newline, no timestamp."""
    ordered = sorted(candidates, key=lambda candidate: (candidate.mark.casefold(), candidate.source))
    document: Mapping[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "dataset": dataset_id,
        "datasetVersion": dataset_version,
        "candidates": [candidate.as_dict() for candidate in ordered],
    }
    return json.dumps(document, indent=2, ensure_ascii=False, sort_keys=True) + "\n"


def write_candidates(
    candidates: Iterable[Candidate],
    *,
    name: str,
    directory: Path | str | None = None,
    dataset_id: str = uspto.DATASET_ID,
    dataset_version: str | None = None,
) -> Path:
    """Write the candidate file and return its path."""
    target = candidate_target(name, directory=directory)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(
        render_candidates(candidates, dataset_id=dataset_id, dataset_version=dataset_version),
        encoding="utf-8",
        newline="\n",
    )
    return target


# ---- reconfirm ------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class ReconfirmResult:
    """New candidates plus a re-check entry per registry row the census still finds."""

    candidates: tuple[Candidate, ...]
    model_errors: tuple[str, ...]


def reconfirm(
    registry: Registry,
    stats: Iterable[TokenStat],
    *,
    as_of: str,
    run_id: str | None = None,
    propose: Callable[[Candidate], str | None] | None = None,
    model: str | None = None,
) -> ReconfirmResult:
    """Second curation stage: census evidence, plus model proposals when one is injected.

    A token the registry already knows becomes a **re-check** entry (the row exists; only a person can
    re-confirm it). A token it does not know, occurring on a surface the policy enforces, becomes a new
    candidate. The model never sets a category or a scope: a proposal only changes the candidate's
    evidence, and a proposer that raises is recorded rather than fatal.
    """
    run = run_id or as_of
    canonical = {
        alias.text.casefold(): group.mark
        for group in registry.groups
        for alias in group.aliases
    }
    enforced = registry.scope.enforced_surfaces
    candidates: list[Candidate] = []
    errors: list[str] = []

    for stat in stats:
        token = stat.token.casefold()
        known = canonical.get(token)
        if known is not None:
            candidates.append(
                Candidate(
                    mark=known,
                    aliases=(stat.display,),
                    category=None,
                    stage="reconfirm",
                    evidence="census",
                    source=f"census:{run}",
                    recheck=True,
                )
            )
            continue
        if not (set(stat.by_surface) & enforced):
            continue
        candidate = Candidate(
            mark=stat.display,
            aliases=(stat.display,),
            category=None,
            stage="reconfirm",
            evidence="census",
            source=f"census:{run}",
        )
        if propose is not None:
            try:
                propose(candidate)
            except Exception as exc:  # noqa: BLE001 - recorded, never fatal (spec-curate)
                errors.append(f"{stat.token}: proposer failed: {exc!r}")
            else:
                candidate = _with_model_evidence(candidate, model=model, run=run)
        candidates.append(candidate)

    ordered = tuple(sorted(candidates, key=lambda item: (item.mark.casefold(), item.source)))
    return ReconfirmResult(candidates=ordered, model_errors=tuple(errors))


def _with_model_evidence(candidate: Candidate, *, model: str | None, run: str) -> Candidate:
    return Candidate(
        mark=candidate.mark,
        aliases=candidate.aliases,
        category=candidate.category,
        stage=candidate.stage,
        evidence="model-proposal",
        source=f"model-proposal:{model or 'unknown'}:{run}",
        recheck=candidate.recheck,
    )


# ---- admit ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class AdmitResult:
    """The registry after a person's decisions: what was admitted, re-checked, rejected or refused."""

    groups: tuple[AliasGroup, ...]
    admitted: tuple[str, ...]
    rechecked: tuple[str, ...]
    rejected: tuple[str, ...]
    refused: tuple[tuple[str, str], ...]

    def render(self) -> str:
        return registry_render_marks(self.groups)


def admit(
    candidates: Iterable[Candidate],
    registry: Registry,
    *,
    as_of: str,
) -> AdmitResult:
    """Transcribe a person's decisions into the registry, then re-parse what was written.

    A row without a decision, a confirming person, a scope, a category or a remediation is refused,
    never defaulted. A row the registry already knows keeps its admitting stage and gains
    `reconfirmedOn` only — the stage that admitted a row is history.
    """
    groups = list(registry.groups)
    by_mark = {group.mark.casefold(): group for group in registry.groups}
    by_alias = {
        alias.text.casefold(): group for group in registry.groups for alias in group.aliases
    }
    admitted: list[str] = []
    rechecked: list[str] = []
    rejected: list[str] = []
    refused: list[tuple[str, str]] = []

    for candidate in candidates:
        if candidate.decision == "reject":
            rejected.append(candidate.mark)
            continue
        if candidate.decision is None:
            refused.append((candidate.mark, "no decision"))
            continue
        if candidate.decision != "accept":
            refused.append((candidate.mark, f"unknown decision {candidate.decision!r}"))
            continue
        if not candidate.confirmed_by:
            refused.append((candidate.mark, "no confirmedBy"))
            continue
        if not candidate.scope:
            refused.append((candidate.mark, "no scope"))
            continue
        unknown = sorted(set(candidate.scope) - SURFACES)
        if unknown:
            refused.append((candidate.mark, f"unknown surface(s) {unknown}"))
            continue
        if candidate.category is None or candidate.category not in CATEGORIES:
            refused.append((candidate.mark, "no category"))
            continue
        if candidate.remediation is None:
            refused.append((candidate.mark, "no remediation"))
            continue

        existing = by_mark.get(candidate.mark.casefold()) or by_alias.get(
            candidate.mark.casefold()
        )
        if existing is not None:
            groups[groups.index(existing)] = _reconfirmed(existing, candidate, as_of=as_of)
            rechecked.append(existing.mark)
            continue
        if candidate.recheck:
            refused.append((candidate.mark, "re-check of a row the registry does not have"))
            continue

        group = AliasGroup(
            mark=candidate.mark,
            aliases=tuple(Alias(text=text, scope=None) for text in candidate.aliases),
            category=candidate.category,
            scope=frozenset(candidate.scope),
            remediation=candidate.remediation,
            admission=Admission(
                stage=candidate.stage,
                evidence=candidate.evidence,
                source=candidate.source,
                confirmed_by=candidate.confirmed_by,
                confirmed_on=candidate.confirmed_on or as_of,
                reconfirmed_on=None,
            ),
        )
        groups.append(group)
        admitted.append(candidate.mark)

    ordered = tuple(sorted(groups, key=lambda group: (group.mark.casefold(), group.mark)))
    result = AdmitResult(
        groups=ordered,
        admitted=tuple(admitted),
        rechecked=tuple(rechecked),
        rejected=tuple(rejected),
        refused=tuple(refused),
    )
    # Re-parse what would be written: a row the parser would reject never lands (spec-curate.md).
    parse_marks(result.render(), min_alias_length=registry.boundary.min_alias_length)
    return result


def _reconfirmed(group: AliasGroup, candidate: Candidate, *, as_of: str) -> AliasGroup:
    return AliasGroup(
        mark=group.mark,
        aliases=group.aliases,
        category=group.category,
        scope=group.scope,
        remediation=group.remediation,
        admission=Admission(
            stage=group.admission.stage,
            evidence=group.admission.evidence,
            source=group.admission.source,
            confirmed_by=group.admission.confirmed_by,
            confirmed_on=group.admission.confirmed_on,
            reconfirmed_on=candidate.confirmed_on or as_of,
        ),
    )


def write_marks(result: AdmitResult, path: Path | str) -> Path:
    """Write the admitted registry. `admit` writes only this file (spec-curate.md §Boundaries)."""
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(result.render(), encoding="utf-8", newline="\n")
    return target


def candidate_from_dict(row: Mapping[str, Any], *, source: str = "candidates") -> Candidate:
    """Read one candidate row back out of a candidate file. A missing or mistyped field throws."""
    if not isinstance(row, Mapping):
        raise CurateError(f"{source}: a candidate row must be an object")
    for key in ("mark", "aliases", "stage", "evidence", "source"):
        if key not in row or row[key] is None:
            raise CurateError(f"{source}: candidate.{key}: missing")

    raw_scope = row.get("scope")
    scope: frozenset[str] | None = None
    if raw_scope is not None:
        if not isinstance(raw_scope, list) or not raw_scope:
            raise CurateError(f"{source}: candidate.scope: must be a non-empty array or null")
        scope = frozenset(str(item) for item in raw_scope)

    return Candidate(
        mark=str(row["mark"]),
        aliases=tuple(str(item) for item in row["aliases"]),
        category=None if row.get("category") is None else str(row["category"]),
        stage=str(row["stage"]),
        evidence=str(row["evidence"]),
        source=str(row["source"]),
        decision=None if row.get("decision") is None else str(row["decision"]),
        confirmed_by=None if row.get("confirmedBy") is None else str(row["confirmedBy"]),
        confirmed_on=None if row.get("confirmedOn") is None else str(row["confirmedOn"]),
        scope=scope,
        remediation=None if row.get("remediation") is None else str(row["remediation"]),
        recheck=bool(row.get("recheck", False)),
    )


# ---- small validators -----------------------------------------------------------


def _fail(source: str, key: str, detail: str) -> NoReturn:
    raise CurateError(f"{source}: {key}: {detail}")


def _as_str(document: Mapping[str, Any], key: str, source: str) -> str:
    value = document[key]
    if not isinstance(value, str) or not value.strip():
        _fail(source, key, "must be a non-empty string")
    return value


def _as_str_list(document: Mapping[str, Any], key: str, source: str) -> tuple[str, ...]:
    value = document[key]
    if not isinstance(value, list) or not value:
        _fail(source, key, "must be a non-empty array")
    out: list[str] = []
    for index, item in enumerate(value):
        if not isinstance(item, str) or not item.strip():
            _fail(source, f"{key}[{index}]", "must be a non-empty string")
        out.append(item)
    return tuple(out)
