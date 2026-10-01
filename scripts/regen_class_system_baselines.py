#!/usr/bin/env python3
"""Regenerate the three checked-in class-system baselines every later phase diffs against.

Writes, into `--out-dir` (default `docs/research/class-system`):

  * `_baseline-residual.json`   -- CombatSim `predict --json` (closed form vs simulator, per arrow)
  * `_baseline-dominance.json`  -- CombatSim `trinity --json` (12x12 dominance matrix + coverage)
  * `_baseline-goldens.json`    -- the four `BattleGoldenTests.cs` hash consts, extracted not retyped

The live tuning config is the HIGHEST `data/tuning/aptitudes.v*.json` resolved NUMERICALLY, never a
pinned literal, so a run always measures the shipped config.

Replaces `scripts/regen-class-system-baselines.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE SCRATCH DIRECTORY LEAKED INTO THE OUTPUT TREE, AND INTO A TRACKED ONE.** The element-scratch
  copies were written under `$OutDir` and removed by a bare `Remove-Item ... -Recurse -Force` that sat
  AFTER the simulator call, not in a `finally`. A non-zero exit from `dotnet run` therefore left
  `_scratch-elements/` on disk -- and the default `$OutDir` is the TRACKED `docs/research/class-system`,
  so a failed run littered a committed directory. The scratch is now a private temporary directory that
  is never inside the output tree, so a failure cannot write there at all.

* **A TOOL THAT WAS NEVER BUILT SURFACED AS A C# STACK TRACE.** Every `dotnet run` is `--no-build`, so a
  fresh worktree or a Release-only CI build has no binaries in the chosen configuration, and the script
  reported `The system cannot find the file specified` from deep inside `dotnet` -- a message that names
  neither the configuration nor the tool. The port checks for the built output FIRST and refuses with
  `TOOL-NOT-BUILT`, naming the configuration and the path it looked for.

* **NO CALL WAS BOUNDED.** Three `dotnet run` invocations, no timeouts. These are simulators; a wedged
  one ran until something else killed it.

* **THE EXIT CODE WAS READ AFTER A PIPE.** Each call ended `| Out-Null` and `$LASTEXITCODE` was then
  tested. The port measures the exit code of the call itself.

* **A PS-5.1-VERSUS-7 HOST DIVERGENCE WAS INHERENT.** The original's own comment records that this
  script is invoked as plain `powershell` by the Guard.Tests determinism proof, and worked around
  `-AsHashtable` not existing on 5.1. It also emitted `Set-Content -Encoding UTF8`, which writes a BOM on
  5.1 and not on 7, so the same script produced different bytes per host. The port has one
  implementation and no host.

* **`ConvertTo-Json -Depth 10` TRUNCATES.** A document nested deeper than ten levels is silently
  serialized wrong. There is no depth limit here.

* **`Push-Location $Root` / `Pop-Location`** changed the working directory of the process, for every
  caller sharing it.

WHAT IS DELIBERATELY UNCHANGED
------------------------------
Same three tools, same arguments, same fixed seeds (predict 8888, trinity 20260826, theta 100), same
element assignment (FORCE=fire, FINESSE=air, BASTION=earth) applied to scratch copies and never to the
tracked `builds/*.json`, same `model` rewrite to a repo-relative forward-slash path, same `_meta`
contract (`measuredAt` + `conditions`) on all three files, the same `coverage.tuningSync` note, the same
dominanceMatrix/dominantCorners overlay from `FusionRpg.Core.DominanceGuard`, and the same four hash
consts extracted by regex from `BattleGoldenTests.cs`.

DECLARED DIFFERENCES, IN ADVANCE
--------------------------------
  * LINE ENDINGS. The PowerShell form emitted CRLF; this writes LF. `.gitattributes` carries
    `* text=auto eol=lf`, so git normalises CRLF to LF in the blob and the committed bytes are the same
    as the tracked files' -- which are LF.
  * SCRATCH LOCATION. The element scratch sits in a private temp directory rather than under the output
    directory. The emitted JSON embeds no path, so the outputs are byte-identical either way; this was
    verified, not assumed.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

# `keepverse_roots` lives in this repository's `scripts/lib`, which is not on sys.path when the script is
# run as a FILE — only when it is run as a module from this directory. That is why the import inside
# `tool_base` is guarded rather than top-level, and why the guard's own fallback (`return root`) is what
# a caller that has not set PYTHONPATH gets: a silent return to the pre-split behaviour instead of a named
# failure. The two-line sys.path insert below is the established idiom for exactly this, copied from
# gk-fusion/scripts/guard-single-writer.py:53-54, so a tool in this repository resolves a sibling the same
# way a tool in that one does.
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

TOOL_ID = "regen-class-system-baselines"

EXIT_PASSED = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_OUT = ("docs", "research", "class-system")
RESIDUAL_NAME = "_baseline-residual.json"
DOMINANCE_NAME = "_baseline-dominance.json"
GOLDENS_NAME = "_baseline-goldens.json"

COMBATSIM = ("tools", "CombatSim")
DOMINANCE_TOOL = ("tools", "DominanceBaseline")
ARCHETYPE_BUILDS = ("tools", "CombatSim", "builds")

PREDICT_SEED = 8888
TRINITY_SEED = 20260826
THETA = 100

# The element assignment P8.1 validated: the tracked builds/*.json are all `fire`, so elements can only
# be live if the archetypes differ. Applied to SCRATCH COPIES; the tracked files are never written.
ELEMENT_BY_ARCHETYPE = {"force": "fire", "finesse": "air", "bastion": "earth"}
ARCHETYPE_ORDER = ("force", "finesse", "bastion")

GOLDENS_TEST = ("tests", "FusionRpg.Core.Tests", "Battle", "BattleGoldenTests.cs")
RULESET_SOURCE = ("src", "FusionRpg.Core", "Battle", "BattleModels.cs")
HASH_CONSTS = ("StompHash", "CloseHash", "WipeHash", "SeedSweepHash")

DEFAULT_SIM_TIMEOUT = 3600
DEFAULT_TOOL_TIMEOUT = 900

REFUSAL_REASONS = {
    "OUT-OF-ROOT", "NO-TUNING-CONFIG", "TUNING-UNREADABLE", "DOTNET-NOT-ON-PATH", "TOOL-NOT-BUILT",
    "ARCHETYPE-BUILD-MISSING", "TOOL-FAILED", "TOOL-TIMED-OUT", "SCRATCH-REMOVAL-FAILED",
    "CORE-BASELINE-UNREADABLE", "BASELINE-UNREADABLE", "SOURCE-MISSING", "HASH-CONST-MISSING",
    "RULESET-VERSION-MISSING", "INVALID-TIMEOUT", "INVALID-CONFIGURATION", "BASELINE-NOT-WRITTEN",
}

# Bound once, module-private. `subprocess` and `shutil` are process-wide modules: a test that patches
# either reaches every other test in the project, which this program has measured at 506 unrelated
# failures in one case.
_RUN = subprocess.run
_WHICH = shutil.which
_RMTREE = shutil.rmtree

# The framework is a fixed `net8.0` in every one of these tools; the built output is located by probing
# for it rather than by guessing a TFM, so a TFM bump is not a hardcoded string to forget.
_TFM_GLOB = "net*"


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


@dataclass
class Report:
    out_dir: str = ""
    configuration: str = "Debug"
    live_tuning: str = ""
    live_tuning_rel: str = ""
    written: list[str] = field(default_factory=list)
    scratch_removed: bool | None = None
    tool_runs: list[dict] = field(default_factory=list)
    refused: tuple[str, str] | None = None

    @property
    def ok(self) -> bool:
        # All three files present, and no scratch directory of ours left behind.
        return (self.refused is None and len(self.written) == 3
                and all(Path(p).is_file() for p in self.written)
                and self.scratch_removed is not False)

    @property
    def reasons(self) -> list[str]:
        out: list[str] = []
        if self.refused is not None:
            out.append(f"{self.refused[0]}: {self.refused[1]}")
        missing = [Path(p).name for p in (self.written or []) if not Path(p).is_file()]
        if self.refused is None and len(self.written) != 3:
            out.append(f"only {len(self.written)} of 3 baselines were written")
        if missing:
            out.append(f"written but absent on disk: {', '.join(missing)}")
        if self.scratch_removed is False:
            out.append("a scratch directory of this tool's could not be removed")
        return out


# --------------------------------------------------------------------------------------------- config


def resolve_dotnet() -> str:
    found = _WHICH("dotnet")
    if not found:
        raise Refusal("DOTNET-NOT-ON-PATH", "dotnet is not on PATH")
    return found


def live_tuning(root: Path) -> Path:
    """The highest `aptitudes.v{N}.json`, sorted NUMERICALLY.

    A lexical sort puts v9 above v10, and this repository has a v10. The original recorded the same
    hazard in a comment and got it right; the port keeps it right and the suite pins it.
    """
    pattern = re.compile(r"^aptitudes\.v(\d+)\.json$")
    best: tuple[int, Path] | None = None
    for candidate in (root / "data" / "tuning").glob("aptitudes.v*.json"):
        match = pattern.match(candidate.name)
        if not match:
            continue
        version = int(match.group(1))
        if best is None or version > best[0]:
            best = (version, candidate)
    if best is None:
        raise Refusal("NO-TUNING-CONFIG",
                      f"no data/tuning/aptitudes.v*.json under {root}; the live shipped config is "
                      f"resolved, never pinned, so nothing can be measured without it")
    return best[1]


def tool_base(root: Path, tool: tuple[str, str]) -> Path:
    """The repository that carries `tool` — this one, or the sibling that took it.

    `tools/CombatSim` is gk-core's and `tools/DominanceBaseline` is gk-FORGE's, so a single `root` for
    both made the regen refuse with `TOOL-NOT-BUILT` against a tool that is built and present. The comment
    beside the call site already said "gk-forge/tools/DominanceBaseline" — the prose was right and the code
    under it was not, which is the ninth instance of that shape in this program.

    `owning_base` is the shared resolver's answer to "which repository carries this path", searching
    `root` first and then the fixed sibling set, and returning None when nothing does — so a tool that
    exists nowhere still falls back to `root` and fails with the original, more useful message rather
    than silently looking somewhere new. That is the decidable rule, not a special case on the name.
    """
    rel = "/".join(tool)
    try:
        from keepverse_roots import owning_base
    except ImportError:  # a clone without scripts/lib beside this file
        return root
    base = owning_base(rel, root)
    return Path(base) if base is not None else root


def require_built(root: Path, tool: tuple[str, str], configuration: str) -> None:
    """Refuse when the tool has no built output for this configuration, BEFORE running it.

    Every invocation is `--no-build`, so a configuration with no binaries produces
    `The system cannot find the file specified` from inside `dotnet` -- which names neither the tool
    nor the configuration. The original's own comment records hitting exactly that on a fresh worktree.
    """
    base = tool_base(root, tool)
    binaries = base.joinpath(*tool, "bin", configuration)
    if not any(binaries.glob(os.path.join(_TFM_GLOB, "*.dll"))):
        raise Refusal(
            "TOOL-NOT-BUILT",
            f"{'/'.join(tool)} has no build output under "
            f"{binaries.relative_to(base) if binaries.is_relative_to(base) else binaries} for "
            f"--owner {base}" if base != root else f"{'/'.join(tool)} has no build output under "
            f"{binaries.relative_to(base) if binaries.is_relative_to(base) else binaries} for "
            f"--configuration {configuration}. Every invocation is --no-build; build it first, or pass "
            f"the configuration that is already built.")


# -------------------------------------------------------------------------------------------- running


def run_tool(dotnet: str, root: Path, project: Path, configuration: str, args: list[str],
             timeout: int, label: str, report: Report) -> None:
    """One bounded `dotnet run --no-build`, with its exit code measured on the call itself."""
    command = [dotnet, "run", "--project", str(project), "-c", configuration,
               "--no-restore", "--no-build", *args]
    try:
        proc = _RUN(command, capture_output=True, text=True, timeout=timeout, cwd=str(root))
    except subprocess.TimeoutExpired as expired:
        report.tool_runs.append({"tool": label, "exit": None, "timedOut": True,
                                 "seconds": timeout})
        raise Refusal("TOOL-TIMED-OUT",
                      f"{label} did not finish within {timeout}s. It is a simulator over the shipped "
                      f"config; pass --sim-timeout if that is genuinely too short.") from expired
    report.tool_runs.append({"tool": label, "exit": proc.returncode, "timedOut": False,
                             "seconds": timeout})
    if proc.returncode != 0:
        tail = ((proc.stdout or "") + (proc.stderr or ""))[-1500:]
        raise Refusal("TOOL-FAILED", f"{label} exited {proc.returncode}:\n{tail}")


def write_element_scratch(root: Path, scratch: Path) -> list[str]:
    """Scratch copies of the three archetype builds, with only `element` changed.

    The tracked `builds/*.json` are all `fire`, so elements cannot be live without differing
    archetypes. The tracked files are NEVER written; these copies go to a private temp directory so a
    failed run cannot litter the output tree, tracked or not.
    """
    paths: list[str] = []
    for name in ARCHETYPE_ORDER:
        source = root.joinpath(*ARCHETYPE_BUILDS, f"{name}.json")
        if not source.is_file():
            raise Refusal("ARCHETYPE-BUILD-MISSING",
                          f"{source} is missing; the element mapping needs all three archetype builds")
        try:
            build = json.loads(source.read_text(encoding="utf-8-sig"))
        except json.JSONDecodeError as error:
            raise Refusal("ARCHETYPE-BUILD-MISSING", f"{source} is not readable JSON: {error}") from error
        build["element"] = ELEMENT_BY_ARCHETYPE[name]
        target = scratch / f"{name}.json"
        target.write_text(json.dumps(build, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                          newline="\n")
        paths.append(str(target))
    return paths


def read_json(path: Path, reason: str) -> dict:
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise Refusal(reason, f"{path} could not be read: {error}") from error
    if not isinstance(payload, dict):
        raise Refusal(reason, f"{path} is a {type(payload).__name__}, not an object")
    return payload


# A float token carrying an exponent at the END of a chunk, optionally preceded by the structural
# punctuation and indentation the encoder accumulated. Measured from `iterencode` itself, because there
# are THREE shapes and getting two of them is how a uniformity transform silently stops being uniform:
#     '6.919219087686557e-06'    a dict value's float, bare
#     '[\n    1.2345e-17'        the first LIST float, with `[` prepended
#     ',\n    9.87654321e+25'     a later list float, with the separator prepended
# A quote is deliberately absent from the allowed prefix and a string chunk ends with a closing quote,
# so neither a string nor a string ending in a number can ever match.
_FLOAT_WITH_EXPONENT = re.compile(r"^([\[\]\{\}():,\s]*)(-?\d+(?:\.\d+)?e[+-]?\d+)$")


def dumps_powershell_floats(payload: object) -> str:
    """`json.dumps(indent=2, ensure_ascii=False)` with the exponent letter uppercased.

    PowerShell's `ConvertTo-Json` writes `6.9E-06`; Python writes `6.9e-06`. Same double, different
    byte, in a file every later phase diffs against -- so the port matches the original's spelling.

    The rewrite runs on the encoder's own chunks, not on the finished text, so a number inside a STRING
    is unreachable. Skipped entirely unless the result re-parses to the same document.
    """
    encoder = json.JSONEncoder(indent=2, ensure_ascii=False)
    pieces: list[str] = []
    for chunk in encoder.iterencode(payload):
        matched = _FLOAT_WITH_EXPONENT.match(chunk)
        if matched:
            indent, number = matched.group(1), matched.group(2)
            pieces.append(indent + number.replace("e", "E"))
        else:
            pieces.append(chunk)
    text = "".join(pieces)
    if json.loads(text) != payload:
        raise Refusal("BASELINE-UNREADABLE",
                      "the float re-spelling did not round-trip; refusing to write a file whose value "
                      "the serialiser could not confirm")
    return text


def write_json(path: Path, payload: dict) -> None:
    """LF, two-space indent, raw non-ASCII, no BOM -- the shape of every tracked baseline at HEAD.

    `Set-Content -Encoding UTF8` emitted a BOM under Windows PowerShell 5.1 and not under pwsh 7, so the
    same script produced different bytes per host. There is no host here.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(dumps_powershell_floats(payload) + "\n", encoding="utf-8", newline="\n")


