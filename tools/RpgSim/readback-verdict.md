# The verdict and digest contract — `readback-verdict` (rpg-simulator RS2.2)

**Module id:** `readback-verdict` (capability map `docs/architecture/rpg-simulator-map.md`, module 3).
**Machine:** `ScenarioVerdict.cs` (the artifact), `ReadingDigest.cs` (canonical form, exclusions, hash,
the double-run comparison), `CanonicalJson.cs` (the one writing of a value).
**Depends on:** `scenario-format.md` (RS2.1) — a reading exists only where a `read.*` step declared it.
**Tests:** `gk-core/tests/FusionRpg.E2E.Tests/RpgSimVerdictContractTests.cs`.

> The map promises `docs/architecture/rpg-simulator/spec-readback-verdict.md`; that tree is outside
> this lane's fence, so the contract is here, beside its machine (see `scenario-format.md`'s header).

---

## 1. The artifact

```jsonc
{
  "scenarioId": "first-session-forward",
  "seed": 20260922,
  "clock": "ambient — the machine clock, through the one seam",
  "host": "inproc",                        // or "process:http://127.0.0.1:53111"
  "ok": true,
  "steps":    [{ "index": 0, "op": "api.player.create", "route": "POST /api/players", "outcome": "ok" }],
  "captures": [{ "name": "playerId", "path": "$.id", "source": "POST /api/players", "value": 1 }],
  "readings": [{ "name": "read.souls", "source": "GET /api/souls/{playerId}", "method": "GET", "value": { } }],
  "failures": [],
  "digest": "5f0c…",
  "digestExclusions": [{ "field": "*Utc", "reason": "Ambient wall clock: …" }]
}
```

Four things are load-bearing.

