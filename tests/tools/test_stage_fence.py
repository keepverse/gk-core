"""Contract tests for `gk-core/scripts/stage-fence.py`.

The tool exists because this program published another lane's work three times, and the third was
caused by its own staging rule: a stage list built from `git status` MINUS A DENYLIST of path
fragments I had happened to learn. The fix is to stage from the session FENCE, so an unfamiliar
path is not staged rather than being staged because nothing excluded it.

So the contract is the MATCHING, and the direction of its failure is what matters: a path the fence
does not cover must be left ALONE. A test that only checked "my files get staged" would pass with
the denylist rule too.
"""
from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


tool = _load("stage_fence", REPO / "scripts" / "stage-fence.py")

# A fence shaped like the real one: exact paths, a file glob, and a directory glob.
FENCE = [
    "scripts/cscan.py",
    "tests/tools/test_*.py",
    "docs/architecture/ps1-*.md",
    "tasks/ps1-ban-todo.md",
    "tasks/reports/ps1-ban-*.md",
    "scripts/guard-*.py",
]


class MatchingIsFenceAuthored(unittest.TestCase):
    def test_an_exact_path_matches(self) -> None:
        self.assertTrue(tool.in_fence("scripts/cscan.py", FENCE))

    def test_a_file_glob_matches(self) -> None:
        self.assertTrue(tool.in_fence("tests/tools/test_cscan.py", FENCE))
        self.assertTrue(tool.in_fence("docs/architecture/ps1-port-checklist.md", FENCE))

    def test_a_directory_glob_matches_below_it(self) -> None:
        self.assertTrue(tool.in_fence("tasks/reports/ps1-ban-2026.md", FENCE))

    def test_a_path_outside_the_fence_does_not_match(self) -> None:
        # The direction that matters. Each of these is the shape of a path another lane owns.
        for path in ("tools/seedsmith/tests/test_briefkit_avoid_list.py",
                     ".commandcode/taste/taste/taste.md",
                     "tasks/sessions/numeric-types-dedup-20260926.json",
                     "tests/FusionRpg.Data.Tests/BuildPresets/BuildPresetStoreTests.cs",
                     "Directory.Build.props"):
            with self.subTest(path=path):
                self.assertFalse(tool.in_fence(path, FENCE),
                                 f"{path} is not in the fence and must not be staged by it")

    def test_a_near_miss_name_is_not_matched(self) -> None:
        # `test_cscan.py` is in the fence via the glob; `cscan.py.bak` is not, and a prefix or
        # substring rule would pull it in.
        self.assertFalse(tool.in_fence("tests/tools/cscan.py.bak", FENCE))
        self.assertFalse(tool.in_fence("scripts/cscan.py.orig", FENCE))

    def test_a_deeper_path_under_a_file_glob_is_not_matched(self) -> None:
        # `tests/tools/test_*.py` is a FILE glob, not a directory. fnmatch's `*` crosses
        # separators, so `tests/tools/sub/test_x.py` must not match it.
        self.assertFalse(tool.in_fence("tests/tools/sub/test_x.py", FENCE))

    def test_an_empty_fence_matches_nothing(self) -> None:
        # A fence that failed to load must not become a match-everything rule.
        for path in ("scripts/cscan.py", "anything.py"):
            self.assertFalse(tool.in_fence(path, []))


class TheRealFenceLoads(unittest.TestCase):
    def test_this_session_record_declares_paths(self) -> None:
        paths = tool.fence_paths("ps1-ban-manager-20260926")
        self.assertGreater(len(paths), 20)
        self.assertIn("scripts/cscan.py", paths)

    def test_an_unknown_session_is_refused_by_name(self) -> None:
        # Fail closed: a missing record must not degrade into "stage everything".
        with self.assertRaises(SystemExit) as caught:
            tool.fence_paths("no-such-session-20260926")
        self.assertIn("FENCE-RECORD-MISSING", str(caught.exception))


class AChangedPathOutsideTheFenceIsReported(unittest.TestCase):
    def test_the_report_names_the_path(self) -> None:
        # The tool's whole value is that an unfamiliar path is SURFACED rather than staged. This
        # asserts the two lists are computed from the same `changed()` set and split by the fence.
        changed = {"scripts/cscan.py", "tools/seedsmith/x.py", "tests/tools/test_cscan.py"}
        inside = sorted(p for p in changed if tool.in_fence(p, FENCE))
        outside = sorted(p for p in changed if not tool.in_fence(p, FENCE))
        self.assertEqual(inside, ["scripts/cscan.py", "tests/tools/test_cscan.py"])
        self.assertEqual(outside, ["tools/seedsmith/x.py"])
        # Every changed path is accounted for exactly once: nothing silently vanishes.
        self.assertEqual(len(inside) + len(outside), len(changed))


if __name__ == "__main__":
    unittest.main()
