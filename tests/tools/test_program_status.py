"""Tests for `gk-core/scripts/program_status.py` — the status reader every agent is told to read.

The load-bearing property is not "it prints a number". It is that the number is the SAME number the
established block rule produces, that a program the marker map does not cover is never reported as
zero, and that a broken input is a NAMED refusal with a non-zero exit rather than an empty report —
because an empty report read as success is the failure this tool exists to prevent.

Substrate: temp directories only, deleted in `tearDown` with the delete asserted (never swallowed),
per `docs/contributing/testing-standard.md`. No store, no network, no game.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import root_carrying  # noqa: E402


def _owned(relative: str) -> Path:
    """The file `relative`, in the repository that actually OWNS it.

    <para>TWO of this module's four constants moved out of gk-core in the split, and a constant built
    from `REPO` names gk-core whatever moved. `BOUNDARY` became gk-workflow's
    `session-boundary-check.py`; `ACCEPT` became gk-workflow's `accept_lane.py`, the twin of
    `accept-lane.ps1`. Neither file exists in gk-core at all, so both constants pointed at nothing,
    and the drift guards below were reading a path with no file behind it - which is why six tests
    failed on an owner that had simply gone somewhere else.

    <para>The point of these guards is that they read the OWNER rather than a copy of the owner's
    copy, so a hard-coded path defeats the thing being tested. Raising rather than falling back is
    deliberate: a fallback would substitute `REPO / relative`, which is exactly the bug, and a guard
    that cannot find its owner must say so instead of reading nothing and reporting a pass.
    """
    root = root_carrying(REPO, relative)
    if root is None:
        raise RuntimeError(
            f"no repository in this workspace carries {relative!r}, so its owner cannot be read")
    return root.joinpath(*relative.replace("\\", "/").split("/"))


TOOL = REPO / "scripts" / "program_status.py"
RULE = REPO / "scripts" / "audit-program-pipeline.py"
BOUNDARY = _owned("scripts/session-boundary-check.py")
# The acceptance gate's owner. It was `accept-lane.ps1` until the port; the vocabulary moved to
# the twin and the drift guard moved with it, because the POINT of this test is that it reads the
# OWNER rather than a copy of the owner's copy.
ACCEPT = _owned(".claude/cmdc-agents/scripts/accept_lane.py")


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    # `@dataclass` resolves annotations through sys.modules[cls.__module__]; without this registration
    # an import-by-path raises AttributeError from inside dataclasses instead of loading.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


status_mod = _load(TOOL, "program_status_under_test")
rule_mod = _load(RULE, "audit_program_pipeline_under_test")

R_BOLD_TODO = """# Todo: `alpha` (prefix `A`)

- [x] **A1.1 — the shipped one** · S
  - [ ] acceptance box left unticked on purpose (its original contract)
  - Acceptance: proven.

- [ ] **A1.2 — the open one** · M
  - [ ] acceptance box for work not started
  - Acceptance: not yet.
