#!/usr/bin/env python3
r"""Guard: a scenario may not assert on state it created through anything but a real route.

Replaces `guard-sim-fabrication.ps1`. The comment stripper is NOT reimplemented: this guard carries a
private copy of a policy `gk-core/scripts/cscan.py` already names, and the port imports the shared one instead.
Which policy is the point, and the answer is measured rather than read off the original's name -- see
WHY THE POWERSHELL FORM WAS RETIRED below, because the original's function name and its comment both
name the wrong one.

WHY THE GUARD EXISTS
--------------------
Owner ruling D4 (b) (`tasks/rpg-simulator-decisions.md`, CLEARED 2026-09-22): the honesty review is an
AUTOMATED GUARD, not a checklist item. Row RS4 (`tasks/rpg-simulator-todo.md`) owns the rule; the
design is preserved there and in `tasks/reports/rpg-sim-rs25.md`.

The standard it enforces is `docs/contributing/live-probe-standard.md` §3 -- a verdict reads through the
same query path the web FE uses. `tools/RpgSim/ScenarioVocabulary.cs.IsFeFacingRead` is that rule's
machine, and HALF A below is a MIRROR of it. The two halves are one rule each:

  HALF A -- the scenario corpus (`gk-core/tests/fixtures/rpg-scenarios/**.json`)
    1. Every `read.*` step names an FE-facing read: a GET under `/api/` outside `/api/test` and
       `/api/sim`, or a `/hub/*` message. `/api/test/snapshot` is refused BY NAME -- it is the
       standard's named anti-pattern, and the one route a fabricated scenario would reach for.
    2. Every `sim.*` / `test.*` / `api.*` call op is in the CLOSED table, and its declared route is
       that op's own route. The table is read from `gk-core/tools/RpgSim/ScenarioVocabulary.cs` -- ONE copy,
       never a second list here to drift from it.
    3. Every `expect.*` / `digest` step names a `read.*` step declared BEFORE it. An assertion over a
       reading that never ran is the shape a fabrication takes.
    4. Every `test.*` step is on the fixture allowlist below (with a written reason) AND named in the
       scenario's own `notes`. A `test.*` step is a sanctioned SIM fixture route; a scenario that uses
       one without saying so in its own notes is hiding the fixture surface it leans on.

  HALF B -- the shim surface (`gk-core/src/FusionRpg.Server/**.cs`)
    5. No `/api/sim/*` handler may take `RpgStore`. Measured 2026-09-23: 55 handlers, 0 take it. The
       sim surface is the INPUT feed (PvZ -> server); a handler that reached the store directly would let
       a scenario write state through a route no player can reach.
    6. Every `/api/test/*` handler that DOES take `RpgStore` is on the allowlist below with a written
       reason. A STALE entry -- an allowlisted route that no longer takes the store, or no longer exists
       -- is itself a violation, because a stale allowlist is how the next real handler slips in unread.
       The rule is checked in BOTH directions and the port keeps both.

WHY THE ALLOWLISTS LIVE HERE AND NOT IN A TABLE
-----------------------------------------------
Each entry carries the reason its route cannot be a player-facing one, and a reason nobody can re-derive
is not a review record -- it is a list. `test.expedition.due` is the instructive one: the op is RETIRED
(its store bypass is gone) yet the entry STAYS, because the bite-proof C# test plants a scenario using
it to exercise the NOTES rule, and the guard only reaches that rule when the op is allowlisted. The
vocabulary no longer knows the op, so the same planted scenario is also refused as not-in-the-closed-table
-- which is the point of a planted violation. A tidy-up that removed the entry would silently stop the
notes rule from being reachable.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **THE PRIVATE STRIPPER'S NAME AND COMMENT BOTH OVERSTATE IT, AND THE PORT USES WHAT THE CODE DOES.**
  The original's helper is called `Strip-CommentsPreservingLayout` and its comment says it blanks
  "string/char literal INTERIORS in place". It does not: the literal branch only ADVANCES the index
  past the closing quote and writes nothing, so the text keeps its literals intact. **Measured, not
  inferred** — on `ScenarioVocabulary.cs` the blanking policy parses 0 `ScenarioOp` rows (it deletes the
  quotes the table's own regex needs) and the comment-only policy parses 5, which is the table's real
  size. So the port uses `cscan.strip_comments_preserving_layout`. Carrying the name forward would have
  meant importing a scanner one policy stronger than the guard ever was, which is exactly the "quietly
  gets stronger and turns a passing guard red for reasons nobody can point at" failure
  `cscan.py` warns about in its own header.
* **A CONSEQUENCE OF KEEPING LITERALS, STATED BECAUSE IT IS A FALSE-POSITIVE SHAPE.** Rule 5 and rule 6
  both decide by searching the handler's span for `\bRpgStore\b` on the literal-PRESERVING view, so a
  route whose handler mentions `RpgStore` inside a string literal — a log message, a comment-shaped
  string, an error text — is reported as taking the store. Tightening it to the blanking view would fix
  the false positive and would also stop matching the real parameter, because the parameter's TYPE
  annotation is itself a literal: `static void Seed(RpgStore store)`. The two views disagree about
  which occurrences are the parameter, and neither is a clean answer. The original's choice is kept,
  and the case is pinned in the contract test so a future tightening is a decision rather than an
  accident.
* **The same `$Root.Length` slice as the three guards before it.** The original builds each file's
  repository-relative path as `$file.FullName.Substring($Root.Length)`, guarded only by a
  `StartsWith($Root, OrdinalIgnoreCase)` test. When the caller's `--root` and the enumerator disagree
  about the path's spelling, the slice removes the wrong number of characters and every reported path
  is chopped. The port uses `Path.relative_to`, which cannot chop; the differential declares the
  difference rather than reproducing it.
* **The count line is a READING** (validation-ssot.md) and is printed on the SUCCESS path, because a
  guard that silently found zero scenarios looks exactly like an honest corpus. Nothing asserts it.
* **One dead parameter survived in the original**: `Add-Handlers` took a `-BodyStart` that it never read.
  Dropped rather than carried, with the note kept so nobody re-adds it.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cscan  # noqa: E402  (the shared scanner lives beside this tool)

GUARD_ID = "sim-fabrication"
EXIT_OK = 0
EXIT_FAILED = 1

# The clock declaration modes. CLOSED vocabulary, and the original compares with `-notcontains`, which
# FOLDS -- so `Ambient` is accepted. Folding is preserved deliberately: a mode is a machine token, and
# refusing `Ambient` would redden a corpus on a spelling rather than on a rule.
CLOCK_MODES = ("ambient", "offset", "explicit")

# Route group prefixes, checked in this order. `/api/sim/` before `/api/test/` before `/api/`, and
# `/hub/` last, because the prefixes overlap and a route can only be on one surface.
SIM_PREFIX = "/api/sim/"
TEST_PREFIX = "/api/test/"
API_PREFIX = "/api/"
HUB_PREFIX = "/hub/"

# HALF A rule 1: the live-probe standard's named anti-pattern, refused BY NAME and not by surface, so
# the message can name the standard rather than describe a shape.
SNAPSHOT_ROUTE = "/api/test/snapshot"

# ── the fixture allowlist (half A rule 4) ─────────────────────────────────────────────────────────────
# Keyed by the call op name. Adding an entry is a REVIEWED change: a new `test.*` op lands in
# gk-core/tools/RpgSim/ScenarioVocabulary.cs AND here in the same commit. The reasons are transcribed verbatim:
# they are the review record, and a paraphrase would be a second thing to keep true.
TEST_OP_ALLOWLIST: dict[str, str] = {
    "test.seed.souls": (
        "POST /api/test/seed-souls-demo -- there is NO player-facing soul award route "
        "(src/FusionRpg.Server/SoulEndpoints.cs:11 exposes only the two GETs), so the only real "
        "write path a scenario can reach is this sanctioned fixture route. Filed as RS-F1."),
    # `test.expedition.due` is RETIRED as an OP (RS3 increment 5b, RS-F16): its store bypass is gone
    # and a scenario makes an expedition due with a `clock.set` step instead. The entry STAYS, and the
    # reason is not that the surface is still sanctioned: the RS4 bite-proof test
    # (`gk-core/tests/FusionRpg.Guard.Tests/SimFabricationGuardTests.cs:76`, a pipeline-protected path this lane
    # may not edit) plants a scenario that uses this op to exercise the NOTES rule, and the guard only
    # reaches that rule when the op is allowlisted. The vocabulary no longer knows the op, so the same
    # planted scenario is refused as not-in-the-closed-table as well - which is the point of a planted
    # violation.
    "test.expedition.due": (
        "RETIRED with its route (RS3 increment 5b) - kept in this allowlist only so the RS4 "
        "planted-violation test still reaches the notes rule; the op is no longer in the closed call "
        "vocabulary, so a real corpus that names it fails validation by name."),
}

# ── the /api/test direct-store allowlist (half B rule 6) ─────────────────────────────────────────────
# Keyed by the full route path under the /api/test group. Each reason says why the handler is a
# sanctioned fixture WRITER and not a player-facing route. Same discipline: verbatim, because each line
# is the answer to "why may a player not do this?".
TEST_STORE_ALLOWLIST: dict[str, str] = {
    "/api/test/reset": (
        "the SIM fixture reset: wipes the fixture world through SimService.FullResetAsync, then "
        "refreshes the patron cache. A player-facing world wipe is deliberately not specified."),
    "/api/test/snapshot": (
        "the fixture/debug snapshot read. NOT a scenario read-back -- it is refused by name in half A "
        "rule 1; it exists for tests and manual probes."),
    "/api/test/seed-pvz-stats-demo": (
        "seeds a demo PvZ stats sheet so the web FE has a row to render. A fixture writer, not a "
        "player route: a real sheet is written by the injector through the event pipeline."),
    "/api/test/seed-pvz-activity-demo": (
        "seeds a demo PvZ activity rollup, same class as seed-pvz-stats-demo."),
    "/api/test/seed-rpg-progression-demo": (
        "seeds a demo RPG progression summary, same class as seed-pvz-stats-demo."),
    "/api/test/seed-souls-demo": (
        "the sanctioned soul seeding surface (see test.seed.souls above); it performs the real "
        "RpgStore.AwardSouls write with reason seed, because no player-facing award route exists."),
    "/api/test/web-match": (
        "runs one real web match outside the expedition loop. It is a fixture trigger for the "
        "battle pipeline, not a player route (expeditions owns the player battle surface)."),
    # `/api/test/expedition-due` was HERE and is gone with its handler (RS3 increment 5b, RS-F16): the
    # route no longer exists, so an entry for it would be a stale allowlist entry -- itself a violation.
    "/api/test/seed-materials": (
        "grants creature materials so a fixture can exercise crafting without a drop loop."),
    "/api/test/mint-creature": (
        "mints a deterministic species specimen so a fixture can exercise the roster without a roll."),
    "/api/test/contracts/settle": (
        "settles contracts as if days had passed, so tribute and decay are testable without waiting "
        "for real midnights."),
    "/api/test/world/create": (
        "creates a world from a template for fixtures; a player-facing world creation route is not "
        "specified in this program."),
}

# ── the closed op table, read from the ONE source of truth ───────────────────────────────────────────
# `new ScenarioOp("<name>", "<surface>", "<METHOD>", "<route template>")`. Four double-quoted
# arguments, in that order; the route template may itself contain no quotes, which is why the pattern
# uses `"[^"]*"` and not a non-greedy anything.
SCENARIO_OP = re.compile(
    r'new\s+ScenarioOp\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)')
# A route group variable: `var <name> = <...>.MapGroup("<path>");`
MAP_GROUP = re.compile(r'var\s+(\w+)\s*=\s*[\w.]+\.MapGroup\(\s*"([^"]+)"\s*\)')
# An extension mapper: `public static void <Name>(this RouteGroupBuilder <param>) {`
MAPPER_DEF = re.compile(
    r'public\s+static\s+void\s+(\w+)\s*\(\s*this\s+RouteGroupBuilder\s+(\w+)\s*\)\s*\{')
# A route registration: `<groupVar>.Map(Post|Get|Delete|Put|Patch)("<path>",`
ROUTE_REGISTRATION = re.compile(
    r'(?<![\w.])(\w+)\.Map(Post|Get|Delete|Put|Patch)\(\s*"([^"]+)"\s*,')
# A zero-argument call, which is how a mapper is invoked: `<groupVar>.<MapperName>()`
MAPPER_CALL = re.compile(r'(?<![\w.])(\w+)\.(\w+)\(\s*\)')
# Does the handler's own span take the store? A WORD boundary, so `RpgStoreFactory` is not a match.
TAKES_STORE = re.compile(r"\bRpgStore\b")

SURFACES = frozenset({"api", "hub"})
# The step op PREFIXES that dispatch, and the closed set the guard advertises when an op is unknown.
CALL_PREFIXES = ("sim", "test", "api")
KNOWN_SURFACE_HELP = "sim.* | test.* | api.* | read.* | expect.* | clock.set | digest"
# `/api/test/snapshot` is matched anywhere in the declared route, not only as a prefix.
SNAPSHOT_IN_ROUTE = re.compile(r"/api/test/snapshot")


class Refusal(Exception):
    """A named precondition failure. A missing vocabulary file is a refusal, not an empty table."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


