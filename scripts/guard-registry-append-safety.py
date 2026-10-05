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

THE RULE IS ABOUT MEANING, NOT NUMBERS, AND IT HAS TWO AXES BECAUSE ONE IS NOT ENOUGH. For every
citation found, TWO things are fingerprinted against a committed baseline: the CONTENT of the line
it points at, and the ROW that line belongs to. Content alone was the whole rule until commit
6a27e30f9 demonstrated that it is not a rule at all.

WHAT CONTENT COMPARISON CANNOT SEE, MEASURED NOT ASSUMED. `enforcement-registry.v1.json` holds 13
byte-identical `      "paths": [` lines and 31 byte-identical `      "tier": "ci",` lines, and two of
the seven cited lines of that file are one of those twins. A shift that lands a cited line on an
identical line produces identical text, so the content fingerprint matches and the guard concludes
nothing moved. It is right about the text and wrong about the meaning. Commit 6a27e30f9 added a
seven-line `guards` row, which moved `enforcement-registry.v1.json:392` from the `paths` line of
exemption `regen-class-system-baselines` to the `paths` line of exemption `smoke-player-pack` - both
byte-identical - and this guard reported OK. That is the defect this axis now closes.

THE ROW AXIS. The row is the pair of JSON elements a line spans: the one open when the line's first
non-whitespace character is read, and the one open when its last is. The PAIR, because a JSON row is
written open-brace-first and close-brace-last: the opening `    {` of a boundary belongs both to the
556-row `boundaries` array and to the boundary it opens, and the closing `    },` belongs both to the
boundary it closes and to the array it returns to. Taking either endpoint alone under-identifies one
of those two cases - every opening line of a 556-row array would report the same row, which is the
defect repeated. The pair is UNIQUE BY CONSTRUCTION: a map key names one member and an array index
names one element, so no two rows in a file can share a path. That is the property the content
fingerprint never had, and it is why this axis can certify a line whose text occurs 13 times.

THE ROW IS A POSITION *AND* AN IDENTITY, AND THE POSITION ALONE IS NOT ENOUGH. A first version
fingerprinted the path pair alone and was caught by its own mutation control. Duplicating the
boundary row ten lines above a cited line lands that line on a byte-identical twin AND in array
index 6 exactly as before, because the content and the index shift by the same amount - the copy
takes index 5 and pushes the cited row to index 7, so line 679 names index 6 either way and now
means the PREVIOUS row. So the fingerprint carries the row's declared identity (`id`,
`verificationId`, `script`, ...) as well as its position. Position plus identity is what separates
the row that was there from the row that is there now.

WHAT THE ROW AXIS DELIBERATELY DOES NOT DO. It is not a window. It does not fingerprint a row's
CONTENTS, so editing a row below a cited line stays green - that edit misdirects nothing. It DOES
fingerprint a row's identity, so renaming a row in place reds. That is a deliberate cost and not an
oversight: a document that cites `registry.v1.json:679` is pointing at a specific row, so changing
what that row is called is a change to what the citation means. The known false positive is a cited
row renamed with no other edit, which a reader resolves by re-pointing the citing sentence - and which
no fingerprint of the present could let through unnoticed anyway. Refusing a non-unique TEXT would
have been the third option and was REJECTED: it would go red on 38 of 49 citations, none of which
would then be repairable here, and a gate that cannot go green is a gate people re-baseline.

THE ROW BASELINE IS TAKEN FROM EACH REGISTRY'S FIRST COMMITTED STATE, NEVER FROM THE WORKING TREE.
This is the only bootstrap that does not accept an unreviewed citation. No citing document in this
workspace has datable per-line history - the workspace root is a single `Import snapshot from legacy
repo` commit - so the guard cannot ask WHEN a sentence was written. It can ask the stronger, date-free
question: has this line EVER belonged to a different row? A citation cannot predate the file that
holds it, so the file's first commit is the earliest a citation could have been written, and a row
that has not changed since then satisfies the citation for every date it could have been written. The
provenance commit is recorded in the baseline as `rowBaselineSource`, and `--update` structurally
cannot write this axis, so `--update` is not a re-baselining escape hatch for the very thing this axis
exists to catch. `--adopt-rows` re-derives it, and refuses on a shallow clone rather than silently
adopting the tip.

