"""Regression tests for `gk-core/scripts/lib/verification_boundaries.py`'s `pattern_match`.

One test, and it exists because the differential behind the `guard-verification-boundaries` port found
a LIVE defect in this function rather than in the port:

    `path_tail` was taken from `lowered` - the LOWERCASED path - while `file_part` came straight out of
    the pattern, and the regex was compiled without `re.IGNORECASE`. So a pattern whose final segment
    carries any uppercase letter produced a regex that could never match the lowercased tail.

Measured on this repository's own registry: **14 owner patterns** are affected, and two files were
provably mis-resolved - `gk-core/src/FusionRpg.Contracts/NarrativeTextDtos.cs` and
`gk-core/tests/FusionRpg.Guard.Tests/NarrativeDoctrineReadingGuardTests.cs`. `wildcard_match` returned True for
both while `pattern_match` returned False, which is the cheapest possible way to see it: the two
functions are supposed to agree about a legal final-segment wildcard, and when they disagree one of
them is wrong.

The affected files fell to a WIDER FALLBACK OWNER rather than to nothing, so the failure mode is the
worst kind: `verify-change.py` reported a plausible, narrower-looking answer. A change to
`NarrativeDoctrineReadingGuardTests.cs` was verified as a whole-module run instead of the owner's
focused selection, and nothing anywhere said so.

The other three branches of `pattern_match` never had this bug, because they compare with `.lower()` on
BOTH sides. That asymmetry is why reading the whole function does not find it, and why the fix has to be
asserted per branch rather than trusted to a single case-fold test.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

_spec = importlib.util.spec_from_file_location(
    "lib_verification_boundaries", REPO / "scripts" / "lib" / "verification_boundaries.py")
vb = importlib.util.module_from_spec(_spec)
sys.modules["lib_verification_boundaries"] = vb
_spec.loader.exec_module(vb)


class TheFinalSegmentWildcardBranch(unittest.TestCase):
    """`dir/name*.ext` - a `*` in the FINAL segment only, matching `[^/]*`, never crossing a `/`."""

    def test_an_UPPERCASE_pattern_segment_matches_a_LOWERCASE_path_and_VICE_VERSA(self) -> None:
        # THE DEFECT. Both directions, because a fix that lowercases only one side passes one of them.
        for pattern, path in [
            ("src/FusionRpg.Contracts/NarrativeText*.cs", "src/FusionRpg.Contracts/NarrativeTextDtos.cs"),
            ("src/FusionRpg.Contracts/NarrativeText*.cs", "src/fusionrpg.contracts/narrativetextdtos.cs"),
            ("src/FusionRpg.Data/Sqlite/RpgStore.Story*.cs", "src/FusionRpg.Data/Sqlite/RpgStore.Story.cs"),
            ("src/FusionRpg.Server/DelveEvent*.cs", "src/fusionrpg.server/delveeventhandler.cs"),
            ("tests/FusionRpg.Guard.Tests/Storylet*.cs", "tests/FusionRpg.Guard.Tests/StoryletReader.cs"),
        ]:
            with self.subTest(pattern=pattern, path=path):
                self.assertTrue(vb.pattern_match(path, pattern),
                                f"{path!r} must match {pattern!r}")

    def test_it_agrees_with_the_bare_wildcard_translator(self) -> None:
        # The cheapest possible detector, and the one that found it: the two functions answer the same
        # question about a legal final-segment wildcard, so a disagreement means one of them is wrong.
        # Asserted over a spread of casings, not over the two files that happened to be affected.
        cases = [
            ("src/A/Narrative*.cs", "src/A/Narrative.cs"),
            ("src/A/Narrative*.cs", "src/A/NarrativeDtos.cs"),
            ("src/A/RpgStore.Story*.cs", "src/A/RpgStore.StoryWorld.cs"),
            ("tests/T.Guard/Storylet*.cs", "tests/T.Guard/StoryletX.cs"),
            ("src/B/lower*.cs", "src/B/LOWERCASED.cs"),
        ]
        for pattern, path in cases:
            with self.subTest(pattern=pattern, path=path):
                self.assertTrue(vb.pattern_match(path, pattern),
                                "pattern_match disagrees with wildcard_match, which is the defect")

    def test_the_wildcard_still_never_crosses_a_separator(self) -> None:
        # The fix must not have widened the branch. A `*` in the final segment matches `[^/]*`, so a
        # deeper path is NOT a match - and that is the property the C4 last-segment grammar rests on.
        self.assertFalse(vb.pattern_match("src/A/Name.cs/Deep.cs", "src/A/Name*.cs"))
        self.assertFalse(vb.pattern_match("src/B/Name.cs", "src/A/Name*.cs"))

    def test_a_star_matches_the_empty_string(self) -> None:
        # `*` is zero-or-more, so `Name*.cs` matches `Name.cs` and `NameDtos.cs` alike.
        self.assertTrue(vb.pattern_match("src/A/Name.cs", "src/A/Name*.cs"))
        self.assertTrue(vb.pattern_match("src/A/Name.cs", "src/A/Na*.cs"))


class TheOtherBranchesAreUnaffected(unittest.TestCase):
    """The three branches that compare with `.lower()` on BOTH sides never had this bug. Asserted so a
    future refactor that unifies the branches cannot silently reintroduce it in the wrong direction."""

    def test_the_prefix_branch_folds_case(self) -> None:
        self.assertTrue(vb.pattern_match("src/FusionRpg.Core/A.cs", "src/FusionRpg.Core/**"))
        self.assertTrue(vb.pattern_match("SRC/FUSIONRPG.CORE/A.cs", "src/FusionRpg.Core/**"))

    def test_the_exact_branch_folds_case(self) -> None:
        self.assertTrue(vb.pattern_match("scripts/Verify-Change.PY", "scripts/verify-change.py"))

    def test_the_markdown_branch_folds_case(self) -> None:
        # `.agents/**/*.md` IS one of the four legal shapes. The first version of this test used
        # `.claude/**/SKILL.md`, which is not: it ends in `/**/SKILL.md`, so it falls into the
        # final-segment branch with a `file_part` of `SKILL.md` that contains no `*`, and the tail
        # carries a `/`. The test then measured my own invented pattern rather than the branch, and I
        # read the failure as an off-by-one in the slice. There is no off-by-one: `.claude/**/*.md` is
        # 15 characters, so [:-7] is the first 8, `.claude/`, which is correct. The retraction is left
        # in this comment because a wrong finding that a reader cannot see was retracted is a wrong
        # finding that gets re-derived.
        for root in (".claude", ".agents", ".commandcode", ".kilo"):
            with self.subTest(root=root):
                self.assertTrue(vb.pattern_match(f"{root}/skills/thing/SKILL.MD", f"{root}/**/*.md"))
                self.assertTrue(vb.pattern_match(f"{root}/SKILL.md", f"{root}/**/*.md"))
                # Still markdown-only: a script beside it must not be swept in.
                self.assertFalse(vb.pattern_match(f"{root}/skills/thing/run.py", f"{root}/**/*.md"))

    def test_an_illegal_shape_is_rejected_by_the_GRAMMAR_not_silently_matched(self) -> None:
        # `.claude/**/SKILL.md` is the shape that misled the first version of this test. The grammar
        # guard is what refuses it, and it is the layer that has to catch it - `pattern_match` on its
        # own will happily evaluate a shape it does not recognise.
        self.assertFalse(vb.valid_pattern_grammar(".claude/**/SKILL.md"))
        for good in (".claude/**/*.md", "src/A/**", "src/A/Name*.cs", "scripts/x.py"):
            with self.subTest(good=good):
                self.assertTrue(vb.valid_pattern_grammar(good))


if __name__ == "__main__":
    unittest.main()
