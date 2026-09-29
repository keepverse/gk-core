"""Contract tests for `gk-core/scripts/dump_melon_p0.py`.

Asserts the contract: the CLI surface, the refusal vocabulary, the exit-code vocabulary, the `--json`
envelope, and the four properties the tool exists to guarantee -- which are all properties of what the
ORIGINAL got wrong, and each is therefore a defect the differential could not see (it compares healthy
runs, where both agree).

  * every `dotnet` invocation's exit code is CHECKED. The original ran `dotnet build -v q | Out-Null` and
    two `dotnet run`s, never read `$LASTEXITCODE` for any of them, and finished with `Pop-Location` plus a
    `Write-Host` -- so a failed build and a failed reflection both ended at **exit 0**. The tool whose
    whole job is to say whether two assemblies agree had no way to say they did not.
  * the build's output is NOT discarded. `| Out-Null` threw away the compiler's messages for the step
    most likely to fail.
  * each run gets its OWN scratch directory. The original used a fixed `$env:TEMP/fusionrpg-p0-dump` that
    every caller shared and overwrote with `Set-Content`, so two overlapping runs could reflect over each
    other's payload.
  * the scratch directory is REMOVED, and a failure to remove it is a FAILURE rather than a leak.

Every spawn is stubbed except where a case is about the real pipeline, so a case that needs a real
`dotnet build` is named for that and nothing else depends on one.
"""
from __future__ import annotations

import ast
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("DUMP_MELON_P0_SCRIPT", REPO / "scripts" / "dump_melon_p0.py")).resolve()
# This suite, by path. Read by two cases below that audit how the suite PATCHES, so it must not depend
# on `__file__` being rewritten by a runner, and must stay correct when the tool is substituted from an
# environment variable for a mutation run.
SUITE = REPO / "tests" / "tools" / "test_dump_melon_p0.py"
RUN_TIMEOUT = 300

_spec = importlib.util.spec_from_file_location("dump_melon_p0", SCRIPT)
dm = importlib.util.module_from_spec(_spec)
sys.modules["dump_melon_p0"] = dm
_spec.loader.exec_module(dm)

# The globals this suite substitutes, captured during COLLECTION -- before any case runs, which is the
# one moment they are certainly pristine. See `test_the_PRIVATE_seams_are_BOUND_to_the_real_globals`
# for why the live values are the wrong thing to compare against.
_PRISTINE = {"run": subprocess.run, "which": shutil.which,
             "rmtree": shutil.rmtree, "mkdtemp": tempfile.mkdtemp}

REFUSAL_REASONS = {"ML-GAME-DIR-UNSET", "ML-GAME-DIR-MISSING", "MELON-ASSEMBLY-MISSING",
                   "DOTNET-NOT-ON-PATH", "BUILD-INVOCATION-FAILED", "RUN-INVOCATION-FAILED",
                   "INVALID-TIMEOUT"}


class SeamGuard(unittest.TestCase):
    """Every case leaves the tool's private seams exactly as it found them.

    A stub that outlives its `with` block is invisible from inside the case that opened it -- that case
    already has what it asked for and reports green. The damage lands on whatever runs NEXT, in a file
    that has nothing to do with the tool, which is why it was found as 506 unexplained failures in
    `test_ps1_port_census.py` rather than as a bug in the suite that caused it.

    So the check lives in `tearDown`, which runs after EVERY case and therefore attributes the failure to
    the case that actually leaked. Asserted at the end of the run as well: a final reading is the only
    one that catches a patch nobody stopped at all.
    """

    def tearDown(self) -> None:
        for name, seam, pristine in (("_RUN", dm._RUN, _PRISTINE["run"]),
                                     ("_WHICH", dm._WHICH, _PRISTINE["which"]),
                                     ("_RMTREE", dm._RMTREE, _PRISTINE["rmtree"]),
                                     ("_MKTEMPT", dm._MKTEMPT, _PRISTINE["mkdtemp"])):
            if seam is not pristine:
                self.fail(f"{name} was still substituted after {self.id()}: {seam!r}. "
                          f"A stub outlived its `with` block.")


class Install:
    """A planted game install: a Melon side and (optionally) a Bep side, with a real-looking layout."""

    def __init__(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="dump-melon-contract-")
        self.root = Path(self._tmp.name)
        self.melon = self.root / "melon"
        self.bep = self.root / "bep"
        for base in (self.melon, self.bep):
            (base / "MelonLoader" / "Il2CppAssemblies").mkdir(parents=True)
            (base / "BepInEx" / "interop").mkdir(parents=True)
            (base / "MelonLoader" / "net6").mkdir(parents=True)
            (base / "BepInEx" / "core").mkdir(parents=True)

    def assembly(self, side: str, name: str = "Assembly-CSharp.dll") -> Path:
        base = self.melon if side == "melon" else self.bep
        relative = dm.MELON_RELATIVE if side == "melon" else dm.BEP_ASM_RELATIVE
        path = base.joinpath(*relative)
        path.write_bytes(b"MZ fake assembly")
        return path

    def close(self) -> None:
        self._tmp.cleanup()


