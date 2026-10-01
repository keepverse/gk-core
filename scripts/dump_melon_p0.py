#!/usr/bin/env python3
"""Dump Melon vs Bep `Assembly-CSharp` symbols for the P0 gate.

Generates a small C# project in a scratch directory, builds it, and runs it against the Melon
`Il2CppAssemblies/Assembly-CSharp.dll` and the BepInEx `interop/Assembly-CSharp.dll`, printing the types
and methods this gate cares about plus a namespace census.

Replaces `scripts/dump-melon-p0.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **THE SCRIPT REPORTED SUCCESS WHEN NOTHING HAD RUN.** It ran `dotnet build -v q | Out-Null` and then
  `dotnet run`, never checked `$LASTEXITCODE` for either, and finished with `Pop-Location` and a
  `Write-Host`. So a failed build and a failed reflection both ended with **exit 0**. The one tool whose
  whole job is to tell a developer whether two assemblies agree had no way to say they did not.

* **THE BUILD'S OUTPUT WAS DISCARDED, so a build failure had no diagnostic at all.** `| Out-Null` threw
  away the compiler's messages for the step most likely to fail, leaving only whatever `dotnet run`
  printed afterwards -- which is nothing, because `dotnet run` on a project that did not build says so
  tersely.

* **THE SCRATCH DIRECTORY HAD A FIXED NAME AND WAS SHARED BY EVERY CALLER.** `$env:TEMP\fusionrpg-p0-dump`,
  with `New-Item -Force` and `Set-Content` overwriting. Two runs overlapped -- two developers, or one
  developer and a CI job -- and each overwrote the other's `Program.cs` mid-flight, so the tool could
  reflect over the OTHER run's payload. `tempfile.mkdtemp` gives each run its own.

* **NO TIMEOUT ON EITHER `dotnet` CALL.** A wedged build or a reflection that loops holds the run open
  indefinitely.

* **A MISSING BepInEx ASSEMBLY WAS A WARNING AND A CONTINUE, WHICH IS FINE, BUT THE MELON SIDE'S MISSING
  ASSEMBLY WAS A BARE `throw` WITH NO EXIT CODE OF ITS OWN** -- so a configuration fault and a tool
  failure were the same event to a caller.

WHAT THIS TOOL MUST NOT DO
--------------------------
It must never exit 0 having reflected over nothing. Every `dotnet` invocation's exit code is checked, and
the report says which side produced which output, so "Melon missing, Bep fine" is distinguishable from
"both fine" and from "nothing ran".
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "dump-melon-p0"

# EVERY global this tool touches is bound ONCE, to a module-private name, and only those names are
# called. Not style: the contract suite has to substitute them, and `dm.subprocess` IS the process-wide
# `subprocess` module -- so `mock.patch.object(dm.subprocess, "run", ...)` is a global patch wearing a
# local name. When such a patch outlives its `with` block, every OTHER test in the process breaks while
# this one stays green: that is how a 40-case suite produced 506 failures in `test_ps1_port_census.py`
# (whose sandbox calls `subprocess.run(["git", "init"], check=True)`, so with `run` still a mock the
# throwaway repo was never created). Binding the seam here makes a leak impossible by construction
# rather than by discipline.
_RUN = subprocess.run
_WHICH = shutil.which
_RMTREE = shutil.rmtree
_MKTEMPT = tempfile.mkdtemp
EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

# A `dotnet build` on a cold NuGet cache restores first; a reflection pass over a large assembly follows.
DEFAULT_BUILD_TIMEOUT = 600
DEFAULT_RUN_TIMEOUT = 300

# Where the two-sided dump is READ. Two entry points, two documentation surfaces; see
# `gk-core/scripts/dump_game_profile.py`. A shared constant rather than two literals, so the profile entry point
# cannot drift back to pointing at the P0 memo.
P0_HINT = "Update docs/research/melonloader-assembly-csharp-p0.md with any deltas."

MELON_RELATIVE = ("MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll")
MELON_NET6 = ("MelonLoader", "net6")
BEP_ASM_RELATIVE = ("BepInEx", "interop", "Assembly-CSharp.dll")
BEP_CORE_RELATIVE = ("BepInEx", "core")

# The payload, verbatim from the original's here-string. It is the SUBJECT, not the wrapper: this C# is
# what reflects over the assembly, so a rewrite would be a different tool rather than a port of this one.
PROGRAM_CS = """using System; using System.Linq; using System.Reflection; using System.Runtime.Loader;
var path = args[0];
var extraDirs = args.Skip(1).ToArray();
var alc = new AssemblyLoadContext("p0", true);
alc.Resolving += (c, n) => {
  foreach (var dir in extraDirs.Append(Path.GetDirectoryName(path)!)) {
    var cand = Path.Combine(dir!, n.Name + ".dll");
    if (File.Exists(cand)) { try { return c.LoadFromAssemblyPath(cand); } catch {} }
  }
  return null;
};
var a = alc.LoadFromAssemblyPath(path);
Type[] types;
try { types = a.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
foreach (var n in new[] { "Plant", "Zombie", "Board", "CreateZombie" }) {
  var t = types.FirstOrDefault(x => x.Name == n);
  Console.WriteLine("== " + n + " ==");
  if (t == null) { Console.WriteLine("  (missing)"); continue; }
  Console.WriteLine("  " + t.FullName);
  foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
      .Where(m => m.Name.Contains("TakeDamage", StringComparison.Ordinal) || m.Name.StartsWith("SetZombie", StringComparison.Ordinal))) {
    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
    Console.WriteLine("  " + m.Name + "(" + m.GetParameters().Length + ") " + ps);
  }
}
var globalCount = types.Count(t => string.IsNullOrEmpty(t.Namespace));
var il2 = types.Count(t => (t.Namespace ?? "").StartsWith("Il2Cpp", StringComparison.Ordinal));
Console.WriteLine($"stats global={globalCount} il2cppNs={il2} total={types.Length}");
"""

# NOTE ON THE PAYLOAD: the original computed `ps` (the parameter type list) and then never printed it --
# its line read `Console.WriteLine("  " + m.Name + "(" + len + ") ")`. The parameter types are what a
# Melon-vs-Bep comparison is read for, so they are printed here: a dump that quietly emits half of what it
# replaced is a smaller report than the one it retires, and the fix belongs in the port rather than in a
# note about the port.

CSPROJ = ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
          '<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
          "</PropertyGroup></Project>")

SIDES = ("melon", "bep")


class Refusal(Exception):
    """A named precondition or stage failure. Never exits 0 having not run."""

    def __init__(self, reason: str, detail: str, exit_code: int = EXIT_REFUSED) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail
        self.exit_code = exit_code


@dataclass
class SideResult:
    """One assembly's reflection pass: what was asked for, and what came back."""

    side: str
    assembly: str
    ran: bool = False
    exit: int = EXIT_REFUSED
    timed_out: bool = False
    output: str = ""
    reason: str = ""

    @property
    def ok(self) -> bool:
        return self.ran and self.exit == 0 and not self.timed_out


