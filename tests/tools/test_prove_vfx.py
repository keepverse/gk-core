"""Contract tests for `gk-core/scripts/prove_vfx.py`.

THE PROOFABLE SUBSTANCE IS THE TEST MATRIX AND THE MATCH/VALIDATE LOGIC, and they are PURE.
Reaching the rest of the flow needs a running game with the injector connected, which no slot
on this machine has -- and a stub that fakes a connected injector would be proving a fiction.
So the matrix is decided by planted data here, and the refusal path is decided against a REAL
server, which is what every one of these live scripts actually does until a game is running.

THE SKIP CASE MUST BE ok-DISTINCT FROM A PASS. The PowerShell original wrote `ok = $true` for
the no-TargetPtr path, so a run with zero organic coverage reported `pass: true`. A skip is
now `ok: false` with a detail that says SKIPPED.

THE VERDICT JSON SHAPE IS PINNED: top-level `at`, `baseUrl`, `targetPtr`, `pass`, `results`;
per-result exactly `case` (string), `ok` (bool), `detail` (string). The vfx-v2-spec.md:124
contract says "verdict JSON unchanged in shape".
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROVE_VFX_SCRIPT",
                            REPO / "scripts" / "prove_vfx.py")).resolve()


def _load():
    spec = importlib.util.spec_from_file_location("prove_vfx", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["prove_vfx"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


def _passing_run(target_ptr: str) -> dict:
    """Run the full matrix with every library call mocked to succeed.

    Returns the verdict dict. Fast: no real sleeps, no real HTTP.
    """
    with tempfile.TemporaryDirectory() as td:
        out = Path(td) / "verdict.json"

        # A comprehensive set of fx events that satisfies every count-based assertion
        # in the run() function (rate-limit, mute, master-toggle, sustain-refresh, fx-list).
        comprehensive_events = [
            {"id": 200, "kind": "debug.fx.shown", "payload": {"cueId": "debug.probe", "amount": 0}},
            {"id": 201, "kind": "debug.fx.skipped", "payload": {"reason": "rate-limited"}},
            {"id": 202, "kind": "debug.fx.skipped", "payload": {"reason": "muted"}},
            {"id": 203, "kind": "debug.fx.skipped", "payload": {"reason": "disabled"}},
            {"id": 204, "kind": "debug.fx.state.started", "payload": {"statusId": "pact_mark"}},
            {"id": 205, "kind": "debug.fx.list", "payload": {"cues": ["combat.hit", "debug.probe"]}},
        ]

        def fake_get_events(url, after_id, limit, timeout=10):
            return comprehensive_events

        def fake_max_event_id(url, timeout=10, budget_sec=30.0):
            return 100

        def fake_invoke_post(url, path, body=None, timeout=8):
            return {}

        def fake_run_case(base_url, play, after_id):
            return {"case": play["name"], "ok": True, "detail": "mocked pass"}

        def fake_run_organic(base_url, name, path, body, after_id, expect, timeout_ms=5000):
            return {"case": name, "ok": True, "detail": "mocked pass"}

        def fake_wait_match(url, after_id, match_fn, timeout_ms=5000,
                            poll_interval=0.25, event_timeout=10):
            return {"cueId": "debug.probe", "amount": 0, "rgb": "#FFFFFF",
                    "hybrid": True, "primitives": ["burst"], "reason": None,
                    "statusId": "wither"}

        def fake_apply_status(*args, **kwargs):
            return True

        with mock.patch.object(p.lib, "get_events", side_effect=fake_get_events), \
             mock.patch.object(p.lib, "get_debug_max_event_id", side_effect=fake_max_event_id), \
             mock.patch.object(p.lib, "invoke_debug_post", side_effect=fake_invoke_post), \
             mock.patch.object(p, "run_case", side_effect=fake_run_case), \
             mock.patch.object(p, "run_organic_case", side_effect=fake_run_organic), \
             mock.patch.object(p, "wait_for_fx_match", side_effect=fake_wait_match), \
             mock.patch.object(p, "_apply_status_until_started", side_effect=fake_apply_status), \
             mock.patch.object(p, "get_fx_events", side_effect=fake_get_events), \
             mock.patch.object(p, "set_cheat", return_value=None), \
             mock.patch("time.sleep", return_value=None):
            exit_code = p.run(
                base_url="http://127.0.0.1:5088",
                target_ptr=target_ptr,
                col=4, row=2,
                out_json=out,
            )

        with open(out, "r", encoding="utf-8") as f:
            verdict = json.load(f)
        verdict["_exit_code"] = exit_code
        return verdict


# ---------------------------------------------------------------------------
# Test matrix
# ---------------------------------------------------------------------------

class TestMatrix(unittest.TestCase):
    """The matrix is data, so a case can pin it with no server."""

    def test_base_plays_always_present(self) -> None:
        plays = p.build_plays(4, 2, "")
        names = [pl["name"] for pl in plays]
        for expected in ("probe", "hit-neutral-cell", "hit-fire", "hit-ice", "hit-air",
                         "hit-earth", "hit-light", "hit-dark", "hit-hybrid",
                         "heal-rising", "unknown-cue", "rift-portal-open",
                         "rift-portal-surge", "rift-quarantine-seal",
                         "rift-quarantine-fade", "rift-missing-anchor"):
            self.assertIn(expected, names)

    def test_ptr_plays_only_with_target_ptr(self) -> None:
        plays_no = p.build_plays(4, 2, "")
        names_no = [pl["name"] for pl in plays_no]
        for absent in ("hit-ptr-crit-fire", "hit-ptr-plain-no-burst",
                       "heal-ptr", "rift-portal-open-ptr"):
            self.assertNotIn(absent, names_no)

        plays_yes = p.build_plays(4, 2, "2897AD9EC80")
        names_yes = [pl["name"] for pl in plays_yes]
        for present in ("hit-ptr-crit-fire", "hit-ptr-plain-no-burst",
                        "heal-ptr", "rift-portal-open-ptr"):
            self.assertIn(present, names_yes)

    def test_status_recipes_all_21_present(self) -> None:
        plays = p.build_plays(4, 2, "")
        names = [pl["name"] for pl in plays]
        for sid in ("butter", "freeze", "cold", "poison", "hypno", "ember",
                    "jala", "kelp", "wither", "bond", "rally", "leech",
                    "expose", "command", "shatter", "charm_pulse", "blight",
                    "rot", "spark", "pact_mark", "spore"):
            self.assertIn(f"status-recipe-{sid}", names)

    def test_every_play_has_name_path_body_expect(self) -> None:
        for target_ptr in ("", "2897AD9EC80"):
            for play in p.build_plays(4, 2, target_ptr):
                self.assertIsInstance(play["name"], str)
                self.assertIsInstance(play["path"], str)
                self.assertIsInstance(play["body"], dict)
                self.assertIsInstance(play["expect"], dict)
                self.assertIn("kind", play["expect"])

    def test_unique_amounts_per_cue(self) -> None:
        """Every fx.play case carries a UNIQUE amount so its shown/skipped event is matched
        exactly (cueId + amount), immune to late arrivals from the previous case."""
        plays = p.build_plays(4, 2, "2897AD9EC80")
        seen: dict[str, int] = {}
        for play in plays:
            cue = play["body"].get("cueId")
            amount = play["body"].get("amount")
            if cue is None or amount is None:
                continue
            key = f"{cue}:{amount}"
            self.assertNotIn(key, seen, f"duplicate amount {amount} for cue {cue}")
            seen[key] = play["name"]


# ---------------------------------------------------------------------------
# Match / validate
# ---------------------------------------------------------------------------

class TestMatchEvent(unittest.TestCase):
    """Base match: kind + cueId + amount (+ reason for skip)."""

    def test_matches_kind_cue_amount(self) -> None:
        ev = {"kind": "debug.fx.shown"}
        pl = {"cueId": "combat.hit", "amount": -61}
        expect = {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -61}
        self.assertTrue(p.match_event(ev, pl, expect))

    def test_rejects_wrong_kind(self) -> None:
        ev = {"kind": "debug.fx.skipped"}
        pl = {"cueId": "combat.hit", "amount": -61}
        expect = {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -61}
        self.assertFalse(p.match_event(ev, pl, expect))

    def test_rejects_wrong_amount(self) -> None:
        ev = {"kind": "debug.fx.shown"}
        pl = {"cueId": "combat.hit", "amount": -99}
        expect = {"kind": "debug.fx.shown", "cueId": "combat.hit", "amount": -61}
        self.assertFalse(p.match_event(ev, pl, expect))

    def test_skip_requires_reason(self) -> None:
        ev = {"kind": "debug.fx.skipped"}
        pl = {"cueId": "combat.hit", "amount": -55, "reason": "no-element"}
        expect = {"kind": "debug.fx.skipped", "cueId": "combat.hit",
                  "amount": -55, "reason": "no-element"}
        self.assertTrue(p.match_event(ev, pl, expect))

    def test_skip_rejects_wrong_reason(self) -> None:
        ev = {"kind": "debug.fx.skipped"}
        pl = {"cueId": "combat.hit", "amount": -55, "reason": "muted"}
        expect = {"kind": "debug.fx.skipped", "cueId": "combat.hit",
                  "amount": -55, "reason": "no-element"}
        self.assertFalse(p.match_event(ev, pl, expect))

    def test_none_amount_matches_any(self) -> None:
        ev = {"kind": "debug.fx.shown"}
        pl = {"cueId": "debug.probe", "amount": 0}
        expect = {"kind": "debug.fx.shown", "cueId": "debug.probe", "amount": None}
        self.assertTrue(p.match_event(ev, pl, expect))


class TestValidatePayload(unittest.TestCase):
    """Post-match validation: rgb / hybrid / expectPrim / expectNotPrim."""

    def test_passes_when_all_match(self) -> None:
        pl = {"rgb": "#FF5A28", "hybrid": False, "primitives": ["burst"]}
        expect = {"rgb": "#FF5A28", "expectPrim": "burst"}
        ok, detail = p.validate_payload(pl, expect)
        self.assertTrue(ok)
        self.assertIn("rgb=#FF5A28", detail)

    def test_fails_on_wrong_rgb(self) -> None:
        pl = {"rgb": "#FFFFFF", "hybrid": False, "primitives": ["burst"]}
        expect = {"rgb": "#FF5A28"}
        ok, detail = p.validate_payload(pl, expect)
        self.assertFalse(ok)
        self.assertIn("#FFFFFF", detail)

    def test_fails_when_hybrid_expected_but_false(self) -> None:
        pl = {"rgb": "#FF5A28", "hybrid": False, "primitives": ["burst"]}
        expect = {"hybrid": True}
        ok, detail = p.validate_payload(pl, expect)
        self.assertFalse(ok)

    def test_fails_when_prim_missing(self) -> None:
        pl = {"rgb": "#FF5A28", "hybrid": False, "primitives": ["burst"]}
        expect = {"expectPrim": "flash"}
        ok, detail = p.validate_payload(pl, expect)
        self.assertFalse(ok)
        self.assertIn("flash", detail)

    def test_fails_when_not_prim_present(self) -> None:
        pl = {"rgb": "#FFFFFF", "hybrid": False, "primitives": ["floater", "burst"]}
        expect = {"expectNotPrim": "burst"}
        ok, detail = p.validate_payload(pl, expect)
        self.assertFalse(ok)

    def test_passes_when_not_prim_absent(self) -> None:
        pl = {"rgb": "#FFFFFF", "hybrid": False, "primitives": ["floater"]}
        expect = {"expectNotPrim": "burst"}
        ok, _ = p.validate_payload(pl, expect)
        self.assertTrue(ok)

    def test_primitives_not_a_list_treated_as_single(self) -> None:
        pl = {"rgb": "#FF5A28", "hybrid": False, "primitives": "burst"}
        expect = {"expectPrim": "burst"}
        ok, _ = p.validate_payload(pl, expect)
        self.assertTrue(ok)


# ---------------------------------------------------------------------------
# Filter
# ---------------------------------------------------------------------------

class TestFilterFxEvents(unittest.TestCase):
    def test_keeps_only_fx_kinds(self) -> None:
        events = [
            {"id": 1, "kind": "debug.fx.shown"},
            {"id": 2, "kind": "chatter.event"},
            {"id": 3, "kind": "debug.fx.skipped"},
            {"id": 4, "kind": "debug.status.apply"},
            {"id": 5, "kind": "debug.fx.state.started"},
        ]
        result = p.filter_fx_events(events)
        self.assertEqual(len(result), 3)
        self.assertEqual([e["id"] for e in result], [1, 3, 5])

    def test_ignores_non_dict_entries(self) -> None:
        events = [{"id": 1, "kind": "debug.fx.shown"}, "not-a-dict", None, 42]
        result = p.filter_fx_events(events)
        self.assertEqual(len(result), 1)


# ---------------------------------------------------------------------------
# CLI surface
# ---------------------------------------------------------------------------

class TestCliSurface(unittest.TestCase):
    """The CLI must be importable and the argument surface must be stable."""

    def test_module_has_main(self) -> None:
        self.assertTrue(callable(p.main))

    def test_module_has_run(self) -> None:
        self.assertTrue(callable(p.run))

    def test_module_has_refusal(self) -> None:
        self.assertTrue(issubclass(p.Refusal, Exception))

    def test_refusal_reasons_are_closed_set(self) -> None:
        expected = {
            "BASE-URL-INVALID", "SERVER-UNREACHABLE", "EVENT-READ-FAILED",
            "EVENT-READ-TIMED-OUT", "SEARCH-BUDGET-EXHAUSTED", "DEBUG-POST-FAILED",
            "PAYLOAD-UNPARSEABLE", "CHEAT-TOGGLE-FAILED",
        }
        self.assertEqual(p.REFUSAL_REASONS, expected)

    def test_exit_refused_is_64(self) -> None:
        self.assertEqual(p.EXIT_REFUSED, 64)


# ---------------------------------------------------------------------------
# Refusal paths (against a real server that is not there)
# ---------------------------------------------------------------------------

class TestRefusalPaths(unittest.TestCase):
    """The refusal path is decided against a REAL server, which is what every one of these
    live scripts actually does until a game is running."""

    def test_invalid_base_url_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"
            exit_code = p.main(["--base-url", "not-a-url", "--out-json", str(out)])
            self.assertEqual(exit_code, p.EXIT_REFUSED)

    def test_unreachable_server_records_fail_and_writes_json(self) -> None:
        """An unreachable server is caught inside run(), recorded as a failed case, and the
        verdict JSON is still written. Exit code is 1 (fail), not 64 (refused)."""
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"
            # Port 1 is reserved and unbound -- the fastest real refusal on this machine.
            exit_code = p.main(["--base-url", "http://127.0.0.1:1",
                                "--out-json", str(out),
                                "--event-timeout", "1", "--search-budget-sec", "1"])
            self.assertEqual(exit_code, 1)
            self.assertTrue(out.exists())
            with open(out, "r", encoding="utf-8") as f:
                verdict = json.load(f)
            self.assertFalse(verdict["pass"])
            run_case = [r for r in verdict["results"] if r["case"] == "run"]
            self.assertEqual(len(run_case), 1)
            self.assertIn("REFUSED", run_case[0]["detail"])

    def test_refusal_with_json_emits_json_on_stdout(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"
            proc = subprocess.run(
                [sys.executable, str(SCRIPT), "--base-url", "not-a-url",
                 "--out-json", str(out), "--json"],
                capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(proc.returncode, p.EXIT_REFUSED)
            data = json.loads(proc.stdout)
            self.assertEqual(data["verdict"], "REFUSED")
            self.assertIn("reason", data)
            self.assertIn("detail", data)


# ---------------------------------------------------------------------------
# Verdict JSON shape (mocked server)
# ---------------------------------------------------------------------------

class TestVerdictShape(unittest.TestCase):
    """The verdict JSON shape is pinned: top-level at, baseUrl, targetPtr, pass, results;
    per-result exactly case (string), ok (bool), detail (string)."""

    def test_top_level_keys(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertIn("at", verdict)
        self.assertIn("baseUrl", verdict)
        self.assertIn("targetPtr", verdict)
        self.assertIn("pass", verdict)
        self.assertIn("results", verdict)

    def test_at_is_iso8601_with_offset(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        from datetime import datetime
        dt = datetime.fromisoformat(verdict["at"])
        self.assertIsNotNone(dt.tzinfo)

    def test_baseUrl_is_string(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertIsInstance(verdict["baseUrl"], str)
        self.assertTrue(verdict["baseUrl"].startswith("http"))

    def test_targetPtr_is_string(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertEqual(verdict["targetPtr"], "2897AD9EC80")

    def test_pass_is_bool(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertIsInstance(verdict["pass"], bool)

    def test_results_is_array(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertIsInstance(verdict["results"], list)
        self.assertGreater(len(verdict["results"]), 0)

    def test_each_result_has_exactly_case_ok_detail(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        for r in verdict["results"]:
            self.assertEqual(set(r.keys()), {"case", "ok", "detail"})
            self.assertIsInstance(r["case"], str)
            self.assertIsInstance(r["ok"], bool)
            self.assertIsInstance(r["detail"], str)

    def test_skip_case_is_ok_false(self) -> None:
        """The no-TargetPtr path must be ok=false, NOT ok=true."""
        verdict = _passing_run("")
        organic = [r for r in verdict["results"] if r["case"] == "organic-paths"]
        self.assertEqual(len(organic), 1)
        self.assertFalse(organic[0]["ok"])
        self.assertIn("SKIPPED", organic[0]["detail"])

    def test_all_pass_gives_pass_true(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertTrue(verdict["pass"])

    def test_output_is_utf8_no_bom(self) -> None:
        """The PowerShell Set-Content -Encoding utf8 wrote a BOM under PS5.1."""
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"

            comprehensive_events = [
                {"id": 200, "kind": "debug.fx.shown", "payload": {"cueId": "debug.probe", "amount": 0}},
                {"id": 201, "kind": "debug.fx.skipped", "payload": {"reason": "rate-limited"}},
                {"id": 202, "kind": "debug.fx.skipped", "payload": {"reason": "muted"}},
                {"id": 203, "kind": "debug.fx.skipped", "payload": {"reason": "disabled"}},
                {"id": 204, "kind": "debug.fx.state.started", "payload": {"statusId": "pact_mark"}},
                {"id": 205, "kind": "debug.fx.list", "payload": {"cues": ["combat.hit", "debug.probe"]}},
            ]

            def fake_get_events(url, after_id, limit, timeout=10):
                return comprehensive_events

            def fake_max_event_id(url, timeout=10, budget_sec=30.0):
                return 100

            def fake_invoke_post(url, path, body=None, timeout=8):
                return {}

            def fake_run_case(base_url, play, after_id):
                return {"case": play["name"], "ok": True, "detail": "mocked pass"}

            def fake_wait_match(url, after_id, match_fn, timeout_ms=5000,
                                poll_interval=0.25, event_timeout=10):
                return {"cueId": "debug.probe", "amount": 0, "rgb": "#FFFFFF",
                        "hybrid": True, "primitives": ["burst"], "reason": None,
                        "statusId": "wither"}

            def fake_apply_status(*args, **kwargs):
                return True

            with mock.patch.object(p.lib, "get_events", side_effect=fake_get_events), \
                 mock.patch.object(p.lib, "get_debug_max_event_id", side_effect=fake_max_event_id), \
                 mock.patch.object(p.lib, "invoke_debug_post", side_effect=fake_invoke_post), \
                 mock.patch.object(p, "run_case", side_effect=fake_run_case), \
                 mock.patch.object(p, "wait_for_fx_match", side_effect=fake_wait_match), \
                 mock.patch.object(p, "_apply_status_until_started", side_effect=fake_apply_status), \
                 mock.patch.object(p, "get_fx_events", side_effect=fake_get_events), \
                 mock.patch.object(p, "set_cheat", return_value=None), \
                 mock.patch("time.sleep", return_value=None):
                p.run(base_url="http://127.0.0.1:5088", target_ptr="",
                      col=4, row=2, out_json=out)

            raw = out.read_bytes()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"),
                             "output must not start with a UTF-8 BOM")


# ---------------------------------------------------------------------------
# Exit codes
# ---------------------------------------------------------------------------

class TestExitCodes(unittest.TestCase):
    def test_pass_returns_0(self) -> None:
        verdict = _passing_run("2897AD9EC80")
        self.assertEqual(verdict["_exit_code"], 0)

    def test_fail_returns_1(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"

            def fake_get_events(url, after_id, limit, timeout=10):
                return []

            def fake_max_event_id(url, timeout=10, budget_sec=30.0):
                return 100

            def fake_invoke_post(url, path, body=None, timeout=8):
                return {}

            def fake_wait_match(url, after_id, match_fn, timeout_ms=5000,
                                poll_interval=0.25, event_timeout=10):
                return None  # no event ever matches -> every case fails

            def fake_apply_status(*args, **kwargs):
                return True

            with mock.patch.object(p.lib, "get_events", side_effect=fake_get_events), \
                 mock.patch.object(p.lib, "get_debug_max_event_id", side_effect=fake_max_event_id), \
                 mock.patch.object(p.lib, "invoke_debug_post", side_effect=fake_invoke_post), \
                 mock.patch.object(p, "wait_for_fx_match", side_effect=fake_wait_match), \
                 mock.patch.object(p, "_apply_status_until_started", side_effect=fake_apply_status), \
                 mock.patch.object(p, "get_fx_events", side_effect=fake_get_events), \
                 mock.patch.object(p, "set_cheat", return_value=None), \
                 mock.patch("time.sleep", return_value=None):
                exit_code = p.run(
                    base_url="http://127.0.0.1:5088",
                    target_ptr="",
                    col=4, row=2, out_json=out,
                )
            self.assertEqual(exit_code, 1)

    def test_refusal_returns_64(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "verdict.json"
            exit_code = p.main(["--base-url", "not-a-url", "--out-json", str(out)])
            self.assertEqual(exit_code, 64)


# ---------------------------------------------------------------------------
# Docstring
# ---------------------------------------------------------------------------

class TestDocstring(unittest.TestCase):
    def test_module_docstring_states_why_retired(self) -> None:
        self.assertIsNotNone(p.__doc__)
        self.assertIn("POWERSHELL", p.__doc__.upper())
        self.assertIn("prove-vfx.ps1", p.__doc__)


if __name__ == "__main__":
    unittest.main()
