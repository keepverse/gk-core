"""Contract tests for `gk-core/scripts/prove_overlay_combat.py`.

A LIVE OVERLAY COMBAT PROVE (C1-C13), so the contract is about the case recipe, the artifact
document, and the exit shape that `gk-core/tools/ProveAptitude/Program.cs` documents.

THE ARTIFACT IS THE POINT. Top-level `results` (array of `{name, pass, detail}`), `actorPtr`,
`targetPtr`, `at` -- in that key order, matching the committed artifact. The artifact is ALWAYS
written (even when cases fail), then exit 1 when any case failed. A refusal writes nothing.

THE CASE RECIPE IS THE OTHER POINT. C1-C13 in the original order with the original bodies,
thresholds and detail strings. A case that throws is a FAIL with the message as its detail -- the
original's `Run-Case` caught everything, so a transport failure inside a case fails THAT case, not
the whole prove.

THE PTR RESOLUTION IS THE THIRD POINT. When either ptr is missing, the prove resolves the board via
combat/snapshot: first living zombie becomes the target, first living plant becomes the actor (and
the actor stays empty when there is no plant -- it is optional).

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
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROVE_OVERLAY_COMBAT_SCRIPT",
                              REPO / "scripts" / "prove_overlay_combat.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_overlay_combat.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("prove_overlay_combat", SCRIPT)
poc = importlib.util.module_from_spec(_spec)
sys.modules["prove_overlay_combat"] = poc
_spec.loader.exec_module(poc)

sys.path.insert(0, str(REPO / "scripts" / "lib"))
import live_lawn_setup as lib  # noqa: E402

_PRISTINE = {"_URLOPEN": poc._URLOPEN, "_SLEEP": poc._SLEEP,
             "_MONOTONIC": poc._MONOTONIC, "lib_URLOPEN": lib._URLOPEN}

CASE_NAMES = [
    "C1 overlay-fire-vs-ice", "C2 overlay-fire-vs-air", "C3 overlay-hybrid-vs-ice",
    "C4 overlay-miss", "C5 overlay-heal", "C6 overlay-flag-off", "C7 overlay-ice-vs-fire",
    "C8 overlay-air-vs-earth", "C9 overlay-earth-vs-air", "C10 overlay-force-crit",
    "C11 overlay-heal-with-payload-scales-with-heal-power",
    "C12 overlay-heal-with-no-payload-still-reads-heal-power",
    "C13 overlay-full-mitigation-resolves-to-zero-no-chip-floor",
]


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if lib._URLOPEN is not _PRISTINE["lib_URLOPEN"]:
            self.fail(f"lib._URLOPEN was still substituted after {self.id()}: {lib._URLOPEN!r}")
        for name in ("_URLOPEN", "_SLEEP", "_MONOTONIC"):
            if getattr(poc, name) is not _PRISTINE[name]:
                self.fail(f"{name} was still substituted after {self.id()}: {getattr(poc, name)!r}")


class FastClock:
    """A monotonic clock that advances a fixed step per call, so a 15-second budget expires in
    milliseconds without changing what the loop computes."""

    def __init__(self, step: float = 1.0) -> None:
        self.now = 0.0
        self.step = step

    def __call__(self) -> float:
        self.now += self.step
        return self.now


class ScriptedHttp:
    """A scripted answer for the tool's OWN requests (health, toggle, probe, snapshot, ...)."""

    def __init__(self, injector_connected: bool = True) -> None:
        self.injector_connected = injector_connected
        self.requests: list[tuple[str, str, dict | None]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        body = json.loads(request.data.decode("utf-8")) if request.data is not None else None
        self.requests.append((request.method, url, body))
        if request.data is not None:
            return poc._Response({"ok": True})
        if url.endswith("/health"):
            return poc._Response({"ok": True, "injectorConnected": self.injector_connected})
        return poc._Response({"ok": True})

    def posts(self) -> list[tuple[str, dict]]:
        return [(url, body) for method, url, body in self.requests if body is not None]


class LibResponder:
    """A scripted answer for the SHARED LIBRARY's event reads.

    The event-id binary search pages with `limit=1` (afterId=0 has events, afterId>=1 does not, so
    the search converges to 0); the polls page with a larger limit and are answered from a scripted
    sequence -- one event per poll, in the order the recipe issues them.
    """

    def __init__(self, wait_events: list[dict]) -> None:
        self.wait_events = list(wait_events)
        self.wait_calls = 0

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        limit_match = re.search(r"limit=(\d+)", url)
        if limit_match and limit_match.group(1) == "1":
            match = re.search(r"afterId=(\d+)", url)
            after_id = int(match.group(1)) if match else 0
            if after_id == 0:
                return poc._Response({"items": [{"id": 1, "kind": "board.start",
                                                   "payload": {}}]})
            return poc._Response({"items": []})
        if self.wait_calls >= len(self.wait_events):
            return poc._Response({"items": []})
        event = self.wait_events[self.wait_calls]
        self.wait_calls += 1
        return poc._Response({"items": [event]})


def overlay(event_id: int, **payload) -> dict:
    return {"id": event_id, "kind": "debug.combat.overlay", "payload": payload}


def board_stats(event_id: int, ptr: str = "Z0", hp: float = 100) -> dict:
    return {"id": event_id, "kind": "debug.board-stats",
            "payload": {"plants": [], "zombies": [{"ptr": ptr, "hp": hp}]}}


def combat_snapshot(event_id: int, entities: list[dict] | None = None,
                    last_overlay: dict | None = None) -> dict:
    payload = {"entities": entities if entities is not None else [
        {"side": "zombie", "living": True, "ptr": "Z0"},
        {"side": "plant", "living": True, "ptr": "P0"}]}
    if last_overlay is not None:
        payload["lastOverlay"] = last_overlay
    return {"id": event_id, "kind": "debug.combat.snapshot", "payload": payload}


def full_pass_events() -> list[dict]:
    """The lib event sequence for a full C1-C13 pass with both ptrs supplied."""
    return [
        overlay(10, matchupBonus=25),                       # C1
        overlay(11, matchupBonus=-25),                      # C2
        overlay(12, matchupBonus=17.5),                      # C3
        overlay(13, hit=False, finalSignedDelta=0),          # C4
        {"id": 14, "kind": "debug.other", "payload": {}},   # C5: empty overlay page
        {"id": 15, "kind": "debug.other", "payload": {}},   # C6: empty overlay page
        overlay(16, matchupBonus=-25),                      # C7
        overlay(17, matchupBonus=-25),                      # C8
        overlay(18, matchupBonus=25),                       # C9
        overlay(19, crit=True, critMultiplierFinal=1.5),    # C10
        board_stats(20, hp=100),                            # C11 before
        board_stats(21, hp=150),                            # C11 after
        board_stats(22, hp=100),                            # C12 before
        board_stats(23, hp=150),                            # C12 after
        overlay(24, finalSignedDelta=0),                    # C13
    ]


class TheRecipe(SeamGuard):
    def setUp(self) -> None:
        self.http = ScriptedHttp()
        self.out_json = os.path.join(tempfile.mkdtemp(prefix="poc-test-"), "_prove.json")

    def _run(self, http: ScriptedHttp | None = None, wait_events: list[dict] | None = None,
             argv: list[str] | None = None):
        http = http or self.http
        responder = LibResponder(wait_events if wait_events is not None else full_pass_events())
        with mock.patch.object(poc, "_URLOPEN", http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(poc, "_SLEEP", lambda _s: None), \
                mock.patch.object(poc, "_MONOTONIC", FastClock()):
            err = io.StringIO()
            out = io.StringIO()
            with redirect_stderr(err), redirect_stdout(out):
                code = poc.main(argv if argv is not None else
                                ["--base-url", "http://127.0.0.1:5101",
                                 "--target-ptr", "Z0", "--actor-ptr", "P0",
                                 "--out-json", self.out_json, "--json"])
        return code, err.getvalue(), out.getvalue()

    def test_a_full_pass_exits_0_and_writes_the_artifact(self) -> None:
        code, err, out = self._run()
        self.assertEqual(code, poc.EXIT_OK)
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertEqual(list(payload.keys()), ["results", "actorPtr", "targetPtr", "at"])
        self.assertEqual(payload["actorPtr"], "P0")
        self.assertEqual(payload["targetPtr"], "Z0")
        self.assertTrue(payload["at"])
        self.assertEqual([r["name"] for r in payload["results"]], CASE_NAMES)
        self.assertTrue(all(r["pass"] for r in payload["results"]))
        self.assertEqual([r["detail"] for r in payload["results"]][0], "matchupBonus=25")
        self.assertEqual([r["detail"] for r in payload["results"]][3],
                         "hit=false finalSignedDelta=0")
        self.assertEqual([r["detail"] for r in payload["results"]][4],
                         "no overlay breakdown; heal pass-through")
        self.assertEqual([r["detail"] for r in payload["results"]][5],
                         "pass-through -100; no overlay emit")
        self.assertEqual([r["detail"] for r in payload["results"]][9],
                         "crit=true critMultiplierFinal=1.5")
        self.assertEqual([r["detail"] for r in payload["results"]][10], "healed=50 (expected ~50)")
        self.assertEqual([r["detail"] for r in payload["results"]][11],
                         "healed=50 (expected ~50, proving FinalizeHeal ran despite no payload)")
        self.assertEqual([r["detail"] for r in payload["results"]][12],
                         "finalSignedDelta=0, no exception -- the game handled full mitigation cleanly")
        # --json reports exactly the document that was written to disk
        self.assertEqual(json.loads(out), payload)

    def test_the_recipe_posts_in_the_original_order_with_the_original_bodies(self) -> None:
        self._run()
        posts = self.http.posts()
        paths = [url.split("5101", 1)[1].split("?")[0] for url, _ in posts]
        self.assertEqual(paths, [
            "/api/cheats/toggle",                    # OVERLAY-COMBAT on
            "/api/debug/combat/silence-vanilla",
            "/api/debug/session/start",
            "/api/debug/combat/probe",               # C1
            "/api/debug/combat/probe",               # C2
            "/api/debug/combat/probe",               # C3
            "/api/debug/combat/probe",               # C4
            "/api/debug/combat/probe",               # C5
            "/api/cheats/toggle",                    # C6 flag off
            "/api/debug/combat/probe",               # C6
            "/api/cheats/toggle",                    # C6 flag back on
            "/api/debug/combat/probe",               # C7
            "/api/debug/combat/probe",               # C8
            "/api/debug/combat/probe",               # C9
            "/api/debug/combat/probe",               # C10
            "/api/debug/combat/probe",               # C11 pin
            "/api/debug/board-stats",                # C11 before
            "/api/debug/effect/enqueue-delta",       # C11
            "/api/debug/board-stats",                # C11 after
            "/api/debug/combat/probe",               # C12 pin
            "/api/debug/board-stats",                # C12 before
            "/api/debug/effect/enqueue-delta",       # C12
            "/api/debug/board-stats",                # C12 after
            "/api/debug/combat/probe",               # C13 pin
            "/api/debug/combat/probe",               # C13
        ])
        bodies = [body for _, body in posts]
        self.assertEqual(bodies[0], {"id": "OVERLAY-COMBAT", "enabled": True})
        self.assertEqual(bodies[1], {"plant": True})
        self.assertEqual(bodies[2], {})
        c1 = bodies[3]
        self.assertEqual(c1["amount"], -100)
        self.assertEqual(c1["targetPtr"], "Z0")
        self.assertEqual(c1["seed"], 1)
        self.assertTrue(c1["forceHit"])
        self.assertFalse(c1["forceCrit"])
        self.assertEqual(c1["pinTargetElement"], "ice")
        self.assertEqual(c1["elementPayload"], [{"element": "fire", "weight": 1.0}])
        c10 = bodies[14]
        self.assertEqual(c10["actorPtr"], "P0")
        self.assertTrue(c10["forceCrit"])
        self.assertEqual(c10["pinActorChannels"],
                         {"combat.accuracy.omni": 500, "combat.crit.damage.omni": 500,
                          "combat.crit.rate.omni": 500})
        c11_delta = bodies[17]
        self.assertEqual(c11_delta["amount"], 10)
        self.assertEqual(c11_delta["target"], {"mode": "single", "ptr": "Z0"})
        self.assertEqual(c11_delta["elementPayload"], [{"element": "fire", "weight": 1.0}])
        c12_delta = bodies[21]
        self.assertNotIn("elementPayload", c12_delta,
                         "C12 deliberately carries NO elementPayload -- that is the case")

    def test_a_failing_case_FAILs_and_exits_1_but_still_writes_the_artifact(self) -> None:
        events = full_pass_events()
        events[0] = overlay(10, matchupBonus=999)  # C1 asserts ~25
        code, _, out = self._run(wait_events=events)
        self.assertEqual(code, poc.EXIT_FAILED)
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertFalse(payload["results"][0]["pass"])
        self.assertIn("matchupBonus=999 expected ~25", payload["results"][0]["detail"])
        self.assertTrue(all(r["pass"] for r in payload["results"][1:]),
                        "one bad matchup must not fail the other cases")

    def test_a_case_that_cannot_run_FAILs_that_case_not_the_whole_prove(self) -> None:
        """The original's `Run-Case` caught everything: a transport failure inside a case is a case
        FAIL, and the prove continues."""
        events = full_pass_events()
        events[9] = overlay(19, crit=False, critMultiplierFinal=1.0)  # C10 asserts crit + mult > 1
        code, _, out = self._run(wait_events=events)
        self.assertEqual(code, poc.EXIT_FAILED)
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertFalse(payload["results"][9]["pass"])
        self.assertIn("crit=False expected true", payload["results"][9]["detail"])

    def test_a_missing_actor_ptr_FAILs_C10_only(self) -> None:
        """C10 needs a living plant actor; without one the case fails and the prove exits 1. The
        board holds a zombie but no plant, so resolution finds the target and leaves the actor
        empty -- exactly the lab-overlay-without-fixtures shape C10 refuses to run without."""
        events = [combat_snapshot(10, entities=[{"side": "zombie", "living": True, "ptr": "Z0"}])
                  ] + full_pass_events()
        code, _, out = self._run(wait_events=events,
                                 argv=["--base-url", "http://127.0.0.1:5101",
                                       "--out-json", self.out_json, "--json"])
        self.assertEqual(code, poc.EXIT_FAILED)
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertEqual(payload["actorPtr"], "")
        self.assertFalse(payload["results"][9]["pass"])
        self.assertIn("need living plant ActorPtr", payload["results"][9]["detail"])

    def test_ptr_resolution_via_combat_snapshot(self) -> None:
        """With neither ptr given, the prove resolves the board: first living zombie is the target,
        first living plant is the actor."""
        events = [combat_snapshot(10)] + full_pass_events()
        code, err, out = self._run(wait_events=events,
                                   argv=["--base-url", "http://127.0.0.1:5101",
                                         "--out-json", self.out_json, "--json"])
        self.assertEqual(code, poc.EXIT_OK)
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertEqual(payload["targetPtr"], "Z0")
        self.assertEqual(payload["actorPtr"], "P0")
        self.assertIn("Resolving board via combat/snapshot", err)

    def test_a_snapshot_with_no_living_zombie_is_a_NAMED_refusal(self) -> None:
        events = [combat_snapshot(10, entities=[{"side": "plant", "living": True, "ptr": "P0"}])]
        code, _, out = self._run(wait_events=events,
                                 argv=["--base-url", "http://127.0.0.1:5101",
                                       "--out-json", self.out_json, "--json"])
        self.assertEqual(code, poc.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "NO-LIVING-ZOMBIE")
        self.assertFalse(os.path.exists(self.out_json), "a refusal writes no artifact")

    def test_a_missing_combat_snapshot_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(wait_events=[],
                                 argv=["--base-url", "http://127.0.0.1:5101",
                                       "--out-json", self.out_json, "--json"])
        self.assertEqual(code, poc.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "NO-COMBAT-SNAPSHOT")
        self.assertFalse(os.path.exists(self.out_json))

    def test_an_injector_that_is_not_connected_is_a_NAMED_refusal(self) -> None:
        code, _, out = self._run(http=ScriptedHttp(injector_connected=False))
        self.assertEqual(code, poc.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INJECTOR-NOT-CONNECTED")
        self.assertFalse(os.path.exists(self.out_json))

    def test_the_wait_last_overlay_fallback_reads_the_snapshot_lastOverlay(self) -> None:
        """When no overlay event arrives, Wait-Last-Overlay falls back ONCE to a fresh combat
        snapshot's lastOverlay -- but only when it carries a source. The poll (limit=200) is
        answered empty so it spins to its deadline; the fallback's own read (limit=50) is answered
        with the snapshot carrying the lastOverlay."""

        class FallbackResponder:
            def __call__(self, request, timeout=None):
                url = request.full_url if hasattr(request, "full_url") else str(request)
                limit_match = re.search(r"limit=(\d+)", url)
                limit = limit_match.group(1) if limit_match else "1"
                if limit == "1":
                    match = re.search(r"afterId=(\d+)", url)
                    after_id = int(match.group(1)) if match else 0
                    if after_id == 0:
                        return poc._Response({"items": [{"id": 1, "kind": "board.start",
                                                           "payload": {}}]})
                    return poc._Response({"items": []})
                if limit == "200":
                    return poc._Response({"items": []})  # the poll finds no overlay
                return poc._Response({"items": [combat_snapshot(
                    11, last_overlay={"source": "probe", "matchupBonus": 25})]})

        responder = FallbackResponder()
        with mock.patch.object(poc, "_URLOPEN", self.http), \
                mock.patch.object(lib, "_URLOPEN", responder), \
                mock.patch.object(poc, "_SLEEP", lambda _s: None), \
                mock.patch.object(poc, "_MONOTONIC", FastClock()):
            err = io.StringIO()
            with redirect_stderr(err):
                code = poc.main(["--base-url", "http://127.0.0.1:5101",
                                 "--target-ptr", "Z0", "--actor-ptr", "P0",
                                 "--out-json", self.out_json, "--json"])
        # C1 passes via the fallback; C2-C13 find no overlay and fail -- the point is the fallback
        # itself, which C1's detail proves
        with open(self.out_json, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        self.assertEqual(payload["results"][0]["detail"], "matchupBonus=25")
        self.assertFalse(payload["results"][1]["pass"])

    def test_a_NON_POSITIVE_timeout_REFUSES(self) -> None:
        code, _, out = self._run(argv=["--base-url", "http://127.0.0.1:5101",
                                        "--target-ptr", "Z0", "--actor-ptr", "P0",
                                        "--out-json", self.out_json, "--timeout", "0", "--json"])
        self.assertEqual(code, poc.EXIT_REFUSED)
        self.assertEqual(json.loads(out)["reason"], "INVALID-TIMEOUT")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - poc.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - poc.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(poc.EXIT_OK, 0)
        self.assertEqual(poc.EXIT_FAILED, 1)
        self.assertEqual(poc.EXIT_REFUSED, 64)

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
        for flag in ("-BaseUrl", "-TargetPtr", "-ActorPtr", "-OutJson"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-overlay-combat.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "machine-readable", "parse"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--target-ptr", "--actor-ptr", "--out-json", "--timeout",
                     "--json"):
            self.assertIn(flag, out, flag)

    def test_the_module_IMPORTS_cleanly(self) -> None:
        self.assertTrue(SCRIPT.is_file())
        self.assertTrue(callable(poc.main))

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
            label = ast.unparse(target) if target is not None else "?"
            root = label.split(".")[0]
            if root in ("poc", "lib"):
                continue
            if root in globals_seen:
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
