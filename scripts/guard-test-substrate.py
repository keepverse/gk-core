#!/usr/bin/env python3
r"""Guard: tests must not leak temp dirs, and a failed temp-delete must never be swallowed.

Replaces `guard-test-substrate.ps1`. The comment stripper is NOT reimplemented here: this is the
second consumer of the shared scanner in `gk-core/scripts/cscan.py`, which is the reference implementation the
program asked for - three divergent PowerShell copies of this routine existed and the divergence
between them was a live defect in at least one (see below).

Bans, per `docs/contributing/testing-standard.md`:

  swallowed-delete      `Directory.Delete(` or `File.Delete(` whose immediately-enclosing catch block is
                        empty or comment-only, in `tests/**`. `File.Delete` joined `Directory.Delete`
                        under the SAME code (solid-enforcement `tuning-immutability` SE2.3) rather than
                        a new one: it is the same defect class at the same call shape that let
                        `ResidualFitLoopTests`' throwaway-domain tests commit four pollution files into
                        `gk-core/data/tuning/` when a run crashed mid-test.
  temp-store            a `tests/**` file that constructs `new RpgStore(` AND uses `Path.GetTempPath`.
  untagged-file-store   a `tests/**` file that constructs a FILE-BACKED store without
                        `[Trait("Category", "DiskSemantics")]`, so the default profile
                        (`Category!=DiskSemantics&Category!=Heavy`) opens a real file every run. A file
                        is file-backed when it builds a store with the memory plan nowhere in sight:
                        `DataTestStore.CreateFileBacked()`, or `new RpgStore(` in a file that never
                        names the memory plan (InMemory / IsMemoryUri / MemoryUri), or
                        `RpgStoreOptions.For(<one argument>)`, or `new RpgStore(` beside
                        `Path.GetTempPath`. STATED BOUND: the tag is read from comment-stripped text
                        with string literals KEPT (the attribute lives inside literals), so a file that
                        merely QUOTES the attribute in a literal would pass. That is a bound, not a
                        licence.
  temp-corpus           a `tests/**` file that copies the shipped seed corpus into a temp directory to
                        get a corpus it can add rows to: temp path + `File.Copy` + an AllDirectories
                        enumeration of a `"data","seed"` root, read with string literals KEPT. The
                        corpus is DATA - a suite needing the shipped rows plus one of its own appends
                        them in memory (`StructureCorpus.FromRows`/`WithRows`) and does not materialise
                        `gk-data/packs/fusion/data/seed/**` on the developer's SSD every run.
  temp-corpus-write     the write-shaped sibling: a `tests/**` file that writes a structure-corpus
                        document into a temp directory and loads it back (temp path +
                        `File.WriteAllText` + `StructureCorpus.Load`). Deliberately a second contract
                        rather than folded into `temp-corpus`: one copies the shipped corpus, this one
                        authors its own rows. Same escape hatch, same reason.

A file is exempt only by an explicit line in `gk-core/scripts/test-substrate-baseline.txt` (the ratchet). The
baseline only SHRINKS: a listed file with no remaining violation FAILS, and adding a line is a review
event.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **ONE PASS OVER THE TEXT, AND IT IS LOAD-BEARING.** The first version stripped block comments before
  line comments with a regex, so a `/**` inside prose - the path literal `gk-data/packs/fusion/data/seed/items/charms/**`
  in a doc comment - opened a phantom block comment whose match ran forward to the next real `*/`,
  often 150+ lines below, deleting the `Dispose` from the scanned text and making a real swallowed
  delete INVISIBLE. 15 test files contain such prose, so the gate silently passed on them; two really
  were violating. A single left-to-right scan cannot do that: it consumes a `//` to end of line and a
  `/* */` to its own closer, whichever comes first, and never lets one comment type eat the other.
  `cscan.py` is that scan, and this port calls it rather than carrying a fourth copy of it.
* **A REPORTED LINE NUMBER THAT WAS COMPUTED AND NEVER USED.** The original computed a `line` for
  every swallowed delete and then never printed it - every finding is `"<path>: <code>"`. Worse, the
  computation was unsound: it took the match index in the comment-stripped text and sliced the RAW
  text with it, and the strip replaces a whole string literal with `""`, so every index after the
  first literal is short. The port drops the field instead of porting a wrong one, and says so here
  because "it was never used" is the only reason dropping it is free.
* **Five rules meant five reads and eight strip passes** until the text was computed once per file and
  shared. The rules are unchanged; only where the text is computed is. Measured 22.8s before.

TWO THINGS THIS GUARD NEEDS FROM THE SCANNER, AND WHY THE CHOICE IS NOT FREE
----------------------------------------------------------------------------
The rules search the stripped text with regexes, so the two policies differ in whether a string
literal's CONTENTS survive. `cscan.strip_comments_and_literals_preserving_layout` is used for the
"code" view and `cscan.strip_comments_preserving_layout` for the "strings kept" view, because the
DiskSemantics trait, the `"data","seed"` root and the seed path ARE literals - a strip that erased
them would make three of the five rules unfireable. Both preserve every character OFFSET, which the
PowerShell pair did not, and no rule depends on the difference: a pattern outside a literal matches
identically whether the literal's contents became spaces or a pair of quotes.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import date
from dataclasses import dataclass, field
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import RootNotFound, forge_root, fusion_root  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cscan  # noqa: E402  (the shared scanner lives beside this tool)

GUARD_ID = "test-substrate"
EXIT_OK = 0
EXIT_FAILED = 1

BASELINE_RELPATH = "test-substrate-baseline.txt"
TESTS_RELPATH = "tests"
BUILD_DIRS = ("obj", "bin")
# The gate's own tests must contain the banned patterns as fixtures - they prove the gate fails.
# Excluding ONE named file is narrower than allowing it everywhere, and
# `TestSubstrateGuardTests` asserts the exemption cannot widen.
SELF_EXEMPT = ("tests/FusionRpg.Guard.Tests/TestSubstrateGuardTests.cs",)
DISKSEMANTICS_TRAIT = re.compile(r'Trait\s*\(\s*"Category"\s*,\s*"DiskSemantics"\s*\)')
SWALLOWED_DELETE = re.compile(r"(?:Directory|File)\.Delete\s*\(")
MEMORY_PLAN = re.compile(r"InMemory|IsMemoryUri|MemoryUri")
FILE_BACKED_HELPER = re.compile(r"DataTestStore\s*\.\s*CreateFileBacked\s*\(")
CTOR = re.compile(r"new\s+RpgStore\s*\(")
OPTIONS_FOR = re.compile(r"RpgStoreOptions\s*\.\s*For\s*\(\s*[^,()]+\s*\)")
TEMP_PATH = re.compile(r"Path\.GetTempPath")
FILE_COPY = re.compile(r"File\.Copy\s*\(")
ALL_DIRECTORIES = re.compile(r"EnumerateFiles\s*\([^)]*SearchOption\.AllDirectories")
SEED_ROOT = re.compile(r'"data"\s*,\s*"seed"')
WRITE_ALL_TEXT = re.compile(r"File\.WriteAllText\s*\(")
CORPUS_LOAD = re.compile(r"StructureCorpus\s*\.\s*Load\s*\(")

def baseline_header(today: str) -> tuple[str, ...]:
    """The ratchet's own header, BYTE for byte, including its em-dashes.

    The date is the ONE thing that changes, and deliberately: the original hardcoded `2026-09-12`, so
    every regeneration ever since has claimed to be that day. A header that lies about when the file
    was generated is worse than a header that moves.
    """
    return (
        "# Test-substrate baseline (ratchet). One line per exempt file: path : code[,code]",
        "# A listed file with no remaining violation fails the gate \u2014 remove its line when fixed.",
        "# Adding a line is a review event: state why, get owner sign-off. "
        "See docs/contributing/testing-standard.md.",
        f"# Generated {today} by scripts/guard-test-substrate.py --update-baseline.",
    )
# How far after a delete the `catch`'s opening brace must appear, in CHARACTERS. The original used 200
# and the number is reproduced here because it is a decision, not an accident: a `catch` that opens
# more than 200 characters later belongs to a different statement region.
CATCH_WINDOW = 200


class Refusal(Exception):
    """A named precondition failure."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass
