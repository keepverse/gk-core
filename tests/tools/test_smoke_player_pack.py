"""Contract tests for `gk-core/scripts/smoke_player_pack.py`.

A SMOKE TEST, so the contract is about refusing rather than passing. Every case below reaches a
verdict deliberately; the ones that matter most are the failures the retired PowerShell form turned into
silence:

  * a MALFORMED PROBE OUTPUT READ AS A PASS. `$probeJson | ConvertFrom-Json` sat in a `try { } catch {
    $probeObj = @{ ok = ($probeExit -eq 0) } }`, so a build banner on stdout or a truncated stream made a
    probe that proved nothing and exited 0 a recorded PASS with an empty step list.
  * BOTH DELETES WERE SWALLOWED. The `finally` wrapped the process-tree kill and the data-directory
    removal in nested `try { } catch { }`, which is the shape this repository's testing standard names
    as the cause of a 65.5 GB leak.
  * NEITHER `dotnet` CALL WAS BOUNDED.
  * THE PORT WAS A RANDOM NUMBER, not configuration.

`dotnet`, the probe process and the HTTP layer are all reached through private seams, because
`subprocess`, `shutil` and `urllib` are the process-wide modules: a patch of any of them reaches every
other test in the project. The REAL end-to-end run -- both tools, the real pack, a real server boot --
is a separate harness, because a suite that stubs the thing under test is testing its own arithmetic.
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
SCRIPT = Path(os.environ.get("PACK_SMOKE_SCRIPT",
                             REPO / "scripts" / "smoke_player_pack.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_smoke_player_pack.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("smoke_player_pack", SCRIPT)
smoke = importlib.util.module_from_spec(_spec)
sys.modules["smoke_player_pack"] = smoke
_spec.loader.exec_module(smoke)
_PRISTINE = {"_RUN": smoke._RUN, "_POPEN": smoke._POPEN, "_WHICH": smoke._WHICH,
             "_RMTREE": smoke._RMTREE, "_URLOPEN": smoke._URLOPEN}

PROBE_OK = {"ok": True, "packDir": "x", "steps": [{"name": "layout", "ok": True}]}
PROBE_WITH_A_FAILED_STEP = {"ok": False, "packDir": "x",
                            "steps": [{"name": "layout", "ok": False, "message": "Missing seed"},
                                      {"name": "manifest", "ok": True}]}
HEALTH_IMPORTED = '{"ok": true, "contentSource": "imported", "simEnabled": false}'
HEALTH_FALLBACK = '{"ok": true, "contentSource": "", "simEnabled": false}'
HEALTH_SIM_ON = '{"ok": true, "contentSource": "imported", "simEnabled": true}'


class Ground:
    """A planted pack and repository, so a case reaches a verdict without a real dotnet or a real
    server. `has_server` is stated EXPLICITLY at every call site rather than defaulted, because a
    fixture whose name contradicts what it sets up has cost this program three times already."""

    def __init__(self, has_server: bool, has_project: bool = True, has_pack: bool = True) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="pack-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.pack = Path(self._tmp.name) / "pack"
        self.project = self.root.joinpath(*smoke.RELATIVE_PROBE_PROJECT)
        self.summary = Path(self._tmp.name) / "artifacts" / "player-pack-smoke.json"
        if has_project:
            self.project.parent.mkdir(parents=True, exist_ok=True)
            self.project.write_text("<Project/>", encoding="utf-8")
        if has_pack:
            self.pack.mkdir(parents=True, exist_ok=True)
        self.server_exe = self.pack.joinpath(*smoke.SERVER_RELATIVE)
        if has_server:
            self.server_exe.parent.mkdir(parents=True, exist_ok=True)
            self.server_exe.write_text("MZ fake exe", encoding="utf-8")
        self.has_server = has_server

    def cleanup(self) -> None:
        self._tmp.cleanup()


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(smoke, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(smoke, name)!r}")


class TheProbeOutput(SeamGuard):
    """The silent green. A report that cannot be read is not a passing report."""

    def setUp(self) -> None:
        self.ground = Ground(has_server=False)
        self.addCleanup(self.ground.cleanup)

    def drive(self, probe_stdout: str, probe_exit: int = 0) -> smoke.Report:
        """`execute` RAISES a Refusal rather than returning one, so a refusal case uses
        `assertRaises` and this returns a `Report` only on the path that produces one. The first version
        unpacked the return as a tuple, which raised `TypeError` on a `Report` and on a `Refusal`
        alike -- and a harness that cannot call the thing it tests reads as a defect in the tool."""
        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "Build succeeded.", "")
            return subprocess.CompletedProcess(cmd, probe_exit, probe_stdout, "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                return smoke.execute(self.ground.root, self.ground.pack, True, None, 5, 60, 60,
                                     self.ground.summary)

    def test_a_PROBE_that_emits_NON_JSON_REFUSES_rather_than_reading_it_as_a_pass(self) -> None:
        """The original caught this parse failure and substituted `ok = (exit 0)`, so a `dotnet run`
        that proved nothing and exited cleanly became a PASS with an empty step list."""
        with self.assertRaises(smoke.Refusal) as caught:
            self.drive("Build banner on stdout\nnot json at all\n", 0)
        self.assertEqual(caught.exception.reason, "PROBE-OUTPUT-NOT-JSON")
        self.assertIn("not json at all", caught.exception.detail)

    def test_a_PROBE_with_NO_steps_REFUSES(self) -> None:
        with self.assertRaises(smoke.Refusal) as caught:
            self.drive(json.dumps({"ok": True, "packDir": "x"}), 0)
        self.assertEqual(caught.exception.reason, "PROBE-STEPS-MISSING")

    def test_a_PROBE_that_is_not_an_OBJECT_REFUSES(self) -> None:
        with self.assertRaises(smoke.Refusal) as caught:
            self.drive(json.dumps([{"name": "layout"}]), 0)
        self.assertEqual(caught.exception.reason, "PROBE-OUTPUT-NOT-AN-OBJECT")

    def test_a_FAILED_step_is_reported_by_NAME(self) -> None:
        out = self.drive(json.dumps(PROBE_WITH_A_FAILED_STEP), 1)
        self.assertIsInstance(out, smoke.Report)
        self.assertFalse(out.ok)
        self.assertIn("probe step 'layout' failed: Missing seed", out.reasons)

    def test_a_probe_that_says_NOT_ok_is_a_FAILURE_even_with_a_ZERO_exit_and_no_failing_step(self) -> None:
        """`ok` must consult the probe's OWN verdict.

        Found by falsification: dropping `bool(self.probe.get("ok"))` from `ok` satisfied every case,
        because every failing fixture ALSO failed on the exit code or carried a failing step. This one
        is red on the verdict alone.
        """
        out = self.drive(json.dumps({"ok": False, "packDir": "x",
                                     "steps": [{"name": "manifest", "ok": True}]}), 0)
        self.assertFalse(out.ok, "`ok` ignored the probe's own verdict")
        self.assertEqual(out.probe_exit, 0, "the exit code WAS zero; only the verdict failed")

    def test_a_BUILD_that_fails_REFUSES_before_the_probe_is_run(self) -> None:
        """No fixture drove a failing build, so `if build.returncode != 0` could be deleted."""

        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 1, "", "CS1002: ; expected")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                with self.assertRaises(smoke.Refusal) as caught:
                    smoke.execute(self.ground.root, self.ground.pack, True, None, 5, 60, 60,
                                  self.ground.summary)
        self.assertEqual(caught.exception.reason, "PROBE-BUILD-FAILED")
        self.assertIn("CS1002", caught.exception.detail,
                      "the compiler's own message must reach the refusal")

    def test_it_CANNOT_be_OK_with_NO_steps_at_all(self) -> None:
        """A counterweight: `ok` requires a non-empty steps list, so a tool that recorded nothing
        cannot report a pass."""
        self.assertFalse(smoke.Report(probe={}, probe_exit=0).ok)
        self.assertFalse(smoke.Report(probe={"ok": True, "steps": []}, probe_exit=0).ok)


class TheServerStep(SeamGuard):
    """The three properties, in the original's order, with its wording for each failure."""

    def test_a_pack_on_the_CODE_FALLBACK_is_named_as_such(self) -> None:
        step = smoke.assert_pack_properties.__wrapped__("u", HEALTH_FALLBACK) \
            if hasattr(smoke.assert_pack_properties, "__wrapped__") else None
        # The real function performs an HTTP call for the snapshot only when contentSource is
        # `imported`, so the fallback verdict needs no network.
        step = smoke.assert_pack_properties("http://127.0.0.1:1", HEALTH_FALLBACK)
        self.assertFalse(step.ok)
        self.assertIn("contentSource=''", step.message)
        self.assertIn("expected 'imported'", step.message)
        self.assertIn("Server\\data\\seed", step.message)

    def test_SIM_left_ON_is_a_failure(self) -> None:
        step = smoke.assert_pack_properties("http://127.0.0.1:1", HEALTH_SIM_ON)
        self.assertFalse(step.ok)
        self.assertIn("simEnabled=true", step.message)

    def test_health_that_is_not_JSON_is_a_failure_not_a_pass(self) -> None:
        step = smoke.assert_pack_properties("http://127.0.0.1:1", "<html>hello</html>")
        self.assertFalse(step.ok)
        self.assertIn("did not return JSON", step.message)

    def test_a_snapshot_that_returns_SIM_JSON_is_a_failure(self) -> None:
        """The third property. `/api/test/snapshot` is only mapped when SIM is on, and with SIM off an
        SPA fallback answers unknown paths with `index.html` -- so SIM JSON there means the pack left
        SIM on, which is what this asserts.

        Found by falsification: the only snapshot fixture was HTML, so the check could be deleted
        without the suite noticing.
        """
        served: list[str] = []

        def opener(request, timeout=None):
            url = getattr(request, "full_url", request)
            served.append(url)
            if url.endswith("/health"):
                return _Response(HEALTH_IMPORTED)
            return _Response('{"eventCount": 12, "simEnabled": true}',
                             content_type="application/json")

        with mock.patch.object(smoke, "_URLOPEN", opener):
            step = smoke.assert_pack_properties("http://127.0.0.1:5399", HEALTH_IMPORTED)
        self.assertFalse(step.ok)
        self.assertIn("returned SIM JSON", step.message)
        self.assertTrue(any(url.endswith("/api/test/snapshot") for url in served),
                        "the snapshot was never requested, so nothing was proved")

    def test_SIM_JSON_served_AS_HTML_is_not_a_failure(self) -> None:
        """The counterweight to the case above. The original's condition was
        `$looksLikeSimJson -and $ct -notmatch "text/html"`, so a body carrying the markers but served as
        the SPA fallback is NOT a failure -- and a check that dropped the content-type half would fail
        a correct pack."""
        def opener(request, timeout=None):
            url = getattr(request, "full_url", request)
            if url.endswith("/health"):
                return _Response(HEALTH_IMPORTED)
            return _Response('<html>eventCount simEnabled</html>', content_type="text/html")

        with mock.patch.object(smoke, "_URLOPEN", opener):
            step = smoke.assert_pack_properties("http://127.0.0.1:5399", HEALTH_IMPORTED)
        self.assertTrue(step.ok, "markers in an HTML body are the SPA fallback, not SIM JSON")

    def test_a_correct_pack_says_so_and_reaches_the_SNAPSHOT_assertion(self) -> None:
        seen: list[tuple[str, int]] = []

        def opener(request, timeout=None):
            url = getattr(request, "full_url", request)
            seen.append((url, timeout))
            body = b"<html>index</html>"

            class R:
                status = 200
                headers = {"Content-Type": "text/html"}

                def read(self):
                    return body

                def __enter__(self):
                    return self

                def __exit__(self, *exc):
                    return False
            return R()

        with mock.patch.object(smoke, "_URLOPEN", opener):
            step = smoke.assert_pack_properties("http://127.0.0.1:5399", HEALTH_IMPORTED)
        self.assertTrue(step.ok)
        self.assertIn("contentSource='imported'", step.message)
        self.assertIn("SPA fallback", step.message)
        self.assertTrue(any("/api/test/snapshot" in url for url, _ in seen))
        for _, timeout in seen:
            self.assertIsNotNone(timeout, "the snapshot GET was unbounded")