THE RESIDUAL LIMIT, STATED BECAUSE IT IS REAL. "First commit" is the earliest state THIS repository
retains. Both repositories were imported on 2026-09-30 from a legacy repo whose history is not here,
so drift that happened before that import is invisible to this axis and to the content axis alike.
The axis proves a row has not changed since 2026-09-30; it cannot prove anything about 2026-09-29.

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
    S5 CITED-ROW-CHANGED   a cited line now belongs to a different row. Fires whether or not the
                           content changed, which is the entire point: it is the axis that sees the
                           byte-identical twin. An `unverified` declaration does NOT excuse it,
                           because a debt declaration is a statement about the past.

REFUSES CLOSED, NEVER SILENTLY CERTIFIES. Beyond the roots and the empty set: a checked registry
that is not valid JSON, a cited line with no derivable row (an unparseable file, or a line past the
end where S2 does not already speak), a baseline with no `rows` axis, and a `rows` axis with no
recorded provenance. Each is a named exit 2. A guard that cannot resolve a row has no opinion about
whether the row moved, and reporting OK would be a claim it cannot support.

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

HOW MUCH IS ALREADY WRONG, MEASURED IN THIS REVISION, AND THE ANSWER IS NOT 7. The old text of this
docstring said all seven `enforcement-registry.v1.json` citations were wrong and that this was the
extent of it. Measured against each registry's first commit with the row axis, and with the content
axis, 35 of the 49 citations sit on a line that has left the row it held on 2026-09-30:

    7   enforcement-registry.v1.json      - all seven, as previously declared
   28   verification-boundaries.v1.json   - NOT previously declared, and not covered by any gate
    0   todo-shapes.v1.json               - one citation, on a file with a single committed state

Ten of the 28 changed ROW while holding the same array index, which is the case the row's identity
token was added for and which a path-only axis misses too.

The 28 were invisible for a compound reason: the citing documents predate the guard, the registry grew
many times, and the boundary rows they name were inserted and later removed again by 20fd859 - so the
cited line ends where it began while having been somewhere else in between. A line that was ever
occupied by a different row is occupied by that row for a writer who read it then, and no fingerprint
of the present can speak for that period. Every one of the 35 is a citation whose repair is a
workspace-root document edit, so it is not this repository's to make.

The `unverified` declaration for `enforcement-registry.v1.json` is KEPT and is not superseded by the
row axis: it records that those citations were wrong BEFORE any baseline existed, which is a fact
about the past the row axis cannot record because it only compares against 2026-09-30. It is also no
longer load-bearing for anything: the row axis now reds those same seven without being asked to, and
it will red them again if they drift further.

A green run therefore means "no cited line has moved or changed row since the recorded baselines",
never "these citations are correct", and the file itself carries that distinction so it cannot be
read the other way by someone who never reads this docstring. FURTHER drift in an unverified registry
reds exactly as it does in a verified one: the declaration excuses the past, never the future.

USAGE (from gk-core, or anywhere in the workspace):
    python scripts/guard-registry-append-safety.py            # the gate; exit 1 on any finding
    python scripts/guard-registry-append-safety.py --report   # every citation, fingerprint and point
    python scripts/guard-registry-append-safety.py --json     # machine-readable verdict
    python scripts/guard-registry-append-safety.py --root P   # a workspace root other than the resolved one
    python scripts/guard-registry-append-safety.py --adopt-rows   # re-derive the ROW axis from history

Exit 0 = no cited line moved or changed row, and every append container is still safe. Exit 1 = an
S1-S3 or S5 finding. Exit 2 = it could not run, and it names the prerequisite that was missing.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import re
import subprocess
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

#: registry name -> the repository-relative file that holds it, from REGISTRIES. Used to name a
#: provenance commit in a finding without repeating the lookup at three call sites.
REL_OF = {name: rel for name, rel, _container in REGISTRIES}


def rel_of(key: str) -> str:
    """The repository-relative file a citation key names. Never a KeyError: an unknown key names
    nothing, and the caller's default is the honest answer."""
    return REL_OF.get(key.rpartition(":")[0], "the registry")


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
# the row axis: what the content fingerprint structurally cannot see
#
# A content fingerprint separates two lines only when their texts differ. A registry of
# structurally identical rows therefore guarantees a cited line whose twin exists, and a shift onto
# that twin is invisible BY CONSTRUCTION. Measured: enforcement-registry.v1.json holds 13
# byte-identical `      "paths": [` lines and 31 byte-identical `      "tier": "ci",` lines, and two
# of its seven cited lines are one of those twins.
#
# The row axis compares the row a cited line belongs to instead of the text it holds. The row is the
# PAIR of JSON elements the line spans - the one open when its first non-whitespace character is read
# and the one open when its last is - and the pair is unique by construction, because a map key names
# exactly one member and an array index names exactly one element.
# ---------------------------------------------------------------------------------------------

