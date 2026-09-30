"""Contract tests for `gk-core/scripts/lib/keepverse_roots.py`.

This module is the LIVE implementation of the root resolver. It landed with `bdb2a9a10` ("one
content/core/workspace root resolver per language") alongside `gk-core/tests/Shared/KeepverseRoots.cs` and
`scripts/lib/KeepverseRoots.ps1`. The PowerShell form is being retired as superseded: a differential over
planted trees found 0 disagreements across 15 comparisons, including the cases where BOTH must fail.

So this suite is not proving a new port -- it is filling the gap that made the deletion possible. The
Python module had **no owner row and no contract test of its own**: the only coverage was the C# twin,
which tests a DIFFERENT file. A module nothing maps and nothing tests is a module a change can break
silently, and "the C# twin covers the contract" is not the same claim as "this file is covered".

Each case plants its own tree and states its own expectation, and a failed temp-delete is a FAILURE --
never a swallowed `except`, which is this repository's testing standard.
"""
from __future__ import annotations

import importlib.util
import os
import sys
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
MODULE_PATH = Path(os.environ.get("KEEPVERSE_ROOTS_MODULE",
                                  REPO / "scripts" / "lib" / "keepverse_roots.py")).resolve()

_spec = importlib.util.spec_from_file_location("keepverse_roots", MODULE_PATH)
kr = importlib.util.module_from_spec(_spec)
sys.modules["keepverse_roots"] = kr
_spec.loader.exec_module(kr)

ROOTS = ("content_root", "core_root", "workspace_root")


class Tree:
    """A planted layout plus the deepest directory to walk up from.

    Its own `cleanup`, and `cleanup` FAILS if the delete fails. A temp directory that leaks 65.5 GB is
    what this repository's testing standard is written about, and a swallowed delete is what makes the
    leak invisible.
    """

    def __init__(self, kind: str, pack: str | None = "fusion") -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="kr-contract-")
        self.root = Path(self._tmp.name)
        # `kind` decides the SHAPE and `pack` decides whether a content pack exists. They are separate
        # because a name like "no-pack" is a claim about the tree, and a caller that sets `kind` and
        # forgets `pack` gets a tree that contradicts its own name. The cases below pass `pack` at every
        # call site rather than relying on this default.
        if kind == "legacy":
            (self.root / "data" / "seed").mkdir(parents=True)
            (self.root / "FusionRpg.slnx").write_text("<Project/>", encoding="utf-8")
        elif kind in ("workspace", "workspace-no-pack"):
            (self.root / "gk-core").mkdir(parents=True)
            (self.root / "gk-data").mkdir(parents=True)
            if pack:
                (self.root / "gk-data" / "packs" / pack).mkdir(parents=True)
        elif kind != "empty":
            raise ValueError(kind)
        # The start is DEEPER than the layout root, so every case exercises the walk rather than a hit
        # on the first directory. A resolver tested only from its own directory has not been tested.
        self.start = self.root / "deep" / "deeper"
        self.start.mkdir(parents=True, exist_ok=True)

    def cleanup(self) -> None:
        self._tmp.cleanup()