@dataclass
class Report:
    melon_dir: str = ""
    bep_dir: str = ""
    scratch: str = ""
    build_exit: int = EXIT_REFUSED
    build_timed_out: bool = False
    build_output: str = ""
    sides: list[SideResult] = field(default_factory=list)
    refused: tuple[str, str] | None = None
    profile_id: str = ""

    @property
    def ok(self) -> bool:
        """OK means the build succeeded AND every side that RAN succeeded.

        EVERY, not ANY. `any(s.ok ...)` reported OK when the Melon pass was green and the BepInEx pass
        failed -- and a P0 gate reading green from a tool whose job is to say whether two assemblies agree
        is the precise failure this port exists to remove. A side that did NOT run (an absent BepInEx
        assembly) is a legitimate Melon-only result and is excluded; a side that ran and failed is not.

        Caught by the contract suite, not by the differential: the differential drives HEALTHY runs, where
        both implementations agree, so a defect in how RED is reported is invisible to it by
        construction.
        """
        if self.refused is not None or self.build_exit != 0 or self.build_timed_out:
            return False
        ran = [side for side in self.sides if side.ran]
        return bool(ran) and all(side.ok for side in ran)


def resolve_dotnet() -> str:
    """The `dotnet` executable, resolved ONCE and named when absent.

    The RESOLVED path is what runs. `CreateProcess` resolves a bare name by appending `.exe` only, so
    spawning the bare token can silently reach a different tool than the one that was resolved -- the same
    gap two earlier ports closed.
    """
    found = _WHICH("dotnet")
    if not found:
        raise Refusal("DOTNET-NOT-ON-PATH",
                      "dotnet is not on PATH; install the .NET SDK, or run this tool where it is")
    return found


