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

THE ROW IS AN IDENTITY AND A SPAN, AND THE ARRAY INDEX IS NEITHER. The row is the pair of JSON
elements a line spans: the one open when the line's first non-whitespace character is read, and the
one open when its last is. The PAIR, because a JSON row is written open-brace-first and
close-brace-last: the opening `    {` of a boundary belongs both to the 556-row `boundaries` array
and to the boundary it opens, and the closing `    },` belongs both to the boundary it closes and to
the array it returns to. Taking either endpoint alone under-identifies one of those two cases -
every opening line of a 556-row array would report the same row, which is the defect repeated.

The fingerprint is that pair, that row's declared identity (`id`, `verificationId`, `script`, ...),
and NOT the array index. The index is a POSITION. Dropping a row in above a cited line moves every
later row one index up without changing what any of them IS, so a fingerprint carrying the index
reds a citation whose row was never touched - it reports drift where there is none, and it made this
axis demand a repair that no correct line could satisfy. Measured, and that was not theoretical:
with the index in the fingerprint, 35 of the 49 citations were red and ZERO of the 22 target rows
had a line satisfying the check, so re-pointing a citation retired one finding and created two (a new
line has no baseline entry on either axis), and only `--update` or `--adopt-rows` could clear them -
both re-baselining, which is the thing this axis exists to prevent. With the index dropped, a row
that merely SHIFTED goes green and a row that SLID onto a different row still reds, because the
identity token is unchanged and the span is unchanged except for its position.

THE IDENTITY TOKEN IS WHAT MAKES THE SHIFT VISIBLE, AND IT IS NOT OPTIONAL. A first version
fingerprinted the path pair alone and was caught by its own mutation control. Duplicating the
boundary row ten lines above a cited line lands that line on a byte-identical twin - and under the
old index-carrying fingerprint it stayed at the same index too, because the content and the index
shift by the same amount (the copy takes index 5 and pushes the cited row to index 7, so the line
reported index 6 either way and meant the PREVIOUS row). The token separates "the row that was
there" from "the row that is there now", and it is the only component that does: with the index
gone, the token plus the span is the whole discriminator.

