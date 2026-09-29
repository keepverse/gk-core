#!/usr/bin/env python3
r"""
Guard: Secondary IEffectGrantPlugin code must not call Unity / Status / Writer.

Why the PowerShell form was retired (scripts/guard-secondary-no-unity.ps1, 71 lines):

  * **No machine-readable result.** It printed only `SECONDARY NO-UNITY GUARD OK`, so it could be
    grepped, misread and never asserted on. `--json` reports the verdict, how many files were
    scanned, why each was in scope, and every finding with a FILE:LINE — the original gave only
    `<file>: matches /<pattern>/` with no location.
  * **The scope decision was invisible.** A file is in scope either because it sits under
    `FusionRpg.Core/Effects/Plugins/` or because its text declares `IEffectGrantPlugin`, and the
    original did not record which. `--json` reports the reason per file, because "why is this
    file being scanned" is the first question anyone has when this guard goes red on a file they
    did not know was covered.
  * **`$scanned` was a script-scoped mutable hashtable used as a dedupe set across two
    independent `Get-ChildItem` walks.** In Python that is a plain `set`, and the scope is a
    function parameter rather than `$script:` state — a guard that carries state between walks is
    a guard whose behaviour depends on walk order.

BEHAVIOUR IS PRESERVED EXACTLY, INCLUDING ONE KNOWN ROUGH EDGE
----------------------------------------------------------------
This guard scans RAW TEXT. It does not strip comments, so a comment that merely NAMES a banned
symbol — `// TODO: revisit UnityEngine` — is reported as a finding. That is a false-positive
source, and the shared scanner in `gk-core/scripts/cscan.py` would remove it.

It is deliberately NOT changed here. Stripping comments would WIDEN what this guard permits, and
that is a contract change wearing a port's clothes. The false positive is recorded here as a
finding for the owner rather than silently resolved, because a guard that goes loudly red on a
comment is a nuisance while a guard that quietly stops catching something is a hole. Contrast
`guard-dal.py`, where the same class of fix WAS applied — there the repo had already written down
that the false positives "teach people to stop writing the explanation", and the port was
measured to keep every true positive. No such statement exists for this guard, so the decision
belongs to the owner, not to the porter.

The pattern that selects in-scope files, `:\s*.*IEffectGrantPlugin`, is odd but is carried over
verbatim rather than "improved": changing which files the guard covers is a scope change, and a
port that quietly widens or narrows coverage is how a guard stops meaning what its registry row
says it means.

Verdict strings are byte-preserved — the Guard test suite asserts on them with `Assert.Contains`.

Usage (repo root):
    python gk-core/scripts/guard-secondary-no-unity.py
    python gk-core/scripts/guard-secondary-no-unity.py --root <path>    # falsifier fixture
    python gk-core/scripts/guard-secondary-no-unity.py --json

Exit 0 = clean. 1 = a finding. 64 = REFUSED (no `src/` tree, so the guard has no subject and
reporting clean would be a false green).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# The guard's CONTRACT, carried over unchanged so this port cannot quietly widen or narrow it.
BANNED_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = tuple(
    (raw, re.compile(raw, re.IGNORECASE)) for raw in (
        r"UnityEngine",
        r"HarmonyLib",
        r"StatusExecutor",
        r"EntityStatWriter",
        r"FindObjectsOfType",
        r"CreateZombie",
    )
)

# Preserved verbatim from the original. See the module docstring: odd, but changing it changes
# which files the guard covers, which is a scope decision and not a port decision.
IN_SCOPE_DECLARATION = re.compile(r":\s*.*IEffectGrantPlugin")

PLUGIN_DIR_PARTS = ("FusionRpg.Core", "Effects", "Plugins")
GENERATED_SEGMENT = re.compile(r"[\\/](obj|bin)[\\/]")
CSPROJ_PATTERN = re.compile(r"UnityEngine", re.IGNORECASE)

VERDICT_OK = "SECONDARY NO-UNITY GUARD OK — plugins Grant/Withdraw only"
VERDICT_FAILED = "SECONDARY NO-UNITY GUARD FAILED — Unity/Status/Writer refs in Secondary plugins:"


class Refusal(Exception):
    """A named precondition failure. Never reported as a pass."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason + (f"\n{detail}" if detail else ""))
        self.reason, self.detail = reason, detail


