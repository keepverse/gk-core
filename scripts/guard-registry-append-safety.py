#!/usr/bin/env python3
"""
Guard: a line-cited JSON registry has ONE safe insertion point, and this proves it is still safe.

WHAT THIS IS FOR. Three JSON registries under `scripts/` are cited BY LINE. Measured, 63 citation
INSTANCES naming `verification-boundaries.v1.json`, `enforcement-registry.v1.json` or
`todo-shapes.v1.json`, spread over 36 documents that all live OUTSIDE this repository (`docs/` and
`tasks/`), and they resolve to 49 DISTINCT `<registry>:<line>` keys. The gate works on the 49 keys and
names one citing location for each, exactly as its sibling does, so its own counts are keys and not
instances. A citation of the form `<registry>.v1.json:<line>` is a promise that line still holds what
it held when the sentence was written. Inserting one registry row above that line keeps every citation
syntactically valid and silently repoints all of them at different content.

THIS ALREADY HAPPENED, AND NOTHING GATING CAUGHT IT. A boundary row for a new guard was inserted
next to the other guard rows, which sit early in the `boundaries` array. That array is line-cited 54
times, so an 11-line insertion above the cited lines moved 10 of the 41 distinct cited lines onto the
wrong row. `audit-doc-citations.py` reported it in its D5 class and was NOT gating; the author
caught it by re-running an ungated audit and reverted. The reason no gate saw it is the point:
`guard-citation-stability.py` declares the two indexes it protects in its own `CITED` tuple, so
inserting into a line-cited registry is invisible to the one gate whose entire subject is a line that
moved. A gate cannot see a file it has not been told about.

THE RULE IS ABOUT MEANING, NOT NUMBERS, AND IS THE SAME RULE ITS SIBLING USES. For every citation
found, the CONTENT of the line it points at is fingerprinted and compared with a committed baseline,
so a shift reds whether or not the citation was renumbered. Fingerprints are taken over the
whitespace-COLLAPSED line, never the raw bytes: `.gitattributes` normalises every text file to LF, so
a byte comparison on Windows reports drift that does not exist and hides the drift that does.
Refusing a changed LINE COUNT would be the same defect in a different costume - the count moves for
reasons that misdirect nothing.

THE SAFE APPEND POINT IS DERIVED AND REPORTED, NOT A CONVENTION. For each registry the guard locates
the closing bracket of its append container and requires that bracket to sit BELOW every cited line.
That is a geometric fact about the file, it needs no baseline, and it cannot be re-baselined away. The
guard prints the point on every run - green included - so the number is discoverable from the tool
rather than remembered from a commit message, and it goes RED (S3) if growth ever puts a cited line
at or above the bracket, which is the state where the array has no safe insertion point left.

    S1 CITED-LINE-MOVED    a cited line now holds different content. Names every moved entry.
    S2 CITATION-PAST-END   a cited line is past the end of its file.
    S3 NO-SAFE-APPEND      the append container closes at or above the highest cited line, so an
                           append there would move a cited line. Derived geometry, no baseline.
    S4 NO-CITATIONS        refuses (exit 2) rather than reporting success against an empty set.

CITATIONS ARE COUNTED FROM CODE LINES ONLY, AND THE EXCLUSIONS ARE COUNTED OUT LOUD. A comment is
not an invocation: a docstring that shows the citation shape, a fenced block quoting it, or an HTML
comment hiding it would each add a phantom entry that no edit could ever satisfy. Every match is
therefore classified by the line it came from before it is accepted, and the number rejected is
printed on every run - so a citation that was excluded is visible instead of silently dropped, which
is the failure mode of a filter that hides its own effect.

THE BASELINE IS NOT A CITING DOCUMENT, and that exclusion is load-bearing rather than tidy. The
baseline stores its keys in the citation spelling, so scanning it would make the guard cite itself:
every key it had ever recorded would be re-discovered from its own record, which means a key could
never be reported as no-longer-cited and `--update` could never shrink. `guard-citation-stability.py`
has exactly this defect - its own report names `citation-stability.v1.json` as the citing document for
three of its keys - and it is left alone here rather than silently widened, because changing another
guard's baseline is a different decision with its own proof burden.

FAILS CLOSED. Every prerequisite is a named refusal with exit 2, never a traceback and never a green
run. This guard needs BOTH roots and says which one is missing: the registries are gk-core's own, but
all 63 citation instances live in the workspace root's `docs/` and `tasks/`, so a standalone gk-core
clone can supply the targets and not the citers. It writes nothing except under `--update`, and it
writes with `newline=""` because Python's default text mode translates `\n` to `\r\n` on Windows -
which is how a first version of `--update` left 59 CR bytes in a file `.gitattributes` mandates as LF.
Git normalises them on commit, so the damage is invisible in the blob and permanent in the working
copy.

ONE REGISTRY'S CITATIONS ARE ALREADY WRONG, AND THE GUARD SAYS SO ON EVERY RUN. Measured, not
assumed: all seven `enforcement-registry.v1.json` citations resolve to different content than they
cited, and commit 398a6f2 caused it by inserting a seven-line guard row at line 74 of a file whose
highest cited line is 392. Commit 20fd859 fixed the same mistake's effect on
`verification-boundaries.v1.json` and nobody noticed the other half. Five documents cite those lines
and every one of them lives in the workspace root, so the repair is not this repository's to make.

So the baseline declares them `unverified`, WITH A REASON, and the guard prints that declaration on
every run - green included. A green run therefore means "no FURTHER drift since the baseline", never
"these citations are correct", and the file itself carries the distinction so it cannot be read the
other way by someone who never reads this docstring. An `unverified` entry naming a registry this
guard does not check is refused, because a stale entry would keep printing a debt that no longer
exists and quietly train a reader to ignore the line. FURTHER drift in an unverified registry still
reds exactly as it does in a verified one: the declaration excuses the past, never the future.

USAGE (from gk-core, or anywhere in the workspace):
    python scripts/guard-registry-append-safety.py            # the gate; exit 1 on any finding
    python scripts/guard-registry-append-safety.py --report   # every citation, fingerprint and point
    python scripts/guard-registry-append-safety.py --json     # machine-readable verdict
    python scripts/guard-registry-append-safety.py --root P   # a workspace root other than the resolved one

Exit 0 = no cited line moved and every append container is still safe. Exit 1 = an S1-S3 finding.
Exit 2 = it could not run, and it names the prerequisite that was missing.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import re
import sys
import tokenize

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, workspace_root  # noqa: E402  (the shim must run first)

BASELINE = pathlib.Path(__file__).resolve().parent / "registry-append-safety.v1.json"

#: The checked registries. DECLARED, NOT GLOBBED, and each carries the container new rows go into:
#: adding one to this set must be an explicit, reviewable edit, because a glob would widen a gating
#: guard's reach without anyone deciding to. Measured, not assumed - these are the only three
#: `*.v1.json` registries under scripts/ that ANY line citation anywhere in the workspace names; the
#: other five (`battle-responsibility`, `citation-stability`, `test-shards`, `tuning-domain-denylist`,
#: `vocabulary-mirrors`) have zero, so they carry no such hazard. `None` means the root object, for a
#: registry that is a flat map of entries rather than one appendable array.
REGISTRIES = (
    ("verification-boundaries.v1.json", "scripts/verification-boundaries.v1.json", "boundaries"),
    ("enforcement-registry.v1.json", "scripts/enforcement-registry.v1.json", "invariants"),
    ("todo-shapes.v1.json", "scripts/todo-shapes.v1.json", None),
)

SKIP_DIRS = {".git", "node_modules", "obj", "bin", "__pycache__", "dist", ".kilo", "wwwroot"}
SCANNED_SUFFIXES = {".md", ".py", ".cs", ".ts", ".tsx", ".yml", ".yaml", ".json", ".cfg"}

CITATION = re.compile(
    r"(?P<basename>verification-boundaries\.v1\.json|enforcement-registry\.v1\.json"
    r"|todo-shapes\.v1\.json):(?P<line>\d+)"
)

_FENCE = re.compile(r"^\s*(?:```|~~~)")
_HASH_COMMENT = re.compile(r"^\s*(?:#|//)")
_CSHARP_BLOCK = re.compile(r"/\*|\*/")
_STAR_CONTINUATION = re.compile(r"^\s*\*")


class CannotRun(RuntimeError):
    """A named prerequisite is missing. Never a crash, never a green run, never an empty check."""


def normalise(line: str) -> str:
    """The line's identity for comparison: whitespace collapsed.

    Collapsing rather than comparing bytes is not leniency, it is the only comparison that means
    anything here: `.gitattributes` sets `* text=auto eol=lf`, so a checkout on Windows can carry CRLF
    that a checkout on Linux does not, and a byte comparison would either invent drift on one machine
    or be quietly normalised away on both. Collapsing whitespace also makes a re-indent a non-event,
    which is correct: indentation does not move a line, so it cannot misdirect a citation.
    """
    return " ".join(line.split())


def fingerprint(line: str) -> str:
    return hashlib.sha256(normalise(line).encode("utf-8")).hexdigest()[:16]


# ---------------------------------------------------------------------------------------------
# comment classification: a comment is not an invocation
#
# The unit is a COLUMN BOUNDARY per line, not a line. A line that is half statement and half trailing
# comment - `X = "registry.v1.json:268"  # see the note` - is code up to the `#` and documentation
# after it, and treating the whole line as a comment throws away a real citation. A line-granular mask
# got this exactly backwards, and the control caught it: the planted code line carried a trailing
# comment and was silently excluded, so the positive control read as "a comment is not an invocation"
# when what had happened was "a code line was discarded because it has a comment on it".
#
# So each classifier returns {line number -> the column from which the line stops being code}. A
# value of 0 means the whole line is non-code (a fenced block, a full-line comment, a docstring);
# a larger value means only the tail from that column on is non-code.
# ---------------------------------------------------------------------------------------------

NO_COMMENT = 1 << 30


def _python_comment_columns(text: str) -> "dict[int, int]":
    """{line -> column where non-code starts}, via the real lexer.

    Tokenising rather than pattern-matching is the point: a docstring is a STRING, not a COMMENT, and
    this guard's own module docstring documents the citation shape, so a regex that only understood
    `#` would count its own prose as a citation no edit could ever satisfy.

    A DOCSTRING is a triple-quoted STRING that is the first statement of a module, class or function
    body - not "any triple-quoted string", because a triple-quoted string used as a VALUE is code and
    can legitimately assert something. "First statement of a body" is read off the token stream: the
    first token after the file starts, after an INDENT, or after a DEDENT. A first version excluded
    only MULTI-LINE strings, and the control caught the gap immediately: a one-line docstring was
    counted as an invocation, because being short is not what makes a docstring documentation.

    A single-line string that is NOT a docstring stays counted. Where the line is ambiguous - a
    registry path inside a string used as a value - counting it is the safe direction: a phantom
    citation is visible in the report and one edit removes it, while a missed citation is invisible.
    """
    cols: dict[int, int] = {}
    at_body_start = True

    def mark(start: int, end: int, col: int) -> None:
        for n in range(start, end + 1):
            cols[n] = min(cols.get(n, NO_COMMENT), col)

    try:
        for tok in tokenize.generate_tokens(io.StringIO(text).readline):
            if tok.type in (tokenize.INDENT, tokenize.DEDENT, tokenize.NEWLINE, tokenize.NL,
                            tokenize.ENCODING, tokenize.ENDMARKER):
                if tok.type in (tokenize.INDENT, tokenize.DEDENT, tokenize.ENDMARKER):
                    at_body_start = True
                continue
            if tok.type == tokenize.COMMENT:
                # Only from the `#` onward: the code before it on the same line is still an invocation.
                mark(tok.start[0], tok.start[0], tok.start[1])
                continue
            if tok.type == tokenize.STRING:
                body = tok.string.lstrip("rbufRBUF")
                if body.startswith(('"""', "'''")) and at_body_start:
                    mark(tok.start[0], tok.end[0], 0)
            at_body_start = False
    except (tokenize.TokenError, IndentationError, SyntaxError):
        # A file the lexer cannot read is a file whose comment columns cannot be identified. Falling
        # back to full-line `#` UNDER-counts exclusions, so the fallback shows up in the refused
        # figure the guard prints rather than hiding behind a silent success.
        for n, line in enumerate(text.splitlines(), 1):
            stripped = line.lstrip()
            if stripped.startswith("#"):
                cols[n] = 0
            elif " #" in line:
                cols[n] = line.index(" #") + 1
    return cols


