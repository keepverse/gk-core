"""combat-ai `core-scorer` (module 1, CAI1.7, spec-profile-schema.md Tunables): the CAI1.7 test file
named by the plan/todo for `publish.py --remove-key`.

**Known collision, resolved per the manager's brief:** `--remove-key` (plus `--remove-edge`,
`--remove-entry`) was already built by the `solid-enforcement` lane before this task started (see
`gk-core/tools/tuning/publish.py`'s `remove_key` function and `gk-core/tools/tuning/test_publish_add_key.py`'s own
`RemoveKeyTests`/`RemoveMainPublishesNextVersionTests`). This task does NOT re-implement it -- it adds
the coverage the plan named that `test_publish_add_key.py` does not already carry (the batch property:
N removals in one invocation produce exactly one new version file, never N hops), verified against the
REAL shipped syntax.

**Named gap, not silently accepted as met:** the shipped `remove_key`/`main` take `container:leaf`
(colon-separated, to allow a dotted leaf name), not the plain-dotted `ai.weightHitChance` example
`spec-profile-schema.md`'s own Commands section shows -- `CAI1.8`/`CAI3.1` must use the colon form.
More importantly, the tool has **no required `--reason` flag recorded per removed key in `_meta`** --
only an optional, invocation-wide `--label` (`_meta.rebalanceLabel`). `spec-profile-schema.md`'s own
acceptance line ("it requires `--reason \"<text>\"`, recorded in `_meta` beside each removed path")
does not hold against the shipped tool. This is recorded honestly (see
`test_reason_is_not_required_known_gap_vs_spec` below) rather than assumed satisfied; `gk-core/tools/tuning/
publish.py` is a contested file (also claimed by the concurrently-active solid-enforcement lane per
this session's own session-boundary-check.ps1 crossing note), so this task does not extend it further
-- the gap is named for the manager to rule on (accept `--label` as the de facto reason, or assign the
addition to publish.py's current owner) rather than risking a merge collision on a file this task does
not otherwise need to touch.
"""
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import publish  # noqa: E402


class RemoveKeyBatchPropertyTests(unittest.TestCase):
    """The correction that matters (spec-profile-schema.md Tunables): ten removals are ONE publish,
    never ten hops through v2..v11. `--remove-key` is `action="append"`, so N invocations of the flag
    in ONE `publish.py` call must produce exactly one new version file."""

    def test_three_removals_in_one_invocation_produce_exactly_one_new_version(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "widget.v1.json"), "w", encoding="utf-8") as fh:
                json.dump({
                    "version": 1,
                    "ai": {
                        "weightHitChance": 70, "weightObjective": 50, "weightKill": 15,
                        "objectiveReferenceDistanceCells": 20, "threatRadiusCells": 4,
                    },
                }, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            # No --reason flag exists on the shipped CLI (see this module's own docstring) -- --label
            # is the closest available invocation-wide note.
            sys.argv = [
                "publish.py", "widget", "--label", "combat-ai H7 migration (test fixture)",
                "--remove-key", "ai:weightHitChance",
                "--remove-key", "ai:weightObjective",
                "--remove-key", "ai:weightKill",
            ]
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR = original_dir
                sys.argv = original_argv

            files = sorted(f for f in os.listdir(tmp) if f.startswith("widget."))
            # Exactly one new version file -- v1 (untouched) and v2 (the batch), never v2..v4.
            self.assertEqual(files, ["widget.v1.json", "widget.v2.json"])

            out = json.loads(io.open(os.path.join(tmp, "widget.v2.json"), encoding="utf-8").read())
            self.assertEqual(out["version"], 2)
            for removed in ("weightHitChance", "weightObjective", "weightKill"):
                self.assertNotIn(removed, out["ai"])
            # The two geometry keys are untouched -- this task never decides what a caller removes.
            self.assertIn("objectiveReferenceDistanceCells", out["ai"])
            self.assertIn("threatRadiusCells", out["ai"])

            v1 = json.loads(io.open(os.path.join(tmp, "widget.v1.json"), encoding="utf-8").read())
            self.assertIn("weightHitChance", v1["ai"])  # v1 stays on disk, untouched

    def test_refuses_an_unresolvable_path_rather_than_a_silent_no_op(self):
        with self.assertRaises(KeyError):
            publish.remove_key({"version": 1, "ai": {}}, "ai:doesNotExist")

    def test_publish_remove_key_refuses_an_absent_key(self):
        # strain-splice-host SSH5.7: the acceptance name for the refusal discipline `--add-key` and
        # `--rename-key` already share. Both layers refuse: the helper raises, and `main` exits 1
        # WITHOUT publishing a next version (a removal that removed nothing is not a change).
        with self.assertRaises(KeyError):
            publish.remove_key({"version": 1, "ai": {"present": 1}}, "ai:absent")

        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "widget.v1.json"), "w", encoding="utf-8") as fh:
                json.dump({"version": 1, "ai": {"present": 1}}, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "widget", "--remove-key", "ai:absent"]
            try:
                self.assertEqual(publish.main(), 1)
            finally:
                publish.TUNING_DIR = original_dir
                sys.argv = original_argv

            # A refusal publishes nothing: v1 stays, no v2 appears.
            self.assertEqual(sorted(os.listdir(tmp)), ["widget.v1.json"])


class RemoveKeyRequiresReasonGapTests(unittest.TestCase):
    """Documents, rather than silently accepts, the acceptance line the shipped tool does not meet."""

    def test_reason_is_not_required_known_gap_vs_spec(self):
        # spec-profile-schema.md's Tunables section: "it requires --reason '<text>'". The shipped CLI
        # has no such flag or requirement -- a --remove-key invocation with no --reason and no --label
        # succeeds today. This test pins that as the CURRENT, honestly-gapped behaviour so a future
        # fix (by whichever lane owns publish.py) is a deliberate, reviewed change to this test, not a
        # silent regression discovered later. Behavioural check: a bare removal (no --reason, no
        # --label at all) is accepted by main() today.
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "widget.v1.json"), "w", encoding="utf-8") as fh:
                json.dump({"version": 1, "ai": {"weightHitChance": 70}}, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "widget", "--remove-key", "ai:weightHitChance"]
            try:
                exit_code = publish.main()
            finally:
                publish.TUNING_DIR = original_dir
                sys.argv = original_argv

            # Known gap: this SHOULD be refused (no --reason) per spec-profile-schema.md, and today it
            # is accepted (exit 0). Recorded, not silently worked around.
            self.assertEqual(exit_code, 0, "documents the known gap: --remove-key with no --reason "
                                            "is accepted today; a fix would flip this assertion")


if __name__ == "__main__":
    unittest.main()
