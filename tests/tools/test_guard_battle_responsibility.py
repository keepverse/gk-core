"""Contract tests for `gk-core/scripts/guard-battle-responsibility.py`.

What this asserts is the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals,
the `--json` envelope's key set, the CLOSED vocabulary of finding rules, and the behaviour of each
of the guard's three rules. What it deliberately does not assert is a message body, a line count, or
the number of mechanisms in the shipped register — those rot on every content change and guard
nothing. The register's size is a *reading*: it is the owner's list plus reviewed amendments, and it
changes when a human changes the law, not when content ships.

Differential evidence for equivalence with the retired `guard-battle-responsibility.ps1`: 21 fixtures,
one per rule, identical in exit code and every emitted line. That comparison is a one-time proof and
lives outside this file because the PowerShell form no longer exists to compare against.

The C# suite `gk-core/tests/FusionRpg.Guard.Tests/BattleResponsibilityGuardTests.cs` shells this same tool
and covers the unreadable-directory case through a real ACL denial, which cannot be reproduced
without `icacls`.
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
SCRIPT = REPO / "scripts" / "guard-battle-responsibility.py"

_spec = importlib.util.spec_from_file_location("guard_battle_responsibility", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_battle_responsibility"] = guard
_spec.loader.exec_module(guard)


def build(root: Path, sources: dict[str, str], registry: dict | None) -> Path:
    """A throwaway tree, so a real defect in the repo can never turn one of these green by accident."""
    for rel, text in sources.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    if registry is not None:
        (root / "scripts").mkdir(parents=True, exist_ok=True)
        (root / "scripts" / "battle-responsibility.v1.json").write_text(
            json.dumps(registry, indent=2), encoding="utf-8")
    return root


def run(root: Path, registry_path: Path | None = None, extra: list[str] | None = None) -> dict:
    argv = ["--root", str(root), "--json"]
    if registry_path is not None:
        argv += ["--registry-path", str(registry_path)]
    argv += extra or []
    proc = subprocess.run([sys.executable, str(SCRIPT), *argv],
                          capture_output=True, text=True, timeout=600)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


def check(root: Path, registry_path: Path) -> dict:
    """In-process, so a rule test costs no process spawn."""
    return guard.check(root, registry_path)


# The assignment is what the pattern requires. Getting this wrong is a fixture bug, not a tool bug,
# and a differential will not catch it: it proves the two implementations AGREE, not that the
# fixture means what its name says. Every rule test below was silently testing
# owner-does-not-match until this was corrected.
OWNER_SRC = "class Owner { void Step() { var t = this.Health = 1; } }\n"
OTHER_SRC = "class Other { void Step() { var t = this.Health = 1; } }\n"
PATTERN = r"this\.Health\s*="


def register(owner: str = "src/Owner.cs", pattern: str = PATTERN, **decision) -> dict:
    row = {"id": "d1", "pattern": pattern}
    row.update(decision)
    return {"mechanisms": [{"id": "m1", "name": "Health step", "owner": owner,
                            "decisions": [row]}]}


class CliSurface(unittest.TestCase):
    """The three flags a caller may rely on. A tool whose CLI drifts silently is a broken seam."""

    def test_the_documented_flags_are_accepted(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-cli-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, register())
            for flags in ([], ["--json"], ["--registry-path", str(root / "scripts" /
                                                                 "battle-responsibility.v1.json")]):
                with self.subTest(flags=flags):
                    result = run(root, extra=flags)
                    self.assertEqual(result["exit"], guard.EXIT_OK, result["stderr"])

    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("guard-battle-responsibility.py", proc.stdout)


class ExitCodeVocabulary(unittest.TestCase):
    """Two codes, and the distinction between them is the whole point of the envelope.

    `EXIT_FAILED` covers BOTH a real violation and a missing register, because the original reported
    a missing register as a FAILED verdict with exit 1 and a caller reading the code behaves the same
    either way. The two are told apart by `reason`, not by the code — which is exactly why the
    envelope has to exist.
    """

    def test_a_clean_tree_exits_zero(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-exit-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, register())
            self.assertEqual(run(root)["exit"], guard.EXIT_OK)

    def test_a_real_violation_exits_one(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-exit-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, register())
            result = run(root)
            self.assertEqual(result["exit"], guard.EXIT_FAILED)
            self.assertEqual(json.loads(result["stdout"])["verdict"], "FAIL")

    def test_the_exit_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})


class NamedRefusals(unittest.TestCase):
    """A refusal is NAMED. 'Continue and report empty' is the silent-green shape the ban exists for.

    Every precondition gets its own name, so a broken checkout is distinguishable from a finding
    without parsing prose.
    """

    def _refusal(self, tmp: str, registry: str | None) -> dict:
        root = Path(tmp)
        if registry is not None:
            build(root, {"src/Owner.cs": OWNER_SRC}, None)
            (root / "scripts").mkdir(parents=True, exist_ok=True)
            (root / "scripts" / "battle-responsibility.v1.json").write_text(registry, encoding="utf-8")
        else:
            build(root, {"src/Owner.cs": OWNER_SRC}, None)
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(root), "--json"],
            capture_output=True, text=True, timeout=600)
        return {"exit": proc.returncode, "payload": json.loads(proc.stdout)}

    def test_a_missing_register_is_named_and_still_exits_one(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-ref-") as tmp:
            got = self._refusal(tmp, None)
            self.assertEqual(got["exit"], guard.EXIT_FAILED)
            self.assertEqual(got["payload"]["reason"], "MISSING-REGISTRY")
            self.assertEqual(got["payload"]["verdict"], "FAILED")

    def test_unparseable_json_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-ref-") as tmp:
            got = self._refusal(tmp, "{ not json")
            self.assertEqual(got["payload"]["reason"], "REGISTRY-NOT-JSON")

    def test_a_register_without_mechanisms_is_named(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-ref-") as tmp:
            got = self._refusal(tmp, json.dumps({"somethingElse": []}))
            self.assertEqual(got["payload"]["reason"], "REGISTRY-SHAPE-UNEXPECTED")

    def test_every_refusal_reaches_the_envelope(self) -> None:
        # A refusal that only reaches stderr is invisible to a `--json` caller, which is the
        # PowerShell capture trap all over again: the process said something, the caller read nothing.
        for name in ("MISSING-REGISTRY", "UNREADABLE-REGISTRY", "REGISTRY-NOT-JSON",
                     "REGISTRY-SHAPE-UNEXPECTED"):
            with self.subTest(name=name):
                self.assertTrue(hasattr(guard.Refusal(name, "d").reason, "strip"))


class JsonEnvelope(unittest.TestCase):
    """The envelope's KEY SET is closed. A key that appears only on one code path is a broken contract."""

    KEYS = {"guard", "verdict", "mechanisms", "files_scanned", "findings",
            "findings_by_rule", "unreadable"}

    def test_a_clean_run_carries_every_key(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-json-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, register())
            payload = json.loads(run(root)["stdout"])
            self.assertEqual(self.KEYS, set(payload))
            self.assertEqual(payload["guard"], guard.GUARD_ID)
            self.assertEqual(payload["verdict"], "OK")
            self.assertEqual(payload["findings"], [])

    def test_a_failing_run_carries_the_same_keys(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-json-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, register())
            payload = json.loads(run(root)["stdout"])
            self.assertEqual(self.KEYS, set(payload))
            self.assertEqual(payload["verdict"], "FAIL")
            self.assertTrue(payload["findings"])

    def test_a_finding_carries_its_rule_and_nothing_volatile(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-json-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, register())
            for item in json.loads(run(root)["stdout"])["findings"]:
                self.assertEqual({"rule", "file", "message"}, set(item))