def _markdown_comment_columns(lines: "list[str]") -> "dict[int, int]":
    """Fenced code blocks and HTML comment spans: documentation, not invocation. Whole lines."""
    cols: dict[int, int] = {}
    fence: str | None = None
    in_html = False
    for n, line in enumerate(lines, 1):
        if fence is not None:
            cols[n] = 0
            if fence in line:
                fence = None
            continue
        m = _FENCE.match(line)
        if m:
            cols[n] = 0
            fence = m.group(0).strip()[:3]
            continue
        if in_html:
            cols[n] = 0
            if "-->" in line:
                in_html = False
            continue
        if "<!--" in line:
            cols[n] = 0
            if "-->" not in line.split("<!--", 1)[1]:
                in_html = True
    return cols


def _hash_comment_columns(lines: "list[str]") -> "dict[int, int]":
    """Shell/YAML/CFG `#` comments: a full-line `#`, or a trailing ` #` from that column on."""
    cols: dict[int, int] = {}
    for n, line in enumerate(lines, 1):
        stripped = line.lstrip()
        if stripped.startswith("#"):
            cols[n] = 0
        elif " #" in line:
            cols[n] = line.index(" #") + 1
    return cols


def _brace_comment_columns(lines: "list[str]") -> "dict[int, int]":
    """C#/TS line comments, block comments and leading-`*` continuation lines.

    DOCUMENTED APPROXIMATION: this is a textual scan, not a lexer, so a `//` or `/*` INSIDE a string
    literal is read as the start of a comment. That errs toward excluding a citation, which is the
    safe direction for the same reason as above, and it cannot make a real code-line citation vanish
    unless the line also carries comment-looking text after it.
    """
    cols: dict[int, int] = {}
    in_block = False
    for n, line in enumerate(lines, 1):
        if in_block:
            cols[n] = 0
            if "*/" in line:
                in_block = False
            continue
        if _STAR_CONTINUATION.match(line) or _HASH_COMMENT.match(line):
            cols[n] = 0
            continue
        idx = min((i for i in (line.find("//"), line.find("/*")) if i >= 0), default=-1)
        if idx >= 0:
            after = line[idx + 2:]
            cols[n] = idx
            if not after.strip() or after.lstrip().startswith("*/"):
                in_block = True
    return cols


