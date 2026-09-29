"""Contract tests for `gk-core/scripts/smoke_effect_scoped_atk.py`.

A LIVE PROVE, so the contract is about the five scenario assertions and the gate.

THE ASSERTIONS ARE THE POINT. Each scenario's comparison is a pure function with the original's
operators pinned: col1 > col3, pea > wallnut, |col1 - col3| <= 0.5, col1 > col3, and the
grant/withdraw triple (g1 > g3, w1 < g1, |w1 - w3| <= 0.5). A port that swapped `col` for `typeId`,
or `<` for `<=`, would still run and report a plausible note while proving nothing.

THE GATE IS THE OTHER POINT. The health gate requires injectorConnected && !simEnabled && source ==
"injector" -- the same gate as the sibling smoke, because a SIM board is not the Melon LIVE run.

The transport is substituted through the tool's PRIVATE seams (`_URLOPEN`, `_SLEEP`, `_MONOTONIC`)
and the shared library's `_URLOPEN`, because those are process-wide modules: a test that patches
any of them reaches every other test in this project.
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
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("SMOKE_EFFECT_SCOPED_ATK_SCRIPT",
                             REPO / "scripts" / "smoke_effect_scoped_atk.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_smoke_effect_scoped_atk.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("smoke_effect_scoped_atk", SCRIPT)
sea = importlib.util.module_from_spec(_spec)
sys.modules["smoke_effect_scoped_atk"] = sea
_spec.loader.exec_module(sea)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": sea._URLOPEN, "_SLEEP": sea._SLEEP, "_MONOTONIC": sea._MONOTONIC,
             "lib_URLOPEN": lib._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP", "_MONOTONIC"):
            if getattr(sea, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(sea, name)!r}")


class FastClock:
    def __init__(self, step: float = 1.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


def board(plants: list[dict], tag: str = "") -> dict:
    return {"tag": tag, "plants": plants}


def plant(col: int, attack: float, type_id: int = 0) -> dict:
    return {"col": col, "attack": attack, "typeId": type_id}


class ThePlantLookups(SeamGuard):
    def test_get_plant_at_finds_the_plant_in_the_column(self) -> None:
        b = board([plant(0, 5), plant(1, 10), plant(3, 7)])
        self.assertEqual(sea.get_plant_at(b, 1)["attack"], 10)
        self.assertEqual(sea.get_plant_at(b, 3)["attack"], 7)

    def test_get_plant_at_returns_None_for_an_empty_column(self) -> None:
        self.assertIsNone(sea.get_plant_at(board([plant(0, 5)]), 3))
        self.assertIsNone(sea.get_plant_at(None, 1))
        self.assertIsNone(sea.get_plant_at({}, 1))

    def test_get_plant_at_finds_the_plant_in_column_ZERO(self) -> None:
        """Column 0 is a real column. `plant.get('col') or -1` is `-1` in Python, which would make
        the leftmost plant unfindable -- a falsy-zero bug the explicit None check prevents."""
        self.assertEqual(sea.get_plant_at(board([plant(0, 5)]), 0)["attack"], 5)

    def test_get_plant_by_type_finds_the_plant_of_the_type(self) -> None:
        b = board([plant(0, 10, type_id=0), plant(2, 5, type_id=3)])
        self.assertEqual(sea.get_plant_by_type(b, 0)["attack"], 10)
        self.assertEqual(sea.get_plant_by_type(b, 3)["attack"], 5)

    def test_get_plant_by_type_returns_None_for_a_missing_type(self) -> None:
        self.assertIsNone(sea.get_plant_by_type(board([plant(0, 10, type_id=0)]), 3))
        self.assertIsNone(sea.get_plant_by_type(None, 0))

    def test_get_plant_by_type_finds_the_type_ZERO_pea(self) -> None:
        """typeId 0 is the pea. `plant.get('typeId') or -1` is `-1` in Python, which would make the
        pea unfindable -- the same falsy-zero bug."""
        self.assertEqual(sea.get_plant_by_type(board([plant(0, 10, type_id=0)]), 0)["attack"], 10)


class TheScenarioAssertions(SeamGuard):
    """The original's `switch` branches, preserved exactly -- including the note strings."""

    def test_effect_entity_atk_passes_when_col1_hits_harder(self) -> None:
        ok, note = sea.assert_scenario("effect-entity-atk", board([plant(1, 10), plant(3, 5)]))
        self.assertTrue(ok)
        self.assertEqual(note, "col1=10 col3=5")

    def test_effect_entity_atk_fails_when_col3_hits_harder(self) -> None:
        ok, note = sea.assert_scenario("effect-entity-atk", board([plant(1, 5), plant(3, 10)]))
        self.assertFalse(ok)

    def test_effect_entity_atk_fails_without_both_peas(self) -> None:
        ok, note = sea.assert_scenario("effect-entity-atk", board([plant(1, 10)]))
        self.assertFalse(ok)
        self.assertEqual(note, "need peas at col 1 and 3")

    def test_effect_entity_atk_fails_without_board_stats(self) -> None:
        ok, note = sea.assert_scenario("effect-entity-atk", None)
        self.assertFalse(ok)
        self.assertEqual(note, "no debug.board-stats")

    def test_effect_plant_type_atk_passes_when_the_pea_out_damages_the_wallnut(self) -> None:
        ok, note = sea.assert_scenario("effect-plant-type-atk",
                                       board([plant(0, 10, type_id=0), plant(2, 5, type_id=3)]))
        self.assertTrue(ok)
        self.assertEqual(note, "pea=10 wall=5")

    def test_effect_plant_type_atk_fails_without_both_types(self) -> None:
        ok, note = sea.assert_scenario("effect-plant-type-atk",
                                       board([plant(0, 10, type_id=0)]))
        self.assertFalse(ok)
        self.assertEqual(note, "need pea(type0) and wallnut(type3)")

    def test_effect_match_midspawn_passes_when_the_columns_agree(self) -> None:
        ok, _ = sea.assert_scenario("effect-match-midspawn", board([plant(1, 10), plant(3, 10.2)]))
        self.assertTrue(ok, "|10 - 10.2| = 0.2 <= 0.5")

    def test_effect_match_midspawn_fails_when_the_columns_disagree(self) -> None:
        ok, _ = sea.assert_scenario("effect-match-midspawn", board([plant(1, 10), plant(3, 20)]))
        self.assertFalse(ok, "|10 - 20| = 10 > 0.5")

    def test_effect_match_midspawn_passes_at_exactly_the_tolerance(self) -> None:
        ok, _ = sea.assert_scenario("effect-match-midspawn", board([plant(1, 10), plant(3, 10.5)]))
        self.assertTrue(ok, "|10 - 10.5| = 0.5 <= 0.5")

    def test_effect_spawn_then_grant_passes_like_entity_atk(self) -> None:
        ok, note = sea.assert_scenario("effect-spawn-then-grant", board([plant(1, 10), plant(3, 5)]))
        self.assertTrue(ok)
        self.assertEqual(note, "col1=10 col3=5")

    def test_effect_entity_midspawn_passes_when_the_grant_lifts_and_the_withdraw_restores(self) -> None:
        grant = board([plant(1, 10), plant(3, 5)])
        withdraw = board([plant(1, 7), plant(3, 7.2)])
        ok, note = sea.assert_scenario("effect-entity-midspawn", None, grant, withdraw)
        self.assertTrue(ok)
        self.assertEqual(note, "g1=10 g3=5 w1=7 w3=7.2")

    def test_effect_entity_midspawn_fails_when_the_withdraw_does_not_restore(self) -> None:
        grant = board([plant(1, 10), plant(3, 5)])
        withdraw = board([plant(1, 10), plant(3, 10.2)])
        ok, _ = sea.assert_scenario("effect-entity-midspawn", None, grant, withdraw)
        self.assertFalse(ok, "w1=10 is not < g1=10")

    def test_effect_entity_midspawn_fails_when_the_siblings_disagree_after_withdraw(self) -> None:
        grant = board([plant(1, 10), plant(3, 5)])
        withdraw = board([plant(1, 7), plant(3, 20)])
        ok, _ = sea.assert_scenario("effect-entity-midspawn", None, grant, withdraw)
        self.assertFalse(ok, "|7 - 20| = 13 > 0.5")

    def test_effect_entity_midspawn_fails_without_the_tagged_boards(self) -> None:
        ok, note = sea.assert_scenario("effect-entity-midspawn", None, None, None)
        self.assertFalse(ok)
        self.assertEqual(note, "missing after-grant/after-withdraw board-stats")

    def test_an_unknown_scenario_fails_with_a_named_note(self) -> None:
        ok, note = sea.assert_scenario("effect-nope", board([plant(1, 10)]))
        self.assertFalse(ok)
        self.assertEqual(note, "unknown scenario assert")


