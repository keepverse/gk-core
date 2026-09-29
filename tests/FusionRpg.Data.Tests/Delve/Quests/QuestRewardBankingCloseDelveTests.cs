using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve.Quests;

/// <summary>
/// D4.12 (spec-delve-quests.md §4) — `CloseDelve`'s own quest-reward-banking wiring
/// (`RpgStore.ApplyQuestRewardBankingUnlocked`), the ONE remaining piece this task's own todo entry
/// named: `QuestReward.Request`, `DelveLoot.RollQuestReward` and `mintAt` (`RpgStore.MintGrant`) were
/// all already real and tested — nothing called them from `CloseDelve` until this task.
///
/// <para><b>Real, hand-built fixture data, not real imported content</b> — matching the task's own
/// required discipline: the six real shipped domains all correctly refuse `DomainPreflight` today
/// (D4.17 rows 5/6/8/10, already tracked, not this task's job), so every test here builds its OWN
/// domain via `DomainImportTests.cs`'s own established "fully-passing `DomainPreflightInputs` fixture"
/// shape (rows 4-8 trivially satisfied by empty-refusal delegates) rather than real content. The reward
/// ladder is likewise hand-corrected here (`SeedOneRarityRung`), NOT the real shipped
/// `dungeon.v3.json`'s own `quests.rewardBand.*` — D4.13's own already-documented finding is that those
/// values are difficulty-rung ids, not item-rarity-rung ids, so using them here would exercise a
/// separate, already-tracked content bug instead of this task's own wiring.</para>
/// </summary>
public class QuestRewardBankingCloseDelveTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public QuestRewardBankingCloseDelveTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

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

    static readonly PowerTuning MintTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly QuestRow Quest = new("quest.test-001", "kill-boss", null, null, "modest", "delve", null);

    // ---- delve fixture (mirrors DelveAttritionSettlementTests.cs/DelvePackSettlementTests.cs) --------

    static WorldState BuildGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = 42UL, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[] { new WorldSector { SectorId = "r0c0", TypeId = "fight", Climate = null, OwnerFactionId = "dave" } },
        Lanes = Array.Empty<WorldLane>(),
        Entities = new[] { new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" } },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
    };

    /// <summary>One fresh delve for player 1, no party members — attrition/pack/haul-mint's own already-
    /// tested hooks correctly no-op on an empty `Parties` list (confirmed by reading each), so this
    /// fixture stays minimal and focused on the ONE hook under test.</summary>
    DelveRow CreateDelve(string domainId, long playerId = 1)
    {
        var worldId = $"delve-qr-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, reason, delve) = _store.CreateDelve(
            playerId, domainId, "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), BuildRooms(), _rooms, _doors);
        Assert.True(ok, reason);
        // theta_run defaults to 0 -- watermark it via RecordClear (D3.16's real writer) to a realistic
        // depth, matching this whole session's own established Theta=20 convention. RecordClear hands
        // back the freshly-read row itself, so no separate re-read is needed.
        var watermarked = _store.RecordClear(delve!.DelveId, 0, 0, thetaRoom: 20);
        Assert.NotNull(watermarked);
        return watermarked!;
    }

    // ---- domain fixture (mirrors DomainImportTests.cs's own established shape exactly) ----------------

    static DomainRow Domain(string id) =>
        new(id, "Test Domain", "A test flavor.", "theme.overgrown", "fire", "shallow", "many",
            "layout.standard", "species.warden", null, "Lair", null);

    static DomainPreflightInputs PassingDomainInputs(string? cacheTableId) => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: new[] { "layout.standard" },
        KnownSpeciesIds: new[] { "species.warden" },
        ThreatBandOrdinalFor: _ => 5,
        BossFloorRungOrdinal: 3,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: cacheTableId is null ? Array.Empty<string>() : new[] { cacheTableId },
        BoundLootKinds: cacheTableId is null ? Array.Empty<string>() : new[] { "cache" },
        LootBindingFor: _ => cacheTableId is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = cacheTableId },
        OfferedRungCountFor: _ => 3);

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyQuests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, string> EmptyProvenance = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Imports a real domain through the REAL production write path (`ImportDungeonDomains`),
    /// so `ReadLootBinding` -- the exact method the new hook calls -- reads a genuinely-written row,
    /// not a shortcut. `cacheTableId: null` imports a domain with no `cache` binding at all, for the
    /// "no matching lootBinding" test.</summary>
    string ImportDomainWithCacheBinding(string domainId, string? cacheTableId)
    {
        var loot = cacheTableId is null
            ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
              { [domainId] = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = cacheTableId } };

        var outcome = _store.ImportDungeonDomains(
            new[] { Domain(domainId) }, PassingDomainInputs(cacheTableId), EmptyRooms, EmptyQuests, loot, EmptyProvenance, 32);
        Assert.True(outcome.IsOk, string.Join(";", outcome.Refusals));
        return domainId;
    }

    // ---- rarity + unique-reward fixture -----------------------------------------------------------

    void SeedOneRarityRung(string rarityId, int ordinal, int dropWeight)
    {
        Assert.True(_store.UpsertRarity(new RarityRow(rarityId, ordinal, 0, 1, 1, 1)).Ok);
        _store.SetRarityBudget(rarityId, "drop_weight_default", dropWeight);
    }

    /// <summary>A real, mintable Unique container — needs BOTH `Rarity` AND `Frame`/`BaseTypeId`
    /// (confirmed by reading `LootPipeline.MintUnique` directly: it refuses `drop.unique-base-type-
    /// unresolved`/`drop.unknown-unique` without the pair, the SAME "unique-frame-storage gap" the
    /// D4.12 todo entry names as closed) — `UniqueRarityFor` alone is not enough.</summary>
    void SeedRelicContainer(string containerId, string rarityId)
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.quest-relic-vitality", "", 1),
            KindId = "stat.modify", FamilyId = "atom.quest-relic-vitality", Variant = "", Tier = 1,
            Name = "atom.quest-relic-vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item, Rarity = rarityId,
            Frame = "relic", BaseTypeId = "item.qr-relic-base",
            Atoms = new[] { new ContainerAtomRow(0, AtomRow.DeriveId("atom.quest-relic-vitality", "", 1)) },
        }).IsOk);
    }

    static DropTableRow OneUniqueTable(string tableId, string refId) => new(
        tableId, SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null, Enabled: true, Revision: 1,
        Groups: new[]
        {
            new DropTableGroupRow("main", Seq: 0, Rolls: 1, Entries: new[]
            {
                new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Unique, RefId: refId, Weight: 100),
            }),
        });

    static DropVolumeTuning RealDropVolumeTuning() => DropVolumeTuning.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "item-drop-volume.v1.json")));

    RpgStore.QuestRewardBankingInputs Inputs(string tableId, string refId) => new(
        ResolveQuest: id => id == Quest.QuestId ? Quest : null,
        View: _store.BuildLiveLootContentView() with
        {
            Tables = new Dictionary<string, DropTableRow>(StringComparer.Ordinal) { [tableId] = OneUniqueTable(tableId, refId) },
        },
        Drops: RealDropVolumeTuning(),
        RoleFamilyCells: Array.Empty<RoleFamilyCell>(), // Unique-only fixture: never touches this
        MintTuning: MintTuning);

    DungeonTuning TuningWithCorrectRewardBand(string floorRung, string ceilRung) => _tuning with
    {
        QuestsRewardBand = new Dictionary<string, RewardWindow>(StringComparer.Ordinal)
        {
            [Quest.RewardBand] = new RewardWindow(floorRung, ceilRung),
        },
    };

    // ==================================================================================================
    // Fixture wiring self-check -- confirms CreateDelve's own helper (which does an extra RecordClear
    // write outside the constructor's own INSERT) hands back a row with the watermark applied, before
    // trusting it in every other test below.
    // ==================================================================================================

    [Fact]
    public void Fixture_CreateDelve_returns_a_row_with_theta_run_watermarked()
    {
        var domainId = ImportDomainWithCacheBinding("domain.qr-fixture-001", "table.qr-fixture");
        var delve = CreateDelve(domainId);
        Assert.Equal(20, delve.ThetaRun);
        Assert.Equal(domainId, delve.DomainId);
    }

    // ==================================================================================================
    // The full happy-path setup: a real domain (imported through ImportDungeonDomains), a real "cache"
    // lootBinding, a real one-entry Unique drop table, a real mintable container, and one quest offered
    // and (optionally) marked Done -- everything CloseDelve's own new hook needs to bank a real reward.
    // ==================================================================================================

    (DelveRow Delve, RpgStore.QuestRewardBankingInputs Inputs, DungeonTuning Tuning, string TableId, string RefId)
        SetupDoneQuest(string suffix, bool withCacheBinding = true, bool markDone = true)
    {
        var domainId = $"domain.qr-{suffix}";
        var tableId = $"table.qr-{suffix}";
        var refId = $"item.qr-relic-{suffix}";

        SeedOneRarityRung("chaff", 10, 100_000);
        SeedOneRarityRung("sprout", 20, 40_000);
        SeedRelicContainer(refId, "chaff");
        ImportDomainWithCacheBinding(domainId, withCacheBinding ? tableId : null);
        var delve = CreateDelve(domainId);

        _store.WriteQuestOffer(delve.DelveId, new[] { new RpgStore.QuestJsonRow(Quest.QuestId, Need: 1) });
        if (markDone)
            _store.WriteQuestVerdicts(delve.DelveId, new Dictionary<string, (bool Done, int Have)>(StringComparer.Ordinal)
            {
                [Quest.QuestId] = (true, 1),
            });

        var inputs = Inputs(tableId, refId);
        var tuning = TuningWithCorrectRewardBand("chaff", "sprout");
        return (delve, inputs, tuning, tableId, refId);
    }

    // ==================================================================================================
    // The happy path -- the task's own required "real instance, correctly attributed" proof.
    // ==================================================================================================

    [Fact]
    public void A_done_quest_banks_a_real_instance_correctly_attributed_to_player_and_delve()
    {
        var (delve, inputs, tuning, _, refId) = SetupDoneQuest("happy");

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, inputs);

        Assert.True(closed);
        var playerId = delve.PlayerId.ToString();
        var items = _store.ListItemsByPlayer(playerId);
        var item = Assert.Single(items);
        Assert.Equal(playerId, item.PlayerId);
        Assert.Equal("quest-reward", item.OriginKind);
        Assert.Equal($"{delve.DelveId}:quest:{Quest.QuestId}", item.OriginRef);

        var instance = _store.GetInstance(item.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(refId, instance!.ContainerId);

        var log = _store.ListDropLog(playerId, 10);
        Assert.Contains(log, r => r.SourceKind == "dungeon-quest" && r.SourceId == $"{delve.DelveId}:quest:{Quest.QuestId}");
    }

    [Fact]
    public void Two_Done_quests_in_the_same_close_both_bank_with_distinct_attribution()
    {
        var quest2 = new QuestRow("quest.test-002", "kill-boss", null, null, "modest", "delve", null);
        const string domainId = "domain.qr-two";
        const string tableId = "table.qr-two";
        const string refId = "item.qr-relic-two";

        SeedOneRarityRung("chaff", 10, 100_000);
        SeedOneRarityRung("sprout", 20, 40_000);
        SeedRelicContainer(refId, "chaff");
        ImportDomainWithCacheBinding(domainId, tableId);
        var delve = CreateDelve(domainId);

        _store.WriteQuestOffer(delve.DelveId, new[]
        {
            new RpgStore.QuestJsonRow(Quest.QuestId, Need: 1),
            new RpgStore.QuestJsonRow(quest2.QuestId, Need: 1),
        });
        _store.WriteQuestVerdicts(delve.DelveId, new Dictionary<string, (bool Done, int Have)>(StringComparer.Ordinal)
        {
            [Quest.QuestId] = (true, 1),
            [quest2.QuestId] = (true, 1),
        });

        var inputs = new RpgStore.QuestRewardBankingInputs(
            ResolveQuest: id => id == Quest.QuestId ? Quest : id == quest2.QuestId ? quest2 : null,
            View: _store.BuildLiveLootContentView() with
            {
                Tables = new Dictionary<string, DropTableRow>(StringComparer.Ordinal) { [tableId] = OneUniqueTable(tableId, refId) },
            },
            Drops: RealDropVolumeTuning(),
            RoleFamilyCells: Array.Empty<RoleFamilyCell>(),
            MintTuning: MintTuning);
        var tuning = TuningWithCorrectRewardBand("chaff", "sprout");

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, inputs);

        Assert.True(closed);
        var items = _store.ListItemsByPlayer(delve.PlayerId.ToString());
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.OriginRef == $"{delve.DelveId}:quest:{Quest.QuestId}");
        Assert.Contains(items, i => i.OriginRef == $"{delve.DelveId}:quest:{quest2.QuestId}");
        Assert.NotEqual(items[0].InstanceId, items[1].InstanceId);

        var log = _store.ListDropLog(delve.PlayerId.ToString(), 10);
        Assert.Equal(2, log.Count(r => r.SourceKind == "dungeon-quest"));
    }

    // ==================================================================================================
    // The honest "nothing happens" cases -- required by the task, and each a real, distinct code path.
    // ==================================================================================================

    [Fact]
    public void No_Done_quests_means_CloseDelve_banks_nothing()
    {
        var (delve, inputs, tuning, _, _) = SetupDoneQuest("nodone", markDone: false);

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, inputs);

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
        Assert.Empty(_store.ListDropLog(delve.PlayerId.ToString(), 10));
    }

    /// <summary>The task's own named case: "a quest with no matching lootBinding kind correctly
    /// refuses/skips, naming why" — the domain here is imported with NO `cache` binding at all (a real,
    /// legitimate `DomainPreflight`-passing domain, just one that authored no loot table), matching the
    /// production content gap this whole session already tracks (D4.17 rows 5/6/8/10).</summary>
    [Fact]
    public void A_domain_with_no_cache_lootBinding_is_skipped_without_throwing_or_banking()
    {
        var (delve, inputs, tuning, _, _) = SetupDoneQuest("nobind", withCacheBinding: false);

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, inputs);

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    [Fact]
    public void An_unresolvable_quest_id_is_skipped_not_thrown()
    {
        var (delve, inputs, tuning, _, _) = SetupDoneQuest("unresolved");
        var brokenInputs = inputs with { ResolveQuest = _ => null };

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, brokenInputs);

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    [Fact]
    public void A_reward_band_absent_from_tuning_is_skipped_not_thrown()
    {
        var (delve, inputs, tuning, _, _) = SetupDoneQuest("noband");
        var brokenTuning = tuning with { QuestsRewardBand = new Dictionary<string, RewardWindow>(StringComparer.Ordinal) };

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, brokenTuning, inputs);

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    // ==================================================================================================
    // Wipe forfeits the reward -- mirrors ApplyLootEarnUnlocked's own "a wipe forfeits the whole pot".
    // ==================================================================================================

    [Fact]
    public void A_wiped_delve_forfeits_a_Done_quests_reward()
    {
        var (delve, inputs, tuning, _, _) = SetupDoneQuest("wiped");

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, tuning, inputs);

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    // ==================================================================================================
    // Replay safety -- this task's own SAFETY-RELEVANT claim: a replayed CloseDelve must never mint a
    // second, orphaned instance for a quest reward already banked.
    // ==================================================================================================

    [Fact]
    public void A_replayed_CloseDelve_call_does_not_mint_a_second_instance()
    {
        var (delve, inputs, tuning, tableId, refId) = SetupDoneQuest("replay");

        var first = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, inputs);
        Assert.True(first);
        var itemsAfterFirst = _store.ListItemsByPlayer(delve.PlayerId.ToString());
        Assert.Single(itemsAfterFirst);

        // A FRESH BuildLiveLootContentView() -- proves replay-safety rides the LIVE store's own
        // RecordedManifestFor (rebuilt from what the first call just persisted), not a stale in-memory
        // view carried over from the first call.
        var secondInputs = Inputs(tableId, refId);
        var second = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning, secondInputs);

        Assert.True(second);
        var itemsAfterSecond = _store.ListItemsByPlayer(delve.PlayerId.ToString());
        Assert.Single(itemsAfterSecond); // still exactly one -- the replay minted nothing new
        Assert.Equal(itemsAfterFirst[0].InstanceId, itemsAfterSecond[0].InstanceId);

        var log = _store.ListDropLog(delve.PlayerId.ToString(), 10);
        Assert.Single(log, r => r.SourceId == $"{delve.DelveId}:quest:{Quest.QuestId}");
    }

    // ==================================================================================================
    // Without questRewards supplied, CloseDelve(tuning) stays exactly as it behaved before this task --
    // the second, independent gate the new hook's own doc comment claims.
    // ==================================================================================================

    [Fact]
    public void Without_questRewards_supplied_a_Done_quest_banks_nothing_even_with_tuning()
    {
        var (delve, _, tuning, _, _) = SetupDoneQuest("noinputs");

        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, tuning); // no questRewards

        Assert.True(closed);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }
}
