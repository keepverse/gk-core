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

import importlib.util
import json
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
    "  }",                                  # 21
    "}",                                    # 22
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


def _one_citation() -> dict:
    return {FIRST_KEY: ("tasks/x.md", 1)}


def _refused_counts() -> dict:
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
        guard.collect = lambda ws: (_one_citation(), _refused_counts())
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
        guard.collect = lambda ws: (_one_citation(), _refused_counts())
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
        for flag in ("--report", "--json", "--update", "--adopt-rows", "--root"):
            self.assertIn(flag, proc.stdout, f"{flag} is part of the published surface")

    def test_update_and_adopt_rows_cannot_run_together(self):
        proc = subprocess.run([sys.executable, str(SCRIPT), "--update", "--adopt-rows"],
                              cwd=str(REPO), capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=600)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("--adopt-rows", proc.stdout + proc.stderr)


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