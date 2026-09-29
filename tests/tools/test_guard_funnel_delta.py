"""Contract tests for `gk-fusion/scripts/guard-funnel-delta.py`.

The expected finding set is the one MEASURED by running `guard-funnel-delta.ps1` and the port
over the same fixture: 16 findings, same files, same patterns, same exit code, 0 divergences. The
fixture was built to break a careless port, not to exercise the happy path, because a guard that
stopped looking also reports a clean tree and would pass every test written against the clean
tree.

Each class below pins one property a rewrite silently loses. Read the class docstring for which.
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
    # Register BEFORE exec. A dataclass whose annotations are strings (PEP 563, which the guard
    # enables) resolves `cls.__module__` through sys.modules at DECORATION time, so a module that
    # is not registered yet raises `AttributeError: 'NoneType' object has no attribute
    # '__dict__'` from inside dataclasses.py - an error that names neither this file nor the
    # cause. The importlib recipe is incomplete without this line.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


guard = _load("guard_funnel_delta", REPO / "scripts" / "guard-funnel-delta.py")
cscan = _load("cscan", REPO / "scripts" / "cscan.py")

PLUGINS = "src/FusionRpg.Core/Effects/Plugins"
CORE = "src/FusionRpg.Core"
INJECTOR = "src/FusionRpg.Injector"


def _cs(body: str) -> str:
    return f"public class C {{ void Go() {{ {body} }} }}\n"


# The measured contract: this file's verdict is FLAGGED, and the reasons are the specific
# patterns below. Asserting the PATTERN SET (a closed vocabulary the guard owns) rather than a
# count, so the test fails when a rule is lost and stays green when an unrelated file appears.
MEASURED = {
    f"{PLUGINS}/AllPatterns.cs": {
        "TakeDamage", "SetHp", r"thePlantHealth\s*=", r"theHealth\s*=",
        r"Bag\.Grant", r"ctx\.Bag\.Grant",
    },
    f"{PLUGINS}/BareGrant.cs": {r"Bag\.Grant"},
    f"{PLUGINS}/TrailingComment.cs": {"TakeDamage"},
    f"{CORE}/Other/DeclaredElsewhere.cs": {"TakeDamage"},
    f"{CORE}/Battle/CoreFanOut.cs": {
        "EntityStatWriter", "AddPlantHp", "AddZombieHp", "targetPtrs",
    },
    f"{INJECTOR}/Combat/InjectorWriter.cs": {
        r"EntityStatWriter\.AddPlantHp", r"EntityStatWriter\.AddZombieHp", "targetPtrs",
    },
}

# These must produce NO finding. Each is here for a reason named in its own fixture comment.
MEASURED_CLEAN = (
    f"{PLUGINS}/DocumentedNotBroken.cs",      # whole-line comment documents the rule
    f"{PLUGINS}/BlockBody.cs",                # conventional /* * */ body is stripped
    f"{PLUGINS}/LowercaseOnly.cs",            # matching is case-SENSITIVE
    f"{PLUGINS}/EffectFunnel.cs",             # the funnel names what it must not do
    f"{CORE}/Effects/EffectFunnel.cs",        # ... in the core scope too
    f"{INJECTOR}/Combat/EntityStatWriter.cs",  # FA10 sink exempt by file name
    f"{INJECTOR}/Combat/InjectorEffectActionSink.cs",
    f"{PLUGINS}/obj/Debug/Generated.cs",      # build output is not source
    f"{INJECTOR}/obj/Debug/GenInjector.cs",
    f"{CORE}/binding/Legit.cs",               # `binding` is not the `bin` segment
)


def _fixture(files: dict[str, str]) -> Path:
    """Build a tree in a temp dir. addCleanup means a failed delete FAILS the test - the
    standard forbids an empty catch around Directory.Delete, which is how a local run once
    leaked 65.5 GB of temp directories."""
    box = tempfile.TemporaryDirectory(prefix="funnel-delta-test-")
    root = Path(box.name).resolve()
    for rel, body in files.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
    return root, box


def _with_scopes(files: dict[str, str]) -> Path:
    """A fixture that HAS all three scan scopes, because the guard refuses a missing one.

    Minimal fixtures have to create the scopes they do not exercise, empty. That is a deliberate
    consequence of failing closed on a missing scope: a test for the injector rule cannot point
    at a tree with no Core directory, which is correct - such a tree is not this repo.
    """
    seeds = dict(files)
    for scope in (PLUGINS, CORE, INJECTOR):
        seeds.setdefault(f"{scope}/_ScopeExists.cs", "public class ScopeExists { }\n")
    return _fixture(seeds)


def _full_fixture() -> dict[str, str]:
    return {
        f"{PLUGINS}/AllPatterns.cs": _cs(
            "TakeDamage(1); SetHp(2); thePlantHealth = 3; theHealth = 4; ctx.Bag.Grant(x);"),
        f"{PLUGINS}/BareGrant.cs": _cs("Bag.Grant(x);"),
        f"{PLUGINS}/DocumentedNotBroken.cs": (
            "// TakeDamage must not appear here; SetHp neither.\n// theHealth = 1\n"
            "public class DocumentedNotBroken { }\n"),
        f"{PLUGINS}/TrailingComment.cs": (
            "public class TrailingComment { } // TakeDamage is forbidden here\n"),
        f"{PLUGINS}/BlockBody.cs": (
            "/*\n * TakeDamage is documented as forbidden here, mid-block.\n */\n"
            "public class BlockBody { }\n"),
        f"{PLUGINS}/LowercaseOnly.cs": _cs("sethp(1); takedamage(2);"),
        f"{PLUGINS}/EffectFunnel.cs": _cs("TakeDamage(1); SetHp(2); Bag.Grant(x);"),
        f"{CORE}/Other/DeclaredElsewhere.cs": (
            "public class D : IEffectGrantPlugin { void Go() { TakeDamage(1); } }\n"),
        f"{CORE}/Battle/CoreFanOut.cs": _cs(
            "EntityStatWriter.AddPlantHp(1); AddZombieHp(2); var p = targetPtrs;"),
        f"{CORE}/Effects/EffectFunnel.cs": _cs(
            "EntityStatWriter.AddPlantHp(1); var p = targetPtrs;"),
        f"{INJECTOR}/Combat/InjectorWriter.cs": (
            "public class W { void Go() { EntityStatWriter.AddPlantHp(1);\n"
            "EntityStatWriter.AddZombieHp(2); var p = targetPtrs; } }\n"
            "// EntityStatWriter.AddPlantHp in a comment is STILL a finding here\n"),
        f"{INJECTOR}/Combat/EntityStatWriter.cs": _cs(
            "EntityStatWriter.AddPlantHp(1); var p = targetPtrs;"),
        f"{INJECTOR}/Combat/InjectorEffectActionSink.cs": _cs("EntityStatWriter.AddZombieHp(1);"),
        f"{PLUGINS}/obj/Debug/Generated.cs": _cs("TakeDamage(1);"),
        f"{INJECTOR}/obj/Debug/GenInjector.cs": _cs("EntityStatWriter.AddPlantHp(1);"),
        f"{CORE}/binding/Legit.cs": "public class Legit { void Go() { } }\n",
    }


class _FixtureCase(unittest.TestCase):
    def setUp(self) -> None:
        self.root, self._box = _fixture(_full_fixture())
        self.addCleanup(self._box.cleanup)
        self.result = guard.scan(self.root)
        self.hits: dict[str, set[str]] = {}
        for finding in self.result["findings"]:
            self.hits.setdefault(finding["file"], set()).add(finding["pattern"])


class TheMeasuredContract(_FixtureCase):
    """The finding set both implementations produced, asserted per file and per pattern.

    This is the differential's durable form. Asserting the closed PATTERN VOCABULARY per file,
    not a total count, means a lost rule turns this red while an unrelated new source file does
    not - the difference between a contract test and a population test.
    """

    def test_each_violating_file_reports_exactly_its_measured_patterns(self) -> None:
        for rel, patterns in MEASURED.items():
            with self.subTest(file=rel):
                self.assertEqual(self.hits.get(rel, set()), patterns)

    def test_each_exempt_file_reports_nothing(self) -> None:
        for rel in MEASURED_CLEAN:
            with self.subTest(file=rel):
                self.assertNotIn(rel, self.hits)

    def test_no_file_outside_the_fixture_is_reported(self) -> None:
        for rel in self.hits:
            self.assertIn(rel, MEASURED, f"unexpected finding in {rel}")


class RedundancyIsPreserved(_FixtureCase):
    """`ctx.Bag.Grant` is subsumed by `Bag.Grant`, so one call yields TWO findings.

    A port that de-duplicates pattern hits - which looks like an improvement and is not - would
    report one. Findings are per-pattern and operators read the count, so the redundancy is the
    shipped contract. This is the kind of detail a "tidy-up" changes without anyone noticing.
    """

    def test_a_ctx_qualified_grant_is_reported_under_both_patterns(self) -> None:
        patterns = self.hits[f"{PLUGINS}/AllPatterns.cs"]
        self.assertIn(r"Bag\.Grant", patterns)
        self.assertIn(r"ctx\.Bag\.Grant", patterns)

    def test_an_unqualified_grant_is_reported_once(self) -> None:
        self.assertEqual(self.hits[f"{PLUGINS}/BareGrant.cs"], {r"Bag\.Grant"})


class MatchingIsCaseSensitive(_FixtureCase):
    """A narrowing rule is still a change of rule. .NET's IsMatch is case-sensitive by default,
    and a port that added re.IGNORECASE would start flagging `sethp` - passing every positive
    test while quietly widening what the guard rejects."""

    def test_a_lowercase_call_is_not_a_finding(self) -> None:
        self.assertNotIn(f"{PLUGINS}/LowercaseOnly.cs", self.hits)


class CommentPolicyIsTheNarrowOne(_FixtureCase):
    """Whole-line comments are skipped; a TRAILING comment on real code is not.

    The guard's own comment states the policy: the rule is narrowed for documentation and not
    weakened for code. Upgrading to full comment-stripping would satisfy the 2026-09-04 incident
    and also stop scanning trailing comments - a contract change the guard's owner has not asked
    for, so it is preserved and named rather than quietly applied.
    """

    def test_a_whole_line_comment_documenting_the_rule_is_not_a_finding(self) -> None:
        self.assertNotIn(f"{PLUGINS}/DocumentedNotBroken.cs", self.hits)

    def test_a_trailing_comment_on_real_code_is_still_scanned(self) -> None:
        self.assertIn(f"{PLUGINS}/TrailingComment.cs", self.hits)


class ScopingRules(_FixtureCase):
    def test_a_plugin_declared_outside_the_plugin_directory_is_in_scope(self) -> None:
        self.assertIn(f"{CORE}/Other/DeclaredElsewhere.cs", self.hits)

    def test_the_funnel_is_exempt_in_both_scopes(self) -> None:
        self.assertNotIn(f"{PLUGINS}/EffectFunnel.cs", self.hits)
        self.assertNotIn(f"{CORE}/Effects/EffectFunnel.cs", self.hits)

    def test_the_fa10_sink_is_exempt_by_file_name_not_by_path(self) -> None:
        self.assertNotIn(f"{INJECTOR}/Combat/EntityStatWriter.cs", self.hits)
        self.assertNotIn(f"{INJECTOR}/Combat/InjectorEffectActionSink.cs", self.hits)

    def test_build_output_is_not_source(self) -> None:
        self.assertNotIn(f"{PLUGINS}/obj/Debug/Generated.cs", self.hits)
        self.assertNotIn(f"{INJECTOR}/obj/Debug/GenInjector.cs", self.hits)

    def test_a_directory_named_binding_is_not_the_bin_segment(self) -> None:
        # The exclusion matches a path SEGMENT. A substring test would drop every file under
        # `binding`, silently shrinking the scanned set on a real tree.
        sep = chr(92)
        for directory in ("obj", "bin"):
            self.assertIsNotNone(guard.BUILD_OUTPUT.search(rf"src{sep}{directory}{sep}X.cs"))
            self.assertIsNotNone(guard.BUILD_OUTPUT.search(rf"src{sep}X{sep}{directory}{sep}Y.cs"))
        self.assertIsNone(guard.BUILD_OUTPUT.search(rf"a{chr(92)}binding{chr(92)}X.cs"))
        self.assertIsNone(guard.BUILD_OUTPUT.search(rf"a{chr(92)}Combine{chr(92)}X.cs"))
        self.assertEqual([p.name for p in guard.source_files(self.root, f"{CORE}/binding")],
                         ["Legit.cs"])


class TheInjectorScopeScansRawText(_FixtureCase):
    """The Injector rule matches RAW text while the other two match comment-stripped text.

    That asymmetry is in the original (it passes `$text` at one call site and `$code` at the
    others). Harmonising it would stop the injector rule flagging comments, which is a rule
    change nobody requested. The fixture makes it observable: the same trailing comment is a
    finding in the injector scope and not in the plugin scope.
    """

    def test_the_injector_rule_does_not_strip_comments(self) -> None:
        injector = next(r for r in guard.RULES if r.rule == "injector-multi-ptr")
        secondary = next(r for r in guard.RULES if r.rule == "secondary-bypass")
        self.assertFalse(injector.strip_comments)
        self.assertTrue(secondary.strip_comments)

    def test_a_commented_injector_call_is_still_reported(self) -> None:
        root, box = _with_scopes({
            f"{INJECTOR}/OnlyComment.cs": (
                "// EntityStatWriter.AddPlantHp(1)\npublic class OnlyComment { }\n"),
        })
        self.addCleanup(box.cleanup)
        result = guard.scan(root)
        self.assertEqual([f["pattern"] for f in result["findings"]],
                         [r"EntityStatWriter\.AddPlantHp"])


class WholeLineStrippingPreservesLineNumbers(unittest.TestCase):
    """Comment lines are BLANKED, not removed, so a reported line number is the file's own.

    A finding that points at the wrong line sends an operator to the wrong place, which is worse
    than no line number at all - so the blanking is load-bearing and needs its own proof.
    """

    def test_line_numbers_survive_comment_lines_above_them(self) -> None:
        text = "// header comment\n// another\nclass C { void Go() { TakeDamage(1); } }\n"
        stripped = cscan.strip_whole_line_comments(text)
        self.assertEqual(cscan.line_of(stripped, stripped.index("TakeDamage")), 3)

    def test_the_stripped_text_has_the_same_number_of_lines(self) -> None:
        text = "// a\nclass C { }\n// b\nclass D { }\n"
        self.assertEqual(len(cscan.strip_whole_line_comments(text).split("\n")),
                         len(text.split("\n")))

    def test_a_conventional_block_body_is_stripped(self) -> None:
        # The docstring's residual-gap claim was WRONG on first write and the guard-funnel-delta
        # differential disproved it. This is the narrower, measured truth.
        body = "/*\n * TakeDamage\n */\nclass C { }"
        self.assertNotIn("TakeDamage", cscan.strip_whole_line_comments(body))

    def test_a_block_body_without_the_star_prefix_survives(self) -> None:
        body = "/*\nTakeDamage\n*/\nclass C { }"
        self.assertIn("TakeDamage", cscan.strip_whole_line_comments(body))

    def test_code_is_never_removed(self) -> None:
        body = "class C { void Go() { var x = 1; } }\n"
        self.assertEqual(cscan.strip_whole_line_comments(body), body)


class ExitAndStreamContract(_FixtureCase):
    """Findings on stderr, a clean verdict on stdout - the rule the PowerShell form could not
    express, because Write-Host wrote both to the INFORMATION stream where `2>&1` cannot see
    them. Four consecutive ports each rediscovered this at the cost of a red run; it is stated
    once in docs/architecture/ps1-port-checklist.md item 4 and pinned here.
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
        self.assertIn("FUNNEL DELTA GUARD FAILED", err)
        self.assertIn("TakeDamage", err)

    def test_a_clean_tree_exits_zero_with_the_verdict_on_stdout(self) -> None:
        root, box = _with_scopes({f"{PLUGINS}/Clean.cs": "public class Clean { }\n"})
        self.addCleanup(box.cleanup)
        code, out, err = self._run(["--root", str(root)])
        self.assertEqual(code, guard.EXIT_OK)
        self.assertIn("FUNNEL DELTA GUARD OK", out)
        self.assertEqual(err, "")

    def test_json_carries_the_finding_set_not_just_a_verdict(self) -> None:
        code, out, _ = self._run(["--root", str(self.root), "--json"])
        payload = json.loads(out)
        self.assertEqual(code, guard.EXIT_FINDINGS)
        self.assertEqual(payload["verdict"], "FAIL")
        self.assertEqual(payload["guard"], "funnel-delta")
        self.assertGreater(payload["scanned_files"], 0)
        self.assertEqual(set(payload["findings_by_rule"]),
                         {"secondary-bypass", "core-fanout", "injector-multi-ptr"})
        for finding in payload["findings"]:
            for key in ("rule", "file", "line", "pattern", "message", "hint"):
                self.assertIn(key, finding)
            self.assertGreaterEqual(finding["line"], 1)


