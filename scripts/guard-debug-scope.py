#!/usr/bin/env python3
r"""Guard: every /api/debug route is classified, and a scope banner may not disagree. Replaces
`guard-debug-scope.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every line went out through `Write-Host`**, invisible to a `2>&1` capture. The route table and
  the verdict stay on stdout — the table is the guard's report and six caller assertions read it
  there, and it is not a finding — while the findings move to stderr and `--json` carries the machine
  form.
* **Nine separate `throw`s.** Each one meant "the guard's own premise is stale", and each produced a
  stack trace with no distinguishing name, so a reader could not tell a stale relay helper from an
  unbalanced-paren bug. They are now named refusals.

THE GUARD IS A SELF-VERIFYING CLAIM, AND THAT IS ITS DESIGN
----------------------------------------------------------
Shape A classifies a route as Game Injector Debug *by construction*: the call went through the shared
`MapPost(g, path, cmd)` helper, and the helper relays. If a future edit strips that helper's
`Send(hub, inbox, ...)` call, Shape A's claim goes stale silently and the guard would keep asserting
something untrue. So the guard brace-matches the helper's own body and refuses if it no longer relays.
The same holds for `AcceptDebugSpawnExtra`, on which two routes' classification depends.

This is why "no matching helper definition" is a REFUSAL and not a skip: a guard that quietly stops
checking its own premise is worse than one that fails.

TECHNIQUE, AND WHY IT IS NOT A SINGLE FLAT REGEX
------------------------------------------------
Text and regex only; no dotnet build. A route's full call span is isolated by counting **paren depth**
from the call's own `(` to its matching `)`, which spans a block-bodied lambda (`=> { ... }`) and an
expression-bodied one (`=> Results.Ok(...)`, no braces at all) with ONE technique — parens and braces
nest independently in C#, so embedded `{`/`}` never affect the paren count. A flat whole-file regex
was tried here first and found wrong against the real file's shape. Brace-depth counting is used
separately, where it is the right tool: isolating an actual method BODY for the two self-verification
checks.

THE STRIPPER IS THE ONE THAT KEEPS LITERALS, AND IT HAS TO BE
--------------------------------------------------------------
Comments are blanked in place so a keyword mentioned only in prose cannot cause a false
classification, and so character indices line up 1:1 with the raw text for the banner-proximity
check. String and char literals are scanned OVER — an embedded `//` or `/*`, e.g. a URL, is never
mistaken for a comment start — but their own text is left untouched, because route paths and
command-name strings are meaningful and must survive for the regex captures.

That is `cscan.strip_comments_preserving_layout`, the same shared policy `guard-clock-seam` uses. The
literal-BLANKING variant would blank the route paths out of existence and classify nothing.

TWO CASE CONVENTIONS IN ONE SCRIPT
----------------------------------
* The route patterns and the banner pattern were matched with `[regex]::Matches` → **case-SENSITIVE**.
* The classification sub-patterns (`Send(`, `AcceptDebugSpawnExtra(`, `RpgStore`, `ua.*`, `*Service.*`)
  were tested with `-match` → **case-INSENSITIVE**.

Per call site, not per repo — the same pair that splits `guard-actor-hub` from `guard-funnel-delta`.

The fold is invisible in the source: `-match` reads like ordinary regex, and the first version of this
port compiled four of the five case-**sensitively**. Only a differential fixture with a lowercase
`send(` caught it. A rule written in the fold's favour and a rule written against it look identical at
the call site, which is why the pair is asserted in the tests rather than left to a reader.

THE BANNER CHECK IS A NO-OP UNTIL THE BANNERS LAND
----------------------------------------------------
Before the scope-banner comments exist, this check finds none and passes, which is why the guard is
green today with zero exemptions. `ManualReview` routes are never checked against a banner: the rule
deliberately does not auto-classify them either way, so no banner text on them can be "wrong".
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cscan import strip_comments_preserving_layout  # noqa: E402

GUARD_ID = "debug-scope"
VERDICT_OK = "DEBUG SCOPE GUARD OK -- {count} route(s), 0 banner mismatches"
VERDICT_FAILED = "DEBUG SCOPE GUARD FAILED -- banner/classification mismatch:"
EXIT_OK = 0
EXIT_FAILED = 1

DEFAULT_TARGET = ("src", "FusionRpg.Server", "DebugEndpoints.cs")

# CASE-SENSITIVE: `[regex]::Matches`. The `(?<!\.)` excludes a DOTTED call, so Shape A never collides
# with Shape B's `g.MapPost(`.
SHAPE_A = re.compile(
    r'(?<!\.)\bMapPost\s*\(\s*g\s*,\s*"(?P<path>[^"]*)"\s*,\s*"(?P<cmd>[^"]*)"\s*\)\s*;')
SHAPE_B = re.compile(r'g\.(?P<verb>MapPost|MapGet)\s*\(\s*"(?P<path>[^"]*)"')
MAPPOST_DEF = re.compile(
    r"\bstatic\s+void\s+MapPost\s*\(\s*RouteGroupBuilder\s+g\s*,\s*string\s+path\s*,"
    r"\s*string\s+cmdName\s*\)")
ACCEPT_DEF = re.compile(r"\bAcceptDebugSpawnExtra\s*\(\s*JsonElement\s+body")
# Matched against the RAW text, exactly as the original did — so a `//` line inside a block comment
# counts. Transcribed rather than "fixed", because the banner is a human-authored annotation and the
# original's reading of one is the contract this port is proving.
BANNER = re.compile(r"(?m)^\s*//\s*(?P<label>Game Injector Debug|RPG Server Debug)\s*$")

# CASE-INSENSITIVE: PowerShell `-match` / `-notmatch`, which fold. ALL FIVE. The first version of this
# port compiled four of them WITHOUT `re.IGNORECASE` -- because reading the line as `-match` looks like
# ordinary regex and the fold is invisible until you test a lowercase `send(`. The differential found
# it, not a reading. `PERSISTED_UA` looked right by luck: its own pattern is already lowercase.
RELAY = re.compile(r"\bSend\s*\(\s*hub\s*,\s*inbox\s*,", re.IGNORECASE)
DELEGATE_RELAY = re.compile(r"\bAcceptDebugSpawnExtra\s*\(", re.IGNORECASE)
PERSISTED = re.compile(r"\bRpgStore\b", re.IGNORECASE)
PERSISTED_UA = re.compile(r"\bua\.\w+\s*\(", re.IGNORECASE)
PERSISTED_SERVICE = re.compile(r"\b\w+Service\.\w+\s*\(", re.IGNORECASE)

BANNER_TO_CLASS = {"Game Injector Debug": "GameInjectorDebug", "RPG Server Debug": "RpgServerDebug"}
MANUAL_REVIEW = "ManualReview"

STANDARD_HINT = ("Standard: docs/contributing/live-probe-standard.md; spec: "
                 "docs/architecture/live-probe/spec-debug-scope-guard.md")


class Refusal(Exception):
    """A named precondition failure — each one means the guard's OWN premise is stale."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def find_matching_close(code: str, open_index: int, open_ch: str, close_ch: str) -> int:
    """Balanced-depth scan from an opening bracket to its matching close, or -1.

    One routine for either pair: paren-matching a route's call span, or brace-matching a method body.
    """
    if open_index < 0:
        return -1
    depth = 0
    for i in range(open_index, len(code)):
        char = code[i]
        if char == open_ch:
            depth += 1
        elif char == close_ch:
            depth -= 1
            if depth == 0:
                return i
    return -1


