#!/usr/bin/env python3
r"""
Guard: open/closed identity (solid-enforcement commander-identity, SE4.4).
spec: docs/architecture/solid-enforcement/spec-commander-identity.md "The regression guard"

Why the PowerShell form was retired (scripts/guard-open-identity.ps1, 110 lines):

  * **Comment skipping was a line-prefix test, and the guard's own header says that is wrong.**
    `Get-CodeLines` dropped any line whose first non-space characters were `//`, `*` or `/*`, so
    the body of a multi-line `/* ... */` comment WAS scanned - and a doc comment naming these very
    types is exactly what the header says must not be a violation ("a doc comment naming these
    types (this very script's own header, or ICommanderDirectory.cs's summary) is not a
    violation"). The guard therefore did not do what its own comment said it did. This port uses
    the shared scanner `gk-core/scripts/cscan.py`, which strips `//` and `/* */` properly while PRESERVING
    string literals - so a `switch` inside a string is still seen, which matters because I2 is
    textual and the spec is candid that it is not a type-checker.
  * **No machine-readable result.** `--json` now reports the verdict, the file and declaration
    counts, and every finding carrying `I1` or `I2` as a FIELD, so counting per invariant is not
    parsing English. The original mixed the invariant id into a prose string.
  * **The `switch` window was 80 characters in each direction with no statement of why.** That
    magic number is now `SWITCH_WINDOW`, named, with the reason it exists: the subject of a
    `switch` expression sits immediately before the keyword and the subject of a `switch`
    statement sits immediately inside the parenthesis after it, so the window only has to be long
    enough to hold an identifier plus its cast.
  * **A missing `src/` was a bare `throw`.** Now a named `SRC-MISSING` refusal with exit 64, so
    "clean" is never reported for a tree that was never scanned.

BEHAVIOUR: preserved, with ONE deliberate fix, measured rather than assumed
---------------------------------------------------------------------
The comment-stripping fix is the only intended behaviour change, and it can only REMOVE false
positives: a keyword inside a comment is not code, while code is never inside a comment. The port
was differential-tested against the original on a fixture carrying all three violation shapes; the
result is recorded in the port commit. If a future change makes the two disagree, that fixture is
the thing to re-run.

I2 remains TEXTUAL, not a C# type-checker, exactly as the spec states ("Guard I2 cannot see a
string switch, so the Open/Closed test below is what catches a new one" - this guard was never
meant to be the only defence). It catches the realistic shapes: a switch whose subject is a local
or parameter declared `EmpireId <name>` / `CommanderRef <name>` earlier in the same file, and a
switch whose subject is cast directly to one of those types.

Verdict strings are byte-preserved - the Guard suite Assert.Contains them.

Usage (repo root):
    python gk-core/scripts/guard-open-identity.py
    python gk-core/scripts/guard-open-identity.py --root <path>     # falsifier fixture
    python gk-core/scripts/guard-open-identity.py --json

Exit 0 = clean. 1 = a finding. 64 = REFUSED (no src/, so the invariants have no subject).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cscan import strip_comments  # noqa: E402 - sibling module, path set above

I1 = "I1"
I2 = "I2"

# The two namespaces the owner's ruling covers. A repo-wide "no enum ending in Id" rule would hit
# legitimate closed vocabularies (AtomKind ids and the like), so the scan is deliberately narrow.
SCOPED_DIRS = (("FusionRpg.Core", "Commanders"), ("FusionRpg.Core", "World"))

ENUM_DECL = re.compile(r"\benum\s+(\w+)")
ENUM_NAME_IS_ID = re.compile(r"Ids?$")
TYPED_DECL = re.compile(r"\b(?:EmpireId|CommanderRef)\s+(\w+)\b")
SWITCH_KEYWORD = re.compile(r"\bswitch\b")
# A cast directly onto the switch subject: `(EmpireId)x switch` or `switch ((EmpireId)x)`.
CAST_BEFORE = re.compile(r"\((?:EmpireId|CommanderRef)\)\s*\w*\s*$")
CAST_AFTER = re.compile(r"^\s*\(\s*\((?:EmpireId|CommanderRef)\)")
IDENTIFIER_TAIL = re.compile(r"(\w+)\s*$")
PAREN_SUBJECT = re.compile(r"^\s*\(\s*(\w+)\s*\)")

# Long enough to hold an identifier plus its cast - that is all either subject form needs.
SWITCH_WINDOW = 80
GENERATED_SEGMENT = re.compile(r"[\\/](obj|bin)[\\/]")

VERDICT_OK = ("OPEN-IDENTITY GUARD OK — no *Id/*Ids enum under Commanders/World, "
              "no switch over EmpireId/CommanderRef")
VERDICT_FAILED = "OPEN-IDENTITY GUARD FAILED:"


class Refusal(Exception):
    """A named precondition failure. Never reported as a pass."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason + (f"\n{detail}" if detail else ""))
        self.reason, self.detail = reason, detail


def _cs_files(root: Path) -> list[Path]:
    """Every product .cs under src/, generated output excluded. Generated build output is not
    product source; the skip is by PATH SEGMENT, never by file extension."""
    src = root / "src"
    return [p for p in sorted(src.rglob("*.cs"))
            if not GENERATED_SEGMENT.search(p.as_posix())]


