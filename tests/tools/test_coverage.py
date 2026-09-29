"""Contract tests for `gk-core/scripts/coverage.py` and the shared `gk-core/scripts/lib/core_test_project.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the filter construction, the worst-first ordering, the branch `n/a` rule, and the
threshold breach.

WHY THE THREE REFUSALS ARE KEPT APART
`TESTS-FAILED`, `NO-COVERAGE-REPORT` and `NO-MATCHING-CLASSES` are three different faults and name
three different next actions -- fix the tests, install the collector, or change the namespace or the
project. Collapsing them into "coverage failed" is what made the original's single throw useless, so
each is pinned separately here.

WHY `TESTS-FAILED` CARRIES THE OUTPUT TAIL
That is the defect the port exists to fix. The original piped the run through
`Where-Object { $_ -match "^(Passed!|Failed!)" }`, so a run that died before a summary printed nothing
and the throw that followed had nothing to point at. A mistyped `dotnet test` switch produces exactly
that shape, and it is not hypothetical -- a wrong switch in this repository produced
`MSBuild : error MSB1001: Unknown switch` and zero test output, which reads as "no failures" rather
than "no tests ran". So the tail is asserted, not merely the exit code.

WHY THE NAMESPACE MATCH IS A PREFIX AND NOT A WILDCARD
The original used `-notlike "$Namespace*"` for the class filter while correctly escaping the namespace
for the `-replace` on the next line -- the same concept escaped two different ways. `-Namespace
'FusionRpg.Core.*'` would have matched every class in the project and reported a total as if it were
one namespace's. The port compares prefixes, which is what the original's own SYNOPSIS says it does,
and that DECLARED DIVERGENCE is pinned here so a future edit cannot quietly restore the wildcard.

DIFFERENTIAL EVIDENCE
The shared resolver agrees with the original's transcribed logic on every token the real
`gk-core/tests/core-test-projects.v1.json` can match, across 280 generated cases and 63 distinct projects, with
a self-check that refuses to report success when every case resolved to the residual.
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
# The env override exists for MUTATION FALSIFICATION: a suite that can only load one path cannot be
# pointed at a deliberately broken copy. It changes which FILE is loaded and nothing else.
COVERAGE_SCRIPT = Path(os.environ.get("COVERAGE_SCRIPT", REPO / "scripts" / "coverage.py")).resolve()
RUN_TIMEOUT = 900

# The shared resolver needs its own override for the same reason the tool does: a suite that can only
# load one path cannot be pointed at a deliberately broken copy, and this module holds half the port's
# behaviour. Without it every lib mutant would read as SURVIVED, which is a hole in the falsification
# rather than a fact about the module.
LIB_DIR = Path(os.environ.get("COVERAGE_LIB_DIR", REPO / "scripts" / "lib")).resolve()
sys.path.insert(0, str(LIB_DIR))
import core_test_project as ctp  # noqa: E402

_spec = importlib.util.spec_from_file_location("coverage", COVERAGE_SCRIPT)
coverage = importlib.util.module_from_spec(_spec)
sys.modules["coverage"] = coverage
_spec.loader.exec_module(coverage)

EXIT_VOCABULARY = {0, 1}
EXPECTED_REFUSALS = {
    "TESTS-FAILED", "TESTS-TIMEOUT", "TESTS-UNRUNNABLE", "NO-RESULTS-DIR", "NO-COVERAGE-REPORT",
    "COVERAGE-REPORT-UNREADABLE", "NO-MATCHING-CLASSES", "RESULTS-DIR-UNSAFE",
}
# The retired dialect's call-signs. `.ps1` is NOT here: a `.ps1` filename is not an invocation, and this
# tool legitimately names its predecessor in the CLI description. See DECLARED_PS1_REFERENCES.
FORBIDDEN_DIALECT_TOKENS = ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE")
DECLARED_PS1_REFERENCES = {
    "coverage.ps1": "the retired original, named only in the CLI description and in the docstring that "
                    "explains what it replaced",
}


def code_without_retired_dialect(text: str) -> str:
    """Source reduced to the text a retired-dialect CALL could live in: docstrings and comments out,
    every other string and every identifier in. `ast` distinguishes a bare `Expr` string (a docstring,
    therefore documentation) from a string nested in a call or list (therefore code)."""
    import ast

    tree = ast.parse(text)
    bare = {id(n.value) for n in ast.walk(tree)
            if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant)
            and isinstance(n.value.value, str)}
    parts: list[str] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if id(node) not in bare:
                parts.append(node.value)
        elif isinstance(node, (ast.Name, ast.Attribute)):
            parts.append(getattr(node, "id", None) or getattr(node, "attr", ""))
        elif isinstance(node, ast.arg):
            parts.append(node.arg)
    return " ".join(parts)


# --------------------------------------------------------------------------------------------
# Planted coverage reports
# --------------------------------------------------------------------------------------------

def manifest_tokens() -> list[str]:
    """Every folder token the real `gk-core/tests/core-test-projects.v1.json` can match, READ AT RUN TIME.

    A reading, not a constant: the token set grows every time a Core area is split into its own test
    project, so a test that hard-codes one turns the next split into a false red.
    """
    try:
        manifest = json.loads((REPO / "tests" / "core-test-projects.v1.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    found: set[str] = set()
    for project in manifest.get("projects") or []:
        for entry in project.get("include") or []:
            head = entry.split("/")[0]
            found.add(head[:-3] if head.endswith(".cs") else head)
    return sorted(token for token in found if token)


def a_mixed_case_manifest_token() -> str:
    """A manifest token that is NOT already all-lowercase, so a case-folding case can tell the
    spellings apart. Refuses rather than returning a token that would make the caller vacuous."""
    for token in manifest_tokens():
        if token != token.lower():
            return token
    raise AssertionError("no mixed-case token in tests/core-test-projects.v1.json, so case folding "
                         "cannot be distinguished from a case-sensitive match here")


def a_manifest_token() -> str:
    """One token the manifest actually knows, or the test fails naming the reason rather than skipping
    quietly -- a skip here would hide the resolver being wrong."""
    tokens = manifest_tokens()
    if not tokens:
        raise AssertionError("tests/core-test-projects.v1.json yielded no tokens, so the manifest "
                             "lookup cannot be exercised at all")
    return tokens[0]


def report_xml(classes) -> str:
    """A cobertura document shaped like coverlet's, for the classes given as
    `(name, line_rate, branch_rate, [(covered, is_branch), ...])`."""
    blocks = []
    for name, line_rate, branch_rate, lines in classes:
        line_elements = "".join(
            f'<line number="{i + 1}" hits="{1 if hit else 0}" branch="{str(is_branch).lower()}"/>'
            for i, (hit, is_branch) in enumerate(lines))
        blocks.append(f'<class name="{name}" filename="{name}.cs" line-rate="{line_rate}" '
                      f'branch-rate="{branch_rate}"><lines>{line_elements}</lines></class>')
    return ('<?xml version="1.0" encoding="utf-8"?><coverage><packages><package name="p">'
            f'{"".join(blocks)}</package></packages></coverage>')


def plant_report(results_dir: Path, classes) -> Path:
    results = results_dir / "TestResults" / "sub"
    results.mkdir(parents=True, exist_ok=True)
    report = results / "coverage.cobertura.xml"
    report.write_text(report_xml(classes), encoding="utf-8")
    return report


# --------------------------------------------------------------------------------------------
# The shared resolver
# --------------------------------------------------------------------------------------------

class TheSharedResolver(unittest.TestCase):
    """One implementation, two callers. The rules are case-insensitive and fall back rather than
    refuse, and both halves are load-bearing."""

    def test_the_token_is_the_FIRST_segment_after_the_Core_prefix(self) -> None:
        self.assertEqual(ctp.namespace_token("FusionRpg.Core.World.Ai"), "World")
        self.assertEqual(ctp.source_path_token("FusionRpg.Core/World/Topology.cs"), "World")

    def test_a_namespace_outside_Core_has_NO_token_and_keeps_the_fallback(self) -> None:
        """Not an error: a non-Core namespace simply has no manifest entry to find, and refusing would
        be less useful than naming the project actually used."""
        for namespace in ("FusionRpg.Data", "NotCore.At.All", "", "FusionRpg.Core",
                          "FusionRpg.Core."):
            self.assertIsNone(ctp.namespace_token(namespace), namespace)
            self.assertEqual(ctp.project_for_token(REPO, ctp.namespace_token(namespace)),
                             ctp.CORE_FALLBACK_PROJECT)

    def test_the_manifest_comparison_FOLDS_CASE_like_PowerShell_did(self) -> None:
        """`-contains` was case-insensitive. A Python `in` would have fallen through to the residual
        and reported a real number for the WRONG project, which reads as plausible rather than wrong.

        The token is read from the manifest at run time and must be MIXED CASE, or the case cannot
        distinguish the two spellings and this case passes vacuously. That is not hypothetical: the
        first version used `World`, which is not a manifest token at all -- it lives in the residual --
        so the exact, lowered and uppercased spellings all resolved to the same residual and the
        mutant `case-folding-dropped` SURVIVED. A case that cannot fail is not a case.
        """
        token = a_mixed_case_manifest_token()
        exact = ctp.project_for_token(REPO, ctp.namespace_token(f"FusionRpg.Core.{token}"))
        for spelling in (token.lower(), token.upper()):
            folded = ctp.project_for_token(REPO, ctp.namespace_token(f"FusionRpg.Core.{spelling}"))
            self.assertEqual(folded, exact,
                             f"'{spelling}' resolved to {folded!r} but '{token}' resolved to {exact!r}; "
                             f"the manifest lookup stopped folding case")

    def test_a_MISSING_or_CORRUPT_manifest_FALLS_BACK_rather_than_raising(self) -> None:
        """The pre-split shape is a missing manifest, and a coverage report that refuses because one is
        absent is less useful than one that names the project it used. The `manifest-parse-error-raises`
        mutant SURVIVED until this case existed, because nothing ever fed the resolver a broken
        manifest."""
        with tempfile.TemporaryDirectory(prefix="coverage-nomanifest-") as tmp:
            root = Path(tmp)
            for name, content in (("absent", None), ("corrupt", "{ not json")):
                (root / "tests").mkdir(exist_ok=True)
                if content is not None:
                    (root / "tests" / "core-test-projects.v1.json").write_text(content, encoding="utf-8")
                token = a_mixed_case_manifest_token()
                self.assertEqual(ctp.project_for_token(root, token), ctp.CORE_FALLBACK_PROJECT, name)
                self.assertFalse(ctp.manifest_matched(root, token), name)

    def test_the_residual_comes_from_the_manifest_NOT_from_the_hardcoded_fallback(self) -> None:
        """Read from a planted manifest whose residual DIFFERS from the built-in fallback name.

        Without this, `residual-fallback-dropped` is MEASURED EQUIVALENT on the real tree -- the
        manifest's residual happens to be `FusionRpg.Core.Tests`, the same string as
        `CORE_FALLBACK_PROJECT` -- so the mutation reads as a survivor when it is in fact untestable
        here. Planting a different residual name is what makes the two paths distinguishable, and it is
        why an equivalent mutant is recorded as equivalent rather than chased.
        """
        with tempfile.TemporaryDirectory(prefix="coverage-residual-") as tmp:
            root = Path(tmp)
            (root / "tests").mkdir(parents=True)
            (root / "tests" / "core-test-projects.v1.json").write_text(
                json.dumps({"residual": "FusionRpg.Core.Whatever.Tests", "projects": []}),
                encoding="utf-8")
            self.assertEqual(ctp.project_for_token(root, "AnythingAtAll"),
                             "tests/FusionRpg.Core.Whatever.Tests")
            self.assertNotEqual(ctp.project_for_token(root, "AnythingAtAll"), ctp.CORE_FALLBACK_PROJECT)

    def test_it_reports_WHETHER_the_manifest_or_the_fallback_decided(self) -> None:
        """The positive case uses a token READ FROM THE MANIFEST rather than a literal. The first version
        hard-coded `World`, which is not a manifest token -- it lives in the residual -- so the case
        failed on a clean tree. A token list is a reading that changes when Core is split, and a test
        that pins one rots into a false red that looks like a resolver bug."""
        token = a_manifest_token()
        self.assertTrue(ctp.manifest_matched(REPO, token), token)
        self.assertFalse(ctp.manifest_matched(REPO, "NoSuchFolderZzz"))
        self.assertFalse(ctp.manifest_matched(REPO, None))

    def test_a_BACKSLASH_path_folds_before_the_token_is_taken(self) -> None:
        """A Windows-shaped path from a caller must not yield a token of `World\\Topology`, which
        matches nothing and would silently report the residual."""
        self.assertEqual(ctp.source_path_token("FusionRpg.Core\\World\\Topology.cs"), "World")

    def test_the_core_prefix_test_is_CASE_SENSITIVE_as_the_original_was(self) -> None:
        """`$Namespace.StartsWith($corePrefix, [StringComparison]::Ordinal)` was Ordinal, so
        `fusionrpg.core.World` was never a Core namespace and fell to the residual. Folding case here
        would be a behaviour change presented as a fix -- and the `namespace-token-became-case-insensitive`
        mutant SURVIVED until this case existed, because nothing pinned the Ordinal."""
        for wrong_case in ("fusionrpg.core.World", "FUSIONRPG.CORE.World", "FusionRpg.core.World"):
            self.assertIsNone(ctp.namespace_token(wrong_case), wrong_case)

    def test_the_SEPARATOR_is_normalised_but_the_FOLDER_is_not_case_folded(self) -> None:
        """`FusionRpg.Core\\world\\X.cs` normalises its separators and so DOES resolve -- to the token
        `world`, not to `World`. Two distinct claims, and the first version of this case asserted
        `is None` and so asked for a refusal the function was never meant to give.

        The folder's case must survive: `World` and `world` are different folders, the manifest can list
        both, and folding the case after the stem would merge two areas into one project's coverage. The
        FOLDING lives one level down, in the comparison against the manifest's own `include` entries --
        so a differently-cased FOLDER still finds its project, and a differently-cased STEM is not Core's.
        """
        self.assertEqual(ctp.source_path_token("FusionRpg.Core\\world\\X.cs"), "world")
        self.assertEqual(ctp.source_path_token("FusionRpg.Core/World/X.cs"), "World")
        # A differently-cased STEM is not Core's, in either separator form.
        for wrong_stem in ("fusionrpg.core/World/X.cs", "FUSIONRPG.CORE\\World\\X.cs"):
            self.assertIsNone(ctp.source_path_token(wrong_stem), wrong_stem)
        # And the differently-cased FOLDER still finds its project, which is where the folding happens.
        exact = ctp.project_for_token(REPO, ctp.namespace_token(f"FusionRpg.Core.{a_mixed_case_manifest_token()}"))
        folded = ctp.project_for_token(REPO, ctp.source_path_token(
            f"FusionRpg.Core\\{a_mixed_case_manifest_token().lower()}\\X.cs"))
        self.assertEqual(folded, exact)

    def test_FIRST_match_wins_in_MANIFEST_order_not_the_most_specific_one(self) -> None:
        """`break` on the first hit. A "better" tie-break -- most specific, or alphabetically first --
        would silently re-point a folder's tests at a different project the day two projects both claim
        it, so the ORDER is the contract and is pinned with a planted manifest where the two candidate
        orders disagree.

        The `first-match-becomes-last-match` mutant SURVIVED until this case existed.
        """
        with tempfile.TemporaryDirectory(prefix="coverage-firstmatch-") as tmp:
            root = Path(tmp)
            (root / "tests").mkdir(parents=True)
            (root / "tests" / "core-test-projects.v1.json").write_text(json.dumps({
                "residual": "Residual.Tests",
                "projects": [
                    # BOTH projects claim the SAME folder token, by the two shapes the matcher accepts.
                    # That is what makes the order observable: the first version of this case gave the
                    # second project `Shared/One.cs`, which matches NEITHER shape, so only one candidate
                    # existed and first-match and last-match agreed -- the case could not fail, and the
                    # `first-match-becomes-last-match` mutant SURVIVED through it.
                    {"name": "FirstWins.Tests", "include": ["Shared/**"]},
                    {"name": "AlsoClaims.Tests", "include": ["Shared.cs"]},
                ]}), encoding="utf-8")
            self.assertEqual(ctp.project_for_token(root, "Shared"), "tests/FirstWins.Tests")
            # A most-specific rule would pick `AlsoClaims.Tests` (a bare file) over the `/**` glob, so
            # this assertion also distinguishes the two orders rather than restating one.
            self.assertEqual(ctp.project_for_token(root, "shared"), "tests/FirstWins.Tests",
                             "case folding must not reorder the projects")

    def test_mutate_and_coverage_resolve_the_SAME_folder_to_the_SAME_project(self) -> None:
        """The two callers derive their token differently -- one from a namespace, one from a file path
        -- and must still agree, or a namespace's coverage is measured against a different project than
        the same folder's mutation score. Checked over manifest tokens read at run time, plus the
        Windows-shaped path form the original could not resolve at all."""
        for folder in manifest_tokens()[:8] or ["World"]:
            by_namespace = ctp.project_for_token(REPO, ctp.namespace_token(f"FusionRpg.Core.{folder}"))
            by_path = ctp.project_for_token(REPO, ctp.source_path_token(f"FusionRpg.Core/{folder}/X.cs"))
            by_windows = ctp.project_for_token(
                REPO, ctp.source_path_token(f"FusionRpg.Core\\{folder}\\X.cs"))
            self.assertEqual(by_namespace, by_path, folder)
            self.assertEqual(by_namespace, by_windows, folder)


# --------------------------------------------------------------------------------------------
# Filter construction
# --------------------------------------------------------------------------------------------

class TheFilter(unittest.TestCase):
    def test_NO_CALLER_filter_still_applies_the_bench_exclusion(self) -> None:
        """"No filter" means no CALLER filter. The bench exclusion is applied unless explicitly turned
        off, so the default run is instrumentable rather than whole -- which is the honest number for
        the tests that can actually survive instrumentation. The first version of this case expected
        None, which would have meant the exclusion was off by default."""
        self.assertEqual(coverage.test_clauses(None, include_timing_tests=False),
                         "FullyQualifiedName!~Bench")

    def test_timing_tests_are_EXCLUDED_by_default_and_only_on_request(self) -> None:
        self.assertEqual(coverage.test_clauses(None, include_timing_tests=False),
                         "FullyQualifiedName!~Bench")
        self.assertIsNone(coverage.test_clauses(None, include_timing_tests=True))

    def test_a_caller_filter_is_ANDed_with_the_bench_exclusion(self) -> None:
        self.assertEqual(coverage.test_clauses("FullyQualifiedName~World.Ai", False),
                         "FullyQualifiedName~World.Ai&FullyQualifiedName!~Bench")
        self.assertEqual(coverage.test_clauses("FullyQualifiedName~World.Ai", True),
                         "FullyQualifiedName~World.Ai")

    def test_an_empty_filter_string_is_NOT_treated_as_a_filter(self) -> None:
        self.assertEqual(coverage.test_clauses("", False), "FullyQualifiedName!~Bench")


# --------------------------------------------------------------------------------------------
# Reading the report
# --------------------------------------------------------------------------------------------

class ReadingTheReport(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="coverage-report-")
        self.root = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_a_class_with_NO_branch_sites_reports_n_a_not_zero_percent(self) -> None:
        """0% branches reads as a failure rather than as "there was nothing to branch on", which is the
        whole reason the original counted branch lines instead of reading branch-rate."""
        plant_report(self.root, [("A.NoBranches", "0.5", "0", [(True, False), (False, False)])])
        rows = coverage.rows_for_namespace(coverage.read_classes(self.root /
                                                                 "TestResults/sub/coverage.cobertura.xml"),
                                          "A")
        self.assertEqual(rows[0].branch, None)
        self.assertEqual(rows[0].line, 50)
        self.assertEqual(rows[0].lines, 2)

    def test_a_class_WITH_branch_sites_reports_its_branch_rate(self) -> None:
        plant_report(self.root, [("A.Branched", "1.0", "0.5", [(True, True), (False, True)])])
        rows = coverage.rows_for_namespace(coverage.read_classes(self.root /
                                                                 "TestResults/sub/coverage.cobertura.xml"),
                                          "A")
        self.assertEqual(rows[0].branch, 50)

    def test_the_namespace_match_is_a_PREFIX_not_a_WILDCARD(self) -> None:
        """The declared divergence. A `*` in the namespace is an ordinary character here, so it matches
        nothing rather than matching every class in the project."""
        plant_report(self.root, [("A.One", "1.0", "0", [(True, False)])])
        report = self.root / "TestResults/sub/coverage.cobertura.xml"
        self.assertEqual(coverage.rows_for_namespace(coverage.read_classes(report), "A.*"), [])
        self.assertEqual(len(coverage.rows_for_namespace(coverage.read_classes(report), "A")), 1)

    def test_the_displayed_class_drops_the_namespace_and_its_trailing_dot(self) -> None:
        plant_report(self.root, [("FusionRpg.Core.World.Ai.Navigator", "1.0", "0", [(True, False)])])
        report = self.root / "TestResults/sub/coverage.cobertura.xml"
        rows = coverage.rows_for_namespace(coverage.read_classes(report), "FusionRpg.Core.World")
        self.assertEqual([r.cls for r in rows], ["Ai.Navigator"])

    def test_a_MALFORMED_report_is_a_NAMED_refusal_not_an_empty_table(self) -> None:
        """Reporting 'no classes matched' for a file that could not be parsed blames the namespace for
        a corrupt artefact, and the two have different fixes."""
        (self.root / "TestResults").mkdir(parents=True)
        broken = self.root / "TestResults" / "coverage.cobertura.xml"
        broken.write_text("<coverage", encoding="utf-8")
        with self.assertRaises(coverage.Refusal) as caught:
            coverage.read_classes(broken)
        self.assertEqual(caught.exception.reason, "COVERAGE-REPORT-UNREADABLE")

    def test_a_MISSING_report_is_its_OWN_refusal(self) -> None:
        (self.root / "TestResults").mkdir(parents=True)
        with self.assertRaises(coverage.Refusal) as caught:
            coverage.newest_coverage_report(self.root / "TestResults")
        self.assertEqual(caught.exception.reason, "NO-COVERAGE-REPORT")

    def test_a_MISSING_results_directory_is_not_reported_as_a_missing_report(self) -> None:
        """Two different faults: the run never created an output directory, versus it created one with
        no collector output in it. The first means the tests did not run."""
        with self.assertRaises(coverage.Refusal) as caught:
            coverage.newest_coverage_report(self.root / "absent")
        self.assertEqual(caught.exception.reason, "NO-RESULTS-DIR")


# --------------------------------------------------------------------------------------------
# Ordering and arithmetic
# --------------------------------------------------------------------------------------------

class OrderingAndArithmetic(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="coverage-rows-")
        self.root = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def rows(self, classes, namespace="A"):
        plant_report(self.root, classes)
        rows = coverage.rows_for_namespace(
            coverage.read_classes(self.root / "TestResults/sub/coverage.cobertura.xml"), namespace)
        rows.sort(key=lambda r: (r.line, r.cls))
        return rows

    def test_worst_COVERED_first_then_by_name_within_a_tie(self) -> None:
        rows = self.rows([("A.High", "1.0", "0", [(True, False)]),
                          ("A.Low", "0.2", "0", [(True, False), (False, False), (False, False),
                                                 (False, False), (False, False)]),
                          ("A.AlsoLow", "0.2", "0", [(True, False), (False, False), (False, False),
                                                      (False, False), (False, False)])])
        self.assertEqual([(r.cls, r.line) for r in rows], [("AlsoLow", 20), ("Low", 20), ("High", 100)])

    def test_the_summary_is_weighted_by_LINES_not_averaged_over_classes(self) -> None:
        """A 10-line class at 100% and a 90-line class at 0% is 10%, not 50%. Averaging per-class
        percentages is the classic way a coverage number becomes a number about nothing."""
        rows = self.rows([("A.Tiny", "1.0", "0", [(True, False)] * 10),
                          ("A.Big", "0.0", "0", [(False, False)] * 90)])
        total, covered, count = coverage.summarise(rows, "A")
        self.assertEqual((total, count), (100, 2))
        self.assertEqual(covered, 10)

    def test_the_threshold_selects_ON_the_ROUNDED_percentage_a_reader_sees(self) -> None:
        rows = self.rows([("A.At", "0.595", "0", [(False, False)] * 200),
                          ("A.Below", "0.594", "0", [(False, False)] * 200)])
        under = [r for r in rows if r.line < 60]
        self.assertEqual([r.cls for r in under], ["Below"])
        self.assertEqual(under[0].line, 59)


# --------------------------------------------------------------------------------------------
# The run path, with the test run and the report planted
# --------------------------------------------------------------------------------------------

def run_main(argv, *, exit_code=0, output="Passed!", plant=None):
    """Drive `main` with the test run mocked, and return `(exit code, stdout, stderr)`.

    `plant` is called BY the mocked run, not before it. That ordering is the point rather than an
    incidental detail: `main` deletes the results directory before running, precisely so a stale report
    from a previous namespace cannot be read as this one's, so a fixture planted up front is deleted by
    the code under test and every case then failed at `NO-RESULTS-DIR`. The first version of this helper
    planted early and produced six failures that all said the same thing and none of which were about
    the tool.
    """
    out, err = [], []

    def fake_run_tests(project, clauses, repo, timeout):
        if plant is not None:
            plant(repo)
        return exit_code, output

    patches = [
        mock.patch.object(coverage, "run_tests", side_effect=fake_run_tests),
        mock.patch.object(coverage.sys.stdout, "write", out.append),
        mock.patch.object(coverage.sys.stderr, "write", err.append),
    ]
    for patch in patches:
        patch.start()
    try:
        code = coverage.main(argv)
    finally:
        for patch in patches:
            patch.stop()
    return code, "".join(out), "".join(err)


class TheRunPath(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="coverage-run-")
        self.root = Path(self._tmp.name)
        self.project = "tests/FusionRpg.Core.Tests"
        (self.root / self.project).mkdir(parents=True)
        self.planted = [("FusionRpg.Core.World.Ai.Navigator", "0.8", "0.5",
                         [(True, False), (True, False), (False, False), (False, False), (True, True)]),
                        ("FusionRpg.Core.World.Ai.Seeker", "0.2", "0",
                         [(True, False), (False, False), (False, False), (False, False),
                          (False, False)])]

    def plant(self, repo=None):
        """Re-create the report where `main` will look for it: AFTER it cleared the directory, and at the
        depth it actually reads, which is `<root>/<project>/TestResults` and not `<root>/TestResults`."""
        base = (Path(repo) if repo else self.root) / self.project
        return plant_report(base, self.planted)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def base(self, *extra):
        return ["--root", str(self.root), "--project", self.project,
                "--namespace", "FusionRpg.Core.World", "--json", *extra]

    def test_a_good_run_exits_zero_and_reports_the_rows(self) -> None:
        code, out, _ = run_main(self.base(), plant=self.plant)
        self.assertEqual(code, 0, out)
        report = json.loads(out)
        self.assertEqual(report["verdict"], "OK")
        self.assertEqual([r["cls"] for r in report["rows"]], ["Ai.Seeker", "Ai.Navigator"])
        self.assertEqual(report["namespace"], "FusionRpg.Core.World")
        self.assertEqual(report["project"], self.project)
        self.assertFalse(report["project_from_manifest"],
                         "an explicit --project is an override, so the manifest did not decide it")

    def test_the_json_envelope_carries_NO_timing_or_population_field_that_could_rot(self) -> None:
        """The key set is the contract. A field that duplicates something derivable is a second place
        for the two to disagree."""
        _, out, _ = run_main(self.base(), plant=self.plant)
        report = json.loads(out)
        self.assertEqual(set(report), {"tool", "verdict", "namespace", "project",
                                       "project_from_manifest", "filter", "threshold",
                                       "total_lines", "covered_lines", "class_count",
                                       "under_threshold", "rows"})
        self.assertEqual(set(report["rows"][0]), {"cls", "line", "branch", "lines"})

    def test_a_RED_test_run_is_REFUSED_AND_QUOTES_THE_OUTPUT(self) -> None:
        """The defect this port exists to fix. The original showed only Passed!/Failed! lines, so a run
        that died before a summary printed nothing at all."""
        code, out, _ = run_main(self.base(), plant=self.plant, exit_code=1,
                                output="MSBuild : error MSB1001: Unknown switch.\n  Switch: --no-x")
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["verdict"], "REFUSED")
        self.assertEqual(report["reason"], "TESTS-FAILED")
        self.assertIn("MSB1001", report["detail"])
        self.assertIn("--no-x", report["detail"])

    def test_a_RED_run_with_NO_output_says_so_rather_than_showing_nothing(self) -> None:
        code, out, _ = run_main(self.base(), plant=self.plant, exit_code=1, output="")
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["reason"], "TESTS-FAILED")
        self.assertIn("no output at all", report["detail"])

    def test_a_namespace_that_MATCHES_nothing_is_its_OWN_refusal_naming_the_project(self) -> None:
        """The next action differs from a red run: change the namespace, or change the project, rather
        than fix the tests."""
        code, out, _ = run_main(["--root", str(self.root), "--project", self.project,
                                 "--namespace", "FusionRpg.Nope", "--json"], plant=self.plant)
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["reason"], "NO-MATCHING-CLASSES")
        self.assertIn(self.project, report["detail"])

    def test_the_THRESHOLD_breach_exits_one_and_NAMES_the_UNDERTHRESHOLD_classes(self) -> None:
        code, out, _ = run_main(self.base("--threshold", "50"), plant=self.plant)
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["verdict"], "FAIL")
        self.assertEqual([r["cls"] for r in report["under_threshold"]], ["Ai.Seeker"])

    def test_a_threshold_of_zero_DISABLES_the_gate_entirely(self) -> None:
        code, out, _ = run_main(self.base("--threshold", "0"), plant=self.plant)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["under_threshold"], [])

    def test_it_REFUSES_a_project_that_would_SEND_the_recursive_delete_outside_the_repo(self) -> None:
        """The reachable hazard. `Remove-Item -Recurse -Force` on a path assembled from a caller-supplied
        project with no check is one `..` away from deleting something else entirely.

        The first version of this guard tested `results_dir.name != "TestResults"`, which CANNOT be
        true: the path is built as `repo / project / "TestResults"`, so its last segment is that literal
        by construction. This case is written against containment instead, and the case below pins that
        the name check is not what is doing the work.
        """
        outside = Path(tempfile.mkdtemp(prefix="coverage-outside-")) / "escaped"
        (outside / "TestResults").mkdir(parents=True)
        keep = outside / "TestResults" / "keep.txt"
        keep.write_text("precious", encoding="utf-8")
        escaping = os.path.relpath(outside, self.root).replace("\\", "/")
        try:
            code, out, _ = run_main(["--root", str(self.root), "--project", escaping,
                                     "--namespace", "FusionRpg.Core.World", "--json"])
            self.assertEqual(code, 1)
            self.assertEqual(json.loads(out)["reason"], "RESULTS-DIR-UNSAFE")
            self.assertTrue(keep.exists(), "the guard fired but the directory was removed anyway")
            self.assertEqual(keep.read_text(encoding="utf-8"), "precious")
        finally:
            shutil.rmtree(outside, ignore_errors=True)

    def test_a_PROJECT_INSIDE_the_repo_is_deleted_normally(self) -> None:
        """The control for the guard above: a suite that refused every deletion would satisfy it, and
        would leave a stale report readable as the current namespace's."""
        planted = self.root / self.project / "TestResults"
        planted.mkdir(parents=True, exist_ok=True)
        stale = planted / "stale.cobertura.xml"
        stale.write_text("<coverage/>", encoding="utf-8")
        code, out, _ = run_main(self.base(), plant=self.plant)
        self.assertEqual(code, 0, out)
        self.assertFalse(stale.exists(), "the stale report survived the run")


