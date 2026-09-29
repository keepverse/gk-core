"""Contract tests for `gk-core/scripts/guard-verification-boundaries.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the closed vocabularies, the dedupe order, the stdout/stderr split, and every rule
the guard enforces. It does not assert a message body or a count of rows.

WHAT MAKES THIS GUARD WORTH A CASE EACH
---------------------------------------
It is the only validator of a DATASET other tools select work from, so an untested rule is an untested
input to `verify-change.py`. The fixtures are therefore generated from the vocabularies the guard
declares rather than copied out of the registry, because a fixture copied from the data under test
cannot fail when the data changes - which is how six ports in this program produced a differential that
compared two identical missing-source refusals.

FOUR THINGS THE DIFFERENTIAL AND THE LIB FIX TAUGHT, PINNED HERE
---------------------------------------------------------------
  * **`Resolution.owners` is a tuple of plain boundary DICTS.** The original reads
    `$resolution.Owners[0].verificationId`; the Python twin keeps raw dicts. Reaching for `.id` is the
    same mistake as `.Owners` twice over, and a reading of the OTHER language's source is how both
    arrived. A test asserts the shape, not just the answer.
  * **`PytestDirs.test_dir`, not `.TestDir`.** The lib is snake_case; the PowerShell lib is PascalCase.
    Same class of error, so the field names are asserted against the real lib rather than spelled from
    memory.
  * **THE PORT MATCHES THE POWERSHELL ORIGINAL ONLY BECAUSE A LIVE BUG IN THE SHARED LIB WAS FIXED.**
    `pattern_match`'s final-segment wildcard branch lowercased the path and not the pattern, so every
    registry pattern with an uppercase final segment matched nothing in Python while matching in
    PowerShell - 14 rows, and two files falling to a wider fallback owner instead of to an error. The
    report is a READING, so this changed no verdict; the differential caught it because the report is
    where the numbers show. See `gk-core/tests/tools/test_lib_verification_boundaries.py`.
  * **The `/**/*.md` branch is CORRECT and I reported it as broken.** `.claude/**/*.md` is 15
    characters, so `[:-7]` is the first 8, `.claude/` — exactly right. My first fixture used
    `.claude/**/SKILL.md`, which is not one of the four legal pattern shapes, and I read the resulting
    failure as an off-by-one. Nothing here asserts an invented behaviour, and the markdown branch is
    exercised with a legal pattern plus a grammar assertion that the illegal one is refused by
    `valid_pattern_grammar`, which is the layer that actually owns that rule.

Differential evidence: all three flag combinations byte-identical on the real tree - the bare run and
`--skip-coverage-walk` at 1 line each, `--report` at 191 of 191 lines - plus fixture cases for the rules
the real registry happens not to violate. That comparison lives outside this file because the
PowerShell form no longer exists.
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
SCRIPT = REPO / "scripts" / "guard-verification-boundaries.py"

_spec = importlib.util.spec_from_file_location("guard_verification_boundaries", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_verification_boundaries"] = guard
_spec.loader.exec_module(guard)

RUN_TIMEOUT = 900

MINIMAL_BOUNDARY = {
    "id": "sample-owner",
    "kind": "owner",
    "paths": ["src/Sample/**"],
    "guards": ["session-boundary"],
    "level": "module",
}


class Fixture:
    """A throwaway repository holding the two registries, so each case states only what it varies."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="gvb-"))
        (self.root / "scripts").mkdir()
        (self.root / "src" / "Sample").mkdir(parents=True)
        (self.root / "tests").mkdir()
        (self.root / "docs" / "architecture").mkdir(parents=True)
        self.guard_ids = ["session-boundary"]
        self.doc = {
            "schemaVersion": guard.vb.ACCEPTED_VERIFICATION_SCHEMA_VERSION,
            "projects": {},
            "boundaries": [dict(MINIMAL_BOUNDARY)],
            "knownRed": [],
        }
        self.stub = None

    def project(self, pid: str = "sample") -> None:
        rel = f"src/Sample/{pid}.csproj"
        (self.root / rel).write_text("<Project/>\n", encoding="utf-8")
        self.doc["projects"][pid] = rel

    def enforcement(self, guards=None) -> None:
        ids = self.guard_ids if guards is None else guards
        payload = {"schemaVersion": 1,
                   "guards": {g: {"script": f"scripts/{g}.py", "tier": "ci"} for g in ids}}
        self._enforcement = payload

    def boundary(self, **over) -> None:
        self.doc["boundaries"] = [dict(MINIMAL_BOUNDARY, **over)]

    def known_red(self, **over) -> None:
        self.doc["knownRed"] = [over]

    def stub_register(self, text: str) -> None:
        self.stub = text

    def write(self) -> "Fixture":
        (self.root / "scripts" / "verification-boundaries.v1.json").write_text(
            json.dumps(self.doc, indent=2), encoding="utf-8")
        (self.root / "scripts" / "enforcement-registry.v1.json").write_text(
            json.dumps(getattr(self, "_enforcement",
                               {"schemaVersion": 1,
                                "guards": {g: {"script": f"scripts/{g}.py"}
                                           for g in self.guard_ids}}),
                       indent=2), encoding="utf-8")
        if self.stub is not None:
            (self.root / "docs" / "architecture" / "stub-register.md").write_text(
                self.stub, encoding="utf-8")
        return self

    def run(self, *args: str) -> dict:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root), *args],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}

    def json(self, *args: str) -> dict:
        result = self.run("--json", *args)
        return {**result, "payload": json.loads(result["stdout"])}

    def __enter__(self) -> "Fixture":
        return self

    def __exit__(self, *_exc) -> None:
        import shutil
        shutil.rmtree(self.root, ignore_errors=True)


