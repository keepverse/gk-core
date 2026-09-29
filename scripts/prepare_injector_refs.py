#!/usr/bin/env python3
"""Prepare BepInEx core refs plus optional game interop for Injector builds (CI / path-free publish).
Does not install into any game folder.

Replaces `scripts/prepare-injector-refs.ps1`.

WHAT THIS DOES, IN ORDER
------------------------
1. Fetch the BepInEx reference tree (now `gk-core/scripts/fetch_bepinex_refs.py`), unless `--skip-refs-fetch`.
2. Resolve where the per-game **interop** assemblies come from, trying four sources in this order and
   reporting which one was used:
     a. `FUSIONRPG_GAME_DIR`, a legal game folder containing `BepInEx/interop/Assembly-CSharp.dll`;
     b. `FUSIONRPG_INTEROP_ZIP_URL`, a private zip of those DLLs (interop is generated per game and is
        never committed);
     c. an interop tree already present under the refs directory;
     d. `BepInEx/interop/Assembly-CSharp.dll` in the directory ABOVE the repository root.
3. Refuse, with the three ways out, if no interop ends up in place.
4. Report the `FUSIONRPG_GAME_DIR` an Injector build should use.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE FETCHER'S EXIT CODE WAS IGNORED.** `& (Join-Path $PSScriptRoot "fetch-bepinex-refs.ps1")` with
  no `$LASTEXITCODE` check, under `$ErrorActionPreference = "Stop"`. A child process exiting nonzero is
  not a PowerShell exception, so a FAILED fetch fell through to the interop logic and could still
  succeed -- on a stale tree, reporting a green result. A non-success exit from the fetcher is now a
  named refusal.

* **THE INTEROP ZIP DOWNLOAD WAS UNBOUNDED.** `Invoke-WebRequest` with no timeout. The sibling fetcher's
  real run took 1486 seconds for a 34 MB asset on this machine, so "slow" here is the measured norm and
  an unbounded call cannot distinguish it from "hung".

* **IT DESTROYED ITS STAGING DIRECTORY BEFORE PROVING THE REPLACEMENT.** `Remove-Item $tmp -Recurse
  -Force` ran before `Expand-Archive`. The extraction now happens in a staging directory that is only
  consumed on success, matching what `fetch_bepinex_refs.py` does.

* **AN ARBITRARY FIRST HIT FOR THE INTEROP ROOT.** `Get-ChildItem $tmp -Recurse -Filter
  "Assembly-CSharp.dll" | Select-Object -First 1` picked whichever the filesystem returned first, and a
  zip holding more than one would have had its interop chosen arbitrarily and silently. More than one is
  now a named refusal that lists them.

* **A SET-BUT-WRONG `FUSIONRPG_GAME_DIR` FELL THROUGH SILENTLY.** `if ($env:FUSIONRPG_GAME_DIR -and
  (Test-Path $env:FUSIONRPG_GAME_DIR))` treats a path that does not exist as unset, so a typo'd value
  quietly sent the tool down a different branch. It is now a named refusal, because the caller asked for
  a specific source. `--ignore-missing-game-dir` restores the old fall-through for anyone who depended on
  it.

* **`Copy-Item (Join-Path $src "*")` COPIED NOTHING, SILENTLY, IF THE GLOB MATCHED NOTHING.** An empty
  interop source then failed much later at the final check, naming the wrong thing.

* **THE FINAL `$env:FUSIONRPG_GAME_DIR = $Refs` WAS INERT.** It set the variable in the SCRIPT's own
  process, which exits immediately, so a caller that ran this and then built the Injector inherited
  nothing. The value is now REPORTED -- in `--json`, and on stdout as a line a caller can use -- and the
  docstring says plainly that it is not exported. This is the one behaviour that could not be preserved,
  because it never worked.

DELIBERATELY PRESERVED, INCLUDING ONE THAT LOOKS LIKE A BUG
-----------------------------------------------------------
Step (d) looks for a game install in the directory ABOVE the repository root, which on this machine is
`D:\\Works\\source` and is very unlikely to hold one. That is what the original did and it is harmless
(harmless, not useful: it is the last branch and the final check still refuses if nothing was found), so
it is kept rather than "fixed" as an unrequested behaviour change. `--no-parent-lookup` skips it, and the
verdict reports which branch was used so a reader can see it fired.

THE BRANCH TAKEN IS ALWAYS REPORTED
-----------------------------------
An assembly copied from the wrong place is the failure this tool cannot otherwise detect, so the verdict
carries `interopSource` as a closed vocabulary, not prose.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

TOOL_ID = "prepare-injector-refs"

EXIT_REFUSED = 64

MARKER = "Assembly-CSharp.dll"
ZIP_MAGIC = b"PK\x03\x04"
USER_AGENT = "FusionRpg-prepare-injector-refs/1.0"
GAME_DIR_ENV = "FUSIONRPG_GAME_DIR"
INTEROP_URL_ENV = "FUSIONRPG_INTEROP_ZIP_URL"

DEFAULT_REFS_DIR = ("artifacts", "bepinex-refs")
DEFAULT_INTEROP_ZIP = ("artifacts", "interop-cache.zip")
DEFAULT_INTEROP_EXTRACT = ("artifacts", "interop-extract")
FETCHER = "fetch_bepinex_refs.py"

DEFAULT_FETCH_TIMEOUT = 5400
DEFAULT_DOWNLOAD_TIMEOUT = 1800
DEFAULT_COPY_TIMEOUT = 600

# Where the interop came from. A CLOSED vocabulary: the point of reporting it is that a reader can tell
# a right assembly from a wrong one, and prose cannot be checked.
SOURCE_GAME_DIR = "game-dir-env"
SOURCE_ZIP_URL = "interop-zip-url"
SOURCE_ALREADY_PRESENT = "already-present-under-refs"
SOURCE_PARENT = "parent-of-repo-root"
SOURCE_NONE = "none"

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "INVALID-FETCH-ARG", "FETCHER-MISSING", "FETCHER-FAILED", "FETCHER-REFUSED",
    "GAME-DIR-MISSING", "GAME-DIR-NO-INTEROP", "GAME-DIR-EMPTY-INTEROP",
    "INTEROP-DOWNLOAD-TIMED-OUT", "INTEROP-DOWNLOAD-REFUSED", "INTEROP-NOT-A-ZIP",
    "INTEROP-EXTRACT-FAILED", "INTEROP-NOT-FOUND", "INTEROP-AMBIGUOUS", "INTEROP-EMPTY-SOURCE",
    "INTEROP-COPY-FAILED", "STAGING-FAILED", "NO-INTEROP",
}

# Bound once, module-private: `subprocess`/`shutil`/`urllib` are process-wide modules.
_RUN = subprocess.run
_RMTREE = shutil.rmtree
_COPYTREE = shutil.copytree
_URLOPEN = urllib.request.urlopen


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def log(message: str) -> None:
    print(f"==> {message}", file=sys.stderr)


def run_fetcher(root: Path, timeout: int, extra: list[str]) -> dict:
    """The sibling fetcher, as a bounded child. Its exit code is the whole point of this call."""
    fetcher = Path(__file__).resolve().parent / FETCHER
    if not fetcher.is_file():
        raise Refusal("FETCHER-MISSING",
                      f"the refs fetcher is not at {fetcher}. This tool composes it rather than "
                      f"reimplementing the fetch, so a missing fetcher is a named refusal rather than "
                      f"a partially prepared tree")
    command = [sys.executable, str(fetcher), "--root", str(root), "--json", *extra]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(root))
    except subprocess.TimeoutExpired as expired:
        raise Refusal("FETCHER-FAILED",
                      f"{FETCHER} did not finish within {timeout}s. It is never retried here: a retry "
                      f"re-downloads 34 MB and the previous run's diagnosis is lost") from expired
    except OSError as error:
        raise Refusal("FETCHER-FAILED", f"could not start {FETCHER}: {error}") from error
    try:
        payload = json.loads(proc.stdout or "{}")
    except json.JSONDecodeError:
        payload = {}
    if proc.returncode != 0:
        # The original ignored this. A failed fetch that falls through can still produce a green result
        # from a STALE tree, which is the whole reason this is a refusal.
        raise Refusal("FETCHER-FAILED",
                      f"{FETCHER} exited {proc.returncode}"
                      + (f": {payload.get('reason')}" if payload.get("reason") else "")
                      + ". The original did not check this exit code, so a failed fetch could be reported "
                        "as a green prepare on whatever refs were already on disk. Pass --skip-refs-fetch "
                      "to prepare from an existing tree on purpose")
    return payload


def marker_in(directory: Path) -> bool:
    return (directory / MARKER).is_file()


def copy_interop(source: Path, destination: Path) -> int:
    """Copy a directory's contents. Refuses on an empty source rather than reporting a success that
    copied nothing, which the original's `Copy-Item (Join-Path $src "*")` did."""
    if not directory_files(source):
        raise Refusal("INTEROP-EMPTY-SOURCE",
                      f"{source} holds no files, so copying it would prepare nothing and the failure "
                      f"would surface later at the final check naming the wrong thing")
    try:
        destination.mkdir(parents=True, exist_ok=True)
        _COPYTREE(source, destination, dirs_exist_ok=True)
    except OSError as error:
        raise Refusal("INTEROP-COPY-FAILED",
                      f"copying interop from {source} to {destination} failed: {error}") from error
    return len(directory_files(destination))


