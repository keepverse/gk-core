#!/usr/bin/env python3
"""
Deterministic auto-repair for scripts/audit-doc-citations.py findings -- mechanical only, never a
guess. Per docs/architecture/solid-enforcement/spec-doc-citation-gate.md's own fix preference order:

    1. The code moved: point the citation at the successor file:line, verified by opening it.
    2. The code was replaced wholesale: (not automated here -- needs a human's historical-marker call)
    3. The claim itself is now false: (not automated here -- needs a human's judgement)
    4. D3: replace the bare basename with its repository path, after confirming which file the
       passage meant.

This tool automates exactly the mechanical slice of (1) and (4) that carries no judgement call:

    MOVED FILE (D3, one real candidate)   the cited path is stale (wrong directory) but its
                       basename is unambiguous -- exactly one tracked file anywhere shares it. The
                       audit reports this under D3 ("N files share this name"), but N==1 here means
                       there is nothing to disambiguate: it is a moved file, not an ambiguous one --
                       repoint the citation at that file, keeping the cited line (a separate D2 pass,
                       or a human, still confirms the line itself against the new file).
    RE-ANCHOR (D2)     the cited line is past the file's end, but the SAME citation line quotes a
                       distinctive backtick-wrapped token that appears at EXACTLY ONE line inside the
                       (correct, existing) target file -- repoint the citation at that line.
    PATH-QUALIFY (D3, N>1 candidates)   the cited basename is genuinely ambiguous, but the SAME
                       citation line names one of the candidate files' own containing directory or
                       project (e.g. "FusionRpg.Server", "gk-forge/tools/seedsmith") as a substring, and
                       EXACTLY ONE candidate matches that hint -- qualify the citation with that
                       candidate's real path.

D1 (the basename does not exist ANYWHERE in the tracked tree) has no mechanical repair available --
by definition there is no candidate file left to point at, moved or not, so every D1 finding is
residue for a human's judgement call (correct the claim, or a citations-historical marker).

Everything else is left untouched and reported as residue for a human to disposition per the spec's
own preference order (repoint after confirming, a citations-historical marker, a prose correction, or
recorded as unverifiable).

Usage (repo root):
    python gk-core/scripts/fix-doc-citations.py --scope docs/architecture --dry-run   # report only
    python gk-core/scripts/fix-doc-citations.py --scope docs/architecture --apply    # write the fixes
    python gk-core/scripts/fix-doc-citations.py --scope docs/architecture --apply --code D1

Exit codes: 0 always (this is a fixer, not a gate -- re-run the audit to check the result).
"""
import argparse
import importlib.util
import io
import os
import re
import sys
from collections import defaultdict
from pathlib import Path

# Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/scripts/lib/keepverse_roots.py):
# `scripts/` is the workspace root, so this script asks for it rather than walking `..` from its own
# directory — the same private-walk defect class `gk-core/scripts/guard-test-content-root.py` refuses in tests,
# and the `workspace_root(` token kvsplit's `rules/scan.v1.json` `resolvers` looks for.
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from keepverse_roots import workspace_root  # noqa: E402  (the path shim above must run first)

REPO_ROOT = Path(workspace_root())
AUDIT_PATH = REPO_ROOT / "scripts" / "audit-doc-citations.py"

_spec = importlib.util.spec_from_file_location("audit_doc_citations", AUDIT_PATH)
assert _spec is not None and _spec.loader is not None
adc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(adc)

BACKTICK_TOKEN = re.compile(r"`([A-Za-z_][A-Za-z0-9_.]{2,})`")


def build_by_name():
    tracked = adc.tracked_files()
    by_name = defaultdict(list)
    for path in tracked:
        by_name[os.path.basename(path)].append(path)
    return by_name, tracked


def read_lines(path):
    try:
        with io.open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read().split("\n")
    except OSError:
        return None


def fix_moved_file(finding, by_name):
    """D3 with exactly one real candidate: the cited path is stale, but its basename is
    unambiguous -- nothing to disambiguate, so this is a moved file, not an ambiguous one.

    The spec's own preference order requires the successor file:LINE, "verified by opening it" --
    a repointed path with a now-invalid line would just trade one D3 finding for a new D2 one. So
    the line is kept only when the new file is actually that long; otherwise it is dropped rather
    than guessed, leaving a correct bare-path citation a human can re-anchor with real context."""
    m = adc.CITATION.search(finding["ref"])
    if not m:
        return None
    ref, cited_line = m.group(1), m.group(2)
    base = os.path.basename(ref)
    candidates = by_name.get(base, [])
    if len(candidates) != 1:
        return None  # zero (D1, no mechanical repair) or genuinely ambiguous (path-qualify's job)
    new_path = candidates[0]
    if new_path == ref:
        return None  # already correct -- should not have been flagged at all
    suffix = ""
    if cited_line:
        target_lines = read_lines(new_path)
        if target_lines is not None and int(cited_line) <= len(target_lines):
            suffix = ":" + cited_line
    return "`%s%s`" % (new_path, suffix), new_path


