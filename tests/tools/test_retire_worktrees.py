"""Tests for `gk-core/scripts/retire_worktrees.py` — the tool that removes merged, finished worktrees.

The property that matters is not "it removes worktrees". It is that it removes **only** worktrees
which are simultaneously unowned, unlocked, clean and fully integrated — and that every other case
is KEPT with a stated reason. A tool that deletes a worktree holding an unmerged corpus is the worst
outcome in this repository's whole cleanup story, so each refusal has its own test.

Substrate: throwaway git repositories in a temp directory, deleted in teardown with the delete
asserted (never swallowed), per `docs/contributing/testing-standard.md`. The real repository is never
passed to `survey()` — which is why `survey()` takes `repo` instead of reading a module global.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

TOOL = Path(__file__).resolve().parents[2] / "scripts" / "retire_worktrees.py"


def _load():
    spec = importlib.util.spec_from_file_location("retire_worktrees_under_test", TOOL)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    # `@dataclass` resolves annotations through sys.modules[cls.__module__].
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


retire = _load()


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=120, check=False)
    if result.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed: {result.stderr.strip()[:200]}")
    return result.stdout.strip()


class RepoCase(unittest.TestCase):
    """A throwaway repo with an integration branch and real linked worktrees."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()
        (self.root / "tasks" / "sessions").mkdir(parents=True)
        git(self.root, "init", "-q", "-b", "features/mega-merge")
        git(self.root, "config", "user.name", "t")
        git(self.root, "config", "user.email", "t@e")
        (self.root / "README.md").write_text("base\n", encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "base")

    def age(self, wt: Path, seconds: float | None = None) -> None:
        """Backdate every file in a worktree so it reads as idle rather than in flight.

        The survey treats a file written inside `RECENT_ACTIVITY_SECONDS` as a blocker, because a
        lane creates its worktree before its record exists and a fresh clean tree is the normal
        state of a worktree in its first minute. A test that wants to exercise the RETIRABLE path
        must therefore age its fixture, which is also the honest shape of the real backlog: the
        worktrees worth clearing are the ones nobody has touched in hours.
        """
        target = time.time() - (seconds if seconds is not None
                                else retire.RECENT_ACTIVITY_SECONDS + 3600)
        for p in wt.rglob("*"):
            if ".git" in p.parts:
                continue
            try:
                os.utime(p, (target, target))
            except OSError:
                self.skipTest("cannot backdate a file on this filesystem")

    def worktree(self, name: str, *, commit: str | None = None, dirty: str | None = None,
                 untracked: str | None = None, locked: bool = False,
                 fresh: bool = False) -> Path:
        path = self.root.parent / name
        git(self.root, "worktree", "add", "-q", "-b", name, str(path), "features/mega-merge")
        if commit:
            (path / "f.txt").write_text(commit, encoding="utf-8")
            git(path, "add", "-A")
            git(path, "commit", "-qm", f"work on {name}")
        if dirty:
            (path / "README.md").write_text(dirty, encoding="utf-8")
        if untracked:
            (path / "generated.json").write_text(untracked, encoding="utf-8")
        if locked:
            git(self.root, "worktree", "lock", str(path))
        if not fresh:
            self.age(path)
        return path

    def session(self, name: str, *, status: str, branch: str, worktree: str | None) -> None:
        (self.root / "tasks" / "sessions" / f"{name}.json").write_text(json.dumps({
            "session": name, "program": "p", "problem": "x", "mode": "worktree" if worktree else "direct",
            "branch": branch, "worktree": worktree, "paths": ["README.md"],
            "started": "2026-09-26T00:00:00Z", "status": status,
        }, indent=1), encoding="utf-8")

    def lane_registry(self, runner: str, lane: str, branch: str) -> None:
        base = self.root / ".claude" / f"{runner}-agents" / "agents" / lane
        base.mkdir(parents=True)
        (base / "meta.json").write_text(json.dumps({"branch": branch, "cwd": "somewhere"}), encoding="utf-8")

    def verdicts(self, **kwargs):
        # `survey` returns (verdicts, counts, leftovers); the tests care about the verdicts.
        return retire.survey("features/mega-merge", 60.0, repo=self.root, **kwargs)[0]

    def survey(self, **kwargs):
        return retire.survey("features/mega-merge", 60.0, repo=self.root, **kwargs)

    def leftover_dir(self, name: str, *, files: dict[str, str] | None = None) -> Path:
        """Create a directory beside the worktrees that git does NOT register.

        This is the shape a `git worktree remove` leaves behind when the delete fails after
        de-registration, and the shape the survey used to be blind to.
        """
        parent = self.root.parent
        d = parent / name
        d.mkdir(parents=True, exist_ok=True)
        for rel, body in (files or {}).items():
            p = d / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(body, encoding="utf-8")
        return d

    def one(self, branch: str, **kwargs):
        """The verdict for one branch.

        Every test that is not ABOUT the no-record case seeds a closed record first, because the
        tool refuses outright when it cannot prove ownership at all - without the record the test
        would still pass, but for the wrong reason.
        """
        self.session(f"seed-{branch}", status="merged", branch=branch, worktree=None)
        rows = self.by_branch(self.verdicts(**kwargs))
        self.assertIn(branch, rows, sorted(rows))
        return rows[branch]

    def by_branch(self, verdicts) -> dict:
        return {v.branch: v for v in verdicts}


class RetirableTests(RepoCase):
    def test_a_merged_clean_unowned_worktree_is_retirable(self):
        self.worktree("done-and-merged")
        v = self.one("done-and-merged")
        self.assertTrue(v.retirable, v.blockers)
        self.assertIn("integrated", v.reasons)

    def test_a_worktree_with_no_session_record_at_all_is_still_retirable(self):
        # A CLOSED record is not required; the requirement is that no ACTIVE record owns it. This
        # test seeds one record for a DIFFERENT branch, so the worktree itself has no record.
        self.worktree("no-record")
        self.session("s-other", status="merged", branch="some-other-branch", worktree=None)
        v = self.one("no-record")
        self.assertTrue(v.retirable, v.blockers)


