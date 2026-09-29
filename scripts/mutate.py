#!/usr/bin/env python3
"""Break the code on purpose and report which tests failed to notice.

Replaces `mutate.ps1`. Coverage says what the tests *touched*; mutation says what they would *notice*. A
line covered by a test that asserts nothing is 100% covered and worth nothing, and that has happened
twice in this repo already -- see `docs/research/world/mutation-pass-2026-08-22.md`.

Each mutant is one deliberate defect: a swapped constant, a dropped guard, a wrong lens. The suite runs
against it and the mutant is either **caught** (some test failed, good) or it **SURVIVED** (every test
passed while the code was wrong, which is a hole).

Mutants live in `gk-core/scripts/mutants/*.json`, so adding one is a data edit:

    [ { "file":  "gk-core/src/FusionRpg.Core/World/Ai/Hops.cs",
        "name":  "every reachable sector is one hop away",
        "find":  "distance[neighbour] = distance[current] + 1;",
        "with":  "distance[neighbour] = 1;" } ]

`--project` left empty (the default) resolves a set's suite from its first non-Python mutant's `file`
via `gk-core/tests/core-test-projects.v1.json` -- the residual today, and whichever split project the split moves
that folder's tests into once it has. A set whose own tests moved names its project explicitly instead:

    { "project": "tests/FusionRpg.Core.World.Tests", "mutants": [ { "file": ..., ... } ] }

Both shapes are read by the same loader. A surviving mutant is not automatically a bug -- some code is
unreachable through shipped content, and some detail is deliberately not load-bearing -- but each survivor
has to be *explained*, and the explanation belongs in a comment next to the code so the next person does
not re-find it.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **IT DISCARDED EVERY LINE OF TEST OUTPUT, IN A TOOL WHOSE WHOLE PURPOSE IS ATTRIBUTING FAILURES.**
  Both suite invocations ended in `*> $null`, with the comment "a mutant run is all noise". So when the
  pre-flight baseline was red, the throw read "the dotnet suite is red before any mutant was applied"
  and named not one failing test. Every "caught" verdict likewise carried zero evidence. The port keeps
  the output and reports the failure tail on the run that matters, because "caught" is a claim about a
  specific test noticing a specific defect, and the original could not say which.
* **THERE WAS NO TIMEOUT ON ANY SUITE INVOCATION, AND THIS TOOL INVOKES SUITES N+1 TIMES.** A baseline
  plus one full run per mutant. `dotnet test` that wedges held the run open forever, and a mutation pass
  is already the slowest thing anyone runs voluntarily. `--timeout` bounds each one and the refusal names
  the mutant and the stage.
* **IT REWROTE THE WHOLE MUTATED FILE THROUGH A TEXT ROUND-TRIP.** `Get-Content -Raw` then
  `Set-Content -NoNewline` normalises to the platform's line endings, so mutating a file that was stored
  with LF silently rewrote every line of it, and the re-encode could mangle a non-ASCII literal. The port
  reads and writes BYTES and splices only the matched region, so a mutant changes the bytes it names and
  nothing else. Line endings are normalised for MATCHING only, which is what the original's CRLF fix was
  for -- matching is where the normalisation belongs, not writing.
* **THE RESTORE WAS NOT VERIFIED.** `Move-Item -Force` over the `.bak` and then a `LastWriteTime` touch,
  with no check that the bytes came back. This tool is the one place in the repository that deliberately
  corrupts tracked source, so "the file is back" deserves to be a measurement. The port compares the
  restored bytes to the original and REFUSES if they differ, rather than reporting a clean run over a
  tree it damaged.
* **NO MACHINE-READABLE OUTPUT.** The per-mutant verdict is the product of the tool, and it was only
  available as coloured text scraped off stdout. `--json` exposes it.

The restore-if-killed caveat is unchanged and is a property of the approach, not of the language: if the
process dies mid-mutant, the target file stays mutated and a `.bak` sits beside it holding the original.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from core_test_project import (  # noqa: E402  (the lib shim above must run first)
    CORE_FALLBACK_PROJECT,
    project_for_token,
    source_path_token,
)

TOOL_ID = "mutate"
EXIT_OK = 0
EXIT_FAILED = 1
MUTANTS_DIR = "scripts/mutants"

# Exit-code vocabulary of a suite run, as a CLOSED set: 0 means green, anything else means "something
# failed". The tool does not care WHICH non-zero code, because a mutated build can fail to COMPILE (a
# distinct code) as easily as fail an assertion, and a compile failure is still a caught mutant -- a
# dropped guard is often a type error. Collapsing them is the original's behaviour and is correct here.
SUITE_GREEN = 0

# The pytest target for a `.py` mutant, fixed rather than inferred per set. The original hard-coded it
# too, with the same reasoning: seedsmith is the only Python suite with mutants, and inferring a target
# from a file extension would guess.
PYTEST_TARGET = "tools/seedsmith/tests"

DEFAULT_TIMEOUT_SECONDS = 3600


class Refusal(Exception):
    """A named precondition or stage failure. The run says which, and never exits 0 having not run."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Mutant:
    file: str
    name: str
    find: str
    with_: str

    @property
    def is_python(self) -> bool:
        """A `.py` target runs under pytest, not `dotnet test`.

        Inferred from the extension and never a schema field, so every existing `.cs` mutant set keeps
        running exactly as before. The original used PowerShell's `-like "*.py"`, which is a case-
        insensitive glob; `str.endswith` is not, so the comparison is folded to keep the same answer for a
        `.PY` target.
        """
        return self.file.lower().endswith(".py")


