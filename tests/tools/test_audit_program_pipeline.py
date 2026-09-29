"""Fixture-tree tests for gk-core/scripts/audit-program-pipeline.py (backlog-clean-up pipeline-audit-v2).

Every test builds its own tiny doc/task tree under pytest's `tmp_path` and points the audited
module at it directly. None of these assertions ever read the real repository's document tree or
pin its population — the audit is a heuristic over a closed set of finding *kinds* (that vocabulary
is the contract, pinned once below); how many rows a real repo produces is a reading, never a
constant (validation-ssot).

`tmp_path` is pytest's own per-test temp directory and is cleaned up by pytest itself, so there is
no disk cleanup for this file to swallow (testing-standard's substrate rule).
"""
from __future__ import annotations

import datetime
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "scripts" / "audit-program-pipeline.py"

_spec = importlib.util.spec_from_file_location("audit_program_pipeline", SCRIPT)
audit_mod = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = audit_mod  # dataclass field-type resolution needs the module registered
_spec.loader.exec_module(audit_mod)


def _write(root: Path, rel: str, text: str) -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    return p


def _base_tree(root: Path) -> None:
    (root / "docs" / "architecture").mkdir(parents=True, exist_ok=True)
    (root / "docs" / "design").mkdir(parents=True, exist_ok=True)
    (root / "tasks" / "sessions").mkdir(parents=True, exist_ok=True)


def _git_init(root: Path) -> None:
    subprocess.run(["git", "init", "-q"], cwd=root, check=True)
    subprocess.run(["git", "config", "user.email", "test@example.invalid"], cwd=root, check=True)
    subprocess.run(["git", "config", "user.name", "test"], cwd=root, check=True)


def _git_commit(root: Path, message: str, when: datetime.date) -> None:
    env = dict(os.environ)
    stamp = f"{when.isoformat()}T00:00:00"
    env["GIT_AUTHOR_DATE"] = stamp
    env["GIT_COMMITTER_DATE"] = stamp
    subprocess.run(["git", "add", "-A"], cwd=root, check=True)
    subprocess.run(["git", "commit", "-q", "-m", message], cwd=root, check=True, env=env)


def test_closed_kind_vocabulary_is_pinned():
    """A closed vocabulary the code owns (validation-ssot): the finding kinds and the two that are
    advisory. A further kind, or a kind moving in/out of ADVISORY_KINDS, is a reviewed change to this
    test, not silent drift. `todo-task-blocks` joined on 2026-09-23 (TVB-F20: the replacement metric)."""
    assert audit_mod.KINDS == (
        "ideal-no-spec", "ideal-cited", "map-no-spec", "spec-no-plan",
        "map-plan-missing", "plan-no-todo",
        "todo-header-vs-boxes", "todo-task-blocks", "absorbed-no-pointer", "stalled-todo",
    )
    assert audit_mod.ADVISORY_KINDS == ("absorbed-no-pointer", "stalled-todo")


# ---------------------------------------------------------------------------
# B1 — a first-lines blockquote banner reads as a closed status
# ---------------------------------------------------------------------------

def test_blockquote_banner_reads_as_closed(tmp_path):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/widget-map.md", (
        "# Capability map: widget\n\n"
        "> ⛔ **SUPERSEDED 2026-09-06.** Absorbed by gadget.\n\n"
        "No spec files exist for this fixture on purpose.\n"
    ))

    findings = audit_mod.audit(repo=tmp_path)
    hits = [f for f in findings if f.kind == "map-no-spec" and f.program == "widget"]
    assert len(hits) == 1, "the map itself must still surface as a finding to prove the status was read"
    assert hits[0].closed is True
    assert "SUPERSEDED" in hits[0].status.upper()


def test_status_line_without_banner_is_not_closed(tmp_path):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/sprocket-map.md", (
        "# Capability map: sprocket\n\n**Status:** proposed 2026-09-06.\n"
    ))
    findings = audit_mod.audit(repo=tmp_path)
    hits = [f for f in findings if f.kind == "map-no-spec" and f.program == "sprocket"]
    assert len(hits) == 1
    assert hits[0].closed is False


