"""Contract tests for `gk-core/scripts/gate_class_system_phase9.py`.

The headline defect is a SILENT GREEN, and the differential could not have seen it: on a real tree and a
well-formed fixture the two implementations agree, so what differs is entirely the malformed-input
behaviour, which needs its own cases.

  * `Get-Content -Raw | ConvertFrom-Json` then `$census.families_without_reader -gt 0`. In PowerShell a
    missing property is `$null`, `$null -gt 0` is `$false`, and the gate printed **READY** and exited 0.
    A census JSON that was truncated, that renamed the field, or that was a DIFFERENT TOOL's output
    passed the one gate whose job is to stop a human eyeballing readiness.
  * A missing fixture and a real gap shared exit code 1. They have different fixes, so they are now 64
    and 1.
  * Neither `python` invocation was bounded.

Every census the suite builds states its own expectation by construction, and the malformed ones are
built by REMOVING or REPLACING a field rather than by hand-writing a plausible wrong file -- so a
fixture cannot quietly agree with a broken tool.
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

sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import fusion_root  # noqa: E402
SCRIPT = Path(os.environ.get("PHASE9_GATE_SCRIPT",
                             REPO / "scripts" / "gate_class_system_phase9.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_gate_class_system_phase9.py"
RUN_TIMEOUT = 120

_spec = importlib.util.spec_from_file_location("gate_class_system_phase9", SCRIPT)
gate = importlib.util.module_from_spec(_spec)
sys.modules["gate_class_system_phase9"] = gate
_spec.loader.exec_module(gate)
_PRISTINE_RUN = gate._RUN

# A census that answers every question, correctly, in the affirmative. Every fixture below is THIS
# object with exactly one thing changed, so a fixture's expectation is stated by the change.
READY_CENSUS = {
    "families_total": 48,
    "families_with_reader": 48,
    "families_without_reader": 0,
    "edges_total": 486,
    "edges_unmapped": [],
    "edges_reserved": 0,
    "edges_reserved_pct": 0.0,
    "reader_less_families": [],
}
GAPPY_CENSUS = {
    "families_total": 10,
    "families_with_reader": 8,
    "families_without_reader": 2,
    "edges_total": 100,
    "edges_unmapped": [],
    "edges_reserved": 7,
    "edges_reserved_pct": 7.0,
    "reader_less_families": ["planted.familyOne", "planted.familyTwo"],
}


class Raw:
    """Text written VERBATIM, for a case that needs a file the JSON encoder would not produce.

    The first version signalled "raw text" with `isinstance(census, str)`, which conflated a raw string
    with a string VALUE. So the case for the JSON value `"a string"` wrote the file `a string` --
    invalid JSON -- and the tool refused with `CENSUS-NOT-JSON` instead of `CENSUS-NOT-AN-OBJECT`. The
    case was passing for the wrong reason and would have kept passing if the tool stopped type-checking.
    """

    def __init__(self, text: str) -> None:
        self.text = text


def write(census: dict | object, suffix: str = ".json") -> str:
    handle = tempfile.NamedTemporaryFile("w", suffix=suffix, delete=False, encoding="utf-8")
    try:
        if isinstance(census, Raw):
            handle.write(census.text)
        else:
            json.dump(census, handle)
    finally:
        handle.close()
    return handle.name


def unlink(path: str) -> None:
    """A failed temp-delete is a FAILURE, never a swallowed catch. This is the suite's own scratch."""
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass


class SeamGuard(unittest.TestCase):
    """Every case leaves the tool's private seam as it found it.

    `subprocess` is the process-wide module, so a patch of it reaches every other test in the project
    and outliving its `with` block breaks them while this suite reports green. `tearDown` runs after
    EVERY case, so a leak is attributed to the case that caused it."""

    def tearDown(self) -> None:
        if gate._RUN is not _PRISTINE_RUN:
            self.fail(f"_RUN was still substituted after {self.id()}: {gate._RUN!r}")


