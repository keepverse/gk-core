"""The registry parser: every authored decision the scanner needs, and nothing else.

`registry` holds the program's entire judgement surface — alias groups, the scope policy, the boundary
policy and the replacement map. `scan` and `suggest` only apply what this module states; no mark,
spelling, scope, boundary rule or replacement lives in code (spec-registry.md §Objective).

Two properties this module is built around:

* **Nothing is defaulted.** A missing, empty or mistyped field is a load rejection naming the key and
  the file (tunables-ssot.md T5: a missing tunable is a load rejection naming it, never a built-in
  default).
* **The closed vocabularies are pinned; the hit population never is.** `Category`, `Surface`,
  `Remediation`, `Stage` and `Evidence` are closed sets a person edits, so a test may assert their
  membership. How many hits they produce over the tree is a reading.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Literal, Mapping, NoReturn

from ipcensor.source import matches_any

SCHEMA_VERSION = 1

MARKS_FILE = "marks.v1.json"
SCOPE_POLICY_FILE = "scope-policy.v1.json"
BOUNDARY_POLICY_FILE = "boundary-policy.v1.json"
REPLACEMENTS_FILE = "replacements.v1.json"
# IC-4.2's authored import-time rename map (T18). Not one of the four: it is a second authored artefact
# with its own shape and its own reader in the seedsmith briefkit.
IMPORT_RENAMES_FILE = "import-renames.v1.json"

REGISTRY_FILES: tuple[str, ...] = (
    MARKS_FILE,
    SCOPE_POLICY_FILE,
    BOUNDARY_POLICY_FILE,
    REPLACEMENTS_FILE,
)

# Closed vocabularies (spec-registry.md §Code Style, amended by IC-1). `title` is deliberately absent:
# common-phrase titles are a false-positive source and re-adding it reverses IC-1.
Category = Literal["franchise-mark", "real-person", "company-brand"]
Surface = Literal[
    "player-name",
    "player-prose",
    "generator-prompt",
    "docs-prose",
    "code-identifier",
    "registry",
]
Remediation = Literal["authored", "generator-owned", "upstream-imported", "code-change"]
Stage = Literal["import", "reconfirm"]
Evidence = Literal["dataset", "census", "model-proposal"]

CATEGORIES: frozenset[str] = frozenset({"franchise-mark", "real-person", "company-brand"})
SURFACES: frozenset[str] = frozenset(
    {
        "player-name",
        "player-prose",
        "generator-prompt",
        "docs-prose",
        "code-identifier",
        "registry",
    }
)
REMEDIATIONS: frozenset[str] = frozenset(
    {"authored", "generator-owned", "upstream-imported", "code-change"}
)
STAGES: frozenset[str] = frozenset({"import", "reconfirm"})
EVIDENCES: frozenset[str] = frozenset({"dataset", "census", "model-proposal"})

# IC-2: the admitting stage decides which evidence is lawful. An `import` row is dataset-derived and
# never census-derived, so a mismatched pair is a load rejection rather than a warning.
ADMISSION_EVIDENCE: Mapping[str, frozenset[str]] = {
    "import": frozenset({"dataset"}),
    "reconfirm": frozenset({"census", "model-proposal"}),
}

# Self-path classes are authored (spec-registry.md §self_paths); class 4 — whatever the plan writer
# targets — is derived by the caller.
SELF_PATH_CLASSES: tuple[str, ...] = ("class1", "class2", "class3")

# Structural, and named by the spec's remediation table rather than invented here: the fan-pack import
# root is `upstream-imported` because no generator emitted it, whatever provenance a stray row carries.
EXTERNAL_REFERENCE_ROOT = "data/seed/external-reference/**"

# The provenance keys `guard-generated-seed.py:6` defines as provenance. A file carrying one of them
# is generator output, so it may only be changed by changing its generator and regenerating (plan D8).
PROVENANCE_BLOCKS: tuple[str, ...] = ("_meta", "_provenance")
PROVENANCE_KEYS: tuple[str, ...] = ("model", "promptVersion", "batch")


class RegistryError(RuntimeError):
    """A registry file failed to load. The message always names the file and the offending key."""


@dataclass(frozen=True, slots=True)
class Alias:
    """One spelling of a mark. `scope` is the alias's own scope when it declares one (IC-6)."""

    text: str
    scope: frozenset[str] | None