def test_map_plan_missing_still_works(tmp_path):
    """v1 regression: an unrelated finding kind must still work after B1's status-detection change."""
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/nomad-map.md", (
        "# Capability map: nomad\n\n**Status:** proposed.\n\n"
        "Plan: `tasks/nomad-plan.md` / `tasks/nomad-todo.md` (not written yet).\n"
    ))
    findings = audit_mod.audit(repo=tmp_path)
    hits = [f for f in findings if f.kind == "map-plan-missing" and f.program == "nomad"]
    assert len(hits) == 1
    assert "tasks/nomad-plan.md" in hits[0].evidence[0]
    assert "tasks/nomad-todo.md" in hits[0].evidence[0]


# ---------------------------------------------------------------------------
# B2 — todo-header-vs-boxes, and stalled-todo's session/ledger suppression
# ---------------------------------------------------------------------------

def test_header_complete_with_open_boxes_is_flagged_once(tmp_path):
    _base_tree(tmp_path)
    _git_init(tmp_path)
    _write(tmp_path, "tasks/gizmo-todo.md", (
        "# Todo: gizmo\n\n"
        "**Build complete 2026-01-01**: all 3 tasks gated PASS.\n\n"
        "- [ ] **G1** first\n"
        "- [ ] **G2** second\n"
        "- [ ] **G3** third\n"
    ))
    _git_commit(tmp_path, "gizmo: seed", datetime.date(2026, 1, 1))

    findings = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 15))
    header_hits = [f for f in findings if f.kind == "todo-header-vs-boxes" and f.program == "gizmo"]
    assert len(header_hits) == 1
    assert "3 open" in header_hits[0].evidence[0]


def test_header_negation_is_not_a_completion_claim(tmp_path):
    """'No task is done until its verification command is green' is a per-task RULE, not a
    whole-todo completion claim — the live audit's own false-positive instance."""
    _base_tree(tmp_path)
    _write(tmp_path, "tasks/widget-todo.md", (
        "# Todo: widget\n\n"
        "No task is done until its verification command is green.\n\n"
        "- [ ] **W1** first\n"
    ))
    findings = audit_mod.audit(repo=tmp_path)
    assert not [f for f in findings if f.kind == "todo-header-vs-boxes" and f.program == "widget"]


def test_reconciled_banner_suppresses_both_header_and_stalled_findings(tmp_path):
    """paperwork-reconcile rule 4's own fix for this exact finding (add the banner sentence,
    never hand-tick the boxes) must make both v2 kinds stop firing on the same todo."""
    _base_tree(tmp_path)
    _git_init(tmp_path)
    _write(tmp_path, "tasks/relic-todo.md", (
        "# Todo: relic\n\n"
        "**Build complete 2026-01-01**: all 2 tasks gated PASS.\n\n"
        "**All boxes below are closed by the header above** (verified 2026-01-15).\n\n"
        "- [ ] **R1** first\n- [ ] **R2** second\n"
    ))
    _git_commit(tmp_path, "relic: seed", datetime.date(2026, 1, 1))
    findings = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 20))
    assert not [f for f in findings if f.kind in ("todo-header-vs-boxes", "stalled-todo") and f.program == "relic"]


def test_stalled_todo_suppressed_by_merged_session(tmp_path):
    _base_tree(tmp_path)
    _git_init(tmp_path)
    _write(tmp_path, "tasks/gizmo-todo.md", (
        "# Todo: gizmo\n\n- [ ] **G1** first\n- [ ] **G2** second\n"
    ))
    _git_commit(tmp_path, "gizmo: seed", datetime.date(2026, 1, 1))

    before = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 15))
    assert [f for f in before if f.kind == "stalled-todo" and f.program == "gizmo"], (
        "no session yet, no recent commit -> should read stalled"
    )

    _write(tmp_path, "tasks/sessions/gizmo-build.json", json.dumps({
        "session": "gizmo-build", "program": "gizmo", "problem": "build gizmo",
        "mode": "direct", "branch": "main", "worktree": None,
        "paths": ["tasks/gizmo-todo.md"], "started": "2026-01-01T00:00:00Z", "status": "merged",
    }))
    after = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 15))
    assert not [f for f in after if f.kind == "stalled-todo" and f.program == "gizmo"], (
        "a MERGED session naming the program suppresses stalled-todo"
    )