def clean() -> Fixture:
    """A fixture that must pass, so a case can vary ONE thing and know the rest is sound."""
    f = Fixture()
    f.project()
    f.boundary(project="sample")
    return f


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement_and_every_flag(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=300)
        self.assertEqual(0, proc.returncode)
        self.assertIn("guard-verification-boundaries.py", proc.stdout)
        for flag in ("--root", "--report", "--skip-coverage-walk", "--json"):
            with self.subTest(flag=flag):
                self.assertIn(flag, proc.stdout)

    def test_the_real_repository_is_clean(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["problems"][:8])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_a_missing_registry_is_REFUSED_not_reported_as_a_finding(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gvb-gone-") as tmp:
            result = guard.main(["--root", tmp, "--json"])
        self.assertEqual(guard.EXIT_FAILED, result)
        payload = json.loads(subprocess.run(
            [sys.executable, str(SCRIPT), "--root", tmp, "--json"], capture_output=True,
            text=True, timeout=300).stdout)
        self.assertEqual("REGISTRY-MISSING", payload["reason"])
        self.assertEqual("FAILED", payload["verdict"])


class TheClosedVocabularies(unittest.TestCase):
    """Each is a vocabulary the code owns: adding a value is a reviewed change to the list, never a
    data edit. Asserted as tuples so a registry edit cannot quietly widen them."""

    def test_the_five_vocabularies(self) -> None:
        self.assertEqual(("dotnet", "pytest", "script"), guard.VALID_RUNNERS)
        self.assertEqual(("owner", "seam"), guard.BOUNDARY_KINDS)
        self.assertEqual(("focused", "module", "seam", "full"), guard.EVIDENCE_LEVELS)
        self.assertEqual(("project", "test", "debt"), guard.KNOWN_RED_FIELDS)
        self.assertEqual(("schemaVersion", "projects", "boundaries", "knownRed"),
                         guard.REGISTRY_FIELDS)

    def test_a_runner_outside_the_vocabulary_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.doc["projects"] = {"py": {"runner": "nose", "root": "tests", "tests": "tests"}}
            f.boundary(project="py")
            got = guard.check(f.write().root)
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("unsupported runner" in p for p in got["problems"]), got["problems"])

    def test_the_runner_vocabulary_FOLDS_case(self) -> None:
        # `-notcontains` folds, so `PYTEST` is an accepted spelling. A `==` here would invalidate every
        # registry row that used capitals, and nothing would say why.
        with Fixture() as f:
            f.doc["projects"] = {"py": {"runner": "PYTEST", "root": "tests", "tests": "tests"}}
            f.boundary(project="py")
            got = guard.check(f.write().root)
        self.assertNotIn("unsupported runner", " ".join(got["problems"]), got["problems"])

    def test_a_level_outside_the_vocabulary_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", level="thorough")
            got = guard.check(f.write().root)
        self.assertTrue(any("invalid evidence level" in p for p in got["problems"]), got["problems"])

    def test_a_kind_outside_the_vocabulary_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", kind="layer")
            got = guard.check(f.write().root)
        self.assertTrue(any("unsupported boundary kind" in p for p in got["problems"]),
                        got["problems"])

    def test_a_missing_ENFORCEMENT_CATALOG_is_a_finding_and_the_walk_continues(self) -> None:
        # THE OVER-CLOSING FIXTURE. The port made an unreadable enforcement registry a REFUSAL, which
        # was right by the letter of "fail closed" and wrong in the way that costs most: it suppressed
        # every OTHER finding, so a valid boundary registry plus a missing catalog produced one refusal
        # naming the catalog instead of the twenty boundary problems it actually had. Sixteen C# tests
        # asserting a specific boundary finding went red on exactly this.
        #
        # The distinction is by ROLE, not by severity: the guard's SUBJECT is a refusal when unreadable
        # (there is nothing to validate); a CATALOG it consults is a finding.
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", level="thorough", guards=["not-in-any-catalog"])
            f.write()
            (f.root / "scripts" / "enforcement-registry.v1.json").unlink()
            got = guard.check(f.root)
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("enforcement registry missing" in p for p in got["problems"]),
                        got["problems"])
        # The point: the OTHER findings are still there.
        self.assertTrue(any("invalid evidence level" in p for p in got["problems"]), got["problems"])
        self.assertTrue(any("unknown guard" in p for p in got["problems"]), got["problems"])

    def test_an_UNPARSEABLE_enforcement_catalog_is_a_finding_not_a_refusal(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            f.write()
            (f.root / "scripts" / "enforcement-registry.v1.json").write_text("{ not json",
                                                                            encoding="utf-8")
            got = guard.check(f.root)
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("enforcement registry is not valid JSON" in p for p in got["problems"]),
                        got["problems"])

    def test_the_SUBJECT_still_REFUSES_when_it_is_unreadable(self) -> None:
        # The other half of the pair, so the leniency above cannot widen into "never refuse".
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            f.write()
            (f.root / "scripts" / "verification-boundaries.v1.json").unlink()
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(f.root)
        self.assertEqual("REGISTRY-MISSING", caught.exception.reason)

    def test_an_unknown_guard_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", guards=["no-such-guard"])
            got = guard.check(f.write().root)
        self.assertTrue(any("unknown guard" in p for p in got["problems"]), got["problems"])

    def test_the_guards_SECTION_is_refused_on_the_registry(self) -> None:
        # Guard ids resolve through ONE catalog. A `guards` map here would be a second id -> script
        # source of truth, and the two could disagree.
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            f.doc["guards"] = {"session-boundary": "scripts/x.ps1"}
            got = guard.check(f.write().root)
        self.assertTrue(any("must not carry a 'guards' section" in p for p in got["problems"]),
                        got["problems"])


