"""Contract tests for `scripts/session-boundary-check.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the closed vocabularies, and both overlap helpers. It does not assert a message
body or a count of records.

Four things this suite exists to stop a later reader from "fixing":

  * **`$null` interpolates as the EMPTY STRING, and `str(None)` is `"None"`.** The report line printed
    `[]` for a complete record and `[None]` for one missing `status`, so a reader could not tell "no
    status" from "the status is the text None". The differential caught it, exactly as it caught the same
    class in `guard-class-system` — the second time in three ports, which is why `_ps_string` is a
    named helper rather than an inline `str()`.
  * **The `active` filter FOLDS case.** `$_.Record.status -eq 'active'` matches `ACTIVE`, so a
    case-folded record still participates in the overlap check. An exact comparison removed it from the
    set, which is a SILENT NARROWING: the guard reported clean on two sessions that do overlap. The
    vocabulary checks folded; this filter did not, and the differential found it.
  * **`$null -eq $rec.$f` is a NULL test, not a truthiness test.** A record with `started: 0` is
    COMPLETE. `is None` is the exact analogue; a truthiness check would report it absent.
  * **The main-worktree resolution is a FIXED DEFECT.** The original calls `git worktree list` with no
    `-C $RepoRoot`, so it resolves every record's worktree path against the CURRENT DIRECTORY's
    repository. Invisible in production because the CWD is the repo; fatal in every fixture. The port
    asks the repository it was told to check, and a test asserts it does.

Differential evidence: 48 fixtures, 43 identical in exit code and every emitted line, plus 5 declared
divergences — three of which are cases where the ORIGINAL is the defect, and the harness asserts the
PORT's verdict for those rather than asserting agreement. That comparison lives outside this file
because the PowerShell form no longer exists.

The closed vocabularies below are LOAD-BEARING IN BOTH DIRECTIONS:
`gk-core/tests/tools/test_program_status.py` reads these literals as its drift guard's owner, so changing one
here makes that test red, and that test is what proves `program_status.py` has not drifted.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# The file under test is the workspace root's, so it is asked of its owner rather than of REPO. `REPO /
# "scripts/session-boundary-check.py"` does not exist in gk-core, and this suite raised at COLLECTION because of it - which is
# why it was one of the dark suites, and why a collection error that aborts the pytest run could hide
# the rest of the tree. The session records are the workspace root's too, so both sites move together.
sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import workspace_root  # noqa: E402

_WORKSPACE = workspace_root(REPO)

SCRIPT = _WORKSPACE / "scripts" / "session-boundary-check.py"

_spec = importlib.util.spec_from_file_location("session_boundary_check", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["session_boundary_check"] = guard
_spec.loader.exec_module(guard)

GIT_TIMEOUT = 300


def git(root: Path, *args: str) -> str:
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          timeout=GIT_TIMEOUT)
    if proc.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed: {proc.stdout}{proc.stderr}")
    return proc.stdout.strip()


BASE = {"session": "alpha", "program": "p", "problem": "one thing", "mode": "direct",
        "branch": "main", "paths": ["src/**"], "started": "2026-09-26", "status": "merged"}


def record(session: str, **over) -> dict:
    rec = dict(BASE)
    rec["session"] = session
    rec.update(over)
    return rec


class Repo:
    """A throwaway git repository with session records on disk."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="sbc-"))
        (self.root / "tasks" / "sessions").mkdir(parents=True)
        git(self.root, "init", "-q", "-b", "main")
        git(self.root, "config", "user.email", "guard-test@example.invalid")
        git(self.root, "config", "user.name", "guard-test")
        (self.root / "base.txt").write_text("base\n", encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "base")

    def add(self, name: str, body) -> None:
        (self.root / "tasks" / "sessions" / name).write_text(
            body if isinstance(body, str) else json.dumps(body), encoding="utf-8")

    def branch(self, name: str) -> None:
        existing = git(self.root, "branch", "--format=%(refname:short)").splitlines()
        if name not in {e.strip() for e in existing}:
            git(self.root, "branch", name)

    def worktree(self, name: str) -> None:
        git(self.root, "worktree", "add", "-q", "--detach", name, "HEAD")

    def check(self, **kwargs) -> dict:
        return guard.check(self.root, **kwargs)

    def __enter__(self) -> "Repo":
        return self

    def __exit__(self, *_exc) -> None:
        import shutil
        shutil.rmtree(self.root, ignore_errors=True)


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=GIT_TIMEOUT)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(0, proc.returncode)
        self.assertIn("session-boundary-check.py", proc.stdout)

    def test_the_diff_fence_flags_are_kebab_case(self) -> None:
        # `VerificationTopologyTests` asserts the SCRIPT'S OWN TEXT carries these knobs. That test is
        # repointed at the .py, and the flags are its subject, so their spelling is contract.
        for flag in ("--diff-base-ref", "--diff-head-ref", "--require-diff-fence", "--session", "--ci"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                                      capture_output=True, text=True, timeout=300)
                self.assertIn(flag, proc.stdout)

    def test_the_verdict_goes_to_stdout_and_the_findings_to_stderr(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", branch="never-created"))
            result = run("--repo-root", str(repo.root))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("[session-boundary] DRIFT", result["stdout"])
        self.assertIn("does not exist", result["stderr"])
        self.assertNotIn("does not exist", result["stdout"])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_a_missing_repo_root_is_REFUSED_not_reported_clean(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sbc-gone-") as tmp:
            result = run("--repo-root", str(Path(tmp) / "no-such-dir"))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertNotIn("clean", result["stdout"])


class TheRequiredFieldVocabulary(unittest.TestCase):
    def test_every_field_except_worktree_is_required(self) -> None:
        # `worktree` is in REQUIRED_FIELDS and EXCLUDED from the check, because it is mode-dependent:
        # a `direct` record has none. Checking it would make every direct record fail.
        self.assertIn("worktree", guard.REQUIRED_FIELDS)
        self.assertNotIn("worktree", guard.REQUIRED_UNLESS_MODE_DEPENDENT)
        for field in ("session", "program", "problem", "mode", "branch", "paths", "started", "status"):
            with self.subTest(field=field):
                self.assertIn(field, guard.REQUIRED_UNLESS_MODE_DEPENDENT)

    def test_each_missing_field_is_reported(self) -> None:
        for field in guard.REQUIRED_UNLESS_MODE_DEPENDENT:
            with self.subTest(field=field):
                with Repo() as repo:
                    broken = record("alpha")
                    broken.pop(field)
                    repo.add("alpha.json", broken)
                    got = repo.check()
                self.assertEqual("FAIL", got["verdict"])
                self.assertTrue(any(f"missing required field '{field}'" in p
                                    for p in got["problems"]), got["problems"])

    def test_a_field_present_but_FALSY_is_NOT_missing(self) -> None:
        # `$null -eq $rec.$f` is a NULL test. A record with `started: 0` is complete; a truthiness check
        # would report it absent and this guard would fail every record that starts at epoch zero.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", started=0, program="", problem=""))
            got = repo.check()
        self.assertEqual("OK", got["verdict"], got["problems"])


class TheClosedVocabularies(unittest.TestCase):
    def test_the_vocabulary_triples(self) -> None:
        # A closed vocabulary the code owns: a fourth mode or a fifth status is a reviewed change, and
        # `program_status.py` holds the same tuples as literals.
        self.assertEqual(("direct", "worktree"), guard.VALID_MODES)
        self.assertEqual(("active", "merged", "abandoned"), guard.VALID_STATUS)

    def test_they_FOLD_case(self) -> None:
        # `-notcontains` folds in PowerShell, so `DIRECT` and `ACTIVE` are accepted.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", mode="DIRECT", status="ACTIVE"))
            self.assertEqual("OK", repo.check()["verdict"])

    def test_an_unknown_value_is_reported(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="retired"))
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("is not one of active/merged/abandoned" in p for p in got["problems"]))

    def test_the_active_filter_FOLDS_case_too_and_that_is_not_optional(self) -> None:
        # THE BUG THE DIFFERENTIAL FOUND. `-eq` folds, so an `ACTIVE` record participates in the
        # overlap check. An exact comparison dropped it from the set, and the guard reported clean on
        # two sessions that DO overlap - a silent narrowing, which is the worst shape a guard has.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="ACTIVE", paths=["src/**"]))
            repo.add("beta.json", record("beta", status="active", paths=["src/FusionRpg.Core/A.cs"]))
            got = repo.check()
        self.assertEqual(2, got["active"], "a case-folded ACTIVE record is active for this guard")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("both claim" in p for p in got["problems"]), got["problems"])

    def test_is_matches_only_a_real_string(self) -> None:
        self.assertTrue(guard._is("ACTIVE", "active"))
        self.assertFalse(guard._is(None, "active"))
        self.assertFalse(guard._is("retired", "active"))