def write_scratch(directory: Path) -> None:
    """The generated project. Written ONCE per run into a directory this run owns."""
    (directory / "Program.cs").write_text(PROGRAM_CS, encoding="utf-8")
    (directory / "p0.csproj").write_text(CSPROJ, encoding="utf-8")


def spawn(argv: list[str], cwd: Path, timeout: int, stage: str) -> tuple[int, bool, str]:
    """One `dotnet` call, bounded. A timeout is returned, never raised, so the report still says which."""
    try:
        proc = _RUN(argv, capture_output=True, text=True, timeout=timeout, cwd=str(cwd))
        return proc.returncode, False, (proc.stdout or "") + (proc.stderr or "")
    except subprocess.TimeoutExpired as expired:
        partial = (expired.stdout or b"")
        text = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) else str(partial)
        return EXIT_FAILED, True, f"{text}\n[no result within {timeout}s]"
    except (OSError, FileNotFoundError) as exc:
        raise Refusal(f"{stage}-INVOCATION-FAILED", str(exc)) from exc


def check_inputs(ml_game_dir: Path, bep_game_dir: Path) -> tuple[Path, Path, Path, str | None]:
    """The Melon assembly must exist; a missing Bep one is reported, not refused.

    The original refused the Melon side with a bare `throw` and merely warned about the Bep side. That
    asymmetry is CORRECT -- a Melon-only dump is a legitimate result -- but the refusal had no exit code of
    its own, so a configuration fault and a tool failure were the same event to a caller.
    """
    if not ml_game_dir.is_dir():
        raise Refusal("ML-GAME-DIR-MISSING",
                      f"Set FUSIONRPG_ML_GAMEDIR to a MelonLoader game folder; {ml_game_dir} is not a "
                      f"directory")
    melon_asm = ml_game_dir.joinpath(*MELON_RELATIVE)
    if not melon_asm.is_file():
        raise Refusal("MELON-ASSEMBLY-MISSING",
                      f"missing {melon_asm} (expected at <MelonLoader>/Il2CppAssemblies)")
    bep_asm = bep_game_dir.joinpath(*BEP_ASM_RELATIVE)
    bep_note = None if bep_asm.is_file() else f"Bep Assembly-CSharp missing at {bep_asm} - Melon-only dump"
    return melon_asm, ml_game_dir.joinpath(*MELON_NET6), bep_asm, bep_note


