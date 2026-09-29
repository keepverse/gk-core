"""Contract tests for `tasks/reports/f13_schema_upgrade_proof.py`.

A PROOF, so its contract is about honesty under three conditions rather than about a happy path:

  * the precondition. The owner's live `rpg-hot.sqlite` was migrated by a server boot and ALREADY
    carries `dungeon_domain.first_clear_ref`, so running the proof against it is meaningless. Reporting
    that is the honest answer, and the original had a line for it.
  * the reading. Both the `before:` and the `after:` line carry the same
    `first_clear_ref present=` fragment, so a check that searches the whole output cannot tell them
    apart -- and the first draft of this suite's sibling check passed by reading the BEFORE line as the
    AFTER one. The per-line readers are asserted directly.
  * the cleanup. The original removed its scratch directory in a `finally` under
    `$ErrorActionPreference = 'Stop'`, so a throw from the cleanup REPLACED the verdict: a proof that
    had already printed `PROOF OK` could exit non-zero because a temp directory would not delete.

The heavy work -- `dotnet run` over a 521 MB database -- is stubbed through the tool's private `_RUN`.
The REAL end-to-end proof, against the owner's actual file, is a separate harness; a suite that stubs
the thing under test is testing its own arithmetic.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("F13_PROOF_SCRIPT",
                             REPO / "tasks" / "reports" / "f13_schema_upgrade_proof.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_f13_schema_upgrade_proof.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("f13_schema_upgrade_proof", SCRIPT)
proof = importlib.util.module_from_spec(_spec)
sys.modules["f13_schema_upgrade_proof"] = proof
_spec.loader.exec_module(proof)
_PRISTINE_RUN = proof._RUN

PROVEN_OUTPUT = """before: tables=184 dungeon_domain has 15 columns, first_clear_ref present=False
pre-fix: SqliteException: SQLite Error 1: 'no such column: first_clear_ref'.
init: OK
ReadDomains -> 0 rows
after:  tables=184 dungeon_domain has 16 columns, first_clear_ref present=True
PROOF OK
"""

MIGRATED_OUTPUT = """before: tables=184 dungeon_domain has 16 columns, first_clear_ref present=True
FAIL: the copy already had the column; this is not an upgrade.
"""


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if proof._RUN is not _PRISTINE_RUN:
            self.fail(f"_RUN was still substituted after {self.id()}: {proof._RUN!r}")


class TheReading(SeamGuard):
    """`before:` and `after:` carry the same fragment, so the readers must be per-LINE.

    This is the assertion that would have caught the first draft's bug, where the after-check searched
    the whole output for `first_clear_ref present=True` and therefore matched the BEFORE line whenever
    the probe bailed early -- reporting OK for a state that was never observed.
    """

    def test_the_AFTER_reader_does_NOT_match_the_BEFORE_line(self) -> None:
        """The migrated output contains the string `first_clear_ref present=True` -- on its BEFORE
        line. A whole-output search for it therefore matches a probe that never got as far as an
        after state, and the after-check reports OK for something never observed."""
        self.assertIn("first_clear_ref present=True", MIGRATED_OUTPUT,
                      "the control changed: the string the bug matched on is gone")
        self.assertFalse(proof._reports(MIGRATED_OUTPUT, "after",
                                         "first_clear_ref present=True"),
                         "the after-reader matched the BEFORE line")

    def test_a_BAILED_probe_reports_NO_after_state(self) -> None:
        """The migrated output has a before line and no after line, and saying so is the difference
        between a failed check and a passing one."""
        self.assertFalse(proof._reports(MIGRATED_OUTPUT, "after",
                                        "first_clear_ref present=True"))
        self.assertIn("no 'after' line", proof._state(MIGRATED_OUTPUT, "after"))

    def test_the_BEFORE_reader_reads_the_BEFORE_line(self) -> None:
        self.assertTrue(proof._reports(PROVEN_OUTPUT, "before", "first_clear_ref present=False"))
        self.assertTrue(proof._reports(PROVEN_OUTPUT, "after", "first_clear_ref present=True"))

    def test_the_LINE_reader_will_not_match_a_line_that_merely_CONTAINS_the_prefix(self) -> None:
        """The real probe emits `FAIL: first_clear_ref still absent after Init` -- a line that CONTAINS
        `after` and is not the after-state. A substring reader takes it for the after line and reports a
        state the probe never reached.

        Found by falsification: `startswith` weakened to `in` survived every fixture, because none of
        them had a line with the word in the middle.
        """
        output = ("before: tables=184 dungeon_domain has 15 columns, first_clear_ref present=False\n"
                  "init: OK\n"
                  "FAIL: first_clear_ref still absent after Init\n")
        self.assertIn("after Init", output, "the control changed: the line the bug matched on is gone")
        self.assertIsNone(proof._line_for(output, "after"),
                          "a line merely CONTAINING the prefix was read as the after line")
        self.assertFalse(proof._reports(output, "after", "first_clear_ref present=True"))

    def test_the_STATE_reports_the_LINE_it_read(self) -> None:
        state = proof._state(PROVEN_OUTPUT, "before")
        self.assertIn("15 columns", state)
        self.assertIn("present=False", state)
        self.assertNotIn("16 columns", state, "the before-state must not quote the after line")


class TheVerdict(SeamGuard):
    """Driven through `execute` with a stubbed probe, so each verdict is reached deliberately."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="f13-contract-")
        self.addCleanup(self._tmp.cleanup)
        self.work = Path(self._tmp.name)
        (self.work / "src" / "FusionRpg.Data").mkdir(parents=True)
        (self.work / "src" / "FusionRpg.Data" / "FusionRpg.Data.csproj").write_text("<Project/>",
                                                                                    encoding="utf-8")
        self.source = self.work / "rpg-hot.sqlite"
        self._make_db(self.source, with_column=True)

    def _make_db(self, path: Path, with_column: bool) -> None:
        connection = sqlite3.connect(path)
        try:
            connection.execute("CREATE TABLE dungeon_domain (domain_id TEXT, name TEXT)")
            if with_column:
                connection.execute("ALTER TABLE dungeon_domain ADD COLUMN first_clear_ref TEXT")
            connection.commit()
        finally:
            connection.close()

    def drive(self, probe_output: str, probe_exit: int, downgrade: bool = False,
              rmtree_error: OSError | None = None) -> tuple[int, object]:
        def run(cmd, **kwargs):
            if cmd[1:3] == ["run", "-c"]:
                return subprocess.CompletedProcess(cmd, probe_exit, probe_output, "")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            if rmtree_error is not None:
                with mock.patch.object(proof, "_RMTREE", side_effect=rmtree_error):
                    return proof.execute(self.work, self.source, downgrade, 60)
            return proof.execute(self.work, self.source, downgrade, 60)

    def test_a_PROVEN_upgrade_is_OK_and_names_every_check(self) -> None:
        (report, verdict) = self.drive(PROVEN_OUTPUT, 0, downgrade=True)
        self.assertEqual(verdict, proof.EXIT_PROVEN)
        self.assertTrue(report.ok)
        self.assertEqual(report.failed_checks, [])
        self.assertEqual(len(report.checks), 4)

    def test_a_copy_that_ALREADY_had_the_column_is_FAILED_not_PROVEN(self) -> None:
        """The precondition. The original had a line for it: "FAIL: the copy already had the column;
        this is not an upgrade." A proof that cannot tell an upgrade from a no-op proves nothing."""
        (report, verdict) = self.drive(MIGRATED_OUTPUT, 1, downgrade=False)
        self.assertEqual(verdict, proof.EXIT_FAILED)
        self.assertFalse(report.ok)
        self.assertIn("the-copy-did-NOT-already-have-the-column", report.failed_checks)
        self.assertIn("the-column-is-present-after-Init", report.failed_checks,
                      "with no after line the after-check must FAIL, not silently pass")

    def test_a_probe_that_DID_NOT_finish_is_FAILED_with_its_checks(self) -> None:
        (report, verdict) = self.drive("before: tables=1 first_clear_ref present=False\n", 1)
        self.assertEqual(verdict, proof.EXIT_FAILED)
        self.assertIn("Init-completed", report.failed_checks)
        self.assertIn("the-probe-printed-PROOF-OK", report.failed_checks)

    def test_a_non_zero_probe_is_FAILED_EVEN_WHEN_every_named_check_PASSES(self) -> None:
        """The exit code is part of the verdict, not a separate signal.

        The first draft of this case asserted on a `the-probe-exited-zero` check that a guard in the
        tool was supposed to append -- and that guard was UNREACHABLE, because `Report.ok` already
        requires `probe_exit == 0`, so `verdict` was never `EXIT_PROVEN` when the probe failed. The dead
        guard is now removed and this case asserts the property that actually holds it.
        """
        (report, verdict) = self.drive(PROVEN_OUTPUT, 9, downgrade=True)
        self.assertEqual(verdict, proof.EXIT_FAILED)
        self.assertEqual(report.failed_checks, [],
                         "every named check passed, so the exit code is the ONLY thing failing -- which"
                         " is the property under test")
        self.assertEqual(report.probe_exit, 9)
        self.assertFalse(report.ok, "ok ignored a non-zero probe exit")

    def test_the_BACKUP_method_is_recorded(self) -> None:
        report, _ = self.drive(PROVEN_OUTPUT, 0, downgrade=True)
        self.assertEqual(report.backup_method, "online-backup")

    def test_a_FAILED_named_check_is_enough_to_FAIL_even_with_a_ZERO_exit(self) -> None:
        """`ok` must consult the named checks, not only the probe's exit code.

        Found by falsification: `ok` reduced to `probe_exit == 0` satisfied every case, because each
        failing fixture ALSO failed on the exit code. This one exits 0 while a named check fails --
        which is what a probe produces if it prints `PROOF OK` and never reached its after-state.
        """
        output = ("before: tables=184 dungeon_domain has 15 columns, first_clear_ref present=False\n"
                  "init: OK\n"
                  "PROOF OK\n")
        (report, verdict) = self.drive(output, 0, downgrade=True)
        self.assertEqual(verdict, proof.EXIT_FAILED,
                         "`ok` ignored the named checks and reported a proof")
        self.assertIn("the-column-is-present-after-Init", report.failed_checks)
        self.assertEqual(report.probe_exit, 0, "the exit code WAS zero; only a named check failed")

    def test_the_probe_is_handed_the_COPY_and_never_the_SOURCE(self) -> None:
        """A tool that pointed `RpgStore.Init()` at the owner's 521 MB file would migrate the real
        database as a side effect of proving it, and the proof would not be repeatable.

        Found by falsification: handing the probe the source's parent instead of the copy's survived
        every case, because nothing asserted the path the probe was actually given.
        """
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            if cmd[1:3] == ["run", "-c"]:
                return subprocess.CompletedProcess(cmd, 0, PROVEN_OUTPUT, "")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            report, _ = proof.execute(self.work, self.source, True, 60)
        probe = next(c for c in seen if c[1:3] == ["run", "-c"])
        data_dir_arg = Path(probe[probe.index("--") + 1]).resolve()
        self.assertNotEqual(data_dir_arg, self.source.parent.resolve(),
                            "the probe was handed the source directory, not the copy's")
        scratch = Path(report.scratch).resolve()
        self.assertTrue(data_dir_arg == scratch / "data" or scratch in data_dir_arg.parents,
                        f"the probe was handed {data_dir_arg}, which is not under {scratch}")

    def test_the_SOURCE_is_left_UNTOUCHED(self) -> None:
        """The proof copies; it must never write to the owner's 521 MB file. A tool that migrated the
        real database as a side effect of proving it could be migrated would be unrepeatable."""
        before = self.source.read_bytes()
        self.drive(PROVEN_OUTPUT, 0, downgrade=True)
        self.assertEqual(self.source.read_bytes(), before,
                         "the source database was modified by the proof")