class TheNullRendering(unittest.TestCase):
    def test_a_json_null_renders_as_the_EMPTY_STRING(self) -> None:
        self.assertEqual("", guard._ps_string(None))
        self.assertNotEqual("None", guard._ps_string(None))

    def test_the_report_line_shows_no_status_as_EMPTY_not_None(self) -> None:
        # The record listing goes to stdout and a reader must be able to see the difference between
        # "no status" and "the status is the text None".
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status=""))
            repo.add("beta.json", "{ not json")
            result = run("--repo-root", str(repo.root))
        self.assertNotIn("[None]", result["stdout"], result["stdout"])
        self.assertNotIn("-> None", result["stdout"], result["stdout"])


class TheOverlapHelpers(unittest.TestCase):
    def test_a_wildcard_and_a_plain_path_overlap(self) -> None:
        self.assertTrue(guard.path_overlap("src/**", "src/FusionRpg.Core/A.cs"))

    def test_the_trailing_wildcard_run_is_trimmed_off_both_sides(self) -> None:
        # The original trims trailing `/`, then `*`, then `/`. So `a/**` -> `a/` -> `a`, and a wildcard
        # entry equals a plain one.
        self.assertTrue(guard.path_overlap("a/**", "a"))
        self.assertTrue(guard.path_overlap("a/*", "a/"))
        # `a` IS a prefix of `a/b`, so claiming both IS an overlap - a parent claim covers its child.
        self.assertTrue(guard.path_overlap("a", "a/b"))
        # Neither a prefix of the other, so no overlap. NOT "a/bb": that DOES start with "a/b", which
        # is the same prefix rule in the other direction.
        self.assertFalse(guard.path_overlap("a/b", "a/c"))

    def test_the_three_call_trim_is_EQUIVALENT_to_one_rstrip_and_that_is_asserted_not_assumed(self) -> None:
        # A mutation that collapses the three calls into one `rstrip("/*")` changes NOTHING, because
        # rstrip strips repeatedly over a set. An earlier version of this file claimed otherwise in a
        # comment AND ran that no-op mutation as if it were a real one, which reported a green suite
        # against a mutant that never differed. So the equivalence is pinned here, explicitly, and the
        # comment in the guard says the same thing.
        for raw in ("a/**", "a/*", "a/", "a//", "a*//", "a*/", "a", "a/b/c", "**", "a***///"):
            with self.subTest(raw=raw):
                self.assertEqual(raw.rstrip("/*"),
                                 raw.rstrip("/").rstrip("*").rstrip("/"),
                                 "the collapsed form must stay equivalent, or the comment is wrong")

    def test_only_a_NON_prefix_sibling_is_not_an_overlap(self) -> None:
        # This is the behaviour a real mutation would break: replacing the prefix test with equality
        # would turn a non-overlapping pair into one, and only a pair where NEITHER side is a prefix of
        # the other can assert that. `"a/b"` vs `"a/bb"` cannot: the second starts with the first, so
        # they DO overlap - which this file got wrong once and then wrote again, hence the name.
        self.assertFalse(guard.path_overlap("src/FusionRpg.Core", "src/FusionRpg.Data"))
        self.assertFalse(guard.path_overlap("a/b", "a/c"))
        self.assertFalse(guard.path_overlap("web/x.ts", "src/y.ts"))
        # And the direction that IS a prefix stays an overlap, so the negatives above are not vacuous.
        self.assertTrue(guard.path_overlap("src", "src/FusionRpg.Data"))

    def test_overlap_FOLDS_case(self) -> None:
        self.assertTrue(guard.path_overlap("src/Core", "SRC/core"))

    def test_an_empty_side_never_overlaps(self) -> None:
        self.assertFalse(guard.path_overlap("", "a"))
        self.assertFalse(guard.path_overlap("/", "a"))
        self.assertFalse(guard.path_overlap("**", "a"))

    def test_a_wildcard_pattern_matches_a_nested_path_and_FOLDS_case(self) -> None:
        self.assertTrue(guard.session_path_matches("src/FusionRpg.Core/A.cs", "src/**"))
        self.assertTrue(guard.session_path_matches("SRC/Core/A.cs", "src/**"))
        self.assertFalse(guard.session_path_matches("web/x.ts", "src/**"))

    def test_a_blank_pattern_matches_nothing(self) -> None:
        for blank in ("", "   "):
            with self.subTest(blank=blank):
                self.assertFalse(guard.session_path_matches("src/A.cs", blank))

    def test_a_LEADING_SLASH_is_stripped_from_the_PATH_but_not_the_pattern(self) -> None:
        # `$path.Replace('\\','/').TrimStart('/')` on the path only, so a record's pattern still has to
        # be written without a leading slash.
        self.assertTrue(guard.session_path_matches("/src/A.cs", "src/**"))
        self.assertFalse(guard.session_path_matches("/src/A.cs", "/src/**"))

    def test_the_match_is_platform_independent(self) -> None:
        # `fnmatch.fnmatch` would apply the platform's own normcase, so the verdict could change with
        # the OS. Both sides are lower-cased and `fnmatchcase` is used, so it cannot.
        self.assertTrue(guard.session_path_matches("SRC/A.CS", "src/**"))
        self.assertFalse(guard.session_path_matches("WEB/A.TS", "src/**"))