class Finding:
    code: str
    line: int = 0


@dataclass
class Report:
    found: dict[str, list[str]] = field(default_factory=dict)
    files_scanned: int = 0
    baseline: dict[str, str] = field(default_factory=dict)
    baseline_path: str = ""


def read_baseline(path: Path) -> dict[str, str]:
    """`path : code[,code]` per line. Blank lines and `#` comments are skipped; a line without a
    separator is not an entry. A MISSING baseline is an empty ratchet, not a failure - that is how a
    fresh tree passes - and the envelope still says the path it looked at."""
    if not path.is_file():
        return {}
    out: dict[str, str] = {}
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise Refusal("BASELINE-UNREADABLE", f"{path}: {exc}") from exc
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        parts = re.split(r"\s*:\s*", stripped, maxsplit=1)
        if len(parts) == 2:
            out[parts[0].strip()] = parts[1].strip()
    return out


def find_swallowed_deletes(code: str) -> list[Finding]:
    """Every delete whose nearest following `catch` has an empty or comment-only body.

    The body is read from the COMMENT-STRIPPED text, so `catch { /* best effort */ }` is empty. Brace
    matching is character-by-character so a nested block inside the catch does not end it early.
    """
    findings: list[Finding] = []
    for match in SWALLOWED_DELETE.finditer(code):
        tail = code[match.start():]
        catch = re.search(r"catch\b", tail)
        if not catch:
            continue
        after = tail[catch.start():]
        open_index = after.find("{")
        if open_index < 0 or open_index > CATCH_WINDOW:
            continue
        depth, end, i = 0, -1, open_index
        while i < len(after):
            if after[i] == "{":
                depth += 1
            elif after[i] == "}":
                depth -= 1
                if depth == 0:
                    end = i
                    break
            i += 1
        if end < 0:
            continue
        # `[open + 1 : end]` - a SLICE END, not a length. The PowerShell original is
        # `Substring($open + 1, $end - $open - 1)`: a start and a LENGTH. Reading that length as an end
        # index truncates the slice to empty for every real catch body, so `catch (Exception e)
        # { throw; }` - a correct cleanup that RETHROWS, the opposite of the defect - read as an empty
        # body and the rule fired on it. The false-positive direction on a guard about leaked temp dirs
        # is the one that trains people to add baseline lines.
        body = after[open_index + 1:end]
        if re.fullmatch(r"[;\s]*", body):
            findings.append(Finding("swallowed-delete"))
    return findings


