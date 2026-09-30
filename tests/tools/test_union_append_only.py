"""Tests for `.claude/cmdc-agents/scripts/union_append_only.py` — the append-only merge resolver.

Three jobs, in order of how much they are worth:

1. **Falsify the defect against the pre-fix code.** The pre-fix code is the line union with no shape
   gate and no round trip, so it is reproduced HERE as `legacy_merge` -- the shipped algorithm,
   transcribed verbatim, not a hand-written expectation. `FalsifierTests` drives the real CLI on a
   registry pair and asserts a NAMED refusal; against the pre-fix tool every one of those tests fails,
   because it exits 0 having written a file that is not valid JSON. A test that passes before and
   after proves nothing, which is the exact failure mode being fixed, so
   `The_fixture_really_is_a_reproducer` pins that the legacy algorithm on the same fixture produces
   unparseable output -- if that ever stops being true, the falsifiers above it are testing nothing.

2. **Parity against the pre-fix algorithm, on real ledgers.** `ParityTests` sweeps every
   `tasks/*-ledger.jsonl` and a named set of real `tasks/*-todo.md` files, cuts each into two sides
   the way a real append-only conflict looks (`git show :2:` and `:3:`), and asserts the tool's bytes
   equal `legacy_merge`'s byte for byte. No count is pinned: the sweep is a property of the tree, and
   adding a ledger adds a case.

3. **The contract.** The shape vocabulary, the refusal names and exit codes, the round-trip
   guarantee, and the report the operator reads. The round-trip predicates are unreachable from the
   CLI once the pre-write checks pass -- that is what makes them worth having, and it is also why they
   are tested as predicates with crafted corrupt input and as an injected failing validator, rather
   than through an exit code no honest input can produce.

Substrate: temp directories only, deleted in `tearDown` with the delete asserted (never swallowed),
per `docs/contributing/testing-standard.md`. A real file IS the subject here -- the tool's whole
contract is what it writes to disk -- so this is the R2 case, and the temp root is created and removed
inside the test. No store, no network, no game, and nothing outside `tempfile`'s directory.
"""
from __future__ import annotations

import contextlib
import hashlib
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
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL = REPO / ".claude" / "cmdc-agents" / "scripts" / "union_append_only.py"

#: Hard timeout on every subprocess. A tool that hangs must fail the test, not the run.
SUBPROCESS_TIMEOUT_S = 120


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    # `@dataclass` resolves annotations through sys.modules[cls.__module__]; without this registration
    # an import-by-path raises AttributeError from inside dataclasses instead of loading.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


uao = _load(TOOL, "union_append_only_under_test")


# ---------------------------------------------------------------------------------------------
# The PRE-FIX algorithm, transcribed verbatim from the shipped tool at 359aacf36. This is the parity
# reference AND the reproducer: if it ever diverges from the real fix, both jobs above are worthless,
# so `ParityTests` also asserts the transcription still matches the shape the tool implements.
# ---------------------------------------------------------------------------------------------
def legacy_lines(raw: str) -> list[str]:
    out = []
    for line in raw.split("\n"):
        s = line.rstrip("\r")
        if not s.strip():
            continue
        if s.lstrip().startswith(("<<<<<<<", "=======", ">>>>>>>")):
            continue
        out.append(s)
    return out


def legacy_merge(ours_raw: str, theirs_raw: str, out_name: str) -> str:
    """The pre-fix tool's whole behaviour: a line union, plus the `.jsonl` ts re-sort."""
    ours, theirs = legacy_lines(ours_raw), legacy_lines(theirs_raw)
    seen, result = set(), []
    for line in ours:
        if line not in seen:
            seen.add(line)
            result.append(line)
    for line in theirs:
        if line not in seen:
            seen.add(line)
            result.append(line)
    if out_name.endswith(".jsonl"):
        rows, ok = [], True
        for line in result:
            obj = json.loads(line)
            if not isinstance(obj, dict) or "ts" not in obj:
                ok = False
                break
            rows.append((str(obj["ts"]), line))
        if ok:
            rows.sort(key=lambda r: r[0])
            result = [r[1] for r in rows]
    return "\n".join(result) + "\n"


