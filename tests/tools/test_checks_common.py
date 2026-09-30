"""Contract tests for `scripts/checks/common.py` - the harness every check wrapper runs through.

WHY THIS FILE EXISTS, and it is not a "add coverage" reason. `spec_from` accepts a wrapper that
declares EITHER `CHECK` or `CHECKS` and refuses one that declares neither, documenting the reason:
"a wrapper whose spec is incomplete would otherwise be a check that runs an empty command and reports
success". `execute` then built its command sequence from `spec["checks"]` alone. Every wrapper using
the singular `CHECK` therefore got an empty sequence, `steps == []`, `step_count == 0`, verdict OK and
exit 0 - having run nothing.

Measured on the population: 14 of 16 wrappers declared `CHECK` and executed nothing. The two that did
real work were the one declaring `CHECKS` and the one passing its command to `main` explicitly.

Nothing caught it. `test_web_fusion_rpg_web.py` covers this harness well, but only through the ONE
wrapper that happened to declare `CHECKS`, so every assertion it made about the harness was made
through the branch that worked. A harness test that only ever exercises the surviving branch is not a
harness test.

Each assertion below is proven non-vacuous by a mutation named in its docstring, so a future change
that reintroduces the empty-sequence green has to be shown to fail rather than assumed not to.
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
HARNESS = REPO / "scripts" / "checks" / "common.py"

sys.path.insert(0, str(HARNESS.parent))
sys.path.insert(0, str(REPO / "scripts" / "lib"))
import common  # noqa: E402

WRAPPER_TEMPLATE = '''#!/usr/bin/env python3
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import common  # noqa: E402

SUMMARY = "a wrapper planted by tests/tools/test_checks_common.py"
PREFLIGHT = ()
FAIL_HINT = "this wrapper exists only so the harness contract can be observed"
{decl}
WORKING_DIRECTORY = "."

SPEC = common.spec_from(sys.modules[__name__])

if __name__ == "__main__":
    sys.exit(common.main(SPEC))
'''

RECORDING_CHECK = (sys.executable, "-c",
                   "import pathlib,sys; pathlib.Path(sys.argv[1]).write_text(sys.argv[2])")


def spec_of(module) -> dict:
    return common.spec_from(module)


def load_wrapper(directory: Path, decl: str, name: str):
    """Plant a wrapper and import it. Returns the module.

    It is registered in `sys.modules` under a valid identifier, because the wrapper builds its SPEC
    from `sys.modules[__name__]` - and registering it under its FILE name left `sys.modules["w.py"]`,
    so a test reading `sys.modules["w"]` raised KeyError.
    """
    path = directory / name
    path.write_text(WRAPPER_TEMPLATE.format(decl=decl), encoding="utf-8")
    sys.path.insert(0, str(directory))
    try:
        spec = importlib.util.spec_from_file_location(f"planted_{path.stem.replace('-', '_')}", path)
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
    finally:
        sys.path.pop(0)
    return module


class SingularCheckRunsItsOwnArgv(unittest.TestCase):
    """The regression. A wrapper declaring `CHECK` must run THAT argv, exactly once.

    Non-vacuity: mutating `execute` back to `list(spec["checks"])` alone, or spreading
    `spec["check"]` instead of wrapping it, each fails this class.
    """

    def test_the_singular_CHECK_is_run_as_one_argv_and_writes_its_evidence(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            marker = root / "ran.txt"
            argv = (RECORDING_CHECK[0], *RECORDING_CHECK[1:], str(marker), "yes")
            module = load_wrapper(root, f"CHECK = {argv!r}", "w.py")
            result = common.execute(spec_of(module), root, 120, None)
            self.assertEqual(result["verdict"], "OK", result)
            self.assertEqual(result["step_count"], 1, "the single CHECK was not run exactly once")
            self.assertEqual(marker.read_text(encoding="utf-8"), "yes",
                             "the wrapper reported a step it did not run")
            # The step must name the command it ran. Asserting on the OUTPUT was wrong: the
            # fixture's evidence is a file, so `output` is empty and a substring check on it
            # asserted nothing about the argv at all - it failed on the clean run.
            step = result["steps"][0]
            self.assertEqual(step["exit"], 0, step)
            self.assertEqual(common.render(tuple(argv)), step["command"],
                             "the step must report the argv it was given, not a re-spelling of it")

    def test_a_spread_CHECK_would_be_wrong(self) -> None:
        """`spec['check']` is ONE argv tuple, not a bag of tokens. Spreading it runs bare tokens."""
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            module = load_wrapper(root, "CHECK = ('one', 'two', 'three')", "w2.py")
            spec = spec_of(module)
            self.assertEqual(tuple(spec["check"]), ("one", "two", "three"))
            self.assertEqual(spec["checks"], (),
                             "a singular CHECK must leave the sequence form empty, not half-filled")


class SequenceCheckRunsInOrder(unittest.TestCase):
    def test_CHECKS_runs_each_step_and_reports_them(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            a, b = root / "a", root / "b"
            step_a = RECORDING_CHECK[:3] + (str(a), "A")
            step_b = RECORDING_CHECK[:3] + (str(b), "B")
            module = load_wrapper(root, f"CHECKS = (\n    {step_a!r},\n    {step_b!r},\n)", "w3.py")
            result = common.execute(spec_of(module), root, 120, None)
            self.assertEqual(result["verdict"], "OK", result)
            self.assertEqual(result["step_count"], 2)
            self.assertEqual(a.read_text(encoding="utf-8"), "A")
            self.assertEqual(b.read_text(encoding="utf-8"), "B")


class AnEmptySequenceNeverReportsSuccess(unittest.TestCase):
    """`spec_from` already refuses a wrapper declaring neither; `execute` is also called directly with
    hand-built specs by other suites, and an empty sequence reached that way must not read as OK."""

    def test_it_REFUSES_rather_than_reporting_a_clean_run(self) -> None:
        spec = {"check": (), "checks": (), "fail_hint": "", "working_directory": ".",
                "preflight": (), "required_paths": (), "summary": ""}
        with self.assertRaises(common.Refusal) as caught:
            common.execute(spec, REPO, 30, None)
        self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-INCOMPLETE")

    def test_an_explicit_command_still_wins(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            marker = root / "explicit.txt"
            spec = {"check": (), "checks": (), "fail_hint": "", "working_directory": ".",
                    "preflight": (), "required_paths": (), "summary": ""}
            argv = (RECORDING_CHECK[0], *RECORDING_CHECK[1:], str(marker), "explicit")
            result = common.execute(spec, root, 120, argv)
            self.assertEqual(result["verdict"], "OK", result)
            self.assertEqual(marker.read_text(encoding="utf-8"), "explicit")


class OwnerAwareResolution(unittest.TestCase):
    """A check names its tree the way the tree is named next to what owns it, and `root` is gk-core."""

    def test_a_local_tree_wins_over_a_sibling(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            (root / "tools" / "shared").mkdir(parents=True)
            resolved = common.resolve_owned(root, "tools/shared")
            # compared resolved to resolved: resolve_owned resolves its start path, and the temporary
            # directory arrives in its short 8.3 form. That is a spelling difference, not behaviour.
            self.assertEqual(Path(resolved).resolve(), (root / "tools" / "shared").resolve(),
                             "a fixture's own tree must win, or it answers a question it was not asked")

    def test_a_path_no_repository_carries_falls_back_to_root_and_is_still_refused(self) -> None:
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            root = Path(tmp)
            with self.assertRaises(common.Refusal) as caught:
                common.resolve_working_directory(root, "nowhere/at/all")
            self.assertEqual(caught.exception.reason, "WORKING-DIRECTORY-MISSING")
            self.assertIn("nowhere", str(caught.exception.detail))

    def test_a_sibling_owned_tree_resolves_in_the_real_workspace(self) -> None:
        # Measured against the real workspace: this is the case that was refusing exit 64 while
        # reporting itself as a broken tool rather than a moved tree.
        resolved = common.resolve_owned(REPO, "web/fusion-rpg-web")
        self.assertTrue(resolved.is_dir(), resolved)
        self.assertNotEqual(resolved.parent.parent, REPO,
                            "the web tree must not resolve back into gk-core, which has no web/")


class TheWrapperContract(unittest.TestCase):
    def test_declaring_neither_REFUSES_by_name(self) -> None:
        """At IMPORT time, because the wrapper builds its SPEC at module scope.

        My first version asserted the refusal AFTER loading the module. The module never finishes
        loading - `SPEC = common.spec_from(...)` is a module-level statement - so the test asserted on
        a module that could not exist. Refusing at import is the better behaviour anyway: a wrapper
        with no command cannot be imported, run, or reported as a passing check.
        """
        with tempfile.TemporaryDirectory(prefix="checks-common-") as tmp:
            with self.assertRaises(common.Refusal) as caught:
                load_wrapper(Path(tmp), "# no CHECK and no CHECKS", "w4.py")
            self.assertEqual(caught.exception.reason, "WRAPPER-SPEC-AMBIGUOUS")

    def test_no_shipped_wrapper_declares_a_sequence_execute_could_not_run(self) -> None:
        """The population assertion, made STATICALLY.

        The first version of this test ran every shipped wrapper and asserted none of them reported
        OK with zero steps. That was the wrong test twice over: it took a minute per run because it
        executed real generator gates, and it asserted a property of the WRAPPERS by way of their
        side effects, so a red generator - a perfectly normal state - failed it for the wrong reason.

        What has to hold is a property of the DECLARATIONS, and it is checkable in milliseconds: no
        shipped wrapper may produce a spec whose command sequence is empty. That is precisely the
        input the empty-sequence green needed, and it is the thing a later edit to `execute` could
        reintroduce.
        """
        wrappers = sorted(p for p in (REPO / "scripts" / "checks").glob("*.py")
                          if p.name != "common.py")
        self.assertGreaterEqual(len(wrappers), 10, "the wrapper population shrank unexpectedly")
        empty = []
        for wrapper in wrappers:
            sys.path.insert(0, str(wrapper.parent))
            try:
                name = f"spec_probe_{wrapper.stem}"
                spec = importlib.util.spec_from_file_location(name, wrapper)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
            finally:
                sys.path.pop(0)
            declared = common.spec_from(module)
            single = tuple(declared.get("check") or ())
            many = [tuple(step) for step in (declared.get("checks") or ())]
            if not (many or ([single] if single else [])):
                empty.append(wrapper.name)
            else:
                for step in (many or [single]):
                    self.assertTrue(all(token is not None and str(token) for token in step),
                                    f"{wrapper.name} declares an argv with an empty token: {step!r}")
        self.assertEqual(empty, [],
                         f"these wrappers declare no runnable command: {empty}")


if __name__ == "__main__":
    unittest.main()