class TheMainWorktreeResolution(unittest.TestCase):
    """A DEFECT THE PORT FIXES. See the module docstring for the measurement."""

    def test_a_worktree_path_that_EXISTS_is_found(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree", worktree="wt"))
            repo.worktree("wt")
            got = repo.check()
        self.assertEqual("OK", got["verdict"], got["problems"])

    def test_a_worktree_path_that_does_NOT_exist_is_reported(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree",
                                         worktree="no/such/dir"))
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("no longer exists" in p for p in got["problems"]))

    def test_an_ABSOLUTE_worktree_path_is_honoured_as_written(self) -> None:
        with Repo() as repo:
            target = repo.root / "elsewhere"
            target.mkdir()
            repo.add("alpha.json", record("alpha", status="active", mode="worktree",
                                         worktree=str(target)))
            self.assertEqual("OK", repo.check()["verdict"])

    def test_the_resolution_uses_the_REPO_ROOT_not_the_process_directory(self) -> None:
        # The original ran `git worktree list` with no `-C`, so it resolved against the CWD's repository.
        # Running this from the REAL repo is exactly the condition that hid the defect, so the test
        # asserts the port still finds the FIXTURE's worktree while the CWD is elsewhere.
        repo_root = REPO
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree", worktree="wt"))
            repo.worktree("wt")
            found = guard.main_worktree_root(repo.root)
        self.assertEqual(repo.root.resolve(), found.resolve())
        self.assertNotEqual(repo_root.resolve(), found.resolve(),
                            "the guard must ask the repository it was given, not the one it runs in")

    def test_from_inside_a_LINKED_worktree_it_returns_the_MAIN_root_not_the_linked_one(self) -> None:
        # THE FUNCTION'S ACTUAL PURPOSE, and the only assertion that catches `return root`. A record's
        # `worktree` field is written relative to the MAIN checkout, so a guard run from inside a lane's
        # worktree that resolved against its own root reported every such record as "no longer exists" -
        # 7 records, measured 2026-09-22 (RECON-F9). Resolving to the linked root reproduces that.
        with Repo() as repo:
            repo.worktree("wt")
            linked = repo.root / "wt"
            self.assertTrue(linked.is_dir())
            found = guard.main_worktree_root(linked)
        self.assertEqual(repo.root.resolve(), found.resolve())
        self.assertNotEqual(linked.resolve(), found.resolve(),
                            "resolving to the LINKED root is the false positive this function exists to "
                            "prevent")


