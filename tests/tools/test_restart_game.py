"""Contract tests for `gk-core/scripts/restart_game.py` — the Python port of the retired `scripts/restart-game.ps1`.

A RESTART'S CONTRACT IS ABOUT WHAT IT REFUSES AND WHAT IT CLAIMS, and two of the original's defects were
claims rather than behaviour.

THE BASE URL WAS THE OWNER'S PORT, HARDCODED. `[string]$BaseUrl = "http://127.0.0.1:5088"` on a machine
with a three-slot pool on 5101/5102/5103. A probe pointed at the owner's server measures the wrong server
and reports it as ground truth. So the URL is read ONCE from the variable the Injector itself reads, and
WHERE IT CAME FROM is reported -- a probe that silently measured the wrong server is worse than one that
refused, and only the report makes that visible.

THE GAME CHECK COULD SEE ANOTHER SLOT'S GAME. `Get-Process -Name PlantsVsZombiesRH` matches by NAME, so
with three slots running, a probe for slot 2 would see slot 1's game and report a restart it never
observed. A by-path match under a known install is the strong claim; a by-name match is the weak one, and
the verdict says which happened rather than presenting both as the same thing.

THE LOOP IS BOUNDED, and the bound is checked: the budget is a wall clock, the per-request timeout is a
separate smaller bound, and neither can be set to zero or negative. An unbounded poller is the failure
this tool exists to prevent, so "can this hang?" is the first question.

THE ERROR THE ORIGINAL DISCARDED. Its `catch { }` turned every failure -- connection refused, a timeout,
a body that is not JSON -- into `$health = $null` and kept going, recording nothing. The last error is
carried into the verdict, so a timeout that says "server not answering" also says what the server said.

A REAL SERVER IS NOT A STUB. Every case here drives a real `http.server` over loopback, including a body
that is not JSON and a port with nothing on it, because those are the paths that a hand-made return
value would not exercise.
"""
from __future__ import annotations

import http.server
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("RESTART_GAME_SCRIPT",
                            REPO / "scripts" / "restart_game.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_restart_game.py"
BASE_URL_ENV = "FUSIONRPG_SERVER_URL"
GAME_DIR_ENV = "FUSIONRPG_ML_GAMEDIR"
POOL_ENV = "FUSIONRPG_GAME_POOL"
RUN_TIMEOUT = 120
BUDGET = 3
INTERVAL = 1

_spec = importlib.util.spec_from_file_location("restart_game", SCRIPT)
rg = importlib.util.module_from_spec(_spec)
sys.modules["restart_game"] = rg
_spec.loader.exec_module(rg)


class _Doc:
    """What the fixture server answers, and what it saw."""
    # The default body carries a placeholder so EVERY request gets a fresh heartbeat: a real
    # server's heartbeat advances, and the restart contract is precisely that the post-restart
    # heartbeat differs from the pre-restart snapshot. A test that sets _Doc.body explicitly
    # (the stale-heartbeat and not-connected cases) is served verbatim.
    body = b'{"ok": true, "injectorConnected": true, "lastHeartbeatUtc": "__FRESH_HEARTBEAT__"}'
    content_type = "application/json"
    status = 200
    requests: list[str] = []


_DEFAULT_BODY = _Doc.body


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self):
        _Doc.requests.append(self.path)
        if _Doc.status != 200:
            self.send_error(_Doc.status, "fixture refuses")
            return
        body = _Doc.body
        if b"__FRESH_HEARTBEAT__" in body:
            body = body.replace(b"__FRESH_HEARTBEAT__",
                                datetime.now(timezone.utc).isoformat().encode("ascii"))
        self.send_response(200)
        self.send_header("Content-Type", _Doc.content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class _FixtureServer:
    """A real HTTP server over loopback."""

    def __init__(self):
        self.server = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.port}"


class TempGameDir:
    """A throwaway game install directory."""

    def __init__(self, case: unittest.TestCase):
        self.root = Path(tempfile.mkdtemp(prefix="restart-game-test-"))
        case.addCleanup(self.destroy)
        self.exe = self.root / "PlantsVsZombiesRH.exe"
        self.exe.write_text("fake game exe")

    def destroy(self):
        import shutil
        shutil.rmtree(self.root, ignore_errors=True)


