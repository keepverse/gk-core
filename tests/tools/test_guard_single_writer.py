"""Contract tests for `gk-fusion/scripts/guard-single-writer.py`.

The expected verdicts are the ones MEASURED by running `guard-single-writer.ps1` and the port over
the same fixture: 13 findings, same messages, same exit code, 0 divergences.

Four properties are asserted structurally as well as behaviourally, because each is a way a rewrite
loses a rule SILENTLY - the guard still returns a clean run, so nothing goes red without a test:

  * W1 reads RAW text while W2/W3 read comment-AND-literal-stripped text
  * the three directory carve-outs apply to W1 ONLY
  * the field PATTERNS are case-sensitive while every TABLE lookup folds case
  * W1 does not skip obj/bin and W2/W3 do
"""
from __future__ import annotations

import importlib.util
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# The file under test is gk-fusion's, so it is asked of its owner rather than of REPO. `REPO /
# "scripts/guard-single-writer.py"` does not exist in gk-core, and this suite raised at COLLECTION because of it - which is
# why it was one of the dark suites, and why a collection error that aborts the pytest run could hide
# the rest of the tree. Its own docstring already said `gk-fusion/scripts/guard-single-writer.py`. cscan.py stays on
# REPO - it is gk-core's.
sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import fusion_root  # noqa: E402

_FUSION = fusion_root(REPO)