THE NARROWING THE INDEX-DROP CAUSES, MEASURED, BECAUSE IT IS NOT SMALL. A closing `},` ends at its
PARENT element, so `row_token` returns nothing for it and the index was the only thing separating one
row's closing brace from another's. With the index removed, every closing brace of one array shares
one fingerprint: measured, `verification-boundaries.v1.json` goes from 4088 distinct fingerprints over
7308 lines to 2964, and the largest collision class goes from 84 lines to 556 - which is
`boundaries[0]`'s brace through `boundaries[555]`'s. Seven CITED lines are affected, and they are
named here because a reader deciding what this axis is worth has to know: `verification-boundaries`
lines 1369, 1389, 4090, 4100 and 5436, `todo-shapes` line 268, and `verification-boundaries` line 1.
Every one of the five boundary lines is an unreadable-ambiguous citation already (a closing brace of a
row a line-and-column scan report names, or a dated defect report's span), so no adjudication is lost
that was not already unadjudicable - but a shift onto a DIFFERENT closing brace is now invisible, and
that is the price of the owner's ruling rather than a defect to be fixed later.

WHAT THE ROW AXIS DELIBERATELY DOES NOT DO. It is not a window. It does not fingerprint a row's
CONTENTS, so editing a row below a cited line stays green - that edit misdirects nothing. It DOES
fingerprint a row's identity, so renaming a row in place reds. That is a deliberate cost and not an
oversight: a document that cites `registry.v1.json:679` is pointing at a specific row, so changing
what that row is called is a change to what the citation means. Refusing a non-unique TEXT would
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
                           Counts LINE citations only: a KEY citation names no line, so growth above
                           it moves nothing it points at.
    S4 NO-CITATIONS        refuses (exit 2) rather than reporting success against an empty set.
    S5 CITED-ROW-CHANGED   a cited line now belongs to a different row. Fires whether or not the
                           content changed, which is the entire point: it is the axis that sees the
                           byte-identical twin. An `unverified` declaration does NOT excuse it,
                           because a debt declaration is a statement about the past. It does NOT
                           fire on a row that merely SHIFTED index - see the fingerprint above; that
                           is the same meaning at a different line, which is a re-point, not a drift.
    S6 REVIEWED-AXIS       not a finding: the printed inventory of the `reviewed` axis, on every run
                           green included. It exists so a green run cannot be read as "every citation
                           was compared against the registry's first commit" when some were compared
                           against a dated review instead.

    S7-KEY-UNRESOLVED      a `<registry>#<key>` citation names no row of that registry. Tried
                           {spelling(s)}; the namespaces are listed. A FINDING, never a silent pass.
    S7-KEY-AMBIGUOUS       the same citation names MORE THAN ONE row. Measured, this is real and not
                           hypothetical: 84 of the 142 `projects` keys in
                           `verification-boundaries.v1.json` are ALSO a `boundaries[].id`, so `#core-
                           atoms` names two different rows. The guard names both and never picks one;
                           the repair is to write the namespace: `#boundaries/core-atoms`.
    S8-KEY-NOT-REVIEWED    the key resolves to exactly one row, but no review records what that row
                           should hold. `--update` and `--adopt-rows` STRUCTURALLY CANNOT write this
                           axis, so the only repair is `--accept <registry>#<key>` - one row, with a
                           stated reason.
    S1-KEY-CONTENT-CHANGED the row a key citation names now has different CONTENT. Strictly stronger
                           than the line form's S1, because the line form can see one line and this
                           registry holds 556 byte-identical `    {` lines, while a key has the whole
                           row and the whole row is unique.
    S5-KEY-ROW-CHANGED     the key now resolves to a DIFFERENT ROW - a different namespace, element
                           kind or identity - while its content may be unchanged. Two rows can say
                           the same thing and be two different rows; only the identity separates them.
                           It is the key-space analogue of the byte-identical twin.

THE KEY FORM EXISTS BECAUSE THE LINE FORM'S THREE FAILURES ARE MEASURED, NOT SUSPECTED. A citation
of the form `<registry>#<key>` names a ROW BY ITS DECLARED IDENTITY rather than by a position, and
each of the three hazards above is a property of the LINE form alone:

  1. `verification-boundaries.v1.json` holds 556 byte-identical `    {` lines and
     `enforcement-registry.v1.json` holds 65 more. Eleven of the fifteen recorded reviews store the
     fingerprint of one of those, so for those keys the content axis cannot separate the line from its
     555 twins and only the row axis carries the check. A key citation fingerprints the WHOLE ROW,
     which is unique, so the blind spot is closed by construction rather than caught afterwards.
  2. The line form's row fingerprint is derived from the ARRAY INDEX, so one row inserted above a
     citation moves it - which is why nine of the fifteen reviews have already expired under it. A
     key citation's row fingerprint carries NO line number and NO array index: it is the namespace,
     the element kind and the key. Inserting a row above a cited row changes none of those three.
  3. A citation of a GENERATED line-and-column scan record cannot be re-pointed at all, because the
     commit its header names is in neither repository. A key citation names a row in a registry that
     is present in both.

THE RESOLUTION RULE IS DERIVED FROM THE REGISTRIES, NOT ASSUMED, and it is measured rather than
asserted. An ARRAY row is named by the field it declares - `id` in every case here - and a MAP row is
named by the key it sits under; those are two different mechanisms, so a citation says which one it
means. `#<key>` searches every namespace of the registry and resolves only when exactly one row
answers. `#<namespace>/<key>` names one namespace, which is the only way to cite one of the 84
collisions. A `todo-shapes.v1.json` key is `tasks/<name>-todo.md` and is read WHOLE, because no
registry declares a namespace called `tasks` - the qualifier is only a qualifier when its prefix is a
real namespace name.

AND THE KEY AXIS HAS NO BLANKET OPERATION, WHICH IS THE POINT. A key citation is recorded ONLY by
`--accept`, one row at a time, into `reviewedKeys`, with the citing document, the namespace, the
element kind, the adjudication, the date and a stated reason - all of it printed. Neither `--update`
nor `--adopt-rows` can write it, structurally, so there is no re-baselining request that could bless a
key citation. A green run over a key therefore means "this named row has not been renamed, moved or
edited since the day a person read the sentence that cites it", which is exactly the claim a citation
makes, and is strictly stronger than the line form's "this numbered line has not moved".

A GREEN RUN THEREFORE MEANS TWO DIFFERENT THINGS FOR THE TWO FORMS, and the tool prints which is which
on every run: no cited LINE has moved or changed row since the recorded baselines, and no cited ROW
still resolves, still holds its content and still sits in the namespace it was reviewed in. Neither
means "these citations are correct", and the line form additionally still carries the first-commit
limit stated above.

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

A SCANNER COORDINATE IS NOT A CITATION, AND IT IS COUNTED RATHER THAN DROPPED. A third numeric
component - `<registry>:<line>:<column>` - is the coordinate a grep-style report, a lint output and an
editor jump-list emit, and it promises nothing about the row line N holds. `CITATION` reads two
numeric components or none, so a triple is never turned into a citation, and `COORDINATE` counts it so
that re-spelling a real citation as a coordinate - the move that would use the rule as a mute button -
is a changed number on every run rather than a quiet pass.

THE GUARD DOES NOT EXEMPT ITS OWN CONTRACT TEST, AND THE REASON IS STRUCTURAL. It is tempting to read
the phantom citation this guard finds in its own test file as an argument for skipping that file the
way `candidate_files` skips this guard's baseline, and the two cases are not alike. The baseline is
skipped because its CONTENT IS A SET OF CITATION SPELLINGS - scanning it would make the guard cite
itself - whereas a test file is CODE THAT BUILDS a citation at runtime and carries one incidental
literal. A string literal used as a value is counted deliberately (see `_python_comment_columns`),
because the repair for a phantom there is one edit in the citing file and a missed citation is
invisible. Widening this guard's reach to make a fixture disappear would trade a gating check for an
allowlist entry; the fixture is repaired where it is written instead.

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

HOW MUCH IS ALREADY WRONG, MEASURED, AND A FINDING IS NOT A VERDICT ON THE CITATION. Measured against
each registry's first commit, 35 of the 49 citations sit on a line that has left the row it held on
2026-09-30:

    7   enforcement-registry.v1.json      - all seven, as previously declared
   28   verification-boundaries.v1.json   - NOT previously declared, and not covered by any gate
    0   todo-shapes.v1.json               - one citation, on a file with a single committed state

The 28 were invisible for a compound reason: the citing documents predate the guard, the registry grew
many times, and the boundary rows they name were inserted and later removed again by 20fd859 - so the
cited line ends where it began while having been somewhere else in between. A line that was ever
occupied by a different row is occupied by that row for a writer who read it then, and no fingerprint
of the present can speak for that period.

A FINDING IS A DRIFT MEASUREMENT, NOT A DEFECT VERDICT, AND CONFLATING THE TWO IS THE TRAP THIS AXIS
SET FOR ITSELF. The guard compares a line against the registry's FIRST COMMIT; a reader of a document
reads it against TODAY. So all 35 were adjudicated by asking one question - is the row the SENTENCE
names actually at the cited line today? - and the answer split them three ways: 15 wrong-today (the
named row is elsewhere, so the document was re-pointed), 7 stale-but-correct (the named row IS at the
cited line today, so the finding records a historical excursion and the document was left alone), and
13 unreadable-ambiguous (nine line-and-column citations into a GENERATED scan report whose header
names a commit absent from both repositories, three dated defect reports whose subject no longer
exists, and one genuinely two-way ambiguity). Repairing all 35 would have been 13 wrong edits.

TWO OF THE 15 WERE FALSE GREENS UNDER THE INDEX-DROP, AND THE GUARD COULD NOT SEE EITHER. Both sit in a
row whose token never changed, so dropping the index made them green; reading the sentences showed
both name a DIFFERENT row. That is the residual limit of this axis stated as a measurement: it can
see that a row changed, never that a sentence was wrong when the row did not change. The two are
repaired anyway, and the finding count below is not a claim that 50 citations are broken.

A RE-POINT TO A LINE NO DOCUMENT HAS CITED RAISED A FINDING, AND THAT WAS STRUCTURAL - UNTIL `--accept`.
`--adopt-rows` derives each key from the first commit, and a newly cited line held a different row
back then, so adopting it records a row today's line does not match: re-pointing could never go green
under that provenance. The 15 repairs therefore retired 13 findings and raised 30 (one S1 and one S5
each, for a key with no baseline entry on either axis), and the only ways to clear those were
`--update` and `--adopt-rows` - both blanket re-baselines, which is the thing this guard exists to
prevent. `--accept` (above) is now the narrow third way, and those 30 findings are recorded as 15
`--accept` reviews. It is deliberately NOT folded into `--adopt-rows`: a review is dated and attributed,
an adoption is not, and mixing them would let a blanket re-derivation relabel thirty historical
excursions as fifteen reviewed citations.

The `unverified` declaration for `enforcement-registry.v1.json` is KEPT and is not superseded by the
row axis: it records that those citations were wrong BEFORE any baseline existed, which is a fact
about the past the row axis cannot record because it only compares against 2026-09-30.

A green run therefore means "no cited line has moved or changed row since the recorded baselines, and
no cited row has been renamed, moved or edited since its review", never "these citations are correct",
and the file itself carries that distinction so it cannot be read the other way by someone who never
reads this docstring. FURTHER drift in an unverified registry reds exactly as it does in a verified
one: the declaration excuses the past, never the future.

USAGE (from gk-core, or anywhere in the workspace):
    python scripts/guard-registry-append-safety.py            # the gate; exit 1 on any finding
    python scripts/guard-registry-append-safety.py --report   # every citation, its FORM, and the point
    python scripts/guard-registry-append-safety.py --json     # machine-readable verdict
    python scripts/guard-registry-append-safety.py --root P   # a workspace root other than the resolved one
    python scripts/guard-registry-append-safety.py --adopt-rows   # re-derive the ROW axis from history
    python scripts/guard-registry-append-safety.py --accept verification-boundaries.v1.json:2098 \
        --accept-why "resume-29's sentence names guard-tests-fallback, and that is the row at 2098"
    python scripts/guard-registry-append-safety.py --accept verification-boundaries.v1.json#guard-tests-fallback \
        --accept-why "the same sentence, re-spelled against the row's id instead of its line"

THE STORED KEY IS THE SPELLING IN THE DOCUMENT, AND THAT IS NOT AN ACCIDENT. A key citation whose stored
form differed from the text a reader can see would be the exact defect this guard exists to prevent, so
the key is stored as written - including a sentence-final `.` - and resolution reports the spelling it
settled on. The consequence is measured and stated rather than hidden: `tasks/sessions/ps1-ban-manager-
20260926.json` cites `todo-shapes.v1.json:268` inside prose that also spells `todo-shapes.v1.json#tasks/
live-probe-todo.md.` with a full stop, so those are TWO keys naming ONE row. They are counted as two
citations, both reviewed, both printing the row they resolve to - and a reader who wants the citation
count deduplicated by row has `--report` for it. Merging them silently would mean a stored key that does
not match the text.

Exit 0 = no cited line moved or changed row, and every append container is still safe. Exit 1 = an
S1-S3 or S5 finding. Exit 2 = it could not run, and it names the prerequisite that was missing.

A REVIEW IS A THIRD PROVENANCE AND IS STORED AS ONE. The adopted row axis speaks from each registry's
first commit and `--update` speaks from the working tree; neither can record "a person read this
sentence and this is the row it means", so before `--accept` every honest re-point retired one
finding and raised two and the only escape was a blanket operation. `--accept` writes a citation's
fingerprints into a separate axis together with the citing document, the row's declared identity, the
adjudication, the date and a stated reason, and it PRINTS all of it - a silent write into a gate's own
state is the class of thing this programme distrusts. It can only fill a hole: it refuses a citation
that already has a baseline entry, one nothing cites, a line with no resolvable row identity, a key
that resolves to no row or to two, and a citing sentence that names a DIFFERENT row than the citation
points at. It refuses every wildcard, range and bare registry name and names `--update`/`--adopt-rows`
instead, because the entire value of the flag is that it is one citation wide.

There are two such axes, one per form, and they are not merged: a LINE citation is reviewed into
`reviewed`, a KEY citation into `reviewedKeys`. Merging them would mean putting a key record into a
dict whose validator demands a numeric line, and loosening that validator to allow it would weaken
the LINE form's own refusal - so the two axes stay apart and each validator is strict about its own
shape.

THE ADJUDICATION IS REPORTED, NOT ASSUMED, AND `unaudited` IS AN HONEST ANSWER. `corroborated` means
the citing sentence names the row the cited line is in, which this tool checked. `unaudited` means it
names no row of that registry at all, so NO machine cross-check was possible and the record rests on
the reviewer's reading. That second case is common and is not a defect - a document may cite a line
purely for its content - but it is never presented as machine-checked, and `--json` carries the same
distinction. The 15 re-pointed citations of the current repair are recorded in full above the
`unverified` discussion for exactly this reason.
"""
from __future__ import annotations

import argparse
import datetime
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

#: A citation, in EITHER of its two forms, and never in a third.
#:
#:   `<registry>:<line>`  the LINE form. A promise that line N still holds what it held when the
#:                        sentence was written, and it is the form that made this guard necessary.
#:   `<registry>#<key>`   the KEY form. A promise that the ROW this key names still says what it
#:                        said. Immune to a row being inserted above it, because a row's declared
#:                        identity does not change when a row is inserted above it.
#:
#: The key character class excludes whitespace, backticks, quotes, brackets and sentence punctuation,
#: so a citation wrapped in backticks - or followed by a comma, a bracket or a closing paren - is
#: captured whole. A trailing `.` is NOT excluded, because `todo-shapes.v1.json` keys end in `.md`;
#: `key_candidates` resolves that ambiguity against the registry instead of guessing at it.
#:
#: A key citation is ALWAYS spelled in full. A bare `#name` is not a citation form, and must not be
#: one: `#` is a markdown heading, a GitHub anchor and a hex colour, so a bare form would be read as
#: whatever the surrounding prose happened to make of it.
#:
#: A THIRD NUMERIC COMPONENT IS NOT A CITATION, AND THIS IS A RULE ABOUT THE SHAPE, NOT ABOUT ANY ONE
#: DOCUMENT. `<registry>:<line>:<column>` is a SCANNER COORDINATE - "the banned string is at line N,
#: column C of this file", which is what a grep-style report, a lint output and an editor jump-list
#: all emit - and it is not a promise that line N holds a particular row. Reading one as the other
#: silently DROPS the column and then reports the registry for not holding, at line N, whatever text
#: the coordinate's own line happened to be about. That produced 13 findings against
#: `tasks/ip-censor/report.md` and 0 wrong citations, and it would produce 13 more against any other
#: report of the same shape, which is why the refusal is written on the form and not on the filename.
#:
#: The lookahead is `(?!\d*:\d)` rather than `(?!\d*:)` for two measured reasons. `\d*` before the
#: colon keeps PROSE COLONS working - `boundaries.v1.json:802: the row above it` is still a citation -
#: while a second numeric component is not. And because `\d*` can absorb the remaining digits, every
#: shorter alternative `\d+` could backtrack to is refused too: a bare `(?![:\d])` would match `136` of
#: `1369:19` and then report the registry for line 136, which is worse than the defect it fixes. The
#: same triple is still visible, because `COORDINATE` counts and prints it.
CITATION = re.compile(
    r"(?P<basename>verification-boundaries\.v1\.json|enforcement-registry\.v1\.json"
    r"|todo-shapes\.v1\.json)"
    r"(?::(?P<line>\d+)(?!\d*:\d)|#(?P<key>[^\s`'\"()\[\]{}<>|,;:]+))"
)

#: `<registry>:<line>:<column>`, the scanner coordinate `CITATION` refuses. Counted and printed, not
#: merely dropped, so that re-spelling a real citation as a coordinate - the move that would use this
#: rule as a mute button - shows up as a changed count rather than as a quiet pass. A triple inside a
#: comment is counted once, as a comment, and not also here.
COORDINATE = re.compile(
    r"(?P<basename>verification-boundaries\.v1\.json|enforcement-registry\.v1\.json"
    r"|todo-shapes\.v1\.json):(?P<line>\d+):(?P<column>\d+)"
)

#: The separator each form uses, and the closed vocabulary of forms. A citation is one or the other;
#: there is no third spelling, and `citation_form` is how the guard decides which axis a key is on.
FORM_LINE = "line"
FORM_KEY = "key"
FORMS = (FORM_LINE, FORM_KEY)

_FENCE = re.compile(r"^\s*(?:```|~~~)")
_HASH_COMMENT = re.compile(r"^\s*(?:#|//)")
_CSHARP_BLOCK = re.compile(r"/\*|\*/")
_STAR_CONTINUATION = re.compile(r"^\s*\*")

#: registry name -> the repository-relative file that holds it, from REGISTRIES. Used to name a
#: provenance commit in a finding without repeating the lookup at three call sites.
REL_OF = {name: rel for name, rel, _container in REGISTRIES}


def citation_form(key: str) -> str:
    """Which of the two citation forms `key` is spelled in. Never a guess and never a default.

    The form decides which axis a citation is compared on and which findings it can raise, so this
    must be derived from the spelling rather than defaulted: defaulting to the line form would put an
    unresolvable `#key` through `int(...)` and raise, and defaulting to the key form would make a
    regression in the line form silent.
    """
    if "#" in key:
        return FORM_KEY
    if ":" in key:
        return FORM_LINE
    raise CannotRun(
        f"{key!r} is neither citation form. A citation is `<registry>:<line>` or "
        f"`<registry>#<key>` and nothing else, so a third spelling would be checked by no axis at "
        f"all while still counting toward the citation total.")


def registry_of(key: str) -> str:
    """The checked registry a citation key names, as its BASENAME. Never a KeyError: an unknown key
    names nothing, and the caller's own lookup is the honest answer.

    The basename and the file path are deliberately different values. Every registry-state dict in
    this guard is keyed by the basename (`collect` builds keys out of it, `evaluate` loads
    `docs`/`bodies` under it), and `rel_of` is the only place that turns one into the other.
    """
    if citation_form(key) == FORM_KEY:
        return key.split("#", 1)[0]
    return key.rpartition(":")[0]


def line_of(key: str) -> int:
    """The line a LINE-form citation names. Refuses on a key-form key rather than guessing, because
    the two forms have genuinely different subjects and one has no line at all."""
    if citation_form(key) != FORM_LINE:
        raise CannotRun(f"{key} is a KEY citation and names a row, not a line.")
    return int(key.rpartition(":")[2])


def row_key_of(key: str) -> str:
    """The row key a KEY-form citation names, without its registry prefix. Refuses on a line-form
    key, for the same reason `line_of` does."""
    if citation_form(key) != FORM_KEY:
        raise CannotRun(f"{key} is a LINE citation and names a line, not a row key.")
    return key.split("#", 1)[1]


def rel_of(key: str) -> str:
    """The repository-relative file a citation key names. Never a KeyError: an unknown key names
    nothing, and the caller's default is the honest answer."""
    return REL_OF.get(registry_of(key), "the registry")


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


#: Fields that name a row, most specific first. A row's token is what separates "the row that was
#: there" from "the row that is there now" - and with the array index gone from the fingerprint, it is
#: the ONLY component that can.
IDENTITY_FIELDS = ("id", "verificationId", "script", "guard", "name", "project", "test")


def row_token(doc: "object", pair: "tuple[str, str]") -> str:
    """The identity of the row `pair` names: the DEEPEST indexed element's declared id, else the
    deepest map key.

    THE TOKEN CARRIES THE AXIS, NOT THE INDEX. The array index is a position and is deliberately not
    fingerprinted (see `index_free`), so the declared identity is what a row that SLID onto a
    different row is distinguished by. Measured: duplicating the boundary row ten lines above a cited
    line lands that line on a byte-identical twin; the token is what makes that red rather than
    invisible. A token-less row (a closing `},`, whose pair ends at the parent) is the documented
    limit - it can only be distinguished by its span.

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


#: A `[N]` segment of a row path. The array index is a POSITION, and a position is not an identity:
#: dropping a row in above a cited line moves every later row one index up without changing what any
#: of them IS. Fingerprinting the index therefore redded a citation whose row was untouched, which is
#: the defect the owner ruled on - it made the axis report 35 findings and cleared none of the real
#: hazard, so every honest repair it demanded was impossible to land.
_INDEX_SEGMENT = re.compile(r"\[\d+\]")


def index_free(pair: "tuple[str, str]") -> "tuple[str, str]":
    """`pair` with every `[N]` segment removed, one segment per path component.

    Segment-wise on purpose, not a substring strip: `row_token` can legitimately return a map KEY that
    contains brackets, and `paths[N]` where `paths` is a real key must keep its name. Splitting the
    path on `/` and dropping only the components that are an index outright leaves every named element
    standing, so what survives is the SHAPE of the row - which element it spans, and what that element
    is called - and never where in the array it happens to sit.

    WHAT THIS DELIBERATELY DOES NOT DROP IS THE PAIR. A JSON row opens before it closes, so
    `    {` is the element it OPENS and `    },` the element it CLOSES, and with the index gone every
    opening line of a 556-row array shares one fingerprint if the pair collapses. Keeping both
    endpoints is what stops that, and it is why a line that slid onto a DIFFERENT row still reds:
    the token alone would not carry it, but the token AND the span together do.
    """
    return tuple("/".join(seg for seg in path.split("/") if not _INDEX_SEGMENT.fullmatch(seg))
                 for path in pair)


def row_fingerprint(pair: "tuple[str, str]", token: str) -> str:
    """The row a cited line belongs to: the pair of elements it SPANS, plus the row's declared
    identity, with the array INDEX removed.

    The hazard this axis exists for is a citation sliding onto a DIFFERENT row that looks identical,
    and the identity token is what separates the row that was there from the row that is there now.
    Dropping the index does not weaken that: the token is unchanged, and the span is unchanged except
    for its position - so a swap between two adjacent rows still moves the token, and only a row that
    MERELY SHIFTED stops redding, which is the correct answer for a citation whose meaning never
    changed.
    """
    return hashlib.sha256(("\u0000".join((*index_free(pair), token))).encode("utf-8")).hexdigest()[:16]


def row_fingerprints(text: str, doc: "object") -> "dict[int, str]":
    """{line -> row fingerprint} for every line of one registry."""
    identities = row_identity(text)
    return {n: row_fingerprint(pair, row_token(doc, pair))
            for n, pair in identities.items()}


# ---------------------------------------------------------------------------------------------
# the KEY form: `<registry>#<key>`, a citation of a ROW'S IDENTITY rather than of a line
#
# WHY THIS FORM EXISTS, MEASURED NOT ASSUMED. Three measured facts make a line-number citation
# unworkable on these registries, and all three are properties of the LINE form alone:
#
#   1. `verification-boundaries.v1.json` holds 556 byte-identical `    {` lines and
#      `enforcement-registry.v1.json` holds 65 more. A fingerprint over ONE of them cannot tell it
#      from the other 555, so on those lines the content axis is blind and only the row axis carries
#      the check.
#   2. The line form's row fingerprint is derived from the ARRAY INDEX, so inserting one row above a
#      citation moves it - which is why nine recorded reviews have already expired under it.
#   3. A citation of a GENERATED line-and-column scan record cannot be re-pointed at all, because the
#      commit its header names is in neither repository.
#
# A row's declared identity is immune to all three: inserting a row above `id=core-fallback` does not
# change what `core-fallback` is, and every line of the row is available to fingerprint at once.
#
# WHAT THE KEY IS, DETERMINED FROM THE REGISTRIES RATHER THAN ASSUMED - measured, with the counts:
#
#   registry                       ARRAY rows (by declared `id`)   MAP rows (by their own key)
#   verification-boundaries.v1.json     556 `boundaries`, 556 unique     142 `projects`, 142 unique
#   enforcement-registry.v1.json         52 `invariants`, 52 unique     35 `guards`, 35 unique
#                                         13 `verificationExemptions`
#   todo-shapes.v1.json                   0                          122 top-level entries, all `tasks/*`
#
# So an ARRAY row is named by the field it declares (`id` in every case here) and a MAP row is named
# by the key it sits under. Those are two DIFFERENT mechanisms, and a key citation has to say which
# one it means - which is what `resolve_key` refuses to guess about.
#
# AND THE TWO NAMESPACES REALLY DO OVERLAP, MEASURED: 84 of the 142 `projects` keys in
# `verification-boundaries.v1.json` are ALSO a `boundaries[].id` (`core-atoms`, `core-combat`,
# `manager-fail-closed`, ...). A bare `#core-atoms` is therefore genuinely ambiguous there, and an
# ambiguous key is a FINDING naming both containers rather than a guess - see `S7-KEY-AMBIGUOUS`.
# That overlap is also the measure of what the key form is worth: it removes the line/index hazards
# and leaves this one, which has an honest answer because a human can be told what collides.
# ---------------------------------------------------------------------------------------------

#: `<container>/<key>`, which is how a citation says WHICH of the two namespaces it means. The
#: container is the registry's own top-level member name, measured above, so this is not a private
#: vocabulary: `boundaries/core-atoms`, `projects/core-atoms`, `invariants/narrative`, `guards/narrative`.
QUALIFIED = re.compile(r"^(?P<container>[^/]+)/(?P<key>.+)$")

#: The container name reported for a registry that has none - a flat map of entries rather than one
#: appendable array. `<root>`, because that is what the line form's geometry already calls it, and two
#: spellings for one thing would be a trap.
ROOT_CONTAINER = "<root>"

#: A registry's NAMESPACES, in file order. For a registry whose append container is None the whole
#: document is one map and therefore ONE namespace: `todo-shapes.v1.json` holds 122 rows named
#: `tasks/<name>-todo.md`, and treating each of those as a container would invent 122 namespaces where
#: the registry declares none. A citation's `<container>/` prefix must be one of these or it is not a
#: qualifier - which is what lets `tasks/live-probe-todo.md` be read as a KEY rather than as `tasks`
#: plus `live-probe-todo.md`, since no registry has a container called `tasks`.
def containers_of(doc: object, flat: bool) -> "list[str]":
    if flat or not isinstance(doc, dict):
        return [ROOT_CONTAINER]
    return [str(k) for k in doc]


def is_flat(name: str) -> bool:
    """Whether a checked registry is declared as a flat map (REGISTRIES' container is None)."""
    return next(container is None for n, _r, container in REGISTRIES if n == name)


def key_candidates(raw: str) -> "list[str]":
    """The spellings of a captured `#key` to try against a registry, most literal first.

    A prose citation ends in punctuation, and this guard's own sentence about it must survive being
    written in a sentence. A key character class cannot exclude `.` because `todo-shapes.v1.json`
    rows are named `tasks/<name>-todo.md`, so the trailing-dot ambiguity is resolved against the
    registry instead of against a rule: the spelling as written is tried first, then with one trailing
    `.` removed, then two. Nothing else is trimmed, because trimming anything else would be inventing
    a key rather than reading one, and a citation that resolved only after trimming would be a
    citation whose key the author never wrote.

    Measured: `verification-boundaries.v1.json` declares no key containing a `.` at all, so for all
    556 of its `boundaries` rows the first candidate is the answer; the walk exists for the
    `todo-shapes.v1.json` rows and for a sentence-final citation, and it is reported in the
    `unresolved` note so a reader can see every spelling that was tried.
    """
    out = [raw]
    trimmed = raw
    for _ in range(2):
        if not trimmed.endswith("."):
            break
        trimmed = trimmed[:-1]
        out.append(trimmed)
    return out


def row_key_index(doc: object, flat: bool = False) -> "dict[str, list[dict]]":
    """`key -> [every row that key names]`, across EVERY namespace of one registry.

    The value is a LIST and never a single row, because the overlap above is real and a resolver that
    returned one of two matching rows would have picked a winner rather than reporting the collision.
    An ARRAY row is indexed by the identity field it declares, in `IDENTITY_FIELDS` order so the most
    specific name wins; a MAP row is indexed by its own key. Depth is one level below the container,
    because these registries' rows are the container's direct members - `boundaries[]`, `projects{}`,
    `guards{}`, `invariants[]`, `verificationExemptions[]`, and the `todo-shapes.v1.json` document.
    """
    index: "dict[str, list[dict]]" = {}
    if not isinstance(doc, dict):
        return index

    def add(key: str, container: str, kind: str, node: object) -> None:
        index.setdefault(key, []).append(
            {"container": container, "kind": kind, "node": node})

    if flat:
        for key, row in doc.items():
            add(str(key), ROOT_CONTAINER, "map", row)
        return index

    for container, value in doc.items():
        name = str(container)
        if isinstance(value, list):
            for row in value:
                if not isinstance(row, dict):
                    continue
                for field in IDENTITY_FIELDS:
                    declared = row.get(field)
                    if isinstance(declared, str) and declared:
                        add(declared, name, "array", row)
                        break
        elif isinstance(value, dict):
            for key, row in value.items():
                if isinstance(row, (dict, list, str, int, float, bool)) or row is None:
                    add(str(key), name, "map", row)
    return index


def canonical_row(node: object) -> str:
    """The row's content, canonicalised so the fingerprint is about MEANING and not spelling.

    Sorted keys and fixed separators, because `json.dumps` with default settings separates object
    members by insertion order - two spellings of the same row would fingerprint differently and a
    re-order by any writer would red a citation whose meaning never changed. This is the same
    discipline as `normalise` on the line form, one level up.

    The row's IDENTITY IS DELIBERATELY NOT IN HERE for a map row, and is for an array row. For a map
    row the key is what the citation IS, so folding it in would put one change on both axes and leave
    the row axis with nothing to say; for an array row the `id` is a member of the row's own content,
    so renaming it is a content change as well as an identity change, and the guard says both.
    """
    return json.dumps(node, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def key_content_fingerprint(node: object) -> str:
    """The CONTENT axis for a key citation: the WHOLE row, not one line of it.

    STRICTLY STRONGER THAN THE LINE FORM, and that is measurable rather than asserted. The line form
    fingerprints one line, and `verification-boundaries.v1.json` holds 556 byte-identical `    {`
    lines, so a content fingerprint over one of them cannot distinguish it from the other 555 - eleven
    of the fifteen recorded reviews store exactly that shared value. A key citation has the whole row
    available, and the whole row is unique, so the blind spot is closed by construction rather than by
    a second axis catching it afterwards.

    It is also why a key citation reds on a rename (mutant 1) as well as on an edit (mutant 2): for
    an array row the `id` is inside the canonical content.
    """
    return hashlib.sha256(canonical_row(node).encode("utf-8")).hexdigest()[:16]


def key_row_fingerprint(container: str, kind: str, key: str) -> str:
    """The ROW axis for a key citation: WHERE the row is anchored and WHAT names it.

    NO LINE NUMBER AND NO ARRAY INDEX APPEARS HERE, and that is the entire property this form
    exists for. Inserting a row above a cited row moves its line number and its index; it changes
    neither its container, nor the kind of element it is, nor the key that names it - so a key
    citation does not drift, which is the control the owner's ruling was about.

    It carries the key because a CONTENT fingerprint cannot separate two byte-identical rows, and two
    identical rows under different keys is the key-space analogue of the twin line: `boundaries` and
    `projects` rows are both flat field bags, and the registry really does hold rows that differ only
    in which container they sit. Content says WHAT the row says; this says WHICH row it is.
    """
    return hashlib.sha256("\u0000".join((container, kind, key)).encode("utf-8")).hexdigest()[:16]


def resolve_key(doc: object, raw: str, flat: bool = False) -> dict:
    """Resolve one `#key` spelling against a registry. Returns a result, never raises.

    THE RESOLUTION RULE, and every branch of it:
    #
    #   * `<container>/<key>`  where `<container>` is one of the registry's own namespaces
    #     (`containers_of`, which reports `<root>` for a flat registry): that namespace's rows only.
    #     This is how an author names one of the 84 collisions.
    #   * `<key>`               against EVERY namespace at once. Exactly one match resolves. Zero
    #     matches is `unresolved`. Two or more is `ambiguous`, and it NAMES the namespaces rather
    #     than picking one - picking would make the citation's meaning depend on file order.
    #
    # `status` is always one of `resolved`, `unresolved`, `ambiguous` or `unknown-container`, and the
    # two fingerprints are set ONLY when the status is `resolved`, so a caller that forgot to check
    # the status gets a KeyError rather than a silent pass over a key that names nothing.
    """
    containers = containers_of(doc, flat)
    index = row_key_index(doc, flat)
    for candidate in key_candidates(raw):
        qualified = QUALIFIED.match(candidate)
        if qualified and qualified.group("container") in containers:
            container = qualified.group("container")
            bare = qualified.group("key")
            matches = [m for m in index.get(bare, []) if m["container"] == container]
            return _key_result(candidate, container, bare, matches)
        if qualified and qualified.group("container") not in containers:
            # `tasks/live-probe-todo.md` reads as a KEY unless `tasks` is a real namespace. Only report
            # a bad qualifier when the remainder names a row somewhere - otherwise the author wrote a
            # key that contains a slash, which is exactly what every todo-shapes row is.
            if qualified.group("key") in index:
                return {"status": "unknown-container", "key": candidate,
                        "container": qualified.group("container"), "namespaces": containers}
        matches = index.get(candidate, [])
        if matches:
            return _key_result(candidate, None, candidate, matches)
    return {"status": "unresolved", "key": raw, "tried": key_candidates(raw),
            "namespaces": containers}


def _key_result(candidate: str, container: "str | None", bare: str,
                matches: "list[dict]") -> dict:
    if not matches:
        return {"status": "unresolved", "key": candidate, "tried": [candidate]}
    if len(matches) > 1:
        return {"status": "ambiguous", "key": candidate,
                "namespaces": sorted(f"{m['container']}/{m['kind']}" for m in matches)}
    row = matches[0]
    container = container or row["container"]
    return {
        "status": "resolved", "key": candidate, "keyText": bare, "container": container,
        "kind": row["kind"],
        "token": bare if row["kind"] == "map" else f"id={bare}",
        "contentFingerprint": key_content_fingerprint(row["node"]),
        "rowFingerprint": key_row_fingerprint(container, row["kind"], bare),
    }


def key_fingerprints(doc: object, flat: bool = False) -> "dict[str, dict]":
    """`key -> resolution` for every key this registry declares, for the report and for a review.

    The unqualified spelling is what a citation normally uses; the qualified spellings are listed too,
    so a reader can see the collision set without re-running the tool - which is the only way to tell
    which of an ambiguous pair an author meant.
    """
    out: "dict[str, dict]" = {}
    for key, matches in sorted(row_key_index(doc, flat).items()):
        for container in sorted({m["container"] for m in matches}):
            scoped = [m for m in matches if m["container"] == container]
            out[key] = _key_result(key, container, key, scoped)
            out[f"{container}/{key}"] = _key_result(f"{container}/{key}", container, key, scoped)
    return out


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
            # `--adopt-rows` writes the LINE axis and structurally cannot write the KEY axis: a key
            # citation names no line, and deriving its promise from a commit would be recording the
            # present as the promise for a citation whose whole value is that it does not expire.
            if citation_form(key) != FORM_LINE:
                continue
            n = int(key.rpartition(":")[2])
            fp = first_rows.get(n) if 1 <= n <= len(lines) else None
            if fp is not None:
                derived[key] = fp
        source[rel] = shas[0]
    return {"rows": derived, "rowBaselineSource": source}


# ---------------------------------------------------------------------------------------------
# `--accept <registry>:<line>`: recording ONE reviewed citation, without re-baselining anything
#
# THE GAP THIS CLOSES, MEASURED. The guard can detect an unreviewed citation - S1's "no content
# baseline entry" and S5's "no row baseline entry" - and had NO vocabulary for saying "this one was
# reviewed". `--update` re-takes every fingerprint from the working tree and `--adopt-rows` re-derives
# every row from history; both are blanket operations over all 49 keys, and both are exactly what
# this guard exists to prevent. So an honest repair - a citation re-pointed at the row its sentence
# names - could land in NO state that was both truthful and green: it retired one finding and raised
# two, and the only ways to clear those two were the two blanket operations. A gate with no narrow
# repair is a gate people reach for the blanket one with.
#
# WHAT A REVIEW IS, AND WHY IT IS A THIRD KIND OF RECORD. There are three provenances, not two:
# `--update` records the PRESENT as the promise (blind, and the move that made 6a27e30f9 invisible);
# `--adopt-rows` records the registry's FIRST COMMIT (date-free, and unable to speak for a citation
# written after that date - which is every re-point); and `--accept` records a REVIEW: on a stated
# day, a named citing sentence was read, the row at the cited line was identified, and THAT is what
# the citation promises from now on. It is strictly weaker than the first-commit axis - it cannot
# speak for anything before the review - and it is stored separately under `reviewed` so it can
# never be read as first-commit provenance. `S6 REVIEWED-AXIS` prints every one of them on every
# run, green included.
#
# IT REFUSES, AND EVERY REFUSAL IS NAMED, BECAUSE A REVIEW FLAG THAT ACCEPTS ANYTHING IS A RE-BASELINE
# WITH BETTER MANNERS. Refused: a key no document cites (a typo must not create a permanent record of
# a line that does not exist); a line past the end of its file; a line with no resolvable row; a line
# whose row carries NO declared identity (a closing `},` ends at its parent, so `row_token` returns
# nothing and there is nothing to say was reviewed); a key that ALREADY has a baseline entry on
# either axis (filling a hole is recording a review, overwriting a comparison is re-baselining, and
# the difference is the whole value of the flag); and any wildcard, range, or bare registry name -
# those are `--update` and `--adopt-rows`, and the message says which.
#
# THE SENTENCE CROSS-CHECK IS WHAT MAKES "REVIEWED" A CLAIM AND NOT A LABEL, AND ITS LIMIT IS
# STATED BECAUSE IT IS REAL. `--accept` reads the citing sentence - the cited line's markdown
# paragraph, or the single line in a code file - and asks whether it names a row of this registry.
# If it names one and that is not the row at the cited line, the sentence and the line DISAGREE and
# the flag refuses: accepting would bless exactly the defect S5 is blind to. If the sentence names
# the row at the cited line, the record is `corroborated`. If the sentence names no row of this
# registry at all, no cross-check is possible and the record is `unaudited` - accepted, but printed
# and reported as resting on the reviewer's own reading rather than on a machine check. It is NOT
# presented as safe in that case, because it is not: a document that cites a line by number and
# never says which row it means is exactly the case where only a human can adjudicate.
# ---------------------------------------------------------------------------------------------

#: The minimum length of a string before it can be treated as naming a row. Below it, ordinary prose
#: words would collide with a registry's map keys and turn the cross-check into noise.
IDENTITY_VALUE_MIN = 6

#: How far the cross-check reads either side of the citing line. Bounded on purpose: the sentence is
#: the claim, and a claim spanning 40 lines is not adjudicable by reading it either.
CITATION_WINDOW_LINES = 8

#: A line that begins one of these starts a NEW claim, so the window stops there rather than running
#: on into the next sentence.
_BLOCK_START = re.compile(r"^\s*(?:#{1,6}\s|[-*+]\s|\d+[.)]\s|```|~~~)")

#: The closed vocabulary of adjudications a review record may carry. A record whose value is not one
#: of these refuses rather than falling back to a default, because "unknown" and "unchecked" are
#: different facts and a reader must not have to guess which one a record is.
ADJUDICATIONS = ("corroborated", "unaudited")


def row_identity_values(doc: object) -> "set[str]":
    """Every string this registry uses to NAME A ROW, and nothing else.

    Two sources, and the distinction is the point: an ARRAY row is named by a field it declares
    (`id`, `verificationId`, `script`, ...), while a MAP row is named by its own key. A row's FIELD
    NAMES are therefore deliberately excluded - `paths`, `project` and `level` are not identities, and
    a cross-check that treated them as ones would refuse on any sentence containing the word
    "project". Depth is bounded so a row's own nested values cannot masquerade as row names.
    """
    out: set[str] = set()

    def add(value: object) -> None:
        if isinstance(value, str) and len(value) >= IDENTITY_VALUE_MIN:
            out.add(value)

    def declared(node: object) -> None:
        if isinstance(node, dict):
            for field in IDENTITY_FIELDS:
                add(node.get(field))
        elif isinstance(node, list):
            for item in node:
                declared(item)

    def descend(node: object, depth: int) -> None:
        if depth > 3:
            return
        if isinstance(node, dict):
            for key, value in node.items():
                if depth >= 1 and isinstance(value, dict):
                    add(key)              # a MAP row is named by the key it sits under
                declared(value)
                descend(value, depth + 1)
        elif isinstance(node, list):
            for item in node:
                declared(item)
                descend(item, depth + 1)

    if isinstance(doc, dict):
        for container in doc.values():
            descend(container, 1)
    return out


def citation_window(path: pathlib.Path, line: int) -> str:
    """The SENTENCE a citation is asserted in - not the paragraph, and that distinction is load-bearing.

    A markdown citation's claim often wraps, so a claim split across two lines must be read whole. But
    a PARAGRAPH is not the claim: a report routinely cites three registry lines in one paragraph and
    names a different row in each, and a window that spans them cross-checks every citation against
    every other one's row name. Measured on the current corpus, that made `:2862` read as DENIED
    because the NEXT line names `core-atoms` for a different citation - a false refusal produced by a
    window one sentence too wide. A citation's sentence therefore ends at:

      * a blank line, or a line opening a new block (a heading, a list item, a fence), because past
        that point the text is a different claim;
      * ANY OTHER CITATION OF THE SAME REGISTRY, because the line naming it is a new claim about a
        new row and its row names belong to that citation, not this one.

    `CITATION_WINDOW_LINES` bounds it in both directions so one citation in a long report cannot drag
    the whole document in. An unreadable file yields an empty window, which the caller treats as "no
    cross-check possible", never as "the sentence agrees".
    """
    try:
        lines = path.read_text(encoding="utf-8", errors="ignore").splitlines()
    except OSError:
        return ""
    n = len(lines)
    anchor = line - 1
    if not 0 <= anchor < n:
        return ""
    registry = CITATION.search(lines[anchor])
    registry = registry.group("basename") if registry else None

    def stops(text: str, is_anchor: bool) -> bool:
        if not text.strip() or _BLOCK_START.match(text):
            return True
        # A citation of the SAME registry on another line is a different claim. On the anchor line
        # this is ignored: two citations of one registry in a single line are one sentence making two
        # coordinated claims, and cutting between them would drop half of each.
        if not is_anchor and registry and CITATION.search(text) \
                and CITATION.search(text).group("basename") == registry:
            return True
        return False

    first = last = anchor
    while first > 0 and first > anchor - CITATION_WINDOW_LINES and not stops(lines[first - 1], False):
        first -= 1
    while last + 1 < n and last < anchor + CITATION_WINDOW_LINES and not stops(lines[last + 1], False):
        last += 1
    return "\n".join(lines[first:last + 1])


#: A BARE `:NNNN` continuation - the second and third citations of a sentence that spelled the
#: registry basename only once. Measured, not theoretical: `:2862-2912`, then "`:5995-6003`" and
#: "`:878-885`" are three coordinated claims of ONE sentence, and the bare forms are what two of
#: them look like. Counting only fully-spelled keys read that sentence as citing one line.
_BARE_CITATION = re.compile(r"(?<![\w.])[:：](?P<line>\d+)")


def sentence_registry_keys(window: str) -> "set[str]":
    """Every checked registry this window points at, spelled or bare, in EITHER form.

    A bare `:NNNN` counts ONLY when the window also spells a basename for the same registry, because
    otherwise the colon is punctuation. A range (`2862-2912`) contributes its opening line, which is
    the line a citation of that span is anchored at. There is NO bare `#key` continuation form, and
    that is a decision rather than an omission: `#` is a markdown heading, a GitHub anchor and a hex
    colour, so a bare `#name` would be read as whatever the surrounding prose made of it. A key
    citation is always spelled in full.
    """
    spelled = {m.group("basename") for m in CITATION.finditer(window)}
    keys = {citation_text(m.group("basename"), m) for m in CITATION.finditer(window)}
    for name in spelled:
        for m in _BARE_CITATION.finditer(window):
            keys.add(f"{name}:{int(m.group('line'))}")
    return keys


def adjudicate(window: str, token: str, names: "set[str]", cited_key: str = "") -> str:
    """How the citing sentence stands to the row at the cited line: `corroborated`, `denies`, or
    `unaudited`. The three answers are kept apart because only two of them are machine evidence.

    A token is matched on word-ish boundaries (`A-Za-z0-9_` excluded from the boundary set) rather
    than as a bare substring, so `seedsmith-tests` is not "found" inside `seedsmith-tests-extra` -
    but a hyphen or a dot IS a boundary, because the registry's own row names contain both and
    `guard-generated-seed` really does name the row `generated-seed`.

    WHY `denies` REQUIRES A SINGLE-CITATION SENTENCE, MEASURED NOT ASSUMED. The obvious rule - "the
    sentence names a row of this registry and it is not this row's" - is UNSOUND, and the current
    corpus proves it. `tasks/reports/resume-21b-effect-pipeline-triage-20260925.md:176` reads "That
    registry path selects the seed-items corpus/validator checks
    (`verification-boundaries.v1.json:2862-2912`); the Core source selects the effects-owner boundary
    (`:5995-6003`); the atom test selects `core-atoms` (`:878-885`)". It is ONE sentence making THREE
    coordinated claims, and the paragraph-scan version of this check read it as DENIAL because
    `core-atoms` is named for a DIFFERENT citation - a false refusal of a key a human had verified.
    Worse, that sentence never spells `seedsmith-items` at all; it says "seed-items", so even a
    perfect sentence scope would find no name to match.

    So the refusal is gated on what a lexical scan can actually establish: the sentence must carry
    EXACTLY ONE citation of this registry, must name at least one row identity of it, and must not
    name the row at the cited line. With two or more citations in one sentence the row names are not
    attributable to this one by any means this tool has, so the answer is `unaudited` and the
    reviewer's reading carries it. That is a NARROWER refusal than the naive one and it is the
    narrowest one that is sound.
    """
    if not token:
        return "unaudited"
    value = token.split("=", 1)[1] if "=" in token else token
    pattern = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(value) + r"(?![A-Za-z0-9_])")

    def named_in(text: str) -> bool:
        return any(re.search(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])", text)
                   for name in names)

    if pattern.search(window):
        return "corroborated"
    keys = sentence_registry_keys(window)
    if cited_key and len(keys) != 1:
        # A coordinated sentence: its row names belong to claims this scan cannot separate.
        return "unaudited"
    if named_in(window):
        return "denies"
    return "unaudited"


def parse_accept_key(raw: str) -> str:
    """`raw` -> the canonical citation key, in whichever form it was written, or a named refusal.

    NEVER A GUESS, AND NEVER A NORMALISATION. Two forms are accepted - `<registry>:<line>` and
    `<registry>#<key>` - and both are stored EXACTLY as written, because a stored key that differs
    from the citation a reader can see is the failure this whole guard exists to prevent. There is no
    third shape, and a key that resolves is not required here: resolution is a registry fact checked
    at record time and re-checked on every run, and requiring it at parse time as well would mean two
    places that could disagree.

    A WILDCARD, RANGE, OR BARE REGISTRY IS REFUSED OUTRIGHT, AND THE MESSAGE NAMES THE FLAG THAT
    DOES THAT JOB. The whole value of `--accept` is that it is narrow: it records one citation a
    reviewer looked at. `*`, `registry:*`, `registry:100-200` and `registry` are all blanket
    operations wearing this flag's name, and a review of forty-nine citations is not a review.
    """
    if any(ch in raw for ch in "*?[]{},; "):
        raise CannotRun(
            f"--accept {raw!r} is a wildcard or a list, and this flag records ONE reviewed citation "
            f"at a time. Re-baselining every key at once is what --update (content) and --adopt-rows "
            f"(row) are for, and mixing the two vocabularies is how a blanket re-baseline gets "
            f"accepted as fifteen individual reviews. Pass one citation per flag, spelled either "
            f"<registry>:<line> or <registry>#<key>.")
    if raw.count("#") > 1:
        raise CannotRun(
            f"--accept {raw!r} spells more than one `#`. A citation is `<registry>:<line>` or "
            f"`<registry>#<key>` and the second form takes no further `#`; anything else is a "
            f"fragment of a larger identifier, not a row this guard can resolve.")
    coordinate = COORDINATE.fullmatch(raw)
    if coordinate:
        # Refused HERE rather than left to fall through, because two of the checks below would each
        # report it for the wrong reason: `rpartition(":")` leaves `name` as
        # `verification-boundaries.v1.json:4090`, which is not a registry this guard checks, so the
        # registry check would blame the registry rather than the third component that caused it.
        raise CannotRun(
            f"--accept {raw!r} is a scanner COORDINATE (`<registry>:<line>:<column>`), not a citation. "
            f"It records that a match was found at line {coordinate.group('line')}, column "
            f"{coordinate.group('column')} of a scanned file, and promises nothing about the row line "
            f"{coordinate.group('line')} holds - there is no promise here to review. If a document "
            f"really does promise what that line holds, the citation is the two-component spelling "
            f"{coordinate.group('basename')}:{coordinate.group('line')} and that is the form to review.")
    match = CITATION.fullmatch(raw)
    if "#" in raw:
        name = raw.partition("#")[0]
    else:
        head, sep, _tail = raw.rpartition(":")
        # No separator at all means the author wrote a BARE REGISTRY, which is a blanket operation
        # wearing this flag's name; its "registry name" is the whole string, not the empty prefix.
        name = head if sep else raw
    if name not in REL_OF:
        raise CannotRun(
            f"--accept {raw!r} names registry {name!r}, which this guard does not check. The checked "
            f"registries are {', '.join(sorted(REL_OF))}; a review of a file nothing here reads would "
            f"be a record of nothing.")
    if citation_form_of_spelling(raw) == FORM_KEY:
        bare = raw.partition("#")[2]
        if not bare.strip():
            raise CannotRun(
                f"--accept {raw!r} names no row key. The two shapes are <registry>:<line> and "
                f"<registry>#<key>, one citation per flag, and this one spells neither. A whole "
                f"registry is --update or --adopt-rows, not this.")
    else:
        _name, sep, line = raw.rpartition(":")
        if not sep or not line:
            raise CannotRun(
                f"--accept {raw!r} names no line. The shape is <registry>:<line> or "
                f"<registry>#<key>, one citation per flag. A whole registry is --update or "
                f"--adopt-rows, not this.")
        if not line.isdigit():
            raise CannotRun(
                f"--accept {raw!r} names a non-numeric line. This flag records one cited LINE, and a "
                f"range or a wildcard here is a re-baselining request (--update, --adopt-rows).")
    if not match:
        # Reachable only for a spelling the two branches above each accepted but `CITATION` will not
        # match whole - which is a defect in the parser rather than in the author's citation, so it is
        # refused by name rather than guessed at.
        raise CannotRun(
            f"--accept {raw!r} passed the shape checks but is not a citation this guard can read. "
            f"This is a defect in the guard's own pattern, not something to record: the shape is "
            f"<registry>:<line> or <registry>#<key>.")
    return raw


def citation_form_of_spelling(raw: str) -> str:
    """Which form a NOT-YET-VALIDATED spelling claims to be, or the line form when it claims neither.

    Used only to pick the wording of a refusal, so the default is the form whose refusal is the more
    conservative one: an unrecognised string is reported as if it were trying to be a line citation,
    because that message is the one naming the two blanket flags.
    """
    return FORM_KEY if "#" in raw else FORM_LINE


def token_value(token: str) -> str:
    """The identity itself, without the `field=` prefix `row_token` adds."""
    return token.split("=", 1)[1] if "=" in token else token


def validate_reviewed(stored: dict, geo_by_name: dict) -> dict:
    """The `reviewed` axis, validated rather than trusted. Fails closed, like every other axis.

    A hand-edited or truncated record is refused, not read: a review record with no stated reason is
    a suppression with extra steps, one with no fingerprints is a promise with nothing behind it, and
    one that duplicates a key already on the adopted axes would put two provenances on one key and
    make "compared against the registry's first commit" false for it.

    THIS VALIDATES THE LINE FORM AND ONLY THE LINE FORM, AND THAT IS DELIBERATE. A key citation has
    no line, so admitting one here would mean loosening `line.isdigit()` for the whole axis, which
    would weaken the line form's own refusal to name a checked-registry key. Key citations live in
    `reviewedKeys` and are validated by `validate_reviewed_keys`, which is stricter, not looser.
    """
    reviewed = stored.get("reviewed") or {}
    if not isinstance(reviewed, dict):
        raise CannotRun("the baseline's `reviewed` axis is not an object.")
    for key, entry in sorted(reviewed.items()):
        name, sep, line = key.rpartition(":")
        if not sep or name not in geo_by_name or not line.isdigit():
            raise CannotRun(
                f"the baseline's `reviewed` record {key!r} is not a `<checked registry>:<line>` key. "
                f"A record of nothing is indistinguishable from a suppression. (A KEY citation "
                f"belongs in `reviewedKeys`, which this axis does not read.)")
        _validate_review_entry(key, entry)
    return reviewed


def validate_reviewed_keys(stored: dict, geo_by_name: dict) -> dict:
    """The `reviewedKeys` axis: one dated review per KEY-form citation. Fails closed like its sibling.

    STRICTER THAN `validate_reviewed`, on purpose, because a key citation can be wrong in a way a line
    citation cannot be checked for. A record is refused unless it carries both fingerprints, a stated
    reason, a citing document, an adjudication from the closed vocabulary, and the two facts that
    identify WHICH row was reviewed - its namespace and its kind - because those are exactly the two
    the row axis compares, and a record that omitted them would let the row axis pass by default.
    """
    reviewed = stored.get("reviewedKeys") or {}
    if not isinstance(reviewed, dict):
        raise CannotRun("the baseline's `reviewedKeys` axis is not an object.")
    for key, entry in sorted(reviewed.items()):
        name, sep, bare = key.partition("#")
        if not sep or name not in geo_by_name or not bare.strip():
            raise CannotRun(
                f"the baseline's `reviewedKeys` record {key!r} is not a "
                f"`<checked registry>#<row key>` key. A record of nothing is indistinguishable from a "
                f"suppression. (A LINE citation belongs in `reviewed`, which this axis does not read.)")
        if not isinstance(entry, dict):
            raise CannotRun(f"the baseline's `reviewedKeys` record for {key} is not an object.")
        if entry.get("adjudication") not in ADJUDICATIONS:
            raise CannotRun(
                f"the baseline's `reviewedKeys` record for {key} carries adjudication "
                f"{entry.get('adjudication')!r}, which is not one of {list(ADJUDICATIONS)}. 'unknown' "
                f"and 'unchecked' are different facts and a reader must not have to guess which.")
        for field in ("contentFingerprint", "rowFingerprint"):
            if not re.fullmatch(r"[0-9a-f]{16}", str(entry.get(field, ""))):
                raise CannotRun(
                    f"the baseline's `reviewedKeys` record for {key} has no usable {field}. A review "
                    f"that pins nothing is not a review.")
        for field in ("why", "citedFrom", "recordedOn", "container", "kind", "rowToken"):
            if not str(entry.get(field, "")).strip():
                raise CannotRun(
                    f"the baseline's `reviewedKeys` record for {key} states no {field}. The whole claim "
                    f"of a key review is that a row was identified in a sentence read on a stated day, "
                    f"so a record missing any of these is a promise with nothing behind it.")
    return reviewed


def _validate_review_entry(key: str, entry: object) -> None:
    """The checks `validate_reviewed` applies to every one of its records, in one place."""
    if not isinstance(entry, dict):
        raise CannotRun(f"the baseline's `reviewed` record for {key} is not an object.")
    if entry.get("adjudication") not in ADJUDICATIONS:
        raise CannotRun(
            f"the baseline's `reviewed` record for {key} carries adjudication "
            f"{entry.get('adjudication')!r}, which is not one of {list(ADJUDICATIONS)}. 'unknown' "
            f"and 'unchecked' are different facts and a reader must not have to guess which.")
    if not str(entry.get("why", "")).strip():
        raise CannotRun(
            f"the baseline's `reviewed` record for {key} states no reason. A review with no stated "
            f"cause cannot be told apart from a blanket re-baseline, which is the thing this axis "
            f"exists to make impossible.")
    if not str(entry.get("citedFrom", "")).strip():
        raise CannotRun(
            f"the baseline's `reviewed` record for {key} names no citing document. The whole claim "
            f"of a review is that a sentence was read, so a record without one says nothing.")
    for field in ("contentFingerprint", "rowFingerprint"):
        if not re.fullmatch(r"[0-9a-f]{16}", str(entry.get(field, ""))):
            raise CannotRun(
                f"the baseline's `reviewed` record for {key} has no usable {field}. A review "
                f"that pins nothing is not a review.")


def review_records(*, requested: "list[str]", why: str, ws: pathlib.Path,
                   citations: dict, current: dict, current_rows: dict, current_row_fp: dict,
                   stored: dict, docs: dict, keys: "dict[str, dict]") -> "tuple[list[dict], list[dict]]":
    """Validate EVERY requested citation, then return `(line records, key records)`.

    All-or-nothing by construction. Nothing is written here. The caller writes once, after this has
    returned, so a refusal on the fourth citation of five leaves the baseline byte-identical to what it
    was - a review flag that half-applied is a state no reader can interpret.
    """
    if not str(why).strip():
        raise CannotRun(
            "--accept was given no --accept-why. A review recorded with no stated cause is "
            "indistinguishable from a suppression, which is the shape this flag must not have. Say "
            "which sentence you read against which row.")
    line_records: list[dict] = []
    key_records: list[dict] = []
    seen: set[str] = set()
    for raw in requested:
        key = parse_accept_key(raw)
        if key in seen:
            raise CannotRun(f"--accept names {key} twice. One citation is one review.")
        seen.add(key)
        name = registry_of(key)
        if key not in citations:
            raise CannotRun(
                f"--accept {key}: no document in the workspace cites that citation. A review of a key "
                f"nothing points at records nothing, and a typo here would otherwise create a "
                f"permanent record of a row that does not exist. Check the spelling.")
        where = citations[key]
        window = citation_window(ws / where[0], where[1])
        if citation_form(key) == FORM_KEY:
            key_records.append(_review_key(key, name, keys, stored, docs, window, where, why))
        else:
            line_records.append(
                _review_line(key, name, citations, current, current_rows, current_row_fp,
                             stored, docs, window, where, why))
    return line_records, key_records


def _review_line(key: str, name: str, citations: dict, current: dict, current_rows: dict,
                 current_row_fp: dict, stored: dict, docs: dict, window: str,
                 where: "tuple[str, int]", why: str) -> dict:
    """One reviewed LINE citation. Unchanged in substance from the first `--accept`."""
    if current[key] is None:
        raise CannotRun(
            f"--accept {key}: the line is past the end of its file. A citation that points at "
            f"nothing has no row to review; re-point the document first.")
    pair = current_rows[key]
    if pair is None:
        raise CannotRun(
            f"--accept {key}: the line has no resolvable JSON row. This guard will not record a "
            f"review of a row it cannot name.")
    token = row_token(docs[name], pair)
    if not token:
        raise CannotRun(
            f"--accept {key}: the line sits in a row with NO declared identity (it is "
            f"{row_label(pair)}), so there is nothing to record that was reviewed. A closing "
            f"`}},` ends at its parent element and names no row of its own; a citation of one "
            f"cannot be adjudicated against it by anyone, including this tool.")
    if key in stored.get("fingerprints", {}) or key in stored.get("rows", {}):
        raise CannotRun(
            f"--accept {key}: this key ALREADY has a baseline entry. Filling a hole is recording a "
            f"review; overwriting a comparison is re-baselining, and only --update or "
            f"--adopt-rows may do that. If the line really did drift, the citing sentence has to "
            f"be re-pointed at the line it means.")
    if key in (stored.get("reviewed") or {}):
        raise CannotRun(
            f"--accept {key}: this key is already in the `reviewed` axis. Re-reviewing it would "
            f"silently re-date its provenance, and a review is dated precisely so it cannot be.")
    verdict = adjudicate(window, token, row_identity_values(docs[name]), cited_key=key)
    if verdict == "denies":
        raise CannotRun(
            f"--accept {key}: the citing sentence names a DIFFERENT row of {name}. "
            f"{where[0]}:{where[1]} resolves to `{token_value(token)}`, and accepting would "
            f"bless a citation whose own sentence contradicts the line it points at - the exact "
            f"defect the row axis is blind to. Re-point the document at the row its sentence "
            f"names.")
    return {
        "key": key, "registry": name, "line": line_of(key),
        "citedFrom": f"{where[0]}:{where[1]}",
        "rowLabel": row_label(pair), "rowToken": token,
        "rowFingerprint": current_row_fp[key], "contentFingerprint": current[key],
        "adjudication": verdict, "recordedOn": datetime.date.today().isoformat(), "why": why.strip(),
    }


def _review_key(key: str, name: str, keys: "dict[str, dict]", stored: dict, docs: dict,
                window: str, where: "tuple[str, int]", why: str) -> dict:
    """One reviewed KEY citation.

    THE REFSUALS ARE TIGHTER THAN THE LINE FORM'S, AND EACH ONE IS A CASE WHERE A RECORD WOULD BE A
    LIE. A key that resolves to nothing, or to two rows, has no row to review; a key already reviewed
    cannot be re-dated; and a citing sentence that names a DIFFERENT row is the same contradiction the
    line form refuses, checked here against the row the key resolves to rather than against the row a
    line happens to sit in.

    THE ADJUDICATION IS THE SAME CROSS-CHECK AND IT IS STRONGER HERE. A line citation has to be
    re-derived by a reader to know what it meant; a key citation SPELLS the row it means in the
    sentence, so `corroborated` is the normal answer and `unaudited` now means something sharper - the
    sentence carries the citation but names no OTHER row of this registry, so there was nothing to
    contradict. `denies` is still the refusal.
    """
    flat = is_flat(name)
    resolution = keys.get(key) or resolve_key(docs[name], row_key_of(key), flat)
    status = resolution.get("status")
    if status == "unresolved":
        raise CannotRun(
            f"--accept {key}: no row of {name} carries that key. Tried {resolution.get('tried')}; "
            f"its namespaces are {', '.join(resolution.get('namespaces', []))}. A review of a row "
            f"that does not exist is a record of nothing, and a key citation is the one form where "
            f"a typo cannot be caught later, because nothing else in the pipeline reads the key.")
    if status == "ambiguous":
        raise CannotRun(
            f"--accept {key}: that key names {len(resolution.get('namespaces', []))} different rows "
            f"of {name} - {', '.join(resolution.get('namespaces', []))} - so the citation does not "
            f"say which one it means, and the guard will not pick one for you. Name the namespace: "
            f"`--accept {name}#<namespace>/{row_key_of(key)}`.")
    if status == "unknown-container":
        raise CannotRun(
            f"--accept {key}: `{resolution.get('container')}` is not a namespace of {name}. Its "
            f"namespaces are {', '.join(resolution.get('namespaces', []))}. A key that contains a `/` "
            f"and whose prefix is not a real namespace is read as a whole key, so this is most "
            f"likely a misspelling.")
    if status != "resolved":
        raise CannotRun(
            f"--accept {key}: the key did not resolve to a row ({status!r}). Refusing rather than "
            f"recording a review of a row this guard cannot name.")
    if key in (stored.get("reviewedKeys") or {}):
        raise CannotRun(
            f"--accept {key}: this key is already in the `reviewedKeys` axis. Re-reviewing it would "
            f"silently re-date its provenance, and a review is dated precisely so it cannot be.")
    verdict = adjudicate(window, resolution["token"], row_identity_values(docs[name]),
                         cited_key=key)
    if verdict == "denies":
        raise CannotRun(
            f"--accept {key}: the citing sentence names a DIFFERENT row of {name}. "
            f"{where[0]}:{where[1]} resolves to `{resolution['keyText']}`, and accepting would "
            f"bless a citation whose own sentence contradicts the row it points at. Re-point the "
            f"document at the row its sentence names.")
    return {
        "key": key, "registry": name, "rowKey": resolution["keyText"],
        "container": resolution["container"], "kind": resolution["kind"],
        "citedFrom": f"{where[0]}:{where[1]}",
        "rowToken": resolution["token"],
        "rowFingerprint": resolution["rowFingerprint"],
        "contentFingerprint": resolution["contentFingerprint"],
        "adjudication": verdict, "recordedOn": datetime.date.today().isoformat(), "why": why.strip(),
    }


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


def collect(ws: pathlib.Path) -> "tuple[dict[str, tuple[str, int]], dict[str, int], dict[str, int]]":
    """`citation spelling -> (citing file, citing line)`, then two per-registry refusal counts.

    A citation is spelled in ONE of two forms and the spelling IS the key, so one dict holds both and
    `citation_form` decides which axis each is compared on. Nothing is normalised away: a document that
    cited `verification-boundaries.v1.json:802` and a document that cited
    `verification-boundaries.v1.json#core-fallback` are two DIFFERENT keys naming the same row, and
    they are meant to be counted separately, because "how much of this workspace still cites by line"
    is the reading this form exists to make visible.

    The counts are the point of the filter being visible: a citation excluded because it sat in a
    comment, and a `<registry>:<line>:<column>` coordinate excluded because it is a scanner position
    rather than a citation, are both reported every run. So moving a real citation into a comment, or
    re-spelling it as a coordinate, shows up as a changed exclusion count rather than as a quiet pass.
    The two are counted SEPARATELY because they are different mistakes and a reader auditing either
    one needs to see it alone; a coordinate inside a comment is counted once, as a comment.
    """
    found: dict[str, tuple[str, int]] = {}
    refused: dict[str, int] = {name: 0 for name, _, _ in REGISTRIES}
    coordinates: dict[str, int] = {name: 0 for name, _, _ in REGISTRIES}
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
            key = citation_text(name, m)
            if key not in found:
                found[key] = (str(p.relative_to(ws)).replace("\\", "/"), line)
        for m in COORDINATE.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            col = m.start() - (text.rfind("\n", 0, m.start()) + 1)
            if col >= cols.get(line, NO_COMMENT):
                continue
            coordinates[m.group("basename")] += 1
    return found, refused, coordinates


def citation_text(name: str, match: "re.Match") -> str:
    """The canonical citation spelling for one regex match, in whichever form it was written.

    The key form is stored EXACTLY as written, including a sentence-final `.`, and resolution against
    the registry happens later and reports the spelling it settled on. Canonicalising the trailing dot
    here instead would make the stored key differ from the text a reader sees, which is the one thing a
    citation must not do.
    """
    if match.group("key") is not None:
        return f"{name}#{match.group('key')}"
    return f"{name}:{int(match.group('line'))}"


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
    citations, refused, coordinates = collect(ws)
    geos = [geometry(name, rel, container, core) for name, rel, container in REGISTRIES]

    current: dict[str, str | None] = {}
    bodies: dict[str, "list[str]"] = {}
    rows: dict[str, "dict[int, str]"] = {}
    row_pairs: dict[str, "dict[int, tuple[str, str]]"] = {}
    docs: dict[str, object] = {}
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
        docs[name] = doc
        rows[name] = row_fingerprints(text, doc)
        row_pairs[name] = row_identity(text)

    current_rows: dict[str, "tuple[str, str] | None"] = {}
    line_citations = {k for k in citations if citation_form(k) == FORM_LINE}
    for key in line_citations:
        name = registry_of(key)
        n = line_of(key)
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

    current_row_fp = {k: rows[registry_of(k)][line_of(k)]
                      for k in line_citations if current_rows.get(k) is not None}

    # ---- the KEY form: resolve every `#key` against its registry's own namespaces ---------------
    #
    # Resolution is a REGISTRY fact, not a citation fact, so it is computed for every cited key and
    # stored whole - including the failure - because the gate's job for an unresolved key is to report
    # it, and a resolver that returned None would make "unresolvable" indistinguishable from "not
    # checked". Nothing here raises and nothing here defaults to a row.
    key_resolutions: dict[str, dict] = {}
    for key in citations:
        if citation_form(key) != FORM_KEY:
            continue
        name = registry_of(key)
        key_resolutions[key] = resolve_key(docs[name], row_key_of(key), is_flat(name))

    findings: list[dict] = []
    for geo in geos:
        reg = geo["registry"]
        cited_lines = sorted(line_of(k) for k in citations
                              if registry_of(k) == reg and citation_form(k) == FORM_LINE)
        geo["citations"] = sum(1 for k in citations if registry_of(k) == reg)
        geo["lineCitations"] = len(cited_lines)
        geo["keyCitations"] = geo["citations"] - len(cited_lines)
        geo["distinct_lines"] = len(cited_lines)
        geo["max_cited"] = cited_lines[-1] if cited_lines else 0
        geo["ambiguous_lines"] = ambiguous_cited_lines(bodies[reg], cited_lines)
        # S3 - the geometric invariant, derived from the file and independent of any baseline. KEY
        # citations are deliberately NOT in `max_cited`: they name no line, so growth above them moves
        # nothing they point at, and counting them would make the registry look more constrained than
        # the documents actually are.
        if geo["max_cited"] >= geo["close"]:
            findings.append(dict(
                code="S3-NO-SAFE-APPEND-POINT", registry=reg,
                note=f"the append container {geo['container']!r} closes at line {geo['close']} and the "
                     f"highest cited line is {geo['max_cited']}, so there is NO line at which a row can "
                     f"be added without moving a cited line. Every LINE citation of this registry must "
                     f"be re-pointed deliberately, or re-spelled in the KEY form, which is immune to "
                     f"this."))

    return {"citations": citations, "current": current, "currentRows": current_rows,
            "currentRowFingerprints": current_row_fp, "docs": docs, "keys": key_resolutions,
            "geometries": geos, "findings": findings, "refused": refused,
            "coordinates": coordinates}


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
    parser.add_argument("--accept", action="append", default=[], metavar="<registry>:<line>|#<key>",
                        help="record a REVIEWED baseline for exactly this citation, and nothing "
                             "else. Repeatable; one citation per flag. It accepts BOTH forms - "
                             "`<registry>:<line>` and `<registry>#<key>` - and writes the axis "
                             "that form is compared on: the LINE form into `reviewed`, the KEY "
                             "form into `reviewedKeys`. For a KEY citation it writes the WHOLE "
                             "row's content fingerprint plus a row fingerprint carrying its "
                             "namespace, element kind and key, and NO line number and NO array "
                             "index. It refuses a key no document cites, a key that resolves to no "
                             "row or to two, a line with no resolvable row identity, a key that "
                             "already has a baseline entry, a citing sentence that names a "
                             "different row, and any wildcard, range or bare registry name.")
    parser.add_argument("--accept-why", default="", metavar="TEXT",
                        help="the reason every --accept in this run is recorded with. REQUIRED with "
                             "--accept: a review that states no cause is indistinguishable from a "
                             "suppression.")
    parser.add_argument("--json", action="store_true", help="print the machine-readable verdict")
    parser.add_argument("--root", default="", help="workspace root (default: the resolved workspace root)")
    args = parser.parse_args(argv)

    if args.update and args.adopt_rows:
        print("REFUSING: --update and --adopt-rows write different axes with different provenance. "
              "--update re-takes the CONTENT axis from the working tree, which is the move that made "
              "6a27e30f9 invisible; --adopt-rows derives the ROW axis from history. Running both at "
              "once would leave the file claiming a provenance it does not have.", file=sys.stderr)
        return 2

    if args.accept and (args.update or args.adopt_rows):
        print(f"REFUSING: --accept cannot run with --update or --adopt-rows. --accept records ONE "
              f"reviewed citation and touches no other key; the other two re-derive whole axes from "
              f"the working tree or from history. Running them together would file a per-key review "
              f"inside a blanket re-baseline, and the record would then claim a narrow provenance it "
              f"does not have.", file=sys.stderr)
        return 2
    if args.accept_why and not args.accept:
        print("REFUSING: --accept-why was given with no --accept. A reason is recorded with a review, "
              "never on its own; if you meant to re-baseline, that is --update or --adopt-rows.",
              file=sys.stderr)
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

    keys_by_registry = {name: sorted(k for k in citations if registry_of(k) == name)
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
                      "unverified": [], "rows": {}, "rowBaselineSource": {},
                      "reviewed": {}, "reviewedKeys": {}}
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

    # ---- the reviewed axes: third provenance, validated like the other two ----------------------
    try:
        reviewed = validate_reviewed(stored, geo_by_name)
        reviewed_keys = validate_reviewed_keys(stored, geo_by_name)
    except CannotRun as exc:
        print(f"REFUSING REGISTRY-APPEND-SAFETY: {exc}", file=sys.stderr)
        return 2

    # ---- --accept: record ONE reviewed citation, and write only after every key has passed -------
    if args.accept:
        try:
            line_records, key_records = review_records(
                requested=args.accept, why=args.accept_why, ws=ws, citations=citations,
                current=current, current_rows=current_rows, current_row_fp=current_row_fp,
                stored=stored, docs=result["docs"], keys=result["keys"])
        except CannotRun as exc:
            print(f"REFUSING REGISTRY-APPEND-SAFETY: {exc}", file=sys.stderr)
            print("  Nothing was written. A refusal that had already recorded the keys before it "
                  "would be a partial review, which no reader can interpret.", file=sys.stderr)
            return 2
        # All-or-nothing, and the write happens ONCE, here.
        merged = dict(stored.get("reviewed") or {})
        for rec in line_records:
            merged[rec.pop("key")] = rec
        stored["reviewed"] = merged
        merged_keys = dict(stored.get("reviewedKeys") or {})
        for rec in key_records:
            merged_keys[rec.pop("key")] = rec
        stored["reviewedKeys"] = merged_keys
        BASELINE.write_text(json.dumps(stored, indent=1, sort_keys=True) + "\n",
                            encoding="utf-8", newline="")
        audited = stored["reviewed"]
        audited_keys = stored["reviewedKeys"]
        if args.json:
            print(json.dumps({"guard": "registry-append-safety", "ok": True, "accepted": audited,
                              "acceptedKeys": audited_keys,
                              "rowBaselineSource": row_source,
                              "note": "recorded per-citation reviews; these do NOT speak for anything "
                                      "before the date each record carries"}, indent=2,
                             ensure_ascii=False))
            return 0
        print(f"RECORDED {len(line_records)} reviewed LINE citation(s) and {len(key_records)} "
              f"reviewed KEY citation(s) -> {BASELINE.name}. No other key was touched, and "
              f"--update/--adopt-rows were not involved.")
        for key in sorted(audited):
            rec = audited[key]
            print(f"  {key}")
            print(f"      registry        : {rec['registry']}")
            print(f"      line            : {rec['line']}")
            print(f"      cited from      : {rec['citedFrom']}")
            print(f"      row identity    : {rec['rowToken']}  (row {rec['rowLabel']})")
            print(f"      content print   : {rec['contentFingerprint']}")
            print(f"      row fingerprint : {rec['rowFingerprint']}")
            print(f"      adjudication    : {rec['adjudication']}"
                  f"{'  (the citing sentence names this row; machine-checked)' if rec['adjudication'] == 'corroborated' else '  (the citing sentence names NO row of this registry, so NO machine cross-check was possible - this record rests on the reviewer, not on the tool)'}")
            print(f"      recorded on     : {rec['recordedOn']}")
            print(f"      why             : {rec['why']}")
        for key in sorted(audited_keys):
            rec = audited_keys[key]
            print(f"  {key}")
            print(f"      registry        : {rec['registry']}")
            print(f"      row key         : {rec['rowKey']}  (namespace {rec['container']}, "
                  f"{rec['kind']} row)")
            print(f"      cited from      : {rec['citedFrom']}")
            print(f"      row identity    : {rec['rowToken']}")
            print(f"      WHOLE ROW print : {rec['contentFingerprint']}  "
                  f"(the row's entire content, not one line of it)")
            print(f"      row fingerprint : {rec['rowFingerprint']}  "
                  f"(namespace + kind + key; NO line number and NO array index)")
            print(f"      adjudication    : {rec['adjudication']}"
                  f"{'  (the citing sentence spells this row key; machine-checked)' if rec['adjudication'] == 'corroborated' else '  (the citing sentence names NO OTHER row of this registry, so there was nothing to contradict - the key itself is in the sentence)'}")
            print(f"      recorded on     : {rec['recordedOn']}")
            print(f"      why             : {rec['why']}")
        print("  A review speaks for nothing BEFORE the date above. It pins the row and the content "
              "from today forward, so later drift still reds - and it is never presented as "
              "first-commit provenance.")
        return 0

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
        # `unverified`, `rows`, `reviewed` and `reviewedKeys` are carried across untouched. --update
        # re-takes the CONTENT fingerprints from the working tree; it has no business clearing a debt
        # declaration, or a debt could be erased by re-baselining - which is the one thing a re-baseline
        # must never be able to do. Nor may it touch `rows`: those are derived from registry HISTORY,
        # and taking them from the working tree is how a repointed citation becomes the recorded
        # promise. And it cannot touch `reviewedKeys` at all, so there is NO blanket operation that can
        # bless a KEY citation - the one form whose whole value is that it does not need re-baselining.
        if not isinstance(stored.get("rows"), dict) or not stored.get("rows"):
            print(f"REFUSING --update: the baseline has no `rows` axis, and --update cannot create one. "
                  f"The row axis is derived from each registry's first commit by `--adopt-rows`; taking "
                  f"it from the working tree would record the present as the promise. Run "
                  f"`--adopt-rows` first.", file=sys.stderr)
            return 2
        key_cited = [k for k in citations if citation_form(k) == FORM_KEY]
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
        print(f"  KEY axis carried across unchanged: {len(reviewed_keys)} review(s). --update "
              f"STRUCTURALLY CANNOT write it, so a KEY citation can only be baselined by --accept, one "
              f"row at a time, with a stated reason.")
        if key_cited:
            print(f"  note: {len(key_cited)} KEY citation(s) exist and were NOT baselined by this run: "
                  f"{', '.join(sorted(key_cited))}")
        if modified:
            print("  A CHANGED fingerprint means the cited line's CONTENT differs from what it held when "
                  "it was cited. Read each one above and confirm the citing sentence still means what it "
                  "says before accepting this baseline; re-baselining a moved line is how a citation "
                  "becomes a lie that no gate can see.")
        return 0

    gone = sorted(k for k in baseline if k not in current)
    reviewed_gone = sorted(k for k in reviewed if k not in current)
    reviewed_keys_gone = sorted(k for k in reviewed_keys if k not in citations)

    # A REVIEWED key is compared exactly like an adopted one, against the same two axes - which is
    # the point of storing it separately rather than in `fingerprints`. The only difference is where
    # the promise came from, and that difference is printed on every run rather than buried.
    def promised_content(key: str) -> "str | None":
        if key in baseline:
            return baseline[key]
        return (reviewed.get(key) or {}).get("contentFingerprint")

    def promised_row(key: str) -> "str | None":
        if key in rows_baseline:
            return rows_baseline[key]
        return (reviewed.get(key) or {}).get("rowFingerprint")

    def key_content_of(key: str) -> "str | None":
        return (reviewed_keys.get(key) or {}).get("contentFingerprint")

    def key_row_of_(key: str) -> "str | None":
        return (reviewed_keys.get(key) or {}).get("rowFingerprint")

    def key_provenance(key: str) -> str:
        rec = reviewed_keys.get(key)
        if rec is None:
            return ""
        return (f" (compared against a KEY REVIEW recorded {rec.get('recordedOn')}, never against the "
                f"working tree by a blanket operation)")

    def provenance_of(key: str) -> str:
        rec = reviewed.get(key)
        if key in baseline or key in rows_baseline or rec is None:
            return ""
        if rec.get("adjudication") == "unaudited":
            return (f" (compared against a REVIEW recorded {rec.get('recordedOn')} with NO machine "
                    f"cross-check, not against the registry's first commit)")
        return (f" (compared against a REVIEW recorded {rec.get('recordedOn')}, not against the "
                f"registry's first commit)")

    for key in sorted(current):
        was = promised_content(key)
        where = citations[key]
        if current[key] is None:
            findings.append(dict(code="S2-CITATION-PAST-END", registry=registry_of(key),
                                note=f"{key} points PAST THE END of its file "
                                     f"(cited from {where[0]}:{where[1]})"))
        elif was is None:
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=registry_of(key),
                                note=f"{key} is a NEW citation with no content baseline entry "
                                     f"(cited from {where[0]}:{where[1]}). Record a review of it "
                                     f"with --accept {key} once a human has read the sentence; "
                                     f"--update would re-baseline all "
                                     f"{len(current)} citations, which is what this guard exists "
                                     f"to prevent."))
        elif current[key] != was:
            findings.append(dict(code="S1-CITED-LINE-MOVED", registry=registry_of(key),
                                note=f"{key} now holds different content (cited from {where[0]}:{where[1]}) "
                                     f"- the line moved, or the row above it grew"
                                     f"{provenance_of(key)}"))

    # ---- S5, the row axis. The one check that can see a shift onto a byte-identical line. ----------
    for key in sorted(current):
        if current[key] is None:
            continue                      # S2 already speaks for a line past the end of its file
        was_row = promised_row(key)
        pair = current_rows[key]
        where = citations[key]
        if was_row is None:
            findings.append(dict(code="S5-CITED-ROW-CHANGED", registry=registry_of(key),
                                note=f"{key} is a NEW citation with no row baseline entry, so there is "
                                     f"no first-commit row to compare it against (cited from "
                                     f"{where[0]}:{where[1]}). It sits in {row_label(pair)} today. "
                                     f"Record a review of it with --accept {key}; --adopt-rows "
                                     f"would re-derive every key from history."))
        elif current_row_fp[key] != was_row:
            findings.append(dict(code="S5-CITED-ROW-CHANGED", registry=registry_of(key),
                                note=f"{key} now sits in row {row_label(pair)} "
                                     f"(cited from {where[0]}:{where[1]}), which is not the row it "
                                     f"promised"
                                     f"{provenance_of(key) or ' at ' + row_source.get(rel_of(key), 'the registry')[:9]}"
                                     f". The content fingerprint may or may not have seen this: when "
                                     f"the new row's line is byte-identical to the old one, S1 stays "
                                     f"silent and only this check reds."))

    # ---- the KEY form's four checks, and they are four because the states are four -----------------
    #
    # S7  the key names no row, or more than one. This is the check that makes the form safe to trust:
    # a key that resolves to nothing has no subject, and a key that resolves to two rows has an
    #      ambiguous one. Neither is a silent pass, and neither is guessable after the fact - both are
    #      registry facts and both are re-checked on every single run.
    # S8  the key resolves but has NO recorded promise. It is a live, resolvable citation the guard has
    #      never been told what it should hold, and `--update` structurally cannot record it - so the
    #      only way to clear it is `--accept` for that one key, with a stated reason.
    # S1' the row's WHOLE CONTENT changed. Strictly stronger than the line form's S1: the line form can
    #      only see one line, and on these registries one line in 556 is indistinguishable from its
    #      twins; a key has the whole row and the whole row is unique.
    # S5' the key resolves to a DIFFERENT ROW - a different namespace, or a different kind of element,
    #      or a different declared identity - while its content is unchanged. This is the key-space
    #      analogue of the byte-identical twin: two rows can say exactly the same thing and be two
    #      different rows, and only the identity separates them.
    for key in sorted(result["keys"]):
        resolution = result["keys"][key]
        where = citations[key]
        status = resolution.get("status")
        if status == "resolved":
            pass
        elif status == "ambiguous":
            findings.append(dict(code="S7-KEY-AMBIGUOUS", registry=registry_of(key),
                                note=f"{key} names {len(resolution.get('namespaces', []))} different "
                                     f"rows of this registry - {', '.join(resolution.get('namespaces', []))} "
                                     f"- so it does not say which one it means, and the guard will "
                                     f"not pick one (cited from {where[0]}:{where[1]}). Name the "
                                     f"namespace: `{registry_of(key)}#<namespace>/{row_key_of(key)}`. "
                                     f"Measured: 84 of the 142 `projects` keys in "
                                     f"verification-boundaries.v1.json are ALSO a `boundaries[].id`, so "
                                     f"this collision is real and not a defect in the citation."))
            continue
        elif status == "unknown-container":
            findings.append(dict(code="S7-KEY-UNRESOLVED", registry=registry_of(key),
                                note=f"{key} names `{resolution.get('container')}` as a namespace, "
                                     f"which this registry does not have (cited from "
                                     f"{where[0]}:{where[1]}). Its namespaces are "
                                     f"{', '.join(resolution.get('namespaces', []))}."))
            continue
        else:
            findings.append(dict(code="S7-KEY-UNRESOLVED", registry=registry_of(key),
                                note=f"{key} names NO row of this registry (cited from "
                                     f"{where[0]}:{where[1]}). Tried {resolution.get('tried')}. A key "
                                     f"citation is a promise about a named row, so one that names no "
                                     f"row checks nothing and is reported rather than passed."))
            continue

        was_content = key_content_of(key)
        was_row = key_row_of_(key)
        if was_content is None and was_row is None:
            findings.append(dict(code="S8-KEY-NOT-REVIEWED", registry=registry_of(key),
                                note=f"{key} resolves to `{resolution['keyText']}` in namespace "
                                     f"{resolution['container']} but has NO recorded promise of what "
                                     f"that row holds (cited from {where[0]}:{where[1]}). Record it "
                                     f"with `--accept {key}` once a human has read the sentence. "
                                     f"--update and --adopt-rows STRUCTURALLY CANNOT write this axis, "
                                     f"so no blanket re-baseline can clear it."))
        else:
            if resolution["contentFingerprint"] != was_content:
                findings.append(dict(code="S1-KEY-CONTENT-CHANGED", registry=registry_of(key),
                                    note=f"{key} resolves to `{resolution['keyText']}`, and that row's "
                                         f"WHOLE CONTENT has changed since it was reviewed (cited from "
                                         f"{where[0]}:{where[1]}){key_provenance(key)}. A key citation "
                                         f"fingerprints every field of the row, so this is stronger "
                                         f"than the line form, which sees one line in a file that "
                                         f"holds 556 byte-identical ones."))
            if resolution["rowFingerprint"] != was_row:
                findings.append(dict(code="S5-KEY-ROW-CHANGED", registry=registry_of(key),
                                    note=f"{key} now resolves to a different ROW: namespace "
                                         f"{resolution['container']}, {resolution['kind']} element, "
                                         f"identity `{resolution['keyText']}` (cited from "
                                         f"{where[0]}:{where[1]}){key_provenance(key)}. The row's "
                                         f"content may be unchanged - two rows can say the same thing "
                                         f"and be two different rows, and only the identity separates "
                                         f"them. A key citation has no line number and no array index "
                                         f"anywhere in its fingerprint, so a row inserted ABOVE this "
                                         f"one cannot have caused this."))

    if args.report:
        by_form = {f: sum(1 for k in citations if citation_form(k) == f) for f in FORMS}
        print(f"REGISTRY APPEND SAFETY REPORT - {len(citations)} citation(s) over "
              f"{len({w[0] for w in citations.values()})} document(s)")
        print(f"  CITATION FORMS: {by_form[FORM_LINE]} by LINE (`<registry>:<line>`) and "
              f"{by_form[FORM_KEY]} by KEY (`<registry>#<key>`). The KEY form is immune to a row "
              f"being inserted above it; the LINE form is not, and every LINE citation is what keeps "
              f"this registry append-only.")
        for g in geos:
            print(f"\n  {g['rel']}  ({g['lines']} lines)")
            print(f"    append container : {g['container']} closes at line {g['close']}")
            print(f"    SAFE APPEND POINT: insert immediately before line {g['close']} "
                  f"(safe for the {g['lineCitations']} LINE citation(s); the {g['keyCitations']} KEY "
                  f"citation(s) do not constrain it)")
            print(f"    cited            : {g['citations']} citation(s) = {g['lineCitations']} LINE "
                  f"({g['distinct_lines']} distinct line(s), highest {g['max_cited']}) + "
                  f"{g['keyCitations']} KEY")
            print(f"    comment matches refused: {result['refused'][g['registry']]}")
            print(f"    path:line:column coordinates refused: {result['coordinates'][g['registry']]}"
                  f" - a scanner position, not a promise about a row, so no citation is made of it")
            print(f"    cited line text not unique in file: "
                  f"{len(g['ambiguous_lines'])} of {g['distinct_lines']} - these are the citations S1 "
                  f"ALONE could not separate, and every one of them is covered by the row axis: "
                  f"{', '.join(str(n) for n in g['ambiguous_lines']) if g['ambiguous_lines'] else '(none)'}")
        print()
        for key in sorted(citations,
                          key=lambda k: (registry_of(k), k.rpartition(":")[2] if citation_form(k) == FORM_LINE
                                         else f"~{row_key_of(k)}")):
            where = citations[key]
            if citation_form(key) == FORM_KEY:
                res = result["keys"].get(key, {})
                shown = (f"{res.get('status', '?')} "
                         f"{res.get('container', '?')}/{res.get('kind', '?')} "
                         f"{res.get('contentFingerprint', '')} "
                         f"{res.get('rowFingerprint', '')}")
                print(f"    {key:<52} KEY  {shown:<62} {where[0]}:{where[1]}")
                continue
            pair = current_rows.get(key)
            print(f"    {key:<52} LINE  {current[key] or 'PAST END OF FILE':<16} "
                  f"{row_label(pair) if pair else '-':<34} {where[0]}:{where[1]}")
        print(f"\n  ROW AXIS PROVENANCE (earliest state a citation to each registry could have been "
              f"written):")
        for rel, sha in sorted(row_source.items()):
            print(f"    {rel:<46} {sha}")
        if reviewed:
            print(f"\n  REVIEWED CITATIONS ({len(reviewed)}): recorded by --accept, NOT adopted from "
                  f"the registries' first commits. Each speaks only from its recorded date forward.")
            for key in sorted(reviewed):
                rec = reviewed[key]
                print(f"    {key:<48} {rec['rowToken']:<38} {rec['adjudication']:<12} "
                      f"{rec['recordedOn']}  cited from {rec['citedFrom']}")
                print(f"        why: {rec['why']}")
        if reviewed_keys:
            print(f"\n  REVIEWED KEY CITATIONS ({len(reviewed_keys)}): recorded by --accept. No "
                  f"blanket operation can write this axis - neither --update nor --adopt-rows.")
            for key in sorted(reviewed_keys):
                rec = reviewed_keys[key]
                print(f"    {key:<52} {rec['container']}/{rec['kind']} {rec['rowToken']:<30} "
                      f"{rec['adjudication']:<12} {rec['recordedOn']}  cited from {rec['citedFrom']}")
                print(f"        whole-row print: {rec['contentFingerprint']}   "
                      f"row print: {rec['rowFingerprint']}   (no line, no index)")
                print(f"        why: {rec['why']}")
        if gone:
            print(f"\n  baseline entries no longer cited anywhere ({len(gone)}): {', '.join(gone)}")
        if reviewed_gone:
            print(f"\n  REVIEWED entries no longer cited anywhere ({len(reviewed_gone)}): "
                  f"{', '.join(reviewed_gone)}")
        if reviewed_keys_gone:
            print(f"\n  REVIEWED KEY entries no longer cited anywhere ({len(reviewed_keys_gone)}): "
                  f"{', '.join(reviewed_keys_gone)}")
        for entry in unverified:
            print(f"\n  UNVERIFIED {entry['registry']}: {entry['why']}")
        return 0

    if args.json:
        by_form = {f: sum(1 for k in citations if citation_form(k) == f) for f in FORMS}
        print(json.dumps({"guard": "registry-append-safety",
                          "ok": not findings,
                          "geometries": geos,
                          "unverified": unverified,
                          "rowBaselineSource": row_source,
                          "reviewed": reviewed,
                          "reviewedKeys": reviewed_keys,
                          "citationsByForm": by_form,
                          "citationFormNote": "the LINE form (`<registry>:<line>`) is checked against "
                                              "the registry's first commit and is what makes the "
                                              "registry append-only; the KEY form "
                                              "(`<registry>#<key>`) is checked against a dated "
                                              "per-row review and is immune to a row inserted above it",
                          "reviewedNote": "recorded by --accept; these keys are compared against a "
                                          "dated review, NOT against the registries' first commits, "
                                          "so a green run over them means no drift since the review",
                          "reviewedKeysNote": "recorded by --accept; NO blanket operation writes this "
                                               "axis, so a KEY citation cannot be re-baselined - only "
                                               "reviewed one row at a time with a stated reason",
                          "keyResolutions": result["keys"],
                          "commentMatchesRefused": result["refused"],
                           "coordinatesRefused": result["coordinates"],
                          "citations": len(citations),
                          "findings": findings}, indent=2, ensure_ascii=False))
        return 1 if findings else 0

    by_form = {f: sum(1 for k in citations if citation_form(k) == f) for f in FORMS}
    for g in geos:
        print(f"  {g['rel']}: {g['citations']} citation(s) = {g['lineCitations']} LINE "
              f"({g['distinct_lines']} distinct line(s), highest {g['max_cited']} of {g['lines']}) + "
              f"{g['keyCitations']} KEY; safe append = before line {g['close']} "
              f"({g['container']}); {result['refused'][g['registry']]} comment match(es) refused; "
              f"{result['coordinates'][g['registry']]} path:line:column coordinate(s) refused; "
              f"{len(g['ambiguous_lines'])}/{g['distinct_lines']} cited line(s) hold text that is not "
              f"unique in the file, so S1 alone cannot separate them - the row axis covers those")
    print(f"  CITATION FORMS: {by_form[FORM_LINE]} by LINE and {by_form[FORM_KEY]} by KEY. Every KEY "
          f"citation is checked against a dated review of one named row and cannot drift when a row "
          f"is inserted above it; every LINE citation constrains the safe append point, which is what "
          f"keeps these registries append-only.")

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

    # The reviewed axis is printed on EVERY run, green included, for the same reason the row axis's
    # provenance is: "compared against what, and since when" is the whole difference between a
    # certified citation and a recorded present. A review is the weakest provenance here and hiding
    # it behind a green would be the exact claim the guard refuses to make elsewhere.
    if reviewed:
        unaudited = sorted(k for k, r in reviewed.items() if r.get("adjudication") == "unaudited")
        print(f"  (REVIEWED) {len(reviewed)} LINE citation(s) are compared against a dated --accept "
              f"review, NOT against each registry's first commit: a green run over them means no drift "
              f"SINCE the review, and nothing at all about the period before it.")
        for key in sorted(reviewed):
            rec = reviewed[key]
            print(f"    {key:<48} {rec['rowToken']:<36} {rec['adjudication']:<12} "
                  f"{rec['recordedOn']}  cited from {rec['citedFrom']}")
        if unaudited:
            print(f"    {len(unaudited)} of these had NO machine cross-check (their citing sentence "
                  f"names no row of the registry), so they rest on a human reading: "
                  f"{', '.join(unaudited)}")

    # The KEY axis's provenance is printed on EVERY run, green included, and so is the fact that no
    # blanket operation can write it. "Compared against what, and since when" is the whole difference
    # between a certified citation and a recorded present, and a key citation that could be blessed by
    # --update would be the line form wearing a new spelling.
    if reviewed_keys:
        print(f"  (REVIEWED KEYS) {len(reviewed_keys)} KEY citation(s) are compared against a dated "
              f"--accept review of ONE NAMED ROW. --update and --adopt-rows STRUCTURALLY CANNOT write "
              f"this axis, so no re-baselining operation can bless a key citation.")
        for key in sorted(reviewed_keys):
            rec = reviewed_keys[key]
            print(f"    {key:<52} {rec['container']}/{rec['kind']:<6} {rec['rowToken']:<28} "
                  f"{rec['recordedOn']}  cited from {rec['citedFrom']}")
    elif by_form[FORM_KEY]:
        print(f"  (REVIEWED KEYS) none recorded, while {by_form[FORM_KEY]} KEY citation(s) exist - "
              f"each one is reported as S8-KEY-NOT-REVIEWED above.")

    # A stale baseline key is a NOTE, not a finding: a citation removed on purpose is an edit, not a
    # defect. It is printed because it is the observable proof that the baseline is not being read as a
    # citing document. If this line could never print, the self-reference would be back.
    for key in gone:
        print(f"  (note) baseline entry {key} is no longer cited anywhere - it was removed on purpose; "
              f"--update drops it")
    for key in reviewed_keys_gone:
        print(f"  (note) reviewed KEY entry {key} is no longer cited anywhere - it was re-spelled or "
              f"removed on purpose")

    if not findings:
        print(f"REGISTRY APPEND SAFETY GUARD OK - {len(citations)} citation(s) "
              f"({by_form[FORM_LINE]} LINE, {by_form[FORM_KEY]} KEY), no cited line moved or changed "
              f"row, no cited row was renamed or edited, every key citation still resolves, and every "
              f"append container still closes below every line cited from it")
        return 0

    by_code: dict[str, int] = {}
    by_form_findings: dict[str, int] = {f: 0 for f in FORMS}
    for f in findings:
        by_code[f["code"]] = by_code.get(f["code"], 0) + 1
        key = f["note"].split(" ")[0]
        form = citation_form(key) if key in citations else "unknown"
        by_form_findings[form] = by_form_findings.get(form, 0) + 1
    print("\nREGISTRY APPEND SAFETY GUARD FAILED:")
    for code, n in sorted(by_code.items()):
        print(f"  {code:28} {n}")
    print(f"  {'-- BY CITATION FORM --':28} LINE {by_form_findings[FORM_LINE]}, "
          f"KEY {by_form_findings[FORM_KEY]}")
    print()
    for f in findings:
        print(f"  {f['code']:28} {f['registry']}")
        print(f"      {f['note']}")
    print("\nAn S1 or an S5 on a LINE citation is one insertion point: move the new row to the safe "
          "append point its registry printed above, re-point the affected citation deliberately, or "
          "re-spell it as a KEY citation. An S5 on a line whose text did NOT change is the case S1 is "
          "structurally blind to, and no re-baseline fixes it - the citing sentence has to be re-pointed "
          "at the line it means. S7 and S8 are KEY-form findings and have exactly one repair each: "
          "S7 means the key names no row or two, so name the row or the namespace; S8 means the row "
          "resolved but nobody has recorded what it should hold, so `--accept` that one key.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
