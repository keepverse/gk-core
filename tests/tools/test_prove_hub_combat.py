"""Contract tests for `gk-core/scripts/prove_hub_combat.py`.

A PROOF, so the contract is about refusing rather than passing, and about the one mechanism that makes
the proof mean something.

THE RETRY IS THE POINT. The original ran `dotnet run --no-build` and, on ANY nonzero exit, ran
`dotnet run` again WITH a build, then reported the SECOND attempt's exit code. That shape is a defect
in `prove-aptitude.ps1` (a real run has real HTTP side effects, so a retry re-runs a genuine refusal)
and it is deliberately NOT a defect here: `gk-forge/tools/ProveHubCombat` is a pure in-memory proof, so a
second run double-applies nothing. The retry is kept verbatim and must stay visible -- a run that
needed the retry is distinguishable from one that did not.

THE EXIT CODE IS THE CHILD'S. The original's whole verdict was `$LASTEXITCODE`; the port forwards it
unchanged, so a failing proof's exit code reaches the caller and a passing one does not read as a
refusal.

`subprocess` and `shutil` are reached through private seams, because they are process-wide modules. The
REAL end-to-end proof -- the real `gk-forge/tools/ProveHubCombat` -- is a separate differential, because a
suite that stubs the thing under test is testing its own arithmetic.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROVE_HUB_COMBAT_SCRIPT",
                             REPO / "scripts" / "prove_hub_combat.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_hub_combat.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("prove_hub_combat", SCRIPT)
proof = importlib.util.module_from_spec(_spec)
sys.modules["prove_hub_combat"] = proof
_spec.loader.exec_module(proof)
_PRISTINE = {"_RUN": proof._RUN, "_WHICH": proof._WHICH}


class Ground:
    """A planted tool directory and result path. `built` is stated EXPLICITLY at every call site,
    because a fixture whose name contradicts what it sets up has cost this program three times."""

    def __init__(self, built: bool = True) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="phc-contract-")
        self.tool = Path(self._tmp.name) / "ProveHubCombat"
        self.out = Path(self._tmp.name) / "result.json"
        self.tool.mkdir(parents=True, exist_ok=True)
        if built:
            built_dir = self.tool / "bin" / "Debug" / "net8.0"
            built_dir.mkdir(parents=True, exist_ok=True)
            (built_dir / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
        self.built = built

    def cleanup(self) -> None:
        self._tmp.cleanup()


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(proof, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(proof, name)!r}")


class TheRetry(SeamGuard):
    """First attempt `--no-build`; on ANY nonzero exit, one retry WITH a build; the LAST attempt's
    exit code is the run's. The proof is pure in-memory, so the retry double-applies nothing."""

    def _drive(self, first_code: int, second_code: int):
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        calls: list[list[str]] = []

        def run(cmd, **kwargs):
            calls.append(list(cmd))
            # The retry is THREE subprocess calls: the --no-build run, the build, the built run. The
            # build always succeeds; the two runs carry the scripted exit codes.
            if cmd[1] == "build":
                return subprocess.CompletedProcess(cmd, 0, "Build succeeded.", "")
            return subprocess.CompletedProcess(cmd, first_code if len(calls) == 1 else second_code,
                                               "child stdout", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = proof.main(["--json", "--tool", str(ground.tool),
                                       "--out", str(ground.out)])
        return code, calls, json.loads(out.getvalue())

    def test_a_PASSING_first_attempt_is_NOT_retried(self) -> None:
        code, calls, envelope = self._drive(0, 0)
        self.assertEqual(code, 0)
        self.assertEqual(len(calls), 1, f"the proof ran {len(calls)} times")
        self.assertIn("--no-build", calls[0])
        self.assertIs(envelope["retried"], False)

    def test_a_FAILING_first_attempt_is_retried_once_WITH_a_build(self) -> None:
        code, calls, envelope = self._drive(1, 0)
        self.assertEqual(code, 0, "the retry's exit code is the run's, so a recovered run passes")
        self.assertEqual(len(calls), 3, f"the proof ran {len(calls)} times")
        self.assertIn("--no-build", calls[0], "the first attempt must be --no-build")
        self.assertEqual(calls[1][1], "build", "the retry builds, as a separate bounded call")
        self.assertNotIn("--no-build", calls[2], "the retry's run must not pass --no-build")
        self.assertIs(envelope["retried"], True)
        self.assertEqual(envelope["firstAttempt"]["exitCode"], 1)

    def test_a_FAILING_retry_reports_the_SECOND_attempts_exit_code(self) -> None:
        code, calls, envelope = self._drive(1, 2)
        self.assertEqual(code, 2, "the LAST attempt's exit code is the run's")
        self.assertEqual(len(calls), 3)
        self.assertIs(envelope["retried"], True)

    def test_the_retry_BUILDS_and_the_build_is_bounded(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 1, "", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(io.StringIO()):
                    proof.main(["--json", "--tool", str(ground.tool), "--out", str(ground.out)])
        builds = [c for c in seen if c["cmd"][1] == "build"]
        self.assertEqual(len(builds), 1, f"the retry built {len(builds)} times")
        self.assertIsNotNone(builds[0]["kwargs"].get("timeout"), "the build is unbounded")
        self.assertIsNotNone(builds[0]["kwargs"].get("capture_output"))


class TheRun(SeamGuard):
    def setUp(self) -> None:
        self.ground = Ground(built=True)
        self.addCleanup(self.ground.cleanup)
        self.seen: list[dict] = []

    def _run(self, **kwargs):
        def run(cmd, **kw):
            self.seen.append({"cmd": list(cmd), "kwargs": kw})
            return subprocess.CompletedProcess(cmd, kwargs.get("code", 0), "child stdout", "")

        with mock.patch.object(proof, "_RUN", run):
            return proof.run_proof("dotnet", self.ground.tool, ["--out", "x.json"], 90,
                                   with_build=kwargs.get("with_build", False))

    def test_the_ARGUMENTS_are_forwarded_VERBATIM_after_a_SEPARATOR(self) -> None:
        self._run()
        self.assertEqual(self.seen[0]["cmd"], ["dotnet", "run", "--no-build", "--", "--out", "x.json"])

    def test_the_RETRY_shape_is_a_plain_dotnet_run(self) -> None:
        self._run(with_build=True)
        self.assertEqual(self.seen[0]["cmd"], ["dotnet", "run", "--", "--out", "x.json"])

    def test_every_call_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        self._run()
        self.assertIsNotNone(self.seen[0]["kwargs"].get("timeout"))
        self.assertIsNotNone(self.seen[0]["kwargs"].get("capture_output"))

    def test_the_CHILD_gets_a_working_directory_and_the_PROCESS_does_not_CHDIR(self) -> None:
        before = os.getcwd()
        self._run()
        self.assertEqual(self.seen[0]["kwargs"].get("cwd"), str(self.ground.tool))
        self.assertEqual(os.getcwd(), before)
        self.assertNotIn("chdir(", SCRIPT.read_text(encoding="utf-8"))

    def test_the_CHILDS_EXIT_CODE_comes_back_UNCHANGED(self) -> None:
        code, out, _ = self._run(code=1)
        self.assertEqual(code, 1, "a failing proof's exit code must reach the caller")
        self.assertIn("child stdout", out)

    def test_a_TIMEOUT_REFUSES_rather_than_waiting(self) -> None:
        with mock.patch.object(proof, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 90)):
            with self.assertRaises(proof.Refusal) as caught:
                proof.run_proof("dotnet", self.ground.tool, [], 90, with_build=False)
        self.assertEqual(caught.exception.reason, "RUN-TIMED-OUT")
        self.assertIn("90s", caught.exception.detail,
                      "the refusal must say the bound it exceeded, not merely that it timed out")


class TheRefusals(SeamGuard):
    def test_a_MISSING_tool_directory_is_NAMED(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            out = io.StringIO()
            with redirect_stdout(out):
                code = proof.main(["--json", "--tool", str(Path(d) / "absent"),
                                   "--out", str(Path(d) / "o.json")])
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "TOOL-MISSING")

    def test_a_missing_dotnet_is_NAMED(self) -> None:
        with mock.patch.object(proof, "_WHICH", return_value=None):
            with self.assertRaises(proof.Refusal) as caught:
                proof.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_work(self) -> None:
        for flag in ("--build-timeout", "--run-timeout"):
            with self.subTest(flag=flag):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = proof.main([flag, "0", "--json"])
                self.assertEqual(code, proof.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = proof.main(["--run-timeout", "0"])
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())

    def test_a_BUILD_that_LIES_about_SUCCESS_is_a_named_refusal(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_tool("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "TOOL-NOT-BUILT")

    def test_a_FAILED_build_passes_the_COMPILER_output_through(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(
                cmd, 1, "", "error CS1002: ; expected")):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_tool("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-FAILED")
        self.assertIn("CS1002", caught.exception.detail)

    def test_a_BUILD_timeout_REFUSES_rather_than_waiting_forever(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 60)):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_tool("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-TIMED-OUT")
        self.assertIn("60s", caught.exception.detail)


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - proof.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - proof.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(proof.EXIT_REFUSED, 64)

    def test_the_flags_match_the_TOOLS_OWN_spelling(self) -> None:
        """Every flag this tool forwards is a flag `gk-forge/tools/ProveHubCombat` accepts. A rename on either
        side would otherwise surface as an argparse error from a subprocess."""
        tool = REPO / "tools" / "ProveHubCombat" / "Program.cs"
        if not tool.is_file():
            self.skipTest("the tool source is not present")
        text = tool.read_text(encoding="utf-8", errors="replace")
        self.assertIn("--out", text, f"--out is forwarded but {tool.name} does not accept it")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--tool", "--configuration", "--out", "--build-timeout",
                     "--run-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-OutJson", "-Out", "-Tool"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-hub-combat.ps1", head)
        lowered = head.lower()
        for reason in ("retry", "bounded", "working directory", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_DEFAULT_out_path_is_pinned_because_it_is_the_PROGRAMS_own(self) -> None:
        self.assertEqual(proof.DEFAULT_OUT,
                         ("docs", "research", "actor-hub-and-combat-power", "_prove-hub-combat.json"))

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "proof":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_NO_case_STARTS_a_patch_it_cannot_STOP(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned = []
        for cls in (n for n in ast.walk(tree) if isinstance(n, ast.ClassDef)):
            for func in (n for n in cls.body if isinstance(n, ast.FunctionDef)
                         and n.name.startswith("test")):
                for node in ast.walk(func):
                    if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                            and node.func.attr in ("start", "stop")):
                        owned = any(node in ast.walk(stmt)
                                    for parent in ast.walk(func)
                                    if isinstance(parent, (ast.With, ast.AsyncWith))
                                    for stmt in parent.body)
                        if not owned:
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}")
        self.assertEqual(unowned, [], "\n".join(unowned))

    def test_the_TFM_is_GLOBBED_so_a_bump_is_not_a_string_to_forget(self) -> None:
        """Found by falsification on the sibling port: a hardcoded `net8.0` satisfied every case,
        because the fixture only ever built a `net8.0`. The tool globs, so a bumped TFM is found -- and
        a TFM that is present but holds no assembly is still reported as not built."""
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            tool = Path(d) / "ProveHubCombat"
            for tfm in ("net7.0", "net9.0"):
                out = tool / "bin" / "Debug" / tfm
                out.mkdir(parents=True)
                (out / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            self.assertIsNotNone(proof.assembly_path(tool, "Debug"),
                                 "a bumped TFM was not found, so a hardcoded one would be safe here")
            (tool / "bin" / "Debug" / "net9.0" / proof.DEFAULT_ASSEMBLY).unlink()
            (tool / "bin" / "Debug" / "net9.0" / "README.txt").write_text("no assembly",
                                                                            encoding="utf-8")
            self.assertIsNotNone(proof.assembly_path(tool, "Debug"),
                                 "the other TFM's assembly should still satisfy the lookup")
            self.assertIsNone(proof.assembly_path(tool, "Release"),
                              "a configuration with no output at all was reported as built")


if __name__ == "__main__":
    unittest.main()
