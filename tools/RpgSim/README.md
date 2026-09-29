# `gk-core/tools/RpgSim` — the RPG feature simulator

`rpg-simulator` (map: `docs/architecture/rpg-simulator-map.md`; plan: `tasks/rpg-simulator-plan.md`).
An agent- or CI-runnable instrument that drives a **scenario** — a readable list of steps that name
real HTTP routes — against the **real server**, and reads every verdict back through the same GET route
the web FE calls.

Runs the game closed. Claims no live game slot. Adds no server route.

## The rule

> **This tool contains no domain math.** — `gk-core/tools/CombatSim/README.md:8`, one layer up.

A number the API does not return is a route or a service call, never a copied formula. A scenario may
sequence and read; it may not compute.

## Use

```powershell
# validate a scenario against the format contract
dotnet run --project gk-core/tools/RpgSim -- --scenario gk-core/tests/fixtures/rpg-scenarios/<file>.json --validate

# drive it against a running sim server and write the verdict JSON
dotnet run --project gk-core/tools/RpgSim -- --scenario gk-core/tests/fixtures/rpg-scenarios/<file>.json `
    --run --base-url http://127.0.0.1:53111 --out out/verdict.json

# ...and compare two runs of the same scenario (the determinism falsifier; names the pointers that moved)
dotnet run --project gk-core/tools/RpgSim -- --scenario gk-core/tests/fixtures/rpg-scenarios/<file>.json `
    --run --base-url http://127.0.0.1:53111 --double-run

# or let the tool boot the real server itself (RS2.5's slow lane): a real FusionRpg.Server.exe on its own
# FUSIONRPG_DATA and a free loopback port, stopped again on exit with its data dir removed.
# Since CS-F3 (owner ruling 2026-09-23) a FRESH --data-dir also boots: the species tree ships beside the exe
# and the server self-heals the roster on its first boot (gk-core/src/FusionRpg.Server/Program.cs:683). Pass a real
# data dir when you want a specific roster; this tool never opens a store, so it does not provision one
# (use species-import).
dotnet run --project gk-core/tools/RpgSim -- --scenario gk-core/tests/fixtures/rpg-scenarios/<file>.json `
    --run --host process --data-dir <dir> --server-exe <path-to-FusionRpg.Server.exe> --out out/verdict.json

# ...and compare the run against the GOLDEN artifact -- its digest, the reading set and the exclusion fields
# (readback-verdict.md section 5). Refreshing is never the default: pass --update-golden to write it, and the
# refresh PRINTS the move it is blessing so the commit can quote it.
dotnet run --project gk-core/tools/RpgSim -- --scenario gk-core/tests/fixtures/rpg-scenarios/<file>.json `
    --run --base-url http://127.0.0.1:53111 --golden gk-core/tests/fixtures/rpg-scenarios/golden/<id>.verdict.json
```

Exit codes: `0` ok, `1` the run or the scenario failed, `2` usage, `3` refused (the target is not a sim
server, or a live injector is connected), `4` the double-run digests differ **or the golden moved** (or no
golden exists and `--update-golden` was not passed — a missing golden is reported, never assumed).

Two transports, one runner (owner ruling **E2 (a)**: one scenario file, two hosts):

| `--host` | What boots the server | Who runs it |
|---|---|---|
| *(absent, with `--base-url`)* | someone else already did | the CLI, against any running sim server |
| `process` | [`ProcessHost.cs`](ProcessHost.cs) — a real `FusionRpg.Server.exe`, `FUSIONRPG_SIM=1`, its own `FUSIONRPG_DATA`, a free loopback port, real HTTP and SignalR | the CLI (RS2.5) |
| `inproc` | `gk-core/tests/FusionRpg.E2E.Tests/RpgApiFactory.cs` | **refused by design, and that is the decision (RS-F6, closed 2026-09-23).** The in-process host is a run parameter of the EMBEDDING host, not of this CLI: `RpgApiFactory` boots the real `Program` and supplies the `HttpClient`, which is how the E2E tests run this same scenario file in-process (`RpgScenarioSlice0E2ETests`, `RpgSimInProcHostTests`). This CLI stays a real-process front end — `--host process` or `--base-url` — because a tool that referenced the app assembly plus `Microsoft.AspNetCore.Mvc.Testing` would carry the whole server to run what the embedding host already runs, and because no acceptance line needs it: the fast lane is the E2E project and the slow lane is `--host process` / `--base-url`. The map's module-4 row says the same. |

## Contracts

| Document | Module | Status |
|---|---|---|
| [`scenario-format.md`](scenario-format.md) | `scenario-format` | RS2.1 — the envelope, the closed op vocabulary (including the `clock.set` declaration a host applies through the clock seam), the read-back rule |
| [`readback-verdict.md`](readback-verdict.md) | `readback-verdict` | RS2.2 — the verdict, the digest and its written exclusion list, the double-run falsifier, and the golden artifact (§5) |
| [`ProcessHost.cs`](ProcessHost.cs) (beside its machine) | `process-host` | RS2.5 — the real-process lane, its boot flags and its teardown contract |
| [`GoldenVerdict.cs`](GoldenVerdict.cs) (beside its machine) | `readback-verdict` §5 | the golden comparison: the digest, the seed, the reading set and the exclusion fields — never the values |

The corpus lives at `gk-core/tests/fixtures/rpg-scenarios/**` (owner ruling **C1 (a)**), mapped to the `e2e`
verification boundary by `e2e-scenario-fixtures` in `gk-core/scripts/verification-boundaries.v1.json`, with its golden at
`gk-core/tests/fixtures/rpg-scenarios/golden/<id>.verdict.json`. The honesty guard (`scripts/guard-sim-fabrication.ps1`)
scans that tree and **skips the `golden/` subtree by name**, printing the count it skipped.

**Tests:** `gk-core/tests/FusionRpg.E2E.Tests/RpgSimFormatContractTests.cs` (the format),
`RpgSimVerdictContractTests.cs` (the verdict and digest), `RpgSimGoldenTests.cs` (the golden, with a falsifier
seen to fail), `RpgSimRunnerTests.cs` (the runner and its two refusals), `RpgScenarioSlice0E2ETests.cs` (the
corpus file, executed in-process), `RpgSimInProcHostTests.cs` (the in-process host),
`RpgSimProcessHostTests.cs` (the real-process host, both hosts on one file),
`RpgSimClockOffsetTests.cs` (a declared clock offset on both hosts). Only the parts a test project can
construct without a server are referenced — the same relationship `gk-core/tools/CombatSim` has with the test projects
that reference it (a count here would rot; the projects are the list).
