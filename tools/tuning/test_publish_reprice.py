"""publish.py --reprice-rung-power-budget / --mark-tuned: ST4.4 (spec-budget-calibration-report.md
contract 5, spec test 8). One publish call retunes the scalar; "untuned" is cleared only on purpose."""
import importlib.util
import json
import os
import sys
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "publish", os.path.join(os.path.dirname(__file__), "publish.py"))
publish = importlib.util.module_from_spec(spec=_spec)
_spec.loader.exec_module(publish)


def _rungs_doc(*, with_budget=True, reference=1000):
    """A v-shaped rung table with just enough shape for the derivation: two rows whose own columns
    decide their budgets."""
    rows = []
    for rung, pool_rolls, q_power in ((1, 1, 1000), (2, 1, 1323)):
        row = {"rung": rung, "poolRolls": pool_rolls, "qPowerMilli": q_power}
        if with_budget:
            row["powerBudgetMilli"] = pool_rolls * reference * q_power // 1000
        rows.append(row)
    return {
        "version": 3,
        "cap": 2,
        "rows": rows,
        "_meta": {"referencePower": reference, "referencePowerUntuned": True},
    }


def _expected(row, reference):
    return row["poolRolls"] * reference * row["qPowerMilli"] // 1000


class RepriceRungPowerBudgetTests(unittest.TestCase):
    def test_every_row_equals_the_derivation_at_the_new_scalar(self):
        doc = _rungs_doc(with_budget=True, reference=1000)

        publish.reprice_rung_power_budget(doc, 800)

        for row in doc["rows"]:
            self.assertEqual(_expected(row, 800), row["powerBudgetMilli"])
        self.assertEqual(doc["_meta"]["referencePower"], 800)

    def test_untuned_survives_a_reprice_and_clears_only_with_mark_tuned(self):
        doc = _rungs_doc()
        publish.reprice_rung_power_budget(doc, 800)
        self.assertTrue(doc["_meta"]["referencePowerUntuned"],
                        "a reprice alone must never clear 'untuned'")

        marked = _rungs_doc()
        publish.reprice_rung_power_budget(marked, 800, mark_tuned=True)
        self.assertFalse(marked["_meta"]["referencePowerUntuned"])

    def test_a_table_without_the_column_is_refused(self):
        doc = _rungs_doc(with_budget=False)
        with self.assertRaises(KeyError) as ctx:
            publish.reprice_rung_power_budget(doc, 800)
        self.assertIn("powerBudgetMilli", str(ctx.exception))
        self.assertIn("add-rung-power-budget", str(ctx.exception))

    def test_a_non_positive_scalar_is_refused(self):
        for bad in (0, -1, True):
            with self.subTest(bad=bad):
                with self.assertRaises(KeyError):
                    publish.reprice_rung_power_budget(_rungs_doc(), bad)

    def test_main_publishes_the_next_version_with_the_new_scalar(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = _rungs_doc(with_budget=True, reference=1000)
            source["version"] = 3
            with open(os.path.join(tmp, "action-rungs.v3.json"), "w", encoding="utf-8") as fh:
                json.dump(source, fh)

            original_dir, original_argv = publish.TUNING_DIR, sys.argv
            publish.TUNING_DIR = tmp
            sys.argv = ["publish.py", "action-rungs", "--reprice-rung-power-budget", "800"]
            try:
                self.assertEqual(publish.main(), 0)
            finally:
                publish.TUNING_DIR, sys.argv = original_dir, original_argv

            with open(os.path.join(tmp, "action-rungs.v4.json"), encoding="utf-8") as fh:
                out = json.load(fh)

        self.assertEqual(out["version"], 4)
        self.assertEqual(out["_meta"]["referencePower"], 800)
        self.assertTrue(out["_meta"]["referencePowerUntuned"])
        for row in out["rows"]:
            self.assertEqual(_expected(row, 800), row["powerBudgetMilli"])


if __name__ == "__main__":
    unittest.main()
