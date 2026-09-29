"""publish.py --add-key: adds one new key, refuses everything else (2026-09-18)."""
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "publish", os.path.join(os.path.dirname(__file__), "publish.py"))
publish = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(publish)


class AddKeyTests(unittest.TestCase):
    def test_adds_a_block_at_the_root(self):
        doc = {"version": 1}
        path, value = publish.add_key(doc, ':cacheDecay={"baseMilli": 994}')
        self.assertEqual(path, "cacheDecay")
        self.assertEqual(doc["cacheDecay"], {"baseMilli": 994})

    def test_adds_a_key_inside_an_existing_container(self):
        doc = {"version": 1, "cacheDecay": {"baseMilli": 994}}
        publish.add_key(doc, "cacheDecay:capMilli=999")
        self.assertEqual(doc["cacheDecay"]["capMilli"], 999)

    def test_refuses_an_existing_key(self):
        doc = {"version": 1, "cacheDecay": {}}
        with self.assertRaises(KeyError):
            publish.add_key(doc, ":cacheDecay={}")

    def test_refuses_a_missing_container(self):
        with self.assertRaises(KeyError):
            publish.add_key({"version": 1}, "nope:leaf=1")

    def test_refuses_invalid_json(self):
        with self.assertRaises(KeyError):
            publish.add_key({"version": 1}, ":leaf={not json")

    def test_refuses_a_malformed_spec(self):
        with self.assertRaises(KeyError):
            publish.add_key({"version": 1}, "leaf=1")


class ActionRungsScopeWindowsTests(unittest.TestCase):
    """ST3 (`spec-scope-window-tunables.md` contract 1, spec test 7): the rung table's `scopeWindows`
    block is PUBLISHED, never hand-written, and its values are OBJECTS -- so a retune is
    `scopeWindows.family.ceiling=6`, a real int, instead of a stringified `[1,6]` array."""

    SPEC = (':scopeWindows={"general":{"floor":1,"ceiling":4},"family":{"floor":1,"ceiling":7},'
            '"species":{"floor":1,"ceiling":10}}')

    def test_add_key_writes_the_objects_and_refuses_a_second_add(self):
        doc = {"version": 2, "cap": 10, "rows": []}
        path, value = publish.add_key(doc, self.SPEC)
        self.assertEqual(path, "scopeWindows")
        self.assertEqual(doc["scopeWindows"]["family"], {"floor": 1, "ceiling": 7})
        with self.assertRaises(KeyError):
            publish.add_key(doc, self.SPEC)

    def test_a_retune_through_set_writes_an_int(self):
        doc = {"version": 2, "scopeWindows": {"family": {"floor": 1, "ceiling": 7}}}
        publish.set_path(doc, "scopeWindows.family.ceiling", publish.parse_value("6"))
        ceiling = doc["scopeWindows"]["family"]["ceiling"]
        self.assertEqual(ceiling, 6)
        self.assertIsInstance(ceiling, int)

    def test_main_publishes_v3_from_a_v2_fixture(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "action-rungs.v2.json"), "w", encoding="utf-8") as fh:
                json.dump({"version": 2, "cap": 10, "rows": []}, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "action-rungs", "--add-key", self.SPEC]
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR, sys.argv = original_dir, original_argv

            with open(os.path.join(tmp, "action-rungs.v3.json"), encoding="utf-8") as fh:
                out = json.load(fh)

        self.assertEqual(out["version"], 3)
        self.assertEqual(out["scopeWindows"]["species"], {"floor": 1, "ceiling": 10})


if __name__ == "__main__":
    unittest.main()


class RemoveEdgeTests(unittest.TestCase):
    """publish.py --remove-edge (solid-enforcement `retire-atk` R3, 2026-09-18): retire-atk needs the
    LIVE file to stop carrying a dead edge, and the safety property is the refusal -- a removal that
    matched zero or several edges would silently change more (or less) than the operator asked for."""

    def doc(self):
        return {
            "version": 8,
            "edges": [
                {"_group": "progression"},
                {"channel": "progression.bonus.maxHp", "source": "Vigor", "kMilli": 12000},
                {"channel": "progression.bonus.atk", "source": "Might", "kMilli": 10000},
                {"channel": "progression.bonus.atk", "source": "Ferocity", "kMilli": 8000},
            ],
        }

    def test_removes_exactly_the_matching_edge(self):
        doc = self.doc()
        ch, src = publish.remove_edge(doc, "channel=progression.bonus.atk,source=Might")
        self.assertEqual((ch, src), ("progression.bonus.atk", "Might"))
        remaining = [e for e in doc["edges"] if isinstance(e, dict) and "channel" in e]
        self.assertEqual(len(remaining), 2)
        self.assertNotIn(("progression.bonus.atk", "Might"), [(e["channel"], e["source"]) for e in remaining])
        # The sibling Ferocity edge is untouched -- a removal is not a channel sweep.
        self.assertIn(("progression.bonus.atk", "Ferocity"), [(e["channel"], e["source"]) for e in remaining])
        # The `_group` divider is not an edge and survives.
        self.assertIn({"_group": "progression"}, doc["edges"])

    def test_refuses_zero_matches(self):
        with self.assertRaises(KeyError):
            publish.remove_edge(self.doc(), "channel=progression.bonus.atk,source=Nobody")

    def test_refuses_an_ambiguous_match(self):
        # Two edges with the same channel AND source would be a real defect in the file, and a removal
        # must say so rather than delete whichever one it found first.
        doc = self.doc()
        doc["edges"].append({"channel": "progression.bonus.atk", "source": "Might", "kMilli": 1})
        with self.assertRaises(KeyError):
            publish.remove_edge(doc, "channel=progression.bonus.atk,source=Might")

    def test_refuses_a_spec_missing_channel_or_source(self):
        with self.assertRaises(KeyError):
            publish.remove_edge(self.doc(), "channel=progression.bonus.atk")
        with self.assertRaises(KeyError):
            publish.remove_edge(self.doc(), "source=Might")

    def test_refuses_an_unknown_field(self):
        with self.assertRaises(KeyError):
            publish.remove_edge(self.doc(), "channel=progression.bonus.atk,source=Might,nope=1")


