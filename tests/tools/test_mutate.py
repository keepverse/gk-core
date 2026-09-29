"""Contract tests for `gk-core/scripts/mutate.py`.

Asserts the CONTRACT: the CLI surface, both on-disk set shapes, the project-resolution precedence, the
byte-level splice, the `caught`/`survived`/`stale` outcome vocabulary and its exit paths, the verified
restore, the timeout, and the `--json` envelope's key set.

WHY THE THREE OUTCOMES ARE SEPARATE
`caught`, `survived` and `stale` are three different facts and the tool exits non-zero for two of them. A
stale mutant is an UNTESTED CLAIM wearing the colours of a passing one -- its anchor no longer matches the
code, so the defect it describes was never actually introduced and never actually tested. Folding
`stale` into `caught` would let a mutation pass report success over mutants that never ran, which is the
silent-wrong-signal class this whole program exists to eliminate. The original kept them apart and so
does the port.

WHY THE SPLICE IS TESTED IN BYTES
The original read the file with `Get-Content -Raw` and wrote it back with `Set-Content -NoNewline`, so
mutating a file stored with LF rewrote every line of it. The port reads and writes bytes and splices only
the matched region. A test written against strings would not notice the difference, because after
decoding both look the same; only the byte count and the surviving CRLF show it.

WHY THE BASELINE REFUSAL CARRIES THE OUTPUT
The original discarded every line of suite output with `*> $null` -- justified in its own comment as "a
mutant run is all noise" -- in a tool whose entire purpose is attributing failures. So its red-baseline
throw named the project and not one failing test. These cases assert the tail survives, which is the whole
reason the port exists.

DIFFERENTIAL EVIDENCE
The port's project resolution agrees with `mutate.ps1`'s transcribed `Resolve-MutantProject` on every real
mutant set on disk -- 8 sets, 0 differences, reaching 2 distinct projects -- and the harness refuses to
report success when every set resolves to one project.
"""

from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
# The env override exists for MUTATION FALSIFICATION: a suite that can only load one path cannot be
# pointed at a deliberately broken copy. It changes which FILE is loaded and nothing else.
SCRIPT = Path(os.environ.get("MUTATE_SCRIPT", REPO / "scripts" / "mutate.py")).resolve()
LIB_DIR = Path(os.environ.get("MUTATE_LIB_DIR", REPO / "scripts" / "lib")).resolve()
RUN_TIMEOUT = 900

sys.path.insert(0, str(LIB_DIR))
_spec = importlib.util.spec_from_file_location("mutate", SCRIPT)
mutate = importlib.util.module_from_spec(_spec)
sys.modules["mutate"] = mutate
_spec.loader.exec_module(mutate)