class AWellFormedCensus(SeamGuard):
    def run_gate(self, census: dict | object, extra: list[str] | None = None) -> tuple[int, str]:
        path = write(census)
        self.addCleanup(unlink, path)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main(["--census-json", path, *(extra or [])])
        return code, out.getvalue() + err.getvalue()

    def test_a_READY_census_exits_zero_and_says_READY(self) -> None:
        code, text = self.run_gate(READY_CENSUS)
        self.assertEqual(code, gate.EXIT_READY)
        self.assertIn(gate.READY_LINE, text)

    def test_a_census_with_a_GAP_exits_one_and_names_the_families(self) -> None:
        code, text = self.run_gate(GAPPY_CENSUS)
        self.assertEqual(code, gate.EXIT_NOT_READY)
        self.assertIn(gate.NOT_READY_LINE, text)
        self.assertIn(gate.GAP_LINE, text)
        self.assertIn("planted.familyOne", text)
        self.assertIn("planted.familyTwo", text)
        self.assertIn("2 of 10", text)

    def test_the_VERDICT_is_JSON_READABLE_when_asked(self) -> None:
        path = write(GAPPY_CENSUS)
        self.addCleanup(unlink, path)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main(["--census-json", path, "--json"])
        self.assertEqual(code, gate.EXIT_NOT_READY)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["verdict"], "NOT_READY")
        self.assertEqual(payload["exitCode"], gate.EXIT_NOT_READY)
        self.assertEqual(payload["census"], GAPPY_CENSUS)
        self.assertEqual(len(payload["reasons"]), 1)
        for banned in ("pid", "durationMs", "started", "timestamp"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run")


class TheSilentGreen(SeamGuard):
    """The defect. Each case is a census the original read as READY."""

    def expect_refusal(self, census: dict | object, reason: str) -> None:
        path = write(census)
        self.addCleanup(unlink, path)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main(["--census-json", path, "--json"])
        self.assertEqual(code, gate.EXIT_REFUSED, f"a {reason} census reported exit {code}")
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], reason)
        # Scoped to the VERDICT FIELD, not the whole envelope: the refusal detail legitimately says
        # "cannot be reported READY", so a substring search over the whole payload tests the
        # message rather than the verdict -- and would pass/fail for the wrong reason.
        self.assertNotEqual(payload["verdict"], "READY")
        self.assertNotEqual(payload["exitCode"], gate.EXIT_READY)

    def test_a_census_MISSING_the_verdict_field_REFUSES(self) -> None:
        """`$census.families_without_reader` on a census without it is `$null`, `$null -gt 0` is
        `$false`, and the original printed READY. This is the whole defect."""
        for field in ("families_without_reader", "families_total", "reader_less_families",
                      "edges_total", "edges_reserved", "edges_reserved_pct"):
            with self.subTest(missing=field):
                broken = {k: v for k, v in READY_CENSUS.items() if k != field}
                self.expect_refusal(broken, "CENSUS-FIELD-MISSING")

    def test_a_census_that_is_a_DIFFERENT_TOOLS_output_REFUSES(self) -> None:
        """A well-formed JSON object that answers none of the questions is the realistic version of the
        defect: pointing the flag at the wrong file."""
        self.expect_refusal({"ok": True, "items": []}, "CENSUS-FIELD-MISSING")
        self.expect_refusal({}, "CENSUS-FIELD-MISSING")

    def test_a_census_that_is_not_an_OBJECT_REFUSES(self) -> None:
        for payload in ([], "a string", 42, None):
            with self.subTest(payload=payload):
                self.expect_refusal(payload, "CENSUS-NOT-AN-OBJECT")

    def test_a_field_of_the_WRONG_TYPE_REFUSES(self) -> None:
        for field, value in (("families_without_reader", "zero"), ("families_total", None),
                             ("reader_less_families", "planted.familyOne"),
                             ("edges_reserved_pct", "4.8%")):
            with self.subTest(field=field, value=value):
                broken = dict(READY_CENSUS)
                broken[field] = value
                self.expect_refusal(broken, "CENSUS-FIELD-WRONG-TYPE")

    def test_a_BOOLEAN_is_not_a_COUNT(self) -> None:
        """`bool` is an `int` subclass, so a census answering "yes" where it should answer a count would
        satisfy a naive type check."""
        broken = dict(READY_CENSUS)
        broken["families_without_reader"] = True
        self.expect_refusal(broken, "CENSUS-FIELD-WRONG-TYPE")

    def test_a_census_that_DISAGREES_with_ITSELF_REFUSES(self) -> None:
        """The count and the name list come from the same census, so disagreeing means one of them is
        from a different run -- which is exactly what a stale file looks like."""
        broken = dict(READY_CENSUS)
        broken["families_without_reader"] = 3
        broken["reader_less_families"] = ["only.one"]
        self.expect_refusal(broken, "CENSUS-SELF-INCONSISTENT")

    def test_a_fixture_that_is_not_JSON_REFUSES(self) -> None:
        self.expect_refusal(Raw("{not json at all"), "CENSUS-NOT-JSON")

    def test_a_MISSING_fixture_REFUSES_at_64_not_1(self) -> None:
        """A missing fixture and a real gap are different events with different fixes. The original gave
        both exit 1, so a caller could not tell a typo from a genuine NOT-READY."""
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main(["--census-json", str(REPO / "no-such-census.json"), "--json"])
        self.assertEqual(code, gate.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "CENSUS-FIXTURE-MISSING")
        self.assertNotEqual(code, gate.EXIT_NOT_READY)

    def test_it_CANNOT_report_READY_without_a_census_at_all(self) -> None:
        """`evaluate` on an empty check list must not be vacuously ready. The stale-prose failure list is
        the only way in, and a census with no gap and no staleness IS ready -- so the guard is that a
        REFUSED run never reaches `evaluate`."""
        ready, reasons = gate.evaluate(READY_CENSUS, [])
        self.assertTrue(ready)
        self.assertEqual(reasons, [])
        ready, reasons = gate.evaluate(GAPPY_CENSUS, [])
        self.assertFalse(ready)
        self.assertEqual(len(reasons), 1)