def measured_at() -> str:
    """`(Get-Date).ToUniversalTime().ToString("o")`: seven fractional digits and a `Z`."""
    now = datetime.now(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond:06d}0Z"


def add_meta(doc: dict, root: Path, conditions: str) -> dict:
    """Attach `_meta`, and make `model` repo-relative.

    CombatSim and DominanceBaseline emit `model` as the ABSOLUTE path they were handed, which bakes this
    machine's repo root into a committed file and makes the baselines diff noisily between contributors.
    Only the PRESENCE of `model` is asserted anywhere, never its value.
    """
    if doc.get("model"):
        relative = str(doc["model"]).replace(str(root), "", 1).lstrip("\\/")
        doc["model"] = relative.replace("\\", "/")
    doc["_meta"] = {"measuredAt": measured_at(), "conditions": conditions}
    return doc


# --------------------------------------------------------------------------- the three conditions

def residual_conditions(live_rel: str) -> str:
    return (
        f"{live_rel} (live shipped config -- P8.2 stamina bind + P8.3 mitigation dial, and this tool's "
        "own resolver was ported 2026-08-27 to apply that dial the same way Core's "
        "AptitudeResolver.EffectiveKMilli does). FORCE=fire/FINESSE=air/BASTION=earth (scratch copies "
        "of tools/CombatSim/builds/*.json with only element changed, never overwriting the tracked "
        f"fire/fire/fire files). Theta={THETA}, seed {PREDICT_SEED}, 3000 trials/arrow. Elements "
        "genuinely live: closed-form and simulated win shares both move with the matchup (P8.1's own "
        "headline finding, e.g. FORCE v FINESSE closed-form ~98% vs simulated ~68%), not the "
        "~100%/~0% same-element result the tracked builds alone would have produced.")


