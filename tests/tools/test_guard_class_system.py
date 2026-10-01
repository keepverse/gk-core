"""Contract tests for `gk-core/scripts/guard-class-system.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, and G1-G7. It does not assert a message body or a count of anything.

Four things this suite exists to stop a later reader from "fixing":

  * **`[string]$null` is the EMPTY STRING, and `str(None)` is `"None"`.** That single difference made G4
    SKIP a catalog entry whose `unitClass` is `null` and report CLEAN on a catalog that is missing
    every note. The differential caught it on the first fixture with a null `unitClass`; a reading would
    not have, because the cast looks equivalent. `_ps_string` stands wherever a PowerShell `[string]`
    cast stood.
  * **EVERY comparison folds case.** `-eq`, `-contains`, `-like`, `Group-Object`, `Select-Object
    -Unique` and every `-match` were all case-insensitive; the only exception is
    `StartsWith("//", Ordinal)`, which is only ever asked about `//`. So `Might`/`might` is ONE
    aptitude id and `ATK`/`atk` is the same channel. Asserted on each compiled pattern, because a fold
    that is dropped is invisible until a real casing difference ships.
  * **The version sort is NUMERIC, and the tree makes it urgent.** v1 through **v10** are shipped, and a
    lexical sort puts `v9` above `v10` — so a lexical port would validate a superseded config the
    first time the eleventh ships. The test plants a failing v10 and a clean v9.
  * **G5 does NOT exclude `bin`/`obj`, and G7's negative half skips only `//`-PREFIXED lines.** Both are
    transcribed asymmetries. "Fixing" either NARROWS a rule, which is the silent weakening this program
    exists to remove, so both are asserted.

Differential evidence: 57 fixtures, 51 identical in exit code and every finding, plus 6 declared
divergences. That comparison lives outside this file because the PowerShell form no longer exists.
"""

from __future__ import annotations

import importlib.util
import json
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "scripts" / "guard-class-system.py"

