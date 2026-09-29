#!/usr/bin/env python3
"""vocab-rename — the deterministic bulk-rename tool (identity-rename plan D3).

A rename pass over prose is a *tool* run, never a hand edit: the tool is dry-run by default,
deterministic, idempotent, and refuses to touch a file with uncommitted changes. An unhandled
context goes to **residue** — it is never silently skipped — and an agent closes it by adding a
phrase rule to the rules file and re-running, not by editing the tool's output.

    python gk-core/scripts/vocab-rename.py plan  --rules gk-core/scripts/vocab-rename/identity-rename.v1.json --phase names
    python gk-core/scripts/vocab-rename.py apply --rules ... --phase names
    python gk-core/scripts/vocab-rename.py check --rules ... --phase names

`plan` prints a JSON report to stdout, or writes `<id>-<phase>.journal.json` and `.md` when
`--report-dir` is given; it never touches a source file. `apply` writes; `check` exits 1 while any
rule still matches in scope (or any residue is unresolved). `--paths` narrows the scan to named
files, which is how a code task proves "zero replaceable hits outside identifiers" over the files it
just changed (plan D4): an explicit list overrides the phase's include globs, because a code file is
usually outside the prose scope `apply` walks, while the exclude list still applies.

Everything that touches the disk or git goes through the module-level seams below, so a test can
drive the whole engine against an in-memory tree (the `DocCitationAuditTests` precedent).
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

# --------------------------------------------------------------------------------------
# IO seams. A test replaces these with in-memory equivalents.

def repo_root() -> str:
    out = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
    )
    return out.stdout.strip()


def tracked_files() -> list[str]:
    """Every tracked path, repo-relative with forward slashes, sorted."""
    out = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True)
    return sorted(line.strip() for line in out.stdout.splitlines() if line.strip())


def read_text(rel: str) -> str:
    return (Path(repo_root()) / rel).read_text(encoding="utf-8")


def write_text(rel: str, text: str) -> None:
    (Path(repo_root()) / rel).write_text(text, encoding="utf-8", newline="")


def dirty_files(rels: list[str]) -> set[str]:
    """The subset of `rels` with uncommitted changes. `apply` refuses each one."""
    if not rels:
        return set()
    out = subprocess.run(
        ["git", "status", "--porcelain", "--"] + rels,
        capture_output=True, text=True, check=True, cwd=repo_root(),
    )
    dirty = set()
    for line in out.stdout.splitlines():
        if len(line) > 3:
            dirty.add(line[3:].strip().strip('"'))
    return dirty


# --------------------------------------------------------------------------------------
# Globs

def glob_to_regex(pattern: str) -> re.Pattern:
    """`**` crosses directories, `*` and `?` do not. Stdlib only (plan D3, engine)."""
    out: list[str] = []
    i = 0
    while i < len(pattern):
        ch = pattern[i]
        if ch == "*" and pattern[i : i + 2] == "**":
            out.append(".*")
            i += 2
            if i < len(pattern) and pattern[i] == "/":
                i += 1
            continue
        if ch == "*":
            out.append("[^/]*")
            i += 1
            continue
        if ch == "?":
            out.append("[^/]")
            i += 1
            continue
        out.append(re.escape(ch))
        i += 1
    return re.compile("^" + "".join(out) + "$")


def in_scope(rel: str, include: list[str], exclude: list[str]) -> bool:
    if not any(glob_to_regex(p).match(rel) for p in include):
        return False
    return not any(glob_to_regex(p).match(rel) for p in exclude)


def rendered_outputs(phase: dict) -> set[str]:
    """The files a generator writes from a content source (plan D5), derived from the sources that
    exist rather than listed by glob.

    Two reasons it is derived. A new mechanism's rendered page is excluded the moment its content
    file exists, with no rule to update. And a hand-authored page with no content twin stays in
    scope - which a glob exclude silently swallowed: T8 found
    `docs/guide/mechanisms/local-control-room.md` had no `_content` file and was skipped by the
    phase's own exclusion, so twelve real prose hits were never renamed and `check` still said clean.
    """
    spec = phase.get("rendered")
    if not spec:
        return set()

    pattern = glob_to_regex(spec["content"])
    outputs: set[str] = set(spec.get("fixed", []))
    for source in tracked_files():
        if not pattern.match(source):
            continue
        try:
            slug = json.loads(read_text(source)).get(spec["slugKey"])
        except (ValueError, OSError):
            continue
        if not isinstance(slug, str) or not slug:
            continue
        for output in spec["outputs"]:
            outputs.add(output.replace("{slug}", slug))
    return outputs


# --------------------------------------------------------------------------------------
# Protected spans — never rewritten, whatever the rules say

FENCED_BLOCK = re.compile(r"(?ms)^[ \t]*(`{3,}|~{3,}).*?^[ \t]*\1[ \t]*$")
UNCLOSED_FENCE = re.compile(r"(?ms)^[ \t]*(`{3,}|~{3,}).*")
INLINE_CODE = re.compile(r"`[^`\n]*`")
LINK_TARGET = re.compile(r"\]\(([^)\n]*)\)")
REFERENCE_DEFINITION = re.compile(r"(?m)^[ \t]*\[[^\]\n]+\]:[ \t]*\S+.*$")
BARE_URL = re.compile(r"https?://[^\s)>\]\"']+")
TOKEN_BRACE = re.compile(r"\{[A-Za-z_][A-Za-z0-9_]*\}")
HTML_TAG = re.compile(r"</?[A-Za-z][^<>]*>|<!--.*?-->", re.DOTALL)
HTML_ATTR = re.compile(r"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(\"[^\"]*\"|'[^']*')")
JSON_KEY = re.compile(r"\"(?:[^\"\\]|\\.)*\"(?=\s*:)")
JSON_KEY_WITH_COLON = re.compile(r"(\"(?:[^\"\\]|\\.)*\")(\s*:)")


def _mask(text: str, pattern: re.Pattern, kind: str, out: list[dict], group: int = 0) -> str:
    """Replace each match (or `group` inside it) with same-length NULs.

    Same-length is what keeps every later offset identical to the original text, so a replacement
    found in the masked text splices the original directly. NUL is not an identifier character, so
    a masked span is always a boundary.
    """
    pieces: list[str] = []
    cursor = 0
    for m in pattern.finditer(text):
        start, end = m.span(group)
        if start < 0 or end <= start:
            continue
        pieces.append(text[cursor:start])
        pieces.append("\x00" * (end - start))
        cursor = end
        out.append({"kind": kind, "_start": start, "_end": end, "text": text[start:end]})
    pieces.append(text[cursor:])
    return "".join(pieces)


def protect(text: str, rules: dict, rel: str, spans: list[dict]) -> str:
    """Mask everything a rename must never touch. Order matters: fences before inline code (a fence
    contains backticks), link targets before bare URLs."""
    if rel.endswith(".json"):
        return _protect_json(text, rules, spans)

    text = _mask(text, FENCED_BLOCK, "protected", spans)
    text = _mask(text, UNCLOSED_FENCE, "protected", spans)
    text = _mask(text, INLINE_CODE, "protected", spans)
    text = _mask(text, REFERENCE_DEFINITION, "protected", spans)
    text = _mask(text, LINK_TARGET, "protected", spans, group=1)
    text = _mask(text, BARE_URL, "protected", spans)
    text = _mask(text, TOKEN_BRACE, "protected", spans)
    text = _protect_html_tags(text, rules, spans)
    return text


def _protect_json(text: str, rules: dict, spans: list[dict]) -> str:
    """A JSON key is an identifier, never prose; and the value under a key that names a machine field
    (`slug`, `related`, `sources`, `pillar`, a path, an id) is one too — including an array or object
    value, which is the shape `related`/`sources` actually have. Every other string value is prose and
    stays in scope. Masking is in place, so the file's formatting is never reflowed."""
    protected_keys = {key.lower() for key in rules.get("protectedJsonKeys", [])}
    pieces: list[str] = []
    cursor = 0
    for m in JSON_KEY_WITH_COLON.finditer(text):
        key_name = m.group(1)[1:-1].lower()
        if key_name not in protected_keys:
            continue
        span = _json_value_span(text, m.end(2))
        if span is None:
            continue
        start, end = span
        if start < cursor:
            continue
        pieces.append(text[cursor:start])
        pieces.append("\x00" * (end - start))
        cursor = end
        spans.append({"kind": "protected", "_start": start, "_end": end, "text": text[start:end]})
    pieces.append(text[cursor:])
    text = "".join(pieces)
    text = _mask(text, JSON_KEY, "protected", spans)
    return _mask(text, TOKEN_BRACE, "protected", spans)