def test_stalled_todo_suppressed_by_ledger_done_ids(tmp_path):
    _base_tree(tmp_path)
    _git_init(tmp_path)
    _write(tmp_path, "tasks/gizmo-todo.md", "# Todo: gizmo\n\n- [ ] **G1** first\n")
    _write(tmp_path, "tasks/gizmo-ledger.jsonl", json.dumps({"kind": "task", "id": "G1", "state": "done"}) + "\n")
    _git_commit(tmp_path, "gizmo: seed", datetime.date(2026, 1, 1))

    findings = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 15))
    assert not [f for f in findings if f.kind == "stalled-todo" and f.program == "gizmo"], (
        "a ledger recording the open box's task id as done suppresses stalled-todo"
    )


def test_stale_days_threshold_moves_the_verdict(tmp_path):
    _base_tree(tmp_path)
    _git_init(tmp_path)
    _write(tmp_path, "tasks/wobble-todo.md", "# Todo: wobble\n\n- [ ] **W1** work\n")
    _git_commit(tmp_path, "wobble: seed", datetime.date(2026, 1, 1))

    lenient = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 3), stale_days=7)
    assert not [f for f in lenient if f.kind == "stalled-todo" and f.program == "wobble"]

    strict = audit_mod.audit(repo=tmp_path, today=datetime.date(2026, 1, 3), stale_days=1)
    assert [f for f in strict if f.kind == "stalled-todo" and f.program == "wobble"]


# ---------------------------------------------------------------------------
# B3 — absorbed-no-pointer
# ---------------------------------------------------------------------------

def test_absorbed_no_pointer_when_owner_never_cites_the_ticking_program(tmp_path):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/alpha-map.md", "# Capability map: alpha\n\n**Status:** built.\n")
    _write(tmp_path, "docs/architecture/alpha/spec-corpse-cache.md", "# Spec: corpse-cache\n\n**Status:** spec.\n")
    _write(tmp_path, "tasks/alpha-plan.md", "# Plan: alpha\n")
    _write(tmp_path, "tasks/alpha-todo.md", "# Todo: alpha\n\n- [ ] **A1** build corpse-cache\n")
    _write(tmp_path, "tasks/beta-todo.md", "# Todo: beta\n\n- [x] **B1** corpse-cache shipped as part of beta\n")

    findings = audit_mod.audit(repo=tmp_path)
    hits = [f for f in findings if f.kind == "absorbed-no-pointer" and f.program == "alpha"]
    assert len(hits) == 1
    assert "beta" in hits[0].evidence[0]


def test_absorbed_no_pointer_needs_no_row_once_owner_cites_it(tmp_path):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/alpha-map.md", "# Capability map: alpha\n\n**Status:** built. See beta.\n")
    _write(tmp_path, "docs/architecture/alpha/spec-corpse-cache.md", "# Spec: corpse-cache\n\n**Status:** spec.\n")
    _write(tmp_path, "tasks/alpha-plan.md", "# Plan: alpha\n")
    _write(tmp_path, "tasks/alpha-todo.md", "# Todo: alpha\n\n- [ ] **A1** build corpse-cache (see beta)\n")
    _write(tmp_path, "tasks/beta-todo.md", "# Todo: beta\n\n- [x] **B1** corpse-cache shipped as part of beta\n")

    findings = audit_mod.audit(repo=tmp_path)
    assert not [f for f in findings if f.kind == "absorbed-no-pointer" and f.program == "alpha"]


def test_absorbed_no_pointer_ignores_a_bare_generic_word(tmp_path):
    """A short, common module id ('budget') must not fire on unrelated prose that merely contains
    the word next to an unrelated checked box — the live audit's own false-positive instance."""
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/alpha-map.md", "# Capability map: alpha\n\n**Status:** built.\n")
    _write(tmp_path, "docs/architecture/alpha/spec-budget.md", "# Spec: budget\n\n**Status:** spec.\n")
    _write(tmp_path, "tasks/alpha-plan.md", "# Plan: alpha\n")
    _write(tmp_path, "tasks/alpha-todo.md", "# Todo: alpha\n\n- [ ] **A1** size the budget\n")
    _write(tmp_path, "tasks/beta-todo.md", "# Todo: beta\n\n- [x] **B1** stayed under the perf budget\n")

    findings = audit_mod.audit(repo=tmp_path)
    assert not [f for f in findings if f.kind == "absorbed-no-pointer" and f.program == "alpha"]


