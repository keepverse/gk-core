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

Staged and populated. The Keepverse split is applied here; this repository is
the engine, not an empty landing place.

Measured on this tree (`git ls-files`, so these are tracked files and not
build output):

| | |
|---|---|
| Tracked files | 3,892 |
| Project files (`.csproj`) | 100 |
| Test projects (`tests/*.Tests`) | 76 |
| C# files under `src/` | 1,377 |
| Tuning tables under `data/tuning` | 202 |

Re-measure rather than trusting the table; it is a reading, not a constant.

### Building standalone

This repository builds and tests with every sibling repository absent:

```powershell
dotnet build FusionRpg.slnx
dotnet test tests/FusionRpg.Core.Tests
```

Verified in an isolated clone with no sibling checked out: `dotnet build
FusionRpg.slnx` exits 0 with 0 errors.

All seven `ProjectReference`s that point at `$(GkForgeRoot)` — across four test
projects — carry `Condition="'$(GkForgeAvailable)' == 'true'"`, published once in
`Directory.Build.props` from `Exists($(GkForgeRoot))`. So a clone never builds a
tool it cannot run, and `-p:GkForgeRoot=` still outranks the default, so no
machine path is committed. See that file's comment for what the residual
dependency costs and which one project genuinely compiles against gk-forge's
code.

### What is NOT here

The generated corpus is **not** in this repository. `data/seed/**` and
`data/generated/**` live in `gk-data/packs/fusion`, and `tools/seedsmith`,
`tools/FamilyExpandGen` and `tools/ItemSeedValidator` live in `gk-forge`.

That is why every row in `scripts/enforcement-registry.v1.json` now carries a
`repository` field naming the repository whose root that guard inspects, and
why `scripts/guard-registry-wiring.py` refuses a row naming a repository that is
absent, a script that does not exist, or a subject that resolves to nothing. A
guard that cannot see its subject reports a verdict about nothing, and a green
row reached that way is worse than a red one.

Run the gates from a workspace where the siblings are checked out beside this
repository, or point the matching `KEEPVERSE_*_ROOT` overrides at them.
