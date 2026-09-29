"""Contract tests for `gk-core/scripts/test_substrate_leak_alarm.py`.

An alarm is a detector, and a detector's contract is asymmetric: reporting OK is easy and means little,
while reporting a leak when there is none — or reporting OK when it could not look — is the failure that
matters. Every case below is therefore about the alarm's ability to REFUSE, and the green path is the
one case where passing is easy.

The headline property is the one the retired PowerShell form got wrong: an unreadable snapshot root
produced an EMPTY set, the set difference was empty, and the alarm reported OK having observed nothing.
A detector's blindness is not the subject's innocence.

The wrapped run is stubbed through the tool's PRIVATE `_RUN` seam, because `subprocess` is the
process-wide module: patching it reaches every other test in the project, and a patch that outlives its
`with` block breaks them while this suite reports green. Proven the hard way twice in this program —
`test_dump_melon_p0.py` shipped with that mistake and made 506 unrelated failures in
`test_ps1_port_census.py`.

The REAL end-to-end proof — the alarm wrapped around a real process that really leaks — is a separate
harness, because a suite that stubs the very thing under test is testing its own arithmetic.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("LEAK_ALARM_SCRIPT",
                             REPO / "scripts" / "test_substrate_leak_alarm.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_test_substrate_leak_alarm.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("test_substrate_leak_alarm", SCRIPT)
alarm = importlib.util.module_from_spec(_spec)
sys.modules["test_substrate_leak_alarm"] = alarm
_spec.loader.exec_module(alarm)
_PRISTINE_RUN = alarm._RUN


class Ground:
    """A planted repository and temp root, with a real project directory for the sqlite scan.

    `pack`-style mistakes are the failure mode this program keeps meeting, so `plant_sqlite` and
    `plant_temp_dir` are separate, explicit calls and no case relies on a default it did not mean.
    """

    def __init__(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="leak-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.project = self.root / "tests" / "SomeProject"
        self.project.mkdir(parents=True)
        self.temp = Path(self._tmp.name) / "temp"
        self.temp.mkdir()

    def plant_temp_dir(self, name: str = "fusionrpg-leak") -> Path:
        d = self.temp / name
        (d / "nested").mkdir(parents=True)
        return d

    def plant_preexisting_sqlite(self, name: str = "rpg-old.sqlite") -> Path:
        out = self.project / "bin" / "Debug" / "net8.0"
        out.mkdir(parents=True)
        p = out / name
        p.write_bytes(b"SQLite format 3\x00")
        return p

    def cleanup(self) -> None:
        self._tmp.cleanup()


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if alarm._RUN is not _PRISTINE_RUN:
            self.fail(f"_RUN was still substituted after {self.id()}: {alarm._RUN!r}")


class TheSnapshots(SeamGuard):
    """The alarm must be able to SEE. A snapshot that silently yields nothing is the defect."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_an_UNREADABLE_temp_root_REFUSES_rather_than_reporting_no_leak(self) -> None:
        """The retired form used `-ErrorAction SilentlyContinue`, so this produced an EMPTY set, an
        empty delta, and a verdict of OK. The case that must not exist is 'an absent root means clean'."""
        with self.assertRaises(alarm.Refusal) as caught:
            alarm.temp_snapshot(self.ground.root / "no-such-temp")
        self.assertEqual(caught.exception.reason, "TEMP-ROOT-UNREADABLE")
        self.assertIn("no-such-temp", caught.exception.detail)
        self.assertIn("rather than reporting no leak", caught.exception.detail)

    def test_a_MISSING_tests_dir_REFUSES(self) -> None:
        with self.assertRaises(alarm.Refusal) as caught:
            alarm.sqlite_snapshot(self.ground.root / "no-such-tests")
        self.assertEqual(caught.exception.reason, "TESTS-DIR-MISSING")

    def test_a_ROOT_that_CANNOT_be_LISTED_REFUSES(self) -> None:
        """The absent-root case is easy; this one is the shape that actually bites. A temp root that
        exists and is unreadable -- a permissions change, a network share that dropped, a locked
        directory -- used to produce an EMPTY set through `-ErrorAction SilentlyContinue`, and an empty
        set is a verdict of clean.

        Driven by making `iterdir` raise, because making a real directory unlistable on Windows is not
        something a test can arrange portably, and a case that cannot be arranged is a case that does
        not exist.
        """
        root = self.ground.temp
        with mock.patch.object(Path, "iterdir",
                               side_effect=OSError("access denied")):
            with self.assertRaises(alarm.Refusal) as caught:
                alarm.temp_snapshot(root)
        self.assertEqual(caught.exception.reason, "TEMP-ROOT-UNREADABLE")
        self.assertIn("access denied", caught.exception.detail)

    def test_an_UNREADABLE_root_reads_as_NO_leak_under_the_ORIGINAL_SHAPE(self) -> None:
        """A counterweight for the case above, and the reason it exists: the same unreadable root, read
        the way the PowerShell form read it, yields an empty set. An empty set diffs to nothing, and
        nothing reads as clean."""
        root = self.ground.temp
        with mock.patch.object(Path, "iterdir", side_effect=OSError("access denied")):
            observed = None
            try:
                observed = alarm.temp_snapshot(root)
            except alarm.Refusal:
                observed = set()          # what `-ErrorAction SilentlyContinue` produced
        self.assertEqual(observed, set(), "the control itself changed; the case is measuring nothing")
        self.assertEqual(sorted(set() - set()), [], "an empty set diffs to nothing")

    def test_only_the_STORE_PREFIX_is_snapshotted(self) -> None:
        """A directory that merely exists is not a store leak. Watching a wider set makes the alarm
        report other tools' scratch as this repository's defect."""
        (self.ground.temp / "someone-elses-run").mkdir()
        (self.ground.temp / "fusionrpg-ours").mkdir()
        (self.ground.temp / "not-a-dir.txt").write_text("x")
        self.assertEqual(alarm.temp_snapshot(self.ground.temp), {"fusionrpg-ours"})

    def test_both_OUTPUT_DIRS_are_scanned_and_the_FULL_PATH_is_the_member(self) -> None:
        """`bin/` and `TestResults/`, and the member is the path, not the name: two projects holding a
        file of the same name are two survivors."""
        self.ground.plant_preexisting_sqlite("rpg-a.sqlite")
        results = self.ground.project / "TestResults" / "net8.0"
        results.mkdir(parents=True)
        (results / "rpg-b.sqlite").write_bytes(b"x")
        other = self.ground.root / "tests" / "OtherProject" / "bin"
        other.mkdir(parents=True)
        (other / "rpg-a.sqlite").write_bytes(b"x")
        found = alarm.sqlite_snapshot(self.ground.root / "tests")
        self.assertEqual(len(found), 3, f"expected three survivors, found {sorted(found)}")
        self.assertEqual(len({Path(f).name for f in found}), 2,
                         "two projects hold a file of the same name; the path is the member")

    def test_a_SOURCE_TREE_sqlite_is_NOT_a_survivor(self) -> None:
        """The source tree has no such file; only the OUTPUT roots are watched. Scanning the source tree
        would make an authored fixture a permanent false positive."""
        (self.ground.project / "rpg-in-source.sqlite").write_bytes(b"x")
        self.assertEqual(alarm.sqlite_snapshot(self.ground.root / "tests"), set())


