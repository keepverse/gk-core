using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.World.District;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// base-defense `siege-resolver` (module 15, spec-siege-resolver.md): the `IBattleResolver`
/// implementation for <see cref="BattleKinds.District"/> — the world/battle join. Builds a real board
/// from <see cref="DistrictLayout"/>, places combatants (and any structures standing on the district's
/// slots) on it, runs a real <see cref="BattleEngine.Resolve"/>, evaluates <see cref="SiegeObjective"/>,
/// and translates the result back into a <see cref="BattleOutcome"/>. Every non-district kind, and
/// every district request this resolver cannot really simulate (no board projected, no living
/// attacker, or a board too small for the forces on it), returns the same refused/no-op
/// <see cref="BattleOutcome"/> shape (<c>BattleId</c> only — no winner, no sides) rather than
/// inventing a weight-comparison result — `actor-hub-and-combat-power-solid-fixing`'s
/// `placeholder-battle-hub` (T20): there is no real engine for those kinds yet, so there is no
/// winner, not a fake one. The early return IS the feature-absence guarantee, provable by
/// construction rather than by a golden diff.
///
/// <para><b>No raw `IIntentSource` instance is ever constructed here — but a real, scored AI IS wired,
/// via a different seam (2026-09-07, siege-ai, owner-authorized).</b> `BattleEngine.Resolve`'s
/// `aiTuning: SiegeTuningPolicy.Ai` argument below opts every actor in this battle into
/// `BattleRunState.DefaultAiIntentSource` (a real `SiegeAiIntentSource`, built internally once
/// `BattleRunState`'s own view/`Cooldowns`/`CostLedger` exist — no external caller can construct one
/// directly, since `IBattleView`'s only real implementor is private to `BattleEngine`).
/// `DeclareBasicAttack`'s own fallback chain (`intentSource ?? state.DefaultAiIntentSource ?? new
/// StubIntentSource(...)`) tries it before the crude nearest-enemy stub, for both sides — there is no
/// human-played side in this auto-resolved path, so `SiegeIntentSource`'s own played-vs-AI dispatch
/// wrapper isn't needed here at all. Wiring an actual PLAYED side through that wrapper remains real,
/// deferred work for whichever module first has a live human-input channel to plug into it
/// (`siege-stage`, the FE-Phaser piece, unrelated to whether the backend AI itself is wired) — not this
/// one, and not what the `aiTuning` argument below is for.</para>
///
/// <para><b>Two things this pass deliberately does NOT solve</b> — named once, in `tasks/base-defense-todo.md`
/// rather than guessed through under time pressure, and restated here so the code and the task list
/// agree: (1) a structure's `BattleActorSetup.Side` is a single, reversible convention (every structure
/// enters on the DEFENDER's side, so an attacker may destroy it and a defender never targets its own
/// wall) rather than a deep rule; (2) <see cref="SiegeObjective.SiegeCombatant.InCore"/> cannot be read
/// from a real final board position — `BattleReport`/`BattleActorResult` carry none, for any battle kind
/// (verified directly) — so every living combatant is passed as `InCore: true`, which only actually
/// matters for the DEFENDER side (`SiegeObjective.Evaluate` never reads the attacker's own `InCore`):
/// a defender who survives the fight is treated as still holding the Core regardless of where the round
/// loop actually left them standing.</para>
/// </summary>
public sealed class DistrictAssaultResolver : IBattleResolver
{
    /// <summary>The store-free resolver: every legion member fights on flat, level-derived stats.</summary>
    public static readonly DistrictAssaultResolver Instance = new();

    /// <summary>
    /// D4 (solid-remediation T3.3): supplies a legion member's real Hub inputs — aptitude allocation,
    /// bound equip atoms, star/loyalty — so a siege composes the same specimen the same way a web match
    /// does.
    ///
    /// <para><b>Why a delegate and not a store read.</b> This resolver is Core-only and
    /// statics-constructible; `RpgStore` lives in `FusionRpg.Data` and reaching it from here would cross
    /// the DAL boundary `guard-dal.ps1` exists to hold. So Core declares the need and the Data layer
    /// injects the implementation at its own call site, which is dependency inversion rather than a
    /// layering exception.</para>
    ///
    /// <para>Null keeps the previous behaviour exactly: a member with no provider composes from
    /// level-derived baselines alone, which is what every siege did before this seam existed.</para>
    /// </summary>
    public Func<WorldEntityMember, Battle.BattleHubInputs?>? HubInputsFor { get; init; }

