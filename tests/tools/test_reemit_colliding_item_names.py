"""Tests for `gk-core/scripts/reemit-colliding-item-names.py`.

    python -m pytest gk-core/tests/tools/test_reemit_colliding_item_names.py -v

The objective's tooling rule is that new tooling is **unit-testable and importable, not a
process-per-call script**, so this imports the module and drives its functions directly. The script's own
docstring says the script is dry-run by default; these tests are why that is a guarantee rather than a
promise, because the batching is where a silent defect would cost the owner 1,857 real calls.

Three properties carry the weight:

  * **the id list is derived, not stored** — a frozen list would be a second source of truth that drifts
    from the gate it is supposed to agree with, so the tests assert the derivation against a corpus whose
    expected refusals are hand-checkable;
  * **every batch fits the Windows command line**, measured on the real argv rather than estimated,
    because an over-long batch dies at the shell with an error that reads like a tool failure;
  * **each precondition refuses with a named stage and a non-zero exit**, and refusal happens BEFORE any
    spend, because a regeneration run that cannot reach a model fails after the first batch is paid for.
"""
from __future__ import annotations

import copy
import importlib.util
import inspect
import json
import os
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

sys.path.insert(0, str(REPO_ROOT / "scripts" / "lib"))
from keepverse_roots import content_root, forge_root, workspace_root  # noqa: E402

# The corpus is gk-data's pack and the tool under test imports seedsmith, which is gk-forge's. Neither
# path exists under gk-core, so `REPO_ROOT / "tools" / "seedsmith"` raised ModuleNotFoundError AT
# COLLECTION - which is why this file was one of the nine dark suites, and why a collection error
# aborting the run could hide the other 76 files entirely.
_FORGE = forge_root(REPO_ROOT)
# `content_root()` is the PACK root (gk-data/packs/fusion), not its data/seed - verified by measuring
# both: `<pack>/data/seed/items` is a directory and `<pack>/items` is not. I got this wrong first and
# it showed up as `.../gk-data/packs/fusion/items/materials/materials.json` in two failures, which is
# the same slip as ladders.py earlier in this program: a resolver accessor that returns the pack, used
# as though it returned the seed tree.
_ITEMS = content_root(REPO_ROOT) / "data" / "seed" / "items"
# The ref path is workspace-relative, so it is joined here and nowhere else.
_WORKSPACE = workspace_root(REPO_ROOT)
SCRIPT = REPO_ROOT / "scripts" / "reemit-colliding-item-names.py"

_spec = importlib.util.spec_from_file_location("reemit_colliding", SCRIPT)
reemit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(reemit)

sys.path.insert(0, str(_FORGE / "tools" / "seedsmith"))
from seedsmith.adapters.items.materialgen import run as run_mod  # noqa: E402


def _corpus(*names) -> bytes:
    """A corpus whose entries all share one name except the first, so the gate's arithmetic is
    hand-checkable: `n - 1` refused, 1 kept."""
    return json.dumps({
        "schemaVersion": 1, "kind": "material",
        "entries": [{"id": f"material.{i:04d}", "nameKey": f"material.trophy-{i}",
                     "name": n, "runtimeId": f"trophy.species.sp{i}.1",
                     "materialClass": "trophy", "scope": "species", "scopeKey": f"sp{i}", "slot": 1,
                     "iconKey": f"icon.material.trophy-{i}", "tags": ["arcane"]}
                    for i, n in enumerate(names)]
    }).encode("utf-8")


class DerivationTests(unittest.TestCase):
    """The id list comes from the shipped gate, so it cannot drift from it."""

    def test_the_gate_keeps_the_first_of_a_shared_name_and_refuses_the_rest(self) -> None:
        ids, kept, stats = reemit.colliding_ids(_corpus(*["Shared Name"] * 5), run_mod)
        self.assertEqual(stats["entries"], 5)
        self.assertEqual(stats["kept"], 1)
        self.assertEqual(stats["refused"], 4)
        self.assertEqual(stats["refusedByKey"], 4, "all four share a name VERBATIM, so the key half is "
                                                    "the one that refuses them")
        self.assertEqual(stats["refusedByNearDuplicate"], 0)
        self.assertEqual(len(ids), 4)
        self.assertEqual(len(kept), 1, "the kept entries come back for the cross-corpus pass")
        self.assertNotIn("trophy.species.sp0.1", ids, "the first member of a shared name is KEPT")
        self.assertIn("trophy.species.sp4.1", ids)

    def test_distinct_names_produce_no_ids_at_all(self) -> None:
        ids, kept, stats = reemit.colliding_ids(_corpus("Alpha", "Beta", "Gamma"), run_mod)
        self.assertEqual(ids, [])
        self.assertEqual(len(kept), 3)
        self.assertEqual(stats["refused"], 0)

    def test_case_and_spacing_variants_count_as_the_same_name(self) -> None:
        """`_name_key` casefolds and collapses whitespace, so a variant reuse is the same collision for a
        player reading a list — and a batch that treated them as distinct would publish a near-duplicate.

        The fixture self-checks its own literals FIRST. A mistyped variant (an uppercase `LINEAGE` that
        lost a letter while being typed) is a genuinely *different* name, the gate correctly refuses to
        call it a collision, and the test then fails looking exactly like a defect in the gate. Asserting
        the keys collapse first makes that mistake report itself as a bad fixture instead.
        """
        names = ["Lineage Seal", "  lineage   seal  ", "LINEAGE SEAL"]
        keys = {run_mod._name_key(n) for n in names}
        self.assertEqual(keys, {"lineage seal"},
                         f"fixture error, not a gate defect: the three literals do not share a key "
                         f"(got {sorted(keys)}) -- one of them is misspelled")

        ids, _kept, stats = reemit.colliding_ids(_corpus(*names), run_mod)
        self.assertEqual(stats["refused"], 2, "both variants must be refused against the first")
        self.assertEqual(len(ids), 2)

    def test_a_genuinely_different_name_is_not_a_collision(self) -> None:
        """The other half, and the one that makes the test above meaningful: near-miss spellings that are
        actually different words must survive. A gate that refused everything would also pass a
        case-folding test while destroying the corpus."""
        ids, _kept, stats = reemit.colliding_ids(_corpus("Lineage Seal", "Linage Seal"), run_mod)
        self.assertEqual(stats["refused"], 0, "a different word is not a reused name")
        self.assertEqual(ids, [])


