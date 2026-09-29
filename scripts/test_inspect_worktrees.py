import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("inspect-worktrees.py")
spec = importlib.util.spec_from_file_location("inspect_worktrees", MODULE_PATH)
assert spec and spec.loader
inspect_worktrees = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inspect_worktrees)


class WorktreeParserTests(unittest.TestCase):
    def test_parses_worktree_porcelain_records(self):
        text = "\n".join(
            [
                "worktree C:/repo",
                "HEAD abc123",
                "branch refs/heads/main",
                "",
                "worktree C:/repo/wt",
                "HEAD def456",
                "branch refs/heads/feature",
                "locked stale test",
                "",
            ]
        )

        records = inspect_worktrees.parse_worktree_porcelain(text)

        self.assertEqual(records[0]["path"], "C:/repo")
        self.assertEqual(records[0]["branch"], "main")
        self.assertEqual(records[1]["path"], "C:/repo/wt")
        self.assertEqual(records[1]["branch"], "feature")
        self.assertEqual(records[1]["locked"], "stale test")

    def test_classifies_status_records_without_losing_untracked_paths(self):
        text = " M tracked.txt\0?? new file.txt\0UU conflict.txt\0"

        result = inspect_worktrees.parse_status_z(text)

        self.assertEqual(result["unstaged"], ["tracked.txt", "conflict.txt"])
        self.assertEqual(result["untracked"], ["new file.txt"])
        self.assertEqual(result["conflicted"], ["conflict.txt"])
        self.assertEqual(result["changed"], 3)


class WorktreeInspectionTests(unittest.TestCase):
    def test_reports_clean_main_and_dirty_linked_worktree(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp) / "repo"
            repo.mkdir()
            self.git(repo, "init", "-q")
            self.git(repo, "config", "user.email", "test@example.invalid")
            self.git(repo, "config", "user.name", "Test")
            (repo / "tracked.txt").write_text("clean\n", encoding="utf-8")
            self.git(repo, "add", "tracked.txt")
            self.git(repo, "commit", "-qm", "initial")
            linked = Path(temp) / "linked"
            self.git(repo, "worktree", "add", "-q", "-b", "side", str(linked))

            (linked / "tracked.txt").write_text("dirty\n", encoding="utf-8")
            (linked / "new.txt").write_text("new\n", encoding="utf-8")

            result = inspect_worktrees.inspect_repo(repo)
            by_path = {item["path"]: item for item in result["worktrees"]}

            main = next(item for item in result["worktrees"] if item["path"] == str(repo.resolve()))
            side = next(item for item in result["worktrees"] if item["path"] == str(linked.resolve()))
            self.assertFalse(main["dirty"])
            self.assertTrue(side["dirty"])
            self.assertEqual(len(side["status"]["unstaged"]), 1)
            self.assertEqual(len(side["status"]["untracked"]), 1)
            self.assertEqual(result["dirtyCount"], 1)
            self.assertEqual(by_path[str(repo.resolve())]["head"], main["head"])

    @staticmethod
    def git(repo: Path, *args: str) -> None:
        result = subprocess.run(
            ["git", *args], cwd=repo, capture_output=True, text=True, check=False
        )
        if result.returncode:
            raise AssertionError(result.stderr or result.stdout)


if __name__ == "__main__":
    unittest.main()