def comment_columns(path: pathlib.Path, text: str) -> "dict[int, int]":
    """{line -> column where non-code starts} for `text`; empty when the format has no comments."""
    suffix = path.suffix.lower()
    lines = text.splitlines()
    if suffix == ".py":
        return _python_comment_columns(text)
    if suffix == ".md":
        return _markdown_comment_columns(lines)
    if suffix in {".cs", ".ts", ".tsx"}:
        return _brace_comment_columns(lines)
    if suffix in {".yml", ".yaml", ".cfg"}:
        return _hash_comment_columns(lines)
    return {}  # .json has no comment syntax, so nothing in it is a comment


# ---------------------------------------------------------------------------------------------
# collection
# ---------------------------------------------------------------------------------------------

def candidate_files(ws: pathlib.Path) -> "list[pathlib.Path]":
    """Every tracked-ish text file that could carry a citation, deduplicated by real path.

    This guard's OWN baseline is excluded, and only it. It stores its keys in the citation spelling,
    so including it would let the guard cite itself: every key it ever recorded would be re-found in
    its own record, a key could therefore never be reported as no-longer-cited, and `--update` could
    never shrink the set. One file, named as a constant, rather than a rule about "generated data" that
    would quietly drop real citations out of documents that happen to look machine-written.
    """
    seen: dict[str, pathlib.Path] = {}
    baseline_key = BASELINE.resolve()
    for p in sorted(ws.rglob("*")):
        if not p.is_file() or p.suffix not in SCANNED_SUFFIXES:
            continue
        if any(part in SKIP_DIRS for part in p.parts):
            continue
        try:
            resolved = p.resolve()
        except OSError:
            continue
        if resolved == baseline_key:
            continue
        seen.setdefault(str(resolved).lower(), p)
    return list(seen.values())