def fix_reanchor(finding, doc_lines):
    """D2: the cited line is past EOF. If the SAME citation line carries a distinctive
    backtick-quoted token besides the citation itself, and that token appears at exactly one line
    inside the (correct) target file, repoint the line number there."""
    m = adc.CITATION.search(finding["ref"])
    if not m:
        return None
    ref = m.group(1)
    line_text = doc_lines[finding["line"] - 1] if 0 <= finding["line"] - 1 < len(doc_lines) else ""
    tokens = [t for t in BACKTICK_TOKEN.findall(line_text) if t != ref and "." not in t]
    if not tokens:
        return None
    target_lines = read_lines(ref)
    if target_lines is None:
        return None
    hits = None
    for tok in tokens:
        matches = [i + 1 for i, l in enumerate(target_lines) if tok in l]
        if len(matches) == 1:
            hits = matches[0]
            break
    if hits is None:
        return None
    return "`%s:%d`" % (ref, hits), ref


def fix_path_qualify(finding, by_name, doc_lines):
    """D3: the cited basename is ambiguous. If the citation's own line names one candidate's
    containing directory as a substring, and exactly one candidate matches, qualify the path."""
    m = adc.CITATION.search(finding["ref"])
    if not m:
        return None
    ref, cited_line = m.group(1), m.group(2)
    base = os.path.basename(ref)
    candidates = by_name.get(base, [])
    if len(candidates) < 2:
        return None
    line_text = doc_lines[finding["line"] - 1] if 0 <= finding["line"] - 1 < len(doc_lines) else ""
    hits = [c for c in candidates if os.path.dirname(c).replace("/", ".") in line_text
            or os.path.basename(os.path.dirname(c)) in line_text]
    if len(hits) != 1:
        return None
    # Same "verified by opening it" rule as fix_moved_file: keep the line only if it is still
    # inside the qualified file, never carry over a now-invalid one.
    suffix = ""
    if cited_line:
        target_lines = read_lines(hits[0])
        if target_lines is not None and int(cited_line) <= len(target_lines):
            suffix = ":" + cited_line
    return "`%s%s`" % (hits[0], suffix), hits[0]


def run(scope, codes, apply_changes):
    findings, _, _ = adc.audit(scope)
    by_name, _ = build_by_name()

    by_doc = defaultdict(list)
    for f in findings:
        if f["code"] in codes:
            by_doc[f["doc"]].append(f)

    fixed = []
    residue = []

    for doc, doc_findings in by_doc.items():
        text = io.open(doc, encoding="utf-8", errors="replace").read()
        lines = text.split("\n")
        changed = False
        # Apply from the bottom up within a doc so an earlier rewrite never shifts a later finding's
        # own line number out from under it (rewrites are same-line text substitutions, not line
        # insertions, but sorting defensively costs nothing).
        for f in sorted(doc_findings, key=lambda f: -f["line"]):
            result = None
            if f["code"] == "D1":
                pass  # no mechanical repair possible -- see the module docstring
            elif f["code"] == "D2":
                result = fix_reanchor(f, lines)
            elif f["code"] == "D3":
                result = fix_moved_file(f, by_name) or fix_path_qualify(f, by_name, lines)

            if result is None:
                residue.append(f)
                continue

            new_citation, new_path = result
            idx = f["line"] - 1
            if not (0 <= idx < len(lines)) or f["ref"] not in lines[idx]:
                residue.append(f)  # the line moved under us -- never guess, leave for a human
                continue
            lines[idx] = lines[idx].replace(f["ref"], new_citation, 1)
            changed = True
            fixed.append((f, new_path))

        if changed and apply_changes:
            io.open(doc, "w", encoding="utf-8", newline="\n").write("\n".join(lines))

    return fixed, residue


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--scope", default="docs/", help="path prefix to fix (default: docs/)")
    ap.add_argument("--code", action="append", dest="codes", choices=["D1", "D2", "D3"],
                     help="restrict to one code (repeatable); default: D1, D2, D3")
    ap.add_argument("--apply", action="store_true", help="write the fixes (default: dry run)")
    ap.add_argument("--dry-run", action="store_true", help="report only, the default")
    args = ap.parse_args()

    codes = set(args.codes) if args.codes else {"D1", "D2", "D3"}
    fixed, residue = run(args.scope.replace("\\", "/"), codes, args.apply)

    mode = "APPLIED" if args.apply else "DRY RUN"
    print("Doc-citation auto-fix (%s) -- scope %s, codes %s\n" % (mode, args.scope, ", ".join(sorted(codes))))
    print("  %d mechanically resolved" % len(fixed))
    for f, new_path in sorted(fixed, key=lambda t: (t[0]["doc"], t[0]["line"])):
        print("    %s:%d  %s -> %s" % (f["doc"], f["line"], f["code"], new_path))

    by_code_residue = defaultdict(int)
    for f in residue:
        by_code_residue[f["code"]] += 1
    print("\n  %d residue (needs a human -- moved-file/re-anchor/path-qualify could not resolve it mechanically)"
          % len(residue))
    for code in sorted(by_code_residue):
        print("    %s: %d" % (code, by_code_residue[code]))

    if not args.apply:
        print("\nDry run only -- re-run with --apply to write these fixes, then re-run "
              "audit-doc-citations.py to confirm.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
