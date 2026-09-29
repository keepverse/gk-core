"""Contract tests for `gk-core/scripts/cscan.py` - the shared C# comment scanner.

Four guards import this module, and it now holds FOUR policies that differ in ways which change what
a guard reports. Each policy was verified character-for-character against the PowerShell original
before it was added; this suite is where those measurements become durable, so a later edit that
quietly strengthens or weakens a scanner fails here rather than in a deploy.

The pairwise-distinctness class is the one that matters most. A "simplification" that made two
policies the same would leave every test in the other classes green while changing what a guard
catches, which is the failure this module exists to prevent.
"""
from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


cscan = _load("cscan", REPO / "scripts" / "cscan.py")

# One probe per behaviour the PowerShell originals disagreed about, with the answer each policy
# must give. Written as the CONTRACT rather than as a snapshot of an implementation.
PROBES = {
    "line_comment": "// gone\nvar a = 1;",
    "trailing_comment": "var a = 1; // note",
    "block_inline": "/* gone */ var a = 1;",
    "block_multiline": "/*\nBODY\n*/\nvar a = 1;",
    "block_star_body": "/*\n * BODY\n */\nvar a = 1;",
    "string_content": 'Log("SECRET");',
    "char_literal": "c = 'SECRET';",
    "slash_in_string": 'var u = "http://x";',
    "block_in_string": 'var w = "/* not */";',
    "dquote_escape": 'Log("a""b");',
    "back_escape_double": r'Log("a\"b");',
    "back_escape_single": r"Log('a\'); var q = 1;",
    "unterminated_block": "var g = 7; /* unterminated",
    "unterminated_string": 'var h = "unterminated',
    "plain_code": "var p = 1;",
    "empty": "",
}


class PolicyStripComments(unittest.TestCase):
    """`strip_comments`: comments removed, a block becomes ONE space, literals KEPT verbatim.

    Literals are kept because for a SQL/DAL boundary a `CREATE TABLE` inside a string is a REAL
    finding - it is raw SQL handed to a driver. A scanner that also ate literals would let a raw-SQL
    route through by writing it as a string, which is the shape the guard exists to catch.
    """

    def test_a_line_comment_is_removed_and_its_newline_kept(self) -> None:
        out = cscan.strip_comments("// gone\nvar a = 1;")
        self.assertNotIn("gone", out)
        self.assertIn("var a = 1;", out)
        self.assertEqual(out.count("\n"), 1)

    def test_a_trailing_comment_is_removed_and_the_code_survives(self) -> None:
        out = cscan.strip_comments("var a = 1; // note")
        self.assertIn("var a = 1;", out)
        self.assertNotIn("note", out)

    def test_a_become_block_becomes_one_space_so_neighbours_cannot_fuse(self) -> None:
        out = cscan.strip_comments("left/* gone */right")
        self.assertNotIn("left/*", out)
        self.assertNotIn("*/right", out)
        self.assertIn("leftright", out.replace(" ", ""))

    def test_literals_survive_verbatim(self) -> None:
        for source in ('Log("SECRET");', "c = 'SECRET';",
                       'var u = "http://x";', 'var w = "/* not */";'):
            with self.subTest(source=source):
                self.assertIn("SECRET" if "SECRET" in source else
                              ("http://x" if "http" in source else "/* not */"),
                              cscan.strip_comments(source))

    def test_a_backslash_escapes_in_both_quote_kinds(self) -> None:
        # This policy treats a backslash as an escape inside a SINGLE-quoted literal too, which is
        # why it is not a substitute for the blanking policy. Pinned so the difference is visible.
        self.assertIn("q = 1", cscan.strip_comments(r"Log('a\'); var q = 1;"))


class PolicyBlankingLiterals(unittest.TestCase):
    """`strip_comments_and_literals`: literal CONTENTS blanked, so no pattern can match inside one.

    Verified against the copy `guard-single-writer` carried. The distinguishing property is that
    `Log("p.theHealth = 0")` yields no matchable text.
    """

    def test_a_literal_contributes_no_matchable_text(self) -> None:
        out = cscan.strip_comments_and_literals('Log("p.theHealth = 0");')
        self.assertNotIn("theHealth", out)

    def test_the_quotes_survive_so_the_shape_is_still_visible(self) -> None:
        self.assertIn('""', cscan.strip_comments_and_literals('Log("x");'))

    def test_a_doubled_quote_is_one_escaped_quote(self) -> None:
        # `"a""b"` is ONE literal. Reading it as two would leave `b"` to be scanned as code.
        self.assertEqual(cscan.strip_comments_and_literals('Log("a""b"); var q = 1;'),
                         'Log(""); var q = 1;')

    def test_a_backslash_escapes_only_inside_a_double_quoted_literal(self) -> None:
        self.assertEqual(cscan.strip_comments_and_literals(r'Log("a\"b"); var q = 1;'),
                         'Log(""); var q = 1;')
        # Inside single quotes a backslash is a literal backslash, so the literal ends at the
        # second quote and `var q = 1;` is code.
        self.assertIn("q = 1", cscan.strip_comments_and_literals(r"Log('a\'); var q = 1;"))


