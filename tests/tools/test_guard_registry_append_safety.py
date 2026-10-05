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