@dataclass(frozen=True)
class OpDef:
    """One row of the closed call vocabulary, read from `ScenarioVocabulary.cs`."""

    name: str
    surface: str
    method: str
    route: str

    @property
    def declared_route(self) -> str:
        """What a scenario's `route` field must equal for this op: the method and the op's own route."""
        return f"{self.method} {self.route}"


@dataclass
class Handler:
    """One route registered under `/api/sim/` or `/api/test/`, with whether its span takes the store."""

    route: str
    method: str
    takes_store: bool
    file: str


@dataclass
class Result:
    """The findings, the readings, and the counts the verdict line is built from."""

    violations: list[str] = field(default_factory=list)
    scenarios: int = 0
    steps: int = 0
    reads: int = 0
    test_steps: int = 0
    golden_skipped: int = 0
    sim_handlers: list[Handler] = field(default_factory=list)
    test_handlers: list[Handler] = field(default_factory=list)
    test_store_routes: list[str] = field(default_factory=list)


def long_path(path: Path) -> Path:
    """`path` with any Windows 8.3 short component expanded to its long form.

    This is not a nicety. The enumerator and the caller can disagree about a tree's spelling: a
    `--root` under `C:\\Users\\NENEESC~1\\...` resolved to the long `C:\\Users\\NeneScarlet\\...` while
    `rglob` hands back the short one, and then `Path.relative_to` raises on a file that IS under the
    root. The three guards ported before this one each built the relative path with
    `$file.FullName.Substring($Root.Length)`, which does not raise -- it silently slices the wrong
    number of characters and reports a chopped path. `relative_to` turns that silent corruption into a
    loud refusal, which is strictly better and still wrong, because a guard that refuses an ordinary
    temp tree cannot be tested against one.

    So the two spellings are reconciled HERE, once, and everything downstream compares like with like.
    `Path.resolve()` does not do this: it normalises separators and `.`/`..` but leaves a short
    component alone, which is the whole reason the two disagree.

    MEASURED REDUNDANCY, kept deliberately. `relative_to_root` has a SECOND, independent
    reconciliation: when the strings still disagree it asks the filesystem with `os.path.samefile`,
    which does not care about 8.3 either. Disabling this function changes no answer on the 8.3 case --
    measured, and the mutation is classified accordingly by the falsification harness. Both are kept
    because they fail differently: `GetLongPathNameW` needs the path to EXIST and does nothing for a
    file still being created, while `samefile` needs an ancestor that exists and answers the question
    the filesystem can answer. One mechanism would be one failure mode instead of two.
    """
    if os.name != "nt":
        return path
    try:
        import ctypes
        from ctypes import wintypes
        get_long = ctypes.windll.kernel32.GetLongPathNameW
        get_long.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD]
        get_long.restype = wintypes.DWORD
        buffer_size = 32768
        while True:
            buffer = ctypes.create_unicode_buffer(buffer_size)
            written = get_long(str(path), buffer, buffer_size)
            if written == 0:
                # The name is already long, the volume does not do short names, or the path does not
                # exist yet. All three are cases where `str(path)` is the best available answer, and all
                # three are handled by returning it unchanged rather than by raising: a missing file is
                # this function's CALLER's problem to name, not an import-time crash.
                return path
            if written < buffer_size:
                return Path(buffer.value)
            buffer_size = written + 1
    except (AttributeError, OSError, ImportError):
        return path