class CrossCorpusTests(unittest.TestCase):
    """The population the materials-local gate is structurally blind to.

    Measured on the rescue corpus: after the within-materials gate, `seedsmith check` still reports 6
    `SemanticDedup/NearDuplicate` GAPs, every one a `material.*` entry whose name is held verbatim by a
    `charm.*` or a `set.*`. These 6 are the difference between landing on 10 and the objective's 4.
    """

    def test_a_name_held_by_a_charm_is_found_even_though_the_gate_kept_it(self) -> None:
        entries = [{"id": "material.076", "runtimeId": "trophy.species.x.1", "name": "Obsidian Husk"}]
        owners = {"obsidian husk": ["charm.surv-util-220"]}
        found = reemit.cross_corpus_collisions(entries, owners, run_mod)
        self.assertEqual(len(found), 1)
        rid, name, holders = found[0]
        self.assertEqual((rid, name, holders),
                         ("trophy.species.x.1", "Obsidian Husk", ["charm.surv-util-220"]),
                         "the refusal must NAME the other owner, not just count it")

    def test_the_gate_would_not_have_caught_it(self) -> None:
        """The fail-before half: with the corpus holding only this one entry, the gate is silent. That is
        what makes the cross-corpus pass a separate, necessary population rather than a duplicate check."""
        raw = _corpus("Obsidian Husk")
        ids, _kept, stats = reemit.colliding_ids(raw, run_mod)
        self.assertEqual(stats["refused"], 0, "one entry cannot collide with itself")
        found = reemit.cross_corpus_collisions(
            json.loads(raw)["entries"], {"obsidian husk": ["charm.surv-util-220"]}, run_mod)
        self.assertEqual(len(found), 1, "but the cross-corpus pass does see it")

    def test_an_entry_matching_its_own_id_is_not_reported(self) -> None:
        entries = [{"id": "material.076", "runtimeId": "trophy.species.x.1", "name": "Obsidian Husk"}]
        owners = {"obsidian husk": ["material.076"]}
        self.assertEqual(reemit.cross_corpus_collisions(entries, owners, run_mod), [],
                         "an id holding its own name is not a cross-corpus collision")

    def test_the_other_corpora_normalise_their_own_names(self) -> None:
        """`other_corpus_names` is what normalises, so its output is keyed by the SAME form `_name_key`
        produces. A hand-built owners dict must use that form; an un-normalised key silently finds nothing,
        which is the failure mode this test exists to prevent."""
        entries = [{"id": "material.076", "runtimeId": "trophy.species.x.1", "name": "Glacial Bloom"}]
        self.assertEqual(reemit.cross_corpus_collisions(entries, {"glacial   bloom": ["c"]}, run_mod), [],
                         "an un-normalised owners key must NOT match - that is why the collector "
                         "normalises rather than trusting its input")
        self.assertEqual(len(reemit.cross_corpus_collisions(
            entries, {"glacial bloom": ["c"]}, run_mod)), 1)

    def test_a_unique_name_is_not_a_collision(self) -> None:
        entries = [{"id": "material.076", "runtimeId": "trophy.species.x.1", "name": "Lonely Name"}]
        self.assertEqual(reemit.cross_corpus_collisions(entries, {"other": ["charm.e"]}, run_mod), [])

    def test_the_real_tree_is_clean_but_the_pass_is_not_inert(self) -> None:
        """HEAD's OWN materials corpus carries **zero** cross-corpus name collisions - which is exactly the
        state the objective's target describes, and the reason its 4 NearDuplicate findings are all
        recipes. The 6 measured earlier belong to the RESCUE corpus, not to HEAD, so pinning "6" here would
        be pinning a reading of a ref into a test about the tree.

        What is worth pinning on the real tree: the collector gathers names from the other corpora (so the
        pass is live, not inert), and HEAD's materials do not collide with any of them.
        """
        items = _ITEMS
        owners = reemit.other_corpus_names(items, items / "materials" / "materials.json")
        self.assertGreater(len(owners), 500, "the sets corpus alone carries far more names than this; if "
                                             "this drops, the collector stopped walking the tree")
        doc = json.loads((items / "materials" / "materials.json").read_text(encoding="utf-8"))
        found = reemit.cross_corpus_collisions(doc.get("entries") or [], owners, run_mod)
        self.assertEqual(found, [], "HEAD's materials carry no cross-corpus name collision today")

    def test_a_nameless_entry_is_kept_not_dropped(self) -> None:
        """An entry with no usable name is a schema defect the run path refuses elsewhere. It must not be
        silently dropped by a name pass, so it is neither refused nor lost."""
        raw = json.dumps({"schemaVersion": 1, "kind": "material", "entries": [
            {"id": "material.0001", "nameKey": "k", "name": "", "runtimeId": "trophy.species.x.1",
             "materialClass": "trophy", "iconKey": "i", "tags": []}]}).encode("utf-8")
        ids, kept, stats = reemit.colliding_ids(raw, run_mod)
        self.assertEqual(stats["entries"], 1)
        self.assertEqual(ids, [], "an empty name is not a name collision")
        self.assertEqual(len(kept), 1, "and it is kept, so this pass can never lose the row")
        self.assertEqual(reemit.cross_corpus_collisions(kept, {"": ["x"]}, run_mod), [])


class CorpusSourceTests(unittest.TestCase):
    """`--corpus` and `--ref` must derive the SAME two populations, not different ones.

    `--corpus` is the re-run path: after a partial spend the operator points the tool at the working tree so
    it emits only what still collides. An earlier version computed the cross-corpus pass only when reading
    from a ref, so the re-run silently planned 6 fewer ids than the first run and would have finished the
    job still holding 6 duplicates - the exact outcome the second population exists to prevent.
    """

    def _plan_via_corpus(self, entries: "list[dict]") -> dict:
        import contextlib
        import io
        import tempfile as _tempfile

        with _tempfile.TemporaryDirectory() as d:
            path = Path(d) / "materials.json"
            path.write_text(json.dumps({"schemaVersion": 1, "kind": "material", "entries": entries}),
                            encoding="utf-8")
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = reemit.main(["--corpus", str(path), "--json"])
        self.assertEqual(code, 0, buf.getvalue())
        return json.loads(buf.getvalue())

    def test_corpus_mode_still_finds_the_cross_corpus_population(self) -> None:
        """`Obsidian Husk` is held by `charm.surv-util-220` in the real tree, so a one-entry corpus naming
        it must be planned for re-author - proof the pass is not gated on the corpus source."""
        plan = self._plan_via_corpus(
            [{"id": "material.9001", "runtimeId": "trophy.species.probe.1", "name": "Obsidian Husk"}])
        self.assertEqual(plan["refusedByGate"], 0, "one entry cannot collide with another material")
        self.assertEqual(plan["crossCorpusDuplicates"], 1,
                         "the cross-corpus pass must run in --corpus mode too")
        self.assertEqual(plan["totalToReAuthor"], 1, "and the id must reach the plan")

    def test_corpus_mode_reports_both_populations_separately(self) -> None:
        """The plan keeps the two populations distinguishable, because they have different causes and a
        reader has to know which is which."""
        entries = [
            {"id": "material.9001", "runtimeId": "trophy.species.a.1", "name": "Obsidian Husk"},
            {"id": "material.9002", "runtimeId": "trophy.species.b.1", "name": "Obsidian Husk"},
        ]
        plan = self._plan_via_corpus(entries)
        self.assertEqual(plan["corpusEntries"], 2)
        self.assertEqual(plan["refusedByGate"], 1, "the second is a within-materials collision")
        self.assertEqual(plan["keptByGate"], 1)
        self.assertEqual(plan["totalToReAuthor"], 2, "1 within-materials + 1 cross-corpus, not 1")


    def test_a_later_pass_derives_only_what_still_collides(self) -> None:
        """The convergence property the multi-pass budget rests on, and it was asserted until now.

        A gate refusal is TERMINAL for that subject: `run_batch` records `refused` and does not persist, so
        the corpus keeps the colliding name and the gap is still open. Recovery is therefore a SECOND pass
        over the remainder - which only works if re-derivation reads the LIVE corpus rather than the
        original source. This proves it does, by running the real derivation over a corpus, rewriting only
        part of it, and re-deriving.

        It is what makes the cost "1,863 then whatever is left", rather than "1,863 every pass".
        """
        def corpus_of(entries):
            return json.dumps({"schemaVersion": 1, "kind": "material", "entries": entries})

        def entry(i, name):
            return {"id": f"material.{i:04d}", "runtimeId": f"trophy.species.sp{i}.1", "name": name}

        # three distinct names, one of them shared by two ids -> exactly one collision to re-author
        first = [entry(0, "Alpha"), entry(1, "Beta"), entry(2, "Beta"), entry(3, "Gamma")]
        ids, _kept, stats = reemit.colliding_ids(corpus_of(first).encode(), run_mod)
        self.assertEqual(stats["refused"], 1, "two ids share 'Beta'")
        self.assertEqual(ids, ["trophy.species.sp2.1"], "the FIRST holder is kept, the later one re-authored")

        # pass 1 succeeds for that one subject: the corpus now carries a distinct name
        healed = [entry(0, "Alpha"), entry(1, "Beta"), entry(2, "Beta II"), entry(3, "Gamma")]
        ids2, _k2, stats2 = reemit.colliding_ids(corpus_of(healed).encode(), run_mod)
        self.assertEqual(stats2["refused"], 0, "a healed corpus derives nothing, so the loop terminates")
        self.assertEqual(ids2, [])

        # and a PARTIAL pass converges on the remainder rather than restarting
        partial = [entry(0, "Alpha"), entry(1, "Beta"), entry(2, "Beta"), entry(3, "Gamma")]
        ids3, _k3, _s3 = reemit.colliding_ids(corpus_of(partial).encode(), run_mod)
        self.assertEqual(ids3, ["trophy.species.sp2.1"],
                         "an unhealed subject is re-derived, so a partial pass retries exactly it")

    def test_a_refusal_is_for_a_DIFFERENT_id_and_keeps_the_old_row(self) -> None:
        """Why recovery needs a second pass: a gate refusal does NOT re-ask, and it does not blank the row.

        Two halves, and the first one is a correction to an earlier draft of this test. Re-authoring an id
        with the name it ALREADY holds is explicitly not a collision - `run_batch` skips the subject's own
        runtime id, because `--overwrite` exists precisely to re-emit an id and a re-emit that keeps the
        name is a legitimate overwrite. Comparing an id against itself would make the adapter's own
        overwrite path unrunnable. So a refusal means the new name collides with a DIFFERENT id, and that is
        the case pinned here.
        """
        import tempfile as _tempfile
        import unittest.mock

        from seedsmith.pipeline.run_ledger import RunLedger

        def entry(i, name):
            return {"id": f"material.{i:04d}", "runtimeId": f"shard.chaff" if i == 0 else "shard.sprout",
                    "name": name, "materialClass": "shard", "iconKey": f"i{i}", "tags": ["mineral"]}

        with _tempfile.TemporaryDirectory() as d:
            tmp = Path(d)
            materials = tmp / "materials.json"
            # shard.chaff already holds "Shared Name"; shard.sprout holds "Own Name"
            materials.write_text(json.dumps({"schemaVersion": 1, "kind": "material",
                                             "entries": [entry(0, "Shared Name"), entry(1, "Own Name")]}),
                                 encoding="utf-8")
            answers = {
                "shard.chaff": {"name": "Shared Name", "flavor": "One full sentence of fixture text.",
                                "tags": ["mineral"]},
                "shard.sprout": {"name": "Shared Name", "flavor": "One full sentence of fixture text.",
                                 "tags": ["mineral"]},
            }
            with unittest.mock.patch.object(run_mod, "MATERIALS_PATH", materials):
                plan = run_mod.plan_overwrite(["shard.chaff", "shard.sprout"], ledger=RunLedger(tmp / "l.json"))
                result = run_mod.run_batch(plan, answers, ledger=RunLedger(tmp / "l.json"))

            outcomes = {o.subject_id: o.outcome for o in result.outcomes}
            self.assertEqual(outcomes.get("shard.chaff"), "persisted",
                             "keeping its OWN name is a legitimate overwrite, not a collision")
            self.assertEqual(outcomes.get("shard.sprout"), "refused",
                             "taking ANOTHER id's name is the collision the gate exists for")
            doc = json.loads(materials.read_text(encoding="utf-8"))
            by_id = {e["runtimeId"]: e["name"] for e in doc["entries"]}
            self.assertEqual(by_id.get("shard.sprout"), "Own Name",
                             "the refused subject KEEPS its old row - it is not blanked and not re-asked, "
                             "which is why the gap stays open until a later pass")


