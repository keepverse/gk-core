#!/usr/bin/env python3
"""Contract tests for gk-core/scripts/guard-dal.py and its shared scanner gk-core/scripts/cscan.py.

These pin the CONTRACT and the REGRESSION that justified the port:

  * SQL mentioned only in comments is NOT a finding. The PowerShell original flagged three such
    lines on the same fixture (a trailing `//` comment and a multi-line `/* */` body whose lines do
    not begin with `*`). That is the defect this port exists to remove, and it is the assertion
    most likely to be quietly reintroduced by a "simplification" of the scanner.
  * Raw SQL inside a string literal IS a finding, because it is SQL handed to a driver. A scanner
    that also ate string literals would pass this - which is why both directions are asserted.

Nothing here asserts a line count, a message body, or a file population; those rot and guard
nothing. The repo's own standard is that a guardrail pins the contract and closed enums.
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
SCRIPTS = REPO_ROOT / "scripts"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


cscan = _load("cscan", SCRIPTS / "cscan.py")
dal = _load("guard_dal", SCRIPTS / "guard-dal.py")


def _run(argv: list[str]) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = dal.main(argv)
    return code, out.getvalue(), err.getvalue()


class Fixture:
    """A minimal repo: a data project that defines the boundary, plus caller files."""

    def __init__(self, root: Path) -> None:
        self.root = root
        (root / "src" / "FusionRpg.Data").mkdir(parents=True, exist_ok=True)
        (root / "src" / "Server").mkdir(parents=True, exist_ok=True)
        self.write("src/FusionRpg.Data/Store.cs",
                   "namespace FusionRpg.Data { public class Store { } }")

    def write(self, rel: str, text: str) -> Path:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path


class CommentHandlingIsNotAFinding(unittest.TestCase):
    """THE regression. SQL in a comment is inert; flagging it teaches people to stop writing the
    explanation the repo's own guard comment asks for."""

    def test_a_trailing_line_comment_is_not_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/T.cs",
                     "namespace S;\npublic class T { public int X = 1; } // CREATE TABLE was here\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(result["findings"], [])

    def test_a_block_comment_body_is_not_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/B.cs",
                     "namespace S;\n/* the body lines do NOT start with a star:\n"
                     "   PRAGMA foreign_keys;\n   INSERT INTO x VALUES (1);\n*/\npublic class B { }\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(result["findings"], [])

    def test_an_xml_doc_comment_is_not_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/D.cs",
                     "namespace S;\n/// <summary>BEGIN IMMEDIATE is not used here.</summary>\n"
                     "public class D { }\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(result["findings"], [])


class RawSqlInAStringIsAFinding(unittest.TestCase):
    """The other direction. A string literal is code the driver executes."""

    def test_ddl_in_a_string_literal_is_found_with_a_line_number(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/S.cs",
                     "namespace S;\npublic class S { public string D = \"CREATE TABLE t (id int)\"; }\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(len(result["findings"]), 1)
        finding = result["findings"][0]
        self.assertEqual(finding["file"], "src/Server/S.cs")
        self.assertEqual(finding["line"], 2)
        self.assertEqual(result["verdict"], "FAIL")

    def test_the_driver_type_is_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/C.cs",
                     "using Microsoft.Data.Sqlite;\nnamespace S { public class C { } }\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(len(result["findings"]), 1)

    def test_sql_inside_the_data_project_is_never_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Data/Legit.cs",
                     "namespace FusionRpg.Data { public class L { public string S = \"CREATE TABLE t\"; } }\n")
            result = dal.scan(fx.root, [])
        self.assertEqual(result["verdict"], "OK")


class ProjectReferences(unittest.TestCase):
    """A PackageReference is the other half of the boundary: the driver can arrive without any C#."""

    def test_a_sqlite_package_reference_outside_data_is_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/Server.csproj",
                     '<Project><ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" />'
                     '</ItemGroup></Project>')
            result = dal.scan(fx.root, [])
        self.assertEqual(result["verdict"], "FAIL")
        self.assertTrue(any(f["file"].endswith(".csproj") for f in result["findings"]))

    def test_the_same_reference_inside_data_is_clean(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Data/Data.csproj",
                     '<Project><ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" />'
                     '</ItemGroup></Project>')
            result = dal.scan(fx.root, [])
        self.assertEqual(result["verdict"], "OK")


class GeneratedOutputIsNotProductSource(unittest.TestCase):
    def test_bin_and_obj_are_skipped(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/bin/Release/Generated.g.cs",
                     'public class G { public string S = "CREATE TABLE t"; }')
            fx.write("src/Server/obj/Debug/Other.g.cs",
                     'public class O { public string S = "INSERT INTO t VALUES (1)"; }')
            result = dal.scan(fx.root, [])
        self.assertEqual(result["verdict"], "OK")