@dataclass(frozen=True, slots=True)
class Admission:
    """Who admitted the row, at which stage, on what evidence (IC-2)."""

    stage: str
    evidence: str
    source: str
    confirmed_by: str
    confirmed_on: str
    reconfirmed_on: str | None


@dataclass(frozen=True, slots=True)
class AliasGroup:
    """One canonical mark with every spelling, its scope and how it may lawfully be fixed."""

    mark: str
    aliases: tuple[Alias, ...]
    category: str
    scope: frozenset[str]
    remediation: str
    admission: Admission

    def effective_scope(self, alias: Alias) -> frozenset[str]:
        """The scope an alias is enforced on: its own when declared, else the group's."""
        return alias.scope if alias.scope is not None else self.scope


@dataclass(frozen=True, slots=True)
class RenamePair:
    """One authored `match -> replacement` pair, with the person who confirmed it.

    A pair without a confirming person is exactly what the program must never invent (spec-registry.md
    §Boundaries, ideal principle 4), so `confirmed_by`/`confirmed_on` are required on every pair and
    the value is retained rather than only checked.
    """

    match: str
    replacement: str
    confirmed_by: str
    confirmed_on: str


@dataclass(frozen=True, slots=True)
class ImportRenames:
    """IC-4.2's authored map: upstream display names, and the species ids re-keyed with them.

    Two sections, both authored and both carrying provenance per pair. `ids` is filled by T19b
    (gate G1's yes); `names` by T18. The seedsmith reader consumes the data file directly, never this
    module, so the two halves can be rebuilt independently.
    """

    names: tuple[RenamePair, ...]
    ids: tuple[RenamePair, ...]

    def name_map(self) -> dict[str, str]:
        return {pair.match: pair.replacement for pair in self.names}

    def id_map(self) -> dict[str, str]:
        return {pair.match: pair.replacement for pair in self.ids}


@dataclass(frozen=True, slots=True)
class BoundaryPolicy:
    """Which characters end a token, whether a script change breaks one, and the IC-6 length floor."""

    separators: tuple[str, ...]
    script_change_breaks: bool
    min_alias_length: int


@dataclass(frozen=True, slots=True)
class PathRule:
    """One path-shaped surface rule, read by the classifier. Order is authored and load-bearing."""

    surface: str
    paths: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class ScopePolicy:
    """The enforced surface set (IC-3), the classifier's path rules, and the authored self paths.

    `default_surface` is authored rather than assumed so the classifier is total: every tracked path
    gets a surface, and a path nobody classified can never land on an enforced one by accident.
    """

    enforced_surfaces: frozenset[str]
    rules: tuple[PathRule, ...]
    self_paths: tuple[str, ...]
    default_surface: str = "code-identifier"


@dataclass(frozen=True, slots=True)
class Registry:
    """The parsed registry. `self_paths` is the flattened authored classes 1-3, in class order."""

    groups: tuple[AliasGroup, ...]
    boundary: BoundaryPolicy
    scope: ScopePolicy
    replacements: Mapping[str, str] = field(default_factory=dict)
    self_paths: tuple[str, ...] = ()

    def surface_for(self, path: str) -> str:
        """The surface `path` belongs to (A10). Shared by `census` and `scan`."""
        return surface_for(path, self.scope)

    def is_enforced(self, surface: str) -> bool:
        """Whether a finding on `surface` blocks a release (IC-3)."""
        return is_enforced(surface, self.scope)

    def scope_for(self, mark: str, matched: str) -> frozenset[str]:
        """The surfaces a hit is **in scope** on.

        The alias's own scope wins when it declares one (IC-6: an alias under `minAliasLength` is
        admitted only with one of its own); otherwise the group's. `matched` is the spelling as it
        appears, because that is what the matcher matched against an alias key.

        An unknown spelling falls back to its canonical mark's scope, and an unknown mark to the
        empty set — never to "everything": a hit whose mark the registry no longer carries must not
        become enforced by falling through.
        """
        group = self._group_for(mark)
        if group is None:
            return frozenset()
        key = matched.casefold()
        for alias in group.aliases:
            if alias.text.casefold() == key:
                return group.effective_scope(alias)
        return group.scope

    def enforces(self, *, mark: str, matched: str, surface: str) -> bool:
        """Whether a hit blocks a release: the surface is enforced **and** the mark is in scope there.

        Two authored decisions meet here. `scope-policy.v1.json`'s `enforcedSurfaces` says which
        surfaces the gate watches (IC-3); the alias group's `scope` says where that mark is in scope
        at all. The owner's ruling is explicit that they are not the same question: `pvz` is
        "in scope on player-facing surfaces only" (IC-1b), so a `pvz` hit in a `generator-prompt`
        file is a report-only finding, while `overwatch` — which is scoped to `generator-prompt` —
        is enforced in a brief.
        """
        return is_enforced(surface, self.scope) and surface in self.scope_for(mark, matched)

    def _group_for(self, mark: str) -> AliasGroup | None:
        key = mark.casefold()
        for group in self.groups:
            if group.mark.casefold() == key:
                return group
        return None


