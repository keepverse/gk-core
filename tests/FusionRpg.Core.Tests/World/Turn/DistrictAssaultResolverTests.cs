using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.World;
using FusionRpg.Core.World.District;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>
/// base-defense `siege-resolver` (module 15, spec-siege-resolver.md): the `IBattleResolver`
/// implementation for `BattleKinds.District` — the world/battle join. See
/// `DistrictAssaultResolver.cs`'s own top comment for the two named, deliberate simplifications this
/// pass makes (a structure's fixed battle side; `InCore` approximated as "alive" since `BattleReport`
/// carries no final position data for any battle kind).
/// </summary>
public class DistrictAssaultResolverTests
{
    static WorldEntity Legion(string id, string owner, string sectorId, params (string Species, int Level, long Hp)[] members) => new()
    {
        EntityId = id,
        Kind = WorldEntityKind.Warband,
        OwnerFactionId = owner,
        AtSectorId = sectorId,
        Members = members.Select(m => new WorldEntityMember { SpeciesId = m.Species, Level = m.Level, Hp = m.Hp }).ToList(),
    };

    static BattleRequest DistrictRequest(string battleId, string attackerId, string? defenderId, BoardProjection? board, int slotCount = 0) => new()
    {
        BattleId = battleId,
        Kind = BattleKinds.District,
        LocationId = "s1",
        AttackerEntityId = attackerId,
        DefenderEntityId = defenderId,
        DefenderStationary = defenderId is not null,
        Board = board,
    };

    static BoardProjection Board(ulong worldSeed = 42, IReadOnlyList<SlotProjection>? slots = null) => new()
    {
        SectorId = "s1",
        WorldSeed = worldSeed,
        SectorTypeId = "home",
        DevelopmentLevel = 0,
        AttackerEdge = FusionRpg.Core.World.District.BoardEdge.North,
        Slots = slots ?? Array.Empty<SlotProjection>(),
    };

