"""Contract tests for `gk-core/scripts/guard-test-content-root.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the two refusing shapes and their NEGATIVE twins, the asymmetries that are easy to
regress, and the shared scanner this guard consumes rather than reimplements.

WHY THE DIFFERENTIAL'S CASES ARE REPEATED HERE
----------------------------------------------
The differential proves the port against `guard-test-content-root.ps1`, and it dies with that file. A
rule proven only there is unproven afterwards, so every case that carries the rule moves here too. What
it does NOT move is the comparison itself: the byte-identity with the original is evidence about the
PORT, and this file is evidence about the RULE.

WHY EVERY RULE HAS ITS NEGATIVE TWIN
------------------------------------
A rule that fires on everything passes every positive case. Each shape here is paired with a walk that
must NOT fire - a walk landing exactly on the repository root, a segment that is not a Keepverse root, a
commented-out walk, a local that is assigned once - so a rule that has lost its discriminator is visible
rather than quietly agreeing with everything.

THE DEPTH ARITHMETIC, WHICH IS THE WHOLE RULE
`depth` is the segment count of the file's DIRECTORY, so a file at `tests/P/A.cs` sits at depth 2 and
a two-step walk from it lands ON the root. The first revision of the differential called that an escape;
both implementations agreed with each other and both were answering a mislabelled question. Every depth
case below states its arithmetic in the test name.

THE ASYMMETRY THAT IS EASIEST TO REGRESS
Opacity is not a mute button. A local assigned more than once is a walk-up loop's cursor, and that
suppresses `walk-misses-root` but NOT `walk-escapes-root` - because "it did not reach depth 0" is a
claim about a stable anchor, while a walk below the root is below it whatever the cursor did. The two
tests pinning the halves differ by a single line of reassignment, so neither can be satisfied by a
change that moves both.

WHAT IS DELIBERATELY NOT ASSERTED
---------------------------------
The three readings (`files_scanned`, `caller_file_files`, `anchored_walks`) are a READING, not a
population: they move whenever a test is added, and a test that pins them is a test that goes red for
the wrong reason. They are type- and range-checked instead. Likewise no test here asserts a message
body, and the failure lines are checked for their shape - `path:line: kind - prose` - rather than for
their text.

Differential evidence: 27 fixtures - 26 correct against their own stated expectation AND byte-identical
in exit code and every emitted line, 1 declared divergence (a refusal the original had no name for), 0
unexplained.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
# The script under test. The environment override exists for MUTATION FALSIFICATION: a suite that can
# only ever load one path cannot be pointed at a deliberately broken copy, so "break the implementation
# and watch a test go red" would have no way to run. It changes which FILE is loaded and nothing else,
# and it is asserted to resolve to a real file so a typo fails loudly instead of skipping the suite.
SCRIPT = Path(os.environ.get("GUARD_TEST_CONTENT_ROOT_SCRIPT",
                             REPO / "scripts" / "guard-test-content-root.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("guard_test_content_root", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_test_content_root"] = guard
_spec.loader.exec_module(guard)

ESCAPES = "walk-escapes-root"
MISSES = "walk-misses-root"
# The CLOSED vocabulary of what counts as a Keepverse root. A test may not add to it: the vocabulary is
# the rule, and a growing list would make the guard's silence a function of the tree.
ROOT_SEGMENTS = {"data", "content", "docs", "tasks"}
# A finding line is `rel:line: kind - prose`. The shape is the contract; the prose is not.
PROBLEM_SHAPE = re.compile(r"^[^:]+:\d+: (walk-escapes-root|walk-misses-root) - .+$")
# The exit-code vocabulary. Closed on purpose: a FOURTH value is a defect in the mapping, not an
# extension of the contract. That is why this is a literal set and not one derived from the guard: a
# set built from the tool under test can never fail, and this assertion is the only thing that notices
# an exit code the contract does not have.
#
# 64 is in the contract because the guard's own header records putting it there. A refusal used to be
# emitted as `"verdict": "REFUSED"` with a REFUSED banner and then returned EXIT_FAILED, which is the
# conflation this vocabulary exists to catch: one code for "the tree is bad" and one for "I could not
# look at the tree". It is EX_USAGE, the conventional code for a bad invocation. Adding it here is
# recording a decision the tool already documents, not widening the contract to fit the tool.
EXIT_VOCABULARY = {0, 1, 64}


def fixture(body: str, decl: str = "") -> str:
    """A C# file in the shape the RULE tests for: a `[CallerFilePath]`-anchored walk.

    Assembled rather than pasted. A pasted fixture proves only that a regex matches the text someone
    expected to write; this one states the anchor, so a test can reason about the depth.
    """
    return ("using System.IO;\n"
            "public sealed class T {\n"
            "    public void Run([CallerFilePath] string here = \"\") {\n"
            f"{decl}{body}"
            "    }\n}\n")


# `tests/P/A.cs` -> the file's directory is `tests/P`, two segments below the root, so depth 2.
D2_LANDS_ON_ROOT = fixture('        var root = Path.Combine(Path.GetDirectoryName(here), "..", "..");\n')
D2_ESCAPES = fixture('        var root = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..");\n')
D2_MISSES = fixture('        var root = Path.Combine(Path.GetDirectoryName(here), "..", "data");\n')
D2_IMPURE_ESCAPE = fixture(
    decl='        var dir = Path.GetDirectoryName(here);\n'
          '        dir = Path.GetDirectoryName(dir);\n'
          '        var testsDir = Path.Combine(dir, "..");\n',
    body='        var root = Path.Combine(testsDir, "..", "..", "..");\n')
D2_IMPURE_MISSES = fixture(
    decl='        var dir = Path.GetDirectoryName(here);\n'
          '        dir = Path.GetDirectoryName(dir);\n'
          '        var testsDir = Path.Combine(dir, "..");\n',
    body='        var root = Path.Combine(testsDir, "data");\n')
D2_PURE_MISSES = fixture(
    decl='        var dir = Path.GetDirectoryName(here);\n'
          '        var testsDir = Path.Combine(dir, "..");\n',
    body='        var root = Path.Combine(testsDir, "data");\n')
NO_ANCHOR = ('using System.IO;\npublic sealed class T {\n'
             '    public void Run() { var r = Path.Combine("..", "..", ".."); }\n}\n')


def write_tree(root: Path, files: dict[str, str]) -> Path:
    for rel, body in files.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
    return root


class TreeCase(unittest.TestCase):
    """Each test gets its own throwaway repository. The guard's whole job is relative resolution, so a
    shared tree would let one test's fixture change another's depth."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="guard-tcr-test-")
        self.root = Path(self._tmp.name) / "tree"
        self.root.mkdir()

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def invoke(self, *argv: str) -> tuple[int, str, str]:
        """Run the tool as a SUBPROCESS. Not in-process: the CLI surface, the streams and the exit code
        are all part of the contract, and importing the module would prove none of them."""
        proc = subprocess.run([sys.executable, str(SCRIPT), *argv],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO))
        return proc.returncode, proc.stdout, proc.stderr

    def json_of(self, *argv: str) -> tuple[int, dict]:
        code, out, _err = self.invoke(*argv, "--json")
        self.assertIn(code, EXIT_VOCABULARY, f"exit {code} is outside the vocabulary")
        try:
            return code, json.loads(out[out.index("{"):])
        except (ValueError, json.JSONDecodeError) as exc:  # noqa: PERF203 - a bad envelope IS a finding
            self.fail(f"--json did not emit one JSON document: {exc}\n{out[:400]}")


