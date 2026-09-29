"""Contract tests for `gk-core/scripts/ps1-rename-sweep.py`.

The tool had NO test file until the day it rewrote 1,180 files inside other sessions' worktrees, so
the first thing this covers is the fence. The rest pins the preconditions a citation rewrite depends
on: it refuses when the port does not exist, refuses when the retired `.ps1` is still there, and
reports SKIPPED files separately from CHANGED ones so a caller cannot read a partial pass as full
coverage.
"""

from __future__ import annotations

import importlib.util
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "ps1-rename-sweep.py"

_spec = importlib.util.spec_from_file_location("ps1_rename_sweep", SCRIPT)
sweep = importlib.util.module_from_spec(_spec)
sys.modules["ps1_rename_sweep"] = sweep
_spec.loader.exec_module(sweep)


def run(*args: str) -> tuple[int, str, str]:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=900)
    return proc.returncode, proc.stdout, proc.stderr


def payload(stdout: str) -> dict:
    return json.loads(stdout)


class RefusesWithoutARealPort(unittest.TestCase):
    """A citation that points at nothing is worse than a stale one, because it reads as evidence."""

    def test_it_refuses_when_the_port_does_not_exist(self) -> None:
        code, out, err = run("--map", "definitely-not-a-tool", "--json")
        self.assertEqual(code, 1)
        self.assertEqual(payload(out)["reason"], "PORT-MISSING")
        self.assertIn("REFUSED", err)

    def test_it_refuses_a_missing_root(self) -> None:
        code, out, _ = run("--map", "guard-dal", "--root", "no-such-dir", "--json")
        self.assertEqual(code, 1)
        self.assertEqual(payload(out)["reason"], "ROOT-MISSING")

    def test_it_refuses_while_the_retired_script_still_exists(self) -> None:
        # Delete, THEN sweep. Rewriting first mints a pointer to a `.py` that is not there yet, and
        # two files that both look like the tool is the exact ambiguity the port standard forbids.
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            (root / "scripts" / "guard-dal.ps1").write_text("exit 0\n", encoding="utf-8")
            (root / "scripts" / "guard-dal.py").write_text("raise SystemExit(0)\n", encoding="utf-8")
            with self.assertRaises(sweep.Refusal) as caught:
                sweep.check_preconditions(root, "guard-dal")
            self.assertEqual(caught.exception.reason, "SOURCE-STILL-PRESENT")


class TheInvocationForms(unittest.TestCase):
    """Three shapes, and the third was found by a sweep rather than by reading.

    `pwsh -NoProfile -Command "& './scripts/x.ps1'"` matched NOTHING: the `-File` rules want `-File`,
    and the bare-path rules' lookbehind rejects the `./` prefix. Rewriting only the path would have
    left `pwsh -Command "& './scripts/x.py'"` - PowerShell handed a Python file, which is worse than
    the stale citation because it reads as updated.
    """

    def _rewrite(self, text: str) -> str:
        out = text
        for _name, pattern, replacement in sweep._rules("guard-x"):
            out = pattern.sub(replacement, out)
        return out

    def test_the_powershell_command_string_form_is_rewritten_whole(self) -> None:
        got = self._rewrite("pwsh -NoProfile -Command \"& './scripts/guard-x.ps1'\"")
        self.assertEqual("python scripts/guard-x.py", got)

    def test_the_file_form_is_rewritten_whole(self) -> None:
        self.assertEqual("python scripts/guard-x.py",
                         self._rewrite("pwsh -NoProfile -File ./scripts/guard-x.ps1"))
        self.assertEqual("python scripts/guard-x.py",
                         self._rewrite("pwsh -NoProfile -File scripts/guard-x.ps1"))

    def test_the_windows_invocation_form_is_rewritten_whole(self) -> None:
        self.assertEqual("python scripts/guard-x.py", self._rewrite(r".\scripts\guard-x.ps1"))

    def test_a_bare_prose_mention_becomes_a_bare_mention(self) -> None:
        # A prose mention is NOT an invocation, so it must not grow a `python ` prefix it never had.
        self.assertEqual("guard-x.py is green", self._rewrite("guard-x.ps1 is green"))

    def test_no_shape_leaves_a_powershell_wrapper_pointing_at_python(self) -> None:
        # The property that matters, stated over the shapes rather than per shape: after a sweep,
        # nothing may still be a PowerShell invocation naming a `.py`. That is the failure mode a
        # path-only rewrite produces, and it is silent - the citation looks maintained.
        for text in ("pwsh -NoProfile -Command \"& './scripts/guard-x.ps1'\"",
                     "pwsh -NoProfile -File ./scripts/guard-x.ps1",
                     "pwsh -NoProfile -File scripts/guard-x.ps1",
                     r".\scripts\guard-x.ps1"):
            with self.subTest(text=text):
                got = self._rewrite(text)
                self.assertNotIn("pwsh", got.lower())
                self.assertNotIn("powershell", got.lower())
                self.assertIn("scripts/guard-x.py", got)