def line_of(text: str, index: int) -> int:
    """The 1-based line of an index, counted on the RAW text so it matches what a reader sees."""
    return text.count("\n", 0, index) + 1


def _relay_shape(body: str) -> str | None:
    """Which helper a method body matches, for the two self-verification checks.

    The original computed the close index BEFORE checking the open index, so an absent `{` produced a
    negative index and PowerShell's negative string indexing wrapped to the LAST character. The outcome
    was the same (the guard refused) but for a reason that would not have survived inspection; the
    check comes first here.
    """
    return body if (RELAY.search(body) or DELEGATE_RELAY.search(body)) else None


def _verify_helper(code: str, definition: re.Pattern[str], what: str) -> None:
    match = definition.search(code)
    if not match:
        return
    open_index = code.find("{", match.end())
    if open_index < 0:
        raise Refusal(f"{what}-BODY-UNBRACED", "no '{' after the signature")
    close_index = find_matching_close(code, open_index, "{", "}")
    if close_index < 0:
        raise Refusal(f"{what}-BODY-UNBALANCED", "no matching '}' for the helper body")
    if not RELAY.search(code[open_index:close_index + 1]):
        raise Refusal(f"{what}-NO-LONGER-RELAYS",
                      f"{what} no longer relays via Send(hub, inbox, ...), so the Game Injector Debug "
                      "classification it underpins is stale. Fix the guard or the helper.")


