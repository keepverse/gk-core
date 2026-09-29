# The scenario contract — `scenario-format` (rpg-simulator RS2.1)

**Module id:** `scenario-format` (capability map `docs/architecture/rpg-simulator-map.md`, module 2), module 3.
**Machine:** `ScenarioFormat.cs` (the envelope and its steps), `ScenarioVocabulary.cs` (the closed op
table), `ScenarioValidator.cs` (every refusal), `JsonPointer.cs` (the selection grammar),
`CanonicalJson.cs` (the one writing of a value).
**Tests:** `gk-core/tests/FusionRpg.E2E.Tests/RpgSimFormatContractTests.cs`.
**Corpus home:** `gk-core/tests/fixtures/rpg-scenarios/**` (owner ruling **C1 (a)**), mapped to the `e2e`
verification boundary as `e2e-scenario-fixtures`
(`gk-core/scripts/verification-boundaries.v1.json`, landed `80f388db7`).

> **Where this spec lives, and why.** The map promises
> `docs/architecture/rpg-simulator/spec-scenario-format.md`. That tree is outside this lane's fence, so
> the promise is met here instead — beside the machine that enforces it, following
> `gk-core/tools/CombatSim/README.md`. The map's spec path is owed an erratum by whoever owns
> `docs/architecture/**`; it is filed as a finding in `tasks/rpg-simulator-todo.md`.

---

## 1. The one sentence the whole format obeys

> **A scenario may sequence and read. It may not compute.**

Every rule below is that sentence made mechanical. A step names a route; a read-back names the route
the web FE reads the same value through; an expectation compares one reading to another reading or to
a literal; a pointer *selects* a value and never derives one. There is no arithmetic, no string
interpolation beyond a literal template naming a captured value, and no conditional in the format. If a
step needs a number no route returns, the fix is a route, never a copy of the formula
(`docs/architecture/rpg-simulator-idea.md` §6.2, `gk-core/tools/CombatSim/README.md:8` one layer up).

## 2. The envelope

| Field | Required | What it is |
|---|---|---|
| `id` | yes | The scenario's stable name. The verdict repeats it. |
| `title`, `description` | `description` yes | `description` says what the scenario is for. A scenario with no stated purpose is refused. |
| `program`, `task` | no | Which program and row authored it — provenance for a corpus reader. |
| `seed` | yes, non-zero | The run's seed. A seed nobody chose is refused (`gk-core/tools/SquadHarness/Program.cs:35-39`, the same rule one program over). Every synthesized value derives from it, so a rerun is the same rerun. |
| `clock` | yes | `{ mode, note }` (+ `offsetSeconds` for `offset`). `mode` is `ambient` \| `offset` \| `explicit`. **`ambient`** means the machine clock; **`offset`** declares a signed `offsetSeconds` the HOST applies at boot through the ONE seam (`FusionRpg.Core.Time.ServerClock`, RS3) — the in-process factory configures it from `FUSIONRPG_CLOCK_OFFSET` and `ProcessHost` passes that variable to the child, so the declaration is honest for both approved hosts. **`explicit`** (an absolute instant) is refused by name: the seam's product shape is an offset, and a route that sets a clock is deliberately not specified (`rpg-simulator-spec-clock-seam.md` §2, owner ruling D3 (b)). An `offsetSeconds` on an `ambient` run is refused too — a declaration that does not match the run would lie. `note` is required: an undeclared clock makes every timestamp reading meaningless. The verdict prints the declaration, offset included. |
| `rules`, `notes` | no | Prose that is load-bearing: a reviewer and the RS4 honesty guard read them against the steps. A scenario that uses a fixture surface (`/api/test/*`), a known bypass, or knows a reachability gap **must** say so in `notes`. |
| `steps` | yes | The ordered list. One sequential client runs it in order; there is no concurrency in a scenario, because the digest depends on total order. |

**`host` is deliberately not an envelope field.** The plan's RS2.1 acceptance lists it, and this is the
one place this document reads the plan differently: owner ruling **E2 (a)** is *one scenario file, two
hosts*, and a file that declared its host would be two files the moment the slow lane ran. The host is
a **run parameter** (`--host inproc|process`, RS2.4/RS2.5) and the verdict records which one ran, so a
verdict is still self-describing. Making it an envelope field would be the "two harnesses" failure the
map's decision 4 exists to avoid.

## 3. The closed op vocabulary

An op is named after the route it calls. Its first segment is its **surface**, and the surface is
*derived from the declared route and checked against it*:

| Prefix | Kind | The route it may name |
|---|---|---|
| `sim.*` | call | a `/api/sim/*` route — the feed |
| `test.*` | call | a `/api/test/*` route — the fixture/seeding surface |
| `api.*` | call | any other player-facing `/api/*` route |
| `read.*` | read-back | an FE-facing `GET /api/*` route (outside `/api/test` and `/api/sim`), or a `/hub/*` message |
| `expect.*` | assertion | no route; asserts over a declared reading |
| `digest` | marker | no route; names the readings that enter the hash |

The surface list is **closed** and the call table is closed with it (`ScenarioVocabulary.Calls`): an op
that is not in the table is refused, and a step whose declared `route` is not that op's route is
refused. That is RS1's property — *a scenario step cannot drift from the runner that reads it* —
with one copy of the table instead of one per reader.

**Where the plan's four prefixes were not enough.** The plan names `sim.*` / `api.*` / `read.*` /
`expect.*` / `digest` and describes `sim.*` as "a call to an existing `/api/sim/*` route". The shipped
slice-0 scenario has a step that is neither: `POST /api/test/seed-souls-demo` (its companion
`POST /api/test/expedition-due` is gone — RS3 increment 5b retired the timer rewind in favour of a
`clock.set` declaration). Calling it `sim.*` would fold the fixture surface into the input
feed and lose the distinction the RS4 honesty guard needs (the `seed-*-demo` writers and the reset
route are the allowlist *because* they write rows directly —
`docs/architecture/rpg-simulator-map.md`, "The test group"). So `test.*` is its own surface: a scenario
may call it, must say why in `notes`, and the guard can name every step that touches it.

**A sixth shape, `clock.set`.** RS3 increment 5b added it: a DECLARATION that the host must believe the
machine clock is `offsetSeconds` ahead from this point on. It names no route — a route that sets a clock is
deliberately not specified (`rpg-simulator-spec-clock-seam.md` §2) — and the runner hands the value to the
host's clock control: the in-process host applies it to the seam in place, and the real process is stopped
and rebooted on the SAME data dir and port with a new `FUSIONRPG_CLOCK_OFFSET`. A host that cannot be told
(the CLI's `--base-url` lane, or any host with no control) REFUSES the run by name.

**Route templates.** `{playerId}` and `{id}` resolve from values captured earlier in the same run.
A template placeholder with no captured value is a run failure, not an empty string.

## 4. Reads, expectations and the digest

- **A `read.*` step is the read-back.** Its payload is registered under the op's name
  (`read.souls` → reading `read.souls`) and it carries the `source` the verdict prints. The route must
  be FE-facing: a **GET under `/api/` outside `/api/test` and `/api/sim`**, or a `/hub/*` message. This
  is `docs/contributing/live-probe-standard.md` §3 as a validator rule: `/api/test/snapshot` — the
  debug-only accessor the standard names as the anti-pattern — is refused, and so is the sim feed, which
  has no result to read. A POST body is never a read-back (it is the write's own echo, not persisted
  state).
- **`capture` is sequencing, not evidence.** `{name, path}` pulls a value out of a step's response so a
  later step can address it. A capture used in an `expect.*` carries the source of the step it came
  from, and the rule is that an assertion over a capture is an assertion over *that route's read*, never
  over a value the runner invented.
- **`expect.*` is the assertion vocabulary**, closed at nine checks (`equals`, `notEmpty`, `atLeast`,
  `nonZero`, `memberOf`, `equalSet`, `allIn`, `contains`, `allStartWith`). Two rules make this
  non-computational and non-fabricating:
  1. An order-insensitive or set comparison (`equalSet`, `allIn`) takes its other side from
     **another reading or a captured value** (`other`), never a constant. Comparing a read-back to a
     read-back is what makes "the squad the expedition fought with is the roster this run built" a
     *measurement*; comparing it to a constant would let a scenario assert what it hopes.
  2. A check that needs a literal takes it as `value`. There is no expression form.
- **`digest` marks the readings that must not move.** `include` names `reading#/pointer` references;
  `exclude` lists field names blanked before hashing. **Every exclusion carries a written reason** and
  the validator refuses one without it — the `gk-core/tests/FusionRpg.Core.Tests/Battle/BattleGoldenTests.cs:163-172`
  recipe, whose comment is the argument: *folding a non-determinism field in makes the golden move for a
  reason that is not a determinism break.* The digest's own contract (canonical form, hashing, the
  same-run double-run falsifier) is `readback-verdict.md` (RS2.2).

## 5. The selection grammar (`JsonPointer`)