EXIT_VOCABULARY = {0, 1}
OUTCOME_VOCABULARY = {"caught", "survived", "stale"}
EXPECTED_REFUSALS = {
    "NO-MUTANTS-DIR", "NO-MUTANT-SET", "MUTANT-SET-UNREADABLE", "MUTANT-SET-SHAPE", "MUTANT-SHAPE",
    "BASELINE-RED", "TARGET-UNREADABLE", "RESTORE-FAILED", "SUITE-TIMEOUT", "SUITE-UNRUNNABLE",
}
# The retired dialect's call-signs. `.ps1` is NOT here: a `.ps1` name is not an invocation, and this tool
# legitimately names its predecessor in the docstring that explains what it replaced.
FORBIDDEN_DIALECT_TOKENS = ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE")
DECLARED_PS1_REFERENCES = {
    "mutate.ps1": "the retired original, named only in the module docstring that explains what it "
                  "replaced and why",
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


# ------------------------------------------------------------------------------------------------
# Repository-shaped fixture
# ------------------------------------------------------------------------------------------------

# (outcome, the MUTANT-RUN suite exit code, the output that run produced). The BASELINE exit code is
# a separate parameter on `drive`, because a caught mutant and a red baseline are different facts and
# the tool checks the baseline before it applies anything.
CAUGHT = ("caught", 1, "one test failed, good")
SURVIVED = ("survived", 0, "every test passed while the code was wrong")


class Fixture:
    """A throwaway repository with mutant sets on disk and a real source file to mutate."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.mutants = root / "scripts" / "mutants"
        self.mutants.mkdir(parents=True)
        (root / "tests").mkdir()
        (root / "tests" / "core-test-projects.v1.json").write_text(json.dumps({
            "residual": "FusionRpg.Core.Tests",
            "projects": [{"name": "FusionRpg.Core.ActorHub.Tests", "include": ["ActorHub/**"]}],
        }), encoding="utf-8")
        (root / "tests" / "Fake.Tests").mkdir()
        (root / "tests" / "Fake.Tests" / "Fake.Tests.csproj").write_text("<Project/>", encoding="utf-8")

    def source(self, relative: str, body: str, *, crlf: bool = False) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        text = body.replace("\n", "\r\n") if crlf else body
        path.write_bytes(text.encode("utf-8"))
        return path

    def set(self, name: str, payload) -> Path:
        path = self.mutants / f"{name}.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def write_pytest_target(self) -> None:
        tests = self.root / "tools" / "seedsmith" / "tests"
        tests.mkdir(parents=True)
        (tests / "test_nothing.py").write_text("def test_ok():\n    assert True\n", encoding="utf-8")


class TemporaryRepo(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="mutate-contract-")
        self.repo = Path(self._tmp.name)
        self.fx = Fixture(self.repo)
        self.fx.write_pytest_target()
        self.fx.source("src/FusionRpg.Core/ActorHub/Thing.cs",
                       "class Thing {\n    void M() {\n        keep = 1;\n    }\n}\n")
        self.fx.set("bare", [make_mutant()])

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def base(self, *extra):
        """The argv prefix every `main` call in this suite shares. It lives HERE rather than on `TheRun`
        because the baseline cases drive `main` too, and the first version of this suite put it on the
        narrower class -- so three baseline cases errored with 'no attribute base' instead of testing the
        behaviour they were written for."""
        return ["--root", str(self.repo), "--json", *extra]


def make_mutant(find: str = "keep = 1;", with_: str = "keep = 2;") -> dict:
    return {"file": "src/FusionRpg.Core/ActorHub/Thing.cs", "name": "one deliberate defect",
            "find": find, "with": with_}


class SetLoading(TemporaryRepo):
    def test_the_BARE_ARRAY_shape_loads_with_no_project(self) -> None:
        loaded = mutate.read_set(self.fx.mutants / "bare.json")
        self.assertIsNone(loaded.project)
        self.assertEqual([m.name for m in loaded.mutants], ["one deliberate defect"])

    def test_the_OBJECT_shape_carries_its_project_and_the_same_array(self) -> None:
        self.fx.set("wrapped", {"project": "tests/FusionRpg.Core.World.Tests",
                                "mutants": [make_mutant()]})
        loaded = mutate.read_set(self.fx.mutants / "wrapped.json")
        self.assertEqual(loaded.project, "tests/FusionRpg.Core.World.Tests")
        self.assertEqual(len(loaded.mutants), 1)

    def test_a_PYTHON_mutant_is_recognised_by_extension_case_INSENSITIVELY(self) -> None:
        """PowerShell's `-like "*.py"` folds case, so a `.PY` target ran pytest. `str.endswith` does
        not, so an unfolded comparison would silently switch such a mutant to `dotnet test`."""
        self.assertTrue(mutate.Mutant("a.py", "n", "x", "y").is_python)
        self.assertTrue(mutate.Mutant("a.PY", "n", "x", "y").is_python)
        self.assertFalse(mutate.Mutant("a.cs", "n", "x", "y").is_python)

    def test_a_set_missing_a_required_key_is_a_NAMED_refusal(self) -> None:
        self.fx.set("broken", [{"file": "a.cs", "name": "n", "find": "x"}])
        with self.assertRaises(mutate.Refusal) as caught:
            mutate.read_set(self.fx.mutants / "broken.json")
        self.assertEqual(caught.exception.reason, "MUTANT-SHAPE")
        self.assertIn("with", caught.exception.detail)

    def test_an_OBJECT_set_with_NO_mutants_key_is_refused_not_read_as_empty(self) -> None:
        """Reading it as an empty set would report a green pass over a set that was never defined."""
        self.fx.set("nomutants", {"project": "tests/X"})
        with self.assertRaises(mutate.Refusal) as caught:
            mutate.read_set(self.fx.mutants / "nomutants.json")
        self.assertEqual(caught.exception.reason, "MUTANT-SET-SHAPE")

    def test_a_set_name_that_matches_NOTHING_names_what_is_on_disk(self) -> None:
        """A refusal that does not list the available names makes the caller guess."""
        with self.assertRaises(mutate.Refusal) as caught:
            mutate.load_sets(self.repo, "no-such-set")
        self.assertEqual(caught.exception.reason, "NO-MUTANT-SET")
        self.assertIn("bare", caught.exception.detail)


# ------------------------------------------------------------------------------------------------
# Project resolution precedence
# ------------------------------------------------------------------------------------------------

class ProjectResolution(TemporaryRepo):
    def test_a_sets_OWN_project_beats_the_global_override(self) -> None:
        """Not alphabetical precedence. A set whose tests have split names the split project
        deliberately; a global override would quietly run it against the wrong suite."""
        self.assertEqual(
            mutate.resolve_project(self.repo, "tests/Set.Tests", "tests/Global.Tests",
                                   [mutate.Mutant("a.cs", "n", "x", "y")]),
            "tests/Set.Tests")

    def test_the_global_override_beats_AUTO_resolution(self) -> None:
        self.assertEqual(
            mutate.resolve_project(self.repo, None, "tests/Global.Tests",
                                   [mutate.Mutant("src/FusionRpg.Core/ActorHub/T.cs", "n", "x", "y")]),
            "tests/Global.Tests")

    def test_auto_resolution_reads_the_FIRST_DOTNET_mutants_folder(self) -> None:
        mutants = [mutate.Mutant("tools/seedsmith/x.py", "py", "x", "y"),
                   mutate.Mutant("src/FusionRpg.Core/ActorHub/T.cs", "cs", "x", "y"),
                   mutate.Mutant("src/FusionRpg.Core/Battle/T.cs", "later", "x", "y")]
        self.assertEqual(mutate.resolve_project(self.repo, None, None, mutants),
                         "tests/FusionRpg.Core.ActorHub.Tests")

    def test_a_set_with_NO_dotnet_mutants_falls_back_to_the_RESIDUAL(self) -> None:
        self.assertEqual(
            mutate.resolve_project(self.repo, None, None,
                                   [mutate.Mutant("tools/seedsmith/x.py", "py", "x", "y")]),
            mutate.CORE_FALLBACK_PROJECT)

    def test_a_folder_the_manifest_does_not_know_falls_back_to_the_RESIDUAL(self) -> None:
        self.assertEqual(
            mutate.resolve_project(self.repo, None, None,
                                   [mutate.Mutant("src/FusionRpg.Core/Nope/T.cs", "n", "x", "y")]),
            "tests/FusionRpg.Core.Tests")

    def test_every_real_set_on_disk_resolves_through_the_SHARED_resolver(self) -> None:
        """The duplication this retired: the rule lived in two tools and had already diverged. Every real
        set is resolved here through the shared module, so a change to the manifest cannot leave one
        caller stale."""
        sets = mutate.load_sets(REPO, None)
        self.assertTrue(sets, "no real mutant sets found, so nothing was exercised")
        for mutant_set in sets:
            resolved = mutate.resolve_project(REPO, mutant_set.project, None, mutant_set.mutants)
            self.assertTrue(resolved.startswith("tests/"), f"{mutant_set.name} -> {resolved}")
            self.assertTrue((REPO / resolved).is_dir(),
                            f"{mutant_set.name} resolved to {resolved}, which is not a directory")


# ------------------------------------------------------------------------------------------------
# The splice
# ------------------------------------------------------------------------------------------------

class TheSplice(unittest.TestCase):
    LF = b"class C {\n    void M() {\n        keep = 1;\n    }\n}\n"
    CRLF = b"class C {\r\n    void M() {\r\n        keep = 1;\r\n    }\r\n}\r\n"

    def test_an_LF_anchor_matches_an_LF_file_and_replaces_only_that_region(self) -> None:
        out = mutate.splice(self.LF, "keep = 1;", "keep = 2;")
        self.assertEqual(out, self.LF.replace(b"keep = 1;", b"keep = 2;"))

    def test_an_LF_anchor_matches_a_CRLF_file_and_the_CRLF_SURVIVES_everywhere_else(self) -> None:
        """The defect the byte-level splice exists to avoid. A text round-trip through
        `Get-Content -Raw` / `Set-Content` would have rewritten this whole file to the platform's
        endings, changing every line it was not mutating."""
        out = mutate.splice(self.CRLF, "keep = 1;", "keep = 2;")
        self.assertIsNotNone(out)
        self.assertIn(b"keep = 2;", out)
        self.assertEqual(out.count(b"\r\n"), self.CRLF.count(b"\r\n"),
                         "the CRLF endings outside the spliced region were rewritten")
        self.assertEqual(out.count(b"\n"), self.CRLF.count(b"\n"),
                         "a bare LF appeared where the file used CRLF")

    def test_only_the_spliced_REGION_differs_from_the_original_bytes(self) -> None:
        out = mutate.splice(self.CRLF, "keep = 1;", "keep = 2;")
        self.assertEqual(out.replace(b"keep = 2;", b"keep = 1;"), self.CRLF)

    def test_a_MULTI_LINE_anchor_splices_the_whole_span(self) -> None:
        out = mutate.splice(self.LF, "keep = 1;\n    }", "keep = 99;\n    }")
        self.assertEqual(out, self.LF.replace(b"keep = 1;\n    }", b"keep = 99;\n    }"))

    def test_a_MISSING_anchor_returns_None_rather_than_an_UNCHANGED_copy(self) -> None:
        """Returning the original bytes would be read as "applied and caught", which is a fabricated
        verdict on a mutant that never ran."""
        self.assertIsNone(mutate.splice(self.LF, "not in the file at all", "x"))

    def test_only_the_FIRST_occurrence_is_mutated(self) -> None:
        """Two occurrences and a mutant meaning the second is a DATA bug; mutating both would hide it."""
        doubled = b"keep = 1;\nkeep = 1;\n"
        out = mutate.splice(doubled, "keep = 1;", "keep = 2;")
        self.assertEqual(out, b"keep = 2;\nkeep = 1;\n")

    def test_normalisation_is_for_MATCHING_only(self) -> None:
        self.assertEqual(mutate.normalise("a\r\nb"), "a\nb")
        self.assertEqual(mutate.normalise("a\nb"), "a\nb")


# ------------------------------------------------------------------------------------------------
# The run: verdicts, restore, baseline
# ------------------------------------------------------------------------------------------------

def drive(argv, *, dotnet_baseline=0, dotnet_mutant=0, pytest_baseline=0, pytest_mutant=0,
          dotnet_output="Passed!", pytest_output="passed"):
    """Drive `main` with both suite runners mocked, returning `(exit code, stdout, stderr)`.

    THE BASELINE AND THE MUTANT RUN ARE SEPARATE PARAMETERS, and the first version of this helper had
    one. The tool baselines each suite ONCE and then runs it again per mutant, so a single code made every
    "the mutant was caught" case report `BASELINE-RED` instead -- six failures that all said the same
    thing and none of which were about the tool. The count is what tells the two apart: call 0 is the
    baseline, calls 1..n are mutants.
    """
    out, err = [], []
    seen = {"dotnet": 0, "pytest": 0}

    def fake_dotnet(project, filter_text, repo, timeout):
        index = seen["dotnet"]
        seen["dotnet"] += 1
        return (dotnet_baseline if index == 0 else dotnet_mutant), dotnet_output

    def fake_pytest(repo, timeout):
        index = seen["pytest"]
        seen["pytest"] += 1
        return (pytest_baseline if index == 0 else pytest_mutant), pytest_output

    patches = [
        mock.patch.object(mutate, "run_dotnet_suite", side_effect=fake_dotnet),
        mock.patch.object(mutate, "run_pytest_suite", side_effect=fake_pytest),
        mock.patch.object(mutate.sys.stdout, "write", out.append),
        mock.patch.object(mutate.sys.stderr, "write", err.append),
    ]
    for patch in patches:
        patch.start()
    try:
        code = mutate.main(argv)
    finally:
        for patch in patches:
            patch.stop()
    return code, "".join(out), "".join(err)


class TheRun(TemporaryRepo):
    def test_a_CAUGHT_mutant_exits_zero_and_reports_the_verdict(self) -> None:
        code, out, _ = drive(self.base(), dotnet_mutant=CAUGHT[1], dotnet_output=CAUGHT[2])
        self.assertEqual(code, 0, out)
        report = json.loads(out)
        self.assertEqual(report["verdict"], "OK")
        self.assertEqual(report["totals"], {"mutants": 1, "caught": 1, "survived": 0, "stale": 0})
        self.assertEqual(report["verdicts"][0]["outcome"], "caught")

    def test_a_SURVIVING_mutant_exits_ONE_and_names_it(self) -> None:
        code, out, _ = drive(self.base(), dotnet_mutant=SURVIVED[1], dotnet_output=SURVIVED[2])
        self.assertEqual(code, 1)
        self.assertEqual(json.loads(out)["verdict"], "FAIL")
        self.assertEqual([r["name"] for r in json.loads(out)["survived"]], ["one deliberate defect"])
        self.assertIn("every test passed while the code was wrong",
                      json.loads(out)["survived"][0]["detail"])

    def test_a_SURVIVOR_GOES_TO_STDERR_while_the_reading_stays_on_STDOUT(self) -> None:
        """The stream discipline, in prose mode where it is observable. `--json` mode prints only the
        envelope, so asserting the finding text on stderr there tested nothing -- the assertion below runs
        the tool the way a human reads it."""
        out: list[str] = []
        err: list[str] = []
        patches = [
            mock.patch.object(mutate, "run_dotnet_suite",
                              side_effect=[(0, "ok"), (SURVIVED[1], SURVIVED[2])]),
            mock.patch.object(mutate.sys.stdout, "write", out.append),
            mock.patch.object(mutate.sys.stderr, "write", err.append),
        ]
        for patch in patches:
            patch.start()
        try:
            code = mutate.main(["--root", str(self.repo)])
        finally:
            for patch in patches:
                patch.stop()
        self.assertEqual(code, 1)
        self.assertIn("one deliberate defect", "".join(err), "the finding is not on stderr")
        self.assertIn("1 survived", "".join(err), "the survivor count is not on stderr")
        self.assertIn("one deliberate defect", "".join(out), "the per-mutant reading is not on stdout")
        # The per-mutant READING line carries the outcome on stdout -- that is the report. The FINDING is
        # the summary line, and it belongs on stderr. The first version of this case asserted the word
        # "SURVIVED" never appeared on stdout, which is wrong: the reading legitimately says so.
        self.assertIn("1 survived", "".join(err), "the survivor summary is not on stderr")
        self.assertNotIn("1 survived", "".join(out),
                         "a finding leaked onto stdout, where a pipe would read it as the report")

    def test_a_STALE_mutant_exits_ONE_and_is_NEITHER_caught_nor_survived(self) -> None:
        """The core distinction: an untested claim must not read as a pass. The suite exit code for a
        stale mutant is never consulted, because the defect was never introduced."""
        self.fx.set("stale", [{"file": "src/FusionRpg.Core/ActorHub/Thing.cs",
                               "name": "anchor that no longer exists",
                               "find": "this text is gone;", "with": "x"}])
        code, out, _ = drive(self.base("--set", "stale"), dotnet_mutant=CAUGHT[1])
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["verdict"], "FAIL")
        self.assertEqual(report["totals"], {"mutants": 1, "caught": 0, "survived": 0, "stale": 1})
        self.assertEqual([r["name"] for r in report["stale"]], ["anchor that no longer exists"])
        self.assertEqual(report["stale"][0]["stage"], "anchor")

    def test_a_STALE_mutant_is_reported_as_NEVER_RAN_on_STDERR(self) -> None:
        """The original's own reason for keeping `stale` apart: a stale mutant is an UNTESTED CLAIM
        wearing the colours of a passing one, so the wording has to say it did not run."""
        self.fx.set("stale", [{"file": "src/FusionRpg.Core/ActorHub/Thing.cs",
                               "name": "anchor that no longer exists",
                               "find": "this text is gone;", "with": "x"}])
        out: list[str] = []
        err: list[str] = []
        patches = [
            mock.patch.object(mutate, "run_dotnet_suite", return_value=(0, "ok")),
            mock.patch.object(mutate.sys.stdout, "write", out.append),
            mock.patch.object(mutate.sys.stderr, "write", err.append),
        ]
        for patch in patches:
            patch.start()
        try:
            code = mutate.main(["--root", str(self.repo), "--set", "stale"])
        finally:
            for patch in patches:
                patch.stop()
        self.assertEqual(code, 1)
        self.assertIn("never ran", "".join(err))
        self.assertIn("anchor that no longer exists", "".join(err))

    def test_every_outcome_in_the_report_is_from_the_CLOSED_vocabulary(self) -> None:
        _, out, _ = drive(self.base(), dotnet_mutant=1)
        for row in json.loads(out)["verdicts"]:
            self.assertIn(row["outcome"], OUTCOME_VOCABULARY, row)

    def test_the_json_envelope_carries_the_KEYS_and_not_a_field_that_could_rot(self) -> None:
        _, out, _ = drive(self.base(), dotnet_mutant=CAUGHT[1])
        report = json.loads(out)
        self.assertEqual(set(report), {"tool", "verdict", "totals", "survived", "stale", "verdicts"})
        self.assertEqual(set(report["verdicts"][0]),
                         {"set", "name", "file", "outcome", "stage", "detail"})
        # No wall-clock, no timestamp, no run id: a field that differs per run is a field nothing can
        # assert on.
        for banned in ("duration", "elapsed", "timestamp", "started", "finished"):
            self.assertNotIn(banned, json.dumps(report))

    def test_the_file_is_RESTORED_byte_for_byte_after_a_CAUGHT_mutant(self) -> None:
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        before = target.read_bytes()
        drive(self.base(), dotnet_mutant=CAUGHT[1])
        self.assertEqual(target.read_bytes(), before, "the mutated file was not restored")

    def test_the_file_is_RESTORED_byte_for_byte_even_after_a_SURVIVING_mutant(self) -> None:
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        before = target.read_bytes()
        drive(self.base(), dotnet_mutant=SURVIVED[1])
        self.assertEqual(target.read_bytes(), before)

    def test_the_file_is_RESTORED_even_when_the_SUITE_raises(self) -> None:
        """The `finally` is the whole reason a kill mid-mutant leaves only a `.bak`; an exception during
        the run must not skip it."""
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        before = target.read_bytes()
        with mock.patch.object(mutate, "run_dotnet_suite", side_effect=RuntimeError("boom")):
            with self.assertRaises(RuntimeError):
                mutate.main(self.base())
        self.assertEqual(target.read_bytes(), before, "an exception left the file mutated")

    def test_a_FAILED_RESTORE_is_a_NAMED_refusal_not_a_green_run(self) -> None:
        """This tool is the one place the repository corrupts tracked source on purpose, so 'the bytes
        came back' is measured. A green summary over a damaged tree is the silent wrong signal.

        THE SABOTAGE IDENTIFIES THE RESTORE BY ITS CONTENT, NOT BY THE PATH OBJECT. The first version
        guarded on `self == target` and never fired, because `main` resolves the repository root and the
        test's own `Path` is therefore a different object from the tool's -- equal as a file, unequal as
        an identity. The case then passed for the wrong reason: no sabotage, no damage, and a `caught`
        verdict reported as though the guard had been exercised. Content is the reliable discriminator,
        because a write carrying the original bytes IS the restore by definition."""
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        original = target.read_bytes()
        real_write = Path.write_bytes
        fired = []

        def sabotage(self, data, **kwargs):
            if data == original:
                fired.append("restore")
                return real_write(self, b"class C { }\n", **kwargs)
            fired.append("mutation")
            return real_write(self, data, **kwargs)

        with mock.patch.object(Path, "write_bytes", sabotage):
            code, out, _ = drive(self.base(), dotnet_mutant=CAUGHT[1])
        self.assertEqual(fired, ["mutation", "restore"],
                         "the restore was never written, so the guard under test was never reached")
        self.assertEqual(code, 1)
        self.assertEqual(json.loads(out)["reason"], "RESTORE-FAILED")
        self.assertIn("every later verdict is void", json.loads(out)["detail"])

    def test_a_CSHARP_mutant_TOUCHES_the_file_but_a_PYTHON_one_does_not(self) -> None:
        """Restoring leaves an OLDER timestamp than the compiled output, so MSBuild keeps the MUTATED
        assembly and the next ordinary run fails against clean source. Python has no build step to fool,
        so the touch is skipped -- the original's rule, kept for a reason that is not obvious."""
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        os.utime(target, (1_000_000, 1_000_000))
        drive(self.base(), dotnet_mutant=CAUGHT[1])
        self.assertGreater(target.stat().st_mtime, 1_000_000, "the .cs file was not touched")

        self.fx.source("tools/seedsmith/thing.py", "keep = 1\n")
        self.fx.set("py", [{"file": "tools/seedsmith/thing.py", "name": "python defect",
                            "find": "keep = 1", "with": "keep = 2"}])
        # A `.py` target is restored WITH ITS TIMESTAMP, so the tool leaves no trace at all: byte- and
        # timestamp-identical. The original skipped its touch for these because there is no build step,
        # but it still MOVED a `.bak` over the file, which restamps it -- so "not touched" was never
        # actually true there either.
        py_target = self.repo / "tools" / "seedsmith" / "thing.py"
        os.utime(py_target, (1_000_000, 1_000_000))
        drive(self.base("--set", "py"), pytest_mutant=1)
        self.assertEqual(py_target.stat().st_mtime, 1_000_000, "the .py file was left re-stamped")

    def test_a_PYTHON_mutant_runs_PYTEST_not_dotnet(self) -> None:
        self.fx.source("tools/seedsmith/thing.py", "keep = 1\n")
        self.fx.set("py", [{"file": "tools/seedsmith/thing.py", "name": "python defect",
                            "find": "keep = 1", "with": "keep = 2"}])
        # Baseline green, mutant run red: ONE code for both made the red BASELINE fire first, so pytest
        # was called once instead of twice and the assertion read as "a .py mutant skipped the suite".
        seen = {"n": 0}

        def fake_pytest(repo, timeout):
            seen["n"] += 1
            return (0, "ok") if seen["n"] == 1 else (1, "failed")

        with mock.patch.object(mutate, "run_dotnet_suite") as dotnet, \
                mock.patch.object(mutate, "run_pytest_suite", side_effect=fake_pytest) as pytest:
            out: list[str] = []
            with mock.patch.object(mutate.sys.stdout, "write", out.append):
                mutate.main(self.base("--set", "py"))
        self.assertEqual(dotnet.call_count, 0, "a .py mutant invoked dotnet")
        # One baseline plus one per mutant.
        self.assertEqual(pytest.call_count, 2)

    def test_NO_bak_file_is_left_behind_on_the_success_path(self) -> None:
        drive(self.base(), dotnet_mutant=CAUGHT[1])
        self.assertEqual(list(self.repo.rglob("*.mutate-bak")), [])


# ------------------------------------------------------------------------------------------------
# The baseline gate
# ------------------------------------------------------------------------------------------------

class TheBaselineGate(TemporaryRepo):
    def test_a_RED_baseline_REFUSES_before_any_mutation_is_applied(self) -> None:
        """A red baseline makes every mutant report 'caught' because the suite already fails, and a
        compile error in somebody else's file looks exactly like a test noticing the defect."""
        target = self.repo / "src" / "FusionRpg.Core" / "ActorHub" / "Thing.cs"
        before = target.read_bytes()
        code, out, _ = drive(self.base(), dotnet_baseline=1, dotnet_output="Failed!  - Failed: 3")
        self.assertEqual(code, 1)
        report = json.loads(out)
        self.assertEqual(report["reason"], "BASELINE-RED")
        self.assertEqual(report["verdicts"], [], "mutants ran despite a red baseline")
        self.assertEqual(target.read_bytes(), before)

    def test_the_REFUSAL_QUOTES_the_failing_output_the_original_discarded(self) -> None:
        """Both original suite runners ended in `*> $null` -- "a mutant run is all noise" -- so its red
        baseline named the project and not one failing test. This is the reason the port keeps output."""
        code, out, _ = drive(self.base(), dotnet_baseline=1,
                             dotnet_output="Failed Xyz\nAssert.Equal() Failure\nsome detail")
        detail = json.loads(out)["detail"]
        self.assertIn("Failed Xyz", detail)
        self.assertIn("Assert.Equal() Failure", detail)

    def test_a_RED_baseline_with_NO_output_says_so_rather_than_showing_nothing(self) -> None:
        code, out, _ = drive(self.base(), dotnet_baseline=1, dotnet_output="")
        self.assertEqual(code, 1)
        self.assertIn("no output at all", json.loads(out)["detail"])

    def test_the_baseline_is_checked_ONCE_PER_DISTINCT_project_not_once_per_set(self) -> None:
        self.fx.set("second", [make_mutant(find="keep = 1;", with_="keep = 3;")])
        with mock.patch.object(mutate, "run_dotnet_suite", return_value=(1, "red")) as runner:
            with self.assertRaises(mutate.Refusal):
                mutate.check_baselines(self.repo, mutate.load_sets(self.repo, None),
                                       {"bare": "tests/Fake.Tests", "second": "tests/Fake.Tests"},
                                       None, 5)
        self.assertEqual(runner.call_count, 1, "the same project was baselined more than once")

    def test_a_PYTEST_baseline_is_checked_when_a_set_has_a_python_mutant(self) -> None:
        self.fx.source("tools/seedsmith/thing.py", "keep = 1\n")
        self.fx.set("py", [{"file": "tools/seedsmith/thing.py", "name": "n",
                            "find": "keep = 1", "with": "keep = 2"}])
        with mock.patch.object(mutate, "run_dotnet_suite", return_value=(0, "ok")), \
                mock.patch.object(mutate, "run_pytest_suite", return_value=(1, "red")):
            with self.assertRaises(mutate.Refusal) as caught:
                mutate.check_baselines(self.repo, mutate.load_sets(self.repo, None),
                                       {"bare": "tests/Fake.Tests", "py": "tests/Fake.Tests"},
                                       None, 5)
        self.assertEqual(caught.exception.reason, "BASELINE-RED")
        self.assertIn("seedsmith", caught.exception.detail)


# ------------------------------------------------------------------------------------------------
# The timeout, and the suite-translation layer
# ------------------------------------------------------------------------------------------------

class TheTimeoutPath(unittest.TestCase):
    def test_a_wedged_dotnet_run_becomes_a_NAMED_timeout_refusal(self) -> None:
        with mock.patch.object(mutate.subprocess, "run",
                               side_effect=mutate.subprocess.TimeoutExpired(cmd="dotnet", timeout=9)):
            with self.assertRaises(mutate.Refusal) as caught:
                mutate.run_dotnet_suite("tests/X", None, REPO, 9)
        self.assertEqual(caught.exception.reason, "SUITE-TIMEOUT")
        self.assertIn("tests/X", caught.exception.detail)

    def test_a_wedged_pytest_run_becomes_a_NAMED_timeout_refusal(self) -> None:
        with mock.patch.object(mutate.subprocess, "run",
                               side_effect=mutate.subprocess.TimeoutExpired(cmd="pytest", timeout=4)):
            with self.assertRaises(mutate.Refusal) as caught:
                mutate.run_pytest_suite(REPO, 4)
        self.assertEqual(caught.exception.reason, "SUITE-TIMEOUT")

    def test_a_MISSING_dotnet_is_a_NAMED_refusal_rather_than_a_traceback(self) -> None:
        with mock.patch.object(mutate.subprocess, "run", side_effect=FileNotFoundError("dotnet")):
            with self.assertRaises(mutate.Refusal) as caught:
                mutate.run_dotnet_suite("tests/X", None, REPO, 1)
        self.assertEqual(caught.exception.reason, "SUITE-UNRUNNABLE")

    def test_a_non_zero_suite_code_means_CAUGHT_and_the_CODE_itself_does_not_matter(self) -> None:
        """A dropped guard is often a TYPE ERROR, so a mutated build can fail to COMPILE as easily as
        fail an assertion. Both are the mutant being caught, and collapsing the distinction is correct."""
        completed = subprocess.CompletedProcess(args=[], returncode=1, stdout="", stderr="")
        with mock.patch.object(mutate.subprocess, "run", return_value=completed):
            code, _ = mutate.run_dotnet_suite("tests/X", None, REPO, 5)
        self.assertEqual(code, 1)

    def test_the_WHOLE_output_is_kept_rather_than_discarded(self) -> None:
        whole = "restore warning NU1701\nFailed!  - Failed: 1\nxunit writes to stderr\n"
        completed = subprocess.CompletedProcess(args=[], returncode=1, stdout=whole, stderr="")
        with mock.patch.object(mutate.subprocess, "run", return_value=completed):
            _, output = mutate.run_dotnet_suite("tests/X", None, REPO, 5)
        self.assertEqual(output, whole)
        self.assertIn("NU1701", output, "the original's `*> $null` filter is back")


# ------------------------------------------------------------------------------------------------
# Surface and vocabulary
# ------------------------------------------------------------------------------------------------

class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--set", "--project", "--filter", "--timeout", "--root", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--set", "x", "-Project", "y"],
                             capture_output=True, text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower())

    def test_it_shells_out_to_no_PowerShell_interpreter(self) -> None:
        code = code_without_retired_dialect(SCRIPT.read_text(encoding="utf-8"))
        for token in FORBIDDEN_DIALECT_TOKENS:
            self.assertNotIn(token, code, f"the port still uses {token!r} in CODE")

    def test_every_PS1_it_names_is_a_DECLARED_reference(self) -> None:
        import re
        found = set(re.findall(r"[\w.-]+\.ps1\b",
                               code_without_retired_dialect(SCRIPT.read_text(encoding="utf-8"))))
        self.assertEqual(found - set(DECLARED_PS1_REFERENCES), set(),
                         f"undeclared .ps1 reference(s): {sorted(found - set(DECLARED_PS1_REFERENCES))}")
        for name, reason in DECLARED_PS1_REFERENCES.items():
            self.assertTrue(reason.strip(), f"{name} is declared without a reason")

    def test_the_refusal_reasons_are_the_CLOSED_set_this_suite_knows(self) -> None:
        import re
        found = set(re.findall(r'Refusal\(\s*"([A-Z][A-Z0-9-]+)"',
                               SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons were found at all")
        self.assertEqual(found - EXPECTED_REFUSALS, set(),
                         f"undocumented refusal reason(s): {sorted(found - EXPECTED_REFUSALS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({mutate.EXIT_OK, mutate.EXIT_FAILED}, EXIT_VOCABULARY)

    def test_the_timeout_is_declared_and_reachable(self) -> None:
        """This tool invokes suites N+1 times, so a single unbounded run can hold the whole pass open."""
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        self.assertIn("no timeout", " ".join(out.split()).lower())


if __name__ == "__main__":
    unittest.main()