def _json_value_span(text: str, start: int) -> tuple[int, int] | None:
    """The end offset of the JSON value beginning at `start` (skipping leading whitespace). A
    string-aware bracket scan, so a `]` inside a string does not close an array early."""
    i = start
    while i < len(text) and text[i] in " \t\r\n":
        i += 1
    if i >= len(text) or text[i] in ",]}":
        return None

    if text[i] == '"':
        j = i + 1
        while j < len(text):
            if text[j] == "\\":
                j += 2
                continue
            if text[j] == '"':
                return i, j + 1
            j += 1
        return None

    if text[i] in "[{":
        opener, closer = text[i], "]" if text[i] == "[" else "}"
        depth, j, in_string = 0, i, False
        while j < len(text):
            ch = text[j]
            if in_string:
                if ch == "\\":
                    j += 2
                    continue
                if ch == '"':
                    in_string = False
            elif ch == '"':
                in_string = True
            elif ch == opener:
                depth += 1
            elif ch == closer:
                depth -= 1
                if depth == 0:
                    return i, j + 1
            j += 1
        return None

    j = i
    while j < len(text) and text[j] not in ",]}":
        j += 1
    return i, j


def _protect_html_tags(text: str, rules: dict, spans: list[dict]) -> str:
    """A tag's own markup is code; only `content`, `alt`, `title` and `aria-label` values are prose.
    `href` and every other attribute stay protected (a link target is not prose)."""
    allowed = {name.lower() for name in rules.get("protectedHtmlAttributes", [])}
    pieces: list[str] = []
    cursor = 0
    for tag in HTML_TAG.finditer(text):
        pieces.append(text[cursor:tag.start()])
        cursor = tag.start()
        for attr in HTML_ATTR.finditer(tag.group(0)):
            if attr.group(1).lower() not in allowed:
                continue
            start = tag.start() + attr.start(2) + 1
            end = start + len(attr.group(2)) - 2
            pieces.append("\x00" * (start - cursor))
            pieces.append(text[start:end])
            spans.append({"kind": "allowed-attr", "_start": start, "_end": end,
                          "text": text[start:end]})
            cursor = end
        pieces.append("\x00" * (tag.end() - cursor))
        cursor = tag.end()
    pieces.append(text[cursor:])
    return "".join(pieces)