class ExemptionsAreVisible(unittest.TestCase):
    """The original matched `-AllowlistFiles` on file NAME, so one entry silently exempted EVERY
    same-named file. The capability stays; the breadth is now reported."""

    def test_a_repo_relative_path_exempts_only_that_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/Dup.cs", 'public class D { public string S = "CREATE TABLE t"; }')
            fx.write("src/Other/Dup.cs", 'public class D2 { public string S = "CREATE TABLE t"; }')
            result = dal.scan(fx.root, ["src/Server/Dup.cs"])
        self.assertEqual([f["file"] for f in result["findings"]], ["src/Other/Dup.cs"])

    def test_a_bare_filename_exempts_every_same_named_file_and_says_so(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/Dup.cs", 'public class D { public string S = "CREATE TABLE t"; }')
            fx.write("src/Other/Dup.cs", 'public class D2 { public string S = "CREATE TABLE t"; }')
            result = dal.scan(fx.root, ["Dup.cs"])
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["exemptions"], [{"spec": "Dup.cs", "mode": "basename"}])
        self.assertGreater(result["exempted_files"], 0)

    def test_the_exemption_mode_is_reported_for_each_spec(self) -> None:
        self.assertEqual(dal._exemption_mode("src/a/B.cs"), "path")
        self.assertEqual(dal._exemption_mode("src\\a\\B.cs"), "path")
        self.assertEqual(dal._exemption_mode("B.cs"), "basename")


class Refusals(unittest.TestCase):
    """A guard that cannot run must say so and exit non-zero. Reporting 'clean' because the
    boundary is missing is the false green this whole program exists to remove."""

    def test_a_missing_data_project_refuses_rather_than_reporting_clean(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "src").mkdir()
            code, _out, err = _run(["--root", tmp])
        self.assertEqual(code, 64)
        self.assertIn("DATA-PROJECT-MISSING", err)

    def test_a_missing_src_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, _out, err = _run(["--root", tmp])
        self.assertEqual(code, 64)
        self.assertIn("DATA-PROJECT-MISSING", err)

    def test_a_refusal_still_emits_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, out, _err = _run(["--root", tmp, "--json"])
        self.assertEqual(code, 64)
        self.assertEqual(json.loads(out)["verdict"], "REFUSED")


class ExitVocabulary(unittest.TestCase):
    """0 clean, 1 a finding, 64 a refusal - three distinguishable outcomes, not one non-zero."""

    def test_clean_exits_zero(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            code, out, _err = _run(["--root", str(fx.root), "--json"])
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["verdict"], "OK")

    def test_a_finding_exits_one(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/Server/X.cs", 'public class X { public string S = "PRAGMA foo"; }')
            code, _out, err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 1)
        self.assertIn("DAL GUARD FAILED", err)


class ScannerContract(unittest.TestCase):
    """gk-core/scripts/cscan.py is shared, so its own edge cases are pinned here rather than left to
    whichever guard happens to hit them first."""

    def test_a_slash_slash_inside_a_string_is_not_a_comment(self) -> None:
        self.assertEqual(cscan.strip_comments('var u = "http://x"; // gone').count("//"), 1)
        self.assertIn("http://x", cscan.strip_comments('var u = "http://x"; // gone'))

    def test_a_block_open_inside_a_string_is_not_a_comment(self) -> None:
        code = cscan.strip_comments('var s = "/* not a comment */"; int x = 1;')
        self.assertIn("int x = 1;", code)
        self.assertIn("/* not a comment */", code)

    def test_an_escaped_quote_does_not_end_the_literal(self) -> None:
        code = cscan.strip_comments('var s = "a\\"b"; // tail')
        self.assertIn('"a\\"b"', code)
        self.assertNotIn("tail", code)

    def test_a_block_comment_becomes_a_space_so_neighbours_cannot_fuse(self) -> None:
        self.assertEqual(cscan.strip_comments("PRAG/* x */MA").count("PRAGMA"), 0)

    def test_an_unterminated_block_comment_terminates_instead_of_looping(self) -> None:
        self.assertEqual(cscan.strip_comments("int a = 1; /* never closed"), "int a = 1;  ")

    def test_line_of_reports_one_based_lines(self) -> None:
        text = "a\nbb\nccc"
        self.assertEqual(cscan.line_of(text, 0), 1)
        self.assertEqual(cscan.line_of(text, 2), 2)
        self.assertEqual(cscan.line_of(text, 6), 3)


if __name__ == "__main__":
    unittest.main()

