"""Contract tests for `gk-core/scripts/guard-tuning-immutability.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, and T1-T4. It does not assert a message body.

The refusals are most of the value here. The original had THREE shapes that reported a clean guard
while checking nothing, and each is now a named refusal with a test:

  * **A root that is not a git repository** reported "no gk-core/data/tuning/*.json changes vs HEAD" and
    exited 0, because `2>$null` swallowed git's fatal and the empty changed-file set read as a clean
    tree. A typo'd `-Root` passed a CI gate without checking anything.
  * **A missing denylist** left the pattern list empty, which permits every domain - T4 silently off.
    Proven: the original prints `GUARD OK` for `test-x.v1.json` when pointed at a denylist path that
    does not exist.
  * **A modification whose before- or after-side cannot be read** was skipped INVISIBLY. The port
    performs the same skip - so a case the original passed still passes - and REPORTS it, because a
    rule that silently declines to look is indistinguishable from one that looked and found nothing.

Two host-dependent readings are pinned deliberately:

  * **The `<domain>.v<n>.json` filename shape FOLDS case.** `[a-z0-9-]` matches `D` and `.V1.JSON`
    matches, so `D.V1.JSON` is accepted as well-formed. Surprising, and the original's reading.
  * **Only the TOP-LEVEL `_meta` is stripped.** A nested `_meta` is ordinary data, so changing it IS a
    T1 violation. A test that "strips `_meta` everywhere" would quietly weaken the rule.

Differential evidence: 35 fixtures, 30 identical in exit code and every emitted line, plus 5 declared
divergences. That comparison lives outside this file because the PowerShell form no longer exists.

TEST SUBSTRATE: each fixture is a real git repository in a temporary directory, and the delete is
UNGUARDED. A swallowed `Directory.Delete` over git's read-only `.git/objects` blobs is the exact
shape that leaked 65.5 GB of temp dirs here; a failed delete must be a failure.
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-tuning-immutability.py"
DENYLIST = REPO / "scripts" / "tuning-domain-denylist.v1.json"
GIT_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("guard_tuning_immutability", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_tuning_immutability"] = guard
_spec.loader.exec_module(guard)


def git(root: Path, *args: str) -> None:
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          timeout=GIT_TIMEOUT)
    if proc.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed: {proc.stdout}{proc.stderr}")


def cleanup(root: Path) -> None:
    """Unguarded, deliberately. `git commit` on Windows leaves some `.git/objects/**` blobs read-only
    and a recursive delete throws on the first one, so the attribute is cleared - but the delete call
    itself has NO try/except, because any other genuine failure must still fail the test."""

    def on_error(func, path, _exc):
        os.chmod(path, stat.S_IWRITE)
        func(path)

    shutil.rmtree(root, onerror=on_error)


class Fixture:
    """A throwaway git repository under `gk-core/data/tuning/`, with a checked delete."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="ti-"))
        (self.root / "data" / "tuning").mkdir(parents=True, exist_ok=True)
        git(self.root, "init", "-q")
        git(self.root, "config", "user.email", "guard-test@example.invalid")
        git(self.root, "config", "user.name", "guard-test")

    def write(self, name: str | None, content: str) -> None:
        target = self.root / "data" / "tuning" / (name or "d.v1.json")
        if content == "":
            target.write_text("", encoding="utf-8")
        else:
            target.write_text(content, encoding="utf-8")

    def remove(self, name: str | None = None) -> None:
        (self.root / "data" / "tuning" / (name or "d.v1.json")).unlink()

    def commit(self, message: str) -> None:
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", message)

    def check(self, **kwargs) -> dict:
        # `denylist_path` is overridable: the "missing denylist" refusal needs a DIFFERENT one, and
        # hardcoding it here made that impossible (a duplicate keyword, not a clear failure).
        kwargs.setdefault("denylist_path", DENYLIST)
        return guard.check(self.root, **kwargs)

    def write_raw(self, relative: str, content: str) -> None:
        """A path OUTSIDE gk-core/data/tuning/. `write` always lands inside it, which silently turned a
        fixture's marker file into a tuning file."""
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")

    def __enter__(self) -> "Fixture":
        return self

    def __exit__(self, *_exc) -> None:
        cleanup(self.root)


