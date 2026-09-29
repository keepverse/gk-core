#!/usr/bin/env python3
"""Contract tests for gk-core/scripts/guard-stat-pairs.py.

Structured around the FOUR subtleties the port had to preserve, because each is a place where a
careful-looking rewrite silently loses a rule and the guard still passes on the real catalog:

  1. the two catalog arrays are checked SEPARATELY - a counterpart never resolves across them
  2. comparisons are case-INSENSITIVE, as PowerShell's -eq and -notcontains were
  3. a boolean cap is NOT a numeric cap (isinstance(True, int) is True in Python)
  4. whitespace-only is "absent", but a legitimate "0" or "false" is not

Each has a test that FAILS if the property is dropped. The real catalog passes all of them today,
so without these the whole class of regression is invisible.

Asserts the contract and closed vocabulary (the P1-P4 rule ids, the magnitude unit list, the
verdict strings) - never a row count, which grows whenever content ships.
"""
from __future__ import annotations

import importlib.util
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GUARD_PATH = REPO_ROOT / "scripts" / "guard-stat-pairs.py"


def _load():
    spec = importlib.util.spec_from_file_location("guard_stat_pairs", GUARD_PATH)
    assert spec and spec.loader, f"{GUARD_PATH} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules["guard_stat_pairs"] = module
    spec.loader.exec_module(module)
    return module


guard = _load()


def _run(argv: list[str]) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = guard.main(argv)
    return code, out.getvalue(), err.getvalue()


def entry(family, stat_class="Contest", counterpart="", cap=None, unit="GameUnits"):
    return {"family": family, "statClass": stat_class, "counterpart": counterpart,
            "cap": cap, "unitClass": unit}


def prefix(name, stat_class="Contest", counterpart="", cap=None, unit="GameUnits"):
    return {"prefix": name, "statClass": stat_class, "counterpart": counterpart,
            "cap": cap, "unitClass": unit}


class Fixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        (root / "data" / "seed" / "derived-stats").mkdir(parents=True, exist_ok=True)

    def write(self, catalog: dict) -> "Fixture":
        (self.root / "data" / "seed" / "derived-stats" / "catalog.json").write_text(
            json.dumps(catalog), encoding="utf-8")
        return self

    def raw(self, text: str) -> "Fixture":
        (self.root / "data" / "seed" / "derived-stats" / "catalog.json").write_text(
            text, encoding="utf-8")
        return self

    def scan(self) -> dict:
        return guard.scan(self.root)


def fixture(catalog: dict) -> Fixture:
    with tempfile.TemporaryDirectory() as tmp:
        yield_fixture = Fixture(Path(tmp)).write(catalog)
        # scan() eagerly inside the tempdir, so the caller gets a plain dict
        return yield_fixture


class VerdictStringsArePreserved(unittest.TestCase):
    """The Guard suite Assert.Contains these, so they are a contract, not prose."""

    def test_the_ok_string_is_byte_preserved(self) -> None:
        self.assertEqual(
            guard.VERDICT_OK,
            "STAT-PAIRS GUARD OK — every Contest paired, every pair symmetric, "
            "no Race paired, no capped Contest magnitude")

    def test_the_failure_banner_is_byte_preserved(self) -> None:
        self.assertEqual(guard.VERDICT_FAILED, "STAT-PAIRS GUARD FAILED:")


