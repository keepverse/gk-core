"""Contract tests for `gk-core/scripts/guard-clock-seam.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, and the behaviour of both rules. It does not assert a message body or a line
count. It DOES pin the allowlist's shape and each entry's REASON, because a reason-free entry is
indistinguishable from an oversight and the reasons are the specification
(`docs/architecture/rpg-simulator-spec-clock-seam.md` §7 is the authority).

Three things this suite exists to stop a later reader from "fixing":

  * **The stripper KEEPS string literals**, so `Log("DateTime.UtcNow")` is reported. That over-match is
    the contract. `cscan.strip_comments_and_literals_preserving_layout` would blank the literal and
    NARROW the guard — a plausible-looking cleanup nobody would notice.
  * **Rule 1's ambient pattern does NOT fold case; rule 2's DOES**, because the original used
    `[regex]::IsMatch` for one and `-notmatch` for the other. Two conventions, one script.
  * **A stale allowlist entry is itself a finding.** That half is why the guard is deliberately
    sensitive to its source set, and why a missing source tree reddens rather than passing quietly.

Differential evidence: 20 fixtures, 18 identical in exit code and every emitted line INCLUDING the
counter summary, plus 2 declared divergences. That comparison lives outside this file because the
PowerShell form no longer exists.

The seed content here is GENERATED from the port's own allowlist. A hand-written version satisfied
10 of 19 entries, so every fixture was red on staleness and the "clean" case was not clean — and a
differential comparing like-for-like red trees says nothing about the clean path.
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
SCRIPT = REPO / "scripts" / "guard-clock-seam.py"

_spec = importlib.util.spec_from_file_location("guard_clock_seam", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_clock_seam"] = guard
_spec.loader.exec_module(guard)

# The seed tree: every allowlisted path carrying every one of its fragments, plus the clock type.
SEED: dict[str, str] = {
    path: "".join(f"        {fragment}\n" for fragment, _reason in entries) + "    }\n}\n"
    for path, entries in guard.ALLOWLIST.items()
}
SEED[guard.CLOCK_TYPE] = "class Clock { DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }\n"


def build(root: Path, extra: dict[str, str] | None = None) -> Path:
    for rel, text in {**SEED, **(extra or {})}.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return root


def check(root: Path, **kwargs) -> dict:
    return guard.check(root, **kwargs)


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=1800)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("guard-clock-seam.py", proc.stdout)

    def test_the_src_dir_flag_is_accepted(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-cli-") as tmp:
            root = build(Path(tmp))
            result = run("--root", str(root), "--src-dir", str(root / "src"))
            self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_the_real_tree_is_clean(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["violations"][:3])

    def test_a_violation_fails(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-exit-") as tmp:
            root = build(Path(tmp), {"src/FusionRpg.Core/Battle/O.cs":
                                     "class O { void M() { var t = DateTime.UtcNow; } }\n"})
            self.assertEqual("FAIL", check(root)["verdict"])

    def test_findings_go_to_stderr_and_the_verdict_to_stdout(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-exit-") as tmp:
            root = build(Path(tmp), {"src/FusionRpg.Core/Battle/O.cs":
                                     "class O { void M() { var t = DateTime.UtcNow; } }\n"})
            result = run("--root", str(root))
            self.assertEqual(guard.EXIT_FAILED, result["exit"])
            self.assertEqual("", result["stdout"].strip())
            self.assertIn("DateTime.UtcNow", result["stderr"])


class TheSeededTreeIsClean(unittest.TestCase):
    """If this fails, every rule test below is running against a red tree and proving less than it
    appears to. The hand-written seed satisfied 10 of 19 entries, so this assertion is the one that
    would have caught it."""

    def test_the_seed_satisfies_every_allowlist_entry(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-seed-") as tmp:
            got = check(build(Path(tmp)))
            self.assertEqual([], got["violations"])
            self.assertEqual(len(guard.ALLOWLIST) and
                             sum(len(e) for e in guard.ALLOWLIST.values()),
                             got["allowlist_entries"])

    def test_every_allowlisted_read_is_counted(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-seed-") as tmp:
            got = check(build(Path(tmp)))
            self.assertEqual(0, got["purity_references"])
            self.assertEqual(got["allowlist_entries"], got["allowlisted_reads"])
            self.assertGreaterEqual(got["clock_type_reads"], 1)


class RuleOneAmbientReads(unittest.TestCase):
    def _ids(self, extra) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="cs-r1-") as tmp:
            return check(build(Path(tmp), extra))["violations"]

    def test_a_read_outside_the_allowlist_is_reported(self) -> None:
        self.assertTrue(self._ids({"src/FusionRpg.Core/Battle/O.cs":
                                    "class O { void M() { var t = DateTime.UtcNow; } }\n"}))

    def test_both_Now_and_UtcNow_are_caught(self) -> None:
        for expr in ("DateTime.UtcNow", "DateTimeOffset.UtcNow", "DateTime.Now",
                     "DateTimeOffset.Now"):
            with self.subTest(expr=expr):
                self.assertTrue(self._ids({"src/FusionRpg.Core/Battle/O.cs":
                                           f"class O {{ void M() {{ var t = {expr}; }} }}\n"}))

    def test_a_read_inside_a_comment_is_NOT_one(self) -> None:
        # The stripper blanks comments while preserving line numbers, so a documented read is not a
        # read and the finding still cites the right line.
        self.assertEqual([], self._ids({"src/FusionRpg.Core/Battle/N.cs":
                                        "class N { // DateTime.UtcNow\n void M() { } }\n"}))
        self.assertEqual([], self._ids({"src/FusionRpg.Core/Battle/N.cs":
                                        "class N { /* DateTime.UtcNow */ void M() { } }\n"}))

    def test_a_read_inside_a_STRING_LITERAL_IS_caught_and_that_is_the_contract(self) -> None:
        # The stripper KEEPS literals, so this over-matches. Switching to the literal-blanking policy
        # would narrow the guard silently, which is why this test exists.
        self.assertTrue(self._ids({"src/FusionRpg.Core/Battle/N.cs":
                                   'class N { void M() { Log("DateTime.UtcNow"); } }\n'}))

    def test_the_clock_type_is_exempt_BY_PATH(self) -> None:
        # The exemption names ONE file. A read in a different file under the same directory is a
        # violation, and the clean seed proves the named file itself is exempt while holding a read.
        self.assertTrue(self._ids({"src/FusionRpg.Core/Time/Other.cs":
                                   "class O { void M() { var t = DateTime.UtcNow; } }\n"}))

    def test_the_ambient_pattern_does_NOT_fold_case(self) -> None:
        # The original used [regex]::IsMatch for this pattern, which is case-sensitive. Rule 2 folds.
        # One script, two conventions, and this is the pair.
        self.assertEqual([], self._ids({"src/FusionRpg.Core/Battle/O.cs":
                                        "class O { void M() { var t = datetime.utcnow; } }\n"}))

    def test_the_allowlist_matches_a_LINE_SUBSTRING_so_trailing_text_is_fine(self) -> None:
        self.assertEqual([], self._ids({"src/FusionRpg.Server/WebMatchService.cs":
                                        "class W { void M() { var t0 = DateTime.UtcNow; "
                                        "// stamped at ingest\n } }\n"}))

    def test_build_output_is_never_source(self) -> None:
        self.assertEqual([], self._ids({
            "src/FusionRpg.Core/obj/D.cs": "class D { void M() { var t = DateTime.UtcNow; } }\n",
            "src/FusionRpg.Core/bin/D.cs": "class D { void M() { var t = DateTime.UtcNow; } }\n"}))

    def test_the_line_number_is_the_FILES_OWN(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-r1-") as tmp:
            got = check(build(Path(tmp), {"src/FusionRpg.Core/Battle/O.cs":
                                          "// a comment line\n"
                                          "class O { void M() { var t = DateTime.UtcNow; } }\n"}))
            hit = [v for v in got["violations"] if "O.cs" in v]
            self.assertTrue(hit)
            self.assertIn(":2:", hit[0])


class RuleOneStaleness(unittest.TestCase):
    """An allowlist entry that no longer matches a real read is itself a finding — a stale allowlist
    is how the next genuine read slips in unexamined."""

    def test_dropping_a_file_reports_each_of_its_entries_as_stale(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-stale-") as tmp:
            root = Path(tmp)
            for rel, text in SEED.items():
                if rel == "src/FusionRpg.Server/WebMatchService.cs":
                    continue
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(text, encoding="utf-8")
            got = check(root)
            self.assertEqual("FAIL", got["verdict"])
            self.assertEqual(1, len(got["violations"]))
            self.assertIn("stale allowlist entry", got["violations"][0])

    def test_a_missing_source_tree_is_named_rather_than_reported_as_staleness(self) -> None:
        # The original scanned an empty file set and then reported EVERY entry stale, so the message
        # is about staleness rather than about the missing tree — a red guard pointing at the wrong
        # thing. The port names it, and the diagnostic cost of that is one line.
        with tempfile.TemporaryDirectory(prefix="cs-stale-") as tmp:
            with self.assertRaises(guard.Refusal) as caught:
                check(Path(tmp), src_dir="no-such-src")
            self.assertEqual("SRC-DIR-MISSING", caught.exception.reason)


class RuleTwoThePurityTrees(unittest.TestCase):
    """The seam IS a wall clock, so the replay-critical trees may not read one."""

    def _ids(self, rel: str, text: str) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="cs-r2-") as tmp:
            return check(build(Path(tmp), {rel: text}))["violations"]

    def test_a_reference_in_each_purity_tree_is_reported(self) -> None:
        for root in guard.PURITY_ROOTS:
            with self.subTest(root=root):
                self.assertTrue(self._ids(f"src/{root}/T.cs", "class T { ServerClock c; }\n"))

    def test_the_rule_2_pattern_FOLDS_case(self) -> None:
        # `-notmatch` in the original, so rule 2 folds where rule 1 does not.
        self.assertTrue(self._ids("src/FusionRpg.Core/Battle/T.cs",
                                   "class T { serverclock c; }\n"))

    def test_a_reference_outside_a_purity_tree_is_fine(self) -> None:
        self.assertEqual([], self._ids("src/FusionRpg.Core/Economy/T.cs",
                                       "class T { ServerClock c; }\n"))

    def test_a_reference_inside_a_COMMENT_is_not_one(self) -> None:
        self.assertEqual([], self._ids("src/FusionRpg.Core/Effects/N.cs",
                                       "class N { /* ServerClock */ void M() { } }\n"))

    def test_the_AMBIENT_pattern_does_not_trigger_rule_2(self) -> None:
        # An ambient read in a purity tree IS a rule-1 violation - rule 1 applies everywhere - so the
        # claim under test is narrower: it must not produce a RULE 2 finding. Asserting "no
        # violations" would have been asserting something false, and would have passed for the wrong
        # reason if rule 1 had been broken.
        found = self._ids("src/FusionRpg.Core/World/T.cs",
                          "class T { void M() { var t = DateTime.UtcNow; } }\n")
        self.assertTrue(found, "rule 1 must still fire in a purity tree")
        self.assertFalse([v for v in found if "ServerClock is referenced" in v])

    def test_the_purity_roots_are_the_documented_three(self) -> None:
        # A closed vocabulary the code owns; a fourth tree needs a decision, not an edit.
        self.assertEqual(("FusionRpg.Core/World", "FusionRpg.Core/Battle",
                          "FusionRpg.Core/Effects"), guard.PURITY_ROOTS)


class TheAllowlistIsTheSpecification(unittest.TestCase):
    """Every entry carries a REASON, keyed by an exact repo-relative path. Both are contract."""

    def test_every_entry_carries_a_reason(self) -> None:
        for path, entries in guard.ALLOWLIST.items():
            for fragment, reason in entries:
                with self.subTest(path=path, fragment=fragment[:40]):
                    self.assertTrue(reason.strip(), "an allowlist entry without a reason is an oversight")
                    self.assertGreater(len(reason), 40, "a reason should say WHY, not restate the code")

    def test_every_path_is_repo_relative_and_absolute_free(self) -> None:
        for path in guard.ALLOWLIST:
            with self.subTest(path=path):
                self.assertFalse(path.startswith("/"))
                self.assertNotIn("\\", path)
                self.assertTrue(path.startswith("src/"))

    def test_the_table_is_not_empty(self) -> None:
        # Not a count: an empty allowlist makes the allowlist mechanism vacuous and every allowlist
        # test above would pass for the wrong reason.
        self.assertTrue(guard.ALLOWLIST)

    def test_no_entry_is_listed_twice_in_one_file(self) -> None:
        for path, entries in guard.ALLOWLIST.items():
            fragments = [f for f, _ in entries]
            with self.subTest(path=path):
                self.assertEqual(len(fragments), len(set(fragments)))


class TheStripperChoice(unittest.TestCase):
    def test_the_layout_invariant_holds(self) -> None:
        source = "var a = 1; // NOTE\n/* x\n y */\nvar b = 2;"
        out = guard._stripped("".join(guard._stripped([source]))) if False else None
        text = "\n".join(guard._stripped(source))
        self.assertEqual(source.count("\n"), text.count("\n"))
        self.assertNotIn("NOTE", text)
        self.assertNotIn("*/", text)

    def test_literals_are_KEPT_which_is_what_this_guard_needs(self) -> None:
        # Asserted directly on the policy, so a later editor swapping in the literal-blanking variant
        # sees the reason here rather than finding out through a narrower guard.
        self.assertIn('"DateTime.UtcNow"', "\n".join(guard._stripped('Log("DateTime.UtcNow");')))


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "violations", "summary", "source_files", "ambient_reads",
            "clock_type_reads", "allowlisted_reads", "allowlist_entries", "purity_references"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-json-") as tmp:
            good = build(Path(tmp) / "ok")
            bad = build(Path(tmp) / "bad", {"src/FusionRpg.Core/Battle/O.cs":
                                             "class O { void M() { var t = DateTime.UtcNow; } }\n"})
            for root, verdict in ((good, "OK"), (bad, "FAIL")):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run(
                        [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                        capture_output=True, text=True, timeout=1800)
                    self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
                    self.assertEqual(verdict, json.loads(proc.stdout)["verdict"])

    def test_stdout_carries_only_the_verdict(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cs-json-") as tmp:
            root = build(Path(tmp))
            result = run("--root", str(root))
            self.assertEqual(guard.VERDICT_OK, result["stdout"].strip())
            self.assertIn("clock seam guard: source files=", result["stderr"])


class TheShippedState(unittest.TestCase):
    def test_this_repo_is_clean(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["violations"][:3])

    def test_every_shipped_allowlist_entry_still_matches(self) -> None:
        # The staleness half, on the real tree. If a refactor moves one of these reads, this goes red
        # and the entry must be repointed with its reason - not deleted to make it quiet.
        got = guard.check(REPO)
        self.assertEqual(0, len([v for v in got["violations"] if "stale" in v]))
        self.assertEqual(got["allowlist_entries"], got["allowlisted_reads"])


if __name__ == "__main__":
    unittest.main()
