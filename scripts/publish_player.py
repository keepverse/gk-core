#!/usr/bin/env python3
"""Build a zip folder players can use with no Node and no .NET SDK / Desktop Runtime.

Replaces `publish-player.ps1`. Publishes a self-contained server and launcher, builds the BepInEx
injector plugin (or copies a committed CI drop), lays the result out as `DropIntoGame` by game profile and
loader, and assembles the player folder.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **THERE WAS NO TIMEOUT ON ANY INVOCATION, AND THIS SCRIPT IS THE LONGEST-RUNNING THING IN THE
  REPOSITORY.** `npm ci`, `npm run build`, two self-contained `dotnet publish`es and up to two
  `dotnet build`s, none bounded. A wedged npm or a restore that waits on a locked feed holds the whole
  player pack open indefinitely and the only signal is the absence of one.
* **`$LASTEXITCODE` IS A SIDE EFFECT, NOT A RETURN VALUE.** The original checked it after each native
  command through an `Assert-NativeExit` helper whose whole contract is "the last native process to run
  happened to be the one I care about". That is true only as long as nothing else native runs in between,
  so the safety of the script depends on an invariant no test could see. Here ONE function runs every
  external command and RAISES on a non-zero exit, so the guarantee is structural: there is no path from a
  native command to the next stage that does not pass through the check. `VerificationTopologyTests`
  now asserts that structural property instead of grepping for `$LASTEXITCODE`.
* **NO MACHINE-READABLE OUTPUT.** CI had nothing to assert beyond "did it exit 0", and "did it exit 0" is
  exactly what the build command it replaced already gave. `--json` reports every stage, the resolved
  paths and the version.
* **THE MEANINGFUL STAGES WERE NOT ENUMERATED ANYWHERE.** A failure in `dotnet publish` and a failure in
  the MelonLoader drop both surfaced as "exit code 1", with no way to ask which. Each stage is named here,
  and the refusal names it.

WHAT THIS SCRIPT MUST NOT DO
----------------------------
**`Server\\data` STAYS.** It was deleted here until 2026-09-23 -- a line from the initial commit, with no
comment and no caller that ever needed it -- and the deletion was the whole defect. `dotnet publish` has
just put every `<Content Link="data\\...">` item from `gk-core/src/FusionRpg.Server/FusionRpg.Server.csproj`
there: `gk-core/data/tuning/**`, which `Program.cs` reads beside the exe, and the whole `gk-data/packs/fusion/data/seed/**` tree, which
`SeedImportRunner.RunSelfHealing` imports. With it gone, a player install answers `SeedTreeNotFound`
forever and `/health` reports `contentSource: "codeFallback"` -- the entire content layer, every atom,
container, curve, rarity, element row and channel policy, runs on the code fallback (E46
player-content-boot, `docs/architecture/effect-atom/spec-player-content-boot.md` §1).

The shape is not a new decision; it is the one already committed three times over:

* `tasks/keepverse-split-plan.md` "Resolver contract" exempts `src/FusionRpg.{Core,Data,Server}` from the
  content-root walk precisely BECAUSE "it resolves content against its build output, whose layout the
  `<Content Link="data\\...">` items preserve" -- an exemption that is false if the tree is then deleted.
* `FusionRpgUpdater.PrepareApply` (`gk-fusion/src/FusionRpg.Launcher/Services/FusionRpgUpdater.cs:63-70`) already
  PRESERVES `Server\\data` across an in-place update, so a pack that never had one was already the odd case.
* `PlayerPackProbe`'s layout contract (and `gk-core/scripts/smoke_player_pack.py`'s `/health` read) require the
  tree, so it cannot silently regress again.

Only the content COPY was ever at risk: the output folder is wiped at the top of this script, so no
developer's runtime database can be in it. A test asserts the tree survives, because the guard that matters
here is a test and not a comment.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "publish-player"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_VERSION = "1.0.0"
DEFAULT_MELON_PROFILE = "pvzrh-3.8.1"
# The MelonLoader 3.9 detection, as a MEASURED GameAssembly.dll size. It is a byte count of one specific
# shipped build, not a version field, which is why it lives here with its reason rather than as a bare
# magic number in a comparison. 3.8.1's GameAssembly differs in size, so the check discriminates the two.
MELON_39_GAMEASSEMBLY_BYTES = 57717248
PLUGIN_FILE_SUFFIXES = (".dll", ".json", ".pdb")
MELON_FILE_SUFFIXES = (".dll", ".json", ".pdb", ".cfg")
# Never shipped: a pdb in a player pack is a source path on someone's machine, and the pack is a download.
PDB_SUFFIX = ".pdb"

STAGES = (
    "resolve-game-dir", "web", "publish-server", "publish-launcher", "injector",
    "drop-bep", "melon", "documents", "sweep-pdb",
)


class Refusal(Exception):
    """A named precondition or stage failure. The run says WHICH stage, and never exits 0 having not run."""

    def __init__(self, stage: str, reason: str, detail: str = "") -> None:
        super().__init__(f"{stage}/{reason}: {detail}" if detail else f"{stage}/{reason}")
        self.stage = stage
        self.reason = reason
        self.detail = detail


def run(argv: list[str], cwd: Path, timeout: int, stage: str) -> str:
    """The ONLY place an external command is invoked. Raises unless it exited 0.

    STRUCTURAL, NOT A CONVENTION. The original read `$LASTEXITCODE` after each command, so whether a
    failure was noticed depended on nothing else native having run in between -- an invariant no test
    could check. Here every native command passes through this function, so "every native command is
    failure-checked" is a property of the control flow rather than a string to grep for, and
    `VerificationTopologyTests` asserts THAT instead of the old idiom.
    """
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
    except subprocess.TimeoutExpired as exc:
        raise Refusal(stage, "TIMEOUT",
                      f"{' '.join(argv)} did not exit within {timeout}s") from exc
    except FileNotFoundError as exc:
        raise Refusal(stage, "NOT-ON-PATH", f"{argv[0]}: {exc}") from exc
    except OSError as exc:
        raise Refusal(stage, "SPAWN-FAILED", f"{' '.join(argv)}: {exc}") from exc
    if proc.returncode != 0:
        tail = [line for line in ((proc.stdout or "") + (proc.stderr or "")).splitlines() if line.strip()][-6:]
        raise Refusal(stage, "EXIT-NON-ZERO",
                      f"{' '.join(argv)} exited {proc.returncode}"
                      + ("\n" + "\n".join(tail) if tail else " (no output at all)"))
    return (proc.stdout or "") + (proc.stderr or "")


def which(name: str) -> str | None:
    """The resolved path of an executable, or None. Checked BEFORE any expensive stage, not after."""
    return shutil.which(name)


# ------------------------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------------------------

def version_from_env(env: dict[str, str]) -> str:
    """`FUSIONRPG_VERSION` with a leading `v` stripped; the default when unset.

    `.TrimStart("v", "V")` in the original strips every leading `v` or `V`, not just one, so `vviceroy`
    would become `iceroy`. One leading version tag is the intent, so exactly one is removed -- and a
    version that was nothing but `v` is a configuration error rather than an empty string that flows on
    into `-p:Version=`.
    """
    raw = (env.get("FUSIONRPG_VERSION") or "").strip()
    if not raw:
        return DEFAULT_VERSION
    if raw[0] in "vV":
        raw = raw[1:]
    if not raw:
        raise Refusal("resolve-game-dir", "BAD-VERSION",
                      "FUSIONRPG_VERSION held only a 'v' tag and no version")
    return raw


def use_ci_drop(env: dict[str, str]) -> bool:
    """`FUSIONRPG_USE_CI_DROP` is TRUE only for the exact string `1`."""
    return env.get("FUSIONRPG_USE_CI_DROP") == "1"


def resolve_game_dir(root: Path, env: dict[str, str]) -> Path | None:
    """The folder holding `BepInEx/core` and `BepInEx/interop`, or None when the CI drop is used.

    None is a legitimate answer and not a failure: `FUSIONRPG_USE_CI_DROP=1` means the injector is copied
    prebuilt, so there is nothing to resolve. The refusal names every way to make it work, because a
    developer hitting this has three options and guessing which is available wastes the most time.
    """
    if use_ci_drop(env):
        return None
    configured = env.get("FUSIONRPG_GAME_DIR")
    if configured:
        candidate = Path(configured)
        if not candidate.is_dir():
            raise Refusal("resolve-game-dir", "GAME-DIR-MISSING",
                          f"FUSIONRPG_GAME_DIR is set to {configured}, which is not a directory")
        return candidate.resolve()
    parent = root.parent
    if (parent / "BepInEx" / "core").is_dir():
        return parent.resolve()
    ci_refs = root / "artifacts" / "bepinex-refs"
    if (ci_refs / "BepInEx" / "core").is_dir():
        return ci_refs.resolve()
    # The refs fetcher is named with the extension it HAS on disk, not the one this port would prefer.
    # It was `fetch-bepinex-refs.ps1` when this comment was written and became `fetch_bepinex_refs.py`
    # when that tool was ported; naming the retired form here would be a rot citation introduced by the
    # very commit that removes rot citations. The contract suite resolves this path and fails if it
    # names something absent, and it caught this one going stale.
    raise Refusal("resolve-game-dir", "NO-REFERENCE-TREE",
                  "no BepInEx reference tree found. Set FUSIONRPG_GAME_DIR to a game folder with "
                  "BepInEx\\core and BepInEx\\interop; or run scripts/fetch_bepinex_refs.py for "
                  "CI-style refs under artifacts/bepinex-refs; or set FUSIONRPG_USE_CI_DROP=1 with "
                  "artifacts/ci-drop-into-game present.")


def detect_melon_profile(ml_dir: Path, env: dict[str, str]) -> str:
    """The MelonLoader game profile, forced by `FUSIONRPG_GAME_PROFILE` or auto-detected.

    The auto-detect compares `GameAssembly.dll`'s SIZE against a measured constant, so it is only as good
    as that one measurement. An explicit `FUSIONRPG_GAME_PROFILE` always wins, which is the escape hatch
    when a new build changes the size -- and the reason the constant is named here rather than inlined.
    """
    forced = env.get("FUSIONRPG_GAME_PROFILE")
    if forced:
        return forced
    game_assembly = ml_dir / "GameAssembly.dll"
    try:
        size = game_assembly.stat().st_size
    except OSError:
        return DEFAULT_MELON_PROFILE
    return "pvzrh-3.9" if size == MELON_39_GAMEASSEMBLY_BYTES else DEFAULT_MELON_PROFILE


def melon_is_available(ml_dir: Path | None) -> bool:
    """Whether a MelonLoader drop can be built at all: the env var names a real MelonLoader tree."""
    if not ml_dir:
        return False
    return (ml_dir / "MelonLoader" / "net6" / "MelonLoader.dll").is_file()


# ------------------------------------------------------------------------------------------------
# The output layout
# ------------------------------------------------------------------------------------------------

@dataclass
class Layout:
    """Every path the run touches, resolved once.

    RESOLVED ONCE, HERE, because the original re-derived `$Out`, `$Drop`, `$PluginOut` and friends
    inline at eleven different points. A layout assembled in one place is what lets the whole pipeline be
    asserted in a test without a build, and it is why the contract suite can check the Drop shape.
    """
    root: Path
    out: Path
    server_out: Path
    web: Path
    plugin_out: Path
    ci_drop: Path
    drop: Path
    manifest_out: Path
    melon_plugin_out: Path

    @classmethod
    def build(cls, root: Path) -> "Layout":
        out = root / "dist" / "FusionRpg"
        return cls(root=root, out=out, server_out=out / "Server", web=root / "web" / "fusion-rpg-web",
                   plugin_out=root / "artifacts" / "plugins" / "FusionRpg",
                   ci_drop=root / "artifacts" / "ci-drop-into-game",
                   drop=out / "DropIntoGame",
                   manifest_out=out / "loader-manifest.json",
                   melon_plugin_out=root / "artifacts" / "plugins" / "MelonLoader")

    def drop_bep_legacy(self) -> Path:
        return self.drop / "BepInEx"

    def drop_bep_381(self) -> Path:
        return self.drop / "pvzrh-3.8.1" / "BepInEx"

    def drop_melon_scoped(self, profile: str) -> Path:
        return self.drop / profile / "MelonLoader"


def reset_directory(path: Path, root: Path) -> None:
    """Create `path` empty, refusing to remove anything outside the repository.

    This is a RECURSIVE DELETE of a path assembled from configuration, and the original ran
    `Remove-Item -Recurse -Force` with no containment check at all. Three of the four are inside the
    repository by construction; the check makes that a measurement rather than an assumption.
    """
    if not path.resolve().is_relative_to(root.resolve()):
        raise Refusal("web", "DELETE-OUTSIDE-REPO",
                      f"refusing to remove {path}: it is outside {root}")
    if path.is_dir():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def copy_tree(source: Path, destination: Path) -> int:
    """Copy a directory's contents, returning the file count. Missing sources are NOT an error here."""
    if not source.is_dir():
        return 0
    count = 0
    for item in sorted(source.iterdir()):
        target = destination / item.name
        if item.is_dir():
            target.mkdir(parents=True, exist_ok=True)
            count += copy_tree(item, target)
        else:
            shutil.copy2(item, target)
            count += 1
    return count


