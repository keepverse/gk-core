"""Contract tests for `gk-core/scripts/guard-debug-scope.py`.

Asserts the CONTRACT: the CLI surface, the exit-code vocabulary, the named refusals, the `--json`
envelope's key set, and both classification rules. It does not assert a message body or a route count.

Five things this suite exists to stop a later reader from "fixing":

  * **The classification sub-patterns FOLD case; the route patterns do NOT.** The original used
    PowerShell `-match` for one set and `[regex]::Matches` for the other. The first version of this
    port compiled four of the five case-*sensitively* because reading `-match` looks like ordinary
    regex — and the fold is invisible until you test a lowercase `send(`. Only the differential caught
    it. Adjacent tests pin both halves so neither can drift into the other.
  * **The banner pass reads the RAW text, so a `//` banner inside a block comment counts.** The
    pattern still requires `//`; what makes this surprising is the *stripped* text would hide it.
  * **`ManualReview` routes are never checked against a banner** — the rule deliberately does not
    auto-classify them either way, so no banner text on them can be "wrong".
  * **The nearest banner wins**, not the first, and a banner *after* a route does not apply to it.
  * **An empty target is REFUSED, not clean.** The original crashed on a zero-byte file
    (`Get-Content -Raw` returns `$null` there), and reading it as "no routes, nothing to check" would
    make a truncated `DebugEndpoints.cs` pass vacuously.

Differential evidence: 26 fixtures, 20 identical in exit code and every emitted line, plus 6 declared
divergences — 5 named refusals the original buried in a stack trace, 1 zero-byte target it crashed on,
and 1 unbraced helper body where the original refused only because PowerShell's negative string
indexing happened to wrap. That comparison lives outside this file because the PowerShell form no
longer exists.

THE C# SUITE ONLY EVER PROVED THE GREEN PATH
---------------------------------------------
`DebugScopeGuardTests` has six facts and every one asserts `exit == 0`; not one produces a banner
mismatch, and none reaches a self-verification refusal. `TheFindingBranch` and `TheRefusals` below are
that missing half, and they are the half that makes this a guard.
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
SCRIPT = REPO / "scripts" / "guard-debug-scope.py"

_spec = importlib.util.spec_from_file_location("guard_debug_scope", SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules["guard_debug_scope"] = guard
_spec.loader.exec_module(guard)

# The two self-verified helpers, from DebugScopeGuardTests.cs. Both must genuinely relay or the guard
# refuses — that is the point of the self-verification.
RELAY_HELPERS = """
    static void MapPost(RouteGroupBuilder g, string path, string cmdName)
    {
        g.MapPost(path, async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            await Send(hub, inbox, cmdName, body);
            return Results.Ok(new { ok = true });
        });
    }

    public static async Task<IResult> AcceptDebugSpawnExtra(
        JsonElement body,
        RpgStore store,
        IHubContext<RpgHub> hub,
        InjectorCommandInbox inbox,
        string reasonDefault)
    {
        await Send(hub, inbox, "pvz.spawn.extra", body);
        return Results.Ok(new { ok = true });
    }
"""


def fixture(body: str, *, helpers: bool = True) -> str:
    """Always wrapped. An earlier differential used the bare body when given, so no fixture carried
    the helper definitions and five cases passed while exercising a branch their names deny."""
    return f"""