@dataclass
class MutantSet:
    name: str
    path: Path
    project: str | None
    mutants: list[Mutant] = field(default_factory=list)


@dataclass
class Verdict:
    """One mutant's outcome. `stage` names what actually happened, so "caught" is never a bare guess."""

    set: str
    name: str
    file: str
    outcome: str          # caught | survived | stale | error
    stage: str            # baseline | suite | anchor | restore
    detail: str = ""


# ------------------------------------------------------------------------------------------------
# Loading
# ------------------------------------------------------------------------------------------------

def read_set(path: Path) -> MutantSet:
    """Read one mutant set, accepting BOTH on-disk shapes.

    The bare array is what every set on disk uses; the object form wraps an explicit project override
    around the same array. `isinstance(raw, list)` is the discriminator, exactly as the original's
    `-is [array]` was -- and it is the discriminator that matters, because a set that gains a `project`
    key must not silently start resolving from its first mutant's folder instead.
    """
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise Refusal("MUTANT-SET-UNREADABLE", f"{path.name}: {exc}") from exc

    project: str | None = None
    entries = raw
    if not isinstance(raw, list):
        if not isinstance(raw, dict):
            raise Refusal("MUTANT-SET-SHAPE",
                          f"{path.name}: expected a JSON array or an object with 'mutants', "
                          f"got {type(raw).__name__}")
        project = raw.get("project")
        entries = raw.get("mutants")
        if not isinstance(entries, list):
            raise Refusal("MUTANT-SET-SHAPE", f"{path.name}: the object form needs a 'mutants' array")

    mutants: list[Mutant] = []
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise Refusal("MUTANT-SHAPE", f"{path.name}[{index}]: expected an object")
        missing = [key for key in ("file", "name", "find", "with") if key not in entry]
        if missing:
            raise Refusal("MUTANT-SHAPE",
                          f"{path.name}[{index}]: missing {', '.join(missing)}")
        mutants.append(Mutant(file=str(entry["file"]), name=str(entry["name"]),
                              find=str(entry["find"]), with_=str(entry["with"])))
    return MutantSet(name=path.stem, path=path, project=project, mutants=mutants)


def load_sets(repo: Path, wanted: str | None) -> list[MutantSet]:
    """Every set, or the one named. A name that matches nothing is a refusal, not an empty success."""
    root = repo / MUTANTS_DIR
    if not root.is_dir():
        raise Refusal("NO-MUTANTS-DIR", f"no mutant directory at {root}")
    paths = sorted(root.glob("*.json"))
    if wanted:
        paths = [p for p in paths if p.stem == wanted]
    if not paths:
        available = ", ".join(sorted(p.stem for p in root.glob("*.json"))) or "(none on disk)"
        raise Refusal("NO-MUTANT-SET",
                      f"no mutant set matched {wanted!r}; on disk: {available}")
    return [read_set(p) for p in paths]


# ------------------------------------------------------------------------------------------------
# Project resolution
# ------------------------------------------------------------------------------------------------