class TheStructuralRules(unittest.TestCase):
    """One test per rule, each varying ONE thing from a fixture that passes. A case that varies two
    things cannot attribute the failure."""

    def test_the_baseline_fixture_passes_so_each_case_below_is_attributable(self) -> None:
        with clean() as f:
            got = guard.check(f.write().root)
        self.assertEqual("OK", got["verdict"], got["problems"])

    def test_an_unknown_field_on_the_registry_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            f.doc["extraField"] = 1
            got = guard.check(f.write().root)
        self.assertTrue(any("registry has unknown field" in p for p in got["problems"]),
                        got["problems"])

    def test_an_unknown_field_on_a_boundary_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", extraField=1)
            got = guard.check(f.write().root)
        self.assertTrue(any("has unknown field: extraField" in p for p in got["problems"]),
                        got["problems"])

    def test_a_duplicate_boundary_id_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            f.doc["boundaries"].append(dict(MINIMAL_BOUNDARY, project="sample"))
            got = guard.check(f.write().root)
        self.assertTrue(any("duplicate boundary id" in p for p in got["problems"]), got["problems"])

    def test_a_boundary_with_no_paths_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=[])
            got = guard.check(f.write().root)
        self.assertTrue(any("has no paths" in p for p in got["problems"]), got["problems"])

    def test_a_STALE_EXACT_path_is_reported(self) -> None:
        # C8: an exact pattern naming no file has silently dropped whatever it owned to a wider
        # fallback, with nothing failing until this rule.
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Sample/ThereIsNoSuchFile.cs"])
            got = guard.check(f.write().root)
        self.assertTrue(any("stale exact path" in p for p in got["problems"]), got["problems"])

    def test_a_GLOB_path_is_not_required_to_exist(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Nobody/**"])
            got = guard.check(f.write().root)
        self.assertNotIn("stale exact path", " ".join(got["problems"]), got["problems"])

    def test_a_PARENT_ESCAPE_in_a_pattern_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/../outside/**"])
            got = guard.check(f.write().root)
        self.assertTrue(any("invalid boundary path" in p for p in got["problems"]), got["problems"])

    def test_an_ABSOLUTE_pattern_is_reported(self) -> None:
        # Each case separately, so a failure names which rule refused rather than "one of these four".
        for rejected, why in [("C:/outside/**", "a drive-rooted path"),
                              ("/etc/**", "a rooted path"),
                              ("", "the empty string"),
                              ("   ", "whitespace, which `$IsNullOrWhiteSpace` refused"),
                              ("src/../outside/**", "a parent escape"),
                              ("src\\A\\**", "a backslash")]:
            with self.subTest(pattern=rejected, why=why):
                self.assertFalse(guard.is_relative_registry_path(rejected), why)

    def test_a_RELATIVE_pattern_is_accepted(self) -> None:
        for accepted in ("src/A/**", "src/A/Name*.cs", "scripts/x.py", ".claude/**/*.md"):
            with self.subTest(pattern=accepted):
                self.assertTrue(guard.is_relative_registry_path(accepted), accepted)

    def test_a_BACKSLASH_pattern_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src\\Sample\\**"])
            got = guard.check(f.write().root)
        self.assertTrue(any("invalid boundary path" in p for p in got["problems"]), got["problems"])

    def test_two_owner_rows_claiming_one_pattern_are_reported_as_AMBIGUOUS(self) -> None:
        # The ambiguity key is `ToLowerInvariant()`, so two rows differing only in case are the SAME
        # pattern and one of them would silently win a whole directory.
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Sample/**"])
            f.doc["boundaries"].append({"id": "other-owner", "kind": "owner",
                                        "paths": ["src/sample/**"], "guards": ["session-boundary"],
                                        "level": "module"})
            got = guard.check(f.write().root)
        self.assertTrue(any("ambiguous owner pattern" in p for p in got["problems"]), got["problems"])

    def test_a_boundary_with_NEITHER_a_project_NOR_a_guard_is_reported(self) -> None:
        with Fixture() as f:
            f.boundary(project=None, guards=[])
            got = guard.check(f.write().root)
        self.assertTrue(any("needs a project or at least one guard" in p for p in got["problems"]),
                        got["problems"])

    def test_that_rule_is_WAIVED_under_a_data_or_fixture_root(self) -> None:
        # The waiver is the whole reason the four enforced roots exist: a boundary may name no proof at
        # all there, because those trees are still being mapped.
        for root in ("data/generated/**", "tests/fixtures/**"):
            with self.subTest(root=root):
                with Fixture() as f:
                    f.boundary(project=None, guards=[], paths=[root], level="full")
                    got = guard.check(f.write().root)
                self.assertNotIn("needs a project or at least one guard", " ".join(got["problems"]),
                                 got["problems"])

    def test_a_level_MISMATCH_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", level="focused")
            got = guard.check(f.write().root)
        self.assertTrue(any("level mismatch" in p for p in got["problems"]), got["problems"])


class TheDedupe(unittest.TestCase):
    """`$failures | Select-Object -Unique`: first-seen order, later duplicates dropped. A dedupe that
    dropped a *different* finding would hide it, so the key is the whole message and the order holds."""

    def test_identical_findings_collapse_to_one(self) -> None:
        self.assertEqual(["a", "b"], guard._dedupe(["a", "b", "a", "b", "a"]))

    def test_a_NEAR_identical_finding_is_KEPT(self) -> None:
        # Two rules firing on one row with different ids are two findings, and collapsing them would
        # hide one.
        self.assertEqual(["unknown project: a", "unknown project: b"],
                         guard._dedupe(["unknown project: a", "unknown project: b"]))

    def test_the_FIRST_seen_order_is_preserved(self) -> None:
        self.assertEqual(["z", "y", "x"], guard._dedupe(["z", "y", "x", "y", "z"]))


class TheStreamSplit(unittest.TestCase):
    def test_the_OK_line_is_the_only_thing_on_STDOUT(self) -> None:
        with clean() as f:
            result = f.write().run()
        self.assertEqual(guard.EXIT_OK, result["exit"])
        self.assertEqual(["VERIFICATION BOUNDARY GUARD OK"], result["stdout"].strip().splitlines())
        self.assertEqual("", result["stderr"].strip())

    def test_the_FINDINGS_are_on_STDERR_and_stdout_carries_no_finding(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", level="thorough")
            result = f.write().run()
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("VERIFICATION BOUNDARY GUARD FAILED", result["stderr"])
        self.assertIn("invalid evidence level", result["stderr"])
        self.assertEqual("", result["stdout"].strip(),
                         "a caller reading stdout alone must not be able to mistake a finding for a "
                         "verdict")


class TheJsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "problems", "report", "owners", "boundaries", "walked"}
    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with clean() as f:
            ok = f.write().json()
        with Fixture() as g:
            g.project()
            g.boundary(project="sample", level="thorough")
            bad = g.write().json()
        self.assertEqual(self.KEYS, set(ok["payload"]))
        self.assertEqual(self.KEYS, set(bad["payload"]))
        self.assertEqual("OK", ok["payload"]["verdict"])
        self.assertEqual("FAIL", bad["payload"]["verdict"])

    def test_a_refusal_carries_its_reason_and_the_same_keys(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gvb-refuse-") as tmp:
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--json"],
                                  capture_output=True, text=True, timeout=300)
        payload = json.loads(proc.stdout)
        self.assertEqual(self.REFUSAL_KEYS, set(payload))
        self.assertEqual("REGISTRY-MISSING", payload["reason"])
        self.assertEqual(guard.EXIT_FAILED, proc.returncode)

    def test_an_UNPARSEABLE_registry_is_its_OWN_refusal(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gvb-bad-") as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            (root / "scripts" / "verification-boundaries.v1.json").write_text(
                "{ not json", encoding="utf-8")
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                                  capture_output=True, text=True, timeout=300)
        self.assertEqual("REGISTRY-UNPARSEABLE", json.loads(proc.stdout)["reason"])


class TheReportIsAReading(unittest.TestCase):
    """`--report` prints scale, never an assertion. A number that moves with the registry must not be
    pinned, and combining it with the walk-skip is a REFUSAL, not an empty report that reads clean."""

    def test_report_with_the_walk_is_accepted_and_names_its_headings(self) -> None:
        with clean() as f:
            result = f.write().run("--report")
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"][:400])
        for heading in ("Registry depth over src/**", "production files mapped",
                        "VerificationId-filtered", "whole-project fallback",
                        "Orphan VerificationId traits", "Inputs with no local proof"):
            with self.subTest(heading=heading):
                self.assertIn(heading, result["stdout"])

    def test_report_WITH_skip_coverage_walk_is_REFUSED(self) -> None:
        with clean() as f:
            result = f.write().run("--report", "--skip-coverage-walk", "--json")
        payload = json.loads(result["stdout"])
        self.assertEqual("REPORT-NEEDS-THE-WALK", payload["reason"])
        self.assertEqual(guard.EXIT_FAILED, result["exit"])

    def test_the_skip_flag_still_checks_the_registry_itself(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", level="thorough")
            result = f.write().run("--skip-coverage-walk")
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("invalid evidence level", result["stderr"])

    def test_the_walk_is_reported_as_WALKED_or_not(self) -> None:
        # Inside the `with`, because the fixture deletes its temp directory on exit - the first version
        # checked it afterwards and got REGISTRY-MISSING, which says nothing about the walk.
        with clean() as f:
            root = f.write().root
            self.assertTrue(guard.check(root)["walked"])
            self.assertFalse(guard.check(root, skip_coverage_walk=True)["walked"])


class TheCoverageWalk(unittest.TestCase):
    def test_an_unmapped_source_file_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Nowhere/**"])
            (f.root / "src" / "Sample" / "orphan.cs").write_text("//\n", encoding="utf-8")
            got = guard.check(f.write().root)
        self.assertTrue(any(p.startswith("unmapped source:") for p in got["problems"]),
                        got["problems"])

    def test_the_skip_flag_suppresses_the_unmapped_finding_but_not_the_others(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Nowhere/**"], level="thorough")
            (f.root / "src" / "Sample" / "orphan.cs").write_text("//\n", encoding="utf-8")
            got = guard.check(f.write().root, skip_coverage_walk=True)
        self.assertFalse(any(p.startswith("unmapped source:") for p in got["problems"]),
                         got["problems"])
        self.assertTrue(any("invalid evidence level" in p for p in got["problems"]), got["problems"])

    def test_the_WALK_does_not_descend_into_build_output(self) -> None:
        # The measured cost the original's comment describes: a plain rglob descends into every bin/obj
        # before filtering, which is what made the src walk take 60-90s on this tree.
        #
        # The noise goes under a directory the owner does NOT cover. The first version put it under
        # `src/Sample/bin/...`, which `src/Sample/**` matches at any depth - so the file resolved, no
        # failure was produced, and the test passed whether or not pruning happened. A pruning test
        # whose fixture is covered by the owner tests nothing, and the mutation run proved it: removing
        # `skip` left the suite green.
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Sample/**"])
            for name in ("bin", "obj", "TestResults"):
                noise = f.root / "src" / "Elsewhere" / name
                noise.mkdir(parents=True)
                (noise / "generated.cs").write_text("//\n", encoding="utf-8")
            got = guard.check(f.write().root)
        self.assertNotIn("unmapped source", " ".join(got["problems"]),
                         f"build output was walked: {got['problems'][:4]}")

    def test_the_walk_STILL_reports_a_real_file_outside_build_output(self) -> None:
        # The pair that makes the pruning test meaningful: pruning must not be able to hide a real
        # unmapped source, or "we skip bin" becomes "we skip what we find inconvenient".
        with Fixture() as f:
            f.project()
            f.boundary(project="sample", paths=["src/Sample/**"])
            real = f.root / "src" / "Elsewhere"
            real.mkdir(parents=True)
            (real / "genuine.cs").write_text("//\n", encoding="utf-8")
            got = guard.check(f.write().root)
        self.assertTrue(any(p.startswith("unmapped source:") for p in got["problems"]),
                        got["problems"])

    def test_an_unregistered_TEST_project_is_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            (f.root / "tests" / "Fake.Tests").mkdir()
            (f.root / "tests" / "Fake.Tests" / "Fake.Tests.csproj").write_text(
                "<Project/>\n", encoding="utf-8")
            got = guard.check(f.write().root)
        self.assertTrue(any("test project not in registry" in p for p in got["problems"]),
                        got["problems"])

    def test_an_EXEMPT_test_project_is_not_reported(self) -> None:
        with Fixture() as f:
            f.project()
            f.boundary(project="sample")
            exempt = f.root / "tests" / "FusionRpg.Injector.Tests"
            exempt.mkdir()
            (exempt / "FusionRpg.Injector.Tests.csproj").write_text("<Project/>\n", encoding="utf-8")
            got = guard.check(f.write().root)
        self.assertNotIn("test project not in registry", " ".join(got["problems"]), got["problems"])


class TheKnownRedRules(unittest.TestCase):
    STUB = ("| id | status | debt |\n"
            "| --- | --- | --- |\n"
            "| `SR-1` | red | gone |\n"
            "| `SR-2` | green | fine |\n")

    def _fixture(self, **entry) -> Fixture:
        f = Fixture()
        f.project()
        f.boundary(project="sample")
        (f.root / "tests" / "Sample.Tests").mkdir()
        (f.root / "tests" / "Sample.Tests" / "test_thing.py").write_text("def test_x(): pass\n",
                                                                        encoding="utf-8")
        f.stub_register(self.STUB)
        f.known_red(project="sample", test="tests/Sample.Tests/test_thing.py::test_x",
                    debt="`SR-1`")
        for k, v in entry.items():
            f.doc["knownRed"][0][k] = v
        return f

    def test_an_honest_entry_passes(self) -> None:
        with self._fixture() as f:
            got = guard.check(f.write().root)
        self.assertEqual("OK", got["verdict"], got["problems"])

    def test_an_unknown_project_is_reported(self) -> None:
        with self._fixture(project="nope") as f:
            got = guard.check(f.write().root)
        self.assertTrue(any("names an unknown project" in p for p in got["problems"]), got["problems"])

    def test_a_missing_test_file_is_reported(self) -> None:
        with self._fixture(test="tests/Sample.Tests/test_absent.py::test_x") as f:
            got = guard.check(f.write().root)
        self.assertTrue(any("test file does not exist" in p for p in got["problems"]),
                        got["problems"])

    def test_a_debt_that_is_not_a_RED_row_is_reported(self) -> None:
        with self._fixture(debt="`SR-2`") as f:
            got = guard.check(f.write().root)
        self.assertTrue(any("does not resolve to a red row" in p for p in got["problems"]),
                        got["problems"])

    def test_an_unknown_field_on_an_entry_is_reported(self) -> None:
        with self._fixture(extra=1) as f:
            got = guard.check(f.write().root)
        self.assertTrue(any("has unknown field" in p for p in got["problems"]), got["problems"])

    def test_the_stub_register_status_FOLDS_case(self) -> None:
        # `$cells[1] -eq 'red'` folds, so a `RED` row resolves a debt. `==` here would invalidate
        # every knownRed entry in a register that used capitals.
        f = self._fixture()
        f.stub_register("| id | status |\n| --- | --- |\n| `SR-1` | RED |\n")
        got = guard.check(f.write().root)
        self.assertNotIn("does not resolve to a red row", " ".join(got["problems"]), got["problems"])

    def test_a_MISSING_stub_register_leaves_every_debt_unresolvable_and_SAYS_so(self) -> None:
        with self._fixture() as f:
            f.stub = None
            got = guard.check(f.write().root)
        self.assertTrue(any("does not resolve to a red row" in p for p in got["problems"]),
                        got["problems"])


class TheLibShapeIsReadNotRecalled(unittest.TestCase):
    """The two attributes this port got wrong, both by transcribing the OTHER language's spelling."""

    def test_PytestDirs_is_snake_case(self) -> None:
        dirs = guard.vb.pytest_project_dirs(
            {"py": {"runner": "pytest", "root": "tests", "tests": "tools/x"}}, "py")
        self.assertTrue(hasattr(dirs, "test_dir"))
        self.assertFalse(hasattr(dirs, "TestDir"),
                         "the lib is snake_case; the PowerShell twin's PascalCase has no business here")

    def test_a_Resolution_carries_boundary_DICTS(self) -> None:
        owners = [{"id": "o1", "kind": "owner", "paths": ["src/A/**"], "guards": ["g"],
                   "level": "module"}]
        resolution = guard.vb.resolve_owner("src/A/x.cs", owners)
        self.assertIsNotNone(resolution)
        self.assertIsInstance(resolution.owners, tuple)
        self.assertIsInstance(resolution.owners[0], dict,
                              "the owners are raw boundary dicts, not objects; `.id` would raise")
        self.assertIn("id", resolution.owners[0])


if __name__ == "__main__":
    unittest.main()