_spec = importlib.util.spec_from_file_location("guard_class_system", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_class_system"] = guard
_spec.loader.exec_module(guard)

ROSTER = {"schemaVersion": 1, "entries": [{"id": "might"}, {"id": "ferocity"}]}
CATALOG = {"schemaVersion": 1, "entries": [
    # `combat.power` is registered IN ITS OWN RIGHT, so a prefix case ("combat.power.brand_new" ->
    # `combat.power`) is a real positive. Without it the prefix rule is untestable and any prefix
    # fixture degenerates into "an unregistered channel fails".
    {"family": "combat.power", "unitClass": None, "unitClassNote": "the family root"},
    {"family": "combat.power.attack", "unitClass": "Melee"},
    {"family": "progression.bonus.atk", "unitClass": None, "unitClassNote": "loader-retired"},
    {"family": "armor.plate", "unitClass": None, "unitClassNote": "not a unit stat"},
]}


class Fixture:
    """A throwaway tree carrying the guard's two SOURCES plus whatever a rule needs.

    A default that means "absent" is a trap - the differential lost 55 of its 57 cases to one - so the
    sources are always written unless a test says otherwise.
    """

    def __init__(self, *, roster=None, catalog=None) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="gcs-"))
        (self.root / "data" / "seed" / "aptitudes").mkdir(parents=True)
        (self.root / "data" / "seed" / "derived-stats").mkdir(parents=True)
        (self.root / "data" / "tuning").mkdir(parents=True)
        self.write_json("data/seed/aptitudes/roster.json", ROSTER if roster is None else roster)
        self.write_json("data/seed/derived-stats/catalog.json", CATALOG if catalog is None else catalog)

    def write_json(self, rel: str, doc) -> None:
        (self.root / rel).write_text(json.dumps(doc), encoding="utf-8")

    def write(self, rel: str, text: str) -> None:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def tuning(self, name: str, edges: list[dict]) -> None:
        self.write_json(f"data/tuning/{name}", {"schemaVersion": 1, "edges": edges})

    def check(self) -> dict:
        return guard.check(self.root)

    def __enter__(self) -> "Fixture":
        return self

    def __exit__(self, *_exc) -> None:
        import shutil
        shutil.rmtree(self.root, ignore_errors=True)


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=1800)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(0, proc.returncode)
        self.assertIn("guard-class-system.py", proc.stdout)

    def test_the_real_tree_is_clean(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["failures"][:3])

    def test_stdout_carries_only_the_verdict_and_stderr_the_findings(self) -> None:
        with Fixture() as f:
            f.write_json("data/seed/aptitudes/roster.json", {"entries": [{"id": "x"}, {"id": "x"}]})
            result = run("--root", str(f.root))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn(guard.VERDICT_FAILED.strip(), result["stdout"])
        self.assertIn("G1", result["stderr"])
        self.assertNotIn("G1", result["stdout"])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED, guard.EXIT_REFUSED}, {0, 1, 64})

    def test_a_refusal_is_never_reported_as_clean(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gcs-none-") as tmp:
            result = run("--root", tmp)
        self.assertEqual(guard.EXIT_REFUSED, result["exit"])
        self.assertNotIn("GUARD OK", result["stdout"])


class ThePsStringCast(unittest.TestCase):
    """`[string]$null` is `""`; `str(None)` is `"None"`. That difference made G4 skip a real violation."""

    def test_a_json_null_reads_as_the_EMPTY_STRING(self) -> None:
        self.assertEqual("", guard._ps_string(None))
        self.assertNotEqual("None", guard._ps_string(None))

    def test_other_scalars_round_trip_the_way_PowerShell_spells_them(self) -> None:
        self.assertEqual("True", guard._ps_string(True))
        self.assertEqual("False", guard._ps_string(False))
        self.assertEqual("7", guard._ps_string(7))

    def test_a_container_is_REFUSED_rather_than_guessed(self) -> None:
        for value in ({}, []):
            with self.subTest(value=value):
                with self.assertRaises(guard.Refusal) as caught:
                    guard._ps_string(value)
                self.assertEqual("UNEXPECTED-JSON-SHAPE", caught.exception.reason)

    def test_G4_fires_on_a_null_unitClass_with_a_blank_note(self) -> None:
        # THE BUG. `str(None)` is non-empty, so this entry was skipped and the guard reported CLEAN on a
        # catalog that is missing every note.
        with Fixture() as f:
            f.write_json("data/seed/derived-stats/catalog.json",
                         {"entries": [{"family": "armor.plate", "unitClass": None,
                                       "unitClassNote": "   "}]})
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any(x.startswith("G4 armor.plate") for x in got["failures"]), got["failures"])

    def test_a_json_null_family_does_not_become_the_string_None(self) -> None:
        with Fixture(catalog={"entries": [{"family": None, "unitClass": "Melee"}]}) as f:
            got = f.check()
        self.assertFalse(any("None" in x for x in got["failures"]), got["failures"])


class G1IdsAreCollisionFree(unittest.TestCase):
    def test_a_duplicate_id_fails(self) -> None:
        with Fixture(roster={"entries": [{"id": "might"}, {"id": "might"}]}) as f:
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("duplicate aptitude id" in x for x in got["failures"]))

    def test_a_duplicate_differing_only_by_CASE_fails(self) -> None:
        # `Group-Object` folds case, so these are ONE id. A case-sensitive port passes this.
        with Fixture(roster={"entries": [{"id": "Might"}, {"id": "might"}]}) as f:
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("duplicate aptitude id" in x for x in got["failures"]))

    def test_a_family_collision_names_the_CATALOGS_spelling(self) -> None:
        # The original interpolates `$family`, not `$id`, so the two can differ in case and the finding
        # names the registered family. A first version echoed the id and the message differed.
        with Fixture(roster={"entries": [{"id": "ARMOR.PLATE"}]}) as f:
            got = f.check()
        self.assertTrue(any("ARMOR.PLATE" in x and "'armor.plate'" in x for x in got["failures"]),
                        got["failures"])

    def test_distinct_ids_pass(self) -> None:
        with Fixture() as f:
            self.assertEqual("OK", f.check()["verdict"])


