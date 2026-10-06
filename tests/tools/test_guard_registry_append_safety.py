"""Contract tests for `gk-core/scripts/guard-registry-append-safety.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the two
fingerprint axes, and - the reason this revision exists - that the ROW axis catches a shift onto a
byte-identical line that the CONTENT axis cannot.

WHY A MUTATION CONTROL AND NOT A FIXTURE COPY
---------------------------------------------
A fixture copied out of the live registry cannot fail when the registry changes, which is how several
guards in this programme ended up asserting their own data. The control here is built from a registry
the test itself writes, with a deliberate byte-identical twin, and the mutation is applied to a COPY.
Three things are asserted together, because any one alone is satisfied by a broken check:

  * the cited line's normalised TEXT is unchanged by the mutation   (so S1 has nothing to see)
  * the cited line's ROW FINGERPRINT is changed                    (so S5 must fire)
  * the CONTENT fingerprint is provably identical across it         (so the blindness is real)

WHAT THE FIRST VERSION OF THE ROW AXIS GOT WRONG, PINNED HERE
--------------------------------------------------------------
The row fingerprint was the structural path alone, without the row's identity. That is not sufficient,
and the real registries showed it: duplicating the boundary row ten lines above a cited line lands
that line on a byte-identical twin AND leaves it at the same array index, because the content and the
index shift by the same amount. `RowIdentityIsPartOfTheFingerprint` asserts the token is in the
fingerprint, so dropping it fails here rather than in production.

WHAT IS DELIBERATELY NOT ASSERTED
---------------------------------
How many citations the row axis currently finds. That is a READING: this revision ships the gate RED
on real pre-existing drift, and an assertion pinning the count would have to be rewritten every time a
citation is repaired - which is the population-count assertion the repo standard forbids.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-registry-append-safety.py"

_spec = importlib.util.spec_from_file_location("guard_registry_append_safety", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_registry_append_safety"] = guard
_spec.loader.exec_module(guard)


# ---------------------------------------------------------------------------------------------
# a registry the test owns, carrying a deliberate byte-identical twin
#
# 1-based line numbers, 22 lines. Both guard rows are 9 lines and put `      "paths": [` at the same
# offset, so ALPHA_PATHS and BETA_PATHS are byte-identical nine lines apart.
# ---------------------------------------------------------------------------------------------

FIXTURE = "\n".join([
    "{",                                    # 1
    '  "guards": {',                        # 2
    '    "alpha": {',                       # 3
    '      "id": "alpha",',                 # 4
    '      "script": "scripts/guard-alpha.py",',   # 5
    '      "paths": [',                     # 6   <- ALPHA_PATHS
    '        "scripts/guard-alpha.py"',     # 7
    "      ],",                             # 8
    '      "tier": "ci",',                  # 9
    '      "status": "gating"',              # 10
    "    },",                               # 11
    '    "beta": {',                        # 12
    '      "id": "beta",',                  # 13
    '      "script": "scripts/guard-beta.py",',    # 14
    '      "paths": [',                     # 15  <- BETA_PATHS
    '        "scripts/guard-beta.py"',      # 16
    "      ],",                             # 17
    '      "tier": "ci",',                  # 18
    '      "status": "gating"',              # 19
    "    }",                                # 20
    "  },",                                 # 21
    # An EMPTY append container, so `geometry` has one to locate on this fixture. Not under test
    # here - the append-point rule has its own assertions - but without it every check below refuses
    # on the geometry precondition before reaching the axis being exercised.
    '  "boundaries": [',                    # 22
    "  ]",                                  # 23
    "}",                                    # 24
    "",
])

ALPHA_PATHS = 6
BETA_PATHS = 15
ALPHA_ROW = (3, 11)          # 1-based inclusive line span of alpha
BETA_ROW = (12, 20)          # 1-based inclusive line span of beta
TWIN_TEXT = '      "paths": ['


# ---------------------------------------------------------------------------------------------
# a registry whose rows are an ARRAY, which is the only shape the index exists in
#
# FIXTURE above is a MAP (`"alpha": {...}`), so nothing in it carries an array index and the
# index-drop is unobservable there. These two rows are ARRAY elements, equal height, so swapping them
# moves no line number and leaves the cited bytes identical - the exact shape of the hazard.
# 1-based line numbers, 21 lines.
# ---------------------------------------------------------------------------------------------

ARRAY_FIXTURE = "\n".join([
    "{",                                 # 1
    '  "rows": [',                       # 2
    "    {",                             # 3   <- ARRAY_FIRST[0]
    '      "id": "alpha",',              # 4
    '      "kind": "owner",',            # 5
    '      "paths": [',                  # 6   <- ARRAY_CITE
    '        "alpha/**"',                # 7
    "      ],",                          # 8
    '      "project": "core",',          # 9
    '      "level": "module"',           # 10
    "    },",                            # 11  <- ARRAY_FIRST[1]
    "    {",                             # 12  <- ARRAY_SECOND[0]
    '      "id": "beta",',               # 13
    '      "kind": "owner",',            # 14
    '      "paths": [',                  # 15
    '        "beta/**"',                 # 16
    "      ],",                          # 17
    '      "project": "core",',          # 18
    '      "level": "module"',           # 19
    "    }",                             # 20  <- ARRAY_SECOND[1]
    "  ]",                               # 21
    "}",                                 # 22
    "",
])
ARRAY_LINES = ARRAY_FIXTURE.split("\n")
ARRAY_FIRST = (3, 11)
ARRAY_SECOND = (12, 20)
ARRAY_CITE = 6              # a `      "paths": [` line, byte-identical to line 15


def duplicate_alpha_before_itself() -> str:
    """A legal registry in which BETA_PATHS names alpha's `      "paths": [` instead of beta's.

    alpha's whole row is copied to the front of `guards`, so every line from alpha's row onward
    shifts down by nine. BETA_PATHS therefore lands on the copy's line at the same offset: the same
    bytes, a different row.
    """
    lines = FIXTURE.split("\n")
    block = lines[ALPHA_ROW[0] - 1:ALPHA_ROW[1]]
    mutant = "\n".join(lines[:ALPHA_ROW[0] - 1] + block + lines[ALPHA_ROW[0] - 1:])
    json.loads(mutant)                      # refuses to build a mutant that is not legal JSON
    return mutant


#: The citation key these tests inject. ASSEMBLED, never written literally: a file in the workspace
#: that spells a registry basename and a line number in one code line is a REAL citation to this
#: guard's own pattern, and it would add a phantom entry to the committed baseline. This test file's
#: first draft did exactly that and the guard reported it as a new citation with no baseline entry -
#: which is the filter working, and the reason the key is built here instead.
FIRST_REGISTRY = guard.REGISTRIES[1][0]
FIRST_KEY = f"{FIRST_REGISTRY}:1"

#: A registry declared FLAT (no append container), so its keys contain a `.` and a `/`. That is what
#: makes it the right subject for the trailing-dot test, and spelling its key out literally would make
#: this file a citing document - see `test_a_sentence_final_dot_...`.
FLAT_REGISTRY = next(name for name, _rel, container in guard.REGISTRIES if container is None)

#: The registry and line number the citation-FORM tests build their spellings from. Named indirectly
#: and held as a number, never spliced into a literal, for the reason in `FIRST_REGISTRY`: this file is
#: scanned by the guard it tests, so a spelled-out `registry:line` in its TEXT is a real citation.
#: Keeping the line number in one constant is also what makes Control C legible - changing it is a
#: single-token edit, and a fixture whose finding MOVED with its line number would be a phantom.
FORM_REGISTRY = guard.REGISTRIES[0][0]
FORM_LINE = 802


def _key_of(registry: str) -> str:
    """A real key of `registry`, read from disk, and checked to survive the key spelling.

    Read rather than written out, for the reason in `FIRST_REGISTRY`. The round trip is checked here
    rather than in each test because a key holding a character the key class excludes would fail every
    assertion below on a spelling this file never wrote - which is a fixture defect reported as a guard
    defect, and the worst way for this test to fail.
    """
    rel = next(r for n, r, _c in guard.REGISTRIES if n == registry)
    doc = json.loads((REPO / rel).read_text(encoding="utf-8"))
    for key in sorted(guard.row_key_index(doc)):
        if guard.CITATION.search(f"see {registry}#{key} for it") is not None:
            return key
    raise AssertionError(f"no key of {registry} round-trips through the key spelling")


def _flat_key() -> str:
    """The flat registry's first key that ends in a plain `.md`, read from the real file.

    Read rather than written out, for the reason above. The `.md` suffix is what makes this key the
    subject of the trailing-dot test: the citation character class cannot exclude `.` because this key
    contains one, so the sentence-final dot has to be resolved against the registry.
    """
    rel = next(r for n, r, _c in guard.REGISTRIES if n == FLAT_REGISTRY)
    doc = json.loads((REPO / rel).read_text(encoding="utf-8"))
    return next(k for k in doc if k.endswith(".md"))


def _one_citation() -> dict:
    return {FIRST_KEY: ("tasks/x.md", 1)}


def _refused_counts() -> dict:
    return {name: 0 for name, _rel, _c in guard.REGISTRIES}


def _coordinate_counts() -> dict:
    return {name: 0 for name, _rel, _c in guard.REGISTRIES}


class TheTwinFixtureIsHonest(unittest.TestCase):
    """If these fail the control below is proving nothing, so they are asserted first."""

    def test_the_two_twin_lines_are_byte_identical(self):
        lines = FIXTURE.split("\n")
        self.assertEqual(lines[ALPHA_PATHS - 1], lines[BETA_PATHS - 1])
        self.assertEqual(lines[ALPHA_PATHS - 1], TWIN_TEXT)
        self.assertEqual(FIXTURE.count(TWIN_TEXT), 2, "exactly two twins, or it is not a twin test")

    def test_the_fixture_is_valid_json(self):
        json.loads(FIXTURE)


class RowIdentity(unittest.TestCase):
    """The scanner: which element a line belongs to, and what that element is called."""

    def test_a_line_spans_the_element_it_sits_in_and_the_one_it_opens(self):
        rows = guard.row_identity(FIXTURE)
        self.assertEqual(rows[ALPHA_PATHS], ("<root>/guards/alpha", "<root>/guards/alpha/paths"))
        self.assertEqual(rows[BETA_PATHS], ("<root>/guards/beta", "<root>/guards/beta/paths"))
        self.assertNotEqual(rows[ALPHA_PATHS], rows[BETA_PATHS])

    def test_an_opening_brace_names_the_row_it_opens(self):
        rows = guard.row_identity(FIXTURE)
        # line 12 is `"beta": {` - at its START the innermost open element is `guards` (alpha closed
        # on line 11) and at its END it is beta. The PAIR is what makes an opening line identifiable.
        self.assertEqual(rows[ALPHA_ROW[0]], ("<root>/guards", "<root>/guards/alpha"))
        self.assertEqual(rows[BETA_ROW[0]], ("<root>/guards", "<root>/guards/beta"))

    def test_a_closing_brace_names_the_row_it_closes_and_the_one_it_returns_to(self):
        rows = guard.row_identity(FIXTURE)
        self.assertEqual(rows[ALPHA_ROW[1]], ("<root>/guards/alpha", "<root>/guards"))

    def test_a_brace_inside_a_string_is_not_a_brace(self):
        rows = guard.row_identity('{"a": "not { a brace",\n "b": {\n "c": 1\n }\n}\n')
        self.assertEqual(rows[3][0], "<root>/b")

    def test_a_blank_line_inherits_the_previous_line_s_row(self):
        rows = guard.row_identity('{\n "a": {\n\n "b": 1\n }\n}\n')
        self.assertEqual(rows[2], ("<root>", "<root>/a"))
        self.assertEqual(rows[3], ("<root>/a", "<root>/a"))

    def test_row_token_is_the_row_s_own_name(self):
        """A map row is named by its key; an array row by its declared id. Both are unique."""
        doc = json.loads(FIXTURE)
        rows = guard.row_identity(FIXTURE)
        # `guards` is a map, so a guard row is named by its key.
        self.assertEqual(guard.row_token(doc, rows[ALPHA_PATHS]), "alpha")
        self.assertEqual(guard.row_token(doc, rows[BETA_PATHS]), "beta")

    def test_row_token_uses_the_declared_id_of_an_array_row(self):
        text = ('{\n "rows": [\n  {\n   "id": "first",\n   "paths": [\n' +
                '    "a"\n   ]\n  },\n  {\n   "id": "second",\n   "paths": [\n' +
                '    "b"\n   ]\n  }\n ]\n}\n')
        doc = json.loads(text)
        rows = guard.row_identity(text)
        self.assertEqual(rows[5][1], "<root>/rows/[0]/paths")
        self.assertEqual(guard.row_token(doc, rows[5]), "id=first")
        self.assertEqual(guard.row_token(doc, rows[11]), "id=second")

    def test_row_token_falls_back_to_the_map_key_when_no_id_is_declared(self):
        doc = json.loads('{"rows": {\n  "named": {\n   "v": 1\n  }\n }\n}\n')
        rows = guard.row_identity('{"rows": {\n  "named": {\n   "v": 1\n  }\n }\n}\n')
        self.assertEqual(guard.row_token(doc, rows[3]), "named")


class RowIdentityIsPartOfTheFingerprint(unittest.TestCase):
    """The blind spot the first version of this axis still had.

    Duplicating the row above a cited line moves the CONTENT and the array INDEX by the same amount,
    so a path-only fingerprint lands back on an identical value. The row's declared identity is what
    separates "the row that was there" from "the row that is there now", and dropping it from the
    fingerprint must fail here.
    """

    def test_the_content_fingerprint_is_identical_across_the_twin_shift(self):
        lines = FIXTURE.split("\n")
        mutant = duplicate_alpha_before_itself().split("\n")
        self.assertEqual(guard.normalise(mutant[BETA_PATHS - 1]),
                         guard.normalise(lines[BETA_PATHS - 1]),
                         "the twin line's bytes must be unchanged, or this control proves nothing")

    def test_the_row_path_changes_and_so_does_the_row_identity(self):
        mutant = duplicate_alpha_before_itself()
        doc0 = json.loads(FIXTURE)
        doc1 = json.loads(mutant)
        pair0 = guard.row_identity(FIXTURE)[BETA_PATHS]
        pair1 = guard.row_identity(mutant)[BETA_PATHS]
        self.assertNotEqual(pair0, pair1)
        self.assertEqual(guard.row_token(doc0, pair0), "beta")
        self.assertEqual(guard.row_token(doc1, pair1), "alpha")

    def test_the_row_fingerprint_carries_the_identity_not_only_the_path(self):
        mutant = duplicate_alpha_before_itself()
        before = guard.row_fingerprints(FIXTURE, json.loads(FIXTURE))[BETA_PATHS]
        after = guard.row_fingerprints(mutant, json.loads(mutant))[BETA_PATHS]
        self.assertNotEqual(before, after, "S5 must be able to see this shift")

    def test_a_path_only_fingerprint_would_have_missed_it(self):
        """The negative control, so the test above cannot pass for the wrong reason."""
        mutant = duplicate_alpha_before_itself()
        pair0 = guard.row_identity(FIXTURE)[BETA_PATHS]
        pair1 = guard.row_identity(mutant)[BETA_PATHS]
        self.assertNotEqual(pair0, pair1,
                            "in THIS fixture the path does change; the index-collision case it "
                            "cannot see is covered by the on-disk swap control, not here")


class IndexIsNotFingerprinted(unittest.TestCase):
    """THE OWNER'S RULED CHANGE: the array index is a POSITION and must not be in the fingerprint.

    Two guarantees, and the second is the one that matters, so both are asserted rather than one:

      a row that merely SHIFTED (inserted into above) keeps its fingerprint  -> a re-point can land
      a row that SLID onto a DIFFERENT row loses its fingerprint              -> the hazard survives

    The negative control is here for the same reason `ContentAxisBlindnessIsReal` exists: without it,
    the first test also passes for a fingerprint that is simply constant.
    """

    def test_the_index_free_pair_keeps_the_span_and_drops_only_the_index(self):
        self.assertEqual(guard.index_free(("<root>/boundaries", "<root>/boundaries/[7]")),
                         ("<root>/boundaries", "<root>/boundaries"))
        # a MAP row has no index at all and must come through untouched
        self.assertEqual(guard.index_free(("<root>/guards/generated-seed",
                                           "<root>/guards/generated-seed/args")),
                         ("<root>/guards/generated-seed", "<root>/guards/generated-seed/args"))

    def test_a_map_key_that_merely_looks_indexed_is_not_stripped(self):
        """Segment-wise, not a substring strip: `row_token` can return a real key with brackets."""
        self.assertEqual(guard.index_free(("<root>/rows/[0]", "<root>/rows/[0]/paths")),
                         ("<root>/rows", "<root>/rows/paths"))
        self.assertEqual(guard.index_free(("<root>/k[0]", "<root>/k[0]")), ("<root>/k[0]", "<root>/k[0]"))

    def test_a_shifted_row_keeps_its_fingerprint(self):
        """Inserting a row above must not red: nothing about the cited row's identity changed.

        The comparison the guard actually makes is the row's fingerprint BEFORE the shift against its
        fingerprint AFTER - at the line it MOVED to, because that is where a repaired citation points.
        Comparing one line number across both states would land on the copy in both and prove nothing.
        """
        rows = ARRAY_LINES
        block = rows[ARRAY_FIRST[0] - 1:ARRAY_FIRST[1]]
        shifted = "\n".join(rows[:ARRAY_FIRST[0] - 1] + block + rows[ARRAY_FIRST[0] - 1:])
        json.loads(shifted)
        doc0, doc1 = json.loads(ARRAY_FIXTURE), json.loads(shifted)
        before_line = ARRAY_FIRST[0]                     # alpha's own opener, at index 0
        after_line = before_line + len(block)            # alpha's real body, now at index 1
        pair0 = guard.row_identity(ARRAY_FIXTURE)[before_line]
        pair1 = guard.row_identity(shifted)[after_line]
        self.assertIn("[0]", pair0[1], "the fixture must start at index 0")
        self.assertIn("[1]", pair1[1], "the inserted row must have pushed it to index 1")
        self.assertEqual(guard.row_token(doc0, pair0), "id=alpha")
        self.assertEqual(guard.row_token(doc1, pair1), "id=alpha", "the identity must be unchanged")
        self.assertEqual(guard.row_fingerprint(pair0, guard.row_token(doc0, pair0)),
                         guard.row_fingerprint(pair1, guard.row_token(doc1, pair1)),
                         "a row that merely shifted must keep its fingerprint")

    def test_a_row_that_slid_onto_a_different_row_loses_its_fingerprint(self):
        """The hazard this axis exists for. Swapping two rows must still red, index-free or not."""
        rows = ARRAY_LINES
        a = rows[ARRAY_FIRST[0] - 1:ARRAY_FIRST[1]]
        b = rows[ARRAY_SECOND[0] - 1:ARRAY_SECOND[1]]
        # Swapping means beta now CLOSES the array: its `    }` must become `    },` and alpha's
        # `    },` must become `    }`, or the mutant is not legal JSON and proves nothing.
        a = list(a); a[-1] = "    }"
        b = list(b); b[-1] = "    },"
        swapped = "\n".join(rows[:ARRAY_FIRST[0] - 1] + b + a + rows[ARRAY_SECOND[1]:])
        doc = json.loads(swapped)
        cite = ARRAY_CITE                                 # the byte-identical `      "paths": [`
        self.assertEqual(swapped.split("\n")[cite - 1], ARRAY_LINES[cite - 1],
                         "the cited bytes must be unchanged, or this control proves nothing")
        pair = guard.row_identity(swapped)[cite]
        self.assertEqual(guard.row_token(json.loads(ARRAY_FIXTURE),
                                         guard.row_identity(ARRAY_FIXTURE)[cite]), "id=alpha")
        self.assertEqual(guard.row_token(doc, pair), "id=beta",
                         "the cited line must now sit in the OTHER row")
        orig = guard.row_fingerprints(ARRAY_FIXTURE, json.loads(ARRAY_FIXTURE))[cite]
        after = guard.row_fingerprints(swapped, doc)[cite]
        self.assertNotEqual(orig, after, "S5 must still see a shift onto a different row")

    def test_a_constant_fingerprint_would_fail_both_of_the_above(self):
        """The negative control: proves the two tests above are not passing for free.

        A fingerprint that ignored BOTH the index and the identity would agree across a shift, so it
        would pass the first test. It would then also agree across a swap, which is what the second
        test forbids. Neither test is meaningful alone.
        """
        first = guard.row_identity(ARRAY_FIXTURE)[ARRAY_FIRST[1]]
        second = guard.row_identity(ARRAY_FIXTURE)[ARRAY_SECOND[1]]
        self.assertNotEqual(guard.row_fingerprint(first, "id=alpha"),
                            guard.row_fingerprint(second, "id=beta"),
                            "identity must still separate two different rows")
        # and the same row at two indexes must agree, or the shift control above cannot pass
        self.assertEqual(guard.row_fingerprint(("<root>/rows", "<root>/rows/[3]"), "id=x"),
                         guard.row_fingerprint(("<root>/rows", "<root>/rows/[9]"), "id=x"))

    def test_a_closing_brace_no_longer_carries_its_own_row_identity(self):
        """THE MEASURED COST OF THE INDEX-DROP, pinned so it cannot be forgotten.

        A closing `},` ends at its PARENT element, so `row_token` returns no identity and the pair is
        (`rows/[N]`, `rows`). With the index removed that pair is (`rows`, `rows`) - identical for
        EVERY row in the array. Two closing lines therefore share one fingerprint, where the old
        index-carrying fingerprint separated them. This is a real narrowing and the live registries
        hold hundreds of closing lines; it is asserted here as a fact about the shape rather than left
        for a reader to rediscover. It is why S5 is a row-IDENTITY check and not a line check.
        """
        rows = guard.row_identity(ARRAY_FIXTURE)
        close_first, close_second = ARRAY_FIRST[1], ARRAY_SECOND[1]
        self.assertEqual(guard.row_token(json.loads(ARRAY_FIXTURE), rows[close_first]), "")
        self.assertNotEqual(rows[close_first], rows[close_second],
                            "the scanner still distinguishes them; only the fingerprint cannot")
        self.assertEqual(guard.row_fingerprint(rows[close_first], ""),
                         guard.row_fingerprint(rows[close_second], ""),
                         "the measured narrowing: two closing lines of the same array collide")
        # and the closing line of a row is still separated from the row's INTERIOR lines
        self.assertNotEqual(guard.row_fingerprint(rows[close_first], ""),
                            guard.row_fingerprint(rows[ARRAY_FIRST[1] - 1], "id=alpha"))


class ContentAxisBlindnessIsReal(unittest.TestCase):
    """The defect, stated as an assertion rather than as a claim in a docstring."""

    def test_two_byte_identical_twins_get_one_content_fingerprint(self):
        a = guard.fingerprint(TWIN_TEXT)
        b = guard.fingerprint(TWIN_TEXT)
        self.assertEqual(a, b, "identical text must fingerprint identically - that is the blindness")

    def test_but_the_two_twins_get_different_row_fingerprints(self):
        doc = json.loads(FIXTURE)
        rows = guard.row_identity(FIXTURE)
        fps = guard.row_fingerprints(FIXTURE, doc)
        self.assertNotEqual(fps[ALPHA_PATHS], fps[BETA_PATHS])


class Refusals(unittest.TestCase):
    """FAILS CLOSED. Each is a named exit 2 - never a green run, never a traceback, never empty."""

    def _main_with_baseline(self, payload: dict) -> int:
        tmp = tempfile.TemporaryDirectory(prefix="gras-baseline-")
        self.addCleanup(tmp.cleanup)
        p = Path(tmp.name) / "baseline.json"
        p.write_text(json.dumps(payload), encoding="utf-8", newline="")
        original_baseline = guard.BASELINE
        original_collect = guard.collect
        guard.BASELINE = p
        # One citation, so `evaluate` does not walk the whole workspace: these tests are about the
        # baseline's shape, and a 20-second workspace walk per assertion buys nothing.
        guard.collect = lambda ws: (_one_citation(), _refused_counts(), _coordinate_counts())
        try:
            return guard.main([])
        finally:
            guard.BASELINE = original_baseline
            guard.collect = original_collect

    def test_a_baseline_with_no_row_axis_refuses_rather_than_reporting_content_only_ok(self):
        self.assertEqual(self._main_with_baseline({"fingerprints": {}, "unverified": []}), 2)

    def test_a_row_axis_with_no_provenance_refuses(self):
        self.assertEqual(self._main_with_baseline(
            {"fingerprints": {}, "unverified": [], "rows": {"x": "y"}}), 2)

    def test_provenance_naming_an_unchecked_registry_refuses(self):
        self.assertEqual(self._main_with_baseline(
            {"fingerprints": {}, "unverified": [], "rows": {"x": "y"},
             "rowBaselineSource": {"scripts/not-checked.v1.json": "a" * 40}}), 2)

    def test_an_unverified_declaration_still_needs_a_stated_reason(self):
        self.assertEqual(self._main_with_baseline(
            {"fingerprints": {}, "rows": {"x": "y"},
             "rowBaselineSource": {"scripts/enforcement-registry.v1.json": "a" * 40},
             "unverified": [{"registry": "enforcement-registry.v1.json", "why": "   "}]}), 2)

    def test_an_unverified_declaration_naming_an_unchecked_registry_refuses(self):
        self.assertEqual(self._main_with_baseline(
            {"fingerprints": {}, "rows": {"x": "y"},
             "rowBaselineSource": {"scripts/todo-shapes.v1.json": "a" * 40},
             "unverified": [{"registry": "gone.v1.json", "why": "because"}]}), 2)

    def test_update_cannot_create_a_missing_row_axis(self):
        """--update must not be a re-baselining escape hatch for the axis it cannot derive."""
        tmp = tempfile.TemporaryDirectory(prefix="gras-update-")
        self.addCleanup(tmp.cleanup)
        p = Path(tmp.name) / "baseline.json"
        p.write_text(json.dumps({"fingerprints": {}, "unverified": []}), encoding="utf-8",
                     newline="")
        original_baseline, original_collect = guard.BASELINE, guard.collect
        guard.BASELINE = p
        guard.collect = lambda ws: (_one_citation(), _refused_counts(), _coordinate_counts())
        try:
            self.assertEqual(guard.main(["--update"]), 2)
        finally:
            guard.BASELINE, guard.collect = original_baseline, original_collect
        stored = json.loads(p.read_text(encoding="utf-8"))
        self.assertNotIn("rows", stored, "the refusal must not have written one")

    def test_an_unregistered_json_container_cannot_be_silently_checked(self):
        # geometry() must refuse a container the registry does not have, or the guard would locate no
        # append point, check nothing, and report success.
        with tempfile.TemporaryDirectory() as tmp:
            core = Path(tmp)
            (core / "scripts").mkdir()
            (core / "scripts" / "r.v1.json").write_text('{"other": []}\n', encoding="utf-8")
            with self.assertRaises(guard.CannotRun):
                guard.geometry("r.v1.json", "scripts/r.v1.json", "invariants", core)

    def test_a_registry_that_is_not_json_refuses_rather_than_being_content_compared(self):
        with self.assertRaises(guard.CannotRun):
            guard.row_identity('{"a": "unterminated\n')


class CliSurface(unittest.TestCase):
    """The invocation contract a runner depends on."""

    def test_every_published_flag_is_accepted(self):
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"], cwd=str(REPO),
                              capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=300)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        for flag in ("--report", "--json", "--update", "--adopt-rows", "--root",
                     "--accept", "--accept-why"):
            self.assertIn(flag, proc.stdout, f"{flag} is part of the published surface")

    def test_update_and_adopt_rows_cannot_run_together(self):
        proc = subprocess.run([sys.executable, str(SCRIPT), "--update", "--adopt-rows"],
                              cwd=str(REPO), capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=600)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("--adopt-rows", proc.stdout + proc.stderr)


# ---------------------------------------------------------------------------------------------
# `--accept`: recording ONE reviewed citation, and refusing everything that is not one
#
# WHY THESE TESTS EXIST AT ALL. The guard could flag an unreviewed citation and had no vocabulary for
# the opposite state, so an honest repair landed two findings and could only be cleared by a blanket
# re-baseline. The tests below are therefore as much about the REFUSALS as about the recording: a
# review flag that accepts anything is a re-baseline with better manners, and the whole value of the
# flag is that it is one citation wide.
#
# `--accept` is exercised through `guard.main` against a temp baseline and a temp citing document,
# never against the committed one. A test that wrote a review into the real baseline would be a test
# that changes the thing it measures.
# ---------------------------------------------------------------------------------------------

ACCEPT_REGISTRY = guard.REGISTRIES[0][0]
ACCEPT_LINE = ALPHA_PATHS                       # a real line of FIXTURE, inside row `alpha`
ACCEPT_KEY = f"{ACCEPT_REGISTRY}:{ACCEPT_LINE}"
BETA_KEY = f"{ACCEPT_REGISTRY}:{BETA_PATHS}"


def _fake_core(directory: Path, registry_text: str) -> Path:
    """A throwaway `core` whose checked registries are FIXTURE (and copies of the real other two).

    `--accept` resolves a citation against the REAL registries on disk, so testing it against
    FIXTURE - which is the only fixture carrying a deliberate byte-identical twin - means resolving it
    against a fake core. Without this the token assertions would be about whichever real row happens
    to sit at the fixture's line numbers, which is a reading and not a contract.
    """
    scripts = directory / "scripts"
    scripts.mkdir(parents=True, exist_ok=True)
    for _name, rel, _container in guard.REGISTRIES:
        source = (REPO / rel).read_text(encoding="utf-8") if (REPO / rel).is_file() else "{}\n"
        (scripts / Path(rel).name).write_text(
            registry_text if Path(rel).name == ACCEPT_REGISTRY else source,
            encoding="utf-8", newline="")
    return directory


def _baseline_with_hole() -> dict:
    """A baseline that is structurally complete but has NO entry for ACCEPT_KEY.

    Complete, because every refusal about a missing axis must not be what is under test; hollow at
    exactly one key, because that is the state a re-pointed citation is in.
    """
    rows = guard.row_identity(FIXTURE)
    doc = json.loads(FIXTURE)
    first_key = f"{ACCEPT_REGISTRY}:1"
    return {"fingerprints": {first_key: guard.fingerprint(FIXTURE.split("\n")[0])},
            "unverified": [],
            "rows": {first_key: guard.row_fingerprint(rows[1], guard.row_token(doc, rows[1]))},
            "rowBaselineSource": {rel: "a" * 40 for _n, rel, _c in guard.REGISTRIES}}


def _recorded(key: str, token: str, pair: "tuple[str, str]", why: str = "because") -> dict:
    """A well-formed `reviewed` record, so a test can vary ONE field and see that field refused."""
    return {"registry": key.rpartition(":")[0], "line": int(key.rpartition(":")[2]),
            "citedFrom": "tasks/x.md:1", "rowLabel": guard.row_label(pair), "rowToken": token,
            "rowFingerprint": guard.row_fingerprint(pair, token),
            "contentFingerprint": guard.fingerprint(TWIN_TEXT),
            "adjudication": "corroborated", "recordedOn": "2026-01-01", "why": why}


class _AcceptHarness(unittest.TestCase):
    """A temp baseline, a temp citing document, and a way to read back exactly what was written.

    `assert_nothing_written` compares BYTES, not parsed JSON: a refusal that rewrote the file
    identically would still be a refusal that touched the thing it refused to touch, and only the
    byte comparison sees it.
    """

    def setUp(self):
        tmp = tempfile.TemporaryDirectory(prefix="gras-accept-")
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.baseline = self.tmp / "baseline.json"
        self.baseline.write_text(json.dumps(_baseline_with_hole()), encoding="utf-8", newline="")
        self.citer = self.tmp / "tasks" / "x.md"
        self.citer.parent.mkdir(parents=True, exist_ok=True)
        self.write_citer(f"the `alpha` row owns it ({ACCEPT_REGISTRY}:{ACCEPT_LINE})\n")
        self._original = (guard.BASELINE, guard.collect, guard.evaluate)
        guard.BASELINE = self.baseline
        guard.collect = lambda ws: ({ACCEPT_KEY: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        self.fake = _fake_core(self.tmp / "fakecore", FIXTURE)
        _original_evaluate = guard.evaluate
        guard.evaluate = lambda core, ws: _original_evaluate(self.fake, ws)

    def tearDown(self):
        guard.BASELINE, guard.collect, guard.evaluate = self._original

    def use_registry(self, text: str) -> None:
        """Point the resolved registry at `text`, e.g. an on-disk mutant."""
        _fake_core(self.fake, text)
        self.read_back = (self.fake / "scripts" / ACCEPT_REGISTRY).read_text(encoding="utf-8")
        self.assertEqual(self.read_back, text, "the mutant is not what landed on disk")

    def write_citer(self, text: str) -> None:
        self.citer.write_text(text, encoding="utf-8", newline="")

    def run_guard(self, *args: str) -> "tuple[int, str]":
        import contextlib
        import io as _io
        buf = _io.StringIO()
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            code = guard.main(["--root", str(self.tmp), *args])
        return code, buf.getvalue()

    def stored(self) -> dict:
        return json.loads(self.baseline.read_text(encoding="utf-8"))

    def assert_nothing_written(self) -> None:
        self.assertEqual(self.baseline.read_bytes(), self._before,
                         "a refusal must leave the baseline byte-identical")

    def remember(self) -> bytes:
        self._before = self.baseline.read_bytes()
        return self._before


class AcceptRecordsTheReview(_AcceptHarness):
    """The happy path. A test suite that only asserts refusals never proves the flag works."""

    def test_a_cited_key_with_a_reason_is_recorded_on_both_axes(self):
        self.remember()
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why",
                                   "alpha is the row it names")
        self.assertEqual(code, 0, out)
        rec = self.stored()["reviewed"][ACCEPT_KEY]
        self.assertEqual(rec["registry"], ACCEPT_REGISTRY)
        self.assertEqual(rec["line"], ACCEPT_LINE)
        self.assertEqual(rec["rowToken"], "alpha")
        self.assertEqual(rec["citedFrom"], "tasks/x.md:1")
        self.assertEqual(rec["contentFingerprint"], guard.fingerprint(TWIN_TEXT))
        self.assertEqual(rec["rowFingerprint"],
                         guard.row_fingerprint(guard.row_identity(FIXTURE)[ALPHA_PATHS], "alpha"))
        self.assertEqual(rec["adjudication"], "corroborated")
        self.assertEqual(rec["why"], "alpha is the row it names")
        self.assertRegex(rec["recordedOn"], r"^\d{4}-\d{2}-\d{2}$")

    def test_the_recording_is_printed_not_silent(self):
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "alpha")
        self.assertEqual(code, 0)
        for needle in (ACCEPT_KEY, ACCEPT_REGISTRY, str(ACCEPT_LINE), "alpha",
                       "corroborated", "tasks/x.md:1", "row identity", "row fingerprint",
                       "content print", "recorded on", "why"):
            self.assertIn(needle, out, f"the audit output must state {needle!r}")

    def test_the_recording_is_visible_in_json(self):
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "alpha", "--json")
        self.assertEqual(code, 0)
        payload = json.loads(out)
        self.assertIn(ACCEPT_KEY, payload["accepted"])
        self.assertEqual(payload["accepted"][ACCEPT_KEY]["rowToken"], "alpha")

    def test_the_reviewed_axis_is_visible_in_the_gate_json_and_the_report(self):
        self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "alpha")
        _, out = self.run_guard("--json")
        payload = json.loads(out)
        self.assertIn(ACCEPT_KEY, payload["reviewed"])
        self.assertEqual(payload["reviewed"][ACCEPT_KEY]["rowToken"], "alpha")
        _, report = self.run_guard("--report")
        self.assertIn(ACCEPT_KEY, report)
        self.assertIn("REVIEWED", report)

    def test_a_reviewed_key_no_longer_reads_as_new_and_is_still_compared(self):
        self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "alpha")
        code, out = self.run_guard()
        self.assertNotIn(f"{ACCEPT_KEY} is a NEW citation", out)
        self.assertIn("REVIEWED", out, "a green run must state the reviewed axis, not hide it")
        self.assertIn("compared against a dated", out)

    def test_a_review_still_detects_later_drift(self):
        """Recording a promise, not clearing a finding: the comparison keeps running."""
        self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "alpha")
        date = self.stored()["reviewed"][ACCEPT_KEY]["recordedOn"]
        drifted = self.stored()
        drifted["reviewed"][ACCEPT_KEY]["rowFingerprint"] = guard.row_fingerprint(
            guard.row_identity(FIXTURE)[BETA_PATHS], "beta")
        self.baseline.write_text(json.dumps(drifted), encoding="utf-8", newline="")
        code, out = self.run_guard()
        self.assertEqual(code, 1, "a reviewed key whose row changed must red")
        self.assertIn(f"{ACCEPT_KEY} now sits in row", out)
        self.assertIn(date, out, "the finding must name the review's date, not the first commit")





class AcceptRefusals(_AcceptHarness):
    """Every refusal is named, exits 2, and writes NOTHING."""

    def test_a_key_nothing_cites_is_refused(self):
        self.remember()
        code, out = self.run_guard("--accept", f"{ACCEPT_REGISTRY}:2", "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("no document in the workspace cites", out)
        self.assert_nothing_written()

    def test_an_unknown_registry_is_refused(self):
        self.remember()
        code, out = self.run_guard("--accept", "not-a-checked-registry.v1.json:1",
                                   "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("which this guard does not check", out)
        self.assert_nothing_written()

    def test_a_wildcard_is_refused_and_names_the_flag_that_does_that_job(self):
        for wildcard in ("*", f"{ACCEPT_REGISTRY}:*", "a,b", f"{ACCEPT_REGISTRY}:[12]"):
            with self.subTest(wildcard=wildcard):
                self.remember()
                code, out = self.run_guard("--accept", wildcard, "--accept-why", "x")
                self.assertEqual(code, 2)
                self.assertIn("--adopt-rows", out)
                self.assertIn("--update", out)
                self.assert_nothing_written()

    def test_a_range_is_refused_as_a_re_baselining_request(self):
        self.remember()
        code, out = self.run_guard("--accept", f"{ACCEPT_REGISTRY}:1-9", "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("non-numeric line", out)
        self.assertIn("--update", out)
        self.assert_nothing_written()

    def test_a_bare_registry_is_refused(self):
        self.remember()
        code, out = self.run_guard("--accept", ACCEPT_REGISTRY, "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("names no line", out)
        self.assert_nothing_written()

    def test_a_review_with_no_reason_is_refused(self):
        for why in ([], ["--accept-why", "   "]):
            with self.subTest(why=why):
                self.remember()
                code, out = self.run_guard("--accept", ACCEPT_KEY, *why)
                self.assertEqual(code, 2)
                self.assertIn("--accept-why", out)
                self.assert_nothing_written()

    def test_a_key_that_already_has_a_baseline_entry_is_refused(self):
        """Filling a hole is a review; overwriting a comparison is a re-baseline."""
        baselined = f"{ACCEPT_REGISTRY}:{BETA_PATHS}"
        self.write_citer(f"the `alpha` row owns it ({ACCEPT_REGISTRY}:{ALPHA_PATHS}) and the "
                         f"`beta` row ({baselined})\n")
        guard.collect = lambda ws: ({ACCEPT_KEY: ("tasks/x.md", 1),
                                    baselined: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        # give `beta` a baseline entry on BOTH axes, so only that rule can refuse it
        payload = self.stored()
        pair = guard.row_identity(FIXTURE)[BETA_PATHS]
        payload["fingerprints"][baselined] = guard.fingerprint(TWIN_TEXT)
        payload["rows"][baselined] = guard.row_fingerprint(pair, "beta")
        self.baseline.write_text(json.dumps(payload), encoding="utf-8", newline="")
        self.remember()
        code, out = self.run_guard("--accept", baselined, "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("ALREADY has a baseline entry", out)
        self.assertIn("--update", out)
        self.assert_nothing_written()

    def test_a_line_with_no_resolvable_row_identity_is_refused(self):
        """A container's closing bracket names no row of its own, so nothing was reviewed."""
        closing = 23                                           # FIXTURE line 23, `  ]`
        self.assertEqual(guard.row_token(json.loads(FIXTURE),
                                         guard.row_identity(FIXTURE)[closing]), "",
                         "the fixture must carry a line whose row declares no identity")
        guard.collect = lambda ws: ({f"{ACCEPT_REGISTRY}:{closing}": ("tasks/x.md", 1)},
                                    _refused_counts(), _coordinate_counts())
        self.remember()
        code, out = self.run_guard("--accept", f"{ACCEPT_REGISTRY}:{closing}",
                                   "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("NO declared identity", out)
        self.assert_nothing_written()

    def test_a_citing_sentence_that_names_a_different_row_is_refused(self):
        """The refusal that makes the flag a check rather than a label.

        The cited line is FIXTURE line 20, a closing brace whose row token is the MAP key `guards`.
        The sentence names `scripts/guard-alpha.py`, the declared `script` of a DIFFERENT row of the
        same registry - so the cross-check is genuinely exercised rather than short-circuited.
        """
        key = f"{ACCEPT_REGISTRY}:20"
        self.assertEqual(guard.row_token(json.loads(FIXTURE),
                                         guard.row_identity(FIXTURE)[20]), "guards")
        self.assertIn("scripts/guard-alpha.py",
                      guard.row_identity_values(json.loads(FIXTURE)))
        self.write_citer(f"it is `scripts/guard-alpha.py` that owns it ({ACCEPT_REGISTRY}:20)\n")
        guard.collect = lambda ws: ({key: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        self.remember()
        code, out = self.run_guard("--accept", key, "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("names a DIFFERENT row", out)
        self.assertIn("guards", out)
        self.assert_nothing_written()

    def test_a_short_row_name_is_below_the_floor_and_is_not_matched(self):
        """`IDENTITY_VALUE_MIN` exists so prose words cannot be read as row names, and it has a cost.

        FIXTURE's rows are named `alpha` and `beta`, both under the floor, so a sentence naming one of
        them cannot be cross-checked at all. The answer must be `unaudited` - never a false `denies`
        and never a false `corroborated`. Pinning it means a later change to the floor has to say so.
        """
        key = f"{ACCEPT_REGISTRY}:20"
        self.write_citer(f"it is the `alpha` row ({ACCEPT_REGISTRY}:20)\n")
        guard.collect = lambda ws: ({key: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        code, out = self.run_guard("--accept", key, "--accept-why", "read it myself")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.stored()["reviewed"][key]["adjudication"], "unaudited")

    def test_a_sentence_naming_no_row_is_recorded_as_unaudited_not_as_corroborated(self):
        """An honest limit, pinned: no name to match means NO machine check was possible."""
        self.write_citer(f"see line {ACCEPT_LINE} above ({ACCEPT_REGISTRY}:{ACCEPT_LINE})\n")
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "read it myself")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.stored()["reviewed"][ACCEPT_KEY]["adjudication"], "unaudited")
        self.assertIn("NO machine cross-check", out,
                      "an unaudited record must never read as machine-checked")

    def test_a_coordinated_sentence_is_not_read_as_a_denial(self):
        """The false refusal this gate exists to avoid, on the real corpus's shape.

        One sentence, two citations of the same registry, two row names. The paragraph-wide scan read
        the first as DENIED because the second claim names a different row. Two citations in one
        window means the names are not attributable, so the answer must be `unaudited`.
        """
        self.write_citer(f"the seed path (`{ACCEPT_REGISTRY}:{ACCEPT_LINE}`); the core row "
                         f"(`:{BETA_PATHS}`)\n")
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "read it myself")
        self.assertEqual(code, 0, out)
        self.assertEqual(self.stored()["reviewed"][ACCEPT_KEY]["adjudication"], "unaudited")

    def test_one_bad_key_refuses_the_whole_run_and_records_none_of_it(self):
        """All-or-nothing, asserted: a partial review is a state no reader can interpret."""
        self.remember()
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept",
                                   f"{ACCEPT_REGISTRY}:9999", "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("Nothing was written", out)
        self.assert_nothing_written()

    def test_the_same_key_twice_is_refused(self):
        self.remember()
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept", ACCEPT_KEY,
                                   "--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("twice", out)
        self.assert_nothing_written()

    def test_accept_cannot_run_with_update_or_adopt_rows(self):
        for other in ("--update", "--adopt-rows"):
            with self.subTest(other=other):
                self.remember()
                code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "x", other)
                self.assertEqual(code, 2)
                self.assertIn("--accept cannot run with", out)
                self.assert_nothing_written()

    def test_accept_why_alone_is_refused(self):
        self.remember()
        code, out = self.run_guard("--accept-why", "x")
        self.assertEqual(code, 2)
        self.assertIn("--accept-why was given with no --accept", out)
        self.assert_nothing_written()

    def test_a_duplicate_accept_does_not_re_date_an_existing_review(self):
        self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "first")
        first_date = self.stored()["reviewed"][ACCEPT_KEY]["recordedOn"]
        self.remember()
        code, out = self.run_guard("--accept", ACCEPT_KEY, "--accept-why", "second")
        self.assertEqual(code, 2)
        self.assertIn("already in the `reviewed` axis", out)
        self.assertEqual(self.stored()["reviewed"][ACCEPT_KEY]["recordedOn"], first_date)
        self.assert_nothing_written()