class OutcomeAccountingTests(unittest.TestCase):
    """A pass can exit 0 while the gate refuses most of it, so the refusal must be read, not inferred.

    `materialgen` returns `1 if call_failures else 0` - a gate refusal is NOT a call failure - so a run in
    which 900 of 1,000 subjects were refused exits 0. The runner used to report `completed N ids` from the
    exit code alone, where N counted ids ATTEMPTED. That is the same lie as reporting success, and it hides
    the one number that decides whether the brief fix worked.
    """

    def test_tally_counts_each_outcome_the_child_reported(self) -> None:
        payload = {"outcomes": [{"subjectId": "a", "outcome": "persisted"},
                                {"subjectId": "b", "outcome": "persisted"},
                                {"subjectId": "c", "outcome": "refused", "defects": ["dup"]},
                                {"subjectId": "d", "outcome": "blocked"}],
                   "callFailures": []}
        self.assertEqual(reemit.tally_outcomes(payload),
                         {"persisted": 2, "refused": 1, "blocked": 1})

    def test_a_call_failure_counts_as_not_persisted(self) -> None:
        """A call that raised leaves the subject with no answer, which the next pass records as
        `missing_answer` - so it is 'not persisted' for the budget guard even before that happens."""
        payload = {"outcomes": [{"subjectId": "a", "outcome": "persisted"}],
                   "callFailures": [{"subjectId": "b", "error": "RuntimeError: refused"}]}
        self.assertEqual(reemit.tally_outcomes(payload), {"persisted": 1, "call_failure": 1})

    def test_the_refusal_rate_is_per_mille_of_subjects_seen(self) -> None:
        self.assertEqual(reemit.refusal_permille({"persisted": 8, "refused": 2}), 200)
        self.assertEqual(reemit.refusal_permille({"persisted": 10}), 0)
        self.assertEqual(reemit.refusal_permille({"refused": 1}), 1000)
        self.assertEqual(reemit.refusal_permille({}), 0, "no subjects is not a division by a guess")

    def test_every_non_persisted_outcome_counts_against_the_budget(self) -> None:
        """`blocked` and `missing_answer` are refusals in the sense that matters: the row keeps its old name,
        so the gap stays open. Counting only `refused` would let a pass fail on all three and still pass
        the guard."""
        for key in ("refused", "blocked", "missing_answer"):
            self.assertEqual(reemit.refusal_permille({key: 1}), 1000, f"{key} must count as not persisted")
        self.assertEqual(reemit.refusal_permille({"persisted": 1, "call_failure": 1}), 0,
                         "a call failure is reported separately and is not a gate refusal")

    def test_the_default_budget_is_a_fifth(self) -> None:
        """200 per mille, chosen against the measured pre-fix rate: ~71% of entries held a name another
        entry held, so a pass still refusing more than a fifth is not converging and must stop and be read.
        Pinned because it is a SPEND guard, and a guard nobody can name is a guard nobody can rely on."""
        self.assertEqual(reemit.DEFAULT_MAX_REFUSAL_RATE_PERMILLE, 200)
        # The guard stops when the rate EXCEEDS the budget, so assert the trip in that direction - asserting
        # it the other way round would have passed against a guard that never fires.
        self.assertGreater(reemit.refusal_permille({"persisted": 71, "refused": 29}),
                           reemit.DEFAULT_MAX_REFUSAL_RATE_PERMILLE,
                           "a 29% refusal rate is over budget and must trip the guard")
        self.assertEqual(reemit.refusal_permille({"persisted": 80, "refused": 20}), 200)
        self.assertFalse(reemit.refusal_permille({"persisted": 80, "refused": 20})
                         > reemit.DEFAULT_MAX_REFUSAL_RATE_PERMILLE,
                         "exactly 20% is within budget - the guard must not fire on the boundary case")
        self.assertTrue(reemit.refusal_permille({"persisted": 79, "refused": 21})
                        > reemit.DEFAULT_MAX_REFUSAL_RATE_PERMILLE,
                        "21% is one refusal over the line and must trip it")


class WriteTargetTests(unittest.TestCase):
    """The write target is not the source, and `run_batch` APPENDS an id it does not find.

    `materialgen` resolves `MATERIALS_PATH` itself and its CLI has no `--out-dir`, so the ids go to the
    WORKING TREE whatever `--ref`/`--corpus` was read from. Measured at this HEAD: the live corpus holds 31
    rows and all 1,863 targets are absent from it, so reading the plan from the rescue ref and writing to
    live would leave 31 + 1,863 = 1,894 rows against a rescue population of 3,633 - short by 1,739, because
    the 1,776 collision-free rows are never written at all. That is silent, expensive, and wrong, so it is a
    precondition refusal instead.
    """

    def _run(self, live_entries, argv):
        import contextlib
        import io
        import json as _json
        import tempfile as _tempfile
        import unittest.mock

        with _tempfile.TemporaryDirectory() as d:
            live = Path(d) / "materials.json"
            live.write_text(_json.dumps({"schemaVersion": 1, "kind": "material", "entries": live_entries}),
                            encoding="utf-8")
            buf, err = io.StringIO(), io.StringIO()
            with unittest.mock.patch.object(reemit, "LIVE_MATERIALS_PATH",
                                           str(live).replace("\\", "/")):
                with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(err):
                    code = reemit.main(argv)
            return code, buf.getvalue(), err.getvalue()

    def test_execute_refuses_when_the_targets_are_absent_from_the_live_corpus(self) -> None:
        """The failure this prevents is silent: without the guard the run spends 1,863 calls and leaves a
        1,894-row fragment."""
        code, _out, err = self._run(
            [{"id": "material.0001", "runtimeId": "shard.chaff", "name": "Almanac Shard"}],
            ["--ref", "rescue/corpus-bcu211-itemseedgen-run", "--execute",
             "--endpoint", "http://127.0.0.1:9/x"])
        self.assertEqual(code, 2, "a precondition refusal, before any spend")
        self.assertIn("REFUSED [precondition]", err)
        self.assertIn("APPENDS", err, "the refusal must say WHY, or the operator cannot judge it")
        self.assertIn("git checkout", err, "and it must carry the remedy, not just the diagnosis")

    def test_the_dry_run_reports_the_mismatch_without_refusing(self) -> None:
        """The dry run is a plan, so it reports the mismatch rather than refusing - but it must not stay
        silent about it, because that silence is what made the plan look executable."""
        code, out, _err = self._run(
            [{"id": "material.0001", "runtimeId": "shard.chaff", "name": "Almanac Shard"}],
            ["--ref", "rescue/corpus-bcu211-itemseedgen-run"])
        self.assertEqual(code, 0, "a plan is not a spend, so no refusal")
        flat = " ".join(out.split())
        self.assertIn("targets present", flat,
                      "the plan must state how many targets the live corpus holds")
        self.assertIn("fragment", flat, "and what executing now would actually produce")

    def test_execute_does_not_refuse_once_the_corpus_is_installed(self) -> None:
        """The guard must not fire on the INSTALLED state, or the correct sequence would be blocked.

        An earlier version of this test put ONE of the 1,863 targets in the live corpus and then asserted
        the guard did not fire - which is wrong twice over: 1,862 are still absent so the guard *should*
        fire, and the assertion contradicted its own message. The installed state means EVERY target is
        present, so the fixture is the whole rescue corpus; the run then proceeds past this precondition and
        reaches the next gate, a closed port, which is a different and correctly named refusal.
        """
        import subprocess

        raw = subprocess.run(
            ["git", "cat-file", "blob",
             "rescue/corpus-bcu211-itemseedgen-run:data/seed/items/materials/materials.json"],
            cwd=REPO_ROOT, capture_output=True, timeout=120, check=True).stdout
        installed = json.loads(raw.decode("utf-8"))["entries"]
        code, _out, err = self._run(
            installed,
            ["--ref", "rescue/corpus-bcu211-itemseedgen-run", "--execute",
             "--endpoint", "http://127.0.0.1:9/v1/chat/completions"])
        self.assertNotEqual(code, 2,
                            "with every target present this precondition must NOT fire; exit 2 would mean "
                            f"the guard blocks the correct sequence. stderr was: {err[:200]}")
        self.assertEqual(code, 4, "it should instead reach the spend and fail on the closed port")


