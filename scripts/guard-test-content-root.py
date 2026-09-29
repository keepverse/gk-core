#!/usr/bin/env python3
r"""Guard: a test's hand-rolled repo-root walk must land where it claims.

Replaces `guard-test-content-root.ps1`. The comment stripper is not reimplemented: this guard imports the
shared scanner in `gk-core/scripts/cscan.py`, as `guard-test-substrate.py` does. The relationship is stated
rather than an ordinal, because `cscan.py`'s own header says its file list is a reading that rots - the
third port to arrive would have made "second" wrong in the same way "fourth" was.

WHY THE GUARD EXISTS
--------------------
`CS-F1` was a fixture defect, not a product defect. `SpeciesModLedgerTests` resolved the seed root with
a private `..\..\..` walk from its own source directory. The file sits directly in
`gk-core/tests/FusionRpg.Data.Tests`, so three levels up is the repository root's PARENT - and because
`SeedImportRunner.FindUp` keeps walking above a wrong answer, the test silently read a DIFFERENT tree
from a lane worktree and failed only in the main checkout. The shared resolver
(`gk-core/tests/Shared/KeepverseRoots.cs`, compiled into every `*.Tests` project by `Directory.Build.props`)
fixed that one file. Nothing refused the NEXT hand-rolled walk.

THE RULE
--------
A `[CallerFilePath]`-anchored walk is the only one whose depth is statically knowable - the anchor IS
the source file's own directory - so the guard resolves it exactly and refuses the two shapes that are
wrong BY CONSTRUCTION:

  walk-escapes-root  the walk climbs past the repository root (the CS-F1 shape). `..` x N from a file
                     whose directory sits D segments below the root escapes when N > D.
  walk-misses-root   the walk lands INSIDE the repository while being used as a Keepverse root -
                     `Path.Combine(<walk>, "data" | "content" | "docs" | "tasks", ...)` where the walk
                     did not reach depth 0. That is the quieter half of the same defect: `..` x 2 from
                     a depth-3 test directory names `tests/data`, which does not exist, and the failure
                     surfaces as an absent-fixture answer instead of a wrong path.

WHAT IT DELIBERATELY DOES NOT DO
--------------------------------
It does not ban `..` in `tests/**`. Most walks in this tree are correct and are not repo-root walks at
all - `Path.Combine(goldenDir, "..")`, a walk to `gk-core/tests/fixtures`, or a RUNTIME walk-up loop
(`ResolvableHereTests.FindSourceFile` climbs with `Directory.GetParent` until it finds the file).
Refusing those would need a ~30-file rewrite plus a baseline, and a baseline on a depth-fragile walk is
the exact thing this guard exists to prevent. The rule is stated over the RESOLUTION, so a correct walk
passes and a wrong one bites without touching the rest.

BOUNDS, STATED RATHER THAN HIDDEN
---------------------------------
  * Only `[CallerFilePath]`-anchored walks are in scope. A walk anchored at `AppContext.BaseDirectory`
    or `Environment.CurrentDirectory` has a depth that depends on the build layout, so no static rule
    can resolve it - and a rule that guessed would be a false positive.
  * A local assigned more than once (a walk-up loop's cursor) is OPAQUE, and the opacity is
    ASYMMETRIC on purpose. It suppresses `walk-misses-root` and not `walk-escapes-root`, because the
    two findings rest on different premises: "it did not reach depth 0" is a claim about a STABLE
    anchor and is unprovable through a loop cursor, while "it climbed past the root" is a floor the
    walk cannot be below whatever the cursor did. `Pure` carries that asymmetry through every hop.
  * The scanner strips `//` and `/* */` comments but KEEPS string literals, because the `".."`
    literals and the `[CallerFilePath]` attribute are the subject. The original's comment here claims a
    walk written INSIDE a string literal is "therefore scanned", and that is the one overreach in it:
    keeping literals is what makes a real walk visible, but C# escapes a `".."` written in a literal as
    `\"..\"`, and that never matches the `".."` argument token. So code that plants a walk in a literal
    is unreachable to this guard - measured, not inferred, and it is why the original's own test
    assembles its planted walk at RUN TIME rather than writing one into this tree. The bound is
    harmless in practice (a literal is not executed) and it is stated rather than left implied.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE PURE-POWERSHELL SHAPE WAS A TRAP, AND IT IS RECORDED IN THE ORIGINAL.** `Get-RestArgs` has a
  comment saying its parameter is deliberately NOT named `$Args`, because inside a function that name
  binds to the automatic unbound-argument variable and the declared parameter never receives the value
  - found by the planted-violation check, where EVERY walk counted zero. Python has no such binding, so
  the hazard is gone, and the note is kept so nobody reintroduces the equivalent.
* **The same relative-path surgery as the two guards before it.** The original builds each file's
  repository-relative path as `$file.FullName.Substring($Root.Length)`. Its own header records a
  related 8.3 short-name incident and fixes the DEPTH arithmetic with `Path.GetFullPath` - which
  normalises separators but does NOT expand a short name, so the slice can still chop when the caller's
  `--root` and the enumerator disagree about the path's length. The port uses `Path.relative_to`, which
  cannot chop; the differential declares the difference rather than reproducing it.
* **The counts are a READING** (validation-ssot.md) and are printed on the SUCCESS path too, because
  they are the only way to see that the resolver is finding walks at all - a guard that silently
  resolved zero walks looks exactly like a clean tree.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cscan  # noqa: E402  (the shared scanner lives beside this tool)

GUARD_ID = "test-content-root"
EXIT_OK = 0
EXIT_FAILED = 1

# The four names a Keepverse root resolves to. CLOSED vocabulary: `data`/`content` are the gk-data
# pack's trees, `docs`/`tasks` the workspace's - the same four the split plan's resolver table and
# `gk-core/tests/Shared/KeepverseRoots.cs` name. `-contains` folds in the original, so `DATA` is accepted, and
# the port folds on the same comparison.
ROOT_SEGMENTS = ("data", "content", "docs", "tasks")
BUILD_DIRS = ("obj", "bin")
# `Path` may be written with any namespace prefix (`System.IO.Path.Combine`, `IO.Path.Combine`). The
# rule is about the SHAPE of the walk, so the pattern is qualified-tolerant: a test that spells the
# class out is the same walk, and pinning the unqualified form would let the next one through. Found by
# the original's planted probe: its own fixture wrote `System.IO.Path.Combine` and resolved zero walks.
PATH_QUAL = r"(?:[A-Za-z_]\w*\s*\.\s*)*Path"
# Four fixpoint passes is enough for the chains this tree uses (`testsDir` -> `repo` ->
# `Path.Combine(repo, ...)`). The original calls it a fixpoint over a tiny graph, not a budget; it is
# a number either way, so it is a named constant rather than a bare `4`.
MAX_PASSES = 4

COMBINE_CALL = re.compile(rf"{PATH_QUAL}\s*\.\s*Combine\s*\(")
CALLER_FILE_PATH = re.compile(r"CallerFilePath[^\]]*\]\s*string\s+(\w+)")
GET_FULL_PATH_WRAPPED = re.compile(rf"^{PATH_QUAL}\s*\.\s*GetFullPath\s*\(\s*(.*)\s*\)$", re.DOTALL)
GET_DIRECTORY_NAME_OF = re.compile(rf"^{PATH_QUAL}\s*\.\s*GetDirectoryName\s*\(\s*(\w+)\s*\)$")
DIRECTORY_NAME_LOCAL = re.compile(rf"var\s+(\w+)\s*=\s*{PATH_QUAL}\s*\.\s*GetDirectoryName\s*\(\s*(\w+)\s*\)")
ASSIGNED_HERE = re.compile(rf"(\w+)\s*=\s*(?:{PATH_QUAL}\s*\.\s*GetFullPath\s*\(\s*)?$")
DOTS = '".."'


class Refusal(Exception):
    """A named precondition failure."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Anchor:
    """A local's resolved DEPTH below the repository root, and whether it is a stable anchor.

    `Pure` is false for a local assigned more than once - a walk-up loop's cursor. See the module
    docstring: the flag suppresses only `walk-misses-root`, because that finding asserts a positive
    ("it did not reach depth 0") that an unstable anchor cannot support.
    """

    depth: int
    pure: bool


