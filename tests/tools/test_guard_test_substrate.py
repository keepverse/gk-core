"""Contract tests for `gk-core/scripts/guard-test-substrate.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, each of the five banned shapes and its NEGATIVE twin, the ratchet's two directions,
the byte-preserved verdict strings, and the scanner this guard shares with two others.

WHY EVERY RULE HAS ITS NEGATIVE TWIN
------------------------------------
A rule that fires on everything passes every positive case. Each banned shape here is therefore paired
with a file that must NOT fire, so a rule that has lost its discriminator is visible rather than
quietly agreeing with everything. The fixtures are assembled from the shape the RULE tests for, not
pasted from the tree - a fixture pasted by hand proves only that a regex matches the text someone
expected.

THE FIVE FACTS THIS GUARD EXISTS TO HOLD
----------------------------------------
  * A swallowed delete is an empty or comment-only catch, detected on COMMENT-STRIPPED text - so
    `catch { /* best effort */ }` counts. A catch with a real body does not, and a nested brace inside
    the body must not end it early.
  * The one-pass stripper is load-bearing. The first version stripped block comments before line
    comments with a regex, so a `/**` inside prose - the path literal `gk-data/packs/fusion/data/seed/items/charms/**` in a
    doc comment - opened a phantom block comment that ran to the next real `*/`, deleted the `Dispose`
    from the scanned text, and made a real swallowed delete INVISIBLE. Fifteen files contain such prose
    and two really were violating. `a_doc_comment_naming_a_glob_path_does_not_eat_the_next_real_comment`
    is the regression test, and it is here rather than only in the differential because the differential
    no longer exists once the `.ps1` is gone.
  * The trait, the seed root and the corpus paths ARE string literals, so three of the five rules read
    the STRINGS-KEPT view. Reading the erased view would make them unfireable - or, for the trait, fire
    on every correctly tagged file.
  * The ratchet only shrinks: a listed file with no remaining violation FAILS, and a NEW code inside an
    already-exempt file is still new, because an exemption is per code and not per file.
  * The gate's own test file is exempt by NAME, once. It has to contain the banned shapes or it could
    not prove the gate fires, and the exemption is one entry rather than a rule about tests directories.

Differential evidence: 28 fixtures - 21 identical in exit code and every emitted line, 7 declared
divergences all of which are cases where the ORIGINAL is the defect, 0 unexplained. That comparison
lives outside this file because the PowerShell form no longer exists.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-test-substrate.py"
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("guard_test_substrate", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_test_substrate"] = guard
_spec.loader.exec_module(guard)


def read(name: str) -> str:
    return (REPO / "tests" / "Fixture" / name).read_text(encoding="utf-8")


class Tree:
    """A throwaway repository holding a `tests/` tree, so each case states only what it varies."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="gts-"))
        (self.root / "tests").mkdir()
        self.baseline = self.root / "baseline.txt"

    def add(self, rel: str, body: str) -> "Tree":
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
        return self

    def baseline_text(self, text: str) -> "Tree":
        self.baseline.write_text(text, encoding="utf-8")
        return self

    def scan(self) -> guard.Report:
        return guard.scan(self.root, self.baseline)

    def evaluate(self) -> list[str]:
        return guard.evaluate(self.scan())

    def run(self, *args: str) -> dict:
        argv = [sys.executable, str(SCRIPT), "--root", str(self.root),
                "--baseline-path", str(self.baseline), *args]
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=RUN_TIMEOUT)
        return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}

    def __enter__(self) -> "Tree":
        return self

    def __exit__(self, *_exc) -> None:
        import shutil
        shutil.rmtree(self.root, ignore_errors=True)