def directory_files(path: Path) -> list[Path]:
    return [p for p in sorted(path.rglob("*")) if p.is_file()] if path.is_dir() else []


def download_interop_zip(url: str, destination: Path, timeout: int) -> int:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    written = 0
    try:
        with _URLOPEN(request, timeout=timeout) as response:
            with destination.open("wb") as handle:
                while True:
                    chunk = response.read(1 << 20)
                    if not chunk:
                        break
                    handle.write(chunk)
                    written += len(chunk)
    except TimeoutError as expired:
        raise Refusal("INTEROP-DOWNLOAD-TIMED-OUT",
                      f"the interop zip did not finish within {timeout}s; {written} bytes had been "
                      f"written to {destination}") from expired
    except urllib.error.HTTPError as error:
        raise Refusal("INTEROP-DOWNLOAD-REFUSED",
                      f"the interop zip answered {error.code}: {error.reason}") from error
    except urllib.error.URLError as error:
        if isinstance(error.reason, TimeoutError):
            raise Refusal("INTEROP-DOWNLOAD-TIMED-OUT",
                          f"the interop zip did not finish within {timeout}s: {error.reason}") from error
        raise Refusal("INTEROP-DOWNLOAD-REFUSED",
                      f"the interop zip was unreachable: {error.reason}") from error
    except OSError as error:
        raise Refusal("INTEROP-DOWNLOAD-REFUSED", f"the interop zip failed: {error}") from error
    return written