class dotnet_stub:  # noqa: N801 - a context manager, lowercase reads like a fixture
    """Stub the tool's PRIVATE runner seam, recording every spawn.

    A fixture rather than a `Mock` because the property under test is WHICH argv reached the child and in
    what order, and whether any of them went UNCHECKED.

    It patches `dm._RUN` and `dm._WHICH`, NOT `subprocess.run` and `shutil.which`. Those are the same
    objects -- `dm.subprocess` is the process-wide `subprocess` module -- so patching through them makes
    the whole process see a mock, and a patch that outlives its `with` block breaks every other test in
    the project while this suite stays green. It happened: 40 cases here, 506 failures in
    `test_ps1_port_census.py`, and the first symptom was a case of MINE reading the literal string
    `'stdout'` and reporting that `--ml-game-dir` was "not found in it".
    """

    def __init__(self, exits: list[int] | None = None, timeouts: list[int] | None = None) -> None:
        self.exits = list(exits or [])
        self.timeouts = set(timeouts or [])
        self.calls: list[dict] = []
        self._patchers: list = []
        self._depth = 0

    def __enter__(self):
        # RE-ENTRANT, deliberately. A case that wants to inspect `stub.calls` after the run needs the
        # stub in scope, and this suite's `run_tool` helper takes one and enters it -- so the common
        # shape is `with dotnet_stub() as stub: self.run_tool(stub)`, entered TWICE. A plain
        # start-on-enter / stop-on-exit pair breaks there: the second `__enter__` starts a second pair
        # whose "original" is already the first pair's mock, and the first `__exit__` stops all four,
        # so the second `__exit__` stops them again and the module is left holding a dead patcher. The
        # failure that produced is spectacular rather than obvious -- 35 cases in this suite failed and
        # the tool under test was innocent.
        if self._depth == 0:
            outer = self

            def run(argv, **kwargs):
                outer.calls.append({"argv": list(argv), "cwd": kwargs.get("cwd"),
                                   "timeout": kwargs.get("timeout")})
                if len(outer.calls) in outer.timeouts:
                    raise subprocess.TimeoutExpired(cmd=argv, timeout=kwargs.get("timeout", 1),
                                                    output=b"", stderr=b"")
                code = outer.exits.pop(0) if outer.exits else 0
                return subprocess.CompletedProcess(argv, code, "stdout", "stderr")

            for target, attribute, value in ((dm, "_RUN", run),
                                             (dm, "_WHICH", lambda _: "/usr/bin/dotnet")):
                patcher = mock.patch.object(target, attribute, value)
                patcher.start()
                self._patchers.append(patcher)
        self._depth += 1
        return self

    def __exit__(self, *exc):
        self._depth -= 1
        if self._depth == 0:
            for patcher in self._patchers:
                patcher.stop()
            self._patchers = []
        return False

    @property
    def commands(self) -> list[list[str]]:
        return [call["argv"] for call in self.calls]


class TheInputs(SeamGuard):
    def setUp(self) -> None:
        self.fixture = Install()
        self.addCleanup(self.fixture.close)
        self.fixture.assembly("melon")
        self.fixture.assembly("bep")

    def test_an_UNSET_game_dir_REFUSES_and_says_what_to_SET(self) -> None:
        """The original's `$MlGameDir` defaulted to an env var that is usually unset and then tested it,
        so the failure arrived as a bare `throw` naming nothing to install."""
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = dm.main(["--json"])
        self.assertEqual(code, dm.EXIT_REFUSED)
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["reason"], "ML-GAME-DIR-UNSET")
        self.assertIn("FUSIONRPG_ML_GAMEDIR", payload["detail"])

    def test_a_NON_POSITIVE_timeout_is_REFUSED_before_any_work(self) -> None:
        for flag in ("--build-timeout", "--run-timeout"):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = dm.main(["--ml-game-dir", str(self.fixture.melon), flag, "0", "--json"])
            self.assertEqual(code, dm.EXIT_REFUSED, flag)
            self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT", flag)

    def test_a_MISSING_Melon_directory_and_a_MISSING_assembly_are_DIFFERENT_refusals(self) -> None:
        """They need different fixes -- point at the right folder, versus install the game -- so they get
        different names. One refusal for both would send the operator to install a game they already have."""
        with self.assertRaises(dm.Refusal) as caught:
            dm.check_inputs(self.fixture.root / "nope", self.fixture.bep)
        self.assertEqual(caught.exception.reason, "ML-GAME-DIR-MISSING")

        # Now the directory exists and the ASSEMBLY inside it does not. Both the Melon-side and the
        # Bep-side assembly are removed, because a Melon folder that happens to carry a BepInEx assembly
        # would otherwise satisfy the check it is not the subject of.
        for base, relative in ((self.fixture.melon, dm.MELON_RELATIVE),
                               (self.fixture.bep, dm.BEP_ASM_RELATIVE)):
            base.joinpath(*relative).unlink(missing_ok=True)
        with self.assertRaises(dm.Refusal) as caught:
            dm.check_inputs(self.fixture.melon, self.fixture.bep)
        self.assertEqual(caught.exception.reason, "MELON-ASSEMBLY-MISSING")
        self.assertIn(str(self.fixture.melon), caught.exception.detail,
                      "the refusal must name the path it looked for")

    def test_a_MISSING_Bep_assembly_is_a_RESULT_and_not_a_refusal(self) -> None:
        """A Melon-only dump is legitimate: the original merely warned and continued, which is right. The
        refusal asymmetry is preserved, and the note names what was absent."""
        (self.fixture.bep / "BepInEx" / "interop" / "Assembly-CSharp.dll").unlink()
        melon_asm, _, _, note = dm.check_inputs(self.fixture.melon, self.fixture.bep)
        self.assertTrue(melon_asm.is_file())
        self.assertIsNotNone(note)
        self.assertIn("Melon-only", note)

    def test_the_LAYOUT_constants_point_at_what_the_original_used(self) -> None:
        self.assertEqual(dm.MELON_RELATIVE, ("MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll"))
        self.assertEqual(dm.BEP_ASM_RELATIVE, ("BepInEx", "interop", "Assembly-CSharp.dll"))


