#!/usr/bin/env python3
"""Download the official BepInEx Unity IL2CPP win-x64 zip for REFERENCE ASSEMBLES ONLY (CI / path-free
builds). It does not install into any game folder.

Replaces `scripts/fetch-bepinex-refs.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **NEITHER NETWORK CALL WAS BOUNDED.** Two calls -- `Invoke-RestMethod` for the release metadata and
  `Invoke-WebRequest` for the asset -- with no timeout on either. Measured on this repository: a real
  run of the original took **1443 seconds** to fetch a 34 MB asset. That is not a slow network, it is
  a call with nothing stopping it, and there was no way to tell "slow" from "hung" from "never coming
  back" except by killing the shell. Both calls here carry an explicit timeout and a distinct refusal
  per call, so a hang is a named outcome rather than an indistinguishable wait.

* **IT DESTROYED PROVEN-GOOD STATE BEFORE PROVING THE REPLACEMENT.** The original ran
  `Remove-Item $OutDir -Recurse -Force` as its third statement, *before* the API call. So a rate-limited
  API, a typo'd tag, or a dropped connection left the machine with NO reference assemblies at all --
  the exact state the tool exists to prevent. The fetch here happens into a fresh staging directory and
  is only swapped in once the tree has been verified to contain `BepInEx/core`, so a failed run leaves
  the previous tree intact.

* **`Select-Object -First 1` ON AN ASSET MATCH, SILENTLY.** Six of the thirteen assets in the real
  release differ only by platform, and the first match was taken without ever reporting that others
  matched. The same shape at the archive level: `Get-ChildItem $OutDir -Directory | Select-Object -First 1`
  picked an arbitrary directory when the archive had several, and if that one happened to be the wrong
  one the script did nothing and fell through to a generic throw. Both refusals here name the
  candidates they actually saw.

* **A FAILED DOWNLOAD LANDED AS A FILE NAMED `.zip`.** With no `Content-Type` or magic-byte check, a
  GitHub rate-limit page saved to the zip path failed later inside `Expand-Archive` with a message
  about a compression method. The bytes are checked for the ZIP magic before extraction, so the refusal
  names the download.

* **A MISSING `BepInEx/core` REFUSED WITHOUT SAYING WHAT IT FOUND.** The original's final `throw` named
  only the output directory. This refusal lists the top-level entries the archive actually produced,
  because "missing BepInEx\\core" with no evidence is the least actionable message a fetch tool can give.

* **NO MACHINE-READABLE VERDICT, AND A `Write-Host` PROGRESS LOG ON THE INFORMATION STREAM.** Progress
  goes to stderr; the verdict is `--json`.

DELIBERATELY UNCHANGED
----------------------
The same default tag (`v6.0.0-pre.2`, overridable by `BEPINEX_REF_TAG` read ONCE at startup), the same
asset pattern, the same two output paths, the same flatten-if-nested behaviour, and the same closing
note -- that `Assembly-CSharp` interop is per-game and still needs `FUSIONRPG_GAME_DIR` or a CI cache.
`User-Agent` is still sent: GitHub's API rejects a request without one, and it is a plain HTTP header
rather than anything identifying a tool.

WHAT THIS DOES NOT DO
---------------------
It does not install, and it does not verify the refs against the game. Reference assemblies are for
compiling; the interop assemblies the Injector build actually links are generated per-game.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

TOOL_ID = "fetch-bepinex-refs"

EXIT_REFUSED = 64

DEFAULT_TAG = "v6.0.0-pre.2"
TAG_ENV = "BEPINEX_REF_TAG"
ASSET_PATTERN = "BepInEx-Unity\\.IL2CPP-win-x64"
API = "https://api.github.com/repos/BepInEx/BepInEx/releases/tags/{tag}"
MARKER = "BepInEx/core"
USER_AGENT = "FusionRpg-fetch-bepinex-refs/1.0"
ACCEPT = "application/vnd.github+json"

# ZIP local-file-header magic. Checked before extraction so a GitHub rate-limit page saved as the
# download is refused as a bad download, not later as a compression-method error.
ZIP_MAGIC = b"PK\x03\x04"

DEFAULT_OUT_DIR = ("artifacts", "bepinex-refs")
DEFAULT_ZIP = ("artifacts", "bepinex-il2cpp.zip")

DEFAULT_API_TIMEOUT = 60
DEFAULT_DOWNLOAD_TIMEOUT = 1800

# Closed vocabulary. A case in the contract suite asserts every reason the tool can raise is in here,
# so a new refusal cannot appear without being declared.
REFUSAL_REASONS = {
    "TAG-ENV-EMPTY", "ASSET-PATTERN-INVALID", "INVALID-TIMEOUT", "API-TIMED-OUT",
    "API-REFUSED", "API-MALFORMED", "ASSET-NOT-FOUND", "ASSET-AMBIGUOUS", "DOWNLOAD-TIMED-OUT",
    "DOWNLOAD-REFUSED", "NOT-A-ZIP", "ZIP-UNREADABLE", "EXTRACT-FAILED", "STAGING-FAILED",
    "NO-BEPINEX-CORE", "MULTIPLE-CANDIDATE-DIRS", "SWAP-FAILED",
}

# Bound once, module-private. `subprocess`/`shutil`/`urllib` are process-wide modules; a test that
# patches one of them reaches every other test in the project.
_URLOPEN = urllib.request.urlopen
_RMTREE = shutil.rmtree


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def log(message: str) -> None:
    """Progress on stderr. stdout carries the verdict, so `--json` output stays parseable on its own."""
    print(f"==> {message}", file=sys.stderr)


def fetch_json(url: str, timeout: int) -> dict:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": ACCEPT})
    try:
        with _URLOPEN(request, timeout=timeout) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        raise Refusal("API-REFUSED",
                      f"the GitHub API answered {error.code} for {url}: {error.reason}. A 403 here is "
                      f"almost always the unauthenticated rate limit, so an unauthenticated retry will "
                      f"not help") from error
    except TimeoutError as expired:
        raise Refusal("API-TIMED-OUT",
                      f"the GitHub API did not answer within {timeout}s. The original had no timeout "
                      f"here, so this call could not distinguish a slow answer from a hung one") from expired
    except urllib.error.URLError as error:
        reason = error.reason
        if isinstance(reason, TimeoutError):
            raise Refusal("API-TIMED-OUT",
                          f"the GitHub API did not answer within {timeout}s: {reason}") from error
        raise Refusal("API-REFUSED", f"the GitHub API was unreachable for {url}: {reason}") from error
    except OSError as error:
        raise Refusal("API-REFUSED", f"the GitHub API was unreachable for {url}: {error}") from error
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("API-MALFORMED",
                      f"the GitHub API answered with something that is not JSON: {error}") from error
    if not isinstance(document, dict):
        raise Refusal("API-MALFORMED",
                      f"the GitHub API answered with a {type(document).__name__}, not the release object")
    return document


def select_asset(release: dict, pattern: re.Pattern[str]) -> dict:
    """The ONE matching asset. Zero is a refusal; more than one is a refusal, because the original took
    the first silently and the six platform variants in the real release differ by two characters."""
    assets = release.get("assets")
    if not isinstance(assets, list):
        raise Refusal("API-MALFORMED", f"the release has no 'assets' array: {sorted(release)[:8]}")
    matches = [a for a in assets if isinstance(a, dict)
               and isinstance(a.get("name"), str) and pattern.search(a["name"])]
    names = sorted(a["name"] for a in assets if isinstance(a, dict) and isinstance(a.get("name"), str))
    if not matches:
        raise Refusal("ASSET-NOT-FOUND",
                      f"no asset in this release matches {pattern.pattern!r}. The release has "
                      f"{len(names)} asset(s): {names}")
    if len(matches) > 1:
        raise Refusal("ASSET-AMBIGUOUS",
                      f"{len(matches)} assets match {pattern.pattern!r} "
                      f"({sorted(a['name'] for a in matches)}); the original took the first one without "
                      f"saying so. Narrow the pattern rather than let this pick")
    chosen = matches[0]
    url = chosen.get("browser_download_url")
    if not isinstance(url, str) or not url:
        raise Refusal("API-MALFORMED", f"the matching asset has no browser_download_url: {chosen.get('name')!r}")
    return chosen


def download(url: str, destination: Path, timeout: int) -> int:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": ACCEPT})
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
        raise Refusal("DOWNLOAD-TIMED-OUT",
                      f"the asset download did not finish within {timeout}s; {written} bytes had been "
                      f"written to {destination}. A real run of the original took 1443s for this asset, "
                      f"so raise --download-timeout rather than treat this as a hang") from expired
    except urllib.error.HTTPError as error:
        raise Refusal("DOWNLOAD-REFUSED", f"the asset download answered {error.code}: {error.reason}") from error
    except urllib.error.URLError as error:
        reason = error.reason
        if isinstance(reason, TimeoutError):
            raise Refusal("DOWNLOAD-TIMED-OUT",
                          f"the asset download did not finish within {timeout}s: {reason}") from error
        raise Refusal("DOWNLOAD-REFUSED", f"the asset download was unreachable: {reason}") from error
    except OSError as error:
        raise Refusal("DOWNLOAD-REFUSED", f"the asset download failed for {url}: {error}") from error
    return written


def require_zip(path: Path, written: int) -> None:
    """The bytes must be a ZIP. Otherwise a rate-limit page is about to be extracted as one."""
    if not path.is_file():
        raise Refusal("NOT-A-ZIP", f"the download wrote {written} bytes but there is no file at {path}")
    head = path.open("rb").read(4)
    if head != ZIP_MAGIC:
        raise Refusal("NOT-A-ZIP",
                      f"{path} starts with {head!r}, not the ZIP magic {ZIP_MAGIC!r}. A GitHub rate-limit "
                      f"or error page is the usual cause, and the original would have carried it into "
                      f"Expand-Archive as a compression-method error")
    if not zipfile.is_zipfile(path):
        raise Refusal("NOT-A-ZIP", f"{path} has a ZIP header but is not a readable archive")


def extract(zip_path: Path, staging: Path) -> None:
    try:
        with zipfile.ZipFile(zip_path) as archive:
            archive.extractall(staging)
    except (zipfile.BadZipFile, OSError, RuntimeError) as error:
        raise Refusal("EXTRACT-FAILED", f"extracting {zip_path} into {staging} failed: {error}") from error


def flatten_if_nested(staging: Path) -> str:
    """Bring a single-wrapper archive's contents up one level, as the original did. A tree that already
    has the marker is left alone; a tree with several candidate directories is a refusal, not a guess."""
    if (staging / MARKER).is_dir():
        return "already flat"
    candidates = sorted(p for p in staging.iterdir() if p.is_dir())
    if not candidates:
        raise Refusal("NO-BEPINEX-CORE",
                      f"{staging} extracted to no directories at all, so {MARKER} cannot be present")
    if len(candidates) > 1:
        raise Refusal("MULTIPLE-CANDIDATE-DIRS",
                      f"{staging} has no {MARKER} at its root and holds {len(candidates)} directories "
                      f"({[p.name for p in candidates]}); the original moved the first one it found. "
                      f"Refusing to guess which is the payload")
    inner = candidates[0]
    if not (inner / MARKER).is_dir():
        raise Refusal("NO-BEPINEX-CORE",
                      f"{staging} is wrapped in {inner.name!r}, but that directory has no {MARKER} "
                      f"either. Its entries are {sorted(p.name for p in inner.iterdir())[:12]}")
    for child in sorted(inner.iterdir()):
        shutil.move(str(child), str(staging / child.name))
    try:
        _RMTREE(inner)
    except OSError as error:
        raise Refusal("EXTRACT-FAILED", f"removing the emptied wrapper {inner} failed: {error}") from error
    return f"flattened {inner.name}"


def digest_tree(root: Path) -> str:
    """A content digest of the tree, so `--json` reports WHAT was produced rather than that something
    was. Over file contents, not names, because names alone cannot distinguish a truncated asset."""
    hasher = hashlib.sha256()
    for path in sorted(p for p in root.rglob("*") if p.is_file()):
        hasher.update(path.relative_to(root).as_posix().encode("utf-8"))
        hasher.update(b"\0")
        hasher.update(hashlib.sha256(path.read_bytes()).digest())
    return hasher.hexdigest()[:32]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="fetch-bepinex-refs",
        description="Fetch the BepInEx Unity IL2CPP win-x64 zip for reference assemblies "
                    "(replaces fetch-bepinex-refs.ps1). Does not install into any game folder.")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--api-base", default="https://api.github.com",
                        help="base for the release-metadata URL. Exists so the contract suite can drive a "
                             "real HTTP server over loopback instead of the internet: a stubbed socket "
                             "cannot exercise a timeout, and a network-dependent test is either slow or "
                             "skipped (default: %(default)s)")
    parser.add_argument("--tag", default=None,
                        help=f"release tag (default: ${TAG_ENV}, else {DEFAULT_TAG!r})")
    parser.add_argument("--asset-pattern", default=ASSET_PATTERN,
                        help="regex the asset name must match (default: %(default)r)")
    parser.add_argument("--out-dir", default="",
                        help=f"where the tree is published (default: {'/'.join(DEFAULT_OUT_DIR)})")
    parser.add_argument("--zip", default="",
                        help=f"staging download path, removed afterwards "
                             f"(default: {'/'.join(DEFAULT_ZIP)})")
    parser.add_argument("--api-timeout", type=int, default=DEFAULT_API_TIMEOUT,
                        help=f"seconds for the release-metadata call (default {DEFAULT_API_TIMEOUT})")
    parser.add_argument("--download-timeout", type=int, default=DEFAULT_DOWNLOAD_TIMEOUT,
                        help=f"seconds for the asset download (default {DEFAULT_DOWNLOAD_TIMEOUT})")
    parser.add_argument("--keep-zip", action="store_true",
                        help="leave the staging download on disk instead of removing it")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON on stdout")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    for name, value in (("--api-timeout", args.api_timeout), ("--download-timeout", args.download_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
    if not args.asset_pattern:
        return _refuse("ASSET-PATTERN-INVALID", "--asset-pattern must not be empty", args.json)
    try:
        pattern = re.compile(args.asset_pattern)
    except re.error as error:
        return _refuse("ASSET-PATTERN-INVALID",
                       f"--asset-pattern {args.asset_pattern!r} is not a valid regex: {error}", args.json)

    # Read ONCE, explicitly, and refuse loudly when the variable is present but empty. `if $env:X` in
    # the original treated an empty string as absent, so a typo'd empty export silently fetched a
    # different tag than the caller asked for.
    tag = args.tag
    if tag is None:
        raw_env = os.environ.get(TAG_ENV)
        if raw_env is None:
            tag = DEFAULT_TAG
            tag_source = f"default ({TAG_ENV} unset)"
        elif raw_env.strip():
            tag = raw_env.strip()
            tag_source = f"${TAG_ENV}"
        else:
            return _refuse("TAG-ENV-EMPTY",
                           f"${TAG_ENV} is set but empty. The original treated that as unset and fetched "
                           f"{DEFAULT_TAG!r} instead, so a typo'd export silently got a different tag. "
                           f"Pass --tag, or unset {TAG_ENV}", args.json)
    else:
        tag_source = "--tag"

    root = Path(args.root).expanduser().resolve() if args.root else Path(__file__).resolve().parent.parent
    out_dir = _resolve(root, args.out_dir, DEFAULT_OUT_DIR)
    zip_path = _resolve(root, args.zip, DEFAULT_ZIP)

    started = time.monotonic()
    envelope: dict = {"tool": TOOL_ID, "tag": tag, "tagSource": tag_source,
                      "assetPattern": args.asset_pattern, "outDir": str(out_dir),
                      "published": False, "replacedExisting": out_dir.is_dir()}
    staging = zip_path.with_name(zip_path.stem + ".staging")
    try:
        try:
            if staging.is_dir():
                _RMTREE(staging)
        except OSError as error:
            raise Refusal("STAGING-FAILED", f"could not clear the staging directory {staging}: {error}") from error
        zip_path.parent.mkdir(parents=True, exist_ok=True)

        log(f"Fetching release {tag}")
        release = fetch_json(API.format(tag=tag).replace("https://api.github.com", args.api_base.rstrip("/")),
                             args.api_timeout)
        asset = select_asset(release, pattern)
        envelope["assetName"] = asset.get("name")
        envelope["assetSize"] = asset.get("size")

        log(f"Downloading {asset['name']} ({asset.get('size')} bytes)")
        written = download(asset["browser_download_url"], zip_path, args.download_timeout)
        envelope["downloadedBytes"] = written
        require_zip(zip_path, written)

        log(f"Extracting into staging {staging}")
        try:
            staging.mkdir(parents=True, exist_ok=True)
        except OSError as error:
            raise Refusal("STAGING-FAILED", f"could not create the staging directory {staging}: {error}") from error
        extract(zip_path, staging)
        # flatten_if_nested is also the marker post-condition: it returns only when BepInEx/core is
        # present, and raises NO-BEPINEX-CORE otherwise. A separate require_marker() used to follow this
        # line and could not fire -- falsification proved that by deleting it and watching two mutants
        # survive, so the dead call is gone rather than defended.
        envelope["flatten"] = flatten_if_nested(staging)

        # Only now is the existing tree replaced. The original deleted it as its third statement, so a
        # rate-limited API or a typo'd tag left the machine with no reference assemblies at all.
        tree_digest = digest_tree(staging)
        files = sum(1 for p in staging.rglob("*") if p.is_file())
        if out_dir.is_dir():
            try:
                _RMTREE(out_dir)
            except OSError as error:
                raise Refusal("SWAP-FAILED", f"could not remove the previous tree {out_dir}: {error}") from error
        try:
            staging.replace(out_dir)
        except OSError as error:
            raise Refusal("SWAP-FAILED",
                          f"the verified tree {staging} could not be published at {out_dir}: {error}") from error
        if not args.keep_zip:
            try:
                zip_path.unlink()
            except OSError as error:
                # Not fatal: the tree is published and verified. Reported, never silent.
                envelope["zipRemoved"] = False
                envelope["zipRemovalError"] = str(error)
            else:
                envelope["zipRemoved"] = True
        else:
            envelope["zipRemoved"] = False

        # `published` is re-asserted here rather than relying on the optimistic value seeded into the
        # envelope before the work began. The first version left it at False, so a SUCCESSFUL run
        # reported `published: false` in its own machine-readable verdict -- the one field a caller
        # would use to decide whether the tree is there. Found by the loopback fixture, not by reading.
        envelope.update({"verdict": "OK", "exitCode": 0, "published": True, "files": files,
                         "marker": MARKER, "treeDigest": tree_digest,
                         "seconds": round(time.monotonic() - started, 3)})
    except Refusal as refusal:
        _discard(staging, zip_path, args.keep_zip)
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)

    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        log(f"Refs ready: {out_dir}  ({files} files, {MARKER} verified, digest {tree_digest})")
        log("Note: Assembly-CSharp interop is generated per game; it still needs FUSIONRPG_GAME_DIR or a CI "
            "cache of interop. This tool fetches reference assemblies only.")
    return 0


def _resolve(root: Path, given: str, default: tuple[str, ...]) -> Path:
    path = Path(given).expanduser() if given else root.joinpath(*default)
    return (path if path.is_absolute() else Path.cwd() / path).resolve()


def _discard(staging: Path, zip_path: Path, keep_zip: bool) -> None:
    """A refused run leaves nothing of its own behind, and above all does NOT touch a published tree."""
    for path in (staging,):
        if path.is_dir():
            try:
                _RMTREE(path)
            except OSError:
                pass
    if not keep_zip and zip_path.is_file():
        try:
            zip_path.unlink()
        except OSError:
            pass


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        # ONE source of truth. The base payload used to carry `published: False` as well, and the
        # envelope's update overwrote it, so that literal was dead and a mutant flipping it was MASKED
        # rather than killed. Whichever source there is, it is the only one.
        payload.setdefault("published", False)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
