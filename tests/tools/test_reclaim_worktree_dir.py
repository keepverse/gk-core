"""Tests for `gk-core/scripts/reclaim_worktree_dir.py`.

The behaviour under test is the one the leftover walk needed and the cleanup tool could not do:
`git worktree remove` de-registers first and deletes second, so a long-path failure leaves a directory
full of files that git can never reclaim and never lists again. Measured 2026-09-27: 14,392 files.

The safety property these tests exist to pin is the **division of labour**. The manager adjudicates by
content and names the paths that are not at integration; the tool proves every *other* file is landed
and refuses if one is not. So a file nobody adjudicated cannot be deleted - the proof fails - and a file
somebody did adjudicate is deleted only because they looked at it. Neither side can delete
unadjudicated content alone, and every refusal is named rather than collapsed into "could not remove".
"""
from __future__ import annotations

import importlib.util
import io
import contextlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'reclaim_worktree_dir.py'


def _load():
    spec = importlib.util.spec_from_file_location('reclaim_worktree_dir', SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


reclaim = _load()


def git(repo: Path, *args: str) -> str:
    return subprocess.run(['git', *args], cwd=repo, capture_output=True, text=True,
                          errors='replace', timeout=120).stdout


class ReclaimCase(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name) / 'repo'
        self.root.mkdir()
        git(self.root, 'init', '-q', '-b', 'features/mega-merge')
        git(self.root, 'config', 'user.name', 't')
        git(self.root, 'config', 'user.email', 't@e')
        (self.root / 'a.txt').write_text('alpha\n', encoding='utf-8')
        (self.root / 'deep').mkdir()
        (self.root / 'deep' / 'b.txt').write_text('beta\n', encoding='utf-8')
        git(self.root, 'add', '-A')
        git(self.root, 'commit', '-qm', 'base')
        # a second commit so `deep/b.txt` is not the only thing distinguishing the trees
        (self.root / 'a.txt').write_text('alpha2\n', encoding='utf-8')
        git(self.root, 'add', '-A')
        git(self.root, 'commit', '-qm', 'second')
        (self.root / '.claude' / 'worktrees').mkdir(parents=True)

    def dereregistered(self, name: str, *, extra: dict[str, str] | None = None) -> Path:
        """Make a directory that LOOKS like the leftovers this tool exists for.

        `git worktree add` then a manual de-registration, so the path is a real checkout of a commit
        that is an ancestor of integration, with no registration left - which is the state
        `git worktree remove` leaves behind when its delete half fails.
        """
        path = self.root / '.claude' / 'worktrees' / name
        git(self.root, 'worktree', 'add', '-q', '-b', name, str(path), 'HEAD~1')
        for rel, body in (extra or {}).items():
            target = path / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(body, encoding='utf-8')
        _de_register(self.root, name, path)
        return path


def _de_register(root: Path, name: str, path: Path) -> None:
    """Leave exactly the state a FAILED `git worktree remove` leaves: no registration, files intact.

    `git worktree prune` is the obvious candidate and it is the wrong one: prune only drops
    registrations whose directory has already vanished, so a directory that is still full of files stays
    registered - which is precisely why the tool under test answered `STILL-REGISTERED` on the first
    version of this fixture, correctly. A failed remove de-registers first and deletes second, so the
    faithful reproduction is to delete the administrative entry and the `.git` link by hand and leave
    every file in place.
    """
    admin = root / '.git' / 'worktrees' / name
    if admin.is_dir():
        import shutil
        shutil.rmtree(admin)
    dotgit = path / '.git'
    if dotgit.is_file():
        dotgit.unlink()
    elif dotgit.is_dir():
        import shutil
        shutil.rmtree(dotgit)


class RefusalTests(ReclaimCase):
    def test_a_registered_worktree_is_never_touched(self):
        path = self.root / '.claude' / 'worktrees' / 'live'
        git(self.root, 'worktree', 'add', '-q', '-b', 'live', str(path), 'HEAD')
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'live', [], apply=True)
        self.assertEqual(ctx.exception.name, 'STILL-REGISTERED')
        self.assertTrue((path / 'a.txt').is_file(), 'a live worktree must survive')

    def test_a_directory_still_holding_dotgit_is_refused(self):
        path = self.dereregistered('halfgone')
        (path / '.git').mkdir(exist_ok=True)
        (path / '.git' / 'junk').write_text('x\n', encoding='utf-8')
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'halfgone', [], apply=True)
        self.assertEqual(ctx.exception.name, 'HAS-GITDIR')
        self.assertTrue((path / 'a.txt').is_file())

    def test_a_missing_directory_is_refused_by_name(self):
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'never-existed', [], apply=True)
        self.assertEqual(ctx.exception.name, 'NOT-FOUND')

    def test_an_unadjudicated_difference_refuses_and_deletes_nothing(self):
        """The property the whole design exists for."""
        path = self.dereregistered('dirty', extra={'src/OnlyHere.cs': 'class OnlyHere { }\n'})
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'dirty', [], apply=True)
        self.assertEqual(ctx.exception.name, 'UNLANDED-FILE')
        self.assertIn('OnlyHere.cs', ctx.exception.detail)
        self.assertTrue((path / 'src' / 'OnlyHere.cs').is_file(),
                        'the refusal must not have deleted anything')

    def test_a_modified_tracked_file_also_counts_as_unlanded(self):
        path = self.dereregistered('edited')
        (path / 'a.txt').write_text('edited in the worktree\n', encoding='utf-8')
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'edited', [], apply=True)
        self.assertEqual(ctx.exception.name, 'UNLANDED-FILE')
        self.assertIn('a.txt', ctx.exception.detail)
        self.assertTrue((path / 'a.txt').is_file())