class G2AndG3TheShippedConfig(unittest.TestCase):
    EDGE_POWER = {"source": "might", "channel": "combat.power.attack"}
    EDGE_ATK = {"source": "might", "channel": "progression.bonus.atk"}

    def test_an_unregistered_channel_fails(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "might", "channel": "nowhere.unregistered"}])
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any(x.startswith("G2 might -> nowhere.unregistered") for x in got["failures"]))

    def test_a_family_PREFIX_registers_the_whole_subtree(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "combat.power.brand_new"}])
            self.assertEqual("OK", f.check()["verdict"])

    def test_G2_registration_FOLDS_case(self) -> None:
        # A REAL hole, found by falsification: making G2's `-contains` case-sensitive left all 60 tests
        # green, because the only fold coverage was on G3's channel comparison. `-contains` folded in
        # the original, so a channel differing only in case from a registered one IS registered, and a
        # case-sensitive port would report a violation that does not exist.
        #
        # TWO fixtures, and the exact one needs a DOTLESS channel. G2 tries the exact lookup first and
        # falls through to the parent-prefix lookup whenever the channel has a dot, so a channel like
        # `COMBAT.POWER.ATTACK` is registered by the PREFIX (`combat.power`) even with the exact
        # comparison made case-sensitive - the mutation stayed invisible through two attempts because
        # the branch under test was never reached. The exact lookup is reachable only when there is no
        # parent to fall back on, so the fixture uses one.
        with Fixture(catalog={"entries": [
                {"family": "solo", "unitClass": "Melee"}]}) as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "SOLO"}])
            self.assertEqual([], f.check()["failures"],
                             "the EXACT lookup must fold case; a dotless channel is the only way to "
                             "reach it")
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "COMBAT.POWER.brand_new"}])
            self.assertEqual([], f.check()["failures"], "the PREFIX lookup must fold case")

    def test_the_prefix_is_the_LAST_dot(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "armor.plate.deep.deeper"}])
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_a_blank_channel_is_not_an_edge(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "   "}])
            self.assertEqual("OK", f.check()["verdict"])

    def test_one_source_feeding_both_FAILS(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [self.EDGE_POWER, self.EDGE_ATK])
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any(x.startswith("G3 might:") for x in got["failures"]))

    def test_two_sources_do_not_collide(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [self.EDGE_POWER,
                                           {"source": "ferocity", "channel": "progression.bonus.atk"}])
            self.assertEqual("OK", f.check()["verdict"])

    def test_the_channel_comparisons_FOLD_case(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "might", "channel": "COMBAT.POWER.attack"},
                                           {"source": "might", "channel": "PROGRESSION.BONUS.ATK"}])
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_NO_tuning_file_is_a_REPORTED_skip_not_a_silent_pass(self) -> None:
        with Fixture() as f:
            got = f.check()
        self.assertEqual("OK", got["verdict"], "a fresh checkout must still say 'nothing to check'")
        self.assertTrue(any("G2/G3" in s for s in got["skipped"]), got["skipped"])
        self.assertIsNone(got["shipped_tuning"])


class TheVersionSortIsNumeric(unittest.TestCase):
    """v1..v10 are SHIPPED, so a lexical sort reads a superseded config. This is not hypothetical."""

    def test_v10_beats_v9(self) -> None:
        self.assertEqual(10, int(guard.VERSION_IN_NAME.search("aptitudes.v10.json").group(1)))
        with Fixture() as f:
            f.tuning("aptitudes.v9.json", [{"source": "might", "channel": "combat.power.attack"}])
            f.tuning("aptitudes.v10.json", [{"source": "might", "channel": "nowhere.unregistered"}])
            got = f.check()
        self.assertEqual("data/tuning/aptitudes.v10.json", got["shipped_tuning"])
        self.assertEqual("FAIL", got["verdict"], "the LIVE config is the failing one, so a lexical "
                                                 "sort (v9 > v10) would report OK")

    def test_the_live_tree_ships_more_than_nine_versions(self) -> None:
        shipped = sorted(p.name for p in (REPO / "data" / "tuning").glob("aptitudes.v*.json"))
        self.assertGreaterEqual(len(shipped), 10, shipped)
        self.assertEqual("data/tuning/aptitudes.v10.json",
                         guard.check(REPO)["shipped_tuning"], shipped)

    def test_an_unparseable_version_scores_zero(self) -> None:
        with Fixture() as f:
            f.tuning("aptitudes.vx.json", [{"source": "m", "channel": "combat.power.attack"}])
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "combat.power.attack"}])
            self.assertEqual("data/tuning/aptitudes.v1.json", f.check()["shipped_tuning"])


