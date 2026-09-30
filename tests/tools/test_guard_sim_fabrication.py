"""Contract tests for `gk-core/scripts/guard-sim-fabrication.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, both directions of rule 6, the order-sensitivity of rule 3, the two allowlists as
CLOSED registries, and the two behaviours that are easy to "fix" by accident.

WHY THE ALLOWLIST MEMBERSHIP IS PINNED WHILE THE COUNTS ARE NOT
The allowlists are the closed vocabularies the code owns and a human changes by review, and rule 6 makes
a stale entry a violation -- so the membership IS the contract and is pinned exactly, with the reason
stated (validation-ssot.md). The guard's READINGS are the opposite: `scenarios`, `steps`, `reads`,
handler counts and `golden_skipped` move whenever a scenario or a route ships, so they are range-checked
and never compared to a literal. `test.seed.souls` and `test.expedition.due` are both pinned because the
SECOND is retired and stays anyway, and the reason it stays is the whole reason it is in this file.

WHY THE DIFFERENTIAL'S CASES ARE REPEATED HERE
The differential proves the port against `guard-sim-fabrication.ps1`, and it dies with that file. Every
rule it proved moves here too; what does not move is the comparison.

THE TWO BEHAVIOURS THAT ARE EASY TO BREAK BY ACCIDENT
  * The span anchor is the COMMA after a route path, not the opening parenthesis, because the
    registration regex ends at the comma. The "unbalanced handler span" branch therefore cannot fire,
    and the span it returns happens to contain the parameter list, which is why `TakesStore` works.
    Both are pinned, so a later fix to the anchor is a DECISION the tests will ask about rather than a
    silent behaviour change.
  * The scan runs on the literal-PRESERVING view, so `RpgStore` inside a string literal still sets the
    store flag. Tightening it to the blanking view would fix the false positive and would also stop
    matching the real parameter, whose type annotation IS a string literal.

Differential evidence: 58 fixtures - 56 correct against their own stated expectation, 54 byte-identical
in exit code, report and finding set, 2 declared divergences (the two refusals the original had no name
for), 0 unexplained. The remaining declared differences are the stream discipline (findings on stderr),
the message naming the `.py` a reader must edit, and the 8.3 path reconciliation.
"""

from __future__ import annotations

import pathlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
# The environment override exists for MUTATION FALSIFICATION: a suite that can only ever load one path
# cannot be pointed at a deliberately broken copy, so "break the implementation and watch a test go red"
# would have no way to run. It changes which FILE is loaded and nothing else.
SCRIPT = Path(os.environ.get("GUARD_SIM_FABRICATION_SCRIPT",
                             REPO / "scripts" / "guard-sim-fabrication.py")).resolve()
RUN_TIMEOUT = 1800