class RemovalTests(ReclaimCase):
    def test_a_fully_landed_leftover_is_removed(self):
        path = self.dereregistered('clean')
        self.assertTrue((path / 'a.txt').is_file())
        plan = reclaim.reclaim_worktree_dir(self.root, 'clean', [], apply=True)
        self.assertEqual(plan['outcome'], 'REMOVED')
        self.assertFalse(path.exists(), 'the directory must actually be gone')
        self.assertGreater(plan['filesWalked'], 0)

    def test_without_apply_it_is_a_plan_and_nothing_is_touched(self):
        path = self.dereregistered('planned')
        plan = reclaim.reclaim_worktree_dir(self.root, 'planned', [], apply=False)
        self.assertEqual(plan['outcome'], 'WOULD-REMOVE')
        self.assertTrue((path / 'a.txt').is_file(), 'a plan must not remove anything')

    def test_an_adjudicated_path_is_excluded_and_the_rest_still_must_verify(self):
        path = self.dereregistered('mixed', extra={'src/Reviewed.cs': 'class Reviewed { }\n'})
        # naming the adjudicated file lets the proof proceed for everything else
        plan = reclaim.reclaim_worktree_dir(
            self.root, 'mixed', ['src/Reviewed.cs'], apply=True)
        self.assertEqual(plan['outcome'], 'REMOVED')
        self.assertEqual(plan['adjudicated'], ['src/Reviewed.cs'])
        self.assertFalse(path.exists())

    def test_adjudicating_one_file_does_not_excuse_another(self):
        self.dereregistered('twoleft', extra={'src/Reviewed.cs': 'a\n', 'src/AlsoOnly.cs': 'b\n'})
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'twoleft', ['src/Reviewed.cs'], apply=True)
        self.assertEqual(ctx.exception.name, 'UNLANDED-FILE')
        self.assertIn('AlsoOnly.cs', ctx.exception.detail)