def find_temp_store(code: str) -> Finding | None:
    if CTOR.search(code) and TEMP_PATH.search(code):
        return Finding("temp-store")
    return None


def find_untagged_file_store(code: str, with_strings: str) -> Finding | None:
    """A file-backed store construction WITHOUT the DiskSemantics trait.

    The trait is read from the strings-KEPT view, because `[Trait("Category", "DiskSemantics")]` is an
    attribute whose arguments are literals; the erased view would hide it and the rule would fire on
    every correctly tagged file.
    """
    names_memory_plan = bool(MEMORY_PLAN.search(code))
    constructs_file_plan = (
        bool(FILE_BACKED_HELPER.search(code))
        or (bool(CTOR.search(code)) and not names_memory_plan)
        or (bool(OPTIONS_FOR.search(code)) and not names_memory_plan)
        or (bool(CTOR.search(code)) and bool(TEMP_PATH.search(code)))
    )
    if not constructs_file_plan:
        return None
    if DISKSEMANTICS_TRAIT.search(with_strings):
        return None
    return Finding("untagged-file-store")


def find_temp_corpus_copy(with_strings: str) -> Finding | None:
    if (TEMP_PATH.search(with_strings) and FILE_COPY.search(with_strings)
            and ALL_DIRECTORIES.search(with_strings) and SEED_ROOT.search(with_strings)):
        return Finding("temp-corpus")
    return None


def find_temp_corpus_write(with_strings: str) -> Finding | None:
    if (TEMP_PATH.search(with_strings) and WRITE_ALL_TEXT.search(with_strings)
            and CORPUS_LOAD.search(with_strings)):
        if DISKSEMANTICS_TRAIT.search(with_strings):
            return None
        return Finding("temp-corpus-write")
    return None