class TheLegacyLayout(unittest.TestCase):
    def test_EVERY_root_resolves_to_the_repo(self) -> None:
        tree = Tree("legacy")
        self.addCleanup(tree.cleanup)
        for name in ROOTS:
            self.assertEqual(getattr(kr, name)(tree.start), tree.root.resolve(), name)

    def test_a_LEGACY_root_is_found_from_DEEP_inside_it(self) -> None:
        """The walk is the contract. A resolver that only works from its own directory passes a test
        that starts there and fails in every real caller, which is nested under `src/`, `tests/` or
        `tools/`."""
        tree = Tree("legacy")
        self.addCleanup(tree.cleanup)
        deep = tree.root / "tools" / "seedsmith" / "seedsmith"
        deep.mkdir(parents=True)
        self.assertEqual(kr.core_root(deep), tree.root.resolve())

    def test_a_LEGACY_root_is_found_from_ITS_OWN_directory(self) -> None:
        """The walk starts at the START directory itself, not at its parent. Every other case plants a
        `deep/deeper` beneath the layout, so a resolver that skipped its own start directory still found
        the layout two levels up and every one of them passed. Found by falsification.

        This is the shape a real caller has when it resolves from a repository checkout root, and it is
        the one case where a walk that begins one level too high silently resolves the PARENT -- which
        for this repository is the drive root.
        """
        tree = Tree("legacy")
        self.addCleanup(tree.cleanup)
        for name in ROOTS:
            self.assertEqual(getattr(kr, name)(tree.root), tree.root.resolve(), name)

    def test_a_WORKSPACE_is_found_from_its_OWN_directory(self) -> None:
        tree = Tree("workspace")
        self.addCleanup(tree.cleanup)
        self.assertEqual(kr.core_root(tree.root), (tree.root / "gk-core").resolve())
        self.assertEqual(kr.workspace_root(tree.root), tree.root.resolve())

    def test_a_DIRECTORY_named_like_the_solution_is_NOT_a_legacy_repo(self) -> None:
        """`FusionRpg.slnx` is a FILE. An `.exists()` check would accept a DIRECTORY of that name, so a
        stray or half-created checkout would resolve as a repository and every content path would read
        from a tree that cannot serve one. Found by falsification."""
        import tempfile
        with tempfile.TemporaryDirectory(prefix="kr-dirsln-") as tmp:
            root = Path(tmp)
            (root / "FusionRpg.slnx").mkdir()
            (root / "data" / "seed").mkdir(parents=True)
            with self.assertRaises(kr.RootNotFound):
                kr.core_root(root)

    def test_a_GYML_file_alone_is_NOT_a_layout(self) -> None:
        """`FusionRpg.slnx` is a FILE, and `gk-data/packs/fusion/data/seed` must be a DIRECTORY beside it. A solution-shaped
        FILE without the content directory is not a repo, and a check that accepted it would resolve a
        root into a directory that cannot serve one."""
        import tempfile
        with tempfile.TemporaryDirectory(prefix="kr-yaml-") as tmp:
            root = Path(tmp)
            (root / "FusionRpg.slnx").write_text("<Project/>", encoding="utf-8")
            with self.assertRaises(kr.RootNotFound):
                kr.core_root(root)


class TheWorkspaceLayout(unittest.TestCase):
    def test_CORE_is_gk_core_WORKSPACE_is_the_root_and_CONTENT_is_the_pack(self) -> None:
        tree = Tree("workspace")
        self.addCleanup(tree.cleanup)
        self.assertEqual(kr.core_root(tree.start), (tree.root / "gk-core").resolve())
        self.assertEqual(kr.workspace_root(tree.start), tree.root.resolve())
        self.assertEqual(kr.content_root(tree.start),
                         (tree.root / "gk-data" / "packs" / "fusion").resolve())

    def test_the_DEFAULT_pack_is_fusion_and_is_STRUCTURAL(self) -> None:
        self.assertEqual(kr.DEFAULT_PACK, "fusion")
        self.assertEqual(kr.PACK_ENV, "KEEPVERSE_PACK")

    def test_KEEPVERSE_PACK_selects_a_different_pack(self) -> None:
        tree = Tree("workspace", pack="vanilla")
        self.addCleanup(tree.cleanup)
        with mock.patch.dict(os.environ, {"KEEPVERSE_PACK": "vanilla"}, clear=False):
            self.assertEqual(kr.content_root(tree.start),
                             (tree.root / "gk-data" / "packs" / "vanilla").resolve())

    def test_a_MISSING_pack_FAILS_rather_than_falling_back(self) -> None:
        """The contract is "a root is never guessed". Falling back to `fusion` when the requested pack is
        absent would resolve content into a pack the caller did not ask for, and every seed read from
        there would be quietly the wrong corpus."""
        tree = Tree("workspace", pack="vanilla")
        self.addCleanup(tree.cleanup)
        with mock.patch.dict(os.environ, {"KEEPVERSE_PACK": "not-built"}, clear=False):
            with self.assertRaises(kr.RootNotFound) as caught:
                kr.content_root(tree.start)
        self.assertIn("not-built", str(caught.exception))

    def test_a_MISSING_pack_still_leaves_CORE_and_WORKSPACE_resolvable(self) -> None:
        """A content failure must not take the other two roots down with it -- they do not depend on the
        pack existing, and a resolver that refused all three would make one missing directory look like
        a broken repository.

        `pack=None` is PASSED EXPLICITLY. `Tree`'s own default is `"fusion"`, so naming the kind
        `workspace-no-pack` and leaving the default alone built a tree that DID have a pack -- and the
        case passed for the wrong reason until it failed. A fixture whose name contradicts what it sets
        up teaches the wrong lesson in both directions, and this program has now paid for it twice.
        """
        tree = Tree("workspace-no-pack", pack=None)
        self.addCleanup(tree.cleanup)
        with self.assertRaises(kr.RootNotFound):
            kr.content_root(tree.start)
        self.assertEqual(kr.core_root(tree.start), (tree.root / "gk-core").resolve())
        self.assertEqual(kr.workspace_root(tree.start), tree.root.resolve())