def copy_with_suffixes(source_dir: Path, destinations: list[Path], suffixes: tuple[str, ...]) -> int:
    """Copy the files with one of `suffixes` into every destination. Returns the number COPIED."""
    count = 0
    for item in sorted(source_dir.iterdir()) if source_dir.is_dir() else []:
        if not item.is_file() or item.suffix.lower() not in suffixes:
            continue
        for destination in destinations:
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, destination / item.name)
        count += 1
    return count


def sweep_pdbs(root: Path) -> int:
    """Delete every `.pdb` under the player folder. Returns how many went.

    After the drops are laid out, so it also sweeps the copies inside them. A pdb in a player pack
    embeds a source path from the machine that built it, and the pack is a download.
    """
    removed = 0
    for pdb in sorted(root.rglob(f"*{PDB_SUFFIX}")):
        if pdb.is_file():
            pdb.unlink()
            removed += 1
    return removed


# ------------------------------------------------------------------------------------------------
# Stages
# ------------------------------------------------------------------------------------------------

@dataclass
class Report:
    """What the run did. Every field is a READING re-measured each run, never a constant."""
    version: str = ""
    game_dir: str | None = None
    ci_drop: bool = False
    melon_profile: str | None = None
    plugin_files: int = 0
    melon_files: int = 0
    pdbs_removed: int = 0
    server_data_present: bool = False
    stages: list[str] = field(default_factory=list)
    out: str = ""