def tuning_sync_note(live_rel: str) -> str:
    return (
        "dominanceMatrix/dominantCorners updated 2026-08-27 (Checkpoint 8) -- measured via "
        "tools/DominanceBaseline (FusionRpg.Core.DominanceGuard/TerminationGuard, the SAME production "
        "resolver TerminationGuard.Assert uses), which reads the LIVE "
        f"{live_rel} config automatically, no internal copy. Independently corroborated by "
        "DominanceGuardTests.cs's own "
        "Measure_theRealTwelveCornerShape_matchesTheCheckedInBaselinesEmptyDominantCorners and "
        "docs/research/class-residual-2026-08-27.md's P8.5 section (same headline finding: no absolute "
        "dominant corner, Retribution near-dominant at 10 of 11, loses only to Pierce). "
        # `chains` NOT `\u0060chains\u0060`: PowerShell drops the backtick from an unrecognised escape, so
        # the original committed this with no code marks. Reproduced byte-for-byte -- see the module
        # docstring; these strings are content a committed baseline diffs against.
        f"chains above is ALSO fresh, closing the one remaining gap: trinity now runs with --models "
        f"pointed at this same live {live_rel} file (never tools/CombatSim's own internal v1 POC copy), "
        "and that tool's own resolver (AptitudeTuning.ToModel) was ported the same day to apply the "
        "AptitudeMitigation dial the way Core's AptitudeResolver.EffectiveKMilli does "
        "(ResolverMatchesSimulatorTests.cs, P3.4) -- so this is a best-response CHASE "
        "(BestResponse.Chase, a search DominanceGuard has no equivalent for and so cannot reproduce the "
        # The space in `dominanceMatrix/ dominantCorners` is a line-wrap artifact of the original's
        # string concatenation and is reproduced exactly.
        "way dominanceMatrix/ dominantCorners are overlaid above) against the real shipped config, not a "
        'stale internal copy. coverage.elementAxis stays "neutral" deliberately, not as a residual gap: '
        "dominanceMatrix/dominantCorners/chains all spike individual APTITUDES, and aptitudes feed "
        "combat.power.omni only by design (class-system-ideal.md 4.1 rule 2 -- \"an aptitude reaches a "
        "MECHANISM, never a FLAVOUR\"; elements are a flavour axis, so any aptitude-to-element mapping "
        "would be arbitrary, which is exactly the rule this file's OWN elements-live measurement lives "
        "in _baseline-residual.json instead, at the ARCHETYPE/posture level, where FORCE/FINESSE/BASTION "
        "do carry a real element).")