class CliSurface(TreeCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_takes_no_PowerShell_spelled_flag(self) -> None:
        """A port that still answered `-Root` would let a caller keep the old invocation alive and
        never notice the retirement, which is the whole failure this migration exists to prevent."""
        code, out, err = self.invoke("-Root", str(self.root))
        self.assertNotEqual(code, 0, f"the port still accepts -Root:\n{out}\n{err}")
        self.assertIn("unrecognized arguments", err.lower() + out.lower())

    def test_it_does_not_shell_out_to_a_PowerShell_interpreter(self) -> None:
        """The program is retiring PowerShell. A Python tool that shells back out to `pwsh` is a
        wrapper, not a port, and would keep the interpreter on the critical path forever."""
        source = SCRIPT.read_text(encoding="utf-8")
        for token in ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "PSTypeName"):
            self.assertNotIn(token, source, f"the port still references {token!r}")

    def test_it_does_not_reimplement_the_shared_scanner(self) -> None:
        """It imports the shared scanner, as `guard-test-substrate.py` does. A private copy is a second
        stripper, and a second stripper is a second set of bugs - the one that ate `Dispose` in the
        substrate guard. The relationship is asserted rather than an ordinal: `cscan.py`'s own header
        says its consumer list is a reading, so pinning a count would have rotted on the next port."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("import cscan", source, "the guard must consume the shared scanner")
        self.assertIs(guard.cscan, sys.modules["cscan"],
                      "the imported cscan is not the repository's shared module")


class VerdictAndEnvelope(TreeCase):
    OK_KEYS = {"guard", "verdict", "problems", "files_scanned", "caller_file_files", "anchored_walks"}
    REFUSED_KEYS = OK_KEYS | {"reason", "detail"}

    def test_a_clean_tree_is_OK_with_exit_zero(self) -> None:
        write_tree(self.root, {"tests/P/A.cs": D2_LANDS_ON_ROOT})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["problems"], [])

    def test_a_finding_is_FAIL_with_exit_one(self) -> None:
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1)
        self.assertEqual(payload["verdict"], "FAIL")
        self.assertEqual(len(payload["problems"]), 1)

    def test_the_envelope_key_set_is_closed_in_both_states(self) -> None:
        """An envelope, not a population: the keys are the contract, so a NEW key is a change a caller
        can depend on and a DROPPED key is a silent removal. Both are checked exactly."""
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        _, ok_or_fail = self.json_of("--root", str(self.root))
        self.assertEqual(set(ok_or_fail), self.OK_KEYS)
        clean = Path(self._tmp.name) / "empty"
        (clean / "tests").mkdir(parents=True)
        _, empty = self.json_of("--root", str(clean))
        self.assertEqual(set(empty), self.OK_KEYS)
        _, refused = self.json_of("--root", str(Path(self._tmp.name) / "absent"))
        self.assertEqual(set(refused), self.REFUSED_KEYS)

    def test_a_refusal_is_its_OWN_verdict_not_a_finding(self) -> None:
        """A caller that reads `verdict == "FAIL"` as "the tree has a bad walk" would report a broken
        invocation as a bad tree, and a guard that cannot tell those apart trains people to ignore it."""
        code, payload = self.json_of("--root", str(Path(self._tmp.name) / "absent"))
        # 64, NOT 1. Asserting 1 here would encode the exact conflation this test exists to catch: it
        # is the only assertion that distinguishes "the tree has a bad walk" from "I could not look at
        # the tree", and every other assertion in this file is about the first. A refusal that returned
        # 1 would satisfy this line and defeat the four assertions below it.
        self.assertEqual(code, 64)
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["problems"], [], "a refusal must not invent findings")
        self.assertEqual(payload["files_scanned"], 0, "a refusal must not report a reading it never took")

    def test_the_guard_names_itself_in_every_state(self) -> None:
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        _, failing = self.json_of("--root", str(self.root))
        _, refused = self.json_of("--root", str(Path(self._tmp.name) / "absent"))
        self.assertEqual(failing["guard"], "test-content-root")
        self.assertEqual(refused["guard"], "test-content-root")

    def test_the_readings_are_INTEGERS_that_move_and_are_never_pinned(self) -> None:
        """They are a reading (validation-ssot.md): a test that pins them goes red when someone adds a
        test file. So they are range-checked, and deliberately NOT compared to a literal - including
        here, where the value is whatever this tree happens to hold."""
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        _, payload = self.json_of("--root", str(self.root))
        for key in ("files_scanned", "caller_file_files", "anchored_walks"):
            self.assertIsInstance(payload[key], int, f"{key} is not an int")
            self.assertGreaterEqual(payload[key], 0, f"{key} went negative")
        self.assertEqual(payload["files_scanned"], 1, "one planted file is a CLOSED count, not a population")
        self.assertEqual(payload["caller_file_files"], 1, "the planted file declares the anchor")
        self.assertEqual(payload["anchored_walks"], 1, "the planted walk is the only anchored one")

    def test_findings_are_sorted_so_the_output_does_not_depend_on_directory_order(self) -> None:
        """The original pipes through `Sort-Object`, and the reason is real: the enumerator's order is a
        filesystem property, and an unsorted report reorders itself between machines.

        The fixture is built so the sort is OBSERVABLE, which took two attempts. The obvious version -
        three files in `tests/A`, `tests/M`, `tests/Z` - proves nothing, because the scanner already
        walks the tree in sorted order, so an unsorted accumulator comes out sorted anyway and a
        mutation removing the sort survives. The discriminating case is TWO findings in ONE file, on
        lines 4 and 10: sorted as strings `":10:" < ":4:"`, so the sorted report reverses them while
        insertion order keeps them. Line 9 would not do - `":4:" < ":9:"`, so the two orders coincide
        and the test proves nothing, which is what the first version of it did. A line number is not a
        sortable quantity and the report sorts it as one anyway - which is deterministic, and is the
        property being pinned.
        """
        two_walks = fixture(
            '        var a = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..");\n'
            '\n'
            '\n'
            '\n'
            '\n'
            '\n'
            '        var b = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..");\n')
        write_tree(self.root, {"tests/P/A.cs": two_walks})
        _, payload = self.json_of("--root", str(self.root))
        problems = payload["problems"]
        self.assertEqual(len(problems), 2, f"expected one finding per walk: {problems}")
        self.assertEqual(problems, sorted(problems), f"unsorted findings: {problems}")
        self.assertIn(":10: ", problems[0],
                      f"string order puts \":10:\" before \":4:\", so the sorted report REVERSES the "
                      f"file's line order: {problems}")
        self.assertIn(":4: ", problems[1], f"and the line-4 walk comes second: {problems}")

    def test_findings_across_files_are_ordered_by_path(self) -> None:
        """The other half of the same property, and the one a reader actually relies on: `tests/F/G`
        precedes `tests/P` regardless of which file was read first."""
        write_tree(self.root, {"tests/Z/A.cs": D2_ESCAPES, "tests/A/B.cs": D2_MISSES,
                               "tests/M/C.cs": D2_ESCAPES})
        _, payload = self.json_of("--root", str(self.root))
        problems = payload["problems"]
        self.assertEqual(problems, sorted(problems), f"unsorted findings: {problems}")
        self.assertEqual([p.split(":", 1)[0] for p in problems],
                         ["tests/A/B.cs", "tests/M/C.cs", "tests/Z/A.cs"])

    def test_two_runs_of_the_same_tree_are_byte_identical(self) -> None:
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES, "tests/Q/B.cs": D2_MISSES})
        first = self.invoke("--root", str(self.root), "--json")
        second = self.invoke("--root", str(self.root), "--json")
        self.assertEqual(first, second, "the tool is not deterministic on an unchanged tree")


class Refusals(TreeCase):
    def test_a_missing_tests_directory_is_refused_by_name(self) -> None:
        code, out, err = self.invoke("--root", str(self.root))
        # 64, not 1 - see VerdictAndEnvelope.test_a_refusal_is_its_OWN_verdict_not_a_finding. What this
        # test actually requires is "not clean", and 64 says that more precisely than 1 did, because 1
        # is the code a bad tree gets. The message below is the requirement; the code is the guard's.
        self.assertEqual(code, 64, f"a missing tests/ must not read as a clean tree\n{out}\n{err}")
        self.assertIn("TESTS-MISSING", out + err)
        self.assertIn("REFUSED", out + err, "the refusal is not announced as one")

    def test_the_refusal_names_the_path_it_looked_for(self) -> None:
        """Fail CLOSED and say what was missing. A refusal that only says "refused" is a refusal the
        next reader has to reproduce by hand to understand."""
        _, payload = self.json_of("--root", str(self.root))
        self.assertEqual(payload["reason"], "TESTS-MISSING")
        self.assertIn("tests", payload["detail"], "the detail does not name the directory it wanted")

    def test_an_empty_tests_directory_is_OK_not_refused(self) -> None:
        """The KNOWN answer. A repository with no test sources has no bad walks; refusing would make the
        guard unusable in a tree that has not grown tests yet, and would train people to pass a
        different `--root` to get past it."""
        (self.root / "tests").mkdir()
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 0)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["files_scanned"], 0)


class TheTwoRules(TreeCase):
    def assert_kind(self, files: dict, kind: str) -> list[str]:
        write_tree(self.root, files)
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1, f"expected a finding, got {payload['verdict']}")
        problems = payload["problems"]
        for problem in problems:
            self.assertRegex(problem, PROBLEM_SHAPE,
                             f"finding is not `path:line: kind - prose`: {problem!r}")
            self.assertIn(kind, problem)
        return problems

    def assert_quiet(self, files: dict) -> None:
        write_tree(self.root, files)
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 0, f"a walk that lands correctly was reported: {payload['problems']}")
        self.assertEqual(payload["problems"], [])

    # --- walk-escapes-root, with the correct-walk twin beside every positive --------------------
    def test_depth2_three_steps_escapes(self) -> None:
        self.assert_kind({"tests/P/A.cs": D2_ESCAPES}, ESCAPES)

    def test_depth2_two_steps_lands_ON_the_root_and_is_quiet(self) -> None:
        """The twin. If this fired, the guard would be refusing the ~30 correct walks in this tree - a
        rule with no discriminating power is a rule that gets deleted."""
        self.assert_quiet({"tests/P/A.cs": D2_LANDS_ON_ROOT})

    def test_the_depth_is_a_rule_not_a_constant(self) -> None:
        """The same three-step walk from a DEEPER file is correct, because that file is deeper. A guard
        that counted steps alone would flag one and miss the other."""
        deep = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..");\n')
        self.assert_quiet({"tests/F/G/A.cs": deep})
        escape = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..", "..");\n')
        self.assert_kind({"tests/F/G/A.cs": escape}, ESCAPES)

    # --- walk-misses-root, and the CLOSED root vocabulary -----------------------------------------
    def test_depth2_one_step_then_data_misses_root(self) -> None:
        self.assert_kind({"tests/P/A.cs": D2_MISSES}, MISSES)

    def test_every_name_in_the_root_vocabulary_is_a_root(self) -> None:
        for segment in sorted(ROOT_SEGMENTS):
            with self.subTest(segment=segment):
                body = fixture(f'        var r = Path.Combine(Path.GetDirectoryName(here), "..", '
                               f'"{segment}");\n')
                self.assert_kind({"tests/P/A.cs": body}, MISSES)

    def test_a_name_outside_the_vocabulary_is_not_a_root(self) -> None:
        """The negative twin of the loop above. A vocabulary test that only checked its members would
        pass a guard that accepted every string."""
        for segment in ("fixtures", "TestData", "data2", "", "src"):
            with self.subTest(segment=segment):
                body = fixture(f'        var r = Path.Combine(Path.GetDirectoryName(here), "..", '
                               f'"{segment}");\n')
                self.assert_quiet({"tests/P/A.cs": body})

    def test_the_vocabulary_folds_case(self) -> None:
        """`-contains` folds, so `DATA` names a Keepverse root exactly as `data` does. Case sensitivity
        would let the same defect through by capitalisation, which is the cheapest possible evasion."""
        body = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), "..", "DATA");\n')
        self.assert_kind({"tests/P/A.cs": body}, MISSES)

    def test_a_walk_that_lands_ON_the_root_may_name_a_root(self) -> None:
        """`Path.Combine(dir, "..", "..", "data")` from depth 2 IS the repository's data directory. The
        finding is about a path that does not exist, not about the word `data`."""
        body = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), "..", "..", "data");\n')
        self.assert_quiet({"tests/P/A.cs": body})

    def test_a_trailing_step_AFTER_a_name_is_not_a_walk_step(self) -> None:
        """`("..", "fixtures", "..")` is ONE step: a later non-`..` argument ends the walk. A counter
        that added every `".."` would report two and invent an escape out of a correct combine."""
        body = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), "..", "fixtures", "..");\n')
        self.assert_quiet({"tests/P/A.cs": body})

    # --- what the guard is not the business of ----------------------------------------------------
    def test_a_file_with_no_anchor_is_skipped(self) -> None:
        """No `[CallerFilePath]` means no statically knowable depth. Guessing one is what produced the
        CS-F1 false confidence in the first place."""
        self.assert_quiet({"tests/P/A.cs": NO_ANCHOR})

    def test_a_commented_out_walk_is_not_reported(self) -> None:
        """Comments are stripped; string literals are KEPT, because the `".."` and the attribute are the
        subject. If literals were erased no walk would resolve, and if comments were kept a disabled
        walk would be reported."""
        body = ('using System.IO;\npublic sealed class T {\n'
                '    public void Run([CallerFilePath] string here = "") {\n'
                '        // var r = Path.Combine(Path.GetDirectoryName(here), "..", "..", "..");\n'
                '        var s = here;\n    }\n}\n')
        self.assert_quiet({"tests/P/A.cs": body})

    def test_a_walk_planted_in_a_string_literal_is_UNREACHABLE_and_says_so(self) -> None:
        """A stated bound, pinned in both directions.

        The original's comment says a walk written inside a string literal "is therefore scanned",
        because its stripper keeps literals. That is half true and the half that is false is the part
        that matters: C# escapes a `".."` written in a literal as `\\"..\\"`, and the escaped form never
        matches the `".."` argument token, so the walk is invisible. The test asserts the bound so the
        day somebody widens the argument matcher they have to decide what it now catches - and so no
        reader inherits the overreach from the retired comment.
        """
        body = ('using System.IO;\npublic sealed class T {\n'
                '    public void Run([CallerFilePath] string here = "") {\n'
                '        var template = "var r = Path.Combine(Path.GetDirectoryName(here), '
                '\\"..\\", \\"..\\", \\"..\\");";\n'
                '        var s = template;\n    }\n}\n')
        self.assert_quiet({"tests/P/A.cs": body})
        # ... and literals really ARE kept, which is the half that is true and the reason the rule
        # works at all: the segment name is a literal, and D2_MISSES is the proof.
        self.assert_kind({"tests/P/A.cs": D2_MISSES}, MISSES)

    def test_build_output_is_not_scanned(self) -> None:
        """A checked-in `obj/` is not source, and a generated walk in it is not a defect to fix by hand.

        The planted `obj/` file is itself a REAL violation: it is the same three-step escape placed at
        `tests/obj/Debug/Gen.cs`, whose directory `tests/obj/Debug` is three segments deep, so three
        steps land ON the root and a correct guard reports nothing for it. An earlier version of this
        fixture used that walk verbatim and therefore proved nothing - a mutation that stopped skipping
        `obj/` survived, because the file it would have started scanning was not a violation in the
        first place. The assertion is a count, so both halves have to be real to move it.
        """
        obj_escape = fixture('        var r = Path.Combine(Path.GetDirectoryName(here), '
                             '"..", "..", "..", "..");\n')
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES, "tests/obj/Debug/Gen.cs": obj_escape})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1)
        self.assertEqual(len(payload["problems"]), 1,
                         f"the obj/ walk was scanned ({payload['problems']}) or the tests/ walk was not")
        self.assertNotIn("obj", payload["problems"][0])

    def test_two_files_are_reported_independently(self) -> None:
        """Two different shapes in one run, each file resolved at its OWN depth. `assert_kind` is
        deliberately shape-only, and this test does not assume report ORDER: the findings are sorted, so
        `tests/F/G/A.cs` precedes `tests/P/A.cs` and indexing into the list would be testing the sort
        twice instead of the resolution."""
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES, "tests/F/G/A.cs": D2_MISSES})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1)
        problems = payload["problems"]
        self.assertEqual(len(problems), 2, f"expected one finding per file: {problems}")
        for problem in problems:
            self.assertRegex(problem, PROBLEM_SHAPE, f"wrong shape: {problem!r}")
        by_file = {problem.split(":", 1)[0]: problem for problem in problems}
        self.assertEqual(set(by_file), {"tests/P/A.cs", "tests/F/G/A.cs"},
                         f"one file reported twice or not at all: {by_file}")
        self.assertIn(ESCAPES, by_file["tests/P/A.cs"])
        self.assertIn(MISSES, by_file["tests/F/G/A.cs"])
        # The depth-3 file needed its own arithmetic: one step from depth 3 lands 2 below the root, and
        # naming `data` there is the misses finding rather than the escape the depth-2 file reports.
        self.assertIn("depth 3", by_file["tests/F/G/A.cs"])

    def test_a_finding_reports_the_line_the_walk_is_on(self) -> None:
        """The offset arithmetic is load-bearing: the stripper blanks comments while PRESERVING layout,
        so a leading comment must not shift the reported line. That is why the port uses the shared
        stripper's `line_of` and not `raw.count("\\n")`."""
        body = ("using System.IO;\npublic sealed class T {\n"
                "    public void Run([CallerFilePath] string here = \"\") {\n"
                "        // a comment that must not move the reported line\n"
                "        /* and a block comment too */\n"
                "        var root = Path.Combine(Path.GetDirectoryName(here), \"..\", \"..\", \"..\");\n"
                "    }\n}\n")
        write_tree(self.root, {"tests/P/A.cs": body})
        _, payload = self.json_of("--root", str(self.root))
        self.assertEqual(len(payload["problems"]), 1)
        self.assertIn(":6: ", payload["problems"][0],
                      f"the walk is on line 6 of the file, reported: {payload['problems'][0]}")