class FindingRuleVocabulary(unittest.TestCase):
    """The rule set is CLOSED. A sixth rule must be added deliberately, and this turns it red.

    This is the one place a literal is the right assertion: the rules are a vocabulary the code owns
    and a human changes, not a population that grows when content ships.
    """

    RULES = {"unfenced-mechanism", "allow-without-module", "owner-does-not-match",
             "uncompilable-pattern", "second-owner"}

    def test_the_vocabulary_is_exactly_these_five(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-rules-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, register())
            by_rule = check(root, root / "scripts" / "battle-responsibility.v1.json")["findings_by_rule"]
            self.assertTrue(set(by_rule) <= self.RULES, by_rule)
            self.assertIn("second-owner", by_rule)

    def test_an_uncompilable_pattern_is_a_finding_not_a_silent_pass(self) -> None:
        # The shape this pins: a pattern the engine cannot compile reported as "no hits" would make
        # the decision look ENFORCED when in fact nothing is checking it. A guard that cannot read its
        # own register must say so, and must not exit clean.
        with tempfile.TemporaryDirectory(prefix="br-rules-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, register(pattern="this.Health((("))
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "FAIL")
            self.assertIn("uncompilable-pattern", got["findings_by_rule"])


class RuleOneOwnerPerMechanism(unittest.TestCase):
    """A mechanism is decided in exactly one place. This is the question the guard exists for."""

    def _findings(self, sources, reg) -> list[str]:
        with tempfile.TemporaryDirectory(prefix="br-rule2-") as tmp:
            root = build(Path(tmp), sources, reg)
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "FAIL", "fixture must actually violate the rule")
            return got["findings_by_rule"]

    def test_a_second_file_writing_the_mechanism_is_reported(self) -> None:
        rules = self._findings({"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, register())
        self.assertIn("second-owner", rules)

    def test_the_failure_path_is_genuinely_exercised(self) -> None:
        # A guard proven only on success is half-proven, and the cheapest way to be half-proven is to
        # never assert that the bad tree is bad. This is that assertion.
        with tempfile.TemporaryDirectory(prefix="br-rule2-") as tmp:
            clean = build(Path(tmp) / "clean", {"src/Owner.cs": OWNER_SRC}, register())
            dirty = build(Path(tmp) / "dirty", {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC},
                          register())
            self.assertEqual(check(clean, clean / "scripts" /
                                   "battle-responsibility.v1.json")["verdict"], "OK")
            self.assertEqual(check(dirty, dirty / "scripts" /
                                   "battle-responsibility.v1.json")["verdict"], "FAIL")

    def test_an_owner_that_owns_nothing_is_reported(self) -> None:
        rules = self._findings({"src/Owner.cs": "class Owner { void Step() { } }\n"}, register())
        self.assertIn("owner-does-not-match", rules)

    def test_an_allowlisted_file_with_a_module_is_not_a_second_owner(self) -> None:
        reg = register(allow=[{"path": "src/Other.cs", "module": "moveset"}])
        with tempfile.TemporaryDirectory(prefix="br-rule2-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, reg)
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "OK", got["findings"])

    def test_an_allowlist_entry_naming_no_module_is_itself_a_finding(self) -> None:
        # "An exception with no module that will remove it is how grandfathered debt becomes a
        # template." The check has to be load-bearing or the exception becomes permanent.
        rules = self._findings({"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC},
                               register(allow=[{"path": "src/Other.cs"}]))
        self.assertIn("allow-without-module", rules)

    def test_a_banned_shape_flags_its_own_owner(self) -> None:
        # The reason `banned` exists: the owner was extracted, only the copies still spell it out,
        # and allowlisting the owner would permit the very formula it just stopped writing.
        rules = self._findings({"src/Owner.cs": OWNER_SRC}, register(shape="banned"))
        self.assertIn("second-owner", rules)

    def test_a_banned_shape_nobody_writes_is_clean(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-rule2-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": "class Owner { void Step() { } }\n"},
                         register(shape="banned"))
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "OK", got["findings"])


class RuleARowThatSaysNothingLooksEnforced(unittest.TestCase):
    """A mechanism with no decision pattern is either unimplemented or unexplained; both are findings."""

    def test_no_patterns_and_no_note_is_reported(self) -> None:
        reg = {"mechanisms": [{"id": "m1", "name": "Stub", "owner": "src/Owner.cs",
                               "decisions": []}]}
        with tempfile.TemporaryDirectory(prefix="br-rule1-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, reg)
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "FAIL")
            self.assertIn("unfenced-mechanism", got["findings_by_rule"])

    def test_no_patterns_but_a_note_is_clean(self) -> None:
        reg = {"mechanisms": [{"id": "m1", "name": "Stub", "owner": "src/Owner.cs",
                               "decisions": [], "note": "no mechanical signature exists yet"}]}
        with tempfile.TemporaryDirectory(prefix="br-rule1-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, reg)
            self.assertEqual(check(root, root / "scripts" /
                                   "battle-responsibility.v1.json")["verdict"], "OK")


class PatternSemantics(unittest.TestCase):
    """What counts as the mechanism, which is where a scan silently narrowing shows up as a clean run."""

    def _verdict(self, source: str) -> str:
        with tempfile.TemporaryDirectory(prefix="br-pat-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": source}, register())
            return check(root, root / "scripts" / "battle-responsibility.v1.json")["verdict"]

    def test_code_is_a_hit(self) -> None:
        self.assertEqual(self._verdict(OWNER_SRC), "OK")

    def test_a_string_literal_is_not_a_hit(self) -> None:
        # A mechanism must not be satisfied by text inside a string, or a log message that quotes the
        # formula satisfies the register and nothing implements it.
        self.assertEqual(
            self._verdict('class Owner { void L() { System.Console.WriteLine("var t = this.Health = 1;"); } }\n'),
            "FAIL")

    def test_a_comment_is_not_a_hit(self) -> None:
        self.assertEqual(self._verdict("class Owner { /* this.Health = 1; */ void Step() { } }\n"), "FAIL")

    def test_a_verbatim_string_is_not_a_hit(self) -> None:
        # `@"` is two ordinary characters to a scanner that does not know it, and the scan then walks
        # into the middle of the literal and out the wrong side.
        self.assertEqual(
            self._verdict('class Owner { void L() { var s = @"var t = this.Health = 1;"; } }\n'), "FAIL")

    def test_an_apostrophe_in_a_comment_does_not_blind_the_guard(self) -> None:
        # The failure this prevents is the dangerous direction: one stray apostrophe blanks every
        # following line, the owner stops matching, and the guard reports a finding about the CODE.
        self.assertEqual(
            self._verdict("class Owner { // don't touch\n void S() { var t = this.Health = 1; } }\n"),
            "OK")

    def test_patterns_fold_case(self) -> None:
        # PowerShell's `-match` folds case, and `[regex]::IsMatch` without IgnoreCase does not. The
        # whole register is matched case-insensitively, so getting this backwards would silently
        # narrow the guard to nothing.
        with tempfile.TemporaryDirectory(prefix="br-pat-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": "class O { void S() { var t = THIS.Health = 1; } }\n"},
                         register(pattern=r"this\.health\s*="))
            self.assertEqual(check(root, root / "scripts" /
                                   "battle-responsibility.v1.json")["verdict"], "OK")

    def test_the_register_s_own_spacing_is_what_patterns_are_written_against(self) -> None:
        # A block comment between two tokens becomes one space PER CHARACTER under the layout-
        # preserving stripper and a SINGLE space under the collapsing one, so a quantifier needing
        # three-or-more can only match under this one. That is what makes the two policies
        # non-interchangeable, and it is why the port honours the original's choice rather than
        # substituting the cheaper one: no pattern in the shipped register distinguishes them today
        # (all six use `\s*`), so a future whitespace-sensitive one is what would first notice.
        import cscan as scanner  # noqa: PLC0415  (proves the policy contrast, not the tool)

        source = "class O { void S() { var t = this./* gap */Health; } }\n"
        preserved = scanner.strip_comments_and_literals_preserving_layout(source)
        collapsed = scanner.strip_comments_and_literals(source)
        self.assertRegex(preserved, r"this\.\s{3,}Health")
        self.assertNotRegex(collapsed, r"this\.\s{3,}Health")

        with tempfile.TemporaryDirectory(prefix="br-pat-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": source},
                         register(pattern=r"this\.\s{3,}Health"))
            self.assertEqual(check(root, root / "scripts" /
                                   "battle-responsibility.v1.json")["verdict"], "OK")


class ScanSurface(unittest.TestCase):
    """Where the guard looks, and what it must refuse to look at."""

    def test_the_scan_roots_are_honoured(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-scan-") as tmp:
            root = build(Path(tmp), {"other/Third.cs": OTHER_SRC, "src/Owner.cs": OWNER_SRC},
                         {"scan": ["other"],
                          "mechanisms": [{"id": "m1", "name": "S", "owner": "src/Owner.cs",
                                          "decisions": [{"id": "d1", "pattern": PATTERN}]}]})
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "FAIL")
            self.assertIn("second-owner", got["findings_by_rule"])

    def test_an_empty_scan_falls_back_rather_than_scanning_nothing(self) -> None:
        # Scanning nothing and reporting clean is the silent-green shape: the guard would exit 0 on
        # every commit forever.
        with tempfile.TemporaryDirectory(prefix="br-scan-") as tmp:
            reg = register()
            reg["scan"] = []
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC, "src/Other.cs": OTHER_SRC}, reg)
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertIn("second-owner", got["findings_by_rule"])

    def test_build_output_is_never_source(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-scan-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC,
                                     "src/obj/Dead.cs": OTHER_SRC}, register())
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["verdict"], "OK", got["findings"])
            self.assertEqual(got["files_scanned"], 1)

    def test_a_directory_that_does_not_exist_is_not_a_failure(self) -> None:
        with tempfile.TemporaryDirectory(prefix="br-scan-") as tmp:
            root = build(Path(tmp), {"src/Owner.cs": OWNER_SRC}, register())
            got = check(root, root / "scripts" / "battle-responsibility.v1.json")
            self.assertEqual(got["unreadable"], [])
            self.assertEqual(got["verdict"], "OK")