class CiMode(unittest.TestCase):
    def test_ci_SKIPS_the_branch_and_worktree_checks(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree", worktree="gone"))
            got = repo.check(ci=True)
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertTrue(any("SKIPPED" in s for s in got["skipped"]), got["skipped"])

    def test_ci_SKIPS_the_branch_existence_check_too_and_not_only_the_worktree_path(self) -> None:
        # The first version of the CI test used a record whose branch EXISTED, so it only ever
        # exercised the worktree-path check. Dropping the `not ci` from the branch check - a plausible
        # "simplification" - was invisible to it. This one has a branch that does not exist at all.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", branch="never-created"))
            self.assertEqual("OK", repo.check(ci=True)["verdict"])

    def test_without_ci_that_same_missing_branch_IS_reported(self) -> None:
        # The pair that makes the CI skip meaningful: the same fixture, one flag apart. A skip that
        # fires unconditionally would also pass the test above.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", branch="never-created"))
            got = repo.check(ci=False)
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("does not exist" in p for p in got["problems"]), got["problems"])

    def test_ci_still_checks_the_field_vocabulary(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", mode="nonsense"))
            self.assertEqual("FAIL", repo.check(ci=True)["verdict"])

    def test_ci_still_reports_an_active_worktree_record_with_no_path(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree", worktree="  "))
            self.assertEqual("FAIL", repo.check(ci=True)["verdict"])


class TheTwoWorktreeExemption(unittest.TestCase):
    def test_two_ACTIVE_worktree_sessions_claiming_the_same_path_do_NOT_overlap(self) -> None:
        # Two worktree sessions are isolated BY CONSTRUCTION - each has its own directory, so an
        # overlap between them is not a conflict. This was in the differential only, which means the
        # contract suite would have stayed green on a port that dropped the exemption and made the
        # guard report drift for every pair of concurrent worktree lanes.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="worktree", worktree="wt",
                                         paths=["src/**"]))
            repo.add("beta.json", record("beta", status="active", mode="worktree", worktree="wt2",
                                         paths=["src/**"]))
            repo.worktree("wt")
            repo.worktree("wt2")
            got = repo.check()
        self.assertEqual(2, got["active"])
        self.assertEqual("OK", got["verdict"], got["problems"])

    def test_the_exemption_needs_BOTH_to_be_worktree(self) -> None:
        # The exemption is `and`, not `or`, so a direct session still overlaps a worktree one.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", mode="direct", paths=["src/**"]))
            repo.add("beta.json", record("beta", status="active", mode="worktree", worktree="wt",
                                         paths=["src/**"]))
            repo.worktree("wt")
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("both claim" in p for p in got["problems"]), got["problems"])