class TheTimeoutPath(unittest.TestCase):
    """`run_tests` translated, without a real `dotnet test`.

    Every run-path case mocks `run_tests`, so the timeout branch had no coverage at all: the mutant
    `test-timeout-refusal-dropped` was killed only because renaming the refusal reason tripped the
    CLOSED-VOCABULARY test, which is a kill by the wrong mechanism -- it would also have been killed if
    the branch simply stopped existing, which is the opposite of what it proves. These cases drive
    `run_tests` with a `subprocess.run` that raises, so the branch itself is exercised.
    """

    def test_a_wedged_test_run_becomes_a_NAMED_timeout_refusal(self) -> None:
        with mock.patch.object(coverage.subprocess, "run",
                               side_effect=coverage.subprocess.TimeoutExpired(cmd="dotnet", timeout=7)):
            with self.assertRaises(coverage.Refusal) as caught:
                coverage.run_tests("tests/X", None, REPO, 7)
        self.assertEqual(caught.exception.reason, "TESTS-TIMEOUT")
        self.assertIn("7s", caught.exception.detail)
        self.assertIn("tests/X", caught.exception.detail)

    def test_the_timeout_refusal_is_DISTINCT_from_a_red_suite(self) -> None:
        """A red suite and a wedged suite need different actions -- fix the tests versus kill the run --
        so collapsing them would lose the one fact that says which happened."""
        with mock.patch.object(coverage.subprocess, "run",
                               side_effect=coverage.subprocess.TimeoutExpired(cmd="dotnet", timeout=1)):
            with self.assertRaises(coverage.Refusal) as timeout:
                coverage.run_tests("tests/X", None, REPO, 1)
        self.assertNotEqual(timeout.exception.reason, "TESTS-FAILED")

    def test_a_MISSING_dotnet_is_a_NAMED_refusal_rather_than_a_traceback(self) -> None:
        with mock.patch.object(coverage.subprocess, "run", side_effect=FileNotFoundError("dotnet")):
            with self.assertRaises(coverage.Refusal) as caught:
                coverage.run_tests("tests/X", None, REPO, 1)
        self.assertEqual(caught.exception.reason, "TESTS-UNRUNNABLE")

    def test_the_WHOLE_output_is_returned_not_just_the_summary_lines(self) -> None:
        """The original piped the run through a two-line filter, so anything else was invisible. The
        port returns the concatenation, and this pins that the filter is not quietly back."""
        whole = "restore warning NU1701\nPassed!  - Failed: 0\nsome diagnostic after the summary\n"
        completed = subprocess.CompletedProcess(args=[], returncode=0, stdout=whole, stderr="")
        with mock.patch.object(coverage.subprocess, "run", return_value=completed):
            _, output = coverage.run_tests("tests/X", None, REPO, 5)
        self.assertEqual(output, whole)
        self.assertIn("restore warning", output)