_JSON_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "/": "/", "b": "\b", "f": "\f"}


def _read_json_string(text: str, i: int) -> "tuple[str, int]":
    """`text[i]` is `"`. Returns (decoded value, index just past the closing quote)."""
    out: list[str] = []
    j = i + 1
    n = len(text)
    while j < n:
        c = text[j]
        if c == "\\":
            nxt = text[j + 1] if j + 1 < n else ""
            out.append(_JSON_ESCAPES.get(nxt, nxt))
            j += 2
            continue
        if c == '"':
            return "".join(out), j + 1
        out.append(c)
        j += 1
    raise CannotRun("a registry holds an unterminated JSON string, so its rows cannot be resolved")


def row_identity(text: str) -> "dict[int, tuple[str, str]]":
    """{line -> (element open at the line's start, element open at the line's end)}.

    A single character scanner over the raw text rather than a parse, because the answer needed is
    WHICH ELEMENT OWNS A LINE NUMBER, and a parser discards line numbers. It tracks JSON string state
    so a brace inside a string is not a brace, exactly as `closing_lines` does for the append point -
    two scanners in one guard, over the same file, is a shape a reader can check.

    A line's own extent is its identity, so a blank line inherits the previous line's row and a line
    of pure whitespace is not a gap in the map. Both endpoints are returned because a JSON row opens
    before it closes: `    {` must identify the element it OPENS, and `    },` the element it CLOSES,
    and neither endpoint alone says both.
    """
    frames: "list[list]" = []          # [kind, slot, members_consumed]
    out: "dict[int, tuple[str, str]]" = {}
    pending_key: "str | None" = None
    mode = "value"
    line = 1
    at_bol = True
    start_path: "str | None" = None
    previous_end = "<root>"
    i = 0
    n = len(text)

    def path_now() -> str:
        segs = []
        for depth, (_kind, slot, _consumed) in enumerate(frames):
            segs.append("<root>" if depth == 0
                        else (slot if isinstance(slot, str) else f"[{slot}]"))
        return "/".join(segs) or "<root>"

    def consume_slot() -> None:
        if not frames:
            return
        parent = frames[-1]
        if parent[0] == "arr":
            parent[2] += 1
        elif pending_key is None:
            parent[2] += 1

    def mode_after_value() -> str:
        return "key_or_end" if (frames and frames[-1][0] == "obj") else "value"

    while i < n:
        ch = text[i]
        if at_bol:
            if ch in " \t\r\n":
                if ch == "\n":
                    out[line] = (previous_end, previous_end)
                    line += 1
                i += 1
                continue
            start_path = path_now()
            at_bol = False
        if ch == "\n":
            end = path_now()
            out[line] = (start_path or previous_end, end)
            previous_end = end
            line += 1
            at_bol = True
            start_path = None
            i += 1
            continue
        if mode == "key_or_end":
            if ch == "}":
                frames.pop()
                mode = "done" if not frames else mode_after_value()
                i += 1
                continue
            if ch == ",":
                pending_key = None
                mode = "key_or_end"
                i += 1
                continue
            if ch == '"':
                key, after = _read_json_string(text, i)
                j = after
                while j < n and text[j] in " \t\r\n":
                    j += 1
                if j >= n or text[j] != ":":
                    raise CannotRun(
                        f"a registry holds a JSON object member that is not a key/value pair at "
                        f"line {line}, so its rows cannot be resolved")
                pending_key = key
                mode = "value"
                i = j + 1
                continue
            i += 1
            continue
        if ch in "{[":
            slot = (frames[-1][2] if frames[-1][0] == "arr" else pending_key) if frames else None
            if frames:
                frames[-1][2] += 1
            frames.append(["arr" if ch == "[" else "obj", slot, 0])
            mode = "value" if ch == "[" else "key_or_end"
            pending_key = None
            i += 1
            continue
        if ch in "}]":
            frames.pop()
            mode = "done" if not frames else mode_after_value()
            pending_key = None
            i += 1
            continue
        if ch == '"':
            _value, after = _read_json_string(text, i)
            consume_slot()
            pending_key = None
            mode = mode_after_value()
            i = after
            continue
        if ch in ", \t\r":
            i += 1
            continue
        j = i
        while j < n and text[j] not in ",}]\n \t\r":
            j += 1
        if j == i:
            raise CannotRun(f"a registry holds an unparseable character {ch!r} at line {line}")
        consume_slot()
        pending_key = None
        mode = mode_after_value()
        i = j
    return out


