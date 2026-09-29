#!/usr/bin/env python3
"""Build the Injector against a legal game (or FUSIONRPG_GAME_DIR) and cache DropIntoGame DLLs.

Replaces `sync-ci-drop-into-game.ps1`. Publishes a player pack, then copies ONLY the `DropIntoGame`
payloads into `artifacts/ci-drop-into-game` so a cloud release can ship the plugin without ever holding
`BepInEx/interop` game DLLs.

WHY THIS IS PORTED IN THE SAME CHANGE AS `publish-player.ps1`
The original invoked `publish-player.ps1` directly, so deleting that file would have left this one
calling a file that does not exist -- a break discovered only when a release was being cut. The program's
own order is "make every dispatcher interpreter-aware first"; for a caller this small, porting it is
cheaper than a transitional shim AND it removes another file from the population, which is the point.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **IT HAD NO TIMEOUT**, and it delegates to the longest-running pipeline in the repository.
* **`& (Join-Path $PSScriptRoot "publish-player.ps1")` DISCARDS THE PUBLISHER'S EXIT CODE.** A caller
  that ignores the exit code of the thing it exists to call is a caller that reports success over a failed
  publish -- and this script's very next check is for the artefact the publish produces, so a partial
  publish that still left a `FusionRpg.Injector.dll` from a PREVIOUS run would sail through. The port
  checks the exit code AND that the drop is newer than the publish started, which is the check that
  actually distinguishes "this publish produced it" from "an old one is still lying there".
* **`Remove-Item Env:FUSIONRPG_USE_CI_DROP` AFTER assigning `$env:FUSIONRPG_USE_CI_DROP = $null`** is two
  statements where one suffices, and the assignment is the one that would matter if the variable were
  absent. Only the removal is kept.

The README this writes is part of the DELIVERABLE, not a log line: it is what tells whoever finds this
cache next that game interop DLLs must never land in it.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import publish_player  # noqa: E402  (the sibling import must follow the path shim)

TOOL_ID = "sync-ci-drop-into-game"
EXIT_OK = 0
EXIT_FAILED = 1
CACHE_FILE_SUFFIXES = (".dll", ".json")
DEFAULT_VERSION = "0.1.0"
# How far a drop's mtime may fall short of the publish's stamp before it counts as left over. It absorbs
# filesystem timestamp rounding on both sides of the comparison; a genuinely stale drop is minutes or
# hours old, so this cannot let one through. See `sync` for the clock this exists to reconcile.
STALENESS_TOLERANCE_SECONDS = 2.0

README = """# CI DropIntoGame cache

Prebuilt FusionRpg plugin DLLs for GitHub Actions release when ``FUSIONRPG_INTEROP_ZIP_URL`` is unset.

Refresh after injector changes:

```
$env:FUSIONRPG_GAME_DIR = "<legal game with BepInEx\\interop>"
python scripts/sync_ci_drop_into_game.py
```

