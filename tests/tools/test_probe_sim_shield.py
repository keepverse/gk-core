"""Contract tests for `gk-core/scripts/probe_sim_shield.py`.

Asserts the contract, and the four properties the tool exists to guarantee -- all of which are
properties of what the ORIGINAL got wrong, and each of which the differential could not see (it drives
real servers, where the two implementations agree on a well-behaved one).

  * the probe CAN FAIL. The original's comment promised "expect shieldAbsorbed 50, hp 300 -> 270" and
    nothing checked it; on a server that grants no shield its pipelines printed nothing and it exited 0.
  * `shieldAbsorbed` is ABSENT when nothing was absorbed, because `SimEngine` adds the key only
    `if (applied.AbsorbedAmount > 0)`. "Absorbed zero" and "the pipeline did not run" are the same JSON,
    so the probe has to require the event first and then the key.
  * EVERY request is bounded. `Invoke-RestMethod` without `-TimeoutSec` waits on the server's own idea
    of forever.
  * the port is CONFIGURATION. The original hardcoded `http://127.0.0.1:5088`, the OWNER's server, so a
    probe aimed at a pooled slot had no way to say so without editing the file.

The transport is substituted through the tool's PRIVATE `_urlopen` seam, for the reason documented in
`test_dump_melon_p0.py`: patching the process-wide `urllib` module reaches every other test in the
process, and a patch that outlives its `with` block breaks them while this suite stays green.
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
SCRIPT = Path(os.environ.get("PROBE_SIM_SHIELD_SCRIPT", REPO / "scripts" / "probe_sim_shield.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_probe_sim_shield.py"
RUN_TIMEOUT = 120

_PRISTINE_URLOPEN = None

_spec = importlib.util.spec_from_file_location("probe_sim_shield", SCRIPT)
pss = importlib.util.module_from_spec(_spec)
sys.modules["probe_sim_shield"] = pss
_spec.loader.exec_module(pss)
_PRISTINE_URLOPEN = pss._urlopen

REFUSAL_REASONS = {"BASE-URL-UNSET", "INVALID-TIMEOUT", "REQUEST-FAILED", "RESPONSE-NOT-JSON",
                   "RESPONSE-NOT-OBJECT", "SERVER-UNREACHABLE"}


class Reply:
    """A scripted `urllib` answer. `payload` of `None` means "not JSON at all"."""

    def __init__(self, payload=None, raw: bytes | None = None) -> None:
        self.payload = payload
        self.raw = raw

    def __call__(self, url, body=None, timeout=None):
        return self.payload


def evt(kind: str, payload: dict) -> dict:
    return {"kind": kind, "payload": payload}


GRANTED = evt("shield.granted", {"ptr": "P1", "outcome": "Applied", "shieldId": "s-1",
                                 "hp": 50, "maxHp": 50})
DAMAGED = evt("plant.damage", {"ptr": "P1", "damage": 80, "before": 80, "after": 80,
                               "shieldAbsorbed": 50})
HEALTHY_STATE = {"ok": True, "plants": [{"ptr": "P1", "hp": 270, "maxHp": 300}],
                 "shields": [{"ptr": "P1", "hp": 0, "maxHp": 50}]}


class FakeResponse:
    """What `_urlopen` must return: a context manager yielding an object with `read()`.

    Returning a bare dict satisfies neither. The first version of this harness did, and every case that
    drove the tool raised `AttributeError: __enter__` -- a harness that cannot call the thing it is
    testing, which reads exactly like a tool that cannot be called.
    """

    def __init__(self, payload: dict | None, raw: bytes | None) -> None:
        self._raw = raw if raw is not None else json.dumps(payload or {}).encode("utf-8")

    def read(self) -> bytes:
        return self._raw

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def scripted(replies: dict[str, object]):
    """A `_urlopen` that answers by PATH and records every call, so the case can assert both the
    requests made and the timeout each one carried."""
    calls: list[dict] = []

    def open_url(request, body=None, timeout=None):
        # The tool hands `_urlopen` a `urllib.request.Request`, which the real `urlopen` accepts
        # alongside a bare string. The stub accepts both, because a stub that accepts only one of the
        # two forms the real function accepts is testing a stricter contract than the tool has.
        url = getattr(request, "full_url", request)
        for suffix, reply in replies.items():
            if url.endswith(suffix):
                calls.append({"url": url, "body": body, "timeout": timeout})
                if isinstance(reply, Exception):
                    raise reply
                # A case may supply a ready-made `FakeResponse` (to hand back bytes rather than a
                # payload), or a payload dict to be encoded. Both are legitimate; neither is wrapped
                # twice.
                return reply if isinstance(reply, FakeResponse) else FakeResponse(reply, None)
        calls.append({"url": url, "body": body, "timeout": timeout})
        return FakeResponse(None, None)

    return open_url, calls


class SeamGuard(unittest.TestCase):
    """Every case leaves the tool's private seam as it found it.

    A stub that outlives its `with` block is invisible from the case that opened it, and the damage
    lands on whatever runs next in a file that has nothing to do with the tool. That is how this
    repository's first `test_dump_melon_p0.py` produced 506 unrelated failures in
    `test_ps1_port_census.py`. `tearDown` runs after EVERY case, so the failure is attributed to the
    case that actually leaked."""

    def tearDown(self) -> None:
        if pss._urlopen is not _PRISTINE_URLOPEN:
            self.fail(f"_urlopen was still substituted after {self.id()}: {pss._urlopen!r}")


class TheBaseUrl(SeamGuard):
    """A port is configuration, never a constant. The original hardcoded the owner's 5088."""

    def test_the_FALLBACK_names_the_OWNERS_server_and_says_so(self) -> None:
        self.assertEqual(pss.FALLBACK_BASE_URL, "http://127.0.0.1:5088")
        self.assertIn("owner", (pss.resolve_base_url.__doc__ or "").lower(),
                      "the fallback must be labelled as the OWNER's server, not as the server")

    def test_the_FLAG_wins(self) -> None:
        self.assertEqual(pss.resolve_base_url("http://127.0.0.1:5101"), "http://127.0.0.1:5101")

    def test_a_TRAILING_slash_is_TRIMMED(self) -> None:
        self.assertEqual(pss.resolve_base_url("http://127.0.0.1:5102/"), "http://127.0.0.1:5102")
        self.assertEqual(pss.resolve_base_url("http://127.0.0.1:5102///"), "http://127.0.0.1:5102")

    def test_the_TOOL_SPECIFIC_variable_is_read(self) -> None:
        with mock.patch.dict(os.environ, {"FUSIONRPG_SIM_PROBE_URL": "http://127.0.0.1:5103"},
                             clear=False):
            self.assertEqual(pss.resolve_base_url(""), "http://127.0.0.1:5103")

    def test_FUSIONRPG_URLS_is_read_because_that_is_what_the_SERVER_reads(self) -> None:
        """A mismatch between the port a server bound and the port a probe dialled is the exact failure
        this repository's port rule exists to prevent, so the probe reads the same variable the server
        does rather than a second spelling of it."""
        with mock.patch.dict(os.environ, {"FUSIONRPG_URLS": "http://127.0.0.1:5101"}, clear=False):
            with mock.patch.dict(os.environ, {"FUSIONRPG_SIM_PROBE_URL": ""}, clear=False):
                self.assertEqual(pss.resolve_base_url(""), "http://127.0.0.1:5101")

    def test_the_FLAG_beats_BOTH_environment_variables(self) -> None:
        with mock.patch.dict(os.environ, {"FUSIONRPG_SIM_PROBE_URL": "http://127.0.0.1:5103",
                                          "FUSIONRPG_URLS": "http://127.0.0.1:5101"}, clear=False):
            self.assertEqual(pss.resolve_base_url("http://127.0.0.1:5099"), "http://127.0.0.1:5099")