    /// <summary>
    /// T74/A33 (`spec-battle-holder-wiring.md` §2c): the member's own unlock state, keyed by
    /// `InstanceId`, so a siege prices a held action at its holder's real rung instead of the content's
    /// authored one.
    ///
    /// <para><b>The same inversion as <see cref="HubInputsFor"/>, for the same reason</b>: this
    /// resolver is Core-only and must never read a store, so the Data layer injects the store-backed
    /// provider beside the Hub one. It takes an `InstanceId` rather than a whole
    /// <see cref="WorldEntityMember"/> because the resolver, not the provider, is what knows each
    /// actor's generated key (`{entityId}:{index}` — see <see cref="BuildAnimateSetups"/>), and the
    /// provider should not have to re-derive it.</para>
    ///
    /// <para>Null keeps the previous behaviour exactly: no provider means no rung read at all, which is
    /// what every siege did before this seam existed. Resolved lazily, per key the engine actually
    /// prices — never materialised for the whole match (spec §4's own "not a second cache").</para>
    /// </summary>
    public Func<string, UnlockState>? UnlockStateFor { get; init; }

    /// <summary>The tuning half of <see cref="UnlockStateFor"/>'s pair. Null (every pre-T74 caller) has
    /// `EffectiveRungOf` fall back to the authored rung, so a project that never configures it stays
    /// byte-identical.</summary>
    public UnlockTuning? UnlockTuning { get; init; }

    /// <summary>
    /// commander-roster EP3.11 (`spec-legion-commander.md`): why a legion's Commander member is
    /// **not fielded** in this siege. One reason today — the specimen is not in `Roster` this turn
    /// (seated on the lawn, on a delve, or away with another legion) — and it is a report reason, so
    /// it lives in the closed vocabulary of reasons next to the phase's own drop reasons.
    ///
    /// <para>Away is not a casualty: the member is left exactly where it is and carries forward
    /// through <see cref="BuildSideOutcome"/>'s own "never fielded" branch, so the legion keeps its
    /// commander for the next turn. EP3.12's `Recovering` detach is a different rule with a different
    /// trigger.</para>
    /// </summary>
    public const string CommanderAway = "commander.away";

    /// <summary>
    /// The store half of <see cref="CommanderAway"/>: `true` when this member's specimen cannot fight
    /// here this turn. Core cannot know it — the specimen's phase is a store fact — so the Data layer
    /// injects the predicate beside <see cref="HubInputsFor"/> and <see cref="UnlockStateFor"/>, the
    /// same inversion rather than a layering exception.
    ///
    /// <para>Asked only for a <see cref="WorldEntityMemberRole.Commander"/> member, and expected to be
    /// a side-effect-free read: the setup builder asks once to decide, and the reporter asks once to
    /// name it. Null (every caller that supplies no store, including
    /// <see cref="Instance"/>) fields every member exactly as before.</para>
    /// </summary>
    public Func<WorldEntityMember, bool>? MemberAway { get; init; }

    /// <summary>The ONE expression for "this member is away", so the setup builder and the report
    /// line can never disagree about which members it covers.</summary>
    internal static bool IsAway(WorldEntityMember member, Func<WorldEntityMember, bool>? memberAway) =>
        member.Role == WorldEntityMemberRole.Commander && memberAway?.Invoke(member) == true;

    const string AttackerSide = "squad";
    const string DefenderSide = "wave";

