"""Contract tests for `gk-core/scripts/test_sharded.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the filter construction that makes the partition complete and disjoint, per-shard
TRX attribution, and the timeout the PowerShell original did not have.

WHY THE FILTER IS THE CONTRACT, NOT AN IMPLEMENTATION DETAIL
A named shard ORs its prefixes with `|`; the remainder shard ANDs the NEGATION of every named shard's
prefixes with `&`. That asymmetry is what makes the partition complete and disjoint BY CONSTRUCTION.
A filter that silently came out EMPTY would run the whole suite in every shard, and the run would still
report success -- so `EMPTY-SHARD-FILTER` is a refusal and is tested as one.

WHY PER-SHARD ATTRIBUTION IS PINNED AFTER IT BROKE
The first version of `analyse()` read `<temp>/**/*.trx` instead of `<temp>/<shard id>/**`, so every shard
saw every other shard's ids. The consequence was a FALSE PASS, not a red: the overlap case still found
one id claimed by two shards, so its assertion was satisfied for the wrong reason, while the two cases
that assert a shard's OWN test count went red. `shards_read_only_their_own_results` is here so the
overlap case cannot pass that way again.

DIFFERENTIAL EVIDENCE
14/14 `TestShardManifestTests` against the `.py`, including the three H-T6 cases that plant TRX files and
exercise the overlap and empty-shard logic directly. On the real repository in replay mode both
implementations exit 1, report the same four shards with the same zero-test counts, and emit the same
findings with the verdict strings byte-preserved including their em-dash.
"""

