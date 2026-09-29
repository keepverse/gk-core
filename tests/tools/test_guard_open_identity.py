#!/usr/bin/env python3
r"""Contract tests for gk-core/scripts/guard-open-identity.py.

Structured around the properties a careless rewrite loses:

  * I1 is NARROW on purpose - `*Id` outside Commanders/ and World/ is legitimate (AtomKind ids and
    the like), so the scope is the thing under test, not an implementation detail.
  * I2 has THREE detection shapes (cast before, expression subject, statement subject) and all
    three are separately asserted, because a port that keeps two of them still looks correct.
  * I2 is TEXTUAL, not a type-checker - the spec is explicit that it cannot see a string switch and
    was never meant to be the only defence. A test pins that it does NOT become a parser.
  * Comment bodies are NOT code. The original's line-prefix filter flagged a `switch` inside a
    multi-line block comment; that false positive is asserted gone, and the test that proves it
    builds the exact shape (a typed declaration AND a switch, both inside the comment).

No population is asserted: the shipped corpus may gain closed vocabularies at any time.
"""
from __future__ import annotations

import importlib.util
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GUARD_PATH = REPO_ROOT / "scripts" / "guard-open-identity.py"
sys.path.insert(0, str(GUARD_PATH.parent))


def _load():
    spec = importlib.util.spec_from_file_location("guard_open_identity", GUARD_PATH)
    assert spec and spec.loader, f"{GUARD_PATH} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules["guard_open_identity"] = module
    spec.loader.exec_module(module)
    return module


guard = _load()


def _run(argv):
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = guard.main(argv)
    return code, out.getvalue(), err.getvalue()


class Fixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        for parts in (("FusionRpg.Core", "Commanders"), ("FusionRpg.Core", "World"),
                      ("FusionRpg.Core", "Combat")):
            (root / "src" / parts[0] / parts[1]).mkdir(parents=True, exist_ok=True)

    def write(self, rel: str, text: str) -> "Fixture":
        path = self.root / "src" / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return self

    def scan(self) -> dict:
        return guard.scan(self.root)


def scan_with(build) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        return build(Fixture(Path(tmp))).scan()


class VerdictStringsArePreserved(unittest.TestCase):
    def test_the_ok_string_is_byte_preserved(self) -> None:
        self.assertEqual(
            guard.VERDICT_OK,
            "OPEN-IDENTITY GUARD OK — no *Id/*Ids enum under Commanders/World, "
            "no switch over EmpireId/CommanderRef")

    def test_the_failure_banner_is_byte_preserved(self) -> None:
        self.assertEqual(guard.VERDICT_FAILED, "OPEN-IDENTITY GUARD FAILED:")


class I1IsNarrow(unittest.TestCase):
    """A repo-wide 'no enum ending in Id' rule would hit legitimate closed vocabularies."""

    def test_an_Id_enum_under_Commanders_is_a_finding(self) -> None:
        result = scan_with(lambda f: f.write("FusionRpg.Core/Commanders/A.cs",
                                            "namespace C { public enum CommanderId { A } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I1"])

    def test_an_Ids_enum_under_World_is_a_finding(self) -> None:
        result = scan_with(lambda f: f.write("FusionRpg.Core/World/B.cs",
                                            "namespace W { public enum EmpireIds { A } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I1"])

    def test_an_Id_enum_OUTSIDE_the_two_scoped_dirs_is_clean(self) -> None:
        result = scan_with(lambda f: f.write("FusionRpg.Core/Combat/C.cs",
                                            "namespace X { public enum AtomId { A } }"))
        self.assertEqual(result["findings"], [])

    def test_a_similarly_named_enum_not_ending_in_Id_is_clean(self) -> None:
        result = scan_with(lambda f: f.write("FusionRpg.Core/Commanders/D.cs",
                                            "namespace C { public enum Identity { A } }"))
        self.assertEqual(result["findings"], [])

    def test_a_missing_scoped_dir_is_not_a_finding(self) -> None:
        # The guard is narrow by design; an absent directory must not read as a violation.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "src" / "FusionRpg.Core" / "Commanders").mkdir(parents=True)
            (root / "src" / "FusionRpg.Core" / "Commanders" / "A.cs").write_text(
                "namespace C { public enum Ok { A } }", encoding="utf-8")
            result = guard.scan(root)
        self.assertEqual(result["findings"], [])