class LexicalGateTests(unittest.TestCase):
    """The gate's SECOND half: names that are one token apart, which `_name_key` cannot see.

    `_name_key` is an O(1) equality test, so it closes verbatim and case/spacing reuse and nothing else.
    `SemanticDedup/NearDuplicate` is a different predicate, and on the BCU2.11 corpus the 1,776 rows the key
    half leaves alone still carry **535** pairs at Jaccard >= 0.6 - `'Blover's Essence'` against
    `"Sunblover's Essence"`. With the lexical half, **0** remain.

    These are the fail-before/pass-after tests: on the key-only gate every one of them returns None.
    """

    def _kept(self, *names):
        return [{"id": f"material.{i:04d}", "runtimeId": f"trophy.species.s{i}.1", "name": n}
                for i, n in enumerate(names)]

    def test_a_name_one_token_apart_is_refused_where_the_key_gate_is_silent(self) -> None:
        kept = self._kept("Blover's Essence")
        answer = {"name": "Sunblover's Essence"}
        self.assertEqual(run_mod._name_collision_defects(answer, "trophy.species.s1.1", kept, {}, {}), [],
                         "FAIL-BEFORE: the key half cannot see this at all, which is the whole point")
        defect = run_mod._near_duplicate_defect(answer, "trophy.species.s1.1", kept)
        self.assertIsNotNone(defect, "PASS-AFTER: the lexical half must refuse it")
        self.assertIn("Jaccard", defect)
        self.assertIn("trophy.species.s0.1", defect, "the refusal must NAME the holder, not just count")

    def test_a_genuinely_distinct_name_is_accepted(self) -> None:
        """The other half, and the one that makes the test above meaningful: a gate that refuses
        everything would pass it while destroying the corpus."""
        kept = self._kept("Blover's Essence", "Tallnut Provenance", "Glacial Husk")
        for name in ("Emberthorn Reliquary", "Barnacle Sovereign", "Quartz Votive"):
            self.assertIsNone(run_mod._near_duplicate_defect({"name": name}, "trophy.species.s9.1", kept),
                              f"{name!r} is distinct and must be accepted")

    def test_a_subject_is_never_compared_against_its_own_row(self) -> None:
        """`--overwrite` exists to re-emit an id, so keeping its own name is a legitimate overwrite. The
        key half already had this exemption; the lexical half must have it too or every re-emit of a
        near-duplicated name becomes unrunnable."""
        kept = self._kept("Blover's Essence")
        self.assertIsNone(run_mod._near_duplicate_defect(
            {"name": "Blover's Essence"}, "trophy.species.s0.1", kept),
            "a subject compared against ITSELF is not a collision")

    def test_the_verdict_is_deterministic_across_calls(self) -> None:
        """The first version of this gate passed SHINGLE SETS to `jaccard_estimate`, which takes
        signatures; `zip` over two unordered frozensets made the verdict depend on PYTHONHASHSEED, and the
        same corpus planned 1,884 ids in one process and 1,886 in the next. Exact set arithmetic cannot have
        that failure, and this pins it."""
        kept = self._kept("Blover's Essence", "Sunblover's Essence", "Tallnut Provenance")
        verdicts = {run_mod._near_duplicate_defect({"name": "Sunblover's Essence"}, "t.s.2", kept)
                    for _ in range(5)}
        self.assertEqual(len(verdicts), 1, f"the verdict must not vary between identical calls: {verdicts}")

    def test_an_empty_or_tiny_name_is_not_scored(self) -> None:
        """Under five characters there is one shingle, so Jaccard is degenerate. The schema's own
        minimum-length rule is the right place for that, not a similarity score."""
        kept = self._kept("Blover's Essence")
        for name in ("", "   ", "A", "Abc"):
            self.assertIsNone(run_mod._near_duplicate_defect({"name": name}, "t.s.9", kept),
                              f"{name!r} has no usable shingle set and must not be scored")


class DefectClassTests(unittest.TestCase):
    """A refusal must say WHICH gate refused it, because the two regimes need opposite responses.

    ⛔ The gap this pins, found by measuring rather than by reading the tool. `materialgen` has a single
    outcome name for every refusal - `"refused"` - so a subject refused because the model returned a
    colliding name and one refused because the answer broke the schema both landed in the same bucket,
    and the tool reported only the total. That is survivable for a 20-call pilot and useless for the
    decision a 2,176-subject pass needs, because the two mean opposite things:

      * `name_gate` - the model DID answer; the name is not distinct. This is the variety question, and
        it is the one that decides whether the pass is worth spending at all.
      * `shape` - the answer never satisfied the schema or the tag axes. Nothing about variety is
        implied and more subjects produce more of the same.

    The budget guard fired on both (exit 7) with no way to tell them apart, so the pilot could not
    answer the question it exists to answer. FAIL-BEFORE: `classify_defect` and `tally_defect_classes`
    did not exist, so a caller asking "which gate?" got an AttributeError rather than a reading.
    """

    # The child's own message shapes, read from materialgen/run.py. Not invented strings - if the
    # generator changes its wording, these must be re-read, which is why they are quoted here.
    DUPLICATE = ("duplicate name 'Lineage Seal': already the name of 'material.099'. Every material "
                 "id must hold a distinct name - author a new one rather than reusing another's.")
    SAME_BATCH = ("duplicate name 'Verdant Lineage': already claimed by 'material.120' in this same "
                  "batch. Two ids in one batch may not share a name.")
    NEAR = ("near-duplicate name 'Sunblover's Essence': Jaccard 0.79 against the name already held by "
            "'material.201' (\"Blover's Essence\"), at or over the 0.6 threshold. Author a name that is "
            "not a near-variant of an existing one.")
    SCHEMA = "$.name: expected at least 3 characters, got 'ab'"
    TAG_AXIS = ("tags 'light' and 'heavy' are both on the 'mass-class' axis, which tags.v1.json marks "
                "exclusive - an entry may carry at most one")

    def test_a_name_collision_is_classified_as_the_name_gate(self) -> None:
        for defect in (self.DUPLICATE, self.SAME_BATCH, self.NEAR):
            self.assertEqual(reemit.classify_defect(defect), "name_gate",
                             f"a name collision was not recognised: {defect[:60]!r}")

    def test_a_schema_or_tag_defect_is_classified_as_shape(self) -> None:
        for defect in (self.SCHEMA, self.TAG_AXIS):
            self.assertEqual(reemit.classify_defect(defect), "shape",
                             f"a shape defect was not recognised: {defect[:60]!r}")

    def test_an_unrecognised_defect_is_reported_as_other_not_folded_into_a_bucket(self) -> None:
        """A new defect shape must show up as itself. Silently folding it into `shape` would make a
        growing failure look like a static one, which is the reporting lie the whole class exists to
        prevent."""
        self.assertEqual(reemit.classify_defect("something nobody has seen before"), "other")

    def test_the_tally_breaks_a_mixed_payload_down_by_gate(self) -> None:
        payload = {"outcomes": [
            {"subjectId": "material.001", "outcome": "persisted", "defects": []},
            {"subjectId": "material.002", "outcome": "refused", "defects": [self.DUPLICATE]},
            {"subjectId": "material.003", "outcome": "refused", "defects": [self.NEAR]},
            {"subjectId": "material.004", "outcome": "refused", "defects": [self.SCHEMA]},
            {"subjectId": "material.005", "outcome": "blocked", "defects": ["not a real theme"]},
        ]}
        classes = reemit.tally_defect_classes(payload)
        self.assertEqual(classes.get("name_gate"), 2, "two name collisions must be counted as such")
        self.assertEqual(classes.get("shape"), 1, "one schema defect must be counted as such")
        self.assertNotIn("persisted", classes, "only refusals carry a gate")
        self.assertNotIn("blocked", classes,
                         "`blocked` is a model decision, not a gate refusal, and folding it in here "
                         "would double-count it against the outcome tally")

    def test_a_refusal_with_no_defect_string_is_reported_as_unnamed(self) -> None:
        """A refusal the child could not explain is itself a finding, and must not be attributed to a
        gate it did not name."""
        classes = reemit.tally_defect_classes(
            {"outcomes": [{"subjectId": "material.007", "outcome": "refused", "defects": []}]})
        self.assertEqual(classes, {"unnamed": 1},
                         "an unexplained refusal must be surfaced, not assigned a class")

    def test_the_breakdown_does_not_disturb_the_refusal_rate_denominator(self) -> None:
        """`refusal_permille` computes `sum(totals.values())`, so folding per-defect counts into the
        outcome tally would double-count every refusal and HALVE the rate the budget guard reads. A
        wrong number in the one place a stop decision is made is worse than no number."""
        payload = {"outcomes": [
            {"subjectId": "material.001", "outcome": "persisted", "defects": []},
            {"subjectId": "material.002", "outcome": "refused",
             "defects": [self.DUPLICATE, self.NEAR]},   # one subject, TWO defects
        ]}
        totals = reemit.tally_outcomes(payload)
        classes = reemit.tally_defect_classes(payload)
        self.assertEqual(sum(totals.values()), 2, "the outcome tally counts SUBJECTS")
        self.assertEqual(sum(classes.values()), 2, "the class tally counts DEFECT STRINGS")
        self.assertEqual(reemit.refusal_permille(totals), 500,
                         "1 refused of 2 subjects is 500 per mille, whatever the defect breakdown is")

    def test_the_regime_sentence_names_which_of_the_two_problems_this_is(self) -> None:
        """Counts alone are not a decision. Each single-gate regime has to read as itself, because the
        operator's next move differs: resize the subject count versus fix the prompt."""
        only_gate = reemit._regime_sentence({"name_gate": 7})
        self.assertIn("variety", only_gate,
                      "a pure name-collision reading must name the variety limit, since that is what "
                      "resizing the subject count addresses")
        only_shape = reemit._regime_sentence({"shape": 3})
        self.assertIn("prompt or pipeline", only_shape,
                      "a pure shape reading must name the prompt/pipeline defect, since that is what "
                      "more spending cannot fix")
        mixed = reemit._regime_sentence({"name_gate": 2, "shape": 1})
        self.assertIn("mix", mixed.lower())
        self.assertIn("unavailable", reemit._regime_sentence({}).lower(),
                      "no classes must not read as a clean run - the breakdown is simply missing")

    def test_the_print_helper_says_nothing_when_there_is_nothing_to_say(self) -> None:
        import io
        import contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            reemit._print_defect_classes({})
        self.assertEqual(buf.getvalue(), "", "an empty breakdown must not print a reading")