class TheVerdict(SeamGuard):
    """Driven through `main` with a stubbed spawn, so the arithmetic and the exit code are both
    exercised rather than inferred."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def drive(self, side_effect=None, argv: list[str] | None = None,
              plant=None) -> tuple[int, dict | None, str]:
        """`plant` is called INSIDE the stubbed spawn, because a wrapped run can only leak from between
        the two snapshots. A stub that merely returns cannot leak, so a case that planted its dir
        beforehand was planting it BEFORE the before-snapshot and asserting a leak that could not exist.
        That is the fixture-contradicts-itself defect this program has now paid for four times."""

        def run(cmd, **kwargs):
            if plant is not None:
                plant()
            if side_effect is not None:
                return side_effect(cmd, **kwargs)
            return subprocess.CompletedProcess(cmd, 0, "the wrapped run said so", "")

        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(alarm, "_RUN", run):
            with redirect_stdout(out), redirect_stderr(err):
                code = alarm.main(["--root", str(self.ground.root), "--temp-root",
                                   str(self.ground.temp), "--json",
                                   *(argv or ["--run", "python", "-c", "pass"])])
        try:
            return code, json.loads(out.getvalue()), out.getvalue() + err.getvalue()
        except json.JSONDecodeError:
            return code, None, out.getvalue() + err.getvalue()

    def test_a_CLEAN_run_is_OK_and_names_what_it_watched(self) -> None:
        code, payload, _ = self.drive()
        self.assertEqual(code, alarm.EXIT_OK)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["reasons"], [])
        self.assertEqual(payload["leakedTempDirs"], [])
        self.assertEqual(set(payload["tempDirs"]), {"before", "after"})
        self.assertEqual(set(payload["rpgSqlite"]), {"before", "after"})

    def test_a_PRE_EXISTING_dir_is_NOT_a_leak_even_though_the_count_is_NONZERO(self) -> None:
        """The set-diff property, stated as a case. `before == after == 1` and no leak -- so a COUNT
        check is the only thing that could be asserted here, and it is the wrong thing."""
        self.ground.plant_temp_dir("fusionrpg-someone-elses")
        code, payload, _ = self.drive()
        self.assertEqual(code, alarm.EXIT_OK)
        self.assertEqual(payload["tempDirs"], {"before": 1, "after": 1})
        self.assertEqual(payload["leakedTempDirs"], [])

    def test_a_NEW_temp_dir_is_a_leak_and_is_NAMED(self) -> None:
        def plant():
            self.ground.plant_temp_dir("fusionrpg-survivor")

        code, payload, _ = self.drive(plant=plant)
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertEqual(payload["leakedTempDirs"], ["fusionrpg-survivor"])
        self.assertIn("temp dir survived: fusionrpg-survivor", payload["reasons"])

    def test_a_PRE_EXISTING_sqlite_is_NOT_a_leak_and_a_NEW_ONE_IS(self) -> None:
        self.ground.plant_preexisting_sqlite("rpg-old.sqlite")
        code, payload, _ = self.drive()
        self.assertEqual(code, alarm.EXIT_OK)
        self.assertEqual(payload["newRpgSqlite"], [])

        def plant():
            (self.ground.project / "bin" / "Debug" / "net8.0" / "rpg-new.sqlite").write_bytes(b"x")

        code, payload, _ = self.drive(plant=plant)
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertEqual(len(payload["newRpgSqlite"]), 1)
        self.assertIn("rpg-new.sqlite", payload["newRpgSqlite"][0])

    def test_a_PRE_EXISTING_dir_stays_UNREPORTED_when_a_NEW_one_appears_beside_it(self) -> None:
        """The set-diff property where it actually bites.

        A pre-existing dir alone is easy: before == after, so a count check also passes. The
        discriminating shape is a pre-existing dir PLUS a new one: the count now differs, so a check
        degraded to `sorted(after)` reports the pre-existing dir as a leak too -- and a false positive
        in a leak alarm is how people learn to ignore it.
        """
        self.ground.plant_temp_dir("fusionrpg-someone-elses")

        def plant():
            self.ground.plant_temp_dir("fusionrpg-ours")

        code, payload, _ = self.drive(plant=plant)
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertEqual(payload["leakedTempDirs"], ["fusionrpg-ours"],
                         "the pre-existing dir must not be reported as ours")
        self.assertEqual(payload["tempDirs"], {"before": 1, "after": 2})

    def test_a_NON_ZERO_wrapped_run_is_a_FAILURE_even_with_nothing_leaked(self) -> None:
        """A run that failed is not a clean run. An alarm that only watched the filesystem would call
        this OK, and the failure would be reported by the caller as a leak that was not there."""
        def failing(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 3, "", "3 tests failed")

        code, payload, _ = self.drive(side_effect=failing)
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertEqual(payload["runExit"], 3)
        self.assertEqual(payload["leakedTempDirs"], [])
        self.assertIn("the wrapped command exited 3", payload["reasons"])

    def test_a_TIMEOUT_is_a_named_result_not_a_pass(self) -> None:
        def timing_out(cmd, **kwargs):
            raise subprocess.TimeoutExpired(cmd=cmd, timeout=kwargs.get("timeout", 1))

        code, payload, _ = self.drive(side_effect=timing_out, argv=["--timeout", "7", "--run",
                                                                    "python", "-c", "pass"])
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertTrue(payload["runTimedOut"])
        self.assertIn("the wrapped run exceeded its timeout", payload["reasons"])

    def test_the_wrapped_runs_output_reaches_the_envelope_BOUNDED(self) -> None:
        """It was captured and DISCARDED in the first draft, so a caller told 'exited 1' had to re-run
        the suite to learn why. Bounded because a full `dotnet test` transcript is megabytes."""
        def chatty(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 1, "x" * (alarm.RUN_OUTPUT_TAIL + 500), "boom")

        code, payload, _ = self.drive(side_effect=chatty)
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertTrue(payload["runOutputTruncated"])
        self.assertLessEqual(len(payload["runOutput"]), alarm.RUN_OUTPUT_TAIL)
        self.assertIn("boom", payload["runOutput"])

    def test_it_CANNOT_be_OK_with_NO_run_at_all(self) -> None:
        """A counterweight for every case above: passing with nothing observed is the shape that reads
        green while blind."""
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = alarm.main(["--json"])
        self.assertEqual(code, alarm.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "NO-WRAPPED-RUN")

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_spawn(self) -> None:
        spawned: list = []

        def spy(cmd, **kwargs):
            spawned.append(cmd)
            return subprocess.CompletedProcess(cmd, 0, "", "")

        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(alarm, "_RUN", spy):
            with redirect_stdout(out), redirect_stderr(err):
                code = alarm.main(["--timeout", "0", "--json", "--run", "python", "-c", "pass"])
        self.assertEqual(code, alarm.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")
        self.assertEqual(spawned, [], "a refused run still spawned the wrapped command")


class TheWrappedCommand(SeamGuard):
    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_EVERY_spawn_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(alarm, "_RUN", run):
            alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                        "--timeout", "42", "--json", "--run", "python", "-c", "pass"])
        self.assertEqual(len(seen), 1)
        self.assertEqual(seen[0]["kwargs"].get("timeout"), 42)
        self.assertIsNotNone(seen[0]["kwargs"].get("capture_output"))

    def test_an_ABSENT_executable_REFUSES_BY_NAME(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                               "--json", "--run", "definitely-not-a-real-tool-xyz"])
        self.assertEqual(code, alarm.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "WRAPPED-RUN-NOT-FOUND")

    def test_the_RESOLVED_executable_is_what_SPAWNS(self) -> None:
        """`CreateProcess` resolves a bare name by appending `.exe` only, so the bare token can reach a
        different tool than the one that was resolved."""
        seen: list[str] = []

        def run(cmd, **kwargs):
            seen.append(cmd[0])
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(alarm, "_WHICH", return_value=r"C:\shims\python.CMD"):
            with mock.patch.object(alarm, "_RUN", run):
                alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                            "--json", "--run", "python", "-c", "pass"])
        self.assertTrue(seen, "nothing spawned")
        self.assertEqual(seen[0], r"C:\shims\python.CMD", f"argv[0] was {seen[0]!r}")

    def test_the_WHOLE_TAIL_is_passed_through_verbatim(self) -> None:
        """`--run` takes the rest of the line. A tool that stopped at the first token would run `dotnet`
        with no arguments, and a nightly's `--blame-hang-timeout 15min` would silently vanish."""
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, "", "")

        tail = ["dotnet", "test", "x.csproj", "-c", "Release", "--no-build",
                "--blame-hang-timeout", "15min"]
        with mock.patch.object(alarm, "_RUN", run):
            alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                        "--json", "--run", *tail])
        # argv[0] is the RESOLVED interpreter, asserted separately; what matters here is that every
        # other token survived, including the ones that look like this tool's own flags.
        self.assertEqual(seen[0][1:], tail[1:], f"the tail was not passed through: {seen[0]}")
        self.assertEqual(len(seen[0]), len(tail), "a token was dropped from the command line")