def classify_routes(code: str, raw: str, target: Path) -> list[dict]:
    """Every route, classified, in source order."""
    routes: list[dict] = []

    for match in SHAPE_A.finditer(code):
        routes.append({
            "method": "POST",
            "path": match.group("path"),
            "shape": "A",
            "index": match.start(),
            "classification": "GameInjectorDebug",
            "reason": "shared MapPost(g, path, cmd) helper -- Game Injector Debug by construction",
        })

    # Self-verify the premise of Shape A. Refusing is the point: a guard that quietly stops checking
    # its own premise is worse than one that fails.
    if MAPPOST_DEF.search(code):
        _verify_helper(code, MAPPOST_DEF, "MapPost(g, path, cmdName)")
    elif routes:
        raise Refusal("MAPPOST-DEFINITION-MISSING",
                      "found MapPost(g, path, cmd) call sites but no matching helper definition to "
                      "self-verify against -- fix the guard.")
    _verify_helper(code, ACCEPT_DEF, "AcceptDebugSpawnExtra")

    for match in SHAPE_B.finditer(code):
        open_paren = code.find("(", match.start())
        if open_paren < 0:
            raise Refusal("ROUTE-OPEN-PAREN-MISSING",
                          f"no '(' for {match.group('verb')} '{match.group('path')}'")
        close_paren = find_matching_close(code, open_paren, "(", ")")
        if close_paren < 0:
            raise Refusal("ROUTE-PARENS-UNBALANCED",
                          f"unbalanced parens scanning {match.group('verb')} "
                          f"'{match.group('path')}' at line {line_of(raw, match.start())}")
        span = code[open_paren + 1:close_paren]

        has_relay = bool(RELAY.search(span))
        has_delegate = bool(DELEGATE_RELAY.search(span))
        has_persisted = bool(PERSISTED.search(span) or PERSISTED_UA.search(span)
                             or PERSISTED_SERVICE.search(span))
        if has_relay or has_delegate:
            classification = "GameInjectorDebug"
            reason = ("relay call Send(hub, inbox, ...) in body" if has_relay
                      else "delegates to AcceptDebugSpawnExtra, which itself relays")
        elif has_persisted:
            classification = "RpgServerDebug"
            reason = "no relay; real persisted-domain call/param (RpgStore / ua.* / *Service.*)"
        else:
            classification = MANUAL_REVIEW
            reason = "no relay and no persisted-domain call found -- needs manual review"

        routes.append({
            "method": match.group("verb").replace("Map", "").upper(),
            "path": match.group("path"),
            "shape": "B",
            "index": match.start(),
            "classification": classification,
            "reason": reason,
        })

    routes.sort(key=lambda r: r["index"])
    return routes


