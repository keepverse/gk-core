"""Contract tests for `gk-core/scripts/guard-actor-hub.py`.

The expected finding set is the one MEASURED by running `guard-actor-hub.ps1` and the port over the
same fixture: 11 findings, same files, same messages, same exit code, 0 divergences. The fixture was
built to break a careless port, because a guard that stopped looking also reports a clean tree.

Each class below pins one of the four contract notes in the port's docstring. Those notes are the
things a rewrite loses SILENTLY: the result is still a clean run, so nothing goes red unless a test
says so.
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
sys.path.insert(0, str(REPO / "scripts"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    # Register BEFORE exec: a dataclass resolves `cls.__module__` through sys.modules at
    # decoration time under PEP 563, so an unregistered module raises from inside dataclasses.py
    # with an error that names neither this file nor the cause.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


guard = _load("guard_actor_hub", REPO / "scripts" / "guard-actor-hub.py")

CORE = "src/FusionRpg.Core"
SERVER = "src/FusionRpg.Server"
INJECTOR = "src/FusionRpg.Injector"


def _drop_src(root: Path) -> None:
    """Remove the whole source tree. Renaming one project would leave `src/` present, and the
    precondition under test is a missing TREE, not a missing project."""
    import shutil
    shutil.rmtree(root / "src")


def _cs(body: str) -> str:
    return f"namespace X {{ class C {{ void Go() {{ {body} }} }} }}\n"


# The measured contract, as FILE -> whether the .ps1 flagged it. Asserting the flag per file (a
# closed vocabulary of rule ids via the rule column) rather than a total, so a lost rule turns this
# red while an unrelated new source file does not.
MEASURED_FLAGGED = (
    f"{CORE}/Bad/CasingComposer.cs",
    f"{CORE}/Bad/LowerCtor.cs",
    f"{CORE}/Battle/OtherProducer.cs",
    f"{CORE}/Battle/Sub/TraitAtomSource.cs",
    f"{CORE}/Elsewhere/StrayComposer.cs",
    f"{CORE}/SimEngine.cs",
    f"{INJECTOR}/Bypass.cs",
    f"{INJECTOR}/LowerResolve.cs",
    f"{SERVER}/ServerProducer.cs",
)
MEASURED_CLEAN = (
    f"{CORE}/Battle/TraitAtomSource.cs",        # the one allowlisted ChannelMod producer
    f"{CORE}/PlainCompose.cs",                 # name lacks "Composer", so R2 never scans it
    f"{CORE}/Stats/Derived/HubComposer.cs",    # allowlisted: where compose belongs
    f"{CORE}/Stats/PvzStatsSheetComposer.cs",   # allowlisted: orthogonal sheet
    f"{CORE}/Stats/StatComposer.cs",            # allowlisted: orthogonal stat assembly
    f"{CORE}/obj/Debug/GenComposer.cs",         # build output is not source
    f"{INJECTOR}/obj/Debug/Gen.cs",             # build output is not source
    f"{INJECTOR}/NotABypass.cs",               # the lookbehind: MyStats is not Stats
    f"{INJECTOR}/GameHooks.cs",                # R4 satisfied
    f"{SERVER}/AuraDerivedEndpoints.cs",       # R1 satisfied
    f"{SERVER}/UniqueActorHubCompose.cs",      # R1 satisfied via the `new ActorHub` alternative
    f"{SERVER}/NoHubCtor.cs",                  # R1 judges only its two NAMED files
    f"{SERVER}/Unrelated.cs",                  # an unjudged server file
    f"{SERVER}/Program.cs",                    # R7: absent, so not checked
    f"{CORE}/Stats/Derived/Subsystems/StatusDerivedSubsystem.cs",   # R8: absent
    f"{INJECTOR}/CheatCommandRunner.cs",       # R9: absent
)
MISSING_REQUIRED = "FusionRpg.Injector\\Stats\\EntityApply.cs"


def _fixture() -> tuple[Path, tempfile.TemporaryDirectory]:
    """The measured fixture. EntityApply.cs, Program.cs, StatusDerivedSubsystem.cs and
    CheatCommandRunner.cs are all ABSENT, which is what makes the R1/R4-versus-R7/R8/R9
    asymmetry observable rather than assumed."""
    files = {
        f"{CORE}/Bad/CasingComposer.cs": _cs("APPLIEDCOMBAT = 1; derivedmodifier(2); BattleChannelMod(3);"),
        f"{CORE}/Bad/LowerCtor.cs": _cs("var m = new battlechannelmod(1);"),
        f"{CORE}/Battle/TraitAtomSource.cs": _cs("var m = new BattleChannelMod();"),
        f"{CORE}/Battle/OtherProducer.cs": _cs("var m = new BattleChannelMod();"),
        f"{CORE}/Battle/Sub/TraitAtomSource.cs": _cs("var m = new BattleChannelMod();"),
        f"{SERVER}/ServerProducer.cs": _cs("var m = new BattleChannelMod();"),
        f"{CORE}/Elsewhere/StrayComposer.cs": _cs("AppliedCombat(1);"),
        f"{CORE}/PlainCompose.cs": _cs("AppliedCombat(1);"),
        f"{CORE}/Stats/Derived/HubComposer.cs": _cs("AppliedCombat(1);"),
        f"{CORE}/Stats/PvzStatsSheetComposer.cs": _cs("ContributeDerived(1);"),
        f"{CORE}/Stats/StatComposer.cs": _cs("ActorDerivedSnapshot(1);"),
        f"{CORE}/obj/Debug/GenComposer.cs": _cs("AppliedCombat(1);"),
        f"{INJECTOR}/Bypass.cs": _cs("var r = Stats.Resolve(1);"),
        f"{INJECTOR}/LowerResolve.cs": _cs("stats.resolve(1);"),
        f"{INJECTOR}/NotABypass.cs": _cs("MyStats.Resolve(1);"),
        f"{INJECTOR}/obj/Debug/Gen.cs": _cs("Stats.Resolve(1);"),
        f"{INJECTOR}/GameHooks.cs": _cs("ActorHub.Resolve();"),
        f"{CORE}/SimEngine.cs": _cs("Stats.Resolve(1);"),
        f"{SERVER}/UniqueActorHubCompose.cs": _cs(
            "var h = new ActorHub(); h.ResolveDerivedWithContributions(); var e = EquippedBoundAtoms;"),
        f"{SERVER}/AuraDerivedEndpoints.cs": _cs("UniqueActorHubCompose.Run(); var k = composeKind;"),
        f"{SERVER}/NoHubCtor.cs": _cs("ResolveDerivedWithContributions();"),
        f"{SERVER}/Unrelated.cs": _cs(""),
    }
    box = tempfile.TemporaryDirectory(prefix="actor-hub-test-")
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
        self.flagged = {f["file"] for f in self.result["findings"]}
        self.rules = {f["rule"] for f in self.result["findings"] if f["file"] in set(MEASURED_FLAGGED)}


class TheMeasuredContract(_FixtureCase):
    """11 findings from each implementation, same files, same messages, same exit, 0 divergences."""

    def test_each_violating_file_is_flagged(self) -> None:
        for rel in MEASURED_FLAGGED:
            with self.subTest(file=rel):
                self.assertIn(rel, self.flagged)

    def test_each_exempt_or_unjudged_file_is_not_flagged(self) -> None:
        for rel in MEASURED_CLEAN:
            with self.subTest(file=rel):
                self.assertNotIn(rel, self.flagged)

    def test_no_file_outside_the_fixture_is_flagged(self) -> None:
        expected = {r.replace("\\", "/") for r in MEASURED_FLAGGED}
        expected |= {MISSING_REQUIRED.replace("\\", "/")}
        for reported in self.flagged:
            normalised = reported.replace("\\", "/")
            self.assertIn(normalised, expected, f"unexpected finding in {reported}")

    def test_the_missing_required_file_is_one_finding_and_is_marked_missing(self) -> None:
        missing = [f for f in self.result["findings"] if f.get("missing")]
        self.assertEqual(len(missing), 1)
        self.assertEqual(missing[0]["rule"], "hub-required")
        self.assertIn("EntityApply.cs", missing[0]["message"])


class MatchingFoldsCase(_FixtureCase):
    """Contract note 1. PowerShell's `-match` folds case, so this guard does too.

    `guard-funnel-delta` uses `[regex]::IsMatch`, which does NOT. Copying its `re.compile` line
    from the neighbour would have silently WIDENED this guard to catch `derivedmodifier`,
    `APPLIEDCOMBAT` and `actorhub.resolve` — a different guard wearing the same name.
    """

    def test_uppercase_and_lowercase_spellings_are_both_findings(self) -> None:
        # CasingComposer.cs carries APPLIEDCOMBAT and derivedmodifier; LowerCtor.cs carries
        # new battlechannelmod; LowerResolve.cs carries stats.resolve.
        self.assertIn(f"{CORE}/Bad/CasingComposer.cs", self.flagged)
        self.assertIn(f"{CORE}/Bad/LowerCtor.cs", self.flagged)
        self.assertIn(f"{INJECTOR}/LowerResolve.cs", self.flagged)

    def test_the_flag_is_present_on_every_compiled_pattern(self) -> None:
        # Asserted structurally as well as behaviourally: a future edit to one re.compile that
        # drops the flag shows up here even if no fixture happens to exercise that pattern.
        for pattern in (guard.COMPOSER_TOUCHES, guard.NEW_CHANNEL_MOD, guard.STATS_RESOLVE,
                        guard.HUB_RESOLVE, guard.EMPTY_SOURCE_ID):
            self.assertTrue(pattern.flags & guard.re.IGNORECASE,
                            f"{pattern.pattern} lost its case folding")


class TheLookbehindIsTheRule(_FixtureCase):
    """Contract note 1b. `MyStats.Resolve(` is another type's member, not a bypass.

    Dropping `(?<![A-Za-z])` would flag every type whose name ends in `Stats`, which is a
    different rule with the same name.
    """

    def test_a_prefixed_member_access_is_not_a_bypass(self) -> None:
        self.assertNotIn(f"{INJECTOR}/NotABypass.cs", self.flagged)

    def test_bare_stats_resolve_is_a_bypass(self) -> None:
        self.assertIn(f"{INJECTOR}/Bypass.cs", self.flagged)
        self.assertTrue(guard.STATS_RESOLVE.search("Stats.Resolve(1)"))
        self.assertFalse(guard.STATS_RESOLVE.search("MyStats.Resolve(1)"))


class AlternativesAreNotTwoRequirements(_FixtureCase):
    """Contract note 1c, learned by shipping the bug this class prevents.

    The original's Hub-construction check is `-notmatch CreateDefault -and -notmatch 'new
    ActorHub'`: AT LEAST ONE. Written as a flat list of required patterns it reads as BOTH, and
    that shipped a false finding on the real tree — the file calls
    `ActorHubBootstrap.CreateDefault` and was reported as not constructing a Hub.
    """

    def test_one_alternative_alone_satisfies_the_requirement(self) -> None:
        self.assertNotIn(f"{SERVER}/UniqueActorHubCompose.cs", self.flagged)

    def test_the_alternatives_type_is_distinct_from_required(self) -> None:
        self.assertFalse(hasattr(guard.Required("x", "m"), "options"))
        both = guard.Alternatives(
            options=(guard.Required("ALPHA_MARKER", ""), guard.Required("BETA_MARKER", "")),
            message="m")
        self.assertTrue(both.satisfied("class X { BETA_MARKER y; }"))
        self.assertTrue(both.satisfied("class X { ALPHA_MARKER y; }"))
        self.assertFalse(both.satisfied("class X { GAMMA_MARKER y; }"))
        self.assertFalse(guard.Required("ALPHA_MARKER", "m").satisfied("class X { }"))

    def test_a_file_with_neither_alternative_is_a_finding(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        # Replace the satisfying file with one that constructs nothing.
        (root / SERVER / "UniqueActorHubCompose.cs").write_text(
            _cs("ResolveDerivedWithContributions(); EquippedBoundAtoms;"), encoding="utf-8")
        messages = [f["message"] for f in guard.scan(root)["findings"]
                    if f["file"] == f"{SERVER}/UniqueActorHubCompose.cs"]
        self.assertTrue(any("sole compose gate" in m for m in messages), messages)


class MissingIsAFindingOnlyWhereItShouldBe(_FixtureCase):
    """Contract note 2. The distinction is the guard's purpose, not sloppiness.

    `EntityApply.cs` disappearing means the Hub call it was required to make disappeared with it,
    so the guard must FAIL. `Program.cs` disappearing is none of this guard's business. Collapsing
    the two would either stop the guard noticing a deleted consumer, or make it fail for files it
    never cared about.
    """

    def test_a_missing_file_the_guard_requires_is_a_finding(self) -> None:
        self.assertTrue([f for f in self.result["findings"] if f.get("missing")])

    def test_a_missing_optional_file_is_silent(self) -> None:
        for absent in (f"{SERVER}/Program.cs",
                       f"{CORE}/Stats/Derived/Subsystems/StatusDerivedSubsystem.cs",
                       f"{INJECTOR}/CheatCommandRunner.cs"):
            self.assertFalse((self.root / absent).exists())
        self.assertNotIn(f"{SERVER}/Program.cs", self.flagged)

    def test_the_two_missing_messages_keep_the_original_prefix_difference(self) -> None:
        # R1 reports the repo-relative path (with `src/`); R4 reports the path as declared in its
        # own table (without). Preserved rather than made uniform, so a reader diffing against
        # the original sees no change.
        path = self.root / SERVER / "AuraDerivedEndpoints.cs"
        path.unlink()
        messages = [f["message"] for f in guard.scan(self.root)["findings"]
                    if f.get("missing") and "Aura" in f["message"]]
        self.assertEqual(len(messages), 1)
        self.assertIn("src/", messages[0].replace("\\", "/"))


class TheAllowlistsAreLiterals(_FixtureCase):
    """Contract note 3. Each entry is transcribed with the reason it is there.

    "Tidying" these — sorting, merging the obj/bin entries, widening a prefix — changes which
    files the guard forgives, and the failure is silent because the result is still a clean run.
    """

    def test_every_allowlist_entry_carries_its_reason(self) -> None:
        for table in (guard.ALLOW_COMPOSER, guard.ALLOW_CHANNEL_MOD, guard.ALLOW_STATS_RESOLVE):
            for pattern, why in table:
                self.assertTrue(why.strip(), f"allowlist entry {pattern!r} has no stated reason")

    def test_battlestatcomposer_is_not_allowlisted(self) -> None:
        # It was fused and deleted on 2026-09-13. Reintroducing a parallel composer is the whole
        # point of the rule, so it must not appear among the forgiven.
        for pattern, _ in guard.ALLOW_COMPOSER:
            self.assertNotIn("BattleStatComposer", pattern)

    def test_the_channnel_mod_allowlist_is_anchored_to_the_one_file(self) -> None:
        patterns = [p for p, _ in guard.ALLOW_CHANNEL_MOD]
        self.assertTrue(any(p.endswith(r"TraitAtomSource\.cs$") for p in patterns))
        # A same-named file in a subdirectory is NOT covered, which is why Sub/TraitAtomSource.cs
        # is flagged while Battle/TraitAtomSource.cs is not.
        self.assertIn(f"{CORE}/Battle/Sub/TraitAtomSource.cs", self.flagged)
        self.assertNotIn(f"{CORE}/Battle/TraitAtomSource.cs", self.flagged)

    def test_the_composer_glob_is_on_the_filename(self) -> None:
        # PlainCompose.cs contains AppliedCombat and is not flagged, because it does not match
        # *Composer*.cs. A port that globbed the CONTENT or matched a substring of the class name
        # would flag it.
        self.assertIn("Composer", guard.COMPOSER_GLOB)
        self.assertNotIn(f"{CORE}/PlainCompose.cs", self.flagged)


class CommentPolicyIsTheNarrowOne(_FixtureCase):
    """A whole-line comment is skipped; a trailing comment on real code is not."""

    def test_a_whole_line_comment_documentation_is_not_a_finding(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        (root / CORE / "DocumentedComposer.cs").write_text(
            "// AppliedCombat is forbidden in a parallel composer\n"
            "class DocumentedComposer { void Go() { } }\n", encoding="utf-8")
        self.assertNotIn(f"{CORE}/DocumentedComposer.cs",
                         {f["file"] for f in guard.scan(root)["findings"]})

    def test_a_trailing_comment_on_real_code_is_still_scanned(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        (root / CORE / "TrailingComposer.cs").write_text(
            "class TrailingComposer { void Go() { } } // AppliedCombat is forbidden\n",
            encoding="utf-8")
        self.assertIn(f"{CORE}/TrailingComposer.cs",
                      {f["file"] for f in guard.scan(root)["findings"]})


class SimEngineCarriesTwoRules(_FixtureCase):
    """R4 says SimEngine MUST call ActorHub.Resolve; R6 says it must NOT call Stats.Resolve.

    A file carrying the second without the first is the exact regression the pair exists to catch,
    so one path legitimately earns two findings. A port that deduplicated findings by file would
    hide the second, and the regression would read as one ordinary miss.
    """

    def test_one_path_can_earn_two_findings_from_two_rules(self) -> None:
        rules = {f["rule"] for f in self.result["findings"] if f["file"] == f"{CORE}/SimEngine.cs"}
        self.assertEqual(rules, {"hub-required", "sim-engine-stats-resolve"})


class ExitAndStreamContract(_FixtureCase):
    """Findings on stderr, a clean verdict on stdout.

    The original emitted both with `Write-Host`, which writes the INFORMATION stream where a
    `2>&1` capture cannot see it — so its findings reached a test's stdout only by accident.
    Stated once in docs/architecture/ps1-port-checklist.md item 4.
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
        self.assertIn("ACTOR-HUB GUARD FAILED", err)
        self.assertIn("AppliedCombat", err)

    def test_a_clean_tree_exits_zero_with_the_verdict_on_stdout(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        # Satisfy every rule: add the three absent required files, correctly populated.
        (root / INJECTOR / "Stats").mkdir(parents=True, exist_ok=True)
        (root / INJECTOR / "Stats" / "EntityApply.cs").write_text(
            _cs("ActorHub.Resolve();"), encoding="utf-8")
        (root / CORE / "SimEngine.cs").write_text(_cs("ActorHub.Resolve();"), encoding="utf-8")
        (root / INJECTOR / "Bypass.cs").unlink()
        (root / INJECTOR / "LowerResolve.cs").unlink()
        (root / CORE / "SimEngine.cs").write_text(_cs("ActorHub.Resolve();"), encoding="utf-8")
        for rel in (f"{CORE}/Bad/CasingComposer.cs", f"{CORE}/Bad/LowerCtor.cs",
                    f"{CORE}/Battle/OtherProducer.cs", f"{CORE}/Battle/Sub/TraitAtomSource.cs",
                    f"{CORE}/Elsewhere/StrayComposer.cs", f"{SERVER}/ServerProducer.cs"):
            (root / rel).unlink()
        code, out, err = self._run(["--root", str(root)])
        self.assertEqual(code, guard.EXIT_OK, out + err)
        self.assertIn("ACTOR-HUB GUARD OK", out)
        self.assertEqual(err, "")

    def test_json_carries_the_rules_and_the_findings(self) -> None:
        code, out, _ = self._run(["--root", str(self.root), "--json"])
        payload = json.loads(out)
        self.assertEqual(code, guard.EXIT_FINDINGS)
        self.assertEqual(payload["guard"], "actor-hub")
        self.assertEqual(payload["verdict"], "FAIL")
        self.assertEqual(len(payload["rules"]), len(guard.RULES))
        for item in payload["findings"]:
            for key in ("rule", "file", "message", "hint"):
                self.assertIn(key, item)
        self.assertTrue(set(payload["findings_by_rule"]) <= set(payload["rules"]))


class RefusalIsNamedAndClosed(_FixtureCase):
    """The original wrapped its sweeps in `if (Test-Path $Src)`, so a wrong `--root` reported OK
    and exited 0 — a green verdict for a run that examined nothing."""

    def test_a_root_without_a_source_tree_refuses(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        _drop_src(root)
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(root)
        self.assertEqual(caught.exception.reason, "MISSING_SOURCE_TREE")
        self.assertEqual(guard.main(["--root", str(root), "--json"]), guard.EXIT_REFUSED)

    def test_a_refusal_reports_on_stderr_and_keeps_the_exit_code_vocabulary(self) -> None:
        root, box = _fixture()
        self.addCleanup(box.cleanup)
        _drop_src(root)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = guard.main(["--root", str(root), "--json"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())
        self.assertEqual(json.loads(out.getvalue())["verdict"], "REFUSED")


if __name__ == "__main__":
    unittest.main()