# ---------------------------------------------------------------------------------------------
# Fixtures. The registry shape is cut from the real gk-core/scripts/verification-boundaries.v1.json --
# `projects` arrays on single lines, `boundaries` as multi-line objects, two-space indent -- because
# the defect is about SHAPE and a hand-invented shape would be a weaker claim than the real one.
# ---------------------------------------------------------------------------------------------
def registry_doc(extra_id: str) -> str:
    return (
        "{\n"
        '  "schemaVersion": 5,\n'
        '  "projects": {\n'
        '    "core": [\n'
        '      "tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj"\n'
        "    ]\n"
        "  },\n"
        '  "boundaries": [\n'
        "    {\n"
        '      "id": "core-area-activity",\n'
        '      "paths": [\n'
        '        "src/FusionRpg.Core/Activity/**"\n'
        "      ]\n"
        "    },\n"
        "    {\n"
        '      "id": "battle-effect-math",\n'
        '      "paths": [\n'
        '        "src/FusionRpg.Core/Battle/Effect/**"\n'
        "      ]\n"
        "    },\n"
        "    {\n"
        f'      "id": "{extra_id}",\n'
        '      "paths": [\n'
        f'        "src/FusionRpg.Core/{extra_id}/**"\n'
        "      ]\n"
        "    }\n"
        "  ],\n"
        '  "knownRed": []\n'
        "}\n"
    )