class ThePrivateTempRoot(SeamGuard):
    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_isolate_temp_creates_a_private_root_REDIRECTS_and_then_REMOVES_it(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                               "--json", "--isolate-temp", "--run", "python", "-c", "pass"])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, alarm.EXIT_OK)
        self.assertTrue(payload["isolatedTemp"])
        self.assertTrue(payload["privateRootRemoved"])
        self.assertFalse(Path(payload["isolatedTemp"]).exists(),
                         "the alarm's own private root survived the run")

    def test_isolate_temp_RESTORES_the_ambient_TEMP_and_TMP(self) -> None:
        """A leaked redirect would point every later call in the process at a directory the alarm has
        just deleted.

        READ INSIDE the `patch.dict` context, immediately after the call. The first version read it
        after, and `patch.dict` restores `os.environ` on its own -- so the case passed whether or not
        the TOOL restored anything, and a mutant that deleted the tool's restore entirely survived it.
        A case whose own scaffolding supplies the property it claims to test cannot fail.
        """
        seen: dict[str, str | None] = {}
        with mock.patch.dict(os.environ, {"TEMP": "ambient-temp", "TMP": "ambient-tmp"}, clear=False):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                            "--json", "--isolate-temp", "--run", "python", "-c", "pass"])
            # Sampled HERE, while the ambient values this case installed are still in place.
            seen = {v: os.environ.get(v) for v in ("TEMP", "TMP")}
        for variable in ("TEMP", "TMP"):
            self.assertEqual(seen[variable], f"ambient-{variable.split()[0].lower()}",
                             f"{variable} was {seen[variable]!r} after --isolate-temp")

    def test_a_LEFTOVER_in_the_private_root_is_reported_and_the_root_is_KEPT(self) -> None:
        """Deleting the root would erase the evidence and then the evidence. A survivor is already a
        reported leak, so the root stays until a human looks."""
        def leaky(cmd, **kwargs):
            # The child writes into the PRIVATE root, which is what --isolate-temp handed it.
            private = Path(kwargs["env"]["TEMP"])
            (private / "fusionrpg-abandoned").mkdir(parents=True, exist_ok=True)
            return subprocess.CompletedProcess(cmd, 0, "", "")

        original_run = alarm._RUN

        def shim(cmd, **kwargs):
            if "capture_output" in kwargs:
                env = dict(os.environ)
                env["TEMP"] = os.environ.get("TEMP", "")
                kwargs = dict(kwargs, env=env)
            return leaky(cmd, **kwargs)

        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(alarm, "_RUN", shim):
            with redirect_stdout(out), redirect_stderr(err):
                code = alarm.main(["--root", str(self.ground.root), "--temp-root", str(self.ground.temp),
                                   "--json", "--isolate-temp", "--run", "python", "-c", "pass"])
        payload = json.loads(out.getvalue())
        self.assertEqual(code, alarm.EXIT_FAILED)
        self.assertIs(payload["privateRootRemoved"], False)
        self.assertIn("fusionrpg-abandoned", payload["leakedTempDirs"])
        self.assertTrue(Path(payload["isolatedTemp"]).exists(),
                        "a root with a survivor in it must not be deleted")
        shutil.rmtree(payload["isolatedTemp"], ignore_errors=True)
        del original_run

    def test_a_FAILED_delete_of_the_private_root_REFUSES_rather_than_being_SWALLOWED(self) -> None:
        """`Remove-Item ... -ErrorAction SilentlyContinue` on this exact root is the shape a swallowed
        delete takes, and a swallowed delete is how one local run leaked 65.5 GB."""
        with mock.patch.object(alarm, "_RMTREE", side_effect=OSError("file in use")):
            with self.assertRaises(alarm.Refusal) as caught:
                alarm.execute(self.ground.root, self.ground.temp, ["python", "-c", "pass"],
                              True, 30)
        self.assertEqual(caught.exception.reason, "PRIVATE-TEMP-ROOT-NOT-CLEANED")