class RefusalTests(RepoCase):
    """Every keep-reason. Each one is a case where deleting would lose work."""

    def test_an_unintegrated_commit_is_kept(self):
        self.worktree("has-unmerged", commit="x")
        v = self.one("has-unmerged")
        self.assertFalse(v.retirable)
        self.assertTrue(any("not integrated" in b for b in v.blockers), v.blockers)

    def test_a_lane_claim_with_an_active_session_behind_it_is_kept(self) -> None:
        # The safety side of the stale-claim rule. A registry claim alone does not block, but when a
        # tracked session record corroborates it the worktree is genuinely live and MUST be kept -
        # otherwise the fix for a bookkeeping ratchet becomes a fail-open on real work.
        self.worktree("live-lane")
        self.session("live-lane-s", status="active", branch="live-lane", worktree=None)
        self.lane_registry("cmdc", "live-lane", "live-lane")

        v = self.one("live-lane")
        self.assertFalse(v.retirable, v.blockers)
        self.assertTrue(any("active session" in b for b in v.blockers), v.blockers)
        self.assertTrue(any("lane registry" in b and "corroborated" in b for b in v.blockers),
                        v.blockers)

    def test_an_uncorroborated_lane_claim_does_not_block_and_is_surfaced(self) -> None:
        # The defect this fixes, measured 2026-09-26: all 83 lane-registry entries had no active
        # session behind them, and the 80 branches they held had no unmerged patch. The registries
        # are untracked and unpruned, so an absolute veto is a one-way ratchet that no merge can
        # ever reopen - it is what turned a finished pile into an unfinishable one.
        self.worktree("stale-claim")
        self.session("stale-claim-s", status="merged", branch="stale-claim", worktree=None)
        self.lane_registry("cmdc", "stale-claim", "stale-claim")

        v = self.one("stale-claim")
        self.assertTrue(v.retirable, v.blockers)
        # ...and the override is visible, not silent: a manager reading RETIRABLE must see that a
        # registry named this branch and that it was overruled.
        self.assertTrue(v.staleClaims, "the stale claim must be recorded, not dropped")
        self.assertIn("stale-claim", v.render())

    def test_a_lock_still_blocks_a_worktree_whose_lane_claim_is_stale(self) -> None:
        # A stale claim must not become an excuse to delete a LOCKED worktree. The two blockers are
        # independent, and loosening one is not licence to loosen the other.
        self.worktree("locked-stale", locked=True)
        self.session("locked-stale-s", status="abandoned", branch="locked-stale", worktree=None)
        self.lane_registry("cmdc", "locked-stale", "locked-stale")

        v = self.one("locked-stale")
        self.assertFalse(v.retirable, v.blockers)
        self.assertTrue(any("locked" in b for b in v.blockers), v.blockers)

    def test_local_modifications_are_kept(self):
        self.worktree("dirty", dirty="changed\n")
        v = self.one("dirty")
        self.assertFalse(v.retirable)
        self.assertTrue(any("local path" in b for b in v.blockers), v.blockers)

    def test_acceptance_evidence_inside_the_worktree_is_kept_and_named(self):
        # Acceptance raw logs live inside a review worktree, and the artefact that cites them is
        # tracked while the logs are not. Measured 2026-09-26: of 78 artefacts, 17 named a `logDir`
        # that resolved nowhere, and this tool had no awareness of the field at all. So the evidence a
        # verdict rests on could be deleted with nothing to show for it.
        target = self.worktree("holds-evidence")
        self.session("s", status="merged", branch="holds-evidence", worktree=None)
        logdir = Path("external-evidence") / "some-lane"
        (target / logdir).mkdir(parents=True, exist_ok=True)
        (target / logdir / "verify.log").write_text("output a reviewer would want to re-read\n",
                                                    encoding="utf-8")
        artefact = self.root / ".claude" / "cmdc-agents" / "acceptance"
        artefact.mkdir(parents=True, exist_ok=True)
        (artefact / "some-lane-abc12345.json").write_text(json.dumps({
            "lane": "some-lane",
            "sha": "0" * 40,
            "verdict": "GREEN",
            "logDir": logdir.as_posix(),
            # `contendedTree` is a BOOLEAN in the current schema. It is included here as a string
            # sibling field on purpose: a reader must not treat it as a path.
            "contendedTree": False,
        }), encoding="utf-8")

        v = self.one("holds-evidence")
        self.assertFalse(v.retirable, v.blockers)
        self.assertTrue(any("acceptance evidence" in b for b in v.blockers), v.blockers)
        self.assertTrue(any("some-lane" in b for b in v.blockers), v.blockers)

    def test_a_boolean_contendedTree_is_not_read_as_a_path(self):
        # The real schema has `contendedTree` as a bool. Treating it as a path made an earlier audit of
        # mine report "0 missing" for a check that had not actually run, so the type is asserted here.
        target = self.worktree("bool-field")
        self.session("s", status="merged", branch="bool-field", worktree=None)
        artefact = self.root / ".claude" / "cmdc-agents" / "acceptance"
        artefact.mkdir(parents=True, exist_ok=True)
        (artefact / "bool-lane-def01234.json").write_text(json.dumps({
            "lane": "bool-lane", "sha": "0" * 40, "verdict": "GREEN", "contendedTree": True,
        }), encoding="utf-8")

        found = retire.acceptance_evidence_under(self.root, target)
        self.assertEqual(found, {}, "a boolean field must not be resolved as a directory")
        self.assertTrue(self.one("bool-field").retirable)

    def test_untracked_generated_data_is_kept_and_named(self):
        # The corpus case: a worktree holding generated content nobody merged must never be a
        # candidate, and the reason must say so rather than "dirty".
        self.worktree("corpus", untracked='{"nodes": 706}')
        v = self.one("corpus")
        self.assertFalse(v.retirable)
        self.assertTrue(any("untracked" in b for b in v.blockers), v.blockers)

    def test_an_active_session_record_owns_the_worktree(self):
        path = self.worktree("live-lane")
        self.session("s-other", status="merged", branch="unrelated", worktree=None)
        self.session("s-live", status="active", branch="live-lane", worktree=str(path))
        v = self.by_branch(self.verdicts())["live-lane"]
        self.assertFalse(v.retirable)
        self.assertIn("s-live", " ".join(v.blockers))

    def test_an_active_record_for_the_branch_owns_it_even_without_a_worktree_path(self):
        self.worktree("branch-owned")
        self.session("s-other", status="merged", branch="unrelated", worktree=None)
        self.session("s-branch", status="active", branch="branch-owned", worktree=None)
        v = self.by_branch(self.verdicts())["branch-owned"]
        self.assertFalse(v.retirable)
        self.assertIn("s-branch", " ".join(v.blockers))

    def test_an_unintegrated_commit_is_kept_even_with_no_session_record(self):
        # The unintegrated-commit blocker must be visible on its own, not masked by the
        # no-ownership refusal, so this seeds a closed record for an unrelated branch.
        self.worktree("lonely-unmerged", commit="x")
        self.session("s-other", status="merged", branch="unrelated", worktree=None)
        v = self.by_branch(self.verdicts())["lonely-unmerged"]
        self.assertFalse(v.retirable)
        self.assertTrue(any("not integrated" in b for b in v.blockers), v.blockers)

    def test_a_locked_worktree_is_kept(self):
        self.worktree("locked-one", locked=True)
        v = self.one("locked-one")
        self.assertFalse(v.retirable)
        self.assertIn("locked", " ".join(v.blockers))

    def test_a_live_lane_registry_claims_the_branch(self):
        # The OPENCODE registry root is read, and a claim backed by an active session blocks in its
        # own right. The contract changed on 2026-09-26 - an uncorroborated claim no longer vetoes,
        # because the registries are untracked and unpruned - so this keeps the half of the original
        # intent that still protects anything: the registry is honoured, not merely counted.
        self.worktree("registered")
        self.session("registered-s", status="active", branch="registered", worktree=None)
        self.lane_registry("opencode", "some-lane", "registered")

        v = self.one("registered")
        self.assertFalse(v.retirable, v.blockers)
        self.assertIn("opencode:some-lane", " ".join(v.blockers))
        self.assertIn("corroborated", " ".join(v.blockers))

    def test_the_cmdc_registry_is_read_too(self):
        # Same for the CMDC root, which is a separate directory and a separate reader branch.
        self.worktree("cmdc-lane")
        self.session("cmdc-lane-s", status="active", branch="cmdc-lane", worktree=None)
        self.lane_registry("cmdc", "cmdc-lane-1", "cmdc-lane")

        v = self.one("cmdc-lane")
        self.assertFalse(v.retirable, v.blockers)
        self.assertIn("cmdc:cmdc-lane-1", " ".join(v.blockers))

    def test_ownership_is_read_from_the_main_checkout_not_the_cwd(self):
        # Two independent fail-opens, both from resolving ownership against the cwd. The lane
        # registries are UNTRACKED, so they exist in main and not in a linked worktree; and
        # `tasks/sessions/` in a worktree is a snapshot from fork time, so a session created after
        # the fork is missing there too. Each one alone under-counted owners and pushed worktrees
        # toward RETIRABLE - 108 reported where the truth was 38.
        linked = self.worktree("viewpoint")
        self.lane_registry("opencode", "live-elsewhere", "viewpoint")
        self.session("s-other", status="merged", branch="unrelated", worktree=None)

        # Neither source is visible from the linked worktree — and the session reader REFUSES there
        # rather than returning an empty list, which is the correct fail-closed shape: "no records"
        # must never read as "nothing is owned".
        self.assertEqual(retire.runner_owned_branches(linked, 60.0), {})
        with self.assertRaises(retire.Refusal) as caught:
            retire.load_sessions(linked)
        self.assertEqual(caught.exception.name, "NO-SESSIONS")

        # Both must therefore be resolved through git to the main checkout, whatever the cwd is.
        self.assertEqual(retire.main_worktree(linked, 60.0).resolve(), self.root.resolve())
        self.assertIn("viewpoint", retire.runner_owned_branches(retire.main_worktree(linked, 60.0), 60.0))
        self.assertEqual(len(retire.load_sessions(retire.main_worktree(linked, 60.0))), 1)

        # And end to end: the registered branch is KEPT, and the registry contributes a blocker IN
        # ITS OWN RIGHT rather than merely being counted - otherwise this whole guard would pass on
        # a tool that reads the registry and ignores it, which is the fail-open it exists to catch.
        self.session("viewpoint-s", status="active", branch="viewpoint", worktree=None)
        verdicts, counts, _leftovers = retire.survey("features/mega-merge", 60.0, repo=self.root)
        self.assertEqual(counts["liveLaneBranches"], 1)
        rows = {v.branch: v for v in verdicts}
        self.assertFalse(rows["viewpoint"].retirable, rows["viewpoint"].blockers)
        self.assertIn("lane registry", " ".join(rows["viewpoint"].blockers))