class TheCleanupAndTheStop(SeamGuard):
    """The swallowed deletes, and the stop that must not be reported as success when it failed."""

    def setUp(self) -> None:
        self.ground = Ground(has_server=True)
        self.addCleanup(self.ground.cleanup)

    def _boot(self, health: str, stop_ok: bool = True, rmtree_ok: bool = True) -> smoke.Report:
        report = smoke.Report(pack_dir=str(self.ground.pack))

        class FakeProc:
            pid = 4242

            def __init__(self):
                self.terminated = False

            def poll(self):
                return None

            def terminate(self):
                self.terminated = True

            def wait(self, timeout=None):
                if not stop_ok:
                    raise subprocess.TimeoutExpired(cmd="server", timeout=timeout or 1)
                return 0

            def kill(self):
                if not stop_ok:
                    raise subprocess.TimeoutExpired(cmd="server", timeout=1)

        opener = lambda request, timeout=None: _Response(health)
        with mock.patch.object(smoke, "_POPEN", return_value=FakeProc()):
            with mock.patch.object(smoke, "_URLOPEN", opener):
                with mock.patch.object(smoke, "_RUN",
                                       return_value=subprocess.CompletedProcess([], 0, "", "")):
                    with mock.patch.object(smoke, "_RMTREE",
                                           side_effect=None if rmtree_ok
                                           else OSError("file in use")):
                        smoke.boot_and_check(self.ground.server_exe, 5399, 3, report)
        return report

    def test_a_data_directory_that_WILL_NOT_DELETE_is_REPORTED_not_SWALLOWED(self) -> None:
        """`Remove-Item ... -ErrorAction SilentlyContinue` inside `try { } catch { }` is the shape this
        repository's testing standard names as the cause of a 65.5 GB leak."""
        report = self._boot(HEALTH_FALLBACK, rmtree_ok=False)
        self.assertIs(report.data_dir_removed, False)
        self.assertTrue(any("could not be removed" in r for r in report.reasons))
        self.assertIn("FusionRpgSmokeData-", report.data_dir)

    def test_a_CLEAN_run_reports_both_removals(self) -> None:
        report = self._boot(HEALTH_FALLBACK)
        self.assertIs(report.data_dir_removed, True)
        self.assertIs(report.process_stopped, True)

    def test_a_process_that_WILL_NOT_stop_is_reported_as_STILL_RUNNING(self) -> None:
        """`_stop_tree` is the function that decides, so the case drives IT rather than building a
        `Report` with the flag set by hand -- which is what the first version did, and which left the
        function's own failure path untested.
        """

        class Stubborn:
            pid = 5150

            def poll(self):
                return None

            def terminate(self):
                raise OSError("access denied")

            def wait(self, timeout=None):
                raise subprocess.TimeoutExpired(cmd="server", timeout=timeout or 1)

            def kill(self):
                raise OSError("access denied")

        def tasklist(cmd, **kwargs):
            return subprocess.CompletedProcess(cmd, 1, "", "ERROR: not found")

        with mock.patch.object(smoke, "_RUN", tasklist):
            self.assertFalse(smoke._stop_tree(Stubborn()),
                             "a process that cannot be terminated was reported as stopped")

    def test_a_process_that_EXITS_ON_ITS_OWN_counts_as_stopped(self) -> None:
        """The smoke did not have to kill it, so that is a success and not a failure to report."""

        class AlreadyGone:
            pid = 5151

            def poll(self):
                return 0

        self.assertTrue(smoke._stop_tree(AlreadyGone()))

    def test_a_server_that_WILL_NOT_stop_is_REPORTED_and_FAILS_the_run(self) -> None:
        report = smoke.Report(pack_dir="x", process_stopped=False, data_dir_removed=True)
        self.assertFalse(report.ok)
        self.assertTrue(any("may still be running" in r for r in report.reasons))