def resolve_project(repo: Path, set_project: str | None, override: str | None,
                    mutants: list[Mutant]) -> str:
    """The suite a set's `.cs` mutants run against.

    Precedence is the original's and it is not alphabetical: a set's OWN `project` key beats the global
    `--project`, which beats auto-resolution, which falls back to the residual. The set's own key wins
    first because it is the most specific statement of intent -- a set whose tests have split names the
    split project deliberately, and a global override would quietly run it against the wrong suite.
    """
    if set_project:
        return set_project
    if override:
        return override
    first_dotnet = next((m for m in mutants if not m.is_python), None)
    if first_dotnet is None:
        return CORE_FALLBACK_PROJECT
    return project_for_token(repo, source_path_token(first_dotnet.file))


# ------------------------------------------------------------------------------------------------
# Splicing
# ------------------------------------------------------------------------------------------------

def normalise(text: str) -> str:
    """CRLF to LF, for MATCHING only.

    A multi-line anchor written with LF endings never matches a CRLF file, and the mutant then reports
    STALE forever while looking like it ran. Normalising the SOURCE and the ANCHOR is the fix; the
    original normalised the source and the anchor but then wrote the whole file back through a
    platform-ending text round-trip, so it also rewrote every line it was not mutating.
    """
    return text.replace("\r\n", "\n")


def normalised_offsets(text: str) -> list[int]:
    """Map every index in the newline-NORMALISED text to its index in the original.

    Needed because the anchor is matched against the normalised text -- so a multi-line anchor written
    with LF endings finds its target in a CRLF file -- but the splice happens on the ORIGINAL, whose
    offsets are larger wherever a CRLF was collapsed.

    The first version of `splice` computed this arithmetically as
    `len(prefix) + prefix.count("\\n")`, which is right ONLY when the source is CRLF: for an LF file the
    prefix length already equals the normalised index, so every newline was counted twice and the
    replacement landed one character early -- `keep = 1;` became `kekeep = 2;`. A string-level test does
    not see that, because the spliced result still contains the replacement text; only a byte-for-byte
    comparison does. That is why the cases here assert exact bytes.

    Walks the original once, costing O(n) ints. A source file is kilobytes, so the map is small next to
    the cost of getting the offset wrong.
    """
    offsets = [0]
    index = 0
    length = len(text)
    while index < length:
        if text[index] == "\r" and index + 1 < length and text[index + 1] == "\n":
            index += 2
        else:
            index += 1
        offsets.append(index)
    return offsets


def splice(source: bytes, find: str, replace: str) -> bytes | None:
    """The mutated bytes, or None when the anchor is not present.

    Splices BYTES, so a file stored with CRLF keeps its CRLF everywhere except the region the mutant names.
    The original's first-occurrence-only rule is kept: two occurrences of the same anchor and a mutant that
    means the second one is a data bug, and silently mutating both would hide it.
    """
    text = source.decode("utf-8")
    index = normalise(text).find(find)
    if index < 0:
        return None
    offsets = normalised_offsets(text)
    start = offsets[index]
    end = offsets[index + len(find)]
    return (text[:start] + replace + text[end:]).encode("utf-8")


# ------------------------------------------------------------------------------------------------
# Running suites
# ------------------------------------------------------------------------------------------------

def run_dotnet_suite(project: str, filter_text: str | None, repo: Path, timeout: int) -> tuple[int, str]:
    argv = ["dotnet", "test", project, "--nologo", "-v", "q"]
    if filter_text:
        argv += ["--filter", filter_text]
    return _run(argv, repo, timeout, f"dotnet test {project}")


def run_pytest_suite(repo: Path, timeout: int) -> tuple[int, str]:
    return _run([sys.executable, "-m", "pytest", PYTEST_TARGET, "-q"], repo, timeout,
                f"pytest {PYTEST_TARGET}")


def _run(argv: list[str], repo: Path, timeout: int, what: str) -> tuple[int, str]:
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, cwd=str(repo))
    except subprocess.TimeoutExpired as exc:
        raise Refusal("SUITE-TIMEOUT", f"{what} did not exit within {timeout}s") from exc
    except OSError as exc:
        raise Refusal("SUITE-UNRUNNABLE", f"{what}: {exc}") from exc
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def tail_of(output: str, limit: int = 6) -> list[str]:
    return [line for line in output.splitlines() if line.strip()][-limit:]


# ------------------------------------------------------------------------------------------------
# The run
# ------------------------------------------------------------------------------------------------