def require_zip(path: Path, written: int) -> None:
    if not path.is_file():
        raise Refusal("INTEROP-NOT-A-ZIP", f"the download wrote {written} bytes but there is no {path}")
    with path.open("rb") as handle:
        head = handle.read(4)
    if head != ZIP_MAGIC:
        raise Refusal("INTEROP-NOT-A-ZIP",
                      f"{path} starts with {head!r}, not the ZIP magic {ZIP_MAGIC!r}. An auth error or an "
                      f"HTML login page is the usual cause, and the original carried it into "
                      f"Expand-Archive as a compression-method error")
    if not zipfile.is_zipfile(path):
        raise Refusal("INTEROP-NOT-A-ZIP", f"{path} has a ZIP header but is not a readable archive")


def locate_in_extract(extract_dir: Path) -> Path:
    """The directory holding `Assembly-CSharp.dll`. The original tried the root, then
    `BepInEx/interop`, then an arbitrary recursive first hit. The third strategy is now a refusal when
    it is ambiguous, because a zip with more than one interop cannot be resolved by guesswork."""
    if marker_in(extract_dir):
        return extract_dir
    nested = extract_dir / "BepInEx" / "interop"
    if marker_in(nested):
        return nested
    hits = sorted({p.parent for p in extract_dir.rglob(MARKER)}) if extract_dir.is_dir() else []
    if not hits:
        top = sorted(p.name for p in extract_dir.iterdir())[:20] if extract_dir.is_dir() else []
        raise Refusal("INTEROP-NOT-FOUND",
                      f"the interop zip holds no {MARKER}. Its top-level entries are {top}. The original "
                      f"raised the same condition with no evidence of what it had looked at")
    if len(hits) > 1:
        # Comma-joined, POSIX, and truncated with a count of what was left out. Rendering the list with
        # `str(list_of_PurePath)` was both uglier to read and platform-dependent, and it made a case that
        # asserted the candidates appeared unable to distinguish "listed" from "some letter occurs in the
        # sentence".
        candidates = [h.relative_to(extract_dir).as_posix() for h in hits]
        shown = ", ".join(candidates[:6])
        if len(candidates) > 6:
            shown += f", ... ({len(candidates) - 6} more)"
        raise Refusal("INTEROP-AMBIGUOUS",
                      f"the interop zip holds {MARKER} in {len(candidates)} places ({shown}); the "
                      f"original took whichever the filesystem returned first. Repack the zip with one "
                      f"interop set")
    return hits[0]


