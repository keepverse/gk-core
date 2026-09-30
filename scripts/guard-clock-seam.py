#!/usr/bin/env python3
r"""Guard: the clock seam — one wall clock, read through one type, and never in the simulation trees.
Replaces `guard-clock-seam.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **Every line went out through `Write-Host`**, invisible to a `2>&1` capture. Findings now go to
  stderr, the OK verdict to stdout, and the counter summary to stderr as a diagnostic, so stdout
  carries the verdict and nothing else.
* **The repo-relative path was mis-sliced, which for THIS guard breaks the allowlist.** Every finding
  computed it as `$file.FullName.Substring($Root.Length)`, and `Get-ChildItem` returns a canonicalised
  path while `$Root` is whatever the caller spelled. Under an 8.3 short root the slice drops
  characters — and the allowlist is KEYED BY REPO-RELATIVE PATH, so a mis-sliced `$rel` matches no
  entry and every allowlisted site is reported as a violation. `Path.relative_to` cannot mis-slice.

WHICH STRIPPER, AND WHY THE OBVIOUS ONE IS WRONG
------------------------------------------------
Comments are blanked and line numbers are preserved, so a finding cites a line an operator can open.
That part is `cscan.strip_comments_preserving_layout`, and the guard's private copy of it is the same
policy — the port reuses the shared one rather than carrying a sixth copy.

It is specifically the policy that KEEPS string literals, and that matters: the ambient pattern
`DateTime(Offset)?\.(Now|UtcNow)` matches text inside a string, so `Log("DateTime.UtcNow")` is
reported. That over-match is the contract. `cscan.strip_comments_and_literals_preserving_layout` would
blank the literal and NARROW the guard — the same shape of silent weakening this program exists to
remove, and a plausible-looking "cleanup" that nobody would notice. A test says so explicitly.

TWO CASE CONVENTIONS IN ONE SCRIPT, AGAIN
------------------------------------------
* Rule 1's ambient pattern was matched with `[regex]::IsMatch` → **case-SENSITIVE**.
* Rule 2's `ServerClock` pattern was matched with `-notmatch` → **case-INSENSITIVE**.
* The allowlist test was `$trimmed.Contains($entry.Match)` → .NET `String.Contains`, an **ordinal
  case-sensitive** substring.

Three conventions, one script, none of them the repo's default. They are per call site, not per repo,
and the same pair of conventions already split `guard-actor-hub` from `guard-funnel-delta`.

RULE 1 HAS A SECOND HALF, AND IT IS THE ONE THAT GOES STALE
-----------------------------------------------------------
Every allowlist entry must still match a real ambient read. An allowlist entry that no longer matches
is reported, because a stale allowlist is how the next genuine clock read slips in unexamined. This
also means the guard is deliberately sensitive to its source set: point it at a directory with few
`.cs` files and most entries read as stale. That is fail-closed, and it is why a missing source tree
reddens rather than passing quietly.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))
from cscan import strip_comments_preserving_layout  # noqa: E402
from keepverse_roots import RootNotFound, fusion_root_or_owner  # noqa: E402

GUARD_ID = "clock-seam"
VERDICT_OK = "CLOCK SEAM GUARD OK"
VERDICT_FAILED = "CLOCK SEAM GUARD FAILED ({count} violation(s)):"
EXIT_OK = 0
EXIT_FAILED = 1
# A REFUSAL IS NOT A FINDING, AND THE EXIT CODE IS HOW SAY SO. guard-stat-pairs.py already
# established 64 for "I cannot run"; these four mapped every Refusal onto EXIT_FAILED, so a
# guard refusing because a sibling repository is not checked out was indistinguishable from a
# guard that found a violation. A standalone clone is a SUPPORTED layout, so on a clone six
# guards legitimately cannot run, and an operator has to be able to read that as a named
# condition rather than as six broken guards.
EXIT_REFUSED = 64

# The one clock type: the file that IS the seam.
CLOCK_TYPE = "src/FusionRpg.Core/Time/ServerClock.cs"

DEFAULT_SRC_DIR = "src"
BUILD_OUTPUT = ("bin", "obj")
SOURCE_SUFFIX = ".cs"

# CASE-SENSITIVE: the original used [regex]::IsMatch, which does not fold.
AMBIENT_PATTERN = re.compile(r"DateTime(Offset)?\.(Now|UtcNow)")
# CASE-INSENSITIVE: the original used -notmatch, which does.
PURITY_PATTERN = re.compile(r"\bServerClock\b", re.IGNORECASE)

# Rule 2: the replay-critical trees. A match is on the ABSOLUTE, forward-slashed path, exactly as the
# original did — the original never stripped the root for this check, and narrowing it to a
# repo-relative form would change which files are in scope.
PURITY_ROOTS = ("FusionRpg.Core/World", "FusionRpg.Core/Battle", "FusionRpg.Core/Effects")

SEAM_HINT = ("read the seam (FusionRpg.Core.Time.ServerClock) instead, or add a reason to the "
             "allowlist in scripts/guard-clock-seam.py "
             "(docs/architecture/rpg-simulator-spec-clock-seam.md section 7)")

# The allowlist, keyed by repo-relative path. Every entry carries its REASON, because an allowlist
# entry without one is indistinguishable from an oversight, and the reasons are the specification:
# docs/architecture/rpg-simulator-spec-clock-seam.md section 7 is the authority for the list.
# Entries marked MEASURED were added when a scan found a real site the spec's list had missed.
ALLOWLIST: dict[str, tuple[tuple[str, str], ...]] = {
    "src/FusionRpg.Core/Effects/EffectModels.cs": (
        ("public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;",
         "SystemEffectClock - the purity scan's ONE named exemption, and rightly so: the injector's "
         "live-PvZ effect runtime applies effects to a real-time match and claims no replay "
         "determinism. It is the precedent for the seam, not a site to migrate."),
    ),
    "src/FusionRpg.Data/Sqlite/RpgStore.cs": (
        ("LastHeartbeatUtc is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(5)",
         "SPEC 7's TRAP, and a safety one. This 5-second freshness window feeds InjectorConnected -> "
         "LiveInjector, which is the property SimService.Guard() reads for its D1 (b) refusal. Shift "
         "the clock forward and a LIVE injector looks stale, so the refusal silently stops firing and "
         "a scenario could run against a player's install; shift it backward and a dead injector looks "
         "alive."),
        ("LastHeartbeatUtc = DateTimeOffset.UtcNow",
         "The write the window above compares against. It must stay on the same clock as the read: a "
         "shifted write against a raw read (or the reverse) breaks the window just as surely as "
         "shifting the read alone."),
    ),
    "src/FusionRpg.Server/DebugEndpoints.cs": (
        ("var snapshotDeadline = DateTime.UtcNow.AddSeconds(15);",
         "SPEC 7: a bound on a LIVE process (a board snapshot from a real injector). A simulated "
         "clock makes the loop exit instantly or spin for ever."),
        ("while (DateTime.UtcNow < snapshotDeadline && snapshot is null)",
         "The same live-process deadline's loop condition."),
        ("var deadline = DateTime.UtcNow + timeout;",
         "SPEC 7: the kind-poll deadline, same class - a bound on a live process, not a game "
         "duration."),
        ("while (DateTime.UtcNow < deadline)",
         "The same live-process deadline's loop condition."),
        ("var cutoff = DateTime.UtcNow - window;",
         "MEASURED addition to SPEC 7's list: a freshness window over LIVE events "
         "(CountRecentEventsOfKind, which catches a rapidly cycling board before quick-start polls "
         "into a confusing timeout). Same class as the heartbeat window - shifting the clock makes "
         "live data look stale."),
        ("var now = DateTime.UtcNow;",
         "MEASURED addition to SPEC 7's list: the /api/debug/lawn/state recency window (30 s on "
         "board.economy, and the sinceMs read). A freshness window over live lifecycle signals."),
    ),
    "src/FusionRpg.Server/WebMatchService.cs": (
        ("var t0 = DateTime.UtcNow;",
         "SPEC 7: the match-resolution ingest stamp. The engine is clockless and every event is "
         "stamped strictly monotonic from this one value at ingest; simulating it would make an "
         "ingest ordering a number the sim chose."),
    ),
    "src/FusionRpg.Injector/Hud/OverlayViewHost.cs": (
        ("var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);",
         "SPEC 7: a bound on a frame-driven wait inside the game. Shifting it changes what the "
         "player sees, and the loop's done() predicate is real engine state."),
        ("while (!done() && DateTime.UtcNow < deadline)",
         "The same in-game wait deadline's loop condition."),
    ),
    "src/FusionRpg.Injector/CheatState.cs": (
        ("ActiveProbeUtc = DateTime.UtcNow;",
         "SPEC 7: the probe-timeout freshness window's write. It must stay on the same clock as the "
         "read below."),
        ("if ((DateTime.UtcNow - ActiveProbeUtc).TotalMinutes > ProbeTimeoutMinutes)",
         "SPEC 7: a freshness window over a LIVE probe. Same class as the RpgStore heartbeat trap."),
    ),
    "src/FusionRpg.Injector/CheatCommandRunner.cs": (
        ("CheatState.ActiveProbeUtc = DateTime.UtcNow;",
         "MEASURED addition to SPEC 7's list: the other write to the probe window above. A shifted "
         "write against a raw read breaks the window."),
    ),
    "src/FusionRpg.Launcher/Services/HealthMonitor.cs": (
        ("var deadline = DateTime.UtcNow + timeout;",
         "SPEC 7: a bound on a process that is STARTING. A simulated clock either skips the wait or "
         "waits for ever."),
        ("while (DateTime.UtcNow < deadline)",
         "The same server-start deadline's loop condition."),
    ),
    "src/FusionRpg.Launcher/MainWindow.xaml.cs": (
        ('var stamp = DateTime.Now.ToString("HH:mm:ss");',
         "MEASURED (RS-F14): FusionRpg.Launcher references NO FusionRpg project at all (its csproj "
         "carries no ProjectReference), so the seam is unreachable from it, and guard-repo-boundary's "
         "pinned graph does not name the Launcher either. The read is a LOCAL UI log stamp - not a "
         "duration, a deadline, or a persisted row. Migrating it needs a Launcher -> Core dependency "
         "decision."),
    ),
    "src/FusionRpg.Injector/Effects/EffectRuntime.cs": (
        ("static readonly Core.Effects.AdvancedEffectClock _clock = new(DateTimeOffset.UtcNow);",
         "SPEC 7's own list calls this site ALREADY INJECTABLE and correctly so - the effect "
         "runtime's monotonic clock, fed by its host. Increment 4 tried sourcing it from ServerClock "
         "and REVERTED, because PlayerSpeciesMaterialiseCallerGuardTests asserts this exact literal as "
         "the proof that the wall clock is still the SOURCE and no round trip was added to the "
         "injector's hot path. Spec 7 says increment 4 MAY source it, never that it must."),
    ),
}


class Refusal(Exception):
    """A named precondition failure. Nothing is reported as clean when this is raised."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def source_files(src_dir: Path) -> list[Path]:
    """`.cs` files under `src_dir`, excluding build output, in a stable order."""
    if not src_dir.is_dir():
        return []
    found = [p for p in src_dir.rglob(f"*{SOURCE_SUFFIX}")
             if p.is_file() and not (set(p.parts) & set(BUILD_OUTPUT))]
    return sorted(found)