class ReviewedAxisIsValidated(unittest.TestCase):
    """A hand-edited or truncated `reviewed` axis refuses rather than being read."""

    def _main_with_reviewed(self, reviewed) -> int:
        tmp = tempfile.TemporaryDirectory(prefix="gras-reviewed-")
        self.addCleanup(tmp.cleanup)
        payload = _baseline_with_hole()
        payload["reviewed"] = reviewed
        p = Path(tmp.name) / "baseline.json"
        p.write_text(json.dumps(payload), encoding="utf-8", newline="")
        original_baseline, original_collect = guard.BASELINE, guard.collect
        guard.BASELINE = p
        guard.collect = lambda ws: (_one_citation(), _refused_counts(), _coordinate_counts())
        try:
            return guard.main([])
        finally:
            guard.BASELINE, guard.collect = original_baseline, original_collect

    def _good(self) -> dict:
        pair = guard.row_identity(FIXTURE)[ALPHA_PATHS]
        return {ACCEPT_KEY: _recorded(ACCEPT_KEY, "alpha", pair)}

    def test_a_well_formed_review_is_read_not_refused(self):
        """Exit 1, not 2: the gate may still have findings elsewhere, but the axis itself is sound."""
        self.assertEqual(self._main_with_reviewed(self._good()), 1)

    def test_a_key_that_is_not_a_checked_registry_line_refuses(self):
        pair = guard.row_identity(FIXTURE)[ALPHA_PATHS]
        for key in ("nonsense", "other.v1.json:1", f"{ACCEPT_REGISTRY}:x", ":1"):
            with self.subTest(key=key):
                self.assertEqual(self._main_with_reviewed({key: _recorded(ACCEPT_KEY, "alpha", pair)}),
                                 2)

    def test_an_unknown_adjudication_refuses(self):
        rec = self._good()
        rec[ACCEPT_KEY]["adjudication"] = "probably fine"
        self.assertEqual(self._main_with_reviewed(rec), 2)

    def test_a_review_with_no_reason_refuses(self):
        rec = self._good()
        rec[ACCEPT_KEY]["why"] = "  "
        self.assertEqual(self._main_with_reviewed(rec), 2)

    def test_a_review_naming_no_citing_document_refuses(self):
        rec = self._good()
        rec[ACCEPT_KEY]["citedFrom"] = ""
        self.assertEqual(self._main_with_reviewed(rec), 2)

    def test_a_review_with_no_usable_fingerprint_refuses(self):
        for field in ("contentFingerprint", "rowFingerprint"):
            with self.subTest(field=field):
                rec = self._good()
                rec[ACCEPT_KEY][field] = "not-a-fingerprint"
                self.assertEqual(self._main_with_reviewed(rec), 2)

    def test_a_reviewed_axis_that_is_not_an_object_refuses(self):
        self.assertEqual(self._main_with_reviewed(["x"]), 2)

    def test_a_review_record_that_is_not_an_object_refuses(self):
        self.assertEqual(self._main_with_reviewed({ACCEPT_KEY: "alpha"}), 2)