class CliTests(ReclaimCase):
    def test_exit_is_zero_on_success_and_two_on_refusal(self):
        self.dereregistered('cli-ok')
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = reclaim.main(['cli-ok', '--apply', '--json', '--root', str(self.root)])
        self.assertEqual(rc, 0, buf.getvalue())
        self.assertEqual(json.loads(buf.getvalue())['outcome'], 'REMOVED')

        self.dereregistered('cli-refused', extra={'src/OnlyHere.cs': 'x\n'})
        err = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            rc = reclaim.main(['cli-refused', '--apply', '--root', str(self.root)])
        self.assertEqual(rc, 2, 'a refusal must exit non-zero')
        self.assertIn('REFUSED', err.getvalue())
        self.assertIn('meaning:', err.getvalue(), 'a refusal must name what it means')

    def test_a_negative_timeout_is_refused_before_anything_is_read(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = reclaim.main(['anything', '--timeout', '0', '--root', str(self.root)])
        self.assertEqual(rc, 2)
        self.assertIn('NEGATIVE-TIMEOUT', err.getvalue())

    def test_json_refusal_carries_the_name_and_the_meaning(self):
        self.dereregistered('json-refused', extra={'src/OnlyHere.cs': 'x\n'})
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = reclaim.main(['json-refused', '--json', '--root', str(self.root)])
        self.assertEqual(rc, 2)
        payload = json.loads(buf.getvalue())
        self.assertEqual(payload['outcome'], 'REFUSED')
        self.assertEqual(payload['name'], 'UNLANDED-FILE')
        self.assertTrue(payload['meaning'])

    def test_a_repeated_adjudicated_flag_does_not_drop_the_earlier_names(self):
        """`--adjudicated` is a list, so repeating it must add names, not replace them.

        Measured 2026-09-27: a driver passed `--adjudicated` four times for four adjudicated files.
        As a store flag argparse keeps only the LAST, so three names were silently dropped and the
        proof refused on exactly the files the caller had already adjudicated - which reads as
        "integration holds work this worktree never landed" when it holds none. Nothing could be
        deleted by the mistake (a dropped name refuses, never removes), but a trap that sends the
        manager to adjudicate a phantom is a trap, so both spellings are accepted and merged.
        """
        self.dereregistered('repeated', extra={'src/A.cs': 'a\n', 'src/B.cs': 'b\n', 'src/C.cs': 'c\n'})
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = reclaim.main(['repeated', '--apply', '--json', '--root', str(self.root),
                               '--adjudicated', 'src/A.cs',
                               '--adjudicated', 'src/B.cs',
                               '--adjudicated', 'src/C.cs'])
        self.assertEqual(rc, 0, buf.getvalue())
        self.assertEqual(sorted(json.loads(buf.getvalue())['adjudicated']),
                         ['src/A.cs', 'src/B.cs', 'src/C.cs'])

    def test_both_adjudicated_spellings_may_be_mixed_in_one_call(self):
        self.dereregistered('mixed-spelling', extra={'src/A.cs': 'a\n', 'src/B.cs': 'b\n'})
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = reclaim.main(['mixed-spelling', '--apply', '--json', '--root', str(self.root),
                               '--adjudicated', 'src/A.cs,src/B.cs'])
        self.assertEqual(rc, 0, buf.getvalue())
        self.assertEqual(sorted(json.loads(buf.getvalue())['adjudicated']), ['src/A.cs', 'src/B.cs'])

    def test_a_duplicated_adjudication_is_listed_once(self):
        self.dereregistered('dupe', extra={'src/A.cs': 'a\n'})
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = reclaim.main(['dupe', '--apply', '--json', '--root', str(self.root),
                               '--adjudicated', 'src/A.cs', '--adjudicated', 'src/A.cs'])
        self.assertEqual(rc, 0, buf.getvalue())
        self.assertEqual(json.loads(buf.getvalue())['adjudicated'], ['src/A.cs'])

    def test_every_refusal_name_has_a_meaning(self):
        """A refusal nobody can act on is the 'could not remove' bucket this replaced."""
        self.assertEqual(set(reclaim.REFUSALS),
                         {'STILL-REGISTERED', 'HAS-GITDIR', 'NOT-FOUND', 'HELD',
                          'PATH-TOO-LONG', 'UNLANDED-FILE', 'WALK-CAPPED',
                          'OBJECTS-UNREADABLE', 'HASH-FAILED', 'HASH-COUNT-MISMATCH',
                          'LONG-PATH-NOT-IGNORED', 'IGNORE-QUERY-FAILED',
                          'PREFIX-IS-TRACKED'})
        for name, text in reclaim.REFUSALS.items():
            self.assertTrue(text.strip(), f'{name} has no explanation')

    def test_a_walk_cap_is_not_reported_as_anything_else(self):
        """`WALK-CAPPED` must mean the cap, and only the cap.

        Three unrelated failures - `rev-list` non-zero, `hash-object` non-zero, and a hash/file count
        mismatch - all reported `WALK-CAPPED` with a detail naming something else. A caller reading that
        would go looking for a cap that did not exist, which is the same unactionable bucket the tool
        was written to remove, reintroduced inside the tool.
        """
        source = SCRIPT.read_text(encoding='utf-8')
        # every WALK-CAPPED raise must be about the cap, and about nothing else
        import re as _re
        for match in _re.finditer(r"Refusal\('WALK-CAPPED'(.{0,160})", source, _re.S):
            detail = match.group(1)
            self.assertIn('stopped after', detail,
                          f'a WALK-CAPPED refusal must name the cap, not something else: {detail[:80]!r}')
        # and each unrelated failure has its own name, raised somewhere
        for name in ('OBJECTS-UNREADABLE', 'HASH-FAILED', 'HASH-COUNT-MISMATCH'):
            self.assertIn(f"Refusal('{name}'", source, f'{name} is never raised')


class IgnoredPathTests(ReclaimCase):
    """Build output is git-ignored, and `git hash-object` cannot open paths past 260 characters.

    Measured on the real orphan: 14,912 of 14,912 files were ignored, and dropping them left 1 file to
    hash. Without this the tool refuses a directory it should be able to prove.
    """

    def setUp(self) -> None:
        super().setUp()
        # this fixture needs the ignore rules the real repo carries at `.gitignore:22`
        (self.root / '.gitignore').write_text('[Oo]bj/\nbin/\n', encoding='utf-8')
        git(self.root, 'add', '-A')
        git(self.root, 'commit', '-qm', 'ignore build output, as this repo does')

    def test_git_ignored_output_is_excluded_from_the_proof_and_reported(self):
        path = self.dereregistered('withbin', extra={'bin/Release/net8.0/app.dll': 'MZLIB\x00',
                                                      'obj/Debug/t.log': 'noise\n'})
        plan = reclaim.reclaim_worktree_dir(self.root, 'withbin', [], apply=False)
        part = plan['partition']
        self.assertEqual(part['ignoredSkipped'], 2, part)
        self.assertEqual(part['unlandedCount'] if 'unlandedCount' in part else plan['unlandedCount'], 0)
        self.assertIn('bin/Release', part['ignoredPatterns'])
        self.assertIn('obj/Debug', part['ignoredPatterns'])
        self.assertTrue(path.exists(), 'a plan must leave the directory in place')

    def test_the_partition_is_reported_rather_than_swallowed(self):
        """A silent exclusion is a blind spot, and closing blind spots is the tool's whole purpose."""
        self.dereregistered('reported', extra={'bin/x.dll': 'a\n'})
        plan = reclaim.reclaim_worktree_dir(self.root, 'reported', [], apply=False)
        for key in ('totalFiles', 'hashed', 'ignoredSkipped', 'ignoredPatterns'):
            self.assertIn(key, plan['partition'], f'the partition must report {key}')

    def test_a_long_non_ignored_path_refuses_instead_of_being_dropped(self):
        """git cannot open a path past 260 chars. If such a path is NOT ignored it may be real work,
        so the tool must refuse by name rather than skip it and call the directory clean."""
        deep = 'src/' + '/'.join('segment%02d' % i for i in range(20)) + '/OnlyHere.cs'
        path = self.dereregistered('deep', extra={deep: 'class OnlyHere { }\n'})
        full = str(path / deep)
        if len(full) <= 255:
            self.skipTest('this filesystem is not long-path limited, so the refusal cannot trigger')
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'deep', [], apply=True)
        self.assertEqual(ctx.exception.name, 'LONG-PATH-NOT-IGNORED')
        self.assertTrue((path / deep).is_file(), 'the refusal must not have deleted anything')