@dataclass
class Walk:
    """The scan's findings, the readings, and what it refused to look at."""

    files_scanned: int = 0
    caller_file_files: int = 0
    anchored_walks: int = 0
    failures: list[str] = field(default_factory=list)


def split_top_level_args(text: str) -> list[str]:
    """Split an argument list at TOP-LEVEL commas only; a nested call's commas are not separators."""
    parts: list[str] = []
    depth, current = 0, []
    for char in text:
        if char in "([":
            depth += 1
        elif char in ")]":
            depth -= 1
        elif char == "," and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(char)
    parts.append("".join(current))
    return parts


def combine_calls(text: str) -> list[tuple[int, list[str]]]:
    """Every `Path.Combine(...)` as (offset, arguments), paren-matched so a nested call is one call."""
    calls: list[tuple[int, list[str]]] = []
    for match in COMBINE_CALL.finditer(text):
        index, depth, start = match.end(), 1, match.end()
        while index < len(text) and depth > 0:
            if text[index] == "(":
                depth += 1
            elif text[index] == ")":
                depth -= 1
            index += 1
        inner = text[start:max(start, index - 1)]
        calls.append((match.start(), split_top_level_args(inner)))
    return calls


def rest_args(items: list[str]) -> list[str]:
    """The arguments after the anchor. Empty for a one-argument call, which Python gets right for
    free - the original needed a helper and a comment because PowerShell's `1..0` range is not empty,
    it walks BACKWARDS and yields two elements, which made `Path.Combine(x)` look like it had a walk."""
    return items[1:] if len(items) > 1 else []