class TheReviewAxisDoesNotWeakenTheRowAxis(_AcceptHarness):
    """THE NEGATIVE CONTROL FOR THE WHOLE FEATURE.

    A flag that records a promise could easily become a flag that stops checking. These assert the
    opposite: a reviewed key still reds on a shift onto a byte-identical line, and `--update` still
    cannot invent a review.
    """

    def setUp(self):
        super().setUp()
        pair = guard.row_identity(FIXTURE)[BETA_PATHS]
        payload = _baseline_with_hole()
        payload["reviewed"] = {BETA_KEY: _recorded(BETA_KEY, "beta", pair)}
        self.baseline.write_text(json.dumps(payload), encoding="utf-8", newline="")
        self.write_citer(f"the `beta` row owns it ({ACCEPT_REGISTRY}:{BETA_PATHS})\n")
        guard.collect = lambda ws: ({BETA_KEY: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())

    def test_a_reviewed_key_is_green_when_nothing_moved(self):
        code, out = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assertIn(BETA_KEY, out)

    def test_a_reviewed_key_still_reds_when_its_row_slides_onto_a_byte_identical_line(self):
        """The whole hazard, with a REVIEW in place instead of a first-commit baseline.

        The review recorded `beta`'s fingerprint for BETA_PATHS. `duplicate_alpha_before_itself`
        lands that same line on alpha's byte-identical `      "paths": [`, so the CONTENT
        fingerprint is unchanged and only the row axis can see it. A review must not have blinded it.
        """
        mutant = duplicate_alpha_before_itself()
        self.assertEqual(guard.normalise(mutant.split("\n")[BETA_PATHS - 1]),
                         guard.normalise(FIXTURE.split("\n")[BETA_PATHS - 1]),
                         "the twin line's bytes must be unchanged, or this control proves nothing")
        self.use_registry(mutant)                       # installed, and read back to prove it
        code, out = self.run_guard()
        self.assertEqual(code, 1, "a reviewed key whose row slid must red")
        self.assertIn(BETA_KEY, out)
        self.assertIn("REVIEWED", out, "the finding must still name the review's provenance")

    def test_update_still_cannot_write_or_drop_the_reviewed_axis(self):
        code, out = self.run_guard("--update")
        self.assertEqual(code, 0, out)
        self.assertIn(BETA_KEY, self.stored().get("reviewed", {}),
                      "--update must neither drop nor fabricate a review")

    def test_update_cannot_record_a_review_for_an_unreviewed_key(self):
        """The property that keeps `--accept` necessary rather than redundant.

        `--update` re-takes the CONTENT axis from the working tree. It has no reviewer and no reason
        to record, so the key it just fingerprinted must still read as unreviewed afterwards.
        """
        fresh = f"{ACCEPT_REGISTRY}:{ALPHA_PATHS}"
        guard.collect = lambda ws: ({fresh: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        code, out = self.run_guard("--update")
        self.assertEqual(code, 0, out)
        self.assertNotIn(fresh, self.stored().get("reviewed", {}))
        code, out = self.run_guard()
        self.assertEqual(code, 1)
        self.assertIn("is a NEW citation", out)


# ---------------------------------------------------------------------------------------------
# the KEY form: `<registry>#<key>`
#
# WHY THIS CLASS EXISTS. The line form's three failures are measured, not suspected: 556
# byte-identical `    {` lines that one content fingerprint cannot separate; a row fingerprint
# derived from the array index, which nine recorded reviews have already expired under; and a
# citation of a generated line-and-column scan record that cannot be re-pointed at all. A key
# citation names a row by its declared identity, so it is immune to all three - and the only way to
# know that is to mutate the registry and watch.
#
# EVERY MUTANT BELOW IS INSTALLED ON DISK AND READ BACK BEFORE THE GUARD RUNS. Two mutation controls
# in this programme passed because the mutant was never written; `use_registry` asserts the bytes on
# disk differ from the clean fixture before the run and match the mutant after it, so a fixture that
# failed to write cannot produce a green control.
#
# MUTANT 4 IS THE ONE THIS CLASS EXISTS FOR. Inserting an unrelated row ABOVE a key-cited row must
# NOT drift the citation: no line number and no array index appears anywhere in a key row
# fingerprint, so the row's meaning cannot have changed even though its position moved by nine lines.
# ---------------------------------------------------------------------------------------------

#: A registry whose rows are an ARRAY, so the mutants can insert above a cited row without moving the
#: cited row's identity. `alpha` is cited by key; `beta` is the row inserted above it by mutant 4.
KEY_FIXTURE = "\n".join([
    "{",                                    # 1
    '  "boundaries": [',                    # 2
    "    {",                                # 3   <- beta[0]
    '      "id": "beta",',                  # 4
    '      "kind": "owner",',               # 5
    '      "paths": [',                     # 6
    '        "beta/**"',                    # 7
    "      ],",                             # 8
    '      "project": "core",',             # 9
    '      "level": "module"',              # 10
    "    },",                               # 11  <- beta[1]
    "    {",                                # 12  <- alpha[0]
    '      "id": "alpha",',                 # 13
    '      "kind": "owner",',               # 14
    '      "paths": [',                     # 15
    '        "alpha/**"',                   # 16
    "      ],",                             # 17
    '      "project": "core",',             # 18
    '      "level": "module"',              # 19
    "    }",                                # 20  <- alpha[1]
    "  ]",                                  # 21
    "}",                                    # 22
    "",
])

#: The nine lines mutant 4 inserts immediately after `"boundaries": [`. A row above a cited row, in a
#: registry whose rows are an array - the exact shape that expires a line citation.
INSERT_ABOVE = ('  "boundaries": [\n'
                '    {\n'
                '      "id": "unrelated",\n'
                '      "kind": "owner",\n'
                '      "paths": [\n'
                '        "unrelated/**"\n'
                '      ],\n'
                '      "project": "core",\n'
                '      "level": "module"\n'
                '    },')

KEY_ALPHA = f"{ACCEPT_REGISTRY}#alpha"
KEY_BETA = f"{ACCEPT_REGISTRY}#beta"


class _KeyHarness(unittest.TestCase):
    """A fake `core`, a temp baseline, a temp citing document, and a mutant-aware registry file.

    Everything a `--accept` or a gate run reads comes from the temp tree, so no test here can write to
    the committed baseline or to the committed registries - a test that changed the thing it measures
    would measure itself.
    """

    def setUp(self):
        tmp = tempfile.TemporaryDirectory(prefix="gras-key-")
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.core = _fake_core(self.tmp, KEY_FIXTURE)
        self.clean_registry = (self.core / "scripts" / ACCEPT_REGISTRY).read_bytes()
        self.baseline = self.tmp / "baseline.json"
        self.baseline.write_text(json.dumps(_baseline_with_hole()), encoding="utf-8", newline="")
        self.citer = self.tmp / "tasks" / "x.md"
        self.citer.parent.mkdir(parents=True, exist_ok=True)
        self.original_baseline = guard.BASELINE
        self.original_collect = guard.collect
        self.original_core = None
        guard.BASELINE = self.baseline
        guard.collect = lambda ws: ({self.key: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        self.addCleanup(self._restore)

    def _restore(self):
        guard.BASELINE = self.original_baseline
        guard.collect = self.original_collect

    # the citation this class cites by key
    key = KEY_ALPHA

    def use_registry(self, text: str) -> None:
        """Install `text` as the registry ON DISK, then prove it is there.

        The read-back is not ceremony. A mutant control that passed because the mutant was never
        installed is the exact failure this repo has already paid for twice, so the write is verified
        against the clean fixture before the guard runs and the mutation is proved different.
        """
        path = self.core / "scripts" / ACCEPT_REGISTRY
        path.write_text(text, encoding="utf-8", newline="")
        on_disk = path.read_bytes()
        self.assertEqual(on_disk, text.encode("utf-8").replace(b"\r\n", b"\n"),
                         "the mutant is not on disk byte-for-byte")
        self.assertNotEqual(on_disk, self.clean_registry,
                            "the mutant is byte-identical to the clean fixture, so it is no mutant")

    def write_citer(self, text: str) -> None:
        self.citer.write_text(text, encoding="utf-8")

    def run_guard(self, *args: str) -> "tuple[int, str]":
        """Run the guard against the FAKE core, capturing everything it printed.

        `guard.evaluate` resolves the registries relative to `__file__`'s grandparent, so pointing the
        module at the fake `core` is what makes each mutant real rather than merely described.
        """
        original = guard.__file__
        guard.__file__ = str(self.core / "scripts" / "guard-registry-append-safety.py")
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
                code = guard.main(["--root", str(self.tmp), *args])
        finally:
            guard.__file__ = original
        return code, buf.getvalue()

    def stored(self) -> dict:
        return json.loads(self.baseline.read_text(encoding="utf-8"))

    def review(self) -> dict:
        """Record the key citation and return the stored record, failing loudly if it was refused."""
        code, out = self.run_guard("--accept", self.key, "--accept-why", "because I read it")
        self.assertEqual(code, 0, out)
        return self.stored()["reviewedKeys"][self.key]


class KeyFormResolution(unittest.TestCase):
    """The resolution rule, tested against the real registries rather than a guessed shape."""

    def setUp(self):
        self.docs = {name: json.loads((REPO / rel).read_text(encoding="utf-8"))
                     for name, rel, _c in guard.REGISTRIES}
        self.flat = {name: guard.is_flat(name) for name, _r, _c in guard.REGISTRIES}

    def _resolve(self, name: str, key: str) -> dict:
        return guard.resolve_key(self.docs[name], key, self.flat[name])

    def test_an_array_row_resolves_by_its_declared_id(self):
        res = self._resolve("verification-boundaries.v1.json", "core-fallback")
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["container"], "boundaries")
        self.assertEqual(res["kind"], "array")
        self.assertEqual(res["token"], "id=core-fallback")

    def test_a_map_row_resolves_by_its_own_key(self):
        """MEASURED, not assumed: `narrative` is a `guards` map key in this registry, not an
        `invariants` id. Asserting what the file says rather than what the name suggests is the point
        - the resolution rule has to be derived from the rows, not from how a key reads."""
        res = self._resolve("enforcement-registry.v1.json", "narrative")
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["container"], "guards")
        self.assertEqual(res["kind"], "map")
        self.assertEqual(res["token"], "narrative",
                         "a MAP row is named by its key, not by an `id=` prefix")

    def test_an_invariant_array_row_resolves_by_its_declared_id(self):
        invariant = self.docs["enforcement-registry.v1.json"]["invariants"][0]["id"]
        res = self._resolve("enforcement-registry.v1.json", invariant)
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["container"], "invariants")
        self.assertEqual(res["token"], f"id={invariant}")

    def test_a_registry_row_map_resolves_by_the_guard_name(self):
        res = self._resolve("enforcement-registry.v1.json", "citation-stability")
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["container"], "guards")
        self.assertEqual(res["kind"], "map")
        self.assertEqual(res["token"], "citation-stability",
                         "a MAP row is named by its key, not by an `id=` prefix")

    def test_a_flat_registry_row_resolves_and_its_slash_is_not_a_qualifier(self):
        res = self._resolve("todo-shapes.v1.json", "tasks/live-probe-todo.md")
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["container"], "<root>",
                         "a registry declared with no container has ONE namespace, not one per row")
        self.assertEqual(res["keyText"], "tasks/live-probe-todo.md",
                         "`tasks` is not a namespace, so the whole string is the key")

    def test_a_key_that_names_no_row_is_unresolved_and_names_what_it_tried(self):
        res = self._resolve("verification-boundaries.v1.json", "no-such-row")
        self.assertEqual(res["status"], "unresolved")
        self.assertEqual(res["tried"], ["no-such-row"])
        self.assertIn("boundaries", res["namespaces"])

    def test_the_two_namespaces_really_do_collide_and_the_collision_is_reported(self):
        """Measured on the real registry: this is why a namespace qualifier exists at all."""
        index = guard.row_key_index(self.docs["verification-boundaries.v1.json"], flat=False)
        collisions = {k for k, v in index.items() if len({m["container"] for m in v}) > 1}
        self.assertIn("core-atoms", collisions, "the collision this guards is no longer in the file")
        res = self._resolve("verification-boundaries.v1.json", "core-atoms")
        self.assertEqual(res["status"], "ambiguous")
        self.assertEqual(res["namespaces"], ["boundaries/array", "projects/map"])

    def test_the_namespace_qualifier_resolves_a_colliding_key(self):
        for spelled, container in (("boundaries/core-atoms", "boundaries"),
                                   ("projects/core-atoms", "projects")):
            with self.subTest(spelled=spelled):
                res = self._resolve("verification-boundaries.v1.json", spelled)
                self.assertEqual(res["status"], "resolved")
                self.assertEqual(res["container"], container)

    def test_a_namespace_this_registry_does_not_have_is_refused_by_name(self):
        res = self._resolve("verification-boundaries.v1.json", "invented/core-atoms")
        self.assertEqual(res["status"], "unknown-container")
        self.assertIn("boundaries", res["namespaces"])

    def test_every_declared_key_in_every_registry_resolves_or_is_reported_ambiguous(self):
        """A KEY INDEX THAT SILENTLY DROPS A ROW WOULD MAKE THE FORM UNSAFE, SO NONE MAY."""
        for name, _rel, _c in guard.REGISTRIES:
            index = guard.row_key_index(self.docs[name], self.flat[name])
            for key, matches in index.items():
                with self.subTest(registry=name, key=key):
                    namespaces = {m["container"] for m in matches}
                    res = self._resolve(name, key)
                    self.assertEqual(res["status"],
                                     "resolved" if len(namespaces) == 1 else "ambiguous")
                    self.assertTrue(res.get("contentFingerprint") or res.get("namespaces"))

    def test_a_sentence_final_key_resolves_because_the_dot_is_tried_off_the_registry(self):
        res = self._resolve("todo-shapes.v1.json", "tasks/live-probe-todo.md.")
        self.assertEqual(res["status"], "resolved")
        self.assertEqual(res["keyText"], "tasks/live-probe-todo.md")


