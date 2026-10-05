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
import contextlib
import hashlib
import importlib.util
import io
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


def plant(root: Path, rel: str, text: str) -> Path:
    """Write `text` at `root/rel`, creating the parent directories.

    Every marker a case plants goes through here, because a marker that moved into a sub-directory --
    `src/FusionRpg.Launcher/...`, `docs/runbook/PLAYERS.txt` -- needs its parents, and a case that
    hand-rolled `write_text` raised FileNotFoundError instead of the refusal it was asserting.
    """
    target = root / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")
    return target


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
    """A throwaway repository with the files the pipeline reads, so no stage needs a real build.

    THE FOUR ROOTS ARE FOUR SEPARATE TREES, none of them under the repository. That is the shape the
    split produced: gk-web, gk-fusion and the workspace are all SIBLINGS of gk-core, so a fixture that
    recreated the retired `web/fusion-rpg-web`, `src/FusionRpg.Launcher` and `docs/runbook/PLAYERS.txt`
    under this repository would have kept the defect alive underneath every stage assertion in this
    file. They live beside the repository instead, so a case that forgets to pass one of them gets a
    NAMED REFUSAL rather than silently resolving against the real workspace -- the blindness the shared
    resolver's own docstring warns about, and the reason `guards` plant fixtures that carry their own
    roots.

    `Repo.layout()` never plants these under the repository, so a test that wants to know "does the tool
    still reach for `<repo>/src/FusionRpg.Launcher`?" can plant it there and watch the run refuse.
    """

    def __init__(self, root: Path) -> None:
        self.root = root
        self.web = self._sibling("web-repo") / "packages" / "fusion-rpg-web"
        self.fusion = self._sibling("fusion-repo")
        self.documents = self._sibling("documents-repo")
        self.web.mkdir(parents=True, exist_ok=True)
        self.fusion.mkdir(parents=True, exist_ok=True)
        self.documents.mkdir(parents=True, exist_ok=True)
        for rel in ("src/FusionRpg.Server/wwwroot", "scripts"):
            (root / rel).mkdir(parents=True, exist_ok=True)
        (self.web / "package.json").write_text("{}", encoding="utf-8")
        (self.web / "package-lock.json").write_text("{}", encoding="utf-8")
        self.write_fusion_markers()
        (self.documents / "docs" / "runbook").mkdir(parents=True, exist_ok=True)
        (self.documents / "docs" / "runbook" / "PLAYERS.txt").write_text("players", encoding="utf-8")
        (self.documents / "LICENSE").write_text("licence", encoding="utf-8")
        (self.documents / "NOTICE").write_text("notice", encoding="utf-8")

    def _sibling(self, name: str) -> Path:
        """A directory BESIDE the repository, never inside it. See the class docstring."""
        path = self.root.parent / (self.root.name + "-" + name)
        path.mkdir(parents=True, exist_ok=True)
        return path

    @staticmethod
    def catalog() -> str:
        """A profile catalog the LAUNCHER's own acceptance rule accepts: `profiles` non-empty, every row
        carrying an `id`. `GameProfileCatalog.LoadFromLauncherBase` requires `Count > 0`, which is why
        a fixture writing `{}` -- as the pre-split fixture did -- is no longer a catalog at all."""
        return json.dumps({"defaultProfileId": "pvzrh-3.8.1",
                           "profiles": [{"id": "pvzrh-3.8.1", "loaders": ["BepInEx"],
                                         "fingerprints": []}]})

    def write_fusion_markers(self, *, melon: bool = True) -> None:
        """Every file `required_fusion_markers` asks for in a run with a game dir and a MelonLoader pack.

        Written through the tool's OWN constants rather than spelled out here, so a marker that moves
        cannot leave the fixture describing the retired path -- which is exactly the rot the retired
        layout would have reintroduced silently.
        """
        for rel in (pp.LAUNCHER_PROJECT_REL, pp.LAUNCHER_MANIFEST_REL, pp.BEP_INJECTOR_PROJECT_REL,
                    *pp.MELON_INJECTOR_PROJECT_RELS, pp.FUSION_PROFILE_GUARD_REL):
            plant(self.fusion, rel, "{}")
        (self.fusion / pp.FUSION_CATALOG_REL).write_text(self.catalog(), encoding="utf-8")

    def server_root(self) -> Path:
        """A supplied server root. It must already exist, because the tool checks it before the build."""
        server = self.root / "dist" / "FusionRpg" / "Server"
        server.mkdir(parents=True, exist_ok=True)
        return server

    def roots(self) -> dict[str, Path]:
        """All FOUR explicit roots, as a caller supplies them."""
        return {"server_root": self.server_root(), "web_root": self.web,
                "fusion_root": self.fusion, "documents_root": self.documents}

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

    def publish(self, env: dict[str, str] | None = None, **overrides):
        """`publish()` with ALL FOUR roots supplied explicitly -- how every caller in this file invokes it.

        `env` is an ENV-OVERRIDE mapping merged over the fixture's, not a whole environment. The
        mechanical rewrite from the positional call sites dropped `self.env(FUSIONRPG_USE_CI_DROP="1")`
        on its way to `self.publish()`, which turned two CI-drop cases into ones that never took the
        CI-drop branch at all; an override dictionary is what keeps a case's environment readable at
        the call site instead of hidden in a helper.
        """
        roots = self.repo.roots()
        roots.update(overrides)
        return pp.publish(self.root, self.env(**(env or {})), 5, **roots)

    def layout(self, **overrides) -> "pp.Layout":
        """The layout a case is about, built from the fixture's explicit roots."""
        roots = self.repo.roots()
        roots.update(overrides)
        return pp.Layout.build(self.root, roots["server_root"], roots["web_root"],
                               roots["fusion_root"], roots["documents_root"])

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

    # -- the spawn resolves argv[0] ------------------------------------------------------------------------

    def test_the_SPAWN_resolves_argv_0_so_a_CMD_shim_is_executable(self) -> None:
        """THE DEFECT. `CreateProcess` does not search `PATHEXT` for a bare program name, so on an
        nvm-for-windows install `shutil.which("npm")` answers a `npm.CMD` and every publish refused at
        `web/NOT-ON-PATH` -- after a preflight that had already passed on that same answer.

        Measured on the machine this was written on, and the case asserts the RESOLUTION rather than
        re-measuring the platform: `subprocess.run(["npm", "--version"])` raised
        `FileNotFoundError: [WinError 2]`, while the resolved path returned `11.12.1`. The paths below
        are POSIX and therefore drive-letter-free, because a committed file may not carry one; the
        property under test is that `argv[0]` is REPLACED by whatever `which` answered, and that holds
        for any spelling.
        """
        shim = "/opt/node/bin/npm.CMD"
        with mock.patch.object(pp, "which", return_value=shim), \
                mock.patch.object(pp.subprocess, "run") as spawn:
            spawn.return_value = subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")
            pp.run(["npm", "ci"], REPO, 5, "web")
        self.assertEqual(spawn.call_args[0][0], [shim, "ci"],
                         "the program name was passed through unresolved, so a .CMD shim cannot spawn")

    def test_a_BARE_EXECUTABLE_that_resolves_to_ITSELF_is_UNCHANGED(self) -> None:
        """The fix must not change what it does for a program that was already spawnable -- an ELF `npm`
        on Linux, `dotnet` under `/usr/lib`. `which` answers the same string and the command line is
        byte-identical, so this is not merely 'close enough'."""
        for resolved in ("/usr/bin/npm", "/usr/lib/dotnet/dotnet", "/opt/dotnet/dotnet"):
            with self.subTest(resolved=resolved):
                with mock.patch.object(pp, "which", return_value=resolved), \
                        mock.patch.object(pp.subprocess, "run") as spawn:
                    spawn.return_value = subprocess.CompletedProcess(args=[], returncode=0, stdout="",
                                                                     stderr="")
                    pp.run(["npm", "ci"], REPO, 5, "web")
                self.assertEqual(spawn.call_args[0][0], [resolved, "ci"])

    def test_a_NAME_that_resolves_to_NOTHING_is_passed_through_so_NOT_ON_PATH_still_fires(self) -> None:
        """The other half of 'unchanged': when `which` answers None the argv must reach `subprocess`
        UNTOUCHED, or this change would have turned every missing-tool refusal into something else --
        a new refusal vocabulary is not a fix, it is a second failure mode."""
        with mock.patch.object(pp, "which", return_value=None), \
                mock.patch.object(pp.subprocess, "run") as spawn:
            spawn.side_effect = FileNotFoundError("nope")
            with self.assertRaises(pp.Refusal) as caught:
                pp.run(["npm", "ci"], REPO, 5, "web")
        self.assertEqual(caught.exception.reason, "NOT-ON-PATH")
        self.assertEqual(spawn.call_args[0][0], ["npm", "ci"])

    def test_the_REAL_npm_answer_on_THIS_machine_is_a_shim_and_it_now_SPAWNS(self) -> None:
        """THE EVIDENCE, ON THE MACHINE, NOT A MOCK. This is the acceptance case: `npm --version`
        through the tool's own choke point, with whatever `which` says here. It asserts the version is
        non-empty and the exit code is zero, so it is a spawn and not a refusal.

        Skipped, never failed, when npm is absent -- a tool that cannot find its own npm is a
        configuration fact, and this case is about the resolution, not about the installation.
        """
        resolved = pp.which("npm")
        if resolved is None:
            self.skipTest("npm is not on PATH here, so there is no shim resolution to observe")
        with mock.patch.object(pp.subprocess, "run") as spawn:
            spawn.side_effect = lambda argv, **kw: subprocess.CompletedProcess(
                args=argv, returncode=0, stdout="0.0.0-mock", stderr="")
            pp.run(["npm", "--version"], REPO, 60, "web")
        self.assertEqual(spawn.call_args[0][0], [resolved, "--version"])
        # And UNMOCKED, which is the part that can actually fail: the resolved path has to run.
        completed = pp.run(["npm", "--version"], REPO, 120, "web")
        self.assertRegex(completed.strip(), r"\A\d+\.\d+\.\d+", completed)

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
        the result. `VerificationTopologyTests` asserts the same ordering repository-side.

        The literals are the RESOLVED-program forms (`[npm, "ci"]`), which is what the code says now that
        `stage_web` passes `which`'s answer instead of the bare string -- otherwise this case would pass
        by finding nothing, because `str.index` on a missing substring raises rather than skipping."""
        source = PUBLISH.read_text(encoding="utf-8")
        self.assertLess(source.index('run([npm, "ci"]'), source.index('run([npm, "run", "build"]'))

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
        report = self.publish()
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
                # BOTH markers `verify-pack` requires. The exe is checked first and the content tree
                # second, so a fake that wrote only `data` would now report the exe -- which is the point
                # of the ordering, asserted here rather than in prose.
                (layout.server_out / pp.SERVER_PUBLISH_EXE).write_text("exe", encoding="utf-8")
                (layout.server_out / pp.SERVER_CONTENT_DIR).mkdir(parents=True, exist_ok=True)
            mock.patch.object(pp, "stage_publish_server",
                              side_effect=fake_server).start()
            roots = self.repo.roots()
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = pp.main(["--root", str(self.root), "--json", "--timeout", "5",
                                "--server-root", str(roots["server_root"]),
                                "--web-root", str(roots["web_root"]),
                                "--fusion-root", str(roots["fusion_root"]),
                                "--documents-root", str(roots["documents_root"])])
        self.assertEqual(code, 0, buffer.getvalue())
        report = json.loads(buffer.getvalue())
        self.assertEqual(set(report), {"tool", "verdict", "version", "server_root", "web_root",
                                       "fusion_root", "documents_root",
                                       "game_dir", "ci_drop", "melon_profile", "plugin_files",
                                       "melon_files", "pdbs_removed", "server_data_present",
                                       "stages", "out"})
        # The resolved roots are IN the envelope, not merely implied by it: a consumer of this JSON has to
        # be able to answer "which tree was this pack built from", and `dist/FusionRpg` in a log line is
        # not an answer a program can parse. ALL FOUR, because a pack is built from four repositories and
        # a consumer that cannot name three of them cannot tell which tree a marker came from.
        self.assertEqual(report["server_root"], str(roots["server_root"].resolve()))
        self.assertEqual(report["web_root"], str(roots["web_root"].resolve()))
        self.assertEqual(report["fusion_root"], str(roots["fusion_root"].resolve()))
        self.assertEqual(report["documents_root"], str(roots["documents_root"].resolve()))
        for banned in ("duration", "elapsed", "timestamp", "started", "finished", "seed"):
            self.assertNotIn(banned, report, f"{banned!r} differs per run, so nothing can assert on it")


class TheLayout(TemporaryRepo):
    def test_every_path_is_resolved_ONCE_into_one_Layout(self) -> None:
        """The original re-derived `$Out`, `$Drop` and friends inline at eleven points, which is why the
        Drop shape could not be asserted without a build. Resolved once, it can."""
        layout = self.layout()
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
        layout = self.layout()
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
        layout = self.layout()
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
        # The stage a reset names is the stage that ran. It used to be hardcoded `web`, including for the
        # two resets that are not the web stage at all.
        with self.assertRaises(pp.Refusal) as caught:
            pp.reset_directory(outside, root, "reset-output")
        self.assertEqual(caught.exception.stage, "reset-output")


# ------------------------------------------------------------------------------------------------
# The explicit roots
# ------------------------------------------------------------------------------------------------

#: The cases the mutation control runs, as node-id SUFFIXES. An explicit list rather than `-k`, for two
#: reasons: a `-k` on the class holding the resolution cases would also match the control, so a control
#: that re-runs itself under a mutant recurses until the machine gives up; and a `-k` substring match is a
#: second thing that can silently select nothing.
ROOT_RESOLUTION_NODES = (
    "TheExplicitRoots::test_BOTH_roots_supplied_EXPLICITLY_are_RESOLVED_as_given",
    "TheExplicitRoots::test_ALL_FOUR_roots_supplied_EXPLICITLY_are_RESOLVED_as_given",
    "TheExplicitRoots::test_an_EXPLICIT_root_WINS_over_a_DEFAULT_that_would_also_resolve",
    "TheExplicitRoots::test_a_SERVER_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path",
    "TheExplicitRoots::test_a_SERVER_ROOT_OUTSIDE_the_repository_REFUSES_rather_than_being_deleted",
    "TheExplicitRoots::test_a_WEB_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path",
    "TheExplicitRoots::test_a_WEB_ROOT_without_BOTH_npm_MARKS_REFUSES_rather_than_being_used",
    "TheExplicitRoots::test_a_WEB_ROOT_with_BOTH_npm_MARKS_is_ACCEPTED",
    "TheExplicitRoots::test_the_DEFAULT_web_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot",
    "TheExplicitRoots::test_a_FUSION_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path",
    "TheExplicitRoots::test_a_FUSION_ROOT_missing_ANY_marker_this_run_reads_REFUSES_by_name",
    "TheExplicitRoots::test_a_FUSION_ROOT_with_ALL_of_this_runs_markers_is_ACCEPTED",
    "TheExplicitRoots::test_the_CI_DROP_does_NOT_require_the_BepInEx_HOST_PROJECT",
    "TheExplicitRoots::test_a_MELON_PACK_does_NOT_require_the_Melon_HOSTS_without_a_melon_drop",
    "TheExplicitRoots::test_the_RETIRED_REPO_RELATIVE_layout_REFUSES_rather_than_being_used",
    "TheExplicitRoots::test_the_FUSION_and_DOCUMENTS_roots_are_read_from_the_FLAGS_and_not_from_the_REPOSITORY",
    "TheExplicitRoots::test_a_PRESENT_but_EMPTY_catalog_REFUSES_rather_than_shipping_a_silent_fallback",
    "TheExplicitRoots::test_a_DOCUMENTS_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path",
    "TheExplicitRoots::test_a_DOCUMENTS_ROOT_missing_EITHER_marker_REFUSES_by_name",
    "TheExplicitRoots::test_a_DOCUMENTS_ROOT_with_BOTH_markers_is_ACCEPTED",
    "TheExplicitRoots::test_the_DEFAULT_fusion_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot",
    "TheExplicitRoots::test_the_DEFAULT_documents_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot",
    "TheExplicitRoots::test_a_SUPPLIED_server_ROOT_left_over_from_a_previous_publish_is_EMPTIED",
    "TheExplicitRoots::test_MAIN_exits_NON_ZERO_on_a_root_REFUSAL_and_never_prints_an_empty_success",
    "TheExplicitRoots::test_the_json_envelope_is_PARSEABLE_and_carries_the_ROOTS_on_a_REFUSAL",
    "TheExplicitRoots::test_a_REFUSAL_envelope_names_a_ROOT_that_DID_resolve",
    "TheExplicitRoots::test_the_RETIRED_MONOREPO_DERIVATION_is_not_reachable_from_the_code",
)

#: The cases that MUST go red under the mutant. Asserting the specific names rather than "somebody
#: failed" is what makes the control a control: a non-zero exit from an unrelated failure -- a typo, an
#: import error, a fixture that stopped building -- would otherwise be read as the mutant being caught.
MUST_GO_RED = (
    "test_BOTH_roots_supplied_EXPLICITLY_are_RESOLVED_as_given",
    "test_an_EXPLICIT_root_WINS_over_a_DEFAULT_that_would_also_resolve",
    "test_a_WEB_ROOT_with_BOTH_npm_MARKS_is_ACCEPTED",
    "test_the_RETIRED_MONOREPO_DERIVATION_is_not_reachable_from_the_code",
)


def run_resolution_cases(cwd: Path) -> subprocess.CompletedProcess:
    """The resolution cases in a FRESH interpreter, which is what makes the mutant observable at all.

    Two controls against a stale-bytecode pass, because this tool is imported by path
    (`spec_from_file_location`) and CPython validates a cached `.pyc` against the source's mtime and
    SIZE -- a mutant installed inside one filesystem-mtime tick, with an unchanged byte count, can be
    read out of the cache while the file on disk says otherwise. So: `PYTHONDONTWRITEBYTECODE`, `-B`, and
    any cached bytecode for this module deleted first. A control that passes because it read the OLD
    module is the exact failure this programme has already produced twice.
    """
    env = dict(os.environ)
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    for stale in sorted((cwd / "scripts" / "__pycache__").glob("publish_player*.pyc")):
        stale.unlink()
    nodes = [f"tests/tools/test_publish_player.py::{node}" for node in ROOT_RESOLUTION_NODES]
    return subprocess.run([sys.executable, "-B", "-m", "pytest", "-q", "--no-header", "-p",
                           "no:cacheprovider", *nodes],
                          cwd=str(cwd), capture_output=True, text=True, timeout=RUN_TIMEOUT, env=env)


class TheExplicitRoots(TemporaryRepo):
    """The two roots as INPUTS, because the pre-split tool could not be run at all after the split.

    Every case here states a root and asks what the tool did with it. The fixture's web package lives at
    `packages/fusion-rpg-web` rather than `web/fusion-rpg-web`, because a fixture that recreated the
    retired shape would have kept the defect alive underneath every assertion in this file.
    """

    # -- both roots supplied -----------------------------------------------------------------------------

    def test_BOTH_roots_supplied_EXPLICITLY_are_RESOLVED_as_given(self) -> None:
        roots = self.repo.roots()
        self.assertEqual(pp.resolve_server_root(self.root, roots["server_root"]),
                         roots["server_root"].resolve())
        self.assertEqual(pp.resolve_web_root(self.root, roots["web_root"]),
                         roots["web_root"].resolve())
        # And they survive all the way into the report, so a machine consumer can read which tree was
        # used without parsing a log line.
        report = self.publish()
        self.assertEqual(report.server_root, str(roots["server_root"].resolve()))
        self.assertEqual(report.web_root, str(roots["web_root"].resolve()))
        self.assertIn(pp.STAGE_RESOLVE_ROOTS, report.stages)

    def test_ALL_FOUR_roots_supplied_EXPLICITLY_are_RESOLVED_as_given(self) -> None:
        """THE FIVE PATHS THE SPLIT MOVED, ONTO FOUR ROOTS, EACH VALIDATED BY ITS OWN MARKER.

        This is the case that says what the other two do not: that a run can be handed every tree it
        needs and reach a pack. The marker column is what each root is validated by, and every marker is
        the artefact the CONSUMER reads rather than a name that happens to match a directory.

          server     gk-core    no pre-flight marker; after the publish FusionRpg.Server.exe + data/
          web        gk-web     package.json AND package-lock.json
          fusion     gk-fusion  src/FusionRpg.Launcher/FusionRpg.Launcher.csproj
                                src/FusionRpg.Launcher/loader-manifest.json
                                game-profiles.json (schema-checked)
                                src/FusionRpg.Injector.BepInEx/...csproj   (no CI drop)
                                the two MelonLoader hosts + scripts/guard-game-profile.py (Melon drop)
          documents  workspace  docs/runbook/PLAYERS.txt AND LICENSE
        """
        roots = self.repo.roots()
        self.assertEqual(pp.resolve_fusion_root(self.root, roots["fusion_root"], self.env()),
                         roots["fusion_root"].resolve())
        self.assertEqual(pp.resolve_documents_root(self.root, roots["documents_root"]),
                         roots["documents_root"].resolve())
        report = self.publish()
        self.assertEqual(report.fusion_root, str(roots["fusion_root"].resolve()))
        self.assertEqual(report.documents_root, str(roots["documents_root"].resolve()))
        self.assertIn(pp.STAGE_RESOLVE_ROOTS, report.stages)
        # And the four roots are FOUR DIFFERENT DIRECTORIES in the fixture, so this case cannot pass by
        # one tree quietly answering for all four.
        self.assertEqual(len({report.server_root, report.web_root, report.fusion_root,
                              report.documents_root}), 4)

    def test_an_EXPLICIT_root_WINS_over_a_DEFAULT_that_would_also_resolve(self) -> None:
        """THE RULE THE WHOLE DEFECT TURNED ON: a default that resolves and an explicit value that
        resolves are both available, and the explicit one is the one that must be used.

        The default is made genuinely resolvable here -- `KEEPVERSE_WEB_ROOT` is the override the shared
        resolver honours, pointed at a directory that really is an npm package -- so a tool that quietly
        preferred the default would find a working web root and pass every other check in this file.
        """
        default_web = self.root / "default-web"
        (default_web / "packages").mkdir(parents=True)
        (default_web / "packages" / "fusion-rpg-web").mkdir()
        for marker in pp.WEB_PACKAGE_MARKERS:
            (default_web / "packages" / "fusion-rpg-web" / marker).write_text("{}", encoding="utf-8")
        with mock.patch.dict(os.environ, {"KEEPVERSE_WEB_ROOT": str(default_web)}, clear=False):
            report = self.publish()
        self.assertEqual(report.web_root, str(self.repo.web.resolve()),
                         "the explicit --web-root lost to a resolvable default")

    # -- the server root refuses --------------------------------------------------------------------------

    def test_a_SERVER_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path(self) -> None:
        absent = self.root / "dist" / "FusionRpg" / "NoSuchServer"
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_server_root(self.root, absent)
        refusal = caught.exception
        self.assertEqual(refusal.stage, pp.STAGE_RESOLVE_ROOTS)
        self.assertEqual(refusal.reason, "SERVER-ROOT-MISSING")
        self.assertIn(str(absent.resolve()), refusal.detail)

    def test_a_SERVER_ROOT_OUTSIDE_the_repository_REFUSES_rather_than_being_deleted(self) -> None:
        """The root is emptied before every publish, so accepting one outside the repository would mean a
        recursive delete of a path the caller merely named. Refused at RESOLUTION, before any deletion."""
        with tempfile.TemporaryDirectory() as tmp:
            outside = Path(tmp) / "not-the-repo"
            outside.mkdir()
            with self.assertRaises(pp.Refusal) as caught:
                pp.resolve_server_root(self.root, outside)
            self.assertEqual(caught.exception.reason, "SERVER-ROOT-OUTSIDE-REPO")
            self.assertIn(str(outside.resolve()), caught.exception.detail)

    def test_a_SUPPLIED_server_ROOT_left_over_from_a_previous_publish_is_EMPTIED(self) -> None:
        """THE FAIL-OPEN THIS CLOSED. A server root outside the pack tree keeps its previous publish, so a
        publish that produced nothing would still satisfy both `verify-pack` markers and the run would
        report a pack it never built. The root is emptied, so the markers describe THIS run."""
        stale = self.root / "dist" / "stale-server"
        stale.mkdir(parents=True)
        (stale / pp.SERVER_PUBLISH_EXE).write_text("last week's build", encoding="utf-8")
        (stale / pp.SERVER_CONTENT_DIR).mkdir()
        report = self.publish(server_root=stale)
        self.assertEqual(report.server_root, str(stale.resolve()))
        self.assertEqual((stale / pp.SERVER_PUBLISH_EXE).read_text(encoding="utf-8"), "exe")

    # -- the web root refuses -----------------------------------------------------------------------------

    def test_a_WEB_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path(self) -> None:
        absent = self.root / "packages" / "no-such-web"
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_web_root(self.root, absent)
        refusal = caught.exception
        self.assertEqual(refusal.stage, pp.STAGE_RESOLVE_ROOTS)
        self.assertEqual(refusal.reason, "WEB-ROOT-MISSING")
        self.assertIn(str(absent.resolve()), refusal.detail)

    def test_a_WEB_ROOT_without_BOTH_npm_MARKS_REFUSES_rather_than_being_used(self) -> None:
        """Existence is not validity. Both marks are required, and the refusal names which one is absent
        rather than stating a requirement and leaving the reader to work out what failed."""
        for missing in pp.WEB_PACKAGE_MARKERS:
            with self.subTest(missing=missing):
                bare = self.root / "packages" / f"bare-{missing}"
                bare.mkdir(parents=True)
                for marker in pp.WEB_PACKAGE_MARKERS:
                    if marker != missing:
                        (bare / marker).write_text("{}", encoding="utf-8")
                with self.assertRaises(pp.Refusal) as caught:
                    pp.resolve_web_root(self.root, bare)
                self.assertEqual(caught.exception.reason, "WEB-ROOT-NOT-A-PACKAGE")
                self.assertEqual(caught.exception.stage, pp.STAGE_RESOLVE_ROOTS)
                self.assertIn(missing, caught.exception.detail)
                self.assertIn(str(bare.resolve()), caught.exception.detail)

    def test_a_WEB_ROOT_with_BOTH_npm_MARKS_is_ACCEPTED(self) -> None:
        """The positive twin of the case above. Without it, a resolver that refused EVERY web root would
        pass the refusal case for entirely the wrong reason."""
        self.assertEqual(pp.resolve_web_root(self.root, self.repo.web), self.repo.web.resolve())

    def test_the_DEFAULT_web_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot(self) -> None:
        """One rule, in one sentence, and it is the repository's own: gk-web is a SIBLING of gk-core, so
        the default asks `keepverse_roots.web_root()` -- which honours `KEEPVERSE_WEB_ROOT` and refuses
        rather than guessing -- and the refusal NAMES every way to make it work."""
        with tempfile.TemporaryDirectory() as tmp:
            orphan = Path(tmp) / "orphan" / "repo"
            orphan.mkdir(parents=True)
            with self.assertRaises(pp.Refusal) as caught:
                pp.default_web_root(orphan)
            self.assertEqual(caught.exception.reason, "WEB-ROOT-UNRESOLVED")
            for hint in ("--web-root", "KEEPVERSE_WEB_ROOT"):
                self.assertIn(hint, caught.exception.detail, hint)
        # And it agrees with the resolver, rather than being a second implementation of it.
        self.assertEqual(pp.default_web_root(REPO), pp.shared_web_root(REPO) / "web" / "fusion-rpg-web")

    # -- the fusion root refuses --------------------------------------------------------------------------

    def test_a_FUSION_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path(self) -> None:
        absent = self.root.parent / (self.root.name + "-no-such-fusion")
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_fusion_root(self.root, absent, self.env())
        refusal = caught.exception
        self.assertEqual(refusal.stage, pp.STAGE_RESOLVE_ROOTS)
        self.assertEqual(refusal.reason, "FUSION-ROOT-MISSING")
        self.assertIn(str(absent.resolve()), refusal.detail)

    def test_a_FUSION_ROOT_missing_ANY_marker_this_run_reads_REFUSES_by_name(self) -> None:
        """Existence is not validity, one marker at a time, and the refusal names WHICH one is absent
        rather than stating a requirement and leaving the reader to work out what failed.

        Driven off the tool's own marker list, so a marker that moves cannot leave this case asserting a
        requirement the tool no longer has.
        """
        for missing in pp.required_fusion_markers(self.env()):
            with self.subTest(missing=missing):
                bare = self.root.parent / (self.root.name + "-bare-" + missing.replace("/", "-"))
                bare.mkdir(parents=True, exist_ok=True)
                for rel in pp.required_fusion_markers(self.env()):
                    if rel == missing:
                        continue
                    plant(bare, rel, self.repo.catalog() if rel == pp.FUSION_CATALOG_REL else "{}")
                with self.assertRaises(pp.Refusal) as caught:
                    pp.resolve_fusion_root(self.root, bare, self.env())
                self.assertEqual(caught.exception.reason, "FUSION-ROOT-NOT-A-SOURCE-TREE")
                self.assertEqual(caught.exception.stage, pp.STAGE_RESOLVE_ROOTS)
                self.assertIn(missing, caught.exception.detail)
                self.assertIn(str(bare.resolve()), caught.exception.detail)

    def test_a_FUSION_ROOT_with_ALL_of_this_runs_markers_is_ACCEPTED(self) -> None:
        """The positive twin of the case above. Without it, a resolver that refused EVERY fusion root
        would pass the refusal case for entirely the wrong reason."""
        self.assertEqual(pp.resolve_fusion_root(self.root, self.repo.fusion, self.env()),
                         self.repo.fusion.resolve())

    def test_the_CI_DROP_does_NOT_require_the_BepInEx_HOST_PROJECT(self) -> None:
        """A MARKER THE RUN WILL NEVER READ MUST NOT BE REQUIRED. Under `FUSIONRPG_USE_CI_DROP=1` the
        injector is copied prebuilt, so the BepInEx host project is not opened -- and refusing a CI
        publish for a file it does not touch is a false refusal, which is as wrong as a silent one and
        harder to argue with because it looks cautious."""
        env = self.env(FUSIONRPG_USE_CI_DROP="1")
        self.assertNotIn(pp.BEP_INJECTOR_PROJECT_REL, pp.required_fusion_markers(env))
        (self.repo.fusion / pp.BEP_INJECTOR_PROJECT_REL).unlink()
        self.assertEqual(pp.resolve_fusion_root(self.root, self.repo.fusion, env),
                         self.repo.fusion.resolve(),
                         "a CI publish refused a fusion root it would never have read")
        # And the reverse: without the drop it IS required, or a real build would find out at MSBuild.
        self.assertIn(pp.BEP_INJECTOR_PROJECT_REL, pp.required_fusion_markers(self.env()))
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_fusion_root(self.root, self.repo.fusion, self.env())
        self.assertEqual(caught.exception.reason, "FUSION-ROOT-NOT-A-SOURCE-TREE")

    def test_a_MELON_PACK_does_NOT_require_the_Melon_HOSTS_without_a_melon_drop(self) -> None:
        """The twin of the case above for the MelonLoader half: the hosts and the profile guard join the
        marker set only when `melon_is_available` says a Melon drop will be built, which is the same
        predicate `stage_melon` branches on."""
        self.assertIn(pp.FUSION_PROFILE_GUARD_REL, pp.required_fusion_markers(
            self.env(FUSIONRPG_ML_GAMEDIR=str(self.repo.melon_dir()))))
        self.assertNotIn(pp.FUSION_PROFILE_GUARD_REL, pp.required_fusion_markers(self.env()))

    def test_the_RETIRED_REPO_RELATIVE_layout_REFUSES_rather_than_being_used(self) -> None:
        """THE DEFECT, PLANTED WHERE THE TOOL USED TO LOOK.

        Every path the split moved is planted INSIDE the fixture repository, exactly where the
        pre-split tool read them from -- and the run still refuses, because the roots were not supplied.
        A fixture that never planted them would not know that: every other case hands over a valid root,
        so a derivation that reached back into `<repo>/src/FusionRpg.Launcher` would simply never be
        exercised, and the case would pass for the wrong reason.

        The refusal names the missing root rather than the file, which is the whole difference between
        "here is what to pass" and "this pack needs a lockfile".
        """
        for rel in ("src/FusionRpg.Launcher/FusionRpg.Launcher.csproj",
                    "src/FusionRpg.Launcher/loader-manifest.json",
                    "src/FusionRpg.Injector.BepInEx/FusionRpg.Injector.BepInEx.csproj",
                    "src/FusionRpg.Injector.MelonLoader/FusionRpg.Injector.MelonLoader.csproj",
                    "src/FusionRpg.Injector.MelonLoader.39/FusionRpg.Injector.MelonLoader.39.csproj",
                    "scripts/guard-game-profile.py", "game-profiles.json", "docs/runbook/PLAYERS.txt"):
            plant(self.root, rel, "{}" if rel.endswith(".json") else "// decoy")
        plant(self.root, "LICENSE", "decoy licence")
        # Server and web are supplied; the two roots that no longer exist in this repository are NOT.
        # The fixture's own siblings are visible to the shared resolvers, so the run must resolve
        # FUSION/ documents rather than reach back into the decoys.
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = pp.main(["--root", str(self.root), "--timeout", "5",
                            "--server-root", str(self.repo.server_root()),
                            "--web-root", str(self.repo.web)])
        # `Repo` planted the siblings, and the shared resolver finds gk-fusion/workspace only in a real
        # workspace -- so from a temp tree BOTH defaults refuse. Which one refuses first is the fusion
        # root, because resolution order is server, web, fusion, documents.
        self.assertEqual(code, pp.EXIT_FAILED)
        self.assertIn("FUSION-ROOT-UNRESOLVED", err.getvalue())
        self.assertEqual(out.getvalue(), "", "a refusal must not also print a success line")

    def test_the_FUSION_and_DOCUMENTS_roots_are_read_from_the_FLAGS_and_not_from_the_REPOSITORY(self) -> None:
        """The positive twin: with the same decoys planted and all FOUR roots supplied, the pack's
        documents and manifest come from the supplied trees. Without this, a resolver that quietly
        preferred `<repo>/...` would pass the refusal case above."""
        plant(self.root, "NOTICE", "DECOY")
        plant(self.root, "docs/runbook/PLAYERS.txt", "DECOY")
        plant(self.root, "src/FusionRpg.Launcher/loader-manifest.json", "DECOY")
        report = self.publish()
        out = self.root / "dist" / "FusionRpg"
        self.assertEqual((out / "NOTICE").read_text(encoding="utf-8"), "notice")
        self.assertEqual((out / "PLAYERS.txt").read_text(encoding="utf-8"), "players")
        self.assertEqual((out / "loader-manifest.json").read_text(encoding="utf-8"), "{}")
        self.assertEqual(report.fusion_root, str(self.repo.fusion.resolve()))
        self.assertEqual(report.documents_root, str(self.repo.documents.resolve()))

    # -- the documents root refuses -----------------------------------------------------------------------

    def test_a_DOCUMENTS_ROOT_that_is_not_a_directory_REFUSES_naming_the_stage_and_the_path(self) -> None:
        absent = self.root.parent / (self.root.name + "-no-such-documents")
        with self.assertRaises(pp.Refusal) as caught:
            pp.resolve_documents_root(self.root, absent)
        refusal = caught.exception
        self.assertEqual(refusal.stage, pp.STAGE_RESOLVE_ROOTS)
        self.assertEqual(refusal.reason, "DOCUMENTS-ROOT-MISSING")
        self.assertIn(str(absent.resolve()), refusal.detail)

    def test_a_DOCUMENTS_ROOT_missing_EITHER_marker_REFUSES_by_name(self) -> None:
        for missing in pp.DOCUMENTS_MARKERS:
            with self.subTest(missing=missing):
                bare = self.root.parent / (self.root.name + "-bare-docs-" + missing.replace("/", "-"))
                bare.mkdir(parents=True, exist_ok=True)
                for rel in pp.DOCUMENTS_MARKERS:
                    if rel != missing:
                        plant(bare, rel, "{}")
                with self.assertRaises(pp.Refusal) as caught:
                    pp.resolve_documents_root(self.root, bare)
                self.assertEqual(caught.exception.reason, "DOCUMENTS-ROOT-NOT-A-DOCUMENT-TREE")
                self.assertIn(missing, caught.exception.detail)
                self.assertIn(str(bare.resolve()), caught.exception.detail)

    def test_a_DOCUMENTS_ROOT_with_BOTH_markers_is_ACCEPTED(self) -> None:
        self.assertEqual(pp.resolve_documents_root(self.root, self.repo.documents),
                         self.repo.documents.resolve())

    # -- the defaults are the shared resolvers -------------------------------------------------------------

    def test_the_DEFAULT_fusion_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            orphan = Path(tmp) / "orphan" / "repo"
            orphan.mkdir(parents=True)
            with self.assertRaises(pp.Refusal) as caught:
                pp.default_fusion_root(orphan)
            self.assertEqual(caught.exception.reason, "FUSION-ROOT-UNRESOLVED")
            for hint in ("--fusion-root", "KEEPVERSE_FUSION_ROOT"):
                self.assertIn(hint, caught.exception.detail, hint)
        # And it agrees with the resolver, rather than being a second implementation of it.
        self.assertEqual(pp.default_fusion_root(REPO), pp.shared_fusion_root(REPO))

    def test_the_DEFAULT_documents_ROOT_is_the_SHARED_RESOLVERS_and_REFUSES_when_it_cannot(self) -> None:
        """`workspace_root()` RAISES rather than guessing when there is no workspace above this
        repository -- a standalone gk-core clone has no qualifying ancestor -- and a refusal that names
        the missing root is the answer. A synthesised parent is not."""
        with tempfile.TemporaryDirectory() as tmp:
            orphan = Path(tmp) / "orphan" / "repo"
            orphan.mkdir(parents=True)
            with self.assertRaises(pp.Refusal) as caught:
                pp.default_documents_root(orphan)
            self.assertEqual(caught.exception.reason, "DOCUMENTS-ROOT-UNRESOLVED")
            for hint in ("--documents-root", "KEEPVERSE_WORKSPACE_ROOT"):
                self.assertIn(hint, caught.exception.detail, hint)
        self.assertEqual(pp.default_documents_root(REPO), pp.shared_workspace_root(REPO))

    # -- the profile catalog is SCHEMA-CHECKED, not merely present -----------------------------------------

    def test_a_PRESENT_but_EMPTY_catalog_REFUSES_rather_than_shipping_a_silent_fallback(self) -> None:
        """THE CASE THAT A PRESENCE CHECK MISSES. `{"profiles": []}` is a file, so a marker that only
        asked for existence is satisfied -- and the launcher reads an empty `profiles` as "no catalog"
        and answers from a BUILT-IN table with no diagnostic, while `guard-game-profile.py` reports an
        uncatalogued profile as ALLOWED. The pack would then detect the wrong game version and nothing
        anywhere would have failed."""
        for label, text, expected in (
            ("empty profiles array", json.dumps({"profiles": []}), "EMPTY"),
            ("no profiles key", json.dumps({"defaultProfileId": "pvzrh-3.8.1"}), "no 'profiles' array"),
            ("profiles not an array", json.dumps({"profiles": {}}), "no 'profiles' array"),
            ("a row with no id", json.dumps({"profiles": [{"displayName": "x"}]}), "has no 'id'"),
            ("a row that is not an object", json.dumps({"profiles": ["pvzrh-3.8.1"]}), "is not an object"),
            ("not JSON", "{oh dear", "is not JSON"),
            ("not an object", json.dumps([1, 2, 3]), "is not a JSON object"),
        ):
            with self.subTest(label):
                (self.repo.fusion / pp.FUSION_CATALOG_REL).write_text(text, encoding="utf-8")
                problems = pp.profile_catalog_problems(self.repo.fusion / pp.FUSION_CATALOG_REL)
                self.assertTrue(problems, label)
                self.assertIn(expected, problems[0], label)
                with self.assertRaises(pp.Refusal) as caught:
                    pp.resolve_fusion_root(self.root, self.repo.fusion, self.env())
                self.assertEqual(caught.exception.reason, "PROFILE-CATALOG-INVALID", label)
        # And the live repository's own catalog passes, so the rule is not stricter than the real thing.
        live = owning_base(pp.FUSION_CATALOG_REL, REPO)
        self.assertIsNotNone(live, "no repository carries game-profiles.json, so the marker is wrong")
        self.assertEqual(pp.profile_catalog_problems(live / pp.FUSION_CATALOG_REL), [],
                         "the live catalog is rejected by the rule this change adds")

    def test_an_ABSENT_catalog_is_a_REFUSAL_not_an_absent_file(self) -> None:
        """Distinct from the schema cases because the refusal has to NAME IT: the launcher csproj copies
        `..\\..\\game-profiles.json` into the publish output, so a missing catalog is an MSBuild copy
        error two minutes in rather than a refusal in the first second."""
        (self.repo.fusion / pp.FUSION_CATALOG_REL).unlink()
        self.assertEqual(pp.profile_catalog_problems(self.repo.fusion / pp.FUSION_CATALOG_REL),
                         ["game-profiles.json is absent"])

    # -- the documents the pack ships now come from the documents root -------------------------------------

    def test_NOTICE_and_PLAYERS_come_from_the_DOCUMENTS_ROOT_and_not_the_repository(self) -> None:
        """`NOTICE` exists at the workspace root and at NO split repository, so before the documents root
        became an input every pack built since the split shipped without it and nothing said so -- the
        optional branch simply did not fire. This case plants the file ONLY in the documents root and
        asserts it reaches the pack, and plants a DECOY in the repository to prove the tool is not
        reading it from there."""
        decoy = self.root / "NOTICE"
        decoy.write_text("NOT THE ENGINE REPOSITORY'S NOTICE", encoding="utf-8")
        pp.stage_documents(self.layout())
        out = self.root / "dist" / "FusionRpg"
        self.assertEqual((out / "NOTICE").read_text(encoding="utf-8"), "notice")
        self.assertEqual((out / "PLAYERS.txt").read_text(encoding="utf-8"), "players")
        self.assertEqual((out / "LICENSE").read_text(encoding="utf-8"), "licence")

    def test_EVERY_root_REFUSAL_exits_NON_ZERO_and_writes_NOTHING_to_stdout(self) -> None:
        """ONE case for the whole refusal vocabulary, and the reason it is one case rather than eleven:
        "stdout is empty on a refusal" is a property of the ENTRY POINT, so asserting it per refusal
        would repeat one fact eleven times and let the tenth drift.

        Each entry is driven through `main()` -- not through a resolver -- because `main` is the only
        place that decides where a refusal's text goes, and a resolver that prints nothing says nothing
        about the tool.
        """
        good = self.repo.roots()
        cases: list[tuple[str, dict[str, object], str]] = [
            ("server root absent", {"server_root": self.root / "dist" / "FusionRpg" / "Nope"},
             "SERVER-ROOT-MISSING"),
            ("web root absent", {"web_root": self.root.parent / "no-such-web"}, "WEB-ROOT-MISSING"),
            ("fusion root absent", {"fusion_root": self.root.parent / "no-such-fusion"},
             "FUSION-ROOT-MISSING"),
            ("documents root absent", {"documents_root": self.root.parent / "no-such-docs"},
             "DOCUMENTS-ROOT-MISSING"),
        ]
        for label, override, reason in cases:
            with self.subTest(label):
                roots = dict(good) | override
                out, err = io.StringIO(), io.StringIO()
                with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                    code = pp.main(["--root", str(self.root), "--timeout", "5",
                                    "--server-root", str(roots["server_root"]),
                                    "--web-root", str(roots["web_root"]),
                                    "--fusion-root", str(roots["fusion_root"]),
                                    "--documents-root", str(roots["documents_root"])])
                self.assertEqual(code, pp.EXIT_FAILED, label)
                self.assertNotEqual(code, 0, label)
                self.assertIn(pp.STAGE_RESOLVE_ROOTS, err.getvalue(), label)
                self.assertIn(reason, err.getvalue(), label)
                self.assertEqual(out.getvalue(), "", f"{label}: a refusal wrote to stdout")
        # The marker refusals and the catalog refusal, through the same entry point.
        for missing in (pp.LAUNCHER_MANIFEST_REL, pp.FUSION_CATALOG_REL):
            with self.subTest(marker=missing):
                (self.repo.fusion / missing).unlink()
                try:
                    out, err = io.StringIO(), io.StringIO()
                    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                        code = pp.main(["--root", str(self.root), "--timeout", "5",
                                        "--server-root", str(good["server_root"]),
                                        "--web-root", str(good["web_root"]),
                                        "--fusion-root", str(good["fusion_root"]),
                                        "--documents-root", str(good["documents_root"])])
                    self.assertEqual(code, pp.EXIT_FAILED)
                    self.assertEqual(out.getvalue(), "", f"{missing}: a refusal wrote to stdout")
                    self.assertIn(missing, err.getvalue())
                finally:
                    self.repo.write_fusion_markers()
        # And the empty-catalog refusal, which is the one a presence check would have let through.
        catalog = self.repo.fusion / pp.FUSION_CATALOG_REL
        good_catalog = catalog.read_text(encoding="utf-8")
        try:
            catalog.write_text(json.dumps({"profiles": []}), encoding="utf-8")
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = pp.main(["--root", str(self.root), "--timeout", "5",
                                "--server-root", str(good["server_root"]),
                                "--web-root", str(good["web_root"]),
                                "--fusion-root", str(good["fusion_root"]),
                                "--documents-root", str(good["documents_root"])])
            self.assertEqual(code, pp.EXIT_FAILED)
            self.assertEqual(out.getvalue(), "", "PROFILE-CATALOG-INVALID wrote to stdout")
            self.assertIn("PROFILE-CATALOG-INVALID", err.getvalue())
        finally:
            catalog.write_text(good_catalog, encoding="utf-8")
        # `--json` is the other surface, and it is the one a CI step reads. The envelope is its output,
        # so "stdout is empty" does NOT apply there -- what applies is that it says REFUSED and carries
        # the stage and the reason, and never a `verdict: OK`.
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(io.StringIO()):
            code = pp.main(["--root", str(self.root), "--json", "--timeout", "5",
                            "--server-root", str(self.root / "dist" / "FusionRpg" / "Nope"),
                            "--web-root", str(good["web_root"]),
                            "--fusion-root", str(good["fusion_root"]),
                            "--documents-root", str(good["documents_root"])])
        self.assertEqual(code, pp.EXIT_FAILED)
        envelope = json.loads(buffer.getvalue())
        self.assertEqual(envelope["verdict"], "REFUSED")
        self.assertEqual(envelope["reason"], "SERVER-ROOT-MISSING")

    # -- the CLI surface ----------------------------------------------------------------------------------

    def test_MAIN_exits_NON_ZERO_on_a_root_REFUSAL_and_never_prints_an_empty_success(self) -> None:
        """A refusal has to be visible in the EXIT CODE, not only in the exception and the text. A tool
        that exits 0 having published nothing is the shape that ships."""
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = pp.main(["--root", str(self.root), "--timeout", "5",
                            "--server-root", str(self.root / "dist" / "FusionRpg" / "Nope"),
                            "--web-root", str(self.repo.web),
                            "--fusion-root", str(self.repo.fusion),
                            "--documents-root", str(self.repo.documents)])
        self.assertEqual(code, pp.EXIT_FAILED)
        self.assertNotEqual(code, 0)
        self.assertIn(pp.STAGE_RESOLVE_ROOTS, err.getvalue())
        self.assertIn("SERVER-ROOT-MISSING", err.getvalue())
        self.assertEqual(out.getvalue(), "", "a refusal must not also print a success line")

    def test_the_json_envelope_is_PARSEABLE_and_carries_the_ROOTS_on_a_REFUSAL(self) -> None:
        """The refused envelope has to be as machine-readable as the successful one, and it has to say how
        far the run got -- a consumer cannot otherwise tell a first-stage refusal from a last-stage one."""
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(io.StringIO()):
            code = pp.main(["--root", str(self.root), "--json", "--timeout", "5",
                            "--server-root", str(self.root / "dist" / "FusionRpg" / "Nope"),
                            "--web-root", str(self.repo.web),
                            "--fusion-root", str(self.repo.fusion),
                            "--documents-root", str(self.repo.documents)])
        self.assertEqual(code, pp.EXIT_FAILED)
        envelope = json.loads(buffer.getvalue())       # raises rather than returning None
        self.assertEqual(envelope["tool"], pp.TOOL_ID)
        self.assertEqual(envelope["verdict"], "REFUSED")
        self.assertEqual(envelope["stage"], pp.STAGE_RESOLVE_ROOTS)
        self.assertEqual(envelope["reason"], "SERVER-ROOT-MISSING")
        # A root that never resolved is `null`, NOT the default it would have been. A consumer reading a
        # refusal must never be handed a path the run did not use -- that is precisely what a silent
        # fallback looks like from the outside.
        self.assertIsNone(envelope["server_root"])
        self.assertIsNone(envelope["web_root"],
                          "the web root DID resolve before the server refusal was reached, so it must "
                          "be reported rather than dropped")
        self.assertEqual(envelope["stagesCompleted"], [],
                         "the first stage refusing is the honest answer: nothing completed")
        self.assertIn("dist", envelope["detail"].replace("\\", "/"))

    def test_a_REFUSAL_envelope_names_a_ROOT_that_DID_resolve(self) -> None:
        """The twin of the null case above, and the reason both exist: a refusal that happens AFTER one
        root resolved must say so, or the envelope cannot tell "nothing resolved" from "we never looked"."""
        with self.assertRaises(pp.Refusal) as caught:
            pp.publish(self.root, self.env(), 5,
                       server_root=self.repo.roots()["server_root"],
                       web_root=self.root / "packages" / "no-such-web")
        self.assertEqual(caught.exception.roots,
                         {"server": str(self.repo.server_root().resolve())},
                         "the server root resolved and must be reported; the web root never did")

    def test_the_RETIRED_MONOREPO_DERIVATION_is_not_reachable_from_the_code(self) -> None:
        """Asserted on the AST, not by grepping for a string. `"fusion-rpg-web"` has to remain in the
        tool -- it is the sub-directory UNDER gk-web -- so the check is on the OPERAND: no path in the code
        may be assembled as `<something>/"web"/"fusion-rpg-web"` rooted at the repository.

        THE FOUR MOVED PATHS ARE IN THE SAME SHAPE, and would be missed by a check that only knows about
        the web one. Each is asserted here as its own operand, because each was a live derivation before
        the split and each resolved to a path no repository has:
        `root/"src"/"FusionRpg.Launcher"`, `root/"game-profiles.json"`,
        `root/"docs"/"runbook"/"PLAYERS.txt"` and `root/"scripts"/"guard-game-profile.py"`.
        """
        offenders: list[str] = []
        # The OPERAND each retired derivation ended with, as a set of STRINGS. This began life as a set
        # of (parent, leaf) TUPLES compared against `node.right.value`, which is a string -- so the
        # membership test was false for every entry, the case collected zero offenders and passed under
        # the mutant, and the control caught it. Which is the whole reason the control exists: this was
        # a green assertion that could not have failed.
        retired_leaves = {"fusion-rpg-web", "FusionRpg.Launcher", "FusionRpg.Injector.BepInEx"}
        for node in ast.walk(ast.parse(PUBLISH.read_text(encoding="utf-8"))):
            if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Div) \
                    and isinstance(node.right, ast.Constant) and node.right.value in retired_leaves:
                base = node.left
                if isinstance(base, ast.BinOp) and isinstance(base.op, ast.Div):
                    base = base.left
                if isinstance(base, ast.Name) and base.id in ("root", "self"):
                    offenders.append(ast.unparse(node))
            # The single-segment ones: `root / "game-profiles.json"`, `root / "NOTICE"` and friends.
            if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Div) \
                    and isinstance(node.right, ast.Constant) \
                    and node.right.value in ("game-profiles.json", "NOTICE", "LICENSE",
                                             "docs", "scripts") \
                    and isinstance(node.left, ast.Name) and node.left.id in ("root", "self"):
                offenders.append(ast.unparse(node))
        self.assertEqual(offenders, [], f"a retired repo-relative derivation is reachable: {offenders}")
        # And the operands themselves are what the check reasons about -- asserted so a future edit that
        # re-spells the tuple mistake above cannot pass by changing the constants instead.
        self.assertEqual(sorted(retired_leaves),
                         ["FusionRpg.Injector.BepInEx", "FusionRpg.Launcher", "fusion-rpg-web"])
        # And `Layout.build` takes ALL FOUR roots as required parameters, so the retired spellings are not
        # recoverable from there either -- adding a derivation back would be a visible edit.
        build = [n for n in ast.walk(ast.parse(PUBLISH.read_text(encoding="utf-8")))
                 if isinstance(n, ast.FunctionDef) and n.name == "build"]
        self.assertEqual(len(build), 1)
        self.assertEqual([a.arg for a in build[0].args.args],
                         ["cls", "root", "server_root", "web_root", "fusion_root", "documents_root"])


class TheOldWalkIsGone(unittest.TestCase):
    """THE CONTROL. The retired monorepo walk is asserted GONE by a mutant, not by inspection.

    WHY A CONTROL AND NOT A READ. "The old code is not there" is a claim about a string, and a string is
    exactly what a wrong-but-plausible reimplementation leaves behind: a mutant that resolves the web root
    the old way while looking nothing like the original line still defeats a grep. So the mutant below
    puts the retired derivation BACK -- `root / "web" / "fusion-rpg-web"`, which is what the tool did
    before the split and which cannot resolve after it -- and requires specific named cases to go red.

    WHY IT IS A SEPARATE CLASS. `run_resolution_cases` names its cases explicitly, but a control living
    in the class it runs would be reachable by any future `-k TheExplicitRoots`, and a control that
    re-runs itself under a mutant recurses.

    THE MUTANT IS VERIFIED INSTALLED, THREE TIMES OVER, because a control that never installed its mutant
    reports the strongest possible false signal. Before believing the red: the bytes on disk hash
    differently from the bytes we saved; the mutant text is present in the file we are about to run; and
    the restore is confirmed by an EQUAL digest afterwards. The third is the one that catches a stale
    bytecode cache, because a run that read the unmutated module would be green under the mutant and the
    red assertion would have failed instead.
    """

    #: The exact text replaced. `Path(supplied).resolve()` -- the line that makes an explicit root win.
    ANCHOR = (
        "    if supplied is None:\n"
        "        candidate = default_web_root(root)\n"
        "    else:\n"
        "        candidate = Path(supplied).resolve()\n"
    )
    #: The retired walk, restored verbatim: the web root derived from the repository, with the supplied
    #: value discarded exactly as the pre-split tool discarded the sibling it had no way to reach.
    MUTANT = (
        "    if supplied is None:\n"
        "        candidate = default_web_root(root)\n"
        "    else:\n"
        '        candidate = root / "web" / "fusion-rpg-web"\n'
    )

    def test_a_mutant_RESTORING_the_old_WALK_is_CAUGHT_and_the_file_comes_back_byte_identically(self) -> None:
        original = PUBLISH.read_bytes()
        original_sha = hashlib.sha256(original).hexdigest()
        source = original.decode("utf-8")
        mutant_source = source.replace(self.ANCHOR, self.MUTANT, 1)
        self.assertNotEqual(mutant_source, source,
                            "the mutant did not install: the anchor text is no longer in the source")
        self.assertEqual(mutant_source.count(self.ANCHOR), 0,
                         "the anchor survived the replacement, so the mutant is only half installed")
        try:
            PUBLISH.write_text(mutant_source, encoding="utf-8", newline="")
            # VERIFICATION 1 -- the bytes on disk really changed.
            mutated_sha = hashlib.sha256(PUBLISH.read_bytes()).hexdigest()
            self.assertNotEqual(mutated_sha, original_sha,
                                "the mutant was written but the file on disk is unchanged")
            # VERIFICATION 2 -- the retired derivation is really in the file about to be run.
            on_disk = PUBLISH.read_text(encoding="utf-8")
            self.assertIn('candidate = root / "web" / "fusion-rpg-web"', on_disk,
                          "the mutant is not on disk; the run below would be measuring the ORIGINAL")
            proc = run_resolution_cases(REPO)
            self.assertNotEqual(proc.returncode, 0,
                                "THE MUTANT SURVIVED: the retired monorepo walk came back and every case "
                                "still passed.\n" + proc.stdout[-3000:])
            for name in MUST_GO_RED:
                self.assertIn(name, proc.stdout,
                              f"{name} stayed green under the mutant, so the control is not measuring "
                              f"what it claims to measure.\n{proc.stdout[-3000:]}")
        finally:
            # Byte-identical restore, from the bytes saved BEFORE the write -- not from a re-render, and
            # not from a `git checkout`, which would also have undone an unrelated edit in the same file.
            PUBLISH.write_bytes(original)
            restored_sha = hashlib.sha256(PUBLISH.read_bytes()).hexdigest()
            self.assertEqual(restored_sha, original_sha,
                             "the restore was not byte-identical; this file must not be left mutated")
        # VERIFICATION 3 -- the same cases are green again, which is what proves the red above came from
        # the mutant and not from something the restore happened to fix on the way out.
        green = run_resolution_cases(REPO)
        self.assertEqual(green.returncode, 0,
                         "the cases are still red after a byte-identical restore, so the red above was not "
                         "caused by the mutant.\n" + green.stdout[-3000:])


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
        mel = self.layout()
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
        mel = self.layout()
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
            self.publish()
        self.assertEqual(caught.exception.reason, "SERVER-DATA-MISSING")
        self.assertIn("codeFallback", caught.exception.detail)

    def test_the_CI_DROP_path_NEEDS_no_game_dir_and_REJECTS_an_incomplete_cache(self) -> None:
        self.unfake("stage_injector")   # the real stage is what checks the cache
        env = self.env(FUSIONRPG_USE_CI_DROP="1", FUSIONRPG_GAME_DIR="")
        with self.assertRaises(pp.Refusal) as caught:
            self.publish({"FUSIONRPG_USE_CI_DROP": "1", "FUSIONRPG_GAME_DIR": ""})
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
        report = self.publish({"FUSIONRPG_USE_CI_DROP": "1"})
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
        (self.repo.web / "package-lock.json").unlink()
        with self.assertRaises(pp.Refusal) as caught:
            pp.stage_web(self.layout(), 5)
        self.assertEqual(caught.exception.reason, "NO-LOCKFILE")

    def test_a_missing_PLAYERS_or_LICENSE_is_REFUSED_rather_than_shipped_without(self) -> None:
        """The LICENSE is read from the DOCUMENTS ROOT now, not from the engine repository -- the split
        moved it -- so the fixture plants it there and the case deletes it there. The assertion is
        unchanged: a pack never ships without one of the two."""
        (self.repo.documents / "LICENSE").unlink()
        with self.assertRaises(pp.Refusal) as caught:
            pp.stage_documents(self.layout())
        self.assertEqual(caught.exception.reason, "SOURCE-MISSING")

    def test_the_guard_the_MELON_stage_calls_EXISTS_under_the_name_the_stage_uses(self) -> None:
        """The port writes `guard-game-profile.py` because that is the live spelling -- the guard was
        ported WITHOUT a stem change, unlike this program's snake_case tools. A wrong-but-plausible name
        would survive until a player pack with a MelonLoader drop was actually built."""
        source = PUBLISH.read_text(encoding="utf-8")
        # The spelling now lives in ONE constant, `FUSION_PROFILE_GUARD_REL`, resolved against the FUSION
        # root rather than this repository -- so the assertion is on the CONSTANT the stage uses, not on a
        # quoted literal appearing somewhere in the file, which is a string a comment could satisfy.
        self.assertTrue(pp.FUSION_PROFILE_GUARD_REL.endswith("guard-game-profile.py"),
                        f"the guard is named {pp.FUSION_PROFILE_GUARD_REL!r}")
        self.assertIn("FUSION_PROFILE_GUARD_REL", source)
        # And the stage must reach it through the FUSION root, since gk-core has no such script at all.
        self.assertIn("layout.fusion / FUSION_PROFILE_GUARD_REL", source)
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
                       "SOURCE-MISSING", "SERVER-DATA-MISSING",
                       # The explicit-roots refusals. Every one of them names a path the run LOOKED at,
                       # so a reader is never told a requirement instead of the thing that failed it.
                       "SERVER-ROOT-MISSING", "SERVER-ROOT-OUTSIDE-REPO", "SERVER-EXE-MISSING",
                       "WEB-ROOT-MISSING", "WEB-ROOT-NOT-A-PACKAGE", "WEB-ROOT-UNRESOLVED",
                       "FUSION-ROOT-MISSING", "FUSION-ROOT-NOT-A-SOURCE-TREE", "FUSION-ROOT-UNRESOLVED",
                       "DOCUMENTS-ROOT-MISSING", "DOCUMENTS-ROOT-NOT-A-DOCUMENT-TREE",
                       "DOCUMENTS-ROOT-UNRESOLVED", "PROFILE-CATALOG-INVALID"}),
            (SYNC, {"DROP-MISSING", "DROP-STALE"}),
        ):
            # THE STAGE ARGUMENT MAY BE A CONSTANT. This pattern used to require a string literal for
            # the first argument, which meant every refusal raised as `Refusal(STAGE_RESOLVE_ROOTS,
            # "WEB-ROOT-MISSING", ...)` was INVISIBLE to it -- the six reasons added with the explicit
            # roots all passed this check without ever being read. A closed vocabulary that silently
            # ignores part of its own vocabulary is not a closed vocabulary.
            found = set(re.findall(r'Refusal\(\s*(?:"[a-z-]+"|[A-Z][A-Z0-9_]*)\s*,\s*"([A-Z][A-Z0-9-]+)"',
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