def check_baselines(repo: Path, sets: list[MutantSet], resolved: dict[str, str],
                    filter_text: str | None, timeout: int) -> None:
    """Refuse before any mutation if a baseline is red, and say WHICH failure.

    A red baseline makes the whole run meaningless: every mutant would report "caught" because the suite
    already fails, and a compile error in somebody else's file looks exactly like a test noticing the
    defect. Checked once per DISTINCT project actually needed, up front, rather than after reading a page
    of false green.

    The original threw the same refusal but had just discarded the output, so the message named the
    project and nothing else. The tail is carried here.
    """
    dotnet_projects: list[str] = []
    for mutant_set in sets:
        if any(not m.is_python for m in mutant_set.mutants):
            project = resolved[mutant_set.name]
            if project not in dotnet_projects:
                dotnet_projects.append(project)
    for project in dotnet_projects:
        code, output = run_dotnet_suite(project, filter_text, repo, timeout)
        if code != SUITE_GREEN:
            tail = tail_of(output)
            raise Refusal("BASELINE-RED",
                          f"{project} is red before any mutant was applied: fix that first, or every "
                          f"mutant will look caught" + ("\n" + "\n".join(tail) if tail else
                                                        " (no output at all)"))

    if any(m.is_python for s in sets for m in s.mutants):
        code, output = run_pytest_suite(repo, timeout)
        if code != SUITE_GREEN:
            tail = tail_of(output)
            raise Refusal("BASELINE-RED",
                          f"pytest {PYTEST_TARGET} is red before any mutant was applied: fix that first, "
                          f"or every mutant will look caught"
                          + ("\n" + "\n".join(tail) if tail else " (no output at all)"))


def run_mutant(mutant_set: MutantSet, mutant: Mutant, project: str, repo: Path,
               filter_text: str | None, timeout: int) -> Verdict:
    """Apply one mutant, run the suite, and ALWAYS put the file back.

    The restore is verified rather than assumed. This is the one tool in the repository that deliberately
    corrupts tracked source on purpose, so "the bytes came back" is measured -- and a mismatch is a
    refusal naming the file, because continuing would run every later mutant against a tree this tool has
    damaged, and a green summary over a damaged tree is the exact silent-wrong-signal this program exists
    to eliminate.
    """
    target = repo / mutant.file
    try:
        original = target.read_bytes()
        # Captured so a Python target can be put back with its TIMESTAMP too. Restoring the bytes is not
        # the same as restoring the file: a write stamps it now, and a file that is byte-identical but
        # freshly stamped is still a change the next `git status` may or may not show. For a `.cs` target
        # the stamp is then pushed forward again on purpose (see below), but for `.py` there is no build
        # step to fool, so the honest outcome is a file the tool never touched.
        original_mtime = target.stat().st_mtime
    except OSError as exc:
        raise Refusal("TARGET-UNREADABLE", f"{mutant.file}: {exc}") from exc

    mutated = splice(original, normalise(mutant.find), normalise(mutant.with_))
    if mutated is None:
        # STALE, not caught. A stale mutant is an UNTESTED CLAIM wearing the colours of a passing one, so
        # it gets its own outcome and its own exit path -- the original did the same, and the reason it
        # says so is in its own comment.
        return Verdict(mutant_set.name, mutant.name, mutant.file, "stale", "anchor",
                       f"anchor no longer present in {mutant.file}; the mutant needs rewriting")

    backup = target.with_name(target.name + ".mutate-bak")
    try:
        target.write_bytes(mutated)
        code, output = (run_pytest_suite(repo, timeout) if mutant.is_python
                        else run_dotnet_suite(project, filter_text, repo, timeout))
        outcome = "survived" if code == SUITE_GREEN else "caught"
        detail = "" if code == SUITE_GREEN else f"suite exited {code}"
        if outcome == "survived":
            detail = "every test passed while the code was wrong"
    finally:
        if backup.exists():
            backup.unlink()
        target.write_bytes(original)
        if target.read_bytes() != original:
            raise Refusal("RESTORE-FAILED",
                          f"{mutant.file} does not match its original bytes after the restore; the tree "
                          f"is not what the run started from and every later verdict is void")
        if mutant.is_python:
            # Put the timestamp back too, so a `.py` target ends byte- AND timestamp-identical. Python has
            # no build step to fool, which is the original's reason for not touching these.
            import os
            os.utime(target, (original_mtime, original_mtime))
        else:
            # Restoring gives the file an OLDER timestamp than the compiled output, so MSBuild keeps the
            # MUTATED assembly and the next ordinary run fails against clean source. Pushed forward, and
            # deliberately so: writing the bytes already stamps it now, but the mutated run may have
            # written the output assembly within the same clock tick, and MSBuild compares the two.
            import os
            stamp = time.time()
            os.utime(target, (stamp, stamp))
    # Returned OUTSIDE the try, so a `finally` that raises cannot swallow it -- and, the first version of
    # this function had no return at all, so every verdict was None and the report said
    # "0 caught, 0 survived" over a run that had actually mutated files. A tool that corrupts source and
    # then reports nothing is worse than one that fails loudly.
    return Verdict(mutant_set.name, mutant.name, mutant.file, outcome, "suite", detail)