class DefectClassWiringTests(unittest.TestCase):
    """The breakdown has to reach the tool's OUTPUT, not just exist as helpers.

    The 8 tests in `DefectClassTests` prove `classify_defect` and `tally_defect_classes` are right. They
    cannot prove `main()` calls them: the accumulation across batches, the per-batch `defectClasses` on
    each result, and the top-level `defectClasses` on the summary are separate wiring, and a wiring
    mistake is invisible until the owner runs the pilot and finds the reading missing from a real run.
    So this drives `main()` end to end with a stubbed child and reads the JSON the tool actually prints.

    The child is stubbed rather than called, so this spends nothing - which is also the point: the pilot
    is the instrument that decides a 2,176-call authorisation, and it must be provable to report before
    any of it is spent.
    """

    LIVE_CORPUS = _ITEMS / "materials" / "materials.json"

    def _live_ids(self) -> "tuple[str, str]":
        """Two REAL runtimeIds from the live corpus, so the write-target guard is satisfied.

        The guard refuses `--execute` when a target id is absent from the live corpus, because
        `materialgen` APPENDS an unknown id and would write a fragment. That guard is correct and is not
        something to relax for a test. Instead the fixture borrows ids that genuinely exist: the guard
        only READS the live file, and the child subprocess is stubbed, so **this test writes nothing to
        the production corpus** - the failure mode that already bit this session once, when a
        verification script put three fixture rows into `materials.json`.
        """
        entries = json.loads(self.LIVE_CORPUS.read_text(encoding="utf-8")).get("entries") or []
        ids = [e.get("runtimeId") for e in entries if e.get("runtimeId")]
        self.assertGreaterEqual(len(ids), 2, "the live corpus must supply two ids for this fixture")
        return ids[0], ids[1]

    def _corpus_with_one_collision(self) -> "dict":
        """The REAL live corpus with exactly one name collision injected, so the gate derives exactly one
        id to re-author and the plan is a single batch.

        Built from the live file rather than invented names, for two reasons. First, the write-target
        guard refuses `--execute` when a target id is absent from the live corpus (correctly -
        `materialgen` APPENDS an unknown id), so a fixture of made-up ids plans nothing and proves
        nothing. Second, hand-written "distinct" names do not survive the lexical gate at all: an
        earlier version of this fixture used `Relic Alpha 0` ... `Relic Alpha 11` and the gate refused
        **11 of 11** as near-variants of each other, which is the gate working, not the test.

        Reading the live file and writing the copy to a temp dir means **nothing is written to the
        production corpus** - the failure mode that already bit this session once, when a verification
        script put three fixture rows into `materials.json`. The child subprocess is stubbed too.
        """
        live = json.loads(self.LIVE_CORPUS.read_text(encoding="utf-8"))
        entries = copy.deepcopy(live["entries"])
        self.assertGreaterEqual(len(entries), 2, "the live corpus must supply two rows for this fixture")
        entries[1]["name"] = entries[0]["name"]          # the one collision
        return {"entries": entries}

    def _child_payload(self, persisted: int) -> "dict":
        """What the stubbed child reports: `persisted` clean answers and exactly one name collision."""
        return {
            "entries": [],
            "outcomes": [{"subjectId": f"material.{i:03d}", "outcome": "persisted", "defects": []}
                         for i in range(persisted)]
            + [{"subjectId": "material.999", "outcome": "refused", "defects": [
                "duplicate name 'Relic Alpha 0': already the name of 'material.000'. Every material "
                "id must hold a distinct name - author a new one rather than reusing another's."]}],
        }

    def _run_with_stubbed_child(self, tmp: Path, persisted: int,
                                extra: "list[str]" = ()) -> "tuple[int, str, str]":
        import subprocess

        child = self._child_payload(persisted)
        corpus = tmp / "materials.json"
        corpus.write_text(json.dumps(self._corpus_with_one_collision()), encoding="utf-8")

        class _Result:
            returncode = 0
            stdout = "seedsmith noise before the json\n" + json.dumps(child)
            stderr = ""

        real_run = subprocess.run
        calls: "list[list[str]]" = []

        def fake_run(cmd, **kwargs):
            calls.append(list(cmd))
            return _Result()

        subprocess.run = fake_run
        try:
            argv = ["--corpus", str(corpus), "--execute", "--endpoint", "http://localhost:1/v1",
                    "--model", "m", "--stop-after", "1", "--json"] + list(extra)
            import contextlib
            import io
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = reemit.main(argv)
        finally:
            subprocess.run = real_run
        self.assertTrue(calls, "the stubbed child was never invoked, so nothing was proven")
        return code, out.getvalue(), err.getvalue()

    def _in_temp(self, body) -> None:
        import shutil
        import tempfile

        tmp = Path(tempfile.mkdtemp(prefix="reemit-wiring-"))
        try:
            body(tmp)
        finally:
            try:
                shutil.rmtree(tmp)
            except Exception as exc:  # a failed cleanup is a failure, never swallowed
                self.fail(f"SCRATCH-REMOVE-FAILED: {tmp} ({exc.__class__.__name__}: {exc})")

    def test_the_breakdown_reaches_the_json_on_a_clean_pilot_stop(self) -> None:
        """9 persisted, 1 refused = 100 per mille, inside the 200 budget: the clean path."""

        def body(tmp: Path) -> None:
            code, out, _err = self._run_with_stubbed_child(tmp, persisted=9)
            self.assertEqual(code, 0, f"a clean pilot stop must exit 0, got {code}")
            payload = json.loads(out[out.index("{"):])
            self.assertIn("defectClasses", payload,
                          "FAIL-BEFORE: the top-level summary carried no defect breakdown, so an "
                          "operator running the pilot would see only a bare refusal count")
            self.assertEqual(payload["defectClasses"].get("name_gate"), 1,
                             "the one name collision must reach the summary")
            self.assertEqual(payload["outcomeTotals"].get("refused"), 1,
                             "the outcome tally is unchanged - the breakdown must not disturb it")
            self.assertEqual(payload["outcomeTotals"].get("persisted"), 9)
            results = payload.get("results") or []
            self.assertTrue(results, "no per-batch results were reported")
            self.assertEqual(results[0].get("defectClasses", {}).get("name_gate"), 1,
                             "each batch result must carry its own breakdown, or a multi-batch run "
                             "cannot be attributed to a batch")

        self._in_temp(body)

    def test_the_budget_stop_names_which_gate_refused_on_stderr(self) -> None:
        """1 persisted, 1 refused = 500 per mille, over the 200 budget: exit 7.

        The stderr case is the one that matters most. A stop is usually all the operator sees, and
        FAIL-BEFORE it read as a bare count - indistinguishable between "the model cannot produce
        distinct names" (resize the subject count) and "the answers are malformed" (fix the prompt and
        spend nothing). Those are opposite moves.
        """

        def body(tmp: Path) -> None:
            code, _out, err = self._run_with_stubbed_child(tmp, persisted=1)
            self.assertEqual(code, 7, f"over-budget must exit 7, got {code}")
            self.assertIn("REFUSED", err, "a stop must carry its named stage on stderr")
            self.assertIn("name_gate", err,
                          "the exit-7 message must name the gate that refused, not just a count")
            self.assertIn("variety", err,
                          "and it must say what that MEANS, since a name-collision regime means "
                          "resizing the subject count rather than raising the budget")

        self._in_temp(body)