_spec = importlib.util.spec_from_file_location("guard_sim_fabrication", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_sim_fabrication"] = guard
_spec.loader.exec_module(guard)

SIM_TAKES_STORE = "takes RpgStore"
ESCAPES = "walk"  # unused sentinel kept out of the vocabulary; see ESCAPES_UNUSED below

# The closed registries, pinned with their REASON rather than by count alone.
EXPECTED_TEST_OPS = {
    "test.seed.souls",
    "test.expedition.due",  # RETIRED, and still here so the RS4 planted-violation test reaches the
                            # notes rule. Removing it silently disables that rule's reach.
}
EXPECTED_TEST_STORE_ROUTES = {
    "/api/test/reset", "/api/test/snapshot", "/api/test/seed-pvz-stats-demo",
    "/api/test/seed-pvz-activity-demo", "/api/test/seed-rpg-progression-demo",
    "/api/test/seed-souls-demo", "/api/test/web-match", "/api/test/seed-materials",
    "/api/test/mint-creature", "/api/test/contracts/settle", "/api/test/world/create",
}
EXIT_VOCABULARY = {0, 1}

VOCAB = """using System;
namespace RpgSim;
public static class ScenarioVocabulary
{
    public static readonly ScenarioOp[] Calls = new[]
    {
        new ScenarioOp("api.player.read", "api", "GET", "/api/players"),
        new ScenarioOp("sim.tick", "sim", "POST", "/api/sim/tick"),
        new ScenarioOp("test.seed.souls", "test", "POST", "/api/test/seed-souls-demo"),
    };
}
"""


def _all_store_routes() -> str:
    return "".join(
        f'        test.MapPost("{route[len("/api/test"):]}", (RpgStore store) => {{ return Results.Ok(); }});\n'
        for route in sorted(EXPECTED_TEST_STORE_ROUTES))


SERVER_OK = """using System;
public static class Endpoints
{
    public static void MapAll(WebApplication app)
    {
        var sim = app.MapGroup("/api/sim");
        var test = app.MapGroup("/api/test");
        sim.MapPost("/tick", (HttpContext ctx) => { return Results.Ok(); });
""" + _all_store_routes() + """    }
}
"""


def scenario(steps, *, clock=None, notes=None, scenario_id="probe"):
    doc = {"id": scenario_id,
           "clock": {"mode": "offset", "note": "fixed"} if clock is None else clock,
           "steps": steps}
    if notes is not None:
        doc["notes"] = notes
    return json.dumps(doc, indent=2)


def step(op, **fields):
    return {"op": op, **fields}


READ = step("read.players", route="GET /api/players")
CLEAN = scenario([READ, step("expect.p", reading="read.players", other="read.players#/$name"),
                  step("digest.d", include=["read.players#/$name"])])


class TreeCase(unittest.TestCase):
    """Each test builds its own throwaway repository: the guard's findings are per-file, so a shared
    tree would let one test's fixture become another test's subject."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="guard-sfab-test-")
        self.root = Path(self._tmp.name) / "tree"
        (self.root / "tools" / "RpgSim").mkdir(parents=True)
        (self.root / "tests" / "fixtures" / "rpg-scenarios").mkdir(parents=True)
        (self.root / "tools" / "RpgSim" / "ScenarioVocabulary.cs").write_text(VOCAB, encoding="utf-8")
        self.server(SERVER_OK)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def server(self, code: str, name: str = "Endpoints.cs") -> None:
        directory = self.root / "src" / "FusionRpg.Server"
        path = directory / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(code, encoding="utf-8")

    def scenario_file(self, body: str, name: str = "probe.json") -> None:
        (self.root / "tests" / "fixtures" / "rpg-scenarios" / name).write_text(body, encoding="utf-8")

    def invoke(self, *argv: str) -> tuple[int, str, str]:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root), *argv],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        return proc.returncode, proc.stdout, proc.stderr

    def result(self) -> dict:
        code, out, _err = self.invoke("--json")
        self.assertIn(code, EXIT_VOCABULARY, f"exit {code} is outside the vocabulary")
        try:
            return json.loads(out[out.index("{"):])
        except (ValueError, json.JSONDecodeError) as exc:  # noqa: PERF203
            self.fail(f"--json did not emit one JSON document: {exc}\n{out[:400]}")

    def violations(self) -> list[str]:
        return self.result()["violations"]


class CliSurface(TreeCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--scenario-dir", "--server-dir", "--vocabulary-path", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_takes_no_PowerShell_spelled_flag(self) -> None:
        """A port that still answered `-Root` would let a caller keep the old invocation alive and never
        notice the retirement, which is the whole failure this migration exists to prevent."""
        proc = subprocess.run([sys.executable, str(SCRIPT), "-Root", str(self.root)],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0, f"the port still accepts -Root:\n{proc.stdout}")
        self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower())

    def test_it_does_not_shell_out_to_a_PowerShell_interpreter(self) -> None:
        """A Python tool that shells back out to `pwsh` is a wrapper, not a port."""
        source = SCRIPT.read_text(encoding="utf-8")
        for token in ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "PSTypeName"):
            self.assertNotIn(token, source, f"the port still references {token!r}")

    def test_it_consumes_the_shared_scanner_rather_than_reimplementing_it(self) -> None:
        """A private copy is a second stripper, and a second stripper is a second set of bugs."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("import cscan", source, "the guard must consume the shared scanner")
        # THE SHARED FILE, not the module's IDENTITY. `assertIs(guard.cscan, sys.modules["cscan"])`
        # reads as the same claim and is not: five suites in this tree each register
        # `sys.modules["cscan"]` themselves - test_cscan, test_guard_dal, test_guard_funnel_delta,
        # test_guard_single_writer, test_guard_test_substrate - so two module objects can exist for the
        # ONE cscan.py, and the assertion then depends on load order. Measured: this test passes alone,
        # passes in file context, and fails in the full suite, with the failure text reading
        # `<module 'cscan' from ...gk-core/scripts/cscan.py> is not <module 'cscan' from
        # ...gk-core/scripts/cscan.py>` - the same path on both sides.
        #
        # The property the docstring actually states is that the guard consumes the SHARED scanner
        # rather than a private copy, and that is a question about the FILE the module was loaded from.
        # A private copy would have a different `__file__`, so this is not weaker - it is the claim
        # itself, expressed without the ordering coupling.
        self.assertEqual(
            pathlib.Path(guard.cscan.__file__).resolve(),
            (REPO / "scripts" / "cscan.py").resolve(),
            "the guard must consume the shared scanner, not a private copy of it")
        self.assertIn("strip_comments_preserving_layout", source)


class EnvelopeAndVerdicts(TreeCase):
    OK_KEYS = {"guard", "verdict", "violations", "scenarios", "steps", "reads", "test_steps",
               "golden_skipped", "sim_handlers", "test_handlers", "test_store_routes",
               "test_store_allowlist", "test_op_allowlist"}
    REFUSED_KEYS = {"guard", "verdict", "reason", "detail", "violations"}

    def test_a_clean_tree_is_OK_with_exit_zero(self) -> None:
        self.scenario_file(CLEAN)
        payload = self.result()
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["violations"], [])

    def test_a_finding_is_FAIL_with_exit_one(self) -> None:
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        code, out, err = self.invoke()
        self.assertEqual(code, 1)
        self.assertIn("SIM FABRICATION GUARD FAILED", err)
        self.assertNotIn("walk-escapes-root", out)  # nothing leaked onto stdout

    def test_the_envelope_key_set_is_closed_in_both_states(self) -> None:
        """An envelope, not a population: the keys are the contract, so a NEW key is something a caller
        can come to depend on and a DROPPED key is a silent removal."""
        self.scenario_file(CLEAN)
        self.assertEqual(set(self.result()), self.OK_KEYS)
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        self.assertEqual(set(self.result()), self.OK_KEYS)
        (self.root / "tools" / "RpgSim" / "ScenarioVocabulary.cs").unlink()
        self.assertEqual(set(self.result()), self.REFUSED_KEYS)

    def test_a_refusal_is_its_OWN_verdict_not_a_finding(self) -> None:
        """A caller that reads `FAIL` as 'the tree is dishonest' would report a broken invocation as a
        dishonest corpus, and a guard that cannot tell those apart trains people to ignore it."""
        (self.root / "tools" / "RpgSim" / "ScenarioVocabulary.cs").unlink()
        self.scenario_file(CLEAN)
        payload = self.result()
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], "VOCABULARY-MISSING")
        self.assertEqual(payload["violations"], [], "a refusal must not invent findings")

    def test_an_empty_vocabulary_is_refused_not_treated_as_an_empty_table(self) -> None:
        """An empty table makes EVERY sim/test/api op a violation, so a scanner that silently found
        nothing would read as a wall of red instead of as a broken precondition."""
        (self.root / "tools" / "RpgSim" / "ScenarioVocabulary.cs").write_text(
            "public static class V { public static readonly ScenarioOp[] Calls = new[] { }; }\n",
            encoding="utf-8")
        self.scenario_file(CLEAN)
        payload = self.result()
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], "VOCABULARY-EMPTY")
        self.assertIn("ScenarioVocabulary.cs", payload["detail"])

    def test_the_readings_are_INTEGERS_that_move_and_are_never_pinned(self) -> None:
        self.scenario_file(CLEAN)
        payload = self.result()
        for key in ("scenarios", "steps", "reads", "test_steps", "golden_skipped", "sim_handlers",
                    "test_handlers", "test_store_routes"):
            self.assertIsInstance(payload[key], int, f"{key} is not an int")
            self.assertGreaterEqual(payload[key], 0, f"{key} went negative")
        # The CLOSED counts: one planted scenario, its steps, and the allowlist sizes the guard owns.
        self.assertEqual(payload["scenarios"], 1)
        self.assertEqual(payload["test_store_allowlist"], len(EXPECTED_TEST_STORE_ROUTES))
        self.assertEqual(payload["test_op_allowlist"], len(EXPECTED_TEST_OPS))

    def test_two_runs_of_the_same_tree_are_byte_identical(self) -> None:
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        self.assertEqual(self.invoke(), self.invoke(), "the tool is not deterministic")