def collect(ws: pathlib.Path) -> "tuple[dict[str, tuple[str, int]], dict[str, int]]":
    """`basename:line` -> (citing file, citing line), plus the count of comment-line matches refused.

    The second element of the return value is the point of the filter being visible: a citation
    excluded because it sat in a comment is reported every run, so moving a real citation into a
    comment to silence the gate shows up as a changed exclusion count rather than as a quiet pass.
    """
    found: dict[str, tuple[str, int]] = {}
    refused: dict[str, int] = {name: 0 for name, _, _ in REGISTRIES}
    for p in candidate_files(ws):
        try:
            text = p.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        cols = comment_columns(p, text)
        for m in CITATION.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            col = m.start() - (text.rfind("\n", 0, m.start()) + 1)
            name = m.group("basename")
            if col >= cols.get(line, NO_COMMENT):
                refused[name] += 1
                continue
            key = f"{name}:{int(m.group('line'))}"
            if key not in found:
                found[key] = (str(p.relative_to(ws)).replace("\\", "/"), line)
    return found, refused


def closing_lines(text: str) -> "dict[str, int]":
    """Line number of each top-level container's closing bracket, tracking JSON string state.

    Counting brackets without tracking strings would break on any value containing one, so the scan
    carries `instr`/`esc` and only counts brackets outside string literals.
    """
    depth = 0
    instr = False
    esc = False
    cur: str | None = None
    out: dict[str, int] = {}
    for n, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if depth == 1 and stripped.startswith('"'):
            cur = stripped.split('"')[1]
        for ch in line:
            if instr:
                if esc:
                    esc = False
                elif ch == "\\":
                    esc = True
                elif ch == '"':
                    instr = False
                continue
            if ch == '"':
                instr = True
            elif ch in "[{":
                depth += 1
            elif ch in "]}":
                depth -= 1
                if depth == 1 and cur is not None:
                    out[cur] = n
                elif depth == 0:
                    out["<root>"] = n
    return out