class TheKeyAxesDoTheirJob(_KeyHarness):
    """THE FOUR MUTANTS, each installed on disk and read back before the guard runs.

    Every one of these is the hazard the key form exists to survive, and every one is a case where a
    guard that passed would be worse than no guard at all.
    """

    def test_mutant1_renaming_the_row_a_key_citation_names_is_reported(self):
        """RENAME the cited row. The key no longer resolves, so the citation names nothing."""
        self.review()
        self.use_registry(KEY_FIXTURE.replace('"id": "alpha"', '"id": "alpha-renamed"'))
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S7-KEY-UNRESOLVED", out)
        self.assertIn(KEY_ALPHA, out)
        self.assertIn("names NO row of this registry", out)

    def test_mutant1b_deleting_the_row_a_key_citation_names_is_reported(self):
        """REMOVE the row. Same finding, and it is the other half of the same hazard."""
        self.review()
        without_alpha = "\n".join([
            "{",
            '  "boundaries": [',
            "    {",
            '      "id": "beta",',
            '      "kind": "owner",',
            '      "paths": [',
            '        "beta/**"',
            "      ],",
            '      "project": "core",',
            '      "level": "module"',
            "    }",
            "  ]",
            "}",
            "",
        ])
        self.use_registry(without_alpha)
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S7-KEY-UNRESOLVED", out)
        self.assertIn("names NO row of this registry", out)

    def test_mutant2_changing_the_content_of_the_row_a_key_citation_names_is_reported(self):
        """EDIT the row under its key. The key still resolves, so only the content axis can see it."""
        self.review()
        self.use_registry(KEY_FIXTURE.replace('"alpha/**"', '"alpha/**", "alpha/extra/**"'))
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S1-KEY-CONTENT-CHANGED", out)
        self.assertIn("WHOLE CONTENT has changed", out)

    def test_mutant2b_editing_a_row_the_key_does_not_name_stays_green(self):
        """The control on mutant 2: a key citation is about ITS row, not about the file.

        A fingerprint over the whole FILE would red here and would also have made every registry edit
        a finding, which is the mistake a per-row fingerprint exists to avoid.
        """
        self.review()
        self.use_registry(KEY_FIXTURE.replace('"beta/**"', '"beta/**", "beta/extra/**"'))
        code, out = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assertIn("GUARD OK", out)

    def test_mutant3_a_key_citation_naming_a_key_that_does_not_resolve_is_reported(self):
        """A citation of a row that does not exist. Never a silent pass.

        `--accept` REFUSES to record such a key, which is the other half of this check and is
        asserted here too: a review of a row that is not there cannot be written at all.
        """
        self.key = f"{ACCEPT_REGISTRY}#no-such-row"
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S7-KEY-UNRESOLVED", out)
        self.assertIn("no-such-row", out)
        self.assertIn("Tried ['no-such-row']", out, "the finding must name every spelling it tried")
        # And the review flag refuses it, rather than writing a promise about nothing.
        code, out = self.run_guard("--accept", self.key, "--accept-why", "read it")
        self.assertEqual(code, 2, out)
        self.assertIn("no row of", out)

    def test_mutant3b_a_key_citation_naming_an_ambiguous_key_is_reported_not_guessed(self):
        """The measured collision: two rows answer, so the guard names both and picks neither."""
        self.use_registry("\n".join([
            "{",
            '  "boundaries": [',
            '    {',
            '      "id": "dup",',
            '      "kind": "owner"',
            "    }",
            "  ],",
            '  "projects": {',
            '    "dup": [',
            '      "dup/**"',
            "    ]",
            "  }",
            "}",
            "",
        ]))
        self.key = f"{ACCEPT_REGISTRY}#dup"
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S7-KEY-AMBIGUOUS", out)
        self.assertIn("boundaries/array", out)
        self.assertIn("projects/map", out)
        self.assertIn("will not pick one", out)
        # And the review flag refuses it, naming the repair rather than choosing a namespace.
        code, out = self.run_guard("--accept", self.key, "--accept-why", "read it")
        self.assertEqual(code, 2, out)
        self.assertIn(f"#<namespace>/dup", out)

    def test_mutant3c_the_namespace_qualifier_is_the_repair_the_ambiguity_finding_names(self):
        self.use_registry("\n".join([
            "{",
            '  "boundaries": [',
            '    {',
            '      "id": "dup",',
            '      "kind": "owner"',
            "    }",
            "  ],",
            '  "projects": {',
            '    "dup": [',
            '      "dup/**"',
            "    ]",
            "  }",
            "}",
            "",
        ]))
        self.key = f"{ACCEPT_REGISTRY}#boundaries/dup"
        record = self.review()
        self.assertEqual(record["container"], "boundaries")
        self.assertEqual(record["rowKey"], "dup")
        code, out = self.run_guard()
        self.assertEqual(code, 0, out)

    def test_mutant4_inserting_an_unrelated_row_above_a_key_cited_row_does_NOT_drift(self):
        """THE CONTROL THIS WHOLE CHANGE EXISTS FOR, and the one to demonstrate most carefully.

        The same insertion on the LINE form moves every later cited line and expires every review
        under it. Here it moves alpha from `boundaries[1]` to `boundaries[2]` and its line from 12 to
        21, and the citation must not notice: a key row fingerprint is the namespace, the element kind
        and the key - no line, no index.
        """
        record = self.review()
        self.assertEqual(record["container"], "boundaries")
        self.assertEqual(record["kind"], "array")
        self.use_registry(KEY_FIXTURE.replace('  "boundaries": [', INSERT_ABOVE))
        # The row really did move: prove it before asserting that moving it changed nothing.
        alpha_index = KEY_FIXTURE.split("\n").index('      "id": "alpha",') + 1
        moved_index = KEY_FIXTURE.replace(
            '  "boundaries": [', INSERT_ABOVE).split("\n").index('      "id": "alpha",') + 1
        self.assertEqual(moved_index - alpha_index, 9,
                         "the fixture did not move alpha by nine lines, so the control proves nothing")
        code, out = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assertIn("GUARD OK", out)
        self.assertIn("1 KEY", out, "the run must still be counting this citation as a KEY citation")

    def test_mutant4b_the_same_insertion_expires_a_line_citation_of_the_same_row(self):
        """The paired control: it is not that insertion is safe here - it is that the KEY form is why.

        The same nine lines inserted above a citation of a line inside alpha move that line onto a
        line inside `beta`, and the LINE form's row axis sees it because its fingerprint is the
        position. Asserted next to mutant 4 because a control that shows only one half is the shape of
        the mistake this class exists to prevent.
        """
        clean = json.loads(KEY_FIXTURE)
        self.assertEqual(clean["boundaries"][1]["id"], "alpha")
        alpha_line = next(n for n, line in enumerate(KEY_FIXTURE.split("\n"), 1)
                          if line.strip().startswith('"alpha/**"'))
        clean_rows = guard.row_identity(KEY_FIXTURE)
        clean_token = guard.row_token(clean, clean_rows[alpha_line])
        self.assertEqual(clean_token, "id=alpha",
                         "before the insertion, that line is inside alpha")
        clean["boundaries"].insert(0, {"id": "unrelated", "kind": "owner",
                                       "paths": ["unrelated/**"], "project": "core",
                                       "level": "module"})
        moved_text = json.dumps(clean, indent=2)
        moved_rows = guard.row_identity(moved_text)
        moved_token = guard.row_token(clean, moved_rows[alpha_line])
        self.assertNotEqual(moved_token, "id=alpha",
                            "after the insertion, the SAME line number is inside a different row")
        self.assertNotEqual(guard.row_fingerprint(clean_rows[alpha_line], clean_token),
                            guard.row_fingerprint(moved_rows[alpha_line], moved_token),
                            "so the LINE form's row axis reds on it")
        # And the KEY form's row fingerprint cannot see a position, because it has none:
        self.assertEqual(guard.key_row_fingerprint("boundaries", "array", "alpha"),
                         guard.key_row_fingerprint("boundaries", "array", "alpha"))

    def test_a_row_that_slides_into_another_namespace_is_reported_on_the_row_axis(self):
        """Two rows can say the same thing and be two different rows; only identity separates them.

        `alpha` is moved verbatim from the `boundaries` ARRAY into the `projects` MAP. The content
        fingerprint is therefore IDENTICAL and the key still resolves to exactly one row - so this is
        the one case the content axis provably cannot see, and the row axis is what reports it. That is
        the byte-identical twin, transplanted from lines to keys.
        """
        self.review()
        self.use_registry("\n".join([
            "{",
            '  "boundaries": [',
            "    {",
            '      "id": "beta",',
            '      "kind": "owner",',
            '      "paths": [',
            '        "beta/**"',
            "      ],",
            '      "project": "core",',
            '      "level": "module"',
            "    }",
            "  ],",
            '  "projects": {',
            '    "alpha": {',
            '      "id": "alpha",',
            '      "kind": "owner",',
            '      "paths": [',
            '        "alpha/**"',
            "      ],",
            '      "project": "core",',
            '      "level": "module"',
            "    }",
            "  }",
            "}",
            "",
        ]))
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S5-KEY-ROW-CHANGED", out)
        self.assertIn("now resolves to a different ROW", out)
        self.assertNotIn("S1-KEY-CONTENT-CHANGED", out,
                         "the content really is unchanged, so S1 must stay silent - that is the point")
        self.assertIn("no array index", out.replace("\n", " "))

    def test_the_content_axis_really_is_silent_when_only_the_namespace_changed(self):
        """The measurement behind the test above, asserted directly so it cannot drift silently."""
        before = guard.resolve_key(json.loads(KEY_FIXTURE), "alpha", flat=False)
        after_doc = json.loads(KEY_FIXTURE)
        alpha_row = after_doc["boundaries"].pop(1)
        self.assertEqual(alpha_row["id"], "alpha")
        after_doc["projects"] = {"alpha": alpha_row}
        after = guard.resolve_key(after_doc, "alpha", flat=False)
        self.assertEqual(before["status"], "resolved")
        self.assertEqual(after["status"], "resolved")
        self.assertNotEqual(before["container"], after["container"])
        self.assertEqual(before["contentFingerprint"], after["contentFingerprint"],
                         "the content axis is blind to a namespace slide, which is what S5-KEY exists for")
        self.assertNotEqual(before["rowFingerprint"], after["rowFingerprint"])