sys.path.insert(0, str(REPO / "scripts"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    # Registered BEFORE exec: a dataclass resolves `cls.__module__` through sys.modules at
    # decoration time under PEP 563, so an unregistered module raises from inside dataclasses.py
    # with an error naming neither this file nor the cause.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


guard = _load("guard_single_writer", _FUSION / "scripts" / "guard-single-writer.py")
cscan = _load("cscan", REPO / "scripts" / "cscan.py")

INJ = "src/FusionRpg.Injector"

# Measured: the file the .ps1 flagged, and why. Asserting file -> rule keeps the assertion on the
# contract rather than on a total, so a lost rule turns this red and a new source file does not.
MEASURED_FLAGGED = {
    f"{INJ}/Commented.cs": "W1",                # W1 is raw: a comment naming a field is a W1 finding
    f"{INJ}/Literal.cs": "W1",                  # W1 matches inside a literal; W2/W3 cannot
    f"{INJ}/RealWrite.cs": "W1",                # a real write
    f"{INJ}/EntityStatWriter.cs": "W2",         # theHealth is NOT in this writer's pinned list
    f"{INJ}/ZombieCombatFields.cs": "W2",       # SetHp is not in its own pinned list
    f"{INJ}/UniqueBoundLoadout.cs": "W2",       # the pinned list is EMPTY
    f"{INJ}/RetiredCase.cs": "W3",              # ATTACKDAMAGE folds to a retired field
    f"{INJ}/RetiredInWriter.cs": "W3",          # W3 only, never double-reported as W2
    f"{INJ}/RetiredInOrdinary.cs": "W3",
    f"{INJ}/Bridges/Version.cs": "W3",          # a carve-out hides it from W1, not from W3
    f"{INJ}/Fx/AuraPool.cs": "W3",
    f"{INJ}/obj/Debug/Gen.cs": "W1",            # W1 does not skip build output
}
MEASURED_CLEAN = (
    f"{INJ}/Comparison.cs",                     # `(?!=)`: a comparison is a read
    f"{INJ}/CasePattern.cs",                    # the PATTERNS are case-sensitive
    f"{INJ}/EntityPositionWriter.cs",           # every field is pinned
    f"{INJ}/Dotted.cs",                         # `a.b` is one target, in a non-writer
    f"{INJ}/Hud/ActorHudPool.cs",               # carve-out is W1-only, but no list to violate
    f"{INJ}/BridgeSetterElsewhere.cs",          # no receiver, not retired, no pinned list
)


def _fixture() -> tuple[Path, tempfile.TemporaryDirectory]:
    files = {
        f"{INJ}/Commented.cs":
            "// p.theHealth = 1 is forbidden here\n"
            "class Commented { void Go() { } } // .attackDamage = 2 also forbidden\n",
        f"{INJ}/Literal.cs":
            'class Literal { void Go() { System.Console.WriteLine("p.theHealth = 0"); } }\n',
        f"{INJ}/Comparison.cs":
            "class Comparison { bool Go(z z, int row) { return z.theZombieRow == row; } }\n",
        f"{INJ}/RealWrite.cs":
            "class RealWrite { void Go(z z) { z.theZombieRow = 3; } }\n",
        f"{INJ}/CasePattern.cs":
            "class CasePattern { void Go() { int THEHEALTH = 1; int TheHealth = 2; } }\n",
        f"{INJ}/RetiredCase.cs":
            "class RetiredCase { void Go(p p) { p.ATTACKDAMAGE = 1; } }\n",
        f"{INJ}/EntityStatWriter.cs":
            "class EntityStatWriter { void Go(p p) { p.theHealth = 1; p.theSpeed = 2; "
            "p.thePlantProduceInterval = 3; } }\n",
        f"{INJ}/UniqueBoundLoadout.cs":
            "class UniqueBoundLoadout { void Go(p p) { p.theLevel = 1; } }\n",
        f"{INJ}/ZombieCombatFields.cs":
            "class ZombieCombatFields { void Go() { ZombieCombatFields.SetHp(1); } }\n",
        f"{INJ}/EntityPositionWriter.cs":
            "class EntityPositionWriter { void Go(p p, Transform t) { p.thePlantRow = 1; "
            "t.transform.position = v; } }\n",
        f"{INJ}/BridgeSetterElsewhere.cs":
            "class X { void Go() { ZombieCombatFields.SetHp(1); } }\n",
        f"{INJ}/RetiredInWriter.cs":
            "class RetiredInWriter { void Go(p p) { p.takeDmgMultiplier = 1; } }\n",
        f"{INJ}/RetiredInOrdinary.cs":
            "class RetiredInOrdinary { void Go(p p) { p.theArmor = 1; } }\n",
        f"{INJ}/Fx/AuraPool.cs":
            "class AuraPool { void Go(p p) { p.theShieldHealth = 1; } }\n",
        f"{INJ}/Hud/ActorHudPool.cs":
            "class ActorHudPool { void Go(p p) { p.theHealth = 1; } }\n",
        f"{INJ}/Bridges/Version.cs":
            "class Version { void Go(p p) { p.attackDamage = 1; } }\n",
        f"{INJ}/obj/Debug/Gen.cs":
            "class Gen { void Go() { int x; x = 1; } void W(p p) { p.attackDamage = 2; } }\n",
        f"{INJ}/Dotted.cs":
            "class Dotted { void Go(p p) { p.a.b = 1; } }\n",
    }
    box = tempfile.TemporaryDirectory(prefix="single-writer-test-")
    root = Path(box.name).resolve()
    for rel, body in files.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
    return root, box


class _FixtureCase(unittest.TestCase):
    def setUp(self) -> None:
        self.root, self._box = _fixture()
        self.addCleanup(self._box.cleanup)
        self.result = guard.scan(self.root)
        self.by_file: dict[str, set[str]] = {}
        for item in self.result["findings"]:
            self.by_file.setdefault(item["file"], set()).add(item["rule"])


class TheMeasuredContract(_FixtureCase):
    """13 findings from each implementation, same messages, same exit, 0 divergences."""

    def test_each_flagged_file_reports_exactly_its_measured_rule(self) -> None:
        for rel, rule in MEASURED_FLAGGED.items():
            with self.subTest(file=rel):
                self.assertEqual(self.by_file.get(rel), {rule})

    def test_each_exempt_file_reports_nothing(self) -> None:
        for rel in MEASURED_CLEAN:
            with self.subTest(file=rel):
                self.assertNotIn(rel, self.by_file)

    def test_no_file_outside_the_fixture_is_flagged(self) -> None:
        for rel in self.by_file:
            self.assertIn(rel, MEASURED_FLAGGED, f"unexpected finding in {rel}")

    def test_the_rule_vocabulary_is_closed(self) -> None:
        self.assertEqual(sorted(self.result["rule_ids"]), ["W1", "W2", "W3"])
        self.assertTrue(set(self.result["findings_by_rule"]) <= {"W1", "W2", "W3"})


class W1IsRawAndW2W3AreStripped(_FixtureCase):
    """The documented asymmetry, in both directions.

    A comment naming a field is a W1 FINDING (W1 never strips) and is invisible to W2/W3 (they do).
    A field inside a string literal is the reverse: W1's pattern matches the raw text, and W2/W3
    cannot see it because the literal is blanked before targets are extracted.
    """

    def test_a_comment_is_a_w1_finding(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/Commented.cs"], {"W1"})

    def test_w1_matching_inside_a_literal_is_still_w1(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/Literal.cs"], {"W1"})

    def test_the_same_literal_is_invisible_to_w2_and_w3(self) -> None:
        # `theHealth` is not retired, so a blanked literal produces no W3 either.
        self.assertNotIn("W2", self.by_file[f"{INJ}/Literal.cs"])
        self.assertNotIn("W3", self.by_file[f"{INJ}/Literal.cs"])

    def test_the_two_rules_read_different_text(self) -> None:
        self.assertFalse(any(guard.strip_comments_and_literals(x) == x
                             for x in ("// x", "/* x */", '"y"')))


class CarveOutsAreForW1Only(_FixtureCase):
    """`Bridges/`, `Fx/` and `Hud/` hide a file from W1 and from nothing else.

    Only the second loop skips obj/bin, so a VFX or HUD file is invisible to the raw scan and fully
    scanned by W2/W3. A port that moved the carve-outs into the shared skip list would silently
    forgive a retired-field write in a particle-lease file.
    """

    def test_a_carve_out_hides_from_w1_but_not_from_w3(self) -> None:
        for rel in (f"{INJ}/Bridges/Version.cs", f"{INJ}/Fx/AuraPool.cs"):
            with self.subTest(file=rel):
                self.assertEqual(self.by_file[rel], {"W3"})

    def test_every_carve_out_pattern_is_anchored_to_a_directory_segment(self) -> None:
        for pattern, why in guard.CARVE_OUTS:
            self.assertTrue(pattern.pattern.endswith(r"[\\/]"), pattern.pattern)
            self.assertTrue(why.strip(), f"{pattern.pattern} has no stated reason")

    def test_build_output_is_skipped_by_w2_w3_and_not_by_w1(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/obj/Debug/Gen.cs"], {"W1"})
        self.assertNotIn("W3", self.by_file[f"{INJ}/obj/Debug/Gen.cs"])


class CaseSensitivityIsPerPosition(_FixtureCase):
    """Case-SENSITIVE about the patterns, case-INSENSITIVE about the tables.

    .NET's IsMatch does not fold case, so `theHealth` does not catch `THEHEALTH`. PowerShell's
    `-contains` and its hashtable lookup both DO, so a lowercased writer file is allowed and an
    uppercased retired field is retired. Normalising either would change which files and which
    fields are exempt.
    """

    def test_the_patterns_do_not_fold_case(self) -> None:
        self.assertNotIn(f"{INJ}/CasePattern.cs", self.by_file)
        self.assertEqual(guard.SENSITIVE, 0)
        self.assertFalse(any(p.flags & guard.FOLDED for p in guard.W1_COMPILED))

    def test_the_retired_table_folds_case(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/RetiredCase.cs"], {"W3"})
        self.assertTrue(guard.is_retired("ATTACKDAMAGE"))
        self.assertTrue(guard.is_retired("attackDamage"))

    def test_the_writer_name_lookup_folds_case(self) -> None:
        # The bug the differential found: `is_allowed_file` folded but the Pinned-LIST LOOKUP did
        # not, so a lowercased writer passed the membership test and was then judged against no
        # list at all - every one of its writes reported as a violation of a list it was never
        # measured against. PowerShell's hashtable folds, so the lookup must too.
        self.assertTrue(guard.is_allowed_file("entitystatwriter.cs"))
        self.assertEqual(guard.canonical_writer("entitystatwriter.cs"), "EntityStatWriter.cs")
        self.assertTrue(guard.allowed_field(guard.canonical_writer("entitystatwriter.cs"),
                                            "theLevel"))

    def test_the_pinned_list_membership_folds_case(self) -> None:
        self.assertTrue(guard.allowed_field("EntityStatWriter.cs", "theSpeed"))
        self.assertTrue(guard.allowed_field("EntityStatWriter.cs", "THESPEED"))


class PinnedListsAreMeasuredNotInvented(_FixtureCase):
    """Each writer's list is a measurement. Two of them surprise, and both surprises are the point.

    `UniqueBoundLoadout.cs` has an EMPTY list, so any receiver write there is a W2 - every grant
    goes through the RPG-layer Funnel, and that is the measured answer rather than an oversight.
    `EntityStatWriter.cs` may NOT write `theHealth`: that field belongs to `ZombieCombatFields.cs`,
    whose list is exactly (theHealth, theMaxHealth). The lists deliberately do not overlap.
    """

    def test_the_bound_loadout_list_is_empty_and_its_writes_are_w2(self) -> None:
        self.assertEqual(guard.ALLOWED_FIELDS["UniqueBoundLoadout.cs"], ())
        self.assertEqual(self.by_file[f"{INJ}/UniqueBoundLoadout.cs"], {"W2"})

    def test_a_field_owned_by_another_writer_is_a_w2_here(self) -> None:
        self.assertFalse(guard.allowed_field("EntityStatWriter.cs", "theHealth"))
        self.assertTrue(guard.allowed_field("ZombieCombatFields.cs", "theHealth"))
        self.assertEqual(self.by_file[f"{INJ}/EntityStatWriter.cs"], {"W2"})

    def test_every_writer_has_an_entry_in_both_tables(self) -> None:
        self.assertEqual(set(guard.ALLOWED_FILES), set(guard.ALLOWED_FIELDS))
        for name, why in guard.ALLOWED_FILES.items():
            self.assertTrue(why.strip(), f"{name} is allowed with no stated reason")


class W3IsEverywhereAndNeverDoublesAsW2(_FixtureCase):
    """A retired field is retired everywhere, and one write yields one finding.

    The original `continue`s past a W2 check for a retired target, so a retired field in a writer
    with a pinned list is W3 alone. Deduplicating by file instead of by target would report the
    first rule and hide the second.
    """

    def test_a_retired_field_in_a_writer_is_w3_only(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/RetiredInWriter.cs"], {"W3"})

    def test_a_retired_field_in_a_non_writer_is_w3_only(self) -> None:
        self.assertEqual(self.by_file[f"{INJ}/RetiredInOrdinary.cs"], {"W3"})

    def test_w2_and_w3_share_one_pass(self) -> None:
        # Two walks would duplicate every W3 finding under a W2 name.
        self.assertEqual(len(guard.w2_w3(self.root)),
                         sum(1 for f in self.result["findings"] if f["rule"] in ("W2", "W3")))


class NegatedEqualsIsTheRule(_FixtureCase):
    """`(?!=)` is what makes a comparison a read.

    `z.theZombieRow == row` is a comparison, and a guard that flags it would fire on the ordinary
    way this field is used. It is carried by W1's pattern AND by the target extractor, so dropping
    it from either one is a bug.
    """

    def test_a_comparison_is_not_a_write(self) -> None:
        self.assertNotIn(f"{INJ}/Comparison.cs", self.by_file)
        self.assertEqual(guard.live_targets("z.theZombieRow == row"), [])
        self.assertEqual(guard.live_targets("z.theZombieRow = 3"), ["theZombieRow"])

    def test_the_extractor_takes_a_dotted_path_as_one_target(self) -> None:
        self.assertEqual(guard.live_targets("p.a.b = 1"), ["a.b"])

    def test_the_bridge_setter_form_needs_no_receiver(self) -> None:
        self.assertEqual(guard.live_targets("ZombieCombatFields.SetHp(1)"), ["SetHp"])


class ExitAndStreamContract(_FixtureCase):
    """Findings on stderr, a clean verdict on stdout.

    The original emitted both with `Write-Host`, which writes the INFORMATION stream where a
    `2>&1` capture cannot see it. Stated once in docs/architecture/ps1-port-checklist.md item 4.
    """

    def _run(self, argv: list[str]) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = guard.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_a_violating_tree_exits_one_with_findings_on_stderr(self) -> None:
        code, out, err = self._run(["--root", str(self.root)])
        self.assertEqual(code, guard.EXIT_FINDINGS)
        self.assertEqual(out, "")
        self.assertIn("SINGLE-WRITER GUARD FAILED", err)
        self.assertIn("attackDamage", err)

    def test_a_clean_tree_exits_zero_with_the_verdict_on_stdout(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        for rel in MEASURED_FLAGGED:
            (root / rel).unlink()
        code, out, err = self._run(["--root", str(root)])
        self.assertEqual(code, guard.EXIT_OK, out + err)
        self.assertIn("SINGLE-WRITER GUARD OK", out)
        self.assertEqual(err, "")

    def test_json_carries_the_findings_and_the_rule_vocabulary(self) -> None:
        code, out, _ = self._run(["--root", str(self.root), "--json"])
        payload = json.loads(out)
        self.assertEqual(code, guard.EXIT_FINDINGS)
        self.assertEqual(payload["guard"], "single-writer")
        self.assertEqual(payload["verdict"], "FAIL")
        self.assertGreater(payload["scanned_files"], 0)
        for item in payload["findings"]:
            for key in ("rule", "file", "message"):
                self.assertIn(key, item)


class RefusalIsNamedAndClosed(_FixtureCase):
    """The original THREW on a missing injector rather than reporting a clean tree, so this is a
    refusal and not a pass. Exit 64 rather than the throw's 1: the vocabulary is the port's own."""

    def test_a_root_without_the_injector_refuses(self) -> None:
        import shutil
        shutil.rmtree(self.root / INJ)
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(self.root)
        self.assertEqual(caught.exception.reason, "MISSING_INJECTOR")
        self.assertEqual(guard.main(["--root", str(self.root), "--json"]), guard.EXIT_REFUSED)

    def test_a_refusal_reports_on_stderr_and_keeps_the_exit_code_vocabulary(self) -> None:
        import shutil
        shutil.rmtree(self.root / INJ)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = guard.main(["--root", str(self.root), "--json"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())
        self.assertEqual(json.loads(out.getvalue())["verdict"], "REFUSED")


class TheSharedStripperIsTheBlankingOne(_FixtureCase):
    """W2/W3 need `strip_comments_and_literals`, NOT `strip_comments`.

    The PowerShell original this guard carried blanked literal CONTENTS so a pattern could not match
    text inside one. `cscan.strip_comments` keeps literals verbatim, so under it a log message
    reading `"p.theHealth = 0"` becomes a W2 finding. The two were compared character-for-character
    before this port, including doubled quotes and backslash escapes inside single-quoted literals.
    """

    def test_a_literal_is_blanked_so_a_pattern_cannot_match_inside_it(self) -> None:
        stripped = cscan.strip_comments_and_literals('Log("p.theHealth = 0");')
        self.assertNotIn("theHealth", stripped)
        self.assertEqual(guard.live_targets(stripped), [])

    def test_the_other_policy_would_have_matched(self) -> None:
        # Asserted as a contrast, so the choice is pinned rather than assumed: the kept-literal
        # policy DOES match, which is exactly why this guard cannot use it.
        self.assertIn("theHealth", cscan.strip_comments('Log("p.theHealth = 0");'))

    def test_a_doubled_quote_is_one_literal(self) -> None:
        self.assertEqual(cscan.strip_comments_and_literals('Log("a""b"); var q = 1;'),
                         'Log(""); var q = 1;')

    def test_a_backslash_escapes_only_inside_a_double_quoted_literal(self) -> None:
        self.assertEqual(cscan.strip_comments_and_literals(r'Log("a\"b"); var q = 1;'),
                         'Log(""); var q = 1;')
        self.assertIn("q = 1", cscan.strip_comments_and_literals(r"Log('a\'); var q = 1;"))


if __name__ == "__main__":
    unittest.main()