def geometry(name: str, rel: str, container: "str | None", core: pathlib.Path) -> dict:
    """The registry's line count and the line its single safe append point sits on."""
    path = core / rel
    if not path.is_file():
        raise CannotRun(f"the checked registry does not exist: {path}")
    text = path.read_text(encoding="utf-8")
    closes = closing_lines(text)
    key = "<root>" if container is None else container
    close = closes.get(key)
    if close is None:
        raise CannotRun(
            f"{rel} has no top-level {'object' if container is None else 'array'} named "
            f"{key!r}, so its safe append point cannot be located. Declaring a container this "
            f"registry does not have would make the guard check nothing and report success."
        )
    return {"registry": name, "rel": rel, "container": key, "lines": len(text.splitlines()),
            "close": close}


def ambiguous_cited_lines(body: "list[str]", cited: "list[int]") -> "list[int]":
    """Cited lines whose normalised text occurs more than once in the file.

    REPORTED, NOT SILENTLY ACCEPTED. A content fingerprint cannot separate two lines that hold the
    same text, so a shift that lands a cited line on an identical line is invisible to S1 - and that
    is the whole limit of this mechanism, measured rather than described. It is benign in the case
    that actually arises, because a reader following such a citation reads the same characters either
    way, but a guard must not claim a coverage it does not have. The alternative - fingerprinting a
    WINDOW around the cited line - was rejected deliberately: it would make an edit to the lines BELOW
    a cited line red, and an edit below a citation misdirects nothing, so it would refuse harmless
    work and teach people to re-baseline to get changes in. That is the defect the sibling's own
    docstring names when it declines to refuse a changed line COUNT.
    """
    counts: dict[str, int] = {}
    for line in body:
        key = normalise(line)
        counts[key] = counts.get(key, 0) + 1
    return [n for n in cited if 1 <= n <= len(body) and counts.get(normalise(body[n - 1]), 0) > 1]


