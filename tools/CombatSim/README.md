# CombatSim

Runs many randomized fights through the **real** combat pipeline and reports what came out.
Built for balance work: change a number, re-run, compare.

## The one rule

**This tool contains no combat math.** Every damage number comes back out of `gk-core/src/FusionRpg.Core`
via `CombatDamageDispatcher.DispatchInstant` — the same entry point the injector and battle hosts
call — driven through `FoundationHarness`, the offline harness Core already ships for this.

A reimplementation here would drift from `src/` the first time either changed, and every reading
would silently become a lie. If you need the sim to cover something new, wire it to the real code
or don't cover it.

Tuning comes from the shipped `gk-core/data/tuning/*.json`, loaded exactly the way `FusionRpg.Server`'s
startup loads it. The report header prints the constants it actually used, so a pasted result is
self-describing.

## `--actions` — the resource economy

Without it every swing is free and `resource.max.*`/`resource.regen.*` constrain nothing, which is
the fight the resource-free model was measuring. With it, **an actor who cannot pay cannot attack**:

```powershell
dotnet run --no-build -- predict --actions basic -a force,finesse,bastion --theta 100 -n 3000
```

`actions/basic.json` is a POC slice of the action program's COST model only (A1-A10 are specced and
unbuilt) - it lists exactly what it does and does not model. **Both engines run it**: the duel runner
pays and regenerates, the closed form walks the same deterministic schedule. A residual between a model
with an economy and a simulator without one would measure nothing.

**max is burst, regen is sustain.** Full pools buy a run of actions up front; regen sets the rate you
hold forever. Short fights are decided by the pool, long ones by the rate.

Measured effect: fight length 20-50 rounds -> **9-16**, and the closed-form residual 0.7% -> **9.1%** -
because shorter fights are exactly where the normal-race approximation is weakest. The `simRnds` column
separates the two failure modes: predicted kill-time vs the simulator's median rounds. They agree, so
the rate is right and the variance is the gap.

## `--status` — the fourth axis

Elements can be stood in for by `omni` (other elements are a bonus on top), but **status was never
applied at all** — and status is the one axis no arrow of the RPS cycle touches: not negated by dodge,
not short-circuited by parry, not saturated by defence.

```powershell
dotnet run --no-build -- predict --actions basic --status -a force,finesse,bastion --theta 100 -n 3000
```

The apply contest runs through the shipped `ResistanceEvaluator` — the same object `StatusRuntime.Apply`
drives — so delta, the potency split, the apply roll and both net factors are real. What `StatusModel.cs`
owns is only the DoT per-round bookkeeping, because neither engine has a `StatusRuntime` to tick.

**First run finds a live defect:** `netFactor` scales magnitude AND duration from one delta, so status
power is **quadratic in its own delta** — a 3-round 25%-of-base DoT becomes a 20-round 168%-of-base one,
about 33x the authored output. Kill times drop from 13.5 rounds to **1.0**. Write-up:
[`class-system-ideal.md`](../../../docs/architecture/class-system-ideal.md) 5c.4.

`dotnet run -- status --actions basic` sweeps all 21 ids in the locked catalog. It found four things:
21 statuses collapse to **3 behaviours** (nothing feeds the per-id channels); `status.resist.contagion`
had **no source at all**; DoT kills in **6% of baseline** time; and **cc at ZERO investment on both
sides is a permanent lock** - because `pApply = Sigmoid(delta/scale)` gives 0.5 at delta=0, which for
a 3-round cc means p x duration = 1.5. That is the exact defect `OverlayCombatCalculator.cs:162-165`
refused for parry ("a sigmoid would give 0.5 at delta=0 - a new default nobody chose") - one chain
fixed, the other not.

**Not modelled:** refresh/stacking (StatusRuntime owns family mutex - a re-apply over-counts) and CC
(it costs the target its turn, which needs the readiness model).

## `trinity` — the structural test

Does the posture trinity survive free build, or is it vocabulary? Spike each of the 12 aptitudes to the
max a legal allocation permits and play every spike against every other. **A row that beats all 11 is a
dominant build: free build has one correct answer.**

```powershell
dotnet run --no-build -- trinity --actions basic --status -a force --theta 100 --rounds 25
```

144 closed-form evaluations, instant - cheap enough to be a guard. Use this rather than best-response
chasing: the hill-climb reported "nothing beats Bulwark 55" while a direct check showed Vigor 55
beating it 100%. A search that misses a 100% counter is not evidence of absence.

**`--rounds N` is the clock - and it is NOT a balance metric.** A clock penalises fights for being
LONG, which is what survival and cc builds legitimately make them; measured on win rate with no clock,
the dominant corner never leaves. Keep it OFF for balance judgements; it is an encounter-design
parameter. The termination invariant is the instrument for unresolvable fights. (Old note, withdrawn:: a fight not decided in N rounds is a loss for BOTH. Without it,
"cannot lose" is optimal even when it also cannot win - and that alone flipped the result
no clock: Bulwark dominates; 25 rounds: no dominant corner; 15 rounds: dominant again - that apparent
band was the clock changing the win condition, not the design balancing.) Write-up: [`class-system-ideal.md`](../../../docs/architecture/class-system-ideal.md) 8.7a/8.8a.