SEED = '{"schemaVersion":1,"version":1,"_meta":{},"value":10}'


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=GIT_TIMEOUT)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=120)
        self.assertEqual(0, proc.returncode)
        self.assertIn("guard-tuning-immutability.py", proc.stdout)

    def test_the_real_repo_is_clean(self) -> None:
        got = guard.check(REPO, denylist_path=DENYLIST)
        self.assertEqual("OK", got["verdict"], got["failures"][:3])

    def test_the_denylist_default_is_relative_to_the_SCRIPT_not_the_root(self) -> None:
        # The guard is invoked FROM the real repo but pointed AT a fixture, which is what
        # TuningImmutabilityGuardTests does. A port that resolved the denylist against --root would
        # find nothing in every fixture and pass every T4 case VACUOUSLY.
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                              timeout=120)
        self.assertIn("SCRIPT", proc.stdout)
        with Fixture() as f:
            f.write("test-x.v1.json", '{"_meta":{}}')
            got = f.check()
            self.assertEqual("FAIL", got["verdict"],
                             "a denylisted domain must fail even when --root is a fixture repo")


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_the_verdict_line_goes_to_STDOUT_and_the_findings_to_STDERR(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("d.v1.json", '{"schemaVersion":1,"version":1,"_meta":{},"value":11}')
            result = run("--root", str(f.root), "--denylist-path", str(DENYLIST))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn(guard.VERDICT_FAILED.strip(), result["stdout"])
        self.assertIn("T1", result["stderr"])


    def test_the_correction_notice_is_LOUD_because_it_passes(self) -> None:
        # An escape hatch that passes quietly is an escape hatch nobody reviews. The deletion is
        # COMMITTED because a range reads committed history - an uncommitted one is invisible to
        # `git diff a..b`, and the notice would never be emitted.
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            f.commit("tuning-immutability: correction undo data/tuning/d.v1.json")
            result = run("--root", str(f.root), "--denylist-path", str(DENYLIST),
                         "--range", "HEAD~1..HEAD")
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])
        self.assertIn("correction marker used", result["stderr"])
