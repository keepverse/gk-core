"""Contract tests for `gk-fusion/scripts/guard-injector-compile.py`.

The property that matters most is the one the guard EXISTS for, and it is not "the build exits 0":
`dotnet build` exits 0 when the project decides it has no interop references and prints
`NOT COMPILED - skipping FusionRpg.Injector.MelonLoader.39`. The original reads the log for that
message and calls it a FAILURE, because a guard that says OK for a project that declined to build
is worse than no guard - it gets trusted.

The build is not invoked here. The cases that need an outcome craft one, because what is under test
is the VERDICT LOGIC over a log, and running a real compile to observe a log this machine never
produces would be a slower way to test less. The invocation is pinned separately by
`TheInvocationIsPinned`, so the argv cannot drift while the log cases stay hermetic.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import subprocess
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
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


guard = _load("guard_injector_compile", REPO / "scripts" / "guard-injector-compile.py")

SKIP_MARKER = "NOT COMPILED — skipping FusionRpg.Injector.MelonLoader.39"


@contextlib.contextmanager
def environ(**values: str):
    """Set real environment variables for the duration of a block.

    `main` reads `os.environ` and deliberately takes no env seam: a second way in would let a caller
    and the CLI disagree about which pack is in force, which is the one thing this guard exists to
    be unambiguous about. So the CLI cases set the environment the tool actually reads.
    """
    previous = {key: os.environ.get(key) for key in values}
    try:
        for key, value in values.items():
            os.environ[key] = value
        yield
    finally:
        for key, value in previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


def fake_build(exit_code: int, output: str, timed_out: bool = False):
    """Replace `run_build` with one reporting a crafted outcome.

    The call list is exposed so a test can assert the tool REACHED the build. A verdict reached
    without building would be a guard that passes without evidence, and that is the defect this
    whole tool is being retired for.
    """
    calls: list[tuple] = []

    def fake(project, out, game_dir, timeout):
        calls.append((project, out, game_dir, timeout))
        return {"exit": exit_code, "output": output, "timed_out": timed_out}

    fake.calls = calls  # type: ignore[attr-defined]
    return fake


class _StubCase(unittest.TestCase):
    """A root that HAS the project, so a build is reached, plus helpers for one that does not."""

    def setUp(self) -> None:
        real = guard.run_build
        self.addCleanup(setattr, guard, "run_build", real)

    def make_root(self, with_project: bool = True) -> Path:
        box = tempfile.TemporaryDirectory(prefix="injector-compile-test-")
        self.addCleanup(box.cleanup)
        root = Path(box.name).resolve()
        project = root / guard.PROJECT
        project.parent.mkdir(parents=True, exist_ok=True)
        if with_project:
            project.write_text("<Project />\n", encoding="utf-8")
        return root

    def make_pack(self) -> Path:
        box = tempfile.TemporaryDirectory(prefix="injector-pack-test-")
        self.addCleanup(box.cleanup)
        pack = Path(box.name).resolve() / "game"
        (pack / "MelonLoader").mkdir(parents=True, exist_ok=True)
        return pack

    def scan_with(self, build, env: dict, timeout: int = 60, with_project: bool = True) -> dict:
        root = self.make_root(with_project)
        guard.run_build = build
        return guard.scan(root, timeout=timeout, env=env)


class EnvPrecedenceIsTheCells(_StubCase):
    """Cell, then loader, then .env - and the SOURCE is reported.

    The middle one losing is specific, not tidiness: the cell must be pvzrh-3.9, and a caller that
    set only the loader-wide variable to a 3.8.1 pack would compile the Int64 bridge against a
    3.8.1 interop's Int32 fields. The build passes the pack as an explicit `-p:`, which is what
    makes the cell authoritative even when a broader variable is also set.
    """

    def test_the_cell_variable_wins_over_the_loader_variable(self) -> None:
        pack = self.make_pack()
        value, source = guard.resolve_game_dir(
            REPO, {guard.ENV_CELL: str(pack), guard.ENV_LOADER: "C:/other"})
        self.assertEqual(value, str(pack))
        self.assertEqual(source, f"env:{guard.ENV_CELL}")

    def test_the_loader_variable_is_used_when_the_cell_one_is_absent(self) -> None:
        pack = self.make_pack()
        value, source = guard.resolve_game_dir(REPO, {guard.ENV_LOADER: str(pack)})
        self.assertEqual(value, str(pack))
        self.assertEqual(source, f"env:{guard.ENV_LOADER}")

    def test_an_empty_cell_variable_falls_through(self) -> None:
        # An empty string is not a pack. Treating it as one produces a confusing skip.
        pack = self.make_pack()
        value, source = guard.resolve_game_dir(
            REPO, {guard.ENV_CELL: "", guard.ENV_LOADER: str(pack)})
        self.assertEqual(value, str(pack))

    def test_the_env_file_is_the_last_resort(self) -> None:
        box = tempfile.TemporaryDirectory(prefix="injector-dotenv-")
        self.addCleanup(box.cleanup)
        root = Path(box.name).resolve()
        (root / ".env").write_text(
            "# a comment\n"
            f"  {guard.ENV_DEFAULT} = {root}\\Game  \n"
            f"{guard.ENV_LOADER}=ignored\n", encoding="utf-8")
        value, source = guard.resolve_game_dir(root, {})
        self.assertEqual(value, f"{root}\\Game")
        self.assertEqual(source, f"file:.env:{guard.ENV_DEFAULT}")

    def test_a_value_containing_an_equals_survives(self) -> None:
        # Split on the FIRST '=' only. Truncating at the second '=' yields a path that does not
        # exist, which then reads as "no pack configured" and hides the real cause.
        box = tempfile.TemporaryDirectory(prefix="injector-dotenv-eq-")
        self.addCleanup(box.cleanup)
        root = Path(box.name).resolve()
        (root / ".env").write_text(f"{guard.ENV_DEFAULT}=C:/a=b/c\n", encoding="utf-8")
        value, _ = guard.resolve_game_dir(root, {})
        self.assertEqual(value, "C:/a=b/c")

    def test_nothing_configured_reports_where_it_looked(self) -> None:
        box = tempfile.TemporaryDirectory(prefix="injector-unset-")
        self.addCleanup(box.cleanup)
        value, source = guard.resolve_game_dir(Path(box.name).resolve(), {})
        self.assertIsNone(value)
        self.assertEqual(source, "unset")


class SkipIsItsOwnVerdict(_StubCase):
    """SKIPPED is a third outcome, not a pass.

    The original exits 0 for a skip AND for a compile, so a caller reading only the exit code cannot
    tell evidence from its absence. The exit code stays 0 so no caller has to change; the verdict is
    what distinguishes them, which is why it is in `--json`.
    """

    def test_no_pack_configured_skips(self) -> None:
        build = fake_build(0, "should never run")
        result = self.scan_with(build, {})
        self.assertEqual(result["verdict"], "SKIPPED")
        self.assertFalse(result["compiled"])
        self.assertEqual(build.calls, [], "a skip must not have invoked a build")

    def test_a_pack_without_the_melonloader_folder_skips(self) -> None:
        # The pack resolved but is not a MelonLoader install. Reporting OK there would claim a
        # compile of something never attempted against a real loader.
        box = tempfile.TemporaryDirectory(prefix="injector-noml-")
        self.addCleanup(box.cleanup)
        build = fake_build(0, "should never run")
        result = self.scan_with(build, {guard.ENV_CELL: str(Path(box.name).resolve())})
        self.assertEqual(result["verdict"], "SKIPPED")
        self.assertEqual(build.calls, [])

    def test_skip_and_ok_share_an_exit_code_and_not_a_verdict(self) -> None:
        # Identical exit codes are acceptable ONLY because the verdict differs, so both halves are
        # asserted: a future edit that made a skip report OK would otherwise slip through.
        self.assertEqual(guard.EXIT_SKIPPED, guard.EXIT_OK)
        self.assertNotEqual(guard.EXIT_SKIPPED, guard.EXIT_FAILED)

    def test_the_skip_verdict_says_the_injector_was_not_compiled(self) -> None:
        # The original's own words, and the whole reason a skip is reported rather than silent.
        self.assertIn("NOT compiled", guard.VERDICT_SKIPPED)


class ExitZeroIsNotACompile(_StubCase):
    """The rule the guard exists for."""

    def test_exit_zero_with_the_skip_marker_is_a_failure(self) -> None:
        result = self.scan_with(fake_build(0, f"info: {SKIP_MARKER}\n"),
                                {guard.ENV_CELL: str(self.make_pack())})
        self.assertEqual(result["verdict"], "FAILED")
        self.assertFalse(result["compiled"])
        self.assertEqual(result["build_exit"], 0)
        self.assertIn("project-skipped-itself", result["findings_by_rule"])

    def test_the_finding_says_the_exit_code_is_not_proof(self) -> None:
        result = self.scan_with(fake_build(0, SKIP_MARKER),
                                {guard.ENV_CELL: str(self.make_pack())})
        self.assertIn("not proof of a compile", result["findings"][0]["message"])

    def test_a_clean_log_with_exit_zero_is_ok(self) -> None:
        result = self.scan_with(
            fake_build(0, "Build succeeded.\n0 Warning(s)\n0 Error(s)\n"),
            {guard.ENV_CELL: str(self.make_pack())})
        self.assertEqual(result["verdict"], "OK")
        self.assertTrue(result["compiled"])
        self.assertEqual(result["findings"], [])

    def test_the_marker_needs_the_project_name_not_a_bare_phrase(self) -> None:
        # A loose pattern would fail a build over an unrelated line mentioning "not compiled".
        result = self.scan_with(
            fake_build(0, "NOT COMPILED — skipping something else\n"),
            {guard.ENV_CELL: str(self.make_pack())})
        self.assertEqual(result["verdict"], "OK")

    def test_the_marker_tolerates_a_hyphen_or_an_em_dash(self) -> None:
        for dash in ("-", "—"):
            with self.subTest(dash=dash):
                result = self.scan_with(
                    fake_build(0, f"NOT COMPILED {dash} skipping FusionRpg.Injector.MelonLoader.39"),
                    {guard.ENV_CELL: str(self.make_pack())})
                self.assertEqual(result["verdict"], "FAILED")


class AFailedBuildIsReported(_StubCase):
    def _env(self) -> dict:
        return {guard.ENV_CELL: str(self.make_pack())}

    def test_error_lines_are_excerpted(self) -> None:
        log = "\n".join(f"src/F{i}.cs({i},1): error CS100{i}: something" for i in range(3))
        result = self.scan_with(fake_build(1, log), self._env())
        self.assertEqual(result["verdict"], "FAILED")
        self.assertEqual(len(result["findings"]), 3)
        self.assertTrue(all(f["rule"] == "build-failed" for f in result["findings"]))

    def test_the_excerpt_is_capped(self) -> None:
        # A compile error list can run to hundreds of lines; the guard prints a bounded excerpt.
        log = "\n".join(f"error CS{i:04d}: x" for i in range(60))
        result = self.scan_with(fake_build(1, log), self._env())
        self.assertEqual(len(result["findings"]), guard.MAX_ERROR_LINES)

    def test_a_failure_with_no_error_lines_still_reports_something(self) -> None:
        # A bare FAILED with no reason is the failure mode this tool is being retired for, so the
        # tail is reported rather than nothing.
        result = self.scan_with(fake_build(1, "MSB4326: restore failed\n"), self._env())
        self.assertEqual(result["verdict"], "FAILED")
        self.assertTrue(result["findings"])
        self.assertIn("restore failed", result["findings"][0]["message"])

    def test_the_error_pattern_needs_its_spaces(self) -> None:
        # `' error '` did not match `errorish` in the original either, and tightening it here would
        # change which lines are excerpted.
        self.assertTrue(guard.ERROR_LINE.search("x.cs(1,1): error CS1001: y"))
        self.assertFalse(guard.ERROR_LINE.search("x.cs(1,1): errorish CS1"))


class ATimeoutIsANamedVerdict(_StubCase):
    """The original passed NO timeout, so a stalled restore hung with no report.

    A timeout is a named verdict with an exit code, not a raised traceback and not a silent pass: a
    guard that hangs tells the operator nothing, and one that says TIMED OUT after N seconds tells
    them where to look.
    """

    def _env(self) -> dict:
        return {guard.ENV_CELL: str(self.make_pack())}

    def test_a_timeout_fails_with_its_own_rule(self) -> None:
        result = self.scan_with(fake_build(0, "", timed_out=True), self._env(), timeout=7)
        self.assertEqual(result["verdict"], "FAILED")
        self.assertTrue(result["timed_out"])
        self.assertIn("build-timeout", result["findings_by_rule"])
        self.assertIn("7s", result["findings"][0]["message"])

    def test_a_timeout_is_not_reported_as_a_build_failure(self) -> None:
        # The exit code of a build that never finished is unknown, so calling it build-failed would
        # assert something the guard did not observe.
        result = self.scan_with(fake_build(0, "", timed_out=True), self._env())
        self.assertNotIn("build-failed", result["findings_by_rule"])

    def test_the_default_timeout_is_finite(self) -> None:
        # The property the retirement is about. A default of 0 or None restores the defect.
        self.assertIsInstance(guard.DEFAULT_TIMEOUT, int)
        self.assertGreater(guard.DEFAULT_TIMEOUT, 0)

    def test_run_build_maps_a_real_timeout_onto_the_named_verdict(self) -> None:
        """The DETECTION, not just the verdict downstream of it.

        Every other timeout test replaces `run_build`, so without this one the branch that turns a
        `TimeoutExpired` into `timed_out=True` is never executed - and mutating that branch to
        return an ordinary failure left all 34 tests green. A coverage gap that a mutation
        demonstrated is worth more than the mutation.
        """
        real = subprocess.run

        def expired(*args, **kwargs):
            # str, not bytes: `run_build` passes `text=True`, so a real TimeoutExpired carries
            # decoded output. A bytes fixture made the concatenation raise and masked the branch
            # this test exists to cover.
            raise subprocess.TimeoutExpired(cmd="dotnet", timeout=kwargs.get("timeout", 1),
                                           output="partial build output", stderr="")

        subprocess.run = expired
        try:
            result = guard.run_build(Path("x.csproj"), Path("C:/tmp/out"), "C:/game", 1)
        finally:
            subprocess.run = real
        self.assertTrue(result["timed_out"])
        self.assertEqual(result["exit"], guard.EXIT_TIMED_OUT)
        # The partial output is kept: a build that got halfway is the only evidence of where it
        # stopped, and a timeout that discards it reports nothing an operator can act on.
        self.assertIn("partial build output", result["output"])

    def test_run_build_returns_the_combined_streams_on_success(self) -> None:
        real = subprocess.run

        class Done:
            returncode = 0
            stdout = "out"
            stderr = "err"

        subprocess.run = lambda *a, **k: Done()
        try:
            result = guard.run_build(Path("x.csproj"), Path("C:/tmp/out"), "C:/game", 5)
        finally:
            subprocess.run = real
        self.assertFalse(result["timed_out"])
        self.assertIn("out", result["output"])
        self.assertIn("err", result["output"])

    def test_a_non_positive_timeout_is_refused(self) -> None:
        err = io.StringIO()
        with redirect_stderr(err):
            code = guard.main(["--timeout", "0"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("INVALID-TIMEOUT", err.getvalue())


class TheInvocationIsPinned(_StubCase):
    """The argv, asserted without running anything.

    The build must write to a TEMP OutputPath - never the game's Mods folder - so the guard works
    while the game runs and holds the deployed DLLs, and it must pass the pack as an explicit
    `-p:MlGameDir`, which is what makes the cell authoritative over a broader variable.
    """

    def test_the_output_path_is_a_temp_directory_not_the_game(self) -> None:
        out = Path("C:/tmp/out")
        argv = guard.build_argv(Path("C:/repo/x.csproj"), out, "C:/game")
        output = next(a for a in argv if a.startswith("-p:OutputPath="))
        self.assertEqual(output, f"-p:OutputPath={out}")
        self.assertNotIn("Mods", output)

    def test_the_pack_is_passed_explicitly(self) -> None:
        argv = guard.build_argv(Path("C:/repo/p.csproj"), Path("C:/tmp/out"), "C:/game")
        self.assertIn("-p:MlGameDir=C:/game", argv)

    def test_it_is_a_release_nologo_build_of_the_cell_project(self) -> None:
        project = Path("C:/repo/p.csproj")
        argv = guard.build_argv(project, Path("C:/tmp/out"), "C:/game")
        self.assertEqual(argv[0], "dotnet")
        self.assertEqual(argv[1], "build")
        self.assertEqual(argv[2], str(project))
        self.assertEqual(argv[argv.index("-c") + 1], "Release")
        self.assertIn("-nologo", argv)

    def test_the_project_is_the_melonloader_39_cell(self) -> None:
        self.assertIn("MelonLoader.39", guard.PROJECT)
        self.assertEqual(guard.CELL, "pvzrh-3.9")


class RefusalIsNamedAndClosed(_StubCase):
    """A missing project with a pack configured is a BROKEN CHECKOUT, not a skip.

    Reporting SKIPPED there would read as "no pack configured", which sends an operator to set an
    environment variable that is already set. The original threw; this refuses with a name.
    """

    def test_a_missing_project_with_a_pack_refuses(self) -> None:
        pack = self.make_pack()
        with self.assertRaises(guard.Refusal) as caught:
            self.scan_with(fake_build(0, "unused"), {guard.ENV_CELL: str(pack)},
                           with_project=False)
        self.assertEqual(caught.exception.reason, "MISSING_PROJECT")

    def test_a_missing_dotnet_refuses_rather_than_raising(self) -> None:
        real = subprocess.run

        def boom(*args, **kwargs):
            raise FileNotFoundError("dotnet")

        subprocess.run = boom
        try:
            with self.assertRaises(guard.Refusal) as caught:
                guard.run_build(Path("x.csproj"), Path("C:/tmp/out"), "C:/game", 5)
        finally:
            subprocess.run = real
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")

    def test_the_cli_reports_a_refusal_on_stderr_with_exit_64(self) -> None:
        pack = self.make_pack()
        root = self.make_root(with_project=False)
        guard.run_build = fake_build(0, "must not be reached")
        out, err = io.StringIO(), io.StringIO()
        with environ(**{guard.ENV_CELL: str(pack)}):
            with redirect_stdout(out), redirect_stderr(err):
                code = guard.main(["--root", str(root), "--json", "--timeout", "5"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn(guard.VERDICT_REFUSED, err.getvalue())
        self.assertEqual(json.loads(out.getvalue())["verdict"], "REFUSED")


class JsonCarriesTheVerdictNotJustTheCode(_StubCase):
    """`--json` must distinguish three outcomes that share at most two exit codes."""

    def _json(self, build, env, timeout: int = 60) -> tuple[int, dict]:
        root = self.make_root()
        guard.run_build = build
        out = io.StringIO()
        with environ(**env):
            with redirect_stdout(out):
                code = guard.main(["--root", str(root), "--json", "--timeout", str(timeout)])
        return code, json.loads(out.getvalue())

    def test_a_skip_is_reported_as_skipped_with_its_reason(self) -> None:
        for key in (guard.ENV_CELL, guard.ENV_LOADER):
            os.environ.pop(key, None)
        code, payload = self._json(fake_build(0, "unused"), {})
        self.assertEqual(payload["verdict"], "SKIPPED")
        self.assertFalse(payload["compiled"])
        self.assertEqual(payload["reason"], "no-melonloader-pack")
        self.assertEqual(code, guard.EXIT_SKIPPED)

    def test_the_source_of_the_pack_is_in_the_envelope(self) -> None:
        pack = self.make_pack()
        _, payload = self._json(fake_build(0, "Build succeeded."), {guard.ENV_CELL: str(pack)})
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["game_dir_source"], f"env:{guard.ENV_CELL}")
        self.assertEqual(payload["cell"], guard.CELL)
        self.assertTrue(payload["compiled"])

    def test_a_failure_carries_the_findings_and_the_build_exit(self) -> None:
        pack = self.make_pack()
        _, payload = self._json(fake_build(1, "error CS1: x"), {guard.ENV_CELL: str(pack)})
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertEqual(payload["build_exit"], 1)
        self.assertTrue(payload["findings"])

    def test_a_skip_and_an_ok_differ_despite_sharing_an_exit_code(self) -> None:
        # The envelope property the whole three-verdict design exists to provide.
        pack = self.make_pack()
        _, skipped = self._json(fake_build(0, "unused"), {})
        _, ok = self._json(fake_build(0, "Build succeeded."), {guard.ENV_CELL: str(pack)})
        self.assertNotEqual(skipped["verdict"], ok["verdict"])


if __name__ == "__main__":
    unittest.main()
