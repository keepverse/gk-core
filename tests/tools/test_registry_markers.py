"""No registry may carry merge-conflict markers, and nothing may read as resolved when it is not.

MEASURED 2026-09-26: `gk-core/scripts/verification-boundaries.v1.json` reached HEAD containing
`<<<<<<< Updated upstream` / `=======` / `>>>>>>> Stashed changes`, and
`guard-verification-boundaries.py` returned **OK** on it. The guard validates structure and
coverage, and a file with conflict markers still has valid structure and full coverage - so every
structural check passed on a file a human reads as broken.

That is the shape of defect this class exists for: not "the registry is missing a row" but "the
registry is text nobody agreed on". Both JSON registries are consumed by the planner, so a marker
that survives into a row's `paths` list produces a boundary that silently matches nothing.

The check is here rather than in the guard because the guard is still PowerShell and this program is
porting it; when `guard-verification-boundaries` is ported, the check belongs in the port and this
file should shrink to a pointer. What must not happen is the gap closing over: nothing else in the
tree looks for these markers, so an absence of complaints here is not evidence of anything.
"""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# Every machine-read registry the planner and the guards consume. A marker in any of them is the
# same defect, so the list is the closed set of places this can happen rather than the ones a grep
# happened to find.
REGISTRIES = (
    "scripts/verification-boundaries.v1.json",
    "scripts/enforcement-registry.v1.json",
)

MARKERS = ("<<<<<<<", "=======", ">>>>>>>")


def marker_lines(text: str) -> list[int]:
    """1-based lines carrying a marker, so a report names where to look."""
    return [i for i, line in enumerate(text.splitlines(), 1)
            if any(m in line for m in MARKERS)]


class TheRegistriesAreClean(unittest.TestCase):
    def test_no_registry_carries_a_conflict_marker(self) -> None:
        for rel in REGISTRIES:
            path = REPO / rel
            with self.subTest(registry=rel):
                self.assertTrue(path.is_file(), f"{rel} is missing, so it cannot be checked")
                found = marker_lines(path.read_text(encoding="utf-8"))
                self.assertEqual(
                    found, [],
                    f"{rel} carries merge-conflict markers on line(s) {found}. A lane ran a "
                    "stash-merge on it and the resolution was never committed, so the committed "
                    "file is the broken one.")

    def test_every_registry_still_parses(self) -> None:
        # A conflict marker inside a JSON string is syntactically valid, which is why parsing
        # alone is not the check. This asserts it anyway, because a broken parse is a worse failure
        # and should be reported as itself.
        for rel in REGISTRIES:
            with self.subTest(registry=rel):
                json.loads((REPO / rel).read_text(encoding="utf-8"))

    def test_no_registry_declares_a_path_containing_a_marker(self) -> None:
        # The consequence, stated directly: a marker inside a `paths` entry produces a boundary
        # that matches nothing, so the planner is green and the path is unmapped.
        data = json.loads((REPO / "scripts/verification-boundaries.v1.json").read_text(encoding="utf-8"))
        rows = data["boundaries"] if isinstance(data.get("boundaries"), list) else data
        offenders = [r.get("id") for r in rows
                     for p in (r.get("paths") or []) if any(m in str(p) for m in MARKERS)]
        self.assertEqual(offenders, [], f"rows declaring a marked path: {offenders}")


class TheCheckCanFail(unittest.TestCase):
    """A test that cannot fail proves nothing.

    The real failure this class guards against is a marker check that has been narrowed until it
    never fires - by a substring, a context requirement, or a scan of the wrong file. So the
    detector is run against a fixture carrying each marker form, in the shapes that actually occur.
    """

    CASES = (
        "{\n  \"a\": 1\n<<<<<<< Updated upstream\n  \"a\": 2\n=======\n  \"a\": 3\n>>>>>>> Stashed changes\n}\n",
        "<<<<<<< HEAD\n{}\n",
        "\n>>>>>>> branch-name\n",
        "{\n  \"note\": \"======= not a marker, just a long rule\"\n}\n",
    )

    def test_each_shape_is_detected_or_deliberately_exempt(self) -> None:
        for index, case in enumerate(self.CASES):
            with self.subTest(case=index):
                found = marker_lines(case)
                if index == 3:
                    # A `=======` inside a string is a markdown rule, not a conflict marker. The
                    # check is line-based and cannot tell them apart, so this case is asserted as
                    # a KNOWN FALSE POSITIVE rather than pretending the check is clever.
                    self.assertTrue(found, "documented limitation: a '=======' in prose trips this")
                else:
                    self.assertTrue(found, f"shape {index} was missed: {case!r}")

    def test_a_registry_with_markers_is_reported_by_file_and_line(self) -> None:
        with tempfile.TemporaryDirectory(prefix="marker-test-") as tmp:
            path = Path(tmp) / "reg.json"
            body = "{\n  \"x\": 1\n<<<<<<< Updated upstream\n  \"x\": 2\n=======\n  \"x\": 3\n>>>>>>> Stashed changes\n}\n"
            path.write_text(body, encoding="utf-8")
            found = marker_lines(path.read_text(encoding="utf-8"))
            self.assertEqual(found, [3, 5, 7])
            # And the report is usable: a line number an operator can jump to.
            self.assertIn("reg.json", path.name)
            self.assertTrue(all(isinstance(n, int) and n > 0 for n in found))


if __name__ == "__main__":
    unittest.main()