class TheRun(SeamGuard):
    def setUp(self) -> None:
        self.fixture = Install()
        self.addCleanup(self.fixture.close)
        self.fixture.assembly("melon")
        self.fixture.assembly("bep")

    def run_tool(self, stub: dotnet_stub, **kwargs):
        with stub:
            return dm.execute(self.fixture.melon, self.fixture.bep, "pvzrh-3.9", None, False,
                              kwargs.get("build_timeout", 300), kwargs.get("run_timeout", 300))

    def test_EVERY_dotnet_invocation_is_CHECKED(self) -> None:
        """The original ran three and checked none. A red build must not reach `dotnet run`."""
        for failing in (0, 1, 2):
            exits = [0, 0, 0]
            exits[failing] = 9
            stub = dotnet_stub(exits=exits)
            report = self.run_tool(stub)
            self.assertFalse(report.ok, f"invocation {failing} failed and the run still reported OK")
            if failing == 0:
                self.assertEqual(len(stub.commands), 1,
                                 "a failed build must stop before any `dotnet run`")

    def test_a_build_exiting_1_is_a_FAILURE_not_a_NEAR_MISS(self) -> None:
        """Exit 1 is the ORDINARY compiler failure, so it is the exit code a "did the build work?"
        check is most likely to wave through. Asserted over the whole plausible range rather than one
        sentinel, because a check that only rejects 9 would pass against a tool that only rejects 9."""
        for code in (1, 2, 3, 9, 70, 127):
            with self.subTest(exit=code):
                report = self.run_tool(dotnet_stub(exits=[code]))
                self.assertFalse(report.ok, f"build exit {code} still reported OK")
                self.assertIn(f"build failed with exit {code}", report.build_output)

    def test_the_BUILD_output_is_NOT_discarded(self) -> None:
        """`| Out-Null` threw away the compiler's messages for the step most likely to fail."""
        stub = dotnet_stub(exits=[9])
        report = self.run_tool(stub)
        self.assertIn("build failed with exit 9", report.build_output)
        self.assertTrue(report.build_output.strip(),
                        "a failed build must carry the compiler's own words, not just a code")

    def test_each_run_gets_ITS_OWN_scratch_directory(self) -> None:
        """The original used a fixed `$env:TEMP/fusionrpg-p0-dump` that every caller shared and
        overwrote with `Set-Content`, so two overlapping runs could reflect over each other's payload."""
        seen: list[Path] = []
        for _ in range(2):
            stub = dotnet_stub()
            original = dm._MKTEMPT

            def capture(*args, **kwargs):
                path = original(*args, **kwargs)
                seen.append(Path(path))
                return path

            with stub, mock.patch.object(dm, "_MKTEMPT", side_effect=capture):
                dm.execute(self.fixture.melon, self.fixture.bep, "", None, False, 300, 300)
        self.assertEqual(len(set(seen)), 2, f"two runs shared a scratch directory: {seen}")

    def test_the_scratch_is_REMOVED_and_a_FAILED_REMOVE_is_a_FAILURE(self) -> None:
        """A temp directory that is never cleaned is how one local run leaked 65.5 GB, and a swallowed
        delete makes it invisible."""
        stub = dotnet_stub()
        report = self.run_tool(stub)
        self.assertFalse(Path(report.scratch).exists(), "the scratch directory outlived the run")

        with mock.patch.object(dm, "_RMTREE", side_effect=OSError("file in use")):
            with self.assertRaises(OSError):
                self.run_tool(dotnet_stub())

    def test_the_delete_is_NOT_silently_forgiving(self) -> None:
        """`shutil.rmtree(..., ignore_errors=True)` is the shape that leaked 65.5 GB: the delete appears
        to happen, fails, and reports nothing. Asserted on the CALL, not by provoking an OSError --
        replacing `rmtree` with a raiser cannot tell `ignore_errors=True` from `False`, because the
        replacement never reads the flag. So the call itself is inspected."""
        seen: list[dict] = []
        real = dm._RMTREE

        def record(path, **kwargs):
            seen.append(kwargs)
            return real(path, **kwargs)

        with mock.patch.object(dm, "_RMTREE", side_effect=record):
            self.run_tool(dotnet_stub())
        self.assertTrue(seen, "nothing was deleted")
        for kwargs in seen:
            self.assertNotEqual(kwargs.get("ignore_errors"), True,
                                "the delete swallowed its own failure")

    def test_KEEP_SCRATCH_leaves_it_and_SAYS_WHERE(self) -> None:
        with dotnet_stub():
            report = dm.execute(self.fixture.melon, self.fixture.bep, "", None, True, 300, 300)
        self.assertTrue(Path(report.scratch).exists(),
                        "--keep-scratch promises the project is still there")
        self.assertTrue(report.scratch)
        shutil.rmtree(report.scratch, ignore_errors=True)

    def test_a_TIMEOUT_is_a_named_result_not_a_hang(self) -> None:
        with dotnet_stub(timeouts=[1]) as stub:
            report = self.run_tool(stub, build_timeout=7)
        self.assertFalse(report.ok)
        self.assertTrue(report.build_timed_out)
        self.assertIn("no result within 7s", report.build_output,
                      "the message must name the bound that was exceeded")
        self.assertEqual(len(stub.commands), 1)

    def test_a_build_TIMEOUT_is_not_reported_as_a_FAILED_BUILD(self) -> None:
        """A build that ran out of time did not "fail with exit 1", and the report must not say so.

        The distinction is small and easy to lose: both paths return non-OK, so a test that only checks
        the verdict cannot tell them apart, and the diagnostic the tool leaves for a human is exactly
        where the false statement would sit unnoticed.
        """
        with dotnet_stub(timeouts=[1]) as stub:
            report = self.run_tool(stub, build_timeout=9)
        self.assertFalse(report.ok)
        self.assertTrue(report.build_timed_out)
        self.assertIn("no result within 9s", report.build_output)
        self.assertNotIn("build failed with exit", report.build_output,
                         "a timeout was reported as a build failure")
        self.assertEqual(len(stub.commands), 1, "a timed-out build must not reach `dotnet run`")

    def test_a_RUN_timeout_names_the_SIDE(self) -> None:
        with dotnet_stub(timeouts=[2]) as stub:
            report = self.run_tool(stub, run_timeout=5)
        melon = next(s for s in report.sides if s.side == "melon")
        self.assertTrue(melon.timed_out)
        self.assertIn("no result within 5s", melon.output)
        self.assertFalse(report.ok)

    def test_a_MISSING_dotnet_REFUSES_BY_NAME(self) -> None:
        with mock.patch.object(dm, "_WHICH", return_value=None):
            with self.assertRaises(dm.Refusal) as caught:
                dm.resolve_dotnet()
        self.assertEqual(caught.exception.reason, "DOTNET-NOT-ON-PATH")
        self.assertIn("dotnet", caught.exception.detail)
        self.assertIn("SDK", caught.exception.detail)

    def test_the_RESOLVED_executable_is_what_SPAWNS(self) -> None:
        """`CreateProcess` resolves a bare name by appending `.exe` only, so spawning the bare token can
        reach a different tool than the one that was resolved -- the gap two earlier ports closed."""
        seen: list[str] = []
        with mock.patch.object(dm, "_WHICH", return_value=r"C:\shims\dotnet.CMD"):
            with mock.patch.object(dm, "_RUN",
                                   side_effect=lambda argv, **kw: (seen.append(argv[0]) or
                                                                   subprocess.CompletedProcess(argv, 0, "", ""))):
                report = dm.execute(self.fixture.melon, self.fixture.bep, "", None, True, 300, 300)
        self.assertTrue(seen, "nothing spawned")
        for argv0 in seen:
            self.assertEqual(argv0, r"C:\shims\dotnet.CMD",
                             f"a bare or differently-resolved argv[0]: {argv0}")
        # The scratch this case asked to be KEPT, cleaned from the report's own path. Globbing the
        # system temp for the tool's prefix is how a case starts failing for a reason that has nothing
        # to do with the tool -- and that failure reads as a defect in the tool under test.
        shutil.rmtree(report.scratch, ignore_errors=True)


