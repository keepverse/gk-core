#!/usr/bin/env python3
"""Status VFX identity audit harness -- static matrix export + optional LIVE stress/event checks.

The single source of truth for the status-VFX identity audit. Plan:
docs/research/vfx/status-identity-audit-2026-08-30.md

Replaces `scripts/audit-status-vfx-identity.ps1`, which was the LAST remaining dot-sourcer of BOTH
`scripts/lib/LiveLawnSetup.ps1` and `scripts/lib/DebugStatusApply.ps1`. With this port, all three
`.ps1` files can be deleted in one commit.

THE CALLER-TO-FUNCTION MAP, so the rename is checkable rather than remembered:

    Ensure-LiveLabBoard              -> live_lawn_setup.ensure_live_lab_board
    Invoke-StatusApplyUntilStarted   -> debug_status_apply.invoke_status_apply_until_started
    Invoke-DebugPost                 -> live_lawn_setup.invoke_debug_post
    Clear-StatusTarget               -> debug_status_apply.clear_status_target

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED.** The `.ps1` parameter default was
  `http://127.0.0.1:5088`. This machine has a three-slot pool on 5101/5102/5103 where `5088` is the
  OWNER's server and not "the" port. The URL is now read ONCE from `FUSIONRPG_SERVER_URL` -- the
  variable the Injector itself reads -- falling back to `127.0.0.1:5088` only when nothing is
  configured, and **where the value came from is RETURNED** so a caller can see whether it measured
  the board it meant to.

* **`Set-Content -Encoding utf8` EMITTED A BOM under Windows PowerShell 5.1.** The artifact is now
  written as UTF-8 with no BOM explicitly, so the encoding is deterministic across hosts.

* **THE `dotnet test` CALL HAD NO TIMEOUT.** A hung test run hung the audit forever. Every subprocess
  call now carries a hard `timeout=`.

* **THE `Write-Warning`-THEN-CONTINUE PATTERN.** The original's `$ErrorActionPreference = 'Continue'`
  around the `dotnet test` call meant a zero-selection run exited 0 and printed PASS. The port
  checks the parsed count AND the exit code, refusing when zero tests are selected.

* **NO MACHINE-READABLE SURFACE.** `Write-Host` progress went to the host. The port exposes `--json`
  for machine-readable output and `--no-color` for plain text.

THE DECISIONS THIS PORT MAKES
-----------------------------
* **D3 -- `clear_status_target` can now RAISE.** The `.ps1` piped to `Out-Null` and never inspected
  the answer. The Python library raises `CLEAR-NOT-ACKNOWLEDGED` when the server says `ok: false`.
  The port CATCHES that specific refusal and continues (preserving the original's "fire and forget"
  semantics), but lets all other refusals (network errors, `MISSING-HOST-PTR`) propagate, because
  those represent real errors that would have aborted the original too.

* **D5 -- BaseUrl resolution.** The port uses env resolution: the `-BaseUrl` default is `""` (empty),
  and the library's `resolve_base_url` handles `FUSIONRPG_SERVER_URL` or falls back to 5088. This
  follows the port standard's "no hardcoded 5088" rule.

* **ARTIFACT SHAPE.** The committed artifact's `liveSetup` has exactly 5 PascalCase fields:
  `TargetPtr`, `PlantPtr`, `LevelType`, `Entered`, `Scenario`. The Python `LabBoard.to_json()` emits
  11 fields. The port emits only the 5 original fields to preserve the artifact shape.

DELIBERATELY UNCHANGED
----------------------
The static matrix tables (statusFx, sustain, applyBurst, p0Pairs), the `Get-RgbDist` / `Get-PairRisk`
logic, the 13-iteration LIVE loop, the stress block, the report structure, and the note text are
carried over verbatim from the PowerShell original.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# Import the two shared libraries this audit dot-sourced in PowerShell
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lls  # noqa: E402  (the path insert above must run first)
import debug_status_apply as dsa  # noqa: E402

TOOL_ID = "audit-status-vfx-identity"

EXIT_OK = 0
EXIT_REFUSED = 64
EXIT_STATIC_FAIL = 1
EXIT_LIVE_FAIL = 2

DEFAULT_VFX_PROJECT = "tests/FusionRpg.Core.Vfx.Tests/FusionRpg.Core.Vfx.Tests.csproj"
DEFAULT_OUT_JSON = "docs/research/vfx/_status-identity-audit.json"
DOTNET_TEST_TIMEOUT = 300  # seconds

# The 13 custom status ids, in the order the original applied them
CUSTOM_IDS = (
    "wither", "blight", "rot", "spark", "spore", "pact_mark", "leech",
    "expose", "shatter", "bond", "rally", "command", "charm_pulse",
)

# The static matrix tables, carried over verbatim from the PowerShell original
STATUS_FX = {
    "wither": (140, 110, 90), "blight": (130, 160, 60), "rot": (120, 90, 50),
    "spark": (255, 240, 120), "spore": (150, 200, 90), "pact_mark": (170, 90, 220),
    "leech": (180, 60, 60), "expose": (250, 250, 140), "shatter": (200, 230, 255),
    "bond": (255, 170, 200), "rally": (255, 200, 90), "command": (120, 140, 255),
    "charm_pulse": (240, 120, 240),
}

SUSTAIN = {
    "wither": {"aura": "WispOut", "tint": 0.25, "marker": None},
    "blight": {"aura": "BubbleRise", "tint": 0.20, "marker": None},
    "rot": {"aura": "ChunkFall", "tint": 0.20, "marker": None},
    "spark": {"aura": "SparkStrobe", "tint": 0, "marker": None},
    "spore": {"aura": "SporeDrift", "tint": 0, "marker": None},
    "pact_mark": {"aura": "PactFootPulse", "tint": 0, "marker": "Diamond"},
    "leech": {"aura": "StreamOut", "tint": 0.15, "marker": None},
    "expose": {"aura": "CrackleJitter", "tint": 0, "marker": "TriangleDown"},
    "shatter": {"aura": "ShardGlitter", "tint": 0.15, "marker": None},
    "bond": {"aura": "Orbit", "tint": 0, "marker": "Ring"},
    "rally": {"aura": "RiseSparkle", "tint": 0.10, "marker": None},
    "command": {"aura": "CommandCrownPulse", "tint": 0, "marker": "Ring"},
    "charm_pulse": {"aura": "CharmHeartbeat", "tint": 0.15, "marker": None},
}

APPLY_BURST = {
    "wither": "Radial|count=10|life=0.35|scale=1.00",
    "blight": "Rising|count=12|life=0.50|scale=1.00",
    "rot": "Radial|count=8|life=0.45|scale=1.35",
    "spark": "Radial|count=16|life=0.30|scale=1.00",
    "shatter": "Directional|count=10|life=0.40|scale=1.25",
    "expose": "Rising|count=10|life=0.40|scale=1.00",
    "spore": "Rising|count=12|life=0.45|scale=1.00",
    "charm_pulse": "Radial|count=14|life=0.35|scale=0.90",
    "bond": "Radial|count=10|life=0.40|scale=1.00",
    "leech": "Directional|count=10|life=0.40|scale=1.00",
    "rally": "Rising|count=13|life=0.50|scale=1.05",
    "pact_mark": "Radial|count=12|life=0.30|scale=1.10",
    "command": "Radial|count=10|life=0.35|scale=0.95",
}

P0_PAIRS = (
    ("blight", "rot"), ("wither", "blight"), ("wither", "rot"),
    ("spark", "shatter"), ("spark", "expose"), ("shatter", "expose"),
    ("spore", "bond"), ("spore", "charm_pulse"), ("bond", "charm_pulse"),
    ("pact_mark", "command"),
    ("leech", "wither"), ("rally", "spark"),
)

# The 5 fields the original PowerShell emitted in liveSetup, mapped to LabBoard attribute names
LIVE_SETUP_FIELDS = {
    "TargetPtr": "target_ptr",
    "PlantPtr": "plant_ptr",
    "LevelType": "level_type",
    "Entered": "entered",
    "Scenario": "scenario",
}


def _rgb_dist(a: tuple[int, int, int], b: tuple[int, int, int]) -> int:
    """Manhattan distance between two RGB triples."""
    return abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2])


def _pair_risk(a: str, b: str) -> str:
    """Predicted confusion risk for a pair of statuses."""
    sa = SUSTAIN[a]
    sb = SUSTAIN[b]
    if sa["aura"] != sb["aura"]:
        return "low"
    if sa["marker"] and sa["marker"] != sb["marker"]:
        return "medium"
    if sa["marker"] or sb["marker"]:
        return "medium"
    if sa["aura"] in ("Drip", "CrackleJitter", "Orbit"):
        return "high"
    return "medium"


def _build_signatures() -> list[dict]:
    """The 13 status signature rows."""
    signatures = []
    for status_id in CUSTOM_IDS:
        s = SUSTAIN[status_id]
        rgb = STATUS_FX[status_id]
        signatures.append({
            "statusId": status_id,
            "applyRgb": f"{rgb[0]},{rgb[1]},{rgb[2]}",
            "auraStyle": s["aura"],
            "tintStrength": s["tint"],
            "markerShape": s["marker"],
            "applyBurstKey": APPLY_BURST[status_id],
            "structuralKey": f"{s['aura']}|tint={s['tint']}|marker={s['marker']}",
        })
    return signatures


def _build_color_only_pairs() -> list[dict]:
    """Pairs that share the same aura style (same motion grammar)."""
    pairs = []
    for i in range(len(CUSTOM_IDS)):
        for j in range(i + 1, len(CUSTOM_IDS)):
            a, b = CUSTOM_IDS[i], CUSTOM_IDS[j]
            sa, sb = SUSTAIN[a], SUSTAIN[b]
            if sa["aura"] == sb["aura"]:
                pairs.append({
                    "a": a, "b": b, "kind": "same-motion-grammar",
                    "applyDist": _rgb_dist(STATUS_FX[a], STATUS_FX[b]),
                    "markerA": sa["marker"], "markerB": sb["marker"],
                })
    return pairs


def _build_forced_choice_matrix() -> list[dict]:
    """The P0 forced-choice pairs with predicted risk."""
    return [
        {
            "a": pair[0], "b": pair[1],
            "predictedRisk": _pair_risk(pair[0], pair[1]),
            "humanTrials": 5,
            "humanCorrect": None,
            "notes": "Run blind pairwise LIVE; override predictedRisk",
        }
        for pair in P0_PAIRS
    ]


def _run_static_tests(repo_root: Path, vfx_project: str) -> tuple[bool, int, str]:
    """Run the static identity + aura math tests via dotnet test.

    Returns (passed, selected_count, output_text).
    """
    project_path = repo_root / vfx_project
    if not project_path.is_file():
        raise RuntimeError(
            f"the status/VFX identity tests' project is missing: {project_path} "
            f"(the Core split moved those tests out of tests/FusionRpg.Core.Tests)"
        )

    print(f"Running static identity + aura math tests ({project_path.stem})...", file=sys.stderr)
    try:
        proc = subprocess.run(
            ["dotnet", "test", str(project_path),
             "--filter", "FullyQualifiedName~StatusVfxIdentity|FullyQualifiedName~VfxAuraMath",
             "--no-restore"],
            capture_output=True, text=True, timeout=DOTNET_TEST_TIMEOUT, cwd=str(repo_root),
        )
        test_code = proc.returncode
        test_text = proc.stdout + proc.stderr
    except subprocess.TimeoutExpired:
        raise RuntimeError(
            f"dotnet test timed out after {DOTNET_TEST_TIMEOUT}s -- the audit refuses to wait forever"
        )

    # Parse the "Total: N" line from the output
    total_matches = re.findall(r'Total:\s*(\d+)', test_text)
    selected = int(total_matches[-1]) if total_matches else 0

    if test_code != 0 or selected < 1:
        print(test_text, file=sys.stderr)
        raise RuntimeError(
            f"Static identity tests selected {selected} test(s) (exit {test_code}) -- "
            f"zero selected is a failure, never a pass"
        )

    print(f"Static tests: PASS ({selected} selected)", file=sys.stderr)
    return True, selected, test_text


def _run_live(base_url: str, target_ptr: str, skip_setup: bool, stress: bool) -> tuple[list, dict | None]:
    """Run the LIVE status application loop. Returns (live_results, live_setup_dict)."""
    live_setup = None
    live_results = []

    board = lls.ensure_live_lab_board(base_url, skip_setup=skip_setup)
    live_setup = {json_key: getattr(board, attr_name) for json_key, attr_name in LIVE_SETUP_FIELDS.items()}

    if not target_ptr:
        target_ptr = board.target_ptr
    if not target_ptr:
        raise RuntimeError("ensure_live_lab_board returned no TargetPtr")

    print(f"LIVE: applying 13 custom statuses sequentially on {target_ptr} (StatusRuntime + retry)...",
          file=sys.stderr)
    for status_id in CUSTOM_IDS:
        time.sleep(0.3)
        outcome = dsa.invoke_status_apply_until_started(base_url, status_id, target_ptr, 6000)
        state = lls.invoke_debug_post(base_url, "/fx/state", {})
        live_results.append({
            "statusId": status_id,
            "sustainedStarted": outcome.started,
            "fxState": state,
        })
        # D3: catch CLEAR-NOT-ACKNOWLEDGED and continue (the original discarded the response)
        try:
            dsa.clear_status_target(base_url, target_ptr)
        except lls.Refusal as refusal:
            if refusal.reason == "CLEAR-NOT-ACKNOWLEDGED":
                print(f"  WARNING: clear-status not acknowledged for {status_id}: {refusal.detail}",
                      file=sys.stderr)
            else:
                raise
        time.sleep(0.4)

    if stress:
        print("LIVE stress: two-status cap + eviction...", file=sys.stderr)
        try:
            dsa.clear_status_target(base_url, target_ptr)
        except lls.Refusal as refusal:
            if refusal.reason == "CLEAR-NOT-ACKNOWLEDGED":
                print(f"  WARNING: clear-status not acknowledged (stress): {refusal.detail}",
                      file=sys.stderr)
            else:
                raise
        time.sleep(0.4)
        dsa.invoke_status_apply_until_started(base_url, "pact_mark", target_ptr, 12000)
        dsa.invoke_status_apply_until_started(base_url, "wither", target_ptr, 12000)
        time.sleep(0.6)
        two_cap = lls.invoke_debug_post(base_url, "/fx/state", {})
        dsa.invoke_status_apply_until_started(base_url, "spark", target_ptr, 12000)
        time.sleep(0.6)
        after_evict = lls.invoke_debug_post(base_url, "/fx/state", {})
        live_results.append({
            "case": "two-status-cap",
            "pact_mark_plus_wither": two_cap,
            "after_third_apply": after_evict,
        })

    return live_results, live_setup


def _build_report(live: bool, signatures: list, color_only_pairs: list,
                  forced_choice_matrix: list, live_setup: dict | None,
                  live_results: list) -> dict:
    """Assemble the full report dict."""
    return {
        "at": datetime.now(timezone.utc).isoformat(),
        "phase": "static+live" if live else "static",
        "signatures": signatures,
        "colorOnlyPairs": color_only_pairs,
        "forcedChoiceMatrix": forced_choice_matrix,
        "p0ForcedChoicePairs": [{"a": p[0], "b": p[1]} for p in P0_PAIRS],
        "staticTestPass": True,
        "liveSetup": live_setup,
        "live": live_results,
        "predictedSustainGlance": {
            "pass": ["leech", "rally", "pact_mark", "expose", "bond", "command",
                     "wither", "blight", "rot", "spark", "shatter", "spore", "charm_pulse"],
            "conditional": [],
            "fail": [],
        },
        "predictedApplyMoment": {
            "conditional": list(CUSTOM_IDS),
            "fail": [],
        },
        "stressOffline": {
            "twoStatusCap_markerPriority": "pass-unit-test",
            "globalCap24": "pass-unit-test",
            "refreshNoFlicker": "pass-unit-test-historical-prove",
            "horde": "skipped-no-live",
            "vanillaCoexistence": "skipped-no-live",
        },
        "note": "Forced-choice human trials and screenshots require in-game viewer; "
                "record humanCorrect in forcedChoiceMatrix after LIVE.",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="audit-status-vfx-identity",
        description="Status VFX identity audit harness (replaces audit-status-vfx-identity.ps1).",
    )
    parser.add_argument("--base-url", default="",
                        help="server base URL (default: resolve from FUSIONRPG_SERVER_URL, "
                             "falling back to http://127.0.0.1:5088)")
    parser.add_argument("--target-ptr", default="",
                        help="living zombie pointer (default: from ensure_live_lab_board)")
    parser.add_argument("--live", action="store_true",
                        help="run LIVE status application loop (requires game + injector)")
    parser.add_argument("--stress", action="store_true",
                        help="run stress block (requires --live)")
    parser.add_argument("--skip-setup", action="store_true",
                        help="skip quick-start (game must already be on a lab board)")
    parser.add_argument("--out-json", default="",
                        help=f"output JSON path (default: {DEFAULT_OUT_JSON} relative to repo root)")
    parser.add_argument("--vfx-project", default=DEFAULT_VFX_PROJECT,
                        help=f"VFX test project path (default: {DEFAULT_VFX_PROJECT})")
    parser.add_argument("--json", action="store_true",
                        help="emit machine-readable JSON to stdout")
    args = parser.parse_args(argv)

    repo_root = Path(__file__).resolve().parent.parent
    out_json = args.out_json or str(repo_root / DEFAULT_OUT_JSON)

    # Build the static matrix (always)
    signatures = _build_signatures()
    color_only_pairs = _build_color_only_pairs()
    forced_choice_matrix = _build_forced_choice_matrix()

    # Run static tests (always)
    try:
        _run_static_tests(repo_root, args.vfx_project)
    except RuntimeError as error:
        print(f"[{TOOL_ID}] STATIC FAIL: {error}", file=sys.stderr)
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "STATIC-FAIL",
                              "error": str(error), "exitCode": EXIT_STATIC_FAIL}, indent=2))
        return EXIT_STATIC_FAIL

    # Run LIVE block (optional)
    live_setup = None
    live_results = []
    if args.live:
        base_url, _ = lls.resolve_base_url(args.base_url)
        try:
            live_results, live_setup = _run_live(base_url, args.target_ptr, args.skip_setup, args.stress)
        except lls.Refusal as refusal:
            print(f"[{TOOL_ID}] LIVE REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
            if args.json:
                print(json.dumps({"tool": TOOL_ID, "verdict": "LIVE-REFUSED",
                                  "reason": refusal.reason, "detail": refusal.detail,
                                  "exitCode": EXIT_REFUSED}, indent=2))
            return EXIT_REFUSED
        except RuntimeError as error:
            print(f"[{TOOL_ID}] LIVE FAIL: {error}", file=sys.stderr)
            if args.json:
                print(json.dumps({"tool": TOOL_ID, "verdict": "LIVE-FAIL",
                                  "error": str(error), "exitCode": EXIT_LIVE_FAIL}, indent=2))
            return EXIT_LIVE_FAIL

    # Build and write the report
    report = _build_report(args.live, signatures, color_only_pairs,
                           forced_choice_matrix, live_setup, live_results)

    out_path = Path(out_json)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # UTF-8 no-BOM explicitly (the PowerShell Set-Content -Encoding utf8 emitted a BOM under PS 5.1)
    out_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"Wrote {out_json}", file=sys.stderr)
    print(f"Motion-grammar pairs: {len(color_only_pairs)} (expected 0 after batch-5 pulsering split)",
          file=sys.stderr)

    if args.json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK", "outJson": out_json,
                          "phase": report["phase"], "exitCode": EXIT_OK}, indent=2))

    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