    [Fact]
    public void Non_district_kinds_refuse_cleanly_rather_than_inventing_a_winner()
    {
        // actor-hub-and-combat-power-solid-fixing T20: no engine resolves a non-district kind, so no
        // winner is invented for one either — the pre-existing "refused" BattleOutcome shape (BattleId
        // only) is reused, matching what this same resolver already returns for a missing attacker.
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 1, 100));
        var request = new BattleRequest
        {
            BattleId = "b1", Kind = BattleKinds.Sector, LocationId = "s1",
            AttackerEntityId = attacker.EntityId, DefenderEntityId = defender.EntityId, DefenderStationary = true,
        };
        var combatants = new[] { attacker, defender };

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, combatants, 1);

        Assert.Equal("b1", outcome.BattleId);
        Assert.Null(outcome.WinnerEntityId);
        Assert.Empty(outcome.Sides);
    }

    [Fact]
    public void District_kind_with_no_board_refuses_cleanly_rather_than_inventing_a_winner()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var request = DistrictRequest("b1", attacker.EntityId, null, board: null);
        var combatants = new[] { attacker };

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, combatants, 1);

        Assert.Equal("b1", outcome.BattleId);
        Assert.Null(outcome.WinnerEntityId);
        Assert.Empty(outcome.Sides);
    }

    [Fact]
    public void Resolver_is_constructible_from_statics_only()
    {
        // The whole point of `Instance` -- no constructor parameter, no live service.
        var resolver = new DistrictAssaultResolver();
        Assert.NotNull(resolver);
        Assert.NotNull(DistrictAssaultResolver.Instance);
    }

    [Fact]
    public void An_unopposed_assault_resolves_as_core_taken_with_no_battle_engine_call()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var request = DistrictRequest("b1", attacker.EntityId, null, Board());

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker }, seed: 7);

        Assert.Equal(attacker.EntityId, outcome.WinnerEntityId);
        var side = Assert.Single(outcome.Sides);
        Assert.Equal(attacker.EntityId, side.EntityId);
        Assert.False(side.Destroyed);
        Assert.NotEmpty(side.Survivors);
    }

    [Fact]
    public void A_real_fight_produces_two_sides_and_a_version_stamp()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 3, 300), ("conezombie", 3, 300));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 3, 300));
        var request = DistrictRequest("b1", attacker.EntityId, defender.EntityId, Board());

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker, defender }, seed: 123);

        Assert.Equal(2, outcome.Sides.Count);
        Assert.Contains(outcome.Sides, s => s.EntityId == attacker.EntityId);
        Assert.Contains(outcome.Sides, s => s.EntityId == defender.EntityId);
        Assert.True(outcome.EngineVersion > 0);
        Assert.True(outcome.RulesetVersion > 0);
        Assert.NotNull(outcome.Seed);
        // Nobody is duplicated or invented -- survivor counts never exceed the roster they came from.
        foreach (var side in outcome.Sides)
        {
            var original = side.EntityId == attacker.EntityId ? attacker : defender;
            Assert.True(side.Survivors.Count <= original.Members.Count);
        }
    }

    [Fact]
    public void Same_seed_same_siege_10000_times()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 3, 300));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 3, 300));
        var request = DistrictRequest("b1", attacker.EntityId, defender.EntityId, Board());
        var combatants = new[] { attacker, defender };

        var first = DistrictAssaultResolver.Instance.Resolve(request, combatants, seed: 999);
        for (var i = 0; i < 10_000; i++)
        {
            var repeat = DistrictAssaultResolver.Instance.Resolve(request, combatants, seed: 999);
            Assert.Equal(first.WinnerEntityId, repeat.WinnerEntityId);
            Assert.Equal(first.Sides.Select(s => s.Survivors.Count), repeat.Sides.Select(s => s.Survivors.Count));
        }
    }

    [Fact]
    public void Two_assaults_in_one_turn_get_different_seeds()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 3, 300));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 3, 300));
        var combatants = new[] { attacker, defender };

        var requestOne = DistrictRequest("t1:district:s1:e-a|e-d", attacker.EntityId, defender.EntityId, Board());
        var requestTwo = DistrictRequest("t1:district:s2:e-a|e-d", attacker.EntityId, defender.EntityId, Board());

        var outcomeOne = DistrictAssaultResolver.Instance.Resolve(requestOne, combatants, seed: 5000);
        var outcomeTwo = DistrictAssaultResolver.Instance.Resolve(requestTwo, combatants, seed: 5000);

        Assert.NotEqual(outcomeOne.Seed, outcomeTwo.Seed);
    }

    [Fact]
    public void Structure_hp_survives_the_round_trip_as_long()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var slots = new[]
        {
            new SlotProjection { SlotIndex = 0, SlotTypeId = "rootbed", StructureId = "well", StructureHp = 4_000_000_000L },
        };
        var request = DistrictRequest("b1", attacker.EntityId, null, Board(slots: slots));

        // Unopposed (no defender entity) but a structure stands on the board -- still fights (the
        // structure is on the defender side), so this exercises the BattleEngine.Resolve path with a
        // real, large long HP value end to end.
        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker }, seed: 1);

        Assert.NotNull(outcome);
        // The structure itself is not a WorldEntity, so it never appears in outcome.Sides -- but it
        // MUST appear in SlotResults (base-defense siege-construction: found and fixed a real,
        // previously-unwired gap where this seam existed and was tested in isolation but nothing ever
        // populated it from a real fight). A single level-1 peashooterzombie cannot dent 4 billion HP.
        var slotResult = Assert.Single(outcome.SlotResults);
        Assert.Equal(0, slotResult.SlotIndex);
        Assert.False(slotResult.StructureDestroyed);
        Assert.True(slotResult.StructureHp > int.MaxValue, "structure HP must survive the round trip as a real long, not narrow to int");
    }

    /// <summary>
    /// `BuildSlotResults` tested directly against a synthetic `resultByKey`, the same "test the seam in
    /// isolation" discipline `StructureStateTests`/`BattleSeamWideningTests` already use for
    /// `ApplySlotResults` itself — deliberately NOT routed through a full `BattleEngine.Resolve` call.
    /// Found while writing this fix: an approach-zone attacker with no equipped movement action cannot
    /// reliably reach a core-zone structure within the siege profile's own round/tick budget in this
    /// synthetic test harness, so a real fight landing zero damage on a structure proves nothing about
    /// THIS translation step either way — a separate, pre-existing question (whether the shipped default
    /// action loadout and approach-to-core distance ever let a real attacker close to melee range) that
    /// belongs to `siege-pathing`/`siege-resolver`, not to this seam fix.
    /// </summary>
    [Fact]
    public void Build_slot_results_reports_an_existing_structures_damage_and_destruction()
    {
        var board = Board(slots: new[]
        {
            new SlotProjection { SlotIndex = 2, SlotTypeId = "wildland", StructureId = "granary", OwnerFactionId = "zomboss" },
        });
        var resultByKey = new Dictionary<string, BattleActorResult>(StringComparer.Ordinal)
        {
            ["slot:2"] = new("slot:2", "wave", "granary", 0, HpRemaining: 0, DamageDealt: 0, Kills: 0, Survived: false, Retreated: false, XpMilli: 0),
        };

        var results = DistrictAssaultResolver.BuildSlotResults(board, resultByKey);

        var slotResult = Assert.Single(results);
        Assert.Equal(2, slotResult.SlotIndex);
        Assert.True(slotResult.StructureDestroyed);
        Assert.Equal(0, slotResult.StructureHp);
        Assert.Equal("zomboss", slotResult.HeldByFactionId); // preserved, not recomputed from capture
    }

    [Fact]
    public void Build_slot_results_skips_a_slot_with_no_structure_or_no_battle_engine_result()
    {
        var board = Board(slots: new[]
        {
            new SlotProjection { SlotIndex = 0, SlotTypeId = "wildland", StructureId = null },
            new SlotProjection { SlotIndex = 1, SlotTypeId = "rootbed", StructureId = "well" }, // never fielded -- no "slot:1" entry
        });

        var results = DistrictAssaultResolver.BuildSlotResults(board, new Dictionary<string, BattleActorResult>(StringComparer.Ordinal));

        Assert.Empty(results);
    }

    [Fact]
    public void Build_slot_results_reports_a_structure_placed_this_battle_finished_immediately()
    {
        var board = Board(); // empty -- the slot did not exist before this battle
        var placed = new[] { new StructurePlacementRecord(SlotIndex: 3, StructureId: "granary", Instant: true) };

        var results = DistrictAssaultResolver.BuildSlotResults(
            board, new Dictionary<string, BattleActorResult>(StringComparer.Ordinal), placed);

        var slotResult = Assert.Single(results);
        Assert.Equal(3, slotResult.SlotIndex);
        Assert.Equal("granary", slotResult.StructurePlaced);
        Assert.Null(slotResult.PlacedConstructionTurnsRemaining); // instant: finished immediately
    }

    [Fact]
    public void Build_slot_results_reports_a_structure_placed_this_battle_still_under_construction()
    {
        // "well" ships with a real, positive BuildTurns (Loam.LoamPolicy.WellBuildTurns) -- instant:
        // false must read it from the catalog, not invent a countdown of its own.
        var board = Board();
        var placed = new[] { new StructurePlacementRecord(SlotIndex: 1, StructureId: "well", Instant: false) };

        var results = DistrictAssaultResolver.BuildSlotResults(
            board, new Dictionary<string, BattleActorResult>(StringComparer.Ordinal), placed);

        var slotResult = Assert.Single(results);
        Assert.Equal("well", slotResult.StructurePlaced);
        Assert.Equal(StructureCatalog.Get("well").BuildTurns, slotResult.PlacedConstructionTurnsRemaining);
        Assert.True(slotResult.PlacedConstructionTurnsRemaining > 0);
    }

    [Fact]
    public void An_existing_structures_ownership_is_preserved_not_recomputed_from_capture()
    {
        // This fix's own named, deliberate scope boundary: HP/destruction persist, but capture-based
        // ownership transfer for an EXISTING structure's slot is a separate, unscoped question --
        // HeldByFactionId passes the projection's own OwnerFactionId straight through, unchanged.
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var slots = new[]
        {
            new SlotProjection
            {
                SlotIndex = 0, SlotTypeId = "rootbed", StructureId = "well",
                StructureHp = 4_000_000_000L, OwnerFactionId = "zomboss",
            },
        };
        var request = DistrictRequest("b1", attacker.EntityId, null, Board(slots: slots));

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker }, seed: 1);

        Assert.Equal("zomboss", Assert.Single(outcome.SlotResults).HeldByFactionId);
    }

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, MAJOR finding this session): before
    /// `BattleActorSetup.AdditionalHeldActions` existed, no legion member built by this resolver could
    /// ever hold a construction action at all. Proving the FULL "a real siege actually builds a
    /// structure" chain also needs the attacker positioned adjacent to an affordable, legal cell —
    /// `ConstructionAi.ChooseBuiltSite`'s own 8-cell search — which this resolver's own
    /// zone-based `Placement.PlaceActors` does not guarantee relative to an arbitrary slot's cell
    /// (confirmed empirically: an earlier version of this test asserted a built `moat` and failed with
    /// neither combat damage nor a placement, meaning the attacker's real placement here simply was not
    /// adjacent to the target cell — a real, pre-existing, geometry-only constraint, unrelated to
    /// whether the action is held). `ConstructionLiveWiringTests.cs`'s own
    /// `AdditionalHeldActionsAloneMakeBuiltReachable` proves the actual mechanism this fix adds, under
    /// the same hand-controlled positioning that module's other tests already use. This test instead
    /// proves the narrower, still-real claim: wiring `AdditionalHeldActions` into a genuinely opposed,
    /// unpositioned real battle changes nothing observable when no legal build site is adjacent —
    /// no exception, no stray structure, no regression to the pre-existing "well" outcome.
    /// </summary>
    [Fact]
    public void Granting_construction_actions_does_not_change_an_unrelated_battles_outcome()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        var slots = new[]
        {
            new SlotProjection { SlotIndex = 0, SlotTypeId = "rootbed", StructureId = "well", StructureHp = 4_000_000_000L },
        };
        var board = Board(slots: slots) with { RubbleStock = 10, IronworkStock = 10 };
        var request = DistrictRequest("b1", attacker.EntityId, null, board);

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker }, seed: 1);

        // Same outcome shape `An_existing_structures_ownership_is_preserved...` already established
        // for this exact slot setup -- proving AdditionalHeldActions being wired for the FIRST time
        // introduced no regression to an unrelated battle it has no legal site to act on.
        var slotResult = Assert.Single(outcome.SlotResults);
        Assert.Equal(0, slotResult.SlotIndex);
        Assert.False(slotResult.StructureDestroyed);
    }

    [Fact]
    public void A_slot_with_no_structure_never_appears_in_slot_results()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 3, 300));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 3, 300));
        var slots = new[] { new SlotProjection { SlotIndex = 0, SlotTypeId = "wildland", StructureId = null } };
        var request = DistrictRequest("b1", attacker.EntityId, defender.EntityId, Board(slots: slots));

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker, defender }, seed: 1);

        Assert.Empty(outcome.SlotResults);
    }

    [Fact]
    public void No_battle_engine_call_when_the_defender_has_no_living_members()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 1, 100));
        // Defender exists but every member is already past death -- Hp <= Wounds.
        var defender = new WorldEntity
        {
            EntityId = "e-d", Kind = WorldEntityKind.Warband, OwnerFactionId = "zomboss", AtSectorId = "s1",
            Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 100, Wounds = 100 } },
        };
        var request = DistrictRequest("b1", attacker.EntityId, defender.EntityId, Board());

        var outcome = DistrictAssaultResolver.Instance.Resolve(request, new[] { attacker, defender }, seed: 1);

        Assert.Equal(attacker.EntityId, outcome.WinnerEntityId);
    }

    // ---- commander-roster EP3.11 (spec-legion-commander.md) ----------------------------------------

    /// <summary>Any real species id — the catalog is content and its membership is a reading, so this
    /// file names no species literal that a content pass could invalidate.</summary>
    static string AnySpecies => FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All[0].SpeciesId;

    /// <summary>Any real aptitude id — the catalog is a closed vocabulary the code owns and which
    /// member is picked is not part of what these tests assert.</summary>
    static string AnyAptitude => AptitudeCatalog.All[0].Id;

    static WorldEntity CommanderLegion(string id, string owner, string sectorId) => new()
    {
        EntityId = id,
        Kind = WorldEntityKind.Warband,
        OwnerFactionId = owner,
        AtSectorId = sectorId,
        Members = new[]
        {
            new WorldEntityMember { SpeciesId = AnySpecies, Level = 3, Hp = 300 },
            new WorldEntityMember
            {
                InstanceId = "unique:away-commander",
                SpeciesId = AnySpecies,
                Level = 9,
                Hp = 900,
                Role = WorldEntityMemberRole.Commander,
            },
        },
    };

    /// <summary>
    /// spec test 3, the consumer-side half of SP1.2 (`layer-source-selector`): a member carrying an
    /// `InstanceId` reaches its setup with the inputs its builder resolved, and **none of them is a
    /// `CreatureType`-scope term** — the empire species fallback that used to leak onto a unique in a
    /// siege. The provider's own half is asserted where the provider lives
    /// (`FusionRpg.Data.Tests/WorldTurnHubInputsForTests`); this pins the resolver's end of the same
    /// contract, which is the end a siege consumer reads.
    /// </summary>
    [Fact]
    public void A_commander_members_setup_carries_its_hub_inputs_and_no_creature_type_points()
    {
        var commander = new WorldEntityMember
        {
            InstanceId = "unique:legion-commander", SpeciesId = AnySpecies, Level = 9, Hp = 900,
            Role = WorldEntityMemberRole.Commander,
        };
        var attacker = new WorldEntity
        {
            EntityId = "e-a", Kind = WorldEntityKind.Warband, OwnerFactionId = "player", AtSectorId = "s1",
            Members = new WorldEntityMember[]
            {
                new() { SpeciesId = AnySpecies, Level = 1, Hp = 100 }, commander,
            },
        };
        // The shape the real provider builds: the owner's commander pool + the specimen's OWN
        // allocation, and no species/CreatureType term at all (ruling R16, SP1.2).
        var inputs = new BattleHubInputs
        {
            Aptitude = AptitudeAllocation.Single(AllocationScope.Commander, AnyAptitude, 3)
                + AptitudeAllocation.Single(AllocationScope.UniqueCreature, AnyAptitude, 7),
        };

        var keys = new List<string>();
        var setups = new DistrictAssaultResolver { HubInputsFor = m => m.InstanceId is null ? null : inputs }
            .BuildAnimateSetups(attacker, "squad", keys);

        Assert.Equal(2, setups.Count);
        var commanderSetup = setups.Single(s => s.SpecimenId == "unique:legion-commander");
        Assert.Same(inputs, commanderSetup.HubInputs);
        Assert.NotNull(commanderSetup.HubInputs!.Aptitude);
        Assert.Equal(0, commanderSetup.HubInputs.Aptitude!.TotalForScope(AllocationScope.CreatureType));
        Assert.Equal(
            new[] { AllocationScope.Commander, AllocationScope.UniqueCreature },
            commanderSetup.HubInputs.Aptitude.Entries.Select(e => e.Scope).Distinct().OrderBy(s => s).ToArray());

        // A general member still reaches its setup with no inputs at all — the pre-seam behaviour the
        // existing siege goldens rest on.
        Assert.Null(setups.Single(s => s.SpecimenId is null).HubInputs);
    }

    /// <summary>
    /// spec test 4 and the mechanism test 7 rests on: the away rule is **Role-gated**, so it cannot
    /// touch a member that carries no `InstanceId`, and a siege whose members are all general troops is
    /// byte-identical with the rule wired. Asserted on the resolved outcome rather than assumed.
    /// </summary>
    [Fact]
    public void A_siege_with_no_instance_id_members_is_byte_identical_with_the_away_rule_wired()
    {
        var attacker = Legion("e-a", "player", "s1", ("peashooterzombie", 3, 300));
        var defender = Legion("e-d", "zomboss", "s1", ("normalzombie", 3, 300));
        var request = DistrictRequest("b1", attacker.EntityId, defender.EntityId, Board());

        var withoutProvider = DistrictAssaultResolver.Instance
            .Resolve(request, new[] { attacker, defender }, seed: 123);
        // `_ => true` is the most aggressive possible away answer; a Fighter must be fielded anyway.
        var withAwayAnsweringAlways = new DistrictAssaultResolver { MemberAway = _ => true }
            .Resolve(request, new[] { attacker, defender }, seed: 123);

        Assert.Equal(JsonSerializer.Serialize(withoutProvider), JsonSerializer.Serialize(withAwayAnsweringAlways));

        var keys = new List<string>();
        var setups = new DistrictAssaultResolver { MemberAway = _ => true }
            .BuildAnimateSetups(attacker, "squad", keys);
        Assert.Single(setups);
        Assert.Equal(new[] { "e-a:0" }, keys);
    }

    /// <summary>
    /// spec test 6b, resolver half: an away Commander is not fielded (its key is never generated, so
    /// no setup and no placement), the rest of the legion fights, and the member is **carried forward
    /// unchanged** — away is not a casualty, so the legion keeps its commander next turn. EP3.12's
    /// `Recovering` detach is the rule that removes one.
    /// </summary>
    [Fact]
    public void An_away_commander_is_not_fielded_and_the_rest_of_the_legion_fights()
    {
        var attacker = CommanderLegion("e-a", "player", "s1");
        var request = DistrictRequest("b1", attacker.EntityId, null, Board());
        var resolver = new DistrictAssaultResolver
        {
            MemberAway = m => m.InstanceId == "unique:away-commander",
        };

        var keys = new List<string>();
        var setups = resolver.BuildAnimateSetups(attacker, "squad", keys);

        // Only the Fighter is priced, and it keeps its own generated key — the away member is skipped
        // WITHOUT renumbering the members behind it.
        var setup = Assert.Single(setups);
        Assert.Null(setup.SpecimenId);
        Assert.Equal(new[] { "e-a:0" }, keys);

        var outcome = resolver.Resolve(request, new[] { attacker }, seed: 7);
        var side = Assert.Single(outcome.Sides);
        Assert.False(side.Destroyed);
        // Both members survive the turn: the fighter fought, the away commander carries forward.
        Assert.Equal(2, side.Survivors.Count);
        Assert.Equal("unique:away-commander",
            side.Survivors.Single(m => m.Role == WorldEntityMemberRole.Commander).InstanceId);
    }

    /// <summary>
    /// spec test 6b, report half: the skip is named `commander.away` in the turn report, scoped to the
    /// ground it happened on and to the legion's own faction. `BattleReporting.Fight` is the one place
    /// that has both the resolver's rule and the report, so it is where the line is written.
    /// </summary>
    [Fact]
    public void An_away_commander_is_named_commander_away_in_the_report()
    {
        var attacker = CommanderLegion("e-a", "dave", "s1");
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[]
            {
                new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            },
            Sectors = new[] { new WorldSector { SectorId = "s1", OwnerFactionId = "zomboss", Phase = SectorPhase.Held } },
            Entities = new[] { attacker },
        };
        var request = DistrictRequest("b1", attacker.EntityId, null, Board());
        var resolver = new DistrictAssaultResolver { MemberAway = m => m.InstanceId == "unique:away-commander" };
        var report = new TurnReport();

        var next = BattleReporting.Fight(world, request, resolver, report, "assaults", seed: 1);

        var away = Assert.Single(report.Entries, e =>
            e.Detail.StartsWith(DistrictAssaultResolver.CommanderAway, StringComparison.Ordinal));
        Assert.Equal(TurnReportKinds.Event, away.Kind);
        Assert.Equal("b1", away.Subject);
        Assert.Equal($"{DistrictAssaultResolver.CommanderAway}:{AnySpecies}", away.Detail);
        Assert.Equal("s1", away.SectorId);
        Assert.Equal("dave", away.Audience);

        // The battle itself still happened, and the away commander is still on the legion afterwards.
        Assert.Single(report.Entries, e => e.Kind == TurnReportKinds.Battle);
        Assert.Equal(2, next.Entities.Single(e => e.EntityId == "e-a").Members.Count);
    }
}