class TheCensusSubprocess(SeamGuard):
    def test_EVERY_census_invocation_carries_a_TIMEOUT(self) -> None:
        calls: list[dict] = []

        def run(argv, **kwargs):
            calls.append({"argv": list(argv), "kwargs": kwargs})
            return subprocess.CompletedProcess(argv, 0, json.dumps(READY_CENSUS), "")

        with mock.patch.object(gate, "_RUN", run):
            gate.run_census(REPO, 42)
        self.assertEqual(len(calls), 2, f"expected --check then --json, made {len(calls)}")
        for call in calls:
            self.assertEqual(call["kwargs"].get("timeout"), 42,
                             f"unbounded census invocation: {call['argv']}")
            self.assertTrue(call["kwargs"].get("capture_output") is not None)

    def test_a_STALE_prose_becomes_a_REASON_not_a_CRASH(self) -> None:
        def run(argv, **kwargs):
            if "--check" in argv:
                return subprocess.CompletedProcess(argv, 1, "", "prose disagrees")
            return subprocess.CompletedProcess(argv, 0, json.dumps(READY_CENSUS), "")

        with mock.patch.object(gate, "_RUN", run):
            census, failures = gate.run_census(REPO, 30)
        self.assertEqual(failures and len(failures), 1)
        self.assertIn(gate.STALE_LINE, failures[0])
        ready, reasons = gate.evaluate(census, failures)
        self.assertFalse(ready, "a stale prose made the gate READY")

    def test_a_census_script_that_EXITS_NON_ZERO_REFUSES(self) -> None:
        def run(argv, **kwargs):
            if "--check" in argv:
                return subprocess.CompletedProcess(argv, 0, "", "")
            return subprocess.CompletedProcess(argv, 2, "boom", "boom")

        with mock.patch.object(gate, "_RUN", run):
            with self.assertRaises(gate.Refusal) as caught:
                gate.run_census(REPO, 30)
        self.assertEqual(caught.exception.reason, "CENSUS-FAILED")

    def test_a_census_that_TIMES_OUT_REFUSES(self) -> None:
        def run(argv, **kwargs):
            raise subprocess.TimeoutExpired(cmd=argv, timeout=kwargs.get("timeout", 1))

        with mock.patch.object(gate, "_RUN", run):
            with self.assertRaises(gate.Refusal) as caught:
                gate.run_census(REPO, 7)
        self.assertEqual(caught.exception.reason, "CENSUS-TIMED-OUT")
        self.assertIn("7s", caught.exception.detail)

    def test_a_MISSING_census_script_REFUSES_BY_NAME(self) -> None:
        with self.assertRaises(gate.Refusal) as caught:
            gate.run_census(REPO.parent / "no-such-repo", 30)
        self.assertEqual(caught.exception.reason, "CENSUS-SCRIPT-MISSING")

    def test_a_MISSING_python_REFUSES_BY_NAME(self) -> None:
        with mock.patch.object(gate, "_WHICH", return_value=None):
            with self.assertRaises(gate.Refusal) as caught:
                gate.resolve_python()
        self.assertEqual(caught.exception.reason, "PYTHON-NOT-ON-PATH")

    def test_a_NON_POSITIVE_timeout_REFUSES_before_any_invocation(self) -> None:
        calls: list = []

        def run(argv, **kwargs):
            calls.append(argv)
            return subprocess.CompletedProcess(argv, 0, json.dumps(READY_CENSUS), "")

        with mock.patch.object(gate, "_RUN", run):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = gate.main(["--census-timeout", "0", "--json"])
        self.assertEqual(code, gate.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")
        self.assertEqual(calls, [], "a refused run still invoked the census")


class TheRealTree(SeamGuard):
    """`NOT READY` is the EXPECTED verdict today. A case asserting READY here would be a defect."""

    def test_the_REAL_tree_reports_NOT_READY_and_names_a_real_family(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main([])
        text = out.getvalue() + err.getvalue()
        self.assertEqual(code, gate.EXIT_NOT_READY,
                         f"the gate's verdict on the real tree changed:\n{text}")
        self.assertIn(gate.NOT_READY_LINE, text)
        self.assertIn(gate.GAP_LINE, text)
        self.assertIn("resource.efficiency", text,
                      "the live reader-less roster is named by ClassSystemPhase9ReadinessGateTests")

    def test_a_ROOT_that_is_not_a_DIRECTORY_REFUSES(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = gate.main(["--root", str(REPO / "scripts" / "nope"), "--json"])
        self.assertEqual(code, gate.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ROOT-NOT-A-DIRECTORY")

    def test_it_is_NOT_wired_into_the_deploy(self) -> None:
        """Deliberate, and pinned here as well as in C#: wiring a gate whose expected verdict is NOT
        READY into a throw-on-failure pipeline would break the run for an honestly-expected state."""
        # deploy-play.py is gk-fusion's. This is a NEGATIVE assertion - the readiness gate must not be
        # wired into the deploy - and a negative assertion against a file that does not exist passes
        # for the wrong reason: `read_text` raised here, so the gate was never checked against anything.
        # Read the deploy script where it actually lives, so the refusal means what it says.
        deploy = fusion_root(REPO) / "scripts" / "deploy-play.py"
        self.assertTrue(deploy.is_file(), f"the deploy script to audit is not there: {deploy}")
        text = deploy.read_text(encoding="utf-8")
        for token in ("gate-class-system-phase9", "gate_class_system_phase9"):
            self.assertNotIn(token, text.lower(),
                             f"the readiness gate is wired into deploy-play.py ({token!r})")


class Surface(SeamGuard):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        declared = {"PYTHON-NOT-ON-PATH", "CENSUS-SCRIPT-MISSING", "CENSUS-TIMED-OUT",
                    "CENSUS-INVOCATION-FAILED", "CENSUS-FAILED", "CENSUS-NOT-JSON",
                    "CENSUS-NOT-AN-OBJECT", "CENSUS-FIELD-MISSING", "CENSUS-FIELD-WRONG-TYPE",
                    "CENSUS-SELF-INCONSISTENT", "CENSUS-FIXTURE-MISSING",
                    "CENSUS-FIXTURE-UNREADABLE", "INVALID-TIMEOUT", "ROOT-NOT-A-DIRECTORY"}
        self.assertEqual(found - declared, set(),
                         f"undocumented refusal reason(s) {sorted(found - declared)}")

    def test_the_EXIT_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({gate.EXIT_READY, gate.EXIT_NOT_READY, gate.EXIT_REFUSED}, {0, 1, 64})

    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--census-json", "--census-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-CensusJsonPath", "-Root", "-CensusJsonPath"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_MODULE_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("gate-class-system-phase9.ps1", head)
        lowered = head.lower()
        for reason in ("malformed census", "silent green", "no timeout", "shared an exit code"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "importlib", "ast",
                          "re"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "gate":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in global_modules:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))


if __name__ == "__main__":
    unittest.main()
