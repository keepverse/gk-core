"""Contract tests for `gk-core/scripts/seed_aptitude_sheet_review.py`.

A SEEDER, so the contract is about the six requests it makes and the shape of the one it writes.

THE PORT IS THE POINT. The original hardcoded `http://127.0.0.1:5088` -- the OWNER's server -- so a
seeder pointed at a pooled slot on 5101/5102/5103 wrote its review preset into somebody else's
server and reported success. The URL now comes from `lib.resolve_base_url` and the resolved value is
printed, so a run says which server it seeded.

EVERY REQUEST IS BOUNDED. The original called `Invoke-RestMethod` six times with no `-TimeoutSec`
anywhere in the file. Each request now carries a timeout, and a refusal names the endpoint.

THE PRESET SHAPE IS THE DELIVERABLE. Twelve rows, the first four at per-mille 84 and the rest at 83,
name "Review Even", kind "player" -- the original's `83 + $(if ($i -lt 4) { 1 } else { 0 })`. A port
that flattened the two tiers would seed a sheet that does not show what the review exists to show.

The transport is substituted through the tool's PRIVATE `_URLOPEN` seam, for the reason documented in
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
SCRIPT = Path(os.environ.get("SEED_APTITUDE_SHEET_REVIEW_SCRIPT",
                             REPO / "scripts" / "seed_aptitude_sheet_review.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_seed_aptitude_sheet_review.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("seed_aptitude_sheet_review", SCRIPT)
sasr = importlib.util.module_from_spec(_spec)
sys.modules["seed_aptitude_sheet_review"] = sasr
_spec.loader.exec_module(sasr)
_PRISTINE = {"_URLOPEN": sasr._URLOPEN}


class SeamGuard(unittest.TestCase):
    def tearDown(self) -> None:
        if sasr._URLOPEN is not _PRISTINE["_URLOPEN"]:
            self.fail(f"_URLOPEN was still substituted after {self.id()}: {sasr._URLOPEN!r}")


class Scripted:
    """A scripted `urllib` answer keyed by URL substring. `fault` names a URL substring that must
    raise instead of answering; `raw` names one that must answer with bytes that are not JSON."""

    def __init__(self, answers: dict[str, dict], fault: str | None = None,
                 raw: str | None = None) -> None:
        self.answers = answers
        self.fault = fault
        self.raw = raw
        self.calls: list[tuple[str, dict | None, int]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url if hasattr(request, "full_url") else str(request)
        body = json.loads(request.data.decode("utf-8")) if request.data else None
        self.calls.append((url, body, timeout))
        if self.fault and self.fault in url:
            raise OSError(f"connection refused by the harness: {url}")
        if self.raw and self.raw in url:
            return sasr._Response(None, raw=b"<html>not json</html>")
        for needle, payload in self.answers.items():
            if needle in url:
                return sasr._Response(payload)
        raise AssertionError(f"unscripted URL: {url}")


def happy_answers(instance_id: str = "derived-audit") -> dict[str, dict]:
    return {
        "/health": {"ok": True, "currentPlayerId": "p-1", "injectorConnected": True},
        "/api/players": {"currentPlayerId": "p-1"},
        "/api/debug/derived-audit-actor": {"instanceId": instance_id},
        "/api/aptitudes/presets": {"presetId": "preset-9", "name": "Review Even"},
        "/api/aptitudes/unique/": {"budget": 1000, "spent": 400, "leftover": 600},
        "/api/aptitudes/p-1": {"budget": 500, "spent": 100},
    }


class TheHappyRun(SeamGuard):
    def setUp(self) -> None:
        self.scripted = Scripted(happy_answers())
        self.err = io.StringIO()
        self.out = io.StringIO()
        self.addCleanup(self._restore_env, os.environ.get("FUSIONRPG_SERVER_URL"))

    @staticmethod
    def _restore_env(saved):
        if saved is None:
            os.environ.pop("FUSIONRPG_SERVER_URL", None)
        else:
            os.environ["FUSIONRPG_SERVER_URL"] = saved

    def _run(self, argv: list[str]):
        with mock.patch.object(sasr, "_URLOPEN", self.scripted):
            with redirect_stderr(self.err), redirect_stdout(self.out):
                code = sasr.main(argv)
        return code

    def test_a_clean_run_exits_0_and_reports_OK(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        code = self._run(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sasr.EXIT_OK)
        payload = json.loads(self.out.getvalue())
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["baseUrl"], "http://127.0.0.1:5101")
        self.assertEqual(payload["baseUrlSource"], "explicit")
        self.assertEqual(payload["instanceId"], "derived-audit")
        self.assertEqual(payload["presetId"], "preset-9")

    def test_the_BASE_URL_comes_from_the_ENVIRONMENT_when_no_argument_is_given(self) -> None:
        os.environ["FUSIONRPG_SERVER_URL"] = "http://127.0.0.1:5102"
        code = self._run(["--json"])
        self.assertEqual(code, sasr.EXIT_OK)
        payload = json.loads(self.out.getvalue())
        self.assertEqual(payload["baseUrl"], "http://127.0.0.1:5102")
        self.assertEqual(payload["baseUrlSource"], "$FUSIONRPG_SERVER_URL")

    def test_the_INSTANCE_ID_falls_back_to_derived_audit_when_the_seed_omits_it(self) -> None:
        answers = happy_answers()
        answers["/api/debug/derived-audit-actor"] = {}
        self.scripted = Scripted(answers)
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        code = self._run(["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sasr.EXIT_OK)
        self.assertEqual(json.loads(self.out.getvalue())["instanceId"], "derived-audit")

    def test_the_SIX_requests_are_made_in_order_with_the_original_bodies(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        self._run(["--base-url", "http://127.0.0.1:5101", "--json"])
        paths = [url.split("127.0.0.1:5101", 1)[1] for url, _, _ in self.scripted.calls]
        self.assertEqual(paths, ["/health", "/api/debug/derived-audit-actor", "/api/players",
                                 "/api/aptitudes/presets", "/api/aptitudes/unique/derived-audit",
                                 "/api/aptitudes/p-1"])
        bodies = {url.split("127.0.0.1:5101", 1)[1]: body for url, body, _ in self.scripted.calls}
        self.assertIsNone(bodies["/health"])
        self.assertEqual(bodies["/api/debug/derived-audit-actor"], {"playerId": "p-1"})
        self.assertEqual(bodies["/api/aptitudes/presets"]["name"], "Review Even")
        self.assertEqual(bodies["/api/aptitudes/presets"]["kind"], "player")
        self.assertEqual(bodies["/api/aptitudes/presets"]["playerId"], "p-1")

    def test_every_request_carries_a_TIMEOUT(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        self._run(["--base-url", "http://127.0.0.1:5101", "--timeout", "7"])
        self.assertTrue(self.scripted.calls, "no requests were made")
        for _, _, timeout in self.scripted.calls:
            self.assertEqual(timeout, 7, "an unbounded request would wait on the server's idea of "
                                          "forever")

    def test_the_OPERATOR_output_carries_the_review_URLS(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        self._run(["--base-url", "http://127.0.0.1:5101"])
        text = self.err.getvalue()
        self.assertIn("=== aptitude-sheet review seed ===", text)
        self.assertIn("http://127.0.0.1:5101/#/actor-ladder-demo?sel=derived-audit", text)
        self.assertIn("window.__fusionRpgAptitudeObs", text)
        self.assertIn("health ok=True playerId=p-1 injector=True", text)
        self.assertIn("preset saved presetId=preset-9 name=Review Even", text)
        self.assertIn("unique budget=1000 spent=400 leftover=600", text)
        self.assertIn("commander budget=500 spent=100", text)


class ThePresetShape(SeamGuard):
    """Twelve rows, the first four at per-mille 84 and the rest at 83 -- the original's
    `83 + $(if ($i -lt 4) { 1 } else { 0 })`. A port that flattened the two tiers would seed a sheet
    that does not show what the review exists to show."""

    def test_the_rows_are_the_twelve_aptitudes_in_the_original_order(self) -> None:
        rows = sasr.aptitude_rows()
        self.assertEqual([r["aptitudeId"] for r in rows], list(sasr.APTITUDE_IDS))
        self.assertEqual(len(rows), 12)

    def test_the_first_four_are_at_84_and_the_rest_at_83(self) -> None:
        rows = sasr.aptitude_rows()
        permilles = [r["targetPermille"] for r in rows]
        self.assertEqual(permilles[:4], [84, 84, 84, 84])
        self.assertEqual(permilles[4:], [83] * 8)

    def test_the_rows_carry_exactly_two_fields(self) -> None:
        for row in sasr.aptitude_rows():
            self.assertEqual(sorted(row), ["aptitudeId", "targetPermille"])


class TheRefusals(SeamGuard):
    def setUp(self) -> None:
        self.err = io.StringIO()
        self.out = io.StringIO()

    def _run(self, scripted: Scripted, argv: list[str]):
        with mock.patch.object(sasr, "_URLOPEN", scripted):
            with redirect_stderr(self.err), redirect_stdout(self.out):
                code = sasr.main(argv)
        return code

    def test_a_SERVER_that_never_answers_is_a_NAMED_refusal(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        scripted = Scripted(happy_answers(), fault="/health")
        code = self._run(scripted, ["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sasr.EXIT_REFUSED)
        payload = json.loads(self.out.getvalue())
        self.assertEqual(payload["reason"], "REQUEST-FAILED")
        self.assertIn("/health", payload["detail"])
        self.assertEqual(payload["exitCode"], sasr.EXIT_REFUSED)

    def test_a_MALFORMED_answer_is_a_NAMED_refusal(self) -> None:
        """The original's `$ErrorActionPreference = "Stop"` turned a non-JSON body into a bare exit 1
        with no word about which endpoint. The refusal names the endpoint and the stage."""
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        scripted = Scripted(happy_answers(), raw="/api/players")
        code = self._run(scripted, ["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sasr.EXIT_REFUSED)
        payload = json.loads(self.out.getvalue())
        self.assertEqual(payload["reason"], "RESPONSE-NOT-JSON")
        self.assertIn("/api/players", payload["detail"])

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_work(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        code = self._run(Scripted(happy_answers()), ["--base-url", "http://127.0.0.1:5101",
                                                     "--timeout", "0", "--json"])
        self.assertEqual(code, sasr.EXIT_REFUSED)
        self.assertEqual(json.loads(self.out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_REFUSAL_reports_the_RESPONSES_it_had_already_read(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        scripted = Scripted(happy_answers(), fault="/api/aptitudes/presets")
        code = self._run(scripted, ["--base-url", "http://127.0.0.1:5101", "--json"])
        self.assertEqual(code, sasr.EXIT_REFUSED)
        payload = json.loads(self.out.getvalue())
        self.assertEqual(payload["reason"], "REQUEST-FAILED")
        self.assertTrue(payload["responses"], "a refusal that discards what it already read hides "
                                               "the stage that succeeded")
        self.assertEqual(payload["responses"][-1]["path"], "/api/players")

    def test_a_REFUSAL_exits_64_and_SAYS_so_on_stderr(self) -> None:
        os.environ.pop("FUSIONRPG_SERVER_URL", None)
        scripted = Scripted(happy_answers(), fault="/health")
        with redirect_stderr(self.err), redirect_stdout(self.out):
            code = sasr.main(["--base-url", "http://127.0.0.1:5101"])
        self.assertEqual(code, sasr.EXIT_REFUSED)
        self.assertIn("REFUSED", self.err.getvalue())


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - sasr.REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - sasr.REFUSAL_REASONS)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual(sasr.EXIT_OK, 0)
        self.assertEqual(sasr.EXIT_REFUSED, 64)

    def test_it_uses_the_shared_resolve_base_url_and_NOT_a_hardcoded_port(self) -> None:
        """The original's defect was `param([string]$BaseUrl = "http://127.0.0.1:5088")`. The port
        reads the URL through the shared resolver, so a pooled slot is configuration, not a constant."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("lib.resolve_base_url", source)
        tree = ast.parse(source)
        defaults = [ast.unparse(n) for node in ast.walk(tree)
                    if isinstance(node, ast.Call) and getattr(node.func, "id", "") == "add_argument"
                    for n in node.defaults if isinstance(n, ast.Constant)]
        for default in defaults:
            self.assertNotIn("5088", default, f"a flag still defaults to the owner's port: {default}")

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-Timeout", "-Json"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("seed-aptitude-sheet-review.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "5088", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--base-url", "--timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                        "re", "socket", "urllib"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "sasr":
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