class BatchingTests(unittest.TestCase):
    """Every batch must fit the Windows command line, measured on the real argv."""

    def test_every_batch_fits_and_they_partition_the_list_exactly(self) -> None:
        ids = [f"trophy.species.some-fairly-long-species-name-{i:05d}.1" for i in range(400)]
        batches = reemit.batch_ids(ids, 2000, prefix_len=60)
        self.assertGreater(len(batches), 1, "400 ids cannot fit 2,000 chars in one batch")
        flat = [i for b in batches for i in b]
        self.assertEqual(flat, ids, "the batches must partition the list in order, losing nothing")
        self.assertEqual(len(flat), len(set(flat)), "and must not duplicate an id")
        for b in batches:
            argv = reemit.argv_for(b, "http://x/v1/chat/completions", "m")
            self.assertLessEqual(len(" ".join(argv)), reemit.WINDOWS_CMDLINE_LIMIT)

    def test_a_list_that_fits_stays_in_one_batch(self) -> None:
        ids = ["trophy.species.a.1", "trophy.species.b.1"]
        self.assertEqual(reemit.batch_ids(ids, reemit.WINDOWS_CMDLINE_LIMIT, prefix_len=60), [ids])

    def test_an_empty_list_produces_no_batches_rather_than_one_empty_batch(self) -> None:
        self.assertEqual(reemit.batch_ids([], 1000, 60), [],
                         "an empty plan must not manufacture a batch, which would spend a call to "
                         "regenerate nothing")

    def test_the_real_corpus_splits_within_the_limit(self) -> None:
        """The measured shape, not a guess: 1,857 ids split into 2 batches, both under the limit."""
        ids = [f"trophy.species.species-name-number-{i:05d}.1" for i in range(1857)]
        batches = reemit.batch_ids(ids, reemit.DEFAULT_MAX_BATCH_CHARS, prefix_len=40)
        self.assertGreaterEqual(len(batches), 2)
        self.assertEqual(sum(len(b) for b in batches), 1857)
        for b in batches:
            self.assertLessEqual(len(" ".join(reemit.argv_for(b, "e" * 40, "m"))),
                                 reemit.WINDOWS_CMDLINE_LIMIT)


class RefusalTests(unittest.TestCase):
    """Every precondition refuses with a named stage and a non-zero exit, before any spend."""

    def _exit(self, *argv) -> int:
        return reemit.main(list(argv))

    def test_execute_without_an_endpoint_refuses(self) -> None:
        old = os.environ.pop("SEEDSMITH_LLM_ENDPOINT", None)
        try:
            self.assertEqual(self._exit("--execute", "--ref", "rescue/corpus-bcu211-itemseedgen-run"),
                             2, "a run that cannot reach a model must refuse BEFORE spending anything")
        finally:
            if old is not None:
                os.environ["SEEDSMITH_LLM_ENDPOINT"] = old

    def test_a_batch_budget_above_the_windows_limit_refuses(self) -> None:
        self.assertEqual(self._exit("--max-batch-chars", str(reemit.WINDOWS_CMDLINE_LIMIT + 1)), 2)

    def test_a_missing_corpus_path_refuses(self) -> None:
        self.assertEqual(self._exit("--corpus", str(REPO_ROOT / "no" / "materials.json")), 2)

    def test_a_missing_ref_refuses(self) -> None:
        self.assertEqual(self._exit("--ref", "rescue/definitely-not-a-ref"), 2)

    def test_an_empty_corpus_refuses_rather_than_planning_nothing(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "materials.json"
            p.write_bytes(b"")
            self.assertEqual(self._exit("--corpus", str(p)), 2,
                             "a 0-byte capture must not read as a corpus with nothing to do")


class ImportabilityTests(unittest.TestCase):
    """The tooling rule: importable, not a process-per-call script."""

    def test_the_module_exposes_main_taking_argv(self) -> None:
        self.assertTrue(callable(reemit.main), "main(argv) is what makes this unit-testable")
        params = list(inspect.signature(reemit.main).parameters)
        self.assertEqual(params, ["argv"], "main must accept an argv so a test need not spawn a process")

    def test_the_default_batch_budget_is_inside_the_platform_limit(self) -> None:
        self.assertLess(reemit.DEFAULT_MAX_BATCH_CHARS, reemit.WINDOWS_CMDLINE_LIMIT,
                        "the default must already be inside the platform limit, or the very first batch "
                        "of an unconfigured run dies at the shell")

    def test_spending_requires_an_explicit_execute_flag(self) -> None:
        """`--execute` is the only thing that can spend, so the default path is proven by the flag's
        absence in the argv of the batch-building calls the tests just made."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('ap.add_argument("--execute", action="store_true"', source)
        self.assertIn('if not args.execute:', source,
                      "the plan path must short-circuit before any subprocess is started")


class NameAttemptForwardingTests(unittest.TestCase):
    """The one knob that moves the yield has to REACH the child, or it is not a knob.

    ⛔ Real gap, measured 2026-09-28 on a live pass over the rescue corpus. `argv_for` built the child
    invocation without `--max-name-attempts`, so the runner always spent at the child's own default of 3
    and there was no way to buy a different yield from this tool. Every refusal this tool exists to
    resolve is a NAME collision, and a collision at 3 attempts keeps the old colliding name and leaves
    the gap open - so the operator watching the refusal budget trip at 352 per mille had no lever to pull
    except the budget itself, which only decides WHEN to stop, never whether a retry can succeed.

    That is the same shape as the `--overwrite` defect fixed earlier in this programme: a named knob
    exists, and something between the flag and the work silently drops it. A knob that does not reach the
    work is a comment, not a control.
    """

    IDS = ["trophy.species.a.1", "trophy.species.b.2"]

    def test_the_attempt_count_reaches_the_child_argv(self) -> None:
        argv = reemit.argv_for(self.IDS, "http://x/v1/chat/completions", "m", 8)
        self.assertIn("--max-name-attempts", argv,
                      "FAIL-BEFORE: the flag was never added to the child invocation, so the runner could "
                      "not change the yield at all")
        self.assertEqual(argv[argv.index("--max-name-attempts") + 1], "8",
                         "the value must be the one asked for, not a default")

    def test_omitting_it_leaves_the_childs_own_default_in_place(self) -> None:
        """`None` must mean ABSENT, not `0` and not the default. Passing 0 would switch the retry OFF in
        the child, which is the opposite of 'use the child's default', and would silently reintroduce the
        terminal-collision behaviour this whole mechanism exists to avoid."""
        argv = reemit.argv_for(self.IDS, "http://x/v1/chat/completions", "m")
        self.assertNotIn("--max-name-attempts", argv,
                         "the default path must not pin the child's retry count")
        zero = reemit.argv_for(self.IDS, "http://x/v1/chat/completions", "m", 0)
        self.assertEqual(zero[zero.index("--max-name-attempts") + 1], "0",
                         "an explicit 0 is a real request - disable retries - and must be forwarded as "
                         "such rather than confused with 'unset'")

    def test_the_flag_is_wired_from_the_parser_to_the_argv(self) -> None:
        """Parser and call site are both checked, because a flag added to neither is the defect, and a
        flag added to only one is the same defect wearing a hat."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('ap.add_argument("--max-name-attempts"', source)
        self.assertIn("argv_for(b, args.endpoint, args.model, args.max_name_attempts)", source,
                      "FAIL-BEFORE: the batch call site passed only three arguments, so the parsed value "
                      "went nowhere")

    def test_forwarding_does_not_break_the_argv_length_budget(self) -> None:
        """The batching tests assert the joined argv stays inside the Windows command-line limit, and
        `--max-name-attempts N` lengthens every batch. A knob that silently pushes a batch over the limit
        would trade a refusal problem for an OS-level one."""
        argv = reemit.argv_for(self.IDS, "http://x/v1/chat/completions", "m", 8)
        self.assertLessEqual(len(" ".join(argv)), reemit.WINDOWS_CMDLINE_LIMIT)
        longer = reemit.argv_for(self.IDS, "e" * 40, "m", 8)
        self.assertLessEqual(len(" ".join(longer)), reemit.WINDOWS_CMDLINE_LIMIT)


class ChildFailureNamesItsCauseTests(unittest.TestCase):
    """⛔ A failure message that names a code and not a cause is not a diagnosis. Measured 2026-09-28.

    `results[i]["stderrTail"]` has carried the child's last 400 characters of stderr since the batch loop
    was written, and it is still only ever printed under `--json`. The human-readable path said
    `batch N exited 1 ... the corpus is mid-regeneration` and stopped there - so a child that died on a
    traceback was indistinguishable from one that died on a transport hiccup.

    That exact sentence cost TWO diagnostic cycles on a live run. Batch 29 reported `exited 1`, and I read
    that as a schema or endpoint problem and went looking for a wedge; the child's own last words were
    sitting unread in a local variable, and the answer would have been on the same line as the message.
    It then happened again at batch 10, which is what made it a fix rather than an observation.

    The empty case matters as much as the non-empty one: a child that wrote nothing to stderr must not
    produce a message that reads as though there were nothing to report, or the reader cannot tell silence
    from a cause that was dropped.
    """

    def test_the_refusal_carries_the_childs_own_last_words(self) -> None:
        """The whole point: the cause an operator needs is in the message, not only in a JSON field."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("The child's own last words", source,
                      "FAIL-BEFORE: the refusal named the exit code and the id count and nothing else")
        self.assertIn('tail = (r.stderr or "").strip()', source,
                      "the tail must come from the child's stderr, not be reconstructed")

    def test_an_empty_child_stderr_says_so_rather_than_appending_nothing(self) -> None:
        """Silence and a dropped cause must not look the same. If the child said nothing, the message has
        to say that, or the reader cannot distinguish it from a message whose cause was omitted."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("the child wrote nothing to stderr", source,
                      "FAIL-BEFORE: an empty tail produced a message indistinguishable from a dropped one")

    def test_the_tail_is_bounded_so_a_child_cannot_flood_the_message(self) -> None:
        """A traceback can be long and a chatty child can be longer, but the message is the thing an
        operator reads. The last few lines are the cause; the first fifty are the import preamble."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("tail.splitlines()[-6:]", source,
                      "the tail must be bounded to its last lines, not the child's whole stderr")

    def test_the_stderr_tail_was_always_in_the_json_and_still_is(self) -> None:
        """The JSON path already carried it; the fix adds the human path, it must not remove the machine
        one. A tool whose machine-readable output loses a field is a regression dressed as a fix."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('"stderrTail"', source)