def _rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def _under_plugin_dir(path: Path) -> bool:
    parts = path.parts
    return all(part in parts for part in PLUGIN_DIR_PARTS)


def _scan_file(root: Path, path: Path, reason: str) -> tuple[list[dict], str | None]:
    """Return (findings, unreadable_reason). A file that cannot be read is a refusal-shaped
    condition, not a silent skip: a file this guard cannot read is one nobody checked."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return [], f"{_rel(root, path)}: {type(exc).__name__}: {exc}"
    if not text.strip():
        return [], None
    findings = []
    for pattern_text, pattern in BANNED_PATTERNS:
        match = pattern.search(text)
        if match is not None:
            findings.append({
                "file": _rel(root, path),
                "line": text.count("\n", 0, match.start()) + 1,
                "pattern": pattern_text,
                "scope_reason": reason,
                "excerpt": text[max(0, match.start() - 40):match.end() + 40].strip(),
            })
    return findings, None


def scan(root: Path) -> dict:
    """Perform the check. Raises Refusal when it cannot be performed."""
    src = root / "src"
    if not src.is_dir():
        raise Refusal("SRC-MISSING",
                      f"{src} does not exist, so there is no Secondary plugin surface to check; "
                      f"reporting clean would be a false green")

    findings: list[dict] = []
    unreadable: list[str] = []
    in_scope: dict[Path, str] = {}

    # Pass 1: everything under the plugin directory.
    plugin_dir = src.joinpath(*PLUGIN_DIR_PARTS)
    if plugin_dir.is_dir():
        for cs in sorted(plugin_dir.rglob("*.cs")):
            if GENERATED_SEGMENT.search(cs.as_posix()):
                continue
            in_scope[cs] = "plugin-directory"

    # Pass 2: anything anywhere under src/ that DECLARES the interface, deduplicated against
    # pass 1 exactly as the original's $scanned hashtable did.
    for cs in sorted(src.rglob("*.cs")):
        if GENERATED_SEGMENT.search(cs.as_posix()) or cs in in_scope:
            continue
        try:
            text = cs.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            unreadable.append(f"{_rel(root, cs)}: {type(exc).__name__}: {exc}")
            continue
        if text.strip() and IN_SCOPE_DECLARATION.search(text):
            in_scope[cs] = "declares-IEffectGrantPlugin"

    for cs, reason in sorted(in_scope.items()):
        found, problem = _scan_file(root, cs, reason)
        findings.extend(found)
        if problem:
            unreadable.append(problem)

    # The Core project file itself must not reference UnityEngine.
    csproj = src / "FusionRpg.Core" / "FusionRpg.Core.csproj"
    projects_scanned = 0
    if csproj.is_file():
        projects_scanned = 1
        text = csproj.read_text(encoding="utf-8", errors="replace")
        match = CSPROJ_PATTERN.search(text)
        if match is not None:
            findings.append({
                "file": _rel(root, csproj),
                "line": text.count("\n", 0, match.start()) + 1,
                "pattern": "UnityEngine",
                "scope_reason": "core-project-file",
                "excerpt": "references UnityEngine",
            })

    if unreadable:
        raise Refusal("SOURCE-UNREADABLE", "; ".join(unreadable))

    return {
        "guard": "secondary-no-unity",
        "files_scanned": len(in_scope),
        "projects_scanned": projects_scanned,
        "scope_reasons": sorted({r for r in in_scope.values()}),
        "findings": findings,
        "verdict": "FAIL" if findings else "OK",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: Secondary IEffectGrantPlugin code must not call Unity / Status / Writer.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"SECONDARY NO-UNITY GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": "secondary-no-unity", "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        return 64

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding['file']}:{finding['line']}: matches /{finding['pattern']}/ "
                  f"[{finding['scope_reason']}] — {finding['excerpt']}", file=sys.stderr)
    return 0 if result["verdict"] == "OK" else 1


if __name__ == "__main__":
    sys.exit(main())