# ---- parse entry points ---------------------------------------------------------


def parse_registry(files: Mapping[str, str]) -> Registry:
    """Parse the four authored files. `files` maps a file name to its JSON text.

    The boundary policy is parsed first because `minAliasLength` is a marks-validation input (IC-6),
    and it lives in data, never in code.
    """
    for name in REGISTRY_FILES:
        if name not in files:
            raise RegistryError(f"registry is missing {name}")

    boundary = parse_boundary_policy(files[BOUNDARY_POLICY_FILE])
    groups = parse_marks(files[MARKS_FILE], min_alias_length=boundary.min_alias_length)
    scope = parse_scope_policy(files[SCOPE_POLICY_FILE])
    replacements = parse_replacements(files[REPLACEMENTS_FILE])
    return Registry(
        groups=groups,
        boundary=boundary,
        scope=scope,
        replacements=replacements,
        self_paths=scope.self_paths,
    )


def load_registry(directory: Path | str) -> Registry:
    """Read the four authored files from `directory` and parse them."""
    base = Path(directory)
    files: dict[str, str] = {}
    for name in REGISTRY_FILES:
        path = base / name
        try:
            files[name] = path.read_text(encoding="utf-8")
        except OSError as exc:
            raise RegistryError(f"{path}: cannot be read ({exc})") from exc
    return parse_registry(files)


def render_marks(
    groups: Iterable[AliasGroup], *, schema_version: int = SCHEMA_VERSION
) -> str:
    """The exact text of `marks.v1.json` for `groups`.

    The one serializer: `curate admit` writes through it and re-parses the result, so a row the parser
    would reject can never land. Deterministic by construction — groups sorted by mark, scope lists
    sorted, fixed key order, LF, trailing newline.
    """
    document = {
        "schemaVersion": schema_version,
        "groups": [_group_document(group) for group in sorted(groups, key=_mark_order)],
    }
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def _mark_order(group: AliasGroup) -> tuple[str, str]:
    return (group.mark.casefold(), group.mark)


def _group_document(group: AliasGroup) -> dict[str, Any]:
    admission: dict[str, Any] = {
        "stage": group.admission.stage,
        "evidence": group.admission.evidence,
        "source": group.admission.source,
        "confirmedBy": group.admission.confirmed_by,
        "confirmedOn": group.admission.confirmed_on,
    }
    if group.admission.reconfirmed_on is not None:
        admission["reconfirmedOn"] = group.admission.reconfirmed_on
    return {
        "mark": group.mark,
        "category": group.category,
        "scope": sorted(group.scope),
        "remediation": group.remediation,
        "aliases": [
            {"text": alias.text}
            if alias.scope is None
            else {"text": alias.text, "scope": sorted(alias.scope)}
            for alias in group.aliases
        ],
        "admission": admission,
    }


def parse_marks(text: str, *, min_alias_length: int, source: str = MARKS_FILE) -> tuple[AliasGroup, ...]:
    """Parse `marks.v1.json` into alias groups, rejecting every malformed or ambiguous row."""
    document = _load_json(text, source)
    _require_schema(document, source)
    raw_groups = _as_list(document, "groups", source, "", non_empty=True)

    groups: list[AliasGroup] = []
    marks: dict[str, str] = {}
    spellings: dict[str, str] = {}
    for index, raw_group in enumerate(raw_groups):
        where = f"groups[{index}]"
        group = _parse_group(raw_group, source, where, min_alias_length)
        canonical = group.mark.casefold()
        if canonical in marks:
            _raise(source, where, "mark", f"'{group.mark}' is already declared by another group")
        marks[canonical] = group.mark
        for alias in group.aliases:
            key = alias.text.casefold()
            if key in spellings:
                _raise(
                    source,
                    where,
                    "aliases",
                    f"'{alias.text}' is already declared by group '{spellings[key]}'",
                )
            spellings[key] = group.mark
        groups.append(group)

    return tuple(groups)