def relative_to_root(path: Path, root: Path, fallback: Path | None = None) -> str:
    """`path` relative to `root`, with both spellings reconciled first.

    Falls back to the same-file question the FILESYSTEM answers when the strings disagree, so a root
    spelled short and a file spelled long is a relationship rather than a `ValueError`.

    `fallback` is a SECOND base to try before giving up, and it exists because `--scenario-dir` may
    legitimately point outside `--root`: the C# bite-proof test plants a violation in a temp directory
    and passes the REAL repository as the root, so the planted file is genuinely not under it. Refusing
    that would make the guard unusable for exactly the planted-violation work it exists to support, and
    the original permitted it - its `StartsWith` test simply failed and it reported an absolute path.
    Only when neither base contains the file is `PATH-OUTSIDE-ROOT` the honest answer.
    """
    for base in (root, fallback):
        if base is None:
            continue
        resolved_root = long_path(base)
        candidate = long_path(path)
        try:
            return candidate.relative_to(resolved_root).as_posix()
        except ValueError:
            pass
        # The strings disagree. Ask the filesystem, which does not care about 8.3: walk up from the file
        # until one ancestor IS the base, and slice there. Bounded by the path's own depth, so it cannot
        # run away, and it answers exactly the question `relative_to` was asked.
        for depth in range(1, len(candidate.parts) + 1):
            ancestor = candidate.parent
            for _ in range(depth - 1):
                if ancestor.parent == ancestor:
                    break
                ancestor = ancestor.parent
            try:
                if os.path.samefile(ancestor, resolved_root):
                    tail = candidate.parts[len(ancestor.parts):]
                    return Path(*tail).as_posix() if tail else candidate.name
            except OSError:
                continue
    raise Refusal("PATH-OUTSIDE-ROOT", f"{path} is under neither {root} nor {fallback}")