def scan_file(path: Path, rel: str) -> list[Finding]:
    """One read and TWO strip passes per file, shared by every rule.

    The rules used to read and strip the file themselves, so five rules meant five reads and eight
    character-by-character strip passes over every `tests/**` file. The rules are unchanged; only where
    the text is computed is.
    """
    try:
        raw = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("TESTS-FILE-UNREADABLE", f"{rel}: {exc}") from exc
    code = cscan.strip_comments_and_literals_preserving_layout(raw)
    with_strings = cscan.strip_comments_preserving_layout(raw)
    findings: list[Finding] = []
    findings += find_swallowed_deletes(code)
    for finder in (lambda: find_temp_store(code),
                   lambda: find_untagged_file_store(code, with_strings),
                   lambda: find_temp_corpus_copy(with_strings),
                   lambda: find_temp_corpus_write(with_strings)):
        found = finder()
        if found:
            findings.append(found)
    return findings


def iter_test_sources(tests_dir: Path):
    """Every `tests/**` `.cs` file, skipping build output. Sorted so a run is reproducible."""
    if not tests_dir.is_dir():
        return
    for path in sorted(tests_dir.rglob("*.cs")):
        parts = {p.casefold() for p in path.parts}
        if parts & set(BUILD_DIRS):
            continue
        yield path


def sibling_roots(root: Path) -> list[Path]:
    """The repositories a baseline entry may live in, resolved - never guessed by walking up.

    A monorepo had one root, so a ratchet line named a path relative to it and that was the whole
    addressing scheme. After the split the same line names a file in a sibling, and `root / rel` is
    a path that has never existed. Resolving the owner per entry keeps one ratchet for the seam
    instead of three partial ones.
    """
    out: list[Path] = []
    base = root.resolve()
    for accessor in (fusion_root, forge_root):
        try:
            cand = accessor(root)
        except RootNotFound:
            # ONLY a missing sibling. A blanket `except Exception` here would convert any other
            # failure - a typo in an accessor, a permissions error - into "that repository is absent",
            # which is the silent-blindness shape in a different costume: the ratchet would shrink its
            # own coverage and report the shrinkage as a clean run.
            continue
        if cand.is_dir() and cand.resolve() != base and cand.resolve() not in out:
            out.append(cand.resolve())
    return out


def scan(root: Path, baseline_path: Path) -> Report:
    tests_dir = root / TESTS_RELPATH
    if not tests_dir.is_dir():
        raise Refusal("TESTS-MISSING", f"tests/ is not a directory: {tests_dir}")
    report = Report(baseline=read_baseline(baseline_path),
                    baseline_path=baseline_path.as_posix())
    for path in iter_test_sources(tests_dir):
        report.files_scanned += 1
        try:
            rel = path.relative_to(root).as_posix()
        except ValueError as exc:
            raise Refusal("PATH-OUTSIDE-ROOT", f"{path} is not under {root}") from exc
        if rel in SELF_EXEMPT:
            continue
        codes = sorted({f.code for f in scan_file(path, rel)})
        if codes:
            report.found[rel] = codes

    # THE RATCHET SPANS THREE REPOSITORIES. Twelve of the twenty-four baseline entries name test
    # files that have been gk-fusion's or gk-forge's since the split, and all twelve still
    # violate. The guard reported every one as "no longer violated - remove the line" because it
    # never opened them, which is a ratchet reporting its own blindness as a repair instruction.
    # Taking the instruction would turn the guard green while dropping twelve live exemptions -
    # the silent weakening the ratchet exists to prevent, and the reason a stale entry is a
    # finding at all is that a stale entry is indistinguishable from a deleted one.
    #
    # Scoped deliberately. Only baseline entries that are NOT under this root are looked up in a
    # sibling, so the guard keeps exactly the coverage its committed list declares. Walking every
    # sibling test file instead would open a scope this guard never had, and any violation found
    # there belongs in that repository's own ratchet, declared by whoever owns it.
    for rel in sorted(report.baseline):
        if rel in report.found or (root / rel).is_file():
            continue
        for sibling in sibling_roots(root):
            candidate = sibling / rel
            if not candidate.is_file():
                continue
            report.files_scanned += 1
            codes = sorted({f.code for f in scan_file(candidate, rel)})
            # Only a file that STILL violates is recorded as found. Recording an empty code list
            # would insert the key, and evaluate() decides staleness by key absence - so an
            # emptied list would keep a baseline line alive that should be reported for removal,
            # which is the ratchet refusing to shrink. Found means found.
            if codes:
                report.found[rel] = codes
            break
    return report