# --------------------------------------------------------------------------------------
# Findings

class Finding:
    """One report row. A plain class, not a dataclass: the Guard harness loads this module through
    `importlib.util.spec_from_file_location`, and a dataclass there needs the module registered in
    `sys.modules` first — a trap for anyone loading the tool the same way."""

    __slots__ = ("file", "line", "col", "before", "rule", "kind", "after", "reason")

    def __init__(self, file: str, line: int, col: int, before: str, rule: str, kind: str,
                 after: str | None = None, reason: str | None = None) -> None:
        self.file = file
        self.line = line
        self.col = col
        self.before = before
        self.rule = rule
        self.kind = kind
        self.after = after
        self.reason = reason

    def as_row(self) -> dict:
        row = {
            "file": self.file,
            "line": self.line,
            "col": self.col,
            "before": self.before,
            "kind": self.kind,
            "rule": self.rule,
        }
        if self.after is not None:
            row["after"] = self.after
        if self.reason is not None:
            row["reason"] = self.reason
        return row


def _position(text: str, offset: int) -> tuple[int, int]:
    line = text.count("\n", 0, offset) + 1
    line_start = text.rfind("\n", 0, offset) + 1
    return line, offset - line_start + 1


SENTENCE_START_BEFORE = re.compile(r"(?:^|\s)(?:#+|\||[-*+]|\d{1,3}\.)\s*$")
SENTENCE_END_BEFORE = re.compile(r"[.!?]\s+$")
DOUBLED_ARTICLE_BEFORE = re.compile(r"\b(?:the|The|THE|a|A|an|An)\s+$")
FUSION_BEFORE = re.compile(r"\bFusion\s*$")
WORD_CHAR = re.compile(r"[A-Za-z0-9_]")
COMPOUND_SEPARATORS = "-./:_"