def find_matching_close(code: str, open_index: int, open_ch: str, close_ch: str) -> int:
    """The index of the closer that balances the opener at `open_index`, or -1.

    -1 rather than an exception, because the callers turn it into a `guard defect:` violation naming the
    route: a malformed body is a finding about the tree, not a crash of the guard about itself.
    """
    depth = 0
    i, n = open_index, len(code)
    while i < n:
        if code[i] == open_ch:
            depth += 1
        elif code[i] == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def read_call_table(vocabulary: Path) -> dict[str, OpDef]:
    """The closed op table, read once from the one source of truth.

    Read on the BLANKING view: a `new ScenarioOp(...)` line inside a doc comment is not a row, and a
    route template that happens to contain the word `ScenarioOp` is not a row either.
    """
    try:
        raw = vocabulary.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise Refusal("VOCABULARY-UNREADABLE", f"{vocabulary}: {exc}") from exc
    code = cscan.strip_comments_preserving_layout(raw)
    table: dict[str, OpDef] = {}
    for match in SCENARIO_OP.finditer(code):
        name, surface, method, route = match.groups()
        table[name] = OpDef(name, surface, method, route)
    if not table:
        # An empty table would make EVERY `sim.*`/`test.*`/`api.*` op a violation, so a scanner that
        # silently found nothing reads as a wall of red rather than as a broken precondition. Refuse.
        raise Refusal("VOCABULARY-EMPTY", f"no ScenarioOp rows parsed from {vocabulary}")
    return table


def route_surface(route: str) -> str | None:
    """Which surface a declared route belongs to, or None when it belongs to none.

    A route is a method + path pair, or a bare hub message (`/hub/rpg`), which has no method. The
    original measures the first SPACE and takes everything after it as the path, which is why a leading
    space yields a path of `""` and a surface of None rather than a crash.
    """
    if not route.strip():
        return None
    trimmed = route.strip()
    space = trimmed.find(" ")
    path = trimmed if space <= 0 else trimmed[space + 1:].strip()
    if path.startswith(SIM_PREFIX):
        return "sim"
    if path.startswith(TEST_PREFIX):
        return "test"
    if path.startswith(API_PREFIX):
        return "api"
    if path.startswith(HUB_PREFIX):
        return "hub"
    return None