class TheFixpoint(TreeCase):
    """`MAX_PASSES` is the one constant in this guard whose value is not self-evident, and pinning it
    took a fixture shape that does not occur naturally in C# - which is the point of this class."""

    def test_a_definition_that_appears_AFTER_its_use_needs_a_second_and_third_pass(self) -> None:
        """The ONLY thing the fixpoint buys, pinned with the shape that makes it observable.

        Getting this right took three attempts, and each earlier version was a test that could not fail:

          * An IN-ORDER chain needs one pass, because C# forbids using a local before declaring it inside
            one method, so every realistic chain resolves in the first pass.
          * A ONE-HOP out-of-order chain needs two - and still does not discriminate, because the
            REPORTING loop is a separate full pass over every call, so a single fixpoint pass has already
            populated the walk table by the time findings are computed.
          * A TWO-HOP out-of-order chain needs three: the consumer is the FIRST call in the file, its
            producer is the SECOND, and that producer's own source is the THIRD. One pass fixes the last
            link only, and the first two calls are never revisited until the next one.

        The shape is deliberately unusual: it is here to make the fixpoint's cost observable, not because
        the tree is written that way. `test_an_IN_ORDER_chain_resolves_and_is_reported` below is the one
        that describes real code.
        """
        out_of_order = (
            "using System.IO;\npublic sealed class T {\n"
            "    public static string hopC;\n"
            "    public static string hopB;\n"
            "    public void Run([CallerFilePath] string here = \"\") {\n"
            "        var root = Path.Combine(hopC, \"..\", \"..\", \"..\");\n"
            "    }\n"
            "    public void Mid([CallerFilePath] string here = \"\") {\n"
            "        hopC = Path.Combine(hopB, \"..\");\n"
            "    }\n"
            "    public void Low([CallerFilePath] string here = \"\") {\n"
            "        var dir = Path.GetDirectoryName(here);\n"
            "        hopB = Path.Combine(dir, \"..\");\n"
            "    }\n}\n")
        write_tree(self.root, {"tests/P/A.cs": out_of_order})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1,
                         "the three-hop out-of-order chain was not resolved, so the fixpoint did not "
                         f"run far enough: {payload}")
        self.assertIn(ESCAPES, payload["problems"][0])

    def test_an_IN_ORDER_chain_resolves_and_is_reported(self) -> None:
        """The control for the test above, and the shape real code has: three chained walks, each
        consuming the previous, all declared before use. The third escapes, which is the point - a chain
        nobody checks is a chain nobody resolves."""
        chained = fixture(
            '        var a = Path.Combine(Path.GetDirectoryName(here), "..");\n'
            '        var b = Path.Combine(a, "..");\n'
            '        var c = Path.Combine(b, "..");\n')
        write_tree(self.root, {"tests/P/A.cs": chained})
        code, payload = self.json_of("--root", str(self.root))
        self.assertEqual(code, 1, f"the three-hop chain was not resolved at all: {payload}")