def parse_scope_policy(text: str, *, source: str = SCOPE_POLICY_FILE) -> ScopePolicy:
    """Parse `scope-policy.v1.json`: the enforced surfaces, the path rules, the three self classes."""
    document = _load_json(text, source)
    _require_schema(document, source)

    enforced = _scope_set(document, "enforcedSurfaces", source, "")

    rules: list[PathRule] = []
    for index, raw_rule in enumerate(_as_list(document, "pathRules", source, "", non_empty=True)):
        where = f"pathRules[{index}]"
        surface = _as_str(raw_rule, "surface", source, where)
        if surface not in SURFACES:
            _raise(source, where, "surface", f"unknown surface {surface!r}")
        paths = _as_str_list(raw_rule, "paths", source, where)
        rules.append(PathRule(surface=surface, paths=paths))

    raw_self = _as_mapping(document, "selfPaths", source, "")
    self_paths: list[str] = []
    for class_name in SELF_PATH_CLASSES:
        paths = _as_str_list(raw_self, class_name, source, "selfPaths")
        self_paths.extend(paths)

    default_surface = _as_str(document, "defaultSurface", source, "")
    if default_surface not in SURFACES:
        _raise(source, "", "defaultSurface", f"unknown surface {default_surface!r}")

    return ScopePolicy(
        enforced_surfaces=enforced,
        rules=tuple(rules),
        self_paths=tuple(self_paths),
        default_surface=default_surface,
    )


def parse_boundary_policy(text: str, *, source: str = BOUNDARY_POLICY_FILE) -> BoundaryPolicy:
    """Parse `boundary-policy.v1.json`.

    A policy that names no real separator is rejected: a `\\b`-only value is the measured failure
    mode that misses `PvZ融合版` while matching inside `pvz-fusion-almanac`, so it cannot be
    reintroduced through configuration (spec-registry.md §Testing Strategy).
    """
    document = _load_json(text, source)
    _require_schema(document, source)

    raw_separators = _as_list(document, "separators", source, "", non_empty=True)
    separators: list[str] = []
    for index, raw in enumerate(raw_separators):
        if not isinstance(raw, str):
            _raise(source, "", f"separators[{index}]", "must be a string")
        separators.append(raw)
    real = [item for item in separators if item.strip() and item.strip() != r"\b"]
    if not real:
        _raise(
            source,
            "",
            "separators",
            "names no separator; a \\b-only boundary policy is rejected",
        )

    script_change_breaks = _as_bool(document, "scriptChangeBreaks", source, "")
    min_alias_length = _as_int(document, "minAliasLength", source, "")
    if min_alias_length < 1:
        _raise(source, "", "minAliasLength", "must be at least 1")

    return BoundaryPolicy(
        separators=tuple(separators),
        script_change_breaks=script_change_breaks,
        min_alias_length=min_alias_length,
    )


def parse_replacements(text: str, *, source: str = REPLACEMENTS_FILE) -> dict[str, str]:
    """Parse `replacements.v1.json` into authored `match -> replacement` pairs.

    Every pair names the person who confirmed it: a replacement no person authored is exactly what the
    program must never invent (spec-registry.md §Boundaries, ideal principle 4).
    """
    document = _load_json(text, source)
    _require_schema(document, source)
    raw_pairs = _as_list(document, "replacements", source, "", non_empty=True)

    pairs: dict[str, str] = {}
    seen: dict[str, str] = {}
    for index, raw_pair in enumerate(raw_pairs):
        where = f"replacements[{index}]"
        match = _as_str(raw_pair, "match", source, where)
        replacement = _as_str(raw_pair, "replacement", source, where)
        _as_str(raw_pair, "confirmedBy", source, where)
        _as_str(raw_pair, "confirmedOn", source, where)
        key = match.casefold()
        if key in seen:
            _raise(source, where, "match", f"'{match}' already has a replacement")
        seen[key] = match
        pairs[match] = replacement
    return pairs