class TheShippedRegister(unittest.TestCase):
    """The real register, and the real tree. Its SIZE is a reading, so no count is pinned."""

    def test_the_shipped_register_is_green(self) -> None:
        result = guard.check(REPO, REPO / "scripts" / "battle-responsibility.v1.json")
        self.assertEqual(result["verdict"], "OK", result["findings"])
        self.assertGreater(result["files_scanned"], 0)

    def test_the_shipped_register_declares_at_least_one_mechanism(self) -> None:
        # Not a count: an EMPTY register would make every other assertion here vacuously true.
        data = json.loads((REPO / "scripts" / "battle-responsibility.v1.json").read_text(
            encoding="utf-8"))
        self.assertTrue(data.get("mechanisms"))

    def test_the_cli_agrees_with_the_in_process_run(self) -> None:
        # Both modes, because a tool whose human output and machine output disagree is the worst
        # version of this defect: a human reads OK while CI reads FAIL.
        as_json = run(REPO)
        as_text = subprocess.run([sys.executable, str(SCRIPT), "--root", str(REPO)],
                                 capture_output=True, text=True, timeout=600)
        self.assertEqual(as_json["exit"], guard.EXIT_OK, as_json["stderr"])
        self.assertEqual(json.loads(as_json["stdout"])["verdict"], "OK")
        self.assertEqual(as_text.returncode, guard.EXIT_OK, as_text.stderr)
        self.assertIn(guard.VERDICT_OK.split("(")[0], as_text.stdout)


if __name__ == "__main__":
    unittest.main()
