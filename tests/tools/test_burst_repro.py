"""Contract tests for `gk-core/scripts/burst_repro.py`.

A BURST REPRO, so the contract is about the synthetic fill it sends and the verdict it returns.

THE FILL IS THE POINT. The original's `New-Batch` mimics the real stress fill: per entity a
zombie.place + zombie.spawn (fat ~30-field dump) + stat.applied, then a zombie.die -- and the die's
pointer arithmetic is `0xA000 + n - 2`, killing the entity two indices back rather than the current
one. A port that "simplified" the die to the current index would send a fill the server never sees in
reality, and the repro would measure a shape that does not occur.

THE VERDICT IS THE OTHER POINT. The original had no `exit` statement anywhere: a run whose server died
mid-burst printed the result and exited 0, so "reports whether the server survives" was not true. The
port decides a verdict deliberately -- 0 only when the burst completed AND the server answered
/health afterwards AND (when it started the scratch server) the process was alive.

THE LEAKED PROCESS. `Start-Process` detaches, and the original's early `exit 1` on a failed startup
left the half-started server running, holding its port and data dir. The port kills the child on
every exit path, refusals included.

The transport and the process are substituted through the tool's PRIVATE seams (`_URLOPEN`,
`_POPEN`, `_SLEEP`), because those are process-wide modules: a test that patches any of them reaches
every other test in this project.
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
SCRIPT = Path(os.environ.get("BURST_REPRO_SCRIPT",
                             REPO / "scripts" / "burst_repro.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_burst_repro.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("burst_repro", SCRIPT)
burst = importlib.util.module_from_spec(_spec)
sys.modules["burst_repro"] = burst
_spec.loader.exec_module(burst)
_PRISTINE = {"_URLOPEN": burst._URLOPEN, "_POPEN": burst._POPEN, "_SLEEP": burst._SLEEP}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(burst, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(burst, name)!r}")


class ScriptedHttp:
    """A scripted `urllib` answer. `fail_posts` counts FLOOD posts that must raise (board.start is
    always answered -- the original let a board.start failure terminate the script, so the harness
    must not fail it); `down` makes every request fail (a server that is not answering)."""

    def __init__(self, fail_posts: int = 0, down: bool = False) -> None:
        self.fail_posts = fail_posts
        self.down = down
        self.posts: list[dict] = []
        self.health_checks = 0

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        if self.down:
            raise OSError(f"connection refused by the harness: {url}")
        if request.data is not None:
            body = json.loads(request.data.decode("utf-8"))
            if self.posts and self.fail_posts > 0:
                # flood posts only: the first post is board.start, which must succeed
                self.fail_posts -= 1
                raise OSError(f"connection reset by the harness: {url}")
            self.posts.append(body)
            return burst._Response({"ok": True})
        self.health_checks += 1
        return burst._Response({"ok": True})


class FakeProc:
    """What `_POPEN` must return: pid/poll/kill/wait, recording whether the kill happened."""

    def __init__(self) -> None:
        self.pid = 4242
        self.killed = False
        self.exited = False

    def poll(self) -> int | None:
        return 1 if self.exited else None

    def kill(self) -> None:
        self.killed = True

    def wait(self, timeout: float | None = None) -> int:
        return 1 if self.exited else 0


class TheBatch(SeamGuard):
    """The synthetic fill, preserved exactly -- including the die's `n - 2` pointer arithmetic."""

    def test_the_FOUR_kinds_cycle_by_n_mod_4(self) -> None:
        items = burst.build_batch(0, 8, "burst-x", "2026-01-01T00:00:00.000000Z")
        kinds = [i["kind"] for i in items]
        self.assertEqual(kinds, ["zombie.place", "zombie.spawn", "stat.applied", "zombie.die"] * 2)

    def test_every_event_carries_the_matchKey_and_the_batch_timestamp(self) -> None:
        items = burst.build_batch(0, 4, "burst-x", "2026-01-01T00:00:00.000000Z")
        for item in items:
            self.assertEqual(item["matchKey"], "burst-x")
            self.assertEqual(item["t"], "2026-01-01T00:00:00.000000Z")

    def test_the_place_carries_the_fat_payload(self) -> None:
        item = burst.build_batch(0, 1, "burst-x", "t")[0]
        self.assertEqual(item["payload"], {"ptr": f"B{0xA000:X}", "type": 0,
                                           "typeName": "Zed0", "row": 0, "theX": 7.5,
                                           "mindControlled": False, "withEffect": False})

    def test_the_spawn_carries_the_full_thirty_field_dump(self) -> None:
        item = burst.build_batch(1, 1, "burst-x", "t")[0]
        payload = item["payload"]
        self.assertEqual(payload["ptr"], f"B{0xA000 + 1:X}")
        self.assertEqual(payload["typeId"], 1)
        self.assertEqual(payload["side"], "zombie")
        self.assertEqual(payload["hp"], 500)
        self.assertEqual(payload["maxHp"], 500)
        self.assertEqual(payload["attack"], 20)
        self.assertEqual(payload["armor"], 100)
        self.assertEqual(payload["theSecondArmorHealth"], 0)
        self.assertEqual(payload["theSpeed"], 1.2)
        self.assertEqual(payload["source"], "debug.spawn")
        self.assertEqual(payload["displayName"], "Burst Zombie 1")
        self.assertEqual([payload[f"f{k}"] for k in range(1, 11)], list(range(1, 11)))

    def test_the_stat_applied_carries_the_projection_fanout(self) -> None:
        item = burst.build_batch(2, 1, "burst-x", "t")[0]
        self.assertEqual(item["payload"], {"ptr": f"B{0xA000 + 2:X}", "side": "zombie",
                                           "typeId": 2, "hpBefore": 500, "hpAfter": 25000,
                                           "maxBefore": 500, "maxAfter": 25000,
                                           "atkBefore": 20, "atkAfter": 200,
                                           "source": "debug.spawn"})

    def test_the_die_kills_the_entity_TWO_indices_back(self) -> None:
        """The original's `0xA000 + $n - 2`. A port that 'simplified' it to the current index would
        send a fill the server never sees in reality."""
        item = burst.build_batch(3, 1, "burst-x", "t")[0]
        self.assertEqual(item["payload"]["ptr"], f"B{0xA000 + 1:X}")
        self.assertEqual(item["payload"]["type"], 1)
        self.assertEqual(item["payload"]["typeName"], "Zed1")
        self.assertEqual(item["payload"]["reason"], 1)

    def test_the_pointer_is_uppercase_hex_without_prefix(self) -> None:
        item = burst.build_batch(0, 1, "burst-x", "t")[0]
        self.assertEqual(item["payload"]["ptr"], "BA000")
        item = burst.build_batch(0x10, 1, "burst-x", "t")[0]
        self.assertEqual(item["payload"]["ptr"], "BA010")

    def test_a_batch_STARTS_at_start_idx_and_its_types_follow_the_absolute_index(self) -> None:
        """n is start_idx + i, so a batch starting at 5 begins with n%4 == 1 (spawn)."""
        items = burst.build_batch(5, 4, "burst-x", "t")
        self.assertEqual([i["kind"] for i in items],
                         ["zombie.spawn", "stat.applied", "zombie.die", "zombie.place"])