def parse_import_renames(text: str, *, source: str = IMPORT_RENAMES_FILE) -> ImportRenames:
    """Parse `import-renames.v1.json` (T18/T19b).

    An unknown or missing shape is a load rejection naming the file and the key; every pair must name
    the person who confirmed it. `T19b`'s "a new id that already exists anywhere in the corpus" is a
    cross-corpus check and belongs to its own task — what is enforced here is the file's own
    contract: no duplicate `match`, and no two pairs claiming the same replacement (two old ids
    collapsing onto one new id would make the re-key ambiguous).
    """
    document = _load_json(text, source)
    _require_schema(document, source)
    names = _parse_rename_section(document, "names", source)
    ids = _parse_rename_section(document, "ids", source)
    return ImportRenames(names=names, ids=ids)


def load_import_renames(
    directory: Path | str, *, name: str = IMPORT_RENAMES_FILE
) -> ImportRenames:
    """Read the map from `directory` and parse it."""
    path = Path(directory) / name
    try:
        return parse_import_renames(path.read_text(encoding="utf-8"), source=str(path))
    except OSError as exc:
        raise RegistryError(f"{path}: cannot be read ({exc})") from exc


def _parse_rename_section(
    document: Mapping[str, Any], key: str, source: str
) -> tuple[RenamePair, ...]:
    raw_pairs = _as_list(document, key, source, "", non_empty=False)
    pairs: list[RenamePair] = []
    by_match: dict[str, str] = {}
    by_replacement: dict[str, str] = {}
    for index, raw_pair in enumerate(raw_pairs):
        where = f"{key}[{index}]"
        match = _as_str(raw_pair, "match", source, where)
        replacement = _as_str(raw_pair, "replacement", source, where)
        confirmed_by = _as_str(raw_pair, "confirmedBy", source, where)
        confirmed_on = _as_str(raw_pair, "confirmedOn", source, where)
        folded = match.casefold()
        if folded in by_match:
            _raise(source, where, "match", f"'{match}' already has a replacement in {key}")
        if match == replacement:
            _raise(source, where, "replacement", f"'{match}' maps to itself in {key}")
        claimed = by_replacement.get(replacement.casefold())
        if claimed is not None:
            _raise(
                source,
                where,
                "replacement",
                f"'{replacement}' is already the replacement for '{claimed}' in {key}",
            )
        by_match[folded] = match
        by_replacement[replacement.casefold()] = match
        pairs.append(
            RenamePair(
                match=match,
                replacement=replacement,
                confirmed_by=confirmed_by,
                confirmed_on=confirmed_on,
            )
        )
    return tuple(pairs)


# ---- surface classifier and remediation derivation (A3, A10) -------------------


def surface_for(path: str, policy: ScopePolicy) -> str:
    """The surface `path` belongs to: the first authored rule that matches, else the default.

    The classifier is path-based on purpose (plan D10): a file holding both a kept identifier and a
    renamed literal cannot be split by a rule, so only pure display files are player surfaces.
    """
    for rule in policy.rules:
        if matches_any(path, rule.paths):
            return rule.surface
    return policy.default_surface


def is_enforced(surface: str, policy: ScopePolicy) -> bool:
    """Whether a finding on `surface` is on the gate's watch list (IC-3).

    The surface half only; `Registry.enforces` adds the mark's own scope. Kept separate because a
    caller that has a surface but no hit (a classifier test, the census) legitimately asks this one.
    """
    return surface in policy.enforced_surfaces


def carries_provenance(document: object) -> bool:
    """Whether a parsed JSON document carries generator provenance.

    Three shapes exist in this tree (plan D8), and all three are recognised rather than the guard's
    `_meta` alone: a top-level `_meta`, a top-level `_provenance` with `promptVersion`/`model`, and a
    per-row `_provenance` or `promptVersion`.
    """
    if isinstance(document, list):
        return any(_row_carries_provenance(row) for row in document)
    if isinstance(document, dict):
        if _row_carries_provenance(document):
            return True
        return any(
            carries_provenance(value) for value in document.values() if isinstance(value, list)
        )
    return False


