#!/usr/bin/env python3
r"""Guard: the generated-seed hard rule — generated data is regenerated, never hand-edited.
Replaces `guard-generated-seed.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every line went out through `Write-Host`**, invisible to a `2>&1` capture. Findings and the
  BLOCKED block now go to stderr; only a verdict reaches stdout; `--json` carries the machine form.
* **Both preconditions THREW**, producing a stack trace and exit 1 — the same exit as a real violation,
  so "CI passed no range" and "someone hand-edited the corpus" were indistinguishable to a reader. The
  port names them `CI-RANGE-REQUIRED` and `RANGE-AND-BASEREF`.

THE THING THIS GUARD IS FOR
---------------------------
A tree whose entries carry generator provenance (`_meta.model` / `promptVersion` / `batch`) is the
OUTPUT of seedsmith or a `tools/*Gen` program, not authored source. Hand-editing an emitted row forks
the corpus from its generator: the next run reverts the edit and the ledger stops describing the file.
So a change set that modifies a provenance-carrying file under a generated tree, WITHOUT also touching
that tree's generator, tuning or registry, is a finding.

A file is inspected only when BOTH hold: its path is under a generated tree, and its JSON carries
generator provenance. The second half is why `gk-data/packs/fusion/data/seed/items/_registry/**` is correctly ignored — it
lives under a generated root but is authored.

THIS GUARD ALREADY FAILED CLOSED, AND THAT IS WORTH SAYING
-----------------------------------------------------------
Its `Invoke-GitLines` threw on a non-zero git exit, unlike its sibling `guard-repo-boundary`, whose copy
swallowed the failure and read the empty output as "nothing changed" (a hole closed in that port). So
the transcription here is faithful by default: a git failure is a named refusal, and the port does not
get credit for fixing something that was not broken.

CASE FOLDING, AND TWO PLACES IT BITES
-------------------------------------
Every path match came from PowerShell `-match`, so it FOLDS case — and the patterns are all lowercase
paths, so a change to `DATA/SEED/ITEMS/...` is matched here exactly as the original matched it.
Separately, `Sort-Object -Unique` dedups case-INsensitively, so `A.json` and `a.json` are one entry.
Python's `set` would keep both, which would change the count the OK line reports and could double-report
a file. The dedup is therefore explicit and case-insensitive.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

GUARD_ID = "generated-seed"
VERDICT_BLOCKED = "[guard-generated-seed] BLOCKED: generated seed edited without its generator"
VERDICT_CLEAN = "[guard-generated-seed] clean ({count} changed file(s) inspected)"
EXIT_OK = 0
EXIT_FAILED = 1

GIT_TIMEOUT = 300

# Generated is the emitted output; Sources are the generator code plus the tuning/registry inputs the
# generator reads. Touching any Source is the sanctioned way to change Generated.
TREES: tuple[dict[str, object], ...] = (
    {"generated": r"^data/seed/items/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/items/",
                 r"^tools/seedsmith/seedsmith/pipeline/",
                 r"^tools/seedsmith/seedsmith/report/",
                 r"^data/seed/items/_registry/",
                 r"^data/seed/items/_tuning/",
                 r"^data/seed/items/_seed/",
                 r"^tools/ItemSeedValidator/")},
    {"generated": r"^data/seed/actions/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/actions/",
                 r"^tools/seedsmith/seedsmith/adapters/items/",
                 r"^data/seed/actions/_registry/",
                 r"^data/seed/actions/_generated/")},
    {"generated": r"^data/seed/atoms/generated/",
     "sources": (r"^tools/FamilyExpandGen/",
                 r"^src/FusionRpg.Core/Effects/Atoms/Generation/",
                 r"^data/seed/channel-pools/",
                 r"^data/seed/items/_registry/")},
    {"generated": r"^data/generated/",
     "sources": (r"^tools/", r"^data/seed/", r"^data/tuning/")},
    {"generated": r"^data/seed/passive-tree/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/trees/",
                 r"^data/seed/passive-tree/_registry/")},
    {"generated": r"^data/seed/creatures/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/creatures/",)},
    {"generated": r"^data/seed/dungeon/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/dungeon/",)},
    {"generated": r"^data/seed/structures/",
     "sources": (r"^tools/seedsmith/seedsmith/adapters/structures/",)},
)

# Generator bookkeeping and provenance side-cars are never corpus content.
IGNORED_NAME_PATTERNS = (r"\.ledger\.json$", r"^data/seed/items/_runs/", r"^data/seed/actions/_runs/",
                         r"_meta\.json$", r"/_index\.json$")

# FOLDS CASE, because the original used `-match` and the patterns are lowercase paths.
PROVENANCE_KEYS = ("model", "promptVersion", "batch")

_NAME_ONLY = ("diff", "--name-only", "--diff-filter=ACMRD")


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def _git(root: Path, args: list[str]) -> list[str]:
    """Run git, return its trimmed non-empty stdout lines, or REFUSE.

    Already faithful to the original, which threw here too — unlike its sibling. See the docstring.
    """
    try:
        proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                              timeout=GIT_TIMEOUT)
    except subprocess.TimeoutExpired as exc:
        raise Refusal("GIT-TIMEOUT", f"git {' '.join(args)} did not finish within {GIT_TIMEOUT}s") from exc
    except OSError as exc:
        raise Refusal("GIT-UNAVAILABLE", f"cannot run git {' '.join(args)}: {exc}") from exc
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip().splitlines()
        raise Refusal("GIT-FAILED",
                      f"git {' '.join(args)} failed with exit {proc.returncode}: "
                      f"{detail[0] if detail else 'no output'}")
    return [line.strip() for line in proc.stdout.splitlines() if line.strip()]


def _dedup_case_insensitive(values: list[str]) -> list[str]:
    """`Sort-Object -Unique` semantics: case-insensitive, first occurrence kept.

    A plain `set` would keep `A.json` and `a.json` as two entries, which changes the count the OK line
    reports and can report one file twice. Transcribed deliberately — see the module docstring.
    """
    seen: set[str] = set()
    out: list[str] = []
    for value in values:
        key = value.lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(value)
    return sorted(out, key=lambda v: v.lower())


def changed_files(root: Path, base_ref: str, commit_range: str | None) -> list[str]:
    """The changed paths for the requested scope.

    Working-tree mode is the widest: the unstaged diff, the staged diff, and untracked non-ignored
    additions. A range or a base ref replaces all three, because both describe a push rather than the
    tree in front of you.
    """
    collected: list[str] = []
    if commit_range:
        collected += _git(root, [*_NAME_ONLY, commit_range])
    elif base_ref:
        collected += _git(root, [*_NAME_ONLY, base_ref])
    else:
        collected += _git(root, list(_NAME_ONLY))
        collected += _git(root, ["diff", "--cached", *(_NAME_ONLY[1:])])
        collected += _git(root, ["ls-files", "--others", "--exclude-standard"])
    return _dedup_case_insensitive([p.replace("\\", "/") for p in collected])


def _truthy(value: object) -> bool:
    """PowerShell truthiness for the values JSON can carry: 0, "", false, [] and {} are all falsy."""
    if value is None or value is False:
        return False
    if isinstance(value, (int, float)) and value == 0:
        return False
    if isinstance(value, (str, list, dict, tuple)) and len(value) == 0:
        return False
    return True


def has_generator_provenance(root: Path, rel_path: str) -> bool:
    """Does this file's JSON carry generator provenance?

    Unreadable or non-JSON is FALSE, not a failure: this predicate decides whether a file is corpus
    content, and a file that is not JSON cannot be a generated seed. Transcribed from the original's
    `catch { return $false }`.
    """
    full = root / rel_path
    if not full.is_file():
        return False
    try:
        doc = json.loads(full.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, ValueError):
        return False
    # PowerShell MEMBER ENUMERATION, transcribed. `$doc._meta` on a top-level ARRAY returns each
    # element's `_meta`, so the original treats a file whose top level is `[{"_meta":{...}}]` as
    # provenance-carrying. Measured: the original BLOCKS it and a naive `isinstance(doc, dict)` port
    # does not. Member enumeration is a language feature leaking into a predicate, and the honest
    # options are to transcribe it or to declare the narrowing. Narrowing is the wrong direction here
    # - this guard's job is to notice provenance-carrying output, and "we saw it and did not look" is
    # the failure mode - so the enumeration is reproduced and the reasoning is recorded.
    candidates: list[object]
    if isinstance(doc, dict):
        candidates = [doc]
    elif isinstance(doc, list):
        candidates = [item for item in doc if isinstance(item, dict)]
    else:
        return False
    for candidate in candidates:
        meta = candidate.get("_meta")
        if not isinstance(meta, dict):
            continue
        if any(key in meta and _truthy(meta[key]) for key in PROVENANCE_KEYS):
            return True
    return False


def check(root: Path, *, base_ref: str = "", commit_range: str | None = None,
          require_explicit_range: bool = False) -> dict:
    """`require_explicit_range` is the CI contract, and it must be GATED.

    Found by the differential: the first version returned the CI-contract result whenever no range and
    no base ref were given, with no reference to the flag. Working-tree mode is the DEFAULT, so the
    guard inspected nothing at all locally and printed a clean verdict - a silent no-op, which is the
    failure class this whole program exists to remove, introduced by the port rather than inherited.
    A guard that cannot tell "not asked" from "asked and forbidden" is worse than one that has no CI
    contract at all, because it looks like it is working.
    """
    if commit_range and base_ref:
        raise Refusal("RANGE-AND-BASEREF",
                      "generated-seed accepts either --range or --base-ref, not both")
    if require_explicit_range and not commit_range and not base_ref:
        # A push range is not a working tree: the working tree says nothing about what the push
        # contains. `main` turns this into a named refusal and exit 1; reaching it through `check`
        # directly returns the same shape with `requires_range` set, so the envelope is the same
        # whether the caller is the CLI or a test.
        return {"guard": GUARD_ID, "verdict": "OK", "changed": 0, "violations": [],
                "scope": "working tree", "requires_range": True, "refused": "CI-RANGE-REQUIRED"}

    changed = changed_files(root, base_ref, commit_range)
    # The label the original's if/elseif/else produced. `commit_range or base_ref` is EMPTY in
    # working-tree mode, so without this the clean line read "no changes vs " with nothing after it -
    # found by the differential, and the kind of small wrongness that makes a log line unusable.
    scope = commit_range or base_ref or "working tree"
    if not changed:
        return {"guard": GUARD_ID, "verdict": "OK", "changed": 0, "violations": [],
                "scope": scope, "requires_range": False, "refused": None}

    violations: list[str] = []
    for tree in TREES:
        generated_re = re.compile(str(tree["generated"]), re.IGNORECASE)
        ignored = [re.compile(p, re.IGNORECASE) for p in IGNORED_NAME_PATTERNS]
        touched = [p for p in changed
                   if generated_re.search(p) and not any(rx.search(p) for rx in ignored)]
        if not touched:
            continue
        if any(re.compile(str(s), re.IGNORECASE).search(p) for s in tree["sources"] for p in changed):
            # A source moved, so the tree may legitimately re-emit. The sanctioned path.
            continue
        violations.extend(p for p in touched if has_generator_provenance(root, p))

    return {"guard": GUARD_ID, "verdict": "FAIL" if violations else "OK", "changed": len(changed),
            "violations": violations, "scope": scope, "requires_range": False, "refused": None}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: generated data is regenerated, never hand-edited (replaces "
                    "guard-generated-seed.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--base-ref", default="", help="diff against this ref")
    parser.add_argument("--range", default=None, help="an explicit complete commit range, e.g. a..b")
    parser.add_argument("--require-explicit-range", action="store_true",
                        help="the CI contract: refuse rather than inspect the working tree")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    try:
        result = check(root, base_ref=args.base_ref, commit_range=args.range,
                       require_explicit_range=args.require_explicit_range)
    except Refusal as refusal:
        print(f"{VERDICT_BLOCKED}: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "changed": 0, "violations": [],
                              "scope": args.range or args.base_ref or "working tree",
                              "requires_range": args.require_explicit_range,
                              "refused": refusal.reason}, indent=2))
        return EXIT_FAILED

    if result.get("requires_range"):
        # The CI contract. `check` reports it as a shape so the envelope is identical whether the
        # caller is the CLI or a test; the CLI turns it into the refusal and the non-zero exit. The
        # original THREW here, so this is faithful in outcome and named rather than a stack trace.
        refusal = Refusal("CI-RANGE-REQUIRED",
                          "generated-seed CI inspection requires an explicit --range or --base-ref; "
                          "working-tree mode is not a push range")
        print(f"{VERDICT_BLOCKED}: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({**result, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "refused": refusal.reason}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        # stdout carries the VERDICT and nothing else, so a capture of stdout alone is never empty on a
        # clean run and never carries a finding.
        if result["changed"] == 0:
            print(f"[guard-generated-seed] no changes vs {result['scope']}")
        else:
            print(VERDICT_CLEAN.format(count=result["changed"]))
    else:
        print(VERDICT_BLOCKED, file=sys.stderr)
        print("", file=sys.stderr)
        for violation in result["violations"]:
            print(f"  ! {violation}", file=sys.stderr)
        print("", file=sys.stderr)
        for line in (
            "These files carry generator provenance (_meta.model / promptVersion / batch), so they",
            "are OUTPUT, not authored source. Hand-editing them forks the corpus from its generator:",
            "the next run reverts the edit and the run ledger stops describing the file.",
            "",
            "Sanctioned path: change the generator / tuning / registry, then regenerate and commit",
            "the re-emitted output. See AGENTS.md and CLAUDE.md, and the seedsmith skill.",
        ):
            print(line, file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
