#!/usr/bin/env python3
"""Dump ACS symbols for a GAME PROFILE -- the same reflection pass, pointed at the profile docs.

An entry point onto `dump_melon_p0.py`, not a second implementation. The original
`scripts/dump-game-profile.ps1` was a 14-line PowerShell file whose entire body was: resolve two
directories from the environment, call `dump-melon-p0.ps1`, then print a hint naming the PROFILE
documentation surface (`docs/research/game-types-*.md` and `game-profiles.json` fingerprints) rather
than the P0 surface the underlying tool hints at.

WHY IT IS RETIRED AS A SEPARATE FILE
-----------------------------------
* **IT FORWARDED NOTHING IT PROMISED.** It accepted `-ProfileId` and used it ONLY in the trailing
  `Write-Host`; the callee was never told the profile existed, so a dump taken "for pvzrh-3.9" was
  byte-identical to one taken for any other profile and the report could not say which it was about.
  Here `--profile-id` reaches the underlying tool and lands in its `--json` envelope, so the profile is
  part of the machine-readable result instead of a line of prose that nothing downstream reads.

* **IT COMPUTED THE SAME REPO ROOT A SECOND TIME, DIFFERENTLY.** `dump-melon-p0.ps1` fell back to
  a `Resolve-Path` over `..\\..`; this file's fallback for `BepGameDir` was `""` and the resolution
  happened at the CALL. Two spellings of one default, so a change to one was not a change to the other.

* **A CHILD PROCESS FOR AN IN-PROCESS CALL.** `& (Join-Path $PSScriptRoot "dump-melon-p0.ps1")` pays a
  PowerShell start-up and re-derives every path. This module imports the implementation.

WHAT THE UNDERLYING TOOL GETS RIGHT, AND THIS ENTRY POINT INHERITS
  every `dotnet` exit code checked, a bounded build, a per-run scratch directory, and a `--json`
  envelope. See `dump_melon_p0.py` for the full account of why the PowerShell form was retired; this
  file is the same tool aimed at the profile documentation.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import dump_melon_p0 as core  # noqa: E402 - the path insert above is what makes this importable

TOOL_ID = "dump-game-profile"

# The original defaulted the profile to "auto" here and to nothing in the underlying tool, so the hint
# always named something even when the caller knew nothing. Kept, because a hint that says "auto" tells
# the operator the profile was not supplied, where an empty string tells them nothing at all.
DEFAULT_PROFILE_ID = "auto"

PROFILE_HINT = ("Profile hint: {profile} -- update docs/research/game-types-*.md and "
                "game-profiles.json fingerprints.")


def main(argv: list[str] | None = None) -> int:
    """The same run as `dump_melon_p0.main`, pointed at the profile documentation.

    The exit code is the underlying tool's own, unwrapped. A caller scripting against this entry point
    must be able to tell a refusal (64) from a failed reflection (1) without parsing text, and a
    "wrapper" that mapped everything to 0 -- the habit the PowerShell form acquired by never checking
    `$LASTEXITCODE` -- would destroy the one thing the port is for.
    """
    return core.run(core.build_parser(TOOL_ID, DEFAULT_PROFILE_ID).parse_args(argv),
                    tool_id=TOOL_ID, profile_hint=PROFILE_HINT)


if __name__ == "__main__":
    sys.exit(main())