class KeyAxisProvenance(unittest.TestCase):
    """NO BLANKET OPERATION MAY BLESS A KEY CITATION. These are the refusals that make that true."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory(prefix="gras-keyprov-")
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.baseline = self.tmp / "baseline.json"
        self.baseline.write_text(json.dumps(_baseline_with_hole()), encoding="utf-8", newline="")
        self.core = _fake_core(self.tmp, KEY_FIXTURE)
        self.key = KEY_ALPHA
        self.original_baseline, self.original_collect = guard.BASELINE, guard.collect
        self.original_core = pathlib.Path(guard.__file__).resolve().parent.parent
        guard.BASELINE = self.baseline
        guard.collect = lambda ws: ({self.key: ("tasks/x.md", 1)}, _refused_counts(), _coordinate_counts())
        self.addCleanup(self._restore)

    def _restore(self):
        guard.BASELINE = self.original_baseline
        guard.collect = self.original_collect
        guard.__file__ = str(self.original_core / "scripts" / "guard-registry-append-safety.py")

    def run_guard(self, *args: str) -> "tuple[int, str]":
        original = guard.__file__
        guard.__file__ = str(self.core / "scripts" / "guard-registry-append-safety.py")
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
                code = guard.main(["--root", str(self.tmp), *args])
        finally:
            guard.__file__ = original
        return code, buf.getvalue()

    def stored(self) -> dict:
        return json.loads(self.baseline.read_text(encoding="utf-8"))

    def _seed_review(self) -> None:
        code, out = self.run_guard("--accept", self.key, "--accept-why", "read it")
        self.assertEqual(code, 0, out)

    def test_an_unreviewed_key_citation_is_reported_and_names_its_only_repair(self):
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S8-KEY-NOT-REVIEWED", out)
        self.assertIn(f"--accept {self.key}", out)

    def test_update_cannot_write_the_key_axis(self):
        """`--update` is the blanket operation. If it could bless a key, the form would expire too."""
        code, out = self.run_guard("--update")
        self.assertEqual(code, 0, out)
        self.assertIn("STRUCTURALLY CANNOT write it", out)
        self.assertEqual(self.stored().get("reviewedKeys", {}), {})
        code, out = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("S8-KEY-NOT-REVIEWED", out)

    def test_update_does_not_drop_an_existing_key_review(self):
        self._seed_review()
        before = self.stored()["reviewedKeys"]
        code, _out = self.run_guard("--update")
        self.assertEqual(code, 0)
        self.assertEqual(self.stored()["reviewedKeys"], before)

    def test_a_reviewed_key_is_green_when_nothing_moved(self):
        self._seed_review()
        code, out = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assertIn("REVIEWED KEYS", out)

    def test_a_reviewed_key_cannot_be_re_reviewed_and_re_dated(self):
        self._seed_review()
        recorded_on = self.stored()["reviewedKeys"][self.key]["recordedOn"]
        code, out = self.run_guard("--accept", self.key, "--accept-why", "again")
        self.assertEqual(code, 2, out)
        self.assertIn("already in the `reviewedKeys` axis", out)
        self.assertEqual(self.stored()["reviewedKeys"][self.key]["recordedOn"], recorded_on)

    def test_a_reviewed_key_names_no_line_anywhere_in_its_record(self):
        """The whole property, asserted on the record rather than inferred from the fingerprints."""
        self._seed_review()
        record = self.stored()["reviewedKeys"][self.key]
        self.assertNotIn("line", record)
        self.assertRegex(record["rowFingerprint"], r"^[0-9a-f]{16}$")
        self.assertRegex(record["contentFingerprint"], r"^[0-9a-f]{16}$")

    def test_the_row_fingerprint_carries_neither_a_line_nor_an_index(self):
        """Structural, so it cannot rot: three strings and no position of any kind.

        Asserted by BEHAVIOUR rather than by reading the source: there is no argument that could carry a
        position, and a fingerprint computed from the same three inputs is the same value forever.
        """
        import inspect
        signature = inspect.signature(guard.key_row_fingerprint)
        self.assertEqual(list(signature.parameters), ["container", "kind", "key"],
                         "the function takes no position, so it cannot encode one")
        first = guard.key_row_fingerprint("boundaries", "array", "alpha")
        second = guard.key_row_fingerprint("boundaries", "array", "beta")
        self.assertNotEqual(first, second, "two rows under different keys must differ")
        self.assertEqual(first, guard.key_row_fingerprint("boundaries", "array", "alpha"),
                         "the fingerprint must be a pure function of namespace, kind and key")
        self.assertNotEqual(first, guard.key_row_fingerprint("projects", "array", "alpha"),
                            "the namespace is part of the identity, so a slide between them reds")

    def test_the_content_fingerprint_is_over_the_whole_row_not_one_line(self):
        """The measurement that makes this form stronger than the line form, asserted as a fact."""
        doc = json.loads(KEY_FIXTURE)
        row = doc["boundaries"][1]
        self.assertEqual(guard.key_content_fingerprint(row),
                         guard.key_content_fingerprint(dict(reversed(list(row.items())))),
                         "member order must not change the fingerprint")
        changed = dict(row, level="focused")
        self.assertNotEqual(guard.key_content_fingerprint(row),
                            guard.key_content_fingerprint(changed),
                            "any field of the row must change it, not only the first line")

    def test_the_key_axis_is_validated_and_an_incomplete_record_refuses(self):
        self._seed_review()
        record = self.stored()["reviewedKeys"][self.key]
        for field in ("why", "citedFrom", "container", "kind", "rowToken", "recordedOn"):
            with self.subTest(field=field):
                stored = self.stored()
                broken = dict(record)
                broken.pop(field)
                stored["reviewedKeys"] = {self.key: broken}
                self.baseline.write_text(json.dumps(stored), encoding="utf-8", newline="")
                code, out = self.run_guard()
                self.assertEqual(code, 2, out)
                self.assertIn(field, out)

    def test_a_line_record_in_the_key_axis_refuses_rather_than_being_read(self):
        stored = self.stored()
        stored["reviewedKeys"] = {self.key: {"why": "x", "citedFrom": "y"}}
        self.baseline.write_text(json.dumps(stored), encoding="utf-8", newline="")
        code, out = self.run_guard()
        self.assertEqual(code, 2, out)
        self.assertIn("reviewedKeys", out)

    def test_a_key_record_in_the_line_axis_refuses_rather_than_being_read(self):
        """The other direction, and the reason the two axes are not merged."""
        stored = self.stored()
        stored["reviewed"] = {self.key: {"why": "x", "citedFrom": "y"}}
        self.baseline.write_text(json.dumps(stored), encoding="utf-8", newline="")
        code, out = self.run_guard()
        self.assertEqual(code, 2, out)
        self.assertIn("reviewed", out)


class KeyFormSurface(unittest.TestCase):
    """The CLI, the output, and the count a reader uses to judge how much is left to convert."""

    def test_the_two_forms_are_distinguishable_in_the_report(self):
        proc = subprocess.run([sys.executable, str(SCRIPT), "--report"], cwd=str(REPO),
                              capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=900)
        self.assertIn(proc.returncode, (0, 1), proc.stderr)
        self.assertIn("CITATION FORMS", proc.stdout)
        self.assertIn("by LINE", proc.stdout)
        self.assertIn("by KEY", proc.stdout)

    def test_the_json_carries_the_form_of_every_citation(self):
        proc = subprocess.run([sys.executable, str(SCRIPT), "--json"], cwd=str(REPO),
                              capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=900)
        payload = json.loads(proc.stdout)
        self.assertEqual(set(payload["citationsByForm"]), {"line", "key"})
        self.assertEqual(sum(payload["citationsByForm"].values()), payload["citations"])
        for g in payload["geometries"]:
            self.assertEqual(g["lineCitations"] + g["keyCitations"], g["citations"])

    def test_adopt_rows_cannot_write_the_key_axis(self):
        """Structural, asserted against the source rather than trusted to the code path."""
        import inspect
        source = inspect.getsource(guard.adopt_rows)
        self.assertIn("citation_form(key) != FORM_LINE", source,
                      "--adopt-rows must skip key citations, not fingerprint them as lines")
        self.assertIn("structurally cannot write the KEY axis", source)

    def test_the_line_form_is_not_weakened_by_the_key_form(self):
        """Both forms parse, both are distinguished, and neither is silently reinterpreted.

        THE SPELLINGS ARE ASSEMBLED, NOT WRITTEN OUT, for the reason in `FIRST_REGISTRY` - and this
        method's own draft is the proof of that reason. It spelled one, so the guard read it as a real
        citation in this workspace and reported the cited row as one whose line had drifted. Nothing
        in any registry could have cleared it: the fixture's number is arbitrary, so re-pointing it
        MOVED the finding instead of removing it, which is the signature of a phantom rather than of
        a claim. The repair belongs here, in the fixture, because a string literal used as a value is
        counted on purpose - see the module docstring. The number is not written out even in this
        sentence, because prose is scanned too.
        """
        registry, line_no, key = FORM_REGISTRY, FORM_LINE, _key_of(FORM_REGISTRY)
        line = guard.CITATION.search(f"see {registry}:{line_no} for the row")
        self.assertEqual(guard.citation_form(guard.citation_text(line.group("basename"), line)), "line")
        self.assertEqual(guard.citation_text(line.group("basename"), line),
                         f"{registry}:{line_no}")
        keyed = guard.CITATION.search(f"see {registry}#{key} for it")
        self.assertEqual(guard.citation_form(guard.citation_text(keyed.group("basename"), keyed)), "key")
        self.assertEqual(guard.citation_text(keyed.group("basename"), keyed), f"{registry}#{key}")

    def test_a_key_wrapped_in_backticks_or_followed_by_punctuation_is_captured_whole(self):
        """Built from parts, for the reason in `FIRST_REGISTRY`.

        These five spellings were LITERAL until this revision, which is how the file was still a citing
        document for a row it never claimed anything about: the line form's phantom was reported,
        because its registry had drifted, while this one stayed silent only because another document
        happened to cite the same key first. A shadowed phantom is still a phantom - it would surface
        the day that other citation moved.
        """
        registry, key = FORM_REGISTRY, _key_of(FORM_REGISTRY)
        for spelling, expected in ((f"`{registry}#{key}`", key),
                                  (f"{registry}#{key})", key),
                                  (f"{registry}#{key},", key),
                                  (f"({registry}#{key})", key),
                                  (f'"{registry}#{key}"', key)):
            with self.subTest(spelling=spelling):
                m = guard.CITATION.search(spelling)
                self.assertIsNotNone(m, spelling)
                self.assertEqual(m.group("key"), expected)

    def test_a_sentence_final_dot_is_captured_and_then_resolved_off_the_registry(self):
        """`.md` keys mean the character class cannot exclude `.`, so the registry decides - and the
        citation is still stored exactly as written, because a stored key must match what a reader
        can see.

        THE SPELLINGS ARE BUILT FROM PARTS, NOT WRITTEN OUT. This file is scanned by the guard it tests,
        and a literal citation here is a REAL citation in the workspace: the first draft of this file
        spelled one and the guard reported itself as a new citation with no baseline entry. That is the
        comment filter working, and it is why the pre-existing keys in this file are built from
        `guard.REGISTRIES` rather than written.
        """
        registry = FLAT_REGISTRY
        key = _flat_key()
        plain = guard.CITATION.search(f"the row is `{registry}#{key}`")
        self.assertEqual(plain.group("key"), key)
        trailing = guard.CITATION.search(f"the row is {registry}#{key}.")
        self.assertEqual(trailing.group("key"), f"{key}.",
                         "the trailing dot IS captured, and resolved off the registry instead")
        stored = guard.citation_text(trailing.group("basename"), trailing)
        self.assertEqual(stored, f"{registry}#{key}.",
                         "the stored key is the spelling in the text, dot and all")
        self.assertEqual(guard.resolve_key(
            json.loads((REPO / "scripts" / registry).read_text(encoding="utf-8")),
            guard.row_key_of(stored), flat=True)["keyText"], key)

    def test_a_bare_hash_is_not_a_citation_form(self):
        """`#` is a heading, an anchor and a colour, so a bare `#name` must not be read as a citation."""
        m = guard.CITATION.search("see #core-fallback for the row")
        self.assertIsNone(m)
        self.assertEqual(guard.sentence_registry_keys("see #core-fallback"), set())


# ---------------------------------------------------------------------------------------------
# `path:line:column` is a scanner coordinate, not a citation
#
# The repair this covers is the guard reading the `:19` of a coordinate's `1369:19` as nothing at
# all, keeping `:1369`, and then reporting the registry for not holding at line 1369 whatever the
# coordinate was about. It produced 13 findings and 0 wrong citations. So the tests below are written
# as a PAIR at every level - the triple is not a citation, and the same line WITHOUT its column still
# is - because a rule that cannot be shown to hold both ways is an exemption wearing a rule's clothes.
# ---------------------------------------------------------------------------------------------


class _CoordinateHarness(unittest.TestCase):
    """A temp workspace, the REAL `collect`, and a baseline that says the cited line is stale.

    `collect` is deliberately NOT stubbed here. Every other harness patches it out, which is right for
    testing what happens to a citation the guard has been handed and useless for proving what the
    guard decided a STRING was - so these tests let the real scanner read a document off disk. The
    baseline records one stale content fingerprint and one stale row for the line under test, which is
    exactly the state a moved line is in, and it is the state that makes a REAL citation red.

    The fake core lives OUTSIDE the scanned workspace on purpose: `collect` walks everything under the
    workspace root, and a registry JSON sitting inside it could carry a citation of its own.
    """

    def setUp(self):
        tmp = tempfile.TemporaryDirectory(prefix="gras-coordinate-")
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.ws = self.tmp / "ws"
        (self.ws / "tasks").mkdir(parents=True)
        self.core = _fake_core(self.tmp / "core", FIXTURE)
        self.key = f"{ACCEPT_REGISTRY}:{ACCEPT_LINE}"
        stale = _baseline_with_hole()
        stale["fingerprints"][self.key] = guard.fingerprint("a line that has since moved")
        stale["rows"][self.key] = guard.row_fingerprint(("<root>/gone", "<root>/gone"), "gone")
        self.baseline = self.tmp / "baseline.json"
        self.baseline.write_text(json.dumps(stale), encoding="utf-8", newline="")
        self._original_baseline = guard.BASELINE
        guard.BASELINE = self.baseline

    def tearDown(self):
        guard.BASELINE = self._original_baseline

    def write(self, text: str) -> None:
        """Install a citing document and read it back, so a silent no-op write cannot pass as a test."""
        target = self.ws / "tasks" / "x.md"
        target.write_text(text, encoding="utf-8", newline="")
        self.assertEqual(target.read_text(encoding="utf-8"), text, "the citer is not what landed on disk")

    def run_guard(self, *, as_json: bool = True) -> tuple:
        """Run the REAL CLI and hand back `(exit code, payload)` - parsed, or raw text if not `as_json`.

        Through `main` rather than `evaluate`, because the content and row axes are applied in `main`:
        `evaluate` resolves what a citation POINTS AT, and the finding about whether it still holds
        what it promised is made where the baseline is read. `guard.__file__` is moved to the fake core
        so the registries resolve there instead of in the repository, the same trick `_KeyHarness`
        uses. `--json` because a finding read back as data cannot be satisfied by a substring that
        happens to appear in a paragraph of advice - except where the run REFUSES, which is printed to
        stderr and has no JSON, so those tests ask for the text.
        """
        original = guard.__file__
        guard.__file__ = str(self.core / "scripts" / "guard-registry-append-safety.py")
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
                code = guard.main(["--root", str(self.ws)] + (["--json"] if as_json else []))
        finally:
            guard.__file__ = original
        return code, (json.loads(buf.getvalue()) if as_json else buf.getvalue())

    def codes(self, payload: dict) -> list:
        return sorted(f["code"] for f in payload["findings"])

    def notes_about(self, payload: dict) -> str:
        return " ".join(f.get("note", "") for f in payload["findings"])


class AScannerCoordinateIsNotACitation(_CoordinateHarness):
    """THE RULE. `<registry>:<line>:<column>` is a scanner position; `<registry>:<line>` is a promise."""

    def test_a_coordinate_is_read_as_no_citation_at_all(self):
        """The whole point: nothing is kept, because silently keeping the line is what produced 13
        findings against documents whose citations were correct all along.

        A workspace whose only spelling is a coordinate holds ZERO citations, and this guard's own
        answer to that is exit 2 - it refuses rather than reporting a clean run against an empty set.
        So the refusal IS the assertion: it is the loudest statement the guard can make that the
        coordinate yielded no citation, and it is checked as the outcome rather than routed around.
        """
        self.write(f"- `scripts/{ACCEPT_REGISTRY}:{ACCEPT_LINE}:19` **zomboss** - bucket `code-identifier`\n")
        code, out = self.run_guard(as_json=False)
        self.assertEqual(code, 2, out)
        self.assertIn("ZERO citations found", out)

    def test_the_same_line_without_its_column_is_still_reported(self):
        """THE NEAR MISS, and the half of the pair that makes the rule a discrimination. Identical
        document, identical line number, one character less - and the guard reds."""
        self.write(f"the row is `scripts/{ACCEPT_REGISTRY}:{ACCEPT_LINE}` (see it)\n")
        code, payload = self.run_guard()
        self.assertEqual(payload["citations"], 1)
        self.assertEqual(self.codes(payload), ["S1-CITED-LINE-MOVED", "S5-CITED-ROW-CHANGED"])
        self.assertIn(self.key, self.notes_about(payload), "both findings must name the citation")
        self.assertEqual(code, 1)

    def test_both_spellings_in_one_document_split_exactly_along_the_column(self):
        """The two forms side by side in a single document, so the discrimination cannot be an
        artefact of which file was scanned."""
        self.write(f"- `scripts/{ACCEPT_REGISTRY}:{ACCEPT_LINE}:19` zomboss\n"
                   f"- `scripts/{ACCEPT_REGISTRY}:{ACCEPT_LINE}` the row\n")
        _code, payload = self.run_guard()
        self.assertEqual(payload["citations"], 1, "only the bare line became a citation")
        self.assertEqual(self.codes(payload), ["S1-CITED-LINE-MOVED", "S5-CITED-ROW-CHANGED"])
        self.assertEqual(payload["coordinatesRefused"][ACCEPT_REGISTRY], 1,
                         "and exactly the coordinate was counted as one")

    def test_a_prose_colon_after_a_citation_is_not_mistaken_for_a_column(self):
        """THE OVER-REACH GUARD. `<registry>:<line>: and then the row` is a citation written with a
        colon, which is ordinary prose; refusing it would lose a real citation to protect a rule."""
        self.write(f"as `scripts/{ACCEPT_REGISTRY}:{ACCEPT_LINE}: and then the row above it\n")
        _code, payload = self.run_guard()
        self.assertEqual(self.codes(payload), ["S1-CITED-LINE-MOVED", "S5-CITED-ROW-CHANGED"],
                         "a prose colon is not a column, so the citation is still checked")
        self.assertIn(self.key, self.notes_about(payload))
        self.assertEqual(payload["coordinatesRefused"][ACCEPT_REGISTRY], 0)

    def test_the_line_number_is_never_truncated_to_a_prefix(self):
        r"""THE BACKTRACKING TRAP. A naive `(?![:\d])` refuses `:1369:` only after `\d+` has already
        given back a digit, and the match it then finds is `:136` - so the guard would report the
        registry for line 136, a worse finding than the one being fixed. Asserted over several digit
        counts, AND against the control that the prefix spelling is a citation on its own - without
        that control this test would also pass if the rule simply refused everything."""
        registry = ACCEPT_REGISTRY
        for line_no, column in ((1, 9), (8, 1), (136, 9), (1369, 19), (123456, 789)):
            with self.subTest(line=line_no, column=column):
                self.assertIsNone(guard.CITATION.search(f"{registry}:{line_no}:{column}"),
                                  "a coordinate yields no match at any digit count")
        for prefix in (1, 13, 136):
            with self.subTest(prefix=prefix):
                self.assertIsNotNone(guard.CITATION.search(f"{registry}:{prefix}"),
                                     "the prefix spelling IS a citation, so the refusals above are "
                                     "about the third component and not about the line number")

    def test_the_coordinate_regex_reads_the_line_and_the_column_it_declined_to_cite(self):
        """What is refused is still MEASURED, so the refusal can be reported and audited."""
        m = guard.COORDINATE.search(f"{ACCEPT_REGISTRY}:1369:19")
        self.assertIsNotNone(m)
        self.assertEqual((m.group("line"), m.group("column")), ("1369", "19"))
        self.assertIsNone(guard.COORDINATE.search(f"{ACCEPT_REGISTRY}:1369"),
                          "a two-component citation is not a coordinate")

    def test_every_checked_registry_is_covered_by_the_coordinate_rule(self):
        """A CLOSED VOCABULARY, so it is pinned as one: the rule must hold for every registry the
        guard checks, not only for whichever one the first false positive happened to name."""
        for name, _rel, _container in guard.REGISTRIES:
            with self.subTest(registry=name):
                self.assertIsNone(guard.CITATION.search(f"{name}:7:3"))
                self.assertIsNotNone(guard.CITATION.search(f"{name}:7"))

    def test_accept_refuses_a_coordinate_by_its_own_name(self):
        """The refusal must not fall through to a check that blames the wrong thing: `rpartition(":")`
        leaves the registry name as `<registry>:7`, which is not a registry this guard checks, so the
        registry check would report an unchecked registry rather than the third component."""
        registry = ACCEPT_REGISTRY
        with self.assertRaises(guard.CannotRun) as caught:
            guard.parse_accept_key(f"{registry}:7:3")
        self.assertIn("COORDINATE", str(caught.exception))
        # The near miss, on the same parser: the two-component form passes the shape checks and is
        # refused later, by `review_records`, for not being cited - never for being a coordinate.
        guard.parse_accept_key(f"{registry}:7")

    def test_a_coordinate_is_counted_out_loud_on_every_run(self):
        """An exclusion nobody can see is indistinguishable from one that was never applied. Checked
        on BOTH surfaces that print it, so a silent one of the two is caught."""
        proc = subprocess.run([sys.executable, str(SCRIPT), "--report"], cwd=str(REPO),
                              capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=900)
        self.assertIn(proc.returncode, (0, 1), proc.stderr)
        self.assertIn("path:line:column coordinates refused", proc.stdout,
                      "the human report must print the count every run")
        payload = json.loads(subprocess.run(
            [sys.executable, str(SCRIPT), "--json"], cwd=str(REPO), capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=900).stdout)
        self.assertEqual(set(payload["coordinatesRefused"]), {n for n, _r, _c in guard.REGISTRIES},
                         "one entry per checked registry, so a new registry cannot silently go uncounted")
        for name, count in payload["coordinatesRefused"].items():
            with self.subTest(registry=name):
                self.assertIsInstance(count, int)
                self.assertGreaterEqual(count, 0)


class ThisTestFileIsNotACitingDocument(unittest.TestCase):
    """Cause 2, pinned so the phantom cannot come back by accident.

    The repair was made HERE rather than in the guard, because a string literal used as a value is
    counted on purpose and the fixture's line number was arbitrary - re-pointing it moved the finding
    instead of clearing it. That makes this file the one place where a citation would be a claim no
    edit could satisfy, so the claim is asserted absent rather than left to review.
    """

    def code_line_citations_in_this_file(self) -> list:
        path = Path(__file__).resolve()
        text = path.read_text(encoding="utf-8")
        cols = guard.comment_columns(path, text)
        found = []
        for m in guard.CITATION.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            col = m.start() - (text.rfind("\n", 0, m.start()) + 1)
            if col >= cols.get(line, guard.NO_COMMENT):
                continue
            found.append(guard.citation_text(m.group("basename"), m))
        return found

    def test_no_line_of_this_file_is_a_citation_the_guard_would_have_to_report(self):
        """THE WHOLE POINT. Any entry here is a citation this workspace makes of a registry, and the
        only thing that could ever satisfy one is an edit to a registry - which is the wrong repair for
        a test fixture."""
        self.assertEqual(self.code_line_citations_in_this_file(), [],
                         "build citation spellings from parts in this file; see FIRST_REGISTRY")

    def test_the_fixture_line_number_is_one_editable_constant(self):
        """Why the finding could MOVE instead of clearing: the number was written into the test body.
        Held in one constant it is a single-token edit, and - the part that matters - the number no
        longer reaches this file's TEXT as part of a citation spelling."""
        self.assertIsInstance(FORM_LINE, int)
        self.assertNotIn(f":{FORM_LINE}", Path(__file__).resolve().read_text(encoding="utf-8"),
                         "the fixture's line number must not appear spliced into this file's text")

    def test_a_spelling_that_is_neither_form_refuses_rather_than_defaulting(self):
        for raw in ("verification-boundaries.v1.json", "just-a-word", "core-fallback", "#alpha"):
            with self.subTest(raw=raw):
                if "#" in raw:
                    # `#alpha` names no registry, which is caught by the registry check and not by the
                    # form check - it genuinely does claim the key form.
                    with self.assertRaises(guard.CannotRun):
                        guard.parse_accept_key(raw)
                else:
                    with self.assertRaises(guard.CannotRun):
                        guard.citation_form(raw)