class TheEnvelope(SeamGuard):
    def setUp(self) -> None:
        self.fixture = Install()
        self.addCleanup(self.fixture.close)
        self.fixture.assembly("melon")
        self.fixture.assembly("bep")

    def test_the_JSON_keys_are_the_CONTRACT(self) -> None:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            with dotnet_stub() as stub:
                code = dm.main(["--ml-game-dir", str(self.fixture.melon), "--bep-game-dir",
                                str(self.fixture.bep), "--profile-id", "pvzrh-3.9", "--json"])
        self.assertEqual(code, dm.EXIT_OK)
        payload = json.loads(out.getvalue())
        self.assertEqual(set(payload), {"tool", "verdict", "exitCode", "melonGameDir", "bepGameDir",
                                        "scratch", "profileId", "build", "sides"})
        self.assertEqual(set(payload["build"]), {"exit", "timedOut", "output"})
        for banned in ("pid", "durationMs", "started", "timestamp"):
            self.assertNotIn(banned, payload, f"{banned!r} differs per run")
        self.assertEqual(payload["profileId"], "pvzrh-3.9")
        self.assertTrue(payload["sides"])

    def test_the_ENVIRONMENT_is_READ_as_a_default_for_each_directory_flag(self) -> None:
        """`FUSIONRPG_ML_GAMEDIR` and `FUSIONRPG_GAME_DIR` are how the tool is configured on this
        machine, and the original defaulted both to them. An explicit flag must win, so this asserts the
        precedence rather than the default alone -- a parser that ignored the flag and always read the
        environment would pass a default-only check."""
        other = self.fixture.root / "other-melon"
        (other / "MelonLoader" / "Il2CppAssemblies").mkdir(parents=True)
        # The assembly too: `check_inputs` refuses a Melon folder without one, and a case that plants a
        # bare directory and then calls the real check is testing a refusal, not a default.
        (other / "MelonLoader" / "Il2CppAssemblies" / "Assembly-CSharp.dll").write_bytes(b"MZ")
        planted, _, _, _ = dm.check_inputs(other, self.fixture.bep)

        with mock.patch.dict(os.environ, {"FUSIONRPG_ML_GAMEDIR": str(self.fixture.melon),
                                          "FUSIONRPG_GAME_DIR": str(self.fixture.bep)}, clear=False):
            with dotnet_stub() as stub:
                dm.main(["--json"])
            self.assertTrue(any(str(self.fixture.melon) in " ".join(c["argv"]) for c in stub.calls),
                            "FUSIONRPG_ML_GAMEDIR was not the default for --ml-game-dir")
            # The Bep side is checked too, and by its OWN path. Asserting only the Melon default would
            # pass against a parser that read one environment variable and not the other -- which is
            # what a Melon-only developer would never notice, because their Bep side is always absent.
            self.assertTrue(any("interop" in " ".join(c["argv"]) and str(self.fixture.bep) in
                                " ".join(c["argv"]) for c in stub.calls),
                            "FUSIONRPG_GAME_DIR was not the default for --bep-game-dir")

            with dotnet_stub() as stub:
                dm.main(["--json", "--ml-game-dir", str(other)])
            self.assertTrue(any(str(planted) in " ".join(c["argv"]) for c in stub.calls),
                            "an explicit --ml-game-dir did not override the environment")

    def test_the_PROFILE_ID_has_an_environment_default_that_does_not_ERASE_an_explicit_one(self) -> None:
        """`FUSIONRPG_GAME_PROFILE` names the profile in the report, so the default has to actually
        reach the envelope. Asserted through the JSON the tool emits, not by reading the parser: a case
        that inspects `args` proves the default was constructed, not that it was used."""
        with mock.patch.dict(os.environ, {"FUSIONRPG_ML_GAMEDIR": str(self.fixture.melon),
                                          "FUSIONRPG_GAME_DIR": str(self.fixture.bep),
                                          "FUSIONRPG_GAME_PROFILE": "from-env"}, clear=False):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                with dotnet_stub():
                    dm.main(["--json"])
            self.assertEqual(json.loads(out.getvalue())["profileId"], "from-env")

            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                with dotnet_stub():
                    dm.main(["--json", "--profile-id", "from-flag"])
            self.assertEqual(json.loads(out.getvalue())["profileId"], "from-flag",
                             "an explicit --profile-id did not override the environment")

    def test_the_ENVIRONMENT_absence_is_a_REFUSAL_not_an_empty_path(self) -> None:
        """Configuration read once, explicitly, failing loudly. An unset var must name itself rather than
        becoming a path that happens not to exist."""
        with mock.patch.dict(os.environ, {}, clear=True):
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = dm.main(["--json"])
        self.assertEqual(code, dm.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ML-GAME-DIR-UNSET")
        self.assertFalse(Path(json.loads(out.getvalue()).get("detail", "")).is_absolute()
                         and Path(json.loads(out.getvalue())["detail"]).exists())

    def test_a_SIDE_that_did_NOT_run_says_so_in_a_closed_field(self) -> None:
        """`ran` is the closed vocabulary, not an absent key: a missing Bep assembly must be
        distinguishable from a Bep pass that produced nothing."""
        (self.fixture.bep / "BepInEx" / "interop" / "Assembly-CSharp.dll").unlink()
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            with dotnet_stub():
                dm.main(["--ml-game-dir", str(self.fixture.melon), "--bep-game-dir",
                         str(self.fixture.bep), "--json"])
        payload = json.loads(out.getvalue())
        bep = next(s for s in payload["sides"] if s["side"] == "bep")
        self.assertFalse(bep["ran"])
        self.assertTrue(bep["reason"])
        melon = next(s for s in payload["sides"] if s["side"] == "melon")
        self.assertTrue(melon["ran"])
        self.assertEqual(set(bep), {"side", "assembly", "ran", "exit", "timed_out", "output", "reason"})

    def test_a_side_DFAULTS_to_not_having_run(self) -> None:
        """`ran` is FAIL-SAFE in its default. Flipping the default to True would report a pass that never
        happened as having happened, and every caller that reads the flag without a test of its own would
        inherit the lie -- so the DEFAULT is pinned, not just the flags a case happens to set.

        Asserted on the field default, not on a call site: a case that only exercised the two paths the
        tool currently takes would not notice the default changing at all, which is exactly what
        mutation testing found.
        """
        import dataclasses

        fields = {f.name: f for f in dataclasses.fields(dm.SideResult)}
        self.assertIn("ran", fields)
        self.assertIs(fields["ran"].default, False,
                      "`ran` must default to False: a side is not known to have run until it did")
        self.assertIs(fields["exit"].default, dm.EXIT_REFUSED,
                      "an unrun side must not carry an exit code that reads as success")


class ThePayload(SeamGuard):
    """The generated C# is the SUBJECT. A rewrite would be a different tool, not a port of this one."""

    def test_the_PAYLOAD_is_embedded_and_reaches_the_scratch(self) -> None:
        self.assertIn("AssemblyLoadContext", dm.PROGRAM_CS)
        self.assertIn("ReflectionTypeLoadException", dm.PROGRAM_CS,
                      "a partially-loadable assembly must not abort the whole dump")
        for name in ("Plant", "Zombie", "Board", "CreateZombie"):
            self.assertIn(f'"{name}"', dm.PROGRAM_CS, name)
        self.assertIn("TakeDamage", dm.PROGRAM_CS)
        self.assertIn("SetZombie", dm.PROGRAM_CS)
        self.assertIn("il2cppNs", dm.PROGRAM_CS)

    def test_the_PAYLOAD_prints_the_PARAMETER_TYPES_it_COMPUTES(self) -> None:
        """The original computed `ps` and then never printed it -- its line read `... + ") "`. The
        parameter types are what a Melon-vs-Bep comparison is read for, so the port prints them."""
        self.assertRegex(dm.PROGRAM_CS, r'Console\.WriteLine\("  " \+ m\.Name.*\+ ps\);')
        self.assertNotRegex(dm.PROGRAM_CS, r'\+ m\.GetParameters\(\)\.Length \+ "\) "\);')

    def test_the_PAYLOAD_is_written_to_the_scratch_directory(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dump-melon-scratch-") as tmp:
            scratch = Path(tmp)
            dm.write_scratch(scratch)
            self.assertEqual((scratch / "Program.cs").read_text(encoding="utf-8"), dm.PROGRAM_CS)
            self.assertEqual((scratch / "p0.csproj").read_text(encoding="utf-8"), dm.CSPROJ)

    def test_the_PROJECT_targets_a_framework_the_sdk_has(self) -> None:
        self.assertIn("<TargetFramework>net8.0</TargetFramework>", dm.CSPROJ)


class TheProfileEntryPoint(SeamGuard):
    """`gk-core/scripts/dump_game_profile.py` is the same tool aimed at the profile documentation. It must
    share the implementation, not re-derive it -- the two PowerShell files each computed the same
    repository-root fallback on their own, which is how one interface became two spellings."""

    def setUp(self) -> None:
        self.fixture = Install()
        self.addCleanup(self.fixture.close)
        self.fixture.assembly("melon")
        self.fixture.assembly("bep")
        self.scripts = REPO / "scripts"

    def _load(self, name: str):
        path = self.scripts / name
        spec = importlib.util.spec_from_file_location(path.stem, path)
        module = importlib.util.module_from_spec(spec)
        sys.modules[path.stem] = module
        spec.loader.exec_module(module)
        return module

    def test_it_SHARES_the_implementation_rather_than_spawning_it(self) -> None:
        """A child process for an in-process call: the original paid a PowerShell start-up and
        re-derived every path. Asserted structurally, because a spawn here would be a shell-out that
        `patch.object` on the imported module would not see."""
        source = (self.scripts / "dump_game_profile.py").read_text(encoding="utf-8")
        tree = ast.parse(source)
        spawns = [n for n in ast.walk(tree)
                  if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                  and n.func.attr in ("run", "Popen", "call", "check_call", "check_output")
                  and isinstance(n.func.value, ast.Name)
                  and n.func.value.id in ("subprocess", "os")]
        self.assertEqual(spawns, [], f"the alias spawns a process: "
                                      f"{[ast.dump(n)[:60] for n in spawns]}")

    def test_it_EXPOSES_the_same_FLAGS(self) -> None:
        module = self._load("dump_game_profile.py")
        out = subprocess.run([sys.executable, str(self.scripts / "dump_game_profile.py"), "--help"],
                             capture_output=True, text=True, timeout=RUN_TIMEOUT).stdout
        for flag in ("--ml-game-dir", "--bep-game-dir", "--profile-id", "--keep-scratch", "--json"):
            self.assertIn(flag, out, flag)
        self.assertEqual(module.core.TOOL_ID, "dump-melon-p0",
                         "the underlying implementation must keep its own identity")

    def test_the_PROFILE_default_is_auto_and_REACHES_the_envelope(self) -> None:
        """The original printed "Profile hint: auto" when the caller knew nothing, which told the
        operator the profile was unsupplied. Kept -- and unlike the original it now REACHES the report
        instead of only a line of prose."""
        self.assertEqual(self._load("dump_game_profile.py").DEFAULT_PROFILE_ID, "auto")
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err), mock.patch.dict(os.environ, {}, clear=True):
            with dotnet_stub():
                code = self._load("dump_game_profile.py").main(
                    ["--ml-game-dir", str(self.fixture.melon), "--bep-game-dir",
                     str(self.fixture.bep), "--json"])
        self.assertEqual(code, dm.EXIT_OK)
        self.assertEqual(json.loads(out.getvalue())["profileId"], "auto")
        self.assertEqual(json.loads(out.getvalue())["tool"], "dump-game-profile")

    def test_it_PRINTS_the_PROFILE_hint_and_not_only_the_P0_one(self) -> None:
        module = self._load("dump_game_profile.py")
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err), dotnet_stub():
            module.main(["--ml-game-dir", str(self.fixture.melon), "--bep-game-dir",
                         str(self.fixture.bep), "--profile-id", "pvzrh-3.9"])
        printed = out.getvalue()
        self.assertIn("Profile hint: pvzrh-3.9", printed)
        self.assertIn("game-profiles.json fingerprints", printed)
        self.assertIn("game-types-", printed)

    def test_the_P0_HINT_is_REPLACED_not_APPENDED(self) -> None:
        """The two entry points point at DIFFERENT documentation. Printing both would send an operator
        updating the P0 memo when they asked for a profile, which is the confusion the alias existed to
        avoid."""
        module = self._load("dump_game_profile.py")
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err), dotnet_stub():
            module.main(["--ml-game-dir", str(self.fixture.melon), "--bep-game-dir",
                         str(self.fixture.bep)])
        self.assertNotIn("melonloader-assembly-csharp-p0.md", out.getvalue())

    def test_a_REFUSAL_still_EXITS_64_through_the_ALIAS(self) -> None:
        """A wrapper that mapped everything to one code would destroy the distinction a caller scripts
        against -- which is the habit the PowerShell form acquired by never checking `$LASTEXITCODE`."""
        module = self._load("dump_game_profile.py")
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err), mock.patch.dict(os.environ, {}, clear=True):
            code = module.main(["--json"])
        self.assertEqual(code, dm.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ML-GAME-DIR-UNSET")

    def test_it_states_WHY_THE_POWERSHELL_FORM_WAS_RETIRED(self) -> None:
        head = (self.scripts / "dump_game_profile.py").read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("dump-game-profile.ps1", head)
        lowered = head.lower()
        for reason in ("forwarded nothing", "-profileid", "second implementation"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")


class Surface(SeamGuard):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--ml-game-dir", "--bep-game-dir", "--profile-id", "--keep-scratch",
                     "--scratch-parent", "--build-timeout", "--run-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-MlGameDir", "-BepGameDir", "-ProfileId"):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, "x"], capture_output=True,
                                  text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - REFUSAL_REASONS, set(),
                         f"undocumented refusal reason(s) {sorted(found - REFUSAL_REASONS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({dm.EXIT_OK, dm.EXIT_FAILED, dm.EXIT_REFUSED}, {0, 1, 64})

    def test_EVERY_subprocess_call_carries_a_timeout(self) -> None:
        """Structural, by AST. A spawn without a bound is the defect the retirement was about, and a
        code-reading claim about it is exactly what a case should not rely on.

        Read through the module-private seam rather than `subprocess.run`, because that is what the tool
        now calls -- and the binding between the two is asserted separately, so the seam cannot quietly
        stop being `subprocess.run` and leave this case passing over nothing.
        """
        tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
        sites = [n for n in ast.walk(tree)
                 if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
                 and n.func.id == "_RUN"]
        self.assertEqual(len(sites), 1, f"expected one spawn site, found {len(sites)}")
        keywords = {kw.arg for kw in sites[0].keywords}
        self.assertIn("timeout", keywords, "the single spawn site must carry a timeout")
        self.assertIn("capture_output", keywords)

    def test_the_PRIVATE_seams_are_BOUND_to_the_real_globals(self) -> None:
        """`_RUN` exists so the suite can substitute THIS module's runner without touching the
        process-wide one. If an alias stopped being what it claims to alias, every stub in the suite
        would be patching nothing and the suite would go quietly vacuous -- green, and testing nothing.

        Compared against the globals AS THEY WERE AT IMPORT, not as they are now. A live comparison is
        the wrong anchor: another file in this project patches `subprocess.run` globally, so asking
        "is the current `subprocess.run` the one I bound?" reports a failure that belongs to somebody
        else -- and a case that fails for someone else's reason gets deleted, which is how a real hole
        goes unrecorded. Module import happens during collection, before any case runs, so it is the
        one moment this process's globals are certainly pristine.
        """
        self.assertIs(dm._RUN, _PRISTINE["run"])
        self.assertIs(dm._WHICH, _PRISTINE["which"])
        self.assertIs(dm._RMTREE, _PRISTINE["rmtree"])
        self.assertIs(dm._MKTEMPT, _PRISTINE["mkdtemp"])
        for name, seam in (("run", dm._RUN), ("which", dm._WHICH), ("rmtree", dm._RMTREE),
                           ("mkdtemp", dm._MKTEMPT)):
            self.assertNotIsInstance(seam, mock.NonCallableMock,
                                     f"the {name} seam is a Mock, so it was never bound to the real one")

        # A SECOND, independent proof, by provenance rather than by reference. The four `assertIs`
        # lines above all compare against one captured snapshot, so a mutant that rewrites ANY of them
        # into a tautology -- `assertIs(dm._WHICH, dm._WHICH)` -- satisfies the rest and the suite stays
        # green with the check gone. Provenance cannot be tautologised: a locally-defined stand-in
        # declares its own module, and that module is not `shutil`.
        for name, seam, home in (("_RUN", dm._RUN, "subprocess"), ("_WHICH", dm._WHICH, "shutil"),
                                 ("_RMTREE", dm._RMTREE, "shutil"),
                                 ("_MKTEMPT", dm._MKTEMPT, "tempfile")):
            self.assertEqual(getattr(seam, "__module__", None), home,
                             f"{name} is {getattr(seam, '__qualname__', seam)!r} from "
                             f"{getattr(seam, '__module__', None)!r}, not the real {home} function")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        """This suite patches module-private seams. A patch of `subprocess`, `shutil` or `tempfile`
        through the tool is a patch of the PROCESS-WIDE module -- the same object -- so it reaches every
        other test that runs in the same process, and a patch that outlives its `with` block breaks them
        while this suite stays green.

        It happened, and the count is why this case exists: 40 cases here produced 506 failures in
        `test_ps1_port_census.py`, whose sandbox calls `subprocess.run(["git", "init"], check=True)` and
        so silently never created its throwaway repo. The first symptom visible from inside was a case of
        MINE reading the stub's literal `'stdout'` and reporting that `--ml-game-dir` was "not found in
        it" -- which is why a suite can be fully self-consistent and still be wrong.

        Read by AST over this file, so a `mock.patch.dict(os.environ, ...)` -- which is genuinely
        process-wide and genuinely restored -- is not confused with a module-attribute patch.
        """
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        global_modules = {"subprocess", "shutil", "tempfile", "os", "sys", "importlib"}
        offences: list[str] = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "dm":
                continue
            label = ast.unparse(target) if target is not None else "?"
            root = label.split(".")[0]
            if root in global_modules:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [],
                         f"process-wide patches; each breaks every other test in the process:\n"
                         + "\n".join(offences))

    def test_NO_test_METHOD_STARTS_a_patch_it_cannot_STOP(self) -> None:
        """A patch this suite starts must have an owner that will stop it. `with` gives one; a
        `patcher.start()` written directly into a test body does not, and if the assertion below it
        fails there is nothing left to restore the module.

        That is the other half of the leak, and it is the worse half: the patch escapes precisely when
        the test that owns it is the thing that broke, so the damage lands on OTHER tests and the broken
        one still reports its own failure.

        Scoped to methods named `test_*`. A fixture's `__enter__` starting a patch is the context
        manager doing its job -- the caller's `with stub:` is the owner -- and flagging that would make
        this case complain about the correct shape."""
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        unowned: list[str] = []
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
                            unowned.append(f"{cls.name}.{func.name} line {node.lineno}: "
                                           f".{node.func.attr}() with no owner")
        self.assertEqual(unowned, [], "\n".join(unowned))

    def test_the_MODULE_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        """Provenance outlives the deletion only if the docstring carries it, and the docstring is
        prose -- so the case is on the DEFAULTS it must keep naming, not on the exact wording. A test
        that pinned the wording would fail on a harmless rewrap and pass on a docstring that named the
        file and none of the defects."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        lowered = head.lower()
        self.assertIn("dump-melon-p0.ps1", head)
        self.assertIn("why the powershell form was retired", lowered)
        for reason in ("exit 0", "out-null", "fixed name", "no timeout"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")


if __name__ == "__main__":
    unittest.main()