# ---------------------------------------------------------------------------------------------
# the gate
# ---------------------------------------------------------------------------------------------

def evaluate(core: pathlib.Path, ws: pathlib.Path) -> dict:
    citations, refused = collect(ws)
    geos = [geometry(name, rel, container, core) for name, rel, container in REGISTRIES]

    current: dict[str, str | None] = {}
    bodies: dict[str, "list[str]"] = {}
    for name, rel, _ in REGISTRIES:
        bodies[name] = (core / rel).read_text(encoding="utf-8").splitlines()
    for key in citations:
        name, _, raw = key.rpartition(":")
        n = int(raw)
        body = bodies[name]
        current[key] = fingerprint(body[n - 1]) if 1 <= n <= len(body) else None

    findings: list[dict] = []
    for geo in geos:
        cited = sorted(int(k.rpartition(":")[2]) for k in citations if k.startswith(geo["registry"] + ":"))
        geo["citations"] = sum(1 for k in citations if k.startswith(geo["registry"] + ":"))
        geo["distinct_lines"] = len(cited)
        geo["max_cited"] = cited[-1] if cited else 0
        geo["ambiguous_lines"] = ambiguous_cited_lines(bodies[geo["registry"]], cited)
        # S3 - the geometric invariant, derived from the file and independent of any baseline.
        if geo["max_cited"] >= geo["close"]:
            findings.append(dict(
                code="S3-NO-SAFE-APPEND-POINT", registry=geo["registry"],
                note=f"the append container {geo['container']!r} closes at line {geo['close']} and the "
                     f"highest cited line is {geo['max_cited']}, so there is NO line at which a row can "
                     f"be added without moving a cited line. Every citation of this registry must be "
                     f"re-pointed deliberately, or the registry must stop being cited by number."))

    return {"citations": citations, "current": current, "geometries": geos,
            "findings": findings, "refused": refused}