# ---------------------------------------------------------------------------
# v1 kinds still behave, and the CLI flags keep/gain their meaning
# ---------------------------------------------------------------------------

def test_fail_on_open_excludes_advisory_unless_strict(tmp_path, monkeypatch):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/alpha-map.md", "# Capability map: alpha\n\n**Status:** built.\n")
    _write(tmp_path, "docs/architecture/alpha/spec-corpse-cache.md", "# Spec: corpse-cache\n\n**Status:** spec.\n")
    _write(tmp_path, "tasks/alpha-plan.md", "# Plan: alpha\n")
    _write(tmp_path, "tasks/alpha-todo.md", "# Todo: alpha\n\n- [x] **A1** build corpse-cache\n")
    _write(tmp_path, "tasks/beta-todo.md", "# Todo: beta\n\n- [x] **B1** corpse-cache shipped as part of beta\n")

    argv = ["audit-program-pipeline", "--root", str(tmp_path), "--only", "absorbed-no-pointer", "--fail-on-open"]
    monkeypatch.setattr(sys, "argv", argv)
    assert audit_mod.main() == 0, "an advisory-only open finding must not fail --fail-on-open alone"

    monkeypatch.setattr(sys, "argv", argv + ["--strict"])
    assert audit_mod.main() == 1, "--strict makes an open advisory finding fail the gate"


def test_only_and_json_flags(tmp_path, monkeypatch, capsys):
    _base_tree(tmp_path)
    _write(tmp_path, "docs/architecture/nomad-map.md", (
        "# Capability map: nomad\n\n**Status:** proposed.\n\n"
        "Plan: `tasks/nomad-plan.md` / `tasks/nomad-todo.md`.\n"
    ))
    monkeypatch.setattr(sys, "argv", [
        "audit-program-pipeline", "--root", str(tmp_path), "--only", "map-plan-missing", "--json",
    ])
    rc = audit_mod.main()
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert len(out) == 1
    assert out[0]["kind"] == "map-plan-missing"
    assert out[0]["program"] == "nomad"


# ---------------------------------------------------------------------------------------------------
# TVB-F20 — the block counter (`todo-task-blocks`). Fixtures only: each case is a tiny in-memory
# document, and nothing here reads the real repo or pins a population (validation-ssot).
# ---------------------------------------------------------------------------------------------------