class RemoveKeyTests(unittest.TestCase):
    """publish.py --remove-key: the removal twin of --add-key, and it has to handle a DOTTED key name
    (familyRead's own keys are channel ids) -- the reason it takes `container:leaf` and not a plain
    dotted path."""

    def test_removes_a_dotted_key(self):
        doc = {"version": 8, "familyRead": {"progression.bonus.atk": "magnitude", "combat.power": "contest"}}
        path, old = publish.remove_key(doc, "familyRead:progression.bonus.atk")
        self.assertEqual(path, "familyRead.progression.bonus.atk")
        self.assertEqual(old, "magnitude")
        self.assertEqual(doc["familyRead"], {"combat.power": "contest"})

    def test_removes_a_root_key(self):
        doc = {"version": 8, "oldBlock": {"a": 1}}
        path, old = publish.remove_key(doc, ":oldBlock")
        self.assertEqual(path, "oldBlock")
        self.assertEqual(old, {"a": 1})
        self.assertNotIn("oldBlock", doc)

    def test_refuses_an_absent_key(self):
        with self.assertRaises(KeyError):
            publish.remove_key({"version": 8, "familyRead": {}}, "familyRead:progression.bonus.atk")

    def test_refuses_a_missing_container(self):
        with self.assertRaises(KeyError):
            publish.remove_key({"version": 8}, "nope:leaf")

    def test_refuses_a_malformed_spec(self):
        with self.assertRaises(KeyError):
            publish.remove_key({"version": 8}, "leaf")


class RemoveMainPublishesNextVersionTests(unittest.TestCase):
    """The ops are useless if `main` does not publish them. Runs the real CLI path against a fixture
    directory, the same way ActionRungsScopeWindowsTests does."""

    def test_main_publishes_v9_dropping_the_retired_edge_and_key(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "aptitudes.v8.json"), "w", encoding="utf-8") as fh:
                json.dump({
                    "version": 8,
                    "familyRead": {"progression.bonus.atk": "magnitude", "combat.power": "magnitude"},
                    "edges": [{"channel": "progression.bonus.atk", "source": "Might", "kMilli": 10000}],
                }, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "aptitudes",
                        "--remove-edge", "channel=progression.bonus.atk,source=Might",
                        "--remove-key", "familyRead:progression.bonus.atk",
                        "--label", "retire atk (owner 2026-09-18)"]
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR = original_dir
                sys.argv = original_argv

            out = json.loads(io.open(os.path.join(tmp, "aptitudes.v9.json"), encoding="utf-8").read())
            self.assertEqual(out["version"], 9)
            self.assertNotIn("progression.bonus.atk", out["familyRead"])
            self.assertEqual(out["edges"], [])
            self.assertEqual(out["_meta"]["rebalanceLabel"], "retire atk (owner 2026-09-18)")
            # v8 stays on disk untouched -- published versions are immutable.
            old = json.loads(io.open(os.path.join(tmp, "aptitudes.v8.json"), encoding="utf-8").read())
            self.assertIn("progression.bonus.atk", old["familyRead"])


class RemoveEntryTests(unittest.TestCase):
    """publish.py --remove-entry (solid-enforcement `retire-atk` R4, 2026-09-18): the catalog's rows
    live in `entries`, not `edges`, and the removal must still name exactly one row."""

    def doc(self):
        return {
            "version": 2,
            "entries": [
                {"family": "progression.bonus.maxHp", "compose": "FlatSum"},
                {"family": "progression.bonus.atk", "compose": "FlatSum"},
            ],
        }

    def test_removes_exactly_the_matching_row(self):
        doc = self.doc()
        path, removed = publish.remove_entry(doc, "entries[family=progression.bonus.atk]")
        self.assertEqual(path, "entries[family=progression.bonus.atk]")
        self.assertEqual(removed["family"], "progression.bonus.atk")
        self.assertEqual([e["family"] for e in doc["entries"]], ["progression.bonus.maxHp"])

    def test_refuses_zero_matches(self):
        with self.assertRaises(KeyError):
            publish.remove_entry(self.doc(), "entries[family=nope.not.real]")

    def test_refuses_an_ambiguous_match(self):
        doc = self.doc()
        doc["entries"].append({"family": "progression.bonus.atk", "compose": "FlatSum"})
        with self.assertRaises(KeyError):
            publish.remove_entry(doc, "entries[family=progression.bonus.atk]")

    def test_refuses_a_missing_array(self):
        with self.assertRaises(KeyError):
            publish.remove_entry({"version": 2}, "entries[family=x]")

    def test_refuses_a_non_array(self):
        with self.assertRaises(KeyError):
            publish.remove_entry({"version": 2, "entries": {}}, "entries[family=x]")

    def test_refuses_a_malformed_spec(self):
        with self.assertRaises(KeyError):
            publish.remove_entry(self.doc(), "entries")