def run_all(sets: list[MutantSet], resolved: dict[str, str], repo: Path, filter_text: str | None,
            timeout: int) -> list[Verdict]:
    verdicts: list[Verdict] = []
    for mutant_set in sets:
        for mutant in mutant_set.mutants:
            verdicts.append(run_mutant(mutant_set, mutant, resolved[mutant_set.name], repo,
                                       filter_text, timeout))
    return verdicts


def summarise(verdicts: list[Verdict]) -> dict:
    caught = [v for v in verdicts if v.outcome == "caught"]
    survived = [v for v in verdicts if v.outcome == "survived"]
    stale = [v for v in verdicts if v.outcome == "stale"]
    return {
        "tool": TOOL_ID,
        "verdict": "FAIL" if (survived or stale) else "OK",
        "totals": {"mutants": len(verdicts), "caught": len(caught),
                   "survived": len(survived), "stale": len(stale)},
        "survived": [asdict(v) for v in survived],
        "stale": [asdict(v) for v in stale],
        "verdicts": [asdict(v) for v in verdicts],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Break the code on purpose and report which tests failed to notice "
                    "(replaces mutate.ps1).")
    parser.add_argument("--set", default=None, help="which scripts/mutants/<set>.json to run; "
                                                    "omit for every set")
    parser.add_argument("--project", default=None,
                        help="override the suite for every set; a set's own 'project' key still wins")
    parser.add_argument("--filter", default=None,
                        help="narrow the dotnet run. Makes it quick and the verdict WEAKER: a mutant "
                             "'caught' by a filtered run is caught by THOSE tests. The pytest runner has "
                             "no equivalent, so seedsmith always runs in full")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"seconds per suite invocation (default {DEFAULT_TIMEOUT_SECONDS}; the "
                             f"original had NO timeout and this tool invokes suites N+1 times)")
    parser.add_argument("--root", type=Path, default=None, help="the repository")
    parser.add_argument("--json", action="store_true", help="emit every per-mutant verdict as JSON")
    args = parser.parse_args(argv)

    repo = (args.root or Path(__file__).resolve().parent.parent).resolve()
    try:
        sets = load_sets(repo, args.set)
        resolved = {s.name: resolve_project(repo, s.project, args.project, s.mutants) for s in sets}
        check_baselines(repo, sets, resolved, args.filter, args.timeout)
        verdicts = run_all(sets, resolved, repo, args.filter, args.timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "totals": None, "verdicts": []}, indent=2))
        else:
            print(f"MUTATE REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    report = summarise(verdicts)
    if args.json:
        print(json.dumps(report, indent=2))
        return EXIT_OK if report["verdict"] == "OK" else EXIT_FAILED

    by_set: dict[str, list[Verdict]] = {}
    for verdict in verdicts:
        by_set.setdefault(verdict.set, []).append(verdict)
    for name, rows in by_set.items():
        print(f"\n{name}  (suite: {resolved[name]})")
        for row in rows:
            print(f"  {row.outcome.upper():<10} {row.name}" + (f"  [{row.detail}]" if row.detail else ""))

    totals = report["totals"]
    for row in report["stale"]:
        print(f"{totals['stale']} never ran - their anchors no longer match the code:", file=sys.stderr)
        print(f"  {row['set']}: {row['name']}  ({row['detail']})", file=sys.stderr)
    for row in report["survived"]:
        print(f"{totals['survived']} survived - every test passed while the code was wrong:",
              file=sys.stderr)
        print(f"  {row['set']}: {row['name']}", file=sys.stderr)
    if report["verdict"] != "OK":
        return EXIT_FAILED
    print(f"\nevery mutant was caught ({totals['caught']} of {totals['mutants']})")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