# ---- the fixtures, one per rule, with its negative twin ----------------------------------------------
SWALLOWED = """
public sealed class T {
    public void Run(string dir) {
        try { Directory.Delete(dir, true); } catch { }
    }
}
"""
SWALLOWED_FILE = """
public sealed class T {
    public void Run(string f) {
        try { File.Delete(f); } catch { /* best effort */ }
    }
}
"""
SWALLOWED_MULTILINE = """
public sealed class T {
    public void Run(string dir) {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
        }
    }
}
"""
REAL_CATCH_BODY = """
public sealed class T {
    public void Run(string dir) {
        try { Directory.Delete(dir, true); } catch (Exception e) { throw; }
    }
}
"""
NESTED_BRACE_BODY = """
public sealed class T {
    public void Run(string dir) {
        try { Directory.Delete(dir, true); } catch { if (x) { y(); } }
    }
}
"""
PHANTOM_COMMENT = """
// See `data/seed/items/charms/**` for the shipped set.
public sealed class T {
    /* a real block comment
       spanning lines */
    public void Run(string dir) {
        try { Directory.Delete(dir, true); } catch { }
    }
}
"""
CLEAN = """
public sealed class T {
    public int Value => 1;
}
"""
# The plan must be named in the rule's OWN vocabulary - InMemory / IsMemoryUri / MemoryUri. The first
# version of this fixture used `new RpgStore("memory:1")`, which matches none of them, so it was not a
# negative twin at all: it was a second positive case wearing a negative case's name, and it failed.
MEMORY_STORE = """
public sealed class T {
    public void Run() {
        var store = new RpgStore(MemoryUri);
    }
}
"""
TEMP_STORE = """
public sealed class T {
    public void Run() {
        var dir = Path.GetTempPath();
        var store = new RpgStore(dir);
    }
}
"""
FILE_BACKED = """
public sealed class T {
    public void Run() { var store = DataTestStore.CreateFileBacked(); }
}
"""
FILE_BACKED_TAGGED = """
public sealed class T {
    [Trait("Category", "DiskSemantics")]
    public void Run() { var store = DataTestStore.CreateFileBacked(); }
}
"""
CTOR_NO_PLAN = """
public sealed class T {
    public void Run(string dir) { var store = new RpgStore(dir); }
}
"""
CTOR_WITH_PLAN = """
public sealed class T {
    public void Run(string dir) { var store = new RpgStore(dir); var m = InMemory; }
}
"""
OPTIONS_FOR = """
public sealed class T {
    public void Run(string dir) { var store = RpgStoreOptions.For(dir); }
}
"""
CORPUS_COPY = """
public sealed class T {
    public void Run() {
        var root = Path.Combine(Path.GetTempPath(), "x");
        foreach (var f in Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories))
            File.Copy(f, dest, true);
    }
    private static readonly string[] seed = { "data", "seed" };
}
"""
# The negative twin the suite was missing. Temp path + File.Copy + an AllDirectories enumeration is
# what the rule looks for, and a suite that copied the corpus has a temp directory and a file copy in
# it too - so with no twin, dropping the `"data","seed"` requirement changed no fixture's answer and the
# mutation survived. The rule is about the SHIPPED CORPUS, not about every temp directory a test writes.
CORPUS_COPY_NO_SEED_ROOT = """
public sealed class T {
    public void Run() {
        var root = Path.Combine(Path.GetTempPath(), "x");
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, dest, true);
    }
}
"""
CORPUS_WRITE = """
public sealed class T {
    public void Run() {
        var p = Path.Combine(Path.GetTempPath(), "c.json");
        File.WriteAllText(p, "{}");
        var c = StructureCorpus.Load(p);
    }
}
"""
CORPUS_WRITE_TAGGED = """
public sealed class T {
    [Trait("Category", "DiskSemantics")]
    public void Run() {
        var p = Path.Combine(Path.GetTempPath(), "c.json");
        File.WriteAllText(p, "{}");
        var c = StructureCorpus.Load(p);
    }
}
"""


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement_and_every_flag(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=300)
        self.assertEqual(0, proc.returncode)
        self.assertIn("guard-test-substrate.py", proc.stdout)
        for flag in ("--root", "--baseline-path", "--update-baseline", "--json"):
            with self.subTest(flag=flag):
                self.assertIn(flag, proc.stdout)


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_a_missing_tests_directory_is_REFUSED_not_reported_clean(self) -> None:
        # A bare directory, because `Tree` always creates tests/ and so can never provoke this.
        with tempfile.TemporaryDirectory(prefix="gts-bare-") as tmp:
            with self.assertRaises(guard.Refusal) as caught:
                guard.scan(Path(tmp), Path(tmp) / "baseline.txt")
        self.assertEqual("TESTS-MISSING", caught.exception.reason)

    def test_a_missing_repository_root_is_REFUSED(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gts-gone-") as tmp:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(Path(tmp) / "no-such-dir"), "--json"],
                capture_output=True, text=True, timeout=300)
        self.assertEqual(guard.EXIT_FAILED, result.returncode)
        self.assertEqual("TESTS-MISSING", json.loads(result.stdout)["reason"])


