#!/usr/bin/env python3
"""Mark linked worktrees that are proven complete for a later cleanup pass."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Sequence

import worktree_cleanup_core as core


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for command in ("mark", "show"):
        sub = subparsers.add_parser(command)
        sub.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
        sub.add_argument("--integration-ref", required=True)
        sub.add_argument("--upstream-ref", action="append", default=[], metavar="BRANCH=REFS/REMOTES/NAME")
        sub.add_argument("--json", action="store_true")
    revoke = subparsers.add_parser("revoke")
    revoke.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    revoke.add_argument("--marker", required=True)
    return parser


def _upstream_refs(values: list[str]) -> dict[str, str]:
    refs: dict[str, str] = {}
    for value in values:
        if "=" not in value:
            raise core.CleanupError(f"upstream mapping must be BRANCH=REFS/REMOTES/NAME: {value}")
        branch, ref = value.split("=", 1)
        if not branch or not ref.startswith("refs/remotes/"):
            raise core.CleanupError(f"upstream mapping must be BRANCH=REFS/REMOTES/NAME: {value}")
        if branch in refs and refs[branch] != ref:
            raise core.CleanupError(f"conflicting upstream mappings for {branch}")
        refs[branch] = ref
    return refs


def _print_report(report: dict, markers: list[dict]) -> None:
    print(f"Worktrees: {len(report['worktrees'])}  should-clean: {sum(m['state'] == core.MARKER_STATE_SHOULD_CLEAN for m in markers)}  manual-review: {sum(m['state'] == core.MARKER_STATE_MANUAL for m in markers)}")
    for marker in markers:
        item = marker["worktree"]
        state = marker["state"]
        print(f"\n{state} {item['path']} ({item.get('branch')}@{str(item.get('head', ''))[:12]})")
        print(f"  marker: {marker['markerId']}.json")
        if state == core.MARKER_STATE_SHOULD_CLEAN:
            print(f"  confirm: {marker['confirmationToken']}")
        else:
            blockers = ", ".join(marker.get("blockers", [])) or "no reason recorded"
            print(f"  blockers: {blockers}")
            changed = marker.get("evidence", {}).get("status", {}).get("changedPaths", [])
            if changed:
                print("  changed paths:")
                for changed_item in changed:
                    print(f"    {changed_item['status']} {changed_item['path']}")


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "revoke":
            marker_name = args.marker if args.marker.endswith(".json") else args.marker + ".json"
            core.remove_marker(args.repo, marker_name)
            print(f"revoked {marker_name}")
            return 0
        upstream_refs = _upstream_refs(args.upstream_ref)
        report = core.build_report(args.repo, args.integration_ref, None, upstream_refs)
        if args.command == "mark":
            markers = core.mark_report(args.repo, report)
        else:
            markers = []
            with core.marker_lock(args.repo):
                for stored in core.list_markers(args.repo):
                    current = core.load_report_item(report, Path(stored.get("worktree", {}).get("path", "")))
                    shown = dict(stored)
                    shown["state"] = core.effective_marker_state(stored, current)
                    markers.append(shown)
        if args.json:
            output = {"report": report, "markers": markers} if args.command == "mark" else markers
            json.dump(output, sys.stdout, indent=2, ensure_ascii=False)
            sys.stdout.write("\n")
        else:
            _print_report(report, markers)
        return 0
    except (core.CleanupError, OSError, ValueError) as exc:
        print(f"worktree cleanup marking failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