def evaluate(report: Report) -> list[str]:
    """New violations, then STALE baseline entries. The ratchet only shrinks."""
    failures: list[str] = []
    for rel in sorted(report.found):
        codes = report.found[rel]
        if rel not in report.baseline:
            failures += [f"{rel}: {c} (new \u2014 not in baseline)" for c in codes]
            continue
        allowed = {c.strip() for c in report.baseline[rel].split(",") if c.strip()}
        # A NEW code inside an already-exempt file is still new: the exemption is per code, not per file.
        failures += [f"{rel}: {c} (new code \u2014 not in baseline line)"
                     for c in codes if c not in allowed]
    for rel in sorted(report.baseline):
        if rel not in report.found:
            failures.append(
                f"{rel}: baseline entry no longer violated \u2014 remove the line "
                "(the ratchet only shrinks)")
    return failures


def write_baseline(path: Path, report: Report, today: str) -> int:
    lines = list(baseline_header(today))
    for rel in sorted(report.found):
        lines.append(f"{rel} : {', '.join(report.found[rel])}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return len(report.found)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: tests must not leak temp dirs and a failed temp-delete must never be "
                    "swallowed (replaces guard-test-substrate.ps1).")
    parser.add_argument("--root", type=Path, default=None,
                        help="the repository to check (default: this tool's own repository)")
    parser.add_argument("--baseline-path", type=Path, default=None,
                        help="the ratchet file (default: the baseline beside this tool)")
    parser.add_argument("--update-baseline", action="store_true",
                        help="rewrite the baseline from what the tree actually does, then exit 0")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    baseline_path = args.baseline_path or (Path(__file__).resolve().parent / BASELINE_RELPATH)

    try:
        report = scan(root, baseline_path)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "problems": [], "files_scanned": 0,
                              "baseline": "", "baseline_entries": 0}, indent=2))
        else:
            print(f"TEST SUBSTRATE GUARD REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    if args.update_baseline:
        try:
            written = write_baseline(baseline_path, report, date.today().isoformat())
        except OSError as exc:
            refusal = Refusal("BASELINE-UNWRITABLE", f"{baseline_path}: {exc}")
            print(f"TEST SUBSTRATE GUARD REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
            return EXIT_FAILED
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "BASELINE-WRITTEN", "problems": [],
                              "files_scanned": report.files_scanned, "files_in_baseline": written,
                              "baseline": baseline_path.as_posix()}, indent=2))
        else:
            print(f"test-substrate baseline written: {written} file(s) -> {baseline_path}")
        return EXIT_OK

    failures = evaluate(report)
    if args.json:
        print(json.dumps({"guard": GUARD_ID, "verdict": "FAIL" if failures else "OK",
                          "problems": failures, "files_scanned": report.files_scanned,
                          "baseline": report.baseline_path,
                          "baseline_entries": len(report.baseline)}, indent=2))
        return EXIT_FAILED if failures else EXIT_OK

    if failures:
        # Findings to stderr; the OK line is the only thing on stdout, so a caller reading stdout alone
        # cannot mistake a finding for a verdict.
        print("TEST SUBSTRATE GUARD FAILED \u2014 test temp-dir / swallowed-delete / untagged "
              "file-store / corpus copy or write violations:", file=sys.stderr)
        for failure in failures:
            print(f"  {failure}", file=sys.stderr)
        print("", file=sys.stderr)
        print("Standard: docs/contributing/testing-standard.md", file=sys.stderr)
        return EXIT_FAILED
    print("TEST SUBSTRATE GUARD OK \u2014 no new swallowed deletes, temp-backed or untagged "
          "file-backed stores, or shipped-corpus copies / corpus writes into temp, in tests/")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