public static class Fixture
{{
    public static void Map(RouteGroupBuilder g)
    {{
{body}
    }}
{RELAY_HELPERS if helpers else ""}
}}
"""


def check_body(body: str, *, helpers: bool = True) -> dict:
    with tempfile.TemporaryDirectory(prefix="gds-"):
        pass
    tmp = tempfile.mkdtemp(prefix="gds-")
    path = Path(tmp) / "DebugEndpoints.cs"
    path.write_text(fixture(body, helpers=helpers), encoding="utf-8")
    try:
        return guard.check(path)
    finally:
        Path(tmp).cleanup if False else None
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)


def classify(body: str, **kwargs) -> dict[str, str]:
    """path -> classification, for a body that must not raise."""
    return {r["path"]: r["classification"] for r in check_body(body, **kwargs)["routes"]}


def run(*args: str) -> dict:
    proc = subprocess.run([sys.executable, str(SCRIPT), *args], cwd=REPO,
                          capture_output=True, text=True, timeout=1800)
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


class CliSurface(unittest.TestCase):
    def test_help_names_the_replacement(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--help"],
                              capture_output=True, text=True, timeout=300)
        self.assertEqual(0, proc.returncode)
        self.assertIn("guard-debug-scope.py", proc.stdout)

    def test_the_file_path_flag_is_accepted(self) -> None:
        result = run("--file-path", str(REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs"))
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])


class ExitCodeVocabulary(unittest.TestCase):
    def test_the_vocabulary_is_closed(self) -> None:
        self.assertEqual({guard.EXIT_OK, guard.EXIT_FAILED}, {0, 1})

    def test_the_real_file_is_clean(self) -> None:
        got = guard.check(REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs")
        self.assertEqual("OK", got["verdict"], got["findings"][:3])

    def test_findings_go_to_stderr_and_the_report_to_stdout(self) -> None:
        # The route table is REPORT content, not a finding: eight caller assertions read it on stdout,
        # so it stays there. The findings move to stderr. A guard proven only on success is half-proven,
        # so the failing stream is asserted, not assumed.
        got = check_body('        // RPG Server Debug\n'
                         '        g.MapPost("/x", async (JsonElement? b, IHubContext<RpgHub> hub, '
                         'InjectorCommandInbox inbox) => { await Send(hub, inbox, "d", b); '
                         'return Results.Ok(); });')
        self.assertEqual("FAIL", got["verdict"])
        with tempfile.TemporaryDirectory(prefix="gds-cli-") as tmp:
            path = Path(tmp) / "D.cs"
            path.write_text(fixture('        // RPG Server Debug\n'
                                    '        g.MapPost("/x", async (JsonElement? b, IHubContext<RpgHub> hub, '
                                    'InjectorCommandInbox inbox) => { await Send(hub, inbox, "d", b); '
                                    'return Results.Ok(); });'),
                            encoding="utf-8")
            result = run("--file-path", str(path))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertIn("DEBUG SCOPE GUARD FAILED", result["stderr"])
        self.assertIn("banner says", result["stderr"])
        self.assertNotIn("banner/classification mismatch", result["stdout"])


class ShapeATheSharedHelper(unittest.TestCase):
    def test_a_bare_helper_call_is_Game_Injector_Debug_with_no_body_scan(self) -> None:
        got = classify('        MapPost(g, "/bare", "debug.bare");')
        self.assertEqual("GameInjectorDebug", got["/bare"])
        self.assertIn("by construction", check_body('        MapPost(g, "/bare", "debug.bare");')
                      ["routes"][0]["reason"])

    def test_a_DOTTED_call_is_never_shape_A(self) -> None:
        # The `(?<!\\.)` lookbehind exists so `g.MapPost(` never double-counts. Without it every
        # inline route would be reported twice, once as A and once as B.
        got = check_body('        g.MapPost("/dotted", (JsonElement? b) => Results.Ok());')
        self.assertEqual(["B"], [r["shape"] for r in got["routes"]])

    def test_shape_A_sorts_into_source_order_not_append_order(self) -> None:
        # Shape A is collected first, then Shape B, then merged by index. If the merge were dropped the
        # report would read all-A-then-all-B instead of file order.
        got = check_body('        g.MapGet("/first", (RpgStore s) => { s.Merge(); return Results.Ok(); });\n'
                         '        MapPost(g, "/second", "debug.second");')
        self.assertEqual(["/first", "/second"], [r["path"] for r in got["routes"]])


class ShapeBTheInlineRoute(unittest.TestCase):
    def test_a_relay_is_Game_Injector_Debug(self) -> None:
        self.assertEqual("GameInjectorDebug", classify(
            '        g.MapPost("/r", async (JsonElement? b, IHubContext<RpgHub> hub, '
            'InjectorCommandInbox inbox) => { await Send(hub, inbox, "d", b); return Results.Ok(); });'
        )["/r"])

    def test_a_persisted_call_is_Rpg_Server_Debug(self) -> None:
        self.assertEqual("RpgServerDebug", classify(
            '        g.MapPost("/p", (JsonElement? b, RpgStore s) => { s.MergeCheatField("X", true, null); '
            'return Results.Ok(); });')["/p"])

    def test_ANY_relay_wins_even_alongside_a_persisted_call(self) -> None:
        # Named as the actual regression case by the C# suite: a body with BOTH must be
        # Game-Injector-Debug-shaped, never RPG Server Debug and never a conflict.
        got = classify('        g.MapPost("/m", async (JsonElement? b, RpgStore s, IHubContext<RpgHub> hub, '
                       'InjectorCommandInbox inbox) => { s.MergeCheatField("X", true, null); '
                       'await Send(hub, inbox, "d", b); return Results.Ok(); });')
        self.assertEqual("GameInjectorDebug", got["/m"])

    def test_a_delegate_to_AcceptDebugSpawnExtra_is_Game_Injector_Debug(self) -> None:
        self.assertEqual("GameInjectorDebug", classify(
            '        g.MapPost("/s", async (JsonElement? b, RpgStore s, IHubContext<RpgHub> hub, '
            'InjectorCommandInbox inbox) => { await AcceptDebugSpawnExtra(b, s, hub, inbox, "x"); '
            'return Results.Ok(); });')["/s"])

    def test_neither_relay_nor_persisted_is_ManualReview(self) -> None:
        self.assertEqual("ManualReview", classify(
            '        g.MapGet("/e", (JsonElement? b) => Results.Ok(new { n = 1 }));')["/e"])

    def test_MapGet_and_MapPost_report_different_verbs(self) -> None:
        got = check_body('        g.MapGet("/g", (RpgStore s) => { s.Merge(); return Results.Ok(); });\n'
                         '        g.MapPost("/p", (RpgStore s) => { s.Merge(); return Results.Ok(); });')
        self.assertEqual({"/g": "GET", "/p": "POST"},
                         {r["path"]: r["method"] for r in got["routes"]})

    def test_an_expression_bodied_lambda_with_no_braces_is_isolated_correctly(self) -> None:
        # Paren depth, not brace depth: embedded braces inside a block-bodied lambda must not
        # terminate the span early, and an expression-bodied one has no braces at all.
        for body in ('        g.MapGet("/blk", (RpgStore s) => { s.Merge(); return Results.Ok(); });',
                     '        g.MapGet("/expr", (RpgStore s) => Results.Ok(new { n = 1 }));'):
            with self.subTest(body=body):
                self.assertEqual("RpgServerDebug", classify(body)[
                    "/blk" if "/blk" in body else "/expr"])

    def test_a_relay_inside_a_COMMENT_is_not_a_relay(self) -> None:
        # This is what the comment stripper is FOR: a keyword in prose must not classify a route.
        self.assertEqual("ManualReview", classify(
            '        g.MapPost("/c", (JsonElement? b) => {\n'
            '            // await Send(hub, inbox, "d", b);\n'
            '            return Results.Ok(); });')["/c"])


class TheCaseFoldPair(unittest.TestCase):
    """The two conventions, asserted side by side so neither can drift into the other."""

    def test_the_classification_patterns_FOLD_case(self) -> None:
        # PowerShell `-match` folds. The first port compiled four of these case-sensitively and only a
        # differential caught it, because reading `-match` looks like ordinary regex.
        self.assertEqual("GameInjectorDebug", classify(
            '        g.MapPost("/fold", (JsonElement? b) => { send(hub, inbox, "d", b); '
            'return Results.Ok(); });')["/fold"])
        self.assertEqual("RpgServerDebug", classify(
            '        g.MapPost("/fold2", (JsonElement? b) => { rpgstore.Merge(); '
            'return Results.Ok(); });')["/fold2"])

    def test_every_classification_pattern_carries_the_fold(self) -> None:
        # Asserted on the compiled patterns rather than through one fixture, so a pattern that loses
        # `re.IGNORECASE` is caught even where no fixture reaches it.
        for name in ("RELAY", "DELEGATE_RELAY", "PERSISTED", "PERSISTED_UA", "PERSISTED_SERVICE"):
            with self.subTest(pattern=name):
                self.assertTrue(getattr(guard, name).flags & guard.re.IGNORECASE,
                                f"{name} lost the case fold PowerShell -match provides")

    def test_the_route_patterns_do_NOT_fold(self) -> None:
        # `[regex]::Matches` does not fold, so a lower-case `g.mappost(` is not a route. Transcribed:
        # making these fold would ADD routes the original never reported.
        for name in ("SHAPE_A", "SHAPE_B", "BANNER"):
            with self.subTest(pattern=name):
                self.assertFalse(getattr(guard, name).flags & guard.re.IGNORECASE,
                                 f"{name} gained a fold the original did not have")
        self.assertEqual({}, classify(
            '        g.mappost("/lower", (RpgStore s) => { s.Merge(); return Results.Ok(); });'))


class TheFindingBranch(unittest.TestCase):
    """No C# coverage exists for any of this."""

    def test_a_banner_disagreeing_with_the_classification_fails(self) -> None:
        for banner, body, path in (
            ("// RPG Server Debug",
             '        g.MapPost("/x", async (JsonElement? b, IHubContext<RpgHub> hub, '
             'InjectorCommandInbox inbox) => { await Send(hub, inbox, "d", b); return Results.Ok(); });',
             "/x"),
            ("// Game Injector Debug",
             '        g.MapPost("/y", (JsonElement? b, RpgStore s) => { s.Merge(); return Results.Ok(); });',
             "/y"),
        ):
            with self.subTest(banner=banner):
                got = check_body(f"        {banner}\n{body}")
                self.assertEqual("FAIL", got["verdict"])
                self.assertTrue(any(path in f for f in got["findings"]))

    def test_the_NEAREST_banner_wins_not_the_first(self) -> None:
        # Three routes under two banners. If the FIRST banner were used for all of them, `/c` would
        # agree and pass -- so `/c` failing is the proof that the nearest one was used. `/a` failing
        # proves a banner is applied at all, and `/b` passing proves a MATCHING banner is not a
        # finding. An earlier version of this test asserted that a correctly-classified route
        # disagreed, i.e. it asserted a bug.
        relay = ('async (JsonElement? b, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) => '
                 '{ await Send(hub, inbox, "d", b); return Results.Ok(); }')
        persisted = '(JsonElement? b, RpgStore s) => { s.Merge(); return Results.Ok(); }'
        got = check_body(
            f'        // Game Injector Debug\n'
            f'        g.MapPost("/a", {persisted});\n'
            f'        g.MapPost("/b", {relay});\n'
            f'        // RPG Server Debug\n'
            f'        g.MapGet("/c", {relay});')
        self.assertEqual("FAIL", got["verdict"])
        self.assertEqual({"GameInjectorDebug": 2, "RpgServerDebug": 1},
                         {c: sum(1 for r in got["routes"] if r["classification"] == c)
                          for c in ("GameInjectorDebug", "RpgServerDebug")})
        blamed = {f.split()[1] for f in got["findings"]}
        # `/a` is judged by the Game Injector banner above it and disagrees.
        self.assertIn("/a", blamed)
        # `/b` agrees with that same banner, so it is not a finding.
        self.assertNotIn("/b", blamed)
        # `/c` is judged by the SECOND banner and disagrees -- which is the actual claim.
        self.assertIn("/c", blamed)

    def test_a_banner_AFTER_a_route_does_not_apply_to_it(self) -> None:
        got = classify('        g.MapPost("/b", (JsonElement? b, RpgStore s) => { s.Merge(); '
                       'return Results.Ok(); });\n        // Game Injector Debug')
        self.assertEqual("RpgServerDebug", got["/b"])
        self.assertEqual("OK", check_body(
            '        g.MapPost("/b", (JsonElement? b, RpgStore s) => { s.Merge(); return Results.Ok(); });\n'
            '        // Game Injector Debug')["verdict"])

    def test_a_ManualReview_route_is_NEVER_checked_against_a_banner(self) -> None:
        # The rule deliberately does not auto-classify them either way, so no banner text on them can
        # be "wrong". A guard that flagged these would demand a classification nobody decided on.
        got = check_body('        // RPG Server Debug\n'
                         '        g.MapPost("/m", (JsonElement? b) => Results.Ok());')
        self.assertEqual("ManualReview", {r["path"]: r["classification"]
                                          for r in got["routes"]}["/m"])
        self.assertEqual("OK", got["verdict"])
        self.assertEqual([], got["findings"])

    def test_a_slash_slash_banner_inside_a_block_comment_STILL_counts(self) -> None:
        # The banner pass reads the RAW text, so a `//` banner buried in `/* */` is found. The pattern
        # still requires `//` -- what is surprising is that the *stripped* text would hide it.
        got = check_body('        /*\n        // Game Injector Debug\n        */\n'
                         '        g.MapPost("/blk", (JsonElement? b, RpgStore s) => { s.Merge(); '
                         'return Results.Ok(); });')
        self.assertEqual("FAIL", got["verdict"])
        self.assertTrue(any("GameInjectorDebug" in f for f in got["findings"]))

    def test_a_finding_cites_the_FILES_OWN_line(self) -> None:
        body = ('        // RPG Server Debug\n'
                '        g.MapPost("/x", async (JsonElement? b, IHubContext<RpgHub> hub, '
                'InjectorCommandInbox inbox) => { await Send(hub, inbox, "d", b); '
                'return Results.Ok(); });')
        text = fixture(body)
        expected = text[:text.index('g.MapPost("/x"')].count("\n") + 1
        got = check_body(body)
        self.assertIn(f"(line {expected})", got["findings"][0])
        # And the cited line really is the route: strip the fixture back to that one line.
        self.assertIn("g.MapPost", text.splitlines()[expected - 1])


