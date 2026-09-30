"""Contract tests for `gk-core/scripts/publish_player.py` and `gk-core/scripts/sync_ci_drop_into_game.py`.

Asserts the CONTRACT: the CLI surface, the version/profile/config decisions, the run() choke point that
makes "every native command is failure-checked" structural, the DropIntoGame layout, the pdb sweep, the
`Server/data` survival rule, the caller/callee coupling, the refusal vocabulary and the `--json` envelope.

WHY THE CHOKE POINT IS THE CONTRACT, NOT A STRING
The original checked `$LASTEXITCODE` after each native command, so whether a failure was noticed depended
on nothing ELSE native having run in between -- an invariant no test could see and no code enforced. Here
one function runs every external command and raises unless it exited 0, so the property is a fact about
the control flow. `every_external_command_goes_through_run` asserts it by AST rather than by grepping for
an idiom, and `VerificationTopologyTests` asserts the same thing on the repository side.

WHY `Server/data` IS PINNED AS A MEASUREMENT
It was deleted by this script until 2026-09-23 -- a line from the initial commit, with no comment and no
caller that ever needed it. With the tree gone, a player install answers `SeedTreeNotFound` forever and
`/health` reports `contentSource: "codeFallback"`, so the entire content layer runs on the code fallback.
The port makes it a REFUSAL, and this case plants a publish whose `Server/data` never appears and asserts
the refusal rather than trusting the comment.

WHY THE CALLER IS TESTED HERE AND NOT IN ITS OWN SUITE
`sync-ci-drop-into-game` exists to call this publisher, and it was ported in the same change because
deleting `publish-player.ps1` would otherwise have left it calling a file that does not exist. The
staleness check is the interesting part: verifying the artefact EXISTS is not enough, because a previous
run's drop satisfies that too.
"""

from __future__ import annotations

import ast
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]

sys.path.insert(0, str(REPO / "scripts" / "lib"))
from keepverse_roots import owning_base  # noqa: E402
PUBLISH = Path(os.environ.get("PUBLISH_PLAYER_SCRIPT", REPO / "scripts" / "publish_player.py")).resolve()
SYNC = Path(os.environ.get("SYNC_CI_DROP_SCRIPT",
                           REPO / "scripts" / "sync_ci_drop_into_game.py")).resolve()
RUN_TIMEOUT = 900

_spec = importlib.util.spec_from_file_location("publish_player", PUBLISH)
pp = importlib.util.module_from_spec(_spec)
sys.modules["publish_player"] = pp
_spec.loader.exec_module(pp)

# sync_ci_drop_into_game imports publish_player by sibling path, so the module above must already be in
# sys.modules -- which is why it is loaded first rather than after.
_spec2 = importlib.util.spec_from_file_location("sync_ci_drop_into_game", SYNC)
sc = importlib.util.module_from_spec(_spec2)
sys.modules["sync_ci_drop_into_game"] = sc
_spec2.loader.exec_module(sc)

EXIT_VOCABULARY = {0, 1}
STAGE_VOCABULARY = set(pp.STAGES) | {"reset-output", "unknown", "cache"}
FORBIDDEN_DIALECT_TOKENS = ("pwsh", "powershell", "-NoProfile", "-ExecutionPolicy", "$LASTEXITCODE")
DECLARED_PS1_REFERENCES = {
    "publish-player.ps1": "the retired original, named only in the module docstring explaining what it "
                           "replaced and why",
    "sync-ci-drop-into-game.ps1": "the retired original of the caller, named in its own docstring",
    # The refs fetcher is named with the extension it HAS on disk, and the case below resolves the name
    # and fails if it is absent. That contract is what caught this entry going stale: the fetcher was
    # ported from `fetch-bepinex-refs.ps1` to `fetch_bepinex_refs.py`, and the refusal text kept naming
    # the retired form until the port that removed it. A rot citation introduced by the very commit
    # that removes rot citations, found by the guard that was already there.
    "fetch_bepinex_refs.py": "the refs fetcher, ported in this program; the refusal names the extension "
                             "it HAS, and the case below fails if that path does not exist",
}


def code_without_retired_dialect(text: str) -> str:
    """Source reduced to the text a retired-dialect CALL could live in: docstrings and comments out, every
    other string and every identifier in."""
    tree = ast.parse(text)
    bare = {id(n.value) for n in ast.walk(tree)
            if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant)
            and isinstance(n.value.value, str)}
    parts: list[str] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if id(node) not in bare:
                parts.append(node.value)
        elif isinstance(node, (ast.Name, ast.Attribute)):
            parts.append(getattr(node, "id", None) or getattr(node, "attr", ""))
    return " ".join(parts)


# ------------------------------------------------------------------------------------------------
# A repository-shaped fixture
# ------------------------------------------------------------------------------------------------

class Repo:
    """A throwaway repository with the files the pipeline reads, so no stage needs a real build."""

    def __init__(self, root: Path) -> None:
        self.root = root
        for rel in ("web/fusion-rpg-web", "src/FusionRpg.Server/wwwroot", "src/FusionRpg.Launcher",
                    "docs/runbook", "scripts"):
            (root / rel).mkdir(parents=True, exist_ok=True)
        (root / "web" / "fusion-rpg-web" / "package-lock.json").write_text("{}", encoding="utf-8")
        (root / "docs" / "runbook" / "PLAYERS.txt").write_text("players", encoding="utf-8")
        (root / "LICENSE").write_text("licence", encoding="utf-8")
        (root / "game-profiles.json").write_text("{}", encoding="utf-8")
        (root / "src" / "FusionRpg.Launcher" / "loader-manifest.json").write_text("{}", encoding="utf-8")

    def game_dir(self) -> Path:
        """A legal game tree, under a sibling of the repo so the parent's probe finds it."""
        game = self.root.parent / (self.root.name + "-game")
        (game / "BepInEx" / "core").mkdir(parents=True, exist_ok=True)
        (game / "BepInEx" / "interop").mkdir(parents=True, exist_ok=True)
        return game

    def melon_dir(self, game_assembly_bytes: int | None = None) -> Path:
        ml = self.root.parent / (self.root.name + "-melon")
        (ml / "MelonLoader" / "net6").mkdir(parents=True, exist_ok=True)
        (ml / "MelonLoader" / "net6" / "MelonLoader.dll").write_text("m", encoding="utf-8")
        if game_assembly_bytes is not None:
            (ml / "GameAssembly.dll").write_bytes(b"\0" * game_assembly_bytes)
        return ml