def route_method(route: str) -> str | None:
    """The HTTP method of a declared route, upper-cased, or None when the route names no method."""
    if not route.strip():
        return None
    trimmed = route.strip()
    space = trimmed.find(" ")
    if space <= 0:
        return None
    return trimmed[:space].strip().upper()


def is_fe_facing_read(route: str) -> bool:
    """The mirror of `ScenarioVocabulary.IsFeFacingRead`.

    The surface must be api|hub, and it must be a GET or a hub message. `/api/test` and `/api/sim` are
    never FE-facing reads. A hub message has no method, so it is accepted on the surface alone; a
    non-GET under `/api/` is not.
    """
    surface = route_surface(route)
    if surface not in SURFACES:
        return False
    if route_method(route) == "GET":
        return True
    return surface == "hub"


def as_list(value) -> list:
    """A JSON value as a list, so a single object behaves like a one-element array.

    The original wraps every access in `@(...)`, which normalises a scalar to a one-element array. This
    is the same normalisation, and it is why `notes: "one string"` is a valid scenario.
    """
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def as_text(value) -> str:
    """A JSON scalar as text. `None` becomes `""`, matching `[string]$null`."""
    return "" if value is None else str(value)


def scan_scenario(path: Path, rel: str, result: Result, call_table: dict[str, OpDef],
                  vocab_names: list[str]) -> None:
    """Every violation in one scenario file, and the readings it contributes.

    Steps are walked IN ORDER and the `readings` table is built as it goes, which is what makes rule 3
    ("an assertion names a reading declared BEFORE it") decidable at all: a scenario that declares
    `expect` before its `read` is a violation, and reordering the array changes the answer.
    """
    try:
        doc = json.loads(path.read_text(encoding="utf-8", errors="replace"))
    except (json.JSONDecodeError, ValueError) as exc:
        result.violations.append(f"{rel}: not valid JSON: {exc}")
        return
    result.scenarios += 1
    scenario_id = as_text(doc.get("id")).strip() or rel
    where_doc = f"{rel} ({scenario_id})"

    clock = doc.get("clock")
    if clock is None:
        result.violations.append(
            f"{where_doc}: clock is required -- the verdict must say what time the run believed it was")
    else:
        mode = as_text(clock.get("mode"))
        # `-notcontains` folds, so the comparison folds too.
        if mode.casefold() not in {m.casefold() for m in CLOCK_MODES}:
            result.violations.append(
                f"{where_doc}: clock.mode '{mode}' is not one of {'|'.join(CLOCK_MODES)}")
        if not as_text(clock.get("note")).strip():
            result.violations.append(
                f"{where_doc}: clock.note is required -- an undeclared clock makes every timestamp "
                "reading meaningless")

    notes_text = "\n".join(as_text(n) for n in as_list(doc.get("notes")))
    readings: set[str] = set()
    captures: set[str] = set()

    for index, step in enumerate(as_list(doc.get("steps"))):
        result.steps += 1
        if not isinstance(step, dict):
            continue
        op = as_text(step.get("op"))
        where = f"{where_doc} steps[{index}]"
        if not op.strip():
            result.violations.append(f"{where}: op is required")
            continue
        for capture in as_list(step.get("capture")):
            if isinstance(capture, dict):
                name = as_text(capture.get("name"))
                if name:
                    captures.add(name)
        dot = op.find(".")
        prefix = op if dot < 0 else op[:dot]

        if prefix in CALL_PREFIXES:
            _check_call_step(step, op, prefix, where, call_table, vocab_names, notes_text, result)
        elif prefix == "read":
            _check_read_step(step, op, where, readings, result)
        elif prefix == "expect":
            _check_expect_step(step, where, readings, captures, result)
        elif prefix == "digest":
            _check_digest_step(step, where, readings, result)
        elif prefix == "clock":
            _check_clock_step(step, where, result)
        else:
            result.violations.append(
                f"{where}: '{op}' has no surface (expected {KNOWN_SURFACE_HELP})")

    if not readings:
        result.violations.append(
            f"{where_doc}: no read.* step -- a verdict with nothing read back is not a verdict")


def _check_call_step(step: dict, op: str, prefix: str, where: str, call_table: dict[str, OpDef],
                     vocab_names: list[str], notes_text: str, result: Result) -> None:
    """Rules 2 and 4: the closed vocabulary, the op's own route, the fixture allowlist and the notes."""
    route = as_text(step.get("route"))
    op_def = call_table.get(op)
    if op_def is None:
        result.violations.append(
            f"{where}: '{op}' is not in the closed call vocabulary ({', '.join(vocab_names)})")
    else:
        expected = op_def.declared_route
        if route != expected:
            result.violations.append(
                f"{where}: declared route '{route}' is not the route '{op}' calls ('{expected}')")
        elif route_surface(route) != op_def.surface:
            result.violations.append(
                f"{where}: declared route's surface is not the op's surface '{op_def.surface}'")
    if prefix != "test":
        return
    result.test_steps += 1
    if op not in TEST_OP_ALLOWLIST:
        result.violations.append(
            f"{where}: '{op}' is not on the fixture allowlist in scripts/guard-sim-fabrication.py "
            "-- a test.* step is a sanctioned SIM fixture route and a new one is a reviewed change")
        return
    # Named in the scenario's own notes: either the op name or the route it calls. `in` on a str is
    # ordinal, which is what .NET's String.Contains gives here.
    named = op in notes_text
    if not named and route.strip():
        named = route.strip() in notes_text
    if not named:
        result.violations.append(
            f"{where}: '{op}' is not named in the scenario's own notes -- a scenario that leans on a "
            "fixture surface must say so where a reader looks")