class TheClosedRegistries(TreeCase):
    def test_the_fixture_allowlist_is_exactly_the_reviewed_set(self) -> None:
        """Pinned exactly, because the allowlist IS a registry a human changes by review and rule 4 is
        meaningless without it. The RETIRED entry is here on purpose and its presence is the point."""
        self.assertEqual(set(guard.TEST_OP_ALLOWLIST), EXPECTED_TEST_OPS)
        self.assertIn("RETIRED", guard.TEST_OP_ALLOWLIST["test.expedition.due"])

    def test_every_allowlist_entry_carries_a_written_reason(self) -> None:
        """A reason nobody can re-derive is not a review record, it is a list."""
        for name, reason in guard.TEST_OP_ALLOWLIST.items():
            self.assertGreater(len(reason.strip()), 40, f"{name} has no real reason")
        for route, reason in guard.TEST_STORE_ALLOWLIST.items():
            self.assertGreater(len(reason.strip()), 40, f"{route} has no real reason")

    def test_the_store_allowlist_is_exactly_the_reviewed_set(self) -> None:
        self.assertEqual(set(guard.TEST_STORE_ALLOWLIST), EXPECTED_TEST_STORE_ROUTES)

    def test_a_NEW_store_route_is_a_violation(self) -> None:
        self.server(SERVER_OK.replace(
            'test.MapPost("/reset", (RpgStore store) =>',
            'test.MapGet("/reset", (HttpContext ctx) =>').replace(
            '        test.MapPost("/mint-creature", (RpgStore store) => { return Results.Ok(); });',
            '        test.MapPost("/mint-creature", (RpgStore store) => { return Results.Ok(); });\n'
            '        test.MapPost("/brand-new", (RpgStore store) => { return Results.Ok(); });'))
        self.scenario_file(CLEAN)
        self.assertIn("is not on the allowlist", " ".join(self.violations()))

    def test_a_STALE_allowlist_entry_is_a_violation(self) -> None:
        """The OTHER direction. A single-direction check would miss it entirely, and a stale allowlist
        is how the next real handler slips in unread."""
        self.server(SERVER_OK.replace("var test = app.MapGroup(\"/api/test\");\n", "")
                    .replace('        var sim = app.MapGroup("/api/sim");\n', "")
                    .replace('sim.MapPost("/tick", (HttpContext ctx) => { return Results.Ok(); });\n', ""))
        self.scenario_file(CLEAN)
        violations = self.violations()
        stale = [v for v in violations if "stale allowlist entry" in v]
        self.assertEqual(len(stale), len(EXPECTED_TEST_STORE_ROUTES),
                         f"every allowlist entry should be stale, got {len(stale)}")

    def test_a_sim_handler_taking_the_store_is_a_violation(self) -> None:
        self.server(SERVER_OK.replace(
            'sim.MapPost("/tick", (HttpContext ctx) =>',
            'sim.MapPost("/tick", (RpgStore store) =>'))
        self.scenario_file(CLEAN)
        self.assertIn(SIM_TAKES_STORE, " ".join(self.violations()))

    def test_RpgStoreFactory_is_NOT_the_store(self) -> None:
        """The word boundary is the rule, and this is the only test that can see it. A handler that
        takes a `RpgStoreFactory` and no store must NOT be reported as a store route -- otherwise every
        real handler would be, and the allowlist would be useless rather than merely out of date."""
        self.server(SERVER_OK.replace(
            'test.MapPost("/reset", (RpgStore store) => { return Results.Ok(); });',
            'test.MapPost("/reset", (RpgStoreFactory factory) => { return Results.Ok(); });'))
        self.scenario_file(CLEAN)
        violations = self.violations()
        self.assertIn("stale allowlist entry '/api/test/reset'",
                      " ".join(violations),
                      "a RpgStoreFactory parameter was counted as the store, so the word boundary is gone")

    def test_RpgStore_in_a_STRING_literal_still_sets_the_flag_AND_THAT_IS_PINNED(self) -> None:
        """The documented false-positive shape. The handler takes no store; its span MENTIONS the type
        in a log string, and the scan runs on the literal-PRESERVING view. The blanking view would miss
        this AND would miss the type annotation that IS the parameter, so neither view is a clean
        answer. Pinned so a future tightening is a decision rather than an accident."""
        self.server(SERVER_OK.replace(
            'test.MapPost("/reset", (RpgStore store) => { return Results.Ok(); });',
            'test.MapPost("/reset", (HttpContext ctx) => { Log("RpgStore reset"); return Results.Ok(); });'))
        self.scenario_file(CLEAN)
        stale = [v for v in self.violations() if "stale allowlist entry" in v]
        self.assertTrue(stale, "the string mention did not set the store flag, so the policy changed")