def declared_banner(banners: list[re.Match[str]], index: int) -> str | None:
    """The nearest banner at or before this route, as a classification token.

    The last banner wins, so a route is judged by the annotation nearest above it rather than by the
    first one in the file.
    """
    best: re.Match[str] | None = None
    for banner in banners:
        if banner.start() <= index:
            best = banner
        else:
            break
    return BANNER_TO_CLASS[best.group("label")] if best is not None else None


def check(target: Path) -> dict:
    if not target.is_file():
        raise Refusal("TARGET-FILE-MISSING", str(target))
    try:
        raw = target.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("TARGET-FILE-UNREADABLE", f"{target}: {exc}") from exc
    if not raw.strip():
        # `Get-Content -Raw` returns $null on a zero-byte file, so the original reached
        # `[regex]::Matches($null, ...)` and died with "Value cannot be null. Parameter name: input".
        # Reading it as an empty document instead would make this guard pass VACUOUSLY on a truncated
        # DebugEndpoints.cs -- the "0 is green" shape this program treats as a defect. A vanished
        # target is not a clean target, so it is refused by name.
        raise Refusal("TARGET-FILE-EMPTY", f"{target} is empty, so there is nothing to classify. A "
                                            "truncated target must not read as a clean one.")

    code = strip_comments_preserving_layout(raw)
    if len(code) != len(raw):
        # The original threw here. It cannot happen under this policy - blanking preserves length by
        # construction - which is exactly why the check is kept and the refusal named: it is the
        # invariant the rest of the guard's index arithmetic rests on.
        raise Refusal("STRIPPED-LENGTH-DRIFT",
                      f"stripped text is {len(code)} chars against {len(raw)} raw; character indices "
                      "would no longer line up with the source")

    routes = classify_routes(code, raw, target)
    banners = list(BANNER.finditer(raw))

    failures: list[str] = []
    for route in routes:
        declared = declared_banner(banners, route["index"])
        if (declared is not None and route["classification"] != MANUAL_REVIEW
                and declared != route["classification"]):
            failures.append(
                f"{route['method']} {route['path']} "
                f"(line {line_of(raw, route['index'])}): banner says '{declared}' but computed "
                f"classification is '{route['classification']}' -- {route['reason']}")

    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if failures else "OK",
        "target": target.as_posix(),
        "routes": routes,
        "route_count": len(routes),
        "banners_found": len(banners),
        "findings": failures,
    }


def format_table(result: dict) -> list[str]:
    """The report, in the original's column layout — eight caller assertions read these exact lines."""
    return [f"  [{r['classification']:<17}] {r['method']:<5} {r['path']}  -- {r['reason']}"
            for r in result["routes"]]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: every /api/debug route classified (replaces guard-debug-scope.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--file-path", type=Path, default=None,
                        help=f"the file to classify (default: <repo>/{'/'.join(DEFAULT_TARGET)})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    target = args.file_path or root.joinpath(*DEFAULT_TARGET)

    try:
        result = check(target)
    except Refusal as refusal:
        print(f"{VERDICT_FAILED} {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "FAILED", "reason": refusal.reason,
                              "detail": refusal.detail, "target": target.as_posix(), "routes": [],
                              "route_count": 0, "banners_found": 0, "findings": []}, indent=2))
        return EXIT_FAILED

    if args.json:
        print(json.dumps(result, indent=2))
        return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED

    # stdout carries the REPORT and the verdict; stderr carries the findings. The table is not a
    # finding — it is the classification every reader and eight caller assertions come for.
    print(f"DEBUG SCOPE GUARD -- {result['route_count']} route(s) classified in {result['target']}")
    for line in format_table(result):
        print(line)
    if result["verdict"] == "OK":
        print("")
        print(VERDICT_OK.format(count=result["route_count"]))
        return EXIT_OK
    print("", file=sys.stderr)
    print(VERDICT_FAILED, file=sys.stderr)
    for finding in result["findings"]:
        print(f"  {finding}", file=sys.stderr)
    print("", file=sys.stderr)
    print(STANDARD_HINT, file=sys.stderr)
    return EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