def _check_read_step(step: dict, op: str, where: str, readings: set[str], result: Result) -> None:
    """Rule 1: a read names an FE-facing route, and never the snapshot anti-pattern."""
    result.reads += 1
    name = op[len("read."):] if len(op) > len("read.") else ""
    if not name.strip():
        result.violations.append(f"{where}: read.<name> requires a name")
    elif op in readings:
        result.violations.append(f"{where}: reading '{op}' is declared twice")
    else:
        readings.add(op)

    route = as_text(step.get("route"))
    if not route.strip():
        result.violations.append(
            f"{where}: route is required -- a read-back names the route or hub message it came from")
    elif SNAPSHOT_IN_ROUTE.search(route):
        result.violations.append(
            f"{where}: '{SNAPSHOT_ROUTE}' is the live-probe standard's named anti-pattern -- a verdict "
            "reads through the same query path the web FE uses "
            "(docs/contributing/live-probe-standard.md section 3)")
    elif not is_fe_facing_read(route):
        result.violations.append(
            f"{where}: '{route}' is not an FE-facing read -- a verdict reads a GET under /api/ outside "
            "/api/test and /api/sim, or a /hub/* message")


def _check_expect_step(step: dict, where: str, readings: set[str], captures: set[str],
                       result: Result) -> None:
    """Rule 3: an assertion names a reading declared BEFORE it, and a capture that was captured."""
    reading = as_text(step.get("reading"))
    if not reading.strip():
        result.violations.append(f"{where}: reading is required")
    elif reading not in readings:
        result.violations.append(
            f"{where}: reading '{reading}' was never declared before this step -- an assertion over a "
            "reading that never ran is the shape a fabrication takes")
    other = as_text(step.get("other"))
    if not other.strip():
        return
    if other.startswith("{") and other.endswith("}"):
        if other[1:-1] not in captures:
            result.violations.append(f"{where}: other '{other}' names a value that was never captured")
        return
    pointer = other.find("#")
    if pointer <= 0:
        result.violations.append(
            f"{where}: other '{other}' must be <reading>#</$pointer> or {{capturedName}}")
    elif other[:pointer] not in readings:
        result.violations.append(
            f"{where}: other '{other}' names reading '{other[:pointer]}', which was never declared "
            "before this step")


def _check_digest_step(step: dict, where: str, readings: set[str], result: Result) -> None:
    """Rule 3 for digests, plus the exclusion list: a digest with nothing in it proves nothing."""
    include = as_list(step.get("include"))
    if not include:
        result.violations.append(
            f"{where}: include is required -- a digest with nothing in it proves nothing")
    for reference in include:
        ref = as_text(reference)
        pointer = ref.find("#")
        if pointer <= 0:
            result.violations.append(f"{where}: include '{ref}' must be <reading>#</$pointer>")
        elif ref[:pointer] not in readings:
            result.violations.append(
                f"{where}: include '{ref}' names reading '{ref[:pointer]}', which was never declared "
                "before this step")
    for exclusion in as_list(step.get("exclude")):
        if not isinstance(exclusion, dict):
            continue
        field_name = as_text(exclusion.get("field"))
        if not field_name.strip():
            result.violations.append(f"{where}: exclude has an empty field")
        if not as_text(exclusion.get("reason")).strip():
            result.violations.append(
                f"{where}: exclude '{field_name}' has no reason -- the exclusion list is load-bearing")


def _check_clock_step(step: dict, where: str, result: Result) -> None:
    """`clock.set` is a DECLARATION: it must say how much, and it must name no route.

    A route that sets the clock is deliberately not specified (spec-clock-seam.md §2), so a scenario that
    reaches for one is asking for a surface the program refused to build.
    """
    if step.get("offsetSeconds") is None:
        result.violations.append(
            f"{where}: clock.set needs offsetSeconds -- the absolute offset from the machine clock the "
            "host must believe")
    if as_text(step.get("route")).strip():
        result.violations.append(
            f"{where}: clock.set names no route -- a route that sets the clock is deliberately not "
            "specified (spec-clock-seam.md section 2)")


def scenario_files(scenario_dir: Path) -> tuple[list[Path], int]:
    """The scenario corpus, and how many `golden/` verdicts were set aside.

    `golden/` holds VERDICTS, not scenarios (readback-verdict.md §5, owner ruling C2 (a)): the stored
    artifact a scenario PRODUCED, kept beside the corpus it came from. It has no `steps` array, no
    `clock` block and no `digest` step of its own, so scanning it as a scenario reports a wall of
    violations that say nothing about honesty. COUNTED, not silently dropped.
    """
    if not scenario_dir.is_dir():
        return [], 0
    everything = sorted((p for p in scenario_dir.rglob("*.json") if p.is_file()),
                        key=lambda p: str(p))
    golden, scenarios = [], []
    for path in everything:
        (golden if "golden" in {part.casefold() for part in path.parts} else scenarios).append(path)
    return scenarios, len(golden)