## The termination invariant

Owner principle: **two actors at the same power scale, neither a pure-survival build with no damage,
must not fight forever.** That case excepted, an unending fight is a RESOURCE ECONOMY DEFECT - usually
regeneration outpacing consumption.

```text
netAttrition(X) = damage taken per round - recovery per round
netAttrition <= 0 on BOTH sides  =>  neither can ever die
```

`predict` flags it as `NEVER ENDS`; `trinity` marks the pairing `INF` in the matrix. Both engines now
TICK REGENERATION - before 2026-08-26 neither did, so a pool that refills read as one that does not and
this whole class of defect was outside what the harness could express.

Measured on a max-Vigor mirror: damage 7,535/round, recovery 9,992/round, **net -2,457 - the fight
cannot end.** Sizing rule that follows: **regen is sized against PEER DAMAGE, never against the pool.**
With r = recovery/damage, fights stretch by 1/(1-r) and r >= 1 is never.

## Two engines, one config — and why that matters

There are now **two** ways to get a number out of this tool, and they read the same
`tuning/aptitudes.v1.json`:

| | How | Cost | Valid when |
|---|---|---|---|
| **simulate** (`matrix`, `ladder`, `search`, `explain`) | many randomized fights through the real dispatcher | seconds to minutes | always |
| **predict** (`predict`, `search --analytic`) | closed form: per-round outcome mixture → first passage → `Phi(dT/sd)` | microseconds | single phase only — no depleting pools |

`Analytic.cs` obeys **the one rule** as strictly as the simulator does: it calls
`CombatProbability.Sigmoid`, `OverlayCombatCalculator.PierceFactor` / `.DivisiveMitigation` /
`.AmpFactorReciprocal` / `.CapAvoidanceBand` and `ClampedContest.Apply` through `CombatPolicy.Default`.
It adds the **expectation**, nothing else. So when the two disagree, it is a modelling gap — never two
implementations drifting apart.

```powershell
# closed form, then simulate the same builds and print the residual
dotnet run --no-build -- predict -a force-ns,finesse-ns,bastion-ns --theta 100 -n 4000

# closed form only, across the whole ladder — zero drift, and it costs nothing
dotnet run --no-build -- predict -a force-ns,finesse-ns,bastion-ns --theta 10,100,1000,5000 --no-verify

# SOLVE for a balanced cycle instead of hunting for one: 5,280 matrix evaluations in ~2s
dotnet run --no-build -- search --analytic -m aptitudes.v1 -a force-ns,finesse-ns,bastion-ns --theta 100 --restarts 24 --steps 220
```

Measured: **0.4% mean residual** on shield-free builds, **19.6%** with shields on — because a
depleting pool makes rounds non-identical, which is the one assumption the closed form needs. Full
record: [`docs/research/class-analytic-balance-2026-08-25.md`](../../../docs/research/class-analytic-balance-2026-08-25.md).

The `-ns` builds are shield-free copies, kept so a real disagreement can be told apart from the known
missing phase.

## `marginal` — the free-build test

The player has no class, so nothing stops them putting a point wherever it pays most. That makes the
aptitude distribution correct only if **the answer to "where does it pay most?" depends on who you are
fighting**:

- **Mandatory** — best point against *every* opponent. Every build takes it: a tax, not a choice.
- **Dead** — best point against *no* opponent. Nobody takes it: also not a choice.

Both read off one table — `dW/d(share_i)` against every opponent, renormalised so the number is the
point's value *net of what it costs elsewhere*.

```powershell
dotnet run --no-build -- marginal -a force-ns,finesse-ns,bastion-ns --theta 100
```

**Closed form only, and it has to be.** The per-point effect is a fraction of a percent; sampling noise
at 3,000 duels is ~0.9 pp, so the simulated version of this table is buried in its own error bars. The
whole 12 × N grid costs milliseconds and is exact.

Measured 2026-08-25: the shipped coefficients make `Fortitude` the best point everywhere and leave 5–7
of 12 aptitudes dead. `tuning/aptitudes.shape-diagnostic.json` is the test that found most of the cause
(sigmoid-consumed and reciprocal-consumed channels sized by one rule) — **a diagnostic, not a proposal,
and deliberately not named `v2`**. Write-up:
[`class-system-ideal.md`](../../../docs/architecture/class-system-ideal.md) §7b.

## Run

```powershell
dotnet build tools\CombatSim\CombatSim.csproj

# from tools\CombatSim
dotnet run --no-build -- list
dotnet run --no-build -- run   -s baseline
dotnet run --no-build -- run   -s duel -n 50000 --seed 7
dotnet run --no-build -- sweep -s duel --channel combat.penetration.omni --side attacker --from 0 --to 400 --steps 9
```