def extract(zip_path: Path, extract_dir: Path) -> None:
    try:
        extract_dir.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(zip_path) as archive:
            archive.extractall(extract_dir)
    except (zipfile.BadZipFile, OSError, RuntimeError) as error:
        raise Refusal("INTEROP-EXTRACT-FAILED",
                      f"extracting {zip_path} into {extract_dir} failed: {error}") from error


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="prepare-injector-refs",
        description="Prepare BepInEx core refs plus optional game interop for Injector builds "
                    "(replaces prepare-injector-refs.ps1). Does not install into any game folder.")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--refs-dir", default="",
                        help=f"where the reference tree lives (default: {'/'.join(DEFAULT_REFS_DIR)})")
    parser.add_argument("--skip-refs-fetch", action="store_true",
                        help="prepare from the existing reference tree instead of fetching it. The "
                             "original always re-fetched, which costs a measured 1486s for the 34 MB "
                             "asset even when the tree on disk is already correct")
    parser.add_argument("--fetch-arg", action="append", default=[], metavar="ARGS",
                        help="extra arguments for the refs fetcher, as ONE shell-quoted string; "
                             "repeatable. It is one string rather than a paired flag/value because "
                             "argparse REJECTS a value beginning with '-', so '--fetch-arg --api-base "
                             "--fetch-arg URL' dies with SystemExit 2 before this function runs -- and "
                             "the arguments worth forwarding are exactly the ones that begin with '-'")
    parser.add_argument("--ignore-missing-game-dir", action="store_true",
                        help="fall through instead of refusing when FUSIONRPG_GAME_DIR is set but does "
                             "not exist (the original's behaviour)")
    parser.add_argument("--no-parent-lookup", action="store_true",
                        help="skip the branch that looks for a game install above the repository root")
    parser.add_argument("--keep-interop-extract", action="store_true",
                        help="leave the interop staging directory on disk instead of removing it")
    parser.add_argument("--fetch-timeout", type=int, default=DEFAULT_FETCH_TIMEOUT,
                        help=f"seconds for the refs fetch (default {DEFAULT_FETCH_TIMEOUT})")
    parser.add_argument("--download-timeout", type=int, default=DEFAULT_DOWNLOAD_TIMEOUT,
                        help=f"seconds for the interop zip download (default {DEFAULT_DOWNLOAD_TIMEOUT})")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON on stdout")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for name, value in (("--fetch-timeout", args.fetch_timeout),
                        ("--download-timeout", args.download_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)

    root = Path(args.root).expanduser().resolve() if args.root else Path(__file__).resolve().parent.parent
    refs = _resolve(root, args.refs_dir, DEFAULT_REFS_DIR)
    interop_out = refs / "BepInEx" / "interop"
    zip_path = root.joinpath(*DEFAULT_INTEROP_ZIP)
    extract_dir = root.joinpath(*DEFAULT_INTEROP_EXTRACT)

    # Each `--fetch-arg` is ONE string, split here. See the flag's help for why.
    forwarded: list[str] = []
    for chunk in args.fetch_arg:
        try:
            forwarded.extend(shlex.split(chunk))
        except ValueError as error:
            return _refuse("INVALID-FETCH-ARG",
                           f"--fetch-arg {chunk!r} is not parseable as arguments: {error}", args.json)

    started = time.monotonic()
    envelope: dict = {"tool": TOOL_ID, "refsDir": str(refs), "interopOut": str(interop_out),
                      "marker": MARKER, "gameDirEnv": GAME_DIR_ENV, "gameDir": str(refs),
                      "interopSource": SOURCE_NONE, "prepared": False,
                      "gameDirExported": False,
                      "gameDirNote": "FUSIONRPG_GAME_DIR is REPORTED, not exported. The original assigned it "
                                     "in its own process, which exits immediately, so no caller ever "
                                     "inherited it."}
    try:
        if not args.skip_refs_fetch:
            log(f"Fetching BepInEx reference tree into {refs}")
            envelope["refsFetch"] = run_fetcher(root, args.fetch_timeout, forwarded)
        else:
            envelope["refsFetch"] = {"skipped": True}
            log("Skipping the refs fetch; using the existing tree")
        if not (refs / "BepInEx" / "core").is_dir():
            raise Refusal("FETCHER-FAILED",
                          f"there is no BepInEx reference tree at {refs / 'BepInEx' / 'core'}"
                          + ("" if args.skip_refs_fetch else ", and the fetch reported success"))

        # ── branch (a): an explicit game folder
        #
        # A SET-BUT-MISSING value FALLS THROUGH to the rest of the chain, which is what the original
        # did and what --ignore-missing-game-dir promises. The first version handled it inside the
        # branch, so the flag did not fall through at all: it skipped branches (b), (c) and (d) and went
        # straight to the final refusal, which is the opposite of what its name says. A flag whose name
        # is a promise about control flow has to match the control flow. Without the flag it is a named
        # refusal, because the caller asked for a specific source and a typo must not look honoured.
        game_dir_raw = os.environ.get(GAME_DIR_ENV)
        game_dir = (Path(game_dir_raw.strip()).expanduser()
                    if game_dir_raw and game_dir_raw.strip() else None)
        if game_dir is not None and not game_dir.is_dir():
            if not args.ignore_missing_game_dir:
                raise Refusal("GAME-DIR-MISSING",
                              f"{GAME_DIR_ENV} is set to {game_dir}, which does not exist. The "
                              f"original treated that as unset and quietly tried a different source, "
                              f"so a typo'd path looked like it had been honoured. Pass "
                              f"--ignore-missing-game-dir for the old fall-through")
            envelope["gameDirIgnored"] = str(game_dir)
            log(f"{GAME_DIR_ENV}={game_dir} does not exist; --ignore-missing-game-dir, continuing")
            game_dir = None
        if game_dir is not None:
            if not marker_in(game_dir / "BepInEx" / "interop"):
                raise Refusal("GAME-DIR-NO-INTEROP",
                              f"{GAME_DIR_ENV}={game_dir} has no BepInEx/interop/{MARKER}. Interop is "
                              f"generated per game, so a core-only tree cannot supply it")
            envelope["interopSource"] = SOURCE_GAME_DIR
            envelope["interopFrom"] = str(game_dir / "BepInEx" / "interop")
            envelope["interopFiles"] = copy_interop(game_dir / "BepInEx" / "interop", interop_out)
            log(f"Copied interop from {game_dir}")
        # ── branch (b): a private zip
        elif os.environ.get(INTEROP_URL_ENV):
            url = os.environ[INTEROP_URL_ENV].strip()
            log(f"Downloading interop zip from {url}")
            try:
                zip_path.parent.mkdir(parents=True, exist_ok=True)
                written = download_interop_zip(url, zip_path, args.download_timeout)
                envelope["interopZipBytes"] = written
                require_zip(zip_path, written)
                # Staging first: the original removed this directory BEFORE extracting, so a failed
                # download or a corrupt zip destroyed whatever was there.
                if extract_dir.is_dir():
                    _RMTREE(extract_dir)
                extract(zip_path, extract_dir)
                source = locate_in_extract(extract_dir)
                envelope["interopSource"] = SOURCE_ZIP_URL
                # POSIX separators in a machine-readable field. `str(PurePath)` on Windows yields
                # backslashes, so the same field would read differently on a different host and a
                # consumer matching on it would have to know the platform.
                envelope["interopFrom"] = source.relative_to(extract_dir).as_posix()
                envelope["interopFiles"] = copy_interop(source, interop_out)
                log("Interop ready from the zip URL")
            except Refusal:
                raise
            finally:
                if not args.keep_interop_extract and extract_dir.is_dir():
                    try:
                        _RMTREE(extract_dir)
                    except OSError:
                        pass
                if zip_path.is_file():
                    try:
                        zip_path.unlink()
                    except OSError:
                        pass
        # ── branch (c): already present under the refs tree
        elif marker_in(interop_out):
            envelope["interopSource"] = SOURCE_ALREADY_PRESENT
            envelope["interopFrom"] = str(interop_out)
            envelope["interopFiles"] = len(directory_files(interop_out))
            log(f"Using the interop already present under {interop_out}")
        # ── branch (d): the directory above the repository root
        elif not args.no_parent_lookup:
            parent = root.parent
            candidate = parent / "BepInEx" / "interop"
            if marker_in(candidate):
                envelope["interopSource"] = SOURCE_PARENT
                envelope["interopFrom"] = str(candidate)
                envelope["interopFiles"] = copy_interop(candidate, interop_out)
                log(f"Copied interop from the repository root's parent: {candidate}")
            else:
                envelope["parentLookup"] = f"checked {candidate}, no {MARKER}"
        else:
            envelope["parentLookup"] = "skipped by --no-parent-lookup"

        if not marker_in(interop_out):
            raise Refusal("NO-INTEROP",
                          f"no {MARKER} under {interop_out}. Set {GAME_DIR_ENV} to a legal game folder "
                          f"with BepInIn\\interop, or set {INTEROP_URL_ENV} to a private zip of those "
                          f"DLLs (interop is generated per game and is never committed). Tried: "
                          f"{envelope.get('parentLookup', 'nothing')}")

        envelope.update({"verdict": "OK", "exitCode": 0, "prepared": True,
                         "seconds": round(time.monotonic() - started, 3)})
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        log(f"Refs ready: {refs}")
        log(f"Interop ready: {interop_out}  ({envelope.get('interopFiles')} files, from "
            f"{envelope.get('interopSource')})")
        # On STDOUT, not stderr. This is the one line a caller can actually USE -- the value an Injector
        # build needs -- and the original's `Write-Host` put it on the information stream, where a
        # caller capturing output never sees it. `--json` already carries it as `gameDir`; this is for
        # a shell caller that wants a line it can read.
        print(f"{GAME_DIR_ENV}={refs}   # reported, NOT exported to this process's environment")
    return 0


def _resolve(root: Path, given: str, default: tuple[str, ...]) -> Path:
    path = Path(given).expanduser() if given else root.joinpath(*default)
    return (path if path.is_absolute() else Path.cwd() / path).resolve()


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        # ONE source of truth. The base payload used to carry `prepared: False` as well, and the
        # envelope's update overwrote it, so that literal was dead and a mutant flipping it was MASKED
        # rather than killed -- the identical defect found on the sibling fetcher port. Whichever source
        # there is, there is only one.
        payload.setdefault("prepared", False)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
