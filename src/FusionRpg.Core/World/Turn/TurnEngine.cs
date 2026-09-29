using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Turn;

/// <summary>What one turn produced: the new world, what happened, and the drift detector.</summary>
public sealed record TurnResult(WorldState World, TurnReport Report, string StateHash);

/// <summary>
/// The map's clock (spec-turn-engine.md). <c>state(N+1) = Step(state(N), commands(N), seed)</c> —
/// pure: no I/O, no wall clock, no ambient state, and no knowledge of which commander is human.
///
/// The phase order is locked; changing it bumps the ruleset version, so it is written once here and
/// observable in the report. Movement resolves through a discrete-event queue rather than fixed
/// sub-steps, so a lane crossing lands where it actually crosses instead of on the nearest sample.
/// </summary>
public static class TurnEngine
{
    public const int EngineVersion = 1;
    /// <summary>
    /// Bumped to 13 on 2026-09-19 (warden-freeze-fix, owner-approved; world-continuity-map.md module
    /// 8 names the gap): the per-sector warden freeze the owner withdrew on 2026-09-13
    /// (warden-mortality-ideal.md) stops applying. `LoamPhases.Pressure` no longer skips a warded
    /// sector on recovery, and `LoamForecast.Weakest` no longer drops it from the fade candidates, so
    /// a warded sector fades and recovers like any other. `WorldCommandAdmission` refuses every
    /// `bind-warden` with `warden.retired`, and Reveal re-admits, so a bind filed under 12 and still
    /// open is dropped instead of resolved. A real behaviour change: the same command log, for any
    /// world that holds a `WardenBindingId` or files a `bind-warden`, now produces a different state
    /// than 12 did. A world with neither replays identically. The only pinned world hash
    /// (`WorldWaveOneAcceptanceTests.GoldenFinalHash`) binds no warden and did not move.
    ///
    /// Bumped to 12 on 2026-09-15 (world-action-economy `budget-debit`,
    /// docs/architecture/world-action-economy/spec-budget-debit.md): the `DebitActCostUnlocked`
    /// seam fills in — priced cargo/cache acts spend the refilled next-turn budget same-tx
    /// (claim 250, deposit 100, withdraw 100, load 50, unload 50, transfer 0) with
    /// `entity.spent` refusal, debit-once per `CommandId`, and a post-pass re-hash (the stored
    /// hash always describes the committed post-debit state). A real behaviour change: a log
    /// with a priced kind now debits + re-hashes where 11 stored the pre-debit hash; a log
    /// with zero priced kinds replays identical (no debit fires, re-hash is a fixed point).
    ///
    /// Bumped to 11 on 2026-09-15 (world-action-economy `hold-allowance`,
    /// docs/architecture/world-action-economy/spec-hold-allowance.md): `BudgetFor(Hold)` stops
    /// being a literal 0 and refills to the tuned garrison act allowance
    /// (`movement.holdAllowanceMilli`, provisional 250), and the `first-light` ash-waste fixture
    /// authors the same allowance instead of 0. A real behaviour change, not a field addition:
    /// the same command log now refills every holder to 250 where version 10 refilled 0, and
    /// `WorldCanonical.cs:61` hashes `MovementRemaining`, so every holder-carrying hash moves.
    /// First mover takes 10 → 11 per the hold-allowance spec's own plan-order note (it names
    /// `budget-debit` as the expected carrier, but `budget-debit` is unlanded in this worktree) —
    /// when `budget-debit` lands it rides 11 and bumps 11 → 12 instead of 10 → 11.
    ///
    /// Bumped to 10 on 2026-09-06 (base-defense `siege-construction` 15.4b, audit F10, decision 48):
    /// `WorldSlot.SlotDepletionMilli` now actually increments on a yielding turn, via the SAME
    /// `Production` phase slot — a real behaviour change: an owned `Rootbed` slot (Loam's own faucet)
    /// now accrues depletion every turn it is owned, where before this bump the field was fully wired
    /// end-to-end (hashed, persisted, read by `IsExhausted`) but nothing ever moved it off zero.
    /// **Named, deliberately out of scope**: `IsExhausted` reaching true does not yet stop a slot from
    /// yielding (`LoamProduction.For`/`SiegeConstruction.Yield` neither one checks it) — the counter and
    /// the transition event are real; the "and then it stops" half of `structure-state`'s own
    /// `IsExhausted` doc comment is a separate, unscoped, un-started gap this task does not also close.
    /// Bumped to 9 on 2026-09-06 (base-defense `siege-construction` 15.4): the two raw faucets land in
    /// `Production` — `ironwork` from a CLEARED `shard-vein` slot, `rubble` from a CLEARED
    /// `material-seam` slot (§2). A real behaviour change, not a field addition: the same command log,
    /// for any world with a cleared `shard-vein`/`material-seam` slot, now accrues a stock that stayed
    /// zero under version 8. Re-blessed against `WorldWaveOneAcceptanceTests`' own scripted turn-9
    /// clear of `ash-waste`'s `material-seam` (entry #17 there). The same investigation also found and
    /// fixed a real, unrelated persistence defect — `WorldSector.RubbleStock`/`IronworkStock` had no
    /// database columns at all since 15.1, silently write-only — recorded in `RpgStore.World.cs`'s own
    /// migration list and the same entry #17, not here (it changed no hashed behaviour by itself: a
    /// field already read 0 in memory for every world that never round-tripped through a save).
    ///
    /// Bumped to 8 on 2026-09-06 (base-defense `siege-engagement`, module 20, spec-siege-engagement.md):
    /// a CONTINUING siege — an attacker already standing in a sector it does not own, with no fresh
    /// `assault` order this turn — used to fall through `ContactResolver.SectorContacts` into a generic
    /// `BattleKinds.Sector` request, which `DistrictAssaultResolver`'s own delegation guard sent
    /// straight to `PlaceholderBattleResolver`. A siege silently stopped being a real board fight the
    /// moment its attacker held still for one turn. `MovementPhase.BuildContactRequest` now checks
    /// `SiegeEngagement.IsUnderSiege` per contact and routes a genuinely besieged sector to
    /// `BattleKinds.District` instead, reusing `DistrictAssaultPhase.BuildBoard` — the same real board
    /// (structures, elements, `BattleEngine.Resolve`) an explicit `assault` order already got. A real
    /// behaviour change, not a field addition: the same command log, for any world with a siege that
    /// spans a turn boundary, now resolves that turn's fight on the real board instead of
    /// `PlaceholderBattleResolver`'s abstract strength comparison. Found and re-blessed against
    /// `WorldWaveOneAcceptanceTests`' own scripted incursion (entry #16 there) — Zomboss's band walks
    /// directly onto Dave's newly-claimed `ash-waste` at turns 11-12, the same contact `siege-supply`
    /// F1/F1b (entry #14, `RulesetVersion` unchanged) already named without this fix touching combat.
    ///
    /// Bumped to 7 on 2026-09-05 (world-map W58, spec-sector-development.md §1/§2, decisions.md): the
    /// phase's own second and final re-bless — `growth.seatPulsePerWeek` moves off 0 (a held Seat now
    /// genuinely accrues `RecruitStock` on a week boundary) and `LoamUpkeep`'s season factor moves off
    /// 1000-per-mille for seasons 1-3 (season 0 stays identity, so any world that never leaves it sees
    /// no change). Both terms existed and were wired inert since W43/W48 — this is the tuning-only
    /// publish that makes them real, not a new code path, and it is real hashed behaviour: the same
    /// command log against a sector with an owned Seat, or one that lives long enough to cross a
    /// season boundary, now produces a different state than version 6 did.
    ///
    /// Bumped to 4 on 2026-08-23 (spec-loam-turn.md): `Production` and `Pressure` stop being
    /// pass-throughs. A sector now earns loam and pays upkeep every turn, and ground can be lost to
    /// the Fracture for the first time. The program's second and last golden re-bless — the first
    /// was `loam-model`'s field addition, both recorded here per the same discipline W20 established.
    ///
    /// Bumped to 3 on 2026-08-22: `Intel` moved *after* `Snapshot`. Belief is what you know at the
    /// end of the turn, and the end of the turn is after the turn has finished happening — before
    /// this, a claim settled in Snapshot was invisible to the faction that made it, which re-filed
    /// the order and had it dropped as `claim.already-yours`. Found by playing, not by testing.
    ///
    /// Bumped to 2 on 2026-08-22 when the `Intel` phase landed: every turn now writes each faction's
    /// belief, so the same commands produce a different — and larger — state than they did under
    /// version 1. Stored version-1 reports refuse to re-derive rather than fabricating, which is the
    /// behaviour this counter exists for.
    ///
    /// Bumped to 5 on 2026-08-23 (L27, spec-loam-legions.md): `Pressure` retires wound-based
    /// attrition and wires `LegionSupply.Resolve` in its place — a legion beyond supply now burns
    /// carried loam and is destroyed outright rather than bled slowly, a real change to what the
    /// same command log produces, not a field-only addition.
    ///
    /// Bumped to 6 on 2026-09-04 (world-stage W24-W30, decisions.md:98 — one bump for the whole
    /// `cede`/`bind-warden`/`dowse` wave, not three): `LoamPhases.Pressure`'s shortfall selection
    /// (`LoamForecast.Weakest`, W25) now reads a filed `cede` order as an input to which sector
    /// absorbs the shortfall. A world that files no `cede` order resolves identically to version 5
    /// — this bump exists only for the case a real order changes the outcome, and covers `bind-warden`
    /// (W28) and `dowse` (W30) landing after it without a second bump, per the same decision.
    /// </summary>
    public const int RulesetVersion = 13;

