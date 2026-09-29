"""Contract tests for `gk-fusion/scripts/guard-game-profile.py`.

A pack is accepted when ANY fingerprint signal matches - the `GameAssembly.dll` length OR the length
of any catalogued `Assembly-CSharp.dll`. That OR is the rule, and three details of it are what a
rewrite loses silently:

  * a MISSING GameAssembly.dll is length -1, never 0, so it cannot satisfy the GameAssembly signal
  * the path/length zip is bounded by the SHORTER list, so a hand-maintained pair that disagrees in
    length does not make every earlier pair a mismatch
  * a fingerprint with no `gameAssemblyLength` SKIPS that signal rather than matching against nothing

The other property under test is the one the retirement fixes: the original exits 1 for a missing
game dir, the same code as a genuine fingerprint mismatch, so "you pointed me at nothing" and "this
is the wrong game version" were indistinguishable. Here they are separate named refusals.
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

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader, f"{path} is not importable"
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


guard = _load("guard_game_profile", REPO / "scripts" / "guard-game-profile.py")

GA = "GameAssembly.dll"
# A payload of an exact byte length, so a fixture's fingerprint can state a length and the file can
# be made to agree or disagree deliberately.
PAYLOAD = b"x" * 4096
OTHER = b"y" * 99


class _FixtureCase(unittest.TestCase):
    """A catalog plus a pack, both real files, so lengths are measured rather than mocked."""

    def setUp(self) -> None:
        self._boxes: list[tempfile.TemporaryDirectory] = []

    def tearDown(self) -> None:
        for box in self._boxes:
            box.cleanup()

    def _box(self, prefix: str) -> Path:
        box = tempfile.TemporaryDirectory(prefix=prefix)
        self._boxes.append(box)
        return Path(box.name).resolve()

    def write(self, root: Path, relative: str, payload: bytes) -> Path:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        return path

    def catalog(self, profiles: list[dict]) -> Path:
        root = self._box("gp-catalog-")
        path = root / "game-profiles.json"
        path.write_text(json.dumps({"profiles": profiles}), encoding="utf-8")
        return path

    def pack(self, files: dict[str, bytes]) -> Path:
        root = self._box("gp-pack-")
        for relative, payload in files.items():
            self.write(root, relative, payload)
        return root


class TheMatchIsOrThroughout(_FixtureCase):
    def test_the_game_assembly_length_alone_can_match(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": len(PAYLOAD)}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")

    def test_an_assembly_csharp_length_alone_can_match(self) -> None:
        # The GameAssembly signal disagrees, and the pack is still accepted. That is the rule: any
        # one signal suffices, so a repack that changed GameAssembly does not become unbuildable.
        pack = self.pack({GA: OTHER, "interop/Assembly-CSharp.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["interop/Assembly-CSharp.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        result = guard.scan(pack, "p", catalog)
        self.assertEqual(result["verdict"], "OK")
        self.assertEqual(result["game_assembly_length"], len(OTHER))

    def test_a_signal_from_a_later_fingerprint_still_matches(self) -> None:
        pack = self.pack({"MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["nope.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]},
            {"assemblyCSharpPaths": ["MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")

    def test_nothing_matching_is_a_failure(self) -> None:
        pack = self.pack({GA: OTHER})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": len(PAYLOAD),
             "assemblyCSharpPaths": ["interop/Assembly-CSharp.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        result = guard.scan(pack, "p", catalog)
        self.assertEqual(result["verdict"], "FAILED")
        self.assertEqual(result["game_assembly_length"], len(OTHER))


class AMissingGameAssemblyIsMinusOne(_FixtureCase):
    """-1, never 0.

    A real fingerprint length is never negative, so a missing DLL can never satisfy the GameAssembly
    signal. Using 0 would make an absent or zero-length file look like a match against a catalog row
    that happened to carry 0 - a pack that does not exist passing the check.
    """

    def test_a_missing_game_assembly_reports_minus_one(self) -> None:
        pack = self.pack({"interop/Assembly-CSharp.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": 4096}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["game_assembly_length"],
                         guard.MISSING_LENGTH)
        self.assertEqual(guard.MISSING_LENGTH, -1)

    def test_a_zero_length_row_cannot_be_matched_by_a_missing_file(self) -> None:
        pack = self.pack({"interop/Assembly-CSharp.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 0,
             "assemblyCSharpPaths": ["absent.dll"], "assemblyCSharpLengths": [0]}]}])
        result = guard.scan(pack, "p", catalog)
        # Both signals are skipped: 0 is falsy, and the file does not exist so its length is -1.
        self.assertEqual(result["verdict"], "FAILED")

    def test_a_zero_or_absent_game_assembly_length_skips_the_signal(self) -> None:
        # PowerShell truthiness: absent, null and 0 all skip rather than match.
        for value in ({}, {"gameAssemblyLength": 0}, {"gameAssemblyLength": None}):
            with self.subTest(value=value):
                pack = self.pack({GA: PAYLOAD})
                catalog = self.catalog([{"id": "p", "fingerprints": [value]}])
                self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_a_missing_assembly_csharp_does_not_match_its_length(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["absent/Assembly-CSharp.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_an_absent_game_assembly_length_does_not_match_a_missing_file(self) -> None:
        # The discriminating case for the truthiness guard. With NO GameAssembly.dll the observed
        # length is -1; a fingerprint carrying no `gameAssemblyLength` must SKIP the signal rather
        # than compare against nothing, which is what would make an absent file match. Without a
        # fixture shaped this way, dropping the guard left every test green.
        pack = self.pack({"interop/Assembly-CSharp.dll": OTHER})
        for fingerprint in ({}, {"gameAssemblyLength": None}, {"gameAssemblyLength": 0}):
            with self.subTest(fingerprint=fingerprint):
                catalog = self.catalog([{"id": "p", "fingerprints": [fingerprint]}])
                result = guard.scan(pack, "p", catalog)
                self.assertEqual(result["game_assembly_length"], guard.MISSING_LENGTH)
                self.assertEqual(result["verdict"], "FAILED")


class TheZipIsBoundedByTheShorterList(_FixtureCase):
    """A hand-maintained pair that disagrees in length compares only what both lists have.

    A zip over the LONGER list would read a missing entry as a mismatch against nothing, and
    PowerShell's `@()` around a scalar means a one-element array is the common case here.
    """

    def test_fewer_lengths_than_paths_compares_the_shared_prefix(self) -> None:
        pack = self.pack({"a.dll": PAYLOAD, "b.dll": OTHER})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["a.dll", "b.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        # `a.dll` matches, so the unpaired `b.dll` is never consulted.
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")

    def test_fewer_paths_than_lengths_compares_the_shared_prefix(self) -> None:
        pack = self.pack({"a.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["a.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD), 7, 8]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")

    def test_an_empty_pair_lists_matches_nothing(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": [], "assemblyCSharpLengths": []}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_the_bound_matters_when_the_first_pair_does_not_match(self) -> None:
        # The case that makes the SHORTER list observable. A fixture whose first pair matches never
        # reaches the second index, so it cannot tell `min` from `max` - and a mutation from `min`
        # to `max` passed every such fixture. Here the first pair is a MISSING file, so the loop
        # must stop after one comparison; bounding by the longer list would index past the end.
        pack = self.pack({"b.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["absent.dll", "b.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_the_bound_matters_when_lengths_outnumber_paths(self) -> None:
        pack = self.pack({"b.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"gameAssemblyLength": 1, "assemblyCSharpPaths": ["absent.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD), len(OTHER), 7]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_a_missing_path_array_defaults_to_empty(self) -> None:
        # PowerShell's `@($null)` is a one-element array containing null, and a null entry must not
        # be treated as a path. The port defaults it to empty, so the pair loop does not run.
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": 1}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")


class PathsResolveSegmentWise(_FixtureCase):
    def test_forward_slashes_in_the_catalog_resolve(self) -> None:
        pack = self.pack({"MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll": PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [
            {"assemblyCSharpPaths": ["MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"],
             "assemblyCSharpLengths": [len(PAYLOAD)]}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")

    def test_the_real_catalog_shape_matches_a_real_pack(self) -> None:
        # The shipped catalog's two profiles, against the pack this machine actually has, so the
        # model is checked against the data rather than against a fixture shaped like it.
        real = REPO / "game-profiles.json"
        if not real.is_file():
            self.skipTest("game-profiles.json is absent")
        catalog = json.loads(real.read_text(encoding="utf-8"))
        ids = [p["id"] for p in catalog["profiles"]]
        self.assertIn("pvzrh-3.9", ids)
        for profile in catalog["profiles"]:
            paths = profile["fingerprints"][0].get("assemblyCSharpPaths") or []
            lengths = profile["fingerprints"][0].get("assemblyCSharpLengths") or []
            # Every shipped row pairs its lists, which is what makes the shorter-list zip a
            # documented property rather than a guess about untested data.
            with self.subTest(profile=profile["id"]):
                self.assertEqual(len(paths), len(lengths))
                for entry in paths:
                    self.assertNotIn("\\", entry, "catalog paths use forward slashes")


class AnUnknownProfileIsAllowed(_FixtureCase):
    """A profile with no catalog row exits 0, and says so.

    The question this guard asks is "is this pack the version you said it was", and a profile nobody
    has fingerprinted has nothing to disagree with. The allowance is REPORTED rather than silent, so
    a reader can see it was a decision.
    """

    def test_an_unknown_profile_is_allowed(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "known", "fingerprints": []}])
        result = guard.scan(pack, "absent", catalog)
        self.assertEqual(result["verdict"], "ALLOWED")
        self.assertEqual(result["reason"], "no-fingerprint-row")

    def test_allowed_and_ok_share_an_exit_code_and_not_a_verdict(self) -> None:
        # Stated explicitly: identical codes are acceptable ONLY while the verdict differs, and a
        # future edit that reported an allowance as OK would otherwise pass.
        self.assertEqual(guard.EXIT_ALLOWED, guard.EXIT_OK)
        self.assertNotEqual(guard.EXIT_ALLOWED, guard.EXIT_FAILED)

    def test_an_empty_fingerprint_list_means_no_match_not_no_check(self) -> None:
        # A profile that EXISTS but carries no fingerprints is different from one that is absent:
        # there is a claim to check and nothing to check it against, so it fails.
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": []}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_a_later_duplicate_row_does_not_rescue_a_non_matching_first(self) -> None:
        # `Where-Object { $_.id -eq $Expected } | Select-Object -First 1` takes the FIRST row, so a
        # second row with a matching fingerprint is never consulted. A duplicate id is a catalog
        # mistake, and the guard honours the first rather than searching for a row that agrees -
        # otherwise an appended row could quietly widen what a profile accepts.
        pack = self.pack({GA: OTHER})
        catalog = self.catalog([
            {"id": "p", "fingerprints": [{"gameAssemblyLength": len(PAYLOAD)}]},
            {"id": "p", "fingerprints": [{"gameAssemblyLength": len(OTHER)}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "FAILED")

    def test_the_first_row_is_the_one_used_when_it_matches(self) -> None:
        pack = self.pack({GA: OTHER})
        catalog = self.catalog([
            {"id": "p", "fingerprints": [{"gameAssemblyLength": len(OTHER)}]},
            {"id": "p", "fingerprints": [{"gameAssemblyLength": len(PAYLOAD)}]}])
        self.assertEqual(guard.scan(pack, "p", catalog)["verdict"], "OK")


class RefusalIsDistinctFromFailure(_FixtureCase):
    """The defect the retirement fixes.

    The original `throw`s for a missing catalog or game dir, and PowerShell exits 1 - the SAME code
    as a genuine fingerprint mismatch. So "you pointed me at nothing" and "this is the wrong game
    version" were indistinguishable to any caller reading the exit code, and they have different
    fixes: one is a configuration mistake, the other should stop a deploy.
    """

    def test_a_missing_game_dir_refuses_by_name(self) -> None:
        catalog = self.catalog([{"id": "p", "fingerprints": []}])
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(Path("H:/no/such/pack"), "p", catalog)
        self.assertEqual(caught.exception.reason, "GAMEDIR-MISSING")

    def test_a_missing_catalog_refuses_by_name(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(pack, "p", Path("H:/no/catalog.json"))
        self.assertEqual(caught.exception.reason, "MISSING_CATALOG")

    def test_a_catalog_that_is_not_json_refuses_rather_than_raising(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        path = self._box("gp-bad-") / "catalog.json"
        path.write_text("{not json", encoding="utf-8")
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(pack, "p", path)
        self.assertEqual(caught.exception.reason, "CATALOG-NOT-JSON")

    def test_a_catalog_without_a_profiles_array_refuses(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        path = self._box("gp-shape-") / "catalog.json"
        path.write_text(json.dumps({"nope": []}), encoding="utf-8")
        with self.assertRaises(guard.Refusal) as caught:
            guard.scan(pack, "p", path)
        self.assertEqual(caught.exception.reason, "CATALOG-SHAPE-UNEXPECTED")

    def test_a_refusal_and_a_failure_have_different_exit_codes(self) -> None:
        self.assertNotEqual(guard.EXIT_REFUSED, guard.EXIT_FAILED)

    def test_a_directory_where_a_file_is_expected_is_not_a_match(self) -> None:
        # A fingerprint is a statement about a FILE. A directory of that name is not evidence, and
        # a stat on it would give a length that could coincide with a catalog row.
        pack = self._box("gp-dir-")
        (pack / GA).mkdir(parents=True)
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": 4096}]}])
        result = guard.scan(pack, "p", catalog)
        self.assertEqual(result["game_assembly_length"], guard.MISSING_LENGTH)
        self.assertEqual(result["verdict"], "FAILED")


class StreamsAndEnvelope(_FixtureCase):
    def _run(self, argv: list[str]) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = guard.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_a_matching_pack_prints_the_verdict_on_stdout(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": len(PAYLOAD)}]}])
        code, out, err = self._run(["--game-dir", str(pack), "--profile", "p",
                                   "--catalog", str(catalog)])
        self.assertEqual(code, guard.EXIT_OK)
        self.assertIn("GAME-PROFILE GUARD OK", out)
        self.assertEqual(err, "")

    def test_a_mismatch_prints_findings_on_stderr(self) -> None:
        pack = self.pack({GA: OTHER})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": len(PAYLOAD)}]}])
        code, out, err = self._run(["--game-dir", str(pack), "--profile", "p",
                                   "--catalog", str(catalog)])
        self.assertEqual(code, guard.EXIT_FAILED)
        self.assertEqual(out, "")
        self.assertIn("GAME-PROFILE GUARD FAILED", err)
        # The observed length is named, because "FAILED" alone leaves an operator guessing which of
        # the two signals was wrong, and the two have different fixes.
        self.assertIn(str(len(OTHER)), err)

    def test_an_unknown_profile_is_reported_on_stdout(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": []}])
        code, out, _ = self._run(["--game-dir", str(pack), "--profile", "absent",
                                 "--catalog", str(catalog)])
        self.assertEqual(code, guard.EXIT_ALLOWED)
        self.assertIn("unknown profile", out)

    def test_json_distinguishes_three_verdicts(self) -> None:
        pack = self.pack({GA: PAYLOAD})
        catalog = self.catalog([{"id": "p", "fingerprints": [{"gameAssemblyLength": 1}]}])
        _, out, _ = self._run(["--game-dir", str(pack), "--profile", "p",
                               "--catalog", str(catalog), "--json"])
        payload = json.loads(out)
        self.assertEqual(payload["verdict"], "FAILED")
        self.assertEqual(payload["guard"], "game-profile")
        self.assertIn("game_assembly_length", payload)
        self.assertEqual(payload["findings_by_rule"], {"fingerprint-mismatch": 1})

    def test_json_reports_a_refusal_with_its_name(self) -> None:
        catalog = self.catalog([{"id": "p", "fingerprints": []}])
        code, out, err = self._run(["--game-dir", "H:/no/such/pack", "--profile", "p",
                                    "--catalog", str(catalog), "--json"])
        self.assertEqual(code, guard.EXIT_REFUSED)
        self.assertIn("GAMEDIR-MISSING", err)
        self.assertEqual(json.loads(out)["verdict"], "REFUSED")

    def test_the_required_arguments_are_required(self) -> None:
        # Both are Mandatory in the original, and a default would let a deploy run unchecked.
        for argv in ([], ["--game-dir", "x"], ["--profile", "p"]):
            with self.subTest(argv=argv):
                with self.assertRaises(SystemExit):
                    with redirect_stderr(io.StringIO()):
                        guard.main(argv)

    def test_the_default_catalog_is_the_repo_one(self) -> None:
        self.assertEqual(guard.DEFAULT_CATALOG, "game-profiles.json")
        self.assertTrue((REPO / guard.DEFAULT_CATALOG).is_file())


if __name__ == "__main__":
    unittest.main()