def row_label(pair: "tuple[str, str]") -> str:
    """A short human name for a row pair, for the report. Never compared, never fingerprinted."""
    start, end = pair
    if start == end:
        segs = start.split("/")
        named = [s for s in segs[1:] if not s.startswith("[")]
        idx = "".join(s for s in segs[1:] if s.startswith("["))
        return ("/".join(named[1:]) or (named[0] if named else "<root>")) + idx
    return f"{start.split('/')[-1]} .. {end.split('/')[-1]}"


#: Fields that name a row, most specific first. A row's token is what makes two DIFFERENT rows with
#: the same array index distinguishable, which the index alone cannot do.
IDENTITY_FIELDS = ("id", "verificationId", "script", "guard", "name", "project", "test")


def row_token(doc: "object", pair: "tuple[str, str]") -> str:
    """The identity of the row `pair` names: the DEEPEST indexed element's declared id, else the
    deepest map key.

    WHY THE INDEX ALONE IS NOT ENOUGH, MEASURED. Duplicating the boundary row ten lines above a cited
    line lands that line on a byte-identical twin - and lands it in array index 6 exactly as before,
    because the content and the index shift by the same amount. An index-only fingerprint is blind to
    that mutation too, which is why the token is here and not optional: index plus identity is what
    separates "the row that was there" from "the row that is there now".

    The DEEPEST indexed element, not the outermost: `boundaries` is a map member whose own token would
    be the useless word "boundaries", while `boundaries[6]`'s `id` names the row a reader means.
    """
    segs = [s for s in pair[1].split("/") if s and s != "<root>"]
    node = doc
    best = ""
    for seg in segs:
        m = re.fullmatch(r"\[(\d+)\]", seg)
        try:
            if m:
                if not isinstance(node, list) or int(m.group(1)) >= len(node):
                    return best
                node = node[int(m.group(1))]
            else:
                if not isinstance(node, dict) or seg not in node:
                    return best
                node = node[seg]
        except (TypeError, ValueError):
            return best
        if isinstance(node, list):
            continue
        if isinstance(node, dict):
            if m is not None:
                for f in IDENTITY_FIELDS:
                    if isinstance(node.get(f), str):
                        best = f"{f}={node[f]}"
                        break
                else:
                    best = f"[{m.group(1)}]"
            else:
                best = seg
    return best


def row_fingerprint(pair: "tuple[str, str]", token: str) -> str:
    return hashlib.sha256(("\u0000".join((*pair, token))).encode("utf-8")).hexdigest()[:16]


def row_fingerprints(text: str, doc: "object") -> "dict[int, str]":
    """{line -> row fingerprint} for every line of one registry."""
    identities = row_identity(text)
    return {n: row_fingerprint(pair, row_token(doc, pair))
            for n, pair in identities.items()}


def _git(core: pathlib.Path, *args: str) -> "str | None":
    """Run git in `core`; None on any failure. Never raises: the caller turns None into a refusal."""
    try:
        proc = subprocess.run(["git", *args], cwd=str(core), capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=120)
    except (OSError, subprocess.SubprocessError):
        return None
    return proc.stdout if proc.returncode == 0 else None


def rows_preflight(core: pathlib.Path) -> None:
    """Refuse BEFORE anything is written if the row axis cannot be derived soundly.

    Split from `adopt_rows` so `--adopt-rows` never creates a seed baseline on its way to refusing.
    A refusal that writes a file is not a refusal; it is a partial adoption, and the next reader finds
    a baseline with an empty `rows` object and has to work out which half of the run produced it.
    """
    shallow = _git(core, "rev-parse", "--is-shallow-repository")
    if shallow is None:
        raise CannotRun(
            "git is unavailable or this is not a repository, so the row axis cannot be derived from "
            "registry history. Refusing: deriving it from the working tree instead would record the "
            "present as the promise, which is the re-baseline this axis exists to prevent.")
    if shallow.strip() == "true":
        raise CannotRun(
            "this is a SHALLOW clone, so `git log --reverse` returns the tip rather than each "
            "registry's first commit, and adopting from it would record the present as the first "
            "state - a green row axis over real drift. Fetch the full history of these three files, "
            "or keep the committed `rows` axis and do not re-derive it here.")