`run` gives one scenario's outcome and damage distribution. `sweep` re-runs the whole scenario at
each value of one channel and prints the curve — that is the balance workflow: *does this stat do
anything, and where does it stop mattering?*

Add `--csv out.csv` to either for a flat metric table.

### `fight` — actual fights to the death

`run` measures one swing. `fight` gives both sides an HP pool and lets them trade until someone
dies, which is the only way to answer *"can the attacker die by attacking this build?"*

```powershell
dotnet run --no-build -- fight -s tank-thorns,tank-max --rounds 20000
dotnet run --no-build -- sweep -s tank-thorns --fight --channel combat.reflect.damage.omni --from 0 --to 120
```

Stats are rolled **once per fight**, not per swing — a build is a build for the whole engagement,
and re-rolling would average away the extremes a tank build is made of.

Four outcomes, and they are asserted to partition every fight: **attacker dies**, **defender dies**,
**both die**, **stalemate**. That fourth-and-third split matters — an earlier three-way version
reported `0% / 0% / 0%` for a pure thorns build because its dominant outcome is a *mutual* kill, and
a missing category reads as "nothing happened".

> **`--rounds` changes conclusions, so set it deliberately.** `tank-max` reads as a 100% stalemate at
> the default 500 swings and a 100% mutual kill at 20,000 — the shield simply takes ~1,800 swings to
> break. A stalemate is only meaningful once you have checked it is not just your cap.

## Trying a tuning change without editing the shipped file

```powershell
dotnet run --no-build -- run -s duel --set combat.pierceScale=200
dotnet run --no-build -- run -s evasion --set combat.parryCapPermille=500 --set shield.chipFloorKPm=50
```

`--set <domain>.<key>=<value>` patches the tuning JSON **in memory before it parses**. It refuses a
key the file does not already contain, so you cannot invent a tunable the game does not read. The
domains are the `gk-core/data/tuning` basenames: `combat`, `shield`, `stats`, `derived-stats`, `status`.

When a value proves out, make it real with `python gk-core/tools/tuning/publish.py combat <key>=<value>` —
same key path, no translation step to get wrong.

## Scenarios

JSON in `scenarios/`. A stat is either a number (fixed) or `{"min":a,"max":b}` (sampled uniformly
per trial). **Any of the 256 registered channel ids works** — ids are validated against the live
`DerivedStatRegistry` on load, so a typo fails loudly instead of being silently ignored.

```json
{
  "name": "my-test",
  "trials": 10000,
  "seed": 42,
  "baseDamage": { "min": 80, "max": 120 },
  "elements": "singleRandom",
  "defenderElement": "ice",
  "attacker": { "combat.power.omni": { "min": 0, "max": 400 } },
  "defender": { "combat.defense.omni": 300 },
  "shieldHp": 0,
  "reflection": true
}
```

| Field | Notes |
|---|---|
| `elements` | `singleRandom` · `fixed` (needs `fixedElements`) · `none` |
| `defenderElement` | drives the matchup matrix; omit for neutral |
| `shieldHp` | shield granted to the defender each trial; `0` = none |
| `reflection` | wires `actorResolve` into the dispatcher, enabling the reflect path |

> `elements: none` sends a packet with no payload, and `OverlayCombatMath.Finalize` early-returns
> for that — so the **whole mitigation chain is skipped** while reflection still runs. That is real
> shipped behaviour, worth being able to demonstrate, but it is not how you measure mitigation.

Two RNG streams, both seeded: one samples each trial's stats, one makes the in-combat rolls.
Changing stat ranges therefore cannot shift the roll sequence, so two sweep steps stay comparable
instead of differing by RNG drift.

## Bundled scenarios

| Scenario | What it is for |
|---|---|
| `baseline` | every channel at its shipped default — the no-op control |
| `duel` | randomized mitigation chain: power/defense/crit/pen/amp |
| `evasion` | parry/block bands and the `ClampedContest` strength/shred exchange |
| `reflect` | reflection with resist on the attacker |
| `shield-reflect` | shielded reflector — reflection reads *pre-shield* damage |

## Reading the report

- **`dmg/base`** — mean damage as a multiple of the authored hit. `1.0` means the stat block is a
  wash; below `1.0` the defender is winning the exchange.
- **`zero-damage`** — trials that dealt literally nothing. Much larger than the miss count means the
  subtractive `power − defense` term is flooring at zero, not that attacks are missing.
- **`attacker self-dmg`** — reflected damage as a share of the damage the attacker dealt. Above
  `100%` the attacker is losing the trade outright.
- **`max bounces / trial`** — reflection ping-pong depth. Bounded by `procDepthLimit`; if it ever
  equals that limit, chains are hitting the terminator rather than converging.