class ARepoWithItsOwnLinkedWorktree:
    """A throwaway repository that OWNS a linked worktree inside the directory the sweep is pointed at.

    The previous version of this test ran the sweep against the REAL repository's `.claude` and asserted
    `worktrees_excluded` was non-empty - i.e. it asserted a POPULATION ("this machine currently has a
    worktree under .claude"), which is exactly the class of assertion `validation-ssot.md` forbids: it
    fails when a lane's worktree is removed and passes for the wrong reason when one exists. The sibling
    test at line 127 has the same shape and is corrected below.

    `linked_worktrees` reads `git worktree list` from the given root, so a fixture repository answers
    the question on its own terms. That is what makes the count checkable without depending on the
    machine: the fixture KNOWS how many worktrees it has, and asserting equality with that number is a
    contract, not a population.
    """

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="sweep-wt-"))
        self.expected = 0
        self._git("init", "-q", "-b", "main")
        self._git("config", "user.email", "sweep-test@example.invalid")
        self._git("config", "user.name", "sweep test")
        (self.root / "seed.txt").write_text("seed\n", encoding="utf-8")
        self._git("add", "-A")
        self._git("commit", "-q", "-m", "seed")

    def _git(self, *args: str) -> str:
        proc = subprocess.run(["git", "-C", str(self.root), *args], capture_output=True, text=True,
                              timeout=300)
        if proc.returncode != 0:
            raise AssertionError(f"git {' '.join(args)}: {proc.stdout}{proc.stderr}")
        return proc.stdout.strip()

    def add_worktree_under(self, relative: str) -> Path:
        """Create a LINKED worktree at `<repo>/<relative>`, which is where a pool would put one."""
        target = self.root / relative
        self._git("worktree", "add", "-q", "--detach", str(target), "HEAD")
        # The docs it would hold, so the sweep has something it could otherwise have rewritten.
        (target / "docs").mkdir(parents=True, exist_ok=True)
        (target / "docs" / "note.md").write_text("guard-dal mention\n", encoding="utf-8")
        self.expected += 1
        return target

    def run_sweep(self, *args: str) -> tuple[int, str, str]:
        proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=str(self.root),
                              capture_output=True, text=True, timeout=900)
        return proc.returncode, proc.stdout, proc.stderr

    def __enter__(self) -> "ARepoWithItsOwnLinkedWorktree":
        return self

    def __exit__(self, *_exc) -> None:
        import shutil
        subprocess.run(["git", "-C", str(self.root), "worktree", "prune"], capture_output=True,
                       text=True, timeout=300)
        shutil.rmtree(self.root, ignore_errors=True)