class TemporaryRepo(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="publish-contract-")
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()
        self.repo = Repo(self.root)
        self.addCleanup(self._tmp.cleanup)
        # The pipeline's own outputs are created by a fake publish, so no real build is ever needed.
        self.fake = self.install_fake_publisher()

    def install_fake_publisher(self, *, server_data: bool = True, drop_mtime_offset: float = 0.0,
                              publish_raises: BaseException | None = None):
        """Replace the three external stages, and plant the artefacts they would have produced.

        `server_data=False` is the case the whole `Server/data` rule exists for: a publish that leaves no
        content tree, which the real publisher's `<Content Link="data/...">` items make possible and a
        broken pack make visible.
        """
        def fake_publish_server(layout, version, timeout):
            layout.server_out.mkdir(parents=True, exist_ok=True)
            (layout.server_out / "FusionRpg.Server.exe").write_text("exe", encoding="utf-8")
            if server_data:
                (layout.server_out / "data" / "tuning").mkdir(parents=True, exist_ok=True)
                (layout.server_out / "data" / "seed").mkdir(parents=True, exist_ok=True)

        def fake_publish_launcher(layout, version, timeout):
            layout.out.mkdir(parents=True, exist_ok=True)
            (layout.out / "FusionRpg.Launcher.exe").write_text("exe", encoding="utf-8")

        def fake_injector(layout, version, game_dir, env, timeout):
            layout.plugin_out.mkdir(parents=True, exist_ok=True)
            (layout.plugin_out / "FusionRpg.Injector.dll").write_text("dll", encoding="utf-8")
            (layout.plugin_out / "FusionRpg.Injector.pdb").write_text("pdb", encoding="utf-8")
            (layout.plugin_out / "notes.txt").write_text("txt", encoding="utf-8")

        # KEYED BY STAGE, so a case can `unfake` the one it is about. Three cases in the first version
        # reached a FAKE and asserted nothing -- the fake was the very code under test -- and a case that
        # silently tests a mock always passes, which is worse than a red because it stops looking.
        staged = {
            "stage_publish_server": fake_publish_server,
            "stage_publish_launcher": fake_publish_launcher,
            "stage_injector": fake_injector,
            "stage_web": lambda layout, timeout: None,
        }
        by_name: dict = {}
        for name, replacement in staged.items():
            patch = mock.patch.object(pp, name, side_effect=replacement)
            patch.start()
            self.addCleanup(patch.stop)
            by_name[name] = patch
        which = mock.patch.object(pp, "which", return_value="npm")
        which.start()
        self.addCleanup(which.stop)
        by_name["which"] = which
        return by_name

    def unfake(self, *names: str) -> None:
        """Stop faking these stages, so a case exercises the REAL one."""
        for name in names:
            self.fake[name].stop()

    def env(self, **overrides) -> dict[str, str]:
        base = {"FUSIONRPG_GAME_DIR": str(self.repo.game_dir())}
        base.update(overrides)
        return base


# ------------------------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------------------------