def dominance_conditions(live_rel: str) -> str:
    return (
        f"dominanceMatrix/dominantCorners: {live_rel} (live, via FusionRpg.Core.DominanceGuard -- "
        "P8.2/P8.3's stamina bind + mitigation dial). chains: ALSO "
        f"{live_rel} now (live, via tools/CombatSim trinity --models, that tool's own resolver ported "
        "2026-08-27 to apply the mitigation dial too). All twelve spiked corners, "
        f"Theta={THETA}, seed {TRINITY_SEED} — elementAxis neutral BY DESIGN (aptitudes are "
        "element-blind, class-system-ideal.md 4.1 rule 2 -- not a gap; see coverage.tuningSync), action "
        "economy off.")


GOLDENS_CONDITIONS = (
    "Extracted from tests/FusionRpg.Core.Tests/Battle/BattleGoldenTests.cs's four const string Hash "
    "declarations — never hand-retyped, so this file cannot itself drift from the test.")


# ------------------------------------------------------------------------------------- the goldens


def extract_goldens(root: Path) -> dict:
    """The four hash consts and the live RulesetVersion, EXTRACTED from the C# -- never retyped.

    The RulesetVersion comes from the const itself, not from a comment: `BattleGoldenTests.cs`
    accumulates one prose paragraph per historical re-bless, and a comment search would match the
    earliest one.
    """
    test_path = root.joinpath(*GOLDENS_TEST)
    if not test_path.is_file():
        raise Refusal("SOURCE-MISSING", f"missing {test_path}")
    test_text = test_path.read_text(encoding="utf-8-sig")

    ruleset_path = root.joinpath(*RULESET_SOURCE)
    if not ruleset_path.is_file():
        raise Refusal("SOURCE-MISSING", f"missing {ruleset_path}")
    ruleset = re.search(r"public const int RulesetVersion\s*=\s*(\d+)",
                        ruleset_path.read_text(encoding="utf-8-sig"))
    if not ruleset:
        raise Refusal("RULESET-VERSION-MISSING",
                      f"could not find BattleRuleset.RulesetVersion in {ruleset_path}")

    goldens: dict = {"rulesetVersion": int(ruleset.group(1))}
    for name in HASH_CONSTS:
        found = re.search(rf"const string {name}\s*=\s*\"([0-9A-Fa-f]+)\"", test_text)
        if not found:
            raise Refusal("HASH-CONST-MISSING",
                          f"could not find const string {name} in {test_path}. The value is extracted, "
                          f"never retyped, so a rename or a removal is named here rather than "
                          f"silently producing a stale baseline.")
        goldens[{"StompHash": "stompHash", "CloseHash": "closeHash", "WipeHash": "wipeHash",
                 "SeedSweepHash": "seedSweepHash"}[name]] = found.group(1)
    return goldens