class TheFlood(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.addCleanup(self._no_sleep, None)

    def _no_sleep(self, _):
        pass

    def _run_flood(self, **kwargs):
        with mock.patch.object(burst, "_URLOPEN", self.http), \
                mock.patch.object(burst, "_SLEEP", self._no_sleep):
            return burst.run_flood("http://127.0.0.1:5177", kwargs.get("events", 8),
                                   kwargs.get("batch", 4), "burst-x",
                                   kwargs.get("send_timeout", 10),
                                   kwargs.get("health_timeout", 3), 0.2)

    def test_board_start_is_POSTed_FIRST_and_alone(self) -> None:
        result = self._run_flood(events=8, batch=4)
        first = self.http.posts[0]
        self.assertEqual(len(first["events"]), 1)
        self.assertEqual(first["events"][0]["kind"], "board.start")
        self.assertEqual(first["events"][0]["payload"], {"levelName": "burst",
                                                         "matchKey": "burst-x"})
        self.assertNotIn("board.start", [i["kind"] for p in self.http.posts[1:]
                                         for i in p["events"]])

    def test_the_fill_is_POSTed_in_batches_of_the_batch_size(self) -> None:
        self._run_flood(events=10, batch=4)
        flood_posts = self.http.posts[1:]
        self.assertEqual([len(p["events"]) for p in flood_posts], [4, 4, 2])

    def test_a_FAILED_post_is_COUNTED_and_the_loop_CARRIES_ON(self) -> None:
        """The original's `catch` counted sendFailures and continued -- a burst that loses some
        requests to a server that then recovers is a different finding than a server that dies."""
        self.http = ScriptedHttp(fail_posts=2)
        result = self._run_flood(events=8, batch=4)
        self.assertEqual(result["sendFailures"], 2)
        # board.start succeeded; both flood posts failed and were counted, not raised
        self.assertEqual(len(self.http.posts), 1)
        self.assertEqual(result["events"], 8)

    def test_the_health_check_fires_every_four_batches(self) -> None:
        self._run_flood(events=32, batch=4)
        # 8 batches -> health checks after batch 4 and batch 8 (the `sent % (4 * batch) == 0` cadence)
        self.assertEqual(self.http.health_checks, 2)

    def test_a_FAILED_health_check_is_COUNTED_not_fatal(self) -> None:
        http = ScriptedHttp()
        real_get_health = burst.get_health

        def flapping(base_url, timeout):
            # fail every other health check
            if http.health_checks % 2 == 0:
                return None
            return real_get_health(base_url, timeout)

        with mock.patch.object(burst, "_URLOPEN", http), \
                mock.patch.object(burst, "_SLEEP", self._no_sleep), \
                mock.patch.object(burst, "get_health", flapping):
            result = burst.run_flood("http://127.0.0.1:5177", 32, 4, "burst-x", 10, 3, 0.2)
        self.assertGreaterEqual(result["healthFailures"], 1)
        self.assertEqual(result["events"], 32)

    def test_the_result_carries_the_original_fields(self) -> None:
        result = self._run_flood(events=8, batch=4)
        for field in ("events", "batch", "matchKey", "healthFailures", "sendFailures",
                      "elapsedMs"):
            self.assertIn(field, result, field)
        self.assertEqual(result["matchKey"], "burst-x")
        self.assertGreaterEqual(result["elapsedMs"], 0)


class TheScratchServer(SeamGuard):
    def setUp(self) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="burst-contract-")
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        self.exe = self.root / "dist" / "FusionRpg.Server" / "FusionRpg.Server.exe"
        self.exe.parent.mkdir(parents=True, exist_ok=True)
        self.exe.write_text("MZ", encoding="utf-8")
        self.out = self.root / "out.json"

    def _drive(self, http: ScriptedHttp, proc: FakeProc, argv: list[str]):
        popen_env: list[dict | None] = []

        def popen(cmd, **kwargs):
            popen_env.append(kwargs.get("env"))
            return proc

        with mock.patch.object(burst, "_URLOPEN", http), \
                mock.patch.object(burst, "_POPEN", popen), \
                mock.patch.object(burst, "_SLEEP", lambda _s: None):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = burst.main(argv)
        return code, popen_env, err.getvalue(), out.getvalue()

    def test_a_MISSING_executable_is_a_NAMED_refusal(self) -> None:
        self.exe.unlink()
        http = ScriptedHttp()
        code, popen_env, _, _ = self._drive(http, FakeProc(),
                                            ["--root", str(self.root), "--out", str(self.out)])
        self.assertEqual(code, burst.EXIT_REFUSED)
        self.assertEqual(popen_env, [], "no process was launched")
        self.assertTrue(self.out.exists() is False or self.out.read_text() == "")

    def test_the_child_gets_FUSIONRPG_URLS_and_DATA_for_ITSELF_only(self) -> None:
        http = ScriptedHttp()
        proc = FakeProc()
        code, popen_env, _, _ = self._drive(http, proc,
                                            ["--root", str(self.root), "--out", str(self.out),
                                             "--events", "8", "--batch", "4"])
        self.assertEqual(code, burst.EXIT_OK)
        child_env = popen_env[0]
        self.assertEqual(child_env["FUSIONRPG_URLS"], "http://127.0.0.1:5177")
        self.assertTrue(child_env["FUSIONRPG_DATA"].startswith(tempfile.gettempdir()))
        self.assertNotIn("FUSIONRPG_URLS", os.environ,
                         "the parent's environment must not carry the child's variables")

    def test_a_server_that_NEVER_answers_is_refused_and_KILLED(self) -> None:
        """The original's `exit 1` on a failed startup left the half-started server running, holding
        its port and data dir. The port kills the child on every exit path, refusals included."""
        http = ScriptedHttp(down=True)
        proc = FakeProc()
        code, _, _, out = self._drive(http, proc, ["--root", str(self.root), "--out", str(self.out),
                                                  "--json"])
        self.assertEqual(code, burst.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "SCRATCH-SERVER-FAILED")
        self.assertTrue(proc.killed, "the failed scratch server was leaked")

    def test_a_successful_run_KILLS_the_scratch_server_at_the_end(self) -> None:
        http = ScriptedHttp()
        proc = FakeProc()
        code, _, _, _ = self._drive(http, proc, ["--root", str(self.root), "--out", str(self.out),
                                                 "--events", "8", "--batch", "4"])
        self.assertEqual(code, burst.EXIT_OK)
        self.assertTrue(proc.killed, "the scratch server outlived the run")

    def test_the_result_file_is_written_BOM_less_UTF8(self) -> None:
        http = ScriptedHttp()
        proc = FakeProc()
        self._drive(http, proc, ["--root", str(self.root), "--out", str(self.out),
                                 "--events", "8", "--batch", "4"])
        raw = self.out.read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "a BOM would make the file unreadable to "
                                                         "consumers expecting plain UTF-8")
        payload = json.loads(raw.decode("utf-8"))
        self.assertEqual(payload["events"], 8)
        self.assertIs(payload["serverRespondingAfter"], True)
        self.assertIs(payload["processAlive"], True)


