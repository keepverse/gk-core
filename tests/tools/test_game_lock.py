"""Contract tests for `gk-core/scripts/game_lock.py`.

Asserts the contract: the CLI surface, the refusal vocabulary, the exit-code vocabulary, the `--json`
shape, and the properties the differential against `game-lock.ps1` CANNOT reach -- the two crash paths
the original had, the atomicity of `--acquire`, and path normalisation.

WHY THE TWO UNREADABLE CASES ARE THE POINT
`game-lock.ps1` ran `ConvertFrom-Json` on the lock file under `$ErrorActionPreference = 'Stop'`. A
partially-written lock -- which `--acquire` itself creates, because `CreateNew` makes the file before
anything writes it -- crashed the tool with an unhandled PowerShell error and no verdict. The port
refuses with a name and fails CLOSED. "Fails closed" is the load-bearing word: treating an unreadable
lock as FREE is how two sessions end up sharing one game install, so every case here that cannot read
the state asserts a NON-ZERO exit rather than a friendly one.

Every fixture writes a REAL lock file in a REAL temp install and drives the REAL tool. Nothing is mocked,
because the subject is a filesystem interaction and a mock of it would assert that the mock works.
"""
from __future__ import annotations

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

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("GAME_LOCK_SCRIPT", REPO / "scripts" / "game_lock.py")).resolve()

_spec = importlib.util.spec_from_file_location("game_lock", SCRIPT)
gl = importlib.util.module_from_spec(_spec)
sys.modules["game_lock"] = gl
_spec.loader.exec_module(gl)

RUN_TIMEOUT = 120

# The refusals, as a CLOSED vocabulary. A new reason must be added here, so the set is a contract rather
# than a growing pile of strings nothing compares against anything.
REFUSAL_REASONS = {
    "GAME-DIR-MISSING", "SESSION-REQUIRED", "LOCK-UNWRITABLE", "FILESYSTEM-ERROR",
    "LOCK-UNREADABLE", "LOCK-EMPTY", "LOCK-SHAPE",
    "RECORD-UNREADABLE", "RECORD-EMPTY", "RECORD-SHAPE",
}
# The successful answers, also closed.
VERDICTS = {"FREE", "HELD", "STALE", "OWN", "ACQUIRED", "RELEASED", "ALREADY-HELD", "UNLOCKED"}


class Install:
    """A throwaway game install plus a repo with session records. A failed delete FAILS the test."""

    def __init__(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="game-lock-contract-")
        self.root = Path(self._tmp.name)
        self.game = self.root / "install"
        self.game.mkdir()
        self.repo = self.root / "repo"
        (self.repo / "tasks" / "sessions").mkdir(parents=True)

    def record(self, session: str, status: str) -> Path:
        path = self.repo / "tasks" / "sessions" / f"{session}.json"
        path.write_text(json.dumps({"session": session, "status": status}), encoding="utf-8")
        return path

    def lock(self, session: str, *, record: Path | None = None, raw: str | None = None,
             acquired: str = "2026-01-01T00:00:00Z") -> Path:
        path = self.game / "fusionrpg-session.lock"
        if raw is not None:
            path.write_text(raw, encoding="utf-8")
            return path
        body = {"session": session, "acquiredAt": acquired}
        if record is not None:
            body["record"] = str(record)
        path.write_text(json.dumps(body), encoding="utf-8")
        return path

    @property
    def lock_file(self) -> Path:
        return self.game / "fusionrpg-session.lock"

    def close(self) -> None:
        self._tmp.cleanup()