def server_files(server_dir: Path) -> list[Path]:
    """Every server `.cs` file outside build output. `bin/` and `obj/` are not source."""
    if not server_dir.is_dir():
        return []
    skip = {"bin", "obj"}
    return sorted((p for p in server_dir.rglob("*.cs")
                   if p.is_file() and not ({part.casefold() for part in p.parts} & skip)),
                  key=lambda p: str(p))


@dataclass(frozen=True)
class Mapper:
    """An extension method that registers routes onto a caller's group: name, its parameter, its file
    and its body. The body's group parameter carries the CALLER's path, which is how a mapper's routes
    reach `/api/test/**` without naming the group itself."""

    name: str
    param: str
    file: str
    body: str


def collect_handlers(code: str, groups: dict[str, str], file_label: str, out: list[Handler],
                     sim: list[Handler], test: list[Handler], result: Result) -> None:
    """Every route under `/api/sim/` and `/api/test/` in one span of code, with its store flag.

    A route's full path is its group's path plus the registration's path, so a route is only knowable
    once the group's variable is resolved in THIS span. A mapper body is scanned with a one-entry group
    table keyed by the mapper's own parameter, which is how the same call sites are reached twice.
    """
    for match in ROUTE_REGISTRATION.finditer(code):
        var_name, method, path = match.groups()
        if var_name not in groups:
            continue
        route = groups[var_name] + path
        if not (route.startswith(SIM_PREFIX) or route.startswith(TEST_PREFIX)):
            continue
        open_index = match.end() - 1
        close = find_matching_close(code, open_index, "(", ")")
        if close < 0:
            result.violations.append(
                f"guard defect: unbalanced handler span at {route} in {file_label}")
            continue
        span = code[open_index:close + 1]
        handler = Handler(route, method, bool(TAKES_STORE.search(span)), file_label)
        (sim if route.startswith(SIM_PREFIX) else test).append(handler)


def scan_server(server_dir: Path, result: Result) -> None:
    """Rules 5 and 6: the sim surface takes no store, and the test surface's store routes are exact."""
    for path in server_files(server_dir):
        try:
            raw = path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            raise Refusal("SERVER-FILE-UNREADABLE", f"{path}: {exc}") from exc
        code = cscan.strip_comments_preserving_layout(raw)
        groups = {m.group(1): m.group(2) for m in MAP_GROUP.finditer(code)}
        file_label = path.name

        collect_handlers(code, groups, file_label, result.sim_handlers, result.sim_handlers,
                         result.test_handlers, result)

        # Mapper bodies, gathered across the whole tree FIRST: a mapper defined in one file is called
        # from another, so resolving call sites before all definitions are known would miss the half
        # that matters. Resolved in a second pass over the same code.
        for match in MAPPER_CALL.finditer(code):
            var_name, mapper_name = match.groups()
            if var_name not in groups or mapper_name not in MAPPERS:
                continue
            mapper = MAPPERS[mapper_name]
            inner = {mapper.param: groups[var_name]}
            collect_handlers(mapper.body, inner, file_label, result.sim_handlers, result.sim_handlers,
                             result.test_handlers, result)

    # Rule 5: no /api/sim handler may take RpgStore.
    for handler in result.sim_handlers:
        if handler.takes_store:
            result.violations.append(
                f"{handler.file}: /api/sim handler '{handler.route}' takes RpgStore -- the sim surface "
                "is the INPUT feed; a handler that reaches the store directly lets a scenario write "
                "state through a route no player can reach")

    # Rule 6, in BOTH directions.
    for handler in result.test_handlers:
        if not handler.takes_store:
            continue
        if handler.route not in result.test_store_routes:
            result.test_store_routes.append(handler.route)
        if handler.route not in TEST_STORE_ALLOWLIST:
            result.violations.append(
                f"{handler.file}: /api/test handler '{handler.route}' takes RpgStore and is not on the "
                "allowlist in scripts/guard-sim-fabrication.py -- a new direct-store fixture route is a "
                "reviewed change and needs a written reason")
    for route in TEST_STORE_ALLOWLIST:
        if route not in result.test_store_routes:
            result.violations.append(
                f"stale allowlist entry '{route}' in scripts/guard-sim-fabrication.py -- no /api/test "
                "handler takes RpgStore there any more. A stale allowlist is how the next real handler "
                "slips in unread")


# Filled by `index_mappers` before the per-file loop, because a mapper may be defined after it is called.
MAPPERS: dict[str, Mapper] = {}