class TheArgumentDialectIsTheSweepsOwn(unittest.TestCase):
    """The sweep rewrote PATHS and knew nothing about ARGUMENTS, so a line naming `-Report` still
    named it after the rewrite, and the file went out holding `python <tool>.py -Report` - a command
    the tool rejects. It shipped TWICE: 74 files on the session-boundary-check sweep, 11 on the
    guard-verification-boundaries one. A broken command is worse than a stale one, because a stale one
    is visibly stale. So the tool now owns the argument dialect and a line it must not guess at is left
    alone and REPORTED rather than written half-true.
    """

    STEM = "guard-verification-boundaries"

    def test_a_powershell_flag_after_the_python_path_is_rewritten(self) -> None:
        for before, after in [
            ("python scripts/guard-verification-boundaries.py -Report",
             "python scripts/guard-verification-boundaries.py --report"),
            ("python scripts/guard-verification-boundaries.py -Root . -Report",
             "python scripts/guard-verification-boundaries.py --root . --report"),
            ("python scripts/guard-verification-boundaries.py --root <wt> -Report",
             "python scripts/guard-verification-boundaries.py --root <wt> --report"),
        ]:
            with self.subTest(line=before):
                self.assertEqual(after, sweep.rewrite_arguments(before, self.STEM))

    def test_an_already_correct_line_is_untouched(self) -> None:
        for line in ("python scripts/guard-verification-boundaries.py --report",
                     "`guard-verification-boundaries.py` is the guard",
                     "a sentence mentioning nothing"):
            with self.subTest(line=line):
                self.assertEqual(line, sweep.rewrite_arguments(line, self.STEM))

    def test_a_HOST_flag_after_the_tool_REFUSES_rather_than_guessing(self) -> None:
        # Past a PowerShell host flag the remaining tokens belong to another command, so the whole line
        # is left untouched - rewriting the first tool's flags while mangling the second tool's is worse
        # than not rewriting at all. The FIRST version of this gate raised out of the entire RUN, which
        # turned a safety valve into a blocker: 200 files went unprocessed because of one line. It
        # skips and reports now.
        #
        # The line must actually REACH the host flag, which means the `.py` marker has to PRECEDE it -
        # a line still naming the `.ps1` returns early, because the path rules rewrite the extension
        # before this pass runs and spotting that is not this gate's job.
        line = ("python scripts/guard-verification-boundaries.py -Report > out.txt 2>&1 ; "
                "pwsh -NoProfile -File build.ps1")
        with self.assertRaises(sweep.HalfRename) as caught:
            sweep.rewrite_arguments(line, self.STEM)
        self.assertIn("-NoProfile", str(caught.exception))

    def test_each_stem_owns_its_OWN_flags(self) -> None:
        # The gate is per-stem: `-Report` is a flag of THIS guard and means nothing to another. Calling
        # it with the wrong stem must leave the line alone rather than translate a foreign flag.
        line = "scripts/session-boundary-check.py -Session abc"
        self.assertEqual(line, sweep.rewrite_arguments(line, self.STEM))
        self.assertEqual("scripts/session-boundary-check.py --session abc",
                         sweep.rewrite_arguments(line, "session-boundary-check"))

    def test_a_line_still_naming_the_PS1_returns_early_and_LEAVES_IT_TO_THE_PATH_RULES(self) -> None:
        # Stated because it is a real limit of this gate, not an accident: the argument pass runs AFTER
        # the path rules, so a line whose path is still `.ps1` has no `.py` marker to work from. A shape
        # the path rules can produce and this gate cannot fix is `pwsh -File <tool>.py` - a PowerShell
        # host told to run a Python file. That is a HOST problem, and it belongs to the caller (as the
        # three `verify-change.ps1` sites are). Pretending this gate closes it would be a claim it does
        # not make.
        line = "pwsh -NoProfile -File scripts/guard-verification-boundaries.ps1 -Report"
        self.assertEqual(line, sweep.rewrite_arguments(line, self.STEM))

    def test_an_UNKNOWN_stem_is_a_no_op_rather_than_a_guess(self) -> None:
        line = "scripts/some-future-tool.py -Whatever"
        self.assertEqual(line, sweep.rewrite_arguments(line, "some-future-tool"))