def repo_relative(root: Path, path: Path, extra_roots: tuple[Path, ...] = ()) -> str:
    """Repository-relative, forward-slashed, or a NAMED REFUSAL.

    Found by the contract test: with an absolute `--src-dir`, `root / <absolute>` keeps whatever
    spelling the caller used while `root` is resolved, so on Windows the two can be the same directory
    in an 8.3 short form and a long one. `relative_to` then fails, and the first version FELL BACK to
    the absolute path - which silently defeated the allowlist, because it is keyed by repo-relative
    path. Thirty-nine violations on a tree that was clean. A fallback that yields a path the rest of
    the guard cannot use is the `guard-power` mis-slice in a different costume, so there is no
    fallback: a file outside the root is refused by name.
    """
    resolved = path.resolve()
    # The allowlist is keyed by a path RELATIVE TO THE REPOSITORY THAT OWNS THE FILE, so a file
    # this guard legitimately scans from a sibling repository needs that repository as its base -
    # not a fallback, which is what the no-fallback rule below exists to forbid. The distinction
    # is the whole point: a FALLBACK invents a spelling the allowlist cannot match, and it did
    # exactly that here. Declaring the second root up front means every path is either relative
    # to a real repository or refused by name, and the refusal still stands for anything that is
    # genuinely outside every scanned tree.
    for base in (root.resolve(), *(r.resolve() for r in extra_roots)):
        try:
            return resolved.relative_to(base).as_posix()
        except ValueError:
            continue
    raise Refusal("FILE-OUTSIDE-ROOT",
                  f"{resolved} is not under any scanned root "
                  f"({', '.join(str(r) for r in (root, *extra_roots))}), so it has no "
                  f"repository-relative path and the allowlist (which is keyed by one) cannot be "
                  f"applied to it")