class PolicyWholeLineOnly(unittest.TestCase):
    """`strip_whole_line_comments`: the WEAKER, deliberate policy.

    A comment that documents a rule must not look like breaking it - measured 2026-09-04, when a
    comment in ModifierOp.cs documenting the boundary failed a guard and then failed a deploy. A
    trailing comment on a line of real code is still scanned, and that narrowing is the stated
    policy rather than an oversight, so it lives under its own name instead of being upgraded.
    """

    def test_a_whole_line_comment_is_skipped(self) -> None:
        self.assertNotIn("forbidden",
                         cscan.strip_whole_line_comments("// forbidden\nclass C { }"))

    def test_a_trailing_comment_is_still_scanned(self) -> None:
        self.assertIn("forbidden",
                      cscan.strip_whole_line_comments("class C { } // forbidden"))

    def test_a_conventional_block_body_is_stripped(self) -> None:
        # Corrected by measurement: an earlier docstring claimed every multi-line body survived.
        # The `*` prefix is caught by the same test as everything else.
        self.assertNotIn("BODY", cscan.strip_whole_line_comments("/*\n * BODY\n */\nclass C { }"))

    def test_a_block_body_without_the_star_prefix_survives(self) -> None:
        self.assertIn("BODY", cscan.strip_whole_line_comments("/*\nBODY\n*/\nclass C { }"))

    def test_code_is_never_removed(self) -> None:
        body = "class C { void Go() { var x = 1; } }\n"
        self.assertEqual(cscan.strip_whole_line_comments(body), body)

    def test_comment_lines_are_blanked_not_removed(self) -> None:
        # Blanking, not removal, is what keeps `line_of` reporting the FILE's line numbers.
        source = "// header\n// another\nclass C { void Go() { TakeDamage(1); } }\n"
        stripped = cscan.strip_whole_line_comments(source)
        self.assertEqual(stripped.count("\n"), source.count("\n"))
        self.assertEqual(cscan.line_of(stripped, stripped.index("TakeDamage")), 3)


class PolicyPreservingLayout(unittest.TestCase):
    """`strip_comments_preserving_layout`: comments become SPACES, so length AND line count hold.

    This is the policy a guard wants when it reports `file.cs:412`. Under the other three a file
    with a large block comment near the top reports line numbers that do not exist in the source,
    which sends an operator to the wrong place - worse than reporting no line at all.
    """

    def test_the_length_is_unchanged(self) -> None:
        for source in PROBES.values():
            with self.subTest(source=source[:28]):
                self.assertEqual(len(cscan.strip_comments_preserving_layout(source)), len(source))

    def test_the_line_count_is_unchanged(self) -> None:
        for source in PROBES.values():
            with self.subTest(source=source[:28]):
                out = cscan.strip_comments_preserving_layout(source)
                self.assertEqual(out.count("\n"), source.count("\n"))

    def test_comment_text_is_gone(self) -> None:
        out = cscan.strip_comments_preserving_layout("var a = 1; // note\nvar b = 2;")
        self.assertNotIn("note", out)
        self.assertIn("var a = 1;", out)

    def test_a_block_becomes_spaces_of_the_same_width(self) -> None:
        out = cscan.strip_comments_preserving_layout("/*\n * three\n */\nvar d = 4;")
        self.assertNotIn("*", out)
        self.assertNotIn("three", out)
        self.assertIn("var d = 4;", out)

    def test_literals_are_kept_verbatim(self) -> None:
        # The other half of this policy, and the difference from the blanking one.
        self.assertIn("SECRET", cscan.strip_comments_preserving_layout('Log("SECRET");'))

    def test_a_block_comment_inside_a_literal_is_not_a_comment(self) -> None:
        self.assertIn("/* not */",
                      cscan.strip_comments_preserving_layout('var w = "/* not */";'))

    def test_a_slash_slash_inside_a_literal_is_not_a_comment(self) -> None:
        self.assertIn("http://x",
                      cscan.strip_comments_preserving_layout('var u = "http://x";'))

    def test_an_unterminated_block_is_consumed_to_end_of_input(self) -> None:
        # Consuming rather than looping is what keeps this from hanging on malformed input.
        out = cscan.strip_comments_preserving_layout("var g = 7; /* unterminated")
        self.assertIn("var g = 7;", out)
        self.assertNotIn("unterminated", out)

    def test_an_unterminated_literal_is_consumed_to_end_of_input(self) -> None:
        out = cscan.strip_comments_preserving_layout('var h = "unterminated')
        self.assertEqual(out, 'var h = "unterminated')

    def test_a_reported_line_number_is_the_files_own(self) -> None:
        source = "// one\n/*\n * three\n */\nvar target = 1;\n// after\n"
        stripped = cscan.strip_comments_preserving_layout(source)
        self.assertEqual(cscan.line_of(stripped, stripped.index("target")), 5)