def stage_web(layout: Layout, timeout: int) -> None:
    """Refresh the locked web dependency tree, then build the UI into the server's wwwroot.

    `npm ci` BEFORE `npm run build`, always -- the order is a contract, not a preference, and
    `VerificationTopologyTests` asserts it. The original also carried the `package-lock.json` check
    TWICE, identically; one check remains.
    """
    if which("npm") is None:
        raise Refusal("web", "NPM-REQUIRED",
                      "npm is required on the developer PC to build the UI. Players will not need npm.")
    if not (layout.web / "package-lock.json").is_file():
        raise Refusal("web", "NO-LOCKFILE",
                      "web/package-lock.json is required for a reproducible player pack")
    run(["npm", "ci"], layout.web, timeout, "web")
    run(["npm", "run", "build"], layout.web, timeout, "web")


def stage_publish_server(layout: Layout, version: str, timeout: int) -> None:
    run(["dotnet", "publish", str(layout.root / "src" / "FusionRpg.Server" / "FusionRpg.Server.csproj"),
         "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
         "-p:Version=" + version, "-p:InformationalVersion=" + version,
         "-o", str(layout.server_out)], layout.root, timeout, "publish-server")


def stage_publish_launcher(layout: Layout, version: str, timeout: int) -> None:
    run(["dotnet", "publish", str(layout.root / "src" / "FusionRpg.Launcher" / "FusionRpg.Launcher.csproj"),
         "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
         "-p:Version=" + version, "-p:InformationalVersion=" + version,
         "-o", str(layout.out)], layout.root, timeout, "publish-launcher")


