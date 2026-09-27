# gk-core

The engine: shared contracts, the RPG domain, persistence, the server, and the
balance numbers. This is the repo most gameplay work lands in.

- **Working rules:** [AGENTS.md](AGENTS.md)
- **Locked decisions:** `gk-workflow/docs/architecture/decisions.md`
- **Design gate:** `gk-workflow/docs/DESIGN-GATE.md`

## What belongs here

| Path | What it is |
|---|---|
| `src/FusionRpg.Contracts` | DTOs shared by every process. Changing one is a wire change. |
| `src/FusionRpg.Core` | The domain. Unity-free, and it must stay Unity-free. |
| `src/FusionRpg.Data` | The only place SQL exists. |
| `src/FusionRpg.Server` | REST + SignalR host. |
| `src/FusionRpg.CheatCore` | Cheat/plugin contracts. |
| `data/tuning` | Balance numbers, Server-loaded. Config, not code. |
| `tests/FusionRpg.Core.*` | Engine tests. |
| `scripts/guard-*.py` | Boundary guards. |

## What does NOT belong here

- **Web frontend** → `gk-web`
- **Game-mod / injector / launcher** → `gk-fusion`
- **Content and corpus data** → `gk-content`, `gk-data`
- **Asset pipeline** → `gk-assets`

## The two rules that bite hardest

1. **No Unity below the injector.** Core is a plain .NET library; a `UnityEngine`
   reference in Core is a defect, not a dependency.
2. **No magic numbers on the balance surface.** A number a balance pass would
   change lives in `data/tuning/<domain>.v{n}.json`, never as a `const`.

## Status

Empty. The migration tool (`kvsplit`, in `gk-workflow/tools/`) has not staged
into this repo yet — `apply` writes here, and it has not run.
