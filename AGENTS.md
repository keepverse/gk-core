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
- **No test in this repo may need gk-data.** It is private, so public CI cannot
  read it. A test that needs real content uses a fixture here, or belongs to the
  private content-integration job.
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

## Status

Empty; not staged yet.