class G5AtMostOneAptitudeReadFunctions(unittest.TestCase):
    def test_one_implementation_passes(self) -> None:
        with Fixture() as f:
            f.write("src/A.cs", "class AptitudeReadFunctions { }")
            self.assertEqual("OK", f.check()["verdict"])

    def test_two_implementations_fail(self) -> None:
        with Fixture() as f:
            f.write("src/A.cs", "class AptitudeReadFunctions { }")
            f.write("src/B.cs", "class AptitudeReadFunctions { }")
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any(x.startswith("G5:") for x in got["failures"]))

    def test_the_pattern_FOLDS_case(self) -> None:
        with Fixture() as f:
            f.write("src/A.cs", "CLASS APTITUDEREADFUNCTIONS { }")
            f.write("src/B.cs", "class   aptitudeReadFunctions { }")
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_build_output_is_NOT_excluded_and_that_is_transcribed(self) -> None:
        # The original's `Get-ChildItem -Recurse -Filter *.cs` has NO bin/obj filter, unlike every other
        # guard ported in this program. A stale copy under src/**/bin/ therefore reports a phantom
        # duplicate. "Fixing" it is a behaviour change, so it is asserted instead.
        with Fixture() as f:
            f.write("src/A.cs", "class AptitudeReadFunctions { }")
            f.write("src/SomeProj/bin/Debug/A.cs", "class AptitudeReadFunctions { }")
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_a_missing_src_is_a_REPORTED_skip(self) -> None:
        with Fixture() as f:
            got = f.check()
        self.assertTrue(any("G5/G6/G7" in s for s in got["skipped"]), got["skipped"])


class G6DominantPostureIsNeverAResolveInput(unittest.TestCase):
    CALL = "var p = DominantPosture.Of(x);"

    def test_a_resolve_shaped_file_calling_it_fails(self) -> None:
        with Fixture() as f:
            f.write("src/ThingResolver.cs", f"class T {{ {self.CALL} }}")
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any(x.startswith("G6 src/ThingResolver.cs") for x in got["failures"]))

    def test_the_name_pattern_FOLDS_case_and_tolerates_spacing(self) -> None:
        with Fixture() as f:
            f.write("src/subsystem.cs", "class S { DominantPosture . Of ( x ); }")
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_all_three_resolve_shapes_are_covered(self) -> None:
        for name in ("ThingResolver.cs", "ThingSubsystem.cs", "ThingComposer.cs"):
            with self.subTest(name=name):
                with Fixture() as f:
                    f.write(f"src/{name}", f"class T {{ {self.CALL} }}")
                    self.assertEqual("FAIL", f.check()["verdict"])

    def test_a_NEUTRAL_file_is_not_checked(self) -> None:
        with Fixture() as f:
            f.write("src/HudRenderer.cs", f"class H {{ {self.CALL} }}")
            self.assertEqual("OK", f.check()["verdict"])

    def test_the_posture_file_itself_is_exempt_and_the_exemption_FOLDS_case(self) -> None:
        with Fixture() as f:
            f.write("src/dominantposture.cs", "class D { }")
            f.write("src/Resolver.cs", f"class R {{ {self.CALL} }}")
            got = f.check()
        self.assertTrue(any(x.startswith("G6 src/Resolver.cs") for x in got["failures"]),
                        got["failures"])