# --------------------------------------------------------------------------------------------- main


def execute(root: Path, out_dir: Path, configuration: str, sim_timeout: int, tool_timeout: int,
            report: Report) -> Report:
    live = live_tuning(root)
    live_rel = f"data/tuning/{live.name}"
    report.live_tuning, report.live_tuning_rel = str(live), live_rel
    report.out_dir, report.configuration = str(out_dir), configuration

    require_built(root, COMBATSIM, configuration)
    require_built(root, DOMINANCE_TOOL, configuration)
    dotnet = resolve_dotnet()
    out_dir.mkdir(parents=True, exist_ok=True)

    # Each tool resolves against the repository that carries it. CombatSim is gk-core's;
    # DominanceBaseline is gk-forge's, and joining both onto one `root` is what made `dotnet run` fail
    # with "The provided file path does not exist" for a tool that is present and built.
    combatsim = tool_base(root, COMBATSIM).joinpath(*COMBATSIM)
    dominance_tool = tool_base(root, DOMINANCE_TOOL).joinpath(*DOMINANCE_TOOL)

    # A private scratch tree, OUTSIDE the output directory. The original wrote its element scratch under
    # $OutDir -- the TRACKED docs/research/class-system by default -- and removed it with a bare
    # `Remove-Item` placed AFTER the simulator call, so a non-zero exit left scratch in a committed tree.
    scratch = Path(tempfile.mkdtemp(prefix="regen-class-system-"))
    try:
        scratch_paths = write_element_scratch(root, scratch)
        report.scratch_removed = True

        # --- 1. _baseline-residual.json : CombatSim predict
        residual_path = out_dir / RESIDUAL_NAME
        run_tool(dotnet, root, combatsim, configuration,
                 ["predict", "--json", "--out", str(residual_path), "--models", str(live),
                  "--archetypes", ",".join(scratch_paths), "--theta", str(THETA),
                  "--seed", str(PREDICT_SEED)],
                 sim_timeout, "CombatSim predict", report)
        write_json(residual_path,
                   add_meta(read_json(residual_path, "BASELINE-UNREADABLE"), root,
                            residual_conditions(live_rel)))
        report.written.append(str(residual_path))

        # --- 2. _baseline-dominance.json : CombatSim trinity
        dominance_path = out_dir / DOMINANCE_NAME
        run_tool(dotnet, root, combatsim, configuration,
                 ["trinity", "--json", "--out", str(dominance_path), "--models", str(live),
                  "--seed", str(TRINITY_SEED)],
                 sim_timeout, "CombatSim trinity", report)
        dominance = read_json(dominance_path, "BASELINE-UNREADABLE")

        # dominanceMatrix/dominantCorners are ALSO closed-form-only, so gk-forge/tools/DominanceBaseline
        # reproduces them via the SAME production resolver rather than a second implementation that could
        # drift from it. `chains` is a best-response CHASE DominanceGuard has no equivalent search for,
        # so it stays trinity's own.
        core_scratch = scratch / "_dominance-core-scratch.json"
        run_tool(dotnet, tool_base(root, DOMINANCE_TOOL), dominance_tool, configuration,
                 ["--theta", str(THETA), "--out", str(core_scratch)],
                 tool_timeout, "DominanceBaseline", report)
        core = read_json(core_scratch, "CORE-BASELINE-UNREADABLE")
        for key in ("dominanceMatrix", "dominantCorners"):
            if key not in core:
                raise Refusal("CORE-BASELINE-UNREADABLE",
                              f"tools/DominanceBaseline emitted no {key!r}; it is the production "
                              f"resolver's own output and its absence is a tool defect, not a gap to "
                              f"carry into a committed baseline")
        dominance["dominanceMatrix"] = core["dominanceMatrix"]
        dominance["dominantCorners"] = core["dominantCorners"]
        coverage = dominance.get("coverage")
        if not isinstance(coverage, dict):
            raise Refusal("BASELINE-UNREADABLE",
                          f"{dominance_path} has no `coverage` object to record tuningSync on; the note "
                          f"is what says these numbers came from the live config")
        coverage["tuningSync"] = tuning_sync_note(live_rel)
        write_json(dominance_path, add_meta(dominance, root, dominance_conditions(live_rel)))
        report.written.append(str(dominance_path))

        # --- 3. _baseline-goldens.json : the C# consts, extracted
        goldens_path = out_dir / GOLDENS_NAME
        write_json(goldens_path, add_meta(extract_goldens(root), root, GOLDENS_CONDITIONS))
        report.written.append(str(goldens_path))
    finally:
        # REPORTED, never swallowed. The original's `Remove-Item` sat outside any finally, so a failed
        # run left the scratch behind; and a scratch that survives is a real cost, not a line to skip.
        try:
            _RMTREE(scratch)
            report.scratch_removed = scratch.exists() is False
        except OSError:
            report.scratch_removed = not scratch.exists()

    for path in report.written:
        if not Path(path).is_file():
            raise Refusal("BASELINE-NOT-WRITTEN", f"{path} was reported written but is not on disk")
    return report


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="regen-class-system-baselines",
        description="Regenerate the three checked-in class-system baselines (replaces "
                    "regen-class-system-baselines.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--out-dir", default="",
                        help=f"where the three baselines are written (default: {'/'.join(DEFAULT_OUT)})")
    parser.add_argument("--configuration", default="Debug", choices=("Debug", "Release"),
                        help="configuration of the ALREADY-BUILT tools; every run is --no-build "
                             "(default: Debug)")
    parser.add_argument("--sim-timeout", type=int, default=DEFAULT_SIM_TIMEOUT,
                        help=f"seconds for each CombatSim run (default {DEFAULT_SIM_TIMEOUT})")
    parser.add_argument("--tool-timeout", type=int, default=DEFAULT_TOOL_TIMEOUT,
                        help=f"seconds for the DominanceBaseline run (default {DEFAULT_TOOL_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def render(report: Report, as_json: bool) -> None:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK" if report.ok else "FAILED",
                          "exitCode": EXIT_PASSED if report.ok else EXIT_FAILED,
                          "outDir": report.out_dir, "configuration": report.configuration,
                          "liveTuning": report.live_tuning_rel,
                          "written": report.written, "toolRuns": report.tool_runs,
                          "scratchRemoved": report.scratch_removed,
                          "reasons": report.reasons}, indent=2))
        return
    print(f"==> live tuning config: {report.live_tuning_rel}")
    for run in report.tool_runs:
        state = "timed out" if run["timedOut"] else f"exit {run['exit']}"
        print(f"==> {run['tool']}: {state}")
    print("==> Wrote:")
    for path in report.written:
        print(f"      {path}")
    if not report.ok:
        print("FAILED")
        for reason in report.reasons:
            print(f"  {reason}")


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for name, value in (("--sim-timeout", args.sim_timeout), ("--tool-timeout", args.tool_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    out_dir = Path(args.out_dir).expanduser() if args.out_dir else root.joinpath(*DEFAULT_OUT)
    out_dir = out_dir if out_dir.is_absolute() else (Path.cwd() / out_dir)
    out_dir = out_dir.resolve()
    report = Report(out_dir=str(out_dir), configuration=args.configuration)
    try:
        if not root.is_dir():
            raise Refusal("OUT-OF-ROOT", f"--root {root} is not a directory")
        execute(root, out_dir, args.configuration, args.sim_timeout, args.tool_timeout, report)
    except Refusal as refusal:
        report.refused = (refusal.reason, refusal.detail)
    render(report, args.json)
    return EXIT_PASSED if report.ok else (EXIT_REFUSED if report.refused else EXIT_FAILED)


def _refuse(reason: str, detail: str, as_json: bool) -> int:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                          "exitCode": EXIT_REFUSED}, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())