"""

# `shapes=NO_MAP` means "write no marker map at all"; `{}` means "write an empty one".
NO_MAP = object()


def _fixture_repo(root: Path, *, todos: dict[str, str], shapes: object = NO_MAP,
                  sessions: dict[str, dict] | None = None, accept: dict[str, dict] | None = None,
                  git: bool = True) -> Path:
    """Build a minimal repository the tool accepts."""
    (root / "tasks" / "sessions").mkdir(parents=True, exist_ok=True)
    (root / "scripts" / "lib").mkdir(parents=True, exist_ok=True)
    # The block rule imports its own resolver shim, so the fake repo needs it too — a fixture that
    # omits it makes the tool fail for a reason the test does not own.
    (root / "scripts" / "audit-program-pipeline.py").write_bytes(RULE.read_bytes())
    (root / "scripts" / "lib" / "keepverse_roots.py").write_bytes(
        (REPO / "scripts" / "lib" / "keepverse_roots.py").read_bytes())
    for name, text in todos.items():
        (root / "tasks" / name).write_text(text, encoding="utf-8")
    if shapes is not NO_MAP:
        (root / "scripts" / "todo-shapes.v1.json").write_text(
            json.dumps({f"tasks/{k}": {"shape": v, "exemplar": f"{v} exemplar"} for k, v in shapes.items()}),
            encoding="utf-8")
    for name, body in (sessions or {}).items():
        (root / "tasks" / "sessions" / f"{name}.json").write_text(json.dumps(body), encoding="utf-8")
    for name, body in (accept or {}).items():
        target = root / ".claude" / "cmdc-agents" / "acceptance"
        target.mkdir(parents=True, exist_ok=True)
        (target / f"{name}.json").write_text(json.dumps(body), encoding="utf-8")
    if git:
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-qm", "fixture"],
                       cwd=root, check=True)
    return root


class TempRepoTest(unittest.TestCase):
    def setUp(self) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()

    def fixture(self, **kwargs) -> Path:
        return _fixture_repo(self.root, **kwargs)


class BlockIdentityTests(unittest.TestCase):
    """The id grammar. A label that returns an English word makes the whole report unreadable."""

    def test_reads_a_leading_task_id_through_emphasis(self):
        # The grammar consumes the separator it matched (`\s*[.:·—–-]`), so the title starts after it.
        self.assertEqual(rule_mod.block_identity("- [ ] **EPL1.1 — the closed enum** · S · deps: —"),
                         ("EPL1.1", " the closed enum** · S · deps: —"))

    def test_reads_a_leading_id_in_a_bracket_heading(self):
        task_id, _ = rule_mod.block_identity("### [ ] 0a.1 `synthetic-graph` — a valid world")
        self.assertEqual(task_id, "0a.1")

    def test_a_prose_heading_gets_no_id_rather_than_a_word(self):
        for head in ("### Task 1: reconcile source specs", "## Wave 1 — the first",
                     "- [ ] **Owner shown the map**", "## Full suite green"):
            self.assertEqual(rule_mod.block_identity(head)[0], "", head)

    def test_title_strips_emphasis_and_caps_length(self):
        self.assertEqual(rule_mod.block_title("**a** `b` and\n  c"), "a b and c")
        self.assertLessEqual(len(rule_mod.block_title("x" * 400, limit=90)), 90)


class BlockRuleAgreementTests(unittest.TestCase):
    """The per-block view and the counting view must never disagree — that is a refusal, not a number."""

    def test_enumeration_matches_the_counting_view_on_every_declared_shape(self):
        samples = {
            "H-task": "## Task 1: one\n\n- [x] done box\n\n## Task 2: two (OPEN)\n",
            "R-bold": R_BOLD_TODO,
            "R-plain": "- [x] plain done\n- [ ] plain open\n",
            "H-checkbox-heading": "## - [x] shipped\n\n## - [ ] open\n",
            "H-bracket": "### [x] one\n\n### [ ] two\n",
            "H-id": "## T3.1: one\n\nbody\n\n## T3.2: two\n",
        }
        for shape, text in samples.items():
            blocks = rule_mod.iter_task_blocks(text, shape)
            opened, done, unticked, shaded = rule_mod.task_blocks(text, shape)
            self.assertEqual(len([b for b in blocks if not b.done]), opened, shape)
            self.assertEqual(len([b for b in blocks if b.done]), done, shape)
            self.assertEqual(sum(b.shaded_boxes for b in blocks), shaded, shape)
            self.assertEqual(unticked, sum(1 for line in text.splitlines() if rule_mod.BOX_OPEN_RE.match(line)))

    def test_an_undeclared_shape_yields_no_blocks_and_no_count(self):
        self.assertEqual(rule_mod.iter_task_blocks("## Task 1: one\n", "none"), [])
        self.assertEqual(rule_mod.task_blocks("## Task 1: one\n", "none"), (0, 0, 0, 0))


class ReportTests(TempRepoTest):
    def test_reads_done_and_open_blocks_by_task_not_by_checkbox_line(self):
        root = self.fixture(todos={"alpha-todo.md": R_BOLD_TODO},
                            shapes={"alpha-todo.md": "R-bold"},
                            sessions={"alpha-20260926": {
                                "session": "alpha-20260926", "program": "alpha", "problem": "p",
                                "mode": "direct", "branch": "master", "worktree": None,
                                "paths": ["tasks/alpha-todo.md"], "started": "2026-09-26T00:00:00Z",
                                "status": "active"}},
                            accept={"alpha-20260926-aaaaaaa": {
                                "lane": "alpha-20260926", "sha": "aaaaaaa" + "0" * 32,
                                "verdict": "GREEN", "scope": "alpha", "schemaVersion": 2}})
        report = status_mod.build_report(root, timeout=60)
        self.assertEqual(report.totals["openBlocks"], 1)
        self.assertEqual(report.totals["doneBlocks"], 1)
        # 3 unticked boxes exist (one inside the shipped task, two acceptance boxes) — and none of
        # them is counted as work, which is the rule this whole tool exists to enforce.
        self.assertEqual(report.programs[0]["untickedBoxes"], 3)
        self.assertEqual(report.programs[0]["open"][0]["id"], "A1.2")
        self.assertEqual(report.programs[0]["fencedByActiveSessions"], ["alpha-20260926"])

    def test_an_unmeasured_program_is_listed_and_never_counted_as_zero_open(self):
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO, "beta-todo.md": "## Task 1: prose only\n"},
            shapes={"alpha-todo.md": "R-bold", "beta-todo.md": "none"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha", "problem": "p",
                                         "mode": "direct", "branch": "master", "worktree": None,
                                         "paths": ["tasks/alpha-todo.md"], "started": "2026-09-26T00:00:00Z",
                                         "status": "merged"}},
            accept={"alpha-20260926-aaaaaaa": {"lane": "alpha-20260926", "sha": "a" * 40, "verdict": "GREEN"}})
        report = status_mod.build_report(root, timeout=60)
        self.assertEqual(report.totals["unmeasured"], 1)
        self.assertEqual([u["program"] for u in report.unmeasured], ["beta"])
        self.assertNotIn("beta", [p["program"] for p in report.programs])
        self.assertEqual(report.totals["openBlocks"], 1)

    def test_a_block_that_declares_itself_blocked_carries_a_signal_not_a_state(self):
        text = ("- [ ] **A2.1 — ⛔ owner live proof** · M\n"
                "  - Acceptance: ⛔ blocked on the owner.\n")
        root = self.fixture(todos={"alpha-todo.md": text}, shapes={"alpha-todo.md": "R-bold"},
                            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                                         "problem": "p", "mode": "direct", "branch": "master",
                                                         "worktree": None, "paths": ["tasks/alpha-todo.md"],
                                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                            accept={"alpha-20260926-aaaaaaa": {"lane": "alpha-20260926", "sha": "a" * 40,
                                                               "verdict": "GREEN"}})
        report = status_mod.build_report(root, timeout=60)
        block = report.programs[0]["open"][0]
        self.assertIn("⛔", block["signals"])
        self.assertEqual(report.totals["openBlocks"], 1)
        self.assertEqual(report.totals["openSignalledBlocks"], 1)

    def test_the_report_never_publishes_an_absolute_path(self):
        root = self.fixture(todos={"alpha-todo.md": R_BOLD_TODO}, shapes={"alpha-todo.md": "R-bold"},
                            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                                         "problem": "p", "mode": "direct", "branch": "master",
                                                         "worktree": None, "paths": ["tasks/alpha-todo.md"],
                                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                            accept={"alpha-20260926-aaaaaaa": {"lane": "alpha-20260926", "sha": "a" * 40,
                                                               "verdict": "GREEN"}})
        rendered = status_mod.render_markdown(status_mod.build_report(root, timeout=60))
        self.assertNotIn(str(root), rendered)
        self.assertIn("tasks/alpha-todo.md", json.dumps(status_mod.asdict(
            status_mod.build_report(root, timeout=60))))


class RefusalTests(TempRepoTest):
    """A broken input is a named refusal with a non-zero exit — never a report that looks empty."""

    def _expect(self, name: str, **kwargs) -> None:
        with self.assertRaises(status_mod.Refusal) as caught:
            self.fixture(**kwargs)
            status_mod.build_report(self.root, timeout=60)
        self.assertEqual(caught.exception.name, name)
        self.assertIn(name, status_mod.REFUSALS)

    def test_a_missing_shape_map_refuses_instead_of_reporting_everything_unmeasured(self):
        self._expect("SHAPE-MAP-UNREADABLE", todos={"alpha-todo.md": R_BOLD_TODO})

    def test_a_status_outside_the_closed_enum_refuses(self):
        self._expect("SESSION-RECORD-INVALID", todos={"alpha-todo.md": R_BOLD_TODO},
                     shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "retired"}})

    def test_a_record_named_for_a_different_session_refuses(self):
        self._expect("SESSION-RECORD-INVALID", todos={"alpha-todo.md": R_BOLD_TODO},
                     shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "something-else", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "active"}})

    def test_a_verdict_outside_the_acceptance_vocabulary_refuses(self):
        self._expect("ACCEPTANCE-ARTEFACT-INVALID", todos={"alpha-todo.md": R_BOLD_TODO},
                     shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                     accept={"alpha-20260926-aaaaaaa": {"lane": "l", "sha": "a" * 40, "verdict": "MOSTLY"}})

    def test_an_artefact_named_for_a_different_sha_refuses(self):
        self._expect("ACCEPTANCE-ARTEFACT-INVALID", todos={"alpha-todo.md": R_BOLD_TODO},
                     shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                     accept={"alpha-20260926-99999999": {"lane": "l", "sha": "abcdef12" + "0" * 32,
                                                          "verdict": "GREEN"}})

    def test_a_decorated_verdict_reads_as_its_family_plus_detail(self):
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO}, shapes={"alpha-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a", "problem": "p",
                                         "mode": "direct", "branch": "master", "worktree": None,
                                         "paths": ["x"], "started": "2026-09-26T00:00:00Z", "status": "merged"}},
            accept={"alpha-20260926-aaaaaaa": {
                "lane": "l", "sha": "a" * 40,
                "verdict": "UNATTRIBUTED (1 red check(s); 1 with NO parsed failure)"}})
        report = status_mod.build_report(root, timeout=60)
        self.assertIn("UNATTRIBUTED", report.acceptances["counts"])
        self.assertEqual(len(report.acceptances["notGreen"]), 1)

    def test_done_blocks_split_by_whether_a_green_acceptance_is_in_head(self):
        """A tick is a claim. The split is the whole point of the totals.

        Two programs, both with ticked rows. One gets a GREEN acceptance naming
        the fixture's real HEAD (so it is in this head); the other gets none.
        The artefact filename must carry the SHA prefix, so the head is read
        from the fixture rather than invented.
        """
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO, "beta-todo.md": R_BOLD_TODO},
            shapes={"alpha-todo.md": "R-bold", "beta-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                         "problem": "p", "mode": "direct", "branch": "master",
                                         "worktree": None, "paths": ["x"],
                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}})
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root, capture_output=True,
                              text=True, check=True).stdout.strip()
        target = root / ".claude" / "cmdc-agents" / "acceptance"
        target.mkdir(parents=True, exist_ok=True)
        # `scope` is what binds an artefact to a program; `lane` matching falls
        # back to the program token appearing in the lane name.
        (target / f"alpha-20260926-{head[:8]}.json").write_text(
            json.dumps({"lane": "alpha-lane", "sha": head, "verdict": "GREEN",
                        "scope": "alpha"}), encoding="utf-8")
        report = status_mod.build_report(root, timeout=60)
        by_name = {p["program"]: p for p in report.programs}
        self.assertEqual(by_name["alpha"]["doneVerified"], by_name["alpha"]["doneBlocks"])
        self.assertEqual(by_name["alpha"]["doneUnverified"], 0)
        self.assertEqual(by_name["beta"]["doneUnverified"], by_name["beta"]["doneBlocks"])
        self.assertEqual(by_name["beta"]["doneVerified"], 0)
        self.assertEqual(report.totals["doneVerified"] + report.totals["doneUnverified"],
                         report.totals["doneBlocks"])

    def test_a_green_verdict_whose_sha_is_not_in_head_does_not_verify(self):
        """A GREEN against a SHA outside this head proves that lane, not this
        integration -- so it must not count as verified. The artefact filename
        carries the SHA prefix, so the stale SHA gets its own name."""
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO},
            shapes={"alpha-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                         "problem": "p", "mode": "direct", "branch": "master",
                                         "worktree": None, "paths": ["x"],
                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
            accept={"alpha-20260926-bbbbbbb": {"lane": "alpha-lane", "sha": "b" * 40, "verdict": "GREEN", "scope": "alpha"}})
        report = status_mod.build_report(root, timeout=60)
        self.assertEqual(report.totals["doneVerified"], 0)
        self.assertGreater(report.totals["doneUnverified"], 0)

    def test_a_non_green_verdict_never_verifies(self):
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO},
            shapes={"alpha-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                         "problem": "p", "mode": "direct", "branch": "master",
                                         "worktree": None, "paths": ["x"],
                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
            accept={"alpha-20260926-aaaaaaa": {"lane": "alpha-lane", "sha": "a" * 40, "verdict": "RED-KNOWN", "scope": "alpha"}})
        report = status_mod.build_report(root, timeout=60)
        self.assertEqual(report.totals["doneVerified"], 0)
        self.assertGreater(report.totals["doneUnverified"], 0)

    def test_the_markdown_names_the_unverified_share(self):
        root = self.fixture(
            todos={"alpha-todo.md": R_BOLD_TODO},
            shapes={"alpha-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha",
                                         "problem": "p", "mode": "direct", "branch": "master",
                                         "worktree": None, "paths": ["x"],
                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
            accept={"alpha-20260926-bbbbbbb": {"lane": "alpha-lane", "sha": "b" * 40, "verdict": "GREEN", "scope": "alpha"}})
        report = status_mod.build_report(root, timeout=60)
        text = status_mod.render_markdown(report)
        self.assertIn("ticked with nothing behind them", text)
        self.assertIn("verified/done", text)

    def test_a_repository_with_no_todos_refuses(self):
        self._expect("NO-TODO-FILES", todos={}, shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                     accept={"alpha-20260926-aaaaaaa": {"lane": "l", "sha": "a" * 40, "verdict": "GREEN"}})

    def test_an_unparseable_ledger_refuses(self):
        self.fixture(todos={"alpha-todo.md": R_BOLD_TODO}, shapes={"alpha-todo.md": "R-bold"},
                     sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "a",
                                                  "problem": "p", "mode": "direct", "branch": "master",
                                                  "worktree": None, "paths": ["x"],
                                                  "started": "2026-09-26T00:00:00Z", "status": "merged"}},
                     accept={"alpha-20260926-aaaaaaa": {"lane": "l", "sha": "a" * 40, "verdict": "GREEN"}})
        (self.root / "tasks" / "alpha-ledger.jsonl").write_text("{not json}\n", encoding="utf-8")
        with self.assertRaises(status_mod.Refusal) as caught:
            status_mod.build_report(self.root, timeout=60)
        self.assertEqual(caught.exception.name, "LEDGER-UNREADABLE")

    def test_a_non_positive_timeout_is_refused_before_any_work(self):
        self.assertEqual(status_mod.main(["--timeout", "0", "--root", str(self.root)]), 2)

    def test_the_cli_exits_two_and_names_the_stage_on_a_broken_repository(self):
        self.fixture(todos={"alpha-todo.md": R_BOLD_TODO})  # no marker map at all
        result = subprocess.run([sys.executable, str(TOOL), "--root", str(self.root)],
                                capture_output=True, encoding="utf-8", timeout=120)
        self.assertEqual(result.returncode, 2)
        self.assertIn("PROGRAM-STATUS REFUSED: SHAPE-MAP-UNREADABLE", result.stderr)
        self.assertEqual(result.stdout, "")


class VocabularyIsPinnedToItsOwnerTests(unittest.TestCase):
    """A closed enum copied into Python drifts unless a test reads the owner. These are the tests
    that make the copy safe: they fail the day either side changes alone."""

    def test_session_vocabulary_matches_the_boundary_checker(self):
        # The owner is IMPORTED, not regexed. This guard used to read
        # `session-boundary-check.ps1` as text and match `$validModes = @(...)` — which is a test of the
        # owner's SOURCE SHAPE, so a reformat could turn a real drift into a green, and the port made it
        # red by changing the dialect rather than the vocabulary. Importing pins the RUNTIME value: the
        # only thing that can satisfy it is the owner actually declaring these tuples.
        self.assertTrue(BOUNDARY.is_file(),
                        f"the session-boundary owner {BOUNDARY} is gone, so this drift guard has "
                        "nothing to compare against and no longer proves anything")
        owner = _load(BOUNDARY, "session_boundary_check_owner")
        self.assertEqual(tuple(owner.VALID_MODES), tuple(status_mod.SESSION_MODES))
        self.assertEqual(tuple(owner.VALID_STATUS), tuple(status_mod.SESSION_STATUSES))
        self.assertEqual(tuple(owner.REQUIRED_FIELDS), tuple(status_mod.SESSION_REQUIRED_FIELDS))
        # `worktree` is in REQUIRED_FIELDS and deliberately EXCLUDED from the owner's missing-field
        # check, because it is mode-dependent. program_status validates by the same rules, so it must
        # carry the same exclusion or it would reject every `direct` record.
        self.assertNotIn("worktree", tuple(owner.REQUIRED_UNLESS_MODE_DEPENDENT))
        self.assertIn("worktree", tuple(owner.REQUIRED_FIELDS))

    def test_both_sides_skip_UNDERSCORE_prefixed_templates(self):
        # The old assertion was `assertIn("Where-Object { $_.Name -notlike '_*' }", text)`, a PowerShell
        # fragment in the owner's source. Asserting the SKIP as a value both sides must agree on keeps
        # the intent and stops a dialect change from deciding it. `tasks/sessions/_template.json` is the
        # file that needs it.
        owner = _load(BOUNDARY, "session_boundary_check_owner_skip")
        self.assertIn("_", (owner.TEMPLATE_PREFIX,))
        self.assertEqual(owner.TEMPLATE_PREFIX, status_mod.SESSION_TEMPLATE_PREFIX)

    def test_acceptance_vocabulary_matches_the_owner(self):
        # Read the OWNER's declaration, and refuse BY NAME when the owner is absent. The previous form
        # was a bare `read_text`, so retiring `accept-lane.ps1` produced a FileNotFoundError from the
        # middle of a vocabulary assertion - which says nothing about which of the two sides drifted,
        # and reads as an environment fault rather than as a contract change.
        self.assertTrue(ACCEPT.is_file(),
                        f"the acceptance gate's owner {ACCEPT} is gone, so its verdict vocabulary "
                        "cannot be pinned and this drift guard no longer proves anything")
        text = ACCEPT.read_text(encoding="utf-8")
        found = re.search(r"ACCEPTANCE_VERDICTS\s*=\s*\(([^)]*)\)", text)
        self.assertIsNotNone(found, f"{ACCEPT.name} no longer declares ACCEPTANCE_VERDICTS")
        self.assertEqual(
            [v.strip().strip("'\"") for v in found.group(1).split(",") if v.strip()],
            list(status_mod.ACCEPTANCE_VERDICTS),
            "the closed verdict vocabulary drifted between the owner and program_status.py")

    def test_the_session_vocabulary_owner_still_exists(self):
        # The same shape, stated for the OTHER owner in this class. This assertion is what a port had
        # to move deliberately: it was written while the owner was still `session-boundary-check.ps1`,
        # and it said so, so the port changed the guard here instead of inheriting a FileNotFoundError
        # and reading it as a vocabulary drift.
        self.assertTrue(BOUNDARY.is_file(),
                        f"the session-boundary checker {BOUNDARY} is gone; this class's drift guard "
                        "for SESSION_* has lost its owner and must be repointed")
    def test_every_named_refusal_explains_itself(self):
        for name, meaning in status_mod.REFUSALS.items():
            self.assertTrue(meaning.strip(), name)
            self.assertIn(" ", meaning, name)


    def test_a_detached_checkout_does_not_print_an_absent_branch_or_a_none_count(self):
        detached = {"head": "a" * 40, "shortHead": "aaaaaaaaa", "branch": "HEAD", "detached": True,
                    "upstream": None, "ahead": None, "behind": None, "dirtyPaths": 0,
                    "untrackedPaths": 0, "conflictedPaths": 0, "worktrees": 1}
        line = status_mod.git_state_line(detached)
        self.assertIn("detached HEAD", line)
        self.assertIn("no upstream", line)
        self.assertNotIn("None", line)

    def test_a_branch_with_an_upstream_prints_the_divergence(self):
        line = status_mod.git_state_line({"branch": "features/mega-merge", "detached": False,
                                          "upstream": "origin/features/mega-merge", "ahead": 2,
                                          "behind": 0, "dirtyPaths": 1, "untrackedPaths": 1,
                                          "conflictedPaths": 0, "worktrees": 3})
        self.assertIn("`features/mega-merge`", line)
        self.assertIn("+2/-0", line)


class HeaderClaimTests(TempRepoTest):
    """A reconciliation check that cries wolf is worse than none: every false-positive class found on
    the real tree has its own test here, because each one named a real program falsely."""

    HEADER = "# Todo: `alpha`\n\n**Status:** {claim}\n\nPlan: alpha-plan.md\n\n"

    def _claims(self, claim: str, body: str = "") -> list:
        root = self.fixture(
            todos={"alpha-todo.md": self.HEADER.format(claim=claim) + body},
            shapes={"alpha-todo.md": "R-bold"},
            sessions={"alpha-20260926": {"session": "alpha-20260926", "program": "alpha", "problem": "p",
                                         "mode": "direct", "branch": "master", "worktree": None,
                                         "paths": ["tasks/alpha-todo.md"],
                                         "started": "2026-09-26T00:00:00Z", "status": "merged"}},
            accept={"alpha-20260926-aaaaaaa": {"lane": "l", "sha": "a" * 40, "verdict": "GREEN"}})
        report = status_mod.build_report(root, timeout=60)
        return [c for c in report.headerClaims if c["file"].endswith("alpha-todo.md")]

    def test_a_whole_file_claim_that_disagrees_is_falsified(self):
        # The file holds 1 done + 1 open; the header says 2 done. One claim agrees, one does not,
        # and a check that cannot tell those apart is not a check.
        found = self._claims("**2 done, 1 open**.", "- [x] **A1 — one**\n- [ ] **A2 — two**\n")
        self.assertEqual({c["unit"]: c["verdict"] for c in found},
                         {"done": "falsified", "open": "confirmed"})
        self.assertEqual({c["unit"]: c["measured"] for c in found}, {"done": 1, "open": 1})

    def test_the_tail_of_a_range_is_not_a_claim(self):
        # "Phases 6-10 open" is a range; reading its tail as "10 open" accused backlog-clear falsely.
        self.assertEqual(self._claims("Phases 6–10 open with a read task."), [])

    def test_a_task_id_is_not_a_claim(self):
        # "F7 done" is a row that shipped, not a count. Accused story-scene falsely.
        self.assertEqual(self._claims("Carries F7 done from wave 2."), [])

    def test_open_as_an_adjective_is_not_a_state(self):
        # "9 open prefix families" and "§14 open question 2" accused passive-tree falsely.
        self.assertEqual(self._claims("Resolve 9 open prefix families."), [])
        self.assertEqual(self._claims("See §14 open question 2."), [])

    def test_a_claim_inside_a_task_row_is_outside_the_header(self):
        body = "- [x] **A1 — queue P0 done**\n- [ ] **A2 — two**\n"
        self.assertEqual(self._claims("Nothing to see.", body), [])

    def test_a_wave_scoped_claim_is_scoped_not_falsified(self):
        found = self._claims("Wave 2 has 15 tasks and is done.", "- [ ] **A1 — one**\n")
        self.assertEqual([c["verdict"] for c in found], ["scoped"])
        self.assertIsNone(found[0]["measured"])

    def test_an_unscoped_task_count_is_informational_not_a_verdict(self):
        # "62 tasks" describes the file, not its open remainder, so it is never called false.
        found = self._claims("This program is 62 tasks.", "- [ ] **A1 — one**\n")
        self.assertEqual([c["verdict"] for c in found], ["unclear"])
        self.assertIsNone(found[0]["measured"])


class RealTreeTests(unittest.TestCase):
    """The tool must work on THIS repository, not only on a fixture — the fixture cannot catch a
    corpus that contains a shape the author never imagined."""

    def test_it_runs_on_the_real_tree_and_reports_task_blocks(self):
        report = status_mod.build_report(REPO, timeout=120)
        self.assertGreater(report.totals["programs"], 1)
        self.assertGreater(report.totals["openBlocks"] + report.totals["doneBlocks"], 0)
        # The tool must report a full git object name, and a SHA-1 is 40 lowercase hex characters.
        # Assert the SHAPE, not a count. `assertEqual(len(head), 40)` says nothing about what `head`
        # actually is - it would pass on any 40-character string - and it reads as a population pin,
        # which guard-population-pin correctly flags: a bare literal cannot tell a scan whether 40 is
        # a closed contract or a drifted reading. The pattern states the contract outright and is
        # strictly stronger: a 40-character non-hex or uppercase value now fails too.
        self.assertRegex(report.git["head"], r"\A[0-9a-f]{40}\Z")
        self.assertIn("active", report.sessions["counts"])
        # Every not-GREEN row is evidence a manager may have to read, so it must be addressable.
        for row in report.acceptances["notGreen"]:
            self.assertTrue(row["artefact"].endswith(".json"))
            self.assertTrue(row["verdict"])
        # Measured + unmeasured must account for every program: a program silently dropped between
        # the two lists is exactly the "an empty report read as success" failure this tool prevents.
        self.assertEqual(len(report.unmeasured) + len(report.programs), report.totals["programs"])
        # The header reconciliation must be precise on the real corpus, not merely non-empty: every
        # falsified claim is checked here to name a number that really disagrees.
        for row in report.headerClaims:
            if row["verdict"] == "falsified":
                self.assertIsNotNone(row["measured"])
                self.assertNotEqual(row["measured"], row["number"], row)

    def test_the_report_is_json_serialisable_and_carries_no_path_from_this_machine(self):
        payload = json.dumps(status_mod.asdict(status_mod.build_report(REPO, timeout=120)))
        # The rule is "no path from the machine that ran this", not "no backslash anywhere" — a
        # committed document may legitimately quote a path such as `H:\Games\...`.
        #
        # The second assertion read the ESCAPED JSON, and an escaped JSON is full of backslashes that
        # are not paths. A task title longer than 43 characters is truncated with the ellipsis
        # character `…`, which json.dumps writes as the six characters `\u2026` - so a title that
        # merely NAMES this repository as a repo-relative path, `... a new gk-core…`, contains the
        # literal `gk-core\` and was reported as a machine path. Measured on the real report: the
        # single match was one such truncated title, and a search for a real drive-rooted path found
        # none. Before the split this repository's name was long and distinctive enough not to occur in
        # ordinary task prose; `gk-core` does occur, so the proxy collided with content.
        #
        # Re-serialising with ensure_ascii=False resolves the escapes, so `gk-core…` no longer contains
        # a backslash while a genuine `gk-core\scripts\...` still does. The criterion is unchanged and
        # the check is now strictly wider, not narrower: the third assertion below rejects ANY
        # drive-rooted absolute path, which the first one could only do for this repository's own
        # prefix. A drive path can never survive as a `\uXXXX` escape, because `D` and `:` are not
        # escaped, so resolving them cannot hide one.
        plain = json.dumps(status_mod.asdict(status_mod.build_report(REPO, timeout=120)),
                           ensure_ascii=False)
        self.assertNotIn(str(REPO), plain)
        self.assertNotIn(str(REPO.name) + os.sep, plain)
        leak = re.search(r"[A-Za-z]:[\\/]", plain)
        self.assertIsNone(leak, f"an absolute Windows path leaked into the report: {leak!r}")


if __name__ == "__main__":
    unittest.main()