def index_mappers(paths: list[Path], result: Result) -> None:
    """Every extension mapper in the tree, keyed by name, with its body and its group parameter.

    The whole tree is indexed before any call site is resolved. A single-pass version would find the
    mappers that happen to be defined ABOVE their call site and silently miss the rest -- and a missed
    mapper means a missed `/api/test` route, which is a MISSED VIOLATION on a guard whose whole job is
    not missing them.
    """
    for path in paths:
        try:
            code = cscan.strip_comments_preserving_layout(
                path.read_text(encoding="utf-8", errors="replace"))
        except OSError as exc:
            raise Refusal("SERVER-FILE-UNREADABLE", f"{path}: {exc}") from exc
        for match in MAPPER_DEF.finditer(code):
            name, param = match.groups()
            open_index = match.end() - 1
            close = find_matching_close(code, open_index, "{", "}")
            if close < 0:
                result.violations.append(f"guard defect: unbalanced mapper body for {name} in {path.name}")
                continue
            MAPPERS[name] = Mapper(name, param, str(path), code[open_index:close + 1])


def scan(root: Path, scenario_dir: Path, server_dir: Path, vocabulary: Path) -> Result:
    """The whole guard: the closed table, HALF A, HALF B. Findings accumulate into one result."""
    result = Result()
    call_table = read_call_table(vocabulary)
    vocab_names = sorted(call_table)

    scenarios, golden = scenario_files(scenario_dir)
    result.golden_skipped = golden
    for path in scenarios:
        rel = relative_to_root(path, root, scenario_dir)
        scan_scenario(path, rel, result, call_table, vocab_names)

    paths = server_files(server_dir)
    MAPPERS.clear()
    index_mappers(paths, result)
    scan_server(server_dir, result)
    return result


def verdict_line(result: Result) -> str:
    """The reading line. A READING, not a population: nothing asserts these numbers, and they are
    printed on the success path because a guard that silently found zero scenarios is indistinguishable
    from an honest corpus."""
    sim_store = sum(1 for h in result.sim_handlers if h.takes_store)
    return (f"sim fabrication guard: scenarios={result.scenarios} steps={result.steps} "
            f"reads={result.reads} test.* steps={result.test_steps} | "
            f"/api/sim handlers={len(result.sim_handlers)} (take RpgStore: {sim_store}) | "
            f"/api/test handlers={len(result.test_handlers)} "
            f"(take RpgStore: {len(result.test_store_routes)}, "
            f"allowlisted: {len(TEST_STORE_ALLOWLIST)}) | "
            f"golden verdicts skipped={result.golden_skipped}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: a scenario may not assert on state it created through anything but a real "
                    "route (replaces guard-sim-fabrication.ps1).")
    parser.add_argument("--root", type=Path, default=None,
                        help="the repository to check (default: this tool's own repository)")
    parser.add_argument("--scenario-dir", type=Path, default=None,
                        help="the scenario corpus (default: <root>/tests/fixtures/rpg-scenarios)")
    parser.add_argument("--server-dir", type=Path, default=None,
                        help="the shim surface (default: <root>/src/FusionRpg.Server)")
    parser.add_argument("--vocabulary-path", type=Path, default=None,
                        help="the closed call vocabulary "
                             "(default: <root>/tools/RpgSim/ScenarioVocabulary.cs)")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    root = (args.root or Path(__file__).resolve().parent.parent).resolve()
    scenario_dir = args.scenario_dir or root / "tests" / "fixtures" / "rpg-scenarios"
    server_dir = args.server_dir or root / "src" / "FusionRpg.Server"
    vocabulary = args.vocabulary_path or root / "tools" / "RpgSim" / "ScenarioVocabulary.cs"
    if not vocabulary.is_file():
        refusal = Refusal("VOCABULARY-MISSING", str(vocabulary))
    else:
        refusal = None
    if refusal is not None:
        result = None
    else:
        try:
            result = scan(root, scenario_dir, server_dir, vocabulary)
        except Refusal as caught:
            result, refusal = None, caught

    if refusal is not None:
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "violations": []}, indent=2))
        else:
            print(f"SIM FABRICATION GUARD REFUSED: {refusal.reason}", file=sys.stderr)
            if refusal.detail:
                print(f"  {refusal.detail}", file=sys.stderr)
        return EXIT_FAILED

    assert result is not None
    if args.json:
        print(json.dumps({
            "guard": GUARD_ID,
            "verdict": "FAIL" if result.violations else "OK",
            "violations": result.violations,
            "scenarios": result.scenarios,
            "steps": result.steps,
            "reads": result.reads,
            "test_steps": result.test_steps,
            "golden_skipped": result.golden_skipped,
            "sim_handlers": len(result.sim_handlers),
            "test_handlers": len(result.test_handlers),
            "test_store_routes": len(result.test_store_routes),
            "test_store_allowlist": len(TEST_STORE_ALLOWLIST),
            "test_op_allowlist": len(TEST_OP_ALLOWLIST),
        }, indent=2))
        return EXIT_FAILED if result.violations else EXIT_OK

    # THE READING GOES FIRST, on stdout, before anything on stderr. The original printed it first too
    # (it is the last thing `Write-Host` reaches before the verdict block), so a caller that merges the
    # two streams sees the same order from both. Findings then go to stderr, which is the only
    # difference from the original and the point of the port: stdout alone is the report, and nothing
    # that could be mistaken for a finding is on it.
    print(verdict_line(result))
    if result.violations:
        print(f"SIM FABRICATION GUARD FAILED ({len(result.violations)} violation(s)):", file=sys.stderr)
        for violation in result.violations:
            print(f"  {violation}", file=sys.stderr)
        return EXIT_FAILED
    print("SIM FABRICATION GUARD OK")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
