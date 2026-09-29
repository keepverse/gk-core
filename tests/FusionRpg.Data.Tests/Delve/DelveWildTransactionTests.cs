using FusionRpg.Contracts;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>D4.8 (spec-wild-room.md §4, §6) — the two store transactions this task closes:
/// <see cref="RpgStore.TalkJoin"/> (server-resolved candidate, party, sink, offer floor, debit,
/// and mint in one transaction) and <see cref="RpgStore.PullAtAltar"/> (price + spend + roll +
/// pity + pending-haul-append, one transaction), plus <c>CloseDelve</c>'s new 4th hook,
/// <c>ApplyHaulMintUnlocked</c>, that turns a pending haul row into a real mint at <c>Extracted</c>
/// and drops it at <c>Wiped</c>. Reads the real, shipped summoning and dungeon tuning rather than
/// fixture copies.</summary>
public class DelveWildTransactionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public DelveWildTransactionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);
        DungeonTuningHub.Configure(_tuning);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
        return dir.FullName;
    }

    // ---- fixtures (mirrors DelveAttritionSettlementTests.cs's own shape) ----

    const ulong Seed = 7UL;

    static WorldState BuildGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = Seed, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "wild", Climate = null, OwnerFactionId = "dave" },
            new WorldSector { SectorId = "r1c0", TypeId = "cache", Climate = null },
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" },
        },
        Entities = new[]
        {
            new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms(string firstKind = "wild", string? resolvedKind = null)
    {
        var archetype = firstKind == "wild" ? "room.wild-none-001" : "room.fight-none-001";
        return new[]
        {
            new DelveRoomRow("r0c0", 0, 0, firstKind, archetype, true, false, null, null, resolvedKind, null, "[]", 0),
            new DelveRoomRow("r1c0", 1, 0, "cache", "room.cache-none-001", false, false, null, null, null, null, "[]", 0),
        };
    }

    /// <summary>One fresh delve for player 1 — a unique world id per call so parallel tests in this
    /// class never collide on `rpg_delves.world_id`'s own UNIQUE constraint.</summary>
    DelveRow CreateDelve(long playerId = 1, string firstKind = "wild", string? resolvedKind = null)
    {
        var worldId = $"delve-wild-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            playerId, "domain.fire-shallow-001", "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", Seed, BuildGraph(worldId), BuildRooms(firstKind, resolvedKind), _rooms, _doors);
        Assert.True(ok);
        return delve!;
    }

    static readonly CreatureSpeciesDef WildSpecies = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    static long JoinPrice(DelveRow delve)
    {
        var dungeon = DungeonTuningHub.Tuning;
        var banner = SummonBannerCatalog.TryGet(dungeon.AltarBannerId)
            ?? SummonBannerCatalog.TryGet(SummonBannerCatalog.StandardRift)!;
        return FusionRpg.Core.Delve.Loot.DelvePrices.OfferFloor(
            banner.CostPerPull,
            delve.ThetaRun,
            dungeon.WildOfferSoulsMilliOfPullPrice,
            FusionRpg.Core.Power.PowerTuningHub.Tuning);
    }

    // ==========================================================================================
    // TalkJoin -- the D4.8 §4 server-resolved debit+mint transaction
    // ==========================================================================================

    [Fact]
    public void TalkJoin_debits_the_server_floor_and_mints_the_server_species_with_Origin_delve()
    {
        var delve = CreateDelve();
        var room = _store.LoadDelveRooms(delve.DelveId).Single(r => r.SectorId == "r0c0");
        var expected = FusionRpg.Core.Delve.Loot.DelveWildJoinResolver.Resolve(
            delve.Seed, room.RowIndex, room.ColIndex, room.ArchetypeId, delve.ThetaRun,
            DungeonTuningHub.Tuning, FusionRpg.Core.Power.PowerTuningHub.Tuning);
        Assert.True(expected.Ok, expected.Reason);
        var price = JoinPrice(delve);
        _store.AccrueUnbanked(delve.DelveId, checked(price + 200), "seed");

        var (ok, reason, specimen, soulsLeft) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: false);

        Assert.True(ok, reason);
        Assert.Equal(200, soulsLeft);
        Assert.Equal(200, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal("delve", specimen!.Profile.Origin);
        Assert.Equal(expected.Resolution!.SpeciesId, specimen.Profile.SpeciesId);
        // spec §4: "the free auto-bind" -- MintCreatureUnlocked's own AutoBindNewSpecimenUnlocked already ran.
        var contracts = _store.ListContracts(1);
        Assert.Contains(contracts, c => c.InstanceId == specimen.Actor.InstanceId && c.Bound);
    }

    [Fact]
    public void TalkJoin_refuses_insufficient_server_floor_and_mints_nothing()
    {
        var delve = CreateDelve();
        var price = JoinPrice(delve);
        Assert.True(price > 1);
        _store.AccrueUnbanked(delve.DelveId, price - 1, "seed");
        var before = _store.ListCreatureRoster(1).Items.Count;

        var (ok, reason, specimen, soulsLeft) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: false);

        Assert.False(ok);
        Assert.Equal("delve.souls-insufficient", reason);
        Assert.Null(specimen);
        Assert.Equal(price - 1, soulsLeft);
        Assert.Equal(price - 1, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal(before, _store.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public void TalkJoin_pays_discovery_souls_once_never_twice_for_the_server_species()
    {
        var delve = CreateDelve();
        var price = JoinPrice(delve);
        _store.AccrueUnbanked(delve.DelveId, checked((price * 2) + 100), "seed");
        var balance0 = _store.GetSoulBalance(1).Balance;

        var (ok1, reason1, specimen1, _) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: false);
        Assert.True(ok1, reason1);
        var species = CreatureSpeciesCatalog.Get(specimen1!.Profile.SpeciesId);
        var expectedDiscovery = SoulEarnPolicy.DiscoveryDelta(species.BaseRarity);
        Assert.Equal(balance0 + expectedDiscovery, _store.GetSoulBalance(1).Balance);

        var balance1 = _store.GetSoulBalance(1).Balance;
        var (ok2, reason2, specimen2, _) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: false);
        Assert.True(ok2, reason2);
        Assert.NotEqual(specimen1.Actor.InstanceId, specimen2!.Actor.InstanceId); // a second, distinct creature
        Assert.Equal(balance1, _store.GetSoulBalance(1).Balance); // no SECOND discovery award
    }

    [Fact]
    public void TalkJoin_refuses_wrong_room_kind_without_side_effects()
    {
        var delve = CreateDelve(firstKind: "fight");
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var rosterBefore = _store.ListCreatureRoster(1).Items.Count;

        var (ok, reason, specimen, soulsLeft) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: false);

        Assert.False(ok);
        Assert.Equal("wild.room-kind", reason);
        Assert.Null(specimen);
        Assert.Equal(10_000, soulsLeft);
        Assert.Equal(10_000, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.Equal(rosterBefore, _store.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public void TalkJoin_refuses_wrong_party_without_side_effects()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var rosterBefore = _store.ListCreatureRoster(1).Items.Count;

        var (ok, reason, specimen, soulsLeft) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 1, sectorId: "r0c0", cage: false);

        Assert.False(ok);
        Assert.Equal("wild.party-not-steered", reason);
        Assert.Null(specimen);
        Assert.Equal(10_000, soulsLeft);
        Assert.Equal(10_000, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.Equal(rosterBefore, _store.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public void TalkJoin_cage_requires_the_persisted_cage_marker()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");

        var (ok, reason, specimen, soulsLeft) = _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "r0c0", cage: true);

        Assert.False(ok);
        Assert.Equal("wild.not-cage", reason);
        Assert.Null(specimen);
        Assert.Equal(10_000, soulsLeft);
        Assert.Equal(10_000, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Empty(_store.ListCreatureRoster(1).Items);
    }

    [Fact]
    public void TalkJoin_throws_on_blank_sector_id()
    {
        var delve = CreateDelve();
        Assert.Throws<ArgumentException>(() => _store.TalkJoin(
            delve.DelveId, playerId: 1, partyEntityId: 0, sectorId: "  ", cage: false));
    }

    // ==========================================================================================
    // PullAtAltar -- the D4.8 §6 price+spend+roll+pity+haul transaction
    // ==========================================================================================

    [Fact]
    public void PullAtAltar_prices_via_PullPrice_spends_and_appends_a_pending_haul_row_without_minting()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 1_000, "seed");
        var rosterBefore = _store.ListCreatureRoster(1).Items.Count;

        var expectedPrice = FusionRpg.Core.Delve.Loot.DelvePrices.PullPrice(100, 70, FusionRpg.Core.Power.PowerTuningHub.Tuning);

        var (ok, reason, result, soulsLeft) = _store.PullAtAltar(
            delve.DelveId, partyEntityId: 0, row: 0, col: 0, thetaRoom: 70, "standard-rift", focusElement: null);

        Assert.True(ok, reason);
        Assert.NotNull(result);
        Assert.Equal(1_000 - expectedPrice, soulsLeft);
        var reloaded = _store.LoadDelve(delve.DelveId)!;
        Assert.Equal(1_000 - expectedPrice, reloaded.SoulsUnbanked);
        var party = reloaded.Parties.Single(p => p.EntityId == 0);
        var entry = Assert.Single(party.Haul);
        Assert.Equal("pull", entry.Kind);
        Assert.Equal(result!.SpeciesId, entry.SpeciesId);
        Assert.Equal(0, entry.Row);
        Assert.Equal(0, entry.Col);
        Assert.Equal(1, entry.N);
        // "no UniqueActor... until CloseDelve(Extracted)"
        Assert.Equal(rosterBefore, _store.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public void PullAtAltar_refuses_an_unknown_banner_before_any_soul_moves()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 1_000, "seed");

        var (ok, reason, result, soulsLeft) = _store.PullAtAltar(
            delve.DelveId, 0, 0, 0, 70, "banner.unknown", null);

        Assert.False(ok);
        Assert.Equal(FusionRpg.Core.Delve.Wild.AltarRefusal.BannerUnknown, reason);
        Assert.Null(result);
        Assert.Equal(0, soulsLeft); // bailed before reading the delve at all -- see the method's own body
        Assert.Equal(1_000, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
    }

    [Fact]
    public void PullAtAltar_refuses_insufficient_souls_leaving_pity_and_haul_untouched()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10, "seed"); // the real price (100) is far above this
        var pityBefore = _store.GetSummonPity(1);

        var (ok, reason, result, soulsLeft) = _store.PullAtAltar(
            delve.DelveId, 0, 0, 0, 70, "standard-rift", null);

        Assert.False(ok);
        Assert.Equal("delve.souls-insufficient", reason);
        Assert.Null(result);
        Assert.Equal(10, soulsLeft);
        Assert.Equal(10, _store.LoadDelve(delve.DelveId)!.SoulsUnbanked);
        Assert.Equal(pityBefore, _store.GetSummonPity(1));
        Assert.Empty(_store.LoadDelve(delve.DelveId)!.Parties); // no party row created either
    }

    [Fact]
    public void PullAtAltar_derives_n_by_counting_this_partys_own_existing_rows_at_the_same_r_c()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");

        Assert.True(_store.PullAtAltar(delve.DelveId, 0, 2, 2, 70, "standard-rift", null).Ok);
        Assert.True(_store.PullAtAltar(delve.DelveId, 0, 2, 2, 70, "standard-rift", null).Ok);
        Assert.True(_store.PullAtAltar(delve.DelveId, 0, 5, 5, 70, "standard-rift", null).Ok); // a DIFFERENT altar

        var haul = _store.LoadDelve(delve.DelveId)!.Parties.Single(p => p.EntityId == 0).Haul;
        Assert.Equal(3, haul.Count);
        var atTwoTwo = haul.Where(h => h.Row == 2 && h.Col == 2).OrderBy(h => h.N).ToList();
        Assert.Equal(new[] { 1, 2 }, atTwoTwo.Select(h => h.N));
        var atFiveFive = Assert.Single(haul.Where(h => h.Row == 5 && h.Col == 5));
        Assert.Equal(1, atFiveFive.N); // restarts at a different altar
    }

    [Fact]
    public void PullAtAltar_advances_pity_identically_to_calling_SummonRoller_directly_for_the_same_pity_and_rng()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        var pityBefore = _store.GetSummonPity(1);

        var (ok, reason, result, _) = _store.PullAtAltar(delve.DelveId, 0, 3, 4, 70, "standard-rift", null);
        Assert.True(ok, reason);
        var pityAfter = _store.GetSummonPity(1);

        var banner = FusionRpg.Core.Creatures.SummonBannerCatalog.TryGet("standard-rift")!;
        var rng = FusionRpg.Core.Battle.SeededRng.DeriveStream(Seed, "dungeon:altar:3:4:1");
        var (independentResults, independentPity) = SummonRoller.Roll(banner, null, 1, pityBefore, rng);

        Assert.Equal(independentPity, pityAfter);
        Assert.Equal(independentResults[0].SpeciesId, result!.SpeciesId);
        Assert.Equal(independentResults[0].Rarity, result.Rarity);
    }

    // ==========================================================================================
    // CloseDelve's 4th hook: ApplyHaulMintUnlocked (D4.8 §6, "no UniqueActor... until Extracted")
    // ==========================================================================================

    [Fact]
    public void CloseDelve_Extracted_mints_every_pending_haul_row_with_Origin_delve_and_clears_the_list()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        var (pullOk, pullReason, result, _) = _store.PullAtAltar(delve.DelveId, 0, 0, 0, 70, "standard-rift", null);
        Assert.True(pullOk, pullReason);
        var rosterBefore = _store.ListCreatureRoster(1).Items.Count;
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        // The SAME CloseDelve(tuning) call also fires the pre-existing D3.16 loot-earn hook against
        // this delve's own leftover souls_unbanked -- computed independently here so the assertion
        // below isolates the haul-mint's OWN discovery-souls contribution rather than assuming this
        // new hook is the balance's only mover (it is not; the two hooks share the same gate by design).
        var beforeClose = _store.LoadDelve(delve.DelveId)!;
        var lootEarn = FusionRpg.Core.Delve.Loot.DelveSoulLedger.AtExtraction(
            beforeClose.SoulsUnbanked, beforeClose.ThetaRun, won: true, FusionRpg.Core.Power.PowerTuningHub.Tuning);

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.True(closed);
        var roster = _store.ListCreatureRoster(1).Items;
        Assert.Equal(rosterBefore + 1, roster.Count);
        Assert.Contains(roster, r => r.Profile.SpeciesId == result!.SpeciesId && r.Profile.Origin == "delve");
        Assert.Empty(_store.LoadDelve(delve.DelveId)!.Parties.Single(p => p.EntityId == 0).Haul);
        // discovery souls fire on the haul mint exactly as a normal summon's would (RpgStore.Summons.cs:118-125).
        var species = CreatureSpeciesCatalog.Get(result!.SpeciesId);
        var expectedDiscovery = SoulEarnPolicy.DiscoveryDelta(species.BaseRarity);
        Assert.Equal(balanceBefore + lootEarn.Kills + lootEarn.Victory + expectedDiscovery, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void CloseDelve_Wiped_drops_the_haul_without_minting_but_the_spend_and_pity_already_stand()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        var (pullOk, pullReason, _, soulsAfterPull) = _store.PullAtAltar(delve.DelveId, 0, 0, 0, 70, "standard-rift", null);
        Assert.True(pullOk, pullReason);
        var pityAfterPull = _store.GetSummonPity(1);
        var rosterBefore = _store.ListCreatureRoster(1).Items.Count;

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.True(closed);
        Assert.Equal(rosterBefore, _store.ListCreatureRoster(1).Items.Count); // nothing minted
        Assert.Empty(_store.LoadDelve(delve.DelveId)!.Parties.Single(p => p.EntityId == 0).Haul); // dropped
        Assert.Equal(pityAfterPull, _store.GetSummonPity(1)); // "the pity advance... stand"
        // "the spend... stand[s]" -- the pull's own debit (already lower than the seeded 10,000) is
        // never refunded by a wipe; souls_unbanked itself is separately zeroed by the pre-existing
        // ApplyLootEarnUnlocked hook (D3.16), not by this one.
        Assert.True(soulsAfterPull < 10_000);
    }

    [Fact]
    public void CloseDelve_a_replayed_close_mints_nothing_the_second_time()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        Assert.True(_store.PullAtAltar(delve.DelveId, 0, 0, 0, 70, "standard-rift", null).Ok);

        Assert.True(_store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning));
        var rosterAfterFirst = _store.ListCreatureRoster(1).Items.Count;

        Assert.True(_store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning));
        Assert.Equal(rosterAfterFirst, _store.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public void CloseDelve_without_tuning_leaves_the_haul_untouched_the_same_byte_identical_contract_the_other_hooks_have()
    {
        var delve = CreateDelve();
        _store.AccrueUnbanked(delve.DelveId, 10_000, "seed");
        Assert.True(_store.PullAtAltar(delve.DelveId, 0, 0, 0, 70, "standard-rift", null).Ok);

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false); // tuning: null

        Assert.True(closed);
        Assert.Single(_store.LoadDelve(delve.DelveId)!.Parties.Single(p => p.EntityId == 0).Haul); // untouched
    }

    // ==========================================================================================
    // AppendPartyHaul -- the standalone wrapper, and the "first room is a wild room" edge case
    // ==========================================================================================

    [Fact]
    public void AppendPartyHaul_creates_a_party_row_on_first_use_the_same_first_room_first_row_upsert_every_other_writer_has()
    {
        var delve = CreateDelve();
        Assert.Empty(delve.Parties); // a brand-new delve: nobody has fought or pulled yet

        _store.AppendPartyHaul(delve.DelveId, 9,
            new DelveHaulEntry("pull", WildSpecies.SpeciesId, "chaff", "normal", new[] { "x" }, 1, 1, 1));

        var party = Assert.Single(_store.LoadDelve(delve.DelveId)!.Parties);
        Assert.Equal(9, party.EntityId);
        Assert.Single(party.Haul);
    }

    [Fact]
    public void AppendPartyHaul_throws_on_a_null_entry()
    {
        var delve = CreateDelve();
        Assert.Throws<ArgumentNullException>(() => _store.AppendPartyHaul(delve.DelveId, 0, null!));
    }
}