def _stripped(text: str) -> list[str]:
    """Comment-stripped, layout-preserved lines — so a finding cites the file's own line number.

    The policy KEEPS string literals, which is deliberate and load-bearing: see the module docstring.
    """
    return strip_comments_preserving_layout(text).split("\n")


def check(root: Path, src_dir: str | None = None) -> dict:
    # Resolved, so an absolute `--src-dir` spelled in the 8.3 short form still lands under a root that
    # `main` has already resolved to the long one. Without this the two disagree and every allowlist
    # entry misses. Found by the contract test; see `repo_relative`.
    src = (root / (src_dir or DEFAULT_SRC_DIR)).resolve()
    if not src.is_dir():
        # Fail CLOSED and say why. The original ran over an empty file set and then reported EVERY
        # allowlist entry as stale, which is a red guard with a misleading message about staleness
        # rather than about a missing tree. Naming it is the difference between a diagnosis and a
        # treasure hunt.
        raise Refusal("SRC-DIR-MISSING", str(src))

    # A DECLARED CROSS-REPOSITORY SOURCE TREE. Five of the nine allowlist entries name
    # FusionRpg.Injector and FusionRpg.Launcher, which are gk-fusion's since the split, so a scan
    # confined to this repository's src/ never sees those files and reports every one of them
    # STALE. That is a red guard whose message is about staleness rather than about a missing
    # sibling - and the obvious "fix" of deleting the five entries deletes the guard's coverage of
    # real clock reads, which is the exact silent narrowing this program exists to prevent.
    #
    # Only added when the caller did NOT pass --src-dir: a contract test that points the guard at a
    # fixture tree is asking about that tree, and quietly adding a second one would make its
    # expectations unreproducible.
    extra_roots: tuple[Path, ...] = ()
    files = source_files(src)
    if src_dir is None or src_dir == DEFAULT_SRC_DIR:
        # Named refusal rather than an unhandled RuntimeError; a sibling that is not checked out
        # is a missing subject, which is what this guard already refuses on for a missing src/.
        try:
            fusion = fusion_root_or_owner(root)
        except RootNotFound as exc:
            raise Refusal("FUSION-ROOT-MISSING", str(exc)) from exc
        if fusion.is_dir():
            extra_roots = (fusion,)
            files = files + source_files(fusion / "src")

    violations: list[str] = []
    ambient_hits = allowed_hits = clock_type_hits = 0
    matched: set[tuple[str, str]] = set()

    # ---- rule 1: an ambient clock read outside the seam and outside the allowlist -------------
    for path in files:
        rel = repo_relative(root, path, extra_roots)
        try:
            lines = _stripped(path.read_text(encoding="utf-8", errors="replace"))
        except OSError as exc:
            violations.append(f"{rel}: unreadable source file: {exc}")
            continue
        for index, line in enumerate(lines):
            if not AMBIENT_PATTERN.search(line):
                continue
            ambient_hits += 1
            if rel == CLOCK_TYPE:
                clock_type_hits += 1
                continue
            trimmed = line.strip()
            entries = ALLOWLIST.get(rel, ())
            # `String.Contains` is an ORDINAL, case-SENSITIVE substring. See the module docstring.
            hit = next((entry for entry in entries if entry[0] in trimmed), None)
            if hit is not None:
                allowed_hits += 1
                matched.add((rel, hit[0]))
                continue
            violations.append(f"{rel}:{index + 1}: ambient clock read '{trimmed}' -- {SEAM_HINT}")

    # ---- rule 1's staleness half: an entry that no longer matches is itself a finding -----------
    entries_total = 0
    for rel, entries in ALLOWLIST.items():
        for fragment, _reason in entries:
            entries_total += 1
            if (rel, fragment) not in matched:
                violations.append(
                    f"stale allowlist entry in scripts/guard-clock-seam.py: '{rel}' no longer contains "
                    f"'{fragment}'. A stale allowlist is how the next real read slips in unread")

    # ---- rule 2: the seam is a wall clock, so the simulation trees may not read one --------------
    purity_hits = 0
    for path in files:
        absolute = path.as_posix()
        if not any(f"/{root_name}/" in absolute for root_name in PURITY_ROOTS):
            continue
        try:
            lines = _stripped(path.read_text(encoding="utf-8", errors="replace"))
        except OSError as exc:
            violations.append(f"{path.name}: unreadable source file: {exc}")
            continue
        for index, line in enumerate(lines):
            if not PURITY_PATTERN.search(line):
                continue
            purity_hits += 1
            # The original reported `$($file.Name)` here — the file NAME, not its path. Transcribed,
            # because a bare name is ambiguous across projects and the finding is read by a person
            # deciding whether to allowlist it.
            violations.append(
                f"{path.name}:{index + 1}: ServerClock is referenced under the replay-critical "
                "simulation trees (Core/World|Battle|Effects). The seam IS a wall clock, and these "
                "trees must read none -- RS-F12's rule, closed here because the C# purity scan bans "
                "symbols by name and could not be taught the new one from this lane")

    summary = (f"clock seam guard: source files={len(files)} ambient reads={ambient_hits} "
               f"(clock type={clock_type_hits}, allowlisted={allowed_hits}, entries={entries_total}) "
               f"| simulation-tree ServerClock references={purity_hits}")
    return {
        "guard": GUARD_ID,
        "verdict": "FAIL" if violations else "OK",
        "violations": violations,
        "summary": summary,
        "source_files": len(files),
        "ambient_reads": ambient_hits,
        "clock_type_reads": clock_type_hits,
        "allowlisted_reads": allowed_hits,
        "allowlist_entries": entries_total,
        "purity_references": purity_hits,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Guard: one wall clock, read through one type (replaces guard-clock-seam.ps1).")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repo root (default: this script's parent directory)")
    parser.add_argument("--src-dir", default=None,
                        help=f"source tree to scan (default: <repo>/{DEFAULT_SRC_DIR})")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)

    try:
        result = check(args.root.resolve(), args.src_dir)
    except Refusal as refusal:
        print(f"{GUARD_ID} REFUSED: {refusal.reason} {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"guard": GUARD_ID, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "violations": []}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    else:
        # A counter summary is a diagnostic, not the verdict, so it goes to stderr: stdout carries
        # the verdict and nothing else, and a caller reading stdout alone is never misled.
        print(result["summary"], file=sys.stderr)
        if result["verdict"] == "OK":
            print(VERDICT_OK)
        else:
            print(VERDICT_FAILED.format(count=len(result["violations"])), file=sys.stderr)
            for violation in result["violations"]:
                print(f"  {violation}", file=sys.stderr)
    return EXIT_OK if result["verdict"] == "OK" else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(main())