def stage_injector(layout: Layout, version: str, game_dir: Path | None, env: dict[str, str],
                   timeout: int) -> None:
    """Build the BepInEx plugin, or copy the committed CI drop."""
    if use_ci_drop(env):
        source_dll = layout.ci_drop / "FusionRpg.Injector.dll"
        if not source_dll.is_file():
            # Named with the extension it HAS. `sync-ci-drop-into-game` is ported to
            # `sync_ci_drop_into_game.py` by this change, so that spelling is already true; naming the
            # retired `.ps1` here would be a rot citation on the day of the port.
            raise Refusal("injector", "CI-DROP-INCOMPLETE",
                          f"FUSIONRPG_USE_CI_DROP=1 but {source_dll} is missing -- run "
                          f"scripts/sync_ci_drop_into_game.py locally")
        reset_directory(layout.plugin_out, layout.root)
        for item in sorted(layout.ci_drop.iterdir()):
            if item.is_file():
                shutil.copy2(item, layout.plugin_out / item.name)
        return
    if game_dir is None:
        raise Refusal("injector", "NO-GAME-DIR",
                      "GameDir required to build injector (or set FUSIONRPG_USE_CI_DROP=1)")
    run(["dotnet", "build", str(layout.root / "src" / "FusionRpg.Injector.BepInEx" /
                                "FusionRpg.Injector.BepInEx.csproj"),
         "-c", "Release", "-p:GameDir=" + str(game_dir),
         "-p:Version=" + version, "-p:InformationalVersion=" + version,
         "-p:OutputPath=" + str(layout.plugin_out) + os.sep], layout.root, timeout, "injector")
    if not (layout.plugin_out / "FusionRpg.Injector.dll").is_file():
        raise Refusal("injector", "OUTPUT-MISSING",
                      f"injector output missing: {layout.plugin_out / 'FusionRpg.Injector.dll'}")


