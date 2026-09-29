using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// **Checkpoint 3 — wave-1 acceptance** (world-map-plan.md). Twenty turns that actually play: a
/// legion marches, clears two guards, claims the sector, meets a warband head-on in the middle of a
/// lane, pushes on to the frontier, and finally has its frontier sector besieged by a third faction
/// walking directly onto it (base-defense siege-supply F1/F1b, 2026-09-05: a besieged sector still
/// supplies ITSELF now — "a base with stores is not a legion in the field" — so this is no longer a
/// `supply.cut`, see re-bless entry #14 below).
///
/// The scenario is scripted rather than improvised, so its value is in the invariants: one turn-log
/// row per turn, a stable hash sequence, and — the sharp one — the pure engine reproducing the
/// store's hashes from nothing but (seed, template, command log).
/// </summary>
public class WorldWaveOneAcceptanceTests : IDisposable
{
    const int Turns = 20;
    const ulong Seed = 4242;
    static readonly string[] Commanders = { "dave", "wild", "zomboss" };

    /// <summary>The one commander with no policy — the only one the auto-fill leaves alone.</summary>
    const string Human = "dave";

    // Re-blessed twice on 2026-08-22, both deliberately, for world-intel:
    //
    //   1. W19 — claiming a sector no longer rewrites its *authored* intel. That field is template
    //      input seeding the player's opening belief; live intel is per-faction now.
    //   2. W20 — the `Intel` phase landed and RulesetVersion went to 2, so every turn writes each
    //      faction's belief and the same commands produce a larger state than they did before.
    //
    //   3. W22 — every faction now starts believing what it can already see, not just the player,
    //      and a surveyed slot remembers which encounter dens there. Both were found by the
    //      projection's own tests: a warband could not describe the ground under its feet until a
    //      turn had been committed, and `guardState` alone cannot tell "cleared" from "never
    //      guarded".
    //
    //   4. A survey now records a sector's development level and each slot's state. Both were
    //      fields the wire already carried and nothing populated, so they read as a flat zero that
    //      looked exactly like a sector that genuinely had none — a trap set for whoever ships
    //      `sector-development`.
    //
    //   5. W37 — Zomboss's `PolicyId` became `frontier-rules`. `WorldCanonical` writes that field
    //      into the faction row, so the hash moves. **Nothing else did:** the scenario's command log
    //      is byte-identical before and after, verified by dumping it both ways, because
    //      `first-light` gives Zomboss no forces — a brain with nothing to command falls straight
    //      through to standing fast. That the map ships without an opponent army is a content gap,
    //      not an AI one; see WorldAiAcceptanceTests.
    //
    //   6. **A glimpse no longer erases a survey.** Found by playing twenty turns and reading them:
    //      a legion surveyed a sector, stepped one lane away, and forgot what was inside, because
    //      `IntelRecorder` wrote a whole new snapshot at whatever level it currently saw. Worse, the
    //      same bug destroyed the *template's authored intel* on the very first turn — Dave was
    //      supposed to start knowing Ember Hollow's insides and did not, past turn zero. Belief is
    //      hashed state, so keeping what a survey taught moves this.
    //
    //   7. **The map changed.** Verdant Shelf now hangs off Black Gate instead of Ash Waste. Ash
    //      Waste used to touch four of six sectors, so one march to the middle lit the whole map
    //      permanently and the fog was a three-turn opening rather than a condition — found by
    //      playing, not by testing. The map now has two chokepoints instead of one hub, and the far
    //      corner stays dark until somebody goes and looks.
    //
    //      (`Intel` also moved after `Snapshot` at RulesetVersion 3, which did *not* move this
    //      hash: belief converges within a turn, so only the intermediate ones changed.)
    //
    //   8. **loam-model (L2 + L4), 2026-08-23** — `WorldCanonical` gained three loam fields:
    //      `LoamStock`/`FractureIntensityMilli` on the sector row, `UpkeepHandicapMilli` on the
    //      faction row. This is the module's own budgeted golden move (its acceptance criteria call
    //      for exactly one), not a drift: `RulesetVersion` is unchanged, and `first-light`'s G-D
    //      minimum edit (a homeworld rootbed plus a starting stock) is what actually changes the
    //      sector row's content — the other two fields sit at their pre-loam defaults everywhere in
    //      this scenario. **Blessed once, after L4** (persistence), not at L2 alone: `Play(...)`
    //      commits each turn through `RpgStore`, so between L2 and L4 the homeworld's authored
    //      `LoamStock` was silently dropped on the first save (the new columns didn't exist yet),
    //      and a hash captured in that window would have blessed a lossy round trip rather than the
    //      real one. Caught by every store-vs-engine replay-parity test in this project going red
    //      at once — the same shape, not nine separate coincidences.
    //
    //   9. **loam-turn (L12-L15), 2026-08-23** — `Production` and `Pressure` stop being
    //      pass-throughs (`RulesetVersion` 4). Zomboss's warband still has nothing here to command
    //      (this scenario predates the AI stream giving him forces), so nothing about *combat* or
    //      *movement* changed; what moved is that `first-light`'s homeworld now seeps loam each turn,
    //      pays its own upkeep, and every other sector does too. This is the module's own budgeted
    //      golden move (its acceptance criteria call for exactly one) and the loam program's
    //      **second and last** — the first was `loam-model`'s field addition, which left
    //      `RulesetVersion` unchanged and needed no re-bless of its own reasoning here beyond entry
    //      #8 above.
    //
    //   10. **Post-gate L25, 2026-08-23** — `WorldCanonical` gains one field per new post-gate
    //       record slot, landed together as this program's third and last budgeted golden move
    //       (after `loam-model`'s and `loam-turn`'s): `WorldEntityMember.Role`, `WorldEntity.
    //       CarriedLoam`, `WorldSlot.StructureId`/`ConstructionTurnsRemaining`, `WorldSector.
    //       WardenBindingId`/`NeglectedTurns`, and `RememberedSlot.StructureId` on the belief side.
    //       Every field sits at its default (Fighter/0/null) for this scenario — no post-gate
    //       behaviour is wired yet, so nothing about the *play* changed, only the row shape the hash
    //       is taken over. `RulesetVersion` is unchanged, matching `loam-model`'s own precedent for
    //       a field-only addition (entry #8 above).
    //
    //   11. **L27, 2026-08-23 (`RulesetVersion` 4 -> 5)** — `Pressure` retires wound-based attrition:
    //       `SupplyGraph.Starve`/`AttritionWoundMilli` are gone, and `LegionSupply.Resolve` now runs
    //       after `LoamPhases.Pressure`'s sector-upkeep draw, burning a legion's own `CarriedLoam`
    //       while it stands outside its faction's supply and destroying it outright (not wounding
    //       it) the turn that reserve would go negative. A real behaviour change, not a field
    //       addition — Dave's legion, out of supply for stretches of this same 20-turn scenario,
    //       now burns/tops up loam instead of accumulating wounds. `first-light`'s and `two-hearths`'
    //       own `e-dave-legion-1` gained one bearer and a starting `CarriedLoam` (500, a placeholder
    //       bootstrap the same shape as G-D's homeworld stock) as the template-side minimum edit a
    //       zero-reserve legion would otherwise need to survive stepping off owned ground at all.
    //
    //   12. **L34, 2026-08-23** — `RememberedSlot.ConstructionTurnsRemaining` (belief side, mirroring
    //       the truth field the same way `StructureId` already does) was a genuine gap the L25
    //       batch did not anticipate: widening `Habitability.For`'s belief overload to recognize an
    //       active waystation needed construction status visible in belief, and nothing carried it
    //       yet. Field-only, `RulesetVersion` unchanged (same precedent as `loam-model`'s and L25's
    //       own) — no pre-existing scenario places a structure at all, so nothing about the *play*
    //       changed, only the row shape the hash is taken over.
    //
    //   13. **world-map W44, 2026-09-04** — `WorldCanonical` gains three sector fields:
    //       `RecruitStock`/`ProjectId`/`ProjectTurnsRemaining` (spec-sector-development.md §1/§3).
    //       This module's own budgeted field batch, `RulesetVersion` unchanged — matching
    //       `loam-model`'s and L25's own precedent for a field-only addition. Every field sits at
    //       its default (0/null/null) for this scenario — no growth or project behaviour is wired
    //       yet (that lands at W50/W52), so nothing about the *play* changed, only the row shape the
    //       hash is taken over.
    //
    //   14. **base-defense `siege-supply` F1/F1b, 2026-09-05** — `SupplyGraph.ConnectedSectors`
    //       stopped silently dropping a besieged sector from its own supply. The audit's own finding:
    //       a hostile force standing IN a sector you hold made that sector, and everything reachable
    //       only through it, vanish from `ConnectedSectors` — indistinguishable from a sector that
    //       was never yours. Fixed by splitting "traversable" (owned, not held against — gates BFS
    //       and Seat-seeding) from "besieged" (owned, held against — unioned back in post-BFS as a
    //       self-source: it can still supply itself, it just cannot be routed THROUGH). A real
    //       behaviour change, not a field addition (`RulesetVersion` unchanged — nothing about how a
    //       turn is hashed moved, only what a besieged sector's own supply state resolves to).
    //
    //       This scenario's own frontier claim is exactly the case the bug hid: Zomboss's band
    //       marches directly onto `ash-waste` (turns 11-12), which Dave claimed on turn 10. Under the
    //       old (buggy) behaviour that made `ash-waste` read as fully cut off, firing `supply.cut:`.
    //       Under the fix it correctly reads as besieged-but-self-supplying, firing
    //       `supply.besieged:` instead — `ash-waste` is first-light's only Seat-less sector
    //       (`SupplyTests.cs`'s own documented reason), so it is the only holding on this map that
    //       can ever be cut off at all, and this script's one hostile incursion stands directly in it
    //       rather than behind it. There is therefore no longer any scripted event in this 20-turn
    //       run that produces a genuine `supply.cut` — `Every_wave_one_verb_fires_somewhere_in_the_
    //       twenty_turns` was updated to require `supply.besieged:` in its place, matching what this
    //       script actually — and now correctly — produces.
    //
    //   15. **world-map W58 (`RulesetVersion` 6 → 7), 2026-09-05 — recorded retroactively, found
    //       missing while closing Phase 12's own checkpoint audit.** `growth.seatPulsePerWeek`
    //       moved off 0 (`gk-core/data/tuning/world.v5.json`, 0 → 20). This scenario holds two Seats across
    //       its own week boundaries (turns 7, 14): `homeworld` from turn 0, and the claimed,
    //       lair-cleared `ember-hollow` from turn 3 — both now accrue `WorldSector.RecruitStock`
    //       (a field `WorldCanonical` has hashed since entry #13), which moves this golden on its
    //       own, independent of entry #14's supply fix. `decisions.md`'s own W58 row named this
    //       exact test as the one golden the bump would move and predicted the reason correctly —
    //       the diff was genuinely checked in advance, only the matching numbered entry here was
    //       never written, leaving the file's own "every re-bless recorded" convention silently
    //       broken by one entry despite the substance being right.
    //
    //   16. **base-defense `siege-engagement`, module 20 (`RulesetVersion` 7 → 8), 2026-09-06** — a
    //       CONTINUING siege (no fresh `assault` order) used to fall through
    //       `ContactResolver.SectorContacts` into a generic `BattleKinds.Sector` request, which
    //       `DistrictAssaultResolver`'s own delegation guard sent straight to
    //       `PlaceholderBattleResolver` — a siege silently stopped being a real board fight the moment
    //       its attacker held still for one turn. `MovementPhase.BuildContactRequest` now checks
    //       `SiegeEngagement.IsUnderSiege` per contact and routes a genuinely besieged sector to
    //       `BattleKinds.District` instead, reusing `DistrictAssaultPhase.BuildBoard`. This scenario's
    //       own frontier claim (entry #14's own words) is exactly where it fires: Zomboss's band walks
    //       directly onto `ash-waste` at turns 11-12, which Dave claimed at turn 10 — before this fix
    //       that contact resolved as an abstract `Sector` fight; now it resolves on the real board
    //       (structures, elements, `BattleEngine.Resolve`), a genuinely different combat outcome from
    //       that point on. A real behaviour change, not a field addition.
    //
    //       Caught alongside a second, unrelated defect this same investigation surfaced:
    //       `The_pure_engine_reproduces_the_stored_hashes_from_the_command_log_alone`'s own replay loop
    //       called `TurnEngine.Step` with no resolver (defaulting to `PlaceholderBattleResolver`), while
    //       the store commits with `DistrictAssaultResolver.Instance`
    //       (`RpgStore.WorldTurns.cs`) — silently correct only because this script never fielded an
    //       `assault` order and, until this fix, never routed a contact into `BattleKinds.District`
    //       either. Fixed by passing `DistrictAssaultResolver.Instance` into that replay loop, which is
    //       what actually makes "pure engine reproduces stored hashes" a true statement instead of one
    //       that happened to hold by coincidence. `WorldTwentyTurnCheckpointTests.cs`'s own CP2 replay
    //       carried the identical gap (fixed there too) and re-ran green, unchanged — CP2's script never
    //       reaches a District battle at all, so that fix was a no-op for it, confirming this was a
    //       test-methodology defect and not a second production behaviour change.
    //
    //   17. **base-defense `siege-construction` 15.4 (`RulesetVersion` 8 → 9), 2026-09-06** — the two
    //       raw faucets (§2: `ironwork` from a CLEARED `shard-vein` slot, `rubble` from a CLEARED
    //       `material-seam` slot) landed in the SAME `Production` turn phase `LoamPhases` already
    //       runs. This scenario's own turn-9 order — `Clear, ash-waste, slot 2` (entry #14's own
    //       words: "ash-waste is first-light's only Seat-less sector") — clears exactly `ash-waste`'s
    //       `material-seam` slot, so from turn 10 onward `WorldSector.RubbleStock` accrues there for
    //       real for the first time. A genuine behaviour change, not a field addition — the same
    //       command log, for any world with a cleared `shard-vein`/`material-seam` slot, now produces
    //       a different state than version 8 did.
    //
    //       **A second, far more consequential defect this same wiring pass surfaced and fixed**:
    //       `WorldSector.RubbleStock`/`IronworkStock` (added 15.1, hashed by `WorldCanonical` and
    //       checked by `RpgStore.WorldGraphDiff.cs`'s own `SectorRowEquals` equivalence guard since
    //       the day they landed) had **no database columns at all** — `rpg_world_sectors`' own schema,
    //       every `INSERT`, and the read-back `SELECT` all omitted them entirely. Both fields were
    //       silently write-only: correct in memory for the lifetime of one `TurnEngine.Step` chain,
    //       but reset to 0 the moment any world was actually saved and reloaded, and undetectable by
    //       anything that had not yet made either field non-zero and then committed-and-reread it in
    //       the same operation — which is exactly what this task's own faucet phase was the first
    //       thing ever to do. Found the same way `development_level` on `rpg_world_faction_intel`
    //       (entry above `RpgStore.World.cs`'s own migration list) was: "found by the diffing writer's
    //       own equivalence guard, not designed in." Fixed with the SAME `EnsureColumn` migration
    //       pattern (`rubble_stock`/`ironwork_stock`, both `INTEGER NOT NULL DEFAULT 0` — an existing
    //       saved world reads both back at 0, exactly the world before either stock existed) plus the
    //       three sites that needed them: `CreateWorld`'s own initial INSERT, `DiffSectors`' own
    //       INSERT-OR-REPLACE (both in the affected files above), and the sector read-back SELECT.
    //
    //   18. **base-defense `siege-construction` 15.4b (`RulesetVersion` 9 → 10), audit F10, decision 48,
    //       2026-09-06** — `SiegeConstruction.AdvanceDepletion` (new) wires the already-shipped, already-
    //       tested pure function `BoardEconomy.AdvanceDepletionMilli` into the SAME `Production` phase
    //       15.4's own faucets already extended — the missing per-turn increment to `WorldSlot.
    //       SlotDepletionMilli` audit F10 named. A genuine behaviour change: EVERY owned sector's
    //       `Rootbed` slot now accrues depletion every turn it is owned (`LoamProduction.For`'s own base
    //       `SeepPerTurn` applies unconditionally to any owned Rootbed, so "yielded this turn" is simply
    //       "sector is owned" — re-derived independently here, `LoamProduction.For` itself untouched),
    //       and this scenario has owned sectors with Rootbed slots from turn 1 — so this is NOT a narrow,
    //       single-slot change like entry #17's, it moves the hash from the very first turn onward.
    //
    //       **A second, previously-undiscovered defect this same pass surfaced and fixed — the THIRD
    //       instance of the exact same bug class entries #17 and (before it) the
    //       `rpg_world_faction_intel.development_level` migration each already found**:
    //       `WorldSlot.SlotDepletionMilli` was hashed by `WorldCanonical` (`structure-state`'s own
    //       "slot-depletion" conditional row) and correctly diffed by `DiffSlots`' own plain record
    //       equality (`WorldSlot == WorldSlot` already compares every field, so the diff logic itself was
    //       never wrong) — but had NO database column anywhere: not on `rpg_world_slots`' own `CREATE
    //       TABLE`, not on `CreateWorld`'s INSERT, not on `DiffSlots`' own INSERT-OR-REPLACE, not on the
    //       read-back SELECT. Silently write-only since `structure-state` shipped, reset to 0 on every
    //       save/reload, invisible until THIS task's own `AdvanceDepletion` phase made it non-zero for
    //       the first time and tripped `DiffWorldGraphUnlocked`'s own equivalence guard with a hard
    //       `DebugAssertException` — caught on the very first run against this exact test, not guessed
    //       at. Fixed with the same `EnsureColumn` pattern across all four sites named above.
    //
    //   19. **base-defense `siege-ai` 17.11 (decision 47), 2026-09-07** — `DistrictLayout.ZoneOf`'s
    //       6-argument overload (17.11's own build: a warded lane's `WorldLane.WardLevel` widens the
    //       Rampart/Approach boundary, but only on the wedge facing the attacker's entry edge) was
    //       built and unit-tested WITHOUT moving this golden — its own closure note said so directly,
    //       because `DistrictAssaultResolver` did not call the 6-arg overload yet at that point, only
    //       the byte-identical 4-arg one every other caller still uses. It has SINCE been wired in
    //       (`DistrictAssaultResolver.cs`'s own `ZoneOf` call now passes `attackerEdge`/`wardExtraDepth`)
    //       — a genuine, deliberate, already-authorized behaviour change (decision 47), not a drift:
    //       this scenario's own district assault now computes Approach-zone membership asymmetrically
    //       for any lane carrying a non-zero `WardLevel`, which can shift which cells are legal for
    //       troop placement and, from there, real combat outcomes and final `WorldSector`/`WorldSlot`
    //       state. Confirmed NOT caused by anything else landing the same day — re-run in isolation
    //       with `DistrictAssaultResolver`'s own new, UNRELATED `aiTuning: SiegeTuningPolicy.Ai`
    //       argument (17.4/live-wiring, same day) temporarily disabled, and the mismatch persisted
    //       identically, isolating this specific change as the sole cause. Verified stable across
    //       multiple repeated runs (both truncated and full-length hash) before re-blessing — the
    //       first observation briefly showed a second, different value while an unrelated concurrent
    //       session was mid-edit in `Battle/BattleModels.cs`; re-checked only after that file's own
    //       diff stopped changing between runs.
    //
    //   20. **actor-hub-and-combat-power-solid-fixing `placeholder-battle-hub` (T20), 2026-09-13** —
    //       `PlaceholderBattleResolver` (the wave-1 Hp×Level "compare weight, apply the loss" stand-in)
    //       is deleted outright: `DistrictAssaultResolver` no longer delegates a non-district kind, or
    //       a district request it cannot really simulate, to it — every one of those cases now returns
    //       the pre-existing "refused, no-op" `BattleOutcome` (no `WinnerEntityId`, no `Sides`) instead
    //       of inventing a winner from a weight comparison. `TurnEngine.Step`'s own default resolver
    //       moved from the deleted class to `DistrictAssaultResolver.Instance` to match.
    //
    //       **This script's own guard-clears and claims were never real combat — they were this exact
    //       fiction succeeding for real, every time**, because `PlaceholderBattleResolver.ResolveGuard`
    //       had no losing case for an attacker with any strength left at all. Turn 1/2's `Clear` at
    //       `ember-hollow`, turn 3's `Claim` there, turn 9's `Clear` at `ash-waste`, and turn 10's
    //       `Claim` there all now refuse rather than succeed — there is no real engine for
    //       `BattleKinds.Guard`/`Sector`/`Lane` yet (tracked, not built, under the provisional
    //       `world-actor-combat` program this task's own spec names). Cascades further than those five
    //       turns: Dave's legion never actually holds `ash-waste`, so Zomboss's turn-11/12 march onto
    //       it at turns 11-12 is no longer a siege of Dave's own ground (it is simply walking onto an
    //       unclaimed, still-guarded sector, which a Guard entity does not even project zone-of-control
    //       against) — the `supply.besieged:`/`halt:zoc:` events entries #14/#16 above carefully
    //       documented for this exact incursion no longer fire either. This is a real, honest
    //       narrowing of what this specific 20-turn script can prove, not a drift: it is the same
    //       class of finding as `cold-equip-one`'s stub-equip gap and this program's other stub-hygiene
    //       tasks, just landing on the flagship wave-1 acceptance scenario instead of a smaller corner.
    //       `Every_wave_one_verb_fires_somewhere_in_the_twenty_turns` and
    //       `The_campaign_ends_with_Dave_holding_what_he_took` were updated to match what this script
    //       can actually still demonstrate; re-scripting it around a real combat kind (`Assault`/
    //       `BattleKinds.District`) or restoring guard-clear coverage is `world-actor-combat`'s own
    //       future work, not this program's (`actor-hub-and-combat-power-solid-fixing`'s own spec locks
    //       "Do not design or ship world stage combat... in this program").
    //
    // The plan expected one re-bless. Many more were needed since — most recently entry #20 above —
    // each for a behaviour change or a budgeted field batch rather than a drift, and each recorded
    // here. Protecting the hash in any of them would have meant shipping something known to be wrong.
    // Re-bless entry #21 (identity-rename T13): the world template's two empire factions now take
// their names from the lead-names registry (player = `lead_summoner`, antagonist = `lead_antagonist`)
// instead of the literals "Dave" / "Dr. Zomboss", so the scenario hash moves. Nothing else in the
// scenario changed: the pre-change run of the Core World subset was 1247/1247, and the same
// template shape, sectors and orders are in play. Deliberate re-bless, not a paste.
const string GoldenFinalHash = "307b3d0e5dfc4c0fa3de366d4d0a8937ec15e9648511b646255dcb88a08d6d6f";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldWaveOneAcceptanceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>
    /// `first-light` plus a mid-campaign roster: Dave's legion is a real force rather than the three
    /// starting conscripts, and Zomboss has a warband on the board. Both are authored here rather
    /// than played into existence — recruitment belongs to a later wave, and the scenario needs a
    /// board where every wave-1 verb can actually fire.
    /// </summary>
    static WorldState Scenario(string worldId)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, Seed, worldId);

        var legion = world.Entities.Single(e => e.EntityId == "e-dave-legion-1") with
        {
            Members = Enumerable.Range(0, 5)
                .Select(_ => new WorldEntityMember { SpeciesId = "peashooterzombie", Level = 3, Hp = 260 })
                .ToList()
        };

        // Replaced rather than appended since 2026-08-22: the template ships this band now, and
        // appending a second one with the same id fails validation. The scenario still wants a
        // heavier version of it than the map gives a new player.
        var zomboss = new WorldEntity
        {
            EntityId = "e-zomboss-band-1",
            Kind = WorldEntityKind.Warband,
            OwnerFactionId = "zomboss",
            AtSectorId = "black-gate",
            Stance = "march",
            MovementRemaining = 1000,
            Members = Enumerable.Range(0, 4)
                .Select(_ => new WorldEntityMember { SpeciesId = "normalzombie", Level = 4, Hp = 300 })
                .ToList()
        };

        return WorldValidation.Validate(world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == legion.EntityId ? legion : e)
                .Select(e => e.EntityId == zomboss.EntityId ? zomboss : e)
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        });
    }

    static WorldCommand Cmd(string commander, int turn, string kind) => new()
    {
        CommanderId = commander,
        CommandId = $"t{turn}-{commander}",
        Kind = kind
    };

    /// <summary>The script. Anything a commander is not told to do this turn, it stands fast.</summary>
    static IReadOnlyList<WorldCommand> ScriptFor(int turn)
    {
        var dave = Cmd("dave", turn, WorldCommandKinds.StandFast);
        var wild = Cmd("wild", turn, WorldCommandKinds.StandFast);
        var zomboss = Cmd("zomboss", turn, WorldCommandKinds.StandFast);

        dave = turn switch
        {
            0 => dave with { Kind = WorldCommandKinds.Move, EntityId = "e-dave-legion-1", LanePath = new[] { "l-home-ember" } },
            1 => dave with { Kind = WorldCommandKinds.Clear, EntityId = "e-dave-legion-1", SectorId = "ember-hollow", SlotIndex = 2 },
            2 => dave with { Kind = WorldCommandKinds.Clear, EntityId = "e-dave-legion-1", SectorId = "ember-hollow", SlotIndex = 3 },
            3 => dave with { Kind = WorldCommandKinds.Claim, EntityId = "e-dave-legion-1", SectorId = "ember-hollow" },
            // Two turns on the ley lane: the first ends in the crossing, the second finishes it.
            4 or 5 => dave with { Kind = WorldCommandKinds.Move, EntityId = "e-dave-legion-1", LanePath = new[] { "l-ember-ash" } },
            9 => dave with { Kind = WorldCommandKinds.Clear, EntityId = "e-dave-legion-1", SectorId = "ash-waste", SlotIndex = 2 },
            10 => dave with { Kind = WorldCommandKinds.Claim, EntityId = "e-dave-legion-1", SectorId = "ash-waste" },
            _ => dave
        };

        // The wild pack is dug in where the template left it, so it has to break camp before it can
        // march — which costs it the turn it gives the order (world-movement §What `hold` is for).
        if (turn == 3)
            wild = wild with { Kind = WorldCommandKinds.Stance, EntityId = "e-wild-pack-1", Stance = "march" };

        // Then it marches out to meet the legion head-on in the middle of the ley lane.
        if (turn == 4)
            wild = wild with { Kind = WorldCommandKinds.Move, EntityId = "e-wild-pack-1", LanePath = new[] { "l-ember-ash" } };

        // Zomboss comes up the rift behind them and squats on the frontier sector.
        if (turn is 11 or 12)
            zomboss = zomboss with { Kind = WorldCommandKinds.Move, EntityId = "e-zomboss-band-1", LanePath = new[] { "l-ash-black" } };

        return new[] { dave, wild, zomboss };
    }

    List<string> Play(string worldId)
    {
        var (ok, reason, _) = _store.CreateWorld(1, Scenario(worldId));
        Assert.True(ok, reason);

        var hashes = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            _store.SubmitWorldCommands(worldId, ScriptFor(turn));

            // Every commander ends the *same* turn. The scripted factions commit **first**: an
            // explicit commit speaks for a faction and keeps `ai-commander`'s auto-fill out of it,
            // which is what lets a scenario script what the wild and Zomboss do. The human commits
            // last, and that is the one that releases the barrier.
            var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
            WorldTurnCommitResult last = default!;
            foreach (var commander in Commanders.Where(c => c != Human).Concat(new[] { Human }))
                last = _store.CommitWorldTurn(worldId, commander, open);

            Assert.True(last.Advanced, $"turn {turn} did not advance");
            hashes.Add(last.StateHash!);
        }

        return hashes;
    }

    IEnumerable<TurnReportEntry> AllEntries(string worldId) =>
        Enumerable.Range(0, Turns).SelectMany(t => _store.GetWorldTurnReport(worldId, t)?.Entries ?? Array.Empty<TurnReportEntry>());

    /// <summary>
    /// Re-bless entry #20 (T20, `placeholder-battle-hub`): "clear" (a `guard:` battle line), "claim",
    /// "zone-of-control", and "supply besieged" are gone from the required list. All four depended on
    /// this script's `Clear`/`Claim` orders actually succeeding against `BattleKinds.Guard`, which had
    /// no real engine and only ever "worked" because the deleted placeholder's guard fight had no
    /// losing case. `crossing` (a real `BattleKinds.Lane` contact, resolved honestly as refused rather
    /// than won) and every non-combat verb below are unaffected and still required.
    /// </summary>
    [Fact]
    public void Every_wave_one_verb_fires_somewhere_in_the_twenty_turns()
    {
        Play("cp3-verbs");
        var entries = AllEntries("cp3-verbs").ToList();

        var verbs = new (string Name, Func<TurnReportEntry, bool> Fired)[]
        {
            ("march", e => e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("arrival:")),
            ("crossing", e => e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith("lane:")),
            // spec-loam-legions.md (L27): wound-based attrition is retired; a legion beyond supply
            // now burns carried loam (surviving on its reserve) or, if that reserve runs out, is
            // destroyed outright rather than bled slowly.
            ("legion burns or starves", e => e.Kind == TurnReportKinds.Event
                && (e.Detail.StartsWith("legion.burn:") || e.Detail.StartsWith("legion.starved:")))
        };

        var missing = verbs.Where(v => !entries.Any(v.Fired)).Select(v => v.Name).ToList();
        Assert.True(missing.Count == 0, "never fired: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_scenario_leaves_one_turn_log_row_per_turn_and_nothing_beyond()
    {
        Play("cp3-log");

        Assert.Equal(Turns, _store.GetActiveWorld(1)!.CurrentTurn);
        for (var turn = 0; turn < Turns; turn++)
            Assert.NotNull(_store.GetWorldTurnLog("cp3-log", turn));
        Assert.Null(_store.GetWorldTurnLog("cp3-log", Turns));
    }

    [Fact]
    public void The_same_script_and_seed_replay_to_the_same_twenty_hashes()
    {
        Assert.Equal(Play("cp3-a"), Play("cp3-b"));
    }

    [Fact]
    public void The_pure_engine_reproduces_the_stored_hashes_from_the_command_log_alone()
    {
        var stored = Play("cp3-replay");

        // The resolver must match what the store actually commits with (RpgStore.WorldTurns.cs) or
        // "pure engine" would silently mean "pure engine, minus whichever battle kind the store's
        // resolver wasn't the default" — a gap base-defense `siege-engagement` (2026-09-06) found the
        // hard way: this scenario fields no `assault` order anywhere in its script, so before that
        // module's own dispatch fix no `BattleKinds.District` battle had ever fired here, and the two
        // resolvers had always agreed by coincidence, not by construction.
        var world = Scenario("cp3-replay");
        var replayed = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            var result = TurnEngine.Step(world, _store.ListWorldCommands("cp3-replay", turn), Seed, DistrictAssaultResolver.Instance);
            world = result.World;
            replayed.Add(result.StateHash);
        }

        Assert.Equal(stored, replayed);
    }

    /// <summary>
    /// The golden. It has no meaning on its own — it is a tripwire: if the ruleset changes without
    /// anyone deciding to change it, this is what notices. Re-bless it deliberately, with the reason
    /// in the commit message, never by pasting whatever the run produced.
    /// </summary>
    [Fact]
    public void The_scenario_hashes_to_its_golden()
    {
        Assert.Equal(GoldenFinalHash, Play("cp3-golden").Last());
    }

    /// <summary>
    /// Renamed from `The_campaign_ends_with_Dave_holding_what_he_took` at re-bless entry #20 (T20,
    /// `placeholder-battle-hub`): Dave's scripted turn-1/2 `Clear` orders at `ember-hollow` targeted
    /// `BattleKinds.Guard`, which has no real engine and only ever succeeded because the deleted
    /// placeholder's guard fight had no losing case — his turn-3 `Claim` there now refuses too
    /// (`claim.guarded`, the sector still shows a live guard). This no longer proves "holding what he
    /// took"; it pins the honest, refused alternative instead, so a future real Guard engine landing
    /// under `world-actor-combat` is a deliberate change to this test, not a silent one.
    /// </summary>
    [Fact]
    public void The_campaign_leaves_the_still_guarded_sector_unclaimed()
    {
        Play("cp3-end");
        var world = _store.LoadWorldState("cp3-end")!;

        var ember = world.Sectors.Single(s => s.SectorId == "ember-hollow");
        Assert.Null(ember.OwnerFactionId);
        Assert.Equal(GuardState.Intact, ember.Slots[2].GuardState);
        Assert.Equal(GuardState.Intact, ember.Slots[3].GuardState);
    }
}