class TheCleanup(SeamGuard):
    """The `finally` defect: a cleanup that raises REPLACES the verdict."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="f13-cleanup-")
        self.addCleanup(self._tmp.cleanup)
        self.work = Path(self._tmp.name)
        (self.work / "src" / "FusionRpg.Data").mkdir(parents=True)
        (self.work / "src" / "FusionRpg.Data" / "FusionRpg.Data.csproj").write_text("<Project/>",
                                                                                    encoding="utf-8")
        self.source = self.work / "rpg-hot.sqlite"
        connection = sqlite3.connect(self.source)
        try:
            connection.execute("CREATE TABLE dungeon_domain (domain_id TEXT)")
            connection.commit()
        finally:
            connection.close()

    def test_a_FAILED_cleanup_does_NOT_replace_a_PROVEN_verdict(self) -> None:
        """The exact original defect: `Remove-Item` in a `finally` under `$ErrorActionPreference = 'Stop'`
        throws on a held-open directory, and a throw from a `finally` discards what the body concluded."""

        def run(cmd, **kwargs):
            if cmd[1:3] == ["run", "-c"]:
                return subprocess.CompletedProcess(cmd, 0, PROVEN_OUTPUT, "")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            with mock.patch.object(proof, "_RMTREE", side_effect=OSError("file in use")):
                report, verdict = proof.execute(self.work, self.source, True, 60)
        self.assertEqual(verdict, proof.EXIT_PROVEN,
                         "a cleanup failure overwrote a verdict the proof had already reached")
        self.assertIs(report.scratch_removed, False, "the failure must still be REPORTED")
        self.assertTrue(report.scratch, "the undeleted path is recorded so it can be cleaned up")

    def test_a_CLEAN_run_reports_the_scratch_as_REMOVED(self) -> None:
        def run(cmd, **kwargs):
            if cmd[1:3] == ["run", "-c"]:
                return subprocess.CompletedProcess(cmd, 0, PROVEN_OUTPUT, "")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(proof, "_RUN", run):
            report, verdict = proof.execute(self.work, self.source, True, 60)
        self.assertIs(report.scratch_removed, True)
        self.assertFalse(Path(report.scratch).exists())


class TheDowngrade(SeamGuard):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="f13-downgrade-")
        self.addCleanup(self._tmp.cleanup)
        self.path = Path(self._tmp.name) / "rpg-hot.sqlite"
        connection = sqlite3.connect(self.path)
        try:
            connection.execute("CREATE TABLE dungeon_domain (domain_id TEXT, name TEXT)")
            connection.execute("INSERT INTO dungeon_domain VALUES ('d1', 'Keep')")
            connection.execute("ALTER TABLE dungeon_domain ADD COLUMN first_clear_ref TEXT")
            connection.execute("CREATE TABLE other_table (x TEXT)")
            connection.commit()
        finally:
            connection.close()

    def _columns(self, table: str) -> list[str]:
        connection = sqlite3.connect(self.path)
        try:
            return [r[1] for r in connection.execute(f'PRAGMA table_info("{table}")')]
        finally:
            connection.close()

    def test_it_removes_the_ADDED_column_and_keeps_the_ROWS(self) -> None:
        proof.downgrade_copy(self.path)
        self.assertNotIn("first_clear_ref", self._columns("dungeon_domain"))
        connection = sqlite3.connect(self.path)
        try:
            rows = list(connection.execute("SELECT domain_id, name FROM dungeon_domain"))
        finally:
            connection.close()
        self.assertEqual(rows, [("d1", "Keep")], "the downgrade dropped data")
        self.assertIn("x", self._columns("other_table"), "the downgrade touched another table")

    def test_a_database_ALREADY_without_the_column_is_left_ALONE(self) -> None:
        proof.downgrade_copy(self.path)
        before = self._columns("dungeon_domain")
        proof.downgrade_copy(self.path)
        self.assertEqual(self._columns("dungeon_domain"), before, "a second downgrade changed it again")

    def test_a_table_that_is_NOT_THERE_is_a_named_refusal_not_a_crash(self) -> None:
        connection = sqlite3.connect(self.path)
        try:
            connection.execute("DROP TABLE dungeon_domain")
            connection.commit()
        finally:
            connection.close()
        proof.downgrade_copy(self.path)          # nothing to do, returns
        self.assertNotIn("first_clear_ref", self._columns("other_table"))


class TheConfiguration(SeamGuard):
    def test_a_MISSING_source_REFUSES_by_name(self) -> None:
        with self.assertRaises(proof.Refusal) as caught:
            proof.resolve_source_db("no-such.sqlite", REPO)
        self.assertEqual(caught.exception.reason, "SOURCE-DB-MISSING")
        self.assertIn("no-such.sqlite", caught.exception.detail)

    def test_a_MISSING_data_project_REFUSES_by_name(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(proof.Refusal) as caught:
                proof.execute(Path(tmp) / "not-a-repo", Path(__file__), False, 60)
        self.assertEqual(caught.exception.reason, "DATA-PROJECT-MISSING")

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_work(self) -> None:
        spawned: list = []

        def spy(cmd, **kwargs):
            spawned.append(cmd)
            return subprocess.CompletedProcess(cmd, 0, "", "")

        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(proof, "_RUN", spy):
            with redirect_stdout(out), redirect_stderr(err):
                code = proof.main(["--timeout", "0", "--json"])
        self.assertEqual(code, proof.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")
        self.assertEqual(spawned, [], "a refused run still spawned the probe")

    def test_EVERY_spawn_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 0, PROVEN_OUTPUT, "")

        work = Path(tempfile.mkdtemp(prefix="f13-spawn-"))
        self.addCleanup(shutil.rmtree, work, True)
        (work / "src" / "FusionRpg.Data").mkdir(parents=True)
        (work / "src" / "FusionRpg.Data" / "FusionRpg.Data.csproj").write_text("<Project/>",
                                                                                encoding="utf-8")
        source = work / "rpg-hot.sqlite"
        connection = sqlite3.connect(source)
        try:
            connection.execute("CREATE TABLE dungeon_domain (domain_id TEXT)")
            connection.commit()
        finally:
            connection.close()
        with mock.patch.object(proof, "_RUN", run):
            proof.execute(work, source, True, 77)
        probe_spawns = [s for s in seen if s["cmd"][1:3] == ["run", "-c"]]
        self.assertEqual(len(probe_spawns), 1, f"expected one probe spawn, made {len(seen)}")
        self.assertEqual(probe_spawns[0]["kwargs"].get("timeout"), 77)
        self.assertIsNotNone(probe_spawns[0]["kwargs"].get("capture_output"))


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
        self.assertEqual({proof.EXIT_PROVEN, proof.EXIT_FAILED, proof.EXIT_REFUSED}, {0, 1, 64})

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--repo-root", "--source-db", "--downgrade-first", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-SourceDb", "-RepoRoot", "-SourceMedia"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_PAYLOAD_is_embedded_verbatim(self) -> None:
        """The embedded C# is the SUBJECT. Asserted on the tokens that carry the proof, not on a count:
        a rewrite would be a different probe rather than a port of this one."""
        for token in ("RpgStore", "ReadDomains", "GetRpgActor", "GetRpgProgressionSummary",
                      "dungeon_domain", "first_clear_ref", "PROOF OK",
                      "no such column", "SqliteConnectionFactory.Open"):
            self.assertIn(token, proof.PROGRAM_CS, token)
        self.assertIn("__DATAPROJ__", proof.CSPROJ,
                      "the project template's placeholder must survive to be substituted")
        self.assertNotIn("__DATAPROJ__", proof.PROGRAM_CS)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("f13-schema-upgrade-proof.ps1", head)
        lowered = head.lower()
        for reason in ("finally", "cleanup", "no timeout", "plain copy", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_it_names_the_FENCE_that_explains_why_a_probe_and_not_a_TEST(self) -> None:
        """A one-off proof that looks like an oversight is an oversight the next reader will 'fix' by
        deleting. The fence is the reason it is where it is."""
        head = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("tests/FusionRpg.Data.Tests", head)
        self.assertIn("fence", head.lower())

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib",
                          "ast", "re", "sqlite3"}
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
            if label.split(".")[0] in global_modules:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))


import shutil  # noqa: E402 - used by one case's cleanup, after the class body

if __name__ == "__main__":
    unittest.main()
