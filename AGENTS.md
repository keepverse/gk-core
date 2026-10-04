# gk-core — agent guide

The engine: Contracts, Core, Data, CheatCore, Server, and `data/tuning`.
**Public.**

The binding rules for every Keepverse repository are in the workspace root:
`../AGENTS.md` (loaded automatically for any agent working inside this folder).
Docs live in `../docs/`. This file is emitted by kvsplit; change its template in
`tools/kvsplit/rules/templates/`, not here.

## Rules specific to this repo

- **Never compile against gk-forge or gk-fusion.** Content comes from gk-data at
  runtime only, through a content root and a pack name.
  *Enforced, with one recorded exception.* `Directory.Build.props` publishes
  `GkForgeAvailable`, and every cross-repository `ProjectReference` is conditioned
  on it, so a standalone clone evaluates. `tests/FusionRpg.Core.Tests` still names
  one — `CorpusDumpTests.cs` is the only file in the test tree that compiles
  against a gk-forge namespace — and both halves of it are gated by the same
  condition. **Closing it properly means MOVING `CorpusReader` + `DumpWriter` from
  gk-forge's `tools/CreatureCorpusDump` into gk-core**, which is a two-repository
  edit: gk-forge must drop the two files and keep `Program.cs`. Copying instead
  of moving would leave two copies of the renderer, and the test would then
  validate a copy production never runs.
- **No test in this repo may need gk-data.** It is private, so public CI cannot
  read it. A test that needs real content uses a fixture here, or belongs to the
  private content-integration job.
  *Honest about what this now costs.* "Needs gk-data" is now a **named refusal at
  the test that needs it**, not a green skip and not an assembly-wide failure.
  `ContractTuningTestBootstrap`'s `[ModuleInitializer]` used to call
  `KeepverseRoots.Content()` eagerly four times, so one `DirectoryNotFoundException`
  became a `TypeInitializationException` and every test in the assembly failed for
  a reason naming neither gk-data nor the pack. Measured in a clone at 638072b:
  12,435 of 12,556 failures carried that one line. The throw is kept; only the
  discovery moved.
- **No game-host reference anywhere** — no Unity, Il2Cpp, Harmony, BepInEx or
  MelonLoader. Core is a plain .NET library and a guard enforces it.
- **SQL only inside `FusionRpg.Data`.** A guard enforces it.
- **No Unity below the injector**, and no ad-hoc Unity stat writes: combat writes
  go through the single writer and the effect Funnel.
- **No magic numbers on the balance surface.** A number a balance pass would
  change lives in `data/tuning/<domain>.v{n}.json`, not as a `const`.
- **One power ladder.** Any level-derived number goes through the power SSOT; a
  private `f(level)` is the defect it exists to prevent.

## Build and test

```powershell
dotnet build FusionRpg.slnx
dotnet test tests/FusionRpg.Core.Tests
```

Web is **not** in this repo — it is `gk-web`. Content is not here either:
`gk-content` and `gk-data`.

## Standalone: what this repository can and cannot do alone

**`dotnet build FusionRpg.slnx` succeeds in a clone with every sibling absent.**
Verified in an isolated clone at `4e09469`. Before `638072b` it exited 1 on a
single `CS0234`, because seven `ProjectReference`s pointed into gk-forge — and
every one of those seven gk-forge projects references gk-core back, so each edge
was a project-level cycle.

A clone resolves **its own** root: `KeepverseRoots.Detect` recognises a layout that
is `src/` + `data/tuning/` beside `FusionRpg.slnx`, so `Core()` answers. The
accessors that cannot answer here — `Content()`, `Fusion()`, `Forge()`, `Web()` —
still **refuse by name**, and that refusal is the safety, not an inconvenience.

What a clone cannot do, and what it says instead:

| Tool | In a clone |
|---|---|
| `dotnet build` | works, exit 0 |
| `verify-change.py` | works; mapped and unmapped paths are **distinguishable** |
| `guard-verification-boundaries.py` | exits 0 with one NOTE naming every unexamined entry and its owner |
| `program_status.py`, `audit-program-pipeline.py`, `split-decisions.py`, `split-design-gate.py` | named refusal, exit 2 — never a traceback |
| `guard-generated-seed.py` | refuses `GENERATED-TREES-ABSENT`, exit 1 — its subject is gk-data's and gk-forge's |
| `guard-population-pin.py` | exit 1 with P3 findings for owners it cannot resolve |
| `run_guards.py --tier ci` | refuses at the catalog stage: 8 registry guards live in gk-fusion, gk-forge and the workspace root |

**A guard whose declared scan root is absent must REFUSE, not pass.** That is now
enforced in `guard-generated-seed.py` and was already true of
`guard-population-pin.py`; `run_guards.py`'s table used to discard the stderr that
told you, and now shows it for a green row too — labelled as a clean verdict that
also declined to answer.

## Status

Empty; not staged yet.