class _Response:
    def __init__(self, body: str, status: int = 200, content_type: str = "application/json") -> None:
        self._body = body.encode("utf-8")
        self.status = status
        self.headers = {"Content-Type": content_type}

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class TheConfiguration(SeamGuard):
    def test_a_MISSING_pack_REFUSES_and_names_the_fix(self) -> None:
        ground = Ground(has_server=False, has_pack=False)
        self.addCleanup(ground.cleanup)
        with self.assertRaises(smoke.Refusal) as caught:
            smoke.execute(ground.root, ground.pack, True, None, 5, 60, 60, ground.summary)
        self.assertEqual(caught.exception.reason, "PACK-DIR-MISSING")
        self.assertIn("publish_player.py", caught.exception.detail,
                      "the refusal must name what to run, not just that a directory is missing")

    def test_a_MISSING_probe_project_REFUSES(self) -> None:
        ground = Ground(has_server=False, has_project=False)
        self.addCleanup(ground.cleanup)
        with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
            with self.assertRaises(smoke.Refusal) as caught:
                smoke.execute(ground.root, ground.pack, True, None, 5, 60, 60, ground.summary)
        self.assertEqual(caught.exception.reason, "PROBE-PROJECT-MISSING")

    def test_a_MISSING_server_exe_is_a_named_STEP_failure_not_a_refusal(self) -> None:
        """The original recorded it as a failed step and carried on, which is right: a pack with no
        server still has probe results worth reporting."""
        ground = Ground(has_server=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "", "")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                report = smoke.execute(ground.root, ground.pack, False, None, 5, 60, 60, ground.summary)
        self.assertFalse(report.ok)
        self.assertIn("Missing Server\\FusionRpg.Server.exe", report.server.message)

    def test_a_PORT_that_is_TAKEN_REFUSES_rather_than_colliding(self) -> None:
        """The original picked a port at random and could not be told otherwise, so a collision
        surfaced as a health timeout against somebody else's server. This repository's rule is that a
        port is configuration, so a taken one is named before anything is started."""
        ground = Ground(has_server=True)
        self.addCleanup(ground.cleanup)
        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "", "")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with _TakenPort() as taken:
            with mock.patch.object(smoke, "_RUN", run):
                with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                    with mock.patch.object(smoke, "_POPEN",
                                           side_effect=AssertionError("nothing may be started")):
                        with self.assertRaises(smoke.Refusal) as caught:
                            smoke.execute(ground.root, ground.pack, False, taken, 5, 60, 60,
                                          ground.summary)
        self.assertEqual(caught.exception.reason, "PORT-IN-USE")
        self.assertIn("--port", caught.exception.detail)

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_work(self) -> None:
        for flag in ("--boot-timeout", "--build-timeout", "--probe-timeout"):
            with self.subTest(flag=flag):
                out, err = io.StringIO(), io.StringIO()
                with redirect_stdout(out), redirect_stderr(err):
                    code = smoke.main([flag, "0", "--json"])
                self.assertEqual(code, smoke.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_EVERY_dotnet_call_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        seen: list[dict] = []
        ground = Ground(has_server=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "", "")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                smoke.execute(ground.root, ground.pack, True, None, 5, 77, 88, ground.summary)
        self.assertGreaterEqual(len(seen), 2, f"expected a build and a run, saw {len(seen)}")
        for call in seen:
            self.assertIsNotNone(call["kwargs"].get("timeout"), f"unbounded: {call['cmd'][:2]}")
            self.assertIsNotNone(call["kwargs"].get("capture_output"))


class _TakenPort:
    """A port held OPEN for the duration of a case, so PORT-IN-USE is a real condition.

    The first version bound a socket, read its port and CLOSED it inside a `with`, so by the time the
    tool asked whether the port was in use it was free again and the refusal never fired. The case
    then failed for a reason that had nothing to do with the tool -- a fixture that stops existing
    before the thing under test looks at it.
    """

    def __init__(self) -> None:
        import socket
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._socket.bind(("127.0.0.1", 0))
        self.port = self._socket.getsockname()[1]

    def __enter__(self):
        return self.port

    def __exit__(self, *exc):
        self._socket.close()
        return False


class TheSummary(SeamGuard):
    def test_it_is_Written_and_SAYS_WHEN_it_was_not(self) -> None:
        ground = Ground(has_server=False)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "", "")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                report = smoke.execute(ground.root, ground.pack, True, None, 5, 60, 60, ground.summary)
        self.assertTrue(report.summary_written)
        written = json.loads(ground.summary.read_text(encoding="utf-8"))
        self.assertIn("utc", written)
        self.assertIn("dataDirRemoved", written, "the summary must record whether the data dir went")
        self.assertIn("processStopped", written, "and whether the server was stopped")

    def test_SKIP_SERVER_BOOT_records_a_SKIP_rather_than_a_PASS_it_did_not_check(self) -> None:
        ground = Ground(has_server=True)
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            if "build" in cmd:
                return subprocess.CompletedProcess(cmd, 0, "", "")
            return subprocess.CompletedProcess(cmd, 0, json.dumps(PROBE_OK), "")

        with mock.patch.object(smoke, "_RUN", run):
            with mock.patch.object(smoke, "_WHICH", return_value="C:\\dotnet.exe"):
                with mock.patch.object(smoke, "_POPEN",
                                       side_effect=AssertionError("the server must NOT be started")):
                    report = smoke.execute(ground.root, ground.pack, True, None, 5, 60, 60,
                                           ground.summary)
        self.assertTrue(report.ok)
        self.assertIn("Skipped", report.server.message,
                      "a skipped step must SAY it was skipped, not read as one that passed")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - smoke.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - smoke.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({smoke.EXIT_PASSED, smoke.EXIT_FAILED, smoke.EXIT_REFUSED}, {0, 1, 64})

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--pack-dir", "--skip-server-boot", "--port", "--summary-path", "--boot-timeout",
                     "--build-timeout", "--probe-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-PackDir", "-SkipServerBoot"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_does_NOT_change_the_process_WORKING_DIRECTORY(self) -> None:
        """`Set-Location $Root` changed the cwd for every other caller in the same process."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("os.chdir", source)
        self.assertNotIn("chdir(", source)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("smoke-player-pack.ps1", head)
        lowered = head.lower()
        for reason in ("malformed probe output", "swallowed", "bounded", "configuration"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_THREE_PROPERTIES_are_named_in_the_MODULE(self) -> None:
        """The assertions are the point of the tool, so they are pinned where a reader will find them."""
        source = SCRIPT.read_text(encoding="utf-8")
        for token in ("contentSource", "simEnabled", "eventCount", "api/test/snapshot"):
            self.assertIn(token, source, token)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib",
                          "ast", "re", "socket", "urllib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "smoke":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in global_modules:
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