Do **not** put ``BepInEx\\interop`` game DLLs here.
"""


class Refusal(Exception):
    def __init__(self, stage: str, reason: str, detail: str = "") -> None:
        super().__init__(f"{stage}/{reason}: {detail}" if detail else f"{stage}/{reason}")
        self.stage = stage
        self.reason = reason
        self.detail = detail


def version_from_env(env: dict[str, str]) -> str:
    """`FUSIONRPG_VERSION` with one leading `v` stripped, else `0.1.0`.

    The default differs from the publisher's on purpose: this cache is a development artefact refreshed by
    a maintainer, and `publish-player`'s default is the SHIPPED version. Sharing one constant would make a
    local refresh stamp the cache with a player release number.
    """
    raw = (env.get("FUSIONRPG_VERSION") or "").strip()
    if not raw:
        return DEFAULT_VERSION
    return raw[1:] if raw[0] in "vV" else raw


@dataclass
class Report:
    version: str = ""
    published_at: float = 0.0
    drop: str = ""
    dest: str = ""
    cached: int = 0
    newest_cached: float = 0.0


def sync(root: Path, env: dict[str, str], timeout: int) -> Report:
    """Publish, then cache the DropIntoGame payloads. Raises rather than caching a stale drop."""
    version = version_from_env(env)
    drop = root / "dist" / "FusionRpg" / "DropIntoGame"
    dest = root / "artifacts" / "ci-drop-into-game"

    # THE STAMP COMES FROM THE FILESYSTEM, NOT FROM `time.time()`. The staleness check below compares a
    # publish's start against a file's mtime, and the first version took the start from the wall clock.
    # Those are DIFFERENT CLOCKS: `st_mtime` is recorded when the write is flushed, which can be a few
    # milliseconds BEHIND the `time.time()` sampled just before it. On a fast machine a drop published
    # microseconds after the stamp was therefore judged stale, and two contract cases failed
    # intermittently -- 6 of 8 runs and 3 of 8 respectively, on two different cases, which is the shape of
    # a real race rather than a bad assertion. Planting a stamp file and reading ITS mtime makes both
    # sides of the comparison come from one clock, and the file is written first so its mtime is a floor
    # the publish can only exceed.
    stamp = root / "artifacts" / ".sync-ci-drop-stamp"
    stamp.parent.mkdir(parents=True, exist_ok=True)
    stamp.write_bytes(b"")
    started = stamp.stat().st_mtime

    # FUSIONRPG_USE_CI_DROP is REMOVED, not blanked: this script exists to build the injector, and a
    # leftover `1` would make the publisher copy the cache it is about to overwrite.
    child = {k: v for k, v in env.items() if k != "FUSIONRPG_USE_CI_DROP"}
    child["FUSIONRPG_VERSION"] = version

    # The publisher's exit code is CHECKED, and its own `run()` raises on a non-zero exit, so a failed
    # publish stops here rather than leaving the cache-refresh looking like it worked.
    publish_player.publish(root, child, timeout)

    source_dll = drop / "FusionRpg.Injector.dll"
    if not source_dll.is_file():
        raise Refusal("cache", "DROP-MISSING", f"missing DropIntoGame after publish: {drop}")

    # THE STALENESS CHECK THE ORIGINAL LACKED. Verifying the artefact EXISTS is not enough: a previous run's
    # drop satisfies that too, so a publish that produced nothing would refresh the cache from the old
    # files and report success. Comparing against the stamp the publish started from is what makes the
    # check mean "this publish produced it".
    #
    # The tolerance is `STALENESS_TOLERANCE_SECONDS` and it is not a fudge factor: the two mtimes come
    # from one filesystem clock but are recorded at different instants, and a coarse timestamp on a
    # network or container filesystem can round the later one DOWN onto the earlier one. A drop that is
    # genuinely left over is measured in minutes or hours, not milliseconds, so a small tolerance removes
    # the rounding entirely without letting a real stale drop through.
    if source_dll.stat().st_mtime < started - STALENESS_TOLERANCE_SECONDS:
        raise Refusal("cache", "DROP-STALE",
                      f"{source_dll} predates the publish that just ran, so the DropIntoGame tree is "
                      f"left over from an earlier run and caching it would ship stale plugin binaries")

    if dest.exists():
        shutil.rmtree(dest)
    # The stamp has done its job; it is a measurement, not an artefact of the cache.
    stamp.unlink(missing_ok=True)
    dest.mkdir(parents=True)
    cached = 0
    newest = 0.0
    for item in sorted(drop.iterdir()):
        if item.is_file() and item.suffix.lower() in CACHE_FILE_SUFFIXES:
            shutil.copy2(item, dest / item.name)
            cached += 1
            newest = max(newest, item.stat().st_mtime)
    (dest / "README.md").write_text(README, encoding="utf-8")
    return Report(version=version, published_at=started, drop=str(drop), dest=str(dest), cached=cached,
                  newest_cached=newest)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Cache DropIntoGame DLLs for cloud releases (replaces sync-ci-drop-into-game.ps1).")
    parser.add_argument("--root", type=Path, default=None, help="the repository")
    parser.add_argument("--timeout", type=int, default=3600,
                        help="seconds per external command (default 3600; the original had none)")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    args = parser.parse_args(argv)
    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        report = sync(root, dict(os.environ), args.timeout)
    except (Refusal, publish_player.Refusal) as refusal:
        stage = getattr(refusal, "stage", "unknown")
        reason = getattr(refusal, "reason", type(refusal).__name__)
        detail = getattr(refusal, "detail", str(refusal))
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "stage": stage, "reason": reason,
                              "detail": detail}, indent=2))
        else:
            print(f"SYNC-CI-DROP REFUSED [{stage}]: {reason}", file=sys.stderr)
            if detail:
                print(f"  {detail}", file=sys.stderr)
        return EXIT_FAILED
    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK", **report.__dict__}, indent=2))
        return EXIT_OK
    print(f"Synced {report.dest}  ({report.cached} file(s), version {report.version})")
    for item in sorted(Path(report.dest).iterdir()):
        print(f"  {item.name:<44} {item.stat().st_size:>10}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
