#!/usr/bin/env python3
"""
Guard: solid-enforcement `vocabulary-mirror` (spec-vocabulary-mirror.md).

Reads gk-core/scripts/vocabulary-mirrors.v1.json (one row per Python mirror of a C# closed vocabulary) and
checks each pair for drift:

    V1 -- a mirror member not produced by any owner member (the mirror invents a value)
    V2 -- relation "equal" with an owner member the mirror never produces (the mirror is stale)
    V3 -- relation "subset" with an absent owner member not covered by `excludes`, or an `excludes`
          entry with an empty reason
    V4 -- a manifest row whose owner file/enum/mirror symbol does not resolve

Python, not PowerShell, because it must import the real seedsmith symbols rather than re-parse
Python source (scripts/guard-vocabulary-mirror.ps1 is the thin wrapper CI/run_guards.py call).

`owner.kind` is one of two shapes:
    csharp-enum  -- a plain `enum Name { A, B = n, ... }` block, comments/trailing commas allowed.
                    An unparseable block is V4, never a silent empty set.
    json-catalog -- a JSON path of the form `<arrayKey>[].<field>`. The manifest's own `file` may
                    name any version; the REAL file checked is always the highest
                    `<domain>.v<n>.json` on disk (spec's own rule: "never a pinned version number").

`transform` maps an owner member's raw name to the string the mirror is expected to carry:
    identity   -- unchanged (a json-catalog id is already the wire string)
    lowerFirst -- lowercase only the first character (PascalCase member -> camelCase wire string,
                  e.g. `RolledTarget` -> `rolledTarget`; a single-word member lowercases the same way
                  `lower` would, so this is the one transform every csharp-enum pair in the real
                  manifest uses)

Usage (repo root):
    python gk-core/scripts/guard-vocabulary-mirror.py
    python gk-core/scripts/guard-vocabulary-mirror.py --root <path> --manifest <path>   # falsifier fixtures

Exit 0 = clean, 1 = a V1-V4 finding.
"""
from __future__ import annotations

import argparse
import fnmatch
import importlib
import json
import re
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, forge_root, owning_base, repo_bases  # noqa: E402


class VocabularyMirrorError(ValueError):
    """A manifest row's owner or mirror could not be resolved -- always reported as V4, never a
    silently empty set."""


def strip_comments(text: str) -> str:
    """`//` line comments, `/* */` block comments, and string/char literals -- the same one-pass,
    left-to-right discipline `scripts/guard-test-substrate.ps1`'s own `Strip-Comments` uses, ported
    to Python so an enum member's own XML-doc `///` summary is never scanned as a member token."""
    out: "list[str]" = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i = min(n, i + 2)
            out.append(" ")
            continue
        if c in ("\"", "'"):
            quote = c
            out.append(c)
            i += 1
            while i < n:
                if text[i] == "\\":
                    out.append(text[i:i + 2])
                    i += 2
                    continue
                out.append(text[i])
                if text[i] == quote:
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


_MEMBER_TOKEN_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def extract_enum_members(csharp_text: str, enum_name: str) -> "list[str]":
    """Every member name of `enum <enum_name> { ... }` in `csharp_text`, comments/trailing-comma/
    explicit-value tolerant. Raises VocabularyMirrorError (never returns an empty list silently) if
    the enum cannot be found or a member token is not a plain identifier."""
    code = strip_comments(csharp_text)
    m = re.search(r"\benum\s+" + re.escape(enum_name) + r"\b\s*\{", code)
    if not m:
        raise VocabularyMirrorError(f"no 'enum {enum_name}' block found")
    depth = 1
    i = m.end()
    while i < len(code) and depth > 0:
        if code[i] == "{":
            depth += 1
        elif code[i] == "}":
            depth -= 1
        i += 1
    if depth != 0:
        raise VocabularyMirrorError(f"enum {enum_name}: unterminated block (no matching '}}')")
    body = code[m.end():i - 1]

    members: "list[str]" = []
    for raw in body.split(","):
        raw = raw.strip()
        if not raw:
            continue
        name = raw.split("=", 1)[0].strip()
        if not _MEMBER_TOKEN_RE.match(name):
            raise VocabularyMirrorError(f"enum {enum_name}: unparseable member token {raw!r}")
        members.append(name)
    if not members:
        raise VocabularyMirrorError(f"enum {enum_name}: parsed to zero members")
    return members


_VERSIONED_NAME_RE = re.compile(r"^(.+)\.v(\d+)\.json$")
_JSON_ARRAY_PATH_RE = re.compile(r"^(\w+)\[\]\.(\w+)$")