def _sentence_start(text: str, line_start: int, start: int) -> bool:
    if start == 0:
        return True
    prefix = text[line_start:start]
    if prefix.strip() == "":
        return True
    if SENTENCE_START_BEFORE.search(prefix):
        return True
    return bool(SENTENCE_END_BEFORE.search(prefix))


def scan_file(rel: str, text: str, phase: dict, rules: dict, findings: list[Finding],
              replacements: list[tuple[int, int, str]]) -> None:
    spans: list[dict] = []
    masked = protect(text, rules, rel, spans)

    for pattern in rules.get("identifierAllowList", []):
        masked = _mask(masked, re.compile("(?<![A-Za-z0-9_])" + pattern + "(?![A-Za-z0-9_])"),
                       "identifier", spans)

    compiled = [(r, re.compile("(?<![A-Za-z0-9_])" + r["match"] + "(?![A-Za-z0-9_])"))
                for r in phase["rules"]]

    candidates: list[tuple[int, int, int, dict, str]] = []
    for order, (rule, pattern) in enumerate(compiled):
        for m in pattern.finditer(masked):
            candidates.append((m.start(), m.end(), order, rule, m.group(0)))
    candidates.sort(key=lambda c: (c[0], -(c[1] - c[0]), c[2]))

    chosen: list[tuple[int, int, int, dict, str]] = []
    last_end = 0
    for candidate in candidates:
        if candidate[0] < last_end:
            continue
        chosen.append(candidate)
        last_end = candidate[1]

    for start, end, _order, rule, before in chosen:
        line, col = _position(text, start)
        replacement = rule["replace"]

        if _is_compound(masked, start, end):
            findings.append(Finding(rel, line, col, before, rule["id"], "residue",
                                    reason="prose-compound"))
            continue

        line_start = text.rfind("\n", 0, start) + 1
        prefix = text[line_start:start]
        if replacement.startswith("the "):
            if DOUBLED_ARTICLE_BEFORE.search(prefix):
                findings.append(Finding(rel, line, col, before, rule["id"], "residue",
                                        reason="doubled-article"))
                continue
            if _sentence_start(text, line_start, start):
                replacement = "The " + replacement[4:]
        if replacement == "Fusion" and FUSION_BEFORE.search(prefix):
            findings.append(Finding(rel, line, col, before, rule["id"], "residue",
                                    reason="fusion-doubling"))
            continue

        findings.append(Finding(rel, line, col, before, rule["id"], "replace", after=replacement))
        replacements.append((start, end, replacement))

    for span in spans:
        if span["kind"] != "identifier":
            continue
        line, col = _position(text, span["_start"])
        findings.append(Finding(rel, line, col, span["text"],
                                _rule_for(phase, span["text"]), "identifier"))


def _is_compound(masked: str, start: int, end: int) -> bool:
    after = masked[end : end + 1]
    if after in COMPOUND_SEPARATORS and WORD_CHAR.match(masked[end + 1 : end + 2] or "\x00"):
        return True
    before = masked[start - 1 : start]
    return before in COMPOUND_SEPARATORS and bool(WORD_CHAR.match(masked[start - 2 : start - 1] or "\x00"))