def adopt_rows(core: pathlib.Path, keys_by_registry: "dict[str, list[str]]") -> "dict[str, str]":
    """Derive the ROW axis from each registry's FIRST committed state: {registry: {key: fingerprint}}.

    Why first, and never the working tree. A citation is a promise that a line holds what it held when
    the sentence was written; a citation cannot predate the file that holds it, so the file's first
    commit is the earliest state a citation could have been written against. A row that has not changed
    since then satisfies every citation that could exist. Deriving from the working tree instead would
    record whatever a drifted file happens to hold today as the promise, which is re-baselining with
    extra steps - and it is exactly the move that made 6a27e30f9 invisible.

    Refuses on a shallow clone, before anything is written - see `rows_preflight`.
    """
    rows_preflight(core)

    derived: dict[str, str] = {}
    source: dict[str, str] = {}
    for name, rel, _container in REGISTRIES:
        commits = _git(core, "log", "--reverse", "--format=%H", "--", rel)
        shas = commits.split() if commits else []
        if not shas:
            raise CannotRun(
                f"{rel} has no committed state in this repository, so the earliest a citation to it "
                f"could have been written cannot be established. Refusing rather than adopting the "
                f"working tree.")
        blob = _git(core, "show", f"{shas[0]}:{rel}")
        if blob is None:
            raise CannotRun(f"{rel} cannot be read at its first commit {shas[0][:9]}")
        try:
            first_doc = json.loads(blob)
        except json.JSONDecodeError as exc:
            raise CannotRun(f"{rel} was not valid JSON at its first commit {shas[0][:9]} ({exc}), "
                            f"so its rows cannot be derived from that state.")
        first_rows = row_fingerprints(blob, first_doc)
        lines = blob.split("\n")
        for key in keys_by_registry.get(name, []):
            n = int(key.rpartition(":")[2])
            fp = first_rows.get(n) if 1 <= n <= len(lines) else None
            if fp is not None:
                derived[key] = fp
        source[rel] = shas[0]
    return {"rows": derived, "rowBaselineSource": source}


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

    THE LIST OF CITATIONS THE CONTENT AXIS COULD NOT HAVE SEPARATED. Reported on every run, and no
    longer a coverage hole: `row_identity` gives each of these lines a row path that IS unique by
    construction, so the shift onto the byte-identical twin is caught by S5 even though the text
    matches. It is still printed because a reader deciding whether to trust a green run needs to know
    how much of it rested on the weaker axis alone.

    Fingerprinting a WINDOW around the cited line was rejected, and the row axis is not a window: it
    identifies the row the line is in, so editing the lines BELOW a cited line - which misdirects
    nothing - stays green, while a shift onto another row reds. That is the same reason
    `guard-citation-stability.py` declines to refuse a changed line COUNT.
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
    rows: dict[str, "dict[int, str]"] = {}
    row_pairs: dict[str, "dict[int, tuple[str, str]]"] = {}
    for name, rel, _ in REGISTRIES:
        text = (core / rel).read_text(encoding="utf-8")
        try:
            doc = json.loads(text)
        except json.JSONDecodeError as exc:
            raise CannotRun(
                f"{rel} is not valid JSON ({exc}), so no cited line in it can be assigned a row. "
                f"Refusing rather than comparing texts in a file whose structure cannot be read: "
                f"a content-only check over an unparseable registry is the blind spot this guard "
                f"exists to close, wearing the file as a disguise.")
        bodies[name] = text.split("\n")
        rows[name] = row_fingerprints(text, doc)
        row_pairs[name] = row_identity(text)

    current_rows: dict[str, "tuple[str, str] | None"] = {}
    for key in citations:
        name, _, raw = key.rpartition(":")
        n = int(raw)
        body = bodies[name]
        current[key] = fingerprint(body[n - 1]) if 1 <= n <= len(body) else None
        current_rows[key] = row_pairs[name].get(n) if 1 <= n <= len(body) else None

    # A cited line inside the file that resolves to NO row is a refusal, not a finding. S2 speaks for
    # a line past the end; a line inside the file with no row means the scanner could not place it, and
    # a guard with no opinion about a row must not report OK about it.
    unplaceable = sorted(k for k, pair in current_rows.items()
                         if pair is None and current[k] is not None)
    if unplaceable:
        first = unplaceable[0]
        raise CannotRun(
            f"{first} is inside its file but has no resolvable JSON row. Refusing rather than reporting "
            f"OK about a line this guard cannot place: {len(unplaceable)} citation(s) affected, "
            f"starting with {', '.join(unplaceable[:5])}.")

    current_row_fp = {k: rows[k.rpartition(':')[0]][int(k.rpartition(':')[2])]
                      for k in citations if current_rows.get(k) is not None}

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

    return {"citations": citations, "current": current, "currentRows": current_rows,
            "currentRowFingerprints": current_row_fp,
            "geometries": geos, "findings": findings, "refused": refused}