def _resolve_hint(repo_root: Path, hint_path: Path) -> Path:
    """Where a manifest `file_hint` actually is, given the CALLER's root.

    The hint is REPOSITORY-QUALIFIED by its own contract - the docstring's own example is
    `gk-core/data/tuning/status-catalog.v1.json` - so joining it onto `repo_root` produced
    `<gk-forge>/gk-core/data/tuning`, a directory that exists in no repository. That is why the guard
    reported "no status-catalog.v*.json found" for a catalog that is published and present.

    So a leading component naming one of the resolver's own repositories is stripped and the remainder
    resolved through `owning_base`, which asks which repository TRACKS it. A hint that names no known
    repository is used as-is, so a relative hint keeps behaving exactly as before.
    """
    known = {base.name for base in repo_bases(repo_root)}
    parts = hint_path.parts
    if len(parts) > 1 and parts[0] in known:
        relative = Path(*parts[1:])
        base = owning_base(relative.as_posix(), repo_root)
        return (base or repo_root) / relative
    # A hint naming no known repository is the caller's own relative path and is used LITERALLY. This is not
    # a style choice: `test_a_non_versioned_hint_is_used_literally` passes a temp directory and the hint
    # `plain.json`, and routing that through the resolver answered with a directory the caller never named.
    return repo_root / hint_path

def resolve_latest_versioned_path(repo_root: Path, file_hint: str) -> Path:
    """The manifest's own rule: 'the owner file for a tuning catalog is the latest published
    version, resolved at run time... never a pinned version number.' `file_hint` (e.g.
    `gk-core/data/tuning/status-catalog.v1.json`) names the DOMAIN and directory; the file actually read is
    always the highest `<domain>.v<n>.json` on disk, regardless of which version the manifest text
    names."""
    hint_path = Path(file_hint)
    m = _VERSIONED_NAME_RE.match(hint_path.name)
    if not m:
        return _resolve_hint(repo_root, hint_path)
    domain = m.group(1)
    directory = _resolve_hint(repo_root, hint_path).parent
    pat = re.compile(r"^" + re.escape(domain) + r"\.v(\d+)\.json$")
    candidates = []
    if directory.is_dir():
        for f in directory.iterdir():
            fm = pat.match(f.name)
            if fm:
                candidates.append((int(fm.group(1)), f))
    if not candidates:
        raise VocabularyMirrorError(f"no {domain}.v*.json found in {directory}")
    candidates.sort(key=lambda t: t[0])
    return candidates[-1][1]


def resolve_json_catalog_owner(doc: dict, path: str) -> "list[str]":
    m = _JSON_ARRAY_PATH_RE.match(path)
    if not m:
        raise VocabularyMirrorError(f"unsupported json-catalog path shape: {path!r} (only 'array[].field' is supported)")
    array_key, field = m.group(1), m.group(2)
    arr = doc.get(array_key)
    if not isinstance(arr, list):
        raise VocabularyMirrorError(f"json-catalog: {array_key!r} is not an array in the resolved file")
    values = [row[field] for row in arr if isinstance(row, dict) and field in row]
    if not values:
        raise VocabularyMirrorError(f"json-catalog: {path!r} resolved to zero values")
    return values


def resolve_owner_members(repo_root: Path, owner: dict) -> "list[str]":
    kind = owner.get("kind")
    if kind == "csharp-enum":
        # NOT `repo_root / owner["file"]`. `repo_root` is the CALLER's root - the test passes gk-forge -
        # while every `csharp-enum` owner is gk-core's, e.g.
        # `src/FusionRpg.Core/Actions/ActionEnums.cs`. That is a sibling, so no `..` hop reaches it and the
        # guard reported "owner file not found" for a file that is present. `owning_base` asks which
        # repository TRACKS the path; `owner["file"]` is always a concrete file, so this is per-file
        # resolution and never the shadow-directory case. A repository that tracks it nowhere falls back to
        # the join, which keeps this guard's refusal unchanged.
        base = owning_base(owner["file"], repo_root)
        path = (base or repo_root) / owner["file"]
        if not path.is_file():
            raise VocabularyMirrorError(f"owner file not found: {path}")
        return extract_enum_members(path.read_text(encoding="utf-8"), owner["enum"])
    if kind == "json-catalog":
        resolved = resolve_latest_versioned_path(repo_root, owner["file"])
        if not resolved.is_file():
            raise VocabularyMirrorError(f"owner file not found: {resolved}")
        doc = json.loads(resolved.read_text(encoding="utf-8"))
        return resolve_json_catalog_owner(doc, owner["path"])
    raise VocabularyMirrorError(f"unknown owner.kind {kind!r} -- only csharp-enum/json-catalog exist; a third is a reviewed extension")