class TheProbeCanFail(SeamGuard):
    """The headline defect. Each case is a server that MISBEHAVES, and the port must not pass it."""

    def drive(self, replies: dict) -> tuple[int, dict | None]:
        opener, _ = scripted(replies)
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                code = pss.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        try:
            return code, json.loads(out.getvalue())
        except json.JSONDecodeError:
            return code, None

    def healthy(self) -> dict:
        return {pss.SHIELD_GRANT: {"ok": True, "events": [GRANTED]},
                pss.PLANT_DAMAGE: {"ok": True, "events": [DAMAGED]},
                pss.SIM_STATE: HEALTHY_STATE}

    def test_a_server_that_BEHAVES_passes_and_reports_every_expectation(self) -> None:
        code, payload = self.drive(self.healthy())
        self.assertEqual(code, pss.EXIT_OK)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["failedChecks"], [])
        self.assertGreaterEqual(len(payload["checks"]), 6)
        for c in payload["checks"]:
            self.assertTrue(c["ok"], c["name"])

    def test_a_server_that_GRANTS_NO_SHIELD_is_a_FAILURE_naming_the_expectation(self) -> None:
        """The case the ORIGINAL passes. Its `Where-Object kind -eq 'shield.granted'` pipeline yields
        nothing at all, prints nothing, and the script exits 0."""
        replies = self.healthy()
        replies[pss.SHIELD_GRANT] = {"ok": True, "events": []}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-shield-grant-emitted-a-shield.granted-event", payload["failedChecks"])

    def test_a_shield_of_the_WRONG_SIZE_is_a_FAILURE(self) -> None:
        replies = self.healthy()
        replies[pss.SHIELD_GRANT] = {"ok": True, "events": [
            evt("shield.granted", {"ptr": "P1", "hp": 10, "maxHp": 10})]}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-granted-shield-holds-the-amount-requested", payload["failedChecks"])

    def test_an_ABSENT_shieldAbsorbed_is_NOT_read_as_ZERO(self) -> None:
        """`SimEngine` adds `shieldAbsorbed` only `if (applied.AbsorbedAmount > 0)`, so an absent key
        means "the pipeline did not run" -- and reading it as 0 would let a broken pipeline pass."""
        replies = self.healthy()
        replies[pss.PLANT_DAMAGE] = {"ok": True, "events": [
            evt("plant.damage", {"ptr": "P1", "damage": 80, "before": 80, "after": 80})]}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-shield-absorbed-the-whole-amount", payload["failedChecks"])
        absorb = next(c for c in payload["checks"]
                      if c["name"] == "the-shield-absorbed-the-whole-amount")
        self.assertIn("None", absorb["actual"], "an absent key must be reported as absent, not as 0")

    def test_a_damage_step_that_EMITTED_NOTHING_is_a_FAILURE(self) -> None:
        """`DamagePlant` adds the event only `if (stats.LogDamage)`, so the whole step can vanish."""
        replies = self.healthy()
        replies[pss.PLANT_DAMAGE] = {"ok": True, "events": []}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-damage-emitted-a-plant.damage-event", payload["failedChecks"])

    def test_a_PLANT_at_the_WRONG_HP_is_a_FAILURE(self) -> None:
        replies = self.healthy()
        replies[pss.SIM_STATE] = {"ok": True, "plants": [{"ptr": "P1", "hp": 220, "maxHp": 300}],
                                 "shields": [{"ptr": "P1", "hp": 50, "maxHp": 50}]}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-plant-survived-at-the-predictable-hp", payload["failedChecks"])

    def test_a_MISSING_plant_in_state_is_a_FAILURE_not_a_SKIP(self) -> None:
        replies = self.healthy()
        replies[pss.SIM_STATE] = {"ok": True, "plants": [], "shields": [{"ptr": "P1"}]}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-state-reports-the-probed-plant", payload["failedChecks"])

    def test_a_PARTIAL_absorb_is_a_failure(self) -> None:
        """A shield that absorbed 30 of 50 is not a working shield. Found by falsification: the case
        for an ABSENT key existed and passed, so a check rewritten as `bool(payload.get(...))` -- which
        accepts any non-zero amount -- satisfied the whole suite."""
        replies = self.healthy()
        replies[pss.PLANT_DAMAGE] = {"ok": True, "events": [
            evt("plant.damage", {"ptr": "P1", "damage": 80, "before": 80, "after": 80,
                                 "shieldAbsorbed": 30})]}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-shield-absorbed-the-whole-amount", payload["failedChecks"])

    def test_an_EMPTY_shield_table_is_a_FAILURE(self) -> None:
        """Found by falsification: the shield-table check was never driven with an empty table, so a
        check rewritten to `True` was unkillable."""
        replies = self.healthy()
        replies[pss.SIM_STATE] = {"ok": True, "plants": [{"ptr": "P1", "hp": 270, "maxHp": 300}],
                                 "shields": []}
        code, payload = self.drive(replies)
        self.assertEqual(code, pss.EXIT_FAILED)
        self.assertIn("the-state-carries-a-shield-table", payload["failedChecks"])

    def test_it_CANNOT_pass_by_checking_NOTHING(self) -> None:
        """A counterweight. `Report.ok` requires a non-empty check list, so a tool that recorded no
        expectations cannot report OK -- otherwise every 'skip the check when unsure' shortcut reads
        green."""
        self.assertFalse(pss.Report().ok)
        empty = pss.Report(base_url="http://127.0.0.1:5101")
        self.assertFalse(empty.ok, "a report with zero checks reported OK")

    def test_EVERY_expectation_is_NAMED_and_the_names_are_STABLE(self) -> None:
        """Names are the tool's output contract: a caller greps them. Asserted as a set, not a count,
        so adding an expectation does not break the case and dropping one does."""
        expected = {
            "the-shield-grant-emitted-a-shield.granted-event",
            "the-granted-shield-holds-the-amount-requested",
            "the-damage-emitted-a-plant.damage-event",
            "the-shield-absorbed-the-whole-amount",
            "the-state-reports-the-probed-plant",
            "the-plant-survived-at-the-predictable-hp",
            "the-state-carries-a-shield-table",
        }
        code, payload = self.drive(self.healthy())
        self.assertEqual(code, pss.EXIT_OK)
        self.assertEqual({c["name"] for c in payload["checks"]}, expected)

    def test_the_EXPECTED_HP_is_DERIVED_not_hardcoded(self) -> None:
        """`300 - (80 - 50)`. Asserted as arithmetic so editing one constant cannot leave the
        expectation silently describing the old numbers."""
        self.assertEqual(pss.EXPECTED_HP_AFTER, pss.PLANT_HP - (pss.DAMAGE - pss.SHIELD_AMOUNT))
        self.assertEqual(pss.EXPECTED_HP_AFTER, 270)