class TheFourPoliciesAreDistinct(unittest.TestCase):
    """No two policies may agree on every probe.

    A change that quietly made two of them identical would leave every behaviour test above green
    while changing what a guard catches - the exact failure a shared scanner is supposed to make
    impossible. This class is the one that would notice.
    """

    NAMES = ("strip_comments", "strip_comments_and_literals",
             "strip_whole_line_comments", "strip_comments_preserving_layout",
             "strip_comments_and_literals_preserving_layout")

    def _outputs(self) -> dict[str, list[str]]:
        return {name: [getattr(cscan, name)(p) for p in PROBES.values()] for name in self.NAMES}

    def test_every_policy_exists(self) -> None:
        for name in self.NAMES:
            self.assertTrue(callable(getattr(cscan, name)), name)

    def test_no_two_policies_agree_on_every_probe(self) -> None:
        outputs = self._outputs()
        for i, a in enumerate(self.NAMES):
            for b in self.NAMES[i + 1:]:
                with self.subTest(a=a, b=b):
                    self.assertNotEqual(outputs[a], outputs[b],
                                        f"{a} and {b} are indistinguishable on every probe, "
                                        "so one of them is redundant or a copy has drifted")

    def test_the_literal_policies_differ_where_it_matters(self) -> None:
        # The pair a reader is most likely to confuse, on the probe that separates them.
        self.assertIn("SECRET", cscan.strip_comments('Log("SECRET");'))
        self.assertNotIn("SECRET", cscan.strip_comments_and_literals('Log("SECRET");'))

    def test_the_line_numbering_policies_differ_where_it_matters(self) -> None:
        # A multi-line block comment COLLAPSES to one space under `strip_comments`, so every line
        # after it shifts; the layout-preserving policy keeps the count and a reported line number
        # stays the file's own. This is the whole reason the fourth policy exists, so it is stated
        # as the line count rather than through `line_of`, which would hide the difference.
        source = "/*\n * a\n * b\n */\nvar t = 1;"
        self.assertLess(cscan.strip_comments(source).count("\n"), source.count("\n"))
        self.assertEqual(cscan.strip_comments_preserving_layout(source).count("\n"),
                         source.count("\n"))
        self.assertEqual(cscan.strip_comments_and_literals(source).count("\n"), 1)