    public static class Phases
    {
        public const string Reveal = "Reveal";
        public const string Movement = "Movement";
        public const string Sieges = "Sieges";

        /// <summary>
        /// base-defense siege-seam (2026-09-05): district assaults, distinct from `Sieges` (guard
        /// fights) — decision 26's ground-level fight, driven by `WorldCommandKinds.Assault`. A
        /// brand-new command kind, so no existing command log can ever populate it: this phase is
        /// provably a no-op for every world that predates it, the same "field/command-only addition"
        /// precedent `RulesetVersion`'s own history above already establishes for several other
        /// additions — no bump needed.
        /// </summary>
        public const string Assaults = "Assaults";
        public const string Production = "Production";
        public const string Growth = "Growth";
        public const string Pressure = "Pressure";
        public const string Events = "Events";

        /// <summary>Claims settle and postures land, because everything they depend on is decided.</summary>
        public const string Snapshot = "Snapshot";

        /// <summary>
        /// **Last**, and it moved here in RulesetVersion 3. A faction records the world as it *ends*
        /// the turn — which means after claims have settled, so it can see that its own claim
        /// worked. Recording before Snapshot left a commander unable to observe the one thing it had
        /// just done.
        /// </summary>
        public const string Intel = "Intel";
    }

    /// <summary>
    /// <paramref name="resolver"/> defaults to <see cref="DistrictAssaultResolver"/> — the one real,
    /// shipped `IBattleResolver` (base-defense's siege resolver). It is a parameter rather than a
    /// container registration on purpose: the world module is the only thing that names it. Since
    /// `actor-hub-and-combat-power-solid-fixing`'s `placeholder-battle-hub` (T20) deleted the wave-1
    /// Hp×Level stand-in, there is no longer a second resolver to default to — every battle kind
    /// `DistrictAssaultResolver` cannot really simulate (non-district, or a degenerate district
    /// request) resolves to a refused/no-op outcome rather than an invented winner.
    /// </summary>
    public static TurnResult Step(
        WorldState world, IReadOnlyList<WorldCommand> commands, ulong seed, IBattleResolver? resolver = null,
        FusionRpg.Core.Power.PowerTuning? powerTuning = null, long? mythicClaimBonusRatePerMillion = null)
    {
        var report = new TurnReport();
        var turn = world.CurrentTurn + 1;
        var battles = resolver ?? DistrictAssaultResolver.Instance;

        // A rout is spent at the *top* of the turn it costs, not at the bottom. Clearing it here
        // rather than in Snapshot is what lets a force that is broken again during this same turn
        // keep the new rout instead of having it cancelled by the one it was already serving.
        var recovering = world.Entities
            .Where(e => e.Routed)
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.Ordinal);

