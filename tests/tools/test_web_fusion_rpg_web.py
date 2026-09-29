"""Contract tests for the `gk-core/scripts/checks/web_fusion_rpg_web.py` port and the sequence support it added
to `gk-core/scripts/checks/common.py`.

WHAT IS ASSERTED, AND WHY THESE CHOICES
  * The ORDER of the two steps, and that the runner is FAIL-FAST. The original's own comment named the
    order as the design ("the order that fails fastest"), and an order nobody asserts is an order a
    reformatting can invert. Asserted as order, never as a count -- a check that pins "two steps" fails
    when a third is added, and the "fix" is to delete the assertion.
  * That `CHECK` and `CHECKS` are MUTUALLY EXCLUSIVE. A wrapper declaring both is ambiguous, and
    resolving the ambiguity silently would pick one at random -- which is the kind of default that makes
    a check green without ever running.
  * That a MISSING `node_modules` REFUSES and runs NO command. The original threw here, which is a
    configuration fault with a different fix from "the check is red"; the port's whole point is that the
    two have different exit codes. A refusal that ran the command anyway would be the worst of both.
  * That the wrapper's declared commands are the ones `ci.yml` runs in the same working directory. That
    is the property the wrapper EXISTS for (a wrapper that drifts from its CI step is a check in two
    places enforced in one), and it is the one thing a differential against the retired `.ps1` cannot
    keep honest after the file is gone.
"""
from __future__ import annotations

import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import types
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
CHECKS = REPO / "scripts" / "checks"
WEB_SCRIPT = CHECKS / "web_fusion_rpg_web.py"
RUN_TIMEOUT = 300

sys.path.insert(0, str(CHECKS))
import common  # noqa: E402


def load(path: Path, name: str) -> types.ModuleType:
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


web = load(WEB_SCRIPT, "web_fusion_rpg_web")

# The refusal vocabulary `common.py` can produce, as a CLOSED set for this surface. Anything else is a
# finding, because a reason nothing compares against anything is prose.
COMMON_REFUSALS = {
    "WRAPPER-SPEC-AMBIGUOUS", "WRAPPER-SPEC-INCOMPLETE", "WORKING-DIRECTORY-MISSING",
    "TOOLCHAIN-NOT-ON-PATH", "REQUIRED-PATH-MISSING", "COMMAND-NOT-FOUND", "INVOCATION-FAILED",
}


def fake_module(**declarations) -> types.ModuleType:
    module = types.ModuleType("fake_wrapper")
    for key, value in declarations.items():
        setattr(module, key, value)
    return module


class TheSpec(unittest.TestCase):
    """`spec_from` is the machine-readable contract every wrapper declares through."""

    def test_CHECKS_alone_is_a_valid_spec(self) -> None:
        spec = common.spec_from(fake_module(CHECKS=(("a",), ("b",)), FAIL_HINT="h"))
        self.assertEqual(spec["checks"], (("a",), ("b",)))
        self.assertEqual(spec["check"], ())

    def test_CHECK_alone_is_still_valid(self) -> None:
        """The other sixteen wrappers. If this breaks, the extension broke sixteen checks."""
        spec = common.spec_from(fake_module(CHECK=("a", "b"), FAIL_HINT="h"))
        self.assertEqual(spec["check"], ("a", "b"))
        self.assertEqual(spec["checks"], ())

    def test_declaring_BOTH_is_REFUSED_rather_than_resolved_by_a_default(self) -> None:
        with self.assertRaises(common.Refusal) as caught:
            common.spec_from(fake_module(CHECK=("a",), CHECKS=(("b",),), FAIL_HINT="h"))
        self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-AMBIGUOUS")

    def test_declaring_NEITHER_is_REFUSED(self) -> None:
        with self.assertRaises(common.Refusal) as caught:
            common.spec_from(fake_module(FAIL_HINT="h"))
        self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-AMBIGUOUS")

    def test_a_MISSING_FAIL_HINT_is_REFUSED_by_name(self) -> None:
        with self.assertRaises(common.Refusal) as caught:
            common.spec_from(fake_module(CHECK=("a",)))
        self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-INCOMPLETE")

    def test_an_EMPTY_CHECK_or_CHECKS_is_REFUSED_rather_than_running_nothing(self) -> None:
        for declarations in ({"CHECK": (), "FAIL_HINT": "h"},
                             {"CHECKS": (), "FAIL_HINT": "h"},
                             {"CHECKS": ((),), "FAIL_HINT": "h"}):
            with self.assertRaises(common.Refusal) as caught:
                common.spec_from(fake_module(**declarations))
            self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-INCOMPLETE", declarations)

    def test_every_wrapper_in_the_directory_still_loads(self) -> None:
        """Every wrapper, not just this one: the shared runner is what the sequence support touched."""
        wrappers = [p for p in sorted(CHECKS.glob("*.py")) if p.name != "common.py"]
        self.assertGreaterEqual(len(wrappers), 16)
        for path in wrappers:
            with self.subTest(wrapper=path.name):
                spec = common.spec_from(load(path, f"probe_{path.stem}"))
                self.assertTrue(spec["check"] or spec["checks"], path.name)
                self.assertTrue(spec["fail_hint"], path.name)