class RefusalIsNamedAndClosed(_FixtureCase):
    """The original wrapped every scan in `if (Test-Path ...)` and reported OK, so pointing it at
    the wrong directory passed. That is the silent-green shape this migration exists to remove, so
    here a missing tree REFUSES with a named reason and exit 64 - and the reason is checked, not
    the exit code alone, because a refusal nobody can name is a refusal nobody can act on.
    """

    def test_a_root_without_a_source_tree_refuses(self) -> None:
        root, box = _fixture({"README.md": "nothing here\n"})
        self.addCleanup(box.cleanup)
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(root)
        self.assertEqual(caught.exception.reason, "MISSING_SOURCE_TREE")
        self.assertEqual(guard.main(["--root", str(root), "--json"]), guard.EXIT_REFUSED)

    def test_a_missing_scoped_directory_names_the_scope(self) -> None:
        root, box = _fixture({f"{PLUGINS}/Only.cs": "public class Only { }\n"})
        self.addCleanup(box.cleanup)
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(root)
        self.assertEqual(caught.exception.reason, "MISSING_SCOPE")
        declared = {scope for rule in guard.RULES for scope in rule.scope}
        self.assertIn(caught.exception.detail, declared,
                      "the refusal must name WHICH scope is absent, so it can be acted on")

    def test_a_refusal_reports_on_stderr_and_keeps_the_exit_code_vocabulary(self) -> None:
        root, box = _fixture({"README.md": "nothing here\n"})
        self.addCleanup(box.cleanup)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = guard.main(["--root", str(root), "--json"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("REFUSED", err.getvalue())
        self.assertEqual(json.loads(out.getvalue())["verdict"], "REFUSED")


if __name__ == "__main__":
    unittest.main()