def normalized_expr(text: str) -> str:
    """`expr!` and a `Path.GetFullPath(expr)` wrapper are transparent to the walk."""
    value = text.strip()
    while value.endswith("!"):
        value = value[:-1].strip()
    match = GET_FULL_PATH_WRAPPED.match(value)
    if match:
        value = match.group(1).strip()
    return value


def count_assignments(text: str, name: str) -> int:
    """How many times `name =` appears, not counting `==` and not matching `x.name`."""
    return len(re.findall(r"(?<![\w.])" + re.escape(name) + r"\s*=(?!=)", text))


def walk_steps(args: list[str]) -> int:
    """How many LEADING `".."` arguments the call has. A later non-`..` argument ends the walk, so
    `Path.Combine(dir, "..", "sub")` is a one-step walk, not two."""
    steps = 0
    for arg in rest_args(args):
        if arg.strip() == DOTS:
            steps += 1
        else:
            break
    return steps


def line_of(text: str, index: int) -> int:
    return cscan.line_of(text, index)


def _resolve_first(first: str, anchors: dict[str, Anchor], walks: dict[str, Anchor], params: list[str],
                   depth: int) -> Anchor | None:
    """The anchor a call's first argument resolves to, or None when it is not statically resolvable.

    Three shapes qualify, and nothing else: a walk local, a `[CallerFilePath]` parameter, or an INLINE
    `Path.GetDirectoryName(<param>)` of such a parameter. The inline case is anchored at the file's own
    depth and is always pure, because nothing is assigned to it.
    """
    if first in walks:
        return walks[first]
    if first in anchors:
        return anchors[first]
    inline = GET_DIRECTORY_NAME_OF.match(first)
    if inline and inline.group(1) in params:
        return Anchor(depth, True)
    return None


def scan_file(path: Path, rel: str, code: str) -> tuple[list[str], int, int]:
    """Every failure in one file, and its two readings."""
    params = CALLER_FILE_PATH.findall(code)
    if not params:
        return [], 0, 0
    file_dir = re.sub(r"/[^/]+$", "", rel)
    depth = len(file_dir.split("/")) if file_dir else 0

    anchors: dict[str, Anchor] = {}
    for param in params:
        anchors[param] = Anchor(depth, count_assignments(code, param) <= 1)
        for local, source in DIRECTORY_NAME_LOCAL.findall(code):
            if source == param:
                anchors[local] = Anchor(depth, count_assignments(code, local) <= 1)

    calls = combine_calls(code)
    walks: dict[str, Anchor] = {}
    for _pass in range(MAX_PASSES):
        for index, args in calls:
            if not args:
                continue
            first = normalized_expr(args[0])
            resolved = _resolve_first(first, anchors, walks, params, depth)
            if resolved is None:
                continue
            steps = walk_steps(args)
            assigned = ASSIGNED_HERE.search(code[:index])
            if assigned:
                name = assigned.group(1)
                walks[name] = Anchor(resolved.depth - steps,
                                     resolved.pure and count_assignments(code, name) <= 1)

    failures: list[str] = []
    anchored = 0
    for index, args in calls:
        if not args:
            continue
        first = normalized_expr(args[0])
        resolved = _resolve_first(first, anchors, walks, params, depth)
        if resolved is None:
            continue
        steps = walk_steps(args)
        # A call with no `..` AND no walk local is an ordinary combine, not a walk. Skipping it is what
        # lets the guard refuse nothing in the ~30 correct walks this tree has.
        if steps == 0 and first not in walks:
            continue
        anchored += 1
        landed = resolved.depth - steps
        line = line_of(code, index)
        if landed < 0:
            # Deliberately NOT gated on `resolved.pure`. A walk below the root is below it whatever
            # the cursor did, so the original fires this through an opaque anchor too; only the
            # misses-root finding below is gated. The differential pins both halves.
            how = (f'"{first}" (a walk local) from the repository root' if first in walks
                   else f'{steps} ".." from depth {resolved.depth}')
            failures.append(f"{rel}:{line}: walk-escapes-root - {how} resolves {abs(landed)} "
                            "level(s) ABOVE the repository root")
            continue
        tail = rest_args(args)[steps:]
        if tail and resolved.pure:
            nxt = tail[0].strip().strip('"')
            # `-contains` folds, so `DATA` names a Keepverse root exactly as `data` does.
            if nxt.casefold() in {s.casefold() for s in ROOT_SEGMENTS} and landed != 0:
                how = (f'"{first}" resolves' if first in walks
                       else f'{steps} ".." from depth {resolved.depth} lands')
                failures.append(f'{rel}:{line}: walk-misses-root - {how} {landed} level(s) BELOW the '
                                f'repository root, then names "{nxt}" (a Keepverse root)')
    return failures, 1, anchored