def _rule_for(phase: dict, text: str) -> str:
    for rule in phase["rules"]:
        if re.search(rule["match"], text):
            return rule["id"]
    return "identifier"


def build_plan(sources: dict[str, str], rules: dict, phase_id: str) -> dict:
    phase = next((p for p in rules["phases"] if p["id"] == phase_id), None)
    if phase is None:
        raise SystemExit(f"vocab-rename: no phase '{phase_id}' in the rules file")

    findings: list[Finding] = []
    changed: dict[str, str] = {}
    for rel in sorted(sources):
        replacements: list[tuple[int, int, str]] = []
        scan_file(rel, sources[rel], phase, rules, findings, replacements)
        if replacements:
            changed[rel] = _splice(sources[rel], replacements)

    ordered = sorted(findings, key=lambda f: (f.file, f.line, f.col, f.rule, f.kind))
    return {
        "schemaVersion": 1,
        "tool": "vocab-rename",
        "phase": phase_id,
        "scanned": len(sources),
        "findings": [f.as_row() for f in ordered],
        "changed": changed,
    }


def _splice(text: str, replacements: list[tuple[int, int, str]]) -> str:
    out: list[str] = []
    cursor = 0
    for start, end, after in sorted(replacements):
        out.append(text[cursor:start])
        out.append(after)
        cursor = end
    out.append(text[cursor:])
    return "".join(out)


def replacements_of(plan: dict) -> list[dict]:
    return [f for f in plan["findings"] if f["kind"] == "replace"]


def residue_of(plan: dict) -> list[dict]:
    return [f for f in plan["findings"] if f["kind"] == "residue"]


def identifiers_of(plan: dict) -> list[dict]:
    return [f for f in plan["findings"] if f["kind"] == "identifier"]


def report_markdown(plan: dict, rules_file: str) -> str:
    lines = [
        f"# vocab-rename report — phase `{plan['phase']}`",
        "",
        f"Rules: `{rules_file}` · files scanned: **{plan['scanned']}** · "
        f"replacements: **{len(replacements_of(plan))}** · "
        f"residue: **{len(residue_of(plan))}** · "
        f"identifiers left alone: **{len(identifiers_of(plan))}**",
        "",
        "## Replacements",
        "",
        "| File | Line | Rule | Before | After |",
        "|---|---|---|---|---|",
    ]
    for f in replacements_of(plan):
        lines.append(f"| `{f['file']}` | {f['line']} | `{f['rule']}` | `{f['before']}` | `{f['after']}` |")
    lines += ["", "## Residue — each one closed by adding a rule, never by editing the tool's output", "",
              "| File | Line | Rule | Text | Reason |", "|---|---|---|---|---|"]
    for f in residue_of(plan):
        lines.append(f"| `{f['file']}` | {f['line']} | `{f['rule']}` | `{f['before']}` | `{f['reason']}` |")
    lines += ["", "## Identifiers left alone (a reading, not a contract)", "",
              "| File | Line | Text |", "|---|---|---|"]
    for f in identifiers_of(plan)[:200]:
        lines.append(f"| `{f['file']}` | {f['line']} | `{f['before']}` |")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------------------
# CLI