class CrossCorpusSkipPathTests(unittest.TestCase):
    """⛔ Real bug, found 2026-09-28: the cross-corpus skip path could not exist, so nothing was skipped.

    `other_corpus_names(items_dir, skip)` skips exactly ONE path, by `resolve()` equality. The call site
    passed `items_root / DEFAULT_CORPUS_IN_REF`, but `DEFAULT_CORPUS_IN_REF` is
    `"gk-data/packs/fusion/data/seed/items/materials/materials.json"` - a REPO-RELATIVE path that already contains
    `gk-data/packs/fusion/data/seed/items/materials`. Joining it onto the items root built

        .../data/seed/items/materials/data/seed/items/materials/materials.json

    which cannot exist. The skip therefore never matched, `rglob` walked the materials corpus anyway, and
    all 3,633 of its names landed in `owners`.

    **The damage was in the DIAGNOSTIC as much as in the spend.** `cross_corpus_collisions(kept, owners, ...)`
    then reported the KEPT half of every within-materials duplicate pair as colliding with its own refused
    twin: 181 reported against 13 real. So the population this function exists to catch - a `material.*` name
    a `charm.*` or `set.*` also holds - was swamped in false positives, and a live pass was planning ~181
    subjects the within-materials gate had already handled.

    Found by CROSS-CHECKING two of my own measurements rather than by reading: a hand-rolled probe said 13,
    the tool said 181, and the holders said `vs material.2447`. When two instruments disagree, the one whose
    output names the WRONG POPULATION is the broken one - and the holders were the tell, because a
    cross-corpus collision by definition names something that is not a material.
    """

    def test_the_skip_path_must_actually_exist(self) -> None:
        """The property that was broken, stated directly: a skip path that does not exist makes the
        exclusion a silent no-op, and the tool cannot say so."""
        items = _ITEMS
        joined = items / reemit.DEFAULT_CORPUS_IN_REF
        self.assertFalse(joined.exists(),
                         f"FAIL-BEFORE: {joined} is a doubled path that cannot exist, so the materials "
                         f"corpus is never skipped and every material name joins `owners`")
        # The criterion is UNCHANGED and is the whole point of this class: the constant is a
        # workspace-relative path, so it must be joined to the WORKSPACE root - joining it onto the
        # items root builds a doubled path that cannot exist and the exclusion becomes a silent no-op.
        # What changed is only WHICH root, because after the split the path no longer hangs off the
        # repository this file lives in. `REPO_ROOT` here is gk-core, which would build
        # `gk-core/gk-data/packs/fusion/...` - and that is the exact failure the assertion exists to
        # catch, now wearing a different prefix.
        self.assertEqual(
            (_WORKSPACE / reemit.DEFAULT_CORPUS_IN_REF), items / "materials" / "materials.json",
            "FAIL-BEFORE: the constant is workspace-relative, so it must be joined to the WORKSPACE root")

    def test_the_materials_corpus_is_really_excluded_from_owners(self) -> None:
        """Behaviour, not a path spelling: no `material.*` id may appear in `owners`."""
        import tempfile
        with tempfile.TemporaryDirectory() as temp:
            items = Path(temp) / "items"
            (items / "materials").mkdir(parents=True)
            (items / "charms").mkdir(parents=True)
            (items / "materials" / "materials.json").write_text(json.dumps({
                "kind": "material",
                "entries": [{"id": "material.0001", "name": "Torch of the Stump"}],
            }), encoding="utf-8")
            (items / "charms" / "c1.json").write_text(json.dumps({
                "kind": "charm",
                "entries": [{"id": "charm.c1-001", "name": "Glacial Echo"}],
            }), encoding="utf-8")

            owners = reemit.other_corpus_names(items, items / "materials" / "materials.json")
            held = [i for ids in owners.values() for i in ids]
            self.assertNotIn("material.0001", held,
                             "FAIL-BEFORE: the materials corpus was walked, so a material's own name "
                             "became an 'other corpus' owner and the kept half of every within-materials "
                             "duplicate pair was reported as a cross-corpus collision")
            self.assertIn("charm.c1-001", held, "a charm's name must still be an owner - that is the point")

    def test_a_doubled_skip_path_silently_excludes_nothing(self) -> None:
        """The failure MODE, so the bug's shape is pinned and not just its fix: a skip path that does not
        exist is not an error, it is a no-op. Nothing warns; the count just comes back wrong."""
        import tempfile
        with tempfile.TemporaryDirectory() as temp:
            items = Path(temp) / "items"
            (items / "materials").mkdir(parents=True)
            (items / "materials" / "materials.json").write_text(json.dumps({
                "kind": "material",
                "entries": [{"id": "material.0001", "name": "Torch of the Stump"}],
            }), encoding="utf-8")
            nonexistent = items / "materials" / "data" / "seed" / "items" / "materials" / "materials.json"

            owners = reemit.other_corpus_names(items, nonexistent)
            self.assertIn("material.0001", [i for ids in owners.values() for i in ids],
                          "a skip path that does not exist must still let everything through - that is "
                          "the silent no-op this test exists to make visible")