```
$                     the root
.name                 an object member
[3]  [-1]             an index, or an index from the end
[1:3]                 a half-open slice
[*]                   every element
[id==7]  [id=={x}]    the single element whose `id` equals the literal 7, or a captured value
```

Two properties are the whole design:

1. **Result shape belongs to the selector, not to the match count.** `[*]` and a slice always produce an
   array — zero, one or many elements — so a scenario written for "every item" cannot silently become a
   scalar when the array happens to hold one. An index and a key match produce the element itself.
2. **Ambiguity and malformation are defects, not readings.** A key match that matches more than one
   element throws (RS1's `Single(...)` semantics, kept). A malformed segment throws even when an earlier
   segment matched nothing — the whole pointer is parsed before anything is selected. A pointer that
   simply selects nothing returns `null`, which is a fact about the reading.

## 6. The expressibility measurement the shape lane could not run

`docs/architecture/rpg-simulator-shape-idea.md` §9 left this open: *"whether `sim.*`/`api.*`/`read.*`
fit that shape or force a discriminated-union redesign has **not** been tried. Also unproven: whether
the two runners should share a step DTO at all, or share only the envelope and the fixture directory."*

**Decision: share the envelope and the fixture convention; do NOT share the step DTO.** The measurement
is `RpgSimFormatContractTests.The_effect_scenario_step_dto_cannot_express_the_rpg_step_and_drops_it_silently`:

| What a step needs | `EffectScenarioStepDto` (`EffectScenarioRunner.cs:34-60`) |
|---|---|
| `op` | present |
| `route` (the declared route) | **absent** |
| `args` | **absent** (it has `payload`, a bag whose meaning is the effect host's) |
| `capture` | **absent** |
| `why` | **absent** |
| `expect` | present — as `IntentPlanDto`, an **effect intent plan** |
| effect-shaped members (`grant`, `ms`, `ptr`, `damage`, `killerPtr`, …) | present, meaningless here |

The decisive fact is not the missing members; it is that **the loss is silent**. `System.Text.Json`
ignores unknown members, so handing that DTO this program's step JSON parses successfully with the route
gone — the drift RS1 shipped its op→route refusal to prevent would be reintroduced by the sharing. And
`expect` would then mean two different things in one type, which is the parallel vocabulary
`docs/DESIGN-GATE.md` §1 exists to stop. The type this program does ship carries all four, which the
same file asserts from the other side.

What *is* shared: the envelope shape (`id` + `seed` + an op-dispatch step list), the JSON tolerances
(comments, trailing commas, case-insensitive names — `EffectScenarioRunner.cs:71-76`), and the fixture
convention (`gk-core/tests/fixtures/**/*.json` with a header and a golden sibling for the effect corpus). The
budget for the duplication is one flat 60-line DTO; the budget for sharing it is every future step field
having to mean something to two hosts.

## 7. What this document does NOT cover, and when it lands
- **The verdict, the digest's canonical form and the double-run falsifier** — RS2.2,
  `gk-core/tools/RpgSim/readback-verdict.md`.
- **The runner** (`--host`, `--base-url`, the `simEnabled` refusal, one sequential client) — RS2.3.
- **The in-process host and "settled" by polling** — RS2.4.
- **The real-process host** — RS2.5, delivered as [`ProcessHost.cs`](ProcessHost.cs) and the CLI's
  `--host process`: the same runner, a different transport.
- **The shipped slice-0 fixture's own migration.** It was still in RS1's original shape when this document was
  written, and it is **migrated** — RS2.3 landed the runner and the corpus in one commit, and
  `gk-core/tests/FusionRpg.E2E.Tests/RpgSimFormatContractTests.cs` now validates every file under
  `gk-core/tests/fixtures/rpg-scenarios/**` against this contract, so the corpus is the validator's largest test rather
  than a promise. (Kept as a line because the reason it waited is the reason any future format change waits:
  migrating the file without the runner would leave the E2E suite red between commits.)
- **The CI wiring, in part.** Owner ruling C3 (c) made slice 0 local-only until the shape held; it holds, and the
  scenarios are in CI **through the E2E project** — `.github/workflows/ci.yml:333` runs
  `gk-core/tests/FusionRpg.E2E.Tests` unfiltered with its own exit check, and `ci.yml:352-368` asserts CI stays
  unfiltered, so the in-process and real-process scenario tests both run there and a headless server claims no
  game-pool slot. **What is still not wired** is the CLI itself: no CI step invokes
  `dotnet run --project gk-core/tools/RpgSim -- --host process …`, which is RS7's one literal gap (its row records the
  measurement and the erratum ask).