class SubtletyOne_GroupsAreSeparate(unittest.TestCase):
    """A counterpart resolves only WITHIN its own array. `entries` and `prefixFamilies` describe
    different things; pooling them would make every sparse override look like a broken pair."""

    def test_a_counterpart_in_the_other_array_does_NOT_resolve(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="p.B")],
                "prefixFamilies": [prefix("p.B")],
            }).scan()
        self.assertEqual(result["verdict"], "FAIL")
        p2 = [f for f in result["findings"] if f["rule"] == "P2"]
        self.assertEqual(len(p2), 1)
        self.assertIn("does not resolve", p2[0]["message"])

    def test_a_symmetric_pair_inside_each_array_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B"), entry("B", counterpart="A")],
                "prefixFamilies": [prefix("p.A", counterpart="p.B"),
                                   prefix("p.B", counterpart="p.A")],
            }).scan()
        self.assertEqual(result["findings"], [])

    def test_an_asymmetric_pair_within_one_array_fails(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B"), entry("B", counterpart="C"),
                            entry("C", counterpart="B")],
                "prefixFamilies": [],
            }).scan()
        p2 = [f for f in result["findings"] if f["rule"] == "P2"]
        self.assertTrue(any("asymmetric" in f["message"] for f in p2))


class SubtletyTwo_CaseInsensitive(unittest.TestCase):
    """PowerShell's -eq and -notcontains are case-insensitive by default; Python's are not."""

    def test_a_miscased_stat_class_still_counts_as_contest(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("Solo", stat_class="contest", counterpart="")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P1"])

    def test_a_miscased_magnitude_unit_still_counts_as_a_magnitude(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B", cap=5, unit="gameunits"),
                            entry("B", counterpart="A")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P4"])

    def test_a_miscased_race_class_still_counts_as_race(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("R", stat_class="race", counterpart="X"),
                            entry("X", counterpart="R")],
                "prefixFamilies": [],
            }).scan()
        self.assertIn("P3", [f["rule"] for f in result["findings"]])


class SubtletyThree_BooleanIsNotNumeric(unittest.TestCase):
    """`isinstance(True, int)` is True in Python. Without the bool exclusion a JSON `true` cap
    becomes a P4 progression-ceiling finding that does not exist."""

    def test_a_boolean_cap_is_not_reported_as_a_capped_magnitude(self) -> None:
        for cap in (True, False):
            with self.subTest(cap=cap), tempfile.TemporaryDirectory() as tmp:
                result = Fixture(Path(tmp)).write({
                    "entries": [entry("A", counterpart="B", cap=cap),
                                entry("B", counterpart="A")],
                    "prefixFamilies": [],
                }).scan()
                self.assertEqual(result["findings"], [])

    def test_a_real_numeric_cap_is_still_reported(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B", cap=12), entry("B", counterpart="A")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P4"])

    def test_the_predicate_separates_numbers_from_booleans(self) -> None:
        self.assertTrue(guard.is_numeric(1))
        self.assertTrue(guard.is_numeric(1.5))
        self.assertFalse(guard.is_numeric(True))
        self.assertFalse(guard.is_numeric(False))
        self.assertFalse(guard.is_numeric(None))
        self.assertFalse(guard.is_numeric("12"))

    def test_a_cap_on_a_non_magnitude_unit_is_not_a_finding(self) -> None:
        # SigmoidPoints is a deliberately uncapped INPUT (spec-stat-taxonomy.md SS2.5).
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B", cap=0.95, unit="SigmoidPoints"),
                            entry("B", counterpart="A")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual(result["findings"], [])


class SubtletyFour_WhitespaceIsAbsent(unittest.TestCase):
    def test_a_whitespace_counterpart_counts_as_absent_for_p1(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("Solo", counterpart="   ")], "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P1"])

    def test_a_zero_or_false_counterpart_is_present_not_absent(self) -> None:
        # A naive truthiness test would treat "" only, but a strip-based one must not treat a
        # legitimate falsy-LOOKING id as missing.
        row = guard.Row({"family": "A", "counterpart": "0"}, "family", "entries")
        self.assertTrue(row.has_counterpart())
        self.assertFalse(guard.Row({"family": "A", "counterpart": "  "}, "family",
                                   "entries").has_counterpart())
        self.assertFalse(guard.Row({"family": "A"}, "family", "entries").has_counterpart())