def classify_remediation(*, path: str, surface: str, document: object | None) -> str:
    """How a finding at `path` may lawfully be fixed.

    `bucket` says what a hit is; this says what may be done about it. Conflating the two is how an
    execute program hand-edits generated data, which `AGENTS.md` names a hard-rule violation.
    """
    if surface == "code-identifier":
        return "code-change"
    if matches_any(path, (EXTERNAL_REFERENCE_ROOT,)):
        return "upstream-imported"
    if document is not None and carries_provenance(document):
        return "generator-owned"
    return "authored"


def _row_carries_provenance(row: object) -> bool:
    if not isinstance(row, dict):
        return False
    if "promptVersion" in row:
        return True
    for block_key in PROVENANCE_BLOCKS:
        block = row.get(block_key)
        if isinstance(block, dict) and any(key in block for key in PROVENANCE_KEYS):
            return True
    return False


def script_class(char: str) -> str:
    """`"han"`, `"latin"`, or `""` for a character with no script opinion.

    The boundary policy's script-change rule is stated in terms of this classification (ideal §6.1c).
    It lives here, beside `scriptChangeBreaks`, because it is part of what that policy means — and
    both consumers (`census`, `scan`) import it rather than each keeping a private copy.
    """
    if _is_han(char):
        return "han"
    if char.isascii() and char.isalnum():
        return "latin"
    return ""


def _is_han(char: str) -> bool:
    code = ord(char)
    return (
        0x3400 <= code <= 0x4DBF
        or 0x4E00 <= code <= 0x9FFF
        or 0xF900 <= code <= 0xFAFF
        or 0x20000 <= code <= 0x2FA1F
    )


# ---- group parsing --------------------------------------------------------------


def _parse_group(
    raw_group: Any, source: str, where: str, min_alias_length: int
) -> AliasGroup:
    mark = _as_str(raw_group, "mark", source, where)
    category = _as_str(raw_group, "category", source, where)
    if category not in CATEGORIES:
        _raise(source, where, "category", f"unknown category {category!r}")
    remediation = _as_str(raw_group, "remediation", source, where)
    if remediation not in REMEDIATIONS:
        _raise(source, where, "remediation", f"unknown remediation {remediation!r}")
    scope = _scope_set(raw_group, "scope", source, where)

    raw_aliases = _as_list(raw_group, "aliases", source, where, non_empty=True)
    aliases: list[Alias] = []
    for index, raw_alias in enumerate(raw_aliases):
        alias_where = f"{where}.aliases[{index}]"
        aliases.append(_parse_alias(raw_alias, source, alias_where, scope, min_alias_length))

    if not any(alias.text.casefold() == mark.casefold() for alias in aliases):
        _raise(
            source,
            where,
            "aliases",
            f"the canonical mark '{mark}' must also be one of its own spellings",
        )

    admission = _parse_admission(
        _as_mapping(raw_group, "admission", source, where), source, f"{where}.admission"
    )
    return AliasGroup(
        mark=mark,
        aliases=tuple(aliases),
        category=category,
        scope=scope,
        remediation=remediation,
        admission=admission,
    )


def _parse_alias(
    raw_alias: Any,
    source: str,
    where: str,
    group_scope: frozenset[str],
    min_alias_length: int,
) -> Alias:
    text = _as_str(raw_alias, "text", source, where)
    raw_scope = _optional(raw_alias, "scope")
    if raw_scope is None:
        if len(text) < min_alias_length:
            _raise(
                source,
                where,
                "scope",
                f"alias '{text}' is shorter than minAliasLength {min_alias_length} and declares no "
                "scope of its own (IC-6)",
            )
        return Alias(text=text, scope=None)

    scope = _scope_value(raw_scope, source, where, "scope")
    if not scope <= group_scope:
        wider = sorted(scope - group_scope)
        _raise(
            source,
            where,
            "scope",
            f"alias scope {wider} is wider than its group's {sorted(group_scope)}",
        )
    return Alias(text=text, scope=scope)