def collect_sources(rules: dict, phase_id: str, paths: list[str] | None) -> tuple[dict[str, str], list[str]]:
    phase = next((p for p in rules["phases"] if p["id"] == phase_id), None)
    if phase is None:
        raise SystemExit(f"vocab-rename: no phase '{phase_id}' in the rules file")
    include, exclude = phase["include"], phase.get("exclude", [])

    if paths:
        # An explicit list is an operator override (plan D4): the caller names the files it just
        # changed, which are usually outside the prose scope `apply` walks. The exclude list and the
        # derived rendered set still apply, so a generated page is refused rather than quietly scanned.
        rendered = rendered_outputs(phase)
        selected = [p for p in paths
                    if p not in rendered and not any(glob_to_regex(e).match(p) for e in exclude)]
        missing = [p for p in paths if p not in selected]
    else:
        rendered = rendered_outputs(phase)
        selected = [rel for rel in tracked_files()
                    if rel not in rendered and in_scope(rel, include, exclude)]
        missing = []
    return {rel: read_text(rel) for rel in sorted(selected)}, missing


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="vocab-rename", description=__doc__)
    parser.add_argument("command", choices=["plan", "apply", "check", "census"])
    parser.add_argument("--rules", required=True)
    parser.add_argument("--phase", default=None, help="phase id; default: every phase, in order")
    parser.add_argument("--paths", nargs="*", default=None,
                        help="restrict the scan to these repo-relative paths")
    parser.add_argument("--report-dir", default=None,
                        help="plan only: write <id>-<phase>.journal.json and .md here")
    args = parser.parse_args(argv)

    rules = json.loads(read_text(args.rules))
    phase_ids = [args.phase] if args.phase else [p["id"] for p in rules["phases"]]

    exit_code = 0
    for phase_id in phase_ids:
        sources, missing = collect_sources(rules, phase_id, args.paths)
        if missing:
            print(f"vocab-rename: {len(missing)} path(s) are excluded from phase '{phase_id}': "
                  + ", ".join(missing), file=sys.stderr)
            return 2
        plan = build_plan(sources, rules, phase_id)
        replacements, residue = replacements_of(plan), residue_of(plan)

        if args.command == "check":
            print(f"phase {phase_id}: {len(replacements)} replacement(s), {len(residue)} residue "
                  f"over {plan['scanned']} file(s)")
            for f in residue[:20]:
                print(f"  residue {f['file']}:{f['line']} [{f['rule']}] {f['before']!r} ({f['reason']})")
            if replacements or residue:
                exit_code = 1
            continue

        if args.command == "apply":
            refusing = dirty_files(sorted(plan["changed"]))
            for rel in sorted(refusing):
                plan["changed"].pop(rel, None)
            for rel, text in sorted(plan["changed"].items()):
                write_text(rel, text)
            print(f"phase {phase_id}: wrote {len(plan['changed'])} file(s) "
                  f"for {len(replacements)} replacement(s)")
            if refusing:
                # Never write over another session's uncommitted work (plan D3).
                print(f"phase {phase_id}: refused {len(refusing)} dirty file(s): "
                      + ", ".join(sorted(refusing)), file=sys.stderr)
                exit_code = 1
            continue

        if args.command == "plan":
            report = {
                "schemaVersion": plan["schemaVersion"],
                "tool": plan["tool"],
                "rulesFile": args.rules,
                "phase": phase_id,
                "scanned": plan["scanned"],
                "replacements": replacements,
                "residue": residue,
                "identifiers": identifiers_of(plan),
            }
            if args.report_dir:
                outdir = Path(repo_root()) / args.report_dir
                outdir.mkdir(parents=True, exist_ok=True)
                # `Path.with_suffix` would eat the `.journal` part of the stem, so the suffix is
                # appended to the name instead (found by this tool's own first real run: it wrote
                # `<id>-<phase>.json`, silently dropping the word the report is named for).
                stem = f"{rules['id']}-{phase_id}.journal"
                json_path = outdir / (stem + ".json")
                md_path = outdir / (stem + ".md")
                json_path.write_text(
                    json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
                    encoding="utf-8", newline="")
                md_path.write_text(report_markdown(plan, args.rules), encoding="utf-8", newline="")
                print(f"phase {phase_id}: wrote {json_path} and {md_path}")
            else:
                print(json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False))
            continue

        # census: a reading only — every case-folded hit, replaced or not.
        counts: dict[str, int] = {}
        for value in sources.values():
            for phase in rules["phases"]:
                for rule in phase["rules"]:
                    hits = len(re.findall(rule["match"], value, flags=re.IGNORECASE))
                    if hits:
                        counts[rule["id"]] = counts.get(rule["id"], 0) + hits
        print(json.dumps(counts, indent=2, sort_keys=True))

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