class HalfARules(TreeCase):
    def assert_clean(self) -> None:
        self.scenario_file(CLEAN)
        self.assertEqual(self.violations(), [])

    def test_the_anti_pattern_is_refused_BY_NAME(self) -> None:
        self.scenario_file(scenario([step("read.s", route="GET /api/test/snapshot")]))
        self.assertIn("named anti-pattern", " ".join(self.violations()))

    def test_a_hub_message_is_an_FE_facing_read(self) -> None:
        self.scenario_file(scenario([step("read.h", route="/hub/rpg"),
                                     step("expect.h", reading="read.h")]))
        self.assert_clean()

    def test_a_POST_under_api_is_not_an_FE_facing_read(self) -> None:
        self.scenario_file(scenario([step("read.c", route="POST /api/players")]))
        self.assertIn("is not an FE-facing read", " ".join(self.violations()))

    def test_rule_3_is_about_ORDER_and_reordering_flips_the_verdict(self) -> None:
        """The pair that pins the rule. The readings table is built as the steps are walked, so an
        assertion before its reading is a violation and the SAME two steps in the other order are not.
        A differential that only read the text could not have seen this."""
        self.scenario_file(scenario([READ, step("expect.p", reading="read.players")]))
        self.assert_clean()
        self.scenario_file(scenario([step("expect.p", reading="read.players"), READ]))
        self.assertIn("was never declared before this step", " ".join(self.violations()))

    def test_a_test_op_must_be_named_in_the_scenarios_own_notes(self) -> None:
        body = scenario([READ, step("test.seed.souls", route="POST /api/test/seed-souls-demo")])
        self.scenario_file(body)
        self.assertIn("not named in the scenario's own notes", " ".join(self.violations()))
        # The SAME file overwritten, not a second file: two scenarios in one tree would make the first
        # one's finding answer the second one's assertion.
        self.scenario_file(scenario([READ, step("test.seed.souls", route="POST /api/test/seed-souls-demo")],
                                    notes=["uses test.seed.souls for fixtures"]))
        self.assertEqual([v for v in self.violations() if "notes" in v], [],
                         "naming the op in notes must satisfy the rule")

    def test_naming_the_ROUTE_also_satisfies_the_notes_rule(self) -> None:
        self.scenario_file(scenario([READ, step("test.seed.souls", route="POST /api/test/seed-souls-demo")],
                                    notes=["drives POST /api/test/seed-souls-demo directly"]))
        self.assertEqual([v for v in self.violations() if "notes" in v], [])

    def test_a_clock_is_required_and_its_vocabulary_FOLDS(self) -> None:
        self.scenario_file(json.dumps({"id": "p", "steps": [READ]}, indent=2))
        self.assertIn("clock is required", " ".join(self.violations()))
        self.scenario_file(scenario([READ], clock={"mode": "Ambient", "note": "n"}))
        self.assertEqual([v for v in self.violations() if "clock" in v], [],
                         "the mode vocabulary folds case, so Ambient is not a spelling error")

    def test_golden_verdicts_are_counted_not_scanned(self) -> None:
        golden = self.root / "tests" / "fixtures" / "rpg-scenarios" / "golden"
        golden.mkdir(parents=True)
        (golden / "verdict.json").write_text('{"id": "stored", "result": "ok"}', encoding="utf-8")
        self.scenario_file(CLEAN)
        payload = self.result()
        self.assertEqual(payload["golden_skipped"], 1, "the golden verdict was not counted")
        self.assertEqual(payload["scenarios"], 1, "the golden verdict was scanned as a scenario")

    def test_build_output_is_not_scanned(self) -> None:
        self.server(SERVER_OK.replace(
            'sim.MapPost("/tick", (HttpContext ctx) =>',
            'sim.MapPost("/tick", (RpgStore store) =>'), "obj/Debug/Gen.cs")
        self.scenario_file(CLEAN)
        self.assertEqual(self.violations(), [],
                         "a violation inside obj/ was reported; obj/ is not source")