class Surface(SeamGuard):
    def test_the_JSON_keys_are_the_CONTRACT(self) -> None:
        ground = Ground()
        self.addCleanup(ground.cleanup)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            alarm.main(["--root", str(ground.root), "--temp-root", str(ground.temp), "--json",
                        "--run", "python", "-c", "pass"])
        payload = json.loads(out.getvalue())
        self.assertEqual(set(payload), {
            "tool", "verdict", "exitCode", "wrapped", "runExit", "runTimedOut", "runOutput",
            "runOutputTruncated", "tempDirs", "rpgSqlite", "leakedTempDirs", "newRpgSqlite",
            "isolatedTemp", "privateRootRemoved", "reasons"})
        for banned in ("pid", "durationMs", "started", "timestamp"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run")

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - alarm.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - alarm.REFUSAL_REASONS)}")
        self.assertEqual(alarm.REFUSAL_REASONS, set(alarm.REFUSAL_REASONS),
                         "the declared set has duplicates")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({alarm.EXIT_OK, alarm.EXIT_FAILED, alarm.EXIT_REFUSED}, {0, 1, 64})

    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--temp-root", "--isolate-temp", "--timeout", "--json", "--run"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Run", "-IsolateTemp", "-TempRoot", "-Root"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED_and_WITHDRAWS_the_UNMEASURED_claim(self) -> None:
        """Provenance is only worth carrying if it is accurate. The docstring must name the defects
        that were MEASURED and must say the `$LASTEXITCODE` claim did not reproduce -- a docstring
        asserting a defect that turned out not to exist is a lie that outlives the file."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("test-substrate-leak-alarm.ps1", head)
        lowered = head.lower()
        for reason in ("unreadable snapshot root", "swallowed", "no timeout", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")
        self.assertIn("withdrawn", lowered,
                      "the unmeasured $LASTEXITCODE claim must be recorded as withdrawn")

    def test_the_SET_DIFF_property_is_STATED_not_a_count(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").lower()
        self.assertIn("set difference", head)
        self.assertIn("never a count", head)
        self.assertIn("validation-ssot.md", head, "the standard must be named")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib",
                          "ast", "re"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "alarm":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in global_modules:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))


if __name__ == "__main__":
    unittest.main()