class ScriptedHttp:
    """A scripted answer for the prove's OWN requests (health, session, scenario queue)."""

    def __init__(self, injector_connected: bool = True, sim_enabled: bool = False,
                 source: str = "injector", queue_ok: bool = True) -> None:
        self.injector_connected = injector_connected
        self.sim_enabled = sim_enabled
        self.source = source
        self.queue_ok = queue_ok
        self.posts: list[tuple[str, dict]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        if request.data is not None:
            body = json.loads(request.data.decode("utf-8"))
            self.posts.append((url, body))
            if "/api/debug/scenario/" in url:
                return sea._Response({"ok": self.queue_ok})
            return sea._Response({"ok": True})
        if url.endswith("/health"):
            return sea._Response({"ok": True, "injectorConnected": self.injector_connected,
                                  "simEnabled": self.sim_enabled, "source": self.source})
        return sea._Response({"ok": True})


class LibResponder:
    """The event-id binary search (limit=1) and the board-stats pages (limit=500).

    `pages` is the sequence of limit=500 answers, one per get_board_stats_after call that finds
    nothing yet; the LAST page in the sequence carries the board-stats event. When the sequence is
    exhausted, empty pages are returned (the wait then runs to its deadline).
    """

    def __init__(self, pages: list[list[dict]]) -> None:
        self.pages = list(pages)
        self.page_calls = 0

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        limit_match = re.search(r"limit=(\d+)", url)
        limit = limit_match.group(1) if limit_match else "1"
        if limit == "1":
            match = re.search(r"afterId=(\d+)", url)
            after_id = int(match.group(1)) if match else 0
            if after_id == 0:
                return sea._Response({"items": [{"id": 1, "kind": "board.start",
                                                 "payload": {}}]})
            return sea._Response({"items": []})
        self.page_calls += 1
        if self.page_calls <= len(self.pages):
            return sea._Response({"items": self.pages[self.page_calls - 1]})
        return sea._Response({"items": []})


def board_stats_event(payload: dict, event_id: int = 50) -> dict:
    return {"id": event_id, "kind": "debug.board-stats", "payload": payload}


class TheBoardStatsWait(SeamGuard):
    def test_it_returns_the_LAST_matching_board_stats_payload(self) -> None:
        pages = [[board_stats_event(board([plant(1, 5), plant(3, 5)], tag="t"), event_id=10),
                  {"id": 11, "kind": "other", "payload": {}},
                  board_stats_event(board([plant(1, 10), plant(3, 5)], tag="t"), event_id=12)]]
        responder = LibResponder(pages)
        with mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(sea, "_SLEEP", lambda _s: None), \
                mock.patch.object(sea, "_MONOTONIC", FastClock()):
            result = sea.get_board_stats_after("http://127.0.0.1:5101", 0)
        self.assertEqual(result["plants"][0]["attack"], 10, "the last match's payload wins")

    def test_it_filters_by_the_payloads_own_tag(self) -> None:
        pages = [[board_stats_event(board([plant(1, 5)], tag="after-withdraw"), event_id=10),
                  board_stats_event(board([plant(1, 10)], tag="after-grant"), event_id=11)]]
        responder = LibResponder(pages)
        with mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(sea, "_SLEEP", lambda _s: None), \
                mock.patch.object(sea, "_MONOTONIC", FastClock()):
            result = sea.get_board_stats_after("http://127.0.0.1:5101", 0, "after-grant")
        self.assertEqual(result["tag"], "after-grant")
        self.assertEqual(result["plants"][0]["attack"], 10)

    def test_it_returns_None_when_no_board_stats_arrives(self) -> None:
        responder = LibResponder([])
        with mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(sea, "_SLEEP", lambda _s: None), \
                mock.patch.object(sea, "_MONOTONIC", FastClock()):
            result = sea.get_board_stats_after("http://127.0.0.1:5101", 0, timeout_sec=2)
        self.assertIsNone(result)

    def test_it_ignores_board_stats_events_of_another_tag(self) -> None:
        pages = [[board_stats_event(board([plant(1, 5)], tag="after-withdraw"), event_id=10)]]
        responder = LibResponder(pages)
        with mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(sea, "_SLEEP", lambda _s: None), \
                mock.patch.object(sea, "_MONOTONIC", FastClock()):
            result = sea.get_board_stats_after("http://127.0.0.1:5101", 0, "after-grant",
                                               timeout_sec=2)
        self.assertIsNone(result, "an after-withdraw board must not satisfy an after-grant wait")


class TheHappyRun(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()
        # one board-stats page per scenario, in scenario order; the midspawn scenario needs two
        # (after-grant, then after-withdraw)
        self.lib_pages = [
            [board_stats_event(board([plant(1, 10), plant(3, 5)]), event_id=10)],
            [board_stats_event(board([plant(0, 10, type_id=0), plant(2, 5, type_id=3)]),
                              event_id=20)],
            [board_stats_event(board([plant(1, 10), plant(3, 10.2)]), event_id=30)],
            [board_stats_event(board([plant(1, 10), plant(3, 5)]), event_id=40)],
            [board_stats_event(board([plant(1, 10), plant(3, 5)], tag="after-grant"),
                              event_id=50)],
            [board_stats_event(board([plant(1, 7), plant(3, 7.2)], tag="after-withdraw"),
                              event_id=60)],
        ]

    def _run(self, http: ScriptedHttp | None = None, lib_pages: list[list[dict]] | None = None,
             argv: list[str] | None = None):
        """Drive main() with a TEMP result path, so the suite never writes the default
        docs/research artifact -- a prove that writes its output into the tree under test is a
        prove that dirtyies it. A caller's explicit argv wins (the refusal and result-file cases
        manage their own paths)."""
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            base_argv = ["--base-url", "http://127.0.0.1:5101", "--json",
                         "--out", str(Path(d) / "result.json")]
            with mock.patch.object(sea, "_URLOPEN", http or self.http), \
                    mock.patch.object(lib, "_URLOPEN",
                                      LibResponder(lib_pages if lib_pages is not None
                                                   else self.lib_pages)), \
                    mock.patch.object(sea, "_SLEEP", lambda _s: None), \
                    mock.patch.object(sea, "_MONOTONIC", FastClock()):
                err = io.StringIO()
                out = io.StringIO()
                with redirect_stderr(err), redirect_stdout(out):
                    code = sea.main(argv if argv is not None else base_argv)
            return code, err.getvalue(), out.getvalue()

    def test_a_clean_run_exits_0_and_reports_PASS(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, sea.EXIT_OK)
        envelope = json.loads(out)
        self.assertEqual(envelope["verdict"], "PASS")
        payload = envelope["payload"]
        self.assertEqual(payload["passed"], 5)
        self.assertEqual(payload["total"], 5)
        self.assertEqual(payload["status"], "PASS")
        self.assertEqual(payload["game"], "pvzrh-3.9")
        self.assertTrue(all(r["pass"] for r in payload["results"]))

    def test_the_five_scenarios_run_in_order(self) -> None:
        code, err, _ = self._run()
        self.assertEqual(code, sea.EXIT_OK)
        rows = [line for line in err.splitlines() if line.startswith("[")]
        self.assertEqual([r.split("] ")[1].split(":")[0] for r in rows], list(sea.SCENARIOS))
        self.assertTrue(all(r.startswith("[PASS]") for r in rows))

    def test_the_scenario_queue_posts_the_scenario_id(self) -> None:
        self._run()
        scenario_posts = [url for url, _ in self.http.posts if "/api/debug/scenario/" in url]
        self.assertEqual(len(scenario_posts), 5)
        self.assertIn("effect-entity-atk", scenario_posts[0])
        self.assertIn("effect-entity-midspawn", scenario_posts[4])

    def test_a_failing_scenario_FAILs_the_run(self) -> None:
        self.lib_pages[0] = [board_stats_event(board([plant(1, 5), plant(3, 10)]), event_id=10)]
        code, err, out = self._run()
        self.assertEqual(code, sea.EXIT_FAILED)
        envelope = json.loads(out)
        self.assertEqual(envelope["verdict"], "FAILED")
        payload = envelope["payload"]
        self.assertEqual(payload["status"], "PENDING_LIVE")
        self.assertEqual(payload["passed"], 4)
        self.assertIn("[FAIL] effect-entity-atk", err)

    def test_a_scenario_that_fails_to_queue_FAILs_that_scenario_only(self) -> None:
        self.http = ScriptedHttp(queue_ok=False)
        code, err, out = self._run()
        self.assertEqual(code, sea.EXIT_FAILED)
        payload = json.loads(out)["payload"]
        self.assertEqual(payload["passed"], 0)
        self.assertIn("scenario queue failed", payload["results"][0]["note"])

    def test_a_SIM_board_is_refused_at_the_gate(self) -> None:
        self.http = ScriptedHttp(sim_enabled=True)
        code, _, out = self._run()
        self.assertEqual(code, sea.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INJECTOR-NOT-CONNECTED")

    def test_a_disconnected_injector_is_refused_at_the_gate(self) -> None:
        self.http = ScriptedHttp(injector_connected=False)
        code, _, out = self._run()
        self.assertEqual(code, sea.EXIT_REFUSED)

    def test_the_result_file_is_written(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            out_path = Path(d) / "result.json"
            code, err, _ = self._run(argv=["--base-url", "http://127.0.0.1:5101",
                                           "--out", str(out_path)])
            self.assertEqual(code, sea.EXIT_OK)
            self.assertIn("Wrote", err)
            self.assertTrue(out_path.is_file(), "the result file was not written")
            on_disk = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertEqual(on_disk["passed"], 5)
            self.assertEqual(on_disk["status"], "PASS")

    def test_a_NEGATIVE_wait_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101",
                                       "--wait-seconds", "-1", "--json"])
        self.assertEqual(code, sea.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-WAIT")

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101", "--timeout", "0",
                                       "--json"])
        self.assertEqual(code, sea.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - sea.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - sea.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(sea.EXIT_OK, 0)
        self.assertEqual(sea.EXIT_FAILED, 1)
        self.assertEqual(sea.EXIT_REFUSED, 64)

    def test_it_uses_the_shared_resolve_base_url_and_NOT_a_hardcoded_port(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.resolve_base_url", source)
        tree = ast.parse(source)
        defaults = [ast.unparse(n) for node in ast.walk(tree)
                    if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "add_argument"
                    for n in node.defaults if isinstance(n, ast.Constant)]
        for default in defaults:
            self.assertNotIn("5088", default, f"a flag still defaults to the owner's port: {default}")

    def test_it_uses_the_shared_event_id_search_and_NOT_a_reimplementation(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.get_debug_max_event_id", source)
        self.assertNotIn("def get_max_event_id", source,
                         "the event-id binary search was reimplemented locally")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-WaitSeconds", "-OutJson"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("smoke-effect-scoped-atk.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "assert", "deadline"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--wait-seconds", "--out", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib", "time"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id in ("sea", "lib"):
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
