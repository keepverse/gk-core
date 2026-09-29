#!/usr/bin/env python3
"""
Guard: SQLite / SQL only inside FusionRpg.Data.

Why the PowerShell form was retired (scripts/guard-dal.ps1, 66 lines):

  * **Comment stripping was line-prefix only and got block comments wrong.** It dropped any line
    whose first non-space characters were `//`, `*` or `/*`, so a `/* ... */` comment whose body
    put the SQL keyword on a line that did NOT start with `*` or `/*` was still scanned, and a
    trailing comment after real code (`var x = 1; // see PRAGMA`) was scanned too. Both directions
    are wrong: false positives train people to stop writing the explanation, and the repo's own
    comment said so. This port uses one real scanner (`gk-core/scripts/cscan.py`) that strips `//` and
    `/* */` properly while PRESERVING string literals - because raw SQL handed to a driver as a
    string literal is a real finding, and only a comment makes a keyword inert.
  * **`-AllowlistFiles` matched on file NAME, not path.** `if ($AllowlistFiles -contains $name)`
    compared `Foo.cs`, so one allowlist entry silently exempted EVERY `Foo.cs` in the tree. The
    capability is kept, but a bare filename is now reported as a broad basename exemption in
    `--json` rather than passing as quietly as it did.
  * **No machine-readable result.** It printed only `DAL GUARD OK`, which is greppable, misreadable
    and unassertable. `--json` reports the verdict, the exemption mode and every finding with a
    FILE:LINE an operator can jump to, where the original gave only `<file>: matches /<pattern>/`.
  * **Findings had no location and no pattern identity** beyond the raw regex, so two findings on
    one file were indistinguishable.

There is deliberately NO `--timeout` claim here: this guard runs no external process, so the
unbounded-subprocess defect that retired other PowerShell tools does not apply to it. The standard
is applied where it is real, not uniformly for its own sake.

The contract is unchanged: SQL and the SQLite driver live only under `gk-core/src/FusionRpg.Data`, and
`*.csproj` outside it must not carry a `Microsoft.Data.Sqlite` PackageReference. Generated build
output (`bin`/`obj`, WPF `*.g.cs`) is not product source and is skipped.

Usage (repo root):
    python gk-core/scripts/guard-dal.py
    python gk-core/scripts/guard-dal.py --root <path>                  # falsifier fixture
    python gk-core/scripts/guard-dal.py --allow src/Foo/Bar.cs          # precise, repo-relative
    python gk-core/scripts/guard-dal.py --allow Bar.cs                  # broad: every Bar.cs, reported
    python gk-core/scripts/guard-dal.py --json

Exit 0 = clean. 1 = a finding. 64 = REFUSED (the data project is missing, so the check has no
subject and reporting "clean" would be a false green).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cscan import line_of, strip_comments  # noqa: E402 - sibling module, path set above

# The pattern set is the guard's CONTRACT and is carried over unchanged, so this port cannot
# quietly widen or narrow what counts as a violation. Compiled once, case-insensitive as before.
CODE_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = tuple(
    (raw, re.compile(raw, re.IGNORECASE)) for raw in (
        r"Microsoft\.Data\.Sqlite",
        r"SqliteConnection",
        r"SqliteCommand",
        r"SqliteTransaction",
        r"PRAGMA\s+",
        r"CREATE\s+TABLE",
        r"INSERT\s+INTO",
        r"BEGIN\s+IMMEDIATE",
    )
)

CSPROJ_PATTERN = re.compile(r"Microsoft\.Data\.Sqlite", re.IGNORECASE)
GENERATED_SEGMENT = re.compile(r"[\\/](obj|bin)[\\/]")


class Refusal(Exception):
    """A named precondition failure. Never reported as a pass."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}" + (f"\n{detail}" if detail else ""))
        self.reason, self.detail = reason, detail


def _is_under(path: Path, parent: Path) -> bool:
    """True when `path` is inside `parent`, comparing resolved absolute paths. Used for the
    FusionRpg.Data exclusion, which must not be defeated by `..` or a case difference on Windows."""
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def _exemption_mode(spec: str) -> str:
    """`path` for a precise repo-relative exemption, `basename` for the broad legacy form. Surfaced
    in --json so a broad exemption is visible in the result instead of hiding in a flag."""
    return "basename" if "/" not in spec and "\\" not in spec else "path"


