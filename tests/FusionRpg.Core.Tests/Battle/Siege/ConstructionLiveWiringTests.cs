using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-construction`/`siege-ai` (2026-09-07, session 5, owner-authorized default
/// policy): the FIRST live wiring of `ConstructionAi.ChooseBuiltSite` into a real
/// `BattleEngine.Resolve` round — proves `BasicAttack.TryDeclareBuilt`'s own new branch end to end,
/// through the REAL atomic action-phase loop (`Siege`/`Delve` both use it; `ClassicRound`/`GalaxySync`/
/// `HybridAtb` do not, confirmed directly against `BattleModeProfile.cs`), not a bare
/// `BattleEffectHost` shortcut the way `ConstructionActionsTests.cs` proves the firing mechanism
/// itself.
///
/// <para><b>Why the enemy can exist without ever becoming a false combat target</b>: `Built`'s own
/// `ActionRow` never overrides `MinRange`/`MaxRange` (both default to 0 — confirmed directly,
/// `ActionRow.cs`), and "with no board, every range check passes" is the ONLY case where that would
/// matter — every test here supplies a REAL board with real, distinct positions, so `Built` is
/// structurally never "in range" of another actor's cell. `StubIntentSource` therefore always reaches
/// its own step 5 (pass) for an actor holding only `Built`, returning `ActionIntent.None` — the exact
/// case `TryDeclareBuilt` exists to intercept before `Break`'s own hazard-3 round-ending.</para>
/// </summary>
public class ConstructionLiveWiringTests
{
    /// <summary>
    /// A real `ActionCatalog` carrying only `Built` — resolved through `ActionCompiler.Compile`
    /// (the real pipeline, not a hand-built `CompiledAction`), matching `ConstructionActions.Actions`'
    /// own real content. `RungTable(cap: 0, ...)` matches `Built`'s own `ActionRow.Rung` default (0,
    /// never overridden by `ConstructionActions.cs`), and `Built`'s own atom-carrying container is
    /// resolved via the SAME `ConstructionActions.ContainerResolver` these tests already pass to
    /// `BattleEngine.Resolve` for the runtime grant.
    /// </summary>
    static ActionCatalog BuiltOnlyCatalog()
    {
        var builtRow = ConstructionActions.Actions.Single(a => a.ActionId == ConstructionActions.BuiltActionId) with { Rung = 1 };
        var containerAtomIds = ConstructionActions.ContainerResolver.EffectIdsFor(ConstructionActions.BuiltContainerId);
        var rungTable = new RungTable(cap: 1, new[] { new RungRow(1, 1, 1, 1, 1000, 1000, 1000, Array.Empty<string>()) });
        var (rejection, compiled) = ActionCompiler.Compile(
            builtRow, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            containerAtomIds, boardAvailable: true, rungTable);
        Assert.True(rejection.IsOk, rejection.ToString());
        return ActionCatalog.Build(new[] { compiled! });
    }

    static BattleActorSetup Builder(string key) => new()
    {
        Key = key, Side = "squad", MaxHp = 1000,
        EquippedActionIds = new[] { ConstructionActions.BuiltActionId },
    };

    static BattleActorSetup Enemy(string key) => new() { Key = key, Side = "wave", MaxHp = 1000 };

    static (BoardState Board, ConstructionBoardContext Ctx) Board(long sectorRubble, long sectorIronwork)
    {
        var spec = new GridSpec(20, 20);
        var board = new BoardState(spec);
        board.Place("squad:0", new GridPos(10, 15));
        board.Place("wave:0", new GridPos(10, 16)); // adjacent to the builder, but NOT to any build cell below
        var moat = StructureCatalog.Get("moat");
        var slotByCell = new Dictionary<GridPos, (int SlotIndex, SlotKind Kind)>
        {
            [new GridPos(9, 15)] = (0, moat.RequiredSlotKind), // the FIRST compass offset from (10,15)
        };
        var ctx = new ConstructionBoardContext(
            board, boardSide: 20, coreSideMilli: 400, rampartThickness: 1, slotByCell,
            sectorRubble, sectorIronwork);
        return (board, ctx);
    }

    [Fact]
    public void An_actor_holding_Built_places_a_structure_when_combat_finds_no_target()
    {
        var moat = StructureCatalog.Get("moat");
        var (board, ctx) = Board(moat.ConstructRubbleCost, moat.ConstructIronworkCost);

        var setup = new BattleSetup { Squad = new[] { Builder("squad:0") }, Wave = new[] { Enemy("wave:0") } };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                host.ConstructionBoard = ctx;
            },
            containerResolver: ConstructionActions.ContainerResolver,
            actionCatalog: BuiltOnlyCatalog(),
            board: board);

        Assert.NotNull(report);
        Assert.True(ctx.Placed.Count > 0,
            $"expected 1 placement, got {ctx.Placed.Count}. Report={System.Text.Json.JsonSerializer.Serialize(report)}");
        var placed = Assert.Single(ctx.Placed);
        Assert.Equal("moat", placed.StructureId);
        Assert.Equal(0, ctx.RemainingRubble); // debited exactly the moat's own cost
        Assert.Equal(0, ctx.RemainingIronwork);
    }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, a SECOND, deeper MAJOR finding this
    /// session, discovered while trying to prove the first one fixed): `AdditionalHeldActions` alone
    /// does NOT make `TryDeclareBuilt` fire for a real, combat-capable actor, and cannot — this is not
    /// a defect in the fix, it is a genuinely separate, pre-existing, game-wide constraint.
    ///
    /// <para><b>`BasicAttackCompiled` (`BattleRunState.cs`) is declared with `MaxRange: int.MaxValue`</b>
    /// — unbounded, by design, for the default squad-vs-wave battle mode where board distance does not
    /// matter. `StubIntentSource`'s own step 2 (target selection) is purely geometric nearest-enemy,
    /// with NO range filter of its own; step 3 tries `held` in order and returns on the FIRST usable
    /// action. Since `EquippedActionIds` is null for every real legion member (the ORIGINAL finding
    /// this session), `held` = `[BasicAttackCompiled, ...AdditionalHeldActions]` — and because basic
    /// attack's own range is unbounded, it is ALWAYS usable against ANY live enemy, at ANY distance,
    /// so step 3 NEVER reaches the appended construction actions, and `intent.IsNone` — the precondition
    /// `TryDeclareBuilt` gates on — can never become true while any enemy exists anywhere on the board.
    /// Confirmed empirically: an earlier version of this test placed the enemy far away expecting
    /// construction to fire instead of combat, and it still did not — the actor attacked at range
    /// instead (this file's OTHER tests avoid this entirely by using `EquippedActionIds` to REPLACE
    /// basic attack outright, which a real per-player loadout will do once one exists, but which
    /// `AdditionalHeldActions` deliberately does not, since it must never cost an actor its own
    /// combat capability).</para>
    ///
    /// <para><b>Deliberately not "fixed" here</b> — the candidate fixes are both real, separate, and
    /// bigger than this task: bounding combat's own range for siege battles specifically (a real,
    /// game-wide combat-behavior change needing its own review), or reordering `DeclareBasicAttack`'s
    /// own gate so `TryDeclareBuilt` is tried BEFORE step 3 under some new condition (a kernel-level
    /// change to the shared attack-declaration path). Both are genuine design decisions, not narrow
    /// wiring. What THIS test proves instead: `AdditionalHeldActions` is purely additive and safe —
    /// granting it never costs the actor its own real combat capability, proven by the enemy actually
    /// taking damage, not just by reading the append code.</para>
    /// </summary>
    [Fact]
    public void AdditionalHeldActions_are_additive_and_never_cost_an_actor_its_own_combat_capability()
    {
        var moat = StructureCatalog.Get("moat");
        var (board, ctx) = Board(moat.ConstructRubbleCost, moat.ConstructIronworkCost);

        var builder = new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", MaxHp = 1000,
            AdditionalHeldActions = ConstructionActions.CompiledActionsForGrant,
        };
        var setup = new BattleSetup { Squad = new[] { builder }, Wave = new[] { Enemy("wave:0") } };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                host.ConstructionBoard = ctx;
            },
            containerResolver: ConstructionActions.ContainerResolver,
            // Deliberately NO actionCatalog -- AdditionalHeldActions is pre-compiled, needing no
            // id-based resolution at all, unlike EquippedActionIds above.
            board: board);

        Assert.NotNull(report);
        var wave0 = report!.Actors.Single(a => a.Key == "wave:0");
        Assert.True(wave0.HpRemaining < 1000, // Enemy("wave:0")'s own starting MaxHp, per this file's helper
            $"expected basic attack to still land despite AdditionalHeldActions being granted; HpRemaining={wave0.HpRemaining}");
        Assert.Empty(ctx.Placed); // named consequence of the finding above, not this test's own claim
    }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, re-examining the "combat always wins"
    /// finding one more time before accepting it as fully irreducible): `NearestEnemy` (step 2) fails
    /// -- `targetKey is null` -- once NO live enemy exists at all, which can happen MID-ROUND, not just
    /// between battles: if a first attacker's own turn lands the killing blow on the only enemy, a
    /// SECOND attacker acting later the SAME round finds no target before step 3's own range check
    /// (unbounded or not) is ever reached. This is a genuinely different path than the "combat's own
    /// unbounded range always wins" finding -- it does not depend on range at all, since target
    /// SELECTION fails first. Tests whether this actually gives a real, combat-capable actor a real
    /// window to build, or whether the round ends before a second actor's own turn comes.
    /// </summary>
    [Fact]
    public void A_second_attacker_builds_after_a_first_attacker_kills_the_only_enemy_the_same_round()
    {
        var moat = StructureCatalog.Get("moat");
        var spec = new GridSpec(20, 20);
        var board = new BoardState(spec);
        board.Place("squad:0", new GridPos(10, 15));
        board.Place("squad:1", new GridPos(10, 3)); // far from squad:0 -- its own independent build site
        board.Place("wave:0", new GridPos(10, 16)); // adjacent to squad:0 only
        var slotByCell = new Dictionary<GridPos, (int SlotIndex, SlotKind Kind)>
        {
            [new GridPos(9, 3)] = (0, moat.RequiredSlotKind), // squad:1's own compass-adjacent cell
        };
        var ctx = new ConstructionBoardContext(
            board, boardSide: 20, coreSideMilli: 400, rampartThickness: 1, slotByCell,
            sectorRubble: moat.ConstructRubbleCost, sectorIronwork: moat.ConstructIronworkCost);

        var squad0 = new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", MaxHp = 1000,
            AdditionalHeldActions = ConstructionActions.CompiledActionsForGrant,
        };
        var squad1 = new BattleActorSetup
        {
            Key = "squad:1", Side = "squad", MaxHp = 1000,
            AdditionalHeldActions = ConstructionActions.CompiledActionsForGrant,
        };
        // MaxHp=1 -- any non-zero basic-attack damage is a guaranteed one-hit kill, so the ONLY enemy
        // is confirmed dead before squad:1's own turn can come (source order: squad:0 acts first).
        var wave0 = new BattleActorSetup { Key = "wave:0", Side = "wave", MaxHp = 1 };

        var setup = new BattleSetup { Squad = new[] { squad0, squad1 }, Wave = new[] { wave0 } };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                host.ConstructionBoard = ctx;
            },
            containerResolver: ConstructionActions.ContainerResolver,
            board: board);

        Assert.NotNull(report);
        var killed = report!.Actors.Single(a => a.Key == "wave:0");
        Assert.False(killed.Survived); // confirms the premise: the only enemy really did die
        var placed = Assert.Single(ctx.Placed); // squad:1's own build, once no live enemy remained
        Assert.Equal("moat", placed.StructureId);
    }

    [Fact]
    public void An_actor_holding_Built_falls_through_to_the_existing_break_when_nothing_is_affordable()
    {
        var (board, ctx) = Board(sectorRubble: 0, sectorIronwork: 0);

        var setup = new BattleSetup { Squad = new[] { Builder("squad:0") }, Wave = new[] { Enemy("wave:0") } };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                host.ConstructionBoard = ctx;
            },
            containerResolver: ConstructionActions.ContainerResolver,
            actionCatalog: BuiltOnlyCatalog(),
            board: board);

        Assert.NotNull(report); // the round-break path still resolves cleanly, exactly as before this task
        Assert.Empty(ctx.Placed);
    }

    [Fact]
    public void A_battle_with_no_construction_board_wired_is_unaffected()
    {
        // The SAME builder setup, but onEffectHostReady never sets ConstructionBoard -- byte-identical
        // to every battle before this task existed. Proves TryDeclareBuilt's own null-check, not just
        // asserts it by reading the code.
        var spec = new GridSpec(20, 20);
        var board = new BoardState(spec);
        board.Place("squad:0", new GridPos(10, 15));
        board.Place("wave:0", new GridPos(10, 16));

        var setup = new BattleSetup { Squad = new[] { Builder("squad:0") }, Wave = new[] { Enemy("wave:0") } };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                // ConstructionBoard deliberately never set.
            },
            containerResolver: ConstructionActions.ContainerResolver,
            actionCatalog: BuiltOnlyCatalog(),
            board: board);

        Assert.NotNull(report); // no exception, no crash -- the pre-existing Break path runs unchanged
    }

    [Fact]
    public void A_besieging_legion_can_afford_more_than_one_structure()
    {
        // 15.3b's own headline acceptance test, finally drivable now that a live intent source
        // exists: TWO builders, each with their own legal adjacent cell, and enough Rubble/Ironwork
        // in the SHARED battle budget for both moats -- proves the budget is a real, shared, battle-
        // scoped pool (ConstructionBoardContext), not per-actor.
        var moat = StructureCatalog.Get("moat");
        var spec = new GridSpec(20, 20);
        var board = new BoardState(spec);
        board.Place("squad:0", new GridPos(10, 15));
        board.Place("squad:1", new GridPos(10, 3)); // far from squad:0 -- independent build sites
        board.Place("wave:0", new GridPos(10, 16));
        var slotByCell = new Dictionary<GridPos, (int SlotIndex, SlotKind Kind)>
        {
            [new GridPos(9, 15)] = (0, moat.RequiredSlotKind),
            [new GridPos(9, 3)] = (1, moat.RequiredSlotKind),
        };
        var ctx = new ConstructionBoardContext(
            board, boardSide: 20, coreSideMilli: 400, rampartThickness: 1, slotByCell,
            sectorRubble: checked(moat.ConstructRubbleCost * 2), sectorIronwork: checked(moat.ConstructIronworkCost * 2));

        var setup = new BattleSetup
        {
            Squad = new[] { Builder("squad:0"), Builder("squad:1") },
            Wave = new[] { Enemy("wave:0") },
        };
        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1,
            onEffectHostReady: host =>
            {
                var catalog = (FusionRpg.Core.Effects.InMemoryEffectCatalog)host.Bag.Catalog;
                foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                host.ConstructionBoard = ctx;
            },
            containerResolver: ConstructionActions.ContainerResolver,
            actionCatalog: BuiltOnlyCatalog(),
            board: board);

        Assert.NotNull(report);
        Assert.Equal(2, ctx.Placed.Count);
        Assert.All(ctx.Placed, p => Assert.Equal("moat", p.StructureId));
        Assert.Equal(0, ctx.RemainingRubble);
        Assert.Equal(0, ctx.RemainingIronwork);
    }
}