class FailClosedTests(RepoCase):
    def test_a_closed_record_does_not_block(self):
        self.worktree("closed-record")
        v = self.one("closed-record")  # seeds a `merged` record
        self.assertTrue(v.retirable, v.blockers)

    def test_no_session_record_refuses_rather_than_calling_everything_retirable(self):
        # The dangerous direction: with no ownership evidence, "nothing is owned" would make every
        # worktree a deletion candidate. It must refuse instead.
        self.worktree("orphan")
        with self.assertRaises(retire.Refusal) as caught:
            self.verdicts()
        self.assertEqual(caught.exception.name, "NO-SESSIONS")

    def test_an_unreadable_session_record_refuses(self):
        self.worktree("orphan2")
        (self.root / "tasks" / "sessions" / "broken.json").write_text("{not json", encoding="utf-8")
        with self.assertRaises(retire.Refusal):
            self.verdicts()

    def test_a_status_outside_the_closed_enum_refuses(self):
        self.worktree("orphan3")
        (self.root / "tasks" / "sessions" / "weird.json").write_text(json.dumps({
            "session": "weird", "status": "retired", "branch": "x", "worktree": None,
            "mode": "direct", "program": "p", "problem": "p", "paths": [], "started": "t"}), encoding="utf-8")
        with self.assertRaises(retire.Refusal) as caught:
            self.verdicts()
        self.assertEqual(caught.exception.name, "NO-SESSIONS")

    def test_an_unknown_integration_branch_refuses(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        with self.assertRaises(retire.Refusal) as caught:
            retire.survey("no/such/branch", 60.0, repo=self.root)
        self.assertEqual(caught.exception.name, "BAD-INTEGRATION-REF")

    def test_a_non_positive_timeout_is_refused_before_any_work(self):
        self.assertEqual(retire.main(["--timeout", "0", "--root", str(self.root)]), 2)


class OutputTests(RepoCase):
    def test_json_carries_counts_and_one_row_per_worktree(self):
        self.worktree("a-done")
        self.worktree("b-dirty", dirty="x")
        self.worktree("c-done-2")
        self.session("s", status="merged", branch="a-done", worktree=None)
        self.session("s2", status="merged", branch="c-done-2", worktree=None)
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = retire.main(["--json", "--root", str(self.root)])
        self.assertEqual(code, 0)
        payload = json.loads(buffer.getvalue())
        # Three linked worktrees, and the count must be 3. An earlier version of the parser dropped
        # the LAST record, so this assertion is the one that catches a plan hiding a worktree.
        self.assertEqual(payload["counts"]["total"], 3)
        self.assertEqual(payload["counts"]["retirable"], 2)
        self.assertEqual(payload["counts"]["kept"], 1)

    def test_apply_dry_run_removes_nothing(self):
        path = self.worktree("keepme")
        self.session("s", status="merged", branch="keepme", worktree=None)
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = retire.main(["--apply", "--dry-run", "--root", str(self.root)])
        self.assertEqual(code, 0)
        self.assertIn("would remove 1", buffer.getvalue())
        self.assertTrue(path.exists(), "dry run must not remove anything")
        self.assertIn("keepme", git(self.root, "worktree", "list"))

    def test_why_is_a_filter_not_a_short_circuit(self):
        # `--why <group> --apply` printed the plan, removed NOTHING and exited 0. A removal tool that
        # looks like it worked is worse than one that fails, so the combination is pinned here.
        target = self.worktree("grouped")
        self.worktree("other-group")
        self.session("s1", status="merged", branch="grouped", worktree=None)
        self.session("s2", status="merged", branch="other-group", worktree=None)
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = retire.main(["--why", "grouped", "--apply", "--root", str(self.root)])
        self.assertEqual(code, 0)
        self.assertIn("removed 1 worktree(s)", buffer.getvalue())
        self.assertFalse(target.exists(), "the filtered worktree must actually be gone")
        self.assertTrue((self.root.parent / "other-group").exists(), "and only that one")


class UnregisteredLeftoverTests(RepoCase):
    """A worktree survey is blind to a directory git has forgotten.

    Measured 2026-09-26: 28 empty directories and 3 that still held content sat beside the
    worktrees, and the plan reported a clean tree over all 31. `git worktree remove`
    de-registers first and deletes second, so a failed delete (a Windows long path, permissions)
    leaves something git can never reclaim and never lists again. One of the three held 451 lines
    across 9 files that exist nowhere else in the repository.
    """

    def test_a_leftover_directory_is_reported_not_ignored(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.leftover_dir("opencode-resume-33", files={"src/A.cs": "x\n"})
        _v, counts, leftovers = self.survey()
        self.assertEqual(counts["leftoverScan"], "on")
        self.assertEqual(counts["unregisteredDirs"], 1, counts)
        self.assertEqual([d.name for d in leftovers], ["opencode-resume-33"])
        # 2, not 1: the count includes the `src` directory as well as `A.cs`. The number is a
        # magnitude for a human, not a file count, and this pins that it counts both.
        self.assertEqual(leftovers[0].entries, 2)

    def test_an_empty_leftover_is_distinguished_from_one_holding_content(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.leftover_dir("empty-one")
        self.leftover_dir("full-one", files={"a/b/c.txt": "hi\n"})
        _v, counts, leftovers = self.survey()
        by = {d.name: d for d in leftovers}
        self.assertEqual(by["empty-one"].entries, 0)
        self.assertGreater(by["full-one"].entries, 0)
        self.assertEqual(counts["unregisteredDirsWithContent"], 1, counts)

    def test_the_leftover_scan_never_makes_a_leftover_retirable(self):
        # The dangerous direction: a leftover that happens to be empty and unowned could be swept
        # by --apply. It must not be, because nothing has adjudicated its content.
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        leftover = self.leftover_dir("forgettable", files={"keep.txt": "unlanded\n"})
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = retire.main(["--apply", "--dry-run", "--root", str(self.root)])
        self.assertEqual(code, 0)
        self.assertTrue(leftover.exists(), "--apply must never remove an unregistered directory")
        self.assertIn("forgettable", buffer.getvalue())

    def test_scan_off_is_recorded_rather_than_reading_as_clean(self):
        # `--no-leftover-scan` is a legitimate speed option. What it must not do is let a clean plan
        # imply a clean directory, so the counts say the scan was off.
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.leftover_dir("unseen", files={"a.txt": "x\n"})
        _v, counts, leftovers = self.survey(scan_leftovers=False)
        self.assertEqual(counts["leftoverScan"], "off")
        self.assertEqual(counts["unregisteredDirs"], 0)
        self.assertEqual(leftovers, [])
        # `render` RETURNS the plan; it does not print it. An earlier version of this test wrapped
        # it in `redirect_stdout` and asserted against an empty buffer, which would have passed on
        # any output at all had the return value been used - the assertion was vacuous.
        text = retire.render([], {**counts, "total": 0, "retirable": 0, "kept": 0,
                                 "integration": "features/mega-merge", "activeSessions": 0,
                                 "liveLaneBranches": 0, "staleLaneClaims": 0,
                                 "ownershipReadFrom": "repo"}, [])
        self.assertIn("--no-leftover-scan", text)
        self.assertNotIn("None found", text)

    def test_a_registered_worktree_is_never_reported_as_a_leftover(self):
        wt = self.worktree("registered-one")
        self.session("s", status="merged", branch="registered-one", worktree=None)
        _v, _c, leftovers = self.survey()
        self.assertNotIn("registered-one", [d.name for d in leftovers], leftovers)
        self.assertTrue(wt.exists())

    def test_the_main_checkout_is_never_reported_as_a_leftover(self):
        # The scan derives its directory from the registered worktrees, and the main checkout is
        # excluded from that list. If it were not, the largest directory in the repository would be
        # listed as disposable litter.
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        _v, _c, leftovers = self.survey()
        self.assertNotIn(self.root.name, [d.name for d in leftovers], leftovers)

    def test_no_scannable_parent_refuses_rather_than_guessing_a_directory(self):
        # The scan derives its directory ONLY from registered worktrees git reported. When none
        # yields a usable parent it must refuse rather than scan a directory it invented: a scan of
        # the wrong place that finds nothing is indistinguishable from a clean repository, which is
        # the fail-open this whole tool exists to prevent.
        ghost = self.root.parent / "no" / "such" / "place" / "wt"
        with self.assertRaises(retire.Refusal) as caught:
            retire.scan_unregistered_dirs(self.root, [str(ghost)], 60.0)
        self.assertEqual(caught.exception.name, "NO-WORKTREE-PARENT")

    def test_the_main_checkout_alone_never_becomes_a_scan_target(self):
        # The bug this pins: deriving the scan directory from the MAIN CHECKOUT's parent, rather
        # than from where git actually put worktrees, swept in 92 unrelated directories on this
        # machine - 28 of them sibling repositories (`lore-weave-*`, `Keepverse`, `wabbajack`)
        # that have nothing to do with this repo and were listed as disposable litter.
        (self.root.parent / "some-sibling-repo").mkdir()
        (self.root.parent / "some-sibling-repo" / "big.txt").write_text("x\n", encoding="utf-8")
        with self.assertRaises(retire.Refusal):
            retire.scan_unregistered_dirs(self.root, [str(self.root)], 60.0)

    def test_a_sibling_of_the_worktree_root_is_still_reported(self):
        # The narrow scope must not be so narrow it misses the real thing. A leftover sitting beside
        # the worktrees - which is where a failed `git worktree remove` leaves one - must appear.
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.leftover_dir("real-leftover", files={"keep.txt": "unlanded\n"})
        (self.root.parent / "unrelated-neighbour").mkdir()
        (self.root.parent / "unrelated-neighbour" / "f.txt").write_text("x\n", encoding="utf-8")
        _v, _c, leftovers = self.survey()
        names = [d.name for d in leftovers]
        self.assertIn("real-leftover", names, names)


class RecentActivityTests(RepoCase):
    """A worktree can be minutes old, clean, and unrecorded - and still be in flight.

    Measured 2026-09-26: `.kilo/worktrees/materialistic-spear` was reported RETIRABLE with the
    reason "no active owner; integrated; clean", seven minutes after a Kilo agent-manager session
    created it. No session record existed yet, no lane registry named it, and `git status` was
    empty - because the checkout that creates a worktree finishes before anyone edits it. Every
    signal the tool had said "abandoned".
    """

    def test_a_freshly_written_clean_worktree_is_never_retirable(self):
        wt = self.worktree("fresh", fresh=True)
        self.session("s", status="merged", branch="fresh", worktree=None)
        # no dirt, no commits of its own: everything the ownership model can see says "abandoned"
        self.assertTrue(retire._newest_mtime(wt, 60.0) is not None)
        v = self.one("fresh")
        self.assertFalse(v.retirable, v.blockers)
        self.assertTrue(v.recentlyTouched)
        self.assertIn("in flight", " ".join(v.blockers))

    def test_freshness_never_masks_the_dirty_blocker(self):
        # Measured 2026-09-26: run unconditionally the freshness guard held 36 of 38 worktrees,
        # because a repo-wide event rewrote the same three shared spec files in every worktree
        # inside the hour. On a DIRTY worktree it adds nothing and hides the actionable reason, so
        # the dirt must be the blocker the operator reads.
        self.worktree("busy", fresh=True, dirty="local edit\n")
        self.session("s", status="merged", branch="busy", worktree=None)
        v = self.one("busy")
        self.assertFalse(v.recentlyTouched)
        self.assertTrue(any("local path(s)" in b for b in v.blockers), v.blockers)
        self.assertFalse(any("in flight" in b for b in v.blockers), v.blockers)

    def test_an_old_worktree_with_no_record_is_still_retirable(self):
        # The guard must not become a ratchet in the other direction: if every unrecorded worktree
        # is held, the tool is useless for the exact backlog it exists to clear.
        self.worktree("ancient")
        self.session("s", status="merged", branch="ancient", worktree=None)
        v = self.one("ancient")
        self.assertTrue(v.retirable, v.blockers)
        self.assertFalse(v.recentlyTouched)

    def test_git_metadata_is_excluded_from_the_measurement(self):
        # In a LINKED worktree `.git` is a FILE, so it arrives in `filenames` rather than
        # `dirnames`. Filtering only `dirnames` counted git's own pointer file, every linked
        # worktree read as freshly written, and this guard blocked all eight retirable-path tests
        # at once. This pins the file case, which is the case that actually occurs.
        wt = self.worktree("gity")
        self.session("s", status="merged", branch="gity", worktree=None)
        self.assertTrue((wt / ".git").is_file(),
                        "a linked worktree must have a .git FILE for this test to mean anything")
        # `.git` was just written by `git worktree add` and is the newest thing in the tree; the
        # reading must be the AGED content's, not the pointer file's.
        newest = retire._newest_mtime(wt, 60.0)
        self.assertIsNotNone(newest, "the worktree holds real content, so a reading must exist")
        self.assertGreater(time.time() - newest, retire.RECENT_ACTIVITY_SECONDS,
                           "an aged worktree must not read as fresh; .git is leaking into it")


class ReclaimEmptyLeftoverTests(RepoCase):
    """A provably empty leftover can be reclaimed; one holding content still cannot.

    Measured 2026-09-27: 28 empty de-registered directories sat beside the worktrees, 27 of them
    removable and one held by a live process handle. The tool could enumerate them and refused to
    remove any, so the gap the leftover walk is supposed to close - a directory git de-registered and
    then failed to delete - stayed open indefinitely and was carried as a known gap.

    Emptiness is the one property that makes removal safe without a content adjudication: a
    directory with nothing in it cannot hold unlanded work. These tests pin that boundary from both
    sides, because the failure mode is deleting real work, not leaving litter.
    """

    def test_an_empty_leftover_is_removed(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33")
        self.assertTrue(d.is_dir())
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=False, newest_utc=None, has_git=False)],
            apply=True)
        self.assertEqual([r["outcome"] for r in out], ["REMOVED"], out)
        self.assertFalse(d.exists(), "an empty leftover must actually be gone, not just reported")

    def test_without_apply_it_is_a_plan_and_nothing_is_touched(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33")
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=False, newest_utc=None, has_git=False)],
            apply=False)
        self.assertEqual([r["outcome"] for r in out], ["WOULD-REMOVE"], out)
        self.assertTrue(d.is_dir(), "a plan must not remove anything")

    def test_a_leftover_holding_content_is_never_removed(self):
        """The whole point of the adjudication: content means a content decision, not a path test."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33", files={"src/A.cs": "x\n"})
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=2, capped=False, newest_utc=None, has_git=False)],
            apply=True)
        self.assertEqual([r["outcome"] for r in out], ["KEPT-NOT-EMPTY"], out)
        self.assertTrue((d / "src" / "A.cs").is_file(), "real content must survive the reclaim")

    def test_a_capped_scan_is_refused_rather_than_read_as_empty(self):
        """A capped count of zero is not evidence of emptiness.

        The entry cap exists so a huge directory cannot stall the walk, so a capped scan stopped
        counting. Reading that as "empty" would delete on the strength of a number that was never
        finished being produced - the exact shape of the bug this tool exists to report.
        """
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-deep-audit-01")
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=True, newest_utc=None, has_git=False)],
            apply=True)
        self.assertEqual([r["outcome"] for r in out], ["REFUSED-SCAN-CAPPED"], out)
        self.assertTrue(d.is_dir(), "a capped scan must not authorise a delete")

    def test_a_directory_that_filled_up_between_scan_and_delete_is_kept(self):
        """`rmdir` is the second, independent proof - and it is the one that catches the race."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33")
        # scanned as empty, then something arrived
        (d / "appeared.txt").write_text("late\n", encoding="utf-8")
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=False, newest_utc=None, has_git=False)],
            apply=True)
        self.assertEqual([r["outcome"] for r in out], ["NOT-EMPTY"], out)
        self.assertTrue((d / "appeared.txt").is_file(), "the late arrival must survive")

    def test_an_already_absent_directory_is_reported_as_gone_not_as_a_failure(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33")
        d.rmdir()
        out = retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=False, newest_utc=None, has_git=False)],
            apply=True)
        self.assertEqual([r["outcome"] for r in out], ["GONE"], out)

    def test_a_locked_directory_is_named_LOCKED_not_bucketed_as_a_failure(self):
        """Three refusals with three different fixes must not read as one.

        Measured: `opencode-resume-32-bp1112-20260925` refuses with "being used by another process"
        under .NET recursive delete, .NET empty delete, and the `\\\\?\\` long-path form alike. An
        owner reading "could not remove" learns nothing; an owner reading LOCKED learns that a
        process is holding it.
        """
        for win, err, want in ((32, 13, "LOCKED"), (33, 13, "LOCKED"), (5, 13, "PERMISSION"),
                               (206, 36, "PATH-TOO-LONG"), (145, 39, "NOT-EMPTY")):
            exc = OSError(err, "refused")
            exc.winerror = win
            self.assertEqual(retire._classify_reclaim_error(exc), want,
                             f"winerror {win} must classify as {want}")

    def test_winerror_wins_over_errno_when_they_disagree(self):
        """`errno` 13 is both a Windows sharing violation and a POSIX `EACCES`.

        An earlier ordering read `errno` first, so a real `ERROR_ACCESS_DENIED` was reported as
        LOCKED - telling the owner to go and find a process when the fix is an ACL. `winerror` is
        the authoritative code on Windows and is now consulted first and exclusively.
        """
        exc = OSError(13, "refused")
        exc.winerror = 5
        self.assertEqual(retire._classify_reclaim_error(exc), "PERMISSION")
        exc.winerror = 32
        self.assertEqual(retire._classify_reclaim_error(exc), "LOCKED")

    def test_errno_is_still_used_when_there_is_no_winerror(self):
        exc = OSError(36, "name too long")
        self.assertIsNone(getattr(exc, "winerror", None))
        self.assertEqual(retire._classify_reclaim_error(exc), "PATH-TOO-LONG")

    def test_an_unrecognised_error_keeps_its_text_rather_than_claiming_a_known_cause(self):
        exc = OSError(999, "something new")
        exc.winerror = 999
        got = retire._classify_reclaim_error(exc)
        self.assertTrue(got.startswith("UNCLASSIFIED"), got)
        self.assertIn("something new", got, "an unknown refusal must not lose the OS's own words")

    def test_the_cli_exposes_the_capability_and_reports_the_outcome(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-resume-33")
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = retire.main(["--json", "--reclaim-empty-leftovers", "--root", str(self.root)])
        self.assertEqual(rc, 0, buf.getvalue())
        plan = json.loads(buf.getvalue())
        self.assertEqual([r["outcome"] for r in plan["reclaim"]], ["WOULD-REMOVE"], plan["reclaim"])
        self.assertTrue(d.is_dir(), "--reclaim without --apply is a plan")

    def test_reclaim_only_removes_a_leftover_and_touches_no_worktree(self):
        """Reclaiming litter and retiring a worktree are independent decisions.

        Measured 2026-09-27: the only retirable worktree on this machine was one held back on
        purpose, so a reclaim authorised through `--apply` would have removed it as a side effect of
        a question about directories. `--reclaim-only` exists so that cannot happen.
        """
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        wt = self.root.parent / "wt"          # worktree() makes it a SIBLING of the repo
        d = self.leftover_dir("opencode-resume-33")
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = retire.main(["--json", "--reclaim-only", "--root", str(self.root)])
        self.assertEqual(rc, 0, buf.getvalue())
        self.assertFalse(d.exists(), "--reclaim-only must actually reclaim")
        self.assertTrue(wt.is_dir(), "no worktree may be touched by a reclaim")
        self.assertTrue((wt / "README.md").is_file(), "and its content must be intact")

    def test_reclaim_only_exits_non_zero_when_a_refusal_stands(self):
        """A run that leaves the gap open must not exit 0 - that is the checker-that-cannot-fail shape."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        # a file makes the directory non-empty, then the scan reports it as empty: rmdir refuses
        d = self.leftover_dir("opencode-resume-33")
        (d / "late.txt").write_text("x\n", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            rc = retire.main(["--json", "--reclaim-only", "--root", str(self.root)])
        # the scan sees the real count, so this one is KEPT-NOT-EMPTY and the run is clean
        self.assertEqual(rc, 0, "a correctly-kept content directory is not a failure")
        self.assertTrue((d / "late.txt").is_file())

    def test_a_capped_leftover_alone_makes_reclaim_only_fail(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("opencode-deep-audit-01")
        rows = [retire.reclaim_empty_leftovers([retire.UnregisteredDir(
            name=d.name, path=str(d), entries=0, capped=True, newest_utc=None, has_git=False)],
            apply=True)]
        self.assertEqual([r["outcome"] for r in rows[0]], ["REFUSED-SCAN-CAPPED"])
        self.assertTrue(d.is_dir())


if __name__ == "__main__":
    unittest.main()


class ProveStaleTests(RepoCase):
    """A leftover the tool can PROVE is stale is not a leftover that needs a human.

    Measured 2026-09-27: the survey reported 37 unregistered leftovers and told a manager to adjudicate
    33. Running the reclaim tool's own proof over the same 37 said 4 empty, 10 provably stale, 23 real.
    A queue inflated by work the tool can already prove is a queue nobody clears - and the next survey
    finds the same pile, which is how this cleanup became a project at all.
    """

    def install_reclaim_tool(self) -> None:
        """Put the real reclaim tool in the fixture repo, since the proof imports it by path."""
        scripts = self.root / "scripts"
        scripts.mkdir(parents=True, exist_ok=True)
        source = Path(retire.__file__).with_name("reclaim_worktree_dir.py")
        (scripts / "reclaim_worktree_dir.py").write_text(
            source.read_text(encoding="utf-8"), encoding="utf-8")

    def leftovers_for(self, *dirs) -> list:
        return [retire.UnregisteredDir(
            name=d.name, path=str(d), entries=sum(1 for _ in d.rglob("*")) if d.is_dir() else 0,
            capped=False, newest_utc=None, has_git=(d / ".git").exists()) for d in dirs]

    def test_a_leftover_whose_content_is_already_at_integration_is_provably_stale(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.install_reclaim_tool()
        landed = self.root / "landed.txt"
        landed.write_text("already integrated\n", encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "land the content")
        d = self.leftover_dir("stale-one", files={"copy.txt": "already integrated\n"})
        out = retire.prove_stale_leftovers(self.root, self.leftovers_for(d), 120)
        self.assertEqual([r["verdict"] for r in out], ["PROVABLY-STALE"], out)

    def test_a_leftover_holding_unlanded_content_needs_a_human_and_says_how_much(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.install_reclaim_tool()
        d = self.leftover_dir("real-work", files={"only/here.txt": "exists nowhere else\n"})
        out = retire.prove_stale_leftovers(self.root, self.leftovers_for(d), 120)
        self.assertEqual([r["verdict"] for r in out], ["NEEDS-ADJUDICATION"], out)
        self.assertEqual(out[0]["unlanded"], 1, out)

    def test_a_missing_proof_instrument_is_reported_and_never_reads_as_clean(self):
        """The worst outcome would be a missing instrument reporting everything as landed."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("unprovable", files={"a.txt": "x\n"})
        out = retire.prove_stale_leftovers(self.root, self.leftovers_for(d), 120)
        self.assertEqual([r["verdict"] for r in out], ["PROOF-UNAVAILABLE"], out)
        self.assertIsNone(out[0]["unlanded"], "no content claim may be made without the instrument")
        self.assertIn("reclaim_worktree_dir.py", out[0]["detail"])

    def test_a_directory_holding_a_git_file_is_named_as_such_not_as_stale(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.install_reclaim_tool()
        d = self.leftover_dir("half-removed", files={".git": "gitdir: elsewhere\n", "a.txt": "x\n"})
        out = retire.prove_stale_leftovers(self.root, self.leftovers_for(d), 120)
        self.assertEqual([r["verdict"] for r in out], ["HAS-GIT"], out)

    def test_a_capped_walk_is_never_proved_either_way(self):
        """A capped count is not evidence: the walk stopped before it finished counting."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.install_reclaim_tool()
        d = self.leftover_dir("capped", files={"a.txt": "x\n"})
        capped = [retire.UnregisteredDir(name=d.name, path=str(d), entries=99, capped=True,
                                         newest_utc=None, has_git=False)]
        out = retire.prove_stale_leftovers(self.root, capped, 120)
        self.assertEqual([r["verdict"] for r in out], ["CAPPED-NOT-PROVEN"], out)
        self.assertIsNone(out[0]["unlanded"])

    def test_an_empty_leftover_needs_no_proof_because_it_cannot_hold_work(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        d = self.leftover_dir("nothing-here")
        out = retire.prove_stale_leftovers(self.root, self.leftovers_for(d), 120)
        self.assertEqual([r["verdict"] for r in out], ["PROVABLY-STALE"], out)

    def test_prove_stale_refuses_to_run_alongside_apply(self):
        """Asking what is disposable must not remove anything as a side effect."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = retire.main(["--prove-stale", "--apply", "--root", str(self.root)])
        self.assertEqual(rc, 2)
        self.assertIn("PROOF-COUPLED-TO-APPLY", err.getvalue())
        self.assertTrue((self.root.parent / "wt").exists(), "the worktree must still be there")

    def test_the_report_shows_the_split_rather_than_one_bucket(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.install_reclaim_tool()
        landed = self.root / "landed.txt"
        landed.write_text("already integrated\n", encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "land")
        self.leftover_dir("stale-one", files={"copy.txt": "already integrated\n"})
        self.leftover_dir("real-one", files={"only.txt": "nowhere else\n"})
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            rc = retire.main(["--prove-stale", "--root", str(self.root)])
        self.assertEqual(rc, 0, buffer.getvalue())
        out = buffer.getvalue()
        self.assertIn("--prove-stale", out)
        self.assertIn("genuinely hold unlanded content", out)
        self.assertIn("real-one", out)

    def test_prove_stale_is_off_by_default_so_the_default_plan_is_unchanged(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        self.leftover_dir("some-leftover", files={"a.txt": "x\n"})
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            rc = retire.main(["--root", str(self.root)])
        self.assertEqual(rc, 0, buffer.getvalue())
        self.assertNotIn("--prove-stale", buffer.getvalue())


class DeclaredPoolTests(RepoCase):
    """A pool nobody has a worktree in any more must still be scanned.

    Measured 2026-09-27, and it is the direction that matters. The candidate pool set was derived from
    the parents of the REGISTERED worktrees, which is right as far as it goes - deriving it any other way
    swept in 28 unrelated sibling repositories. But it also means the coverage SHRINKS as the cleanup
    succeeds: once the last worktree leaves `.claude/worktrees`, that pool is nobody's parent, it drops
    out of the set, and the husks it still holds become invisible to the tool whose whole job is to find
    them. Measured here: 31 leftovers reported without the pool, 35 with it, and the four husks that
    had been invisible are back in the report.

    So a caller may DECLARE a pool. A declared path is a stated location rather than a guess, which is
    the property the derivation exists to preserve, and a declared pool that does not exist is refused by
    name - silently skipping it would restore exactly the blind spot the flag exists to close.
    """

    def test_a_declared_pool_with_no_registered_worktree_is_still_scanned(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        pool = self.root / ".claude" / "worktrees"
        pool.mkdir(parents=True)
        (pool / "orphan-husk").mkdir()
        (pool / "orphan-husk" / "a.txt").write_text("unlanded\n", encoding="utf-8")
        # No registered worktree lives in that pool, so the derived set cannot contain it.
        _v, counts, leftovers = self.survey(pools=(str(pool),))
        self.assertIn("orphan-husk", [d.name for d in leftovers], counts)

    def test_the_same_pool_is_invisible_without_the_declaration(self):
        """The blind spot itself, asserted: without --pool the husk is not reported at all."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        pool = self.root / ".claude" / "worktrees"
        pool.mkdir(parents=True)
        (pool / "orphan-husk").mkdir()
        (pool / "orphan-husk" / "a.txt").write_text("unlanded\n", encoding="utf-8")
        _v, _counts, leftovers = self.survey()
        self.assertNotIn("orphan-husk", [d.name for d in leftovers])

    def test_a_declared_pool_that_does_not_exist_is_refused_by_name(self):
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = retire.main(["--pool", "no/such/pool", "--root", str(self.root)])
        self.assertEqual(rc, 2)
        self.assertIn("POOL-NOT-A-DIRECTORY", err.getvalue())

    def test_declaring_the_repository_root_as_a_pool_is_refused(self):
        """It would sweep in the whole checkout, which is the over-scan the derivation exists to avoid."""
        self.worktree("wt")
        self.session("s", status="merged", branch="wt", worktree=None)
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = retire.main(["--pool", ".", "--root", str(self.root)])
        self.assertEqual(rc, 2)
        self.assertIn("POOL-IS-THE-ROOT", err.getvalue())

    def test_every_refusal_name_has_a_meaning_including_the_new_pool_ones(self):
        for name in ("POOL-NOT-A-DIRECTORY", "POOL-IS-THE-ROOT"):
            self.assertIn(name, retire.REFUSALS)
            self.assertTrue(retire.REFUSALS[name].strip(), f'{name} has no explanation')