class TheRefusals(unittest.TestCase):
    """No coverage in either language before this port."""

    def test_a_missing_target_is_refused_by_name(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            guard.check(REPO / "no" / "such" / "DebugEndpoints.cs")
        self.assertEqual("TARGET-FILE-MISSING", caught.exception.reason)

    def test_an_EMPTY_target_is_refused_not_clean(self) -> None:
        # The original CRASHED here: `Get-Content -Raw` returns $null on a zero-byte file, so
        # `[regex]::Matches($null, ...)` threw "Value cannot be null. Parameter name: input". Reading it
        # as "no routes, nothing to check" would make a truncated DebugEndpoints.cs pass vacuously.
        with tempfile.TemporaryDirectory(prefix="gds-empty-") as tmp:
            for name in ("", "   \n\n  \t "):
                with self.subTest(content=name):
                    path = Path(tmp) / "D.cs"
                    path.write_text(name, encoding="utf-8")
                    with self.assertRaises(guard.Refusal) as caught:
                        guard.check(path)
                    self.assertEqual("TARGET-FILE-EMPTY", caught.exception.reason)

    def test_unbalanced_parens_are_refused_by_name(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            check_body('        g.MapPost("/u", (JsonElement? b) => { return Results.Ok();')
        self.assertEqual("ROUTE-PARENS-UNBALANCED", caught.exception.reason)

    def test_shape_A_with_NO_helper_definition_to_self_verify_is_refused(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            check_body('        MapPost(g, "/a", "debug.a");', helpers=False)
        self.assertEqual("MAPPOST-DEFINITION-MISSING", caught.exception.reason)

    def test_a_helper_that_is_PRESENT_but_no_longer_relays_is_refused(self) -> None:
        # The stale-premise shape Shape A exists to catch. Omitting the definition reaches a DIFFERENT
        # refusal, and confusing the two is how a stale-premise check gets mistaken for a missing file.
        with self.assertRaises(guard.Refusal) as caught:
            check_body('        static void MapPost(RouteGroupBuilder g, string path, string cmdName)\n'
                       '        { g.MapPost(path, (JsonElement? b) => Results.Ok()); }\n'
                       '        MapPost(g, "/a", "debug.a");', helpers=False)
        self.assertTrue(caught.exception.reason.endswith("NO-LONGER-RELAYS")
                        or "NO-LONGER-RELAYS" in caught.exception.reason)

    def test_a_helper_with_no_body_brace_is_refused(self) -> None:
        with self.assertRaises(guard.Refusal) as caught:
            check_body('        static void MapPost(RouteGroupBuilder g, string path, string cmdName)\n'
                       '        ;\n        MapPost(g, "/a", "debug.a");', helpers=False)
        self.assertTrue("BODY-UNBRACED" in caught.exception.reason)

    def test_a_refusal_is_never_reported_as_a_clean_verdict(self) -> None:
        # The invariant the whole class exists for: a guard that cannot say "I could not check" is
        # worse than one that fails.
        with tempfile.TemporaryDirectory(prefix="gds-ref-") as tmp:
            path = Path(tmp) / "D.cs"
            path.write_text("", encoding="utf-8")
            result = run("--file-path", str(path))
        self.assertEqual(guard.EXIT_FAILED, result["exit"])
        self.assertNotIn("GUARD OK", result["stdout"])


class TheStripperChoice(unittest.TestCase):
    def test_literals_are_survived_so_route_paths_still_match(self) -> None:
        # The literal-BLANKING variant would erase the route paths out of existence and classify
        # nothing, which is the whole guard.
        stripped = guard.strip_comments_preserving_layout('g.MapPost("/kept", x);')
        self.assertIn('"/kept"', stripped)

    def test_comments_are_blanked_at_the_same_length(self) -> None:
        source = "a; // gone\n/* b */ c; // gone\n"
        out = guard.strip_comments_preserving_layout(source)
        self.assertEqual(len(source), len(out))
        self.assertEqual(source.count("\n"), out.count("\n"))
        self.assertNotIn("gone", out)

    def test_an_embedded_comment_start_inside_a_literal_is_not_a_comment(self) -> None:
        # A URL in a string is the reason the stripper scans literals over rather than blanking them.
        stripped = guard.strip_comments_preserving_layout('var u = "http://x/y"; // real\n')
        self.assertIn('"http://x/y"', stripped)
        self.assertNotIn("real", stripped)


class JsonEnvelope(unittest.TestCase):
    KEYS = {"guard", "verdict", "target", "routes", "route_count", "banners_found", "findings"}

    REFUSAL_KEYS = KEYS | {"reason", "detail"}

    def test_the_key_set_is_the_same_on_both_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gds-json-") as tmp:
            ok = Path(tmp) / "ok.cs"
            ok.write_text(fixture('        g.MapPost("/a", (RpgStore s) => { s.Merge(); '
                                  'return Results.Ok(); });'), encoding="utf-8")
            bad = Path(tmp) / "bad.cs"
            bad.write_text(fixture('        // Game Injector Debug\n'
                                   '        g.MapPost("/b", (RpgStore s) => { s.Merge(); '
                                   'return Results.Ok(); });'), encoding="utf-8")
            refused = Path(tmp) / "refused.cs"
            refused.write_text("", encoding="utf-8")
            # A refusal names itself, so its envelope is a SUPERSET: the same keys plus a reason and a
            # detail. Asserting an identical key set would force a refusal to hide why it refused, which
            # is the opposite of a named refusal.
            for path, verdict, keys in ((ok, "OK", self.KEYS), (bad, "FAIL", self.KEYS),
                                        (refused, "FAILED", self.REFUSAL_KEYS)):
                with self.subTest(verdict=verdict):
                    proc = subprocess.run([sys.executable, str(SCRIPT), "--file-path", str(path),
                                           "--json"], capture_output=True, text=True, timeout=1800)
                    payload = json.loads(proc.stdout)
                    self.assertEqual(keys, set(payload))
                    self.assertEqual(verdict, payload["verdict"])

    def test_a_refusal_carries_its_reason_in_the_json(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gds-json-") as tmp:
            path = Path(tmp) / "e.cs"
            path.write_text("", encoding="utf-8")
            proc = subprocess.run([sys.executable, str(SCRIPT), "--file-path", str(path), "--json"],
                                  capture_output=True, text=True, timeout=1800)
            self.assertEqual("TARGET-FILE-EMPTY", json.loads(proc.stdout)["reason"])


class TheShippedState(unittest.TestCase):
    """The real file, asserted through the same code path the C# suite uses."""

    def test_it_is_clean_and_reports_a_report(self) -> None:
        target = REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs"
        result = run("--file-path", str(target))
        self.assertEqual(guard.EXIT_OK, result["exit"], result["stderr"])
        self.assertIn("DEBUG SCOPE GUARD OK", result["stdout"])

    def test_the_report_lines_keep_the_column_layout_callers_read(self) -> None:
        # Six DebugScopeGuardTests assertions read these exact strings on stdout, e.g.
        # "[RpgServerDebug   ] POST  /derived-audit-actor". The padding IS the contract.
        result = run("--file-path", str(REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs"))
        self.assertIn("[RpgServerDebug   ] POST  /derived-audit-actor", result["stdout"])
        self.assertIn("[GameInjectorDebug] POST  /lawn/quick-start", result["stdout"])

    def test_every_route_is_classified_into_the_closed_vocabulary(self) -> None:
        got = guard.check(REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs")
        for route in got["routes"]:
            with self.subTest(path=route["path"]):
                self.assertIn(route["classification"],
                              {"GameInjectorDebug", "RpgServerDebug", "ManualReview"})
                self.assertTrue(route["reason"], "a route without a reason is a route nobody can check")

    def test_no_route_is_reported_twice(self) -> None:
        got = guard.check(REPO / "src" / "FusionRpg.Server" / "DebugEndpoints.cs")
        spans = [(r["method"], r["path"], r["shape"]) for r in got["routes"]]
        self.assertEqual(len(spans), len(set(spans)))


if __name__ == "__main__":
    unittest.main()
