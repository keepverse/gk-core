"""Contract tests for `gk-core/scripts/prove_aptitude.py`.

A PROOF, so the contract is about refusing rather than passing, and about the one mechanism that makes
the proof mean something.

THE CHANNELS OMISSION IS THE POINT. `--channels ""` means "every touched channel" and is selected by
OMISSION of the flag. Forwarding an empty string instead would mean "no channels" and the tool would
compare nothing and report a pass -- a proof that cannot fail. The real run proves this is not
hypothetical: on this repository the default scope passes with a zero delta and the all-channels scope
FAILS with a large non-zero delta (the documented P3.1 gap, the battle composer's uncapped ChannelMods
loop). A suite that only drove the passing case would not notice a port that broke either.

THE RETRY THAT MASKED A FAILURE. The original ran `dotnet run --no-build` and, on ANY nonzero exit, ran
it again with a build and reported the SECOND attempt's code. Measured here: the first attempt ran a
stale Debug binary and died, and the script reported success. So the build is keyed on the build OUTPUT
being absent, decided BEFORE the recipe runs, and the recipe runs exactly once.

AN ABSENT RESULT IS NOT A PASS. The tool's own machine-readable surface is read back: an exit 0 with no
result file, or with one that is not JSON, is a named refusal rather than a success.

`subprocess` and `shutil` are reached through private seams, because they are process-wide modules. The
REAL end-to-end proof -- both tools, the real `gk-core/tools/ProveAptitude`, both the passing and the failing
case -- is a separate differential, because a suite that stubs the thing under test is testing its own
arithmetic.
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
SCRIPT = Path(os.environ.get("PROVE_APTITUDE_SCRIPT",
                             REPO / "scripts" / "prove_aptitude.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_aptitude.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("prove_aptitude", SCRIPT)
proof = importlib.util.module_from_spec(_spec)
sys.modules["prove_aptitude"] = proof
_spec.loader.exec_module(proof)
_PRISTINE = {"_RUN": proof._RUN, "_WHICH": proof._WHICH}

PASSING = {"Theta": 1000, "Source": "Might", "Points": 100,
           "Deltas": {"combat.power.omni": 0}, "PerChannel": {"combat.power.omni": {}},
           "Pass": True}
FAILING = {"Theta": 1000, "Source": "Might", "Points": 100,
           "Deltas": {"combat.power.omni": 0, "combat.accuracy.omni": -26220},
           "PerChannel": {}, "Pass": False}


class Ground:
    """A planted tool directory and result path. `built` and `result` are stated EXPLICITLY at every call
    site, because a fixture whose name contradicts what it sets up has cost this program three times."""

    def __init__(self, built: bool = True, result: dict | None = None) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="pa-contract-")
        self.tool = Path(self._tmp.name) / "ProveAptitude"
        self.out = Path(self._tmp.name) / "result.json"
        self.tool.mkdir(parents=True, exist_ok=True)
        if built:
            built_dir = self.tool / "bin" / "Debug" / "net8.0"
            built_dir.mkdir(parents=True, exist_ok=True)
            (built_dir / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
        if result is not None:
            self.out.write_text(json.dumps(result), encoding="utf-8")
        self.built = built

    def cleanup(self) -> None:
        self._tmp.cleanup()


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(proof, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(proof, name)!r}")


class TheChannelsOmission(SeamGuard):
    """`--channels ""` selects "every touched channel" by OMISSION. Forwarding an empty string would
    select "no channels", and the tool would compare nothing and report a pass."""

    def forwarded_for(self, channels: str) -> tuple[list[str], dict]:
        ground = Ground(built=True, result=PASSING)
        self.addCleanup(ground.cleanup)
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, "child stdout", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                envelope = {}
                out = io.StringIO()
                with redirect_stdout(out):
                    proof.main(["--json", "--tool", str(ground.tool), "--out", str(ground.out),
                                "--channels", channels])
                envelope = json.loads(out.getvalue())
        return seen[0], envelope

    def test_an_EMPTY_channels_is_OMITED_from_the_forwarded_argv(self) -> None:
        argv, envelope = self.forwarded_for("")
        self.assertNotIn("--channels", argv, f"an empty --channels reached the tool: {argv}")
        self.assertIs(envelope["channelsOmitted"], True)

    def test_a_NAMED_channels_IS_forwarded_verbatim(self) -> None:
        argv, envelope = self.forwarded_for("combat.power.omni,resource.max.hp")
        self.assertIn("--channels", argv)
        self.assertEqual(argv[argv.index("--channels") + 1], "combat.power.omni,resource.max.hp")
        self.assertIs(envelope["channelsOmitted"], False)

    def test_every_FORWARDED_flag_carries_its_value(self) -> None:
        """A flag forwarded without its value would shift the next flag into the wrong slot."""
        argv, _ = self.forwarded_for("combat.power.omni")
        forwarded = argv[argv.index("--") + 1:]
        value_flags = {"--theta", "--source", "--points", "--out", "--channels"}
        for i, token in enumerate(forwarded):
            if token in value_flags:
                self.assertLess(i + 1, len(forwarded), f"{token} was forwarded with no value")
                self.assertNotIn(forwarded[i + 1], value_flags,
                                 f"{token} was given the next FLAG as its value: {forwarded}")


class TheAbsentResult(SeamGuard):
    """An exit 0 with no result file is not a pass."""

    def setUp(self) -> None:
        self.ground = Ground(built=True, result=None)
        self.addCleanup(self.ground.cleanup)

    def _run(self, result: dict | None, code: int):
        def run(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, code, "child stdout", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    rc = proof.main(["--json", "--tool", str(self.ground.tool),
                                     "--out", str(self.ground.out)])
        return rc, out.getvalue()

    def test_exit_0_with_NO_result_is_a_named_refusal(self) -> None:
        code, raw = self._run(None, 0)
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertEqual(json.loads(raw)["reason"], "UNREADABLE-RESULT")
        self.assertIn("no result", json.loads(raw)["detail"])

    def test_exit_0_with_UNREADABLE_result_is_a_named_refusal(self) -> None:
        self.ground.out.write_text("{not json", encoding="utf-8")
        code, raw = self._run(None, 0)
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertEqual(json.loads(raw)["reason"], "UNREADABLE-RESULT")

    def test_a_REFUSING_recipe_that_still_wrote_a_result_keeps_the_RECIPES_exit(self) -> None:
        """The tool's own verdict is the run's verdict. A refusal that wrote a result is not this
        wrapper's to overturn."""
        self.ground.out.write_text(json.dumps(FAILING), encoding="utf-8")
        code, raw = self._run(None, 1)
        self.assertEqual(code, 1)
        payload = json.loads(raw)
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertIs(payload["pass"], False)
        self.assertEqual(payload["deltas"], FAILING["Deltas"])

    def test_a_result_is_READ_BACK_and_REPORTED_not_inferred_from_the_exit_code(self) -> None:
        self.ground.out.write_text(json.dumps(PASSING), encoding="utf-8")
        code, raw = self._run(None, 0)
        payload = json.loads(raw)
        self.assertIs(payload["resultWritten"], True)
        self.assertIs(payload["pass"], True)
        self.assertEqual(payload["deltas"], PASSING["Deltas"])