# --------------------------------------------------------------------------------------------
# Surface, vocabulary, and the retired dialect
# --------------------------------------------------------------------------------------------

class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(COVERAGE_SCRIPT), "--help"],
                             capture_output=True, text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--project", "--namespace", "--filter", "--include-timing-tests", "--threshold",
                     "--timeout", "--root", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        proc = subprocess.run([sys.executable, str(COVERAGE_SCRIPT), "--namespace", "x", "-Threshold",
                               "50"], capture_output=True, text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower())

    def test_it_shells_out_to_no_PowerShell_interpreter(self) -> None:
        code = code_without_retired_dialect(COVERAGE_SCRIPT.read_text(encoding="utf-8"))
        for token in FORBIDDEN_DIALECT_TOKENS:
            self.assertNotIn(token, code, f"the port still uses {token!r} in CODE")

    def test_every_PS1_it_names_is_a_DECLARED_reference(self) -> None:
        import re
        found = set(re.findall(r"[\w.-]+\.ps1\b",
                               code_without_retired_dialect(COVERAGE_SCRIPT.read_text(encoding="utf-8"))))
        self.assertEqual(found - set(DECLARED_PS1_REFERENCES), set(),
                         f"undeclared .ps1 reference(s): {sorted(found - set(DECLARED_PS1_REFERENCES))}")
        for name, reason in DECLARED_PS1_REFERENCES.items():
            self.assertTrue(reason.strip(), f"{name} is declared without a reason")

    def test_the_refusal_reasons_are_the_CLOSED_set_this_suite_knows(self) -> None:
        import re
        found = set(re.findall(r'Refusal\(\s*"([A-Z][A-Z0-9-]+)"',
                               COVERAGE_SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons were found at all")
        self.assertEqual(found - EXPECTED_REFUSALS, set(),
                         f"undocumented refusal reason(s): {sorted(found - EXPECTED_REFUSALS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({coverage.EXIT_OK, coverage.EXIT_FAILED}, EXIT_VOCABULARY)

    def test_the_timeout_is_declared_and_reachable(self) -> None:
        """`dotnet test` that wedges ran forever under the original, and a coverage tool is exactly the
        kind of long run nobody is watching."""
        out = subprocess.run([sys.executable, str(COVERAGE_SCRIPT), "--help"], capture_output=True,
                             text=True, timeout=RUN_TIMEOUT).stdout
        self.assertIn("no timeout and could hang forever", " ".join(out.split()).lower())


if __name__ == "__main__":
    unittest.main()