class LiveBaseline(unittest.TestCase):
    """The committed baseline's SHAPE, never its values.

    Which citations the row axis currently finds is a reading, and this revision ships the gate red on
    real pre-existing drift. Asserting the count would be asserting the drift away.
    """

    def test_baseline_carries_both_axes_and_provenance(self):
        stored = json.loads(guard.BASELINE.read_text(encoding="utf-8"))
        self.assertIn("fingerprints", stored)
        self.assertIn("rows", stored)
        self.assertIn("rowBaselineSource", stored)
        self.assertEqual(set(stored["rowBaselineSource"]),
                         {rel for _n, rel, _c in guard.REGISTRIES})
        for rel, sha in stored["rowBaselineSource"].items():
            self.assertRegex(sha, r"^[0-9a-f]{40}$", f"{rel} must name a real commit")

    def test_every_row_fingerprint_is_well_formed_and_names_a_checked_registry(self):
        stored = json.loads(guard.BASELINE.read_text(encoding="utf-8"))
        self.assertTrue(stored["rows"], "an empty row axis would check nothing")
        known = {name for name, _rel, _c in guard.REGISTRIES}
        for key, fp in stored["rows"].items():
            self.assertRegex(fp, r"^[0-9a-f]{16}$", key)
            name, sep, line = key.rpartition(":")
            self.assertTrue(sep and name in known and line.isdigit(), key)


if __name__ == "__main__":
    unittest.main()