class TheBuildCheck(SeamGuard):
    """Keyed on the OUTPUT EXISTING, decided BEFORE the recipe runs."""

    def test_an_ALREADY_BUILT_tool_is_NOT_rebuilt(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN") as run:
            self.assertIs(proof.build_if_needed("dotnet", ground.tool, "Debug", 60), False)
        self.assertEqual(run.call_count, 0)

    def test_an_UNBUILT_tool_is_built_once_and_then_MUST_EXIST(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            out = ground.tool / "bin" / "Debug" / "net8.0"
            out.mkdir(parents=True, exist_ok=True)
            (out / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "Build succeeded.", "")

        with mock.patch.object(proof, "_RUN", run):
            self.assertIs(proof.build_if_needed("dotnet", ground.tool, "Debug", 60), True)

    def test_a_BUILD_that_LIES_about_SUCCESS_is_a_named_refusal(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "TOOL-NOT-BUILT")

    def test_a_FAILED_build_passes_the_COMPILER_output_through(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(
                cmd, 1, "", "error CS1002: ; expected")):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-FAILED")
        self.assertIn("CS1002", caught.exception.detail)
    def test_a_BUILD_timeout_REFUSES_rather_than_waiting_forever(self) -> None:
        """Found by falsification on this port AND on the sibling forwarder: no case drove a build
        timeout, so the build path's `except subprocess.TimeoutExpired` could not be killed."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(proof, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 60)):
            with self.assertRaises(proof.Refusal) as caught:
                proof.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-TIMED-OUT")
        self.assertIn("60s", caught.exception.detail,
                      "the refusal must say the bound it exceeded, not merely that it timed out")

    def test_BOTH_the_BUILD_and_the_RUN_carry_a_TIMEOUT(self) -> None:
        """The requirement is that EVERY subprocess call is bounded. Exercising it with a run alone left
        the build's timeout unpinned -- the same hole on this port as on the sibling forwarder."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            if cmd[1] == "build":   # argv[1] is the subcommand; a substring test matches `--no-build`
                out = ground.tool / "bin" / "Debug" / "net8.0"
                out.mkdir(parents=True, exist_ok=True)
                (out / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            proof.build_if_needed("dotnet", ground.tool, "Debug", 60)
            proof.run_proof("dotnet", ground.tool, ["--theta", "1000"], 90)
        subcommands = [c["cmd"][1] for c in seen]
        self.assertEqual(len(seen), 2, f"expected a build and a run, saw {len(seen)}")
        self.assertIn("build", subcommands, "the build was never called")
        self.assertIn("run", subcommands, "the run was never called")
        for call in seen:
            self.assertIsNotNone(call["kwargs"].get("timeout"),
                                 f"unbounded: {' '.join(call['cmd'][:3])}")
            self.assertIsNotNone(call["kwargs"].get("capture_output"))



class TheRun(SeamGuard):
    def setUp(self) -> None:
        self.ground = Ground(built=True, result=PASSING)
        self.addCleanup(self.ground.cleanup)
        self.seen: list[dict] = []

    def _run(self, **kwargs):
        def run(cmd, **kw):
            self.seen.append({"cmd": list(cmd), "kwargs": kw})
            return subprocess.CompletedProcess(cmd, kwargs.get("code", 0), "child stdout", "")

        with mock.patch.object(proof, "_RUN", run):
            return proof.run_proof("dotnet", self.ground.tool, ["--theta", "1000"], 90)

    def test_the_ARGUMENTS_are_forwarded_VERBATIM_after_a_SEPARATOR(self) -> None:
        self._run()
        self.assertEqual(self.seen[0]["cmd"], ["dotnet", "run", "--no-build", "--", "--theta", "1000"])

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
                proof.run_proof("dotnet", self.ground.tool, [], 90)
        self.assertEqual(caught.exception.reason, "RUN-TIMED-OUT")
        self.assertIn("not retried", caught.exception.detail)


class TheNeverRetryInvariant(SeamGuard):
    """The original's retry reported the SECOND attempt's exit code, which is how a stale-binary
    failure was recorded as a success."""

    def test_a_REFUSING_proof_is_NOT_re_run(self) -> None:
        ground = Ground(built=True, result=FAILING)
        self.addCleanup(ground.cleanup)
        runs: list[list[str]] = []

        def run(cmd, **kwargs):
            runs.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 1, "", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = proof.main(["--json", "--tool", str(ground.tool), "--out", str(ground.out)])
        self.assertEqual(code, 1)
        self.assertEqual(len(runs), 1, f"the proof ran {len(runs)} times; a retry can mask the first failure")
        self.assertIs(json.loads(out.getvalue())["retried"], False)

    def test_a_build_is_NEVER_requested_after_a_run(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        order: list[str] = []

        def run(cmd, **kwargs):
            # argv[1] is the subcommand: a substring test matches `--no-build`
            if cmd[1] == "build":
                order.append("build")
                out = ground.tool / "bin" / "Debug" / "net8.0"
                out.mkdir(parents=True, exist_ok=True)
                (out / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            else:
                order.append("run")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    proof.main(["--json", "--tool", str(ground.tool), "--out", str(ground.out)])
        self.assertEqual(order, ["build", "run"], f"the wrapper's call order was {order}")


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

    def test_a_MISSING_dotnet_is_NAMED(self) -> None:
        with mock.patch.object(proof, "_WHICH", return_value=None):
            with self.assertRaises(proof.Refusal) as caught:
                proof.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_a_NON_POSITIVE_theta_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = proof.main(["--json", "--theta", "0"])
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "NON-POSITIVE-THETA")

    def test_a_NON_POSITIVE_points_REFUSES_because_it_allocates_NOTHING(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = proof.main(["--json", "--points", "0"])
        self.assertEqual(code, proof.EXIT_REFUSED)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["reason"], "NON-POSITIVE-POINTS")
        self.assertIn("proves nothing", payload["detail"])

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
            code = proof.main(["--points", "0"])
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


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
        """Every flag this tool forwards is a flag `gk-core/tools/ProveAptitude` accepts. A rename on either
        side would otherwise surface as an argparse error from a subprocess."""
        tool = REPO / "tools" / "ProveAptitude" / "Program.cs"
        if not tool.is_file():
            self.skipTest("the tool source is not present")
        text = tool.read_text(encoding="utf-8", errors="replace")
        for flag in ("--theta", "--source", "--points", "--out", "--channels"):
            self.assertIn(flag, text, f"{flag} is forwarded but {tool.name} does not accept it")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--tool", "--configuration", "--theta", "--source", "--points",
                     "--channels", "--out", "--build-timeout", "--run-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Theta", "-Source", "-Points", "-Channels", "-OutJson"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-aptitude.ps1", head)
        lowered = head.lower()
        for reason in ("retry", "bounded", "working directory", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_DEFAULT_scope_is_pinned_because_it_is_a_MEASURED_CHOICE(self) -> None:
        self.assertEqual(proof.DEFAULT_CHANNELS, "combat.power.omni")
        self.assertNotEqual(proof.DEFAULT_CHANNELS, "",
                            "the all-channels scope is a documented gap, not the default")

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
        """Found by falsification: a hardcoded `net8.0` satisfied every case, because the fixture only
        ever built a `net8.0`. The tool globs, so a bumped TFM is found -- and a TFM that is present but
        holds no assembly is still reported as not built."""
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            tool = Path(d) / "ProveAptitude"
            for tfm in ("net7.0", "net9.0"):
                out = tool / "bin" / "Debug" / tfm
                out.mkdir(parents=True)
                (out / proof.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            self.assertIsNotNone(proof.assembly_path(tool, "Debug"),
                                 "a bumped TFM was not found, so a hardcoded one would be safe here")
            (tool / "bin" / "Debug" / "net9.0" / proof.DEFAULT_ASSEMBLY).unlink()
            (tool / "bin" / "Debug" / "net9.0" / "README.txt").write_text("no assembly", encoding="utf-8")
            self.assertIsNotNone(proof.assembly_path(tool, "Debug"),
                                 "the other TFM's assembly should still satisfy the lookup")
            self.assertIsNone(proof.assembly_path(tool, "Release"),
                              "a configuration with no output at all was reported as built")



if __name__ == "__main__":
    unittest.main()