class TheTransport(SeamGuard):
    def test_EVERY_request_carries_a_TIMEOUT(self) -> None:
        replies = {pss.SHIELD_GRANT: {"ok": True, "events": [GRANTED]},
                   pss.PLANT_DAMAGE: {"ok": True, "events": [DAMAGED]},
                   pss.SIM_STATE: HEALTHY_STATE}
        opener, calls = scripted(replies)
        with mock.patch.object(pss, "_urlopen", opener):
            pss.main(["--base-url", "http://127.0.0.1:5101", "--json", "--timeout", "9"])
        self.assertEqual(len(calls), 5, f"expected five requests, made {len(calls)}")
        for call in calls:
            self.assertEqual(call["timeout"], 9, f"unbounded request: {call['url']}")

    def test_an_UNREACHABLE_server_REFUSES_BY_NAME(self) -> None:
        import urllib.error

        opener, _ = scripted({"": urllib.error.URLError("connection refused")})
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                code = pss.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, pss.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "SERVER-UNREACHABLE")

    def test_a_NON_POSITIVE_timeout_is_REFUSED_before_any_request(self) -> None:
        opener, calls = scripted({})
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                code = pss.main(["--base-url", "http://127.0.0.1:5101", "--timeout", "0", "--json"])
        self.assertEqual(code, pss.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")
        self.assertEqual(calls, [], "a refused run still made a request")

    def test_an_HTTP_error_REFUSES_rather_than_being_IGNORED(self) -> None:
        """Found by falsification: an `HTTPError` handler rewritten to return a payload satisfied the
        whole suite, because no case drove one. A 500 from a server is a refusal, not a result -- and
        returning `{"ok": false}` would let a broken server look like a clean run with no events."""
        import urllib.error

        opener, _ = scripted({"": urllib.error.HTTPError("u", 500, "boom", None, None)})
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                code = pss.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, pss.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "REQUEST-FAILED")

    def test_a_BODY_that_is_not_JSON_REFUSES_rather_than_being_accepted(self) -> None:
        """Found by falsification: the non-JSON handler rewritten to return `{}` satisfied the suite. An
        HTML error page from a proxy is the single most common way a probe silently reports nothing."""
        opener, _ = scripted({"": FakeResponse(None, b"<html>not json</html>")})
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                code = pss.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, pss.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "RESPONSE-NOT-JSON")

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - REFUSAL_REASONS)}")
        self.assertEqual(pss.REFUSAL_REASONS, REFUSAL_REASONS,
                         "the declared set and the constant disagree")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({pss.EXIT_OK, pss.EXIT_FAILED, pss.EXIT_REFUSED}, {0, 1, 64})