class NothingToResolve(unittest.TestCase):
    def test_EVERY_root_FAILS_when_no_layout_is_above(self) -> None:
        import tempfile
        with tempfile.TemporaryDirectory(prefix="kr-empty-") as tmp:
            start = Path(tmp) / "deep"
            start.mkdir()
            for name in ROOTS:
                with self.subTest(root=name):
                    with self.assertRaises(kr.RootNotFound):
                        getattr(kr, name)(start)

    def test_a_NESTED_LEGACY_repo_wins_over_an_OUTER_workspace(self) -> None:
        """The walk stops at the FIRST match, so the NEAREST layout wins whichever kind it is.

        The first version of this case nested a WORKSPACE inside a LEGACY repo, which no walk-order
        mutant could break: the nearest was the workspace either way. So the discriminating shape is
        the other way round -- a LEGACY repo inside a WORKSPACE, where returning the outer one resolves
        content into a pack the caller never asked for. Found by falsification.
        """
        import tempfile
        with tempfile.TemporaryDirectory(prefix="kr-nested2-") as tmp:
            outer = Path(tmp)
            (outer / "gk-core").mkdir(parents=True)
            (outer / "gk-data" / "packs" / "fusion").mkdir(parents=True)
            inner = outer / "inner"
            (inner / "data" / "seed").mkdir(parents=True)
            (inner / "FusionRpg.slnx").write_text("<Project/>", encoding="utf-8")
            start = inner / "deep"
            start.mkdir()
            self.assertEqual(kr.workspace_root(start), inner.resolve(),
                             "the walk must stop at the NEAREST layout, whichever kind it is")
            self.assertEqual(kr.core_root(start), inner.resolve())
            self.assertEqual(kr.content_root(start), inner.resolve())


class TheEnvironmentOverrides(unittest.TestCase):
    """`KEEPVERSE_*_ROOT` wins over any walk. Asserted against a tree that WOULD resolve, so a case
    cannot be satisfied by the walk happening to agree."""

    def setUp(self) -> None:
        import tempfile
        self._tmp = tempfile.TemporaryDirectory(prefix="kr-env-")
        self.tree = Tree("legacy")
        self.addCleanup(self.tree.cleanup)
        self.elsewhere = Path(self._tmp.name) / "elsewhere"
        self.elsewhere.mkdir()

    def test_each_OVERRIDE_wins_for_its_own_root(self) -> None:
        import tempfile
        for name, variable in (("content_root", "KEEPVERSE_CONTENT_ROOT"),
                               ("core_root", "KEEPVERSE_CORE_ROOT"),
                               ("workspace_root", "KEEPVERSE_WORKSPACE_ROOT")):
            with self.subTest(root=name):
                with mock.patch.dict(os.environ, {variable: str(self.elsewhere)}, clear=False):
                    self.assertEqual(getattr(kr, name)(self.tree.start), self.elsewhere)
        del tempfile

    def test_an_OVERRIDE_is_used_even_with_NO_layout_above(self) -> None:
        """The override is checked BEFORE the walk, so it works on a machine that has never had a
        checkout. A resolver that walked first would refuse here, and the override -- the one thing a CI
        job sets deliberately -- would be the one case that failed."""
        import tempfile
        with tempfile.TemporaryDirectory(prefix="kr-nolayout-") as tmp:
            start = Path(tmp)
            with mock.patch.dict(os.environ, {"KEEPVERSE_CORE_ROOT": str(self.elsewhere)},
                                 clear=False):
                self.assertEqual(kr.core_root(start), self.elsewhere)

    def test_an_EMPTY_override_is_IGNORED_rather_than_resolving_to_nothing(self) -> None:
        """`KEEPVERSE_CONTENT_ROOT=""` in a CI environment is common, and an empty string is not a root.
        Reading it would resolve every content path to the current directory."""
        with mock.patch.dict(os.environ, {"KEEPVERSE_CONTENT_ROOT": ""}, clear=False):
            self.assertEqual(kr.content_root(self.tree.start), self.tree.root.resolve())