class I2HasThreeShapes(unittest.TestCase):
    """All three are asserted separately: keeping two of three still looks correct."""

    def test_a_cast_onto_the_switch_subject_is_caught(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C { public class A { public int G(int x) "
            "{ return (EmpireId)x switch { _ => 0 }; } } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I2"])
        self.assertIn("cast directly", result["findings"][0]["message"])

    def test_a_switch_expression_over_a_typed_local_is_caught(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C { public class A { public string G(EmpireId id) "
            "{ return id switch { _ => \"\" }; } } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I2"])
        self.assertEqual(result["findings"][0]["name"], "id")

    def test_a_switch_statement_over_a_typed_parameter_is_caught(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C { public class A { public void G(CommanderRef r) "
            "{ switch (r) { default: break; } } } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I2"])
        self.assertEqual(result["findings"][0]["name"], "r")

    def test_a_switch_over_an_unrelated_value_is_clean(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C { public class A { public int G(int x) "
            "{ return x switch { 1 => 2, _ => 0 }; } } }"))
        self.assertEqual(result["findings"], [])

    def test_i2_scans_all_of_src_not_only_the_two_scoped_dirs(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Combat/A.cs",
            "namespace X { public class A { public string G(EmpireId id) "
            "{ return id switch { _ => \"\" }; } } }"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I2"])


class I2IsTextualNotAParser(unittest.TestCase):
    """The spec is explicit: this guard cannot see a string switch and was never the only defence.
    A port that quietly became a parser would change the contract, so pin that it did not."""

    def test_a_switch_over_a_string_literal_is_not_attempted(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            'namespace C { public class A { public string G() '
            '{ var s = "EmpireId"; return s switch { _ => "" }; } } }'))
        self.assertEqual(result["findings"], [])

    def test_the_scan_window_is_named_and_positive(self) -> None:
        self.assertGreater(guard.SWITCH_WINDOW, 0)


class CommentBodiesAreNotCode(unittest.TestCase):
    """The fix the port made, asserted so it cannot be undone by a 'simplification'."""

    def test_a_typed_declaration_and_switch_inside_a_block_comment_are_not_findings(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C {\n"
            "  /* Historical note, body lines deliberately not star-prefixed:\n"
            "     EmpireId legacyId;\n"
            "     switch (legacyId) { default: break; }\n"
            "  */\n"
            "  public class A { public enum Fine { A } }\n"
            "}\n"))
        self.assertEqual(result["findings"], [],
                         "a switch inside a block comment is not a violation")

    def test_a_trailing_line_comment_naming_the_types_is_not_a_finding(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C { public class A { public enum Fine { A } } }\n"
            "// this used to be an EmpireId switch\n"))
        self.assertEqual(result["findings"], [])

    def test_real_code_still_fires_when_a_comment_is_present_too(self) -> None:
        result = scan_with(lambda f: f.write(
            "FusionRpg.Core/Commanders/A.cs",
            "namespace C {\n"
            "  // EmpireId appears in this comment\n"
            "  public class A { public string G(EmpireId id) "
            "{ return id switch { _ => \"\" }; } }\n"
            "}\n"))
        self.assertEqual([x["invariant"] for x in result["findings"]], ["I2"])


class Deduplication(unittest.TestCase):
    """The original used Select-Object -Unique; two identical switches are one problem."""

    def test_duplicate_findings_are_collapsed(self) -> None:
        body = ("namespace C { public class A { public string G(EmpireId id) "
                "{ return id switch { _ => \"\" }; } "
                "public string H(EmpireId id) { return id switch { _ => \"\" }; } } }")
        result = scan_with(lambda f: f.write("FusionRpg.Core/Commanders/A.cs", body))
        self.assertEqual(len(result["findings"]), 1)


class Refusals(unittest.TestCase):
    def test_a_missing_src_refuses_rather_than_reporting_clean(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, _out, err = _run(["--root", tmp])
        self.assertEqual(code, 64)
        self.assertIn("SRC-MISSING", err)

    def test_a_refusal_still_emits_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, out, _err = _run(["--root", tmp, "--json"])
        self.assertEqual(code, 64)
        self.assertEqual(json.loads(out)["verdict"], "REFUSED")


class ExitVocabulary(unittest.TestCase):
    def test_clean_exits_zero(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp)).write("FusionRpg.Core/Commanders/A.cs",
                                          "namespace C { public enum Fine { A } }")
            code, out, _err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 0)
        self.assertIn(guard.VERDICT_OK, out)

    def test_a_finding_exits_one_and_names_the_invariant(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp)).write("FusionRpg.Core/Commanders/A.cs",
                                          "namespace C { public enum CommanderId { A } }")
            code, _out, err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 1)
        self.assertIn(guard.VERDICT_FAILED, err)
        self.assertIn("I1", err)


class TheRealTree(unittest.TestCase):
    def test_the_shipped_corpus_is_compliant(self) -> None:
        result = guard.scan(REPO_ROOT)
        self.assertEqual(result["findings"], [], f"ported guard disagrees: {result['findings']}")


if __name__ == "__main__":
    unittest.main()
