import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock


MODULE_PATH = Path(__file__).with_name("worktree_cleanup_core.py")
spec = importlib.util.spec_from_file_location("worktree_cleanup_core", MODULE_PATH)
assert spec and spec.loader
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)


class WorktreeCleanupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "user.name", "Test")
        (self.repo / ".gitignore").write_text("ignored.txt\n", encoding="utf-8")
        (self.repo / "tracked.txt").write_text("clean\n", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-qm", "initial")
        self.records = self.repo / "tasks" / "sessions"
        self.records.mkdir(parents=True)
        self.records.mkdir(exist_ok=True)
        self.linked = self.root / "linked"
        self.git("worktree", "add", "-q", "-b", "side", str(self.linked))
        self.write_session("merged", "side", self.linked)

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args, cwd=None):
        result = subprocess.run(
            ["git", *args],
            cwd=cwd or self.repo,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode:
            raise AssertionError(result.stderr or result.stdout)
        return result.stdout.strip()

    def write_session(self, status, branch, worktree, session="side-session"):
        (self.records / f"{session}.json").write_text(
            json.dumps(
                {
                    "session": session,
                    "program": "test",
                    "problem": "test",
                    "mode": "worktree",
                    "branch": branch,
                    "worktree": str(worktree),
                    "paths": ["tracked.txt"],
                    "started": "2026-01-01T00:00:00Z",
                    "status": status,
                }
            ),
            encoding="utf-8",
        )

    def write_lane(self, runner, lane, worktree, state=None, base="HEAD", extra=None):
        """Write one runner lane record (`meta.json`, plus `status.json` when a state is given)."""
        directory = self.repo / ".claude" / f"{runner}-agents" / "agents" / lane
        directory.mkdir(parents=True, exist_ok=True)
        meta = {
            "id": lane,
            "branch": f"{runner}/{lane}",
            "base": base,
            "cwd": str(worktree) if worktree is not None else None,
        }
        meta.update(extra or {})
        meta = {key: value for key, value in meta.items() if value is not None}
        (directory / "meta.json").write_text(json.dumps(meta), encoding="utf-8")
        if state is not None:
            (directory / "status.json").write_text(
                json.dumps({"id": lane, "state": state, "cwd": str(worktree) if worktree else None}),
                encoding="utf-8",
            )
        return directory

    def write_manager(self, worktree, session="ses_legacy"):
        manager = self.repo / ".kilo" / "agent-manager.json"
        manager.parent.mkdir(parents=True, exist_ok=True)
        manager.write_text(
            json.dumps(
                {
                    "worktrees": {"wt-1": {"branch": "side", "path": str(worktree)}},
                    "sessions": {session: {"worktreeId": "wt-1"}},
                }
            ),
            encoding="utf-8",
        )
        return manager

    def report(self, **kwargs):
        return core.build_report(self.repo, "main", **kwargs)

    def linked_item(self, report):
        return next(item for item in report["worktrees"] if core.path_key(item["path"]) == core.path_key(self.linked))

    def test_clean_merged_worktree_is_marked(self):
        report = self.report()
        item = self.linked_item(report)
        self.assertEqual(item["state"], core.MARKER_STATE_SHOULD_CLEAN)
        self.assertTrue(item["completion"]["merged"])
        self.assertEqual(item["blockers"], [])

    def test_unowned_worktree_is_held(self):
        self.records.joinpath("side-session.json").unlink()
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("no-session-owner", item["blockers"])

    def test_active_owner_is_held(self):
        self.write_session("active", "side", self.linked)
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("active-session-owner", item["blockers"])

    def test_active_direct_mode_branch_owner_is_held(self):
        (self.records / "direct-session.json").write_text(
            json.dumps(
                {
                    "session": "direct-session",
                    "program": "test",
                    "problem": "test",
                    "mode": "direct",
                    "branch": "side",
                    "worktree": None,
                    "paths": ["tracked.txt"],
                    "started": "2026-01-01T00:00:00Z",
                    "status": "active",
                }
            ),
            encoding="utf-8",
        )
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("active-session-owner", item["blockers"])

    def test_dirty_and_ignored_worktrees_are_held(self):
        (self.linked / "tracked.txt").write_text("dirty\n", encoding="utf-8")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("dirty-or-ignored-worktree", item["blockers"])

        self.git("restore", "tracked.txt", cwd=self.linked)
        (self.linked / "ignored.txt").write_text("disposable\n", encoding="utf-8")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("ignored.txt", [entry["path"] for entry in item["status"]["changedPaths"]])

    def test_configured_upstream_marks_pushed_branch(self):
        self.git("update-ref", "refs/remotes/origin/side", self.git("rev-parse", "side", cwd=self.linked))
        item = self.linked_item(self.report(upstream_refs={"side": "refs/remotes/origin/side"}))
        self.assertEqual(item["state"], core.MARKER_STATE_SHOULD_CLEAN)
        self.assertTrue(item["completion"]["pushed"])

    def test_marker_stale_when_branch_tip_changes(self):
        report = self.report()
        marker = core.marker_for_item(report, self.linked_item(report))
        (self.linked / "tracked.txt").write_text("new tip\n", encoding="utf-8")
        self.git("add", "tracked.txt", cwd=self.linked)
        self.git("commit", "-qm", "new tip", cwd=self.linked)
        fresh = self.report()
        self.assertEqual(core.effective_marker_state(marker, self.linked_item(fresh)), "stale")

    def test_cleanup_recycles_and_unregisters_only_the_worktree(self):
        report = self.report()
        marker = core.mark_report(self.repo, report)
        marker = next(value for value in marker if value["worktree"]["path"] == str(self.linked.resolve()))

        def fake_recycle(path):
            import shutil
            shutil.rmtree(path)
            return {"backend": "test-recycle", "sourcePath": str(path), "recycledUtc": "test"}

        with mock.patch.object(core, "recycle_directory", side_effect=fake_recycle):
            cleaned = core.remove_worktree(self.repo, marker, report, marker["confirmationToken"])
        self.assertEqual(cleaned["state"], core.MARKER_STATE_RECYCLED)
        self.assertTrue(cleaned["cleanup"]["recycleWorktree"])
        self.assertFalse(cleaned["cleanup"]["permanentDelete"])
        self.assertEqual(cleaned["recycleReceipt"]["backend"], "test-recycle")
        self.assertFalse(self.linked.exists())
        self.assertTrue(self.git("show-ref", "--verify", "refs/heads/side"))
        self.assertTrue((self.records / "side-session.json").exists())
        registered = self.git("worktree", "list", "--porcelain")
        self.assertNotIn(str(self.linked.resolve()), registered)

    def test_cleanup_refuses_wrong_confirmation(self):
        report = self.report()
        marker = core.marker_for_item(report, self.linked_item(report))
        with self.assertRaises(core.CleanupError):
            core.remove_worktree(self.repo, marker, report, "confirm-wrong")

    def test_cleanup_revalidates_dirty_change_after_marking(self):
        report = self.report()
        marker = core.mark_report(self.repo, report)
        marker = next(value for value in marker if value["worktree"]["path"] == str(self.linked.resolve()))
        (self.linked / "tracked.txt").write_text("changed after marker\n", encoding="utf-8")
        with self.assertRaises(core.CleanupError):
            core.remove_worktree(self.repo, marker, report, marker["confirmationToken"])
        self.assertTrue(self.linked.exists())

    def test_live_opencode_lane_holds_its_worktree(self):
        """A `running` OpenCode lane owns its `cwd`; the worktree is manual-review, never clean."""
        report = self.report()
        self.assertEqual(self.linked_item(report)["state"], core.MARKER_STATE_SHOULD_CLEAN)

        self.write_lane("opencode", "cs-rank-b", self.linked, state="running")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn(core.RUNNER_BLOCKER, item["blockers"])
        self.assertIn(core.RUNNER_UNFINISHED_BLOCKER, item["blockers"])
        self.assertEqual([owner["session"] for owner in item["runnerEvidence"]], ["cs-rank-b"])
        self.assertEqual(item["runnerEvidence"][0]["runner"], "opencode")
        self.assertEqual(item["runnerEvidence"][0]["state"], "running")

    def test_live_cmdc_lane_holds_its_worktree(self):
        """The same ownership rule applies to the cmdc lane registry."""
        self.write_lane("cmdc", "lane-a", self.linked, state="blocked")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn(core.RUNNER_BLOCKER, item["blockers"])
        self.assertIn(core.RUNNER_UNFINISHED_BLOCKER, item["blockers"])
        self.assertEqual([owner["session"] for owner in item["runnerEvidence"]], ["lane-a"])
        self.assertEqual(item["runnerEvidence"][0]["runner"], "cmdc")

    def test_lane_without_status_json_holds_as_unknown_state(self):
        """An absent `status.json` means 'owner, unknown state' — not 'finished'."""
        self.write_lane("opencode", "cs-rank-b", self.linked, state=None)
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn(core.RUNNER_UNFINISHED_BLOCKER, item["blockers"])
        self.assertEqual(item["runnerEvidence"][0]["state"], "unknown")

    def test_terminal_lane_still_holds_its_worktree(self):
        """A finished lane is a stale owner, and a stale owner is a human decision, not a removal.

        `done` is terminal, so the not-finished blocker is dropped — but `managed-runner-session`
        remains, so the worktree is still manual-review and never consumable.
        """
        self.write_lane("cmdc", "lane-a", self.linked, state="done")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn(core.RUNNER_BLOCKER, item["blockers"])
        self.assertNotIn(core.RUNNER_UNFINISHED_BLOCKER, item["blockers"])
        self.assertEqual(item["runnerEvidence"][0]["state"], "done")

    def test_malformed_lane_record_holds_instead_of_raising(self):
        """Malformed means HELD, and that choice is deliberate.

        A corrupt `meta.json` hides the worktree its lane owns, so the reader cannot hold that
        one path. Proceeding as it did before the fix would re-open the fail-open hole this
        reader exists to close, so the record is surfaced as a registry error and every candidate
        is held. The alternative reading — contribute no owner and carry on — is the defect.
        """
        directory = self.write_lane("opencode", "cs-rank-b", self.linked, state="running")
        (directory / "meta.json").write_text("{ this is not json", encoding="utf-8")

        report = self.report()  # must not raise
        item = self.linked_item(report)
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("runner-record-errors", item["blockers"])
        self.assertTrue(any("cs-rank-b" in entry for entry in report["runnerRecordErrors"]))
        self.assertEqual(item["runnerEvidence"], [])

    def test_pathless_lane_owns_nothing_and_does_not_block(self):
        """Review-only lanes carry `id`/`repo`/`branch` but no worktree: they own nothing.

        They are well-formed records in the live estate, so they must not be reported as
        corruption — otherwise two review lanes would hold every worktree in the repository.
        """
        self.write_lane("cmdc", "resume-29-review", None, state=None, extra={"repo": str(self.repo)})
        report = self.report()
        self.assertEqual(report["runnerRecordErrors"], [])
        self.assertEqual(self.linked_item(report)["state"], core.MARKER_STATE_SHOULD_CLEAN)

    def test_legacy_manager_evidence_still_holds_its_worktree(self):
        """Regression: the `.kilo/agent-manager.json` source keeps its original behaviour."""
        self.write_manager(self.linked, session="ses_legacy")
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertIn(core.RUNNER_BLOCKER, item["blockers"])
        self.assertEqual([owner["session"] for owner in item["runnerEvidence"]], ["ses_legacy"])
        self.assertEqual(item["runnerEvidence"][0]["state"], "managed")
        # The legacy record carries no `runner`, so it never gains the lane-specific blocker.
        self.assertNotIn(core.RUNNER_UNFINISHED_BLOCKER, item["blockers"])

    def test_unreadable_manager_file_holds_instead_of_raising(self):
        """The legacy source used to return a pathless record that made the consumer raise KeyError."""
        manager = self.write_manager(self.linked)
        manager.write_text("{ truncated", encoding="utf-8")
        report = self.report()
        self.assertEqual(self.linked_item(report)["state"], core.MARKER_STATE_MANUAL)
        self.assertIn("runner-record-errors", self.linked_item(report)["blockers"])

    def test_lane_path_and_cwd_both_resolve_as_owners(self):
        """A lane may record its worktree under `path` instead of `cwd`; both are ownership."""
        self.write_lane("opencode", "path-lane", None, state="spawning", extra={"path": str(self.linked)})
        item = self.linked_item(self.report())
        self.assertEqual(item["state"], core.MARKER_STATE_MANUAL)
        self.assertEqual([owner["session"] for owner in item["runnerEvidence"]], ["path-lane"])


if __name__ == "__main__":
    unittest.main()
