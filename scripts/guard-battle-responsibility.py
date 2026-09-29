#!/usr/bin/env python3
r"""Guard: one owner per battle mechanism, and no second owner. Replaces
`guard-battle-responsibility.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
It dot-sourced `scripts/lib/SourceText.ps1` for four helpers, and the file it validated is a
REGISTRY, not code.

* **The finding surface was invisible.** Every verdict and every finding went out through
  `Write-Host`, which writes the INFORMATION stream and is invisible to a `2>&1` capture. See
  `docs/architecture/ps1-port-checklist.md` item 4.
* **A missing registry and a real violation shared an exit code.** A missing registry printed
  `BATTLE RESPONSIBILITY GUARD FAILED` and exited 1 - the same code as nineteen mechanisms with a
  second owner. One is a broken checkout; the other is a finding. The port keeps the original's exit
  1 for the missing registry (its message says FAILED, and callers may already read that) but says so
  in the envelope, so the two are distinguishable without breaking a caller.
* **Its scanner was a private copy.** `Remove-CSharpCommentsAndStrings` blanked comments AND string
  literals while keeping every character offset and every newline. No other policy in `cscan` did
  both, and it is now `strip_comments_and_literals_preserving_layout` - verified 23/23 against this
  file's own copy before it was deleted.

  The blanking half is load-bearing: a mechanism must not be satisfied by text inside a string or a
  comment. The layout half is FIDELITY, and the distinction is measured rather than asserted. All 6
  patterns in the register use `\s*` (zero-or-more), which matches whether a comment between two
  tokens became one space or five, so no current pattern distinguishes the two policies. A fixture
  with `\s{3,}` across a block comment does. So the port honours the original's choice rather than
  substituting the cheaper collapsing policy, which would be a latent behaviour change: the next
  person to write a whitespace-sensitive pattern would find the guard had quietly changed meaning
  underneath them, with no test able to say when.

THE REGISTER IS THE LAW, AND THE GUARD CHECKS ITS OWN ENFORCEMENT
----------------------------------------------------------------
`gk-core/scripts/battle-responsibility.v1.json` names, per mechanism, the file that OWNS a decision and the
decision's mechanical pattern. Three rules, and each exists because a weaker version reports a
clean run while the thing it was written to prevent is happening:

  1. A registered mechanism with NO decision patterns must carry a `note` saying why. Silently
     skipping it makes a stub row look identical to an enforced one.
  2. A non-`banned` decision requires its OWNER to match its own pattern. A register naming a file
     that no longer decides the mechanism is worse than no register: it reports one owner while the
     real one has moved somewhere nothing checks.
  3. Every file that matches a decision pattern is either the owner or on that decision's `allow`
     list. An allow entry with no `module` is itself a finding, because "an exception with no module
     that will remove it is how grandfathered debt becomes a template".

TWO DECISION SHAPES, AND THE DIFFERENCE IS NOT COSMETIC
-------------------------------------------------------
`shape: "owned"` (the default) is a shape the owner is SUPPOSED to exhibit, so the owner must match.
`shape: "banned"` is a shape that should exist NOWHERE, so the owner is NOT allowlisted - once the
owner has been extracted, only the copies still spell it out, and requiring the owner to match would
demand it keep the very formula it just stopped writing. Added 2026-09-17, when extracting the
reflect shape into ElementalResolver made exactly that happen.

Patterns are matched with .NET semantics and therefore FOLD CASE, like every other `-match` in this
program's PowerShell. `[regex]::IsMatch` with no `RegexOptions.IgnoreCase` is case-SENSITIVE, so
getting this backwards would silently narrow the guard.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cscan import strip_comments_and_literals_preserving_layout  # noqa: E402

GUARD_ID = "battle-responsibility"
VERDICT_OK = ("BATTLE RESPONSIBILITY GUARD OK ({mechanisms} mechanisms, {files} files scanned)")
VERDICT_FAILED = "BATTLE RESPONSIBILITY GUARD FAILED"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_REGISTRY = "scripts/battle-responsibility.v1.json"
DEFAULT_SCAN_ROOTS = ("src", "tools")

# Directory names that are never source. `TestResults` is here because a failed test run leaves
# copies of the sources there, and scanning those would report a mechanism that exists only as a
# test artefact.
SKIP_DIRECTORIES = frozenset({"obj", "bin", "node_modules", ".git", ".vs", "TestResults"})
TEMP_DIRECTORY_PREFIX = ".tmp-"

# PowerShell's `-match` folds case. See the module docstring.
FLAGS = re.IGNORECASE

# `shape` is optional; anything that is not exactly "banned" means "owned".
SHAPE_BANNED = "banned"


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def source_files(root: Path, scan_roots: list[str]) -> list[Path]:
    """`.cs` files under the scan roots, skipping build output and surviving unreadable trees.

    An unreadable directory is SKIPPED, not a finding. Measured: a `.tmp-*` directory whose ACL
    denied enumeration made the original THROW, which reddened CI for a week with nothing wrong with
    the code it guards. The original handled it with a `catch { continue }` around the enumeration,
    and that behaviour is the contract - a guard that cannot see into a directory must not claim the
    tree is clean either, so it is reported in the envelope rather than passed over in silence.
    """
    found: list[Path] = []
    unreadable: list[str] = []
    for name in scan_roots:
        base = root / name
        if not base.is_dir():
            continue
        stack = [base]
        while stack:
            directory = stack.pop()
            leaf = directory.name
            if leaf in SKIP_DIRECTORIES or leaf.startswith(TEMP_DIRECTORY_PREFIX):
                continue
            try:
                stack.extend(p for p in directory.iterdir() if p.is_dir())
                found.extend(p for p in directory.iterdir()
                             if p.is_file() and p.suffix.lower() == ".cs")
            except OSError as exc:
                unreadable.append(f"{directory}: {exc}")
    return sorted(found), unreadable


def load_registry(path: Path) -> dict:
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError as exc:
        # A MISSING REGISTRY, not a violation. The original reported it as a FAILED verdict with
        # exit 1; the exit code is kept so a caller reading it behaves the same, and the envelope
        # says which of the two things happened.
        raise Refusal("MISSING-REGISTRY", str(path)) from exc
    except OSError as exc:
        raise Refusal("UNREADABLE-REGISTRY", f"{path}: {exc}") from exc
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise Refusal("REGISTRY-NOT-JSON", f"{path}: {exc}") from exc
    if not isinstance(data, dict) or not isinstance(data.get("mechanisms"), list):
        raise Refusal("REGISTRY-SHAPE-UNEXPECTED", f"{path}: no 'mechanisms' array")
    return data


def scan_roots_of(registry: dict) -> list[str]:
    roots = registry.get("scan")
    if isinstance(roots, list) and roots:
        return [str(r) for r in roots]
    return list(DEFAULT_SCAN_ROOTS)


def check(root: Path, registry_path: Path) -> dict:
    registry = load_registry(registry_path)
    files, unreadable = source_files(root, scan_roots_of(registry))

    # Strip ONCE per file, not once per pattern: nineteen mechanisms over thousands of files, and
    # stripping is the expensive half. The original made the same call for the same reason.
    stripped: dict[str, str] = {}
    for path in files:
        try:
            stripped[path.relative_to(root).as_posix()] = \
                strip_comments_and_literals_preserving_layout(
                    path.read_text(encoding="utf-8", errors="replace"))
        except OSError as exc:
            unreadable.append(f"{path}: {exc}")

    findings: list[dict] = []

    def report(rule: str, message: str) -> None:
        findings.append({"rule": rule, "file": registry_path.name, "message": message})

    for mechanism in registry["mechanisms"]:
        if not isinstance(mechanism, dict):
            continue
        label = f"#{mechanism.get('id')} {mechanism.get('name')}"
        owner = str(mechanism.get("owner", ""))
        decisions = mechanism.get("decisions")
        decisions = decisions if isinstance(decisions, list) else []

        # Rule 1: a mechanism with nothing to check must say why.
        if not decisions:
            if not mechanism.get("note"):
                report("unfenced-mechanism",
                       f"{label} : no decision patterns and no note. Every registered mechanism "
                       "either carries a mechanical signature or states why it cannot carry one yet.")
            continue

        for decision in decisions:
            if not isinstance(decision, dict):
                continue
            did = decision.get("id")
            shape = decision.get("shape")
            banned = shape == SHAPE_BANNED

            allowed: dict[str, str] = {}
            if not banned:
                # The owner is allowed its own shape - EXCEPT for a banned shape, whose point is
                # that nobody writes it any more, including the file it was extracted from.
                allowed[owner] = "owner"
            for entry in decision.get("allow") or []:
                if not isinstance(entry, dict) or not entry.get("path"):
                    continue
                if not entry.get("module"):
                    report("allow-without-module",
                           f"{label} / {did}: allowlist entry '{entry.get('path')}' names no "
                           "owning module. An exception with no module that removes it is how "
                           "grandfathered debt becomes a template.")
                    continue
                allowed[str(entry["path"])] = str(entry["module"])

            pattern = decision.get("pattern")
            hits: list[str] = []
            if isinstance(pattern, str) and pattern:
                try:
                    compiled = re.compile(pattern, FLAGS)
                except re.error as exc:
                    # A pattern the engine cannot compile is a REGISTRY defect, and reporting it as
                    # "no hits" would make the decision look unenforced - which is the silent-green
                    # shape. Say so.
                    report("uncompilable-pattern",
                           f"{label} / {did}: pattern does not compile: {exc}")
                    continue
                hits = [rel for rel, text in sorted(stripped.items()) if compiled.search(text)]

            # Rule 2: the owner must still exhibit the shape it is registered for.
            if not banned and owner not in hits:
                report("owner-does-not-match",
                       f"{label} / {did}: the registered owner '{owner}' does not match its own "
                       "decision pattern. Either the owner moved, the pattern rotted, or this "
                       "decision is now shape 'banned'.")

            # Rule 3: anyone else matching is a second owner.
            for hit in hits:
                if hit not in allowed:
                    report("second-owner",
                           f"{label} / {did}: second owner in '{hit}'. Owner is '{owner}'. Call it "
                           "instead of rewriting it, or allowlist the file with the module id that "
                           "will remove the allowlist.")

    by_rule: dict[str, int] = {}
    for item in findings:
        by_rule[item["rule"]] = by_rule.get(item["rule"], 0) + 1
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if findings else "OK",
        "mechanisms": len(registry["mechanisms"]),
        "files_scanned": len(stripped),
        "findings": findings,
        "findings_by_rule": by_rule,
        "unreadable": unreadable,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: one owner per battle mechanism (replaces "
                    "guard-battle-responsibility.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--registry-path", type=Path, default=None,
                        help=f"the register (default: <repo>/{DEFAULT_REGISTRY})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    registry_path = args.registry_path or root / DEFAULT_REGISTRY
    try:
        result = check(root, registry_path)
    except Refusal as refusal:
        # Exit 1, not 64: the original reported a missing registry as a FAILED verdict with exit 1,
        # and a caller reading the code behaves the same. The envelope says which it was, so the two
        # are distinguishable without breaking anyone.
        print(f"{VERDICT_FAILED}: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "findings": [],
                              "findings_by_rule": {}}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK.format(mechanisms=result["mechanisms"],
                                files=result["files_scanned"]))
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for item in result["findings"]:
            print(f"  - {item['message']}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