def stage_melon(layout: Layout, version: str, env: dict[str, str], timeout: int) -> tuple[str | None, int]:
    """The optional MelonLoader drop. Returns `(profile, file count)`; `(None, 0)` when skipped."""
    ml_dir = env.get("FUSIONRPG_ML_GAMEDIR")
    if not melon_is_available(Path(ml_dir) if ml_dir else None):
        return None, 0
    assert ml_dir is not None
    profile = detect_melon_profile(Path(ml_dir), env)
    if profile == "pvzrh-3.9":
        project = layout.root / "src" / "FusionRpg.Injector.MelonLoader.39" / "FusionRpg.Injector.MelonLoader.39.csproj"
        dll_name = "FusionRpg.Injector.MelonLoader.39.dll"
    else:
        project = layout.root / "src" / "FusionRpg.Injector.MelonLoader" / "FusionRpg.Injector.MelonLoader.csproj"
        dll_name = "FusionRpg.Injector.MelonLoader.dll"
    # The guard is a Python tool already, so the invocation and its arguments are the .py spellings. A
    # stale `& path.ps1` here fails only when a player pack is BUILT, which is the worst place to find it.
    # The KEBAB name is the live one -- that guard was ported without a stem change, unlike the snake_case
    # this program's own tools use -- so the contract suite asserts this path EXISTS rather than trusting
    # the spelling here, which is how a wrong-but-plausible name survives until a player pack is built.
    run([sys.executable, str(layout.root / "scripts" / "guard-game-profile.py"),
         "--game-dir", ml_dir, "--profile", profile], layout.root, timeout, "melon")
    reset_directory(layout.melon_plugin_out, layout.root)
    run(["dotnet", "build", str(project), "-c", "Release", "-p:MlGameDir=" + ml_dir,
         "-p:GameProfile=" + profile, "-p:Version=" + version,
         "-p:InformationalVersion=" + version,
         "-p:OutputPath=" + str(layout.melon_plugin_out) + os.sep], layout.root, timeout, "melon")
    scoped = layout.drop_melon_scoped(profile)
    destinations = [scoped]
    if profile == DEFAULT_MELON_PROFILE:
        destinations.append(layout.drop / "MelonLoader")
    count = copy_with_suffixes(layout.melon_plugin_out, destinations, MELON_FILE_SUFFIXES)
    if not (scoped / dll_name).is_file():
        # A REFUSAL, not a warning. The original had this as a `Write-Warning` in an earlier revision,
        # and a warning is invisible to `set -e`, to a CI step reading the exit code, and to anyone
        # scrolling a release log.
        raise Refusal("melon", "OUTPUT-MISSING",
                      f"MelonLoader injector DLL missing after a successful build: {scoped / dll_name}")
    return profile, count


def stage_documents(layout: Layout) -> None:
    """The files a player needs next to the binaries. Each is optional except PLAYERS and LICENSE."""
    # The destination is ENSURED rather than assumed. In the pipeline `reset_directory` has already made
    # it, but as a unit this stage would otherwise raise a bare FileNotFoundError out of `copy2` -- a
    # crash rather than a named refusal, which is the shape that reads as "something odd happened"
    # instead of "this file was missing".
    layout.out.mkdir(parents=True, exist_ok=True)
    optional = [
        (layout.root / "src" / "FusionRpg.Launcher" / "loader-manifest.json", layout.manifest_out),
        (layout.root / "game-profiles.json", layout.out / "game-profiles.json"),
        (layout.root / "NOTICE", layout.out / "NOTICE"),
    ]
    for source, destination in optional:
        if source.is_file():
            shutil.copy2(source, destination)
    required = [(layout.root / "docs" / "runbook" / "PLAYERS.txt", layout.out / "PLAYERS.txt"),
                (layout.root / "LICENSE", layout.out / "LICENSE")]
    for source, destination in required:
        if not source.is_file():
            raise Refusal("documents", "SOURCE-MISSING", f"{source} is required in a player pack")
        shutil.copy2(source, destination)