def resolve_mirror_members(module_name: str, symbol: str) -> "set[str]":
    try:
        module = importlib.import_module(module_name)
    except ImportError as ex:
        raise VocabularyMirrorError(f"cannot import mirror module {module_name!r}: {ex}") from ex
    if not hasattr(module, symbol):
        raise VocabularyMirrorError(f"mirror module {module_name!r} has no symbol {symbol!r}")
    value = getattr(module, symbol)
    if not isinstance(value, (set, frozenset, tuple, list)):
        raise VocabularyMirrorError(f"mirror {module_name}.{symbol} is a {type(value).__name__}, not a set/tuple/list")
    return set(value)


TRANSFORMS = {
    "identity": lambda s: s,
    "lowerFirst": lambda s: (s[0].lower() + s[1:]) if s else s,
}


def check_pair(repo_root: Path, pair: dict) -> "list[str]":
    pid = pair.get("id", "<unnamed>")
    transform_name = pair.get("transform")
    transform = TRANSFORMS.get(transform_name)
    if transform is None:
        return [f"V4 {pid}: unknown transform {transform_name!r} -- only {sorted(TRANSFORMS)} exist"]

    try:
        owner_members_raw = resolve_owner_members(repo_root, pair["owner"])
    except VocabularyMirrorError as ex:
        return [f"V4 {pid}: owner did not resolve -- {ex}"]

    try:
        mirror_set = resolve_mirror_members(pair["mirror"]["module"], pair["mirror"]["symbol"])
    except VocabularyMirrorError as ex:
        return [f"V4 {pid}: mirror did not resolve -- {ex}"]

    owner_expected = {transform(m) for m in owner_members_raw}
    findings: "list[str]" = []

    for extra in sorted(mirror_set - owner_expected):
        findings.append(f"V1 {pid}: mirror invents {extra!r} -- no owner member produces it")

    missing = owner_expected - mirror_set
    relation = pair.get("relation")
    if relation == "equal":
        for m in sorted(missing):
            findings.append(f"V2 {pid}: owner produces {m!r}, absent from the mirror (relation=equal)")
    elif relation == "subset":
        excludes = pair.get("excludes") or []
        for ex in excludes:
            if not str(ex.get("reason", "")).strip():
                findings.append(f"V3 {pid}: excludes entry {ex.get('member')!r} has an empty reason")
        for m in sorted(missing):
            if not any(fnmatch.fnmatch(m, str(ex.get("member", ""))) for ex in excludes):
                findings.append(f"V3 {pid}: owner produces {m!r}, absent from the mirror and not covered by any excludes entry")
    else:
        findings.append(f"V4 {pid}: unknown relation {relation!r} -- only equal/subset exist")

    return findings


def find_repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=None, help="repo root (default: two directories up from this script)")
    parser.add_argument("--manifest", default=None, help="manifest path (default: <root>/scripts/vocabulary-mirrors.v1.json)")
    args = parser.parse_args(argv)

    repo_root = Path(args.root).resolve() if args.root else find_repo_root()
    manifest_path = Path(args.manifest) if args.manifest else repo_root / "scripts" / "vocabulary-mirrors.v1.json"

    if not manifest_path.is_file():
        print(f"VOCABULARY MIRROR GUARD: EXIT_CANNOT_RUN -- manifest not found: {manifest_path}", file=sys.stderr)
        return 2

    # seedsmith is gk-forge's Python tree, and gk-forge is a SIBLING of gk-core, so no number of
    # ".." hops from a gk-core subdirectory reaches it. The walk-up that stood here returned a
    # path that has not existed since the split, and every V4 mirror check then reported "did not
    # resolve" - nine of them - which reads as a vocabulary problem and is a ROOT problem.
    # A MISSING SIBLING IS THIS GUARD'S "CANNOT RUN", not a crash. keepverse_roots raises
    # RootNotFound (a RuntimeError) and nothing here catches it, so a repository that is simply not
    # checked out produced an unhandled traceback and exit 1 - the same code a real finding uses.
    # The named-code-and-return-2 path below is the convention this guard already had for a missing
    # manifest; a missing sibling is the same class of condition and gets the same treatment.
    try:
        seedsmith_root = forge_root(repo_root) / "tools" / "seedsmith"
    except RootNotFound as exc:
        print(f"VOCABULARY MIRROR GUARD: EXIT_FORGE_ROOT_MISSING -- {exc}", file=sys.stderr)
        return 2
    if str(seedsmith_root) not in sys.path:
        sys.path.insert(0, str(seedsmith_root))

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    pairs = manifest.get("pairs", [])

    findings: "list[str]" = []
    for pair in pairs:
        findings.extend(check_pair(repo_root, pair))

    if findings:
        print("VOCABULARY MIRROR GUARD FAILED:")
        for f in findings:
            print(f"  {f}")
        return 1

    print(f"VOCABULARY MIRROR GUARD OK — {len(pairs)} pair(s) checked, no drift")
    return 0


if __name__ == "__main__":
    sys.exit(main())