class TheVerdict(SeamGuard):
    """The original had no exit-code contract; the port decides one. 0 only when the burst completed
    AND the server answered /health afterwards AND (when it started the scratch server) the process
    was alive."""

    def setUp(self) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="burst-verdict-")
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        self.exe = self.root / "dist" / "FusionRpg.Server" / "FusionRpg.Server.exe"
        self.exe.parent.mkdir(parents=True, exist_ok=True)
        self.exe.write_text("MZ", encoding="utf-8")
        self.out = self.root / "out.json"

    def _drive(self, http: ScriptedHttp, proc: FakeProc, events: int = 8):
        def popen(cmd, **kwargs):
            return proc

        with mock.patch.object(burst, "_URLOPEN", http), \
                mock.patch.object(burst, "_POPEN", popen), \
                mock.patch.object(burst, "_SLEEP", lambda _s: None):
            err = io.StringIO()
            with redirect_stderr(err):
                code = burst.main(["--root", str(self.root), "--out", str(self.out),
                                   "--events", str(events), "--batch", "4"])
        return code, err.getvalue()

    def test_a_server_that_answers_after_the_burst_exits_0(self) -> None:
        code, _ = self._drive(ScriptedHttp(), FakeProc())
        self.assertEqual(code, burst.EXIT_OK)

    def test_a_server_that_DIES_after_the_burst_exits_1(self) -> None:
        """The original printed the result and exited 0 here -- a repro that cannot fail."""
        http = ScriptedHttp()
        proc = FakeProc()
        proc.exited = True  # the process died; /health still answers once via the seam
        code, _ = self._drive(http, proc)
        self.assertEqual(code, burst.EXIT_FAILED)

    def test_a_server_that_never_answers_at_the_end_exits_1(self) -> None:
        http = ScriptedHttp()
        proc = FakeProc()

        original_get_health = burst.get_health

        def dying(base_url, timeout):
            # The startup wait succeeds (no flood posts yet); the final liveness check fails.
            if len(http.posts) > 1:
                return None
            return original_get_health(base_url, timeout)

        def popen(cmd, **kwargs):
            return proc

        with mock.patch.object(burst, "_URLOPEN", http), \
                mock.patch.object(burst, "_POPEN", popen), \
                mock.patch.object(burst, "_SLEEP", lambda _s: None), \
                mock.patch.object(burst, "get_health", dying):
            err = io.StringIO()
            with redirect_stderr(err):
                code = burst.main(["--root", str(self.root), "--out", str(self.out),
                                   "--events", "8", "--batch", "4"])
        self.assertEqual(code, burst.EXIT_FAILED)
        self.assertIn("server responding after: False", err.getvalue())

    def test_sendFailures_are_REPORTED_and_never_FATAL(self) -> None:
        """A burst that loses some requests to a server that then recovers is a different finding
        than a server that dies -- the original counted them and carried on."""
        http = ScriptedHttp(fail_posts=3)
        code, err = self._drive(http, FakeProc(), events=12)
        self.assertEqual(code, burst.EXIT_OK)
        self.assertIn("sendFailures=3", err)

    def test_an_EXPLICIT_base_url_launches_no_process_and_exits_0(self) -> None:
        http = ScriptedHttp()
        proc = FakeProc()

        def popen(cmd, **kwargs):  # pragma: no cover - must never be called
            raise AssertionError("an explicit --base-url must not launch a scratch server")

        with mock.patch.object(burst, "_URLOPEN", http), \
                mock.patch.object(burst, "_POPEN", popen), \
                mock.patch.object(burst, "_SLEEP", lambda _s: None):
            err = io.StringIO()
            with redirect_stderr(err):
                code = burst.main(["--base-url", "http://127.0.0.1:5101",
                                   "--out", str(self.out), "--events", "8", "--batch", "4"])
        self.assertEqual(code, burst.EXIT_OK)
        payload = json.loads(self.out.read_text())
        self.assertIsNone(payload["processAlive"], "no process was started, so the field is null")