def execute(ml_game_dir: Path, bep_game_dir: Path, profile_id: str, scratch_parent: Path | None,
            keep_scratch: bool, build_timeout: int, run_timeout: int) -> Report:
    executable = resolve_dotnet()
    melon_asm, melon_net6, bep_asm, bep_note = check_inputs(ml_game_dir, bep_game_dir)

    report = Report(melon_dir=str(ml_game_dir), bep_dir=str(bep_game_dir), profile_id=profile_id)
    if bep_note:
        report.sides.append(SideResult(side="bep", assembly=str(bep_asm), ran=False,
                                       reason=bep_note))

    # A directory THIS RUN OWNS. The original used a fixed `$env:TEMP/fusionrpg-p0-dump` that every caller
    # shared and overwrote with `Set-Content`, so two overlapping runs could reflect over each other's
    # payload.
    scratch = Path(_MKTEMPT(prefix="fusionrpg-p0-dump-", dir=str(scratch_parent)
                                    if scratch_parent else None))
    report.scratch = str(scratch)
    try:
        write_scratch(scratch)
        report.build_exit, report.build_timed_out, report.build_output = spawn(
            [executable, "build", "-v", "q"], scratch, build_timeout, "BUILD")
        if report.build_timed_out:
            return report
        if report.build_exit != 0:
            # The compiler's own output is the report. The original piped the build to Out-Null, so the
            # one step most likely to fail had no diagnostic.
            #
            # NOTE the ordering: a TIMEOUT returns above, before this message. A build that ran out of
            # time did not "fail with exit 1", and saying so would put a false statement in the only
            # diagnostic this tool emits for a red build.
            report.build_output += (f"\n[build failed with exit {report.build_exit}; the generated "
                                    f"project is at {scratch}]")
            return report

        melon = SideResult(side="melon", assembly=str(melon_asm))
        melon.exit, melon.timed_out, melon.output = spawn(
            [executable, "run", "--", str(melon_asm), str(melon_net6)], scratch, run_timeout, "RUN")
        melon.ran = True
        report.sides.insert(0, melon)
        if not melon.ok:
            return report

        if bep_asm.is_file():
            bep = SideResult(side="bep", assembly=str(bep_asm))
            bep.exit, bep.timed_out, bep.output = spawn(
                [executable, "run", "--", str(bep_asm), str(bep_game_dir.joinpath(*BEP_CORE_RELATIVE))],
                scratch, run_timeout, "RUN")
            bep.ran = True
            report.sides.append(bep)
        return report
    finally:
        if keep_scratch:
            pass
        else:
            # Removed. The original left it, which is the shape this repository's testing standard calls
            # out: a temp directory that is never cleaned is how one local run leaked 65.5 GB.
            _RMTREE(scratch, ignore_errors=False)


def build_parser(tool_id: str = TOOL_ID, default_profile_id: str = "") -> argparse.ArgumentParser:
    """The CLI surface, built ONCE and shared.

    `gk-core/scripts/dump_game_profile.py` is the same tool aimed at the profile documentation, and it takes the
    same flags. Building the parser from a function is what keeps those two entry points from drifting
    into two spellings of one interface -- which is exactly how the two PowerShell files already
    differed, each computing the same repository-root fallback on its own.
    """
    parser = argparse.ArgumentParser(
        prog=Path(tool_id).name,
        description="Dump Melon vs Bep Assembly-CSharp symbols (replaces dump-melon-p0.ps1 and "
                    "dump-game-profile.ps1).")
    parser.add_argument("--ml-game-dir", default=os.environ.get("FUSIONRPG_ML_GAMEDIR", ""),
                        help="MelonLoader game folder (default: $FUSIONRPG_ML_GAMEDIR)")
    parser.add_argument("--bep-game-dir",
                        default=os.environ.get("FUSIONRPG_GAME_DIR", ""),
                        help="BepInEx game folder; an absent BepInEx assembly is a Melon-only dump "
                             "(default: $FUSIONRPG_GAME_DIR)")
    parser.add_argument("--profile-id", default=os.environ.get("FUSIONRPG_GAME_PROFILE",
                                                               default_profile_id),
                        help="recorded in the report and echoed as the doc hint")
    parser.add_argument("--keep-scratch", action="store_true",
                        help="leave the generated project on disk, and say where")
    parser.add_argument("--scratch-parent", type=Path, default=None,
                        help="where the per-run scratch directory is created (default: the system temp)")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for `dotnet build` (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--run-timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"seconds for each `dotnet run` (default {DEFAULT_RUN_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def render(report: Report, as_json: bool, tool_id: str = TOOL_ID,
           doc_hint: str = P0_HINT) -> None:
    if as_json:
        print(json.dumps({"tool": tool_id,
                          "verdict": "OK" if report.ok else "FAILED",
                          "exitCode": EXIT_OK if report.ok else EXIT_FAILED,
                          "melonGameDir": report.melon_dir, "bepGameDir": report.bep_dir,
                          "scratch": report.scratch, "profileId": report.profile_id,
                          "build": {"exit": report.build_exit, "timedOut": report.build_timed_out,
                                    "output": report.build_output},
                          "sides": [side.__dict__ for side in report.sides]}, indent=2))
        return
    print(f"== MELON ({report.melon_dir}) ==")
    for side in report.sides:
        if side.side != "melon":
            continue
        if side.ok:
            for line in side.output.splitlines():
                print(line)
        else:
            print(f"  melon pass {side.reason or f'failed with exit {side.exit}'}")
    for side in report.sides:
        if side.side == "bep":
            if side.ran and side.ok:
                print(f"== BEP ({report.bep_dir}) ==")
                for line in side.output.splitlines():
                    print(line)
            else:
                print(f"  {side.reason or f'bep pass failed with exit {side.exit}'}")
    # The closing hint names the documentation surface the OUTPUT IS FOR, and there are two of them. It
    # is REPLACED, never appended: printing the P0 memo after a profile dump would send an operator to
    # update the wrong document, which is the confusion the profile entry point exists to avoid.
    print(doc_hint)


