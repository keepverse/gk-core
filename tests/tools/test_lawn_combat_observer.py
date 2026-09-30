"""Contract tests for `gk-core/scripts/lawn_combat_observer.py`.

A FORWARDER'S CONTRACT IS ABOUT THE SEAM, not about the tool. The tool's own behaviour is proven
separately, by a differential that drives the real `gk-fusion/tools/LawnCombatObserver` against the real slot-1
server; a suite that stubbed the tool would be testing its own arithmetic. So everything here is about
what this wrapper does with arguments, with a build, and with the tool's output.

THE FORWARDING SEAM IS THE POINT. The original used `[Parameter(ValueFromRemainingArguments)] $RestArgs`
and forwarded every named flag through to `dotnet run --` as-is. In Python that is the `argparse` trap
this program has now hit three times: a bare `--` is rejected before the body runs, and a paired flag
cannot carry a value beginning with `-`. So the wrapper takes the flags it owns and forwards the rest
VERBATIM AND IN ORDER -- and then ASSERTS, at run time, that the two flag sets are disjoint, because a
collision would silently steal a flag from the tool. The tool's flags are READ FROM ITS OWN SOURCE rather
than transcribed, because a transcribed list agrees with itself forever and never notices a change.

THE RUN FILE IS THE TOOL'S REAL OUTPUT, and it is READ BACK. A wrapper that printed only its child's
stdout would be reporting its own opinion of a result. An exit 0 with no run file has observed nothing,
and that is a named refusal rather than a success -- "no data" and "zero hits" are different findings and
the run file is where the difference is recorded.

THE PROCESS WORKING DIRECTORY IS NOT MUTATED. The original reached the tool directory by
`Push-Location $toolDir`, so the tool's bare relative `--out` default resolved there. The port passes an
explicit child `cwd` instead, which lands the file in the same place while leaving the caller's cwd
alone.
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
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("LAWN_COMBAT_OBSERVER_SCRIPT",
                             REPO / "scripts" / "lawn_combat_observer.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_lawn_combat_observer.py"
# The tool is gk-fusion's, so it is asked of its owner rather than of REPO. `REPO / "tools" /
# LawnCombatObserver"` does not exist in gk-core, and this suite then GUARDED that absence:
# every real-tool case begins `if not REAL_TOOL.is_dir(): return`. So the assertions that read the
# tool's own source and assert its real flag names - the ones that would catch a flag renamed on
# either side - were skipping, and the suite was green because it had stopped looking.
sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import fusion_root  # noqa: E402

REAL_TOOL = fusion_root(REPO) / "tools" / "LawnCombatObserver"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("lawn_combat_observer", SCRIPT)
lco = importlib.util.module_from_spec(_spec)
sys.modules["lawn_combat_observer"] = lco
_spec.loader.exec_module(lco)
_PRISTINE = {"_RUN": lco._RUN, "_WHICH": lco._WHICH}

NO_DATA = {"NoData": True, "NoDataReason": "no /api/perf windows landed",
           "TotalHits": 0, "WindowsObserved": 0, "InjectorSessionActiveEverTrue": None}
WITH_DATA = {"NoData": False, "NoDataReason": None, "TotalHits": 7, "WindowsObserved": 4,
             "InjectorSessionActiveEverTrue": False}


class Ground:
    """A planted tool directory, run-file path, and expectations. `built` and `result` are stated
    EXPLICITLY at every call site: a fixture whose name contradicts what it set up has cost this program
    three times."""

    def __init__(self, built: bool = True, result: dict | None = None, source: bool = True) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="lco-contract-")
        self.root = Path(self._tmp.name)
        self.tool = self.root / "LawnCombatObserver"
        self.tool.mkdir(parents=True, exist_ok=True)
        if source:
            # A real Options.Parse, so the flag-disjointness check has something true to read.
            (self.tool / "Options.cs").write_text(
                'class Options { public static Options Parse(string[] a) { switch (a[0]) {'
                ' case "baseurl": break; case "durationsec": break;'
                ' case "pollintervalsec": break; case "out": case "outfile": break;'
                ' case "maxhitsample": break; default: break; } return new Options(); } }',
                encoding="utf-8")
        if built:
            out = self.tool / "bin" / "Debug" / "net8.0"
            out.mkdir(parents=True, exist_ok=True)
            (out / lco.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
        if result is not None:
            (self.tool / lco.DEFAULT_RUN_FILE).write_text(json.dumps(result), encoding="utf-8")
        self.built = built
        self.result = result

    def cleanup(self) -> None:
        self._tmp.cleanup()


def same_path(got: str, want: str) -> bool:
    r"""Two spellings of one path, compared as a LOCATION.

    `os.path.normcase` lowercases but does not expand an 8.3 short name; `os.path.realpath` expands but
    does not case-fold. On this machine `tempfile` returns `C:\Users\NENESC~1\...` while the tool reports
    `C:\Users\NeneScarlet\...`, so comparing either side alone is comparing SPELLINGS and reporting a
    difference that is not one. Both operations, in that order, is the fix.
    """
    return os.path.normcase(os.path.realpath(got)) == os.path.normcase(os.path.realpath(want))


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(lco, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(lco, name)!r}")


class TheForwardingSeam(SeamGuard):
    def drive(self, ground: Ground, argv: list[str], code: int = 0) -> tuple[list[str], dict]:
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, code, "child stdout", "")

        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    lco.main(["--json", "--tool", str(ground.tool), *argv])
        return seen[0], json.loads(out.getvalue())

    def test_a_TOOL_FLAG_reaches_the_CHILD_verbatim_and_IN_ORDER(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        argv, _ = self.drive(ground, ["--baseurl", "http://127.0.0.1:5101", "--durationsec", "8"])
        forwarded = argv[argv.index("--") + 1:]
        self.assertEqual(forwarded, ["--baseurl", "http://127.0.0.1:5101", "--durationsec", "8"],
                         f"the forwarded argv was {forwarded}")

    def test_a_tool_FLAG_carrying_a_VALUE_that_looks_LIKE_a_flag_is_not_mangled(self) -> None:
        """The reason the seam exists. A paired `--flag value` cannot carry a value beginning with `-`,
        and a bare `--` is rejected by argparse before the body runs, so the arguments a tool most wants
        are the ones a naive forwarder cannot deliver."""
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        argv, _ = self.drive(ground, ["--out", "-weird-name.json", "--baseurl", "http://x"])
        forwarded = argv[argv.index("--") + 1:]
        self.assertEqual(forwarded, ["--out", "-weird-name.json", "--baseurl", "http://x"])

    def test_NO_arguments_at_all_is_still_a_valid_forward(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        argv, _ = self.drive(ground, [])
        self.assertEqual(argv[argv.index("--") + 1:], [],
                         "the tool has defaults; the wrapper must not invent arguments for it")

    def test_a_COLLIDING_flag_is_a_NAMED_refusal_and_nothing_is_forwarded(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        (ground.tool / "Options.cs").write_text(
            'class Options { public static Options Parse(string[] a) { switch (a[0]) {'
            ' case "json": break; case "root": break; } return new Options(); } }', encoding="utf-8")
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = lco.main(["--json", "--tool", str(ground.tool)])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, lco.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "FLAG-COLLISION")
        self.assertEqual([c for c in seen if "run" in c], [],
                         "a colliding flag was forwarded anyway, stealing it from the tool")

    def test_the_TOOL_flags_are_READ_FROM_its_OWN_source(self) -> None:
        """Not transcribed into this file. A transcribed list agrees with itself forever, so it can never
        report that the tool changed -- which is the only reason to read it at all."""
        if not REAL_TOOL.is_dir():
            self.skipTest("the tool source is not present")
        flags = lco.tool_flags(REAL_TOOL)
        self.assertTrue(flags, f"no flags were read from {REAL_TOOL}")
        self.assertIn("baseurl", flags)
        self.assertIn("durationsec", flags)
        self.assertEqual(flags & lco.WRAPPER_FLAGS, set(),
                         f"the real tool's flags collide with the wrapper's: {sorted(flags & lco.WRAPPER_FLAGS)}")


class TheRunFile(SeamGuard):
    """The tool's real output, read back. `noData` and "zero hits" are DIFFERENT findings."""

    def setUp(self) -> None:
        self.ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(self.ground.cleanup)

    def run_main(self, code: int = 0):
        def run(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, code, "child stdout", "")

        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    rc = lco.main(["--json", "--tool", str(self.ground.tool)])
        return rc, json.loads(out.getvalue())

    def test_the_run_file_is_READ_BACK_and_REPORTED(self) -> None:
        rc, payload = self.run_main(1)
        self.assertEqual(rc, 1, "the tool's own non-zero exit must reach the caller")
        self.assertIs(payload["resultWritten"], True)
        self.assertIs(payload["noData"], True)
        self.assertEqual(payload["noDataReason"], NO_DATA["NoDataReason"])
        self.assertEqual(payload["windowsObserved"], 0)

    def test_NO_data_and_ZERO_hits_are_reported_as_DIFFERENT_findings(self) -> None:
        """The whole point of reading the run file. A run that saw no windows and a run that saw windows
        with no hits are different results, and a wrapper that flattened them would hide a real gap."""
        self.ground.tool.joinpath(lco.DEFAULT_RUN_FILE).write_text(
            json.dumps(WITH_DATA), encoding="utf-8")
        _, payload = self.run_main(0)
        self.assertIs(payload["noData"], False)
        self.assertEqual(payload["totalHits"], 7)
        self.assertEqual(payload["windowsObserved"], 4)

    def test_exit_0_with_NO_run_file_is_a_named_refusal(self) -> None:
        """The first version of this case ran the tool WITHOUT patching `_RUN` -- so it drove the REAL
        tool -- and then asserted only that stdout was not None. It exercised nothing, and falsification
        duly reported a mutant that turns "an absent run file is a pass" on through it. A case that
        cannot fail is worse than a missing case, because it occupies the slot of one."""
        self.ground.tool.joinpath(lco.DEFAULT_RUN_FILE).unlink()
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "child stdout", "")):
            with mock.patch.object(lco, "_WHICH", return_value=r"C:\dotnet.exe"):
                with redirect_stdout(out):
                    code = lco.main(["--json", "--tool", str(self.ground.tool)])
        self.assertEqual(code, lco.EXIT_REFUSED)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["reason"], "UNREADABLE-RESULT")
        self.assertIn("absent run file is not an observation", payload["detail"])

    def test_the_OUT_path_is_resolved_against_the_CHILDS_working_directory(self) -> None:
        """The original reached the tool directory by Push-Location, so a bare relative --out resolved
        THERE. The port must land the file in the same place, which means resolving against the child's
        cwd rather than the caller's."""
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(out):
                    lco.main(["--json", "--tool", str(ground.tool), "--out", "elsewhere.json"])
        payload = json.loads(out.getvalue())
        self.assertTrue(same_path(payload["runFile"], str(ground.tool / "elsewhere.json")),
                        f"the run file landed at {payload['runFile']}, not under the child's cwd")

    def test_an_ABSOLUTE_OUT_path_is_honoured_as_given(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        elsewhere = Path(tempfile.mkdtemp(prefix="lco-out-")) / "run.json"
        self.addCleanup(lambda: elsewhere.parent.rmdir() if elsewhere.parent.is_dir() else None)
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(out):
                    lco.main(["--json", "--tool", str(ground.tool), "--out", str(elsewhere)])
        self.assertTrue(same_path(json.loads(out.getvalue())["runFile"], str(elsewhere)))

    def test_a_NON_ZERO_exit_with_NO_run_file_reports_resultWritten_False(self) -> None:
        """The one reachable state where `result is not None` is False on a non-refusal path.

        The tool refuses an ABSENT run file only when the exit code is 0, because a non-zero exit is the
        tool's own "collected nothing" answer and is legitimate on its own. So a non-zero exit with
        nothing written flows through to the verdict -- and every case so far drove a non-zero exit WITH a
        run file, or an exit 0, which the tool turns into a refusal. This is the state where a wrapper
        that always claimed `resultWritten: true` would claim an observation that does not exist.
        """
        (self.ground.tool / lco.DEFAULT_RUN_FILE).unlink()
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 1, "", "")):
            with mock.patch.object(lco, "_WHICH", return_value=r"C:\dotnet.exe"):
                with redirect_stdout(out):
                    code = lco.main(["--json", "--tool", str(self.ground.tool)])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, 1)
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertIs(payload["resultWritten"], False,
                      "a run that wrote nothing claimed it wrote a result")
        self.assertIsNone(payload["noData"], "noData must come from a file that was actually read")

    def test_resultWritten_is_FALSE_when_there_is_NO_run_file(self) -> None:
        """Every case that read `resultWritten` read it where it was True, so a wrapper that always
        claimed it had written a run file passed everything -- and that field is what a caller reads to
        decide whether there is an observation at all."""
        (self.ground.tool / lco.DEFAULT_RUN_FILE).unlink()
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN",
                               lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with mock.patch.object(lco, "_WHICH", return_value=r"C:\dotnet.exe"):
                with redirect_stdout(out):
                    lco.main(["--json", "--tool", str(self.ground.tool)])
        payload = json.loads(out.getvalue())
        # `assertEqual`, not `assertIs`: `assertIs` is IDENTITY, and two equal-but-distinct strings do
        # not share it. That is the same class of mistake as comparing a path's two SPELLINGS.
        self.assertEqual(payload["verdict"], "REFUSED")
        # `_refuse` is the only source of `prepared`, and it does not carry `resultWritten`; the field is
        # absent rather than True, which is what "we never read one" should look like.
        self.assertIsNot(payload.get("resultWritten"), True)

    def test_an_UNREADABLE_run_file_is_a_named_refusal_not_a_silent_pass(self) -> None:
        (self.ground.tool / lco.DEFAULT_RUN_FILE).write_text("{not json", encoding="utf-8")
        out = io.StringIO()
        with mock.patch.object(lco, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(out):
                    code = lco.main(["--json", "--tool", str(self.ground.tool)])
        self.assertEqual(code, lco.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "UNREADABLE-RESULT")


class TheBuildAndTheBounds(SeamGuard):
    def test_an_ALREADY_BUILT_tool_is_NOT_rebuilt(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(lco, "_RUN") as run:
            self.assertIs(lco.build_if_needed("dotnet", ground.tool, "Debug", 60), False)
        self.assertEqual(run.call_count, 0)

    def test_an_UNBUILT_tool_is_built_once_and_then_MUST_exist(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            out = ground.tool / "bin" / "Debug" / "net8.0"
            out.mkdir(parents=True, exist_ok=True)
            (out / lco.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "Build succeeded.", "")

        with mock.patch.object(lco, "_RUN", run):
            self.assertIs(lco.build_if_needed("dotnet", ground.tool, "Debug", 60), True)

    def test_a_BUILD_that_LIES_about_SUCCESS_is_a_named_refusal(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(lco, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(cmd, 0, "", "")):
            with self.assertRaises(lco.Refusal) as caught:
                lco.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "TOOL-NOT-BUILT")

    def test_a_FAILED_build_passes_the_COMPILER_output_through(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(lco, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(
                cmd, 1, "", "error CS1002: ; expected")):
            with self.assertRaises(lco.Refusal) as caught:
                lco.build_if_needed("dotnet", ground.tool, "Debug", 60)
        self.assertEqual(caught.exception.reason, "BUILD-FAILED")
        self.assertIn("CS1002", caught.exception.detail)

    def test_BOTH_calls_carry_a_TIMEOUT_and_CAPTURE(self) -> None:
        """Found by falsification on two earlier ports: exercising the bound with the run alone leaves the
        build's timeout unpinned, even though it is the same requirement."""
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            if cmd[1] == "build":      # argv[1] is the subcommand; a substring test matches `--no-build`
                out = ground.tool / "bin" / "Debug" / "net8.0"
                out.mkdir(parents=True, exist_ok=True)
                (out / lco.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(lco, "_RUN", run):
            lco.build_if_needed("dotnet", ground.tool, "Debug", 60)
            lco.run_observer("dotnet", ground.tool, [], 90)
        self.assertEqual(len(seen), 2)
        for call in seen:
            self.assertIsNotNone(call["kwargs"].get("timeout"))
            self.assertIsNotNone(call["kwargs"].get("capture_output"))

    def test_a_RUN_timeout_REFUSES_and_says_it_is_not_retried(self) -> None:
        ground = Ground(built=True)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(lco, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 90)):
            with self.assertRaises(lco.Refusal) as caught:
                lco.run_observer("dotnet", ground.tool, [], 90)
        self.assertEqual(caught.exception.reason, "RUN-TIMED-OUT")
        self.assertIn("NOT retried", caught.exception.detail)

    def test_a_FAILING_observation_is_NOT_re_run(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        runs: list[list[str]] = []

        def run(cmd, **kwargs):
            runs.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 1, "", "")

        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = lco.main(["--json", "--tool", str(ground.tool)])
        self.assertEqual(code, 1)
        self.assertEqual(len(runs), 1, f"the observation ran {len(runs)} times")
        self.assertIs(json.loads(out.getvalue())["retried"], False)

    def test_the_CHILD_gets_a_working_directory_and_the_PROCESS_does_not_CHDIR(self) -> None:
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append(kwargs)
            return subprocess.CompletedProcess(cmd, 0, "", "")

        before = os.getcwd()
        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value="C:\\dotnet.exe"):
                with redirect_stdout(io.StringIO()):
                    lco.main(["--json", "--tool", str(ground.tool)])
        self.assertTrue(same_path(seen[0].get("cwd", ""), str(ground.tool)),
                        f"the child's cwd was {seen[0].get('cwd')!r}, not the tool directory")
        self.assertEqual(os.getcwd(), before)
        self.assertNotIn("chdir(", SCRIPT.read_text(encoding="utf-8"))


class TheRefusals(SeamGuard):
    def test_a_MISSING_tool_directory_is_NAMED(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = lco.main(["--json", "--tool", str(REPO / "no-such-tool")])
        self.assertEqual(code, lco.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "TOOL-MISSING")

    def test_a_MISSING_dotnet_is_NAMED(self) -> None:
        with mock.patch.object(lco, "_WHICH", return_value=None):
            with self.assertRaises(lco.Refusal) as caught:
                lco.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_a_NON_POSITIVE_TIMEOUT_or_empty_CONFIGURATION_REFUSES(self) -> None:
        for argv, reason in (([ "--build-timeout", "0", "--json"], "INVALID-TIMEOUT"),
                             (["--run-timeout", "-1", "--json"], "INVALID-TIMEOUT"),
                             (["--configuration", "", "--json"], "INVALID-CONFIGURATION")):
            with self.subTest(argv=argv):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = lco.main(argv)
                self.assertEqual(code, lco.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], reason)

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = lco.main(["--tool", str(REPO / "no-such-tool")])
        self.assertEqual(code, lco.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - lco.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - lco.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(lco.EXIT_REFUSED, 64)

    def test_it_uses_KNOWN_ARGS_so_a_tool_flag_is_never_a_wrapper_error(self) -> None:
        """`parse_args` would exit 2 on any argument the wrapper does not own, which is every tool flag.
        The whole seam depends on unknown arguments being forwarded rather than rejected."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("parse_known_args", source)
        self.assertNotIn("args = parser.parse_args(", source)

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--tool", "--configuration", "--build-timeout", "--run-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_a_PowerShell_spelled_TOOL_flag_is_FORWARDED_not_rejected(self) -> None:
        """Deliberately the opposite of what this program asserts for a NON-forwarding tool.

        This wrapper forwards everything it does not own, so a PowerShell-spelled tool flag is forwarded
        rather than rejected -- and the tool's own `Options.Parse` trims leading dashes, so
        `-DurationSec 8` still reaches it and still works. The retired script's usage line showed exactly
        those spellings, so a user migrating keeps the same behaviour. A case demanding "unrecognized
        arguments" here was testing the OPPOSITE of this tool's contract.
        """
        if not REAL_TOOL.is_dir():
            self.skipTest("the tool source is not present")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--json", "--tool", str(REAL_TOOL),
             "-baseurl", "http://127.0.0.1:9", "-durationsec", "1", "-out", str(
                 Path(tempfile.mkdtemp(prefix="lco-fwd-")) / "r.json")],
            capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO))
        payload = json.loads(proc.stdout)
        self.assertEqual(payload["forwarded"],
                         ["-baseurl", "http://127.0.0.1:9", "-durationsec", "1", "-out",
                          payload["forwarded"][-1]],
                         f"the PowerShell spellings were not forwarded verbatim: {payload['forwarded']}")

    def test_the_WRAPPERs_OWN_flags_are_NOT_forwarded(self) -> None:
        """The other half: a flag the wrapper owns must be consumed by the wrapper, never handed on. If
        one leaked, the tool would see a flag it does not accept and answer "unknown flag"."""
        ground = Ground(built=True, result=NO_DATA)
        self.addCleanup(ground.cleanup)
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(lco, "_RUN", run):
            with mock.patch.object(lco, "_WHICH", return_value=r"C:\dotnet.exe"):
                with redirect_stdout(io.StringIO()):
                    lco.main(["--json", "--tool", str(ground.tool), "--run-timeout", "77"])
        forwarded = seen[0][seen[0].index("--") + 1:]
        for flag in ("--json", "--tool", "--run-timeout", "--root", "--configuration"):
            self.assertNotIn(flag, forwarded, f"the wrapper's own {flag} leaked to the tool")

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("lawn-combat-observer.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("working directory", "bounded", "machine-readable", "retry"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_default_ASSEMBLY_name_is_the_ONE_the_csproj_declares(self) -> None:
        """Found by falsification. The assembly name is a closed contract the project file owns, so the
        name is read FROM the csproj rather than from a second copy of the constant: two copies agree
        until one of them is wrong, and then the wrapper silently never finds a build."""
        csproj = REAL_TOOL / f"{REAL_TOOL.name}.csproj"
        if not csproj.is_file():
            self.skipTest("the tool project file is not present")
        text = csproj.read_text(encoding="utf-8")
        if "<AssemblyName>" in text:
            # An explicit declaration wins over the stem, so the wrapper's default must match IT.
            declared = text.split("<AssemblyName>")[1].split("</AssemblyName>")[0].strip()
            # The csproj declares the assembly name WITHOUT the extension; the FILE on disk carries it.
            # So the wrapper's constant, which is a file name, is `declared + ".dll"`. Comparing the two
            # directly would report a difference that is only a spelling one -- the same mistake as
            # comparing a path's short and long spellings.
            self.assertEqual(lco.DEFAULT_ASSEMBLY, f"{declared}.dll",
                             "the wrapper's default assembly file is not the declared name plus the "
                             "extension the on-disk file carries")
        else:
            # No declaration: the SDK derives the assembly name from the project file's STEM. The first
            # version of this case asserted an <AssemblyName> element the csproj does not contain, so it
            # could only ever fail -- a case written against a shape the subject does not have.
            self.assertEqual(lco.DEFAULT_ASSEMBLY, f"{csproj.stem}.dll",
                             "with no AssemblyName declared, the name is the project stem, and the "
                             "wrapper's default does not match it -- so it would never find a build")

    def test_the_TFM_is_GLOBBED_so_a_bump_is_not_a_string_to_forget(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            tool = Path(d) / "T"
            for tfm in ("net9.0", "net10.0"):
                out = tool / "bin" / "Debug" / tfm
                out.mkdir(parents=True)
                (out / lco.DEFAULT_ASSEMBLY).write_text("MZ", encoding="utf-8")
            self.assertIsNotNone(lco.assembly_path(tool, "Debug"))
            self.assertIsNone(lco.assembly_path(tool, "Release"))

    def test_the_TOOLs_own_flag_names_are_the_ONES_the_SUITE_asserts(self) -> None:
        """Closed vocabulary: the tool's six flags are a closed set the tool owns, and a case that reads
        them from the tool's source rather than from a constant here is what keeps the two in step."""
        if not REAL_TOOL.is_dir():
            self.skipTest("the tool source is not present")
        self.assertEqual(lco.tool_flags(REAL_TOOL),
                         {"baseurl", "durationsec", "maxhitsample", "out", "outfile", "pollintervalsec"})

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
            if isinstance(target, ast.Name) and target.id == "lco":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_every_case_writes_UNDER_a_temporary_directory(self) -> None:
        temp_root = Path(tempfile.gettempdir()).resolve()
        found = list(temp_root.glob("lco-contract-*"))
        outside = [str(p) for p in found if temp_root not in p.resolve().parents]
        self.assertEqual(outside, [], f"a case wrote outside a temporary directory: {outside}")
        inside_repo = [str(p) for p in found if REPO in p.resolve().parents]
        self.assertEqual(inside_repo, [], f"a case wrote inside the repository: {inside_repo}")

    def test_the_tools_RUN_FILE_is_IGNORED_so_a_real_run_does_not_dirty_the_tree(self) -> None:
        """The tool's own Options docstring calls the run file scratch and says it is \"never a repo path a
        caller did not explicitly choose\", but its DEFAULT is a bare relative filename and the wrapper
        runs the child IN the tool directory -- so the default does land inside the repository. A real run
        is how the tool is proven, so every proof would otherwise leave an untracked file behind."""
        ignored = subprocess.run(["git", "check-ignore", "-q", f"tools/LawnCombatObserver/{lco.DEFAULT_RUN_FILE}"],
                                 cwd=str(REPO), capture_output=True)
        self.assertEqual(ignored.returncode, 0,
                         f"tools/LawnCombatObserver/{lco.DEFAULT_RUN_FILE} is not gitignored, so a real "
                         f"run shows up as an untracked file")


if __name__ == "__main__":
    unittest.main()
