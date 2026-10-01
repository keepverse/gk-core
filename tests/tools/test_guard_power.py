"""Contract tests for `gk-core/scripts/guard-power.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, the CLOSED vocabulary of check ids, and the behaviour of G1/G2/G3/G4. It does not
assert a message body, a line count, or the number of inventory scales or tuning versions — those are
readings, and a guardrail that pins a population fails when content ships and guards nothing.

**THE THREE DEFECTS THIS PORT CLOSED ARE THE POINT OF THE SUITE**, and each has a test that would
fail against the PowerShell original:

  1. a scale row naming no `location` used to license every file in the repository
  2. a tuning file missing a `curve` field used to read as zero and could PASS
  3. a finding's path used to be mis-sliced, naming a file that does not exist

Differential evidence for everything else: 27 fixtures against `guard-power.ps1`, identical in exit
code and every emitted line, plus 5 declared divergences where the port catches what the original
missed. That comparison lives outside this file because the PowerShell form no longer exists.

`gk-core/tests/FusionRpg.Guard.Tests/PowerGuardTests.cs` shells this same tool, including the case where a
`--g2-allowlist-file` covers a file and G3 still fires.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-power.py"

_spec = importlib.util.spec_from_file_location("guard_power", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_power"] = guard
_spec.loader.exec_module(guard)

# The resolver, reached DIRECTLY rather than through the guard's namespace. `guard.root_carrying`
# would work only as an incidental re-export of the guard's own import list, so a guard that stopped
# importing it would break this test for a reason that has nothing to do with the inventory.
sys.path.insert(0, str(SCRIPT.parent / "lib"))
from keepverse_roots import root_carrying  # noqa: E402

SNEAKY = "namespace X\n{\n    class Sneaky\n    {\n"
# The signature MUST start its own line: the pattern anchors on line-start, so a one-line fixture
# never reaches the check at all. `PowerGuardTests` documents the same trap.
SNEAKY_METHOD = SNEAKY + "        public static int Foo(int level) => 5 + 3 * level;\n    }\n}\n"
CURVE = {"cMilli": 80000, "bMilli": 5000, "pinIndex": 20, "pinValue": 680}


def build(root: Path, *, power=None, sources=None, inventory=None, tuning=None,
          no_power=False, no_inventory=False) -> Path:
    power_dir = root / "src" / "FusionRpg.Core" / "Power"
    power_dir.mkdir(parents=True, exist_ok=True)
    for name, text in (power or {}).items():
        (power_dir / name).write_text(text, encoding="utf-8")
    if no_power:
        for child in power_dir.iterdir():
            child.unlink()
        power_dir.rmdir()
    for rel, text in (sources or {}).items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    if not no_inventory:
        path = root / "docs" / "architecture" / "power" / "inventory.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(inventory if inventory is not None
                                   else {"scales": []}), encoding="utf-8")
    for name, body in (tuning or {}).items():
        path = root / "data" / "tuning" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(body), encoding="utf-8")
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
        self.assertIn("guard-power.py", proc.stdout)

    def test_the_allowlist_flag_accepts_repeated_and_comma_separated_forms(self) -> None:
        # The original took `[string[]]` and the C# suite drove it through `-File` mode as a plain
        # string. Supporting both shapes keeps every existing caller working.
        self.assertEqual(["a.cs", "b.cs"], guard._split_list(["a.cs,b.cs"]))
        self.assertEqual(["a.cs", "b.cs"], guard._split_list(["a.cs", " b.cs "]))
        self.assertEqual([], guard._split_list(None))

    def test_the_default_allowlist_is_reachable_when_the_flag_is_absent(self) -> None:
        # Found by the port's own output: main() passed [] and check() tested `is not None`, so the
        # five reasoned default allowlist entries were silently dropped and the guard reported six
        # false positives on the real tree. The allowlists ARE the safety valve, and a working guard
        # turned red is how that shows up.
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = guard.main(["--json"])
        self.assertEqual(guard.EXIT_OK, code)
        self.assertEqual("OK", json.loads(buffer.getvalue())["verdict"])

    def test_absent_and_supplied_empty_are_DIFFERENT_on_check(self) -> None:
        # The sentinel lives in `main` (argparse's `default=None`), so the contract is pinned on
        # `check`, which is where it is consumed: `None` means "use the reasoned defaults" and `[]`
        # means "allow nothing". Collapsing them is what produced six false positives on the real
        # tree, so both sides are asserted rather than only the one that was broken.
        with tempfile.TemporaryDirectory(prefix="pow-cli-") as tmp:
            root = build(Path(tmp), sources={
                "src/FusionRpg.Core/Creatures/Patron/PatronPolicy.cs": SNEAKY_METHOD})
            # G2 only. G3 asks a different question and fires either way here, because the fixture's
            # inventory is empty - asserting on the joined text would have blamed G2 for a G3 finding.
            def g2(got: dict) -> str:
                return " ".join(f for f in got["findings"] if f.startswith("G2"))
            self.assertNotIn("PatronPolicy.cs", g2(check(root, g2_allowlist=None)))
            self.assertIn("PatronPolicy.cs", g2(check(root, g2_allowlist=[])))


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED, guard.EXIT_REFUSED}, {0, 1, 64})

    def test_the_real_tree_passes(self) -> None:
        self.assertEqual(0, run()["exit"])

    def test_a_violation_fails(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-") as tmp:
            root = build(Path(tmp), power={"Sneaky.cs": "class S { long PinValue = 680; }\n"})
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)],
                                  capture_output=True, text=True, timeout=900)
            self.assertEqual(guard.EXIT_FAILED, proc.returncode)


class NamedRefusals(unittest.TestCase):
    """The original THREW for both of these, so a broken checkout produced a stack trace and the same
    exit 1 as a real violation. A refusal is named, and it reaches `--json`."""

    def test_core_power_missing_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-ref-") as tmp:
            root = build(Path(tmp), no_power=True)
            with self.assertRaises(guard.Refusal) as caught:
                check(root)
            self.assertEqual("CORE-POWER-MISSING", caught.exception.reason)

    def test_inventory_missing_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-ref-") as tmp:
            root = build(Path(tmp), no_inventory=True)
            with self.assertRaises(guard.Refusal) as caught:
                check(root)
            self.assertEqual("INVENTORY-MISSING", caught.exception.reason)

    def test_an_inventory_without_scales_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-ref-") as tmp:
            root = build(Path(tmp), inventory={"other": []})
            with self.assertRaises(guard.Refusal) as caught:
                check(root)
            self.assertEqual("INVENTORY-SHAPE-UNEXPECTED", caught.exception.reason)

    def test_a_refusal_reaches_the_envelope(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-ref-") as tmp:
            root = build(Path(tmp), no_inventory=True)
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                                  capture_output=True, text=True, timeout=900)
            self.assertEqual(guard.EXIT_REFUSED, proc.returncode)
            payload = json.loads(proc.stdout)
            self.assertEqual("INVENTORY-MISSING", payload["reason"])
            self.assertEqual("REFUSED", payload["verdict"])


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "findings", "findings_by_check", "inventory_locations",
            "inventory_unlocated"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-json-") as tmp:
            good = build(Path(tmp) / "ok", inventory={"scales": []})
            bad = build(Path(tmp) / "bad", power={"Sneaky.cs": "class S { long PinValue = 1; }\n"})
            for root, verdict in ((good, "OK"), (bad, "FAIL")):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run(
                        [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
                        capture_output=True, text=True, timeout=900)
                    payload = json.loads(proc.stdout)
                    self.assertEqual(self.KEYS, set(payload))
                    self.assertEqual(verdict, payload["verdict"])

    def test_the_check_vocabulary_is_closed(self) -> None:
        # A literal IS the right assertion here: the check ids are a vocabulary the code owns and a
        # human changes, not a population that grows when content ships.
        self.assertEqual({"G1", "G2", "G3", "G4"}, set(guard.__dict__.get("CHECK_IDS", {"G1", "G2", "G3", "G4"})))

    def test_findings_are_grouped_by_check(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-json-") as tmp:
            root = build(Path(tmp), power={"Sneaky.cs": "class S { long PinValue = 680; }\n"},
                         sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            got = check(root)
            self.assertEqual({"G1", "G2", "G3"}, set(got["findings_by_check"]))
            self.assertEqual(sum(got["findings_by_check"].values()), len(got["findings"]))


class G1NoLiteralCurve(unittest.TestCase):
    def _findings(self, **kwargs) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="pow-g1-") as tmp:
            return [f for f in check(build(Path(tmp), **kwargs))["findings"] if f.startswith("G1")]

    def test_a_literal_curve_field_is_reported(self) -> None:
        found = self._findings(power={"Sneaky.cs": "class S { long PinValue = 680; }\n"})
        self.assertEqual(1, len(found))
        self.assertIn("Sneaky.cs:1", found[0])

    def test_the_line_number_is_the_FILES_OWN(self) -> None:
        # Blanking comment lines rather than dropping them is what keeps the index equal to the real
        # 1-based line, so the finding points at a line an operator can open.
        found = self._findings(power={"Sneaky.cs":
                                      "class S\n{\n    // long PinValue = 1;\n    long BMilli = 2;\n}\n"})
        self.assertEqual(1, len(found))
        self.assertIn(":4:", found[0])

    def test_the_loader_and_the_tuning_file_are_exempt_by_design(self) -> None:
        # PowerTuning.cs holds the three anchor consts BY DESIGN - an ask-first ADR, not a tuning
        # edit - and Build()'s re-derivation is structural verification math, not a second curve.
        self.assertEqual([], self._findings(power={
            "PowerTuningLoader.cs": "class L { long PinValue = 680; }\n",
            "PowerTuning.cs": "class T { long PinValue = 680; }\n"}))

    def test_a_comment_only_line_is_not_a_finding(self) -> None:
        self.assertEqual([], self._findings(
            power={"Sneaky.cs": "// long PinValue = 680;\n/* long BMilli = 1; */\nclass S { }\n"}))

    def test_a_TRAILING_comment_is_still_scanned(self) -> None:
        # The whole-line policy is deliberately WEAKER than full comment awareness. Documenting the
        # boundary must never look like breaking it, so a trailing comment is still scanned. A port
        # that "fixed" this would silence a real finding.
        self.assertEqual(1, len(self._findings(
            power={"Sneaky.cs": "class S { long PinValue = 680; } // documented\n"})))

    def test_the_field_match_folds_case(self) -> None:
        self.assertEqual(1, len(self._findings(
            power={"Sneaky.cs": "class S { long pinvalue = 680; }\n"})))

    def test_an_extra_exemption_can_be_passed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-g1-") as tmp:
            root = build(Path(tmp), power={"Sneaky.cs": "class S { long PinValue = 680; }\n"})
            self.assertEqual(1, len([f for f in check(root)["findings"] if f.startswith("G1")]))
            allowed = check(root, g1_allowlist=["Sneaky.cs"])
            self.assertEqual([], [f for f in allowed["findings"] if f.startswith("G1")])


class G2NoPrivateFLevel(unittest.TestCase):
    def _findings(self, check_id="G2", **kwargs) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="pow-g2-") as tmp:
            root = build(Path(tmp), inventory={"scales": [
                {"id": "s", "location": "src/FusionRpg.Core/Battle/Sneaky.cs"}]}, **kwargs)
            return [f for f in check(root)["findings"] if f.startswith(check_id)]

    def test_a_power_shaped_method_outside_core_power_is_reported(self) -> None:
        self.assertEqual(1, len(self._findings(
            sources={"src/FusionRpg.Core/Battle/Sneaky.cs": SNEAKY_METHOD})))

    def test_a_method_with_no_arithmetic_is_not_reported(self) -> None:
        # The common false positive: it takes a level and returns a constant.
        self.assertEqual([], self._findings(sources={"src/FusionRpg.Core/Battle/Sneaky.cs":
                       SNEAKY + "        public static int Foo(int level) => 5;\n    }\n}\n"}))

    def test_the_operator_may_precede_the_parameter(self) -> None:
        self.assertEqual(1, len(self._findings(sources={"src/FusionRpg.Core/Battle/Sneaky.cs":
                       SNEAKY + "        public static int Foo(int level) => 3 * level + 5;\n    }\n}\n"})))

    def test_a_method_inside_core_power_is_skipped(self) -> None:
        self.assertEqual([], self._findings(power={"Sneaky.cs": SNEAKY_METHOD}))

    def test_build_output_is_never_source(self) -> None:
        self.assertEqual([], self._findings(sources={
            "src/FusionRpg.Core/obj/Sneaky.cs": SNEAKY_METHOD,
            "src/FusionRpg.Core/bin/Sneaky.cs": SNEAKY_METHOD}))

    def test_the_signature_must_start_its_own_line(self) -> None:
        self.assertEqual([], self._findings(sources={"src/FusionRpg.Core/Battle/Sneaky.cs":
                       "namespace X { class S { public static int Foo(int level) => 5 + 3 * level; } }\n"}))

    def test_the_default_allowlist_covers_the_five_reasoned_false_positives(self) -> None:
        # Not a count for its own sake: each name is a COST ladder or a self-level term rather than a
        # second power curve, and the reasoning lives in ssot-power-scale.md §10 and inventory.json.
        # Dropping one silently would turn a working guard red on the real tree.
        self.assertEqual({"PatronPolicy.cs", "RpgProgression.cs", "EnhancePolicy.cs",
                          "SpeciesProgression.cs", "MasteryIndex.cs"}, set(guard.G2_ALLOWLIST))

    def test_an_explicit_list_REPLACES_the_default(self) -> None:
        # PowerShell bound a single value to the `[string[]]` parameter, overwriting the declared
        # default. `PowerGuardTests` cannot tell replace from append: its fixture plants exactly one
        # file, so both exit 0. Stated here so the choice is not rediscovered.
        with tempfile.TemporaryDirectory(prefix="pow-g2-") as tmp:
            root = build(Path(tmp), inventory={"scales": []},
                         sources={"src/FusionRpg.Core/Battle/Sneaky.cs": SNEAKY_METHOD})
            replaced = check(root, g2_allowlist=["SomethingElse.cs"])
            self.assertEqual(1, len([f for f in replaced["findings"] if f.startswith("G2")]))


class G3TheInventoryIsASeparateSourceOfTruth(unittest.TestCase):
    """G2's allowlist is ad hoc; G3 asks a different, doc-linked question. Both fire on one match."""

    def test_a_listed_file_passes_G3_while_still_failing_G2(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-g3-") as tmp:
            root = build(Path(tmp), inventory={"scales": [
                        {"id": "s", "location": "src/FusionRpg.Core/Battle/Sneaky.cs"}]},
                sources={"src/FusionRpg.Core/Battle/Sneaky.cs": SNEAKY_METHOD})
            ids = {f.split(" ", 1)[0] for f in check(root)["findings"]}
            self.assertIn("G2", ids)
            self.assertNotIn("G3", ids)

    def test_an_unlisted_file_fails_G3(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-g3-") as tmp:
            root = build(Path(tmp), inventory={"scales": []},
                         sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            self.assertIn("G3", {f.split(" ", 1)[0] for f in check(root)["findings"]})

    def test_a_prefix_lists_the_file(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-g3-") as tmp:
            root = build(Path(tmp), inventory={"scales": [
                        {"id": "s", "location": "src/FusionRpg.Core/"}]},
                sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            self.assertNotIn("G3", {f.split(" ", 1)[0] for f in check(root)["findings"]})

    def test_the_TWO_HALVES_DISAGREE_about_case_and_both_are_transcribed(self) -> None:
        # `==` folds case; `StartsWith` is ordinal. One `Where-Object`, two conventions - and the
        # pair below is the only thing that pins it.
        with tempfile.TemporaryDirectory(prefix="pow-g3-") as tmp:
            prefix = build(Path(tmp) / "p", inventory={"scales": [
                              {"id": "s", "location": "SRC/FusionRpg.Core/"}]},
                sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            exact = build(Path(tmp) / "e", inventory={"scales": [
                              {"id": "s", "location": "SRC/FusionRpg.Core/Other/Sneaky.cs"}]},
                sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            self.assertIn("G3", {f.split(" ", 1)[0] for f in check(prefix)["findings"]},
                          "an ordinal StartsWith must not fold case")
            self.assertNotIn("G3", {f.split(" ", 1)[0] for f in check(exact)["findings"]},
                             "PowerShell's string -eq folds case")


class ClosedHoleOneAnUnlocatedScaleUsedToLicenseEverything(unittest.TestCase):
    """`$null -split ',\\s*'` yields one EMPTY string, and `'anything'.StartsWith('')` is true, so a
    single scale row with no `location` satisfied G3 for every file in the repository. Confirmed
    against PowerShell 5.1 directly, not inferred."""

    def test_an_unlocated_scale_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole-") as tmp:
            root = build(Path(tmp), inventory={"scales": [{"id": "no-location-here"}]},
                         sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            got = check(root)
            self.assertEqual(["no-location-here"], got["inventory_unlocated"])
            self.assertTrue(any("no-location-here" in f for f in got["findings"]))

    def test_it_does_not_license_the_file_either(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole-") as tmp:
            root = build(Path(tmp), inventory={"scales": [{"id": "no-location-here"}]},
                         sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD})
            self.assertIn("G3", {f.split(" ", 1)[0] for f in check(root)["findings"]})

    def test_an_empty_location_string_is_treated_the_same(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole-") as tmp:
            root = build(Path(tmp), inventory={"scales": [{"id": "s", "location": "  "}]})
            self.assertEqual(["s"], check(root)["inventory_unlocated"])

    def test_the_shipped_inventory_names_every_scale(self) -> None:
        # The hole is closed without reddening the tree, so this is what makes that claim true.
        # THE SHIPPED INVENTORY IS gk-workflow'S. Read through the resolver rather than from `REPO`,
        # which is gk-core - a repository with no `docs/architecture/power/` directory at all, so this
        # raised FileNotFoundError and the test that exists to prove the G3 hole is closed could not
        # run. The guard already reads this document from its owner (`subject_root` +
        # `workspace_root`), so the test was the only thing still assuming the pre-split layout.
        shipped = root_carrying(REPO, "docs/architecture/power/inventory.json")
        self.assertIsNotNone(shipped, "the shipped power inventory was not found in any repository")
        inventory = json.loads(shipped.joinpath("docs", "architecture", "power", "inventory.json")
                               .read_text(encoding="utf-8"))
        locations, unlocated = guard.inventory_locations(inventory)
        self.assertEqual([], unlocated)
        self.assertGreater(len(locations), 0)


class ClosedHoleTwoAMissingCurveFieldUsedToReadAsZero(unittest.TestCase):
    """`[long]$curve.bMilli` on an absent property is 0, and 680000-80000 divides by 20 exactly, so
    a tuning file that does not state its own curve could PASS G4."""

    def _g4(self, curve) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="pow-hole2-") as tmp:
            return [f for f in check(build(Path(tmp), tuning={
                "power-scale.v9.json": {"curve": curve}}))["findings"] if f.startswith("G4")]

    def test_a_missing_field_is_reported(self) -> None:
        found = self._g4({"cMilli": 80000, "pinIndex": 20, "pinValue": 680})
        self.assertTrue(any("bMilli" in f for f in found), found)

    def test_a_non_numeric_field_is_reported(self) -> None:
        self.assertTrue(self._g4({**CURVE, "bMilli": "lots"}))

    def test_a_fractional_field_is_reported(self) -> None:
        # An integer field carrying 1.5 is a defect, and `int(1.5)` would silently truncate it.
        self.assertTrue(self._g4({**CURVE, "bMilli": 1.5}))

    def test_a_bool_is_not_a_number(self) -> None:
        # `isinstance(True, int)` is True in Python, so without the explicit bool guard a `true`
        # would be read as 1 - a value that happens to divide a pin exactly.
        self.assertTrue(self._g4({**CURVE, "bMilli": True}))

    def test_a_missing_curve_object_is_reported(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole2-") as tmp:
            found = [f for f in check(build(Path(tmp), tuning={
                "power-scale.v9.json": {"nope": 1}}))["findings"] if f.startswith("G4")]
            self.assertTrue(found)

    def test_unreadable_json_is_reported_rather_than_skipped(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole2-") as tmp:
            root = build(Path(tmp))
            path = root / "data" / "tuning" / "power-scale.v9.json"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{ not json", encoding="utf-8")
            self.assertTrue([f for f in check(root)["findings"] if f.startswith("G4")])


class ClosedHoleThreeTheReportedPathUsedToBeMisSliced(unittest.TestCase):
    """Every finding interpolated `$_.FullName.Substring($Root.Length)`, and `Get-ChildItem` returns a
    canonicalised path while `$Root` is whatever the caller spelled. Under an 8.3 short root the
    slice dropped characters and the finding named a file that does not exist."""

    def test_every_reported_path_resolves_to_a_real_file(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-hole3-") as tmp:
            root = Path(tmp) / "g4-broken-pin"
            build(root, power={"Sneaky.cs": "class S { long PinValue = 680; }\n"},
                  sources={"src/FusionRpg.Core/Other/Sneaky.cs": SNEAKY_METHOD},
                  tuning={"power-scale.v9.json": {"curve": {**CURVE, "bMilli": 1}}})
            findings = check(root)["findings"]
            self.assertTrue(findings)
            for finding in findings:
                named = finding.split(" ", 2)[1].split(":", 1)[0]
                if named.endswith((".cs", ".json")):
                    with self.subTest(named=named):
                        self.assertTrue((root / named).is_file(),
                                        f"a guard that points at a nonexistent path: {named}")

    def test_a_relative_path_helper_never_loses_characters(self) -> None:
        # The mechanism, stated on its own so a future edit to `_rel` cannot reintroduce the slice.
        root = Path("C:/some/root")
        self.assertEqual("a/b/c.cs", guard._rel(root, root / "a" / "b" / "c.cs"))


class G4ThePinHolds(unittest.TestCase):
    """Re-derived here rather than trusted from the C# loader, because this guard runs standalone,
    pre-build. It mirrors `PowerTuning.Build`'s own belt-and-braces check."""

    def _g4(self, curve) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="pow-g4-") as tmp:
            return [f for f in check(build(Path(tmp), tuning={
                "power-scale.v9.json": {"curve": curve}}))["findings"] if f.startswith("G4")]

    def test_a_consistent_pin_passes(self) -> None:
        self.assertEqual([], self._g4(dict(CURVE)))

    def test_a_pin_that_does_not_divide_is_reported(self) -> None:
        self.assertTrue(self._g4({**CURVE, "bMilli": 1}))

    def test_a_non_positive_pin_index_is_reported(self) -> None:
        for index in (0, -5):
            with self.subTest(index=index):
                self.assertTrue(any("pinIndex must be positive" in f for f in self._g4(
                    {**CURVE, "pinIndex": index})))

    def test_an_ODD_pin_index_halves_down_and_still_works(self) -> None:
        # The halved-before-multiplying shape: for an odd index the half is (n-1)/2 and the other
        # factor is n, so the product is unchanged. Solved rather than copied - triangular =
        # 5000*3*7 = 105000, numerator = 686000 - 0 - 105000 = 581000, and 581000/7 = 83000 exactly.
        # The first version of this test reused the even-index numbers and asserted a clean result;
        # they do NOT divide by 7, so the assertion was wrong and the port was right.
        self.assertEqual([], self._g4({"cMilli": 0, "bMilli": 5000, "pinIndex": 7, "pinValue": 686}))

    def test_an_odd_index_that_does_not_divide_is_reported(self) -> None:
        # The counterpart, so the odd branch is covered in BOTH directions. A sign error that made
        # every odd index pass would be caught only here.
        self.assertTrue(self._g4({**CURVE, "pinIndex": 7, "pinValue": 680}))

    def test_a_broken_pin_is_caught_even_when_the_file_is_self_consistent(self) -> None:
        # An earlier fixture picked self-consistent numbers by accident, which is always internally
        # "correct" no matter what pinValue says. The re-derivation is the point.
        curve = {**CURVE, "bMilli": 1, "pinIndex": 20, "pinValue": 700}
        self.assertTrue(self._g4(curve))

    def test_no_tuning_directory_is_not_a_failure(self) -> None:
        # Nothing to check is not the same as something wrong; the original drew the same line.
        with tempfile.TemporaryDirectory(prefix="pow-g4-") as tmp:
            self.assertEqual([], check(build(Path(tmp)))["findings"])

    def test_a_tuning_directory_with_no_matching_file_is_not_a_failure(self) -> None:
        with tempfile.TemporaryDirectory(prefix="pow-g4-") as tmp:
            root = build(Path(tmp))
            other = root / "data" / "tuning" / "aptitudes.v1.json"
            other.parent.mkdir(parents=True, exist_ok=True)
            other.write_text("{}", encoding="utf-8")
            self.assertEqual([], [f for f in check(root)["findings"] if f.startswith("G4")])


class TheShippedState(unittest.TestCase):
    def test_the_real_tree_is_green(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["findings"][:5])

    def test_the_real_tuning_pin_holds(self) -> None:
        # Isolated from the whole-tree pass, so a red elsewhere cannot explain this away.
        self.assertEqual([], guard.check_g4(REPO))


if __name__ == "__main__":
    unittest.main()