class FailOpenTrapTests(ReclaimCase):
    """The trap that would have made this tool report a clean reclaim over 2.28 GB of nothing.

    `.gitignore:91` in this repo is `.claude/worktrees/`. So `git check-ignore` answers "IGNORED" for
    EVERY path under a worktree - including tracked source. An exclusion built on the obvious query
    would skip the whole proof, find no unlanded content, and report REMOVED having examined nothing.

    The fix is to make paths worktree-relative before they reach git. These tests fail if that regresses.
    """

    def _repo_ignoring_the_worktree_parent(self):
        # BOTH rules, as this repo actually has them: `.gitignore:22` ignores build output and
        # `.gitignore:91` ignores the worktree parent. Writing only the second would silently un-ignore
        # `obj/`, and the ignored-path tests would then pass for the wrong reason.
        (self.root / '.gitignore').write_text(
            '[Oo]bj/\nbin/\n.claude/worktrees/\n', encoding='utf-8')
        git(self.root, 'add', '-A')
        git(self.root, 'commit', '-qm', 'ignore the worktree parent, as this repo does')

    def test_an_ignored_parent_does_not_make_every_file_look_ignored(self):
        self._repo_ignoring_the_worktree_parent()
        self.assertEqual(reclaim._git_ignored(self.root, ['src/a.cs', 'obj/b.dll']),
                         ({'obj/b.dll'}, 2),
                         'src/ must NOT read as ignored just because its parent directory is')

    def test_a_real_leftover_under_an_ignored_parent_is_still_proven_not_assumed_clean(self):
        self._repo_ignoring_the_worktree_parent()
        path = self.dereregistered('trapped', extra={'src/OnlyHere.cs': 'class OnlyHere { }\n'})
        with self.assertRaises(reclaim.Refusal) as ctx:
            reclaim.reclaim_worktree_dir(self.root, 'trapped', [], apply=True)
        self.assertEqual(ctx.exception.name, 'UNLANDED-FILE',
                         'the unlanded file must be found, not skipped as ignored')
        self.assertIn('OnlyHere.cs', ctx.exception.detail)
        self.assertTrue((path / 'src' / 'OnlyHere.cs').is_file())

    def test_a_landed_leftover_under_an_ignored_parent_still_reclaims(self):
        self._repo_ignoring_the_worktree_parent()
        path = self.dereregistered('trapped-clean')
        plan = reclaim.reclaim_worktree_dir(self.root, 'trapped-clean', [], apply=True)
        self.assertEqual(plan['outcome'], 'REMOVED')
        self.assertFalse(path.exists())


if __name__ == '__main__':
    unittest.main()
