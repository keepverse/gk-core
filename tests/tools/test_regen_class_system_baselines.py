"""Contract tests for `gk-core/scripts/regen_class_system_baselines.py`.

A baseline regenerator, so the contract is about the MEASUREMENT not changing: the live config resolved
numerically, the three tools run with the same fixed seeds, the goldens extracted rather than retyped,
and the emitted prose reproduced byte-for-byte.

The prose point is not vanity. These three files are committed baselines every later phase diffs
against, and a port that "improved" the wording would be a silent semantic change to a measurement
artifact. The first transcription of the `coverage.tuningSync` note cleaned up three quirks and the
differential caught every one:

  * `chains` NOT `` `chains` `` -- PowerShell drops the backtick from an UNRECOGNISED escape, so
    `` "`chains` `` committed as plain `chains` and the markdown was lost silently;
  * `dominanceMatrix/ dominantCorners` WITH A SPACE -- a line-wrap artifact of string concatenation;
  * `"neutral"` IN DOUBLE QUOTES -- `` `" `` IS a recognised escape, so those survived while the bare
    backticks did not.

And the float spelling: PowerShell writes `6.9E-06`, Python `6.9e-06`. Same double, different byte, in
a diff target. The port matches the original, and the transform is applied to the encoder's own float
chunks -- three shapes, all three handled -- because an anchored pattern rewrote dict floats and
silently skipped every list float.

`subprocess`, `shutil` and `tempfile` are reached through private seams, because they are process-wide
modules: a patch of any of them reaches every other test in the project. The REAL end-to-end run --
both tools, the same three simulators, the live config -- is a separate harness, because a suite that
stubs the thing under test is testing its own arithmetic.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("REGEN_BASELINES_SCRIPT",
                             REPO / "scripts" / "regen_class_system_baselines.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_regen_class_system_baselines.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("regen_class_system_baselines", SCRIPT)
regen = importlib.util.module_from_spec(_spec)
sys.modules["regen_class_system_baselines"] = regen
_spec.loader.exec_module(regen)
_PRISTINE = {"_RUN": regen._RUN, "_WHICH": regen._WHICH, "_RMTREE": regen._RMTREE}

TUNING = {"aptitudes.v2.json": 2, "aptitudes.v9.json": 9, "aptitudes.v10.json": 10}
GOLDENS_CS = """
namespace FusionRpg.Core.Tests.Battle;
public class BattleGoldenTests
{
    // Several historical re-bless paragraphs accumulate here, which is why the RulesetVersion is read
    // from the const below and never from a comment like "RulesetVersion 3".
    private const string StompHash = "AF6A4F0C785DB30CB465A356B9F58D3C626AD85AB5827B74B33D1D023D0A59CD";
    private const string CloseHash = "2734593679CC89F6E1DDC81064FB1A7923B3D0D986B1E52C2D538AECACB085EC";
    private const string WipeHash = "D8801D6403BD89B702299A083808AA81936F1BC2B1EA686E802EB8770F79CEF4";
    private const string SeedSweepHash = "38CCD2CDEF7C5009928B288700ED2938D31F21109159B2789A648EF2C1E5782F";
}
"""
RULESET_CS = """
namespace FusionRpg.Core.Battle;
public static class BattleRuleset
{
    public const int RulesetVersion = 5;
}
"""


class Ground:
    """A planted repository: tuning files, the three archetype builds, both built tools, and the two
    C# sources. Every attribute is set EXPLICITLY at each construction site -- a fixture whose name
    contradicts what it sets up has cost this program three times already."""

    def __init__(self, tuning: dict[str, int] | None = None, builds: bool = True,
                 built: bool = True, goldens: str | None = GOLDENS_CS,
                 ruleset: str | None = RULESET_CS) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="regen-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.out = Path(self._tmp.name) / "out"
        for directory in (self.root / "data" / "tuning", self.root / "tools" / "CombatSim" / "builds"):
            directory.mkdir(parents=True, exist_ok=True)
        for name, value in (tuning if tuning is not None else TUNING).items():
            (self.root / "data" / "tuning" / name).write_text(
                json.dumps({"version": value, "edges": []}), encoding="utf-8")
        if builds:
            for name in regen.ARCHETYPE_ORDER:
                (self.root / "tools" / "CombatSim" / "builds" / f"{name}.json").write_text(
                    json.dumps({"archetype": name, "element": "fire"}), encoding="utf-8")
        for tool in (regen.COMBATSIM, regen.DOMINANCE_TOOL):
            out = self.root.joinpath(*tool, "bin", "Debug", "net8.0")
            out.mkdir(parents=True, exist_ok=True)
            if built:
                (out / "Tool.dll").write_text("MZ", encoding="utf-8")
        if goldens is not None:
            path = self.root.joinpath(*regen.GOLDENS_TEST)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(goldens, encoding="utf-8")
        if ruleset is not None:
            path = self.root.joinpath(*regen.RULESET_SOURCE)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(ruleset, encoding="utf-8")
        self.root.joinpath(*regen.DEFAULT_OUT).mkdir(parents=True, exist_ok=True)
        (self.root / "Directory.Build.props").write_text("<Project/>", encoding="utf-8")

    def cleanup(self) -> None:
        self._tmp.cleanup()


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        for name, original in _PRISTINE.items():
            if getattr(regen, name) is not original:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(regen, name)!r}")


class TheLiveConfig(SeamGuard):
    """Sorted NUMERICALLY. This repository has a v10, and a lexical sort puts v9 above it."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_it_picks_the_HIGHEST_NUMERIC_version_not_the_lexically_last(self) -> None:
        self.assertEqual(regen.live_tuning(self.ground.root).name, "aptitudes.v10.json")

    def test_a_LEXICAL_order_would_pick_a_DIFFERENT_file(self) -> None:
        """The counterweight: a lexical sort really does choose differently, so this is a real hazard
        and not a tautology."""
        names = sorted(TUNING)
        self.assertEqual(names[-1], "aptitudes.v9.json",
                         "lexical order would pick v9; the numeric rule must pick v10")

    def test_NO_config_REFUSES_and_says_it_is_never_pinned(self) -> None:
        ground = Ground(tuning={})
        self.addCleanup(ground.cleanup)
        with self.assertRaises(regen.Refusal) as caught:
            regen.live_tuning(ground.root)
        self.assertEqual(caught.exception.reason, "NO-TUNING-CONFIG")
        self.assertIn("never pinned", caught.exception.detail)

    def test_a_file_that_is_NOT_aptitudes_is_ignored(self) -> None:
        tuning = self.ground.root / "data" / "tuning"
        (tuning / "combat.v99.json").write_text("{}", encoding="utf-8")
        # These two are the ones the ANCHORS reject and a bare substring would accept. The original
        # fixture (`combat.v99.json`) matched neither form, so deleting the anchors changed nothing and
        # the case passed -- the anchors were untested. `old-aptitudes.v5.json` and
        # `aptitudes.v99.json.bak` would each be a silent staleness bug if accepted as the live config.
        (tuning / "old-aptitudes.v5.json").write_text(json.dumps({"version": 5}), encoding="utf-8")
        (tuning / "aptitudes.v99.json.bak").write_text(json.dumps({"version": 99}), encoding="utf-8")
        (tuning / "aptitudes.archived.v50.json").write_text(json.dumps({"version": 50}),
                                                            encoding="utf-8")
        self.assertEqual(regen.live_tuning(self.ground.root).name, "aptitudes.v10.json")

    def test_a_DOUBLE_EXTENSION_the_GLOB_admits_is_rejected_by_the_ANCHORS(self) -> None:
        """The only fixture that actually exercises the anchors.

        Every other decoy is removed by the GLOB `aptitudes.v*.json` before the pattern is consulted --
        measured, after two wrong guesses. This one is admitted by the glob (with `*` = `50.json5`) and
        rejected only by the `$`: the unanchored pattern matches the PREFIX, reads version 50, and wins
        over v10. The glob is the real filter and the anchors are belt-and-braces, so a mutant that
        removes them is NARROWLY different rather than equivalent, and this case is what shows it.
        """
        tuning = self.ground.root / "data" / "tuning"
        (tuning / "aptitudes.v50.json5.json").write_text(json.dumps({"version": 50}),
                                                         encoding="utf-8")
        # The decoy IS reachable through the glob, or the case would prove nothing.
        self.assertIn(tuning / "aptitudes.v50.json5.json",
                      list(tuning.glob("aptitudes.v*.json")),
                      "the decoy must be one the glob actually enumerates")
        self.assertEqual(regen.live_tuning(self.ground.root).name, "aptitudes.v10.json")