class TheEnvelopeAndSurface(SeamGuard):
    def test_the_JSON_keys_are_the_CONTRACT(self) -> None:
        opener, _ = scripted({pss.SHIELD_GRANT: {"ok": True, "events": [GRANTED]},
                              pss.PLANT_DAMAGE: {"ok": True, "events": [DAMAGED]},
                              pss.SIM_STATE: HEALTHY_STATE})
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(pss, "_urlopen", opener):
            with redirect_stdout(out), redirect_stderr(err):
                pss.main(["--base-url", "http://127.0.0.1:5101", "--json"])
        payload = json.loads(out.getvalue())
        self.assertEqual(set(payload), {"tool", "verdict", "exitCode", "baseUrl", "checks",
                                        "failedChecks", "shieldEvent", "damageEvent", "state"})
        self.assertEqual(set(payload["checks"][0]), {"name", "ok", "expected", "actual"})
        for banned in ("pid", "durationMs", "started", "timestamp"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run")

    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-BaseURL"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_EVERY_URL_call_carries_a_timeout_by_AST(self) -> None:
        """Structural. `Invoke-RestMethod` with no `-TimeoutSec` was the original's shape, and a
        code-reading claim about it is exactly what a case should not rely on."""
        tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
        sites = [n for n in ast.walk(tree)
                 if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
                 and n.func.id == "_urlopen"]
        self.assertEqual(len(sites), 1, f"expected one call site, found {len(sites)}")
        self.assertFalse(any(isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                             and n.func.attr == "urlopen" and isinstance(n.func.value, ast.Name)
                             and n.func.value.id == "urllib" for n in ast.walk(tree)),
                         "a direct urllib.urlopen bypasses the seam the suite substitutes")

    def test_the_PRIVATE_seam_is_BOUND_to_urllib(self) -> None:
        self.assertIs(pss._urlopen, _PRISTINE_URLOPEN)
        self.assertEqual(getattr(pss._urlopen, "__module__", None), "urllib.request")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        """The defect this suite's sibling had: `dm.subprocess` IS the process-wide `subprocess`. A
        patch of a global module reaches every other test in the process."""
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "urllib", "json",
                          "importlib", "ast", "re"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute)
                    and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "pss":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in global_modules:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_NO_test_METHOD_STARTS_a_patch_it_cannot_STOP(self) -> None:
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

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("probe-sim-shield.ps1", head)
        lowered = head.lower()
        for reason in ("could not fail", "no timeout", "was a constant", "absent"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")


if __name__ == "__main__":
    unittest.main()
