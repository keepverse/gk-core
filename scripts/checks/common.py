#!/usr/bin/env python3
"""Shared runner for the `scripts/checks/gen-*.py` wrappers. Replaces fifteen
`scripts/checks/gen-*.ps1` wrappers.

WHY THE POWERSHELL FORMS WERE RETIRED
-------------------------------------
Fifteen near-identical scripts, each carrying its own copy of the same six lines, and each unable to
say what it was about to do in a form a program could read.

* **A `throw` and a non-zero exit were the same event.** `if ($LASTEXITCODE -ne 0) { throw ... }`
  both failed the build and produced a message, so a caller could not separate "the check found
  something" from "the check could not run" - and the second is a configuration problem that wants a
  different fix. Here a missing toolchain is a named refusal with its own exit code.
* **The command was only discoverable by parsing prose.** The real command was the line before the
  LAST `if ($LASTEXITCODE -ne 0) { throw `, a positional convention nothing enforced, and the
  working directory came from the first `Push-Location (Join-Path $Root "...")`. A wrapper that
  reordered its own lines, or inserted a comment, would silently stop being parseable - and the test
  that depended on it would report a *parity* failure rather than a parse failure. Each wrapper now
  declares `CHECK`, `PREFLIGHT`, `WORKING_DIRECTORY` and `FAIL_HINT` as module-level constants,
  which is the machine-readable spec `GeneratorCheckCiParityTests` reads.
* **No timeout, and `*> $null` for the preflight.** A stalled generator hung; a preflight whose
  output was discarded could not say why it failed.

WHAT THE WRAPPERS ARE
---------------------
They are NOT guards (`docs/architecture/ps1-ban-map.md` §3.4): each is an argument-free convenience
wrapper around the command a `ci.yml` step already runs, from the same working directory. Their
parity with CI is the property worth protecting, because a wrapper that drifts from its CI step is a
check that exists in two places and is enforced in one.

A wrapper exists because running `dotnet run --project gk-forge/tools/TreeBinder -- --check` from the right
directory is easy to get subtly wrong; the wrapper is the one place that spelling lives.
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
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from keepverse_roots import owning_base  # noqa: E402  (the insert above must run first)

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64
EXIT_TIMED_OUT = 1

DEFAULT_TIMEOUT = 1800

# A generation/validation pass over a corpus is the slow case, and a runner that times out on a
# legitimately long one is its own false red. It is bounded rather than absent, which is the whole
# difference from the form this replaces.
VERDICT_OK = "CHECK OK"
VERDICT_FAILED = "CHECK FAILED"
VERDICT_REFUSED = "CHECK REFUSED"
VERDICT_TIMED_OUT = "CHECK FAILED — no result within --timeout"


class Refusal(Exception):
    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(f"{reason}: {detail}" if detail else reason)
        self.reason = reason
        self.detail = detail


def repo_root() -> Path:
    # gk-core/scripts/checks/<wrapper>.py -> gk-core/scripts/checks -> scripts -> repo root.
    return Path(__file__).resolve().parents[2]


def resolve_owned(root: Path, relative: str) -> Path:
    """`relative` against the repository that OWNS it: local root first, then a sibling.

    A check runs its command inside a tree it names the way that tree is named next to the thing that
    owns it - `web/fusion-rpg-web`, `tools/seedsmith`. `root` is gk-core, which after the split has no
    `web/` directory at all, so `root / relative` was a path that could never exist and the web check
    refused WORKING-DIRECTORY-MISSING (exit 64) before running a single command. That is not a subtle
    miscount: it is the refusal arriving before the work, and a caller reads 64 as "the tool is
    broken" rather than "the tree moved".

    LOCAL ROOT FIRST, and that order is load-bearing. A check run against a fixture carries its own
    tree, and that tree is what the fixture is testing; resolving the real workspace first would let
    gk-core's or gk-web's answer a fixture's question.

    The fallback is not a guess and makes nothing weaker: when no repository has the path the caller
    gets `root / relative` exactly as before, so a path that exists nowhere is still reported by name
    and still refuses.
    """
    if relative in (".", ""):
        return root
    base = owning_base(relative, root)
    return (base / relative) if base is not None else (root / relative)


def resolve_working_directory(root: Path, relative: str) -> Path:
    path = resolve_owned(root, relative)
    if not path.is_dir():
        raise Refusal("WORKING-DIRECTORY-MISSING", str(path))
    return path


def toolchain(relative: str) -> str:
    """A declared toolchain, resolved ONCE and named when it is absent.

    `shutil.which` is the check the original's `& dotnet --version *> $null` performed, except the
    original threw with a message that named the tool but not how to fix it, and reported a missing
    toolchain with the same exit code as a failed check.
    """
    found = resolve_executable(relative)
    if not found:
        raise Refusal("TOOLCHAIN-NOT-ON-PATH",
                      f"{relative} is not on PATH; install it, or run this check where it is")
    return found


def resolve_executable(token: str) -> str | None:
    """The PATH entry for a declared tool, honouring Windows' extension-less `.cmd` shims.

    `shutil.which` alone is NOT enough for the RUN, only for the preflight, and the gap between them is
    a silent refusal on the one platform this repo runs on. Measured on Windows:

        shutil.which("npm")            -> C:\\...\\npm.CMD      (the PREFLIGHT passes)
        subprocess.run(["npm", "test"]) -> FileNotFoundError     (the RUN cannot start)

    `npm`, `npx`, `yarn` and `pnpm` are all batch shims on Windows, and `CreateProcess` will not execute
    a `.cmd` without its extension in the argv[0] it is given. So the declared token is resolved ONCE,
    here, and the RESOLVED path is what runs. This is why the web wrapper could not be a plain
    `("npm", "test")` argv handed straight to `subprocess`.
    """
    if not token or "/" in token or "\\" in token:
        return token
    return shutil.which(token) or None


def render(command: tuple[str, ...]) -> str:
    """The command as one line, for a message and for a JSON envelope.

    Quoted the way a human would retype it, so the text a failure prints is the text an operator can
    paste. The parity test compares the same rendering, so both agree on one spelling.
    """
    parts = []
    for token in command:
        parts.append(f'"{token}"' if (" " in token or not token) else token)
    return " ".join(parts)


def run(command: tuple[str, ...], cwd: Path, timeout: int) -> dict:
    # argv[0] is resolved through PATH before the run, for the `.cmd` shim reason in
    # `resolve_executable`. The DECLARED token is what the messages and the parity test read; only the
    # spawned executable is the resolved path, so a report still says `npm test` rather than
    # `C:\nvm4w\nodejs\npm.CMD test`.
    executable = resolve_executable(command[0]) or command[0]
    try:
        proc = subprocess.run([executable, *command[1:]], capture_output=True, text=True,
                              timeout=timeout, cwd=str(cwd))
    except subprocess.TimeoutExpired as expired:
        return {"exit": EXIT_TIMED_OUT, "output": _as_text(expired.stdout) + _as_text(expired.stderr),
                "timed_out": True}
    except FileNotFoundError as exc:
        raise Refusal("COMMAND-NOT-FOUND", str(exc)) from exc
    except OSError as exc:
        raise Refusal("INVOCATION-FAILED", str(exc)) from exc
    return {"exit": proc.returncode, "output": (proc.stdout or "") + (proc.stderr or ""),
            "timed_out": False}


def _as_text(value) -> str:
    if value is None:
        return ""
    return value if isinstance(value, str) else value.decode("utf-8", errors="replace")


def execute(spec: dict, root: Path, timeout: int, command: tuple[str, ...] | None = None) -> dict:
    """Run one wrapper's declared command(s) and produce a verdict.

    `command` lets a wrapper with real logic of its own - one that must build a scratch directory and
    hand it to the command - substitute a token before the run. It is the ONLY extension point, and it
    exists because one of the fifteen genuinely does more than run a command. It is a plain argv,
    not a mapping: a mapping would have to invent a rule for repeated tokens, and none is needed.

    A SEQUENCE (spec["checks"]) runs each command in declaration order and stops at the first non-zero
    exit. The ordering is the point -- `web-fusion-rpg-web` runs the vitest suite and then the build
    because the build is the slower half, and a fast red should not cost a full build. Fail-fast is the
    ONLY defensible default here: the commands are independent, so running the rest after a red buys
    nothing and costs the operator the whole sequence's time on every failure.
    """
    # FOURTEEN OF SIXTEEN WRAPPERS WERE RUNNING NOTHING AND REPORTING SUCCESS. `spec_from` accepts a
    # wrapper that declares EITHER `CHECK` or `CHECKS`, and refuses one that declares neither - on the
    # stated ground that "a wrapper whose spec is incomplete would otherwise be a check that runs an
    # empty command and reports success". This line then read `spec["checks"]` alone, so for every
    # wrapper using the singular `CHECK` the sequence was empty, `steps` was `[]`, `step_count` was 0,
    # and the verdict was OK with exit 0.
    #
    # Measured on the population: 14 of 16 wrappers declared `CHECK` and executed nothing. The only two
    # that did real work were `web_fusion-rpg-web.py`, which declares `CHECKS`, and
    # `gen-content-validate.py`, which passes its command to `main` explicitly and so never relied on
    # the spec. Every generator gate in the wrappers - creature contract, metrics, preflight, report,
    # species, build plan, passive tree, items gate, structure contract, resource ownership, corpus
    # dump, item seed validator, family expand, fusion recipe - had been green without running once.
    #
    # This is the class the audit brief asks about: a check that cannot see what it checks. It is worse
    # than a red gate, because a red gate sends someone to look and a green one does not. And the
    # evidence these gates exist to produce - generators `--check` byte-identical to the import SHA,
    # goldens unchanged - was never produced at all.
    #
    # The refusal below is not reachable through `spec_from`, which already refuses a wrapper with
    # neither declaration. It exists because `execute` is also called with hand-built specs by tests,
    # and an empty sequence reached through one of those must not read as success either.
    # `spec["check"]` is ONE argv tuple and `spec["checks"]` is a sequence OF argv tuples, so the
    # singular branch has to be wrapped rather than spread. My first version of this read
    # `list(spec.get("check") or ())`, which turned `('python', '-m', 'seedsmith', 'structures',
    # 'contract', '--audit')` into six separate one-token commands; the run then tried to spawn bare
    # `python` with no arguments and refused COMMAND-NOT-FOUND. An intercepted `run()` call is what
    # showed `command: python` - six characters of argv standing in for a check.
    if command is not None:
        sequence = [tuple(command)]
    else:
        single = tuple(spec.get("check") or ())
        many = [tuple(step) for step in (spec.get("checks") or ())]
        sequence = many or ([single] if single else [])
    if not sequence:
        raise Refusal("WRAPPER-SPEC-INCOMPLETE",
                      "the wrapper declared no CHECK and no CHECKS, so there is nothing to run; "
                      "an empty sequence must never report a verdict")
    cwd = resolve_working_directory(root, spec.get("working_directory", "."))
    for relative in spec.get("required_paths", ()):
        if not resolve_owned(root, relative).exists():
            raise Refusal("REQUIRED-PATH-MISSING",
                          f"{relative} is missing; the check cannot run until it exists")
    for tool in spec.get("preflight", ()):
        toolchain(tool)

    steps: list[dict] = []
    budget = timeout
    started = time.monotonic()
    for index, entry in enumerate(sequence):
        remaining = max(1, int(budget - (time.monotonic() - started)))
        result = run(entry, cwd, remaining)
        steps.append({"command": render(entry), "exit": result["exit"],
                      "timed_out": result["timed_out"], "output": result["output"]})
        if result["timed_out"]:
            return {"verdict": "TIMED_OUT", "exit": EXIT_TIMED_OUT, "command": render(entry),
                    "working_directory": str(cwd), "output": result["output"], "timeout": timeout,
                    "steps": steps, "reason": f"no result within {remaining}s of the remaining budget"}
        if result["exit"] != 0:
            # The WRAPPER's exit is EXIT_FAILED, and the command's OWN code is carried as
            # `command_exit`. Propagating the command's code instead would let a command exiting 64 --
            # which is this module's REFUSED -- masquerade as a refusal the wrapper never made, so the
            # wrapper's exit vocabulary stays closed and the detail is in the envelope.
            return {"verdict": "FAILED", "exit": EXIT_FAILED, "command": render(entry),
                    "working_directory": str(cwd), "output": result["output"], "steps": steps,
                    "reason": spec.get("fail_hint", "the check reported a finding"),
                    "command_exit": result["exit"],
                    "failed_step": index + 1, "step_count": len(sequence)}
    return {"verdict": "OK", "exit": EXIT_OK, "command": render(sequence[-1]) if sequence else "",
            "working_directory": str(cwd), "output": "", "steps": steps,
            "step_count": len(sequence)}


def spec_from(module) -> dict:
    """Read a wrapper's machine-readable declaration.

    Refuses by NAME when a field is missing, rather than running something. A wrapper whose spec is
    incomplete would otherwise be a check that runs an empty command and reports success.

    A wrapper declares EITHER a single `CHECK` or an ordered `CHECKS` sequence. `CHECKS` exists because
    one wrapper genuinely runs two commands in a deliberate order (`web-fusion-rpg-web`: vitest, then
    the build, because the build is the slower half), and expressing that order by hand in each of two
    places is how it drifts. Exactly one of the two must be present -- a wrapper declaring both is
    ambiguous, and resolving the ambiguity silently would pick one at random.
    """
    spec: dict = {"check": (), "checks": (), "fail_hint": "", "working_directory": ".",
                  "preflight": (), "required_paths": (), "summary": ""}
    has_check = hasattr(module, "CHECK")
    has_checks = hasattr(module, "CHECKS")
    if has_check == has_checks:
        raise Refusal("WRAPPER-SPEC-AMBIGUOUS",
                      f"{module.__name__} must declare exactly one of CHECK or CHECKS, not "
                      f"{'both' if has_check else 'neither'}")
    if not hasattr(module, "FAIL_HINT"):
        raise Refusal("WRAPPER-SPEC-INCOMPLETE", f"{module.__name__} does not declare FAIL_HINT")

    def as_argv(value, what: str) -> tuple[str, ...]:
        if not isinstance(value, (tuple, list)) or not value:
            raise Refusal("WRAPPER-SPEC-INCOMPLETE",
                          f"{module.__name__}.{what} must be a non-empty tuple of argv tokens")
        return tuple(str(token) for token in value)

    if has_check:
        spec["check"] = as_argv(getattr(module, "CHECK"), "CHECK")
    else:
        steps = getattr(module, "CHECKS")
        if not isinstance(steps, (tuple, list)) or not steps:
            raise Refusal("WRAPPER-SPEC-INCOMPLETE",
                          f"{module.__name__}.CHECKS must be a non-empty sequence of argv tuples")
        spec["checks"] = tuple(as_argv(step, "CHECKS entry") for step in steps)
    spec["fail_hint"] = str(getattr(module, "FAIL_HINT"))
    spec["working_directory"] = str(getattr(module, "WORKING_DIRECTORY", "."))
    for field, default in (("PREFLIGHT", ()), ("REQUIRED_PATHS", ())):
        value = getattr(module, field, default)
        if not isinstance(value, (tuple, list)):
            raise Refusal("WRAPPER-SPEC-INCOMPLETE",
                          f"{module.__name__}.{field} must be a tuple")
        spec[field.lower()] = tuple(str(item) for item in value)
    spec["summary"] = str(getattr(module, "SUMMARY", ""))
    return spec


def primary_command(spec: dict) -> tuple[str, ...]:
    """The command a wrapper is ABOUT, for `--help`, a refusal message and the JSON envelope.

    For a sequence this is the FIRST command, because that is the one a reader needs in order to know
    what the check does at all; the full ordered list is in the `steps` of a result.
    """
    if spec.get("checks"):
        return spec["checks"][0]
    return spec["check"]


def main(spec: dict, argv: list[str] | None = None,
         command: tuple[str, ...] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=f"Wrapper around: {render(primary_command(spec))} (replaces a .ps1 wrapper).")
    parser.add_argument("--root", type=Path, default=None,
                        help="repo root (default: this script's parents)")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help=f"seconds for the WHOLE check (default {DEFAULT_TIMEOUT}); a sequence "
                             f"shares one budget and fails fast, because its commands are independent "
                             f"and a red first step should not cost the rest of the run")
    parser.add_argument("--json", action="store_true", help="emit the result as JSON")
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        print(f"{VERDICT_REFUSED} INVALID-TIMEOUT: --timeout must be positive", file=sys.stderr)
        return EXIT_REFUSED

    root = (args.root or repo_root()).resolve()
    try:
        result = execute(spec, root, args.timeout, command)
    except Refusal as refusal:
        print(f"{VERDICT_REFUSED} {refusal.reason}: {refusal.detail}", file=sys.stderr)
        if args.json:
            print(json.dumps({"verdict": "REFUSED", "reason": refusal.reason,
                              "detail": refusal.detail,
                              "command": render(primary_command(spec))}, indent=2))
        return EXIT_REFUSED

    if args.json:
        print(json.dumps(result, indent=2))
    elif result["verdict"] == "OK":
        steps = result.get("step_count", 1)
        print(f"{VERDICT_OK} — {render(primary_command(spec))}"
              + (f" (+{steps - 1} more step(s))" if steps > 1 else ""))
    elif result["verdict"] == "TIMED_OUT":
        print(VERDICT_TIMED_OUT, file=sys.stderr)
        print(f"  command: {result['command']}", file=sys.stderr)
    else:
        failed = result.get("failed_step")
        where = f" (step {failed} of {result['step_count']})" if failed else ""
        print(VERDICT_FAILED, file=sys.stderr)
        print(f"  {result['command']} exited {result['exit']} in {result['working_directory']}{where}",
              file=sys.stderr)
        print(f"  {result['reason']}", file=sys.stderr)
        skipped = result.get("step_count", 1) - (failed or 0)
        if skipped > 0:
            print(f"  {skipped} later step(s) not run (fail-fast: they are independent of this one)",
                  file=sys.stderr)
    return result["exit"]