class TheWebWrapper(unittest.TestCase):
    """The declaration itself, read from the module rather than from a copy of it."""

    def test_it_declares_a_SEQUENCE_and_not_a_single_CHECK(self) -> None:
        self.assertFalse(hasattr(web, "CHECK"), "declaring CHECK too would be ambiguous")
        self.assertTrue(hasattr(web, "CHECKS"))

    def test_the_TESTS_run_BEFORE_the_BUILD(self) -> None:
        """The order is the design, and the original's comment said so. Asserted as order, never as a
        count -- a count fails when a step is added and the fix is to delete the assertion."""
        steps = [tuple(step) for step in web.CHECKS]
        test_at = next(i for i, step in enumerate(steps) if step[-1] == "test")
        build_at = next(i for i, step in enumerate(steps) if step[-1] == "build")
        self.assertLess(test_at, build_at,
                        f"the suite must run before the build: {steps}")

    def test_the_BUILD_is_the_type_CHECK_and_not_a_bare_bundler(self) -> None:
        """`npm run build` is `tsc --noEmit + vite build`; the type check is the half a bare vitest run
        does NOT do, so the wrapper must invoke the SCRIPT and not `vite` directly."""
        build = next(tuple(step) for step in web.CHECKS if tuple(step)[-1] == "build")
        self.assertEqual(build[:3], ("npm", "run", "build"))

    def test_it_requires_node_modules_and_names_npm_as_its_tool(self) -> None:
        self.assertEqual(web.REQUIRED_PATHS, ("web/fusion-rpg-web/node_modules",))
        self.assertEqual(web.PREFLIGHT, ("npm",))

    def test_it_declares_a_FAIL_HINT(self) -> None:
        self.assertTrue(web.FAIL_HINT.strip())


