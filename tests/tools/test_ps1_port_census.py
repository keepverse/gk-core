#!/usr/bin/env python3
"""Contract tests for gk-core/scripts/ps1-port-census.py.

The load-bearing logic is `classify_landmarks`, because getting it wrong is what broke 11 tests:
a walk-up loop keyed on a guard `.ps1` is EITHER a repo-root landmark probe (the path is only
passed to `File.Exists`; the function returns the DIRECTORY) or a script finder (the function
RETURNS the path, which is then executed). Rewriting the second kind to point at a stable
landmark turns it into `powershell -File <repo>/Directory.Build.props`.

These tests use a THROWAWAY git repo rather than the real one, so they assert the classifier's
rule without depending on the current shape of this repository - a census tool that breaks when
the tree moves is a census tool nobody runs.
"""
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
TOOL_PATH = REPO_ROOT / "scripts" / "ps1-port-census.py"


def _load():
    spec = importlib.util.spec_from_file_location("ps1_port_census", TOOL_PATH)
    assert spec and spec.loader, f"{TOOL_PATH} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules["ps1_port_census"] = module
    spec.loader.exec_module(module)
    return module


census_tool = _load()


class Sandbox:
    """A throwaway git repo the tool can grep. The module resolves REPO_ROOT at import, so the
    test points it at the sandbox for the duration of the census."""

    def __init__(self, root: Path) -> None:
        self.root = root
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "t@example.com"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "T"], cwd=root, check=True)

    def write(self, rel: str, text: str) -> None:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def commit(self) -> None:
        subprocess.run(["git", "add", "-A"], cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-qm", "sandbox"], cwd=self.root, check=True)


MARKER_TEST = """using System.IO;
static class MarkerFinder {
    static string FindRepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            var scripts = Path.Combine(dir.FullName, "scripts", "guard-x.ps1");
            if (File.Exists(scripts)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.Exception("nope");
    }
}
"""

SCRIPT_TEST = """using System.IO;
static class ScriptFinder {
    static string FindScript() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            var script = Path.Combine(dir.FullName, "scripts", "guard-x.ps1");
            if (File.Exists(script)) return script;
            dir = dir.Parent;
        }
        throw new System.Exception("nope");
    }
}
"""

SHELL_TEST = """using System.Diagnostics;
using System.IO;
static class ShellRunner {
    static (int, string, string) Run(string root) {
        var script = Path.Combine(root, "scripts", "guard-x.ps1");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -File \"{script}\" -Root \"{root}\"",
        };
        return (0, "", "");
    }
}
"""

INLINE_MARKER_TEST = """using System.IO;
static class Inline {
    static string Root() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "guard-x.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.Exception("nope");
    }
}
"""


class Classification:
    """Shared sandbox plumbing: one repo carrying all three shapes plus a registry row."""

    @classmethod
    def setUpClass(cls) -> None:
        cls._tmp = tempfile.TemporaryDirectory()
        cls.box = Sandbox(Path(cls._tmp.name))
        cls.box.write("scripts/guard-x.ps1", "# guard\n")
        cls.box.write("scripts/enforcement-registry.v1.json", json.dumps(
            {"guards": {"x": {"script": "scripts/guard-x.ps1", "tier": "ci",
                              "status": "gating"}}}))
        cls.box.write("tests/MarkerTests.cs", MARKER_TEST)
        cls.box.write("tests/ScriptTests.cs", SCRIPT_TEST)
        cls.box.write("tests/InlineTests.cs", INLINE_MARKER_TEST)
        cls.box.write("tests/ShellTests.cs", SHELL_TEST)
        cls.box.write("docs/a.md", "see `scripts/guard-x.ps1`\n")
        cls.box.commit()
        original = census_tool.REPO_ROOT
        census_tool.REPO_ROOT = cls.box.root
        try:
            cls.result = census_tool.census("guard-x")
        finally:
            census_tool.REPO_ROOT = original

    @classmethod
    def tearDownClass(cls) -> None:
        cls._tmp.cleanup()


class FindsEveryCoupling(Classification, unittest.TestCase):
    def test_the_registry_row_is_found(self) -> None:
        self.assertEqual(len(self.result["couplings"]["1_registry_row"]), 1)

    def test_a_missing_boundary_row_is_reported_as_zero_not_omitted(self) -> None:
        # A coupling that reads as absent must still be PRESENT in the output, because "0" is the
        # signal that a mapping is missing. Omitting the key would hide it.
        self.assertIn("2_boundary_owner_row", self.result["couplings"])
        self.assertEqual(self.result["couplings"]["2_boundary_owner_row"], [])

    def test_tests_naming_the_tool_are_split_into_execute_and_prose(self) -> None:
        named = self.result["couplings"]["3_tests_naming_it"]
        self.assertEqual(len(named["execute"]) + len(named["prose"]), 4)

    def test_docs_prose_citations_are_counted(self) -> None:
        self.assertEqual(self.result["prose_citations_in_docs"], 1)

    def test_existence_of_source_and_port_is_reported(self) -> None:
        self.assertTrue(self.result["source_exists"])
        self.assertFalse(self.result["port_exists"])


class MarkerVersusScript(Classification, unittest.TestCase):
    """The distinction that matters, and the one that broke 11 tests when guessed wrong."""

    def test_the_two_step_repo_root_probe_is_a_MARKER(self) -> None:
        files = [e["file"] for e in self.result["couplings"]["4_landmark_sites"]["marker"]]
        self.assertIn("tests/MarkerTests.cs", files)

    def test_the_two_step_script_finder_is_a_SCRIPT(self) -> None:
        entries = self.result["couplings"]["4_landmark_sites"]["script"]
        self.assertEqual([e["file"] for e in entries], ["tests/ScriptTests.cs"])

    def test_the_inline_probe_is_a_MARKER(self) -> None:
        files = [e["file"] for e in self.result["couplings"]["4_landmark_sites"]["marker"]]
        self.assertIn("tests/InlineTests.cs", files)

    def test_exactly_three_landmark_sites_are_classified(self) -> None:
        sites = self.result["couplings"]["4_landmark_sites"]
        self.assertEqual(len(sites["marker"]) + len(sites["script"]), 3)


class RenderIsActionable(Classification, unittest.TestCase):
    def test_a_missing_boundary_row_is_called_out_in_the_text(self) -> None:
        text = census_tool.render(self.result)
        self.assertIn("none: a missing mapping is a defect", text)

    def test_a_script_role_site_is_labelled_do_not_repoint(self) -> None:
        text = census_tool.render(self.result)
        self.assertIn("SCRIPT", text)
        self.assertIn("do NOT repoint", text)

    def test_the_render_names_the_sweep_command_to_run_afterwards(self) -> None:
        self.assertIn("ps1-rename-sweep.py --map guard-x --apply", census_tool.render(self.result))


class ActionsFollowTheCensus(Classification, unittest.TestCase):
    """The action list is derived from the census, not hardcoded prose.

    This exists because the stdout/stderr rule was rediscovered on four consecutive ports at the
    cost of a red run each time. A procedure that lives where it is used gets followed; one that
    lives in a document gets skimmed. So the conditional steps are PRINTED BY THE TOOL, and these
    tests pin that the printout tracks the census rather than drifting from it.
    """

    def test_a_zero_boundary_row_produces_an_add_instruction(self) -> None:
        actions = "\n".join(census_tool.render_actions(self.result))
        self.assertIn("ADD a boundary owner row", actions)

    def test_a_present_boundary_row_produces_no_add_instruction(self) -> None:
        result = census_tool.census("guard-x")
        result["couplings"]["2_boundary_owner_row"] = [{"file": "b.json", "line": 1}]
        actions = "\n".join(census_tool.render_actions(result))
        self.assertNotIn("ADD a boundary owner row", actions)

    def test_the_stderr_instruction_appears_when_a_test_names_the_tool(self) -> None:
        actions = "\n".join(census_tool.render_actions(self.result))
        self.assertIn("move EVERY finding assertion to STDERR", actions)
        self.assertIn("keep the OK assertion on stdout", actions)

    def test_marker_and_script_sites_produce_different_instructions(self) -> None:
        actions = "\n".join(census_tool.render_actions(self.result))
        self.assertIn("MARKER site(s) to Directory.Build.props", actions)
        # The SCRIPT instruction must say the .py, not a landmark - that inversion is the exact
        # mistake that broke 11 tests, and stating it wrongly here would re-teach it.
        self.assertIn("point them at the .py once ported, NOT at a landmark", actions)

    def test_differential_testing_and_falsifying_are_always_instructed(self) -> None:
        actions = "\n".join(census_tool.render_actions(self.result))
        self.assertIn("differential-test against the .ps1 BEFORE deleting it", actions)
        self.assertIn("falsify the new test suite", actions)

    def test_the_checklist_is_referenced(self) -> None:
        self.assertIn("docs/architecture/ps1-port-checklist.md",
                      "\n".join(census_tool.render_actions(self.result)))


class ExecuteVersusProse(Classification, unittest.TestCase):
    """Coupling 3 must distinguish EXECUTE from PROSE, because they are different work.

    Measured on the real tree: `FunnelDeltaGuardTests.cs` builds the script path at :23 and
    sets `FileName = "powershell"` at :152 - 129 lines apart - so a same-line search for
    `powershell.*funnel-delta` returns nothing while the class executes the guard four times.
    The reliable signal is per FILE: it names the tool AND spawns a shell.
    """

    def test_a_file_that_names_the_tool_and_spawns_a_shell_is_execute(self) -> None:
        execute = [e["file"] for e in self.result["couplings"]["3_tests_naming_it"]["execute"]]
        self.assertIn("tests/ShellTests.cs", execute)

    def test_a_file_that_only_names_the_tool_is_prose(self) -> None:
        prose = [e["file"] for e in self.result["couplings"]["3_tests_naming_it"]["prose"]]
        self.assertIn("tests/MarkerTests.cs", prose)
        self.assertIn("tests/InlineTests.cs", prose)

    def test_the_render_lists_execute_sites(self) -> None:
        self.assertIn("EXECUTE", census_tool.render(self.result))


if __name__ == "__main__":
    unittest.main()