class LinkedWorktreesAreNotOurs(unittest.TestCase):
    """The fence. A worktree is another session's WORKING TREE even when git cannot see the edit.

    Measured incident: `--root .claude` rewrote 1,180 files across ~20 other sessions' worktrees.
    They are git-IGNORED, so `git status` showed nothing and no commit could have carried the
    damage; it was caught only by reading the tool's own `--json`, and undone by hand.
    """

    def test_pointing_a_root_into_a_worktree_is_refused(self) -> None:
        # A real worktree, not `.claude/worktrees` - that directory CONTAINS worktrees, and pointing
        # at it is a legitimate request that the tool serves by pruning, not by refusing. The two
        # cases are different and conflating them would either block real work or miss the fence.
        worktrees = sweep.linked_worktrees(REPO)
        self.assertTrue(worktrees, "this repo has linked worktrees; the fixture needs one")
        target = worktrees[0] / "tasks"
        if not target.is_dir():
            target = worktrees[0]
        code, out, _ = run("--map", "guard-battle-responsibility", "--json", "--root", str(target))
        self.assertEqual(code, 1)
        got = payload(out)
        self.assertEqual(got["reason"], "ROOT-IS-A-LINKED-WORKTREE")
        self.assertIn("inside", got["detail"])

    def test_a_broad_root_excludes_worktrees_and_reports_the_count(self) -> None:
        # Not a refusal: a root that CONTAINS a worktree is a legitimate request for the main tree's own
        # content, and the tool must serve it while leaving every worktree under it untouched. The count
        # is what makes the coverage claim checkable, so it has to be in the envelope.
        #
        # The fixture KNOWS how many worktrees it created, so asserting EQUALITY with that number is a
        # contract. Asserting non-emptiness against the real repository was a population assertion: it
        # went red on 2026-09-28 purely because a lane's worktree had been cleaned up, and it would have
        # gone green for the wrong reason on any machine that happened to have one.
        with ARepoWithItsOwnLinkedWorktree() as fixture:
            fixture.add_worktree_under(".claude/worktrees/lane-a")
            code, out, _ = fixture.run_sweep("--map", "guard-battle-responsibility", "--json",
                                             "--root", ".claude")
        self.assertEqual(code, 0)
        excluded = payload(out)["worktrees_excluded"]
        self.assertEqual(fixture.expected, len(excluded),
                         f"the envelope must name every worktree it left alone; got {excluded}")
        for row in excluded:
            # NAMED, not measured. The first version counted the files inside each worktree in order
            # to report "candidates left alone", which meant walking the trees the guard exists to
            # avoid - 63 seconds, and a number read out of a tree the run had refused to read.
            self.assertEqual({"worktree", "action"}, set(row))
            self.assertEqual("pruned-not-entered", row["action"])

    def test_the_worktree_list_comes_from_the_repository_it_WAS_POINTED_AT(self) -> None:
        # THE DEFECT THIS FIXTURE FOUND. The sweep called `linked_worktrees(REPO_ROOT)` - the
        # repository the SCRIPT lives in - so its worktree list was right only when the tool and its
        # target were the same repository. Pointed anywhere else it pruned the wrong repository's
        # worktrees and left the target's own alone. Same class as `session-boundary-check` resolving
        # worktree paths against the process directory instead of the repository it was told to check:
        # an answer that depends on where the TOOL is rather than on what it was pointed at.
        with ARepoWithItsOwnLinkedWorktree() as fixture:
            under = fixture.add_worktree_under("docs/notes/lane-a")
            self.assertEqual([under.resolve()],
                             [w.resolve() for w in sweep.linked_worktrees(fixture.root)])
            # And the tool, pointed at that repository, reports that worktree as excluded.
            code, out, _ = fixture.run_sweep("--map", "guard-battle-responsibility", "--json",
                                             "--root", ".")
            self.assertEqual(code, 0)
            excluded = payload(out)["worktrees_excluded"]
            # Compared as RESOLVED PATHS, not as strings. `git worktree list --porcelain` reports
            # forward slashes and the envelope carries what git said verbatim, while Python's canonical
            # form on Windows uses backslashes. Both name the same tree. The first version of this
            # assertion compared strings and failed on the separator alone - a test of the OPERATING
            # SYSTEM, not of the tool. The refusal itself is unaffected: `worktree_of` compares resolved
            # path COMPONENTS, so the separator never decides whether a worktree is recognised.
            self.assertEqual([under.resolve()],
                             [Path(row["worktree"]).resolve() for row in excluded])

    def test_a_target_OUTSIDE_any_repository_is_served_with_an_empty_list(self) -> None:
        # Not-in-a-repository is a KNOWN answer, not an unknown one: no linked worktree can exist where
        # there is no repository. The first version of `repository_of` returned the path itself, so the
        # sweep asked git outside a checkout, git failed, and a legitimate "sweep this docs directory"
        # request was REFUSED - over-closing in the direction that blocks ordinary work.
        with tempfile.TemporaryDirectory(prefix="sweep-norepo-") as tmp:
            root = Path(tmp)
            (root / "docs").mkdir()
            (root / "docs" / "a.md").write_text("`guard-dal.ps1`\n", encoding="utf-8")
            self.assertIsNone(sweep.repository_of(root / "docs"),
                              "this temp directory is not inside a repository")
            code, out, _ = run("--map", "guard-dal", "--json", "--root", str(root / "docs"))
        self.assertEqual(code, 0)
        self.assertEqual("DRY-RUN", payload(out)["verdict"])
        self.assertEqual([], payload(out)["worktrees_excluded"])

    def test_a_root_with_NO_worktree_reports_an_EMPTY_list_not_a_refusal(self) -> None:
        # The pair that makes the count above meaningful: zero worktrees is a legitimate, reported
        # answer. A tool that refused here would be refusing the ordinary case, and one that reported a
        # stale non-zero count would be lying about what it read.
        with ARepoWithItsOwnLinkedWorktree() as fixture:
            code, out, _ = fixture.run_sweep("--map", "guard-battle-responsibility", "--json",
                                             "--root", ".")
        self.assertEqual(code, 0)
        self.assertEqual([], payload(out)["worktrees_excluded"])

    def test_pruning_is_what_makes_the_run_fast(self) -> None:
        # A regression guard on the cost, because the enumerate-then-filter version was correct and
        # took minutes: 20+ complete checkouts including their bin/ and obj/ before discarding them.
        import time
        started = time.monotonic()
        run("--map", "guard-battle-responsibility", "--json", "--root", ".claude")
        self.assertLess(time.monotonic() - started, 60.0)

    def test_a_worktree_that_is_a_prefix_but_not_a_parent_is_not_excluded(self) -> None:
        # `wt2` is a string prefix of `wt20` while being no kind of parent of it. A prefix test would
        # silently skip a file outside every worktree, which is the quiet-wrong-answer class this
        # repo keeps paying for.
        base = Path("C:/x")
        inside = Path("C:/x/wt2/docs/a.md")
        sibling = Path("C:/x/wt20/docs/a.md")
        roots = [base / "wt2"]
        self.assertIsNotNone(sweep.worktree_of(inside, roots))
        self.assertIsNone(sweep.worktree_of(sibling, roots))

    def test_the_main_checkout_is_not_treated_as_a_worktree(self) -> None:
        others = sweep.linked_worktrees(REPO)
        self.assertNotIn(REPO.resolve(), others)
        self.assertIsNone(sweep.worktree_of(REPO / "docs" / "README.md", others))

    def test_an_unknown_worktree_list_refuses_rather_than_assuming_empty(self) -> None:
        # The 1,180-file incident was an UNKNOWN list being read as an EMPTY one. This is the fix
        # for that assumption, so it needs a fixture: a directory that is not a repository.
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            with self.assertRaises(sweep.Refusal) as caught:
                sweep.linked_worktrees(Path(tmp))
            self.assertEqual(caught.exception.reason, "WORKTREE-LIST-UNAVAILABLE")


