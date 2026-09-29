#!/usr/bin/env python3
"""Recycle one previously marked, still-valid linked worktree."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Sequence

import worktree_cleanup_core as core


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--permanent-delete", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--marker", required=True, help="marker id or marker filename")
    parser.add_argument("--confirm", required=True, help="confirmation token from the marker")
    parser.add_argument("--json", action="store_true")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.permanent_delete:
        print("worktree cleanup refused: --permanent-delete is not supported; cleanup uses the Recycle Bin", file=sys.stderr)
        return 2
    try:
        marker_name = args.marker if args.marker.endswith(".json") else args.marker + ".json"
        marker = core.read_marker(args.repo, marker_name)
        if marker.get("repoCommonDir") != str(core.common_dir(args.repo).resolve()):
            raise core.CleanupError("marker belongs to a different Git common directory")
        report = core.build_report(
            args.repo,
            marker["integrationRef"],
            marker.get("configuredUpstream"),
            marker.get("upstreamRefs", {}),
        )
        result = core.remove_worktree(args.repo, marker, report, args.confirm)
        if args.json:
            json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
            sys.stdout.write("\n")
        else:
            print(f"{result['state']} {result['worktree']['path']}")
        return 0
    except (core.CleanupError, OSError, ValueError) as exc:
        print(f"worktree cleanup refused: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
