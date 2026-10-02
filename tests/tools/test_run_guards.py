"""Contract tests for `gk-core/scripts/run_guards.py`.

Asserts the CONTRACT: the CLI surface, every registry refusal, the selection rules, the `{ciRange}`
substitution and its DROP-THE-SWITCH-TOO rule, the extension dispatch and its fail-closed default, the
interpreter probe (by the interpreter each script ACTUALLY dispatches to), the "every guard runs even
after one fails" loop, and the envelope -- which carries the per-guard results on a RED run, because a
differential against the original caught the port discarding them.

WHY THE RESULTS MUST SURVIVE A RED RUN
The original prints its guard table BEFORE it throws, so a reader of a red run sees which guards ran and
what each returned. The first version of this port raised on the red verdict and discarded the whole
result set with it, and the differential against `run-guards.ps1` reported zero guards on every tier
where guards were red. `a_red_run_STILL_reports_every_guard` is that defect, pinned.

WHY EVERY SELECTED GUARD RUNS
One red guard must never hide a second: that is the masking defect `ci.yml` met once already, and it is
the reason the dispatch is a LOOP that collects every result rather than a fail-fast chain.
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
SCRIPT = Path(os.environ.get("RUN_GUARDS_SCRIPT", REPO / "scripts" / "run_guards.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("run_guards", SCRIPT)
rg = importlib.util.module_from_spec(_spec)
sys.modules["run_guards"] = rg
_spec.loader.exec_module(rg)

EXIT_VOCABULARY = {0, 1, 64}

# The registry refusals, as a CLOSED vocabulary. A new reason must be added here, so the set is a
# contract rather than a growing pile of strings nobody compares against anything.
REGISTRY_REASONS = {
    "REGISTRY-MISSING", "REGISTRY-UNREADABLE", "REGISTRY-SCHEMA", "CATALOG-EMPTY",
    "GUARD-EVIDENCE-MISSING", "GUARD-TIER-INVALID", "GUARD-STATUS-INVALID",
    "EXEMPTION-INCOMPLETE", "EXEMPTION-CATCH-ALL",
}
OTHER_REASONS = {
    "CI-RANGE-REQUIRED", "CI-RANGE-MALFORMED", "CI-RANGE-UNRESOLVED",
    "UNKNOWN-GUARD", "NO-GUARDS-SELECTED",
    "NO-GATING-GUARDS", "GUARD-SCRIPT-MISSING", "GUARD-RANGE-REQUIRED", "POWERSHELL-NOT-ON-PATH",
    "LOCAL-ARG-MALFORMED",
}


def code_without_bare_docstrings(text: str) -> str:
    tree = ast.parse(text)
    bare = {id(n.value) for n in ast.walk(tree)
            if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant)
            and isinstance(n.value.value, str)}
    parts: list[str] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if id(node) not in bare:
                parts.append(node.value)
        elif isinstance(node, (ast.Name, ast.Attribute)):
            parts.append(getattr(node, "id", None) or getattr(node, "attr", ""))
    return " ".join(parts)


def guard_row(script: str, tier: str = "local", status: str = "gating", **extra) -> dict:
    row = {"script": script, "tier": tier, "status": status}
    row.update(extra)
    return row


class Root:
    """A throwaway repository whose registry is a real document, so every refusal is reached for real."""

    def __init__(self, path: Path, guards: dict | None = None, exemptions: list | None = None) -> None:
        self.path = path
        (path / "scripts").mkdir(parents=True, exist_ok=True)
        self.write_registry(guards if guards is not None else {
            "green": guard_row("scripts/green-guard.py"),
        }, exemptions or [])

    def write_registry(self, guards: dict, exemptions: list) -> None:
        doc = {"schemaVersion": 1, "guards": guards}
        if exemptions:
            doc["verificationExemptions"] = exemptions
        (self.path / "scripts" / "enforcement-registry.v1.json").write_text(
            json.dumps(doc, indent=2), encoding="utf-8")

    def guard(self, name: str, body: str = "import sys\nsys.exit(0)\n") -> Path:
        path = self.path / "scripts" / name
        path.write_text(body, encoding="utf-8")
        return path


class TemporaryRoot(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="run-guards-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()
        self.repo = Root(self.root)
        self.addCleanup(self._tmp.cleanup)

    def invoke(self, *args: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = rg.main(["--root", str(self.root), *args])
        return code, out.getvalue(), err.getvalue()

    def spawns(self, codes: dict[str, int] | None = None) -> list[list[str]]:
        """Record the GUARD spawns and let the `git rev-parse` PROBE through to a real answer.

        Mocking the whole `subprocess` module is mocking a tool the subject ALSO uses: `repo_is_git`
        shells out once per dispatch, so a case recording every call saw one extra, and a case asserting
        "no guard may run" saw the probe and failed. Both were this helper's fault, not the tool's.

        Guards are matched on the PROJECT FILENAME in the argv ELEMENT, never by a substring of the
        joined command -- a temp directory's own name contains letters, so `"a" in " ".join(argv)`
        matched paths that had nothing to do with guard `a`.
        """
        recorded: list[list[str]] = []
        real_run = rg.subprocess.run

        def record(argv, **kwargs):
            args = list(argv)
            if args[:1] == ["git"]:
                return real_run(args, **kwargs)
            recorded.append(args)
            for name, code in (codes or {}).items():
                if any(a.endswith(f"{name}.py") for a in args):
                    return subprocess.CompletedProcess(args=args, returncode=code, stdout="", stderr="")
            return subprocess.CompletedProcess(args=args, returncode=0, stdout="", stderr="")

        patch = mock.patch.object(rg.subprocess, "run", side_effect=record)
        patch.start()
        self.addCleanup(patch.stop)
        return recorded

    def passing(self, *argv: str) -> None:
        """A `subprocess.run` that succeeds, so a case is about the RUNNER and not about a guard."""
        self.spawns()


# ------------------------------------------------------------------------------------------------
# The registry: every refusal, by name
# ------------------------------------------------------------------------------------------------

class TheRegistry(TemporaryRoot):
    def test_a_MISSING_registry_REFUSES_rather_than_selecting_nothing(self) -> None:
        (self.root / "scripts" / "enforcement-registry.v1.json").unlink()
        with self.assertRaises(rg.Refusal) as caught:
            rg.read_registry(self.root)
        self.assertEqual(caught.exception.reason, "REGISTRY-MISSING")

    def test_UNPARSEABLE_JSON_is_a_DISTINCT_refusal_from_a_MISSING_file(self) -> None:
        """A truncated registry and an absent one need different fixes, so they get different names."""
        (self.root / "scripts" / "enforcement-registry.v1.json").write_text("{ not json", encoding="utf-8")
        with self.assertRaises(rg.Refusal) as caught:
            rg.read_registry(self.root)
        self.assertEqual(caught.exception.reason, "REGISTRY-UNREADABLE")

    def test_a_WRONG_schemaVersion_REFUSES(self) -> None:
        self.repo.write_registry({"green": guard_row("scripts/green-guard.py")}, [])
        doc = json.loads((self.root / "scripts" / "enforcement-registry.v1.json").read_text())
        doc["schemaVersion"] = 2
        (self.root / "scripts" / "enforcement-registry.v1.json").write_text(json.dumps(doc))
        with self.assertRaises(rg.Refusal) as caught:
            rg.read_registry(self.root)
        self.assertEqual(caught.exception.reason, "REGISTRY-SCHEMA")

    def test_an_EMPTY_catalog_REFUSES_because_no_guards_means_no_evidence(self) -> None:
        self.repo.write_registry({}, [])
        with self.assertRaises(rg.Refusal) as caught:
            rg.read_registry(self.root)
        self.assertEqual(caught.exception.reason, "CATALOG-EMPTY")

    def test_a_guard_MISSING_its_evidence_REFUSES(self) -> None:
        """A guard with no `script`, `tier` or `status` cannot be selected meaningfully, and a catalog
        that quietly lost one would report a green run over fewer guards than anyone believes are
        gating -- so each of the three fields is checked separately."""
        for missing in ("script", "tier", "status"):
            row = guard_row("scripts/green-guard.py")
            del row[missing]
            self.repo.write_registry({"broken": row}, [])
            with self.assertRaises(rg.Refusal) as caught:
                rg.read_registry(self.root)
            self.assertEqual(caught.exception.reason, "GUARD-EVIDENCE-MISSING", missing)

    def test_an_INVALID_tier_or_status_REFUSES_rather_than_being_coerced(self) -> None:
        for field, value, reason in (("tier", "nightly", "GUARD-TIER-INVALID"),
                                     ("status", "maybe", "GUARD-STATUS-INVALID")):
            self.repo.write_registry({"bad": guard_row("scripts/green-guard.py", **{field: value})}, [])
            with self.assertRaises(rg.Refusal) as caught:
                rg.read_registry(self.root)
            self.assertEqual(caught.exception.reason, reason, field)

    def test_an_EXEMPTION_missing_id_paths_or_reason_REFUSES(self) -> None:
        for exemption, label in (({"paths": ["a"], "reason": "r"}, "id"),
                                 ({"id": "x", "reason": "r"}, "paths"),
                                 ({"id": "x", "paths": ["a"]}, "reason")):
            self.repo.write_registry({"green": guard_row("scripts/green-guard.py")}, [exemption])
            with self.assertRaises(rg.Refusal) as caught:
                rg.read_registry(self.root)
            self.assertEqual(caught.exception.reason, "EXEMPTION-INCOMPLETE", label)

    def test_a_CATCH_ALL_exemption_root_REFUSES(self) -> None:
        """A catch-all exemption makes the whole verification boundary vacuous, so the shapes that mean
        "everything" are refused by name rather than left to be discovered as a silent green."""
        for pattern in ("**", "scripts/**", "tools/**", ".github/**", "scripts/*/"):
            self.repo.write_registry(
                {"green": guard_row("scripts/green-guard.py")},
                [{"id": "x", "reason": "r", "paths": [pattern]}])
            with self.assertRaises(rg.Refusal) as caught:
                rg.read_registry(self.root)
            self.assertEqual(caught.exception.reason, "EXEMPTION-CATCH-ALL", pattern)

    def test_a_NARROW_exemption_is_ACCEPTED(self) -> None:
        """The counterweight: the refusal is about CATCH-ALLS, not about exemptions. A case that only
        asserted the refusal would pass with a rule that refuses every exemption."""
        self.repo.write_registry(
            {"green": guard_row("scripts/green-guard.py")},
            [{"id": "x", "reason": "r", "paths": ["scripts/one-file.py"]}])
        self.assertEqual(rg.read_registry(self.root)["guards"].keys(), {"green"})


# ------------------------------------------------------------------------------------------------
# The CI range
# ------------------------------------------------------------------------------------------------

class TheRange(TemporaryRoot):
    def test_a_CI_run_with_NO_range_REFUSES_because_a_missing_range_is_not_a_green_range(self) -> None:
        # A git repo, because the refusal depends on being one: a non-git fixture has no push range.
        with mock.patch.object(rg, "repo_is_git", return_value=True):
            with self.assertRaises(rg.Refusal) as caught:
                rg.resolve_ci_range("ci", "", self.root)
        self.assertEqual(caught.exception.reason, "CI-RANGE-REQUIRED")

    def test_a_MALFORMED_range_REFUSES_rather_than_passing_it_on(self) -> None:
        with mock.patch.object(rg, "repo_is_git", return_value=True):
            with self.assertRaises(rg.Refusal) as caught:
                rg.resolve_ci_range("ci", "not-a-range", self.root)
        self.assertEqual(caught.exception.reason, "CI-RANGE-MALFORMED")

    def test_a_WELL_FORMED_range_that_does_NOT_resolve_REFUSES_by_name(self) -> None:
        # The defect this pins, measured 2026-10-02: a base that is not a commit in THIS repository
        # was passed straight through, and the failure surfaced from whichever guard diffs first as
        # `GIT-FAILED git diff ... fatal: unknown revision`, reported as THAT GUARD BEING RED. A
        # cross-repository SHA read as a genuine generated-seed failure and was misdiagnosed as one
        # before the SHA was noticed. "0 red" therefore rested on a range string nobody had checked.
        with mock.patch.object(rg, "repo_is_git", return_value=True):
            with self.assertRaises(rg.Refusal) as caught:
                rg.resolve_ci_range("ci", "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef..HEAD", self.root)
        self.assertEqual(caught.exception.reason, "CI-RANGE-UNRESOLVED")

    def test_an_UNRESOLVED_head_REFUSES_too_not_just_the_base(self) -> None:
        with mock.patch.object(rg, "repo_is_git", return_value=True):
            with self.assertRaises(rg.Refusal) as caught:
                rg.resolve_ci_range("ci", "HEAD..nosuchhead0000000000000000000000", self.root)
        self.assertEqual(caught.exception.reason, "CI-RANGE-UNRESOLVED")

    def test_a_resolvable_range_is_PASSED_through_unchanged(self) -> None:
        # The refusal must not swallow a legitimate range. `repo_is_git` is mocked in this suite but
        # the `git rev-parse` probe is a REAL call, and TemporaryRoot is a plain directory rather
        # than a repository - so the probe has to be made to succeed explicitly. Asserting it
        # resolved on its own was my first attempt and it failed for exactly that reason.
        ok = subprocess.CompletedProcess(args=["git"], returncode=0, stdout="deadbeef\n", stderr="")
        with mock.patch.object(rg, "repo_is_git", return_value=True), \
             mock.patch.object(rg.subprocess, "run", return_value=ok):
            self.assertEqual(rg.resolve_ci_range("ci", "HEAD~1..HEAD", self.root), "HEAD~1..HEAD")

    def test_an_UNRESOLVED_range_still_REFUSES_when_the_probe_really_fails(self) -> None:
        # Same real-call caveat, the other way: a probe returning non-zero must produce the refusal
        # rather than being waved through, so the two cases cannot both pass by accident.
        fail = subprocess.CompletedProcess(args=["git"], returncode=1, stdout="", stderr="fatal: bad revision")
        with mock.patch.object(rg, "repo_is_git", return_value=True), \
             mock.patch.object(rg.subprocess, "run", return_value=fail):
            with self.assertRaises(rg.Refusal) as caught:
                rg.resolve_ci_range("ci", "HEAD~1..HEAD", self.root)
        self.assertEqual(caught.exception.reason, "CI-RANGE-UNRESOLVED")

    def test_a_LOCAL_run_with_NO_range_is_LEGAL(self) -> None:
        with mock.patch.object(rg, "repo_is_git", return_value=True):
            self.assertEqual(rg.resolve_ci_range("local", "", self.root), "")

    def test_a_NON_GIT_root_does_NOT_demand_a_range(self) -> None:
        """The refusal depends on being a real checkout, so a planted fixture stays testable -- and the
        converse is checked so the escape cannot widen into a CI run."""
        with mock.patch.object(rg, "repo_is_git", return_value=False):
            self.assertEqual(rg.resolve_ci_range("ci", "", self.root), "")


# ------------------------------------------------------------------------------------------------
# Selection
# ------------------------------------------------------------------------------------------------

class TheSelection(TemporaryRoot):
    def setUp(self) -> None:
        super().setUp()
        self.catalog = {
            "a": guard_row("scripts/a.py"),
            "b": guard_row("scripts/b.py", tier="ci"),
            "backlog": guard_row("scripts/backlog.py", status="backlog"),
            "own-step": guard_row("scripts/own.py", tier="ci", ciEntry="own-step"),
        }

    def test_a_LOCAL_tier_is_the_WIDEST_and_ignores_the_declared_tier(self) -> None:
        """THE TIER FILTER IS ONE-DIRECTIONAL, and that is the whole point of the case.

        It applies to a `ci` run only. A `local` run takes every non-backlog guard whatever tier the
        registry declares, because `local` is the WIDEST tier -- every `ci` guard plus the machine-only
        ones. An earlier version of this case asserted the opposite ("a local tier selects gating LOCAL
        guards only"), which is the rule a reader would GUESS and the tool correctly does not implement;
        a test that asserts a guessed rule is worse than no test, because satisfying it would REMOVE
        guards from the developer's own loop, which is the silent coverage loss this program's own
        comments keep naming.
        """
        self.assertEqual(rg.select(self.catalog, "local", [], [], False), ["a", "b", "own-step"])

    def test_a_CI_tier_selects_CI_tier_and_keeps_an_own_step_guard_OUT(self) -> None:
        """`ci.yml` runs an own-step guard in its own isolated step; the runner must not run it twice."""
        self.assertEqual(rg.select(self.catalog, "ci", [], [], False), ["b"])

    def test_backlog_needs_the_FLAG_and_never_gates(self) -> None:
        self.assertIn("backlog", rg.select(self.catalog, "local", [], [], True))
        self.assertNotIn("backlog", rg.select(self.catalog, "local", [], [], False))

    def test_SKIP_excludes_from_the_TIER_batch_only(self) -> None:
        """`-Skip` removes a CALLER-INVOKED guard from the tier batch only; `--only` still selects it.

        That asymmetry is deliberate -- the one caller-supplied exception is a guard a caller runs itself
        at a position that is part of its meaning, and it is never used to silence a gating guard in CI.
        """
        self.assertEqual(rg.select(self.catalog, "local", [], ["a"], False), ["b", "own-step"])
        self.assertEqual(rg.select(self.catalog, "local", ["a"], ["a"], False), ["a"])

    def test_ONLY_selects_EXACTLY_those_and_bypasses_the_tier_filters(self) -> None:
        self.assertEqual(rg.select(self.catalog, "local", ["backlog", "b"], [], False), ["backlog", "b"])

    def test_an_UNKNOWN_guard_in_ONLY_REFUSES_rather_than_selecting_nothing(self) -> None:
        with self.assertRaises(rg.Refusal) as caught:
            rg.select(self.catalog, "local", ["nope"], [], False)
        self.assertEqual(caught.exception.reason, "UNKNOWN-GUARD")

    def test_an_EMPTY_selection_is_RED(self) -> None:
        with self.assertRaises(rg.Refusal) as caught:
            rg.check_selection([], self.catalog, "local", [], False)
        self.assertEqual(caught.exception.reason, "NO-GUARDS-SELECTED")

    def test_a_CI_run_with_no_GATING_guard_is_RED(self) -> None:
        """An evidence-free guard run must not print an empty table and exit 0."""
        backlog_only = {"b": guard_row("scripts/b.py", tier="ci", status="backlog")}
        with self.assertRaises(rg.Refusal) as caught:
            rg.check_selection(["b"], backlog_only, "ci", [], False)
        self.assertEqual(caught.exception.reason, "NO-GATING-GUARDS")

    def test_the_NO_GATING_refusal_does_not_fire_when_backlog_was_EXPLICITLY_asked_for(self) -> None:
        backlog_only = {"b": guard_row("scripts/b.py", tier="ci", status="backlog")}
        rg.check_selection(["b"], backlog_only, "ci", [], True)  # must not raise


# ------------------------------------------------------------------------------------------------
# Arguments
# ------------------------------------------------------------------------------------------------

class TheArguments(unittest.TestCase):
    def test_the_CI_RANGE_placeholder_is_SUBSTITUTED(self) -> None:
        row = guard_row("scripts/g.py", args={"ci": ["-Range", "{ciRange}"]})
        self.assertEqual(rg.resolve_guard_args("g", row, "ci", "base..head", {}, True),
                         ["-Range", "base..head"])

    def test_an_EMPTY_range_DROPS_the_switch_AND_its_placeholder(self) -> None:
        """Keeping the switch without its value hands a guard `-Range` and nothing after it, and the
        failure is an argparse error from a guard that never had a chance to run. BOTH spellings are
        covered because the catalog holds a PowerShell guard and a Python one."""
        for switch in rg.RANGE_SWITCHES:
            row = guard_row("scripts/g.py", args={"local": [switch, "{ciRange}", "--after"]})
            self.assertEqual(rg.resolve_guard_args("g", row, "local", "", {}, False), ["--after"],
                             switch)

    def test_a_CI_guard_that_NEEDS_a_range_REFUSES_rather_than_dropping_it(self) -> None:
        """The drop is for a non-git fixture. A real CI checkout reaching it is a caller bug, and
        silently dropping the switch there would run the guard over the wrong diff."""
        row = guard_row("scripts/g.py", args={"ci": ["-Range", "{ciRange}"]})
        with self.assertRaises(rg.Refusal) as caught:
            rg.resolve_guard_args("g", row, "ci", "", {}, True)
        self.assertEqual(caught.exception.reason, "GUARD-RANGE-REQUIRED")

    def test_LOCAL_args_are_APPENDED_per_guard_and_addressed_BY_NAME(self) -> None:
        row = guard_row("scripts/g.py")
        # THE GUARD'S OWN DIALECT, which is what the switch is FOR. This line used to assert
        # ["-GameDir", "C:/game"] while calling the `.py` branch, so it pinned the exact defect the
        # runner's own docstring records as fixed: `-Key` was emitted unconditionally, the PowerShell
        # spelling, while `.py` guards were dispatched through sys.executable - so argparse got
        # `-GameDir` where it wanted `--game-dir` and the guard exited 2 with `unrecognized
        # arguments`, reported as a red guard on a run that had in fact passed both values.
        # The name of this test is about APPENDING and BY NAME, and both halves are still asserted.
        got = rg.resolve_guard_args("g", row, "local", "", {"g": {"GameDir": "C:/game"}}, False)
        self.assertEqual(got, ["--GameDir", "C:/game"])
        # and the switch tracks the extension rather than being pinned. The extension here is
        # deliberately a language no guard in this workspace uses: the assertion is about the
        # derivation, not about resurrecting a dialect. A `.py` guard is the only kind that ships.
        self.assertEqual(
            rg.resolve_guard_args("g", row, "local", "", {"g": {"GameDir": "C:/game"}}, False,
                                  extension=".rb"),
            ["-GameDir", "C:/game"])
        # and they do not leak to a guard that was not addressed
        self.assertEqual(rg.resolve_guard_args("other", row, "local", "", {"g": {"x": "1"}}, False), [])

    def test_a_MALFORMED_local_arg_REFUSES_rather_than_being_dropped(self) -> None:
        for entry in ("no-colon", "id:no-equals", ":k=v", "id:=v"):
            with self.assertRaises(rg.Refusal) as caught:
                rg.parse_local_args([entry])
            self.assertEqual(caught.exception.reason, "LOCAL-ARG-MALFORMED", entry)


# ------------------------------------------------------------------------------------------------
# Dispatch: the loop, the extension, and the interpreter probe
# ------------------------------------------------------------------------------------------------

class TheDispatch(TemporaryRoot):
    def test_a_PY_guard_is_RUN_with_this_INTERPRETER(self) -> None:
        self.repo.guard("g.py")
        catalog = {"g": guard_row("scripts/g.py")}
        seen = self.spawns()
        rg.dispatch(self.root, ["g"], catalog, "local", "", {}, 5)
        self.assertEqual(len(seen), 1)
        self.assertEqual(seen[0][0], sys.executable, "a .py guard runs under THIS interpreter")
        self.assertTrue(seen[0][1].endswith("g.py"))

    def test_a_PS_guard_is_RUN_with_pwsh_and_the_ALTERNATE_name(self) -> None:
        self.repo.guard("g.ps1", "exit 0\n")
        catalog = {"g": guard_row("scripts/g.ps1")}
        seen = self.spawns()
        for interpreter in ("pwsh", "powershell"):
            with mock.patch.object(rg.shutil, "which", return_value=f"/usr/bin/{interpreter}"):
                rg.dispatch(self.root, ["g"], catalog, "local", "", {}, 5)
            self.assertEqual(seen[-1][0], f"/usr/bin/{interpreter}")

    def test_an_UNDISPATCHABLE_extension_fails_CLOSED_with_64_AND_an_EXPLANATION(self) -> None:
        """A guard that silently does not run is worse than one that refuses: the registry said it gates,
        so its absence must be visible. The explanation goes in the REPORT, because the original wrote
        it to a log it then deleted unconditionally."""
        (self.root / "scripts" / "g.sh").write_text("#!/bin/sh\n", encoding="utf-8")
        catalog = {"g": guard_row("scripts/g.sh")}
        report = rg.dispatch(self.root, ["g"], catalog, "local", "", {}, 5)
        row = report.results[0]
        self.assertEqual(row["exit"], rg.EXIT_UNDISPATCHABLE)
        self.assertEqual(report.undispatchable, ["g"])
        self.assertIn("unsupported extension", row["stderr"])

    def test_EVERY_GUARD_RUNS_even_after_one_FAILS(self) -> None:
        """The masking defect `ci.yml` met once: one red guard must never hide a second."""
        for name in ("a", "b", "c"):
            self.repo.guard(f"{name}.py")
        catalog = {n: guard_row(f"scripts/{n}.py") for n in ("a", "b", "c")}
        self.spawns(codes={"a": 0, "b": 1, "c": 2})
        report = rg.dispatch(self.root, ["a", "b", "c"], catalog, "local", "", {}, 5)
        self.assertEqual([r["id"] for r in report.results], ["a", "b", "c"])
        self.assertEqual([r["exit"] for r in report.results], [0, 1, 2])

    def test_a_WEDGED_guard_is_a_NAMED_timeout_not_a_hang(self) -> None:
        """The original had no timeout on any guard invocation, and this runner is the longest thing in
        CI -- so a wedged guard held the whole run open and the only signal was the absence of a row."""
        self.repo.guard("g.py")
        catalog = {"g": guard_row("scripts/g.py")}
        with mock.patch.object(rg.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired(cmd="x", timeout=11)):
            report = rg.dispatch(self.root, ["g"], catalog, "local", "", {}, 11)
        self.assertEqual(report.results[0]["exit"], rg.EXIT_UNDISPATCHABLE)
        self.assertIn("timed out after 11s", report.results[0]["stderr"])
        self.assertIn("g", report.results[0]["stderr"], "the timeout must NAME the guard")

    def test_a_guard_runs_FROM_the_root(self) -> None:
        """A Python guard is a plain script that opens relative paths; measured from anywhere else it
        fails closed with "no such path(s): ['src']". That is the runner's contract."""
        self.repo.guard("g.py")
        catalog = {"g": guard_row("scripts/g.py")}
        seen: list[str] = []
        real_run = rg.subprocess.run

        def record(argv, **kwargs):
            if list(argv)[:1] == ["git"]:
                return real_run(argv, **kwargs)
            seen.append(str(kwargs["cwd"]))
            return subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")

        with mock.patch.object(rg.subprocess, "run", side_effect=record):
            rg.dispatch(self.root, ["g"], catalog, "local", "", {}, 5)
        self.assertEqual(Path(seen[0]).resolve(), self.root.resolve())

    def test_the_interpreter_probe_asks_for_what_is_ACTUALLY_DISPATCHED(self) -> None:
        """The original probed for `powershell` while dispatching on `pwsh` first, so a PowerShell-7-only
        machine -- the normal case off Windows -- got a warning naming an interpreter it never needed.
        A warning that cries wolf on the correct platform trains its reader to skip it."""
        with mock.patch.object(rg.shutil, "which", return_value="/usr/bin/anything"):
            self.assertEqual(rg.missing_interpreters(["scripts/g.py"], []), [])
            self.assertEqual(rg.missing_interpreters(["scripts/g.ps1"], []), [])
            self.assertEqual(rg.missing_interpreters(["scripts/g.txt"], []), [])
        with mock.patch.object(rg.shutil, "which", return_value=None):
            self.assertEqual(rg.missing_interpreters(["scripts/g.py"], []), ["python"])
            self.assertEqual(rg.missing_interpreters(["scripts/g.ps1"], []), ["powershell"])

    def test_EVERY_external_command_goes_through_subprocess_run_from_the_dispatch_LOOP(self) -> None:
        """Structural, by AST: the dispatch is the only place a guard is spawned, so "every guard is
        bounded and its exit code collected" is a fact about the control flow."""
        sites = [n for n in ast.walk(ast.parse(SCRIPT.read_text(encoding="utf-8")))
                 if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                 and n.func.attr == "run" and isinstance(n.func.value, ast.Name)
                 and n.func.value.id == "subprocess"]
        enclosing = {id(n) for n in ast.walk(ast.parse(SCRIPT.read_text(encoding="utf-8")))
                     if isinstance(n, ast.Call)}
        # every spawn except the one inside `repo_is_git` (a one-shot probe, not a guard) is in dispatch
        in_dispatch = 0
        for node in ast.walk(ast.parse(SCRIPT.read_text(encoding="utf-8"))):
            if isinstance(node, ast.FunctionDef) and node.name == "dispatch":
                in_dispatch = sum(1 for c in ast.walk(node)
                                  if isinstance(c, ast.Call) and isinstance(c.func, ast.Attribute)
                                  and c.func.attr == "run" and isinstance(c.func.value, ast.Name)
                                  and c.func.value.id == "subprocess")
        self.assertEqual(in_dispatch, 1,
                         "the guard spawn must be one call site inside dispatch(), so a second one is "
                         "a guard that escaped the timeout and the result collection")
        self.assertGreaterEqual(len(sites), 2, "dispatch and the git probe are the only two")


# ------------------------------------------------------------------------------------------------
# The envelope
# ------------------------------------------------------------------------------------------------

class TheEnvelope(TemporaryRoot):
    def test_a_red_run_STILL_reports_EVERY_guard(self) -> None:
        """The defect the differential against the original caught: the port raised on the red verdict
        and discarded the whole result set, so a red run reported ZERO guards where the original reported
        all 29. The original prints its table BEFORE it throws."""
        for name in ("a", "b"):
            self.repo.guard(f"{name}.py")
        self.repo.write_registry({"a": guard_row("scripts/a.py"),
                                  "b": guard_row("scripts/b.py")}, [])
        self.spawns(codes={"a": 0, "b": 1})
        code, out, _ = self.invoke("--tier", "local", "--json")
        self.assertNotEqual(code, 0)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "FAILED", "a red run COMPLETED; it is not a refusal")
        self.assertEqual([r["id"] for r in payload["results"]], ["a", "b"])
        self.assertEqual([r["exit"] for r in payload["results"]], [0, 1])
        self.assertIn("b", payload["summary"])

    def test_a_GREEN_run_answers_OK_and_0(self) -> None:
        self.repo.guard("g.py")
        self.repo.write_registry({"g": guard_row("scripts/g.py")}, [])
        self.passing()
        code, out, _ = self.invoke("--tier", "local", "--json")
        self.assertEqual(code, 0)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "OK")
        self.assertEqual(payload["exitCode"], 0)
        self.assertIn("0 red", payload["summary"])

    def test_a_REFUSAL_is_REFUSED_and_reports_no_results(self) -> None:
        """The counterpart: a run that could not PROCEED has no result set, and that is the difference
        between REFUSED and FAILED."""
        (self.root / "scripts" / "enforcement-registry.v1.json").unlink()
        code, out, _ = self.invoke("--tier", "local", "--json")
        self.assertNotEqual(code, 0)
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["stage"], "registry")
        self.assertEqual(payload["reason"], "REGISTRY-MISSING")
        self.assertNotIn("results", payload)

    def test_a_json_REFUSAL_prints_NO_prose_and_a_TEXT_REFUSAL_prints_NO_json(self) -> None:
        self.repo.write_registry({"g": guard_row("scripts/g.py")}, [])
        _, out, err = self.invoke("--tier", "local", "--only", "nope", "--json")
        self.assertEqual(err, "", "a --json refusal must not also print prose on stderr")
        self.assertEqual(json.loads(out)["reason"], "UNKNOWN-GUARD")
        _, out2, err2 = self.invoke("--tier", "local", "--only", "nope")
        self.assertEqual(out2, "", "a text refusal must not print a json document on stdout")
        self.assertIn("UNKNOWN-GUARD", err2)

    def test_the_envelope_carries_KEYS_and_nothing_that_ROTS(self) -> None:
        self.repo.guard("g.py")
        self.repo.write_registry({"g": guard_row("scripts/g.py")}, [])
        self.passing()
        _, out, _ = self.invoke("--tier", "local", "--json")
        report = json.loads(out)
        self.assertEqual(set(report), {"tool", "verdict", "summary", "exitCode", "tier", "ci_range",
                                       "selected", "results", "red_gating", "backlog",
                                       "undispatchable", "missing_interpreters", "environment_note",
                                       "stages"})
        for banned in ("pid", "duration", "elapsed", "timestamp", "started", "finished", "seed"):
            self.assertNotIn(banned, report, f"{banned!r} differs per run, so nothing can assert on it")

    def test_a_MISSING_guard_SCRIPT_is_REFUSED_BEFORE_any_guard_runs(self) -> None:
        """One refusal naming it, rather than a red row discovered half-way through a batch whose
        earlier guards have already been reported."""
        self.repo.guard("a.py")
        self.repo.write_registry({"a": guard_row("scripts/a.py"),
                                  "gone": guard_row("scripts/gone.py")}, [])
        calls = self.spawns()
        code, out, err = self.invoke("--tier", "local", "--json")
        self.assertNotEqual(code, 0)
        self.assertEqual(json.loads(out)["reason"], "GUARD-SCRIPT-MISSING")
        self.assertEqual(calls, [], "no guard may run once the catalog is known to be incomplete")