class OpacityIsAsymmetric(TreeCase):
    """A local assigned more than once is a walk-up loop's cursor. The two halves below differ by ONE
    line of reassignment, so a change that moved both would fail exactly one of them."""

    def _verdict(self, body: str) -> tuple[int, list[str]]:
        write_tree(self.root, {"tests/P/A.cs": body})
        code, payload = self.json_of("--root", str(self.root))
        return code, payload["problems"]

    def test_an_OPAQUE_anchor_still_reports_an_ESCAPE(self) -> None:
        code, problems = self._verdict(D2_IMPURE_ESCAPE)
        self.assertEqual(code, 1, "opacity must not mute an escape")
        self.assertIn(ESCAPES, problems[0])

    def test_an_OPAQUE_anchor_SUPPRESSES_a_misses_root(self) -> None:
        code, problems = self._verdict(D2_IMPURE_MISSES)
        self.assertEqual(code, 0,
                         "'it did not reach depth 0' is a claim about a stable anchor, and a loop "
                         f"cursor cannot support it; reported anyway: {problems}")

    def test_the_stable_twin_of_the_suppressed_case_does_report(self) -> None:
        """Without this, the test above would also pass for a guard that never reported a misses-root at
        all - which is the single most likely way for this rule to die quietly."""
        code, problems = self._verdict(D2_PURE_MISSES)
        self.assertEqual(code, 1, f"the stable twin of a suppressed case stayed silent: {problems}")
        self.assertIn(MISSES, problems[0])

    def test_the_two_halves_differ_by_exactly_one_ASSIGNMENT(self) -> None:
        """The suppression is the reassignment flag, not an accident of these two fixtures' depths. The
        stable twin is asserted to be the opaque fixture with ONE line removed, and that line is named -
        so a later edit that quietly rebalanced the pair fails here instead of making the comparison
        look controlled while measuring something else."""
        impure = D2_IMPURE_MISSES.splitlines()
        pure = D2_PURE_MISSES.splitlines()
        self.assertEqual(len(impure) - len(pure), 1,
                         f"expected one extra line in the opaque twin: {len(impure)} vs {len(pure)}")
        removed = [line for line in impure if line not in pure]
        self.assertEqual(len(removed), 1, f"more than one line differs: {removed}")
        self.assertRegex(removed[0].strip(), r"^dir\s*=\s*Path\.GetDirectoryName\(dir\);$",
                         f"the removed line is not the reassignment that makes the anchor opaque: "
                         f"{removed[0]!r}")