class Configuration(unittest.TestCase):
    def test_an_unset_VERSION_gets_the_publisher_default(self) -> None:
        self.assertEqual(pp.version_from_env({}), pp.DEFAULT_VERSION)

    def test_ONE_leading_v_is_stripped(self) -> None:
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "v2.3.4"}), "2.3.4")
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "V2.3.4"}), "2.3.4")
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "2.3.4"}), "2.3.4")

    def test_a_VERSION_of_nothing_but_a_v_is_REFUSED_not_passed_on(self) -> None:
        """`.TrimStart("v", "V")` strips EVERY leading v, and on input `vv` the original would have
        produced an EMPTY version that flowed straight into `-p:Version=`. Empty is not a version."""
        for raw in ("v", "V", " v "):
            with self.assertRaises(pp.Refusal) as caught:
                pp.version_from_env({"FUSIONRPG_VERSION": raw})
            self.assertEqual(caught.exception.reason, "BAD-VERSION")

    def test_the_caller_default_is_DELIBERATELY_different_from_the_publishers(self) -> None:
        """This cache is a development artefact; sharing the publisher's default would stamp it with a
        player release number. Two constants with a reason beats one that is wrong half the time."""
        self.assertNotEqual(sc.DEFAULT_VERSION, pp.DEFAULT_VERSION)
        self.assertEqual(sc.version_from_env({}), sc.DEFAULT_VERSION)

    def test_the_CI_DROP_flag_is_TRUE_only_for_the_exact_string_1(self) -> None:
        for value, expected in (("1", True), ("0", False), ("true", False), ("", False), ("11", False)):
            self.assertEqual(pp.use_ci_drop({"FUSIONRPG_USE_CI_DROP": value}), expected, value)
        self.assertFalse(pp.use_ci_drop({}))

    def test_the_melon_profile_is_FORCED_by_the_env_and_AUTO_detected_otherwise(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ml = Path(tmp)
            (ml / "GameAssembly.dll").write_bytes(b"\0" * pp.MELON_39_GAMEASSEMBLY_BYTES)
            self.assertEqual(pp.detect_melon_profile(ml, {}), "pvzrh-3.9")
            self.assertEqual(pp.detect_melon_profile(ml, {"FUSIONRPG_GAME_PROFILE": "pvzrh-3.8.1"}),
                             "pvzrh-3.8.1")
            (ml / "GameAssembly.dll").write_bytes(b"\0" * 12345)
            self.assertEqual(pp.detect_melon_profile(ml, {}), pp.DEFAULT_MELON_PROFILE)

    def test_melon_is_UNAVAILABLE_without_the_melonloader_assembly(self) -> None:
        self.assertFalse(pp.melon_is_available(None))
        with tempfile.TemporaryDirectory() as tmp:
            self.assertFalse(pp.melon_is_available(Path(tmp)))
            (Path(tmp) / "MelonLoader").mkdir()
            (Path(tmp) / "MelonLoader" / "net6").mkdir()
            (Path(tmp) / "MelonLoader" / "net6" / "MelonLoader.dll").write_text("m", encoding="utf-8")
            self.assertTrue(pp.melon_is_available(Path(tmp)))


# ------------------------------------------------------------------------------------------------
# run(): the choke point
# ------------------------------------------------------------------------------------------------

class TheChokePoint(unittest.TestCase):
    """`run()` is the only place an external command is invoked, and it raises unless it exited 0."""

    def test_a_NON_ZERO_exit_becomes_a_NAMED_refusal_carrying_the_STAGE_and_the_output(self) -> None:
        completed = subprocess.CompletedProcess(args=[], returncode=3, stdout="", stderr="boom\ndetail\n")
        with mock.patch.object(pp.subprocess, "run", return_value=completed):
            with self.assertRaises(pp.Refusal) as caught:
                pp.run(["dotnet", "publish", "x"], REPO, 5, "publish-server")
        self.assertEqual(caught.exception.stage, "publish-server")
        self.assertEqual(caught.exception.reason, "EXIT-NON-ZERO")
        self.assertIn("exited 3", caught.exception.detail)
        self.assertIn("boom", caught.exception.detail)

    def test_a_FAILED_command_with_NO_output_says_so_rather_than_showing_nothing(self) -> None:
        completed = subprocess.CompletedProcess(args=[], returncode=1, stdout="", stderr="")
        with mock.patch.object(pp.subprocess, "run", return_value=completed):
            with self.assertRaises(pp.Refusal) as caught:
                pp.run(["x"], REPO, 5, "web")
        self.assertIn("no output at all", caught.exception.detail)

    def test_a_WEDGED_command_becomes_a_NAMED_timeout_refusal(self) -> None:
        with mock.patch.object(pp.subprocess, "run",
                               side_effect=pp.subprocess.TimeoutExpired(cmd="dotnet", timeout=11)):
            with self.assertRaises(pp.Refusal) as caught:
                pp.run(["dotnet", "publish", "x"], REPO, 11, "publish-launcher")
        self.assertEqual(caught.exception.reason, "TIMEOUT")
        self.assertIn("11s", caught.exception.detail)

    def test_a_MISSING_executable_is_a_NAMED_refusal_not_a_traceback(self) -> None:
        with mock.patch.object(pp.subprocess, "run", side_effect=FileNotFoundError("npm")):
            with self.assertRaises(pp.Refusal) as caught:
                pp.run(["npm", "ci"], REPO, 5, "web")
        self.assertEqual(caught.exception.reason, "NOT-ON-PATH")

    def test_EVERY_external_command_goes_through_run_and_nothing_else(self) -> None:
        """THE STRUCTURAL CONTRACT, asserted by AST rather than by grepping for an idiom.

        The original read `$LASTEXITCODE` after each command, so a failure was noticed only if nothing
        ELSE native had run in between. This walks the tool's own AST and requires that `subprocess.run`
        is called from exactly one place -- `run()` -- and nowhere else. A later edit that spawns a
        process directly fails here rather than silently reintroducing the fragility.
        """
        calls: list[ast.AST] = []
        for node in ast.walk(ast.parse(PUBLISH.read_text(encoding="utf-8"))):
            # `subprocess` is a plain NAME (`subprocess.run(...)`), not an attribute chain, so the receiver
            # check is `ast.Name`. The first version looked for an Attribute, found ZERO call sites, and
            # read that as "the tool spawns nothing directly" -- a pass for the wrong reason.
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                    and node.func.attr == "run" and isinstance(node.func.value, ast.Name) \
                    and node.func.value.id == "subprocess":
                calls.append(node)
        self.assertEqual(len(calls), 1,
                         f"subprocess.run is called from {len(calls)} places; every external command must "
                         f"go through the one that raises on a non-zero exit")
        enclosing = [n for n in ast.walk(ast.parse(PUBLISH.read_text(encoding="utf-8")))
                     if isinstance(n, ast.FunctionDef) and n.name == "run"]
        self.assertEqual(len(enclosing), 1)

    def test_the_pipeline_calls_NPM_CI_before_NPM_BUILD(self) -> None:
        """The order is a contract: a build against an unlocked tree is not reproducible, and CI consumes
        the result. `VerificationTopologyTests` asserts the same ordering repository-side."""
        source = PUBLISH.read_text(encoding="utf-8")
        self.assertLess(source.index('run(["npm", "ci"]'), source.index('run(["npm", "run", "build"]'))

    def test_the_tool_never_checks_for_node_modules_before_building(self) -> None:
        """A pre-existing `node_modules` check short-circuits `npm ci`, which is the one step that makes
        the pack reproducible. The topology test asserts the absence; so does this one."""
        code = code_without_retired_dialect(PUBLISH.read_text(encoding="utf-8"))
        self.assertNotIn("node_modules", code)


# ------------------------------------------------------------------------------------------------
# The layout
# ------------------------------------------------------------------------------------------------

class TheGapsFalsificationFound(TemporaryRepo):
    """Cases added because a mutation SURVIVED. Each names the mutation that motivated it."""

    def test_a_GENERIC_OSError_is_a_NAMED_refusal_not_a_swallow(self) -> None:
        """`FileNotFoundError` is an `OSError` SUBCLASS, so testing only that left the generic branch
        unpinned -- and `run_swallows_an_OS_error` survived because of it. A spawn that fails for any
        other reason (a revoked handle, a resource limit) must still fail CLOSED and by name."""
        for exc in (PermissionError("denied"), OSError("too many open files")):
            with mock.patch.object(pp.subprocess, "run", side_effect=exc):
                with self.assertRaises(pp.Refusal) as caught:
                    pp.run(["x"], REPO, 5, "injector")
            self.assertEqual(caught.exception.reason, "SPAWN-FAILED", type(exc).__name__)

    def test_ONLY_ONE_leading_v_is_stripped_not_a_RUN_of_them(self) -> None:
        """`.TrimStart("v", "V")` strips EVERY leading v, so `vv1.2.3` became `1.2.3` by way of removing
        two characters and `vviceroy` became `iceroy`. The port removes exactly one -- and `vv` is the
        input that tells the two apart, which is why the case that motivated the fix had no test."""
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "vv1.2.3"}), "v1.2.3")
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "vVicErOy"}), "VicErOy")
        self.assertEqual(pp.version_from_env({"FUSIONRPG_VERSION": "v1.2.3-rc"}), "1.2.3-rc")

    def test_PUBLISH_writes_ALL_THREE_Bep_destinations_not_just_the_scoped_one(self) -> None:
        """Both the probe and the earlier cases called `copy_with_suffixes` DIRECTLY, so nothing exercised
        the three-destination call inside `publish()` -- and `the_Bep_drop_reaches_only_the_SCOPED_
        destination` survived. A pack that wrote only the scoped path would work for nobody, because 3.8.1
        Bep players read the unscoped one."""
        report = pp.publish(self.root, self.env(), 5)
        drop = self.root / "dist" / "FusionRpg" / "DropIntoGame"
        for destination, label in ((drop / "BepInEx", "legacy unscoped"),
                                   (drop / "pvzrh-3.8.1" / "BepInEx", "3.8.1 scoped"),
                                   (drop, "legacy flat")):
            self.assertTrue((destination / "FusionRpg.Injector.dll").is_file(),
                            f"the {label} destination got no plugin: {destination}")
        self.assertEqual(report.plugin_files, 2)

    def test_the_json_envelope_carries_the_KEYS_and_not_a_field_that_could_rot(self) -> None:
        """`the_json_envelope_gains_a_per_run_field` survived because publish_player had no envelope-key
        case at all -- the mutate and coverage ports each have one, and this one was written without it.
        The key SET is the contract; a field that differs per run is a field nothing can assert on."""
        import io
        import contextlib
        # `main` reads the REAL environment -- that is what a CLI does, and it is why this case first
        # failed at NO-REFERENCE-TREE: it had no FUSIONRPG_GAME_DIR. The environment is supplied here
        # rather than by passing a flag the tool does not have.
        with mock.patch.dict(os.environ, self.env(), clear=False), \
                mock.patch.object(pp.subprocess, "run") as runner:
            runner.return_value = subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")
            # The real stages need real artefacts, so the three expensive ones are faked and the ENTRY
            # POINT is what runs -- the envelope is produced by `main`, not by the stages.
            for name in ("stage_publish_server", "stage_publish_launcher", "stage_injector", "stage_web"):
                patch = mock.patch.object(pp, name)
                patch.start()
                self.addCleanup(patch.stop)
            def fake_server(layout, version, timeout):
                layout.server_out.mkdir(parents=True, exist_ok=True)
                (layout.server_out / "data").mkdir(parents=True, exist_ok=True)
            mock.patch.object(pp, "stage_publish_server",
                              side_effect=fake_server).start()
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = pp.main(["--root", str(self.root), "--json", "--timeout", "5"])
        self.assertEqual(code, 0, buffer.getvalue())
        report = json.loads(buffer.getvalue())
        self.assertEqual(set(report), {"tool", "verdict", "version", "game_dir", "ci_drop",
                                       "melon_profile", "plugin_files", "melon_files", "pdbs_removed",
                                       "server_data_present", "stages", "out"})
        for banned in ("duration", "elapsed", "timestamp", "started", "finished", "seed"):
            self.assertNotIn(banned, report, f"{banned!r} differs per run, so nothing can assert on it")