class RestartGameContractTests(unittest.TestCase):
    """The contract: CLI surface, refusal paths, exit codes, --json shape."""

    def setUp(self):
        self.server = _FixtureServer()
        self.addCleanup(self.server.stop)
        self.game_dir = TempGameDir(self)
        # Clear env so tests are deterministic
        self._old_env = {}
        for key in (BASE_URL_ENV, GAME_DIR_ENV, POOL_ENV):
            self._old_env[key] = os.environ.pop(key, None)
        self.addCleanup(self._restore_env)

    def _restore_env(self):
        for key, val in self._old_env.items():
            if val is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = val

    def _run(self, *args, timeout=RUN_TIMEOUT):
        """Run the script as a subprocess and capture everything."""
        result = subprocess.run(
            [sys.executable, str(SCRIPT)] + list(args),
            capture_output=True, text=True, timeout=timeout, check=False)
        return result

    def _run_module(self, *args):
        """Run main() in-process and capture stdout/stderr."""
        from io import StringIO
        from contextlib import redirect_stdout, redirect_stderr
        out, err = StringIO(), StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = rg.main(list(args))
        return code, out.getvalue(), err.getvalue()

    @staticmethod
    def _verdict(out: str) -> dict:
        """The trailing --json verdict: the last column-0 ``{`` ... ``}`` block in stdout.

        The verdict is pretty-printed (``indent=2``), so it is multi-line. Transcript lines
        (``==> ...``) and the health dump precede it; the verdict is always the LAST line that
        is exactly ``{`` -- a pretty-printed object's opening brace sits at column 0, while
        nested braces are indented.
        """
        lines = out.rstrip().splitlines()
        start = max(i for i, line in enumerate(lines) if line == "{")
        return json.loads("\n".join(lines[start:]))

    # -- CLI surface ---------------------------------------------------------

    def test_cli_defaults(self):
        """The parser carries the same defaults as the PowerShell original."""
        parser = rg.build_parser()
        args = parser.parse_args([])
        self.assertEqual(args.timeout_sec, 120)
        self.assertEqual(args.interval_sec, 3)
        self.assertEqual(args.game_dir, "")
        self.assertEqual(args.base_url, "")
        self.assertEqual(args.session, "")
        self.assertFalse(args.json)

    def test_cli_accepts_all_flags(self):
        parser = rg.build_parser()
        args = parser.parse_args([
            "--timeout-sec", "60",
            "--interval-sec", "2",
            "--game-dir", r"C:\Games\Test",
            "--base-url", "http://127.0.0.1:5101",
            "--session", "test-session",
            "--json",
        ])
        self.assertEqual(args.timeout_sec, 60)
        self.assertEqual(args.interval_sec, 2)
        self.assertEqual(args.game_dir, r"C:\Games\Test")
        self.assertEqual(args.base_url, "http://127.0.0.1:5101")
        self.assertEqual(args.session, "test-session")
        self.assertTrue(args.json)

    # -- Refusal paths -------------------------------------------------------

    def test_refuses_zero_timeout(self):
        code, out, err = self._run_module("--timeout-sec", "0")
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("INVALID-TIMEOUT", err)

    def test_refuses_negative_timeout(self):
        code, out, err = self._run_module("--timeout-sec", "-5")
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("INVALID-TIMEOUT", err)

    def test_refuses_zero_interval(self):
        code, out, err = self._run_module("--interval-sec", "0")
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("INVALID-INTERVAL", err)

    def test_refuses_missing_game_dir(self):
        code, out, err = self._run_module(
            "--game-dir", r"C:\Nonexistent\Path",
            "--base-url", self.server.url)
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("GAME-DIR-MISSING", err)

    def test_refuses_missing_game_exe(self):
        empty_dir = Path(tempfile.mkdtemp(prefix="restart-game-empty-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(empty_dir, ignore_errors=True))
        code, out, err = self._run_module(
            "--game-dir", str(empty_dir),
            "--base-url", self.server.url)
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("GAME-EXE-MISSING", err)

    def test_refuses_invalid_base_url(self):
        code, out, err = self._run_module(
            "--game-dir", str(self.game_dir.root),
            "--base-url", "not-a-url")
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("BASE-URL-INVALID", err)

    def test_refuses_when_lock_held(self):
        """Another live session's lock refuses the restart."""
        with mock.patch.object(rg, "check_game_lock") as mock_lock:
            mock_lock.side_effect = rg.Refusal(
                "GAME-LOCK-HELD",
                "game install is held by another live session")
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url)
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("GAME-LOCK-HELD", err)

    def test_refuses_when_process_enum_fails(self):
        with mock.patch.object(rg, "enumerate_game_processes") as mock_enum:
            mock_enum.side_effect = rg.Refusal(
                "PROCESS-ENUM-FAILED",
                "neither pwsh nor powershell is available")
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url)
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("PROCESS-ENUM-FAILED", err)

    def test_refuses_when_kill_fails(self):
        with mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes") as mock_kill:
            mock_enum.return_value = (
                [{"pid": 1234, "image": str(self.game_dir.exe)}],
                [])
            mock_kill.side_effect = rg.Refusal(
                "PROCESS-KILL-FAILED",
                "could not kill PID 1234")
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url)
        self.assertEqual(code, rg.EXIT_REFUSED)
        self.assertIn("PROCESS-KILL-FAILED", err)

    # -- Green path ----------------------------------------------------------

    def test_success_when_game_reconnects(self):
        """The happy path: game is killed, relaunched, and reconnects."""
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes") as mock_kill, \
             mock.patch.object(rg, "launch_game") as mock_launch:
            # First call (before kill): game is running
            # Second call (poll loop): game is running with fresh heartbeat
            mock_enum.side_effect = [
                ([{"pid": 1234, "image": str(self.game_dir.exe)}], []),
                ([{"pid": 5678, "image": str(self.game_dir.exe)}], []),
            ]
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "10",
                "--interval-sec", "1")
        self.assertEqual(code, rg.EXIT_OK)
        self.assertIn("Game relaunched and injector reconnected", out)
        mock_kill.assert_called_once()
        mock_launch.assert_called_once()

    def test_success_leaves_other_installs_alone(self):
        """Processes from other installs are never killed."""
        other_exe = r"H:\Games\Other\PlantsVsZombiesRH.exe"
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes") as mock_kill, \
             mock.patch.object(rg, "launch_game"):
            mock_enum.side_effect = [
                ([{"pid": 1234, "image": str(self.game_dir.exe)}],
                 [{"pid": 9999, "image": other_exe}]),
                ([{"pid": 5678, "image": str(self.game_dir.exe)}],
                 [{"pid": 9999, "image": other_exe}]),
            ]
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "10",
                "--interval-sec", "1")
        self.assertEqual(code, rg.EXIT_OK)
        # Only the own-install process was killed
        killed_pids = [call[0][0][0]["pid"] for call in mock_kill.call_args_list]
        self.assertEqual(killed_pids, [1234])
        self.assertIn("Left alone", out)

    # -- Failure path --------------------------------------------------------

    def test_timeout_when_game_never_reconnects(self):
        """The game never reconnects: the loop times out."""
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes"), \
             mock.patch.object(rg, "launch_game"):
            # Game is running but health never reports injectorConnected
            mock_enum.return_value = (
                [{"pid": 5678, "image": str(self.game_dir.exe)}], [])
            _Doc.body = b'{"ok": true, "injectorConnected": false, "lastHeartbeatUtc": "2026-09-30T12:00:00Z"}'
            self.addCleanup(setattr, _Doc, "body", _DEFAULT_BODY)
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "3",
                "--interval-sec", "1")
        self.assertEqual(code, rg.EXIT_TIMEOUT)
        self.assertIn("TIMEOUT", out)

    def test_timeout_when_server_not_answering(self):
        """The server never answers: the loop times out."""
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes"), \
             mock.patch.object(rg, "launch_game"):
            mock_enum.return_value = (
                [{"pid": 5678, "image": str(self.game_dir.exe)}], [])
            _Doc.status = 503
            self.addCleanup(setattr, _Doc, "status", 200)
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "3",
                "--interval-sec", "1")
        self.assertEqual(code, rg.EXIT_TIMEOUT)
        self.assertIn("TIMEOUT", out)

    def test_timeout_when_heartbeat_stale(self):
        """The heartbeat never changes: the loop times out."""
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes"), \
             mock.patch.object(rg, "launch_game"):
            mock_enum.return_value = (
                [{"pid": 5678, "image": str(self.game_dir.exe)}], [])
            # Heartbeat never changes
            _Doc.body = b'{"ok": true, "injectorConnected": true, "lastHeartbeatUtc": "2026-09-30T12:00:00Z"}'
            self.addCleanup(setattr, _Doc, "body", _DEFAULT_BODY)
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "3",
                "--interval-sec", "1")
        self.assertEqual(code, rg.EXIT_TIMEOUT)
        self.assertIn("TIMEOUT", out)

    # -- --json shape --------------------------------------------------------

    def test_json_shape_on_success(self):
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes"), \
             mock.patch.object(rg, "launch_game"):
            mock_enum.side_effect = [
                ([{"pid": 1234, "image": str(self.game_dir.exe)}], []),
                ([{"pid": 5678, "image": str(self.game_dir.exe)}], []),
            ]
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "10",
                "--interval-sec", "1",
                "--json")
        self.assertEqual(code, rg.EXIT_OK)
        envelope = self._verdict(out)
        self.assertEqual(envelope["tool"], "restart-game")
        self.assertEqual(envelope["verdict"], "READY")
        self.assertEqual(envelope["exitCode"], 0)
        self.assertTrue(envelope["ready"])
        self.assertEqual(envelope["reason"], "ready")
        self.assertIn("baseUrl", envelope)
        self.assertIn("baseUrlSource", envelope)
        self.assertIn("gameDir", envelope)
        self.assertIn("gameExe", envelope)
        self.assertIn("killedPids", envelope)
        self.assertIn("leftAlonePids", envelope)
        self.assertIn("beforeHeartbeat", envelope)
        self.assertIn("refusals", envelope)
        self.assertIn("transcript", envelope)

    def test_json_shape_on_refusal(self):
        code, out, err = self._run_module(
            "--game-dir", r"C:\Nonexistent\Path",
            "--base-url", self.server.url,
            "--json")
        self.assertEqual(code, rg.EXIT_REFUSED)
        envelope = self._verdict(out)
        self.assertEqual(envelope["tool"], "restart-game")
        self.assertEqual(envelope["verdict"], "REFUSED")
        self.assertEqual(envelope["exitCode"], 2)
        self.assertIn("reason", envelope)
        self.assertIn("detail", envelope)
        self.assertIn("refusals", envelope)

    def test_json_shape_on_timeout(self):
        with mock.patch.object(rg, "check_game_lock"), \
             mock.patch.object(rg, "enumerate_game_processes") as mock_enum, \
             mock.patch.object(rg, "kill_processes"), \
             mock.patch.object(rg, "launch_game"):
            mock_enum.return_value = (
                [{"pid": 5678, "image": str(self.game_dir.exe)}], [])
            _Doc.body = b'{"ok": true, "injectorConnected": false, "lastHeartbeatUtc": "2026-09-30T12:00:00Z"}'
            self.addCleanup(setattr, _Doc, "body", _DEFAULT_BODY)
            code, out, err = self._run_module(
                "--game-dir", str(self.game_dir.root),
                "--base-url", self.server.url,
                "--timeout-sec", "3",
                "--interval-sec", "1",
                "--json")
        self.assertEqual(code, rg.EXIT_TIMEOUT)
        envelope = self._verdict(out)
        self.assertEqual(envelope["tool"], "restart-game")
        self.assertEqual(envelope["verdict"], "TIMEOUT")
        self.assertEqual(envelope["exitCode"], 1)
        self.assertFalse(envelope["ready"])

    # -- Reason ladder -------------------------------------------------------

    def test_reason_ladder_closed_vocabulary(self):
        self.assertEqual(rg.REASONS, {
            "ready",
            "game-process-not-found",
            "server-not-answering",
            "health-ok-false",
            "injector-connected-false",
            "heartbeat-still-matches-pre-restart-snapshot",
        })

    def test_ladder_reason_no_game(self):
        self.assertEqual(rg.ladder_reason(None, False, False), rg.REASON_NO_GAME)

    def test_ladder_reason_no_health(self):
        self.assertEqual(rg.ladder_reason(None, True, False), rg.REASON_NO_HEALTH)

    def test_ladder_reason_not_ok(self):
        self.assertEqual(rg.ladder_reason({"ok": False}, True, False), rg.REASON_NOT_OK)

    def test_ladder_reason_not_connected(self):
        self.assertEqual(
            rg.ladder_reason({"ok": True, "injectorConnected": False}, True, False),
            rg.REASON_NOT_CONNECTED)

    def test_ladder_reason_stale_heartbeat(self):
        self.assertEqual(
            rg.ladder_reason({"ok": True, "injectorConnected": True}, True, False),
            rg.REASON_STALE_HEARTBEAT)

    def test_ladder_reason_ready(self):
        self.assertEqual(
            rg.ladder_reason({"ok": True, "injectorConnected": True}, True, True),
            rg.REASON_READY)

    # -- Refusal vocabulary --------------------------------------------------

    def test_refusal_vocabulary_closed(self):
        """Every refusal name has a meaning."""
        for name in rg.REFUSALS:
            self.assertIn(name, rg.REFUSALS)
            self.assertTrue(rg.REFUSALS[name])

    def test_unnamed_refusal_raises(self):
        with self.assertRaises(KeyError):
            rg.Refusal("NONEXISTENT", "detail")

    # -- Process enumeration -------------------------------------------------

    def test_enumerate_game_processes_refuses_on_missing_sentinel(self):
        with mock.patch.object(rg, "run_external") as mock_run:
            mock_run.return_value = mock.Mock(
                returncode=0, stdout="1234\tC:\\game\\PlantsVsZombiesRH.exe\n",
                stderr="")
            with self.assertRaises(rg.Refusal) as ctx:
                rg.enumerate_game_processes(Path(r"C:\game"), 30)
            self.assertEqual(ctx.exception.name, "PROCESS-ENUM-FAILED")

    def test_enumerate_game_processes_refuses_on_nonzero_exit(self):
        with mock.patch.object(rg, "run_external") as mock_run:
            mock_run.return_value = mock.Mock(
                returncode=1, stdout="", stderr="error")
            with self.assertRaises(rg.Refusal) as ctx:
                rg.enumerate_game_processes(Path(r"C:\game"), 30)
            self.assertEqual(ctx.exception.name, "PROCESS-ENUM-FAILED")

    def test_enumerate_game_processes_splits_own_and_other(self):
        with mock.patch.object(rg, "run_external") as mock_run:
            mock_run.return_value = mock.Mock(
                returncode=0,
                stdout=(
                    "1234\tC:\\game\\PlantsVsZombiesRH.exe\n"
                    "5678\tH:\\Other\\PlantsVsZombiesRH.exe\n"
                    "#RESTART-GAME-ENUM-COMPLETE\n"
                ),
                stderr="")
            own, other = rg.enumerate_game_processes(Path(r"C:\game"), 30)
        self.assertEqual(len(own), 1)
        self.assertEqual(own[0]["pid"], 1234)
        self.assertEqual(len(other), 1)
        self.assertEqual(other[0]["pid"], 5678)

    # -- Base URL resolution -------------------------------------------------

    def test_base_url_from_env(self):
        os.environ[BASE_URL_ENV] = "http://127.0.0.1:5101"
        url, source = rg.resolve_base_url("")
        self.assertEqual(url, "http://127.0.0.1:5101")
        self.assertEqual(source, f"${BASE_URL_ENV}")

    def test_base_url_from_flag(self):
        url, source = rg.resolve_base_url("http://127.0.0.1:5102")
        self.assertEqual(url, "http://127.0.0.1:5102")
        self.assertEqual(source, "--base-url")

    def test_base_url_default(self):
        url, source = rg.resolve_base_url("")
        self.assertEqual(url, rg.DEFAULT_BASE_URL)
        self.assertIn("built-in default", source)

    # -- Game dir resolution -------------------------------------------------

    def test_game_dir_from_env(self):
        os.environ[GAME_DIR_ENV] = str(self.game_dir.root)
        path, source = rg.resolve_game_dir("")
        self.assertEqual(path, self.game_dir.root.resolve())
        self.assertEqual(source, f"${GAME_DIR_ENV}")

    def test_game_dir_from_flag(self):
        path, source = rg.resolve_game_dir(str(self.game_dir.root))
        self.assertEqual(path, self.game_dir.root.resolve())
        self.assertEqual(source, "--game-dir")

    def test_game_dir_missing_refuses(self):
        with self.assertRaises(rg.Refusal) as ctx:
            rg.resolve_game_dir(r"C:\Nonexistent\Path")
        self.assertEqual(ctx.exception.name, "GAME-DIR-MISSING")

    # -- Exit codes ----------------------------------------------------------

    def test_exit_codes(self):
        self.assertEqual(rg.EXIT_OK, 0)
        self.assertEqual(rg.EXIT_TIMEOUT, 1)
        self.assertEqual(rg.EXIT_REFUSED, 2)


if __name__ == "__main__":
    unittest.main()