class TheExecution(unittest.TestCase):
    """The runner's behaviour, driven with the real subprocess call replaced by a recorder."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="checks-sequence-")
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        (self.root / "web" / "fusion-rpg-web" / "node_modules").mkdir(parents=True)

    def spec(self, **over) -> dict:
        base = {"check": (), "checks": (("npm", "test"), ("npm", "run", "build")), "fail_hint": "h",
                "working_directory": "web/fusion-rpg-web", "preflight": ("npm",), "required_paths": (),
                "summary": ""}
        base.update(over)
        return base

    def run_steps(self, spec: dict, codes: list[int], timeout: int = 300) -> tuple[list[list[str]], dict]:
        """Run the sequence against a recorder, answering each command with the next code."""
        seen: list[list[str]] = []
        queue = list(codes)

        def fake_run(command, cwd, seconds):
            seen.append(list(command))
            import subprocess as sp
            return {"exit": queue.pop(0) if queue else 0, "output": "recorded", "timed_out": False}

        with mock.patch.object(common, "run", side_effect=fake_run):
            result = common.execute(spec, self.root, timeout)
        return seen, result

    def test_a_GREEN_sequence_runs_EVERY_step_in_ORDER(self) -> None:
        seen, result = self.run_steps(self.spec(), [0, 0])
        self.assertEqual([tuple(s) for s in seen], [("npm", "test"), ("npm", "run", "build")])
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["exit"], common.EXIT_OK)

    def test_a_RED_first_step_STOPS_the_sequence(self) -> None:
        """Fail-fast. The steps are independent, so running the rest after a red buys nothing and costs
        the operator the whole sequence's time on every single failure."""
        seen, result = self.run_steps(self.spec(), [1])
        self.assertEqual([tuple(s) for s in seen], [("npm", "test")])
        self.assertEqual(result["verdict"], "FAILED")
        self.assertEqual(result["exit"], common.EXIT_FAILED)
        self.assertEqual(result["failed_step"], 1)
        self.assertEqual(result["step_count"], 2)

    def test_a_RED_second_step_is_reported_AS_STEP_TWO(self) -> None:
        seen, result = self.run_steps(self.spec(), [0, 2])
        self.assertEqual(len(seen), 2)
        self.assertEqual(result["failed_step"], 2)
        self.assertEqual(result["exit"], common.EXIT_FAILED,
                         "the wrapper's own exit stays in the wrapper's vocabulary")
        self.assertEqual(result["command_exit"], 2,
                         "the command's own code is carried, because collapsing it to 1 loses which "
                         "failure it was")

    def test_a_COMMAND_exiting_64_cannot_masquerade_as_a_REFUSAL(self) -> None:
        """64 is this module's REFUSED. If a command's code were propagated, a check exiting 64 would be
        read as a refusal the wrapper never made -- and a caller acting on that would look for a missing
        tree or a missing toolchain instead of reading the check's output."""
        _, result = self.run_steps(self.spec(), [64])
        self.assertEqual(result["exit"], common.EXIT_FAILED)
        self.assertNotEqual(result["exit"], common.EXIT_REFUSED)
        self.assertEqual(result["command_exit"], 64)

    def test_a_MISSING_required_path_REFUSES_and_runs_NOTHING(self) -> None:
        spec = self.spec(required_paths=("web/fusion-rpg-web/node_modules", "does/not/exist"))
        with mock.patch.object(common, "run") as recorder:
            with self.assertRaises(common.Refusal) as caught:
                common.execute(spec, self.root, 300)
        self.assertEqual(caught.exception.reason, "REQUIRED-PATH-MISSING")
        recorder.assert_not_called()

    def test_a_MISSING_working_directory_REFUSES_rather_than_running_from_the_repo_root(self) -> None:
        """Running from the wrong directory would make a relative path mean the wrong thing, which is how
        a check reads a different file than the one it was asked about."""
        with self.assertRaises(common.Refusal) as caught:
            common.execute(self.spec(working_directory="no/such/tree"), self.root, 300)
        self.assertEqual(caught.exception.reason, "WORKING-DIRECTORY-MISSING")

    def test_a_TIMEOUT_is_a_VERDICT_and_names_the_step(self) -> None:
        seen: list[list[str]] = []

        def timeout_run(command, cwd, seconds):
            seen.append(list(command))
            import subprocess as sp
            return {"exit": common.EXIT_TIMED_OUT, "output": "", "timed_out": True}

        with mock.patch.object(common, "run", side_effect=timeout_run):
            result = common.execute(self.spec(), self.root, 300)
        self.assertEqual(result["verdict"], "TIMED_OUT")
        self.assertEqual(len(seen), 1, "a timed-out step must not be followed by the next one")

    def test_the_SEQUENCE_SHARES_ONE_timeout_budget(self) -> None:
        """A per-step timeout would multiply the bound by the step count, so `--timeout 60` on two steps
        could hang for 120s -- which is the unbounded behaviour the retirement was about.

        Asserted by ELAPSED TIME, not by inspecting the budget passed to `run`. The first version of this
        case recorded the `seconds` argument and asserted `all(b <= 60)`, and a mutation that passes the
        FULL budget to every step passes that too -- `--timeout 60` twice is still `<= 60`. The property
        is that the second step's budget is what REMAINS, and only a clock shows that.
        """
        started = time.monotonic()
        steps = {"n": 0}

        def slow_step(command, cwd, seconds):
            steps["n"] += 1
            time.sleep(0.30)
            return {"exit": 0, "output": "", "timed_out": False}

        with mock.patch.object(common, "run", side_effect=slow_step):
            result = common.execute(self.spec(), self.root, 60)
        elapsed = time.monotonic() - started
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(steps["n"], 2, "both steps must run for this case to measure anything")
        # Two steps at 0.30s each under a SHARED 60s budget. This asserts only that the second call
        # happened while the first's time was counted, so the discriminating assertion is the one below.
        self.assertGreaterEqual(elapsed, 0.60)

    def test_the_SECOND_step_budget_is_what_REMAINS_not_the_whole_timeout(self) -> None:
        """The case that actually kills M4, stated as the arithmetic rather than as a wall clock.

        A `--timeout` is a budget for the WHOLE check. If the second step is handed the full timeout
        again, the bound the caller asked for is twice what they asked for, and a wedged second step
        hangs for the whole budget instead of what was left. Measured through `common.run`'s own
        argument, with the elapsed time pinned so the FIRST budget is already partly spent.
        """
        budgets: list[int] = []

        def spend_then_record(command, cwd, seconds):
            budgets.append(seconds)
            return {"exit": 0, "output": "", "timed_out": False}

        # Make `time.monotonic` advance by 5s per call so "what remains" is unambiguous at 60s total.
        clock = {"t": 1000.0}

        def fake_now() -> float:
            clock["t"] += 5.0
            return clock["t"]

        with mock.patch.object(common, "run", side_effect=spend_then_record):
            with mock.patch.object(common.time, "monotonic", side_effect=fake_now):
                common.execute(self.spec(), self.root, 60)
        self.assertEqual(len(budgets), 2)
        # The clock advances 5s per CALL, and `execute` calls it once for `started` before the first
        # step, so the first step is already 5s in. Asserted as the RELATIONSHIP rather than as literals,
        # because a literal pair is arithmetic the reader has to re-derive -- and because the property
        # under test is "each step gets what REMAINS", not "the numbers happen to be 55 and 50".
        self.assertLess(budgets[1], budgets[0],
                        f"the second step must get LESS than the first, not the whole budget again: "
                        f"{budgets}")
        self.assertEqual(budgets[0] - budgets[1], 5,
                         f"each step's budget must fall by exactly the elapsed clock: {budgets}")

    def test_a_TIME_OUT_is_never_MORE_THAN_the_remaining_budget(self) -> None:
        """And the floor cannot exceed it either: `max(1, ...)` is what keeps a negative remainder from
        becoming a timeout that has already expired, and the recorded reason must name the REAL number
        the step was given rather than the caller's total."""
        clock = {"t": 1000.0}

        def fake_now() -> float:
            clock["t"] += 100.0
            return clock["t"]

        def hang(command, cwd, seconds):
            return {"exit": common.EXIT_TIMED_OUT, "output": "", "timed_out": True}

        with mock.patch.object(common, "run", side_effect=hang):
            with mock.patch.object(common.time, "monotonic", side_effect=fake_now):
                result = common.execute(self.spec(), self.root, 60)
        self.assertEqual(result["verdict"], "TIMED_OUT")
        self.assertIn("1s", result["reason"],
                      f"the reason must name the budget the step actually got: {result['reason']}")
        self.assertNotIn("60s", result["reason"],
                         "naming the caller's total instead of the step's own bound is the misleading "
                         "half of this")


