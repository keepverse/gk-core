using Xunit;

// aura-skill-todo.md Phase 5 / TC3 — found while running the verification sweep, 2026-08-30.
//
// THE FLAKE THIS FIXES. `AuraRuntimeEndpointsTests.Disable_anActiveAura_removesItAndReflectsInGet`
// failed once with `409 (Conflict)` on its own `enable` setup call, then passed 3/3 in isolation and
// 2/2 in full-suite re-runs — the signature of a cross-class race, not a broken test.
//
// ROOT CAUSE, verified in code rather than guessed at:
//   * `AuraRuntimeEndpoints` keeps session state in a BARE STATIC dictionary keyed by playerId, which
//     `AuraRuntimeEndpointsTests` itself documents ("without a reset a later test's 'player 1' would
//     inherit an earlier test's still-active aura") and defends against with a per-test
//     `AuraRuntimeEndpoints.ResetForTests()` in `InitializeAsync`.
//   * `AuraTuningHub.Configure(...)` is a second process-global set the same way.
//   * That defence only holds WITHIN one class. xUnit runs distinct test classes in PARALLEL by
//     default, and `CommanderListEndpointsTests` (added 2026-08-30) calls the same
//     `AuraRuntimeEndpoints.ResetForTests()` and `AuraTuningHub.Configure(...)`.
//   * Every test here builds a FRESH SQLite file, and a fresh file restarts its autoincrement — so
//     `GetCurrentPlayerId()` returns **1 in both classes**. The two therefore share one static key:
//     one class's reset wipes the other's enabled aura mid-test, or one's leaks into the other, and
//     the loser sees a spurious `AlreadyActive` 409.
//
// WHY THE ASSEMBLY-WIDE SWITCH RATHER THAN A SHARED [Collection]. A collection would pin exactly the
// two classes known to collide today and would silently stop covering the next class that touches one
// of these statics — and the hazard is structural to this assembly, not to this pair: process-global
// hubs plus a per-test SQLite file that always hands back player 1. Serialising the assembly removes
// the whole class of race in one place.
//
// THE COST, MEASURED AND NOT MINIMISED: the suite goes from ~6s to ~15-20s (80 tests, 5 consecutive
// green runs). That is a real ~3x on wall-clock, accepted deliberately — an intermittently red suite
// costs far more than 12 seconds, because the failure it hides next time may be a genuine one. If this
// ever becomes the slow step in CI, the right fix is to give these tests distinct player ids (or make
// the endpoint state non-static), not to re-enable parallelism over the same shared statics.
//
// This is a TEST-ISOLATION fix. It deliberately does not touch `AuraRuntimeEndpoints`' static-state
// design, which is that file's own (single-player, process-local) decision and is being actively
// edited elsewhere.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