def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Refuse a change that moved a line some document cites in a JSON registry by number.")
    parser.add_argument("--report", action="store_true",
                        help="print every citation, its fingerprint and its registry's safe append point")
    parser.add_argument("--update", action="store_true",
                        help="re-baseline, PRINTING every key added, removed or changed")
    parser.add_argument("--json", action="store_true", help="print the machine-readable verdict")
    parser.add_argument("--root", default="", help="workspace root (default: the resolved workspace root)")
    args = parser.parse_args(argv)

    core = pathlib.Path(__file__).resolve().parent.parent
    try:
        ws = pathlib.Path(args.root).resolve() if args.root else pathlib.Path(workspace_root())
    except RootNotFound as exc:
        print(f"REFUSING REGISTRY-APPEND-SAFETY: all {len(REGISTRIES)} checked registries are cited "
              f"from the workspace root's docs/ and tasks/, which this clone cannot see: {exc}",
              file=sys.stderr)
        print("  Set KEEPVERSE_WORKSPACE_ROOT, or run inside the workspace. The registries themselves "
              "are gk-core's, but a citation with no citing document is not a citation.", file=sys.stderr)
        return 2

    try:
        result = evaluate(core, ws)
    except CannotRun as exc:
        print(f"REFUSING REGISTRY-APPEND-SAFETY: {exc}", file=sys.stderr)
        return 2

    citations = result["citations"]
    current = result["current"]
    geos = result["geometries"]
    findings = result["findings"]

    # S4 - the dangerous state: a pattern stopped matching, or every citer was filtered out as a
    # comment. A guard that reads zero citations reports success while checking nothing.
    if not citations:
        print("REFUSING REGISTRY-APPEND-SAFETY: ZERO citations found. Refusing rather than reporting a "
              "clean run against an empty set - either the pattern stopped matching or every match sat "
              "on a comment line.", file=sys.stderr)
        return 2

    geo_by_name = {g["registry"]: g for g in geos}
    if not BASELINE.is_file():
        if not args.update:
            print(f"REFUSING: no baseline at {BASELINE}. Run with --update to take one deliberately.",
                  file=sys.stderr)
            return 2
        BASELINE.write_text(json.dumps({"fingerprints": current, "unverified": []},
                                       indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        print(f"re-baselined {len(current)} citation(s) -> {BASELINE.name}")
        return 0

    try:
        stored = json.loads(BASELINE.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"REFUSING: the baseline is not valid JSON: {exc}", file=sys.stderr)
        return 2
    if not isinstance(stored, dict) or "fingerprints" not in stored:
        print(f"REFUSING: the baseline has no `fingerprints` object. Its shape is part of the "
              f"contract: `unverified` sits beside it so the debt declaration cannot be mistaken for "
              f"a fingerprint.", file=sys.stderr)
        return 2
    baseline = stored["fingerprints"]
    if not isinstance(baseline, dict):
        print("REFUSING: `fingerprints` is not an object", file=sys.stderr)
        return 2

    # The debt declaration is validated, never trusted: an entry naming a registry this guard does not
    # check would keep printing a claim about nothing.
    unverified = stored.get("unverified") or []
    for entry in unverified:
        name = str(entry.get("registry", ""))
        if name not in geo_by_name:
            print(f"REFUSING: the baseline's `unverified` entry names {name!r}, which is not one of "
                  f"the registries this guard checks ({', '.join(sorted(geo_by_name))}). A stale entry "
                  f"would print a debt that no longer exists.", file=sys.stderr)
            return 2
        if not str(entry.get("why", "")).strip():
            print(f"REFUSING: the baseline's `unverified` entry for {name} states no reason. An "
                  f"excuse with no stated cause is indistinguishable from a suppression.", file=sys.stderr)
            return 2

    if args.update:
        changed = [k for k in sorted(current) if baseline.get(k) != current[k]]
        gone = sorted(k for k in baseline if k not in current)
        added = sorted(k for k in changed if k not in baseline)
        modified = [k for k in changed if k in baseline]
        # `unverified` is carried across untouched. --update re-takes fingerprints; it has no business
        # clearing a debt declaration, or a debt could be erased by re-baselining - which is the one
        # thing a re-baseline must never be able to do.
        stored["fingerprints"] = current
        BASELINE.write_text(json.dumps(stored, indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        print(f"re-baselined {len(current)} citation(s) -> {BASELINE.name}")
        print(f"  added   {len(added)}: {', '.join(added) if added else '(none)'}")
        print(f"  changed {len(modified)}: {', '.join(modified) if modified else '(none)'}")
        print(f"  removed {len(gone)}: {', '.join(gone) if gone else '(none)'}")
        print(f"  unverified declarations carried across unchanged: "
              f"{', '.join(str(e.get('registry')) for e in unverified) if unverified else '(none)'}")
        if modified:
            print("  A CHANGED fingerprint means the cited line's CONTENT differs from what it held when "
                  "it was cited. Read each one above and confirm the citing sentence still means what it "
                  "says before accepting this baseline; re-baselining a moved line is how a citation "
                  "becomes a lie that no gate can see.")
        return 0

    gone = sorted(k for k in baseline if k not in current)

    for key in sorted(current):
        was = baseline.get(key)
        if was is None:
            where = citations[key]
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=key.rpartition(":")[0],
                                note=f"{key} is a NEW citation with no baseline entry "
                                     f"(cited from {where[0]}:{where[1]})"))
        elif current[key] is None:
            where = citations[key]
            findings.append(dict(code="S2-CITATION-PAST-END", registry=key.rpartition(":")[0],
                                note=f"{key} points PAST THE END of its file "
                                     f"(cited from {where[0]}:{where[1]})"))
        elif current[key] != was:
            where = citations[key]
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=key.rpartition(":")[0],
                                note=f"{key} now holds different content (cited from {where[0]}:{where[1]}) "
                                     f"- the line moved, or the row above it grew"))

    if args.report:
        print(f"REGISTRY APPEND SAFETY REPORT - {len(citations)} citation(s) over "
              f"{len({w[0] for w in citations.values()})} document(s)")
        for g in geos:
            print(f"\n  {g['rel']}  ({g['lines']} lines)")
            print(f"    append container : {g['container']} closes at line {g['close']}")
            print(f"    SAFE APPEND POINT: insert immediately before line {g['close']}")
            print(f"    cited            : {g['citations']} citation(s), {g['distinct_lines']} distinct "
                  f"line(s), highest {g['max_cited']}")
            print(f"    comment matches refused: {result['refused'][g['registry']]}")
            print(f"    not unique in file: {len(g['ambiguous_lines'])} of {g['distinct_lines']} cited "
                  f"line(s) - a shift onto an identical line is not detectable by content: "
                  f"{', '.join(str(n) for n in g['ambiguous_lines']) if g['ambiguous_lines'] else '(none)'}")
        for key in sorted(citations, key=lambda k: (k.rpartition(":")[0], int(k.rpartition(":")[2]))):
            where = citations[key]
            print(f"    {key:<48} {current[key] or 'PAST END OF FILE':<18} {where[0]}:{where[1]}")
        if gone:
            print(f"\n  baseline entries no longer cited anywhere ({len(gone)}): {', '.join(gone)}")
        for entry in unverified:
            print(f"\n  UNVERIFIED {entry['registry']}: {entry['why']}")
        return 0

    if args.json:
        print(json.dumps({"guard": "registry-append-safety",
                          "ok": not findings,
                          "geometries": geos,
                          "unverified": unverified,
                          "commentMatchesRefused": result["refused"],
                          "citations": len(citations),
                          "findings": findings}, indent=2, ensure_ascii=False))
        return 1 if findings else 0

    for g in geos:
        print(f"  {g['rel']}: {g['citations']} citation(s) over {g['distinct_lines']} distinct line(s), "
              f"highest {g['max_cited']} of {g['lines']}; safe append = before line {g['close']} "
              f"({g['container']}); {result['refused'][g['registry']]} comment match(es) refused; "
              f"{len(g['ambiguous_lines'])}/{g['distinct_lines']} cited line(s) hold text that is not "
              f"unique in the file, so a shift onto an identical line is invisible to S1 by content")

    # The debt declaration is printed on EVERY run, green included. It is the only thing standing
    # between "this registry's citations are correct" and "this registry's citations have not drifted
    # further since the baseline", and those are different claims.
    for entry in unverified:
        print(f"  (UNVERIFIED) {entry['registry']}: its cited lines were ALREADY resolving to the wrong "
              f"content before this baseline was taken, so this guard's green run means no FURTHER "
              f"drift, not that the citations are right. Reason: {entry['why']}")

    # A stale baseline key is a NOTE, not a finding: a citation removed on purpose is an edit, not a
    # defect. It is printed because it is the observable proof that the baseline is not being read as a
    # citing document. If this line could never print, the self-reference would be back.
    for key in gone:
        print(f"  (note) baseline entry {key} is no longer cited anywhere - it was removed on purpose; "
              f"--update drops it")

    if not findings:
        print(f"REGISTRY APPEND SAFETY GUARD OK - {len(citations)} citation(s), no cited line moved, and "
              f"every append container still closes below every line cited from it")
        return 0

    by_code: dict[str, int] = {}
    for f in findings:
        by_code[f["code"]] = by_code.get(f["code"], 0) + 1
    print("\nREGISTRY APPEND SAFETY GUARD FAILED:")
    for code, n in sorted(by_code.items()):
        print(f"  {code:28} {n}")
    print()
    for f in findings:
        print(f"  {f['code']:28} {f['registry']}")
        print(f"      {f['note']}")
    print("\nEvery one of these is one insertion point. Move the new row to the safe append point its "
          "registry printed above, or re-point the affected citations deliberately and re-baseline.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