class EachRuleFires(unittest.TestCase):
    def test_p1_contest_without_a_counterpart(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("Solo", counterpart="")], "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P1"])

    def test_p2_unresolvable_counterpart(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="Ghost")], "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P2"])

    def test_p3_race_declaring_a_counterpart(self) -> None:
        # BOTH rows declare a counterpart, so P3 fires once per row. Asserting a single finding
        # here would have been a wrong expectation about the guard rather than a real contract.
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("R", stat_class="Race", counterpart="S"),
                            entry("S", stat_class="Race", counterpart="R")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P3", "P3"])
        self.assertEqual({f["name"] for f in result["findings"]}, {"R", "S"})

    def test_p4_capped_contest_magnitude(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("A", counterpart="B", cap=9, unit="GameUnitsPerSecond"),
                            entry("B", counterpart="A", unit="GameUnitsPerSecond")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual([f["rule"] for f in result["findings"]], ["P4"])


class Refusals(unittest.TestCase):
    """A guard that cannot run must say so and exit non-zero; "clean" because the catalog is
    absent is the false green this program exists to remove."""

    def test_a_missing_catalog_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, _out, err = _run(["--root", tmp])
        self.assertEqual(code, 64)
        self.assertIn("CATALOG-MISSING", err)

    def test_malformed_json_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, _out, err = _run(["--root", str(Fixture(Path(tmp)).raw("{not json").root)])
        self.assertEqual(code, 64)
        self.assertIn("CATALOG-UNREADABLE", err)

    def test_a_non_object_catalog_refuses(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, _out, err = _run(["--root", str(Fixture(Path(tmp)).raw("[]").root)])
        self.assertEqual(code, 64)
        self.assertIn("CATALOG-SHAPE", err)

    def test_a_refusal_still_emits_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            code, out, _err = _run(["--root", tmp, "--json"])
        self.assertEqual(code, 64)
        self.assertEqual(json.loads(out)["verdict"], "REFUSED")

    def test_absent_arrays_are_treated_as_empty_not_as_an_error(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({}).scan()
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["rows"], 0)


class ExitVocabulary(unittest.TestCase):
    def test_clean_exits_zero_and_prints_the_preserved_string(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp)).write({"entries": [], "prefixFamilies": []})
            code, out, _err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 0)
        self.assertIn(guard.VERDICT_OK, out)

    def test_a_finding_exits_one_and_names_the_rule(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            fx = Fixture(Path(tmp)).write({"entries": [entry("Solo", counterpart="")],
                                          "prefixFamilies": []})
            code, _out, err = _run(["--root", str(fx.root)])
        self.assertEqual(code, 1)
        self.assertIn(guard.VERDICT_FAILED, err)
        self.assertIn("P1 Solo", err)


class JsonShape(unittest.TestCase):
    def test_findings_carry_the_rule_id_and_a_per_rule_count(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = Fixture(Path(tmp)).write({
                "entries": [entry("Solo", counterpart=""), entry("A", counterpart="Ghost")],
                "prefixFamilies": [],
            }).scan()
        self.assertEqual(result["findings_by_rule"], {"P1": 1, "P2": 1})
        for finding in result["findings"]:
            self.assertIn(finding["rule"], {"P1", "P2", "P3", "P4"})


class TheRealTree(unittest.TestCase):
    """The property the guard exists to hold must hold on the actual repository."""

    def test_the_shipped_catalog_is_clean(self) -> None:
        result = guard.scan(REPO_ROOT)
        self.assertEqual(result["findings"], [], f"ported guard disagrees: {result['findings']}")

    def test_the_shipped_catalog_reads_both_arrays(self) -> None:
        # Not a count assertion: it asserts the guard LOOKED at both arrays, which is the
        # property that would silently break if the group split were dropped.
        result = guard.scan(REPO_ROOT)
        self.assertEqual(set(result["rows_per_group"]), {"entries", "prefixFamilies"})
        self.assertTrue(all(v >= 0 for v in result["rows_per_group"].values()))


if __name__ == "__main__":
    unittest.main()