def scan(root: Path, allow_specs: list[str]) -> dict:
    """Perform the check. Raises Refusal when it cannot be performed."""
    data_dir = root / "src" / "FusionRpg.Data"
    src_dir = root / "src"
    if not data_dir.is_dir():
        raise Refusal("DATA-PROJECT-MISSING",
                      f"{data_dir} does not exist, so 'SQL only inside FusionRpg.Data' has no "
                      f"boundary to enforce; reporting clean would be a false green")
    if not src_dir.is_dir():
        raise Refusal("SRC-MISSING", f"{src_dir} does not exist, so there is nothing to scan")

    precise = {s.replace("\\", "/") for s in allow_specs if _exemption_mode(s) == "path"}
    basenames = {s for s in allow_specs if _exemption_mode(s) == "basename"}

    findings: list[dict] = []
    scanned = exempted = 0

    for cs in sorted(src_dir.rglob("*.cs")):
        if _is_under(cs, data_dir):
            continue
        if GENERATED_SEGMENT.search(cs.as_posix()):
            continue  # generated build output is not product source
        rel = cs.relative_to(root).as_posix()
        if rel in precise or cs.name in basenames:
            exempted += 1
            continue
        scanned += 1
        try:
            raw = cs.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            # An unreadable source file is a REFUSAL-shaped condition, not a silent skip: a file
            # this guard cannot read is a file whose SQL nobody has checked.
            raise Refusal("SOURCE-UNREADABLE", f"{rel}: {type(exc).__name__}: {exc}") from exc
        code = strip_comments(raw)
        for pattern_text, pattern in CODE_PATTERNS:
            match = pattern.search(code)
            if match is None:
                continue
            findings.append({
                "file": rel,
                "line": line_of(code, match.start()),
                "pattern": pattern_text,
                "excerpt": code[max(0, match.start() - 40):match.end() + 40].strip(),
            })

    projects_scanned = 0
    for proj in sorted(src_dir.rglob("*.csproj")):
        if _is_under(proj, data_dir):
            continue
        rel = proj.relative_to(root).as_posix()
        if rel in precise or proj.name in basenames:
            continue
        projects_scanned += 1
        text = proj.read_text(encoding="utf-8", errors="replace")
        match = CSPROJ_PATTERN.search(text)
        if match is not None:
            findings.append({
                "file": rel,
                "line": line_of(text, match.start()),
                "pattern": "PackageReference Microsoft.Data.Sqlite",
                "excerpt": "outside FusionRpg.Data",
            })

    return {
        "guard": "dal",
        "root": str(root),
        "scanned_cs_files": scanned,
        "scanned_projects": projects_scanned,
        "exempted_files": exempted,
        "exemptions": [{"spec": s, "mode": _exemption_mode(s)} for s in allow_specs],
        "findings": findings,
        "verdict": "FAIL" if findings else "OK",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: SQLite / SQL only inside FusionRpg.Data.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--allow", action="append", default=[], metavar="PATH",
                        help="exempt a file. A repo-relative path is precise; a bare filename "
                             "exempts EVERY file of that name and is reported as mode 'basename'")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = scan(args.root.resolve(), args.allow)
    except Refusal as refusal:
        print(f"DAL GUARD REFUSED {refusal}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": "dal", "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail}, indent=2))
        return 64

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        broad = [e["spec"] for e in result["exemptions"] if e["mode"] == "basename"]
        suffix = f" (broad basename exemption: {', '.join(broad)})" if broad else ""
        print(f"DAL GUARD OK — no SQLite/SQL outside FusionRpg.Data{suffix}")
    else:
        print(f"DAL GUARD FAILED — {len(result['findings'])} database access site(s) outside "
              f"FusionRpg.Data:", file=sys.stderr)
        for finding in result["findings"]:
            print(f"  {finding['file']}:{finding['line']}: matches /{finding['pattern']}/ "
                  f"— {finding['excerpt']}", file=sys.stderr)
    return 0 if result["verdict"] == "OK" else 1


if __name__ == "__main__":
    sys.exit(main())
