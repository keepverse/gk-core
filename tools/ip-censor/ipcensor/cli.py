"""Argument wiring for `python -m ipcensor.report <verb>`.

`report` is the composition root; this module is only the command line. It resolves the root once
(explicit `--root`, else the git working tree) and passes it down, so no verb depends on the process
working directory.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence

from ipcensor import curate, llm, report
from ipcensor.registry import RegistryError
from ipcensor.scan import BUCKETS
from ipcensor.suggest import PROPOSAL_MARKER

EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_ERROR = 2

MARKS_PATH = "data/seed/ip-censor/_registry/marks.v1.json"

SUGGEST_PROMPT = (
    "You propose one short replacement token for a third-party mark that must not appear in a game's "
    "player-facing text. Answer with the token only, no explanation."
)


def _global_options() -> argparse.ArgumentParser:
    """`--root` and `--format` are accepted before or after the verb.

    `SUPPRESS` as the default is load-bearing: a subparser that does not see the flag must not
    overwrite the value the top-level parser already stored.
    """
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--root", default=argparse.SUPPRESS, help="repository root (default: the git working tree)")
    parser.add_argument("--format", choices=("json", "text"), default=argparse.SUPPRESS)
    return parser


def build_parser() -> argparse.ArgumentParser:
    global_options = _global_options()
    parser = argparse.ArgumentParser(prog="ipcensor.report", parents=[global_options])
    subparsers = parser.add_subparsers(dest="verb", required=True)

    census = subparsers.add_parser("census", help="distinct-token census", parents=[global_options])
    census.add_argument("--tree", default=None)
    census.add_argument("--top", type=int, default=None)

    scan = subparsers.add_parser("scan", help="registry hits, bucketed", parents=[global_options])
    _scan_arguments(scan)

    suggest_parser = subparsers.add_parser(
        "suggest", help="replacement tokens per finding", parents=[global_options]
    )
    suggest_parser.add_argument("--authored-only", action="store_true")
    suggest_parser.add_argument("--tree", default=None)

    curate_parser = subparsers.add_parser(
        "curate", help="dataset import and reconfirmation", parents=[global_options]
    )
    curate_verbs = curate_parser.add_subparsers(dest="curate_verb", required=True)
    import_verb = curate_verbs.add_parser("import", parents=[global_options])
    import_verb.add_argument("--dataset", default="uspto")
    import_verb.add_argument("--input", required=True)
    import_verb.add_argument("--dataset-version", required=True)
    import_verb.add_argument("--as-of", required=True)
    import_verb.add_argument("--out-dir", default=None)
    reconfirm_verb = curate_verbs.add_parser("reconfirm", parents=[global_options])
    reconfirm_verb.add_argument("--no-model", action="store_true")
    reconfirm_verb.add_argument("--as-of", required=True)
    reconfirm_verb.add_argument("--out-dir", default=None)
    admit_verb = curate_verbs.add_parser("admit", parents=[global_options])
    admit_verb.add_argument("--candidates", required=True)
    admit_verb.add_argument("--as-of", required=True)
    admit_verb.add_argument("--dry-run", action="store_true")

    all_verb = subparsers.add_parser(
        "all", help="census, scan, suggest, plan and report", parents=[global_options]
    )
    _scan_arguments(all_verb)

    subparsers.add_parser(
        "registry-check", help="parse the shipped registry and exit", parents=[global_options]
    )
    return parser


def _scan_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--authored-only", action="store_true")
    parser.add_argument("--fail-on", choices=("enforced",), default=None)
    parser.add_argument("--tree", default=None)
    parser.add_argument("--bucket", choices=sorted(BUCKETS), default=None)
    parser.add_argument("--by", choices=report.GROUPINGS, default="file")
    parser.add_argument("--plan", default=None)
    parser.add_argument("--report", default=None)


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(list(sys.argv[1:] if argv is None else argv))
    try:
        root = report.resolve_root(getattr(args, "root", None))
        if args.verb == "registry-check":
            print(report.registry_check(root))
            return EXIT_OK
        if args.verb == "census":
            return _census(args, root)
        if args.verb == "scan":
            return _scan(args, root)
        if args.verb == "suggest":
            return _suggest(args, root)
        if args.verb == "curate":
            return _curate(args, root)
        if args.verb == "all":
            return _all(args, root)
    except (report.ReportError, RegistryError, curate.CurateError) as exc:
        print(f"ipcensor: {exc}", file=sys.stderr)
        return EXIT_ERROR
    except OSError as exc:
        print(f"ipcensor: {exc}", file=sys.stderr)
        return EXIT_ERROR
    parser.error(f"unknown verb {args.verb!r}")
    return EXIT_ERROR


# ---- verbs ----------------------------------------------------------------------


def _census(args: argparse.Namespace, root: Path) -> int:
    registry = report.load_registry_for(root)
    stats = report.census_tree(root, registry, tree=args.tree)
    selected = stats if args.top is None else stats[: args.top]
    if _format(args) == "json":
        print(json.dumps([stat.as_dict() for stat in selected], indent=2, ensure_ascii=False))
    else:
        for stat in selected:
            trees = ", ".join(f"{key}={value}" for key, value in sorted(stat.by_tree.items()))
            print(f"{stat.total:7d}  {stat.display:<24}  {trees}")
    return EXIT_OK


def _scan(args: argparse.Namespace, root: Path) -> int:
    registry = report.load_registry_for(root)
    extra = report.output_self_paths(root, [args.plan, args.report])
    findings = report.scan_tree(root, registry, tree=args.tree, extra_self_paths=extra)
    if args.bucket is not None:
        findings = tuple(finding for finding in findings if finding.bucket == args.bucket)

    model = None if args.authored_only else llm.load_config().model
    suggestions = report.suggest_for(
        findings,
        registry,
        authored_only=args.authored_only,
        propose=None if args.authored_only else _proposer(),
    )
    plan = report.build_plan(
        findings,
        root=root,
        registry=registry,
        model=model,
        suggestions=suggestions,
        generated_at=_now(),
    )

    if args.plan:
        _write(Path(root) / args.plan, report.render_plan(plan, by=args.by))
    if args.report:
        _write(Path(root) / args.report, report.render_report(plan, by=args.by))

    if _format(args) == "json":
        print(report.render_plan(plan, by=args.by), end="")
    else:
        for finding in findings:
            print(
                f"{finding.path}:{finding.line}:{finding.column}  {finding.matched} "
                f"[{finding.mark}] {finding.bucket} / {finding.remediation}"
            )
        print(f"{len(findings)} finding(s)")

    if args.fail_on == "enforced" and report.enforced_findings(findings, registry):
        return EXIT_FINDINGS
    return EXIT_OK


def _suggest(args: argparse.Namespace, root: Path) -> int:
    registry = report.load_registry_for(root)
    findings = report.scan_tree(root, registry, tree=args.tree)
    suggestions = report.suggest_for(
        findings,
        registry,
        authored_only=args.authored_only,
        propose=None if args.authored_only else _proposer(),
    )
    rows = [suggestion.as_dict() for suggestion in suggestions]
    if _format(args) == "json":
        print(json.dumps(rows, indent=2, ensure_ascii=False))
    else:
        for row in rows:
            note = f"  # {PROPOSAL_MARKER}" if row["note"] == PROPOSAL_MARKER else ""
            print(f"{row['mark']:<24} {row['source']:<9} {row['replacement']}{note}")
    return EXIT_OK


def _curate(args: argparse.Namespace, root: Path) -> int:
    registry = report.load_registry_for(root)
    directory = Path(args.out_dir) if getattr(args, "out_dir", None) else root / curate.DEFAULT_CANDIDATE_DIR

    if args.curate_verb == "import":
        filter = curate.load_import_filter(root / curate.DEFAULT_FILTER_PATH)
        candidates = curate.read_and_import(
            args.input, filter, dataset_version=args.dataset_version
        )
        name = curate.candidate_file_name(when=args.as_of)
        target = curate.write_candidates(
            candidates,
            name=name,
            directory=directory,
            dataset_version=args.dataset_version,
        )
        print(f"{target}: {len(candidates)} candidate(s)")
        return EXIT_OK

    if args.curate_verb == "reconfirm":
        stats = report.census_tree(root, registry)
        model = None if args.no_model else llm.load_config().model
        result = curate.reconfirm(
            registry,
            stats,
            as_of=args.as_of,
            propose=None if args.no_model else _proposer(),
            model=model,
        )
        name = curate.candidate_file_name(dataset_id="reconfirm", when=args.as_of)
        target = curate.write_candidates(
            result.candidates, name=name, directory=directory, dataset_id="reconfirm"
        )
        for error in result.model_errors:
            print(f"ipcensor: {error}", file=sys.stderr)
        print(f"{target}: {len(result.candidates)} candidate(s)")
        return EXIT_OK

    document = json.loads(Path(args.candidates).read_text(encoding="utf-8"))
    candidates = [curate.candidate_from_dict(row) for row in document["candidates"]]
    result = curate.admit(candidates, registry, as_of=args.as_of)
    for mark, reason in result.refused:
        print(f"ipcensor: refused {mark}: {reason}", file=sys.stderr)
    if not args.dry_run:
        curate.write_marks(result, root / MARKS_PATH)
    print(
        f"admitted {len(result.admitted)}, rechecked {len(result.rechecked)}, "
        f"rejected {len(result.rejected)}, refused {len(result.refused)}"
    )
    return EXIT_FINDINGS if result.refused else EXIT_OK


def _all(args: argparse.Namespace, root: Path) -> int:
    registry = report.load_registry_for(root)
    extra = report.output_self_paths(root, [args.plan, args.report])
    findings = report.scan_tree(root, registry, tree=args.tree, extra_self_paths=extra)
    suggestions = report.suggest_for(
        findings,
        registry,
        authored_only=args.authored_only,
        propose=None if args.authored_only else _proposer(),
    )
    plan = report.build_plan(
        findings,
        root=root,
        registry=registry,
        model=None if args.authored_only else llm.load_config().model,
        suggestions=suggestions,
        generated_at=_now(),
    )
    if args.plan:
        _write(Path(root) / args.plan, report.render_plan(plan, by=args.by))
    if args.report:
        _write(Path(root) / args.report, report.render_report(plan, by=args.by))
    stats = report.census_tree(root, registry, tree=args.tree)
    print(f"census: {len(stats)} distinct token(s); scan: {len(findings)} finding(s)")
    if args.fail_on == "enforced" and report.enforced_findings(findings, registry):
        return EXIT_FINDINGS
    return EXIT_OK


# ---- helpers --------------------------------------------------------------------


def _proposer():
    """The tool's one client, adapted to the finding-shaped seam `suggest` takes.

    Only the mark and its bucket are sent: the prompt never carries the surrounding file text, so a
    model call cannot leak proprietary content (spec-suggest.md §Boundaries).
    """
    call = llm.build_proposer(llm.load_config(env=os.environ, defaults={}), system_prompt=SUGGEST_PROMPT)

    def propose(finding) -> str:
        return call(f"mark: {finding.mark}\nbucket: {finding.bucket}")

    return propose


def _format(args: argparse.Namespace) -> str:
    return getattr(args, "format", "text")


def _now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def _write(target: Path, text: str) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    raise SystemExit(main())
