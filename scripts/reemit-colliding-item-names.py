#!/usr/bin/env python3
"""Derive the item names whose regeneration is owed, and run that regeneration in batches that FIT.

**Why this exists.** The BCU2.11 trophy corpus carries 731 distinct reused names involving 2,588 of its
3,633 entries, so 1,857 subjects must be re-authored against a fixed brief before any of it can land. That
figure is not a hand-kept list: it is derived, every time, by replaying the **shipped** gate
(`materialgen.run._name_collision_defects`) over the corpus in order. A frozen id list would be a second
source of truth that drifts from the gate it is supposed to agree with; this has one.

**Why batching is not optional.** `items generate --kind material --overwrite` takes ONE comma-separated
id string. The 1,857 ids serialise to **48,805 characters** against Windows' **32,767**-character
`CreateProcess` limit, so a single invocation dies at the shell with an error that reads like a tool
failure rather than an argument-length problem. This splits them, and **measures each batch's real argv
length** instead of estimating it.

**And why the split is safe, which is the part that had to be proven rather than argued:** a name
published by batch N must block batch N+1, or splitting the run would silently reintroduce the exact
defect the gate exists to stop. It does, because `run_batch` re-reads the corpus at the start of every
invocation — a property of the write path, pinned by `CrossBatchCollisionTests` in
`gk-forge/tools/seedsmith/tests/test_materials_gen.py`.

**DRY RUN BY DEFAULT.** `--execute` is required to spend anything, and it requires an endpoint, because a
regeneration run that cannot reach a model fails after the first batch has already been paid for.

Usage:
    python gk-core/scripts/reemit-colliding-item-names.py                      # plan only, no calls
    python gk-core/scripts/reemit-colliding-item-names.py --json               # machine-readable plan
    python gk-core/scripts/reemit-colliding-item-names.py --execute \\
        --endpoint http://localhost:1234/v1/chat/completions           # actually spend

Exit codes name the stage that failed: 2 precondition, 3 plan, 4 batch N, 5 partial completion.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import subprocess
import sys
import time

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]

# TWO ROOTS, BECAUSE TWO REPOSITORIES. Before the split every workspace path hung off one root; the
# corpus is gk-data's pack, seedsmith is gk-forge's, and this file is gk-core's. The seedsmith path and
# the items root below both named directories that exist in neither gk-core nor anywhere this module
# could reach - and the tool's own comment at the cross_corpus_collisions call already said so: it
# names `gk-data/packs/fusion/data/seed/items/materials/materials.json` as what DEFAULT_CORPUS_IN_REF
# IS, while the constant beside it still held the pre-split spelling. That is the fifth time in this
# program that the documentation was right and the code beside it was stale.
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent / "lib"))
from keepverse_roots import content_root, forge_root, workspace_root  # noqa: E402  (above first)

FORGE_ROOT = forge_root(REPO_ROOT)
SEEDSMITH = FORGE_ROOT / "tools" / "seedsmith"
# `content_root()` is the PACK root, so the seed tree sits below it. Measured rather than assumed,
# because guessing this wrong is a mistake this program has already made once with ladders.py:
# <pack>/data/seed/items is a directory and <pack>/items is not.
ITEMS_ROOT = content_root(REPO_ROOT) / "data" / "seed" / "items"
# The ref path is workspace-relative, so it is joined here and nowhere else.
WORKSPACE_ROOT = workspace_root(REPO_ROOT)
DEFAULT_REF = "rescue/corpus-bcu211-itemseedgen-run"
# TWO JOBS, TWO BASES, AND THE SPLIT MADE THEM DIFFERENT. This constant is the path INSIDE a git ref,
# which is workspace-relative - which is exactly what this file's own comment further down says when it
# names `gk-data/packs/fusion/data/seed/items/materials/materials.json`. It was ALSO being joined onto
# REPO_ROOT for the filesystem, which after the split produces
# `gk-core/data/seed/items/materials/materials.json` - the failure this run surfaced. So it is now the
# ref path and nothing else, and the live corpus is ITEMS_ROOT.
DEFAULT_CORPUS_IN_REF = "gk-data/packs/fusion/data/seed/items/materials/materials.json"
#: Where `materialgen` actually WRITES: its CLI has no `--out-dir`, so this is the target no matter what
#: `--ref`/`--corpus` was read from. A module constant rather than an inline join, so the precondition that
#: compares the plan against it can be pointed at a fixture instead of the real corpus.
LIVE_MATERIALS_PATH = "data/seed/items/materials/materials.json"   # relative to ITEMS_ROOT

#: Windows `CreateProcess` caps a command line at 32,767 characters, and the id list is one argument
#: inside it. The default leaves headroom for the interpreter, the module path and the other flags.
WINDOWS_CMDLINE_LIMIT = 32_767
DEFAULT_MAX_BATCH_CHARS = 30_000

#: Every external call gets a hard timeout. `SEEDSMITH_LLM_TIMEOUT=420` per request, `ATTEMPTS=2`,
#: `MAX_HEAL=3`, so a batch can legitimately take a long time; this bounds the whole batch, not the plan.
DEFAULT_BATCH_TIMEOUT = 3_600
DEFAULT_GIT_TIMEOUT = 900

#: Refusal budget for a pass, in per mille of subjects seen. `materialgen` exits 0 unless a model CALL
#: failed, so a pass can "succeed" while the name gate refuses most of it - and every refused row keeps its
#: old name, so the gap never closes. Measured pre-fix, ~71% of entries held a name another entry held, so
#: a run that still refuses more than a fifth of its subjects is not converging and should stop and be read.
#: 200 per mille = 20%. Override with 0 to disable.
DEFAULT_MAX_REFUSAL_RATE_PERMILLE = 200

#: How many NAMED refusals one batch's JSON carries. The names are what make a refusal actionable - the
#: count never was - so this is a size guard on the report, not a sample: past the cap the batch says
#: `refusalsTruncated` rather than quietly showing a prefix that reads as the whole story.
REFUSAL_DETAIL_CAP = 40


def fail(stage: str, code: int, message: str) -> "int":
    """One refusal shape: a named stage, a non-zero exit, and the reason on stderr."""
    print(f"reemit-colliding-item-names: REFUSED [{stage}] {message}", file=sys.stderr)
    return code


def git_blob(ref: str, path: str, timeout: int) -> "bytes | None":
    """Read a blob in BINARY mode. `text=True` translates newlines in both directions on Windows, which
    this repo has already been bitten by twice producing wrong numbers."""
    try:
        r = subprocess.run(["git", "cat-file", "blob", f"{ref}:{path}"], cwd=REPO_ROOT,
                           capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return None
    return None if r.returncode != 0 else r.stdout


def colliding_ids(corpus_bytes: bytes, run_mod) -> "tuple[list[str], list[dict], dict]":
    """Replay the shipped gate over the corpus in order, returning the ids it refuses AND the entries it kept.

    Kept honest to `run_batch`: an entry joins the kept set only once it survives, so the first member of a
    shared name is kept and every later one is refused. That is why the refusal count is
    `entries_involved - shared_names`, and why this must not be re-implemented here.

    **Both halves of the gate, or the plan and the gate disagree.** `run_batch` refuses on
    `_name_collision_defects` (exact / key reuse) *and* on `_near_duplicate_defect` (lexical, Jaccard at
    0.6). Deriving from the first alone would plan FEWER ids than the gate refuses, and every id the gate
    refuses but the plan omits keeps its colliding name forever - the exact silent gap this runner exists to
    close. So the lexical half is applied here too, in the same order and with the same self-exemption.

    The kept entries come back because the cross-corpus pass needs them: an entry the gate KEEPS can still
    be a duplicate, if the name it kept is held by a charm or a set rather than by another material.
    """
    doc = json.loads(corpus_bytes.decode("utf-8"))
    entries = doc.get("entries") or []
    kept: "list[dict]" = []
    by_runtime: "dict[str, dict]" = {}
    claimed: "dict[str, str]" = {}
    needs: "list[str]" = []
    by_key = by_near = 0
    for e in entries:
        rid = e.get("runtimeId") or ""
        if run_mod._name_collision_defects({"name": e.get("name")}, rid, kept, by_runtime, claimed):
            by_key += 1
            needs.append(rid)
            continue
        if run_mod._near_duplicate_defect({"name": e.get("name")}, rid, kept):
            by_near += 1
            needs.append(rid)
            continue
        kept.append(e)
        by_runtime[rid] = e
        key = run_mod._name_key(e.get("name"))
        if key:
            claimed[key] = rid
    return needs, kept, {"entries": len(entries), "kept": len(kept), "refused": len(needs),
                         "refusedByKey": by_key, "refusedByNearDuplicate": by_near}


def other_corpus_names(items_dir: "pathlib.Path", skip: "pathlib.Path") -> "dict[str, list[str]]":
    """`_name_key` -> the ids OUTSIDE the materials corpus that already hold that name.

    Measured, and this is not hypothetical: after the within-materials gate is applied, `seedsmith check`
    still reports **6** `SemanticDedup/NearDuplicate` GAPs, every one a `material.*` entry whose name is
    held verbatim by a `charm.*` or a `set.*`. `materialgen` cannot see them -- `_sibling_count` returns a
    COUNT and its brief says outright that the model cannot see sibling names, and `run_batch` loads only
    `materials_path` -- so a within-materials gate is structurally blind to this population. Leaving them
    out is what makes the spend land on 10 instead of the objective's target of 4.

    ⛔ **Now a delegation, and that is the fix, not a refactor.** This used to be a private copy of the walk
    while `materialgen` had no cross-corpus awareness at all, so the two sides were each correct about their
    own question and the 4 surviving collisions looked like a model limit from either side. The generator now
    runs the SAME check, so the population here and the population the gate enforces cannot drift - and
    `_name_key` moved with it, because an owner map keyed by a different normalisation than the gate's finds
    nothing, which is indistinguishable from a clean tree.
    """
    from seedsmith.pipeline import cross_corpus_names as _shared

    return _shared.other_corpus_names(items_dir, skip)


def cross_corpus_collisions(entries: "list[dict]", owners: "dict[str, list[str]]",
                            run_mod) -> "list[tuple[str, str, list[str]]]":
    """Materials whose name is already held outside the materials corpus.

    Returned as `(runtime_id, name, other_owners)` so the refusal message can name the actual owner, which
    is the difference between an actionable refusal and a bare count.
    """
    found: "list[tuple[str, str, list[str]]]" = []
    for e in entries:
        key = run_mod._name_key(e.get("name"))
        if not key:
            continue
        holders = [h for h in owners.get(key, []) if h != e.get("id")]
        if holders:
            found.append((e.get("runtimeId") or "", e.get("name") or "", holders))
    return found


def batch_ids(ids: "list[str]", max_chars: int, prefix_len: int) -> "list[list[str]]":
    """Split so each rendered `--overwrite` argument fits, measuring the real length per batch."""
    batches: "list[list[str]]" = []
    current: "list[str]" = []
    length = 0
    for rid in ids:
        piece = len(rid) + 1  # the id plus its separating comma
        if current and length + piece + prefix_len > max_chars:
            batches.append(current)
            current, length = [], 0
        current.append(rid)
        length += piece
    if current:
        batches.append(current)
    return batches


def tally_outcomes(payload: dict) -> "dict[str, int]":
    """Count what a `materialgen` run ACTUALLY persisted, from the result object it printed.

    `materialgen` exits 0 unless a model CALL failed, so a pass in which the name gate refused most
    subjects still exits 0. Reporting "completed N ids" on that basis is the same lie as reporting success:
    N counts ids ATTEMPTED, not ids written. The refusal is invisible from the exit code alone, and it is
    the single number that decides whether the brief fix worked, so it is read out of the child's own
    `outcomes` rather than inferred from anything else.

    `call_failure` is counted alongside the gate outcomes because a call that raised is an id with no answer
    at all, which the child records as `missing_answer` on the next pass - so both are "not persisted" for
    the purpose of the budget guard.
    """
    counts: "dict[str, int]" = {}
    for outcome in payload.get("outcomes") or []:
        key = str(outcome.get("outcome") or "unknown")
        counts[key] = counts.get(key, 0) + 1
    for _entry in payload.get("callFailures") or []:
        counts["call_failure"] = counts.get("call_failure", 0) + 1
    return counts


def classify_defect(defect: str) -> str:
    """Which GATE refused this subject, from the defect string the child emitted.

    ⛔ Why the pilot needs this. `materialgen` has one outcome name for every refusal - `"refused"` -
    so a subject refused because the model returned a name that collides with an existing one and a
    subject refused because the answer was malformed or broke a tag axis both land in the same bucket.
    They are not the same finding and they do not call for the same next step:

      * `name_gate` - the model DID answer, and the answer is unusable because it is not distinct. This
        is the variety question, and it is the one that decides whether a 2,176-subject pass is worth
        spending. Measured on the pre-fix corpus: of the model's own 1,742 distinct names, the gate
        keeps 1,428 and refuses 314 as near-variants of each other, so its demonstrated supply of
        mutually-acceptable names is BELOW the plan's subject count.
      * `shape` - the answer never satisfied the schema or the tag axes. Nothing about name variety is
        implied, and re-running more subjects would just produce more of the same.

    Without the split, the refusal budget fires (exit 7) on both and the operator cannot tell which
    regime they are in - which is the single thing the pilot exists to establish.

    Matched on the child's OWN message prefixes, read from `materialgen/run.py`:
    `_name_collision_defects` emits `"duplicate name ..."`, `_near_duplicate_defect` emits
    `"near-duplicate name ..."`, `answers.schema_defects` emits `"$..."`-rooted paths, and
    `tag_axis_violations` emits `"tags {a!r} and {b!r} are both on the {axis!r} axis ..."`.
    An unrecognised string is reported as `other` rather than silently folded into a bucket, so a new
    defect shape shows up as itself instead of disappearing into `shape`.

    ⛔ The cross-corpus prefix was filed as `other` when it was introduced, 2026-09-28, and that was wrong in
    the direction that matters. `other` reads as neither variety nor shape, so the regime sentence below
    described a name-variety problem as something "no amount of spending will fix" - while the fix was a
    name, and a cheap one. The prefix is read from the generator's own constant rather than copied as a
    literal, because a classifier that has to be told about a new defect shape is a classifier that will be
    a version behind: this way adding the prefix to `materialgen` moves this file too, or the import fails
    loudly instead of quietly re-filing a name refusal as `other`.
    """
    text = defect.strip()
    try:
        from seedsmith.adapters.items.materialgen.run import CROSS_CORPUS_DEFECT_PREFIX  # noqa: PLC0415
    except Exception:  # noqa: BLE001 - a missing generator must not make this classifier lie
        CROSS_CORPUS_DEFECT_PREFIX = "name already used elsewhere: "  # noqa: N806
    if (text.startswith("duplicate name ") or text.startswith("near-duplicate name ")
            or text.startswith(CROSS_CORPUS_DEFECT_PREFIX)):
        return "name_gate"
    if text.startswith("$"):
        return "shape"
    if text.startswith("tags ") and " axis" in text:
        return "shape"
    return "other"


def tally_defect_classes(payload: dict) -> "dict[str, int]":
    """`refused` outcomes broken down by which gate refused them. See `classify_defect`.

    Deliberately a SEPARATE dict from `tally_outcomes`. `refusal_permille` computes its denominator as
    `sum(totals.values())`, so mixing per-defect counts into that dict would double-count every refused
    subject and quietly halve the reported rate - a wrong number in the one place the budget guard
    reads. The two tallies stay orthogonal: outcomes count subjects, classes count defect strings, and
    a subject with two defects is counted once in the first and twice in the second.
    """
    counts: "dict[str, int]" = {}
    for outcome in payload.get("outcomes") or []:
        if str(outcome.get("outcome") or "") != "refused":
            continue
        defects = outcome.get("defects") or []
        if not defects:
            # A refusal with no defect string is itself a finding: the child refused without saying
            # why, so the breakdown cannot be trusted. Recorded rather than counted as some class.
            counts["unnamed"] = counts.get("unnamed", 0) + 1
            continue
        for defect in defects:
            cls = classify_defect(str(defect))
            counts[cls] = counts.get(cls, 0) + 1
    return counts


def _regime_sentence(classes: "dict[str, int]") -> str:
    """One clause naming which regime the refusals are in, for the exit-7 message.

    Separate from `_print_defect_classes` because stderr must carry the reading too: a failure printed
    to a terminal is often all the operator sees, and a bare count would leave them to work out whether
    the fix is a better brief or a smaller subject count.
    """
    if not classes:
        return "no defect classes were recorded, so the breakdown is unavailable"
    parts = [f"{n} {cls}" for cls, n in sorted(classes.items(), key=lambda kv: -kv[1])]
    breakdown = ", ".join(parts)
    gate, shape = classes.get("name_gate", 0), classes.get("shape", 0)
    if gate and not shape:
        reading = ("all of them name collisions - the model answers and the name is not distinct, so "
                   "this is a variety limit, and more spending buys more of the same name")
    elif shape and not gate:
        reading = ("all of them shape defects - the answer broke the schema or a tag axis, which is a "
                   "prompt or pipeline defect that more subjects will not fix")
    elif gate and shape:
        reading = ("a mix of name collisions and shape defects - the shape half is a defect to fix "
                   "before any further spending says anything about variety")
    else:
        reading = "no gate was named, so read the run ledger before spending more"
    return f"{breakdown}. {reading}"


def _print_defect_classes(classes: "dict[str, int]") -> None:
    """Print the refusal breakdown AND the reading it implies, because counts alone are not a decision.

    This is the one place the pilot's whole reason for existing is cashed out. `name_gate` refusals
    mean the model answered and the answer was not distinct - the variety question, and the one that
    says whether a multi-thousand-subject pass is worth spending. `shape` refusals mean the answer never
    satisfied the schema or the tag axes, which says nothing about variety and means more subjects would
    produce more of the same. An `unnamed` refusal means the child refused without naming a gate, so the
    breakdown cannot be trusted and the honest response is to read the ledger, not to spend.
    """
    if not classes:
        return
    print("   refused by gate:")
    for cls, n in sorted(classes.items(), key=lambda kv: -kv[1]):
        print(f"      {n:>5}  {cls}")
    gate = classes.get("name_gate", 0)
    shape = classes.get("shape", 0)
    if classes.get("unnamed"):
        print("      ⛔ some refusals named no gate, so this breakdown is incomplete - read the run "
              "ledger before spending more.")
    if gate and not shape:
        print("      → every refusal is a NAME collision: the model answers, the name is just not "
              "distinct.")
        print("        This is the variety signal. If it is high, more spending buys more of the same "
              "name, and the")
        print("        subject count has to come down rather than the budget going up.")
    elif shape and not gate:
        print("      → every refusal is a SHAPE defect: the answer broke the schema or a tag axis. "
              "That is a prompt or")
        print("        pipeline problem, not a variety problem, and re-running subjects will not fix it.")
    elif gate and shape:
        print(f"      → mixed: {gate} name collision(s) and {shape} shape defect(s). Both need reading; "
              "the shape half is a")
        print("        defect to fix before any further spending says anything about variety.")


def refusal_permille(totals: "dict[str, int]") -> int:
    """Per mille of subjects seen that did NOT persist. Empty input is 0, not a division by a guess."""
    seen = sum(totals.values())
    if not seen:
        return 0
    refused = totals.get("refused", 0) + totals.get("blocked", 0) + totals.get("missing_answer", 0)
    return (refused * 1000) // seen


def argv_for(ids: "list[str]", endpoint: str, model: str,
             max_name_attempts: "int | None" = None) -> "list[str]":
    """The child invocation for one batch of ids.

    `--max-name-attempts` is forwarded when given, and this is not a convenience. It is the ONLY knob that
    moves the yield, because every refusal this tool exists to resolve is a NAME collision: the child asks
    a subject once by default, and a collision keeps the old colliding name and leaves the gap open.
    Measured on this corpus, live: 0 attempts -> 56% persisted, 3 attempts -> 87.5%, and once ~500 of the
    2,176 subjects are re-emitted the remaining tail drops to 65% and the refusal budget correctly trips.

    Before this, the flag was NOT forwarded, so the runner always spent at the child's default of 3 and
    there was no way to buy a different yield from this tool - the same shape as the `--overwrite` defect
    where a knob existed and was silently dropped. `None` means "do not pass it", so the child's own
    default still applies and this stays backward compatible with the recorded plans and tests.
    """
    argv = [sys.executable, "-m", "seedsmith", "items", "generate", "--kind", "material",
            "--overwrite", ",".join(ids), "--write", "--endpoint", endpoint, "--model", model]
    if max_name_attempts is not None:
        argv += ["--max-name-attempts", str(int(max_name_attempts))]
    return argv


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--ref", default=DEFAULT_REF,
                    help=f"ref to read the corpus from for the collision scan (default {DEFAULT_REF})")
    ap.add_argument("--corpus", default="",
                    help="read the corpus from this working-tree path instead of a ref")
    ap.add_argument("--execute", action="store_true",
                    help="ACTUALLY spend model calls; without this the command only plans")
    ap.add_argument("--endpoint", default=os.environ.get("SEEDSMITH_LLM_ENDPOINT", ""),
                    help="model endpoint; required with --execute")
    ap.add_argument("--model", default=os.environ.get("SEEDSMITH_LLM_MODEL", ""),
                    help="model id; empty uses seedsmith's own configuration")
    ap.add_argument("--max-batch-chars", type=int, default=DEFAULT_MAX_BATCH_CHARS,
                    help=f"per-batch argv budget (default {DEFAULT_MAX_BATCH_CHARS}, "
                         f"Windows limit {WINDOWS_CMDLINE_LIMIT})")
    ap.add_argument("--batch-timeout", type=int, default=DEFAULT_BATCH_TIMEOUT,
                    help=f"hard seconds per batch (default {DEFAULT_BATCH_TIMEOUT})")
    ap.add_argument("--max-refusal-rate-permille", type=int, default=DEFAULT_MAX_REFUSAL_RATE_PERMILLE,
                    help="stop the pass once refused+blocked+missing exceeds this share of the subjects "
                         "seen so far, in per mille (0 = never stop). The budget guard for the whole run: "
                         "a non-zero rate means the re-authored names are not landing, and the unpersisted "
                         "rows keep their old names, so more spending is not converging.")
    ap.add_argument("--max-name-attempts", type=int, default=None,
                    help="forwarded to the child: how many times a subject whose NAME collided is re-asked, "
                         "each time with the collision named. Omitted by default so the child's own default "
                         "(3) applies. This is the only knob that moves the yield, and the refusal budget "
                         "is what tells you whether a higher value is worth the calls: on this corpus 3 "
                         "attempts held 87.5% early and fell to 65% on the tail after ~500 subjects.")
    ap.add_argument("--stop-after", type=int, default=0,
                    help="stop cleanly after this many batches and report (0 = run them all). This is the "
                         "PILOT control: the refusal budget is only evaluated BETWEEN batches, so with the "
                         "default 30,000-char budget a pass spends ~1,081 calls before the guard can "
                         "possibly fire. Pair it with a small --max-batch-chars to buy a real refusal-rate "
                         "reading for a few dozen calls instead.")
    ap.add_argument("--json", action="store_true", help="machine-readable result on stdout")
    ap.add_argument("--ids", default="",
                    help="comma-separated runtime ids to NARROW the pass to. Intersected with what the gate "
                         "derives from the live corpus, never unioned: an id the gate does not flag is not "
                         "re-authored, and any named id the gate does not flag is reported on stderr. Use it "
                         "for a small population the within-materials gate is blind to (a handful of "
                         "cross-corpus duplicates), where spending the whole tail is the wrong trade.")
    args = ap.parse_args(argv)

    if args.max_batch_chars > WINDOWS_CMDLINE_LIMIT:
        return fail("precondition", 2,
                    f"--max-batch-chars {args.max_batch_chars} exceeds the Windows command-line limit "
                    f"{WINDOWS_CMDLINE_LIMIT}; a batch would die at the shell, not at a gate")
    if args.execute and not args.endpoint:
        return fail("precondition", 2,
                    "--execute with no --endpoint and no SEEDSMITH_LLM_ENDPOINT: a regeneration run "
                    "that cannot reach a model fails after the first batch has already been paid for")

    sys.path.insert(0, str(SEEDSMITH))
    try:
        from seedsmith.adapters.items.materialgen import run as run_mod  # noqa: E402
    except Exception as exc:  # pragma: no cover - import failure is a precondition, not a crash
        return fail("precondition", 2, f"could not import materialgen.run: {exc}")

    if args.corpus:
        p = pathlib.Path(args.corpus)
        if not p.is_file():
            return fail("precondition", 2, f"--corpus {p} does not exist")
        raw = p.read_bytes()
        source = str(p)
    else:
        raw = git_blob(args.ref, DEFAULT_CORPUS_IN_REF, DEFAULT_GIT_TIMEOUT)
        if raw is None:
            return fail("precondition", 2,
                        f"could not read {args.ref}:{DEFAULT_CORPUS_IN_REF} - is the ref present?")
        source = f"{args.ref}:{DEFAULT_CORPUS_IN_REF}"
    if not raw.strip():
        return fail("precondition", 2, f"{source} is 0 bytes; an empty capture is not a corpus to scan")

    ids, kept, stats = colliding_ids(raw, run_mod)

    # An entry the gate KEEPS can still be a duplicate: if its name is held by a charm or a set rather
    # than by another material, the within-materials gate is structurally blind to it. Measured on the
    # rescue corpus, that is 6 entries, and they are the difference between landing on 10 and the
    # objective's target of 4.
    #
    # Unconditional, and that is the whole point. Only the MATERIALS corpus varies between `--ref` and
    # `--corpus`; the other corpora are always read from the working tree, because that is where charms
    # and sets live either way. Gating this on `not args.corpus` - as an earlier version did - silently
    # dropped the 6 in exactly the mode that needs them: `--corpus` is the RE-RUN path, used after a
    # partial spend to emit only what is still colliding, and a plan missing the cross-corpus population
    # there would under-report and finish the job still holding 6 duplicates.
    cross = cross_corpus_collisions(
        kept,
        # `WORKSPACE_ROOT / DEFAULT_CORPUS_IN_REF`, NOT `ITEMS_ROOT / DEFAULT_CORPUS_IN_REF`.
        #
        # ⛔ Real bug, found 2026-09-28 by cross-checking two of my own measurements: my hand-rolled probe
        # counted 13 cross-corpus collisions and this call reported 181, with holders reading
        # `vs material.2447` - i.e. collisions with MATERIALS, not with a charm or a set.
        #
        # `DEFAULT_CORPUS_IN_REF` is `"gk-data/packs/fusion/data/seed/items/materials/materials.json"` - a REPO-RELATIVE path
        # with slashes in it. Joining it onto the items root built
        # `.../data/seed/items/materials/data/seed/items/materials/materials.json`, which cannot exist, so
        # the skip never matched and `rglob` walked the materials corpus anyway. Every one of the 3,633
        # material names landed in `owners`, so the KEPT half of each within-materials duplicate pair was
        # reported as colliding with its own refused twin.
        #
        # Two consequences, and the second is why it mattered. (1) ~181 subjects were added to the plan
        # that the within-materials gate had already handled, so a live pass was spending real calls on
        # them. (2) The genuine cross-corpus population - 13, every one a `material.*` holding a name a
        # `charm.*` or `set.*` also holds, which is exactly what this function exists to catch - was
        # SWAMPED in 181 false positives, so the tool's own diagnostic understated the very thing it was
        # written to measure.
        other_corpus_names(ITEMS_ROOT,
                           WORKSPACE_ROOT / DEFAULT_CORPUS_IN_REF),
        run_mod)
    already = set(ids)
    ordered = list(ids) + [rid for rid, _, _ in cross if rid not in already]

    # `--ids` NARROWS the plan; it never widens it. A caller-supplied id list is a second source of truth
    # about what is wrong, and a second source of truth is how a subject that the gate has since fixed gets
    # re-authored anyway - paid for twice, and able to reintroduce a collision that no longer exists. So the
    # requested ids are INTERSECTED with what the gate just derived from the live corpus, in the gate's own
    # order, and any requested id the gate did not flag is reported rather than silently dropped: "you asked
    # for 5, the gate found 3" is a finding, and quietly doing the 3 is how it gets read as 5.
    requested: "list[str]" = []
    if args.ids:
        requested = sorted({s.strip() for s in args.ids.split(",") if s.strip()})
        if not requested:
            return fail("precondition", 2, "--ids was given but named no ids after splitting on commas")
        planned = set(ordered)
        ordered = [rid for rid in ordered if rid in set(requested)]
        skipped = sorted(set(requested) - planned)
        if not ordered:
            return fail("precondition", 2,
                        f"--ids named {len(requested)} id(s) and the gate flags NONE of them, so there is "
                        f"nothing to re-author; the gate is the authority, not this list. First named: "
                        f"{', '.join(requested[:5])}")
        if skipped:
            print(f"--ids: {len(skipped)} of {len(requested)} named id(s) are NOT flagged by the gate on the "
                  f"live corpus and were not planned: {', '.join(skipped[:10])}", file=sys.stderr)

    # ⛔ The write target is NOT the source. `materialgen` resolves `MATERIALS_PATH` itself and its CLI has
    # no `--out-dir`, so the ids are written into the WORKING TREE whatever `--ref`/`--corpus` was read
    # from - and `run_batch` APPENDS an id that is not already there (run.py: `entries.append(entry)`).
    # Measured at this HEAD: the live corpus holds 31 rows, and all 1,863 targets are absent from it, so
    # reading the plan from the rescue ref and writing to live would leave 31 + 1,863 = **1,894** rows
    # against a rescue population of **3,633** - short by 1,739, because the 1,776 collision-free rows are
    # never written at all. That is a silent, expensive, wrong-corpus outcome, so it is a named refusal.
    #
    # The remedy is the merge-then-REGENERATE the objective allows: install the rescue corpus first, so
    # every target is present and `run_batch` upserts in place. That is a `git checkout <ref> -- <path>`,
    # a git operation rather than a hand-edit, and the stale intermediate is never committed - the
    # re-emit rewrites the file before the single commit that lands it.
    live_path = ITEMS_ROOT / "materials" / "materials.json"
    live_rows, present, absent = 0, 0, []
    if live_path.is_file():
        try:
            live_entries = json.loads(live_path.read_text(encoding="utf-8")).get("entries") or []
        except json.JSONDecodeError:
            live_entries = []
        live_rows = len(live_entries)
        live_ids = {e.get("runtimeId") for e in live_entries}
        present = sum(1 for rid in ordered if rid in live_ids)
        absent = [rid for rid in ordered if rid not in live_ids]

    if args.execute and absent:
        # An error path must not raise. `relative_to` throws when the live path is not under the repo root,
        # which is exactly the situation a test fixture creates - and a refusal that dies with a traceback
        # is worse than no refusal, because the operator sees a crash instead of the reason.
        try:
            shown = str(live_path.relative_to(ITEMS_ROOT))
        except ValueError:
            shown = str(live_path)
        return fail("precondition", 2,
                    f"{len(absent)} of {len(ordered)} ids to re-author are NOT in the live corpus "
                    f"({shown} holds {live_rows} rows), and materialgen APPENDS "
                    f"an id it does not find - so this would write a {live_rows + len(absent)}-row "
                    f"fragment, not the corpus you mean. Install the corpus first, then re-run against it: "
                    f"  git checkout {args.ref} -- {LIVE_MATERIALS_PATH}\n"
                    f"  {pathlib.Path(sys.argv[0]).name} --corpus {LIVE_MATERIALS_PATH} "
                    f"--execute --endpoint <url>\n"
                    f"That upserts the {len(ordered)} ids IN PLACE and leaves the other rows untouched. "
                    f"The intermediate is never committed - the re-emit rewrites the file before the one "
                    f"commit that lands it.")

    probe = argv_for(ordered[:1] or ["x"], args.endpoint, args.model)
    prefix_len = len(" ".join(probe[:probe.index("--overwrite") + 1]))
    batches = batch_ids(ordered, args.max_batch_chars, prefix_len)

    plan = {
        "execute": bool(args.execute),
        "source": source,
        "corpusEntries": stats["entries"],
        "keptByGate": stats["kept"],
        "refusedByGate": stats["refused"],
        "crossCorpusDuplicates": len(cross),
        "totalToReAuthor": len(ordered),
        "idsRequested": len(requested),
        "idsRequestedButNotFlagged": [r for r in requested if r not in set(ordered)],
        "liveCorpusRows": live_rows,
        "targetsPresentInLive": present,
        "targetsAbsentFromLive": len(absent),
        "batches": len(batches),
        "maxBatchChars": args.max_batch_chars,
        "windowsCmdlineLimit": WINDOWS_CMDLINE_LIMIT,
        "perBatch": [
            {"index": i + 1, "ids": len(b), "argvChars": len(" ".join(argv_for(b, args.endpoint, args.model))),
             "firstId": b[0], "lastId": b[-1]}
            for i, b in enumerate(batches)],
    }
    over = [b for b in plan["perBatch"] if b["argvChars"] > WINDOWS_CMDLINE_LIMIT]
    if over:
        return fail("plan", 3,
                    f"batch(es) {[b['index'] for b in over]} exceed the Windows command-line limit "
                    f"({WINDOWS_CMDLINE_LIMIT} chars); lower --max-batch-chars")

    if not args.execute:
        if args.json:
            print(json.dumps(plan, ensure_ascii=False, indent=2))
        else:
            print(f"source            : {source}")
            print(f"corpus entries    : {stats['entries']}")
            print(f"kept by the gate  : {stats['kept']}")
            print(f"  refused (name already used by another MATERIAL): {stats['refused']}")
            print(f"  refused (name already used by a CHARM or a SET) : {len(cross)}")
            for rid, name, holders in cross:
                print(f"      {rid:<34} {name!r} also held by {holders}")
            print(f"total to re-author: {len(ordered)}")
            print(f"live corpus       : {live_rows} rows; {present} of {len(ordered)} targets present, "
                  f"{len(absent)} absent")
            if absent:
                print("  ⛔ the write target is the WORKING TREE, not the source read above, and")
                print("     materialgen APPENDS an id it does not find - so executing now would write a")
                print(f"     {live_rows + len(absent)}-row fragment. Install the corpus first:")
                print(f"       git checkout {args.ref} -- data/seed/items/materials/materials.json")
                print(f"       {pathlib.Path(sys.argv[0]).name} --corpus "
                      f"data/seed/items/materials/materials.json --execute --endpoint <url>")
            print(f"batches           : {len(batches)}  (max {args.max_batch_chars} chars each, "
                  f"Windows limit {WINDOWS_CMDLINE_LIMIT})")
            for b in plan["perBatch"]:
                print(f"   batch {b['index']:>2}: {b['ids']:>5} ids, argv {b['argvChars']:>6} chars  "
                      f"{b['firstId']} .. {b['lastId']}")
            print("\nDRY RUN - nothing was called and nothing was written.")
            print("Re-run with --execute --endpoint <url> to spend.")
            if batches:
                pilot_ids = len(batches[0])
                print(f"\nThe refusal budget is only checked BETWEEN batches, and batch 1 here is "
                      f"{pilot_ids} ids -")
                print(f"so a full pass spends {pilot_ids} calls before the guard can act. To buy a real")
                print("refusal-rate reading for a few dozen calls first:")
                print(f"  python {pathlib.Path(sys.argv[0]).name} --corpus "
                      f"data/seed/items/materials/materials.json \\")
                print(f"      --max-batch-chars 600 --stop-after 1 --execute --endpoint <url>")
            if cross:
                print("\nNOTE: the gate is materials-local, so a re-authored name for the cross-corpus")
                print("      rows is NOT provably free of the charm or set that holds the old one. The")
                print("      `check` below is what closes that loop - read NearDuplicate, do not assume it.")
        return 0

    # ---- spend ----
    env = dict(os.environ, PYTHONPATH=str(SEEDSMITH))
    results, done_ids = [], 0
    totals: "dict[str, int]" = {}
    # Kept separate from `totals` on purpose: `refusal_permille` uses `sum(totals.values())` as its
    # denominator, so folding per-defect counts in would double-count refusals and halve the rate the
    # budget guard reads. See `tally_defect_classes`.
    classes: "dict[str, int]" = {}

    for i, b in enumerate(batches, start=1):
        cmd = argv_for(b, args.endpoint, args.model, args.max_name_attempts)
        started = time.time()
        try:
            r = subprocess.run(cmd, cwd=REPO_ROOT, env=env, capture_output=True, text=True,
                               errors="replace", timeout=args.batch_timeout)
        except subprocess.TimeoutExpired:
            results.append({"batch": i, "ids": len(b), "exit": None, "seconds": args.batch_timeout,
                            "status": "timeout"})
            return fail(f"batch-{i}", 4,
                        f"batch {i} of {len(batches)} exceeded --batch-timeout "
                        f"{args.batch_timeout}s after {done_ids} ids had already been written")
        elapsed = round(time.time() - started, 1)
        # The child prints a bare JSON object after the run; anything before it is not JSON, so decode from
        # the first brace and refuse if there is no object, rather than treating unparseable output as zero.
        counts, unparsed, batch_classes = {}, False, {}
        # `payload` is bound ONLY on the success path below, so it must be bound to an empty dict here. The
        # refusal-detail read added after it walked `payload` unconditionally, and a child that exits
        # non-zero - the exact case `WriteTargetTests` covers - raised `UnboundLocalError` from inside the
        # failure report. A diagnostic that crashes on the failure it exists to describe is worse than none.
        payload: dict = {}
        start = (r.stdout or "").find("{")
        if r.returncode == 0:
            if start < 0:
                unparsed = True
            else:
                try:
                    payload = json.JSONDecoder().raw_decode(r.stdout[start:])[0]
                    counts = tally_outcomes(payload)
                    batch_classes = tally_defect_classes(payload)
                except json.JSONDecodeError:
                    unparsed = True
        for key, n in counts.items():
            totals[key] = totals.get(key, 0) + n
        for key, n in batch_classes.items():
            classes[key] = classes.get(key, 0) + n
        # ⛔ The refusals, NAMED, 2026-09-28. The payload carries every defect string in `outcomes`, and this
        # loop threw them away for a COUNT. So a batch that refused 1 of 4 subjects reported "1 name_gate" and
        # nothing else - and the two halves of the name gate, a collision inside this corpus and a name another
        # corpus already holds, land in that SAME bucket while needing opposite responses. Guessing which it was
        # cost a full diagnostic cycle on a live pass; the string was in the variable the whole time. A tally
        # that cannot name its members is a tally, not a diagnosis, so the members are carried through - capped,
        # because a 1,000-subject batch that refuses everything must not produce a megabyte of JSON.
        refusals = [{"id": str(o.get("subjectId") or ""), "defects": [str(d) for d in (o.get("defects") or [])]}
                for o in (payload.get("outcomes") or []) if str(o.get("outcome") or "") == "refused"]
        results.append({"batch": i, "ids": len(b), "exit": r.returncode, "seconds": elapsed,
                        "status": "ok" if r.returncode == 0 else "failed",
                        "outcomes": counts, "outcomesUnparsed": unparsed,
                        "defectClasses": batch_classes,
                        "refusals": refusals[:REFUSAL_DETAIL_CAP],
                        "refusalsTruncated": len(refusals) > REFUSAL_DETAIL_CAP,
                        "stderrTail": (r.stderr or "")[-400:] if r.returncode else ""})
        if r.returncode != 0:
            # ⛔ The cause was here and was being thrown away, 2026-09-28. `results[i]["stderrTail"]` has
            # carried the child's last 400 characters of stderr since this loop was written, but only
            # `--json` ever printed it - the human-readable `fail()` said "exited 1" and stopped. A child
            # that dies with a traceback is indistinguishable from one that dies on a transport error, and
            # this exact message cost TWO diagnostic cycles on a live run: batch 29 "exited 1" sent me
            # hunting a schema/endpoint theory while the child's own traceback sat unread in a variable.
            # A failure message that names a code and not a cause is not a diagnosis, and the budget
            # guard's own regime sentence above is only as good as the detail behind it.
            tail = (r.stderr or "").strip()
            if not tail:
                # An empty stderr is itself a finding, and must not read as "no information": say so
                # rather than appending nothing, so the reader knows the child said nothing at all.
                tail = "(the child wrote nothing to stderr)"
            else:
                tail = " | ".join(line for line in tail.splitlines()[-6:] if line.strip())
            if args.json:
                print(json.dumps({**plan, "results": results, "outcomeTotals": totals,
                                  "defectClasses": classes},
                                 ensure_ascii=False, indent=2))
            return fail(f"batch-{i}", 4,
                        f"batch {i} of {len(batches)} exited {r.returncode} after {done_ids} ids were "
                        f"written; the corpus is mid-regeneration and the ledger records what completed. "
                        f"The child's own last words: {tail}")
        if unparsed:
            return fail(f"batch-{i}", 4,
                        f"batch {i} of {len(batches)} exited 0 but printed no parsable result object, so "
                        f"its persisted/refused split cannot be reported. Treating that as success would "
                        f"report {len(b)} ids completed when an unknown number were refused")
        done_ids += len(b)

        # The refusal budget is checked BEFORE the `--stop-after` request, deliberately: asking to stop
        # early must never mask a budget breach behind an exit 0. A pilot that is already over budget is a
        # refusal, and the operator should read it as one.
        refused = totals.get("refused", 0) + totals.get("blocked", 0) + totals.get("missing_answer", 0)
        rate = refusal_permille(totals)
        if args.max_refusal_rate_permille and rate > args.max_refusal_rate_permille:
            if args.json:
                print(json.dumps({**plan, "results": results, "outcomeTotals": totals,
                                  "refusalRatePermille": rate, "defectClasses": classes},
                                 ensure_ascii=False, indent=2))
            return fail(f"batch-{i}", 7,
                        f"STOPPED at batch {i} of {len(batches)}: {refused} of "
                        f"{sum(totals.values())} subjects came back "
                        f"refused/blocked/missing ({rate} per mille), over the "
                        f"--max-refusal-rate-permille budget of {args.max_refusal_rate_permille}. "
                        f"{totals.get('persisted', 0)} persisted so far and the rest keep their old names, "
                        f"so more spending is not converging. Read the refusals before continuing. "
                        f"Broken down by gate: {_regime_sentence(classes)}")

        if args.stop_after and len([x for x in results if x["status"] == "ok"]) >= args.stop_after:
            # A CLEAN stop, not a failure: everything asked for was attempted, and the operator is choosing
            # to look before spending more. Exit 0 with `stoppedAfter` set, because a pilot that "failed"
            # would be misread as a broken tool rather than a deliberate measurement.
            summary = {"attempted": done_ids, "outcomeTotals": totals, "defectClasses": classes,
                       "stoppedAfter": args.stop_after, "batchesRemaining": len(batches) - len(results)}
            if args.json:
                print(json.dumps({**plan, "results": results, **summary}, ensure_ascii=False, indent=2))
            else:
                print(f"\npilot stop: {args.stop_after} of {len(batches)} batches, "
                      f"{done_ids} ids attempted")
                print(f"   PERSISTED {totals.get('persisted', 0)}   refused "
                      f"{totals.get('refused', 0)}   blocked {totals.get('blocked', 0)}   missing "
                      f"{totals.get('missing_answer', 0)}   call failures "
                      f"{totals.get('call_failure', 0)}")
                print(f"   refusal rate: {refusal_permille(totals)} per mille "
                      f"(budget {args.max_refusal_rate_permille})")
                _print_defect_classes(classes)
                print(f"   {len(batches) - len(results)} batch(es) still planned. To continue, re-derive "
                      f"from the LIVE corpus:")
                print("     python scripts/reemit-colliding-item-names.py --corpus "
                      "data/seed/items/materials/materials.json --json")
            return 0

    summary = {"attempted": done_ids, "outcomeTotals": totals, "defectClasses": classes}
    if args.json:
        print(json.dumps({**plan, "results": results, **summary}, ensure_ascii=False, indent=2))
    else:
        print(f"\nattempted {done_ids} ids across {len(batches)} batches")
        print(f"   PERSISTED {totals.get('persisted', 0)}   refused {totals.get('refused', 0)}   "
              f"blocked {totals.get('blocked', 0)}   missing {totals.get('missing_answer', 0)}   "
              f"call failures {totals.get('call_failure', 0)}")
        if totals.get("refused") or totals.get("blocked") or totals.get("missing_answer"):
            print("   NOT persisted rows KEEP their old names, so the gap is still open for them.")
        _print_defect_classes(classes)
        for r in results:
            print(f"   batch {r['batch']:>2}: {r['ids']:>5} ids  exit {r['exit']}  {r['seconds']}s  "
                  f"persisted {r['outcomes'].get('persisted', 0)}  refused {r['outcomes'].get('refused', 0)}")
        print("\nNow run:  python -m seedsmith check data/seed/items --adapter items")
        print("and read the NearDuplicate count against the baseline before committing anything.")
        if totals.get("refused") or totals.get("blocked") or totals.get("missing_answer"):
            print("\nTo continue, re-derive the remainder from the LIVE corpus - NOT from the ref, which")
            print("still holds the pre-pass names and would re-emit everything:")
            print("  python scripts/reemit-colliding-item-names.py --corpus "
                  "data/seed/items/materials/materials.json --json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