class PlanShape(unittest.TestCase):
    """Three kinds of file, kept apart. One total would make a skip read as a change."""

    def test_changed_skipped_and_unreadable_are_distinguishable(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "docs").mkdir()
            (root / "docs" / "a.md").write_text("see `scripts\\guard-dal.ps1`\n", encoding="utf-8")
            (root / "docs" / "guard-dal.py").write_text("Replaces `guard-dal.ps1`\n", encoding="utf-8")
            (root / "docs" / "b.md").write_text("and `guard-dal.ps1` here\n", encoding="utf-8")
            # `.py` must be in the suffix set for the port file to be a CANDIDATE at all - the
            # default is (.md, .html), so the port's own docstring is not swept by default and the
            # provenance skip cannot fire. That is why the first version of this fixture saw 0 skips.
            got = sweep.plan(root, ["guard-dal"], [root / "docs"], (".md", ".py"))
            changed = [f for f in got["files"] if "skipped" not in f and "error" not in f]
            skipped = [f for f in got["files"] if "skipped" in f]
            self.assertEqual(2, len(changed))
            self.assertEqual(1, len(skipped))
            self.assertEqual("port-provenance", skipped[0]["skipped"])

    def test_a_dry_run_writes_nothing(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "docs").mkdir()
            target = root / "docs" / "a.md"
            target.write_text("`guard-dal.ps1`\n", encoding="utf-8")
            sweep.plan(root, ["guard-dal"], [root / "docs"])
            self.assertEqual("`guard-dal.ps1`\n", target.read_text(encoding="utf-8"))

    def test_it_is_idempotent(self) -> None:
        # Safe in a loop, and a re-run is how an operator confirms the first one finished.
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "docs").mkdir()
            (root / "docs" / "a.md").write_text("`guard-dal.ps1`\n", encoding="utf-8")
            first = sweep.plan(root, ["guard-dal"], [root / "docs"])
            sweep.apply_plan(first)
            second = sweep.plan(root, ["guard-dal"], [root / "docs"])
            self.assertTrue(any("changed_lines" in f for f in first["files"]))
            self.assertFalse(any("changed_lines" in f for f in second["files"]))


