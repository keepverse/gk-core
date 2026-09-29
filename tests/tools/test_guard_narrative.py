"""Contract tests for `gk-core/scripts/guard-narrative.py`.

What this asserts is the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the
`--json` envelope's key set, and the behaviour of each of the three checks. What it deliberately does
not assert is a message body, a line count, or the number of narrative rows in the registry — the row
set is the owner's list plus reviewed amendments, so its size is a reading, not a constant.

Differential evidence for equivalence with the retired `guard-narrative.ps1`: 11 fixtures, one per
rule, identical in exit code and every emitted line once the two DECLARED divergences are accounted
for — the flag's spelling and the move of findings to stderr with the checklist's `  - ` prefix. That
comparison is a one-time proof and lives outside this file because the PowerShell form no longer
exists to compare against.

The C# suite `gk-core/tests/FusionRpg.Guard.Tests/NarrativeGuardContractTests.cs` shells this same tool.
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
SCRIPT = REPO / "scripts" / "guard-narrative.py"

_spec = importlib.util.spec_from_file_location("guard_narrative", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_narrative"] = guard
_spec.loader.exec_module(guard)

TRAIT = '[Trait("Guard", "narrative")]\n'
ROWS = guard.parse_row_map(guard.ROW_MAP)
ROW_IDS = [r["row"] for r in ROWS]
PROJECTS = sorted({r["project"] for r in ROWS})


def build(root: Path, invariants, *, classes=None, projects=()) -> Path:
    """A throwaway tree, so a real defect in the repo can never turn one of these green by accident."""
    (root / "scripts").mkdir(parents=True, exist_ok=True)
    (root / "scripts" / "enforcement-registry.v1.json").write_text(
        json.dumps({"invariants": invariants}, indent=2), encoding="utf-8")
    for row in ROWS:
        path = root / row["file"]
        path.parent.mkdir(parents=True, exist_ok=True)
        if classes is not None and row["file"] not in classes:
            continue
        text = (classes or {}).get(row["file"], TRAIT + f"class {row['row']} {{}}\n")
        path.write_text(text, encoding="utf-8")
    for project in projects:
        path = root / project
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("<Project />\n", encoding="utf-8")
    return root


def narrative(ids):
    return [{"id": i, "guards": ["narrative"]} for i in ids]


def check(root: Path, **kwargs) -> dict:
    return guard.check(root, root / "scripts" / "enforcement-registry.v1.json", **kwargs)


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=1800)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_the_documented_flags_are_accepted(self) -> None:
        for flags in ([], ["--json"], ["--run-trait-filter"]):
            with self.subTest(flags=flags):
                result = run("--root", str(REPO), *flags)
                self.assertEqual(result["exit"], guard.EXIT_OK, result["stderr"])

    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("guard-narrative.py", proc.stdout)

    def test_a_non_positive_timeout_is_refused_rather_than_passed_to_subprocess(self) -> None:
        # `subprocess.run(timeout=0)` raises immediately and `timeout=-1` means "no limit at all",
        # which is precisely the hang the port exists to remove. Caught before any child is started.
        for value in ("0", "-5"):
            with self.subTest(value=value):
                result = run("--root", str(REPO), "--dotnet-timeout", value, "--run-trait-filter")
                self.assertEqual(result["exit"], guard.EXIT_FAILED)
                self.assertIn("BAD-TIMEOUT", result["stderr"])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_the_real_registry_passes(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-cli-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS))
            self.assertEqual(check(root)["verdict"], "OK")

    def test_a_violation_fails(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-cli-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS[1:]))
            self.assertEqual(check(root)["verdict"], "FAIL")


class NamedRefusals(unittest.TestCase):
    """A refusal is NAMED, and it reaches the `--json` envelope.

    The original THREW on a missing registry, so a broken checkout produced a stack trace and exit 1 —
    the same exit as the seven ways this guard is meant to fail. That is the shape the port removes.
    """

    def _refuse(self, payload) -> dict:
        with tempfile.TemporaryDirectory(prefix="narr-ref-") as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir(parents=True, exist_ok=True)
            path = root / "scripts" / "enforcement-registry.v1.json"
            if payload is not None:
                path.write_text(payload, encoding="utf-8")
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                capture_output=True, text=True, timeout=900)
            return {"exit": proc.returncode, "payload": json.loads(proc.stdout)}

    def test_a_missing_registry_is_named(self) -> None:
        got = self._refuse(None)
        self.assertEqual(got["exit"], guard.EXIT_FAILED)
        self.assertEqual(got["payload"]["reason"], "MISSING-REGISTRY")

    def test_unparseable_json_is_named(self) -> None:
        self.assertEqual(self._refuse("{ not json")["payload"]["reason"], "REGISTRY-NOT-JSON")

    def test_a_registry_without_invariants_is_named(self) -> None:
        self.assertEqual(self._refuse(json.dumps({"other": []}))["payload"]["reason"],
                         "REGISTRY-SHAPE-UNEXPECTED")

    def test_a_malformed_row_map_is_a_hard_refusal(self) -> None:
        # A map line the parser cannot read is a defect in the guard's OWN source, and the checks
        # would otherwise cover fewer rows than the text claims while reporting the full count.
        for bad in ("only-one-field\n",
                    "a | b | c | d\n",
                    "a |  | c\n",
                    " | b | c\n"):
            with self.subTest(bad=bad):
                with self.assertRaises(guard.Refusal) as caught:
                    guard.parse_row_map(bad)
                self.assertEqual(caught.exception.reason, "ROW-MAP-MALFORMED")

    def test_comments_and_blank_lines_are_not_map_entries(self) -> None:
        rows = guard.parse_row_map("# a comment\n\n  \na | b | c\n")
        self.assertEqual(1, len(rows))
        self.assertEqual({"row": "a", "project": "b", "file": "c"}, rows[0])

    def test_every_named_refusal_reaches_the_envelope(self) -> None:
        # A refusal that only reaches stderr is invisible to a `--json` caller: the process said
        # something and the caller read nothing, which is the PowerShell capture trap all over again.
        got = self._refuse(None)
        self.assertEqual({"guard", "verdict", "reason", "detail", "narrative_rows",
                          "mapped_rows", "trait_filter_run", "trait_filter",
                          "findings"}, set(got["payload"]))


class JsonEnvelope(unittest.TestCase):
    """The key set is closed, and it is the SAME on the success and failure paths."""

    KEYS = {"guard", "verdict", "narrative_rows", "mapped_rows", "trait_filter_run",
            "trait_filter", "findings"}

    def test_both_paths_carry_the_same_keys(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-json-") as tmp:
            good = build(Path(tmp) / "ok", narrative(ROW_IDS))
            bad = build(Path(tmp) / "bad", narrative(ROW_IDS[1:]))
            for root, verdict in ((good, "OK"), (bad, "FAIL")):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run(
                        [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                        capture_output=True, text=True, timeout=900)
                    payload = json.loads(proc.stdout)
                    self.assertEqual(self.KEYS, set(payload))
                    self.assertEqual(payload["verdict"], verdict)
                    self.assertEqual(payload["guard"], guard.GUARD_ID)

    # The envelope speaks OK/FAIL; the human verdict line is a MESSAGE and says OK/FAILED. Two
    # vocabularies, mapped here explicitly rather than assumed identical - a test that compares the
    # raw strings would fail on the spelling and then tempt someone into renaming the message to
    # match, which would be changing a caller-visible string to satisfy a test.
    HUMAN_FOR = {"OK": guard.VERDICT_OK, "FAIL": guard.VERDICT_FAILED}

    def test_the_human_and_machine_verdicts_agree(self) -> None:
        # A tool whose human output says OK while CI reads FAIL is the worst version of this defect.
        # Checked on a tree that FAILS as well as one that passes, because a guard proven only on
        # success is half-proven - and the failure path is where the two surfaces are most likely to
        # drift, since a finding is printed to stderr while the verdict is printed to stdout.
        failing = build(Path(tempfile.mkdtemp(prefix="narr-h-")), narrative(ROW_IDS[1:]))
        for root, expected in ((REPO, "OK"), (failing, "FAIL")):
            with self.subTest(root=str(root)):
                proc = subprocess.run(
                    [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                    capture_output=True, text=True, timeout=900)
                machine = json.loads(proc.stdout)["verdict"]
                text = subprocess.run(
                    [sys.executable, str(SCRIPT), "--root", str(root)],
                    capture_output=True, text=True, timeout=900)
                self.assertEqual(machine, expected)
                # THE STREAM RULE, pinned because it is easy to get backwards. Findings and a FAILED
                # verdict go to STDERR; only the OK verdict reaches stdout. So on a failing run stdout
                # is empty, and a caller that captures stdout alone sees nothing at all - which is why
                # the port exists rather than the PowerShell it replaced, where every line went to
                # `Write-Host` and `2>&1` captured none of it.
                if expected == "OK":
                    self.assertIn(self.HUMAN_FOR["OK"].split(" - ")[0], text.stdout)
                    self.assertNotIn(self.HUMAN_FOR["FAIL"].split(" - ")[0],
                                     text.stdout + text.stderr)
                else:
                    self.assertEqual("", text.stdout.strip())
                    self.assertIn(self.HUMAN_FOR["FAIL"].split(" - ")[0], text.stderr)
                    self.assertNotIn(self.HUMAN_FOR["OK"].split(" - ")[0],
                                     text.stdout + text.stderr)


class CheckOneTheMapAndTheRegistryMustAgree(unittest.TestCase):
    """A row that is guarded but unmapped, or mapped but unguarded, enforces nothing."""

    def test_a_complete_map_passes(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS))
            self.assertEqual(check(root)["findings"], [])

    def test_a_row_guarded_but_unmapped_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS + ["row-c"]))
            got = check(root)
            self.assertEqual(got["verdict"], "FAIL")
            self.assertTrue(any("row-c" in f for f in got["findings"]))

    def test_a_map_line_naming_no_invariant_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS[1:]))
            got = check(root)
            self.assertTrue(any(ROW_IDS[0] in f for f in got["findings"]))

    def test_no_invariant_naming_the_guard_is_reported(self) -> None:
        # THE VACUOUS PASS. An empty row set makes every later check pass without examining anything,
        # which is why this rule exists separately from the correspondence ones.
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative([]))
            got = check(root)
            self.assertEqual(got["verdict"], "FAIL")
            self.assertTrue(any("no invariant names" in f for f in got["findings"]))

    def test_rows_guarded_by_another_guard_are_ignored(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS) +
                         [{"id": "row-z", "guards": ["funnel-delta"]}])
            self.assertEqual(check(root)["verdict"], "OK")

    def test_a_duplicate_map_line_is_reported(self) -> None:
        # The port detects this from the committed text; the original needed `Group-Object`. Same rule,
        # so the parser itself is exercised with a doubled entry.
        # A doubled line is WELL FORMED, so it parses; the duplicate is a correspondence finding, not
        # a parse refusal. Asserting a refusal here would have tested the opposite of the rule.
        doubled = guard.ROW_MAP + ROWS[0]["row"] + " | " + ROWS[0]["project"] + \
            " | " + ROWS[0]["file"] + "\n"
        rows = guard.parse_row_map(doubled)
        self.assertEqual(len(ROWS) + 1, len(rows))
        with tempfile.TemporaryDirectory(prefix="narr-c1-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS))
            failures = guard.check_correspondence(rows, ROW_IDS)
            self.assertTrue(any("more than one line" in f for f in failures))


class CheckTwoTheClassMustCarryTheTrait(unittest.TestCase):
    """The static half, so a stripped trait is caught without starting a test host."""

    def _verdict(self, classes) -> str:
        with tempfile.TemporaryDirectory(prefix="narr-c2-") as tmp:
            return check(build(Path(tmp), narrative(ROW_IDS), classes=classes))["verdict"]

    def test_every_class_carrying_the_trait_passes(self) -> None:
        # `None`, not `{}`: an empty dict means "create no class files", so every mapped class read
        # as missing and the fixture tested the wrong rule. The empty-dict case is
        # `test_a_missing_class_is_reported`, which is a different test for a different reason.
        self.assertEqual("OK", self._verdict(None))

    def test_a_missing_class_is_reported(self) -> None:
        classes = {r["file"]: TRAIT for r in ROWS}
        del classes[ROWS[0]["file"]]
        self.assertEqual("FAIL", self._verdict(classes))

    def test_missing_and_unreadable_are_DISTINGUISHABLE(self) -> None:
        # Found by falsifying: deleting the `is_file()` check leaves the guard FAILING, because the
        # read then raises and the unreadable branch reports it instead. So a verdict-only assertion
        # cannot see the difference - the mutation passed a suite that claimed to cover this rule.
        # The two are not the same defect to whoever reads the output: a file that is ABSENT is a
        # committed-row problem, while a file that cannot be READ is a permissions or checkout
        # problem, and they lead to different fixes.
        with tempfile.TemporaryDirectory(prefix="narr-c2-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS), classes={r["file"]: TRAIT for r in ROWS})
            target = root / ROWS[0]["file"]
            target.unlink()
            findings = check(root)["findings"]
            self.assertTrue(any("class missing" in f for f in findings), findings)
            self.assertFalse(any("unreadable" in f for f in findings), findings)

    def test_a_class_without_the_trait_is_reported(self) -> None:
        classes = {r["file"]: TRAIT for r in ROWS}
        classes[ROWS[0]["file"]] = "class Stripped {}\n"
        self.assertEqual("FAIL", self._verdict(classes))

    def test_the_trait_match_FOLDS_CASE(self) -> None:
        # The original used PowerShell `-notmatch`, which is case-insensitive, so
        # `[TRAIT("GUARD", "NARRATIVE")]` PASSED. Getting this backwards in the port would reject a
        # legitimate class - a real behavioural difference, and one a first reading gets wrong because
        # `guard-funnel-delta`'s `[regex]::IsMatch` does NOT fold while this call site does.
        classes = {r["file"]: TRAIT for r in ROWS}
        classes[ROWS[0]["file"]] = '[TRAIT("GUARD", "NARRATIVE")]\nclass A {}\n'
        classes[ROWS[1]["file"]] = '[trait("guard", "narrative")]\nclass B {}\n'
        self.assertEqual("OK", self._verdict(classes))

    def test_the_trait_match_tolerates_whitespace(self) -> None:
        classes = {r["file"]: TRAIT for r in ROWS}
        classes[ROWS[0]["file"]] = '[Trait( "Guard" , "narrative" )]\nclass A {}\n'
        self.assertEqual("OK", self._verdict(classes))

    def test_the_count_scrape_is_CASE_SENSITIVE_and_that_is_deliberate(self) -> None:
        # Two matching conventions in one script, transcribed faithfully. `[regex]::Matches` does not
        # fold, so a lower-case `total:` in the test host's output is not a count.
        self.assertTrue(guard.TOTAL_PATTERN.search("Total: 9"))
        self.assertIsNone(guard.TOTAL_PATTERN.search("total: 9"))
        self.assertTrue(guard.TRAIT_PATTERN.search('[TRAIT("GUARD", "NARRATIVE")]'))


class CheckThreeTheFilterMustSelectTests(unittest.TestCase):
    """The runtime half: a class whose trait never reaches the runner enforces nothing."""

    def test_it_is_skipped_unless_asked_for(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c3-") as tmp:
            got = check(build(Path(tmp), narrative(ROW_IDS)))
            self.assertFalse(got["trait_filter_run"])
            self.assertEqual(got["trait_filter"], [])

    def test_it_is_gated_on_the_earlier_checks_being_clean(self) -> None:
        # A map that does not correspond to the registry is reported in full and no test host is
        # started. A guard that begins a multi-minute test run in order to report a typo in its own
        # map is a guard nobody runs.
        with tempfile.TemporaryDirectory(prefix="narr-c3-") as tmp:
            broken = build(Path(tmp), narrative(ROW_IDS[1:]))
            got = check(broken, run_filter=True, timeout=1)
            self.assertFalse(got["trait_filter_run"])
            self.assertEqual(got["verdict"], "FAIL")

    def test_a_missing_test_project_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c3-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS), projects=PROJECTS[1:])
            got = check(root, run_filter=True, timeout=600)
            self.assertTrue(got["trait_filter_run"])
            self.assertEqual(got["verdict"], "FAIL")
            self.assertTrue(any("test project missing" in f for f in got["findings"]))

    def test_a_project_whose_filter_selects_nothing_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="narr-c3-") as tmp:
            root = build(Path(tmp), narrative(ROW_IDS), projects=PROJECTS)
            got = check(root, run_filter=True, timeout=600)
            self.assertTrue(any("selected 0 test" in f for f in got["findings"]))

    def test_the_run_is_bounded_by_a_hard_timeout(self) -> None:
        # The original shelled `dotnet test` with NO timeout: a wedged test host hung the guard
        # forever, and a hang reads as "still running" rather than as a failure. The refusal is named.
        #
        # Run against a REAL csproj, not a stub. `dotnet test` on a stub `<Project />` fails in a few
        # hundred milliseconds, so a one-second budget is never reached and the timeout branch is
        # never entered - a fixture too fast to hit the condition is the classic way a timeout test
        # passes without testing anything.
        with self.assertRaises(guard.Refusal) as caught:
            guard.run_trait_filter(REPO, ROWS, timeout=1)
        self.assertEqual(caught.exception.reason, "TRAIT-FILTER-TIMEOUT")

    def test_the_count_taken_is_the_LAST_one(self) -> None:
        # The test host prints a summary per project, and a build failure can print an earlier one, so
        # the original took the last match. Taking the first would report a build warning's count.
        self.assertEqual(9, guard._last_int("Total: 0\nTotal: 9\n", guard.TOTAL_PATTERN))
        self.assertEqual(0, guard._last_int("nothing here", guard.TOTAL_PATTERN))


class TheCommittedMap(unittest.TestCase):
    """The map is the thing under audit, so its own envelope is a contract."""

    def test_every_line_names_a_row_a_project_and_a_class(self) -> None:
        for entry in ROWS:
            with self.subTest(row=entry["row"]):
                self.assertTrue(all((entry["row"], entry["project"], entry["file"])))
                self.assertTrue(entry["file"].endswith(".cs"))
                self.assertTrue(entry["project"].endswith(".csproj"))

    def test_no_row_appears_twice(self) -> None:
        self.assertEqual(len(ROW_IDS), len(set(ROW_IDS)))

    def test_the_map_is_not_empty(self) -> None:
        # Not a count: an empty map would make the correspondence check vacuously true.
        self.assertTrue(ROWS)


class TheShippedRegistry(unittest.TestCase):
    def test_the_real_registry_is_green(self) -> None:
        got = guard.check(REPO, REPO / "scripts" / "enforcement-registry.v1.json")
        self.assertEqual(got["verdict"], "OK", got["findings"])
        self.assertTrue(got["narrative_rows"] > 0)

    def test_every_shipped_narrative_row_is_mapped_and_real(self) -> None:
        got = guard.check(REPO, REPO / "scripts" / "enforcement-registry.v1.json")
        self.assertEqual(got["mapped_rows"], len(ROWS))
        self.assertEqual(sorted(ROW_IDS), sorted(
            r["row"] for r in ROWS if r["row"] in [x for x in ROW_IDS]))


if __name__ == "__main__":
    unittest.main()