        var opening = recovering.Count == 0
            ? world
            : world with
            {
                Entities = world.Entities.Select(e => e.Routed ? e with { Routed = false } : e).ToList()
            };

        var revealed = Reveal(opening, commands, recovering, report);
        var movement = Movement(opening, revealed, report, turn, battles, seed);
        var next = movement.World;
        next = Sieges(next, revealed, report, turn, battles, seed);
        next = Assaults(next, revealed, report, turn, battles, seed);
        next = Production(next, report);
        next = Growth(next, report, turn, seed);
        next = Pressure(next, revealed, report, turn, seed);
        next = Events(next, report, turn, seed);

        // Snapshot before Intel (RulesetVersion 3): a claim settles in Snapshot, so recording belief
        // first meant a faction could not see that its *own* claim had succeeded — it re-filed the
        // order the next turn and the engine dropped it as `claim.already-yours`. Belief is what you
        // know at the end of the turn, and the end of the turn is after the turn has finished
        // happening. Found by playing twenty turns and watching Zomboss claim the same sector twice.
        next = Snapshot(next, revealed, report, turn, powerTuning, mythicClaimBonusRatePerMillion);
        next = Observe(world, next, report, turn, movement.VisitedByFaction);

        return new TurnResult(next, report, StateHasher.Hash(next));
    }

    /// <summary>
    /// Orders seal at commit and reveal together, in stable (commander, command) order — never the
    /// order they arrived in, or two clients racing to submit would change a turn's outcome.
    ///
    /// Legality is re-checked here because the world may have moved since submission. A stale order
    /// is reported and skipped: one commander's out-of-date plan must never abort everyone's turn.
    /// </summary>
    static List<WorldCommand> Reveal(
        WorldState world, IReadOnlyList<WorldCommand> commands,
        IReadOnlySet<string> recovering, TurnReport report)
    {
        report.BeginPhase(Phases.Reveal);

        var ordered = commands
            .OrderBy(c => c.CommanderId, StringComparer.Ordinal)
            .ThenBy(c => c.CommandId, StringComparer.Ordinal)
            .ToList();

        var legal = new List<WorldCommand>(ordered.Count);
        foreach (var command in ordered)
        {
            var (ok, reason) = WorldCommandAdmission.Admit(world, command);
            if (!ok)
            {
                report.Add(Phases.Reveal, TurnReportKinds.CommandDropped, command.CommandId, reason);
                continue;
            }

            // A force beaten in the field spends the following turn recovering, so its orders are
            // dropped here rather than silently ignored downstream.
            if (command.EntityId is { } subject && recovering.Contains(subject))
            {
                report.Add(Phases.Reveal, TurnReportKinds.CommandDropped, command.CommandId, "entity.routed");
                continue;
            }

            // A garrison has given up its mobility for the turn; a march order for one is refused
            // here rather than silently producing a zero-distance move. Move-ONLY by lock
            // (spec-hold-allowance.md §Design 3): priced acts for holders pass here — their
            // budget refusal is `entity.spent`-shaped at the debit site, never `entity.held`.
            // Widening this gate onto priced acts would re-imprison garrisons.
            if (command.Kind == WorldCommandKinds.Move && command.EntityId is { } marcher
                && world.Entities.Any(e =>
                    string.Equals(e.EntityId, marcher, StringComparison.Ordinal)
                    && string.Equals(e.Stance, MovementPolicy.Hold, StringComparison.Ordinal)))
            {
                report.Add(Phases.Reveal, TurnReportKinds.CommandDropped, command.CommandId, "entity.held");
                continue;
            }

            legal.Add(command);
            report.Add(Phases.Reveal, TurnReportKinds.CommandAccepted, command.CommandId, command.Kind);
        }

        return legal;
    }

    /// <summary>
    /// Discrete-event movement, contact, and the fights either one starts. The work lives in
    /// <see cref="MovementPhase"/>; the engine owns only the fact that it happens here, before
    /// sieges and after reveal.
    /// </summary>
    static MovementResult Movement(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report,
        int turn, IBattleResolver resolver, ulong seed)
    {
        report.BeginPhase(Phases.Movement);
        return MovementPhase.Run(world, commands, report, Phases.Movement, turn, resolver, seed);
    }

    /// <summary>Deliberate attacks on slot guards — never a consequence of walking past one.</summary>
    static WorldState Sieges(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report,
        int turn, IBattleResolver resolver, ulong seed)
    {
        report.BeginPhase(Phases.Sieges);
        return SiegePhase.Run(world, commands, report, Phases.Sieges, turn, resolver, seed);
    }

    /// <summary>Deliberate assaults on a hostile district's core — never a consequence of walking
    /// past one, the same distinction `Sieges` draws for a slot's guard.</summary>
    static WorldState Assaults(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report,
        int turn, IBattleResolver resolver, ulong seed)
    {
        report.BeginPhase(Phases.Assaults);
        return DistrictAssaultPhase.Run(world, commands, report, Phases.Assaults, turn, resolver, seed);
    }

    /// <summary>A sector earns before it pays — `LoamPhases.Production` (spec-loam-turn.md).</summary>
    static WorldState Production(WorldState world, TurnReport report)
    {
        report.BeginPhase(Phases.Production);
        var next = LoamPhases.Production(world, report, Phases.Production);
        // base-defense `siege-construction` 15.4: the SAME "a sector earns before it pays" moment,
        // for the two raw ironwork/rubble faucets (`shard-vein`/`material-seam`, cleared) — one phase
        // slot, not a second one, since both are conceptually the identical "this sector earns
        // resources this turn" step.
        next = World.Siege.SiegeConstruction.Production(next, report, Phases.Production);
        // 15.4b / audit F10 / decision 48 (2026-09-06): the per-slot depletion counter nothing was
        // incrementing — same phase slot again, since "a slot just yielded" is this exact moment.
        return World.Siege.SiegeConstruction.AdvanceDepletion(next, report, Phases.Production);
    }

    static WorldState Growth(WorldState world, TurnReport report, int turn, ulong seed)
    {
        report.BeginPhase(Phases.Growth);
        return FusionRpg.Core.World.Growth.GrowthPhases.Growth(
            world, report, Phases.Growth, turn, seed,
            FusionRpg.Core.World.Growth.RecruitPolicy.SeatPulsePerWeek,
            FusionRpg.Core.World.Growth.RecruitPolicy.LairMultiplierMilli,
            FusionRpg.Core.World.Growth.RecruitPolicy.SpecialWeekMultiplierMilli);
    }

    /// <summary>
    /// `Sustain` resolves first, at the very top: its spend must already be sitting in a sector's
    /// stock before the component's automatic accounting runs this same turn (spec-loam-legions.md).
    /// Supply is recomputed next, from scratch, and nothing about it is carried between turns.
    /// Loam's upkeep and fade run *after*, so garrison upkeep reads the garrison that survived this
    /// turn's supply pass. `LegionSupply.Resolve` runs last: sector upkeep first, legion top-up
    /// and burn second, from whatever the same pool has left.
    /// </summary>
    static WorldState Pressure(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, int turn, ulong seed)
    {
        report.BeginPhase(Phases.Pressure);
        var afterSustain = SustainResolver.Run(world, commands, report, Phases.Pressure);
        var afterSupply = SupplyGraph.Run(afterSustain, report, Phases.Pressure);

        // Built the same way `Snapshot` derives `postures` from `stance` orders (:285-288): a plain
        // faction id → sector id map, last order per faction wins, never a service or a lookup.
        var ceded = commands
            .Where(c => c.Kind == WorldCommandKinds.Cede && c.SectorId != null)
            .GroupBy(c => c.CommanderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().SectorId!, StringComparer.Ordinal);

        var afterPressure = LoamPhases.Pressure(afterSupply, report, Phases.Pressure, turn, seed, ceded);
        return LegionSupply.Resolve(afterPressure, report, Phases.Pressure);
    }

    /// <summary>Calendar boundaries are rolled and reported; their effects belong to later modules.</summary>
    static WorldState Events(WorldState world, TurnReport report, int turn, ulong seed)
    {
        report.BeginPhase(Phases.Events);

        var roll = TurnCalendar.Roll(turn, seed);
        if (roll.WeekBoundary)
        {
            report.Add(Phases.Events, TurnReportKinds.Calendar, "week", roll.SpecialWeek ? "special" : "ordinary");
            if (roll.MonthBoundary)
                report.Add(Phases.Events, TurnReportKinds.Calendar, "month",
                    roll.Plague ? "plague" : roll.SpecialMonth ? "special" : "ordinary");
        }

        // world-map W58 (spec-sector-development.md §2, "the season is visible in the turn report"):
        // reported only on the turn a season actually changes — the same "week"/"month" precedent
        // immediately above, which reports only at its own boundary rather than every turn. Season
        // is never fogged (TurnCalendar.SeasonOf's own doc comment), so this needs no belief-side
        // twin: every faction computes the same season from the same turn. Turn 0 reports nothing —
        // there is no prior season to have changed from, matching every other boundary check's own
        // `turn > 0` guard in this file.
        if (turn > 0 && roll.Season != TurnCalendar.SeasonOf(turn - 1))
            report.Add(Phases.Events, TurnReportKinds.Calendar, "season", roll.Season.ToString());

        return world;
    }

    /// <summary>
    /// Every faction writes down what it can see. Visibility spans the turn's start *and* end, so a
    /// legion that marched through somewhere reports on it and a faction driven off its own ground
    /// remembers it as of this turn — neither needs a special case.
    /// </summary>
    static WorldState Observe(
        WorldState atStart, WorldState world, TurnReport report, int turn,
        IReadOnlyDictionary<string, IReadOnlySet<string>> marchedThrough)
    {
        report.BeginPhase(Phases.Intel);

        var before = world.Intel;
        var after = IntelRecorder.Observe(atStart, world, turn, marchedThrough);

        // Report only what changed, and only as a count: the map view reads belief from the
        // projection, and a line per sector per faction would drown every other event in the turn.
        foreach (var faction in after)
        {
            var previously = before.FirstOrDefault(f => f.FactionId == faction.FactionId);
            var learned = faction.Sectors.Count - (previously?.Sectors.Count ?? 0);
            if (learned > 0)
                report.Add(Phases.Intel, TurnReportKinds.Event, faction.FactionId, "intel.new:" + learned);
        }

        return world with { Intel = after };
    }

    /// <summary>
    /// Closes the turn: claims settle, and every legion starts the next one with a full march
    /// budget. Rout is deliberately untouched here — it was already spent at the top of the turn,
    /// so anything still flagged was broken during *this* turn and owes next turn's orders.
    /// </summary>
    static WorldState Snapshot(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, int turn,
        FusionRpg.Core.Power.PowerTuning? powerTuning = null, long? mythicClaimBonusRatePerMillion = null)
    {
        report.BeginPhase(Phases.Snapshot);

        // Claims settle here because everything they depend on — who is standing where, who is still
        // alive, which guards are left — is only decided once the rest of the turn has run.
        world = ClaimResolver.Run(world, commands, report, Phases.Snapshot, turn, powerTuning, mythicClaimBonusRatePerMillion);

        // Build resolves right after — the same reason, and so it sees this same turn's claim if
        // one just landed on the same sector (spec-loam-structures.md).
        world = BuildResolver.Run(world, commands, report, Phases.Snapshot);

        // Raise resolves right after Build, the same reason and the same order (world-map W51,
        // spec-sector-development.md §1) — a claim and a raise may land on the same sector in the
        // same turn.
        world = FusionRpg.Core.World.Growth.RaiseResolver.Run(world, commands, report, Phases.Snapshot, turn);

        // Develop resolves right after Raise, the same reason and the same order (world-map W52,
        // spec-sector-development.md §3) — a claim and a develop may land on the same sector in the
        // same turn.
        world = FusionRpg.Core.World.Growth.DevelopResolver.Run(world, commands, report, Phases.Snapshot);

        // Warden binding resolves right after Build, the same reason and the same order — a claim
        // and a bind-warden may land in the same turn (spec-loam-texture.md, world-stage W28).
        // RulesetVersion 13: Reveal drops every bind-warden as `warden.retired`, so nothing reaches
        // this call today. It stays wired as the reconciliation seam (WardenResolver's own note).
        world = WardenResolver.Run(world, commands, report, Phases.Snapshot);
        // commander-roster EP3.8: the legion commander seat — same Snapshot phase, for the same reason
        // (ownership and a legion that changed hands settle last).
        world = CommanderAttachResolver.Run(world, commands, report, Phases.Snapshot);

        // Posture changes land here, then the refill reads them — so a legion keeps the budget it
        // started the turn with and only pays for its new posture from the next turn. Digging in
        // *after* marching your full distance must not be free.
        var postures = commands
            .Where(c => c.Kind == WorldCommandKinds.Stance && c.EntityId != null && c.Stance != null)
            .GroupBy(c => c.EntityId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Stance!, StringComparer.Ordinal);

        return world with
        {
            CurrentTurn = turn,
            Entities = world.Entities
                .Select(e =>
                {
                    var stance = postures.TryGetValue(e.EntityId, out var ordered) ? ordered : e.Stance;
                    return e with { Stance = stance, MovementRemaining = MovementPolicy.BudgetFor(stance) };
                })
                .ToList()
        };
    }
}