class TheLayout(TemporaryRepo):
    def test_every_path_is_resolved_ONCE_into_one_Layout(self) -> None:
        """The original re-derived `$Out`, `$Drop` and friends inline at eleven points, which is why the
        Drop shape could not be asserted without a build. Resolved once, it can."""
        layout = pp.Layout.build(self.root)
        self.assertEqual(layout.out, self.root / "dist" / "FusionRpg")
        self.assertEqual(layout.server_out, layout.out / "Server")
        self.assertEqual(layout.drop, layout.out / "DropIntoGame")
        self.assertEqual(layout.drop_bep_legacy(), layout.drop / "BepInEx")
        self.assertEqual(layout.drop_bep_381(), layout.drop / "pvzrh-3.8.1" / "BepInEx")
        self.assertEqual(layout.drop_melon_scoped("pvzrh-3.9"),
                         layout.drop / "pvzrh-3.9" / "MelonLoader")

    def test_the_Bep_drop_reaches_all_THREE_legacy_and_scoped_destinations(self) -> None:
        """3.8.1 Bep players read the unscoped path, so a pack that only writes the scoped one works for
        nobody. Three destinations, one copy each."""
        layout = pp.Layout.build(self.root)
        layout.plugin_out.mkdir(parents=True)
        for name in ("FusionRpg.Injector.dll", "FusionRpg.Injector.pdb", "plugin.json", "notes.txt"):
            (layout.plugin_out / name).write_text("x", encoding="utf-8")
        count = pp.copy_with_suffixes(layout.plugin_out,
                                      [layout.drop_bep_legacy(), layout.drop_bep_381(), layout.drop],
                                      pp.PLUGIN_FILE_SUFFIXES)
        self.assertEqual(count, 3, "the .txt is not a plugin payload and must not be copied")
        for destination in (layout.drop_bep_legacy(), layout.drop_bep_381(), layout.drop):
            self.assertTrue((destination / "FusionRpg.Injector.dll").is_file(), destination)
            self.assertTrue((destination / "plugin.json").is_file(), destination)
            self.assertFalse((destination / "notes.txt").exists(), destination)

    def test_the_pdb_sweep_removes_pdbs_from_the_DROPS_too(self) -> None:
        """The sweep runs after the drops are laid out, so a pdb inside `DropIntoGame` would ship -- and a
        pdb in a downloaded pack embeds the builder's source path."""
        layout = pp.Layout.build(self.root)
        (layout.drop / "BepInEx").mkdir(parents=True)
        (layout.drop / "BepInEx" / "a.pdb").write_text("p", encoding="utf-8")
        (layout.out / "b.pdb").write_text("p", encoding="utf-8")
        (layout.out / "keep.dll").write_text("d", encoding="utf-8")
        self.assertEqual(pp.sweep_pdbs(layout.out), 2)
        self.assertFalse((layout.drop / "BepInEx" / "a.pdb").exists())
        self.assertTrue((layout.out / "keep.dll").is_file())

    def test_a_recursive_delete_OUTSIDE_the_repository_is_REFUSED(self) -> None:
        """The original ran `Remove-Item -Recurse -Force` on a path assembled from configuration with no
        containment check at all. The one reachable hazard is a path that escapes the tree."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "repo"
            root.mkdir()
            outside = Path(tmp) / "outside"
            outside.mkdir()
            (outside / "precious.txt").write_text("keep", encoding="utf-8")
            with self.assertRaises(pp.Refusal) as caught:
                pp.reset_directory(outside, root)
            self.assertEqual(caught.exception.reason, "DELETE-OUTSIDE-REPO")
            self.assertTrue((outside / "precious.txt").is_file())


# ------------------------------------------------------------------------------------------------
# The pipeline
# ------------------------------------------------------------------------------------------------

class ThePipeline(TemporaryRepo):

    def test_a_MELON_build_that_PRODUCES_no_DLL_is_a_REFUSAL_not_a_warning(self) -> None:
        """The original made this a `Write-Warning` in an earlier revision, and a warning is invisible to
        a CI step reading the exit code and to anyone scrolling a release log. The port made it a
        refusal; this case is what keeps it one.

        Both the probe and the earlier cases only ever built a Melon drop that PRODUCED the DLL, so
        disabling the guard was unobservable and the mutation survived.
        """
        mel = pp.Layout.build(self.root)
        mel.melon_plugin_out.mkdir(parents=True, exist_ok=True)
        # A build "succeeded" and wrote something -- just not the DLL the profile requires.
        (mel.melon_plugin_out / "FusionRpg.Injector.MelonLoader.dll.pdb").write_text("p", encoding="utf-8")
        completed = subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")
        with mock.patch.object(pp.subprocess, "run", return_value=completed):
            with self.assertRaises(pp.Refusal) as caught:
                pp.stage_melon(mel, "1.0.0", {"FUSIONRPG_ML_GAMEDIR": str(self.repo.melon_dir())}, 5)
        self.assertEqual(caught.exception.stage, "melon")
        self.assertEqual(caught.exception.reason, "OUTPUT-MISSING")

    def test_a_COMPLETE_MELON_drop_copies_the_Melon_suffixes_and_the_legacy_path(self) -> None:
        """The positive case, which was missing. 3.8.1 is the profile that also writes the legacy
        UNSCOPED `DropIntoGame/MelonLoader`, so a pack for it has to reach players reading either path;
        3.9 must NOT, because writing it would put a 3.9 plugin where a 3.8.1 player looks for it."""
        mel = pp.Layout.build(self.root)
        # The build output is WRITTEN BY THE MOCK, keyed off `-p:OutputPath=`, because `stage_melon`
        # resets the plugin directory first -- a fixture that plants files there has them deleted before
        # the build runs, and then asserts against an empty tree.
        def fake_build(argv, cwd, timeout, stage):
            for index, arg in enumerate(argv):
                if arg.startswith("-p:OutputPath="):
                    out = Path(arg.split("=", 1)[1])
                    out.mkdir(parents=True, exist_ok=True)
                    for name in ("FusionRpg.Injector.MelonLoader.dll", "plugin.json", "plugin.pdb",
                                 "plugin.cfg", "notes.txt"):
                        (out / name).write_text("x", encoding="utf-8")
            return ""

        with mock.patch.object(pp, "run", side_effect=fake_build):
            profile, files = pp.stage_melon(
                mel, "1.0.0", {"FUSIONRPG_ML_GAMEDIR": str(self.repo.melon_dir()),
                               "FUSIONRPG_GAME_PROFILE": "pvzrh-3.8.1"}, 5)
        self.assertEqual(profile, "pvzrh-3.8.1")
        self.assertEqual(files, 4, "notes.txt is not a Melon payload, so four of the five are copied")
        for destination in (mel.drop / "pvzrh-3.8.1" / "MelonLoader", mel.drop / "MelonLoader"):
            self.assertTrue((destination / "plugin.cfg").is_file(), destination)
            self.assertTrue((destination / "FusionRpg.Injector.MelonLoader.dll").is_file(), destination)
            self.assertFalse((destination / "notes.txt").exists(), destination)

    def test_a_publish_with_NO_content_tree_is_REFUSED_rather_than_reported_as_a_pack(self) -> None:
        """The defect this rule exists for. It was deleted here until 2026-09-23; with it gone a player
        install answers SeedTreeNotFound forever and /health reports contentSource codeFallback."""
        # RE-FAKED rather than unfaked: unfaking ran the REAL stage, whose `dotnet publish` fails on a
        # csproj the temp repo does not have, so the case reported EXIT-NON-ZERO and never reached the
        # rule it exists to pin. The behaviour under test is "a publish that omits the content tree",
        # and a fake that omits it is the only way to produce that here.
        self.unfake("stage_publish_server")
        dataless = mock.patch.object(
            pp, "stage_publish_server",
            side_effect=lambda layout, version, timeout: (
                layout.server_out.mkdir(parents=True, exist_ok=True),
                (layout.server_out / "FusionRpg.Server.exe").write_text("exe", encoding="utf-8")))
        dataless.start()
        self.addCleanup(dataless.stop)
        with self.assertRaises(pp.Refusal) as caught:
            pp.publish(self.root, self.env(), 5)
        self.assertEqual(caught.exception.reason, "SERVER-DATA-MISSING")
        self.assertIn("codeFallback", caught.exception.detail)

    def test_the_CI_DROP_path_NEEDS_no_game_dir_and_REJECTS_an_incomplete_cache(self) -> None:
        self.unfake("stage_injector")   # the real stage is what checks the cache
        env = self.env(FUSIONRPG_USE_CI_DROP="1", FUSIONRPG_GAME_DIR="")
        with self.assertRaises(pp.Refusal) as caught:
            pp.publish(self.root, env, 5)
        self.assertEqual(caught.exception.reason, "CI-DROP-INCOMPLETE")
        # The refusal names the tool that BUILDS the cache, and that spelling must exist on disk.
        # The named tool lives in the REPOSITORY, not the temp fixture repo, so BOTH bases count --
        # the first version checked only the temp repo and reported a real file as missing.
        named = Path(caught.exception.detail.split("scripts/")[-1].split()[0].rstrip("."))
        self.assertTrue((REPO / "scripts" / named).is_file()
                        or (self.root / "scripts" / named).is_file(),
                        f"the refusal names scripts/{named}, which does not exist")

    def test_the_CI_DROP_cache_is_copied_when_present(self) -> None:
        self.unfake("stage_injector")   # the COPY is the behaviour under test
        ci_drop = self.root / "artifacts" / "ci-drop-into-game"
        ci_drop.mkdir(parents=True)
        (ci_drop / "FusionRpg.Injector.dll").write_text("prebuilt", encoding="utf-8")
        report = pp.publish(self.root, self.env(FUSIONRPG_USE_CI_DROP="1"), 5)
        self.assertIsNone(report.game_dir, "the CI drop means there is no reference tree to resolve")
        self.assertIn("drop-bep", report.stages)

    def test_a_GAME_DIR_that_is_not_a_directory_is_REFUSED_rather_than_ignored(self) -> None:
        """The original's `Test-Path $env:FUSIONRPG_GAME_DIR` fell through to the parent probe when the
        configured path did not exist, so a typo'd game dir silently measured against a different tree."""
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_game_dir(self.root, {"FUSIONRPG_GAME_DIR": str(self.root / "nope")})
        self.assertEqual(caught.exception.reason, "GAME-DIR-MISSING")

    def test_no_reference_tree_at_all_names_EVERY_way_out(self) -> None:
        """A developer hitting this has three options, and guessing which is available costs the most
        time. The refusal also names the refs fetcher with the extension it HAS on disk."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "repo"
            root.mkdir()
            with self.assertRaises(pp.Refusal) as caught:
                pp.resolve_game_dir(root, {})
            self.assertEqual(caught.exception.reason, "NO-REFERENCE-TREE")
            for hint in ("FUSIONRPG_GAME_DIR", "FUSIONRPG_USE_CI_DROP"):
                self.assertIn(hint, caught.exception.detail, hint)
            # The detail reads "run gk-core/scripts/fetch_bepinex_refs.py", so the name is relative to
            # `scripts/`. Resolving it against the REPO ROOT reported a real file as missing -- a false
            # alarm from resolving a repo-relative path against the wrong base. This case is what noticed
            # when the fetcher was ported and the refusal text kept naming the retired `.ps1`.
            named = caught.exception.detail.split("run scripts/")[-1].split()[0]
            self.assertTrue((REPO / "scripts" / named).is_file() or (root / "scripts" / named).is_file(),
                            f"the refusal names scripts/{named}, which does not exist")

    def test_the_npm_stage_refuses_BEFORE_the_lockfile_is_trusted(self) -> None:
        self.unfake("stage_web", "which")   # the real stage is what reads the lockfile
        (self.root / "web" / "fusion-rpg-web" / "package-lock.json").unlink()
        with self.assertRaises(pp.Refusal) as caught:
            pp.stage_web(pp.Layout.build(self.root), 5)
        self.assertEqual(caught.exception.reason, "NO-LOCKFILE")

    def test_a_missing_PLAYERS_or_LICENSE_is_REFUSED_rather_than_shipped_without(self) -> None:
        (self.root / "LICENSE").unlink()
        with self.assertRaises(pp.Refusal) as caught:
            pp.stage_documents(pp.Layout.build(self.root))
        self.assertEqual(caught.exception.reason, "SOURCE-MISSING")

    def test_the_guard_the_MELON_stage_calls_EXISTS_under_the_name_the_stage_uses(self) -> None:
        """The port writes `guard-game-profile.py` because that is the live spelling -- the guard was
        ported WITHOUT a stem change, unlike this program's snake_case tools. A wrong-but-plausible name
        would survive until a player pack with a MelonLoader drop was actually built."""
        source = PUBLISH.read_text(encoding="utf-8")
        self.assertIn('"guard-game-profile.py"', source)
        # The guard is gk-fusion's, so the assertion asks its OWNER for it. REPO is gk-core, which has
        # no scripts/guard-game-profile.py at all - so the test that exists to catch a wrong-but-plausible
        # guard name could not run, which is the one failure this class of name typo produces. The tool
        # itself was already right: publish_player.py resolves the guard through its stage layout root
        # rather than through REPO, so only this assertion was still spelling the pre-split path.
        self.assertIsNotNone(
            owning_base("scripts/guard-game-profile.py", REPO),
            "no repository carries scripts/guard-game-profile.py, so the name the stage calls is wrong")


# ------------------------------------------------------------------------------------------------
# The caller
# ------------------------------------------------------------------------------------------------

class TheCaller(TemporaryRepo):
    def test_the_caller_forwards_the_VERSION_and_REMOVES_the_CI_DROP_flag(self) -> None:
        """It exists to BUILD the injector, so a leftover FUSIONRPG_USE_CI_DROP=1 would make the publisher
        copy the very cache this script is about to overwrite."""
        seen: dict = {}

        def fake_publish(root, env, timeout):
            seen.update(env)
            drop = root / "dist" / "FusionRpg" / "DropIntoGame"
            drop.mkdir(parents=True, exist_ok=True)
            (drop / "FusionRpg.Injector.dll").write_text("dll", encoding="utf-8")
            (drop / "FusionRpg.Injector.pdb").write_text("pdb", encoding="utf-8")
            (drop / "notes.txt").write_text("txt", encoding="utf-8")
            return object()

        env = self.env(FUSIONRPG_VERSION="v9.9.9", FUSIONRPG_USE_CI_DROP="1")
        with mock.patch.object(sc.publish_player, "publish", side_effect=fake_publish):
            report = sc.sync(self.root, env, 5)
        self.assertNotIn("FUSIONRPG_USE_CI_DROP", seen)
        self.assertEqual(seen["FUSIONRPG_VERSION"], "9.9.9")
        self.assertEqual(report.version, "9.9.9")

    def test_a_DROP_OLDER_than_the_publish_is_REFUSED_rather_than_cached(self) -> None:
        """THE CHECK THE ORIGINAL LACKED. Verifying the artefact EXISTS is not enough: a previous run's
        drop satisfies that too, so a publish that produced nothing would refresh the cache from the old
        files and report success. Comparing against when the publish STARTED is what makes it mean
        'this publish produced it'."""
        def stale_publish(root, env, timeout):
            drop = root / "dist" / "FusionRpg" / "DropIntoGame"
            drop.mkdir(parents=True, exist_ok=True)
            old = time_origin() - 3600
            target = drop / "FusionRpg.Injector.dll"
            target.write_text("old", encoding="utf-8")
            os.utime(target, (old, old))
            return object()

        with mock.patch.object(sc.publish_player, "publish", side_effect=stale_publish):
            with self.assertRaises(sc.Refusal) as caught:
                sc.sync(self.root, self.env(), 5)
        self.assertEqual(caught.exception.reason, "DROP-STALE")
        self.assertFalse((self.root / "artifacts" / "ci-drop-into-game").exists(),
                         "a stale drop must not be cached, and the cache must not be half-written")

    def test_a_MISSING_drop_is_REFUSED(self) -> None:
        with mock.patch.object(sc.publish_player, "publish", return_value=object()):
            with self.assertRaises(sc.Refusal) as caught:
                sc.sync(self.root, self.env(), 5)
        self.assertEqual(caught.exception.reason, "DROP-MISSING")

    def test_the_cache_holds_ONLY_dll_and_json_and_ships_a_README(self) -> None:
        def fake_publish(root, env, timeout):
            drop = root / "dist" / "FusionRpg" / "DropIntoGame"
            drop.mkdir(parents=True, exist_ok=True)
            for name in ("FusionRpg.Injector.dll", "plugin.json", "x.pdb", "notes.txt"):
                (drop / name).write_text("x", encoding="utf-8")
            return object()

        with mock.patch.object(sc.publish_player, "publish", side_effect=fake_publish):
            report = sc.sync(self.root, self.env(), 5)
        dest = self.root / "artifacts" / "ci-drop-into-game"
        self.assertEqual(sorted(p.name for p in dest.iterdir()),
                         ["FusionRpg.Injector.dll", "README.md", "plugin.json"])
        self.assertEqual(report.cached, 2)
        # The README is the deliverable: it is what tells whoever finds this cache that game interop DLLs
        # must never land in it.
        self.assertIn("interop", (dest / "README.md").read_text(encoding="utf-8"))

    def test_a_FAILED_publish_is_not_a_cached_refresh(self) -> None:
        def failing(root, env, timeout):
            raise pp.Refusal("publish-server", "EXIT-NON-ZERO", "dotnet publish exited 1")
        with mock.patch.object(sc.publish_player, "publish", side_effect=failing):
            with self.assertRaises(pp.Refusal) as caught:
                sc.sync(self.root, self.env(), 5)
        self.assertEqual(caught.exception.reason, "EXIT-NON-ZERO")
        self.assertFalse((self.root / "artifacts" / "ci-drop-into-game").exists())

    def test_a_publishers_Refusal_surfaces_with_its_stage_and_reason(self) -> None:
        """The caller must not swallow the publisher's diagnosis -- a caller that reports "sync failed"
        when the real answer is "the injector build exited 1" wastes the reader's whole next step."""
        def failing(root, env, timeout):
            raise pp.Refusal("injector", "OUTPUT-MISSING", "no dll")
        out: list[str] = []
        err: list[str] = []
        # The `.write` patch, not a STRING substitution: replacing sys.stdout with a str makes
        # `print`'s own `.write()` fail, which is a crash in the harness rather than a captured report.
        patches = [mock.patch.object(sc.publish_player, "publish", side_effect=failing),
                   mock.patch.object(sys.stdout, "write", out.append),
                   mock.patch.object(sys.stderr, "write", err.append)]
        for patch in patches:
            patch.start()
        try:
            code = sc.main(["--root", str(self.root), "--json"])
        finally:
            for patch in patches:
                patch.stop()
        self.assertEqual(code, 1)
        self.assertEqual(json.loads("".join(out))["stage"], "injector")


