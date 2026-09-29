"""Contract tests for `gk-core/scripts/guard-generated-seed.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, and the behaviour of the rule. It does not assert a message body, a line count, or
the number of trees, sources or provenance keys — those are readings, and a guardrail that pins a
population fails when content ships and guards nothing.

**THE HOLE THIS PORT INTRODUCED AND THE DIFFERENTIAL CAUGHT** is the reason
`TestWorkingTreeModeActuallyInspects` exists. The first `check()` returned the CI-contract result
whenever no range and no base ref were given, with no reference to the flag. Working-tree mode is the
DEFAULT, so the guard inspected nothing locally and printed a clean verdict — a silent no-op,
introduced by the port rather than inherited from the PowerShell.

Differential evidence: 29 fixtures, 26 identical in exit code and every emitted line, plus 3 declared
divergences where the original THREW and the port names the refusal. That comparison lives outside this
file because the PowerShell form no longer exists.

Two details the differential forced, both easy to get wrong:
  * PowerShell MEMBER ENUMERATION — `$doc._meta` on a top-level array returns each element's `_meta`,
    so the original treats `[{_meta: {...}}]` as provenance-carrying. Transcribed, not narrowed:
    narrowing is the wrong direction for a guard whose job is to notice provenance.
  * `Sort-Object -Unique` dedups CASE-INSENSITIVELY, so a plain `set` would keep `A.json` and `a.json`
    as two entries, changing the count the verdict reports and double-reporting a file.
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
SCRIPT = REPO / "scripts" / "guard-generated-seed.py"

_spec = importlib.util.spec_from_file_location("guard_generated_seed", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_generated_seed"] = guard
_spec.loader.exec_module(guard)

PROVENANCE = {"_meta": {"model": "seedsmith", "promptVersion": 3, "batch": "b1"}}
ITEM = "data/seed/items/0001.json"
GENERATOR = "tools/seedsmith/seedsmith/adapters/items/a.py"


def write(root: Path, rel: str, body) -> None:
    path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body if isinstance(body, str) else json.dumps(body), encoding="utf-8")


def build(root: Path, *, files=None) -> Path:
    """Seed the repository FIRST, then write the fixture's files.

    The order is load-bearing: files written before the seed commit are invisible to a working-tree
    diff, so a fixture would assert a clean run for a tree that should be red — and both
    implementations would agree, so nothing would look wrong.
    """
    root.mkdir(parents=True, exist_ok=True)

    def git(*args: str) -> None:
        subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, timeout=300)

    (root / "README.md").write_text("# seed\n", encoding="utf-8")
    git("init", "-q")
    git("config", "user.email", "guard@example.invalid")
    git("config", "user.name", "guard-test")
    git("add", "-A")
    git("commit", "-q", "-m", "seed")
    for rel, body in (files or {}).items():
        write(root, rel, body)
    return root


def check(root: Path, **kwargs) -> dict:
    return guard.check(root, base_ref="", commit_range=None, **kwargs)


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=1800)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("guard-generated-seed.py", proc.stdout)

    def test_working_tree_mode_is_the_default_and_exits_zero_on_this_repo(self) -> None:
        result = run()
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])

    def test_the_base_ref_flag_is_accepted(self) -> None:
        result = run("--base-ref", "HEAD")
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])


class TestWorkingTreeModeActuallyInspects(unittest.TestCase):
    """The hole the port introduced. A guard that cannot tell "not asked" from "asked and forbidden"
    is worse than one with no CI contract at all, because it looks like it is working."""

    def test_a_hand_edited_corpus_file_is_caught_with_no_flags(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-gate-") as tmp:
            root = build(Path(tmp), files={ITEM: PROVENANCE})
            self.assertEqual([ITEM], check(root)["violations"])

    def test_the_ci_contract_is_GATED_on_the_flag(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-gate-") as tmp:
            root = build(Path(tmp), files={ITEM: PROVENANCE})
            self.assertFalse(check(root).get("requires_range"))
            gated = check(root, require_explicit_range=True)
            self.assertTrue(gated["requires_range"])
            self.assertEqual("CI-RANGE-REQUIRED", gated["refused"])


class TheCiContract(unittest.TestCase):
    """`-RequireExplicitRange`: a push range is not a working tree. The original THREW; the port
    names the refusal, because "CI passed no range" and "someone hand-edited the corpus" otherwise
    share an exit code and neither is diagnosable from a log line."""

    def test_it_refuses_by_name_and_exits_one(self) -> None:
        result = run("--require-explicit-range")
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("CI-RANGE-REQUIRED", result["stderr"])

    def test_it_reaches_the_envelope(self) -> None:
        result = run("--require-explicit-range", "--json")
        payload = json.loads(result["stdout"])
        self.assertEqual("FAILED", payload["verdict"])
        self.assertEqual("CI-RANGE-REQUIRED", payload["refused"])

    def test_a_range_satisfies_it(self) -> None:
        result = run("--require-explicit-range", "--range", "HEAD..HEAD")
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])
        self.assertNotIn("CI-RANGE-REQUIRED", result["stderr"])


class Refusals(unittest.TestCase):
    def test_a_range_and_a_base_ref_together_is_named(self) -> None:
        result = run("--range", "a..b", "--base-ref", "c")
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("RANGE-AND-BASEREF", result["stderr"])

    def test_an_unresolvable_base_ref_is_named_and_quotes_git(self) -> None:
        # This guard's `Invoke-GitLines` already threw on a non-zero git exit, unlike its sibling
        # `guard-repo-boundary`, whose copy swallowed it and read empty stdout as "no changes". So this
        # is a naming improvement over an already-fail-closed guard, NOT a hole closed here, and the
        # claim is checked rather than asserted.
        with tempfile.TemporaryDirectory(prefix="gs-ref-") as tmp:
            root = build(Path(tmp))
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(root, base_ref="no-such-ref-xyz")
            self.assertEqual("GIT-FAILED", caught.exception.reason)
            self.assertIn("ambiguous argument", caught.exception.detail)

    def test_a_root_that_is_not_a_repository_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-ref-") as tmp:
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(Path(tmp))
            self.assertEqual("GIT-FAILED", caught.exception.reason)

    def test_git_has_a_hard_timeout(self) -> None:
        # The original could wait forever on a wedged git. A hang is not a verdict.
        self.assertGreater(guard.GIT_TIMEOUT, 0)


class TheRule(unittest.TestCase):
    """Provenance-carrying generated output needs a generator change in the same change set."""

    def _violations(self, **kwargs) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="gs-rule-") as tmp:
            return check(build(Path(tmp), **kwargs))["violations"]

    def test_a_hand_edited_corpus_file_is_a_violation(self) -> None:
        self.assertEqual([ITEM], self._violations(files={ITEM: PROVENANCE}))

    def test_touching_the_generator_clears_it(self) -> None:
        self.assertEqual([], self._violations(files={ITEM: PROVENANCE, GENERATOR: "# gen\n"}))

    def test_touching_the_registry_clears_it(self) -> None:
        # The registry is a Source, not Generated: it is authored, so editing it is the sanctioned way
        # to change what the generator emits.
        self.assertEqual([], self._violations(
            files={ITEM: PROVENANCE, "data/seed/items/_registry/r.json": {}}))

    def test_a_file_without_provenance_is_not_corpus_content(self) -> None:
        # The second half of the two-part condition. Authored JSON under a generated root is ignored.
        self.assertEqual([], self._violations(files={ITEM: {"name": "hand authored"}}))

    def test_an_unrelated_change_is_allowed(self) -> None:
        self.assertEqual([], self._violations(files={"docs/x.md": "# d\n"}))

    def test_the_failure_path_is_exercised_and_the_clean_path_too(self) -> None:
        # A guard proven only on success is half-proven, and the cheapest way to be half-proven is never
        # to assert that the bad tree is bad.
        self.assertTrue(self._violations(files={ITEM: PROVENANCE}))
        self.assertFalse(self._violations())


class TheProvenancePredicate(unittest.TestCase):
    def _has(self, body) -> bool:
        with tempfile.TemporaryDirectory(prefix="gs-prov-") as tmp:
            root = Path(tmp)
            write(root, "x.json", body)
            return guard.has_generator_provenance(root, "x.json")

    def test_each_key_alone_is_enough(self) -> None:
        for key in guard.PROVENANCE_KEYS:
            with self.subTest(key=key):
                self.assertTrue(self._has({"_meta": {key: "v"}}))

    def test_a_FALSY_value_is_not_provenance(self) -> None:
        # Transcribed from PowerShell truthiness, and it is the difference between "this file declares
        # which model wrote it" and "this file has an empty field called model".
        for body in ({"_meta": {"model": ""}}, {"_meta": {"promptVersion": 0}},
                     {"_meta": {"batch": False}}, {"_meta": {"model": []}}, {"_meta": {}}):
            with self.subTest(body=body):
                self.assertFalse(self._has(body))

    def test_no_meta_is_not_provenance(self) -> None:
        self.assertFalse(self._has({"name": "x"}))

    def test_a_non_dict_meta_is_not_provenance(self) -> None:
        self.assertFalse(self._has({"_meta": "model=x"}))

    def test_unparseable_json_is_not_provenance(self) -> None:
        self.assertFalse(self._has("{ not json"))

    def test_a_missing_file_is_not_provenance(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-prov-") as tmp:
            self.assertFalse(guard.has_generator_provenance(Path(tmp), "absent.json"))

    def test_a_top_level_ARRAY_is_enumerated_for_provenance(self) -> None:
        # PowerShell member enumeration: `$doc._meta` on an array returns each element's `_meta`, so
        # the original blocks `[{_meta:{model:m}}]`. Transcribed rather than narrowed, because this
        # guard's job is to notice provenance-carrying output and "we saw it and did not look" is the
        # failure mode.
        self.assertTrue(self._has([{"_meta": {"model": "m"}}]))
        self.assertFalse(self._has([{"name": "x"}]))

    def test_the_key_set_is_closed(self) -> None:
        # A literal is right here: the provenance keys are a vocabulary the code owns and a human
        # changes, not a population.
        self.assertEqual(("model", "promptVersion", "batch"), guard.PROVENANCE_KEYS)


class TheIgnoredPatterns(unittest.TestCase):
    """Generator bookkeeping and provenance side-cars are never corpus content."""

    def _violations(self, rel: str) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="gs-ig-") as tmp:
            return check(build(Path(tmp), files={rel: PROVENANCE}))["violations"]

    def test_each_ignored_shape_is_ignored(self) -> None:
        for rel in ("data/seed/items/_meta.json",
                    "data/seed/items/x/_index.json",
                    "data/seed/items/0001.ledger.json",
                    "data/seed/items/_runs/0001.json",
                    "data/seed/actions/_runs/0001.json"):
            with self.subTest(rel=rel):
                self.assertEqual([], self._violations(rel))

    def test_the_pattern_set_is_the_documented_one(self) -> None:
        self.assertEqual(5, len(guard.IGNORED_NAME_PATTERNS))

    def test_an_authored_registry_under_a_generated_root_is_ignored(self) -> None:
        self.assertEqual([], self._violations("data/seed/items/_registry/0001.json"))


class CaseFoldingAndDedup(unittest.TestCase):
    def test_a_case_variant_generated_path_still_matches(self) -> None:
        # Every path match came from PowerShell `-match`, which folds case, and the patterns are
        # lowercase paths - so an upper-case path is matched exactly as the original matched it.
        with tempfile.TemporaryDirectory(prefix="gs-case-") as tmp:
            root = build(Path(tmp), files={"DATA/SEED/ITEMS/0001.json": PROVENANCE})
            self.assertEqual(1, len(check(root)["violations"]))

    def test_a_case_variant_source_still_clears_it(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-case-") as tmp:
            root = build(Path(tmp), files={ITEM: PROVENANCE,
                                           "TOOLS/SEEDSMITH/seedsmith/adapters/items/x.py": "# g\n"})
            self.assertEqual([], check(root)["violations"])

    def test_dedup_is_case_insensitive(self) -> None:
        # `Sort-Object -Unique` folds case; a plain set would keep both and double-report a file.
        self.assertEqual(["A.json"], guard._dedup_case_insensitive(["A.json", "a.json"]))
        self.assertEqual(1, len(guard._dedup_case_insensitive(["A.json", "a.json"])))


class TheChangedFileSurface(unittest.TestCase):
    def test_working_tree_mode_covers_unstaged_staged_and_untracked(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-surf-") as tmp:
            root = build(Path(tmp))
            write(root, "a.txt", "a\n")                       # untracked
            write(root, "b.txt", "b\n")
            subprocess.run(["git", "-C", str(root), "add", "b.txt"], capture_output=True, timeout=300)
            found = set(guard.changed_files(root, "", None))
            self.assertEqual({"a.txt", "b.txt"}, found)
            # README.md is COMMITTED by the seed step, so it must NOT appear: the working-tree surface
            # is the three diffs, and a committed file is in none of them. An earlier version of this
            # test asserted the opposite while its own comment said so, which is a test that cannot
            # pass and does not know it.

    def test_a_base_ref_replaces_the_working_tree_surface(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-surf-") as tmp:
            root = build(Path(tmp))
            write(root, "a.txt", "a\n")                       # untracked
            self.assertEqual([], guard.changed_files(root, "HEAD", None))
            self.assertIn("a.txt", guard.changed_files(root, "", None))

    def test_a_range_skips_the_untracked_pass(self) -> None:
        # Transcribed: the untracked sweep is a WORKING-TREE concern, and a commit range is a
        # statement about history. Mixing them would report a file no commit contains.
        with tempfile.TemporaryDirectory(prefix="gs-surf-") as tmp:
            root = build(Path(tmp))
            write(root, "a.txt", "a\n")
            self.assertEqual([], guard.changed_files(root, "", "HEAD..HEAD"))


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "changed", "violations", "scope", "requires_range", "refused"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-json-") as tmp:
            good = build(Path(tmp) / "ok")
            bad = build(Path(tmp) / "bad", files={ITEM: PROVENANCE})
            for root, verdict in ((good, "OK"), (bad, "FAIL")):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run(
                        [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                        capture_output=True, text=True, timeout=900)
                    self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
                    self.assertEqual(verdict, json.loads(proc.stdout)["verdict"])

    def test_the_scope_label_is_never_empty(self) -> None:
        # Found by the differential: `commit_range or base_ref` is empty in working-tree mode, so the
        # clean line read "no changes vs " with nothing after it.
        with tempfile.TemporaryDirectory(prefix="gs-json-") as tmp:
            root = build(Path(tmp))
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)],
                                  capture_output=True, text=True, timeout=900)
            self.assertNotIn("vs \n", proc.stdout)
            self.assertTrue(proc.stdout.strip() or "no changes" in proc.stdout)

    def test_findings_go_to_stderr_and_the_verdict_to_stdout(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gs-json-") as tmp:
            root = build(Path(tmp), files={ITEM: PROVENANCE})
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)],
                                  capture_output=True, text=True, timeout=900)
            self.assertEqual(guard.EXIT_FAILED, proc.returncode)
            self.assertEqual("", proc.stdout.strip(), "a failing run must put nothing on stdout")
            self.assertIn(ITEM, proc.stderr)
            self.assertIn("BLOCKED", proc.stderr)


class TheShippedState(unittest.TestCase):
    def test_this_repo_is_clean_against_head(self) -> None:
        got = guard.check(REPO, base_ref="HEAD")
        self.assertEqual("OK", got["verdict"], got["violations"][:5])

    def test_the_real_tree_has_generated_trees(self) -> None:
        # Not a count: an empty TREES table would make the whole guard vacuous and every rule above
        # would pass for the wrong reason.
        self.assertTrue(guard.TREES)
        for tree in guard.TREES:
            self.assertTrue(str(tree["generated"]).startswith("^"))
            self.assertTrue(tree["sources"])


if __name__ == "__main__":
    unittest.main()