1. **`readings[].source` is required by the type.** A verdict value that has no route behind it cannot
   be written at all — the contract's version of
   `docs/contributing/live-probe-standard.md` §3 ("the same query path the FE uses, or the hub message
   it receives, and the verdict must say which"). No reading may come from `/api/test/snapshot`
   (`gk-core/src/FusionRpg.Server/SimEndpoints.cs:144`), which the RS2.1 validator refuses as a read route for
   every scenario, digest-bearing or not — the narrower rule ("no *digest-bearing* read from the
   snapshot route") is implied by the wider one, and the wider one is cheaper to hold.
2. **A response body is never evidence.** A call step's body becomes a `captures[]` entry, carrying the
   route it came from, and `captures` is not part of the digest. The value an assertion compares is the
   read-back; `equalSet` compares read-back to read-back.
3. **`clock` and `host` are printed.** A pasted verdict has to be self-describing about the two things
   that make two runs comparable: what time the server thought it was, and which host ran.
4. **`failures` is separate from `digest`.** A moved digest is a determinism question; an absent
   assertion is a behaviour question. A verdict that conflated them would make a flaky digest look like
   a broken feature.

## 2. What enters the digest

The scenario's `digest` step lists `include` references: `read.souls#$` (the whole reading) or
`read.souls#$.balance` (the selected value). Nothing else enters — **not** captures, not step outcomes,
not the readings a scenario did not name. Two consequences worth stating:

- A scenario that names nothing digest-bearing gets `"digest": null`. That is legal (the assertion
  vocabulary alone can be a scenario's whole value) and it is visible, not silent.
- The canonical form is an object keyed by the reference (ordinal order), each entry
  `{ "source": <route>, "value": <blanked value> }`. Declaration order cannot move the hash, so
  reordering `include` is not a determinism event.

## 3. The exclusion list — written down, one reason per field

The recipe is `gk-core/tests/FusionRpg.Core.Tests/Battle/BattleGoldenTests.cs:163-172`: blank the fields that
move for a reason that is *not* a determinism break, and write the reason next to each. The digest
cannot be trusted without it, and a digest nobody trusts is worse than no digest.

`ReadingDigest.Baseline` is that list. Each entry names a field and why; `*` is a suffix pattern, which
is how the six spellings of the same wall clock share one reason (`dispatchedUtc`, `dueUtc`,
`collectedUtc`, `createdUtc`, `serverUtc`, `lastHeartbeatUtc`). The list today: `*Utc`, `*At`,
`instanceId`, `correlationId`, `revision`, `generatedAt`.

Three rules keep it honest:

- **An exclusion without a reason is refused** (RS2.1's validator, for scenario-declared exclusions;
  `Baseline` is impossible to build without one, and a test asserts every entry has one).
- **The list is extended by measurement, never by guessing.** A scenario that digests a payload with a
  new volatile field finds out from the double-run falsifier, which names the pointers that moved, and
  the field is added with its reason in the same commit. The list is therefore *incomplete by
  construction* — that is what the falsifier is for, and it is why the falsifier must be run every time
  and not once. RS2.4 ran it and added `activityFactId` (a per-row insert counter).
- **Every verdict prints the list it used** (`digestExclusions`), so a reader can see what was blanked
  without opening this document.

### What the first real measurement found (RS2.4)

Two runs of `first-session-forward` on fresh in-process hosts, same seed and same file:

| Observation | Value |
|---|---|
| The scenario's **declared** digest | identical both runs — `daa9df408054f32e762eae9abdd5ea5c98312170e175abdb295e2c210cf9db3e` |
| The falsifier over **every** reading | **moved**, 71–110 named pointers across runs (the count itself varies) |
| What moved | the summon roll (`roster[*].profile.speciesId`, `.rarity`, `.elementPrimary`, `actor.typeId`, `actor.side`), `squadInstanceIds` (fresh GUIDs), the reward/XP amounts attributed to those specimens, and two fields the list did not name: `activityFactId` (added above) and `t` |

`t` is deliberately **not** added: it is the XP ledger row's timestamp under a one-letter field name, and
`IsExcluded` blanks a name everywhere it appears, so excluding a name this generic could silently blank a
value that matters in another payload. The honest fixes are (a) the ledger publishes a self-describing
name, or (b) a scenario that digests the ledger declares the exclusion itself. Filed as **RS-F7** in
`tasks/rpg-simulator-todo.md`; the shipped scenario's declared digest does not include the ledger, so
nothing here is smoothed over.

**RS-F7's contract half, closed 2026-09-23 (lane `sim-t3-2`).** Branch (b) of that row's acceptance is now
written down rather than rediscovered: **the XP ledger is NOT digest-eligible.** A scenario that wants to
digest `read.progression.ledger` must declare the exclusion for its `t` field itself, with its own reason
(the validator refuses an exclusion without one); a scenario that does not is not allowed to fold the ledger
in. `ReadingDigest.Baseline`'s own doc comment carries the same line, so the rule is beside the list it
constrains. The ledger's payload shape is still owned by the progression surface — a self-describing
`createdUtc` would let this become a normal `*Utc` exclusion, and that half stays open for its owner.

The roster/expedition divergence is **not** an exclusion-list problem: it is the two server-minted RNG
seeds (`gk-core/src/FusionRpg.Server/CreatureEndpoints.cs:96`, `gk-core/src/FusionRpg.Server/ExpeditionEndpoints.cs:36`),
which no scenario can own today. That is **RS-F4**, and it is why the scenario asserts closed vocabularies
and attribution over those readings and digests only what it can hold still. A seed seam (or the clock
seam's sibling) is what would let the digest cover the values.

## 4. The double-run falsifier

A determinism claim asserted once is a claim; this one is executed every run. The driver runs the same
scenario twice and compares the two canonical forms:

```
rpg-sim --scenario <f> --double-run
```

`ReadingDigest.Compare` returns `Same` **and** `MovedPointers`, and the pointers are named, not counted:
`$.read.expeditions#$.items[0].state: "Dispatched" -> "Collected"` is evidence; "digest mismatch" is
not. A run whose two digests differ is reported as a **determinism finding with the moved pointers**;
the runner does not retry, does not re-normalize and does not "smooth" the difference (the same rule the
map applies to in-process vs real-process disagreement).

**Cross-host agreement** (in-process vs real process, owner ruling E2 (a)) uses the same comparison: the
digest must agree where the scenario declares it must, and a disagreement is **reported**. That check is
RS2.5's slow lane; this contract owns the comparison it will call.

## 5. The golden artifact

Owner ruling **C2 (a)**: golden *and* hash. The golden is a stored verdict at
`gk-core/tests/fixtures/rpg-scenarios/golden/<scenario-id>.verdict.json` — the `gk-core/tests/fixtures/effects/**`
sibling convention. Its role is deliberately narrow:

- The golden pins the **stored artifact** (so a reviewer can see what the last accepted run read) and
  its `digest` is what a later run is compared against.
- The golden is **not** the outcome oracle. An outcome-dependent row set (a victory credits one XP
  reason, a defeat another) is asserted by `expect.*`, which pins the *rule* and the attribution rather
  than a count — the reading RS1 already shipped. A golden that pinned counts would fail the day a
  balance change landed, for a reason that is not a regression.
- A golden is refreshed only when its **distinguishable** readings move, and the refresh commit says
  which pointer moved and why. `--update-golden` exists for that; it is never the default. **Both refresh
  verbs now PRINT the move before they write** (`GoldenVerdict.Report`), so the commit that moves a golden can
  quote it instead of describing it — a silent re-bless is how a golden stops being evidence.

**Landed 2026-09-23 (lane `sim-t3-2`), with two refresh verbs rather than one.** The golden exists at
`gk-core/tests/fixtures/rpg-scenarios/golden/first-session-forward.verdict.json` — a stored verdict whose declared
digest is `daa9df408054f32e…` — and `gk-core/tools/RpgSim/GoldenVerdict.cs` is the comparison: the **digest**, the
**seed**, the **scenario id**, the **reading set** (each read-back's name, method and route) and the
exclusion **fields**. The readings' *values* are deliberately not compared, per §5's second bullet — the
golden is not the outcome oracle.

| Verb | Who uses it | Why two |
|---|---|---|
| `--golden <path>` / `--update-golden` on the CLI | a process or `--base-url` lane | the CLI cannot boot an in-process host (RS-F6: it is a real-process front end), so its refresh works against whatever host it drove |
| `FUSIONRPG_BLESS_RPGSIM_GOLDEN=1` in `gk-core/tests/FusionRpg.E2E.Tests/RpgSimGoldenTests.cs` | the test lane, which IS in CI | this project is where an in-process host exists, and it is the house pattern the sibling fixtures use (`FUSIONRPG_BLESS_CONTRACT_FIXTURES`, `FUSIONRPG_BLESS_WORLD_FIXTURE`) |

Both are explicit; neither is a default. `RpgSimGoldenTests` compares a fresh in-process run against the
checked-in golden (`daa9df408054f32e…` unchanged, the reading set and exclusion fields unchanged) and carries a
**falsifier seen to fail**: a moved digest, a dropped reading and a changed exclusion field are each reported by
name, while a values-only move under the same digest is correctly *not* reported.

**One guard interaction, resolved rather than discovered later:** `scripts/guard-sim-fabrication.ps1` scans
`gk-core/tests/fixtures/rpg-scenarios/**` recursively for scenarios, and a golden is not one — it has no `steps` array,
no `clock` block and no `digest` step, so scanning it reported a wall of honesty violations that said nothing.
The guard now **skips the `golden/` subtree and prints the count it skipped** (`golden verdicts skipped=1`), so
the exclusion is visible in every run instead of being a silent filter.

## 6. What this document does NOT cover

- **Running the scenario** (the client, the `simEnabled` refusal, the settle wait) — RS2.3/RS2.4.
- **The real-process host** — RS2.5, delivered as `ProcessHost.cs` and the CLI's `--host process`. Its
  cross-host measurement: the declared digest is identical on a fresh in-process host and a fresh real
  process (`daa9df408054f32e…`), while the whole-reading falsifier moves 109 named pointers — the two
  server-minted RNG seeds of **RS-F4**, reported rather than smoothed.
- **The honesty guard** (`/api/test/*` allowlist, the corpus scan) — RS4.
- **A clock seam — landed (RS3).** The seam is product surface now (`FusionRpg.Core.Time.ServerClock`),
  and **increment 5a** makes `clock.mode: offset` honest: a scenario declares `offsetSeconds` and BOTH
  approved hosts apply it at boot (the in-process factory configures the seam; `ProcessHost` passes
  `FUSIONRPG_CLOCK_OFFSET` to the child), which `RpgSimClockOffsetTests` measures on a real row's
  `dispatchedUtc`/`dueUtc` rather than reading the seam back. **Increment 5b** adds the mid-run movement
  (`clock.set`, a declaration the host's clock control applies — in place in-process, by rebooting the real
  process on the same data dir), which is what retired `ForceExpeditionDue`'s `UPDATE` and the corpus's
  `test.expedition-due` step. The shipped corpus still declares `ambient` at boot, so its timestamps stay
  excluded from the digest by pattern; a scenario that declares an offset makes them stable and may digest
  them. `explicit` (an absolute instant) stays refused by name — the seam's product shape is a signed
  offset, and a route that sets a clock is deliberately not specified (spec §2).