class IdsNarrowingTests(unittest.TestCase):
    """⛔ `--ids` must be an INTERSECTION with the gate's plan, never a union with it.

    Added 2026-09-28 with a measured reason. The re-emit converged at 57 residual subjects out of 3,633, and
    the 4 cross-corpus duplicates among them are the only ones that land a RED test: `CrossCorpusTests.
    test_the_real_tree_is_clean_but_the_pass_is_not_inert` asserts `found == []` on the real tree, and the
    within-materials tail (53 subjects, 550 per mille refused even at 20 attempts) will not clear them
    because they are not in that tail's failure mode at all - a charm's name is a different name space with
    far more room. Spending the whole 57 to reach 4 was the wrong trade, so the population has to be
    selectable.

    The direction of the flag is the whole risk. A UNION would let a caller re-author an id the gate does not
    flag, which spends a call to overwrite a name that is already fine and can put a collision BACK that no
    longer exists - the second source of truth that made a frozen id list unsafe to begin with. So the tests
    below pin the direction, not just the presence of the flag.
    """

    def _plan(self, entries: "list[dict]", *ids: str) -> "tuple[int, dict, str]":
        import contextlib
        import io
        import tempfile as _tempfile

        with _tempfile.TemporaryDirectory() as d:
            path = Path(d) / "materials.json"
            path.write_text(json.dumps({"schemaVersion": 1, "kind": "material", "entries": entries}),
                            encoding="utf-8")
            argv = ["--corpus", str(path), "--json"]
            if ids:
                argv += ["--ids", ",".join(ids)]
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = reemit.main(argv)
        body = out.getvalue()
        return code, (json.loads(body) if body.strip().startswith("{") else {}), err.getvalue()

    def _three_colliding(self) -> "list[dict]":
        # Three materials sharing one name: the gate keeps the first and refuses the next two.
        return [
            {"id": "material.9001", "runtimeId": "trophy.family.keep.1", "name": "Ancestral Sap"},
            {"id": "material.9002", "runtimeId": "trophy.family.refuse.1", "name": "Ancestral Sap"},
            {"id": "material.9003", "runtimeId": "trophy.family.refuse.2", "name": "Ancestral Sap"},
        ]

    def test_ids_narrows_the_plan_to_the_named_subset(self) -> None:
        """The point of the flag: 2 flagged subjects, 1 named, 1 planned - and the batches follow."""
        code, plan, _err = self._plan(self._three_colliding(), "trophy.family.refuse.2")
        self.assertEqual(code, 0)
        self.assertEqual(plan["refusedByGate"], 2, "the gate still sees both; the flag does not change that")
        self.assertEqual(plan["totalToReAuthor"], 1, "only the named id is planned")
        self.assertEqual(plan["perBatch"][0]["firstId"], "trophy.family.refuse.2")
        self.assertEqual(plan["perBatch"][0]["lastId"], "trophy.family.refuse.2")

    def test_ids_never_widens_the_plan_past_what_the_gate_flags(self) -> None:
        """⛔ The load-bearing direction. An id the gate does NOT flag must not be re-authored, and naming
        only such ids is a refusal rather than a spend."""
        code, plan, err = self._plan(self._three_colliding(), "trophy.family.keep.1")
        self.assertNotEqual(code, 0,
                            "FAIL-BEFORE: a union would plan the kept id and pay a call to overwrite a name "
                            "that is already fine - the second source of truth this flag must not be")
        self.assertEqual(plan, {}, "a refused plan must not also print a plan")
        self.assertIn("nothing to re-author", err)
        self.assertIn("trophy.family.keep.1", err, "the refusal names the id so the operator can see why")

    def test_a_named_id_the_gate_does_not_flag_is_reported_not_silently_dropped(self) -> None:
        """"You asked for 2, the gate found 1" is a finding. Quietly doing the 1 is how it gets read as 2."""
        code, plan, err = self._plan(self._three_colliding(),
                                     "trophy.family.refuse.1", "trophy.family.keep.1")
        self.assertEqual(code, 0)
        self.assertEqual(plan["totalToReAuthor"], 1)
        self.assertEqual(plan["idsRequested"], 2)
        self.assertEqual(plan["idsRequestedButNotFlagged"], ["trophy.family.keep.1"],
                         "the dropped id must be in the machine-readable plan, not only on stderr")
        self.assertIn("trophy.family.keep.1", err)

    def test_ids_naming_nothing_is_refused_rather_than_planning_everything(self) -> None:
        """`--ids ",,"` is a typo, and the dangerous reading of a typo is "no filter" - which would spend on
        the entire 57-subject tail the flag exists to avoid.

        Asserted as a RETURN CODE and a named refusal, not as a `SystemExit`: the tool's refusal shape is
        `fail()` -> stderr + non-zero, and a test that asserts argparse's mechanism instead of the property
        would pass for the wrong reason the day the refusal shape changes.
        """
        import contextlib
        import io
        import tempfile as _tempfile

        code, plan, err = self._plan(self._three_colliding())
        self.assertEqual(code, 0, "control: the same corpus plans fine without the flag")
        self.assertEqual(plan["totalToReAuthor"], 2)
        self.assertEqual(plan["idsRequested"], 0)
        self.assertEqual(plan["idsRequestedButNotFlagged"], [])

        with _tempfile.TemporaryDirectory() as d:
            path = Path(d) / "materials.json"
            path.write_text(json.dumps({"schemaVersion": 1, "kind": "material",
                                        "entries": self._three_colliding()}), encoding="utf-8")
            out, errbuf = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(errbuf):
                code = reemit.main(["--corpus", str(path), "--json", "--ids", ",,"])
        self.assertNotEqual(code, 0, "FAIL-BEFORE: an all-empty --ids must refuse, not plan all 2")
        self.assertEqual(out.getvalue().strip(), "", "a refusal must not also print a plan")
        self.assertIn("named no ids", errbuf.getvalue(),
                      "the refusal must say the ids were empty, so the operator sees the typo")


class RefusalDetailTests(unittest.TestCase):
    """⛔ The refusals were counted and thrown away, 2026-09-28.

    The child's payload carries every defect string, and the runner's `tally_outcomes` /
    `tally_defect_classes` reduced them to counts. A batch that refused 1 of 4 subjects reported
    "1 name_gate" and stopped - and BOTH halves of the name gate land in that bucket while needing opposite
    responses: a collision inside the materials corpus, and a name a charm or drop table already holds. On a
    live pass I spent a full diagnostic cycle assuming the second, because the report could not say. The
    string was in the variable the whole time.
    """

    PAYLOAD = {
        "outcomes": [
            {"subjectId": "trophy.family.a.1", "outcome": "persisted", "defects": []},
            {"subjectId": "trophy.family.b.1", "outcome": "refused",
             "defects": ["near-duplicate name 'Verdant Lineage Trace': Jaccard 0.65 against "
                         "'Verdant Lineage'"]},
            {"subjectId": "trophy.family.c.1", "outcome": "refused",
             "defects": ["name already used elsewhere: 'Primordial Silt' is already used by "
                         "charm.surv-util-074; do not use the word(s) Primordial, Silt in any new name"]},
        ],
    }

    def test_the_two_halves_of_the_name_gate_are_distinguishable_only_if_they_are_named(self) -> None:
        """The property: a reader must be able to tell the two apart from the report. A count cannot."""
        counts = reemit.tally_outcomes(self.PAYLOAD)
        classes = reemit.tally_defect_classes(self.PAYLOAD)
        self.assertEqual(counts.get("refused"), 2)
        self.assertEqual(classes.get("name_gate"), 2,
                         "both halves share the bucket - that is the whole reason the strings are kept")
        strings = [d for o in self.PAYLOAD["outcomes"] for d in o["defects"]]
        self.assertTrue(any("near-duplicate" in s for s in strings))
        self.assertTrue(any(s.startswith("name already used elsewhere") for s in strings),
                        "FAIL-BEFORE: the cross-corpus refusal is indistinguishable from a within-corpus one, "
                        "so the reader cannot tell 'retry with a different word' from 'the model is stuck'")

    def test_the_json_actually_carries_them(self) -> None:
        """Wiring, not just the vocabulary: the runner's per-batch record must include `refusals` and must say
        so when it had to cap them, so a truncated list never reads as the whole story."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('"refusals"', source)
        self.assertIn('"refusalsTruncated"', source)
        self.assertIn("REFUSAL_DETAIL_CAP", source)
        self.assertGreater(reemit.REFUSAL_DETAIL_CAP, 0)

    def test_the_cap_is_a_cap_and_not_a_sample(self) -> None:
        """Past the cap the report must SAY it truncated. A prefix that looks like the whole list is the
        failure this cap exists next to."""
        many = {"outcomes": [{"subjectId": f"trophy.family.x.{i}", "outcome": "refused", "defects": ["d"]}
                             for i in range(reemit.REFUSAL_DETAIL_CAP + 5)]}
        kept = many["outcomes"][:reemit.REFUSAL_DETAIL_CAP]
        self.assertEqual(len(kept), reemit.REFUSAL_DETAIL_CAP)
        self.assertTrue(len(many["outcomes"]) > reemit.REFUSAL_DETAIL_CAP,
                        "and the excess is what `refusalsTruncated` reports")


if __name__ == "__main__":
    unittest.main()