def _read_code(path: Path) -> str:
    """Source with comments removed and string literals kept.

    A `switch` inside a string literal is still visible, which matters because I2 is textual: the
    spec is explicit that this guard is not a type-checker and the Open/Closed tests are the real
    defence, so narrowing what the text can see would narrow the guard for no stated reason.
    """
    return strip_comments(path.read_text(encoding="utf-8", errors="replace"))


def check_i1(root: Path) -> list[dict]:
    """I1 - no `enum` named *Id / *Ids under Commanders/ or World/."""
    findings: list[dict] = []
    for parts in SCOPED_DIRS:
        directory = root.joinpath("src", *parts)
        if not directory.is_dir():
            continue                      # a missing scoped dir is not a finding; the guard is narrow
        for path in sorted(directory.rglob("*.cs")):
            if GENERATED_SEGMENT.search(path.as_posix()):
                continue
            for match in ENUM_DECL.finditer(_read_code(path)):
                name = match.group(1)
                if ENUM_NAME_IS_ID.search(name):
                    findings.append({
                        "invariant": I1,
                        "file": path.relative_to(root).as_posix(),
                        "name": name,
                        "message": (f"enum '{name}' - a closed vocabulary here belongs in "
                                    f"ICommanderDirectory / a data-backed registry, "
                                    f"never a re-invented *Id enum"),
                    })
    return findings


def check_i2(root: Path) -> list[dict]:
    """I2 - no switch (statement or expression) over an EmpireId / CommanderRef value, anywhere."""
    findings: list[dict] = []
    for path in _cs_files(root):
        code = _read_code(path)
        rel = path.relative_to(root).as_posix()

        typed_names = {m.group(1) for m in TYPED_DECL.finditer(code)}

        for match in SWITCH_KEYWORD.finditer(code):
            start = max(0, match.start() - SWITCH_WINDOW)
            before = code[start:match.start()]
            after_start = match.end()
            after = code[after_start:after_start + SWITCH_WINDOW]

            if CAST_BEFORE.search(before) or CAST_AFTER.search(after):
                findings.append({
                    "invariant": I2, "file": rel, "name": None,
                    "message": ("switch cast directly to EmpireId/CommanderRef - use "
                                "ICommanderDirectory.TryResolve, never a switch over the "
                                "identity type")})
                continue

            # Switch EXPRESSION form: `<name> switch` - `before` ends with the discriminant.
            expr = IDENTIFIER_TAIL.search(before)
            if expr and expr.group(1) in typed_names:
                name = expr.group(1)
                findings.append({
                    "invariant": I2, "file": rel, "name": name,
                    "message": (f"'{name} switch' - '{name}' is EmpireId/CommanderRef-typed in "
                                f"this file; use ICommanderDirectory.TryResolve, never a switch "
                                f"over the identity type")})
                continue

            # Switch STATEMENT form: `switch (<name>)` - the subject is inside the parenthesis.
            stmt = PAREN_SUBJECT.search(after)
            if stmt and stmt.group(1) in typed_names:
                name = stmt.group(1)
                findings.append({
                    "invariant": I2, "file": rel, "name": name,
                    "message": (f"'switch ({name})' - '{name}' is EmpireId/CommanderRef-typed in "
                                f"this file; use ICommanderDirectory.TryResolve, never a switch "
                                f"over the identity type")})
    return findings


def scan(root: Path) -> dict:
    if not (root / "src").is_dir():
        raise Refusal("SRC-MISSING",
                      f"{(root / 'src').as_posix()} does not exist, so the open/closed invariants "
                      f"have no subject; reporting clean would be a false green")
    findings = check_i1(root) + check_i2(root)
    # The original de-duplicated with `Select-Object -Unique`; kept, because two identical switches
    # in one file are one problem to fix, not two rows in a report.
    unique: list[dict] = []
    seen: set[tuple] = set()
    for finding in findings:
        key = (finding["invariant"], finding["file"], finding["name"], finding["message"])
        if key not in seen:
            seen.add(key)
            unique.append(finding)
    by_invariant: dict[str, int] = {}
    for finding in unique:
        by_invariant[finding["invariant"]] = by_invariant.get(finding["invariant"], 0) + 1
    return {
        "guard": "open-identity",
        "scoped_dirs": ["/".join(p) for p in SCOPED_DIRS],
        "switch_window": SWITCH_WINDOW,
        "files_scanned": len(_cs_files(root)),
        "findings": unique,
        "findings_by_invariant": by_invariant,
        "verdict": "FAIL" if unique else "OK",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: open/closed identity (solid-enforcement commander-identity, SE4.4).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve())
    except Refusal as refusal:
        print(f"OPEN-IDENTITY GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": "open-identity", "verdict": "REFUSED",
                              "reason": refusal.reason, "detail": refusal.detail}, indent=2))
        return 64

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        print(VERDICT_OK)
    else:
        print(VERDICT_FAILED, file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding['invariant']}: {finding['file']}: {finding['message']}",
                  file=sys.stderr)
    return 0 if result["verdict"] == "OK" else 1


if __name__ == "__main__":
    sys.exit(main())