class ToolTestCase(unittest.TestCase):
    """Temp substrate with an ASSERTED delete (R3): a failed cleanup fails the test."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="union-append-only-"))
        self.addCleanup(self._drop_tmp)

    def _drop_tmp(self) -> None:
        shutil.rmtree(self.tmp)  # raises on failure; never swallowed

    def write(self, name: str, text: str) -> Path:
        path = self.tmp / name
        path.write_text(text, encoding="utf-8", newline="")
        return path

    def run_tool(self, ours: Path, theirs: Path, out: Path, *extra: str) -> tuple[int, str, str, dict | None]:
        proc = subprocess.run(
            [sys.executable, str(TOOL), "--ours", str(ours), "--theirs", str(theirs),
             "--out", str(out), *extra],
            capture_output=True, text=True, timeout=SUBPROCESS_TIMEOUT_S, cwd=str(self.tmp),
        )
        envelope = None
        if "--json" in extra and proc.stdout.strip():
            envelope = json.loads(proc.stdout)
        return proc.returncode, proc.stdout, proc.stderr, envelope

    def run_in_process(self, ours: Path, theirs: Path, out: Path, *extra: str) -> tuple[int, str]:
        """The same entry point, in-process: the parity sweep runs one case per real ledger."""
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            code = uao.main(["--ours", str(ours), "--theirs", str(theirs), "--out", str(out), *extra])
        return code, buf.getvalue()

    def assertNoStagedLeftovers(self) -> None:
        leftovers = [p.name for p in self.tmp.iterdir() if ".staged-" in p.name]
        self.assertEqual([], leftovers, "a staged file was left in the destination directory")


# ---------------------------------------------------------------------------------------------
# 1. Falsifiers. Each of these FAILS against the pre-fix tool (exit 0, corrupt output, no name).
#
#    The expected exit codes and refusal names are LITERALS in this class, not read back off the
#    module. That is deliberate: a test asserting `EXIT_SHAPE_UNSUPPORTED` fails against the pre-fix
#    code with an AttributeError, which proves only that a name is missing, while a test asserting `2`
#    fails with `0 != 2` -- which is the defect. The constants are still asserted, as a closed
#    vocabulary, in VocabularyTests.
# ---------------------------------------------------------------------------------------------
ACCEPTED = 0
EXIT_REFUSED = 1
EXIT_SHAPE_UNSUPPORTED = 2
REFUSAL_UNREADABLE = "INPUT-UNREADABLE"
REFUSAL_ROW_NOT_JSON = "INPUT-ROW-NOT-JSON"
REFUSAL_SHAPE_UNSUPPORTED = "INPUT-SHAPE-UNSUPPORTED"
class FalsifierTests(ToolTestCase):
    def test_A_json_registry_pair_is_refused_by_name_and_writes_nothing(self) -> None:
        """The headline case. The FIRST assertion is the file's existence, not the exit code: argparse
        also exits 2 on an unknown flag, so an exit-code-first test can pass for the wrong reason.
        Against the pre-fix tool the union file EXISTS here and does not parse."""
        ours = self.write("ours.json", registry_doc("cheatcore-fallback"))
        theirs = self.write("theirs.json", registry_doc("commander-directory"))
        out = self.tmp / "union.json"

        code, _, err, _ = self.run_tool(ours, theirs, out)

        self.assertFalse(out.exists(), "the destructive path must be unreachable")
        if out.exists():  # the pre-fix outcome, kept as the failure MESSAGE rather than a mystery
            with self.assertRaises(ValueError):
                json.loads(out.read_text(encoding="utf-8"))
        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, code)
        self.assertIn(REFUSAL_SHAPE_UNSUPPORTED, err)
        self.assertNoStagedLeftovers()

    def test_A2_the_refusal_is_a_machine_readable_claim_not_only_prose(self) -> None:
        """`--json` is new capability, so this cannot pass against the pre-fix tool at all (the flag
        does not exist). It is here to pin the envelope a caller can assert on."""
        ours = self.write("ours.json", registry_doc("cheatcore-fallback"))
        theirs = self.write("theirs.json", registry_doc("commander-directory"))
        out = self.tmp / "union.json"

        code, _, _, envelope = self.run_tool(ours, theirs, out, "--json")

        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, code)
        self.assertFalse(out.exists())
        self.assertIsNotNone(envelope)
        self.assertFalse(envelope["ok"])
        self.assertEqual(REFUSAL_SHAPE_UNSUPPORTED, envelope["refusal"]["name"])
        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, envelope["refusal"]["exitCode"])
        self.assertIsNone(envelope["out"], "a refused run reports no output")
        self.assertEqual("structured", envelope["inputs"]["ours"]["shape"])

    def test_B_registry_sides_named_txt_are_refused_by_content(self) -> None:
        """The shape the incident actually arrived in: resolve-append-only.ps1:38-39 renames both
        sides to ours.txt / theirs.txt, so a suffix rule on the INPUTS alone would not catch it."""
        ours = self.write("ours.txt", registry_doc("cheatcore-fallback"))
        theirs = self.write("theirs.txt", registry_doc("commander-directory"))
        out = self.tmp / "union.txt"

        code, _, err, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, code)
        self.assertIn(REFUSAL_SHAPE_UNSUPPORTED, err)
        self.assertIn("parses as ONE json document", err)
        self.assertFalse(out.exists())

    def test_C_a_conflicted_registry_is_refused_behind_its_own_markers(self) -> None:
        """A conflicted working file's marker-stripped WHOLE text is two documents concatenated, which
        parses as nothing -- so the probe has to look at the conflict regions, not the whole file.
        The first assertion is why this case exists: without it a reader cannot tell whether the
        region probe is load-bearing or decorative."""
        conflicted = ("<<<<<<< HEAD\n" + registry_doc("cheatcore-fallback") + "=======\n"
                      + registry_doc("commander-directory") + ">>>>>>> feature/actor-hud\n")
        with self.assertRaises(ValueError):
            json.loads("\n".join(legacy_lines(conflicted)))  # the whole, marker-stripped text
        side = self.write("registry.txt", conflicted)
        out = self.tmp / "union.txt"

        code, _, err, _ = self.run_tool(side, side, out)

        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, code)
        self.assertIn(REFUSAL_SHAPE_UNSUPPORTED, err)
        self.assertFalse(out.exists())

    def test_D_a_refusal_leaves_an_existing_destination_byte_identical(self) -> None:
        """The incident left a corrupt 96 KB registry sitting in the tree, reported as success. A
        refusal must not touch the destination at all."""
        ours = self.write("ours.json", registry_doc("cheatcore-fallback"))
        theirs = self.write("theirs.json", registry_doc("commander-directory"))
        out = self.write("registry.json", '{"the": "previous, valid content"}\n')
        before = out.read_bytes()

        code, _, _, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(EXIT_SHAPE_UNSUPPORTED, code)
        self.assertEqual(before, out.read_bytes())
        self.assertEqual(1, len(list(self.tmp.glob("registry.json.staged-*"))) + 1)  # dest + nothing else
        self.assertNoStagedLeftovers()

    def test_E_the_fixture_really_is_a_reproducer(self) -> None:
        """If the pre-fix algorithm stopped mangling this fixture, every falsifier above would be
        asserting a shape that no longer reaches the destructive path -- and would pass for the wrong
        reason. This test therefore pins the 'before' from inside the suite."""
        ours, theirs = registry_doc("cheatcore-fallback"), registry_doc("commander-directory")
        self.assertEqual(ours, ours)  # both sides are well-formed documents on purpose
        json.loads(ours)
        json.loads(theirs)

        mangled = legacy_merge(ours, theirs, "union.json")
        with self.assertRaises(json.JSONDecodeError):
            json.loads(mangled)
        # The mechanism, stated: the dedup collapses the repeated structural lines.
        self.assertLess(len(legacy_lines(mangled)), len(legacy_lines(ours)) + len(legacy_lines(theirs)))
        self.assertLess(legacy_lines(mangled).count("    {"), 2 * legacy_lines(ours).count("    {"))


# ---------------------------------------------------------------------------------------------
# 2. Parity with the pre-fix algorithm, over real ledgers from tasks/.
# ---------------------------------------------------------------------------------------------
def real_ledgers() -> list[Path]:
    return sorted((REPO / "tasks").glob("*ledger.jsonl"))


def real_markdown_ledgers() -> list[Path]:
    """A named set, not a population: these are the files the resolver is actually pointed at."""
    names = ["data-test-substrate-todo.md", "actor-hud-todo.md", "summoner-convergence-todo.md",
             "verification-boundaries-todo.md", "ps1-ban-todo.md"]
    return [REPO / "tasks" / n for n in names if (REPO / "tasks" / n).exists()]


def cut_sides(text: str) -> tuple[str, str]:
    """Two sides the way a real append-only conflict looks: a shared prefix, then each lane's rows."""
    lines = [l for l in text.split("\n") if l.strip()]
    split = max(1, len(lines) // 2)
    return ("\n".join(lines[:split + 5]) + "\n", "\n".join(lines[split:]) + "\n")


class ParityTests(ToolTestCase):
    def _parity(self, source: Path, out_name: str) -> None:
        raw = source.read_text(encoding="utf-8")
        ours, theirs = cut_sides(raw)
        ours_path, theirs_path = self.write("ours.txt", ours), self.write("theirs.txt", theirs)
        out = self.tmp / out_name
        if out.exists():
            out.unlink()

        # Through the CLI, never through main(argv): a parity test that depends on the fixed tool's
        # entry-point signature cannot prove the BEHAVIOUR is unchanged, only that the new code runs.
        # Driven this way every test in this class passes against the PRE-FIX tool too -- which is the
        # entire claim it makes.
        code, _, err, _ = self.run_tool(ours_path, theirs_path, out)

        self.assertEqual(ACCEPTED, code, f"{source.name} was refused: {err}")
        self.assertEqual(legacy_merge(ours, theirs, out_name), out.read_text(encoding="utf-8"),
                         f"the fixed tool's bytes differ from the pre-fix algorithm on {source.name}")

    def test_jsonl_ledgers_merge_byte_identically_to_the_pre_fix_algorithm(self) -> None:
        ledgers = real_ledgers()
        self.assertTrue(ledgers, "no tasks/*ledger.jsonl found -- the sweep proved nothing")
        for source in ledgers:
            with self.subTest(ledger=source.name):
                self._parity(source, "union.jsonl")

    def test_markdown_task_lists_merge_byte_identically_to_the_pre_fix_algorithm(self) -> None:
        sources = real_markdown_ledgers()
        self.assertTrue(sources, "none of the named real task lists exist -- the sweep proved nothing")
        for source in sources:
            with self.subTest(todo=source.name):
                self._parity(source, "union.md")

    def test_conflict_markers_are_still_stripped_exactly_as_before(self) -> None:
        """The marker rule is the pre-existing behaviour most likely to be broken by a shape gate that
        reads the raw file, so it is pinned against the transcription. It calls the fixed tool's
        `split_lines` directly, so unlike the two sweeps above it cannot run against the pre-fix
        source -- which is why the sweeps drive the CLI and this one does not."""
        raw = "alpha\n<<<<<<< HEAD\nbeta\n=======\ngamma\n>>>>>>> branch\ndelta\n"
        self.assertEqual(legacy_lines(raw), uao.split_lines(raw))
        self.assertEqual(["alpha", "beta", "gamma", "delta"], uao.split_lines(raw))


# ---------------------------------------------------------------------------------------------
# 3a. The shape vocabulary and the refusals.
# ---------------------------------------------------------------------------------------------
class ShapeTests(ToolTestCase):
    LEDGER = '{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n'

    def classify(self, name: str, text: str) -> uao.Shape:
        return uao.classify("ours", name, text)

    def test_markdown_and_text_are_this_tools_class(self) -> None:
        for name in ("tasks/foo-todo.md", "ours.txt", "notes.log", "noextension"):
            with self.subTest(name=name):
                self.assertEqual("lines", self.classify(name, "# Task\n\n- [ ] A1\n").kind)

    def test_jsonl_is_recognised_by_suffix_even_when_a_row_has_no_ts(self) -> None:
        shape = self.classify("tasks/foo-ledger.jsonl", '{"task": "A1"}\n{"task": "B2"}\n')
        self.assertEqual("jsonl", shape.kind)
        self.assertFalse(shape.document)

    def test_a_json_registry_is_refused_by_suffix_whatever_its_content(self) -> None:
        """The suffix signal does not need the content to look like a document: a registry flattened
        onto one line is still a registry."""
        shape = self.classify("scripts/verification-boundaries.v1.json", '{"boundaries": []}\n')
        self.assertEqual("structured", shape.kind)

    def test_block_structured_suffixes_are_refused_by_declaration(self) -> None:
        """Not measured, reasoned -- these are the same class as JSON by construction: a line is not
        the unit of meaning. The report says which of these are evidence and which are reasoning."""
        for suffix in (".yaml", ".yml", ".toml", ".xml", ".ini", ".cfg", ".conf"):
            with self.subTest(suffix=suffix):
                self.assertEqual("structured", self.classify("config" + suffix, "a: 1\n").kind)

    def test_csv_is_deliberately_not_refused(self) -> None:
        """A row-per-line table IS a legitimate line union; refusing it would block a valid use."""
        self.assertEqual("lines", self.classify("tasks/rows.csv", "id,ts\nA1,2026-09-01\n").kind)

    def test_a_one_line_json_document_under_a_neutral_name_is_refused(self) -> None:
        """The stated trade-off. A minified structured file is the likelier reading, and a one-line
        append-only ledger cannot conflict in the first place."""
        self.assertEqual("structured", self.classify("ours.txt", '{"a": 1}\n').kind)
        # ... and the .jsonl spelling of the same content is NOT refused, so the rule is the suffix.
        self.assertEqual("jsonl", self.classify("ours.jsonl", '{"a": 1}\n').kind)

    def test_a_ledger_that_merely_contains_a_json_line_is_still_this_tools_class(self) -> None:
        self.assertEqual("lines", self.classify("tasks/foo-todo.md", '# Task\n\n```json\n{"a": 1}\n```\n').kind)

    def test_an_unreadable_input_is_a_named_refusal_not_a_traceback(self) -> None:
        ours = self.write("ours.txt", "alpha\n")
        code, _, err, envelope = self.run_tool(ours, self.tmp / "absent.txt", self.tmp / "out.txt", "--json")

        self.assertEqual(EXIT_REFUSED, code)
        self.assertIn(REFUSAL_UNREADABLE, err)
        self.assertEqual(REFUSAL_UNREADABLE, envelope["refusal"]["name"])
        self.assertFalse((self.tmp / "out.txt").exists())


class JsonlContractTests(ToolTestCase):
    ROWS = ('{"ts": "2026-09-03T00:00:00Z", "task": "C3"}\n'
            '{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n'
            '{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n')

    def test_a_jsonl_ledger_merges_and_re_sorts_by_ts(self) -> None:
        """Behaviour only -- no envelope -- so this test passes against the PRE-FIX tool as well, which
        is the claim: the jsonl path is untouched."""
        ours = self.write("ours.jsonl", '{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n'
                                        '{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n')
        theirs = self.write("theirs.jsonl", self.ROWS)
        out = self.tmp / "union.jsonl"

        code, _, err, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(ACCEPTED, code, err)
        stamps = [json.loads(l)["ts"] for l in out.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(sorted(stamps), stamps)
        self.assertEqual(legacy_merge(ours.read_text(encoding="utf-8"), theirs.read_text(encoding="utf-8"),
                                      "union.jsonl"), out.read_text(encoding="utf-8"))

    def test_a_row_without_ts_is_merged_unsorted(self) -> None:
        """The pre-existing soft case, unchanged: exit 0, no sort, both rows kept."""
        ours = self.write("ours.jsonl", '{"task": "no-ts-1"}\n{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n')
        theirs = self.write("theirs.jsonl", '{"task": "no-ts-2"}\n{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n')
        out = self.tmp / "union.jsonl"

        code, stdout, _, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(ACCEPTED, code)
        self.assertIn("sorted_by_ts=False", stdout)
        self.assertIn('{"task": "no-ts-1"}', out.read_text(encoding="utf-8"))
        self.assertIn('{"task": "no-ts-2"}', out.read_text(encoding="utf-8"))

    def test_a_row_that_is_not_json_is_refused_and_nothing_is_written(self) -> None:
        ours = self.write("ours.jsonl", '{"ts": "2026-09-01T00:00:00Z"}\n')
        theirs = self.write("theirs.jsonl", '{"ts": "2026-09-02T00:00:00Z"}\nthis is not json\n')
        out = self.tmp / "union.jsonl"

        code, _, err, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(EXIT_REFUSED, code)
        self.assertIn(REFUSAL_ROW_NOT_JSON, err)
        self.assertIn("not json", err)  # the pre-existing message, kept
        self.assertFalse(out.exists())
        self.assertNoStagedLeftovers()

    def test_the_reason_the_sort_was_skipped_is_reported(self) -> None:
        """The one thing that IS new here, and the 'report the shape it saw' half of the contract: an
        operator can see WHY `sorted_by_ts=False` rather than having to infer it."""
        ours = self.write("ours.jsonl", '{"task": "no-ts-1"}\n{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n')
        theirs = self.write("theirs.jsonl", '{"task": "no-ts-2"}\n{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n')
        out = self.tmp / "union.jsonl"

        code, _, err, envelope = self.run_tool(ours, theirs, out, "--json")

        self.assertEqual(ACCEPTED, code, err)
        self.assertFalse(envelope["sortedByTs"])
        self.assertEqual(2, envelope["rowsWithoutTs"])
        self.assertIn("UNSORTED", err)


class ReportTests(ToolTestCase):
    """The `--json` envelope and the shape report are NEW capability (the brief's third requirement),
    so every test here is unrunnable against the pre-fix tool rather than failing on behaviour. They
    are kept apart from ParityTests for exactly that reason."""

    def test_the_shape_of_every_input_is_reported(self) -> None:
        """So an operator can tell 'no conflicts' from 'I could not parse this' -- the run reports what
        it saw for all three paths, not only the ones it merged."""
        ours = self.write("ours.jsonl", '{"ts": "2026-09-01T00:00:00Z", "task": "A1"}\n')
        theirs = self.write("theirs.jsonl", '{"ts": "2026-09-02T00:00:00Z", "task": "B2"}\n')
        out = self.tmp / "union.jsonl"

        code, _, _, envelope = self.run_tool(ours, theirs, out, "--json")

        self.assertEqual(ACCEPTED, code)
        self.assertEqual({"ours", "theirs", "out"}, set(envelope["inputs"]))
        for role in ("ours", "theirs"):
            self.assertEqual("jsonl", envelope["inputs"][role]["shape"])
            self.assertEqual(1, envelope["inputs"][role]["lines"])
            self.assertEqual(0, envelope["inputs"][role]["conflictMarkers"])
        self.assertEqual(uao.SCHEMA, envelope["schema"])
        self.assertTrue(envelope["ok"])
        self.assertEqual(ACCEPTED, envelope["exitCode"])
        # A hex digest is two characters per digest byte. That is the invariant; 64 is what it
        # happens to be for SHA-256 today, and hard-coding the number would pin a property of
        # a hash this test does not own. guard-population-pin is right that a bare literal here
        # is a marker with nothing behind it - and its own standard says the fix is never to
        # add a pin marker to such a line but to rewrite it as the contract, so this states the
        # contract instead. It also fails if the tool ever stops emitting a hex digest.
        self.assertEqual(hashlib.sha256().digest_size * 2, len(envelope["out"]["sha256"]))

    def test_the_human_summary_keeps_its_original_fields(self) -> None:
        """resolve-append-only.ps1 reads only the exit code, but a human greps this line: the original
        fields must stay in their original order, with the new ones appended."""
        ours = self.write("ours.md", "# T\n\n- [ ] A1\n")
        theirs = self.write("theirs.md", "# T\n\n- [ ] B2\n")
        out = self.tmp / "union.md"

        code, stdout, _, _ = self.run_tool(ours, theirs, out)

        self.assertEqual(ACCEPTED, code)
        line = stdout.strip()
        # The original fields, in their original order, with the new ones appended -- a human greps
        # this line, and resolve-append-only.ps1 reads only the exit code.
        self.assertRegex(
            line,
            re.escape(str(out)) + r": ours=\d+ theirs=\d+ theirs_only=\d+ result=\d+ "
            r"sorted_by_ts=(True|False) shape=lines roundtrip=ok",
        )

    def test_conflict_markers_in_an_input_are_stripped_and_counted(self) -> None:
        conflicted = "alpha\n<<<<<<< HEAD\nbeta\n=======\ngamma\n>>>>>>> branch\n"
        ours = self.write("ours.md", conflicted)
        theirs = self.write("theirs.md", "alpha\ndelta\n")
        out = self.tmp / "union.md"

        code, _, _, envelope = self.run_tool(ours, theirs, out, "--json")

        self.assertEqual(ACCEPTED, code)
        self.assertEqual(3, envelope["inputs"]["ours"]["conflictMarkers"])
        self.assertEqual(["alpha", "beta", "gamma", "delta"], out.read_text(encoding="utf-8").splitlines())


# ---------------------------------------------------------------------------------------------
# 3b. The round trip. The predicate is what guarantees a file the tool cannot read back never
# reaches the destination; it is unreachable from the CLI by construction, so it is driven here with
# crafted corrupt input and an injected failing validator rather than through a green exit code.
# ---------------------------------------------------------------------------------------------
class RoundTripTests(ToolTestCase):
    """The predicate is driven with REAL files on disk, because that is what it reads: a test that fed
    it a string would be testing a different function."""

    def staged(self, text: str) -> str:
        path = self.tmp / "union.md.staged-x.tmp"
        path.write_text(text, encoding="utf-8", newline="")
        return str(path)

    def test_the_exact_union_survives(self) -> None:
        uao.verify_round_trip(self.staged("alpha\nbeta\n"), ["alpha", "beta"], jsonl=False)  # no raise

    def test_a_reordered_file_is_refused(self) -> None:
        with self.assertRaises(uao.Refusal) as caught:
            uao.verify_round_trip(self.staged("beta\nalpha\n"), ["alpha", "beta"], jsonl=False)
        self.assertEqual("OUTPUT-ROUNDTRIP-FAILED", caught.exception.name)
        self.assertEqual(uao.EXIT_ROUNDTRIP, caught.exception.exit_code)
        self.assertIn("different order", caught.exception.detail)

    def test_a_truncated_file_is_refused(self) -> None:
        with self.assertRaises(uao.Refusal) as caught:
            uao.verify_round_trip(self.staged("alpha\n"), ["alpha", "beta"], jsonl=False)
        self.assertIn("wrote 2 lines, read back 1", caught.exception.detail)

    def test_a_written_jsonl_row_that_no_longer_parses_is_refused(self) -> None:
        # The lines are byte-identical to what was expected, so only the per-row re-parse can catch
        # this -- which is why it is a separate assertion from the truncation case above.
        with self.assertRaises(uao.Refusal) as caught:
            uao.verify_round_trip(self.staged('{"ts": "1"}\nnot json at all\n'),
                                  ['{"ts": "1"}', "not json at all"], jsonl=True)
        self.assertEqual("OUTPUT-ROUNDTRIP-FAILED", caught.exception.name)
        self.assertIn("does not parse", caught.exception.detail)

    def test_a_staged_file_that_cannot_be_read_is_a_round_trip_failure_not_a_read_failure(self) -> None:
        with self.assertRaises(uao.Refusal) as caught:
            uao.verify_round_trip(str(self.tmp / "absent"), ["alpha"], jsonl=True)
        self.assertEqual("OUTPUT-ROUNDTRIP-FAILED", caught.exception.name)
        self.assertNotIn("INPUT-UNREADABLE", caught.exception.name)

    def test_the_destination_is_replaced_only_after_the_validator_has_seen_the_whole_file(self) -> None:
        out = self.write("union.md", "previous\n")
        seen = {}

        def validator(staged: str) -> None:
            seen["text"] = Path(staged).read_text(encoding="utf-8")

        uao.atomic_write_validated(str(out), "alpha\nbeta\n", validator)

        self.assertEqual("alpha\nbeta\n", seen["text"])
        self.assertEqual("alpha\nbeta\n", out.read_text(encoding="utf-8"))

    def test_a_failing_validator_leaves_the_destination_and_the_directory_untouched(self) -> None:
        out = self.write("union.md", "previous\n")

        def refuse(staged: str) -> None:
            raise uao.Refusal("OUTPUT-ROUNDTRIP-FAILED", "crafted", uao.EXIT_ROUNDTRIP)

        with self.assertRaises(uao.Refusal) as caught:
            uao.atomic_write_validated(str(out), "alpha\n", refuse)

        self.assertEqual("OUTPUT-ROUNDTRIP-FAILED", caught.exception.name)
        self.assertEqual("previous\n", out.read_text(encoding="utf-8"))
        self.assertNoStagedLeftovers()

    def test_a_staged_file_that_cannot_be_removed_is_reported_not_swallowed(self) -> None:
        """R3, in the tool itself: a temp file the tool cannot clean up is named and located, not
        quietly left. Windows holds the delete on an open handle; POSIX does not, so the case is
        Windows-only rather than silently passing everywhere."""
        if os.name != "nt":
            self.skipTest("an open handle does not block unlink on this platform")
        out = self.write("union.md", "previous\n")
        handle_box = {}

        def refuse(staged: str) -> None:
            handle_box["handle"] = open(staged, "r", encoding="utf-8")
            raise uao.Refusal("OUTPUT-ROUNDTRIP-FAILED", "crafted", uao.EXIT_ROUNDTRIP)

        try:
            with self.assertRaises(uao.Refusal) as caught:
                uao.atomic_write_validated(str(out), "alpha\n", refuse)
            self.assertEqual("OUTPUT-STAGED-LEFT-BEHIND", caught.exception.name)
            self.assertEqual(uao.EXIT_OUTPUT, caught.exception.exit_code)
            staged = next(p for p in self.tmp.iterdir() if ".staged-" in p.name)
            self.assertIn(str(staged), caught.exception.detail)  # the leftover is locatable
        finally:
            handle_box["handle"].close()
            for leftover in self.tmp.glob("*.staged-*"):
                leftover.unlink()

    def test_an_unwritable_destination_directory_is_a_named_refusal(self) -> None:
        missing = self.tmp / "no-such-dir" / "union.md"
        with self.assertRaises(uao.Refusal) as caught:
            uao.atomic_write_validated(str(missing), "alpha\n", lambda staged: None)
        self.assertEqual("OUTPUT-UNWRITABLE", caught.exception.name)
        self.assertEqual(uao.EXIT_OUTPUT, caught.exception.exit_code)


class VocabularyTests(unittest.TestCase):
    """The shape vocabulary is a CLOSED set the code owns, so it is asserted as a set, not a count."""

    def test_the_structured_suffixes_are_exactly_the_documented_ones(self) -> None:
        self.assertEqual(
            {".json", ".yaml", ".yml", ".toml", ".xml", ".ini", ".cfg", ".conf"},
            set(uao.STRUCTURED_SUFFIXES),
            "widening or narrowing this set is a reviewed change, not an accident",
        )

    def test_the_jsonl_suffixes_cannot_overlap_the_structured_ones(self) -> None:
        self.assertEqual(set(), set(uao.JSONL_SUFFIXES) & set(uao.STRUCTURED_SUFFIXES))

    def test_every_refusal_name_maps_to_a_non_zero_exit(self) -> None:
        for name in ("INPUT-UNREADABLE", "INPUT-ROW-NOT-JSON", "INPUT-SHAPE-UNSUPPORTED",
                     "OUTPUT-ROUNDTRIP-FAILED", "OUTPUT-UNWRITABLE", "OUTPUT-STAGED-LEFT-BEHIND"):
            with self.subTest(name=name):
                self.assertNotEqual(ACCEPTED, getattr(uao, {
                    "INPUT-UNREADABLE": "EXIT_INPUT_UNREADABLE",
                    "INPUT-ROW-NOT-JSON": "EXIT_INPUT_UNREADABLE",
                    "INPUT-SHAPE-UNSUPPORTED": "EXIT_INPUT_SHAPE",
                    "OUTPUT-ROUNDTRIP-FAILED": "EXIT_ROUNDTRIP",
                    "OUTPUT-UNWRITABLE": "EXIT_OUTPUT",
                    "OUTPUT-STAGED-LEFT-BEHIND": "EXIT_OUTPUT",
                }[name]))

    def test_there_is_no_flag_that_forces_a_structured_merge(self) -> None:
        """An override is how the destructive path gets reopened by the next agent in a hurry. The
        option list is the contract, so it is read off the parser rather than grepped out of prose."""
        options: set[str] = set()
        for action in uao.build_parser()._actions:
            options.update(action.option_strings)
        self.assertEqual({"--ours", "--theirs", "--out", "--json", "-h", "--help"}, options)


if __name__ == "__main__":
    unittest.main()