# ------------------------------------------------------------------------------------------------
# Surface
# ------------------------------------------------------------------------------------------------

class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--tier", "--only", "--skip", "--include-backlog", "--local-arg", "--ci-range",
                     "--root", "--timeout", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        for flag, value in (("-Tier", "ci"), ("-Only", "g"), ("-CiRange", "a..b"), ("-Root", "x")):
            proc = subprocess.run([sys.executable, str(SCRIPT), flag, value], capture_output=True,
                                 text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, flag)
            self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower(), flag)

    def test_it_answers_no_INVALID_tier(self) -> None:
        proc = subprocess.run([sys.executable, str(SCRIPT), "--tier", "nightly"], capture_output=True,
                              text=True, timeout=RUN_TIMEOUT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("invalid choice", (proc.stderr + proc.stdout).lower())

    def test_it_shells_out_to_no_PowerShell_INTERPRETER_except_to_dispatch_a_PS_guard(self) -> None:
        """`pwsh` appears ONLY as the interpreter a `.ps1` guard is dispatched to. That is the one job it
        has left, and the rest of the tool is interpreter-agnostic."""
        code = code_without_bare_docstrings(SCRIPT.read_text(encoding="utf-8"))
        for token in ("-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE", "Assert-NativeExit"):
            self.assertNotIn(token, code, f"the tool still uses {token!r} in CODE")

    def test_the_timeout_is_declared_and_says_the_original_had_none(self) -> None:
        flat = " ".join(subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True,
                                       text=True, timeout=RUN_TIMEOUT).stdout.split()).lower()
        self.assertIn("timeout", flat)
        self.assertIn("original had no timeout", flat)

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        found = set(re.findall(r'Refusal\(\s*"[a-z-]+",\s*"([A-Z][A-Z0-9-]+)"',
                               SCRIPT.read_text(encoding="utf-8")))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - (REGISTRY_REASONS | OTHER_REASONS), set(),
                         f"undocumented refusal reason(s) {sorted(found - REGISTRY_REASONS - OTHER_REASONS)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({rg.EXIT_OK, rg.EXIT_FAILED, rg.EXIT_UNDISPATCHABLE}, EXIT_VOCABULARY)

    def test_every_declared_stage_is_in_the_vocabulary(self) -> None:
        # No `summary` stage: `summarise` RETURNS a verdict rather than raising, so it cannot refuse
        # and a stage that cannot fail is not a stage.
        stages = {"registry", "arguments", "range", "selection", "catalog", "interpreters", "dispatch"}
        self.assertTrue(stages >= set(rg.STAGES), f"missing: {sorted(set(rg.STAGES) - stages)}")

    def test_the_catalog_is_not_asserted_by_LENGTH_anywhere(self) -> None:
        """A guard that pins a population count guards nothing: it fails when a guard ships, and the
        'fix' is to bump the number. The contract suite asserts the CLOSED vocabulary instead."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn(f"== {len(rg.RANGE_SWITCHES)}", source)
        self.assertNotIn(f"== {len(rg.STAGES)}", source)


if __name__ == "__main__":
    unittest.main()