class TheRefusals(SeamGuard):
    def test_a_NON_POSITIVE_events_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = burst.main(["--events", "0", "--json"])
        self.assertEqual(code, burst.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-EVENTS")

    def test_a_NON_POSITIVE_batch_REFUSES(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = burst.main(["--batch", "0", "--json"])
        self.assertEqual(code, burst.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-BATCH")

    def test_an_INVALID_port_REFUSES(self) -> None:
        for port in (0, 65536, -1):
            with self.subTest(port=port):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = burst.main(["--port", str(port), "--json"])
                self.assertEqual(code, burst.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-PORT")

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        for flag in ("--send-timeout", "--health-timeout"):
            with self.subTest(flag=flag):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = burst.main([flag, "0", "--json"])
                self.assertEqual(code, burst.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = burst.main(["--events", "0"])
        self.assertEqual(code, burst.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - burst.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - burst.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(burst.EXIT_OK, 0)
        self.assertEqual(burst.EXIT_FAILED, 1)
        self.assertEqual(burst.EXIT_REFUSED, 64)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("burst-repro.ps1", head)
        lowered = head.lower()
        for reason in ("unbounded", "leak", "exit", "verdict"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--events", "--batch", "--port", "--out", "--send-timeout",
                     "--health-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Events", "-Batch", "-Port", "-OutLog"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib", "time"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "burst":
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