class TestTaskBlocks:
    def test_a_heading_task_file_counts_two_done_and_one_open(self):
        text = (
            "# Todo: alpha\n\n"
            "## Task 1: one\n- [x] box\n\n"
            "## Task 2: two\n- [x] box\n\n"
            "## Task 3: three\n- [ ] box\n"
        )
        assert audit_mod.task_blocks(text, "H-task") == (1, 2, 1, 0)

    def test_checked_checkbox_heading_is_done_and_open_sibling_is_open(self):
        text = (
            "# Todo: checkbox-headings\n\n"
            "## - [x] closed heading\n"
            "  - [ ] acceptance contract\n\n"
            "## - [ ] open heading\n"
        )
        assert audit_mod.task_blocks(text, "H-checkbox-heading") == (1, 1, 1, 1)

    def test_a_header_banner_closes_every_block_and_shades_its_boxes(self):
        text = (
            "# Todo: beta\n\n"
            "**All boxes below are closed by the header above.**\n\n"
            "## Task 1: one\n- [ ] a\n- [ ] b\n- [ ] c\n"
        )
        open_blocks, done_blocks, boxes, shaded = audit_mod.task_blocks(text, "H-task")
        assert (open_blocks, done_blocks, boxes) == (0, 1, 3)
        assert shaded == 3  # unticked boxes inside a block the file declares done

    def test_a_mid_file_banner_closes_the_rows_beneath_it(self):
        # The loam shape: a phase banner, then the phase's own rows beneath it.
        text = (
            "# Todo: gamma\n\n"
            "## Task 1: one\n- [x] box\n\n"
            "> **⛔ CLOSED — SUPERSEDED, 2026-09-03. Do not start this phase.**\n\n"
            "## Task 2: two\n- [ ] box\n"
        )
        assert audit_mod.task_blocks(text, "H-task") == (0, 2, 1, 1)

    def test_a_mid_file_banner_does_not_close_a_row_above_it(self):
        # The species-gear-chain shape: a file-end banner must not swallow the rows that precede it.
        text = (
            "# Todo: delta\n\n"
            "## Task 1: one\n- [ ] box\n\n"
            "> **⛔ CLOSED — this phase is dead.**\n\n"
            "## Task 2: two\n- [x] box\n"
        )
        assert audit_mod.task_blocks(text, "H-task") == (1, 1, 1, 0)

    def test_a_heading_declaring_itself_open_beats_the_file_banner(self):
        text = (
            "# Todo: zeta\n\n"
            "**All boxes below are closed by the header above.**\n\n"
            "## Task 1: one\n- [ ] box\n\n"
            "## Task 2: two (OPEN)\n- [ ] box\n"
        )
        assert audit_mod.task_blocks(text, "H-task") == (1, 1, 2, 1)

    def test_the_contract_clause_shades_a_ticked_block_but_never_closes_an_unticked_one(self):
        text = (
            "# Todo: epsilon\n\n"
            "A ticked task's acceptance boxes are its original contract.\n\n"
            "## Task 1: one\n- [x] done\n  - [ ] acceptance\n\n"
            "## Task 2: two\n- [ ] open\n"
        )
        assert audit_mod.task_blocks(text, "H-task") == (1, 1, 2, 1)

    def test_a_row_shaped_file_uses_its_own_checkbox_as_the_tick(self):
        text = (
            "# Todo: eta\n\n"
            "- [x] **E1** first\n  - [ ] acceptance\n"
            "- [ ] **E2** second\n"
        )
        assert audit_mod.task_blocks(text, "R-bold") == (1, 1, 2, 1)

    def test_an_undeclared_shape_counts_nothing(self):
        # A file with no reliable marker is UNMEASURED, never defaulted to zero (RECON-F8).
        assert audit_mod.task_blocks("# Todo: theta\n\nno boxes here\n", "none") == (0, 0, 0, 0)


class TestTodoTaskBlocksFinding:
    def test_an_undeclared_file_is_reported_unmeasured(self, tmp_path, monkeypatch, capsys):
        _base_tree(tmp_path)
        _write(tmp_path, "tasks/nomad-todo.md", "# Todo: nomad\n\nsome prose\n")
        _write(tmp_path, "scripts/todo-shapes.v1.json", json.dumps({}))
        monkeypatch.setattr(sys, "argv", [
            "audit-program-pipeline", "--root", str(tmp_path), "--only", "todo-task-blocks", "--json",
        ])
        assert audit_mod.main() == 0
        out = json.loads(capsys.readouterr().out)
        assert [f["kind"] for f in out] == ["todo-task-blocks"]
        assert "unmeasured" in out[0]["evidence"][0]

    def test_a_declared_file_prints_the_reading(self, tmp_path, monkeypatch, capsys):
        _base_tree(tmp_path)
        _write(tmp_path, "tasks/nomad-todo.md",
               "# Todo: nomad\n\n## Task 1: one\n- [x] a\n\n## Task 2: two\n- [ ] b\n")
        _write(tmp_path, "scripts/todo-shapes.v1.json",
               json.dumps({"tasks/nomad-todo.md": {"shape": "H-task", "exemplar": "## Task 1: one"}}))
        monkeypatch.setattr(sys, "argv", [
            "audit-program-pipeline", "--root", str(tmp_path), "--only", "todo-task-blocks", "--json",
        ])
        assert audit_mod.main() == 0
        out = json.loads(capsys.readouterr().out)
        assert out[0]["evidence"][0] == "shape=H-task open=1 done=1 boxes=1 (not a work count) shaded=0"