class G7TheClosedFormCallsShippedCombatSymbols(unittest.TestCase):
    #: Every file in DAMAGE_COMPUTING_FILES, each calling a shipped symbol, so the POSITIVE half passes
    #: and a test can then break exactly one of them. The first version of this table carried a mangled
    #: f-string whose expression evaluated to the empty string, so StrikeMixture.cs held no shipped
    #: symbol and three G7 cases failed for a reason unrelated to what they were testing.
    SHIP = "class S { void M() { CombatProbability.Of(1); } }"
    POSITIVE = {
        "src/FusionRpg.Core/Balance/Analytic/StrikeMixture.cs": SHIP,
        "src/FusionRpg.Core/Balance/Analytic/PhaseModel.cs": SHIP.replace("S", "P"),
        "src/FusionRpg.Core/Battle/Siege/SiegeExpectedDamage.cs": SHIP.replace("S", "E"),
        "src/FusionRpg.Core/Battle/Siege/SiegeHitChance.cs": SHIP.replace("S", "H"),
    }

    def _covered(self, body: str) -> None:
        with Fixture() as f:
            for rel, text in self.POSITIVE.items():
                f.write(rel, body if rel.endswith("PhaseModel.cs") else text)
            return f.check()

    def test_a_damage_file_calling_a_shipped_symbol_passes(self) -> None:
        # `_covered` REPLACES PhaseModel.cs's body, so this must hand it a body that DOES call a
        # shipped symbol. The first version passed `"class P { }"` - the negative fixture - and then
        # expected OK, so the test asserted the opposite of its own name.
        self.assertEqual("OK", self._covered(self.SHIP.replace("class S", "class P"))["verdict"])

    def test_a_damage_file_with_NO_shipped_symbol_fails(self) -> None:
        got = self._covered("class P { double X = 1; }")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("no reference to a shipped combat symbol" in x for x in got["failures"]))

    def test_every_re_derivation_shape_fails_on_a_code_line(self) -> None:
        for code, label in (("return Math.Exp(1);", "Math.Exp"),
                            ("return 1.0 / (1.0 + 2);", "1/(1+"),
                            ("return Math.Clamp(Math.Max(0, x), 0, 1);", "clamp-and-scale")):
            with self.subTest(shape=label):
                got = self._covered(f"class P {{ double F() {{ {code} }} }}")
                self.assertEqual("FAIL", got["verdict"])
                self.assertTrue(any("re-derives" in x for x in got["failures"]), got["failures"])

    def test_a_SLASH_SLASH_prefixed_line_is_skipped(self) -> None:
        # The body must ALSO call a shipped symbol: `_covered` replaces PhaseModel.cs wholesale, so a
        # body with only the comment fails the POSITIVE half and the test passes for the wrong reason.
        got = self._covered("// return Math.Exp(1); -- explained, not re-derived\n"
                            + self.SHIP.replace("class S", "class P"))
        self.assertEqual("OK", got["verdict"], got["failures"])

    def test_a_BLOCK_comment_is_NOT_skipped_and_that_is_transcribed(self) -> None:
        # Only a `//`-PREFIXED line is skipped, so a shape inside `/* */` is still reported. Adding a
        # comment stripper here would NARROW the rule, which is the silent weakening this program exists
        # to remove. Asserted so the narrowing has to be a decision.
        got = self._covered("/* return Math.Exp(1); */\nclass P { }")
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("re-derives" in x for x in got["failures"]), got["failures"])

    def test_Race_cs_is_NOT_covered_so_its_sigmoid_is_fine(self) -> None:
        with Fixture() as f:
            for rel, text in self.POSITIVE.items():
                f.write(rel, text)
            f.write("src/FusionRpg.Core/Balance/Analytic/Race.cs",
                    "class R { double F() { return Math.Exp(1) / (1 + 2); } }")
            self.assertEqual("OK", f.check()["verdict"])

    def test_a_MISSING_covered_file_is_a_REPORTED_skip(self) -> None:
        with Fixture() as f:
            got = f.check()
        self.assertTrue(any("does not exist" in s for s in got["skipped"]), got["skipped"])


class G7CombatSimMustCallTheTwin(unittest.TestCase):
    def test_strike_must_call_StrikeMixture(self) -> None:
        with Fixture() as f:
            f.write("tools/CombatSim/Analytic.cs", "class A { StrikeMixture . Compute(x); }")
            self.assertEqual("OK", f.check()["verdict"])

    def test_a_re_assembled_strike_fails(self) -> None:
        with Fixture() as f:
            f.write("tools/CombatSim/Analytic.cs", "class A { }")
            self.assertEqual("FAIL", f.check()["verdict"])

    def test_status_must_call_StatusUptime(self) -> None:
        with Fixture() as f:
            f.write("tools/CombatSim/StatusModel.cs", "class S { StatusUptime . Of(x); }")
            self.assertEqual("OK", f.check()["verdict"])

    def test_an_OWN_uptime_formula_fails_even_with_the_call_present(self) -> None:
        with Fixture() as f:
            f.write("tools/CombatSim/StatusModel.cs",
                    "class S { StatusUptime.Of(x); double F() { return Math.Pow(1 - 0.1, 3); } }")
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("uptime formula of its own" in x for x in got["failures"]))

    def test_the_economy_must_call_BOTH_halves(self) -> None:
        with Fixture() as f:
            f.write("tools/CombatSim/ActionEconomy.cs",
                    "class E { ActionSchedule . Choose(x); ActionSchedule . Advance(x); }")
            self.assertEqual("OK", f.check()["verdict"])
        with Fixture() as f:
            f.write("tools/CombatSim/ActionEconomy.cs", "class E { ActionSchedule . Choose(x); }")
            self.assertEqual("FAIL", f.check()["verdict"])