class PolicyBlankingAndPreservingLayout(unittest.TestCase):
    """The FIFTH policy: literals blanked AND length/line count kept.

    `guard-battle-responsibility` needs both axes at once. Its findings carry a line number, so the
    layout must hold; and a mechanism must not be satisfied by text inside a string literal, so the
    literal must be blanked. Verified 23/23 against `lib/SourceText.ps1`'s
    `Remove-CSharpCommentsAndStrings`, with the length and line-count invariant checked on every
    probe.
    """

    def test_length_and_line_count_are_preserved(self) -> None:
        for source in PROBES.values():
            with self.subTest(source=source[:28]):
                out = cscan.strip_comments_and_literals_preserving_layout(source)
                self.assertEqual(len(out), len(source))
                self.assertEqual(out.count("\n"), source.count("\n"))

    def test_a_literal_contributes_no_matchable_text(self) -> None:
        out = cscan.strip_comments_and_literals_preserving_layout('Log("p.theHealth = 0");')
        self.assertNotIn("theHealth", out)
        self.assertEqual(len(out), len('Log("p.theHealth = 0");'))

    def test_code_survives_verbatim(self) -> None:
        body = "var a = 1; var b = 2;"
        self.assertEqual(cscan.strip_comments_and_literals_preserving_layout(body), body)

    def test_a_verbatim_string_is_understood(self) -> None:
        # `@"` is two ordinary characters to a scanner that does not know it, and the walk then
        # leaves the literal through the wrong side. A raw C# string's contents are code-shaped.
        out = cscan.strip_comments_and_literals_preserving_layout('var g = @"VERBATIM SECRET"; var h = 7;')
        self.assertNotIn("VERBATIM", out)
        self.assertIn("var h = 7;", out)

    def test_a_doubled_quote_inside_a_verbatim_string_is_blanked(self) -> None:
        # This pins the OBSERVABLE consequence, not the escape branch. The `""` escape inside a
        # verbatim string is defensive and cannot be observed through the stripped output - six
        # candidate inputs, including ones carrying a `//`, a `/* */` and a following real literal,
        # all produce identical text with the branch on or off, because the scan leaves the verbatim
        # string at the same place either way and everything after it is blanked regardless. So what
        # is asserted here is that the literal's contents do not survive, which IS observable; a
        # test that claimed to cover the branch itself would be asserting nothing.
        out = cscan.strip_comments_and_literals_preserving_layout('var i = @"has ""escaped"" quotes"; var j = 8;')
        self.assertNotIn("escaped", out)
        self.assertNotIn("has", out)
        self.assertIn("var j = 8;", out)

    def test_a_verbatim_string_may_span_lines(self) -> None:
        out = cscan.strip_comments_and_literals_preserving_layout('var k = @"spans\nlines"; var l = 9;')
        self.assertNotIn("spans", out)
        self.assertIn("var l = 9;", out)
        self.assertEqual(out.count("\n"), 1)

    def test_an_unterminated_literal_stops_at_the_newline(self) -> None:
        # The original's own comment: "an unterminated literal must not eat the rest of the file."
        # A stray apostrophe would otherwise blank every following line and turn the guard silently
        # blind - the failure this policy exists to prevent, and the OPPOSITE choice from
        # `strip_comments_preserving_layout`, whose own original consumes to end of input.
        out = cscan.strip_comments_and_literals_preserving_layout('var o = "unterminated\nvar p = 12;')
        self.assertIn("var p = 12;", out)

    def test_an_apostrophe_inside_a_comment_does_not_blank_the_file(self) -> None:
        out = cscan.strip_comments_and_literals_preserving_layout(
            "it's an apostrophe in a comment // don't\nvar ac = 20;")
        self.assertIn("var ac = 20;", out)

    def test_a_backslash_escapes_in_both_quote_kinds(self) -> None:
        self.assertNotIn("inside", cscan.strip_comments_and_literals_preserving_layout(
            r'Log("esc \" inside"); var m = 10;'))
        self.assertNotIn("inside", cscan.strip_comments_and_literals_preserving_layout(
            r"Log('esc \' inside'); var n = 11;"))


class EveryPolicyTerminates(unittest.TestCase):
    """No policy may loop forever on malformed input.

    A guard that hangs is a deploy that hangs, and the ban on unbounded work exists for exactly
    this. Each pathological shape is checked for termination by the test simply finishing.
    """

    HOSTILE = (
        "/*" * 200,
        '"' * 200,
        "'" * 200,
        "/" * 200,
        "/*" + "a" * 500,
        '"' + "\\" * 300,
        "*/" * 100,
        "\x00\x01/*\x02",
    )

    def test_each_policy_terminates_on_hostile_input(self) -> None:
        for name in TheFourPoliciesAreDistinct.NAMES:
            fn = getattr(cscan, name)
            for shape in self.HOSTILE:
                with self.subTest(policy=name, shape=shape[:12]):
                    out = fn(shape)
                    self.assertIsInstance(out, str)


class LineOfIsHonest(unittest.TestCase):
    def test_it_reports_a_one_based_line(self) -> None:
        self.assertEqual(cscan.line_of("a\nb\nc", 0), 1)
        self.assertEqual(cscan.line_of("a\nb\nc", 2), 2)
        self.assertEqual(cscan.line_of("a\nb\nc", 4), 3)

    def test_it_handles_crlf_without_miscounting(self) -> None:
        self.assertEqual(cscan.line_of("a\r\nb\r\nc", 6), 3)


if __name__ == "__main__":
    unittest.main()