    public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
    {
        // No real engine resolves a non-district kind, and a district request with no board is not
        // simulable — refuse cleanly (no winner, no sides) rather than invent one. See T20 above.
        if (request.Kind != BattleKinds.District || request.Board is null)
            return new BattleOutcome { BattleId = request.BattleId };

        var attacker = combatants.FirstOrDefault(e => string.Equals(e.EntityId, request.AttackerEntityId, StringComparison.Ordinal));
        if (attacker is null)
            return new BattleOutcome { BattleId = request.BattleId };

        var defender = request.DefenderEntityId is { } defenderId
            ? combatants.FirstOrDefault(e => string.Equals(e.EntityId, defenderId, StringComparison.Ordinal))
            : null;

        var board = request.Board;

        // Two assaults resolved inside the SAME turn share the SAME raw `seed` (verified directly in
        // TurnEngine.Step: one ulong, threaded unchanged through every phase) -- mixing it with this
        // battle's own id is this resolver's own job, matching DistrictLayout.DistrictSeed's own
        // established mixing pattern rather than inventing a second one.
        var battleSeed = SeededRng.DeriveStream(seed, request.BattleId).NextULong();

        var syntheticSector = new WorldSector
        {
            SectorId = board.SectorId,
            TypeId = board.SectorTypeId,
            DevelopmentLevel = board.DevelopmentLevel,
            Slots = board.Slots.Select(s => new WorldSlot { SlotIndex = s.SlotIndex, State = s.State }).ToList(),
        };
        var spec = DistrictLayout.Build(syntheticSector, board.WorldSeed, board.AttackerEdge);
        var boardState = new BoardState(spec);
        var districtSeed = DistrictLayout.DistrictSeed(board.WorldSeed, board.SectorId);
        var coreCenter = new GridPos(spec.Rows / 2, spec.Rows / 2);
        var coreSideCells = DistrictLayout.CoreSideCells(spec.Rows, SiegeTuningPolicy.District.CoreSideMilli);

        var structureSetups = PlaceStructures(board, spec, boardState, districtSeed, coreCenter, coreSideCells);

        // base-defense `siege-construction` (decision 27, 2026-09-06): the SAME deterministic
        // slot-to-cell mapping PlaceStructures just used, inverted, plus each slot's own SlotKind --
        // closes ConstructionPlacement.CanPlace's own named gap ("the tactical board has no mapping
        // from a GridPos cell to a world-layer SlotKind today"). An unrecognised SlotTypeId is skipped
        // rather than thrown on -- "cannot verify what may be built here" is refused construction, not
        // a crash, matching this file's own established "fall back rather than throw mid-turn" posture.
        var slotByCell = new Dictionary<GridPos, (int SlotIndex, SlotKind Kind)>();
        foreach (var slot in board.Slots)
        {
            if (!SlotTypeCatalog.IsKnown(slot.SlotTypeId)) continue;
            var cell = DistrictLayout.CellForSlot(districtSeed, slot.SlotIndex, spec, coreCenter, coreSideCells);
            slotByCell[cell] = (slot.SlotIndex, SlotTypeCatalog.Get(slot.SlotTypeId).Kind);
        }
        var constructionBoard = new ConstructionBoardContext(
            boardState, spec.Rows, SiegeTuningPolicy.District.CoreSideMilli,
            SiegeTuningPolicy.District.RampartThickness, slotByCell,
            sectorRubble: board.RubbleStock, sectorIronwork: board.IronworkStock);

        var attackerKeys = new List<string>();
        var attackerSetups = BuildAnimateSetups(attacker, AttackerSide, attackerKeys);
        if (attackerSetups.Count == 0)
            return new BattleOutcome { BattleId = request.BattleId };

        var defenderKeys = new List<string>();
        var defenderSetups = defender is null ? new List<BattleActorSetup>() : BuildAnimateSetups(defender, DefenderSide, defenderKeys);

        // 17.11 (decision 47, resolved 2026-09-07): the task's own pre-existing acceptance formula --
        // ApproachDepth + WardLevel x ApproachDepthPerWardLevel -- applies ApproachDepth
        // UNCONDITIONALLY, not only once WardLevel > 0 (mirrors fortressRampartBonus's own
        // additive-to-thickness shape, just on one wedge instead of the whole ring). A genuine
        // geometry change for every district, warded or not -- verified against golden/unit tests,
        // not assumed byte-identical, matching this program's own established diligence for a real
        // behavior change (15.4/15.4b's own RulesetVersion-bump precedent).
        var wardExtraDepth = checked(SiegeTuningPolicy.District.ApproachDepth
            + board.WardLevel * SiegeTuningPolicy.District.ApproachDepthPerWardLevel);
        var approachCells = OpenCellsInZone(spec, boardState, DistrictZone.Approach, board.AttackerEdge, wardExtraDepth);
        var coreCells = OpenCellsInZone(spec, boardState, DistrictZone.Core, board.AttackerEdge, wardExtraDepth);

        // A board too small for the forces standing on it refuses cleanly rather than throwing
        // mid-turn or inventing a weight-comparison winner for a fight that was never simulated.
        if (attackerKeys.Count > approachCells.Count || defenderKeys.Count > coreCells.Count)
            return new BattleOutcome { BattleId = request.BattleId };

        Placement.PlaceActors(boardState, attackerKeys, approachCells);
        if (defenderKeys.Count > 0) Placement.PlaceActors(boardState, defenderKeys, coreCells);

        var defenderSideSetups = defenderSetups.Concat(structureSetups).ToList();

        // T74/A33: `BattleEngine.Resolve` asks for a holder by ACTOR KEY; the injected provider answers
        // by `InstanceId` (see the property's own doc). This is the one hop between them, and it maps
        // only keys that are real holders — a structure, a wave enemy or a member with no `InstanceId`
        // resolves to `Empty`, which leaves `EffectiveRungOf`'s own authored-rung fallback the only
        // place a rung is decided for it. Null provider => null delegate, the pre-T74 call exactly.
        var instanceIdByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (UnlockStateFor is not null)
            foreach (var s in attackerSetups.Concat(defenderSideSetups))
                if (!string.IsNullOrWhiteSpace(s.SpecimenId)) instanceIdByKey[s.Key] = s.SpecimenId!;

        Func<string, UnlockState>? unlockStateFor = UnlockStateFor is null
            ? null
            : key => instanceIdByKey.TryGetValue(key, out var instanceId)
                ? UnlockStateFor(instanceId)
                : UnlockState.Empty();

        // Unopposed: nothing stands against the attacker at all (no defender entity, or one with no
        // living members and no structures on the board). BattleEngine.Resolve throws on an empty
        // Wave, and there is nothing to simulate -- SiegeObjective.Evaluate below reads an empty
        // defender combatant list and resolves CoreTaken on its own, with no round loop needed.
        BattleReport? report = null;
        if (defenderSideSetups.Count > 0)
        {
            var setup = new BattleSetup
            {
                WaveId = "district:" + request.LocationId,
                Squad = attackerSetups,
                Wave = defenderSideSetups,
            };
            report = BattleEngine.Resolve(setup, battleSeed,
                profile: BattleModeProfileCatalog.Resolve(BattleModeProfileCatalog.SiegeId),
                board: boardState,
                containerResolver: ConstructionActions.ContainerResolver,
                // base-defense siege-ai (2026-09-07, session 5, owner-authorized): every actor in a
                // real siege now gets SiegeAiIntentSource's scored, XCOM-style decisions instead of
                // StubIntentSource's crude nearest-enemy fallback -- the live consumer AggressionOf and
                // every other AiCandidate scoring input were built and proven against, but never
                // actually reached, before this. No `intentSource:` override, so nothing here touches
                // the OTHER wiring (TryDeclareBuilt/TryDeclareObjectiveAdvance) also tried at the same
                // DeclareBasicAttack call site -- both remain reachable exactly as before.
                aiTuning: SiegeTuningPolicy.Ai,
                // T74/A33: a legion member holding a granted action prices it at its own effective
                // rung, through the store-backed provider the Data layer injected beside HubInputsFor.
                unlockStateFor: unlockStateFor,
                unlockTuning: UnlockTuning,
                onEffectHostReady: host =>
                {
                    host.ConstructionBoard = constructionBoard;
                    // siege-ai R3 (2026-09-07): the SAME AttackerEdge already used above (line 75) to
                    // orient DistrictLayout.Build's own GridSpec -- threaded one step further so
                    // BattleRunState.ObjectivePositionOf can answer "which cell should I advance
                    // toward" for a live AI with no target in reach.
                    host.AttackerEdge = board.AttackerEdge;
                    // 15.3b (2026-09-06): the four construction effects, upserted BEFORE BindContainers
                    // runs later in this same BattleRunState constructor (onEffectHostReady fires at
                    // BattleRunState.cs:330, BindContainers at :473 -- confirmed by reading the file, not
                    // assumed) so a builder actor's own container grant resolves correctly.
                    // CORRECTED 2026-09-07 (MAJOR finding, same session): this comment used to say "no
                    // actor holds a construction action yet -- that is siege-ai's own live intent
                    // source's job, not this resolver's." That reasoning was wrong: `SiegeAiIntentSource`
                    // only DECIDES among actions an actor already holds -- it has no mechanism to GRANT
                    // held actions at all. Granting them is this resolver's own job, and
                    // `BuildAnimateSetups` now does it (`AdditionalHeldActions`, attacker-side only).
                    if (host.Bag.Catalog is Effects.InMemoryEffectCatalog catalog)
                        foreach (var def in ConstructionActions.CompiledEffects) catalog.Upsert(def);
                });
        }

        var resultByKey = report?.Actors.ToDictionary(a => a.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, BattleActorResult>(StringComparer.Ordinal);

        var siegeCombatants = new List<SiegeCombatant>();
        AddSiegeCombatants(siegeCombatants, attackerKeys, AttackerSide, resultByKey);
        if (defender is not null) AddSiegeCombatants(siegeCombatants, defenderKeys, DefenderSide, resultByKey);

        var objective = SiegeObjective.Evaluate(siegeCombatants, DefenderSide, AttackerSide);

        var sides = new List<BattleSideOutcome>
        {
            BuildSideOutcome(attacker, attackerKeys, resultByKey, routed: objective == SiegeOutcomeKind.AssaultBroken),
        };
        if (defender is not null)
            sides.Add(BuildSideOutcome(defender, defenderKeys, resultByKey, routed: objective == SiegeOutcomeKind.CoreTaken));

        var winnerEntityId = objective switch
        {
            SiegeOutcomeKind.CoreTaken => attacker.EntityId,
            SiegeOutcomeKind.AssaultBroken => defender?.EntityId,
            _ => null,
        };

        return new BattleOutcome
        {
            BattleId = request.BattleId,
            WinnerEntityId = winnerEntityId,
            Sides = sides.OrderBy(s => s.EntityId, StringComparer.Ordinal).ToList(),
            SlotResults = BuildSlotResults(board, resultByKey, constructionBoard.Placed),
            EngineVersion = BattleRuleset.EngineVersion,
            RulesetVersion = BattleRuleset.RulesetVersion,
            Seed = battleSeed,
            // base-defense `siege-engagement` (module 20, decision 24): an Inconclusive objective is
            // Spent -- the everyday case, the siege continues next turn (assuming a later fix teaches
            // the movement/contact phase to keep re-issuing a district request; see this resolver's
            // own module notes for that named, un-started gap).
            Exit = SiegeEngagement.ExitFor(objective, sides, attacker.EntityId),
        };
    }

    /// <summary>
    /// base-defense `siege-construction`: the pre-existing structure-damage half of the seam, found
    /// unwired while building the new structure-PLACEMENT half (`structure.place`) alongside it — this
    /// resolver has always built a `resultByKey` entry for every `"slot:{index}"` structure key
    /// (<see cref="AddSiegeCombatants"/>'s own key format matches <see cref="PlaceStructures"/>'s), but
    /// nothing ever read it back into <see cref="BattleOutcome.SlotResults"/>, so a structure's HP
    /// damage from a real district fight was silently discarded on every return from this method until
    /// now. Scoped deliberately to HP/destruction only: <see cref="SlotOutcome.HeldByFactionId"/> is
    /// passed straight through from <paramref name="board"/>'s own projected ownership (a no-op write)
    /// rather than computed from post-battle occupation — capture-based ownership transfer for an
    /// EXISTING structure's own slot is a separate, unscoped question this fix does not also attempt
    /// (F11's capture-transfers-the-stockpile rule belongs to `siege-economy`'s own `SiegeDepot`, not
    /// this field). A slot with no `resultByKey` entry (never fielded — the whole fight was unopposed
    /// with no structures at all, so `BattleEngine.Resolve` never ran) contributes nothing, matching
    /// every existing caller's exact current behaviour for that case.
    /// </summary>
    internal static IReadOnlyList<SlotOutcome> BuildSlotResults(
        BoardProjection board, IReadOnlyDictionary<string, BattleActorResult> resultByKey,
        IReadOnlyList<StructurePlacementRecord>? placed = null)
    {
        var outcomes = new Dictionary<int, SlotOutcome>();
        foreach (var slot in board.Slots)
        {
            if (slot.StructureId is null) continue;
            if (!resultByKey.TryGetValue($"slot:{slot.SlotIndex}", out var result)) continue;

            outcomes[slot.SlotIndex] = new SlotOutcome
            {
                SlotIndex = slot.SlotIndex,
                StructureHp = result.HpRemaining,
                StructureDestroyed = !result.Survived,
                HeldByFactionId = slot.OwnerFactionId,
            };
        }

        // base-defense `siege-construction`: a structure PLACED this battle (any of the four
        // acquisition paths, all resolved through `structure.place`) wins over anything computed
        // above for the same slot. Not really a merge conflict -- the placement gate itself refuses
        // building on an occupied cell, so a slot cannot be both "an existing structure that fought"
        // and "freshly placed" in the same battle; overwriting only guards a hypothetical double-count.
        foreach (var record in placed ?? Array.Empty<StructurePlacementRecord>())
        {
            var def = StructureCatalog.Get(record.StructureId);
            outcomes[record.SlotIndex] = new SlotOutcome
            {
                SlotIndex = record.SlotIndex,
                StructurePlaced = record.StructureId,
                PlacedConstructionTurnsRemaining = record.Instant || def.BuildTurns <= 0 ? null : def.BuildTurns,
            };
        }

        return outcomes.Values.OrderBy(o => o.SlotIndex).ToList();
    }

    /// <summary>
    /// Places every structure standing on the district's slots at the SAME cell
    /// <see cref="DistrictLayout.Build"/> itself derives for that slot index — never through
    /// <see cref="ConstructionPlacement"/>, which gates NEW construction, not a structure the world
    /// already recorded standing. Returns the (Structure-kind) setups; the caller is responsible for
    /// excluding these cells from animate placement (<see cref="OpenCellsInZone"/> already does, by
    /// reading live board occupancy after this runs).
    /// </summary>
    static List<BattleActorSetup> PlaceStructures(
        BoardProjection board, GridSpec spec, BoardState boardState,
        ulong districtSeed, GridPos coreCenter, int coreSideCells)
    {
        var setups = new List<BattleActorSetup>();
        foreach (var slot in board.Slots)
        {
            if (slot.StructureId is not { } structureId || !StructureCatalog.IsKnown(structureId)) continue;

            var def = StructureCatalog.Get(structureId);
            var maxHp = StructureDef.MaxHpOf(def, board.DevelopmentLevel);
            var key = $"slot:{slot.SlotIndex}";

            setups.Add(new BattleActorSetup
            {
                Key = key,
                // Decision 4: a structure has no ownership. One fixed, reversible convention: every
                // structure enters on the DEFENDER's side, so the attacker's own units may target and
                // destroy it while the defender's own units never attack their own wall. See this
                // class's own top comment for why this is named as a convention, not a deep rule.
                Side = DefenderSide,
                SpeciesId = structureId,
                Level = 0,
                MaxHp = slot.StructureHp ?? maxHp,
                Atk = 0,
                Defense = 0,
                Kind = CombatantKind.Structure,
            });

            var cell = DistrictLayout.CellForSlot(districtSeed, slot.SlotIndex, spec, coreCenter, coreSideCells);
            boardState.Place(key, cell);
        }

        return setups;
    }

    /// <summary>
    /// One `BattleActorSetup` per LIVING member (`Math.Max(0, member.Hp - member.Wounds) > 0` — the
    /// same effective-HP reading <see cref="Intel.ForceStrength.Of"/> uses for fog-of-war, unrelated
    /// use of the same simple test), reusing the real, shipped, Core-only pattern
    /// <see cref="Battle.WaveCatalog"/> already uses for AI-side content: species-derived
    /// Element/Traits/AttackInterval, magnitudes from <see cref="BattleRuleset.BaseHp"/>/
    /// <see cref="BattleRuleset.BaseAtk"/>/<see cref="BattleRuleset.BaseDefense"/>. Appends each built
    /// key to <paramref name="keys"/> in the same order, so the caller can place and later read results
    /// back by the exact same key list.
    ///
    /// <para><b>The gap this used to name, now closed by a seam (D4 / T3.3).</b> It read: "a
    /// player-owned specimen's real loadout/aptitude/equipment bonuses are NOT read here — that
    /// mechanism lives in `FusionRpg.Server` and needs a live `RpgStore`, which this Core-only,
    /// statics-constructible resolver cannot reach." The diagnosis was right and so was the refusal to
    /// reach across the layer. What was missing was the inversion: <see cref="HubInputsFor"/> lets the
    /// Data layer inject the store-backed lookup at its own call site, so Core still never sees a store
    /// and a legion member still composes from the same inputs a web-match specimen does.</para>
    ///
    /// <para>With no provider this behaves exactly as before — flat, level-derived stats, the same a
    /// wave enemy gets.</para>
    /// </summary>
    /// <remarks>`internal` rather than `private` since solid-remediation T3.3: the D4 seam's own test
    /// drives this directly, because reaching it through `Resolve` would need a whole world and would
    /// then be testing turn resolution rather than the seam.</remarks>
    internal List<BattleActorSetup> BuildAnimateSetups(WorldEntity entity, string side, List<string> keys)
    {
        var setups = new List<BattleActorSetup>();
        for (var i = 0; i < entity.Members.Count; i++)
        {
            var member = entity.Members[i];

            // commander-roster EP3.11, checked first so an away commander is never priced, placed or
            // fielded: its specimen is elsewhere this turn, so the rest of the legion fights without
            // it. The member keeps its slot in `entity.Members` (and so its generated key is never
            // reused by a later member) and is carried forward unchanged by `BuildSideOutcome`.
            if (IsAway(member, MemberAway)) continue;

            var effectiveHp = Math.Max(0, member.Hp - member.Wounds);
            if (effectiveHp <= 0) continue; // already gone -- never fielded

            var species = CreatureSpeciesCatalog.Get(member.SpeciesId);
            var key = $"{entity.EntityId}:{i}";
            keys.Add(key);

            setups.Add(new BattleActorSetup
            {
                Key = key,
                Side = side,
                SpeciesId = member.SpeciesId,
                TypeId = species.CreatureTypeId,
                Level = member.Level,
                ElementPrimary = species.ElementPrimary,
                ElementSecondary = species.ElementSecondary,
                TraitIds = species.TraitPool,
                MaxHp = effectiveHp,
                Atk = BattleRuleset.BaseAtk(member.Level),
                Defense = BattleRuleset.BaseDefense(member.Level),
                AttackIntervalMs = species.AttackIntervalMs,
                // base-defense `siege-construction`/`siege-ai` (2026-09-07, MAJOR finding this
                // session): this setup previously never granted ANY equipped action, so no real
                // legion member could ever hold a construction action -- `Built`/`Assembled`/etc were
                // structurally unreachable in every real siege, not just missing content. Attacker-only
                // -- decision 27's own framing is "a besieging LEGION" acquiring structures as it
                // advances; a defender already holds a base with structures via PlaceStructures above,
                // and defender-side construction during an active siege is a separate, unspecced
                // question this fix does not also decide. Purely additive (see
                // BattleActorSetup.AdditionalHeldActions's own doc comment) -- this member's basic
                // attack is completely unaffected.
                AdditionalHeldActions = side == AttackerSide ? ConstructionActions.CompiledActionsForGrant : null,
                // D4: the same Hub inputs a web match builds for the same specimen. Null provider, or a
                // member the provider cannot resolve, leaves this null — the pre-seam behaviour.
                SpecimenId = member.InstanceId,
                // The InstanceId guard lives HERE, not in the provider: a member without one is a
                // non-player force or a guard (its own field doc), so there is nothing to look up and
                // no provider should have to remember that. Any future provider gets the rule free.
                HubInputs = string.IsNullOrWhiteSpace(member.InstanceId) ? null : HubInputsFor?.Invoke(member),
            });
        }

        return setups;
    }

    /// <summary>Every `Open` cell of the given zone, currently unoccupied, ordinal (row, then column)
    /// for determinism — a plain deterministic order rather than "nearest the entry edge," which is a
    /// deferred realism polish, not a correctness requirement for `Placement.PlaceActors` (any valid,
    /// deterministic cell list works).</summary>
    static List<GridPos> OpenCellsInZone(
        GridSpec spec, BoardState boardState, DistrictZone zone, BoardEdge attackerEdge, int wardExtraDepth)
    {
        var district = SiegeTuningPolicy.District;
        var cells = new List<GridPos>();
        for (var r = 0; r < spec.Rows; r++)
        for (var c = 0; c < spec.Cols; c++)
        {
            var p = new GridPos(r, c);
            if (spec.TerrainAt(p) != CellTerrain.Open) continue;
            if (DistrictLayout.ZoneOf(p, spec.Rows, district.CoreSideMilli, district.RampartThickness,
                    attackerEdge, wardExtraDepth) != zone) continue;
            if (boardState.OccupantAt(p) is not null) continue;
            cells.Add(p);
        }

        return cells;
    }

    static void AddSiegeCombatants(
        List<SiegeCombatant> combatants, IReadOnlyList<string> keys, string side,
        IReadOnlyDictionary<string, BattleActorResult> resultByKey)
    {
        foreach (var key in keys)
        {
            // A key with no result (never reached, e.g. an unopposed fight that never called
            // BattleEngine.Resolve) is still alive -- it never fought at all.
            var alive = !resultByKey.TryGetValue(key, out var result) || result.Survived;
            combatants.Add(new SiegeCombatant(key, side, Alive: alive, Withdrawn: false, InCore: true, Kind: CombatantKind.Animate));
        }
    }

    /// <summary>Translates this side's battle result back into world state — the same
    /// entering-effective-hp / new-total-wounds composition the deleted wave-1 `PlaceholderBattleResolver`
    /// once established: a member entered with `member.Hp - member.Wounds` effective HP; the battle
    /// leaves it with `HpRemaining` of THAT; the new total wounds relative to the member's own full
    /// `Hp` is `member.Hp - HpRemaining`.</summary>
    static BattleSideOutcome BuildSideOutcome(
        WorldEntity entity, IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, BattleActorResult> resultByKey, bool routed)
    {
        var survivors = new List<WorldEntityMember>(entity.Members.Count);
        for (var i = 0; i < entity.Members.Count; i++)
        {
            var member = entity.Members[i];
            var key = $"{entity.EntityId}:{i}";
            if (!keys.Contains(key))
            {
                // Never fielded (already at zero effective HP before this fight) -- carries forward
                // unchanged rather than being silently dropped from the roster.
                survivors.Add(member);
                continue;
            }

            if (!resultByKey.TryGetValue(key, out var result))
            {
                // Fielded, but the fight never actually ran (unopposed) -- unchanged.
                survivors.Add(member);
                continue;
            }

            var newWounds = checked(member.Hp - result.HpRemaining);
            if (newWounds < member.Hp) survivors.Add(member with { Wounds = checked((int)newWounds) });
        }

        return new BattleSideOutcome
        {
            EntityId = entity.EntityId,
            Survivors = survivors,
            Destroyed = survivors.Count == 0,
            Routed = routed && survivors.Count > 0,
        };
    }
}