class G7ResourcePoolOwnership(unittest.TestCase):
    def test_a_local_pool_clamp_fails(self) -> None:
        with Fixture() as f:
            f.write("src/FusionRpg.Core/Balance/Analytic/FirstPassage.cs",
                    "class F { void M() { x = Math.Clamp(_regen, 0, 1); } }")
            got = f.check()
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("advances a resource pool" in x for x in got["failures"]))

    def test_ActionSchedule_must_go_through_ResourcePoolState(self) -> None:
        with Fixture() as f:
            f.write("src/FusionRpg.Core/Balance/Analytic/ActionSchedule.cs",
                    "class A { ResourcePoolState.Settle(x); }")
            self.assertEqual("OK", f.check()["verdict"])
        with Fixture() as f:
            f.write("src/FusionRpg.Core/Balance/Analytic/ActionSchedule.cs", "class A { }")
            self.assertEqual("FAIL", f.check()["verdict"])


class TheCaseFoldPair(unittest.TestCase):
    """Asserted on the compiled patterns, not only through a fixture: a fold that is dropped is
    invisible until a real casing difference ships."""

    FOLDS = ("APTITUDE_READ_CLASS", "RESOLVE_SHAPED_NAME", "DOMINANT_POSTURE_CALL",
             "STRIKE_MIXTURE_CALL", "STATUS_UPTIME_CALL", "ACTION_SCHEDULE_CHOOSE",
             "ACTION_SCHEDULE_ADVANCE")

    def test_every_comparison_that_was_a_PowerShell_match_FOLDS(self) -> None:
        for name in self.FOLDS:
            with self.subTest(pattern=name):
                self.assertTrue(getattr(guard, name).flags & guard.re.IGNORECASE,
                                f"{name} lost the case fold PowerShell -match provides")

    def test_the_one_ORDINAL_comparison_is_the_line_comment_and_stays_ordinal(self) -> None:
        # `StartsWith("//", StringComparison.Ordinal)` is the only case-SENSITIVE comparison in the
        # original, and it is only ever asked about `//`, so its ordinal-ness is unobservable. It is
        # pinned as a plain prefix so nobody "fixes" it into a case-insensitive one.
        self.assertEqual("//", guard.LINE_COMMENT)
        self.assertTrue("  // x".lstrip().startswith(guard.LINE_COMMENT))

    def test_the_version_pattern_does_NOT_fold(self) -> None:
        # `[regex]::Match` without IgnoreCase: `APTITUDES.V10.JSON` is not a shipped config, and a fold
        # would let a mis-cased duplicate masquerade as the live one.
        self.assertFalse(guard.VERSION_IN_NAME.flags & guard.re.IGNORECASE)
        self.assertIsNone(guard.VERSION_IN_NAME.search("APTITUDES.V10.JSON"))