def run(args: argparse.Namespace, tool_id: str = TOOL_ID, profile_hint: str | None = None,
        default_profile_id: str = "") -> int:
    """Validate, execute, report. Returns the exit code; never raises past a refusal.

    Split out of `main` so the profile entry point can share the whole path -- validation, the
    directory defaults, the refusal vocabulary and the envelope -- instead of re-implementing the parts
    it happened to need.

    `default_profile_id` is the CALLER's fallback for an empty `--profile-id`, and it is passed in
    rather than read from a module constant because the constant belongs to the profile entry point
    (`dump_game_profile.py`), not here. Naming it unqualified read as though this module owned it; it
    does not, so an empty `--profile-id` on the profile entry point raised `NameError` instead of
    printing the hint the caller had already configured a default for. The same reason the parser
    takes the default as a parameter: one owner per value.
    """
    for name, value in (("--build-timeout", args.build_timeout), ("--run-timeout", args.run_timeout)):
        if value <= 0:
            message = f"{name} must be positive"
            if args.json:
                print(json.dumps({"tool": tool_id, "verdict": "REFUSED", "reason": "INVALID-TIMEOUT",
                                  "detail": message, "exitCode": EXIT_REFUSED}, indent=2))
            else:
                print(f"[{tool_id}] REFUSED: INVALID-TIMEOUT: {message}", file=sys.stderr)
            return EXIT_REFUSED

    # Configuration read ONCE, explicitly, and failing loudly when absent. The original's `$MlGameDir`
    # defaulted to an env var that is usually unset and then tested it, so the failure arrived as a bare
    # `throw` naming nothing to install.
    if not args.ml_game_dir.strip():
        message = ("Set FUSIONRPG_ML_GAMEDIR (or pass --ml-game-dir) to a MelonLoader game folder; "
                   "it is not set in this environment")
        if args.json:
            print(json.dumps({"tool": tool_id, "verdict": "REFUSED", "reason": "ML-GAME-DIR-UNSET",
                              "detail": message, "exitCode": EXIT_REFUSED}, indent=2))
        else:
            print(f"[{tool_id}] REFUSED: ML-GAME-DIR-UNSET: {message}", file=sys.stderr)
        return EXIT_REFUSED

    root = Path(__file__).resolve().parent.parent
    bep_game_dir = Path(args.bep_game_dir).expanduser() if args.bep_game_dir.strip() else root
    try:
        report = execute(Path(args.ml_game_dir).expanduser(), bep_game_dir, args.profile_id,
                         args.scratch_parent, args.keep_scratch, args.build_timeout, args.run_timeout)
    except Refusal as refusal:
        if args.json:
            print(json.dumps({"tool": tool_id, "verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail, "exitCode": refusal.exit_code}, indent=2))
        else:
            print(f"[{tool_id}] REFUSED: {refusal.reason}", file=sys.stderr)
            print(f"  {refusal.detail}", file=sys.stderr)
        return refusal.exit_code

    render(report, args.json, tool_id,
           doc_hint=(profile_hint.format(profile=args.profile_id or default_profile_id)
                     if profile_hint else P0_HINT))
    return EXIT_OK if report.ok else EXIT_FAILED


def main(argv: list[str] | None = None) -> int:
    return run(build_parser().parse_args(argv))


if __name__ == "__main__":
    sys.exit(main())