def iter_test_sources(tests_dir: Path):
    if not tests_dir.is_dir():
        return
    for path in sorted(tests_dir.rglob("*.cs"), key=lambda p: str(p).casefold()):
        if {part.casefold() for part in path.parts} & set(BUILD_DIRS):
            continue
        yield path


def scan(root: Path) -> Walk:
    tests_dir = root / "tests"
    if not tests_dir.is_dir():
        raise Refusal("TESTS-MISSING", f"tests/ is not a directory: {tests_dir}")
    result = Walk()
    for path in iter_test_sources(tests_dir):
        result.files_scanned += 1
        try:
            rel = path.relative_to(root).as_posix()
        except ValueError as exc:
            raise Refusal("PATH-OUTSIDE-ROOT", f"{path} is not under {root}") from exc
        try:
            raw = path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            raise Refusal("TESTS-FILE-UNREADABLE", f"{rel}: {exc}") from exc
        # Comments stripped, string literals KEPT: the `".."` literals and the `[CallerFilePath]`
        # attribute are the subject, so erasing literals would make every walk unresolvable.
        code = cscan.strip_comments_preserving_layout(raw)
        failures, callers, anchored = scan_file(path, rel, code)
        result.failures += failures
        result.caller_file_files += callers
        result.anchored_walks += anchored
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: a test's hand-rolled repo-root walk must land where it claims "
                    "(replaces guard-test-content-root.ps1).")
    parser.add_argument("--root", type=Path, default=None,
                        help="the repository to check (default: this tool's own repository)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        result = scan(root)
    except Refusal as refusal:
        if args.json:
            # A refusal is its own verdict, not a finding: a caller that treats "FAILED" as "the tree
            # has a bad walk" would report a broken invocation as a bad tree.
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "problems": [], "files_scanned": 0,
                              "caller_file_files": 0, "anchored_walks": 0}, indent=2))
        else:
            print(f"TEST CONTENT-ROOT GUARD REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    verdict = "FAIL" if result.failures else "OK"
    if args.json:
        print(json.dumps({"guard": GUARD_ID, "verdict": verdict, "problems": sorted(result.failures),
                          "files_scanned": result.files_scanned,
                          "caller_file_files": result.caller_file_files,
                          "anchored_walks": result.anchored_walks}, indent=2))
        return EXIT_FAILED if result.failures else EXIT_OK

    if result.failures:
        # Findings to stderr; the counts and the verdict are the report, so they stay on stdout - a
        # caller reading stdout alone sees the resolver's reach and the verdict, and nothing else.
        print("TEST CONTENT-ROOT GUARD FAILED - a hand-rolled repo-root walk does not land on the "
              "repository root:", file=sys.stderr)
        for failure in sorted(result.failures):
            print(f"  {failure}", file=sys.stderr)
        print("", file=sys.stderr)
        print("Use FusionRpg.TestSupport.ContentRoot.Path / CoreRoot.Path / WorkspaceRoot.Path",
              file=sys.stderr)
        print("instead of a private relative walk (tests/Shared/KeepverseRoots.cs; resolver contract "
              "in", file=sys.stderr)
        print("tasks/keepverse-split-plan.md).", file=sys.stderr)
    print(f"test-content-root guard: scanned {result.files_scanned} tests/**/*.cs file(s); "
          f"{result.caller_file_files} declare [CallerFilePath]; {result.anchored_walks} anchored "
          "walk(s) resolved against the repository root.")
    if result.failures:
        return EXIT_FAILED
    print("TEST CONTENT-ROOT GUARD OK - every [CallerFilePath]-anchored walk in tests/ lands on the "
          "repository root.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