class TheTwoDeadBranches(TreeCase):
    """Two branches that cannot fire. Both are pinned as DEAD so a later fix is a decision the tests ask
    about rather than a silent behaviour change - deleting them would make a fix look like a
    regression, and a 'tidy-up' that removed a branch nobody proved reachable is how a guard loses a
    rule nobody knew it had."""

    def test_the_unbalanced_handler_span_branch_cannot_fire(self) -> None:
        """The registration regex ends at the COMMA, so the span anchor is the comma and the next
        balanced pair closes at depth 0. The branch is unreachable, and the span it returns happens to
        contain the parameter list - which is why TakesStore works at all."""
        self.server("""using System;
public static class E
{
    public static void MapAll(WebApplication app)
    {
        var test = app.MapGroup("/api/test");
        test.MapPost("/reset", (RpgStore store) => { return Results.Ok();
    }
}
""")
        self.scenario_file(CLEAN)
        self.assertEqual([v for v in self.violations() if "guard defect" in v], [],
                         "the unbalanced-span branch fired, so the span anchor was changed; that is a "
                         "DECISION and the test needs to say so")

    def test_the_route_surface_branch_cannot_fire(self) -> None:
        """It is an `elif` behind `route == the op's own method+route`, and a route built from the op's
        own method and path cannot have a different surface. Planted here so the guard is not quietly
        relying on it."""
        self.scenario_file(scenario([READ, step("sim.tick", route="POST /api/players")]))
        found = " ".join(self.violations())
        self.assertIn("is not the route 'sim.tick' calls", found)
        self.assertNotIn("surface is not the op's surface", found)