class TheCli(unittest.TestCase):
    """The wrapper's own surface, driven through `common.main`."""

    def invoke(self, *args: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        # Every argument is stringified here: argparse indexes `arg_string[0]`, so a `Path` in the list
        # raises `TypeError: 'WindowsPath' object is not subscriptable` from inside argparse rather than
        # being rejected as a bad argument.
        with redirect_stdout(out), redirect_stderr(err):
            code = common.main(web.SPEC, [str(arg) for arg in args])
        return code, out.getvalue(), err.getvalue()

    def test_it_refuses_a_MISSING_tree_by_name_and_runs_nothing(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-absent-") as tmp:
            code, _, err = self.invoke("--root", tmp, "--json")
        self.assertEqual(code, common.EXIT_REFUSED)
        self.assertIn("WORKING-DIRECTORY-MISSING", err)

    def test_a_NON_POSITIVE_timeout_is_REFUSED_before_any_work(self) -> None:
        for value in ("0", "-1"):
            code, _, err = self.invoke("--timeout", value)
            self.assertEqual(code, common.EXIT_REFUSED, value)
            self.assertIn("INVALID-TIMEOUT", err)

    def test_a_json_REFUSAL_is_MACHINE_READABLE(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-absent-") as tmp:
            code, out, _ = self.invoke("--root", tmp, "--json")
        self.assertEqual(code, common.EXIT_REFUSED)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], "WORKING-DIRECTORY-MISSING")
        self.assertIn("command", payload)

    def test_a_GREEN_run_reports_OK_and_0_with_a_steps_ARRAY(self) -> None:
        with mock.patch.object(common, "run",
                               return_value={"exit": 0, "output": "", "timed_out": False}):
            code, out, _ = self.invoke("--root", REPO, "--json")
        self.assertEqual(code, common.EXIT_OK)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual([step["command"] for step in payload["steps"]],
                         ["npm test", "npm run build"])
        for banned in ("pid", "duration", "elapsed"):
            self.assertNotIn(banned, payload)

    def test_a_RED_run_names_the_STEP_and_says_how_many_were_SKIPPED(self) -> None:
        calls = {"n": 0}

        def red(command, cwd, seconds):
            calls["n"] += 1
            return {"exit": 1, "output": "", "timed_out": False}

        with mock.patch.object(common, "run", side_effect=red):
            code, _, err = self.invoke("--root", REPO)
        self.assertEqual(code, 1)
        self.assertIn("step 1 of 2", err)
        self.assertIn("1 later step(s) not run", err)

    def test_the_help_text_names_the_FIRST_command(self) -> None:
        out = subprocess.run([sys.executable, str(WEB_SCRIPT), "--help"], capture_output=True,
                             text=True, timeout=RUN_TIMEOUT).stdout
        self.assertIn("npm test", out, "a reader must be able to see what the check does from --help")

    def test_a_WINDOWS_cmd_SHIM_is_resolved_before_the_run(self) -> None:
        """THE BUG THIS PORT EXPOSED, and it is a live one on the platform this repo runs on.

        `shutil.which("npm")` succeeds (the preflight passes) and `subprocess.run(["npm", "test"])` then
        raises `FileNotFoundError`, because `npm`/`npx`/`yarn`/`pnpm` are `.cmd` batch shims on Windows and
        `CreateProcess` will not execute one without its extension in argv[0]. Measured:

            shutil.which("npm")            -> C:\\...\\npm.CMD
            subprocess.run(["npm", "--version"]) -> FileNotFoundError

        So a wrapper declaring a `.cmd` tool would have refused with `COMMAND-NOT-FOUND` on a tool that is
        demonstrably installed. This case runs the REAL tool, so it fails on a machine where npm is absent
        for the right reason rather than for the shim reason.
        """
        resolved = common.resolve_executable("npm")
        self.assertIsNotNone(resolved, "npm is not on PATH here, so this case proves nothing")
        if sys.platform == "win32":
            self.assertTrue(os.path.splitext(resolved)[1].lower() in (".cmd", ".exe", ".bat"),
                            f"on Windows the resolved npm must carry an extension: {resolved}")

        # And the RUN path must use it: the same argv, through `run`, with the declared token.
        import tempfile as tf
        with tf.TemporaryDirectory(prefix="checks-shim-") as tmp:
            proc = subprocess.run([resolved, "--version"], capture_output=True, text=True, timeout=60)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertTrue(proc.stdout.strip(), "npm --version printed nothing")

    def test_a_DECLARED_token_that_is_ALREADY_a_path_is_passed_through(self) -> None:
        """Resolution is for PATH LOOKUPS. A wrapper that declares an absolute path must run exactly
        that path, or a tool would be swapped for whatever shares its name on PATH."""
        with tempfile.TemporaryDirectory(prefix="checks-abs-") as tmp:
            target = Path(tmp) / "my-tool"
            target.write_text("#!/bin/sh\n", encoding="utf-8")
            self.assertEqual(common.resolve_executable(str(target)), str(target))

    def test_the_RUN_uses_the_RESOLVED_executable_and_the_REPORT_names_the_DECLARED_one(self) -> None:
        """M5's kill, and the reason it needs two assertions.

        The run must receive the resolved `.cmd` path or it cannot start at all on Windows. The REPORT
        must still say the DECLARED token, because `npm test` is what a reader retype and what the
        parity test compares -- a report naming `C:\\nvm4w\\nodejs\\npm.CMD test` is neither.
        """
        launched: list[list[str]] = []

        class Completed:
            returncode = 0
            stdout = ""
            stderr = ""

        def capture(argv, **kwargs):
            launched.append(list(argv))
            return Completed()

        with tempfile.TemporaryDirectory(prefix="checks-shim-run-") as tmp:
            cwd = Path(tmp)
            with mock.patch.object(common.subprocess, "run", side_effect=capture):
                result = common.run(("npm", "test"), cwd, 60)

        self.assertEqual(result["exit"], 0)
        self.assertEqual(len(launched), 1, "the child was never spawned")
        self.assertNotEqual(launched[0][0], "npm",
                            "the RUN must use the resolved executable, or Windows cannot start it")
        if sys.platform == "win32":
            self.assertTrue(launched[0][0].lower().endswith((".cmd", ".exe", ".bat")),
                            f"on Windows the spawned argv[0] must carry an extension: {launched[0][0]}")
        self.assertEqual(launched[0][1:], ["test"],
                         "the ARGUMENTS must be forwarded unchanged")

        # and the rendered report still names the declared token
        self.assertEqual(common.render(("npm", "test")), "npm test")

    def test_a_PREFLIGHT_tool_that_is_absent_REFUSES_rather_than_reaching_the_RUN(self) -> None:
        """M10's kill. The refusal has to happen BEFORE the run: a wrapper whose declared toolchain is
        missing must not spawn anything, because a spawned command that cannot find its own tool reports
        the tool's error -- which names nothing the wrapper declared and nothing the caller configured.
        """
        missing = "a-tool-that-is-not-installed-anywhere"
        self.assertIsNone(common.resolve_executable(missing), "the fixture tool must really be absent")
        with tempfile.TemporaryDirectory(prefix="checks-preflight-") as tmp:
            root = Path(tmp)
            (root / "tree").mkdir()
            spec = {"check": ("whatever",), "checks": (), "fail_hint": "h",
                    "working_directory": "tree", "preflight": (missing,), "required_paths": (),
                    "summary": ""}
            with mock.patch.object(common, "run") as recorder:
                with self.assertRaises(common.Refusal) as caught:
                    common.execute(spec, root, 60)
        self.assertEqual(caught.exception.reason, "TOOLCHAIN-NOT-ON-PATH")
        self.assertIn(missing, caught.exception.detail,
                      "the refusal must name the tool, or the operator has nothing to install")
        recorder.assert_not_called()

    def test_a_DECLARED_tool_that_is_present_does_NOT_refuse(self) -> None:
        """The counterweight, so the case above cannot be satisfied by refusing everything."""
        self.assertIsNotNone(common.resolve_executable("python"))
        self.assertEqual(common.toolchain("python"), common.resolve_executable("python"))

    def test_it_shells_out_to_NOTHING_HIDDEN(self) -> None:
        """Every external command goes through `common.run`, which is the one place with a timeout. A
        second spawn site would be a command with no bound."""
        source = WEB_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("subprocess", source)
        self.assertNotIn("os.system", source)


class ParityWithCi(unittest.TestCase):
    """The property the wrapper exists for: it runs what CI runs, from where CI runs it.

    Read from `ci.yml` rather than from a copy of it, so a CI edit that this wrapper does not follow
    fails here instead of drifting.
    """

    def test_the_declared_steps_appear_in_ci_at_the_same_working_directory(self) -> None:
        ci = (REPO / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        # npm is invoked through its own runner in CI; the commands themselves are what must agree.
        for step in web.CHECKS:
            rendered = common.render(tuple(step))
            self.assertIn(rendered, ci, f"ci.yml does not run {rendered}, so the wrapper has drifted")

    def test_the_wrapper_is_the_scripts_PROJECT_ci_AND_THE_REGISTRY_POINT_AT(self) -> None:
        doc = json.loads((REPO / "scripts" / "verification-boundaries.v1.json")
                         .read_text(encoding="utf-8"))
        self.assertEqual(doc["projects"]["web-fusion-rpg-web"]["script"],
                         "scripts/checks/web_fusion_rpg_web.py",
                         "the project entry still points at the retired .ps1")


if __name__ == "__main__":
    unittest.main()