def publish(root: Path, env: dict[str, str], timeout: int) -> Report:
    """The whole pipeline. Raises on the first named failure, leaving the tree as it found it where it
    can -- the output folder is wiped by design, so a partial pack is not a usable pack and is never
    reported as one."""
    report = Report(version=version_from_env(env), ci_drop=use_ci_drop(env), stages=[])
    layout = Layout.build(root)

    game_dir = resolve_game_dir(root, env)
    report.game_dir = str(game_dir) if game_dir else None
    report.stages.append("resolve-game-dir")

    stage_web(layout, timeout)
    report.stages.append("web")

    reset_directory(layout.out, root)
    layout.server_out.mkdir(parents=True, exist_ok=True)
    reset_directory(layout.plugin_out, root)
    report.stages.append("reset-output")

    stage_publish_server(layout, report.version, timeout)
    report.stages.append("publish-server")

    stage_publish_launcher(layout, report.version, timeout)
    report.stages.append("publish-launcher")

    # The published wwwroot is the build's; fall back to the checked-in one only if publish did not put
    # it there, which is the shape the original used.
    if not (layout.server_out / "wwwroot").is_dir():
        copy_tree(layout.root / "src" / "FusionRpg.Server" / "wwwroot", layout.server_out / "wwwroot")

    stage_injector(layout, report.version, game_dir, env, timeout)
    report.stages.append("injector")

    # Nested DropIntoGame by profile + loader, with the legacy flat and unscoped paths kept for 3.8.1
    # Bep. Three destinations, one copy each, so a player on any of the three finds the plugin.
    report.plugin_files = copy_with_suffixes(
        layout.plugin_out, [layout.drop_bep_legacy(), layout.drop_bep_381(), layout.drop],
        PLUGIN_FILE_SUFFIXES)
    report.stages.append("drop-bep")

    profile, melon_files = stage_melon(layout, report.version, env, timeout)
    report.melon_profile, report.melon_files = profile, melon_files
    report.stages.append("melon")

    stage_documents(layout)
    report.stages.append("documents")

    report.pdbs_removed = sweep_pdbs(layout.out)
    report.stages.append("sweep-pdb")

    report.out = str(layout.out)
    # Measured, not asserted in prose: the whole reason the `Server\data` tree exists is that a player
    # install without it silently runs the entire content layer on the code fallback.
    report.server_data_present = (layout.server_out / "data").is_dir()
    if not report.server_data_present:
        raise Refusal("sweep-pdb", "SERVER-DATA-MISSING",
                      f"{layout.server_out / 'data'} is absent from the published pack. A player install "
                      f"without it answers SeedTreeNotFound and /health reports "
                      f"contentSource: codeFallback, so the whole content layer runs on the fallback")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Build a zip folder players can use with no Node and no .NET SDK / Desktop Runtime "
                    "(replaces publish-player.ps1).")
    parser.add_argument("--root", type=Path, default=None, help="the repository")
    parser.add_argument("--timeout", type=int, default=3600,
                        help="seconds per external command (default 3600; the original had NO timeout "
                             "on any of npm, dotnet publish or dotnet build)")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    args = parser.parse_args(argv)
    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    env = dict(os.environ)
    try:
        report = publish(root, env, args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": refusal.stage,
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        else:
            print(f"PUBLISH-PLAYER REFUSED [{refusal.stage}]: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK", **{k: v for k, v in report.__dict__.items()}},
                         indent=2))
        return EXIT_OK

    if report.game_dir:
        print(f"==> Injector GameDir (refs only): {report.game_dir}")
    else:
        print("==> Using committed artifacts/ci-drop-into-game (no injector rebuild)")
    print(f"==> MelonLoader drop: {report.melon_profile or 'skipped (set FUSIONRPG_ML_GAMEDIR)'}")
    print(f"==> pdb files removed: {report.pdbs_removed}")
    print()
    print(f"Player folder: {report.out}")
    print(f"Version: {report.version}")
    print("Players: double-click FusionRpg.Launcher.exe (no Node, no .NET SDK, no Desktop Runtime).")
    print("Launcher copies DropIntoGame into BepInEx\\plugins\\FusionRpg and starts "
          "Server\\FusionRpg.Server.exe.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