class PathReconciliation(TreeCase):
    """The 8.3 short-name hazard, which is the defect this port fixes. The original built each file's
    path as `$file.FullName.Substring($Root.Length)` behind a `StartsWith($Root, OrdinalIgnoreCase)`
    test; when the caller's root and the enumerator disagree about the spelling the slice removes the
    wrong number of characters and the reported path is chopped. `relative_to` alone converts that
    silent corruption into a loud refusal - still wrong, because a guard that refuses an ordinary temp
    tree cannot be tested against one - so the two spellings are reconciled first."""

    def test_a_root_that_agrees_with_the_enumerator_yields_a_RELATIVE_path(self) -> None:
        """No skip and no short-name lookup, and that is deliberate.

        `tempfile` already hands back the 8.3 SHORT root (`C:\\Users\\NENEESC~1\\...`) while
        `Path.resolve()` inside the tool expands it to the long form, so the disagreement is present on
        every run and the reconciliation is what makes the reported path relative. An earlier version of
        this test called `GetShortPathNameW` on a path that was ALREADY short, got the same string back,
        and skipped -- so a mutation that removed the reconciliation entirely SURVIVED. A test that skips
        is a test that pins nothing, which is why this one asserts the outcome unconditionally.
        """
        self.assertNotEqual(str(self.root), str(self.root.resolve()),
                            "this volume hands out no short names, so the case is vacuous here")
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root), "--json"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        payload = json.loads(proc.stdout[proc.stdout.index("{"):])
        self.assertNotEqual(payload["verdict"], "REFUSED",
                            "a short-form root was refused, so the two spellings are not reconciled")
        self.assertTrue(payload["violations"][0].startswith("tests/fixtures/rpg-scenarios/probe.json"),
                        f"the reported path is not repository-relative: {payload['violations'][0]!r}")

    def test_relative_to_root_reconciles_a_root_spelled_with_a_redundant_segment(self) -> None:
        """The fallback, pinned on a disagreement that needs no 8.3 name to exist, so it holds on every
        platform. `Path` does not collapse `..`, so `relative_to` refuses and the same-file walk is what
        answers."""
        (self.root / "sub").mkdir(parents=True, exist_ok=True)
        awkward = self.root / "sub" / ".."
        probe = self.root / "tests" / "fixtures" / "rpg-scenarios" / "probe.json"
        probe.parent.mkdir(parents=True, exist_ok=True)
        probe.write_text("{}", encoding="utf-8")
        self.assertEqual(guard.relative_to_root(probe, awkward),
                         "tests/fixtures/rpg-scenarios/probe.json")

    def test_a_scenario_dir_OUTSIDE_the_root_is_still_scanned(self) -> None:
        """`--scenario-dir` may legitimately point outside `--root`.

        The C# bite-proof test plants its violation in a temp directory and passes the REAL repository
        as the root, so the planted file is genuinely not under it. The original permitted that -- its
        `StartsWith` test simply failed and it reported an absolute path. A port that REFUSES it makes
        the guard unusable for exactly the planted-violation work it exists to support, and that is how
        the first version of this tool failed its own C# suite. The path is reported relative to the
        scenario directory instead.
        """
        elsewhere = self._tmp.name + "-elsewhere"
        outside = Path(elsewhere) / "corpus"
        outside.mkdir(parents=True)
        self.addCleanup(lambda: shutil.rmtree(elsewhere, ignore_errors=True))
        (outside / "planted.json").write_text(
            scenario([step("read.snap", route="GET /api/test/snapshot")]), encoding="utf-8")
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(self.root),
                               "--scenario-dir", str(outside), "--json"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT)
        payload = json.loads(proc.stdout[proc.stdout.index("{"):])
        self.assertEqual(payload["verdict"], "FAIL",
                         f"a scenario outside the root was not scanned: {payload}")
        self.assertTrue(payload["violations"][0].startswith("planted.json"),
                        f"the path should be relative to the scenario dir: {payload['violations'][0]!r}")

    def test_relative_to_root_refuses_a_file_under_neither_base(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            guard.relative_to_root(Path(r"C:\definitely\not\here\probe.json"), self.root,
                                   Path(r"C:\nor\here"))
        self.assertEqual(caught.exception.reason, "PATH-OUTSIDE-ROOT")


class StreamDiscipline(TreeCase):
    def test_findings_go_to_stderr_and_the_reading_to_stdout(self) -> None:
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        code, out, err = self.invoke()
        self.assertEqual(code, 1)
        self.assertIn("SIM FABRICATION GUARD FAILED", err)
        self.assertIn("anti-pattern", err)
        self.assertNotIn("anti-pattern", out, "a finding leaked onto stdout")
        self.assertRegex(out, r"sim fabrication guard: scenarios=")
        self.assertRegex(out, r"golden verdicts skipped=")

    def test_a_clean_run_puts_everything_on_stdout(self) -> None:
        self.scenario_file(CLEAN)
        code, out, err = self.invoke()
        self.assertEqual(code, 0)
        self.assertEqual(err, "", "a clean run wrote to stderr")
        self.assertIn("SIM FABRICATION GUARD OK", out)

    def test_the_reading_is_printed_before_the_findings(self) -> None:
        """On a merged stream the reading comes first, as it did in the original. A caller tailing both
        streams sees the summary above the detail rather than the reverse."""
        self.scenario_file(scenario([step("read.snap", route="GET /api/test/snapshot")]))
        code, out, err = self.invoke()
        merged = out + err
        self.assertLess(merged.index("sim fabrication guard:"), merged.index("SIM FABRICATION GUARD FAILED"),
                        "the reading line must precede the failure block")


class HelperUnits(TreeCase):
    """The pure helpers, exercised directly. A guard's arithmetic is a contract, and these are the
    pieces a reader cannot check by reading the call site."""

    def test_route_surface_and_method(self) -> None:
        self.assertEqual(guard.route_surface("GET /api/players"), "api")
        self.assertEqual(guard.route_surface("POST /api/sim/tick"), "sim")
        self.assertEqual(guard.route_surface("POST /api/test/x"), "test")
        self.assertEqual(guard.route_surface("/hub/rpg"), "hub")
        self.assertIsNone(guard.route_surface("/nowhere"))
        self.assertIsNone(guard.route_surface("   "))
        self.assertEqual(guard.route_method("get /api/players"), "GET")
        self.assertIsNone(guard.route_method("/hub/rpg"), "a hub message names no method")

    def test_is_fe_facing_read_is_the_mirror_of_the_vocabulary_rule(self) -> None:
        self.assertTrue(guard.is_fe_facing_read("GET /api/players"))
        self.assertTrue(guard.is_fe_facing_read("/hub/rpg"))
        self.assertFalse(guard.is_fe_facing_read("POST /api/players"))
        self.assertFalse(guard.is_fe_facing_read("GET /api/test/x"))
        self.assertFalse(guard.is_fe_facing_read("GET /api/sim/x"))

    def test_find_matching_close_returns_MINUS_ONE_and_never_raises(self) -> None:
        """-1 rather than an exception, because the callers turn it into a `guard defect` violation
        naming the route: a malformed body is a finding about the tree, not a crash of the guard."""
        self.assertEqual(guard.find_matching_close("(a(b)c)", 0, "(", ")"), 6)
        self.assertEqual(guard.find_matching_close("(a(b)c)", 2, "(", ")"), 4)
        # Started on a character that is neither opener nor closer, the very first `)` drives the depth
        # to -1 and it never returns 0 -- which is exactly why the span anchored on a comma always
        # resolves and the unbalanced branch is dead.
        self.assertEqual(guard.find_matching_close("(a(b)c)", 3, "(", ")"), -1)
        self.assertEqual(guard.find_matching_close("(a", 0, "(", ")"), -1)

    def test_as_list_normalises_a_scalar_so_one_element_is_not_a_scalar(self) -> None:
        self.assertEqual(guard.as_list(None), [])
        self.assertEqual(guard.as_list("one"), ["one"])
        self.assertEqual(guard.as_list(["a", "b"]), ["a", "b"])
        self.assertEqual(guard.as_text(None), "")


if __name__ == "__main__":
    unittest.main()