class PathHandling(TreeCase):
    def test_a_relative_root_and_an_absolute_root_agree(self) -> None:
        """The guard runner, the C# tests and a human all spell `--root` differently. A difference here
        would be invisible until a CI step passed a relative path and reported a different set of
        findings from the local run that "passed"."""
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        _, by_absolute = self.json_of("--root", str(self.root))
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", ".", "--json"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(self.root))
        self.assertEqual(proc.returncode, 1, "a relative --root did not even reach the same verdict")
        by_relative = json.loads(proc.stdout[proc.stdout.index("{"):])
        self.assertEqual(sorted(by_relative["problems"]), sorted(by_absolute["problems"]),
                         "a relative --root reported a different finding than an absolute one")
        self.assertEqual(by_relative["verdict"], by_absolute["verdict"])

    def test_the_default_root_is_the_repository_the_tool_ships_in(self) -> None:
        """The guard is invoked with no arguments by the guard runner, so the default has to be the
        repository it lives in - not the process's working directory, which is whatever the caller
        happened to be standing in."""
        proc = subprocess.run([sys.executable, str(SCRIPT), "--json"], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT, cwd=str(Path(tempfile.gettempdir())))
        payload = json.loads(proc.stdout[proc.stdout.index("{"):])
        self.assertIn(payload["verdict"], ("OK", "FAIL"))
        self.assertGreater(payload["files_scanned"], 0,
                           "the default root found no test sources, so it is not the repository")

    def test_a_root_that_is_not_a_repository_directory_is_refused_not_crashed(self) -> None:
        """A path that exists but is a FILE. The refusal path must hold for every bad `--root`, not
        just the one this test happens to think of."""
        target = self.root / "not-a-tree"
        target.write_text("x", encoding="utf-8")
        code, out, err = self.invoke("--root", str(target), "--json")
        self.assertEqual(code, 64)   # a refusal, not a finding - see the note in VerdictAndEnvelope
        payload = json.loads(out[out.index("{"):])
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertIn("TESTS-MISSING", payload["reason"])