class TheReviewedDiffFence(unittest.TestCase):
    def test_it_needs_BOTH_refs(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active"))
            with self.assertRaises(guard.Refusal) as caught:
                repo.check(session="alpha", require_diff_fence=True)
        self.assertEqual("DIFF-FENCE-INCOMPLETE", caught.exception.reason)

    def test_it_needs_a_SESSION(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active"))
            with self.assertRaises(guard.Refusal) as caught:
                repo.check(require_diff_fence=True, diff_base_ref="HEAD", diff_head_ref="HEAD")
        self.assertEqual("DIFF-FENCE-NO-SESSION", caught.exception.reason)

    def test_it_needs_a_record_for_the_session(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active"))
            got = repo.check(session="ghost", require_diff_fence=True,
                             diff_base_ref="HEAD", diff_head_ref="HEAD")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("no session record for 'ghost'" in p for p in got["problems"]))

    def test_it_rejects_an_INACTIVE_session(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="merged"))
            got = repo.check(session="alpha", require_diff_fence=True,
                             diff_base_ref="HEAD", diff_head_ref="HEAD")
        self.assertTrue(any("is not active" in p for p in got["problems"]), got["problems"])

    def test_a_path_OUTSIDE_the_fence_is_reported(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", paths=["src/**"]))
            (repo.root / "tasks" / "sessions" / "beta.json").write_text(
                json.dumps(record("beta", status="active", paths=["web/**"])), encoding="utf-8")
            git(repo.root, "add", "-A")
            git(repo.root, "commit", "-q", "-m", "add beta")
            got = repo.check(session="alpha", require_diff_fence=True,
                             diff_base_ref="HEAD~1", diff_head_ref="HEAD")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("outside session fence" in p for p in got["problems"]), got["problems"])

    def test_the_fence_is_the_records_PATTERNS_and_not_a_bare_prefix_test(self) -> None:
        # The fence is a WILDCARD list, so a path outside every pattern is refused while a path inside
        # one is allowed. The first version of the mutation aimed at this code was
        # `get("paths") or ["**"]`, which short-circuits on any fixture that HAS paths - it changed
        # nothing, and a green suite was reported against it. This pair pins both directions on a
        # fixture where the patterns genuinely decide the answer.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", paths=["src/**"]))
            (repo.root / "tasks" / "sessions" / "beta.json").write_text(
                json.dumps(record("beta", status="active", paths=["docs/**"])), encoding="utf-8")
            # The RECORDS are committed first, so the reviewed range holds only the intended edit.
            # Staging them together put `tasks/sessions/*.json` into the range, which is outside a
            # `src/**` fence, and the guard was right to refuse it - the test was measuring its own
            # fixture, which is the sixth time in this program that a fixture has been the finding.
            git(repo.root, "add", "-A")
            git(repo.root, "commit", "-q", "-m", "records")
            (repo.root / "src").mkdir()
            (repo.root / "src" / "Inside.cs").write_text("// inside\n", encoding="utf-8")
            git(repo.root, "add", "-A")
            git(repo.root, "commit", "-q", "-m", "inside the fence")
            got = repo.check(session="alpha", require_diff_fence=True,
                             diff_base_ref="HEAD~1", diff_head_ref="HEAD")
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertEqual(1, got["diff"]["changed"])

    def test_a_path_INSIDE_the_fence_passes(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="active", paths=["**"]))
            git(repo.root, "add", "-A")
            git(repo.root, "commit", "-q", "-m", "touch")
            got = repo.check(session="alpha", require_diff_fence=True,
                             diff_base_ref="HEAD~1", diff_head_ref="HEAD")
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertIsNotNone(got["diff"])
        self.assertEqual("alpha", got["diff"]["session"])


class UnclaimedWorktreeBranches(unittest.TestCase):
    def test_an_unclaimed_worktree_branch_is_drift(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha"))
            repo.branch("worktree-orphan")
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("is not claimed by any session record" in p for p in got["problems"]))

    def test_a_harness_agent_branch_is_LISTED_not_drift(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha"))
            repo.branch("worktree-agent-9f2c1ab")
            got = repo.check()
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertEqual(["worktree-agent-9f2c1ab"], got["harness_agents"])

    def test_the_harness_agent_pattern_FOLDS_case(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha"))
            repo.branch("WORKTREE-AGENT-9F2C1AB")
            got = repo.check()
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertEqual(1, len(got["harness_agents"]))

    def test_a_claimed_worktree_branch_is_not_drift(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", worktree="worktree-keep"))
            repo.branch("worktree-keep")
            self.assertEqual("OK", repo.check()["verdict"])

    def test_a_merged_records_GONE_branch_is_history_not_drift(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", status="merged", branch="deleted-long-ago"))
            self.assertEqual("OK", repo.check()["verdict"])


class SessionScoping(unittest.TestCase):
    def test_drift_is_split_between_mine_and_theirs(self) -> None:
        # beta owns a problem ALONE - a branch that does not exist - so it can only be attributed to
        # beta. Two active records with the SAME paths would instead produce an overlap owned by BOTH
        # sessions, which correctly lands in `problems` and is why the first version of this test found
        # `others` empty.
        with Repo() as repo:
            # Disjoint paths as well, or the two records ALSO overlap, and an overlap is owned by BOTH
            # sessions - so it correctly lands in `problems` and the first version of this test found
            # `others` empty for exactly that reason.
            repo.add("alpha.json", record("alpha", status="active", branch="never-created-a",
                                         paths=["src/**"]))
            repo.add("beta.json", record("beta", status="active", branch="never-created-b",
                                         paths=["web/**"]))
            got = repo.check(session="alpha")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(got["others"], "the other session's drift must be reported, not hidden")
        self.assertTrue(all("beta" in p for p in got["others"]), got["others"])
        self.assertFalse(any("beta" in p for p in got["problems"]), got["problems"])

    def test_a_session_with_no_record_is_drift(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", record("alpha"))
            got = repo.check(session="ghost")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("no session record for 'ghost'" in p for p in got["problems"]))

    def test_my_branch_counts_as_my_id(self) -> None:
        # A problem owned by a BRANCH belongs to the session that owns the branch, which is how an
        # unclaimed-branch finding is attributed.
        with Repo() as repo:
            repo.add("alpha.json", record("alpha", branch="feature-x"))
            repo.branch("feature-x")
            repo.add("beta.json", record("beta", branch="main"))
            got = repo.check(session="alpha")
        self.assertEqual("OK", got["verdict"], got["problems"])


class MalformedRecords(unittest.TestCase):
    def test_an_unparseable_record_is_reported_and_SKIPPED(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", "{ not json")
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("does not parse as JSON" in p for p in got["problems"]), got["problems"])
        self.assertEqual(0, got["records"], "a record that does not parse contributes no record")

    def test_a_record_that_is_not_an_OBJECT_is_refused_by_shape(self) -> None:
        with Repo() as repo:
            repo.add("alpha.json", "[1, 2, 3]")
            got = repo.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("not an object" in p for p in got["problems"]), got["problems"])

    def test_an_UNDERSCORE_prefixed_record_is_ignored_entirely(self) -> None:
        with Repo() as repo:
            repo.add("_template.json", record("template"))
            repo.add("alpha.json", record("alpha"))
            got = repo.check()
        self.assertEqual("OK", got["verdict"], got["problems"])
        self.assertEqual(1, got["records"])

    def test_no_sessions_directory_is_clean_and_SAYS_SO(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sbc-bare-") as tmp:
            root = Path(tmp)
            git(root, "init", "-q", "-b", "main")
            got = guard.check(root)
        self.assertEqual("OK", got["verdict"])
        self.assertTrue(any("tasks/sessions" in s for s in got["skipped"]), got["skipped"])


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "session", "ci", "records", "active", "branches", "harness_agents",
            "diff", "problems", "others", "skipped"}
    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with Repo() as ok:
            ok.add("alpha.json", record("alpha"))
            proc = subprocess.run([sys.executable, str(SCRIPT), "--repo-root", str(ok.root), "--json"],
                                  capture_output=True, text=True, timeout=GIT_TIMEOUT)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
        with Repo() as bad:
            bad.add("alpha.json", record("alpha", status="active", branch="never-created"))
            proc = subprocess.run([sys.executable, str(SCRIPT), "--repo-root", str(bad.root), "--json"],
                                  capture_output=True, text=True, timeout=GIT_TIMEOUT)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
            self.assertEqual("FAIL", json.loads(proc.stdout)["verdict"])

    def test_a_refusal_carries_its_reason(self) -> None:
        with Repo() as repo:
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--repo-root", str(repo.root), "--session", "alpha",
                 "--require-diff-fence", "--json"],
                capture_output=True, text=True, timeout=GIT_TIMEOUT)
            payload = json.loads(proc.stdout)
        self.assertEqual(self.REFUSAL_KEYS, set(payload))
        self.assertEqual("DIFF-FENCE-INCOMPLETE", payload["reason"])


class TheShippedState(unittest.TestCase):
    def test_the_real_repo_runs_and_reports(self) -> None:
        # The WORKSPACE root, not gk-core: `check()` reads `root / "tasks" / "sessions"` and the records
        # are the workspace root's - 262 of them, 4 active. The next test in this class already uses
        # `_WORKSPACE` for exactly this path, so the file was holding two roots at once and the shipped
        # state read as empty.
        got = guard.check(_WORKSPACE)
        self.assertIn(got["verdict"], {"OK", "FAIL"})
        self.assertGreater(got["records"], 0, "the real workspace has session records")

    def test_the_real_repo_has_a_tasks_sessions_directory(self) -> None:
        self.assertTrue((_WORKSPACE / "tasks" / "sessions").is_dir())

    def test_program_status_holds_the_same_vocabularies(self) -> None:
        # The drift guard in gk-core/tests/tools/test_program_status.py reads THIS module's owner for these
        # literals. Asserting the two agree here means a port that changed one side knows at once.
        sys.path.insert(0, str(REPO / "scripts"))
        try:
            import program_status  # type: ignore
        finally:
            sys.path.pop(0)
        self.assertEqual(guard.VALID_MODES, tuple(program_status.SESSION_MODES))
        self.assertEqual(guard.VALID_STATUS, tuple(program_status.SESSION_STATUSES))
        self.assertEqual(guard.REQUIRED_FIELDS, tuple(program_status.SESSION_REQUIRED_FIELDS))


if __name__ == "__main__":
    unittest.main()