class EachRuleAndItsNegativeTwin(unittest.TestCase):
    def _codes(self, tree: Tree) -> set[str]:
        return set(guard.scan(tree.root, tree.baseline).found.get("tests/A.cs", []))

    def test_swallowed_delete_empty_catch(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            self.assertEqual({"swallowed-delete"}, self._codes(tree))

    def test_swallowed_delete_file_delete_comment_only_catch(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED_FILE)
            self.assertEqual({"swallowed-delete"}, self._codes(tree))

    def test_swallowed_delete_multiline_empty_body(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED_MULTILINE)
            self.assertEqual({"swallowed-delete"}, self._codes(tree))

    def test_a_catch_with_a_REAL_body_is_NOT_swallowed(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", REAL_CATCH_BODY)
            self.assertEqual(set(), self._codes(tree))

    def test_a_NESTED_BRACE_in_the_body_does_not_end_the_catch_early(self) -> None:
        # The body is brace-matched, so `catch { if (x) { y(); } }` is NOT empty. A regex that stopped
        # at the first `}` would see `{ if (x) {` and call it a violation - failing a file that
        # correctly reports its own failure.
        with Tree() as tree:
            tree.add("tests/A.cs", NESTED_BRACE_BODY)
            self.assertEqual(set(), self._codes(tree))

    def test_A_DOC_COMMENT_NAMING_A_GLOB_PATH_DOES_NOT_EAT_THE_NEXT_REAL_COMMENT(self) -> None:
        # THE PHANTOM-COMMENT REGRESSION. `/**` in prose opened a block that ran to the next real `*/`,
        # deleting the Dispose and making a real swallowed delete invisible. Fifteen files contain such
        # prose and two really were violating.
        with Tree() as tree:
            tree.add("tests/A.cs", PHANTOM_COMMENT)
            self.assertEqual({"swallowed-delete"}, self._codes(tree))

    def test_a_MEMORY_PLANNED_store_is_clean(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", MEMORY_STORE)
            self.assertEqual(set(), self._codes(tree))

    def test_a_temp_store_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", TEMP_STORE)
            self.assertIn("temp-store", self._codes(tree))

    def test_an_untagged_helper_built_file_store_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", FILE_BACKED)
            self.assertIn("untagged-file-store", self._codes(tree))

    def test_the_DiskSemantics_TRAIT_EXEMPTS_it(self) -> None:
        # The trait is an attribute whose ARGUMENTS ARE LITERALS, so this rule must read the
        # strings-kept view. Reading the erased view fires on every correctly tagged file.
        with Tree() as tree:
            tree.add("tests/A.cs", FILE_BACKED_TAGGED)
            self.assertEqual(set(), self._codes(tree))

    def test_a_ctor_with_no_memory_plan_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CTOR_NO_PLAN)
            self.assertIn("untagged-file-store", self._codes(tree))

    def test_a_ctor_that_NAMES_the_memory_plan_is_clean(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CTOR_WITH_PLAN)
            self.assertEqual(set(), self._codes(tree))

    def test_RpgStoreOptions_For_with_one_argument_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", OPTIONS_FOR)
            self.assertIn("untagged-file-store", self._codes(tree))

    def test_a_shipped_corpus_COPY_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CORPUS_COPY)
            self.assertIn("temp-corpus", self._codes(tree))

    def test_a_temp_copy_of_something_that_is_NOT_the_shipped_corpus_is_clean(self) -> None:
        # The negative twin. Temp path + File.Copy + AllDirectories with no `"data","seed"` root is some
        # other fixture's business, and the rule is about the SHIPPED CORPUS - so without this the
        # seed-root requirement could be dropped and every fixture would still agree.
        with Tree() as tree:
            tree.add("tests/A.cs", CORPUS_COPY_NO_SEED_ROOT)
            self.assertEqual(set(), self._codes(tree))

    def test_a_catch_beyond_THE_WINDOW_is_declined_rather_than_claimed(self) -> None:
        # The window measures the distance from the `catch` to ITS OWN opening brace - the exception
        # specification - not the distance from the delete. That is the decision: a catch whose
        # specification runs past the window is a different statement, so the rule declines rather than
        # claiming it. The first version of this test put the catch in another METHOD, which the rule
        # reaches anyway (it searches forward from the delete for the next `catch`), so the fixture never
        # came near the boundary and making the window unbounded changed no answer.
        with Tree() as tree:
            # CONTROL: a short exception specification, so the brace is inside the window.
            tree.add("tests/A.cs",
                     "try { Directory.Delete(d, true); } catch (InvalidOperationException) { }")
            self.assertEqual({"swallowed-delete"}, self._codes(tree))
        with Tree() as tree:
            # A specification longer than the window. The body is empty, so without the window this
            # would be reported - the guard declining a real finding is a cost, and a stated one.
            long_spec = "E" * (guard.CATCH_WINDOW + 40)
            tree.add("tests/A.cs",
                     f"try {{ Directory.Delete(d, true); }} catch ({long_spec}) {{ }}")
            self.assertEqual(set(), self._codes(tree))

    def test_a_corpus_WRITE_and_load_is_flagged(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CORPUS_WRITE)
            self.assertIn("temp-corpus-write", self._codes(tree))

    def test_the_trait_also_exempts_a_corpus_write(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CORPUS_WRITE_TAGGED)
            self.assertEqual(set(), self._codes(tree))

    def test_a_clean_file_produces_nothing(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            self.assertEqual({}, guard.scan(tree.root, tree.baseline).found)


class TheScanSurface(unittest.TestCase):
    def test_build_output_is_not_scanned(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            tree.add("tests/obj/Debug/net8.0/Gen.cs", SWALLOWED)
            tree.add("tests/bin/Release/Gen.cs", SWALLOWED)
            self.assertEqual({}, tree.scan().found)

    def test_a_non_cs_file_is_not_scanned(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.txt", SWALLOWED)
            self.assertEqual({}, tree.scan().found)

    def test_the_GATE_OWN_TESTS_are_exempt_by_NAME(self) -> None:
        # They have to contain the banned shapes or they could not prove the gate fires. ONE entry, not
        # a rule about tests directories.
        with Tree() as tree:
            # NOT `f"tests/{...}"`: the entry already begins with `tests/`, and the first version of this
            # test wrote to `tests/tests/...`, which is outside the exemption - so the "exempt" case
            # reported a violation and the "not exempt" case below passed for the wrong reason. Two
            # tests, one prefix.
            tree.add(guard.SELF_EXEMPT[0], SWALLOWED)
            self.assertEqual({}, tree.scan().found)
        self.assertEqual(1, len(guard.SELF_EXEMPT),
                         "the exemption is one named file; a second entry is a widened exemption")

    def test_a_DIFFERENTLY_named_test_file_is_NOT_exempt(self) -> None:
        with Tree() as tree:
            tree.add("tests/SomeOtherGuardTests.cs", SWALLOWED)
            self.assertIn("tests/SomeOtherGuardTests.cs", tree.scan().found)

    def test_every_reported_path_is_relative_to_the_ROOT_not_a_chopped_prefix(self) -> None:
        # The port's `relative_to` cannot produce `0a_/tests/A.cs`, which is what the original's
        # `Substring($Root.Length)` did whenever the two disagreed about the path's length. Asserted as
        # a property of every reported key rather than on one fixture, because the defect was a
        # mismatch between two path spellings and a single fixture can miss it.
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            tree.add("tests/nested/deep/B.cs", TEMP_STORE)
            for rel in tree.scan().found:
                with self.subTest(rel=rel):
                    self.assertFalse(rel.startswith("/"))
                    self.assertNotIn(":", rel)
                    self.assertEqual(rel, rel.lstrip("/"))
                    self.assertTrue((tree.root / rel).is_file(), f"{rel} is not a real file")


class TheRatchet(unittest.TestCase):
    def test_a_baseline_line_absorbs_the_violation(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            tree.baseline_text("tests/A.cs : swallowed-delete")
            self.assertEqual([], tree.evaluate())

    def test_a_NEW_code_in_an_exempt_file_is_still_new(self) -> None:
        # The exemption is per CODE, not per file: a file already exempted for a swallowed delete does
        # not thereby become exempt for a temp-backed store.
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED + TEMP_STORE)
            tree.baseline_text("tests/A.cs : swallowed-delete")
            failures = tree.evaluate()
        self.assertTrue(any("temp-store" in f and "new code" in f for f in failures), failures)

    def test_a_STALE_baseline_line_fails_so_the_ratchet_only_shrinks(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            tree.baseline_text("tests/A.cs : swallowed-delete")
            failures = tree.evaluate()
        self.assertTrue(any("no longer violated" in f for f in failures), failures)

    def test_an_unlisted_file_with_a_violation_is_new(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            failures = tree.evaluate()
        self.assertTrue(any("not in baseline" in f for f in failures), failures)

    def test_comments_and_blank_lines_in_the_baseline_are_skipped(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            tree.baseline_text("# a comment\n\ntests/A.cs : swallowed-delete")
            self.assertEqual([], tree.evaluate())

    def test_a_line_with_NO_separator_is_not_an_entry(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            tree.baseline_text("tests/A.cs swallowed-delete")
            self.assertEqual(0, len(guard.read_baseline(tree.baseline)))

    def test_a_missing_baseline_is_an_EMPTY_ratchet(self) -> None:
        with Tree() as tree:
            self.assertEqual({}, guard.read_baseline(tree.baseline))
            tree.add("tests/A.cs", SWALLOWED)
            self.assertTrue(tree.evaluate(), "a missing baseline means nothing is exempted")


class TheVerdictStringsAreBytePreserved(unittest.TestCase):
    """22 `Assert.Contains` across the Guard suite depend on these strings, so they are preserved
    including their em-dashes. A test that asserts only the prefix would not notice ASCII-fication, and
    the next substring assertion on the tail would."""

    def test_the_OK_line(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            result = tree.run()
        self.assertEqual(guard.EXIT_OK, result["exit"])
        self.assertTrue(result["stdout"].startswith("TEST SUBSTRATE GUARD OK \u2014 "),
                        result["stdout"][:90])

    def test_the_FAILED_line_and_its_findings(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            result = tree.run()
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("TEST SUBSTRATE GUARD FAILED \u2014 ", result["stderr"])
        self.assertIn("swallowed-delete (new \u2014 not in baseline)", result["stderr"])

    def test_the_new_CODE_wording_is_preserved(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED + TEMP_STORE)
            tree.baseline_text("tests/A.cs : swallowed-delete")
            result = tree.run()
        self.assertIn("new code \u2014 not in baseline line", result["stderr"])

    def test_the_STALE_wording_is_preserved(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            tree.baseline_text("tests/A.cs : swallowed-delete")
            result = tree.run()
        self.assertIn("no longer violated \u2014 remove the line (the ratchet only shrinks)",
                      result["stderr"])

    def test_the_findings_go_to_STDERR_and_stdout_carries_no_finding(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            result = tree.run()
        self.assertEqual("", result["stdout"].strip(),
                         "a caller reading stdout alone must not be able to mistake a finding for a "
                         "verdict")

    def test_the_ratchet_header_is_byte_preserved_INCLUDING_its_em_dashes(self) -> None:
        header = guard.baseline_header(date(2026, 9, 28).isoformat())
        self.assertEqual(4, len(header))
        self.assertIn("remove its line when fixed", header[1])
        self.assertIn("\u2014", header[1], "the header's em-dash is part of the tracked file's text")
        self.assertIn("guard-test-substrate.py", header[3])


class TheJsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "problems", "files_scanned", "baseline", "baseline_entries"}
    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with Tree() as ok:
            ok.add("tests/A.cs", CLEAN)
            good = json.loads(ok.run("--json")["stdout"])
        with Tree() as bad:
            bad.add("tests/A.cs", SWALLOWED)
            poor = json.loads(bad.run("--json")["stdout"])
        self.assertEqual(self.KEYS, set(good))
        self.assertEqual(self.KEYS, set(poor))
        self.assertEqual("OK", good["verdict"])
        self.assertEqual("FAIL", poor["verdict"])

    def test_a_refusal_carries_its_reason_and_the_same_keys(self) -> None:
        # A bare directory, not a `Tree`: a `Tree` always has tests/ and would return OK.
        with tempfile.TemporaryDirectory(prefix="gts-refuse-") as tmp:
            payload = json.loads(subprocess.run(
                [sys.executable, str(SCRIPT), "--root", tmp, "--json"],
                capture_output=True, text=True, timeout=300).stdout)
        self.assertEqual(self.REFUSAL_KEYS, set(payload))
        self.assertEqual("TESTS-MISSING", payload["reason"])


class UpdateBaseline(unittest.TestCase):
    def test_it_writes_a_ratchet_and_exits_zero(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            result = tree.run("--update-baseline")
            self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])
            written = tree.baseline.read_text(encoding="utf-8")
        self.assertIn("tests/A.cs : swallowed-delete", written)
        self.assertTrue(written.startswith("# Test-substrate baseline"))

    def test_the_written_ratchet_then_MAKES_the_run_pass(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", SWALLOWED)
            tree.run("--update-baseline")
            self.assertEqual(guard.EXIT_OK, tree.run()["exit"])

    def test_the_written_baseline_is_the_FILES_that_actually_violate(self) -> None:
        with Tree() as tree:
            tree.add("tests/A.cs", CLEAN)
            tree.add("tests/B.cs", SWALLOWED)
            tree.run("--update-baseline")
            lines = [l for l in tree.baseline.read_text(encoding="utf-8").splitlines()
                     if l and not l.startswith("#")]
        self.assertEqual(["tests/B.cs : swallowed-delete"], lines)


class TheShippedState(unittest.TestCase):
    def test_the_real_repository_is_clean(self) -> None:
        report = guard.scan(REPO, REPO / "scripts" / guard.BASELINE_RELPATH)
        failures = guard.evaluate(report)
        self.assertEqual([], failures, failures[:8])

    def test_the_real_repository_has_a_baseline(self) -> None:
        self.assertTrue((REPO / "scripts" / guard.BASELINE_RELPATH).is_file())

    def test_the_scanner_is_SHARED_not_reimplemented(self) -> None:
        # Two other guards already use `cscan`; this is the third. A fourth copy of the stripper is the
        # defect the program set out to remove, and its divergence was a live invisible pass.
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("import cscan", source)
        self.assertNotIn("def strip_comments", source,
                         "this guard must call the shared scanner, not carry a copy of it")
        for policy in ("strip_comments_and_literals_preserving_layout",
                       "strip_comments_preserving_layout"):
            with self.subTest(policy=policy):
                self.assertIn(policy, source)

    def test_both_scanner_policies_preserve_EVERY_offset(self) -> None:
        # `Find-SwallowedDelete` takes a match index in the stripped text; the original then sliced the
        # RAW text with it, which is only sound if the two have the same length. Asserted directly, so
        # a future scanner change that collapses text cannot silently invalidate an index again.
        sample = (SWALLOWED + 'string s = "data", t = "seed";\n// `a/**b` in prose\n'
                  'var u = @"verbatim\nmultiline";\n')
        for policy in (guard.cscan.strip_comments_and_literals_preserving_layout,
                       guard.cscan.strip_comments_preserving_layout):
            with self.subTest(policy=policy.__name__):
                self.assertEqual(len(sample), len(policy(sample)),
                                 f"{policy.__name__} changed the length, so no index into its output "
                                 "may be used to slice the original")


if __name__ == "__main__":
    unittest.main()
