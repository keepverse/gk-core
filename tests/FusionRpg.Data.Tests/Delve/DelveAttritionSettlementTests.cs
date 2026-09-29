using FusionRpg.Contracts;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>D2.23 (spec-delve-attrition.md §1, §7, §9) — `CloseDelve`'s attrition settlement, the
/// `members[]` writer, cross-delve pool persistence, the recovery counter (including every OTHER
/// delve this player owns), and the recovery ritual. Reads the real, shipped dungeon tuning/registry
/// files — a fixture copy could drift from what ships.
///
/// <para>Also D3.16 (spec-dungeon-loot.md §7) — `souls_unbanked`/`theta_run`, `Accrue`/`Spend`/
/// `RecordClear`, and `CloseDelve`'s own loot-earn hook. Tested in THIS file, not a separate one,
/// specifically for the cross-hook, single-transaction property the two settlements share: an
/// ordering/atomicity proof needs a genuine attrition-observable effect (a downed-once member's own
/// Recover transition) alongside a genuine souls-observable one, and this file already owns the
/// creature-minting fixture (<see cref="MintBoundCreature"/>) that effect needs.</para></summary>
public class DelveAttritionSettlementTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public DelveAttritionSettlementTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 1_000_000, "seed", "attrition-bank");

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    // ---- fixtures ----

    static WorldState BuildGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = 42UL, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "fight", Climate = null, OwnerFactionId = "dave" },
            new WorldSector { SectorId = "r1c0", TypeId = "cache", Climate = null },
            new WorldSector { SectorId = "r2c0", TypeId = "boss", Climate = null },
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" },
            new WorldLane { LaneId = "l1", FromSectorId = "r1c0", ToSectorId = "r2c0", TypeId = "passage" },
        },
        Entities = new[]
        {
            new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r1c0", 1, 0, "cache", "room.cache-none-001", false, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r2c0", 2, 0, "boss", "room.boss-none-001", false, false, null, null, null, null, "[]", 0),
    };

    /// <summary>One fresh three-room delve for player 1 — a unique world id per call so parallel
    /// tests in this class never collide on `rpg_delves.world_id`'s own UNIQUE constraint.</summary>
    DelveRow CreateDelve(string rungId = "hard", long playerId = 1)
    {
        var worldId = $"delve-attr-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            playerId, "domain.fire-shallow-001", "solo", rungId, "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), BuildRooms(), _rooms, _doors);
        Assert.True(ok);
        return delve!;
    }

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    /// <summary>A real, minted, bound-contract creature for player 1 — so loyalty crediting has a real
    /// row to observe changing.</summary>
    string MintBoundCreature()
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId, Side = Species.Side, GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(), Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(), ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] }, Origin = "summon"
        });
        var id = specimen.Actor.InstanceId;
        Assert.True(_store.BindContract(1, id).Ok);
        return id;
    }

    static readonly Dictionary<string, long> FullPools = new(StringComparer.Ordinal)
    {
        ["hp"] = 1000, ["stamina"] = 1000, ["hunger"] = 700, ["spirit"] = 1000, ["qi"] = 1000, ["poise"] = 1000,
    };

    static DelveMemberState Member(string instanceId, bool downed = false, bool downedOnce = false, int nerveStacks = 0, long spirit = 1000, long hunger = 700) =>
        new(instanceId, new Dictionary<string, long>(FullPools, StringComparer.Ordinal) { ["spirit"] = spirit, ["hunger"] = hunger },
            Array.Empty<Core.Battle.BattleStatusSpec>(), null, nerveStacks, downed, downedOnce);

    // ---- backward compatibility: no tuning supplied ----

    [Fact]
    public void CloseDelve_without_tuning_is_byte_identical_to_before_this_task()
    {
        var delve = CreateDelve();
        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false);
        Assert.True(closed);
        var reloaded = _store.LoadDelve(delve.DelveId);
        Assert.Equal(DelveStates.Extracted, reloaded!.State);
    }

    // ---- WritePartyMembers ----

    [Fact]
    public void WritePartyMembers_round_trips_through_the_real_store()
    {
        var delve = CreateDelve();
        var members = new[] { Member("creature-a"), Member("creature-b", downed: true, downedOnce: true, nerveStacks: 5) };

        var updated = _store.WritePartyMembers(delve.DelveId, partyEntityId: 0, members);

        var party = updated!.Parties.Single(p => p.EntityId == 0);
        Assert.Equal(2, party.Members!.Count);
        var b = party.Members!.Single(m => m.InstanceId == "creature-b");
        Assert.True(b.Downed);
        Assert.True(b.DownedOnce);
        Assert.Equal(5, b.NerveStacks);
        Assert.Equal(1000L, b.Pools["spirit"]);
    }

    [Fact]
    public void WritePartyMembers_creates_the_party_row_the_first_time_a_delve_has_none_yet()
    {
        // CreateDelve seeds parties_json = '[]' -- no party rows exist for ANY entity id yet, so the
        // first room a party ever fights must be able to create its own row, not require one already
        // exist (an upsert, matching the "first room, first row" shape).
        var delve = CreateDelve();
        var updated = _store.WritePartyMembers(delve.DelveId, partyEntityId: 7, new[] { Member("fresh-party-creature") });
        var party = updated!.Parties.Single(p => p.EntityId == 7);
        Assert.Equal("fresh-party-creature", party.Members!.Single().InstanceId);
    }

    [Fact]
    public void A_party_written_before_this_field_existed_deserializes_members_to_null_not_empty()
    {
        // CreateDelve seeds parties_json = '[]' -- no party rows at all yet, matching a real delve
        // that has not called WritePartyMembers. Simulate the "existing party, no members[] key"
        // shape directly through the same JSON round trip DelveRow.ParsePartiesJson uses.
        var json = """[{"EntityId":0,"Route":["r0c0"],"Pity":{},"Haul":[]}]""";
        var parsed = DelveRow.ParsePartiesJson(json);
        Assert.Null(parsed[0].Members);
    }

    // ---- CloseDelve settlement: Outcome ----

    [Fact]
    public void A_never_downed_member_settles_to_Roster_untouched()
    {
        var delve = CreateDelve();
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature) });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var actor = _store.GetUniqueActor(creature)!;
        // BindContract binds the CONTRACT, a separate axis from roster/deploy phase -- a minted,
        // bound-but-never-deployed creature starts and stays Roster. The claim this test makes is
        // narrower and still real: settlement never MOVED it to Recovering/Retired.
        Assert.Equal(UniqueActorPhases.Roster, actor.Phase);
    }

    [Fact]
    public void A_downedOnce_member_below_the_permadeath_gate_Recovers_with_the_real_tunable_count()
    {
        var delve = CreateDelve(rungId: "hard"); // "hard" sits below domain.permadeathFromRung
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var actor = _store.GetUniqueActor(creature)!;
        Assert.Equal(UniqueActorPhases.Recovering, actor.Phase);
        var recovery = _store.GetUniqueActorRecovery(creature)!.Value;
        Assert.Equal(_tuning.RiskDownedRecoveryDelves, recovery.RecoveryDelvesLeft);
        Assert.Equal(delve.DelveId, recovery.WoundedDelveId);
        Assert.Equal(delve.ThetaRun, recovery.ThetaRun);
    }

    [Fact]
    public void A_downedOnce_member_on_a_permadeath_rung_Retires()
    {
        var permadeathRung = _tuning.Domain.PermadeathFromRung;
        var delve = CreateDelve(rungId: permadeathRung);
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(creature)!.Phase);
        Assert.Null(_store.GetUniqueActorRecovery(creature));
    }

    [Fact]
    public void A_wipe_forces_downedOnce_for_every_member_even_one_never_actually_downed()
    {
        var delve = CreateDelve(rungId: "hard");
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: false) }); // never downed

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        // A wipe treats every member as downedOnce=true (spec §8: "on a wipe, all of them") -- below
        // the permadeath gate, that means Recovering, never Roster.
        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(creature)!.Phase);
    }

    // ---- CloseDelve settlement: won / loyalty ----

    [Fact]
    public void A_member_who_extracted_after_killing_the_boss_wins_and_is_credited()
    {
        var delve = CreateDelve();
        _store.MarkRoom(delve.DelveId, "r2c0", cleared: true); // the boss room, per BuildRooms/BuildGraph
        var creature = MintBoundCreature();
        var before = _store.GetContract(creature)!.Loyalty;
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature) });
        SetRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0", "r2c0" });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.True(_store.GetContract(creature)!.Loyalty > before);
    }

    // ---- D4.8 (spec-wild-room.md §7): MarkRoom's new resolvedKind parameter ----

    [Fact]
    public void MarkRoom_records_a_cage_resolved_kind_and_it_persists()
    {
        var delve = CreateDelve();
        _store.MarkRoom(delve.DelveId, "r0c0", resolvedKind: "cage");

        var room = _store.LoadDelveRooms(delve.DelveId).Single(r => r.SectorId == "r0c0");
        Assert.Equal("cage", room.ResolvedKind);
    }

    [Fact]
    public void MarkRoom_can_set_resolvedKind_alongside_visited_and_cleared_in_one_call()
    {
        var delve = CreateDelve();
        _store.MarkRoom(delve.DelveId, "r0c0", visited: true, cleared: true, resolvedKind: "cage");

        var room = _store.LoadDelveRooms(delve.DelveId).Single(r => r.SectorId == "r0c0");
        Assert.True(room.Visited);
        Assert.True(room.Cleared);
        Assert.Equal("cage", room.ResolvedKind);
    }

    [Fact]
    public void MarkRoom_with_no_arguments_at_all_writes_nothing_not_even_a_revision_bump()
    {
        var delve = CreateDelve();
        var before = _store.LoadDelveRooms(delve.DelveId).Single(r => r.SectorId == "r0c0").Revision;

        _store.MarkRoom(delve.DelveId, "r0c0");

        var after = _store.LoadDelveRooms(delve.DelveId).Single(r => r.SectorId == "r0c0").Revision;
        Assert.Equal(before, after);
    }

    // ---- D4.14 (spec-delve-quests.md §2, §4): quests_json read/write ----

    [Fact]
    public void WriteQuestOffer_then_ReadQuestOffer_round_trips_exactly()
    {
        var delve = CreateDelve();
        var offer = new[]
        {
            new RpgStore.QuestJsonRow("quest.explore", 3),
            new RpgStore.QuestJsonRow("quest.kill-boss", 0),
        };

        _store.WriteQuestOffer(delve.DelveId, offer);
        var read = _store.ReadQuestOffer(delve.DelveId);

        Assert.Equal(2, read.Count);
        Assert.Equal("quest.explore", read[0].QuestId);
        Assert.Equal(3, read[0].Need);
        Assert.Null(read[0].Done); // no verdict yet
        Assert.Null(read[0].Have);
    }

    [Fact]
    public void The_stored_offer_is_truth_a_rebuild_never_silently_overwrites_it()
    {
        // "the stored offer is truth and a rebuild is asserted equal on load" (spec §2, verbatim) --
        // ReadQuestOffer must return exactly what was WRITTEN, never something freshly recomputed,
        // even if a caller's own re-derivation (a re-rolled pool, a re-run Draw) would now disagree.
        var delve = CreateDelve();
        var originalOffer = new[] { new RpgStore.QuestJsonRow("quest.explore", 3) };
        _store.WriteQuestOffer(delve.DelveId, originalOffer);

        // Simulate "the corpus changed mid-delve" -- a fresh computation would now disagree with
        // what's already stored. ReadQuestOffer must still return the ORIGINAL, stored value.
        var hypotheticalRebuild = new[] { new RpgStore.QuestJsonRow("quest.explore", 5) }; // never written
        var read = _store.ReadQuestOffer(delve.DelveId);

        Assert.Equal(3, read.Single().Need);
        Assert.NotEqual(hypotheticalRebuild.Single().Need, read.Single().Need);
    }

    [Fact]
    public void WriteQuestVerdicts_merges_into_the_stored_offer_without_growing_or_reordering_it()
    {
        var delve = CreateDelve();
        _store.WriteQuestOffer(delve.DelveId, new[]
        {
            new RpgStore.QuestJsonRow("quest.explore", 3),
            new RpgStore.QuestJsonRow("quest.kill-boss", 0),
        });

        _store.WriteQuestVerdicts(delve.DelveId, new Dictionary<string, (bool Done, int Have)>(StringComparer.Ordinal)
        {
            ["quest.kill-boss"] = (true, 1),
        });

        var read = _store.ReadQuestOffer(delve.DelveId);
        Assert.Equal(2, read.Count); // no growth
        Assert.Equal("quest.explore", read[0].QuestId); // no reorder
        Assert.Null(read[0].Done); // untouched
        Assert.True(read[1].Done);
        Assert.Equal(1, read[1].Have);
    }

    [Fact]
    public void WriteQuestVerdicts_for_a_quest_id_not_in_the_offer_is_silently_ignored_never_appended()
    {
        var delve = CreateDelve();
        _store.WriteQuestOffer(delve.DelveId, new[] { new RpgStore.QuestJsonRow("quest.explore", 3) });

        _store.WriteQuestVerdicts(delve.DelveId, new Dictionary<string, (bool Done, int Have)>(StringComparer.Ordinal)
        {
            ["quest.never-offered"] = (true, 1),
        });

        var read = _store.ReadQuestOffer(delve.DelveId);
        Assert.Single(read); // still just the one originally offered quest
    }

    [Fact]
    public void An_empty_delve_reads_an_empty_quest_offer_never_null_or_a_throw()
    {
        var delve = CreateDelve();
        var read = _store.ReadQuestOffer(delve.DelveId);
        Assert.Empty(read);
    }

    [Fact]
    public void An_afflicted_member_never_wins_even_after_killing_the_boss()
    {
        var delve = CreateDelve();
        _store.MarkRoom(delve.DelveId, "r2c0", cleared: true);
        var creature = MintBoundCreature();
        var before = _store.GetContract(creature)!.Loyalty;
        var topStage = _tuning.AttritionNerve.StageThresholds[^1];
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, nerveStacks: topStage) });
        SetRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0", "r2c0" });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.True(_store.GetContract(creature)!.Loyalty < before); // afflicted -> not won -> ApplyLoss
    }

    void SetRoute(long delveId, long partyEntityId, IReadOnlyList<string> route) =>
        _store.WritePartyRoute(delveId, partyEntityId, route);

    // ---- cross-delve pool persistence ----

    [Fact]
    public void Hunger_persists_across_delves_other_pools_do_not()
    {
        var delve = CreateDelve();
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, hunger: 430) });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var persisted = _store.GetUniqueActorPersistedPools(creature);
        Assert.Equal(new[] { "hunger" }, persisted.Keys); // the real shipped attrition.persistAcrossDelves shape
        Assert.Equal(430, persisted["hunger"]);
    }

    [Fact]
    public void A_second_delve_closing_overwrites_the_first_delves_persisted_value()
    {
        var creature = MintBoundCreature();
        var delve1 = CreateDelve();
        _store.WritePartyMembers(delve1.DelveId, 0, new[] { Member(creature, hunger: 900) });
        _store.CloseDelve(delve1.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        Assert.Equal(900, _store.GetUniqueActorPersistedPools(creature)["hunger"]);

        var delve2 = CreateDelve();
        _store.WritePartyMembers(delve2.DelveId, 0, new[] { Member(creature, hunger: 200) });
        _store.CloseDelve(delve2.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        Assert.Equal(200, _store.GetUniqueActorPersistedPools(creature)["hunger"]);
    }

    // ---- the recovery counter: R6, virtual time, every OTHER delve too ----

    [Fact]
    public void Closing_an_unrelated_delve_decrements_every_other_recovering_actor_this_player_owns()
    {
        var woundDelve = CreateDelve(rungId: "hard");
        var creature = MintBoundCreature();
        _store.WritePartyMembers(woundDelve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.CloseDelve(woundDelve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        var left0 = _store.GetUniqueActorRecovery(creature)!.Value.RecoveryDelvesLeft;
        Assert.True(left0 >= 1);

        // A completely unrelated later delve, no shared members at all.
        var otherDelve = CreateDelve();
        _store.CloseDelve(otherDelve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var afterOne = _store.GetUniqueActorRecovery(creature);
        if (left0 - 1 <= 0)
            Assert.Null(afterOne); // flipped straight to Roster in the same write
        else
            Assert.Equal(left0 - 1, afterOne!.Value.RecoveryDelvesLeft);
    }

    [Fact]
    public void The_recovery_counter_reaching_zero_flips_the_creature_back_to_Roster_in_the_same_write()
    {
        var delve = CreateDelve(rungId: "hard");
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        var left = _store.GetUniqueActorRecovery(creature)!.Value.RecoveryDelvesLeft;

        for (var i = 0; i < left; i++)
        {
            var closer = CreateDelve();
            _store.CloseDelve(closer.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        }

        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(creature)!.Phase);
        Assert.Null(_store.GetUniqueActorRecovery(creature));
    }

    // ---- the recovery ritual ----

    [Fact]
    public void The_recovery_ritual_prices_at_the_wounding_rungs_souls_and_writes_Roster()
    {
        var delve = CreateDelve(rungId: "hard");
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(creature)!.Phase);

        var before = _store.GetSoulBalance(1).Balance;
        var (ok, reason, actor) = _store.TryPerformRecoveryRitual(1, creature, "ritual-corr-1", _tuning);

        Assert.True(ok, reason);
        Assert.Equal(UniqueActorPhases.Roster, actor!.Phase);
        Assert.Null(_store.GetUniqueActorRecovery(creature));
        Assert.True(_store.GetSoulBalance(1).Balance < before);
    }

    [Fact]
    public void The_recovery_ritual_price_is_never_a_literal_it_scales_with_the_wounding_rung()
    {
        // "the ritual price is never a literal" (D2.23's own verify line) -- proven by varying the
        // ONE real, tunable input this test can directly control (the wounding rung) and observing
        // the SoulSinkPolicy.Price(risk.recoveryRitualSouls.{rung}, theta_run, tuning) call actually
        // charge two different amounts, against the real shipped tuning (medium: 800, hard: 1600).
        var mediumDelve = CreateDelve(rungId: "medium");
        var mediumCreature = MintBoundCreature();
        _store.WritePartyMembers(mediumDelve.DelveId, 0, new[] { Member(mediumCreature, downedOnce: true) });
        _store.CloseDelve(mediumDelve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        var balanceBeforeMedium = _store.GetSoulBalance(1).Balance;
        Assert.True(_store.TryPerformRecoveryRitual(1, mediumCreature, "ritual-corr-medium", _tuning).Ok);
        var mediumPrice = balanceBeforeMedium - _store.GetSoulBalance(1).Balance;

        var hardDelve = CreateDelve(rungId: "hard");
        var hardCreature = MintBoundCreature();
        _store.WritePartyMembers(hardDelve.DelveId, 0, new[] { Member(hardCreature, downedOnce: true) });
        _store.CloseDelve(hardDelve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        var balanceBeforeHard = _store.GetSoulBalance(1).Balance;
        Assert.True(_store.TryPerformRecoveryRitual(1, hardCreature, "ritual-corr-hard", _tuning).Ok);
        var hardPrice = balanceBeforeHard - _store.GetSoulBalance(1).Balance;

        Assert.True(mediumPrice > 0);
        Assert.True(hardPrice > mediumPrice); // 1600 base > 800 base, at the same theta_run (0)
    }

    [Fact]
    public void The_recovery_ritual_refuses_a_row_that_is_not_Recovering()
    {
        var creature = MintBoundCreature(); // ActiveBound, never wounded
        var (ok, reason, _) = _store.TryPerformRecoveryRitual(1, creature, "ritual-corr-2", _tuning);
        Assert.False(ok);
        Assert.StartsWith("phase.", reason);
    }

    [Fact]
    public void The_recovery_ritual_is_correlation_idempotent_a_replay_never_charges_twice()
    {
        var delve = CreateDelve(rungId: "hard");
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var first = _store.TryPerformRecoveryRitual(1, creature, "ritual-corr-3", _tuning);
        Assert.True(first.Ok, first.Reason);
        var balanceAfterFirst = _store.GetSoulBalance(1).Balance;

        var second = _store.TryPerformRecoveryRitual(1, creature, "ritual-corr-3", _tuning);
        Assert.True(second.Ok);
        Assert.Equal("replay", second.Reason);
        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(1).Balance); // never charged twice
    }

    [Fact]
    public void The_recovery_ritual_refuses_insufficient_souls()
    {
        var poor = _store.CreatePlayer("poor").Id; // a fresh player, no soul bank seeded
        var delve = CreateDelve(rungId: "hard", playerId: poor);
        var (specimen, _) = _store.MintCreature(poor, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId, Side = Species.Side, GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(), Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(), ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] }, Origin = "summon"
        });
        var creature = specimen.Actor.InstanceId;
        Assert.True(_store.BindContract(poor, creature).Ok);
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var (ok, reason, _) = _store.TryPerformRecoveryRitual(poor, creature, "ritual-corr-poor", _tuning);
        Assert.False(ok);
        Assert.Equal("souls.insufficient", reason);
    }

    // ---- route facts: real room data, not a fixture guess ----

    [Fact]
    public void A_route_that_clears_the_boss_room_registers_bossKilled_even_without_half_the_route()
    {
        var delve = CreateDelve();
        _store.MarkRoom(delve.DelveId, "r2c0", cleared: true); // only the boss room
        var creature = MintBoundCreature();
        var before = _store.GetContract(creature)!.Loyalty;
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature) });
        // A three-room route, only one cleared -- fails "half the route" but bossKilled alone still wins.
        SetRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0", "r2c0" });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.True(_store.GetContract(creature)!.Loyalty > before);
    }

    [Fact]
    public void An_uncleared_route_with_no_boss_kill_never_wins()
    {
        var delve = CreateDelve(); // no rooms marked cleared at all
        var creature = MintBoundCreature();
        var before = _store.GetContract(creature)!.Loyalty;
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature) });
        SetRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0", "r2c0" });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.True(_store.GetContract(creature)!.Loyalty < before);
    }

    // ===========================================================================================
    // D3.16 (spec-dungeon-loot.md §7) -- souls_unbanked/theta_run store surface, CloseDelve's loot-earn hook.
    // ===========================================================================================

    // ---- AccrueUnbanked ----

    [Fact]
    public void AccrueUnbanked_adds_to_souls_unbanked_across_multiple_calls()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 40, "r0c0");
        var updated = _store.AccrueUnbanked(delve.DelveId, 60, "r1c0");
        Assert.Equal(100, updated!.SoulsUnbanked);
    }

    [Fact]
    public void AccrueUnbanked_throws_on_overflow_and_writes_nothing()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, long.MaxValue - 10, "r0c0");

        Assert.Throws<OverflowException>(() => _store.AccrueUnbanked(delve.DelveId, 20, "r1c0"));

        Assert.Equal(long.MaxValue - 10, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
    }

    [Fact]
    public void AccrueUnbanked_rejects_a_negative_delta()
    {
        var delve = CreateDelve();
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.AccrueUnbanked(delve.DelveId, -1, "r0c0"));
    }

    [Fact]
    public void AccrueUnbanked_rejects_an_empty_roomKey()
    {
        var delve = CreateDelve();
        Assert.Throws<ArgumentException>(() => _store.AccrueUnbanked(delve.DelveId, 10, " "));
    }

    [Fact]
    public void AccrueUnbanked_returns_null_for_an_unknown_delve()
    {
        Assert.Null(_store.AccrueUnbanked(999_999, 10, "r0c0"));
    }

    // ---- SpendUnbanked ----

    [Fact]
    public void SpendUnbanked_deducts_and_returns_the_new_balance()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 100, "r0c0");

        var (ok, reason, left) = _store.SpendUnbanked(delve.DelveId, 40, "wild:0:0");

        Assert.True(ok);
        Assert.Equal("", reason);
        Assert.Equal(60, left);
        Assert.Equal(60, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
    }

    [Fact]
    public void SpendUnbanked_refuses_on_shortfall_and_changes_nothing()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 50, "r0c0");

        var (ok, reason, left) = _store.SpendUnbanked(delve.DelveId, 100, "wild:0:0");

        Assert.False(ok);
        Assert.Equal("delve.souls-insufficient", reason);
        Assert.Equal(50, left);
        Assert.Equal(50, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
    }

    [Fact]
    public void SpendUnbanked_rejects_a_non_positive_price()
    {
        var delve = CreateDelve();
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SpendUnbanked(delve.DelveId, 0, "wild:0:0"));
    }

    // ---- RecordClear ----

    [Fact]
    public void RecordClear_keeps_the_max_never_lowers_the_watermark()
    {
        var delve = CreateDelve();
        Assert.Equal(0, delve.ThetaRun);

        _store.RecordClear(delve.DelveId, 0, 0, 30);
        var afterLower = _store.RecordClear(delve.DelveId, 1, 0, 10);
        Assert.Equal(30, afterLower!.ThetaRun);

        var afterHigher = _store.RecordClear(delve.DelveId, 2, 0, 50);
        Assert.Equal(50, afterHigher!.ThetaRun);
    }

    // ---- CloseDelve's own loot-earn hook ----

    [Fact]
    public void CloseDelve_Extracted_pays_kills_and_victory_then_zeroes_the_unbanked_pot()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 500, "r0c0");
        _store.RecordClear(delve.DelveId, 2, 0, 30);
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var tuning = FusionRpg.Core.Power.PowerTuningHub.Tuning;
        var expected = FusionRpg.Core.Delve.Loot.DelveSoulLedger.AtExtraction(500, 30, won: true, tuning);
        Assert.True(expected.Kills > 0);
        Assert.True(expected.Victory > 0); // both must be non-zero for this test's own assertions to mean anything

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var reloaded = _store.LoadDelve(delve.DelveId)!;
        Assert.Equal(0, reloaded.SoulsUnbanked);
        var balanceAfter = _store.GetSoulBalance(1).Balance;
        Assert.Equal(balanceBefore + expected.Kills + expected.Victory, balanceAfter);

        var ledger = _store.ListSoulLedger(1, limit: 10).Items;
        Assert.Contains(ledger, e => e.Reason == SoulEarnPolicy.Reasons.Kill && e.Delta == expected.Kills && e.RefId == delve.DelveId.ToString());
        Assert.Contains(ledger, e => e.Reason == SoulEarnPolicy.Reasons.Victory && e.Delta == expected.Victory && e.RefId == delve.DelveId.ToString());
    }

    [Fact]
    public void CloseDelve_Wiped_forfeits_the_unbanked_pot_with_no_award()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 500, "r0c0");
        var balanceBefore = _store.GetSoulBalance(1).Balance;

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.Equal(0, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void CloseDelve_without_tuning_leaves_souls_unbanked_untouched()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 500, "r0c0");

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false); // no tuning -- pre-D2.23 shape

        Assert.Equal(500, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
    }

    [Fact]
    public void CloseDelve_replayed_close_does_not_double_pay()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 500, "r0c0");
        _store.RecordClear(delve.DelveId, 2, 0, 30);

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);
        var balanceAfterFirst = _store.GetSoulBalance(1).Balance;
        var ledgerCountAfterFirst = _store.ListSoulLedger(1, limit: 100).Items.Count;

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(1).Balance);
        Assert.Equal(ledgerCountAfterFirst, _store.ListSoulLedger(1, limit: 100).Items.Count);
    }

    // ---- ordering + atomicity across CloseDelve's own hooks (spec's own "one stated order") ----

    [Fact]
    public void CloseDelve_runs_attrition_settlement_and_loot_earn_together_in_one_call()
    {
        var delve = CreateDelve(rungId: "hard"); // "hard" sits below domain.permadeathFromRung
        var creature = MintBoundCreature();
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.AccrueUnbanked(delve.DelveId, 500, "r0c0");
        _store.RecordClear(delve.DelveId, 2, 0, 30);

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        // Attrition settlement's own effect landed...
        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(creature)!.Phase);
        // ...and loot earn's own effect landed too, from the SAME single CloseDelve call.
        Assert.Equal(0, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Contains(_store.ListSoulLedger(1, limit: 10).Items, e => e.Reason == SoulEarnPolicy.Reasons.Kill);
    }

    [Fact]
    public void CloseDelve_rolls_back_attrition_settlement_when_loot_earn_overflows()
    {
        var creature = MintBoundCreature();
        var delve = CreateDelve(rungId: "hard");
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.AccrueUnbanked(delve.DelveId, 100, "r0c0"); // Kills=100

        // Push player 1's balance to within 10 of the int64 ceiling -- headroom becomes 10, less than
        // the 100-soul Kills earn CloseDelve is about to try to credit, so GuardSoulAwardOrThrow throws
        // partway through the SAME transaction attrition settlement's own writes already landed in.
        var currentBalance = _store.GetSoulBalance(1).Balance;
        _store.AwardSouls(1, RpgStore.MaxSoulAwardFrom(currentBalance) - 10, "top-up", "top-up-key");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning));

        // Every hook's writes in this one transaction rolled back together -- not just loot earn's own.
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(creature)!.Phase);
        Assert.Null(_store.GetUniqueActorRecovery(creature));
        var reloaded = _store.LoadDelve(delve.DelveId)!;
        Assert.Equal(DelveStates.Active, reloaded.State);
        Assert.Equal(100, reloaded.SoulsUnbanked);
    }
}
