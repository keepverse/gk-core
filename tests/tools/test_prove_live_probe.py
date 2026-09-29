"""Contract tests for `gk-core/scripts/prove_live_probe.py`.

A FORWARDER, so the contract is about the wrapper, not the recipe. The properties that matter are the
ones the wrapper itself owns, and each corresponds to a real failure:

  * THE BUILD CHECK IS KEYED ON THE BUILD OUTPUT EXISTING, never on a recipe's exit code. A real run has
    real side effects against a live server -- it mints a specimen, spends allocation points, equips an
    item -- so retrying on any nonzero exit would silently re-run a genuine step refusal. The port must
    therefore NEVER retry, and never call the build after a run.
  * Every `dotnet` call is bounded. The original bounded neither.
  * A missing tool, a missing interpreter, a failed build, a build that "succeeds" without producing an
    entry assembly, and an empty `--run` are all NAMED refusals -- not a `dotnet` message.
  * The working directory is passed to the child, not set on the process.

The REAL end-to-end proof -- both tools, the real `gk-fusion/tools/ProveLiveProbe`, a real running server on the
slot's own port, real HTTP and real refusals -- is a separate differential harness, because a suite that
stubs the thing under test is testing its own arithmetic.

`subprocess` and `shutil` are reached through private seams, because they are process-wide modules: a
patch of either reaches every other test in the project.
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
SCRIPT = Path(os.environ.get("PROVE_LIVE_PROBE_SCRIPT",
                             REPO / "scripts" / "prove_live_probe.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_live_probe.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("prove_live_probe", SCRIPT)
probe = importlib.util.module_from_spec(_spec)
sys.modules["prove_live_probe"] = probe
_spec.loader.exec_module(probe)
_PRISTINE = {"_RUN": probe._RUN, "_WHICH": probe._WHICH}

RECIPE = ["-Mode", "A", "-BaseUrl", "http://127.0.0.1:5101", "-PlayerId", "1"]


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(probe, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(probe, name)!r}")


class Ground:
    """A planted tool directory. `built` is stated EXPLICITLY at every call site rather than defaulted,
    because the build-check invariant is about that one fact and a fixture whose name contradicts what
    it sets up has cost this program three times already."""

    def __init__(self, built: bool, tool_dir_name: str = "ProveLiveProbe") -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="plp-contract-")
        self.tool = Path(self._tmp.name) / tool_dir_name
        self.root = Path(self._tmp.name) / "repo"
        (self.root / "scripts").mkdir(parents=True, exist_ok=True)
        # The tool DIRECTORY always exists; `built` decides only whether its build output does. A case
        # planting `built=False` to force a build was refusing with TOOL-MISSING before making a call.
        self.tool.mkdir(parents=True, exist_ok=True)
        if built:
            out = self.tool / "bin" / "Debug" / "net8.0"
            out.mkdir(parents=True, exist_ok=True)
            (out / probe.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
        self.built = built

    def cleanup(self) -> None:
        self._tmp.cleanup()


class TheBuildCheck(SeamGuard):
    """Keyed on the OUTPUT EXISTING, never on a recipe's exit code."""

    def test_an_ALREADY_BUILT_tool_is_NOT_rebuilt(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(probe, "_RUN") as run:
            needed = probe.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertIs(needed, False, "an existing build must not be redone")
        self.assertEqual(run.call_count, 0, "a redundant build ran, which is a live-side cost")

    def test_an_UNBUILT_tool_is_built_once_and_then_required_to_EXIST(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            # The build claims success, so the tool must then CHECK the output rather than trust it.
            (ground.tool / "bin" / "Debug" / "net8.0").mkdir(parents=True, exist_ok=True)
            (ground.tool / "bin" / "Debug" / "net8.0" / probe.DEFAULT_ASSEMBLY).write_text(
                "MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "Build succeeded.", "")

        with mock.patch.object(probe, "_RUN", run):
            self.assertIs(probe.build_if_needed("dotnet", ground.tool, "Debug", 60), True)

    def test_a_BUILD_that_LIES_about_SUCCESS_is_a_named_refusal(self) -> None:
        """A build that exits 0 and produces no entry assembly must not be handed to `run --no-build`."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(probe, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with self.assertRaises(probe.Refusal) as caught:
                probe.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "TOOL-NOT-BUILT")
        self.assertIn(probe.DEFAULT_ASSEMBLY, caught.exception.detail)

    def test_a_FAILED_build_passes_the_COMPILER_output_through(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(probe, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(
                cmd, 1, "", "error CS1002: ; expected")):
            with self.assertRaises(probe.Refusal) as caught:
                probe.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-FAILED")
        self.assertIn("CS1002", caught.exception.detail)
    def test_a_BUILD_timeout_REFUSES_rather_than_waiting_forever(self) -> None:
        """Found by falsification: no case drove a build timeout, so the build path's
        `except subprocess.TimeoutExpired` could not be killed."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(probe, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 60)):
            with self.assertRaises(probe.Refusal) as caught:
                probe.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-TIMED-OUT")
        self.assertIn("60s", caught.exception.detail,
                      "the refusal must say the bound it exceeded, not merely that it timed out")

    def test_BOTH_the_BUILD_and_the_RUN_carry_a_TIMEOUT(self) -> None:
        """The requirement is that EVERY subprocess call is bounded. The first version of this case only
        ever made a run, so the build's timeout was unpinned even though it is the same requirement."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            if cmd[1] == "build":   # argv[1] is the subcommand; a substring test matches `--no-build`
                out = ground.tool / "bin" / "Debug" / "net8.0"
                out.mkdir(parents=True, exist_ok=True)
                (out / probe.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(probe, "_RUN", run):
            probe.build_if_needed("dotnet", ground.tool, "Debug", 60)
            probe.run_recipe("dotnet", ground.tool, RECIPE, 90)
        subcommands = [c["cmd"][1] for c in seen]
        self.assertEqual(len(seen), 2, f"expected a build and a run, saw {len(seen)}")
        self.assertIn("build", subcommands, "the build was never called")
        self.assertIn("run", subcommands, "the run was never called")
        for call in seen:
            self.assertIsNotNone(call["kwargs"].get("timeout"),
                                 f"unbounded: {' '.join(call['cmd'][:3])}")
            self.assertIsNotNone(call["kwargs"].get("capture_output"))



class TheAssemblyLookup(SeamGuard):
    def test_the_TFM_is_GLOBBED_so_a_bump_is_not_a_string_to_forget(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            tool = Path(d) / "T"
            for tfm in ("net7.0", "net9.0"):
                out = tool / "bin" / "Debug" / tfm
                out.mkdir(parents=True)
                (out / probe.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            self.assertIsNotNone(probe.assembly_path(tool, "Debug"))
            self.assertIsNone(probe.assembly_path(tool, "Release"),
                              "a configuration with no output must not be found")

    def test_a_configuration_directory_that_does_not_exist_is_NOT_an_error(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(probe.assembly_path(Path(d) / "absent", "Debug"))
    def test_a_tool_directory_THAT_EXISTS_but_has_NO_configuration_is_not_found(self) -> None:
        """The case this replaces pointed `assembly_path` at a directory that did not exist AT ALL, so
        with the `is_dir()` guard removed the glob over a missing directory still returned None: the case
        passed with and without the property it claimed to test.

        Here the tool directory EXISTS and `bin/Release` exists, but holds no entry assembly -- so only
        the guard can reject it.
        """
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            tool = Path(d) / "T"
            (tool / "bin" / "Release").mkdir(parents=True)
            (tool / "bin" / "Release" / "README.txt").write_text("no assembly here", encoding="utf-8")
            self.assertIsNone(probe.assembly_path(tool, "Release"),
                              "an existing configuration with no entry assembly was reported as built")



class TheRun(SeamGuard):
    def setUp(self) -> None:
        self.ground = Ground(built=True)
        self.addCleanup(self.ground.cleanup)
        self.seen: list[dict] = []

    def _run(self, **kwargs):
        def run(cmd, **kw):
            self.seen.append({"cmd": list(cmd), "kwargs": kw})
            return subprocess.CompletedProcess(cmd, kwargs.get("code", 0),
                                              kwargs.get("out", "child stdout"), "")
        with mock.patch.object(probe, "_RUN", run):
            return probe.run_recipe("dotnet", self.ground.tool, RECIPE, 90)

    def test_the_RECIPE_arguments_are_forwarded_VERBATIM(self) -> None:
        self._run()
        self.assertEqual(self.seen[0]["cmd"], ["dotnet", "run", "--no-build", "--", *RECIPE],
                         "an argument was rewritten in transit, which is how a flag silently changes meaning")

    def test_every_call_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        self._run()
        self.assertIsNotNone(self.seen[0]["kwargs"].get("timeout"))
        self.assertIsNotNone(self.seen[0]["kwargs"].get("capture_output"))

    def test_the_CHILD_gets_a_working_directory_and_the_PROCESS_does_not_CHDIR(self) -> None:
        """`Push-Location $toolDir` changed the cwd for every other caller in the same process."""
        before = os.getcwd()
        self._run()
        self.assertEqual(self.seen[0]["kwargs"].get("cwd"), str(self.ground.tool))
        self.assertEqual(os.getcwd(), before)
        self.assertNotIn("chdir(", SCRIPT.read_text(encoding="utf-8"))

    def test_the_CHILDS_exit_code_and_OUTPUT_come_back_unchanged(self) -> None:
        code, out, _ = self._run(code=7, out="RESULT: FAIL (2 step(s) not ok)")
        self.assertEqual(code, 7, "a refused recipe's exit code must reach the caller")
        self.assertIn("2 step(s) not ok", out)

    def test_a_TIMEOUT_REFUSES_and_carries_what_the_child_already_printed(self) -> None:
        with mock.patch.object(probe, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 90)):
            with self.assertRaises(probe.Refusal) as caught:
                probe.run_recipe("dotnet", self.ground.tool, RECIPE, 90)
        self.assertEqual(caught.exception.reason, "RUN-TIMED-OUT")
        self.assertIn("real side effects", caught.exception.detail,
                      "a timeout is not a retry, and the refusal has to say so")
    def test_a_bare_separator_is_a_LOUD_argparse_refusal_not_a_forwarded_one(self) -> None:
        """The tool's documented behaviour, pinned because the first version of this case asserted the
        OPPOSITE and was wrong.

        `argparse.REMAINDER` lets argparse itself reject a bare `--`: the process raises
        `SystemExit: 2` with "unrecognized arguments: -- -Side plant", BEFORE `main`'s body runs. So a
        caller who reaches for `--` gets a loud failure rather than a separator silently handed to the
        child, which would then have to interpret it -- the risk worth closing.

        """
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with self.assertRaises(SystemExit) as caught:
            probe.main(["--tool", str(ground.tool), "--run", "-Mode", "A", "--", "-Side", "plant"])
        self.assertEqual(caught.exception.code, 2, "a bare `--` must be argparse's non-zero refusal")

    def test_the_RECIPE_arguments_reach_the_CHILD_in_order_without_a_separator(self) -> None:
        """Driven through `main`, so the forwarding is observed at the CHILD rather than at the helper.
        A case asserting forwarding on the helper alone would pass even if `main` reordered or filtered
        the argv on the way."""
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, "RESULT: PASS", "")

        fake_dotnet = "C:" + chr(92) * 2 + "dotnet.exe"
        with mock.patch.object(probe, "_RUN", run):
            with mock.patch.object(probe, "_WHICH", return_value=fake_dotnet):
                probe.main(["--tool", str(ground.tool), "--run", *RECIPE])
        self.assertEqual(len(seen), 1, f"expected exactly one child call, saw {len(seen)}")
        self.assertEqual(seen[0][seen[0].index("--") + 1:], RECIPE,
                         f"the recipe's own arguments must survive intact: {seen[0]}")



class TheNeverRetryInvariant(SeamGuard):
    """A real run has real side effects, so the wrapper must run the recipe exactly once."""

    def test_a_REFUSED_recipe_is_NOT_re_run(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        runs: list[list[str]] = []

        def run(cmd, **kwargs):
            runs.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 1, "RESULT: FAIL", "")

        with mock.patch.object(probe, "_RUN", run):
            with mock.patch.object(probe, "_WHICH", return_value="C:\\dotnet.exe"):
                code = probe.main(["--json", "--tool", str(ground.tool), "--run", *RECIPE])
        self.assertEqual(code, 1)
        self.assertEqual(len(runs), 1, f"the recipe ran {len(runs)} times; it has side effects")
        self.assertIn("run", runs[0])

    def test_a_build_is_NEVER_requested_after_a_run(self) -> None:
        """The build check is keyed on the output EXISTING, so once a build has happened the check is
        satisfied. A second build would mean the check was re-read from the wrong thing."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        order: list[str] = []

        def run(cmd, **kwargs):
            # argv[1] is the subcommand. A substring test is wrong here: the RUN's argv is
            # `dotnet run --no-build -- ...`, and `--no-build` contains "build" -- so the first version
            # classified the run as a build and read the order as ['build', 'build'].
            if cmd[1] == "build":
                order.append("build")
                out = ground.tool / "bin" / "Debug" / "net8.0"
                out.mkdir(parents=True, exist_ok=True)
                (out / probe.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            else:
                order.append("run")
            return subprocess.CompletedProcess(cmd, 0, "RESULT: PASS", "")

        with mock.patch.object(probe, "_RUN", run):
            with mock.patch.object(probe, "_WHICH", return_value="C:\\dotnet.exe"):
                probe.main(["--tool", str(ground.tool), "--run", *RECIPE])
        self.assertEqual(order, ["build", "run"],
                         f"the wrapper's own call order was {order}, not build-then-run-once")

    def test_the_envelope_REPORTS_that_it_did_not_retry(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        out = io.StringIO()
        with mock.patch.object(probe, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 1, "", "")):
            with mock.patch.object(probe, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(out):
                    probe.main(["--json", "--tool", str(ground.tool), "--run", *RECIPE])
        payload = json.loads(out.getvalue())
        self.assertIs(payload["retried"], False,
                      "the envelope must state the never-retry invariant, not leave it implied")


class TheRefusals(SeamGuard):
    def test_a_MISSING_tool_directory_is_NAMED(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            out = io.StringIO()
            with redirect_stdout(out):
                code = probe.main(["--json", "--tool", str(Path(d) / "absent"), "--run", *RECIPE])
        self.assertEqual(code, probe.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "TOOL-MISSING")

    def test_a_MISSING_dotnet_is_NAMED(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(probe, "_WHICH", return_value=None):
            with self.assertRaises(probe.Refusal) as caught:
                probe.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_an_EMPTY_run_REFUSES_and_shows_the_INVOCATION(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = probe.main(["--json", "--run"])
        self.assertEqual(code, probe.EXIT_REFUSED)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["reason"], "NO-RUN-ARGS")
        self.assertIn("--run", payload["detail"],
                      "the refusal must show the invocation, not merely say it is empty")

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_work(self) -> None:
        for flag in ("--build-timeout", "--run-timeout"):
            with self.subTest(flag=flag):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = probe.main([flag, "0", "--json", "--run", *RECIPE])
                self.assertEqual(code, probe.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        # No --json here: with it the refusal is JSON on STDOUT by design, and the first version of this
        # case passed it anyway, so stderr was empty and the assertion measured the flag, not the tool.
        err = io.StringIO()
        with redirect_stderr(err):
            code = probe.main(["--run"])
        self.assertEqual(code, probe.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - probe.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - probe.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(probe.EXIT_REFUSED, 64)

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--tool", "--configuration", "--build-timeout", "--run-timeout",
                     "--json", "--run"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Mode", "-BaseUrl"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "A"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-live-probe.ps1", head)
        lowered = head.lower()
        for reason in ("bounded", "working directory", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_NEVER_RETRY_rationale_is_in_the_docstring(self) -> None:
        """It is the one invariant a future editor is most likely to break by adding a retry."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1].lower()
        self.assertIn("side effect", head)
        self.assertIn("never retries", head)

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
            if isinstance(target, ast.Name) and target.id == "probe":
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


if __name__ == "__main__":
    unittest.main()