def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Refuse a change that moved a line some document cites in a JSON registry by number.")
    parser.add_argument("--report", action="store_true",
                        help="print every citation, its fingerprint and its registry's safe append point")
    parser.add_argument("--update", action="store_true",
                        help="re-baseline the CONTENT axis, PRINTING every key added, removed or "
                             "changed. It structurally cannot write the row axis.")
    parser.add_argument("--adopt-rows", action="store_true",
                        help="derive the ROW axis from each registry's FIRST committed state and "
                             "record the provenance commit")
    parser.add_argument("--json", action="store_true", help="print the machine-readable verdict")
    parser.add_argument("--root", default="", help="workspace root (default: the resolved workspace root)")
    args = parser.parse_args(argv)

    if args.update and args.adopt_rows:
        print("REFUSING: --update and --adopt-rows write different axes with different provenance. "
              "--update re-takes the CONTENT axis from the working tree, which is the move that made "
              "6a27e30f9 invisible; --adopt-rows derives the ROW axis from history. Running both at "
              "once would leave the file claiming a provenance it does not have.", file=sys.stderr)
        return 2

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
    current_rows = result["currentRows"]
    current_row_fp = result["currentRowFingerprints"]
    geos = result["geometries"]
    findings = result["findings"]

    # S4 - the dangerous state: a pattern stopped matching, or every citer was filtered out as a
    # comment. A guard that reads zero citations reports success while checking nothing.
    if not citations:
        print("REFUSING REGISTRY-APPEND-SAFETY: ZERO citations found. Refusing rather than reporting a "
              "clean run against an empty set - either the pattern stopped matching or every match sat "
              "on a comment line.", file=sys.stderr)
        return 2

    keys_by_registry = {name: sorted(k for k in citations if k.rpartition(":")[0] == name)
                        for name, _rel, _c in REGISTRIES}

    # BEFORE any write: can the row axis be derived soundly at all? A refusal that has already
    # created a seed baseline is a partial adoption, not a refusal.
    if args.adopt_rows:
        try:
            rows_preflight(core)
        except CannotRun as exc:
            print(f"REFUSING REGISTRY-APPEND-SAFETY: {exc}", file=sys.stderr)
            return 2

    geo_by_name = {g["registry"]: g for g in geos}
    if not BASELINE.is_file():
        if not (args.update or args.adopt_rows):
            print(f"REFUSING: no baseline at {BASELINE}. Run with --update to take the CONTENT axis, or "
                  f"--adopt-rows to derive the ROW axis from registry history, deliberately.",
                  file=sys.stderr)
            return 2
        seed: dict = {"fingerprints": current if args.update else {},
                      "unverified": [], "rows": {}, "rowBaselineSource": {}}
        BASELINE.write_text(json.dumps(seed, indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        if args.update:
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

    # ---- the row axis: adopted, validated, and never silently absent -----------------------------
    if args.adopt_rows:
        try:
            adopted = adopt_rows(core, keys_by_registry)
        except CannotRun as exc:
            print(f"REFUSING REGISTRY-APPEND-SAFETY: {exc}", file=sys.stderr)
            return 2
        previous_rows = stored.get("rows") or {}
        previous_source = stored.get("rowBaselineSource") or {}
        added = sorted(k for k in adopted["rows"] if k not in previous_rows)
        modified = sorted(k for k in adopted["rows"]
                          if k in previous_rows and previous_rows[k] != adopted["rows"][k])
        gone = sorted(k for k in previous_rows if k not in adopted["rows"])
        stored["rows"] = adopted["rows"]
        stored["rowBaselineSource"] = adopted["rowBaselineSource"]
        BASELINE.write_text(json.dumps(stored, indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        print(f"adopted {len(adopted['rows'])} row fingerprint(s) from registry FIRST commits -> "
              f"{BASELINE.name}")
        for rel, sha in sorted(adopted["rowBaselineSource"].items()):
            print(f"  {rel:<46} first commit {sha}")
        print(f"  added   {len(added)}: {', '.join(added) if added else '(none)'}")
        print(f"  changed {len(modified)}: {', '.join(modified) if modified else '(none)'}")
        print(f"  removed {len(gone)}: {', '.join(gone) if gone else '(none)'}")
        if modified:
            print("  A CHANGED row fingerprint means the cited line now sits in a DIFFERENT row than it "
                  "did at the registry's first commit, so a citation written any time since then reads "
                  "the wrong thing. That is a workspace-root document repair, not a re-baseline: "
                  "re-deriving it here is only correct once the citing sentence itself has been "
                  "re-pointed at the line it means.")
        return 0

    rows_baseline = stored.get("rows")
    row_source = stored.get("rowBaselineSource")
    if not isinstance(rows_baseline, dict) or not rows_baseline:
        print(f"REFUSING: the baseline has no `rows` axis. The content fingerprint cannot separate two "
              f"byte-identical lines, and these registries hold 13 of them, so a content-only run would "
              f"report OK over a repointed citation - which is exactly what it did on 6a27e30f9. Run "
              f"`--adopt-rows` to derive it from each registry's first commit.", file=sys.stderr)
        return 2
    if not isinstance(row_source, dict) or not row_source:
        print(f"REFUSING: the baseline has a `rows` axis but no `rowBaselineSource`. An axis with no "
              f"recorded provenance cannot be reviewed, and an unreviewable axis is how an unreviewed "
              f"citation becomes permanent. Run `--adopt-rows`.", file=sys.stderr)
        return 2
    unknown_sources = sorted(set(row_source) - {rel for _n, rel, _c in REGISTRIES})
    if unknown_sources:
        print(f"REFUSING: `rowBaselineSource` names {unknown_sources}, which are not registries this "
              f"guard checks. A stale provenance entry describes a file nothing reads.", file=sys.stderr)
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
        # `unverified` and `rows` are carried across untouched. --update re-takes the CONTENT
        # fingerprints from the working tree; it has no business clearing a debt declaration, or a
        # debt could be erased by re-baselining - which is the one thing a re-baseline must never be
        # able to do. Nor may it touch `rows`: those are derived from registry HISTORY, and taking
        # them from the working tree is how a repointed citation becomes the recorded promise.
        if not isinstance(stored.get("rows"), dict) or not stored.get("rows"):
            print(f"REFUSING --update: the baseline has no `rows` axis, and --update cannot create one. "
                  f"The row axis is derived from each registry's first commit by `--adopt-rows`; taking "
                  f"it from the working tree would record the present as the promise. Run "
                  f"`--adopt-rows` first.", file=sys.stderr)
            return 2
        stored["fingerprints"] = current
        BASELINE.write_text(json.dumps(stored, indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        print(f"re-baselined {len(current)} CONTENT citation(s) -> {BASELINE.name}")
        print(f"  added   {len(added)}: {', '.join(added) if added else '(none)'}")
        print(f"  changed {len(modified)}: {', '.join(modified) if modified else '(none)'}")
        print(f"  removed {len(gone)}: {', '.join(gone) if gone else '(none)'}")
        print(f"  unverified declarations carried across unchanged: "
              f"{', '.join(str(e.get('registry')) for e in unverified) if unverified else '(none)'}")
        print(f"  row axis and its provenance carried across unchanged: "
              f"{len(stored['rows'])} fingerprint(s) from "
              f"{', '.join(sorted(str(v)[:9] for v in stored['rowBaselineSource'].values()))}"
              f" - --update structurally cannot re-derive it")
        if modified:
            print("  A CHANGED fingerprint means the cited line's CONTENT differs from what it held when "
                  "it was cited. Read each one above and confirm the citing sentence still means what it "
                  "says before accepting this baseline; re-baselining a moved line is how a citation "
                  "becomes a lie that no gate can see.")
        return 0

    gone = sorted(k for k in baseline if k not in current)

    for key in sorted(current):
        was = baseline.get(key)
        where = citations[key]
        if current[key] is None:
            findings.append(dict(code="S2-CITATION-PAST-END", registry=key.rpartition(":")[0],
                                note=f"{key} points PAST THE END of its file "
                                     f"(cited from {where[0]}:{where[1]})"))
        elif was is None:
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=key.rpartition(":")[0],
                                note=f"{key} is a NEW citation with no content baseline entry "
                                     f"(cited from {where[0]}:{where[1]})"))
        elif current[key] != was:
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=key.rpartition(":")[0],
                                note=f"{key} now holds different content (cited from {where[0]}:{where[1]}) "
                                     f"- the line moved, or the row above it grew"))

    # ---- S5, the row axis. The one check that can see a shift onto a byte-identical line. ----------
    for key in sorted(current):
        if current[key] is None:
            continue                      # S2 already speaks for a line past the end of its file
        was_row = rows_baseline.get(key)
        pair = current_rows[key]
        where = citations[key]
        if was_row is None:
            findings.append(dict(code="S5-CITED-ROW-CHANGED", registry=key.rpartition(":")[0],
                                note=f"{key} is a NEW citation with no row baseline entry, so there is "
                                     f"no first-commit row to compare it against (cited from "
                                     f"{where[0]}:{where[1]}). It sits in {row_label(pair)} today."))
        elif current_row_fp[key] != was_row:
            findings.append(dict(code="S5-CITED-ROW-CHANGED", registry=key.rpartition(":")[0],
                                note=f"{key} now sits in row {row_label(pair)} "
                                     f"(cited from {where[0]}:{where[1]}), which is not the row it "
                                     f"occupied at "
                                     f"{row_source.get(rel_of(key), 'the registry')[:9]}. The content "
                                     f"fingerprint may or may not have seen this: when the new row's "
                                     f"line is byte-identical to the old one, S1 stays silent and only "
                                     f"this check reds."))

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
            print(f"    cited line text not unique in file: "
                  f"{len(g['ambiguous_lines'])} of {g['distinct_lines']} - these are the citations S1 "
                  f"ALONE could not separate, and every one of them is covered by the row axis: "
                  f"{', '.join(str(n) for n in g['ambiguous_lines']) if g['ambiguous_lines'] else '(none)'}")
        for key in sorted(citations, key=lambda k: (k.rpartition(":")[0], int(k.rpartition(":")[2]))):
            where = citations[key]
            pair = current_rows.get(key)
            print(f"    {key:<48} {current[key] or 'PAST END OF FILE':<18} "
                  f"{row_label(pair) if pair else '-':<34} {where[0]}:{where[1]}")
        print(f"\n  ROW AXIS PROVENANCE (earliest state a citation to each registry could have been "
              f"written):")
        for rel, sha in sorted(row_source.items()):
            print(f"    {rel:<46} {sha}")
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
                          "rowBaselineSource": row_source,
                          "commentMatchesRefused": result["refused"],
                          "citations": len(citations),
                          "findings": findings}, indent=2, ensure_ascii=False))
        return 1 if findings else 0

    for g in geos:
        print(f"  {g['rel']}: {g['citations']} citation(s) over {g['distinct_lines']} distinct line(s), "
              f"highest {g['max_cited']} of {g['lines']}; safe append = before line {g['close']} "
              f"({g['container']}); {result['refused'][g['registry']]} comment match(es) refused; "
              f"{len(g['ambiguous_lines'])}/{g['distinct_lines']} cited line(s) hold text that is not "
              f"unique in the file, so S1 alone cannot separate them - the row axis covers those")

    # The row axis's provenance is printed on EVERY run, green included, because "since when" is the
    # difference between a row axis that certifies a citation and one that merely records the present.
    print(f"  row axis compared against each registry's FIRST commit: "
          f"{', '.join(f'{pathlib.Path(r).name}={s[:9]}' for r, s in sorted(row_source.items()))}")

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
        print(f"REGISTRY APPEND SAFETY GUARD OK - {len(citations)} citation(s), no cited line moved or "
              f"changed row since the recorded baselines, and every append container still closes below "
              f"every line cited from it")
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
    print("\nAn S1 or an S5 is one insertion point: move the new row to the safe append point its "
          "registry printed above, or re-point the affected citations deliberately. An S5 on a line "
          "whose text did NOT change is the case S1 is structurally blind to, and no re-baseline "
          "fixes it - the citing sentence has to be re-pointed at the line it means.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