class GameLockContract(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = Install()
        self.addCleanup(self.fixture.close)

    def invoke(self, *args: str) -> tuple[int, dict]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gl.main(["--game-dir", str(self.fixture.game), "--repo-root",
                            str(self.fixture.repo), "--json", *args])
        try:
            return code, json.loads(out.getvalue())
        except json.JSONDecodeError:
            self.fail(f"stdout was not a JSON document:\n{out.getvalue()}\n{err.getvalue()}")

    # --------------------------------------------------------------------------------------------
    # Status
    # --------------------------------------------------------------------------------------------
    def test_a_FREE_install_answers_FREE_and_0(self) -> None:
        code, payload = self.invoke("--status")
        self.assertEqual(code, gl.EXIT_OK)
        self.assertEqual(payload["verdict"], "FREE")
        self.assertIsNone(payload["holder"])

    def test_a_LIVE_holder_answers_HELD_and_1(self) -> None:
        record = self.fixture.record("other", "active")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--status")
        self.assertEqual(code, gl.EXIT_HELD)
        self.assertEqual(payload["verdict"], "HELD")
        self.assertEqual(payload["holder"], "other")

    def test_YOUR_OWN_lock_answers_OWN_and_0(self) -> None:
        record = self.fixture.record("me", "active")
        self.fixture.lock("me", record=record)
        code, payload = self.invoke("--status", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "OWN"))

    def test_an_ENDED_holder_answers_STALE_and_0(self) -> None:
        record = self.fixture.record("other", "merged")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--status")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "STALE"),
                         "a lock whose holder has ended must not block --status, or a finished session's "
                         "install stays unusable forever")

    def test_a_MISSING_record_answers_STALE_and_names_the_path_it_looked_for(self) -> None:
        absent = self.fixture.repo / "tasks" / "sessions" / "ghost.json"
        self.fixture.lock("ghost", record=absent)
        code, payload = self.invoke("--status")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "STALE"))
        self.assertEqual(payload["record"], str(absent),
                         "the answer must carry the path it decided from, or 'stale' is unfalsifiable")

    def test_a_lock_with_NO_record_field_falls_back_to_THIS_trees_record(self) -> None:
        """A lock written before the field existed. Falling back is the only available answer, and the
        fallback has to be the record the holder would have in the tree that wrote it."""
        self.fixture.record("other", "active")
        self.fixture.lock("other", record=None)
        code, payload = self.invoke("--status")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_HELD, "HELD"))

    # --------------------------------------------------------------------------------------------
    # Acquire
    # --------------------------------------------------------------------------------------------
    def test_acquiring_a_FREE_install_writes_the_lock_and_answers_ACQUIRED(self) -> None:
        self.fixture.record("me", "active")
        code, payload = self.invoke("--acquire", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "ACQUIRED"))
        self.assertTrue(self.fixture.lock_file.is_file())
        body = json.loads(self.fixture.lock_file.read_text(encoding="utf-8"))
        self.assertEqual(body["session"], "me")
        self.assertIn("acquiredAt", body)

    def test_the_record_path_is_SEPARATOR_AGNOSTIC(self) -> None:
        """The original built this with a hardcoded `tasks\\sessions\\...`. On POSIX that is a path with a
        literal backslash, the record never resolves, EVERY lock reads stale, and --acquire steals a live
        install. The stored path must therefore be one THIS TOOL CAN OPEN AGAIN.

        Asserted as openability plus the os-native join -- NOT as "contains no backslash". A Windows
        absolute path is made of backslashes; my first version asserted their absence and failed on the
        temp directory's own path, which says nothing about the tool. What matters is that the path is
        re-openable and ends in the platform's own `tasks/sessions/<session>.json`.
        """
        self.fixture.record("me", "active")
        self.invoke("--acquire", "--session", "me")
        body = json.loads(self.fixture.lock_file.read_text(encoding="utf-8"))
        stored = Path(body["record"])
        self.assertTrue(stored.is_file(),
                        f"the stored record path must be one this tool can open: {stored}")
        # Compared as RESOLVED paths, not as strings. On Windows `Path.resolve()` expands an 8.3 short
        # name -- `C:\Users\NENESC~1\...` -- so the stored text is not always the long spelling of the
        # directory the fixture created. That is cosmetic and harmless (the path opens), and a
        # string comparison here failed on it. What has to hold is that both name the SAME FILE.
        expected = self.fixture.repo / "tasks" / "sessions" / "me.json"
        self.assertEqual(stored.resolve(), expected.resolve(),
                         "the stored path must be the os-native join, so it resolves on every platform")
        # And a lock whose stored path does NOT resolve must read STALE, which is what makes the
        # separator a correctness property rather than a cosmetic one.
        self.fixture.lock("ghost", record=self.fixture.repo / "tasks" / "sessions" / "absent.json")
        _, payload = self.invoke("--status")
        self.assertEqual(payload["verdict"], "STALE")

    def test_acquiring_a_lock_YOU_hold_is_ALREADY_HELD_and_0(self) -> None:
        record = self.fixture.record("me", "active")
        self.fixture.lock("me", record=record)
        code, payload = self.invoke("--acquire", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "ALREADY-HELD"))

    def test_acquiring_over_a_LIVE_holder_REFUSES_and_does_NOT_delete_their_lock(self) -> None:
        record = self.fixture.record("other", "active")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--acquire", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_HELD, "HELD"))
        self.assertTrue(self.fixture.lock_file.is_file(), "a refused acquire must leave the lock intact")
        self.assertEqual(json.loads(self.fixture.lock_file.read_text())["session"], "other")

    def test_acquiring_over_an_ENDED_holder_takes_over_AND_REPORTS_WHOSE_it_took(self) -> None:
        """The takeover is the one action here that DISCARDS another session's claim, so it must be
        reported rather than inferred from the absence of an error."""
        record = self.fixture.record("other", "merged")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--acquire", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "ACQUIRED"))
        self.assertEqual(json.loads(self.fixture.lock_file.read_text())["session"], "me")
        self.assertTrue(any("other" in note for note in payload["notes"]),
                        f"a takeover must name whose lock it took: {payload['notes']}")

    def test_acquire_is_ATOMIC_and_exactly_one_of_many_wins(self) -> None:
        """`O_CREAT | O_EXCL` is the mechanism, so this asserts the kernel guarantee rather than the
        code: N processes race for one install and exactly one lock names one winner."""
        self.fixture.record("me", "active")
        self.fixture.record("other", "active")
        contenders = [sys.executable, str(SCRIPT), "--game-dir", str(self.fixture.game),
                      "--repo-root", str(self.fixture.repo), "--acquire", "--json"]
        procs = []
        for session in ("me", "other", "me2", "other2"):
            procs.append(subprocess.Popen(contenders + ["--session", session],
                                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True))
        results = []
        for proc in procs:
            out, _ = proc.communicate(timeout=RUN_TIMEOUT)
            results.append(json.loads(out))
        winners = [r for r in results if r.get("verdict") == "ACQUIRED"]
        losers = [r for r in results if r.get("verdict") == "HELD"]
        self.assertEqual(len(winners), 1, f"exactly one acquirer must win: {results}")
        self.assertEqual(len(losers), len(procs) - 1)
        self.assertEqual(json.loads(self.fixture.lock_file.read_text())["session"],
                         winners[0]["holder"])

    # --------------------------------------------------------------------------------------------
    # Release
    # --------------------------------------------------------------------------------------------
    def test_releasing_a_FREE_install_is_NOT_an_error(self) -> None:
        code, payload = self.invoke("--release", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "UNLOCKED"))

    def test_releasing_YOUR_OWN_lock_removes_it(self) -> None:
        record = self.fixture.record("me", "active")
        self.fixture.lock("me", record=record)
        code, payload = self.invoke("--release", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_OK, "RELEASED"))
        self.assertFalse(self.fixture.lock_file.exists())

    def test_releasing_SOMEONE_ELSES_lock_refuses_and_leaves_it(self) -> None:
        record = self.fixture.record("other", "active")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--release", "--session", "me")
        self.assertEqual((code, payload["verdict"]), (gl.EXIT_HELD, "HELD"))
        self.assertTrue(self.fixture.lock_file.is_file())

    # --------------------------------------------------------------------------------------------
    # The two crash paths the original had
    # --------------------------------------------------------------------------------------------
    def test_an_EMPTY_lock_file_is_a_NAMED_refusal_and_FAILS_CLOSED(self) -> None:
        """`--acquire` creates the file before anything writes it, so an empty lock is a real state a
        concurrent reader can observe. The original crashed here; the port names it and refuses."""
        self.fixture.lock("x", raw="")
        code, payload = self.invoke("--status")
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], "LOCK-EMPTY")
        self.assertNotEqual(code, gl.EXIT_OK, "an unreadable lock must never read as FREE")

    def test_UNPARSEABLE_JSON_is_a_DISTINCT_refusal_from_an_EMPTY_one(self) -> None:
        self.fixture.lock("x", raw="{ not json")
        code, payload = self.invoke("--status")
        self.assertEqual(payload["reason"], "LOCK-UNREADABLE")
        self.assertNotEqual(code, gl.EXIT_OK)
        self.assertIn("LOCK-EMPTY", REFUSAL_REASONS)

    def test_a_lock_that_is_not_an_OBJECT_is_refused_rather_than_coerced(self) -> None:
        self.fixture.lock("x", raw="[1, 2, 3]")
        _, payload = self.invoke("--status")
        self.assertEqual(payload["reason"], "LOCK-SHAPE")

    def test_an_UNREADABLE_SESSION_record_is_refused_and_FAILS_CLOSED(self) -> None:
        """The same crash one level down, with no diagnostic naming which file failed to parse."""
        record = self.fixture.repo / "tasks" / "sessions" / "other.json"
        record.write_text("{ truncated", encoding="utf-8")
        self.fixture.lock("other", record=record)
        code, payload = self.invoke("--status")
        self.assertEqual(payload["reason"], "RECORD-UNREADABLE")
        self.assertIn("RECORD-UNREADABLE", REFUSAL_REASONS)
        self.assertNotEqual(code, gl.EXIT_OK,
                            "a holder whose liveness cannot be read must not be reported as stale -- that "
                            "is the direction that lets two sessions into one install")

    # --------------------------------------------------------------------------------------------
    # Refusals
    # --------------------------------------------------------------------------------------------
    def test_a_MISSING_game_dir_REFUSES_rather_than_creating_a_lock_in_the_cwd(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gl.main(["--game-dir", str(self.fixture.root / "nope"), "--repo-root",
                            str(self.fixture.repo), "--status"])
        self.assertEqual(code, gl.EXIT_REFUSED)
        self.assertIn("GAME-DIR-MISSING", err.getvalue())

    def test_acquire_or_release_WITHOUT_a_session_REFUSES_by_name(self) -> None:
        for action in ("--acquire", "--release"):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = gl.main(["--game-dir", str(self.fixture.game), "--repo-root",
                                str(self.fixture.repo), action, "--json"])
            self.assertEqual(code, gl.EXIT_REFUSED, action)
            self.assertEqual(json.loads(out.getvalue())["reason"], "SESSION-REQUIRED", action)
            self.assertFalse(self.fixture.lock_file.exists(), action)

    def test_a_WHITESPACE_ONLY_session_is_REFUSED(self) -> None:
        _, payload = self.invoke("--acquire", "--session", "   ")
        self.assertEqual(payload["reason"], "SESSION-REQUIRED")

    def test_CALLER_ERROR_and_HELD_by_a_LIVE_session_are_DIFFERENT_exit_codes(self) -> None:
        """The original returned 1 for both, so a deploy reported "someone else is probing this install"
        for a typo. Splitting them is the reason this tool has four exit codes and not two."""
        _, err = io.StringIO(), io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(err):
            caller_error = gl.main(["--game-dir", str(self.fixture.root / "nope"), "--status"])
        record = self.fixture.record("other", "active")
        self.fixture.lock("other", record=record)
        code, _ = self.invoke("--status")
        self.assertNotEqual(caller_error, code)
        self.assertEqual((caller_error, code), (gl.EXIT_REFUSED, gl.EXIT_HELD))

    def test_ACQUIRE_and_RELEASE_together_is_a_USAGE_error(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--game-dir", str(self.fixture.game),
                               "--acquire", "--release", "--json"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("not allowed with argument", (proc.stdout + proc.stderr).lower())

    # --------------------------------------------------------------------------------------------
    # Normalisation
    # --------------------------------------------------------------------------------------------
    def test_two_SPELLINGS_of_one_INSTALL_are_ONE_lock(self) -> None:
        """`install` and `./install` are the same directory and must not be two locks."""
        record = self.fixture.record("me", "active")
        self.fixture.lock("me", record=record)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gl.main(["--game-dir", str(self.fixture.game / "."), "--repo-root",
                            str(self.fixture.repo), "--status", "--session", "me", "--json"])
        self.assertEqual((code, json.loads(out.getvalue())["verdict"]), (gl.EXIT_OK, "OWN"))

    # --------------------------------------------------------------------------------------------
    # Surface and shape
    # --------------------------------------------------------------------------------------------
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--acquire", "--release", "--status", "--game-dir", "--session",
                     "--repo-root", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        """`--game-dir` is passed, because `--game-dir` is REQUIRED and argparse reports the missing
        argument before it reports an unknown one. Without it this case asserted "no unrecognized
        arguments" against a run that had failed for a different reason -- and would have passed for a
        tool that accepted every flag.
        """
        for flag, value in (("-Status", ""), ("-Acquire", ""), ("-GameDir", "x"), ("-Session", "s")):
            proc = subprocess.run([sys.executable, str(SCRIPT), "--game-dir", str(self.fixture.game),
                                   flag, value],
                                  capture_output=True, text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_a_json_REFUSAL_prints_NO_prose_and_a_TEXT_REFUSAL_prints_NO_json(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            gl.main(["--game-dir", str(self.fixture.root / "nope"), "--repo-root",
                     str(self.fixture.repo), "--status", "--json"])
        self.assertEqual(err.getvalue(), "", "a --json refusal must not also print prose on stderr")
        out2, err2 = io.StringIO(), io.StringIO()
        with redirect_stdout(out2), redirect_stderr(err2):
            gl.main(["--game-dir", str(self.fixture.root / "nope"), "--repo-root",
                     str(self.fixture.repo), "--status"])
        self.assertEqual(out2.getvalue(), "", "a text refusal must not print a json document on stdout")
        self.assertIn("GAME-DIR-MISSING", err2.getvalue())

    def test_the_envelope_carries_KEYS_and_nothing_that_ROTS(self) -> None:
        self.fixture.record("me", "active")
        _, payload = self.invoke("--acquire", "--session", "me")
        self.assertEqual(set(payload), {"tool", "verdict", "ok", "exit", "holder", "acquired_at",
                                       "record", "lock_file", "message", "notes"})
        for banned in ("pid", "duration", "elapsed", "started"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run, so nothing can assert on it")

    def test_the_verdicts_are_a_CLOSED_vocabulary(self) -> None:
        """Every verdict the tool can emit, read from the code, is in the set above. A new one must be
        added there -- otherwise `notes`-carrying states grow without anything comparing them."""
        found = set(re.findall(r'Answer\(\s*"([A-Z-]+)"', SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no verdicts found at all")
        self.assertEqual(found - VERDICTS, set(),
                         f"undocumented verdict(s) {sorted(found - VERDICTS)}")

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        found = set(re.findall(r'Refusal\(\s*"([A-Z-]+)"', SCRIPT.read_text(encoding="utf-8")))
        found |= {f"{prefix}-{suffix}" for prefix in ("LOCK", "session record")
                  for suffix in ("UNREADABLE", "EMPTY", "SHAPE")} & found
        found |= set(re.findall(r'"(GAME-DIR-MISSING|SESSION-REQUIRED|LOCK-UNWRITABLE|FILESYSTEM-ERROR)"',
                                SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - REFUSAL_REASONS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({gl.EXIT_OK, gl.EXIT_HELD, gl.EXIT_REFUSED, gl.EXIT_UNREADABLE},
                         {0, 1, 2, 3})

    def test_the_lock_is_created_with_O_EXCL_and_not_with_a_check_then_create(self) -> None:
        """A `Test-Path` then `Create` is a race, and the original had the atomic form while a reader
        of its code would not be sure. Asserted structurally on the flags, because the property is the
        atomicity and not the spelling."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("os.O_CREAT | os.O_EXCL", source)
        self.assertNotIn("os.O_CREAT | os.O_WRONLY | os.O_TRUNC",
                         source.split("def acquire(")[1].split("def ")[0],
                         "O_TRUNC would let two acquirers both succeed")

    def test_the_module_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        """Provenance must survive the deletion: a reader must be able to learn what the original got
        wrong without the original."""
        doc = SCRIPT.read_text(encoding="utf-8")
        head = doc.split('"""')[1]
        self.assertIn("game-lock.ps1", head, "the docstring must name what it replaced")
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        for reason in ("UNPARSEABLE", "backslash"):
            self.assertIn(reason, head, f"the docstring omits the {reason} defect")

    def test_it_shells_out_to_NOTHING(self) -> None:
        """A lock is a file operation. Any subprocess here would be a lock acquisition with a network
        dependency and a timeout, which is a different tool."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("subprocess", source)


if __name__ == "__main__":
    unittest.main()