class TheRefusals(unittest.TestCase):
    def test_a_missing_roster_is_refused_by_name(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gcs-r-") as tmp:
            root = Path(tmp)
            (root / "data" / "seed" / "derived-stats").mkdir(parents=True)
            (root / "data" / "seed" / "derived-stats" / "catalog.json").write_text(json.dumps(CATALOG))
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(root)
        self.assertEqual("SOURCE-MISSING", caught.exception.reason)

    def test_a_missing_catalog_is_refused_by_name(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gcs-r-") as tmp:
            root = Path(tmp)
            (root / "data" / "seed" / "aptitudes").mkdir(parents=True)
            (root / "data" / "seed" / "aptitudes" / "roster.json").write_text(json.dumps(ROSTER))
            with self.assertRaises(guard.Refusal) as caught:
                guard.check(root)
        self.assertEqual("SOURCE-MISSING", caught.exception.reason)

    def test_a_source_with_no_entries_is_REFUSED_not_reported_clean(self) -> None:
        # The original's `@(...).entries` would be an empty array and the guard would report OK on a
        # roster that names nothing - a fail-open on a malformed source.
        with Fixture(roster={"schemaVersion": 1}) as f:
            with self.assertRaises(guard.Refusal) as caught:
                f.check()
        self.assertEqual("ROSTER-SHAPE", caught.exception.reason)

    def test_unparseable_JSON_is_refused_by_name(self) -> None:
        with Fixture() as f:
            f.write("data/seed/aptitudes/roster.json", "{ not json")
            with self.assertRaises(guard.Refusal) as caught:
                f.check()
        self.assertEqual("SOURCE-UNREADABLE", caught.exception.reason)


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "root", "shipped_tuning", "aptitude_ids", "catalog_families",
            "failures", "skipped"}
    # A refusal carries the SAME key set plus the two that name it, so a consumer needs no branch.
    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with Fixture() as ok:
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(ok.root), "--json"],
                                  capture_output=True, text=True, timeout=1800)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
        with Fixture() as bad:
            bad.write_json("data/seed/aptitudes/roster.json", {"entries": [{"id": "x"}, {"id": "x"}]})
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(bad.root), "--json"],
                                  capture_output=True, text=True, timeout=1800)
            self.assertEqual(self.KEYS, set(json.loads(proc.stdout)))
            self.assertEqual("FAIL", json.loads(proc.stdout)["verdict"])

    def test_a_refusal_carries_its_reason(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gcs-j-") as tmp:
            proc = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--json"],
                                  capture_output=True, text=True, timeout=1800)
            payload = json.loads(proc.stdout)
        self.assertEqual(self.REFUSAL_KEYS, set(payload))
        self.assertEqual("SOURCE-MISSING", payload["reason"])


class TheShippedState(unittest.TestCase):
    def test_this_repo_is_clean_and_names_its_inputs(self) -> None:
        got = guard.check(REPO)
        self.assertEqual("OK", got["verdict"], got["failures"][:3])
        self.assertIsNotNone(got["shipped_tuning"])
        self.assertEqual([], got["skipped"], "the real tree exercises every rule, so nothing is skipped")

    def test_EVERY_rule_id_can_actually_be_produced(self) -> None:
        """Each rule gets a fixture that makes it fire.

        The first version of this test unioned the real findings with the full id set, so it could not
        fail - which is the same "vacuous assertion" shape this program keeps rejecting. A rule id
        that nothing can produce is a rule that has silently stopped existing, and a set-membership
        test over a constant is exactly what would not notice.
        """
        produced = set()

        def rule_ids(findings: list[str]) -> set[str]:
            return {re.match(r"^(G\d)", line).group(1) for line in findings
                    if re.match(r"^(G\d)", line)}

        with Fixture(roster={"entries": [{"id": "x"}, {"id": "x"}]}) as f:
            produced |= rule_ids(f.check()["failures"])
        with Fixture() as f:
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "nowhere.unregistered"}])
            produced |= rule_ids(f.check()["failures"])
            # `combat.power` does NOT match `combat.power.*` -- the wildcard needs the trailing dot --
            # so the first version of this fixture produced no G3 finding and the assertion could not
            # fail for the reason it claimed.
            f.tuning("aptitudes.v1.json", [{"source": "m", "channel": "combat.power.attack"},
                                           {"source": "m", "channel": "progression.bonus.atk"}])
            produced |= rule_ids(f.check()["failures"])
        with Fixture(catalog={"entries": [{"family": "armor.plate", "unitClass": None}]}) as f:
            produced |= rule_ids(f.check()["failures"])
        with Fixture() as f:
            f.write("src/A.cs", "class AptitudeReadFunctions { }")
            f.write("src/B.cs", "class AptitudeReadFunctions { }")
            produced |= rule_ids(f.check()["failures"])
        with Fixture() as f:
            f.write("src/ThingResolver.cs", "class T { DominantPosture.Of(x); }")
            produced |= rule_ids(f.check()["failures"])
        with Fixture() as f:
            f.write("src/FusionRpg.Core/Balance/Analytic/PhaseModel.cs",
                    "class P { double F() { return Math.Exp(1); } }")
            produced |= rule_ids(f.check()["failures"])

        self.assertEqual({"G1", "G2", "G3", "G4", "G5", "G6", "G7"}, produced)


if __name__ == "__main__":
    unittest.main()
