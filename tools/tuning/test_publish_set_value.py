"""publish.py `set` on a structural (array/object) value — T57 (2026-09-21).

    python -m pytest gk-core/tools/tuning/test_publish_set_value.py -q

Before T57, `set` fed every token through `parse_value`'s int/float/bool/str ladder, which has no
spelling for a JSON array or object: `resolutionOrder=["unique-species","family","general"]` published
the STRING `'["unique-species","family","general"]'` into a new revision and reported `1 change(s)`.
The value's own SHAPE now decides the parser, and a scalar token against a structural key refuses by
name instead of being written.
"""
import importlib.util
import json
import os
import shutil
import sys
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "publish", os.path.join(os.path.dirname(__file__), "publish.py"))
publish = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(publish)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REAL_TUNING_DIR = os.path.join(REPO_ROOT, "data", "tuning")


class StructuralValueTests(unittest.TestCase):
    def test_a_json_array_token_is_stored_as_a_real_list(self):
        self.assertEqual(publish.parse_value_for(["a"], '["b","c"]', "k"), ["b", "c"])
        self.assertIsInstance(publish.parse_value_for(["a"], '["b","c"]', "k"), list)

    def test_a_json_object_token_is_stored_as_a_real_object(self):
        self.assertEqual(publish.parse_value_for({"a": 1}, '{"b": 2}', "k"), {"b": 2})

    def test_a_scalar_token_against_an_array_refuses_by_name(self):
        with self.assertRaises(KeyError) as ctx:
            publish.parse_value_for(["a"], "general", "resolutionOrder")
        self.assertIn("resolutionOrder", str(ctx.exception))
        self.assertIn("array", str(ctx.exception))

    def test_an_array_token_against_an_object_refuses(self):
        with self.assertRaises(KeyError):
            publish.parse_value_for({"a": 1}, "[1]", "k")

    def test_an_invalid_json_token_against_an_array_refuses(self):
        with self.assertRaises(KeyError):
            publish.parse_value_for(["a"], "['b']", "k")

    def test_scalar_keys_keep_the_historical_parse(self):
        # The old ladder is untouched for every scalar key: int, float, bool, string.
        self.assertEqual(publish.parse_value_for(3, "20", "k"), 20)
        self.assertEqual(publish.parse_value_for(1.5, "2.25", "k"), 2.25)
        self.assertIs(publish.parse_value_for(True, "false", "k"), False)
        self.assertEqual(publish.parse_value_for("x", "y", "k"), "y")
        # `bool` is a subclass of `int`, but it is a scalar here, not a structural kind.
        self.assertEqual(publish.parse_value_for(True, "7", "k"), 7)

    def test_value_at_refuses_a_missing_path(self):
        with self.assertRaises(KeyError):
            publish.value_at({"version": 1}, "nope")


class MainPublishesStructuralValuesTests(unittest.TestCase):
    """The refusal half matters as much as the store half: a refused `set` must write NO revision."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        with open(os.path.join(self.tmp, "set-topology.v1.json"), "w", encoding="utf-8") as fh:
            json.dump({"version": 1, "resolutionOrder": ["general"],
                       "memberRoleSet": ["core"], "tiers": {"floor": 1}}, fh)
        self.original_dir, self.original_argv = publish.TUNING_DIR, sys.argv
        publish.TUNING_DIR = self.tmp
        self.addCleanup(lambda: (setattr(publish, "TUNING_DIR", self.original_dir),
                                 setattr(sys, "argv", self.original_argv)))

    def _published(self):
        path = os.path.join(self.tmp, "set-topology.v2.json")
        if not os.path.exists(path):
            return None
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)

    def test_a_list_round_trips_through_main(self):
        sys.argv = ["publish.py", "set-topology",
                    'resolutionOrder=["unique-species","family","general"]']
        self.assertEqual(publish.main(), 0)
        out = self._published()
        self.assertEqual(out["resolutionOrder"], ["unique-species", "family", "general"])
        self.assertNotIsInstance(out["resolutionOrder"], str)
        # ...and the scalar sibling on the same document is still a scalar.
        self.assertEqual(out["tiers"]["floor"], 1)

    def test_an_object_round_trips_through_main(self):
        sys.argv = ["publish.py", "set-topology", 'tiers={"floor":1,"ceiling":4}']
        self.assertEqual(publish.main(), 0)
        self.assertEqual(self._published()["tiers"], {"floor": 1, "ceiling": 4})

    def test_a_scalar_token_against_the_list_refuses_and_publishes_nothing(self):
        sys.argv = ["publish.py", "set-topology", "resolutionOrder=general"]
        self.assertEqual(publish.main(), 1)
        self.assertIsNone(self._published())

    def test_a_scalar_key_still_publishes_a_scalar(self):
        sys.argv = ["publish.py", "set-topology", "tiers.floor=2"]
        self.assertEqual(publish.main(), 0)
        self.assertEqual(self._published()["tiers"]["floor"], 2)


class TheShippedSetTopologyFileTests(unittest.TestCase):
    """The row's own subject, read from the real file: `resolutionOrder` IS a list today, so the
    defect would have corrupted a shipped domain, not a hypothetical shape."""

    def test_the_shipped_resolution_order_is_a_list(self):
        with open(os.path.join(REAL_TUNING_DIR, "set-topology.v1.json"), encoding="utf-8") as fh:
            doc = json.load(fh)
        self.assertIsInstance(doc["resolutionOrder"], list)
        self.assertTrue(all(isinstance(v, str) for v in doc["resolutionOrder"]))

    def test_the_shipped_domain_round_trips_through_a_private_copy(self):
        # The token must DIFFER from the shipped list, or publish.py correctly reports `no changes`.
        # Any other array proves the write path; the private copy is why this cannot touch production.
        with tempfile.TemporaryDirectory() as tmp:
            shutil.copy(os.path.join(REAL_TUNING_DIR, "set-topology.v1.json"),
                        os.path.join(tmp, "set-topology.v1.json"))
            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "set-topology", 'resolutionOrder=["family","general"]']
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR, sys.argv = original_dir, original_argv

            with open(os.path.join(tmp, "set-topology.v2.json"), encoding="utf-8") as fh:
                out = json.load(fh)
        self.assertEqual(out["resolutionOrder"], ["family", "general"])
        self.assertEqual(out["version"], 2)


if __name__ == "__main__":
    unittest.main()