class TheThreeSilentlyCleanShapes(unittest.TestCase):
    """The refusals. Most of the value of this port."""

    def test_a_root_that_is_not_a_git_repository_is_REFUSED_not_clean(self) -> None:
        with tempfile.TemporaryDirectory(prefix="ti-notrepo-") as tmp:
            root = Path(tmp)
            (root / "data" / "tuning").mkdir(parents=True)
            (root / "data" / "tuning" / "x.v1.json").write_text('{"_meta":{}}', encoding="utf-8")
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(root, denylist_path=DENYLIST)
            self.assertEqual("NOT-A-GIT-REPOSITORY", caught.exception.reason)
            # And through the CLI, so the refusal is what a CI gate would see.
            result = run("--root", str(root), "--denylist-path", str(DENYLIST))
            self.assertEqual(guard.EXIT_FAILED, result["exit"])
            self.assertIn("NOT-A-GIT-REPOSITORY", result["stderr"])

    def test_a_MISSING_denylist_is_REFUSED_because_T4_is_silently_off_without_it(self) -> None:
        with tempfile.TemporaryDirectory(prefix="ti-nodenylist-") as tmp:
            absent = Path(tmp) / "no-such-denylist.json"
            with self.assertRaises(guard.Refusal) as caught:
                guard.load_denylist(absent)
            self.assertEqual("DENYLIST-MISSING", caught.exception.reason)
        # The original prints GUARD OK here for a denylisted domain. A test that only checked the
        # refusal would miss that the ORIGINAL was clean, so this asserts the port is not.
        with Fixture() as f:
            f.write("test-x.v1.json", '{"_meta":{}}')
            with self.assertRaises(guard.Refusal):
                f.check(denylist_path=Path(tempfile.gettempdir()) / "no-such-denylist.json")

    def test_a_denylist_without_a_patterns_array_is_REFUSED(self) -> None:
        with tempfile.TemporaryDirectory(prefix="ti-baddenylist-") as tmp:
            bad = Path(tmp) / "d.json"
            bad.write_text('{"schemaVersion":1}', encoding="utf-8")
            with self.assertRaises(guard.Refusal) as caught:
                guard.load_denylist(bad)
            self.assertEqual("DENYLIST-MALFORMED", caught.exception.reason)

    def test_an_unreadable_side_is_skipped_AND_REPORTED(self) -> None:
        # A zero-byte committed file: `git show HEAD:path` emits nothing, so the original's
        # `if (-not $beforeText) { continue }` fires silently. The verdict is unchanged, so a case the
        # original passed still passes - but the skip is no longer invisible.
        with Fixture() as f:
            f.write("e.v1.json", "")
            f.commit("seed")
            f.write("e.v1.json", '{"_meta":{},"v":1}')
            got = f.check()
        self.assertEqual("OK", got["verdict"])
        self.assertEqual(1, len(got["skipped"]), got)
        self.assertIn("could not be read", got["skipped"][0])
        # And it is on stderr through the CLI.
        with Fixture() as f:
            f.write("e.v1.json", "")
            f.commit("seed")
            f.write("e.v1.json", '{"_meta":{},"v":1}')
            result = run("--root", str(f.root), "--denylist-path", str(DENYLIST))
        self.assertEqual(guard.EXIT_OK, result["exit"])
        self.assertIn("SKIPPED", result["stderr"])

    def test_a_tuning_document_that_is_not_JSON_is_refused(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            guard.strip_meta("not json at all")
        self.assertEqual("TUNING-DOC-NOT-JSON", caught.exception.reason)

    def test_a_tuning_document_that_is_not_an_OBJECT_is_refused(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            guard.strip_meta("[1,2,3]")
        self.assertEqual("TUNING-DOC-NOT-AN-OBJECT", caught.exception.reason)


class RuleT1OnlyMetaMayChange(unittest.TestCase):
    def test_a_meta_only_edit_passes(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("d.v1.json", '{"schemaVersion":1,"version":1,"_meta":{"note":"y"},"value":10}')
            self.assertEqual("OK", f.check()["verdict"])

    def test_a_real_value_change_fails(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("d.v1.json", '{"schemaVersion":1,"version":1,"_meta":{},"value":11}')
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T1 ") for x in got["failures"]))

    def test_key_ORDER_alone_is_not_a_change(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", '{"_meta":{},"v":{"a":1,"b":2}}')
            f.commit("seed")
            f.write("d.v1.json", '{"v":{"b":2,"a":1},"_meta":{}}')
            self.assertEqual("OK", f.check()["verdict"])

    def test_ARRAY_order_IS_a_change_because_the_order_is_meaningful(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", '{"_meta":{},"v":[1,2]}')
            f.commit("seed")
            f.write("d.v1.json", '{"_meta":{},"v":[2,1]}')
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_only_a_TOP_LEVEL_meta_is_stripped_so_a_NESTED_one_is_data(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", '{"_meta":{},"deep":{"_meta":1}}')
            f.commit("seed")
            f.write("d.v1.json", '{"_meta":{"x":1},"deep":{"_meta":2}}')
            got = f.check()
            self.assertEqual("FAIL", got["verdict"],
                             "a nested _meta is ordinary data; stripping it everywhere would "
                             "quietly weaken T1")

    def test_a_float_that_renders_as_an_integer_is_stricter_here(self) -> None:
        # PowerShell parses 10.0 to a Double and re-renders it 10, so the original called this
        # unchanged. `json` preserves the float. The stricter direction, and the only numeric
        # rendering the two disagree on.
        self.assertNotEqual(guard.strip_meta('{"value":10}'), guard.strip_meta('{"value":10.0}'))


class RuleT2VersionsMustBeContiguous(unittest.TestCase):
    def test_a_first_version_passes(self) -> None:
        with Fixture() as f:
            f.write("a.v1.json", '{"_meta":{}}')
            self.assertEqual("OK", f.check()["verdict"])

    def test_a_second_version_with_its_predecessor_passes(self) -> None:
        with Fixture() as f:
            f.write("a.v1.json", '{"_meta":{}}')
            f.commit("seed")
            f.write("a.v2.json", '{"_meta":{}}')
            self.assertEqual("OK", f.check()["verdict"])

    def test_a_second_version_WITHOUT_its_predecessor_fails(self) -> None:
        with Fixture() as f:
            f.write("a.v2.json", '{"_meta":{}}')
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T2 ") for x in got["failures"]))

    def test_a_third_version_needs_v2_not_merely_v1(self) -> None:
        with Fixture() as f:
            f.write("a.v1.json", '{"_meta":{}}')
            f.commit("seed")
            f.write("a.v3.json", '{"_meta":{}}')
            self.assertEqual("FAIL", f.check()["verdict"])


    def test_an_UNTRACKED_addition_is_still_checked(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("a.v9.json", '{"_meta":{}}')
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T2 data/tuning/a.v9.json") for x in got["failures"]),
                            got["failures"])
    def test_a_COMMITTED_addition_is_invisible_to_the_working_tree_mode(self) -> None:
        # Not a defect: the default mode is documented as "working tree vs HEAD", and a committed
        # change is in neither. The range form sees it.
        with Fixture() as f:
            f.write("a.v1.json", '{"_meta":{}}')
            f.commit("seed")
            f.write("a.v2.json", '{"_meta":{}}')
            f.commit("add v2")
            self.assertEqual(0, f.check()["tuning_changes"],
                             "working-tree mode cannot see a committed change")
            got = f.check(commit_range="HEAD~1..HEAD")
            self.assertEqual(1, got["tuning_changes"], "a range reads committed history")
            self.assertEqual("OK", got["verdict"])
class RuleT3DeletionNeedsTheNamedCorrection(unittest.TestCase):
    def test_a_deletion_fails(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T3 ") for x in got["failures"]))


    def test_a_correction_marker_naming_the_path_exempts_it(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            f.commit("tuning-immutability: correction undid an in-place edit data/tuning/d.v1.json")
            got = f.check(commit_range="HEAD~1..HEAD")
            self.assertEqual("OK", got["verdict"])
            self.assertTrue(any("T3 correction" in c for c in got["corrections_used"]), got)

    def test_a_correction_marker_not_naming_the_path_does_NOT_exempt_it(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            f.commit("tuning-immutability: correction for something else entirely")
            self.assertEqual("FAIL", f.check(commit_range="HEAD~1..HEAD")["verdict"])

    def test_a_marker_naming_a_DIFFERENT_file_does_NOT_exempt_this_one(self) -> None:
        # THE PROPERTY THE WHOLE ESCAPE HATCH RESTS ON: the marker is scoped to the paths it names,
        # never a blanket pass. The test above cannot see this, because its marker names NOTHING -
        # so the marked set is empty, and "is this path in it" and "is it non-empty at all" both
        # answer false. A mutation turning the check into `bool(marked)` passed 46 tests green
        # because of exactly that. Named here, and for a MODIFICATION as well as a deletion, because
        # T1 and T3 share the mechanism and either one could drift alone.
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.write("a.v1.json", '{"_meta":{}}')
            f.commit("seed")
            f.remove()
            f.commit("tuning-immutability: correction for data/tuning/aaa.v1.json")
            self.assertEqual("FAIL", f.check(commit_range="HEAD~1..HEAD")["verdict"])
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("d.v1.json", '{"_meta":{},"value":99}')
            f.commit("tuning-immutability: correction for data/tuning/somewhere-else.json")
            got = f.check(commit_range="HEAD~1..HEAD")
            self.assertEqual("FAIL", got["verdict"], "a T1 edit is not exempted by an unrelated marker")
            self.assertEqual([], got["corrections_used"])

    def test_a_marker_naming_the_EXACT_path_does_exempt_exactly_that_path(self) -> None:
        # The positive half of the same property, so the test above cannot pass by the marker being
        # ignored altogether: a.v1.json is deleted in the same commit and IS exempt, while d.v1.json
        # is not. One marker, two deletions, one exemption - that asymmetry IS the contract.
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.write("a.v1.json", '{"_meta":{}}')
            f.commit("seed")
            f.remove("d.v1.json")
            f.remove("a.v1.json")
            f.commit("tuning-immutability: correction for data/tuning/a.v1.json only")
            got = f.check(commit_range="HEAD~1..HEAD")
            used = " ".join(got["corrections_used"])
            self.assertIn("data/tuning/a.v1.json", used)
            self.assertNotIn("d.v1.json", used)
            self.assertTrue(any(x.startswith("T3 data/tuning/d.v1.json") for x in got["failures"]),
                            got["failures"])

    def test_the_correction_marker_FOLDS_case(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            f.commit("TUNING-IMMUTABILITY: CORRECTION data/tuning/d.v1.json")
            self.assertEqual("OK", f.check(commit_range="HEAD~1..HEAD")["verdict"])

    def test_an_UNCOMMITTED_rename_is_a_DELETION_of_the_old_path(self) -> None:
        # Nothing is committed, so `git diff HEAD` reports D for the old path and the new one is
        # untracked. T3 must fire on the OLD name: renaming a published file out of gk-core/data/tuning is a
        # deletion, which is the whole point.
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("e.v1.json", SEED)
            f.remove()
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T3 data/tuning/d.v1.json") for x in got["failures"]),
                            got["failures"])

    def test_a_STAGED_rename_is_still_a_DELETION_of_the_old_path(self) -> None:
        # Staging both halves lets git PAIR them, so `git diff --name-status` emits `R100` with three
        # fields and the guard must expand it to a D of the old path PLUS an A of the new one.
        # Collapsing it to the A alone would let a published version be renamed away unnoticed.
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.write("e.v1.json", SEED)
            f.remove()
            git(f.root, "add", "-A")
            raw = guard.git_lines(f.root, ["diff", "--name-status", "--diff-filter=ACMDR", "HEAD"])
            self.assertTrue(any(line.startswith("R") and line.count("\t") == 2 for line in raw),
                            f"expected git to pair the rename as R100, got {raw}")
            self.assertEqual([("D", "data/tuning/d.v1.json"), ("A", "data/tuning/e.v1.json")],
                             [s for s in guard.changed_file_statuses(f.root, "HEAD", None)
                              if s[1].startswith("data/tuning/")])
            got = f.check()
            self.assertEqual("FAIL", got["verdict"])
            self.assertTrue(any(x.startswith("T3 data/tuning/d.v1.json") for x in got["failures"]),
                            got["failures"])
    def test_a_marker_in_a_commit_OUTSIDE_the_range_does_not_count(self) -> None:
        with Fixture() as f:
            f.write("d.v1.json", SEED)
            f.commit("seed")
            f.remove()
            f.commit("tuning-immutability: correction data/tuning/d.v1.json")
            f.write_raw("other.txt", "x")
            f.write_raw("other.txt", "y")
            f.commit("later")
            self.assertEqual("OK", f.check(commit_range="HEAD~1..HEAD~1")["verdict"])
class RuleT4NameShapeAndTheDenylist(unittest.TestCase):
    def _added(self, name: str) -> dict:
        with Fixture() as f:
            f.write(name, '{"_meta":{}}')
            return f.check()

    def test_a_bad_filename_fails(self) -> None:
        self.assertEqual("FAIL", self._added("d.json")["verdict"])

    def test_every_denylisted_domain_shape_fails(self) -> None:
        # A closed vocabulary the code owns: a new pattern is a reviewed change, so a shape that
        # slips past is a hole in the list rather than a value to update.
        for name in ("test-x.v1.json", "tmp-x.v1.json", "scratch-x.v1.json", "loopfoo-test.v1.json"):
            with self.subTest(name=name):
                self.assertEqual("FAIL", self._added(name)["verdict"])

    def test_a_REAL_domain_passes(self) -> None:
        self.assertEqual("OK", self._added("aura.v1.json")["verdict"])

    def test_the_denylist_pattern_FOLDS_case(self) -> None:
        self.assertEqual("FAIL", self._added("TEST-x.v1.json")["verdict"])

    def test_the_filename_shape_FOLDS_case_which_is_the_originals_reading(self) -> None:
        # `[a-z0-9-]` matches `D` and `.V1.JSON` matches the shape, so this is ACCEPTED as well-formed
        # and then its domain is compared case-insensitively. Surprising, and asserted so a later
        # tightening is a decision rather than a drift.
        got = self._added("D.V1.JSON")
        self.assertEqual("OK", got["verdict"])
        self.assertEqual(1, got["tuning_changes"])

    def test_a_denied_domain_also_needs_a_denied_PATTERN_not_a_whole_list_check(self) -> None:
        # First matching pattern wins, so the finding does not name one. Asserted through the shape so
        # a change to "report every matching pattern" is visible.
        self.assertTrue(guard.domain_is_denied("test-x", ["^loop.*test", "^test-"]))
        self.assertFalse(guard.domain_is_denied("aura", ["^test-"]))


class TheCaseConventions(unittest.TestCase):
    """Four fold, three do not. Asserted on the compiled patterns, not only through a fixture."""

    FOLD = ("CORRECTION_MARKER", "TUNING_PATH", "TUNING_FILENAME")

    def test_the_three_case_INSENSITIVE_patterns_fold(self) -> None:
        for name in self.FOLD:
            with self.subTest(pattern=name):
                self.assertTrue(getattr(guard, name).flags & guard.re.IGNORECASE)


    def test_the_correction_PATH_pattern_does_NOT_fold(self) -> None:
        # `[regex]::Matches` does not fold, so a commit naming DATA/TUNING/x.json exempts nothing.
        # The marker itself DOES fold (tested above), so the pair is the point: one folds, one does not,
        # in the same feature.
        self.assertFalse(guard.CORRECTION_PATH.flags & guard.re.IGNORECASE)
        self.assertEqual(set(), guard.correction_marked_files(
            ["tuning-immutability: correction DATA/TUNING/a.json"]),
            "an upper-case path in the message matches nothing, so nothing is exempted")
        self.assertEqual({"data/tuning/a.json"}, guard.correction_marked_files(
            ["tuning-immutability: correction data/tuning/a.json"]))
    def test_a_correction_marker_outside_the_range_is_not_collected(self) -> None:
        self.assertEqual(set(), guard.correction_marked_files(["an ordinary commit"]))
        self.assertEqual(set(), guard.correction_marked_files([""]))
        self.assertEqual(set(), guard.correction_marked_files([]))


class TheEmptyMarkedSetIsTheCommonCase(unittest.TestCase):
    def test_no_marker_at_all_is_an_empty_set_not_a_crash(self) -> None:
        # The original needed the unary comma operator `,$marked` to stop PowerShell unrolling a
        # HashSet onto the pipeline, which turned an EMPTY set into `$null` at the call site and a
        # non-empty one into an `object[]` with no `.Contains()` - reproduced live on the very first
        # real fixture. The empty case is the COMMON one, so the trap fired on nearly every run.
        self.assertEqual(set(), guard.correction_marked_files([]))
        marked = guard.correction_marked_files(
            ["tuning-immutability: correction data/tuning/a.json"])
        self.assertIn("data/tuning/a.json", marked)
        self.assertTrue(marked.__contains__("data/tuning/a.json"))


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "scope", "tuning_changes", "failures", "corrections_used", "skipped"}
    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with Fixture() as ok:
            ok.write("aura.v1.json", '{"_meta":{}}')
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(ok.root), "--denylist-path",
                                   str(DENYLIST), "--json"], capture_output=True, text=True,
                                  timeout=GIT_TIMEOUT)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
        with Fixture() as bad:
            bad.write("test-x.v1.json", '{"_meta":{}}')
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(bad.root), "--denylist-path",
                                   str(DENYLIST), "--json"], capture_output=True, text=True,
                                  timeout=GIT_TIMEOUT)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
            self.assertEqual("FAIL", json.loads(proc.stdout)["verdict"])

    def test_a_refusal_carries_its_reason_in_the_json(self) -> None:
        with tempfile.TemporaryDirectory(prefix="ti-json-") as tmp:
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--denylist-path",
                                   str(DENYLIST), "--json"], capture_output=True, text=True,
                                  timeout=GIT_TIMEOUT)
            payload = json.loads(proc.stdout)
            self.assertEqual(self.REFUSAL_KEYS, set(payload))
            self.assertEqual("NOT-A-GIT-REPOSITORY", payload["reason"])


class TheShippedState(unittest.TestCase):
    def test_this_repo_has_no_immutability_violation(self) -> None:
        got = guard.check(REPO, denylist_path=DENYLIST)
        self.assertEqual("OK", got["verdict"], got["failures"][:3])

    def test_the_denylist_is_a_DENYLIST_and_not_an_allowlist(self) -> None:
        # An allowlist of the ~100+ real domains is a POPULATION that grows every time a program adds
        # one, exactly the number the contract-not-population rule forbids pinning. The note in the
        # file says so; this asserts the file does not drift into the other shape.
        document = json.loads(DENYLIST.read_text(encoding="utf-8"))
        self.assertIn("patterns", document)
        for forbidden in ("domains", "allowlist", "allow"):
            with self.subTest(key=forbidden):
                self.assertNotIn(forbidden, document)

    def test_the_denylist_has_no_REAL_domain_in_it(self) -> None:
        # A pattern broad enough to catch a real domain would fail every legitimate publish.
        published = {p.name for p in (REPO / "data" / "tuning").glob("*.v*.json")}
        self.assertTrue(published, "the published corpus is not empty; this test needs it")
        for path in published:
            domain = path.split(".v")[0]
            with self.subTest(domain=domain):
                self.assertFalse(guard.domain_is_denied(domain, guard.load_denylist(DENYLIST)),
                                 f"{domain} is a PUBLISHED domain and must not be denylisted")


if __name__ == "__main__":
    unittest.main()