class TheModule(unittest.TestCase):
    def test_it_names_EVERY_copy_that_exists_and_asks_for_them_to_agree(self) -> None:
        head = MODULE_PATH.read_text(encoding="utf-8")
        self.assertIn("workspace_roots.py", head,
                      "the docstring must name the copy that has to be kept identical")
        # THE CONTRACT IS "every copy on disk is named, and they are kept in step" - not a COUNT.
        # A bare number is a population pin: it reads "two" today, fails the day a third copy lands,
        # and the fix is to edit the number. That is precisely how this assertion drifted out of step -
        # gk-fusion's copy was added, and neither this line nor the docstring was updated, so both went
        # on saying two while three were on disk and ResolverCopyParityTests was already holding all
        # three. The sibling set is walked instead, so a fourth copy cannot be added without this
        # naming it.
        copies: list[tuple[str, str]] = []
        for repo in sorted(p for p in REPO.parent.iterdir()
                           if p.is_dir() and p.name.startswith("gk-")):
            for pattern in ("keepverse_roots.py", "workspace_roots.py"):
                copies.extend((repo.name, hit.relative_to(repo).as_posix())
                              for hit in repo.rglob(pattern)
                              if hit.is_file() and "worktrees" not in hit.parts)
        self.assertGreaterEqual(len(copies), 2,
                                "fewer than two sibling copies were found, so this test is vacuous")
        # The RELATIVE PATH, on a line of its own, not merely the repository's name. The first
        # version of this assertion searched for the name and passed against a docstring I had
        # deliberately broken - "gk-fusion, gk-forge and gk-web are siblings" is four paragraphs
        # earlier, so the name was present no matter what the list said. That is what a mutation run
        # is for: the assertion looked like a contract check and was a substring test.
        # The docstring spells a copy WORKSPACE-relative - `gk-fusion/scripts/lib/keepverse_roots.py` -
        # while the walk yields it repository-relative, so the pair is what is matched. Matching the
        # bare relative path was the second wrong version of this assertion and it failed the
        # UNMODIFIED file, which is the other way a contract check turns into a decoration.
        lines = [line.strip() for line in head.splitlines()]
        for repo_name, relative in copies:
            self.assertTrue(
                any(f"{repo_name}/{relative}" in line for line in lines),
                f"the docstring does not list {repo_name}'s copy ({relative}) as an implementation")
        low = head.lower()
        self.assertIn("byte-identical", low,
                      "the docstring must say how the copies are held in step, not only that they exist")

    def test_the_DOCSTRING_states_the_NEVER_GUESSED_contract(self) -> None:
        head = MODULE_PATH.read_text(encoding="utf-8").lower()
        self.assertIn("never guessed", head,
                      "the contract that a missing layout FAILS rather than guesses")

    def test_the_Start_DEFAULT_is_this_file_s_OWN_directory(self) -> None:
        """Both implementations default to their own directory, so a caller that passes nothing resolves
        from `gk-core/scripts/lib` -- inside the repository in every real case."""
        self.assertEqual(kr._start(None), MODULE_PATH.parent)

    def test_it_makes_NO_subprocess_call(self) -> None:
        """A path resolver must not shell out. Asserted structurally: a `subprocess` import here would be
        a new failure mode on a module that runs inside every test project's discovery."""
        import ast
        tree = ast.parse(MODULE_PATH.read_text(encoding="utf-8"))
        spawned = [n for n in ast.walk(tree)
                   if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                   and n.func.attr in ("run", "Popen", "call", "check_call", "check_output")]
        self.assertEqual(spawned, [], f"the resolver spawns a process: {len(spawned)} site(s)")

    def test_the_RETIRED_POWERSHELL_form_is_not_needed_by_this_module(self) -> None:
        """The `.ps1` is retired as superseded, so nothing here may reference it as a dependency. Its own
        docstring listed the twin, not the other way round."""
        head = MODULE_PATH.read_text(encoding="utf-8")
        self.assertNotIn("KeepverseRoots.ps1", head,
                         "the live module must not name the retired PowerShell form")


if __name__ == "__main__":
    unittest.main()