class TheShippedToolIsSelfConsistent(unittest.TestCase):
    """The tool's own file is a citation surface, and it is a dry run by default."""

    def test_a_dry_run_is_the_default(self) -> None:
        # Behaviour, not a grep for the flag: a dry run that reports DRY-RUN and leaves the file
        # exactly as it found it. Checking the source text for a string asserts nothing.
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "docs").mkdir()
            target = root / "docs" / "a.md"
            target.write_text("`guard-dal.ps1`\n", encoding="utf-8")
            code, out, _ = run("--map", "guard-dal", "--json", "--root", str(root / "docs"))
            self.assertEqual(code, 0)
            self.assertEqual("DRY-RUN", payload(out)["verdict"])
            self.assertEqual("`guard-dal.ps1`\n", target.read_text(encoding="utf-8"))

    def test_the_envelope_always_carries_its_keys(self) -> None:
        code, out, _ = run("--map", "guard-battle-responsibility", "--json", "--root", "docs")
        self.assertEqual(code, 0)
        self.assertTrue({"verdict", "stems", "roots", "files_changed", "lines_changed",
                         "substitutions", "files_skipped", "files_unreadable",
                         "worktrees_excluded"} <= set(payload(out)))


class AStemWhoseSuccessorIsRenamed(unittest.TestCase):
    """Two scripts predate the kebab-case rule and became snake_case MODULES.

    `accept-lane.ps1` -> `accept_lane.py`, `post-merge-check.ps1` -> `post_merge_check.py`. `--map
    accept-lane` derives `accept-lane.py`, which does not exist, and the tool REFUSES - correctly,
    because there is nothing for the citations to point at. The `OLD=NEW` form names the real target,
    and every PATTERN still matches the retired stem because that is what the documents contain.
    """

    def test_the_pattern_matches_the_retired_stem_and_the_replacement_the_target(self) -> None:
        rules = dict((name, (pattern, repl))
                     for name, pattern, repl in sweep._rules("accept-lane", "accept_lane"))
        for name, (pattern, _repl) in rules.items():
            with self.subTest(rule=name):
                # The pattern is built with `re.escape`, so the stem appears escaped; the claim is that it
                # matches the RETIRED name. A pattern built from the target would find nothing at all in
                # the documents, because `accept_lane.ps1` has never existed.
                self.assertIn(re.escape("accept-lane"), pattern.pattern)
                self.assertNotIn(re.escape("accept_lane"), pattern.pattern)
        pattern, repl = rules["bare-mention"]
        self.assertEqual("a bare accept_lane.py mention",
                         pattern.sub(repl, "a bare accept-lane.ps1 mention"))

    def test_a_bare_stem_still_maps_to_itself(self) -> None:
        rules = dict((name, (pattern, repl)) for name, pattern, repl in sweep._rules("guard-dal"))
        pattern, repl = rules["bare-mention"]
        self.assertEqual("guard-dal.py", pattern.sub(repl, "guard-dal.ps1"))

    def test_an_EMPTY_target_is_refused_rather_than_deriving_a_path(self) -> None:
        # `--map accept-lane=` must not fall through to `<stem>.py` and fail later with PORT-MISSING,
        # which would name the wrong thing entirely.
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            proc = subprocess.run(
                [sys.executable, str(Path(sweep.__file__)), "--map", "accept-lane=", "--root", str(root), "--json"],
                capture_output=True, text=True, timeout=300)
            self.assertNotEqual(0, proc.returncode)
            self.assertIn("MAP-TARGET-EMPTY", proc.stdout + proc.stderr)

    def test_the_port_is_resolved_across_the_tool_directories(self) -> None:
        # Hardcoding `scripts/` made the precondition report "scripts/accept_lane.py does not exist" for
        # a file sitting in `.claude/cmdc-agents/scripts/` - a refusal naming a path the reader cannot
        # verify, which is the opposite of what a named refusal is for.
        self.assertIn("scripts", sweep.TOOL_DIRS)
        self.assertIn(".claude/cmdc-agents/scripts", sweep.TOOL_DIRS)
        harness = REPO / ".claude" / "cmdc-agents" / "scripts" / "accept_lane.py"
        self.assertTrue(harness.is_file(), "the fixture this test reasons about must exist")
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / ".claude" / "cmdc-agents" / "scripts").mkdir(parents=True)
            (root / ".claude" / "cmdc-agents" / "scripts" / "accept_lane.py").write_text("x\n")
            sweep.check_preconditions(root, "accept-lane", "accept_lane")  # must not raise

    def test_the_refusal_lists_every_directory_it_searched(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sweep-") as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            with self.assertRaises(sweep.Refusal) as caught:
                sweep.check_preconditions(root, "no-such-tool")
        for directory in sweep.TOOL_DIRS:
            self.assertIn(directory, caught.exception.detail)


class AQualifiedCitationCarriesItsDirectory(unittest.TestCase):
    """A path with a directory prefix matched NEITHER existing rule.

    `bare-mention`'s `(?<![\\w/])` lookbehind rejects any stem preceded by a separator, and
    `slash-invocation` needs the `scripts/` tail to also be preceded by a non-path character - so
    `.claude/cmdc-agents/scripts/accept-lane.ps1` fell through both, survived the sweep, and the audit
    kept reporting it as a stale citation. The directory is captured and re-emitted, because the
    successor lives in the SAME directory as the file it replaces.
    """

    def test_both_directory_shapes_are_rewritten(self) -> None:
        pattern, repl = dict((n, (p, r)) for n, p, r
                             in sweep._rules("accept-lane", "accept_lane"))["path-with-directory"]
        self.assertEqual("`.claude/cmdc-agents/scripts/accept_lane.py`",
                         pattern.sub(repl, "`.claude/cmdc-agents/scripts/accept-lane.ps1`"))
        self.assertEqual("`scripts/accept_lane.py`",
                         pattern.sub(repl, "`scripts/accept-lane.ps1`"))

    def test_a_BARE_mention_is_left_for_the_bare_rule(self) -> None:
        pattern, repl = dict((n, (p, r)) for n, p, r
                             in sweep._rules("accept-lane", "accept_lane"))["path-with-directory"]
        self.assertEqual("a bare accept-lane.ps1 mention",
                         pattern.sub(repl, "a bare accept-lane.ps1 mention"))

    def test_the_three_invocation_rules_are_still_ahead_of_it(self) -> None:
        names = [name for name, _, _ in sweep._rules("guard-dal")]
        self.assertLess(names.index("powershell-command-string"), names.index("path-with-directory"))
        self.assertLess(names.index("powershell-file-sep"), names.index("path-with-directory"))
        self.assertLess(names.index("windows-invocation"), names.index("path-with-directory"))
        # ...and it must come before the bare rule, or a path's tail would be rewritten on its own and
        # leave the directory naming a file that does not exist.
        self.assertLess(names.index("path-with-directory"), names.index("bare-mention"))


if __name__ == "__main__":
    unittest.main()