class ThePreconditions(SeamGuard):
    """`dotnet run --no-build` on a configuration with no binaries is the original's own documented
    failure, and it surfaced as `The system cannot find the file specified` from inside `dotnet`."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_a_TOOL_that_was_NEVER_BUILT_REFUSES_naming_the_CONFIGURATION(self) -> None:
        ground = Ground(built=False)
        self.addCleanup(ground.cleanup)
        with self.assertRaises(regen.Refusal) as caught:
            regen.require_built(ground.root, regen.DOMINANCE_TOOL, "Debug")
        self.assertEqual(caught.exception.reason, "TOOL-NOT-BUILT")
        self.assertIn("Debug", caught.exception.detail)
        self.assertIn("--no-build", caught.exception.detail)

    def test_the_built_configuration_is_FOUND_and_no_TFM_is_hardcoded(self) -> None:
        """A TFM bump must not require editing this file, so the probe globs `net*`."""
        old = self.ground.root / "tools" / "CombatSim" / "bin" / "Debug" / "net8.0" / "Tool.dll"
        old.unlink()
        new = self.ground.root / "tools" / "CombatSim" / "bin" / "Debug" / "net9.0"
        new.mkdir(parents=True)
        (new / "Tool.dll").write_text("MZ", encoding="utf-8")
        regen.require_built(self.ground.root, regen.COMBATSIM, "Debug")

    def test_a_MISSING_dotnet_REFUSES(self) -> None:
        with mock.patch.object(regen, "_WHICH", return_value=None):
            with self.assertRaises(regen.Refusal) as caught:
                regen.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_a_MISSING_archetype_build_REFUSES(self) -> None:
        (self.ground.root / "tools" / "CombatSim" / "builds" / "finesse.json").unlink()
        with tempfile.TemporaryDirectory() as scratch:
            with self.assertRaises(regen.Refusal) as caught:
                regen.write_element_scratch(self.ground.root, Path(scratch))
        self.assertEqual(caught.exception.reason, "ARCHETYPE-BUILD-MISSING")
        self.assertIn("finesse", caught.exception.detail)

    def test_the_element_mapping_is_applied_to_the_SCRATCH_only(self) -> None:
        """The tracked builds/*.json are all `fire`; writing them would destroy the fixture the
        elements-live measurement depends on."""
        with tempfile.TemporaryDirectory() as scratch:
            paths = regen.write_element_scratch(self.ground.root, Path(scratch))
            # Read INSIDE the block. The first version read after it, so the files it asserted on had
            # already been deleted and the case failed for a reason that had nothing to do with the tool.
            for name, element in regen.ELEMENT_BY_ARCHETYPE.items():
                scratch_doc = json.loads(Path(dict(zip(regen.ARCHETYPE_ORDER, paths))[name])
                                         .read_text(encoding="utf-8"))
                tracked = json.loads(
                    (self.ground.root / "tools" / "CombatSim" / "builds" / f"{name}.json")
                    .read_text(encoding="utf-8"))
                # The LITERAL, not `regen.ELEMENT_BY_ARCHETYPE[name]`. Falsification found the
                # dict-reading version: a mutant that edits the module's own mapping satisfies a
                # comparison against itself, and the measured input silently becomes all-fire.
                self.assertEqual(scratch_doc["element"], element)
                self.assertEqual(scratch_doc["element"],
                                 {"force": "fire", "finesse": "air", "bastion": "earth"}[name],
                                 "the mapping is a measured input (P8.1), not a value the test reads "
                                 "back from the tool it is testing")
                self.assertEqual(tracked["element"], "fire", f"the tracked {name}.json was written to")
                self.assertEqual(scratch_doc["archetype"], tracked["archetype"],
                                 "only `element` may differ")


class TheBounds(SeamGuard):
    """Every `dotnet run` bounded, and its exit code measured on the call itself -- the original read
    `$LASTEXITCODE` after a `| Out-Null`."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)
        self.seen: list[dict] = []

    def drive(self, label: str) -> None:
        """One bounded call, recorded. `in` on the argument LIST is exact membership, so the label is
        matched against a joined string -- the first version used `label in cmd` and never matched."""
        report = regen.Report()

        def run(cmd, **kwargs):
            self.seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(regen, "_RUN", run):
            regen.run_tool("dotnet", self.ground.root, self.ground.root, "Debug", [label, "--out", "x"],
                           77, label, report)

    def test_EVERY_call_carries_a_timeout_and_capture_and_its_OWN_exit_code(self) -> None:
        self.drive("predict")
        self.assertEqual(len(self.seen), 1)
        call = self.seen[0]
        self.assertIsNotNone(call["kwargs"].get("timeout"))
        self.assertIsNotNone(call["kwargs"].get("capture_output"))
        self.assertIn("--no-build", call["cmd"], "every invocation must stay --no-build")
        self.assertIn("--no-restore", call["cmd"])
        self.assertNotIn("|", " ".join(call["cmd"]), "the exit code must not be read after a pipe")

    def test_a_NON_ZERO_exit_REFUSES_and_passes_the_tool_output_through(self) -> None:
        report = regen.Report()
        with mock.patch.object(regen, "_RUN", lambda cmd, **kw: subprocess.CompletedProcess(
                cmd, 3, "", "CS1002: ; expected")):
            with self.assertRaises(regen.Refusal) as caught:
                regen.run_tool("dotnet", self.ground.root, self.ground.root, "Debug", ["predict"], 5,
                               "CombatSim predict", report)
        self.assertEqual(caught.exception.reason, "TOOL-FAILED")
        self.assertIn("CS1002", caught.exception.detail)
        self.assertIn("CombatSim predict", caught.exception.detail)

    def test_a_TIMEOUT_REFUSES_rather_than_hanging(self) -> None:
        report = regen.Report()
        with mock.patch.object(regen, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 5)):
            with self.assertRaises(regen.Refusal) as caught:
                regen.run_tool("dotnet", self.ground.root, self.ground.root, "Debug", ["predict"], 5,
                               "CombatSim predict", report)
        self.assertEqual(caught.exception.reason, "TOOL-TIMED-OUT")
        self.assertTrue(report.tool_runs[0]["timedOut"])
        self.assertIsNone(report.tool_runs[0]["exit"])


class TheFloatSpelling(SeamGuard):
    """`E-06` not `e-06`, because these files are diff targets. Three chunk shapes, all three handled."""

    def payload(self) -> dict:
        return {"a": 6.919219087686557e-06, "s": "value 1e-5 inside prose", "i": 5, "n": None,
                "list": [1.2345e-17, 9.87654321e+25], "nested": {"deep": 1e-300}, "plain": 1.5}

    def text(self) -> str:
        return regen.dumps_powershell_floats(self.payload())

    def test_a_DICT_FLOAT_is_UPPERCASED(self) -> None:
        self.assertIn('"a": 6.919219087686557E-06', self.text())

    def test_a_LIST_FLOAT_is_UPPERCASED(self) -> None:
        """The anchored version of this pattern rewrote dict floats and silently skipped every list
        float -- two shapes right out of three is how a uniformity transform stops being uniform."""
        self.assertIn("1.2345E-17", self.text())
        self.assertIn("9.87654321E+25", self.text())

    def test_a_NESTED_FLOAT_is_UPPERCASED(self) -> None:
        self.assertIn('"deep": 1E-300', self.text())

    def test_a_number_INSIDE_a_STRING_is_never_touched(self) -> None:
        self.assertIn('"s": "value 1e-5 inside prose"', self.text())

    def test_INT_null_and_expONENT_FREE_floats_are_untouched(self) -> None:
        text = self.text()
        self.assertIn('"i": 5', text)
        self.assertIn('"n": null', text)
        self.assertIn('"plain": 1.5', text)

    def test_the_document_ROUND_TRIPS(self) -> None:
        self.assertEqual(json.loads(self.text()), self.payload())

    def test_it_REFUSES_rather_than_writing_a_document_it_cannot_confirm(self) -> None:
        with mock.patch.object(regen, "json") as fake:
            fake.JSONEncoder = json.JSONEncoder
            fake.loads = lambda text: {"deliberately": "different"}
            with self.assertRaises(regen.Refusal) as caught:
                regen.dumps_powershell_floats({"a": 1.5e-06})
        self.assertEqual(caught.exception.reason, "BASELINE-UNREADABLE")


class TheGoldens(SeamGuard):
    """Extracted, never retyped -- and a rename is named rather than silently producing a stale file."""

    def setUp(self) -> None:
        self.ground = Ground()
        self.addCleanup(self.ground.cleanup)

    def test_the_FOUR_consts_and_the_RULESET_VERSION_are_read_from_the_CSHARP(self) -> None:
        goldens = regen.extract_goldens(self.ground.root)
        self.assertEqual(goldens["rulesetVersion"], 5)
        self.assertEqual(set(goldens), {"rulesetVersion", "stompHash", "closeHash", "wipeHash",
                                        "seedSweepHash"})
        self.assertEqual(goldens["stompHash"],
                         "AF6A4F0C785DB30CB465A356B9F58D3C626AD85AB5827B74B33D1D023D0A59CD")

    def test_the_RULESET_VERSION_comes_from_the_const_not_a_COMMENT(self) -> None:
        """`BattleGoldenTests.cs` accumulates one prose paragraph per historical re-bless, so a comment
        search matches the earliest one. The fixture's comment says 3 and the const says 5."""
        goldens = regen.extract_goldens(self.ground.root)
        self.assertEqual(goldens["rulesetVersion"], 5)

    def test_a_RENAMED_const_REFUSES_by_NAME(self) -> None:
        ground = Ground(goldens=GOLDENS_CS.replace("StompHash", "StompDigest"))
        self.addCleanup(ground.cleanup)
        with self.assertRaises(regen.Refusal) as caught:
            regen.extract_goldens(ground.root)
        self.assertEqual(caught.exception.reason, "HASH-CONST-MISSING")
        self.assertIn("StompHash", caught.exception.detail)

    def test_a_MISSING_RULESET_VERSION_REFUSES(self) -> None:
        ground = Ground(ruleset="public static class BattleRuleset { }")
        self.addCleanup(ground.cleanup)
        with self.assertRaises(regen.Refusal) as caught:
            regen.extract_goldens(ground.root)
        self.assertEqual(caught.exception.reason, "RULESET-VERSION-MISSING")

    def test_a_MISSING_SOURCE_REFUSES(self) -> None:
        ground = Ground(goldens=None)
        self.addCleanup(ground.cleanup)
        with self.assertRaises(regen.Refusal) as caught:
            regen.extract_goldens(ground.root)
        self.assertEqual(caught.exception.reason, "SOURCE-MISSING")


class TheNotes(SeamGuard):
    """The prose IS content. Three quirks of the original, reproduced byte-for-byte."""

    def test_the_quirks_the_original_actually_EMITTED_are_reproduced(self) -> None:
        note = regen.tuning_sync_note("data/tuning/aptitudes.v10.json")
        self.assertIn("loses only to Pierce). chains above is ALSO fresh", note,
                      "the markdown backticks PowerShell ate must stay eaten")
        self.assertNotIn("`chains`", note)
        self.assertIn("way dominanceMatrix/ dominantCorners are overlaid", note,
                      "the line-wrap space is part of the committed text")
        self.assertIn('stays "neutral" deliberately', note,
                      '`"neutral`" survived as double quotes; the port must not switch it to backticks')
        self.assertNotIn("`neutral`", note)

    def test_the_NOTE_names_the_LIVE_config_it_measured(self) -> None:
        for note in (regen.tuning_sync_note("data/tuning/aptitudes.v99.json"),
                     regen.residual_conditions("data/tuning/aptitudes.v99.json"),
                     regen.dominance_conditions("data/tuning/aptitudes.v99.json")):
            self.assertIn("data/tuning/aptitudes.v99.json", note,
                          "a note that does not name the config it measured is how the staleness this "
                          "tool exists to prevent happened once already")
        self.assertNotIn("aptitudes.v10", regen.tuning_sync_note("data/tuning/aptitudes.v99.json"))

    def test_the_SEEDS_and_THETA_are_FIXED_and_stated(self) -> None:
        self.assertIn(f"seed {regen.PREDICT_SEED}", regen.residual_conditions("x"))
        self.assertIn(f"seed {regen.TRINITY_SEED}", regen.dominance_conditions("x"))
        self.assertIn(f"Theta={regen.THETA}", regen.residual_conditions("x"))


class TheWrittenShape(SeamGuard):
    def test_LF_two_space_indent_raw_non_ascii_and_NO_BOM(self) -> None:
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "b.json"
            regen.write_json(path, {"a": 1, "b": {"c": "em—dash"}})
            raw = path.read_bytes()
        self.assertNotIn(b"\r\n", raw, "CRLF is a Windows artifact; .gitattributes stores LF")
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "a BOM differs per PowerShell host; there is "
                                                           "no host here")
        self.assertIn("\n  \"b\": {", raw.decode("utf-8"))
        self.assertIn("em—dash", raw.decode("utf-8"))

    def test_measuredAt_is_SEVEN_fractional_digits_and_a_Z(self) -> None:
        """`(Get-Date).ToUniversalTime().ToString("o")`, which the determinism tests then strip."""
        stamp = regen.measured_at()
        self.assertRegex(stamp, r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$")
        self.assertEqual(len(stamp.split(".")[1]), 8, "seven digits plus the trailing Z")

    def test_model_is_REWRITTEN_repo_relative_with_forward_slashes(self) -> None:
        root = Path("D:/a/repo")
        doc = regen.add_meta({"model": "D:\\a\\repo\\data\\tuning\\aptitudes.v10.json"}, root, "why")
        self.assertEqual(doc["model"], "data/tuning/aptitudes.v10.json")
        self.assertEqual(set(doc["_meta"]), {"measuredAt", "conditions"})

    def test_a_doc_with_NO_model_is_left_alone(self) -> None:
        doc = regen.add_meta({"rulesetVersion": 5}, Path("D:/a/repo"), "why")
        self.assertNotIn("model", doc)


class TheScratch(SeamGuard):
    """The original wrote its scratch under the OUTPUT directory -- by default a TRACKED one -- and
    removed it with a bare `Remove-Item` placed after the simulator call, not in a `finally`."""

    def test_the_scratch_is_never_inside_the_output_directory(self) -> None:
        self.assertNotIn("_scratch", regen.dumps_powershell_floats({"x": 1}))
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("tempfile.mkdtemp", source)
        self.assertNotRegex(source, r'out_dir\s*/\s*"_scratch',
                            "the scratch must not be written into the output tree")

    def test_a_scratch_that_WILL_NOT_DELETE_is_REPORTED_and_FAILS_the_run(self) -> None:
        ground = Ground()
        self.addCleanup(ground.cleanup)

        def run(cmd, **kwargs):
            if "predict" in cmd:
                return subprocess.CompletedProcess(cmd, 1, "", "boom")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        report = regen.Report(out_dir=str(ground.out))
        with mock.patch.object(regen, "_RUN", run):
            with mock.patch.object(regen, "_WHICH", return_value="C:\\dotnet.exe"):
                with mock.patch.object(regen, "_RMTREE", side_effect=OSError("file in use")):
                    with self.assertRaises(regen.Refusal):
                        regen.execute(ground.root, ground.out, "Debug", 5, 5, report)
        self.assertIs(report.scratch_removed, False, "a failed delete was swallowed")
        self.assertTrue(any("could not be removed" in r for r in report.reasons))

    def test_a_CLEAN_run_leaves_NO_scratch_directory_behind_on_DISK(self) -> None:
        """Found by falsification: a mutant that skips the delete entirely left
        `scratch_removed` at None -- which is `is not False`, so the run still reported OK.

        The assertion belongs on the FILESYSTEM, not on the flag: the path handed to the delete is
        recorded and then required to be absent. `scratch_removed` is what the tool REPORTS; the
        directory is what the tool DID.
        """
        ground = Ground()
        self.addCleanup(ground.cleanup)
        report = regen.Report(out_dir=str(ground.out))
        deleted: list[Path] = []

        def run(cmd, **kwargs):
            out = cmd[cmd.index("--out") + 1]
            joined = " ".join(cmd)
            payload = ({"dominanceMatrix": [[1]], "dominantCorners": []}
                       if "DominanceBaseline" in joined else
                       {"model": "x", "arrows": [], "coverage": {}})
            Path(out).write_text(json.dumps(payload), encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        real_rmtree = regen._RMTREE

        def recording_rmtree(path, *args, **kwargs):
            deleted.append(Path(path))
            return real_rmtree(path, *args, **kwargs)

        with mock.patch.object(regen, "_RUN", run):
            with mock.patch.object(regen, "_WHICH", return_value="C:\\dotnet.exe"):
                with mock.patch.object(regen, "_RMTREE", recording_rmtree):
                    regen.execute(ground.root, ground.out, "Debug", 5, 5, report)
        self.assertTrue(deleted, "the run deleted nothing at all, so this proves nothing")
        for path in deleted:
            self.assertFalse(path.exists(), f"the scratch directory survived: {path}")
        self.assertIs(report.scratch_removed, True)

    def test_a_CLEAN_run_removes_the_scratch_and_is_OK(self) -> None:
        ground = Ground()
        self.addCleanup(ground.cleanup)
        report = regen.Report(out_dir=str(ground.out))

        def run(cmd, **kwargs):
            out = cmd[cmd.index("--out") + 1]
            # Substring on the JOINED command: `in` on a list of arguments is EXACT MEMBERSHIP, so the
            # first version's test never matched and every call got the trinity payload.
            joined = " ".join(cmd)
            payload = ({"dominanceMatrix": [[1]], "dominantCorners": []}
                       if "DominanceBaseline" in joined else
                       {"model": "x", "arrows": [], "coverage": {}})
            Path(out).write_text(json.dumps(payload), encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(regen, "_RUN", run):
            with mock.patch.object(regen, "_WHICH", return_value="C:\\dotnet.exe"):
                regen.execute(ground.root, ground.out, "Debug", 5, 5, report)
        self.assertIs(report.scratch_removed, True)
        self.assertEqual(len(report.written), 3)
        for path in report.written:
            self.assertTrue(Path(path).is_file(), path)
            self.assertIn("_meta", json.loads(Path(path).read_text(encoding="utf-8")))
        self.assertEqual(report.live_tuning_rel, "data/tuning/aptitudes.v10.json")

    def test_a_core_baseline_with_NO_dominanceMatrix_REFUSES(self) -> None:
        """It is the production resolver's own output; its absence is a tool defect, not a gap to carry
        into a committed baseline."""
        ground = Ground()
        self.addCleanup(ground.cleanup)
        report = regen.Report(out_dir=str(ground.out))

        def run(cmd, **kwargs):
            out = cmd[cmd.index("--out") + 1]
            payload = {} if "DominanceBaseline" in " ".join(cmd) else {"coverage": {}}
            Path(out).write_text(json.dumps(payload), encoding="utf-8")
            return subprocess.CompletedProcess(cmd, 0, "", "")

        with mock.patch.object(regen, "_RUN", run):
            with mock.patch.object(regen, "_WHICH", return_value="C:\\dotnet.exe"):
                with self.assertRaises(regen.Refusal) as caught:
                    regen.execute(ground.root, ground.out, "Debug", 5, 5, report)
        self.assertEqual(caught.exception.reason, "CORE-BASELINE-UNREADABLE")
        self.assertIn("dominanceMatrix", caught.exception.detail)


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - regen.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - regen.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({regen.EXIT_PASSED, regen.EXIT_FAILED, regen.EXIT_REFUSED}, {0, 1, 64})

    def test_a_REFUSAL_exits_64_and_names_its_REASON_in_json(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = regen.main(["--sim-timeout", "0", "--json"])
        self.assertEqual(code, regen.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_is_non_zero_and_NAMED_on_stderr(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = regen.main(["--tool-timeout", "-1"])
        self.assertEqual(code, regen.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())
        self.assertIn("INVALID-TIMEOUT", err.getvalue())

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--out-dir", "--configuration", "--sim-timeout", "--tool-timeout",
                     "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-OutDir", "-Configuration", "-Root"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_does_NOT_change_the_process_WORKING_DIRECTORY(self) -> None:
        """`Push-Location $Root` changed the cwd for every other caller in the same process."""
        self.assertNotIn("os.chdir", SCRIPT.read_text(encoding="utf-8"))
        self.assertNotIn("chdir(", SCRIPT.read_text(encoding="utf-8"))

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("regen-class-system-baselines.ps1", head)
        lowered = head.lower()
        for reason in ("scratch", "no-build", "bounded", "host", "depth"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_SEEDS_are_CONSTANTS_not_typed_at_the_call_site(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        for seed in (str(regen.PREDICT_SEED), str(regen.TRINITY_SEED), str(regen.THETA)):
            self.assertIn(seed, source, seed)
        self.assertEqual(len(re.findall(r'"--seed",\s*str\(', source)), 2,
                         "both simulator invocations must take their seed from the constant")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "regen":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_NO_case_STARTS_a_patch_it_cannot_STOP(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned = []
        for cls in (n for n in ast.walk(tree) if isinstance(n, ast.ClassDef)):
            for func in (n for n in cls.body if isinstance(n, ast.FunctionDef)
                         and n.name.startswith("test")):
                for node in ast.walk(func):
                    if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                            and node.func.attr in ("start", "stop")):
                        owned = any(node in ast.walk(stmt)
                                    for parent in ast.walk(func)
                                    if isinstance(parent, (ast.With, ast.AsyncWith))
                                    for stmt in parent.body)
                        if not owned:
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}")
        self.assertEqual(unowned, [], "\n".join(unowned))


if __name__ == "__main__":
    unittest.main()