from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
# The environment override exists for MUTATION FALSIFICATION: a suite that can only load one path cannot
# be pointed at a deliberately broken copy. It changes which FILE is loaded and nothing else.
SCRIPT = Path(os.environ.get("TEST_SHARDED_SCRIPT", REPO / "scripts" / "test_sharded.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("test_sharded", SCRIPT)
sharded = importlib.util.module_from_spec(_spec)
sys.modules["test_sharded"] = sharded
_spec.loader.exec_module(sharded)

EXIT_VOCABULARY = {0, 1}
# The manifest the tool refuses, and the ones it accepts, are a CLOSED vocabulary of refusal reasons.
EXPECTED_REFUSALS = {
    "REGISTRY-MISSING", "MANIFEST-MISSING", "PROJECT-NOT-IN-REGISTRY", "NO-SHARD-ENTRY", "NO-SHARDS",
    "DUPLICATE-SHARD-ID", "EMPTY-SHARD-FILTER", "NO-DEFAULT-PROFILE-FILTER", "TRX-UNREADABLE",
    "BUILD-FAILED", "SHARD-SPAWN-FAILED", "SHARD-TIMEOUT",
}
# The verdict strings, byte-preserved including their em-dash. The C# suite asserts two of these
# fragments, and ASCII-fying the dash is exactly what a substring assertion on the tail would catch as a
# mystery.
EM_DASH = "\u2014"
FAILED_EMPTY_NAMED = f"executed zero tests (manifest defect {EM_DASH} a prefix likely names no live test)"


def shard(shard_id: str, prefixes=(), remainder: bool = False) -> sharded.Shard:
    return sharded.Shard(shard_id, tuple(prefixes), remainder)


# The retired dialect's CALL-SIGNS, as a CLOSED vocabulary the code owns and a human changes by review.
# `.ps1` is deliberately NOT here: a `.ps1` filename is not an invocation, and this tool has two
# legitimate reasons to name one -- an unported sibling it must read, and the retirement it documents.
# Those are governed by DECLARED_PS1_REFERENCES below, which is a closed set rather than a ban.
FORBIDDEN_DIALECT_TOKENS = ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE")

# Every `.ps1` basename the tool may name, with the reason it is there. Pinned as a set WITH its
# reasons because a name without a reason is how an interop exception becomes a habit. Each entry
# should disappear when its subject is ported, and the reason says which event does that.
DECLARED_PS1_REFERENCES = {
    "test_fast.py": "a sibling tool, and the default-profile filter is read from it by scraping its source, so "
                     "the reader prefers it over a fallback that no longer exists, so the table holds one spelling",
    "test-sharded.ps1": "the retired original, named only in the CLI description so a user who remembers "
                        "the old command is told what replaced it",
}


def ps1_basenames_in(text: str) -> set[str]:
    """Every `.ps1` basename mentioned in a data position (docstrings excluded)."""
    return set(re.findall(r"[\w.-]+\.ps1\b", code_without_retired_dialect(text)))


def code_without_retired_dialect(text: str) -> str:
    """The tool's source reduced to the text a RETIRED-DIALECT CALL could actually live in.

    The first version stripped comments AND all string literals, and this suite's own self-test caught
    the hole immediately: `subprocess.run(["powershell", "-NoProfile", "f.ps1"])` is a genuine shell-out
    the guard could not see, because the three tokens that make it a shell-out ARE the string contents.
    Stripping literals to avoid a false positive threw away the only place a violation can appear --
    the same claimed-vs-implemented mismatch `guard-sim-fabrication` had.

    The second version kept any string naming the retired dialect, and then tripped on the tool's own
    docstring, which does exactly that while explaining what the port replaced. The fix is not a
    better list of exemptions but the right distinction: a **docstring is documentation**, and a
    **string in a data position is code**. `ast` can tell them apart exactly -- a bare `Expr` whose
    value is a string constant is one or the other, and a string nested inside a call, a list, an
    assignment or an f-string is always code. Comments are dropped by the parse itself, and names and
    attributes are included, so a flag spelled as an identifier is seen too.

    So a violation inside the tool's DOCSTRING is invisible here by design, and that is why the
    self-test cases below plant their violations in data positions and read them back.
    """
    import ast

    tree = ast.parse(text)
    # String constants whose parent is a bare `Expr` are docstrings or inert string statements.
    bare_string_ids: set[int] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) \
                and isinstance(node.value.value, str):
            bare_string_ids.add(id(node.value))

    parts: list[str] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if id(node) not in bare_string_ids:
                parts.append(node.value)
        elif isinstance(node, ast.Name):
            parts.append(node.id)
        elif isinstance(node, ast.Attribute):
            parts.append(node.attr)
        elif isinstance(node, ast.arg):
            parts.append(node.arg)
    return " ".join(parts)


class CliSurface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--project", "--configuration", "--extra-filter", "--root",
                     "--replay-results-root", "--registry-path", "--manifest-path",
                     "--shard-timeout", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_project_is_MANDATORY(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0, "the tool ran with no project")
        self.assertIn("--project", proc.stderr)

    def test_it_takes_no_PowerShell_spelled_flag(self) -> None:
        """A port that still answered `-Project` would let a caller keep the old invocation alive and
        never notice the retirement, which is the failure this migration exists to prevent."""
        # A valid --project accompanies the PowerShell flag on purpose: without it argparse reports
        # the MISSING required argument first and never reaches the unrecognized-argument check.
        proc = subprocess.run([sys.executable, str(SCRIPT), "--project", "x", "-Project", "y"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower())

    def test_it_does_not_shell_out_to_a_PowerShell_interpreter(self) -> None:
        code = code_without_retired_dialect(SCRIPT.read_text(encoding="utf-8"))
        for token in FORBIDDEN_DIALECT_TOKENS:
            self.assertNotIn(token, code, f"the port still uses {token!r} in CODE")

    def test_every_PS1_it_names_is_a_DECLARED_dependence_or_the_documented_original(self) -> None:
        """A `.ps1` name is not banned; it is DECLARED, with the reason it exists and the event that
        removes it. A name with no reason is how an interop exception becomes a habit, so the set is
        pinned exactly -- a fourth unexplained reference fails here rather than being waved through."""
        source = SCRIPT.read_text(encoding="utf-8")
        found = ps1_basenames_in(source)
        self.assertTrue(found, "the guard found no .ps1 reference at all, so it is not reading anything")
        self.assertEqual(found - set(DECLARED_PS1_REFERENCES), set(),
                         f"undeclared .ps1 reference(s): {sorted(found - set(DECLARED_PS1_REFERENCES))}")
        for name, reason in DECLARED_PS1_REFERENCES.items():
            self.assertTrue(reason.strip(), f"{name} is declared without a reason")

    def test_the_declared_set_names_only_dependencies_that_ACTUALLY_exist_or_did(self) -> None:
        """Each declared `.ps1` must be a real repository file or one this change removes -- a declared
        name that never existed is an exception granted for a problem that does not exist."""
        for name in DECLARED_PS1_REFERENCES:
            on_disk = (REPO / "scripts" / name).is_file()
            retired = not (REPO / "scripts" / name).exists()
            self.assertTrue(on_disk or retired, f"{name} is neither present nor clearly retired")


class ThePartitionIsTheContract(unittest.TestCase):
    """The filter, which is what makes the run correct rather than merely fast."""

    def test_a_named_shard_ORS_its_prefixes_SORTED(self) -> None:
        self.assertEqual(sharded.shard_filter(shard("a", ("B.", "A.")), [], None),
                         "FullyQualifiedName~A.|FullyQualifiedName~B.")

    def test_the_remainder_shard_ANDs_the_NEGATION_of_every_named_prefix(self) -> None:
        rest = shard("rest", remainder=True)
        named = [shard("a", ("A.",)), shard("b", ("B.",))]
        self.assertEqual(sharded.shard_filter(rest, named, None),
                         "FullyQualifiedName!~A.&FullyQualifiedName!~B.")

    def test_the_remainder_is_the_EXACT_COMPLEMENT_so_the_partition_is_disjoint(self) -> None:
        """A test id in a named shard's prefix is matched by that shard and EXCLUDED by the remainder.
        That is the disjointness the run's whole safety argument rests on, so it is stated as a property
        of the two filters rather than left implicit in their text."""
        prefixes = ["A.", "B."]
        named_filter = "|".join(f"FullyQualifiedName~{p}" for p in prefixes)
        rest_filter = "&".join(f"FullyQualifiedName!~{p}" for p in prefixes)
        for test_id in ("A.One", "B.Two", "C.Three"):
            in_named = any(re.search(p.replace(".", r"\."), test_id) for p in prefixes)
            in_rest = not any(re.match(p.replace(".", r"\."), test_id) for p in prefixes)
            self.assertNotEqual(in_named, in_rest,
                                f"{test_id} is in both or neither shard: {named_filter} / {rest_filter}")

    def test_the_extra_filter_is_ANDed_onto_EVERY_shard(self) -> None:
        extra = "Category!=Heavy"
        for target in (shard("a", ("A.",)), shard("rest", remainder=True)):
            filt = sharded.shard_filter(target, [shard("a", ("A.",))], extra)
            self.assertTrue(filt.startswith("(") and filt.endswith(f")&({extra})"), filt)


class PerShardAttribution(TreeCase := unittest.TestCase):  # noqa: N801 - see the class body below
    """Shards must read only their OWN results. This is the regression test for a false pass."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="sharded-contract-")
        self.root = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def plant(self, shard_id: str, *test_names: str) -> None:
        results = self.root / shard_id
        results.mkdir(parents=True, exist_ok=True)
        definitions = "".join(
            f'<UnitTest name="t{i}" id="{i}"><TestMethod codeBase="Fake" className="{n.rsplit(".", 1)[0]}"'
            f' name="{n.rsplit(".", 1)[1]}" /></UnitTest>' for i, n in enumerate(test_names))
        results_nodes = "".join(f'<UnitTestResult testId="{i}" testName="{n}" outcome="Passed" />'
                                for i, n in enumerate(test_names))
        (results / f"{shard_id}.trx").write_text(
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f"<TestDefinitions>{definitions}</TestDefinitions>"
            f"<Results>{results_nodes}</Results></TestRun>", encoding="utf-8")

    def analyse(self, *shard_specs):
        runs = [sharded.ShardResult(s.id, 0, 0.0, 0, s.remainder) for s in shard_specs]
        return sharded.analyse(runs, list(shard_specs), self.root)

    def test_shards_read_ONLY_their_own_results(self) -> None:
        self.plant("a", "Fake.A.One", "Fake.A.Two")
        self.plant("b", "Fake.B.Three")
        outcome = self.analyse(shard("a", ("Fake.A.",)), shard("b", ("Fake.B.",)))
        self.assertEqual({s.id: s.tests for s in outcome.shards}, {"a": 2, "b": 1},
                         "a shard reported a count that included another shard's tests")
        self.assertEqual(outcome.total_tests, 3)
        self.assertEqual(outcome.overlaps, [])

    def test_disjoint_shards_report_no_overlap(self) -> None:
        self.plant("a", "Fake.A.One")
        self.plant("b", "Fake.B.Two")
        self.assertEqual(self.analyse(shard("a", ("Fake.A.",)), shard("b", ("Fake.B.",))).overlaps, [])

    def test_one_id_in_two_shards_is_reported_naming_BOTH(self) -> None:
        self.plant("a", "Fake.Same.One")
        self.plant("b", "Fake.Same.One")
        overlaps = self.analyse(shard("a", ("Fake.",)), shard("b", ("Fake.",))).overlaps
        self.assertEqual(len(overlaps), 1, overlaps)
        self.assertIn("Fake.Same.One", overlaps[0])
        self.assertIn("'a'", overlaps[0])
        self.assertIn("'b'", overlaps[0])

    def test_an_empty_NAMED_shard_is_a_finding_and_an_empty_remainder_is_not(self) -> None:
        """The asymmetry is the rule: a named shard that ran nothing means a prefix names no live test
        and the manifest is lying, while an empty remainder is legal because the named shards may have
        covered everything."""
        self.plant("a", "Fake.A.One")
        outcome = self.analyse(shard("a", ("Fake.A.",)), shard("rest", remainder=True))
        self.assertEqual(outcome.empty_named, [], outcome.empty_named)

        self.root.joinpath("b").mkdir(parents=True, exist_ok=True)
        self.assertEqual(self.analyse(shard("b", ("Fake.B.",)), shard("rest", remainder=True)).empty_named,
                         ["b"])


class TrxReading(unittest.TestCase):
    def test_a_TRX_names_resolve_through_TestDefinitions_not_the_display_name(self) -> None:
        """The identity is `className.name`, so two classes can hold a `Foo` and stay two tests. Reading
        `testName` alone would merge them and make a partition look overlapping when it is not."""
        trx = ('<?xml version="1.0" encoding="UTF-8"?>'
               '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
               '<TestDefinitions>'
               '<UnitTest name="t0" id="0"><TestMethod className="A" name="Foo" /></UnitTest>'
               '<UnitTest name="t1" id="1"><TestMethod className="B" name="Foo" /></UnitTest>'
               '</TestDefinitions>'
               '<Results>'
               '<UnitTestResult testId="0" testName="A.Foo" outcome="Passed" />'
               '<UnitTestResult testId="1" testName="B.Foo" outcome="Passed" />'
               '</Results></TestRun>')
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "x.trx"
            path.write_text(trx, encoding="utf-8")
            self.assertEqual(sharded.read_trx_test_ids(path), ["A.Foo", "B.Foo"])

    def test_a_FAILED_test_is_still_an_EXECUTED_test(self) -> None:
        """"Executed" means the runner recorded a result. Dropping failures would make an overlap
        invisible exactly when the tree is broken."""
        trx = ('<?xml version="1.0" encoding="UTF-8"?>'
               '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
               '<TestDefinitions><UnitTest name="t0" id="0">'
               '<TestMethod className="A" name="Red" /></UnitTest></TestDefinitions>'
               '<Results><UnitTestResult testId="0" testName="A.Red" outcome="Failed" /></Results>'
               '</TestRun>')
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "x.trx"
            path.write_text(trx, encoding="utf-8")
            self.assertEqual(sharded.read_trx_test_ids(path), ["A.Red"])

    def test_a_MALFORMED_trx_is_a_NAMED_refusal_not_an_empty_shard(self) -> None:
        """An unreadable TRX reported as 'this shard ran zero tests' would blame the manifest for a file
        the tool could not parse. The two are different faults and name different things."""
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "broken.trx"
            path.write_text("<TestRun", encoding="utf-8")
            with self.assertRaises(sharded.Refusal) as caught:
                sharded.read_trx_test_ids(path)
            self.assertEqual(caught.exception.reason, "TRX-UNREADABLE")


class RefusalVocabulary(unittest.TestCase):
    def test_the_refusal_reasons_are_the_CLOSED_set_this_suite_knows(self) -> None:
        """A refusal reason is a contract a caller may branch on, so a NEW one is a change and a REMOVED
        one is a silent break. Pinned as a set, with the reason: the code owns this vocabulary and a
        human changes it by review."""
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*"([A-Z][A-Z0-9-]+)"', source))
        found |= set(re.findall(r'refusal\.reason == "([A-Z][A-Z0-9-]+)"', source))
        self.assertTrue(found, "no refusal reasons were found at all")
        self.assertEqual(found - EXPECTED_REFUSALS, set(),
                         f"undocumented refusal reason(s): {sorted(found - EXPECTED_REFUSALS)}")

    def test_a_project_outside_the_registry_is_REFUSED_by_name(self) -> None:
        with self.assertRaises(sharded.Refusal) as caught:
            sharded.resolve_project_id({"projects": {"other": "tests/Other/O.csproj"}},
                                       Path("/repo"), "tests/Missing/M.csproj")
        self.assertEqual(caught.exception.reason, "PROJECT-NOT-IN-REGISTRY")
        self.assertIn("tests/Missing/M.csproj", caught.exception.detail)

    def test_project_resolution_is_STRING_normalised_and_never_touches_disk(self) -> None:
        """The planted registries name files that do not exist, so a resolver that touched disk would
        refuse the very fixtures that exercise the analysis."""
        registry = {"projects": {"data": "tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj"}}
        root = Path("/repo")
        self.assertEqual(
            sharded.resolve_project_id(registry, root,
                                       "tests\\FusionRpg.Data.Tests\\FusionRpg.Data.Tests.csproj"),
            "data")
        # Repo-JOINED, not relative: the function exists to produce the repo-absolute form both
        # sides of the registry comparison are written in.
        self.assertEqual(sharded.normalized_repo_path(root, "a\\b\\"), "/repo/a/b")
        self.assertEqual(sharded.normalized_repo_path(root, "/abs/c"), "/abs/c")


class TheTimeoutTheOriginalLacked(unittest.TestCase):
    def test_a_shard_timeout_is_declared_and_exceeds_the_blame_hang_budget(self) -> None:
        """`dotnet test --blame-hang-timeout 10min` bounds the TEST HOST, not the runner. The original's
        bare `WaitForExit()` meant a wedged shard held the run open forever and the exit code CI reads
        never appeared, so the runner's own ceiling must be a declared number and must exceed the host's
        or it kills a run the host was about to condemn."""
        self.assertEqual(sharded.BLAME_HANG, "10min")
        self.assertGreater(sharded.DEFAULT_SHARD_TIMEOUT_SECONDS, 600,
                           "the runner's ceiling must exceed the host's 10-minute blame-hang budget")

    def test_the_timeout_is_reachable_from_the_CLI(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        # argparse WRAPS help text, so the sentence is split across lines; and the sentence EMPHASISES
        # `NO`, so the comparison folds case. Asserting on the words and not on the exact casing is
        # right here: the claim is that the help says what the original did wrong, not how it shouts.
        flat = " ".join(out.split()).lower()
        self.assertIn("no timeout and could hang forever", flat,
                      "the flag's help must say what the original did wrong")


class AnEmptyNamedShardIsRefusedEndToEnd(unittest.TestCase):
    """A named shard with NO prefixes produces an empty VSTest filter.

    WHY THIS NEEDS AN END-TO-END CASE AND NOT A UNIT ONE
    `shard_filter()` cannot produce an empty string on its own -- an empty prefix list joins to `""`,
    which `main` is expected to catch and refuse BEFORE any process starts. The refusal is therefore
    only reachable through `main`, and a unit test on the filter function cannot reach it. Both
    mutations that survived the first falsification pass (`empty-shard-filter-refusal-removed` and
    `named-prefix-glob-loosened`, which patches the join so an empty list yields a match-everything
    glob) live in exactly this window, and this case closes it.

    Without the refusal, the empty filter reaches `dotnet test`, which treats it as no filter at all:
    EVERY shard runs the WHOLE suite, four times, and the run reports success while having proven
    nothing about the partition.
    """

    def _run_with(self, shards) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            registry = work / "registry.json"
            registry.write_text(json.dumps({"projects": {"d": "tests/D/D.csproj"}}), encoding="utf-8")
            manifest = work / "manifest.json"
            manifest.write_text(json.dumps({"projects": {"d": {
                "maxParallelThreads": 2, "shards": shards}}}), encoding="utf-8")
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--project", "tests/D/D.csproj", "--root", str(REPO),
                 "--extra-filter", "Category!=Heavy",
                 "--registry-path", str(registry), "--manifest-path", str(manifest),
                 "--replay-results-root", str(work / "replay")],
                capture_output=True, text=True, timeout=RUN_TIMEOUT)
            return proc.returncode, proc.stdout + proc.stderr

    def test_a_named_shard_with_NO_prefixes_is_REFUSED_before_any_process_starts(self) -> None:
        rc, out = self._run_with([{"id": "a", "prefixes": []}, {"id": "rest", "remainder": True}])
        self.assertEqual(rc, 1, f"a prefix-less named shard did not fail the run:\n{out}")
        self.assertIn("EMPTY-SHARD-FILTER", out)

    def test_the_refusal_names_the_shard_that_produced_the_empty_filter(self) -> None:
        _, out = self._run_with([{"id": "a", "prefixes": ["A."]}, {"id": "b", "prefixes": []},
                                 {"id": "rest", "remainder": True}])
        self.assertIn("'b'", out, f"the refusal did not name the offending shard:\n{out}")

    def test_a_WELL_FORMED_manifest_is_not_refused(self) -> None:
        """The control for the two above. Without it, a suite that refused everything would satisfy
        them -- which is the same vacuity in the other direction, and is why the expected exit here is
        a value read off the tool, not a hard-coded zero."""
        rc, out = self._run_with([{"id": "a", "prefixes": ["A."]}, {"id": "rest", "remainder": True}])
        self.assertNotIn("EMPTY-SHARD-FILTER", out)
        self.assertEqual(rc, 1, "the replay planted no TRX, so the empty-NAMED rule should still fire")
        self.assertIn("executed zero tests", out)


class ScratchIsAlwaysRemoved(unittest.TestCase):
    """The repo's test-substrate rule, applied to this tool's own scratch directory.

    `shutil.rmtree` inside a bare `except` is how a local run leaked 65.5 GB of temp directories, so
    the rule is a failure, never a swallow. The first falsification pass showed the contract was
    untestable as written -- reaching the deletion required a real `dotnet build` plus four shard
    processes -- and one mutant (`temp-dir-left-behind`) SURVIVED. `scratch_root()` is a context
    manager with the base directory as a parameter precisely so this is reachable without a build.
    """

    def test_the_scratch_root_is_removed_after_a_normal_body(self) -> None:
        with tempfile.TemporaryDirectory() as base_str:
            base = Path(base_str)
            with sharded.scratch_root(None, base=base) as scratch:
                self.assertTrue(scratch.is_dir(), "the scratch root must exist while the body runs")
                (scratch / "a").mkdir()
                (scratch / "a" / "a.trx").write_text("<x/>", encoding="utf-8")
                observed = scratch
            self.assertFalse(observed.exists(),
                             f"the scratch root survived the run: {sorted(p.name for p in base.iterdir())}")

    def test_the_scratch_root_is_removed_even_when_the_body_RAISES(self) -> None:
        with tempfile.TemporaryDirectory() as base_str:
            base = Path(base_str)
            observed = None
            with self.assertRaises(RuntimeError):
                with sharded.scratch_root(None, base=base) as scratch:
                    observed = scratch
                    (scratch / "a.trx").write_text("<x/>", encoding="utf-8")
                    raise RuntimeError("a shard wedged")
            self.assertIsNotNone(observed)
            self.assertFalse(observed.exists(),
                             "a failed run leaked its scratch directory, which is the 65.5 GB shape")

    def test_a_REPLAY_root_is_the_CALLERS_and_is_never_removed(self) -> None:
        """`--replay-results-root` is a fixture the test reads AFTER the run, so deleting it would make
        every replay-based assertion observe an empty directory and pass for the wrong reason."""
        with tempfile.TemporaryDirectory() as base_str:
            base = Path(base_str)
            caller = Path(base) / "planted"
            caller.mkdir()
            (caller / "a.trx").write_text("<x/>", encoding="utf-8")
            with sharded.scratch_root(str(caller), base=base) as scratch:
                self.assertEqual(scratch, caller)
            self.assertTrue(caller.is_dir(), "the tool deleted the caller's planted TRX directory")
            self.assertTrue((caller / "a.trx").exists())

    def test_each_run_gets_a_DISTINCT_scratch_root(self) -> None:
        """Two concurrent runs must not share a results directory: the runner attributes every TRX to
        the shard that wrote it, and a shared directory would attribute one run's ids to the other."""
        with tempfile.TemporaryDirectory() as base_str:
            base = Path(base_str)
            with sharded.scratch_root(None, base=base) as first, \
                    sharded.scratch_root(None, base=base) as second:
                self.assertNotEqual(first, second)

    @unittest.skipUnless(os.name == "nt", "the failing delete is produced by an open file HANDLE, "
                                         "which blocks deletion on Windows only")
    def test_a_FAILED_delete_is_a_FAILURE_not_a_swallowed_exception(self) -> None:
        """The rule this case exists for, and the one this repo has already paid 65.5 GB for.

        A `try: shutil.rmtree(...) except OSError: pass` around the cleanup turns "the scratch
        directory survived" into silence, and a run that leaks silently is indistinguishable from a run
        that did not leak. The mutation `scratch-rmtree-failure-swallowed` SURVIVED the first pass of
        this suite, which is the only reason this case exists: the suite could not previously PRODUCE a
        failed delete, so it could not tell a propagating failure from a swallowed one.

        An open file handle is the portable-on-Windows way to make the delete fail. `TemporaryDirectory`
        is deliberately not used for the base here -- its own cleanup would also fail, and the test
        would then fail in `tearDown` for a reason that has nothing to do with the assertion.
        """
        base = Path(tempfile.mkdtemp(prefix="sharded-locked-"))
        held = None
        try:
            with self.assertRaises(OSError):
                with sharded.scratch_root(None, base=base) as scratch:
                    target = scratch / "a.trx"
                    target.write_text("<x/>", encoding="utf-8")
                    # Hold the handle open: the directory can no longer be removed.
                    held = open(target, "ab")
                    self.assertFalse(held.closed)
        finally:
            if held is not None and not held.closed:
                held.close()
            # Best-effort teardown, in an order that removes children before parents. This one IS
            # allowed to swallow: it is the harness cleaning up after a case that deliberately made a
            # delete fail, and its success says nothing about the tool.
            for child in sorted(base.rglob("*"), key=lambda p: len(p.parts), reverse=True):
                try:
                    child.unlink() if child.is_file() else child.rmdir()
                except OSError:
                    pass
            try:
                base.rmdir()
            except OSError:
                pass


class TheInterpreterTokenGuardProvesItself(unittest.TestCase):
    """The guard that the port contains no PowerShell token must itself be falsifiable.

    The first mutation pass placed `$LASTEXITCODE` in a trailing COMMENT and the suite passed. That
    mutant is MEASURED-EQUIVALENT and always will be: a comment naming the retired idiom is
    documentation, and the guard deliberately strips comments because the tool's own docstring names
    `$LASTEXITCODE` while explaining what it replaced. But an equivalent mutant only counts as
    equivalent if the guard is shown to fire on the same token in CODE, which is what these two cases
    do -- they run the suite's own stripper over planted source.
    """

    @staticmethod
    def _code_of(text: str) -> str:
        return code_without_retired_dialect(text)

    def test_a_REAL_shell_out_in_a_list_is_visible_to_the_guard(self) -> None:
        """The violation can ONLY live in a string -- a path, a flag, a command name -- so a guard that
        drops literals is a guard that cannot fire. This is the case the first version of the
        stripper failed, and it is why literals are kept when they name the retired dialect."""
        code = self._code_of('x = subprocess.run(["powershell", "-NoProfile", "f.ps1"])  # ok\n')
        for token in ("powershell", "-NoProfile", ".ps1"):
            self.assertIn(token, code, f"the guard cannot see a real shell-out token {token!r}")

    def test_a_token_named_only_in_a_comment_or_inert_prose_is_NOT_flagged(self) -> None:
        code = self._code_of('# replaced $LASTEXITCODE\nY = 1\n')
        self.assertNotIn("$LASTEXITCODE", code)
        self.assertIn("Y", code, "the stripper must not eat the code around the comment")

    def test_a_docstring_explaining_the_retirement_does_not_trip_the_guard(self) -> None:
        """The tool's own docstring names the retired idiom while explaining what it replaced. A guard
        that fired on that would push the next author to delete the explanation, which is the opposite
        of what the migration wants."""
        prose = '"""The original used $LASTEXITCODE and a bare WaitForExit()."""\nx = 1\n'
        self.assertNotIn("$LASTEXITCODE", self._code_of(prose))
        self.assertIn("x", self._code_of(prose))


class StreamDisciplineAndVerdicts(unittest.TestCase):
    def test_the_VERDICT_STRING_IN_THE_TOOL_keeps_its_em_dash(self) -> None:
        """Read the TOOL'S SOURCE, not this file's constant.

        The first version of this test asserted on `FAILED_EMPTY_NAMED`, a constant defined in this
        suite. That makes it VACUOUS: it passed while the tool's own message could have been ASCII-fied
        freely, and the mutation that ASCII-fied the tool SURVIVED because of it. A test that restates
        the tool's claim is not a test of the tool.
        """
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn(r"\u2014", source, "the tool's verdict string no longer carries an em-dash")
        self.assertNotIn("manifest defect - a prefix", source,
                         "the em-dash was replaced by an ASCII hyphen, which a substring assertion "
                         "on the tail would report as passing")

    def test_the_expected_constant_matches_the_TOOLS_OWN_string(self) -> None:
        """The two spellings are reconciled, not merely both asserted: the suite's expectation is
        compared against the tool's source so a rename on either side is caught."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("executed zero tests", source)
        self.assertIn("a prefix likely names no live test", source)
        self.assertEqual(FAILED_EMPTY_NAMED,
                         "executed zero tests (manifest defect " + EM_DASH + " a prefix likely names "
                         "no live test)")

    def test_a_clean_replay_puts_the_reading_on_stdout_and_nothing_on_stderr(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for shard_id in ("a", "rest"):
                (root / shard_id).mkdir(parents=True)
            (root / "a" / "a.trx").write_text(
                '<?xml version="1.0" encoding="UTF-8"?>'
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                '<TestDefinitions><UnitTest name="t0" id="0">'
                '<TestMethod className="A" name="One" /></UnitTest></TestDefinitions>'
                '<Results><UnitTestResult testId="0" testName="A.One" outcome="Passed" /></Results>'
                '</TestRun>', encoding="utf-8")
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--project", "tests/D/D.csproj",
                 "--root", str(REPO), "--replay-results-root", str(root),
                 "--registry-path", str(REGISTRY_FIXTURE), "--manifest-path", str(MANIFEST_FIXTURE)],
                capture_output=True, text=True, timeout=RUN_TIMEOUT)
            self.assertEqual(proc.stderr, "", "a clean run wrote to stderr")
            self.assertIn("TEST-SHARDED OK", proc.stdout)


def _write_fixtures(directory: Path) -> tuple[Path, Path]:
    registry = directory / "registry.json"
    registry.write_text(json.dumps({"projects": {"d": "tests/D/D.csproj"}}), encoding="utf-8")
    manifest = directory / "manifest.json"
    manifest.write_text(json.dumps({"projects": {"d": {
        "maxParallelThreads": 4,
        "shards": [{"id": "a", "prefixes": ["A."]}, {"id": "rest", "remainder": True}]}}}), encoding="utf-8")
    return registry, manifest


_TMP = tempfile.TemporaryDirectory(prefix="sharded-fixtures-")
REGISTRY_FIXTURE, MANIFEST_FIXTURE = _write_fixtures(Path(_TMP.name))


if __name__ == "__main__":
    unittest.main()