def time_origin() -> float:
    import time
    return time.time()


# ------------------------------------------------------------------------------------------------
# Surface
# ------------------------------------------------------------------------------------------------

class Surface(unittest.TestCase):
    def test_the_flags_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(PUBLISH), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--timeout", "--json"):
            self.assertIn(flag, out, f"{flag} is not in --help, so no caller can find it")
        for flag in ("--root", "--timeout", "--json"):
            self.assertIn(flag, subprocess.run([sys.executable, str(SYNC), "--help"],
                                               capture_output=True, text=True,
                                               timeout=RUN_TIMEOUT).stdout, f"{flag} missing on the caller")

    def test_it_answers_no_PowerShell_spelled_flag(self) -> None:
        for script in (PUBLISH, SYNC):
            proc = subprocess.run([sys.executable, str(script), "-Root", "x"], capture_output=True,
                                 text=True, timeout=RUN_TIMEOUT)
            self.assertNotEqual(proc.returncode, 0, script)
            self.assertIn("unrecognized arguments", (proc.stderr + proc.stdout).lower(), script)

    def test_neither_tool_shells_out_to_a_PowerShell_interpreter(self) -> None:
        for script in (PUBLISH, SYNC):
            code = code_without_retired_dialect(script.read_text(encoding="utf-8"))
            for token in FORBIDDEN_DIALECT_TOKENS:
                self.assertNotIn(token, code, f"{script.name} still uses {token!r} in CODE")

    def test_every_PS1_either_tool_names_is_a_DECLARED_reference(self) -> None:
        import re
        for script in (PUBLISH, SYNC):
            found = set(re.findall(r"[\w.-]+\.ps1\b",
                                   code_without_retired_dialect(script.read_text(encoding="utf-8"))))
            self.assertTrue(found, f"{script.name} names no .ps1 at all, so the guard is not reading")
            self.assertEqual(found - set(DECLARED_PS1_REFERENCES), set(),
                             f"{script.name}: undeclared .ps1 reference(s) "
                             f"{sorted(found - set(DECLARED_PS1_REFERENCES))}")
        for name, reason in DECLARED_PS1_REFERENCES.items():
            self.assertTrue(reason.strip(), f"{name} is declared without a reason")

    def test_the_refusal_reasons_are_a_CLOSED_vocabulary(self) -> None:
        import re
        for script, known in (
            (PUBLISH, {"TIMEOUT", "NOT-ON-PATH", "SPAWN-FAILED", "EXIT-NON-ZERO", "BAD-VERSION",
                       "GAME-DIR-MISSING", "NO-REFERENCE-TREE", "DELETE-OUTSIDE-REPO", "NPM-REQUIRED",
                       "NO-LOCKFILE", "CI-DROP-INCOMPLETE", "NO-GAME-DIR", "OUTPUT-MISSING",
                       "SOURCE-MISSING", "SERVER-DATA-MISSING"}),
            (SYNC, {"DROP-MISSING", "DROP-STALE"}),
        ):
            found = set(re.findall(r'Refusal\(\s*"[a-z-]+",\s*"([A-Z][A-Z0-9-]+)"',
                                   script.read_text(encoding="utf-8")))
            self.assertTrue(found, f"{script.name}: no refusal reasons found at all")
            self.assertEqual(found - known, set(),
                             f"{script.name}: undocumented refusal reason(s) {sorted(found - known)}")

    def test_every_stage_name_in_the_report_is_from_the_CLOSED_vocabulary(self) -> None:
        self.assertTrue(STAGE_VOCABULARY >= set(pp.STAGES),
                        f"a declared stage is missing from the vocabulary: "
                        f"{sorted(set(pp.STAGES) - STAGE_VOCABULARY)}")

    def test_the_exit_codes_are_the_declared_vocabulary(self) -> None:
        self.assertEqual({pp.EXIT_OK, pp.EXIT_FAILED}, EXIT_VOCABULARY)
        self.assertEqual({sc.EXIT_OK, sc.EXIT_FAILED}, EXIT_VOCABULARY)

    def test_the_timeout_is_declared_on_BOTH_and_reachable(self) -> None:
        for script in (PUBLISH, SYNC):
            flat = " ".join(subprocess.run([sys.executable, str(script), "--help"], capture_output=True,
                                          text=True, timeout=RUN_TIMEOUT).stdout.split()).lower()
            self.assertIn("original had no", flat, script.name)
            self.assertIn("timeout", flat, script.name)


if __name__ == "__main__":
    unittest.main()