def _parse_admission(raw_admission: Any, source: str, where: str) -> Admission:
    stage = _as_str(raw_admission, "stage", source, where)
    if stage not in STAGES:
        _raise(source, where, "stage", f"unknown stage {stage!r}")
    evidence = _as_str(raw_admission, "evidence", source, where)
    if evidence not in EVIDENCES:
        _raise(source, where, "evidence", f"unknown evidence {evidence!r}")
    allowed = ADMISSION_EVIDENCE[stage]
    if evidence not in allowed:
        _raise(
            source,
            where,
            "evidence",
            f"stage '{stage}' admits evidence {sorted(allowed)}, not {evidence!r}",
        )
    reconfirmed_on = _optional(raw_admission, "reconfirmedOn")
    if reconfirmed_on is not None and not isinstance(reconfirmed_on, str):
        _raise(source, where, "reconfirmedOn", "must be a string or null")
    return Admission(
        stage=stage,
        evidence=evidence,
        source=_as_str(raw_admission, "source", source, where),
        confirmed_by=_as_str(raw_admission, "confirmedBy", source, where),
        confirmed_on=_as_str(raw_admission, "confirmedOn", source, where),
        reconfirmed_on=reconfirmed_on,
    )


# ---- field helpers --------------------------------------------------------------


def _load_json(text: str, source: str) -> Mapping[str, Any]:
    try:
        document = json.loads(text)
    except json.JSONDecodeError as exc:
        raise RegistryError(f"{source}: not valid JSON ({exc})") from exc
    if not isinstance(document, dict):
        raise RegistryError(f"{source}: the top level must be an object")
    return document


def _require_schema(document: Mapping[str, Any], source: str) -> None:
    value = _optional(document, "schemaVersion")
    if not isinstance(value, int) or isinstance(value, bool):
        _raise(source, "", "schemaVersion", "must be an integer")
    if value != SCHEMA_VERSION:
        _raise(source, "", "schemaVersion", f"must be {SCHEMA_VERSION}, got {value}")


def _optional(container: Any, key: str) -> Any:
    if not isinstance(container, dict) or key not in container:
        return None
    return container[key]


def _get(container: Any, key: str, source: str, where: str) -> Any:
    if not isinstance(container, dict):
        _raise(source, where, key, "the containing value must be an object")
    if key not in container:
        _raise(source, where, key, "missing")
    value = container[key]
    if value is None:
        _raise(source, where, key, "must not be null")
    return value


def _as_str(container: Any, key: str, source: str, where: str) -> str:
    value = _get(container, key, source, where)
    if not isinstance(value, str) or not value.strip():
        _raise(source, where, key, "must be a non-empty string")
    return value


def _as_str_list(container: Any, key: str, source: str, where: str) -> tuple[str, ...]:
    raw = _as_list(container, key, source, where, non_empty=True)
    out: list[str] = []
    for index, item in enumerate(raw):
        if not isinstance(item, str) or not item.strip():
            _raise(source, where, f"{key}[{index}]", "must be a non-empty string")
        out.append(item)
    return tuple(out)


def _as_list(container: Any, key: str, source: str, where: str, *, non_empty: bool) -> list[Any]:
    value = _get(container, key, source, where)
    if not isinstance(value, list):
        _raise(source, where, key, "must be an array")
    if non_empty and not value:
        _raise(source, where, key, "must not be empty")
    return value


def _as_mapping(container: Any, key: str, source: str, where: str) -> Mapping[str, Any]:
    value = _get(container, key, source, where)
    if not isinstance(value, dict):
        _raise(source, where, key, "must be an object")
    return value


def _as_bool(container: Any, key: str, source: str, where: str) -> bool:
    value = _get(container, key, source, where)
    if not isinstance(value, bool):
        _raise(source, where, key, "must be a boolean")
    return value


def _as_int(container: Any, key: str, source: str, where: str) -> int:
    value = _get(container, key, source, where)
    if not isinstance(value, int) or isinstance(value, bool):
        _raise(source, where, key, "must be an integer")
    return value


def _scope_set(container: Any, key: str, source: str, where: str) -> frozenset[str]:
    return _scope_value(_get(container, key, source, where), source, where, key)


def _scope_value(value: Any, source: str, where: str, key: str) -> frozenset[str]:
    if not isinstance(value, list) or not value:
        _raise(source, where, key, "must be a non-empty array of surfaces")
    out: set[str] = set()
    for index, item in enumerate(value):
        if not isinstance(item, str) or item not in SURFACES:
            _raise(source, where, f"{key}[{index}]", f"unknown surface {item!r}")
        out.add(item)
    return frozenset(out)


def _raise(source: str, where: str, key: str, detail: str) -> NoReturn:
    location = f"{where}.{key}" if where else key
    raise RegistryError(f"{source}: {location}: {detail}")
