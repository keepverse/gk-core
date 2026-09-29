#!/usr/bin/env python3
r"""Guard: a published tuning version is immutable. Replaces `guard-tuning-immutability.ps1`.

  T1 - a MODIFIED published version may change only keys under `_meta` (a normalised comparison with
       `_meta` removed must be equal).
  T2 - an ADDED `<domain>.v<n>.json` (n > 1) requires `<domain>.v<n-1>.json` to already exist.
  T3 - a DELETED `gk-core/data/tuning/*.json` fails.
  T4 - an ADDED file's basename matches `^[a-z0-9-]+\.v\d+\.json$`, and its domain is not listed in
       `gk-core/scripts/tuning-domain-denylist.v1.json` - a pattern list of shapes that are never real
       domains, e.g. test pollution named `loop*test*`. Never a domain ALLOWLIST, which would pin a
       population.

The sanctioned escape for T1/T3: a commit in the checked range whose message contains
`tuning-immutability: correction <reason>` exempts every `gk-core/data/tuning/*.json` path ALSO named in
that same commit message - scoped to the files it names, never a blanket pass, and LOUD: it is
printed on stderr whenever used, so a correction cannot pass unnoticed.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every line went out through `Write-Host`**, invisible to a `2>&1` capture. stdout now carries
  the verdict and nothing else; findings, correction notices and skips go to stderr.
* **A MISSING ROOT READ AS CLEAN.** `git -C <not-a-repo> diff` fails, `2>$null` swallows the
  message, the changed-file set comes back empty, and the guard reports "no gk-core/data/tuning changes" and
  exits 0. A typo'd `-Root` therefore passed a CI gate without checking anything. It is now a named
  refusal. This is the fail-open shape the Python-tool standard exists to remove.
* **A MISSING DENYLIST SILENTLY DISABLED T4.** `if (Test-Path $DenylistPath)` with an empty pattern
  list means every domain is permitted - the same failure with a different trigger. Named refusal.
* **stderr was suppressed wholesale**, to dodge a PS7 native-stderr hazard (a harmless CRLF warning
  aborting the whole guard). Python separates the streams, so the suppression is no longer needed
  and a real git failure is no longer indistinguishable from a warning.

FOUR CASE CONVENTIONS, NONE OF THEM THE REPO DEFAULT
----------------------------------------------------
| Where | How | Folds case? |
|---|---|---|
| correction marker in a commit message | `-notmatch` | **yes** |
| denylist pattern vs a domain | `-match` | **yes** |
| is this path a tuning file | `-match '^gk-core/data/tuning/.*\.json$'` | **yes** |
| the `<domain>.v<n>.json` filename shape | `-match` | **yes** |
| path named inside a correction message | `[regex]::Matches` | no |
| `Status.StartsWith('R')` | .NET `String.StartsWith` | no |
| `HashSet<string>.Contains` | ordinal | no |

The filename shape folding is the surprising one: `[a-z0-9-]` matches `D` and `.V1.JSON` matches, so
`D.V1.JSON` is accepted as a well-formed tuning file and its domain compared case-insensitively
against the denylist. That is the original's reading and this port is proving the port, not rewriting
the rule - but it is asserted, so a later editor tightening it has to decide rather than drift.

THE T1 COMPARISON, AND WHY THE HOST'S JSON SHAPE DOES NOT MATTER
---------------------------------------------------------------
The original walked a `PSCustomObject` tree and could not use `-AsHashtable`, because Windows
PowerShell 5.1 - what every `FusionRpg.Guard.Tests` fixture launches - has no such parameter, while
CI's `pwsh` does. A guard that only works under one host is not portable.

This port parses with `json` and sorts keys ordinally, which is a DIFFERENT sort: PowerShell's
`Sort-Object Name` is case-insensitive and culture-aware, so `{a, B}` orders `a, B` there and
`B, a` here. That difference cannot produce a false positive, and the reason is worth stating: the
comparison is between two canonicalisations that use the SAME sort, so two identical key SETS always
canonicalise to the same string under either ordering. It could only matter if the two sides had
different keys, which is a real change under any sort. A test pins both halves.

Two number renderings DO differ, in the stricter direction: PowerShell parses `10.0` to a `Double`
and re-renders it `10`, so it would call `10.0 -> 10` unchanged, while `json` preserves the float and
this port reports it as a change. A stricter reading of "must be equal" is the safe direction, and it
is declared rather than papered over.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

GUARD_ID = "tuning-immutability"
VERDICT_OK = ("TUNING IMMUTABILITY GUARD OK — {count} data/tuning/*.json change(s) checked, "
              "T1-T4 clean")
VERDICT_FAILED = "TUNING IMMUTABILITY GUARD FAILED:"
VERDICT_NO_CHANGES = "[guard-tuning-immutability] no data/tuning/*.json changes vs {scope}"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_BASE_REF = "HEAD"
DEFAULT_DENYLIST = "tuning-domain-denylist.v1.json"
TUNING_DIR = "data/tuning"
# A hard ceiling on every git call. The tool standard: a script that can hang is a defect.
GIT_TIMEOUT_SECONDS = 120
# PowerShell's `ConvertTo-Json -Depth 64` refused to go deeper and warned. `json.loads` has no depth
# argument and raises RecursionError instead, so the limit is explicit and the failure is named.
MAX_JSON_DEPTH = 64

# CASE-INSENSITIVE (`-match` / `-notmatch` fold). All four.
CORRECTION_MARKER = re.compile(r"tuning-immutability:\s*correction\b", re.IGNORECASE)
TUNING_PATH = re.compile(r"^data/tuning/.*\.json$", re.IGNORECASE)
TUNING_FILENAME = re.compile(r"^([a-z0-9-]+)\.v(\d+)\.json$", re.IGNORECASE)
# CASE-SENSITIVE (`[regex]::Matches`).
CORRECTION_PATH = re.compile(r"data/tuning/[A-Za-z0-9._-]+\.json")


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def git_lines(root: Path, args: list[str]) -> list[str]:
    """`git -C <root> <args>` stdout as lines, with stderr separated and a hard timeout.

    stderr is DELIBERATELY not consulted. The original piped it to `2>$null` to stop a harmless CRLF
    warning from aborting the guard, and that suppression is why a genuine git failure was
    indistinguishable from a warning. Python separates the streams, so the warning is harmless again
    and only the caller decides what a non-zero exit means - `git show` of an absent path legitimately
    exits non-zero with empty stdout, and that emptiness is a MEASURED answer (T2's predecessor
    check), not an error.
    """
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          timeout=GIT_TIMEOUT_SECONDS)
    return (proc.stdout or "").splitlines()


def git_ok(root: Path, args: list[str]) -> bool:
    """Whether git reported success. Used only where a non-zero exit IS the answer."""
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          timeout=GIT_TIMEOUT_SECONDS)
    return proc.returncode == 0


def is_git_repository(root: Path) -> bool:
    return git_ok(root, ["rev-parse", "--git-dir"])


def changed_file_statuses(root: Path, base_ref: str, commit_range: str | None) -> list[tuple[str, str]]:
    """`(status, path)` per changed path, forward-slashed.

    `git diff --name-status <ref>` already reflects BOTH staged and unstaged changes against `<ref>`
    (unlike `git diff --cached`, which is index-only), so no second `--cached` pass is needed. In
    working-tree mode untracked additions are added too, which matches the sibling guard's own rule.
    """
    spec = [commit_range] if commit_range else [base_ref]
    raw = git_lines(root, ["diff", "--name-status", "--diff-filter=ACMDR", *spec])

    results: list[tuple[str, str]] = []
    for line in raw:
        if not line:
            continue
        parts = line.split("\t")
        status = parts[0]
        # .NET String.StartsWith: case-sensitive.
        if status.startswith("R") or status.startswith("C"):
            if len(parts) >= 3:
                # A rename is a DELETION of the old path AND an ADDITION of the new one, which is what
                # makes renaming a published tuning file fail T3 rather than pass as a modification.
                if status.startswith("R"):
                    results.append(("D", parts[1].replace("\\", "/")))
                results.append(("A", parts[2].replace("\\", "/")))
            continue
        if len(parts) >= 2:
            results.append((status[0], parts[1].replace("\\", "/")))

    if not commit_range:
        for untracked in git_lines(root, ["ls-files", "--others", "--exclude-standard"]):
            if untracked:
                results.append(("A", untracked.strip().replace("\\", "/")))
    return results


def correction_marked_files(commit_messages: list[str]) -> set[str]:
    """Every `gk-core/data/tuning/*.json` path named by a commit carrying the correction marker.

    A plain `set`. The original needed the unary comma operator `,$marked` to stop PowerShell
    unrolling a `HashSet` onto the pipeline element-by-element - which turned an EMPTY set into
    `$null` at the call site and a non-empty one into an `object[]` with no `.Contains()`, reproduced
    live on the very first real fixture. A Python `set` has no such shape, and the empty case is the
    common one, so the trap fired on nearly every run.
    """
    marked: set[str] = set()
    for message in commit_messages:
        if not message or not CORRECTION_MARKER.search(message):
            continue
        marked.update(m.group(0) for m in CORRECTION_PATH.finditer(message))
    return marked


def _canonical(node) -> object:
    """Recursively sort object keys; arrays keep their order (it is meaningful)."""
    if isinstance(node, dict):
        return {key: _canonical(node[key]) for key in sorted(node)}
    if isinstance(node, list):
        return [_canonical(item) for item in node]
    return node


def strip_meta(json_text: str) -> str:
    """The document with top-level `_meta` removed and keys sorted at every level, as one string.

    Only the TOP level's `_meta` is dropped: `_meta` nested deeper is ordinary data.
    """
    try:
        document = json.loads(json_text)
    except json.JSONDecodeError as exc:
        raise Refusal("TUNING-DOC-NOT-JSON", f"could not parse: {exc}") from exc
    if not isinstance(document, dict):
        # The original read `$doc.PSObject.Properties`, which on a top-level ARRAY yields the array's
        # own members (`Length`, `Rank`, ...) rather than its elements, and compared that. Refusing is
        # the honest reading: this is a published tuning document, and it is an object.
        raise Refusal("TUNING-DOC-NOT-AN-OBJECT",
                      f"a published tuning document is a JSON object, not a {type(document).__name__}")
    without_meta = {key: value for key, value in document.items() if key != "_meta"}
    return json.dumps(_canonical(without_meta), separators=(",", ":"), sort_keys=True)


def load_denylist(denylist_path: Path) -> list[str]:
    """The domain denylist patterns, or a named refusal.

    The original's `if (Test-Path ...)` left the list EMPTY when the file was absent, and an empty
    list permits every domain - T4 silently off, reported as a clean guard.
    """
    if not denylist_path.is_file():
        raise Refusal("DENYLIST-MISSING",
                      f"{denylist_path} does not exist. An absent denylist is an empty pattern list, "
                      "which permits every domain and turns T4 off silently.")
    try:
        document = json.loads(denylist_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("DENYLIST-UNREADABLE", f"{denylist_path}: {exc}") from exc
    patterns = document.get("patterns") if isinstance(document, dict) else None
    if not isinstance(patterns, list):
        raise Refusal("DENYLIST-MALFORMED",
                      f"{denylist_path} has no `patterns` array, so no domain could ever be denied")
    return [str(p) for p in patterns]


def domain_is_denied(domain: str, patterns: list[str]) -> bool:
    """First matching pattern wins, so the finding does not name one - as in the original.

    CASE-INSENSITIVE: PowerShell `-match`.
    """
    return any(re.search(pattern, domain, re.IGNORECASE) for pattern in patterns)


def check(root: Path, base_ref: str = DEFAULT_BASE_REF, commit_range: str | None = None,
          denylist_path: Path | None = None) -> dict:
    if not root.is_dir():
        raise Refusal("ROOT-MISSING", str(root))
    if not is_git_repository(root):
        # The original reported "no gk-core/data/tuning changes" and exited 0 here, because `2>$null`
        # swallowed git's "not a git repository" and the empty changed-file set looked like a clean
        # tree. A typo'd -Root passed a CI gate without checking anything.
        raise Refusal("NOT-A-GIT-REPOSITORY",
                      f"{root} is not a git repository, so there is nothing to compare and 'no "
                      "changes' would be indistinguishable from 'checked and clean'.")

    patterns = load_denylist(denylist_path) if denylist_path else []

    parts = commit_range.split("..", 1) if commit_range else []
    range_before = (parts[0] if len(parts) >= 1 and parts[0] else base_ref)
    range_after = (parts[1] if len(parts) >= 2 and parts[1] else None)
    log_spec = commit_range if commit_range else f"{base_ref}..HEAD"

    messages = [m for m in "\n".join(
        git_lines(root, ["log", "--format=%B%x02", log_spec])).split("\x02") if m]
    marked = correction_marked_files(messages)

    changes = changed_file_statuses(root, base_ref, commit_range)
    tuning_changes = [(s, p) for s, p in changes if TUNING_PATH.search(p)]

    if not tuning_changes:
        return {"guard": GUARD_ID, "verdict": "OK", "scope": commit_range or base_ref,
                "tuning_changes": 0, "failures": [], "corrections_used": [], "skipped": []}

    failures: list[str] = []
    corrections: list[str] = []
    skipped: list[str] = []

    for status, path in tuning_changes:
        is_marked = path in marked  # HashSet.Contains: ordinal, case-sensitive.

        if status == "M":
            before_text = "\n".join(git_lines(root, ["show", f"{range_before}:{path}"]))
            if range_after:
                after_text = "\n".join(git_lines(root, ["show", f"{range_after}:{path}"]))
            else:
                full = root / path
                after_text = full.read_text(encoding="utf-8", errors="replace") if full.is_file() else ""
            if not before_text or not after_text:
                # The original `continue`d here, so an unreadable side of an 'M' was INVISIBLE. The
                # skip is transcribed, but it is now REPORTED: a rule that silently declines to look
                # is indistinguishable from a rule that looked and found nothing.
                skipped.append(f"{path}: modified, but {before_text and 'the after' or 'the before'} "
                               f"side could not be read, so T1 could not be evaluated")
                continue
            if strip_meta(before_text) != strip_meta(after_text):
                if is_marked:
                    corrections.append(f"T1 correction marker used for {path}")
                else:
                    failures.append(f"T1 {path}: a published tuning version changed outside _meta. "
                                    "Publish a new version through tools/tuning/publish.py instead.")

        elif status == "A":
            filename = Path(path).name
            shape = TUNING_FILENAME.search(filename)
            if shape:
                domain, raw_n = shape.group(1), shape.group(2)
                n = int(raw_n)
                if domain_is_denied(domain, patterns):
                    failures.append(f"T4 {path}: domain '{domain}' matches the tuning-domain denylist "
                                    "— test pollution, never a published domain")
                if n > 1:
                    prior = f"{TUNING_DIR}/{domain}.v{n - 1}.json"
                    if range_after:
                        prior_exists = bool(git_lines(root, ["show", f"{range_after}:{prior}"]))
                    else:
                        prior_exists = (root / prior).is_file()
                    if not prior_exists:
                        failures.append(f"T2 {path}: predecessor {prior} does not exist — tuning "
                                        "versions must be contiguous")
            else:
                failures.append(f"T4 {path}: file name does not match <domain>.v<n>.json")

        elif status == "D":
            if is_marked:
                corrections.append(f"T3 correction marker used for {path}")
            else:
                failures.append(f"T3 {path}: a published tuning file was deleted. If this undoes an "
                                "in-place edit, use 'tuning-immutability: correction <reason>' in "
                                "the commit message, naming this exact path.")

    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if failures else "OK",
        "scope": commit_range or base_ref,
        "tuning_changes": len(tuning_changes),
        "failures": failures,
        "corrections_used": corrections,
        "skipped": skipped,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: a published tuning version is immutable "
                    "(replaces guard-tuning-immutability.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="the repository to check (default: this script's parent directory)")
    parser.add_argument("--base-ref", default=DEFAULT_BASE_REF,
                        help=f"compare the working tree against this ref (default: {DEFAULT_BASE_REF})")
    parser.add_argument("--range", dest="commit_range", default=None,
                        help="an explicit commit range, e.g. a..b (overrides --base-ref)")
    parser.add_argument("--denylist-path", type=Path, default=None,
                        help=f"the domain denylist (default: this script's directory/"
                             f"{DEFAULT_DENYLIST}). Deliberately relative to the SCRIPT, not to "
                             "--root, so a fixture repository still finds the real denylist.")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    denylist = args.denylist_path or (Path(__file__).resolve().parent / DEFAULT_DENYLIST)

    try:
        result = check(root, args.base_ref, args.commit_range, denylist)
    except Refusal as refusal:
        print(f"{VERDICT_FAILED} {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "scope": args.commit_range or args.base_ref,
                              "tuning_changes": 0, "failures": [], "corrections_used": [],
                              "skipped": []}, indent=2))
        return EXIT_FAILED
    except RecursionError as exc:
        # json.loads' own limit, and the named counterpart to the original's -Depth 64 warning.
        print(f"{VERDICT_FAILED} JSON-TOO-DEEP a tuning document nests deeper than "
              f"{MAX_JSON_DEPTH} levels, which this guard will not parse", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": "JSON-TOO-DEEP",
                              "detail": str(exc), "scope": args.commit_range or args.base_ref,
                              "tuning_changes": 0, "failures": [], "corrections_used": [],
                              "skipped": []}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
        return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED

    # stdout carries the VERDICT and nothing else. Findings, correction notices and skips are stderr:
    # a correction that passes must still be loud, and it is loud in a stream nobody reads by habit.
    if result["tuning_changes"] == 0:
        print(VERDICT_NO_CHANGES.format(scope=result["scope"]))
        return EXIT_OK
    for note in result["corrections_used"]:
        print(f"[guard-tuning-immutability] {note}", file=sys.stderr)
    for note in result["skipped"]:
        print(f"[guard-tuning-immutability] SKIPPED {note}", file=sys.stderr)
    if result["verdict"] == "OK":
        print(VERDICT_OK.format(count=result["tuning_changes"]))
        return EXIT_OK
    print(VERDICT_FAILED)
    for failure in result["failures"]:
        print(f"  {failure}", file=sys.stderr)
    return EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
