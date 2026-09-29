"""publish.py notification-catalog domain (NS5.5, world-notify-source spec §4): --add-category
appends a category row, --promote-toast/--promote-critical replace a promotions list wholesale.
Mirrors NotificationCatalogLoader.Parse's own rejections so a bad document is refused at publish
time, never written and caught only at load."""
import importlib.util
import json
import os
import sys
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "publish", os.path.join(os.path.dirname(__file__), "publish.py"))
publish = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(publish)


class AddCategoryTests(unittest.TestCase):
    def test_appends_a_category_row(self):
        doc = {"version": 1, "categories": [], "promotions": {"toast": [], "critical": []}}
        row = publish.add_notification_category(
            doc, "id=loam.shortfall,domain=world,displayName=Loam shortfall,messageKeys=world.turn-entry")
        self.assertEqual(row["id"], "loam.shortfall")
        self.assertEqual(row["domain"], "world")
        self.assertEqual(row["displayName"], "Loam shortfall")
        self.assertEqual(row["messageKeys"], ["world.turn-entry"])
        self.assertEqual(doc["categories"], [row])

    def test_pipe_joins_multiple_message_keys(self):
        doc = {"version": 1, "categories": []}
        row = publish.add_notification_category(
            doc, "id=loam.release,domain=world,displayName=Ground release,messageKeys=world.turn-entry|world.release-forecast")
        self.assertEqual(row["messageKeys"], ["world.turn-entry", "world.release-forecast"])

    def test_refuses_a_duplicate_id(self):
        doc = {"version": 1, "categories": [{"id": "growth", "domain": "world", "displayName": "Growth", "messageKeys": ["k"]}]}
        with self.assertRaises(KeyError):
            publish.add_notification_category(doc, "id=growth,domain=world,displayName=Growth again,messageKeys=k")

    def test_refuses_a_channel_field(self):
        doc = {"version": 1, "categories": []}
        with self.assertRaises(KeyError):
            publish.add_notification_category(
                doc, "id=x,domain=world,displayName=X,messageKeys=k,channel=toast")

    def test_refuses_a_severity_field(self):
        doc = {"version": 1, "categories": []}
        with self.assertRaises(KeyError):
            publish.add_notification_category(
                doc, "id=x,domain=world,displayName=X,messageKeys=k,severity=critical")

    def test_refuses_a_missing_field(self):
        doc = {"version": 1, "categories": []}
        with self.assertRaises(KeyError):
            publish.add_notification_category(doc, "id=x,domain=world,messageKeys=k")

    def test_refuses_an_unknown_field(self):
        doc = {"version": 1, "categories": []}
        with self.assertRaises(KeyError):
            publish.add_notification_category(
                doc, "id=x,domain=world,displayName=X,messageKeys=k,extra=nope")

    def test_refuses_empty_message_keys(self):
        doc = {"version": 1, "categories": []}
        with self.assertRaises(KeyError):
            publish.add_notification_category(doc, "id=x,domain=world,displayName=X,messageKeys=")


class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.doc = {
            "version": 1,
            "categories": [
                {"id": "loam.shortfall", "domain": "world", "displayName": "Shortfall", "messageKeys": ["k"]},
                {"id": "loam.release", "domain": "world", "displayName": "Release", "messageKeys": ["k"]},
                {"id": "growth", "domain": "world", "displayName": "Growth", "messageKeys": ["k"]}
            ],
            "promotions": {"toast": [], "critical": []}
        }

    def test_promote_toast_sets_the_whole_list(self):
        old, new = publish.set_promotion_toast(self.doc, "loam.shortfall,loam.release")
        self.assertEqual(old, [])
        self.assertEqual(new, ["loam.shortfall", "loam.release"])
        self.assertEqual(self.doc["promotions"]["toast"], ["loam.shortfall", "loam.release"])

    def test_promote_toast_refuses_an_unregistered_id(self):
        with self.assertRaises(KeyError):
            publish.set_promotion_toast(self.doc, "nothing.registered")

    def test_promote_critical_refuses_an_id_not_in_toast(self):
        publish.set_promotion_toast(self.doc, "loam.shortfall")
        with self.assertRaises(KeyError):
            publish.set_promotion_critical(self.doc, "growth")

    def test_promote_critical_accepts_an_id_already_in_toast(self):
        publish.set_promotion_toast(self.doc, "loam.shortfall,growth")
        old, new = publish.set_promotion_critical(self.doc, "loam.shortfall")
        self.assertEqual(new, ["loam.shortfall"])

    def test_promote_critical_refuses_an_unregistered_id(self):
        with self.assertRaises(KeyError):
            publish.set_promotion_critical(self.doc, "nothing.registered")


class MainPublishesV2Tests(unittest.TestCase):
    """Real end-to-end shape: category rows and both promotion lists land in ONE publish call,
    mirroring how world-notify-source's NS5.6 actually needs to call this tool (H7 - the readers
    switch to the new version in the same commit as the publish, not this test's own concern, but
    the publish itself must produce a complete, loader-valid v{n+1} in one shot)."""

    def test_add_category_then_promote_in_one_call(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "notification-catalog.v1.json"), "w", encoding="utf-8") as fh:
                json.dump({"schemaVersion": 1, "version": 1, "categories": [],
                           "promotions": {"toast": [], "critical": []}}, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = [
                "publish.py", "notification-catalog",
                "--add-category", "id=loam.shortfall,domain=world,displayName=Loam shortfall,messageKeys=world.turn-entry",
                "--add-category", "id=legion.runway,domain=world,displayName=Legion runway,messageKeys=world.turn-entry",
                "--promote-toast", "loam.shortfall,legion.runway"
            ]
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR, sys.argv = original_dir, original_argv

            with open(os.path.join(tmp, "notification-catalog.v2.json"), encoding="utf-8") as fh:
                out = json.load(fh)
            # v1 stays on disk untouched (T4) — checked before the tempdir is cleaned up.
            self.assertTrue(os.path.exists(os.path.join(tmp, "notification-catalog.v1.json")))

        self.assertEqual(out["version"], 2)
        self.assertEqual([c["id"] for c in out["categories"]], ["loam.shortfall", "legion.runway"])
        self.assertEqual(out["promotions"]["toast"], ["loam.shortfall", "legion.runway"])

    def test_a_row_carrying_channel_refuses_and_publishes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "notification-catalog.v1.json"), "w", encoding="utf-8") as fh:
                json.dump({"version": 1, "categories": [], "promotions": {"toast": [], "critical": []}}, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "notification-catalog",
                        "--add-category", "id=x,domain=world,displayName=X,messageKeys=k,channel=toast"]
            try:
                self.assertEqual(publish.main(), 1)
            finally:
                publish.TUNING_DIR, sys.argv = original_dir, original_argv

            self.assertFalse(os.path.exists(os.path.join(tmp, "notification-catalog.v2.json")))


if __name__ == "__main__":
    unittest.main()
