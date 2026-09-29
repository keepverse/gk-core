#!/usr/bin/env python3
"""Contract tests for gk-core/scripts/guard-secondary-no-unity.py.

Pins the CONTRACT and, deliberately, the two decisions this port recorded rather than resolved:

  * **Raw-text scanning is preserved.** A comment naming a banned symbol IS a finding. That is a
    known false-positive source, and `cscan.strip_comments` would remove it - but stripping would
    widen what the guard permits, which is a contract change and not a port. The assertion below
    exists so that IF the owner later rules the other way, the change is a deliberate edit to a
    named test rather than a silent drift.
  * **The in-scope pattern is preserved verbatim**, so the test pins what it actually matches
    rather than what it looks like it should match. It is currently redundant on this tree (every
    file it selects is already in the plugin directory), and the test says so.

No line count, message body or population is asserted; those rot and guard nothing.
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
GUARD_PATH = REPO_ROOT / "scripts" / "guard-secondary-no-unity.py"


def _load():
    spec = importlib.util.spec_from_file_location("guard_secondary_no_unity", GUARD_PATH)
    assert spec and spec.loader, f"{GUARD_PATH} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules["guard_secondary_no_unity"] = module
    spec.loader.exec_module(module)
    return module


guard = _load()
PLUGINS = "src/FusionRpg.Core/Effects/Plugins"


def _run(argv: list[str]) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = guard.main(argv)
    return code, out.getvalue(), err.getvalue()


class Fixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        (root / PLUGINS).mkdir(parents=True, exist_ok=True)
        (root / "src" / "FusionRpg.Core").mkdir(parents=True, exist_ok=True)

    def write(self, rel: str, text: str) -> Path:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def plugin(self, name: str, text: str) -> Path:
        return self.write(f"{PLUGINS}/{name}", text)


class VerdictStringsArePreserved(unittest.TestCase):
    """The Guard suite asserts on these with Assert.Contains, so they are a contract, not prose."""

    def test_the_ok_string_is_byte_preserved(self) -> None:
        self.assertEqual(
            guard.VERDICT_OK,
            "SECONDARY NO-UNITY GUARD OK — plugins Grant/Withdraw only")

    def test_the_failure_string_is_byte_preserved(self) -> None:
        self.assertEqual(
            guard.VERDICT_FAILED,
            "SECONDARY NO-UNITY GUARD FAILED — Unity/Status/Writer refs in Secondary plugins:")


class BannedSymbols(unittest.TestCase):
    """Each banned symbol is part of the closed set this guard enforces."""

    def test_every_banned_pattern_is_present_in_the_contract(self) -> None:
        self.assertEqual(
            [raw for raw, _ in guard.BANNED_PATTERNS],
            ["UnityEngine", "HarmonyLib", "StatusExecutor", "EntityStatWriter",
             "FindObjectsOfType", "CreateZombie"],
        )

    def test_a_clean_plugin_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("Good.cs", "namespace P; public class Good { public void Grant() { } }")
            result = guard.scan(fx.root)
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["findings"], [])

    def test_each_banned_symbol_in_a_plugin_is_a_finding(self) -> None:
        for raw, _pattern in guard.BANNED_PATTERNS:
            with self.subTest(pattern=raw), tempfile.TemporaryDirectory() as tmp:
                fx = Fixture(Path(tmp))
                fx.plugin("Bad.cs", f"namespace P; public class Bad {{ void M() {{ var x = {raw}; }} }}")
                result = guard.scan(fx.root)
                self.assertEqual(result["verdict"], "FAIL")
                self.assertEqual(result["findings"][0]["pattern"], raw)
                self.assertEqual(result["findings"][0]["line"], 1)

    def test_a_finding_carries_a_line_number(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("Bad.cs", "namespace P;\npublic class Bad {\n  void M() { var e = UnityEngine.Object; }\n}\n")
            result = guard.scan(fx.root)
        self.assertEqual(result["findings"][0]["line"], 3)


class RawTextScanningIsPreserved(unittest.TestCase):
    """THE recorded decision. This guard does not strip comments, so a comment naming a banned
    symbol is a finding. The assertion is here so a future ruling changes a named test."""

    def test_a_banned_symbol_in_a_comment_is_still_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("Commented.cs",
                      "namespace P;\n// TODO: this must never touch UnityEngine\n"
                      "public class Commented { }")
            result = guard.scan(fx.root)
        self.assertEqual(result["verdict"], "FAIL")
        self.assertEqual(result["findings"][0]["pattern"], "UnityEngine")

    def test_the_shared_scanner_would_have_suppressed_it(self) -> None:
        """Why this is a known rough edge rather than an accident: the fix exists and is one call."""
        sys.path.insert(0, str(GUARD_PATH.parent))
        import cscan
        text = "// TODO: this must never touch UnityEngine\npublic class C { }"
        self.assertIn("UnityEngine", text)
        self.assertNotIn("UnityEngine", cscan.strip_comments(text))


class ScopeRules(unittest.TestCase):
    """Two ways in: the plugin directory, or a file that declares the interface anywhere in src/."""

    def test_a_file_in_the_plugin_directory_is_in_scope(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("A.cs", "public class A { }")
            result = guard.scan(fx.root)
        self.assertEqual(result["scope_reasons"], ["plugin-directory"])
        self.assertEqual(result["files_scanned"], 1)

    def test_a_file_outside_the_directory_is_in_scope_when_it_declares_the_interface(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Core/Elsewhere/Stray.cs",
                     "namespace P; public class Stray : IEffectGrantPlugin { }")
            result = guard.scan(fx.root)
        self.assertIn("declares-IEffectGrantPlugin", result["scope_reasons"])
        self.assertEqual(result["files_scanned"], 1)

    def test_a_file_outside_the_directory_without_the_interface_is_not_scanned(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Core/Elsewhere/Unrelated.cs",
                     "namespace P; public class Unrelated { void M() { var e = UnityEngine.Object; } }")
            result = guard.scan(fx.root)
        self.assertEqual(result["files_scanned"], 0)
        self.assertEqual(result["verdict"], "OK")

    def test_the_two_passes_deduplicate(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("Both.cs", "namespace P; public class Both : IEffectGrantPlugin { }")
            result = guard.scan(fx.root)
        self.assertEqual(result["files_scanned"], 1)
        self.assertEqual(result["scope_reasons"], ["plugin-directory"])

    def test_generated_output_under_bin_or_obj_is_not_scanned(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("bin/Obj.cs", "public class B { void M() { var e = UnityEngine.Object; } }")
            fx.plugin("obj/Debug/O.cs", "public class O { var e = UnityEngine.Object; }")
            fx.plugin("Generated/Deep/obj/X.cs", "public class X { var e = UnityEngine.Object; }")
            result = guard.scan(fx.root)
        self.assertEqual(result["files_scanned"], 0)
        self.assertEqual(result["verdict"], "OK")

    def test_a_g_cs_file_in_the_plugin_directory_IS_scanned(self) -> None:
        """The skip is by PATH SEGMENT (bin/obj), never by file extension. Asserted because
        "generated" invites the assumption that `*.g.cs` is excluded, and it is not - a `.g.cs`
        inside the plugin directory is product source as far as this guard is concerned."""
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("Gen.g.cs", "public class G { void M() { var e = UnityEngine.Object; } }")
            result = guard.scan(fx.root)
        self.assertEqual(result["files_scanned"], 1)
        self.assertEqual(result["verdict"], "FAIL")


class CoreProjectFile(unittest.TestCase):
    def test_a_unity_reference_in_the_core_project_is_a_finding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Core/FusionRpg.Core.csproj",
                     '<Project><ItemGroup><PackageReference Include="UnityEngine" />'
                     '</ItemGroup></Project>')
            result = guard.scan(fx.root)
        self.assertEqual(result["verdict"], "FAIL")
        self.assertEqual(result["findings"][0]["file"], "src/FusionRpg.Core/FusionRpg.Core.csproj")
        self.assertEqual(result["findings"][0]["scope_reason"], "core-project-file")

    def test_a_clean_core_project_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.write("src/FusionRpg.Core/FusionRpg.Core.csproj", "<Project />")
            result = guard.scan(fx.root)
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["projects_scanned"], 1)


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
    def test_clean_exits_zero_and_prints_the_preserved_string(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("A.cs", "public class A { }")
            code, out, _err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 0)
        self.assertIn(guard.VERDICT_OK, out)

    def test_a_finding_exits_one_and_prints_the_preserved_string(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp))
            fx.plugin("B.cs", "public class B { var e = UnityEngine.Object; }")
            code, _out, err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 1)
        self.assertIn(guard.VERDICT_FAILED, err)


class TheRealTree(unittest.TestCase):
    """The guard must be clean against the actual repository - the property it exists to hold."""

    def test_the_repository_is_clean(self) -> None:
        result = guard.scan(REPO_ROOT)
        self.assertEqual(
            result["findings"], [],
            f"the ported guard disagrees with the tree: {result['findings']}")


if __name__ == "__main__":
    unittest.main()