class StreamDiscipline(TreeCase):
    def test_findings_go_to_stderr_and_the_report_to_stdout(self) -> None:
        """The convention every port in this program follows, and the reason the C# assertions had to
        move to `stdout + stderr` twelve times: a caller reading stdout alone must see the verdict and
        the readings, and must not have to separate prose from findings to do it."""
        write_tree(self.root, {"tests/P/A.cs": D2_ESCAPES})
        code, out, err = self.invoke("--root", str(self.root))
        self.assertEqual(code, 1)
        self.assertIn("TEST CONTENT-ROOT GUARD FAILED", err, "the failure banner is not on stderr")
        self.assertNotIn("walk-escapes-root", out, "a finding leaked onto stdout")
        self.assertIn("scanned", out, "the readings are missing from stdout")
        self.assertIn("KeepverseRoots.cs", err, "the remediation hint is not on stderr")

    def test_a_clean_run_puts_everything_on_stdout(self) -> None:
        write_tree(self.root, {"tests/P/A.cs": D2_LANDS_ON_ROOT})
        code, out, err = self.invoke("--root", str(self.root))
        self.assertEqual(code, 0)
        self.assertEqual(err, "", "a clean run wrote to stderr")
        self.assertIn("TEST CONTENT-ROOT GUARD OK", out)
        self.assertIn("scanned", out)

    def test_the_readings_are_printed_on_the_success_path_too(self) -> None:
        """A guard that silently resolved zero walks looks exactly like a clean tree. The counts are
        how a reader tells the two apart, so they may not be conditional on a finding."""
        write_tree(self.root, {"tests/P/A.cs": D2_LANDS_ON_ROOT})
        _, out, _err = self.invoke("--root", str(self.root))
        self.assertRegex(out, r"scanned \d+ tests/\*\*/\*\.cs file\(s\)")


if __name__ == "__main__":
    unittest.main()
